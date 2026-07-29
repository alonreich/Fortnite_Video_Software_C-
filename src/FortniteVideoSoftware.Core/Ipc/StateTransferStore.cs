using System.Text.Json;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Ipc;

public sealed class StateTransferStore
{
    public const string MutexName = @"Global\FvsStateTransferMutex";
    public const int SchemaVersion = 1;
    public static readonly TimeSpan DefaultMutexTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// ISSUE_10 — the budget for saves that happen on the UI thread (window Closing handlers).
    ///
    /// The 15s default is right for a background write that must not lose data, but catastrophic
    /// on the interface thread: three processes share this mutex, so closing a window could hang
    /// the app for a quarter of a minute with a "Not Responding" title bar. Window bounds are a
    /// convenience, not data worth freezing the app for — if the lock is genuinely contended for
    /// two whole seconds, skip the save and let the position be slightly stale.
    /// </summary>
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
        "PreviewMonitorWindowBounds"
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

    public async Task<JsonObject> LoadAsync(CancellationToken cancellationToken = default)
    {
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
            catch (FortniteVideoSoftware.Core.Infrastructure.LockException)
            {
                return new JsonObject();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <param name="mutexTimeout">
    /// ISSUE_10 — how long to wait for the cross-process lock. Pass
    /// <see cref="InteractiveMutexTimeout"/> when calling from the UI thread.
    /// </param>
    public JsonObject LoadSync(CancellationToken cancellationToken = default, TimeSpan? mutexTimeout = null)
    {
        try
        {
            Paths.EnsureWritableDirectories();
            using NamedSystemMutex guard = NamedSystemMutex.Acquire(
                MutexName,
                mutexTimeout ?? DefaultMutexTimeout,
                cancellationToken);

            return LoadUnlocked();
        }
        catch
        {
            return new JsonObject();
        }
    }

    /// <summary>
    /// ISSUE_09 — writes the supplied state.
    ///
    /// WHAT WAS WRONG: this method only caught <c>LockException</c>. Validation throws
    /// <c>InvalidDataException</c> for an unrecognised key, which escaped as a faulted Task — some
    /// callers `await` it inside a broad `try {} catch {}` and some do not, so depending on the
    /// call site the write was either swallowed or blew up somewhere unrelated. Either way the
    /// user's change appeared to save and was gone at next launch.
    ///
    /// Now: unusable entries are DROPPED (and logged) and everything valid is still written, so a
    /// single bad key can no longer cost the user the rest of their settings. A genuine I/O failure
    /// is logged loudly instead of vanishing.
    /// </summary>
    public async Task SaveAsync(JsonObject state, CancellationToken cancellationToken = default)
    {
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
            catch (FortniteVideoSoftware.Core.Infrastructure.LockException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                CoreLogger.Fail("SessionState", $"Could not save session state: {ex.Message}");
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// ISSUE_09 — merges the supplied properties into the stored state. See
    /// <see cref="SaveAsync"/> for why unrecognised keys are dropped rather than thrown.
    /// </summary>
    public async Task UpdatePropertiesAsync(JsonObject updates, CancellationToken cancellationToken = default)
    {
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
            catch (FortniteVideoSoftware.Core.Infrastructure.LockException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                CoreLogger.Fail("SessionState", $"Could not update session state: {ex.Message}");
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronous update for use in Closing/Closed event handlers where
    /// async-over-sync would deadlock the UI thread. Performs I/O directly
    /// on the calling thread without Task.Run + GetAwaiter().GetResult().
    ///
    /// ISSUE_09 — the bare `catch { }` here meant a genuinely failed save looked exactly like a
    /// successful one, and this is the variant used on window CLOSE, i.e. the one that persists
    /// window bounds and folder preferences. A failure is now logged so it is at least
    /// diagnosable, and bad keys are dropped rather than aborting the whole write.
    /// </summary>
    /// <param name="mutexTimeout">
    /// ISSUE_10 — how long to wait for the cross-process lock before giving up. Callers on the UI
    /// thread must pass <see cref="InteractiveMutexTimeout"/>; the 15s default would otherwise
    /// freeze the window that is trying to close.
    /// </param>
    public void UpdatePropertiesSync(JsonObject updates, CancellationToken cancellationToken = default, TimeSpan? mutexTimeout = null)
    {
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

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
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
            catch (FortniteVideoSoftware.Core.Infrastructure.LockException)
            {
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// ===== ISSUE_08 — one odd entry must not wipe every remembered setting =====================
    ///
    /// WHAT WAS WRONG: this method called <c>ValidateKnownProperties</c>, which THROWS
    /// <c>InvalidDataException</c> for any key it does not recognise. That exception was caught
    /// below as "the file is corrupt", the whole file was renamed to <c>.corrupted</c>, and an
    /// empty object was returned. So a single unexpected entry — left by an older build, written
    /// by a newer build, or a half-finished write — silently erased EVERY window position and size
    /// plus every remembered upload/output/music folder. From the user's side the app had simply
    /// forgotten everything, with no explanation and nothing they could do about it.
    ///
    /// NOW: unrecognised or malformed entries are DROPPED individually (and logged), and every
    /// entry the app does understand is kept. Quarantine is reserved for what it was actually
    /// meant for — a file that is not parseable JSON at all.
    /// </summary>
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
        catch (UnauthorizedAccessException)
        {
            return new JsonObject();
        }
    }

    /// <summary>
    /// ISSUE_08/ISSUE_09 — returns a copy of <paramref name="state"/> containing only the entries
    /// that pass validation. Anything unrecognised or malformed is dropped and logged rather than
    /// aborting the whole operation.
    /// </summary>
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

    /// <summary>
    /// ISSUE_09 — merges <paramref name="updates"/> into <paramref name="current"/>, skipping any
    /// entry that fails validation instead of throwing and losing the entire write.
    /// </summary>
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

    /// <summary>
    /// Validates a single property without throwing. Returns false when the entry must be dropped.
    /// The accepted value is a detached deep clone, so callers can never alias the caller's tree.
    /// </summary>
    private static bool TryAcceptProperty(string key, JsonNode? value, out JsonNode? accepted)
    {
        accepted = null;

        try
        {
            ValidateKnownProperty(key, value);
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (Exception)
        {
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static JsonObject Clone(JsonObject source)
    {
        return source.DeepClone().AsObject();
    }


    /// <summary>
    /// Validates a single entry, throwing <see cref="InvalidDataException"/> when it is unusable.
    /// Only ever called via <c>TryAcceptProperty</c>, which converts the throw into a drop.
    /// </summary>
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

        if (key is "source" or "pid" or "written_utc")
        {
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
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (FormatException)
        {
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
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (FormatException)
        {
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
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (FormatException)
        {
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
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
