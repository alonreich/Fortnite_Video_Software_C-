// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// CLIPACTIONS_01 — THE SAME CLIP ACTIONS FROM THE LIST AND FROM THE TIMELINE (P5.4, D20).
///
/// • Delete key (list or timeline focus, not while typing, not while merging) and "Remove from list"
///   in the clip menu remove the SELECTED rows — by index, so a file queued twice loses only the
///   copy that was selected. Confirmation follows the existing ConfirmVideoMergerRemove setting;
///   Ctrl+Z brings a removed clip back with its id and effects.
/// • Right-click on a timeline chip or clip block opens the same menu the list's flyout offers.
/// • Any queue change rebuilds the merged timeline SYNCHRONOUSLY from the analysis cache when every
///   clip is already analysed — pure arithmetic, no I/O — and repaints timeline and lanes from their
///   caches at once. New clips still go through the background analysis, as before. The UI thread
///   never waits on ffprobe/ffmpeg.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class VideoMergerWindow
{
    private bool _clipActionsHooked;
    private bool _removeDialogOpen;

    private void InitializeClipActions()
    {
        if (_clipActionsHooked) return;
        _clipActionsHooked = true;
        AddHandler(InputElement.KeyDownEvent, ClipActionsKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void ClipActionsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Delete || e.KeyModifiers != KeyModifiers.None) return;
        if (FocusManager?.GetFocusedElement() is TextBox or NumericUpDown) return;
        if (SelectedQueueIndices().Count == 0) return;
        e.Handled = true;
        _ = RequestRemoveSelectedAsync();
    }

    /// <summary>Removes the selected rows, asking first only when the user's setting says so.</summary>
    private async Task RequestRemoveSelectedAsync()
    {
        if (_removeDialogOpen) return;
        if (_activeMergerWorker != null)
        {
            Controls.FloatingNotice.Show(this, "The merge is running. Clips can be removed when it finishes.", Controls.NoticeKind.Info);
            return;
        }
        var rows = SelectedQueueIndices();
        if (rows.Count == 0) return;

        if (Infrastructure.SettingsManager.Instance.ConfirmVideoMergerRemove)
        {
            _removeDialogOpen = true;
            try
            {
                var dlg = new Controls.ConfirmDialogWindow();
                dlg.SetTitle(rows.Count == 1 ? "Remove Clip" : "Remove Clips");
                dlg.SetMessage((rows.Count == 1 ? "Remove the selected clip from the queue?" : $"Remove the {rows.Count} selected clips from the queue?")
                               + "\nThe files on your computer are not touched. Ctrl+Z brings them back.");
                dlg.SetButtonText("YES, REMOVE", "CANCEL");
                dlg.UseDestructiveStyling();   // REMOVEUX_01 — a destructive answer is red and never the Enter key
                await dlg.ShowDialog(this);
                if (!dlg.Result) return;
            }
            finally { _removeDialogOpen = false; }
        }
        RemoveQueueRows(SelectedQueueIndices());
    }

    /// <summary>Removes rows by index (highest first), then selects the row that took the first one's place.</summary>
    private void RemoveQueueRows(IReadOnlyList<int> rows)
    {
        if (rows.Count == 0) return;
        var ordered = rows.Where(i => i >= 0 && i < VideoQueue.Count).Distinct().OrderByDescending(i => i).ToList();
        int first = ordered.Count > 0 ? ordered[^1] : 0;
        foreach (int i in ordered) VideoQueue.RemoveAt(i);
        RuntimeLog.Info("MERGER", $"Removed {ordered.Count} clip(s) from the queue.");
        if (VideoQueue.Count > 0) SelectQueueRow(Math.Min(first, VideoQueue.Count - 1));
        RedrawTimelineSelection();
    }

    /// <summary>The clip menu for the timeline (the list's own flyout offers the same three actions).</summary>
    private void ShowClipContextMenu(Control target)
    {
        var up = new MenuItem { Header = "Move earlier" };
        up.Click += (_, _) => MoveVideo(-1);
        var down = new MenuItem { Header = "Move later" };
        down.Click += (_, _) => MoveVideo(1);
        var remove = new MenuItem { Header = "Remove from list   (Delete)" };
        remove.Classes.Add("Danger");
        remove.Click += (_, _) => _ = RequestRemoveSelectedAsync();
        var menu = new ContextMenu { ItemsSource = new object[] { up, down, new Separator(), remove } };
        menu.Open(target);
    }

    /// <summary>
    /// A queue change with every clip already analysed: rebuild the merged timeline NOW from the
    /// cache (no I/O) and repaint, so the timeline follows a drag in the list without a gap.
    /// </summary>
    private void RebuildTimelineFromCacheNow()
    {
        var queue = VideoQueue.ToList();
        if (queue.Count == 0) return;
        var infos = new List<MergeClipInfo>(queue.Count);
        foreach (var p in queue)
        {
            if (!MergeClipAnalyzer.TryGetCompleted(p, out var info)) return;   // a new clip: the background path handles it
            infos.Add(info);
        }
        if (_thumbPath != null && !queue.Any(p => SameVideoPath(p, _thumbPath))) _thumbPath = null;

        _timelineVersion++;   // supersede the background rebuild queued for this same change
        var sources = infos.Select(i => new MergeClipSource(i.Path, i.DurationSec, i.IntroSec)).ToList();
        ApplyTimeline(MergedTimeline.Build(sources, _scraperEnabled, _thumbPath != null), infos.Select(i => i.IntroSec).ToList());
        RefreshLanes();              // cached tiles paint immediately; the rest keeps building in the background
        RedrawTimelineSelection();   // the timeline blocks and ants follow the new order at once
    }
}
