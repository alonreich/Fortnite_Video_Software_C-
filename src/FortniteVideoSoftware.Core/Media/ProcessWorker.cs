// ==============================================================================
// ProcessWorker.cs — Full port of Python processing/worker.py ProcessThread
// Orchestrates the complete FFmpeg rendering pipeline with all features:
// granular speed, mobile portrait conversion, audio ducking, WhatsApp intro,
// text overlay, encoder fallback, file-size targeting.
// ==============================================================================

using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

public class ProcessWorker : IDisposable
{
    private readonly ApplicationPaths _paths;
    private Process? _currentProcess;
    private bool _isCanceled;
    private bool _finishEmitted;
    private string _ffmpegPath;
    private string _ffprobePath;

    public event Action<int>? ProgressUpdate;
    public event Action<int, string, int>? PhaseUpdate;
    public event Action<bool, string>? Finished;

    // Processing parameters
    public string InputPath { get; set; } = "";
    public double StartTimeMs { get; set; }
    public double EndTimeMs { get; set; }
    public string OriginalResolution { get; set; } = "1920x1080";
    public bool IsMobileFormat { get; set; } = true;
    public double SpeedFactor { get; set; } = 1.0;
    public bool IsBossHp { get; set; }
    public bool ShowTeammates { get; set; }
    public bool ShowSpectating { get; set; }
    public int QualityLevel { get; set; } = 2;
    public bool DisableFades { get; set; }
    public string? PortraitText { get; set; }
    public JsonObject? MusicConfig { get; set; }
    public List<SpeedSegment>? SpeedSegments { get; set; }
    public string HardwareStrategy { get; set; } = "CPU";
    public List<MusicTrack>? MusicTracks { get; set; }
    public double? TargetMbOverride { get; set; }
    public double ThumbnailPosMs { get; set; }
    public double VolumeNormalizeDb { get; set; }
    public double IntroStillSec { get; set; }
    public bool IntroFromMidpoint { get; set; }
    public double? IntroAbsTimeMs { get; set; }
    public double MusicVolume { get; set; } = 0.8;

    public ProcessWorker(ApplicationPaths? paths = null)
    {
        _paths = paths ?? ApplicationPaths.CreateDefault();
        _ffmpegPath = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "binaries", "ffmpeg.exe");
        if (!File.Exists(_ffmpegPath)) _ffmpegPath = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "ffmpeg.exe");
        if (!File.Exists(_ffmpegPath)) _ffmpegPath = "ffmpeg.exe";
        
        _ffprobePath = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "binaries", "ffprobe.exe");
        if (!File.Exists(_ffprobePath)) _ffprobePath = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "ffprobe.exe");
        if (!File.Exists(_ffprobePath)) _ffprobePath = "ffprobe.exe";
    }

    public void Cancel()
    {
        _isCanceled = true;
        if (_currentProcess != null)
        {
            try { _currentProcess.Kill(entireProcessTree: true); } catch { }
        }
    }

    /// <summary>
    /// Runs the complete rendering pipeline. Returns true on success.
    /// Exact port of ProcessThread.run() logic flow.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Encoder preflight
            var encoderMgr = new EncoderManager(HardwareStrategy, _ffmpegPath);
            if (encoderMgr.EncoderPreflightError != null)
            {
                EmitFinished(false, encoderMgr.EncoderPreflightError);
                return;
            }

            // Create temp working directory
            string jobId = Guid.NewGuid().ToString("N")[..8];
            string tempJobDir = Path.Combine(Path.GetTempPath(), $"fvs_job_{jobId}");
            Directory.CreateDirectory(tempJobDir);

            CoreLogger.Info("Process", $"Input Path: {InputPath}");
            CoreLogger.Info("Process", $"Start Time: {StartTimeMs} ms, End Time: {EndTimeMs} ms");
            CoreLogger.Info("Process", $"Base Speed Factor: {SpeedFactor}");
            CoreLogger.Info("Process", $"Is Mobile Format: {IsMobileFormat}, Portrait Text: {PortraitText ?? "None"}");
            CoreLogger.Info("Process", $"Target Quality: {QualityLevel}, MB Override: {TargetMbOverride?.ToString() ?? "None"}");
            CoreLogger.Info("Process", $"Thumbnail Pos: {ThumbnailPosMs} ms");
            
            if (SpeedSegments != null && SpeedSegments.Count > 0)
            {
                foreach (var seg in SpeedSegments)
                {
                    CoreLogger.Info("Process", $"Granular Speed Segment: {seg.Speed}x from {seg.StartMs}ms to {seg.EndMs}ms");
                }
            }

            if (MusicTracks != null && MusicTracks.Count > 0)
            {
                foreach (var track in MusicTracks)
                {
                    CoreLogger.Info("Process", $"Music Selected: {track.Path} | Start Time: {track.Offset}s | Duration: {track.Duration}s");
                }
                if (MusicConfig != null)
                {
                    CoreLogger.Info("Process", $"Music Ducking Enabled: {MusicConfig["ducking_threshold"]?.GetValue<double>() != 1.0}");
                }
            }

            try
            {
                // Probe source
                var prober = new MediaProber(_ffprobePath, InputPath);
                bool sourceHasAudio = await prober.HasAudioAsync();
                int sourceAudioKbps = await prober.GetAudioBitrateAsync();
                double sourceDuration = await prober.GetDurationAsync();

                var config = new VideoConfig();
                var (keepHighestRes, targetMb, qualityLevel) = config.GetQualitySettings(QualityLevel, TargetMbOverride);

                string targetFps = "60";

                // Generate portrait text PNG overlay
                string? textPngPath = null;
                if (!string.IsNullOrEmpty(PortraitText))
                {
                    textPngPath = Path.Combine(tempJobDir, "portrait_text.png");
                    try
                    {
                        TextOverlayGenerator.GeneratePng(PortraitText, textPngPath);
                    }
                    catch (Exception ex)
                    {
                        textPngPath = null;
                        CoreLogger.Fail("Process", $"Failed to generate text PNG: {ex.Message}");
                    }
                }

                // Build granular speed chain
                string granularFilters = "";
                string gV = "", gA = "";
                double gDur = (EndTimeMs - StartTimeMs) / 1000.0 / SpeedFactor;

                if (SpeedSegments != null && SpeedSegments.Count > 0)
                {
                    var (filterGraph, vLabel, aLabel, finalDur, _) = GranularSpeedBuilder.Build(
                        EndTimeMs - StartTimeMs,
                        SpeedSegments,
                        SpeedFactor,
                        StartTimeMs,
                        "[0:v]",
                        sourceHasAudio ? "[0:a]" : null,
                        targetFps);
                    granularFilters = filterGraph;
                    gV = vLabel;
                    gA = aLabel;
                    gDur = finalDur;
                }

                int audioKbps = MediaProber.ChooseAudioBitrate(sourceAudioKbps, gDur, targetMb);
                int? videoBitrateKbps;
                if (keepHighestRes && qualityLevel >= 20 && !targetMb.HasValue)
                {
                    videoBitrateKbps = null; // CQ mode
                }
                else
                {
                    string outputRes = IsMobileFormat ? "1080x1920" : OriginalResolution;
                    videoBitrateKbps = MediaProber.CalculateVideoBitrate(
                        gDur, audioKbps, targetMb, keepHighestRes, qualityLevel, outputRes, targetFps);
                }

                // Music tracks setup
                var musicTracks = MusicTracks ?? new List<MusicTrack>();
                if (musicTracks.Count == 0 && MusicConfig != null)
                {
                    string? mPath = MusicConfig["path"]?.ToString();
                    if (!string.IsNullOrEmpty(mPath))
                    {
                        double mOffset = (double)(MusicConfig["file_offset_sec"]?.GetValue<double>() ?? 0);
                        musicTracks.Add(new MusicTrack(mPath, mOffset, gDur));
                    }
                }

                // Intro setup
                double introDurationSec = Math.Max(0, IntroStillSec);
                int? introInputIndex = introDurationSec > 0.001 ? 1 + musicTracks.Count : null;
                string? textInputLabel = textPngPath != null
                    ? $"[{1 + musicTracks.Count + (introInputIndex.HasValue ? 1 : 0)}:v]"
                    : null;

                double renderDurationSec = gDur + introDurationSec;

                // Execute loudnorm pass if requested and no VolumeNormalizeDb is pre-set
                if (VolumeNormalizeDb == 0.0 && sourceHasAudio)
                {
                    PhaseUpdate?.Invoke(1, "Analyzing Audio (Two-Pass Normalization)", 0);
                    await PerformLoudnormPassAsync(cancellationToken);
                }

                PhaseUpdate?.Invoke(2, "Encoding Video Pipeline", 0);

                // Build audio chain
                var (audioChains, finalALabel) = AudioFilterChain.Build(
                    MusicConfig,
                    StartTimeMs / 1000.0,
                    EndTimeMs / 1000.0,
                    SpeedFactor,
                    DisableFades,
                    DisableFades ? 0 : 0.5,
                    null,
                    48000,
                    musicTracks,
                    1,
                    gDur,
                    sourceHasAudio ? "[0:a]" : "",
                    VolumeNormalizeDb);

                // Build mobile filter chain
                JsonObject mobileCoords = await VideoConfig.GetMobileCoordinatesAsync(_paths);

                // Build complete filter script
                var coreFilters = new List<string>();
                string vOutputPad, vStabilizedPad, aPreparedPad;

                if (!string.IsNullOrEmpty(granularFilters))
                {
                    coreFilters.Add(granularFilters);
                    vStabilizedPad = gV;
                    aPreparedPad = gA;
                }
                else
                {
                    // Simple speed (no segments)
                    string cfrFilter = $"fps={targetFps}:round=near";
                    coreFilters.Add($"[0:v]setpts='(PTS-STARTPTS)/{SpeedFactor:F4}',{cfrFilter}[v_stabilized]");
                    vStabilizedPad = "[v_stabilized]";

                    if (sourceHasAudio)
                    {
                        var atempo = string.Join(",", GranularSpeedBuilder.BuildAtempoChain(SpeedFactor));
                        coreFilters.Add($"[0:a]asetpts=PTS-STARTPTS,{atempo},aresample=48000:async=1[a_prepared_base]");
                        aPreparedPad = "[a_prepared_base]";
                    }
                    else
                    {
                        coreFilters.Add($"anullsrc=r=48000:cl=stereo,atrim=duration={gDur:F4},asetpts=PTS-STARTPTS[a_prepared_base]");
                        aPreparedPad = "[a_prepared_base]";
                    }
                }

                // WhatsApp intro
                if (introDurationSec > 0 && introInputIndex.HasValue)
                {
                    int introFrames = Math.Max(1, (int)Math.Round(introDurationSec * 60.0));
                    int loopFrames = Math.Max(0, introFrames - 1);
                    coreFilters.Add($"[{introInputIndex}:v]trim=duration={Math.Max(0.2, introDurationSec + 0.1):F4}," +
                                   $"setpts=PTS-STARTPTS,select='eq(n\\,0)',setsar=1," +
                                   $"loop=loop={loopFrames}:size=1:start=0," +
                                   $"fps={targetFps}:round=near," +
                                   $"trim=duration={introDurationSec:F4},setpts=PTS-STARTPTS[v_intro_same_frame]");
                    coreFilters.Add($"{vStabilizedPad}setsar=1[v_main_after_intro]");
                    coreFilters.Add("[v_intro_same_frame][v_main_after_intro]concat=n=2:v=1:a=0[v_with_intro]");
                    vStabilizedPad = "[v_with_intro]";
                }

                // Mobile portrait conversion
                if (IsMobileFormat)
                {
                    var (mobileChain, mobileOut) = MobileFilterBuilder.Build(
                        vStabilizedPad, mobileCoords, IsBossHp, ShowTeammates, ShowSpectating,
                        textInputLabel, false, OriginalResolution);
                    coreFilters.Add(mobileChain);
                    vOutputPad = mobileOut;
                }
                else
                {
                    vOutputPad = vStabilizedPad;
                }

                // Audio chains
                string currentALabel = finalALabel;
                foreach (var part in audioChains)
                {
                    coreFilters.Add(part.Replace("[0:a]", aPreparedPad));
                }

                // Intro silence + audio concat
                if (introDurationSec > 0)
                {
                    coreFilters.Add($"anullsrc=r=48000:cl=stereo," +
                                   $"atrim=duration={introDurationSec:F4},asetpts=PTS-STARTPTS[a_intro_silence]");
                    coreFilters.Add($"[a_intro_silence]{currentALabel}concat=n=2:v=0:a=1[a_with_intro]");
                    currentALabel = "[a_with_intro]";
                }

                // Final FPS enforcement
                coreFilters.Add($"{vOutputPad}fps={targetFps}:round=near," +
                               $"setpts=N/({targetFps})/TB[v_render_out]");

                // Write filter script
                string filterScript = string.Join(";", coreFilters.Where(p => !string.IsNullOrEmpty(p)));
                string filterScriptPath = Path.Combine(tempJobDir, "filter_complex.txt");
                await File.WriteAllTextAsync(filterScriptPath, filterScript, cancellationToken);

                // Build ffmpeg command
                string corePath = Path.Combine(tempJobDir, "core.mp4");

                // Execute with encoder retry for file size targeting
                bool success = false;
                string lastError = "Render failed.";

                async Task<bool> RunFfmpegOnce(bool useCuda, int? requestedBitrate, int attemptNum)
                {
                    string currentEncoder = encoderMgr.GetInitialEncoder(useCuda);

                    while (true)
                    {
                        var (codecArgs, rcLabel) = encoderMgr.GetCodecFlags(
                            currentEncoder, requestedBitrate, gDur, targetFps, qualityLevel,
                            targetMb.HasValue);

                        var ffmpegArgs = new List<string>
                        {
                            "-y", "-hide_banner", "-progress", "pipe:1",
                            "-ss", (StartTimeMs / 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                            "-t", ((EndTimeMs - StartTimeMs) / 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                            "-i", InputPath,
                        };

                        // Music inputs
                        foreach (var track in musicTracks)
                            ffmpegArgs.AddRange(["-i", track.Path]);

                        // Intro input
                        if (introInputIndex.HasValue)
                        {
                            double introAbsSec = IntroAbsTimeMs.HasValue
                                ? IntroAbsTimeMs.Value / 1000.0
                                : StartTimeMs / 1000.0;
                            if (sourceDuration > 0.25)
                                introAbsSec = Math.Min(Math.Max(0, introAbsSec), Math.Max(0, sourceDuration - 0.2));
                            ffmpegArgs.AddRange(["-ss", introAbsSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "-t", Math.Max(0.2, introDurationSec + 0.1).ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "-i", InputPath]);
                        }

                        // Text PNG input
                        if (textPngPath != null)
                            ffmpegArgs.AddRange(["-loop", "1", "-i", textPngPath]);

                        // Filter and mapping
                        ffmpegArgs.AddRange(["-filter_complex_script", filterScriptPath]);
                        ffmpegArgs.AddRange(["-map", "[v_render_out]", "-map", currentALabel]);
                        ffmpegArgs.AddRange(codecArgs);
                        ffmpegArgs.AddRange(["-c:a", "aac", "-b:a", $"{audioKbps}k",
                            "-t", renderDurationSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                            "-movflags", "+faststart", corePath]);

                        string cmdLine = string.Join(" ", ffmpegArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
                        CoreLogger.Info("FFmpeg", $"Executing Final Pipeline Command:\n{_ffmpegPath} {cmdLine}");

                        var psi = new ProcessStartInfo
                        {
                            FileName = _ffmpegPath,
                            Arguments = cmdLine,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        };

                        _currentProcess = Process.Start(psi);
                        if (_currentProcess == null) return false;

                        try { ChildProcessTracker.AddProcess(_currentProcess); } catch { }

                        using var reg = cancellationToken.Register(() =>
                        {
                            try { _currentProcess.Kill(entireProcessTree: true); } catch { }
                        });

                        // Parse progress from stdout
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
                                        if (renderDurationSec > 0)
                                        {
                                            int percent = (int)Math.Clamp(currentSec / renderDurationSec * 100, 0, 100);
                                            int scaledPercent = percent;
                                            if (targetMb.HasValue && attemptNum == 1) scaledPercent = percent / 2;
                                            else if (targetMb.HasValue && attemptNum == 2) scaledPercent = 50 + (percent / 2);
                                            ProgressUpdate?.Invoke(scaledPercent);
                                            PhaseUpdate?.Invoke(2, "Encoding Video", scaledPercent);
                                        }
                                    }
                                }
                            }
                        }, cancellationToken);

                        // Async stderr reading to capture ffmpeg log
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
                            return true;

                        lastError = $"FFmpeg exited with code {_currentProcess.ExitCode}";
                        CoreLogger.Fail("FFmpeg", lastError);

                        // Encoder fallback
                        if (useCuda && !_isCanceled)
                        {
                            var fallbacks = encoderMgr.GetFallbackList(currentEncoder, false);
                            if (fallbacks.Count > 0)
                            {
                                currentEncoder = fallbacks[0];
                                continue;
                            }
                        }
                        return false;
                    }
                }

                // File-size targeting loop
                int? currentBitrate = videoBitrateKbps;
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    if (File.Exists(corePath)) File.Delete(corePath);
                    success = await RunFfmpegOnce(HardwareStrategy != "CPU", currentBitrate, attempt);
                    if (!success || !targetMb.HasValue) break;

                    long actualSize = File.Exists(corePath) ? new FileInfo(corePath).Length : 0;
                    long targetSize = (long)(targetMb.Value * 1024 * 1024);
                    double variance = targetSize * 0.01;

                    if (Math.Abs(actualSize - targetSize) <= variance) break;

                    // Adjust bitrate proportionally
                    if (actualSize > 0 && currentBitrate.HasValue)
                    {
                        currentBitrate = (int)(currentBitrate.Value * ((double)targetSize / actualSize));
                    }
                }

                if (!success)
                {
                    EmitFinished(false, lastError);
                    return;
                }

                // Move to output
                string outputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                string finalOutput = ResolveOutputPath(outputDir);
                File.Move(corePath, finalOutput);

                if (ThumbnailPosMs > 0)
                {
                    string thumbnailOutput = Path.ChangeExtension(finalOutput, ".jpg");
                    double ss = ThumbnailPosMs / 1000.0;
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = _ffmpegPath,
                        Arguments = $"-y -ss {ss.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)} -i \"{InputPath}\" -vframes 1 -q:v 2 \"{thumbnailOutput}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var p = System.Diagnostics.Process.Start(psi);
                    p?.WaitForExit();
                }

                ProgressUpdate?.Invoke(100);
                EmitFinished(true, finalOutput);
            }
            finally
            {
                try { if (Directory.Exists(tempJobDir)) Directory.Delete(tempJobDir, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("Process", $"Pipeline failed with exception: {ex}");
            EmitFinished(false, ex.Message);
        }
    }

    private static string ResolveOutputPath(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        int idx = 1;
        while (true)
        {
            string path = Path.Combine(outputDir, $"Fortnite-Video-{idx}.mp4");
            if (!File.Exists(path)) return path;
            idx++;
        }
    }

    private void EmitFinished(bool success, string message)
    {
        if (_finishEmitted) return;
        _finishEmitted = true;
        Finished?.Invoke(success, message);
    }

    private async Task PerformLoudnormPassAsync(CancellationToken cancellationToken)
    {
        try
        {
            double targetLufs = -14.0;
            var args = new List<string>
            {
                "-y", "-hide_banner",
                "-ss", (StartTimeMs / 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                "-t", ((EndTimeMs - StartTimeMs) / 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                "-i", InputPath,
                "-af", "loudnorm=I=-14:TP=-1.5:LRA=11:print_format=json",
                "-vn", "-sn", "-dn",
                "-f", "null", "-"
            };

            string cmdLine = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            CoreLogger.Info("Loudnorm", $"Executing pass 1: {_ffmpegPath} {cmdLine}");

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = cmdLine,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null) return;

            string stdErr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            // Extract input_i from JSON at end of stderr
            int jsonStart = stdErr.LastIndexOf("{");
            int jsonEnd = stdErr.LastIndexOf("}");
            if (jsonStart != -1 && jsonEnd != -1 && jsonEnd > jsonStart)
            {
                string jsonStr = stdErr.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var node = JsonNode.Parse(jsonStr);
                if (node != null && node["input_i"] != null)
                {
                    if (double.TryParse(node["input_i"]!.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double inputI))
                    {
                        VolumeNormalizeDb = targetLufs - inputI;
                        CoreLogger.Info("Loudnorm", $"Pass 1 complete. input_i={inputI} LUFS. Normalization DB applied: {VolumeNormalizeDb:F2} dB.");
                        return;
                    }
                }
            }
            CoreLogger.Fail("Loudnorm", "Failed to parse json block from loudnorm pass.");
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("Loudnorm", $"Exception during Pass 1: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _currentProcess?.Dispose();
    }
}
