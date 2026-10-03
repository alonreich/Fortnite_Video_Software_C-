// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Services;

// ══════════════════════════════════════════════════════════════════════════════════════════════
// VOCAPTURE_01 — THE MICROPHONE HAS ONE OWNER, AND IT IS NOT THE WINDOW.
//
// VoiceOverWindow used to hold the VoiceRecorder, the MicLevelMonitor, the VOASYNC_02 device
// chain and the list of in-flight take drains as loose fields beside its canvases and lamps. That
// made the single most dangerous rule in the studio — the monitor and the recorder must never hold
// the capture device at the same time — impossible to test without a live window and a real
// microphone. The resource lifecycle now lives here; the window keeps pixels, dialogs and pointer
// handling (docs/02 §4 AUD-VOICEOVER, VOCAPTURE_01).
//
// ⚠️ This type must never reference Window, Control, Canvas or the Avalonia Dispatcher. Results go
//    back to the caller through the injected `post` delegate (the window passes
//    Dispatcher.UIThread.Post), which keeps the class testable headless and bit-for-bit faithful to
//    the old "drain on the chain, then Post the verdict" order.
// ══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>VOCAPTURE_01 — lifecycle states of the capture device owner.</summary>
public enum VoiceCaptureState
{
    Idle,
    Monitoring,
    StartingRecording,
    Recording,
    StoppingRecording,
    Faulted,
    Disposed,
}

/// <summary>How a queued recorder open ended.</summary>
public enum RecordingOpenOutcome
{
    /// <summary>The device is open and capture is live.</summary>
    Opened,
    /// <summary>The device refused to open. The session is <see cref="VoiceCaptureState.Faulted"/>.</summary>
    Failed,
    /// <summary>Stop, release or dispose arrived while the driver was opening; the take file was deleted.</summary>
    Cancelled,
}

/// <summary>Result of a queued recorder open, delivered through the session's post delegate.</summary>
public sealed record RecordingOpenResult(RecordingOpenOutcome Outcome, Exception? Failure);

/// <summary>Capture accounting read after the drain (VODIAG_01 / VOASYNC_01).</summary>
public sealed record CapturedTake(long Bytes, int Buffers, float Peak);

/// <summary>The idle level meter's device (VOMON_01). Seam over <see cref="MicLevelMonitor"/>.</summary>
public interface IMicMonitorDevice : IDisposable
{
    event EventHandler<float>? LevelChanged;
    bool IsRunning { get; }
    void Start(int deviceNumber);
    void Stop();
}

/// <summary>One take's capture device. Seam over <see cref="VoiceRecorder"/>.</summary>
public interface IVoiceRecorderDevice : IDisposable
{
    event EventHandler<float>? VolumeChanged;
    long BytesCaptured { get; }
    int BuffersSeen { get; }
    float PeakSeen { get; }
    void StartRecording();
    void StopRecording();
}

/// <summary>Creates the capture devices. Tests inject fakes; production uses <see cref="NAudioVoiceCaptureDevices"/>.</summary>
public interface IVoiceCaptureDeviceFactory
{
    IMicMonitorDevice CreateMonitor();
    IVoiceRecorderDevice CreateRecorder(string outputPath, int deviceNumber);
}

/// <summary>VOCAPTURE_01 — the microphone resource owner, as seen by the window.</summary>
public interface IVoiceCaptureSession : IAsyncDisposable, IDisposable
{
    VoiceCaptureState State { get; }
    Exception? LastFault { get; }
    bool IsMonitorOpen { get; }
    bool IsOpeningRecorder { get; }
    bool IsRecorderLive { get; }
    bool IsDeviceChainIdle { get; }
    bool HasPendingFinalizations { get; }

    /// <summary>Peak of each idle-monitor buffer, raised on the capture thread.</summary>
    event EventHandler<float>? MonitorLevel;

    /// <summary>Peak of each recorded buffer, raised on the capture thread.</summary>
    event EventHandler<float>? RecordingLevel;

    Task StartMonitorAsync(int deviceNumber);
    Task StopMonitorAsync();
    Task<RecordingOpenResult>? StartRecordingAsync(string takePath, int deviceNumber, Action<RecordingOpenResult> onSettled);
    Task<CapturedTake>? FinalizeRecordingAsync(Action<CapturedTake> onSettled);
    Task ReleaseRecorderAsync();
    Task WhenFinalizationsSettled();
}

/// <summary>Production devices: NAudio WinMM through the Core types, unchanged.</summary>
public sealed class NAudioVoiceCaptureDevices : IVoiceCaptureDeviceFactory
{
    public static readonly NAudioVoiceCaptureDevices Instance = new();

    public IMicMonitorDevice CreateMonitor() => new MonitorAdapter(new MicLevelMonitor());

    public IVoiceRecorderDevice CreateRecorder(string outputPath, int deviceNumber)
        => new RecorderAdapter(new VoiceRecorder(outputPath, deviceNumber));

    private sealed class MonitorAdapter(MicLevelMonitor inner) : IMicMonitorDevice
    {
        public event EventHandler<float>? LevelChanged
        {
            add => inner.LevelChanged += value;
            remove => inner.LevelChanged -= value;
        }
        public bool IsRunning => inner.IsRunning;
        public void Start(int deviceNumber) => inner.Start(deviceNumber);
        public void Stop() => inner.Stop();
        public void Dispose() => inner.Dispose();
    }

    private sealed class RecorderAdapter(VoiceRecorder inner) : IVoiceRecorderDevice
    {
        public event EventHandler<float>? VolumeChanged
        {
            add => inner.VolumeChanged += value;
            remove => inner.VolumeChanged -= value;
        }
        public long BytesCaptured => inner.BytesCaptured;
        public int BuffersSeen => inner.BuffersSeen;
        public float PeakSeen => inner.PeakSeen;
        public void StartRecording() => inner.StartRecording();
        public void StopRecording() => inner.StopRecording();
        public void Dispose() => inner.Dispose();
    }
}

/// <summary>
/// VOCAPTURE_01 — owns the recorder, the idle monitor and the VOASYNC_02 device chain.
/// Every public member is callable from any thread; the window calls them from the UI thread and
/// none of them blocks it. All device work runs on the chain, on the thread pool, in issue order.
/// </summary>
public sealed class VoiceCaptureSession : IVoiceCaptureSession
{
    private readonly IVoiceCaptureDeviceFactory _devices;
    private readonly Action<Action> _post;
    private readonly object _gate = new();

    // ══════════════════════════════════════════════════════════════════════════════
    // VOASYNC_02 — THE AUDIO DEVICE CHAIN. (Moved here verbatim in intent from VoiceOverWindow.)
    //
    // Four operations touch the capture device, and EVERY one of them blocks:
    //   opening the recorder      waveInOpen + creating the WAV file
    //   draining the recorder     waits on RecordingStopped, up to 2 s
    //   stopping the monitor      waveInReset + waveInClose, joins the capture thread
    //   starting the monitor      waveInOpen
    // Run inline they froze the window on every press of record. Run on separate tasks they would
    // race: the recorder could try to open the device before the monitor had let go of it, which
    // on many drivers simply fails and loses the take.
    //
    // So they are queued onto ONE chain. Order is preserved exactly as the caller issued it,
    // nothing runs on the caller's thread, and the device is never held by two objects at once.
    // ⚠️ Do NOT replace this with independent Task.Run calls.
    // ══════════════════════════════════════════════════════════════════════════════
    private Task _chain = Task.CompletedTask;

    private VoiceCaptureState _state = VoiceCaptureState.Idle;
    private Exception? _lastFault;
    private IMicMonitorDevice? _monitor;
    private IVoiceRecorderDevice? _recorder;

    /// <summary>Identity of the open currently queued. Cleared (cancelled) by stop/release/dispose.</summary>
    private object? _openAttempt;

    /// <summary>VOASYNC_02 — takes whose drain is still in flight.</summary>
    private readonly List<TaskCompletionSource> _pendingFinalizes = new();

    private Task? _disposeTask;

    public VoiceCaptureSession(IVoiceCaptureDeviceFactory devices, Action<Action> post)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _post = post ?? throw new ArgumentNullException(nameof(post));
    }

    public event EventHandler<float>? MonitorLevel;
    public event EventHandler<float>? RecordingLevel;

    public VoiceCaptureState State { get { lock (_gate) return _state; } }
    public Exception? LastFault { get { lock (_gate) return _lastFault; } }
    public bool IsMonitorOpen { get { lock (_gate) return _monitor?.IsRunning == true; } }
    public bool IsOpeningRecorder { get { lock (_gate) return _openAttempt != null; } }
    public bool IsRecorderLive { get { lock (_gate) return _recorder != null; } }
    public bool IsDeviceChainIdle { get { lock (_gate) return _chain.IsCompleted; } }
    public bool HasPendingFinalizations { get { lock (_gate) return _pendingFinalizes.Count > 0; } }

    /// <summary>VOMON_01 — opens idle monitoring. Ignored while a take owns the device.</summary>
    public Task StartMonitorAsync(int deviceNumber)
    {
        lock (_gate)
        {
            if (_state is VoiceCaptureState.Disposed
                       or VoiceCaptureState.StartingRecording
                       or VoiceCaptureState.Recording) return Task.CompletedTask;

            if (_monitor == null)
            {
                _monitor = _devices.CreateMonitor();
                _monitor.LevelChanged += OnMonitorLevel;
            }

            _state = VoiceCaptureState.Monitoring;
            _lastFault = null;
            var monitor = _monitor;
            return Enqueue(() => monitor.Start(deviceNumber));
        }
    }

    /// <summary>VOMON_01 — releases the device so the recorder can claim it.</summary>
    public Task StopMonitorAsync()
    {
        lock (_gate)
        {
            if (_state == VoiceCaptureState.Disposed) return _chain;
            if (_state == VoiceCaptureState.Monitoring) _state = VoiceCaptureState.Idle;
            var monitor = _monitor;
            return monitor == null ? _chain : Enqueue(monitor.Stop);
        }
    }

    /// <summary>
    /// VOASYNC_02 — queues the recorder open. Returns null (and opens nothing) when a take is
    /// already opening or live, so a burst of record presses can never open the device twice.
    /// The monitor is stopped on the chain FIRST, inside the same job, whatever the caller did.
    /// </summary>
    public Task<RecordingOpenResult>? StartRecordingAsync(string takePath, int deviceNumber, Action<RecordingOpenResult> onSettled)
    {
        lock (_gate)
        {
            if (_state is VoiceCaptureState.Disposed
                       or VoiceCaptureState.StartingRecording
                       or VoiceCaptureState.Recording) return null;
            if (_openAttempt != null || _recorder != null) return null;

            var attempt = new object();
            _openAttempt = attempt;
            _state = VoiceCaptureState.StartingRecording;
            _lastFault = null;

            var done = new TaskCompletionSource<RecordingOpenResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var monitor = _monitor;

            Enqueue(() =>
            {
                // Never monitor AND recorder: the monitor lets go before the recorder asks.
                monitor?.Stop();

                var recorder = _devices.CreateRecorder(takePath, deviceNumber);
                Exception? failure = null;
                try { recorder.StartRecording(); }
                catch (Exception ex)
                {
                    failure = ex;
                    try { recorder.Dispose(); } catch (Exception dex) { RuntimeLog.Swallowed(dex); }
                }

                RecordingOpenResult result;
                bool orphan = false;
                lock (_gate)
                {
                    bool current = ReferenceEquals(_openAttempt, attempt);
                    if (current) _openAttempt = null;

                    if (failure != null)
                    {
                        if (current)
                        {
                            _state = VoiceCaptureState.Faulted;
                            _lastFault = failure;
                        }
                        result = new RecordingOpenResult(current ? RecordingOpenOutcome.Failed : RecordingOpenOutcome.Cancelled, failure);
                    }
                    else if (!current || _state == VoiceCaptureState.Disposed)
                    {
                        orphan = true;
                        result = new RecordingOpenResult(RecordingOpenOutcome.Cancelled, null);
                    }
                    else
                    {
                        recorder.VolumeChanged += OnRecordingLevel;
                        _recorder = recorder;
                        _state = VoiceCaptureState.Recording;
                        result = new RecordingOpenResult(RecordingOpenOutcome.Opened, null);
                    }
                }

                if (orphan)
                {
                    // The stop arrived while the driver was still opening. Close the device, then
                    // delete the WAV it just created — after the dispose, because the file is still
                    // open until then.
                    try { recorder.StopRecording(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                    try { recorder.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                    FreeVideoStudio.App.Infrastructure.VoiceOverAudioTools.TryDeleteFile(takePath);
                }

                // VOCAPTURE_02 — publish THEN complete: the returned Task completes only after
                // onSettled has run (on the post target), so an awaiting caller always sees it.
                PostThenComplete(() => onSettled(result), () => done.TrySetResult(result));
            });

            return done.Task;
        }
    }

    /// <summary>
    /// VOASYNC_02 — detaches the live recorder and drains it on the chain. <paramref name="onSettled"/>
    /// runs through the post delegate BEFORE <see cref="WhenFinalizationsSettled"/> and the returned
    /// Task complete (VOCAPTURE_02), so a caller awaiting either always sees the take. Returns null when no recorder is live (an
    /// open still in flight is cancelled instead).
    /// </summary>
    public Task<CapturedTake>? FinalizeRecordingAsync(Action<CapturedTake> onSettled)
    {
        lock (_gate)
        {
            _openAttempt = null;
            var recorder = _recorder;
            _recorder = null;

            if (recorder == null)
            {
                if (_state is VoiceCaptureState.StartingRecording or VoiceCaptureState.Recording)
                    _state = VoiceCaptureState.Idle;
                return null;
            }

            recorder.VolumeChanged -= OnRecordingLevel;
            if (_state != VoiceCaptureState.Disposed) _state = VoiceCaptureState.StoppingRecording;

            var settled = new TaskCompletionSource();
            _pendingFinalizes.Add(settled);
            var take = new TaskCompletionSource<CapturedTake>(TaskCreationOptions.RunContinuationsAsynchronously);

            Enqueue(() =>
            {
                try { recorder.StopRecording(); }
                catch (Exception ex) { RuntimeLog.Swallowed(ex); }

                var captured = new CapturedTake(recorder.BytesCaptured, recorder.BuffersSeen, recorder.PeakSeen);

                try { recorder.Dispose(); }
                catch (Exception ex) { RuntimeLog.Swallowed(ex); }

                lock (_gate)
                {
                    if (_state == VoiceCaptureState.StoppingRecording) _state = VoiceCaptureState.Idle;
                }
                // VOCAPTURE_02 — the verdict first, then the settle and the returned Task.
                PostThenComplete(
                    () => onSettled(captured),
                    () =>
                    {
                        lock (_gate) _pendingFinalizes.Remove(settled);
                        settled.TrySetResult();
                        take.TrySetResult(captured);
                    });
            });

            return take.Task;
        }
    }

    /// <summary>VOASYNC_02 — retires the recorder (no verdict) and cancels an open in flight. Never blocks.</summary>
    public Task ReleaseRecorderAsync()
    {
        lock (_gate)
        {
            _openAttempt = null;
            if (_state is VoiceCaptureState.StartingRecording or VoiceCaptureState.Recording)
                _state = VoiceCaptureState.Idle;
            return RetireRecorderLocked();
        }
    }

    /// <summary>Completes once every queued drain has run and its verdict callback has returned.</summary>
    public Task WhenFinalizationsSettled()
    {
        lock (_gate)
        {
            if (_pendingFinalizes.Count == 0) return Task.CompletedTask;
            var tasks = new List<Task>(_pendingFinalizes.Count);
            foreach (var tcs in _pendingFinalizes) tasks.Add(tcs.Task);
            return Task.WhenAll(tasks);
        }
    }

    /// <summary>
    /// Releases everything. The recorder is drained exactly once and the monitor disposed, both on
    /// the chain, after any drain already queued. The returned task completes when the device is free.
    /// </summary>
    public ValueTask DisposeAsync() => new(Shutdown());

    public void Dispose() => _ = Shutdown();

    private Task Shutdown()
    {
        lock (_gate)
        {
            if (_disposeTask != null) return _disposeTask;

            _state = VoiceCaptureState.Disposed;
            _openAttempt = null;
            MonitorLevel = null;
            RecordingLevel = null;

            RetireRecorderLocked();

            var monitor = _monitor;
            _monitor = null;
            if (monitor != null)
            {
                monitor.LevelChanged -= OnMonitorLevel;
                Enqueue(monitor.Dispose);
            }

            _disposeTask = _chain;
            return _disposeTask;
        }
    }

    private Task RetireRecorderLocked()
    {
        var recorder = _recorder;
        _recorder = null;
        if (recorder == null) return _chain;
        recorder.VolumeChanged -= OnRecordingLevel;
        return Enqueue(() =>
        {
            try { recorder.StopRecording(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            try { recorder.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        });
    }

    /// <summary>
    /// VOCAPTURE_02 — THE SETTLEMENT CONTRACT. Runs <paramref name="callback"/> through the post
    /// delegate (the UI thread in production) and only then <paramref name="complete"/>, inside the
    /// same posted action, so a caller that awaits the returned Task can never observe it before the
    /// callback has published the same result. The callback runs at most once; <paramref name="complete"/>
    /// runs even if the callback throws. If the post target refuses the work, the callback is
    /// skipped and the Task is still completed, so no caller hangs. Never blocks the chain.
    /// </summary>
    private void PostThenComplete(Action callback, Action complete)
    {
        int completed = 0;
        void CompleteOnce()
        {
            if (Interlocked.Exchange(ref completed, 1) == 0) complete();
        }

        try
        {
            _post(() =>
            {
                try { callback(); }
                finally { CompleteOnce(); }
            });
        }
        catch (Exception ex)
        {
            // Either the post target refused the work (callback never ran) or an inline post target
            // rethrew the callback's exception (callback ran once; completion already happened).
            RuntimeLog.Swallowed(ex);
            CompleteOnce();
        }
    }

    /// <summary>VOASYNC_02 — appends blocking device work to the chain. Caller holds <see cref="_gate"/>.</summary>
    private Task Enqueue(Action work)
    {
        _chain = _chain.ContinueWith(
            _ =>
            {
                try { work(); }
                catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        return _chain;
    }

    private void OnMonitorLevel(object? sender, float level) => MonitorLevel?.Invoke(this, level);

    private void OnRecordingLevel(object? sender, float level) => RecordingLevel?.Invoke(this, level);
}
