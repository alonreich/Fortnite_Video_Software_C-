// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// LANES_01 — THE MERGER'S FILMSTRIP AND WAVEFORM (Video-Merger-Migration.md P5.1/P5.2, D9).
///
/// Two thin lanes under the merged timeline. Every clip gets one tile spanning its slice of the
/// merge; tiles are planned left→right (<see cref="LanePlanner"/>) and produced in that order by a
/// <see cref="ProgressiveLaneRunner"/> per lane (two ffmpeg processes at most per lane). Nothing waits
/// on them: the preview plays, the queue edits, and each tile paints itself when it lands. Filmstrip
/// tiles even paint frame by frame (<see cref="ThumbnailStripGenerator.StreamAsync"/>).
///
/// LANECACHE_02 (P11, user report 2026-09-27) — a clip is decoded ONCE, EVER. Its filmstrip is a fixed
/// grid of frames in source time (<see cref="ThumbGrid"/>) and its waveform a fixed list of peaks
/// (<see cref="WaveformPeaks"/>), both cached in memory and on disk (<see cref="Infrastructure.LaneDiskCache"/>)
/// under a key that ignores the lane width and the clip's position. A resize or a reorder therefore only
/// RE-PLACES cached pieces: each on-screen frame slot shows the nearest grid frame (a cropped view of the
/// strip, no pixel work) and the waveform is a vector shape stretched to the clip's width. ffmpeg runs
/// only for a clip it has never seen (keyframe-only decode, tiny frames, 2 at a time).
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class VideoMergerWindow
{
    private const int LaneParallelism = 2;
    private const int LaneCacheSize = 128;

    private readonly ProgressiveLaneRunner _filmRunner = new();
    private readonly ProgressiveLaneRunner _waveRunner = new();
    private readonly LaneCache<IImage> _filmCache = new(LaneCacheSize);
    private readonly LaneCache<float[]> _waveCache = new(LaneCacheSize);
    private DispatcherTimer? _laneTimer;
    private string? _laneKey;
    private bool _lanesClosed;
    private string? _laneFfmpeg;

    private void InitializeLanes()
    {
        _laneTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _laneTimer.Tick += (s, e) =>
        {
            _laneTimer.Stop();
            RefreshLanes();
        };
        this.Closed += (s, e) =>
        {
            _lanesClosed = true;
            _laneTimer.Stop();
            _filmRunner.Dispose();
            _waveRunner.Dispose();
        };
    }

    /// <summary>The merged layout or the lane width may have changed: replan soon (debounced).</summary>
    private void ScheduleLaneRefresh()
    {
        if (_laneTimer == null || _lanesClosed) return;
        _laneTimer.Stop();
        _laneTimer.Start();
    }

    private double FilmSlotPx(Canvas film) => (film.Height > 0 ? film.Height : 34) * 16.0 / 9.0;

    private void RefreshLanes()
    {
        var film = FilmLaneCtl;
        var wave = WaveLaneCtl;
        if (film == null || wave == null || _lanesClosed) return;

        bool show = TimelineMatchesQueue() && _timeline.TotalSec > 0;
        film.IsVisible = show;
        wave.IsVisible = show;
        if (!show)
        {
            _filmRunner.Cancel();
            _waveRunner.Cancel();
            _laneKey = null;
            film.Children.Clear();
            wave.Children.Clear();
            return;
        }

        double w = film.Bounds.Width;
        if (w <= 0) w = MarkersCanvasCtl?.Bounds.Width ?? 0;
        if (w <= 0) { ScheduleLaneRefresh(); return; }   // not laid out yet: try again shortly

        string key = $"{w:F0}|{_timeline.Signature}";
        if (key == _laneKey) return;
        _laneKey = key;

        var clips = new List<LaneClip>(_timeline.Clips.Count);
        var audioClips = new List<LaneClip>(_timeline.Clips.Count);
        foreach (var c in _timeline.Clips)
        {
            var id = FileIdentity(c.Path);
            var lc = new LaneClip(c.Index, c.Path, c.ContentStartSec, c.ContentEndSec, c.MergedStartSec, c.MergedEndSec,
                id?.Size ?? 0, id?.WriteTicks ?? 0);
            clips.Add(lc);
            if (!MergeClipAnalyzer.TryGetCompleted(c.Path, out var info) || info.HasAudio) audioClips.Add(lc);
        }

        var filmTiles = LanePlanner.Plan(clips, _timeline.TotalSec, w, FilmSlotPx(film), "film");
        var waveTiles = LanePlanner.Plan(audioClips, _timeline.TotalSec, w, 0, "wave");

        film.Children.Clear();
        wave.Children.Clear();
        var filmTodo = new List<LaneTile>();
        var waveTodo = new List<LaneTile>();
        foreach (var t in filmTiles)
        {
            if (_filmCache.TryGet(t.CacheKey, out var img)) PlaceFilmTile(film, t, img);
            else filmTodo.Add(t);
        }
        foreach (var t in waveTiles)
        {
            if (_waveCache.TryGet(t.CacheKey, out var peaks)) PlaceWaveTile(wave, t, peaks);
            else waveTodo.Add(t);
        }

        _laneFfmpeg ??= BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
        if (filmTodo.Count > 0 || waveTodo.Count > 0)
            RuntimeLog.Info("MERGER", $"Lanes: {filmTiles.Count - filmTodo.Count}/{filmTiles.Count} filmstrip and {waveTiles.Count - waveTodo.Count}/{waveTiles.Count} waveform tiles from memory; the rest from the disk cache or ffmpeg, in the background.");
        _ = _filmRunner.RunAsync(filmTodo, BuildFilmTileAsync, LaneParallelism);
        _ = _waveRunner.RunAsync(waveTodo, BuildWaveTileAsync, LaneParallelism);
    }

    /// <summary>
    /// Lays one clip's filmstrip into its slice: as many frame slots as fit (lane height × 16:9 each),
    /// each showing the nearest grid frame of the clip's strip. Returns the slot images (to repaint while
    /// a strip is still filling).
    /// </summary>
    private List<Image> PlaceFilmTile(Canvas lane, LaneTile t, IImage strip)
    {
        var images = new List<Image>();
        int frames = Math.Max(1, t.Frames);
        int slots = ThumbGrid.Slots(t.Width, FilmSlotPx(lane));
        int[] pick = ThumbGrid.Pick(frames, slots);
        double slotW = t.Width / slots;
        double h = lane.Height > 0 ? lane.Height : 34;
        int fw = ThumbnailStripGenerator.FrameWidthPx, fh = ThumbnailStripGenerator.StripHeightPx;
        for (int j = 0; j < slots; j++)
        {
            var img = new Image
            {
                Source = new CroppedBitmap(strip, new Avalonia.PixelRect(pick[j] * fw, 0, fw, fh)),
                Width = Math.Max(1, slotW),
                Height = h,
                Stretch = Stretch.UniformToFill,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(img, t.X0 + j * slotW);
            Canvas.SetTop(img, 0);
            lane.Children.Add(img);
            images.Add(img);
        }
        return images;
    }

    /// <summary>One clip's waveform: its peaks as a filled envelope, stretched to the clip's slice (vector, any width).</summary>
    private static void PlaceWaveTile(Canvas lane, LaneTile t, float[] peaks)
    {
        if (peaks.Length == 0) return;
        double h = lane.Height > 0 ? lane.Height : 18;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            int n = peaks.Length;
            // Scaled to the clip's own loudest moment so a quiet clip still reads as a shape.
            float top = 0.05f;
            foreach (var v in peaks) if (v > top) top = v;
            g.BeginFigure(new Avalonia.Point(0, 1), true);
            for (int i = 0; i < n; i++) g.LineTo(new Avalonia.Point(i + 0.5, 1 - Math.Min(1, peaks[i] / top)));
            g.LineTo(new Avalonia.Point(n, 1));
            for (int i = n - 1; i >= 0; i--) g.LineTo(new Avalonia.Point(i + 0.5, 1 + Math.Min(1, peaks[i] / top)));
            g.EndFigure(true);
        }
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = geo,
            Fill = Infrastructure.ThemeResources.Brush(lane, "AppInfoBrush", Brushes.DeepSkyBlue),
            Opacity = 0.75,
            Stretch = Stretch.Fill,
            Width = Math.Max(1, t.Width),
            Height = h,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(path, t.X0);
        Canvas.SetTop(path, 0);
        lane.Children.Add(path);
    }

    /// <summary>Runs on the thread pool: disk cache first, else ffmpeg (keyframes only), painting as frames land.</summary>
    private async Task BuildFilmTileAsync(LaneTile t, int generation, CancellationToken ct)
    {
        // 1. Disk cache — a clip seen in any earlier session paints at once.
        byte[]? png = await Task.Run(() => Infrastructure.LaneDiskCache.TryRead(t.CacheKey, ".png"), ct).ConfigureAwait(false);
        if (png != null)
        {
            Bitmap? cached = null;
            try { using var ms = new MemoryStream(png); cached = new Bitmap(ms); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            if (cached != null && cached.PixelSize.Width >= ThumbnailStripGenerator.FrameWidthPx * Math.Max(1, t.Frames))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _filmCache.Put(t.CacheKey, cached);
                    var lane = FilmLaneCtl;
                    if (lane != null && !_lanesClosed && generation == _filmRunner.Generation) PlaceFilmTile(lane, CurrentTile(t), cached);
                });
                return;
            }
        }

        // 2. ffmpeg — the fixed grid of this clip, painted frame by frame.
        string ffmpeg = _laneFfmpeg ?? "ffmpeg";
        List<Image>? shown = null;
        WriteableBitmap? bitmap = null;
        bool ok = await ThumbnailStripGenerator.StreamAsync(
            ffmpeg, t.Path, t.StartSec, t.DurationSec, ct,
            onReady: wb =>
            {
                bitmap = wb;
                var lane = FilmLaneCtl;
                if (lane != null && !_lanesClosed && generation == _filmRunner.Generation) shown = PlaceFilmTile(lane, CurrentTile(t), wb);
            },
            onFrame: () => { if (shown != null) foreach (var img in shown) img.InvalidateVisual(); },
            frames: Math.Max(1, t.Frames),
            logTag: "MergerLane").ConfigureAwait(false);

        if (!ok || bitmap is not WriteableBitmap done) return;
        byte[]? bytes = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _filmCache.Put(t.CacheKey, done);
            try { using var ms = new MemoryStream(); done.Save(ms); bytes = ms.ToArray(); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        });
        if (bytes != null) await Task.Run(() => Infrastructure.LaneDiskCache.Write(t.CacheKey, ".png", bytes)).ConfigureAwait(false);
    }

    /// <summary>Runs on the thread pool: disk cache first, else ffmpeg reads the clip's sound once and reduces it to peaks.</summary>
    private async Task BuildWaveTileAsync(LaneTile t, int generation, CancellationToken ct)
    {
        float[]? peaks = WaveformPeaks.FromBytes(await Task.Run(() => Infrastructure.LaneDiskCache.TryRead(t.CacheKey, ".peaks"), ct).ConfigureAwait(false));
        if (peaks == null)
        {
            peaks = await WaveformPeaks.ExtractAsync(_laneFfmpeg ?? "ffmpeg", t.Path, t.StartSec, t.DurationSec, ct).ConfigureAwait(false);
            if (peaks == null) return;
            var bytes = WaveformPeaks.ToBytes(peaks);
            _ = Task.Run(() => Infrastructure.LaneDiskCache.Write(t.CacheKey, ".peaks", bytes));
        }

        var done = peaks;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _waveCache.Put(t.CacheKey, done);
            var lane = WaveLaneCtl;
            if (lane != null && !_lanesClosed && generation == _waveRunner.Generation) PlaceWaveTile(lane, CurrentTile(t), done);
        });
    }

    /// <summary>A tile planned for an older width: re-derive its span on the lane as it is NOW (same clip).</summary>
    private LaneTile CurrentTile(LaneTile t)
    {
        var film = FilmLaneCtl;
        double w = film?.Bounds.Width ?? 0;
        if (w <= 0 || _timeline.TotalSec <= 0 || t.ClipIndex < 0 || t.ClipIndex >= _timeline.Clips.Count) return t;
        var c = _timeline.Clips[t.ClipIndex];
        return t with { X0 = c.MergedStartSec / _timeline.TotalSec * w, X1 = c.MergedEndSec / _timeline.TotalSec * w };
    }
}
