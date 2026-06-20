using System.Text.Json;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;

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

            JsonObject payload = IsUsableConfig(config) ? Clone(config) : CropConfigDefaults.Create();
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
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }

        JsonObject healed = CropConfigDefaults.Create();
        RotateBackupsUnlocked();
        AtomicJsonFile.WriteObject(Paths.CropCoordinatesFile, healed);
        return Clone(healed);
    }

    private static bool IsUsableConfig(JsonObject config)
    {
        if (!TryGetInt(config["schema_version"], out int schemaVersion) ||
            schemaVersion < CropConfigDefaults.SchemaVersion)
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

        return true;
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
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
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
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
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
