
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

public class ProcessWorker : IDisposable
{
    private readonly ApplicationPaths _paths;
    private Process? _currentProcess;
    private volatile bool _isCanceled;
    private volatile bool _finishEmitted;
    private string _ffmpegPath;
    private string _ffprobePath;

    public event Action<int>? ProgressUpdate;
    public event Action<int, string, int>? PhaseUpdate;
    public event Action<bool, string>? Finished;

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
    public bool EnableFades { get; set; } = true;
    public string? MemeFile { get; set; }
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
    public bool KeepMusicDuringMeme { get; set; }

    public string? VoiceOverWavPath { get; set; }
    public double VoiceOverStartSec { get; set; } 
    public List<VoiceOverTake>? VoiceOverTakes { get; set; }
    public double VoiceOverMuteMaleHz { get; set; }
    public double VoiceOverMuteFemaleHz { get; set; }
    public double VoiceOverMuteChildHz { get; set; }
    public bool AutoVoiceNormalization { get; set; } = true;

    public bool VoiceOverMuteMale { get; set; }
    public bool VoiceOverMuteFemale { get; set; }
    public bool VoiceOverMuteChild { get; set; }

    public ProcessWorker(ApplicationPaths? paths = null)
    {
        _paths = paths ?? ApplicationPaths.CreateDefault();
        _ffmpegPath = FortniteVideoSoftware.Core.Infrastructure.BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
        _ffprobePath = FortniteVideoSoftware.Core.Infrastructure.BinaryPathResolver.Resolve("ffprobe.exe", "backend", "binaries");
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
            var encoderMgr = new EncoderManager(HardwareStrategy, _ffmpegPath);
            if (encoderMgr.EncoderPreflightError != null)
            {
                EmitFinished(false, encoderMgr.EncoderPreflightError);
                return;
            }

            string jobId = Guid.NewGuid().ToString("N")[..8];
            string tempJobDir = Path.Combine(_paths.TempDirectory, $"fvs_job_{jobId}");
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
                var prober = new MediaProber(_ffprobePath, InputPath);
                bool sourceHasAudio = await prober.HasAudioAsync();
                int sourceAudioKbps = await prober.GetAudioBitrateAsync();
                double sourceDuration = await prober.GetDurationAsync();
                OriginalResolution = await prober.GetResolutionStringAsync();

                var config = new VideoConfig();
                var (keepHighestRes, targetMb, qualityLevel) = config.GetQualitySettings(QualityLevel, TargetMbOverride);

                string targetFps = "60";

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

                double maxPadSec = 1.0;
                double minPadSec = 0.5;

                double availStartSec = StartTimeMs / 1000.0;
                double availEndSec = Math.Max(0, sourceDuration - (EndTimeMs / 1000.0));

                double padStartHumanSec = EnableFades ? Math.Min(maxPadSec, availStartSec / SpeedFactor) : 0;
                if (padStartHumanSec < minPadSec) padStartHumanSec = 0;
                double sourcePadStartSec = padStartHumanSec * SpeedFactor;

                double padEndHumanSec = EnableFades ? Math.Min(maxPadSec, availEndSec / SpeedFactor) : 0;
                if (padEndHumanSec < minPadSec) padEndHumanSec = 0;
                double sourcePadEndSec = padEndHumanSec * SpeedFactor;

                double memeDuration = 0;
                int memeWidth = 0;
                int memeHeight = 0;
                if (!string.IsNullOrEmpty(MemeFile) && File.Exists(MemeFile))
                {
                    var memeProber = new MediaProber(_ffprobePath, MemeFile);
                    memeDuration = await memeProber.GetDurationAsync();
                    var res = await memeProber.GetResolutionAsync();
                    memeWidth = res.width;
                    memeHeight = res.height;
                    
                    padEndHumanSec = 0;
                    sourcePadEndSec = 0;
                }

                double actualExtractStartMs = StartTimeMs - (sourcePadStartSec * 1000.0);
                double actualExtractEndMs = EndTimeMs + (sourcePadEndSec * 1000.0);

                var coreFilters = new List<string>();
                string baseAudioLabel = "[0:a]";

                if (sourceHasAudio && (VoiceOverMuteMale || VoiceOverMuteFemale || VoiceOverMuteChild))
                {
                    var eqFilters = new List<string>();
                    if (VoiceOverMuteMale && VoiceOverMuteMaleHz > 0)
                        eqFilters.Add($"equalizer=f={VoiceOverMuteMaleHz.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}:width_type=o:width=1.5:g=-20");
                    if (VoiceOverMuteFemale && VoiceOverMuteFemaleHz > 0)
                        eqFilters.Add($"equalizer=f={VoiceOverMuteFemaleHz.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}:width_type=o:width=1.5:g=-20");
                    if (VoiceOverMuteChild && VoiceOverMuteChildHz > 0)
                        eqFilters.Add($"equalizer=f={VoiceOverMuteChildHz.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}:width_type=o:width=1.5:g=-20");

                    if (eqFilters.Count > 0)
                    {
                        coreFilters.Add($"[0:a]{string.Join(",", eqFilters)}[a_muted]");
                        baseAudioLabel = "[a_muted]";
                    }
                }

                string granularFilters = "";
                string gV = "", gA = "";
                double gDur = (actualExtractEndMs - actualExtractStartMs) / 1000.0 / SpeedFactor;

                if (SpeedSegments != null && SpeedSegments.Count > 0)
                {
                    var (filterGraph, vLabel, aLabel, finalDur, _) = GranularSpeedBuilder.Build(
                        actualExtractEndMs - actualExtractStartMs,
                        SpeedSegments,
                        SpeedFactor,
                        actualExtractStartMs,
                        "[0:v]",
                        sourceHasAudio ? baseAudioLabel : null,
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
                    videoBitrateKbps = null;
                }
                else
                {
                    string outputRes = IsMobileFormat ? "1080x1920" : OriginalResolution;
                    videoBitrateKbps = MediaProber.CalculateVideoBitrate(
                        gDur, audioKbps, targetMb, keepHighestRes, qualityLevel, outputRes, targetFps);
                }

                var musicTracks = MusicTracks != null ? new List<MusicTrack>(MusicTracks) : new List<MusicTrack>();
                if (musicTracks.Count == 0 && MusicConfig != null)
                {
                    string? mPath = MusicConfig["path"]?.ToString();
                    if (!string.IsNullOrEmpty(mPath))
                    {
                        double mOffset = (double)(MusicConfig["file_offset_sec"]?.GetValue<double>() ?? 0);
                        musicTracks.Add(new MusicTrack(mPath, mOffset, gDur));
                    }
                }

                bool mixMusicAfterMeme = KeepMusicDuringMeme && memeDuration > 0 && musicTracks.Count > 0;
                if (mixMusicAfterMeme)
                {
                    for (int i = 0; i < musicTracks.Count; i++)
                    {
                        musicTracks[i] = musicTracks[i] with { Duration = musicTracks[i].Duration + memeDuration, ApplyFadeOut = false };
                    }
                }

                double introDurationSec = Math.Max(0, IntroStillSec);
                int? introInputIndex = introDurationSec > 0.001 ? 1 + musicTracks.Count : null;
                string? textInputLabel = textPngPath != null
                    ? $"[{1 + musicTracks.Count + (introInputIndex.HasValue ? 1 : 0)}:v]"
                    : null;
                
                int? memeInputIndex = MemeFile != null
                    ? 1 + musicTracks.Count + (introInputIndex.HasValue ? 1 : 0) + (textPngPath != null ? 1 : 0)
                    : null;

                double renderDurationSec = gDur + introDurationSec;

                if (VolumeNormalizeDb == 0.0 && sourceHasAudio)
                {
                    PhaseUpdate?.Invoke(1, "Analyzing Audio (Two-Pass Normalization)", 0);
                    await PerformLoudnormPassAsync(cancellationToken);
                }

                PhaseUpdate?.Invoke(2, "Encoding Video Pipeline", 0);

                List<string> audioChains = new();
                string finalALabel = sourceHasAudio ? "[0:a]" : "";
                
                if (!mixMusicAfterMeme)
                {
                    var built = AudioFilterChain.Build(
                        MusicConfig,
                        actualExtractStartMs / 1000.0,
                        actualExtractEndMs / 1000.0,
                        SpeedFactor,
                        true,
                        0,
                        null,
                        48000,
                        musicTracks,
                        1,
                        gDur,
                        sourceHasAudio ? baseAudioLabel : "",
                        VolumeNormalizeDb);
                    audioChains = built.chains;
                    finalALabel = built.finalLabel;
                }

                JsonObject mobileCoords = await VideoConfig.GetMobileCoordinatesAsync(_paths);

                string vOutputPad, vStabilizedPad, aPreparedPad;

                if (!string.IsNullOrEmpty(granularFilters))
                {
                    coreFilters.Add(granularFilters);
                    vStabilizedPad = gV;
                    aPreparedPad = gA;
                }
                else
                {
                    string cfrFilter = $"fps={targetFps}:round=near";
                    coreFilters.Add($"[0:v]setpts='(PTS-STARTPTS)/{SpeedFactor:F4}',{cfrFilter}[v_stabilized]");
                    vStabilizedPad = "[v_stabilized]";

                    if (sourceHasAudio)
                    {
                        var atempo = string.Join(",", GranularSpeedBuilder.BuildAtempoChain(SpeedFactor));
                        coreFilters.Add($"{baseAudioLabel}asetpts=PTS-STARTPTS,{atempo},aresample=48000:async=1[a_prepared_base]");
                        aPreparedPad = "[a_prepared_base]";
                    }
                    else
                    {
                        coreFilters.Add($"anullsrc=r=48000:cl=stereo,atrim=duration={gDur:F4},asetpts=PTS-STARTPTS[a_prepared_base]");
                        aPreparedPad = "[a_prepared_base]";
                    }

                }

                // VoiceOver take mixing — applies after BOTH granular and non-granular audio paths
                var effectiveTakes = GetEffectiveVoiceOverTakes();
                if (effectiveTakes.Count > 0)
                {
                    int voBaseIndex = 1 + musicTracks.Count + (introDurationSec > 0.001 ? 1 : 0) + (textPngPath != null ? 1 : 0) + (MemeFile != null ? 1 : 0);

                    double rawGameLufs = -14.0 - VolumeNormalizeDb;
                    rawGameLufs = Math.Clamp(rawGameLufs, -70.0, -5.0);
                    string voLoudnorm = $"loudnorm=I={rawGameLufs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}:LRA=11:TP=-1.5";

                    for (int t = 0; t < effectiveTakes.Count; t++)
                    {
                        var take = effectiveTakes[t];
                        int inputIdx = voBaseIndex + t;
                        int delayMs = (int)(take.StartSec * 1000);
                        string delayLabel = $"[vo_delayed_{t}]";
                        string mixedLabel = $"[a_mixed_vo_{t}]";

                        coreFilters.Add($"[{inputIdx}:a]aresample=48000:async=1,{voLoudnorm},adelay={delayMs}|{delayMs}{delayLabel}");
                        coreFilters.Add($"{aPreparedPad}{delayLabel}amix=inputs=2:duration=first:dropout_transition=2{mixedLabel}");
                        aPreparedPad = mixedLabel;
                    }
                }

                string currentALabel = aPreparedPad;
                if (!mixMusicAfterMeme)
                {
                    currentALabel = finalALabel;
                    foreach (var part in audioChains)
                    {
                        coreFilters.Add(part.Replace("[0:a]", aPreparedPad));
                    }
                }

                if (padStartHumanSec > 0 || padEndHumanSec > 0)
                {
                    double fadeVideoLengthSec = gDur; 
                    double fadeOutStart = Math.Max(0, fadeVideoLengthSec - padEndHumanSec);
                    
                    var vFades = new List<string>();
                    var aFades = new List<string>();

                    if (padStartHumanSec > 0)
                    {
                        vFades.Add($"fade=t=in:st=0:d={padStartHumanSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
                        aFades.Add($"afade=t=in:st=0:d={padStartHumanSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
                    }
                    if (padEndHumanSec > 0)
                    {
                        vFades.Add($"fade=t=out:st={fadeOutStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:d={padEndHumanSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
                        aFades.Add($"afade=t=out:st={fadeOutStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:d={padEndHumanSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
                    }

                    if (vFades.Count > 0)
                    {
                        coreFilters.Add($"{vStabilizedPad}{string.Join(",", vFades)}[v_faded]");
                        vStabilizedPad = "[v_faded]";

                        coreFilters.Add($"{currentALabel}{string.Join(",", aFades)}[a_faded]");
                        currentALabel = "[a_faded]";
                    }
                }

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

                if (introDurationSec > 0)
                {
                    coreFilters.Add($"anullsrc=r=48000:cl=stereo," +
                                   $"atrim=duration={introDurationSec:F4},asetpts=PTS-STARTPTS[a_intro_silence]");
                    coreFilters.Add($"[a_intro_silence]{currentALabel}concat=n=2:v=0:a=1[a_with_intro]");
                    currentALabel = "[a_with_intro]";
                }

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

                coreFilters.Add($"{vOutputPad}fps={targetFps}:round=near," +
                               $"setpts=N/({targetFps})/TB[v_render_out]");

                string vOutputFinal = "[v_render_out]";
                string aOutputFinal = currentALabel;

                if (memeInputIndex.HasValue)
                {
                    string memeRes = IsMobileFormat ? "1080:1920" : "1920:1080";
                    string memeScale;
                    double memeRatio = memeWidth > 0 && memeHeight > 0 ? (double)memeWidth / memeHeight : 1.777;

                    if (IsMobileFormat)
                    {
                        if (memeRatio >= 1.7)
                        {
                            memeScale = $"scale=1280:1920:force_original_aspect_ratio=increase,crop=1280:1920,scale=1080:1920:force_original_aspect_ratio=decrease,pad=1080:1920:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps={targetFps}:round=near";
                        }
                        else
                        {
                            memeScale = $"scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1,fps={targetFps}:round=near";
                        }
                    }
                    else
                    {
                        memeScale = $"scale=1920:1080:force_original_aspect_ratio=increase,crop=1920:1080,setsar=1,fps={targetFps}:round=near";
                    }

                    string memeAudio = "aresample=48000:async=1";

                    if (EnableFades && memeDuration >= 0.5)
                    {
                        double memeFadeDur = Math.Min(1.0, memeDuration / 2.0);
                        double memeFadeStart = Math.Max(0, memeDuration - memeFadeDur);
                        memeScale += $",fade=t=out:st={memeFadeStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:d={memeFadeDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}";
                        
                        if (!mixMusicAfterMeme)
                        {
                            memeAudio += $",afade=t=out:st={memeFadeStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:d={memeFadeDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}";
                        }
                    }

                    coreFilters.Add($"[{memeInputIndex}:v]{memeScale}[meme_v]");
                    coreFilters.Add($"[{memeInputIndex}:a]{memeAudio}[meme_a]");
                    coreFilters.Add($"[v_render_out]{aOutputFinal}[meme_v][meme_a]concat=n=2:v=1:a=1[v_final][a_final_before_music]");
                    vOutputFinal = "[v_final]";
                    aOutputFinal = "[a_final_before_music]";
                }

                if (mixMusicAfterMeme)
                {
                    var built = AudioFilterChain.Build(
                        MusicConfig,
                        actualExtractStartMs / 1000.0,
                        actualExtractEndMs / 1000.0,
                        SpeedFactor,
                        true, 
                        0,
                        null,
                        48000,
                        musicTracks,
                        1,
                        gDur + memeDuration,
                        aOutputFinal,
                        VolumeNormalizeDb);
                        
                    foreach (var part in built.chains)
                    {
                        coreFilters.Add(part);
                    }
                    aOutputFinal = built.finalLabel;
                    
                    if (EnableFades && memeDuration >= 0.5)
                    {
                        double memeFadeDur = Math.Min(1.0, memeDuration / 2.0);
                        double totalOutDur = gDur + introDurationSec + memeDuration;
                        double memeFadeStart = Math.Max(0, totalOutDur - memeFadeDur);
                        coreFilters.Add($"{aOutputFinal}afade=t=out:st={memeFadeStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:d={memeFadeDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}[a_final_music_faded]");
                        aOutputFinal = "[a_final_music_faded]";
                    }
                }

                string filterScript = string.Join(";", coreFilters.Where(p => !string.IsNullOrEmpty(p)));
                CoreLogger.Info("FFmpeg", $"Filter Script Content:\n{filterScript}");
                string filterScriptPath = Path.Combine(tempJobDir, "filter_complex.txt");
                await File.WriteAllTextAsync(filterScriptPath, filterScript, cancellationToken);

                string corePath = Path.Combine(tempJobDir, "core.mp4");

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
                            "-ss", (actualExtractStartMs / 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                            "-t", ((actualExtractEndMs - actualExtractStartMs) / 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                            "-i", InputPath,
                        };

                        foreach (var track in musicTracks)
                            ffmpegArgs.AddRange(["-i", track.Path]);

                        if (introInputIndex.HasValue)
                        {
                            double introAbsSec = IntroAbsTimeMs.HasValue
                                ? IntroAbsTimeMs.Value / 1000.0
                                : StartTimeMs / 1000.0;
                            if (sourceDuration > 0.25)
                                introAbsSec = Math.Min(Math.Max(0, introAbsSec), Math.Max(0, sourceDuration - 0.2));
                            ffmpegArgs.AddRange(["-ss", introAbsSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "-t", Math.Max(0.2, introDurationSec + 0.1).ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "-i", InputPath]);
                        }

                        if (textPngPath != null)
                            ffmpegArgs.AddRange(["-loop", "1", "-i", textPngPath]);

                        if (MemeFile != null)
                            ffmpegArgs.AddRange(["-i", MemeFile]);
                            
                        var voTakes = GetEffectiveVoiceOverTakes();
                        foreach (var voTake in voTakes)
                            ffmpegArgs.AddRange(["-i", voTake.Path]);

                        ffmpegArgs.AddRange(["-filter_complex_script", filterScriptPath]);
                        ffmpegArgs.AddRange(["-map", vOutputFinal, "-map", aOutputFinal]);
                        ffmpegArgs.AddRange(codecArgs);
                        double totalOutputDurationSec = renderDurationSec + memeDuration;
                        ffmpegArgs.AddRange(["-c:a", "aac", "-b:a", $"{audioKbps}k",
                            "-t", totalOutputDurationSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
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
                                        if (totalOutputDurationSec > 0)
                                        {
                                            int percent = (int)Math.Clamp(currentSec / totalOutputDurationSec * 100, 0, 100);
                                            int scaledPercent = 50 + (percent / 2);
                                            ProgressUpdate?.Invoke(scaledPercent);
                                            PhaseUpdate?.Invoke(2, "Encoding Video", scaledPercent);
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
                                    if (!line.StartsWith("frame=") && !line.StartsWith("size="))
                                    {
                                        CoreLogger.Append(line);
                                    }
                                }
                            }
                        }, cancellationToken);

                        await _currentProcess.WaitForExitAsync(cancellationToken);

                        if (_currentProcess.ExitCode == 0 && File.Exists(corePath) && new FileInfo(corePath).Length > 0)
                            return true;

                        lastError = $"FFmpeg exited with code {_currentProcess.ExitCode}";
                        CoreLogger.Fail("FFmpeg", lastError);

                        if (useCuda && !_isCanceled)
                        {
                            var fallbacks = encoderMgr.GetFallbackList(currentEncoder, true);
                            if (fallbacks.Count > 0)
                            {
                                currentEncoder = fallbacks[0];
                                continue;
                            }
                        }
                        return false;
                    }
                }

                int? currentBitrate = videoBitrateKbps;
                bool sizeTargetMet = !targetMb.HasValue;
                long finalActualSize = 0;
                long finalTargetSize = targetMb.HasValue ? (long)(targetMb.Value * 1024 * 1024) : 0;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    if (File.Exists(corePath)) File.Delete(corePath);
                    success = await RunFfmpegOnce(HardwareStrategy != "CPU", currentBitrate, attempt);
                    if (!success || !targetMb.HasValue) break;

                    finalActualSize = File.Exists(corePath) ? new FileInfo(corePath).Length : 0;
                    double variance = finalTargetSize * 0.05;

                    if (Math.Abs(finalActualSize - finalTargetSize) <= variance)
                    {
                        sizeTargetMet = true;
                        break;
                    }

                    if (finalActualSize > 0 && currentBitrate.HasValue)
                    {
                        currentBitrate = (int)(currentBitrate.Value * ((double)finalTargetSize / finalActualSize));
                    }
                }

                if (!success)
                {
                    EmitFinished(false, lastError);
                    return;
                }

                if (targetMb.HasValue && !sizeTargetMet)
                {
                    double actualMb = finalActualSize / 1024.0 / 1024.0;
                    lastError = $"Export size target was not met. Target={targetMb.Value:F2} MB, actual={actualMb:F2} MB.";
                    CoreLogger.Fail("FFmpeg", lastError);
                    EmitFinished(false, lastError);
                    return;
                }

                string outputDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                outputDir = Path.Combine(outputDir, "Downloads");
                string finalOutput = ResolveOutputPath(outputDir);
                File.Copy(corePath, finalOutput, true);
                try { File.Delete(corePath); } catch { }

                if (ThumbnailPosMs > 0)
                {
                    string thumbnailOutput = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(finalOutput) + "_thumbnail.jpg");
                    double extractTargetSec = Math.Max(0.0, (ThumbnailPosMs - actualExtractStartMs) / 1000.0 / Math.Max(0.001, SpeedFactor));
                    if (introDurationSec > 0.0)
                    {
                        extractTargetSec += introDurationSec;
                    }
                    string targetStr = extractTargetSec.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = _ffmpegPath,
                        Arguments = $"-y -ss {targetStr} -i \"{finalOutput}\" -vframes 1 -q:v 2 \"{thumbnailOutput}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var p = System.Diagnostics.Process.Start(psi);
                    if (p != null) await p.WaitForExitAsync(cancellationToken);
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

    private static string ResolveOutputPath(string defaultDir)
    {
        string outputDir = defaultDir;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders");
                if (key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string path && Directory.Exists(path))
                    outputDir = path;
            }
        }
        catch { }
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

            var lastLines = new System.Collections.Generic.Queue<string>(100);
            double totalDurationSec = (EndTimeMs - StartTimeMs) / 1000.0;
            
            using var reader = process.StandardError;
            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) continue;
                
                lastLines.Enqueue(line);
                if (lastLines.Count > 100) lastLines.Dequeue();
                
                int timeIdx = line.IndexOf("time=");
                if (timeIdx != -1)
                {
                    int endIdx = line.IndexOf(" ", timeIdx);
                    if (endIdx == -1) endIdx = line.Length;
                    string timeStr = line.Substring(timeIdx + 5, endIdx - (timeIdx + 5));
                    if (TimeSpan.TryParse(timeStr, out TimeSpan ts))
                    {
                        double currentSec = ts.TotalSeconds;
                        int percent = totalDurationSec > 0 ? (int)Math.Clamp(currentSec / totalDurationSec * 100, 0, 100) : 0;
                        int scaledPercent = percent / 2; // Phase 1 is 0-50%
                        ProgressUpdate?.Invoke(scaledPercent);
                        PhaseUpdate?.Invoke(1, "Analyzing Audio (Two-Pass Normalization)", scaledPercent);
                    }
                }
            }

            await process.WaitForExitAsync(cancellationToken);
            string stdErr = string.Join("\n", lastLines);

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

    private List<VoiceOverTake> GetEffectiveVoiceOverTakes()
    {
        if (VoiceOverTakes != null && VoiceOverTakes.Count > 0)
            return VoiceOverTakes;
        if (!string.IsNullOrEmpty(VoiceOverWavPath))
            return [new VoiceOverTake(VoiceOverWavPath, VoiceOverStartSec)];
        return [];
    }

    public void Dispose()
    {
        _currentProcess?.Dispose();
    }
}

public record VoiceOverTake(string Path, double StartSec);
