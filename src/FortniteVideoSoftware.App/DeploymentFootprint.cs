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

    public static string TempRoot => Environment.GetEnvironmentVariable("TMP") ?? Path.GetTempPath();

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

    public static IEnumerable<string> GetDirectoryPurgeTargets(bool includeInstallFolder, bool includeProgramData)
    {
        if (includeInstallFolder)
        {
            yield return InstallFolder;
        }

        if (includeProgramData)
        {
            yield return ProgramDataFolder;
        }

        yield return RoamingAppDataFolder;
        yield return LocalAppDataFolder;
        yield return TempAppFolder;
    }

    public static IEnumerable<string> GetVerificationTargets()
    {
        yield return InstallFolder;
        yield return ProgramDataFolder;
        yield return RoamingAppDataFolder;
        yield return LocalAppDataFolder;
    }

    public static IEnumerable<string> GetShortcutSearchFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
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
