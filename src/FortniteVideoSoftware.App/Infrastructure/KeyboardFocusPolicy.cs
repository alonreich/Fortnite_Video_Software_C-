using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace FortniteVideoSoftware.App.Infrastructure;

/// <summary>
/// KEYFOCUS_01 — keyboard focus-isolation policy shared by every hotkey surface.
///
/// Timeline/transport hotkeys (Space, arrows, marks, seeks) are window-level conveniences.
/// The moment a text-entry control owns focus, the keyboard belongs to it: hotkeys are
/// suspended so typing "Boss HP" into the Element Name box produces "Boss HP" — the video
/// does not pause on the space and the caret moves on the arrows instead of the playhead.
/// </summary>
public static class KeyboardFocusPolicy
{
    /// <summary>
    /// Root focus element for the given top level, null-safe. Hotkey handlers must intercept
    /// this — not the routed-event source — to decide whether they are allowed to run.
    /// </summary>
    public static IInputElement? GetFocusedElement(TopLevel? topLevel)
        => topLevel?.FocusManager?.GetFocusedElement();

    /// <summary>
    /// True when the focused element is a control whose primary interaction is typing or
    /// item selection (TextBox, NumericUpDown, ComboBox). While one of these holds focus,
    /// hotkey handlers must return immediately without setting <c>e.Handled</c>.
    /// </summary>
    public static bool IsTextInputFocused(IInputElement? focused)
        => focused is TextBox or NumericUpDown or ComboBox;

    /// <summary>True while hotkeys must be suspended for the current focus of this top level.</summary>
    public static bool HotkeysSuspended(TopLevel? topLevel)
        => IsTextInputFocused(GetFocusedElement(topLevel));
}
