// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// MUSICPAD_01 — THE MUSIC LANDS WHERE THE PINK NOTES SAY, EVEN WITH FADES ON.
///
/// The Main App places the music in OUTPUT seconds measured from MARK START (OutputTimeline with the
/// trim start as origin — the same clock the preview plays). But the exported body does NOT start at
/// MARK START: with fades on, ProcessWorker extracts up to 1 s of footage BEFORE it (the fade-in pad,
/// <c>padStartHumanSec</c>) and the music is mixed into that padded body. Every track therefore
/// started — and ended — exactly one pad EARLY in the file compared to the preview (measured: onset
/// 1.923 s instead of 2.918 s on a 3–9 s trim at 1.1x). Voice-over takes were already measured from
/// the padded start; music was not.
///
/// <see cref="Align"/> moves every track by the lead pad, so the song meets the video on exactly the
/// frames the preview showed. Two edge refinements keep "music from the beginning / to the end" full:
///   • LEAD — the first track starts on MARK START and the lead fade is on: it pre-rolls into the
///     fade-in pad from earlier in the song (as far as the song has room before its start point),
///     so the song is still in sync with the video AND fills the fade-in.
///   • TAIL — the bed reaches MARK END and the tail fade is on: the last track runs on through the
///     fade-out pad (its own fade-out then ends with the picture).
/// Pure; unit-tested.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class MusicPadAlignment
{
    private const double EdgeToleranceSec = 0.05;

    /// <param name="tracks">Tracks as the UI built them: delays in output seconds from MARK START.</param>
    /// <param name="padStartSec">Output seconds of the fade-in pad in front of MARK START (0 = none).</param>
    /// <param name="padEndSec">Output seconds of the fade-out pad after MARK END (0 = none).</param>
    /// <param name="bodySec">Output seconds of the padded body (pads included).</param>
    public static List<MusicTrack> Align(IReadOnlyList<MusicTrack> tracks, double padStartSec, double padEndSec,
        double bodySec, bool leadFadeIn, bool tailFadeOut)
    {
        var result = new List<MusicTrack>(tracks);
        if (result.Count == 0) return result;
        padStartSec = Math.Max(0, padStartSec);
        padEndSec = Math.Max(0, padEndSec);
        if (padStartSec <= 0 && padEndSec <= 0) return result;

        double firstDelay = result[0].TimelineStartDelay;
        double preRoll = 0;
        if (leadFadeIn && padStartSec > 0 && firstDelay <= EdgeToleranceSec)
            preRoll = Math.Min(padStartSec, Math.Max(0, result[0].Offset));

        // Everything moves by the lead pad; a pre-roll starts the first track earlier by the same
        // amount it gains in length, so every later track (placed after the ones before it) stays put.
        for (int i = 0; i < result.Count; i++)
            result[i] = result[i] with { TimelineStartDelay = result[i].TimelineStartDelay + padStartSec - preRoll };
        if (preRoll > 0)
            result[0] = result[0] with { Offset = result[0].Offset - preRoll, Duration = result[0].Duration + preRoll };

        if (tailFadeOut && padEndSec > 0)
        {
            double bedEnd = firstDelay + Sum(tracks);   // where the bed ends on the MARK START clock
            double trimmedBody = bodySec - padStartSec - padEndSec;
            if (bedEnd >= trimmedBody - EdgeToleranceSec)
                result[^1] = result[^1] with { Duration = result[^1].Duration + padEndSec };
        }
        return result;
    }

    private static double Sum(IReadOnlyList<MusicTrack> tracks)
    {
        double s = 0;
        foreach (var t in tracks) s += t.Duration;
        return s;
    }
}
