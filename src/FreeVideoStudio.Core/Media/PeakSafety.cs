// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Globalization;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// PEAKSAFE_01 — the two stages that protect a viewer's ears, shared by the Main App export, the
/// Video Merger export and the live preview so all three apply the SAME filters.
///
/// <para><b>1. THE PEAK TAMER (user switch: Settings › "Sudden loud moments").</b> A fast peak
/// compressor on the GAMEPLAY bus whose threshold sits <see cref="TamerHeadroomLu"/> above the
/// clip's own measured integrated loudness. The body of the gameplay never reaches it; only a
/// sudden burst far above the clip's average (an explosion, a scream) is pulled down. It runs on
/// the gameplay alone — never the summed mix — because a threshold derived from the GAMEPLAY's
/// loudness would otherwise squash a music bed that is simply mixed louder than the game.</para>
///
/// <para><b>2. THE SAFETY LIMITER (always on, no switch).</b> A true-peak brick wall on the final
/// mix at <see cref="SafetyCeilingDbtp"/>. It is 4x oversampled so it catches inter-sample peaks
/// (plain <c>alimiter</c> only sees sample peaks), and it carries <c>level=disabled</c>.</para>
///
/// <para>⚠️ <c>level=disabled</c> IS LOAD-BEARING. <c>alimiter</c>'s <c>level</c> (auto-level)
/// defaults to ON and rescales the output by 1/limit after limiting: the old
/// <c>alimiter=limit=-1.5dB</c> chain measured +1.5 dB LOUDER on everything with peaks back at
/// 0.0 dBFS (+1.0 dBTP). A limiter must never raise the level.</para>
/// </summary>
public static class PeakSafety
{
    /// <summary>How far above the measured integrated loudness the tamer starts to act, in LU.</summary>
    public const double TamerHeadroomLu = 9.0;

    /// <summary>The tamer threshold is clamped into this window (dBFS).</summary>
    public const double TamerMinThresholdDb = -40.0;
    public const double TamerMaxThresholdDb = -6.0;

    public const double TamerRatio = 6.0;
    public const double TamerAttackMs = 1.0;
    public const double TamerReleaseMs = 150.0;

    /// <summary>The always-on true-peak ceiling of every export, in dBTP.</summary>
    public const double SafetyCeilingDbtp = -2.0;

    /// <summary>
    /// The sample-peak limit handed to the oversampled <c>alimiter</c>. Measured: -2.3 dB at 4x
    /// oversampling lands true peaks at -2.1 dBTP on hot pink noise + an 8 kHz tone, i.e. at or
    /// under <see cref="SafetyCeilingDbtp"/>; -2.0 dB landed at -1.8 dBTP.
    /// </summary>
    public const double SafetyLimiterLimitDb = -2.3;

    private const int OversampleRate = 192000;

    /// <summary>The tamer's threshold for a clip measured at <paramref name="integratedLufs"/>.</summary>
    public static double TamerThresholdDb(double integratedLufs) =>
        Math.Clamp(integratedLufs + TamerHeadroomLu, TamerMinThresholdDb, TamerMaxThresholdDb);

    /// <summary>
    /// The tamer filter (no leading comma, no labels), or null when the loudness is unknown —
    /// a threshold cannot be placed relative to a level nobody measured.
    /// </summary>
    public static string? TamerFilter(double? integratedLufs)
    {
        if (integratedLufs is not double i || !double.IsFinite(i) || i < -69.0) return null;
        var ci = CultureInfo.InvariantCulture;
        return $"acompressor=threshold={TamerThresholdDb(i).ToString("F2", ci)}dB" +
               $":ratio={TamerRatio.ToString(ci)}" +
               $":attack={TamerAttackMs.ToString(ci)}" +
               $":release={TamerReleaseMs.ToString(ci)}" +
               ":knee=2:makeup=1:detection=peak";
    }

    /// <summary>The always-on true-peak safety limiter (no leading comma, no labels).</summary>
    public static string SafetyLimiterFilter(int outputSampleRate = 48000)
    {
        var ci = CultureInfo.InvariantCulture;
        return $"aresample={OversampleRate}," +
               $"alimiter=limit={SafetyLimiterLimitDb.ToString("F1", ci)}dB:attack=1:release=50" +
               ":level_in=1:level_out=1:level=disabled," +
               $"aresample={outputSampleRate}";
    }
}
