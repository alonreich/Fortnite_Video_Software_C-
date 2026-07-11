using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FortniteVideoSoftware.Core.Infrastructure;

public sealed class RecoveryManager
{
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
            // If state exists but lock file does NOT, it means ReleaseLockOnly() was intentionally called
            // during a graceful process handoff (Main -> Merger/Crop). This is not a crash.
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
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
        }
        catch (Exception)
        {
        }

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

                File.Delete(_paths.SafeModeSentinelFile);
            }
            catch
            {
            }
        }
        return false;
    }

    public void ActivateSafeMode()
    {
        try
        {
            _paths.EnsureWritableDirectories();
            File.WriteAllText(_paths.SafeModeSentinelFile, string.Empty);
        }
        catch
        {
        }
    }

    public void AcquireLock()
    {
        try
        {
            _paths.EnsureWritableDirectories();
            string lockData = $"{Environment.ProcessId}:{Process.GetCurrentProcess().StartTime.Ticks}";
            File.WriteAllText(_paths.AppSessionLockFile, lockData);
        }
        catch
        {
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
        }
        catch
        {
        }
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
        }
        catch
        {
        }
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
                AtomicJsonFile.WriteObject(_paths.RecoveryStateFile, state);

                if (sequence.HasValue)
                {
                    _latestCommittedSave = sequence.Value;
                }
            }
            catch
            {
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
            return AtomicJsonFile.ReadObject(_paths.RecoveryStateFile);
        }
        catch
        {
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
        catch
        {
        }
    }
}
