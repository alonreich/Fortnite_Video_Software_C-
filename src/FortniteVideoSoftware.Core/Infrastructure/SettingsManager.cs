using System;
using System.IO;
using System.Text.Json;
using Avalonia.Input;

namespace FortniteVideoSoftware.Core.Infrastructure;

public class AppSettings
{
    public KeyBinds KeyBinds { get; set; } = new();
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
}

public static class SettingsManager
{
    private static readonly string SettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
    
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
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Settings", $"Failed to save settings: {ex.Message}");
        }
    }
}
