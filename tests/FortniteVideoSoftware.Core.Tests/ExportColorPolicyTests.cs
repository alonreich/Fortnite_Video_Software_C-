// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Media;
using Xunit;

namespace FortniteVideoSoftware.Core.Tests;

/// <summary>COLOR_01 — SDR BT.709 TV range, tagged, from any source.</summary>
public class ExportColorPolicyTests
{
    private static VideoColorInfo Probe(string json) => VideoColorInfo.FromStream(JsonNode.Parse(json));

    [Fact]
    public void Sdr709LimitedPassesThroughUntouched()
    {
        var info = Probe("""{"pix_fmt":"yuv420p","color_primaries":"bt709","color_transfer":"bt709","color_space":"bt709","color_range":"tv"}""");
        Assert.Null(ExportColorPolicy.BuildConversionChain(info, hasZscale: true, out _, out string? degraded));
        Assert.Null(degraded);
    }

    [Fact]
    public void UntaggedSourcePassesThroughUntouched()
    {
        var info = Probe("""{"pix_fmt":"yuv420p"}""");
        Assert.Null(ExportColorPolicy.BuildConversionChain(info, true, out _, out _));
    }

    [Fact]
    public void Hdr10IsToneMappedWhenZscaleExists()
    {
        var info = Probe("""{"pix_fmt":"yuv420p10le","color_primaries":"bt2020","color_transfer":"smpte2084","color_space":"bt2020nc"}""");
        Assert.True(info.IsHdr);
        Assert.Equal(ExportColorPolicy.HdrToneMapChain, ExportColorPolicy.BuildConversionChain(info, true, out _, out _));
    }

    [Fact]
    public void HdrWithoutZscaleDegradesLoudlyInsteadOfFailing()
    {
        var info = Probe("""{"color_transfer":"arib-std-b67"}""");
        Assert.Null(ExportColorPolicy.BuildConversionChain(info, false, out _, out string? degraded));
        Assert.NotNull(degraded);
    }

    [Fact]
    public void FullRangeIsConvertedToTvRange()
    {
        var info = Probe("""{"pix_fmt":"yuvj420p","color_range":"pc"}""");
        string? chain = ExportColorPolicy.BuildConversionChain(info, true, out _, out _);
        Assert.Equal("colorspace=all=bt709:iall=bt709:irange=pc:range=tv:format=yuv420p", chain);
    }

    [Fact]
    public void Bt601MatrixIsConvertedTo709()
    {
        var info = Probe("""{"pix_fmt":"yuv420p","color_space":"smpte170m","color_range":"tv"}""");
        Assert.Contains("iall=bt601-6-525", ExportColorPolicy.BuildConversionChain(info, true, out _, out _));
    }

    [Fact]
    public void ConversionForcesTheCpuFilterRouteAndSdrKeepsTheGpuRoute()
    {
        string sdr = "[0:v]scale=1080:1920:flags=lanczos,format=yuv420p[v]";
        Assert.True(ExportVideoPipeline.Create("h264_nvenc", sdr).UsesGpuFrames);

        string hdr = "[0:v]" + ExportColorPolicy.HdrToneMapChain + ",scale=1080:1920:flags=lanczos[v]";
        Assert.False(ExportVideoPipeline.Create("h264_nvenc", hdr).UsesGpuFrames);
    }

    [Fact]
    public void EveryEncoderRouteTagsTheOutput()
    {
        var args = TwoPassEncoding.PassArgs(4000, 1, "p");
        Assert.Contains("-color_primaries", args);
        Assert.Contains("-color_range", args);
    }
}
