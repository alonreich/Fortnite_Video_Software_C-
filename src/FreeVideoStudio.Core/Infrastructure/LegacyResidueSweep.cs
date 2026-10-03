// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.Core.Infrastructure;

/// <summary>
/// REBRAND_02 — removes what the previous product brand left on disk once it is provably no
/// longer needed, so an updated machine ends up with ONLY Free Video Studio folders.
///
/// <para>
/// REBRAND_01 (<see cref="AppDataPaths"/>) and UPGRADE_04 (<see cref="UserDataUpgrade"/>) migrate
/// the data; neither deletes the copy-only sources. This sweep is the last step and runs on every
/// normal UI launch (never inside a deployment helper, never with the development override):
/// </para>
/// <list type="number">
///   <item><b>Legacy temp roots</b> (<c>%TEMP%\&lt;legacy temp names&gt;</c>) are scratch space.
///   Rescued renders (<c>*-RECOVERED-*.mp4</c>, RESCUE_01) are user work and are MOVED into the
///   current temp root first, renamed to <see cref="OutputFileNaming.MainRecoveredPrefix"/>
///   (Merger rescues keep <see cref="OutputFileNaming.MergerRecoveredPrefix"/>). The rest is deleted.</item>
///   <item><b>Legacy per-user roots</b> under %LOCALAPPDATA% and %APPDATA% are deleted only when
///   the current root exists AND every legacy file already exists at the same relative path in
///   the current root. One missing file keeps the whole legacy root for the next launch.</item>
/// </list>
///
/// <para>
/// ⚠️ SAFETY GATES — the sweep does nothing at all while any of these hold:
/// a legacy installation is still present in Program Files (a rolled-back update brings the old
/// app back, and it needs its own folders); a legacy process is running; or a user-data upgrade
/// transaction (<c>FreeVideoStudioMigration</c> journal) is not yet committed — that transaction
/// owns the legacy roots and moves them itself. The shared machine root under %ProgramData% is
/// NEVER touched here (other Windows accounts may not have migrated yet); the elevated uninstaller
/// removes it (DeploymentFootprint.GetDirectoryPurgeTargets).
/// </para>
///
/// TOTAL BY CONTRACT: never throws; every failure is logged and retried on the next launch.
/// </summary>
public static class LegacyResidueSweep
{
    /// <summary>Result of one sweep, for the log and for tests.</summary>
    public sealed record SweepResult(int RescuedRendersMoved, int LegacyRootsRemoved, int LegacyRootsKept, string? SkippedReason);

    /// <summary>REBRAND_02 — production entry point. Resolves the real Windows locations.</summary>
    public static SweepResult RunForCurrentUser()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable)))
                return new SweepResult(0, 0, 0, "development override");

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(local) || string.IsNullOrWhiteSpace(roaming))
                return new SweepResult(0, 0, 0, "known folders unavailable");

            var legacyRoots = new List<(string Legacy, string Current)>();
            foreach (string name in AppDataPaths.LegacyDirectoryNames)
            {
                legacyRoots.Add((Path.Combine(local, name), Path.Combine(local, ApplicationPaths.AppDirectoryName)));
                legacyRoots.Add((Path.Combine(roaming, name), Path.Combine(roaming, ApplicationPaths.AppDirectoryName)));
            }

            string tempParent = Path.GetTempPath();
            string[] legacyTemps = LegacyTempNames().Select(n => Path.Combine(tempParent, n)).ToArray();

            string? blocked = BlockingReason(
                [Path.Combine(local, "FreeVideoStudioMigration"), Path.Combine(roaming, "FreeVideoStudioMigration")]);

            SweepResult result = Run(legacyTemps, ApplicationPaths.CreateDefault().TempDirectory, legacyRoots, blocked);
            if (result.SkippedReason != null)
                CoreLogger.Info("Rebrand", $"REBRAND_02 — legacy residue sweep postponed: {result.SkippedReason}.");
            else if (result.RescuedRendersMoved + result.LegacyRootsRemoved + result.LegacyRootsKept > 0)
                CoreLogger.Info("Rebrand",
                    $"REBRAND_02 — legacy residue sweep: moved {result.RescuedRendersMoved} rescued render(s), removed {result.LegacyRootsRemoved} legacy folder(s), kept {result.LegacyRootsKept} for the next launch.");
            return result;
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("Rebrand", $"REBRAND_02 — legacy residue sweep failed ({ex.Message}); it will retry on the next launch.");
            return new SweepResult(0, 0, 0, "error");
        }
    }

    /// <summary>
    /// REBRAND_02 — the testable core. <paramref name="blockedReason"/> non-null means a safety
    /// gate is closed and nothing is touched.
    /// </summary>
    public static SweepResult Run(
        IEnumerable<string> legacyTempRoots,
        string currentTempRoot,
        IEnumerable<(string Legacy, string Current)> legacyUserRoots,
        string? blockedReason)
    {
        if (blockedReason != null) return new SweepResult(0, 0, 0, blockedReason);

        int moved = 0, removed = 0, kept = 0;

        foreach (string legacyTemp in legacyTempRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(legacyTemp) || SamePath(legacyTemp, currentTempRoot)) continue;
            string[]? rescues = SafeEnumerateFiles(legacyTemp, "*-RECOVERED-*.mp4");
            bool allRescued = rescues != null;
            foreach (string file in rescues ?? [])
            {
                if (MoveRescuedRender(file, currentTempRoot)) moved++;
                else allRescued = false;
            }
            if (allRescued && TryDeleteTree(legacyTemp)) removed++;
            else kept++;
        }

        foreach ((string legacy, string current) in legacyUserRoots)
        {
            if (!Directory.Exists(legacy) || SamePath(legacy, current)) continue;
            if (Directory.Exists(current) && IsFullyMirrored(legacy, current) && TryDeleteTree(legacy)) removed++;
            else kept++;
        }

        return new SweepResult(moved, removed, kept, null);
    }

    /// <summary>Previous-brand storage folder names (REBRAND_01 resource), for the uninstaller.</summary>
    public static IEnumerable<string> LegacyStorageNames() => AppDataPaths.LegacyDirectoryNames;

    /// <summary>Legacy %TEMP% folder names: the underscore display name and the compact name.</summary>
    public static IEnumerable<string> LegacyTempNames()
    {
        yield return LegacyProductIdentity.TempName;
        yield return LegacyProductIdentity.TempName + "_DEV";
        yield return LegacyProductIdentity.CompactName;
        yield return LegacyProductIdentity.DisplayName;
    }

    private static string? BlockingReason(IEnumerable<string> userMigrationStores)
    {
        foreach (string store in userMigrationStores)
        {
            if (!Directory.Exists(store)) continue;
            foreach (string transaction in Directory.EnumerateDirectories(store))
            {
                if (!File.Exists(Path.Combine(transaction, "journal.json"))) continue;
                bool committed;
                try { committed = DirectoryUpgrade.IsCommitted(transaction); }
                catch (IOException ex) { CoreLogger.Swallowed(ex); committed = false; }
                if (!committed) return "a user-data upgrade transaction is still open";
            }
        }

        foreach (Environment.SpecialFolder parent in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string root = Environment.GetFolderPath(parent);
            if (string.IsNullOrWhiteSpace(root)) continue;
            foreach (string name in new[] { LegacyProductIdentity.DisplayName, LegacyProductIdentity.CompactName })
            {
                string folder = Path.Combine(root, name);
                if (LegacyProductIdentity.ExecutableNames.Any(exe => File.Exists(Path.Combine(folder, exe))))
                    return "a previous-brand installation is still present";
            }
        }

        foreach (string exe in LegacyProductIdentity.ExecutableNames)
        {
            Process[] running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
            try
            {
                if (running.Length > 0) return "a previous-brand process is running";
            }
            finally
            {
                foreach (Process p in running) p.Dispose();
            }
        }

        return null;
    }

    private static bool MoveRescuedRender(string file, string currentTempRoot)
    {
        try
        {
            Directory.CreateDirectory(currentTempRoot);
            string name = Path.GetFileName(file);
            string renamed = name.StartsWith(OutputFileNaming.MergerRecoveredPrefix, StringComparison.OrdinalIgnoreCase)
                ? name
                : OutputFileNaming.MainRecoveredPrefix + name[(name.IndexOf("-RECOVERED-", StringComparison.OrdinalIgnoreCase) + "-RECOVERED-".Length)..];

            string stem = Path.GetFileNameWithoutExtension(renamed);
            string target = Path.Combine(currentTempRoot, renamed);
            for (int i = 2; File.Exists(target) && i < 10000; i++)
                target = Path.Combine(currentTempRoot, $"{stem}-{i}.mp4");
            if (File.Exists(target)) return false;

            File.Move(file, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CoreLogger.Fail("Rebrand", $"REBRAND_02 — a rescued render could not be moved ({ex.Message}); its folder is kept.");
            return false;
        }
    }

    internal static bool IsFullyMirrored(string legacyRoot, string currentRoot)
    {
        string[]? files = SafeEnumerateFiles(legacyRoot, "*", SearchOption.AllDirectories);
        if (files == null) return false;
        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(legacyRoot, file);
            if (!File.Exists(Path.Combine(currentRoot, relative))) return false;
        }
        return true;
    }

    /// <summary>Null means "could not be read completely" — callers then keep the folder.</summary>
    private static string[]? SafeEnumerateFiles(string root, string pattern, SearchOption option = SearchOption.TopDirectoryOnly)
    {
        try { return Directory.EnumerateFiles(root, pattern, option).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CoreLogger.Swallowed(ex);
            return null;
        }
    }

    private static bool TryDeleteTree(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            foreach (FileInfo f in info.EnumerateFiles("*", SearchOption.AllDirectories))
                if (f.Attributes.HasFlag(FileAttributes.ReadOnly)) f.Attributes &= ~FileAttributes.ReadOnly;
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CoreLogger.Fail("Rebrand", $"REBRAND_02 — a legacy folder could not be removed yet ({ex.Message}); retrying next launch.");
            return false;
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            CoreLogger.Swallowed(ex);
            return false;
        }
    }
}
