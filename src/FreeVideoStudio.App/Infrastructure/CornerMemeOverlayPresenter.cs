// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
// ⚠️ NAME COLLISION (see MemePreviewDirector.cs): a bare MemePlacement here binds to the MEME_02 enum.
using CoreMeme = FreeVideoStudio.Core.Media.MemePlacement;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>One corner overlay on a host's GAMEPLAY clock (output seconds, full-screen memes not counted).</summary>
public sealed record CornerMemeSpan(CoreMeme Meme, double GameStartSec, double GameEndSec);

/// <summary>
/// MEMEMODE_01 — THE CORNER MEME, PREVIEWED AS IT EXPORTS (UI-MEMESELECT / FFM-MEMECORNER).
///
/// <para>A corner overlay is SIMULTANEOUS with the gameplay: this presenter never pauses, seeks or
/// swaps the one mpv player (that is <see cref="MemePreviewDirector"/>'s job, and only for full-screen
/// memes). It draws the meme's frames in an <see cref="Image"/> laid over the video host, positioned
/// with <see cref="MemeOverlayLayout"/> — the same box the export's <c>overlay</c> filter uses —
/// inside the letterboxed frame rectangle. The frame shown is a pure function of the host's gameplay
/// clock, so scrubbing, pausing and playing all stay in step without any timer of its own.</para>
///
/// <para>Frames come from <see cref="CornerMemeFrames"/> (decoded once per file, off the UI thread).
/// The meme's SOUND is heard through the rendered preview mix (PREVIEWMIX_01), which runs the export's
/// own audio graph; the live fallback players stay silent for it.</para>
/// </summary>
public sealed class CornerMemeOverlayPresenter
{
    private readonly Canvas _layer = new() { IsHitTestVisible = false, ZIndex = 50, ClipToBounds = true };
    private readonly Image _image = new() { Stretch = Avalonia.Media.Stretch.Fill, IsVisible = false };
    private IReadOnlyList<CornerMemeSpan> _spans = Array.Empty<CornerMemeSpan>();
    private Control? _videoHost;
    private (int W, int H) _frame = (16, 9);

    public CornerMemeOverlayPresenter()
    {
        _layer.Children.Add(_image);
    }

    /// <summary>
    /// Lays the overlay over <paramref name="videoHost"/>: added to the nearest ancestor panel and
    /// spanning it, with the host's own offset applied on every update (re-parents when the host
    /// moved, e.g. a detached preview).
    /// </summary>
    public void Attach(Control? videoHost)
    {
        if (videoHost == null) return;
        Panel? panel = null;
        for (var v = videoHost.Parent; v != null; v = v.Parent)
            if (v is Panel p) { panel = p; break; }
        if (panel == null) return;
        if (ReferenceEquals(videoHost, _videoHost) && ReferenceEquals(_layer.Parent, panel)) return;
        (_layer.Parent as Panel)?.Children.Remove(_layer);
        Grid.SetRow(_layer, 0);
        Grid.SetColumn(_layer, 0);
        Grid.SetRowSpan(_layer, 1000);
        Grid.SetColumnSpan(_layer, 1000);
        panel.Children.Add(_layer);
        _videoHost = videoHost;
    }

    /// <summary>The OUTPUT frame size the export overlays onto (portrait 1080x1920, else the source size).</summary>
    public void SetFrameSize(int width, int height)
    {
        if (width > 0 && height > 0) _frame = (width, height);
    }

    /// <summary>Replaces the overlays. Starts decoding their frames in the background.</summary>
    public void SetSpans(IReadOnlyList<CornerMemeSpan>? spans)
    {
        _spans = spans ?? Array.Empty<CornerMemeSpan>();
        foreach (var s in _spans) CornerMemeFrames.Prefetch(s.Meme.FilePath);
        if (_spans.Count == 0) Hide();
    }

    public void Hide() => _image.IsVisible = false;

    /// <summary>Shows the frame due at <paramref name="gameplaySec"/>, or nothing.</summary>
    public void Update(double gameplaySec)
    {
        CornerMemeSpan? active = null;
        foreach (var s in _spans)
            if (gameplaySec >= s.GameStartSec && gameplaySec < s.GameEndSec) { active = s; break; }
        if (active == null || _videoHost == null) { Hide(); return; }

        var frames = CornerMemeFrames.TryGet(active.Meme.FilePath);
        if (frames == null || frames.Frames.Count == 0) { Hide(); return; }

        int index = (int)Math.Floor((gameplaySec - active.GameStartSec) * frames.Fps);
        var bmp = frames.Frames[Math.Clamp(index, 0, frames.Frames.Count - 1)];

        // The letterboxed video rectangle inside the host, then the export's box inside it.
        var host = _videoHost.Bounds;
        if (host.Width < 2 || host.Height < 2 || _layer.Parent is not Visual panel) { Hide(); return; }
        Point origin = _videoHost.TranslatePoint(default, panel) ?? default;
        double scale = Math.Min(host.Width / _frame.W, host.Height / _frame.H);
        double vw = _frame.W * scale, vh = _frame.H * scale;
        double ox = origin.X + (host.Width - vw) / 2, oy = origin.Y + (host.Height - vh) / 2;
        var (x, y, w, h) = MemeOverlayLayout.Place(_frame.W, _frame.H, bmp.PixelSize.Width, bmp.PixelSize.Height,
            active.Meme.Corner, active.Meme.Size);

        _image.Source = bmp;
        _image.Width = w * scale;
        _image.Height = h * scale;
        Canvas.SetLeft(_image, ox + x * scale);
        Canvas.SetTop(_image, oy + y * scale);
        _image.IsVisible = true;
    }
}

/// <summary>A meme decoded for the corner preview.</summary>
public sealed record CornerMemeFrameSet(IReadOnlyList<Bitmap> Frames, double Fps);

/// <summary>
/// MEMEMODE_01 — corner-preview frames, decoded ONCE per file (path + size + mtime) off the UI thread:
/// a picture is one bitmap; a video is piped out of the bundled FFmpeg as a PNG sequence at
/// <see cref="Fps"/>, at most <see cref="MaxSeconds"/> long and <see cref="Width"/> px wide.
/// </summary>
public static class CornerMemeFrames
{
    public const double Fps = 12;
    public const int Width = 360;
    public const double MaxSeconds = 30;

    private static readonly ConcurrentDictionary<string, Task<CornerMemeFrameSet?>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static void Prefetch(string path) => _ = Get(path);

    /// <summary>The frames if they are ready; null while decoding or on failure.</summary>
    public static CornerMemeFrameSet? TryGet(string path)
    {
        var t = Get(path);
        return t is { IsCompletedSuccessfully: true } ? t.Result : null;
    }

    private static Task<CornerMemeFrameSet?>? Get(string path)
    {
        string key;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length == 0) return null;
            key = $"{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); return null; }
        return _cache.GetOrAdd(key, _ => Task.Run(() => DecodeAsync(path)));
    }

    private static async Task<CornerMemeFrameSet?> DecodeAsync(string path)
    {
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg")
            {
                using var fs = File.OpenRead(path);
                return new CornerMemeFrameSet(new[] { Bitmap.DecodeToWidth(fs, Width, BitmapInterpolationMode.MediumQuality) }, Fps);
            }

            string ffmpeg = BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-i", path, "-t", MaxSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         "-vf", $"fps={Fps.ToString(System.Globalization.CultureInfo.InvariantCulture)},scale={Width}:-2",
                         "-f", "image2pipe", "-c:v", "png", "pipe:1",
                     })
                psi.ArgumentList.Add(a);

            using Process? p = Process.Start(psi);
            if (p == null) return null;
            try { ChildProcessTracker.AddProcess(p); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var ms = new MemoryStream();
            Task copy = p.StandardOutput.BaseStream.CopyToAsync(ms, timeout.Token);
            Task<string> err = p.StandardError.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await copy.ConfigureAwait(false);
            _ = await err.ConfigureAwait(false);
            if (p.ExitCode != 0) return null;

            var frames = new List<Bitmap>();
            foreach (var png in SplitPngStream(ms.ToArray()))
            {
                using var one = new MemoryStream(png);
                frames.Add(new Bitmap(one));
            }
            return frames.Count > 0 ? new CornerMemeFrameSet(frames, Fps) : null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("Memes", $"Corner preview frames failed for '{Path.GetFileName(path)}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Splits concatenated PNG files on each IEND chunk (+ its 4-byte CRC).</summary>
    public static IEnumerable<byte[]> SplitPngStream(byte[] data)
    {
        int start = 0;
        for (int i = 0; i + 8 <= data.Length; i++)
        {
            if (data[i] == (byte)'I' && data[i + 1] == (byte)'E' && data[i + 2] == (byte)'N' && data[i + 3] == (byte)'D')
            {
                int end = i + 8;
                var chunk = new byte[end - start];
                Buffer.BlockCopy(data, start, chunk, 0, chunk.Length);
                yield return chunk;
                start = end;
                i = end - 1;
            }
        }
    }
}
