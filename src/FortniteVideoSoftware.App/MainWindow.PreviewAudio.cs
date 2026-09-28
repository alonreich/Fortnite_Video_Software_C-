// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Globalization;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// MUSICSYNC_01 / MUSICSYNC_02 — PREVIEW AUDIO FOLLOWS THE VIDEO, IN OUTPUT TIME.
///
/// The master clock is the video player's position, mapped to FINISHED-VIDEO seconds through
/// OutputTimeline (North Star #2). The music bed and every voice-over take are followers.
///
/// What was wrong. The music preview computed the song position from RAW SOURCE seconds and
/// played at 1.0x beside a video running at the base speed (1.1x by default), with cuts skipped
/// and freezes held. It resynced only when the playhead jumped more than 0.5 s between ticks, so
/// during normal playback it drifted freely, and it disagreed with the export (which has always
/// placed music in output time, CUTS_02) wherever playback started. Voice-over takes tolerated
/// 0.5 s of error on 300 ms of uncompensated buffering.
///
/// ⚠️ THE MUSIC ALWAYS PLAYS AT ITS OWN NORMAL SPEED. Only the video speed changes. The music
/// player's "speed" is pinned to 1.0 on every start and is never adjusted; sync is corrected by
/// SEEKING the music to where the export will have it.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class MainWindow
{
    /// <summary>After a load or seek, time-pos needs a moment to report the new position.</summary>
    private const int MusicSyncSettleMs = 350;

    private string? _musicPreviewPath;
    private int _musicDriftStrikes;
    private long _musicSyncHoldUntilTicks;

    private object? _musicPlanKey;
    private IReadOnlyList<MusicBedSegment> _musicPlan = Array.Empty<MusicBedSegment>();

    /// <summary>
    /// MUSICSYNC_01 — the video's current position in FINISHED-VIDEO seconds. It is the same
    /// OutputTimeline mapping the export uses. It is non-mutating and cached, so it is safe per tick.
    /// While a freeze is being held on screen, the output clock keeps running even though the
    /// source position does not.
    /// </summary>
    private double PreviewOutputSeconds(double sourceTimeSec)
    {
        var timeline = _viewModel.Timeline;
        if (_isCurrentlyFrozen && _freezeTimeMs >= 0)
        {
            double freezeBaseSec = timeline.PreviewSourceToOutputSeconds(_freezeTimeMs);
            double elapsedFreezeSec = Math.Clamp((DateTime.UtcNow - _freezeStartTime).TotalSeconds, 0, Math.Max(0, _freezeDurationS));
            return freezeBaseSec + elapsedFreezeSec;
        }

        return timeline.PreviewSourceToOutputSeconds(sourceTimeSec * 1000.0);
    }

    /// <summary>
    /// MUSICSYNC_01 — the music bed exactly as the export will lay it: same start/end marker
    /// mapping, same first-track offset, same multi-track order, same LOOP_01 repeat
    /// (<see cref="MusicBedPlan.Build"/> is shared with MainWindow.Export). Rebuilt only when an
    /// input changes.
    /// </summary>
    private IReadOnlyList<MusicBedSegment> GetMusicPreviewPlan()
    {
        var result = _musicWizardResult;
        if (result == null || string.IsNullOrEmpty(result.MusicFilePath)) return Array.Empty<MusicBedSegment>();

        double videoEndMs = _trimEndMs > 0 ? _trimEndMs : _loadedVideoDurationMs;
        double musicStartMs = Math.Max(_trimStartMs, result.TimelineStartSeconds * 1000.0);
        double musicEndMs = Math.Min(videoEndMs, result.TimelineEndSeconds * 1000.0);

        double startDelay = _viewModel.Timeline.PreviewSourceToOutputSeconds(musicStartMs);
        double outputEnd = _viewModel.Timeline.PreviewSourceToOutputSeconds(musicEndMs);
        double bedDuration = outputEnd - startDelay;
        if (bedDuration <= 0) bedDuration = 1.0;   // the export's own floor

        var key = (result, startDelay, bedDuration, result.OffsetSeconds, result.LoopMusic,
                   result.MusicFilePath, result.MusicFilePaths?.Count ?? 0, result.MusicDurationSeconds);
        if (Equals(key, _musicPlanKey)) return _musicPlan;

        var paths = new List<string>();
        if (result.MusicFilePaths != null && result.MusicFilePaths.Count > 0)
        {
            foreach (var p in result.MusicFilePaths)
                if (!string.IsNullOrWhiteSpace(p) && System.IO.File.Exists(p)) paths.Add(p);
        }
        else
        {
            paths.Add(result.MusicFilePath);
        }

        // The export ffprobes a track whose length the wizard did not record. The preview does
        // not spawn processes on the UI thread, so an unknown length is treated as "covers the rest".
        // That is the same assumption the export makes when its probe also comes back empty.
        var durations = new List<double>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            double known = 0;
            if (result.MusicDurationsSeconds != null && i < result.MusicDurationsSeconds.Count)
                known = result.MusicDurationsSeconds[i];
            else if (i == 0)
                known = result.MusicDurationSeconds;
            durations.Add(known);
        }

        _musicPlan = MusicBedPlan.Build(paths, durations, result.OffsetSeconds, bedDuration, result.LoopMusic, startDelay);
        _musicPlanKey = key;
        return _musicPlan;
    }

    /// <summary>MUSICSYNC_01/02 — called on every playback tick with the video's SOURCE time.</summary>
    private void UpdateMusicPreview(double sourceTimeSec, bool videoEnded)
    {
        if (_musicWizardResult == null || string.IsNullOrEmpty(_musicWizardResult.MusicFilePath))
        {
            if (_isMusicPreviewPlaying) StopMusicPreview();
            return;
        }

        var ipc = ActiveVideoHost?.IpcClient;
        bool isPaused = ipc?.IsPaused ?? true;
        if (_isCurrentlyFrozen) isPaused = false;
        bool isDraggingAnyMarker = _draggingStartMarker || _draggingEndMarker || _draggingMusicStart || _draggingMusicEnd || _draggingMusicBlock;

        (string Path, double PositionSec, int SegmentIndex)? target = null;
        if (!isPaused && !videoEnded)
        {
            target = MusicBedPlan.Locate(GetMusicPreviewPlan(), PreviewOutputSeconds(sourceTimeSec));
        }

        if (target is not { } want)
        {
            if (_isMusicPreviewPlaying) StopMusicPreview();
            return;
        }

        if (!_isMusicPreviewPlaying)
        {
            if (!isDraggingAnyMarker) StartMusicPreview(want.Path, want.PositionSec);
            return;
        }

        // Crossed into another track of the bed (multi-track or LOOP_01): load it where it belongs.
        if (!string.Equals(want.Path, _musicPreviewPath, StringComparison.OrdinalIgnoreCase))
        {
            StartMusicPreview(want.Path, want.PositionSec);
            return;
        }

        if (Environment.TickCount64 < _musicSyncHoldUntilTicks || _musicPreviewIpcClient == null) return;

        double actual = _musicPreviewIpcClient.CurrentTime;
        if (Math.Abs(actual - want.PositionSec) <= Infrastructure.PreviewAudioSync.DriftToleranceSec)
        {
            _musicDriftStrikes = 0;
            return;
        }

        if (++_musicDriftStrikes < Infrastructure.PreviewAudioSync.DriftStrikes) return;
        _musicDriftStrikes = 0;

        // Seek, never re-rate: the music stays at its normal speed.
        _ = _musicPreviewIpcClient.SendCommandAsync("seek", want.PositionSec, "absolute");
        _musicSyncHoldUntilTicks = Environment.TickCount64 + MusicSyncSettleMs;
    }

    private async void StartMusicPreview(string path, double positionSec)
    {
        if (_musicWizardResult == null || string.IsNullOrEmpty(path)) return;

        string mpvExe = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? System.AppContext.BaseDirectory, "binaries", "mpv.exe");
        if (!System.IO.File.Exists(mpvExe)) mpvExe = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? System.AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "mpv.exe");
        if (!System.IO.File.Exists(mpvExe)) mpvExe = "mpv.exe";

        if (_musicPreviewIpcClient == null)
        {
            _musicPreviewIpcClient = new FortniteVideoSoftware.Core.Media.MpvIpcClient();
            await _musicPreviewIpcClient.StartAudioOnlyAsync(mpvExe);
        }

        var volSlider = VolumeSliderCtl;
        double masterVol = volSlider?.Value ?? 100.0;
        double effectiveMusicVol = masterVol * _musicWizardResult.MusicVolume;

        await _musicPreviewIpcClient.SetPreviewVolumeAsync(effectiveMusicVol);
        // MUSICSYNC_02 — the music's own playback rate is pinned to normal speed. Never anything else.
        await _musicPreviewIpcClient.SetPropertyDoubleAsync("speed", 1.0);
        // LoadFileAsync starts playback (it unpauses) at the requested position.
        await _musicPreviewIpcClient.LoadFileAsync(path, Math.Max(0, positionSec));

        _musicPreviewPath = path;
        _musicDriftStrikes = 0;
        _musicSyncHoldUntilTicks = Environment.TickCount64 + MusicSyncSettleMs;
        _isMusicPreviewPlaying = true;
        _lastMusicPreviewSyncTime = positionSec;
        _playingMusicTimelineStartSeconds = _musicWizardResult.TimelineStartSeconds;
    }

    private async void StopMusicPreview()
    {
        if (_musicPreviewIpcClient != null)
        {
            await _musicPreviewIpcClient.SetPropertyAsync("pause", "yes");
            await _musicPreviewIpcClient.SendCommandAsync("stop");
        }
        _isMusicPreviewPlaying = false;
        _musicPreviewPath = null;
        _musicDriftStrikes = 0;
    }

    /// <summary>MUSICSYNC_02 — every voice-over take follows the video in output time.</summary>
    private void UpdateVoiceOverPreview(double sourceTimeSec, bool videoEnded)
    {
        if (_voiceOverResult == null || _voiceOverPreviewTakes.Count == 0) return;

        var ipc = ActiveVideoHost?.IpcClient;
        bool isPaused = ipc?.IsPaused ?? true;
        if (_isCurrentlyFrozen) isPaused = false;

        double editedTime = PreviewOutputSeconds(sourceTimeSec);
        var timeline = _viewModel.Timeline;

        foreach (var take in _voiceOverPreviewTakes)
        {
            // Take start in output seconds, from the SAME mapping as the playhead. It is
            // recomputed each tick (the timeline is cached), so trims, cuts and speed edits
            // re-place the take immediately.
            take.StartProjectSec = timeline.PreviewSourceToOutputSeconds(take.Take.StartSec * 1000.0);

            double voiceTime = editedTime - take.StartProjectSec;
            bool shouldPlayVoice = !isPaused && !videoEnded && voiceTime >= 0 && voiceTime <= take.Reader.TotalTime.TotalSeconds;

            try
            {
                Infrastructure.PreviewAudioSync.SyncVoiceTake(take.Reader, take.Player, shouldPlayVoice, voiceTime, ref take.DriftStrikes);
            }
            catch (Exception ex) { RuntimeLog.SwallowedThrottled(ex); }
        }
    }
}
