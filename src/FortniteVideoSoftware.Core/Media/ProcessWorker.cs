
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

        // LOG_03: Cancel() is also called on normal app shutdown, long AFTER a successful export.
        // It used to log "Cancellation requested by user. Terminating FFmpeg process tree."
        // unconditionally, so a perfectly clean run ended with a line claiming the user cancelled
        // and that FFmpeg was killed — neither of which happened. In a log that is meant to be the
        // source of truth, a false statement is worse than no statement. Say what is actually true.
        bool encoding = _currentProcess is { HasExited: false };
        CoreLogger.Info("Process", encoding
            ? "Cancellation requested by user. Terminating FFmpeg process tree."
            : "Export worker released on shutdown (no encode was running).");

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

                // ─────────────────────────────────────────────────────────────────────────────
                // MEME_05 — RESOLVE EVERY MEME IN THIS EXPORT, NOT JUST ONE.
                //
                // `MemePlacements` is authoritative when it is non-empty. When it is empty the
                // legacy `MemeFile` + `MemeAtStart` pair is projected into a single placement so
                // that a payload written before multi-meme support produces the SAME graph it
                // always did:
                //   * MemeAtStart=false -> placed at the end of the trimmed clip  -> appended
                //   * MemeAtStart=true  -> placed at 0                            -> leads the
                //     gameplay, and still lands AFTER the frozen thumbnail head (see the concat).
                // The legacy entry deliberately keeps the id "meme", so the emitted labels remain
                // [meme_v] / [meme_a] exactly as before.
                //
                // The duration is PROBED here rather than trusted from the placement: the graph's
                // trims, the bitrate budget and the `-t` ceiling all depend on it, and the file on
                // disk is the only honest answer.
                // ─────────────────────────────────────────────────────────────────────────────
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

                        // ─────────────────────────────────────────────────────────────────────
                        // MEME_01 — MEASURE THIS MEME'S LOUDNESS, IF IT HAS ANY SOUND AT ALL.
                        //
                        // Memes are third-party clips ripped from anywhere, so their loudness is
                        // wild — the measured spread across the shipped set is 15.9 dB, with one
                        // clipping at +0.75 dBTP. The body of the video is normalised to
                        // TargetLufs, so each meme is aimed at the SAME figure: it then sits at the
                        // same perceived loudness as everything around it, whatever its source was.
                        //
                        // ⚠️ THIS IS PER MEME AND MUST STAY PER MEME. N memes are N independent
                        // measurements; a single shared gain would level one of them and leave the
                        // rest wherever they happened to be.
                        //
                        // Image memes and silent clips are skipped entirely — there is nothing to
                        // measure, and the graph already substitutes `anullsrc` for them.
                        // A failed measurement leaves the gain at 0 dB and falls back to the older
                        // proportional shift, so this can never block an export.
                        // ─────────────────────────────────────────────────────────────────────
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
                        // Nothing survived: the legacy field must not keep claiming a meme exists.
                        MemeFile = null;
                    }
                    else
                    {
                        // ─────────────────────────────────────────────────────────────────────
                        // MEME_05 — THE CLOSING FADE PAD IS DROPPED ONLY WHEN A MEME ACTUALLY
                        // ENDS THE VIDEO.
                        //
                        // The pad buys a second of footage past MARK END to fade the gameplay out
                        // over. That is pointless when a meme plays last — hence the original
                        // unconditional `padEndHumanSec = 0`. But "a meme exists" and "a meme is
                        // last" stopped being the same statement the moment memes could land
                        // mid-video: with every meme inside the clip the GAMEPLAY is still last,
                        // and dropping the pad robs the export of its closing fade for no reason.
                        //
                        // ⚠️ THIS ALSO CORRECTS THE LEGACY MemeAtStart CASE, which zeroed the pad
                        // even though the meme played FIRST and the gameplay still ended the
                        // video. That was always wrong; it simply never mattered while "meme" and
                        // "meme at the end" were nearly synonymous. A legacy start-meme export
                        // therefore gains its closing fade back — a deliberate behaviour change,
                        // recorded in MEME_TIMELINE_PLAN.txt.
                        //
                        // The test is in SOURCE time on purpose: it has to be answered here, before
                        // the extract range is fixed, and the output-time cut positions do not
                        // exist yet.
                        // ─────────────────────────────────────────────────────────────────────
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

                // AUDIO_03: measure the music BEFORE the graph is built, so every track can be put
                // at a proper bed level rather than arriving at whatever loudness its label chose.
                var musicBedGains = musicTracks.Count > 0
                    ? await MeasureMusicBedGainsAsync(musicTracks, cancellationToken)
                    : new Dictionary<string, double>();

                bool mixMusicAfterMeme = KeepMusicDuringMeme && memeTotalDuration > 0 && musicTracks.Count > 0;
                // ⚠️ The music tracks are ADJUSTED further down, not here — their start delays have
                // to be shifted by the meme time in front of them, and that is not known until the
                // cut times are computed. See the `mixMusicAfterMeme` block after MemeTimeInsertedBefore.

                int? introInputIndex = introDurationSec > 0.001 ? 1 + musicTracks.Count : null;
                string? textInputLabel = textPngPath != null
                    ? $"[{1 + musicTracks.Count + (introInputIndex.HasValue ? 1 : 0)}:v]"
                    : null;
                
                // MEME_05 — EACH MEME IS ITS OWN FFMPEG INPUT, in list order. The order here and the
                // order the `-i` arguments are emitted in below MUST stay identical, and the
                // voice-over base index below must skip all of them.
                int memeInputBase = 1 + musicTracks.Count + (introInputIndex.HasValue ? 1 : 0) + (textPngPath != null ? 1 : 0);
                for (int i = 0; i < memes.Count; i++) memes[i].InputIndex = memeInputBase + i;

                double renderDurationSec = gDur + introDurationSec;

                // ─────────────────────────────────────────────────────────────────────────────
                // MEME_05 — WHERE EACH MEME CUTS INTO THE RENDERED STREAM.
                //
                // ⚠️⚠️ THE SINGLE EASIEST THING TO GET WRONG IN THIS WHOLE FEATURE. ⚠️⚠️
                // `AtSourceSecRelative` is CLIP-RELATIVE SOURCE time — a moment of gameplay,
                // measured from the trim-in point. The stream being cut, `[v_render_out]`, has
                // ALREADY had the speed changes, the freezes and the thumbnail intro applied, and
                // has NOT yet had any meme spliced into it. So the cut time is:
                //
                //     introDurationSec + <source -> output mapping of the moment>
                //
                // and the mapping MUST be the pre-meme one. `granularTimeMapper` is exactly that:
                // OutputTimeline.SourceToOutput built WITHOUT insertions (see
                // GranularSpeedBuilder.Build). Feeding a timeline that already knows about the memes
                // would count every earlier meme's duration into this number, and each meme after
                // the first would drift later by the sum of the ones before it. This is the same
                // mapper the voice-over positions use, so meme cuts and voice-over starts cannot
                // disagree.
                //
                // ⚠️ THE FROZEN THUMBNAIL HEAD STAYS FIRST (MEME_02). The lower clamp is
                // introDurationSec, not 0, so a meme placed at the very beginning lands AFTER the
                // chosen still and WhatsApp never grabs a meme's black opening frame.
                //
                // ⚠️ EVERY CUT IS SNAPPED TO THE FRAME GRID. `trim` can only cut on a frame
                // boundary but `atrim` is sample-accurate, so an arbitrary cut time makes the video
                // piece and the audio piece different lengths — up to half a frame each, and N seams
                // accumulate. Snapping first makes both sides land on the same instant: at 60fps and
                // 48kHz a frame is exactly 800 samples, so the seam is exact rather than merely
                // close. This codebase has already been bitten by A/V drift at concat seams (see the
                // VFR negative-timestamp and implicit-split entries in the changelog).
                //
                // ⚠️ THIS RUNS HERE, BEFORE THE VOICE-OVER BLOCK, NOT DOWN AT THE CONCAT. The
                // voice-over needs `MemeTimeInsertedBefore` to place its takes on the KeepMusic
                // path — see the comment there. Moving it back down reintroduces that defect.
                // ─────────────────────────────────────────────────────────────────────────────
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

                    // A meme asked for AT (or before) the trim-in point LEADS the gameplay. It must
                    // not be dropped in after the fade-in, because that lead-in is footage from
                    // BEFORE the trim-in point and the mapper reports it as positive time.
                    if (m.AtSourceSecRelative <= 0.0005) bodyOutSec = 0;

                    double cut = Math.Clamp(introDurationSec + bodyOutSec, introDurationSec, renderDurationSec);
                    m.CutOutputSec = Math.Clamp(Math.Round(cut * fpsValue) / fpsValue, 0, renderDurationSec);
                }
                memes.Sort((a, b) => a.CutOutputSec.CompareTo(b.CutOutputSec));

                // ─────────────────────────────────────────────────────────────────────────────
                // MEME_05 — CUT THE RENDERED STREAM INTO PIECES.
                //
                // A cut at 0 or at the very end is not a cut at all: those memes simply lead or
                // trail. Everything else becomes an interior cut point, and N interior cuts produce
                // N+1 pieces.
                //
                // ⚠️ MinPieceSec EXISTS BECAUSE AN EMPTY TRIM KILLS concat. Two cut points a
                // fraction of a frame apart would leave a piece with no frames in it, and the graph
                // fails to configure. Cuts that close are merged onto the earlier one, so the memes
                // simply stack at the same seam — which is the correct outcome anyway: they are at
                // the same moment.
                // ─────────────────────────────────────────────────────────────────────────────
                const double MinPieceSec = 0.05;
                var memeCuts = new List<double>();
                {
                    var trailing = new List<ResolvedMeme>();
                    foreach (var m in memes)
                    {
                        // Trails everything — no cut needed, it is simply concatenated last.
                        if (m.CutOutputSec >= renderDurationSec - MinPieceSec)
                        {
                            m.CutOutputSec = renderDurationSec;
                            trailing.Add(m);
                            continue;
                        }

                        // Leads everything. ⚠️ ONLY LEGAL WHEN THERE IS NO THUMBNAIL HEAD TO
                        // PROTECT. With an intro present the head must stay first (MEME_02), so a
                        // meme at the beginning becomes a real cut at the intro boundary instead —
                        // which is exactly the shipped 3-way split.
                        if (introDurationSec <= 0.001 && m.CutOutputSec <= MinPieceSec)
                        {
                            m.CutOutputSec = 0;
                            m.SlotIndex = 0;
                            continue;
                        }

                        // An interior cut. The thumbnail head is deliberately short (0.1s) and is
                        // exempt from the merge rule — it is the FIRST cut, so there is nothing
                        // before it to merge onto, and it must survive as its own piece.
                        if (memeCuts.Count > 0 && m.CutOutputSec - memeCuts[^1] < MinPieceSec)
                            m.CutOutputSec = memeCuts[^1];
                        else
                            memeCuts.Add(m.CutOutputSec);

                        m.SlotIndex = memeCuts.Count;
                    }
                    foreach (var m in trailing) m.SlotIndex = memeCuts.Count + 1;
                }
                int memePieceCount = memeCuts.Count + 1;

                // ─────────────────────────────────────────────────────────────────────────────
                // MEME_05 — HOW MUCH MEME TIME SITS IN FRONT OF A GIVEN MOMENT OF THE RENDERED
                // STREAM. This is the ONLY honest way to convert a position in the pre-meme clock
                // into a position in the FINISHED video, and anything mixed in AFTER the concat
                // needs it.
                //
                // The boundary is inclusive on purpose: a meme sitting exactly on the instant in
                // question plays BEFORE that instant (the rendered stream is cut there and resumes
                // after the meme), so its duration counts.
                // ─────────────────────────────────────────────────────────────────────────────
                // <paramref name="landsAfter"/> decides what happens to something sitting EXACTLY on
                // a meme's instant. A voice-over take is locked to a moment of gameplay, and that
                // gameplay resumes AFTER the meme, so the take must move with it (true). Music on
                // the KeepMusicDuringMeme path is explicitly meant to play THROUGH memes, so a cue
                // on that instant starts WITH the meme, not after it (false). Getting this backwards
                // for music would make a track whose start delay is 0 begin after a leading meme —
                // silence over the very meme the flag exists to keep music under.
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

                // ─────────────────────────────────────────────────────────────────────────────
                // MEME_05 — MUSIC ON THE KEEP-MUSIC PATH LIVES IN THE FINISHED CLOCK, TOO.
                //
                // When KeepMusicDuringMeme is on, AudioFilterChain runs AFTER the meme concat, so
                // the stream it lays music over already contains every meme. Extending each track's
                // Duration by the meme total — all this block used to do — keeps the music playing
                // to the end, but a track that STARTS partway in was still delayed by a body-clock
                // figure, so it came in early by the length of every meme before it. That is the
                // same defect fixed for the voice-over, and the same fix applies.
                //
                // ⚠️ `Duration` IS LEFT ALONE APART FROM THE ORIGINAL `+ memeTotalDuration`.
                // AudioFilterChain sequences consecutive tracks with `accumProjectSec += Duration`
                // AND adds TimelineStartDelay on top, so Duration is not purely a length — trimming
                // it by the shift would drag every LATER track's start with it. Overshooting the
                // end is harmless (the `-t` ceiling and the tail fade both bound it); undershooting
                // would leave silence. Leave it long.
                //
                // ⚠️ THE INTRO OFFSET IS DELIBERATELY NOT CORRECTED HERE. On this path the stream's
                // t=0 is the thumbnail intro, so the music is also introDurationSec early — one
                // frame at the default 0.1s. That error predates memes, and correcting it moves the
                // music tail fade the Music Wizard's markers were tuned against. It is a Music
                // Wizard decision, not a meme decision; recorded in MEME_TIMELINE_PLAN.txt.
                // ─────────────────────────────────────────────────────────────────────────────
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

                    // ─────────────────────────────────────────────────────────────────────────
                    // AUDIO_02 — AIM THE VOICE AT WHERE THE GAME ENDS UP, NOT WHERE IT STARTED.
                    //
                    // This used to target `_loudnorm.InputI` — the game's loudness BEFORE
                    // normalisation. But the game bus is levelled to TargetLufs a few lines above,
                    // so on a quiet clip the voice was aimed at the old, quieter number and ended
                    // up sitting that many dB underneath the gameplay it was supposed to match.
                    // The quieter the source, the further the voice fell behind — the opposite of
                    // "everything keeps its proportions".
                    //
                    // When the game bus is normalised, aim the voice at the SAME target so the two
                    // land together. Only when no measurement exists (nothing to normalise
                    // against) does it fall back to the measured/raw loudness as before.
                    // ─────────────────────────────────────────────────────────────────────────
                    double gameLufsForVoice = hasSecondPass
                        ? AudioLoudnessProbe.TargetLufs
                        : (_loudnorm?.InputI
                           ?? SourceMeasuredLufs
                           ?? (AudioLoudnessProbe.TargetLufs - VolumeNormalizeDb));
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

                            // ─────────────────────────────────────────────────────────────────
                            // MEME_05 / D5 — WHICH CLOCK THIS DELAY IS MEASURED IN DEPENDS ON
                            // WHERE THE VOICE-OVER GETS MIXED, AND THE TWO ANSWERS ARE DIFFERENT.
                            //
                            // NORMAL PATH (mixMusicAfterMeme == false): the voice is mixed into the
                            // body audio BEFORE the thumbnail intro is prepended and BEFORE the
                            // memes are spliced in. `voStartOutSec` — the pre-meme body clock — is
                            // therefore exactly right, and D5 falls out for free: the atrim that
                            // cuts the rendered audio at a meme seam cuts the voice with it, so a
                            // take straddling a meme splits and RESUMES, and a take after a meme
                            // slides later intact. Nothing to do.
                            //
                            // KEEP-MUSIC PATH (mixMusicAfterMeme == true): the voice is mixed AFTER
                            // the concat, onto a stream whose t=0 is the start of the thumbnail
                            // intro and which already contains every meme. A delay in the body
                            // clock is then wrong twice over — early by the intro, and early by the
                            // total length of every meme that plays before the take. With memes
                            // only ever at the very end that never showed; with a meme mid-video a
                            // take can land seconds out of place.
                            //
                            // ⚠️ The ducking pulse above deliberately keeps the BODY clock: it acts
                            // on `aPreparedPad`, which is the pre-intro, pre-meme audio. Do not
                            // "fix" it to match this.
                            // ─────────────────────────────────────────────────────────────────
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
                    // ─────────────────────────────────────────────────────────────────────────
                    // AUDIO_02 — THE GAME BUS WAS SKIPPING ITS NORMALISATION WHENEVER THERE WAS
                    // NO VOICE-OVER.
                    //
                    // This branch used to ONLY set the flag, with a comment saying it did so "so
                    // we pass correct args to AudioFilterChain" — and that is exactly the trap.
                    // `hasSecondPass = true` tells the mixer "the game is already levelled, do not
                    // apply the flat fallback gain" (see the `hasSecondPass ? 0.0` arguments), and
                    // the music still receives its matching follow-gain. But the filter that was
                    // supposed to do the levelling was never added to the graph on this path.
                    //
                    // Net effect on every export WITHOUT a voice-over: game +0 dB, music +4.4 dB.
                    // The proof is in the exported filter graph — `[a_prepared_base]anull[a_main_raw]`
                    // and no `loudnorm` anywhere, while the music carried `volume=1.6672`.
                    //
                    // ⚠️ THE TWO BRANCHES MUST APPLY THE SAME FILTER. If you touch the voice-over
                    // path above, mirror it here. A flag that claims work was done, without the
                    // work, is worse than no flag.
                    // ─────────────────────────────────────────────────────────────────────────
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
                        voiceOverLabel: effectiveTakes.Count > 0 ? (effectiveTakes.Count > 1 ? "[vo_mixed_all]" : "[vo_delayed_0]") : null,
                        musicBedGainDb: musicBedGains);   // AUDIO_03

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

                    // ─────────────────────────────────────────────────────────────────────────
                    // MEME_05 — PREPARE EVERY MEME'S OWN VIDEO AND AUDIO BRANCH.
                    //
                    // ⚠️ THIS LOOP IS PER MEME, DELIBERATELY. Memes are unrelated third-party rips:
                    // different resolutions, different frame rates, different levels, some silent,
                    // some still images. Every one of them has to be brought onto the SAME canvas,
                    // the SAME frame rate and the SAME sample format before concat will accept it,
                    // and each needs its own measured gain (MEME_01). Nothing here may be hoisted
                    // out of the loop and shared.
                    // ─────────────────────────────────────────────────────────────────────────
                    foreach (var m in memes)
                    {
                        string memeScale =
                            $"scale={canvas}:force_original_aspect_ratio=decrease," +
                            $"pad={canvas}:(ow-iw)/2:(oh-ih)/2:color=black," +
                            $"scale=w=floor(iw/2)*2:h=floor(ih/2)*2,format=yuv420p,setsar=1,fps={targetFps}:start_time=0:round=near";

                        // ─────────────────────────────────────────────────────────────────────
                        // AUDIO_02 — THE MEME RIDES THE SAME LOUDNESS CHANGE AS EVERYTHING ELSE.
                        //
                        // A meme is concatenated into the finished stream, so it never passes
                        // through the game bus and receives none of its normalisation. On a quiet
                        // clip the body was lifted toward the target while the meme stayed where it
                        // was — so the meme arrived jarringly loud by comparison.
                        //
                        // Shift it by the SAME dB the rest of the mix moved. This is deliberately a
                        // relative shift, not a normalisation: the meme keeps its own character and
                        // its relationship to the body is preserved, which is the whole point.
                        // ─────────────────────────────────────────────────────────────────────
                        string memeAudio = "aresample=48000:async=1";

                        // ─────────────────────────────────────────────────────────────────────
                        // MEME_01 — LEVEL THE MEME, THEN CLAMP ITS SPIKES.
                        //
                        // Two different jobs, and they need to happen in this order:
                        //   1. `volume` puts the whole clip at the same loudness as the rest of the
                        //      video, so it no longer arrives louder or quieter than its neighbours.
                        //   2. `alimiter` catches the individual bangs. Meme rips are full of
                        //      clipped screams and explosions that sit far above their own average,
                        //      and a loudness match alone does nothing about those — matching the
                        //      AVERAGE can even push a spiky clip's peaks higher. The limiter is
                        //      what stops the "off the chart" moments, and it is deliberately a
                        //      touch below the master ceiling so a meme can never be the loudest
                        //      thing in the export.
                        //
                        // When the measurement failed we fall back to the previous behaviour —
                        // shift by the same dB as the rest of the mix — so a meme is never left
                        // un-levelled just because ffprobe could not read it.
                        // ─────────────────────────────────────────────────────────────────────
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

                        // ─────────────────────────────────────────────────────────────────────
                        // MEME_05 — A MEME FADES OUT ONLY WHERE THERE IS SOMETHING TO FADE INTO.
                        //
                        // The fade was unconditional, from when a meme was always the last thing on
                        // screen. Whether it helps or hurts depends entirely on WHAT FOLLOWS:
                        //
                        //   TRAILING meme  -> the video ends. Fading to black is the ending. KEEP.
                        //   LEADING meme   -> the gameplay that follows opens with its own
                        //                     `fade=t=in` over the lead-in pad, so the meme's fade
                        //                     to black and the gameplay's fade up form one clean
                        //                     transition. KEEP. (This is also what the shipped
                        //                     MemeAtStart export does, preserved exactly.)
                        //   INTERIOR meme  -> the gameplay resumes mid-render with NO fade-in, so a
                        //                     fade to black is followed by a hard cut out of black.
                        //                     That is the jarring case. SKIP IT — a mid-video meme
                        //                     hard-cuts on both sides, the classic cutaway.
                        //
                        // Owner's decision, 2026-08-16.
                        // ─────────────────────────────────────────────────────────────────────
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
                            // ⚠️ A SILENT OR IMAGE MEME STILL NEEDS AN AUDIO PAD. concat with a=1
                            // demands one audio stream per segment; without this the whole graph
                            // fails to configure.
                            coreFilters.Add($"anullsrc=r=48000:cl=stereo,atrim=duration={m.DurationSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS{m.ALabel}");
                    }

                    // The cut times, the piece boundaries and each meme's slot were all computed
                    // further up, BEFORE the voice-over block, because the voice-over needs them to
                    // place its takes on the KeepMusicDuringMeme path. Do not move them back here.
                    var cuts = memeCuts;
                    int pieceCount = memePieceCount;

                    var vPieces = new List<string>();
                    var aPieces = new List<string>();

                    if (pieceCount == 1)
                    {
                        // Nothing lands inside the video, so there is nothing to cut. This is the
                        // historical prepend/append graph, reproduced exactly.
                        vPieces.Add("[v_render_out]");
                        aPieces.Add(aOutputFinal);
                    }
                    else
                    {
                        // ⚠️ EXPLICIT split / asplit, NEVER an implicit one. Handing the same label
                        // to several filters lets FFmpeg improvise the duplication, and this
                        // codebase has already lost a full second of video to that (see the
                        // implicit-split A/V desync entry in the changelog). An explicit split
                        // clones the buffers in lockstep.
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

                    // ─────────────────────────────────────────────────────────────────────────
                    // MEME_05 — THE N-WAY CONCAT.
                    //
                    // Slot 0 is ahead of the first piece, slot i sits between piece i-1 and piece i,
                    // and the last slot trails everything. ⚠️ THE VIDEO AND AUDIO LABELS OF EACH
                    // SEGMENT MUST STAY ADJACENT AND IN [v][a][v][a] ORDER — concat reads its
                    // inputs positionally, and one transposed pair silently welds the wrong audio
                    // to the wrong picture.
                    // ─────────────────────────────────────────────────────────────────────────
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
                        musicBedGainDb: musicBedGains);   // AUDIO_03

                    foreach (var part in built.chains)
                    {
                        coreFilters.Add(part);
                    }
                    aOutputFinal = built.finalLabel;
                    
                    // ─────────────────────────────────────────────────────────────────────────
                    // MEME_05 — THIS IS THE MUSIC'S TAIL FADE, SO IT IS SIZED BY WHATEVER IS LAST
                    // ON SCREEN — AND THAT IS ONLY A MEME IF A MEME ACTUALLY TRAILS.
                    //
                    // `memes` is sorted by cut position, so `memes[^1]` is the last meme to appear
                    // — but with every meme mid-video the last THING is the gameplay, which already
                    // carries its own closing fade from the pad above. Sizing a music fade off a
                    // meme that plays in the middle would start the fade in the wrong place and
                    // duck the music under footage that is not ending.
                    // ─────────────────────────────────────────────────────────────────────────
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

                // ─────────────────────────────────────────────────────────────────────────────
                // AUDIO_01 — REMOVED: a SECOND, BLIND loudnorm over the finished mix.
                //
                // This line used to sit here:
                //     {aOutputFinal}loudnorm=I=-23:TP=-1.5:LRA=11[a_mastered]
                //
                // ⚠️ NEVER PUT IT BACK. It looks harmless — same target as the game pass — but it
                // supplies NO measured values and NO `linear=true`, and FFmpeg's loudnorm only
                // applies a single fixed gain when all five measured_* parameters are present.
                // Without them it runs in DYNAMIC mode: a continuously-acting automatic gain
                // control over the whole mix. See GetLoudnormFilter() below, which does it
                // correctly with linear=true + measured_I/TP/LRA/thresh, and whose own comment
                // spells out that this is what separates a real two-pass from a guess.
                //
                // WHAT IT ACTUALLY DID: a dynamic AGC follows whatever is loudest, and a
                // commercial music master (~-8 to -10 LUFS) is far louder than gameplay. So the
                // MUSIC drove the gain of the ENTIRE mix — every swell pulled the gunshots and
                // effects down with it, every dip pushed them back up. The sidechain ducking and
                // the carving EQ above were computed correctly and then silently overridden by
                // this one line, producing the exact inverse of the intended effect: game audio
                // being modulated by the music instead of the other way around.
                //
                // It was also UNCONDITIONAL, so exports with no music at all were squashed by the
                // same AGC for no reason.
                //
                // The game audio is already linear-normalised with real measured values, the music
                // sits beneath it via sidechaincompress, and the alimiter below remains as the peak
                // safety net. A second normalisation pass over the mix is not needed and cannot be
                // done honestly here — it would require measuring the MIXED audio first.
                // ─────────────────────────────────────────────────────────────────────────────

                if (AutoSpikeFlattening)
                {
                    // LOG_04: the loudness "cutoff" stage — say it is on and at what ceiling.
                    // AUDIO_07 (#7): -1.5 dB -> -1.0 dB. loudnorm already guarantees a -1.5 dBTP
                    // true-peak ceiling, so a limiter at the SAME -1.5 dB could only ever act on
                    // material that had already been handled — contributing distortion risk and
                    // nothing else. Held slightly higher it becomes what it should be: a genuine
                    // emergency catch for the rare case where music and game peaks coincide.
                    CoreLogger.Info("Audio",
                        "PEAK LIMITER ON: alimiter ceiling -1.0 dB (safety net above the -1.5 dBTP " +
                        "loudnorm ceiling). Only momentary peaks are held back; it does not change " +
                        "the overall level set by the normalisation plan.");
                    coreFilters.Add($"{aOutputFinal}alimiter=limit=-1.0dB:level_in=1:level_out=1[a_flattened]");
                    aOutputFinal = "[a_flattened]";
                }
                else
                {
                    CoreLogger.Info("Audio", "PEAK LIMITER OFF: no ceiling applied (Auto Spike Flattening is disabled).");
                }

                string filterScript = string.Join(";", coreFilters.Where(p => !string.IsNullOrEmpty(p)));
                // ─────────────────────────────────────────────────────────────────────────────
                // LOG_02 — THE FILTER GRAPH IS THE STORY. IT MUST BE AT INFO.
                //
                // The logged "Executing Final Pipeline Command" line looks complete but is NOT:
                // the graph is handed to FFmpeg as `-filter_complex_script <file>`, so the command
                // shows only a PATH. That file is written into tempJobDir and deleted with it when
                // the job ends — so after any finished export, every filter that actually shaped
                // the video and audio was gone, and the log could not answer the one question
                // worth asking: what did we really do to this footage?
                //
                // This is where the scale/crop/pad geometry, the zoom ramps, the HUD compositing
                // and the ENTIRE audio chain live — carving EQ, the sidechain ducking values, the
                // 150 Hz bass split. Debugging "the music ducks too hard" or "the crop is off"
                // without this line means reproducing the export just to see the graph.
                // ─────────────────────────────────────────────────────────────────────────────
                CoreLogger.Info("FFmpeg", $"Filter Script Content:\n{filterScript}");
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
                    tempJobDir, DiskSpaceGuard.EstimateTwoPassMasterBytes(renderDurationSec + memeTotalDuration));

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

                        // MEME_05 — one `-i` per meme, in the SAME order the input indices were
                        // handed out above. Reordering either side silently swaps the memes.
                        foreach (var m in memes)
                        {
                            if (m.IsImage)
                            {
                                // Still image — intentionally NO hwaccel.
                                ffmpegArgs.AddRange(["-loop", "1", "-framerate", targetFps, "-t", m.DurationSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "-i", m.FilePath]);
                            }
                            else
                            {
                                // G04: video meme — needs its own decode flags.
                                ffmpegArgs.AddRange(decodeFlags);
                                ffmpegArgs.AddRange(["-i", m.FilePath]);
                            }
                        }

                        var voTakes = GetEffectiveVoiceOverTakes();
                        foreach (var voTake in voTakes)
                            ffmpegArgs.AddRange(["-i", voTake.Path]);

                        ffmpegArgs.AddRange(["-filter_complex_script", filterScriptPath]);
                        double totalOutputDurationSec = renderDurationSec + memeTotalDuration;

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
                        CoreLogger.Info("FFmpeg", $"Executing Final Pipeline Command:\n{_ffmpegPath} {cmdLine}");

                        // ─────────────────────────────────────────────────────────────────────
                        // LOG_05 — ONE FINAL, SELF-CONTAINED, RUNNABLE COMMAND.
                        //
                        // The line above is what was literally executed, but it is NOT reproducible
                        // on its own: the whole filter graph hides behind
                        // `-filter_complex_script <path>`, and that file lives in tempJobDir, which
                        // is deleted the moment the job ends. So after any finished export the log
                        // could describe the encode but never REPLAY it.
                        //
                        // This second line substitutes the script back inline as `-filter_complex`,
                        // producing a single command that can be pasted into a terminal and re-run
                        // verbatim. That is what makes the log the source of truth rather than a
                        // summary of it: every filter, every audio value, every path, in one place.
                        //
                        // Safe to quote with double quotes — the graph uses single quotes
                        // internally (weights='1 1', volume='...'), never double.
                        // ─────────────────────────────────────────────────────────────────────
                        try
                        {
                            // Rebuild the script token EXACTLY as FormatForLog rendered it — it only
                            // quotes an argument that is empty or contains a space or a quote. Assuming
                            // the unquoted form would silently fail to match the day the temp path
                            // lands somewhere with a space in it, and the "final command" would then
                            // still point at a file that no longer exists.
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
                    // ─────────────────────────────────────────────────────────────────────────
                    // TEMP_01 — THE THUMBNAIL IS SCRATCH, NOT A DELIVERABLE. KEEP IT OUT OF THE
                    // USER'S FOLDER.
                    //
                    // This used to be `Path.Combine(outputDir, ...)`, which dropped a
                    // `<video>_thumbnail.jpg` right next to the exported video — in Downloads, or
                    // wherever the user sends their exports. It was the ONLY thing in the whole
                    // pipeline that wrote anything other than the finished video into that folder,
                    // and it was never cleaned up: the delete below only fires for a ZERO-BYTE
                    // file, so every successful export left one more .jpg behind, for ever.
                    //
                    // It now lands in this job's own `tempJobDir` under
                    // %TMP%\Fortnite_Video_Software\, which is deleted wholesale in the finally
                    // block at the end of this method — so cleanup is automatic and cannot be
                    // forgotten, exactly like the two-pass master and the filter scripts.
                    //
                    // ⚠️ NOTHING IN THE APP READS THIS FILE. It is produced, validated, logged and
                    // discarded. The thumbnail MARKER still does real work regardless — the same
                    // `ThumbnailPosMs` is assigned to `IntroAbsTimeMs` in MainMediaController, and
                    // that is what freezes the chosen frame at the head of the exported video. If
                    // you ever want the user to KEEP this picture, surface it deliberately (a
                    // "Save thumbnail" action); do not send it back to `outputDir` by default.
                    // ─────────────────────────────────────────────────────────────────────────
                    string thumbnailOutput = Path.Combine(tempJobDir, Path.GetFileNameWithoutExtension(finalOutput) + "_thumbnail.jpg");
                    double extractTargetSec = granularTimeMapper != null
                        ? Math.Max(0.0, granularTimeMapper(ThumbnailPosMs / 1000.0))
                        : Math.Max(0.0, (ThumbnailPosMs - actualExtractStartMs) / 1000.0 / Math.Max(0.001, SpeedFactor));
                    if (introDurationSec > 0.0)
                    {
                        extractTargetSec += introDurationSec;
                    }

                    // ⚠️ MEME_05 — THIS SEEKS THE FINISHED FILE, WHICH CONTAINS THE MEMES. Every
                    // term above is in the PRE-MEME clock, so without this the grab lands early by
                    // the total length of every meme before the chosen moment — far enough, with a
                    // mid-video meme, to grab a frame of the meme instead of the gameplay the user
                    // picked. Same shift as the voice-over: the chosen frame is a moment of
                    // gameplay, and that gameplay resumes AFTER any meme sitting on it.
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
                // LOG_01: the FULL destination, not just the file name. "Output: Fortnite-Video-1.mp4"
                // told you nothing about WHERE it landed — Downloads, a custom folder, or the
                // fallback picked when the chosen one was unwritable. The log is the story teller.
                CoreLogger.Info("Process", $"Pipeline completed in {pipelineStopwatch.Elapsed.TotalSeconds:F1}s. Output: {finalOutput}");
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
            // AUDIO_02: single source of truth — the same target the user is judged against in the
            // "your video is too quiet" prompt, and the same one the export masters to.
            double targetLufs = AudioLoudnessProbe.TargetLufs;
            var args = new List<string>
            {
                "-y", "-hide_banner",
                "-ss", (measureStartMs / 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                "-t", ((measureEndMs - measureStartMs) / 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                "-i", InputPath,
                // AUDIO_02: the measurement pass MUST declare the same target as the second pass.
                // input_i/tp/lra/thresh describe the source and do not depend on the target, but
                // `target_offset` does — and that offset is handed straight to the second pass.
                // Measuring against -23 and then rendering against -14 fed the second pass an
                // offset computed for a different target, skewing the very gain it was supposed
                // to make exact.
                "-af", $"loudnorm=I={AudioLoudnessProbe.TargetLufs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}" +
                       $":TP={AudioLoudnessProbe.PeakCeilingDbtp.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}:LRA=11:print_format=json",
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

                        // ─────────────────────────────────────────────────────────────────────
                        // LOG_04 — SAY WHAT THE NORMALISATION IS ABOUT TO DO, IN FULL.
                        // The measurement line above only reported what was MEASURED. It never
                        // stated the target, the resulting gain, or which streams would move with
                        // it — so a mix that came out wrong could not be diagnosed from the log.
                        // ─────────────────────────────────────────────────────────────────────
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
            if (string.IsNullOrWhiteSpace(track.Path) || gains.ContainsKey(track.Path)) continue;
            try
            {
                var reading = await AudioLoudnessProbe.MeasureAsync(_ffmpegPath, track.Path, cancellationToken)
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

                CoreLogger.Info("Audio",
                    $"MUSIC BED PLAN: '{Path.GetFileName(track.Path)}' measured {reading.IntegratedLufs:F2} LUFS -> " +
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

        // ─────────────────────────────────────────────────────────────────────────────────────
        // AUDIO_02 — ONE LOUDNESS TARGET FOR THE WHOLE APP. THIS USED TO SAY I=-23.
        //
        // -23 LUFS is EBU R128, the BROADCAST television standard. Every platform this app
        // actually exports for — YouTube, TikTok, Reels, Shorts — normalises to about -14 LUFS
        // and will simply turn anything louder back down. Mastering to -23 meant shipping a file
        // roughly 9 dB quieter than the platform target for no benefit.
        //
        // ⚠️ THE REAL DAMAGE WAS THE MISMATCH, NOT THE NUMBER. The music's "follow" gain is
        // derived from VolumeNormalizeDb = TargetLufs - measured, i.e. against -14, while THIS
        // filter pushed the game bus toward -23. On a -18.4 LUFS clip that is +4.4 dB on the music
        // and -4.6 dB on the game: the two moved in OPPOSITE directions, roughly 9 dB apart, every
        // single export. That is what buried the gunshots under the music.
        //
        // Both now read the same constants, so the number the user is judged against in the
        // "your video is too quiet" prompt is the number the export actually delivers.
        // ⚠️ DO NOT hardcode a target here again — change AudioLoudnessProbe.TargetLufs instead.
        // ─────────────────────────────────────────────────────────────────────────────────────
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
            catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }

            try { proc.Dispose(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
            _currentProcess = null;
        }
    }
}

public record VoiceOverTake(string Path, double StartSec);
