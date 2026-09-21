// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System.Text.Json;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.Core.Ipc;

public sealed class CropConfigStore
{
    public CropConfigStore(ApplicationPaths? paths = null)
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
                StateTransferStore.MutexName,
                StateTransferStore.DefaultMutexTimeout,
                cancellationToken);

            return LoadUnlocked();
        }, cancellationToken);
    }

    public async Task SaveAsync(JsonObject config, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            Paths.EnsureWritableDirectories();

            using NamedSystemMutex guard = NamedSystemMutex.Acquire(
                StateTransferStore.MutexName,
                StateTransferStore.DefaultMutexTimeout,
                cancellationToken);

            // SILENTRESET_01 — this line used to read
            //     JsonObject payload = IsUsableConfig(config) ? Clone(config) : CropConfigDefaults.Create();
            // A caller that handed over a document this store considered unusable therefore had its
            // save REPLACED by factory defaults, silently and with a completed Task: every element
            // in the user's profile was overwritten with the shipped layout, the rotation above then
            // pushed the good file down the backup chain, and five more saves aged it out of
            // existence. Nothing anywhere reported it. A save that cannot be performed must fail
            // loudly; healing belongs in LoadUnlocked, where there is genuinely nothing to lose.
            if (!IsUsableConfig(config))
            {
                string reason = DescribeUnusable(config);
                CoreLogger.Info("CropConfig", $"Refused to save an unusable crop config ({reason}). The file on disk is unchanged.");
                throw new InvalidOperationException(
                    $"Refusing to save the crop configuration: {reason}. The existing file has not been modified.");
            }

            JsonObject payload = Clone(config);
            RotateBackupsUnlocked();
            AtomicJsonFile.WriteObject(Paths.CropCoordinatesFile, payload);
        }, cancellationToken);
    }

    private JsonObject LoadUnlocked()
    {
        try
        {
            JsonObject? config = AtomicJsonFile.ReadObject(Paths.CropCoordinatesFile);
            if (config is not null && IsUsableConfig(config))
            {
                return config;
            }
        }
        catch (JsonException swallowed7)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed7);   // FAULTTIER_02 — no failure is silent.
        }
        catch (IOException swallowed9)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed9);   // FAULTTIER_02 — no failure is silent.
        }
        catch (UnauthorizedAccessException swallowed)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed);   // FAULTTIER_02 — no failure is silent.
        }

        // Newest backup first. Do not rotate during recovery: that would replace a
        // useful backup with the damaged live file and discard the oldest backup.
        for (int i = 1; i <= 5; i++)
        {
            JsonObject? backup = AtomicJsonFile.ReadObject(BackupPath(i));
            if (backup is null || !IsUsableConfig(backup)) continue;

            AtomicJsonFile.WriteObject(Paths.CropCoordinatesFile, backup);
            CoreLogger.Info("CropConfig", $"Restored crop settings from backup {i}.");
            return backup;
        }

        JsonObject healed = CropConfigDefaults.Create();
        AtomicJsonFile.WriteObject(Paths.CropCoordinatesFile, healed);
        return Clone(healed);
    }

    /// <summary>
    /// SILENTRESET_01 — names the first failed precondition so the thrown message and the log line
    /// say WHICH one, instead of the useless "unusable".
    /// </summary>
    private static string DescribeUnusable(JsonObject config)
    {
        if (!TryGetInt(config["schema_version"], out int schemaVersion))
        {
            return "schema_version is missing or is not an integer";
        }

        if (schemaVersion < CropConfigDefaults.MinimumUsableSchemaVersion)
        {
            return $"schema_version {schemaVersion} is older than the minimum usable version {CropConfigDefaults.MinimumUsableSchemaVersion}";
        }

        if (!TryGetString(config["coordinate_space"], out string? coordinateSpace))
        {
            return "coordinate_space is missing or is not a string";
        }

        if (coordinateSpace != CropConfigDefaults.CoordinateSpace)
        {
            return $"coordinate_space is '{coordinateSpace}', expected '{CropConfigDefaults.CoordinateSpace}'";
        }

        foreach (string section in CropConfigDefaults.RequiredSections)
        {
            if (config[section] is not JsonObject)
            {
                return $"the required section '{section}' is missing or is not an object";
            }
        }

        return "one or more HUD elements have invalid crop, scale, position, or layer data";
    }

    public static bool IsUsableConfig(JsonObject config)
    {
        if (!TryGetInt(config["schema_version"], out int schemaVersion) ||
            schemaVersion < CropConfigDefaults.MinimumUsableSchemaVersion)
        {
            return false;
        }

        if (!TryGetString(config["coordinate_space"], out string? coordinateSpace) ||
            coordinateSpace != CropConfigDefaults.CoordinateSpace)
        {
            return false;
        }

        foreach (string section in CropConfigDefaults.RequiredSections)
        {
            if (config[section] is not JsonObject)
            {
                return false;
            }
        }

        // CROPFALLBACK_02 — valid JSON and section names alone do not make a renderable profile.
        // Reject damaged layer data before selecting a recovery backup or saving over good work.
        // Empty sections and explicit zero rectangles remain legal (the user can remove all HUDs).
        var crops = config["crops_1080p"]!.AsObject();
        var scales = config["scales"]!.AsObject();
        var overlays = config["overlays"]!.AsObject();
        var zOrders = config["z_orders"]!.AsObject();
        foreach (string section in CropConfigDefaults.RequiredSections)
        {
            foreach (string key in config[section]!.AsObject().Select(kvp => kvp.Key))
            {
                if (HudConfig.IsRetiredRole(key)) continue;
                if (!IsUsableRect(crops[key]) || !IsPositiveScale(scales[key]) ||
                    overlays[key] is not JsonObject position ||
                    !TryGetInt(position["x"], out _) || !TryGetInt(position["y"], out _) ||
                    !TryGetInt(zOrders[key], out _)) return false;
            }
        }

        if (config[CropConfigDefaults.SourceCropsSection] is { } sourceNode)
        {
            if (sourceNode is not JsonObject sourceCrops) return false;
            foreach (var entry in sourceCrops)
            {
                if (HudConfig.IsRetiredRole(entry.Key)) continue;
                if (!crops.ContainsKey(entry.Key) || !IsUsableRect(entry.Value)) return false;
            }
        }
        return true;
    }

    private static bool IsUsableRect(JsonNode? node)
    {
        if (node is not JsonArray rect || rect.Count < 4 ||
            !TryGetInt(rect[0], out int width) || !TryGetInt(rect[1], out int height) ||
            !TryGetInt(rect[2], out _) || !TryGetInt(rect[3], out _)) return false;
        return (width > 0 && height > 0) || (width == 0 && height == 0);
    }

    private static bool IsPositiveScale(JsonNode? node)
    {
        if (node is not JsonValue) return false;
        try { return Frac.FromString(node.ToString()) > Frac.Zero; }
        catch (System.Exception swallowed2)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed2);   // FAULTTIER_02 — no failure is silent.
            return false;
        }
    }

    private static bool TryGetInt(JsonNode? node, out int value)
    {
        value = 0;
        if (node is null)
        {
            return false;
        }

        try
        {
            value = node.GetValue<int>();
            return true;
        }
        catch (InvalidOperationException swallowed5)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed5);   // FAULTTIER_02 — no failure is silent.
            return false;
        }
        catch (FormatException swallowed6)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed6);   // FAULTTIER_02 — no failure is silent.
            return false;
        }
        catch (InvalidCastException swallowed8)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed8);   // FAULTTIER_02 — no failure is silent.
            return false;
        }
    }

    private static bool TryGetString(JsonNode? node, out string? value)
    {
        value = null;
        if (node is null)
        {
            return false;
        }

        try
        {
            value = node.GetValue<string>();
            return true;
        }
        catch (InvalidOperationException swallowed3)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed3);   // FAULTTIER_02 — no failure is silent.
            return false;
        }
        catch (InvalidCastException swallowed4)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed4);   // FAULTTIER_02 — no failure is silent.
            return false;
        }
    }

    private void RotateBackupsUnlocked()
    {
        string configPath = Paths.CropCoordinatesFile;
        string? directory = Path.GetDirectoryName(configPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new IOException($"Cannot resolve a parent directory for '{configPath}'.");
        }

        Directory.CreateDirectory(directory);

        string bak5 = BackupPath(5);
        AtomicJsonFile.TryDelete(bak5);

        for (int i = 4; i >= 1; i--)
        {
            string source = BackupPath(i);
            if (File.Exists(source))
            {
                File.Move(source, BackupPath(i + 1), overwrite: true);
            }
        }

        if (File.Exists(configPath))
        {
            string tempBackup = Path.Combine(directory, $"{Path.GetFileName(configPath)}.bak1.{Guid.NewGuid():N}.tmp");
            try
            {
                File.Copy(configPath, tempBackup, overwrite: true);
                File.Move(tempBackup, BackupPath(1), overwrite: true);
            }
            catch
            {
                AtomicJsonFile.TryDelete(tempBackup);
                throw;
            }
        }
    }

    private string BackupPath(int index)
    {
        return $"{Paths.CropCoordinatesFile}.bak{index}";
    }

    private static JsonObject Clone(JsonObject source)
    {
        return source.DeepClone().AsObject();
    }
}
