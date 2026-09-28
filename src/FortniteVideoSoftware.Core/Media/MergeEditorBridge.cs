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

// ══════════════════════════════════════════════════════════════════════════════════════════════
// MERGEEDIT_01 — ONE GRANULAR EDITOR OVER THE WHOLE MERGE (Video-Merger-Migration.md P6.2, D16–D18).
//
// The Granular Speed Editor knows ONE source with ONE time axis. The merge becomes exactly that:
//   • PLAYBACK — mpv plays an inline EDL (edl://) of every clip's kept window. Each segment's LENGTH
//     is the clip's integer merged-frame count / 60, so mpv's time-pos IS the merged clock (P4.1 PASS:
//     seamless across mixed codecs / sizes / rates).
//   • EFFECTS  — the editor edits in MERGED milliseconds. On the way in, every clip's EdlEffects are
//     mapped from (ClipId, source µs) to merged ms; on the way out, every editor effect is put back
//     into the clip it belongs to. A speed segment or cut that crosses a clip boundary is SPLIT (the
//     output is identical, and D4 "effects stay inside one clip" holds by construction). A freeze or
//     meme belongs to the clip under it; a meme's Start/Mid/End placement follows D17.
// Pure: no UI, no mpv, no disk. Unit-tested.
// ══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>One clip as the merge editor sees it (merged ms ↔ this clip's source µs).</summary>
public sealed record MergeEditorClip(
    int Index, Guid ClipId, string Path,
    double StartMs, double EndMs,
    long KeepInUs, long KeepOutUs,
    double FadeInMs, double FadeOutMs)
{
    public double LengthMs => EndMs - StartMs;

    public long ToSourceUs(double mergedMs)
        => Math.Clamp(KeepInUs + (long)Math.Round((mergedMs - StartMs) * 1000.0), KeepInUs, KeepOutUs);

    public double ToMergedMs(long sourceUs)
        => StartMs + Math.Clamp(sourceUs - KeepInUs, 0, KeepOutUs - KeepInUs) / 1000.0;
}

/// <summary>What the editor is opened with / hands back — all positions in MERGED milliseconds.</summary>
public sealed record MergeEditorState(
    IReadOnlyList<SpeedSegment> Segments,
    double FreezeTimeMs,
    double FreezeDurationS,
    IReadOnlyList<CutRange> Cuts,
    IReadOnlyList<MemePlacement> Memes,
    double BaseSpeed);

/// <summary>The whole merge as ONE editor source.</summary>
public sealed class MergeEditorSource
{
    /// <summary>One merged frame, the tolerance for "on the first/last frame" (D17).</summary>
    public const double FrameMs = 1000.0 / CompositeTimeline.MergeFps;

    public IReadOnlyList<MergeEditorClip> Clips { get; }
    public double TotalMs { get; }

    /// <summary>mpv inline EDL that plays the merge as one file whose clock is the merged clock.</summary>
    public string MpvUrl { get; }

    /// <summary>Freezes that the v1 editor (one freeze for the whole merge) could not show; kept on Accept.</summary>
    public int ExtraFreezes { get; private set; }

    private MergeEditorSource(List<MergeEditorClip> clips, double totalMs, string url)
    {
        Clips = clips;
        TotalMs = totalMs;
        MpvUrl = url;
    }

    public static MergeEditorSource Build(CompositeTimeline tl)
    {
        var clips = new List<MergeEditorClip>(tl.Clips.Count);
        var url = new StringBuilder("edl://");
        var ci = CultureInfo.InvariantCulture;
        foreach (var c in tl.Clips)
        {
            if (c.MergedFrames <= 0) continue;
            var edlClip = tl.Edl.Clips[c.Index];
            long keepOut = c.KeepInUs + CompositeTimeline.FramesToUs(c.MergedFrames);
            double startMs = c.MergedStartFrame * FrameMs;
            double endMs = c.MergedEndFrame * FrameMs;

            double fadeIn = 0, fadeOut = 0;
            if (edlClip.Timing is ExportTiming t && t.Fps > 0)
            {
                if (edlClip.InUs == 0 && t.FadeInFrames is int fi) fadeIn = fi / t.Fps * 1000.0;
                bool reachesEnd = edlClip.DurationUs > 0 && c.KeepOutUs >= edlClip.DurationUs - 1000;
                if (reachesEnd && t.FadeOutFrames is int fo) fadeOut = fo / t.Fps * 1000.0;
            }
            clips.Add(new MergeEditorClip(c.Index, c.ClipId, c.Path, startMs, endMs, c.KeepInUs, keepOut, fadeIn, fadeOut));

            if (clips.Count > 1) url.Append(';');
            int bytes = Encoding.UTF8.GetByteCount(c.Path);
            url.Append('%').Append(bytes.ToString(ci)).Append('%').Append(c.Path)
               .Append(',').Append((c.KeepInUs / 1_000_000.0).ToString("0.######", ci))
               .Append(',').Append((c.MergedFrames / (double)CompositeTimeline.MergeFps).ToString("0.######", ci));
        }
        return new MergeEditorSource(clips, tl.TotalMergedFrames * FrameMs, url.ToString());
    }

    /// <summary>The clip that owns merged position <paramref name="ms"/>. A boundary belongs to the clip that STARTS there; the very end to the last clip.</summary>
    public int ClipAt(double ms)
    {
        if (Clips.Count == 0) return -1;
        for (int i = Clips.Count - 1; i >= 0; i--)
            if (ms >= Clips[i].StartMs - 0.0005) return i;
        return 0;
    }

    public int IndexOf(Guid id)
    {
        for (int i = 0; i < Clips.Count; i++) if (Clips[i].ClipId == id) return i;
        return -1;
    }

    // ── EDL → editor ─────────────────────────────────────────────────────────────────────────

    public MergeEditorState ToEditor(MergeEdl edl)
    {
        var segments = new List<SpeedSegment>();
        var cuts = new List<CutRange>();
        var memes = new List<MemePlacement>();
        double freezeAt = -1, freezeDur = 1.0;
        int freezes = 0;

        foreach (var clip in Clips)
        {
            var fx = edl.Clips[clip.Index].Effects;
            foreach (var s in fx.Speed)
            {
                var z = s.Zoom;
                segments.Add(new SpeedSegment(
                    clip.ToMergedMs(s.StartUs), clip.ToMergedMs(s.EndUs), s.Speed,
                    z?.X, z?.Y, z?.W, z?.H,
                    z is { SourceW: > 0, SourceH: > 0 } ? $"{z.SourceW}x{z.SourceH}" : null,
                    z?.Slow ?? false,
                    z?.StartUs is long zs ? clip.ToMergedMs(zs) : null,
                    z?.EndUs is long ze ? clip.ToMergedMs(ze) : null));
            }
            foreach (var f in fx.Freezes)
            {
                freezes++;
                if (freezeAt < 0) { freezeAt = clip.ToMergedMs(f.AtUs); freezeDur = f.DurationSec; }
            }
            foreach (var k in fx.Cuts) cuts.Add(new CutRange(clip.ToMergedMs(k.StartUs), clip.ToMergedMs(k.EndUs)));
            foreach (var m in fx.Memes)
            {
                double at = m.Placement switch
                {
                    EdlMemePlacement.AtStart => clip.StartMs,
                    EdlMemePlacement.AtEnd => clip.EndMs - clip.FadeOutMs,
                    _ => clip.ToMergedMs(m.AtUs),
                };
                memes.Add(new MemePlacement(m.FilePath, at / 1000.0, m.DurationSec, m.Id));
            }
        }
        ExtraFreezes = Math.Max(0, freezes - 1);
        segments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        cuts.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        memes.Sort((a, b) => a.AtSourceSecRelative.CompareTo(b.AtSourceSecRelative));
        return new MergeEditorState(segments, freezeAt, freezeDur, cuts, memes, edl.BaseSpeed);
    }

    // ── editor → EDL ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Puts the editor's result back into the clips. Freezes the v1 editor could not show
    /// (<see cref="ExtraFreezes"/>) are kept in their clips; everything else is replaced.
    /// </summary>
    public MergeEdl FromEditor(MergeEditorState st, MergeEdl edl)
    {
        int n = Clips.Count;
        var speed = NewLists<EdlSpeedSegment>(n);
        var cuts = NewLists<EdlCut>(n);
        var memes = NewLists<EdlMeme>(n);
        var freezes = NewLists<EdlFreeze>(n);

        foreach (var s in st.Segments)
        {
            foreach (var (i, a, b) in Split(s.StartMs, s.EndMs))
            {
                var clip = Clips[i];
                EdlZoom? zoom = null;
                if (s.ZoomX is int zx && s.ZoomY is int zy && s.ZoomW is int zw && s.ZoomH is int zh)
                {
                    var (sw, sh) = ParseRes(s.ZoomOrigRes);
                    long? zs = s.ZoomStartMs is double z0 ? clip.ToSourceUs(Math.Clamp(z0, a, b)) : null;
                    long? ze = s.ZoomEndMs is double z1 ? clip.ToSourceUs(Math.Clamp(z1, a, b)) : null;
                    zoom = new EdlZoom(zx, zy, zw, zh, s.ZoomSlow, zs, ze, sw, sh);
                }
                speed[i].Add(new EdlSpeedSegment(clip.ToSourceUs(a), clip.ToSourceUs(b), s.Speed, zoom));
            }
        }

        foreach (var k in st.Cuts)
            foreach (var (i, a, b) in Split(k.StartMs, k.EndMs))
                cuts[i].Add(new EdlCut(Clips[i].ToSourceUs(a), Clips[i].ToSourceUs(b)));

        if (st.FreezeTimeMs >= 0 && n > 0)
        {
            int i = ClipAt(st.FreezeTimeMs);
            freezes[i].Add(new EdlFreeze(Clips[i].ToSourceUs(st.FreezeTimeMs), st.FreezeDurationS));
        }

        foreach (var m in st.Memes)
        {
            if (n == 0) break;
            double at = m.AtSourceSecRelative * 1000.0;
            int i = ClipAt(at);
            var clip = Clips[i];
            var placement = PlacementAt(clip, at);
            long atUs = placement switch
            {
                EdlMemePlacement.AtStart => clip.KeepInUs,
                EdlMemePlacement.AtEnd => clip.ToSourceUs(clip.EndMs - clip.FadeOutMs),
                _ => clip.ToSourceUs(at),
            };
            memes[i].Add(new EdlMeme(m.Id, m.FilePath, placement, atUs, m.DurationSec));
        }

        var clips = new List<EdlClip>(edl.Clips);
        for (int i = 0; i < n; i++)
        {
            var clip = Clips[i];
            var old = edl.Clips[clip.Index].Effects;
            // v1: the editor shows ONE freeze; the rest (ExtraFreezes) stay where they were.
            var keptFreezes = new List<EdlFreeze>(freezes[i]);
            if (ExtraFreezes > 0) keptFreezes.AddRange(SkipFirstFreeze(edl, clip.Index));
            clips[clip.Index] = edl.Clips[clip.Index] with
            {
                Effects = old with { Speed = speed[i], Cuts = cuts[i], Memes = memes[i], Freezes = keptFreezes },
            };
        }
        return edl with { Clips = clips, BaseSpeed = st.BaseSpeed > 0 && double.IsFinite(st.BaseSpeed) ? st.BaseSpeed : edl.BaseSpeed };
    }

    /// <summary>D17 — Start/Mid/End by where the meme was dropped.</summary>
    public static EdlMemePlacement PlacementAt(MergeEditorClip clip, double atMs)
    {
        double rel = atMs - clip.StartMs;
        double toEnd = clip.EndMs - atMs;
        if (rel <= Math.Max(clip.FadeInMs, FrameMs)) return EdlMemePlacement.AtStart;
        if (toEnd <= Math.Max(clip.FadeOutMs, FrameMs)) return EdlMemePlacement.AtEnd;
        return EdlMemePlacement.Mid;
    }

    /// <summary>Splits [a, b) merged ms at clip boundaries. Pieces shorter than 1 ms are dropped.</summary>
    public IEnumerable<(int Clip, double A, double B)> Split(double a, double b)
    {
        if (b < a) (a, b) = (b, a);
        for (int i = 0; i < Clips.Count; i++)
        {
            double s = Math.Max(a, Clips[i].StartMs);
            double e = Math.Min(b, Clips[i].EndMs);
            if (e - s >= 1.0) yield return (i, s, e);
        }
    }

    private IEnumerable<EdlFreeze> SkipFirstFreeze(MergeEdl edl, int clipIndex)
    {
        // The first freeze of the WHOLE merge was shown in the editor; every other one is kept.
        bool firstSeen = false;
        foreach (var c in Clips)
        {
            foreach (var f in edl.Clips[c.Index].Effects.Freezes)
            {
                if (!firstSeen) { firstSeen = true; continue; }
                if (c.Index == clipIndex) yield return f;
            }
        }
    }

    private static List<T>[] NewLists<T>(int n)
    {
        var a = new List<T>[n];
        for (int i = 0; i < n; i++) a[i] = new List<T>();
        return a;
    }

    private static (int W, int H) ParseRes(string? res)
    {
        if (string.IsNullOrWhiteSpace(res)) return (0, 0);
        var parts = res.Split('x', 'X');
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
               && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)
            ? (w, h) : (0, 0);
    }
}
