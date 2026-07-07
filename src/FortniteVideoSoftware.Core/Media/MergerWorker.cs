
using System.Diagnostics;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

public class MergerWorker : IDisposable
{
    private readonly ApplicationPaths _paths;
    private Process? _currentProcess;
    private bool _finishEmitted;
    private string _ffmpegPath;
    private string _ffprobePath;

    public event Action<int>? ProgressUpdate;
    public event Action<bool, string>? Finished;

    public List<string> InputFiles { get; set; } = new();
    public MusicTrack? MusicTrack { get; set; }
    public JsonObject? MusicConfig { get; set; }

    public MergerWorker(ApplicationPaths? paths = null)
    {
        _paths = paths ?? ApplicationPaths.CreateDefault();
        _ffmpegPath = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "backend", "ffmpeg.exe");
        if (!File.Exists(_ffmpegPath)) _ffmpegPath = "ffmpeg.exe";
        _ffprobePath = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "backend", "ffprobe.exe");
        if (!File.Exists(_ffprobePath)) _ffprobePath = "ffprobe.exe";
    }

    public void Cancel()
    {
        CoreLogger.Info("Merger", "Merge cancelled by user.");
        if (_currentProcess != null)
        {
            try { _currentProcess.Kill(entireProcessTree: true); } catch { }
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (InputFiles.Count == 0)
            {
                EmitFinished(false, "No input files provided.");
                return;
            }

            string jobId = Guid.NewGuid().ToString("N")[..8];
            string tempJobDir = Path.Combine(_paths.TempDirectory, $"fvs_merger_{jobId}");
            Directory.CreateDirectory(tempJobDir);

            try
            {
                CoreLogger.Info("Merger", $"Merging {InputFiles.Count} file(s):");
                double totalDuration = 0;
                for (int fi = 0; fi < InputFiles.Count; fi++)
                {
                    var prober = new MediaProber(_ffprobePath, InputFiles[fi]);
                    double dur = await prober.GetDurationAsync();
                    CoreLogger.Info("Merger", $"  [{fi + 1}] {InputFiles[fi]} — {dur:F2}s");
                    totalDuration += dur;
                }

                if (totalDuration == 0) totalDuration = 10.0;
                CoreLogger.Info("Merger", $"Total combined duration: {totalDuration:F2}s");

                var filters = new List<string>();
                var cmdArgs = new List<string> { "-y", "-hide_banner", "-progress", "pipe:1" };

                for (int i = 0; i < InputFiles.Count; i++)
                {
                    cmdArgs.AddRange(["-i", InputFiles[i]]);
                }

                int musicInputIndex = InputFiles.Count;
                if (MusicTrack != null)
                {
                    cmdArgs.AddRange(["-i", MusicTrack.Path]);
                }

                string vOutputLabel = "[v_concat]";
                string aOutputLabel = "[a_concat]";

                string vInputs = "";
                string aInputs = "";
                for (int i = 0; i < InputFiles.Count; i++)
                {
                    filters.Add($"[{i}:v]scale=1920:1080:force_original_aspect_ratio=decrease:flags=lanczos,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,setsar=1[v{i}]");
                    filters.Add($"[{i}:a]aformat=sample_fmts=fltp:channel_layouts=stereo:sample_rates=48000[a{i}]");
                    vInputs += $"[v{i}]";
                    aInputs += $"[a{i}]";
                }
                filters.Add($"{vInputs}concat=n={InputFiles.Count}:v=1:a=0{vOutputLabel}");
                filters.Add($"{aInputs}concat=n={InputFiles.Count}:v=0:a=1{aOutputLabel}");

                string finalAudioLabel = aOutputLabel;

                if (MusicTrack != null)
                {
                    var (duckChains, finalDuckingLabel) = AudioFilterChain.Build(
                        musicConfig: MusicConfig,
                        videoStartTime: 0,
                        videoEndTime: totalDuration,
                        speedFactor: 1.0,
                        disableFades: false,
                        vfadeInD: 0,
                        audioFilterCmd: null,
                        sampleRate: 48000,
                        musicTracks: new List<MusicTrack> { MusicTrack },
                        musicStartIndex: musicInputIndex,
                        totalProjectDuration: totalDuration,
                        mainAudioLabel: aOutputLabel,
                        volumeNormalizeDb: 0.0
                    );

                    filters.AddRange(duckChains);
                    finalAudioLabel = finalDuckingLabel;
                }

                string filterScript = string.Join(";", filters.Where(p => !string.IsNullOrEmpty(p)));
                string filterScriptPath = Path.Combine(tempJobDir, "filter_complex.txt");
                await File.WriteAllTextAsync(filterScriptPath, filterScript, cancellationToken);

                cmdArgs.AddRange(["-filter_complex_script", filterScriptPath]);
                cmdArgs.AddRange(["-map", vOutputLabel, "-map", finalAudioLabel]);
                
                var encoderMgr = new EncoderManager("GPU", _ffmpegPath);
                var (codecArgs, _) = encoderMgr.GetCodecFlags(encoderMgr.GetInitialEncoder(true), null, totalDuration, "60", 3, false);
                cmdArgs.AddRange(codecArgs);

                cmdArgs.AddRange(["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);

                string outputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Directory.CreateDirectory(outputDir);
                string corePath = Path.Combine(tempJobDir, "merged_output.mp4");
                cmdArgs.Add(corePath);

                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = string.Join(" ", cmdArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                CoreLogger.Info("FFmpeg", $"Executing Final Pipeline Command: {psi.FileName} {psi.Arguments}");

                _currentProcess = Process.Start(psi);
                if (_currentProcess == null)
                {
                    EmitFinished(false, "Failed to start FFmpeg process.");
                    return;
                }

                using var reg = cancellationToken.Register(() =>
                {
                    try { _currentProcess.Kill(entireProcessTree: true); } catch { }
                });

                _ = Task.Run(async () =>
                {
                    using var reader = _currentProcess.StandardOutput;
                    while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(cancellationToken);
                        if (line != null && line.StartsWith("out_time_us="))
                        {
                            if (long.TryParse(line.AsSpan(12), out long outTimeUs))
                            {
                                double currentSec = outTimeUs / 1_000_000.0;
                                if (totalDuration > 0)
                                {
                                    int percent = (int)Math.Clamp(currentSec / totalDuration * 100, 0, 100);
                                    ProgressUpdate?.Invoke(percent);
                                }
                            }
                        }
                    }
                }, cancellationToken);

                _ = Task.Run(async () =>
                {
                    using var reader = _currentProcess.StandardError;
                    while (!reader.EndOfStream)
                    {
                        string? line = await reader.ReadLineAsync(cancellationToken);
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            CoreLogger.Append(line);
                        }
                    }
                }, cancellationToken);

                await _currentProcess.WaitForExitAsync(cancellationToken);

                if (_currentProcess.ExitCode == 0 && File.Exists(corePath) && new FileInfo(corePath).Length > 0)
                {
                    int idx = 1;
                    string finalOutput;
                    while (true)
                    {
                        finalOutput = Path.Combine(outputDir, $"Merged-Videos-{idx}.mp4");
                        if (!File.Exists(finalOutput)) break;
                        idx++;
                    }
                    File.Move(corePath, finalOutput);
                    ProgressUpdate?.Invoke(100);
                    EmitFinished(true, finalOutput);
                }
                else
                {
                    EmitFinished(false, $"FFmpeg exited with code {_currentProcess.ExitCode}. Render failed.");
                }
            }
            finally
            {
                try { if (Directory.Exists(tempJobDir)) Directory.Delete(tempJobDir, true); } catch { }
            }
        }
        catch (OperationCanceledException)
        {
            CoreLogger.Info("Merger", "Merge pipeline canceled.");
            EmitFinished(false, "Merge canceled.");
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("Merger", $"Merge pipeline failed with exception: {ex}");
            EmitFinished(false, ex.Message);
        }
    }

    private void EmitFinished(bool success, string message)
    {
        if (_finishEmitted) return;
        _finishEmitted = true;
        Finished?.Invoke(success, message);
    }

    public void Dispose()
    {
        _currentProcess?.Dispose();
    }
}

