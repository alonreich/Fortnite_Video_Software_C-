using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Models;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class MainWindow
{
    /// <summary>
    /// EXPORTSESSION_01 — THE SINGLE-FLIGHT GATE AND THE CANCELLATION-TOKEN-SOURCE OWNER.
    ///
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// Everything about an export's LIFETIME lives here; everything about its CONTENT lives in
    /// <see cref="ProcessVideoCoreAsync"/>. That split is the whole point, because the three defects
    /// this replaces were all lifetime defects, not pipeline defects:
    ///
    ///   • Export had no mutual exclusion beyond processButton.IsEnabled, and the overlay's Cancel
    ///     handler re-enabled that button while the previous pipeline was still unwinding. Two
    ///     FFmpeg pipelines could run at once.
    ///   • The old code did `previousCts?.Dispose()` at the top of every export, disposing the
    ///     CancellationTokenSource the PREVIOUS worker still held live registrations on. That is an
    ///     ObjectDisposedException inside ProcessWorker, and it used to escape RunAsync without ever
    ///     reaching EmitFinished — leaving the awaiting UI hung forever with the overlay already
    ///     dismissed.
    ///   • Nothing waited for the pipeline before tearing down the window.
    ///
    /// THE RULE, and it is not negotiable: the CancellationTokenSource created here is disposed HERE,
    /// in the finally, and only AFTER the pipeline Task it was handed to has completed. No other code
    /// path may dispose it. The UI is likewise restored in exactly one place — this finally — so
    /// "the overlay is gone" can never again mean anything except "the pipeline has stopped".
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    private async Task ProcessVideoAsync(Button processButton)
    {
        if (_exportRunning)
        {
            RuntimeLog.Info("UI", "PROCESS ignored: an export is already running or still stopping.");
            ShowTacticalFeedback("An export is already running");
            return;
        }

        _exportRunning = true;

        var cts = new System.Threading.CancellationTokenSource();
        _processCts = cts;

        processButton.IsEnabled = false;
        processButton.Content = "PROCESSING...";

        Task work = ProcessVideoCoreAsync(processButton, cts);
        _exportInFlight = work;

        try
        {
            await work;
        }
        catch (OperationCanceledException)
        {
            RuntimeLog.Info("EXPORT", "Export cancelled.");
            ShowTacticalFeedback("Processing Cancelled");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("EXPORT", ex);
            await ErrorReporter.ShowAsync(this, "Export failed",
                "Something went wrong while preparing or running the export.", ex.Message);
        }
        finally
        {
            _exportInFlight = null;
            _processCts = null;

            try { cts.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }

            OverlayLayerCtl?.StopOverlay();
            if (ActiveVideoHost != null) ActiveVideoHost.IsVisible = true;
            processButton.IsEnabled = true;
            processButton.Content = "PROCESS";

            _exportRunning = false;
        }
    }

    private async Task ProcessVideoCoreAsync(Button processButton, System.Threading.CancellationTokenSource processCts)
    {
        if (ActiveVideoHost?.IpcClient != null)
        {
            _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
        }

        if (string.IsNullOrEmpty(_loadedVideoPath) || !System.IO.File.Exists(_loadedVideoPath))
        {
            ShowTacticalFeedback("No valid video loaded to process!");
            PlayUiSound();
            processButton.IsEnabled = true;
            processButton.Content = "PROCESS";
            await ErrorReporter.ShowAsync(this, "Nothing to export",
                "There is no video loaded, or the file that was loaded has been moved or deleted. Load a video and try again.",
                "");
            return;
        }

        string? outputDirectory = await Infrastructure.OutputFolderResolver.ResolveAsync(
            this, Infrastructure.OutputFolderResolver.AppScope.Main);
        if (outputDirectory == null)
        {
            ShowTacticalFeedback("Export cancelled — no output folder");
            processButton.IsEnabled = true;
            processButton.Content = "PROCESS";
            return;
        }

        if (!await ConfirmHighSegmentCountAsync())
        {
            processButton.IsEnabled = true;
            processButton.Content = "PROCESS";
            return;
        }

        await Task.Yield();
        
        SetTimelinePopupsVisible(false);
        if (ActiveVideoHost != null) ActiveVideoHost.IsVisible = false;
        OverlayLayerCtl?.StartOverlay();

        var addMemeCb = this.FindControl<Avalonia.Controls.ToggleSwitch>("AddMemeCheckbox");
        string? memeFile = null;
        if (addMemeCb != null && addMemeCb.IsChecked == true)
        {
            var memeCb = this.FindControl<Avalonia.Controls.ComboBox>("MemeComboBox");
            if (memeCb?.SelectedItem is MemeItem memeSel && !memeSel.IsDownloadAction && System.IO.File.Exists(memeSel.FullPath))
            {
                memeFile = memeSel.FullPath;
            }
        }

        
        int qualityIdx = this.FindControl<FreeVideoStudio.App.Controls.SpinningWheelSlider>("QualitySlider")?.Value
                         ?? FreeVideoStudio.App.ViewModels.QualityLadder.DefaultIndex;
        int resolvedQuality = FreeVideoStudio.App.ViewModels.QualityLadder.ToWorkerQualityLevel(qualityIdx);
        var sizeRequest = CaptureSizeRequest();
        Services.OutputSizeEstimate sizeEstimate;
        try
        {
            var estimateToken = processCts.Token;
            sizeEstimate = await Task.Run(() => SizeEstimator.EstimateMainAsync(sizeRequest, estimateToken), estimateToken);
        }
        catch (OperationCanceledException swallowed)
        {
            OverlayLayerCtl?.StopOverlay();
            processButton.IsEnabled = true;
            processButton.Content = "PROCESS";
            if (ActiveVideoHost != null) ActiveVideoHost.IsVisible = true;
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed);
            return;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("SIZE ESTIMATE", ex);
            sizeEstimate = Services.OutputSizeEstimate.Empty;
        }
        double? resolvedTargetMb = sizeEstimate.TargetMegabytes;
        if (!FreeVideoStudio.App.ViewModels.QualityLadder.IsOriginal(qualityIdx) && !resolvedTargetMb.HasValue)
        {
            OverlayLayerCtl?.StopOverlay();
            processButton.IsEnabled = true;
            processButton.Content = "PROCESS";
            if (ActiveVideoHost != null) ActiveVideoHost.IsVisible = true;
            await ErrorReporter.ShowAsync(this, "Could not read the video", "Please reload the video and try again.", "");
            return;
        }

        RuntimeLog.Info("EXPORT",
            $"Quality tier '{FreeVideoStudio.App.ViewModels.QualityLadder.NameOf(qualityIdx)}' -> " +
            $"{(resolvedTargetMb.HasValue ? FreeVideoStudio.App.ViewModels.QualityLadder.FormatSize(resolvedTargetMb.Value) : "no size limit")} " +
            $"(worker quality level {resolvedQuality}).");

        bool musicLeadIn = true;
        bool musicTailOut = true;
        System.Collections.Generic.List<FreeVideoStudio.Core.Media.MusicTrack>? musicTracks = null;
        System.Text.Json.Nodes.JsonObject? musicConfig = null;

        var allSegments = BuildExportSpeedSegments();

        if (_musicWizardResult != null && !string.IsNullOrEmpty(_musicWizardResult.MusicFilePath) && System.IO.File.Exists(_musicWizardResult.MusicFilePath))
        {
            double musicStartMs = Math.Max(_trimStartMs, _musicWizardResult.TimelineStartSeconds * 1000.0);
            double musicEndMs = Math.Min(_trimEndMs > 0 ? _trimEndMs : _loadedVideoDurationMs, _musicWizardResult.TimelineEndSeconds * 1000.0);
            double startDelay = SourceMsToOutputSeconds(musicStartMs, allSegments);
            double outputEndSec = SourceMsToOutputSeconds(musicEndMs, allSegments);
            double dur = outputEndSec - startDelay;
            if (dur <= 0) dur = 1.0;
            
            const double MarkerToleranceMs = 50.0;
            double rawMusicStartMs = _musicWizardResult.TimelineStartSeconds * 1000.0;
            double rawMusicEndMs = _musicWizardResult.TimelineEndSeconds * 1000.0;

            musicLeadIn = rawMusicStartMs <= _trimStartMs + MarkerToleranceMs;
            musicTailOut = rawMusicEndMs <= (_trimEndMs > 0 ? _trimEndMs : _loadedVideoDurationMs) + MarkerToleranceMs;
            
            musicTracks = new System.Collections.Generic.List<FreeVideoStudio.Core.Media.MusicTrack>();
            var musicPaths = new System.Collections.Generic.List<string>();
            if (_musicWizardResult.MusicFilePaths != null && _musicWizardResult.MusicFilePaths.Count > 0)
            {
                foreach(var p in _musicWizardResult.MusicFilePaths) if (!string.IsNullOrWhiteSpace(p) && System.IO.File.Exists(p)) musicPaths.Add(p);
            }
            else
            {
                musicPaths.Add(_musicWizardResult.MusicFilePath);
            }

            var musicDurations = new System.Collections.Generic.List<double>(musicPaths.Count);
            for (int i = 0; i < musicPaths.Count; i++)
            {
                double knownDuration = 0;
                if (_musicWizardResult.MusicDurationsSeconds != null && i < _musicWizardResult.MusicDurationsSeconds.Count)
                    knownDuration = _musicWizardResult.MusicDurationsSeconds[i];
                else if (i == 0)
                    knownDuration = _musicWizardResult.MusicDurationSeconds;

                if (knownDuration <= 0)
                {
                    knownDuration = await ProbeMusicDurationSecondsAsync(musicPaths[i]);
                }
                musicDurations.Add(knownDuration);
            }

            var bedPlan = FreeVideoStudio.Core.Media.MusicBedPlan.Build(
                musicPaths, musicDurations, _musicWizardResult.OffsetSeconds, dur, _musicWizardResult.LoopMusic, startDelay);
            foreach (var seg in bedPlan)
            {
                musicTracks.Add(new FreeVideoStudio.Core.Media.MusicTrack(seg.Path, seg.FileOffsetSec, seg.DurationSec, startDelay, true));
            }

            if (_musicWizardResult.LoopMusic && musicTracks.Count > musicPaths.Count)
            {
                RuntimeLog.Info("UI",
                    $"Loop music: repeated the {musicPaths.Count} selected track(s) to {musicTracks.Count} segment(s) to cover the video.");
            }

            musicConfig = new System.Text.Json.Nodes.JsonObject
            {
                ["ducking_enabled"] = _musicWizardResult.EnableDucking,
                ["ducking_threshold"] = _musicWizardResult.EnableDucking ? FreeVideoStudio.Core.Media.SidechainCompressNode.TunedThreshold : FreeVideoStudio.Core.Media.SidechainCompressNode.BypassThreshold,
                ["ducking_ratio"] = _musicWizardResult.EnableDucking ? FreeVideoStudio.Core.Media.SidechainCompressNode.TunedRatio : FreeVideoStudio.Core.Media.SidechainCompressNode.BypassRatio,
                ["main_vol"] = _musicWizardResult.VideoVolume,
                ["music_vol"] = _musicWizardResult.MusicVolume,
                ["carving_enabled"] = _musicWizardResult.EnableCarving
            };
        }
        else
        {
            musicConfig = new System.Text.Json.Nodes.JsonObject { ["main_vol"] = _musicWizardResult?.VideoVolume ?? 1.0 };
        }
        
        var payload = new FreeVideoStudio.App.Models.ExportPayload
        {
            InputPath = _loadedVideoPath,
            OutputDirectory = outputDirectory,
            TrimStartMs = _trimStartMs,
            TrimEndMs = _trimEndMs,
            LoadedVideoDurationMs = _loadedVideoDurationMs,
            BaseSpeed = _baseSpeed,
            ThumbnailSet = _thumbnailSet,
            ThumbnailPosMs = _thumbnailPosMs,
            HardwareMode = ResolveHardwareMode(),
            SourceMeasuredLufs = _sourceMeasuredLufs,
            ApplyLoudnessNormalization = _applyLoudnessNormalization,
            ApplyPeakFlattening = _applyPeakFlattening,
            IsMobileFormat = this.FindControl<Avalonia.Controls.ToggleSwitch>("PortraitModeCheckbox")?.IsChecked ?? true,
            EnableFades = this.FindControl<Avalonia.Controls.ToggleSwitch>("EnableFadeCheckbox")?.IsChecked ?? true,
            ShowTeammates = this.FindControl<Avalonia.Controls.ToggleSwitch>("TeammatesCheckbox")?.IsChecked ?? false,
            ShowSpectating = IsSpectating,
            MemeFile = memeFile,
            MemeAtStart = !string.IsNullOrWhiteSpace(memeFile) &&
                          Infrastructure.MemePlacementStore.Get(memeFile!) == Infrastructure.MemePlacement.Start,
            PortraitText = this.FindControl<Avalonia.Controls.TextBox>("PortraitTextInput")?.Text,
            SpeedSegments = allSegments,
            Cuts = new List<FreeVideoStudio.Core.Media.CutRange>(_cuts),

            MemePlacements = _memePlacements.Count > 0
                ? new List<FreeVideoStudio.Core.Media.MemePlacement>(_memePlacements)
                : null,
            QualityLevel = resolvedQuality,
            TargetMbOverride = resolvedTargetMb,
            MusicLeadFadeIn = musicLeadIn,
            MusicTailFadeOut = musicTailOut,
            MusicTracks = musicTracks,
            MusicConfig = musicConfig,
            KeepMusicDuringMeme = _keepMusicDuringMeme ?? false,
            VoiceOverWavPath = _voiceOverResult?.VoiceOverWavPath,
            VoiceOverStartSec = _voiceOverResult?.VoiceOverStartTimestampSec ?? 0.0,
            VoiceOverTakes = _voiceOverResult != null ? GetExistingVoiceOverTakes(_voiceOverResult) : null,
            VoiceOverDuckAudio = _voiceOverResult?.DuckAudio ?? false,
            VoiceOverProtectFromMusic = _voiceOverResult?.ProtectFromMusic ?? false
        };

        var controller = new FreeVideoStudio.App.Services.MainMediaController();
        
        var result = await controller.ExecuteExportAsync(
            payload, 
            processCts.Token,
            (percent) => { Avalonia.Threading.Dispatcher.UIThread.Post(() => processButton.Content = $"PROCESSING... {percent}%"); },
            (phase, title, progress) => { 
                Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                    OverlayLayerCtl?.UpdatePhase(phase, title, progress));
            }
        );

        OverlayLayerCtl?.StopOverlay();
        if (ActiveVideoHost != null) ActiveVideoHost.IsVisible = true;

        if (result.Canceled)
        {
            processButton.IsEnabled = true;
            processButton.Content = "PROCESS";
            return;
        }

        if (result.Success)
        {
            ShowTacticalFeedback(result.Warning == null ? "Processing complete" : "Complete — thumbnail failed");
            PlayUiSound();

            ClearOverlayTextForNextVideo("the video finished processing");

            _exportedCleanSinceLastEdit = true;
            _recovery.ClearState();
            RuntimeLog.Info("RECOVERY", "Export completed successfully - recovery state cleared; no crash prompt is due for this session.");

            var dlg = new FreeVideoStudio.App.Controls.FinishedDialogWindow();
            dlg.SetOutputPath(result.OutputPath ?? string.Empty);
            await dlg.ShowDialog(this);

            if (result.Warning != null && dlg.DialogResult != 1)
            {
                await ErrorReporter.ShowAsync(this, "Thumbnail not created", result.Warning, "");
            }

            if (dlg.DialogResult == 1) Close();
            else if (dlg.DialogResult == 2)
            {
                ResetProjectStateToUpload();
                OnUploadVideoClicked(null, new Avalonia.Interactivity.RoutedEventArgs());
            }
        }
        else
        {
            ShowTacticalFeedback("Processing failed");
            PlayUiSound();
            if (result.Failure != null)
            {
                await ErrorReporter.ShowAsync(this, result.Failure);
            }
            else
            {
                await ErrorReporter.ShowAsync(this, "Export failed", "The video could not be exported.", result.ErrorMessage);
            }
        }
        
        processButton.IsEnabled = true;
        processButton.Content = "PROCESS";
    }

    /// <summary>
    /// ISSUE_08 — the export graph gives every speed/freeze chunk its own parallel FFmpeg
    /// branch, and FFmpeg holds frames in memory for every branch `concat` has not read yet.
    /// Past a certain number of segments that becomes a lot of RAM, so ask before committing
    /// rather than letting the machine discover it mid-encode.
    /// Returns true if the export should proceed.
    /// </summary>
    private async Task<bool> ConfirmHighSegmentCountAsync()
    {
        var segments = BuildExportSpeedSegments();
        int segmentCount = segments?.Count ?? 0;

        int cutChunks = 0;
        if (_cuts.Count > 0)
        {
            EnsureTrimPointsSet();
            var relCuts = FreeVideoStudio.Core.Media.CutRange.ToClipRelative(_cuts, _trimStartMs);
            cutChunks = FreeVideoStudio.Core.Media.OutputTimeline
                .Create(_trimEndMs - _trimStartMs, null, _baseSpeed, _trimStartMs, null, relCuts)
                .ExtraChunkCost();
        }

        int estimatedChunks = (segmentCount * 2) + 1 + cutChunks;
        if (estimatedChunks <= FreeVideoStudio.Core.Media.GranularSpeedBuilder.HighChunkCountWarnThreshold)
        {
            return true;
        }

        RuntimeLog.Info("Process",
            $"High segment count before export: {segmentCount} segment(s) -> ~{estimatedChunks} chunks. Asking the user to confirm.");

        var dlg = new FreeVideoStudio.App.Controls.ConfirmDialogWindow();
        dlg.SetTitle("A lot of speed segments");
        dlg.SetMessage(
            $"This edit has {segmentCount} speed/freeze segments." + Environment.NewLine + Environment.NewLine +
            "Each one becomes its own parallel stream inside the encoder, and the encoder holds " +
            "frames in memory for every stream it has not reached yet. With this many segments the " +
            "export can use a large amount of RAM and may be slow." + Environment.NewLine + Environment.NewLine +
            "Export anyway?");
        dlg.SetButtonText("EXPORT ANYWAY", "GO BACK");
        await dlg.ShowDialog(this);

        if (!dlg.Result)
        {
            RuntimeLog.Info("Process", "User backed out of a high-segment-count export.");
            ShowTacticalFeedback("Export cancelled");
        }

        return dlg.Result;
    }

}
