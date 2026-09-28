using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Media;
using Xunit;

namespace FortniteVideoSoftware.Core.Tests;

/// <summary>TIMINGTAG_02 — Video-Merger-Migration.md P1 (T1.1a–c, T1.3a).</summary>
public class ExportTimingTagTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(30, null)]
    [InlineData(null, 45)]
    [InlineData(30, 45)]
    [InlineData(0, 0)]
    public void RoundTrip_AllFadeCombinations(int? fadeIn, int? fadeOut)   // T1.1a
    {
        var t = new ExportTiming(60, 1, 6, fadeIn, fadeOut);
        string s = ExportTimingTag.Format(t);
        Assert.True(ExportTimingTag.TryParse(s, 10, out var back));
        Assert.Equal(t, back);
        Assert.Equal(0.1, back.IntroSec, 9);
    }

    [Fact]
    public void Ntsc_RationalFps_IsExact()
    {
        var t = new ExportTiming(60000, 1001, 6, 60, 60);
        Assert.True(ExportTimingTag.TryParse(ExportTimingTag.Format(t), 10, out var back));
        Assert.Equal(6 * 1001 / 60000.0, back.IntroSec, 12);
    }

    [Fact]
    public void V1Only_FallsBackToSeconds_FadesUnknown()   // T1.1b
    {
        var tags = new JsonObject { ["fvs_intro_sec"] = "0.100" };
        var t = ExportTimingTag.Read(tags, 10);
        Assert.NotNull(t);
        Assert.Equal(0.1, t!.Value.IntroSec, 6);
        Assert.Null(t.Value.FadeInFrames);
        Assert.Null(t.Value.FadeOutFrames);
    }

    [Fact]
    public void V2_WinsOverV1()
    {
        var tags = new JsonObject { ["fvs_intro_sec"] = "0.100", ["fvs_timing"] = "v=2;fps=30/1;intro=3;fadein=15;fadeout=0" };
        var t = ExportTimingTag.Read(tags, 10)!.Value;
        Assert.Equal(3, t.IntroFrames);
        Assert.Equal(15, t.FadeInFrames);
        Assert.Equal(0, t.FadeOutFrames);
    }

    [Theory]
    [InlineData("v=2;fps=60/1;intro=-1")]
    [InlineData("v=2;fps=0/1;intro=6")]
    [InlineData("v=2;fps=60/1;intro=abc")]
    [InlineData("v=3;fps=60/1;intro=6")]
    [InlineData("v=2;intro=6")]
    [InlineData("v=2;fps=60/1;intro=600")]            // 10 s intro, implausible
    [InlineData("v=2;fps=60/1;intro=6;fadein=99999")] // longer than the file
    [InlineData("")]
    public void Garbage_IsRejected(string raw)   // T1.1c
        => Assert.False(ExportTimingTag.TryParse(raw, 10, out _));

    [Fact]
    public void IntroLongerThanHalfTheFile_IsRejected()
        => Assert.False(ExportTimingTag.TryParse("v=2;fps=60/1;intro=60", 1.5, out _));

    [Fact]
    public void UnknownKeys_AreIgnored()
    {
        Assert.True(ExportTimingTag.TryParse("v=2;fps=60/1;intro=6;future=abc", 10, out var t));
        Assert.Equal(6, t.IntroFrames);
    }

    [Fact]
    public void NoTags_IsNull()   // T1.3a
    {
        Assert.Null(ExportTimingTag.Read(new JsonObject { ["encoder"] = "Lavf" }, 10));
        Assert.Null(ExportTimingTag.Read(null, 10));
    }

    [Fact]
    public void OutputArgs_WriteBothKeys()
    {
        var args = IntroTag.OutputArgs(new ExportTiming(60, 1, 6, 30, 0));
        Assert.Equal(["-metadata", "fvs_intro_sec=0.100", "-metadata", "fvs_timing=v=2;fps=60/1;intro=6;fadein=30;fadeout=0", "-movflags", "+faststart+use_metadata_tags"], args);
    }

    [Theory]
    [InlineData("60", 60, 1)]
    [InlineData("30000/1001", 30000, 1001)]
    [InlineData("59.94", 59940, 1000)]
    public void FpsParsing(string text, int num, int den)
    {
        Assert.True(ExportTiming.TryParseFps(text, out int n, out int d));
        Assert.Equal((num, den), (n, d));
    }
}
