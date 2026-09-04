
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

    /// <summary>
    /// MEME_03 — every meme in this export, each at its own moment. When this list is EMPTY the
    /// legacy <see cref="MemeFile"/> + <see cref="MemeAtStart"/> pair is used instead, so payloads
    /// written before multi-meme support still export identically. When it is non-empty it is
    /// authoritative and the legacy fields are ignored.
    /// </summary>
    public List<MemePlacement> MemePlacements { get; set; } = new();
    public string? PortraitText { get; set; }
    public JsonObject? MusicConfig { get; set; }
    public List<SpeedSegment>? SpeedSegments { get; set; }

    /// <summary>
    /// CUT_01 — stretches of footage deleted from the middle of the clip, in ABSOLUTE source ms.
    /// Null or empty is the historical behaviour: one unbroken clip.
    /// </summary>
    public List<CutRange>? Cuts { get; set; }
    public string HardwareStrategy { get; set; } = "CPU";
    public List<MusicTrack>? MusicTracks { get; set; }
    public double? TargetMbOverride { get; set; }
    public double ThumbnailPosMs { get; set; }
    public double VolumeNormalizeDb { get; set; }
    public double IntroStillSec { get; set; }
    public double? IntroAbsTimeMs { get; set; }

    /// <summary>
    /// MEME_02 — true when the meme should play BEFORE the gameplay instead of after it.
    /// ⚠️ Even when true the frozen thumbnail frame stays FIRST — see the concat block that
    /// consumes this. Default false keeps every existing caller on the historical behaviour.
    /// </summary>
    public bool MemeAtStart { get; set; }
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

    /// <summary>
    /// MEME_05 — ONE MEME, FULLY RESOLVED: probed, levelled, given an FFmpeg input slot, a pair of
    /// filter-graph labels and the exact moment of the rendered stream it cuts into.
    ///
    /// <para>
    /// ⚠️ <see cref="CutOutputSec"/> IS NOT <see cref="AtSourceSecRelative"/> CONVERTED NAIVELY. It
    /// is a position in <c>[v_render_out]</c> — the stream that has ALREADY had speed changes,
    /// freezes and the thumbnail intro applied but NOT the memes. Computed in any other clock, in
    /// particular one that already counts earlier memes, every meme after the first drifts later by
    /// the sum of the ones before it. See the block that fills this in.
    /// </para>
    ///
    /// <para>
    /// <see cref="SlotIndex"/> is the position in the final concat: 0 means "ahead of the first
    /// piece of rendered video", <c>n</c> means "between piece n-1 and piece n", and the last slot
    /// means "after everything".
    /// </para>
    /// </summary>
    private sealed class ResolvedMeme
    {
        public string Id = "meme";
        public string FilePath = "";
        public double AtSourceSecRelative;
        public double DurationSec;
        public bool IsImage;
        public bool HasAudio;
        public double LoudnessGainDb;
        public bool LoudnessMeasured;
        public int InputIndex;
        public double CutOutputSec;
        public int SlotIndex;

        public string VLabel => $"[{Id}_v]";
        public string ALabel => $"[{Id}_a]";
    }

    /// <summary>
    /// MEME_05 — an FFmpeg filter label may only contain letters, digits and underscores. Meme
    /// placements carry generated ids, but a payload written by hand — or recovered from an older
    /// crash file — could carry anything, and one stray bracket or comma silently corrupts the whole
    /// graph. Anything unexpected falls back to the positional name.
    /// </summary>
    private static string SanitizeMemeLabel(string? raw, int index)
    {
        if (string.IsNullOrWhiteSpace(raw)) return MemePlacement.NewId(index);

        var chars = new List<char>(raw.Length);
        foreach (char c in raw)
            if (char.IsAsciiLetterOrDigit(c) || c == '_') chars.Add(c);

        if (chars.Count == 0 || char.IsAsciiDigit(chars[0])) return MemePlacement.NewId(index);
        return new string(chars.ToArray());
    }

    /// <summary>MEME_05 — cut times are trim arguments; six decimals is well under a frame at 60fps.</summary>
    private static string CutSec(double seconds) =>
        Math.Max(0, seconds).ToString("F6", System.Globalization.CultureInfo.InvariantCulture);

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

        bool encoding = _currentProcess is { HasExited: false };
        CoreLogger.Info("Process", encoding
            ? "Cancellation requested by user. Terminating FFmpeg process tree."
            : "Export worker released on shutdown (no encode was running).");

        if (_currentProcess != null)
        {
            try { _currentProcess.Kill(entireProcessTree: true); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
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
                    try { proc.Kill(entireProcessTree: true); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
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
                    CoreLogger.Info("Process", $"Music Selected: {Path.GetFileName(track.Path)} | Start Time: {track.Offset}s | Duration: {track.Duration}s");
                    CoreLogger.Info("Process", $"Music Path: {track.Path}");
                }
                if (MusicConfig != null)
                {
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

                var memes = new List<ResolvedMeme>();
                {
                    var requested = new List<(string path, double atRel, string? id)>();
                    if (MemePlacements != null && MemePlacements.Count > 0)
                    {
                        foreach (var p in MemePlacements)
                        {
                            if (p == null || string.IsNullOrWhiteSpace(p.FilePath)) continue;
                            requested.Add((p.FilePath, p.AtSourceSecRelative, p.Id));
                        }
                    }
                    else if (!string.IsNullOrEmpty(MemeFile))
                    {
                        double clipLenSec = Math.Max(0, (EndTimeMs - StartTimeMs) / 1000.0);
                        requested.Add((MemeFile, MemeAtStart ? 0.0 : clipLenSec, "meme"));
                    }

                    var usedIds = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < requested.Count; i++)
                    {
                        var (path, atRel, rawId) = requested[i];
                        if (!File.Exists(path))
                        {
                            CoreLogger.Fail("Meme",
                                $"Meme file no longer exists and was skipped: '{Path.GetFileName(path)}'.");
                            continue;
                        }

                        string id = SanitizeMemeLabel(rawId, i);
                        while (!usedIds.Add(id)) id += "_d";

                        var m = new ResolvedMeme
                        {
                            Id = id,
                            FilePath = path,
                            AtSourceSecRelative = atRel
                        };

                        string memeExt = Path.GetExtension(path).ToLowerInvariant();
                        m.IsImage = memeExt is ".png" or ".jpg" or ".jpeg";

                        var memeProber = new MediaProber(_ffprobePath, path);
                        if (!m.IsImage)
                        {
                            m.DurationSec = await memeProber.GetDurationAsync();
                            if (m.DurationSec <= 0.0) m.IsImage = true;
                            else m.HasAudio = await memeProber.HasAudioAsync();
                        }

                        if (m.IsImage) m.DurationSec = MemePlacement.StillImageDurationSec;

                        if (m.HasAudio && !m.IsImage)
                        {
                            try
                            {
                                var memeReading = await AudioLoudnessProbe
                                    .MeasureAsync(_ffmpegPath, path, cancellationToken).ConfigureAwait(false);
                                if (memeReading != null)
                                {
                                    m.LoudnessGainDb = Math.Clamp(
                                        AudioLoudnessProbe.TargetLufs - memeReading.IntegratedLufs,
                                        AudioLoudnessProbe.MinMusicGainDb,
                                        AudioLoudnessProbe.MaxMusicGainDb);
                                    m.LoudnessMeasured = true;
                                    CoreLogger.Info("Audio",
                                        $"MEME LEVEL PLAN: '{Path.GetFileName(path)}' measured " +
                                        $"{memeReading.IntegratedLufs:F2} LUFS -> target {AudioLoudnessProbe.TargetLufs:F1} LUFS " +
                                        $"= {m.LoudnessGainDb:+0.00;-0.00} dB, then a peak limiter to clamp off-the-chart spikes.");
                                }
                                else
                                {
                                    CoreLogger.Info("Audio",
                                        $"Meme level could not be measured for '{Path.GetFileName(path)}' — leaving it as recorded.");
                                }
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                CoreLogger.Info("Audio", $"Meme level measurement skipped: {ex.Message}");
                            }
                        }

                        memes.Add(m);
                    }

                    if (memes.Count == 0)
                    {
                        MemeFile = null;
                    }
                    else
                    {
                        double clipLenSec = Math.Max(0, (EndTimeMs - StartTimeMs) / 1000.0);
                        bool aMemeEndsTheVideo = memes.Any(m => m.AtSourceSecRelative >= clipLenSec - 0.001);
                        if (aMemeEndsTheVideo)
                        {
                            padEndHumanSec = 0;
                            sourcePadEndSec = 0;
                        }
                        else
                        {
                            CoreLogger.Info("Meme",
                                "Every meme lands inside the clip, so the gameplay still ends the video — " +
                                "its closing fade pad is kept.");
                        }
                    }
                }

                double memeTotalDuration = 0;
                foreach (var m in memes) memeTotalDuration += m.DurationSec;

                double actualExtractStartMs = StartTimeMs - (sourcePadStartSec * 1000.0);
                double actualExtractEndMs = EndTimeMs + (sourcePadEndSec * 1000.0);

                var coreFilters = new List<string>();
                string baseAudioLabel = "[0:a]";


                string granularFilters = "";
                string gV = "", gVHud = "", gA = "";
                double gDur = (actualExtractEndMs - actualExtractStartMs) / 1000.0 / SpeedFactor;
                Func<double, double>? granularTimeMapper = null;

                // CUT_01 — cuts are built by the SAME chunk/concat engine as speed segments, so
                // the granular path must be taken when there are cuts even with no speed segments.
                // Without this the export silently ignored every cut whenever the user had not also
                // used the speed editor — which is the normal case.
                var exportCuts = CutRange.ToClipRelative(Cuts, actualExtractStartMs);
                bool hasCuts = exportCuts.Count > 0;

                if ((SpeedSegments != null && SpeedSegments.Count > 0) || hasCuts)
                {
                    var (filterGraph, vLabel, hudLabel, aLabel, finalDur, timeMapper) = GranularSpeedBuilder.Build(
                        actualExtractEndMs - actualExtractStartMs,
                        SpeedSegments,
                        SpeedFactor,
                        actualExtractStartMs,
                        "[0:v]",
                        sourceHasAudio ? baseAudioLabel : null,
                        targetFps,
                        needHudBranch: IsMobileFormat,
                        cuts: exportCuts);
                    granularFilters = filterGraph;
                    gV = vLabel;
                    gVHud = hudLabel;
                    gA = aLabel;
                    gDur = finalDur;
                    granularTimeMapper = timeMapper;
                }

                double introDurationSec = Math.Max(0, IntroStillSec);
                double budgetDurationSec = gDur + introDurationSec + memeTotalDuration;

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

                var musicBedGains = musicTracks.Count > 0
                    ? await MeasureMusicBedGainsAsync(musicTracks, cancellationToken)
                    : new Dictionary<string, double>();

                bool mixMusicAfterMeme = KeepMusicDuringMeme && memeTotalDuration > 0 && musicTracks.Count > 0;

                int? introInputIndex = introDurationSec > 0.001 ? 1 + musicTracks.Count : null;
                string? textInputLabel = textPngPath != null
                    ? $"[{1 + musicTracks.Count + (introInputIndex.HasValue ? 1 : 0)}:v]"
                    : null;
                
                int memeInputBase = 1 + musicTracks.Count + (introInputIndex.HasValue ? 1 : 0) + (textPngPath != null ? 1 : 0);
                for (int i = 0; i < memes.Count; i++) memes[i].InputIndex = memeInputBase + i;

                double renderDurationSec = gDur + introDurationSec;

                double fpsValue = double.TryParse(targetFps, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsedFps) && parsedFps > 0
                    ? parsedFps
                    : 60.0;

                foreach (var m in memes)
                {
                    double absSourceSec = StartTimeMs / 1000.0 + m.AtSourceSecRelative;
                    double bodyOutSec = granularTimeMapper != null
                        ? granularTimeMapper(absSourceSec)
                        : (absSourceSec - actualExtractStartMs / 1000.0) / SpeedFactor;

                    if (m.AtSourceSecRelative <= 0.0005) bodyOutSec = 0;

                    double cut = Math.Clamp(introDurationSec + bodyOutSec, introDurationSec, renderDurationSec);
                    m.CutOutputSec = Math.Clamp(Math.Round(cut * fpsValue) / fpsValue, 0, renderDurationSec);
                }
                memes.Sort((a, b) => a.CutOutputSec.CompareTo(b.CutOutputSec));

                const double MinPieceSec = 0.05;
                var memeCuts = new List<double>();
                {
                    var trailing = new List<ResolvedMeme>();
                    foreach (var m in memes)
                    {
                        if (m.CutOutputSec >= renderDurationSec - MinPieceSec)
                        {
                            m.CutOutputSec = renderDurationSec;
                            trailing.Add(m);
                            continue;
                        }

                        if (introDurationSec <= 0.001 && m.CutOutputSec <= MinPieceSec)
                        {
                            m.CutOutputSec = 0;
                            m.SlotIndex = 0;
                            continue;
                        }

                        if (memeCuts.Count > 0 && m.CutOutputSec - memeCuts[^1] < MinPieceSec)
                            m.CutOutputSec = memeCuts[^1];
                        else
                            memeCuts.Add(m.CutOutputSec);

                        m.SlotIndex = memeCuts.Count;
                    }
                    foreach (var m in trailing) m.SlotIndex = memeCuts.Count + 1;
                }
                int memePieceCount = memeCuts.Count + 1;

                double MemeTimeInsertedBefore(double renderedOutSec, bool landsAfter)
                {
                    double acc = 0;
                    foreach (var m in memes)
                    {
                        bool before = landsAfter
                            ? m.CutOutputSec <= renderedOutSec + 1e-9
                            : m.CutOutputSec < renderedOutSec - 1e-9;
                        if (before) acc += m.DurationSec;
                    }
                    return acc;
                }

                if (mixMusicAfterMeme)
                {
                    for (int i = 0; i < musicTracks.Count; i++)
                    {
                        double shift = MemeTimeInsertedBefore(
                            introDurationSec + musicTracks[i].TimelineStartDelay, landsAfter: false);
                        musicTracks[i] = musicTracks[i] with
                        {
                            Duration = musicTracks[i].Duration + memeTotalDuration,
                            TimelineStartDelay = musicTracks[i].TimelineStartDelay + shift
                        };
                    }
                }

                {
                    long estimatedBytes = DiskSpaceGuard.EstimateOutputBytes(
                        renderDurationSec + memeTotalDuration, videoBitrateKbps, targetMb);
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
                if (memeTotalDuration > 0.001) costSpans.Add((bodyEnd, bodyEnd + memeTotalDuration, 1.0));
                double totalCostW = costSpans.Sum(sp => sp.w * (sp.e - sp.s));
                double EncodeFraction(double outSec)
                {
                    if (totalCostW <= 1e-6) return Math.Clamp(outSec / Math.Max(1e-6, bodyEnd + memeTotalDuration), 0, 1);
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

                    int voBaseIndex = 1 + musicTracks.Count + (introDurationSec > 0.001 ? 1 : 0) + (textPngPath != null ? 1 : 0) + memes.Count;

                    var secondPassFilter = BuildLoudnormSecondPassFilter();
                    hasSecondPass = !string.IsNullOrEmpty(secondPassFilter);
                    if (sourceHasAudio && hasSecondPass)
                    {
                        coreFilters.Add($"{aPreparedPad}{secondPassFilter},aresample=48000[a_master_leveled]");
                        aPreparedPad = "[a_master_leveled]";
                    }

                    double gameLufsForVoice = hasSecondPass
                        ? AudioLoudnessProbe.TargetLufs
                        : (_loudnorm?.InputI
                           ?? SourceMeasuredLufs
                           ?? (AudioLoudnessProbe.TargetLufs - VolumeNormalizeDb));
                    gameLufsForVoice = Math.Clamp(gameLufsForVoice, -70.0, -5.0);
                    string voLoudnorm = AutoVoiceNormalization
                        ? $"loudnorm=I={gameLufsForVoice.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}:LRA=11:TP=-1.5,aresample=48000"
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

                            double voDelaySec = voStartOutSec;
                            if (mixMusicAfterMeme)
                                voDelaySec += introDurationSec
                                            + MemeTimeInsertedBefore(introDurationSec + voStartOutSec, landsAfter: true);

                            int delayMs = Math.Max(0, (int)Math.Round(voDelaySec * 1000.0));
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
                    var secondPassFilterNoVoice = BuildLoudnormSecondPassFilter();
                    hasSecondPass = !string.IsNullOrEmpty(secondPassFilterNoVoice);
                    if (sourceHasAudio && hasSecondPass)
                    {
                        coreFilters.Add($"{aPreparedPad}{secondPassFilterNoVoice},aresample=48000[a_master_leveled]");
                        aPreparedPad = "[a_master_leveled]";
                        CoreLogger.Info("Audio",
                            $"Game bus normalised to {AudioLoudnessProbe.TargetLufs:F1} LUFS (no voice-over on this export).");
                    }
                }

                string? finalVoLabelScope = effectiveTakes.Count > 0 ? "[vo_mixed_all]" : null;

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
                        voiceOverLabel: effectiveTakes.Count > 0 ? (effectiveTakes.Count > 1 ? "[vo_mixed_all]" : "[vo_delayed_0]") : null,
                        musicBedGainDb: musicBedGains);

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

                if (memes.Count > 0)
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

                    foreach (var m in memes)
                    {
                        string memeScale =
                            $"scale={canvas}:force_original_aspect_ratio=decrease," +
                            $"pad={canvas}:(ow-iw)/2:(oh-ih)/2:color=black," +
                            $"scale=w=floor(iw/2)*2:h=floor(ih/2)*2,format=yuv420p,setsar=1,fps={targetFps}:start_time=0:round=near";

                        string memeAudio = "aresample=48000:async=1";

                        if (m.LoudnessMeasured && Math.Abs(m.LoudnessGainDb) > 0.01)
                        {
                            double g = Math.Pow(10, m.LoudnessGainDb / 20.0);
                            memeAudio += $",volume={g.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}";
                        }
                        else if (!m.LoudnessMeasured && Math.Abs(VolumeNormalizeDb) > 0.01)
                        {
                            double memeGain = Math.Pow(10, VolumeNormalizeDb / 20.0);
                            memeAudio += $",volume={memeGain.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}";
                            CoreLogger.Info("Audio",
                                $"Meme audio shifted {VolumeNormalizeDb:+0.00;-0.00} dB (unmeasured) to stay in proportion with the mix.");
                        }

                        if (m.HasAudio)
                        {
                            memeAudio += ",alimiter=limit=-2.0dB:level_in=1:level_out=1";
                        }

                        bool memeTrails = m.SlotIndex == memePieceCount;
                        bool memeLeads = m.CutOutputSec <= introDurationSec + 1e-9;
                        bool fadeThisMeme = memeTrails || memeLeads;

                        if (EnableFades && fadeThisMeme && m.DurationSec >= 0.5)
                        {
                            double memeFadeDur = Math.Min(1.0, m.DurationSec / 2.0);
                            double memeFadeStart = Math.Max(0, m.DurationSec - memeFadeDur);
                            memeScale += $",fade=t=out:st={memeFadeStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:d={memeFadeDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}";

                            if (!mixMusicAfterMeme && m.HasAudio)
                            {
                                memeAudio += $",afade=t=out:st={memeFadeStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:d={memeFadeDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}";
                            }
                        }

                        coreFilters.Add($"[{m.InputIndex}:v]{memeScale}{m.VLabel}");
                        if (m.HasAudio)
                            coreFilters.Add($"[{m.InputIndex}:a]{memeAudio}{m.ALabel}");
                        else
                            coreFilters.Add($"anullsrc=r=48000:cl=stereo,atrim=duration={m.DurationSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS{m.ALabel}");
                    }

                    var cuts = memeCuts;
                    int pieceCount = memePieceCount;

                    var vPieces = new List<string>();
                    var aPieces = new List<string>();

                    if (pieceCount == 1)
                    {
                        vPieces.Add("[v_render_out]");
                        aPieces.Add(aOutputFinal);
                    }
                    else
                    {
                        var vSrc = new List<string>(pieceCount);
                        var aSrc = new List<string>(pieceCount);
                        for (int i = 0; i < pieceCount; i++)
                        {
                            vSrc.Add($"[v_cut{i}_src]");
                            aSrc.Add($"[a_cut{i}_src]");
                        }
                        coreFilters.Add($"[v_render_out]split={pieceCount}{string.Join("", vSrc)}");
                        coreFilters.Add($"{aOutputFinal}asplit={pieceCount}{string.Join("", aSrc)}");

                        for (int i = 0; i < pieceCount; i++)
                        {
                            string startArg = i == 0 ? "0" : CutSec(cuts[i - 1]);
                            string endArg = i == pieceCount - 1 ? "" : $":end={CutSec(cuts[i])}";
                            coreFilters.Add($"[v_cut{i}_src]trim=start={startArg}{endArg},setpts=PTS-STARTPTS[v_cut{i}]");
                            coreFilters.Add($"[a_cut{i}_src]atrim=start={startArg}{endArg},asetpts=PTS-STARTPTS[a_cut{i}]");
                            vPieces.Add($"[v_cut{i}]");
                            aPieces.Add($"[a_cut{i}]");
                        }

                        if (pieceCount > 8)
                        {
                            CoreLogger.Fail("Meme",
                                $"HIGH CUT COUNT: {cuts.Count} meme insertion point(s) means {pieceCount} parallel " +
                                "branches off the rendered stream. FFmpeg buffers frames on every branch concat has " +
                                "not reached yet, so peak RAM grows with this number.");
                        }
                    }

                    var concatOrder = new List<string>();
                    int concatSegments = 0;
                    for (int slot = 0; slot <= pieceCount; slot++)
                    {
                        foreach (var m in memes)
                        {
                            if (m.SlotIndex != slot) continue;
                            concatOrder.Add(m.VLabel);
                            concatOrder.Add(m.ALabel);
                            concatSegments++;
                        }
                        if (slot < pieceCount)
                        {
                            concatOrder.Add(vPieces[slot]);
                            concatOrder.Add(aPieces[slot]);
                            concatSegments++;
                        }
                    }

                    coreFilters.Add($"{string.Join("", concatOrder)}concat=n={concatSegments}:v=1:a=1[v_final][a_final_before_music]");

                    foreach (var m in memes)
                    {
                        string where =
                            m.SlotIndex == 0 ? "leading the gameplay"
                            : m.SlotIndex == pieceCount ? "at the very end"
                            : $"cutting in at {m.CutOutputSec:F3}s of the rendered video";
                        CoreLogger.Info("Meme",
                            $"'{Path.GetFileName(m.FilePath)}' ({m.DurationSec:F2}s) {where} " +
                            $"— placed at {m.AtSourceSecRelative:F3}s of the clip.");
                    }
                    CoreLogger.Info("Meme",
                        $"{memes.Count} meme(s) spliced into {pieceCount} piece(s) of rendered video " +
                        $"(concat=n={concatSegments}); the finished video grows by {memeTotalDuration:F3}s. " +
                        (introDurationSec > 0.001
                            ? "The frozen thumbnail frame stays FIRST, so the share thumbnail is still the frame you chose."
                            : "No thumbnail intro on this export."));

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
                        gDur + memeTotalDuration,
                        aOutputFinal,
                        hasSecondPass ? 0.0 : VolumeNormalizeDb,
                        null,
                        musicFollowGainDb: _loudnorm != null ? VolumeNormalizeDb : 0.0,
                        musicLeadFadeIn: MusicLeadFadeIn,
                        musicTailFadeOut: MusicTailFadeOut,
                        voiceOverLabel: effectiveTakes.Count > 0 ? (effectiveTakes.Count > 1 ? "[vo_mixed_all]" : "[vo_delayed_0]") : null,
                        musicBedGainDb: musicBedGains);

                    foreach (var part in built.chains)
                    {
                        coreFilters.Add(part);
                    }
                    aOutputFinal = built.finalLabel;
                    
                    double lastMemeDuration =
                        memes.Count > 0 && memes[^1].SlotIndex == memePieceCount ? memes[^1].DurationSec : 0;
                    if (EnableFades && lastMemeDuration >= 0.5)
                    {
                        double memeFadeDur = Math.Min(1.0, lastMemeDuration / 2.0);
                        double totalOutDur = renderDurationSec + memeTotalDuration;
                        double memeFadeStart = Math.Max(0, totalOutDur - memeFadeDur);
                        coreFilters.Add($"{aOutputFinal}afade=t=out:st={memeFadeStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:d={memeFadeDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}[a_final_music_faded]");
                        aOutputFinal = "[a_final_music_faded]";
                    }
                }


                double quietBoostTrimDb =
                    -AudioLoudnessProbe.QuietBoostReductionFactor * Math.Max(0.0, VolumeNormalizeDb);
                if (quietBoostTrimDb < -0.01)
                {
                    coreFilters.Add($"{aOutputFinal}volume={quietBoostTrimDb.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}dB[a_quiet_boost_trimmed]");
                    aOutputFinal = "[a_quiet_boost_trimmed]";
                    CoreLogger.Info("Audio",
                        $"QUIET-BOOST TRIM: source measured {AudioLoudnessProbe.TargetLufs - VolumeNormalizeDb:F2} LUFS " +
                        $"and asked for {VolumeNormalizeDb:+0.00;-0.00} dB to reach {AudioLoudnessProbe.TargetLufs:F1} LUFS. " +
                        $"{quietBoostTrimDb:F2} dB given back on the finished mix " +
                        $"({(1.0 - AudioLoudnessProbe.QuietBoostReductionFactor) * 100:F0}% of the lift kept), " +
                        $"landing near {AudioLoudnessProbe.TargetLufs + quietBoostTrimDb:F2} LUFS. " +
                        "Mix balance unchanged — music, voice-over and memes move with it.");
                }

                if (AutoSpikeFlattening)
                {
                    CoreLogger.Info("Audio",
                        "PEAK LIMITER ON: loudnorm + alimiter ceiling -1.0 dB. The summed mix " +
                        "is dynamically normalized before final limiting to prevent the combined bus " +
                        "from exceeding the target loudness.");
                    coreFilters.Add($"{aOutputFinal}loudnorm=I={AudioLoudnessProbe.TargetLufs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}:TP=-1.0:LRA=11,aresample=48000[a_dyn_master]");
                    coreFilters.Add($"[a_dyn_master]alimiter=limit=-1.0dB:level_in=1:level_out=1[a_flattened]");
                    aOutputFinal = "[a_flattened]";
                }
                else
                {
                    CoreLogger.Info("Audio", "PEAK LIMITER OFF: no ceiling applied (Auto Spike Flattening is disabled).");
                }

                string filterScript = string.Join(";", coreFilters.Where(p => !string.IsNullOrEmpty(p)));
                CoreLogger.Info("FFmpeg", $"Filter Script Content:\n{filterScript}");
                string filterScriptPath = Path.Combine(tempJobDir, "filter_complex.txt");
                await File.WriteAllTextAsync(filterScriptPath, filterScript, cancellationToken);

                string corePath = Path.Combine(tempJobDir, "core.mp4");

                string twoPassMasterPath = Path.Combine(tempJobDir, "twopass_master.mp4");
                string twoPassLogPrefix = Path.Combine(tempJobDir, "twopass_stats");

                bool twoPassDisabled = false;

                bool twoPassProducedResult = false;

                bool twoPassFastRoute = DiskSpaceGuard.HasRoomFor(
                    tempJobDir, DiskSpaceGuard.EstimateTwoPassMasterBytes(renderDurationSec + memeTotalDuration));

                bool success = false;
                string lastError = "Render failed.";

                string? lastSuccessfulEncoder = null;
                string? lastAttemptedEncoder = null;
                async Task<bool> RunFfmpegOnce(bool useCuda, int? requestedBitrate, int attemptNum)
                {
                    string currentEncoder = lastSuccessfulEncoder ?? lastAttemptedEncoder ?? encoderMgr.GetInitialEncoder(useCuda);

                    int slowStage = 1;

                    while (true)
                    {
                        lastAttemptedEncoder = currentEncoder;

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

                        var decodeFlags = EncoderManager.GetDecodeFlags(currentEncoder);

                        ffmpegArgs.AddRange(decodeFlags);
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
                            ffmpegArgs.AddRange(decodeFlags);
                            ffmpegArgs.AddRange(["-ss", introAbsSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "-t", Math.Max(0.2, introDurationSec + 0.1).ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "-i", InputPath]);
                        }

                        if (textPngPath != null)
                            ffmpegArgs.AddRange(["-loop", "1", "-i", textPngPath]);

                        foreach (var m in memes)
                        {
                            if (m.IsImage)
                            {
                                ffmpegArgs.AddRange(["-loop", "1", "-framerate", targetFps, "-t", m.DurationSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "-i", m.FilePath]);
                            }
                            else
                            {
                                ffmpegArgs.AddRange(decodeFlags);
                                ffmpegArgs.AddRange(["-i", m.FilePath]);
                            }
                        }

                        var voTakes = GetEffectiveVoiceOverTakes();
                        foreach (var voTake in voTakes)
                            ffmpegArgs.AddRange(["-i", voTake.Path]);

                        ffmpegArgs.AddRange(["-filter_complex_script", filterScriptPath]);
                        double totalOutputDurationSec = renderDurationSec + memeTotalDuration;

                        bool graphIsMaster = twoPass && twoPassFastRoute;
                        bool graphIsSlowPass1 = twoPass && !twoPassFastRoute && slowStage == 1;
                        bool graphIsSlowPass2 = twoPass && !twoPassFastRoute && slowStage == 2;

                        ffmpegArgs.AddRange(["-map", vOutputFinal, "-map", aOutputFinal]);

                        if (graphIsMaster)
                        {
                            ffmpegArgs.AddRange(TwoPassEncoding.MasterCodecArgs());
                            ffmpegArgs.AddRange(["-c:a", "aac", "-b:a", $"{audioKbps}k",
                                "-t", totalOutputDurationSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                                twoPassMasterPath]);
                        }
                        else if (graphIsSlowPass1)
                        {
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
                        string routeLabel = graphIsMaster
                            ? "two-pass FAST (rendering master, 2 passes follow)"
                            : graphIsSlowPass1
                                ? "two-pass SLOW (pass 1 over the filter graph — not enough temp disk for a master)"
                                : "single-pass";
                        CoreLogger.Info("FFmpeg", $"Starting encode: decode={EncoderManager.DescribeDecoder(currentEncoder)}, encode={EncoderManager.DescribeEncoder(currentEncoder)}, mode={rcLabel}, route={routeLabel}, attempt={attemptNum}.");
                        CoreLogger.Info("FFmpeg", $"Executing Final Pipeline Command:\n{_ffmpegPath} {cmdLine}");

                        try
                        {
                            string scriptToken = filterScriptPath.Length == 0
                                                 || filterScriptPath.Contains(' ')
                                                 || filterScriptPath.Contains('"')
                                ? "\"" + filterScriptPath.Replace("\"", "\\\"") + "\""
                                : filterScriptPath;

                            string needle = $"-filter_complex_script {scriptToken}";
                            if (cmdLine.Contains(needle))
                            {
                                string inlineCmd = cmdLine.Replace(needle, $"-filter_complex \"{filterScript}\"");
                                CoreLogger.Info("FFmpeg",
                                    "FINAL COMMAND (filter graph inlined — copy/paste runnable, this is exactly what happened):\n" +
                                    $"\"{_ffmpegPath}\" {inlineCmd}");
                            }
                            else
                            {
                                CoreLogger.Info("FFmpeg",
                                    "FINAL COMMAND: this attempt used no filter script, so the command logged above is already complete.");
                            }
                        }
                        catch (Exception ex)
                        {
                            CoreLogger.Debug("FFmpeg", $"Could not build the inlined command for the log: {ex.Message}");
                        }

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

                        try { ChildProcessTracker.AddProcess(proc); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

                        using var reg = cancellationToken.Register(() =>
                        {
                            try { proc.Kill(entireProcessTree: true); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
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

                        string producedPath = graphIsMaster ? twoPassMasterPath : corePath;
                        bool producedOk = graphIsSlowPass1
                            ? exitCode == 0
                            : exitCode == 0 && File.Exists(producedPath) && new FileInfo(producedPath).Length > 0;

                        if (producedOk)
                        {
                            if (graphIsSlowPass1)
                            {
                                slowStage = 2;
                                CoreLogger.Info("FFmpeg", "Two-pass SLOW: analysis complete, starting the real pass.");
                                continue;
                            }

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
                                    twoPassDisabled = true;
                                    CoreLogger.Fail("FFmpeg",
                                        "Two-pass tail failed — falling back to a single-pass encode for this export.");
                                    if (File.Exists(corePath)) { try { File.Delete(corePath); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); } }
                                    continue;
                                }
                            }

                            lastSuccessfulEncoder = currentEncoder;
                            twoPassProducedResult = twoPass;

                            bool cpuFallback = currentEncoder == "libx264" && useCuda && !encoderMgr.ForcedCpu;
                            string passLabel = twoPass ? (twoPassFastRoute ? " route=two-pass(fast)" : " route=two-pass(slow)") : "";
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
                                try { proc.Dispose(); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
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
                                if (File.Exists(corePath)) { try { File.Delete(corePath); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); } }

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
                                    try { File.Delete(corePath); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
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
                    if (File.Exists(bestPath)) { try { File.Delete(bestPath); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); } }
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

                try { File.Delete(corePath); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

                if (ThumbnailPosMs > 0)
                {
                    EmitProgress(2, "Generating Thumbnail", 98);
                    string thumbnailOutput = Path.Combine(tempJobDir, Path.GetFileNameWithoutExtension(finalOutput) + "_thumbnail.jpg");
                    double extractTargetSec = granularTimeMapper != null
                        ? Math.Max(0.0, granularTimeMapper(ThumbnailPosMs / 1000.0))
                        : Math.Max(0.0, (ThumbnailPosMs - actualExtractStartMs) / 1000.0 / Math.Max(0.001, SpeedFactor));
                    if (introDurationSec > 0.0)
                    {
                        extractTargetSec += introDurationSec;
                    }

                    extractTargetSec += MemeTimeInsertedBefore(extractTargetSec, landsAfter: true);
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
                            try { thumbErr = await thumbErrTask; } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

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
                                    try { File.Delete(thumbnailOutput); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
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
                CoreLogger.Info("Process", $"Pipeline completed in {pipelineStopwatch.Elapsed.TotalSeconds:F1}s. Output: {finalOutput}");
                EmitProgress(2, "Complete", 100);
                EmitFinished(true, finalOutput);
            }
            finally
            {
                try { if (Directory.Exists(tempJobDir)) Directory.Delete(tempJobDir, true); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
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
        var pass1 = new List<string> { "-y", "-hide_banner", "-progress", "pipe:1", "-i", masterPath };
        pass1.AddRange(TwoPassEncoding.PassArgs(videoBitrateKbps, 1, passLogPrefix));
        pass1.AddRange(["-an", "-sn", "-dn", "-f", "null", "NUL"]);

        if (!await RunPassAsync(pass1, "Analyzing Video (2 of 3)", totalOutputDurationSec,
                                pass1Floor, pass1Ceiling, cancellationToken))
        {
            return false;
        }

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

        _currentProcess = proc;

        try
        {
            try { ChildProcessTracker.AddProcess(proc); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

            using var reg = cancellationToken.Register(() =>
            {
                try { proc.Kill(entireProcessTree: true); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
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

            try { await progressTask; } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
            try { await stderrTask; } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

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

    /// <summary>
    /// CUT_01 — the export span minus every cut, as ABSOLUTE SOURCE SECONDS ranges, in order.
    ///
    /// Returns a single range covering the whole span when there are no cuts, so the caller keeps
    /// the cheap `-ss`/`-t` path and nothing changes for the overwhelmingly common case. Ranges
    /// shorter than a frame are dropped: they contribute nothing measurable and each one would cost
    /// an extra branch in the measurement graph.
    /// </summary>
    private List<(double, double)> SurvivingAudioRangesSec(double spanStartMs, double spanEndMs)
    {
        double spanStart = spanStartMs / 1000.0;
        double spanEnd = spanEndMs / 1000.0;

        var ranges = new List<(double, double)>();
        if (spanEnd <= spanStart) return ranges;

        var cuts = CutRange.ToClipRelative(Cuts, spanStartMs);
        var normalized = OutputTimeline.NormalizeCuts(cuts, spanEnd - spanStart);
        if (normalized.Count == 0)
        {
            ranges.Add((spanStart, spanEnd));
            return ranges;
        }

        double cursor = spanStart;
        foreach (var c in normalized)
        {
            double holeStart = spanStart + c.StartSec;
            double holeEnd = spanStart + c.EndSec;
            if (holeStart > cursor + 0.02) ranges.Add((cursor, holeStart));
            cursor = Math.Max(cursor, holeEnd);
        }
        if (spanEnd > cursor + 0.02) ranges.Add((cursor, spanEnd));

        return ranges;
    }

    private async Task PerformLoudnormPassAsync(double measureStartMs, double measureEndMs, CancellationToken cancellationToken)
    {
        try
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            double targetLufs = AudioLoudnessProbe.TargetLufs;

            string loudnormArg =
                $"loudnorm=I={AudioLoudnessProbe.TargetLufs.ToString("F1", ci)}" +
                $":TP={AudioLoudnessProbe.PeakCeilingDbtp.ToString("F1", ci)}:LRA=11:print_format=json";

            // CUT_01 / BEDSEG_01 — MEASURE ONLY WHAT THE VIEWER WILL HEAR.
            //
            // This used to be a single `-ss`/`-t` window, which was correct while the export was
            // always one unbroken stretch. With cuts it is not: the window still spans the deleted
            // footage, so the game bus would be normalised against audio that never reaches the
            // finished file — a long silent stretch the user deleted would drag the measurement
            // down and push everything else up. This is the same fault we fixed for the music bed,
            // and it is the objection the original Cut_Feature_Design.txt never raised.
            //
            // The surviving ranges are trimmed and concatenated in the filter graph first, so
            // loudnorm sees exactly the audio the export will produce.
            var survivingRanges = SurvivingAudioRangesSec(measureStartMs, measureEndMs);

            var args = new List<string> { "-y", "-hide_banner" };

            if (survivingRanges.Count <= 1)
            {
                var only = survivingRanges.Count == 1
                    ? survivingRanges[0]
                    : (measureStartMs / 1000.0, measureEndMs / 1000.0);

                args.Add("-ss"); args.Add(only.Item1.ToString("F3", ci));
                args.Add("-t"); args.Add((only.Item2 - only.Item1).ToString("F3", ci));
                args.Add("-i"); args.Add(InputPath);
                args.Add("-af"); args.Add(loudnormArg);
            }
            else
            {
                var trims = new List<string>();
                var labels = new System.Text.StringBuilder();
                for (int i = 0; i < survivingRanges.Count; i++)
                {
                    var (rs, re) = survivingRanges[i];
                    trims.Add($"[0:a]atrim=start={rs.ToString("F3", ci)}:end={re.ToString("F3", ci)}," +
                              $"asetpts=PTS-STARTPTS[lnseg{i}]");
                    labels.Append($"[lnseg{i}]");
                }
                string graph = string.Join(";", trims) +
                               $";{labels}concat=n={survivingRanges.Count}:v=0:a=1," +
                               $"{loudnormArg}[lnout]";

                args.Add("-i"); args.Add(InputPath);
                args.Add("-filter_complex"); args.Add(graph);
                args.Add("-map"); args.Add("[lnout]");

                CoreLogger.Info("Loudnorm",
                    $"CUT-AWARE MEASUREMENT: {survivingRanges.Count} surviving range(s) totalling " +
                    $"{survivingRanges.Sum(r => r.Item2 - r.Item1):F2}s, out of a " +
                    $"{(measureEndMs - measureStartMs) / 1000.0:F2}s span. Deleted footage is excluded.");
            }

            args.Add("-vn"); args.Add("-sn"); args.Add("-dn");
            args.Add("-f"); args.Add("null"); args.Add("-");

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

            try { ChildProcessTracker.AddProcess(process); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

            using var loudnormKill = cancellationToken.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
            });

            var lastLines = new System.Collections.Generic.Queue<string>(100);
            // CUT_01 — the progress bar counts the audio ffmpeg actually decodes, which after cuts
            // is shorter than the span. Using the span here would stall the bar short of 100%.
            double totalDurationSec = survivingRanges.Count > 0
                ? survivingRanges.Sum(r => r.Item2 - r.Item1)
                : (measureEndMs - measureStartMs) / 1000.0;
            
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

                        CoreLogger.Info("Loudnorm",
                            $"NORMALISATION PLAN: measured {inputI:F2} LUFS -> target {targetLufs:F1} LUFS " +
                            $"= {VolumeNormalizeDb:+0.00;-0.00} dB, true-peak ceiling {AudioLoudnessProbe.PeakCeilingDbtp:F1} dBTP. " +
                            $"Applied to the game bus via linear loudnorm; the SAME {VolumeNormalizeDb:+0.00;-0.00} dB " +
                            $"is applied to music, voice-over and meme so every element keeps its relative balance.");
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
    /// <summary>
    /// AUDIO_03 — measure every music file once and work out how far each is from the bed level.
    ///
    /// Runs at most once per distinct path per export, and only when music is actually present. A
    /// failed or impossible measurement yields NO entry, which downstream means "0 dB correction" —
    /// identical to the old behaviour. This must never be able to fail an export.
    /// </summary>
    private async Task<Dictionary<string, double>> MeasureMusicBedGainsAsync(
        List<MusicTrack> tracks, CancellationToken cancellationToken)
    {
        var gains = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            // BEDSEG_01 note — the dictionary stays keyed on PATH because that is how
            // AudioFilterChain looks a gain up (musicBedGainDb.TryGetValue(track.Path, ...)).
            // So if the SAME song is queued twice at different offsets, both uses take the first
            // occurrence's segment gain. Changing that means re-keying the lookup on both sides;
            // it is not worth it until someone actually queues one song twice.
            if (string.IsNullOrWhiteSpace(track.Path) || gains.ContainsKey(track.Path)) continue;
            try
            {
                // BEDSEG_01 — measure the SEGMENT that will play, not the whole song.
                // track.Offset is the seek point into the file and track.Duration is how much of
                // it is used, and both were being thrown away here. The game bus is already
                // measured over exactly its exported range (PerformLoudnormPassAsync), so passing
                // these makes the two sides of the mix symmetrical; without them a 45-second cut
                // of a 4-minute song was corrected using an average it never plays.
                // The probe falls back to the whole file when the window is under
                // AudioLoudnessProbe.MinSegmentSec, so a very short cut still gets an answer.
                var reading = await AudioLoudnessProbe.MeasureAsync(
                                          _ffmpegPath, track.Path, cancellationToken,
                                          segmentStartSec: Math.Max(0.0, track.Offset),
                                          segmentDurationSec: Math.Max(0.0, track.Duration))
                                                      .ConfigureAwait(false);
                if (reading == null)
                {
                    CoreLogger.Info("Audio",
                        $"Music level could not be measured for '{Path.GetFileName(track.Path)}' — leaving it untouched.");
                    continue;
                }

                double raw = AudioLoudnessProbe.MusicBedLufs - reading.IntegratedLufs;
                double clamped = Math.Clamp(raw, AudioLoudnessProbe.MinMusicGainDb, AudioLoudnessProbe.MaxMusicGainDb);
                gains[track.Path] = clamped;

                bool measuredSegment = track.Duration >= AudioLoudnessProbe.MinSegmentSec && track.Offset >= 0;
                string scope = measuredSegment
                    ? $"segment {Math.Max(0.0, track.Offset):F1}s +{track.Duration:F1}s"
                    : "whole file (segment too short to measure)";

                CoreLogger.Info("Audio",
                    $"MUSIC BED PLAN: '{Path.GetFileName(track.Path)}' [{scope}] measured {reading.IntegratedLufs:F2} LUFS -> " +
                    $"bed target {AudioLoudnessProbe.MusicBedLufs:F1} LUFS = {clamped:+0.00;-0.00} dB" +
                    (Math.Abs(raw - clamped) > 0.01 ? $" (clamped from {raw:+0.00;-0.00} dB)" : "") +
                    ". The music slider is applied on top of this.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                CoreLogger.Info("Audio",
                    $"Music level measurement skipped for '{Path.GetFileName(track.Path)}': {ex.Message}");
            }
        }
        return gains;
    }

    private string? BuildLoudnormSecondPassFilter()
    {
        if (_loudnorm is null) return null;

        var ci = CultureInfo.InvariantCulture;

        return $"loudnorm=I={AudioLoudnessProbe.TargetLufs.ToString("F1", ci)}" +
               $":TP={AudioLoudnessProbe.PeakCeilingDbtp.ToString("F1", ci)}:LRA=11:linear=true" +
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
            catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

            try { proc.Dispose(); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
            _currentProcess = null;
        }
    }
}

public record VoiceOverTake(string Path, double StartSec);
