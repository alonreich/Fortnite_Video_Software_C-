using System.Diagnostics;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

public static class HardwareScanner
{
    /// <summary>
    /// G03: returned when the scan could NOT complete (exception / timeout) as opposed to
    /// completing and legitimately finding no hardware encoder. These two outcomes used to
    /// collapse into the same "CPU" string, which made EncoderManager set ForcedCpu=true and
    /// discard its own successful encoder detection — the exact reason a machine with a working
    /// RTX exported every video on libx264. NEVER merge this back into "CPU".
    /// </summary>
    public const string ScanFailed = "SCAN_FAILED";

    private static readonly string[] Priority = ["NVIDIA", "AMD", "INTEL"];

    private static readonly Dictionary<string, string> Encoders = new()
    {
        { "NVIDIA", "h264_nvenc" },
        { "AMD", "h264_amf" },
        { "INTEL", "h264_qsv" },
        { "CPU", "libx264" }
    };

    /// <summary>
    /// Suite-wide entry point (issue 2). Returns the shared result published by whichever app
    /// scanned first, and only pays for a real scan when there is nothing valid to reuse.
    ///
    /// A full <see cref="ScanAsync"/> spawns ffmpeg.exe up to FOUR times (one `-hwaccels` listing
    /// plus a real trial encode per vendor). Doing that in every process of a three-process suite
    /// was pure waste AND allowed two apps to disagree about the same machine. Every caller should
    /// use THIS method; call <see cref="ScanAsync"/> directly only when a forced re-probe is
    /// genuinely wanted.
    ///
    /// A successful result is published for the other apps. A failure is deliberately NOT
    /// published — see <see cref="HardwareCapability"/>.
    /// </summary>
    public static async Task<string> ScanSharedAsync(string ffmpegPath, CancellationToken cancellationToken = default)
    {
        var cached = HardwareCapability.TryLoadEncoder(ffmpegPath);
        if (cached.HasValue)
        {
            CoreLogger.Info("Hardware",
                $"Hardware scan skipped — reusing the suite-wide result: {cached.Value.EncoderMode}.");
            return cached.Value.EncoderMode;
        }

        string mode = await ScanAsync(ffmpegPath, cancellationToken);
        HardwareCapability.SaveEncoder(mode, ffmpegPath);
        return mode;
    }

    public static async Task<string> ScanAsync(string ffmpegPath, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            var hwaccels = await GetAvailableHwaccelsAsync(ffmpegPath, watchdog.Token);
            CoreLogger.Info("Hardware", $"FFmpeg hardware scan using: {Path.GetFileName(ffmpegPath)}");
            CoreLogger.Debug("Hardware", $"FFmpeg hardware scan path: {ffmpegPath}");
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
            // G03: an ABORTED scan proves nothing about the hardware. Do not report "CPU".
            CoreLogger.Fail("Hardware", "Hardware scan timed out or was cancelled — hardware capability UNKNOWN, deferring to the encoder probe at export time.");
            return ScanFailed;
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("Hardware", $"Hardware scan failed: {ex.Message} — hardware capability UNKNOWN, deferring to the encoder probe at export time.");
            return ScanFailed;
        }

        // Reached only when every probe RAN and every probe legitimately failed.
        CoreLogger.Fail("Hardware", "No working hardware encoder detected; using CPU.");
        return "CPU";
    }

    public static string GetEncoder(string mode)
    {
        return Encoders.TryGetValue(mode, out string? encoder) ? encoder : "libx264";
    }

    public static string GetPixelFormat()
    {
        return "yuv420p";
    }

    public static string GetLevel()
    {
        return "4.2";
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
        using var killReg = cancellationToken.Register(() => { try { process.Kill(true); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); } });
        CoreLogger.Debug("HardwareScanner", $"Command: {psi.FileName} {psi.Arguments}");
        // G02: these were the ONLY two unguarded ChildProcessTracker.AddProcess call sites in the
        // solution. Process tracking is cosmetic bookkeeping; it must never be able to abort the
        // GPU capability scan and downgrade the whole session to CPU encoding.
        try { ChildProcessTracker.AddProcess(process); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
            throw;
        }

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
        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-hide_banner -f lavfi -i color=c=black:s=256x256:r=30 -frames:v 1 -pix_fmt yuv420p -c:v {encoder} -f null -",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start FFmpeg for encoder test.");
        using var killReg = cancellationToken.Register(() => { try { process.Kill(true); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); } });
        // G02: see GetAvailableHwaccelsAsync — tracking failures must never abort the probe.
        try { ChildProcessTracker.AddProcess(process); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
            throw;
        }

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
