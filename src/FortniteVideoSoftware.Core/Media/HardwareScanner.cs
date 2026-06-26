using System.Diagnostics;
using FortniteVideoSoftware.Core.Infrastructure;

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
            CoreLogger.Info("Hardware", $"FFmpeg hardware scan using: {ffmpegPath}");
            CoreLogger.Info("Hardware", $"Available FFmpeg hwaccels: {(hwaccels.Count == 0 ? "none" : string.Join(", ", hwaccels))}");
            foreach (var mode in Priority)
            {
                watchdog.Token.ThrowIfCancellationRequested();
                string encoder = Encoders[mode];
                if (await CheckEncoderCapabilityAsync(ffmpegPath, encoder, watchdog.Token))
                {
                    CoreLogger.Info("Hardware", $"Selected hardware encoder: {mode} ({encoder})");
                    return mode;
                }
            }
        }
        catch (OperationCanceledException)
        {
            CoreLogger.Fail("Hardware", "Hardware scan timed out or was cancelled.");
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("Hardware", $"Hardware scan failed: {ex.Message}");
        }

        CoreLogger.Fail("Hardware", "No working hardware encoder detected; using CPU.");
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
            Arguments = "-hide_banner -hwaccels",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start FFmpeg.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        string output = await outputTask;
        _ = await errorTask;
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
        // To verify the encoder, run one real frame through it.
        // 256x256 is intentionally used because current NVENC drivers reject 128x128
        // with "Frame Dimension less than the minimum supported value."
        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-hide_banner -f lavfi -i color=c=black:s=256x256:r=30 -frames:v 1 -pix_fmt yuv420p -c:v {encoder} -f null -",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start FFmpeg for encoder test.");
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        string stderr = await errorTask;
        if (process.ExitCode == 0)
        {
            return true;
        }

        string reason = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line =>
                line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("unsupported", StringComparison.OrdinalIgnoreCase)) ?? $"exit code {process.ExitCode}";
        CoreLogger.Info("Hardware", $"Encoder probe failed: {encoder} ({reason})");
        return false;
    }
}
