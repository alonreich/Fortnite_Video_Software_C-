using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.Core.Media;

public static class WaveformGenerator
{
    public static async Task<string?> GenerateWaveformImageAsync(string ffmpegPath, string audioFilePath, int width = 4000, int height = 400, double? startSec = null, double? durationSec = null, CancellationToken cancellationToken = default)
    {
        try
        {
            string tempPng = Path.Combine(FortniteVideoSoftware.Core.Infrastructure.ApplicationPaths.CreateDefault().TempDirectory, $"fvs_wave_{Guid.NewGuid():N}.png");
            
            string timeArgs = "";
            if (startSec.HasValue) timeArgs += $"-ss {startSec.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)} ";
            if (durationSec.HasValue) timeArgs += $"-t {durationSec.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)} ";

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-y -hide_banner -loglevel error {timeArgs}-i \"{audioFilePath}\" -frames:v 1 -filter_complex \"aformat=channel_layouts=mono,volume=1.5,showwavespic=s={width}x{height}:colors=0x7DD3FC:draw=full\" \"{tempPng}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;
            ChildProcessTracker.AddProcess(process);

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            _ = await outputTask;
            _ = await errorTask;

            if (process.ExitCode == 0 && File.Exists(tempPng))
            {
                return tempPng;
            }

            if (File.Exists(tempPng)) File.Delete(tempPng);
        }
        catch (Exception)
        {
        }
        return null;
    }
}
