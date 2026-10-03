// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// LANECACHE_02 (Video-Merger-Migration.md P11) — A CLIP'S WAVEFORM AS NUMBERS, NOT AS A PICTURE.
///
/// The Merger's waveform lane used to be a PNG rendered by ffmpeg at the lane's pixel width, so every
/// resize produced a new ffmpeg run per clip. Here a clip's sound is read ONCE as tiny mono PCM
/// (<see cref="SampleRate"/> Hz — plenty for an envelope) and reduced to peaks at a FIXED rate
/// (<see cref="PeaksPerSecond"/>, at most <see cref="MaxPeaks"/>). The window draws those peaks as a
/// vector shape stretched to whatever width the clip has, so resizing and reordering cost nothing.
/// Peaks are 0..1 (absolute sample peak per bucket). Serializable for the on-disk cache.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class WaveformPeaks
{
    public const int SampleRate = 4000;
    public const int PeaksPerSecond = 40;
    public const int MaxPeaks = 6000;

    /// <summary>Number of peaks for a window of <paramref name="durationSec"/>.</summary>
    public static int PeakCount(double durationSec)
        => Math.Clamp((int)Math.Ceiling(Math.Max(0.05, durationSec) * PeaksPerSecond), 1, MaxPeaks);

    /// <summary>Reduces signed 16-bit mono samples to <paramref name="count"/> peaks (0..1).</summary>
    public static float[] Reduce(ReadOnlySpan<short> samples, int count)
    {
        count = Math.Max(1, count);
        var peaks = new float[count];
        if (samples.Length == 0) return peaks;
        for (int i = 0; i < samples.Length; i++)
        {
            int b = (int)((long)i * count / samples.Length);
            int v = Math.Abs((int)samples[i]);
            float f = v / 32768f;
            if (f > peaks[b]) peaks[b] = f;
        }
        return peaks;
    }

    /// <summary>Reads the window's audio once and returns its peaks; null when there is no audio or ffmpeg fails.</summary>
    public static async Task<float[]?> ExtractAsync(string ffmpegPath, string path, double startSec, double durationSec, CancellationToken ct)
    {
        var ci = CultureInfo.InvariantCulture;
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", Math.Max(0, startSec).ToString("0.###", ci),
            "-t", Math.Max(0.05, durationSec).ToString("0.###", ci),
            "-i", path,
            "-vn", "-sn", "-dn", "-ac", "1", "-ar", SampleRate.ToString(ci),
            "-f", "s16le", "-acodec", "pcm_s16le", "-",
        }) psi.ArgumentList.Add(a);

        Process? p = null;
        try
        {
            p = Process.Start(psi);
            if (p == null) return null;
            try { ChildProcessTracker.AddProcess(p); } catch (Exception ex) { CoreLogger.Swallowed(ex); }
            var err = p.StandardError.ReadToEndAsync(ct);
            using var ms = new MemoryStream();
            await p.StandardOutput.BaseStream.CopyToAsync(ms, ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            _ = await err.ConfigureAwait(false);
            if (ms.Length < 2) return null;

            var bytes = ms.GetBuffer();
            int n = (int)(ms.Length / 2);
            var samples = new short[n];
            Buffer.BlockCopy(bytes, 0, samples, 0, n * 2);
            return Reduce(samples, PeakCount(durationSec));
        }
        catch (OperationCanceledException)
        {
            try { if (p is { HasExited: false }) p.Kill(true); } catch (Exception ex) { CoreLogger.Swallowed(ex); }
            throw;
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return null;
        }
        finally
        {
            p?.Dispose();
        }
    }

    /// <summary>Little-endian float32 array, for the on-disk cache.</summary>
    public static byte[] ToBytes(float[] peaks)
    {
        var b = new byte[peaks.Length * 4];
        Buffer.BlockCopy(peaks, 0, b, 0, b.Length);
        return b;
    }

    public static float[]? FromBytes(byte[]? bytes)
    {
        if (bytes == null || bytes.Length < 4 || bytes.Length % 4 != 0) return null;
        var f = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, f, 0, bytes.Length);
        foreach (var v in f) if (!(v >= 0 && v <= 1.0001f)) return null;
        return f;
    }
}
