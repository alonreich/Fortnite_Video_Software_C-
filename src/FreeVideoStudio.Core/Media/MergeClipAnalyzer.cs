// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

/// <summary>What the merger needs to know about one queued file. <see cref="Ok"/> is false when the probe failed.</summary>
public sealed record MergeClipInfo(string Path, double DurationSec, double IntroSec, bool HasAudio, int Width, int Height, bool Ok)
{
    /// <summary>TIMINGTAG_02 — frame-exact timing (intro + fades) when the file carries it; null otherwise.</summary>
    public ExportTiming? Timing { get; init; }
}

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// SCRAPER_03 — CLIPS ARE ANALYSED THE MOMENT THEY ARE QUEUED, IN THE BACKGROUND, ONCE.
///
/// A user adds clips long before pressing MERGE. That time is used: each file gets ONE ffprobe
/// (format + streams, which is where <see cref="IntroTag"/> lives) on the thread pool, a few at a
/// time, the instant it enters the queue. The result is cached per file identity
/// (path + size + last write), so reordering, removing and re-adding, reopening the merger, or
/// toggling the scraper never probes again. The UI thread only ever awaits finished tasks.
///
/// Concurrency is bounded (half the cores, 2..4) so a drop of forty clips does not start forty
/// ffprobe processes at once and stall the disk the preview is reading from.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class MergeClipAnalyzer
{
    private sealed record Entry(long Size, DateTime WriteUtc, Task<MergeClipInfo> Task);

    private static readonly ConcurrentDictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Gate = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));

    /// <summary>Starts (or joins) the analysis of <paramref name="path"/>. Never throws; never runs on the caller's thread.</summary>
    public static Task<MergeClipInfo> AnalyzeAsync(string ffprobePath, string path)
    {
        long size = 0;
        DateTime write = default;
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists) { size = fi.Length; write = fi.LastWriteTimeUtc; }
        }
        catch (Exception ex) { CoreLogger.Swallowed(ex); }

        while (true)
        {
            if (Cache.TryGetValue(path, out var hit))
            {
                bool fresh = hit.Size == size && hit.WriteUtc == write;
                var done = hit.Task;
                bool failed = done.IsCompletedSuccessfully && !done.Result.Ok;
                if (fresh && !failed) return hit.Task;
                var replacement = new Entry(size, write, Task.Run(() => ProbeAsync(ffprobePath, path)));
                if (Cache.TryUpdate(path, replacement, hit)) return replacement.Task;
                continue;
            }

            var entry = new Entry(size, write, Task.Run(() => ProbeAsync(ffprobePath, path)));
            if (Cache.TryAdd(path, entry)) return entry.Task;
        }
    }

    /// <summary>A finished, successful result for <paramref name="path"/>, without starting anything.</summary>
    public static bool TryGetCompleted(string path, out MergeClipInfo info)
    {
        if (Cache.TryGetValue(path, out var hit))
        {
            var done = hit.Task;
            if (done.IsCompletedSuccessfully && done.Result.Ok)
            {
                info = done.Result;
                return true;
            }
        }
        info = null!;
        return false;
    }

    private static async Task<MergeClipInfo> ProbeAsync(string ffprobePath, string path)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var prober = new MediaProber(ffprobePath, path);
            double duration = await prober.GetDurationAsync().ConfigureAwait(false);
            ExportTiming? timing = await prober.GetExportTimingAsync().ConfigureAwait(false);
            double intro = timing?.IntroSec ?? 0;
            if (timing is ExportTiming tt && tt.IntroFrames > 0)
            {
                // FRAMESNAP_01 — the cut is the REAL pts of the first kept frame, not frames / nominal fps.
                var pts = await FramePtsProbe.ProbeAsync(ffprobePath, path, tt.IntroFrames + 1).ConfigureAwait(false);
                if (FramePtsProbe.IntroCutUs(pts, tt.IntroFrames) is long cutUs)
                {
                    CoreLogger.Info("Merger", $"{Path.GetFileName(path)}: intro cut snapped {intro:F6}s -> {cutUs / 1_000_000.0:F6}s (frame {tt.IntroFrames}).");
                    intro = cutUs / 1_000_000.0;
                }
            }
            bool audio = await prober.HasAudioAsync().ConfigureAwait(false);
            var (w, h) = await prober.GetResolutionAsync().ConfigureAwait(false);
            bool ok = duration > 0;
            CoreLogger.Info("Merger", $"Analysed {Path.GetFileName(path)}: {duration:F2}s, timing {(timing is ExportTiming t ? ExportTimingTag.Format(t) : "none")}.");
            return new MergeClipInfo(path, duration, intro, audio, w, h, ok) { Timing = timing };
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return new MergeClipInfo(path, 0, 0, false, 0, 0, false);
        }
        finally
        {
            Gate.Release();
        }
    }
}
