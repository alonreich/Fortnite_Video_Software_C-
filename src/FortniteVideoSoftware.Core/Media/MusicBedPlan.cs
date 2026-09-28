// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// One contiguous piece of the music bed: <see cref="DurationSec"/> seconds of <see cref="Path"/>,
/// starting <see cref="FileOffsetSec"/> into the file, laid at <see cref="OutputStartSec"/> in the
/// FINISHED video.
/// </summary>
public readonly record struct MusicBedSegment(string Path, double FileOffsetSec, double DurationSec, double OutputStartSec)
{
    public double OutputEndSec => OutputStartSec + DurationSec;
}

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// MUSICSYNC_01 — ONE DEFINITION OF "WHICH SECOND OF WHICH SONG PLAYS AT OUTPUT TIME T".
///
/// The export has always laid the music bed in OUTPUT time (CUTS_02). The start and end markers are
/// mapped through OutputTimeline, the first track starts at the wizard's offset, following tracks
/// start at 0, and LOOP_01 repeats the list until the video is covered. The live preview did none of
/// that. It computed the song position from RAW SOURCE seconds and played one file, so it
/// disagreed with the export by the base speed (1.1x by default), by every cut and freeze before the
/// playhead, and completely after the first track ended. A beat lined up by ear in the preview then
/// missed in the export.
///
/// Both now read this plan. <see cref="Build"/> is the export's former inline loop, moved verbatim
/// in behaviour. <see cref="Locate"/> answers the preview's question against the same segments.
///
/// ⚠️ MUSIC SPEED IS ALWAYS 1.0x. The music bed is never time-stretched in the export and must never
/// be in the preview. Only the VIDEO changes speed. Preview sync is corrected by SEEKING the music,
/// never by nudging its playback rate.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class MusicBedPlan
{
    public const int LoopGuard = 500;

    /// <param name="paths">Music files, in play order.</param>
    /// <param name="knownDurationsSec">Full length of each file; &lt;= 0 means unknown (the file then
    /// covers whatever remains, which is what the export has always assumed).</param>
    /// <param name="firstFileOffsetSec">Where playback starts inside the FIRST file only.</param>
    /// <param name="bedDurationSec">How long the bed runs in the finished video.</param>
    /// <param name="loop">LOOP_01: after the list is exhausted, repeat every file from 0.</param>
    /// <param name="startDelaySec">Output time at which the bed begins.</param>
    public static IReadOnlyList<MusicBedSegment> Build(
        IReadOnlyList<string> paths,
        IReadOnlyList<double> knownDurationsSec,
        double firstFileOffsetSec,
        double bedDurationSec,
        bool loop,
        double startDelaySec)
    {
        var plan = new List<MusicBedSegment>();
        if (paths == null || paths.Count == 0) return plan;

        double remaining = bedDurationSec;
        double cursor = startDelaySec;

        for (int i = 0; i < paths.Count; i++)
        {
            double offset = i == 0 ? firstFileOffsetSec : 0.0;
            double known = i < knownDurationsSec.Count ? knownDurationsSec[i] : 0.0;

            double available = known > 0 ? Math.Max(0.0, known - offset) : remaining;
            double take = Math.Min(remaining, available);
            if (take <= 0.01) continue;

            plan.Add(new MusicBedSegment(paths[i], offset, take, cursor));
            cursor += take;
            remaining -= take;
            if (remaining <= 0.01) break;
        }

        if (loop && remaining > 0.01)
        {
            int guard = 0;
            while (remaining > 0.01 && guard++ < LoopGuard)
            {
                bool addedAnything = false;
                for (int i = 0; i < paths.Count && remaining > 0.01; i++)
                {
                    double full = i < knownDurationsSec.Count ? knownDurationsSec[i] : 0.0;
                    if (full <= 0.01) continue;

                    double take = Math.Min(remaining, full);
                    if (take <= 0.01) continue;

                    plan.Add(new MusicBedSegment(paths[i], 0.0, take, cursor));
                    cursor += take;
                    remaining -= take;
                    addedAnything = true;
                }
                if (!addedAnything) break;   // every track is unreadable or zero-length
            }
        }

        return plan;
    }

    /// <summary>
    /// The file and the position inside it that the finished video plays at <paramref name="outputSec"/>,
    /// or <see langword="null"/> when the bed is silent there (before its start, after its end).
    /// </summary>
    public static (string Path, double PositionSec, int SegmentIndex)? Locate(IReadOnlyList<MusicBedSegment> plan, double outputSec)
    {
        if (plan == null || !double.IsFinite(outputSec)) return null;
        for (int i = 0; i < plan.Count; i++)
        {
            MusicBedSegment s = plan[i];
            if (outputSec >= s.OutputStartSec && outputSec < s.OutputEndSec)
                return (s.Path, s.FileOffsetSec + (outputSec - s.OutputStartSec), i);
        }
        return null;
    }
}
