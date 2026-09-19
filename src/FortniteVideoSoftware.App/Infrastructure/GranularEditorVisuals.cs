// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md, docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;   // ResourceNodeExtensions.TryFindResource
using Avalonia.Media;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App.Infrastructure;

/// <summary>
/// GRANVIS_01 — the granular editor's colour tokens, clock formatting, cut arithmetic, transport
/// dispatch and the zoom-tutorial counter, lifted out of <c>GranularSpeedEditorWindow</c>.
///
/// Measured by identifier reference count, that one class carries Zoom 1016, Segment 667, Drag 576,
/// Meme 502, Undo 207 — twelve concerns over a single 181-field bag. These members touch none of it.
///
/// ⚠️ <c>SurvivingMsAfterCuts</c> IS TIMELINE MATHS, GOVERNED BY
/// <c>docs/01_TIMELINE_COORDINATE_MATH.md</c> (CUT_01). It decides how much footage is left once
/// the cuts are removed, which feeds the export duration and the progress bar. Body moved verbatim;
/// it must keep delegating to the Core primitives rather than re-deriving them.
///
/// ⚠️ The zoom tutorial counter persists through <c>UiStateStore</c>, which is now an atomic writer
/// (ATOMICSTATE_01). Keep it going through that store, never a raw file write.
/// </summary>
internal static class GranularEditorVisuals
{
    /// <summary>
    /// GRANVIS_01 — the UI-state file holding how many times the zoom tutorial has been shown.
    /// Moved with Read/WriteZoomTutorialCount, its only consumers. Written through UiStateStore,
    /// which is an atomic writer (ATOMICSTATE_01) — never a raw File.WriteAllText.
    /// </summary>
    private const string ZoomTutorialCounterFile = "zoom_tutorial.txt";

    /// <summary>
    /// IDEA_6 — the ONE zoom colour, resolved from the `AppZoomColor` design token.
    ///
    /// Zoom visuals are built in code-behind (the dashed box, its four handles, the timeline bar,
    /// the onboarding banner), so they cannot use DynamicResource from XAML. Routing them all
    /// through here keeps them on the SAME token as the XAML ones instead of the three hard-coded
    /// hexes they used before (#fde047 yellow, #1e40af blue, #d946ef fuchsia) — which is exactly
    /// why users could not tell a zoom from a speed block.
    ///
    /// Falls back to the literal token value if resource lookup fails, so a missing theme resource
    /// can never leave a zoom visual invisible.
    /// </summary>
    internal static Avalonia.Media.Color ZoomColor()
    {
        if (Avalonia.Application.Current?.TryFindResource("AppZoomColor", out object? res) == true &&
            res is Avalonia.Media.Color c)
        {
            return c;
        }
        return Avalonia.Media.Color.Parse("#2251c1");
    }

    /// <summary>Fresh brush on the zoom token. Fresh instance per call — Avalonia shapes take ownership.</summary>
    internal static Avalonia.Media.SolidColorBrush ZoomBrush(byte alpha = 255)
    {
        var c = ZoomColor();
        return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(alpha, c.R, c.G, c.B));
    }

    /// <summary>
    /// ZOOMANTS_01 — the yellow the zoom rubber-band's animated 1px marching-ants outline is drawn
    /// in, read from the <c>AppZoomAntsColor</c> token.
    ///
    /// ⚠️ THIS IS THE ONE DELIBERATE EXCEPTION TO IDEA_6, AND IT IS DOCUMENTED AT THE TOKEN.
    /// IDEA_6 (AvaloniaApp.axaml) unified every zoom visual onto AppZoomColor and explicitly
    /// removed yellow #fde047 from the zoom box, because with three colours in play "users could
    /// not tell zoom apart from a speed segment". Yellow is back here on the owner's instruction,
    /// and what is meant to carry the distinction now is MOTION, not hue: nothing else in the
    /// editor crawls. Everything else about zoom — the corner handles, the timeline bar, the
    /// lollipops, the badge — still wears <see cref="ZoomColor"/>, so zoom does not acquire a
    /// second STATIC colour.
    ///
    /// ⚠️ THE COLLISIONS IDEA_6 NAMED ARE STILL REAL: #fde047 sits next to AppWarningBrush
    /// (#facc15) and the selected-block highlight. To undo this completely, point
    /// AppZoomAntsColor at AppZoomColor in AvaloniaApp.axaml — no code change needed.
    ///
    /// Falls back to the literal token value if resource lookup fails, so a missing theme resource
    /// can never leave the rubber-band invisible. Same contract as <see cref="ZoomColor"/>.
    /// </summary>
    internal static Avalonia.Media.SolidColorBrush ZoomAntsBrush(byte alpha = 255)
    {
        Avalonia.Media.Color c;
        if (Avalonia.Application.Current?.TryFindResource("AppZoomAntsColor", out object? res) == true &&
            res is Avalonia.Media.Color found)
        {
            c = found;
        }
        else
        {
            c = Avalonia.Media.Color.Parse("#fde047");
        }

        return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(alpha, c.R, c.G, c.B));
    }

    /// <summary>
    /// FREEZE_VIS — the one blue every part of a freeze is drawn in.
    /// <para>
    /// Identical to what <c>GetSegmentOverlayColor</c> returns for a Speed≈0 segment. It is pulled
    /// out here because the frozen span is now drawn in THREE places — the block on the upper lane,
    /// the wash over the thumbnails, and the edge posts on both — and three hand-typed copies of
    /// the same literal is how a colour quietly drifts apart.
    /// </para>
    /// </summary>
    internal static Avalonia.Media.SolidColorBrush FreezeBrush(byte alpha = 255)
        => new(Avalonia.Media.Color.FromArgb(alpha, 96, 165, 250));

    /// <summary>
    /// SHORT CLOCK — the format used by everything the user READS on screen in this window:
    /// the segment list, both ends of the timeline axis, and the ruler tick labels.
    ///
    /// Rule: <c>MM:SS</c>, escalating to <c>HH:MM:SS</c> only when the video is genuinely an hour
    /// or longer. Never milliseconds. A gameplay clip is seconds long, so "00:00:04.963" spent
    /// most of its width showing two zeros and a decimal nobody can act on, and it forced the
    /// segment rows onto two lines.
    ///
    /// ⚠️ THIS IS NOT A REPLACEMENT FOR <see cref="FormatMs"/>. That one keeps millisecond
    /// precision and is still what status messages and every RuntimeLog line use, because a
    /// millisecond-accurate boundary is exactly what you need when diagnosing a segment/export
    /// mismatch. Do not "unify" them — display and diagnostics want different things.
    /// </summary>
    internal static string FormatClock(double ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms < 0 ? 0 : ms);
        return ts.TotalHours >= 1.0
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    internal static string FormatMs(double ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms < 0 ? 0 : ms);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    internal static int Even(int v) => v % 2 == 0 ? v : v - 1;

    /// <summary>The video's actual letterboxed rect inside the overlay canvas (aspect-fit).</summary>
    /// <summary>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// ⚠️ ZOOM_09 — IS THE VIDEO RECT REAL, OR IS IT THE NOT-LAID-OUT FALLBACK?
    ///
    /// <see cref="GetVideoDisplayRect"/> returns `Rect(0,0,max(1,cw),max(1,ch))` when the canvas
    /// has not been through a layout pass yet. That fallback is a SQUARE 1x1, and a square is a
    /// perfectly valid-looking rectangle — so every coordinate conversion downstream keeps working
    /// and quietly produces garbage.
    ///
    /// THIS IS NOT HYPOTHETICAL. It is what a real session logged:
    ///     Zoom Placed on segment #1 ... W=284 H=240 src=854x480 mobile=True
    /// The auto-placed box should have been 160x240 (a 2:3 rectangle). 284x240 is 1.18:1 — not 2:3
    /// at all. The arithmetic reproduces exactly from a 1x1 vid: the box works out as 1/3 x 1/2 of
    /// a unit square, and committing multiplies those by the source size, giving 854/3 = 284 and
    /// 480/2 = 240. The zoom was written against a video rectangle that did not exist yet.
    ///
    /// ANY code that converts between canvas pixels and source pixels MUST check this first.
    /// The threshold is 4, not 1: a 1- or 2-pixel canvas is layout noise, never a real preview.
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    internal static bool IsVideoRectUsable(Avalonia.Rect vid)
        => vid.Width >= 4 && vid.Height >= 4;

    internal static double SurvivingMsAfterCuts(
        IReadOnlyList<FortniteVideoSoftware.Core.Media.CutRange> cuts, double durMs)
    {
        var rel = cuts
            .Select(c => new FortniteVideoSoftware.Core.Media.OutputTimeline.Cut(c.StartMs / 1000.0, c.EndMs / 1000.0))
            .ToList();
        var norm = FortniteVideoSoftware.Core.Media.OutputTimeline.NormalizeCuts(rel, durMs / 1000.0);

        double removed = 0;
        foreach (var c in norm) removed += c.LengthSec * 1000.0;
        return Math.Max(0, durMs - removed);
    }

    /// <summary>Executes a transport command from the global gesture dispatcher (KeyBinding.TryHandle semantics).</summary>
    internal static void ExecuteTransportCommand(FortniteVideoSoftware.App.ViewModels.RelayCommand? command, Avalonia.Input.KeyEventArgs e)
    {
        if (command == null || !command.CanExecute(null)) return;
        command.Execute(null);
        e.Handled = true;
    }

    internal static int ReadZoomTutorialCount()
        => FortniteVideoSoftware.Core.Infrastructure.UiStateStore.ReadInt(ZoomTutorialCounterFile);

    internal static void WriteZoomTutorialCount(int n)
        => FortniteVideoSoftware.Core.Infrastructure.UiStateStore.WriteInt(ZoomTutorialCounterFile, n);
}
