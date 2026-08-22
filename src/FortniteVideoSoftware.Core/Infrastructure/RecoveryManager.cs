using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FortniteVideoSoftware.Core.Infrastructure;

public sealed class RecoveryManager
{
    /// <summary>
    /// Shape version of <c>recovery_v2.json</c>.
    ///
    /// ISSUE_06 — this did not exist, and THAT was the bug. <see cref="LoadState"/> threw
    /// "Missing schema_version in recovery state" whenever the key was absent, but NOTHING ever
    /// wrote the key: MainWindow.SaveRecoveryState builds the payload by hand and stamps 59 keys,
    /// none of them <c>schema_version</c>. So the throw fired on every single restore, was
    /// swallowed by the catch below, and LoadState returned null every time — meaning the
    /// "restore your previous work?" prompt could never restore anything. The stamp is now applied
    /// centrally in <see cref="SaveState"/> so every writer gets it automatically and no caller
    /// has to remember.
    ///
    /// Bump ONLY when the payload shape changes incompatibly.
    /// </summary>
    public const int SchemaVersion = 1;

    private readonly ApplicationPaths _paths;
    private readonly TimeSpan _safeModeThreshold = TimeSpan.FromSeconds(120);
    private readonly object _saveLock = new();
    private int _saveSequence;
    private int _latestCommittedSave;
    private bool _skipCleanup;

    public RecoveryManager(ApplicationPaths? paths = null)
    {
        _paths = paths ?? ApplicationPaths.CreateDefault();
    }

    /// <summary>
    /// RECOVERY_02 — records that the user is closing the app ON PURPOSE.
    ///
    /// MUST be called synchronously at the very top of the close handler, BEFORE any await. The
    /// close path is asynchronous (save window bounds, stop mpv, dispose the host) and during a
    /// Windows shutdown / restart / sign-out the OS can terminate the process partway through it.
    /// The session lock then survives and the next launch reports a crash that never happened.
    /// This marker is the evidence that the exit was intentional, written while we still certainly
    /// have a thread to write it with.
    ///
    /// Non-throwing by contract: it runs inside a close handler, and a failure here must never
    /// prevent the app from closing.
    /// </summary>
    public void MarkCleanShutdownIntent()
    {
        try
        {
            File.WriteAllText(_paths.CleanShutdownIntentFile,
                $"{Environment.ProcessId}:{DateTime.UtcNow:O}");
        }
        catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
    }

    private void ClearCleanShutdownIntent()
    {
        try
        {
            if (File.Exists(_paths.CleanShutdownIntentFile))
                File.Delete(_paths.CleanShutdownIntentFile);
        }
        catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
    }

    public bool CheckFault()
    {
        if (!File.Exists(_paths.RecoveryStateFile))
        {
            return false;
        }

        if (File.Exists(_paths.CleanShutdownIntentFile))
        {
            CoreLogger.Info("Recovery",
                "Previous session was closing intentionally but did not finish its cleanup (typically a Windows shutdown). Not treating this as a crash.");
            ClearCleanShutdownIntent();
            CleanupLock();
            return false;
        }

        if (IsSafeModeActive())
        {
            return false;
        }

        if (!File.Exists(_paths.AppSessionLockFile))
        {
            return false;
        }

        try
        {
            string content = File.ReadAllText(_paths.AppSessionLockFile).Trim();
            string[] parts = content.Split(':');
            if (parts.Length > 0 && int.TryParse(parts[0], out int oldPid))
            {
                if (oldPid != Environment.ProcessId)
                {
                    try
                    {
                        Process proc = Process.GetProcessById(oldPid);
                        if (!proc.HasExited)
                        {
                            if (proc.ProcessName == Process.GetCurrentProcess().ProcessName)
                            {
                                if (parts.Length > 1 && long.TryParse(parts[1], out long oldTicks))
                                {
                                    if (proc.StartTime.Ticks == oldTicks)
                                        return false;
                                }
                                else
                                {
                                    return false;
                                }
                            }
                        }
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
        }
        catch (Exception)
        {
        }

        CoreLogger.Info("Recovery", "Previous session did not shut down cleanly (crash detected). Recovery state is available to restore.");
        return true;
    }

    /// <summary>
    /// RECOVERY_05 — is a restore currently in flight (or did one die mid-flight)?
    ///
    /// OWNERSHIP FIRST, AGE ONLY AS A BACKSTOP. This used to expire the sentinel purely on
    /// wall-clock age (120 seconds). A restore of a large project on a slow disk can legitimately
    /// take longer than that, and when it did the sentinel was deleted MID-RESTORE — so a crash
    /// during that restore was no longer recognised as a crash loop, which is precisely the
    /// scenario safe mode exists to break, arriving exactly when the session is heaviest.
    ///
    /// The sentinel now records the owning process the same way the session lock does
    /// (PID + process start ticks, which together defeat PID reuse). It is honoured for as long as
    /// that process is genuinely alive, however long the restore takes. It is considered stale —
    /// and cleaned up — only when the owner is gone. The age check survives purely as a backstop
    /// for a sentinel written by an older build that carries no owner stamp.
    /// </summary>
    public bool IsSafeModeActive()
    {
        if (!File.Exists(_paths.SafeModeSentinelFile)) return false;

        try
        {
            string content = "";
            try { content = File.ReadAllText(_paths.SafeModeSentinelFile).Trim(); }
            catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

            if (TryParseOwner(content, out int ownerPid, out long ownerTicks))
            {
                if (ownerPid == Environment.ProcessId) return true;

                if (IsProcessStillRunning(ownerPid, ownerTicks)) return true;
            }
            else
            {
                TimeSpan age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_paths.SafeModeSentinelFile);
                if (age < _safeModeThreshold) return true;
            }

            try { if (File.Exists(_paths.AppSessionLockFile)) File.Delete(_paths.AppSessionLockFile); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
            try { if (File.Exists(_paths.RecoveryStateFile)) File.Delete(_paths.RecoveryStateFile); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

            File.Delete(_paths.SafeModeSentinelFile);
        }
        catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

        return false;
    }

    /// <summary>Parses a "pid:startTicks" owner stamp. Shared by the lock and the safe-mode sentinel.</summary>
    private static bool TryParseOwner(string content, out int pid, out long startTicks)
    {
        pid = 0; startTicks = 0;
        if (string.IsNullOrWhiteSpace(content)) return false;
        string[] parts = content.Split(':');
        return parts.Length >= 2
               && int.TryParse(parts[0], out pid)
               && long.TryParse(parts[1], out startTicks);
    }

    /// <summary>
    /// True when the stamped process is still alive AND is genuinely the same process — the start
    /// ticks are what stop a recycled PID from impersonating the previous owner.
    /// </summary>
    private static bool IsProcessStillRunning(int pid, long startTicks)
    {
        try
        {
            using Process proc = Process.GetProcessById(pid);
            return !proc.HasExited && proc.StartTime.Ticks == startTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (System.Exception ex) { CoreLogger.Swallowed(ex); return false; }
    }

    public void ActivateSafeMode()
    {
        try
        {
            _paths.EnsureWritableDirectories();
            File.WriteAllText(_paths.SafeModeSentinelFile,
                $"{Environment.ProcessId}:{Process.GetCurrentProcess().StartTime.Ticks}");
            CoreLogger.Info("Recovery", "Safe mode activated to prevent a crash loop.");
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("Recovery", $"Failed to activate safe mode: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears the safe-mode sentinel. Called after a recovery restore completes
    /// successfully so that genuine later crashes in this session remain recoverable.
    /// </summary>
    public void DeactivateSafeMode()
    {
        try
        {
            if (File.Exists(_paths.SafeModeSentinelFile))
            {
                File.Delete(_paths.SafeModeSentinelFile);
            }
            CoreLogger.Info("Recovery", "Safe mode deactivated after successful recovery.");
        }
        catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
    }

    public void AcquireLock()
    {
        try
        {
            _paths.EnsureWritableDirectories();
            string lockData = $"{Environment.ProcessId}:{Process.GetCurrentProcess().StartTime.Ticks}";
            File.WriteAllText(_paths.AppSessionLockFile, lockData);
            ClearCleanShutdownIntent();
            CoreLogger.Info("Recovery", $"Session lock acquired (PID {Environment.ProcessId}).");
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("Recovery", $"Failed to acquire session lock: {ex.Message}");
        }
    }

    public void CleanupLock()
    {
        if (_skipCleanup)
        {
            return;
        }

        try
        {
            if (File.Exists(_paths.AppSessionLockFile))
            {
                File.Delete(_paths.AppSessionLockFile);
            }

            if (File.Exists(_paths.SafeModeSentinelFile))
            {
                File.Delete(_paths.SafeModeSentinelFile);
            }

            ClearState();
            ClearCleanShutdownIntent();
            CoreLogger.Info("Recovery", "Session lock and recovery state cleaned up on normal shutdown.");
        }
        catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
    }

    public void SetSkipCleanup(bool skip)
    {
        _skipCleanup = skip;
    }

    /// <summary>
    /// Deletes only the session lock file without clearing recovery state.
    /// Use during intentional process handoffs (Main → Merger/CropTool) so
    /// CheckFault() returns false on restart, but recovery state is preserved
    /// for genuine crash detection.
    /// </summary>
    public void ReleaseLockOnly()
    {
        try
        {
            if (File.Exists(_paths.AppSessionLockFile))
            {
                File.Delete(_paths.AppSessionLockFile);
            }
            CoreLogger.Info("Recovery", "Session lock released for process handoff (recovery state preserved).");
        }
        catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
    }

    public void SaveStateAsync(JsonObject state)
    {
        int sequence = Interlocked.Increment(ref _saveSequence);
        Task.Run(() => SaveState(state, sequence));
    }

    public void SaveState(JsonObject state, int? sequence = null)
    {
        lock (_saveLock)
        {
            if (sequence.HasValue && sequence.Value < _latestCommittedSave)
            {
                return;
            }

            try
            {
                _paths.EnsureWritableDirectories();

                state["schema_version"] = SchemaVersion;

                AtomicJsonFile.WriteObject(_paths.RecoveryStateFile, state);

                if (sequence.HasValue)
                {
                    _latestCommittedSave = sequence.Value;
                }
            }
            catch (Exception ex)
            {
                CoreLogger.Fail("Recovery", $"Failed to save recovery state: {ex.Message}");
            }
        }
    }

    public JsonObject? LoadState()
    {
        if (!File.Exists(_paths.RecoveryStateFile))
        {
            return null;
        }

        try
        {
            var state = AtomicJsonFile.ReadObject(_paths.RecoveryStateFile);
            if (state != null)
            {
                int fileVersion = SchemaVersion;
                if (state.TryGetPropertyValue("schema_version", out JsonNode? versionNode) && versionNode != null)
                {
                    try 
                    { 
                        if (versionNode.AsValue().TryGetValue(out int v)) fileVersion = v;
                        else if (versionNode.AsValue().TryGetValue(out string? s) && int.TryParse(s, out int parsed)) fileVersion = parsed;
                        else fileVersion = versionNode.GetValue<int>(); 
                    }
                    catch
                    {
                        CoreLogger.Info("Recovery", "Unreadable schema_version in recovery state. Assuming current schema.");
                        fileVersion = SchemaVersion;
                    }

                    if (fileVersion < 1)
                    {
                        CoreLogger.Info("Recovery", $"Invalid schema_version {fileVersion} in recovery state. Assuming current schema.");
                        fileVersion = SchemaVersion;
                    }
                }
                else
                {
                    CoreLogger.Info("Recovery",
                        "Recovery state predates schema stamping; treating it as the current shape.");
                }

                if (fileVersion != SchemaVersion)
                {
                    CoreLogger.Info("Recovery",
                        $"Recovery state was written by a different app version (found schema {fileVersion}, this build expects {SchemaVersion}). " +
                        "Skipping the restore and starting fresh; the file has been left on disk.");
                    return null;
                }

                CoreLogger.Info("Recovery", "Recovery state loaded for restore.");
            }
            return state;
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("Recovery", $"Failed to load recovery state: {ex.Message}");
            return null;
        }
    }

    public void ClearState()
    {
        try
        {
            if (File.Exists(_paths.RecoveryStateFile))
            {
                File.Delete(_paths.RecoveryStateFile);
            }
        }
        catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
    }

    /// <summary>
    /// ISSUE_1 — sets a FAILED restore aside instead of destroying it.
    ///
    /// A failed restore used to call <see cref="ClearState"/>, which deletes the file outright. The
    /// user answered "yes, restore my work", one bad field threw, and their entire crashed session
    /// was gone with nothing but a log line — and no second chance, because the evidence had been
    /// deleted along with the data.
    ///
    /// Renaming instead keeps two things true at once: the data survives for a manual rescue or a
    /// bug report, AND the app cannot get stuck in a crash-restore-crash loop, because the file is
    /// no longer where <see cref="LoadState"/> looks. Only ONE quarantine file is kept — a repeated
    /// failure overwrites it rather than growing a pile in the user's data folder.
    ///
    /// Returns the quarantine path so the caller can tell the user where their work went, or null
    /// if there was nothing to move.
    /// </summary>
    public string? QuarantineState()
    {
        try
        {
            if (!File.Exists(_paths.RecoveryStateFile)) return null;

            string quarantinePath = _paths.RecoveryStateFile + ".failed";
            File.Move(_paths.RecoveryStateFile, quarantinePath, overwrite: true);
            return quarantinePath;
        }
        catch
        {
            return null;
        }
    }
}
