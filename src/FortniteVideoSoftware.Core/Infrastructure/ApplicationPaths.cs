// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

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

    /// <summary>
    /// RECOVERY_02 — "the user meant to close the app" marker.
    ///
    /// Written SYNCHRONOUSLY as the very first act of MainWindow.OnClosing, before any await, and
    /// deleted by the normal cleanup. Its whole purpose is the case where the app is closing
    /// legitimately but never reaches <see cref="RecoveryManager.CleanupLock"/> — most commonly a
    /// Windows shutdown / restart / sign-out, where the OS terminates the process partway through
    /// the asynchronous close. Without it the leftover session lock makes the next launch announce
    /// a crash that never happened.
    ///
    /// Deliberately lives beside the other recovery sentinels rather than in UiStateStore
    /// (ISSUE_09): it is part of the crash-detection family, must be readable before any UI state
    /// exists, and must be writable with one synchronous call on a shutting-down process.
    /// </summary>
    public string CleanShutdownIntentFile => Path.Combine(ProgramDataRoot, "clean_shutdown.intent");

    public string RecoveryStateFile => Path.Combine(ProgramDataRoot, "recovery_v2.json");

    /// <summary>MERGESESSION_01 — the Video Merger's autosaved edit list (MergerAutosaveStore).</summary>
    public string MergerSessionFile => Path.Combine(ProgramDataRoot, "merger_session.json");

    /// <summary>LANECACHE_02 — the Merger's filmstrip frames and waveform peaks, per clip, across sessions.</summary>
    public string LaneCacheDirectory => Path.Combine(ProgramDataRoot, "cache", "lanes");

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

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // USERSCOPE_01 — ALL MUTABLE APP STATE IS PER WINDOWS USER.
    //
    // The root used to be %ProgramData%\Fortnite Video Software. That is ONE folder for the whole
    // machine, made writable with `icacls … /grant Users:F` (launched fire-and-forget, so the first
    // writes raced the ACL change). Meanwhile SingleInstanceGuard is per user, so two Windows
    // accounts could run the app side by side against the SAME recovery_v2.json,
    // session_state.json, app_session.lock and settings. One account could be offered the other's
    // "crashed" session (with its video paths), take over its session lock, and have its recovery
    // state deleted when the other exited cleanly.
    //
    // Now the default root is %LOCALAPPDATA%\Fortnite Video Software. It is private to the user,
    // needs no ACL change, and the uninstaller already purges it (DeploymentFootprint.LocalAppDataFolder
    // is this same path). The first launch per user copies the legacy machine-wide state across
    // once (settings, crop configuration, mask profiles, window layout, recent projects, undo
    // sidecars). Locks, crash-recovery state, logs and voice-over recordings are NOT copied: the
    // first three belong to a dead session or to another user, and existing projects keep
    // pointing at the recordings where they are. The legacy folder is never modified.
    // FVS_PROGRAMDATA_ROOT still overrides everything (dev builds, tests).
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>USERSCOPE_01 — the pre-migration machine-wide root. Read once for migration; never written.</summary>
    public static string LegacyMachineRoot
    {
        get
        {
            string commonProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(commonProgramData))
            {
                commonProgramData = Environment.GetEnvironmentVariable("PROGRAMDATA") ?? Path.GetTempPath();
            }
            return Path.Combine(commonProgramData, AppDirectoryName);
        }
    }

    /// <summary>USERSCOPE_01 — the per-user root.</summary>
    public static string DefaultUserRoot
    {
        get
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
            {
                local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.GetTempPath();
            }
            return Path.Combine(local, AppDirectoryName);
        }
    }

    private static readonly Lazy<bool> LegacyMigration =
        new(() => MigrateLegacyMachineRoot(DefaultUserRoot, LegacyMachineRoot), LazyThreadSafetyMode.ExecutionAndPublication);

    public static ApplicationPaths CreateDefault()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable(ProgramDataRootOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return new ApplicationPaths(overrideRoot);
        }

        _ = LegacyMigration.Value;   // USERSCOPE_01 — once per process, idempotent per user
        return new ApplicationPaths(DefaultUserRoot);
    }

    /// <summary>Written into the user root once the legacy copy has been attempted.</summary>
    public const string MigrationMarkerName = ".migrated_from_programdata";

    private static readonly HashSet<string> NotMigratedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "app_session.lock", "safe_mode.sentinel", "clean_shutdown.intent", "recovery_v2.json",
    };

    private static readonly HashSet<string> NotMigratedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "logs", "Diagnostics", "voiceovers",
    };

    /// <summary>
    /// USERSCOPE_01 — one-time, copy-only migration from the legacy machine root. Never overwrites
    /// a file the user root already has and never touches the legacy folder. Returns true when
    /// anything was copied.
    /// </summary>
    public static bool MigrateLegacyMachineRoot(string userRoot, string legacyRoot)
    {
        try
        {
            string marker = Path.Combine(userRoot, MigrationMarkerName);
            if (File.Exists(marker)) return false;

            Directory.CreateDirectory(userRoot);
            int copied = 0;

            if (Directory.Exists(legacyRoot)
                && !string.Equals(Path.GetFullPath(legacyRoot), Path.GetFullPath(userRoot), StringComparison.OrdinalIgnoreCase))
            {
                foreach (string file in Directory.EnumerateFiles(legacyRoot))
                {
                    string name = Path.GetFileName(file);
                    if (NotMigratedFiles.Contains(name) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    copied += CopyIfAbsent(file, Path.Combine(userRoot, name));
                }

                foreach (string dir in Directory.EnumerateDirectories(legacyRoot))
                {
                    string dirName = Path.GetFileName(dir);
                    if (NotMigratedDirectories.Contains(dirName)) continue;
                    foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                        string relative = Path.GetRelativePath(legacyRoot, file);
                        copied += CopyIfAbsent(file, Path.Combine(userRoot, relative));
                    }
                }
            }

            File.WriteAllText(marker, $"{DateTime.UtcNow:O} copied={copied} from={legacyRoot}");
            if (copied > 0)
            {
                CoreLogger.Info("Paths", $"USERSCOPE_01 — copied {copied} file(s) of shared state from '{legacyRoot}' into this user's '{userRoot}'.");
            }
            return copied > 0;
        }
        catch (Exception ex)
        {
            // Not fatal: the app starts with defaults in the user root, and the marker was not
            // written, so the copy is retried next launch.
            CoreLogger.Fail("Paths", $"USERSCOPE_01 — legacy state migration failed ({ex.Message}); starting with defaults.");
            return false;
        }
    }

    private static int CopyIfAbsent(string source, string destination)
    {
        try
        {
            if (File.Exists(destination)) return 0;
            string? dir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(source, destination, overwrite: false);
            return 1;
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return 0;
        }
    }

    public void EnsureWritableDirectories()
    {
        // USERSCOPE_01 — the root is per-user, so no ACL change is needed (the old fire-and-forget
        // `icacls … Users:F` grant raced the first writes and opened the folder to every account).
        Directory.CreateDirectory(ProgramDataRoot);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
        Directory.CreateDirectory(UiStateDirectory);
    }
}
