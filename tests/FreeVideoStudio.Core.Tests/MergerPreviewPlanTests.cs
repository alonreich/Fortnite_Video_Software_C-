using System;
using System.Linq;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>MERGEPREVIEW_01 — Video-Merger-Migration.md P8.1 (T8.1a: the preview schedule equals the export timing).</summary>
public class MergerPreviewPlanTests
{
    private static MergeEdl Edl(double baseSpeed = 1.5)
    {
        // Three 10 s clips; clip 2 has a 6-frame intro removed by the scraper.
        var a = new EdlClip
        {
            Path = @"C:\c\a.mp4", DurationUs = 10_000_000,
            Effects = new EdlEffects
            {
                Speed = new[] { new EdlSpeedSegment(2_000_000, 4_000_000, 0.5) },
                Freezes = new[] { new EdlFreeze(5_000_000, 1.5) },
            },
        };
        var b = new EdlClip
        {
            Path = @"C:\c\b.mp4", DurationUs = 10_000_000, IntroCutUs = 100_000,
            Effects = new EdlEffects { Cuts = new[] { new EdlCut(3_000_000, 4_000_000) } },
        };
        var c = new EdlClip
        {
            Path = @"C:\c\c.mp4", DurationUs = 10_000_000,
            Effects = new EdlEffects
            {
                Memes = new[] { new EdlMeme("m1", @"C:\m\boom.mp4", EdlMemePlacement.Mid, 5_000_000, 2.0) },
                Speed = new[] { new EdlSpeedSegment(7_000_000, 9_000_000, 2.0) },
            },
        };
        return new MergeEdl { Clips = new[] { a, b, c }, BaseSpeed = baseSpeed };
    }

    [Fact]
    public void T81a_TotalAndEveryMoment_MatchTheExportClock()
    {
        var tl = CompositeTimeline.Build(Edl());
        var plan = MergerPreviewPlan.Build(tl);

        Assert.True(plan.HasEffects);
        Assert.Equal(tl.TotalMergedFrames / 60.0, plan.TotalMergedSec, 9);
        Assert.Equal(tl.ClipsOutputSec, plan.TotalOutputSec, 6);

        var holds = plan.Steps.Where(s => s.IsHold).Select(s => s.MergedStartSec).ToArray();
        for (int frame = 0; frame <= tl.TotalMergedFrames; frame += 7)
        {
            double t = frame / 60.0;
            if (holds.Any(h => Math.Abs(h - t) < 1.0 / 60)) continue;   // the instant of a hold is ambiguous by definition
            Assert.Equal(tl.MergedSecToBodyOutputSec(t), plan.OutputSecAt(t), 3);
        }
    }

    [Fact]
    public void Schedule_SpeedsCutsAndHolds_AreWhereTheEditorPutThem()
    {
        var plan = MergerPreviewPlan.Build(CompositeTimeline.Build(Edl()));

        // D19: segment speed is absolute; base speed outside segments.
        Assert.Equal(1.5, plan.SpeedAt(1.0, 1.5));
        Assert.Equal(0.5, plan.SpeedAt(3.0, 1.5));
        Assert.Equal(1.5, plan.SpeedAt(4.5, 1.5));

        // Freeze at 5 s of clip 1 (merged 5 s), 1.5 s.
        int f = plan.NextHold(4.9, 5.1, -1);
        Assert.True(f >= 0);
        Assert.Equal(PreviewStepKind.Freeze, plan.Steps[f].Kind);
        Assert.Equal(5.0, plan.Steps[f].MergedStartSec, 6);
        Assert.Equal(1.5, plan.Steps[f].HoldSec, 6);
        Assert.Equal(-1, plan.NextHold(4.9, 5.1, f));   // consumed → not returned again

        // Cut in clip 2: source 3–4 s, clip 2 starts at merged 10 s with kept-in 0.1 s → merged 12.9–13.9.
        Assert.Null(plan.CutResumeAt(12.8));
        Assert.Equal(13.9, plan.CutResumeAt(13.2)!.Value, 6);

        // Meme mid-clip 3: clip 3 starts at merged 19.9 s → at 24.9 s, 2 s, carries its file.
        int m = plan.NextHold(24.0, 25.0, -1);
        Assert.Equal(PreviewStepKind.Meme, plan.Steps[m].Kind);
        Assert.Equal(24.9, plan.Steps[m].MergedStartSec, 6);
        Assert.Equal(@"C:\m\boom.mp4", plan.Steps[m].Meme!.FilePath);
        Assert.Equal(2.0, plan.SpeedAt(19.9 + 8.0, 1.5));

        // P8.2 — memes go to the Main App's MemePreviewDirector on the merged clock; freezes-only search skips them.
        var mp = Assert.Single(plan.MemePlacements);
        Assert.Equal(new MemePlacement(@"C:\m\boom.mp4", 24.9, 2.0, "m1"), mp with { AtSourceSecRelative = Math.Round(mp.AtSourceSecRelative, 6) });
        Assert.Equal(-1, plan.NextHold(24.0, 25.0, -1, includeMemes: false));
    }

    [Fact]
    public void NoEffects_IsOneSpeedEverywhere()
    {
        var edl = new MergeEdl { Clips = new[] { new EdlClip { Path = "a", DurationUs = 5_000_000 }, new EdlClip { Path = "b", DurationUs = 5_000_000 } }, BaseSpeed = 2 };
        var tl = CompositeTimeline.Build(edl);
        var plan = MergerPreviewPlan.Build(tl);
        Assert.False(plan.HasEffects);
        Assert.Equal(5.0, plan.TotalOutputSec, 6);
        Assert.All(plan.Steps, s => Assert.Equal(PreviewStepKind.Play, s.Kind));
        Assert.Equal(2.0, plan.SpeedAt(7.0, 1));
        Assert.Equal(-1, plan.NextHold(0, 10, -1));
        Assert.Equal(3.5, plan.OutputSecAt(7.0), 6);
        Assert.Empty(MergerPreviewPlan.Empty.Steps);
    }
}
