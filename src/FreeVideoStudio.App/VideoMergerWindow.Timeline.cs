
using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// SCRAPER_02 — ONE LONG TIMELINE ACROSS EVERY CLIP.
///
/// Once the queue is analysed, the merger's timeline IS the merged video: the slider, the clock,
/// the ±10 s buttons, the arrow keys and the scrub surface all speak merged seconds. The timeline
/// zooms out as clips are added, because it always spans the whole merge. Each clip boundary gets a
/// divider and a clip number. A clip whose thumbnail intro is cut is marked with ✂.
///
/// Playback crosses clips by itself: MERGEPREVIEW_EDL_01 (Video-Merger-Migration.md P4.2) loads the
/// whole merge into mpv as ONE inline EDL of every clip's KEPT window (VideoMergerWindow.EdlPreview.cs),
/// so mpv's time-pos IS the merged clock and a removed intro is never shown, exactly as the export
/// never contains it. (Before P4.2 the preview switched files at clip ends.)
///
/// Until the analysis lands (normally well under a second after a clip is added), the preview
/// keeps its previous per-clip behaviour, so the window is never blocked waiting for ffprobe.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class VideoMergerWindow
{
    private int _playClip = -1;
    private string? _playPath;
    private int _clipLoadGraceTicks;
    private double _lastMergedPos;
    private bool _mergedEndReached;
    private bool _syncingSelection;
    private bool _draggingThumbMarker;
    private string? _mergedDrawKey;

    private const int SeekGraceTicks = 3;

    private bool MergedMode => _videoHost?.IpcClient != null && TimelineMatchesQueue();

    private void InvalidateMergedTimelineDrawing() => _mergedDrawKey = null;

    /// <summary>Timeline length in the preview's clock: merged seconds, or the loaded file's length before analysis.</summary>
    private double PreviewDurationSec() => MergedMode ? _timeline.TotalSec : _videoHost?.IpcClient?.Duration ?? 0.0;

    /// <summary>Playhead in the preview's clock.</summary>
    private double PreviewPositionSec() => MergedMode && _edlUrl != null ? _lastMergedPos : _videoHost?.IpcClient?.CurrentTime ?? 0.0;

    /// <summary>Seek in the preview's clock.</summary>
    private Task SeekPreview(double t)
    {
        if (!MergedMode) return SeekInternal(t);
        SeekMerged(t);
        return Task.CompletedTask;
    }

    /// <summary>MERGEPREVIEW_EDL_01 — one EDL, one clock: a seek anywhere in the merge is one mpv seek.</summary>
    private void SeekMerged(double mergedSec, bool? play = null)
    {
        var ipc = _videoHost?.IpcClient;
        if (ipc == null || _timeline.Clips.Count == 0) return;
        if (_memePreview?.IsActive == true) return;
        double t = Math.Clamp(mergedSec, 0, _timeline.TotalSec);
        _lastMergedPos = t;
        _mergedEndReached = false;
        RearmPreviewEffects(t);
        if (!EnsureEdlLoaded(t, play))
        {
            _ = SeekInternal(t);
            if (play is bool p) _ = ipc.SetPropertyAsync("pause", p ? "no" : "yes");
            _edlLoadTarget = t;
            _clipLoadGraceTicks = SeekGraceTicks;
        }
        FollowPlayhead(t);
    }

    /// <summary>AUTOPREVIEW_01 in merged mode: highlighting a clip plays it from its middle.</summary>
    private bool StartMergedPreview(string path)
    {
        if (_syncingSelection) return true;
        if (_selectWithoutPreview) return MergedMode;
        if (!MergedMode)
        {
            ForgetLoadedEdl();
            return false;
        }
        int idx = VideoListCtl?.SelectedIndex ?? -1;
        if (idx < 0 || idx >= _timeline.Clips.Count) return false;
        var clip = _timeline.Clips[idx];
        LoadMergedClip(idx, (clip.ContentStartSec + clip.ContentEndSec) / 2.0, play: true);
        return true;
    }

    /// <summary>Plays clip <paramref name="idx"/> from source second <paramref name="src"/> (a seek in the one EDL).</summary>
    private void LoadMergedClip(int idx, double src, bool play)
    {
        var ipc = _videoHost?.IpcClient;
        if (ipc == null || idx < 0 || idx >= _timeline.Clips.Count) return;
        _pendingMiddleSeekPath = null;
        _autoAdvanceArmed = false;
        _ = ipc.SetPropertyAsync("mute", _previewMuted ? "yes" : "no");
        SeekMerged(_timeline.ToMerged(idx, src), play);
    }

    /// <summary>
    /// MERGERUX_01 — when the playhead CROSSES into another clip, that clip becomes the highlighted row. P10 (R-b):
    /// only on a crossing (never every tick — that fought every click, press and drag in the list)
    /// and never while a press or drag on the list or a block is in progress.
    /// </summary>
    private void FollowPlayhead(double mergedSec)
    {
        var (idx, _) = _timeline.Locate(mergedSec);
        if (idx < 0 || idx == _playClip) return;
        _playClip = idx;
        _playPath = _timeline.Clips[idx].Path;
        if (!QueueGestureActive) SyncListSelection(idx);
    }

    private void SyncListSelection(int idx)
    {
        var list = VideoListCtl;
        if (list == null || list.SelectedIndex == idx || (list.SelectedItems?.Count ?? 0) > 1) return;
        _syncingSelection = true;
        try { list.SelectedIndex = idx; list.ScrollIntoView(idx); }
        finally { _syncingSelection = false; }
    }

    /// <summary>Merged-mode half of the 100 ms playback tick. False = not in merged mode, run the legacy tick.</summary>
    private bool TickMergedPlayback()
    {
        if (!MergedMode)
        {
            if (_mergedDrawKey != null)
            {
                MarkersCanvasCtl?.Children.Clear();
                _mergedDrawKey = null;
                _isTimelineDrawn = false;
            }
            return false;
        }
        var ipc = _videoHost!.IpcClient!;

        EnsurePreviewPlan();

        if (TickMergerMemes()) return true;

        EnsureEdlLoaded(null, null);

        if (_mergedEndReached && !ipc.IsPaused && _timeline.Clips.Count > 0)
            SeekMerged(0, play: true);

        double total = _timeline.TotalSec;
        double merged = EdlPositionSec(ipc);

        bool holding = TickPreviewEffects(ipc, ref merged);

        if (!holding && _clipLoadGraceTicks == 0 && !_mergedEndReached
            && (ipc.IsEof || merged >= total - 0.03) && (!ipc.IsPaused || ipc.IsEof))
        {
            _mergedEndReached = true;
            _ = ipc.SetPropertyAsync("pause", "yes");
            merged = total;
        }

        _lastMergedPos = merged;
        FollowPlayhead(merged);

        bool playing = holding || (!ipc.IsPaused && !_mergedEndReached);
        var playIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon");
        var pauseIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PauseIcon");
        if (playIcon != null && pauseIcon != null)
        {
            playIcon.IsVisible = !playing;
            pauseIcon.IsVisible = playing;
        }

        string Fmt(double s) => total >= 3600 ? TimeSpan.FromSeconds(s).ToString("hh\\:mm\\:ss") : TimeSpan.FromSeconds(s).ToString("mm\\:ss");
        var elapsed = this.FindControl<TextBlock>("TimeElapsed");
        if (elapsed != null) elapsed.Text = Fmt(merged);
        var remaining = this.FindControl<TextBlock>("TimeRemaining");
        if (remaining != null) remaining.Text = "-" + Fmt(Math.Max(0, total - merged));

        var slider = this.FindControl<Slider>("TimelineSlider");
        if (slider != null && total > 0)
        {
            _isTimerUpdatingSlider = true;
            slider.Value = Math.Clamp(merged / total * 100.0, 0.0, 100.0);
            _isTimerUpdatingSlider = false;
        }

        var markers = MarkersCanvasCtl;
        if (markers != null && !_draggingThumbMarker && !_draggingClipChip && !_chipPressed)
            DrawMergedTimeline(markers, this.FindControl<Canvas>("TimelineScaleCanvas"));

        UpdateMergerThumbnailButton();
        UpdateMergerMusicPreview(PreviewOutputSec(merged), playing);
        return true;
    }


    private void DrawMergedTimeline(Canvas markers, Canvas? scale)
    {
        double w = markers.Bounds.Width;
        double h = Math.Max(24, markers.Bounds.Height);
        double total = _timeline.TotalSec;
        if (w <= 0 || total <= 0) return;

        double? thumb = ThumbnailMergedSec();
        var music = _musicResult != null && !_musicIsStale && !string.IsNullOrEmpty(_musicResult.MusicFilePath) ? _musicResult : null;
        string key = $"{w:F0}|{h:F0}|{_timeline.Signature}|{thumb:F3}|{music?.TimelineStartSeconds:F3}|{music?.TimelineEndSeconds:F3}|{SelectionKey()}";
        if (key == _mergedDrawKey) return;
        _mergedDrawKey = key;
        _isTimelineDrawn = true;
        ScheduleLaneRefresh();

        DrawTimelineScale(scale, w, total);
        markers.Children.Clear();
        DrawTimelineGrid(markers, w, h, total);

        IBrush divider = Infrastructure.ThemeResources.Brush(markers, "AppInfoBrush", Brushes.DeepSkyBlue);
        IBrush labelBg = new SolidColorBrush(Color.FromArgb(170, 15, 23, 42));

        for (int i = 0; i < _timeline.Clips.Count; i++)
        {
            var c = _timeline.Clips[i];
            double x0 = c.MergedStartSec / total * w;
            double x1 = c.MergedEndSec / total * w;

            if (i > 0)
            {
                var line = new Avalonia.Controls.Shapes.Rectangle { Width = 2, Height = h, Fill = divider, IsHitTestVisible = false };
                Canvas.SetLeft(line, x0 - 1);
                Canvas.SetTop(line, 0);
                markers.Children.Add(line);
            }

            double room = x1 - x0;
            if (room < 16) continue;
            string name = System.IO.Path.GetFileNameWithoutExtension(c.Path);
            string text = (c.RemovedIntroSec > 0 ? "✂ " : "") + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                          + (room > 110 ? " · " + name : "");
            var label = new Border
            {
                Width = Math.Max(14, room - 6),
                IsHitTestVisible = false,
                Child = new Border
                {
                    Background = labelBg,
                    CornerRadius = new Avalonia.CornerRadius(3),
                    Padding = new Avalonia.Thickness(3, 0),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = text,
                        FontSize = Infrastructure.ThemeManager.ScaledFontSize(9),
                        Foreground = Brushes.White,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        TextAlignment = TextAlignment.Center,
                    },
                },
            };
            Canvas.SetLeft(label, x0 + 3);
            Canvas.SetTop(label, 0);
            markers.Children.Add(label);
        }

        DrawTimelineSelection(markers, w, h, total);

        if (music != null)
        {
            double ms = Math.Clamp(music.TimelineStartSeconds / total, 0, 1) * w;
            double me = Math.Clamp(music.TimelineEndSeconds / total, 0, 1) * w;
            var strip = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = Math.Max(2, me - ms),
                Height = 3,
                Fill = Infrastructure.ThemeResources.Brush(markers, "AppAccentBrush", Brushes.MediumPurple),
                Opacity = 0.85,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(strip, ms);
            Canvas.SetTop(strip, h - 3);
            markers.Children.Add(strip);
        }

        if (thumb is double t)
        {
            var marker = MainWindow.CreateTimelineCameraIcon(false, 0, out _, out _);
            ToolTip.SetTip(marker, "This exact frame will be the cover picture (thumbnail) of the merged video when you share it. Drag to change it.");
            Canvas.SetTop(marker, -79);
            Canvas.SetLeft(marker, MainWindow.ClampTimelineCameraLeft(t / total * w, w));
            AttachMergerThumbnailMarker(marker, markers);
            markers.Children.Add(marker);
        }
    }

    /// <summary>Time ticks over the whole merged length (was per clip). Moved here from the code-behind.</summary>
    /// <summary>The Main App's tick spacing (MainWindow.Canvas.cs): 5 s, 10 s past 1 min, 30 s past 5 min, 60 s past 30 min, 5 min past 1 h.</summary>
    private static double TimelineTickInterval(double duration)
    {
        if (duration > 3600) return 300;
        if (duration > 1800) return 60;
        if (duration > 300) return 30;
        if (duration > 60) return 10;
        return 5;
    }

    /// <summary>
    /// MERGERUX_01 — P10 (item 6, D25) — the x-axis the Main App and the Add Music timeline have: a faint vertical grid
    /// line at every tick across the scrub row (drawn first, under everything else on it).
    /// </summary>
    private static void DrawTimelineGrid(Canvas markers, double w, double h, double duration)
    {
        if (duration <= 0 || w <= 0) return;
        double interval = TimelineTickInterval(duration);
        var brush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
        for (double t = interval; t < duration - 0.001; t += interval)
        {
            var line = new Avalonia.Controls.Shapes.Rectangle { Fill = brush, Width = 1, Height = h, IsHitTestVisible = false };
            Canvas.SetLeft(line, t / duration * w);
            Canvas.SetTop(line, 0);
            markers.Children.Add(line);
        }
    }

    private void DrawTimelineScale(Canvas? scaleCanvas, double canvasWidth, double duration)
    {
        if (scaleCanvas == null || canvasWidth <= 0 || duration <= 0) return;
        scaleCanvas.Children.Clear();
        double tickInterval = TimelineTickInterval(duration);
        var labelBrush = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255));
        string Label(double t) => TimeSpan.FromSeconds(t).ToString(t >= 3600 ? "h\\:mm\\:ss" : "m\\:ss");
        void Add(string text, double left)
        {
            var tb = new TextBlock { Text = text, Foreground = labelBrush, FontSize = Infrastructure.ThemeManager.ScaledFontSize(9), IsHitTestVisible = false };
            Canvas.SetLeft(tb, Math.Max(0, Math.Min(Math.Max(0, canvasWidth - 36), left)));
            Canvas.SetTop(tb, 0);
            scaleCanvas.Children.Add(tb);
        }

        Add(Label(0), 0);
        for (double t = tickInterval; t < duration - 0.001; t += tickInterval)
        {
            double tx = (t / duration) * canvasWidth;
            if (tx < 30 || canvasWidth - tx < 40) continue;
            Add(Label(t), tx + 2);
        }
        Add(Label(duration), canvasWidth - 36);
    }


    private void AttachMergerThumbnailMarker(Control marker, Canvas canvas)
    {
        marker.PointerEntered += (_, _) => MainWindow.SetTimelineCameraHover(marker, true);
        marker.PointerExited += (_, _) => { if (!_draggingThumbMarker) MainWindow.SetTimelineCameraHover(marker, false); };
        marker.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(marker).Properties.IsLeftButtonPressed) return;
            _draggingThumbMarker = true;
            MoveThumbMarker(e.GetPosition(canvas).X, canvas, marker);
            e.Pointer.Capture(marker);
            e.Handled = true;
        };
        marker.PointerMoved += (_, e) =>
        {
            if (!_draggingThumbMarker) return;
            if (!e.GetCurrentPoint(marker).Properties.IsLeftButtonPressed) { EndThumbMarkerDrag(marker); return; }
            MoveThumbMarker(e.GetPosition(canvas).X, canvas, marker);
            e.Handled = true;
        };
        marker.PointerReleased += (_, e) =>
        {
            if (!_draggingThumbMarker) return;
            MoveThumbMarker(e.GetPosition(canvas).X, canvas, marker);
            e.Pointer.Capture(null);
            EndThumbMarkerDrag(marker);
            e.Handled = true;
        };
        marker.PointerCaptureLost += (_, _) => { if (_draggingThumbMarker) EndThumbMarkerDrag(marker); };
    }

    private void MoveThumbMarker(double x, Canvas canvas, Control marker)
    {
        double w = canvas.Bounds.Width;
        double total = _timeline.TotalSec;
        if (w <= 0 || total <= 0) return;
        double cx = Math.Clamp(x, 0, w);
        double merged = cx / w * total;
        SetThumbnailAtMerged(merged, announce: false);
        Canvas.SetLeft(marker, MainWindow.ClampTimelineCameraLeft(cx, w));

        _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");
        SeekMerged(merged, play: false);
    }

    private void EndThumbMarkerDrag(Control marker)
    {
        _draggingThumbMarker = false;
        EndHistoryGesture();
        MainWindow.SetTimelineCameraHover(marker, false);
        InvalidateMergedTimelineDrawing();
        RuntimeLog.Info("MERGER", $"Custom thumbnail moved to {_thumbSourceSec:F3}s of {System.IO.Path.GetFileName(_thumbPath ?? "")}.");
    }

    /// <summary>THUMB_01 — the button always says what pressing it does right now.</summary>
    private void UpdateMergerThumbnailButton()
    {
        var btn = this.FindControl<Button>("SetMergerThumbnailButton");
        var txt = this.FindControl<TextBlock>("SetMergerThumbnailText");
        if (btn == null) return;
        btn.IsEnabled = MergedMode;

        string label, tip;
        bool destructive = false;
        if (!HasCustomThumbnail)
        {
            label = " SET THUMBNAIL ";
            tip = "Save the exact frame you are looking at right now as the cover picture of the merged video. It replaces clip 1's own cover intro.";
        }
        else if (IsPlayheadOnThumbnail())
        {
            label = " REMOVE THUMBNAIL ";
            tip = "You are parked on your cover frame. Press to clear it; clip 1's own cover intro is used again.";
            destructive = true;
        }
        else
        {
            label = " MOVE THUMBNAIL HERE ";
            tip = "Move the cover picture to the frame you are looking at now.";
        }

        if (txt != null && txt.Text != label) txt.Text = label;
        if (destructive)
        {
            btn.Classes.Remove("Primary");
            btn.Classes.Remove("Secondary");
            if (!btn.Classes.Contains("Danger")) btn.Classes.Add("Danger");
        }
        else
        {
            btn.Classes.Remove("Danger");
            if (!btn.Classes.Contains("Primary") && !btn.Classes.Contains("Secondary")) btn.Classes.Add("Primary");
        }
        ToolTip.SetTip(btn, tip);
    }
}
