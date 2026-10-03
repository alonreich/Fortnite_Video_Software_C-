// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// MERGESESSION_01 — THE MERGER SURVIVES ITS WINDOW (Video-Merger-Migration.md P3.2, D6).
///
/// • Every queue row has a stable ClipId (<see cref="ClipIdList"/>), kept in step with the queue's
///   collection events, so effects and anchors follow a clip through reorders.
/// • Any edit calls <see cref="NoteEdlChanged"/>. It only POSTS one coalesced capture; the capture
///   (<see cref="MergerSession.Capture"/>, pure and cheap) runs once per burst on the UI thread and
///   hands the edit list to the project document (ToolNavigator) and to the write-behind autosave,
///   whose I/O is on the thread pool. Nothing here blocks the UI thread.
/// • On open with an empty queue the Merger restores the project's edit list, or else the autosave
///   (read off the UI thread). A file that is gone is left out and named; a file that changed is
///   kept, re-analysed and named. Nothing here can crash the window.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class VideoMergerWindow
{
    private static readonly Lazy<MergerAutosaveStore> MergerAutosave =
        new(() => new MergerAutosaveStore(ApplicationPaths.CreateDefault().MergerSessionFile));

    private readonly ClipIdList _clipIds = new();
    private MergeEdl? _lastEdl;
    private bool _edlCapturePosted;
    private bool _restoringSession;

    /// <summary>The newest captured edit list (undo, P3.3, reads this).</summary>
    private MergeEdl CurrentEdl => _lastEdl ?? MergeEdl.Empty;

    private void InitializeSession()
    {
        VideoQueue.CollectionChanged += TrackClipIds;
        InitializeHistory();   // MERGEUNDO_01
        InitializeLanes();     // LANES_01
        InitializeTimelineSelection();   // ANTS_01
        InitializeMergerGranular();      // MERGEEDIT_02
        InitializeClipActions();         // CLIPACTIONS_01 (D20)
        _clipIds.Reset(VideoQueue.Count);
        this.Opened += async (s, e) => await RestoreSessionAsync();
    }

    private void TrackClipIds(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_clipIds.Apply(e, VideoQueue.Count))
            RuntimeLog.WarnThrottled("MERGER", "Clip id list re-synchronised with the queue.");
        // REMOVEUX_01 — every user removal says how to get the clip back (undo/redo/restore do not).
        if (e.Action == NotifyCollectionChangedAction.Remove && !_restoringSession && !_applyingHistory)
            NoteClipsRemoved(e.OldItems?.Count ?? 1);
        // EMPTYQUEUE_01 — no clips: no picture. A clip arrives: the surface is shown again.
        if (VideoQueue.Count == 0) ClearPreviewSurface();
        else if (_videoHost is { IsVisible: false } host) host.IsVisible = true;
        if (e.Action == NotifyCollectionChangedAction.Move)
        {
            // D22 — a reorder never seeks: the list re-selecting the moved row must not start a preview.
            RuntimeLog.Info("MERGER", $"Reorder: clip {e.OldStartingIndex + 1} moved to position {e.NewStartingIndex + 1}; the playhead stays on its clip.");
            _selectWithoutPreview = true;
            Dispatcher.UIThread.Post(() => _selectWithoutPreview = false, DispatcherPriority.Background);
        }
        // D20 — the timeline follows the list at once when nothing needs probing. EDLNULL_01: this runs
        // INSIDE the queue's CollectionChanged, so nothing it throws may escape (it would end the app).
        try { RebuildTimelineFromCacheNow(); }
        catch (Exception ex)
        {
            RuntimeLog.WarnThrottled("MERGER", $"Instant timeline refresh failed; the background rebuild will catch up: {ex.Message}");
            RuntimeLog.Swallowed(ex);
        }
    }

    private int _removedPending;

    /// <summary>REMOVEUX_01 — one notice per removal gesture (a multi-row remove raises several events).</summary>
    private void NoteClipsRemoved(int count)
    {
        bool first = _removedPending == 0;
        _removedPending += Math.Max(1, count);
        if (!first) return;
        Dispatcher.UIThread.Post(() =>
        {
            int n = _removedPending;
            _removedPending = 0;
            Controls.FloatingNotice.Show(this,
                (n == 1 ? "Clip removed from the list." : $"{n} clips removed from the list.") + " Press Ctrl+Z to undo.",
                Controls.NoticeKind.Info);
        }, DispatcherPriority.Background);
    }

    /// <summary>Something the edit list records changed: capture once, soon, on the UI thread.</summary>
    private void NoteEdlChanged()
    {
        _appliedSpeed = double.NaN;   // MERGEPREVIEW_01 — the wheel set mpv's speed directly; the tick re-asserts the schedule
        if (_restoringSession || _edlCapturePosted) return;
        _edlCapturePosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _edlCapturePosted = false;
            CaptureEdlNow(flush: false);
        }, DispatcherPriority.Background);
    }

    /// <summary>Captures the edit list now and publishes it. <paramref name="flush"/> also starts the autosave write immediately (close path).</summary>
    private void CaptureEdlNow(bool flush)
    {
        if (_restoringSession) return;
        try
        {
            _clipIds.EnsureCount(VideoQueue.Count);
            var state = new MergerUiState
            {
                Paths = VideoQueue.ToList(),
                Ids = _clipIds.Ids.ToList(),
                ScraperEnabled = _scraperEnabled,
                BaseSpeed = _baseSpeed,
                ThumbPath = _thumbPath,
                ThumbSourceSec = _thumbSourceSec,
                Music = _musicIsStale ? null : MusicStateFromResult(_musicResult),
            };
            var edl = MergerSession.Capture(state, AnalysisFor, FileIdentity, TimelineMatchesQueue() ? _timeline : null, _lastEdl);
            // Stale music belongs to an older queue: keep its saved placement until the user re-sets it.
            if (_musicIsStale && _lastEdl?.Music is EdlMusic keep) edl = edl with { Music = keep };

            bool changed = !Equals(edl, _lastEdl);
            _lastEdl = edl;
            RecordHistory(edl);   // MERGEUNDO_01
            Services.ToolNavigator.PublishMergeEdl(edl);
            if (changed) MergerAutosave.Value.Schedule(edl);
            if (changed) { UpdateEstimatedSize(); PaintMergedLength(); }   // MERGESIZE_01 — effects/speed change the finished length
            if (flush) _ = MergerAutosave.Value.FlushAsync();
        }
        catch (Exception ex)
        {
            // EDLNULL_01 — a failing capture means the autosave and the project stop following the
            // queue. That must be LOUD in the log (it was a silent debug line and the queue froze).
            RuntimeLog.WarnThrottled("MERGER", $"Could not record the merge state (autosave not updated): {ex.GetType().Name}: {ex.Message}");
            RuntimeLog.Swallowed(ex);
        }
    }

    private static MergeClipInfo? AnalysisFor(string path)
        => MergeClipAnalyzer.TryGetCompleted(path, out var info) ? info : null;

    private static (long Size, long WriteTicks)? FileIdentity(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return null;
        }
    }

    private static MergerMusicState? MusicStateFromResult(MusicWizardResult? r)
    {
        if (r == null) return null;
        var paths = r.MusicFilePaths.Count > 0 ? r.MusicFilePaths.ToList() : new List<string> { r.MusicFilePath };
        paths.RemoveAll(string.IsNullOrWhiteSpace);
        if (paths.Count == 0) return null;
        var durations = r.MusicDurationsSeconds.Count > 0 ? r.MusicDurationsSeconds.ToList() : new List<double> { r.MusicDurationSeconds };
        return new MergerMusicState(paths, durations, r.OffsetSeconds, r.TimelineStartSeconds, r.TimelineEndSeconds,
            r.MusicVolume, r.VideoVolume, r.LoopMusic, r.EnableDucking, r.EnableCarving);
    }

    /// <summary>Opened with an empty queue: bring back the project's merge, or the autosaved one.</summary>
    private async Task RestoreSessionAsync()
    {
        if (VideoQueue.Count > 0) return;
        try
        {
            var projectEdl = Services.ToolNavigator.ReadMergeQueue()?.ToEdl();
            MergeEdl? saved = projectEdl is { Clips.Count: > 0 }
                ? projectEdl
                : await Task.Run(() => MergerAutosave.Value.Load());
            if (saved is null || VideoQueue.Count > 0) return;

            var plan = await Task.Run(() => MergerSession.Plan(saved, FileIdentity));
            if (VideoQueue.Count > 0) return;   // the user added clips while we were reading
            await ApplyRestorePlanAsync(plan, projectEdl is { Clips.Count: > 0 } ? "project" : "last session");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MERGER", $"Could not restore the previous merge: {ex.Message}");
            _restoringSession = false;
        }
    }

    private async Task ApplyRestorePlanAsync(MergerRestorePlan plan, string origin)
    {
        var edl = plan.Edl;

        // RESTOREMISS_01 — the session is restored silently UNLESS files are gone or changed: then the
        // user sees exactly which file, from which folder, and approves what happens next.
        foreach (var m in plan.Missing) RuntimeLog.Warn("MERGER", $"Restore: missing on disk: {m}");
        foreach (var c in plan.Changed) RuntimeLog.Info("MERGER", $"Restore: changed on disk: {c}");
        if (plan.Missing.Count > 0 || plan.Changed.Count > 0)
        {
            var dlg = new Controls.ConfirmDialogWindow();
            dlg.SetTitle(plan.Missing.Count > 0 ? "Some clips are missing" : "Some clips changed");
            dlg.SetMessage(MergerSession.DescribeRestoreProblems(plan.Missing, plan.Changed, edl.Clips.Count));
            if (edl.Clips.Count > 0) dlg.SetButtonText("CONTINUE WITHOUT THEM", "START FRESH");
            else dlg.SetButtonText("OK", "CLOSE");
            await dlg.ShowDialog(this);
            if (edl.Clips.Count == 0 || !dlg.Result)
            {
                MergerAutosave.Value.Clear();
                Services.ToolNavigator.PublishMergeEdl(MergeEdl.Empty);
                RuntimeLog.Info("MERGER", edl.Clips.Count == 0
                    ? "Restore: nothing left to restore; the saved session was cleared."
                    : "Restore: the user chose to start fresh; the saved session was cleared.");
                return;
            }
        }
        if (edl.Clips.Count == 0) return;

        _lastEdl = null;
        await ApplyEdlStateAsync(edl, from: null);   // MERGEUNDO_01 — the same path undo/redo use
        CaptureEdlNow(flush: false);
        ResetHistory();   // MERGEUNDO_01 — the restored merge is where undo starts

        string msg = $"Restored the {origin} merge: {edl.Clips.Count} clip(s)"
            + (edl.Music != null && _musicResult != null ? ", music" : "")
            + (_thumbPath != null ? ", custom thumbnail" : "") + ".";
        RuntimeLog.Info("MERGER", msg + $" Missing: {plan.Missing.Count}, changed: {plan.Changed.Count}.");
        if (plan.Missing.Count > 0 || plan.Changed.Count > 0)
        {
            var parts = new List<string>();
            if (plan.Missing.Count > 0) parts.Add("left out (file not found): " + string.Join(", ", plan.Missing.Select(Path.GetFileName)));
            if (plan.Changed.Count > 0) parts.Add("changed since last time: " + string.Join(", ", plan.Changed.Select(Path.GetFileName)));
            SetQueueStatus(msg + " " + string.Join("; ", parts) + ".", true);
        }
        else
        {
            Controls.FloatingNotice.Show(this, msg, Controls.NoticeKind.Info);
        }
    }
}
