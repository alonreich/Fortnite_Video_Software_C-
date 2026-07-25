using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace FortniteVideoSoftware.App.Infrastructure;

/// <summary>
/// Centralized runtime theme and font-scaling manager.
/// Applies the user-selected ThemeVariant and global font-size multiplier
/// across every open window. Works with DynamicResource tokens defined in AvaloniaApp.axaml.
/// </summary>
public static class ThemeManager
{
    private static double _currentFontMultiplier = 1.0;

    /// <summary>
    /// Applies the saved theme and font scale from SettingsManager.
    /// Call once at startup (after settings are loaded).
    /// </summary>
    public static void ApplyFromSettings()
    {
        var settings = SettingsManager.Instance;
        ApplyTheme(settings.ThemeMode);
        ApplyFontScale(settings.FontScale);
    }

    /// <summary>
    /// Sets the application-wide ThemeVariant based on user preference.
    /// </summary>
    public static void ApplyTheme(ThemeMode mode)
    {
        if (Application.Current == null) return;

        Application.Current.RequestedThemeVariant = mode switch
        {
            ThemeMode.Dark => ThemeVariant.Dark,
            ThemeMode.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default
        };
    }

    /// <summary>
    /// Applies a global font-size multiplier to every top-level window.
    /// Uses FontSize multiplier on Window-level resources so all child
    /// controls inherit the scaled font via relative sizing.
    /// </summary>
    public static void ApplyFontScale(FontScale scale)
    {
        _currentFontMultiplier = scale.ToMultiplier();
        ApplyFontSizeTokens();
        ApplyFontMultiplierToAllWindows();
    }

    /// <summary>
    /// Base values of every runtime-scaled resource token. All FontSize attributes in
    /// the app's XAML reference the numbered AppFontSizeN tokens (N = base size), and
    /// the button/control sizing tokens make buttons physically grow and shrink with
    /// the chosen font scale. At Normal scale (1.0) nothing changes.
    /// </summary>
    private static readonly (string Key, double Base)[] s_scaledTokens =
    {
        ("AppFontSizeSmall", 10), ("AppFontSizeBase", 11), ("AppFontSizeNormal", 13),
        ("AppFontSize9", 9), ("AppFontSize10", 10), ("AppFontSize11", 11),
        ("AppFontSize12", 12), ("AppFontSize13", 13), ("AppFontSize14", 14),
        ("AppFontSize16", 16), ("AppFontSize18", 18), ("AppFontSize20", 20),
        ("AppFontSize24", 24),
        ("AppFontSize28", 28), ("AppFontSize36", 36), ("AppFontSize48", 48),
        ("AppFontSize60", 60),
        ("AppButtonMinHeight", 32), ("AppSpeedPresetMinWidth", 31),
        ("AppSpeedPresetMinHeight", 25), ("AppCloseBtnSize", 32),
    };

    /// <summary>
    /// Scales EVERY font-size and control-sizing token defined in AvaloniaApp.axaml so
    /// all text and all buttons across the whole app follow the chosen font scale.
    /// </summary>
    private static void ApplyFontSizeTokens()
    {
        if (Application.Current is null) return;
        var res = Application.Current.Resources;
        foreach (var (key, baseValue) in s_scaledTokens)
        {
            res[key] = Math.Round(baseValue * _currentFontMultiplier, 1);
        }
    }

    /// <summary>
    /// For controls created in code-behind: returns the base font size scaled by the
    /// current font-scale setting. Use instead of hardcoded FontSize literals.
    /// </summary>
    public static double ScaledFontSize(double baseSize) => Math.Round(baseSize * _currentFontMultiplier, 1);

    /// <summary>
    /// Returns the current font-size multiplier (1.0 = normal).
    /// </summary>
    public static double CurrentFontMultiplier => _currentFontMultiplier;

    private static void ApplyFontMultiplierToAllWindows()
    {
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows)
            {
                ApplyFontMultiplierToWindow(window);
            }
        }
    }

    /// <summary>
    /// Applies the font-size multiplier to a single window.
    /// Call this after creating a new window.
    /// </summary>
    public static void ApplyFontMultiplierToWindow(Window window)
    {
        window.FontSize = Math.Round(13.0 * _currentFontMultiplier, 1);
    }
}