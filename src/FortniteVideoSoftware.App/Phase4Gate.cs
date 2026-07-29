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
            ("acrossover=split=150",                              "150 Hz music split (AudioFilterChain.cs:227)"),
            ("[mus_high][trig_final]sidechaincompress=",           "ducking applied to the HIGH band only (:232)"),
            ("threshold=0.15:ratio=2.5",                           "default ducking threshold/ratio (:229-231)"),
            ("[mus_low][mus_high_ducked]amix=",                    "low + ducked-high recombine (:234)"),
            ("[game_trig]highpass=f=200,lowpass=f=3500",           "sidechain trigger band-pass"),
            ("[game_leveled]asplit=2[game_out_pre][game_trig]",    "split happens AFTER levelling, so ducking is source-level independent"),
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
