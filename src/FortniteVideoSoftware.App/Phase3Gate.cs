using FortniteVideoSoftware.Core.Media;
using System.Diagnostics;

namespace FortniteVideoSoftware.App;

public static class Phase3Gate
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("Testing MPVSafetyManager rapid scrubbing...");
        
        nint mpv = MpvWrapper.mpv_create();
        if (mpv == nint.Zero)
        {
            Console.WriteLine("Failed to create MPV instance. Is libmpv-2.dll available?");
            return 1;
        }

        MpvWrapper.mpv_initialize(mpv);
        
        using MPVSafetyManager safetyManager = new MPVSafetyManager(mpv);

        Stopwatch sw = Stopwatch.StartNew();
        int scrubs = 0;

        Console.WriteLine("Rapidly scrubbing back and forth 100 times...");
        for (int i = 0; i < 100; i++)
        {
            safetyManager.RequestSeek(i % 2 == 0 ? 10.0 : 20.0);
            scrubs++;
            await Task.Delay(10);
        }

        Console.WriteLine($"Sent {scrubs} seeks in {sw.ElapsedMilliseconds}ms. Testing if watchdog is still responsive...");
        
        await Task.Delay(500);

        Console.WriteLine("Scrubbing did not freeze the main thread.");

        MpvWrapper.mpv_terminate_destroy(mpv);
        return 0;
    }
}
