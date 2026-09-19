// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Ipc;

namespace FortniteVideoSoftware.App.Services;

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

                // ⚠️ UNCHANGED CONTRACT: no state file means silently do nothing — no dialog, no
                // error. Exporting before the app has ever written state is not a failure.
                if (File.Exists(stateFile))
                {
                    // CONFIGIO_01 — read a CONSISTENT snapshot instead of byte-copying a file a
                    // sibling process may be mid-write on. LoadSync prefers the live in-process
                    // server, then the pipe, then the file under the named mutex — so whichever
                    // holds the authoritative copy is the one that gets exported.
                    // Off the UI thread: the mutex fallback can wait up to DefaultMutexTimeout.
                    var store = new StateTransferStore(paths);
                    JsonObject snapshot = await Task.Run(() => store.LoadSync());

                    // CONFIGIO_01 — refuse to write a backup that could never be imported.
                    // LoadSync returns an EMPTY JsonObject when it cannot take the lock, and the
                    // import gate below rejects any document without schema_version. Writing that
                    // silently would hand the user a file that fails on restore, months later,
                    // with no way to tell why.
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

                // ⚠️ UNCHANGED, AND STILL FIRST. This is the only thing separating a real backup
                // from an arbitrary .json, and its exact wording is what the user sees. The
                // sanitiser below is ADDED after it, never substituted for it.
                if (state["schema_version"] == null) throw new InvalidOperationException("Missing schema_version lock. This is not a valid Fortnite Video Software configuration file.");

                // CONFIGIO_01 — everything past the schema gate is still untrusted input. Run it
                // through the SAME validator every other writer passes: per-key types, numeric
                // bounds, BoundsKeys/SubprocessStateKeys shapes. Rejected keys are dropped and
                // logged by the sanitiser itself, so a stripped import is diagnosable rather than
                // silent, and the app cannot be started into an unparseable state by a hand-edited
                // or corrupted file.
                JsonObject sanitized = StateTransferStore.SanitizeObjectInternal(state, "config-import");

                // CONFIGIO_01 — commit through the store, which takes the named mutex, re-stamps
                // schema_version and writes via AtomicJsonFile (temp file -> WriteThrough ->
                // flush-to-disk -> atomic rename). It also routes to the live NamedPipeStateServer
                // when one is running, so the import can no longer be silently overwritten by that
                // server's next flush.
                await new StateTransferStore(paths).SaveAsync(sanitized);

                // CONFIGIO_01 — Environment.Exit(0) below runs no finalizers and flushes nothing.
                // When this process owns the state server, SaveAsync only marked it dirty on a
                // debounce timer that the exit would kill, so force the write to disk here and
                // confirm it before going anywhere near the exit.
                // (When another PROCESS owns the state, that server owns the flush; it stays alive
                // after this one exits, so there is nothing to force from here.)
                NamedPipeStateServer.ActiveInstance?.FlushToDiskSafe();

                NativeDialog.ShowInfo("Configuration successfully restored!\n\nThe application will now close to apply changes. Please restart it manually.");

                // ⚠️ RECOVERY_02: onBeforeExit() carries MarkCleanShutdownIntent(). It MUST run
                // before the exit or the next launch reports a crash that never happened.
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
