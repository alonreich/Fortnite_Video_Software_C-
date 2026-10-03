using System;
using System.Linq;
using System.Text.RegularExpressions;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>MERGEGRAPH_01 — Video-Merger-Migration.md P7.1 (T7.1a; T7.1b runs in the container harness).</summary>
public class MergeClipGraphTests
{
    private const string Canvas = "scale=1920:1080:flags=lanczos,setsar=1";
    private const string MemeCanvas = "scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,format=yuv420p";

    private static MergeClipGraphResult Graph(int clip, EdlEffects fx, params MergeMemeInput[] memes)
        => MergeClipGraph.Build(clip, $"[{clip}:v]", $"[{clip}:a]", 0.1, 4.0, fx, 1.0, Canvas, MemeCanvas, memes);

    private static readonly EdlEffects Ramp = new() { Speed = new[] { new EdlSpeedSegment(1_000_000, 2_000_000, 0.5) } };

    [Fact]
    public void TwoClipsInOneGraph_NeverShareALabel()
    {
        var a = Graph(0, Ramp);
        var b = Graph(1, Ramp);
        string all = string.Join(";", a.Filters.Concat(b.Filters));
        // Every OUTPUT pad (the trailing labels of a filter statement) must be unique across the graph.
        var outputs = all.Split(';')
            .SelectMany(stmt => Regex.Match(stmt, @"(\[[A-Za-z0-9_:]+\])+$").Value.Split(']', StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        Assert.NotEmpty(outputs);
        Assert.Equal(outputs.Count, outputs.Distinct().Count());
        Assert.All(Regex.Matches(all, @"\[([A-Za-z_][A-Za-z0-9_]*)\]").Select(m => m.Groups[1].Value),
            l => Assert.True(l.StartsWith("c0_") || l.StartsWith("c1_"), l));
        Assert.StartsWith("[0:v]trim=start=0.099500:end=4.000000,setpts=PTS-STARTPTS", a.Filters[0]);   // FRAMESNAP epsilon
    }

    [Fact]
    public void Duration_IsBodyPlusMemes_AndMatchesTheCompositeTimeline()
    {
        var fx = Ramp with { Memes = new[] { new EdlMeme("m", "x.png", EdlMemePlacement.Mid, 3_000_000, 1.5) } };
        var g = Graph(0, fx, new MergeMemeInput(5, true, false, 1.5, 2.9));
        Assert.Equal(1, g.MemeCount);
        // 3.9 s kept + 1 s (1 s at 0.5x) + 1.5 s meme.
        Assert.Equal(6.4, g.DurationSec, 2);
    }

    [Fact]
    public void MemeOrder_StartLeads_MidSplits_EndTrails()
    {
        var g = Graph(0, EdlEffects.None,
            new MergeMemeInput(7, false, true, 1.0, 3.9),   // end
            new MergeMemeInput(5, true, false, 2.0, 0),     // start
            new MergeMemeInput(6, true, false, 1.0, 1.95)); // mid
        string concat = g.Filters.Last();
        Assert.Matches(@"^\[c0_m\d_v\]\[c0_m\d_a\]\[c0_pv0\]\[c0_pa0\]\[c0_m\d_v\]\[c0_m\d_a\]\[c0_pv1\]\[c0_pa1\]\[c0_m\d_v\]\[c0_m\d_a\]concat=n=5:v=1:a=1", concat);
        // The image meme has no sound: silence of its exact length.
        Assert.Contains(g.Filters, f => f.StartsWith("anullsrc=r=48000:cl=stereo,atrim=duration=2.000000"));
        // The video meme keeps its own audio, padded to length.
        Assert.Contains(g.Filters, f => f.StartsWith("[7:a]atrim=duration=1.000000") && f.Contains("apad"));
        Assert.Equal(3.9 + 4.0, g.DurationSec, 2);
    }

    [Fact]
    public void MemeLevel_MatchesTheGameplayItInterrupts()   // MEMELEVEL_02
    {
        // Meme -14 LUFS into -20 LUFS gameplay → -6 dB. Clamped; either side unmeasured → as recorded.
        Assert.Equal(-6.0, MemeLoudness.GainFor(-14, -20), 6);
        Assert.Equal(6.0, MemeLoudness.GainFor(-26, -20), 6);
        Assert.Equal(MemeLoudness.MaxGainDb, MemeLoudness.GainFor(-60, -10), 6);
        Assert.Equal(MemeLoudness.MinGainDb, MemeLoudness.GainFor(0, -40), 6);
        Assert.Equal(0.0, MemeLoudness.GainFor(null, -20), 6);
        Assert.Equal(0.0, MemeLoudness.GainFor(-20, null), 6);
        // No per-meme limiter: the always-on safety limiter on the final mix covers memes.
        Assert.Equal(",volume=1.9953", MemeLoudness.Chain(6.0));
        Assert.Equal("", MemeLoudness.Chain(0));

        var g = Graph(0, EdlEffects.None, new MergeMemeInput(7, false, true, 1.0, 1.95, GainDb: 6.0));
        Assert.Contains(g.Filters, f => f.StartsWith("[7:a]atrim=duration=1.000000") && f.Contains(",volume=1.9953,apad") && !f.Contains("alimiter"));
    }

    [Fact]
    public void MemeSplices_AreDeClicked()   // SPLICE_03
    {
        var g = Graph(0, EdlEffects.None, new MergeMemeInput(7, false, true, 1.0, 1.95));
        // The meme's own audio and both gameplay pieces around it carry the 8 ms fade pair.
        Assert.Contains(g.Filters, f => f.StartsWith("[7:a]") && f.Contains("afade=t=in:st=0:d=0.0080") && f.Contains("afade=t=out"));
        Assert.Contains(g.Filters, f => f.Contains("[c0_pa0_in]atrim") && f.Contains("afade=t=in:st=0:d=0.0080"));
        Assert.Contains(g.Filters, f => f.Contains("[c0_pa1_in]atrim") && f.Contains("afade=t=out"));
        Assert.Equal("", MemeLoudness.SpliceFade(0.02));
    }

    [Fact]
    public void NoAudioInput_StillProducesAnAudioStream()
    {
        var g = MergeClipGraph.Build(2, "[2:v]", null, 0, 3, Ramp, 1.0, Canvas, MemeCanvas, Array.Empty<MergeMemeInput>());
        Assert.DoesNotContain(g.Filters, f => f.Contains("[2:a]"));
        Assert.Equal("[c2_body_a]", g.AudioLabel);
    }
}
