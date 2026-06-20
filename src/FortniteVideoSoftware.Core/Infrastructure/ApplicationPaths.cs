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

    public string CropCoordinatesFile => Path.Combine(ProgramDataRoot, "crops_coordinations.conf");

    public string LogsDirectory => Path.Combine(ProgramDataRoot, "logs");

    public string TempDirectory => Path.Combine(ProgramDataRoot, "temp");

    public string AppSessionLockFile => Path.Combine(ProgramDataRoot, "app_session.lock");

    public string SafeModeSentinelFile => Path.Combine(ProgramDataRoot, "safe_mode.sentinel");

    public string RecoveryStateFile => Path.Combine(ProgramDataRoot, "recovery_v2.json");

    public string InstallerReportFile => Path.Combine(Path.GetTempPath(), "Fortnite_Video_Software_Install_Report.txt");

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
        Directory.CreateDirectory(ProgramDataRoot);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
    }
}
