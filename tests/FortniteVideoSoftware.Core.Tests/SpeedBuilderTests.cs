using System;
using System.Collections.Generic;
using FortniteVideoSoftware.Core.Media;
using Xunit;

namespace FortniteVideoSoftware.Core.Tests;

public class SpeedBuilderTests
{
    [Fact]
    public void BuildAtempoChain_NormalSpeed_EmitsNoFilter()
    {
        // AVSYNC_01 — 1.0x is a TRUE no-op and must emit NO filter. FFmpeg's atempo
        // runs its WSOLA overlap-add even at rate 1.0 and displaces sharp attacks
        // by up to ~19 ms (measured by the MediaPipelineChecks A/V sync drift check).
        var chain = GranularSpeedBuilder.BuildAtempoChain(1.0);
        Assert.Empty(chain);
    }

    [Theory]
    [InlineData(0.5, "atempo=0.5000")]
    [InlineData(0.75, "atempo=0.7500")]
    [InlineData(1.5, "atempo=1.5000")]
    [InlineData(2.0, "atempo=2.0000")]
    public void BuildAtempoChain_WithinFfmpegNativeBounds_EmitsSingleFilter(double speed, string expected)
    {
        var chain = GranularSpeedBuilder.BuildAtempoChain(speed);
        Assert.Single(chain);
        Assert.Equal(expected, chain[0]);
    }

    [Fact]
    public void BuildAtempoChain_SlowdownBeyondHalf_ChainsHalves()
    {
        // 0.25x -> 0.5 * 0.5
        var chain025 = GranularSpeedBuilder.BuildAtempoChain(0.25);
        Assert.Equal(new[] { "atempo=0.5", "atempo=0.5000" }, chain025);

        // 0.125x -> 0.5 * 0.5 * 0.5
        var chain0125 = GranularSpeedBuilder.BuildAtempoChain(0.125);
        Assert.Equal(new[] { "atempo=0.5", "atempo=0.5", "atempo=0.5000" }, chain0125);
    }

    [Fact]
    public void BuildAtempoChain_SpeedupBeyondDouble_ChainsDoubles()
    {
        // 4.0x -> 2.0 * 2.0
        var chain4 = GranularSpeedBuilder.BuildAtempoChain(4.0);
        Assert.Equal(new[] { "atempo=2.0", "atempo=2.0000" }, chain4);

        // 8.0x -> 2.0 * 2.0 * 2.0
        var chain8 = GranularSpeedBuilder.BuildAtempoChain(8.0);
        Assert.Equal(new[] { "atempo=2.0", "atempo=2.0", "atempo=2.0000" }, chain8);

        // 3.0x -> 2.0 * 1.5
        var chain3 = GranularSpeedBuilder.BuildAtempoChain(3.0);
        Assert.Equal(new[] { "atempo=2.0", "atempo=1.5000" }, chain3);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(-50.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void BuildAtempoChain_ZeroNegativeOrNonFinite_FallsBackToNormalSpeedWithoutHanging(double invalidSpeed)
    {
        // ISSUE_04 — zero or negative speeds previously caused infinite while loops.
        // Must safely return immediately; normal speed needs no filter (AVSYNC_01).
        var chain = GranularSpeedBuilder.BuildAtempoChain(invalidSpeed);
        Assert.Empty(chain);
    }

    [Fact]
    public void BuildAtempoChain_ExtremeSpeeds_ClampsSafely()
    {
        // Lower limit: 0.01x
        var chainTiny = GranularSpeedBuilder.BuildAtempoChain(0.00001);
        Assert.NotEmpty(chainTiny);
        Assert.Equal("atempo=0.6400", chainTiny[^1]); // 0.5^6 * 0.64 = 0.01

        // Upper limit: 100.0x
        var chainHuge = GranularSpeedBuilder.BuildAtempoChain(999999.0);
        Assert.NotEmpty(chainHuge);
        Assert.Equal("atempo=1.5625", chainHuge[^1]); // 2^6 * 1.5625 = 100.0
    }

    [Fact]
    public void Build_FreezeNearEndOfVideo_GeneratesValidGraphWithSamplingBackoff()
    {
        // 10s video with 2.0s freeze placed at the end (10s)
        var segments = new List<SpeedSegment>
        {
            new SpeedSegment(10000, 12000, 0.0)
        };

        var (filterGraph, videoLabel, _, audioLabel, finalDuration, _) =
            GranularSpeedBuilder.Build(10000.0, segments, baseSpeed: 1.0, needHudBranch: false);

        Assert.NotEmpty(filterGraph);
        Assert.NotEmpty(videoLabel);
        Assert.Equal(12.0, finalDuration, 1);
        Assert.True(filterGraph.Contains("tpad=stop_mode=clone") || filterGraph.Contains("loop="));
    }

    [Fact]
    public void Build_CombinedSlowMotionAndFreeze_CalculatesAccurateDurationsAndChains()
    {
        // 10s video:
        // [0..2s] @ 1.0x = 2s output
        // [2..4s] @ 0.1x = 20s output
        // [4..6s] @ 1.0x = 2s output
        // [6s] freeze 1.5s = 1.5s output
        // [6..10s] @ 1.0x = 4s output
        // Expected total = 29.5s
        var segments = new List<SpeedSegment>
        {
            new SpeedSegment(2000, 4000, 0.1),
            new SpeedSegment(6000, 7500, 0.0)
        };

        var (filterGraph, videoLabel, _, audioLabel, finalDuration, _) =
            GranularSpeedBuilder.Build(10000.0, segments, baseSpeed: 1.0, needHudBranch: false);

        Assert.NotEmpty(filterGraph);
        Assert.Equal(29.5, finalDuration, 1);
        Assert.Contains("setpts='PTS/0.1000'", filterGraph);
    }

    [Fact]
    public void Build_FullTimelineRubberbandSpread_CalculatesAccurateDurationWithoutHanging()
    {
        // Rubberband spread across full 10s clip at 0.25x
        var segments = new List<SpeedSegment>
        {
            new SpeedSegment(0, 10000, 0.25)
        };

        var (filterGraph, videoLabel, _, audioLabel, finalDuration, _) =
            GranularSpeedBuilder.Build(10000.0, segments, baseSpeed: 1.0, needHudBranch: false);

        Assert.NotEmpty(filterGraph);
        Assert.Equal(40.0, finalDuration, 1);
        Assert.Contains("setpts='PTS/0.2500'", filterGraph);
    }
}
