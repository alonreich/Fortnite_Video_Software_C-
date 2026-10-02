
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeVideoStudio.Core.Infrastructure;

/// <summary>UPGRADE_03 — durable, restartable directory replacement. Backups survive confirmation.</summary>
public sealed class DirectoryUpgrade
{
    public const int RetentionDays = 30;
    private readonly string _directory;
    private readonly UpgradeJournal _journal;
    public string DirectoryPath => _directory;
    public string Candidate => Path.Combine(_directory, "candidate");
    public string Destination => _journal.Destination;
    public string Phase => _journal.Phase;
    public IReadOnlyList<UpgradePathMapping> Mappings => _journal.Mappings;
    public DateTimeOffset? ConfirmedAt => _journal.ConfirmedAt;
    public IEnumerable<string> Originals => _journal.Originals.Select(x => x.Path);

    private DirectoryUpgrade(string directory, UpgradeJournal journal)
    {
        _directory = UpgradeFiles.FullPath(directory);
        _journal = journal;
    }

    public static DirectoryUpgrade Create(string store, string destination, IEnumerable<string> legacyRoots, bool preferLegacy = false)
    {
        UpgradeFiles.RequirePlainPath(store);
        UpgradeFiles.RequirePlainPath(destination);
        string target = UpgradeFiles.FullPath(destination);
        var legacy = legacyRoots.Select(UpgradeFiles.FullPath).ToArray();
        string[] originals = (preferLegacy ? legacy.Append(target) : new[] { target }.Concat(legacy))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToArray();
        foreach (string source in originals)
        {
            UpgradeFiles.RequirePlainPath(source);
            if (UpgradeFiles.IsWithin(store, source) || UpgradeFiles.IsWithin(source, store) ||
                string.Equals(UpgradeFiles.FullPath(store), source, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The backup store must be separate from every migrated directory.");
            if (!string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(store), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Migration requires a backup store on the same volume as the source.");
            if (originals.Any(other => UpgradeFiles.IsWithin(source, other)))
                throw new IOException("Overlapping migration roots are not supported.");
        }
        if (!string.Equals(Path.GetPathRoot(target), Path.GetPathRoot(store), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Migration requires staging on the destination volume.");
        string directory = Path.Combine(store, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var journal = new UpgradeJournal
        {
            Destination = target,
            Originals = originals.Select((p, i) => new UpgradeOriginal { Path = p, Backup = "previous-" + i }).ToList()
        };
        var transaction = new DirectoryUpgrade(directory, journal);
        transaction.Persist();
        return transaction;
    }

    public static DirectoryUpgrade Open(string directory, IEnumerable<string> allowedRoots)
    {
        UpgradeFiles.RequirePlainPath(directory);
        using var input = File.OpenRead(Path.Combine(directory, "journal.json"));
        var journal = JsonSerializer.Deserialize(input, UpgradeJsonContext.Default.UpgradeJournal)
            ?? throw new IOException("Upgrade journal is empty.");
        var allowed = new HashSet<string>(allowedRoots.Select(UpgradeFiles.FullPath), StringComparer.OrdinalIgnoreCase);
        if (journal.Version != 1 || !allowed.Contains(UpgradeFiles.FullPath(journal.Destination)) ||
            journal.Originals.Any(x => !allowed.Contains(UpgradeFiles.FullPath(x.Path))) ||
            journal.Originals.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Originals.Count)
            throw new IOException("Upgrade journal contains an unexpected installation path.");
        for (int i = 0; i < journal.Originals.Count; i++)
        {
            if (journal.Originals[i].Backup != "previous-" + i)
                throw new IOException("Upgrade journal contains an invalid backup path.");
            UpgradeFiles.RequirePlainPath(journal.Originals[i].Path);
        }
        UpgradeFiles.RequirePlainPath(journal.Destination);
        return new DirectoryUpgrade(directory, journal);
    }

    /// <summary>Existing destination wins; conflicting legacy files are permanently preserved.</summary>
    public void Prepare(Action<string>? completeCandidate = null, Func<string, bool>? include = null)
    {
        if (Phase != "Preparing") throw new InvalidOperationException("Upgrade was already prepared.");
        Directory.CreateDirectory(Candidate);
        for (int index = 0; index < _journal.Originals.Count; index++)
        {
            string source = _journal.Originals[index].Path;
            if (!string.Equals(source, Destination, StringComparison.OrdinalIgnoreCase))
                _journal.Mappings.Add(new(source, Destination));
            foreach (string file in UpgradeFiles.Files(source))
            {
                string relative = Path.GetRelativePath(source, file);
                if (include != null && !include(relative)) continue;
                string target = Path.Combine(Candidate, relative);
                if (File.Exists(target))
                {
                    if (UpgradeFiles.Hash(file) == UpgradeFiles.Hash(target)) continue;
                    string conflict = Path.Combine("MigrationConflicts", Path.GetFileName(_directory), index.ToString(), relative);
                    target = Path.Combine(Candidate, conflict);
                    _journal.Mappings.Add(new(file, Path.Combine(Destination, conflict)));
                }
                UpgradeFiles.CopyVerified(file, target);
            }
        }
        completeCandidate?.Invoke(Candidate);
        _journal.Phase = "Prepared";
        Persist();
    }

    public void Activate(Action<string>? checkpoint = null)
    {
        if (Phase != "Prepared") throw new InvalidOperationException("Upgrade is not prepared.");
        _journal.Phase = "Switching";
        Persist();
        foreach (UpgradeOriginal original in _journal.Originals)
        {
            UpgradeFiles.RequirePlainPath(original.Path);
            Directory.Move(original.Path, Path.Combine(_directory, original.Backup));
            checkpoint?.Invoke("backup:" + original.Backup);
        }
        UpgradeFiles.RequirePlainPath(Destination);
        Directory.CreateDirectory(Path.GetDirectoryName(Destination)!);
        Directory.Move(Candidate, Destination);
        checkpoint?.Invoke("activated");
        _journal.Phase = "AwaitingConfirmation";
        Persist();
    }

    public void Confirm(DateTimeOffset now)
    {
        if (Phase == "Committed") return;
        if (Phase != "AwaitingConfirmation") throw new InvalidOperationException("Upgrade has not been activated.");
        _journal.ConfirmedAt = now;
        _journal.Phase = "Committed";
        Persist();
    }

    public void Rollback()
    {
        if (Phase is "RolledBack" or "Committed") return;
        if (Phase is "Switching" or "AwaitingConfirmation" or "RollingBack")
        {
            if (!Directory.Exists(Candidate) && Directory.Exists(Destination) &&
                !Directory.Exists(Path.Combine(_directory, "failed-install")))
            {
                UpgradeFiles.RequirePlainPath(Destination);
                Directory.Move(Destination, Path.Combine(_directory, "failed-install"));
            }
            _journal.Phase = "RollingBack";
            Persist();
            foreach (UpgradeOriginal original in _journal.Originals)
            {
                string backup = Path.Combine(_directory, original.Backup);
                if (!Directory.Exists(backup)) continue;
                if (Directory.Exists(original.Path))
                    throw new IOException($"Rollback stopped to preserve files created at {original.Path}.");
                UpgradeFiles.RequirePlainPath(backup);
                UpgradeFiles.RequirePlainPath(original.Path);
                Directory.Move(backup, original.Path);
            }
        }
        _journal.Phase = "RolledBack";
        Persist();
    }

    public bool PruneBackup(DateTimeOffset now)
    {
        if (Phase != "Committed" || ConfirmedAt is not { } at || now - at < TimeSpan.FromDays(RetentionDays)) return false;
        foreach (UpgradeOriginal original in _journal.Originals)
            UpgradeFiles.DeleteTree(Path.Combine(_directory, original.Backup), _directory);
        _journal.Phase = "Pruned";
        Persist();
        return true;
    }

    public static bool IsCommitted(string directory)
    {
        var journal = AtomicJsonFile.ReadObject(Path.Combine(directory, "journal.json"))
            ?? throw new IOException("An installation recovery record is unreadable.");
        return journal["Phase"]?.GetValue<string>() is "Committed" or "Pruned";
    }

    private void Persist()
    {
        UpgradeFiles.RequirePlainPath(_directory);
        string path = Path.Combine(_directory, "journal.json");
        string temporary = path + ".tmp";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(output, _journal, UpgradeJsonContext.Default.UpgradeJournal);
            output.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}

public sealed class UpgradeJournal
{
    public int Version { get; set; } = 1;
    public string Destination { get; set; } = "";
    public string Phase { get; set; } = "Preparing";
    public DateTimeOffset? ConfirmedAt { get; set; }
    public List<UpgradeOriginal> Originals { get; set; } = [];
    public List<UpgradePathMapping> Mappings { get; set; } = [];
}

public sealed class UpgradeOriginal
{
    public string Path { get; set; } = "";
    public string Backup { get; set; } = "";
}

public sealed record UpgradePathMapping(string Source, string Destination);

[JsonSerializable(typeof(UpgradeJournal))]
[JsonSerializable(typeof(List<UpgradePathMapping>))]
internal partial class UpgradeJsonContext : JsonSerializerContext;
