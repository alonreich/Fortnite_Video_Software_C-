using Avalonia.Controls;
using Avalonia.Input;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>
/// Makes the timeline scrubber knob react to the user, on every timeline in the suite.
///
/// WHY THIS EXISTS — the knob could not do it on its own.
/// Every timeline in this app lays the seek Canvas (TimelineMarkersCanvas and friends) in the SAME
/// grid row as the Slider, declared AFTER it, with Background="Transparent" and
/// IsHitTestVisible="True". The canvas therefore covers the slider completely and takes every
/// pointer event. Two consequences that looked like styling bugs but are pure hit-testing:
///
///   * The Thumb never receives :pointerover or :pressed, so `Thumb:pointerover` /
///     `Thumb:pressed` styles can never fire. The knob stayed flat while dragging.
///   * `Slider:pointerover` fires ONLY in the thin band where the 44px slider peeks out beyond the
///     32px canvas. Moving the mouse along the timeline crossed that band repeatedly, so the knob
///     grew and shrank over and over — the reported "stutter".
///
/// The canvas is the thing the user actually touches, so the canvas is what reports the state.
/// This flips two classes on the Slider, which the styles in AvaloniaApp.axaml key off:
///   Slider.knobhover -> the pointer is anywhere over the timeline
///   Slider.knobdrag  -> the pointer is held down on the timeline
///
/// Do not "simplify" these back to Thumb or Slider pseudo-classes — see above for why neither
/// works while the seek canvas is on top.
/// </summary>
internal static class TimelineKnob
{
    internal const string HoverClass = "knobhover";
    internal const string DragClass = "knobdrag";

    /// <summary>
    /// Wires one timeline. Safe to call with nulls (a window that has not built its controls yet
    /// simply gets no knob feedback rather than throwing), and safe to call more than once —
    /// duplicate handlers only re-set the same class to the same value.
    /// </summary>
    public static void Attach(InputElement? seekSurface, Slider? slider)
    {
        if (seekSurface == null || slider == null) return;

        seekSurface.PointerEntered += (_, _) => SetClass(slider, HoverClass, true);
        seekSurface.PointerExited += (_, _) => SetClass(slider, HoverClass, false);

        seekSurface.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(seekSurface).Properties.IsLeftButtonPressed)
                SetClass(slider, DragClass, true);
        };

        seekSurface.PointerReleased += (_, _) => SetClass(slider, DragClass, false);

        seekSurface.PointerCaptureLost += (_, _) => SetClass(slider, DragClass, false);
    }

    private static void SetClass(Slider slider, string className, bool on)
    {
        if (on)
        {
            if (!slider.Classes.Contains(className)) slider.Classes.Add(className);
        }
        else
        {
            slider.Classes.Remove(className);
        }
    }
}
