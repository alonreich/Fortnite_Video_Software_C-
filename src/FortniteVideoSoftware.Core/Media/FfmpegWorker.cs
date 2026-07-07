using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

public class FfmpegWorker
{
    private readonly string _ffmpegPath;
    
    public FfmpegWorker(ApplicationPaths paths)
    {
        string binariesPath = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "libmpv-2.dll");
        string localFfmpeg = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "backend", "ffmpeg.exe");
        _ffmpegPath = File.Exists(localFfmpeg) ? localFfmpeg : "ffmpeg.exe";
    }

    public async Task<bool> RunEncodingAsync(
        string inputPath, 
        string outputPath, 
        string encoder, 
        string filterGraph,
        double expectedDurationSec,
        IProgress<int>? progressReporter,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = $"-y -i \"{inputPath}\" -filter_complex \"{filterGraph}\" -c:v {encoder} -c:a aac -progress pipe:1 \"{outputPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return false;

        using var reg = cancellationToken.Register(() => 
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        _ = Task.Run(async () =>
        {
            using var reader = process.StandardOutput;
            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line != null && line.StartsWith("out_time_us="))
                {
                    if (long.TryParse(line.AsSpan(12), out long outTimeUs))
                    {
                        double currentSec = outTimeUs / 1000000.0;
                        if (expectedDurationSec > 0)
                        {
                            int percent = (int)Math.Clamp((currentSec / expectedDurationSec) * 100, 0, 100);
                            progressReporter?.Report(percent);
                        }
                    }
                }
            }
        }, cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        
        return process.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
    }
}

