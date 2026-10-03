// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
namespace FreeVideoStudio.Core.Infrastructure;

/// <summary>REBRAND_01 — copy legacy user data before any new state root is created.</summary>
public static class AppDataPaths
{
    internal static readonly string[] LegacyDirectoryNames = ReadLegacyNames();

    private static readonly Lazy<string> RoamingDirectory = new(() => MigrateDirectory(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyDirectoryNames));

    public static string AppDataDir => RoamingDirectory.Value;

    private static readonly Lazy<string> LocalDirectory = new(() => MigrateDirectory(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        [LegacyDirectoryNames[1], LegacyDirectoryNames[0]]));

    public static string LocalCacheDir => LocalDirectory.Value;

    private static string[] ReadLegacyNames()
    {
        using Stream stream = typeof(AppDataPaths).Assembly.GetManifestResourceStream("LegacyAppDataNames.txt")
            ?? throw new InvalidOperationException("Legacy storage identities are missing.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    internal static string MigrateDirectory(string parent, params string[] legacyNames)
    {
        string destination = Path.Combine(parent, ApplicationPaths.AppDirectoryName);
        if (Directory.Exists(destination)) return destination;

        string[] sources = legacyNames.Select(name => Path.Combine(parent, name))
            .Where(Directory.Exists).ToArray();
        string staging = destination + ".migration-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(staging);
            foreach (string source in sources) CopyDirectory(source, staging);
            try
            {
                Directory.Move(staging, destination);
            }
            catch (IOException) when (Directory.Exists(destination))
            {
                CoreLogger.Info("Paths", "Another process completed user-data migration.");
            }
            return destination;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CoreLogger.Fail("Paths", $"User-data migration failed; retaining legacy data: {ex.Message}");
            return sources.FirstOrDefault() ?? destination;
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CoreLogger.Swallowed(ex);
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            string target = Path.Combine(destination, Path.GetFileName(file));
            if (!File.Exists(target)) File.Copy(file, target, overwrite: false);
        }
        foreach (string directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
