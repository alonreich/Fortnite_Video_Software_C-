
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// MERGEEDIT_02 — THE SAME GRANULAR EDITOR, OPENED ON A WHOLE MERGE (Video-Merger-Migration.md P6.3, D16).
///
/// The Merger opens this window with <c>videoPath</c> = the merge's inline <c>edl://</c> URL
/// (<see cref="MergeEditorSource.MpvUrl"/>), trim 0..<see cref="MergeEditorSource.TotalMs"/> and every
/// effect already mapped into MERGED milliseconds. To the editor that is one source with one clock;
/// mpv plays it seamlessly (P4.1). Only three things differ, all handled here or behind
/// <see cref="IsMergeMode"/>:
///   • no editor crash-recovery and no parked history (the Merger autosaves and undoes the EDL);
///   • the film lane cannot be decoded from a URL, so it is stitched from one strip per clip;
///   • clip boundaries are drawn as dividers, at their OUTPUT positions, so they follow speed edits.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class GranularSpeedEditorWindow
{
    private MergeEditorSource? _mergeSource;
    private Canvas? _mergeDividerCanvas;
    private bool _mergeDividersQueued;

    /// <summary>True when this editor edits a merge (its source is an inline mpv EDL).</summary>
    private bool IsMergeMode => _videoPath.StartsWith("edl://", StringComparison.Ordinal);

    /// <summary>The merge being edited. Set before ShowDialog; null in the Main App.</summary>
    public MergeEditorSource? MergeSource
    {
        get => _mergeSource;
        set
        {
            _mergeSource = value;
            if (value != null) this.Opened += (_, _) => InitializeMergeMode();
        }
    }

    private void InitializeMergeMode()
    {
        if (_mergeSource == null) return;
        Title = $"{Title} — {_mergeSource.Clips.Count} clips merged";
        if (_segmentCanvas != null)
        {
            _segmentCanvas.Children.CollectionChanged += (_, _) => QueueMergeDividers();
            _segmentCanvas.SizeChanged += (_, _) => QueueMergeDividers();
        }
        QueueMergeDividers();
        RuntimeLog.Info("Granular", $"Merge mode: {_mergeSource.Clips.Count} clips, {_mergeSource.TotalMs / 1000.0:F2}s merged.");
    }

    private void QueueMergeDividers()
    {
        if (_mergeDividersQueued || _editorClosing) return;
        _mergeDividersQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _mergeDividersQueued = false;
            DrawMergeDividers();
        }, DispatcherPriority.Background);
    }

    private void DrawMergeDividers()
    {
        var src = _mergeSource;
        var host = GranularLanesCtl?.LaneAHost;
        if (src == null || host == null || _editorClosing) return;
        if (_mergeDividerCanvas == null)
        {
            _mergeDividerCanvas = new Canvas { IsHitTestVisible = false, ClipToBounds = false, ZIndex = 50 };
            host.Children.Add(_mergeDividerCanvas);
        }
        var canvas = _mergeDividerCanvas;
        canvas.Children.Clear();
        double w = _segmentCanvas?.Bounds.Width > 0 ? _segmentCanvas.Bounds.Width : host.Bounds.Width;
        double h = Math.Max(24, host.Bounds.Height);
        if (w <= 0 || GetDuration() <= 0) return;

        IBrush line = Infrastructure.ThemeResources.Brush(host, "AppInfoBrush", Brushes.DeepSkyBlue);
        for (int i = 0; i < src.Clips.Count; i++)
        {
            var c = src.Clips[i];
            double x = SrcMsToX(c.StartMs, w);
            if (i > 0)
            {
                var bar = new Rectangle { Width = 2, Height = h, Fill = line, Opacity = 0.9, IsHitTestVisible = false };
                Canvas.SetLeft(bar, x - 1);
                Canvas.SetTop(bar, 0);
                canvas.Children.Add(bar);
            }
            double x1 = SrcMsToX(c.EndMs, w);
            if (x1 - x < 18) continue;
            var label = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(170, 15, 23, 42)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(3, 0),
                MaxWidth = Math.Max(14, x1 - x - 6),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                           + (x1 - x > 120 ? " · " + System.IO.Path.GetFileNameWithoutExtension(c.Path) : ""),
                    FontSize = Infrastructure.ThemeManager.ScaledFontSize(9),
                    Foreground = Brushes.White,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            Canvas.SetLeft(label, x + 3);
            Canvas.SetTop(label, h - 14);
            canvas.Children.Add(label);
        }
    }

    /// <summary>
    /// The merge's film lane: one keyframe strip per clip (frames in proportion to its length), blitted
    /// into ONE bitmap that spans the merged clock, so the editor's own lane code lays it out unchanged.
    /// The composite is mounted at once (dark) and repainted as each clip's frames land.
    /// </summary>
    private async Task BuildMergeFrameLaneAsync(Grid laneGrid, Border? loading)
    {
        var src = _mergeSource;
        if (src == null || src.Clips.Count == 0 || src.TotalMs <= 0) return;

        _thumbCts?.Cancel();
        var cts = new CancellationTokenSource();
        _thumbCts = cts;
        var token = cts.Token;

        int totalFrames = Math.Clamp((int)Math.Round(src.TotalMs / 1000.0 / 2.0), 15, 90);
        var frames = new int[src.Clips.Count];
        int sum = 0;
        for (int i = 0; i < src.Clips.Count; i++)
        {
            frames[i] = Math.Max(1, (int)Math.Round(totalFrames * src.Clips[i].LengthMs / src.TotalMs));
            sum += frames[i];
        }

        int fw = ThumbnailStripGenerator.FrameWidthPx, fh = ThumbnailStripGenerator.StripHeightPx;
        var composite = new WriteableBitmap(new PixelSize(fw * sum, fh), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        MountMergeLane(composite, laneGrid, loading);

        string ffmpeg = FreeVideoStudio.Core.Infrastructure.BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
        int offset = 0;
        for (int i = 0; i < src.Clips.Count; i++)
        {
            if (token.IsCancellationRequested || _editorClosing) return;
            var clip = src.Clips[i];
            int at = offset;
            offset += frames[i];
            WriteableBitmap? strip = null;
            try
            {
                await ThumbnailStripGenerator.StreamAsync(
                    ffmpeg, clip.Path, clip.KeepInUs / 1_000_000.0, clip.LengthMs / 1000.0, token,
                    onReady: wb => strip = wb,
                    onFrame: () =>
                    {
                        if (strip == null || _editorClosing || token.IsCancellationRequested) return;
                        BlitStrip(strip, composite, at * fw);
                        _filmstrip?.InvalidateVisual();
                    },
                    frames: frames[i],
                    logTag: "GranularMerge");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        }
        RuntimeLog.Info("Granular", $"Merge film lane stitched: {src.Clips.Count} clips, {sum} frames.");
    }

    private void MountMergeLane(WriteableBitmap bmp, Grid laneGrid, Border? loading)
    {
        if (_editorClosing) { bmp.Dispose(); return; }
        DeleteThumbStrip();
        _thumbBitmap = bmp;
        if (_frameLaneHost == null)
        {
            _frameLaneHost = new Canvas
            {
                Name = "GranularFrameLaneCanvas",
                ClipToBounds = true,
                MinHeight = LaneHeight,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            };
            _frameLaneHost.SizeChanged += (_, _) => QueueRelayoutFrameLane();
            laneGrid.Children.Clear();
            laneGrid.Children.Add(_frameLaneHost);
        }
        if (loading != null) loading.IsVisible = false;
        RelayoutFrameLane();
    }

    /// <summary>Copies a clip's strip into the composite at pixel column <paramref name="destX"/>.</summary>
    private static void BlitStrip(WriteableBitmap strip, WriteableBitmap composite, int destX)
    {
        int w = Math.Min(strip.PixelSize.Width, composite.PixelSize.Width - destX);
        int h = Math.Min(strip.PixelSize.Height, composite.PixelSize.Height);
        if (w <= 0 || h <= 0) return;
        int rowBytes = w * 4;
        var row = new byte[rowBytes];
        using var from = strip.Lock();
        using var to = composite.Lock();
        for (int y = 0; y < h; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(from.Address, y * from.RowBytes), row, 0, rowBytes);
            System.Runtime.InteropServices.Marshal.Copy(row, 0, IntPtr.Add(to.Address, y * to.RowBytes + destX * 4), rowBytes);
        }
    }
}
