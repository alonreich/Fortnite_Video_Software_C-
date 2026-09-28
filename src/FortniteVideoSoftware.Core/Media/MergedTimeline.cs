// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>One queued clip as the analysis saw it. Seconds are SOURCE seconds of that file.</summary>
public sealed record MergeClipSource(string Path, double DurationSec, double TaggedIntroSec, double TrimStartSec = 0, double TrimEndSec = 0);

/// <summary>
/// One clip's slice of the merged timeline. <see cref="MergedLengthSec"/> (COMPOSITE_01) is the
/// clip's length on the merged clock: its frame count at 60 fps, which can differ from the source
/// length (<see cref="LengthSec"/>) by up to half a frame. Negative = not given (source length).
/// </summary>
public readonly record struct MergedClip(
    int Index,
    string Path,
    double ContentStartSec,
    double ContentEndSec,
    double MergedStartSec,
    double RemovedIntroSec,
    double MergedLengthSec = -1)
{
    public double LengthSec => Math.Max(0, ContentEndSec - ContentStartSec);
    public double MergedEndSec => MergedStartSec + (MergedLengthSec >= 0 ? MergedLengthSec : LengthSec);
}

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// SCRAPER_02 — ONE MERGED TIMELINE FOR PREVIEW, MUSIC AND EXPORT.
///
/// The Video Merger's clock is "merged seconds": the clips' kept content laid end to end, at 1.0x,
/// BEFORE the merge speed is applied and WITHOUT the synthetic thumbnail intro that a custom
/// thumbnail prepends (that intro is added after the music mix, exactly as the Main App adds its
/// own, so it never moves the music).
///
/// Which thumbnail intros are removed (all of them are tagged by <see cref="IntroTag"/>):
///   • Clips 2..N: removed while the Thumbnail Scraper is on.
///   • Clip 1: kept, because it becomes the merged video's own share thumbnail, UNLESS a custom
///     thumbnail is chosen. Then it is removed whether or not the scraper is on, and a new 0.1 s
///     still built from the chosen frame takes its place.
///   • Fades are never touched. Only the intro is removed.
///
/// The merger preview (playhead, dividers, thumbnail marker), the Music Wizard (clip lanes and the
/// music window), and MergerWorker (trim windows, music delays) all read THIS object. That is why a
/// song placed at 0:42 starts at 0:42 in the preview and in the file.
///
/// <see cref="Remap"/> moves a merged-time position (a music start, a thumbnail) from the timeline
/// it was chosen on to a new one by going through (clip, source second). A song the user started on
/// a particular moment of clip 3 therefore still starts on that moment after intros are removed or
/// restored.
///
/// COMPOSITE_01 (Video-Merger-Migration.md P2.4) — this class is now a SECONDS ADAPTER over
/// <see cref="CompositeTimeline"/>. The kept windows, intro removal and the integer merged-frame
/// boundaries all come from there; only the continuous in-clip playhead maths stays here. Callers
/// move to <see cref="Composite"/> as the merger migrates; delete this adapter when none are left.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class MergedTimeline
{
    /// <summary>A kept window shorter than this after removal means the removal is refused.</summary>
    public const double MinContentSec = 0.05;

    public IReadOnlyList<MergedClip> Clips { get; }
    public double TotalSec { get; }
    public bool ScraperEnabled { get; }
    public bool CustomThumbnail { get; }

    /// <summary>Output seconds of the still intro prepended for a custom thumbnail (0 when none).</summary>
    public double SyntheticIntroSec => CustomThumbnail ? IntroTag.StandardIntroSec : 0;

    /// <summary>Identity of the layout. Equal signatures mean identical clip windows.</summary>
    public string Signature { get; }

    /// <summary>The frame-exact timeline this adapter reads (clip ids are derived from the queue index).</summary>
    public CompositeTimeline Composite { get; }

    public static readonly MergedTimeline Empty = new(Array.Empty<MergedClip>(), false, false, CompositeTimeline.Build(MergeEdl.Empty));

    private MergedTimeline(IReadOnlyList<MergedClip> clips, bool scraper, bool custom, CompositeTimeline composite)
    {
        Composite = composite;
        Clips = clips;
        ScraperEnabled = scraper;
        CustomThumbnail = custom;
        double total = 0;
        var sig = new StringBuilder();
        foreach (var c in clips)
        {
            total = c.MergedEndSec;
            sig.Append(c.Path).Append('|')
               .Append(c.ContentStartSec.ToString("F4", CultureInfo.InvariantCulture)).Append('|')
               .Append(c.ContentEndSec.ToString("F4", CultureInfo.InvariantCulture)).Append(';');
        }
        TotalSec = total;
        Signature = sig.ToString();
    }

    public static MergedTimeline Build(IReadOnlyList<MergeClipSource> sources, bool scraperEnabled, bool customThumbnail)
    {
        static long Us(double sec) => double.IsFinite(sec) && sec > 0 ? (long)Math.Round(sec * 1_000_000.0) : 0;

        var edlClips = new List<EdlClip>(sources.Count);
        for (int i = 0; i < sources.Count; i++)
        {
            var s = sources[i];
            edlClips.Add(new EdlClip
            {
                ClipId = IndexId(i),
                Path = s.Path,
                DurationUs = Us(s.DurationSec),
                InUs = Us(s.TrimStartSec),
                OutUs = Us(s.TrimEndSec),
                IntroCutUs = Us(s.TaggedIntroSec),
            });
        }
        var edl = new MergeEdl
        {
            Clips = edlClips,
            ScraperEnabled = scraperEnabled,
            Thumbnail = customThumbnail && edlClips.Count > 0 ? new EdlThumbnail(new EdlAnchor(edlClips[0].ClipId, 0)) : null,
        };
        var composite = CompositeTimeline.Build(edl);

        var clips = new List<MergedClip>(sources.Count);
        foreach (var c in composite.Clips)
        {
            clips.Add(new MergedClip(
                c.Index, c.Path,
                c.KeepInUs / 1_000_000.0, c.KeepOutUs / 1_000_000.0,
                c.MergedStartFrame / (double)CompositeTimeline.MergeFps,
                c.RemovedIntroUs / 1_000_000.0,
                c.MergedFrames / (double)CompositeTimeline.MergeFps));
        }
        return new MergedTimeline(clips, scraperEnabled, customThumbnail, composite);
    }

    /// <summary>Stable clip id for queue index <paramref name="index"/> (the adapter has no real ids).</summary>
    private static Guid IndexId(int index) => new(index, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>The clip under <paramref name="mergedSec"/> and the matching SOURCE second of that clip.</summary>
    public (int ClipIndex, double SourceSec) Locate(double mergedSec)
    {
        if (Clips.Count == 0) return (-1, 0);
        double t = Math.Clamp(mergedSec, 0, TotalSec);
        for (int i = 0; i < Clips.Count; i++)
        {
            var c = Clips[i];
            bool last = i == Clips.Count - 1;
            if (t < c.MergedEndSec || last)
                return (i, Math.Clamp(c.ContentStartSec + (t - c.MergedStartSec), c.ContentStartSec, c.ContentEndSec));
        }
        var tail = Clips[^1];
        return (tail.Index, tail.ContentEndSec);
    }

    /// <summary>Merged seconds of a source position. A position inside a removed intro lands on the clip's first kept frame.</summary>
    public double ToMerged(int clipIndex, double sourceSec)
    {
        if (clipIndex < 0 || clipIndex >= Clips.Count) return clipIndex < 0 ? 0 : TotalSec;
        var c = Clips[clipIndex];
        return Math.Min(c.MergedEndSec, c.MergedStartSec + (Math.Clamp(sourceSec, c.ContentStartSec, c.ContentEndSec) - c.ContentStartSec));
    }

    /// <summary>
    /// Moves a merged-time position chosen on <paramref name="from"/> onto this timeline through
    /// (clip, source second). Returns the position unchanged when the two timelines do not describe
    /// the same queue (different clip count or paths).
    /// </summary>
    public double Remap(MergedTimeline from, double mergedSec)
    {
        if (!SameQueue(from)) return Math.Clamp(mergedSec, 0, TotalSec);
        var (clip, src) = from.Locate(mergedSec);
        // A position at the very end of a clip stays at the end of that clip.
        return ToMerged(clip, src);
    }

    public bool SameQueue(MergedTimeline other)
    {
        if (other.Clips.Count != Clips.Count) return false;
        for (int i = 0; i < Clips.Count; i++)
            if (!string.Equals(other.Clips[i].Path, Clips[i].Path, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Per-clip source second where the kept content starts (index-aligned with the queue).</summary>
    public List<double> ContentStarts()
    {
        var list = new List<double>(Clips.Count);
        foreach (var c in Clips) list.Add(c.ContentStartSec);
        return list;
    }
}
