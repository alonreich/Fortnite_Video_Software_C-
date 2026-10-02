using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// CONFIGIO_01 — the user-facing backup/restore of <c>session_state.json</c>.
///
/// WHAT WAS WRONG. Both halves of this type reached around every guard that file has.
///
/// IMPORT did <c>File.WriteAllText(paths.SessionStateFile, json)</c> on bytes the user picked off
/// disk, gated only by "does a schema_version key exist". Four separate protections were skipped
/// in that one line:
///   1. SCHEMA VALIDATION. <c>StateTransferStore.SanitizeObject</c> walks every property through
///      <c>ValidateKnownProperty</c>, which enforces per-key types, numeric bounds, the BoundsKeys
///      shape and the SubprocessStateKeys shape. None of it ran. A file with the right two words
///      at the top and arbitrary junk below landed on disk unfiltered and was then consumed by the
///      Main App, the Merger, the Crop Tool and WindowBoundsHelper.
///   2. THE CROSS-PROCESS MUTEX. Every other read and write of this file takes
///      <c>Global\FvsStateTransferMutex</c>. This took nothing, so a sibling process mid-read got
///      a torn document.
///   3. ATOMICITY AND DURABILITY. <c>File.WriteAllText</c> truncates in place and returns when the
///      bytes reach the OS cache, not the platter — not the temp-file + WriteThrough + flush +
///      atomic-rename protocol <c>docs/05_SYSTEM_LIFECYCLE_STORAGE.md#SYS-RECOVERY</c> mandates.
///   4. THE LIVE IN-MEMORY OWNER. When a <c>NamedPipeStateServer</c> is running, IT owns session
///      state and every reader prefers it over the file. Writing the file underneath it meant the
///      import was silently overwritten by that server's next flush — the import appeared to
///      succeed and was then discarded, which is worse than failing.
/// <c>Environment.Exit(0)</c> six lines later removed the last chance to recover: it runs no
/// finalizers and flushes no OS write cache.
///
/// EXPORT did a raw <c>File.Copy</c> of the same file with no mutex, so the backup the user just
/// made could be a torn snapshot of a document another process was mid-write on.
///
/// Both now go through <see cref="StateTransferStore"/>, which already owns the sanitiser, the
/// mutex and <c>AtomicJsonFile</c>. Nothing here re-implements any of them.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
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
            SuggestedFileName = "FreeVideoStudio_Config.json",
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
                    var store = new StateTransferStore(paths);
                    JsonObject snapshot = await Task.Run(() => store.LoadSync());

                    if (snapshot.Count == 0 || snapshot["schema_version"] == null)
                    {
                        RuntimeLog.Fail("Config",
                            "Export aborted: could not obtain a complete session-state snapshot (the file is locked by another process, or holds no state yet).");
                        NativeDialog.ShowError(
                            "Could not read the current configuration — another copy of the app may be using it. Please close any other windows and try again.",
                            "Export Failed");
                        return;
                    }

                    await File.WriteAllTextAsync(
                        file.Path.LocalPath,
                        snapshot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

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

                if (state["schema_version"] == null) throw new InvalidOperationException("Missing schema_version lock. This is not a valid Free Video Studio configuration file.");

                JsonObject sanitized = StateTransferStore.SanitizeObjectInternal(state, "config-import");

                await new StateTransferStore(paths).SaveAsync(sanitized);

                NamedPipeStateServer.ActiveInstance?.FlushToDiskSafe();

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
