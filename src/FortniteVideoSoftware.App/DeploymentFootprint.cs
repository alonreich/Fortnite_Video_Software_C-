using Microsoft.Win32;

namespace FortniteVideoSoftware.App;

internal static class DeploymentFootprint
{
    public const string DisplayName = "Fortnite Video Software";
    public const string AppExeName = "FortniteVideoSoftware.exe";
    public const string UninstallExeName = "Uninstall.exe";
    public const string InstallerMutexName = @"Global\FortniteVideoSoftware_Installer";
    public const string InstallerGateName = @"Global\FortniteVideoSoftware_InstallerGate";
    public const string ScheduledTaskName = "FortniteVideoSoftware";
    public const string UninstallKeyName = "Fortnite Video Software";
    public const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallKeyName;
    public const string AppRegistryKeyPath = @"SOFTWARE\Fortnite Video Software";

    public static readonly string InstallFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        DisplayName);

    public static readonly string InstallPath = Path.Combine(InstallFolder, AppExeName);
    public static readonly string UninstallPath = Path.Combine(InstallFolder, UninstallExeName);

    public static readonly string ProgramDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        DisplayName);

    public static readonly string RoamingAppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        DisplayName);

    public static readonly string LocalAppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        DisplayName);

    public static readonly string TempAppFolder = Path.Combine(TempRoot, DisplayName);
    public static readonly string DeploymentTempRoot = Path.Combine(TempAppFolder, "Lifecycle");
    public static readonly string InstallReportPath = Path.Combine(TempRoot, "Fortnite_Video_Software.log");

    public static string TempRoot => FortniteVideoSoftware.Core.Infrastructure.ApplicationPaths.CreateDefault().TempDirectory;

    public static readonly string[] ProcessNames =
    [
        "FortniteVideoSoftware",
        "FortniteVideoSoftware.App",
        "Uninstall"
    ];

    public static readonly string[] ShortcutFileNames =
    [
        "Fortnite Video Software.lnk",
        "FortniteVideoSoftware.lnk",
        "Uninstall Fortnite Video Software.lnk"
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
    /// RoamingAppData, LocalAppData and %APPDATA%\FortniteVideoSoftware were purged
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
        }

        yield return TempAppFolder;

        if (includeUserData)
        {
            yield return ProgramDataFolder;
            yield return RoamingAppDataFolder;
            yield return LocalAppDataFolder;
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FortniteVideoSoftware");
        }
    }

    public static IEnumerable<string> GetVerificationTargets()
    {
        yield return InstallFolder;
        yield return ProgramDataFolder;
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
            catch { continue; }

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

        yield return FortniteVideoSoftware.Core.Infrastructure.KnownFolders.GetPublicDesktop();
        yield return FortniteVideoSoftware.Core.Infrastructure.KnownFolders.GetDesktop();
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
                yield return (hive, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FortniteVideoSoftware");
                yield return (hive, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Fortnite Video Software");
                yield return (hive, view, @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\FortniteVideoSoftware");
                yield return (hive, view, @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Fortnite Video Software");
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
                yield return (hive, view, @"SOFTWARE\FortniteVideoSoftware");
                yield return (hive, view, @"SOFTWARE\Wow6432Node\FortniteVideoSoftware");
                yield return (hive, view, @"SOFTWARE\Wow6432Node\Fortnite Video Software");
            }
        }
    }

    public static IEnumerable<string> GetUserArtifactPatterns()
    {
        yield return Path.Combine(TempRoot, "Fortnite_Video_Software.log");
        yield return Path.Combine(TempRoot, "Fortnite Video Software", "*");
        yield return Path.Combine(TempRoot, "FVS_*");
        yield return Path.Combine(TempRoot, "fvs_*");
    }
}
