// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// ANTS_01 — ONE SELECTION, ONE ORDER, TWO VIEWS (Video-Merger-Migration.md P5.3, D14).
///
/// The right-hand list and the merged timeline show the SAME queue. Selection lives in the list;
/// the timeline draws the selected clip(s) with the list's yellow marching ants. A reorder from
/// either view is a <c>VideoQueue.Move</c>, so the other view, the clip ids, the autosave and undo
/// all follow.
///
/// MERGERUX_01 — P10 (user decisions D22–D24, 2026-09-27) — ONE JOB PER AREA:
///   • the UPPER timeline (time scale + scrub row) only SEEKS; its clip labels are not handles;
///   • the thumbnail BLOCKS only select and move clips: hover = glow + open hand, press = closed
///     hand, a plain click SELECTS WITHOUT SEEKING, press + 6 px = drag (the block follows the
///     pointer, the gold bar shows where it lands). After a move the playhead stays on the same
///     frame of the same clip (D22, EnsureEdlLoaded remaps it).
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class VideoMergerWindow
{
    private const double ChipDragThresholdPx = 6;

    private bool _draggingClipChip;
    private bool _chipPressed;
    private bool _selectWithoutPreview;   // D23 — a selection that must not seek the preview
    private double _chipBlockLeft;
    private int _chipFrom = -1;
    private double _chipPressX;
    private Rectangle? _chipInsertBar;

    private void InitializeTimelineSelection()
    {
        var list = VideoListCtl;
        if (list != null) list.SelectionChanged += (s, e) => RedrawTimelineSelection();
    }

    private string SelectionKey() => string.Join(",", SelectedQueueIndices());

    private List<int> SelectedQueueIndices()
    {
        var list = VideoListCtl;
        var result = new List<int>();
        if (list?.Selection is { } sel)
            foreach (int i in sel.SelectedIndexes) if (i >= 0) result.Add(i);
        result.Sort();
        return result;
    }

    /// <summary>A press or drag on a queue row or a block is in progress: nothing may move the selection or rebuild the blocks under it.</summary>
    private bool QueueGestureActive => _chipPressed || _draggingClipChip || _videoDragStartPoint.HasValue || _isVideoDragging;

    /// <summary>
    /// D23 / MERGERPLAYHEAD_01 — the upper timeline (time scale + scrub row) AND the waveform lane seek; only the
    /// thumbnail blocks between them do not (they select and reorder clips, and handle their own presses first).
    /// </summary>
    private bool IsOnSeekRows(PointerEventArgs e, Canvas seekRow)
    {
        double y = e.GetPosition(seekRow).Y;
        if (y >= -24 && y <= seekRow.Bounds.Height + 2) return true;
        var wave = WaveLaneCtl;
        if (wave is not { IsVisible: true }) return false;
        double wy = e.GetPosition(wave).Y;
        return wy >= -1 && wy <= wave.Bounds.Height + 4;
    }

    private void RedrawTimelineSelection()
    {
        if (_draggingClipChip || _chipPressed) return;
        InvalidateMergedTimelineDrawing();
        var markers = MarkersCanvasCtl;
        if (markers != null && TimelineMatchesQueue()) DrawMergedTimeline(markers, null);
    }

    private void DrawTimelineSelection(Canvas markers, double w, double h, double total)
    {
        if (total <= 0) return;
        IBrush ants = Infrastructure.ThemeResources.Brush(markers, "AppWarningBrush", Brushes.Gold);
        foreach (int i in SelectedQueueIndices())
        {
            if (i >= _timeline.Clips.Count) continue;
            var c = _timeline.Clips[i];
            double x0 = c.MergedStartSec / total * w;
            double x1 = c.MergedEndSec / total * w;
            var rect = new Rectangle
            {
                Width = Math.Max(3, x1 - x0),
                Height = h,
                Stroke = ants,
                StrokeThickness = AntsThickness,
                StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 2 },
                RadiusX = 3,
                RadiusY = 3,
                IsHitTestVisible = false,
            };
            rect.Classes.Add("TimelineAnts");
            Canvas.SetLeft(rect, x0);
            Canvas.SetTop(rect, 0);
            markers.Children.Add(rect);
        }
        DrawClipBlocks(w, total);   // D20 — the same clips as blocks over the filmstrip
    }

    /// <summary>
    /// D20 — the filmstrip lane as the clip strip: one outlined block per clip over its thumbnails.
    /// Click selects, drag reorders, right-click opens the clip menu. Selected blocks get the ants.
    /// </summary>
    private void DrawClipBlocks(double w, double total)
    {
        var lane = BlocksLaneCtl;
        if (lane == null) return;
        lane.Children.Clear();
        bool show = FilmLaneCtl?.IsVisible == true && total > 0 && w > 0;
        lane.IsVisible = show;
        if (!show) return;

        double h = lane.Height > 0 ? lane.Height : 34;
        var selected = SelectedQueueIndices();
        IBrush ants = Infrastructure.ThemeResources.Brush(lane, "AppWarningBrush", Brushes.Gold);
        IBrush outline = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));
        IBrush hoverOutline = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));
        var glow = new BoxShadows(new BoxShadow { Blur = 10, Spread = 1, Color = Color.FromArgb(150, 255, 255, 255) });
        for (int i = 0; i < _timeline.Clips.Count; i++)
        {
            var c = _timeline.Clips[i];
            double x0 = c.MergedStartSec / total * w;
            double x1 = c.MergedEndSec / total * w;
            if (x1 - x0 < 3) continue;
            var block = new Border
            {
                Width = x1 - x0 - 1,
                Height = h,
                Background = Brushes.Transparent,
                BorderBrush = outline,
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(3),
            };
            Canvas.SetLeft(block, x0);
            Canvas.SetTop(block, 0);
            block.PointerEntered += (_, _) => { if (!_draggingClipChip) { block.BoxShadow = glow; block.BorderBrush = hoverOutline; } };
            block.PointerExited += (_, _) => { if (!_draggingClipChip) { block.BoxShadow = default; block.BorderBrush = outline; } };
            AttachClipChip(block, i, lane);
            ToolTip.SetTip(block, $"Clip {i + 1}: {System.IO.Path.GetFileName(c.Path)}\nClick to select · drag to move · right-click for more · Delete removes it");
            lane.Children.Add(block);

            if (selected.Contains(i))
            {
                var rect = new Rectangle
                {
                    Width = Math.Max(3, x1 - x0 - 1),
                    Height = h,
                    Stroke = ants,
                    StrokeThickness = AntsThickness,
                    StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 2 },
                    RadiusX = 3,
                    RadiusY = 3,
                    IsHitTestVisible = false,
                };
                rect.Classes.Add("TimelineAnts");
                Canvas.SetLeft(rect, x0);
                Canvas.SetTop(rect, 0);
                lane.Children.Add(rect);
            }
        }
    }

    private List<(double X0, double X1)> TimelineBlocks(double w)
    {
        double total = _timeline.TotalSec;
        var blocks = new List<(double, double)>(_timeline.Clips.Count);
        foreach (var c in _timeline.Clips)
            blocks.Add(total > 0 ? (c.MergedStartSec / total * w, c.MergedEndSec / total * w) : (0, 0));
        return blocks;
    }

    private void AttachClipChip(Border chip, int index, Canvas markers)
    {
        chip.IsHitTestVisible = true;
        chip.Cursor = Infrastructure.GrabCursors.Open;   // GRABCURSOR_01 — open hand: this can be picked up

        chip.PointerPressed += (_, e) =>
        {
            var props = e.GetCurrentPoint(chip).Properties;
            if (props.IsRightButtonPressed)
            {
                // D20 — right-click: this clip becomes the selection, then its menu opens.
                if (!SelectedQueueIndices().Contains(index)) SelectQueueRow(index, preview: false);
                ShowClipContextMenu(chip);
                e.Handled = true;
                return;
            }
            if (!props.IsLeftButtonPressed) return;
            _chipPressed = true;
            _draggingClipChip = false;
            _chipFrom = index;
            _chipPressX = e.GetPosition(markers).X;
            _chipBlockLeft = Canvas.GetLeft(chip);
            chip.Cursor = Infrastructure.GrabCursors.Closed;   // closed hand: holding it
            e.Pointer.Capture(chip);
            e.Handled = true;   // a chip press is not a seek
        };
        chip.PointerMoved += (_, e) =>
        {
            if (!_chipPressed) return;
            // THUMB_02 — no button held means the gesture is over, whatever the flags say.
            if (!e.GetCurrentPoint(chip).Properties.IsLeftButtonPressed) { EndClipChip(markers, commitX: null); return; }
            double x = e.GetPosition(markers).X;
            if (!_draggingClipChip && Math.Abs(x - _chipPressX) < ChipDragThresholdPx) return;
            if (!_draggingClipChip)
            {
                _draggingClipChip = true;
                chip.Opacity = 0.75;
                chip.ZIndex = 50;
            }
            Canvas.SetLeft(chip, _chipBlockLeft + (x - _chipPressX));   // the block follows the pointer
            ShowInsertBar(markers, x);
            e.Handled = true;
        };
        chip.PointerReleased += (_, e) =>
        {
            if (!_chipPressed) return;
            double x = e.GetPosition(markers).X;
            e.Pointer.Capture(null);
            chip.Cursor = Infrastructure.GrabCursors.Open;
            EndClipChip(markers, commitX: x);
            e.Handled = true;
        };
        chip.PointerCaptureLost += (_, _) =>
        {
            chip.Cursor = Infrastructure.GrabCursors.Open;
            if (_chipPressed) EndClipChip(markers, commitX: null);
        };
    }

    private void ShowInsertBar(Canvas markers, double x)
    {
        double w = markers.Bounds.Width;
        var blocks = TimelineBlocks(w);
        int target = TimelineReorder.TargetIndex(blocks, _chipFrom, x);
        double bx = TimelineReorder.InsertionX(blocks, _chipFrom, target);
        if (_chipInsertBar == null)
        {
            _chipInsertBar = new Rectangle
            {
                Width = 3,
                Height = Math.Max(24, markers.Bounds.Height),
                Fill = Infrastructure.ThemeResources.Brush(markers, "AppWarningBrush", Brushes.Gold),
                IsHitTestVisible = false,
            };
            markers.Children.Add(_chipInsertBar);
        }
        Canvas.SetLeft(_chipInsertBar, bx - 1.5);
        Canvas.SetTop(_chipInsertBar, 0);
    }

    /// <summary>Ends a chip gesture. <paramref name="commitX"/> null = abandoned (capture lost): no reorder.</summary>
    private void EndClipChip(Canvas markers, double? commitX)
    {
        bool wasDrag = _draggingClipChip;
        int from = _chipFrom;
        _chipPressed = false;
        _draggingClipChip = false;
        _chipFrom = -1;
        if (_chipInsertBar != null)
        {
            markers.Children.Remove(_chipInsertBar);
            _chipInsertBar = null;
        }
        if (from < 0 || from >= VideoQueue.Count) { RedrawTimelineSelection(); return; }

        if (!wasDrag)
        {
            if (commitX != null) SelectQueueRow(from, preview: false);   // D23 — a click selects, it never seeks
            RedrawTimelineSelection();
            return;
        }
        if (commitX is not double x) { RedrawTimelineSelection(); return; }

        int to = TimelineReorder.TargetIndex(TimelineBlocks(markers.Bounds.Width), from, x);
        if (to != from)
        {
            RuntimeLog.Info("MERGER", $"Timeline drag: clip {from + 1} moved to position {to + 1}.");
            VideoQueue.Move(from, to);
            SelectQueueRow(to, preview: false);   // D22 — the playhead stays on its clip; the moved clip is just highlighted
        }
        RedrawTimelineSelection();
    }

    private void SelectQueueRow(int index, bool preview = true)
    {
        var list = VideoListCtl;
        if (list == null || index < 0 || index >= VideoQueue.Count) return;
        bool before = _selectWithoutPreview;
        _selectWithoutPreview = !preview || before;
        try
        {
            list.SelectedIndex = index;
            list.ScrollIntoView(index);
        }
        finally { _selectWithoutPreview = before; }
    }
}
