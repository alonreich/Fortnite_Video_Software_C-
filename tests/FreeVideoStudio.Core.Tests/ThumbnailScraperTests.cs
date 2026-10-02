using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>SCRAPER_01 / SCRAPER_02 — the intro tag and the merged timeline shared by preview, music and export.</summary>
public class ThumbnailScraperTests
{
    private static MergeClipSource Clip(string p, double dur, double intro = 0.1) => new(p, dur, intro);

    [Fact]
    public void Tag_RoundTripsThroughFfprobeTags()
    {
        string[] args = IntroTag.OutputArgs(0.1);
        Assert.Equal(["-metadata", "fvs_intro_sec=0.100", "-movflags", "+faststart+use_metadata_tags"], args);
        var tags = new JsonObject { ["major_brand"] = "isom", ["fvs_intro_sec"] = "0.100" };
        Assert.Equal(0.1, IntroTag.Read(tags, 10), 6);
    }

    [Fact]
    public void Tag_NoIntro_KeepsPlainFaststart()
        => Assert.Equal(["-movflags", "+faststart"], IntroTag.OutputArgs(0));

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("abc", 0)]
    [InlineData("-0.1", 0)]
    [InlineData("5.0", 0)]
    [InlineData("0.100", 0.1)]
    public void Tag_Validation(string? raw, double expected)
        => Assert.Equal(expected, IntroTag.Validate(raw, 10), 6);

    [Fact]
    public void Tag_LongerThanHalfTheFile_IsRejected()
        => Assert.Equal(0, IntroTag.Validate("0.100", 0.15));

    [Fact]
    public void Tag_Absent_IsZero()
        => Assert.Equal(0, IntroTag.Read(new JsonObject { ["encoder"] = "Lavf" }, 10));

    [Fact]
    public void Scraper_RemovesIntroFromClips2ToN_KeepsClip1()
    {
        var tl = MergedTimeline.Build([Clip("a", 10), Clip("b", 10), Clip("c", 10)], scraperEnabled: true, customThumbnail: false);
        Assert.Equal(0, tl.Clips[0].ContentStartSec);
        Assert.Equal(0.1, tl.Clips[1].ContentStartSec, 6);
        Assert.Equal(0.1, tl.Clips[2].ContentStartSec, 6);
        Assert.Equal(29.8, tl.TotalSec, 6);
        Assert.Equal(10, tl.Clips[1].MergedStartSec, 6);
        Assert.Equal(19.9, tl.Clips[2].MergedStartSec, 6);
        Assert.Equal(0, tl.SyntheticIntroSec);
    }

    [Fact]
    public void ScraperOff_KeepsEverything()
    {
        var tl = MergedTimeline.Build([Clip("a", 10), Clip("b", 10)], false, false);
        Assert.Equal(20, tl.TotalSec, 6);
        Assert.All(tl.Clips, c => Assert.Equal(0, c.RemovedIntroSec));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CustomThumbnail_RemovesClip1Intro_WithOrWithoutScraper(bool scraper)
    {
        var tl = MergedTimeline.Build([Clip("a", 10), Clip("b", 10)], scraper, customThumbnail: true);
        Assert.Equal(0.1, tl.Clips[0].ContentStartSec, 6);
        Assert.Equal(scraper ? 0.1 : 0, tl.Clips[1].ContentStartSec, 6);
        Assert.Equal(IntroTag.StandardIntroSec, tl.SyntheticIntroSec);
    }

    [Fact]
    public void UntaggedClips_AreNeverTouched()
    {
        var tl = MergedTimeline.Build([Clip("a", 10, 0), Clip("b", 10, 0)], true, true);
        Assert.Equal(20, tl.TotalSec, 6);
    }

    [Fact]
    public void Locate_And_ToMerged_AreInverse_AndSkipTheIntro()
    {
        var tl = MergedTimeline.Build([Clip("a", 10), Clip("b", 10)], true, false);
        var (clip, src) = tl.Locate(10.0);
        Assert.Equal(1, clip);
        Assert.Equal(0.1, src, 6);
        Assert.Equal(10.0, tl.ToMerged(1, 0.05), 6);
        Assert.Equal(15.0, tl.ToMerged(1, tl.Locate(15.0).SourceSec), 6);
    }

    [Fact]
    public void Remap_KeepsMusicOnTheSameClipMoment()
    {
        var off = MergedTimeline.Build([Clip("a", 10), Clip("b", 10), Clip("c", 10)], false, false);
        double placed = off.ToMerged(2, 5.0);
        Assert.Equal(25.0, placed, 6);

        var on = MergedTimeline.Build([Clip("a", 10), Clip("b", 10), Clip("c", 10)], true, false);
        double moved = on.Remap(off, placed);
        Assert.Equal(24.8, moved, 6);
        Assert.Equal((2, 5.0), (on.Locate(moved).ClipIndex, Math.Round(on.Locate(moved).SourceSec, 6)));

        Assert.Equal(0, on.Remap(off, 0), 6);
        Assert.Equal(on.TotalSec, on.Remap(off, off.TotalSec), 6);
    }

    [Fact]
    public void Remap_DifferentQueue_IsNotAttempted()
    {
        var a = MergedTimeline.Build([Clip("a", 10), Clip("b", 10)], false, false);
        var b = MergedTimeline.Build([Clip("b", 10), Clip("a", 10)], true, false);
        Assert.False(b.SameQueue(a));
        Assert.Equal(12.0, b.Remap(a, 12.0), 6);
    }

    [Fact]
    public void RemovalThatWouldEmptyAClip_IsRefused()
    {
        var tl = MergedTimeline.Build([Clip("a", 10), Clip("b", 0.12)], true, false);
        Assert.Equal(0, tl.Clips[1].RemovedIntroSec);
    }

    [Fact]
    public void Signature_ChangesWithTheLayout()
    {
        var off = MergedTimeline.Build([Clip("a", 10), Clip("b", 10)], false, false);
        var on = MergedTimeline.Build([Clip("a", 10), Clip("b", 10)], true, false);
        Assert.NotEqual(off.Signature, on.Signature);
        Assert.Equal([0.0, 0.1], on.ContentStarts());
    }
}
