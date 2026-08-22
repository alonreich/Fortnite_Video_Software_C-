using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace FortniteVideoSoftware.App;

internal static class DeploymentLifecycle
{
    private const int DeleteRetries = 3;
    private static readonly string SessionTempFolder = Path.Combine(
        DeploymentFootprint.DeploymentTempRoot,
        "Staging_" + Environment.ProcessId);

    private static int _pendingRebootDeletes;

    [Flags]
    private enum MoveFileFlags : uint
    {
        ReplaceExisting = 0x00000001,
        DelayUntilReboot = 0x00000004
    }

    public static bool ShouldHandle(string[] args)
    {
        if (args.Any(a => a.Equals("--install-worker", StringComparison.OrdinalIgnoreCase) ||
                          a.Equals("--cleanup-worker", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return DeploymentFootprint.IsStandaloneInstallerHost(args) ||
               args.Any(arg => arg.Equals("--install", StringComparison.OrdinalIgnoreCase) ||
                               arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)) &&
                !args.Any(a => a.Equals("--cleanup-worker", StringComparison.OrdinalIgnoreCase)))
            {
                return await RunUninstallLauncherAsync(args).ConfigureAwait(false);
            }

            if (args.Any(a => a.Equals("--cleanup-worker", StringComparison.OrdinalIgnoreCase)))
            {
                return await RunUninstallWorkerAsync(args).ConfigureAwait(false);
            }

            return await RunInstallAsync(args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await DeploymentReporter.AppendFatalAsync("LIFECYCLE", ex).ConfigureAwait(false);
            NativeDialog.ShowError(
                "Deployment failed before it could complete." + Environment.NewLine +
                $"Reason: {ex.Message}" + Environment.NewLine +
                $"Report: {DeploymentFootprint.InstallReportPath}");
            return 1;
        }
    }

    internal static async Task<int> RunInstallAsync(string[] args)
    {
        bool isWorker = args.Any(a => a.Equals("--install-worker", StringComparison.OrdinalIgnoreCase));
        bool noLaunch = args.Any(a => a.Equals("--no-launch", StringComparison.OrdinalIgnoreCase));
        bool quiet = args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));

        if (!isWorker)
        {
            await DeploymentReporter.ResetAsync("INSTALL LAUNCHER").ConfigureAwait(false);
            await DeploymentReporter.StepAsync("ELEVATION", "Preparing elevated installer worker from temporary staging.", 5).ConfigureAwait(false);
            await RelaunchInstallFromTempAsync(noLaunch, quiet).ConfigureAwait(false);
            return 0;
        }

        if (!IsElevated())
        {
            await DeploymentReporter.ResetAsync("INSTALL ELEVATION").ConfigureAwait(false);
            await DeploymentReporter.StepAsync("ELEVATION", "Current worker is not elevated. Requesting Administrator permission.", 10).ConfigureAwait(false);
            StartElevated(
                Environment.ProcessPath ?? DeploymentFootprint.InstallPath,
                BuildInstallWorkerArgs(noLaunch, quiet, ReadUserDesktopArgument(args) ?? ProbeInvokingUserDesktop()));
            return 0;
        }

        string? invokingUserDesktop = ReadUserDesktopArgument(args);

        using Semaphore installerGate = CreateInstallerGate();
        if (!AcquireInstallerGate(installerGate))
        {
            await DeploymentReporter.FailAsync("MUTEX", "Another install/uninstall operation is already running. Try again after it finishes.", 0).ConfigureAwait(false);
            NativeDialog.ShowError("Another Fortnite Video Software install or uninstall is already running.");
            return 2;
        }

        try
        {
            await DeploymentReporter.ResetAsync("INSTALL/UPGRADE").ConfigureAwait(false);
            await DeploymentReporter.StepAsync("INIT", "Starting elevated install/upgrade worker.", 1).ConfigureAwait(false);

            bool preserve = false;
            (bool isUpgrade, string? existingVersion) = await ReportExistingVersionAsync().ConfigureAwait(false);

            if (isUpgrade && !ConfirmVersionTransition(existingVersion))
            {
                await DeploymentReporter.StepAsync("CANCELLED",
                    "The user cancelled after being shown the version comparison.", 100).ConfigureAwait(false);
                return 0;
            }

            // ══════════════════════════════════════════════════════════════════════════════════
            // INSTALL_01 — ASK WHENEVER THERE IS SOMETHING TO LOSE, NOT ONLY ON AN "UPGRADE".
            //
            // This prompt used to sit inside `if (isUpgrade)`. `isUpgrade` reports on the INSTALL
            // FOLDER and the uninstall REGISTRY KEY; the cleanup below deletes PROGRAMDATA. Those
            // are different questions, and when they disagreed the user lost. Delete the install
            // folder by hand, or only ever run the compiled exe or the dev build, and `isUpgrade`
            // is false while a complete set of settings — crop profiles, window layout, recovery
            // state — is still sitting in ProgramData. No prompt was shown, `preserve` stayed
            // false, `includeUserData: !preserve` came out TRUE, and the lot was purged silently.
            //
            // The gate is now the data itself. The dialog already carries MB_TOPMOST |
            // MB_SETFOREGROUND, so it was never a z-order problem — it was never reached.
            // ══════════════════════════════════════════════════════════════════════════════════
            bool hasUserData = DeploymentFootprint.UserDataExists();
            if (isUpgrade || hasUserData)
            {
                preserve = NativeDialog.ShowQuestion(
                    (isUpgrade ? "Previous Installation Detected!\r\n" : "Existing Settings Detected!\r\n") +
                    $"Installed version: {existingVersion ?? "unknown"}\r\n" +
                    $"This installer:    {GetCurrentVersion()}\r\n\r\n" +
                    "Would you like to preserve your settings, window layout and preferences from the older installed app?",
                    "Fortnite Video Software Setup");

                await DeploymentReporter.StepAsync("PRESERVE CHOICE",
                    preserve
                        ? "User chose to PRESERVE existing settings. ProgramData, Roaming and Local app data will be kept."
                        : "User chose a CLEAN install. All existing settings will be removed.",
                    4).ConfigureAwait(false);
            }
            else
            {
                await DeploymentReporter.StepAsync("PRESERVE CHOICE",
                    "No previous installation and no existing user data — nothing to preserve, so nothing was asked.",
                    4).ConfigureAwait(false);
            }

            await PerformFullHostCleanupAsync("Pre-Install Cleanup", 5, 55, requireZeroFootprint: false, purgeUserArtifacts: false, includeUserData: !preserve, invokingUserDesktop: invokingUserDesktop).ConfigureAwait(false);
            await InstallFreshAsync(invokingUserDesktop).ConfigureAwait(false);
            await DeploymentReporter.StepAsync("SUCCESS", "Install/upgrade finished. All files, registry entries, Start Menu and desktop shortcuts are in place.", 100).ConfigureAwait(false);

            if (!noLaunch)
            {
                await LaunchInstalledApplicationAsync().ConfigureAwait(false);
            }

            return 0;
        }
        catch (Exception ex)
        {
            await DeploymentReporter.FailAsync("INSTALL FAILED", ex.ToString(), 100).ConfigureAwait(false);
            throw;
        }
        finally
        {
            installerGate.Release();
            QueueSelfCleanup(DeploymentFootprint.TempAppFolder);
        }
    }

    private static async Task<int> RunUninstallLauncherAsync(string[] args)
    {
        bool quiet = args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
        await DeploymentReporter.ResetAsync("UNINSTALL LAUNCHER").ConfigureAwait(false);
        await DeploymentReporter.StepAsync("UNINSTALL", "Preparing elevated cleanup worker from temporary staging.", 5).ConfigureAwait(false);

        Directory.CreateDirectory(SessionTempFolder);
        string source = Environment.ProcessPath ?? DeploymentFootprint.UninstallPath;
        string tempUninstaller = Path.Combine(SessionTempFolder, DeploymentFootprint.UninstallExeName);
        File.Copy(source, tempUninstaller, overwrite: true);

        string? sourceDir = Path.GetDirectoryName(source);
        if (sourceDir != null)
        {
            foreach (string dll in new[] { "libSkiaSharp.dll", "libHarfBuzzSharp.dll" })
            {
                string sourceDll = Path.Combine(sourceDir, dll);
                if (File.Exists(sourceDll))
                {
                    File.Copy(sourceDll, Path.Combine(SessionTempFolder, dll), overwrite: true);
                }
            }
        }

        int parentPid = Environment.ProcessId;
        string workerArgs = $"--uninstall --cleanup-worker {parentPid}" + (quiet ? " --quiet" : "");

        workerArgs = AppendUserDesktopArgument(workerArgs, ProbeInvokingUserDesktop());

        if (!TryStartElevated(tempUninstaller, workerArgs))
        {
            await DeploymentReporter.FailAsync("ELEVATION", "User cancelled Administrator permission or Windows refused to start the elevated cleanup worker.", 10).ConfigureAwait(false);
            if (!quiet)
            {
                NativeDialog.ShowError(
                    "Uninstall could not start because Administrator permission was not granted." + Environment.NewLine + Environment.NewLine +
                    "Log: " + DeploymentFootprint.InstallReportPath);
            }

            return 1;
        }

        return 0;
    }

    internal static async Task<int> RunUninstallWorkerAsync(string[] args)
    {
        bool quiet = args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
        if (!IsElevated())
        {
            string reElevateArgs = AppendUserDesktopArgument(
                "--uninstall --cleanup-worker" + (quiet ? " --quiet" : ""),
                ReadUserDesktopArgument(args) ?? ProbeInvokingUserDesktop());

            StartElevated(Environment.ProcessPath ?? DeploymentFootprint.UninstallPath, reElevateArgs);
            return 0;
        }

        using Semaphore installerGate = CreateInstallerGate();
        if (!AcquireInstallerGate(installerGate))
        {
            await DeploymentReporter.FailAsync("MUTEX", "Another install/uninstall operation is already running. Try again after it finishes.", 0).ConfigureAwait(false);
            return 2;
        }

        try
        {
            int parentPid = ParseParentPid(args);
            if (parentPid > 0)
            {
                await DeploymentReporter.StepAsync("WAIT", $"Waiting for launcher PID {parentPid} to exit before deleting install files.", 8).ConfigureAwait(false);
                await WaitForParentExitAsync(parentPid).ConfigureAwait(false);
            }

            await DeploymentReporter.ResetAsync("UNINSTALL").ConfigureAwait(false);
            await DeploymentReporter.StepAsync("UNINSTALL", "Starting full cleanup.", 10).ConfigureAwait(false);
            await PerformFullHostCleanupAsync("Uninstall", 10, 90, requireZeroFootprint: true, purgeUserArtifacts: true, includeUserData: true, invokingUserDesktop: ReadUserDesktopArgument(args)).ConfigureAwait(false);

            bool clean = await VerifyZeroFootprintAsync().ConfigureAwait(false);
            string status = clean && _pendingRebootDeletes == 0 ? "All components removed successfully." : "Cleanup finished. Some locked files may require a reboot for final removal.";
            await DeploymentReporter.StepAsync("UNINSTALL COMPLETE", status, 100).ConfigureAwait(false);

            return 0;
        }
        catch (Exception ex)
        {
            await DeploymentReporter.FailAsync("UNINSTALL FAILED", ex.ToString(), 100).ConfigureAwait(false);
            throw;
        }
        finally
        {
            installerGate.Release();
            QueueSelfCleanup(DeploymentFootprint.TempAppFolder);
        }
    }

    private static async Task<(bool IsUpgrade, string? ExistingVersion)> ReportExistingVersionAsync()
    {
        string? existingVersion = GetInstalledVersion();
        bool hasFolder = Directory.Exists(DeploymentFootprint.InstallFolder);
        bool hasExe = File.Exists(DeploymentFootprint.InstallPath);

        if (hasFolder || hasExe || !string.IsNullOrWhiteSpace(existingVersion))
        {
            string direction = DescribeVersionTransition(existingVersion);
            await DeploymentReporter.StepAsync(
                "EXISTING INSTALL DETECTED",
                $"Installed='{existingVersion ?? "unknown"}', installer='{GetCurrentVersion()}' ({direction}). " +
                $"folderExists={hasFolder}, exeExists={hasExe}.",
                3).ConfigureAwait(false);
            return (true, existingVersion);
        }

        await DeploymentReporter.StepAsync("FRESH INSTALL", "No previous installation was detected.", 3).ConfigureAwait(false);
        return (false, null);
    }

    /// <summary>ISSUE_02 — classifies installer version vs installed version.</summary>
    private static string DescribeVersionTransition(string? existingVersion)
    {
        if (!TryParseVersion(existingVersion, out Version installed) ||
            !TryParseVersion(GetCurrentVersion(), out Version current))
        {
            return "comparison unavailable";
        }

        int cmp = current.CompareTo(installed);
        return cmp > 0 ? "UPGRADE" : cmp < 0 ? "DOWNGRADE" : "REINSTALL (same version)";
    }

    /// <summary>
    /// ISSUE_02 — a downgrade or same-version reinstall is a deliberate choice, so confirm it.
    /// A straight upgrade proceeds without an extra prompt. Returns true to continue.
    /// </summary>
    private static bool ConfirmVersionTransition(string? existingVersion)
    {
        if (!TryParseVersion(existingVersion, out Version installed) ||
            !TryParseVersion(GetCurrentVersion(), out Version current))
        {
            return true;
        }

        int cmp = current.CompareTo(installed);
        if (cmp > 0) return true;

        string heading = cmp < 0
            ? "You are about to install an OLDER version than the one already installed."
            : "This exact version is already installed.";

        return NativeDialog.ShowQuestion(
            heading + "\r\n\r\n" +
            $"Installed version: {installed}\r\n" +
            $"This installer:    {current}\r\n\r\n" +
            (cmp < 0
                ? "Going backwards can make settings saved by the newer version unreadable.\r\n\r\nContinue anyway?"
                : "Continue and reinstall?"),
            "Fortnite Video Software Setup");
    }

    private static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value)) return false;

        string cleaned = new string(value!.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        return Version.TryParse(cleaned, out version!);
    }

    /// <param name="includeUserData">
    /// ISSUE_02 — false ONLY when the user asked to preserve their settings during an upgrade.
    /// Gates every user-state root (see <see cref="DeploymentFootprint.GetDirectoryPurgeTargets"/>),
    /// not just ProgramData. Uninstall always passes true.
    /// </param>
    /// <param name="invokingUserDesktop">
    /// The real (possibly OneDrive/Group-Policy-redirected) desktop of the user who launched the
    /// operation, resolved BEFORE elevation. Swept for our .lnk files in addition to the
    /// shell-resolved and legacy locations, because an elevated worker's own per-user probe can
    /// resolve to the administrator's profile instead. Null is fine.
    /// </param>
    private static async Task PerformFullHostCleanupAsync(
        string operation,
        int start,
        int end,
        bool requireZeroFootprint,
        bool purgeUserArtifacts,
        bool includeUserData,
        string? invokingUserDesktop = null)
    {
        Interlocked.Exchange(ref _pendingRebootDeletes, 0);
        await DeploymentReporter.StepAsync("CLEANUP START", $"Performing thorough cleanup for {operation}.", start).ConfigureAwait(false);

        await DeploymentReporter.StepAsync("CLEANUP PROCESSES", "Terminating running app instances so files can be replaced safely.", start + 5).ConfigureAwait(false);
        await KillAllProcessesAsync().ConfigureAwait(false);

        await DeploymentReporter.StepAsync("CLEANUP TASKS", "Removing scheduled startup task if it exists.", start + 10).ConfigureAwait(false);
        await RunHiddenProcessAsync("schtasks.exe", $"/Delete /TN \"{DeploymentFootprint.ScheduledTaskName}\" /F", 5000).ConfigureAwait(false);

        List<string> targets = DeploymentFootprint.GetDirectoryPurgeTargets(includeInstallFolder: true, includeUserData: includeUserData)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await DeploymentReporter.StepAsync("CLEANUP SCOPE",
            includeUserData
                ? "User data WILL be removed (settings, window layout, preferences)."
                : "User data will be PRESERVED (settings, window layout, preferences are kept).",
            start + 12).ConfigureAwait(false);

        for (int i = 0; i < targets.Count; i++)
        {
            int progress = start + 15 + (int)((end - start - 35) * (i / Math.Max(1.0, targets.Count)));
            await DeploymentReporter.StepAsync("CLEANUP FILESYSTEM", $"Purging directory: {targets[i]}", progress).ConfigureAwait(false);
            await PurgeDirectoryRecursiveAsync(targets[i]).ConfigureAwait(false);
        }

        await DeploymentReporter.StepAsync("CLEANUP SHELL", "Removing Start Menu, Desktop, and Startup shortcuts.", end - 18).ConfigureAwait(false);
        await DeleteKnownShortcutsAsync(invokingUserDesktop).ConfigureAwait(false);

        await DeploymentReporter.StepAsync("CLEANUP REGISTRY", "Removing uninstall, app, shell cache, and run-key registry footprint.", end - 12).ConfigureAwait(false);
        await DeleteRegistryFootprintAsync().ConfigureAwait(false);

        if (purgeUserArtifacts)
        {
            await DeploymentReporter.StepAsync("CLEANUP USER", "Removing temporary app-generated artifacts.", end - 6).ConfigureAwait(false);
            await PurgeUserGeneratedArtifactsAsync().ConfigureAwait(false);
        }

        if (requireZeroFootprint)
        {
            bool clean = await VerifyZeroFootprintAsync().ConfigureAwait(false);
            if (!clean)
            {
                await DeploymentReporter.StepAsync("CLEANUP VERIFY", "Residual locked items were detected. They may require a reboot.", end).ConfigureAwait(false);
            }
        }
    }

    /// <param name="invokingUserDesktop">
    /// The desktop folder of the user who launched the installer, resolved BEFORE elevation and
    /// handed over on the command line. Null falls back to the Public desktop. See
    /// <see cref="ProbeInvokingUserDesktop"/>.
    /// </param>
    private static async Task InstallFreshAsync(string? invokingUserDesktop = null)
    {
        await DeploymentReporter.StepAsync("DEPLOY FILES", $"Extracting application payload to: {DeploymentFootprint.InstallFolder}", 60).ConfigureAwait(false);
        Directory.CreateDirectory(DeploymentFootprint.InstallFolder);

        string source = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve current executable path.");
        ExtractEmbeddedPayload(DeploymentFootprint.InstallFolder);

        string stagedExe = Path.Combine(DeploymentFootprint.InstallFolder, "FortniteVideoSoftware.App.exe");
        if (File.Exists(stagedExe))
        {
            if (!string.Equals(stagedExe, DeploymentFootprint.InstallPath, StringComparison.OrdinalIgnoreCase))
            {
                await CopyFileAggressiveAsync(stagedExe, DeploymentFootprint.InstallPath).ConfigureAwait(false);
                File.Delete(stagedExe);
            }
        }
        else
        {
            await CopyFileAggressiveAsync(source, DeploymentFootprint.InstallPath).ConfigureAwait(false);
        }

        await CopyFileAggressiveAsync(DeploymentFootprint.InstallPath, DeploymentFootprint.UninstallPath).ConfigureAwait(false);

        await DeploymentReporter.StepAsync("DEPLOY PROGRAMDATA", $"Creating writable ProgramData root: {DeploymentFootprint.ProgramDataFolder}", 72).ConfigureAwait(false);
        Directory.CreateDirectory(DeploymentFootprint.ProgramDataFolder);
        
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "icacls.exe",
                Arguments = $"\"{DeploymentFootprint.ProgramDataFolder}\" /grant *S-1-5-32-545:(OI)(CI)F /T /C /Q",
                CreateNoWindow = true,
                UseShellExecute = false
            })?.WaitForExit();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("ACL", ex);
        }

        await DeploymentReporter.StepAsync("DEPLOY REGISTRY", "Writing Windows Apps & Features uninstall entry.", 78).ConfigureAwait(false);
        await WriteUninstallRegistryAsync().ConfigureAwait(false);

        await DeploymentReporter.StepAsync("DEPLOY SHORTCUT", "Creating Start Menu shortcut with the app icon.", 84).ConfigureAwait(false);
        await CreateStartMenuShortcutAsync().ConfigureAwait(false);

        await DeploymentReporter.StepAsync("DEPLOY DESKTOP", "Creating desktop shortcut with the app icon.", 88).ConfigureAwait(false);
        await CreateDesktopShortcutAsync(invokingUserDesktop).ConfigureAwait(false);

        await DeploymentReporter.StepAsync("DEPLOY VERIFY", "Verifying install files and shortcut exist.", 92).ConfigureAwait(false);
        VerifyInstallArtifacts();
    }

    private static void VerifyInstallArtifacts()
    {
        if (!File.Exists(DeploymentFootprint.InstallPath))
        {
            throw new FileNotFoundException("Installed executable is missing after copy.", DeploymentFootprint.InstallPath);
        }

        if (!File.Exists(DeploymentFootprint.UninstallPath))
        {
            throw new FileNotFoundException("Installed uninstaller is missing after copy.", DeploymentFootprint.UninstallPath);
        }

        string shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Fortnite Video Software.lnk");
        if (!File.Exists(shortcut))
        {
            throw new FileNotFoundException("Start Menu shortcut was not created.", shortcut);
        }
    }

    private static async Task PurgeDirectoryRecursiveAsync(string directory)
    {
        if (!Directory.Exists(directory))
        {
            await DeploymentReporter.StepAsync("CLEANUP SKIP", $"Directory does not exist: {directory}", null).ConfigureAwait(false);
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
            await DeploymentReporter.StepAsync("CLEANUP OK", $"Deleted directory: {directory}", null).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            await DeploymentReporter.StepAsync("CLEANUP RETRY", $"Fast delete failed for {directory}: {ex.Message}. Deleting contents one by one.", null).ConfigureAwait(false);
        }

        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray())
        {
            await DeleteFileWithRetryAsync(file).ConfigureAwait(false);
        }

        foreach (string subDirectory in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToArray())
        {
            TryDeleteDirectory(subDirectory);
        }

        TryDeleteDirectory(directory);
    }

    private static async Task DeleteFileWithRetryAsync(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        for (int attempt = 1; attempt <= DeleteRetries; attempt++)
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                await DeploymentReporter.StepAsync("DELETE OK", path, null).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                if (attempt == DeleteRetries)
                {
                    if (MoveFileEx(path, null, MoveFileFlags.DelayUntilReboot))
                    {
                        Interlocked.Increment(ref _pendingRebootDeletes);
                        await DeploymentReporter.StepAsync("DELETE REBOOT", $"Queued locked file for deletion after reboot: {path}. Reason: {ex.Message}", null).ConfigureAwait(false);
                    }
                    else
                    {
                        await DeploymentReporter.FailAsync("DELETE FAILED", $"{path}: {ex.Message}", null).ConfigureAwait(false);
                    }
                }
                else
                {
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    /// <param name="invokingUserDesktop">
    /// Extra folder to sweep — the launching user's real desktop, resolved before elevation. The
    /// elevated worker cannot discover this itself (its own per-user probe may point at the
    /// administrator's profile), and missing it leaves a dead icon behind after uninstall.
    /// </param>
    private static async Task DeleteKnownShortcutsAsync(string? invokingUserDesktop = null)
    {
        var folders = new List<string>();

        if (!string.IsNullOrWhiteSpace(invokingUserDesktop))
        {
            folders.Add(invokingUserDesktop!);
        }

        folders.AddRange(DeploymentFootprint.GetShortcutSearchFolders());

        var swept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                continue;
            }

            string normalized;
            try { normalized = Path.GetFullPath(folder).TrimEnd('\\', '/'); }
            catch { continue; }

            if (!swept.Add(normalized))
            {
                continue;
            }

            foreach (string shortcutName in DeploymentFootprint.ShortcutFileNames)
            {
                await DeleteFileWithRetryAsync(Path.Combine(normalized, shortcutName)).ConfigureAwait(false);
            }
        }
    }

    private static async Task PurgeUserGeneratedArtifactsAsync()
    {
        foreach (string pattern in DeploymentFootprint.GetUserArtifactPatterns())
        {
            string? directory = Path.GetDirectoryName(pattern);
            string filePattern = Path.GetFileName(pattern);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(directory, filePattern))
            {
                if (string.Equals(file, DeploymentFootprint.InstallReportPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await DeleteFileWithRetryAsync(file).ConfigureAwait(false);
            }
        }
    }

    private static async Task DeleteRegistryFootprintAsync()
    {
        foreach ((RegistryHive hive, RegistryView view, string purgeKey) in DeploymentFootprint.GetUninstallRegistryPurgeTargets())
        {
            await DeleteRegistrySubKeyTreeAsync(hive, view, purgeKey).ConfigureAwait(false);
        }

        foreach ((RegistryHive hive, RegistryView view, string appKey) in DeploymentFootprint.GetAppRegistryPurgeTargets())
        {
            await DeleteRegistrySubKeyTreeAsync(hive, view, appKey).ConfigureAwait(false);
        }

        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                foreach (string mui in new[]
                         {
                             @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
                             @"Software\Microsoft\Windows\ShellNoRoam\MUICache",
                             @"Software\Microsoft\Windows\Shell\MuiCache"
                         })
                {
                    try
                    {
                        using RegistryKey? key = baseKey.OpenSubKey(mui, writable: true);
                        if (key is null)
                        {
                            continue;
                        }

                        foreach (string valueName in key.GetValueNames())
                        {
                            if (valueName.Contains("FortniteVideoSoftware", StringComparison.OrdinalIgnoreCase) ||
                                valueName.Contains("Fortnite Video Software", StringComparison.OrdinalIgnoreCase))
                            {
                                key.DeleteValue(valueName, throwOnMissingValue: false);
                                await DeploymentReporter.StepAsync("REGISTRY MUI", $"{hive}\\{mui}\\{valueName}", null).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                }
            }
        }
    }

    private static async Task DeleteRegistrySubKeyTreeAsync(RegistryHive hive, RegistryView view, string path)
    {
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            await DeploymentReporter.StepAsync("REGISTRY DELETE", $"{hive} {view}\\{path}", null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await DeploymentReporter.StepAsync("REGISTRY SKIP", $"{hive} {view}\\{path}: {ex.Message}", null).ConfigureAwait(false);
        }
    }

    private static async Task WriteUninstallRegistryAsync()
    {
        foreach ((RegistryHive hive, RegistryView view, string uninstallKey) in DeploymentFootprint.GetUninstallRegistryPurgeTargets())
        {
            await DeleteRegistrySubKeyTreeAsync(hive, view, uninstallKey).ConfigureAwait(false);
        }

        (RegistryHive targetHive, RegistryView targetView, string targetPath) = DeploymentFootprint.GetCanonicalUninstallRegistryTarget();
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(targetHive, targetView);
        using RegistryKey createdUninstallKey = baseKey.CreateSubKey(targetPath, writable: true) ??
                                throw new InvalidOperationException("Unable to create uninstall registry key.");

        createdUninstallKey.SetValue("DisplayName", DeploymentFootprint.DisplayName);
        createdUninstallKey.SetValue("UninstallString", $"\"{DeploymentFootprint.UninstallPath}\" --uninstall");
        createdUninstallKey.SetValue("QuietUninstallString", $"\"{DeploymentFootprint.UninstallPath}\" --uninstall --cleanup-worker");
        createdUninstallKey.SetValue("DisplayIcon", DeploymentFootprint.InstallPath + ",0");
        createdUninstallKey.SetValue("InstallLocation", DeploymentFootprint.InstallFolder);
        createdUninstallKey.SetValue("DisplayVersion", GetCurrentVersion());
        createdUninstallKey.SetValue("Publisher", DeploymentFootprint.DisplayName);
        createdUninstallKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
        createdUninstallKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        createdUninstallKey.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        await DeploymentReporter.StepAsync("REGISTRY OK", $"{targetHive} {targetView}\\{targetPath}", null).ConfigureAwait(false);
    }

    private static async Task CreateStartMenuShortcutAsync()
    {
        string shortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            "Fortnite Video Software.lnk");

        await ShellLinkWriter.CreateAsync(
            shortcutPath,
            DeploymentFootprint.InstallPath,
            DeploymentFootprint.InstallFolder,
            DeploymentFootprint.InstallPath + ",0",
            DeploymentFootprint.DisplayName).ConfigureAwait(false);

        await DeploymentReporter.StepAsync("SHORTCUT OK", shortcutPath, null).ConfigureAwait(false);
    }

    /// <summary>
    /// Places the app shortcut on the user's desktop, in addition to the Start Menu entry.
    ///
    /// RESOLUTION ORDER, and why it is this way:
    ///   1. <paramref name="invokingUserDesktop"/> — the real desktop of the person who ran the
    ///      installer, probed BEFORE elevation via <c>SHGetKnownFolderPath</c>. This is the only
    ///      value that survives OneDrive "Backup / Manage folders" redirection
    ///      (<c>%USERPROFILE%\OneDrive\Desktop</c>, or a business variant such as
    ///      <c>OneDrive - Contoso\Desktop</c>) and Group Policy redirection to a UNC share.
    ///   2. The Public / All-Users desktop (<c>C:\Users\Public\Desktop</c>) — a machine location
    ///      that is never redirected, so it resolves correctly even from the elevated worker.
    ///      Windows merges it into every user's desktop view, so the icon still shows up.
    ///
    /// Deliberately NON-FATAL. A missing desktop shortcut is a cosmetic disappointment; aborting a
    /// completed install over one — after the files, registry entry and Start Menu shortcut are all
    /// in place — would be a far worse outcome. Failures are reported and the install continues.
    /// </summary>
    private static async Task CreateDesktopShortcutAsync(string? invokingUserDesktop)
    {
        var candidates = new List<(string Path, string Description)>();

        if (!string.IsNullOrWhiteSpace(invokingUserDesktop))
        {
            candidates.Add((invokingUserDesktop!, "the invoking user's desktop (redirect-aware)"));
        }
        else
        {
            await DeploymentReporter.StepAsync("DESKTOP FALLBACK",
                "No pre-elevation desktop path was supplied; falling back to the Public desktop.", null).ConfigureAwait(false);
        }

        string? publicDesktop = FortniteVideoSoftware.Core.Infrastructure.KnownFolders.GetPublicDesktop()
                                ?? SafeGetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (!string.IsNullOrWhiteSpace(publicDesktop))
        {
            candidates.Add((publicDesktop!, "the Public (All Users) desktop"));
        }

        if (candidates.Count == 0)
        {
            await DeploymentReporter.StepAsync("DESKTOP SKIP",
                "No usable desktop folder could be resolved. The Start Menu shortcut is still installed.", null).ConfigureAwait(false);
            RuntimeLog.Fail("DEPLOY DESKTOP", "No desktop folder could be resolved; skipping the desktop shortcut.");
            return;
        }

        foreach ((string desktopFolder, string description) in candidates)
        {
            string shortcutPath = Path.Combine(desktopFolder, "Fortnite Video Software.lnk");
            try
            {
                Directory.CreateDirectory(desktopFolder);

                if (File.Exists(shortcutPath))
                {
                    try
                    {
                        File.SetAttributes(shortcutPath, FileAttributes.Normal);
                        File.Delete(shortcutPath);
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Debug("DEPLOY DESKTOP", $"Could not remove the existing shortcut first: {ex.Message}");
                    }
                }

                await ShellLinkWriter.CreateAsync(
                    shortcutPath,
                    DeploymentFootprint.InstallPath,
                    DeploymentFootprint.InstallFolder,
                    DeploymentFootprint.InstallPath + ",0",
                    DeploymentFootprint.DisplayName).ConfigureAwait(false);

                await DeploymentReporter.StepAsync("DESKTOP OK", $"{shortcutPath} ({description})", null).ConfigureAwait(false);
                RuntimeLog.Info("DEPLOY DESKTOP", $"Desktop shortcut created on {description}.");
                RuntimeLog.Debug("DEPLOY DESKTOP", $"Path: {shortcutPath}");
                return;
            }
            catch (Exception ex)
            {
                await DeploymentReporter.StepAsync("DESKTOP RETRY",
                    $"Could not write to {desktopFolder} ({ex.Message}). Trying the next location.", null).ConfigureAwait(false);
                RuntimeLog.Fail("DEPLOY DESKTOP", $"Desktop shortcut failed on {description}: {ex.Message}");
            }
        }

        await DeploymentReporter.StepAsync("DESKTOP SKIP",
            "Every desktop location failed. The app is installed and the Start Menu shortcut works.", null).ConfigureAwait(false);
        RuntimeLog.Fail("DEPLOY DESKTOP", "All desktop shortcut locations failed; continuing without one.");
    }

    private static string? SafeGetFolderPath(Environment.SpecialFolder folder)
    {
        try
        {
            string path = Environment.GetFolderPath(folder);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    private static async Task CopyFileAggressiveAsync(string source, string destination)
    {
        if (File.Exists(destination))
        {
            File.SetAttributes(destination, FileAttributes.Normal);
            File.Delete(destination);
        }

        File.Copy(source, destination, overwrite: true);
        await DeploymentReporter.StepAsync("COPY OK", $"{source} -> {destination}", null).ConfigureAwait(false);
    }

    private static void ExtractEmbeddedPayload(string destinationFolder)
    {
        try
        {
            using Stream? stream = typeof(DeploymentLifecycle).Assembly.GetManifestResourceStream("FortniteVideoSoftware.App.payload.zip");
            if (stream is null)
            {
                DeploymentReporter.AppendFatalAsync("EXTRACT PAYLOAD", new Exception("Payload resource FortniteVideoSoftware.App.payload.zip not found")).GetAwaiter().GetResult();
                return;
            }

            Directory.CreateDirectory(DeploymentFootprint.DeploymentTempRoot);
            string tempZip = Path.Combine(DeploymentFootprint.DeploymentTempRoot, "payload.zip");
            using (FileStream fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.CopyTo(fs);
            }

            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, destinationFolder, overwriteFiles: true);
            File.Delete(tempZip);
        }
        catch (Exception ex)
        {
            DeploymentReporter.AppendFatalAsync("EXTRACT PAYLOAD", ex).GetAwaiter().GetResult();
        }
    }

    public static void ExtractAvaloniaDependencies(string destinationFolder)
    {
        try
        {
            using Stream? stream = typeof(DeploymentLifecycle).Assembly.GetManifestResourceStream("FortniteVideoSoftware.App.payload.zip");
            if (stream is null) return;
            
            using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (entry.Name.Equals("libSkiaSharp.dll", StringComparison.OrdinalIgnoreCase) ||
                    entry.Name.Equals("libHarfBuzzSharp.dll", StringComparison.OrdinalIgnoreCase))
                {
                    string destPath = Path.Combine(destinationFolder, entry.Name);
                    if (!File.Exists(destPath))
                    {
                        System.IO.Compression.ZipFileExtensions.ExtractToFile(entry, destPath, overwrite: true);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DeploymentReporter.AppendFatalAsync("EXTRACT DEPENDENCIES", ex).GetAwaiter().GetResult();
        }
    }

    private static async Task LaunchInstalledApplicationAsync()
    {
        await DeploymentReporter.StepAsync("LAUNCH", "Starting installed app after all deployment steps completed.", 98).ConfigureAwait(false);
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using Process? process = Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{DeploymentFootprint.InstallPath}\"",
                    UseShellExecute = true
                });

                await Task.Delay(1200).ConfigureAwait(false);
                if (IsInstalledApplicationRunning())
                {
                    await DeploymentReporter.StepAsync("LAUNCH OK", $"Installed app started on attempt {attempt}.", 99).ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception ex)
            {
                await DeploymentReporter.StepAsync("LAUNCH RETRY", $"Attempt {attempt} failed: {ex.Message}", 98).ConfigureAwait(false);
            }

            await Task.Delay(800).ConfigureAwait(false);
        }

        await DeploymentReporter.StepAsync("LAUNCH WARNING", "Install succeeded, but the installed process could not be confirmed as running.", 99).ConfigureAwait(false);
    }

    private static bool IsInstalledApplicationRunning()
    {
        foreach (string processName in new[] { "FortniteVideoSoftware", "FortniteVideoSoftware.App" })
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (string.Equals(path, DeploymentFootprint.InstallPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            }
        }

        return false;
    }

    private static async Task KillAllProcessesAsync()
    {
        Process current = Process.GetCurrentProcess();
        string installRoot = DeploymentFootprint.InstallFolder.TrimEnd(Path.DirectorySeparatorChar);

        for (int retry = 0; retry < 5; retry++)
        {
            bool foundAny = false;

            foreach (string name in DeploymentFootprint.ProcessNames)
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    if (process.Id == current.Id)
                    {
                        continue;
                    }

                    foundAny = true;
                    await TerminateProcessAsync(process).ConfigureAwait(false);
                }
            }

            foreach (Process process in Process.GetProcesses())
            {
                if (process.Id == current.Id)
                {
                    continue;
                }

                try
                {
                    string? path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) &&
                        path.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        foundAny = true;
                        await TerminateProcessAsync(process).ConfigureAwait(false);
                    }
                }
                catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            }

            if (!foundAny)
            {
                break;
            }

            await Task.Delay(500).ConfigureAwait(false);
        }
    }

    private static async Task TerminateProcessAsync(Process process)
    {
        try
        {
            await DeploymentReporter.StepAsync("PROCESS KILL", $"Terminating PID {process.Id} ({process.ProcessName}).", null).ConfigureAwait(false);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedProcessInspectionException(ex))
        {
            await DeploymentReporter.StepAsync("PROCESS SKIP", $"Could not inspect/terminate PID {process.Id}: {ex.Message}", null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await DeploymentReporter.StepAsync("PROCESS TASKKILL", $"Falling back to taskkill for PID {process.Id}: {ex.Message}", null).ConfigureAwait(false);
            await RunHiddenProcessAsync("taskkill.exe", $"/F /PID {process.Id} /T", 3000).ConfigureAwait(false);
        }
    }

    private static bool IsExpectedProcessInspectionException(Exception ex)
    {
        return ex is UnauthorizedAccessException or InvalidOperationException or Win32Exception or NotSupportedException;
    }

    private static async Task<bool> VerifyZeroFootprintAsync()
    {
        await Task.Delay(500).ConfigureAwait(false);
        bool clean = true;

        foreach (string target in DeploymentFootprint.GetVerificationTargets())
        {
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            {
                clean = false;
                await DeploymentReporter.FailAsync("VERIFY RESIDUE", $"Directory still has contents: {target}", null).ConfigureAwait(false);
            }
        }

        foreach ((RegistryHive hive, RegistryView view, string path) in DeploymentFootprint.GetUninstallRegistryPurgeTargets())
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? key = baseKey.OpenSubKey(path);
                if (key is not null)
                {
                    clean = false;
                    await DeploymentReporter.FailAsync("VERIFY REGISTRY", $"Registry key still exists: {hive} {view}\\{path}", null).ConfigureAwait(false);
                }
            }
            catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }

        return clean;
    }

    private static string? GetInstalledVersion()
    {
        foreach ((RegistryHive hive, RegistryView view, string path) in DeploymentFootprint.GetUninstallRegistryPurgeTargets())
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? key = baseKey.OpenSubKey(path);
                string? version = key?.GetValue("DisplayVersion")?.ToString();
                if (!string.IsNullOrWhiteSpace(version))
                {
                    return version;
                }
            }
            catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }

        if (File.Exists(DeploymentFootprint.InstallPath))
        {
            try
            {
                return FileVersionInfo.GetVersionInfo(DeploymentFootprint.InstallPath).ProductVersion;
            }
            catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }

        return null;
    }

    private static string GetCurrentVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
    }

    private static async Task RelaunchInstallFromTempAsync(bool noLaunch, bool quiet)
    {
        string source = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve current executable path.");


        string args = BuildInstallWorkerArgs(noLaunch, quiet);
        
        if (!TryStartElevated(source, args))
        {
            await DeploymentReporter.FailAsync("ELEVATION", "User cancelled Administrator permission or Windows refused to start the elevated installer worker.", 10).ConfigureAwait(false);
            NativeDialog.ShowError(
                "Install could not start because Administrator permission was not granted." + Environment.NewLine +
                $"Report: {DeploymentFootprint.InstallReportPath}");
        }
    }

    /// <summary>
    /// Flag that carries the INVOKING user's real desktop folder across the UAC boundary.
    /// See <see cref="ProbeInvokingUserDesktop"/> for why this cannot be resolved on the far side.
    /// </summary>
    private const string DesktopShortcutArgument = "--user-desktop";

    private static string BuildInstallWorkerArgs(bool noLaunch, bool quiet)
    {
        return BuildInstallWorkerArgs(noLaunch, quiet, ProbeInvokingUserDesktop());
    }

    private static string BuildInstallWorkerArgs(bool noLaunch, bool quiet, string? userDesktop)
    {
        string args = "--install --install-worker" +
                      (noLaunch ? " --no-launch" : "") +
                      (quiet ? " --quiet" : "");

        return AppendUserDesktopArgument(args, userDesktop);
    }

    /// <summary>
    /// Appends <c>--user-desktop "path"</c> to an elevated-worker command line.
    ///
    /// Appended LAST so <c>args.FirstOrDefault()</c> in Program.cs still sees the real command
    /// ("--install" / "--uninstall"), and so ParseParentPid still finds the PID first.
    ///
    /// QUOTING NOTE: these command lines go to <c>ProcessStartInfo.Arguments</c> with
    /// <c>UseShellExecute = true</c> and <c>Verb = "runas"</c>, so ArgumentList is NOT available —
    /// a correctly quoted single string is mandatory. A plain pair of double quotes is sufficient
    /// and safe here specifically because Windows forbids the <c>"</c> character in file and folder
    /// names outright, so the path cannot contain a quote to break out with. The value is still
    /// checked for one, and trailing separators are trimmed because a trailing <c>\</c> would
    /// escape the closing quote and swallow the rest of the command line. The receiving side
    /// re-validates via <see cref="ReadUserDesktopArgument"/>.
    /// </summary>
    private static string AppendUserDesktopArgument(string args, string? userDesktop)
    {
        if (string.IsNullOrWhiteSpace(userDesktop))
        {
            return args;
        }

        string safe = userDesktop!.TrimEnd('\\', '/');
        if (safe.Length == 0 || safe.Contains('"'))
        {
            return args;
        }

        return args + $" {DesktopShortcutArgument} \"{safe}\"";
    }

    /// <summary>
    /// Resolves the desktop folder of the user who actually launched the installer.
    ///
    /// ⚠️ THIS MUST ONLY BE CALLED BEFORE ELEVATION. ⚠️
    ///
    /// The installer relaunches itself with <c>Verb = "runas"</c>. When the person running it is a
    /// standard user, Windows starts the elevated worker under a DIFFERENT (administrator) account.
    /// From inside that worker, every per-user API — <c>SHGetKnownFolderPath</c>,
    /// <c>Environment.GetFolderPath(SpecialFolder.DesktopDirectory)</c>, <c>%USERPROFILE%</c>,
    /// HKCU — describes the ADMINISTRATOR, not the user. A desktop shortcut written from there
    /// lands in a profile the user never opens, and to them the installer simply "didn't create
    /// the icon".
    ///
    /// So the launcher (which still runs as the real user) probes the path here and passes it over
    /// the command line. <c>KnownFolders.GetDesktop()</c> is used rather than
    /// <c>Environment.GetFolderPath</c> because the latter reads a cached value that goes stale
    /// exactly when it matters — OneDrive "Backup / Manage folders" moving Desktop into
    /// <c>%USERPROFILE%\OneDrive\Desktop</c> (or a business variant like
    /// <c>OneDrive - Contoso\Desktop</c>), and Group Policy redirection pointing it at a UNC share.
    /// </summary>
    private static string? ProbeInvokingUserDesktop()
    {
        try
        {
            string? desktop = FortniteVideoSoftware.Core.Infrastructure.KnownFolders.GetDesktop();
            if (!string.IsNullOrWhiteSpace(desktop))
            {
                RuntimeLog.Info("DESKTOP PROBE", "Resolved the invoking user's desktop folder.");
                RuntimeLog.Debug("DESKTOP PROBE", $"Path: {desktop}");
                return desktop;
            }

            RuntimeLog.Fail("DESKTOP PROBE",
                "Could not resolve the invoking user's desktop folder; the installer will fall back to the Public desktop.");
            return null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("DESKTOP PROBE", $"Desktop probe threw: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads the desktop path handed over by the pre-elevation launcher. Returns null when the
    /// flag is absent (e.g. an already-elevated direct worker launch), in which case the install
    /// falls back to the Public desktop.
    /// </summary>
    private static string? ReadUserDesktopArgument(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(DesktopShortcutArgument, StringComparison.OrdinalIgnoreCase))
            {
                string candidate = args[i + 1];
                if (string.IsNullOrWhiteSpace(candidate)) return null;

                try
                {
                    if (!Path.IsPathFullyQualified(candidate)) return null;
                    string full = Path.GetFullPath(candidate);
                    return Directory.Exists(full) ? full : null;
                }
                catch
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void StartElevated(string exe, string args)
    {
        TryStartElevated(exe, args);
    }

    private static bool TryStartElevated(string exe, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas"
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Semaphore CreateInstallerGate()
    {
        return new Semaphore(1, 1, DeploymentFootprint.InstallerGateName);
    }

    private static bool AcquireInstallerGate(Semaphore semaphore)
    {
        return semaphore.WaitOne(TimeSpan.FromSeconds(15));
    }

    private static int ParseParentPid(string[] args)
    {
        return args.Select(a => int.TryParse(a, out int pid) ? pid : 0).FirstOrDefault(pid => pid > 0);
    }

    private static async Task WaitForParentExitAsync(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private static async Task<int> RunHiddenProcessAsync(string exe, string args, int timeoutMilliseconds)
    {
        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };

            process.Start();
            string command = exe + " " + args;
            await DeploymentReporter.StepAsync("PROCESS START", command, null).ConfigureAwait(false);

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            string output;
            string error;
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds)).ConfigureAwait(false);
                output = await outputTask.ConfigureAwait(false);
                error = await errorTask.ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                try { process.Kill(entireProcessTree: true); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                output = outputTask.IsCompletedSuccessfully ? outputTask.Result : string.Empty;
                error = errorTask.IsCompletedSuccessfully ? errorTask.Result : string.Empty;
                await DeploymentReporter.StepAsync("PROCESS TIMEOUT",
                    $"{command}; killed after {timeoutMilliseconds} ms; output={output}; error={error}", null).ConfigureAwait(false);
                return -1;
            }

            await DeploymentReporter.StepAsync("PROCESS EXIT", $"{command}; exit={process.ExitCode}; output={output}; error={error}", null).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            await DeploymentReporter.StepAsync("PROCESS ERROR", $"{exe} {args}: {ex.Message}", null).ConfigureAwait(false);
            return -1;
        }
    }

    private static void QueueSelfCleanup(string directory)
    {
        try
        {
            string tempRoot = DeploymentFootprint.TempRoot;
            string cleanupCommand = $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{directory}\" & del /q \"{Path.Combine(tempRoot, "FVS_*")}\" 2>nul & del /q \"{Path.Combine(tempRoot, "fvs_*")}\" 2>nul";
            
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cleanupCommand,
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, MoveFileFlags flags);
}
