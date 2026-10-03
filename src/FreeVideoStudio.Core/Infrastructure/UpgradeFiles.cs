// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System.Security.Cryptography;

namespace FreeVideoStudio.Core.Infrastructure;

/// <summary>UPGRADE_02 — verified copies and bounded paths; never follow a link during cleanup.</summary>
public static class UpgradeFiles
{
    public static string FullPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool IsWithin(string path, string directory) =>
        FullPath(path).StartsWith(FullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void RequirePlainPath(string path)
    {
        string? current = FullPath(path);
        while (current != null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Migration will not follow a linked folder or file: {current}");
            current = Path.GetDirectoryName(current);
        }
    }

    public static IEnumerable<string> Files(string root)
    {
        RequirePlainPath(root);
        foreach (string path in Directory.EnumerateFileSystemEntries(root))
        {
            RequirePlainPath(path);
            if (Directory.Exists(path))
            {
                foreach (string file in Files(path)) yield return file;
            }
            else yield return path;
        }
    }

    public static string Hash(string path)
    {
        RequirePlainPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static void CopyVerified(string source, string destination, bool overwrite = false)
    {
        RequirePlainPath(source);
        RequirePlainPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using (var output = new FileStream(destination, overwrite ? FileMode.Create : FileMode.CreateNew,
                   FileAccess.Write, FileShare.None))
        {
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
        input.Position = 0;
        if (!Convert.ToHexString(SHA256.HashData(input)).Equals(Hash(destination), StringComparison.Ordinal))
            throw new IOException($"Migration copy verification failed: {source}");
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
    }

    public static void DeleteTree(string path, string permittedParent)
    {
        if (!IsWithin(path, permittedParent)) throw new IOException("Cleanup escaped its migration directory.");
        if (!Directory.Exists(path)) return;
        _ = Files(path).ToArray();
        Directory.Delete(path, recursive: true);
    }
}
