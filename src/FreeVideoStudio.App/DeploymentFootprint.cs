// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using Microsoft.Win32;

namespace FreeVideoStudio.App;

internal static class DeploymentFootprint
{
    public const string DisplayName = "Free Video Studio";
    public const string AppExeName = "FreeVideoStudio.exe";
    public const string UninstallExeName = "Uninstall.exe";
    public const string InstallerGateName = @"Global\FreeVideoStudio_InstallerGate";
    public const string ScheduledTaskName = "FreeVideoStudio";
    public const string UninstallKeyName = "Free Video Studio";
    public const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallKeyName;
    public const string AppRegistryKeyPath = @"SOFTWARE\Free Video Studio";

    /// <summary>
    /// NOSPACE_01 — folder and file names on disk never contain spaces. <see cref="DisplayName"/>
    /// ("Free Video Studio") is what the user READS — title bars, shortcuts, Apps &amp; features.
    /// Every folder uses <see cref="FreeVideoStudio.Core.Infrastructure.ApplicationPaths.AppDirectoryName"/>.
    /// Builds before this change installed to the spaced name; <see cref="LegacyInstallFolderName"/>
    /// is how the installer finds and moves them (InstallDiscovery.FindRoots).
    /// </summary>
    public const string LegacyInstallFolderName = DisplayName;

    public static readonly string InstallFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        FreeVideoStudio.Core.Infrastructure.ApplicationPaths.AppDirectoryName);

    /// <summary>NOSPACE_01 — where builds before this change were installed.</summary>
    public static readonly string LegacyInstallFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        LegacyInstallFolderName);

    public static readonly string InstallPath = Path.Combine(InstallFolder, AppExeName);
    public static readonly string UninstallPath = Path.Combine(InstallFolder, UninstallExeName);

    public static readonly string ProgramDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        FreeVideoStudio.Core.Infrastructure.ApplicationPaths.AppDirectoryName);   // NOSPACE_01

    /// <summary>NOSPACE_01 — the spaced machine folder older builds may have left behind.</summary>
    public static readonly string LegacyProgramDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        DisplayName);

    public static readonly string RoamingAppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        FreeVideoStudio.Core.Infrastructure.ApplicationPaths.AppDirectoryName);

    public static readonly string LocalAppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        FreeVideoStudio.Core.Infrastructure.ApplicationPaths.AppDirectoryName);

    /// <summary>
    /// NOSPACE_01 — installer/uninstaller scratch. A SUBFOLDER of <see cref="TempRoot"/> on purpose:
    /// TempRoot itself is the app's temp directory and holds rescued renders, and this folder is
    /// deleted wholesale after every install and uninstall.
    /// </summary>
    public static readonly string TempAppFolder = Path.Combine(TempRoot,
        FreeVideoStudio.Core.Infrastructure.ApplicationPaths.AppDirectoryName);

    /// <summary>NOSPACE_01 — the spaced scratch folder used before this change.</summary>
    public static readonly string LegacyTempAppFolder = Path.Combine(TempRoot, DisplayName);
    public static readonly string DeploymentTempRoot = Path.Combine(TempAppFolder, "Lifecycle");
    public static readonly string InstallReportPath = Path.Combine(TempRoot, "FreeVideoStudio.log");

    public static string TempRoot => Path.Combine(Path.GetTempPath(),
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FreeVideoStudio.Core.Infrastructure.ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable))
            ? "FreeVideoStudio" : "FreeVideoStudio_DEV");

    public static readonly string[] ProcessNames =
    [
        "FreeVideoStudio",
        "FreeVideoStudio.App",
        "Uninstall"
    ];

    public static readonly string[] ShortcutFileNames =
    [
        "Free Video Studio.lnk",
        "FreeVideoStudio.lnk",
        "Uninstall Free Video Studio.lnk"
    ];

    public static bool IsRunningFromInstallPath()
    {
        string? current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current))
        {
            return false;
        }

        string full = Path.GetFullPath(current);
        return string.Equals(full, Path.GetFullPath(InstallPath), StringComparison.OrdinalIgnoreCase) ||
               string.Equals(full, Path.GetFullPath(UninstallPath), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsStandaloneInstallerHost(string[] args)
    {
        return args.Length == 0 && !IsRunningFromInstallPath();
    }

    /// <summary>
    /// ISSUE_02 — directories wiped during an install/upgrade/uninstall.
    ///
    /// <paramref name="includeUserData"/> is THE "preserve my settings" switch. It must gate
    /// EVERY location that holds user state, not just ProgramData.
    ///
    /// HISTORY (the bug this parameter fixes): the old signature only gated ProgramData, while
    /// RoamingAppData, LocalAppData and %APPDATA%\FreeVideoStudio were purged
    /// unconditionally. So a user who answered "Yes, preserve my settings" during an upgrade
    /// still silently lost everything stored in those roots (butler-ribbon dismissals, the
    /// zoom-tutorial counter, and anything else that lands there). If you add a new user-state
    /// directory, it belongs INSIDE the includeUserData block — nowhere else.
    ///
    /// TempAppFolder is deliberately outside the switch: it is scratch space, never user state,
    /// and stale staging files must always go.
    /// </summary>
    public static IEnumerable<string> GetDirectoryPurgeTargets(bool includeInstallFolder, bool includeUserData)
    {
        if (includeInstallFolder)
        {
            yield return InstallFolder;
            // NOSPACE_01 — normally already moved into the upgrade backup store by the installer;
            // listed so an uninstall after an interrupted move leaves nothing behind.
            yield return LegacyInstallFolder;
        }

        yield return TempAppFolder;
        yield return LegacyTempAppFolder;   // NOSPACE_01

        if (includeUserData)
        {
            yield return ProgramDataFolder;
            yield return LegacyProgramDataFolder;   // NOSPACE_01
            yield return RoamingAppDataFolder;
            yield return LocalAppDataFolder;
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeVideoStudio");

            // REBRAND_02 — previous-brand user/machine roots (05 SYS-REBRAND). The machine root under
            // %ProgramData% is shared by every account, so only this elevated, explicit
            // "remove my data" path deletes it; the per-user sweep never does.
            foreach (string legacy in FreeVideoStudio.Core.Infrastructure.LegacyResidueSweep.LegacyStorageNames())
            {
                yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), legacy);
                yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), legacy);
                yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), legacy);
            }
        }
    }

    public static IEnumerable<string> GetVerificationTargets()
    {
        yield return InstallFolder;
        yield return LegacyInstallFolder;        // NOSPACE_01
        yield return ProgramDataFolder;
        yield return LegacyProgramDataFolder;    // NOSPACE_01
        yield return RoamingAppDataFolder;
        yield return LocalAppDataFolder;
    }

    /// <summary>
    /// Folders the uninstaller sweeps for our .lnk files.
    ///
    /// ⚠️ THE DESKTOP ENTRIES MATTER MORE THAN THEY LOOK. ⚠️
    /// <c>Environment.GetFolderPath(SpecialFolder.DesktopDirectory)</c> reads a CACHED value that
    /// goes stale in exactly the case the installer now handles: OneDrive "Backup / Manage folders"
    /// redirecting Desktop into <c>%USERPROFILE%\OneDrive\Desktop</c> (or a business variant such
    /// as <c>OneDrive - Contoso\Desktop</c>), and Group Policy redirection to a UNC share. On those
    /// machines the install writes the icon to the REDIRECTED desktop while an
    /// <c>Environment.GetFolderPath</c>-only sweep looks at the OLD one — so uninstalling left a
    /// dead shortcut behind, pointing at a deleted executable.
    ///
    /// The shell-resolved paths from <see cref="KnownFolders"/> are therefore probed FIRST, with
    /// the Environment values kept afterwards as a belt-and-braces fallback (they are also what a
    /// pre-redirection install would have used, so both must be cleaned). Duplicates are filtered.
    ///
    /// NOTE ON ELEVATION: the cleanup worker runs elevated and may be a different account, so its
    /// per-user probe can resolve to the ADMINISTRATOR's desktop. That is why the Public desktop is
    /// always included, and why the uninstaller cannot be relied upon as the only cleanup path for
    /// a redirected per-user icon. It is best-effort by nature.
    /// </summary>
    public static IEnumerable<string> GetShortcutSearchFolders()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string? candidate in EnumerateShortcutFolderCandidates())
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            string normalized;
            try { normalized = Path.GetFullPath(candidate!).TrimEnd('\\', '/'); }
            catch (System.Exception swallowed2)
            {
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed2);   // FAULTTIER_02 — no failure is silent.
                continue;
            }

            if (seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private static IEnumerable<string?> EnumerateShortcutFolderCandidates()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);

        yield return FreeVideoStudio.Core.Infrastructure.KnownFolders.GetPublicDesktop();
        yield return FreeVideoStudio.Core.Infrastructure.KnownFolders.GetDesktop();
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        yield return Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs\Startup");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\Startup");
    }

    public static (RegistryHive Hive, RegistryView View, string SubKeyPath) GetCanonicalUninstallRegistryTarget()
    {
        return (RegistryHive.LocalMachine, RegistryView.Registry64, UninstallKeyPath);
    }

    public static IEnumerable<(RegistryHive Hive, RegistryView View, string SubKeyPath)> GetUninstallRegistryPurgeTargets()
    {
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                yield return (hive, view, UninstallKeyPath);
                yield return (hive, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FreeVideoStudio");
                yield return (hive, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Free Video Studio");
                yield return (hive, view, @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\FreeVideoStudio");
                yield return (hive, view, @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Free Video Studio");
            }
        }
    }

    public static IEnumerable<(RegistryHive Hive, RegistryView View, string Path)> GetAppRegistryPurgeTargets()
    {
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                yield return (hive, view, AppRegistryKeyPath);
                yield return (hive, view, @"SOFTWARE\FreeVideoStudio");
                yield return (hive, view, @"SOFTWARE\Wow6432Node\FreeVideoStudio");
                yield return (hive, view, @"SOFTWARE\Wow6432Node\Free Video Studio");
            }
        }
    }

    public static IEnumerable<string> GetUserArtifactPatterns()
    {
        yield return Path.Combine(TempRoot, "FreeVideoStudio.log");
        yield return Path.Combine(TempAppFolder, "*");
        yield return Path.Combine(LegacyTempAppFolder, "*");   // NOSPACE_01
        yield return Path.Combine(TempRoot, "FVS_*");
        yield return Path.Combine(TempRoot, "fvs_*");
    }
}
