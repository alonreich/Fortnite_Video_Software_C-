
using System;
using System.Collections.Generic;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// SCRAPER_02 / MUSICSYNC_02 — THE MERGER PREVIEW PLAYS THE MUSIC WHERE THE EXPORT PUTS IT.
///
/// Music time in the merger is OUTPUT time: the body output second of the preview schedule
/// (MERGEPREVIEW_01, <see cref="MergerPreviewPlan.OutputSecAt"/>), the same mapping the export's
/// MusicExportSec uses; merged seconds ÷ merge speed only while the edit list does not describe the
/// queue. The bed is laid out by the shared <see cref="MusicBedPlan"/>. Because the merged timeline already excludes
/// the removed intros, the preview and the file agree to the frame.
///
/// Same rules as the Main App (MainWindow.PreviewAudio.cs):
///   • The music plays at its own normal speed, always. Speed is pinned to 1.0; only the video changes speed.
///   • Drift is corrected by SEEKING only, after <see cref="Infrastructure.PreviewAudioSync.DriftStrikes"/>
///     readings beyond <see cref="Infrastructure.PreviewAudioSync.DriftToleranceSec"/>.
///   • Silent while the preview is in its muted browsing mode (AUTOPREVIEW_01).
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class VideoMergerWindow
{
    private const int MergerMusicSettleMs = 350;

    private MpvIpcClient? _mergerMusicClient;
    private bool _mergerMusicPlaying;
    private bool _mergerMusicStarting;
    private string? _mergerMusicPath;
    private int _mergerMusicStrikes;
    private long _mergerMusicHoldUntil;
    private object? _mergerMusicPlanKey;
    private IReadOnlyList<MusicBedSegment> _mergerMusicPlan = Array.Empty<MusicBedSegment>();

    private IReadOnlyList<MusicBedSegment> GetMergerMusicPlan(double speed)
    {
        var r = _musicResult;
        if (r == null || string.IsNullOrEmpty(r.MusicFilePath)) return Array.Empty<MusicBedSegment>();

        double startDelay = _plan?.OutputSecAt(r.TimelineStartSeconds) ?? r.TimelineStartSeconds / speed;
        double bed = (_plan?.OutputSecAt(r.TimelineEndSeconds) ?? r.TimelineEndSeconds / speed) - startDelay;
        if (bed <= 0) bed = 1.0;

        var key = (r, startDelay, bed, r.OffsetSeconds, r.LoopMusic, r.MusicFilePath, r.MusicFilePaths.Count, r.MusicDurationSeconds);
        if (Equals(key, _mergerMusicPlanKey)) return _mergerMusicPlan;

        var paths = new List<string>();
        if (r.MusicFilePaths.Count > 0)
        {
            foreach (var p in r.MusicFilePaths)
                if (!string.IsNullOrWhiteSpace(p) && System.IO.File.Exists(p)) paths.Add(p);
        }
        else paths.Add(r.MusicFilePath);

        var durations = new List<double>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            double known = i < r.MusicDurationsSeconds.Count ? r.MusicDurationsSeconds[i] : (i == 0 ? r.MusicDurationSeconds : 0);
            durations.Add(known);
        }

        _mergerMusicPlan = MusicBedPlan.Build(paths, durations, r.OffsetSeconds, bed, r.LoopMusic, startDelay);
        _mergerMusicPlanKey = key;
        return _mergerMusicPlan;
    }

    /// <param name="outputSec">Body OUTPUT second on screen (<see cref="PreviewOutputSec"/>): runs on through a freeze.</param>
    private void UpdateMergerMusicPreview(double outputSec, bool videoPlaying)
    {
        double speed = _baseSpeed > 0.01 ? _baseSpeed : 1.0;
        (string Path, double PositionSec, int SegmentIndex)? want = null;
        if (videoPlaying && !_previewMuted && !_musicIsStale && _musicResult != null && _clipLoadGraceTicks == 0)
            want = MusicBedPlan.Locate(GetMergerMusicPlan(speed), outputSec);

        if (want is not { } w)
        {
            if (_mergerMusicPlaying) _ = StopMergerMusicPreview();
            return;
        }

        if (_mergerMusicStarting) return;
        if (!_mergerMusicPlaying || !string.Equals(w.Path, _mergerMusicPath, StringComparison.OrdinalIgnoreCase))
        {
            _ = StartMergerMusicPreview(w.Path, w.PositionSec);
            return;
        }

        if (Environment.TickCount64 < _mergerMusicHoldUntil || _mergerMusicClient == null) return;
        if (Math.Abs(_mergerMusicClient.CurrentTime - w.PositionSec) <= Infrastructure.PreviewAudioSync.DriftToleranceSec)
        {
            _mergerMusicStrikes = 0;
            return;
        }
        if (++_mergerMusicStrikes < Infrastructure.PreviewAudioSync.DriftStrikes) return;
        _mergerMusicStrikes = 0;
        _ = _mergerMusicClient.SendCommandAsync("seek", w.PositionSec, "absolute");
        _mergerMusicHoldUntil = Environment.TickCount64 + MergerMusicSettleMs;
    }

    private async System.Threading.Tasks.Task StartMergerMusicPreview(string path, double positionSec)
    {
        if (_musicResult == null) return;
        _mergerMusicStarting = true;
        try
        {
            if (_mergerMusicClient == null)
            {
                string baseDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                string mpvExe = System.IO.Path.Combine(baseDir, "frontend", "mpv.exe");
                if (!System.IO.File.Exists(mpvExe)) mpvExe = System.IO.Path.Combine(baseDir, "binaries", "mpv.exe");
                if (!System.IO.File.Exists(mpvExe)) mpvExe = "mpv.exe";
                _mergerMusicClient = new MpvIpcClient();
                await _mergerMusicClient.StartAudioOnlyAsync(mpvExe);
            }

            double master = VolumeSliderCtl?.Value ?? 100.0;
            await _mergerMusicClient.SetPreviewVolumeAsync(master * _musicResult.MusicVolume);
            await _mergerMusicClient.SetPropertyDoubleAsync("speed", 1.0);
            await _mergerMusicClient.LoadFileAsync(path, Math.Max(0, positionSec));

            _mergerMusicPath = path;
            _mergerMusicStrikes = 0;
            _mergerMusicHoldUntil = Environment.TickCount64 + MergerMusicSettleMs;
            _mergerMusicPlaying = true;
        }
        catch (Exception ex)
        {
            RuntimeLog.WarnThrottled("MERGER", $"Music preview could not start: {ex.Message}");
        }
        finally
        {
            _mergerMusicStarting = false;
        }
    }

    private async System.Threading.Tasks.Task StopMergerMusicPreview()
    {
        _mergerMusicPlaying = false;
        _mergerMusicPath = null;
        _mergerMusicStrikes = 0;
        try
        {
            if (_mergerMusicClient != null)
            {
                await _mergerMusicClient.SetPropertyAsync("pause", "yes");
                await _mergerMusicClient.SendCommandAsync("stop");
            }
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private void ShutdownMergerMusicPreview()
    {
        _mergerMusicPlaying = false;
        try { _mergerMusicClient?.Dispose(); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        _mergerMusicClient = null;
    }
}
