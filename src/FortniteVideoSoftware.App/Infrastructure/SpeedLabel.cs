// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md, docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using Avalonia.Controls;

namespace FortniteVideoSoftware.App.Infrastructure;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// SPEEDLABEL_01 — the one speed → (description, colour) ladder, and the label that renders it.
///
/// WHY THIS IS SHARED. <c>MainWindow.UpdateSpeedLabel</c> and
/// <c>VideoMergerWindow.UpdateSpeedLabel</c> were BYTE-IDENTICAL copies — same eight thresholds,
/// same eight descriptions, same eight hex colours, even the same <c>MainSpeedLabel</c> control
/// name. Two copies of the same ladder in two windows the user switches between.
///
/// ⚠️ THE LADDER IS A PRODUCT DECISION, NOT A FORMATTING DETAIL. "1.2x is a Slight Boost and it is
/// yellow" is a statement the app makes to the user about their edit. If the two copies drift —
/// and duplicated ladders drift, that is what they do — the SAME 1.2x clip is described one way in
/// the editor and another way in the merger, and the user is right to conclude the app does not
/// know what it is doing. One copy makes that impossible.
///
/// ⚠️ THRESHOLDS ARE BOUNDARY-INCLUSIVE AND ASYMMETRIC ON PURPOSE. Note that "Normal" uses
/// <c>&lt; 1.05</c> while every other rung uses <c>&lt;=</c>: the app's default speed is 1.1
/// (SpeedPresetButtons.NativeDefaultSpeed), so a strict bound is what keeps the default OUT of
/// "Normal" and in "Slight Boost". Do not tidy that into a uniform comparison.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
internal static class SpeedLabel
{
    /// <summary>
    /// SPEEDLABEL_01 — classifies a playback speed. Pure: no UI, no window, unit-testable.
    /// </summary>
    internal static (string Description, string Color) Describe(double speed)
    {
        string desc;
        string color;

        if (speed <= 0.5) { desc = "Slow Motion"; color = "#3498db"; }
        else if (speed <= 0.8) { desc = "Cinematic"; color = "#3498db"; }
        else if (speed < 1.05) { desc = "Normal"; color = "White"; }
        else if (speed <= 1.2) { desc = "Slight Boost"; color = "#f1c40f"; }
        else if (speed <= 1.5) { desc = "Fast"; color = "#f39c12"; }
        else if (speed <= 2.0) { desc = "Very Fast"; color = "#e67e22"; }
        else if (speed <= 3.0) { desc = "Turbo"; color = "#e74c3c"; }
        else { desc = "Extreme"; color = "#e74c3c"; }

        return (desc, color);
    }

    /// <summary>
    /// SPEEDLABEL_01 — writes the classification into the window's <c>MainSpeedLabel</c>.
    ///
    /// ⚠️ THE DISPATCHER POST IS PRESERVED FROM BOTH ORIGINALS. Callers reach this from slider
    /// callbacks and from restore paths that are not guaranteed to be on the UI thread, and a
    /// missing label is a silent no-op rather than a throw — both windows relied on that.
    /// </summary>
    internal static void Apply(Window window, double speed)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            var label = window.FindControl<TextBlock>("MainSpeedLabel");
            if (label == null) return;

            var (desc, color) = Describe(speed);

            label.Text = $"{speed:F1}x — {desc}";
            label.Foreground = Avalonia.Media.Brush.Parse(color);
        });
    }
}
