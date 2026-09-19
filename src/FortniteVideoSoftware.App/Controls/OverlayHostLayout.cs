// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using Avalonia.Controls;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// OVERLAYHOST_01 — how a full-window overlay finds the panel it must live in, and how it makes
/// itself cover that panel completely.
///
/// WHY THIS IS SHARED. <c>CoachOverlay</c> and <c>FloatingNotice</c> each held a private,
/// byte-identical copy of BOTH methods below. <c>FloatingNotice</c>'s copy even carried the
/// comment "Mirrors CoachOverlay.ResolveHostPanel" — a duplication its own author documented
/// rather than removed.
///
/// ⚠️ WHAT THE DUPLICATION WAS PROTECTING AGAINST, AND WHY IT MATTERS THAT IT IS ONE COPY NOW.
/// <c>CropToolWindow</c>'s root <c>Content</c> is a <c>Border</c>, not a <c>Panel</c>. The naive
/// <c>window.Content as Panel</c> returns null there, and the feature silently disables itself
/// across an entire application window — no crash, no log, just a walkthrough or a notice that
/// never appears. That is a bug you only find by using the app. Discovering it twice, in two
/// copies, is exactly the cost this consolidation removes: the next window whose root is some
/// other wrapper needs one fix, not two.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
internal static class OverlayHostLayout
{
    /// <summary>
    /// OVERLAYHOST_01 — the panel an overlay should attach to, unwrapping the common root
    /// wrappers. Returns null when the window's content is not something an overlay can sit in;
    /// callers must treat that as "suppress the feature", never as an error.
    ///
    /// ⚠️ DO NOT REPLACE WITH <c>window.Content as Panel</c>. CropToolWindow's root Content is a
    /// Border, and that cast silently disabled the whole feature on that window.
    /// </summary>
    internal static Panel? ResolveHostPanel(Window window)
    {
        if (window.Content is Panel direct) return direct;
        if (window.Content is Decorator dec && dec.Child is Panel decChild) return decChild;
        if (window.Content is ContentControl cc && cc.Content is Panel ccChild) return ccChild;
        return null;
    }

    /// <summary>
    /// OVERLAYHOST_01 — makes <paramref name="child"/> span the whole of <paramref name="host"/>.
    /// Only a Grid needs the explicit spans; every other Panel already stretches its children, so
    /// the no-op for non-Grid hosts is deliberate, not an omission.
    /// </summary>
    internal static void CoverWholeHost(Panel host, Control child)
    {
        if (host is Grid g)
        {
            Grid.SetRow(child, 0);
            Grid.SetColumn(child, 0);
            Grid.SetRowSpan(child, Math.Max(1, g.RowDefinitions.Count));
            Grid.SetColumnSpan(child, Math.Max(1, g.ColumnDefinitions.Count));
        }
    }
}
