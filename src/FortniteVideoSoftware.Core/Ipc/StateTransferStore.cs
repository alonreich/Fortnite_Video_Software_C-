using System.Text.Json;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Ipc;

public sealed class StateTransferStore
{
    public const string MutexName = @"Global\FvsStateTransferMutex";
    public static readonly TimeSpan DefaultMutexTimeout = TimeSpan.FromSeconds(15);

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
        }, cancellationToken);
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

            AtomicJsonFile.WriteObject(Paths.SessionStateFile, Clone(state));
        }, cancellationToken);
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
                current[property.Key] = property.Value?.DeepClone();
            }

            AtomicJsonFile.WriteObject(Paths.SessionStateFile, current);
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
        }, cancellationToken);
    }

    private JsonObject LoadUnlocked()
    {
        try
        {
            return AtomicJsonFile.ReadObject(Paths.SessionStateFile) ?? new JsonObject();
        }
        catch (JsonException)
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
}
