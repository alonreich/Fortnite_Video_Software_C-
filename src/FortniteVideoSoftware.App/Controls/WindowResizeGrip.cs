// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System;

// GRIP_01 — ALIASED, NOT PLAIN `using Avalonia.Controls.Shapes`.
// The App project has <ImplicitUsings>enable</ImplicitUsings>, which injects a global
// `using System.IO;`. Importing the shapes namespace as well makes the bare name `Path`
// ambiguous between `Avalonia.Controls.Shapes.Path` and `System.IO.Path`, and the file will not
// compile. The alias states which one is meant and cannot be broken by a future global using.
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════
/// GRIP_01 — ONE RESIZE GRIP, EVERY WINDOW, AND IT ACTUALLY RESIZES.
///
/// Every window in the suite is borderless (`ExtendClientAreaToDecorationsHint="True"`), so the
/// OS draws no resize frame and the only affordance a user has for "you can drag this corner" is
/// the one the app draws itself. The state before this class:
///
///   Voice Over Studio      grip drawn, wired.                     works
///   Granular Speed Editor  grip drawn, NEVER WIRED.               a dead decoration — the
///                                                                 single worst outcome, because
///                                                                 it advertises a control that
///                                                                 does nothing when grabbed
///   Main App, Music Wizard, Video Merger,
///   Crop Tools, Settings   no grip at all.
///
/// Two near-identical copies of the same 20 lines had already drifted into "one works, one does
/// not", which is exactly what a shared implementation prevents. Attach() is now the only way a
/// window gets a grip: it reuses the Border the XAML already declares where there is one, builds
/// one where there is not, and wires the behaviour identically in both cases.
///
/// ⚠️ NORTH STAR 1 — the mark is vector geometry built in memory (PathGeometry), never an image
/// asset beside the binary.
/// ⚠️ NORTH STAR 5 — the stroke is bound to the `AppBorderBrush` TOKEN through a resource
/// observable, not resolved once to a literal, so it follows a theme change like everything else.
/// ══════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class WindowResizeGrip
{
    /// <summary>The corner mark: three diagonal strokes, shortest at the outside.</summary>
    private const string GripGeometry = "M 8,20 L 20,8 M 13,20 L 20,13 M 18,20 L 20,18";

    /// <summary>Matches the hit target every existing grip in the suite already used.</summary>
    private const double GripSizePx = 24;

    private const double RestingOpacity = 0.75;
    private const double HoverOpacity = 1.0;

    /// <summary>
    /// Gives <paramref name="window"/> a working bottom-right resize grip. Safe to call on a
    /// window whose XAML already declares a Border named "ResizeGrip" — that one is adopted
    /// rather than duplicated.
    /// </summary>
    public static void Attach(Window window, string tooltip)
    {
        if (window == null) return;

        try
        {
            // A window that cannot be resized must not advertise that it can. This is the same
            // rule as the detach button's "never a dead click" (UI-DETACH): an affordance that
            // does nothing is worse than no affordance.
            if (!window.CanResize) return;

            var grip = window.FindControl<Border>("ResizeGrip") ?? BuildGrip(window);
            if (grip.Parent == null && !TryInject(window, grip)) return;

            Wire(window, grip, tooltip);
        }
        catch (Exception ex)
        {
            // A missing grip is a cosmetic loss. It must never stop a window opening.
            RuntimeLog.Swallowed(ex);
        }
    }

    private static Border BuildGrip(Window window)
    {
        var mark = new ShapePath
        {
            StrokeThickness = 1.5,
            StrokeLineCap = PenLineCap.Round,
            Data = Geometry.Parse(GripGeometry),
            IsHitTestVisible = false
        };

        // NORTH STAR 5 — the colour comes from the named token, never a literal. Resolved the
        // same way VoiceOverWindow.GetAppBrush does it, which is the pattern already proven in
        // this codebase; the fallback exists only so a missing token cannot leave an invisible
        // grip, which would be the dead-decoration failure again by another route.
        mark.Stroke = ResolveBrush(window, "AppBorderBrush", Brushes.Gray);

        return new Border
        {
            Name = "ResizeGrip",
            MinWidth = GripSizePx,
            MinHeight = GripSizePx,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            // Transparent, NOT null: a null background is not hit-testable, so the grip would be
            // visible and unclickable — the dead-decoration failure this class exists to end.
            Background = Brushes.Transparent,
            Child = mark
        };
    }

    /// <summary>
    /// Puts the grip in front of everything in the window. Most windows root a Panel and the grip
    /// simply joins it; a window rooted on a single control (Crop Tools roots a Border) is wrapped
    /// in a Grid first. Wrapping preserves the window's name scope, so every existing
    /// FindControl in that window keeps working.
    /// </summary>
    private static IBrush ResolveBrush(Window window, string token, IBrush fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(token, window.ActualThemeVariant, out var value) == true &&
                value is IBrush brush)
            {
                return brush;
            }
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }

        return fallback;
    }

    private static bool TryInject(Window window, Border grip)
    {
        if (window.Content is Panel panel)
        {
            panel.Children.Add(grip);
            return true;
        }

        if (window.Content is Control existing)
        {
            window.Content = null;
            var host = new Grid();
            host.Children.Add(existing);
            host.Children.Add(grip);
            window.Content = host;
            return true;
        }

        RuntimeLog.Fail("UI", $"'{window.Title}' has no content to host a resize grip; the window is not resizable by corner.");
        return false;
    }

    private static void Wire(Window window, Border grip, string tooltip)
    {
        grip.Cursor = new Cursor(StandardCursorType.BottomRightCorner);
        grip.Opacity = RestingOpacity;
        // Above every overlay, notice and dimmer: the corner must stay grabbable even while a
        // screen is busy, which is the one time a user is most likely to want to make it bigger.
        grip.ZIndex = int.MaxValue;
        ToolTip.SetTip(grip, tooltip);
        AutomationProperties.SetName(grip, tooltip);

        grip.PointerEntered += (_, _) => grip.Opacity = HoverOpacity;
        grip.PointerExited += (_, _) => grip.Opacity = RestingOpacity;

        grip.PointerPressed += (_, e) =>
        {
            // Dragging the corner of a maximized window fights the window manager and lands it in
            // a half-restored state, so the gesture is simply not offered there.
            if (window.WindowState != WindowState.Normal) return;
            if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;

            try
            {
                window.BeginResizeDrag(WindowEdge.SouthEast, e);
                e.Handled = true;
            }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        };
    }
}
