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
        var worker = new ProcessWorker(paths);
        
        try
        {
            RuntimeLog.Info("Process", "Starting video processing pipeline via MainMediaController.");
            worker.OutputDirectory = payload.OutputDirectory;
            
            worker.ProgressUpdate += (percent) => onProgress(percent);
            worker.PhaseUpdate += (phase, title, prog) => onPhase(phase, title, prog);
            
            var tcs = new TaskCompletionSource<ExportResult>();
            
            ct.Register(() => {
                try { worker.Cancel(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
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
                    RuntimeLog.Success("Process", $"Video processing completed successfully. Saved to: {Path.GetFileName(message)}");
                    tcs.TrySetResult(new ExportResult { Success = true, OutputPath = message, Warning = worker.CompletionWarning });
                }
                else
                {
                    RuntimeLog.Fail("Process", $"Video processing failed: {message}");
                    tcs.TrySetResult(new ExportResult { Success = false, ErrorMessage = worker.FailureDetail ?? message });
                }
            };
            
            worker.InputPath = payload.InputPath;
            worker.StartTimeMs = payload.TrimStartMs;
            
            double effectiveEndMs = payload.TrimEndMs > 0 ? payload.TrimEndMs : payload.LoadedVideoDurationMs;
            worker.EndTimeMs = effectiveEndMs;
            
            if (payload.SpeedSegments != null) worker.SpeedSegments = payload.SpeedSegments;
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
                worker.IntroAbsTimeMs = payload.TrimStartMs;
                worker.IntroStillSec = 0.1;
            }
            
            var audioPrefs = Infrastructure.SettingsManager.Instance;
            worker.SourceMeasuredLufs = payload.SourceMeasuredLufs;
            worker.ApplyLoudnessNormalization = payload.ApplyLoudnessNormalization ?? audioPrefs.LoudnessNormalizationPrompt != Infrastructure.AudioFixPrompt.NeverApply;
            bool peakWanted = payload.ApplyPeakFlattening ?? audioPrefs.PeakFlatteningPrompt != Infrastructure.AudioFixPrompt.NeverApply;
            worker.AutoSpikeFlattening = audioPrefs.Defaults.AutoSpikeFlattening && peakWanted;
            
            worker.IsMobileFormat = payload.IsMobileFormat;
            worker.IsBossHp = payload.IsBossHp;
            worker.EnableFades = payload.EnableFades;
            worker.ShowTeammates = payload.ShowTeammates;
            worker.ShowSpectating = payload.ShowSpectating;
            worker.MemeFile = payload.MemeFile;
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
            
            _ = worker.RunAsync(ct);
            return await tcs.Task;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Process", ex);
            return new ExportResult { Success = false, ErrorMessage = ex.ToString() };
        }
    }
}
