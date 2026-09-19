// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
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
using FortniteVideoSoftware.App.Controls;
using FortniteVideoSoftware.App.Infrastructure;
using FortniteVideoSoftware.App.Models;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App;

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

            // Disposed only now: every registration ProcessWorker took on this token is released by
            // the time its Task has completed.
            try { cts.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }

            this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer")?.StopOverlay();
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

        // EXPORTSESSION_01 — the CancellationTokenSource is created and disposed by the wrapper
        // above, which is the only code that knows when this pipeline has actually stopped. The old
        // `previousCts?.Dispose()` that stood here disposed a source the PREVIOUS export's worker was
        // still registered on; see the block on ProcessVideoAsync. Do not reintroduce it.
        await Task.Yield();
        
        SetTimelinePopupsVisible(false);
        if (ActiveVideoHost != null) ActiveVideoHost.IsVisible = false;
        this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer")?.StartOverlay();

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

        
        // ══════════════════════════════════════════════════════════════════════════════════════
        // QUALITY_01 — THE WORKER'S CONTRACT DID NOT CHANGE. Only who computes the number did.
        //
        // `resolvedTargetMb` has always been "the size to aim for, or null for constant quality",
        // and it still is. What changed is that it used to BE the dial's value (5 + idx * 5) and
        // is now DERIVED from the quality tier the dial selects — through the same estimator
        // that produces the readout beside PROCESS, so the user is never promised
        // one size and handed another.
        // ══════════════════════════════════════════════════════════════════════════════════════
        int qualityIdx = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("QualitySlider")?.Value
                         ?? FortniteVideoSoftware.App.ViewModels.QualityLadder.DefaultIndex;
        // ⚠️ QUALITY_02 — THE WORKER IS NOT GIVEN THE TIER INDEX. It is given the quality LEVEL
        // its own VideoConfig.GetQualitySettings understands, where ">= 20" is what unlocks
        // keepHighestRes and the constant-quality path. Passing the raw tier here would strip
        // `Original` of the one thing it promises. See QualityLadder.ToWorkerQualityLevel.
        int resolvedQuality = FortniteVideoSoftware.App.ViewModels.QualityLadder.ToWorkerQualityLevel(qualityIdx);
        // SIZEESTIMATE_01 — recapture current inputs; never export using a stale displayed result.
        // Original's rough prediction remains separate from its null (uncapped) encoder target.
        var sizeRequest = CaptureSizeRequest();
        Services.OutputSizeEstimate sizeEstimate;
        try
        {
            var estimateToken = processCts.Token;
            sizeEstimate = await Task.Run(() => SizeEstimator.EstimateMainAsync(sizeRequest, estimateToken), estimateToken);
        }
        catch (OperationCanceledException)
        {
            this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer")?.StopOverlay();
            processButton.IsEnabled = true;
            processButton.Content = "PROCESS";
            if (ActiveVideoHost != null) ActiveVideoHost.IsVisible = true;
            return;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("SIZE ESTIMATE", ex);
            sizeEstimate = Services.OutputSizeEstimate.Empty;
        }
        double? resolvedTargetMb = sizeEstimate.TargetMegabytes;
        if (!FortniteVideoSoftware.App.ViewModels.QualityLadder.IsOriginal(qualityIdx) && !resolvedTargetMb.HasValue)
        {
            this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer")?.StopOverlay();
            processButton.IsEnabled = true;
            processButton.Content = "PROCESS";
            if (ActiveVideoHost != null) ActiveVideoHost.IsVisible = true;
            await ErrorReporter.ShowAsync(this, "Could not read the video", "Please reload the video and try again.", "");
            return;
        }

        RuntimeLog.Info("EXPORT",
            $"Quality tier '{FortniteVideoSoftware.App.ViewModels.QualityLadder.NameOf(qualityIdx)}' -> " +
            $"{(resolvedTargetMb.HasValue ? FortniteVideoSoftware.App.ViewModels.QualityLadder.FormatSize(resolvedTargetMb.Value) : "no size limit")} " +
            $"(worker quality level {resolvedQuality}).");

        bool musicLeadIn = true;
        bool musicTailOut = true;
        System.Collections.Generic.List<FortniteVideoSoftware.Core.Media.MusicTrack>? musicTracks = null;
        System.Text.Json.Nodes.JsonObject? musicConfig = null;

        var allSegments = BuildExportSpeedSegments();

        if (_musicWizardResult != null && !string.IsNullOrEmpty(_musicWizardResult.MusicFilePath) && System.IO.File.Exists(_musicWizardResult.MusicFilePath))
        {
            double musicStartMs = Math.Max(_trimStartMs, _musicWizardResult.TimelineStartSeconds * 1000.0);
            double musicEndMs = Math.Min(_trimEndMs > 0 ? _trimEndMs : _loadedVideoDurationMs, _musicWizardResult.TimelineEndSeconds * 1000.0);
            // ══════════════════════════════════════════════════════════════════════════
            // CUTS_02 — MUSIC IS PLACED IN OUTPUT TIME, AND OUTPUT TIME IS SHORTER AFTER A CUT.
            //
            // These two numbers are where in the finished video the music starts and stops.
            // CalculateEffectiveDurationMs converts source ms to output ms through the speed
            // segments — but it knew nothing about deleted sections, so every second removed
            // before the music's start point was still counted as time the music had to wait
            // through. A thirty-second cut near the top of a clip pushed the whole bed thirty
            // seconds late, and stretched its length by the same amount over the end of the video.
            //
            // OutputTimeline is the one type that owns this conversion and it already handles
            // cuts, so the mapping is delegated to it rather than adding a second implementation.
            // ══════════════════════════════════════════════════════════════════════════
            double startDelay = SourceMsToOutputSeconds(musicStartMs, allSegments);
            double outputEndSec = SourceMsToOutputSeconds(musicEndMs, allSegments);
            double dur = outputEndSec - startDelay;
            if (dur <= 0) dur = 1.0;
            
            const double MarkerToleranceMs = 50.0;
            double rawMusicStartMs = _musicWizardResult.TimelineStartSeconds * 1000.0;
            double rawMusicEndMs = _musicWizardResult.TimelineEndSeconds * 1000.0;

            musicLeadIn = rawMusicStartMs <= _trimStartMs + MarkerToleranceMs;
            musicTailOut = rawMusicEndMs <= (_trimEndMs > 0 ? _trimEndMs : _loadedVideoDurationMs) + MarkerToleranceMs;
            
            musicTracks = new System.Collections.Generic.List<FortniteVideoSoftware.Core.Media.MusicTrack>();
            var musicPaths = new System.Collections.Generic.List<string>();
            if (_musicWizardResult.MusicFilePaths != null && _musicWizardResult.MusicFilePaths.Count > 0)
            {
                foreach(var p in _musicWizardResult.MusicFilePaths) if (!string.IsNullOrWhiteSpace(p) && System.IO.File.Exists(p)) musicPaths.Add(p);
            }
            else
            {
                musicPaths.Add(_musicWizardResult.MusicFilePath);
            }

            // Probe once and keep the lengths: the loop pass below needs them again, and
            // ProbeMusicDurationSecondsAsync spawns ffprobe.
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

            for (int i = 0; i < musicPaths.Count; i++)
            {
                double offset = i == 0 ? _musicWizardResult.OffsetSeconds : 0.0;
                double knownDuration = musicDurations[i];

                double availableDuration = knownDuration > 0 ? Math.Max(0.0, knownDuration - offset) : dur;
                double takeDuration = Math.Min(dur, availableDuration);
                if (takeDuration <= 0.01) continue;

                musicTracks.Add(new FortniteVideoSoftware.Core.Media.MusicTrack(musicPaths[i], offset, takeDuration, startDelay, true));
                dur -= takeDuration;
                if (dur <= 0.01) break;
            }

            // ══════════════════════════════════════════════════════════════════════════════
            // LOOP_01 — "LOOP MUSIC UNTIL VIDEO ENDS" NOW ACTUALLY DOES THAT HERE.
            //
            // The tick box has always existed in the Music Wizard, and `LoopMusic` has always been
            // carried on the result and written into the recovery file. But ONLY MergerWorker ever
            // read it (via `loop_music` in its own music config). On this path — a single video in
            // the Main App — nothing consumed it, so the tick box was decorative: the music still
            // stopped when the song ran out. That was survivable while the box was hidden outside
            // the Video Merger; now that COVER_01 shows it to everyone, it has to be real.
            //
            // The repeat is expressed the same way the first pass is — more MusicTrack entries,
            // each taking a slice of what is left — so the filter graph sees nothing new. Repeats
            // start at 0.0 rather than the user's chosen song start: the start point was chosen as
            // an ENTRANCE, and re-entering there each cycle would skip the same opening every time.
            // The guard is a hard stop against a zero-length track spinning this forever.
            // ══════════════════════════════════════════════════════════════════════════════
            if (_musicWizardResult.LoopMusic && dur > 0.01 && musicPaths.Count > 0)
            {
                int loopGuard = 0;
                while (dur > 0.01 && loopGuard++ < 500)
                {
                    bool addedAnything = false;
                    for (int i = 0; i < musicPaths.Count && dur > 0.01; i++)
                    {
                        double full = musicDurations[i];
                        if (full <= 0.01) continue;

                        double takeDuration = Math.Min(dur, full);
                        if (takeDuration <= 0.01) continue;

                        musicTracks.Add(new FortniteVideoSoftware.Core.Media.MusicTrack(musicPaths[i], 0.0, takeDuration, startDelay, true));
                        dur -= takeDuration;
                        addedAnything = true;
                    }
                    if (!addedAnything) break;   // every track is unreadable or zero-length
                }

                RuntimeLog.Info("UI",
                    $"Loop music: repeated the {musicPaths.Count} selected track(s) to {musicTracks.Count} segment(s) to cover the video.");
            }

            musicConfig = new System.Text.Json.Nodes.JsonObject
            {
                // DUCKOFF_01 — the EXPLICIT flag is what AudioFilterChain reads now. The
                // threshold/ratio below stay for the checked case (and as the legacy fallback for
                // configs written before this key existed).
                ["ducking_enabled"] = _musicWizardResult.EnableDucking,
                ["ducking_threshold"] = _musicWizardResult.EnableDucking ? FortniteVideoSoftware.Core.Media.SidechainCompressNode.TunedThreshold : FortniteVideoSoftware.Core.Media.SidechainCompressNode.BypassThreshold,
                ["ducking_ratio"] = _musicWizardResult.EnableDucking ? FortniteVideoSoftware.Core.Media.SidechainCompressNode.TunedRatio : FortniteVideoSoftware.Core.Media.SidechainCompressNode.BypassRatio,
                ["main_vol"] = _musicWizardResult.VideoVolume,
                ["music_vol"] = _musicWizardResult.MusicVolume,
                ["carving_enabled"] = _musicWizardResult.EnableCarving
            };
        }
        else
        {
            // The main bubble slider is a monitor control, never an export gain.
            musicConfig = new System.Text.Json.Nodes.JsonObject { ["main_vol"] = _musicWizardResult?.VideoVolume ?? 1.0 };
        }
        
        var payload = new FortniteVideoSoftware.App.Models.ExportPayload
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
            // CUT_01 — absolute source ms, converted to clip-relative inside ProcessWorker.
            Cuts = new List<FortniteVideoSoftware.Core.Media.CutRange>(_cuts),

            // ══════════════════════════════════════════════════════════════════════════
            // MEME_06 — THE LINE THAT MAKES MID-VIDEO MEMES REAL.
            //
            // MEME_05 built the entire N-meme export graph and shipped it, and then nothing on any
            // screen ever assigned this property — so every export in the product fell through to
            // ProcessWorker's legacy "one meme, start or end" branch and the whole engine sat
            // unreachable. Non-empty here makes it authoritative; empty keeps the legacy pair,
            // which is exactly how the main screen's Start/End dropdown still works.
            // ══════════════════════════════════════════════════════════════════════════
            MemePlacements = _memePlacements.Count > 0
                ? new List<FortniteVideoSoftware.Core.Media.MemePlacement>(_memePlacements)
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

        var controller = new FortniteVideoSoftware.App.Services.MainMediaController();
        
        var result = await controller.ExecuteExportAsync(
            payload, 
            processCts.Token,
            (percent) => { Avalonia.Threading.Dispatcher.UIThread.Post(() => processButton.Content = $"PROCESSING... {percent}%"); },
            (phase, title, progress) => { 
                Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                    this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer")?.UpdatePhase(phase, title, progress));
            }
        );

        this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer")?.StopOverlay();
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

            // CAPTIONWIPE_01 — this video is finished, so its title is finished with it. MUST run
            // BEFORE the clean flag below: writing OverlayText fires NotifyStateDirty, which would
            // clear that flag again and arm crash recovery for a project that has just been
            // exported successfully. See MainWindow.ClearOverlayTextForNextVideo.
            ClearOverlayTextForNextVideo("the video finished processing");

            _exportedCleanSinceLastEdit = true;
            _recovery.ClearState();
            RuntimeLog.Info("RECOVERY", "Export completed successfully - recovery state cleared; no crash prompt is due for this session.");

            var dlg = new FortniteVideoSoftware.App.Controls.FinishedDialogWindow();
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

        // CUT_01 — a cut splits one stretch of footage in two, so it costs ONE extra branch: half
        // what a speed segment costs (which adds itself plus the gap after it). A cut touching the
        // very start or end of the clip splits nothing and is free, which is what ExtraChunkCost
        // works out. Counting cuts here is what keeps "many tiny snips" — the one real performance
        // danger of this feature — in front of the user before the encode starts rather than after.
        int cutChunks = 0;
        if (_cuts.Count > 0)
        {
            EnsureTrimPointsSet();
            var relCuts = FortniteVideoSoftware.Core.Media.CutRange.ToClipRelative(_cuts, _trimStartMs);
            cutChunks = FortniteVideoSoftware.Core.Media.OutputTimeline
                .Create(_trimEndMs - _trimStartMs, null, _baseSpeed, _trimStartMs, null, relCuts)
                .ExtraChunkCost();
        }

        int estimatedChunks = (segmentCount * 2) + 1 + cutChunks;
        if (estimatedChunks <= FortniteVideoSoftware.Core.Media.GranularSpeedBuilder.HighChunkCountWarnThreshold)
        {
            return true;
        }

        RuntimeLog.Info("Process",
            $"High segment count before export: {segmentCount} segment(s) -> ~{estimatedChunks} chunks. Asking the user to confirm.");

        var dlg = new FortniteVideoSoftware.App.Controls.ConfirmDialogWindow();
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
