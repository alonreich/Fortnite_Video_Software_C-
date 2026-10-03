// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Models;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Project;

namespace FreeVideoStudio.App;

/// <summary>
/// RECOVERYDOC_10 - THE RESTORE SIDE OF THE ONE-CANONICAL-MODEL CONTRACT.
///
/// <para>
/// This file used to be the SECOND implementation of project loading: a 470-line, field-by-field
/// reconstruction of application state out of a parallel recovery JSON, drifting from the normal
/// open path in exactly the way the old recovery WRITER drifted from the normal save path. The
/// project itself is now applied through the canonical path (ProjectSession.OpenDocument - the
/// tail of Open Project minus the file picker), so the project surface cannot drift between
/// save, save-again and crash-restore.
/// </para>
///
/// <para>
/// What remains HERE is the crash-only transient state that deliberately is NOT part of a project
/// document: the thumbnail frame, the freeze preview, HUD tool selections, the voice-over takes
/// and protection flags, the music wizard's working state, and the in-flight granular editor
/// session (granular_session, preserved by RecoveryManager beside the envelope). Everything about
/// the user's WORK - source, trims, speed segments, cuts, memes, audio, export settings, mask,
/// merge - arrives inside the ProjectDocument and never below.
/// </para>
/// </summary>
public partial class MainWindow
{
    private async Task RestoreRecoveryStateAsync()
    {
        _isRestoring = true;
        try
        {
            var snapshot = _recoveryService.LoadSnapshot();
            if (snapshot == null)
            {
                RuntimeLog.Info("RECOVERY", "No recovery state file found. Nothing to restore.");
                return;
            }

            ProjectDocument? document;
            JsonObject? transient;
            if (snapshot.IsLegacy)
            {
                RuntimeLog.Info("RECOVERY",
                    "Legacy recovery format detected - migrating once to the canonical ProjectDocument. " +
                    "Note: the legacy format never recorded the HUD mask or the merger queue, so those " +
                    "start empty; everything else is restored, and the next autosave stores all of it.");
                if (!Services.LegacyRecoveryMigrator.TryMigrate(snapshot.LegacyRaw!, out document, out transient, out string? legacyError))
                {
                    RuntimeLog.Fail("RECOVERY", $"Legacy recovery state unusable, discarding it: {legacyError}");
                    _recovery.ClearState();
                    return;
                }
            }
            else
            {
                document = snapshot.Document!;
                transient = snapshot.TransientMetadata;
            }

            RuntimeLog.Info("RECOVERY", "Beginning canonical state restoration...");

            if (document is null)
            {
                RuntimeLog.Fail("RECOVERY", "The recovery snapshot resolved to no project document.");
                _recovery.ClearState();
                return;
            }

            bool applied = _projectSession?.OpenDocument(document, allowMissingSource: true) ?? false;
            if (!applied)
            {
                RuntimeLog.Fail("RECOVERY", "The recovered project document could not be applied to the editor.");
                _recovery.ClearState();
                return;
            }

            var state = transient ?? new JsonObject();

            _thumbnailPosMs = state["thumbnailPosMs"]?.GetValue<double>() ?? 0;
            _thumbnailSet = state["thumbnailSet"]?.GetValue<bool>() ?? _thumbnailPosMs > 0;

            // THUMB_01 — one owner for this button's appearance. The hand-rolled copy here always
            // came back saying REMOVE THUMBNAIL, which after the change above is only true when the
            // playhead happens to be sitting on the thumbnail — and on a fresh restore it is at the
            // trim start, not on it. The recovered project would have offered to delete the cover
            // frame it had just restored.
            UpdateThumbnailButtonState();

            var markStartBtn = MarkStartButtonCtl;
            if (markStartBtn != null && document.SourceCutStartMs > 0.001)
                markStartBtn.Content = $"START: {FormatTime(TimeSpan.FromMilliseconds(document.SourceCutStartMs))}";
            var markEndBtn = MarkEndButtonCtl;
            if (markEndBtn != null && document.TrimmedDurationMs > 0.001)
                markEndBtn.Content = $"END: {FormatTime(TimeSpan.FromMilliseconds(document.SourceCutStartMs + document.TrimmedDurationMs))}";

            var speedSlider = MainSpeedSliderCtl;
            if (speedSlider != null) speedSlider.Value = (int)Math.Round(_baseSpeed * 10.0, MidpointRounding.AwayFromZero);

            // QUALITY_01 — the stored index means a TIER now. An index written by an older build
            // meant megabytes; ClampIndex (in the setter) keeps it in range rather than failing,
            // and the old top stop "ORIGINAL QUALITY" still lands on the new top stop "Original".
            int qualityVal = document.Export.QualityIndex >= 0
                ? document.Export.QualityIndex
                : ViewModels.QualityLadder.DefaultIndex;
            var qualitySliderRestore = QualitySliderCtl;
            if (qualitySliderRestore != null) qualitySliderRestore.Value = qualityVal;

            _freezeTimeMs = state["freezeTimeMs"]?.GetValue<double>() ?? -1;
            _freezeDurationS = state["freezeDurationS"]?.GetValue<double>() ?? 1.0;
            if (_freezeTimeMs >= 0)
                RuntimeLog.Info("RECOVERY", $"Crash Recovery Restore: Successfully reinstated Freeze parameters [Timestamp={_freezeTimeMs}ms, Duration={_freezeDurationS}s]");

            // CUT_01 — a session saved before cuts existed has no "cuts" key; the list simply
            // stays empty and the clip behaves exactly as it always did.
            bool hasGranular = _speedSegments.Count > 0 || _freezeTimeMs >= 0;
            SetGranularButtonActive(hasGranular || (state["isGranularSpeedActive"]?.GetValue<bool>() ?? false));

            // MEME_06 — additive and absent-is-legal: a recovery file written before memes existed
            // simply has no "memePlacements" key and restores with none, which is exactly the
            // project that build produced.
            if (snapshot.Raw.TryGetPropertyValue("granular_session", out var granularNode) && granularNode is JsonObject granularSession)
            {
                bool isOpen = granularSession["open"]?.GetValue<bool>() ?? false;
                string? gVideo = granularSession["video_path"]?.ToString();
                double gTrimStart = granularSession["trim_start_ms"]?.GetValue<double>() ?? -1;
                double gTrimEnd = granularSession["trim_end_ms"]?.GetValue<double>() ?? -1;

                if (isOpen && !string.IsNullOrEmpty(gVideo)
                    && string.Equals(gVideo, document.Source.FilePath, StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(gTrimStart - _trimStartMs) <= 1.0
                    && Math.Abs(gTrimEnd - _trimEndMs) <= 1.0)
                {
                    RuntimeLog.Info("RECOVERY", "In-flight Granular Speed Editor session detected from crash. Reinstating live granular edits.");

                    if (granularSession.ContainsKey("base_speed") && granularSession["base_speed"] is JsonNode bsNode)
                    {
                        double gSpeed = bsNode.GetValue<double>();
                        if (!double.IsNaN(gSpeed) && gSpeed >= 0.1 && gSpeed <= 40.0)
                        {
                            _baseSpeed = gSpeed;
                            if (speedSlider != null) speedSlider.Value = (int)Math.Round(_baseSpeed * 10.0, MidpointRounding.AwayFromZero);
                        }
                    }

                    if (granularSession.ContainsKey("freeze_time_ms"))
                        _freezeTimeMs = granularSession["freeze_time_ms"]?.GetValue<double>() ?? -1;
                    if (granularSession.ContainsKey("freeze_duration_s"))
                        _freezeDurationS = granularSession["freeze_duration_s"]?.GetValue<double>() ?? 1.0;

                    RestoreGranularSessionTimeline(granularSession);
                }
            }

            _musicWizardResult = null;
            if (state["musicResult"] is JsonObject musicObj)
            {
                _musicWizardResult = new MusicWizardResult
                {
                    MusicFilePath = musicObj["musicFilePath"]?.ToString() ?? "",
                    OffsetSeconds = musicObj["offsetSeconds"]?.GetValue<double>() ?? 0.0,
                    TimelineStartSeconds = musicObj["timelineStartSeconds"]?.GetValue<double>() ?? 0.0,
                    TimelineEndSeconds = musicObj["timelineEndSeconds"]?.GetValue<double>() ?? 0.0,
                    MusicDurationSeconds = musicObj["musicDurationSeconds"]?.GetValue<double>() ?? 0.0,
                    EnableDucking = musicObj["enableDucking"]?.GetValue<bool>() ?? true,
                    EnableCarving = musicObj["enableCarving"]?.GetValue<bool>() ?? true,
                    VideoVolume = musicObj["videoVolume"]?.GetValue<double>() ?? 1.0,
                    MusicVolume = musicObj["musicVolume"]?.GetValue<double>() ?? 1.0,
                    LoopMusic = musicObj["loopMusic"]?.GetValue<bool>() ?? false
                };
                if (musicObj["musicFilePaths"] is JsonArray pathsArr)
                {
                    foreach (var node in pathsArr)
                    {
                        var path = node?.ToString();
                        if (!string.IsNullOrEmpty(path)) _musicWizardResult.MusicFilePaths.Add(path);
                    }
                }
                if (musicObj["musicDurationsSeconds"] is JsonArray durationsArr)
                {
                    foreach (var node in durationsArr)
                    {
                        if (node != null && double.TryParse(node.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double durationSec))
                            _musicWizardResult.MusicDurationsSeconds.Add(durationSec);
                    }
                }
                NormalizeMusicPlacement(_musicWizardResult);
            }

            // EDIT3_02 — as above, and this one was worse than drift: assigning a plain string to
            // `Content` REPLACED the button's whole StackPanel, so a recovered project came back
            // with the two music-note icons gone. Going through the setter keeps the icons and
            // only swaps the label inside them.
            SetMusicButtonActive(_viewModel.IsMusicActive);

            double vol = state["volume"]?.GetValue<double>() ?? 100;
            var volSliderRestore = VolumeSliderCtl;
            if (volSliderRestore != null) volSliderRestore.Value = vol;

            var portraitCbRestore = PortraitModeCheckboxCtl;
            if (portraitCbRestore != null) portraitCbRestore.IsChecked = _viewModel.IsPortraitMode;

            bool showTeammates = state["showTeammates"]?.GetValue<bool>() ?? false;
            var teammatesCbRestore = TeammatesCheckboxCtl;
            if (teammatesCbRestore != null) teammatesCbRestore.IsChecked = showTeammates;

            bool showSpectating = state["showSpectating"]?.GetValue<bool>() ?? true;
            var spectatingCbRestore = SpectatingCheckboxCtl;
            if (spectatingCbRestore != null) spectatingCbRestore.IsChecked = showSpectating;

            var enableFadeCbRestore = EnableFadeCheckboxCtl;
            if (enableFadeCbRestore != null && state.ContainsKey("enableFade"))
                enableFadeCbRestore.IsChecked = state["enableFade"]?.GetValue<bool>() ?? true;

            string portraitText = (string?)state["portraitText"] ?? "";
            var portraitTextRestore = PortraitTextInputCtl;
            if (portraitTextRestore != null) portraitTextRestore.Text = portraitText;

            bool addMeme = state["addMeme"]?.GetValue<bool>() ?? false;
            var addMemeCbRestore = AddMemeCheckboxCtl;
            if (addMemeCbRestore != null) addMemeCbRestore.IsChecked = addMeme;

            // MEMEFOLDER_02 — a meme saved under the old spaced folder resolves to where it moved.
            string memeFilePath = FreeVideoStudio.Core.Infrastructure.MigrationPathResolver.ResolveSavedFile((string?)state["memeFilePath"] ?? "");
            string memeFile = (string?)state["memeFile"] ?? "";
            string restoreTarget = !string.IsNullOrEmpty(memeFilePath) ? memeFilePath
                : (!string.IsNullOrEmpty(memeFile) ? Path.Combine(Infrastructure.MemeDirectory.GetActive(), memeFile) : "");
            if (!string.IsNullOrEmpty(restoreTarget))
            {
                if (File.Exists(restoreTarget))
                {
                    _pendingMemeRestorePath = restoreTarget;
                    var memeCbRestore = MemeComboBoxCtl;
                    var immediate = _memeItems.FirstOrDefault(m =>
                        string.Equals(m.FullPath, restoreTarget, StringComparison.OrdinalIgnoreCase));
                    if (memeCbRestore != null && immediate != null)
                    {
                        memeCbRestore.SelectedItem = immediate;
                        _pendingMemeRestorePath = null;
                    }
                }
                else
                {
                    RuntimeLog.Fail("Memes", $"Recovery meme skipped - file no longer exists: {Path.GetFileName(restoreTarget)}");
                    RuntimeLog.Debug("Memes", $"Recovery meme missing full path: {restoreTarget}");
                }
            }

            string? videoPath = document.Source.FilePath;
            if (!string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath))
            {
                _loadedVideoPath = videoPath;
                _isTimelineDrawn = false;

                for (int i = 0; i < 50; i++)
                {
                    if (ActiveVideoHost?.IpcClient != null) break;
                    await Task.Delay(25);
                }

                if (ActiveVideoHost?.IpcClient != null)
                {
                    _ = ActiveVideoHost.IpcClient.LoadFileAsync(videoPath);
                    _ = ActiveVideoHost.IpcClient.SetPropertyAsync("speed",
                        _baseSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                }
                if (ActiveVideoHost != null) ActiveVideoHost.IsVisible = true;
                UpdatePortraitOverlay();

                var uploadOverlay = UploadOverlayCtl;
                if (uploadOverlay != null) uploadOverlay.IsVisible = false;

                _ = Task.Run(async () =>
                {
                    await Task.Delay(400);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdatePortraitOverlay());
                });

                EnableEditingControls();

                UpdateEstimatedQuality();
                UpdateSpeedLabel();
                UpdateTimelineMarkers();

                RestoreVoiceOverTransient(document, state);
            }
            else
            {
                RuntimeLog.Info("RECOVERY", "Video file no longer exists. Skipping video restore; the edit state was applied from the document.");
            }

            RuntimeLog.Success("RECOVERY",
                $"Session restored. Video={Path.GetFileName(document.Source.FilePath)}, Trim={_trimStartMs:F0}ms-{_trimEndMs:F0}ms, " +
                $"Segments={_speedSegments.Count}, Cuts={_cuts.Count}, Memes={_memePlacements.Count}, " +
                $"MusicActive={_viewModel.IsMusicActive}, Mask={(document.Mask is null ? "none" : document.Mask.ProfileName)}");
            RuntimeLog.Debug("RECOVERY", $"Session restored video path: {videoPath}");
            ShowTacticalFeedback("Previous session restored after crash");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("RECOVERY", $"Failed to restore session state: {ex.Message}");
            _recovery.ClearState();
        }
        finally
        {
            _isRestoring = false;

            // RECOVERY_05 — the mirror of the save line. Put side by side in the log these two
            // lines prove a restore was faithful, field for field, without re-running the crash.
            try
            {
                RuntimeLog.Info("RECOVERY",
                    $"Restored project state: trim[{_trimStartMs:F0}-{_trimEndMs:F0}ms] " +
                    $"speed[base={_baseSpeed:F2}x segs={_speedSegments.Count}] " +
                    $"freeze[at={_freezeTimeMs:F0}ms for={_freezeDurationS:F2}s] " +
                    $"cuts[{_cuts.Count}] memes[{_memePlacements.Count}] " +
                    $"thumb[set={_thumbnailSet} at={_thumbnailPosMs:F0}ms] " +
                    $"voiceOver[{(_voiceOverResult != null ? "yes" : "no")}] " +
                    $"music[{(_musicWizardResult != null ? "yes" : "no")}] ");
            }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        }
    }

    /// <summary>
    /// The in-flight granular editor's own segments, cuts and memes re-instated over the
    /// document's segments/cuts/memes. The granular editor's values are editor-relative; the
    /// view-model's are absolute.
    /// </summary>
    private void RestoreGranularSessionTimeline(JsonObject granularSession)
    {
        if (granularSession["segments"] is JsonArray gSegs)
        {
            _speedSegments.Clear();
            foreach (var node in gSegs)
            {
                if (node is not JsonObject o) continue;
                double start = o["start_ms"]?.GetValue<double>() ?? -1;
                double end = o["end_ms"]?.GetValue<double>() ?? -1;
                if (end <= start || start < -0.5) continue;

                double? zStart = o.ContainsKey("zoom_start_ms") ? o["zoom_start_ms"]?.GetValue<double>() : null;
                double? zEnd = o.ContainsKey("zoom_end_ms") ? o["zoom_end_ms"]?.GetValue<double>() : null;

                _speedSegments.Add(new SpeedSegment(
                    start + _trimStartMs,
                    end + _trimStartMs,
                    o["speed"]?.GetValue<double>() ?? 1.1,
                    o["zoom_x"]?.GetValue<int>(),
                    o["zoom_y"]?.GetValue<int>(),
                    o["zoom_w"]?.GetValue<int>(),
                    o["zoom_h"]?.GetValue<int>(),
                    o["zoom_orig_res"]?.ToString(),
                    o["zoom_slow"]?.GetValue<bool>() ?? false,
                    zStart.HasValue ? zStart.Value + _trimStartMs : null,
                    zEnd.HasValue ? zEnd.Value + _trimStartMs : null));
            }
        }

        if (granularSession["cuts"] is JsonArray gCuts)
        {
            _cuts.Clear();
            foreach (var node in gCuts)
            {
                if (node is not JsonObject o) continue;
                double start = o["start_ms"]?.GetValue<double>() ?? -1;
                double end = o["end_ms"]?.GetValue<double>() ?? -1;
                if (end <= start) continue;
                _cuts.Add(new CutRange(start + _trimStartMs, end + _trimStartMs));
            }
        }

        if (granularSession["memes"] is JsonArray gMemes)
        {
            _memePlacements.Clear();
            int i = 0;
            foreach (var node in gMemes)
            {
                if (node is not JsonObject o) continue;
                string? file = o["file_path"]?.ToString();
                if (file != null) file = FreeVideoStudio.Core.Infrastructure.MigrationPathResolver.ResolveSavedFile(file);   // MEMEFOLDER_02
                double at = o["at_source_sec_relative"]?.GetValue<double>() ?? -1;
                double dur = o["duration_sec"]?.GetValue<double>() ?? 0;
                if (string.IsNullOrEmpty(file) || !File.Exists(file) || at < 0 || dur <= 0) continue;
                string id = o["id"]?.ToString() ?? "";
                _memePlacements.Add(FreeVideoStudio.Core.Project.MemePresentationJson.Apply(o,   // MEMEMODE_01
                    new FreeVideoStudio.Core.Media.MemePlacement(file!, at, dur,
                    !string.IsNullOrEmpty(id) ? id : FreeVideoStudio.Core.Media.MemePlacement.NewId(i))));
                i++;
            }
        }
    }

    /// <summary>
    /// WHERE the voice-over sits (path, position, per-take starts) is project data - the takes
    /// list and the ducking/protection toggles are working session state.
    /// </summary>
    private void RestoreVoiceOverTransient(ProjectDocument document, JsonObject state)
    {
        bool hasVoiceOver = document.Audio.VoiceOverFilePath != null
            || state.ContainsKey("voiceOverTakes")
            || state.ContainsKey("voiceOverDuckAudio")
            || state.ContainsKey("voiceOverProtectFromMusic");
        if (!hasVoiceOver) return;

        string? wavPath = document.Audio.VoiceOverFilePath;
        bool duckAudio = state.TryGetPropertyValue("voiceOverDuckAudio", out var dNode) && dNode != null && dNode.GetValue<bool>();
        bool protectFromMusic = state.TryGetPropertyValue("voiceOverProtectFromMusic", out var pmNode) && pmNode != null && pmNode.GetValue<bool>();

        var restoredTakes = new List<VoiceOverTake>();
        if (state.TryGetPropertyValue("voiceOverTakes", out var takesNode) && takesNode is JsonArray takesArray)
        {
            foreach (var takeNode in takesArray)
            {
                if (takeNode is not JsonObject takeObj) continue;
                string? takePath = takeObj["path"]?.GetValue<string>();
                double takeStart = takeObj["startSec"]?.GetValue<double>() ?? 0;
                if (!string.IsNullOrWhiteSpace(takePath) && File.Exists(takePath))
                {
                    restoredTakes.Add(new VoiceOverTake(takePath, takeStart));
                }
            }
        }

        if (restoredTakes.Count > 0 || (!string.IsNullOrEmpty(wavPath) && File.Exists(wavPath)) || duckAudio)
        {
            var resultObj = new VoiceOverWindow.VoiceOverResult
            {
                VoiceOverWavPath = wavPath ?? "",
                VoiceOverStartTimestampSec = document.Audio.VoiceOverAtOutputSec,
                VoiceOverTakes = restoredTakes,
                DuckAudio = duckAudio,
                ProtectFromMusic = protectFromMusic
            };
            ApplyVoiceOverState(resultObj, true);
        }
    }
}
