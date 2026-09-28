// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>What the Merger preview does over one stretch of the merged clock.</summary>
public enum PreviewStepKind
{
    /// <summary>Play footage at <see cref="PreviewStep.Speed"/> (absolute mpv speed: segment speed, else the base speed).</summary>
    Play,
    /// <summary>Deleted footage: jump to <see cref="PreviewStep.MergedEndSec"/>.</summary>
    Cut,
    /// <summary>Hold the frame at <see cref="PreviewStep.MergedStartSec"/> for <see cref="PreviewStep.HoldSec"/> seconds.</summary>
    Freeze,
    /// <summary>Show a meme for <see cref="PreviewStep.HoldSec"/> seconds, then carry on at <see cref="PreviewStep.MergedStartSec"/>.</summary>
    Meme,
}

/// <summary>One step of the preview schedule. Holds (freeze/meme) have MergedEndSec == MergedStartSec.</summary>
public sealed record PreviewStep(
    PreviewStepKind Kind,
    int ClipIndex,
    double MergedStartSec,
    double MergedEndSec,
    double Speed,
    double HoldSec,
    double OutputStartSec,
    EdlMeme? Meme = null)
{
    /// <summary>Seconds of the finished body this step occupies.</summary>
    public double OutputLengthSec => Kind switch
    {
        PreviewStepKind.Play => (MergedEndSec - MergedStartSec) / Speed,
        PreviewStepKind.Cut => 0,
        _ => HoldSec,
    };

    public bool IsHold => Kind is PreviewStepKind.Freeze or PreviewStepKind.Meme;
}

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// MERGEPREVIEW_01 — THE MERGER PREVIEW PLAYS THE EXPORT'S SCHEDULE (Video-Merger-Migration.md P8.1).
///
/// The preview plays the merge as ONE mpv EDL whose clock is the MERGED clock (P4.2), so the effects
/// must be applied live, the way the Main App previews them: mpv <c>speed</c> per stretch, a timed
/// pause for a freeze, a jump over a cut, a meme cut-away for a meme. This plan is that schedule, read
/// straight from the chunk list of every clip's <see cref="OutputTimeline"/> — the SAME chunks the
/// export's graph and <see cref="CompositeTimeline"/> use — so the preview cannot drift from the file.
///
/// Clock mapping: chunk seconds are clip-relative (0 = the clip's kept-in point, length = the clip's
/// merged frames / 60), so merged seconds = clip start + chunk seconds. <see cref="OutputSecAt"/> is
/// the body output second (the music preview's clock), equal to
/// <see cref="CompositeTimeline.MergedSecToBodyOutputSec"/> (unit-tested).
/// Pure: no UI, no mpv.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class MergerPreviewPlan
{
    /// <summary>Tolerance for "at the same instant", well under one 60 fps frame.</summary>
    private const double Eps = 0.0005;

    public IReadOnlyList<PreviewStep> Steps { get; }
    public double TotalMergedSec { get; }
    public double TotalOutputSec { get; }

    /// <summary>True when the plan does anything other than play everything at one speed.</summary>
    public bool HasEffects { get; }

    /// <summary>
    /// P8.2 — every meme as the Main App's <see cref="MemePlacement"/> on the MERGED clock (origin 0),
    /// for <c>MemePreviewDirector</c>, which plays the cut-away when forward playback crosses it.
    /// </summary>
    public IReadOnlyList<MemePlacement> MemePlacements { get; }

    public static readonly MergerPreviewPlan Empty = new(new List<PreviewStep>(), 0, false);

    private MergerPreviewPlan(List<PreviewStep> steps, double totalMerged, bool hasEffects)
    {
        Steps = steps;
        TotalMergedSec = totalMerged;
        TotalOutputSec = steps.Count == 0 ? 0 : steps[^1].OutputStartSec + steps[^1].OutputLengthSec;
        HasEffects = hasEffects;
        var memes = new List<MemePlacement>();
        foreach (var st in steps)
            if (st.Kind == PreviewStepKind.Meme && st.Meme is { } m && st.HoldSec > 0.001)
                memes.Add(new MemePlacement(m.FilePath, st.MergedStartSec, st.HoldSec, m.Id));
        MemePlacements = memes;
    }

    public static MergerPreviewPlan Build(CompositeTimeline tl)
    {
        var steps = new List<PreviewStep>();
        bool effects = false;
        foreach (var c in tl.Clips)
        {
            if (c.MergedFrames <= 0) continue;
            double clipStart = c.MergedStartFrame / (double)CompositeTimeline.MergeFps;
            double clipEnd = c.MergedEndFrame / (double)CompositeTimeline.MergeFps;
            double outCursor = c.OutputStartSec;
            var memes = tl.Edl.Clips[c.Index].Effects.Memes;
            if (!tl.Edl.Clips[c.Index].Effects.IsEmpty) effects = true;

            foreach (var ch in c.Output.Chunks)
            {
                double a = Math.Clamp(clipStart + ch.SourceStartSec, clipStart, clipEnd);
                double b = Math.Clamp(clipStart + ch.SourceEndSec, clipStart, clipEnd);
                PreviewStep step;
                if (ch.IsCut)
                    step = new PreviewStep(PreviewStepKind.Cut, c.Index, a, b, 1, 0, outCursor);
                else if (ch.IsInsertion)
                    step = new PreviewStep(PreviewStepKind.Meme, c.Index, a, a, 0, ch.FreezeHoldSec, outCursor, FindMeme(memes, ch.InsertionId));
                else if (ch.HoldsSource)
                    step = new PreviewStep(PreviewStepKind.Freeze, c.Index, a, a, 0, ch.FreezeHoldSec, outCursor);
                else
                    step = new PreviewStep(PreviewStepKind.Play, c.Index, a, b, ch.Speed, 0, outCursor);
                if (step.Kind == PreviewStepKind.Play && b <= a) continue;
                steps.Add(step);
                outCursor += step.OutputLengthSec;
            }
        }
        return new MergerPreviewPlan(steps, tl.TotalMergedFrames / (double)CompositeTimeline.MergeFps, effects);
    }

    private static EdlMeme? FindMeme(IReadOnlyList<EdlMeme> memes, string? id)
    {
        foreach (var m in memes) if (m.Id == id) return m;
        return null;
    }

    /// <summary>The play or cut stretch that owns <paramref name="mergedSec"/> (a boundary belongs to the stretch that starts there), or null.</summary>
    public PreviewStep? StretchAt(double mergedSec)
    {
        PreviewStep? last = null;
        foreach (var s in Steps)
        {
            if (s.IsHold) continue;
            if (mergedSec < s.MergedStartSec - Eps) break;
            last = s;
            if (mergedSec < s.MergedEndSec - Eps) return s;
        }
        return last is { } l && mergedSec >= TotalMergedSec - Eps ? l : null;
    }

    /// <summary>mpv speed at <paramref name="mergedSec"/> (the base speed when nothing else applies).</summary>
    public double SpeedAt(double mergedSec, double fallback)
        => StretchAt(mergedSec) is { Kind: PreviewStepKind.Play } s ? s.Speed : fallback;

    /// <summary>Where playback resumes when <paramref name="mergedSec"/> is inside deleted footage; null when it is not.</summary>
    public double? CutResumeAt(double mergedSec)
    {
        foreach (var s in Steps)
        {
            if (s.Kind != PreviewStepKind.Cut) continue;
            if (mergedSec >= s.MergedStartSec - Eps && mergedSec < s.MergedEndSec - Eps)
            {
                double resume = s.MergedEndSec;
                // Back-to-back cuts across a clip boundary: keep jumping.
                var next = CutResumeAt(resume + Eps * 2);
                return next ?? resume;
            }
        }
        return null;
    }

    /// <summary>
    /// The first hold (freeze or meme) placed in [<paramref name="fromSec"/>, <paramref name="toSec"/>]
    /// on the merged clock, in schedule order, whose index is greater than <paramref name="afterIndex"/>.
    /// Returns its index in <see cref="Steps"/>, or -1. <paramref name="includeMemes"/> false = freezes
    /// only (the preview hands memes to <c>MemePreviewDirector</c>).
    /// </summary>
    public int NextHold(double fromSec, double toSec, int afterIndex, bool includeMemes = true)
    {
        for (int i = Math.Max(0, afterIndex + 1); i < Steps.Count; i++)
        {
            var s = Steps[i];
            if (!s.IsHold || (!includeMemes && s.Kind == PreviewStepKind.Meme)) continue;
            if (s.MergedStartSec > toSec + Eps) return -1;
            if (s.MergedStartSec >= fromSec - Eps) return i;
        }
        return -1;
    }

    /// <summary>Index of the last step that starts at or before <paramref name="mergedSec"/> (for re-arming holds after a seek), or -1.</summary>
    public int LastStepIndexBefore(double mergedSec)
    {
        int idx = -1;
        for (int i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].MergedStartSec < mergedSec - Eps) idx = i;
            else break;
        }
        return idx;
    }

    /// <summary>
    /// Body output second at <paramref name="mergedSec"/>, BEFORE any hold that starts exactly there
    /// (a freeze at t is still ahead of a playhead that has just reached t). Inside a cut: the join.
    /// </summary>
    public double OutputSecAt(double mergedSec)
    {
        double t = Math.Clamp(mergedSec, 0, TotalMergedSec);
        double output = 0;
        foreach (var s in Steps)
        {
            if (s.MergedStartSec > t + Eps) break;
            if (s.IsHold)
            {
                if (s.MergedStartSec < t - Eps) output = s.OutputStartSec + s.HoldSec;
                continue;
            }
            if (s.Kind == PreviewStepKind.Cut)
            {
                output = s.OutputStartSec;
                continue;
            }
            output = s.OutputStartSec + (Math.Min(t, s.MergedEndSec) - s.MergedStartSec) / s.Speed;
        }
        return output;
    }
}
