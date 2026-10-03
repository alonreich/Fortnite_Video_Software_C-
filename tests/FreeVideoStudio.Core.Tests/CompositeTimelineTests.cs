using System;
using System.Collections.Generic;
using System.Linq;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>COMPOSITE_01 — Video-Merger-Migration.md P2.3 (T2.3a–c).</summary>
public class CompositeTimelineTests
{
    private static EdlClip Clip(string name, double durSec, int introFrames = 0, int? fadeOut = null, EdlEffects? fx = null)
        => new()
        {
            Path = name,
            DurationUs = (long)Math.Round(durSec * 1_000_000),
            Timing = introFrames > 0 || fadeOut != null ? new ExportTiming(60, 1, introFrames, null, fadeOut) : null,
            Effects = fx ?? EdlEffects.None,
        };

    private static MergeEdl Edl(params EdlClip[] clips) => new() { Clips = clips };

    [Fact]
    public void T23a_RoundTrip_Monotonic_NoDrift_200Clips()
    {
        var rnd = new Random(1234);
        var clips = Enumerable.Range(0, 200)
            .Select(i => Clip($"c{i}.mp4", 1 + rnd.NextDouble() * 20, introFrames: rnd.Next(0, 3) == 0 ? 6 : 0))
            .ToArray();
        var tl = CompositeTimeline.Build(Edl(clips));

        // Integer merged clock: the total is EXACTLY the sum of the per-clip frame counts.
        Assert.Equal(tl.Clips.Sum(c => c.MergedFrames), tl.TotalMergedFrames);
        for (int i = 1; i < tl.Clips.Count; i++)
            Assert.Equal(tl.Clips[i - 1].MergedEndFrame, tl.Clips[i].MergedStartFrame);

        long prevFrame = -1;
        double prevOut = -1;
        for (long f = 0; f <= tl.TotalMergedFrames; f += 7)
        {
            var a = tl.Locate(f);
            Assert.NotNull(a);
            Assert.Equal(f, tl.ToMerged(a!.Value));
            double o = tl.MergedToOutputSec(f);
            Assert.True(f > prevFrame);
            Assert.True(o >= prevOut - 1e-9, $"output not monotonic at {f}");
            prevFrame = f; prevOut = o;
        }
        var end = tl.Locate(tl.TotalMergedFrames)!.Value;
        Assert.Equal(tl.TotalMergedFrames, tl.ToMerged(end));
        Assert.Equal(tl.TotalMergedFrames / 60.0, tl.TotalOutputSec, 3);
    }

    [Fact]
    public void T23a_IntroRemovalRules()
    {
        var c1 = Clip("a.mp4", 10, introFrames: 6);
        var c2 = Clip("b.mp4", 10, introFrames: 6);

        var scraperOn = CompositeTimeline.Build(Edl(c1, c2));
        Assert.Equal(0, scraperOn.Clips[0].RemovedIntroUs);                 // clip 1 keeps its intro
        Assert.Equal(100_000, scraperOn.Clips[1].RemovedIntroUs);           // clip 2 loses it
        Assert.Equal(600 + 594, scraperOn.TotalMergedFrames);

        var off = CompositeTimeline.Build(Edl(c1, c2) with { ScraperEnabled = false });
        Assert.Equal(1200, off.TotalMergedFrames);

        var thumb = CompositeTimeline.Build(Edl(c1, c2) with { Thumbnail = new EdlThumbnail(new EdlAnchor(c2.ClipId, 2_000_000)) });
        Assert.Equal(100_000, thumb.Clips[0].RemovedIntroUs);               // custom thumbnail replaces clip 1's intro
        Assert.Equal(IntroTag.StandardIntroSec, thumb.SyntheticIntroSec);

        // FRAMESNAP_01 — the probed cut wins over the tag's nominal seconds.
        var snapped = CompositeTimeline.Build(Edl(c1, c2 with { IntroCutUs = 95_556 }));
        Assert.Equal(95_556, snapped.Clips[1].KeepInUs);
    }

    [Fact]
    public void T23b_SpeedAndFreezeInClip3_ShiftLaterClipsExactly()
    {
        var plain = new[] { Clip("1", 10), Clip("2", 10), Clip("3", 10), Clip("4", 10), Clip("5", 10) };
        var baseTl = CompositeTimeline.Build(Edl(plain));

        var fx = new EdlEffects
        {
            Speed = new[] { new EdlSpeedSegment(2_000_000, 4_000_000, 0.5) },   // 2 s at 0.5x → +2 s
            Freezes = new[] { new EdlFreeze(6_000_000, 1.5) },                  // +1.5 s
        };
        var edited = plain.ToArray();
        edited[2] = plain[2] with { Effects = fx };
        var tl = CompositeTimeline.Build(Edl(edited));

        // Merged clock is untouched by effects.
        Assert.Equal(baseTl.TotalMergedFrames, tl.TotalMergedFrames);
        // Clips before clip 3 do not move; every later clip moves by exactly 3.5 s.
        for (int i = 0; i < 5; i++)
        {
            double shift = tl.Clips[i].OutputStartSec - baseTl.Clips[i].OutputStartSec;
            Assert.Equal(i <= 2 ? 0 : 3.5, shift, 6);
        }
        Assert.Equal(53.5, tl.TotalOutputSec, 6);

        // D19 — base speed applies only OUTSIDE speed segments; segment speeds are absolute and the
        // freeze is a real-time hold: 4 clips x 10 s / 2 = 20; clip 3: 2 s @0.5x = 4 + 8 s / 2 = 4 + 1.5 hold.
        var fast = CompositeTimeline.Build(Edl(edited) with { BaseSpeed = 2.0 });
        Assert.Equal(29.5, fast.TotalOutputSec, 6);

        // The anchor at the start of clip 4 maps to 30 + 3.5 s.
        Assert.Equal(33.5, tl.AnchorToOutputSec(new EdlAnchor(edited[3].ClipId, 0))!.Value, 6);
        // And back.
        var back = tl.OutputSecToAnchor(33.5 + 1.0)!.Value;
        Assert.Equal(edited[3].ClipId, back.ClipId);
        Assert.InRange(back.SourceUs, 999_000, 1_001_000);
    }

    [Fact]
    public void T23b_MemePlacements()
    {
        var withFade = Clip("m", 10, fadeOut: 60);   // 1 s fade-out at the end of the file
        EdlMeme M(EdlMemePlacement p, long at = 0) => new("m" + p, "meme.mp4", p, at, 2.0);

        var start = CompositeTimeline.Build(Edl(withFade with { Effects = new EdlEffects { Memes = new[] { M(EdlMemePlacement.AtStart) } } }));
        Assert.Equal(12.0, start.TotalOutputSec, 6);
        Assert.Equal(2.0, start.AnchorToOutputSec(new EdlAnchor(withFade.ClipId, 0))!.Value, 6);   // clip (with fade-in) follows the meme

        var end = CompositeTimeline.Build(Edl(withFade with { Effects = new EdlEffects { Memes = new[] { M(EdlMemePlacement.AtEnd) } } }));
        Assert.Equal(8.9, end.AnchorToOutputSec(new EdlAnchor(withFade.ClipId, 8_900_000))!.Value, 6);   // before the fade-out: unchanged
        Assert.Equal(11.0, end.AnchorToOutputSec(new EdlAnchor(withFade.ClipId, 9_000_000))!.Value, 6);  // fade-out starts after the meme

        var mid = CompositeTimeline.Build(Edl(withFade with { Effects = new EdlEffects { Memes = new[] { M(EdlMemePlacement.Mid, 4_000_000) } } }));
        Assert.Equal(3.0, mid.AnchorToOutputSec(new EdlAnchor(withFade.ClipId, 3_000_000))!.Value, 6);
        Assert.Equal(7.0, mid.AnchorToOutputSec(new EdlAnchor(withFade.ClipId, 5_000_000))!.Value, 6);

        // Fade-out unknown (tag lost it) → the AtEnd meme goes at the clip's end.
        var noFade = Clip("n", 10);
        var tail = CompositeTimeline.Build(Edl(noFade with { Effects = new EdlEffects { Memes = new[] { M(EdlMemePlacement.AtEnd) } } }));
        Assert.Equal(9.5, tail.AnchorToOutputSec(new EdlAnchor(noFade.ClipId, 9_500_000))!.Value, 6);
        Assert.Equal(12.0, tail.TotalOutputSec, 6);
    }

    [Fact]
    public void T23c_RemapAcrossLayoutChanges()
    {
        var a = Clip("a", 10, introFrames: 6);
        var b = Clip("b", 10, introFrames: 6);
        var c = Clip("c", 10, introFrames: 6);
        var on = CompositeTimeline.Build(Edl(a, b, c));
        var off = CompositeTimeline.Build(Edl(a, b, c) with { ScraperEnabled = false });

        // A moment 3 s into clip c's source keeps pointing at that moment.
        long fOn = on.ToMerged(new EdlAnchor(c.ClipId, 3_000_000))!.Value;
        long fOff = off.Remap(on, fOn)!.Value;
        Assert.Equal(off.ToMerged(new EdlAnchor(c.ClipId, 3_000_000)), fOff);
        Assert.Equal(12, fOff - fOn);   // two intros of 6 frames restored before it

        // Thumbnail set: clip 1's intro disappears, everything after shifts back 6 frames.
        var thumb = CompositeTimeline.Build(Edl(a, b, c) with { Thumbnail = new EdlThumbnail(new EdlAnchor(b.ClipId, 1_000_000)) });
        Assert.Equal(fOn - 6, thumb.Remap(on, fOn));

        // Reorder: follows the clip.
        var reordered = CompositeTimeline.Build(Edl(c, a, b));
        Assert.Equal(reordered.ToMerged(new EdlAnchor(c.ClipId, 3_000_000)), reordered.Remap(on, fOn));

        // Clip removed: stale.
        var removed = CompositeTimeline.Build(Edl(a, b));
        Assert.Null(removed.Remap(on, fOn));

        // A position inside a removed intro lands on the first kept frame.
        Assert.Equal(on.Clips[1].MergedStartFrame, on.ToMerged(new EdlAnchor(b.ClipId, 50_000)));
    }

    [Fact]
    public void T24a_MergedTimelineAdapter_UsesCompositeFrameBoundaries()
    {
        // Odd, non-frame-aligned lengths: the adapter's boundaries must be the composite's integer frames.
        var sources = new[]
        {
            new MergeClipSource("a", 10.0071, 0.1),
            new MergeClipSource("b", 7.3333, 0.0956),
            new MergeClipSource("c", 5.0104, 0.1),
        };
        var tl = MergedTimeline.Build(sources, scraperEnabled: true, customThumbnail: false);
        var comp = tl.Composite;
        Assert.Equal(comp.TotalMergedFrames / 60.0, tl.TotalSec, 9);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(comp.Clips[i].MergedStartFrame / 60.0, tl.Clips[i].MergedStartSec, 9);
            Assert.Equal(comp.Clips[i].MergedEndFrame / 60.0, tl.Clips[i].MergedEndSec, 9);
            Assert.Equal(comp.Clips[i].KeepInUs / 1e6, tl.Clips[i].ContentStartSec, 9);
        }
        Assert.Equal(0.0956, tl.Clips[1].ContentStartSec, 9);
        // Seconds adapter stays an inverse inside clips and never escapes a clip's merged slice.
        for (double t = 0; t <= tl.TotalSec; t += 0.037)
        {
            var (ci, src) = tl.Locate(t);
            double back = tl.ToMerged(ci, src);
            Assert.True(back >= tl.Clips[ci].MergedStartSec - 1e-9 && back <= tl.Clips[ci].MergedEndSec + 1e-9);
            Assert.Equal(t, back, 6);
        }
    }

    [Fact]
    public void T72a_MusicMovesWithTheVideo_ButIsNeverStretched()
    {
        var a = Clip("a", 10); var b = Clip("b", 10);
        var plain = CompositeTimeline.Build(Edl(a, b));
        // A 4 s span at 2x in clip 1 BEFORE the anchor → the song starts 2 s earlier in the output.
        var fast = CompositeTimeline.Build(Edl(a with { Effects = new EdlEffects { Speed = new[] { new EdlSpeedSegment(2_000_000, 6_000_000, 2.0) } } }, b));
        Assert.Equal(12.0, plain.MergedSecToBodyOutputSec(12.0), 6);
        Assert.Equal(10.0, fast.MergedSecToBodyOutputSec(12.0), 6);
        // A meme at the start of clip 2 is inserted before that moment → +its length.
        var meme = CompositeTimeline.Build(Edl(a, b with { Effects = new EdlEffects { Memes = new[] { new EdlMeme("m", "x.png", EdlMemePlacement.AtStart, 0, 1.5) } } }));
        Assert.Equal(13.5, meme.MergedSecToBodyOutputSec(12.0), 6);
        // Base speed without segments = the old merged / speed rule; the thumbnail intro is not included.
        var speedy = CompositeTimeline.Build(Edl(a, b) with { BaseSpeed = 2.0, Thumbnail = new EdlThumbnail(new EdlAnchor(a.ClipId, 0)) });
        Assert.Equal(6.0, speedy.MergedSecToBodyOutputSec(12.0), 6);
    }

    [Fact]
    public void EmptyAndDegenerate()
    {
        var empty = CompositeTimeline.Build(MergeEdl.Empty);
        Assert.Null(empty.Locate(0));
        Assert.Equal(0, empty.TotalOutputSec);
        Assert.Equal(-1, empty.ClipIndexAt(0));

        // Unanalysed (0-length) clip between two real ones.
        var z = Clip("z", 0);
        var tl = CompositeTimeline.Build(Edl(Clip("a", 1), z, Clip("b", 1)));
        Assert.Equal(120, tl.TotalMergedFrames);
        Assert.Equal(2, tl.ClipIndexAt(60));
        Assert.Equal(2, tl.ClipIndexAt(120));
    }
}
