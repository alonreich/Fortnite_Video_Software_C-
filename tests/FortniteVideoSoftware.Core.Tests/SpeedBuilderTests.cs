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
}
