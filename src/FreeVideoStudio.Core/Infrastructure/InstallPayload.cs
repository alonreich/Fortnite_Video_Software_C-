// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeVideoStudio.Core.Infrastructure;

/// <summary>UPGRADE_06 — validate every staged payload byte before replacing an installed file.</summary>
public static class InstallPayload
{
    public const string ManifestName = "install.manifest.json";
    public const string ExecutableName = "FreeVideoStudio.exe";

    public static void WriteManifest(string directory)
    {
        var manifest = new InstallFileManifest
        {
            Files = UpgradeFiles.Files(directory).Where(p => Path.GetFileName(p) != ManifestName)
                .Select(p => new InstallFile(Path.GetRelativePath(directory, p).Replace('\\', '/'), new FileInfo(p).Length, UpgradeFiles.Hash(p)))
                .OrderBy(x => x.Path, StringComparer.Ordinal).ToList()
        };
        AtomicJsonFile.WriteText(Path.Combine(directory, ManifestName),
            JsonSerializer.Serialize(manifest, InstallManifestContext.Default.InstallFileManifest));
    }

    public static InstallFileManifest ReadManifest(string directory)
    {
        using var input = File.OpenRead(Path.Combine(directory, ManifestName));
        var manifest = JsonSerializer.Deserialize(input, InstallManifestContext.Default.InstallFileManifest)
            ?? throw new IOException("Installation manifest is missing.");
        if (manifest.Product != "FreeVideoStudio" || manifest.Version != 1 ||
            !manifest.Files.Any(x => x.Path == ExecutableName) ||
            manifest.Files.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count)
            throw new IOException("Installation manifest has an invalid product identity or duplicate files.");
        return manifest;
    }

    public static void Verify(string directory)
    {
        foreach (InstallFile entry in ReadManifest(directory).Files)
        {
            string file = EntryPath(directory, entry.Path);
            if (!File.Exists(file) || new FileInfo(file).Length != entry.Length || UpgradeFiles.Hash(file) != entry.Sha256)
                throw new IOException($"Installation payload verification failed: {entry.Path}");
        }
    }

    public static void Extract(Stream embeddedZip, string destination, string? reuseRoot = null)
    {
        UpgradeFiles.RequirePlainPath(destination);
        Directory.CreateDirectory(destination);
        using var archive = new ZipArchive(embeddedZip, ZipArchiveMode.Read, leaveOpen: true);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            string path = EntryPath(destination, entry.FullName);
            if (!seen.Add(path)) throw new IOException("Duplicate payload archive entry.");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new IOException("Linked payload entries are not supported.");
            UpgradeFiles.RequirePlainPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open();
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
        foreach (InstallFile entry in ReadManifest(destination).Files)
        {
            string target = EntryPath(destination, entry.Path);
            if (File.Exists(target)) continue;
            if (reuseRoot == null) throw new IOException("A full installer is required for this installation.");
            string source = EntryPath(reuseRoot, entry.Path);
            if (!File.Exists(source) || new FileInfo(source).Length != entry.Length || HashMismatch(source, entry))
                throw new IOException("Installed runtime files have changed. Download the full installer.");
            UpgradeFiles.CopyVerified(source, target);
        }
        Verify(destination);
    }

    private static bool HashMismatch(string path, InstallFile entry) => UpgradeFiles.Hash(path) != entry.Sha256;

    internal static string EntryPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || relative.Contains('\\') ||
            relative.StartsWith('/') || relative.Split('/').Any(x => x is "" or "." or ".."))
            throw new IOException("Unsafe installation archive path.");
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!UpgradeFiles.IsWithin(path, root)) throw new IOException("Installation file escaped its staging directory.");
        return path;
    }
}

public sealed class InstallFileManifest
{
    public int Version { get; set; } = 1;
    public string Product { get; set; } = "FreeVideoStudio";
    public List<InstallFile> Files { get; set; } = [];
}
public sealed record InstallFile(string Path, long Length, string Sha256);

[JsonSerializable(typeof(InstallFileManifest))]
internal partial class InstallManifestContext : JsonSerializerContext;
