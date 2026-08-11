
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

    private const double AnalysisBandMax = 8.0;
    private const double EncodeBandMax = 96.0;

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // T01 — TWO-PASS PROGRESS SPLIT.
    // The encode band (encodeFloor .. EncodeBandMax) is carved into three sub-bands when the
    // two-pass route runs. Fractions, not absolute percentages, because `encodeFloor` is 8 when
    // the audio loudnorm analysis ran and 0 when it did not.
    //   0.00 -> 0.60 : Stage 1, the filter graph + near-lossless master (BY FAR the slowest part)
    //   0.60 -> 0.75 : Pass 1, complexity analysis (decodes the master, encodes to null)
    //   0.75 -> 1.00 : Pass 2, the real encode at the user's target size
    // These are wall-clock-proportional estimates, not guarantees; the monotonic EmitProgress gate
    // means an over- or under-run can only ever stall the bar, never rewind it (FIX_1).
    // ─────────────────────────────────────────────────────────────────────────────────────────
    private const double TwoPassGraphFraction = 0.60;
    private const double TwoPassAnalysisFraction = 0.75;

    /// <summary>
    /// T01 — SLOW-route split. Used only when the temp drive cannot hold the scratch master, in
    /// which case the filter graph genuinely runs twice and the two halves cost about the same.
    /// </summary>
    private const double TwoPassSlowPass1Fraction = 0.45;

    /// <summary>
    /// G09 — last `speed=` value FFmpeg reported (e.g. "3.4x"). Instance-scoped rather than a
    /// local so the two-pass stages can report through the same field. "?" until the first
    /// progress line arrives.
    /// </summary>
    public string LastReportedSpeed { get; private set; } = "?";

    private int _lastEmittedPct = -1;

    /// <summary>
    /// Single monotonic progress emitter. The bar can NEVER move backwards (kills the
    /// phase-seam reset-to-zero and the size-retry snap-back), so every consumer sees a
    /// steady forward sweep. Reset per job by construction (a fresh worker per export).
    /// </summary>
    private void EmitProgress(int phase, string title, int pct)
    {
        pct = Math.Clamp(pct, 0, 100);
        if (pct < _lastEmittedPct) pct = _lastEmittedPct;
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
    public double? IntroAbsTimeMs { get; set; }
    public bool KeepMusicDuringMeme { get; set; }

    /// <summary>
    /// ISSUE_04 — false when the music-start note marker sits to the RIGHT of MARK START, i.e.
    /// the music deliberately begins partway into the video. It then enters at full level with
    /// no fade-up. Set by the UI, which is the only layer that knows where the markers are.
    /// </summary>
    public bool MusicLeadFadeIn { get; set; } = true;

    /// <summary>
    /// ISSUE_04 — false when the music-end note marker sits to the RIGHT of MARK END, i.e. the
    /// music is asking to outlast the video. There is no video left to fade over, so it is cut
    /// dead at MARK END instead of fading out.
    /// </summary>
    public bool MusicTailFadeOut { get; set; } = true;

    public string? VoiceOverWavPath { get; set; }
    public double VoiceOverStartSec { get; set; } 
    public List<VoiceOverTake>? VoiceOverTakes { get; set; }
    public bool AutoVoiceNormalization { get; set; } = true;
    public bool AutoSpikeFlattening { get; set; } = true;

    /// <summary>
    /// Whether to normalise the SOURCE video's loudness to the streaming standard on export.
    ///
    /// This used to be unconditional and invisible: every export silently retargeted the user's
    /// audio to -14 LUFS whether they wanted it or not, and nothing in the UI ever said so. It is
    /// now the user's decision, taken on upload via the loudness warning dialog and remembered in
    /// Settings → Audio. Default stays true so behaviour is unchanged for anyone who never
    /// answers the dialog.
    /// </summary>
    public bool ApplyLoudnessNormalization { get; set; } = true;

    /// <summary>
    /// The source's measured integrated loudness (LUFS) from the upload-time probe, when the UI
    /// has one.
    ///
    /// This exists so the rest of the mix can stay anchored to the game even when the user
    /// declined normalisation — in that case no measurement pass runs during export, and without
    /// this the voice-over had nothing truthful to match itself against. Null simply means
    /// "nobody measured", and every consumer falls back rather than assuming a level.
    /// </summary>
    public double? SourceMeasuredLufs { get; set; }

    public bool VoiceOverDuckAudio { get; set; }

    /// <summary>
    /// ISSUE_04 — destination folder for the finished file, resolved by the UI layer
    /// (OutputFolderResolver) BEFORE the render starts. Null keeps the legacy behaviour of
    /// resolving Downloads here, but the UI always sets it so a missing Downloads folder is
    /// handled with a picker instead of a crash.
    /// </summary>
    public string? OutputDirectory { get; set; }

    /// <summary>
    /// ISSUE_06 — the raw error text (FFmpeg stderr tail / exception detail) behind the last
    /// failure. The UI feeds this to ErrorReporter, which mines the root-cause line out of it
    /// for the failure dialog. Never shown raw to the user.
    /// </summary>
    public string? FailureDetail { get; private set; }

    /// <summary>
    /// ISSUE_13 — measured loudness stats from the real first pass. When populated, the export
    /// graph runs a genuine second-pass <c>loudnorm</c> (linear mode) instead of the old flat
    /// gain delta, so true-peak and loudness range are actually corrected.
    /// </summary>
    private LoudnormMeasurement? _loudnorm;

    private sealed record LoudnormMeasurement(
        double InputI, double InputTp, double InputLra, double InputThresh, double TargetOffset);

    public ProcessWorker(ApplicationPaths? paths = null)
    {
        _paths = paths ?? ApplicationPaths.CreateDefault();
        _ffmpegPath = FortniteVideoSoftware.Core.Infrastructure.BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
        _ffprobePath = FortniteVideoSoftware.Core.Infrastructure.BinaryPathResolver.Resolve("ffprobe.exe", "backend", "binaries");
    }

    /// <summary>
    /// ISSUE_04 — the single message used for a user-initiated stop, so the UI can distinguish
    /// "you cancelled" from "something broke" without string-guessing.
    /// </summary>
    public const string CancelledMessage = "Export cancelled.";

    /// <summary>True when this job ended because the user stopped it, not because it failed.</summary>
    public bool WasCanceled => _isCanceled;

    /// <summary>
    /// ISSUE_12 — non-fatal problems that happened AFTER the video itself was written (currently
    /// only the thumbnail grab). The export still succeeded; the UI surfaces this as a warning so
    /// the user is not left hunting for a file that was never created.
    /// </summary>
    public string? CompletionWarning { get; private set; }

    public void Cancel()
    {
        _isCanceled = true;
        CoreLogger.Info("Process", "Cancellation requested by user. Terminating FFmpeg process tree.");
        if (_currentProcess != null)
        {
            try { _currentProcess.Kill(entireProcessTree: true); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
        }
    }

    /// <summary>
    /// ISSUE_04 — reads a child process's exit code without ever throwing.
    ///
    /// Callers reach here after a CANCELLABLE wait, so the process may not have finished dying
    /// yet (Kill is asynchronous). Give it a short grace period, then fall back to a sentinel
    /// rather than letting InvalidOperationException masquerade as a pipeline crash.
    /// </summary>
    private static int ReadExitCodeSafely(Process proc, string logTag, int graceMs = 5000)
    {
        try
        {
            if (!proc.HasExited)
            {
                if (!proc.WaitForExit(graceMs))
                {
                    try { proc.Kill(entireProcessTree: true); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
                    proc.WaitForExit(2000);
                }
            }
        }
        catch (Exception ex)
        {
            CoreLogger.Debug(logTag, $"Could not confirm process exit: {ex.Message}");
        }

        try { return proc.HasExited ? proc.ExitCode : -1; }
        catch (Exception ex)
        {
            CoreLogger.Debug(logTag, $"Exit code unavailable: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Runs the complete rendering pipeline. Returns true on success.
    /// Exact port of ProcessThread.run() logic flow.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var cancelMirror = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => _isCanceled = true)
            : default;

        try
        {
            var pipelineStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var encoderMgr = await Task.Run(() => new EncoderManager(HardwareStrategy, _ffmpegPath), cancellationToken).ConfigureAwait(false);
            if (encoderMgr.EncoderPreflightError != null)
            {
                FailureDetail = encoderMgr.EncoderPreflightError;
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
                    // ISSUE_04: this was `MusicConfig["ducking_threshold"]?.GetValue<double>() != 1.0`.
                    // When the key is ABSENT the null-conditional yields a null double?, and
                    // `null != 1.0` is TRUE — so a config with no ducking entry logged
                    // "Ducking Enabled: True" while the pipeline was actually falling back to
                    // AudioFilterChain's default. Anyone reading the log to diagnose an audio
                    // complaint was sent the wrong way. Now the three states are distinct.
                    double? duckThreshold = MusicConfig["ducking_threshold"]?.GetValue<double>();
                    string duckState = duckThreshold switch
                    {
                        null => "Default (no value in config)",
                        1.0 => "Off",
                        _ => "On"
                    };
                    CoreLogger.Info("Process", $"Music Ducking: {duckState}.");
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
                        targetFps,
                        needHudBranch: IsMobileFormat);
                    granularFilters = filterGraph;
                    gV = vLabel;
                    gVHud = hudLabel;
                    gA = aLabel;
                    gDur = finalDur;
                    granularTimeMapper = timeMapper;
                }

                double introDurationSec = Math.Max(0, IntroStillSec);
                double budgetDurationSec = gDur + introDurationSec + memeDuration;

                int audioKbps = MediaProber.ChooseAudioBitrate(sourceAudioKbps, budgetDurationSec, targetMb);
                int? videoBitrateKbps;
                if (keepHighestRes && qualityLevel >= 20 && !targetMb.HasValue)
                {
                    videoBitrateKbps = null;
                }
                else
                {
                    string outputRes = IsMobileFormat ? "1080x1920" : OriginalResolution;
                    videoBitrateKbps = MediaProber.CalculateVideoBitrate(
                        budgetDurationSec, audioKbps, targetMb, keepHighestRes, qualityLevel, outputRes, targetFps);
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
                        musicTracks[i] = musicTracks[i] with { Duration = musicTracks[i].Duration + memeDuration };
                    }
                }

                int? introInputIndex = introDurationSec > 0.001 ? 1 + musicTracks.Count : null;
                string? textInputLabel = textPngPath != null
                    ? $"[{1 + musicTracks.Count + (introInputIndex.HasValue ? 1 : 0)}:v]"
                    : null;
                
                int? memeInputIndex = MemeFile != null
                    ? 1 + musicTracks.Count + (introInputIndex.HasValue ? 1 : 0) + (textPngPath != null ? 1 : 0)
                    : null;

                double renderDurationSec = gDur + introDurationSec;

                {
                    long estimatedBytes = DiskSpaceGuard.EstimateOutputBytes(
                        renderDurationSec + memeDuration, videoBitrateKbps, targetMb);
                    string plannedOutputDir = ResolveOutputDirectory();
                    var space = DiskSpaceGuard.Check(_paths.TempDirectory, plannedOutputDir, estimatedBytes);
                    if (!space.Ok)
                    {
                        FailureDetail = space.Message;
                        EmitFinished(false, space.Message ?? "Not enough free disk space.");
                        return;
                    }
                }

                bool willAnalyzeAudio = ApplyLoudnessNormalization && VolumeNormalizeDb == 0.0 && sourceHasAudio;
                double encodeFloor = willAnalyzeAudio ? AnalysisBandMax : 0.0;

                double outIntro = introDurationSec;
                double bodyStart = outIntro, bodyEnd = outIntro + gDur;
                var costSpans = new List<(double s, double e, double w)>();
                if (outIntro > 0.001) costSpans.Add((0, outIntro, 0.3));
                costSpans.Add((bodyStart, bodyEnd, 1.0));
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
                        double extra = zoom ? (seg.ZoomSlow ? 2.0 : 1.0) : (freeze ? -0.6 : 0.0);
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

                EmitProgress(2, "Encoding Video Pipeline", (int)Math.Round(encodeFloor));

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

                bool hasSecondPass = false;
                var effectiveTakes = GetEffectiveVoiceOverTakes();
                if (effectiveTakes.Count > 0)
                {
                    if (sourceHasAudio && VoiceOverDuckAudio)
                    {
                        var conditions = new List<string>();
                        foreach (var take in effectiveTakes)
                        {
                            var p = new MediaProber(_ffprobePath, take.Path);
                            double dur = await p.GetDurationAsync();
                            
                            double voStartOutSec = granularTimeMapper != null
                                ? granularTimeMapper(take.StartSec)
                                : (take.StartSec - actualExtractStartMs / 1000.0) / SpeedFactor;
                            
                            double relStart = voStartOutSec;
                            double relEnd = relStart + dur;
                            
                            string sStr = relStart.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            string eStr = relEnd.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            
                            string sRamp = $"(t-({sStr}-0.3))/0.3";
                            string eRamp = $"(({eStr}+0.3)-t)/0.3";
                            string pulse = $"clip({sRamp},0,1)*clip({eRamp},0,1)";
                            conditions.Add(pulse);
                        }
                        if (conditions.Count > 0)
                        {
                            string combinedPulses = string.Join("+", conditions);
                            coreFilters.Add($"{aPreparedPad}volume='1.0-0.85*clip({combinedPulses},0,1)':eval=frame[a_ducked]");
                            aPreparedPad = "[a_ducked]";
                        }
                    }

                    int voBaseIndex = 1 + musicTracks.Count + (introDurationSec > 0.001 ? 1 : 0) + (textPngPath != null ? 1 : 0) + (MemeFile != null ? 1 : 0);

                    var secondPassFilter = BuildLoudnormSecondPassFilter();
                    hasSecondPass = !string.IsNullOrEmpty(secondPassFilter);
                    if (sourceHasAudio && hasSecondPass)
                    {
                        coreFilters.Add($"{aPreparedPad}{secondPassFilter},aresample=48000[a_master_leveled]");
                        aPreparedPad = "[a_master_leveled]";
                    }

                    double gameLufsForVoice =
                        _loudnorm?.InputI
                        ?? SourceMeasuredLufs
                        ?? (-14.0 - VolumeNormalizeDb);
                    gameLufsForVoice = Math.Clamp(gameLufsForVoice, -70.0, -5.0);
                    string voLoudnorm = AutoVoiceNormalization
                        ? $"loudnorm=I={gameLufsForVoice.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}:LRA=11:TP=-1.5"
                        : "anull";
                    CoreLogger.Info("Audio",
                        $"Voice-over target {gameLufsForVoice:F2} LUFS (matched to the game bus), " +
                        $"voice normalisation {(AutoVoiceNormalization ? "ON" : "OFF")}.");

                    string? finalVoLabel = null;
                    if (effectiveTakes.Count > 0)
                    {
                        var voMixedLabels = new List<string>();
                        for (int t = 0; t < effectiveTakes.Count; t++)
                        {
                            var take = effectiveTakes[t];
                            int inputIdx = voBaseIndex + t;
                            double voStartOutSec = granularTimeMapper != null
                                ? granularTimeMapper(take.StartSec)
                                : (take.StartSec - actualExtractStartMs / 1000.0) / SpeedFactor;
                            string trimFilter = "";
                            if (voStartOutSec < 0)
                            {
                                trimFilter = $"atrim=start={(-voStartOutSec).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS,";
                                voStartOutSec = 0;
                            }

                            int delayMs = Math.Max(0, (int)Math.Round(voStartOutSec * 1000.0));
                            string delayLabel = $"[vo_delayed_{t}]";
                            
                            coreFilters.Add($"[{inputIdx}:a]aresample=48000:async=1,{voLoudnorm},{trimFilter}adelay={delayMs}|{delayMs}{delayLabel}");
                            voMixedLabels.Add(delayLabel);
                        }
                        
                        if (voMixedLabels.Count > 1)
                        {
                            string mixInputs = string.Join("", voMixedLabels);
                            string weights = string.Join(" ", Enumerable.Repeat("1", voMixedLabels.Count));
                            finalVoLabel = $"[vo_mixed_all]";
                            coreFilters.Add($"{mixInputs}amix=inputs={voMixedLabels.Count}:duration=longest:dropout_transition=0:weights='{weights}':normalize=0{finalVoLabel}");
                        }
                        else
                        {
                            finalVoLabel = voMixedLabels[0];
                        }
                    }
                }
                else
                {
                    // Calculate hasSecondPass even if effectiveTakes is empty, so we pass correct args to AudioFilterChain
                    hasSecondPass = !string.IsNullOrEmpty(BuildLoudnormSecondPassFilter());
                }

                // If we didn't have effective takes but still need the variable
                string? finalVoLabelScope = effectiveTakes.Count > 0 ? "[vo_mixed_all]" : null; // handled below in a clean way

                string currentALabel = aPreparedPad;
                if (!mixMusicAfterMeme)
                {
                    var built = AudioFilterChain.Build(
                        MusicConfig,
                        actualExtractStartMs / 1000.0,
                        actualExtractEndMs / 1000.0,
                        SpeedFactor,
                        false,
                        0,
                        null,
                        48000,
                        musicTracks,
                        1,
                        gDur,
                        aPreparedPad,
                        hasSecondPass ? 0.0 : VolumeNormalizeDb,
                        null,
                        musicFollowGainDb: _loudnorm != null ? VolumeNormalizeDb : 0.0,
                        musicLeadFadeIn: MusicLeadFadeIn,
                        musicTailFadeOut: MusicTailFadeOut,
                        voiceOverLabel: effectiveTakes.Count > 0 ? (effectiveTakes.Count > 1 ? "[vo_mixed_all]" : "[vo_delayed_0]") : null);

                    foreach (var part in built.chains)
                    {
                        coreFilters.Add(part);
                    }
                    currentALabel = built.finalLabel;
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

                    if (!string.IsNullOrEmpty(gVHud))
                    {
                        coreFilters.Add($"[{introInputIndex}:v]trim=duration={Math.Max(0.2, introDurationSec + 0.1):F4}," +
                                       $"setpts=PTS-STARTPTS,select='eq(n\\,0)',setsar=1," +
                                       $"loop=loop={loopFrames}:size=1:start=0," +
                                       $"fps={targetFps}:round=near," +
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
                               $"setpts=N/({targetFps})/TB,format=yuv420p[v_render_out]");

                string vOutputFinal = "[v_render_out]";
                string aOutputFinal = currentALabel;

                if (memeInputIndex.HasValue)
                {
                    string canvas;
                    if (IsMobileFormat)
                    {
                        canvas = $"{CoordinateConstants.PortraitW}:{CoordinateConstants.PortraitH}";
                    }
                    else
                    {
                        var (srcW, srcH) = CoordinateMath.GetResolutionInts(OriginalResolution);
                        int memeCanvasW = Math.Max(2, srcW - (srcW % 2));
                        int memeCanvasH = Math.Max(2, srcH - (srcH % 2));
                        canvas = $"{memeCanvasW}:{memeCanvasH}";
                    }
                    CoreLogger.Info("FFmpeg", $"Meme canvas sized to {canvas.Replace(':', 'x')} to match the video output.");

                    string memeScale =
                        $"scale={canvas}:force_original_aspect_ratio=decrease," +
                        $"pad={canvas}:(ow-iw)/2:(oh-ih)/2:color=black," +
                        $"scale=w=floor(iw/2)*2:h=floor(ih/2)*2,format=yuv420p,setsar=1,fps={targetFps}:start_time=0:round=near";

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
                        false,
                        0,
                        null,
                        48000,
                        musicTracks,
                        1,
                        gDur + memeDuration,
                        aOutputFinal,
                        hasSecondPass ? 0.0 : VolumeNormalizeDb,
                        null,
                        musicFollowGainDb: _loudnorm != null ? VolumeNormalizeDb : 0.0,
                        musicLeadFadeIn: MusicLeadFadeIn,
                        musicTailFadeOut: MusicTailFadeOut,
                        voiceOverLabel: effectiveTakes.Count > 0 ? (effectiveTakes.Count > 1 ? "[vo_mixed_all]" : "[vo_delayed_0]") : null);

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

                coreFilters.Add($"{aOutputFinal}loudnorm=I=-23:TP=-1.5:LRA=11[a_mastered]");
                aOutputFinal = "[a_mastered]";

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

                // ── T01 two-pass state ───────────────────────────────────────────────────────
                // Both artefacts live in tempJobDir and are deleted the moment the job is done
                // (see CleanupTwoPassArtifacts + the job-dir teardown). Nothing survives an export.
                string twoPassMasterPath = Path.Combine(tempJobDir, "twopass_master.mp4");
                string twoPassLogPrefix = Path.Combine(tempJobDir, "twopass_stats");

                // Disabled for the rest of the job once the tail has failed, so the outer retry
                // cannot loop forever trying the same broken route.
                bool twoPassDisabled = false;

                // Set only when a two-pass run actually PRODUCED the delivered file. The
                // size-retry suppression below keys off this, not off "the encoder was libx264" —
                // those are not the same thing and conflating them would silently disable the
                // retry for ordinary single-pass CPU exports that still benefit from it.
                bool twoPassProducedResult = false;

                // The FAST route needs room for the scratch master. If it does not fit we do NOT
                // fall back to single-pass (the user asked for accurate size targeting) — we fall
                // back to plain -pass 1/-pass 2 over the filter graph, which costs ~2x but needs
                // no extra disk. HasRoomFor never fails an export; it only answers yes/no.
                bool twoPassFastRoute = DiskSpaceGuard.HasRoomFor(
                    tempJobDir, DiskSpaceGuard.EstimateTwoPassMasterBytes(renderDurationSec + memeDuration));

                bool success = false;
                string lastError = "Render failed.";

                string? lastSuccessfulEncoder = null;
                string? lastAttemptedEncoder = null;
                async Task<bool> RunFfmpegOnce(bool useCuda, int? requestedBitrate, int attemptNum)
                {
                    string currentEncoder = lastSuccessfulEncoder ?? lastAttemptedEncoder ?? encoderMgr.GetInitialEncoder(useCuda);

                    // T01 SLOW route only: 1 = analysis run pending, 2 = real run pending. On the
                    // FAST route the two passes are handled by RunTwoPassTailAsync and this stays 1.
                    int slowStage = 1;

                    while (true)
                    {
                        lastAttemptedEncoder = currentEncoder;

                        // ── T01 GATE ────────────────────────────────────────────────────────
                        // Two-pass runs ONLY when all three hold:
                        //   (1) a bitrate target exists — CRF/CQ exports have no budget to
                        //       redistribute, so a second pass would cost time for nothing;
                        //   (2) the encoder is libx264 — NVENC has no stats-file two-pass;
                        //   (3) the tail has not already failed this job.
                        // Every other combination takes the original single-pass path unchanged.
                        bool twoPass = requestedBitrate.HasValue
                                       && currentEncoder == "libx264"
                                       && !twoPassDisabled;

                        var (codecArgs, rcLabel) = encoderMgr.GetCodecFlags(
                            currentEncoder, requestedBitrate, gDur, targetFps, qualityLevel,
                            targetMb.HasValue);

                        var ffmpegArgs = new List<string>
                        {
                            "-y", "-hide_banner", "-progress", "pipe:1"
                        };

                        // ─────────────────────────────────────────────────────────────────────
                        // G04 — `-hwaccel` IS A PER-INPUT OPTION.
                        // It binds ONLY to the next `-i` on the command line. This block used to
                        // emit it exactly once, at the head of the arg list, so ONLY input 0 (the
                        // main clip) decoded on the GPU. The thumbnail-intro clone — which is the
                        // SAME 1080p/1440p file re-opened as a second input — and any video meme
                        // both decoded in software, on every single export, on every GPU machine.
                        // `decodeFlags` must therefore be re-emitted immediately before EVERY
                        // video `-i`. Image inputs (text PNG, image memes) are deliberately
                        // excluded: there is no hardware path for a looped still and adding the
                        // flag there just makes FFmpeg warn.
                        // NOTE: still no `-hwaccel_output_format` — see ISSUE_01 in
                        // project_structure.txt. Frames MUST come back to system memory because
                        // the whole filter graph below is software.
                        // ─────────────────────────────────────────────────────────────────────
                        var decodeFlags = EncoderManager.GetDecodeFlags(currentEncoder);

                        ffmpegArgs.AddRange(decodeFlags);
                        ffmpegArgs.AddRange([
                            "-ss", (actualExtractStartMs / 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                            "-t", ((actualExtractEndMs - actualExtractStartMs) / 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                            "-i", InputPath,
                        ]);

                        // Audio-only inputs: no hwaccel (there is nothing to decode on the GPU).
                        foreach (var track in musicTracks)
                            ffmpegArgs.AddRange(["-i", track.Path]);

                        if (introInputIndex.HasValue)
                        {
                            double introAbsSec = IntroAbsTimeMs.HasValue
                                ? IntroAbsTimeMs.Value / 1000.0
                                : StartTimeMs / 1000.0;
                            if (sourceDuration > 0.25)
                                introAbsSec = Math.Min(Math.Max(0, introAbsSec), Math.Max(0, sourceDuration - 0.2));
                            // G04: video input — needs its own decode flags.
                            ffmpegArgs.AddRange(decodeFlags);
                            ffmpegArgs.AddRange(["-ss", introAbsSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "-t", Math.Max(0.2, introDurationSec + 0.1).ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "-i", InputPath]);
                        }

                        // Still image — intentionally NO hwaccel.
                        if (textPngPath != null)
                            ffmpegArgs.AddRange(["-loop", "1", "-i", textPngPath]);

                        if (MemeFile != null)
                        {
                            if (memeIsImage)
                            {
                                // Still image — intentionally NO hwaccel.
                                ffmpegArgs.AddRange(["-loop", "1", "-framerate", targetFps, "-t", memeDuration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "-i", MemeFile]);
                            }
                            else
                            {
                                // G04: video meme — needs its own decode flags.
                                ffmpegArgs.AddRange(decodeFlags);
                                ffmpegArgs.AddRange(["-i", MemeFile]);
                            }
                        }

                        var voTakes = GetEffectiveVoiceOverTakes();
                        foreach (var voTake in voTakes)
                            ffmpegArgs.AddRange(["-i", voTake.Path]);

                        ffmpegArgs.AddRange(["-filter_complex_script", filterScriptPath]);
                        double totalOutputDurationSec = renderDurationSec + memeDuration;

                        // ── T01: what this FFmpeg invocation actually produces ───────────────
                        // FAST two-pass  -> the near-lossless master; passes 1+2 follow separately.
                        // SLOW two-pass  -> pass 1 straight off the filter graph (no master; the
                        //                   graph will simply be re-run for pass 2). Only used
                        //                   when the drive cannot hold the master.
                        // Single-pass    -> the finished file, exactly as before.
                        bool graphIsMaster = twoPass && twoPassFastRoute;
                        bool graphIsSlowPass1 = twoPass && !twoPassFastRoute && slowStage == 1;
                        bool graphIsSlowPass2 = twoPass && !twoPassFastRoute && slowStage == 2;

                        ffmpegArgs.AddRange(["-map", vOutputFinal, "-map", aOutputFinal]);

                        if (graphIsMaster)
                        {
                            // Audio is FINALISED here and stream-copied by pass 2, so it is
                            // encoded exactly once across the whole export.
                            ffmpegArgs.AddRange(TwoPassEncoding.MasterCodecArgs());
                            ffmpegArgs.AddRange(["-c:a", "aac", "-b:a", $"{audioKbps}k",
                                "-t", totalOutputDurationSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                                twoPassMasterPath]);
                        }
                        else if (graphIsSlowPass1)
                        {
                            // ⚠️ THE AUDIO BRANCH MUST STILL BE MAPPED AND ENCODED HERE.
                            // The obvious "optimisation" is `-an`, since audio contributes nothing
                            // to a video complexity map. It does not work: with `-filter_complex`,
                            // a labeled output that is never consumed makes FFmpeg abort with an
                            // unconnected-output error, and `-map [a] -an` is the same thing by
                            // another route. The null muxer discards both streams and the audio
                            // chain is trivial next to the video graph, so the cost is noise.
                            // (The FAST route's pass 1 CAN use `-an` — it reads a plain file, not
                            // a filter graph. See RunTwoPassTailAsync.)
                            ffmpegArgs.AddRange(TwoPassEncoding.PassArgs(requestedBitrate!.Value, 1, twoPassLogPrefix));
                            ffmpegArgs.AddRange(["-c:a", "aac", "-b:a", $"{audioKbps}k", "-sn", "-dn",
                                "-t", totalOutputDurationSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                                "-f", "null", "NUL"]);
                        }
                        else if (graphIsSlowPass2)
                        {
                            ffmpegArgs.AddRange(TwoPassEncoding.PassArgs(requestedBitrate!.Value, 2, twoPassLogPrefix));
                            ffmpegArgs.AddRange(["-c:a", "aac", "-b:a", $"{audioKbps}k",
                                "-t", totalOutputDurationSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                                "-movflags", "+faststart", corePath]);
                        }
                        else
                        {
                            ffmpegArgs.AddRange(codecArgs);
                            ffmpegArgs.AddRange(["-c:a", "aac", "-b:a", $"{audioKbps}k",
                                "-t", totalOutputDurationSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                                "-movflags", "+faststart", corePath]);
                        }

                        // T01: progress window for THIS invocation. On the ordinary single-pass
                        // path graphFloor/graphCeiling collapse to encodeFloor/EncodeBandMax, so
                        // the maths below is byte-identical to before.
                        double encodeBand = EncodeBandMax - encodeFloor;
                        double graphFloor = graphIsSlowPass2
                            ? encodeFloor + encodeBand * TwoPassSlowPass1Fraction
                            : encodeFloor;
                        double graphCeiling =
                            graphIsMaster ? encodeFloor + encodeBand * TwoPassGraphFraction :
                            graphIsSlowPass1 ? encodeFloor + encodeBand * TwoPassSlowPass1Fraction :
                            EncodeBandMax;
                        double pass1Ceiling = encodeFloor + encodeBand * TwoPassAnalysisFraction;

                        string cmdLine = FormatForLog(ffmpegArgs);
                        // G09: name the CHIPS, not just the codec. Nothing in the log used to
                        // distinguish "GPU decode + GPU encode" from "everything on the CPU",
                        // which is exactly why a fully dead hardware pipeline went unnoticed
                        // across entire sessions. This line is INFO on purpose — it contains no
                        // paths, so it is safe in production logs (see logging invariant L8).
                        string routeLabel = graphIsMaster
                            ? "two-pass FAST (rendering master, 2 passes follow)"
                            : graphIsSlowPass1
                                ? "two-pass SLOW (pass 1 over the filter graph — not enough temp disk for a master)"
                                : "single-pass";
                        CoreLogger.Info("FFmpeg", $"Starting encode: decode={EncoderManager.DescribeDecoder(currentEncoder)}, encode={EncoderManager.DescribeEncoder(currentEncoder)}, mode={rcLabel}, route={routeLabel}, attempt={attemptNum}.");
                        CoreLogger.Debug("FFmpeg", $"Executing Final Pipeline Command:\n{_ffmpegPath} {cmdLine}");

                        var psi = new ProcessStartInfo
                        {
                            FileName = _ffmpegPath,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        };

                        foreach (string arg in ffmpegArgs)
                        {
                            psi.ArgumentList.Add(arg);
                        }

                        var proc = Process.Start(psi);
                        if (proc == null) return false;
                        _currentProcess = proc;

                        bool disposedByGuard = false;
                        try
                        {

                        try { ChildProcessTracker.AddProcess(proc); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }

                        using var reg = cancellationToken.Register(() =>
                        {
                            try { proc.Kill(entireProcessTree: true); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
                        });

                        var progressTask = Task.Run(async () =>
                        {
                            using var reader = proc.StandardOutput;
                            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
                            {
                                var line = await reader.ReadLineAsync(cancellationToken);
                                if (line == null) continue;
                                if (line.StartsWith("out_time_us="))
                                {
                                    if (long.TryParse(line.AsSpan(12), out long outTimeUs))
                                    {
                                        double currentSec = outTimeUs / 1_000_000.0;
                                        if (totalOutputDurationSec > 0)
                                        {
                                            double frac = EncodeFraction(currentSec);
                                            // T01: graphFloor/graphCeiling collapse to
                                            // encodeFloor/EncodeBandMax on the single-pass path.
                                            int scaledPercent = (int)Math.Round(graphFloor + frac * (graphCeiling - graphFloor));
                                            EmitProgress(2,
                                                graphIsMaster ? "Preparing Video (1 of 3)" :
                                                graphIsSlowPass1 ? "Analyzing Video (1 of 2)" :
                                                graphIsSlowPass2 ? "Encoding Video (2 of 2)" :
                                                "Encoding Video",
                                                scaledPercent);
                                        }
                                    }
                                }
                                // G09: capture throughput so a slow run is diagnosable from the
                                // log alone. Cheap string slice; no allocation-heavy parsing.
                                else if (line.StartsWith("speed="))
                                {
                                    string v = line[6..].Trim();
                                    if (v.Length > 0 && v != "N/A") LastReportedSpeed = v;
                                }
                            }
                        }, cancellationToken);

                        var stderrChannel = System.Threading.Channels.Channel.CreateBounded<string>(new System.Threading.Channels.BoundedChannelOptions(400) { SingleWriter = true, FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });
                        var stderrTask = Task.Run(async () =>
                        {
                            using var reader = proc.StandardError;
                            while (!reader.EndOfStream)
                            {
                                string? line = await reader.ReadLineAsync(cancellationToken);
                                if (!string.IsNullOrWhiteSpace(line)
                                    && !line.StartsWith("frame=") && !line.StartsWith("size="))
                                {
                                    stderrChannel.Writer.TryWrite(line);
                                }
                            }
                        }, cancellationToken);

                        try { await proc.WaitForExitAsync(cancellationToken); }
                        catch (OperationCanceledException) { }

                        try { await Task.WhenAll(progressTask, stderrTask); }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { CoreLogger.Fail("FFmpeg", $"Reader task error: {ex.Message}"); }

                        string[] stderrLines;
                        var errList = new System.Collections.Generic.List<string>();
                        while (stderrChannel.Reader.TryRead(out var errLine)) errList.Add(errLine);
                        stderrLines = errList.ToArray();

                        int exitCode = ReadExitCodeSafely(proc, "FFmpeg");
                        _currentProcess = null;
                        proc.Dispose();
                        disposedByGuard = true;

                        if (_isCanceled || cancellationToken.IsCancellationRequested)
                        {
                            CoreLogger.Info("FFmpeg", "Encode stopped because the user cancelled.");
                            lastError = CancelledMessage;
                            FailureDetail = null;
                            return false;
                        }

                        // T01: what counts as "this invocation succeeded" depends on the route.
                        // The SLOW analysis pass writes to the null muxer, so there is no file to
                        // check — only the exit code matters.
                        string producedPath = graphIsMaster ? twoPassMasterPath : corePath;
                        bool producedOk = graphIsSlowPass1
                            ? exitCode == 0
                            : exitCode == 0 && File.Exists(producedPath) && new FileInfo(producedPath).Length > 0;

                        if (producedOk)
                        {
                            // ── SLOW route: analysis done, now re-run the graph for the real pass.
                            if (graphIsSlowPass1)
                            {
                                slowStage = 2;
                                CoreLogger.Info("FFmpeg", "Two-pass SLOW: analysis complete, starting the real pass.");
                                continue;
                            }

                            // ── FAST route: the master exists; run both passes off it.
                            if (graphIsMaster)
                            {
                                bool tailOk = await RunTwoPassTailAsync(
                                    twoPassMasterPath, corePath, twoPassLogPrefix,
                                    requestedBitrate!.Value, totalOutputDurationSec,
                                    graphCeiling, pass1Ceiling, EncodeBandMax, cancellationToken);

                                CleanupTwoPassArtifacts(twoPassMasterPath, twoPassLogPrefix);

                                if (_isCanceled || cancellationToken.IsCancellationRequested)
                                {
                                    lastError = CancelledMessage;
                                    FailureDetail = null;
                                    return false;
                                }

                                if (!tailOk)
                                {
                                    // Do NOT fail the export over this. Disable two-pass for the
                                    // rest of the job and fall straight through to the ordinary
                                    // single-pass encode — the user still gets a video. The flag
                                    // guarantees this cannot loop.
                                    twoPassDisabled = true;
                                    CoreLogger.Fail("FFmpeg",
                                        "Two-pass tail failed — falling back to a single-pass encode for this export.");
                                    if (File.Exists(corePath)) { try { File.Delete(corePath); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); } }
                                    continue;
                                }
                            }

                            lastSuccessfulEncoder = currentEncoder;
                            twoPassProducedResult = twoPass;

                            bool cpuFallback = currentEncoder == "libx264" && useCuda && !encoderMgr.ForcedCpu;
                            string passLabel = twoPass ? (twoPassFastRoute ? " route=two-pass(fast)" : " route=two-pass(slow)") : "";
                            // G09: the one line that makes a silent hardware downgrade impossible
                            // to miss. decode / encode / speed, in plain terms, at INFO.
                            CoreLogger.Info("FFmpeg",
                                $"PIPELINE RESULT: decode={EncoderManager.DescribeDecoder(currentEncoder)} " +
                                $"encode={EncoderManager.DescribeEncoder(currentEncoder)} speed={LastReportedSpeed}{passLabel}" +
                                (cpuFallback
                                    ? " — WARNING: this is the CPU fallback, the requested hardware encoder FAILED."
                                    : string.Empty));

                            if (stderrLines.Length > 0)
                                CoreLogger.Debug("FFmpeg", $"FFmpeg stderr (last {stderrLines.Length} lines):\n{string.Join("\n", stderrLines)}");
                            return true;
                        }

                        lastError = $"FFmpeg exited with code {exitCode}";
                        FailureDetail = stderrLines.Length > 0 ? string.Join("\n", stderrLines) : lastError;
                        CoreLogger.Fail("FFmpeg", lastError);
                        if (stderrLines.Length > 0)
                            CoreLogger.Fail("FFmpeg", $"FFmpeg stderr (last {stderrLines.Length} lines):\n{string.Join("\n", stderrLines)}");

                        if (useCuda && !_isCanceled)
                        {
                            var fallbacks = encoderMgr.GetFallbackList(currentEncoder, true);
                            if (fallbacks.Count > 0)
                            {
                                currentEncoder = fallbacks[0];
                                CoreLogger.Info("FFmpeg", $"Retrying with fallback encoder: {currentEncoder}.");
                                continue;
                            }
                        }
                        return false;
                        }
                        finally
                        {
                            if (!disposedByGuard)
                            {
                                _currentProcess = null;
                                try { proc.Dispose(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
                            }
                        }
                    }
                }

                string bestPath = corePath + ".best";
                long bestSize = 0;
                bool haveBest = false;

                int? currentBitrate = videoBitrateKbps;
                bool sizeTargetMet = !targetMb.HasValue;
                long finalActualSize = 0;
                long finalTargetSize = targetMb.HasValue ? (long)(targetMb.Value * 1024 * 1024) : 0;

                try
                {
                    for (int attempt = 1; attempt <= 2; attempt++)
                    {
                        if (File.Exists(corePath)) File.Delete(corePath);
                        // T01: never let a previous attempt's master or stats file leak into this
                        // one — a stale stats file would hand pass 2 a complexity map for a
                        // different bitrate, which is silent quality damage rather than an error.
                        CleanupTwoPassArtifacts(twoPassMasterPath, twoPassLogPrefix);

                        success = await RunFfmpegOnce(HardwareStrategy != "CPU", currentBitrate, attempt);

                        if (_isCanceled || cancellationToken.IsCancellationRequested)
                        {
                            success = false;
                            break;
                        }

                        if (!success)
                        {
                            if (haveBest)
                            {
                                CoreLogger.Fail("FFmpeg",
                                    "Size-target retry failed — delivering the earlier successful render instead of failing the export.");
                                if (File.Exists(corePath)) { try { File.Delete(corePath); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); } }

                                try
                                {
                                    File.Move(bestPath, corePath, overwrite: true);
                                    haveBest = false;
                                    finalActualSize = bestSize;
                                    success = true;
                                    sizeTargetMet = false;
                                }
                                catch (Exception ex)
                                {
                                    CoreLogger.Fail("FFmpeg",
                                        $"Could not restore the preserved render ({ex.Message}) — reporting the export as failed.");
                                    FailureDetail ??= $"The retry failed and the preserved render could not be restored: {ex.Message}";
                                }
                            }
                            break;
                        }

                        if (!targetMb.HasValue) break;

                        finalActualSize = File.Exists(corePath) ? new FileInfo(corePath).Length : 0;
                        double variance = finalTargetSize * 0.05;

                        if (Math.Abs(finalActualSize - finalTargetSize) <= variance)
                        {
                            sizeTargetMet = true;
                            break;
                        }

                        // ── T01: TWO-PASS REPLACES THE BITRATE-SCALING RETRY ────────────────
                        // The retry below was a blind guess: scale the bitrate by the size ratio
                        // and render everything again, with the code itself acknowledging the
                        // second attempt can land FURTHER from the target than the first.
                        // A completed two-pass run already distributed the exact budget it was
                        // given using a real complexity map of the whole timeline; if it still
                        // missed the ±5% band the bitrate estimate itself was off, and burning
                        // another full render on a guess is not the answer. Accept the result,
                        // log the miss, and stop — the user gets their video in ~1.35x rather
                        // than ~2.7x. The retry stays fully intact for the NVENC/CQ paths, which
                        // have no complexity map and genuinely can improve on a second guess.
                        if (twoPassProducedResult)
                        {
                            CoreLogger.Info("FFmpeg",
                                $"Two-pass landed at {finalActualSize / 1048576.0:F2} MB against a {finalTargetSize / 1048576.0:F2} MB target " +
                                "(outside the 5% band). Accepting it — a blind bitrate-scaling retry cannot beat a real complexity map.");
                            break;
                        }

                        if (attempt >= 2)
                        {
                            if (haveBest &&
                                Math.Abs(bestSize - finalTargetSize) < Math.Abs(finalActualSize - finalTargetSize))
                            {
                                CoreLogger.Info("FFmpeg",
                                    $"Retry landed further from the target ({finalActualSize / 1048576.0:F2} MB vs {bestSize / 1048576.0:F2} MB) — keeping the first render.");

                                try
                                {
                                    try { File.Delete(corePath); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
                                    File.Move(bestPath, corePath, overwrite: true);
                                    haveBest = false;
                                    finalActualSize = bestSize;
                                }
                                catch (Exception ex)
                                {
                                    CoreLogger.Fail("FFmpeg",
                                        $"Could not swap in the closer render ({ex.Message}) — delivering the retry instead.");

                                    if (!File.Exists(corePath) && File.Exists(bestPath))
                                    {
                                        try
                                        {
                                            File.Move(bestPath, corePath, overwrite: true);
                                            haveBest = false;
                                            finalActualSize = bestSize;
                                        }
                                        catch (Exception ex2)
                                        {
                                            CoreLogger.Fail("FFmpeg", $"Recovery move also failed: {ex2.Message}");
                                            success = false;
                                            FailureDetail ??= $"Both renders became unavailable while selecting the closest size match: {ex2.Message}";
                                        }
                                    }
                                }
                            }
                            break;
                        }

                        if (finalActualSize <= 0 || !currentBitrate.HasValue)
                        {
                            break;
                        }

                        try
                        {
                            if (File.Exists(bestPath)) File.Delete(bestPath);
                            File.Move(corePath, bestPath);
                            bestSize = finalActualSize;
                            haveBest = true;
                        }
                        catch (Exception ex)
                        {
                            CoreLogger.Fail("FFmpeg",
                                $"Could not preserve the first render before retrying ({ex.Message}) — delivering it as-is.");
                            break;
                        }

                        currentBitrate = (int)(currentBitrate.Value * ((double)finalTargetSize / finalActualSize));
                    }
                }
                finally
                {
                    if (File.Exists(bestPath)) { try { File.Delete(bestPath); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); } }
                }

                if (!success)
                {
                    if (_isCanceled || cancellationToken.IsCancellationRequested)
                    {
                        FailureDetail = null;
                        CoreLogger.Info("Process", "Export cancelled by the user.");
                        EmitFinished(false, CancelledMessage);
                        return;
                    }

                    EmitFinished(false, lastError);
                    return;
                }

                if (targetMb.HasValue && !sizeTargetMet)
                {
                    double actualMb = finalActualSize / 1024.0 / 1024.0;
                    CoreLogger.Fail("FFmpeg", $"Export size target not met after retries. Target={targetMb.Value:F2} MB, actual={actualMb:F2} MB. Delivering closest render.");
                }

                EmitProgress(2, "Finalizing", 97);
                string outputDir = ResolveOutputDirectory();
                string finalOutput = ResolveOutputPath(outputDir);

                try
                {
                    File.Copy(corePath, finalOutput, true);
                }
                catch (Exception copyEx)
                {
                    string? rescued = TryRescueFinishedRender(corePath);

                    CoreLogger.Fail("Output",
                        $"The finished render could not be copied to the destination: {copyEx.Message}");
                    CoreLogger.Debug("Output", $"Destination was: {finalOutput}");

                    if (rescued != null)
                    {
                        CoreLogger.Info("Output", $"Finished render preserved at: {Path.GetFileName(rescued)}");
                        CoreLogger.Debug("Output", $"Preserved render full path: {rescued}");
                        FailureDetail =
                            $"The video finished encoding but could not be written to the destination folder.{Environment.NewLine}" +
                            $"Reason: {copyEx.Message}{Environment.NewLine}" +
                            $"Your finished video has NOT been lost — it is here:{Environment.NewLine}{rescued}";
                        EmitFinished(false,
                            "Your video finished, but it could not be saved to the destination folder. " +
                            "It has been kept safe — see the details for where to find it.");
                    }
                    else
                    {
                        FailureDetail =
                            $"The video finished encoding but could not be written to the destination folder, " +
                            $"and the temporary copy could not be preserved either.{Environment.NewLine}Reason: {copyEx.Message}";
                        EmitFinished(false, "Your video finished, but it could not be saved to the destination folder.");
                    }
                    return;
                }

                try { File.Delete(corePath); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }

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

                    var thumbArgs = new List<string>
                    {
                        "-y", "-hide_banner",
                        "-ss", targetStr,
                        "-i", finalOutput,
                        "-vframes", "1",
                        "-q:v", "2",
                        thumbnailOutput
                    };
                    CoreLogger.Debug("Thumbnail", $"Executing: {_ffmpegPath} {FormatForLog(thumbArgs)}");

                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = _ffmpegPath,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    foreach (string arg in thumbArgs) psi.ArgumentList.Add(arg);

                    try
                    {
                        using var p = System.Diagnostics.Process.Start(psi);
                        if (p == null)
                        {
                            CompletionWarning = "The preview thumbnail could not be created (FFmpeg would not start).";
                            CoreLogger.Fail("Thumbnail", "Process.Start returned null for the thumbnail grab.");
                        }
                        else
                        {
                            var thumbErrTask = Task.Run(async () =>
                            {
                                var q = new System.Collections.Generic.Queue<string>(400);
                                using var reader = p.StandardError;
                                while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
                                {
                                    var line = await reader.ReadLineAsync(cancellationToken);
                                    if (line != null)
                                    {
                                        q.Enqueue(line);
                                        if (q.Count > 400) q.Dequeue();
                                    }
                                }
                                return string.Join("\n", q);
                            }, cancellationToken);

                            try { await p.WaitForExitAsync(cancellationToken); }
                            catch (OperationCanceledException) { }

                            string thumbErr = string.Empty;
                            try { thumbErr = await thumbErrTask; } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }

                            int thumbExit = ReadExitCodeSafely(p, "Thumbnail", graceMs: 2000);
                            bool thumbWritten = File.Exists(thumbnailOutput) && new FileInfo(thumbnailOutput).Length > 0;

                            if (thumbExit == 0 && thumbWritten)
                            {
                                CoreLogger.Info("Thumbnail", $"Thumbnail written: {Path.GetFileName(thumbnailOutput)}");
                            }
                            else if (!_isCanceled && !cancellationToken.IsCancellationRequested)
                            {
                                CompletionWarning =
                                    "Your video was exported, but the preview thumbnail could not be created.";
                                CoreLogger.Fail("Thumbnail",
                                    $"Thumbnail grab failed (exit {thumbExit}, file written: {thumbWritten}).");
                                if (!string.IsNullOrWhiteSpace(thumbErr))
                                    CoreLogger.Fail("Thumbnail", $"FFmpeg stderr:\n{thumbErr.Trim()}");

                                if (File.Exists(thumbnailOutput) && new FileInfo(thumbnailOutput).Length == 0)
                                {
                                    try { File.Delete(thumbnailOutput); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        CompletionWarning =
                            "Your video was exported, but the preview thumbnail could not be created.";
                        CoreLogger.Fail("Thumbnail", $"Thumbnail grab threw: {ex.Message}");
                    }
                }
                pipelineStopwatch.Stop();
                CoreLogger.Info("Process", $"Pipeline completed in {pipelineStopwatch.Elapsed.TotalSeconds:F1}s. Output: {Path.GetFileName(finalOutput)}");
                EmitProgress(2, "Complete", 100);
                EmitFinished(true, finalOutput);
            }
            finally
            {
                try { if (Directory.Exists(tempJobDir)) Directory.Delete(tempJobDir, true); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
            }
        }
        catch (OperationCanceledException)
        {
            _isCanceled = true;
            FailureDetail = null;
            CoreLogger.Info("Process", "Export cancelled by the user.");
            EmitFinished(false, CancelledMessage);
        }
        catch (Exception ex)
        {
            if (_isCanceled || cancellationToken.IsCancellationRequested)
            {
                FailureDetail = null;
                CoreLogger.Info("Process", $"Export cancelled by the user (during: {ex.Message}).");
                EmitFinished(false, CancelledMessage);
                return;
            }

            CoreLogger.Fail("Process", $"Pipeline failed with exception: {ex.Message}");
            CoreLogger.Debug("Process", $"Pipeline failed with exception detail: {ex}");
            FailureDetail = ex.ToString();
            EmitFinished(false, ex.Message);
        }
    }

    /// <summary>
    /// ISSUE_04 — where the finished file goes.
    /// <see cref="OutputDirectory"/> is set by the UI layer, which has already validated it and
    /// (if needed) asked the user to pick one. The shell-resolved Downloads folder and the
    /// %USERPROFILE% guess below exist only so a headless/gated code path still produces a file
    /// rather than throwing.
    /// </summary>
    private string ResolveOutputDirectory()
    {
        if (!string.IsNullOrWhiteSpace(OutputDirectory))
        {
            return OutputDirectory!;
        }

        string? downloads = KnownFolders.GetDownloads();
        if (!string.IsNullOrWhiteSpace(downloads))
        {
            return downloads!;
        }

        CoreLogger.Fail("Output",
            "No output folder was supplied and Downloads could not be resolved — falling back to the user profile.");
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>
    /// ISSUE_03 — moves a COMPLETED render out of the per-job temp folder, which the pipeline's
    /// <c>finally</c> block deletes wholesale, into the temp ROOT where it will survive.
    ///
    /// This is the safety net for the one moment where the expensive work is already done but the
    /// file is not yet at its destination. Returns the preserved path, or null if even that could
    /// not be managed (in which case there is genuinely nothing left to save).
    /// </summary>
    private string? TryRescueFinishedRender(string corePath)
    {
        try
        {
            if (!File.Exists(corePath)) return null;

            Directory.CreateDirectory(_paths.TempDirectory);

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string rescued = Path.Combine(_paths.TempDirectory, $"Fortnite-Video-RECOVERED-{stamp}.mp4");

            int n = 1;
            while (File.Exists(rescued))
            {
                rescued = Path.Combine(_paths.TempDirectory, $"Fortnite-Video-RECOVERED-{stamp}-{n}.mp4");
                n++;
            }

            File.Move(corePath, rescued);
            return rescued;
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("Output", $"Could not preserve the finished render: {ex.Message}");
            return null;
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

    /// <summary>
    /// ISSUE_15 — renders an argument list into a human-readable, copy-pasteable command for
    /// the DEBUG log only. Never used to launch a process.
    /// </summary>
    private static string FormatForLog(IEnumerable<string> args)
    {
        return string.Join(" ", args.Select(a =>
            a.Length == 0 || a.Contains(' ') || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a));
    }

    private void EmitFinished(bool success, string message)
    {
        if (_finishEmitted) return;
        _finishEmitted = true;
        Finished?.Invoke(success, message);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // T01 — TWO-PASS SIZE TARGETING (CPU / libx264 path only)
    //
    // WHAT IT SOLVES: with a size target the encoder previously got ONE blind bitrate guess. If
    // the finished file missed the ±5% band, the whole export was rendered AGAIN with a scaled
    // guess — and the retry could legitimately land FURTHER from the target than the first try
    // (see the "Retry landed further from the target" branch). Bits were also distributed evenly
    // across the timeline, so a static lobby got the same budget per second as a firefight.
    //
    // HOW IT WORKS: pass 1 encodes the whole timeline and writes a per-frame complexity map; pass
    // 2 reads that map and redistributes the SAME total budget toward the frames that need it.
    // Unlike `-rc-lookahead` (which only sees ~32 frames, half a second at 60fps) the map spans
    // the entire video, so it can take bits from the last quiet 20 seconds and spend them on a
    // 3-second fight at 0:45. Nothing here touches pixels — no blur, no smoothing, no filter
    // changes. Only the bit allocation differs.
    //
    // WHY A SCRATCH MASTER: naive `-pass 1`/`-pass 2` re-runs the ENTIRE filter graph for both
    // passes, and in this app the graph (14-18 way split, lanczos portrait scale, zoom pads, HUD
    // overlays) dominates wall clock — that would be ~2x. Instead the graph runs ONCE into a
    // near-lossless master, and both passes read that master: ~1.3-1.4x instead of ~2x.
    //
    // ⚠️ MANDATE #1 CARVE-OUT: `project_structure.txt` forbids caches for video pipelines. The
    // master is NOT a cache — it is created inside the job's own `tempJobDir`, consumed by the two
    // passes, and deleted in the same job. It is never reused across exports and never outlives
    // the job directory. Do NOT "optimise" it into something that survives an export.
    //
    // ⚠️ NVENC IS DELIBERATELY EXCLUDED. `h264_nvenc` does not implement libavcodec's stats-file
    // two-pass; its equivalent is `-multipass fullres` + `-rc-lookahead 32`, both already enabled
    // in EncoderManager and explicitly NOT to be changed. This route is libx264 only.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// T01 — runs the analysis pass and the real pass against the pre-rendered master.
    ///
    /// Audio is NOT re-encoded: it was finalised in the master and is stream-copied through, so it
    /// is encoded exactly once across the whole export and suffers no double loss.
    /// </summary>
    /// <returns>True when <paramref name="finalPath"/> was produced successfully.</returns>
    private async Task<bool> RunTwoPassTailAsync(
        string masterPath,
        string finalPath,
        string passLogPrefix,
        int videoBitrateKbps,
        double totalOutputDurationSec,
        double pass1Floor,
        double pass1Ceiling,
        double pass2Ceiling,
        CancellationToken cancellationToken)
    {
        // PASS 1 — analysis only. `-an` because audio contributes nothing to video complexity
        // stats and decoding it would be wasted work. Output is discarded via the null muxer.
        var pass1 = new List<string> { "-y", "-hide_banner", "-progress", "pipe:1", "-i", masterPath };
        pass1.AddRange(TwoPassEncoding.PassArgs(videoBitrateKbps, 1, passLogPrefix));
        pass1.AddRange(["-an", "-sn", "-dn", "-f", "null", "NUL"]);

        if (!await RunPassAsync(pass1, "Analyzing Video (2 of 3)", totalOutputDurationSec,
                                pass1Floor, pass1Ceiling, cancellationToken))
        {
            return false;
        }

        // PASS 2 — the real encode, reading the map pass 1 wrote.
        // `-c:a copy`: the audio was finalised in the master, so it is encoded exactly ONCE across
        // the whole export and suffers no second-generation loss.
        var pass2 = new List<string> { "-y", "-hide_banner", "-progress", "pipe:1", "-i", masterPath };
        pass2.AddRange(TwoPassEncoding.PassArgs(videoBitrateKbps, 2, passLogPrefix));
        pass2.AddRange(["-c:a", "copy", "-movflags", "+faststart", finalPath]);

        if (!await RunPassAsync(pass2, "Encoding Video (3 of 3)", totalOutputDurationSec,
                                pass1Ceiling, pass2Ceiling, cancellationToken))
        {
            return false;
        }

        return File.Exists(finalPath) && new FileInfo(finalPath).Length > 0;
    }

    /// <summary>
    /// T01 — runs one of the two passes, mapping its `-progress` output onto a progress sub-band.
    /// Deliberately small and self-contained: these invocations have no filter graph, no encoder
    /// fallback chain and no size-retry, so none of RunFfmpegOnce's machinery applies.
    /// </summary>
    private async Task<bool> RunPassAsync(
        List<string> args, string phaseTitle, double totalOutputDurationSec,
        double floor, double ceiling, CancellationToken cancellationToken)
    {
        CoreLogger.Info("FFmpeg", $"Two-pass: {phaseTitle}.");
        CoreLogger.Debug("FFmpeg", $"Two-pass command:\n{_ffmpegPath} {FormatForLog(args)}");

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        var proc = Process.Start(psi);
        if (proc == null)
        {
            CoreLogger.Fail("FFmpeg", $"Two-pass: could not start FFmpeg for {phaseTitle}.");
            return false;
        }

        // Cancellation must reach THIS process too, not just the filter-graph one.
        _currentProcess = proc;

        try
        {
            try { ChildProcessTracker.AddProcess(proc); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }

            using var reg = cancellationToken.Register(() =>
            {
                try { proc.Kill(entireProcessTree: true); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
            });

            var progressTask = Task.Run(async () =>
            {
                using var reader = proc.StandardOutput;
                while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line == null) continue;
                    if (line.StartsWith("out_time_us=") && long.TryParse(line.AsSpan(12), out long outTimeUs))
                    {
                        if (totalOutputDurationSec > 0)
                        {
                            double frac = Math.Clamp(outTimeUs / 1_000_000.0 / totalOutputDurationSec, 0.0, 1.0);
                            EmitProgress(2, phaseTitle, (int)Math.Round(floor + frac * (ceiling - floor)));
                        }
                    }
                    else if (line.StartsWith("speed="))
                    {
                        string v = line[6..].Trim();
                        if (v.Length > 0 && v != "N/A") LastReportedSpeed = v;
                    }
                }
            }, cancellationToken);

            var tail = new System.Collections.Generic.Queue<string>(40);
            var stderrTask = Task.Run(async () =>
            {
                using var reader = proc.StandardError;
                while (!reader.EndOfStream)
                {
                    string? line = await reader.ReadLineAsync(CancellationToken.None);
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("frame=") || line.StartsWith("size=")) continue;
                    tail.Enqueue(line);
                    if (tail.Count > 40) tail.Dequeue();
                }
            }, CancellationToken.None);

            try { await proc.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException) { return false; }

            try { await progressTask; } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
            try { await stderrTask; } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }

            int exitCode = ReadExitCodeSafely(proc, "FFmpeg");
            if (exitCode == 0) return true;

            string detail = tail.Count > 0 ? string.Join("\n", tail) : $"FFmpeg exited with code {exitCode}";
            FailureDetail = detail;
            CoreLogger.Fail("FFmpeg", $"Two-pass {phaseTitle} failed (exit {exitCode}).");
            CoreLogger.Fail("FFmpeg", $"FFmpeg stderr tail:\n{detail}");
            return false;
        }
        finally
        {
            _currentProcess = null;
            proc.Dispose();
        }
    }

    /// <summary>T01 — see <see cref="TwoPassEncoding.Cleanup"/>.</summary>
    private static void CleanupTwoPassArtifacts(string masterPath, string passLogPrefix)
        => TwoPassEncoding.Cleanup(masterPath, passLogPrefix);

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
                "-af", "loudnorm=I=-23:TP=-1.5:LRA=11:print_format=json",
                "-vn", "-sn", "-dn",
                "-f", "null", "-"
            };

            CoreLogger.Info("Loudnorm", "Executing pass 1 (measurement).");
            CoreLogger.Debug("Loudnorm", $"Executing pass 1: {_ffmpegPath} {FormatForLog(args)}");

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null) return;

            try { ChildProcessTracker.AddProcess(process); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }

            using var loudnormKill = cancellationToken.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
            });

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
                        int scaledPercent = (int)Math.Round(percent / 100.0 * AnalysisBandMax);
                        EmitProgress(1, "Analyzing Audio (Two-Pass Normalization)", scaledPercent);
                    }
                }
            }

            try { await process.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException) { return; }

            if (_isCanceled || cancellationToken.IsCancellationRequested) return;

            string stdErr = string.Join("\n", lastLines);

            int jsonStart = stdErr.LastIndexOf("{");
            int jsonEnd = stdErr.LastIndexOf("}");
            if (jsonStart != -1 && jsonEnd != -1 && jsonEnd > jsonStart)
            {
                string jsonStr = stdErr.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var node = JsonNode.Parse(jsonStr);
                if (node != null && node["input_i"] != null)
                {
                    double inputI = 0, inputTp = 0, inputLra = 0, inputThresh = 0, targetOffset = 0;

                    bool haveAll =
                        TryReadDouble(node, "input_i", out inputI) &&
                        TryReadDouble(node, "input_tp", out inputTp) &&
                        TryReadDouble(node, "input_lra", out inputLra) &&
                        TryReadDouble(node, "input_thresh", out inputThresh) &&
                        TryReadDouble(node, "target_offset", out targetOffset);

                    if (haveAll)
                    {
                        _loudnorm = new LoudnormMeasurement(inputI, inputTp, inputLra, inputThresh, targetOffset);

                        VolumeNormalizeDb = targetLufs - inputI;

                        CoreLogger.Info("Loudnorm",
                            $"Pass 1 complete. I={inputI:F2} LUFS, TP={inputTp:F2} dBTP, LRA={inputLra:F2} LU, " +
                            $"thresh={inputThresh:F2}, offset={targetOffset:F2}. Second pass will run in linear mode.");
                        return;
                    }

                    if (double.TryParse(node["input_i"]!.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double fallbackI))
                    {
                        _loudnorm = null;
                        VolumeNormalizeDb = targetLufs - fallbackI;
                        CoreLogger.Info("Loudnorm",
                            $"Pass 1 returned a partial measurement (input_i={fallbackI}). Falling back to a flat {VolumeNormalizeDb:F2} dB gain.");
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

    private static bool TryReadDouble(JsonNode node, string key, out double value)
    {
        value = 0;
        string? raw = node[key]?.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return false;

        if (raw.Contains("inf", StringComparison.OrdinalIgnoreCase)) return false;

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// ISSUE_13 — builds the genuine SECOND pass of a two-pass loudnorm from the pass-1
    /// measurement. Returns null when no usable measurement exists, in which case the caller
    /// keeps the legacy flat-gain behaviour.
    ///
    /// <c>linear=true</c> is what makes this a real two-pass: with the measured values supplied,
    /// loudnorm computes ONE constant gain for the whole track instead of the dynamic,
    /// range-squashing single-pass behaviour, and it backs that gain off if it would breach the
    /// true-peak ceiling.
    /// </summary>
    private string? BuildLoudnormSecondPassFilter()
    {
        if (_loudnorm is null) return null;

        var ci = CultureInfo.InvariantCulture;
        return "loudnorm=I=-23:TP=-1.5:LRA=11:linear=true" +
               $":measured_I={_loudnorm.InputI.ToString("F2", ci)}" +
               $":measured_TP={_loudnorm.InputTp.ToString("F2", ci)}" +
               $":measured_LRA={_loudnorm.InputLra.ToString("F2", ci)}" +
               $":measured_thresh={_loudnorm.InputThresh.ToString("F2", ci)}" +
               $":offset={_loudnorm.TargetOffset.ToString("F2", ci)}";
    }

    private List<VoiceOverTake> GetEffectiveVoiceOverTakes()
    {
        if (VoiceOverTakes != null && VoiceOverTakes.Count > 0)
            return VoiceOverTakes;
        if (!string.IsNullOrEmpty(VoiceOverWavPath))
            return [new VoiceOverTake(VoiceOverWavPath, VoiceOverStartSec)];
        return [];
    }

    /// <summary>
    /// ISSUE_11 — disposing the worker now also STOPS the encoder.
    ///
    /// WHAT WAS WRONG: this released the bookkeeping object and never told FFmpeg anything. Every
    /// caller is *expected* to call <see cref="Cancel"/> first, but nothing enforced it — so any
    /// path that disposed a worker without cancelling (an early return, an exception, a UI teardown
    /// that skipped a step) left a full-speed encode running on a file that would never be
    /// delivered, with the progress overlay already gone. The user saw fans at full tilt and a
    /// pegged CPU with nothing on screen to explain it.
    ///
    /// Killing the tree here makes teardown self-sufficient. Calling Cancel() first remains the
    /// correct, orderly path — this is the backstop, not a replacement for it.
    /// </summary>
    public void Dispose()
    {
        var proc = _currentProcess;
        if (proc != null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    _isCanceled = true;
                    CoreLogger.Info("Process", "Worker disposed while the encoder was still running — terminating the FFmpeg process tree.");
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }

            try { proc.Dispose(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
            _currentProcess = null;
        }
    }
}

public record VoiceOverTake(string Path, double StartSec);
