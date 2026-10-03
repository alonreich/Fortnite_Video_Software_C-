// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Collections.Generic;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>MUSICSYNC_01 — the one music-bed plan the export and the live preview both read.</summary>
public class MusicBedPlanTests
{
    [Fact]
    public void FirstTrackUsesTheWizardOffsetAndLaterTracksStartAtZero()
    {
        var plan = MusicBedPlan.Build(new[] { "a.mp3", "b.mp3" }, new[] { 30.0, 100.0 },
            firstFileOffsetSec: 10, bedDurationSec: 50, loop: false, startDelaySec: 5);

        Assert.Equal(2, plan.Count);
        Assert.Equal(new MusicBedSegment("a.mp3", 10, 20, 5), plan[0]);
        Assert.Equal(new MusicBedSegment("b.mp3", 0, 30, 25), plan[1]);
    }

    [Fact]
    public void LoopRepeatsTheListFromZeroUntilTheBedIsCovered()
    {
        var plan = MusicBedPlan.Build(new[] { "a.mp3" }, new[] { 10.0 },
            firstFileOffsetSec: 4, bedDurationSec: 20, loop: true, startDelaySec: 0);

        // 6 s of the first pass, then full 10 s repeats, then the 4 s remainder.
        Assert.Equal(new[] { 6.0, 10.0, 4.0 }, new List<double> { plan[0].DurationSec, plan[1].DurationSec, plan[2].DurationSec });
        Assert.Equal(0.0, plan[1].FileOffsetSec);
        Assert.Equal(16.0, plan[2].OutputStartSec, 6);
    }

    [Fact]
    public void LocateMapsOutputTimeToFileAndPosition()
    {
        var plan = MusicBedPlan.Build(new[] { "a.mp3", "b.mp3" }, new[] { 30.0, 100.0 }, 10, 50, false, 5);

        Assert.Null(MusicBedPlan.Locate(plan, 4.99));                 // before the bed starts
        var inA = MusicBedPlan.Locate(plan, 7.5)!.Value;
        Assert.Equal("a.mp3", inA.Path);
        Assert.Equal(12.5, inA.PositionSec, 6);                        // 10 s offset + 2.5 s in
        var inB = MusicBedPlan.Locate(plan, 30.0)!.Value;
        Assert.Equal("b.mp3", inB.Path);
        Assert.Equal(5.0, inB.PositionSec, 6);
        Assert.Null(MusicBedPlan.Locate(plan, 55.0));                  // after the bed ends
    }

    [Fact]
    public void UnknownLengthCoversTheRemainder()
    {
        var plan = MusicBedPlan.Build(new[] { "a.mp3" }, new[] { 0.0 }, 3, 40, false, 0);
        Assert.Single(plan);
        Assert.Equal(40.0, plan[0].DurationSec, 6);
    }
}
