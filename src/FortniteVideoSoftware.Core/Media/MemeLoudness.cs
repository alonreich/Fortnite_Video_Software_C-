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
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// MEMELEVEL_01 — A MEME IN A MERGE IS AS LOUD AS THE SAME MEME IN THE MAIN APP (Video-Merger-Migration.md P9).
///
/// The Main App (ProcessWorker, "MEME LEVEL PLAN") measures every meme with sound and brings it to
/// <see cref="AudioLoudnessProbe.TargetLufs"/>, gain clamped to
/// [<see cref="AudioLoudnessProbe.MinMusicGainDb"/>, <see cref="AudioLoudnessProbe.MaxMusicGainDb"/>],
/// then a peak limiter at -2 dB. The Merger's export (MERGEGRAPH_01) now does exactly the same, so a
/// meme dropped in either app lands at the same level. An unmeasurable meme is left as recorded (with
/// the limiter). The ProcessWorker copy is left untouched on purpose (Main App export is out of scope);
/// keep the two in step.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class MemeLoudness
{
    /// <summary>Gain in dB that brings <paramref name="path"/> to the target; 0 when it cannot be measured.</summary>
    public static async Task<double> GainDbAsync(string ffmpegPath, string path, CancellationToken token)
    {
        try
        {
            var reading = await AudioLoudnessProbe.MeasureAsync(ffmpegPath, path, token).ConfigureAwait(false);
            if (reading == null)
            {
                CoreLogger.Info("Audio", $"Merger meme level could not be measured for '{Path.GetFileName(path)}' — leaving it as recorded.");
                return 0;
            }
            double gain = GainFor(reading.IntegratedLufs);
            CoreLogger.Info("Audio",
                $"MERGER MEME LEVEL PLAN: '{Path.GetFileName(path)}' measured {reading.IntegratedLufs:F2} LUFS -> target " +
                $"{AudioLoudnessProbe.TargetLufs:F1} LUFS = {gain:+0.00;-0.00} dB, then a peak limiter.");
            return gain;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            CoreLogger.Info("Audio", $"Merger meme level measurement skipped: {ex.Message}");
            return 0;
        }
    }

    /// <summary>The Main App's rule: target minus measured, clamped.</summary>
    public static double GainFor(double integratedLufs)
        => Math.Clamp(AudioLoudnessProbe.TargetLufs - integratedLufs, AudioLoudnessProbe.MinMusicGainDb, AudioLoudnessProbe.MaxMusicGainDb);

    /// <summary>Filters appended to a meme's audio (leading comma): volume when the gain matters, then the limiter.</summary>
    public static string Chain(double gainDb)
    {
        string chain = "";
        if (Math.Abs(gainDb) > 0.01 && double.IsFinite(gainDb))
            chain += ",volume=" + Math.Pow(10, gainDb / 20.0).ToString("F4", CultureInfo.InvariantCulture);
        return chain + ",alimiter=limit=-2.0dB:level_in=1:level_out=1";
    }
}
