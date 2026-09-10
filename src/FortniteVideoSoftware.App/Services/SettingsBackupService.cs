using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.App.Services;

public static class SettingsBackupService
{
    public static async Task ExportSettingsAsync(Window parent, ApplicationPaths paths)
    {
        var topLevel = TopLevel.GetTopLevel(parent);
        if (topLevel == null) return;

        var options = new FilePickerSaveOptions
        {
            Title = "Export Configuration",
            DefaultExtension = "json",
            SuggestedFileName = "FortniteVideoSoftware_Config.json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("JSON Configuration File") { Patterns = new[] { "*.json" } }
            }
        };

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(options);
        if (file != null)
        {
            try
            {
                string stateFile = paths.SessionStateFile;
                if (File.Exists(stateFile))
                {
                    File.Copy(stateFile, file.Path.LocalPath, true);
                    NativeDialog.ShowInfo("Configuration successfully backed up.");
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("Config", $"Export failed: {ex.Message}");
                NativeDialog.ShowError($"Failed to export configuration: {ex.Message}", "Export Failed");
            }
        }
    }

    public static async Task<bool> ImportSettingsAsync(Window parent, ApplicationPaths paths, Action onBeforeExit)
    {
        var topLevel = TopLevel.GetTopLevel(parent);
        if (topLevel == null) return false;

        var options = new FilePickerOpenOptions
        {
            Title = "Import Configuration",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("JSON Configuration File") { Patterns = new[] { "*.json" } }
            }
        };

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(options);
        if (files.Count > 0)
        {
            try
            {
                string json = File.ReadAllText(files[0].Path.LocalPath);

                var state = JsonNode.Parse(json)?.AsObject();
                if (state == null) throw new InvalidOperationException("File is empty or invalid JSON.");
                if (state["schema_version"] == null) throw new InvalidOperationException("Missing schema_version lock. This is not a valid Fortnite Video Software configuration file.");

                File.WriteAllText(paths.SessionStateFile, json);

                NativeDialog.ShowInfo("Configuration successfully restored!\n\nThe application will now close to apply changes. Please restart it manually.");

                onBeforeExit();
                Environment.Exit(0);
                return true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("Config", $"Import failed: {ex.Message}");
                NativeDialog.ShowError($"Invalid configuration file: {ex.Message}", "Import Failed");
            }
        }
        return false;
    }
}
