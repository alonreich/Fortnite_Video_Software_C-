using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// How a source file's audio compares to the broadcast/streaming loudness standard.
/// </summary>
public enum LoudnessVerdict
{
    /// <summary>Inside the accepted band — nothing to warn about.</summary>
    WithinStandard,
    /// <summary>Quieter than standard; viewers would have to turn the volume up.</summary>
    TooQuiet,
    /// <summary>Louder than standard; viewers would have to turn the volume down.</summary>
    TooLoud,
    /// <summary>The file has no audio track, or measurement failed.</summary>
    Unknown
}

/// <summary>
/// One complete loudness measurement of a source file, in the units FFmpeg's
/// <c>loudnorm</c> filter reports them.
/// </summary>
/// <param name="IntegratedLufs">Average perceived loudness over the whole file (LUFS).</param>
/// <param name="TruePeakDbtp">Highest true peak sample (dBTP). Above 0 clips.</param>
/// <param name="LoudnessRangeLu">Spread between the quiet and loud parts (LU).</param>
/// <param name="ThresholdDb">loudnorm's gating threshold — needed for the second pass.</param>
/// <param name="TargetOffsetDb">loudnorm's own suggested offset — needed for the second pass.</param>
public sealed record LoudnessReading(
    double IntegratedLufs,
    double TruePeakDbtp,
    double LoudnessRangeLu,
    double ThresholdDb,
    double TargetOffsetDb)
{
    /// <summary>
    /// How far this file sits from the target, in LU. Positive = too quiet (needs a boost),
    /// negative = too loud (needs a cut). This is exactly the gain normalisation would apply.
    /// </summary>
    public double GainToStandardDb => AudioLoudnessProbe.TargetLufs - IntegratedLufs;

    /// <summary>
    /// Crest factor: how far the loudest instant sticks out above the average body of the
    /// audio. A conversational clip sits near 10-14 LU; a gameplay capture with an explosion
    /// or a scream spikes far higher, and that spike is what hurts a viewer wearing headphones.
    /// </summary>
    public double PeakAboveAverageLu => TruePeakDbtp - IntegratedLufs;

    public LoudnessVerdict Verdict
    {
        get
        {
            double delta = IntegratedLufs - AudioLoudnessProbe.TargetLufs;
            if (delta < -AudioLoudnessProbe.ToleranceLu) return LoudnessVerdict.TooQuiet;
            if (delta > AudioLoudnessProbe.ToleranceLu) return LoudnessVerdict.TooLoud;
            return LoudnessVerdict.WithinStandard;
        }
    }

    /// <summary>
    /// True when the file contains sudden peaks far above its own average AND those peaks
    /// actually reach the danger zone. Both conditions matter: a uniformly loud file is a
    /// <see cref="LoudnessVerdict.TooLoud"/> problem (fixed by normalising), whereas THIS is
    /// the "quiet video, then an explosion takes your head off" problem (fixed by limiting).
    /// </summary>
    public bool HasHarshPeaks =>
        PeakAboveAverageLu > AudioLoudnessProbe.CrestWarnLu &&
        TruePeakDbtp > AudioLoudnessProbe.PeakCeilingDbtp;
}

/// <summary>
/// Measures a media file's real loudness so the app can tell the user — BEFORE they spend time
/// editing — that their capture is quieter or louder than viewers expect, or that it hides a
/// sudden peak that would startle an audience.
///
/// This is the same measurement FFmpeg's two-pass <c>loudnorm</c> uses, so a reading taken here
/// can be handed straight to the export's second pass without measuring twice.
/// </summary>
public static class AudioLoudnessProbe
{
    /// <summary>
    /// The streaming/broadcast loudness target. YouTube, Spotify and Apple Music all normalise
    /// playback to approximately this level, so a file mastered here is what listeners expect.
    /// </summary>
    public const double TargetLufs = -14.0;

    /// <summary>
    /// True-peak ceiling. -1.5 dBTP leaves headroom so lossy re-encoding downstream (which can
    /// overshoot by a fraction of a dB) still cannot clip.
    /// </summary>
    public const double PeakCeilingDbtp = -1.5;

    /// <summary>
    /// AUDIO_03 — where BACKGROUND MUSIC should sit, in LUFS.
    ///
    /// ⚠️ THIS EXISTS BECAUSE MUSIC WAS NEVER MEASURED AT ALL. Gameplay is normalised to
    /// <see cref="TargetLufs"/>, but a music file went in exactly as its label mastered it — and
    /// commercial masters are LOUD, typically -8 to -10 LUFS. So with both sliders at 1.0, which
    /// the user reasonably reads as "equal", the music actually arrived roughly 5 dB HOTTER than
    /// the gameplay before ducking or carving got a chance to act. Every ducking parameter in the
    /// chain was then being tuned to claw back a head start it should never have had.
    ///
    /// -20 LUFS puts music about 6 LU under the -14 game bus, which is the usual bed level for
    /// music sitting beneath speech or action. The user's music slider is applied ON TOP of this,
    /// so 1.0 now genuinely means "as loud as a background bed should be" instead of "however
    /// loud this particular record happens to be".
    /// </summary>
    public const double MusicBedLufs = -20.0;

    /// <summary>
    /// Safety rail for the music match. A very quiet or badly-tagged file could otherwise ask for
    /// a huge boost that turns its noise floor into a hiss bed, so the correction is clamped.
    /// </summary>
    public const double MaxMusicGainDb = 12.0;
    public const double MinMusicGainDb = -24.0;

    /// <summary>
    /// How far from <see cref="TargetLufs"/> a file may sit before the user is warned.
    ///
    /// Chosen deliberately: at +/-1 LU almost every gameplay capture trips the warning and the
    /// dialog becomes noise the user dismisses reflexively; at +/-6 LU a genuinely quiet -19 LUFS
    /// clip passes unflagged. +/-3 LU flags what a listener would actually notice.
    /// </summary>
    public const double ToleranceLu = 3.0;

    /// <summary>
    /// Crest factor above which peaks count as "harsh". Speech and music normally land around
    /// 10-14 LU above their own average; beyond 15 LU there is a genuine bang in the file.
    /// </summary>
    public const double CrestWarnLu = 15.0;

    /// <summary>
    /// Measures <paramref name="inputPath"/> end to end.
    ///
    /// Integrated loudness is only meaningful over the WHOLE file — a clip that is silent for a
    /// minute and then deafening averages out to something neither half resembles — so this
    /// decodes everything rather than sampling. Video, subtitles and data streams are dropped
    /// (<c>-vn -sn -dn</c>) so only the audio is touched, which keeps it fast enough to run in
    /// the background while the user is already working.
    ///
    /// Returns null when the file has no audio, when FFmpeg fails, or when cancelled. A null
    /// return must never block the user — it simply means "we could not tell", and the caller
    /// stays silent rather than guessing.
    /// </summary>
    public static async Task<LoudnessReading?> MeasureAsync(
        string ffmpegPath,
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath)) return null;

        Process? process = null;
        try
        {
            var args = new List<string>
            {
                "-y", "-hide_banner", "-nostdin",
                "-i", inputPath,
                "-af", $"loudnorm=I={TargetLufs.ToString(CultureInfo.InvariantCulture)}" +
                       $":TP={PeakCeilingDbtp.ToString(CultureInfo.InvariantCulture)}" +
                       ":LRA=11:print_format=json",
                "-vn", "-sn", "-dn",
                "-f", "null", "-"
            };

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
                $"({reading.Verdict}, peak {reading.PeakAboveAverageLu:F1} LU above average).");

            return reading;
        }
        catch (OperationCanceledException)
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
            throw;
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("LoudnessProbe", $"Loudness measurement threw: {ex.Message}");
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
            if (!TryReadDouble(node, "input_thresh", out double thresh)) return null;
            if (!TryReadDouble(node, "target_offset", out double offset)) offset = 0.0;

            if (double.IsInfinity(i) || double.IsNaN(i)) return null;

            return new LoudnessReading(i, tp, lra, thresh, offset);
        }
        catch
        {
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
        catch
        {
            return false;
        }
    }
}
