// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Text.Json;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Ipc;

public sealed class StateTransferStore
{
    public const string MutexName = @"Global\FvsStateTransferMutex";
    public const int SchemaVersion = 1;
    public static readonly TimeSpan DefaultMutexTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan InteractiveMutexTimeout = TimeSpan.FromSeconds(2);

    private static readonly string[] BoundsKeys =
    [
        "MainWindowBounds",
        "VideoMergerBounds",
        "CropToolBounds",
        "GranularBounds",
        "MusicWizardBounds",
        "SettingsBounds",
        "VoiceOverWindowBounds",
        "PreviewMonitorWindowBounds",
        "GranularPreviewMonitorBounds",
        "MusicWizardPreviewMonitorBounds",
        "VoiceOverPreviewMonitorBounds",
        "MergerPreviewMonitorBounds"
    ];

    private static readonly string[] SubprocessStateKeys =
    [
        "AdvancedEditorState",
        "VideoMergerState",
        "CropToolState"
    ];

    private static readonly string[] DirectoryPreferenceKeys =
    [
        "UploadVideoDirectory",
        "MergerUploadDirectory",
        "MergerOutputDirectory",
        "CropToolUploadDirectory",
        "CustomMusicDirectory"
    ];

    public StateTransferStore(ApplicationPaths? paths = null)
    {
        Paths = paths ?? ApplicationPaths.CreateDefault();
    }

    public ApplicationPaths Paths { get; }

    public static JsonObject SanitizeObjectInternal(JsonObject state, string context) => SanitizeObject(state, context);
    public static void ApplySanitizedUpdatesInternal(JsonObject current, JsonObject updates, string context) => ApplySanitizedUpdates(current, updates, context);

    public static JsonObject LoadFromDiskDirect(ApplicationPaths paths)
    {
        try
        {
            var store = new StateTransferStore(paths);
            return store.LoadUnlocked();
        }
        catch (System.Exception swallowed6)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed6);   // FAULTTIER_02 — no failure is silent.
            return new JsonObject { ["schema_version"] = SchemaVersion };
        }
    }

    public async Task<JsonObject> LoadAsync(CancellationToken cancellationToken = default)
    {
        var server = NamedPipeStateServer.ActiveInstance;
        if (server != null && server.IsRunning)
        {
            return server.GetState();
        }

        var startedServer = NamedPipeStateServer.TryStart(Paths);
        if (startedServer != null)
        {
            return startedServer.GetState();
        }

        var ipcState = await NamedPipeStateClient.GetStateAsync(NamedPipeStateClient.FastProbeTimeout, cancellationToken).ConfigureAwait(false);
        if (ipcState != null)
        {
            return ipcState;
        }

        return await Task.Run(() =>
        {
            Paths.EnsureWritableDirectories();

            try
            {
                using NamedSystemMutex guard = NamedSystemMutex.Acquire(
                    MutexName,
                    DefaultMutexTimeout,
                    cancellationToken);

                return LoadUnlocked();
            }
            catch (FortniteVideoSoftware.Core.Infrastructure.LockException swallowed3)
            {
                global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed3);   // FAULTTIER_02 — no failure is silent.
                return new JsonObject();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public JsonObject LoadSync(CancellationToken cancellationToken = default, TimeSpan? mutexTimeout = null)
    {
        var server = NamedPipeStateServer.ActiveInstance;
        if (server != null && server.IsRunning)
        {
            return server.GetState();
        }

        var startedServer = NamedPipeStateServer.TryStart(Paths);
        if (startedServer != null)
        {
            return startedServer.GetState();
        }

        var ipcState = NamedPipeStateClient.GetStateSync(NamedPipeStateClient.FastProbeTimeout, cancellationToken);
        if (ipcState != null)
        {
            return ipcState;
        }

        try
        {
            Paths.EnsureWritableDirectories();
            using NamedSystemMutex guard = NamedSystemMutex.Acquire(
                MutexName,
                mutexTimeout ?? DefaultMutexTimeout,
                cancellationToken);

            return LoadUnlocked();
        }
        catch (System.Exception swallowed5)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed5);   // FAULTTIER_02 — no failure is silent.
            return new JsonObject();
        }
    }

    public async Task SaveAsync(JsonObject state, CancellationToken cancellationToken = default)
    {
        var server = NamedPipeStateServer.ActiveInstance;
        if (server != null && server.IsRunning)
        {
            server.SaveState(state);
            return;
        }

        bool sent = await NamedPipeStateClient.SaveStateAsync(state, NamedPipeStateClient.DefaultTimeout, cancellationToken).ConfigureAwait(false);
        if (sent) return;

        await Task.Run(() =>
        {
            Paths.EnsureWritableDirectories();

            try
            {
                using NamedSystemMutex guard = NamedSystemMutex.Acquire(
                    MutexName,
                    DefaultMutexTimeout,
                    cancellationToken);

                JsonObject payload = SanitizeObject(Clone(state), "save");
                payload["schema_version"] = SchemaVersion;
                AtomicJsonFile.WriteObject(Paths.SessionStateFile, payload);
            }
            catch (FortniteVideoSoftware.Core.Infrastructure.LockException swallowed2)
            {
                global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed2);   // FAULTTIER_02 — no failure is silent.
            }
            catch (OperationCanceledException swallowed20)
            {
                global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed20);   // FAULTTIER_02 — no failure is silent.
            }
            catch (Exception ex)
            {
                CoreLogger.Fail("SessionState", $"Could not save session state: {ex.Message}");
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdatePropertiesAsync(JsonObject updates, CancellationToken cancellationToken = default)
    {
        var server = NamedPipeStateServer.ActiveInstance;
        if (server != null && server.IsRunning)
        {
            server.UpdateProperties(updates);
            return;
        }

        bool sent = await NamedPipeStateClient.UpdatePropertiesAsync(updates, NamedPipeStateClient.DefaultTimeout, cancellationToken).ConfigureAwait(false);
        if (sent) return;

        await Task.Run(() =>
        {
            Paths.EnsureWritableDirectories();

            try
            {
                using NamedSystemMutex guard = NamedSystemMutex.Acquire(
                    MutexName,
                    DefaultMutexTimeout,
                    cancellationToken);

                JsonObject current = LoadUnlocked();
                ApplySanitizedUpdates(current, updates, "update");

                current["schema_version"] = SchemaVersion;
                AtomicJsonFile.WriteObject(Paths.SessionStateFile, current);
            }
            catch (FortniteVideoSoftware.Core.Infrastructure.LockException swallowed)
            {
                global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed);   // FAULTTIER_02 — no failure is silent.
            }
            catch (OperationCanceledException swallowed9)
            {
                global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed9);   // FAULTTIER_02 — no failure is silent.
            }
            catch (Exception ex)
            {
                CoreLogger.Fail("SessionState", $"Could not update session state: {ex.Message}");
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public void UpdatePropertiesSync(JsonObject updates, CancellationToken cancellationToken = default, TimeSpan? mutexTimeout = null)
    {
        var server = NamedPipeStateServer.ActiveInstance;
        if (server != null && server.IsRunning)
        {
            server.UpdateProperties(updates);
            return;
        }

        bool sent = NamedPipeStateClient.UpdatePropertiesSync(updates, NamedPipeStateClient.FastProbeTimeout, cancellationToken);
        if (sent) return;

        JsonObject clonedUpdates = Clone(updates);
        try
        {
            Paths.EnsureWritableDirectories();

            using NamedSystemMutex guard = NamedSystemMutex.Acquire(
                MutexName,
                mutexTimeout ?? DefaultMutexTimeout,
                cancellationToken);

            JsonObject current = LoadUnlocked();
            ApplySanitizedUpdates(current, clonedUpdates, "update (sync)");

            current["schema_version"] = SchemaVersion;
            AtomicJsonFile.WriteObject(Paths.SessionStateFile, current);
        }
        catch (FortniteVideoSoftware.Core.Infrastructure.LockException ex)
        {
            CoreLogger.Fail("SessionState", $"Could not update session state — the file was locked: {ex.Message}");
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("SessionState", $"Could not update session state: {ex.Message}");
        }
    }

    public async Task<bool> SendHandoffAsync(HandoffPayload payload, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, typeof(HandoffPayload), IpcJsonContext.Default);
        var node = JsonNode.Parse(bytes) as JsonObject ?? new JsonObject();
        if (payload.ReturnedFromCropTool.HasValue)
        {
            node["returned_from_crop_tool"] = payload.ReturnedFromCropTool.Value;
        }

        var server = NamedPipeStateServer.ActiveInstance;
        if (server != null && server.IsRunning)
        {
            server.UpdateProperties(node);
            return true;
        }

        bool sent = await NamedPipeStateClient.SendHandoffAsync(node, timeout ?? NamedPipeStateClient.DefaultTimeout, ct).ConfigureAwait(false);
        if (sent) return true;

        await UpdatePropertiesAsync(node, ct).ConfigureAwait(false);
        return true;
    }

    public bool SendHandoffSync(HandoffPayload payload, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, typeof(HandoffPayload), IpcJsonContext.Default);
        var node = JsonNode.Parse(bytes) as JsonObject ?? new JsonObject();
        if (payload.ReturnedFromCropTool.HasValue)
        {
            node["returned_from_crop_tool"] = payload.ReturnedFromCropTool.Value;
        }

        var server = NamedPipeStateServer.ActiveInstance;
        if (server != null && server.IsRunning)
        {
            server.UpdateProperties(node);
            return true;
        }

        bool sent = NamedPipeStateClient.SendHandoffSync(node, timeout ?? NamedPipeStateClient.DefaultTimeout, ct);
        if (sent) return true;

        UpdatePropertiesSync(node, ct);
        return true;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var server = NamedPipeStateServer.ActiveInstance;
        if (server != null && server.IsRunning)
        {
            server.ClearState();
        }

        await NamedPipeStateClient.ClearStateAsync(NamedPipeStateClient.DefaultTimeout, cancellationToken).ConfigureAwait(false);

        await Task.Run(() =>
        {
            Paths.EnsureWritableDirectories();

            try
            {
                using NamedSystemMutex guard = NamedSystemMutex.Acquire(
                    MutexName,
                    DefaultMutexTimeout,
                    cancellationToken);

                AtomicJsonFile.TryDelete(Paths.SessionStateFile);
            }
            catch (FortniteVideoSoftware.Core.Infrastructure.LockException swallowed12)
            {
                global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed12);   // FAULTTIER_02 — no failure is silent.
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private JsonObject LoadUnlocked()
    {
        try
        {
            JsonObject state = AtomicJsonFile.ReadObject(Paths.SessionStateFile) ?? new JsonObject();
            if (state.Count == 0) return state;

            bool versionOk =
                state.TryGetPropertyValue("schema_version", out var versionNode) &&
                versionNode is JsonValue versionVal &&
                versionVal.TryGetValue<int>(out int version) &&
                version == SchemaVersion;

            if (!versionOk)
            {
                CoreLogger.Info("SessionState",
                    $"Session state has a different schema version; keeping only the entries this build understands (expected {SchemaVersion}).");
                JsonObject migrated = SanitizeObject(state, "load (version mismatch)");
                migrated["schema_version"] = SchemaVersion;
                return migrated;
            }

            return SanitizeObject(state, "load");
        }
        catch (JsonException ex)
        {
            CoreLogger.Fail("SessionState", $"Session state file is not valid JSON and has been quarantined: {ex.Message}");
            QuarantineCorruptedSessionFile();
            return new JsonObject();
        }
        catch (InvalidDataException ex)
        {
            CoreLogger.Fail("SessionState", $"Session state could not be interpreted: {ex.Message}");
            return new JsonObject();
        }
        catch (IOException ex)
        {
            CoreLogger.Fail("SessionState", $"Session state could not be read right now: {ex.Message}");
            return new JsonObject();
        }
        catch (UnauthorizedAccessException swallowed18)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed18);   // FAULTTIER_02 — no failure is silent.
            return new JsonObject();
        }
    }

    private static JsonObject SanitizeObject(JsonObject state, string context)
    {
        var clean = new JsonObject();
        List<string>? dropped = null;

        foreach (KeyValuePair<string, JsonNode?> property in state)
        {
            if (TryAcceptProperty(property.Key, property.Value, out JsonNode? accepted))
            {
                clean[property.Key] = accepted;
            }
            else
            {
                (dropped ??= new List<string>()).Add(property.Key);
            }
        }

        if (dropped != null)
        {
            CoreLogger.Info("SessionState",
                $"Ignored {dropped.Count} unusable session_state entr{(dropped.Count == 1 ? "y" : "ies")} during {context}: {string.Join(", ", dropped)}.");
        }

        return clean;
    }

    private static void ApplySanitizedUpdates(JsonObject current, JsonObject updates, string context)
    {
        List<string>? dropped = null;

        foreach (KeyValuePair<string, JsonNode?> property in updates)
        {
            if (TryAcceptProperty(property.Key, property.Value, out JsonNode? accepted))
            {
                current[property.Key] = accepted;
            }
            else
            {
                (dropped ??= new List<string>()).Add(property.Key);
            }
        }

        if (dropped != null)
        {
            CoreLogger.Fail("SessionState",
                $"Refused {dropped.Count} unusable session_state entr{(dropped.Count == 1 ? "y" : "ies")} during {context}: {string.Join(", ", dropped)}. Everything else was saved.");
        }
    }

    private static bool TryAcceptProperty(string key, JsonNode? value, out JsonNode? accepted)
    {
        accepted = null;

        try
        {
            ValidateKnownProperty(key, value);
        }
        catch (InvalidDataException swallowed13)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed13);   // FAULTTIER_02 — no failure is silent.
            return false;
        }
        catch (Exception swallowed8)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed8);   // FAULTTIER_02 — no failure is silent.
            return false;
        }

        accepted = value?.DeepClone();
        return true;
    }

    private void QuarantineCorruptedSessionFile()
    {
        string path = Paths.SessionStateFile;
        if (!File.Exists(path))
        {
            return;
        }

        string corruptedPath = $"{path}.corrupted";
        AtomicJsonFile.TryDelete(corruptedPath);
        try
        {
            File.Move(path, corruptedPath, overwrite: true);
        }
        catch (IOException swallowed19)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed19);   // FAULTTIER_02 — no failure is silent.
        }
        catch (UnauthorizedAccessException swallowed14)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed14);   // FAULTTIER_02 — no failure is silent.
        }
    }

    private static JsonObject Clone(JsonObject source)
    {
        return source.DeepClone().AsObject();
    }

    private static void ValidateKnownProperty(string key, JsonNode? value)
    {
        if (value is null)
        {
            return;
        }

        if (key == "schema_version")
        {
            if (!TryGetInt(value, out int schemaVersion) || schemaVersion < 1)
                throw new InvalidDataException("Invalid session_state schema_version.");
            return;
        }

        if (BoundsKeys.Contains(key))
        {
            if (value is not JsonObject bounds)
                throw new InvalidDataException($"Invalid session_state bounds object for '{key}'.");
            ValidateBoundsObject(key, bounds);
            return;
        }

        if (DirectoryPreferenceKeys.Contains(key))
        {
            if (!TryGetString(value, out _))
                throw new InvalidDataException($"Invalid session_state directory value for '{key}'.");
            return;
        }

        if (key is "WizardVideoVolume" or "WizardMusicVolume" or "MainVolume")
        {
            if (!TryGetDouble(value, out _))
                throw new InvalidDataException($"Invalid session_state volume value for '{key}'.");
            return;
        }

        if (key == "returned_from_crop_tool")
        {
            if (!TryGetBool(value, out _))
                throw new InvalidDataException("Invalid session_state returned_from_crop_tool value.");
            return;
        }

        if (SubprocessStateKeys.Contains(key))
        {
            if (value is not JsonObject subprocessState)
                throw new InvalidDataException($"Invalid session_state subprocess object for '{key}'.");
            ValidateSubprocessState(key, subprocessState);
            return;
        }

        if (key is "source" or "pid" or "written_utc" or "target" or "handoff_utc" or "selected_clip_path" or "selected_clip_start_ms" or "selected_clip_end_ms")
        {
            return;
        }

        if (key is "window_bounds" or "properties")
        {
            if (value is not JsonObject)
                throw new InvalidDataException($"Invalid session_state object for '{key}'.");
            return;
        }

        if (key == "RecentMusicPaths")
        {
            if (value is not System.Text.Json.Nodes.JsonArray)
                throw new InvalidDataException("Invalid session_state RecentMusicPaths value.");
            return;
        }

        throw new InvalidDataException($"Unknown or unvalidated session_state property: '{key}'.");
    }

    private static void ValidateSubprocessState(string key, JsonObject state)
    {
        foreach (KeyValuePair<string, JsonNode?> property in state)
        {
            if (string.IsNullOrEmpty(property.Key))
                throw new InvalidDataException($"Invalid empty property key inside subprocess object '{key}'.");
        }

        if (state.TryGetPropertyValue("schema_version", out JsonNode? version) && version != null)
        {
            if (!TryGetInt(version, out int schemaVersion) || schemaVersion < 1)
                throw new InvalidDataException($"Invalid schema_version inside subprocess object '{key}'.");
        }
        else
        {
            throw new InvalidDataException($"Missing schema_version inside subprocess object '{key}'.");
        }
    }

    private static void ValidateBoundsObject(string key, JsonObject bounds)
    {
        if (bounds.TryGetPropertyValue("X", out JsonNode? x) && x != null && !TryGetInt(x, out _))
            throw new InvalidDataException($"Invalid session_state bounds X value for '{key}'.");
        if (bounds.TryGetPropertyValue("Y", out JsonNode? y) && y != null && !TryGetInt(y, out _))
            throw new InvalidDataException($"Invalid session_state bounds Y value for '{key}'.");
        if (bounds.TryGetPropertyValue("Width", out JsonNode? width) && width != null && !TryGetDouble(width, out _))
            throw new InvalidDataException($"Invalid session_state bounds Width value for '{key}'.");
        if (bounds.TryGetPropertyValue("Height", out JsonNode? height) && height != null && !TryGetDouble(height, out _))
            throw new InvalidDataException($"Invalid session_state bounds Height value for '{key}'.");
        if (bounds.TryGetPropertyValue("WindowState", out JsonNode? windowState) && windowState != null && !TryGetInt(windowState, out _))
            throw new InvalidDataException($"Invalid session_state bounds WindowState value for '{key}'.");
    }

    private static bool TryGetString(JsonNode node, out string value)
    {
        value = string.Empty;
        try
        {
            value = node.GetValue<string>();
            return true;
        }
        catch (InvalidOperationException swallowed10)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed10);   // FAULTTIER_02 — no failure is silent.
            return false;
        }
        catch (FormatException swallowed4)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed4);   // FAULTTIER_02 — no failure is silent.
            return false;
        }
    }

    private static bool TryGetDouble(JsonNode node, out double value)
    {
        value = 0;
        try
        {
            value = node.GetValue<double>();
            return true;
        }
        catch (InvalidOperationException swallowed21)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed21);   // FAULTTIER_02 — no failure is silent.
            return false;
        }
        catch (FormatException swallowed15)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed15);   // FAULTTIER_02 — no failure is silent.
            return false;
        }
    }

    private static bool TryGetInt(JsonNode node, out int value)
    {
        value = 0;
        try
        {
            value = node.GetValue<int>();
            return true;
        }
        catch (InvalidOperationException swallowed16)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed16);   // FAULTTIER_02 — no failure is silent.
            return false;
        }
        catch (FormatException swallowed17)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed17);   // FAULTTIER_02 — no failure is silent.
            return false;
        }
    }

    private static bool TryGetBool(JsonNode node, out bool value)
    {
        value = false;
        try
        {
            value = node.GetValue<bool>();
            return true;
        }
        catch (InvalidOperationException swallowed11)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed11);   // FAULTTIER_02 — no failure is silent.
            return false;
        }
        catch (FormatException swallowed7)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed7);   // FAULTTIER_02 — no failure is silent.
            return false;
        }
    }
}
