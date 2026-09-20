// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Ipc;

public sealed class NamedPipeStateServer : IAsyncDisposable, IDisposable
{
    private static readonly object StaticLock = new();
    private static NamedPipeStateServer? _activeInstance;

    public static NamedPipeStateServer? ActiveInstance
    {
        get { lock (StaticLock) { return _activeInstance; } }
        private set { lock (StaticLock) { _activeInstance = value; } }
    }

    private readonly object _stateLock = new();
    private JsonObject _currentState;
    private readonly ApplicationPaths _paths;

    /// <summary>
    /// IPCLEASE_01 — see <see cref="IpcProtocol.ServerMutexName"/>. This handle IS the lease; it is
    /// never acquired and never released, so it has no thread affinity and can never be abandoned.
    /// </summary>
    private readonly Mutex _serverLease;

    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _readyEvent = new(false);
    private Task? _listenTask;
    private bool _isDisposed;
    private bool _isDirty;
    private System.Timers.Timer? _debounceTimer;

    /// <summary>
    /// FLUSHCEILING_01 — the debounce interval. Coalescing is mandatory here: without it a timeline
    /// drag would issue hundreds of AtomicJsonFile.WriteObject calls per second, each taking a
    /// Global\ mutex and a WriteThrough flush, and the disk pressure would visibly stutter preview
    /// playback.
    /// </summary>
    private const int FlushDebounceMs = 500;

    /// <summary>
    /// FLUSHCEILING_01 — THE MAXIMUM a change may sit in memory before it is forced to disk.
    ///
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// WHAT WAS WRONG: ScheduleDiskFlush did Stop() then Start() on a 500ms one-shot timer, and it
    /// is called from EVERY mutating opcode. A CONTINUOUS stream of updates — which is precisely
    /// what dragging a timeline knob or a volume slider produces — restarted the 500ms clock before
    /// it could ever elapse. The flush was therefore postponed INDEFINITELY: the app believed it was
    /// "continuously serialising the session" (docs/05 §SYS-RECOVERY) while in fact nothing reached
    /// the disk for as long as the user kept working. A crash during that drag — the moment a crash
    /// is MOST likely, because it is when the app is busiest — lost everything since the last pause.
    ///
    /// A debounce with no maximum-wait ceiling is not a debounce. It is a promise that the work
    /// happens only when the user stops.
    ///
    /// THE FIX: the trailing 500ms edge still coalesces bursts, but once a change has been waiting
    /// this long the flush is forced regardless of how much more traffic is arriving. Worst case is
    /// bounded at one write per 3 seconds during sustained editing.
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    private const int FlushMaxWaitMs = 3000;

    /// <summary>FLUSHCEILING_01 — TickCount64 when the current unflushed change first appeared, or 0
    /// when there is nothing pending. Guarded by <see cref="_stateLock"/>.</summary>
    private long _firstDirtyTicks;

    public bool IsRunning => !_isDisposed && !_cts.IsCancellationRequested;

    private NamedPipeStateServer(ApplicationPaths paths, JsonObject initialState, Mutex serverLease)
    {
        _paths = paths;
        _currentState = initialState;
        _serverLease = serverLease;

        _debounceTimer = new System.Timers.Timer(FlushDebounceMs) { AutoReset = false };
        _debounceTimer.Elapsed += (_, _) => FlushToDiskSafe();
    }

    public static NamedPipeStateServer? TryStart(ApplicationPaths? paths = null, JsonObject? initialFallbackState = null)
    {
        lock (StaticLock)
        {
            if (_activeInstance != null && _activeInstance.IsRunning)
            {
                return _activeInstance;
            }

            paths ??= ApplicationPaths.CreateDefault();
            paths.EnsureWritableDirectories();

            // IPCLEASE_01 — created, NOT owned. `createdNew` is true only for the process that
            // created the named object, and that process is the server. Because ownership is never
            // taken there is nothing to release on shutdown, no thread affinity to get wrong, and no
            // abandoned-mutex state to misreport as a prior crash. See IpcProtocol.ServerMutexName.
            Mutex lease;
            bool createdNew;
            try
            {
                lease = new Mutex(initiallyOwned: false, name: IpcProtocol.ServerMutexName, createdNew: out createdNew);
            }
            catch (Exception ex)
            {
                CoreLogger.Debug("IpcServer", $"Could not create the IPC server lease: {ex.Message}");
                return null;
            }

            if (!createdNew)
            {
                // Another process already runs the server. Release our handle and fall back to the
                // client path.
                try { lease.Dispose(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }
                return null;
            }

            JsonObject state = initialFallbackState != null && initialFallbackState.Count > 0
                ? StateTransferStore.SanitizeObjectInternal(initialFallbackState.DeepClone().AsObject(), "server init")
                : StateTransferStore.LoadFromDiskDirect(paths);

            if (!state.ContainsKey("schema_version"))
            {
                state["schema_version"] = StateTransferStore.SchemaVersion;
            }

            var server = new NamedPipeStateServer(paths, state, lease);
            server.Start();
            _activeInstance = server;
            return server;
        }
    }

    private void Start()
    {
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        try { _readyEvent.Wait(TimeSpan.FromSeconds(1)); } catch { }
        CoreLogger.Info("IpcServer", $"In-memory state server listening on {IpcProtocol.PipeName}.");
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? serverStream = null;
            try
            {
                serverStream = new NamedPipeServerStream(
                    IpcProtocol.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                _readyEvent.Set();

                await serverStream.WaitForConnectionAsync(ct).ConfigureAwait(false);

                NamedPipeServerStream connection = serverStream;
                _ = Task.Run(async () =>
                {
                    using (connection)
                    {
                        try
                        {
                            await HandleConnectionAsync(connection, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            CoreLogger.Debug("IpcServer", $"Error handling client connection: {ex.Message}");
                        }
                    }
                }, ct);
            }
            catch (OperationCanceledException)
            {
                serverStream?.Dispose();
                break;
            }
            catch (ObjectDisposedException)
            {
                // IPCTEARDOWN_01 — the source or a stream was disposed underneath us. That is a
                // shutdown, not a fault: stop, do not spin.
                serverStream?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                serverStream?.Dispose();
                CoreLogger.Debug("IpcServer", $"Error in IPC server listener: {ex.Message}");
                try { await Task.Delay(100, ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        if (!IpcProtocol.IsConnectedClientTrusted(stream))
        {
            CoreLogger.Fail("IpcServer", "Rejected untrusted client connection.");
            return;
        }

        var frame = await IpcProtocol.ReadFrameAsync(stream, ct).ConfigureAwait(false);
        if (frame == null) return;

        switch (frame.Value.Opcode)
        {
            case IpcOpcode.GetState:
            {
                JsonObject copy;
                lock (_stateLock)
                {
                    copy = _currentState.DeepClone().AsObject();
                }
                await IpcProtocol.WriteJsonFrameAsync(stream, IpcOpcode.GetStateAck, copy, ct).ConfigureAwait(false);
                break;
            }

            case IpcOpcode.UpdateProperties:
            {
                var updates = IpcProtocol.FromUtf8Bytes(frame.Value.Payload);
                if (updates != null)
                {
                    lock (_stateLock)
                    {
                        StateTransferStore.ApplySanitizedUpdatesInternal(_currentState, updates, "ipc update");
                        _currentState["schema_version"] = StateTransferStore.SchemaVersion;
                        _isDirty = true;
                    }
                    ScheduleDiskFlush();
                }
                await IpcProtocol.WriteFrameAsync(stream, IpcOpcode.UpdatePropertiesAck, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                break;
            }

            case IpcOpcode.SaveState:
            {
                var newState = IpcProtocol.FromUtf8Bytes(frame.Value.Payload);
                if (newState != null)
                {
                    lock (_stateLock)
                    {
                        _currentState = StateTransferStore.SanitizeObjectInternal(newState, "ipc save");
                        _currentState["schema_version"] = StateTransferStore.SchemaVersion;
                        _isDirty = true;
                    }
                    ScheduleDiskFlush();
                }
                await IpcProtocol.WriteFrameAsync(stream, IpcOpcode.SaveStateAck, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                break;
            }

            case IpcOpcode.ClearState:
            {
                lock (_stateLock)
                {
                    _currentState = new JsonObject { ["schema_version"] = StateTransferStore.SchemaVersion };
                    _isDirty = true;
                }
                ScheduleDiskFlush();
                await IpcProtocol.WriteFrameAsync(stream, IpcOpcode.ClearStateAck, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                break;
            }

            case IpcOpcode.Handoff:
            {
                var handoff = IpcProtocol.FromUtf8Bytes(frame.Value.Payload);
                if (handoff != null)
                {
                    lock (_stateLock)
                    {
                        StateTransferStore.ApplySanitizedUpdatesInternal(_currentState, handoff, "ipc handoff");
                        _currentState["schema_version"] = StateTransferStore.SchemaVersion;
                        _isDirty = true;
                    }
                    ScheduleDiskFlush();
                }
                await IpcProtocol.WriteFrameAsync(stream, IpcOpcode.HandoffAck, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                break;
            }

            case IpcOpcode.Ping:
            {
                await IpcProtocol.WriteFrameAsync(stream, IpcOpcode.Pong, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                break;
            }

            default:
            {
                await IpcProtocol.WriteFrameAsync(stream, IpcOpcode.Error, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                break;
            }
        }
    }

    public JsonObject GetState()
    {
        lock (_stateLock)
        {
            return _currentState.DeepClone().AsObject();
        }
    }

    public void UpdateProperties(JsonObject updates)
    {
        lock (_stateLock)
        {
            StateTransferStore.ApplySanitizedUpdatesInternal(_currentState, updates, "in-proc update");
            _currentState["schema_version"] = StateTransferStore.SchemaVersion;
            _isDirty = true;
        }
        ScheduleDiskFlush();
    }

    public void SaveState(JsonObject state)
    {
        lock (_stateLock)
        {
            _currentState = StateTransferStore.SanitizeObjectInternal(state, "in-proc save");
            _currentState["schema_version"] = StateTransferStore.SchemaVersion;
            _isDirty = true;
        }
        ScheduleDiskFlush();
    }

    public void ClearState()
    {
        lock (_stateLock)
        {
            _currentState = new JsonObject { ["schema_version"] = StateTransferStore.SchemaVersion };
            _isDirty = true;
        }
        ScheduleDiskFlush();
    }

    /// <summary>
    /// FLUSHCEILING_01 — debounce WITH a maximum-wait ceiling. See <see cref="FlushMaxWaitMs"/> for
    /// the defect this encodes. Callers must already have set <c>_isDirty</c> under
    /// <see cref="_stateLock"/>.
    /// </summary>
    private void ScheduleDiskFlush()
    {
        bool forceNow = false;

        lock (_stateLock)
        {
            if (_firstDirtyTicks == 0)
            {
                _firstDirtyTicks = Environment.TickCount64;
            }
            else if (Environment.TickCount64 - _firstDirtyTicks >= FlushMaxWaitMs)
            {
                forceNow = true;
            }
        }

        if (forceNow)
        {
            // The ceiling has been reached. Stop restarting the clock and get it on disk NOW —
            // off this thread, because callers include the UI thread via the in-process path.
            try { _debounceTimer?.Stop(); } catch (ObjectDisposedException) { }
            _ = Task.Run(FlushToDiskSafe);
            return;
        }

        try
        {
            _debounceTimer?.Stop();
            _debounceTimer?.Start();
        }
        catch (ObjectDisposedException) { /* torn down between the null check and here. */ }
    }

    public void FlushToDiskSafe()
    {
        JsonObject snapshot;
        lock (_stateLock)
        {
            if (!_isDirty) return;
            snapshot = _currentState.DeepClone().AsObject();
            _isDirty = false;
            _firstDirtyTicks = 0;   // FLUSHCEILING_01 — the pending window closes with the write.
        }

        try
        {
            _paths.EnsureWritableDirectories();
            using var guard = NamedSystemMutex.Acquire(
                StateTransferStore.MutexName,
                TimeSpan.FromSeconds(2),
                CancellationToken.None);

            AtomicJsonFile.WriteObject(_paths.SessionStateFile, snapshot);
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("IpcServer", $"Background disk flush error: {ex.Message}");
        }
    }

    /// <summary>
    /// IPCTEARDOWN_01 — SIGNAL, THEN WAIT, THEN DISPOSE. In that order, always.
    ///
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// WHAT WAS WRONG: this called _cts.Cancel() and then _cts.Dispose() IMMEDIATELY, while
    /// ListenLoopAsync was still suspended inside WaitForConnectionAsync(ct). The NamedPipeServerStream
    /// in flight at that moment was not deterministically closed, so the pipe instance stayed held
    /// until the GC finalised it. A restart inside that window hit TryStart's !createdNew path and
    /// silently returned null — quietly degrading every subsequent LoadSync/SaveState in the process
    /// to the slow direct-disk path with no error anywhere.
    ///
    /// It also released a THREAD-AFFINE Mutex from the wrong thread (see IPCLEASE_01), and the
    /// synchronous path never awaited _listenTask at all — only DisposeAsync did, and IDisposable
    /// is the path most callers reach.
    ///
    /// The order below is the fix: stop producing work, flush what is pending, signal cancellation,
    /// WAIT for the listener to actually finish (bounded — a wedged listener must not hang app
    /// shutdown), and only then free the objects the listener was using.
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        lock (StaticLock)
        {
            if (ReferenceEquals(_activeInstance, this))
            {
                _activeInstance = null;
            }
        }

        // 1. Stop producing new flushes.
        try { _debounceTimer?.Stop(); _debounceTimer?.Dispose(); }
        catch (Exception ex) { CoreLogger.Swallowed(ex); }
        _debounceTimer = null;

        // 2. Write out anything still pending, BEFORE cancellation tears the world down.
        FlushToDiskSafe();

        // 3. Signal the listener.
        try { _cts.Cancel(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }

        // 4. WAIT for it to finish. Bounded: a listener that will not stop must not hang shutdown.
        if (_listenTask != null)
        {
            try
            {
                if (!_listenTask.Wait(TimeSpan.FromSeconds(2)))
                {
                    CoreLogger.Debug("IpcServer", "IPC listener did not stop within 2s; continuing teardown.");
                }
            }
            catch (Exception ex) { CoreLogger.Swallowed(ex); }
        }

        // 5. Drop the lease. The lease IS the open handle: a named kernel object lives exactly as
        //    long as one handle to it remains, so closing this handle is what frees the name for the
        //    next process. There is deliberately NO ReleaseMutex() call — ownership was never taken
        //    (see IPCLEASE_01), and Dispose, unlike ReleaseMutex, has no thread affinity. Calling
        //    ReleaseMutex here is what used to throw on every clean shutdown and leave the mutex
        //    abandoned.
        try { _serverLease.Dispose(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }

        // 6. Only now is nothing still using these.
        try { _cts.Dispose(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }
        try { _readyEvent.Dispose(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;

        // Same ordering as Dispose, but awaits the listener instead of blocking on it.
        Task? listener = _listenTask;
        _listenTask = null;

        if (listener != null)
        {
            try { _cts.Cancel(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }
            try { await listener.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) { CoreLogger.Swallowed(ex); }
        }

        Dispose();
    }
}
