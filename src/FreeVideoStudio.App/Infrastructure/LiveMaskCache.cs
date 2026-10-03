// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Project;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// EDITHOT_01 — THE LIVE HUD MASK, READ OFF THE UI THREAD.
///
/// PROJ_11 records the live mask in every <c>ProjectDocument</c>. It was captured by calling
/// <see cref="MaskOverlayManager.ReadLiveMask"/> inside <c>ProjectSession.Capture()</c>, and Capture
/// runs on EVERY edit tick (every notch of the speed and quality dials). Each tick therefore:
///   • took the machine-wide named mutex <c>StateTransferStore.MutexName</c> (15 s timeout),
///     which the IPC state server holds across a WriteThrough flush and File.Move retries;
///   • read <c>crops_coordinations.conf</c> from disk;
///   • deep-cloned it and SHA-256 fingerprinted it,
/// all on the UI thread (North Star #6). A slow disk, an antivirus scan or a sibling process
/// holding the mutex froze the dial.
///
/// The rule now:
///   • Hot paths (edit capture, undo/redo, autosave) read <see cref="Current"/>. That is a memory
///     read of the last snapshot and never blocks.
///   • The snapshot is refreshed on a thread-pool thread: at startup, whenever the crop
///     configuration file changes on disk (FileSystemWatcher, which covers the Crop Tool, profile
///     switches and external edits), and whenever the active profile name differs from the one
///     the snapshot was taken under.
///   • A user-initiated Save calls <see cref="ReadNow"/>, which reads synchronously. The saved
///     .fvsproj therefore always records the mask that is active at the moment of saving. That
///     read is PROJ_11's actual guarantee, and a click is not a gesture.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class LiveMaskCache
{
    private static ProjectMask? _mask;
    private static string? _profileAtRead;
    private static volatile bool _loaded;
    private static int _refreshQueued;
    private static FileSystemWatcher? _watcher;
    private static readonly object StartGate = new();

    /// <summary>
    /// The last mask snapshot. Never touches the disk or a lock. It schedules a background refresh when
    /// the snapshot is missing or was taken under a different active profile.
    /// </summary>
    public static ProjectMask? Current
    {
        get
        {
            EnsureStarted();
            string? active = SafeActiveProfile();
            if (!_loaded || !string.Equals(active, Volatile.Read(ref _profileAtRead), StringComparison.Ordinal))
            {
                RequestRefresh();
            }
            return Volatile.Read(ref _mask);
        }
    }

    /// <summary>Synchronous read for user-initiated saves. Updates the snapshot as a side effect.</summary>
    public static ProjectMask? ReadNow()
    {
        EnsureStarted();
        string? active = SafeActiveProfile();
        ProjectMask? mask = MaskOverlayManager.ReadLiveMask();
        Publish(mask, active);
        return mask;
    }

    /// <summary>Queues one background refresh. Coalesces: many requests during one read cost one extra read.</summary>
    public static void RequestRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        _ = Task.Run(RefreshCore);
    }

    private static void RefreshCore()
    {
        // Cleared BEFORE reading, so a change that lands during the read queues another pass.
        Interlocked.Exchange(ref _refreshQueued, 0);
        string? active = SafeActiveProfile();
        try
        {
            Publish(MaskOverlayManager.ReadLiveMask(), active);
        }
        catch (Exception ex)
        {
            // The previous snapshot stays in place, so edits keep recording the last known mask.
            FreeVideoStudio.Core.Abstractions.Faults.Degraded("PROJECT",
                "The HUD mask could not be read, so project saves may record the previous mask. " +
                "Editing and export still work.",
                ex);
        }
    }

    private static void Publish(ProjectMask? mask, string? profile)
    {
        Volatile.Write(ref _mask, mask);
        Volatile.Write(ref _profileAtRead, profile);
        _loaded = true;
    }

    private static string? SafeActiveProfile()
    {
        try { return SettingsManager.Instance.ActiveMaskOverlay; }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return null;
        }
    }

    private static void EnsureStarted()
    {
        if (_watcher != null) return;
        lock (StartGate)
        {
            if (_watcher != null) return;
            try
            {
                string file = ApplicationPaths.CreateDefault().CropCoordinatesFile;
                string? dir = Path.GetDirectoryName(file);
                if (string.IsNullOrEmpty(dir)) return;
                Directory.CreateDirectory(dir);

                // AtomicJsonFile writes a GUID temp file and File.Move()s it over the target, so
                // the target name shows up as Renamed (NewName), not Changed. Both are watched.
                var watcher = new FileSystemWatcher(dir, Path.GetFileName(file))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                };
                FileSystemEventHandler changed = (_, _) => RequestRefresh();
                watcher.Changed += changed;
                watcher.Created += changed;
                watcher.Deleted += changed;
                watcher.Renamed += (_, _) => RequestRefresh();
                watcher.Error += (_, e) =>
                {
                    RuntimeLog.Fail("PROJECT", $"Mask file watcher overflowed/failed ({e.GetException().Message}); refreshing.");
                    RequestRefresh();
                };
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception ex)
            {
                // Without a watcher the profile-name check still triggers refreshes, and
                // ReadNow() still guarantees saves. Only external edits of the same profile go
                // unnoticed until the next save.
                FreeVideoStudio.Core.Abstractions.Faults.Degraded("PROJECT",
                    "HUD mask changes made outside this window may not be noticed until the next save. " +
                    "Editing, saving and export still work.",
                    ex);
                _watcher = new FileSystemWatcher();   // sentinel: do not retry every tick
            }
            RequestRefresh();
        }
    }
}
