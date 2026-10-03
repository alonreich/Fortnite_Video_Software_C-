// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.Media;

/// <summary>One clip's place in the <see cref="CompositeTimeline"/>.</summary>
public sealed record CompositeClip(
    int Index,
    Guid ClipId,
    string Path,
    long KeepInUs,
    long KeepOutUs,
    long RemovedIntroUs,
    long MergedStartFrame,
    long MergedFrames,
    double OutputStartSec,
    OutputTimeline Output)
{
    public long MergedEndFrame => MergedStartFrame + MergedFrames;

    /// <summary>Output seconds of this clip BEFORE the global base speed (speed/freeze/memes applied).</summary>
    public double OutputLengthSec => Output.TotalOutputSeconds;
}

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// COMPOSITE_01 — THE VIDEO MERGER'S ONE TIME MAPPER (Video-Merger-Migration.md P2.3).
///
/// Three clocks, never mixed:
///   1. SOURCE µs   — inside one clip's file (<see cref="EdlAnchor"/>). Real frame pts (FRAMESNAP_01).
///   2. MERGED frame — kept content of all clips end to end at 1.0x, integer frames at
///                     <see cref="MergeFps"/>. Integer, so 200 clips add up with zero drift. Each
///                     clip is rounded on its own, which is exactly what the export's per-clip
///                     fps=60 normalisation produces.
///   3. OUTPUT sec  — the finished file: each clip's own speed/freeze/memes/cuts (the Main App's
///                     <see cref="OutputTimeline"/>, so both apps share one set of time maths) with
///                     the global base speed applied per clip to footage OUTSIDE speed segments (D19,
///                     the Main App editor's rule), then the synthetic thumbnail intro in front.
///
/// Intro removal is DERIVED here, never stored per clip (user decision D13): clip 1 loses its intro
/// only when a custom thumbnail replaces it; clips 2..N lose theirs while the scraper is on.
///
/// Every consumer (preview, music, export, undo remap) reads this object, so they cannot disagree.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class CompositeTimeline
{
    /// <summary>The merged clock's rate: the export's CFR.</summary>
    public const int MergeFps = 60;

    /// <summary>A kept window shorter than this after intro removal means the removal is refused.</summary>
    public const long MinContentUs = 50_000;

    public MergeEdl Edl { get; }
    public IReadOnlyList<CompositeClip> Clips { get; }
    public long TotalMergedFrames { get; }
    public double BaseSpeed { get; }

    /// <summary>Output seconds of the still intro prepended for a custom thumbnail (0 when none).</summary>
    public double SyntheticIntroSec { get; }

    /// <summary>Sum of the clips' output lengths (base speed, segments, freezes, memes and cuts applied).</summary>
    public double ClipsOutputSec { get; }

    /// <summary>Length of the finished video.</summary>
    public double TotalOutputSec => SyntheticIntroSec + ClipsOutputSec;

    private CompositeTimeline(MergeEdl edl, List<CompositeClip> clips, double baseSpeed, double introSec)
    {
        Edl = edl;
        Clips = clips;
        BaseSpeed = baseSpeed;
        SyntheticIntroSec = introSec;
        TotalMergedFrames = clips.Count == 0 ? 0 : clips[^1].MergedEndFrame;
        ClipsOutputSec = clips.Count == 0 ? 0 : clips[^1].OutputStartSec + clips[^1].OutputLengthSec;
    }

    public static CompositeTimeline Build(MergeEdl edl)
    {
        var clips = new List<CompositeClip>(edl.Clips.Count);
        double baseSpeed = edl.BaseSpeed > 0.001 && double.IsFinite(edl.BaseSpeed) ? edl.BaseSpeed : 1.0;
        long frameCursor = 0;
        double outCursor = 0;
        for (int i = 0; i < edl.Clips.Count; i++)
        {
            var c = edl.Clips[i];
            var (keepIn, keepOut, removed) = KeepWindow(edl, i);
            long frames = UsToFrames(keepOut - keepIn);
            var output = BuildClipOutput(c, keepIn, keepOut, frames, baseSpeed);
            var cc = new CompositeClip(i, c.ClipId, c.Path, keepIn, keepOut, removed, frameCursor, frames, outCursor, output);
            clips.Add(cc);
            frameCursor += frames;
            outCursor += cc.OutputLengthSec;
        }
        double intro = edl.Thumbnail != null ? IntroTag.StandardIntroSec : 0;
        return new CompositeTimeline(edl, clips, baseSpeed, intro);
    }

    /// <summary>The kept source window of clip <paramref name="index"/> after the user window and intro removal.</summary>
    public static (long KeepInUs, long KeepOutUs, long RemovedIntroUs) KeepWindow(MergeEdl edl, int index)
    {
        var c = edl.Clips[index];
        long dur = Math.Max(0, c.DurationUs);
        long start = Math.Clamp(c.InUs, 0, dur);
        long end = c.OutUs > 0 ? Math.Clamp(c.OutUs, 0, dur) : dur;
        if (end - start < MinContentUs) { start = 0; end = dur; }

        long cut = IntroCutUs(c);
        bool wantsRemoval = cut > 0 && (index == 0 ? edl.Thumbnail != null : edl.ScraperEnabled);
        long removed = 0;
        if (wantsRemoval && start < cut && end - cut >= MinContentUs)
        {
            removed = cut - start;
            start = cut;
        }
        return (start, end, removed);
    }

    /// <summary>Where the clip's content starts after its tagged intro: the probed pts, else the tag's seconds.</summary>
    public static long IntroCutUs(EdlClip c)
    {
        if (c.IntroCutUs > 0) return c.IntroCutUs;
        if (c.Timing is ExportTiming t && t.IntroFrames > 0) return (long)Math.Round(t.IntroSec * 1_000_000.0);
        return 0;
    }

    /// <summary>
    /// D19 — the Main App editor's semantics: a speed segment's speed is ABSOLUTE; <paramref name="baseSpeed"/>
    /// applies only to footage outside segments; freezes and memes are real-time holds. D18 — cuts are holes.
    /// </summary>
    private static OutputTimeline BuildClipOutput(EdlClip c, long keepIn, long keepOut, long frames, double baseSpeed)
    {
        // The clip's length on the output clock is its ROUNDED frame count at MergeFps, because that
        // is what the export's per-clip fps=60 normalisation actually emits. Using the raw µs length
        // drifts by up to half a frame per clip (44 ms over 200 clips in T2.3a).
        double keepSec = frames / (double)MergeFps;
        var fx = c.Effects;

        var segments = new List<SpeedSegment>(fx.Speed.Count + fx.Freezes.Count);
        foreach (var s in fx.Speed) segments.Add(new SpeedSegment(s.StartUs / 1000.0, s.EndUs / 1000.0, s.Speed));
        foreach (var f in fx.Freezes) segments.Add(new SpeedSegment(f.AtUs / 1000.0, f.AtUs / 1000.0 + f.DurationSec * 1000.0, 0));

        var insertions = new List<OutputTimeline.Insertion>(fx.Memes.Count);
        foreach (var m in fx.Memes)
        {
            // MEMEMODE_01 — a corner overlay plays over the clip and adds zero output seconds.
            if (m.IsCornerOverlay) continue;
            insertions.Add(new OutputTimeline.Insertion(MemeAtRelSec(c, m, keepIn, keepOut, keepSec), m.DurationSec, m.Id));
        }

        var cuts = new List<OutputTimeline.Cut>(fx.Cuts.Count);
        foreach (var k in fx.Cuts) cuts.Add(new OutputTimeline.Cut((k.StartUs - keepIn) / 1_000_000.0, (k.EndUs - keepIn) / 1_000_000.0));

        return OutputTimeline.Create(keepSec * 1000.0, segments, baseSpeed, keepIn / 1000.0, insertions, cuts);
    }

    /// <summary>
    /// D4 — where a meme is inserted, in seconds from the clip's kept-in point (clamped to the clip):
    /// AtStart = 0 (the clip and its fade-in follow the meme), AtEnd = where the fade-out begins,
    /// Mid = its own position. Shared by the timeline and the export (MERGEGRAPH_01) so they agree.
    /// </summary>
    public static double MemeAtRelSec(EdlClip c, EdlMeme m, long keepIn, long keepOut, double keepSec)
    {
        double at = m.Placement switch
        {
            EdlMemePlacement.AtStart => 0,
            EdlMemePlacement.AtEnd => FadeOutStartRelSec(c, keepOut, keepSec),
            _ => (m.AtUs - keepIn) / 1_000_000.0,
        };
        return Math.Clamp(at, 0, keepSec);
    }

    /// <summary>
    /// D4 — an AtEnd meme goes where the clip's fade-out begins. That is only known when the kept
    /// window still reaches the end of the file and the file's timing tag carries the fade-out;
    /// otherwise the meme goes at the clip's end.
    /// </summary>
    private static double FadeOutStartRelSec(EdlClip c, long keepOut, double keepSec)
    {
        bool reachesEnd = c.DurationUs > 0 && keepOut >= c.DurationUs - 1000;
        if (reachesEnd && c.Timing is ExportTiming t && t.FadeOutFrames is int fo && fo > 0 && t.Fps > 0)
            return Math.Max(0, keepSec - fo / t.Fps);
        return keepSec;
    }

    public static long UsToFrames(long us) => us <= 0 ? 0 : (us * MergeFps + 500_000) / 1_000_000;
    public static long FramesToUs(long frames) => frames <= 0 ? 0 : (frames * 1_000_000 + MergeFps / 2) / MergeFps;

    /// <summary>Index of the clip that plays merged frame <paramref name="frame"/> (the last clip for the very end), or -1.</summary>
    public int ClipIndexAt(long frame)
    {
        if (Clips.Count == 0) return -1;
        long f = Math.Clamp(frame, 0, TotalMergedFrames);
        if (f == TotalMergedFrames)
        {
            // The very end belongs to the last clip that has content.
            int last = Clips.Count - 1;
            while (last > 0 && Clips[last].MergedFrames == 0) last--;
            return last;
        }
        // Largest index whose start is <= f. Starts never decrease, and a zero-length clip shares its
        // start with the next clip, so for f < total this always lands on a clip that has content at f.
        int lo = 0, hi = Clips.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Clips[mid].MergedStartFrame <= f) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>The (clip, source µs) shown at merged frame <paramref name="frame"/>. Null when there are no clips.</summary>
    public EdlAnchor? Locate(long frame)
    {
        int i = ClipIndexAt(frame);
        if (i < 0) return null;
        var c = Clips[i];
        long local = Math.Clamp(Math.Clamp(frame, 0, TotalMergedFrames) - c.MergedStartFrame, 0, c.MergedFrames);
        return new EdlAnchor(c.ClipId, Math.Min(c.KeepOutUs, c.KeepInUs + FramesToUs(local)));
    }

    /// <summary>Merged frame of an anchor. A position inside a removed intro lands on the first kept frame. Null when the clip is gone.</summary>
    public long? ToMerged(EdlAnchor anchor)
    {
        int i = Edl.IndexOf(anchor.ClipId);
        if (i < 0) return null;
        var c = Clips[i];
        long rel = Math.Clamp(anchor.SourceUs, c.KeepInUs, c.KeepOutUs) - c.KeepInUs;
        return c.MergedStartFrame + Math.Clamp(UsToFrames(rel), 0, c.MergedFrames);
    }

    /// <summary>Output seconds (finished file) of an anchor. Null when the clip is gone.</summary>
    public double? AnchorToOutputSec(EdlAnchor anchor)
    {
        int i = Edl.IndexOf(anchor.ClipId);
        if (i < 0) return null;
        var c = Clips[i];
        long src = Math.Clamp(anchor.SourceUs, c.KeepInUs, c.KeepOutUs);
        double clipOut = c.Output.SourceToOutput(src / 1_000_000.0);
        return SyntheticIntroSec + c.OutputStartSec + clipOut;
    }

    /// <summary>
    /// MUSICMAP_01 (P7.2, D8) — a merged-clock second (what the Music Wizard and the Merger preview
    /// place music on) → seconds of the finished BODY, i.e. after speed/freeze/meme/cut effects and
    /// the base speed but BEFORE the synthetic thumbnail intro (the export prepends that after the
    /// music mix). The music is never stretched; only where it starts/ends moves with the video.
    /// </summary>
    public double MergedSecToBodyOutputSec(double mergedSec)
    {
        long frame = (long)Math.Round(Math.Max(0, mergedSec) * MergeFps);
        return Math.Max(0, MergedToOutputSec(frame) - SyntheticIntroSec);
    }

    /// <summary>Output seconds of merged frame <paramref name="frame"/>.</summary>
    public double MergedToOutputSec(long frame)
        => Locate(frame) is EdlAnchor a ? AnchorToOutputSec(a) ?? 0 : SyntheticIntroSec;

    /// <summary>
    /// The anchor on screen at output second <paramref name="outputSec"/>. Inside a freeze or a meme
    /// this is the held/next source moment (OutputTimeline's documented lossy inverse). Inside the
    /// synthetic intro it is the first frame.
    /// </summary>
    public EdlAnchor? OutputSecToAnchor(double outputSec)
    {
        if (Clips.Count == 0) return null;
        double t = Math.Clamp(outputSec - SyntheticIntroSec, 0, ClipsOutputSec);
        int i = 0;
        for (int k = Clips.Count - 1; k >= 0; k--)
        {
            if (Clips[k].OutputStartSec <= t && (Clips[k].OutputLengthSec > 0 || k == 0)) { i = k; break; }
        }
        var c = Clips[i];
        double rel = c.Output.OutputToSourceRelative(t - c.OutputStartSec);
        long src = c.KeepInUs + (long)Math.Round(rel * 1_000_000.0);
        return new EdlAnchor(c.ClipId, Math.Clamp(src, c.KeepInUs, c.KeepOutUs));
    }

    /// <summary>
    /// Moves a merged frame chosen on <paramref name="from"/> onto this timeline through
    /// (ClipId, source µs), so it survives intro toggles, a new thumbnail and reordering.
    /// Null (stale) when that clip is no longer queued.
    /// </summary>
    public long? Remap(CompositeTimeline from, long frame)
        => from.Locate(frame) is EdlAnchor a ? ToMerged(a) : null;
}
