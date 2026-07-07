using FortniteVideoSoftware.Core.Media;
using System.Text.Json.Nodes;

namespace FortniteVideoSoftware.App;

public static class Phase4Gate
{
    public static Task<int> RunAsync()
    {
        Console.WriteLine("Testing Phase 4: FFmpeg Math and FilterBuilder");

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

        string duckFilter = FilterBuilder.BuildAudioDuckingFilter("v:0", "a:1");
        if (!duckFilter.Contains("lowpass=f=150") || !duckFilter.Contains("sidechaincompress=threshold=0.15:ratio=2.5"))
        {
            Console.WriteLine("Audio Ducking Error: Missing required DSP filters.");
            return Task.FromResult(1);
        }

        string introFilter = FilterBuilder.BuildWhatsAppIntroFilter("0:v");
        if (!introFilter.Contains("trim=duration=0.1"))
        {
            Console.WriteLine("WhatsApp Intro Error: Missing 0.1s trim.");
            return Task.FromResult(1);
        }

        double duration = TimeSyncEngine.CalculateTotalDuration(10.0, new List<(double, double, double)> { (2.0, 4.0, 2.0) }, new List<(double, double)> { (5.0, 2.0) });
        if (Math.Abs(duration - 11.0) > 0.001)
        {
            Console.WriteLine($"Time Sync Error: Expected 11.0, got {duration}");
            return Task.FromResult(1);
        }

        Console.WriteLine("Phase 4 Math and Filters validated.");
        return Task.FromResult(0);
    }
}
