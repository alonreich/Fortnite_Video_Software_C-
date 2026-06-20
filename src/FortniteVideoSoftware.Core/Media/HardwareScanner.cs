using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FortniteVideoSoftware.Core.Media;

public static class HardwareScanner
{
    private static readonly string[] Priority = ["NVIDIA", "AMD", "INTEL"];
    
    private static readonly Dictionary<string, string> Encoders = new()
    {
        { "NVIDIA", "h264_nvenc" },
        { "AMD", "h264_amf" },
        { "INTEL", "h264_qsv" },
        { "CPU", "libx264" }
    };

    public static async Task<string> ScanAsync(string ffmpegPath, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(15)); // 15-second watchdog

        try
        {
            var hwaccels = await GetAvailableHwaccelsAsync(ffmpegPath, watchdog.Token);
            foreach (var mode in Priority)
            {
                watchdog.Token.ThrowIfCancellationRequested();
                string encoder = Encoders[mode];
                if (await CheckEncoderCapabilityAsync(ffmpegPath, encoder, watchdog.Token))
                {
                    return mode;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Watchdog tripped or user cancelled
        }
        catch (Exception)
        {
            // Ignored, fallback to CPU
        }

        return "CPU";
    }

    public static string GetEncoder(string mode)
    {
        return Encoders.TryGetValue(mode, out string? encoder) ? encoder : "libx264";
    }

    public static string GetPixelFormat()
    {
        return "yuv420p"; // Encoder Contract
    }

    public static string GetLevel()
    {
        return "4.2"; // Encoder Contract
    }

    private static async Task<List<string>> GetAvailableHwaccelsAsync(string ffmpegPath, CancellationToken cancellationToken)
    {
        List<string> hwaccels = new();
        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = "-hwaccels",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start FFmpeg.");
        await process.WaitForExitAsync(cancellationToken);

        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        foreach (string line in output.Split('\n'))
        {
            string trimLine = line.Trim();
            if (!string.IsNullOrWhiteSpace(trimLine) && !trimLine.StartsWith("Hardware acceleration methods:"))
            {
                hwaccels.Add(trimLine);
            }
        }

        return hwaccels;
    }

    private static async Task<bool> CheckEncoderCapabilityAsync(string ffmpegPath, string encoder, CancellationToken cancellationToken)
    {
        // To verify the encoder, we run a short test encoding a dummy frame.
        // ffmpeg -f lavfi -i color=c=black:s=128x128 -c:v <encoder> -t 0.1 -f null -
        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-f lavfi -i color=c=black:s=128x128 -c:v {encoder} -t 0.1 -f null -",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start FFmpeg for encoder test.");
        await process.WaitForExitAsync(cancellationToken);

        string stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        if (process.ExitCode == 0 && !stderr.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
