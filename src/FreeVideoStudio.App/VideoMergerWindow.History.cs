
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Undo;

namespace FreeVideoStudio.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// MERGEUNDO_01 — UNDO/REDO FOR THE VIDEO MERGER (Video-Merger-Migration.md P3.3, D7).
///
/// One <see cref="UndoStack{T}"/> of edit lists (the app-wide U1–U4 rules). What is stored is
/// <see cref="MergerSession.UserEdit"/>: the edit list WITHOUT analysis results, so a clip finishing
/// its background probe is never an undo step. Every capture (<see cref="CaptureEdlNow"/>) offers
/// its edit list here; <see cref="MergerSession.DescribeChange"/> names it and groups continuous
/// gestures (speed wheel, thumbnail drag) into one step.
///
/// Undo/redo apply the target through <see cref="ApplyEdlStateAsync"/> — the SAME path a restored
/// session takes — which reorders the queue in place (<see cref="MergerSession.SyncQueue"/>), so the
/// clip being previewed survives an undone reorder.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class VideoMergerWindow
{
    private readonly UndoStack<MergeEdl> _history = new(MergerSession.UserEdit(MergeEdl.Empty));
    private bool _applyingHistory;
    private string? _nextHistoryLabel;
    private bool _historyKeysHooked;

    private void InitializeHistory()
    {
        if (_historyKeysHooked) return;
        _historyKeysHooked = true;
        AddHandler(InputElement.KeyDownEvent, HistoryKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>Offers a freshly captured edit list to the history.</summary>
    private void RecordHistory(MergeEdl edl)
    {
        var user = MergerSession.UserEdit(edl);
        if (_applyingHistory)
        {
            _history.ReplaceCurrent(user);
            return;
        }
        if (Equals(user, _history.Current)) { _nextHistoryLabel = null; return; }
        var (label, gesture) = MergerSession.DescribeChange(_history.Current, user);
        if (_nextHistoryLabel != null) { label = _nextHistoryLabel; gesture = null; _nextHistoryLabel = null; }
        if (_history.Apply(user, label, gesture))
            RuntimeLog.Info("MERGER", $"Undo step recorded: {label}.");
    }

    /// <summary>A new starting point (restored merge): no history before it.</summary>
    private void ResetHistory() => _history.Reset(MergerSession.UserEdit(CurrentEdl));

    /// <summary>A continuous gesture ended (thumbnail marker released): the next drag is its own step.</summary>
    private void EndHistoryGesture() => _history.EndGesture();

    private void HistoryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || (e.KeyModifiers & KeyModifiers.Control) == 0) return;
        bool undo = e.Key == Key.Z && (e.KeyModifiers & KeyModifiers.Shift) == 0;
        bool redo = e.Key == Key.Y || (e.Key == Key.Z && (e.KeyModifiers & KeyModifiers.Shift) != 0);
        if (!undo && !redo) return;
        if (FocusManager?.GetFocusedElement() is TextBox) return;
        e.Handled = true;
        _ = StepHistoryAsync(undo);
    }

    private async Task StepHistoryAsync(bool undo)
    {
        if (_activeMergerWorker != null)
        {
            Controls.FloatingNotice.Show(this, "Undo is paused while the merge is running.", Controls.NoticeKind.Info);
            return;
        }
        CaptureEdlNow(flush: false);
        string? label = undo ? _history.NextUndoLabel : _history.NextRedoLabel;
        MergeEdl? target = undo ? _history.Undo() : _history.Redo();
        if (target is null || label is null)
        {
            Controls.FloatingNotice.Show(this, undo ? "Nothing to undo." : "Nothing to redo.", Controls.NoticeKind.Info);
            return;
        }

        try
        {
            var from = MergerSession.UserEdit(CurrentEdl);
            await ApplyEdlStateAsync(target, from);
            _applyingHistory = true;
            try { CaptureEdlNow(flush: false); }
            finally { _applyingHistory = false; }
            RuntimeLog.Info("MERGER", $"{(undo ? "Undo" : "Redo")}: {label}.");
            Controls.FloatingNotice.Show(this, $"{(undo ? "Undo" : "Redo")}: {label}", Controls.NoticeKind.Info);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MERGER", $"{(undo ? "Undo" : "Redo")} of '{label}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Makes the window show <paramref name="target"/>: scraper (session only), speed, thumbnail,
    /// queue (reordered in place) and music. <paramref name="from"/> is the state being left
    /// (null = unknown, e.g. a restore); it is logged only.
    /// </summary>
    private async Task ApplyEdlStateAsync(MergeEdl target, MergeEdl? from)
    {
        RuntimeLog.Info("MERGER", $"Applying merge state: {target.Clips.Count} clip(s) (was {from?.Clips.Count.ToString() ?? "unknown"}).");
        _restoringSession = true;
        try
        {
            if (_scraperEnabled != target.ScraperEnabled)
            {
                _scraperEnabled = target.ScraperEnabled;
                var cb = ScraperCheckBoxCtl;
                if (cb != null)
                {
                    _suppressScraperToggle = true;
                    cb.IsChecked = _scraperEnabled;
                    _suppressScraperToggle = false;
                }
            }
            if (Math.Abs(_baseSpeed - target.BaseSpeed) > 0.001) ApplySpeedPreset(target.BaseSpeed);

            var thumbClip = target.Thumbnail is EdlThumbnail t ? target.Clips.FirstOrDefault(c => c.ClipId == t.At.ClipId) : null;
            _thumbPath = thumbClip?.Path;
            _thumbSourceSec = thumbClip != null && target.Thumbnail is EdlThumbnail t2 ? t2.At.SourceUs / 1_000_000.0 : 0;

            _musicResult = null;
            _musicTimeline = null;
            _musicIsStale = false;
            _musicQueueSignature = "";

            MergerSession.SyncQueue(VideoQueue, _clipIds, target.Clips);
            _lastEdl = target;

            {
                if (target.Music is EdlMusic music)
                {
                    await EnsureTimelineReadyAsync();
                    if (TimelineMatchesQueue() && MergerSession.MusicFromEdl(music, _timeline, _clipIds.Ids) is MergerMusicState m)
                    {
                        _musicResult = new MusicWizardResult
                        {
                            MusicFilePath = m.Paths[0],
                            MusicFilePaths = m.Paths.ToList(),
                            MusicDurationsSeconds = m.DurationsSec.ToList(),
                            MusicDurationSeconds = m.DurationsSec.Count > 0 ? m.DurationsSec[0] : 0,
                            OffsetSeconds = m.OffsetSec,
                            TimelineStartSeconds = m.StartMergedSec,
                            TimelineEndSeconds = m.EndMergedSec,
                            MusicVolume = m.MusicVolume,
                            VideoVolume = m.VideoVolume,
                            LoopMusic = m.Loop,
                            EnableDucking = m.Ducking,
                            EnableCarving = m.Carving,
                        };
                        _musicQueueSignature = string.Join("|", VideoQueue);
                        OnMusicPlaced();
                    }
                    else
                    {
                        RuntimeLog.WarnThrottled("MERGER", "The music placement could not be mapped onto the clips; add music again.");
                    }
                }
            }
        }
        finally
        {
            _restoringSession = false;
        }

        ScheduleTimelineRebuild();
        UpdateQueueState();
        UpdateEstimatedSize();
        InvalidateMergedTimelineDrawing();
    }
}
