// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

public class MergerWorker : IDisposable
{
    private readonly ApplicationPaths _paths;
    /// <summary>
    /// PIPELIFE_01 / PIPELIFE_02 — process slot, cancel flag, single-flight finish and the
    /// teardown ladder, shared with <see cref="ProcessWorker"/>.
    ///
    /// <para>
    /// ⚠️ This replaces a PLAIN, UNSYNCHRONISED <c>Process? _currentProcess</c> field. PROCGATE_01
    /// fixed a use-after-dispose race in the sibling pipeline — the field read twice, once to
    /// null-test and once to act on, letting the worker thread run <c>_currentProcess = null;
    /// proc.Dispose();</c> in between so teardown called Kill() on a disposed Process — and this
    /// file never received that fix. Its <c>Cancel()</c> passed the raw field to the ladder and
    /// its <c>Dispose()</c> did exactly the two-read pattern. Routing through the gate closes it.
    /// </para>
    /// </summary>
    private readonly FfmpegJobLifetime _lifetime = new("Merger", "FFmpeg MERGE");
    /// <summary>PIPELIFE_01 — alias over the shared lifetime's single-flight flag.</summary>
    private bool _finishEmitted => _lifetime.FinishEmitted;
    private string _ffmpegPath;
    private string _ffprobePath;

    /// <summary>
    /// Single-flight gate for the cooperative shutdown ladder. Cancel(), the cancellation-token
    /// registration inside <see cref="ExecuteFFmpegAsync"/>, and <see cref="Dispose"/> can all
    /// race; the gate guarantees exactly ONE ladder ('q' quit command → grace period → hard
    /// kill → exit confirmation) ever runs per FFmpeg process.
    ///
    /// PIPEDEDUP_01 — the gate's three fields and their three methods used to be written out here
    /// AND, separately, in <c>ProcessWorker</c>. Two copies of one mechanism is how
    /// <c>ReadExitCodeSafely</c> silently diverged between these two files (FFMPEGSTOP_01) and how
    /// <c>TryRescueFinishedRender</c> shipped the same race twice (RESCUE_01). One copy now lives
    /// in <see cref="CooperativeShutdownGate"/>; the members below are thin delegations kept at
    /// their original signatures so no call site in this file changes.
    /// </summary>
    // PIPELIFE_01 — the shutdown ladder now lives in FfmpegJobLifetime.

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
    public bool AutoSpikeFlattening { get; set; } = true;

    /// <summary>
    /// G03 / ISSUE 2 — which chip should encode. Mirrors <c>ProcessWorker.HardwareStrategy</c>.
    ///
    /// ⚠️ CALLERS MUST SET THIS FROM <see cref="ExportEncoderStrategy.Resolve"/>, exactly like the
    /// Main App does. That is what makes the two applications reach the SAME answer on the same
    /// machine: Settings override → the suite-wide boot-scan result the Main App published →
    /// the "unknown, re-probe" sentinel. This worker deliberately runs NO scan of its own.
    ///
    /// Accepted values: "NVIDIA" / "AMD" / "INTEL" / "CPU" / <see cref="HardwareScanner.ScanFailed"/>
    /// / "GPU" / "Auto". "GPU" is the legacy default kept only so an un-set caller still behaves
    /// sanely (best available hardware encoder); it is NOT the path the UI takes any more.
    /// </summary>
    public string HardwareStrategy { get; set; } = "GPU";

    /// <summary>
    /// G09 — last `speed=` value FFmpeg reported for the current attempt (e.g. "3.4x").
    /// "?" until the first progress line arrives.
    /// </summary>
    public string LastReportedSpeed { get; private set; } = "?";
    public string LastVideoPipeline { get; private set; } = "";
    public bool UsedGpuVideoProcessing { get; private set; }

    /// <summary>
    /// IDEA_8 — optional per-clip in/out points, in SOURCE seconds, index-aligned with
    /// <see cref="InputFiles"/>. A null list, a short list, or an entry that covers the whole file
    /// all mean "use the clip untrimmed", so an older caller that never sets this behaves exactly
    /// as before.
    ///
    /// Trimming is done with the `trim`/`atrim` FILTERS rather than input-level `-ss`/`-t` on
    /// purpose. The filters are frame-accurate, and — the part that matters — the identical
    /// start/end numbers are applied to the video and the audio branch of the same clip in one
    /// place, followed by `setpts=PTS-STARTPTS`/`asetpts=PTS-STARTPTS` to rebase both to zero.
    /// That is what keeps picture and sound locked together through the concat. Input seeking
    /// would have split that decision across two mechanisms and risked exactly the silent
    /// audio-drift this feature must not introduce.
    /// </summary>
    public List<ClipTrim>? ClipTrims { get; set; }

    /// <summary>
    /// A clip's in/out point in SOURCE seconds. <see cref="EndSec"/> of 0 or less means "run to the
    /// end of the file".
    /// </summary>
    public readonly record struct ClipTrim(double StartSec, double EndSec);

    /// <summary>
    /// Resolves the effective in/out window for one clip, clamped to the real file duration.
    /// Returns the untrimmed window when no usable trim is configured. A window that would be
    /// shorter than this is treated as a mistake and ignored — a zero-length clip in a concat
    /// chain produces a corrupt output rather than an error.
    /// </summary>
    private const double MinTrimmedClipSec = 0.05;

    private (double start, double end, bool trimmed) ResolveClipWindow(int index, double fileDuration)
    {
        double fullEnd = fileDuration > 0 ? fileDuration : 0;
        if (ClipTrims == null || index < 0 || index >= ClipTrims.Count || fullEnd <= 0)
            return (0, fullEnd, false);

        ClipTrim t = ClipTrims[index];
        double start = Math.Clamp(t.StartSec, 0, fullEnd);
        double end = t.EndSec <= 0 ? fullEnd : Math.Clamp(t.EndSec, 0, fullEnd);

        if (end - start < MinTrimmedClipSec)
        {
            CoreLogger.Info("Merger",
                $"  [{index + 1}] trim ignored — the requested window ({start:F2}s to {end:F2}s) is shorter than {MinTrimmedClipSec:F2}s.");
            return (0, fullEnd, false);
        }

        bool trimmed = start > 0.001 || end < fullEnd - 0.001;
        return (start, end, trimmed);
    }

    /// <summary>
    /// ISSUE_06 — raw error text (FFmpeg stderr tail / exception) behind the last failure.
    /// The UI hands this to ErrorReporter, which extracts the root-cause line for the dialog.
    /// </summary>
    public string? FailureDetail { get; private set; }

    /// <summary>
    /// Strongly typed failure model providing structured category, stage, attempt history,
    /// exit/error codes, and supporting diagnostics for ErrorReporter.
    /// </summary>
    public ExportFailure? LastFailure { get; private set; }

    public MergerWorker(ApplicationPaths? paths = null)
    {
        _paths = paths ?? ApplicationPaths.CreateDefault();
        _ffmpegPath = FortniteVideoSoftware.Core.Infrastructure.BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
        _ffprobePath = FortniteVideoSoftware.Core.Infrastructure.BinaryPathResolver.Resolve("ffprobe.exe", "backend", "binaries");
    }

    /// <summary>
    /// ISSUE_04 — the single message used for a user-initiated stop, so the UI can tell
    /// "you cancelled" apart from "something broke" without string-guessing.
    /// </summary>
    public const string CancelledMessage = "Merge cancelled.";

    /// <summary>
    /// PIPELIFE_01 — private alias over the shared lifetime's flag, so every existing read and
    /// write in this file compiles unchanged. Writing `false` is a deliberate no-op.
    /// </summary>
    private bool _isCanceled
    {
        get => _lifetime.WasCanceled;
        set { if (value) _lifetime.MarkCanceled(); }
    }


    /// <summary>True when this job ended because the user stopped it, not because it failed.</summary>
    public bool WasCanceled => _isCanceled;

    /// <summary>
    /// PIPELIFE_02 — this used to pass the raw <c>_currentProcess</c> field to the ladder, which
    /// is the exact two-read race PROCGATE_01 documents. The shared lifetime takes one consistent
    /// read and acts on that single reference.
    /// </summary>
    public void Cancel() => _lifetime.Cancel(
        stoppingMessage: "Merge cancelled by user. Stopping the FFmpeg process tree (cooperative quit, then hard kill).",
        idleMessage: "Merge worker released on shutdown (no encode was running).");

    /// <summary>
    /// Starts the bounded cooperative shutdown ladder for <paramref name="proc"/> exactly once.
    /// Single-flight: Cancel(), the token registration, and Dispose() may all race each other,
    /// but only one ladder ever runs per process. Faults are observed so a background stop can
    /// never surface as an unobserved task exception.
    /// </summary>
    private void BeginCooperativeShutdown(Process? proc)
        => _lifetime.BeginCooperativeShutdown(proc, attemptQuitCommand: true);

    /// <summary>Awaits the in-flight shutdown ladder, if any. Bounded by the ladder itself.</summary>
    private Task AwaitActiveShutdownAsync() => _lifetime.AwaitActiveShutdownAsync();

    /// <summary>
    /// ISSUE_04 — reads a child process's exit code without ever throwing.
    ///
    /// Callers reach here after a CANCELLABLE wait whose OperationCanceledException is
    /// deliberately swallowed, so the process may still be dying (Kill is asynchronous) and
    /// `ExitCode` would throw InvalidOperationException. Give it a short grace period, then fall
    /// back to a sentinel rather than letting that exception masquerade as a pipeline crash.
    /// </summary>
    /// ⚠️ attemptQuitCommand STAYS false here. The caller has already attempted the cooperative
    /// stop, so a second 'q' would only burn the grace budget. See CooperativeShutdownGate.
    private static int ReadExitCodeSafely(Process proc, string logTag, int graceMs = 5000)
        => CooperativeShutdownGate.ReadExitCodeSafely(proc, logTag, graceMs, attemptQuitCommand: false);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        UsedGpuVideoProcessing = false;
        using var cancelMirror = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => _isCanceled = true)
            : default;

        // Hoisted so the cancellation handlers below can clean the job's partial outputs up
        // before the final cancelled status is emitted, even on the exception paths.
        string? tempJobDir = null;

        try
        {
            if (InputFiles.Count == 0)
            {
                LastFailure = new ExportFailure
                {
                    Category = ExportFailureCategory.CorruptInput,
                    Stage = ExportStage.Preflight,
                    Attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "InputValidation", Description = "Input files validation" },
                    Summary = "No input files were provided for merging.",
                    SpecificCause = "InputFiles list is empty.",
                    DiagnosticLines = ["No input files provided."]
                };
                FailureDetail = LastFailure.FormatDiagnosticReport();
                EmitFinished(false, LastFailure.Summary);
                return;
            }

            string jobId = Guid.NewGuid().ToString("N")[..8];
            tempJobDir = Path.Combine(_paths.TempDirectory, $"fvs_merger_{jobId}");
            Directory.CreateDirectory(tempJobDir);

            try
            {
                CoreLogger.Info("Merger", $"Merging {InputFiles.Count} file(s):");
                double totalDuration = 0;
                var fileDurations = new double[InputFiles.Count];
                var fileHasAudio = new bool[InputFiles.Count];
                var fileResolutions = new (int width, int height)[InputFiles.Count];
                var clipWindows = new (double start, double end, bool trimmed)[InputFiles.Count];
                var clipDurations = new double[InputFiles.Count];
                double peakSourceVideoBitrateKbps = 0;
                double durationWeightedBitrateKbps = 0;
                for (int fi = 0; fi < InputFiles.Count; fi++)
                {
                    var prober = new MediaProber(_ffprobePath, InputFiles[fi]);
                    double dur = await prober.GetDurationAsync();
                    bool hasAudio = await prober.HasAudioAsync();
                    fileResolutions[fi] = await prober.GetResolutionAsync();
                    fileDurations[fi] = dur;
                    fileHasAudio[fi] = hasAudio;

                    var (winStart, winEnd, winTrimmed) = ResolveClipWindow(fi, dur);
                    double effectiveDur = winEnd - winStart;
                    clipWindows[fi] = (winStart, winEnd, winTrimmed);
                    clipDurations[fi] = effectiveDur;

                    CoreLogger.Info("Merger", winTrimmed
                        ? $"  [{fi + 1}] {Path.GetFileName(InputFiles[fi])} — {dur:F2}s, trimmed to {winStart:F2}s-{winEnd:F2}s ({effectiveDur:F2}s), audio={hasAudio}"
                        : $"  [{fi + 1}] {Path.GetFileName(InputFiles[fi])} — {dur:F2}s, audio={hasAudio}");
                    CoreLogger.Info("Merger", $"  [{fi + 1}] full path: {InputFiles[fi]}");
                    totalDuration += effectiveDur;

                    try
                    {
                        double srcVbit = await prober.GetVideoBitrateKbpsAsync();
                        if (srcVbit > 0)
                        {
                            peakSourceVideoBitrateKbps = Math.Max(peakSourceVideoBitrateKbps, srcVbit);
                            durationWeightedBitrateKbps += srcVbit * Math.Max(0.1, effectiveDur);
                        }
                    }
                    catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
                }

                if (totalDuration == 0) totalDuration = 10.0;
                double averageSourceVideoBitrateKbps = totalDuration > 0
                    ? durationWeightedBitrateKbps / totalDuration
                    : peakSourceVideoBitrateKbps;
                CoreLogger.Info("Merger", $"Total combined duration: {totalDuration:F2}s, peak src video bitrate: {peakSourceVideoBitrateKbps:F0} kbps, avg: {averageSourceVideoBitrateKbps:F0} kbps");

                double speedFactor = SpeedFactor > 0 ? SpeedFactor : 1.0;
                double outputDuration = totalDuration / speedFactor;

                {
                    long estimatedBytes = DiskSpaceGuard.EstimateOutputBytes(
                        outputDuration,
                        (int)Math.Round(Math.Max(averageSourceVideoBitrateKbps, peakSourceVideoBitrateKbps)),
                        null);
                    string plannedOutputDir = !string.IsNullOrEmpty(OutputDirectory)
                        ? OutputDirectory!
                        : (KnownFolders.GetDownloads() ?? _paths.TempDirectory);

                    var space = DiskSpaceGuard.Check(_paths.TempDirectory, plannedOutputDir, estimatedBytes);
                    if (!space.Ok)
                    {
                        LastFailure = new ExportFailure
                        {
                            Category = ExportFailureCategory.DiskFull,
                            Stage = ExportStage.Preflight,
                            Attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "Preflight", Description = "Disk space check" },
                            Summary = space.Message ?? "Not enough free disk space.",
                            SpecificCause = space.Message,
                            DiagnosticLines = [space.Message ?? "Not enough free disk space."]
                        };
                        FailureDetail = LastFailure.FormatDiagnosticReport();
                        EmitFinished(false, LastFailure.Summary);
                        return;
                    }
                }

                var filters = new List<string>();
                var cmdArgs = new List<string> { "-y", "-hide_banner", "-progress", "pipe:1" };
                var effectiveMusicTracks = await BuildEffectiveMusicTracksAsync(outputDuration);

                int musicInputIndex = InputFiles.Count;

                List<string> BuildInputArgs(ExportVideoPipeline pipeline)
                {
                    var decodeFlags = pipeline.DecodeFlags;
                    var args = new List<string>(pipeline.DeviceFlags);
                    for (int i = 0; i < InputFiles.Count; i++)
                    {
                        args.AddRange(decodeFlags);
                        args.AddRange(["-i", InputFiles[i]]);
                    }
                    foreach (var musicTrack in effectiveMusicTracks)
                    {
                        args.AddRange(["-i", musicTrack.Path]);
                    }
                    return args;
                }

                string vOutputLabel = "[v_concat]";
                string aOutputLabel = "[a_concat]";

                string avInputs = "";
                for (int i = 0; i < InputFiles.Count; i++)
                {
                    string scaleFilter = OutputRatio == TargetAspectRatio.Portrait9x16
                        ? $"scale=1080:1920:force_original_aspect_ratio=increase:flags=lanczos,crop=1080:1920"
                        : $"scale=1920:1080:force_original_aspect_ratio=decrease:flags=lanczos,pad=1920:1080:(ow-iw)/2:(oh-ih)/2";

                    int canvasW = OutputRatio == TargetAspectRatio.Portrait9x16 ? 1080 : 1920;
                    int canvasH = OutputRatio == TargetAspectRatio.Portrait9x16 ? 1920 : 1080;
                    var resolution = fileResolutions[i];
                    if (resolution.width > 0 && resolution.height > 0 &&
                        (long)resolution.width * canvasH == (long)resolution.height * canvasW)
                    {
                        // Matching aspect ratios need neither padding nor crop. This exact
                        // scale has a CUDA equivalent; mixed aspect ratios keep their effects.
                        scaleFilter = $"scale={canvasW}:{canvasH}:flags=lanczos";
                    }

                    var win = clipWindows[i];
                    string vTrim = win.trimmed
                        ? $"trim=start={win.start.ToString("F3", CultureInfo.InvariantCulture)}:end={win.end.ToString("F3", CultureInfo.InvariantCulture)},"
                        : "";
                    string aTrim = win.trimmed
                        ? $"atrim=start={win.start.ToString("F3", CultureInfo.InvariantCulture)}:end={win.end.ToString("F3", CultureInfo.InvariantCulture)},"
                        : "";

                    filters.Add($"[{i}:v]{vTrim}setpts=PTS-STARTPTS,{scaleFilter},setsar=1,setpts=PTS/{speedFactor.ToString("F4", CultureInfo.InvariantCulture)},fps=60:start_time=0:round=near[v{i}]");
                    double clipDur = clipDurations[i] > 0 ? clipDurations[i] : totalDuration;
                    if (fileHasAudio[i])
                    {
                        double atempoSpeed = speedFactor;
                        var atempoFilters = new List<string>();
                        while (atempoSpeed > 2.0) { atempoFilters.Add("atempo=2.0"); atempoSpeed /= 2.0; }
                        while (atempoSpeed < 0.5) { atempoFilters.Add("atempo=0.5"); atempoSpeed /= 0.5; }
                        atempoFilters.Add($"atempo={atempoSpeed.ToString("F4", CultureInfo.InvariantCulture)}");
                        filters.Add($"[{i}:a]{aTrim}asetpts=PTS-STARTPTS,aformat=sample_fmts=fltp:channel_layouts=stereo:sample_rates=48000,{string.Join(",", atempoFilters)}[a{i}]");
                    }
                    else
                    {
                        filters.Add($"anullsrc=r=48000:cl=stereo,atrim=duration={(clipDur / speedFactor).ToString("F3", CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS[a{i}]");
                    }
                    avInputs += $"[v{i}][a{i}]";
                }
                if (InputFiles.Count > 1)
                {
                    filters.Add($"{avInputs}concat=n={InputFiles.Count}:v=1:a=1{vOutputLabel}{aOutputLabel}");

                    filters.Add($"{aOutputLabel}aresample=48000:async=1:min_comp=0.01:first_pts=0[a_concat_sync]");
                    aOutputLabel = "[a_concat_sync]";
                }
                else
                {
                    vOutputLabel = "[v0]";
                    aOutputLabel = "[a0]";
                }

                string finalAudioLabel = aOutputLabel;

                // Apply the Wizard's gameplay level even when its music is unavailable.
                // No Wizard config means unity gain, independent of preview volume.
                {
                    MusicConfig ??= new JsonObject();

                    if (MusicConfig != null && !MusicConfig.ContainsKey("timeline_start_sec"))
                    {
                        MusicConfig["timeline_start_sec"] = 0.0;
                        MusicConfig["timeline_end_sec"] = outputDuration;
                    }

                    var (duckChains, finalDuckingLabel) = AudioFilterChain.Build(
                        musicConfig: MusicConfig,
                        videoStartTime: 0,
                        videoEndTime: outputDuration,
                        speedFactor: 1.0,
                        disableFades: false,
                        vfadeInD: 0,
                        audioFilterCmd: null,
                        sampleRate: 48000,
                        musicTracks: effectiveMusicTracks,
                        musicStartIndex: musicInputIndex,
                        totalProjectDuration: outputDuration,
                        mainAudioLabel: aOutputLabel,
                        volumeNormalizeDb: 0.0
                    );

                    filters.AddRange(duckChains);
                    finalAudioLabel = finalDuckingLabel;
                }

                if (AutoSpikeFlattening)
                {
                    filters.Add($"{finalAudioLabel}alimiter=limit=-1.5dB:level_in=1:level_out=1[a_flattened]");
                    finalAudioLabel = "[a_flattened]";
                }

                string filterScript = string.Join(";", filters.Where(p => !string.IsNullOrEmpty(p)));
                string filterScriptPath = Path.Combine(tempJobDir, "filter_complex.txt");
                await File.WriteAllTextAsync(filterScriptPath, filterScript, cancellationToken);
                CoreLogger.Info("FFmpeg MERGE", $"Filter Script Content:\n{filterScript}");

                string mergeStrategy = string.IsNullOrWhiteSpace(HardwareStrategy) ||
                                       HardwareStrategy.Equals("Auto", StringComparison.OrdinalIgnoreCase)
                    ? "GPU"
                    : HardwareStrategy;
                var encoderMgr = await Task.Run(() => new EncoderManager(mergeStrategy, _ffmpegPath), cancellationToken).ConfigureAwait(false);
                if (encoderMgr.EncoderPreflightError != null)
                {
                    LastFailure = new ExportFailure
                    {
                        Category = ExportFailureCategory.MissingEncoder,
                        Stage = ExportStage.Preflight,
                        Attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "Preflight", Description = "Encoder discovery preflight" },
                        Summary = encoderMgr.EncoderPreflightError,
                        SpecificCause = encoderMgr.EncoderPreflightError,
                        DiagnosticLines = [encoderMgr.EncoderPreflightError]
                    };
                    FailureDetail = LastFailure.FormatDiagnosticReport();
                    EmitFinished(false, LastFailure.Summary);
                    return;
                }
                string currentEncoder = encoderMgr.GetInitialEncoder(!encoderMgr.ForcedCpu);

                int cqValue = OutputFileSize.MergerConstantQuality(QualityPercent);
                int qualityLevel = QualityPercent >= 100 ? 3 : (QualityPercent >= 50 ? 2 : 1);

                int? losslessBitrateKbps = null;
                int losslessMaxrateKbps = 0;
                if (QualityPercent >= 100 && averageSourceVideoBitrateKbps > 0)
                {
                    losslessBitrateKbps = OutputFileSize.MergerTargetKbps(averageSourceVideoBitrateKbps);
                    losslessMaxrateKbps = Math.Max(losslessBitrateKbps.Value, (int)Math.Min(EncoderManager.MaxBitrateKbps, peakSourceVideoBitrateKbps));
                    CoreLogger.Info("Merger", $"Lossless target bitrate {losslessBitrateKbps} kbps (avg), maxrate {losslessMaxrateKbps} kbps (peak) — output size will track the combined source size.");
                }

                string corePath = Path.Combine(tempJobDir, "merged_output.mp4");
                string? successOutputPath = null;
                string lastErrorMsg = "FFmpeg render failed.";

                string twoPassMasterPath = Path.Combine(tempJobDir, "twopass_master.mp4");
                string twoPassLogPrefix = Path.Combine(tempJobDir, "twopass_stats");
                bool twoPassDisabled = false;

                bool twoPassFastRoute = DiskSpaceGuard.HasRoomFor(
                    tempJobDir, DiskSpaceGuard.EstimateTwoPassMasterBytes(outputDuration));

                TwoPassEncoding.Cleanup(twoPassMasterPath, twoPassLogPrefix);

                bool gpuFiltersDisabled = false;
                var earlierAttempts = new List<ExportFailure>();
                int attemptCounter = 0;
                while (true)
                {
                    attemptCounter++;
                    var (codecArgs, rcLabel) = encoderMgr.GetCodecFlags(currentEncoder, losslessBitrateKbps, outputDuration, "60", qualityLevel, false);
                    var videoPipeline = ExportVideoPipeline.Create(currentEncoder, filterScript, !gpuFiltersDisabled);
                    videoPipeline.ApplyCodecFlags(codecArgs);
                    // IO_OPT: Pass short filter graphs inline to avoid the disk write.
                    bool useInlineFilter = videoPipeline.FilterGraph.Length < 8000;
                    if (!useInlineFilter)
                        await File.WriteAllTextAsync(filterScriptPath, videoPipeline.FilterGraph, cancellationToken);
                    LastVideoPipeline = videoPipeline.Description;
                    CoreLogger.Info("FFmpeg", LastVideoPipeline);

                    if (QualityPercent >= 100 && losslessMaxrateKbps > 0)
                    {
                        for (int ci = 0; ci < codecArgs.Count - 1; ci++)
                        {
                            if (codecArgs[ci] == "-maxrate")
                                codecArgs[ci + 1] = $"{losslessMaxrateKbps}k";
                            else if (codecArgs[ci] == "-bufsize")
                                codecArgs[ci + 1] = $"{Math.Min(EncoderManager.MaxBitrateKbps, losslessMaxrateKbps * 2)}k";
                        }
                    }

                    if (QualityPercent < 100)
                    {
                        for (int ci = 0; ci < codecArgs.Count - 1; ci++)
                        {
                            if (codecArgs[ci] == "-cq" || codecArgs[ci] == "-crf" || codecArgs[ci] == "-global_quality" || codecArgs[ci] == "-qp_i")
                            {
                                codecArgs[ci + 1] = cqValue.ToString();
                                if (codecArgs[ci] == "-qp_i")
                                {
                                    if (ci + 3 < codecArgs.Count && codecArgs[ci + 2] == "-qp_p") codecArgs[ci + 3] = cqValue.ToString();
                                    if (ci + 5 < codecArgs.Count && codecArgs[ci + 4] == "-qp_b") codecArgs[ci + 5] = cqValue.ToString();
                                }
                                break;
                            }
                        }
                    }

                    bool twoPass = currentEncoder == "libx264"
                                   && losslessBitrateKbps.HasValue
                                   && QualityPercent >= 100
                                   && !twoPassDisabled;

                    CoreLogger.Info("FFmpeg", $"Executing merge: decode={videoPipeline.DecoderDescription}, encode={EncoderManager.DescribeEncoder(currentEncoder)}, mode={rcLabel}, route={(twoPass ? (twoPassFastRoute ? "two-pass(fast)" : "two-pass(slow)") : "single-pass")}.");

                    bool attemptSuccess;

                    if (twoPass && twoPassFastRoute)
                    {
                        var masterArgs = new List<string>(cmdArgs);
                        masterArgs.AddRange(BuildInputArgs(videoPipeline));
                        masterArgs.AddRange(useInlineFilter
                            ? ["-filter_complex", videoPipeline.FilterGraph]
                            : ["-filter_complex_script", filterScriptPath]);
                        masterArgs.AddRange(["-map", vOutputLabel, "-map", finalAudioLabel]);
                        masterArgs.AddRange(TwoPassEncoding.MasterCodecArgs());
                        masterArgs.AddRange(["-c:a", "aac", "-b:a", "192k"]);
                        masterArgs.Add(twoPassMasterPath);

                        CoreLogger.Debug("FFmpeg", $"Two-pass master: {_ffmpegPath} {string.Join(" ", masterArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");

                        var masterAttempt = new ExportAttemptIdentity
                        {
                            AttemptIndex = attemptCounter,
                            Operation = "MasterPass",
                            Encoder = currentEncoder,
                            Description = $"Attempt #{attemptCounter}: Two-pass master ({currentEncoder})"
                        };

                        var (mSuccess, mFailure) = await ExecuteFFmpegAsync(masterArgs, outputDuration, cancellationToken, masterAttempt, earlierAttempts, 0, 60);
                        attemptSuccess = mSuccess && File.Exists(twoPassMasterPath) && new FileInfo(twoPassMasterPath).Length > 0;

                        if (attemptSuccess)
                        {
                            var (tailSuccess, tailFailure) = await RunTwoPassTailAsync(
                                twoPassMasterPath, corePath, twoPassLogPrefix,
                                losslessBitrateKbps!.Value, outputDuration, cancellationToken,
                                attemptCounter, currentEncoder, earlierAttempts);
                            attemptSuccess = tailSuccess;
                            if (!attemptSuccess && tailFailure != null)
                            {
                                earlierAttempts.Add(tailFailure);
                            }
                        }
                        else if (mFailure != null)
                        {
                            earlierAttempts.Add(mFailure);
                        }

                        TwoPassEncoding.Cleanup(twoPassMasterPath, twoPassLogPrefix);

                        if (!attemptSuccess && !cancellationToken.IsCancellationRequested)
                        {
                            twoPassDisabled = true;
                            CoreLogger.Fail("FFmpeg", "Two-pass merge failed — falling back to a single-pass merge.");
                            if (File.Exists(corePath)) { try { File.Delete(corePath); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); } }
                            continue;
                        }
                    }
                    else if (twoPass)
                    {
                        int passKbps = Math.Min(EncoderManager.MaxBitrateKbps, Math.Max(300, losslessBitrateKbps!.Value));

                        var pass1Args = new List<string>(cmdArgs);
                        pass1Args.AddRange(BuildInputArgs(videoPipeline));
                        pass1Args.AddRange(["-filter_complex_script", filterScriptPath]);
                        pass1Args.AddRange(["-map", vOutputLabel, "-map", finalAudioLabel]);
                        pass1Args.AddRange(TwoPassEncoding.PassArgs(passKbps, 1, twoPassLogPrefix));
                        pass1Args.AddRange(["-c:a", "aac", "-b:a", "192k", "-sn", "-dn", "-f", "null", "NUL"]);

                        var pass1Attempt = new ExportAttemptIdentity
                        {
                            AttemptIndex = attemptCounter,
                            Operation = "TwoPassAnalysis",
                            Encoder = currentEncoder,
                            Description = $"Attempt #{attemptCounter}: Two-pass analysis ({currentEncoder})"
                        };

                        var (p1Success, p1Failure) = await ExecuteFFmpegAsync(pass1Args, outputDuration, cancellationToken, pass1Attempt, earlierAttempts, 0, 45);
                        attemptSuccess = p1Success;

                        if (attemptSuccess)
                        {
                            var pass2Args = new List<string>(cmdArgs);
                            pass2Args.AddRange(BuildInputArgs(videoPipeline));
                            pass2Args.AddRange(["-filter_complex_script", filterScriptPath]);
                            pass2Args.AddRange(["-map", vOutputLabel, "-map", finalAudioLabel]);
                            pass2Args.AddRange(TwoPassEncoding.PassArgs(passKbps, 2, twoPassLogPrefix));
                            pass2Args.AddRange(["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);
                            pass2Args.Add(corePath);

                            var pass2Attempt = new ExportAttemptIdentity
                            {
                                AttemptIndex = attemptCounter,
                                Operation = "TwoPassEncode",
                                Encoder = currentEncoder,
                                Description = $"Attempt #{attemptCounter}: Two-pass encode ({currentEncoder})"
                            };

                            var (p2Success, p2Failure) = await ExecuteFFmpegAsync(pass2Args, outputDuration, cancellationToken, pass2Attempt, earlierAttempts, 45, 100);
                            attemptSuccess = p2Success;
                            if (!attemptSuccess && p2Failure != null)
                            {
                                earlierAttempts.Add(p2Failure);
                            }
                        }
                        else if (p1Failure != null)
                        {
                            earlierAttempts.Add(p1Failure);
                        }

                        TwoPassEncoding.Cleanup(twoPassMasterPath, twoPassLogPrefix);

                        if (!attemptSuccess && !cancellationToken.IsCancellationRequested)
                        {
                            twoPassDisabled = true;
                            CoreLogger.Fail("FFmpeg", "Two-pass merge failed — falling back to a single-pass merge.");
                            if (File.Exists(corePath)) { try { File.Delete(corePath); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); } }
                            continue;
                        }
                    }
                    else
                    {
                        var attemptArgs = new List<string>(cmdArgs);
                        attemptArgs.AddRange(BuildInputArgs(videoPipeline));
                        attemptArgs.AddRange(["-filter_complex_script", filterScriptPath]);
                        attemptArgs.AddRange(["-map", vOutputLabel, "-map", finalAudioLabel]);
                        attemptArgs.AddRange(codecArgs);
                        attemptArgs.AddRange(["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);
                        attemptArgs.Add(corePath);

                        CoreLogger.Debug("FFmpeg", $"Command: {_ffmpegPath} {string.Join(" ", attemptArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");

                        var singleAttempt = new ExportAttemptIdentity
                        {
                            AttemptIndex = attemptCounter,
                            Operation = "SinglePassEncode",
                            Encoder = currentEncoder,
                            Description = $"Attempt #{attemptCounter}: {currentEncoder} ({(videoPipeline.UsesGpuFrames ? "GPU resident" : "Software filters")})"
                        };

                        var (sSuccess, sFailure) = await ExecuteFFmpegAsync(attemptArgs, outputDuration, cancellationToken, singleAttempt, earlierAttempts);
                        attemptSuccess = sSuccess;
                        if (!attemptSuccess && sFailure != null)
                        {
                            earlierAttempts.Add(sFailure);
                        }
                    }

                    if (attemptSuccess && File.Exists(corePath) && new FileInfo(corePath).Length > 0)
                    {
                        successOutputPath = corePath;
                        UsedGpuVideoProcessing = videoPipeline.UsesGpuFrames;
                        LastFailure = null;
                        earlierAttempts.Clear();
                        FailureDetail = null;
                        string mergeRoute = twoPass ? (twoPassFastRoute ? " route=two-pass(fast)" : " route=two-pass(slow)") : "";
                        CoreLogger.Info("FFmpeg",
                            $"PIPELINE RESULT: decode={videoPipeline.DecoderDescription} " +
                            $"encode={EncoderManager.DescribeEncoder(currentEncoder)} speed={LastReportedSpeed}{mergeRoute}" +
                            (currentEncoder == "libx264" && !encoderMgr.ForcedCpu
                                ? " — WARNING: this is the CPU fallback, the requested hardware encoder FAILED."
                                : string.Empty));
                        break;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        lastErrorMsg = CancelledMessage;
                        LastFailure = new ExportFailure
                        {
                            Category = ExportFailureCategory.Cancellation,
                            Stage = ExportStage.Encoding,
                            Summary = CancelledMessage,
                            SpecificCause = "Merge was cancelled by user.",
                            EarlierAttempts = [.. earlierAttempts]
                        };
                        break;
                    }

                    if (videoPipeline.UsesGpuFrames)
                    {
                        gpuFiltersDisabled = true;
                        CoreLogger.Info("Merger", "GPU video processing failed. Retrying the original effects with the same hardware encoder.");
                        continue;
                    }

                    var fallbacks = encoderMgr.GetFallbackList(currentEncoder, allowCpu: true);
                    if (fallbacks.Count == 0)
                    {
                        lastErrorMsg = $"FFmpeg exited with encoder {currentEncoder}. Render failed.";
                        var finalFail = earlierAttempts.LastOrDefault();
                        if (finalFail != null)
                        {
                            LastFailure = finalFail with { EarlierAttempts = earlierAttempts.Take(earlierAttempts.Count - 1).ToList() };
                            FailureDetail = LastFailure.FormatDiagnosticReport();
                        }
                        else
                        {
                            LastFailure = new ExportFailure
                            {
                                Category = ExportFailureCategory.EncodingFailure,
                                Stage = ExportStage.Encoding,
                                Summary = lastErrorMsg,
                                SpecificCause = $"Encoder {currentEncoder} failed with no further fallbacks available.",
                                EarlierAttempts = [.. earlierAttempts]
                            };
                            FailureDetail = LastFailure.FormatDiagnosticReport();
                        }
                        break;
                    }

                    string failedEncoder = currentEncoder;
                    currentEncoder = fallbacks[0];
                    CoreLogger.Info("Merger", $"Encoder {failedEncoder} failed, retrying with fallback: {currentEncoder}");

                    try { if (File.Exists(corePath)) File.Delete(corePath); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
                }

                if (successOutputPath != null)
                {
                    string outputDir = !string.IsNullOrEmpty(OutputDirectory) && Directory.Exists(OutputDirectory)
                        ? OutputDirectory
                        : (FortniteVideoSoftware.Core.Infrastructure.KnownFolders.GetDownloads()
                           ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                    Directory.CreateDirectory(outputDir);
                    int idx = 1;
                    string finalOutput;
                    while (true)
                    {
                        finalOutput = Path.Combine(outputDir, $"Merged-Videos-{idx}.mp4");
                        if (!File.Exists(finalOutput)) break;
                        idx++;
                    }
                    try
                    {
                        File.Move(successOutputPath, finalOutput);
                        ProgressUpdate?.Invoke(100);
                        LastFailure = null;
                        FailureDetail = null;
                        EmitFinished(true, finalOutput);
                    }
                    catch (Exception moveEx)
                    {
                        string? rescued = TryRescueFinishedRender(successOutputPath);

                        CoreLogger.Fail("Merger",
                            $"The finished merge could not be moved to the destination: {moveEx.Message}");
                        CoreLogger.Debug("Merger", $"Destination was: {finalOutput}");

                        LastFailure = new ExportFailure
                        {
                            Category = ExportFailureCategory.DestinationError,
                            Stage = ExportStage.Finalizing,
                            Attempt = new ExportAttemptIdentity { AttemptIndex = attemptCounter, Operation = "FileMove", Description = "Move output to destination" },
                            Summary = rescued != null
                                ? "Your merged video finished, but it could not be saved to the destination folder. It has been kept safe."
                                : "Your merged video finished, but it could not be saved to the destination folder.",
                            SpecificCause = moveEx.Message,
                            DiagnosticLines = [
                                $"Destination: {finalOutput}",
                                $"Exception: {moveEx.GetType().Name}: {moveEx.Message}",
                                rescued != null ? $"Rescued to: {rescued}" : "Rescue attempt failed."
                            ]
                        };

                        if (rescued != null)
                        {
                            CoreLogger.Info("Merger", $"Finished merge preserved at: {Path.GetFileName(rescued)}");
                            CoreLogger.Debug("Merger", $"Preserved merge full path: {rescued}");
                            FailureDetail =
                                $"The merge finished but could not be written to the destination folder.{Environment.NewLine}" +
                                $"Reason: {moveEx.Message}{Environment.NewLine}" +
                                $"Your merged video has NOT been lost — it is here:{Environment.NewLine}{rescued}";
                            EmitFinished(false, LastFailure.Summary);
                        }
                        else
                        {
                            FailureDetail =
                                $"The merge finished but could not be written to the destination folder, and the " +
                                $"temporary copy could not be preserved either.{Environment.NewLine}Reason: {moveEx.Message}";
                            EmitFinished(false, LastFailure.Summary);
                        }
                    }
                }
                else if (_isCanceled || cancellationToken.IsCancellationRequested)
                {
                    FailureDetail = null;
                    CoreLogger.Info("Merger", "Merge cancelled by the user.");

                    // Remove every partial, half-written artifact BEFORE the cancelled status
                    // is emitted, so the output location is left clean (zero 0-byte or
                    // truncated video files) by the time the UI reports the stop.
                    await CleanupCancelledJobAsync(tempJobDir, corePath, twoPassMasterPath, successOutputPath);

                    LastFailure = new ExportFailure
                    {
                        Category = ExportFailureCategory.Cancellation,
                        Stage = ExportStage.Encoding,
                        Summary = CancelledMessage,
                        SpecificCause = "Merge was cancelled by user.",
                        EarlierAttempts = [.. earlierAttempts]
                    };
                    EmitFinished(false, CancelledMessage);
                }
                else
                {
                    EmitFinished(false, LastFailure?.Summary ?? lastErrorMsg);
                }
            }
            finally
            {
                // Retried delete: the just-stopped encoder's file handles can take a moment to
                // be released by the OS, and a single un-retried attempt is exactly how
                // cancelled jobs used to leave partial files behind on disk.
                await TryDeleteDirectoryWithRetryAsync(tempJobDir, "Merger").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _isCanceled = true;
            LastFailure = new ExportFailure
            {
                Category = ExportFailureCategory.Cancellation,
                Stage = ExportStage.Encoding,
                Summary = CancelledMessage,
                SpecificCause = "Merge pipeline was cancelled."
            };
            FailureDetail = null;
            CoreLogger.Info("Merger", "Merge pipeline canceled.");
            await CleanupCancelledJobAsync(tempJobDir);
            EmitFinished(false, CancelledMessage);
        }
        catch (Exception ex)
        {
            if (_isCanceled || cancellationToken.IsCancellationRequested)
            {
                LastFailure = new ExportFailure
                {
                    Category = ExportFailureCategory.Cancellation,
                    Stage = ExportStage.Encoding,
                    Summary = CancelledMessage,
                    SpecificCause = "Merge pipeline was cancelled."
                };
                FailureDetail = null;
                CoreLogger.Info("Merger", $"Merge cancelled by the user (during: {ex.Message}).");
                await CleanupCancelledJobAsync(tempJobDir);
                EmitFinished(false, CancelledMessage);
                return;
            }

            CoreLogger.Fail("Merger", $"Merge pipeline failed with exception: {ex.Message}");
            CoreLogger.Debug("Merger", $"Merge pipeline failed with exception detail: {ex}");
            LastFailure = new ExportFailure
            {
                Category = ExportFailureCategory.Unknown,
                Stage = ExportStage.Encoding,
                Summary = $"Merge pipeline error: {ex.Message}",
                SpecificCause = ex.Message,
                DiagnosticLines = [ex.ToString()]
            };
            FailureDetail = LastFailure.FormatDiagnosticReport();
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
            catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
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
                catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
            }

            if (durationFromSourceProbe && track.Offset > 0 && duration > track.Offset)
                duration -= track.Offset;

            if (duration <= 0)
                duration = musicWindowDuration;

            normalized.Add(new MusicTrack(track.Path, track.Offset, duration, track.TimelineStartDelay, track.ApplyFadeOut));
        }

        bool loopMusic = false;
        try { loopMusic = MusicConfig?["loop_music"]?.GetValue<bool>() ?? false; } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
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

    /// <summary>
    /// T01 — runs the analysis pass and the real pass against the pre-rendered scratch master.
    ///
    /// Audio is NOT re-encoded: it was finalised in the master and is stream-copied through by
    /// pass 2, so it is encoded exactly once across the whole merge and suffers no double loss.
    /// Progress occupies the 60-100 slice of the bar (the master render owned 0-60).
    /// </summary>
    private async Task<(bool Success, ExportFailure? Failure)> RunTwoPassTailAsync(
        string masterPath, string finalPath, string passLogPrefix,
        int videoBitrateKbps, double outputDuration, CancellationToken cancellationToken,
        int attemptCounter, string encoder, List<ExportFailure> earlierAttempts)
    {
        var pass1 = new List<string> { "-y", "-hide_banner", "-progress", "pipe:1", "-i", masterPath };
        pass1.AddRange(TwoPassEncoding.PassArgs(videoBitrateKbps, 1, passLogPrefix));
        pass1.AddRange(["-an", "-sn", "-dn", "-f", "null", "NUL"]);

        CoreLogger.Info("FFmpeg", "Two-pass merge: analyzing (2 of 3).");
        var pass1Attempt = new ExportAttemptIdentity
        {
            AttemptIndex = attemptCounter,
            Operation = "TwoPassAnalysis",
            Encoder = encoder,
            Description = $"Attempt #{attemptCounter}: Two-pass merge analysis (2 of 3)"
        };
        var (pass1Success, pass1Failure) = await ExecuteFFmpegAsync(pass1, outputDuration, cancellationToken, pass1Attempt, earlierAttempts, 60, 75);
        if (!pass1Success) return (false, pass1Failure);

        var pass2 = new List<string> { "-y", "-hide_banner", "-progress", "pipe:1", "-i", masterPath };
        pass2.AddRange(TwoPassEncoding.PassArgs(videoBitrateKbps, 2, passLogPrefix));
        pass2.AddRange(["-c:a", "copy", "-movflags", "+faststart", finalPath]);

        CoreLogger.Info("FFmpeg", "Two-pass merge: encoding (3 of 3).");
        var pass2Attempt = new ExportAttemptIdentity
        {
            AttemptIndex = attemptCounter,
            Operation = "TwoPassEncode",
            Encoder = encoder,
            Description = $"Attempt #{attemptCounter}: Two-pass merge encoding (3 of 3)"
        };
        var (pass2Success, pass2Failure) = await ExecuteFFmpegAsync(pass2, outputDuration, cancellationToken, pass2Attempt, earlierAttempts, 75, 100);
        if (!pass2Success) return (false, pass2Failure);

        bool exists = File.Exists(finalPath) && new FileInfo(finalPath).Length > 0;
        return (exists, null);
    }

    /// <param name="progressFloor">
    /// T01 — start of this invocation's slice of the 0-100 bar. Defaults keep every pre-existing
    /// caller on the full 0-100 range, so single-pass merges behave exactly as before.
    /// </param>
    /// <param name="progressCeiling">T01 — end of this invocation's slice of the 0-100 bar.</param>
    private async Task<(bool Success, ExportFailure? Failure)> ExecuteFFmpegAsync(
        List<string> cmdArgs,
        double totalDuration,
        CancellationToken cancellationToken,
        ExportAttemptIdentity attemptId,
        List<ExportFailure>? earlierAttempts = null,
        double progressFloor = 0.0,
        double progressCeiling = 100.0)
    {
        string cmdLine = string.Join(" ", cmdArgs.Select(a =>
            a.Length == 0 || a.Contains(' ') || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a));
        CoreLogger.Info("FFmpeg MERGE", $"Executing Final Pipeline Command:\n{_ffmpegPath} {cmdLine}");

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Redirected on purpose: it is the channel for FFmpeg's interactive quit command
            // ('q'), which the cooperative shutdown ladder writes to request a clean stop.
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string arg in cmdArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        var collector = new FfmpegDiagnosticCollector();
        Process proc;
        try
        {
            // PIPELIFE_02 — atomic take-and-clear, then dispose the one we own. The previous
            // `_currentProcess?.Dispose()` left the field pointing at a disposed Process until the
            // next line replaced it, which is a window Cancel() could land in.
            _lifetime.TakeCurrentProcess()?.Dispose();
            proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start process: {_ffmpegPath}");
            _lifetime.SetCurrentProcess(proc);
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

            FailureDetail = startFailure.FormatDiagnosticReport();
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(startEx);   // FAULTTIER_02 — no failure is silent.
            return (false, startFailure);
        }

        try { ChildProcessTracker.AddProcess(proc); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

        // Cooperative stop on external cancellation: 'q' quit command → 1500 ms grace →
        // Kill(entireProcessTree) → 2000 ms exit confirmation. Single-flight and off-thread,
        // so a cancel can never hang the UI, and FFmpeg gets the chance to finalize its
        // output instead of being killed mid-write.
        using var reg = cancellationToken.Register(() => BeginCooperativeShutdown(proc));

        var progressTask = Task.Run(async () =>
        {
            using var reader = proc.StandardOutput;
            // No cancellation token on purpose: the loop drains to EOF once the (cooperatively
            // stopped) process closes its pipes, so this reader always completes cleanly
            // before the Process object is disposed below.
            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;
                if (line.StartsWith("out_time_us="))
                {
                    if (long.TryParse(line.AsSpan(12), out long outTimeUs))
                    {
                        double currentSec = outTimeUs / 1_000_000.0;
                        if (totalDuration > 0)
                        {
                            double frac = Math.Clamp(currentSec / totalDuration, 0.0, 1.0);
                            int percent = (int)Math.Clamp(progressFloor + frac * (progressCeiling - progressFloor), 0, 100);
                            ProgressUpdate?.Invoke(percent);
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

        var stderrTask = Task.Run(async () =>
        {
            using var reader = proc.StandardError;
            while (!reader.EndOfStream)
            {
                string? line = await reader.ReadLineAsync();
                if (line != null)
                {
                    collector.AddStderrLine(line);
                }
            }
        });

        try
        {
            await proc.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException swallowed7)
        {
            // The registration above already started the cooperative ladder; await its bounded
            // completion (≤ ~3.5 s worst case) so the exit code below is read from a process
            // that is actually dead rather than one that is still dying.
            await AwaitActiveShutdownAsync();
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed7);   // FAULTTIER_02 — no failure is silent.
        }

        try
        {
            // Drain BOTH output readers before the Process object is disposed — a process
            // killed with pending pipe data used to leave zombie reader tasks and lost stderr
            // diagnostics behind. Strictly bounded by the same fallback limit as the ladder,
            // so a stuck pipe can never stall a cancelled call either.
            Task drain = Task.WhenAll(progressTask, stderrTask);
            Task completed = await Task.WhenAny(drain, Task.Delay(GracefulProcessTerminator.HardKillConfirmMs));
            if (completed == drain)
            {
                await drain;
            }
            else
            {
                CoreLogger.Fail("Merger", "FFmpeg output readers did not drain within the fallback limit; continuing shutdown.");
                _ = drain.ContinueWith(
                    static t => { if (t.IsFaulted && t.Exception != null) CoreLogger.Swallowed(t.Exception); },
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException swallowed5)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed5);   // FAULTTIER_02 — no failure is silent.
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("Merger", $"Reader task error: {ex.Message}");
        }

        int exitCode = ReadExitCodeSafely(proc, "FFmpeg MERGE");
        CoreLogger.Info("FFmpeg MERGE", $"Process exited with code {exitCode}.");

        var stderrLines = collector.GetTailLines();

        if (_isCanceled || cancellationToken.IsCancellationRequested)
        {
            CoreLogger.Info("FFmpeg MERGE", "Merge stopped because the user cancelled.");
            FailureDetail = null;
            var cancelFailure = FfmpegErrorClassifier.Classify(
                ExportStage.Encoding,
                attemptId,
                processExitCode: exitCode,
                processStartException: null,
                isTimeout: false,
                isCancellation: true,
                collector: collector,
                earlierAttempts: earlierAttempts);
            return (false, cancelFailure);
        }

        if (exitCode != 0)
        {
            var failure = FfmpegErrorClassifier.Classify(
                ExportStage.Encoding,
                attemptId,
                processExitCode: exitCode,
                processStartException: null,
                isTimeout: false,
                isCancellation: false,
                collector: collector,
                earlierAttempts: earlierAttempts);

            FailureDetail = failure.FormatDiagnosticReport();
            if (stderrLines.Count > 0)
                CoreLogger.Fail("FFmpeg MERGE", $"FFmpeg stderr (last {stderrLines.Count} lines):\n{string.Join("\n", stderrLines)}");

            return (false, failure);
        }
        else
        {
            if (stderrLines.Count > 0)
                CoreLogger.Debug("FFmpeg MERGE", $"FFmpeg stderr (last {stderrLines.Count} lines):\n{string.Join("\n", stderrLines)}");
            return (true, null);
        }
    }

    /// <summary>
    /// Removes every artifact a cancelled FFmpeg job may have left behind: the per-job temp
    /// directory plus any explicitly listed partial outputs. Runs BEFORE the cancelled status
    /// is emitted, so by the time the UI reports the stop there are no 0-byte or truncated
    /// video files left in the output location. Never throws — a cleanup failure must not
    /// mask the cancellation itself.
    /// </summary>
    private static async Task CleanupCancelledJobAsync(string? tempJobDir, params string?[] partialFiles)
    {
        foreach (string? file in partialFiles)
        {
            await TryDeleteFileWithRetryAsync(file, "Merger").ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(tempJobDir))
        {
            await TryDeleteDirectoryWithRetryAsync(tempJobDir, "Merger").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes a file, retrying briefly: a just-stopped encoder's file handles can take a
    /// moment to be released by the OS, and a single un-retried delete is exactly how
    /// cancelled jobs leave partial outputs behind on disk. Never throws.
    /// </summary>
    private static async Task TryDeleteFileWithRetryAsync(string? path, string logTag)
    {
        if (string.IsNullOrEmpty(path)) return;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
                CoreLogger.Info(logTag, $"Removed partial output '{Path.GetFileName(path)}' after cancellation.");
                return;
            }
            catch (IOException swallowed3)
            {
                global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed3);   // FAULTTIER_02 — no failure is silent.
            }
            catch (UnauthorizedAccessException swallowed4)
            {
                global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed4);   // FAULTTIER_02 — no failure is silent.
                return;
                // Permissions will not improve by retrying.
            }
            catch (System.Exception ex)
            {
                CoreLogger.Swallowed(ex);
                return;
            }

            await Task.Delay(150 * attempt).ConfigureAwait(false);
        }

        CoreLogger.Fail(logTag, $"Could not remove partial output '{path}' after 3 attempts.");
    }

    /// <summary>Directory counterpart of <see cref="TryDeleteFileWithRetryAsync"/>. Never throws.</summary>
    private static async Task TryDeleteDirectoryWithRetryAsync(string? path, string logTag)
    {
        if (string.IsNullOrEmpty(path)) return;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException swallowed6)
            {
                global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed6);   // FAULTTIER_02 — no failure is silent.
            }
            catch (UnauthorizedAccessException swallowed2)
            {
                global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed2);   // FAULTTIER_02 — no failure is silent.
                return;
                // Permissions will not improve by retrying.
            }
            catch (System.Exception ex)
            {
                CoreLogger.Swallowed(ex);
                return;
            }

            await Task.Delay(150 * attempt).ConfigureAwait(false);
        }

        CoreLogger.Fail(logTag, $"Could not remove temp job directory '{path}' after 3 attempts.");
    }

    private void EmitFinished(bool success, string message)
        => _lifetime.EmitFinished(Finished, success, message);

    /// genuinely nothing left to save.
    /// </summary>
    /// <summary>
    /// RESCUE_01 — delegates to <see cref="RescuedOutputPath.TryRescue"/>. This was a byte-for-byte
    /// copy of <c>ProcessWorker.TryRescueFinishedRender</c> carrying the same
    /// File.Exists-then-File.Move race and the same unbounded index loop; see
    /// <see cref="RescuedOutputPath"/> for the full failure analysis. Keeping one copy is
    /// deliberate: the twin pair <c>ReadExitCodeSafely</c> silently diverged between these two
    /// files, and duplicated logic is how that happened.
    ///
    /// ⚠️ "Merged-Videos-RECOVERED-" and the "Merger" tag are preserved verbatim — they are how
    /// the user and the crash digest know a rescued file came from the Merger and not the editor.
    /// </summary>
    private string? TryRescueFinishedRender(string sourcePath)
        => RescuedOutputPath.TryRescue(
            sourcePath,
            _paths.TempDirectory,
            "Merged-Videos-RECOVERED-",
            "Merger",
            "Could not preserve the finished merge");

    /// <summary>
    /// ISSUE_11 — disposing the worker now also STOPS the encoder.
    ///
    /// WHAT WAS WRONG: this released the bookkeeping object and nothing else. Every caller is
    /// expected to call <see cref="Cancel"/> first, but nothing enforced that, so any path that
    /// disposed a worker without cancelling left FFmpeg grinding at full CPU on a merge whose
    /// progress window had already gone — the machine stayed hot and loud for a file nobody would
    /// ever receive. Killing the tree here makes the object's own teardown sufficient.
    /// </summary>
    public void Dispose() => _lifetime.DisposeJob();
}
