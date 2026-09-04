using FortniteVideoSoftware.Core.Media;
using System.Text.Json.Nodes;

namespace FortniteVideoSoftware.App;

public static class Phase4Gate
{
    public static Task<int> RunAsync()
    {
        Console.WriteLine("Testing Phase 4: FFmpeg Math, AudioFilterChain and GranularSpeedBuilder");

        JsonArray lootCrop = new JsonArray(100, 50, 0, 0);
        JsonArray protectedLoot = CanvasMath.ProtectCropDrift("loot", lootCrop);
        if (protectedLoot[0]!.GetValue<int>() != 99 || protectedLoot[2]!.GetValue<int>() != 0)
        {
            Console.WriteLine("Math Error: Loot crop should be -1 width, x unchanged.");
            return Task.FromResult(1);
        }

        JsonArray statsCrop = new JsonArray(100, 50, 0, 0);
        JsonArray protectedStats = CanvasMath.ProtectCropDrift("stats", statsCrop);
        if (protectedStats[0]!.GetValue<int>() != 99 || protectedStats[2]!.GetValue<int>() != 1)
        {
            Console.WriteLine("Math Error: Stats crop should be -1 width, +1 x.");
            return Task.FromResult(1);
        }

        var (duckChains, duckFinalLabel) = AudioFilterChain.Build(
            null, 0, 10.0, 1.0, false, 0, null, 48000,
            new List<MusicTrack> { new MusicTrack("music.mp3", 0, 10.0) },
            1, 10.0, "[0:a]", 0.0);
        string duckFilter = string.Join(";", duckChains);

        var duckChecks = new (string Needle, string Why)[]
        {
            // DUCKOFF_01 — two of these needles were STALE and this gate could never have passed:
            // the shipped code splits at 250 Hz, not 150, and the tuned ratio is
            // SidechainCompressNode.TunedRatio (1.13), not 2.5. Pinned to the constants now so the
            // gate follows a retune instead of silently rotting against hard-coded numbers.
            ("acrossover=split=250",                              "250 Hz music split"),
            ("[mus_high][trig_final]sidechaincompress=",           "ducking applied to the HIGH band only"),
            ($"threshold={SidechainCompressNode.TunedThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
             $":ratio={SidechainCompressNode.TunedRatio.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                                                                   "tuned ducking threshold/ratio"),
            ("[mus_low][mus_high_ducked]amix=",                    "low + ducked-high recombine (:234)"),
            ("[game_trig]highpass=f=200,lowpass=f=3500",           "sidechain trigger band-pass"),
            ("[game_leveled]asplit=2[game_out_pre_raw][game_trig]",    "split happens AFTER levelling, so ducking is source-level independent"),
        };
        foreach (var (needle, why) in duckChecks)
        {
            if (!duckFilter.Contains(needle))
            {
                Console.WriteLine($"Audio Ducking Error: missing '{needle}' — {why}.");
                Console.WriteLine($"Actual chain:\n{duckFilter}");
                return Task.FromResult(1);
            }
        }

        string tempoProbe = duckFilter.Replace("asetpts", "");
        if (tempoProbe.Contains("atempo") || tempoProbe.Contains("setpts"))
        {
            Console.WriteLine("Audio Ducking Error: music chain contains atempo/setpts — background music must stay at 1.0x.");
            Console.WriteLine($"Actual chain:\n{duckFilter}");
            return Task.FromResult(1);
        }

        if (duckFinalLabel != "[a_music_prepared]")
        {
            Console.WriteLine($"Audio Ducking Error: final label was '{duckFinalLabel}', expected '[a_music_prepared]'.");
            return Task.FromResult(1);
        }

        // DUCKOFF_01 — UNCHECKED MUST MEAN ABSENT, NOT NEUTRALISED.
        // "ducking off" used to ship threshold=1/ratio=1 into a sidechaincompress that still ran,
        // behind an acrossover split-and-sum that still ran, fed by a trigger bus that still ran.
        // Level-transparent, but not nothing. This asserts the filters are GONE from the graph,
        // and that carving off removes its equalizer too.
        var (offChains, offFinalLabel) = AudioFilterChain.Build(
            new JsonObject
            {
                ["ducking_enabled"] = false,
                ["carving_enabled"] = false,
                ["ducking_threshold"] = SidechainCompressNode.BypassThreshold,
                ["ducking_ratio"] = SidechainCompressNode.BypassRatio,
            },
            0, 10.0, 1.0, false, 0, null, 48000,
            new List<MusicTrack> { new MusicTrack("music.mp3", 0, 10.0) },
            1, 10.0, "[0:a]", 0.0);
        string offFilter = string.Join(";", offChains);

        var mustBeAbsent = new (string Needle, string Why)[]
        {
            ("sidechaincompress", "the compressor itself"),
            ("acrossover",        "the 250 Hz split that only existed to feed the compressor"),
            ("mus_high",          "the ducked high band"),
            ("mus_low",           "the untouched low band"),
            ("trig_final",        "the sidechain trigger bus"),
            ("game_trig",         "the asplit branch that fed the trigger bus"),
            ("asplit",            "the split itself — nothing consumes a second game copy now"),
            ("agate",             "the trigger gate"),
            ("equalizer",         "the carving EQ"),
        };
        foreach (var (needle, why) in mustBeAbsent)
        {
            if (offFilter.Contains(needle))
            {
                Console.WriteLine($"Audio Bypass Error: '{needle}' is still in the graph with ducking and carving OFF — {why}.");
                Console.WriteLine($"Actual chain:\n{offFilter}");
                return Task.FromResult(1);
            }
        }

        if (offFinalLabel != "[a_music_prepared]")
        {
            Console.WriteLine($"Audio Bypass Error: final label was '{offFinalLabel}', expected '[a_music_prepared]'.");
            return Task.FromResult(1);
        }

        // The music must still REACH the mix — "no processing" is not "no music".
        if (!offFilter.Contains("amix=inputs=2"))
        {
            Console.WriteLine("Audio Bypass Error: music never reaches the final amix with ducking off.");
            Console.WriteLine($"Actual chain:\n{offFilter}");
            return Task.FromResult(1);
        }

        // ══════════════════════════════════════════════════════════════════════════════════
        // CUT_01 — the cut feature's proof. A cut is the mirror of a freeze: it consumes source
        // time and occupies NO output time. These assertions pin the three things that can
        // silently rot: the arithmetic, the agreement between the timeline model and the emitted
        // graph, and the absence of deleted footage from the filter graph.
        // ══════════════════════════════════════════════════════════════════════════════════
        {
            const double ClipMs = 60000;

            // 1. ARITHMETIC. Removing 10s from a 60s clip must leave exactly 50s.
            var oneCut = new List<OutputTimeline.Cut> { new(20, 30) };
            var tl = OutputTimeline.Create(ClipMs, null, 1.0, 0, null, oneCut);
            if (Math.Abs(tl.TotalOutputSeconds - 50.0) > 0.01)
            {
                Console.WriteLine($"Cut Error: 60s clip minus a 10s cut produced {tl.TotalOutputSeconds:F3}s, expected 50.000s.");
                return Task.FromResult(1);
            }
            if (Math.Abs(tl.RemovedSourceSeconds - 10.0) > 0.01 || Math.Abs(tl.SurvivingSourceSeconds - 50.0) > 0.01)
            {
                Console.WriteLine($"Cut Error: removed/surviving reported {tl.RemovedSourceSeconds:F3}/{tl.SurvivingSourceSeconds:F3}, expected 10/50.");
                return Task.FromResult(1);
            }

            // 2. THE JOIN. A moment INSIDE a cut maps to the instant the footage resumes, and
            //    everything after a cut slides earlier by exactly the cut's length. THIS is why
            //    voice-overs and memes stay aligned across a cut without touching their code.
            if (Math.Abs(tl.SourceToOutput(25.0) - tl.SourceToOutput(30.0)) > 0.01)
            {
                Console.WriteLine("Cut Error: a source moment inside a cut did not map to the join.");
                return Task.FromResult(1);
            }
            if (Math.Abs(tl.SourceToOutput(40.0) - 30.0) > 0.01)
            {
                Console.WriteLine($"Cut Error: source 40s after a 10s cut mapped to {tl.SourceToOutput(40.0):F3}s, expected 30.000s.");
                return Task.FromResult(1);
            }

            // 3. THE PREVIEW COMPOSITION can never land on a deleted frame.
            for (int i = 0; i <= 500; i++)
            {
                double outSec = tl.TotalOutputSeconds * i / 500.0;
                double src = tl.NextSurvivingSource(tl.OutputToSourceRelative(outSec));
                if (tl.IsCutAtSource(src))
                {
                    Console.WriteLine($"Cut Error: preview mapping landed inside a cut at output {outSec:F3}s (source {src:F3}s).");
                    return Task.FromResult(1);
                }
            }

            // 4. NORMALISATION. Overlapping and near-touching cuts must merge, or the chunk walk
            //    emits chunks out of source order and every mapping built on it is corrupt.
            var messy = new List<OutputTimeline.Cut> { new(10, 20), new(15, 25), new(25.1, 30), new(40, 40.01) };
            var clean = OutputTimeline.NormalizeCuts(messy, 60.0);
            if (clean.Count != 1 || Math.Abs(clean[0].StartSec - 10.0) > 0.001 || Math.Abs(clean[0].EndSec - 30.0) > 0.001)
            {
                Console.WriteLine($"Cut Error: normalisation produced {clean.Count} cut(s); expected one 10-30s cut " +
                                  "(overlap merged, 0.1s gap merged, sub-frame sliver dropped).");
                return Task.FromResult(1);
            }

            // 5. THE GRAPH AGREES WITH THE MODEL, and never trims deleted footage.
            var withSpeed = new List<SpeedSegment> { new SpeedSegment(30000, 40000, 0.5) };
            var cutList = new List<OutputTimeline.Cut> { new(10, 15) };
            var (cutGraph, _, _, _, cutDuration, _) = GranularSpeedBuilder.Build(
                ClipMs, withSpeed, 1.0, 0, "[0:v]", "[0:a]", "60", needHudBranch: false, cuts: cutList);

            var modelDuration = OutputTimeline.Create(ClipMs, withSpeed, 1.0, 0, null, cutList).TotalOutputSeconds;
            if (Math.Abs(cutDuration - modelDuration) > 0.05)
            {
                Console.WriteLine($"Cut Error: graph says {cutDuration:F3}s, OutputTimeline says {modelDuration:F3}s. " +
                                  "These MUST agree — every ruler in the app is drawn against the model.");
                return Task.FromResult(1);
            }

            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(cutGraph, @"trim=start=([\d.]+):end=([\d.]+)"))
            {
                double ts = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                double te = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (Math.Min(te, 15.0) - Math.Max(ts, 10.0) > 0.01)
                {
                    Console.WriteLine($"Cut Error: the graph still trims {ts:F3}-{te:F3}, which overlaps the deleted 10-15s.");
                    Console.WriteLine($"Actual chain:\n{cutGraph}");
                    return Task.FromResult(1);
                }
            }

            // 6. DE-CLICK. Every splice must carry the short fade, or a jump cut pops.
            if (!cutGraph.Contains("afade=t=in:st=0:d=0.008"))
            {
                Console.WriteLine("Cut Error: splice de-click fade missing from the audio chunks.");
                return Task.FromResult(1);
            }

            // 7. A CUT COSTS HALF WHAT A SPEED SEGMENT COSTS. This is the number the pre-export
            //    RAM warning is built on; if it drifts, the warning lies.
            if (OutputTimeline.Create(ClipMs, null, 1.0, 0, null, oneCut).ExtraChunkCost() != 1)
            {
                Console.WriteLine("Cut Error: a mid-clip cut should cost exactly 1 extra chunk.");
                return Task.FromResult(1);
            }
            if (OutputTimeline.Create(ClipMs, null, 1.0, 0, null,
                    new List<OutputTimeline.Cut> { new(0, 10) }).ExtraChunkCost() != 0)
            {
                Console.WriteLine("Cut Error: a cut touching the clip start splits nothing and should be free.");
                return Task.FromResult(1);
            }

            // 8. NO CUTS = NO CHANGE. The regression guard for every existing project.
            var before = GranularSpeedBuilder.Build(ClipMs, withSpeed, 1.0, 0, "[0:v]", "[0:a]", "60", needHudBranch: false);
            var after = GranularSpeedBuilder.Build(ClipMs, withSpeed, 1.0, 0, "[0:v]", "[0:a]", "60", needHudBranch: false,
                                                   cuts: new List<OutputTimeline.Cut>());
            if (before.filterGraph != after.filterGraph || Math.Abs(before.finalDuration - after.finalDuration) > 0.0001)
            {
                Console.WriteLine("Cut Error: an EMPTY cut list changed the graph. Existing projects must be untouched.");
                return Task.FromResult(1);
            }

            Console.WriteLine("Cut feature: arithmetic, join mapping, preview safety, normalisation, " +
                              "graph agreement, de-click, chunk cost and the no-cut regression all pass.");
        }

        foreach (double speed in new[] { 0.1, 0.5, 1.7, 4.0 })
        {
            double product = 1.0;
            foreach (string f in GranularSpeedBuilder.BuildAtempoChain(speed))
            {
                double v = double.Parse(f.Substring("atempo=".Length), System.Globalization.CultureInfo.InvariantCulture);
                if (v < 0.5 - 0.0001 || v > 2.0 + 0.0001)
                {
                    Console.WriteLine($"Atempo Error: chain element {v} outside [0.5, 2.0] for speed {speed}.");
                    return Task.FromResult(1);
                }
                product *= v;
            }
            if (Math.Abs(product - speed) > 0.01)
            {
                Console.WriteLine($"Atempo Error: chain product {product} != speed {speed}.");
                return Task.FromResult(1);
            }
        }

        var (_, _, _, _, finalDuration, _) = GranularSpeedBuilder.Build(
            10000.0,
            new List<SpeedSegment> { new SpeedSegment(2000, 4000, 2.0), new SpeedSegment(5000, 7000, 0.0) },
            1.0, 0, "[0:v]", "[0:a]", "60");
        if (Math.Abs(finalDuration - 11.0) > 0.01)
        {
            Console.WriteLine($"Time Sync Error: Expected 11.0, got {finalDuration}");
            return Task.FromResult(1);
        }

        Console.WriteLine("Phase 4 Math and Filters validated.");
        return Task.FromResult(0);
    }
}
