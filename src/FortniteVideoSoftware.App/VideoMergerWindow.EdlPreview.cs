// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Linq;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// MERGEPREVIEW_EDL_01 — THE MERGE PLAYS AS ONE FILE (Video-Merger-Migration.md P4.2, D2).
///
/// Once the queue is analysed, mpv holds ONE inline EDL of every clip's kept window
/// (<see cref="MergeEditorSource.MpvUrl"/> over the merged timeline's composite): mpv's time-pos IS
/// the merged clock, boundaries are seamless (P4.1 PASS), removed intros are never shown. The URL is
/// compared every tick; a new layout (reorder, remove, scraper, thumbnail) reloads it at the SAME
/// moment of the SAME file. Loading is mpv's own asynchronous loadfile — nothing here waits.
///
/// MERGEPREVIEW_01 — PREVIEW PARITY (P8.1). The effects are played the way the Main App previews
/// them, from <see cref="MergerPreviewPlan"/> (the export's own chunk list): mpv <c>speed</c> per
/// stretch (absolute segment speed, else the base speed — D19), a jump over deleted footage, a timed
/// pause for a freeze (consumed once per pass, re-armed by a seek — the FREEZE_01 rule). P8.2: a
/// meme is played by the Main App's own <see cref="Infrastructure.MemePreviewDirector"/> (MEME_07):
/// forward playback crossing it parks the EDL, plays the meme file in the one mpv host behind the
/// "Loading the meme…" veil, then reloads the SAME EDL at the anchor. The music preview runs on the
/// BODY OUTPUT clock (<see cref="PreviewOutputSec"/>), so it keeps playing through a freeze exactly
/// as it does in the file, and never changes speed.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class VideoMergerWindow
{
    private const int EdlLoadGraceTicks = 8;   // 8 x 100 ms: mpv may still report the previous file

    private string? _edlUrl;                   // the EDL mpv holds (null = a single file or nothing)
    private MergedTimeline? _edlTimeline;      // the layout that URL was built from
    private double _edlLoadTarget;

    private MergeEdl? _planEdl;
    private MergerPreviewPlan? _plan;          // null = the edit list does not describe the queue yet
    private double _appliedSpeed = double.NaN;
    private int _consumedHold = -1;
    private int _planMismatchTicks;
    private int _holdStep = -1;
    private long _holdUntilTicks;
    private long _holdStartTicks;
    private double _prevTickPos = -1;
    private Infrastructure.MemePreviewDirector? _memePreview;

    /// <summary>
    /// EMPTYQUEUE_01 — the last clip left the queue: stop mpv, forget the EDL and hide the video surface,
    /// so the NO VIDEO LOADED screen is not drawn over a ghost of the last frame (it is semi-transparent).
    /// </summary>
    private void ClearPreviewSurface()
    {
        var ipc = _videoHost?.IpcClient;
        ForgetLoadedEdl();
        _playClip = -1;
        _playPath = null;
        _lastMergedPos = 0;
        _mergedEndReached = false;
        _pendingMiddleSeekPath = null;
        _autoAdvanceArmed = false;
        if (_mergerMusicPlaying) _ = StopMergerMusicPreview();
        if (ipc != null)
        {
            _ = ipc.SetPropertyAsync("pause", "yes");
            _ = ipc.SendCommandAsync("stop");
        }
        if (_videoHost != null) _videoHost.IsVisible = false;
        RuntimeLog.Info("MERGER", "Queue empty: preview stopped and the video surface hidden.");
    }

    /// <summary>The legacy per-file preview took mpv over.</summary>
    private void ForgetLoadedEdl()
    {
        _edlUrl = null;
        _edlTimeline = null;
        EndHold(resume: false);
    }

    /// <summary>
    /// Makes mpv hold the EDL of the current layout. <paramref name="target"/> null = keep the current
    /// moment (remapped through the clip it is on). Returns true when a load was issued.
    /// </summary>
    private bool EnsureEdlLoaded(double? target, bool? play)
    {
        var ipc = _videoHost?.IpcClient;
        if (ipc == null || _timeline.Clips.Count == 0) return false;
        string url = MergeEditorSource.Build(_timeline.Composite).MpvUrl;
        if (url == _edlUrl) return false;

        bool hadEdl = _edlUrl != null;
        double at = target ?? CurrentMomentOnNewLayout(ipc);
        bool wasPlaying = hadEdl ? !ipc.IsPaused && !_mergedEndReached : !ipc.IsPaused && (VideoListCtl?.SelectedIndex ?? -1) >= 0;
        bool playNow = play ?? wasPlaying;
        at = Math.Clamp(at, 0, _timeline.TotalSec);

        _edlUrl = url;
        _edlTimeline = _timeline;
        _edlLoadTarget = at;
        _clipLoadGraceTicks = EdlLoadGraceTicks;
        _lastMergedPos = at;
        _mergedEndReached = false;
        _appliedSpeed = double.NaN;
        RearmPreviewEffects(at);
        _pendingMiddleSeekPath = null;   // the legacy per-clip machinery stands down
        _autoAdvanceArmed = false;

        _ = LoadEdlAsync(ipc, url, at, playNow);
        RuntimeLog.Info("MERGER", $"Preview: merge loaded as one EDL ({_timeline.Clips.Count} clip(s), {_timeline.TotalSec:F3}s) at {at:F3}s, {(playNow ? "playing" : "paused")}.");
        return true;
    }

    private async System.Threading.Tasks.Task LoadEdlAsync(MpvIpcClient ipc, string url, double at, bool play)
    {
        try
        {
            await ipc.SetPropertyAsync("hr-seek", "yes");   // P4.1 criterion 4: a seek lands on the exact frame
            await ipc.SetPropertyAsync("mute", _previewMuted ? "yes" : "no");
            await ipc.LoadFileAsync(url, at);
            await ipc.SetPropertyAsync("pause", play ? "no" : "yes");
        }
        catch (Exception ex)
        {
            RuntimeLog.WarnThrottled("MERGER", $"Preview could not load the merge: {ex.Message}");
            RuntimeLog.Swallowed(ex);
        }
    }

    /// <summary>Where the playhead is, expressed on the NEW layout (same file, same source second).</summary>
    private double CurrentMomentOnNewLayout(MpvIpcClient ipc)
    {
        if (_edlTimeline is { } old && old.Clips.Count > 0)
        {
            var (oi, src) = old.Locate(_lastMergedPos);
            if (oi < 0) return 0;
            string path = old.Clips[oi].Path;
            int ni = oi < _timeline.Clips.Count && SameVideoPath(_timeline.Clips[oi].Path, path)
                ? oi
                : _timeline.Clips.ToList().FindIndex(c => SameVideoPath(c.Path, path));
            return ni < 0 ? Math.Min(_lastMergedPos, _timeline.TotalSec) : _timeline.ToMerged(ni, src);
        }
        // The legacy per-file preview was showing the selected clip.
        int sel = VideoListCtl?.SelectedIndex ?? -1;
        return sel >= 0 && sel < _timeline.Clips.Count ? _timeline.ToMerged(sel, ipc.CurrentTime) : 0;
    }

    /// <summary>mpv's time-pos on the merged clock (the load target while mpv still reports the previous file).</summary>
    private double EdlPositionSec(MpvIpcClient ipc)
    {
        double t = ipc.CurrentTime;
        if (_clipLoadGraceTicks > 0)
        {
            _clipLoadGraceTicks--;
            if (Math.Abs(t - _edlLoadTarget) > 0.75) return _edlLoadTarget;
            _clipLoadGraceTicks = 0;
        }
        return Math.Clamp(t, 0, _timeline.TotalSec);
    }

    // ── P8.1 — effects ─────────────────────────────────────────────────────────────────────────

    /// <summary>Rebuilds the schedule when the edit list changes. No schedule while it does not describe the queue.</summary>
    private void EnsurePreviewPlan()
    {
        var edl = _lastEdl;
        if (ReferenceEquals(edl, _planEdl) && _plan != null) return;
        _planEdl = edl;
        _plan = null;
        if (edl == null || edl.Clips.Count != _timeline.Clips.Count
            || !edl.Clips.Select(c => c.Path).SequenceEqual(_timeline.Clips.Select(c => c.Path), StringComparer.OrdinalIgnoreCase))
            return;
        var composite = CompositeTimeline.Build(edl);
        if (composite.TotalMergedFrames != _timeline.Composite.TotalMergedFrames)
        {
            // The analysis and the edit list disagree on a kept window. Normal for a tick or two right
            // after clips are added (the capture is posted after the analysis lands, P10.7); only a
            // disagreement that PERSISTS (~2 s) is worth a warning.
            _planEdl = null;   // re-evaluate next tick
            if (++_planMismatchTicks == 20)
                RuntimeLog.Warn("MERGER", $"Preview effects are off: the edit list ({composite.TotalMergedFrames} frames) and the analysed timeline ({_timeline.Composite.TotalMergedFrames} frames) still differ.");
            return;
        }
        _planMismatchTicks = 0;
        _plan = MergerPreviewPlan.Build(composite);
        _appliedSpeed = double.NaN;
    }

    /// <summary>A seek or a load starts a new pass: holds before the target are behind us, the one AT it plays.</summary>
    private void RearmPreviewEffects(double mergedSec)
    {
        EndHold(resume: false);
        _consumedHold = _plan?.LastStepIndexBefore(mergedSec) ?? -1;
        _prevTickPos = mergedSec;
        _memePreview?.NotifySeek();   // MEME_07 — a seek is never a crossing
    }

    /// <summary>True while a freeze (or, until P8.2, a meme) is being held.</summary>
    private bool TickPreviewEffects(MpvIpcClient ipc, ref double merged)
    {
        var plan = _plan;
        if (plan == null) { _prevTickPos = merged; return false; }

        if (_holdStep >= 0)
        {
            var hold = plan.Steps.ElementAtOrDefault(_holdStep);
            if (hold == null) { EndHold(resume: false); }
            else if (!ipc.IsPaused)
            {
                // The user pressed the (pause-showing) transport during the hold: that is a pause.
                EndHold(resume: false);
                _ = ipc.SetPropertyAsync("pause", "yes");
                merged = hold.MergedStartSec;
                _prevTickPos = merged;
                return false;
            }
            else if (Environment.TickCount64 < _holdUntilTicks)
            {
                merged = hold.MergedStartSec;
                return true;
            }
            else
            {
                merged = hold.MergedStartSec;
                EndHold(resume: true);
                _prevTickPos = merged;
                return false;
            }
        }

        if (ipc.IsPaused || _clipLoadGraceTicks > 0 || _mergedEndReached)
        {
            _prevTickPos = merged;
            return false;
        }

        // CUT_01 parity — deleted footage is jumped over, never played.
        if (plan.CutResumeAt(merged) is double resume)
        {
            _ = SeekInternal(resume);
            merged = resume;
        }

        // FREEZE_01 parity — every hold between the last tick and now fires once.
        double from = Math.Min(_prevTickPos, merged);
        int h = plan.NextHold(from, merged, _consumedHold, includeMemes: false);   // memes: MemePreviewDirector
        if (h >= 0)
        {
            var step = plan.Steps[h];
            _consumedHold = h;
            _holdStep = h;
            _holdStartTicks = Environment.TickCount64;
            _holdUntilTicks = _holdStartTicks + (long)Math.Round(step.HoldSec * 1000.0);
            _ = ipc.SetPropertyAsync("pause", "yes");
            _ = SeekInternal(step.MergedStartSec);   // show the held frame, not the one a tick later
            merged = step.MergedStartSec;
            RuntimeLog.Debug("MERGER", $"Preview hold: {step.Kind} at {step.MergedStartSec:F3}s for {step.HoldSec:F2}s.");
            return true;
        }
        _prevTickPos = merged;

        double target = plan.SpeedAt(merged, _baseSpeed);
        if (!(Math.Abs(target - _appliedSpeed) < 0.0005))
        {
            _appliedSpeed = target;
            _ = ipc.SetPropertyAsync("speed", target.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture));
        }
        return false;
    }

    // ── P8.2 — memes ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// MEME_07 in the Merger. True while mpv is NOT showing the merge (a meme is loading, playing or
    /// handing back): the tick must then touch nothing that reads mpv's clock — the caret holds.
    /// </summary>
    private bool TickMergerMemes()
    {
        IReadOnlyList<MemePlacement> memes = _plan?.MemePlacements ?? Array.Empty<MemePlacement>();
        if (memes.Count > 0 && _memePreview == null)
        {
            _memePreview = new Infrastructure.MemePreviewDirector(
                () => _videoHost?.IpcClient,
                () => _edlUrl,          // the gameplay is the EDL; its clock is the merged clock
                () => 0.0,              // placements are already on the merged clock
                (visible, message) => Infrastructure.MemeSwapOverlay.Set(this, visible, message),
                "MERGER");
            // Same agreed behaviour as the Main App: the meme's own sound plays, the music stops and
            // picks up again at the right output moment once the merge is back.
            _memePreview.MemeStarted += () => _ = StopMergerMusicPreview();
            _memePreview.MemeEnded += () => _appliedSpeed = double.NaN;   // the director restored mpv's speed; re-assert the schedule
        }
        if (_memePreview == null) return false;

        // A meme only starts from real forward playback of a loaded EDL, never mid-load or mid-hold.
        _memePreview.Suspended = _edlUrl == null || _clipLoadGraceTicks > 0 || _holdStep >= 0 || _draggingThumbMarker || _draggingClipChip;
        _memePreview.SetMemes(memes);
        _memePreview.Tick();
        return _memePreview.IsActive;
    }

    private void EndHold(bool resume)
    {
        if (_holdStep < 0) return;
        _holdStep = -1;
        if (resume) _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "no");
    }

    /// <summary>
    /// The body OUTPUT second on screen (the music preview's clock): through the schedule when the edit
    /// list describes the queue, else merged ÷ base speed (the rule before effects).
    /// </summary>
    private double PreviewOutputSec(double merged)
    {
        var plan = _plan;
        if (plan == null) return merged / (_baseSpeed > 0.01 ? _baseSpeed : 1.0);
        double t = plan.OutputSecAt(merged);
        if (_holdStep >= 0 && plan.Steps.ElementAtOrDefault(_holdStep) is { } hold)
            t = hold.OutputStartSec + Math.Min(hold.HoldSec, (Environment.TickCount64 - _holdStartTicks) / 1000.0);
        return t;
    }
}
