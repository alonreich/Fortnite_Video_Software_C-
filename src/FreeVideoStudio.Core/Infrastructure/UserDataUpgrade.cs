// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FreeVideoStudio.Core.Infrastructure;

/// <summary>UPGRADE_04 — user data is migrated by its owner, never by the elevated install worker.</summary>
public sealed class UserDataUpgrade
{
    private readonly List<DirectoryUpgrade> _transactions = [];
    public IReadOnlyList<DirectoryUpgrade> Transactions => _transactions;
    public const string PathMapFileName = "migration-paths.json";

    public static string LocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeVideoStudio");

    public static IEnumerable<UserUpgradeRoot> CurrentRoots()
    {
        string compact = LegacyProductIdentity.CompactName;
        string spaced = LegacyProductIdentity.DisplayName;
        yield return Root(Environment.SpecialFolder.LocalApplicationData, "Local", "FreeVideoStudio", [spaced, compact]);
        yield return Root(Environment.SpecialFolder.ApplicationData, "Roaming", "FreeVideoStudio", [compact, spaced]);
    }

    private static UserUpgradeRoot Root(Environment.SpecialFolder folder, string label, string name, string[] oldNames)
    {
        string parent = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(parent)) throw new IOException($"Windows did not supply the {folder} folder.");
        return new(label, Path.Combine(parent, name), oldNames.Select(n => Path.Combine(parent, n)).ToArray(),
            Path.Combine(parent, "FreeVideoStudioMigration", label));
    }

    public static void RecoverInterrupted(IEnumerable<UserUpgradeRoot> roots)
    {
        foreach (UserUpgradeRoot root in roots)
        {
            if (!Directory.Exists(root.Store)) continue;
            UpgradeFiles.RequirePlainPath(root.Store);
            foreach (string folder in Directory.EnumerateDirectories(root.Store))
            {
                if (!File.Exists(Path.Combine(folder, "journal.json"))) continue;
                var transaction = DirectoryUpgrade.Open(folder, root.LegacyRoots.Append(root.Destination));
                if (transaction.Phase is not ("Committed" or "Pruned" or "RolledBack")) transaction.Rollback();
                transaction.PruneBackup(DateTimeOffset.UtcNow);
            }
        }
    }

    public static UserDataUpgrade Begin(IEnumerable<UserUpgradeRoot> roots, string pathMapDestination,
        IEnumerable<UpgradePathMapping>? extraMappings = null, bool includeCurrent = true, bool preferLegacy = false,
        Action<DirectoryUpgrade>? prepared = null)
    {
        var result = new UserDataUpgrade();
        try
        {
            foreach (UserUpgradeRoot root in roots)
            {
                if (!root.LegacyRoots.Any(Directory.Exists) && !(includeCurrent && Directory.Exists(root.Destination))) continue;
                var transaction = DirectoryUpgrade.Create(root.Store, root.Destination, root.LegacyRoots, preferLegacy);
                result._transactions.Add(transaction);
                transaction.Prepare();
                prepared?.Invoke(transaction);
            }
            var mappings = result._transactions.SelectMany(x => x.Mappings).ToList();
            if (extraMappings != null) mappings.AddRange(extraMappings);
            string mapPath = Path.Combine(pathMapDestination, PathMapFileName);
            if (File.Exists(mapPath))
            {
                using var input = File.OpenRead(mapPath);
                mappings.AddRange(JsonSerializer.Deserialize(input, UpgradeJsonContext.Default.ListUpgradePathMapping) ?? []);
            }
            foreach (DirectoryUpgrade transaction in result._transactions)
            {
                foreach (string file in UpgradeFiles.Files(transaction.Candidate).ToArray())
                {
                    if (Path.GetExtension(file) is not (".json" or ".conf" or ".fvsproj")) continue;
                    try
                    {
                        if (JsonNode.Parse(File.ReadAllText(file)) is not { } json) continue;
                        if (MigrationPathResolver.Rewrite(json, mappings))
                            AtomicJsonFile.WriteText(file, json.ToJsonString());
                    }
                    catch (JsonException ex)
                    {
                        CoreLogger.Warn("Upgrade", $"Retained unreadable legacy JSON: {file}: {ex.Message}");
                    }
                }
            }
            foreach (DirectoryUpgrade transaction in result._transactions) transaction.Activate();
            Directory.CreateDirectory(pathMapDestination);
            AtomicJsonFile.WriteText(mapPath, JsonSerializer.Serialize(mappings, UpgradeJsonContext.Default.ListUpgradePathMapping));
            return result;
        }
        catch
        {
            result.Rollback();
            throw;
        }
    }

    public void Confirm()
    {
        foreach (DirectoryUpgrade transaction in _transactions) transaction.Confirm(DateTimeOffset.UtcNow);
    }

    public void Rollback()
    {
        foreach (DirectoryUpgrade transaction in Enumerable.Reverse(_transactions)) transaction.Rollback();
    }
}

public sealed record UserUpgradeRoot(string Name, string Destination, string[] LegacyRoots, string Store);

/// <summary>UPGRADE_05 — resolve moved media in older project files without modifying those files.</summary>
public static class MigrationPathResolver
{
    public static string Resolve(string value, IReadOnlyList<UpgradePathMapping> mappings)
    {
        if (!Path.IsPathFullyQualified(value)) return value;
        foreach (UpgradePathMapping mapping in mappings.OrderByDescending(x => x.Source.Length))
        {
            string source = Path.TrimEndingDirectorySeparator(mapping.Source);
            if (value.Equals(source, StringComparison.OrdinalIgnoreCase)) return mapping.Destination;
            if (value.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(mapping.Destination, value[(source.Length + 1)..]);
        }
        return value;
    }

    public static bool Rewrite(JsonNode node, IReadOnlyList<UpgradePathMapping> mappings)
    {
        bool changed = false;
        if (node is JsonObject obj)
        {
            foreach (string key in obj.Select(x => x.Key).ToArray())
            {
                if (obj[key] is JsonValue v && v.TryGetValue<string>(out string? text))
                {
                    string mapped = Resolve(text, mappings);
                    if (mapped != text) { obj[key] = mapped; changed = true; }
                }
                else if (obj[key] is { } child) changed |= Rewrite(child, mappings);
            }
        }
        else if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue v && v.TryGetValue<string>(out string? text))
                {
                    string mapped = Resolve(text, mappings);
                    if (mapped != text) { array[i] = mapped; changed = true; }
                }
                else if (array[i] is { } child) changed |= Rewrite(child, mappings);
            }
        }
        return changed;
    }

    /// <summary>
    /// MEMEFOLDER_02 / NOSPACE_01 — records one folder move made by the app itself (not by the
    /// installer), so saved projects and recovery files that name a file by its old full path keep
    /// resolving. Idempotent: an identical mapping is not added twice. Never throws.
    /// </summary>
    public static void AppendMapping(string source, string destination)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable)))
            return;
        try
        {
            string path = Path.Combine(UserDataUpgrade.LocalRoot, UserDataUpgrade.PathMapFileName);
            List<UpgradePathMapping> mappings = [];
            if (File.Exists(path))
            {
                using var input = File.OpenRead(path);
                mappings = JsonSerializer.Deserialize(input, UpgradeJsonContext.Default.ListUpgradePathMapping) ?? [];
            }
            if (mappings.Any(m => string.Equals(m.Source, source, StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals(m.Destination, destination, StringComparison.OrdinalIgnoreCase)))
                return;
            mappings.Add(new UpgradePathMapping(source, destination));
            Directory.CreateDirectory(UserDataUpgrade.LocalRoot);
            AtomicJsonFile.WriteText(path, JsonSerializer.Serialize(mappings, UpgradeJsonContext.Default.ListUpgradePathMapping));
            _cachedMappings = null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            CoreLogger.Warn("Upgrade", $"Could not record a migration path mapping: {ex.Message}");
        }
    }

    private static IReadOnlyList<UpgradePathMapping>? _cachedMappings;

    /// <summary>
    /// MEMEFOLDER_02 — resolves ONE saved path through the recorded mappings, but only when the
    /// saved path no longer exists. Used by the recovery restore paths, which read file paths
    /// straight from JSON and drop any that are missing. Never throws.
    /// </summary>
    public static string ResolveSavedFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || File.Exists(path)) return path;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable)))
            return path;
        try
        {
            if (_cachedMappings == null)
            {
                string mapPath = Path.Combine(UserDataUpgrade.LocalRoot, UserDataUpgrade.PathMapFileName);
                if (!File.Exists(mapPath)) return path;
                using var input = File.OpenRead(mapPath);
                _cachedMappings = JsonSerializer.Deserialize(input, UpgradeJsonContext.Default.ListUpgradePathMapping) ?? [];
            }
            return Resolve(path, _cachedMappings);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            CoreLogger.Warn("Upgrade", $"Could not read migration path mappings: {ex.Message}");
            return path;
        }
    }

    public static JsonObject ResolveProject(JsonObject original)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable)))
            return original;
        string path = Path.Combine(UserDataUpgrade.LocalRoot, UserDataUpgrade.PathMapFileName);
        if (!File.Exists(path)) return original;
        try
        {
            using var input = File.OpenRead(path);
            var mappings = JsonSerializer.Deserialize(input, UpgradeJsonContext.Default.ListUpgradePathMapping) ?? [];
            return ResolveProject(original, mappings);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            CoreLogger.Warn("Upgrade", $"Could not read migration path mappings: {ex.Message}");
            return original;
        }
    }

    public static JsonObject ResolveProject(JsonObject original, IReadOnlyList<UpgradePathMapping> mappings)
    {
        var copy = (JsonObject)original.DeepClone();
        foreach (string section in new[] { "source", "memes", "audio", "export", "merge" })
            if (copy[section] is { } node) RewriteProjectPaths(node, mappings);
        return copy;
    }

    private static void RewriteProjectPaths(JsonNode node, IReadOnlyList<UpgradePathMapping> mappings)
    {
        if (node is JsonObject obj)
        {
            foreach (string key in obj.Select(p => p.Key).ToArray())
            {
                if (key is "path" or "music_path" or "voice_over_path" or "output_directory" or "Path" or "FilePath")
                {
                    if (obj[key] is JsonValue v && v.TryGetValue<string>(out string? value)) obj[key] = Resolve(value, mappings);
                }
                else if (key == "FilePaths" && obj[key] is JsonArray paths)
                {
                    for (int i = 0; i < paths.Count; i++)
                        if (paths[i] is JsonValue v && v.TryGetValue<string>(out string? value)) paths[i] = Resolve(value, mappings);
                }
                else if (obj[key] is { } child) RewriteProjectPaths(child, mappings);
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array) if (child != null) RewriteProjectPaths(child, mappings);
    }
}
