using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

public static class WaveformGenerator
{
    public static async Task<string?> GenerateWaveformImageAsync(string ffmpegPath, string audioFilePath, int width = 4000, int height = 400, double? startSec = null, double? durationSec = null, CancellationToken cancellationToken = default)
    {
        string? tempPng = null;
        Process? process = null;
        try
        {
            tempPng = Path.Combine(FortniteVideoSoftware.Core.Infrastructure.ApplicationPaths.CreateDefault().TempDirectory, $"fvs_wave_{Guid.NewGuid():N}.png");
            
            var ci = System.Globalization.CultureInfo.InvariantCulture;

            var waveArgs = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
            if (startSec.HasValue) { waveArgs.Add("-ss"); waveArgs.Add(startSec.Value.ToString(ci)); }
            if (durationSec.HasValue) { waveArgs.Add("-t"); waveArgs.Add(durationSec.Value.ToString(ci)); }
            waveArgs.Add("-i");
            waveArgs.Add(audioFilePath);
            waveArgs.Add("-frames:v");
            waveArgs.Add("1");
            waveArgs.Add("-filter_complex");
            waveArgs.Add($"aformat=channel_layouts=mono,volume=1.5,showwavespic=s={width}x{height}:colors=0x7DD3FC:draw=full");
            waveArgs.Add(tempPng);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string arg in waveArgs) psi.ArgumentList.Add(arg);

            CoreLogger.Debug("WaveformGenerator", $"Command: {psi.FileName} {ProcessArgs.FormatForLog(waveArgs)}");
            process = Process.Start(psi);
            if (process == null) return null;
            ChildProcessTracker.AddProcess(process);

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            _ = await outputTask;
            string waveErr = string.Empty;
            try { waveErr = await errorTask; } catch { }

            if (process.ExitCode == 0 && File.Exists(tempPng) && new FileInfo(tempPng).Length > 0)
            {
                return tempPng;
            }

            CoreLogger.Fail("WaveformGenerator",
                $"Waveform render failed (exit {process.ExitCode}) for '{Path.GetFileName(audioFilePath)}'.");
            if (!string.IsNullOrWhiteSpace(waveErr))
                CoreLogger.Fail("WaveformGenerator", $"FFmpeg stderr:\n{waveErr.Trim()}");

            if (File.Exists(tempPng)) File.Delete(tempPng);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (process != null && !process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }

            if (tempPng != null && File.Exists(tempPng))
            {
                try { File.Delete(tempPng); } catch { }
            }

            throw;
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("WaveformGenerator", $"Waveform generation threw: {ex.Message}");
            if (tempPng != null && File.Exists(tempPng))
            {
                try { File.Delete(tempPng); } catch { }
            }
        }
        finally
        {
            try { process?.Dispose(); } catch { }
        }
        return null;
    }
}
