
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FreeVideoStudio.Core.Infrastructure;

/// <summary>
/// SYS-PAYLOADSPLIT — content fingerprint of files a compact installer reuses.
/// Computed at packaging time; update checks read the stored fingerprint. Before selecting
/// a compact package, the updater separately verifies installed bytes against their manifest.
/// Application executables and manifests are excluded; runtime binaries and starter assets
/// are included. The fingerprint selects a download and never replaces signature verification.
/// </summary>
public sealed record RuntimePayloadManifest(string Fingerprint, int FileCount, long TotalBytes)
{
    /// <summary>The file the build writes into the payload and the installer lays down beside the app.</summary>
    public const string FileName = "runtime.manifest.json";

    /// <summary>
    /// SYS-PAYLOADSPLIT — fingerprints a folder of runtime binaries.
    ///
    /// <para>Ordinal-sorted by relative path so the answer does not depend on the order the
    /// filesystem happened to enumerate, which differs between machines and between runs.</para>
    /// </summary>
    public static RuntimePayloadManifest FromFolder(string folder, IEnumerable<string>? extensions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var allowed = extensions == null ? null : new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);

        var entries = new List<string>();
        long total = 0;

        if (Directory.Exists(folder))
        {
            foreach (string path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (allowed != null && !allowed.Contains(Path.GetExtension(path))) continue;

                var info = new FileInfo(path);
                string relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
                if (relative.Equals(InstallPayload.ExecutableName, StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals(InstallPayload.ManifestName, StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals(FileName, StringComparison.OrdinalIgnoreCase)) continue;
                entries.Add($"{relative}:{info.Length}:{UpgradeFiles.Hash(path)}");
                total += info.Length;
            }
        }

        entries.Sort(StringComparer.Ordinal);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries)));
        return new RuntimePayloadManifest(Convert.ToHexString(hash, 0, 12), entries.Count, total);
    }

    public JsonObject ToJson() => new()
    {
        ["fingerprint"] = Fingerprint,
        ["file_count"] = FileCount,
        ["total_bytes"] = TotalBytes,
    };

    public static RuntimePayloadManifest? FromJson(JsonObject? root)
    {
        if (root?["fingerprint"] is null) return null;
        string fp = root["fingerprint"]!.GetValue<string>();
        if (string.IsNullOrWhiteSpace(fp)) return null;

        int count = root["file_count"] is JsonValue c && c.TryGetValue(out int ci) ? ci : 0;
        long bytes = root["total_bytes"] is JsonValue b && b.TryGetValue(out long bi) ? bi : 0;
        return new RuntimePayloadManifest(fp, count, bytes);
    }

    /// <summary>Reads the manifest the installer laid down beside the app, or null when there is none.</summary>
    public static RuntimePayloadManifest? Read(string folder)
    {
        try
        {
            string path = Path.Combine(folder, FileName);
            return File.Exists(path) ? FromJson(AtomicJsonFile.ReadObject(path)) : null;
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return null;
        }
    }

    /// <summary>Writes the manifest into <paramref name="folder"/>.</summary>
    public void Write(string folder)
    {
        Directory.CreateDirectory(folder);
        AtomicJsonFile.WriteObject(Path.Combine(folder, FileName), ToJson());
    }

    /// <summary>Human-readable size, for the update prompt that tells the user what they are about to spend.</summary>
    public static string FormatBytes(long bytes)
        => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):0.0} GB"
         : bytes >= 1024L * 1024        ? $"{bytes / (1024.0 * 1024):0} MB"
         : bytes >= 1024L               ? $"{bytes / 1024.0:0} KB"
         :                                $"{bytes} bytes";
}
