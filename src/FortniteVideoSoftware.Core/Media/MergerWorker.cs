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

    private volatile bool _isCanceled;

    /// <summary>True when this job ended because the user stopped it, not because it failed.</summary>
    public bool WasCanceled => _isCanceled;

    public void Cancel()
    {
        _isCanceled = true;
        CoreLogger.Info("Merger", "Merge cancelled by user.");
        if (_currentProcess != null)
        {
            try { _currentProcess.Kill(entireProcessTree: true); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
        }
    }

    /// <summary>
    /// ISSUE_04 — reads a child process's exit code without ever throwing.
    ///
    /// Callers reach here after a CANCELLABLE wait whose OperationCanceledException is
    /// deliberately swallowed, so the process may still be dying (Kill is asynchronous) and
    /// `ExitCode` would throw InvalidOperationException. Give it a short grace period, then fall
    /// back to a sentinel rather than letting that exception masquerade as a pipeline crash.
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

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var cancelMirror = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => _isCanceled = true)
            : default;

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
                // IDEA_8: the resolved in/out window and the resulting length for each clip.
                var clipWindows = new (double start, double end, bool trimmed)[InputFiles.Count];
                var clipDurations = new double[InputFiles.Count];
                double peakSourceVideoBitrateKbps = 0;
                double durationWeightedBitrateKbps = 0;
                for (int fi = 0; fi < InputFiles.Count; fi++)
                {
                    var prober = new MediaProber(_ffprobePath, InputFiles[fi]);
                    double dur = await prober.GetDurationAsync();
                    bool hasAudio = await prober.HasAudioAsync();
                    fileDurations[fi] = dur;
                    fileHasAudio[fi] = hasAudio;

                    // IDEA_8: every downstream size, duration and progress calculation must use the
                    // TRIMMED length, not the file length. Getting this wrong does not fail loudly —
                    // it produces a wrong bitrate budget and a progress bar that never reaches 100%.
                    var (winStart, winEnd, winTrimmed) = ResolveClipWindow(fi, dur);
                    double effectiveDur = winEnd - winStart;
                    clipWindows[fi] = (winStart, winEnd, winTrimmed);
                    clipDurations[fi] = effectiveDur;

                    CoreLogger.Info("Merger", winTrimmed
                        ? $"  [{fi + 1}] {Path.GetFileName(InputFiles[fi])} — {dur:F2}s, trimmed to {winStart:F2}s-{winEnd:F2}s ({effectiveDur:F2}s), audio={hasAudio}"
                        : $"  [{fi + 1}] {Path.GetFileName(InputFiles[fi])} — {dur:F2}s, audio={hasAudio}");
                    CoreLogger.Debug("Merger", $"  [{fi + 1}] full path: {InputFiles[fi]}");
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
                    catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
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
                        FailureDetail = space.Message;
                        EmitFinished(false, space.Message ?? "Not enough free disk space.");
                        return;
                    }
                }

                var filters = new List<string>();
                var cmdArgs = new List<string> { "-y", "-hide_banner", "-progress", "pipe:1" };
                var effectiveMusicTracks = await BuildEffectiveMusicTracksAsync(outputDuration);

                int musicInputIndex = InputFiles.Count;

                // ─────────────────────────────────────────────────────────────────────────────
                // G08 — THE MERGER NEVER ASKED THE GPU TO DECODE ANYTHING.
                // This method used to append every `-i` here, with no `-hwaccel` anywhere in the
                // file, so every queued clip was decoded in software and then run through a
                // lanczos rescale + fps=60 conversion on the CPU. Only the final encode touched
                // the GPU. (project_structure.txt notes "the Merger never set
                // -hwaccel_output_format — that is why merging always worked"; that is true and
                // still true, but it also never set plain `-hwaccel`, which is the part that
                // actually moves DECODING onto the GPU.)
                //
                // Inputs are now built PER ATTEMPT, because `-hwaccel` is a per-input option and
                // its value depends on which encoder the current attempt uses — and the encoder
                // can change mid-run via the fallback chain. Input ORDER is unchanged, so
                // `musicInputIndex` and every [n:v]/[n:a] label in the filter graph still line up.
                // Deliberately NO `-hwaccel_output_format`: the merge filter graph is software.
                // ─────────────────────────────────────────────────────────────────────────────
                List<string> BuildInputArgs(string encoder)
                {
                    var decodeFlags = EncoderManager.GetDecodeFlags(encoder);
                    var args = new List<string>();
                    for (int i = 0; i < InputFiles.Count; i++)
                    {
                        args.AddRange(decodeFlags);
                        args.AddRange(["-i", InputFiles[i]]);
                    }
                    // Audio-only inputs — no hardware decode path, intentionally omitted.
                    foreach (var musicTrack in effectiveMusicTracks)
                    {
                        args.AddRange(["-i", musicTrack.Path]);
                    }
                    return args;
                }

                string vOutputLabel = "[v_concat]";
                string aOutputLabel = "[a_concat]";

                // SYNC_01: ONE interleaved list, [v0][a0][v1][a1]..., feeding ONE concat.
                // See the concat block below for why this must never be split back into two.
                string avInputs = "";
                for (int i = 0; i < InputFiles.Count; i++)
                {
                    string scaleFilter = OutputRatio == TargetAspectRatio.Portrait9x16
                        ? $"scale=1080:1920:force_original_aspect_ratio=increase:flags=lanczos,crop=1080:1920"
                        : $"scale=1920:1080:force_original_aspect_ratio=decrease:flags=lanczos,pad=1920:1080:(ow-iw)/2:(oh-ih)/2";

                    // IDEA_8: the trim pair. Both branches of the SAME clip get the identical window
                    // and are then rebased to zero, which is the whole A/V sync guarantee — the
                    // concat downstream assumes every segment starts at PTS 0.
                    // Order matters: trim BEFORE the speed change, so the numbers stay in plain
                    // source seconds and never have to be divided by speedFactor.
                    var win = clipWindows[i];
                    string vTrim = win.trimmed
                        ? $"trim=start={win.start.ToString("F3", CultureInfo.InvariantCulture)}:end={win.end.ToString("F3", CultureInfo.InvariantCulture)},"
                        : "";
                    string aTrim = win.trimmed
                        ? $"atrim=start={win.start.ToString("F3", CultureInfo.InvariantCulture)}:end={win.end.ToString("F3", CultureInfo.InvariantCulture)},"
                        : "";

                    // SYNC_01: the rebase to zero is now UNCONDITIONAL, not only on the trimmed
                    // path. A game-capture MP4 routinely starts its audio stream a few
                    // milliseconds before or after its video stream; without an explicit rebase
                    // that built-in offset walks straight into concat and shifts the whole clip.
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
                    // ─────────────────────────────────────────────────────────────────────────
                    // SYNC_01 — CUMULATIVE A/V DRIFT ACROSS A LONG MERGE.
                    //
                    // This used to be TWO independent concat filters: one that joined every
                    // video branch, and a separate one that joined every audio branch. Each
                    // built its own timeline by simply adding up the lengths handed to it, and
                    // neither one knew the other existed.
                    //
                    // Those two totals are never equal. A clip's audio almost never ends on the
                    // exact same microsecond as its video — game-capture MP4s are typically
                    // variable-frame-rate, and `fps=60` above re-times the video onto an exact
                    // 1/60s grid while the audio keeps its true sample-accurate length. So each
                    // clip contributes a small video-minus-audio difference, a few milliseconds
                    // either way. With two separate concats those differences ADD UP: clip 1 is
                    // fine, clip 10 is noticeably off, clip 30 is badly out. That is exactly the
                    // "gets worse the further into the video you go" signature.
                    //
                    // ONE concat with v=1:a=1 and the streams interleaved fixes it structurally.
                    // The concat filter then treats each clip as a SEGMENT: it takes the longest
                    // stream in that segment as the segment's length, advances every output by
                    // that same amount, and pads the shorter audio with silence. Video and audio
                    // are therefore re-zeroed together at every clip boundary, so an error can
                    // never survive past the clip that produced it, let alone accumulate.
                    //
                    // ⚠️ DO NOT split this back into two concat filters. Doing so reintroduces
                    // the drift, and it will look fine on a two-clip test and only show up on a
                    // long merge.
                    // ─────────────────────────────────────────────────────────────────────────
                    filters.Add($"{avInputs}concat=n={InputFiles.Count}:v=1:a=1{vOutputLabel}{aOutputLabel}");

                    // Belt and braces behind the structural fix: land the joined audio on a
                    // continuous 48 kHz timeline starting at exactly zero. `async=1` repairs any
                    // residual sub-millisecond gap at a boundary by resampling rather than by
                    // shifting everything after it, so nothing downstream can start sliding.
                    filters.Add($"{aOutputLabel}aresample=48000:async=1:min_comp=0.01:first_pts=0[a_concat_sync]");
                    aOutputLabel = "[a_concat_sync]";
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
                CoreLogger.Debug("FFmpeg MERGE", $"Filter Script Content:\n{filterScript}");

                // G03: honour the user's Settings override. "Auto"/empty keeps the historical
                // "GPU" behaviour (best available hardware encoder, CPU only as a fallback).
                string mergeStrategy = string.IsNullOrWhiteSpace(HardwareStrategy) ||
                                       HardwareStrategy.Equals("Auto", StringComparison.OrdinalIgnoreCase)
                    ? "GPU"
                    : HardwareStrategy;
                var encoderMgr = await Task.Run(() => new EncoderManager(mergeStrategy, _ffmpegPath), cancellationToken).ConfigureAwait(false);
                if (encoderMgr.EncoderPreflightError != null)
                {
                    FailureDetail = encoderMgr.EncoderPreflightError;
                    EmitFinished(false, encoderMgr.EncoderPreflightError);
                    return;
                }
                string currentEncoder = encoderMgr.GetInitialEncoder(!encoderMgr.ForcedCpu);

                int cqValue = QualityPercent >= 100 ? 15 : Math.Max(15, 35 - (int)((QualityPercent - 5) * 20.0 / 95.0));
                int qualityLevel = QualityPercent >= 100 ? 3 : (QualityPercent >= 50 ? 2 : 1);

                int? losslessBitrateKbps = null;
                int losslessMaxrateKbps = 0;
                if (QualityPercent >= 100 && averageSourceVideoBitrateKbps > 0)
                {
                    losslessBitrateKbps = Math.Max(800, (int)Math.Min(EncoderManager.MaxBitrateKbps, averageSourceVideoBitrateKbps));
                    losslessMaxrateKbps = Math.Max(losslessBitrateKbps.Value, (int)Math.Min(EncoderManager.MaxBitrateKbps, peakSourceVideoBitrateKbps));
                    CoreLogger.Info("Merger", $"Lossless target bitrate {losslessBitrateKbps} kbps (avg), maxrate {losslessMaxrateKbps} kbps (peak) — output size will track the combined source size.");
                }

                string corePath = Path.Combine(tempJobDir, "merged_output.mp4");
                string? successOutputPath = null;
                string lastErrorMsg = "FFmpeg render failed.";

                // ── T01 two-pass state (see TwoPassEncoding for the full rationale) ──────────
                // Both artefacts live in tempJobDir and are deleted within this job.
                string twoPassMasterPath = Path.Combine(tempJobDir, "twopass_master.mp4");
                string twoPassLogPrefix = Path.Combine(tempJobDir, "twopass_stats");
                bool twoPassDisabled = false;

                // FAST route needs room for the scratch master; without it we still do two-pass,
                // just the slower way that re-runs the filter graph. HasRoomFor never fails a job.
                bool twoPassFastRoute = DiskSpaceGuard.HasRoomFor(
                    tempJobDir, DiskSpaceGuard.EstimateTwoPassMasterBytes(outputDuration));

                // Guarantee a clean slate: a stats file left by anything else would hand pass 2 a
                // complexity map for the wrong video, which is silent quality damage, not an error.
                TwoPassEncoding.Cleanup(twoPassMasterPath, twoPassLogPrefix);

                while (true)
                {
                    var (codecArgs, rcLabel) = encoderMgr.GetCodecFlags(currentEncoder, losslessBitrateKbps, outputDuration, "60", qualityLevel, false);

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

                    // ── T01 GATE (Video Merger) ──────────────────────────────────────────────
                    // Two-pass runs ONLY at 100% "Lossless", because that is the only Merger mode
                    // with a bitrate BUDGET (`-b:v` anchored to the duration-weighted average
                    // source bitrate — the number the "Est. Output" label is derived from).
                    // Below 100% the Merger switches to CQ/CRF, which is constant-quality and has
                    // no budget to redistribute, so a second pass would cost time for nothing.
                    // libx264 only — NVENC has no stats-file two-pass (see EncoderManager).
                    bool twoPass = currentEncoder == "libx264"
                                   && losslessBitrateKbps.HasValue
                                   && QualityPercent >= 100
                                   && !twoPassDisabled;

                    // G09: name the chips, not just the codec.
                    CoreLogger.Info("FFmpeg", $"Executing merge: decode={EncoderManager.DescribeDecoder(currentEncoder)}, encode={EncoderManager.DescribeEncoder(currentEncoder)}, mode={rcLabel}, route={(twoPass ? (twoPassFastRoute ? "two-pass(fast)" : "two-pass(slow)") : "single-pass")}.");

                    bool attemptSuccess;

                    if (twoPass && twoPassFastRoute)
                    {
                        // Graph once into a near-lossless master, then both passes off the master.
                        var masterArgs = new List<string>(cmdArgs);
                        masterArgs.AddRange(BuildInputArgs(currentEncoder));
                        masterArgs.AddRange(["-filter_complex_script", filterScriptPath]);
                        masterArgs.AddRange(["-map", vOutputLabel, "-map", finalAudioLabel]);
                        masterArgs.AddRange(TwoPassEncoding.MasterCodecArgs());
                        masterArgs.AddRange(["-c:a", "aac", "-b:a", "192k"]);
                        masterArgs.Add(twoPassMasterPath);

                        CoreLogger.Debug("FFmpeg", $"Two-pass master: {_ffmpegPath} {string.Join(" ", masterArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");

                        attemptSuccess = await ExecuteFFmpegAsync(masterArgs, outputDuration, cancellationToken, 0, 60)
                                         && File.Exists(twoPassMasterPath) && new FileInfo(twoPassMasterPath).Length > 0;

                        if (attemptSuccess)
                        {
                            attemptSuccess = await RunTwoPassTailAsync(
                                twoPassMasterPath, corePath, twoPassLogPrefix,
                                losslessBitrateKbps!.Value, outputDuration, cancellationToken);
                        }

                        TwoPassEncoding.Cleanup(twoPassMasterPath, twoPassLogPrefix);

                        if (!attemptSuccess && !cancellationToken.IsCancellationRequested)
                        {
                            // Never fail a merge over this — drop to single-pass and try again.
                            twoPassDisabled = true;
                            CoreLogger.Fail("FFmpeg", "Two-pass merge failed — falling back to a single-pass merge.");
                            if (File.Exists(corePath)) { try { File.Delete(corePath); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); } }
                            continue;
                        }
                    }
                    else if (twoPass)
                    {
                        // SLOW route — no room for a master, so the filter graph genuinely runs
                        // twice. Still worth it: the user asked for an accurate output size.
                        int passKbps = Math.Min(EncoderManager.MaxBitrateKbps, Math.Max(300, losslessBitrateKbps!.Value));

                        var pass1Args = new List<string>(cmdArgs);
                        pass1Args.AddRange(BuildInputArgs(currentEncoder));
                        pass1Args.AddRange(["-filter_complex_script", filterScriptPath]);
                        // ⚠️ BOTH streams must be mapped and encoded even though this pass only
                        // measures video. `-an` (or omitting the audio map) makes FFmpeg abort:
                        // with `-filter_complex`, a labeled output nothing consumes is an
                        // unconnected-output error. The null muxer discards everything and the
                        // audio chain is trivial next to the video graph, so the cost is noise.
                        pass1Args.AddRange(["-map", vOutputLabel, "-map", finalAudioLabel]);
                        pass1Args.AddRange(TwoPassEncoding.PassArgs(passKbps, 1, twoPassLogPrefix));
                        pass1Args.AddRange(["-c:a", "aac", "-b:a", "192k", "-sn", "-dn", "-f", "null", "NUL"]);

                        attemptSuccess = await ExecuteFFmpegAsync(pass1Args, outputDuration, cancellationToken, 0, 45);

                        if (attemptSuccess)
                        {
                            var pass2Args = new List<string>(cmdArgs);
                            pass2Args.AddRange(BuildInputArgs(currentEncoder));
                            pass2Args.AddRange(["-filter_complex_script", filterScriptPath]);
                            pass2Args.AddRange(["-map", vOutputLabel, "-map", finalAudioLabel]);
                            pass2Args.AddRange(TwoPassEncoding.PassArgs(passKbps, 2, twoPassLogPrefix));
                            pass2Args.AddRange(["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);
                            pass2Args.Add(corePath);

                            attemptSuccess = await ExecuteFFmpegAsync(pass2Args, outputDuration, cancellationToken, 45, 100);
                        }

                        TwoPassEncoding.Cleanup(twoPassMasterPath, twoPassLogPrefix);

                        if (!attemptSuccess && !cancellationToken.IsCancellationRequested)
                        {
                            twoPassDisabled = true;
                            CoreLogger.Fail("FFmpeg", "Two-pass merge failed — falling back to a single-pass merge.");
                            if (File.Exists(corePath)) { try { File.Delete(corePath); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); } }
                            continue;
                        }
                    }
                    else
                    {
                        var attemptArgs = new List<string>(cmdArgs);
                        // G08: per-attempt inputs carry this attempt's `-hwaccel` flags.
                        attemptArgs.AddRange(BuildInputArgs(currentEncoder));
                        attemptArgs.AddRange(["-filter_complex_script", filterScriptPath]);
                        attemptArgs.AddRange(["-map", vOutputLabel, "-map", finalAudioLabel]);
                        attemptArgs.AddRange(codecArgs);
                        attemptArgs.AddRange(["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);
                        attemptArgs.Add(corePath);

                        CoreLogger.Debug("FFmpeg", $"Command: {_ffmpegPath} {string.Join(" ", attemptArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");

                        attemptSuccess = await ExecuteFFmpegAsync(attemptArgs, outputDuration, cancellationToken);
                    }

                    if (attemptSuccess && File.Exists(corePath) && new FileInfo(corePath).Length > 0)
                    {
                        successOutputPath = corePath;
                        // G09: the one line that makes a silent hardware downgrade impossible to
                        // miss. ⚠️ Its SHAPE must stay identical to ProcessWorker's — including the
                        // `route=` suffix — because Section 5C.6 documents one format for the whole
                        // suite and a log reader should not have to know which app produced a line.
                        string mergeRoute = twoPass ? (twoPassFastRoute ? " route=two-pass(fast)" : " route=two-pass(slow)") : "";
                        CoreLogger.Info("FFmpeg",
                            $"PIPELINE RESULT: decode={EncoderManager.DescribeDecoder(currentEncoder)} " +
                            $"encode={EncoderManager.DescribeEncoder(currentEncoder)} speed={LastReportedSpeed}{mergeRoute}" +
                            (currentEncoder == "libx264" && !encoderMgr.ForcedCpu
                                ? " — WARNING: this is the CPU fallback, the requested hardware encoder FAILED."
                                : string.Empty));
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

                    try { if (File.Exists(corePath)) File.Delete(corePath); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
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
                        EmitFinished(true, finalOutput);
                    }
                    catch (Exception moveEx)
                    {
                        string? rescued = TryRescueFinishedRender(successOutputPath);

                        CoreLogger.Fail("Merger",
                            $"The finished merge could not be moved to the destination: {moveEx.Message}");
                        CoreLogger.Debug("Merger", $"Destination was: {finalOutput}");

                        if (rescued != null)
                        {
                            CoreLogger.Info("Merger", $"Finished merge preserved at: {Path.GetFileName(rescued)}");
                            CoreLogger.Debug("Merger", $"Preserved merge full path: {rescued}");
                            FailureDetail =
                                $"The merge finished but could not be written to the destination folder.{Environment.NewLine}" +
                                $"Reason: {moveEx.Message}{Environment.NewLine}" +
                                $"Your merged video has NOT been lost — it is here:{Environment.NewLine}{rescued}";
                            EmitFinished(false,
                                "Your merged video finished, but it could not be saved to the destination folder. " +
                                "It has been kept safe — see the details for where to find it.");
                        }
                        else
                        {
                            FailureDetail =
                                $"The merge finished but could not be written to the destination folder, and the " +
                                $"temporary copy could not be preserved either.{Environment.NewLine}Reason: {moveEx.Message}";
                            EmitFinished(false, "Your merged video finished, but it could not be saved to the destination folder.");
                        }
                    }
                }
                else if (_isCanceled || cancellationToken.IsCancellationRequested)
                {
                    FailureDetail = null;
                    CoreLogger.Info("Merger", "Merge cancelled by the user.");
                    EmitFinished(false, CancelledMessage);
                }
                else
                {
                    EmitFinished(false, lastErrorMsg);
                }
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
            CoreLogger.Info("Merger", "Merge pipeline canceled.");
            EmitFinished(false, CancelledMessage);
        }
        catch (Exception ex)
        {
            if (_isCanceled || cancellationToken.IsCancellationRequested)
            {
                FailureDetail = null;
                CoreLogger.Info("Merger", $"Merge cancelled by the user (during: {ex.Message}).");
                EmitFinished(false, CancelledMessage);
                return;
            }

            CoreLogger.Fail("Merger", $"Merge pipeline failed with exception: {ex.Message}");
            CoreLogger.Debug("Merger", $"Merge pipeline failed with exception detail: {ex}");
            FailureDetail = ex.ToString();
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
            catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
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
                catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
            }

            if (durationFromSourceProbe && track.Offset > 0 && duration > track.Offset)
                duration -= track.Offset;

            if (duration <= 0)
                duration = musicWindowDuration;

            normalized.Add(new MusicTrack(track.Path, track.Offset, duration, track.TimelineStartDelay, track.ApplyFadeOut));
        }

        bool loopMusic = false;
        try { loopMusic = MusicConfig?["loop_music"]?.GetValue<bool>() ?? false; } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
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
    private async Task<bool> RunTwoPassTailAsync(
        string masterPath, string finalPath, string passLogPrefix,
        int videoBitrateKbps, double outputDuration, CancellationToken cancellationToken)
    {
        // PASS 1 — analysis only; audio is irrelevant to video complexity stats.
        var pass1 = new List<string> { "-y", "-hide_banner", "-progress", "pipe:1", "-i", masterPath };
        pass1.AddRange(TwoPassEncoding.PassArgs(videoBitrateKbps, 1, passLogPrefix));
        pass1.AddRange(["-an", "-sn", "-dn", "-f", "null", "NUL"]);

        // ⚠️ THESE BOUNDARIES ARE DELIBERATELY THE SAME AS ProcessWorker's TWO-PASS SPLIT
        // (TwoPassGraphFraction 0.60 / TwoPassAnalysisFraction 0.75) so Section 5C.4 describes
        // BOTH applications with one set of numbers. If you retune one, retune the other and the
        // documentation together — a silent drift here is what made 5C.4 wrong for the Merger.
        CoreLogger.Info("FFmpeg", "Two-pass merge: analyzing (2 of 3).");
        if (!await ExecuteFFmpegAsync(pass1, outputDuration, cancellationToken, 60, 75)) return false;

        // PASS 2 — the real encode, reading the map pass 1 wrote.
        var pass2 = new List<string> { "-y", "-hide_banner", "-progress", "pipe:1", "-i", masterPath };
        pass2.AddRange(TwoPassEncoding.PassArgs(videoBitrateKbps, 2, passLogPrefix));
        pass2.AddRange(["-c:a", "copy", "-movflags", "+faststart", finalPath]);

        CoreLogger.Info("FFmpeg", "Two-pass merge: encoding (3 of 3).");
        if (!await ExecuteFFmpegAsync(pass2, outputDuration, cancellationToken, 75, 100)) return false;

        return File.Exists(finalPath) && new FileInfo(finalPath).Length > 0;
    }

    /// <param name="progressFloor">
    /// T01 — start of this invocation's slice of the 0-100 bar. Defaults keep every pre-existing
    /// caller on the full 0-100 range, so single-pass merges behave exactly as before.
    /// </param>
    /// <param name="progressCeiling">T01 — end of this invocation's slice of the 0-100 bar.</param>
    private async Task<bool> ExecuteFFmpegAsync(List<string> cmdArgs, double totalDuration, CancellationToken cancellationToken,
                                                double progressFloor = 0.0, double progressCeiling = 100.0)
    {
        string cmdLine = string.Join(" ", cmdArgs.Select(a =>
            a.Length == 0 || a.Contains(' ') || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a));
        CoreLogger.Debug("FFmpeg MERGE", $"Executing Final Pipeline Command:\n{_ffmpegPath} {cmdLine}");

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string arg in cmdArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        _currentProcess?.Dispose();
        _currentProcess = Process.Start(psi);
        if (_currentProcess == null)
            return false;

        try { ChildProcessTracker.AddProcess(_currentProcess); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }

        using var reg = cancellationToken.Register(() =>
        {
            try { _currentProcess.Kill(entireProcessTree: true); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
        });

        var progressTask = Task.Run(async () =>
        {
            using var reader = _currentProcess.StandardOutput;
            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) continue;
                if (line.StartsWith("out_time_us="))
                {
                    if (long.TryParse(line.AsSpan(12), out long outTimeUs))
                    {
                        double currentSec = outTimeUs / 1_000_000.0;
                        if (totalDuration > 0)
                        {
                            // T01: floor/ceiling default to 0/100, so this is unchanged for
                            // every single-pass caller.
                            double frac = Math.Clamp(currentSec / totalDuration, 0.0, 1.0);
                            int percent = (int)Math.Clamp(progressFloor + frac * (progressCeiling - progressFloor), 0, 100);
                            ProgressUpdate?.Invoke(percent);
                        }
                    }
                }
                // G09: throughput, for the PIPELINE RESULT line.
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
            using var reader = _currentProcess.StandardError;
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

        int exitCode = ReadExitCodeSafely(_currentProcess, "FFmpeg MERGE");
        CoreLogger.Info("FFmpeg MERGE", $"Process exited with code {exitCode}.");

        string[] stderrLines;
        var errList = new System.Collections.Generic.List<string>();
        while (stderrChannel.Reader.TryRead(out var errLine)) errList.Add(errLine);
        stderrLines = errList.ToArray();

        if (_isCanceled || cancellationToken.IsCancellationRequested)
        {
            CoreLogger.Info("FFmpeg MERGE", "Merge stopped because the user cancelled.");
            FailureDetail = null;
            return false;
        }

        if (exitCode != 0)
        {
            FailureDetail = stderrLines.Length > 0
                ? string.Join("\n", stderrLines)
                : $"FFmpeg exited with code {exitCode}.";

            if (stderrLines.Length > 0)
                CoreLogger.Fail("FFmpeg MERGE", $"FFmpeg stderr (last {stderrLines.Length} lines):\n{string.Join("\n", stderrLines)}");
        }
        else if (stderrLines.Length > 0)
        {
            CoreLogger.Debug("FFmpeg MERGE", $"FFmpeg stderr (last {stderrLines.Length} lines):\n{string.Join("\n", stderrLines)}");
        }

        return exitCode == 0;
    }

    private void EmitFinished(bool success, string message)
    {
        if (_finishEmitted) return;
        _finishEmitted = true;
        Finished?.Invoke(success, message);
    }

    /// <summary>
    /// ISSUE_03 — moves a COMPLETED merge out of the per-job temp folder (which the pipeline's
    /// <c>finally</c> deletes wholesale) into the temp ROOT, so a destination that cannot be
    /// written never costs the user the work that was already done. Returns null only if there is
    /// genuinely nothing left to save.
    /// </summary>
    private string? TryRescueFinishedRender(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath)) return null;

            Directory.CreateDirectory(_paths.TempDirectory);

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string rescued = Path.Combine(_paths.TempDirectory, $"Merged-Videos-RECOVERED-{stamp}.mp4");

            int n = 1;
            while (File.Exists(rescued))
            {
                rescued = Path.Combine(_paths.TempDirectory, $"Merged-Videos-RECOVERED-{stamp}-{n}.mp4");
                n++;
            }

            File.Move(sourcePath, rescued);
            return rescued;
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("Merger", $"Could not preserve the finished merge: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// ISSUE_11 — disposing the worker now also STOPS the encoder.
    ///
    /// WHAT WAS WRONG: this released the bookkeeping object and nothing else. Every caller is
    /// expected to call <see cref="Cancel"/> first, but nothing enforced that, so any path that
    /// disposed a worker without cancelling left FFmpeg grinding at full CPU on a merge whose
    /// progress window had already gone — the machine stayed hot and loud for a file nobody would
    /// ever receive. Killing the tree here makes the object's own teardown sufficient.
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
                    CoreLogger.Info("Merger", "Worker disposed while the encoder was still running — terminating the FFmpeg process tree.");
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }

            try { proc.Dispose(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
            _currentProcess = null;
        }
    }
}
