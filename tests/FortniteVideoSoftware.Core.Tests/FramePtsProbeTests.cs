using FortniteVideoSoftware.Core.Media;
using Xunit;

namespace FortniteVideoSoftware.Core.Tests;

/// <summary>FRAMESNAP_01 — Video-Merger-Migration.md P2.2 (unit part; T2.2a–c run in the merge harness).</summary>
public class FramePtsProbeTests
{
    [Fact]
    public void Parse_SortsAndMakesRelativeToContainerStart()
    {
        const string json = """
            { "frames": [
                { "best_effort_timestamp_time": "1.033367" },
                { "best_effort_timestamp_time": "1.000000", "side_data_list": [ {} ] },
                { "best_effort_timestamp_time": "1.016683" },
                { "best_effort_timestamp_time": "N/A" } ],
              "format": { "start_time": "1.000000" } }
            """;
        var pts = FramePtsProbe.Parse(json);
        Assert.Equal(new long[] { 0, 16683, 33367 }, pts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void Parse_GarbageIsEmpty(string? json) => Assert.Empty(FramePtsProbe.Parse(json));

    [Fact]
    public void IntroCut_IsPtsOfFirstKeptFrame_EvenForVfr()
    {
        long[] vfr = { 0, 16000, 35000, 49000, 70000, 83000, 101000, 118000 };
        Assert.Equal(101000, FramePtsProbe.IntroCutUs(vfr, 6));
        Assert.Equal(0, FramePtsProbe.IntroCutUs(vfr, 0));
        Assert.Null(FramePtsProbe.IntroCutUs(vfr, 8));
    }

    [Theory]
    [InlineData(60000, 1001)]   // 59.94
    [InlineData(60, 1)]
    [InlineData(30, 1)]
    public void TrimStart_KeepsCutFrame_NeverPreviousFrame(int num, int den)
    {
        double frame = (double)den / num;
        double cut = 6 * frame;
        double start = MergerWorker.TrimStartSec(cut);
        Assert.True(start <= cut);
        Assert.True(start > 5 * frame);
        Assert.Equal(0, MergerWorker.TrimStartSec(0));
    }
}
