namespace FortniteVideoSoftware.Core.Infrastructure;

public sealed class ApplicationPaths
{
    public const string AppDirectoryName = "Fortnite Video Software";
    public const string ProgramDataRootOverrideEnvironmentVariable = "FVS_PROGRAMDATA_ROOT";

    public ApplicationPaths(string programDataRoot)
    {
        if (string.IsNullOrWhiteSpace(programDataRoot))
        {
            throw new ArgumentException("ProgramData root must not be empty.", nameof(programDataRoot));
        }

        ProgramDataRoot = Path.GetFullPath(programDataRoot);
    }

    public string ProgramDataRoot { get; }

    public string SessionStateFile => Path.Combine(ProgramDataRoot, "session_state.json");

    public string WindowStateFile => Path.Combine(ProgramDataRoot, "window_state.json");

    public string CropCoordinatesFile => Path.Combine(ProgramDataRoot, "crops_coordinations.conf");

    public string LogsDirectory => Path.Combine(ProgramDataRoot, "logs");

    public string TempDirectory
    {
        get
        {
            string? overrideRoot = Environment.GetEnvironmentVariable(ProgramDataRootOverrideEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overrideRoot))
            {
                return Path.Combine(Path.GetTempPath(), "Fortnite_Video_Software_DEV");
            }
            return Path.Combine(Path.GetTempPath(), "Fortnite_Video_Software");
        }
    }

    public string AppSessionLockFile => Path.Combine(ProgramDataRoot, "app_session.lock");

    public string SafeModeSentinelFile => Path.Combine(ProgramDataRoot, "safe_mode.sentinel");

    public string RecoveryStateFile => Path.Combine(ProgramDataRoot, "recovery_v2.json");

    public string InstallerReportFile => Path.Combine(TempDirectory, "Fortnite_Video_Software_Install_Report.txt");

    /// <summary>
    /// ISSUE_09 — the SINGLE home for small per-user UI state files (onboarding counters,
    /// dismissed hints, and anything similar).
    ///
    /// WHY THIS EXISTS: these files used to be scattered in
    /// <c>%APPDATA%\FortniteVideoSoftware\Settings</c> — a THIRD state root, separate from
    /// ProgramData (settings.json, session_state.json, recovery) and %TMP% (logs, staging).
    /// That fragmentation is exactly what made the "preserve my settings" upgrade option leaky:
    /// the uninstaller/upgrader had to know about every root, and it did not.
    ///
    /// New small state files belong HERE. Do not create another root.
    /// </summary>
    public string UiStateDirectory => Path.Combine(ProgramDataRoot, "uistate");

    /// <summary>
    /// ISSUE_09 — the legacy %APPDATA% location, kept ONLY so existing installs can be migrated
    /// once (see UiStateStore.Migrate). Never write here.
    /// </summary>
    public static string LegacyRoamingUiStateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FortniteVideoSoftware", "Settings");

    public static ApplicationPaths CreateDefault()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable(ProgramDataRootOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return new ApplicationPaths(overrideRoot);
        }

        string commonProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(commonProgramData))
        {
            commonProgramData = Environment.GetEnvironmentVariable("PROGRAMDATA") ?? Path.GetTempPath();
        }

        return new ApplicationPaths(Path.Combine(commonProgramData, AppDirectoryName));
    }

    public void EnsureWritableDirectories()
    {
        bool createdRoot = !Directory.Exists(ProgramDataRoot);
        Directory.CreateDirectory(ProgramDataRoot);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
        Directory.CreateDirectory(UiStateDirectory);
        
        if (createdRoot)
        {
            try
            {
                var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "icacls.exe",
                    Arguments = $"\"{ProgramDataRoot}\" /grant *S-1-5-32-545:(OI)(CI)F /T /C /Q",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                System.Threading.Tasks.Task.Run(async () => { if (proc != null) await proc.WaitForExitAsync(); });
            }
            catch { }
        }
    }
}

