using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// DUCKMB_01 / VOGATE_01 / PEAKSAFE_01 / CLIPLEVEL_01 — the shared audio mix, pinned. The filter
/// graphs these produce were also run through a real ffmpeg (see docs/02 §2-3 for the measurements).
/// </summary>
public class AudioMixTests
{
    private static string Graph(JsonObject? cfg, string? voice = null, string? pulse = null)
    {
        var (chains, final) = AudioFilterChain.Build(cfg, 0, 10, 1.0, false, 0, null, 48000,
            new List<MusicTrack> { new("music.mp3", 0, 10.0) }, 1, 10.0, "[0:a]",
            voiceOverLabel: voice, voiceProtectMusicPulse: pulse);
        Assert.Equal("[a_music_prepared]", final);
        return string.Join(";", chains);
    }

    [Fact]
    public void Defaults_AreMultibandDuckingAndDynamicCarving()
    {
        string g = Graph(null);
        Assert.Contains("acrossover=split='250 2900'[mus_low][mus_mid][mus_high]", g);
        Assert.Contains("[mus_mid][trig_0]sidechaincompress=threshold=0.1:ratio=4:attack=10:release=400", g);
        Assert.Contains("[mus_high][trig_1]sidechaincompress=threshold=0.1:ratio=4", g);
        Assert.Contains("[mus_mid_c0][trig_2]sidechaincompress=threshold=0.1:ratio=2.5", g);
        Assert.Contains("[mus_low][mus_mid_c2][mus_high_c1]amix=inputs=3", g);
        Assert.Contains("asplit=3[trig_0][trig_1][trig_2]", g);
        Assert.DoesNotContain("equalizer=f=2000", g);   // the static carve is gone
        Assert.DoesNotContain("atempo", g);              // music never changes speed
    }

    [Fact]
    public void DuckingAndCarvingOff_LeaveNoTraceInTheGraph()   // DUCKOFF_01
    {
        string g = Graph(new JsonObject { ["ducking_enabled"] = false, ["carving_enabled"] = false });
        foreach (var needle in new[] { "sidechaincompress", "acrossover", "trig_", "asplit", "agate", "equalizer" })
            Assert.DoesNotContain(needle, g);
        Assert.Contains("amix=inputs=2", g);
    }

    [Fact]
    public void EachSwitchAlone_BuildsOnlyItsOwnStages()
    {
        string duck = Graph(new JsonObject { ["ducking_enabled"] = true, ["carving_enabled"] = false });
        Assert.Contains("[mus_low][mus_mid_c0][mus_high_c1]amix=inputs=3", duck);
        Assert.DoesNotContain("ratio=2.5", duck);

        string carve = Graph(new JsonObject { ["ducking_enabled"] = false, ["carving_enabled"] = true });
        Assert.Contains("[mus_mid][trig_0]sidechaincompress=threshold=0.1:ratio=2.5", carve);
        Assert.Contains("[mus_low][mus_mid_c0][mus_high]amix=inputs=3", carve);
        Assert.DoesNotContain("ratio=4", carve);
    }

    [Fact]
    public void LegacyBypassRatio_StillMeansDuckingOff()
    {
        string g = Graph(new JsonObject { ["ducking_ratio"] = SidechainCompressNode.BypassRatio, ["carving_enabled"] = false });
        Assert.DoesNotContain("sidechaincompress", g);
    }

    [Fact]
    public void VoiceProtection_IsGatedToTheTakes()   // VOGATE_01 / VOPRIO_01
    {
        const string pulse = "clip(1,0,1)";
        string g = Graph(null, "[5:a]", pulse);
        Assert.Contains("asplit=2[vpm_dry][vpm_wet]", g);
        Assert.Contains($"[vpm_wet]{AudioFilterChain.VoiceCarveEq},volume='{pulse}':eval=frame", g);
        Assert.Contains($"[vpm_dry]volume='1-({pulse})':eval=frame", g);
        Assert.Contains($"volume='1.0-0.85*({pulse})':eval=frame[a_bg_voice_protected]", g);
        // The voice is mixed into the trigger: a take also pushes the music down.
        Assert.Contains("[game_leveled_base][5:a]amix", g);
        // Voice protection runs BEFORE the band split.
        Assert.True(g.IndexOf("[a_bg_voice_protected]acrossover", System.StringComparison.Ordinal) < 0
                    || g.IndexOf("vpm_dry", System.StringComparison.Ordinal) < g.IndexOf("acrossover", System.StringComparison.Ordinal));
    }

    [Fact]
    public void SafetyLimiter_NeverAutoLevels_AndIsOversampled()   // PEAKSAFE_01
    {
        string f = PeakSafety.SafetyLimiterFilter();
        Assert.StartsWith("aresample=192000,alimiter=limit=-2.3dB", f);
        Assert.Contains("level=disabled", f);
        Assert.EndsWith("aresample=48000", f);
    }

    [Fact]
    public void Tamer_SitsNineLuAboveTheMeasuredLevel()
    {
        Assert.Null(PeakSafety.TamerFilter(null));
        Assert.Null(PeakSafety.TamerFilter(-70));
        Assert.StartsWith("acompressor=threshold=-14.00dB:ratio=6", PeakSafety.TamerFilter(-23));
        Assert.Equal(PeakSafety.TamerMaxThresholdDb, PeakSafety.TamerThresholdDb(0));
        Assert.Equal(PeakSafety.TamerMinThresholdDb, PeakSafety.TamerThresholdDb(-60));
    }

    [Fact]
    public void ClipMatch_PullsTowardTheQueueMedian()   // CLIPLEVEL_01
    {
        var gains = MergerWorker.ClipMatchGains(new double?[] { -20, -26, null, -14 }, enabled: true);
        Assert.Equal(new[] { 0.0, 6.0, 0.0, -6.0 }, gains);
        Assert.All(MergerWorker.ClipMatchGains(new double?[] { -20, -40 }, enabled: false), g => Assert.Equal(0.0, g));
        Assert.Equal(new[] { -12.0, 12.0 }.Select(x => x).ToArray(),
            MergerWorker.ClipMatchGains(new double?[] { 0, -60 }, enabled: true).Select(x => x).ToArray());
    }
}

public class AudioGraphPrunerTests   // PREVIEWMIX_01
{
    [Fact]
    public void KeepsOnlyTheAudioPath_AndSplitsTheMemeConcat()
    {
        string graph = string.Join(";", new[]
        {
            "[0:v]setpts=PTS/1.1[v_body]",
            "[0:a]atempo=1.1,volume='if(lt(t,1),0.5;1)':eval=frame[a_body]",   // quoted ';' must not split
            "[v_body]split=2[v0][v1]",
            "[a_body]asplit=2[a0][a1]",
            "[3:v]scale=320:180[m_v]",
            "[3:a]volume=0.5[m_a]",
            "[v0][a0][m_v][m_a][v1][a1]concat=n=3:v=1:a=1[v_final][a_final]",
            "[a_final]alimiter=level=disabled[a_out]",
            "[v_final]format=yuv420p[v_out]",
        });
        string pruned = AudioGraphPruner.Prune(graph, "[a_out]");
        Assert.DoesNotContain("[0:v]", pruned);
        Assert.DoesNotContain("split=2[v0]", pruned);
        Assert.DoesNotContain("[3:v]", pruned);
        Assert.Contains("[a0][m_a][a1]concat=n=3:v=0:a=1[a_final]", pruned);
        Assert.Contains("volume='if(lt(t,1),0.5;1)'", pruned);
        Assert.EndsWith("[a_final]alimiter=level=disabled[a_out]", pruned);
    }

    [Fact]
    public void OrphanAudioPads_AreSunk()
    {
        string graph = "[0:a]asplit=2[a][b];[a]anull[out];[b]showwaves[w]";
        string pruned = AudioGraphPruner.Prune(graph, "[out]");
        Assert.Contains("[0:a]asplit=2[a][b];[b]anullsink", pruned);
        Assert.DoesNotContain("showwaves", pruned);
    }

    [Fact]
    public void MixMap_AddsIntroPadAndMemesBeforeThePlayhead()
    {
        var map = new AudioPreviewMap(0.1, 1.0, new List<(double, double)> { (0.1, 2.0), (5.1, 1.5) });
        Assert.Equal(0.1 + 1.0 + 0.0 + 2.0, map.MixSecFor(0), 6);           // head meme already played
        Assert.Equal(0.1 + 1.0 + 4.0 + 2.0, map.MixSecFor(4.0), 6);         // AT the cut: second meme not yet
        Assert.Equal(0.1 + 1.0 + 5.0 + 2.0 + 1.5, map.MixSecFor(5.0), 6);   // after it
    }
}

public class MixStrengthTests   // DUCKSTRENGTH_01
{
    [Fact]
    public void MiddleIsTheTunedValue_AndEveryStepIsSmallAndMonotonic()
    {
        Assert.Equal(SidechainCompressNode.TunedRatio, SidechainCompressNode.RatioFor(SidechainCompressNode.TunedRatio, 50), 9);
        Assert.Equal(SidechainCompressNode.CarveRatio, SidechainCompressNode.RatioFor(SidechainCompressNode.CarveRatio, 50), 9);
        double prev = SidechainCompressNode.RatioFor(SidechainCompressNode.TunedRatio, 0);
        for (int s = 1; s <= 100; s++)
        {
            double r = SidechainCompressNode.RatioFor(SidechainCompressNode.TunedRatio, s);
            Assert.True(r > prev);
            Assert.True(r / prev < 1.04, $"step {s} jumps {r / prev:F3}x");   // no sudden jumps
            prev = r;
        }
        Assert.InRange(SidechainCompressNode.RatioFor(SidechainCompressNode.TunedRatio, 0), 2.0, 2.1);
        Assert.InRange(SidechainCompressNode.RatioFor(SidechainCompressNode.TunedRatio, 100), 9.4, 9.6);
    }

    [Fact]
    public void StrengthsReachTheGraph_AndDefaultIsUnchanged()
    {
        string Graph(JsonObject cfg) => string.Join(";", AudioFilterChain.Build(cfg, 0, 10, 1.0, false, 0, null, 48000,
            new List<MusicTrack> { new("m.mp3", 0, 10) }, 1, 10.0, "[0:a]").chains);
        Assert.Contains("ratio=4:", Graph(new JsonObject()));
        Assert.Contains("ratio=2.5:", Graph(new JsonObject()));
        string strong = Graph(new JsonObject { ["ducking_strength"] = 100.0, ["carving_strength"] = 0.0 });
        Assert.Contains("ratio=9.485:", strong);
        Assert.Contains("ratio=1.53:", strong);
    }
}
