using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using System;

namespace FortniteVideoSoftware.App.Infrastructure;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// THEME_01 — THE ONE SAFE WAY TO PULL A BRUSH OR COLOUR OUT OF THE THEME FROM CODE-BEHIND.
///
/// ⚠️ WHY THIS EXISTS — READ BEFORE WRITING `FindResource` ANYWHERE.
/// `FindResource` DOES NOT RETURN NULL WHEN A KEY IS MISSING. It returns
/// <see cref="AvaloniaProperty.UnsetValue"/>, an instance of `Avalonia.UnsetValueType`. So this,
/// which reads like defensive code, is a live grenade:
///
///     Foreground = (IBrush?)Application.Current?.FindResource("AppTextMutedBrush") ?? Brushes.Gray;
///
/// The `?? Brushes.Gray` never runs. The CAST fires first and throws:
///     InvalidCastException: Unable to cast object of type 'Avalonia.UnsetValueType'
///                           to type 'Avalonia.Media.IBrush'
/// That exact line stopped the Video Merger from opening at all.
///
/// AND THE KEY MOST LIKELY TO GO MISSING IS A PERFECTLY GOOD ONE. Half of this app's brushes live
/// inside `ResourceDictionary.ThemeDictionaries` in AvaloniaApp.axaml — AppSurfaceBrush,
/// AppPanelBrush, AppBorderBrush, AppTextPrimaryBrush, AppTextMutedBrush, AppControlTrackBrush and
/// friends. A theme-scoped resource can only be resolved against a THEME VARIANT. Ask without one,
/// or ask before the app's variant has settled during startup, and you get UnsetValue back for a
/// key that is sitting right there in the file.
///
/// THE RULES THIS CLASS ENFORCES:
///   (T1) Use `TryFindResource`, never `FindResource`. It reports failure instead of handing back
///        a sentinel that looks like a value and explodes at the cast.
///   (T2) Always pass a ThemeVariant, and prefer the CONTROL's over the Application's. A control
///        already attached to a window knows its own variant even while the app's is still
///        settling, and it also picks up any ThemeVariantScope it happens to sit inside.
///   (T3) Always take a fallback. A missing brush must degrade to a readable colour — never to a
///        crash, and never to an invisible control.
///
/// USAGE — pass the control that will DISPLAY the brush as the host:
///     status.Foreground = ThemeResources.Brush(status, "AppTextMutedBrush", Brushes.Gray);
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class ThemeResources
{
    /// <summary>
    /// Resolves a brush against <paramref name="host"/>'s current theme variant, falling back to
    /// <paramref name="fallback"/> if the key is missing, is not a brush, or the lookup throws.
    /// Never throws. Never returns null.
    /// </summary>
    public static IBrush Brush(Control? host, string key, IBrush fallback)
    {
        try
        {
            ThemeVariant? theme = host?.ActualThemeVariant;

            if (host != null && host.TryFindResource(key, theme, out object? fromHost) && fromHost is IBrush hb)
            {
                return hb;
            }

            if (Application.Current is { } app && app.TryFindResource(key, theme, out object? fromApp) && fromApp is IBrush ab)
            {
                return ab;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }

        return fallback;
    }

    /// <summary>
    /// Same contract as <see cref="Brush"/>, for raw Color tokens. Accepts either a Color resource
    /// or a SolidColorBrush resource, so a token can be redefined as either without breaking here.
    /// </summary>
    public static Color Colour(Control? host, string key, Color fallback)
    {
        try
        {
            ThemeVariant? theme = host?.ActualThemeVariant;

            if (host != null && host.TryFindResource(key, theme, out object? fromHost))
            {
                if (fromHost is Color hc) return hc;
                if (fromHost is ISolidColorBrush hb) return hb.Color;
            }

            if (Application.Current is { } app && app.TryFindResource(key, theme, out object? fromApp))
            {
                if (fromApp is Color ac) return ac;
                if (fromApp is ISolidColorBrush ab) return ab.Color;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }

        return fallback;
    }
}
