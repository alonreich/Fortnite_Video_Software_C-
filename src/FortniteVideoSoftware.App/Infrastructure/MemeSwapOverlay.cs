// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using Avalonia.Controls;

namespace FortniteVideoSoftware.App.Infrastructure;

/// <summary>
/// MEMESWAP_01 — the "swapping meme…" busy veil, shown by three different windows.
///
/// <c>MainWindow</c>, <c>GranularSpeedEditorWindow</c> and <c>VoiceOverWindow</c> each carried a
/// byte-identical private copy of this, down to the two control names. Three copies of a
/// five-line method is three places to forget when the overlay gains a spinner, a cancel button
/// or an accessibility announcement.
///
/// ⚠️ BOTH LOOKUPS STAY NULL-TOLERANT. Not every window that calls this has both controls in its
/// XAML at every point in its lifetime, and a missing veil must never throw into a caller that is
/// mid-swap. Body moved verbatim.
/// </summary>
internal static class MemeSwapOverlay
{
    internal static void Set(Window window, bool visible, string message)
    {
        var overlay = window.FindControl<Border>("MemeSwapOverlay");
        var text = window.FindControl<TextBlock>("MemeSwapOverlayText");
        if (text != null && !string.IsNullOrEmpty(message)) text.Text = message;
        if (overlay != null) overlay.IsVisible = visible;
    }
}
