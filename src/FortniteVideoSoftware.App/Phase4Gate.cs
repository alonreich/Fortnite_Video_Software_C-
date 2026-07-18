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

        // ISSUE_06: the gate now validates the PRODUCTION audio pipeline
        // (AudioFilterChain) instead of the deleted legacy FilterBuilder shim,
        // whose duck filter consumed the same music label twice (invalid FFmpeg).
        var (duckChains, duckFinalLabel) = AudioFilterChain.Build(
            null, 0, 10.0, 1.0, false, 0, null, 48000,
            new List<MusicTrack> { new MusicTrack("music.mp3", 0, 10.0) },
            1, 10.0, "[0:a]", 0.0);
        string duckFilter = string.Join(";", duckChains);
        if (!duckFilter.Contains("lowpass=f=150") ||
            !duckFilter.Contains("sidechaincompress=threshold=0.15:ratio=2.5") ||
            duckFinalLabel != "[a_music_prepared]")
        {
            Console.WriteLine("Audio Ducking Error: Missing required DSP filters in AudioFilterChain.");
            return Task.FromResult(1);
        }

        // ISSUE_06: atempo legality — every element of the production chain must sit
        // inside FFmpeg's per-filter [0.5, 2.0] window and multiply back to the speed.
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

        // ISSUE_06: duration math via the PRODUCTION builder (replaces the deleted
        // TimeSyncEngine): 10s clip, 2s→4s at 2x (=1s), 2s freeze at 5s => 11s output.
        var (_, _, _, finalDuration, _) = GranularSpeedBuilder.Build(
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
