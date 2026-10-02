
using Avalonia.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;

namespace FreeVideoStudio.App.Controls;

public partial class SettingsWindow : Window
{
    private void SaveAndClose()
    {
        bool committed = SettingsManager.Update(s =>
        {
            KeyBinds kb = s.KeyBinds;
            kb.PlayPause = _pendingKeys["PlayPause"];
            kb.MarkStart = _pendingKeys["MarkStart"];
            kb.MarkEnd = _pendingKeys["MarkEnd"];
            kb.SeekForward = _pendingKeys["SeekForward"];
            kb.SeekBackward = _pendingKeys["SeekBackward"];
            kb.VolumeUp = _pendingKeys["VolumeUp"];
            kb.VolumeDown = _pendingKeys["VolumeDown"];
            kb.FineSeekForward = _pendingKeys["FineSeekForward"];
            kb.FineSeekBackward = _pendingKeys["FineSeekBackward"];
            kb.AggressiveVolumeUp = _pendingKeys["AggressiveVolumeUp"];
            kb.AggressiveVolumeDown = _pendingKeys["AggressiveVolumeDown"];

            s.Defaults = _pendingDefaults;

            s.ConfirmVideoMergerRemove = ConfirmVideoMergerRemove;
            s.ConfirmVideoMergerClearAll = ConfirmVideoMergerClearAll;
            s.ConfirmCropToolReset = ConfirmCropToolReset;
            s.ConfirmCropToolDelete = ConfirmCropToolDelete;
            s.ConfirmGranularDeleteSegment = ConfirmGranularDeleteSegment;
            s.ConfirmGranularClearAll = ConfirmGranularClearAll;
            s.ConfirmMainAppCancel = ConfirmMainAppCancel;
            s.ConfirmMainAppCut = ConfirmMainAppCut;
            s.ConfirmMainAppSwitchTool = ConfirmMainAppSwitchTool;
            s.KeepOverlayTextBetweenVideos = KeepOverlayTextBetweenVideos;
            s.ConfirmVoiceOverDeleteTake = ConfirmVoiceOverDeleteTake;
            s.ConfirmFinishedDialogExit = ConfirmFinishedDialogExit;
            var autoCheckCb = this.FindControl<CheckBox>("AutoUpdateChecksCheckbox");
            if (autoCheckCb != null && autoCheckCb.IsChecked.HasValue)
            {
                AutoUpdateChecks = autoCheckCb.IsChecked.Value;
            }
            s.AutoUpdateChecks = AutoUpdateChecks;
            s.MergerThumbnailScraper = MergerThumbnailScraper;
            s.UiSoundsEnabled = UiSoundsEnabled;
            s.UiSoundVolume = Math.Clamp(UiSoundVolume, 0, 100);

            s.ThemeMode = _pendingThemeMode;
            s.FontScale = _pendingFontScale;
            s.LoudnessNormalizationPrompt = _pendingLoudnessPrompt;
            s.PeakFlatteningPrompt = _pendingPeakPrompt;
            s.VoiceProtectGameMode = _pendingVoiceProtectGame;
            s.VoiceProtectMusicMode = _pendingVoiceProtectMusic;
            s.VideoEncoderOverride = _pendingVideoEncoder;

            s.GeminiApiKey = _pendingGeminiApiKey;
            s.GeminiModelName = _pendingGeminiModelName;
            s.AiZoomBaseScale = _pendingAiZoomBaseScale;
            s.AiZoomMinScale = _pendingAiZoomMinScale;
            s.AiZoomAvoidHud = _pendingAiZoomAvoidHud;
            s.AiZoomDeadbandPercent = _pendingAiZoomDeadbandPercent;
        });

        if (!committed)
        {
            var btn = this.FindControl<Button>("SaveBtn");
            if (btn != null) btn.Content = "SAVE FAILED";
            return;
        }

        ThemeManager.ApplyTheme(_pendingThemeMode);
        ThemeManager.ApplyFontScale(_pendingFontScale);

        if (_pendingMaskOverlay != _previousActiveMaskOverlay)
        {
            MaskOverlayManager.ApplyProfile(_pendingMaskOverlay);
        }

        if (_pendingVideoEncoder != _previousVideoEncoderOverride)
        {
            RuntimeLog.Info("Settings", $"Video encoder override changed: {_previousVideoEncoderOverride} -> {_pendingVideoEncoder}");
        }

        try
        {
            new FreeVideoStudio.Core.Ipc.StateTransferStore(_paths)
                .UpdatePropertiesSync(new System.Text.Json.Nodes.JsonObject
                {
                    ["CustomMusicDirectory"] = _pendingMusicFolder
                });
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

        Close(true);
    }
}
