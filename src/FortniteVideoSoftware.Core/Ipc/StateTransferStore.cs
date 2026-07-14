using System.Text.Json;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Ipc;

public sealed class StateTransferStore
{
    public const string MutexName = @"Global\FvsStateTransferMutex";
    public const int SchemaVersion = 1;
    public static readonly TimeSpan DefaultMutexTimeout = TimeSpan.FromSeconds(15);
    private static readonly string[] BoundsKeys =
    [
        "MainWindowBounds",
        "VideoMergerBounds",
        "CropToolBounds",
        "GranularBounds",
        "MusicWizardBounds",
        "SettingsBounds",
        "VoiceOverWindowBounds"
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

            using NamedSystemMutex guard = NamedSystemMutex.Acquire(
                MutexName,
                DefaultMutexTimeout,
                cancellationToken);

            return LoadUnlocked();
        }, cancellationToken).ConfigureAwait(false);
    }

    public JsonObject LoadSync(CancellationToken cancellationToken = default)
    {
        try
        {
            Paths.EnsureWritableDirectories();
            using NamedSystemMutex guard = NamedSystemMutex.Acquire(
                MutexName,
                DefaultMutexTimeout,
                cancellationToken);

            return LoadUnlocked();
        }
        catch
        {
            return new JsonObject();
        }
    }

    public async Task SaveAsync(JsonObject state, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            Paths.EnsureWritableDirectories();

            using NamedSystemMutex guard = NamedSystemMutex.Acquire(
                MutexName,
                DefaultMutexTimeout,
                cancellationToken);

            JsonObject payload = Clone(state);
            ValidateKnownProperties(payload);
            payload["schema_version"] = SchemaVersion;
            AtomicJsonFile.WriteObject(Paths.SessionStateFile, payload);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdatePropertiesAsync(JsonObject updates, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            Paths.EnsureWritableDirectories();

            using NamedSystemMutex guard = NamedSystemMutex.Acquire(
                MutexName,
                DefaultMutexTimeout,
                cancellationToken);

            JsonObject current = LoadUnlocked();
            foreach (KeyValuePair<string, JsonNode?> property in updates)
            {
                ValidateKnownProperty(property.Key, property.Value);
                current[property.Key] = property.Value?.DeepClone();
            }

            current["schema_version"] = SchemaVersion;
            AtomicJsonFile.WriteObject(Paths.SessionStateFile, current);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronous update for use in Closing/Closed event handlers where
    /// async-over-sync would deadlock the UI thread. Performs I/O directly
    /// on the calling thread without Task.Run + GetAwaiter().GetResult().
    /// </summary>
    public void UpdatePropertiesSync(JsonObject updates, CancellationToken cancellationToken = default)
    {
        JsonObject clonedUpdates = Clone(updates);
        Task.Run(() =>
        {
            try
            {
                Paths.EnsureWritableDirectories();

                using NamedSystemMutex guard = NamedSystemMutex.Acquire(
                    MutexName,
                    DefaultMutexTimeout,
                    cancellationToken);

                JsonObject current = LoadUnlocked();
                foreach (KeyValuePair<string, JsonNode?> property in clonedUpdates)
                {
                    ValidateKnownProperty(property.Key, property.Value);
                    current[property.Key] = property.Value?.DeepClone();
                }

                current["schema_version"] = SchemaVersion;
                AtomicJsonFile.WriteObject(Paths.SessionStateFile, current);
            }
            catch { }
        }, cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            Paths.EnsureWritableDirectories();

            using NamedSystemMutex guard = NamedSystemMutex.Acquire(
                MutexName,
                DefaultMutexTimeout,
                cancellationToken);

            AtomicJsonFile.TryDelete(Paths.SessionStateFile);
        }, cancellationToken).ConfigureAwait(false);
    }

    private JsonObject LoadUnlocked()
    {
        try
        {
            JsonObject state = AtomicJsonFile.ReadObject(Paths.SessionStateFile) ?? new JsonObject();
            ValidateKnownProperties(state);
            return state;
        }
        catch (JsonException)
        {
            QuarantineCorruptedSessionFile();
            return new JsonObject();
        }
        catch (InvalidDataException)
        {
            QuarantineCorruptedSessionFile();
            return new JsonObject();
        }
        catch (IOException)
        {
            QuarantineCorruptedSessionFile();
            return new JsonObject();
        }
        catch (UnauthorizedAccessException)
        {
            return new JsonObject();
        }
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

    private static void ValidateKnownProperties(JsonObject state)
    {
        foreach (KeyValuePair<string, JsonNode?> property in state)
        {
            ValidateKnownProperty(property.Key, property.Value);
        }
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
            if (value is not JsonObject)
                throw new InvalidDataException($"Invalid session_state subprocess object for '{key}'.");
            return;
        }

        if (key is "source" or "pid" or "written_utc")
        {
            return;
        }

        throw new InvalidDataException($"Unknown or unvalidated session_state property: '{key}'.");
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
