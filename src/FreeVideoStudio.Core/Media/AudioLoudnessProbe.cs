// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// One loudness measurement of a source file, in the units FFmpeg's <c>loudnorm</c> analysis
/// reports them.
///
/// LOUDSTD_REMOVED_01 — there is NO loudness standard in this app any more. The old
/// "too quiet / too loud versus -14 LUFS" verdict, its prompt, its setting and the dead
/// normalisation pipeline behind it were deleted. A reading exists for exactly three consumers:
/// harsh-peak detection (<see cref="HasHarshPeaks"/>), the peak tamer's threshold
/// (<see cref="PeakSafety.TamerThresholdDb"/>), and matching a meme / a Merger clip to the
/// gameplay it sits in (<see cref="MemeLoudness"/>). None of them moves the gameplay level.
/// </summary>
/// <param name="IntegratedLufs">Average perceived loudness over the measured window (LUFS).</param>
/// <param name="TruePeakDbtp">Highest true peak (dBTP). Above 0 clips.</param>
/// <param name="LoudnessRangeLu">Spread between the quiet and loud parts (LU).</param>
public sealed record LoudnessReading(
    double IntegratedLufs,
    double TruePeakDbtp,
    double LoudnessRangeLu)
{
    /// <summary>
    /// Crest factor: how far the loudest instant sticks out above the average body of the
    /// audio. A conversational clip sits near 10-14 LU; a gameplay capture with an explosion
    /// or a scream spikes far higher, and that spike is what hurts a viewer wearing headphones.
    /// </summary>
    public double PeakAboveAverageLu => TruePeakDbtp - IntegratedLufs;

    /// <summary>
    /// True when the file contains sudden peaks far above its own average AND those peaks
    /// actually reach the danger zone — the "quiet video, then an explosion takes your head off"
    /// problem, which the peak tamer fixes.
    /// </summary>
    public bool HasHarshPeaks =>
        PeakAboveAverageLu > AudioLoudnessProbe.CrestWarnLu &&
        TruePeakDbtp > AudioLoudnessProbe.HarshPeakFloorDbtp;
}

/// <summary>
/// Measures a media file's loudness and peaks (EBU R128, via FFmpeg's <c>loudnorm</c> in pure
/// analysis mode — its output is discarded into the null muxer, nothing is ever normalised).
/// </summary>
public static class AudioLoudnessProbe
{
    /// <summary>
    /// A true peak above this counts towards <see cref="LoudnessReading.HasHarshPeaks"/>.
    /// </summary>
    public const double HarshPeakFloorDbtp = -1.5;

    /// <summary>
    /// BEDSEG_01 — the shortest window worth measuring on its own.
    ///
    /// EBU R128 integrated loudness gates in 400 ms blocks and then applies a relative gate across
    /// them; with only a second or two of material the relative gate has almost nothing to work on
    /// and loudnorm reports a figure that swings wildly or comes back as -70 (silence). Anything
    /// shorter than this falls back to measuring the whole file — a known-imperfect answer beats a
    /// random one.
    /// </summary>
    public const double MinSegmentSec = 5.0;

    /// <summary>
    /// Crest factor above which peaks count as "harsh". Speech and music normally land around
    /// 10-14 LU above their own average; beyond 15 LU there is a genuine bang in the file.
    /// </summary>
    public const double CrestWarnLu = 15.0;

    /// <summary>
    /// Measures <paramref name="inputPath"/> end to end.
    ///
    /// Integrated loudness is only meaningful over a WHOLE CONTIGUOUS STRETCH — a clip that is
    /// silent for a minute and then deafening averages out to something neither half resembles —
    /// so this decodes the entire window rather than sampling within it. By default that window is
    /// the whole file; BEDSEG_01 added the option to narrow it to the part that will actually be
    /// used (the exported range of a clip).
    /// Video, subtitles and data streams are dropped
    /// (<c>-vn -sn -dn</c>) so only the audio is touched, which keeps it fast enough to run in
    /// the background while the user is already working.
    ///
    /// Returns null when the file has no audio, when FFmpeg fails, or when cancelled. A null
    /// return must never block the user — it simply means "we could not tell", and the caller
    /// stays silent rather than guessing.
    /// </summary>
    /// <param name="segmentStartSec">
    /// BEDSEG_01 — optional: measure only from this point in the file. See <see cref="MinSegmentSec"/>.
    /// </param>
    /// <param name="segmentDurationSec">
    /// BEDSEG_01 — optional: measure only this many seconds. Ignored together with
    /// <paramref name="segmentStartSec"/> when the window is shorter than <see cref="MinSegmentSec"/>.
    /// </param>
    public static async Task<LoudnessReading?> MeasureAsync(
        string ffmpegPath,
        string inputPath,
        CancellationToken cancellationToken = default,
        double segmentStartSec = 0.0,
        double segmentDurationSec = 0.0)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath)) return null;

        // PREVIEWMIX_01 — the live preview re-renders the mix after every edit, and each render
        // would otherwise re-measure the same gameplay window and the same memes. Readings are
        // cached per (file, size, write time, window); a changed file is a new key.
        string cacheKey;
        try
        {
            var fi = new FileInfo(inputPath);
            cacheKey = string.Create(CultureInfo.InvariantCulture,
                $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{segmentStartSec:F3}|{segmentDurationSec:F3}");
            lock (Cache) { if (Cache.TryGetValue(cacheKey, out var hit)) return hit; }
        }
        catch (Exception ex) { CoreLogger.Swallowed(ex); cacheKey = ""; }

        var measured = await MeasureUncachedAsync(ffmpegPath, inputPath, cancellationToken, segmentStartSec, segmentDurationSec).ConfigureAwait(false);
        if (measured != null && cacheKey.Length > 0)
        {
            lock (Cache)
            {
                if (Cache.Count >= 64) Cache.Clear();
                Cache[cacheKey] = measured;
            }
        }
        return measured;
    }

    private static readonly Dictionary<string, LoudnessReading> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static async Task<LoudnessReading?> MeasureUncachedAsync(
        string ffmpegPath,
        string inputPath,
        CancellationToken cancellationToken,
        double segmentStartSec,
        double segmentDurationSec)
    {

        // BEDSEG_01 — MEASURE THE MATERIAL THAT WILL ACTUALLY BE HEARD: the exported range of the
        // gameplay, or the kept window of a Merger clip. A window is only used when it is long
        // enough for EBU R128 integrated loudness to mean anything; below MinSegmentSec the gating
        // leaves too little material, so the whole file is measured instead.
        bool useSegment = segmentDurationSec >= MinSegmentSec && segmentStartSec >= 0;

        Process? process = null;
        try
        {
            var args = new List<string>
            {
                "-y", "-hide_banner", "-nostdin",
            };

            if (useSegment)
            {
                // Before -i, so the decoder seeks instead of decoding and discarding.
                args.Add("-ss");
                args.Add(segmentStartSec.ToString("F3", CultureInfo.InvariantCulture));
                args.Add("-t");
                args.Add(segmentDurationSec.ToString("F3", CultureInfo.InvariantCulture));
            }

            args.AddRange(new[]
            {
                "-i", inputPath,
                // Analysis only: the targets are loudnorm's defaults and irrelevant — input_* are
                // properties of the SOURCE, and the processed audio goes to the null muxer.
                "-af", "loudnorm=print_format=json",
                "-vn", "-sn", "-dn",
                "-f", "null", "-"
            });

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            CoreLogger.Debug("LoudnessProbe", $"Measuring: {ffmpegPath} {ProcessArgs.FormatForLog(args)}");

            process = Process.Start(psi);
            if (process == null) return null;

            try { ChildProcessTracker.AddProcess(process); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

            Task<string> stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string stdErr = await stdErrTask.ConfigureAwait(false);
            _ = await stdOutTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                CoreLogger.Debug("LoudnessProbe",
                    $"FFmpeg exited {process.ExitCode} measuring '{Path.GetFileName(inputPath)}'; skipping the loudness check.");
                return null;
            }

            var reading = ParseJsonBlock(stdErr);
            if (reading == null)
            {
                CoreLogger.Debug("LoudnessProbe", "No parsable loudnorm JSON block in the output.");
                return null;
            }

            CoreLogger.Info("LoudnessProbe",
                $"'{Path.GetFileName(inputPath)}': I={reading.IntegratedLufs:F2} LUFS, " +
                $"TP={reading.TruePeakDbtp:F2} dBTP, LRA={reading.LoudnessRangeLu:F2} LU " +
                $"(peak {reading.PeakAboveAverageLu:F1} LU above average{(reading.HasHarshPeaks ? ", HARSH PEAKS" : "")}).");

            return reading;
        }
        catch (OperationCanceledException)
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
            throw;
        }
        catch (Exception ex)
        {
            CoreLogger.Warn("LoudnessProbe", $"Loudness measurement threw: {ex.Message}");
            return null;
        }
        finally
        {
            try { process?.Dispose(); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
        }
    }

    /// <summary>
    /// Pulls the final JSON object out of loudnorm's stderr. The filter prints its report last,
    /// so the LAST braces in the stream are the ones that matter.
    /// </summary>
    private static LoudnessReading? ParseJsonBlock(string stdErr)
    {
        if (string.IsNullOrWhiteSpace(stdErr)) return null;

        int start = stdErr.LastIndexOf('{');
        int end = stdErr.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        try
        {
            var node = JsonNode.Parse(stdErr.Substring(start, end - start + 1));
            if (node == null) return null;

            if (!TryReadDouble(node, "input_i", out double i)) return null;
            if (!TryReadDouble(node, "input_tp", out double tp)) return null;
            if (!TryReadDouble(node, "input_lra", out double lra)) return null;

            if (double.IsInfinity(i) || double.IsNaN(i)) return null;

            return new LoudnessReading(i, tp, lra);
        }
        catch (System.Exception swallowed)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed);   // FAULTTIER_02 — no failure is silent.
            return null;
        }
    }

    private static bool TryReadDouble(JsonNode node, string key, out double value)
    {
        value = 0;
        try
        {
            var raw = node[key];
            if (raw == null) return false;
            return double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
        catch (System.Exception swallowed2)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed2);   // FAULTTIER_02 — no failure is silent.
            return false;
        }
    }
}
