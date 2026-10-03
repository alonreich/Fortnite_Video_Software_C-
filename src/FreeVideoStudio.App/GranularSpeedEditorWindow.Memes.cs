// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Forbidden to modify without reading: docs/07_UNDO_AND_HISTORY.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

/// <summary>
/// MEMEMODE_01 — adding a meme, changing how a placed meme plays, and the corner overlays' own band
/// and preview. In its own file because MVVM_02 says so (see GranularSpeedEditorWindow.History.cs).
///
/// <para><b>Two modes, two frames of reference.</b> A FULL SCREEN meme is an insertion: it occupies
/// its whole length on the OUTPUT ruler and obeys D7/D8 (snapped off speed blocks and freezes, never
/// two at one point, kept apart by <see cref="MemeMinSeparationOutSec"/>). A CORNER OVERLAY occupies
/// ZERO output seconds: it is drawn as a thin strip over the gameplay it covers, sits exactly where the
/// playhead was (only pushed out of deleted footage), may overlap anything, and never moves anything
/// after it.</para>
///
/// <para><b>Undo.</b> Every change goes through <c>PushUndo</c> before the list changes; the snapshot
/// holds the <see cref="MemePlacement"/> records, whose equality includes mode, corner, size and
/// sound, so each property change is one undoable step (07 U1–U4).</para>
/// </summary>
public partial class GranularSpeedEditorWindow
{
    private Infrastructure.CornerMemeOverlayPresenter? _cornerMemes;
    private string _cornerMemeKey = "";
    private readonly Services.EditorTimelineCache _gameplayTimelineCache = new();

    /// <summary>The finished video WITHOUT full-screen memes — the clock corner overlays run on.</summary>
    private OutputTimeline GameplayTimeline()
        => _gameplayTimelineCache.Get(Math.Max(0.001, GetDuration()) * 1000, _segments, _cuts, Array.Empty<MemePlacement>(),
            freezeStartMs: _freezeTimeMs >= 0 ? _freezeTimeMs - _trimStartMs : -1,
            freezeDurationSeconds: _freezeDurationS);

    private async void OnAddMemeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try { await AddMemeAsync(); }
        catch (Exception ex) { RuntimeLog.Fail("MEME", $"Adding a meme failed: {ex.Message}"); }
    }

    private async Task AddMemeAsync()
    {
        if (string.IsNullOrWhiteSpace(_videoPath)) return;

        // MEMEPICK_01/02 + MEMEMODE_01 — one modal screen: choose the meme, then FULL SCREEN or
        // CORNER OVERLAY (corner, size, sound).
        var pick = await Controls.MemePickerWindow.PickAsync(this, AvailableMemes, _isMobileFormat,
            latest => AvailableMemes = latest);
        if (pick == null) return;
        var picked = pick.Item;

        if (!System.IO.File.Exists(picked.FullPath))
        {
            NotifyError("That meme file is no longer on disk.");
            return;
        }

        double rawSourceRelSec = Math.Max(0, _playheadMs / 1000.0);
        var tl = OutTimeline();
        bool corner = pick.Mode == MemePresentationMode.CornerOverlay;

        double at;
        if (corner)
        {
            // A corner overlay may sit anywhere the gameplay survives.
            at = tl.IsCutAtSource(rawSourceRelSec) ? tl.NextSurvivingSource(rawSourceRelSec) : rawSourceRelSec;
        }
        else
        {
            // D8 / D7 / separation — exactly the rules a full-screen cutaway always had.
            at = tl.SnapInsertionPoint(rawSourceRelSec);
            if (tl.HasInsertionAtSource(at))
            {
                NotifyError("A full-screen meme already sits at that exact point. Move the playhead a little.");
                return;
            }
            if (!MemeSeparationIsSafe(at, null, out string? clash))
            {
                NotifyError(clash!);
                return;
            }
        }

        double duration = pick.DurationSec is double d && d > 0.01 ? d : await ResolveMemeDurationAsync(picked);
        if (duration <= 0.01)
        {
            NotifyError("That meme's length could not be read, so it was not added.");
            return;
        }

        PushUndo("add meme");   // UNDO_02

        // ⚠️ Ids become FFmpeg filter labels: unique for the life of this editor (MEME_05).
        string id = MemePlacement.NewId(_nextMemeIdIndex++);
        while (_memes.Any(m => m.Id == id))
            id = MemePlacement.NewId(_nextMemeIdIndex++);
        _memes.Add(new MemePlacement(picked.FullPath, at, duration, id, pick.Mode, pick.Corner, pick.Size, pick.PlaySound));
        _selectedMemeId = id;

        bool moved = Math.Abs(at - rawSourceRelSec) > 0.01;
        RuntimeLog.Info("MEME",
            $"Added '{System.IO.Path.GetFileName(picked.FullPath)}' as {(corner ? $"a {pick.Size} corner overlay ({pick.Corner}, sound {(pick.PlaySound ? "on" : "off")})" : "a full-screen cutaway")} " +
            $"at {at:0.###}s source-relative ({duration:0.###}s long){(moved ? $"; slid {at - rawSourceRelSec:0.###}s forward" : "")}.");

        InvalidateMemeTimelines();
        _gameplayTimelineCache.Clear();
        RedrawTimeline();
        UpdateMemeButtonsState();
        if (!corner) ShowMemeLandingFrame(id);                                       // MEME_08
        await RefreshMemePreviewAsync("Fitting the meme into your video...");        // MEME_07

        Notify(corner
            ? "Corner meme added — the video length stays the same"
            : moved
                ? $"Meme added — it slid {at - rawSourceRelSec:0.0}s along, off a block it cannot interrupt."
                : $"Meme added — the video is {duration:0.0}s longer");
    }

    /// <summary>
    /// MEMEMODE_01 — reopens the same popup for a placed meme (double-click its band): another file,
    /// full screen ↔ corner, corner, size, sound. Full screen → corner removes the meme's length from
    /// the video; corner → full screen adds it back (and re-applies D7/D8 at the anchor). One undo step.
    /// </summary>
    private async Task EditMemeAsync(string memeId)
    {
        try
        {
            int idx = _memes.FindIndex(m => m.Id == memeId);
            if (idx < 0) return;
            var old = _memes[idx];

            var pick = await Controls.MemePickerWindow.PickAsync(this, AvailableMemes, _isMobileFormat,
                latest => AvailableMemes = latest, existing: old);
            if (pick == null) return;
            idx = _memes.FindIndex(m => m.Id == memeId);
            if (idx < 0) return;

            double duration = old.DurationSec;
            if (!string.Equals(pick.Item.FullPath, old.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                duration = pick.DurationSec is double d && d > 0.01 ? d : await ResolveMemeDurationAsync(pick.Item);
                if (duration <= 0.01) { NotifyError("That meme's length could not be read, so nothing changed."); return; }
            }

            bool toInline = pick.Mode == MemePresentationMode.InlineFullScreen;
            double at = old.AtSourceSecRelative;
            if (toInline)
            {
                // Re-apply D7/D8 against every OTHER full-screen meme.
                var others = _memes.Where(m => m.Id != memeId).ToList();
                var tlOthers = OutputTimeline.Create(Math.Max(0.001, GetDuration()) * 1000,
                    _segments.Concat(_freezeTimeMs >= 0 && _freezeDurationS > 0
                        ? new[] { new SpeedSegment(_freezeTimeMs - _trimStartMs, _freezeTimeMs - _trimStartMs + _freezeDurationS * 1000, 0) }
                        : Array.Empty<SpeedSegment>()).ToList(),
                    1.0, 0, MemePlacement.ToInsertions(others), CutRange.ToClipRelative(_cuts, 0));
                at = tlOthers.SnapInsertionPoint(at);
                if (tlOthers.HasInsertionAtSource(at))
                {
                    NotifyError("Another full-screen meme already sits at that point, so this one cannot go full screen there.");
                    return;
                }
                if (!MemeSeparationIsSafe(at, memeId, out string? clash))
                {
                    NotifyError(clash!);
                    return;
                }
            }

            var updated = new MemePlacement(pick.Item.FullPath, at, duration, old.Id, pick.Mode, pick.Corner, pick.Size, pick.PlaySound);
            if (updated == old) return;   // U4 — nothing changed, nothing recorded

            PushUndo("change meme");   // UNDO_02 — one step for every property that changed
            _memes[idx] = updated;
            _selectedMemeId = memeId;

            double delta = updated.OutputDurationSec - old.OutputDurationSec;
            RuntimeLog.Info("MEME",
                $"Changed '{System.IO.Path.GetFileName(old.FilePath)}': {old.Mode}/{old.Corner}/{old.Size}/sound {old.PlaySound} -> " +
                $"{updated.Mode}/{updated.Corner}/{updated.Size}/sound {updated.PlaySound}; video length {delta:+0.000;-0.000;0}s.");

            InvalidateMemeTimelines();
            _gameplayTimelineCache.Clear();
            RedrawTimeline();
            UpdateMemeButtonsState();
            await RefreshMemePreviewAsync("Re-timing your video...");

            Notify(Math.Abs(delta) < 0.001 ? "Meme updated"
                : delta < 0 ? $"Meme now plays in a corner — the video is {-delta:0.0}s shorter"
                : $"Meme now plays full screen — the video is {delta:0.0}s longer");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MEME", $"Changing a meme failed: {ex.Message}");
        }
    }

    /// <summary>
    /// MEMEMODE_01 — corner overlays on the OUTPUT ruler: a thin strip in the top quarter of the lane
    /// over the gameplay it covers (it adds no length, so it is never a band that pushes things).
    /// Click selects it (REMOVE MEME deletes it); double-click changes it.
    /// </summary>
    private void DrawCornerMemeBands(Canvas canvas, double w, double h)
    {
        var corners = MemePlacement.CornerOnly(_memes);
        if (corners.Count == 0 || w <= 0) return;
        double outDur = OutDurationSec();
        if (outDur <= 0.0001) return;

        var tl = OutTimeline();
        var colour = Infrastructure.ThemeResources.Colour(this, "AppMemeColor", Avalonia.Media.Color.FromRgb(124, 58, 237));
        var fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(150, colour.R, colour.G, colour.B));
        var fillSelected = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(230, colour.R, colour.G, colour.B));
        var outline = new Avalonia.Media.SolidColorBrush(colour);
        double stripH = Math.Max(8, h * 0.28);

        foreach (var m in corners)
        {
            double start = tl.SourceToOutput(m.AtSourceSecRelative);
            if (MemePlacement.VisibleInterval(start, m.DurationSec, outDur) is not { } v) continue;
            double x1 = Math.Clamp(v.StartSec / outDur * w, 0, w);
            double x2 = Math.Clamp(v.EndSec / outDur * w, 0, w);
            bool selected = _selectedMemeId == m.Id;
            double stripW = Math.Max(MemeGrabMinWidthPx, x2 - x1);

            var strip = new Avalonia.Controls.Shapes.Rectangle
            {
                Fill = selected ? fillSelected : fill,
                Stroke = outline,
                StrokeThickness = selected ? 2 : 1,
                StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 3, 2 },
                Width = stripW,
                Height = stripH,
                RadiusX = 3,
                RadiusY = 3,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            ToolTip.SetTip(strip,
                $"Corner meme: {System.IO.Path.GetFileName(m.FilePath)} ({m.DurationSec:0.0}s, {m.Corner}, {m.Size}, sound {(m.PlaySound ? "on" : "off")})\n" +
                "Plays over the gameplay — the video length stays the same.\nClick to select, double-click to change it.");
            Canvas.SetLeft(strip, Math.Min(x1, Math.Max(0, w - stripW)));
            Canvas.SetTop(strip, 2);
            string id = m.Id;
            strip.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(strip).Properties.IsLeftButtonPressed) return;
                _selectedMemeId = id;
                _selectedSegmentIndex = -1;
                _isFreezeCameraSelected = false;
                UpdateDeleteButtonVisibility();
                UpdateMemeButtonsState();
                RedrawTimeline();
                e.Handled = true;
            };
            strip.DoubleTapped += (_, e) => { e.Handled = true; _ = EditMemeAsync(id); };
            canvas.Children.Add(strip);
        }
    }

    /// <summary>MEMEMODE_01 — the editor preview's corner overlays, on the gameplay clock.</summary>
    private void UpdateEditorCornerMemes(double playheadRelMs)
    {
        var corners = MemePlacement.CornerOnly(_memes);
        if (corners.Count == 0 || _videoHost == null) { _cornerMemes?.Hide(); return; }

        _cornerMemes ??= new Infrastructure.CornerMemeOverlayPresenter();
        _cornerMemes.Attach(_videoHost);
        if (_isMobileFormat) _cornerMemes.SetFrameSize(CoordinateConstants.PortraitW, CoordinateConstants.PortraitH);
        else
        {
            var (rw, rh) = CoordinateMath.GetResolutionInts(_originalResolution);
            _cornerMemes.SetFrameSize(rw, rh);
        }

        var game = GameplayTimeline();
        double total = game.TotalOutputSeconds;
        var spans = new List<Infrastructure.CornerMemeSpan>(corners.Count);
        var key = new System.Text.StringBuilder();
        foreach (var m in corners)
        {
            if (MemePlacement.VisibleInterval(game.SourceToOutput(m.AtSourceSecRelative), m.DurationSec, total) is not { } v) continue;
            spans.Add(new Infrastructure.CornerMemeSpan(m, v.StartSec, v.EndSec));
            key.Append(m).Append('@').Append(v.StartSec.ToString("F3")).Append(';');
        }
        string k = key.ToString();
        if (k != _cornerMemeKey) { _cornerMemeKey = k; _cornerMemes.SetSpans(spans); }

        _cornerMemes.Update(game.SourceToOutput(playheadRelMs / 1000.0));
    }
}
