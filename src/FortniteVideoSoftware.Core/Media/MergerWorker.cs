using System.Diagnostics;
using System.Globalization;
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
    public List<MusicTrack> MusicTracks { get; set; } = new();
    public JsonObject? MusicConfig { get; set; }
    public string? OutputDirectory { get; set; }
    public double SpeedFactor { get; set; } = 1.0;
    public enum TargetAspectRatio { Landscape16x9, Portrait9x16 }
    public TargetAspectRatio OutputRatio { get; set; } = TargetAspectRatio.Landscape16x9;

    public int QualityPercent { get; set; } = 100;

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
                var fileDurations = new double[InputFiles.Count];
                var fileHasAudio = new bool[InputFiles.Count];
                for (int fi = 0; fi < InputFiles.Count; fi++)
                {
                    var prober = new MediaProber(_ffprobePath, InputFiles[fi]);
                    double dur = await prober.GetDurationAsync();
                    bool hasAudio = await prober.HasAudioAsync();
                    fileDurations[fi] = dur;
                    fileHasAudio[fi] = hasAudio;
                    CoreLogger.Info("Merger", $"  [{fi + 1}] {InputFiles[fi]} — {dur:F2}s, audio={hasAudio}");
                    totalDuration += dur;
                }

                if (totalDuration == 0) totalDuration = 10.0;
                CoreLogger.Info("Merger", $"Total combined duration: {totalDuration:F2}s");

                var filters = new List<string>();
                var cmdArgs = new List<string> { "-y", "-hide_banner", "-progress", "pipe:1" };
                var effectiveMusicTracks = await BuildEffectiveMusicTracksAsync(totalDuration);

                for (int i = 0; i < InputFiles.Count; i++)
                {
                    cmdArgs.AddRange(["-i", InputFiles[i]]);
                }

                int musicInputIndex = InputFiles.Count;
                foreach (var musicTrack in effectiveMusicTracks)
                {
                    cmdArgs.AddRange(["-i", musicTrack.Path]);
                }

                string vOutputLabel = "[v_concat]";
                string aOutputLabel = "[a_concat]";

                string vInputs = "";
                string aInputs = "";
                for (int i = 0; i < InputFiles.Count; i++)
                {
                    double speedFactor = SpeedFactor > 0 ? SpeedFactor : 1.0;
                    
                    string scaleFilter = OutputRatio == TargetAspectRatio.Portrait9x16
                        ? $"scale=1080:1920:force_original_aspect_ratio=increase:flags=lanczos,crop=1080:1920"
                        : $"scale=1920:1080:force_original_aspect_ratio=decrease:flags=lanczos,pad=1920:1080:(ow-iw)/2:(oh-ih)/2";

                    filters.Add($"[{i}:v]{scaleFilter},setsar=1,setpts=PTS/{speedFactor.ToString("F4", CultureInfo.InvariantCulture)}[v{i}]");
                    double clipDur = fileDurations[i] > 0 ? fileDurations[i] : totalDuration;
                    if (fileHasAudio[i])
                    {
                        double atempoSpeed = speedFactor;
                        var atempoFilters = new List<string>();
                        while (atempoSpeed > 2.0) { atempoFilters.Add("atempo=2.0"); atempoSpeed /= 2.0; }
                        while (atempoSpeed < 0.5) { atempoFilters.Add("atempo=0.5"); atempoSpeed /= 0.5; }
                        atempoFilters.Add($"atempo={atempoSpeed.ToString("F4", CultureInfo.InvariantCulture)}");
                        filters.Add($"[{i}:a]aformat=sample_fmts=fltp:channel_layouts=stereo:sample_rates=48000,{string.Join(",", atempoFilters)}[a{i}]");
                    }
                    else
                    {
                        filters.Add($"anullsrc=r=48000:cl=stereo,atrim=duration={clipDur.ToString("F3", CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS[a{i}]");
                    }
                    vInputs += $"[v{i}]";
                    aInputs += $"[a{i}]";
                }
                if (InputFiles.Count > 1)
                {
                    filters.Add($"{vInputs}concat=n={InputFiles.Count}:v=1:a=0{vOutputLabel}");
                    filters.Add($"{aInputs}concat=n={InputFiles.Count}:v=0:a=1{aOutputLabel}");
                }
                else
                {
                    vOutputLabel = "[v0]";
                    aOutputLabel = "[a0]";
                }

                string finalAudioLabel = aOutputLabel;

                if (effectiveMusicTracks.Count > 0)
                {
                    MusicConfig ??= new JsonObject();

                    if (MusicConfig != null && !MusicConfig.ContainsKey("timeline_start_sec"))
                    {
                        MusicConfig["timeline_start_sec"] = 0.0;
                        MusicConfig["timeline_end_sec"] = totalDuration;
                    }

                    var (duckChains, finalDuckingLabel) = AudioFilterChain.Build(
                        musicConfig: MusicConfig,
                        videoStartTime: 0,
                        videoEndTime: totalDuration,
                        speedFactor: 1.0,
                        disableFades: false,
                        vfadeInD: 0,
                        audioFilterCmd: null,
                        sampleRate: 48000,
                        musicTracks: effectiveMusicTracks,
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

                var encoderMgr = new EncoderManager("GPU", _ffmpegPath);
                string currentEncoder = encoderMgr.GetInitialEncoder(true);

                int cqValue = QualityPercent >= 100 ? 15 : Math.Max(15, 35 - (int)((QualityPercent - 5) * 20.0 / 95.0));
                int qualityLevel = QualityPercent >= 100 ? 3 : (QualityPercent >= 50 ? 2 : 1);

                string corePath = Path.Combine(tempJobDir, "merged_output.mp4");
                string? successOutputPath = null;
                string lastErrorMsg = "FFmpeg render failed.";

                while (true)
                {
                    var (codecArgs, rcLabel) = encoderMgr.GetCodecFlags(currentEncoder, null, totalDuration / Math.Max(0.1, SpeedFactor), "60", qualityLevel, false);

                    if (QualityPercent < 100)
                    {
                        for (int ci = 0; ci < codecArgs.Count - 1; ci++)
                        {
                            if (codecArgs[ci] == "-cq")
                            {
                                codecArgs[ci + 1] = cqValue.ToString();
                                break;
                            }
                        }
                    }

                    var attemptArgs = new List<string>(cmdArgs);
                    attemptArgs.AddRange(["-filter_complex_script", filterScriptPath]);
                    attemptArgs.AddRange(["-map", vOutputLabel, "-map", finalAudioLabel]);
                    attemptArgs.AddRange(codecArgs);
                    attemptArgs.AddRange(["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);
                    attemptArgs.Add(corePath);

                    CoreLogger.Info("FFmpeg", $"Executing merge with encoder {currentEncoder} ({rcLabel}).");
                    CoreLogger.Info("FFmpeg", $"Command: {_ffmpegPath} {string.Join(" ", attemptArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");

                    bool attemptSuccess = await ExecuteFFmpegAsync(attemptArgs, totalDuration / Math.Max(0.1, SpeedFactor), cancellationToken);

                    if (attemptSuccess && File.Exists(corePath) && new FileInfo(corePath).Length > 0)
                    {
                        successOutputPath = corePath;
                        break;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        lastErrorMsg = "Merge canceled.";
                        break;
                    }

                    var fallbacks = encoderMgr.GetFallbackList(currentEncoder, allowCpu: true);
                    if (fallbacks.Count == 0)
                    {
                        lastErrorMsg = $"FFmpeg exited with encoder {currentEncoder}. Render failed.";
                        break;
                    }

                    string failedEncoder = currentEncoder;
                    currentEncoder = fallbacks[0];
                    CoreLogger.Info("Merger", $"Encoder {failedEncoder} failed, retrying with fallback: {currentEncoder}");

                    try { if (File.Exists(corePath)) File.Delete(corePath); } catch { }
                }

                if (successOutputPath != null)
                {
                    string outputDir = !string.IsNullOrEmpty(OutputDirectory) && Directory.Exists(OutputDirectory)
                        ? OutputDirectory
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    Directory.CreateDirectory(outputDir);
                    int idx = 1;
                    string finalOutput;
                    while (true)
                    {
                        finalOutput = Path.Combine(outputDir, $"Merged-Videos-{idx}.mp4");
                        if (!File.Exists(finalOutput)) break;
                        idx++;
                    }
                    File.Move(successOutputPath, finalOutput);
                    ProgressUpdate?.Invoke(100);
                    EmitFinished(true, finalOutput);
                }
                else
                {
                    EmitFinished(false, lastErrorMsg);
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

    private async Task<List<MusicTrack>> BuildEffectiveMusicTracksAsync(double totalDuration)
    {
        var sourceTracks = MusicTracks.Count > 0
            ? MusicTracks.Where(t => !string.IsNullOrWhiteSpace(t.Path)).ToList()
            : MusicTrack != null && !string.IsNullOrWhiteSpace(MusicTrack.Path)
                ? new List<MusicTrack> { MusicTrack }
                : new List<MusicTrack>();

        if (sourceTracks.Count == 0)
            return sourceTracks;

        double musicWindowDuration = totalDuration;
        if (MusicConfig != null)
        {
            try
            {
                double start = MusicConfig["timeline_start_sec"]?.GetValue<double>() ?? 0.0;
                double end = MusicConfig["timeline_end_sec"]?.GetValue<double>() ?? 0.0;
                if (end > start)
                    musicWindowDuration = Math.Max(0.01, end - start);
            }
            catch { }
        }

        var normalized = new List<MusicTrack>();
        foreach (var track in sourceTracks)
        {
            double duration = track.Duration;
            bool durationFromSourceProbe = true;
            if (durationFromSourceProbe)
            {
                try
                {
                    var prober = new MediaProber(_ffprobePath, track.Path);
                    double probedDuration = await prober.GetDurationAsync();
                    if (probedDuration > 0)
                        duration = probedDuration;
                }
                catch { }
            }

            if (durationFromSourceProbe && track.Offset > 0 && duration > track.Offset)
                duration -= track.Offset;

            if (duration <= 0)
                duration = musicWindowDuration;

            normalized.Add(new MusicTrack(track.Path, track.Offset, duration, track.TimelineStartDelay, track.ApplyFadeOut));
        }

        bool loopMusic = false;
        try { loopMusic = MusicConfig?["loop_music"]?.GetValue<bool>() ?? false; } catch { }
        if (!loopMusic)
            return normalized;

        var looped = new List<MusicTrack>();
        double remaining = musicWindowDuration;
        int guard = 0;
        while (remaining > 0.001 && guard++ < 1000)
        {
            foreach (var track in normalized)
            {
                if (remaining <= 0.001)
                    break;

                double take = Math.Min(track.Duration, remaining);
                if (take <= 0.001)
                    continue;

                bool firstTrack = looped.Count == 0;
                looped.Add(new MusicTrack(
                    track.Path,
                    firstTrack ? track.Offset : 0.0,
                    take,
                    firstTrack ? track.TimelineStartDelay : 0.0,
                    track.ApplyFadeOut));
                remaining -= take;
            }
        }

        return looped.Count > 0 ? looped : normalized;
    }

    private async Task<bool> ExecuteFFmpegAsync(List<string> cmdArgs, double totalDuration, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = string.Join(" ", cmdArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _currentProcess = Process.Start(psi);
        if (_currentProcess == null)
            return false;

        using var reg = cancellationToken.Register(() =>
        {
            try { _currentProcess.Kill(entireProcessTree: true); } catch { }
        });

        var progressTask = Task.Run(async () =>
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

        var stderrTask = Task.Run(async () =>
        {
            using var reader = _currentProcess.StandardError;
            while (!reader.EndOfStream)
            {
                string? line = await reader.ReadLineAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    if (!line.StartsWith("frame=") && !line.StartsWith("size="))
                    {
                        CoreLogger.Append(line);
                    }
                }
            }
        }, cancellationToken);

        try
        {
            await _currentProcess.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await Task.WhenAll(progressTask, stderrTask);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("Merger", $"Reader task error: {ex.Message}");
        }

        return _currentProcess.ExitCode == 0;
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
