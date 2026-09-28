// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.Core.Infrastructure;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// MERGESESSION_01 — THE VIDEO MERGER'S WRITE-BEHIND AUTOSAVE (Video-Merger-Migration.md P3.2, D6).
///
/// Every edit calls <see cref="Schedule"/> (cheap, UI thread). The newest edit list is written
/// <see cref="DefaultDebounce"/> after the LAST edit, on the thread pool, atomically
/// (<see cref="AtomicJsonFile.WriteText"/>). A burst of edits costs one write.
///
/// ORDER (WRITEORDER_01 style): every Schedule/Clear takes a number from one process-wide counter
/// at call time; a write or delete is applied only if its number is newer than the last one applied
/// to the file. A slow write queued before a Clear can therefore never resurrect the file.
///
/// <see cref="Load"/> never throws: a missing, locked or corrupt file is simply "nothing to restore".
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class MergerAutosaveStore : IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(750);

    private static long _versionCounter;

    private readonly string _path;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private MergeEdl? _pending;
    private long _pendingVersion;
    private long _appliedVersion;
    private Task _lastIo = Task.CompletedTask;

    public MergerAutosaveStore(string path, TimeSpan? debounce = null)
    {
        _path = path;
        _debounce = debounce ?? DefaultDebounce;
        _timer = new Timer(_ => StartFlush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string FilePath => _path;

    /// <summary>Queues <paramref name="edl"/> for writing. An empty edit list means "nothing to restore" and clears the file.</summary>
    public void Schedule(MergeEdl edl)
    {
        if (edl.Clips.Count == 0) { Clear(); return; }
        lock (_gate)
        {
            _pending = edl;
            _pendingVersion = Interlocked.Increment(ref _versionCounter);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Writes anything pending now (still off the caller's thread). The task completes when the write is done.</summary>
    public Task FlushAsync()
    {
        lock (_gate) _timer.Change(Timeout.Infinite, Timeout.Infinite);
        return StartFlush();
    }

    /// <summary>Deletes the autosave (ordered after every earlier Schedule).</summary>
    public void Clear()
    {
        long version = Interlocked.Increment(ref _versionCounter);
        lock (_gate)
        {
            _pending = null;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _lastIo = _lastIo.ContinueWith(_ =>
            {
                lock (_gate)
                {
                    if (version <= _appliedVersion) return;
                    _appliedVersion = version;
                }
                AtomicJsonFile.TryDelete(_path);
            }, TaskScheduler.Default);
        }
    }

    /// <summary>The saved edit list, or null. Never throws. Call off the UI thread.</summary>
    public MergeEdl? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var edl = MergeEdl.FromJson(File.ReadAllText(_path));
            return edl is { Clips.Count: > 0 } ? edl : null;
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return null;
        }
    }

    private Task StartFlush()
    {
        lock (_gate)
        {
            if (_pending is not MergeEdl edl) return _lastIo;
            long version = _pendingVersion;
            _pending = null;
            _lastIo = _lastIo.ContinueWith(_ =>
            {
                lock (_gate)
                {
                    if (version <= _appliedVersion) return;
                    _appliedVersion = version;
                }
                try { AtomicJsonFile.WriteText(_path, edl.ToJson()); }
                catch (Exception ex) { CoreLogger.Swallowed(ex); }
            }, TaskScheduler.Default);
            return _lastIo;
        }
    }

    public void Dispose() => _timer.Dispose();
}
