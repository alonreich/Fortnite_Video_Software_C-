using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FortniteVideoSoftware.Core.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace FortniteVideoSoftware.App;

/// <summary>
/// Granular Speed Editor dialog window.
/// Mirrors the Python GranularSpeedEditor: lets the user mark time ranges and assign
/// a playback speed (including freeze-frame at 0x) to each segment.
/// </summary>
public partial class GranularSpeedEditorWindow : Window
{
    // ------------------------------------------------------------------ state
    private MpvVideoView? _videoHost;
    private bool _isSeeking = false;
    private double? _nextSeekTarget = null;
    private readonly string _videoPath;
    private readonly double _trimStartMs;
    private readonly double _trimEndMs;

    private readonly List<SpeedSegment> _segments = new();

    private int _pendingStartMs = -1;
    private int _pendingEndMs   = -1;
    private double _pendingSpeed = 1.1;
    private double _baseSpeed    = 1.1;

    private bool _isSafeToClose = false;

    // Issue #4: Selected segment for inline editing
    private int _selectedSegmentIndex = -1;
    private DispatcherTimer? _marchingAntsTimer;
    private double _marchingAntsOffset = 0;

    private DispatcherTimer? _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _isTimelineDrawn = false;
    private double _lastAppliedSpeed = 1.0;  // Tracks last speed sent to MPV to avoid redundant IPC

    // The result the caller reads after the dialog closes
    public bool Accepted { get; private set; }
    public IReadOnlyList<SpeedSegment> ResultSegments => _segments
        .Select(s => new SpeedSegment(s.StartMs + (int)_trimStartMs, s.EndMs + (int)_trimStartMs, s.Speed))
        .ToList()
        .AsReadOnly();
    public double ResultBaseSpeed => _baseSpeed;

    // ------------------------------------------------------------------ ctor
    /// <summary>
    /// Parameterless ctor required by Avalonia's XAML runtime loader.
    /// Do not call directly — use the overload that accepts a video path.
    /// </summary>
    public GranularSpeedEditorWindow() : this(string.Empty, 0, 0) { }

    /// <summary>
    /// Creates the Granular Speed Editor constrained to a trim region.
    /// The editor will only show/seek between trimStartMs and trimEndMs.
    /// Segments are stored in absolute video timestamps.
    /// </summary>
    public GranularSpeedEditorWindow(string videoPath, double trimStartMs = 0, double trimEndMs = 0, IReadOnlyList<SpeedSegment>? existingSegments = null, double baseSpeed = 1.1)
    {
        _videoPath = videoPath;
        _trimStartMs = trimStartMs;
        _trimEndMs = trimEndMs;
        _baseSpeed = baseSpeed;
        _pendingSpeed = baseSpeed;
        _lastAppliedSpeed = baseSpeed;

        InitializeComponent();
        var initialSpeedSlider = this.FindControl<Slider>("PendingSpeedSlider");
        SpeedPresetButtons.SetSliderValue(initialSpeedSlider, _pendingSpeed);
        var initialSpeedLabel = this.FindControl<TextBlock>("PendingSpeedLabel");
        if (initialSpeedLabel != null) initialSpeedLabel.Text = $"{_pendingSpeed:F2}x";
        
        if (existingSegments != null)
        {
            // Convert incoming segments from absolute → relative to trim region
            foreach (var seg in existingSegments)
            {
                int relStart = (int)(seg.StartMs - _trimStartMs);
                int relEnd = (int)(seg.EndMs - _trimStartMs);
                // Only include segments that fall within the trim region
                if (relEnd > 0 && relStart < (int)(_trimEndMs - _trimStartMs))
                {
                    relStart = Math.Max(0, relStart);
                    int maxEnd = _trimEndMs > 0 ? (int)(_trimEndMs - _trimStartMs) : int.MaxValue;
                    relEnd = Math.Min(relEnd, maxEnd);
                    _segments.Add(new SpeedSegment(relStart, relEnd, seg.Speed));
                }
            }
        }

        this.Loaded += (s, e) => InitializeMpv();
        WireUpControls();
        AttachTitleBarDrag();
        RefreshSegmentList();
        UpdateDeleteButtonVisibility();

        // Issue #4: Marching ants animation timer for selected segment
        _marchingAntsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _marchingAntsTimer.Tick += (_, _) => {
            _marchingAntsOffset = (_marchingAntsOffset + 1) % 8;
            RedrawTimeline();
        };
        _marchingAntsTimer.Start();

        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();

    }

    // ------------------------------------------------------------------ init
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void InitializeMpv()
    {
        _videoHost = this.FindControl<MpvVideoView>("GranularVideoHost");
        if (_videoHost != null)
        {
            RuntimeLog.Info("Granular", "Initializing MPV video host for Granular Speed Editor.");
            string mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "frontend", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath)) mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "backend", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath)) mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath))
            {
                RuntimeLog.Fail("Granular", "Could not locate mpv.exe for Granular Speed Editor. Using PATH fallback.");
                mpvPath = "mpv.exe";
            }
            else
            {
                RuntimeLog.Info("Granular", $"Using MPV at: {mpvPath}");
            }
            await _videoHost.StartMpvProcessAsync(mpvPath);

            if (_videoHost.IpcClient != null)
            {
                RuntimeLog.Info("Granular", "MPV IPC client connected. Attaching seek handler.");
                _videoHost.IpcClient.SeekCompleted += () => {
                    Avalonia.Threading.Dispatcher.UIThread.Post(async () => {
                        _isSeeking = false;
                        if (_nextSeekTarget.HasValue) {
                            double target = _nextSeekTarget.Value;
                            _nextSeekTarget = null;
                            await SeekInternal(target);
                        }
                    });
                };

                LoadVideo();
            }
            else
            {
                RuntimeLog.Fail("Granular", "MPV IPC client is null after starting MPV process.");
            }
        }
        else
        {
            RuntimeLog.Fail("Granular", "Could not find GranularVideoHost control in XAML.");
        }
    }

    private void UpdateTooltips()
    {
        var kb = FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.KeyBinds;
        var playBtn = this.FindControl<Button>("GranularPlayPause");
        if (playBtn != null) ToolTip.SetTip(playBtn, $"Play or pause the video ({kb.PlayPause})");
        
        var startBtn = this.FindControl<Button>("MarkStartBtn");
        if (startBtn != null) ToolTip.SetTip(startBtn, $"Mark the start of the segment ({kb.MarkStart})");
        
        var endBtn = this.FindControl<Button>("MarkEndBtn");
        if (endBtn != null) ToolTip.SetTip(endBtn, $"Mark the end of the segment ({kb.MarkEnd})");
    }

    private void GranularKeyDownHandler(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Avalonia.Controls.TextBox or Avalonia.Controls.NumericUpDown)
            return;

        var kb = FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.KeyBinds;

        // Issue #4: Delete key removes the selected segment
        if (e.Key == Avalonia.Input.Key.Delete || e.Key == Avalonia.Input.Key.Back)
        {
            if (_selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count)
            {
                _segments.RemoveAt(_selectedSegmentIndex);
                _selectedSegmentIndex = -1;
                RefreshSegmentList();
                RedrawTimeline();
                UpdateDeleteButtonVisibility();
                SetStatus("Selected segment deleted.");
                e.Handled = true;
                return;
            }
        }

        if (e.Key == kb.PlayPause)
        {
            var btn = this.FindControl<Button>("GranularPlayPause");
            btn?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        }
        else if (e.Key == kb.MarkStart)
        {
            var btn = this.FindControl<Button>("MarkStartBtn");
            btn?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        }
        else if (e.Key == kb.MarkEnd)
        {
            var btn = this.FindControl<Button>("MarkEndBtn");
            btn?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        }
        else if (e.Key == kb.SeekForward)
        {
            // Seek forward but clamp to trim region end
            double currentAbs = _videoHost?.IpcClient?.CurrentTime ?? 0;
            double trimEndSec = (_trimEndMs > 0) ? _trimEndMs / 1000.0 : double.MaxValue;
            double target = Math.Min(currentAbs + 5, trimEndSec);
            _ = SeekInternal(target - (_trimStartMs / 1000.0));
            e.Handled = true;
        }
        else if (e.Key == kb.SeekBackward)
        {
            // Seek backward but clamp to trim region start
            double currentAbs = _videoHost?.IpcClient?.CurrentTime ?? 0;
            double target = Math.Max(currentAbs - 5, _trimStartMs / 1000.0);
            _ = SeekInternal(target - (_trimStartMs / 1000.0));
            e.Handled = true;
        }
    }

    private async Task SeekInternal(double time) {
        if (_isSeeking) { _nextSeekTarget = time; return; }
        _isSeeking = true;
        // `time` is relative to the trim region; convert to absolute video position
        double absTime = (_trimStartMs / 1000.0) + time;
        double trimEndSec = (_trimEndMs > 0) ? _trimEndMs / 1000.0 : double.MaxValue;
        absTime = Math.Min(absTime, trimEndSec);
        absTime = Math.Max(absTime, _trimStartMs / 1000.0);
        if (_videoHost?.IpcClient != null) await _videoHost.IpcClient.SendCommandAsync("seek", absTime.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
    }

    private void LoadVideo()
    {
        if (string.IsNullOrWhiteSpace(_videoPath) || _videoHost?.IpcClient == null) return;

        _ = _videoHost.IpcClient.LoadFileAsync(_videoPath);
        _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");

        // Constrain playback to the trim region using MPV's A-B loop feature.
        // This ensures MPV itself prevents playback beyond the MARK END boundary
        // and loops back to MARK START — the user only ever sees their clip.
        double startSec = _trimStartMs / 1000.0;
        double endSec = (_trimEndMs > 0) ? _trimEndMs / 1000.0 : 0;

        // Set A-B loop points (MPV will loop playback within this region)
        _ = _videoHost.IpcClient.SetPropertyAsync("ab-loop-a", startSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (endSec > startSec)
        {
            _ = _videoHost.IpcClient.SetPropertyAsync("ab-loop-b", endSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Seek to the trim start position so the editor opens at MARK START
        _ = _videoHost.IpcClient.SendCommandAsync("seek", startSec.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
    }

    private void WireUpControls()
    {
        // ---- Timeline slider ----
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("GranularTimelineCanvas");
        if (canvas != null)
        {
            canvas.SizeChanged += (s, e) => RedrawTimeline();

            // Issue #4: Click-to-select on timeline segments
            canvas.IsHitTestVisible = true;
            canvas.PointerPressed += (s, e) =>
            {
                double dur = GetDuration();
                if (dur <= 0) return;
                double w = canvas.Bounds.Width;
                if (w <= 0) return;
                double clickX = e.GetPosition(canvas).X;
                double clickSec = (clickX / w) * dur;
                int clickMs = (int)(clickSec * 1000);

                // Find segment at click position
                int foundIdx = -1;
                for (int i = 0; i < _segments.Count; i++)
                {
                    if (clickMs >= _segments[i].StartMs && clickMs <= _segments[i].EndMs)
                    {
                        foundIdx = i;
                        break;
                    }
                }

                if (foundIdx >= 0)
                {
                    _selectedSegmentIndex = foundIdx;
                    var seg = _segments[foundIdx];
                    _pendingSpeed = seg.Speed;

                    // Sync slider to selected segment's speed
                    var speedSlider = this.FindControl<Slider>("PendingSpeedSlider");
                    var speedLbl = this.FindControl<TextBlock>("PendingSpeedLabel");
                    if (speedSlider != null && seg.Speed >= 0.01) speedSlider.Value = seg.Speed;
                    if (speedLbl != null) speedLbl.Text = seg.Speed < 0.01 ? "0.00x (FREEZE)" : $"{seg.Speed:F2}x";

                    UpdateDeleteButtonVisibility();
                    RedrawTimeline();
                    SetStatus($"Selected segment #{foundIdx + 1}. Change speed to edit it.");
                    e.Handled = true;
                }
                else
                {
                    // Click on empty area deselects
                    if (_selectedSegmentIndex >= 0)
                    {
                        _selectedSegmentIndex = -1;
                        UpdateDeleteButtonVisibility();
                        RedrawTimeline();
                    }
                }
            };
        }

        var timeline = this.FindControl<Slider>("GranularTimeline");
        if (timeline != null)
        {
            timeline.PropertyChanged += (_, e) =>
            {
                if (e.Property == Slider.ValueProperty && !_isSeeking && _videoHost?.IpcClient != null)
                {
                    double pct = timeline.Value / 1000.0;
                    double dur = GetDuration();
                    if (dur > 0)
                        _ = SeekInternal(pct * dur);
                }
            };
        }

        // ---- Play/Pause ----
        var playPause = this.FindControl<Button>("GranularPlayPause");
        playPause?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            RuntimeLog.Info("UI", "User toggled Play/Pause in Granular Speed Editor.");
            if (_videoHost?.IpcClient != null) _ = _videoHost.IpcClient.SetPropertyAsync("pause", _videoHost.IpcClient.IsPaused ? "no" : "yes");
        });


        // ---- Mark Start / End ----
        var markStart = this.FindControl<Button>("MarkStartBtn");
        markStart?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            RuntimeLog.Info("UI", "User clicked Mark Start in Granular Speed Editor.");

            int currentMs = (int)(GetCurrentTime() * 1000);

            // Prevent marking inside an existing segment (no overlaps allowed)
            int? overlapIdx = FindSegmentAtPosition(currentMs);
            if (overlapIdx.HasValue)
            {
                var overlapping = _segments[overlapIdx.Value];
                ShowFeedback($"⚠ Inside segment #{overlapIdx.Value + 1}! Delete it first.");
                SetStatus($"Cannot mark here — overlaps segment #{overlapIdx.Value + 1} [{FormatMs(overlapping.StartMs)} – {FormatMs(overlapping.EndMs)}]. Delete it first.");
                return;
            }

            // Issue #4: Starting new marking deselects any selected segment
            _selectedSegmentIndex = -1;
            UpdateDeleteButtonVisibility();

            _pendingStartMs = currentMs;
            ShowFeedback($"START: {FormatMs(_pendingStartMs)}");
            RedrawTimeline();
        });

        var markEndBtn = this.FindControl<Button>("MarkEndBtn");
        markEndBtn?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            RuntimeLog.Info("UI", "User clicked Mark End in Granular Speed Editor.");

            int currentMs = (int)(GetCurrentTime() * 1000);

            // Prevent marking END inside an existing segment (unless we're just setting end
            // with a valid start that doesn't overlap)
            if (_pendingStartMs < 0)
            {
                int? overlapIdx = FindSegmentAtPosition(currentMs);
                if (overlapIdx.HasValue)
                {
                    var overlapping = _segments[overlapIdx.Value];
                    ShowFeedback($"⚠ Inside segment #{overlapIdx.Value + 1}! Delete it first.");
                    SetStatus($"Cannot mark here — overlaps segment #{overlapIdx.Value + 1} [{FormatMs(overlapping.StartMs)} – {FormatMs(overlapping.EndMs)}]. Delete it first.");
                    return;
                }
            }

            _pendingEndMs = currentMs;
            
            // Issue #4: Starting new marking deselects any selected segment
            _selectedSegmentIndex = -1;

            // Logic for auto-calculating start:
            if (_pendingStartMs < 0)
            {
                if (_segments.Count == 0)
                {
                    _pendingStartMs = 0;
                }
                else
                {
                    _pendingStartMs = (int)_segments.Last().EndMs + 1000;
                    if (_pendingStartMs > _pendingEndMs) 
                    {
                        _pendingStartMs = (int)_segments.Last().EndMs;
                    }
                }
            }

            if (_videoHost?.IpcClient != null)
            {
                _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");
            }

            ShowFeedback($"SEGMENT ADDED: {FormatMs(_pendingEndMs)}");
            
            // Automatically add segment
            AddPendingSegment();

            // Issue #4: Auto-select the newly created segment (last one added)
            if (_segments.Count > 0)
            {
                _selectedSegmentIndex = _segments.Count - 1;
            }
            UpdateDeleteButtonVisibility();
            RedrawTimeline();
        });

        // ---- Pending speed slider ----
        var speedSlider = this.FindControl<Slider>("PendingSpeedSlider");
        if (speedSlider != null)
        {
            speedSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property == Slider.ValueProperty)
                {
                    _pendingSpeed = Math.Round(speedSlider.Value, 2);
                    var lbl = this.FindControl<TextBlock>("PendingSpeedLabel");
                    if (lbl != null) lbl.Text = $"{_pendingSpeed:F2}x";

                    // Issue #4: If a segment is selected, update its speed live
                    if (_selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count)
                    {
                        var seg = _segments[_selectedSegmentIndex];
                        _segments[_selectedSegmentIndex] = new SpeedSegment(seg.StartMs, seg.EndMs, _pendingSpeed);
                        RefreshSegmentList();
                        RedrawTimeline();
                    }
                }
            };
        }

        // Issue #7: Speed preset buttons
        WireUpSpeedPresets(speedSlider);

        // Freeze frame checkbox syncs slider to 0
        var freezeCheck = this.FindControl<Avalonia.Controls.Primitives.ToggleButton>("FreezeFrameCheck");
        if (freezeCheck != null && speedSlider != null)
        {
            freezeCheck.IsCheckedChanged += (_, _) =>
            {
                bool frozen = freezeCheck.IsChecked == true;
                speedSlider.IsEnabled = !frozen;
                if (frozen) { speedSlider.Value = 0.1; _pendingSpeed = 0.0; }
                var lbl = this.FindControl<TextBlock>("PendingSpeedLabel");
                if (lbl != null) lbl.Text = frozen ? "0.00x (FREEZE)" : $"{_pendingSpeed:F2}x";
            };
        }

        // ---- Delete single segment (Issue #6) ----
        var deleteSegBtn = this.FindControl<Button>("DeleteSegmentBtn");
        deleteSegBtn?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            if (_selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count)
            {
                _segments.RemoveAt(_selectedSegmentIndex);
                _selectedSegmentIndex = -1;
                RefreshSegmentList();
                RedrawTimeline();
                UpdateDeleteButtonVisibility();
                SetStatus("Selected segment deleted.");
            }
        });

        // ---- Clear all ----
        var clearBtn = this.FindControl<Button>("ClearAllSegmentsBtn");
        clearBtn?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            RuntimeLog.Info("UI", "User clicked Clear All in Granular Speed Editor.");
            _segments.Clear();
            _selectedSegmentIndex = -1;
            _pendingStartMs = -1;
            _pendingEndMs = -1;
            RefreshSegmentList();
            RedrawTimeline();
            UpdateDeleteButtonVisibility();
            SetStatus("All segments and pending selections cleared.");
        });

        // ---- Accept / Cancel ----
        var acceptBtn = this.FindControl<Button>("AcceptGranularBtn");
        if (acceptBtn != null) acceptBtn.Click += (s, e) => {
            RuntimeLog.Info("UI", "User clicked Accept in Granular Speed Editor.");
            Accepted = true;
            Close();
        };

        var cancel = this.FindControl<Button>("CancelGranularBtn");
        cancel?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            RuntimeLog.Info("UI", "User clicked Cancel in Granular Speed Editor.");
            Close();
        });

        UpdateTooltips();
        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, GranularKeyDownHandler, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void WireUpSpeedPresets(Slider? speedSlider)
    {
        SpeedPresetButtons.ConfigureBaseButton(
            this,
            _baseSpeed,
            $"Set speed to Main screen base speed {SpeedPresetButtons.FormatSpeed(_baseSpeed)}");

        SpeedPresetButtons.WirePresetButtons(this, _baseSpeed, s =>
        {
            var freezeCheck = this.FindControl<Avalonia.Controls.Primitives.ToggleButton>("FreezeFrameCheck");
            if (freezeCheck != null && freezeCheck.IsChecked == true)
            {
                freezeCheck.IsChecked = false;
            }

            SpeedPresetButtons.SetSliderValue(speedSlider, s);
            _pendingSpeed = s;
            var lbl = this.FindControl<TextBlock>("PendingSpeedLabel");
            if (lbl != null) lbl.Text = $"{s:F2}x";

            if (_selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count)
            {
                var seg = _segments[_selectedSegmentIndex];
                _segments[_selectedSegmentIndex] = new SpeedSegment(seg.StartMs, seg.EndMs, s);
                RefreshSegmentList();
                RedrawTimeline();
            }
        });
    }

    /// <summary>
    /// Issue #6: Update visibility of DELETE SEGMENT and CLEAR ALL buttons.
    /// DELETE SEGMENT: visible only when a segment is selected.
    /// CLEAR ALL: visible when any segment exists.
    /// </summary>
    private void UpdateDeleteButtonVisibility()
    {
        var deleteSegBtn = this.FindControl<Button>("DeleteSegmentBtn");
        var clearAllBtn = this.FindControl<Button>("ClearAllSegmentsBtn");

        if (deleteSegBtn != null)
            deleteSegBtn.IsVisible = _selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count;

        if (clearAllBtn != null)
            clearAllBtn.IsVisible = _segments.Count > 0;
    }

    // ------------------------------------------------------------------ segment management
    private void AddPendingSegment()
    {
        if (_pendingStartMs < 0 || _pendingEndMs < 0)
        {
            SetStatus("Mark a START and END time first.");
            return;
        }

        int start = Math.Min(_pendingStartMs, _pendingEndMs);
        int end   = Math.Max(_pendingStartMs, _pendingEndMs);

        if (end - start < 10) // 10 ms minimum per spec
        {
            SetStatus("Segment must be at least 10 ms long.");
            return;
        }

        // Collision check: no overlaps allowed (spec §3.1)
        foreach (var seg in _segments)
        {
            if (start < seg.EndMs && end > seg.StartMs)
            {
                SetStatus($"Overlap with existing segment [{FormatMs(seg.StartMs)} – {FormatMs(seg.EndMs)}]. Adjust times.");
                return;
            }
        }

        double speed = this.FindControl<Avalonia.Controls.Primitives.ToggleButton>("FreezeFrameCheck")?.IsChecked == true ? 0.0 : _pendingSpeed;
        _segments.Add(new SpeedSegment(start, end, speed));
        _segments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

        // Reset pending markers
        _pendingStartMs = -1;
        _pendingEndMs   = -1;
        RefreshSegmentList();
        SetStatus($"Segment added: {FormatMs(start)} – {FormatMs(end)} @ {speed:F2}x");
    }

    private void RefreshSegmentList()
    {
        var panel = this.FindControl<StackPanel>("SegmentsPanel");
        if (panel == null) return;
        panel.Children.Clear();

        var countLbl = this.FindControl<TextBlock>("SegmentCountLabel");
        if (countLbl != null)
            countLbl.Text = _segments.Count == 0 ? "No segments" : $"{_segments.Count} segment{(_segments.Count == 1 ? "" : "s")}";

        for (int i = 0; i < _segments.Count; i++)
        {
            int idx = i;
            var seg = _segments[i];
            bool isFreeze = seg.Speed < 0.01;
            bool isSelected = idx == _selectedSegmentIndex;

            // Row border — highlight if selected
            var border = new Border
            {
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(isSelected ? "#3d4f63" : "#1e293b")),
                BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(isSelected ? "#fde047" : "#334155")),
                BorderThickness = new Thickness(isSelected ? 2 : 1),
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(0, 2),
                Padding = new Thickness(8, 6),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            // Info text
            var info = new TextBlock
            {
                Text = $"#{idx + 1}  {FormatMs(seg.StartMs)} → {FormatMs(seg.EndMs)}\n" +
                       (isFreeze ? "❄ FREEZE FRAME" : $"{seg.Speed:F2}x speed"),
                Foreground = isFreeze
                    ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#60a5fa"))
                    : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e2e8f0")),
                FontSize = 10.5,
                FontFamily = new Avalonia.Media.FontFamily("Consolas"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };

            // Delete button
            var delBtn = new Button
            {
                Content = "✕",
                Width = 26,
                Height = 26,
                FontSize = 11,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7f1d1d")),
                Foreground = Avalonia.Media.Brushes.White,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(4, 0, 0, 0)
            };
            delBtn.Click += (_, e) =>
            {
                e.Handled = true;
                _segments.RemoveAt(idx);
                if (_selectedSegmentIndex == idx) _selectedSegmentIndex = -1;
                else if (_selectedSegmentIndex > idx) _selectedSegmentIndex--;
                RefreshSegmentList();
                RedrawTimeline();
                UpdateDeleteButtonVisibility();
                SetStatus("Segment removed.");
            };

            // Issue #4: Click border to select this segment
            border.PointerPressed += (_, _) =>
            {
                _selectedSegmentIndex = idx;
                _pendingSpeed = seg.Speed;

                var speedSlider = this.FindControl<Slider>("PendingSpeedSlider");
                var speedLbl = this.FindControl<TextBlock>("PendingSpeedLabel");
                if (speedSlider != null && seg.Speed >= 0.01) speedSlider.Value = seg.Speed;
                if (speedLbl != null) speedLbl.Text = seg.Speed < 0.01 ? "0.00x (FREEZE)" : $"{seg.Speed:F2}x";

                var freezeCheck = this.FindControl<Avalonia.Controls.Primitives.ToggleButton>("FreezeFrameCheck");
                if (freezeCheck != null) freezeCheck.IsChecked = seg.Speed < 0.01;

                RefreshSegmentList();
                UpdateDeleteButtonVisibility();
                RedrawTimeline();
                SetStatus($"Selected segment #{idx + 1}. Change speed or press DELETE to remove.");
            };

            Grid.SetColumn(info, 0);
            Grid.SetColumn(delBtn, 1);
            grid.Children.Add(info);
            grid.Children.Add(delBtn);
            border.Child = grid;
            panel.Children.Add(border);
        }
    }

    // ------------------------------------------------------------------ timeline overlay
    private void RedrawTimeline()
    {
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("GranularTimelineCanvas");
        if (canvas == null) return;

        Dispatcher.UIThread.Post(() =>
        {
            canvas.Children.Clear();
            double dur = GetDuration();
            double w = canvas.Bounds.Width;
            double h = Math.Max(canvas.Bounds.Height, 28);
            if (dur <= 0 || w <= 0) return;

            // ── DRAW ORDER: segments FIRST (bottom z-order), ticks LAST (top z-order) ──
            // This ensures the X-axis tick marks/labels are always visible on top.

            // --- Phase 1: Draw each segment as a colored bar (bottom layer) ---
            for (int i = 0; i < _segments.Count; i++)
            {
                var seg = _segments[i];
                double x1 = (seg.StartMs / 1000.0 / dur) * w;
                double x2 = (seg.EndMs   / 1000.0 / dur) * w;
                bool isSelected = i == _selectedSegmentIndex;

                // Background bar — color based on speed relative to base speed (NOT selection state)
                var rect = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width  = Math.Max(2, x2 - x1),
                    Height = h,
                    Fill   = new Avalonia.Media.SolidColorBrush(GetSegmentOverlayColor(seg))
                };
                Avalonia.Controls.Canvas.SetLeft(rect, x1);
                Avalonia.Controls.Canvas.SetTop(rect, 0);
                canvas.Children.Add(rect);

                // Issue #4: Marching ants border for selected segment (1px yellow, animated)
                if (isSelected)
                {
                    var antsBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#fde047"));
                    double segW = Math.Max(2, x2 - x1);
                    double dashLen = 4;
                    double gapLen = 4;
                    double cycle = dashLen + gapLen;
                    double offset = _marchingAntsOffset;

                    // Top line (horizontal dashes)
                    for (double dx = -offset; dx < segW; dx += cycle)
                    {
                        double drawX = Math.Max(0, dx);
                        double drawW = Math.Min(dashLen, segW - drawX);
                        if (drawW <= 0) break;
                        var dash = new Avalonia.Controls.Shapes.Rectangle
                        {
                            Width = drawW,
                            Height = 1,
                            Fill = antsBrush
                        };
                        Avalonia.Controls.Canvas.SetLeft(dash, x1 + drawX);
                        Avalonia.Controls.Canvas.SetTop(dash, 0);
                        canvas.Children.Add(dash);
                    }

                    // Bottom line (horizontal dashes)
                    for (double dx = -offset; dx < segW; dx += cycle)
                    {
                        double drawX = Math.Max(0, dx);
                        double drawW = Math.Min(dashLen, segW - drawX);
                        if (drawW <= 0) break;
                        var dash = new Avalonia.Controls.Shapes.Rectangle
                        {
                            Width = drawW,
                            Height = 1,
                            Fill = antsBrush
                        };
                        Avalonia.Controls.Canvas.SetLeft(dash, x1 + drawX);
                        Avalonia.Controls.Canvas.SetTop(dash, h - 1);
                        canvas.Children.Add(dash);
                    }

                    // Left line (vertical dashes)
                    for (double dy = -offset; dy < h; dy += cycle)
                    {
                        double drawY = Math.Max(0, dy);
                        double drawH = Math.Min(dashLen, h - drawY);
                        if (drawH <= 0) break;
                        var dash = new Avalonia.Controls.Shapes.Rectangle
                        {
                            Width = 1,
                            Height = drawH,
                            Fill = antsBrush
                        };
                        Avalonia.Controls.Canvas.SetLeft(dash, x1);
                        Avalonia.Controls.Canvas.SetTop(dash, drawY);
                        canvas.Children.Add(dash);
                    }

                    // Right line (vertical dashes)
                    for (double dy = -offset; dy < h; dy += cycle)
                    {
                        double drawY = Math.Max(0, dy);
                        double drawH = Math.Min(dashLen, h - drawY);
                        if (drawH <= 0) break;
                        var dash = new Avalonia.Controls.Shapes.Rectangle
                        {
                            Width = 1,
                            Height = drawH,
                            Fill = antsBrush
                        };
                        Avalonia.Controls.Canvas.SetLeft(dash, x1 + segW - 1);
                        Avalonia.Controls.Canvas.SetTop(dash, drawY);
                        canvas.Children.Add(dash);
                    }
                }
            }

            // --- Phase 2: Draw ticks/labels ON TOP of segments (top layer) ---
            double tickInterval = 5;
            if (dur > 3600) tickInterval = 300;
            else if (dur > 1800) tickInterval = 60;
            else if (dur > 300) tickInterval = 30;
            else if (dur > 60) tickInterval = 10;

            for (double t = 0; t <= dur; t += tickInterval)
            {
                double tx = (t / dur) * w;

                var tickLine = new Avalonia.Controls.Shapes.Rectangle
                {
                    Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(140, 255, 255, 255)),
                    Width = 1,
                    Height = h
                };
                Avalonia.Controls.Canvas.SetLeft(tickLine, tx);
                Avalonia.Controls.Canvas.SetTop(tickLine, 0);
                canvas.Children.Add(tickLine);

                // Tick label with dark background for readability over segments
                var tickText = new TextBlock
                {
                    Text = TimeSpan.FromSeconds(t).ToString(t >= 3600 ? "h\\:mm\\:ss" : "m\\:ss"),
                    Foreground = Avalonia.Media.Brushes.White,
                    FontSize = 9,
                    Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(180, 0, 0, 0)),
                    Padding = new Thickness(2, 0)
                };
                Avalonia.Controls.Canvas.SetLeft(tickText, tx + 2);
                Avalonia.Controls.Canvas.SetTop(tickText, 0);
                canvas.Children.Add(tickText);
            }

            // Draw pending start marker
            if (_pendingStartMs >= 0)
            {
                double px = (_pendingStartMs / 1000.0 / dur) * w;
                var line = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = 2, Height = h,
                    Fill = Avalonia.Media.Brushes.SeaGreen
                };
                Avalonia.Controls.Canvas.SetLeft(line, px);
                canvas.Children.Add(line);
            }

            // Draw pending end marker
            if (_pendingEndMs >= 0)
            {
                double px = (_pendingEndMs / 1000.0 / dur) * w;
                var line = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = 2, Height = h,
                    Fill = Avalonia.Media.Brushes.SeaGreen
                };
                Avalonia.Controls.Canvas.SetLeft(line, px);
                canvas.Children.Add(line);
            }
        });
    }

    // ------------------------------------------------------------------ playback timer
    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_videoHost?.IpcClient == null) return;

        // Update play/pause button label
        var playIcon = this.FindControl<Avalonia.Controls.Shapes.Polygon>("PlayIcon");
        var pauseIcon = this.FindControl<StackPanel>("PauseIcon");
        if (playIcon != null && pauseIcon != null)
        {
            bool isPaused = _videoHost.IpcClient.IsPaused;
            playIcon.IsVisible = isPaused;
            pauseIcon.IsVisible = !isPaused;
        }

        double t = _videoHost.IpcClient.CurrentTime;
        double fullDur = _videoHost.IpcClient.Duration;

        // ── Trim Region Constraint ──
        // Auto-pause when playback reaches trimEnd so user never sees content beyond their clip
        double trimEndSec = (_trimEndMs > 0) ? _trimEndMs / 1000.0 : fullDur;
        if (t >= trimEndSec && !_videoHost.IpcClient.IsPaused)
        {
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
        }

        // Clamp current time to trim region for display purposes
        double trimStartSec = _trimStartMs / 1000.0;
        double relTime = Math.Max(0, t - trimStartSec);
        double trimDurSec = Math.Max(0.1, trimEndSec - trimStartSec);

        if (fullDur > 0)
        {
            if (!_isTimelineDrawn)
            {
                RedrawTimeline();
                _isTimelineDrawn = true;
            }
        }

        // ── Apply granular speed segments in real-time during preview ──
        // Segments are stored relative to trim start, and relTime is also relative.
        // So we check if the current relative position falls within any segment.
        if (!_videoHost.IpcClient.IsPaused && _segments.Count > 0)
        {
            double currentRelMs = relTime * 1000.0;
            double targetSpeed = GetEditorSpeedForPosition(currentRelMs);
            if (Math.Abs(targetSpeed - _lastAppliedSpeed) > 0.001)
            {
                _lastAppliedSpeed = targetSpeed;
                _ = _videoHost.IpcClient.SetPropertyAsync("speed",
                    targetSpeed.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        // Show time relative to the trim region (0:00 = MARK START)
        var elapsed = this.FindControl<TextBlock>("GranularTimeElapsed");
        if (elapsed != null) elapsed.Text = FormatSec(relTime);

        // Slider maps to the trim region, not the full video
        var slider = this.FindControl<Slider>("GranularTimeline");
        if (slider != null && trimDurSec > 0 && !slider.IsPointerOver)
        {
            _isSeeking = true;
            slider.Value = (relTime / trimDurSec) * 1000.0;
            _isSeeking = false;
        }

        // Keep canvas updated as playhead moves (but not every tick to reduce flicker — marching ants timer handles selection redraw)
        if (_selectedSegmentIndex >= 0 || _pendingStartMs >= 0 || _pendingEndMs >= 0)
        {
            // Redraw handled by marching ants timer
        }
        else
        {
            RedrawTimeline();
        }
    }

    // ------------------------------------------------------------------ helpers
    /// <summary>
    /// Returns the current playback position relative to the trim region.
    /// 0.0 = MARK START position.
    /// </summary>
    private double GetCurrentTime()
    {
        if (_videoHost?.IpcClient == null) return 0;
        double absTime = _videoHost.IpcClient.CurrentTime;
        double relTime = absTime - (_trimStartMs / 1000.0);
        double trimEndSec = (_trimEndMs > 0) ? _trimEndMs / 1000.0 : double.MaxValue;
        // Clamp to valid trim range
        if (relTime < 0) relTime = 0;
        if (relTime > trimEndSec - (_trimStartMs / 1000.0)) relTime = trimEndSec - (_trimStartMs / 1000.0);
        return relTime;
    }

    /// <summary>
    /// Looks up the playback speed for a given relative position (in ms from trim start).
    /// Returns the segment's speed if the position falls within a speed segment,
    /// otherwise returns the base speed. Freeze segments (speed ≈ 0) return 0.
    /// </summary>
    private double GetEditorSpeedForPosition(double relPosMs)
    {
        foreach (var seg in _segments)
        {
            if (relPosMs >= seg.StartMs && relPosMs < seg.EndMs)
            {
                return seg.Speed; // 0 = freeze frame
            }
        }
        return _baseSpeed;
    }

    /// <summary>
    /// Returns the duration of the trim region (not the full video).
    /// </summary>
    private int? FindSegmentAtPosition(int positionMs)
    {
        for (int i = 0; i < _segments.Count; i++)
        {
            if (positionMs >= _segments[i].StartMs && positionMs <= _segments[i].EndMs)
                return i;
        }
        return null;
    }

    private double GetDuration()
    {
        if (_videoHost?.IpcClient == null) return 0;
        double fullDur = _videoHost.IpcClient.Duration;
        double trimEndSec = (_trimEndMs > 0) ? _trimEndMs / 1000.0 : fullDur;
        double trimDur = Math.Max(0.1, trimEndSec - (_trimStartMs / 1000.0));

        // Update duration label to show trim-relative duration
        var durLbl = this.FindControl<TextBlock>("GranularDuration");
        if (durLbl != null && durLbl.Text != FormatSec(trimDur))
            Dispatcher.UIThread.Post(() => durLbl.Text = FormatSec(trimDur));
        return trimDur;
    }

    private static string FormatMs(double ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms < 0 ? 0 : ms);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    private static string FormatSec(double sec)
    {
        var ts = TimeSpan.FromSeconds(sec < 0 ? 0 : sec);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    /// <summary>
    /// Returns the timeline overlay color for a speed segment, based on its speed
    /// relative to the base (natural) speed:
    ///   • Freeze (≈0x)    → blue
    ///   • Below base speed → red
    ///   • ≥ base speed     → green
    /// The color is independent of selection state so that live edits recolor
    /// immediately even while a segment is highlighted.
    /// </summary>
    private Avalonia.Media.Color GetSegmentOverlayColor(SpeedSegment seg)
    {
        if (seg.Speed < 0.01)
            return Avalonia.Media.Color.FromArgb(120, 96, 165, 250);  // blue — freeze frame

        if (seg.Speed < _baseSpeed - 0.0001)
            return Avalonia.Media.Color.FromArgb(100, 239, 68, 68);   // red — slower than base

        return Avalonia.Media.Color.FromArgb(100, 34, 197, 94);       // green — base speed or faster
    }

    private void ShowFeedback(string text)
    {
        var popup = this.FindControl<Avalonia.Controls.Primitives.Popup>("GranularFeedbackPopup");
        var popupBorder = this.FindControl<Border>("GranularFeedbackPopupBorder");
        var popupText = this.FindControl<TextBlock>("GranularFeedbackPopupText");
        var videoBorder = this.FindControl<Border>("GranularVideoAreaBorder");
        FloatingFeedback.Show(popup, popupBorder, popupText, videoBorder, text);
    }

    private void SetStatus(string msg)
    {
        var lbl = this.FindControl<TextBlock>("BottomStatusLabel");
        if (lbl != null) lbl.Text = msg;
    }

    // ------------------------------------------------------------------ cleanup
    protected override async void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        // If the background work is done, allow the window to close normally
        if (_isSafeToClose)
        {
            base.OnClosing(e);
            return;
        }

        // STOP the synchronous UI-blocking close
        e.Cancel = true;

        // Hide the window instantly so the app feels incredibly fast and responsive
        this.Hide();

        RuntimeLog.Info("Granular", "Granular Speed Editor closing. Stopping timers and saving bounds async.");
        _playbackTimer?.Stop();
        _marchingAntsTimer?.Stop();

        try
        {
            // Perform the heavy Mutex locking and file I/O ASYNCHRONOUSLY
            await WindowBoundsHelper.SaveBoundsAsync(this, "GranularBounds");

            if (_videoHost?.IpcClient != null)
            {
                await _videoHost.IpcClient.SendCommandAsync("stop");
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Granular", $"Error saving state during close: {ex.Message}");
        }
        finally
        {
            // Mark as safe and programmatically re-trigger the close
            _isSafeToClose = true;
            this.Close();
        }
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        await WindowBoundsHelper.LoadBoundsAsync(this, "GranularBounds");
    }

    protected override void OnClosed(EventArgs e)
    {
        RuntimeLog.Info("Granular", "Granular Speed Editor closed. Disposing resources.");
        _playbackTimer?.Stop();
        _marchingAntsTimer?.Stop();
        base.OnClosed(e);
    }

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<Border>("TitleBarBorder");
        if (titleBar != null)
        {
            titleBar.IsHitTestVisible = true;
            titleBar.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    try { BeginMoveDrag(e); } catch { }
                }
            };
        }
    }
}
