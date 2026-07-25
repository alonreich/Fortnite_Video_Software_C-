
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

    // Progress model (FIX_1..FIX_5): audio analysis is fast so it owns only a small band;
    // encoding (the real CPU/GPU work) owns the bulk; the last few percent are reserved for
    // the thumbnail + file-copy tail so "100%" only appears when the file is truly ready.
    private const double AnalysisBandMax = 8.0;   // loudnorm 0..8%
    private const double EncodeBandMax = 96.0;    // encode fills up to 96%; 96..100 = tail
    private int _lastEmittedPct = -1;

    /// <summary>
    /// Single monotonic progress emitter. The bar can NEVER move backwards (kills the
    /// phase-seam reset-to-zero and the size-retry snap-back), so every consumer sees a
    /// steady forward sweep. Reset per job by construction (a fresh worker per export).
    /// </summary>
    private void EmitProgress(int phase, string title, int pct)
    {
        pct = Math.Clamp(pct, 0, 100);
        if (pct < _lastEmittedPct) pct = _lastEmittedPct;   // forward-only
        _lastEmittedPct = pct;
        ProgressUpdate?.Invoke(pct);
        PhaseUpdate?.Invoke(phase, title, pct);
    }

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
    public bool AutoSpikeFlattening { get; set; } = true;

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
        CoreLogger.Info("Process", "Cancellation requested by user. Terminating FFmpeg process tree.");
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
            var pipelineStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var encoderMgr = await Task.Run(() => new EncoderManager(HardwareStrategy, _ffmpegPath), cancellationToken).ConfigureAwait(false);
            if (encoderMgr.EncoderPreflightError != null)
            {
                EmitFinished(false, encoderMgr.EncoderPreflightError);
                return;
            }

            string jobId = Guid.NewGuid().ToString("N")[..8];
            string tempJobDir = Path.Combine(_paths.TempDirectory, $"fvs_job_{jobId}");
            Directory.CreateDirectory(tempJobDir);

            CoreLogger.Info("Process", $"Input Video: {Path.GetFileName(InputPath)}");
            CoreLogger.Debug("Process", $"Input Path: {InputPath}");
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
                    CoreLogger.Info("Process", $"Music Selected: {Path.GetFileName(track.Path)} | Start Time: {track.Offset}s | Duration: {track.Duration}s");
                    CoreLogger.Debug("Process", $"Music Path: {track.Path}");
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
                bool memeIsImage = false;
                bool memeHasAudio = false;
                if (!string.IsNullOrEmpty(MemeFile))
                {
                    if (!File.Exists(MemeFile)) MemeFile = null;
                    else {
                    string memeExt = Path.GetExtension(MemeFile).ToLowerInvariant();
                    memeIsImage = memeExt is ".png" or ".jpg" or ".jpeg";

                    var memeProber = new MediaProber(_ffprobePath, MemeFile);
                    var res = await memeProber.GetResolutionAsync();
                    memeWidth = res.width;
                    memeHeight = res.height;

                    if (!memeIsImage)
                    {
                        memeDuration = await memeProber.GetDurationAsync();
                        if (memeDuration <= 0.0) memeIsImage = true;
                        else memeHasAudio = await memeProber.HasAudioAsync();
                    }

                    if (memeIsImage) memeDuration = 4.0;

                    padEndHumanSec = 0;
                    sourcePadEndSec = 0;
                    }
                }

                double actualExtractStartMs = StartTimeMs - (sourcePadStartSec * 1000.0);
                double actualExtractEndMs = EndTimeMs + (sourcePadEndSec * 1000.0);

                var coreFilters = new List<string>();
                string baseAudioLabel = "[0:a]";

                if (sourceHasAudio && (VoiceOverMuteMale || VoiceOverMuteFemale || VoiceOverMuteChild))
                {
                    var eqFilters = new List<string>();
                    if (VoiceOverMuteMale && VoiceOverMuteMaleHz > 0)
                        eqFilters.Add($"equalizer=f={VoiceOverMuteMaleHz.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}:t=o:w=1.5:g=-20");
                    if (VoiceOverMuteFemale && VoiceOverMuteFemaleHz > 0)
                        eqFilters.Add($"equalizer=f={VoiceOverMuteFemaleHz.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}:t=o:w=1.5:g=-20");
                    if (VoiceOverMuteChild && VoiceOverMuteChildHz > 0)
                        eqFilters.Add($"equalizer=f={VoiceOverMuteChildHz.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}:t=o:w=1.5:g=-20");

                    if (eqFilters.Count > 0)
                    {
                        coreFilters.Add($"[0:a]{string.Join(",", eqFilters)}[a_muted]");
                        baseAudioLabel = "[a_muted]";
                    }
                }

                string granularFilters = "";
                string gV = "", gVHud = "", gA = "";
                double gDur = (actualExtractEndMs - actualExtractStartMs) / 1000.0 / SpeedFactor;
                Func<double, double>? granularTimeMapper = null;

                if (SpeedSegments != null && SpeedSegments.Count > 0)
                {
                    var (filterGraph, vLabel, hudLabel, aLabel, finalDur, timeMapper) = GranularSpeedBuilder.Build(
                        actualExtractEndMs - actualExtractStartMs,
                        SpeedSegments,
                        SpeedFactor,
                        actualExtractStartMs,
                        "[0:v]",
                        sourceHasAudio ? baseAudioLabel : null,
                        targetFps);
                    granularFilters = filterGraph;
                    gV = vLabel;
                    gVHud = hudLabel;
                    gA = aLabel;
                    gDur = finalDur;
                    granularTimeMapper = timeMapper;
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

                bool willAnalyzeAudio = VolumeNormalizeDb == 0.0 && sourceHasAudio;
                // FIX_2: if analysis runs it owns 0..AnalysisBandMax; otherwise encoding starts at 0
                // so the bar never opens with a dead jump to 8%.
                double encodeFloor = willAnalyzeAudio ? AnalysisBandMax : 0.0;

                // FIX_6: cost map — weight each stretch of OUTPUT time by how expensive it is to
                // encode, so the bar advances by WORK done, not just by output timestamp. Zoom
                // (esp. slow ramp) and freeze frames encode at very different speeds than plain
                // passthrough; without this the bar stalls on heavy chunks and races on light ones.
                double outIntro = introDurationSec;
                double bodyStart = outIntro, bodyEnd = outIntro + gDur;
                var costSpans = new List<(double s, double e, double w)>();
                if (outIntro > 0.001) costSpans.Add((0, outIntro, 0.3));           // cloned still: cheap
                costSpans.Add((bodyStart, bodyEnd, 1.0));                          // main-body base
                if (SpeedSegments != null && granularTimeMapper != null)
                {
                    foreach (var seg in SpeedSegments)
                    {
                        double os, oe;
                        try { os = bodyStart + granularTimeMapper(seg.StartMs / 1000.0); oe = bodyStart + granularTimeMapper(seg.EndMs / 1000.0); }
                        catch { continue; }
                        os = Math.Max(bodyStart, os); oe = Math.Min(bodyEnd, oe);
                        if (oe <= os + 1e-3) continue;
                        bool freeze = seg.Speed < 0.01;
                        bool zoom = seg.ZoomW.HasValue && seg.ZoomH.HasValue;
                        double extra = zoom ? (seg.ZoomSlow ? 2.0 : 1.0) : (freeze ? -0.6 : 0.0); // additive delta over base 1.0
                        if (Math.Abs(extra) > 1e-6) costSpans.Add((os, oe, extra));
                    }
                }
                if (memeDuration > 0.001) costSpans.Add((bodyEnd, bodyEnd + memeDuration, 1.0));
                double totalCostW = costSpans.Sum(sp => sp.w * (sp.e - sp.s));
                double EncodeFraction(double outSec)
                {
                    if (totalCostW <= 1e-6) return Math.Clamp(outSec / Math.Max(1e-6, bodyEnd + memeDuration), 0, 1);
                    double acc = 0;
                    foreach (var sp in costSpans)
                    {
                        double hi = Math.Min(sp.e, outSec);
                        if (hi > sp.s) acc += sp.w * (hi - sp.s);
                    }
                    return Math.Clamp(acc / totalCostW, 0, 1);
                }

                if (willAnalyzeAudio)
                {
                    EmitProgress(1, "Analyzing Audio (Two-Pass Normalization)", 0);
                    await PerformLoudnormPassAsync(actualExtractStartMs, actualExtractEndMs, cancellationToken);
                }

                // FIX_3: hand off to the encode phase at the analysis-band ceiling, NOT 0
                // (the old PhaseUpdate(...,0) made the bar visibly drop 8 -> 0 -> back up).
                EmitProgress(2, "Encoding Video Pipeline", (int)Math.Round(encodeFloor));

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
                    string cfrFilter = $"fps={targetFps}:start_time=0:round=near";
                    coreFilters.Add($"[0:v]setpts='PTS/{SpeedFactor:F4}',{cfrFilter}[v_stabilized]");
                    vStabilizedPad = "[v_stabilized]";

                    if (sourceHasAudio)
                    {
                        var atempo = string.Join(",", GranularSpeedBuilder.BuildAtempoChain(SpeedFactor));
                        coreFilters.Add($"{baseAudioLabel}aresample=48000:async=1,asetpts=PTS,{atempo}[a_prepared_base]");
                        aPreparedPad = "[a_prepared_base]";
                    }
                    else
                    {
                        coreFilters.Add($"anullsrc=r=48000:cl=stereo,atrim=duration={gDur:F4},asetpts=PTS-STARTPTS[a_prepared_base]");
                        aPreparedPad = "[a_prepared_base]";
                    }

                }

                var effectiveTakes = GetEffectiveVoiceOverTakes();
                if (effectiveTakes.Count > 0)
                {
                    int voBaseIndex = 1 + musicTracks.Count + (introDurationSec > 0.001 ? 1 : 0) + (textPngPath != null ? 1 : 0) + (MemeFile != null ? 1 : 0);

                    double rawGameLufs = -14.0 - VolumeNormalizeDb;
                    rawGameLufs = Math.Clamp(rawGameLufs, -70.0, -5.0);
                    string voLoudnorm = AutoVoiceNormalization ? $"loudnorm=I={rawGameLufs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}:LRA=11:TP=-1.5" : "anull";

                    for (int t = 0; t < effectiveTakes.Count; t++)
                    {
                        var take = effectiveTakes[t];
                        int inputIdx = voBaseIndex + t;
                        double voStartOutSec = granularTimeMapper != null
                            ? granularTimeMapper(take.StartSec)
                            : (take.StartSec - actualExtractStartMs / 1000.0) / SpeedFactor;
                        int delayMs = Math.Max(0, (int)Math.Round(voStartOutSec * 1000.0));
                        string delayLabel = $"[vo_delayed_{t}]";
                        string mixedLabel = $"[a_mixed_vo_{t}]";

                        string voAtempo = string.Join(",", GranularSpeedBuilder.BuildAtempoChain(SpeedFactor));
                        coreFilters.Add($"[{inputIdx}:a]aresample=48000:async=1,{voLoudnorm},{voAtempo},adelay={delayMs}|{delayMs}{delayLabel}");
                        coreFilters.Add($"{aPreparedPad}{delayLabel}amix=inputs=2:duration=first:dropout_transition=2:normalize=0{mixedLabel}");
                        aPreparedPad = mixedLabel;
                    }
                }

                string currentALabel = aPreparedPad;
                if (!mixMusicAfterMeme)
                {
                    currentALabel = finalALabel;
                    foreach (var part in audioChains)
                    {
                        coreFilters.Add(part.Replace(sourceHasAudio ? baseAudioLabel : "[0:a]", aPreparedPad));
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

                        if (!string.IsNullOrEmpty(gVHud))
                        {
                            coreFilters.Add($"{gVHud}{string.Join(",", vFades)}[gVHud_faded]");
                            gVHud = "[gVHud_faded]";
                        }

                        coreFilters.Add($"{currentALabel}{string.Join(",", aFades)}[a_faded]");
                        currentALabel = "[a_faded]";
                    }
                }

                if (introDurationSec > 0 && introInputIndex.HasValue)
                {
                    coreFilters.Add($"[{introInputIndex}:v]trim=duration={Math.Max(0.2, introDurationSec + 0.1):F4}," +
                                   $"setpts=PTS-STARTPTS,select='eq(n\\,0)',setsar=1," +
                                   $"tpad=stop_mode=clone:stop_duration={introDurationSec:F4}," +
                                   $"fps={targetFps}:start_time=0:round=near," +
                                   $"trim=duration={introDurationSec:F4},setpts=PTS-STARTPTS[v_intro_same_frame]");
                    coreFilters.Add($"{vStabilizedPad}setsar=1[v_main_after_intro]");
                    coreFilters.Add("[v_intro_same_frame][v_main_after_intro]concat=n=2:v=1:a=0[v_with_intro]");
                    vStabilizedPad = "[v_with_intro]";

                    if (!string.IsNullOrEmpty(gVHud))
                    {
                        coreFilters.Add($"[{introInputIndex}:v]trim=duration={Math.Max(0.2, introDurationSec + 0.1):F4}," +
                                       $"setpts=PTS-STARTPTS,select='eq(n\\,0)',setsar=1," +
                                       $"tpad=stop_mode=clone:stop_duration={introDurationSec:F4}," +
                                       $"fps={targetFps}:start_time=0:round=near," +
                                       $"trim=duration={introDurationSec:F4},setpts=PTS-STARTPTS[v_intro_hud_same_frame]");
                        coreFilters.Add($"{gVHud}setsar=1[gVHud_after_intro]");
                        coreFilters.Add("[v_intro_hud_same_frame][gVHud_after_intro]concat=n=2:v=1:a=0[gVHud_with_intro]");
                        gVHud = "[gVHud_with_intro]";
                    }
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
                    string finalMainPad = vStabilizedPad;
                    string finalHudPad = gVHud;
                    if (string.IsNullOrEmpty(finalHudPad))
                    {
                        coreFilters.Add($"{vStabilizedPad}split=2[v_mob_main][v_mob_hud]");
                        finalMainPad = "[v_mob_main]";
                        finalHudPad = "[v_mob_hud]";
                    }
                    var (mobileChain, mobileOut) = MobileFilterBuilder.Build(
                        finalMainPad, finalHudPad, mobileCoords, IsBossHp, ShowTeammates, ShowSpectating,
                        textInputLabel, false, OriginalResolution);
                    coreFilters.Add(mobileChain);
                    vOutputPad = mobileOut;
                }
                else
                {
                    if (!string.IsNullOrEmpty(gVHud))
                    {
                        coreFilters.Add($"{gVHud}nullsink");
                    }
                    vOutputPad = vStabilizedPad;
                }

                coreFilters.Add($"{vOutputPad}fps={targetFps}:start_time=0:round=near," +
                               $"setpts=N/({targetFps})/TB[v_render_out]");

                string vOutputFinal = "[v_render_out]";
                string aOutputFinal = currentALabel;

                if (memeInputIndex.HasValue)
                {
                    string canvas = IsMobileFormat ? "1080:1920" : "1920:1080";
                    string memeScale =
                        $"scale={canvas}:force_original_aspect_ratio=decrease," +
                        $"pad={canvas}:(ow-iw)/2:(oh-ih)/2:color=black," +
                        $"scale=w=floor(iw/2)*2:h=floor(ih/2)*2,setsar=1,fps={targetFps}:start_time=0:round=near";

                    string memeAudio = "aresample=48000:async=1";

                    if (EnableFades && memeDuration >= 0.5)
                    {
                        double memeFadeDur = Math.Min(1.0, memeDuration / 2.0);
                        double memeFadeStart = Math.Max(0, memeDuration - memeFadeDur);
                        memeScale += $",fade=t=out:st={memeFadeStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:d={memeFadeDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}";

                        if (!mixMusicAfterMeme && memeHasAudio)
                        {
                            memeAudio += $",afade=t=out:st={memeFadeStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:d={memeFadeDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}";
                        }
                    }

                    coreFilters.Add($"[{memeInputIndex}:v]{memeScale}[meme_v]");
                    if (memeHasAudio)
                        coreFilters.Add($"[{memeInputIndex}:a]{memeAudio}[meme_a]");
                    else
                        coreFilters.Add($"anullsrc=r=48000:cl=stereo,atrim=duration={memeDuration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS[meme_a]");
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

                if (AutoSpikeFlattening)
                {
                    coreFilters.Add($"{aOutputFinal}alimiter=limit=-1.5dB:level_in=1:level_out=1[a_flattened]");
                    aOutputFinal = "[a_flattened]";
                }

                string filterScript = string.Join(";", coreFilters.Where(p => !string.IsNullOrEmpty(p)));
                CoreLogger.Debug("FFmpeg", $"Filter Script Content:\n{filterScript}");
                string filterScriptPath = Path.Combine(tempJobDir, "filter_complex.txt");
                await File.WriteAllTextAsync(filterScriptPath, filterScript, cancellationToken);

                string corePath = Path.Combine(tempJobDir, "core.mp4");

                bool success = false;
                string lastError = "Render failed.";

                string? lastSuccessfulEncoder = null;
                async Task<bool> RunFfmpegOnce(bool useCuda, int? requestedBitrate, int attemptNum)
                {
                    string currentEncoder = lastSuccessfulEncoder ?? encoderMgr.GetInitialEncoder(useCuda);

                    while (true)
                    {
                        var (codecArgs, rcLabel) = encoderMgr.GetCodecFlags(
                            currentEncoder, requestedBitrate, gDur, targetFps, qualityLevel,
                            targetMb.HasValue);

                        var ffmpegArgs = new List<string>
                        {
                            "-y", "-hide_banner", "-progress", "pipe:1"
                        };

                        if (currentEncoder == "h264_nvenc")
                        {
                            ffmpegArgs.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"]);
                        }
                        else if (currentEncoder == "h264_amf")
                        {
                            ffmpegArgs.AddRange(["-hwaccel", "d3d11va", "-hwaccel_output_format", "d3d11va"]);
                        }
                        else if (currentEncoder == "h264_qsv")
                        {
                            ffmpegArgs.AddRange(["-hwaccel", "qsv", "-hwaccel_output_format", "qsv"]);
                        }

                        ffmpegArgs.AddRange([
                            "-ss", (actualExtractStartMs / 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                            "-t", ((actualExtractEndMs - actualExtractStartMs) / 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                            "-i", InputPath,
                        ]);

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
                        {
                            if (memeIsImage)
                                ffmpegArgs.AddRange(["-loop", "1", "-framerate", targetFps, "-t", memeDuration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "-i", MemeFile]);
                            else
                                ffmpegArgs.AddRange(["-i", MemeFile]);
                        }

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
                        CoreLogger.Info("FFmpeg", $"Starting encode: encoder={currentEncoder}, mode={rcLabel}, attempt={attemptNum}.");
                        CoreLogger.Debug("FFmpeg", $"Executing Final Pipeline Command:\n{_ffmpegPath} {cmdLine}");

                        var psi = new ProcessStartInfo
                        {
                            FileName = _ffmpegPath,
                            Arguments = cmdLine,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        };

                        var proc = Process.Start(psi);
                        if (proc == null) return false;
                        _currentProcess = proc;

                        try { ChildProcessTracker.AddProcess(proc); } catch { }

                        using var reg = cancellationToken.Register(() =>
                        {
                            try { proc.Kill(entireProcessTree: true); } catch { }
                        });

                        var progressTask = Task.Run(async () =>
                        {
                            using var reader = proc.StandardOutput;
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
                                            // FIX_2/FIX_6: map cost-weighted output progress into the encode band.
                                            double frac = EncodeFraction(currentSec);
                                            int scaledPercent = (int)Math.Round(encodeFloor + frac * (EncodeBandMax - encodeFloor));
                                            EmitProgress(2, "Encoding Video", scaledPercent);
                                        }
                                    }
                                }
                            }
                        }, cancellationToken);

                        var stderrTail = new System.Collections.Generic.Queue<string>();
                        const int stderrTailMax = 400;
                        var stderrTask = Task.Run(async () =>
                        {
                            using var reader = proc.StandardError;
                            while (!reader.EndOfStream)
                            {
                                string? line = await reader.ReadLineAsync(cancellationToken);
                                if (!string.IsNullOrWhiteSpace(line)
                                    && !line.StartsWith("frame=") && !line.StartsWith("size="))
                                {
                                    lock (stderrTail)
                                    {
                                        stderrTail.Enqueue(line);
                                        if (stderrTail.Count > stderrTailMax) stderrTail.Dequeue();
                                    }
                                }
                            }
                        }, cancellationToken);

                        try { await proc.WaitForExitAsync(cancellationToken); }
                        catch (OperationCanceledException) { }

                        try { await Task.WhenAll(progressTask, stderrTask); }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { CoreLogger.Fail("FFmpeg", $"Reader task error: {ex.Message}"); }

                        string[] stderrLines;
                        lock (stderrTail) { stderrLines = stderrTail.ToArray(); }

                        int exitCode = proc.ExitCode;
                        _currentProcess = null;
                        proc.Dispose();

                        if (exitCode == 0 && File.Exists(corePath) && new FileInfo(corePath).Length > 0)
                        {
                            lastSuccessfulEncoder = currentEncoder;
                            if (stderrLines.Length > 0)
                                CoreLogger.Debug("FFmpeg", $"FFmpeg stderr (last {stderrLines.Length} lines):\n{string.Join("\n", stderrLines)}");
                            return true;
                        }

                        lastError = $"FFmpeg exited with code {exitCode}";
                        CoreLogger.Fail("FFmpeg", lastError);
                        if (stderrLines.Length > 0)
                            CoreLogger.Fail("FFmpeg", $"FFmpeg stderr (last {stderrLines.Length} lines):\n{string.Join("\n", stderrLines)}");

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
                for (int attempt = 1; attempt <= 2; attempt++)
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
                    CoreLogger.Fail("FFmpeg", $"Export size target not met after retries. Target={targetMb.Value:F2} MB, actual={actualMb:F2} MB. Delivering closest render.");
                }

                // FIX_5: the encoder tops out at EncodeBandMax (96%); the copy + thumbnail tail
                // fills the last few percent so 100% only shows when the file is truly ready.
                EmitProgress(2, "Finalizing", 97);
                string outputDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                outputDir = Path.Combine(outputDir, "Downloads");
                string finalOutput = ResolveOutputPath(outputDir);
                File.Copy(corePath, finalOutput, true);
                try { File.Delete(corePath); } catch { }

                if (ThumbnailPosMs > 0)
                {
                    EmitProgress(2, "Generating Thumbnail", 98);
                    string thumbnailOutput = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(finalOutput) + "_thumbnail.jpg");
                    double extractTargetSec = granularTimeMapper != null
                        ? Math.Max(0.0, granularTimeMapper(ThumbnailPosMs / 1000.0))
                        : Math.Max(0.0, (ThumbnailPosMs - actualExtractStartMs) / 1000.0 / Math.Max(0.001, SpeedFactor));
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
                pipelineStopwatch.Stop();
                CoreLogger.Info("Process", $"Pipeline completed in {pipelineStopwatch.Elapsed.TotalSeconds:F1}s. Output: {Path.GetFileName(finalOutput)}");
                EmitProgress(2, "Complete", 100);
                EmitFinished(true, finalOutput);
            }
            finally
            {
                try { if (Directory.Exists(tempJobDir)) Directory.Delete(tempJobDir, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("Process", $"Pipeline failed with exception: {ex.Message}");
            CoreLogger.Debug("Process", $"Pipeline failed with exception detail: {ex}");
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

    private async Task PerformLoudnormPassAsync(double measureStartMs, double measureEndMs, CancellationToken cancellationToken)
    {
        try
        {
            double targetLufs = -14.0;
            var args = new List<string>
            {
                "-y", "-hide_banner",
                "-ss", (measureStartMs / 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                "-t", ((measureEndMs - measureStartMs) / 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                "-i", InputPath,
                "-af", "loudnorm=I=-14:TP=-1.5:LRA=11:print_format=json",
                "-vn", "-sn", "-dn",
                "-f", "null", "-"
            };

            string cmdLine = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            CoreLogger.Info("Loudnorm", "Executing pass 1.");
            CoreLogger.Debug("Loudnorm", $"Executing pass 1: {_ffmpegPath} {cmdLine}");

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
            double totalDurationSec = (measureEndMs - measureStartMs) / 1000.0;
            
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
                        // FIX_2: analysis maps into the small 0..AnalysisBandMax band (was 0..50).
                        int scaledPercent = (int)Math.Round(percent / 100.0 * AnalysisBandMax);
                        EmitProgress(1, "Analyzing Audio (Two-Pass Normalization)", scaledPercent);
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
