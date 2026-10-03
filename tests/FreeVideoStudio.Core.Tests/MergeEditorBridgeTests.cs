using System;
using System.Linq;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>MERGEEDIT_01 — Video-Merger-Migration.md P6.2 (whole-merge editor bridge, D16–D19).</summary>
public class MergeEditorBridgeTests
{
    // Three 10 s clips. a: no tag. b: intro 6 f + fade-in 30 f + fade-out 60 f at 60 fps. c: no tag.
    private static readonly EdlClip A = new() { Path = @"C:\c\a.mp4", DurationUs = 10_000_000 };
    private static readonly EdlClip B = new() { Path = @"C:\c\b, weird;name%.mp4", DurationUs = 10_000_000, IntroCutUs = 100_000, Timing = new ExportTiming(60, 1, 6, 30, 60) };
    private static readonly EdlClip C = new() { Path = @"C:\c\c.mp4", DurationUs = 10_000_000 };

    private static (MergeEdl Edl, MergeEditorSource Src) Build(MergeEdl? edl = null)
    {
        edl ??= new MergeEdl { Clips = new[] { A, B, C } };
        return (edl, MergeEditorSource.Build(CompositeTimeline.Build(edl)));
    }

    [Fact]
    public void Source_MergedClock_AndMpvUrl()
    {
        var (_, src) = Build();
        Assert.Equal(3, src.Clips.Count);
        Assert.Equal(10_000, src.Clips[0].EndMs, 6);
        Assert.Equal(10_000, src.Clips[1].StartMs, 6);
        Assert.Equal(19_900, src.Clips[1].EndMs, 6);          // intro (6 frames) removed from clip 2
        Assert.Equal(29_900, src.TotalMs, 6);
        Assert.Equal(500, src.Clips[1].FadeInMs, 6);
        Assert.Equal(1000, src.Clips[1].FadeOutMs, 6);

        // Length-prefixed paths (commas, semicolons, percent), start = kept-in, length = frames/60.
        int bytes = System.Text.Encoding.UTF8.GetByteCount(B.Path);
        Assert.Equal($"edl://%{A.Path.Length}%{A.Path},0,10;%{bytes}%{B.Path},0.1,9.9;%{C.Path.Length}%{C.Path},0,10", src.MpvUrl);
    }

    [Fact]
    public void ClipAt_BoundaryBelongsToTheClipThatStarts_EndToTheLast()
    {
        var (_, src) = Build();
        Assert.Equal(0, src.ClipAt(0));
        Assert.Equal(0, src.ClipAt(9_999));
        Assert.Equal(1, src.ClipAt(10_000));
        Assert.Equal(2, src.ClipAt(29_900));
    }

    [Fact]
    public void SegmentAndCutAcrossABoundary_AreSplitPerClip_WithoutChangingTheOutput()
    {
        var (edl, src) = Build();
        var st = new MergeEditorState(
            new[] { new SpeedSegment(8_000, 12_000, 0.5) },
            -1, 1, new[] { new CutRange(19_000, 21_000) }, Array.Empty<MemePlacement>(), 1.0);
        var back = src.FromEditor(st, edl);

        Assert.Equal(new EdlSpeedSegment(8_000_000, 10_000_000, 0.5), back.Clips[0].Effects.Speed.Single());
        Assert.Equal(new EdlSpeedSegment(100_000, 2_100_000, 0.5), back.Clips[1].Effects.Speed.Single());
        Assert.Equal(new EdlCut(9_100_000, 10_000_000), back.Clips[1].Effects.Cuts.Single());
        Assert.Equal(new EdlCut(0, 1_100_000), back.Clips[2].Effects.Cuts.Single());

        // Output length = 29.9 + (4 s at 0.5x plays 8 s: +4) - 2 s cut = 31.9.
        Assert.Equal(31.9, CompositeTimeline.Build(back).TotalOutputSec, 3);

        // And back into the editor: the two halves come back as two adjacent segments at the same speed.
        var again = MergeEditorSource.Build(CompositeTimeline.Build(back)).ToEditor(back);
        Assert.Equal(new[] { (8_000.0, 10_000.0), (10_000.0, 12_000.0) }, again.Segments.Select(s => (s.StartMs, s.EndMs)));
    }

    [Theory]
    [InlineData(10_000, EdlMemePlacement.AtStart)]    // clip 2's first frame
    [InlineData(10_400, EdlMemePlacement.AtStart)]    // inside clip 2's 0.5 s fade-in
    [InlineData(10_600, EdlMemePlacement.Mid)]
    [InlineData(19_000, EdlMemePlacement.AtEnd)]      // inside clip 2's 1 s fade-out
    [InlineData(5_000, EdlMemePlacement.Mid)]         // clip 1, no fade info
    [InlineData(9_990, EdlMemePlacement.AtEnd)]       // clip 1's last frame
    [InlineData(29_900, EdlMemePlacement.AtEnd)]      // the very end
    public void D17_MemePlacementByPosition(double atMs, EdlMemePlacement expected)
    {
        var (edl, src) = Build();
        var st = new MergeEditorState(Array.Empty<SpeedSegment>(), -1, 1, Array.Empty<CutRange>(),
            new[] { new MemePlacement(@"C:\m\x.mp4", atMs / 1000.0, 2, "meme0") }, 1.0);
        var back = src.FromEditor(st, edl);
        var meme = back.Clips.SelectMany(c => c.Effects.Memes).Single();
        Assert.Equal(expected, meme.Placement);
    }

    [Fact]
    public void AtEndMeme_SnapsToTheFadeOutStart_AndRoundTrips()
    {
        var (edl, src) = Build();
        var st = new MergeEditorState(Array.Empty<SpeedSegment>(), -1, 1, Array.Empty<CutRange>(),
            new[] { new MemePlacement(@"C:\m\x.mp4", 19.5, 2, "meme0") }, 1.0);
        var back = src.FromEditor(st, edl);
        var m = back.Clips[1].Effects.Memes.Single();
        Assert.Equal(EdlMemePlacement.AtEnd, m.Placement);
        Assert.Equal(9_000_000, m.AtUs);                                   // fade-out starts 1 s before the end
        var shown = src.ToEditor(back).Memes.Single();
        Assert.Equal(18.9, shown.AtSourceSecRelative, 6);                  // shown where it will play
        // The composite timeline plays the meme before the fade-out: output grows by exactly 2 s.
        Assert.Equal(31.9, CompositeTimeline.Build(back).TotalOutputSec, 3);
    }

    [Fact]
    public void FullRoundTrip_EveryEffectKind()
    {
        var (edl, src) = Build();
        var st = new MergeEditorState(
            new[]
            {
                new SpeedSegment(1_000, 3_000, 2.0, 10, 20, 640, 360, "1920x1080", true, 1_500, 2_500),
                new SpeedSegment(22_000, 24_000, 0.25),
            },
            15_000, 1.5,
            new[] { new CutRange(26_000, 27_000) },
            new[] { new MemePlacement(@"C:\m\x.mp4", 5.0, 2, "meme0"), new MemePlacement(@"C:\m\y.png", 10.0, 4, "meme1") },
            1.25);

        var back = src.FromEditor(st, edl);
        Assert.Equal(1.25, back.BaseSpeed);
        var z = back.Clips[0].Effects.Speed[0].Zoom!;
        Assert.Equal((1920, 1080, 1_500_000L, 2_500_000L), (z.SourceW, z.SourceH, z.StartUs!.Value, z.EndUs!.Value));
        Assert.Equal(new EdlFreeze(5_100_000, 1.5), back.Clips[1].Effects.Freezes.Single());

        var st2 = MergeEditorSource.Build(CompositeTimeline.Build(back)).ToEditor(back);
        Assert.Equal(st.Segments, st2.Segments);
        Assert.Equal(st.FreezeTimeMs, st2.FreezeTimeMs, 6);
        Assert.Equal(st.Cuts, st2.Cuts);
        Assert.Equal(st.Memes, st2.Memes);
        Assert.Equal(st.BaseSpeed, st2.BaseSpeed);

        // Re-applying the same editor state changes nothing (undo sees no phantom edit).
        Assert.Equal(back, MergeEditorSource.Build(CompositeTimeline.Build(back)).FromEditor(st2, back));
    }

    [Fact]
    public void ExtraFreezes_TheV1EditorCannotShow_AreKept()
    {
        var fx = new EdlEffects { Freezes = new[] { new EdlFreeze(1_000_000, 1), new EdlFreeze(5_000_000, 2) } };
        var edl = new MergeEdl { Clips = new[] { A with { Effects = fx }, B, C } };
        var src = MergeEditorSource.Build(CompositeTimeline.Build(edl));
        var st = src.ToEditor(edl);
        Assert.Equal(1, src.ExtraFreezes);
        Assert.Equal(1_000, st.FreezeTimeMs, 6);

        var back = src.FromEditor(st with { FreezeTimeMs = 2_000 }, edl);
        Assert.Equal(new[] { new EdlFreeze(2_000_000, 1), new EdlFreeze(5_000_000, 2) }, back.Clips[0].Effects.Freezes);
    }

    [Fact]
    public void D18_CutShortensTheOutput_D19_BaseSpeedOutsideSegmentsOnly()
    {
        var fx = new EdlEffects { Cuts = new[] { new EdlCut(2_000_000, 4_000_000) }, Speed = new[] { new EdlSpeedSegment(6_000_000, 8_000_000, 1.0) } };
        var edl = new MergeEdl { Clips = new[] { A with { Effects = fx } }, BaseSpeed = 2.0 };
        // 10 s - 2 s cut = 8 s; 2 s segment at an ABSOLUTE 1.0x = 2 s; the other 6 s at 2x = 3 s → 5 s.
        Assert.Equal(5.0, CompositeTimeline.Build(edl).TotalOutputSec, 6);
    }
}
