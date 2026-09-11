using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Ipc;
using FortniteVideoSoftware.Core.Media;

// Run from the workspace root. All generated media and state stay inside this directory.
string root = Directory.GetCurrentDirectory();
string ffmpeg = Path.Combine(root, "binaries", "ffmpeg.exe");
if (!File.Exists(ffmpeg)) throw new InvalidOperationException("Run from the workspace root.");
string work = Path.Combine(root, "tests", "MediaPipelineChecks", "artifacts", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(work);
Directory.CreateDirectory(Path.Combine(work, "temp"));
Environment.SetEnvironmentVariable("TMP", Path.Combine(work, "temp"));
Environment.SetEnvironmentVariable("TEMP", Path.Combine(work, "temp"));
Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, Path.Combine(work, "state"));
Environment.SetEnvironmentVariable("PATH", Path.Combine(root, "binaries") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
var failures = new List<string>();

await Check("Crop recovery: newest valid backup, unchanged backups, defaults only as last resort", async () =>
{
    var paths = new ApplicationPaths(Path.Combine(work, "crops"));
    Directory.CreateDirectory(paths.ProgramDataRoot);
    var store = new CropConfigStore(paths);
    var older = CropConfigDefaults.Create();
    older["test_marker"] = "older";
    var newest = CropConfigDefaults.Create();
    newest["test_marker"] = "newest";
    AtomicJsonFile.WriteObject(paths.CropCoordinatesFile + ".bak3", older);
    AtomicJsonFile.WriteObject(paths.CropCoordinatesFile + ".bak2", newest);
    File.WriteAllText(paths.CropCoordinatesFile + ".bak1", "{broken");
    File.WriteAllText(paths.CropCoordinatesFile, "{broken");
    string before = File.ReadAllText(paths.CropCoordinatesFile + ".bak2");
    var restored = await store.LoadAsync();
    Require(restored["test_marker"]?.ToString() == "newest", "Did not choose newest valid backup.");
    Require(File.ReadAllText(paths.CropCoordinatesFile + ".bak2") == before, "Recovery changed backup.");
    Require((await store.LoadAsync())["test_marker"]?.ToString() == "newest", "Recovered file was not persisted.");
    File.Delete(paths.CropCoordinatesFile);
    Require((await store.LoadAsync())["test_marker"]?.ToString() == "newest", "Missing live file did not recover.");
    var emptyPaths = new ApplicationPaths(Path.Combine(work, "empty-crops"));
    Directory.CreateDirectory(emptyPaths.ProgramDataRoot);
    var defaults = await new CropConfigStore(emptyPaths).LoadAsync();
    Require(defaults["coordinate_space"]?.ToString() == CropConfigDefaults.CoordinateSpace, "Defaults invalid.");
});

await Check("FFmpeg filters remain identical across regional settings", async () =>
{
    var original = CultureInfo.CurrentCulture;
    try
    {
        string? baseline = null;
        foreach (var culture in new[] { "en-US", "de-DE", "fr-FR", "ar-SA" })
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var speed = GranularSpeedBuilder.Build(2000, [new(200, 800, 0.5), new(1100, 1100, 0.0)],
                baseSpeed: 1.25, needHudBranch: false, targetFps: "30");
            baseline ??= speed.filterGraph;
            Require(speed.filterGraph == baseline, $"Graph varies under {culture}.");
            foreach (var rate in new[] { 0.1, 0.5, 1.0, 1.25, 4.0 })
            {
                // AVSYNC_01 — 1.0x is a true no-op and returns an empty chain.
                var tempoChain = GranularSpeedBuilder.BuildAtempoChain(rate);
                if (tempoChain.Count == 0) continue;
                await Ffmpeg("-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-af",
                    string.Join(",", tempoChain), "-t", "0.05", "-f", "null", "NUL");
            }
            await Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-filter_complex", speed.filterGraph.Replace("[0:a]", "[1:a]"), "-map", speed.videoLabel, "-map", speed.audioLabel,
                "-t", "2", "-f", "null", "NUL");
        }
    }
    finally { CultureInfo.CurrentCulture = original; }
});

await Check("Intel low-quality preset is accepted by the bundled FFmpeg", async () =>
{
    var manager = new EncoderManager("INTEL", ffmpeg);
    var (flags, _) = manager.GetCodecFlags("h264_qsv", null, 1, "30", 1, false);
    string preset = flags[flags.IndexOf("-preset") + 1];
    var result = await Run(["-f", "lavfi", "-i", "color=s=320x180:r=30", "-frames:v", "1", ..flags, "-f", "null", "NUL"]);
    Require(!result.error.Contains("Unable to parse option") && !result.error.Contains("Error setting option preset"), result.error);
    if (result.exit != 0) Console.WriteLine($"  Preset '{preset}' parsed; Intel encode was not verified: {result.error.Trim()}");
});

await Check("Export mix ignores preview master; Wizard levels change each audio channel", async () =>
{
    var mix = new JsonObject { ["main_vol"] = 0.8, ["music_vol"] = 0.4, ["ducking_enabled"] = false, ["carving_enabled"] = false };
    string? baseline = null;
    foreach (int master in new[] { 0, 25, 50, 100 })
    {
        MpvIpcClient.SetGlobalMasterVolume(master);
        var graph = AudioFilterChain.Build(mix, 0, 1, 1, true, 0, null,
            musicTracks: [new("music.wav", 0, 1)], totalProjectDuration: 1);
        string filter = string.Join(";", graph.chains);
        baseline ??= filter;
        Require(filter == baseline, "Preview loudness changed export mix.");
    }
    var samples = await MixSamples(0.8, 0.4);
    var gameMuted = await MixSamples(0, 0.4);
    var musicMuted = await MixSamples(0.8, 0);
    Require(Rms(gameMuted, 0) < 0.00001, "Gameplay mute failed.");
    Require(Rms(musicMuted, 1) < 0.00001, "Music mute failed.");
    Require(Math.Abs(Rms(samples, 0) / Rms(samples, 1) - 2) < 0.01, "80/40 mix ratio was not preserved.");
    Require(Math.Abs(Rms(samples, 1) - Rms(gameMuted, 1)) < 0.00001, "Gameplay control changed music level.");
});

await Check("Actual mpv preview audio matches linear Wizard/export levels", async () =>
{
    string source = Path.Combine(work, "preview-source.wav");
    await Ffmpeg("-f", "lavfi", "-i", "sine=frequency=440:duration=0.5", source);
    double referenceRms = 0;
    foreach (int linear in new[] { 100, 80, 40, 20, 0 })
    {
        string wav = Path.Combine(work, $"preview-{linear}.wav");
        var result = await RunExecutable(Path.Combine(root, "binaries", "mpv.exe"),
            ["--no-config", "--no-terminal", "--vid=no", "--ao=pcm", "--ao-pcm-file=" + wav,
             "--volume=" + MpvIpcClient.ToMpvVolume(linear).ToString(CultureInfo.InvariantCulture), source]);
        Require(result.exit == 0, result.error);
        string raw = wav + ".raw";
        await Ffmpeg("-i", wav, "-ac", "2", "-f", "f32le", raw);
        byte[] bytes = File.ReadAllBytes(raw);
        float[] samples = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        double measured = Rms(samples, 0);
        if (linear == 100) referenceRms = measured;
        Require(Math.Abs(measured / referenceRms - linear / 100.0) < 0.005, $"mpv amplitude differs at {linear}%.");
    }
});

await Check("New mpv audio previews inherit the preview master", async () =>
{
    NativeLibrary.SetDllImportResolver(typeof(MpvWrapper).Assembly, (name, _, _) =>
        name == "libmpv-2.dll" ? NativeLibrary.Load(Path.Combine(root, "binaries", name)) : IntPtr.Zero);
    foreach (int master in new[] { 0, 25, 100 })
    {
        MpvIpcClient.SetGlobalMasterVolume(master);
        using var player = new MpvIpcClient();
        await player.StartAudioOnlyAsync("");
        Require(Math.Abs(double.Parse(player.GetPropertyString("volume")!, CultureInfo.InvariantCulture) - MpvIpcClient.ToMpvVolume(master)) < 0.01, "Initial mpv volume differs.");
    }
});

await Check("Complete Main export works with decimal commas and muted preview", async () =>
{
    string input = Path.Combine(work, "source.mp4");
    await Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
        "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", input);
    var savedCulture = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        MpvIpcClient.SetGlobalMasterVolume(0);
        using var worker = new ProcessWorker(new ApplicationPaths(Path.Combine(work, "main-state")))
        {
            InputPath = input, OutputDirectory = ExportDirectory("main-export"),
            StartTimeMs = 0, EndTimeMs = 1500, SpeedFactor = 1.25, OriginalResolution = "320x180",
            IsMobileFormat = false, HardwareStrategy = "CPU", QualityLevel = 20,
            EnableFades = false, IntroStillSec = 0.1, ApplyLoudnessNormalization = false,
            AutoSpikeFlattening = false, AutoVoiceNormalization = false
        };
        bool success = false;
        string output = "";
        worker.Finished += (ok, message) => { success = ok; output = message; };
        await worker.RunAsync();
        Require(success, worker.FailureDetail ?? output);
        Require(await AudioRms(output) > 0.01, "Preview mute leaked into Main export.");
    }
    finally { CultureInfo.CurrentCulture = savedCulture; }
});

await Check("Complete Merger export honors Wizard gameplay mute without music", async () =>
{
    using var worker = new MergerWorker(new ApplicationPaths(Path.Combine(work, "merger-state")))
    {
        InputFiles = [Path.Combine(work, "source.mp4")], OutputDirectory = ExportDirectory("merge-export"),
        HardwareStrategy = "CPU", QualityPercent = 5, AutoSpikeFlattening = false,
        MusicConfig = new JsonObject { ["main_vol"] = 0.0 }
    };
    bool success = false;
    string output = "";
    worker.Finished += (ok, message) => { success = ok; output = message; };
    await worker.RunAsync();
    Require(success, worker.FailureDetail ?? output);
    Require(await AudioRms(output) < 0.00001, "Wizard gameplay mute was ignored in Merger.");
});

await Check("GPU: unsupported effects retain their complete graph and audio stays independent", () =>
{
    foreach (string effect in new[] { "fade=t=in:st=0:d=1", "crop=160:180:0:0", "scale=320:180:force_original_aspect_ratio=decrease", "format=yuva420p" })
    {
        string graph = $"[0:v]{effect}[v];[0:a]volume='if(lt(t,1),0.4,0.8)'[a]";
        var plan = ExportVideoPipeline.Create("h264_nvenc", graph);
        Require(!plan.UsesGpuFrames && plan.FilterGraph == graph && plan.DecodeFlags.Count == 0,
            "Unsupported effect was changed or triggered GPU downloads.");
    }
    var native = ExportVideoPipeline.Create("h264_nvenc", "[0:v]format=yuv420p[v];[0:a]volume='if(lt(t,1),0.4,0.8)'[a]");
    Require(native.UsesGpuFrames && native.FilterGraph.Contains("volume='if(lt(t,1),0.4,0.8)'"), "Audio expression was changed by GPU planning.");
    var cpu = ExportVideoPipeline.Create("libx264", "[0:v]format=yuv420p[v]");
    Require(!cpu.UsesGpuFrames && cpu.DeviceFlags.Count == 0, "CPU fallback inherited a CUDA device.");
    return Task.CompletedTask;
});

await Check("GPU: complete Main export keeps intro and speed changes in VRAM", async () =>
{
    string input = await EnsureGpuSource();
    var result = await MainGpuExport("gpu-main", input, worker =>
    {
        worker.IntroStillSec = 0.1;
        worker.SpeedSegments = [new(200, 800, 0.5)];
    });
    Require(result.gpu, "Main fell back from its resident graph. See gpu-main.log.");
    Require(await AudioRms(result.path) > 0.01, "Main GPU export lost audio.");
});

await Check("GPU: complete Merger scales and joins multiple clips in VRAM", async () =>
{
    string input = await EnsureGpuSource();
    using var worker = new MergerWorker(new ApplicationPaths(Path.Combine(work, "gpu-merge-state")))
    {
        InputFiles = [input, input], OutputDirectory = ExportDirectory("gpu-merge"),
        HardwareStrategy = "NVIDIA", QualityPercent = 45, AutoSpikeFlattening = false,
        MusicConfig = new JsonObject { ["main_vol"] = 0.8 },
        ClipTrims = [new(0.2, 1.2), new(0.5, 1.5)]
    };
    bool success = false;
    string output = "";
    worker.Finished += (ok, message) => { success = ok; output = message; };
    var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var previous = CoreLogger.InfoAction;
    CoreLogger.InfoAction = (topic, message) => logs.Enqueue($"{topic}: {message}");
    try { await worker.RunAsync(); }
    finally { CoreLogger.InfoAction = previous; File.WriteAllLines(Path.Combine(work, "gpu-merge.log"), logs); }
    Require(success, worker.FailureDetail ?? output);
    Require(worker.UsedGpuVideoProcessing, "Merger fell back from its resident graph. See gpu-merge.log.");
    Require(await AudioRms(output) > 0.01, "GPU merge lost audio.");
});

await Check("GPU: portrait crop and fades retain their effects with hardware encoding", async () =>
{
    string input = await EnsureGpuSource();
    var result = await MainGpuExport("gpu-effects", input, worker =>
    {
        worker.IsMobileFormat = true;
        worker.StartTimeMs = 500;
        worker.EndTimeMs = 1500;
        worker.EnableFades = true;
    });
    Require(!result.gpu && result.description.Contains("NVENC"), "Effects did not retain GPU encoding.");
    var probe = new MediaProber(Path.Combine(root, "binaries", "ffprobe.exe"), result.path);
    Require(await probe.GetResolutionAsync() == (1080, 1920), "Portrait output size changed.");
});

await Check("GPU: unsupported hardware decoding retries once and retains NVENC", async () =>
{
    string input = Path.Combine(work, "software-decoder.mkv");
    await Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
        "-c:v", "ffv1", "-c:a", "pcm_s16le", "-shortest", input);
    var result = await MainGpuExport("gpu-retry", input, _ => { });
    Require(!result.gpu && result.description.Contains("NVENC"), "Decoder failure discarded the hardware encoder.");
    string logs = File.ReadAllText(Path.Combine(work, "gpu-retry.log"));
    Require(logs.Split("Retrying the original effects with the same hardware encoder.").Length == 2,
        "Expected exactly one compatibility retry.");
});

await Check("GPU: size-locked NVENC export probes complexity and lands on target without a blind retry", async () =>
{
    // PROBE_01 — 20 s of 1080p60 content under a 25.0 MB size lock. The export must
    // MEASURE the clip's complexity with a 5 s NVENC CQ-20 middle slice (software
    // decode, null muxer), pull the -b:v ask below the naive budget by the calibrated
    // margin, and land inside the acceptance band on the FIRST encode. The blind
    // size-retry loop (attempt 2) must never be entered; it stays only as the safety
    // net for probe failures and foreign NVENC SDK behaviour.
    string input = Path.Combine(work, "nvenc-size-source.mp4");
    if (!File.Exists(input))
        await Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=60:d=20",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=20",
            "-c:v", "h264_nvenc", "-preset", "p6", "-cq", "19",
            "-c:a", "aac", "-b:a", "128k", "-shortest", input);

    var result = await MainGpuExport("gpu-size-probe", input, worker =>
    {
        worker.EndTimeMs = 20000;
        worker.OriginalResolution = "1920x1080";
        worker.TargetMbOverride = 25.0;
    });
    Require(result.gpu, "Size-locked export fell back from its resident NVENC graph. See gpu-size-probe.log.");
    Require(result.worker.ComplexityProbeRan, "The NVENC complexity probe did not run.");
    Require(result.worker.LastComplexityProbeCommandLine != null &&
        !result.worker.LastComplexityProbeCommandLine.Contains("hwaccel"),
        "The probe injected hardware decode flags (zero-copy guardrail violated).");
    Require(result.worker.ComplexityProbeBitsPerSecond > 10_000_000 &&
        result.worker.ComplexityProbeBitsPerSecond < 120_000_000,
        $"Probe appetite {result.worker.ComplexityProbeBitsPerSecond:N0} bps is implausible for 1080p60 content.");
    Require(result.worker.ComplexityProbeScaleFactor is > 0.98 and < 1.0,
        $"Scale factor {result.worker.ComplexityProbeScaleFactor:F4} is not a calibrated sub-unity margin.");
    string log = File.ReadAllText(Path.Combine(work, "gpu-size-probe.log"));
    Require(log.Split("PROBE_01 complexity probe:").Length == 2,
        "The probe must run exactly once per export.");
    // The probe-scaled rate must actually reach the encoder: -b:v and -maxrate carry
    // the same probe-discounted value, below the 10358 kbps naive budget for this
    // exact 25.0 MB / 20 s / 127 kbps-audio configuration.
    var bv = System.Text.RegularExpressions.Regex.Match(log, @"-b:v (\d+)k");
    var mr = System.Text.RegularExpressions.Regex.Match(log, @"-maxrate (\d+)k");
    Require(bv.Success && mr.Success && bv.Groups[1].Value == mr.Groups[1].Value,
        "The probe-scaled rate must be applied to both -b:v and -maxrate.");
    Require(int.TryParse(bv.Groups[1].Value, out int appliedKbps) && appliedKbps > 9000 && appliedKbps <= 10358,
        $"Applied -b:v {bv.Groups[1].Value} does not look like the probe-discounted budget rate.");
    Require(!log.Contains("Export size target not met after retries"),
        "The export missed the acceptance band even with the probe.");
    Require(!log.Contains("Could not preserve the first render before retrying"),
        "The blind size retry was entered.");
    double mb = new FileInfo(result.path).Length / 1048576.0;
    Require(mb is >= 23.75 and <= 26.25,
        $"Landed at {mb:F2} MB — outside the 5% acceptance band around the 25.0 MB target.");
    Require(mb is >= 24.2 and <= 25.2,
        $"Landed at {mb:F2} MB — the probe should land near ~24.8 MB, not merely inside the band.");
});

await Check("Export errors: missing encoder classification", async () =>
{
    await Task.CompletedTask;
    var collector = new FfmpegDiagnosticCollector();
    collector.AddStderrLine("Unknown encoder 'h264_nvenc_test'");
    collector.AddStderrLine("Error initializing output stream 0:0 -- Error while opening encoder for output stream #0:0 - maybe incorrect parameters such as bit_rate, rate, width or height");
    var attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "SinglePassEncode", Encoder = "h264_nvenc_test" };
    var failure = FfmpegErrorClassifier.Classify(ExportStage.Encoding, attempt, processExitCode: 1, processStartException: null, isTimeout: false, isCancellation: false, collector: collector);
    
    Require(failure.Category == ExportFailureCategory.MissingEncoder, $"Expected MissingEncoder, got {failure.Category}");
    Require(failure.Summary.Contains("encoder", StringComparison.OrdinalIgnoreCase), $"Unexpected summary: {failure.Summary}");
    Require(failure.SpecificCause != null && failure.SpecificCause.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase), $"SpecificCause was {failure.SpecificCause}");
});

await Check("Export errors: corrupt input classification and explicit code", async () =>
{
    await Task.CompletedTask;
    var collector = new FfmpegDiagnosticCollector();
    collector.AddStderrLine("[mov,mp4,m4a,3gp,3g2,mj2 @ 000002] moov atom not found");
    collector.AddStderrLine("input.mp4: Invalid data found when processing input");
    collector.AddStderrLine("Task finished with error code: -1094995529 (Invalid data found when processing input)");
    var attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "SinglePassEncode" };
    var failure = FfmpegErrorClassifier.Classify(ExportStage.Encoding, attempt, processExitCode: 1, processStartException: null, isTimeout: false, isCancellation: false, collector: collector);

    Require(failure.Category == ExportFailureCategory.CorruptInput, $"Expected CorruptInput, got {failure.Category}");
    Require(failure.NativeErrorCode == -1094995529, $"Expected -1094995529, got {failure.NativeErrorCode}");
    Require(failure.NativeErrorSource == "FFmpeg", $"Expected FFmpeg source, got {failure.NativeErrorSource}");
    Require(failure.Summary.Contains("corrupt", StringComparison.OrdinalIgnoreCase), $"Unexpected summary: {failure.Summary}");
});

await Check("Export errors: disk full classification", async () =>
{
    await Task.CompletedTask;
    var collector = new FfmpegDiagnosticCollector();
    collector.AddStderrLine("av_interleaved_write_frame(): No space left on device");
    collector.AddStderrLine("Task finished with error code: -28 (No space left on device)");
    var attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "SinglePassEncode" };
    var failure = FfmpegErrorClassifier.Classify(ExportStage.Encoding, attempt, processExitCode: 1, processStartException: null, isTimeout: false, isCancellation: false, collector: collector);

    Require(failure.Category == ExportFailureCategory.DiskFull, $"Expected DiskFull, got {failure.Category}");
    Require(failure.NativeErrorCode == -28, $"Expected -28, got {failure.NativeErrorCode}");
    Require(failure.Summary.Contains("free space", StringComparison.OrdinalIgnoreCase), $"Unexpected summary: {failure.Summary}");
});

await Check("Export errors: access denied classification", async () =>
{
    await Task.CompletedTask;
    var collector = new FfmpegDiagnosticCollector();
    collector.AddStderrLine("output.mp4: Permission denied");
    collector.AddStderrLine("Task finished with error code: -13 (Permission denied)");
    var attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "SinglePassEncode" };
    var failure = FfmpegErrorClassifier.Classify(ExportStage.Encoding, attempt, processExitCode: 1, processStartException: null, isTimeout: false, isCancellation: false, collector: collector);

    Require(failure.Category == ExportFailureCategory.AccessDenied, $"Expected AccessDenied, got {failure.Category}");
    Require(failure.NativeErrorCode == -13, $"Expected -13, got {failure.NativeErrorCode}");
    Require(failure.Summary.Contains("access", StringComparison.OrdinalIgnoreCase), $"Unexpected summary: {failure.Summary}");
});

await Check("Export errors: startup failure (invalid binary path) classification", async () =>
{
    await Task.CompletedTask;
    Exception? caughtEx = null;
    try
    {
        Process.Start(new ProcessStartInfo("C:\\NonExistent_Directory_12345\\ffmpeg_missing.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }
    catch (Exception ex)
    {
        caughtEx = ex;
    }

    Require(caughtEx != null, "Process.Start unexpectedly succeeded with nonexistent path.");
    var attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "StartProcess" };
    var failure = FfmpegErrorClassifier.Classify(ExportStage.Encoding, attempt, processExitCode: null, processStartException: caughtEx, isTimeout: false, isCancellation: false, collector: null);

    Require(failure.Category == ExportFailureCategory.StartupFailure, $"Expected StartupFailure, got {failure.Category}");
    Require(failure.NativeErrorCode == 2, $"Expected Win32 error 2, got {failure.NativeErrorCode}");
    Require(failure.NativeErrorSource == "Win32", $"Expected Win32 error source, got {failure.NativeErrorSource}");
    Require(failure.Summary.Contains("not found", StringComparison.OrdinalIgnoreCase), $"Unexpected summary: {failure.Summary}");
});

await Check("Export errors: timeout and cancellation classification", async () =>
{
    await Task.CompletedTask;
    var attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "SinglePassEncode" };
    var cancelFailure = FfmpegErrorClassifier.Classify(ExportStage.Encoding, attempt, processExitCode: null, processStartException: null, isTimeout: false, isCancellation: true, collector: null);
    Require(cancelFailure.Category == ExportFailureCategory.Cancellation, $"Expected Cancellation, got {cancelFailure.Category}");
    Require(cancelFailure.Summary.Contains("cancelled", StringComparison.OrdinalIgnoreCase), $"Unexpected summary: {cancelFailure.Summary}");

    var timeoutFailure = FfmpegErrorClassifier.Classify(ExportStage.Encoding, attempt, processExitCode: null, processStartException: null, isTimeout: true, isCancellation: false, collector: null);
    Require(timeoutFailure.Category == ExportFailureCategory.Timeout, $"Expected Timeout, got {timeoutFailure.Category}");
    Require(timeoutFailure.Summary.Contains("timed out", StringComparison.OrdinalIgnoreCase), $"Unexpected summary: {timeoutFailure.Summary}");
});

await Check("Export errors: early useful error preserved despite 100+ trailing diagnostic lines", async () =>
{
    await Task.CompletedTask;
    var collector = new FfmpegDiagnosticCollector();
    collector.AddStderrLine("[h264_qsv @ 000001] Error creating a MFX session: -9.");
    collector.AddStderrLine("[h264_qsv @ 000001] Error while opening encoder - maybe incorrect parameters such as bit_rate, rate, width or height.");
    for (int i = 0; i < 120; i++)
    {
        collector.AddStderrLine($"[routine_filter @ {i:D6}] Routine log line {i} with uninteresting details");
    }

    var attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "SinglePassEncode", Encoder = "h264_qsv" };
    var failure = FfmpegErrorClassifier.Classify(ExportStage.Encoding, attempt, processExitCode: 1, processStartException: null, isTimeout: false, isCancellation: false, collector: collector);

    Require(failure.Category == ExportFailureCategory.MissingEncoder, $"Expected MissingEncoder, got {failure.Category}");
    Require(failure.DiagnosticLines.Any(l => l.Contains("MFX session") || l.Contains("-9")), "Early MFX session error line was evicted from diagnostic lines.");
    Require(failure.SpecificCause != null && failure.SpecificCause.Contains("-9"), $"SpecificCause did not capture MFX error: {failure.SpecificCause}");
});

await Check("Export errors: unfamiliar error wording produces honest Unknown category", async () =>
{
    await Task.CompletedTask;
    var collector = new FfmpegDiagnosticCollector();
    collector.AddStderrLine("Something totally bizarre and unique happened in subsystem XYZ-999");
    collector.AddStderrLine("Thread aborted due to unexplained status 9999");

    var attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "SinglePassEncode" };
    var failure = FfmpegErrorClassifier.Classify(ExportStage.Encoding, attempt, processExitCode: 1, processStartException: null, isTimeout: false, isCancellation: false, collector: collector);

    Require(failure.Category == ExportFailureCategory.Unknown, $"Expected Unknown category, got {failure.Category}");
    Require(failure.NativeErrorCode == null, $"Expected null NativeErrorCode for generic exit code, got {failure.NativeErrorCode}");
    Require(failure.Summary.Contains("unexpected", StringComparison.OrdinalIgnoreCase), $"Unexpected summary: {failure.Summary}");
});

await Check("Export errors: GPU failure followed by successful fallback leaves LastFailure null", async () =>
{
    string input = Path.Combine(work, "software-decoder.mkv");
    if (!File.Exists(input))
    {
        await Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
            "-c:v", "ffv1", "-c:a", "pcm_s16le", "-shortest", input);
    }

    using var worker = new ProcessWorker(new ApplicationPaths(Path.Combine(work, "gpu-fallback-check-state")))
    {
        InputPath = input, OutputDirectory = ExportDirectory("gpu-fallback-check"),
        StartTimeMs = 0, EndTimeMs = 1800, OriginalResolution = "320x180",
        IsMobileFormat = false, HardwareStrategy = "NVIDIA", QualityLevel = 20,
        EnableFades = false, ApplyLoudnessNormalization = false,
        AutoSpikeFlattening = false, AutoVoiceNormalization = false
    };

    bool success = false;
    string output = "";
    worker.Finished += (ok, message) => { success = ok; output = message; };
    await worker.RunAsync();

    Require(success, worker.FailureDetail ?? output);
    Require(worker.LastFailure == null, $"LastFailure should be null on successful export, but was: {worker.LastFailure?.Summary}");
    Require(worker.FailureDetail == null, $"FailureDetail should be null on successful export, but was: {worker.FailureDetail}");
});

await Check("Export errors: old unrelated log entry in shared log cannot become current cause", async () =>
{
    await Task.CompletedTask;
    CoreLogger.Fail("OldJob", "Fatal: No space left on device while writing old video 9999");

    var collector = new FfmpegDiagnosticCollector();
    collector.AddStderrLine("Random pipeline error occurred during filtering");
    var attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "SinglePassEncode" };
    var failure = FfmpegErrorClassifier.Classify(
        ExportStage.Encoding, attempt,
        processExitCode: 1, processStartException: null,
        isTimeout: false, isCancellation: false,
        collector: collector);

    Require(failure.Category != ExportFailureCategory.DiskFull,
        $"Old log falsely caused failure category to become DiskFull.");
    Require(failure.Category == ExportFailureCategory.Unknown,
        $"Expected Unknown category, got {failure.Category}");
    Require(!failure.Summary.Contains("space", StringComparison.OrdinalIgnoreCase),
        $"Summary contained old log content: {failure.Summary}");
    Require(failure.SpecificCause == null || !failure.SpecificCause.Contains("space", StringComparison.OrdinalIgnoreCase),
        $"SpecificCause contained old log content: {failure.SpecificCause}");
    Require(!failure.DiagnosticLines.Any(l => l.Contains("space", StringComparison.OrdinalIgnoreCase)),
        $"DiagnosticLines contained old log content: {string.Join(", ", failure.DiagnosticLines)}");
});

await Check("A/V sync drift: cut and speed seams keep audio packets flush with video", async () =>
{
    const double DriftToleranceSec = 0.005;
    const int SampleRate = 48000;
    const double AacPacketSec = 1024.0 / SampleRate;
    string ffprobe = Path.Combine(root, "binaries", "ffprobe.exe");

    // AVSYNC_01 — the synthetic source. Ten seconds of 30 fps footage with a 1 kHz
    // beep whose ONSET sits exactly on every 1.0 s frame boundary (0.2 s beep,
    // 0.8 s silence). The beeps are drift probes: whatever the export graph does
    // to time, each surviving beep's decoded onset must equal the output second
    // that OutputTimeline predicts for its source second.
    string input = Path.Combine(work, "sync-source.mp4");
    string beep = "0.8*sin(2*PI*1000*t)*lt(mod(t\\,1)\\,0.2)";
    await Ffmpeg(
        "-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=10",
        "-f", "lavfi", "-i", $"aevalsrc={beep}|{beep}:s=48000:d=10",
        "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-b:a", "192k", "-shortest", input);

    // AVSYNC_01 — the edit under test, expressed exactly as the UI expresses it:
    // CutRange and SpeedSegment in ABSOLUTE source milliseconds. The cut swallows
    // the 2 s beep whole and closes the timeline up; the 0.5x segment doubles
    // 5-6 s. Expected finished video: [0,2) + [3,5) + [5,6)@0.5 + [6,10) =
    // exactly 10.0 s. Normalisation and the peak limiter are switched off on
    // purpose: they are gain stages orthogonal to the seam mathematics under
    // test, and the limiter's lookahead is itself a few milliseconds of filter
    // latency that would pollute the drift measurement.
    using var worker = new ProcessWorker(new ApplicationPaths(Path.Combine(work, "sync-state")))
    {
        InputPath = input, OutputDirectory = ExportDirectory("sync-export"),
        StartTimeMs = 0, EndTimeMs = 10000, SpeedFactor = 1.0,
        SpeedSegments = [new SpeedSegment(5000, 6000, 0.5)],
        Cuts = [new CutRange(2000, 3000)],
        OriginalResolution = "320x180", IsMobileFormat = false,
        HardwareStrategy = "CPU", QualityLevel = 20,
        EnableFades = false, ApplyLoudnessNormalization = false,
        AutoSpikeFlattening = false, AutoVoiceNormalization = false
    };
    bool success = false;
    string output = "";
    worker.Finished += (ok, message) => { success = ok; output = message; };
    await worker.RunAsync();
    Require(success, worker.FailureDetail ?? output);

    // The timeline model the rest of the application already draws with is the
    // independent oracle: source second -> output second for each beep that
    // survives the cut. The canonical mapping is asserted too, so a change to
    // the model itself can never silently re-bless this check.
    var timeline = OutputTimeline.Create(10000, [new SpeedSegment(5000, 6000, 0.5)], 1.0, 0, null,
        [new OutputTimeline.Cut(2.0, 3.0)]);
    double[] expected = new[] { 0, 1, 3, 4, 5, 6, 7, 8, 9 }.Select(s => timeline.SourceToOutput(s)).ToArray();
    double[] canonical = [0, 1, 2, 3, 4, 6, 7, 8, 9];
    for (int i = 0; i < canonical.Length; i++)
        Require(Math.Abs(expected[i] - canonical[i]) < 1e-9,
            $"OutputTimeline oracle returned {expected[i]:F4}s for beep {i + 1}, expected {canonical[i]:F4}s.");

    // AVSYNC_01 — packet-level truth on the finished file. The mix is AAC at
    // 48 kHz, so every full packet is exactly 1024 samples. If any seam drops,
    // duplicates or offsets audio, a pts gap stops being 1024/48000 long before
    // the drift can reach the 5 ms budget. (1 ms structural budget — far
    // tighter than the 5 ms DoD, and immune to ffprobe's 6-decimal rounding.)
    var audioPackets = JsonNode.Parse(await RunText(ffprobe, "-v", "error", "-select_streams", "a",
        "-show_entries", "packet=pts_time,duration_time", "-of", "json", output))!["packets"]!.AsArray();
    var audioPts = new List<double>();
    double lastDuration = 0;
    foreach (var packet in audioPackets)
    {
        if (double.TryParse(packet!["pts_time"]?.GetValue<string>(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double ptsTime)) audioPts.Add(ptsTime);
        if (double.TryParse(packet["duration_time"]?.GetValue<string>(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double durationTime)) lastDuration = durationTime;
    }
    audioPts.Sort();
    Require(audioPts.Count > 400, $"Only {audioPts.Count} audio packets in the export.");
    double worstGap = 0;
    for (int i = 1; i < audioPts.Count - 1; i++)
        worstGap = Math.Max(worstGap, Math.Abs(audioPts[i] - audioPts[i - 1] - AacPacketSec));
    // AAC's encoder priming is the ONE negative start allowed: the encoder emits
    // a leading frame the edit list discards, so raw packet pts may begin exactly
    // one frame before zero — decoded content still starts at zero, which the
    // beep-onset measurement below proves against the same budget.
    Require(audioPts[0] >= -AacPacketSec - 0.0005 && audioPts[0] <= DriftToleranceSec,
        $"Audio stream starts at {audioPts[0]:F6}s; only one AAC frame of encoder priming (-{AacPacketSec:F6}s) may precede zero.");
    Require(worstGap <= 0.001,
        $"Audio packet spacing drifted up to {worstGap * 1000:F3} ms; every full AAC frame must be exactly {AacPacketSec:F6}s.");
    Require(Math.Abs(audioPts[^1] + lastDuration - 10.0) <= DriftToleranceSec,
        $"Audio spans {audioPts[0]:F4}-{audioPts[^1] + lastDuration:F4}s; the finished edit must be exactly 10.0 s.");
    Console.WriteLine($"  Audio packets: {audioPts.Count}, first={audioPts[0]:F6}s, last={audioPts[^1]:F6}s (+{lastDuration:F6}s), worst gap error {worstGap * 1000:F3} ms.");
    // AVSYNC-PART2 — the video side of the same contract: 600 frames of strict
    // CFR 60 starting at zero, and a keyframe sitting exactly on each seam
    // second (the exporter's 2 s GOP puts an IDR on every concat boundary:
    // 0/2/4/6/8 s). B-frames reorder packets, so pts are sorted before the
    // uniform-spacing assertion.
    var videoPackets = JsonNode.Parse(await RunText(ffprobe, "-v", "error", "-select_streams", "v",
        "-show_entries", "packet=pts_time,flags", "-of", "json", output))!["packets"]!.AsArray();
    var frames = videoPackets
        .Select(p => (Pts: double.Parse(p!["pts_time"]!.GetValue<string>(), CultureInfo.InvariantCulture),
            Keyframe: (p["flags"]?.GetValue<string>() ?? "").Contains('K')))
        .OrderBy(f => f.Pts)
        .ToArray();
    Require(frames.Length == 600, $"Expected 600 CFR frames (10 s at 60 fps), found {frames.Length}.");
    Require(Math.Abs(frames[0].Pts) <= 0.0005, $"Video starts at {frames[0].Pts:F4}s instead of zero.");
    double worstFrameGap = 0;
    for (int i = 1; i < frames.Length; i++)
        worstFrameGap = Math.Max(worstFrameGap, Math.Abs(frames[i].Pts - frames[i - 1].Pts - 1.0 / 60.0));
    Require(worstFrameGap <= 0.001,
        $"Video is not uniformly 60 fps: worst frame gap error {worstFrameGap * 1000:F3} ms.");
    foreach (double seam in new[] { 2.0, 4.0, 6.0 })
    {
        var onSeam = frames.Where(f => Math.Abs(f.Pts - seam) <= 0.001).ToArray();
        Require(onSeam.Length > 0, $"No video frame lands on the {seam}s seam.");
        Require(onSeam.Any(f => f.Keyframe), $"No keyframe lands on the {seam}s seam.");
    }

    // AVSYNC-PART2 — decode the finished mix and measure where each beep
    // actually begins. A 1 ms backward-looking peak envelope feeds a rising-edge
    // gate (on above 30% of the beep plateau, off below 5%); the backward window
    // keeps the detected edge a fraction of a millisecond after the true one, so
    // the whole 5 ms budget stays available for real drift.
    string raw = Path.Combine(work, "sync-output-audio.raw");
    await Ffmpeg("-i", output, "-vn", "-ac", "1", "-ar", "48000", "-f", "f32le", raw);
    byte[] bytes = File.ReadAllBytes(raw);
    float[] samples = new float[bytes.Length / sizeof(float)];
    Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
    Require(samples.Length >= SampleRate * 10 && samples.Length <= SampleRate * 10 + 1024,
        $"Decoded to {samples.Length} samples; the edit-list-trimmed mix must be exactly {SampleRate * 10} (plus at most one padded AAC tail frame).");
    int window = SampleRate / 1000;
    var onsets = new List<double>();
    bool loud = false;
    for (int i = 0; i < samples.Length; i++)
    {
        double windowPeak = 0;
        for (int j = Math.Max(0, i - window + 1); j <= i; j++)
        {
            double a = Math.Abs(samples[j]);
            if (a > windowPeak) windowPeak = a;
        }
        if (!loud && windowPeak > 0.30) { onsets.Add(i / (double)SampleRate); loud = true; }
        else if (loud && windowPeak < 0.05) loud = false;
    }
    string found = string.Join(", ", onsets.Select(o => o.ToString("F4", CultureInfo.InvariantCulture)));
    Require(onsets.Count == canonical.Length,
        $"Expected {canonical.Length} beeps after the cut, found {onsets.Count}: [{found}].");
    // The beep at output 0.0 sits on TWO by-design amplitude ramps: the SPLICE_01
    // de-click fade into its chunk and the AAC decoder's first-frame
    // reconstruction after the edit-list priming trim. Both delay the envelope
    // crossing (~10 ms combined) without moving the content — the packet checks
    // above prove the t=0 boundary exactly (one priming frame, then zero drift).
    const double StartOfStreamBudgetSec = 0.012;
    double worstDrift = 0;
    int worstIndex = 0;
    bool exceeded = false;
    for (int i = 0; i < Math.Min(onsets.Count, canonical.Length); i++)
    {
        double drift = Math.Abs(onsets[i] - canonical[i]);
        if (drift > worstDrift) { worstDrift = drift; worstIndex = i; }
        if (drift > (i == 0 ? StartOfStreamBudgetSec : DriftToleranceSec)) exceeded = true;
    }
    Require(!exceeded,
        $"Beep {worstIndex + 1} landed at {onsets[worstIndex]:F4}s, expected {canonical[worstIndex]:F4}s " +
        $"(drift {(onsets[worstIndex] - canonical[worstIndex]) * 1000:+0.00;-0.00} ms, budget 5 ms). Onsets: [{found}].");
    Console.WriteLine($"  Max A/V drift across the 2 s cut and 0.5x seams: {worstDrift * 1000:F2} ms over {onsets.Count} beeps.");
});

if (failures.Count > 0)
{
    foreach (string failure in failures) Console.Error.WriteLine(failure);
    Console.Error.WriteLine($"Failures retained for inspection at: {work}");
    return 1;
}

if (!args.Contains("--keep-artifacts"))
{
    try
    {
        if (Directory.Exists(work))
            Directory.Delete(work, recursive: true);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Warning: Could not remove test artifacts: {ex.Message}");
    }
}

Console.WriteLine("All checks passed. Test artifacts cleaned.");
return 0;

async Task Check(string name, Func<Task> check)
{
    if (args.Contains("--gpu-only") && !name.StartsWith("GPU:", StringComparison.Ordinal)) return;
    try { await check(); Console.WriteLine("PASS: " + name); }
    catch (Exception ex) { failures.Add("FAIL: " + name + " — " + ex.Message); }
}
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
async Task<(int exit, string error)> Run(string[] arguments)
{
    return await RunExecutable(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", ..arguments]);
}
async Task<(int exit, string error)> RunExecutable(string executable, string[] arguments)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var arg in arguments) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!;
    var stderr = process.StandardError.ReadToEndAsync();
    var stdout = process.StandardOutput.ReadToEndAsync();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    using var kill = deadline.Token.Register(() => { try { process.Kill(true); } catch { } });
    await process.WaitForExitAsync(deadline.Token);
    await stdout;
    return (process.ExitCode, await stderr);
}
async Task<string> RunText(string executable, params string[] arguments)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var arg in arguments) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!;
    var stderr = process.StandardError.ReadToEndAsync();
    string stdout = await process.StandardOutput.ReadToEndAsync();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    using var kill = deadline.Token.Register(() => { try { process.Kill(true); } catch { } });
    await process.WaitForExitAsync(deadline.Token);
    string error = await stderr;
    Require(process.ExitCode == 0, error);
    return stdout;
}
async Task Ffmpeg(params string[] arguments)
{
    var result = await Run(arguments);
    Require(result.exit == 0, result.error);
}
async Task<float[]> MixSamples(double game, double music)
{
    var config = new JsonObject { ["main_vol"] = game, ["music_vol"] = music, ["ducking_enabled"] = false, ["carving_enabled"] = false };
    var graph = AudioFilterChain.Build(config, 0, 1, 1, true, 0, null,
        musicTracks: [new("music.wav", 0, 1)], totalProjectDuration: 1);
    string path = Path.Combine(work, $"mix-{game.ToString(CultureInfo.InvariantCulture)}-{music.ToString(CultureInfo.InvariantCulture)}.raw");
    await Ffmpeg("-f", "lavfi", "-i", "aevalsrc=0.1*sin(2*PI*440*t)|0:s=48000:d=1",
        "-f", "lavfi", "-i", "aevalsrc=0|0.1*sin(2*PI*880*t):s=48000:d=1",
        "-filter_complex", string.Join(";", graph.chains), "-map", graph.finalLabel, "-t", "1", "-f", "f32le", path);
    byte[] bytes = File.ReadAllBytes(path);
    float[] samples = new float[bytes.Length / sizeof(float)];
    Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
    return samples;
}
async Task<double> AudioRms(string media)
{
    string raw = media + ".audio.raw";
    await Ffmpeg("-i", media, "-vn", "-ac", "2", "-f", "f32le", raw);
    byte[] bytes = File.ReadAllBytes(raw);
    Require(bytes.Length > 0, "Export has no decoded audio samples.");
    float[] samples = new float[bytes.Length / sizeof(float)];
    Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
    return Rms(samples, 0);
}
static double Rms(float[] samples, int channel)
{
    double sum = 0;
    for (int i = channel; i < samples.Length; i += 2) sum += samples[i] * samples[i];
    return Math.Sqrt(sum / (samples.Length / 2));
}

string ExportDirectory(string name) => Directory.CreateDirectory(Path.Combine(work, name)).FullName;

async Task<string> EnsureGpuSource()
{
    string input = Path.Combine(work, "gpu-source.mp4");
    if (!File.Exists(input))
        await Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", input);
    return input;
}

async Task<(string path, bool gpu, string description, ProcessWorker worker)> MainGpuExport(string name, string input, Action<ProcessWorker> configure)
{
    using var worker = new ProcessWorker(new ApplicationPaths(Path.Combine(work, name + "-state")))
    {
        InputPath = input, OutputDirectory = ExportDirectory(name),
        StartTimeMs = 0, EndTimeMs = 1800, OriginalResolution = "320x180",
        IsMobileFormat = false, HardwareStrategy = "NVIDIA", QualityLevel = 20,
        EnableFades = false, ApplyLoudnessNormalization = false,
        AutoSpikeFlattening = false, AutoVoiceNormalization = false
    };
    configure(worker);
    bool success = false;
    string output = "";
    worker.Finished += (ok, message) => { success = ok; output = message; };
    var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var previousInfo = CoreLogger.InfoAction;
    var previousFail = CoreLogger.FailAction;
    CoreLogger.InfoAction = (topic, message) => logs.Enqueue($"{topic}: {message}");
    CoreLogger.FailAction = (topic, message) => logs.Enqueue($"ERROR {topic}: {message}");
    try { await worker.RunAsync(); }
    finally
    {
        CoreLogger.InfoAction = previousInfo;
        CoreLogger.FailAction = previousFail;
        File.WriteAllLines(Path.Combine(work, name + ".log"), logs);
    }
    Require(success, worker.FailureDetail ?? output);
    return (output, worker.UsedGpuVideoProcessing, worker.LastVideoPipeline, worker);
}
