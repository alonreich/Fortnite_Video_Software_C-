// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public class ProcessWorker : IDisposable
{
    private readonly ApplicationPaths _paths;

    /// <summary>
    /// PROCGATE_01 — the currently running child process, and the ONLY field in this class that is
    /// handed between the export thread and the cancelling thread while holding an OS resource.
    ///
    /// It used to be a bare, NON-volatile field — note that its two neighbours below were already
    /// marked volatile, and this one, the only one that matters for correctness, was not. Cancel()
    /// tested it for null and then called Kill() on it as two separate reads, so the export thread
    /// could run `_currentProcess = null; proc.Dispose();` in between. Kill() on a disposed Process
    /// throws ObjectDisposedException straight into CoreLogger.Swallowed, and the user's cancel was
    /// silently lost — AFTER the log had already announced "Terminating FFmpeg process tree."
    ///
    /// Access is now exclusively through <see cref="SetCurrentProcess"/>, <see cref="TakeCurrentProcess"/>
    /// and <see cref="PeekCurrentProcess"/>, all of which hold the shared FfmpegJobLifetime gate (PIPELIFE_01). The gate is
    /// held for a reference copy only — never across a Kill, a Dispose or any I/O — so it cannot
    /// deadlock against the export thread.
    /// </summary>
    /// <summary>
    /// PIPELIFE_01 — process slot, cancel flag, single-flight finish and teardown ladder, shared
    /// with <see cref="MergerWorker"/>. The members below keep their original names and delegate,
    /// so none of this file's ~30 call sites change.
    /// </summary>
    private readonly FfmpegJobLifetime _lifetime = new("Process", "FFmpeg");
    // PIPELIFE_01 — the gate itself moved into FfmpegJobLifetime, which now owns the process slot.

    /// <summary>
    /// FFMPEGSTOP_01 — single-flight gate for the cooperative shutdown ladder.
    ///
    /// <para>WHAT WAS WRONG. Every termination site in this file called
    /// <c>Kill(entireProcessTree: true)</c> as the FIRST and ONLY action, and no
    /// <see cref="ProcessStartInfo"/> here redirected stdin — so FFmpeg's interactive quit
    /// command ('q') had no channel and this pipeline was STRUCTURALLY INCAPABLE of asking the
    /// encoder to stop cleanly. Killing an MP4 muxer mid-write means the <c>moov</c> atom is never
    /// emitted: the output is <c>mdat</c> payload with no index — right size, right name, and
    /// unplayable in every player. On the failure paths that reach
    /// <see cref="TryRescueFinishedRender"/>, that corrupt file was then moved to
    /// <c>FreeVideoStudio-RECOVERED-*.mp4</c> and presented to the user as preserved work.</para>
    ///
    /// <para>⚠️ THE PROJECT ALREADY BUILT THE FIX AND THIS FILE NEVER RECEIVED IT.
    /// <see cref="GracefulProcessTerminator"/> states the failure verbatim and is used by
    /// <c>CrashLogDigest</c>, <c>DeploymentLifecycle</c>, <c>AsyncProcessRunner</c>,
    /// <c>HudAutoDetector</c> and <c>MergerWorker</c>. ProcessWorker — the PRIMARY user-facing
    /// export path — was the only media worker that never referenced it. The two copies of
    /// <see cref="ReadExitCodeSafely"/> are the proof: MergerWorker's routes through the ladder,
    /// this one called Kill raw, from an identical signature. Duplicated logic is how that
    /// divergence happened and went unnoticed.</para>
    ///
    /// <para>The gate guarantees exactly ONE ladder ('q' → grace → Kill(tree) → exit confirmation)
    /// ever runs per FFmpeg process, because <see cref="Cancel"/>, the cancellation-token
    /// registrations and <see cref="Dispose"/> can all race each other.</para>
    /// </summary>
    /// PIPEDEDUP_01 — these three fields and their three methods were written out here AND,
    /// separately, in <c>MergerWorker</c>. Two copies of one mechanism is exactly how
    /// <c>ReadExitCodeSafely</c> silently diverged between these two files (FFMPEGSTOP_01) and how
    /// <c>TryRescueFinishedRender</c> shipped the same race twice (RESCUE_01). One copy now lives
    /// in <see cref="CooperativeShutdownGate"/>; the members below are thin delegations kept at
    /// their original signatures so no call site in this file changes.
    // PIPELIFE_01 — the shutdown ladder now lives in FfmpegJobLifetime.

    /// <summary>
    /// PIPELIFE_01 — kept as a private alias over the shared lifetime's flag so every existing
    /// read and write in this file compiles unchanged. Writing `false` is deliberately a no-op:
    /// a cancelled job is never un-cancelled, and nothing in either pipeline ever tried to.
    /// </summary>
    private bool _isCanceled
    {
        get => _lifetime.WasCanceled;
        set { if (value) _lifetime.MarkCanceled(); }
    }
    /// <summary>PIPELIFE_01 — alias over the shared lifetime's single-flight flag.</summary>
    private bool _finishEmitted => _lifetime.FinishEmitted;
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
    public string LastVideoPipeline { get; private set; } = "";
    public bool UsedGpuVideoProcessing { get; private set; }

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
    public bool ShowTeammates { get; set; }
    public bool ShowSpectating { get; set; } = true;
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

    // ── PROBE_01 ── telemetry for the empirical NVENC complexity probe. Read by
    // tests/MediaPipelineChecks to assert the probe ran, stayed off the CUDA decode
    // path, and actually replaced the blind size retry on the first attempt.
    public bool ComplexityProbeRan { get; private set; }
    public double ComplexityProbeBitsPerSecond { get; private set; }
    public double ComplexityProbeScaleFactor { get; private set; } = 1.0;
    public string? LastComplexityProbeCommandLine { get; private set; }
    public double ThumbnailPosMs { get; set; }
    public double IntroStillSec { get; set; }

    /// <summary>TIMINGTAG_02 — the timing stamped into the delivered file; set before the first encode.</summary>
    private ExportTiming _exportTiming = new(60, 1, 0, null, null);
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
    /// <summary>
    /// PEAKSAFE_01 — the user's "soften sudden loud moments" switch: the gameplay peak tamer
    /// (<see cref="PeakSafety.TamerFilter"/>). The true-peak SAFETY limiter on the final mix is NOT
    /// controlled by this; it is always on.
    /// </summary>
    public bool AutoSpikeFlattening { get; set; } = true;

    /// <summary>
    /// The gameplay's integrated loudness (LUFS) from the upload-time peak probe, when the UI has
    /// one. Only a FALLBACK: the export measures the exported range itself, and uses this when that
    /// measurement fails. Consumers: the peak tamer's threshold and meme matching (MEMELEVEL_02).
    /// Nothing ever normalises the gameplay (LOUDSTD_REMOVED_01).
    /// </summary>
    public double? GameplayLoudnessLufs { get; set; }

    /// <summary>
    /// PREVIEWMIX_01 — when set, the worker builds the export graph exactly as usual and then renders
    /// ONLY its final audio (AudioGraphPruner) to this WAV, for the live preview. No video is decoded
    /// or encoded and nothing is written to the output folder. See <see cref="AudioPreviewMap"/>.
    /// </summary>
    public string? AudioPreviewOutputPath { get; set; }

    /// <summary>PREVIEWMIX_01 — set on a successful audio-preview render: how preview time maps into the WAV.</summary>
    public AudioPreviewMap? AudioPreviewMap { get; private set; }

    public bool VoiceOverDuckAudio { get; set; }

    /// <summary>
    /// VOPROT_01 — "Protect VoiceOver Recording from Music". Ducks and EQ-carves the MUSIC bed
    /// across the voice-over takes. Independent of the Add Music wizard's own ducking, which
    /// protects the GAME from the music and is triggered by the game, not by the voice.
    /// No music on this export means this flag has nothing to act on and is silently inert.
    /// </summary>
    public bool VoiceOverProtectFromMusic { get; set; }

    /// <summary>
    /// ISSUE_04 — destination folder for the finished file, resolved by the UI layer
    /// (OutputFolderResolver) BEFORE the render starts. Null keeps the legacy behaviour of
    /// resolving Downloads here, but the UI always sets it so a missing Downloads folder is
    /// handled with a picker instead of a crash.
    /// </summary>
    public string? OutputDirectory { get; set; }

    /// <summary>
    /// OUTNAME_01 — the user's automatic file-name base from Settings › Output Files. The finished
    /// file is <c>&lt;base&gt;-&lt;N&gt;.mp4</c>. Sanitized here again (defence in depth); null or
    /// unusable input falls back to <see cref="OutputFileNaming.MainDefaultBaseName"/>.
    /// </summary>
    public string? OutputBaseName { get; set; }

    /// <summary>
    /// ISSUE_06 — the raw error text (FFmpeg stderr tail / exception detail) behind the last
    /// failure. The UI feeds this to ErrorReporter, which mines the root-cause line out of it
    /// for the failure dialog. Never shown raw to the user.
    /// </summary>
    public string? FailureDetail { get; private set; }

    /// <summary>
    /// Strongly typed failure model providing structured category, stage, attempt history,
    /// exit/error codes, and supporting diagnostics for ErrorReporter.
    /// </summary>
    public ExportFailure? LastFailure { get; private set; }

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
        /// <summary>MEMELEVEL_02 — the meme's own measured loudness; null when unmeasured.</summary>
        public double? MeasuredLufs;
        public int InputIndex;
        public double CutOutputSec;
        public int SlotIndex;
        /// <summary>MEMEMODE_01 — corner-overlay presentation (only meaningful for the corner list).</summary>
        public MemeOverlayCorner Corner = MemeOverlayCorner.BottomRight;
        public MemeOverlaySize Size = MemeOverlaySize.Medium;
        public bool PlaySound = true;
        public bool IsCorner;

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
        _ffmpegPath = FreeVideoStudio.Core.Infrastructure.BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
        _ffprobePath = FreeVideoStudio.Core.Infrastructure.BinaryPathResolver.Resolve("ffprobe.exe", "backend", "binaries");
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

    /// <summary>PROCGATE_01 — publish the live child process. Export thread only.</summary>
    private void SetCurrentProcess(Process? proc) => _lifetime.SetCurrentProcess(proc);

    /// <summary>
    /// PROCGATE_01 — claim the live child process AND clear the slot in one atomic step, so exactly
    /// one caller can ever be responsible for disposing it. Every teardown site uses this instead of
    /// the old `_currentProcess = null; proc.Dispose();` pair.
    /// </summary>
    private Process? TakeCurrentProcess() => _lifetime.TakeCurrentProcess();

    /// <summary>PROCGATE_01 — one consistent read for callers that only observe.</summary>
    private Process? PeekCurrentProcess() => _lifetime.PeekCurrentProcess();

    /// <summary>
    /// FFMPEGSTOP_01 — starts the bounded cooperative shutdown ladder for <paramref name="proc"/>
    /// exactly once, off the calling thread.
    ///
    /// <para>Ported verbatim from <c>MergerWorker.BeginCooperativeShutdown</c>, which is the
    /// already-proven shape. Fire-and-forget on purpose: the ladder is strictly bounded
    /// (<c>CooperativeGraceMs</c> + <c>HardKillConfirmMs</c>) but it DOES wait, and this is called
    /// from <see cref="Cancel"/> (UI thread) and from cancellation-token registrations (which run
    /// synchronously on whichever thread cancels). Neither may block. Faults are observed so a
    /// background stop can never surface as an unobserved task exception.</para>
    ///
    /// <para>Single-flight: <see cref="Cancel"/>, the token registrations and <see cref="Dispose"/>
    /// may all race, but only one ladder ever runs per process.</para>
    /// </summary>
    private void BeginCooperativeShutdown(Process? proc, string logTag, bool attemptQuitCommand)
        => _lifetime.BeginCooperativeShutdown(proc, attemptQuitCommand);

    /// <summary>
    /// FFMPEGSTOP_01 — awaits the in-flight shutdown ladder, if any. Bounded by the ladder itself.
    /// Called before <see cref="ReadExitCodeSafely"/> so the exit code is read from a process that
    /// has actually finished finalizing its output, not one still writing its moov atom.
    /// </summary>
    private Task AwaitActiveShutdownAsync() => _lifetime.AwaitActiveShutdownAsync();

    /// <summary>
    /// PROCGATE_01 — best-effort cancellation. The authoritative mechanism is the CancellationToken
    /// registrations taken around each child process; this remains the belt-and-braces path, but it
    /// now acts on a SINGLE consistent read of the process reference instead of re-reading the field
    /// between the null test and the Kill.
    ///
    /// FFMPEGSTOP_01 — the Kill is now the LAST rung of a ladder rather than the first action. The
    /// call still returns immediately (the ladder runs off-thread), so a cancel can never hang the
    /// UI, but FFmpeg now gets the chance to finalize its container instead of being shot mid-write.
    /// </summary>
    public void Cancel() => _lifetime.Cancel(
        stoppingMessage: "Cancellation requested by user. Stopping the FFmpeg process tree (cooperative quit, then hard kill).",
        idleMessage: "Export worker released on shutdown (no encode was running).");

    /// <summary>
    /// ISSUE_04 — reads a child process's exit code without ever throwing.
    ///
    /// Callers reach here after a CANCELLABLE wait, so the process may not have finished dying
    /// yet (Kill is asynchronous). Give it a short grace period, then fall back to a sentinel
    /// rather than letting InvalidOperationException masquerade as a pipeline crash.
    /// </summary>
    /// FFMPEGSTOP_01 — was a bare WaitForExit -> Kill(tree) -> WaitForExit while the sibling copy
    /// in MergerWorker had already been hardened to the bounded ladder. PIPEDEDUP_01 removed the
    /// second copy so the two can never disagree again; the default stays true here because this
    /// pipeline's cancellable wait can return with the ladder unfinished.
    private static int ReadExitCodeSafely(Process proc, string logTag, int graceMs = 5000, bool attemptQuitCommand = true)
        => CooperativeShutdownGate.ReadExitCodeSafely(proc, logTag, graceMs, attemptQuitCommand);

    /// <summary>
    /// Runs the complete rendering pipeline. Returns true on success.
    /// Exact port of ProcessThread.run() logic flow.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        UsedGpuVideoProcessing = false;

        var earlierAttempts = new List<ExportFailure>();
        int attemptCounter = 0;

        // ══════════════════════════════════════════════════════════════════════════════════════
        // CANCELREG_01 — THIS REGISTRATION MUST STAY INSIDE THE TRY. DO NOT HOIST IT BACK OUT.
        //
        // It used to sit ABOVE the try. CancellationToken.Register throws ObjectDisposedException
        // when its CancellationTokenSource has already been disposed — which is exactly what
        // happened when the user cancelled an export and immediately started another one, because
        // the new export disposed the previous CTS while this worker still held registrations on it.
        // That exception escaped RunAsync entirely, so EmitFinished NEVER FIRED, the controller's
        // TaskCompletionSource never completed, and the caller's await hung forever: overlay gone,
        // PROCESS button dead, no error on screen.
        //
        // Inside the try, the same throw lands in the catch-all at the bottom of this method, which
        // always calls EmitFinished. A cancelled export then reports as cancelled instead of wedging
        // the UI. (EXPORTSESSION_01 in MainWindow.Export.cs removes the disposal race itself; this
        // is the defence in depth that keeps a future regression from being unrecoverable.)
        // ══════════════════════════════════════════════════════════════════════════════════════
        CancellationTokenRegistration cancelMirror = default;

        try
        {
            if (cancellationToken.CanBeCanceled)
            {
                cancelMirror = cancellationToken.Register(() => _isCanceled = true);
            }
            var pipelineStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var encoderMgr = await Task.Run(() => new EncoderManager(HardwareStrategy, _ffmpegPath), cancellationToken).ConfigureAwait(false);
            // TEMPO_01 — Rubber Band capability, probed once per FFmpeg binary and cached, BEFORE any
            // graph is built. Never throws; a failed probe means the atempo chain.
            await AudioTempoFilterBuilder.EnsureProbedAsync(_ffmpegPath).ConfigureAwait(false);
            if (encoderMgr.EncoderPreflightError != null)
            {
                LastFailure = new ExportFailure
                {
                    Category = ExportFailureCategory.MissingEncoder,
                    Stage = ExportStage.Preflight,
                    Attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "EncoderPreflight", Encoder = HardwareStrategy, Description = $"Hardware encoder preflight ({HardwareStrategy})" },
                    Summary = "The requested video encoder is not available on this system.",
                    SpecificCause = encoderMgr.EncoderPreflightError,
                    DiagnosticLines = [encoderMgr.EncoderPreflightError]
                };
                FailureDetail = LastFailure.FormatDiagnosticReport();
                EmitFinished(false, LastFailure.Summary);
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
                    // The authoritative read (type-agnostic, never throws) is in AudioFilterChain;
                    // this is only the log line.
                    CoreLogger.Info("Process",
                        $"Music ducking flag: {MusicConfig["ducking_enabled"]?.ToString() ?? "default"}, " +
                        $"carving flag: {MusicConfig["carving_enabled"]?.ToString() ?? "default"}.");
                }
            }

            try
            {
                var prober = new MediaProber(_ffprobePath, InputPath);
                bool sourceHasAudio = await prober.HasAudioAsync();
                int sourceAudioKbps = await prober.GetAudioBitrateAsync();
                double sourceDuration = await prober.GetDurationAsync();
                OriginalResolution = await prober.GetResolutionStringAsync();

                // COLOR_01 — decide the colour conversion ONCE per export, from the source's own tags.
                VideoColorInfo sourceColor = await prober.GetVideoColorInfoAsync();
                bool canToneMap = sourceColor.IsHdr
                    && await ExportColorPolicy.HasFilterAsync(_ffmpegPath, "zscale")
                    && await ExportColorPolicy.HasFilterAsync(_ffmpegPath, "tonemap");
                string? colorChain = ExportColorPolicy.BuildConversionChain(sourceColor, canToneMap, out string colorDescription, out string? colorDegraded);
                CoreLogger.Info("COLOR", $"Source {sourceColor}. {colorDescription}");
                if (colorDegraded != null)
                {
                    FreeVideoStudio.Core.Abstractions.Faults.Degraded("EXPORT", colorDegraded, technicalDetail: colorDescription);
                }

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

                // MEMEMODE_01 — `memes` holds ONLY the full-screen cutaways: everything below that
                // shifts time (meme cuts, MemeTimeInsertedBefore for music and voice-over, the end-pad
                // rule, the timing tag) reads it, so a corner overlay can never move anything. Corner
                // overlays are resolved into `cornerMemes` and drawn over the rendered stream.
                var memes = new List<ResolvedMeme>();
                var cornerMemes = new List<ResolvedMeme>();
                {
                    var requested = new List<(string path, double atRel, string? id, MemePlacement? placement)>();
                    if (MemePlacements != null && MemePlacements.Count > 0)
                    {
                        foreach (var p in MemePlacements)
                        {
                            if (p == null || string.IsNullOrWhiteSpace(p.FilePath)) continue;
                            requested.Add((p.FilePath, p.AtSourceSecRelative, p.Id, p));
                        }
                    }
                    else if (!string.IsNullOrEmpty(MemeFile))
                    {
                        double clipLenSec = Math.Max(0, (EndTimeMs - StartTimeMs) / 1000.0);
                        requested.Add((MemeFile, MemeAtStart ? 0.0 : clipLenSec, "meme", null));
                    }

                    var usedIds = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < requested.Count; i++)
                    {
                        var (path, atRel, rawId, placement) = requested[i];
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
                            AtSourceSecRelative = atRel,
                            IsCorner = placement?.IsCornerOverlay ?? false,
                            Corner = placement?.Corner ?? MemeOverlayCorner.BottomRight,
                            Size = placement?.Size ?? MemeOverlaySize.Medium,
                            PlaySound = placement?.PlaySound ?? true,
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

                        // MEMEMODE_01 — sound off: the meme is treated as silent everywhere.
                        if (!m.PlaySound) m.HasAudio = false;

                        if (m.HasAudio && !m.IsImage)
                        {
                            // MEMELEVEL_02 — measured here, matched to the gameplay once that has
                            // been measured too (see GAMEPLAY LOUDNESS below).
                            m.MeasuredLufs = await MemeLoudness.MeasureLufsAsync(_ffmpegPath, path, cancellationToken).ConfigureAwait(false);
                        }

                        if (m.IsCorner) cornerMemes.Add(m);
                        else memes.Add(m);
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

                // ══════════════════════════════════════════════════════════════════════════
                // PROBE_01 — EMPIRICAL COMPLEXITY PROBING FOR SIZE-LOCKED NVENC EXPORTS.
                //
                // NVENC ignores `-pass 1` stats files, so a size-locked export used to
                // allocate `-b:v` from budget arithmetic alone and then, if the file
                // missed the target, run a full blind second encode scaled by a generic
                // ratio — which often missed again. Instead we now MEASURE the clip's
                // complexity before the main graph is built: a 5-second slice from the
                // middle of the timeline is pushed through h264_nvenc at a reference
                // CQ (no rate cap) to the null muxer, and its bits-per-second is the
                // content's "appetite" at reference quality.
                //
                // Calibrated empirically on this project's NVENC stack (p7/hq, CBR,
                // multipass fullres, maxrate == b:v): CBR honours the ask in BOTH
                // regimes — starved content (appetite 2.5-4.5x budget, QP starved to
                // ~44) landed +0.1..0.3% over the ask, and flush content filled a
                // 100 Mbps ask at ~102 Mbps — so the landing error comes from mux
                // overhead (~0.13% measured), integer bitrate rounding and the HRD
                // tail of a 2x VBV buffer when the content is starved. The appetite
                // ratio tells us WHICH regime we are in before spending a full encode:
                //
                //   factor = 1 - 0.004 (mux + rounding margin)
                //                - min(0.004, 0.002 * (appetiteRatio - 1))  [starve tail]
                //
                // A 25.0 MB / 20 s export at appetite 2.5x budget therefore asks
                // ~10.29 Mbps instead of ~10.36 and lands at ~24.8 MB — first attempt,
                // no blind retry (the retry loop below stays as the safety net for
                // probe failures and foreign NVENC SDK behaviour).
                //
                // ZERO-COPY GUARDRAIL: the probe builds its own argument list with NO
                // `-hwaccel*` flags and software decoding; frames are uploaded to NVENC
                // from system RAM. It never touches ExportVideoPipeline device flags
                // and lives nowhere near the libmpv preview path (MpvVideoView).
                //
                // Probe failure (no NVENC session, unparsable output, cancellation)
                // is non-fatal: the budget arithmetic above stands unchanged.
                // ══════════════════════════════════════════════════════════════════════════
                if (targetMb.HasValue && videoBitrateKbps.HasValue
                    && HardwareStrategy != "CPU"
                    && budgetDurationSec >= 8.0
                    && encoderMgr.GetInitialEncoder(useCuda: true) == "h264_nvenc")
                {
                    double naiveKbps = videoBitrateKbps.Value;
                    string probeResolution = IsMobileFormat ? "1080x1920" : OriginalResolution;
                    double? appetiteBps = await RunNvencComplexityProbeAsync(
                        actualExtractStartMs, actualExtractEndMs, probeResolution, targetFps,
                        cancellationToken);

                    if (appetiteBps.HasValue && appetiteBps.Value > 0)
                    {
                        ComplexityProbeRan = true;
                        ComplexityProbeBitsPerSecond = appetiteBps.Value;

                        double budgetBps = naiveKbps * 1000.0;
                        double appetiteRatio = appetiteBps.Value / budgetBps;
                        double overheadMargin = 0.004;
                        double starveTail = appetiteRatio > 1.0
                            ? Math.Min(0.004, 0.002 * (appetiteRatio - 1.0))
                            : 0.0;
                        ComplexityProbeScaleFactor = 1.0 - overheadMargin - starveTail;

                        videoBitrateKbps = Math.Max(
                            300, (int)Math.Round(naiveKbps * ComplexityProbeScaleFactor));

                        CoreLogger.Info("FFmpeg",
                            $"PROBE_01 complexity probe: appetite {appetiteBps.Value / 1e6:F2} Mbps vs " +
                            $"{budgetBps / 1e6:F2} Mbps budget (ratio {appetiteRatio:F2}x) — scaling -b:v/-maxrate " +
                            $"{naiveKbps} -> {videoBitrateKbps} kbps (x{ComplexityProbeScaleFactor:F4}) to land on " +
                            $"{targetMb.Value:F1} MB first attempt.");
                    }
                }

                var musicTracks = MusicTracks != null ? new List<MusicTrack>(MusicTracks) : new List<MusicTrack>();
                if (musicTracks.Count > 0 && (padStartHumanSec > 0 || padEndHumanSec > 0))
                {
                    // MUSICPAD_01 — the UI placed the music from MARK START; the body starts at the fade-in pad.
                    double firstBefore = musicTracks[0].TimelineStartDelay;
                    musicTracks = MusicPadAlignment.Align(musicTracks, padStartHumanSec, padEndHumanSec, gDur, MusicLeadFadeIn, MusicTailFadeOut);
                    CoreLogger.Info("Audio",
                        $"Music aligned to the preview: fade-in pad {padStartHumanSec:F3}s, fade-out pad {padEndHumanSec:F3}s; " +
                        $"first track starts at {musicTracks[0].TimelineStartDelay:F3}s of the body (was {firstBefore:F3}s from MARK START).");
                }
                if (musicTracks.Count == 0 && MusicConfig != null)
                {
                    string? mPath = MusicConfig["path"]?.ToString();
                    if (!string.IsNullOrEmpty(mPath))
                    {
                        double mOffset = (double)(MusicConfig["file_offset_sec"]?.GetValue<double>() ?? 0);
                        musicTracks.Add(new MusicTrack(mPath, mOffset, gDur));
                    }
                }


                bool mixMusicAfterMeme = KeepMusicDuringMeme && memeTotalDuration > 0 && musicTracks.Count > 0;

                int? introInputIndex = introDurationSec > 0.001 ? 1 + musicTracks.Count : null;
                string? textInputLabel = textPngPath != null
                    ? $"[{1 + musicTracks.Count + (introInputIndex.HasValue ? 1 : 0)}:v]"
                    : null;
                
                int memeInputBase = 1 + musicTracks.Count + (introInputIndex.HasValue ? 1 : 0) + (textPngPath != null ? 1 : 0);
                for (int i = 0; i < memes.Count; i++) memes[i].InputIndex = memeInputBase + i;
                for (int i = 0; i < cornerMemes.Count; i++) cornerMemes[i].InputIndex = memeInputBase + memes.Count + i;   // MEMEMODE_01

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
                        LastFailure = new ExportFailure
                        {
                            Category = ExportFailureCategory.DiskFull,
                            Stage = ExportStage.Preflight,
                            Attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "DiskSpaceCheck", Description = "Target drive space check" },
                            Summary = "The drive ran out of free space or has insufficient space to export this video.",
                            SpecificCause = space.Message,
                            DiagnosticLines = space.Message != null ? [space.Message] : Array.Empty<string>()
                        };
                        FailureDetail = LastFailure.FormatDiagnosticReport();
                        EmitFinished(false, LastFailure.Summary);
                        return;
                    }
                }

                double encodeFloor = 0.0;

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
                        catch (System.Exception swallowed10)
                        {
                            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed10);   // FAULTTIER_02 — no failure is silent.
                            continue;
                        }
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

                // ══════════════════════════════════════════════════════════════════════════
                // GAMEPLAY LOUDNESS (PEAKSAFE_01 / MEMELEVEL_02). Measured over the exported
                // range, ONLY to place the peak tamer's threshold and to match memes to the
                // gameplay. The gameplay level itself is never changed (LOUDSTD_REMOVED_01).
                // ══════════════════════════════════════════════════════════════════════════
                double? gameplayLufs = null;
                bool memesNeedLevel = memes.Any(mm => mm.MeasuredLufs.HasValue) || cornerMemes.Any(mm => mm.MeasuredLufs.HasValue);
                if (sourceHasAudio && (AutoSpikeFlattening || memesNeedLevel))
                {
                    EmitProgress(1, "Analyzing Audio Peaks", 0);
                    var gameReading = await AudioLoudnessProbe.MeasureAsync(
                        _ffmpegPath, InputPath, cancellationToken,
                        segmentStartSec: Math.Max(0, StartTimeMs / 1000.0),
                        segmentDurationSec: Math.Max(0, (EndTimeMs - StartTimeMs) / 1000.0)).ConfigureAwait(false);
                    gameplayLufs = gameReading?.IntegratedLufs ?? GameplayLoudnessLufs;
                    CoreLogger.Info("Audio", gameplayLufs.HasValue
                        ? $"Gameplay loudness {gameplayLufs:F2} LUFS ({(gameReading != null ? "measured over the export range" : "upload-time fallback")})."
                        : "Gameplay loudness unknown: no peak tamer, memes left as recorded.");
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
                    coreFilters.Add($"[0:v]setpts='PTS/{SpeedFactor.ToString("F4", CultureInfo.InvariantCulture)}',{cfrFilter}[v_stabilized]");
                    vStabilizedPad = "[v_stabilized]";

                    if (sourceHasAudio)
                    {
                        // TEMPO_01 — the ONE tempo policy (AudioTempoFilterBuilder). AVSYNC_01: "" at
                        // 1.0x; the leading comma lives in the segment so it disappears with it.
                        string baseAtempoSegment = AudioTempoFilterBuilder.Segment(SpeedFactor, leadingComma: true);
                        coreFilters.Add($"{baseAudioLabel}aresample=48000:async=1,asetpts=PTS{baseAtempoSegment}[a_prepared_base]");
                        aPreparedPad = "[a_prepared_base]";
                    }
                    else
                    {
                        coreFilters.Add($"anullsrc=r=48000:cl=stereo,atrim=duration={gDur.ToString("F4", CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS[a_prepared_base]");
                        aPreparedPad = "[a_prepared_base]";
                    }
                }

                // COLOR_01 — convert to SDR BT.709 TV range straight after the timing stage (speed,
                // cuts, CFR: colour-agnostic) and BEFORE fades, the intro still, the portrait crop, the
                // HUD overlays and memes. Those are all SDR artwork, and would be tone-mapped too if
                // they were composited first. Both the main branch and the HUD branch are converted.
                if (colorChain != null)
                {
                    coreFilters.Add($"{vStabilizedPad}{colorChain}[v_color]");
                    vStabilizedPad = "[v_color]";
                    if (!string.IsNullOrEmpty(gVHud))
                    {
                        coreFilters.Add($"{gVHud}{colorChain}[gVHud_color]");
                        gVHud = "[gVHud_color]";
                    }
                }

                // PEAKSAFE_01 — the peak tamer acts on the GAMEPLAY bus, before the voice-over and the
                // music are mixed in, with its threshold placed relative to the gameplay's own
                // measured loudness. Off by the user switch, or when the level is unknown.
                if (sourceHasAudio && AutoSpikeFlattening && PeakSafety.TamerFilter(gameplayLufs) is string tamer)
                {
                    coreFilters.Add($"{aPreparedPad}{tamer}[a_tamed]");
                    aPreparedPad = "[a_tamed]";
                    CoreLogger.Info("Audio",
                        $"PEAK TAMER ON: gameplay peaks above {PeakSafety.TamerThresholdDb(gameplayLufs!.Value):F1} dBFS " +
                        $"({PeakSafety.TamerHeadroomLu:F0} LU over its {gameplayLufs:F1} LUFS average) are compressed {PeakSafety.TamerRatio:F0}:1.");
                }
                else
                {
                    CoreLogger.Info("Audio", $"Peak tamer OFF ({(AutoSpikeFlattening ? "gameplay loudness unknown" : "switched off")}).");
                }

                var effectiveTakes = GetEffectiveVoiceOverTakes();

                // ══════════════════════════════════════════════════════════════════════════
                // VOPROT_01 — ONE PULSE ENVELOPE, TWO CONSUMERS.
                //
                // 1.0 across every take (with a 0.3s ramp either side), 0 everywhere else. It used
                // to be built INSIDE the game-ducking branch, so the music bed had no way to see
                // it — which is why the old "Auto-Duck Game Audio" could leave the voice buried
                // under music it never touched. Declared here so both protections can read it, and
                // left null when neither is on so the graph is byte-for-byte unchanged.
                // ══════════════════════════════════════════════════════════════════════════
                string? voicePulseExpr = null;

                if (effectiveTakes.Count > 0)
                {
                    if (VoiceOverDuckAudio || VoiceOverProtectFromMusic)
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
                            conditions.Add($"clip({sRamp},0,1)*clip({eRamp},0,1)");
                        }

                        if (conditions.Count > 0)
                        {
                            voicePulseExpr = $"clip({string.Join("+", conditions)},0,1)";
                        }
                    }

                    if (sourceHasAudio && VoiceOverDuckAudio && voicePulseExpr != null)
                    {
                        // VOPROT_01 / VOGATE_01 — DUCK **AND** CARVE the game, across the takes ONLY.
                        // The 85% dip alone still leaves the game competing in the 1-4 kHz band that
                        // carries speech, so it is paired with a 2.5 kHz scoop. The scoop used to be a
                        // static equalizer on the whole bus for the whole video; it is now gated by the
                        // same pulse (AudioFilterChain.GatedVoiceProtection).
                        coreFilters.AddRange(AudioFilterChain.GatedVoiceProtection(aPreparedPad, voicePulseExpr, "vpg", "[a_ducked]"));
                        aPreparedPad = "[a_ducked]";
                        CoreLogger.Info("Audio",
                            $"Voice protection: game bus ducked 85% and carved at 2.5 kHz during {effectiveTakes.Count} take(s) only.");
                    }

                    int voBaseIndex = 1 + musicTracks.Count + (introDurationSec > 0.001 ? 1 : 0) + (textPngPath != null ? 1 : 0) + memes.Count + cornerMemes.Count;

                    // PEAKSAFE_01 — no per-take limiter any more: it was a second auto-level alimiter
                    // on top of the final one (+3 dB on the voice). The always-on safety limiter on
                    // the final mix covers the takes.

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
                            
                            coreFilters.Add($"[{inputIdx}:a]aresample=48000:async=1,{trimFilter}adelay={delayMs}|{delayMs}{delayLabel}");
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
                        musicLeadFadeIn: MusicLeadFadeIn,
                        musicTailFadeOut: MusicTailFadeOut,
                        voiceOverLabel: effectiveTakes.Count > 0 ? (effectiveTakes.Count > 1 ? "[vo_mixed_all]" : "[vo_delayed_0]") : null,
                        voiceProtectMusicPulse: VoiceOverProtectFromMusic ? voicePulseExpr : null);

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

                    coreFilters.Add($"[{introInputIndex}:v]trim=duration={Math.Max(0.2, introDurationSec + 0.1).ToString("F4", CultureInfo.InvariantCulture)}," +
                                   $"setpts=PTS-STARTPTS,select='eq(n\\,0)',setsar=1," +
                                   $"loop=loop={loopFrames}:size=1:start=0," +
                                   $"fps={targetFps}:round=near," +
                                   $"trim=duration={introDurationSec.ToString("F4", CultureInfo.InvariantCulture)},setpts=PTS-STARTPTS[v_intro_same_frame]");
                    coreFilters.Add($"{vStabilizedPad}setsar=1[v_main_after_intro]");
                    coreFilters.Add("[v_intro_same_frame][v_main_after_intro]concat=n=2:v=1:a=0[v_with_intro]");
                    vStabilizedPad = "[v_with_intro]";

                    if (!string.IsNullOrEmpty(gVHud))
                    {
                        coreFilters.Add($"[{introInputIndex}:v]trim=duration={Math.Max(0.2, introDurationSec + 0.1).ToString("F4", CultureInfo.InvariantCulture)}," +
                                       $"setpts=PTS-STARTPTS,select='eq(n\\,0)',setsar=1," +
                                       $"loop=loop={loopFrames}:size=1:start=0," +
                                       $"fps={targetFps}:round=near," +
                                       $"trim=duration={introDurationSec.ToString("F4", CultureInfo.InvariantCulture)},setpts=PTS-STARTPTS[v_intro_hud_same_frame]");
                        coreFilters.Add($"{gVHud}setsar=1[gVHud_after_intro]");
                        coreFilters.Add("[v_intro_hud_same_frame][gVHud_after_intro]concat=n=2:v=1:a=0[gVHud_with_intro]");
                        gVHud = "[gVHud_with_intro]";
                    }
                }

                if (introDurationSec > 0)
                {
                    coreFilters.Add($"anullsrc=r=48000:cl=stereo," +
                                   $"atrim=duration={introDurationSec.ToString("F4", CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS[a_intro_silence]");
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
                        finalMainPad, finalHudPad, mobileCoords, ShowTeammates, ShowSpectating,
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
                               $"setpts=N/({targetFps})/TB,format=yuv420p{(cornerMemes.Count > 0 ? "[v_render_base]" : "[v_render_out]")}");

                string vOutputFinal = "[v_render_out]";
                string aOutputFinal = currentALabel;

                // ══════════════════════════════════════════════════════════════════════════
                // MEMEMODE_01 / FFM-MEMECORNER — corner overlays, drawn over the RENDERED stream
                // (intro + body, final canvas) BEFORE any full-screen meme is spliced in, so they ride
                // with the gameplay and a cutaway pauses them with it. Zero added duration: the
                // rendered stream is the main input of every overlay and the first input of the amix.
                // ══════════════════════════════════════════════════════════════════════════
                if (cornerMemes.Count > 0)
                {
                    int frameW, frameH;
                    if (IsMobileFormat) { frameW = CoordinateConstants.PortraitW; frameH = CoordinateConstants.PortraitH; }
                    else
                    {
                        var (srcW, srcH) = CoordinateMath.GetResolutionInts(OriginalResolution);
                        frameW = Math.Max(2, srcW - (srcW % 2));
                        frameH = Math.Max(2, srcH - (srcH % 2));
                    }
                    var cornerInputs = new List<CornerMemeInput>();
                    foreach (var cm in cornerMemes)
                    {
                        double absSourceSec = StartTimeMs / 1000.0 + cm.AtSourceSecRelative;
                        double bodyOutSec = granularTimeMapper != null
                            ? granularTimeMapper(absSourceSec)
                            : (absSourceSec - actualExtractStartMs / 1000.0) / SpeedFactor;
                        if (cm.AtSourceSecRelative <= 0.0005) bodyOutSec = padStartHumanSec;
                        double start = Math.Round((introDurationSec + bodyOutSec) * fpsValue) / fpsValue;
                        var vis = MemePlacement.VisibleInterval(start, cm.DurationSec, renderDurationSec);
                        if (vis is not { } v)
                        {
                            CoreLogger.Warn("Meme", $"Corner meme '{Path.GetFileName(cm.FilePath)}' falls after the end of the video and was skipped.");
                            continue;
                        }
                        double gain = cm.HasAudio ? MemeLoudness.GainFor(cm.MeasuredLufs, gameplayLufs) : 0;
                        cornerInputs.Add(new CornerMemeInput(cm.InputIndex, cm.IsImage, cm.HasAudio, v.StartSec, v.EndSec,
                            cm.Corner, cm.Size, cm.PlaySound, gain));
                        CoreLogger.Info("Meme",
                            $"Corner meme '{Path.GetFileName(cm.FilePath)}' {cm.Corner}/{cm.Size} over {v.StartSec:F3}-{v.EndSec:F3}s " +
                            $"of the rendered video (sound {(cm.HasAudio ? $"{gain:+0.00;-0.00} dB" : "off")}); the video length is unchanged.");
                    }
                    var overlaid = CornerMemeOverlayGraph.Build("[v_render_base]", aOutputFinal, cornerInputs, frameW, frameH, targetFps, "pw_");
                    coreFilters.AddRange(overlaid.Filters);
                    coreFilters.Add($"{overlaid.VideoLabel}format=yuv420p[v_render_out]");
                    aOutputFinal = overlaid.AudioLabel;
                }

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

                        // MEMELEVEL_02 — as loud as the gameplay it interrupts; no per-meme limiter
                        // (the safety limiter on the final mix covers it).
                        double memeGainDb = MemeLoudness.GainFor(m.MeasuredLufs, gameplayLufs);
                        memeAudio += MemeLoudness.Chain(memeGainDb);
                        if (m.HasAudio)
                        {
                            CoreLogger.Info("Audio",
                                $"MEME LEVEL: '{Path.GetFileName(m.FilePath)}' {(m.MeasuredLufs is double ml ? $"{ml:F2}" : "unmeasured")} LUFS " +
                                $"-> gameplay {(gameplayLufs is double gl ? $"{gl:F2}" : "unmeasured")} LUFS = {memeGainDb:+0.00;-0.00} dB.");
                            // SPLICE_03 — the meme butt-joins gameplay on both sides: de-click.
                            memeAudio += MemeLoudness.SpliceFade(m.DurationSec);
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
                            // SPLICE_03 — every piece borders a meme: 8 ms de-click fades.
                            double pieceStart = i == 0 ? 0 : cuts[i - 1];
                            double pieceEnd = i == pieceCount - 1 ? renderDurationSec : cuts[i];
                            coreFilters.Add($"[a_cut{i}_src]atrim=start={startArg}{endArg},asetpts=PTS-STARTPTS{MemeLoudness.SpliceFade(pieceEnd - pieceStart)}[a_cut{i}]");
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
                        musicLeadFadeIn: MusicLeadFadeIn,
                        musicTailFadeOut: MusicTailFadeOut,
                        voiceOverLabel: effectiveTakes.Count > 0 ? (effectiveTakes.Count > 1 ? "[vo_mixed_all]" : "[vo_delayed_0]") : null,
                        voiceProtectMusicPulse: VoiceOverProtectFromMusic ? voicePulseExpr : null);

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


                // PEAKSAFE_01 — the always-on true-peak safety limiter. Not behind any switch: with
                // the old switch off NOTHING limited the summed bus and a hot mix clipped the AAC.
                coreFilters.Add($"{aOutputFinal}{PeakSafety.SafetyLimiterFilter()}[a_flattened]");
                aOutputFinal = "[a_flattened]";
                CoreLogger.Info("Audio", $"SAFETY LIMITER: true-peak ceiling {PeakSafety.SafetyCeilingDbtp:F1} dBTP (4x oversampled, no auto-level).");

                string filterScript = string.Join(";", coreFilters.Where(p => !string.IsNullOrEmpty(p)));
                CoreLogger.Info("FFmpeg", $"Filter Script Content:\n{filterScript}");
                string filterScriptPath = Path.Combine(tempJobDir, "filter_complex.txt");
                await File.WriteAllTextAsync(filterScriptPath, filterScript, cancellationToken);

                string corePath = Path.Combine(tempJobDir, "core.mp4");

                // TIMINGTAG_02 — frame-exact timing stamped into the delivered file (intro + fades),
                // alongside the SCRAPER_01 v1 seconds key that older Merger builds read.
                // A meme at an edge moves the fade away from the file's edge, so that fade is written
                // as UNKNOWN (null) rather than as a wrong position.
                {
                    if (!ExportTiming.TryParseFps(targetFps, out int tagFpsNum, out int tagFpsDen)) { tagFpsNum = 60; tagFpsDen = 1; }
                    double tagFps = (double)tagFpsNum / tagFpsDen;
                    bool memeAtHead = memes.Any(m => m.CutOutputSec <= introDurationSec + 1e-6);
                    bool memeAtTail = memes.Any(m => m.CutOutputSec >= renderDurationSec - 1e-6);
                    _exportTiming = new ExportTiming(
                        tagFpsNum, tagFpsDen,
                        introInputIndex.HasValue ? ExportTiming.SecToFrames(introDurationSec, tagFps) : 0,
                        memeAtHead ? null : ExportTiming.SecToFrames(padStartHumanSec, tagFps),
                        memeAtTail ? null : ExportTiming.SecToFrames(padEndHumanSec, tagFps));
                    CoreLogger.Info("FFmpeg", $"Timing tag: {ExportTimingTag.Format(_exportTiming)}");
                }

                // ══════════════════════════════════════════════════════════════════════════
                // PREVIEWMIX_01 — AUDIO-ONLY RENDER FOR THE LIVE PREVIEW. The graph above is the
                // export's graph, byte for byte; only its audio half is run.
                // ══════════════════════════════════════════════════════════════════════════
                if (!string.IsNullOrEmpty(AudioPreviewOutputPath))
                {
                    string audioGraph = AudioGraphPruner.Prune(filterScript, aOutputFinal);
                    string audioGraphPath = Path.Combine(tempJobDir, "audio_preview_graph.txt");
                    await File.WriteAllTextAsync(audioGraphPath, audioGraph, cancellationToken);
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    var psi = new ProcessStartInfo
                    {
                        FileName = _ffmpegPath,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        RedirectStandardInput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    var a = new List<string> { "-y", "-hide_banner", "-nostdin",
                        "-ss", (actualExtractStartMs / 1000.0).ToString("F3", ci),
                        "-t", ((actualExtractEndMs - actualExtractStartMs) / 1000.0).ToString("F3", ci),
                        "-i", InputPath };
                    foreach (var track in musicTracks) a.AddRange(["-i", track.Path]);
                    if (introInputIndex.HasValue)
                    {
                        // Input slot only — the intro is a still frame; its audio is synthesised silence.
                        a.AddRange(["-t", "0.2", "-i", InputPath]);
                    }
                    if (textPngPath != null) a.AddRange(["-i", textPngPath]);
                    foreach (var m in memes)
                    {
                        if (m.IsImage) a.AddRange(["-i", m.FilePath]);
                        else a.AddRange(["-i", m.FilePath]);
                    }
                    foreach (var m in cornerMemes)   // MEMEMODE_01 — same input slots as the export
                        a.AddRange(CornerMemeOverlayGraph.InputArgs(m.FilePath, m.IsImage, m.DurationSec, targetFps));
                    foreach (var voTake in GetEffectiveVoiceOverTakes()) a.AddRange(["-i", voTake.Path]);
                    a.AddRange(["-filter_complex_script", audioGraphPath, "-map", aOutputFinal,
                                "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "2", AudioPreviewOutputPath!]);
                    foreach (var arg in a) psi.ArgumentList.Add(arg);

                    var (exit, _, err) = await AsyncProcessRunner.RunAsync(psi, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
                    if (exit != 0 || !File.Exists(AudioPreviewOutputPath))
                    {
                        string tail = string.Join(" | ", err.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(4));
                        CoreLogger.Warn("PreviewMix", $"Audio preview render failed ({exit}): {tail}");
                        EmitFinished(false, "Audio preview render failed.");
                        return;
                    }
                    AudioPreviewMap = new AudioPreviewMap(
                        introDurationSec, padStartHumanSec,
                        memes.Select(m => (m.CutOutputSec, m.DurationSec)).ToList());
                    CoreLogger.Info("PreviewMix", $"Audio preview rendered: {Path.GetFileName(AudioPreviewOutputPath)} " +
                        $"(intro {introDurationSec:F3}s, pad {padStartHumanSec:F3}s, {memes.Count} meme(s)).");
                    EmitFinished(true, AudioPreviewOutputPath!);
                    return;
                }

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
                bool gpuFiltersDisabled = false;
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

                        var videoPipeline = ExportVideoPipeline.Create(currentEncoder, filterScript, !gpuFiltersDisabled);
                        videoPipeline.ApplyCodecFlags(codecArgs);
                        // IO_OPT: Pass short filter graphs inline to avoid the disk write.
                        // Falls back to -filter_complex_script for long graphs.
                        bool useInlineFilter = videoPipeline.FilterGraph.Length < 8000;
                        if (!useInlineFilter)
                            await File.WriteAllTextAsync(filterScriptPath, videoPipeline.FilterGraph, cancellationToken);
                        LastVideoPipeline = videoPipeline.Description;
                        CoreLogger.Info("FFmpeg", LastVideoPipeline);

                        var ffmpegArgs = new List<string>
                        {
                            "-y", "-hide_banner", "-progress", "pipe:1"
                        };

                        ffmpegArgs.AddRange(videoPipeline.DeviceFlags);
                        var decodeFlags = videoPipeline.DecodeFlags;

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

                        // MEMEMODE_01 — corner overlay inputs follow the full-screen memes.
                        foreach (var m in cornerMemes)
                            ffmpegArgs.AddRange(CornerMemeOverlayGraph.InputArgs(m.FilePath, m.IsImage, m.DurationSec, targetFps));

                        var voTakes = GetEffectiveVoiceOverTakes();
                        foreach (var voTake in voTakes)
                            ffmpegArgs.AddRange(["-i", voTake.Path]);

                        ffmpegArgs.AddRange(useInlineFilter
                            ? ["-filter_complex", videoPipeline.FilterGraph]
                            : ["-filter_complex_script", filterScriptPath]);
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
                                ..IntroTag.OutputArgs(_exportTiming), corePath]);   // TIMINGTAG_02
                        }
                        else
                        {
                            ffmpegArgs.AddRange(codecArgs);
                            ffmpegArgs.AddRange(["-c:a", "aac", "-b:a", $"{audioKbps}k",
                                "-t", totalOutputDurationSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                                ..IntroTag.OutputArgs(_exportTiming), corePath]);   // TIMINGTAG_02
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
                        CoreLogger.Info("FFmpeg", $"Starting encode: decode={videoPipeline.DecoderDescription}, encode={EncoderManager.DescribeEncoder(currentEncoder)}, mode={rcLabel}, route={routeLabel}, attempt={attemptNum}.");
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
                                string inlineCmd = cmdLine.Replace(needle, $"-filter_complex \"{videoPipeline.FilterGraph}\"");
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
                            CoreLogger.Warn("FFmpeg", $"Could not build the inlined command for the log: {ex.Message}");
                        }

                        var psi = new ProcessStartInfo
                        {
                            FileName = _ffmpegPath,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            // FFMPEGSTOP_01 — redirected on purpose: this is the ONLY channel for
                            // FFmpeg's interactive quit command ('q'), which the cooperative
                            // shutdown ladder writes to ask the encoder to finalize its container
                            // (moov atom, indexes) and exit on its own. Without it a cancel can
                            // only ever be a mid-write kill, and the output is unplayable.
                            // ⚠️ No argument in ffmpegArgs may be -nostdin, or the quit is ignored.
                            RedirectStandardInput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        };

                        foreach (string arg in ffmpegArgs)
                        {
                            psi.ArgumentList.Add(arg);
                        }

                        attemptCounter++;
                        var attemptId = new ExportAttemptIdentity
                        {
                            AttemptIndex = attemptCounter,
                            Operation = graphIsMaster ? "MasterPass" : (twoPass ? "TwoPassEncode" : "SinglePassEncode"),
                            Encoder = currentEncoder,
                            Description = $"Attempt #{attemptCounter}: {currentEncoder} ({(videoPipeline.UsesGpuFrames ? "GPU resident" : "Software filters")})"
                        };

                        var collector = new FfmpegDiagnosticCollector();
                        Process proc;
                        try
                        {
                            proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start process: {_ffmpegPath}");
                        }
                        catch (Exception startEx)
                        {
                            var startFailure = FfmpegErrorClassifier.Classify(
                            ExportStage.Encoding,
                            attemptId,
                            processExitCode: null,
                            processStartException: startEx,
                            isTimeout: false,
                            isCancellation: _isCanceled || cancellationToken.IsCancellationRequested,
                            collector: null,
                            earlierAttempts: earlierAttempts);

                            earlierAttempts.Add(startFailure);
                            LastFailure = startFailure;
                            FailureDetail = startFailure.FormatDiagnosticReport();
                            lastError = startFailure.Summary;
                            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(startEx);   // FAULTTIER_02 — no failure is silent.
                            return false;
                        }

                        SetCurrentProcess(proc);   // PROCGATE_01

                        bool disposedByGuard = false;
                        try
                        {

                        try { ChildProcessTracker.AddProcess(proc); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

                        // FFMPEGSTOP_01 — cooperative stop on external cancellation: 'q' quit
                        // command -> 1500 ms grace -> Kill(entireProcessTree) -> 2000 ms exit
                        // confirmation. Single-flight and off-thread, so cancelling can never hang
                        // the caller, and FFmpeg gets the chance to write its moov atom instead of
                        // leaving a headerless mdat behind.
                        using var reg = cancellationToken.Register(
                            () => BeginCooperativeShutdown(proc, "FFmpeg", attemptQuitCommand: true));

                        // ⚠️ The reader loops below deliberately take NO cancellation token: they
                        // drain to EOF once the cooperatively stopped process closes its pipes, so
                        // they always complete before the Process object is disposed.
                        var progressTask = Task.Run(async () =>
                        {
                            using var reader = proc.StandardOutput;
                            while (!reader.EndOfStream)
                            {
                                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                                if (line == null) break;
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
                        });

                        var stderrTask = Task.Run(async () =>
                        {
                            using var reader = proc.StandardError;
                            while (!reader.EndOfStream)
                            {
                                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                                if (line == null) break;
                                collector.AddStderrLine(line);
                            }
                        });

                        try { await proc.WaitForExitAsync(cancellationToken); }
                        catch (OperationCanceledException swallowed5)
                        {
                            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed5);   // FAULTTIER_02 — no failure is silent.
                        }

                        try { await Task.WhenAll(progressTask, stderrTask); }
                        catch (OperationCanceledException swallowed3)
                        {
                            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed3);   // FAULTTIER_02 — no failure is silent.
                        }
                        catch (Exception ex) { CoreLogger.Fail("FFmpeg", $"Reader task error: {ex.Message}"); }

                        // FFMPEGSTOP_01 — let an in-flight ladder finish before reading the exit
                        // code, so the code comes from a process that has actually finished
                        // finalizing its output rather than one still writing its trailer.
                        // Bounded by the ladder itself; returns immediately when none is running.
                        await AwaitActiveShutdownAsync();

                        int exitCode = ReadExitCodeSafely(proc, "FFmpeg");
                        TakeCurrentProcess();      // PROCGATE_01 — claim + clear atomically
                        proc.Dispose();
                        disposedByGuard = true;

                        if (_isCanceled || cancellationToken.IsCancellationRequested)
                        {
                            CoreLogger.Info("FFmpeg", "Encode stopped because the user cancelled.");
                            lastError = CancelledMessage;
                            FailureDetail = null;
                            LastFailure = null;
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
                                    LastFailure = null;
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
                            UsedGpuVideoProcessing = videoPipeline.UsesGpuFrames;
                            FailureDetail = null;
                            LastFailure = null;
                            earlierAttempts.Clear();
                            twoPassProducedResult = twoPass;

                            bool cpuFallback = currentEncoder == "libx264" && useCuda && !encoderMgr.ForcedCpu;
                            string passLabel = twoPass ? (twoPassFastRoute ? " route=two-pass(fast)" : " route=two-pass(slow)") : "";
                            CoreLogger.Info("FFmpeg",
                                $"PIPELINE RESULT: decode={videoPipeline.DecoderDescription} " +
                                $"encode={EncoderManager.DescribeEncoder(currentEncoder)} speed={LastReportedSpeed}{passLabel}" +
                                (cpuFallback
                                    ? " — WARNING: this is the CPU fallback, the requested hardware encoder FAILED."
                                    : string.Empty));

                            var diagLines = collector.GetDiagnosticLines();
                            if (diagLines.Count > 0)
                                CoreLogger.Debug("FFmpeg", $"FFmpeg diagnostics ({diagLines.Count} lines):\n{string.Join("\n", diagLines)}");
                            return true;
                        }

                        var attemptFailure = FfmpegErrorClassifier.Classify(
                            ExportStage.Encoding,
                            attemptId,
                            processExitCode: exitCode,
                            processStartException: null,
                            isTimeout: false,
                            isCancellation: false,
                            collector: collector,
                            earlierAttempts: earlierAttempts);

                        earlierAttempts.Add(attemptFailure);
                        LastFailure = attemptFailure;
                        FailureDetail = attemptFailure.FormatDiagnosticReport();
                        lastError = attemptFailure.Summary;
                        CoreLogger.Fail("FFmpeg", $"Attempt #{attemptCounter} failed (exit {exitCode}): {attemptFailure.Summary}");
                        var failureDiags = collector.GetDiagnosticLines();
                        if (failureDiags.Count > 0)
                            CoreLogger.Fail("FFmpeg", $"FFmpeg diagnostic lines:\n{string.Join("\n", failureDiags)}");

                        if (videoPipeline.UsesGpuFrames && !_isCanceled && !cancellationToken.IsCancellationRequested)
                        {
                            gpuFiltersDisabled = true;
                            CoreLogger.Info("FFmpeg", "GPU video processing failed. Retrying the original effects with the same hardware encoder.");
                            continue;
                        }

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
                                TakeCurrentProcess();   // PROCGATE_01
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
                        LastFailure = null;
                        CoreLogger.Info("Process", "Export cancelled by the user.");
                        EmitFinished(false, CancelledMessage);
                        return;
                    }

                    if (LastFailure == null)
                    {
                        LastFailure = FfmpegErrorClassifier.Classify(
                            ExportStage.Encoding,
                            new ExportAttemptIdentity { AttemptIndex = attemptCounter > 0 ? attemptCounter : 1, Operation = "SinglePassEncode", Encoder = lastAttemptedEncoder },
                            processExitCode: null,
                            processStartException: null,
                            isTimeout: false,
                            isCancellation: false,
                            explicitLines: [lastError],
                            earlierAttempts: earlierAttempts);
                        FailureDetail = LastFailure.FormatDiagnosticReport();
                    }

                    EmitFinished(false, LastFailure.Summary);
                    return;
                }

                if (targetMb.HasValue && !sizeTargetMet)
                {
                    double actualMb = finalActualSize / 1024.0 / 1024.0;
                    CoreLogger.Fail("FFmpeg", $"Export size target not met after retries. Target={targetMb.Value:F2} MB, actual={actualMb:F2} MB. Delivering closest render.");
                }

                EmitProgress(2, "Finalizing", 97);
                string outputDir = ResolveOutputDirectory();
                string finalOutput = ResolveOutputPath(outputDir, OutputBaseName);

                try
                {
                    // IO_OPT: File.Move is nearly instant on the same volume (atomic rename).
                    // This avoids writing the entire finished video a second time.
                    // Falls back to File.Copy + File.Delete for cross-volume moves.
                    try
                    {
                        File.Move(corePath, finalOutput, overwrite: true);
                    }
                    catch (IOException)
                    {
                        // Cross-volume move — fall back to copy + delete.
                        File.Copy(corePath, finalOutput, true);
                        try { File.Delete(corePath); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
                    }
                }
                catch (Exception copyEx)
                {
                    // OUTPATH_01 — the name was RESERVED with a zero-byte placeholder. The move that
                    // was meant to fill it failed, so remove the placeholder rather than leaving an
                    // empty "FreeVideoStudio-N.mp4" in the user's folder that looks like a broken
                    // export. Only ever deletes a file that is still zero bytes.
                    try
                    {
                        if (File.Exists(finalOutput) && new FileInfo(finalOutput).Length == 0)
                        {
                            File.Delete(finalOutput);
                        }
                    }
                    catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

                    string? rescued = TryRescueFinishedRender(corePath);

                    CoreLogger.Fail("Output",
                        $"The finished render could not be saved to the destination: {copyEx.Message}");
                    CoreLogger.Debug("Output", $"Destination was: {finalOutput}");

                    LastFailure = new ExportFailure
                    {
                        Category = ExportFailureCategory.DestinationError,
                        Stage = ExportStage.Finalizing,
                        Attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "SaveOutput", Description = "Copy render to destination" },
                        Summary = "Your video finished encoding, but it could not be saved to the destination folder.",
                        SpecificCause = copyEx.Message,
                        DiagnosticLines = [copyEx.ToString()]
                    };

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
                            // PROCGATE_02 — the thumbnail grab was the one child process in this
                            // pipeline that was never published to _currentProcess, never registered
                            // against the cancellation token and never handed to ChildProcessTracker.
                            // Cancel() therefore could not reach it at all, and if the app exited
                            // during the grab the ffmpeg child was not covered by the kill-on-close
                            // Job Object either. All three are now wired, exactly like every other
                            // child process here.
                            SetCurrentProcess(p);
                            try { ChildProcessTracker.AddProcess(p); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
                            // FFMPEGSTOP_01 — cooperativeGraceMs: 0 ON PURPOSE. This child has no
                            // redirected stdin and writes a single -vframes 1 still, so there is
                            // no container to finalize and nothing for a 'q' to save; spending the
                            // 1500 ms grace here would only slow a cancel down. What the ladder
                            // DOES add over the bare Kill is a bounded exit CONFIRMATION, so
                            // teardown can no longer proceed while the child is still dying.
                            using var thumbReg = cancellationToken.Register(() =>
                                GracefulProcessTerminator.Terminate(
                                    p, "Thumbnail", attemptQuitCommand: false, cooperativeGraceMs: 0));

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
                            catch (OperationCanceledException swallowed8)
                            {
                                global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed8);   // FAULTTIER_02 — no failure is silent.
                            }

                            string thumbErr = string.Empty;
                            try { thumbErr = await thumbErrTask; } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

                            int thumbExit = ReadExitCodeSafely(p, "Thumbnail", graceMs: 2000);
                            TakeCurrentProcess();   // PROCGATE_02 — the grab is done; stop advertising it.
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
                // FFMPEGSTOP_01 — WAIT FOR THE STOP LADDER BEFORE DELETING THE SCRATCH DIRECTORY.
                // On Windows a file with a live handle cannot be deleted. Terminating FFmpeg is
                // asynchronous, so a cancel that reached here while the child was still dying made
                // this Directory.Delete fail, the failure was swallowed, and the two-pass scratch
                // master — which can be GIGABYTES — was left in the temp root for good. Awaiting
                // the ladder (bounded; a no-op when none is running) closes that leak.
                await AwaitActiveShutdownAsync();
                try { if (Directory.Exists(tempJobDir)) Directory.Delete(tempJobDir, true); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
            }
        }
        catch (OperationCanceledException)
        {
            _isCanceled = true;
            FailureDetail = null;
            LastFailure = null;
            CoreLogger.Info("Process", "Export cancelled by the user.");
            EmitFinished(false, CancelledMessage);
        }
        catch (Exception ex)
        {
            if (_isCanceled || cancellationToken.IsCancellationRequested)
            {
                FailureDetail = null;
                LastFailure = null;
                CoreLogger.Info("Process", $"Export cancelled by the user (during: {ex.Message}).");
                EmitFinished(false, CancelledMessage);
                return;
            }

            CoreLogger.Fail("Process", $"Pipeline failed with exception: {ex.Message}");
            CoreLogger.Debug("Process", $"Pipeline failed with exception detail: {ex}");
            LastFailure = FfmpegErrorClassifier.ClassifyException(ex, ExportStage.Encoding,
                new ExportAttemptIdentity { AttemptIndex = attemptCounter > 0 ? attemptCounter : 1, Operation = "Pipeline", Description = "Export pipeline execution" },
                earlierAttempts);
            FailureDetail = LastFailure.FormatDiagnosticReport();
            EmitFinished(false, LastFailure.Summary);
        }
        finally
        {
            // CANCELREG_01 — the registration replaced the old `using var`, so it is released here.
            // Dispose on a default(CancellationTokenRegistration) is a documented no-op, and on a
            // registration whose source has already been disposed it is also safe.
            try { cancelMirror.Dispose(); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

            // CANCELREG_01 — last-resort completion guarantee. Every path above already calls
            // EmitFinished, and EmitFinished is idempotent (_finishEmitted), so this fires ONLY if
            // some future edit introduces a silent return. Without it, such a path wedges the
            // caller's await forever with no error on screen.
            if (!_finishEmitted)
            {
                CoreLogger.Fail("Process", "Export pipeline ended without reporting a result — reporting failure so the UI cannot hang.");
                EmitFinished(false, _isCanceled ? CancelledMessage : "The export stopped unexpectedly.");
            }
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
    /// <summary>
    /// RESCUE_01 — delegates to <see cref="RescuedOutputPath.TryRescue"/>.
    ///
    /// WHAT WAS WRONG: this method picked its destination with a <c>while (File.Exists(...))</c>
    /// scan and then called the TWO-argument <c>File.Move</c>, which throws when the destination
    /// exists. Two rescues inside the same one-second stamp — cancel-then-restart, or Main App and
    /// Merger together — meant the second one threw, was swallowed, returned null, and the
    /// finished render (the ONLY copy, which is why it is being rescued at all) was abandoned. The
    /// index loop also had no ceiling, so an unwritable temp root spun forever inside a failure
    /// handler. <see cref="RescuedOutputPath"/> carries the OUTPATH_01 reservation primitive this
    /// path always should have used, in ONE copy shared with <c>MergerWorker</c>.
    ///
    /// ⚠️ The prefix and the log tag stay distinct from the Merger's — they are how the user and
    /// the crash digest tell the two tools' rescued files apart.
    /// </summary>
    private string? TryRescueFinishedRender(string corePath)
        => RescuedOutputPath.TryRescue(
            corePath,
            _paths.TempDirectory,
            OutputFileNaming.MainRecoveredPrefix,
            "Output",
            "Could not preserve the finished render");

    /// <summary>
    /// OUTPATH_01 — RESERVES the output name instead of merely testing it.
    ///
    /// This was a File.Exists scan: check, then return the name, then write to it much later. Two
    /// pipelines running at once — which is exactly what a cancel-then-restart used to produce —
    /// both saw the same index free and both returned it, so the second File.Move(..., overwrite:
    /// true) silently destroyed the first render. The window is small but the loss is total and
    /// silent, which is the worst combination.
    ///
    /// FileMode.CreateNew with FileShare.None is an ATOMIC create-or-fail at the filesystem level:
    /// exactly one caller can win a given name, in this process or any other. The zero-byte
    /// placeholder it leaves is overwritten by the pipeline's own File.Move/File.Copy, which
    /// already pass overwrite: true.
    ///
    /// The iteration ceiling exists so a directory that cannot be written to (permissions, a full
    /// disk, an offline network share) fails loudly after a bounded number of attempts instead of
    /// spinning forever inside the export.
    /// </summary>
    private static string ResolveOutputPath(string outputDir, string? baseName)
    {
        Directory.CreateDirectory(outputDir);
        string safeBase = OutputFileNaming.Sanitize(baseName, OutputFileNaming.MainDefaultBaseName);

        const int MaxIndex = 10000;
        for (int idx = 1; idx <= MaxIndex; idx++)
        {
            string path = Path.Combine(outputDir, OutputFileNaming.NumberedFileName(safeBase, idx));
            try
            {
                using var reserve = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return path;
            }
            catch (IOException swallowed4)
            {
                global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed4);   // FAULTTIER_02 — no failure is silent.
            }
        }

        throw new IOException(
            $"Could not reserve an output filename in '{outputDir}' after {MaxIndex} attempts. " +
            "The folder may be full, read-only, or unavailable.");
    }

    /// <summary>
    /// ISSUE_15 — renders an argument list into a human-readable, copy-pasteable command for
    /// the DEBUG log only. Never used to launch a process.
    ///
    /// PIPEDEDUP_01 — this was a PRIVATE COPY of <see cref="ProcessArgs.FormatForLog"/>, byte for
    /// byte. Every other consumer in this assembly — AudioLoudnessProbe, MediaProber,
    /// WaveformGenerator, HudAutoDetector — already called the shared one; this file alone kept
    /// its own. The escaping rules it implements are a correctness contract (ISSUE_07: a path
    /// containing a quote, or ending in a backslash, breaks a hand-assembled command line), and a
    /// second copy is a second place for that contract to drift. Delegating keeps the private
    /// name so none of the six call sites in this file change.
    /// </summary>
    private static string FormatForLog(IEnumerable<string> args) => ProcessArgs.FormatForLog(args);

    private void EmitFinished(bool success, string message)
        => _lifetime.EmitFinished(Finished, success, message);


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
        pass2.AddRange(["-c:a", "copy", ..IntroTag.OutputArgs(_exportTiming), finalPath]);   // TIMINGTAG_02

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
            // FFMPEGSTOP_01 — see the main encode: this is the channel for the 'q' quit command.
            // The two-pass tail is the pass that actually writes the deliverable file, so a
            // mid-write kill here is exactly the case that produces an unplayable export.
            RedirectStandardInput = true,
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

        SetCurrentProcess(proc);   // PROCGATE_01

        try
        {
            try { ChildProcessTracker.AddProcess(proc); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

            // FFMPEGSTOP_01 — cooperative stop, single-flight, off-thread. See the main encode.
            using var reg = cancellationToken.Register(
                () => BeginCooperativeShutdown(proc, "FFmpeg", attemptQuitCommand: true));

            var progressTask = Task.Run(async () =>
            {
                using var reader = proc.StandardOutput;
                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
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
            });

            var collector = new FfmpegDiagnosticCollector();
            var stderrTask = Task.Run(async () =>
            {
                using var reader = proc.StandardError;
                while (!reader.EndOfStream)
                {
                    string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    collector.AddStderrLine(line);
                }
            });

            // ══════════════════════════════════════════════════════════════════════════════════
            // PIPEDRAIN_01 — DRAIN BEFORE DISPOSE, ON EVERY PATH INCLUDING CANCELLATION.
            //
            // This used to be `catch (OperationCanceledException) { return false; }`. That return
            // jumped straight to the finally below, which disposes `proc` — closing the
            // StandardOutput / StandardError pipe handles while progressTask and stderrTask were
            // still suspended inside ReadLineAsync on them. Both tasks faulted with nobody awaiting
            // them (unobserved), their `using var reader` double-disposed the StreamReader, and the
            // anonymous pipe pair survived until finalization. The main encode loop above already
            // does this correctly with Task.WhenAll; this path was simply missed.
            //
            // The 5s ceiling exists so a child that survived the kill cannot hold teardown open.
            // ══════════════════════════════════════════════════════════════════════════════════
            bool tailCanceled = false;
            try { await proc.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException swallowed6)
            {
                tailCanceled = true;
                global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed6);   // FAULTTIER_02 — no failure is silent.
            }

            try { await Task.WhenAll(progressTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException swallowed9)
            {
                global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed9);   // FAULTTIER_02 — no failure is silent.
            }
            catch (TimeoutException) { CoreLogger.Warn("FFmpeg", "Two-pass reader drain timed out after 5s; continuing teardown."); }
            catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

            // FFMPEGSTOP_01 — a cancelled tail must still let the ladder finish, or the process is
            // abandoned mid-finalize and the scratch master/passlog are left behind with a live
            // writer still holding them.
            await AwaitActiveShutdownAsync();

            if (tailCanceled) return false;

            int exitCode = ReadExitCodeSafely(proc, "FFmpeg");
            if (exitCode == 0) return true;

            var passFailure = FfmpegErrorClassifier.Classify(
                ExportStage.TwoPassTail,
                new ExportAttemptIdentity { AttemptIndex = 1, Operation = phaseTitle, Description = $"Two-pass tail ({phaseTitle})" },
                processExitCode: exitCode,
                processStartException: null,
                isTimeout: false,
                isCancellation: _isCanceled || cancellationToken.IsCancellationRequested,
                collector: collector);

            LastFailure = passFailure;
            FailureDetail = passFailure.FormatDiagnosticReport();
            CoreLogger.Fail("FFmpeg", $"Two-pass {phaseTitle} failed (exit {exitCode}): {passFailure.Summary}");
            var passDiags = collector.GetDiagnosticLines();
            if (passDiags.Count > 0)
                CoreLogger.Fail("FFmpeg", $"FFmpeg diagnostic lines:\n{string.Join("\n", passDiags)}");
            return false;
        }
        finally
        {
            TakeCurrentProcess();      // PROCGATE_01
            proc.Dispose();
        }
    }

    /// <summary>T01 — see <see cref="TwoPassEncoding.Cleanup"/>.</summary>
    private static void CleanupTwoPassArtifacts(string masterPath, string passLogPrefix)
        => TwoPassEncoding.Cleanup(masterPath, passLogPrefix);

    /// <summary>
    /// PROBE_01 — measures the clip's NVENC encoding complexity with a 5-second
    /// reference-quality sample, replacing blind bitrate guessing for size-locked
    /// exports. See the PROBE_01 block in RunAsync for the calibration data and
    /// the margin model that consumes the returned bits-per-second figure.
    ///
    /// The slice is taken from the MIDDLE of the extract range (`-ss mid -t 5`),
    /// decoded in SOFTWARE (no `-hwaccel*` flags — the zero-copy guardrail) and
    /// encoded through h264_nvenc at reference CQ 20 with no rate cap, writing to
    /// the null muxer so nothing ever touches the disk. Speed segments collapse
    /// naturally: the `fps` filter duplicates/drops frames exactly like the export
    /// graph does, so duplicated frames cost ~nothing and the measurement already
    /// reflects the rendered timeline's complexity.
    ///
    /// Returns null (non-fatally) when NVENC is unavailable, the output cannot be
    /// parsed, or the export is cancelled — callers keep the budget-derived rate.
    /// </summary>
    private async Task<double?> RunNvencComplexityProbeAsync(
        double extractStartMs, double extractEndMs, string outputResolution, string targetFps,
        CancellationToken cancellationToken)
    {
        double extractSec = (extractEndMs - extractStartMs) / 1000.0;
        if (extractSec < 2.0 || string.IsNullOrEmpty(InputPath) || !File.Exists(InputPath)) return null;

        double probeSec = Math.Min(5.0, extractSec);
        double probeStartSec = extractStartMs / 1000.0 + (extractSec - probeSec) / 2.0;

        // "1080x1920" / "1920x1080" -> scale geometry. Unparsable input falls back
        // to FHD landscape rather than aborting the export's probing attempt.
        int width = 1920, height = 1080;
        string[] dims = (outputResolution ?? "").Split('x');
        if (dims.Length == 2 &&
            int.TryParse(dims[0], out int w) && int.TryParse(dims[1], out int h) &&
            w > 0 && h > 0)
        {
            width = w;
            height = h;
        }

        // Frame-count -> seconds needs the real fps; accept "60" or "60000/1001".
        double fps = 60.0;
        string fpsExpr = string.IsNullOrWhiteSpace(targetFps) ? "60" : targetFps.Trim();
        int slash = fpsExpr.IndexOf('/');
        string fpsNum = slash > 0 ? fpsExpr[..slash] : fpsExpr;
        string fpsDen = slash > 0 ? fpsExpr[(slash + 1)..] : "1";
        if (double.TryParse(fpsNum, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double num) &&
            double.TryParse(fpsDen, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double den) &&
            den > 0 && num / den > 1.0 && num / den <= 240.0)
        {
            fps = num / den;
        }

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var args = new List<string>
        {
            "-y", "-hide_banner",
            "-ss", Math.Max(0, probeStartSec).ToString("F3", ci),
            "-t", probeSec.ToString("F3", ci),
            "-i", InputPath,
            "-vf", $"fps={targetFps},scale={width}:{height},setsar=1,format=yuv420p",
            // Reference-quality appetite measurement — deliberately NOT the export's
            // CBR flag set: no rate cap, single pass, so the bits the content WANTS
            // at CQ 20 are the bits it GETS.
            "-c:v", "h264_nvenc",
            "-preset", "p7", "-tune", "hq",
            "-rc", "vbr", "-cq", "20", "-multipass", "disabled",
            "-spatial-aq", "1", "-temporal-aq", "1",
            "-an", "-sn", "-dn",
            "-f", "null", "-"
        };

        LastComplexityProbeCommandLine = $"{_ffmpegPath} {FormatForLog(args)}";
        CoreLogger.Info("FFmpeg",
            $"PROBE_01: sampling the middle {probeSec:F1}s at {width}x{height}@{fps:F0} through h264_nvenc CQ 20 " +
            "(software decode, null muxer, no hwaccel flags).");
        CoreLogger.Debug("FFmpeg", $"PROBE_01 command: {LastComplexityProbeCommandLine}");
        EmitProgress(1, "Probing Clip Complexity (NVENC)", 0);

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process == null) return null;

        try { ChildProcessTracker.AddProcess(process); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

        // FFMPEGSTOP_01 — PROBE_01 writes to the NULL MUXER ("-f null -"): no file is produced,
        // so there is nothing a cooperative quit could protect. Grace 0 keeps cancellation as
        // immediate as it was, while still confirming the tree actually died.
        using var probeKill = cancellationToken.Register(() =>
            GracefulProcessTerminator.Terminate(
                process, "FFmpeg", attemptQuitCommand: false, cooperativeGraceMs: 0));

        var lastLines = new Queue<string>(100);
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
                    int percent = probeSec > 0 ? (int)Math.Clamp(ts.TotalSeconds / probeSec * 100, 0, 100) : 0;
                    EmitProgress(1, "Probing Clip Complexity (NVENC)",
                        (int)Math.Round(percent / 100.0 * AnalysisBandMax));
                }
            }
        }

        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException swallowed2)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed2);   // FAULTTIER_02 — no failure is silent.
            return null;
        }

        if (_isCanceled || cancellationToken.IsCancellationRequested) return null;

        if (process.ExitCode != 0)
        {
            CoreLogger.Info("FFmpeg",
                $"PROBE_01 complexity probe failed (exit {process.ExitCode}) — keeping the budget-derived bitrate. " +
                "The size-retry loop remains the safety net.");
            return null;
        }

        string stdErr = string.Join("\n", lastLines);

        // "video: 15850KiB" from the null-muxer summary = exact video payload of the slice.
        double kib = 0;
        var videoMatches = System.Text.RegularExpressions.Regex.Matches(stdErr, @"video:\s*([\d.]+)\s*[kK]i?B");
        if (videoMatches.Count == 0 ||
            !double.TryParse(videoMatches[^1].Groups[1].Value, System.Globalization.NumberStyles.Float, ci, out kib))
        {
            CoreLogger.Info("FFmpeg", "PROBE_01 could not parse the probe's video size from ffmpeg output.");
            return null;
        }

        // Denominator from the FINAL frame count, not from `time=` (which trails the
        // last frame's PTS): 300 frames at 60 fps is exactly 5.000s of content.
        long frames = 0;
        var frameMatches = System.Text.RegularExpressions.Regex.Matches(stdErr, @"frame=\s*(\d+)");
        if (frameMatches.Count > 0)
        {
            long.TryParse(frameMatches[^1].Groups[1].Value, out frames);
        }

        double measuredSec = frames / fps;
        if (kib <= 0 || frames <= 0 || measuredSec < 0.5)
        {
            CoreLogger.Info("FFmpeg",
                $"PROBE_01 measurement was degenerate ({kib:F0} KiB, {frames} frames) — keeping the budget-derived bitrate.");
            return null;
        }

        double appetiteBps = kib * 1024.0 * 8.0 / measuredSec;
        return appetiteBps > 0 ? appetiteBps : null;
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
    public void Dispose() => _lifetime.DisposeJob();
}

public record VoiceOverTake(string Path, double StartSec);
