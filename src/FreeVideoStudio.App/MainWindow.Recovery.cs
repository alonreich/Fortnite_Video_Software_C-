
using System;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App;

/// <summary>
/// EDITHOT_02 â€” the main window's edit hook (undo push + crash-recovery write-behind). It was moved
/// out of MainWindow.axaml.cs under MVVM_02 ("new behaviour goes in a new file"). The call sites
/// and the UNDO_20 / RECOVERY_03-04 contracts documented on HasUnsavedWork are unchanged.
/// </summary>
public partial class MainWindow
{
    /// <param name="label">UNDOEQ_02: the action, as the undo menu will name it ("change speed").</param>
    /// <param name="gestureKey">
    /// UNDOEQ_02: pass the SAME key on every tick of one continuous control (a dial sweep, a key
    /// repeat) so U1 collapses the whole gesture into ONE undo step. End it explicitly with
    /// EndProjectGesture() on release. Null for discrete actions (clicks, toggles, releases).
    /// </param>
    private void SaveRecoveryState(bool sync = false, bool isUserEdit = true, string label = "edit", string? gestureKey = null)
    {
        if (_isRestoring) return;
        if (isUserEdit) UpdateEstimatedQuality();

        if (isUserEdit)
        {
            PushProjectEdit(label, gestureKey);
            ProjectAutosaveTick();
        }

        if (isUserEdit && _exportedCleanSinceLastEdit)
        {
            _exportedCleanSinceLastEdit = false;
            RuntimeLog.Info("RECOVERY", "Edit made after a successful export - project is dirty again, recovery re-armed.");
        }

        if (sync)
        {
            _recoveryWriteBehindTimer?.Stop();
            PersistRecoveryStateNow(sync: true);
            return;
        }

        if (_recoveryWriteBehindTimer == null)
        {
            _recoveryWriteBehindTimer = new Avalonia.Threading.DispatcherTimer(Avalonia.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(RecoveryWriteBehindMs)
            };
            _recoveryWriteBehindTimer.Tick += (_, _) =>
            {
                _recoveryWriteBehindTimer!.Stop();
                if (_isRestoring) return;
                PersistRecoveryStateNow(sync: false);
            };
        }
        _recoveryWriteBehindTimer.Stop();
        _recoveryWriteBehindTimer.Start();
    }

    /// <summary>EDITHOT_01 â€” the volume preference write, off the UI thread. Never throws.</summary>
    private async Task PersistMainVolumeAsync(double volume)
    {
        try
        {
            await new FreeVideoStudio.Core.Ipc.StateTransferStore(_paths)
                .UpdatePropertiesAsync(new System.Text.Json.Nodes.JsonObject { ["MainVolume"] = volume })
                .ConfigureAwait(false);
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private const int RecoveryWriteBehindMs = 750;
    private Avalonia.Threading.DispatcherTimer? _recoveryWriteBehindTimer;

    /// <summary>
    /// EDITHOT_02 â€” the disk half of <see cref="SaveRecoveryState"/>: decide between clear and
    /// save, then hand the snapshot to the recovery writer. Snapshot building reads the view-models,
    /// so it runs on the UI thread, but only once per settled edit, never per tick.
    /// </summary>
    private void PersistRecoveryStateNow(bool sync)
    {
        try
        {
            if (!HasUnsavedWork())
            {
                _recoveryService.ClearState();
                return;
            }

            var document = _projectSession?.Capture(forExplicitSave: false);
            if (document is null || !FreeVideoStudio.Core.Project.RecoveryEnvelope.HasContent(document))
            {
                _recoveryService.ClearState();
                return;
            }

            RuntimeLog.Info("RECOVERY",
                $"Saving canonical project snapshot: video={System.IO.Path.GetFileName(document.Source.FilePath)} " +
                $"trim[{document.SourceCutStartMs:F0}ms +{document.TrimmedDurationMs:F0}ms] " +
                $"speed[base={document.BaseSpeed:F2}x segs={document.Segments.Count}] " +
                $"cuts[{document.Cuts.Count}] memes[{document.Memes.Count}] " +
                $"mask[{(document.Mask is null ? "none" : document.Mask.ProfileName)}] " +
                $"merge[{(document.Merge is null ? "none" : document.Merge.Clips.Count.ToString())}] " +
                $"music[{(document.Audio.MusicFilePath != null ? "yes" : "no")}] " +
                $"voiceOver[{(document.Audio.VoiceOverFilePath != null ? "yes" : "no")}] ");

            _recoveryService.SaveState(document, BuildRecoveryTransientMetadata(), sync);
        }
        catch (System.Exception ex)
        {
            RuntimeLog.Fail("RECOVERY", ex);
        }
    }

    /// <summary>
    /// RECOVERYDOC_01 - the crash-only metadata that travels BESIDE the document: the preview
    /// volume, thumbnail frame, freeze preview, HUD tool selections, voice-over takes metadata and
    /// the music wizard's working state. NONE of this belongs in a saved project (it describes
    /// this session, not the work - see RecoveryEnvelope.Write and docs/06 PROJ_06), which is
    /// exactly why it goes into the envelope's transient object and nowhere else.
    /// </summary>
    private System.Text.Json.Nodes.JsonObject? BuildRecoveryTransientMetadata()
    {
        try
        {
            var mainVm = _viewModel;
            var timelineVm = _viewModel.Timeline;

            var transient = new System.Text.Json.Nodes.JsonObject
            {
                ["thumbnailPosMs"] = timelineVm.ThumbnailPosMs,
                ["thumbnailSet"] = timelineVm.IsThumbnailSet,
                ["isGranularSpeedActive"] = mainVm.IsGranularSpeedActive,
                ["volume"] = mainVm.MainVolume,
                ["showTeammates"] = mainVm.IsTeammates,
                ["showSpectating"] = mainVm.IsSpectating,
                ["enableFade"] = mainVm.IsEnableFade,
                ["addMeme"] = mainVm.IsAddMeme,
                ["memeFile"] = mainVm.SelectedMemeItem?.FileName ?? "",
                ["memeFilePath"] = mainVm.SelectedMemeItem?.FullPath ?? "",
                ["portraitText"] = mainVm.OverlayText ?? "",
                ["freezeTimeMs"] = timelineVm.FreezeTimeMs,
                ["freezeDurationS"] = timelineVm.FreezeDurationS,
            };

            if (mainVm.VoiceOverResult != null)
            {
                var takeArray = new System.Text.Json.Nodes.JsonArray();
                var takes = mainVm.VoiceOverResult.VoiceOverTakes;
                if (takes != null)
                {
                    foreach (var take in takes)
                    {
                        if (take is null || string.IsNullOrWhiteSpace(take.Path) || !System.IO.File.Exists(take.Path)) continue;
                        takeArray.AddNode(new System.Text.Json.Nodes.JsonObject
                        {
                            ["path"] = take.Path,
                            ["startSec"] = take.StartSec
                        });
                    }
                }
                transient["voiceOverTakes"] = takeArray;
                transient["voiceOverDuckAudio"] = mainVm.VoiceOverResult.DuckAudio;
                transient["voiceOverProtectFromMusic"] = mainVm.VoiceOverResult.ProtectFromMusic;
            }

            if (mainVm.MusicWizardResult != null)
            {
                var wizard = mainVm.MusicWizardResult;
                var pathsArray = new System.Text.Json.Nodes.JsonArray();
                if (wizard.MusicFilePaths != null)
                    foreach (string path in wizard.MusicFilePaths)
                        pathsArray.AddNode(System.Text.Json.Nodes.JsonValue.Create(path));

                var durationsArray = new System.Text.Json.Nodes.JsonArray();
                if (wizard.MusicDurationsSeconds != null)
                    foreach (double durationSec in wizard.MusicDurationsSeconds)
                        durationsArray.AddNode(System.Text.Json.Nodes.JsonValue.Create(durationSec));

                transient["musicResult"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["musicFilePath"] = wizard.MusicFilePath,
                    ["musicFilePaths"] = pathsArray,
                    ["musicDurationsSeconds"] = durationsArray,
                    ["offsetSeconds"] = wizard.OffsetSeconds,
                    ["timelineStartSeconds"] = wizard.TimelineStartSeconds,
                    ["timelineEndSeconds"] = wizard.TimelineEndSeconds,
                    ["musicDurationSeconds"] = wizard.MusicDurationSeconds,
                    ["enableDucking"] = wizard.EnableDucking,
                    ["enableCarving"] = wizard.EnableCarving,
                    ["videoVolume"] = wizard.VideoVolume,
                    ["musicVolume"] = wizard.MusicVolume,
                    ["loopMusic"] = wizard.LoopMusic
                };
            }

            return transient;
        }
        catch (System.Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return null;
        }
    }
}
