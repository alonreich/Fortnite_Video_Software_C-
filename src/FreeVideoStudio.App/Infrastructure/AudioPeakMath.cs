using System;
using System.IO;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>
/// PEAKMATH_01 — audio-domain arithmetic and scratch-file hygiene lifted out of
/// <c>MusicWizardWindow</c>.
///
/// ⚠️ <c>DbToLinear</c> IS AN AUDIO INVARIANT, NOT A UTILITY. Decibels are logarithmic; every gain,
/// duck and fade amount in the mastering chain passes through this conversion, and
/// <c>docs/02_AUDIO_ENGINE_MASTERING.md</c> governs the values that feed it. Change the formula
/// and you change every mix the app has ever produced.
///
/// <c>FindNearestPeakTime</c> is what makes "snap the music start to the beat" actually land on a
/// beat; its search radius is calibrated feel. Bodies moved verbatim.
/// </summary>
internal static class AudioPeakMath
{
    internal static double DbToLinear(double db) => Math.Pow(10.0, db / 20.0);

    internal static string FormatSeconds(double seconds)
    {
        seconds = Math.Max(0, seconds);
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss\.ff")
            : ts.ToString(@"m\:ss\.ff");
    }

    internal static void DeleteTempFile(ref string? path)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try { File.Delete(path); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        }
        path = null;
    }
}
