// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FortniteVideoSoftware.App.Models;
using FortniteVideoSoftware.Core;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App.Services;

public class MainMediaController
{
    public async Task<ExportResult> ExecuteExportAsync(
        ExportPayload payload, 
        CancellationToken ct, 
        Action<int> onProgress, 
        Action<int, string, int> onPhase)
    {
        var paths = ApplicationPaths.CreateDefault();

        // ══════════════════════════════════════════════════════════════════════════════════════
        // WORKERLIFETIME_01 — THE WORKER IS NOW SCOPED, AND SO IS ITS CANCELLATION REGISTRATION.
        //
        // ProcessWorker is IDisposable and NOTHING EVER DISPOSED IT. Its Dispose() carries the
        // ISSUE_11 contract — "any path that disposed a worker without cancelling left a full-speed
        // encode running on a file that would never be delivered, with the progress overlay already
        // gone" — and that entire backstop was unreachable code, because the only construction site
        // in the app (this method) let the instance fall out of scope on every exit path, success
        // and failure alike. The symptom users report (fans at full tilt, pegged CPU, nothing on
        // screen) is exactly what an undisposed worker produces when the setup path throws AFTER
        // RunAsync has been kicked off.
        //
        // `using` on the worker must be the OUTERMOST scope so Dispose runs strictly AFTER
        // `await tcs.Task` returns — disposing earlier would kill a process that had already
        // succeeded.
        //
        // The ct.Register handle was also being discarded. A discarded CancellationTokenRegistration
        // keeps its closure — and therefore the worker, and therefore the ProgressUpdate/PhaseUpdate/
        // Finished delegate chains that close over MainWindow's controls — rooted for the whole
        // lifetime of the CancellationTokenSource. `using` unregisters it deterministically.
        // ══════════════════════════════════════════════════════════════════════════════════════
        using var worker = new ProcessWorker(paths);

        try
        {
            RuntimeLog.Info("Process", "Starting video processing pipeline via MainMediaController.");
            worker.OutputDirectory = payload.OutputDirectory;
            
            worker.ProgressUpdate += (percent) => onProgress(percent);
            worker.PhaseUpdate += (phase, title, prog) => onPhase(phase, title, prog);
            
            // RunContinuationsAsynchronously: Finished is raised from the FFmpeg pump thread. Without
            // this flag the awaiting continuation in ProcessVideoAsync would be invoked INLINE on
            // that thread, which is how a UI continuation ends up executing off the dispatcher.
            var tcs = new TaskCompletionSource<ExportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            
            using var cancelReg = ct.Register(() => {
                try { worker.Cancel(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            });

            worker.Finished += (success, message) =>
            {
                if (!success && (worker.WasCanceled || ct.IsCancellationRequested))
                {
                    RuntimeLog.Info("Process", "Worker cleaned up after cancellation.");
                    tcs.TrySetResult(new ExportResult { Canceled = true });
                    return;
                }

                if (success)
                {
                    RuntimeLog.Success("Process", $"Video processing completed successfully. Saved to: {message}");
                    tcs.TrySetResult(new ExportResult { Success = true, OutputPath = message, Warning = worker.CompletionWarning });
                }
                else
                {
                    RuntimeLog.Fail("Process", $"Video processing failed: {message}");
                    tcs.TrySetResult(new ExportResult 
                    { 
                        Success = false, 
                        ErrorMessage = worker.LastFailure?.Summary ?? worker.FailureDetail ?? message,
                        Failure = worker.LastFailure 
                    });
                }
            };
            
            worker.InputPath = payload.InputPath;
            worker.StartTimeMs = payload.TrimStartMs;
            
            double effectiveEndMs = payload.TrimEndMs > 0 ? payload.TrimEndMs : payload.LoadedVideoDurationMs;
            worker.EndTimeMs = effectiveEndMs;
            
            if (payload.SpeedSegments != null) worker.SpeedSegments = payload.SpeedSegments;
            // CUT_01 — carry the cut list across to the encoder. Without this the payload would
            // hold the cuts and the export would quietly ignore every one of them.
            if (payload.Cuts != null) worker.Cuts = payload.Cuts;
            worker.SpeedFactor = payload.BaseSpeed;
            worker.HardwareStrategy = payload.HardwareMode;
            
            if (payload.ThumbnailSet && payload.ThumbnailPosMs > 0)
            {
                worker.ThumbnailPosMs = payload.ThumbnailPosMs;
                worker.IntroAbsTimeMs = payload.ThumbnailPosMs;
                worker.IntroStillSec = payload.ThumbnailDurationSec > 0 ? payload.ThumbnailDurationSec : 0.1;
            }
            else
            {
                worker.IntroAbsTimeMs = payload.ThumbnailPosMs > 0 ? payload.ThumbnailPosMs : payload.TrimStartMs;
                worker.IntroStillSec = 0.1;
            }
            
            var audioPrefs = Infrastructure.SettingsManager.Instance;
            worker.SourceMeasuredLufs = payload.SourceMeasuredLufs;
            worker.ApplyLoudnessNormalization = payload.ApplyLoudnessNormalization ?? audioPrefs.LoudnessNormalizationPrompt != Infrastructure.AudioFixPrompt.NeverApply;
            bool peakWanted = payload.ApplyPeakFlattening ?? audioPrefs.PeakFlatteningPrompt != Infrastructure.AudioFixPrompt.NeverApply;
            worker.AutoSpikeFlattening = audioPrefs.Defaults.AutoSpikeFlattening && peakWanted;
            worker.AutoVoiceNormalization = audioPrefs.Defaults.AutoVoiceNormalization;
            
            worker.IsMobileFormat = payload.IsMobileFormat;
            worker.EnableFades = payload.EnableFades;
            worker.ShowTeammates = payload.ShowTeammates;
            worker.ShowSpectating = payload.ShowSpectating;
            worker.MemeFile = payload.MemeFile;
            worker.MemeAtStart = payload.MemeAtStart;
            if (payload.MemePlacements != null && payload.MemePlacements.Count > 0)
                worker.MemePlacements = payload.MemePlacements;
            worker.PortraitText = payload.PortraitText;
            
            worker.QualityLevel = payload.QualityLevel;
            worker.TargetMbOverride = payload.TargetMbOverride;
            
            worker.MusicLeadFadeIn = payload.MusicLeadFadeIn;
            worker.MusicTailFadeOut = payload.MusicTailFadeOut;
            if (payload.MusicTracks != null) worker.MusicTracks = payload.MusicTracks;
            if (payload.MusicConfig != null) worker.MusicConfig = payload.MusicConfig;
            worker.KeepMusicDuringMeme = payload.KeepMusicDuringMeme;
            
            worker.VoiceOverWavPath = payload.VoiceOverWavPath;
            worker.VoiceOverStartSec = payload.VoiceOverStartSec;
            if (payload.VoiceOverTakes != null) worker.VoiceOverTakes = payload.VoiceOverTakes;
            
            worker.VoiceOverDuckAudio = payload.VoiceOverDuckAudio;
            worker.VoiceOverProtectFromMusic = payload.VoiceOverProtectFromMusic;
            
            // ══════════════════════════════════════════════════════════════════════════════════
            // WORKERLIFETIME_02 — THE HANG GUARD.
            //
            // RunAsync signals completion through the Finished EVENT, not through its Task, so the
            // Task is intentionally not awaited. But that means a throw which escapes RunAsync
            // WITHOUT reaching EmitFinished leaves `tcs` uncompleted forever: the await below never
            // returns, the phase overlay never clears and the PROCESS button never re-enables — a
            // permanently wedged UI with no error shown.
            //
            // RunAsync's own top-level catch covers almost everything, but not a throw from the
            // registration it takes before that try opens (see CANCELREG_01 in ProcessWorker), and
            // not an OOM. This continuation is the backstop that converts any such escape into a
            // normal, reported export failure.
            // ══════════════════════════════════════════════════════════════════════════════════
            _ = worker.RunAsync(ct).ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    RuntimeLog.Fail("Process", $"Export pipeline faulted outside its own handler: {t.Exception.GetBaseException().Message}");
                    var escaped = FfmpegErrorClassifier.ClassifyException(
                        t.Exception.GetBaseException(),
                        ExportStage.Preflight,
                        new ExportAttemptIdentity { AttemptIndex = 1, Operation = "ExportPipeline", Description = "Export pipeline" });
                    tcs.TrySetResult(new ExportResult
                    {
                        Success = false,
                        ErrorMessage = escaped.Summary,
                        Failure = escaped
                    });
                }
                else if (t.IsCanceled)
                {
                    tcs.TrySetResult(new ExportResult { Canceled = true });
                }
                else
                {
                    // Completed normally. Finished should already have fired; if a future edit ever
                    // introduces a silent return path, this stops the UI wedging on it.
                    tcs.TrySetResult(new ExportResult
                    {
                        Success = false,
                        ErrorMessage = worker.LastFailure?.Summary ?? "The export pipeline stopped without reporting a result.",
                        Failure = worker.LastFailure
                    });
                }
            }, TaskScheduler.Default);

            return await tcs.Task;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Process", ex);
            var failure = FfmpegErrorClassifier.ClassifyException(ex, ExportStage.Preflight,
                new ExportAttemptIdentity { AttemptIndex = 1, Operation = "ExportSetup", Description = "Export preparation" });
            return new ExportResult { Success = false, ErrorMessage = failure.Summary, Failure = failure };
        }
    }
}
