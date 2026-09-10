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

            var markStartBtn = this.FindControl<Button>("MarkStartButton");
            if (markStartBtn != null && _trimStartSet)
                markStartBtn.Content = $"START: {FormatTime(TimeSpan.FromMilliseconds(_trimStartMs))}";
            var markEndBtn = this.FindControl<Button>("MarkEndButton");
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
            var speedSlider = this.FindControl<SpinningWheelSlider>("MainSpeedSlider");
            if (speedSlider != null) speedSlider.Value = (int)Math.Round(_baseSpeed * 10.0, MidpointRounding.AwayFromZero);

            int qualityVal = state["qualitySliderValue"]?.GetValue<int>() ?? 7;
            var qualitySliderRestore = this.FindControl<SpinningWheelSlider>("QualitySlider");
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

            // EDIT3_02 — restore now goes through the SAME setter as every other path instead of
            // hand-copying the styling. The copy had already drifted (it still said REMOVE SPEEDS),
            // and it is safe to call here because SaveRecoveryState no-ops while `_isRestoring`.
            SetGranularButtonActive(state["isGranularSpeedActive"]?.GetValue<bool>() ?? false);

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
            var volSliderRestore = this.FindControl<Slider>("VolumeSlider");
            if (volSliderRestore != null) volSliderRestore.Value = vol;

            bool portraitMode = state["portraitMode"]?.GetValue<bool>() ?? true;
            var portraitCbRestore = this.FindControl<ToggleSwitch>("PortraitModeCheckbox");
            if (portraitCbRestore != null) portraitCbRestore.IsChecked = portraitMode;

            bool bossHp = state["bossHp"]?.GetValue<bool>() ?? false;
            var bossHpCbRestore = this.FindControl<ToggleSwitch>("BossHpCheckbox");
            if (bossHpCbRestore != null) bossHpCbRestore.IsChecked = bossHp;

            bool showTeammates = state["showTeammates"]?.GetValue<bool>() ?? false;
            var teammatesCbRestore = this.FindControl<ToggleSwitch>("TeammatesCheckbox");
            if (teammatesCbRestore != null) teammatesCbRestore.IsChecked = showTeammates;

            bool showSpectating = state["showSpectating"]?.GetValue<bool>() ?? false;
            var spectatingCbRestore = this.FindControl<ToggleSwitch>("SpectatingCheckbox");
            if (spectatingCbRestore != null) spectatingCbRestore.IsChecked = showSpectating;

            var enableFadeCbRestore = this.FindControl<ToggleSwitch>("EnableFadeCheckbox");
            if (enableFadeCbRestore != null && state.ContainsKey("enableFade"))
                enableFadeCbRestore.IsChecked = state["enableFade"]?.GetValue<bool>() ?? true;

            string portraitText = (string?)state["portraitText"] ?? "";
            var portraitTextRestore = this.FindControl<TextBox>("PortraitTextInput");
            if (portraitTextRestore != null) portraitTextRestore.Text = portraitText;

            bool addMeme = state["addMeme"]?.GetValue<bool>() ?? false;
            var addMemeCbRestore = this.FindControl<ToggleSwitch>("AddMemeCheckbox");
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
                    var memeCbRestore = this.FindControl<ComboBox>("MemeComboBox");
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

            var uploadOverlay = this.FindControl<Border>("UploadOverlay");
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
