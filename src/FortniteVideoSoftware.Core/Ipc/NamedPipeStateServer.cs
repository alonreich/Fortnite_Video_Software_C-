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
    private readonly Mutex _serverMutex;
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _readyEvent = new(false);
    private Task? _listenTask;
    private bool _isDisposed;
    private bool _isDirty;
    private System.Timers.Timer? _debounceTimer;

    public bool IsRunning => !_isDisposed && !_cts.IsCancellationRequested;

    private NamedPipeStateServer(ApplicationPaths paths, JsonObject initialState, Mutex serverMutex)
    {
        _paths = paths;
        _currentState = initialState;
        _serverMutex = serverMutex;

        _debounceTimer = new System.Timers.Timer(500) { AutoReset = false };
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

            Mutex mutex;
            bool createdNew;
            try
            {
                mutex = new Mutex(initiallyOwned: true, IpcProtocol.ServerMutexName, out createdNew);
            }
            catch (AbandonedMutexException ex)
            {
                mutex = ex.Mutex as Mutex ?? new Mutex(true, IpcProtocol.ServerMutexName, out createdNew);
                createdNew = true;
                CoreLogger.Info("IpcServer", "Acquired abandoned IPC server mutex. Prior server process exited abruptly.");
            }
            catch (Exception ex)
            {
                CoreLogger.Debug("IpcServer", $"Could not acquire IPC server mutex: {ex.Message}");
                return null;
            }

            if (!createdNew)
            {
                try { mutex.Dispose(); } catch { }
                return null;
            }

            JsonObject state = initialFallbackState != null && initialFallbackState.Count > 0
                ? StateTransferStore.SanitizeObjectInternal(initialFallbackState.DeepClone().AsObject(), "server init")
                : StateTransferStore.LoadFromDiskDirect(paths);

            if (!state.ContainsKey("schema_version"))
            {
                state["schema_version"] = StateTransferStore.SchemaVersion;
            }

            var server = new NamedPipeStateServer(paths, state, mutex);
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

    private void ScheduleDiskFlush()
    {
        try
        {
            _debounceTimer?.Stop();
            _debounceTimer?.Start();
        }
        catch { }
    }

    public void FlushToDiskSafe()
    {
        JsonObject snapshot;
        lock (_stateLock)
        {
            if (!_isDirty) return;
            snapshot = _currentState.DeepClone().AsObject();
            _isDirty = false;
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

        try { _debounceTimer?.Stop(); _debounceTimer?.Dispose(); } catch { }
        _debounceTimer = null;

        FlushToDiskSafe();

        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }

        try { _serverMutex.ReleaseMutex(); } catch { }
        try { _serverMutex.Dispose(); } catch { }

        try { _readyEvent.Dispose(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_listenTask != null)
        {
            try { await _listenTask.ConfigureAwait(false); } catch { }
        }
    }
}
