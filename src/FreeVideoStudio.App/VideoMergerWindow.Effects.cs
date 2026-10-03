// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// MERGEEDIT_02 — GRANULAR EDIT ON THE WHOLE MERGE (Video-Merger-Migration.md P6.4, D16).
///
/// The button opens the Main App's Granular Speed Editor on the merge as ONE virtual file
/// (<see cref="MergeEditorSource"/>). On Accept every effect is put back into the clip it sits on
/// (<see cref="MergeEditorSource.FromEditor"/>), the edit list is replaced as ONE undo step
/// ("granular edit"), and the autosave follows. Cancel changes nothing.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class VideoMergerWindow
{
    private Button? _cGranularButton;
    private Button? GranularButtonCtl => _cGranularButton ??= this.FindControl<Button>("MergerGranularButton");
    private bool _granularOpen;

    private void InitializeMergerGranular()
    {
        var btn = GranularButtonCtl;
        if (btn != null) btn.Click += (s, e) => _ = OpenMergeGranularEditorAsync();
        UpdateMergerGranularButton();
    }

    /// <summary>Enabled once the merged timeline describes the queue (the editor needs the clip windows).</summary>
    private void UpdateMergerGranularButton()
    {
        var btn = GranularButtonCtl;
        if (btn == null) return;
        btn.IsEnabled = !_granularOpen && VideoQueue.Count > 0 && TimelineMatchesQueue();
        int effects;
        try { effects = CurrentEdl.Clips.Count(c => !c.Effects.IsEmpty); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); effects = 0; }   // EDLNULL_01 — a paint must never take the app down
        // P10 (item 3) — the Main App's GRANULAR SPEED / EDIT SPEEDS states (MainWindow.SetGranularButtonActive).
        if (effects > 0)
        {
            btn.Classes.Remove("Primary");
            btn.Classes.Remove("Secondary");
            btn.Classes.Remove("Danger");
            if (!btn.Classes.Contains("Success")) btn.Classes.Add("Success");
            btn.Content = "EDIT SPEEDS";
            ToolTip.SetTip(btn, $"You have granular edits on {effects} clip(s). Click to reopen the Speed Editor and change them, or to remove them all.");
        }
        else
        {
            btn.Classes.Remove("Danger");
            btn.Classes.Remove("Success");
            if (!btn.Classes.Contains("Primary")) btn.Classes.Add("Primary");
            btn.Content = "GRANULAR SPEED";
            ToolTip.SetTip(btn, "Open the Granular Speed Editor on the whole merge: speed ramps, freeze frames, zoom, delete parts and memes. Effects stay inside the clip they are placed on.");
        }
    }

    /// <summary>
    /// MUSICMAP_01 (P7.2, D8) — where a music start/end placed on the merged clock lands in the
    /// exported body: through the composite timeline, so speed ramps, freezes, memes and cuts before
    /// that moment move it exactly as they move the video. Falls back to merged / base speed (the old
    /// rule, identical when there are no effects) if the edit list does not describe the queue.
    /// </summary>
    private double MusicExportSec(double mergedSec, double speedFactor)
        => EdlDescribesQueue()
            ? CompositeTimeline.Build(CurrentEdl).MergedSecToBodyOutputSec(mergedSec)
            : mergedSec / speedFactor;

    /// <summary>True when the captured edit list is the queue as analysed (same clips, same order).</summary>
    private bool EdlDescribesQueue()
    {
        var edl = CurrentEdl;
        return TimelineMatchesQueue() && edl.Clips.Count == VideoQueue.Count
               && edl.Clips.Select(c => c.Path).SequenceEqual(VideoQueue, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// MERGESIZE_01 — length of the finished video from the edit list (effects, removed intros, custom
    /// thumbnail), or null while it does not describe the queue. Feeds TOTAL LENGTH and the size estimate.
    /// </summary>
    private double? EdlOutputSec() => EdlDescribesQueue() ? CompositeTimeline.Build(CurrentEdl).TotalOutputSec : null;

    private async Task OpenMergeGranularEditorAsync()
    {
        if (_granularOpen) return;
        if (_activeMergerWorker != null)
        {
            Controls.FloatingNotice.Show(this, "The merge is running. Granular edit is available when it finishes.", Controls.NoticeKind.Info);
            return;
        }
        _granularOpen = true;
        UpdateMergerGranularButton();
        try
        {
            await EnsureTimelineReadyAsync();
            if (!TimelineMatchesQueue())
            {
                Controls.FloatingNotice.Show(this, "The clips are still being analysed. Try again in a moment.", Controls.NoticeKind.Info);
                return;
            }

            CaptureEdlNow(flush: false);
            var edl = CurrentEdl;
            var source = MergeEditorSource.Build(CompositeTimeline.Build(edl));
            if (source.Clips.Count == 0) return;
            var state = source.ToEditor(edl);

            _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");

            var first = source.Clips[0];
            string res = "1920x1080";
            bool portrait = false;
            if (MergeClipAnalyzer.TryGetCompleted(first.Path, out var info) && info.Width > 0 && info.Height > 0)
            {
                res = $"{info.Width}x{info.Height}";
                portrait = info.Height > info.Width;
            }

            var memes = await Services.MemeManagementService.ScanMemesAsync();
            RuntimeLog.Info("MERGER", $"Granular edit opened on the merge: {source.Clips.Count} clip(s), {source.TotalMs / 1000.0:F2}s, "
                + $"{state.Segments.Count} segment(s), {state.Cuts.Count} cut(s), {state.Memes.Count} meme(s), extra freezes kept {source.ExtraFreezes}.");

            var editor = await GranularSpeedEditorWindow.CreateAsync(
                source.MpvUrl, 0, source.TotalMs, state.Segments, state.BaseSpeed,
                state.FreezeTimeMs, state.FreezeDurationS, portrait, res, null, state.Cuts, state.Memes);
            editor.AvailableMemes = memes;
            editor.MergeSource = source;
            await editor.ShowDialog(this);

            if (!editor.Accepted)
            {
                RuntimeLog.Info("MERGER", "Granular edit cancelled; the merge is unchanged.");
                return;
            }

            var result = new MergeEditorState(
                editor.ResultSegments.ToList(), editor.ResultFreezeTimeMs, editor.ResultFreezeDurationS,
                editor.ResultCuts.ToList(), editor.ResultMemes.ToList(), editor.ResultBaseSpeed);
            var next = source.FromEditor(result, CurrentEdl);

            _lastEdl = next;   // effects travel forward by ClipId into the capture below
            if (Math.Abs(_baseSpeed - next.BaseSpeed) > 0.001)
            {
                _restoringSession = true;   // the wheel moving is part of THIS step, not its own
                try { ApplySpeedPreset(next.BaseSpeed); }
                finally { _restoringSession = false; }
            }
            _nextHistoryLabel = "granular edit";
            CaptureEdlNow(flush: false);
            MergerAutosave.Value.Schedule(CurrentEdl);

            int effects = CurrentEdl.Clips.Count(c => !c.Effects.IsEmpty);
            RuntimeLog.Info("MERGER", $"Granular edit applied: effects on {effects} clip(s), base speed {CurrentEdl.BaseSpeed:0.##}x.");
            Controls.FloatingNotice.Show(this, effects > 0 ? $"Granular edits applied to {effects} clip(s)." : "Granular edits cleared.", Controls.NoticeKind.Info);
            InvalidateMergedTimelineDrawing();
            UpdateEstimatedSize();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MERGER", $"Granular edit failed: {ex.Message}");
            Controls.FloatingNotice.Show(this, "The Granular Speed Editor could not open on this merge.", Controls.NoticeKind.Error);
        }
        finally
        {
            _granularOpen = false;
            UpdateMergerGranularButton();
        }
    }
}
