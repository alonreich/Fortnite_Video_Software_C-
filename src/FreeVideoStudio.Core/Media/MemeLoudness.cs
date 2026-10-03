// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// MEMELEVEL_02 — A MEME IS AS LOUD AS THE GAMEPLAY IT INTERRUPTS. Shared by the Main App
/// (ProcessWorker) and the Merger (MergeClipGraph), so a meme lands at the same level in both.
///
/// MEMELEVEL_01 pinned every meme to a fixed -14 LUFS while the gameplay is deliberately left at its
/// recorded level (there is no loudness standard in this app — LOUDSTD_REMOVED_01). A typical
/// capture sits around -20 to -25 LUFS, so every meme jumped 6-11 dB above the video it cut into,
/// and the preview (which plays memes raw) never showed it.
///
/// Now: gain = measured gameplay loudness − measured meme loudness, clamped to
/// [<see cref="MinGainDb"/>, <see cref="MaxGainDb"/>]. Either side unmeasured → 0 dB (as recorded).
/// No per-meme limiter any more: the always-on safety limiter on the final mix
/// (<see cref="PeakSafety.SafetyLimiterFilter"/>) covers it.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class MemeLoudness
{
    /// <summary>Safety rails: a near-silent meme must not be boosted into a hiss bed.</summary>
    public const double MaxGainDb = 12.0;
    public const double MinGainDb = -24.0;

    /// <summary>Measures a meme; null when it cannot be measured.</summary>
    public static async Task<double?> MeasureLufsAsync(string ffmpegPath, string path, CancellationToken token)
    {
        try
        {
            var reading = await AudioLoudnessProbe.MeasureAsync(ffmpegPath, path, token).ConfigureAwait(false);
            if (reading == null)
            {
                CoreLogger.Info("Audio", $"Meme level could not be measured for '{Path.GetFileName(path)}' — leaving it as recorded.");
                return null;
            }
            return reading.IntegratedLufs;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            CoreLogger.Info("Audio", $"Meme level measurement skipped: {ex.Message}");
            return null;
        }
    }

    /// <summary>Gain that brings a meme measured at <paramref name="memeLufs"/> to <paramref name="gameplayLufs"/>.</summary>
    public static double GainFor(double? memeLufs, double? gameplayLufs)
    {
        if (memeLufs is not double m || gameplayLufs is not double g) return 0.0;
        if (!double.IsFinite(m) || !double.IsFinite(g) || m < -69.0 || g < -69.0) return 0.0;
        return Math.Clamp(g - m, MinGainDb, MaxGainDb);
    }

    /// <summary>Filters appended to a meme's audio (leading comma): the matching gain, or nothing.</summary>
    public static string Chain(double gainDb)
    {
        if (Math.Abs(gainDb) > 0.01 && double.IsFinite(gainDb))
            return ",volume=" + Math.Pow(10, gainDb / 20.0).ToString("F4", CultureInfo.InvariantCulture);
        return "";
    }

    /// <summary>
    /// SPLICE_03 — the de-click fade pair for a piece of audio that is butt-joined to an unrelated
    /// neighbour (a meme, another clip). Same rule as GranularSpeedBuilder's SPLICE_01/02: 8 ms,
    /// capped at 2% of the piece, nothing on a piece too short to carry it. Leading comma.
    /// </summary>
    public static string SpliceFade(double pieceDurationSec)
    {
        const double SpliceFadeSec = GranularSpeedBuilder.SpliceFadeSec;
        if (!(pieceDurationSec > SpliceFadeSec * 3)) return "";
        double fade = Math.Min(SpliceFadeSec, pieceDurationSec / 50.0);
        var ci = CultureInfo.InvariantCulture;
        string f = fade.ToString("F4", ci);
        double outStart = Math.Max(0, pieceDurationSec - fade);
        return $",afade=t=in:st=0:d={f},afade=t=out:st={outStart.ToString("F4", ci)}:d={f}";
    }
}
