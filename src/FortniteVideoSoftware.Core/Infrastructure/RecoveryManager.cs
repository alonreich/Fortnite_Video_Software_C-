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

    public bool CheckFault()
    {
        if (!File.Exists(_paths.RecoveryStateFile))
        {
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

    public bool IsSafeModeActive()
    {
        if (File.Exists(_paths.SafeModeSentinelFile))
        {
            try
            {
                TimeSpan age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_paths.SafeModeSentinelFile);
                if (age < _safeModeThreshold)
                {
                    return true;
                }

                try { if (File.Exists(_paths.AppSessionLockFile)) File.Delete(_paths.AppSessionLockFile); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
                try { if (File.Exists(_paths.RecoveryStateFile)) File.Delete(_paths.RecoveryStateFile); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }

                File.Delete(_paths.SafeModeSentinelFile);
            }
            catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
        }
        return false;
    }

    public void ActivateSafeMode()
    {
        try
        {
            _paths.EnsureWritableDirectories();
            File.WriteAllText(_paths.SafeModeSentinelFile, string.Empty);
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
        catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
    }

    public void AcquireLock()
    {
        try
        {
            _paths.EnsureWritableDirectories();
            string lockData = $"{Environment.ProcessId}:{Process.GetCurrentProcess().StartTime.Ticks}";
            File.WriteAllText(_paths.AppSessionLockFile, lockData);
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
            CoreLogger.Info("Recovery", "Session lock and recovery state cleaned up on normal shutdown.");
        }
        catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
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
        catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
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
        catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
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
            // Could not move it — leave the original alone. Keeping an unreadable file is strictly
            // better than deleting a readable one, and LoadState already tolerates a bad file.
            return null;
        }
    }
}
