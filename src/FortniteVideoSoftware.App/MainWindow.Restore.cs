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
using FortniteVideoSoftware.App.Controls;
using FortniteVideoSoftware.App.Infrastructure;
using FortniteVideoSoftware.App.Models;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App;

public partial class MainWindow
{
    private async Task RestoreRecoveryStateAsync()
    {
        _isRestoring = true;
        try
        {
            var state = _recovery.LoadState();
            if (state == null)
            {
                RuntimeLog.Info("RECOVERY", "No recovery state file found. Nothing to restore.");
                return;
            }

            int schemaVersion = state.TryGetPropertyValue("schemaVersion", out var svNode) && svNode != null ? svNode.GetValue<int>() : 0;
            if (schemaVersion < 1)
            {
                RuntimeLog.Fail("RECOVERY", $"Recovery state schema version ({schemaVersion}) is outdated or missing. Discarding state to prevent corruption.");
                _recovery.ClearState();
                return;
            }

            RuntimeLog.Info("RECOVERY", "Beginning state restoration...");

            _trimStartMs = state["trimStartMs"]?.GetValue<double>() ?? 0;
            _trimStartSet = state["trimStartSet"]?.GetValue<bool>() ?? false;
            _trimEndMs = state["trimEndMs"]?.GetValue<double>() ?? 0;
            _trimEndSet = state["trimEndSet"]?.GetValue<bool>() ?? false;
            _thumbnailPosMs = state["thumbnailPosMs"]?.GetValue<double>() ?? 0;
            _thumbnailSet = state["thumbnailSet"]?.GetValue<bool>() ?? _thumbnailPosMs > 0;
            
            // THUMB_01 — one owner for this button's appearance. The hand-rolled copy here always
            // came back saying REMOVE THUMBNAIL, which after the change above is only true when the
            // playhead happens to be sitting on the thumbnail — and on a fresh restore it is at the
            // trim start, not on it. The recovered project would have offered to delete the cover
            // frame it had just restored.
            UpdateThumbnailButtonState();

            var markStartBtn = MarkStartButtonCtl;
            if (markStartBtn != null && _trimStartSet)
                markStartBtn.Content = $"START: {FormatTime(TimeSpan.FromMilliseconds(_trimStartMs))}";
            var markEndBtn = MarkEndButtonCtl;
            if (markEndBtn != null && _trimEndMs > 0)
                markEndBtn.Content = $"END: {FormatTime(TimeSpan.FromMilliseconds(_trimEndMs))}";

            double restoredSpeed = state["baseSpeed"]?.GetValue<double>() ?? SpeedPresetButtons.NativeDefaultSpeed;
            if (double.IsNaN(restoredSpeed) || double.IsInfinity(restoredSpeed) || restoredSpeed < 0.1 || restoredSpeed > 4.0)
            {
                RuntimeLog.Fail("RECOVERY",
                    $"Recovered base speed {restoredSpeed} is outside the supported 0.1x-4.0x range — resetting to the default {SpeedPresetButtons.NativeDefaultSpeed}x.");
                restoredSpeed = SpeedPresetButtons.NativeDefaultSpeed;
            }
            _baseSpeed = restoredSpeed;
            var speedSlider = MainSpeedSliderCtl;
            if (speedSlider != null) speedSlider.Value = (int)Math.Round(_baseSpeed * 10.0, MidpointRounding.AwayFromZero);

            // QUALITY_01 — the stored index means a TIER now. An index written by an older build
            // meant megabytes; ClampIndex (in the setter) keeps it in range rather than failing,
            // and the old top stop "ORIGINAL QUALITY" still lands on the new top stop "Original".
            int qualityVal = state["qualitySliderValue"]?.GetValue<int>()
                             ?? FortniteVideoSoftware.App.ViewModels.QualityLadder.DefaultIndex;
            qualityVal = FortniteVideoSoftware.App.ViewModels.QualityLadder.ClampIndex(qualityVal);
            var qualitySliderRestore = QualitySliderCtl;
            if (qualitySliderRestore != null) qualitySliderRestore.Value = qualityVal;

            _freezeTimeMs = state["freezeTimeMs"]?.GetValue<double>() ?? -1;
            _freezeDurationS = state["freezeDurationS"]?.GetValue<double>() ?? 1.0;
            if (_freezeTimeMs >= 0)
                RuntimeLog.Info("RECOVERY", $"Crash Recovery Restore: Successfully reinstated Freeze parameters [Timestamp={_freezeTimeMs}ms, Duration={_freezeDurationS}s]");

            // CUT_01 — a session saved before cuts existed has no "cuts" key; the list simply
            // stays empty and the clip behaves exactly as it always did.
            _cuts.Clear();
            if (state.TryGetPropertyValue("cuts", out var cutsNode) && cutsNode is System.Text.Json.Nodes.JsonArray cutsArray)
            {
                foreach (var cutNode in cutsArray)
                {
                    var cutObj = cutNode?.AsObject();
                    if (cutObj == null) continue;
                    double cs = cutObj["startMs"]?.GetValue<double>() ?? 0;
                    double ce = cutObj["endMs"]?.GetValue<double>() ?? 0;
                    if (ce > cs) _cuts.Add(new FortniteVideoSoftware.Core.Media.CutRange(cs, ce));
                }
                if (_cuts.Count > 0)
                    RuntimeLog.Info("RECOVERY", $"Crash Recovery Restore: {_cuts.Count} deleted section(s) reinstated.");
            }

            // MEME_06 — additive and absent-is-legal: a recovery file written before memes existed
            // simply has no "memePlacements" key and restores with none, which is exactly the
            // project that build produced.
            _memePlacements.Clear();
            if (state.TryGetPropertyValue("memePlacements", out var memesNode) && memesNode is System.Text.Json.Nodes.JsonArray memesArray)
            {
                int skipped = 0;
                foreach (var memeNode in memesArray)
                {
                    var memeObj = memeNode?.AsObject();
                    if (memeObj == null) continue;

                    string path = memeObj["path"]?.ToString() ?? "";
                    double at = memeObj["atSourceSec"]?.GetValue<double>() ?? -1;
                    double dur = memeObj["durationSec"]?.GetValue<double>() ?? 0;
                    string id = memeObj["id"]?.ToString() ?? "";

                    // A meme whose file has been deleted or moved since the crash cannot be
                    // restored — the export would skip it later anyway, and a placement pointing at
                    // nothing would occupy time on the ruler that the finished video never has.
                    if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path) || at < 0 || dur <= 0.01)
                    {
                        skipped++;
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(id))
                        id = FortniteVideoSoftware.Core.Media.MemePlacement.NewId(_memePlacements.Count);

                    _memePlacements.Add(new FortniteVideoSoftware.Core.Media.MemePlacement(path, at, dur, id));
                }

                if (_memePlacements.Count > 0 || skipped > 0)
                    RuntimeLog.Info("RECOVERY",
                        $"Crash Recovery Restore: {_memePlacements.Count} meme placement(s) reinstated" +
                        (skipped > 0 ? $", {skipped} skipped because their file is gone." : "."));
            }

            _speedSegments.Clear();
            if (state.TryGetPropertyValue("speedSegments", out var speedsNode) && speedsNode is System.Text.Json.Nodes.JsonArray speedsArray)
            {
                foreach (var segNode in speedsArray)
                {
                    var segObj = segNode?.AsObject();
                    if (segObj != null)
                    {
                        _speedSegments.Add(new SpeedSegment(
                            segObj["startMs"]?.GetValue<double>() ?? 0,
                            segObj["endMs"]?.GetValue<double>() ?? 0,
                            segObj["speed"]?.GetValue<double>() ?? 1.0,
                            segObj["zoomX"]?.GetValue<int>(),
                            segObj["zoomY"]?.GetValue<int>(),
                            segObj["zoomW"]?.GetValue<int>(),
                            segObj["zoomH"]?.GetValue<int>(),
                            segObj["zoomOrigRes"]?.GetValue<string>(),
                            segObj["zoomSlow"]?.GetValue<bool>() ?? false,
                            segObj.ContainsKey("zoomStartMs") ? segObj["zoomStartMs"]?.GetValue<double>() : null,
                            segObj.ContainsKey("zoomEndMs") ? segObj["zoomEndMs"]?.GetValue<double>() : null));
                    }
                }
            }

            if (state.TryGetPropertyValue("granular_session", out var granularNode) && granularNode is System.Text.Json.Nodes.JsonObject granularSession)
            {
                bool isOpen = granularSession["open"]?.GetValue<bool>() ?? false;
                string? gVideo = granularSession["video_path"]?.ToString();
                double gTrimStart = granularSession["trim_start_ms"]?.GetValue<double>() ?? -1;
                double gTrimEnd = granularSession["trim_end_ms"]?.GetValue<double>() ?? -1;
                string? loadedPath = (string?)state["loadedVideoPath"];

                if (isOpen && !string.IsNullOrEmpty(gVideo)
                    && string.Equals(gVideo, loadedPath, StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(gTrimStart - _trimStartMs) <= 1.0
                    && Math.Abs(gTrimEnd - _trimEndMs) <= 1.0)
                {
                    RuntimeLog.Info("RECOVERY", "In-flight Granular Speed Editor session detected from crash. Reinstating live granular edits.");

                    if (granularSession.ContainsKey("base_speed") && granularSession["base_speed"] is System.Text.Json.Nodes.JsonNode bsNode)
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

                    if (granularSession["segments"] is System.Text.Json.Nodes.JsonArray gSegs)
                    {
                        _speedSegments.Clear();
                        foreach (var node in gSegs)
                        {
                            if (node is not System.Text.Json.Nodes.JsonObject o) continue;
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
                                o["zoom_orig_res"]?.GetValue<string>(),
                                o["zoom_slow"]?.GetValue<bool>() ?? false,
                                zStart.HasValue ? zStart.Value + _trimStartMs : null,
                                zEnd.HasValue ? zEnd.Value + _trimStartMs : null));
                        }
                    }

                    if (granularSession["cuts"] is System.Text.Json.Nodes.JsonArray gCuts)
                    {
                        _cuts.Clear();
                        foreach (var node in gCuts)
                        {
                            if (node is not System.Text.Json.Nodes.JsonObject o) continue;
                            double start = o["start_ms"]?.GetValue<double>() ?? -1;
                            double end = o["end_ms"]?.GetValue<double>() ?? -1;
                            if (end <= start) continue;
                            _cuts.Add(new FortniteVideoSoftware.Core.Media.CutRange(start + _trimStartMs, end + _trimStartMs));
                        }
                    }

                    if (granularSession["memes"] is System.Text.Json.Nodes.JsonArray gMemes)
                    {
                        _memePlacements.Clear();
                        int i = 0;
                        foreach (var node in gMemes)
                        {
                            if (node is not System.Text.Json.Nodes.JsonObject o) continue;
                            string? file = o["file_path"]?.ToString();
                            double at = o["at_source_sec_relative"]?.GetValue<double>() ?? -1;
                            double dur = o["duration_sec"]?.GetValue<double>() ?? 0;
                            if (string.IsNullOrEmpty(file) || !System.IO.File.Exists(file) || at < 0 || dur <= 0) continue;
                            string id = o["id"]?.ToString() ?? "";
                            _memePlacements.Add(new FortniteVideoSoftware.Core.Media.MemePlacement(file!, at, dur,
                                !string.IsNullOrEmpty(id) ? id : FortniteVideoSoftware.Core.Media.MemePlacement.NewId(i)));
                            i++;
                        }
                    }
                }
            }

            // EDIT3_02 — restore now goes through the SAME setter as every other path instead of
            // hand-copying the styling. The copy had already drifted (it still said REMOVE SPEEDS),
            // and it is safe to call here because SaveRecoveryState no-ops while `_isRestoring`.
            bool hasGranular = _speedSegments.Count > 0 || _freezeTimeMs >= 0;
            SetGranularButtonActive(hasGranular || (state["isGranularSpeedActive"]?.GetValue<bool>() ?? false));

            _musicWizardResult = null;
            if (state["musicResult"] is System.Text.Json.Nodes.JsonObject musicObj)
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
                if (musicObj["musicFilePaths"] is System.Text.Json.Nodes.JsonArray pathsArr)
                {
                    foreach (var node in pathsArr)
                    {
                        var path = node?.ToString();
                        if (!string.IsNullOrEmpty(path)) _musicWizardResult.MusicFilePaths.Add(path);
                    }
                }
                if (musicObj["musicDurationsSeconds"] is System.Text.Json.Nodes.JsonArray durationsArr)
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
            SetMusicButtonActive(state["isMusicActive"]?.GetValue<bool>() ?? false);

            double vol = state["volume"]?.GetValue<double>() ?? 100;
            var volSliderRestore = VolumeSliderCtl;
            if (volSliderRestore != null) volSliderRestore.Value = vol;

            bool portraitMode = state["portraitMode"]?.GetValue<bool>() ?? true;
            var portraitCbRestore = PortraitModeCheckboxCtl;
            if (portraitCbRestore != null) portraitCbRestore.IsChecked = portraitMode;

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

            string memeFilePath = (string?)state["memeFilePath"] ?? "";
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
                    RuntimeLog.Fail("Memes", $"Recovery meme skipped — file no longer exists: {Path.GetFileName(restoreTarget)}");
                    RuntimeLog.Debug("Memes", $"Recovery meme missing full path: {restoreTarget}");
                }
            }

            string? videoPath = (string?)state["loadedVideoPath"];
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

                if (state.TryGetPropertyValue("voiceOverWavPath", out var wavPathNode) || state.ContainsKey("voiceOverDuckAudio") || state.ContainsKey("voiceOverTakes"))
                {
                    var wavPath = wavPathNode?.GetValue<string>();
                    bool duckAudio = state.TryGetPropertyValue("voiceOverDuckAudio", out var dNode) && dNode != null && dNode.GetValue<bool>();
                    // Additive, absent-is-legal: a recovery file written before this flag existed
                    // restores with music protection OFF, which is exactly how that project ran.
                    bool protectFromMusic = state.TryGetPropertyValue("voiceOverProtectFromMusic", out var pmNode) && pmNode != null && pmNode.GetValue<bool>();
                    
                    var restoredTakes = new List<VoiceOverTake>();
                    if (state.TryGetPropertyValue("voiceOverTakes", out var takesNode) &&
                        takesNode is System.Text.Json.Nodes.JsonArray takesArray)
                    {
                        foreach (var takeNode in takesArray)
                        {
                            if (takeNode is not System.Text.Json.Nodes.JsonObject takeObj) continue;
                            string? takePath = takeObj["path"]?.GetValue<string>();
                            double takeStart = takeObj["startSec"]?.GetValue<double>() ?? 0;
                            if (!string.IsNullOrWhiteSpace(takePath) && System.IO.File.Exists(takePath))
                            {
                                restoredTakes.Add(new VoiceOverTake(takePath, takeStart));
                            }
                        }
                    }

                    if (restoredTakes.Count > 0 || (!string.IsNullOrEmpty(wavPath) && System.IO.File.Exists(wavPath)) || duckAudio)
                    {
                        var resultObj = new VoiceOverWindow.VoiceOverResult
                        {
                            VoiceOverWavPath = wavPath ?? "",
                            VoiceOverStartTimestampSec = state.TryGetPropertyValue("voiceOverStartSec", out var sNode) && sNode != null ? sNode.GetValue<double>() : 0,
                            VoiceOverTakes = restoredTakes,
                            DuckAudio = duckAudio,
                            ProtectFromMusic = protectFromMusic
                        };
                        ApplyVoiceOverState(resultObj, true);
                    }
                }

                RuntimeLog.Success("RECOVERY", $"Session restored. Video={Path.GetFileName(videoPath)}, Trim={_trimStartMs}ms-{_trimEndMs}ms, Segments={_speedSegments.Count}, GranularActive={_isGranularSpeedActive}, MusicActive={_isMusicActive}");
                RuntimeLog.Debug("RECOVERY", $"Session restored video path: {videoPath}");
                ShowTacticalFeedback("Previous session restored after crash");
            }
            else
            {
                RuntimeLog.Info("RECOVERY", "Video file no longer exists. Skipping video restore; other settings were restored.");
            }
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
                    $"Restored project state: trim[{_trimStartMs:F0}-{_trimEndMs:F0}ms set={_trimStartSet}/{_trimEndSet}] " +
                    $"speed[base={_baseSpeed:F2}x segs={_speedSegments.Count}] " +
                    $"freeze[at={_freezeTimeMs:F0}ms for={_freezeDurationS:F2}s] " +
                    $"cuts[{_cuts.Count} removing {RemovedCutSeconds():F2}s] " +
                    $"thumb[set={_thumbnailSet} at={_thumbnailPosMs:F0}ms] " +
                    $"voiceOver[{(_voiceOverResult != null ? "yes" : "no")}] " +
                    $"music[{(_musicWizardResult != null ? "yes" : "no")}].");
            }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        }
    }


}
