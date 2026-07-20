using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Input;

namespace FortniteVideoSoftware.App.Infrastructure;

public enum ThemeMode
{
    FollowOS,
    Dark,
    Light
}

public enum FontScale
{
    ExtraSmall,
    Small,
    Medium,
    Normal,
    Large,
    ExtraLarge
}

public static class FontScaleExtensions
{
    public static double ToMultiplier(this FontScale scale) => scale switch
    {
        FontScale.ExtraSmall => 0.80,
        FontScale.Small => 0.90,
        FontScale.Medium => 0.95,
        FontScale.Normal => 1.00,
        FontScale.Large => 1.12,
        FontScale.ExtraLarge => 1.25,
        _ => 1.00
    };
}

public class AppSettings
{
    public KeyBinds KeyBinds { get; set; } = new();
    public DefaultValues Defaults { get; set; } = new();
    public int Volume { get; set; } = 100;
    public string ActiveMaskOverlay { get; set; } = "Fortnite";

    // Appearance
    public ThemeMode ThemeMode { get; set; } = ThemeMode.FollowOS;
    public FontScale FontScale { get; set; } = FontScale.Normal;

    // Confirmation Dialogs
    public bool ConfirmVideoMergerRemove { get; set; } = false;
    public bool ConfirmVideoMergerClearAll { get; set; } = false;
    public bool ConfirmCropToolReset { get; set; } = true;
    public bool ConfirmCropToolDelete { get; set; } = true;

    /// <summary>
    /// Meme System §1/§3: the unified meme asset directory. Empty = use the default
    /// (MyVideos\Fortnite Video Software\Memes). Changed via Settings → Meme folder.
    /// Always resolve through <see cref="MemeDirectory.GetActive"/> — never read this raw.
    /// </summary>
    public string MemeDirectoryPath { get; set; } = "";
}

/// <summary>
/// Meme System §1: single source of truth for the ACTIVE meme directory.
/// Default is MyVideos\Fortnite Video Software\Memes, overridable via Settings.
/// </summary>
public static class MemeDirectory
{
    /// <summary>Raised after the user successfully changes the meme directory in Settings, so
    /// the MainWindow can silently re-scan and rebuild the MemeComboBox (§3 State Update).</summary>
    public static event Action? Changed;
    public static void NotifyChanged() { try { Changed?.Invoke(); } catch { } }

    public static string GetDefault() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "Fortnite Video Software", "Memes");

    /// <summary>Resolves the active directory (settings override or default) and ensures it exists.</summary>
    public static string GetActive()
    {
        string configured = SettingsManager.Instance.MemeDirectoryPath;
        string dir = string.IsNullOrWhiteSpace(configured) ? GetDefault() : configured;
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }
}

public class KeyBinds
{
    public Key PlayPause { get; set; } = Key.Space;
    public Key MarkStart { get; set; } = Key.OemOpenBrackets;
    public Key MarkEnd { get; set; } = Key.OemCloseBrackets;
    public Key SeekForward { get; set; } = Key.Right;
    public Key SeekBackward { get; set; } = Key.Left;
    public Key VolumeUp { get; set; } = Key.Up;
    public Key VolumeDown { get; set; } = Key.Down;
    public Key FineSeekForward { get; set; } = Key.Right;
    public Key FineSeekBackward { get; set; } = Key.Left;
    public Key AggressiveVolumeUp { get; set; } = Key.Up;
    public Key AggressiveVolumeDown { get; set; } = Key.Down;
}

public enum CheckboxDefaultBehavior
{
    AlwaysOff,
    AlwaysOn,
    RememberLast
}

public enum ValueDefaultBehavior
{
    FixedValue,
    RememberLast
}

/// <summary>
/// Default initial values applied when the app freshly opens.
/// Edited via the Settings window → "Defaults" tab.
/// </summary>
public class DefaultValues
{
    /// <summary>Default speed multiplier (e.g. 1.1 = 1.1x). Range 0.1–4.0</summary>
    public double DefaultSpeed { get; set; } = 1.1;
    public ValueDefaultBehavior SpeedBehavior { get; set; } = ValueDefaultBehavior.FixedValue;

    /// <summary>Default Portrait Mode checkbox state</summary>
    public bool PortraitMode { get; set; } = true;
    public CheckboxDefaultBehavior PortraitBehavior { get; set; } = CheckboxDefaultBehavior.RememberLast;

    /// <summary>Default Boss HP checkbox state</summary>
    public bool BossHp { get; set; } = false;
    public CheckboxDefaultBehavior BossHpBehavior { get; set; } = CheckboxDefaultBehavior.AlwaysOff;

    /// <summary>Default Show Teammates checkbox state</summary>
    public bool ShowTeammates { get; set; } = false;
    public CheckboxDefaultBehavior ShowTeammatesBehavior { get; set; } = CheckboxDefaultBehavior.AlwaysOff;

    /// <summary>Default Disable Fade checkbox state</summary>
    public bool EnableFade { get; set; } = true;
    public CheckboxDefaultBehavior EnableFadeBehavior { get; set; } = CheckboxDefaultBehavior.AlwaysOn;

    public int QualityIndex { get; set; } = 7;
    public ValueDefaultBehavior QualityBehavior { get; set; } = ValueDefaultBehavior.FixedValue;

    public bool AutoVoiceNormalization { get; set; } = true;
    public bool AutoSpikeFlattening { get; set; } = true;
    
    /// <summary>Whether to remember the music and video volume set in the music wizard</summary>
    public bool RememberMusicVolumes { get; set; } = true;

    // ---- Granular Speed Editor defaults ----
    /// <summary>Default zoom-in ramp: true = SLOW (gradual), false = INSTANT (hard cut).</summary>
    public bool DefaultZoomSlow { get; set; } = false;
    /// <summary>Default freeze-image hold duration in seconds (matches the preset buttons 0.5–3.0).</summary>
    public double DefaultFreezeDurationS { get; set; } = 1.0;
}

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(KeyBinds))]
[JsonSerializable(typeof(DefaultValues))]
[JsonSerializable(typeof(CheckboxDefaultBehavior))]
[JsonSerializable(typeof(ValueDefaultBehavior))]
[JsonSerializable(typeof(ThemeMode))]
[JsonSerializable(typeof(FontScale))]
public partial class SettingsJsonContext : JsonSerializerContext { }

public static class SettingsManager
{
    private static string SettingsPath => Path.Combine(FortniteVideoSoftware.Core.Infrastructure.ApplicationPaths.CreateDefault().ProgramDataRoot, "settings.json");
    
    public static AppSettings Instance { get; private set; } = new AppSettings();

    public static void Load()
    {
        if (File.Exists(SettingsPath))
        {
            try
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);
                if (loaded != null) Instance = loaded;
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("Settings", $"Failed to load settings: {ex.Message}");
            }
        }
    }

    public static void Save()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            options.TypeInfoResolver = SettingsJsonContext.Default;
            var json = JsonSerializer.Serialize(Instance, options);
            string tempFile = SettingsPath + ".tmp";
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Settings", $"Failed to save settings: {ex.Message}");
        }
    }
}
