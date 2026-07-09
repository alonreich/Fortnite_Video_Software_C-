using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Input;

namespace FortniteVideoSoftware.App.Infrastructure;

public class AppSettings
{
    public KeyBinds KeyBinds { get; set; } = new();
    public DefaultValues Defaults { get; set; } = new();
    public int Volume { get; set; } = 100;
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
    public bool NoFade { get; set; } = false;
    public CheckboxDefaultBehavior NoFadeBehavior { get; set; } = CheckboxDefaultBehavior.AlwaysOff;

    /// <summary>Default Output File Size slider index (0-20, where 7 = 40MB)</summary>
    public int QualityIndex { get; set; } = 7;
    public ValueDefaultBehavior QualityBehavior { get; set; } = ValueDefaultBehavior.FixedValue;
}

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
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
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
            var json = JsonSerializer.Serialize(Instance, new JsonSerializerOptions { WriteIndented = true });
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
