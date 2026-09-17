// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FortniteVideoSoftware.App.Infrastructure;
using FortniteVideoSoftware.App.Models;
using FortniteVideoSoftware.App.ViewModels;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App.Services;

public sealed class ProjectRecoveryService
{
    private readonly RecoveryManager _recovery;

    public ProjectRecoveryService(ApplicationPaths? paths = null)
    {
        _recovery = new RecoveryManager(paths ?? ApplicationPaths.CreateDefault());
    }

    public void SaveState(JsonObject state, bool sync = false)
    {
        if (sync)
            _recovery.SaveState(state);
        else
            _recovery.SaveStateAsync(state);
    }

    public JsonObject? LoadState() => _recovery.LoadState();

    public void ClearState() => _recovery.ClearState();

    public void ReleaseLockOnly() => _recovery.ReleaseLockOnly();

    /// <summary>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// "Is there anything here a user would be upset to lose?"
    ///
    /// Two consumers, and they pull in the same direction: it decides whether a recovery snapshot
    /// is worth writing, and (SWITCHPROMPT_01) whether the Video Merger / Crop Tools hand-off is
    /// worth interrupting with a confirmation.
    ///
    /// ⚠️ A LOADED VIDEO IS NOT WORK. Uploading a clip and touching nothing else must return false.
    ///
    /// SWITCHPROMPT_01 — TWO REASONS THIS RETURNED TRUE ON A COMPLETELY EMPTY EDITOR, which is why
    /// the switch prompt fired with nothing loaded:
    ///
    ///   1. The toggle comparisons were against HARDCODED assumptions — portrait on, fade on,
    ///      speed 1.1 — rather than against the defaults the user actually configured in
    ///      Settings → Defaults. Anyone who set "Portrait Mode: off", or a default speed of 1.0,
    ///      had a session that was born dirty: the very first frame after startup already
    ///      disagreed with the constants below, with no video and no edit in sight. Every one of
    ///      those comparisons now reads the SAME defaults ApplyDefaults() seeds the session from,
    ///      so "unchanged from where the app put it" reliably means unchanged.
    ///
    ///   2. Nothing checked whether a video was loaded at all. With no clip there is no project:
    ///      no trim to keep, nothing to recover, nothing to warn about. That is now the first
    ///      question asked, and it is the guard that makes the empty-editor case impossible
    ///      regardless of what any toggle says.
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    public bool HasUnsavedWork(MainViewModel mainVm, TimelineViewModel timelineVm, ExportViewModel exportVm)
    {
        if (mainVm.ExportedCleanSinceLastEdit) return false;

        // No clip, no project. Nothing below can be true in a way that matters.
        if (string.IsNullOrWhiteSpace(mainVm.LoadedVideoPath)) return false;

        var defaults = SettingsManager.Instance.Defaults;

        bool memeSelected = mainVm.IsAddMeme || mainVm.SelectedMemeItem != null;

        // SPECTATINGDEFAULT_01 — the eye starts on, so turning it off is an edit.
        // No Mask forces both controls off; that profile's initial state is still clean.
        bool noMask = MaskOverlayManager.IsNoMask(SettingsManager.Instance.ActiveMaskOverlay);
        bool hudToggled = mainVm.IsTeammates != (!noMask && defaults.ShowTeammates)
                       || mainVm.IsSpectating != !noMask;

        bool exportTogglesChanged = mainVm.IsPortraitMode != defaults.PortraitMode
                                 || mainVm.IsEnableFade != defaults.EnableFade;

        bool speedChanged = Math.Abs(timelineVm.BaseSpeed - defaults.DefaultSpeed) > 0.01;

        return timelineVm.IsTrimStartSet || timelineVm.IsTrimEndSet || timelineVm.IsThumbnailSet ||
               mainVm.IsGranularSpeedActive || mainVm.IsMusicActive || mainVm.VoiceOverResult != null ||
               mainVm.MusicWizardResult != null ||
               timelineVm.Cuts.Count > 0 ||
               timelineVm.MemePlacements.Count > 0 ||
               timelineVm.SpeedSegments.Count > 0 || timelineVm.FreezeTimeMs >= 0 ||
               speedChanged ||
               memeSelected || hudToggled || exportTogglesChanged ||
               !string.IsNullOrWhiteSpace(mainVm.OverlayText);
    }

    public JsonObject SerializeState(MainViewModel mainVm, TimelineViewModel timelineVm, ExportViewModel exportVm)
    {
        var state = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["loadedVideoPath"] = mainVm.LoadedVideoPath,
            ["trimStartMs"] = timelineVm.TrimStartMs,
            ["trimStartSet"] = timelineVm.IsTrimStartSet,
            ["trimEndMs"] = timelineVm.TrimEndMs,
            ["trimEndSet"] = timelineVm.IsTrimEndSet,
            ["thumbnailPosMs"] = timelineVm.ThumbnailPosMs,
            ["thumbnailSet"] = timelineVm.IsThumbnailSet,
            ["baseSpeed"] = timelineVm.BaseSpeed,
            ["qualitySliderValue"] = exportVm.QualitySliderValue,
            ["isGranularSpeedActive"] = mainVm.IsGranularSpeedActive,
            ["isMusicActive"] = mainVm.IsMusicActive,
            ["volume"] = mainVm.MainVolume,
            ["portraitMode"] = mainVm.IsPortraitMode,
            ["showTeammates"] = mainVm.IsTeammates,
            ["showSpectating"] = mainVm.IsSpectating,
            ["enableFade"] = mainVm.IsEnableFade,
            ["addMeme"] = mainVm.IsAddMeme,
            ["memeFile"] = mainVm.SelectedMemeItem?.FileName ?? "",
            ["memeFilePath"] = mainVm.SelectedMemeItem?.FullPath ?? "",
            ["portraitText"] = mainVm.OverlayText ?? "",
            ["freezeTimeMs"] = timelineVm.FreezeTimeMs,
            ["freezeDurationS"] = timelineVm.FreezeDurationS
        };

        if (mainVm.VoiceOverResult != null)
        {
            state["voiceOverWavPath"] = mainVm.VoiceOverResult.VoiceOverWavPath;
            state["voiceOverStartSec"] = mainVm.VoiceOverResult.VoiceOverStartTimestampSec;
            state["voiceOverDuckAudio"] = mainVm.VoiceOverResult.DuckAudio;
            state["voiceOverProtectFromMusic"] = mainVm.VoiceOverResult.ProtectFromMusic;

            var voiceTakeArray = new JsonArray();
            var takes = mainVm.VoiceOverResult.VoiceOverTakes;
            if (takes != null)
            {
                foreach (var take in takes)
                {
                    if (take != null && !string.IsNullOrWhiteSpace(take.Path) && File.Exists(take.Path))
                    {
                        voiceTakeArray.Add(new JsonObject
                        {
                            ["path"] = take.Path,
                            ["startSec"] = take.StartSec
                        });
                    }
                }
            }
            state["voiceOverTakes"] = voiceTakeArray;
        }

        var segArray = new JsonArray();
        foreach (var seg in timelineVm.SpeedSegments)
        {
            var segObj = new JsonObject
            {
                ["startMs"] = seg.StartMs,
                ["endMs"] = seg.EndMs,
                ["speed"] = seg.Speed
            };
            if (seg.ZoomW.HasValue)
            {
                segObj["zoomX"] = seg.ZoomX;
                segObj["zoomY"] = seg.ZoomY;
                segObj["zoomW"] = seg.ZoomW;
                segObj["zoomH"] = seg.ZoomH;
                segObj["zoomOrigRes"] = seg.ZoomOrigRes;
                segObj["zoomSlow"] = seg.ZoomSlow;
                if (seg.ZoomStartMs.HasValue) segObj["zoomStartMs"] = seg.ZoomStartMs.Value;
                if (seg.ZoomEndMs.HasValue) segObj["zoomEndMs"] = seg.ZoomEndMs.Value;
            }
            segArray.Add(segObj);
        }
        state["speedSegments"] = segArray;

        var cutArray = new JsonArray();
        foreach (var c in timelineVm.Cuts)
        {
            cutArray.Add(new JsonObject
            {
                ["startMs"] = c.StartMs,
                ["endMs"] = c.EndMs
            });
        }
        state["cuts"] = cutArray;

        var memeArray = new JsonArray();
        foreach (var m in timelineVm.MemePlacements)
        {
            memeArray.Add(new JsonObject
            {
                ["path"] = m.FilePath,
                ["atSourceSec"] = m.AtSourceSecRelative,
                ["durationSec"] = m.DurationSec,
                ["id"] = m.Id
            });
        }
        state["memePlacements"] = memeArray;

        if (mainVm.MusicWizardResult != null)
        {
            var pathsArray = new JsonArray();
            if (mainVm.MusicWizardResult.MusicFilePaths != null)
            {
                foreach (var path in mainVm.MusicWizardResult.MusicFilePaths)
                {
                    pathsArray.Add(JsonValue.Create(path));
                }
            }
            var durationsArray = new JsonArray();
            if (mainVm.MusicWizardResult.MusicDurationsSeconds != null)
            {
                foreach (var durationSec in mainVm.MusicWizardResult.MusicDurationsSeconds)
                {
                    durationsArray.Add(JsonValue.Create(durationSec));
                }
            }
            state["musicResult"] = new JsonObject
            {
                ["musicFilePath"] = mainVm.MusicWizardResult.MusicFilePath,
                ["musicFilePaths"] = pathsArray,
                ["musicDurationsSeconds"] = durationsArray,
                ["offsetSeconds"] = mainVm.MusicWizardResult.OffsetSeconds,
                ["timelineStartSeconds"] = mainVm.MusicWizardResult.TimelineStartSeconds,
                ["timelineEndSeconds"] = mainVm.MusicWizardResult.TimelineEndSeconds,
                ["musicDurationSeconds"] = mainVm.MusicWizardResult.MusicDurationSeconds,
                ["enableDucking"] = mainVm.MusicWizardResult.EnableDucking,
                ["enableCarving"] = mainVm.MusicWizardResult.EnableCarving,
                ["videoVolume"] = mainVm.MusicWizardResult.VideoVolume,
                ["musicVolume"] = mainVm.MusicWizardResult.MusicVolume,
                ["loopMusic"] = mainVm.MusicWizardResult.LoopMusic
            };
        }

        return state;
    }
}
