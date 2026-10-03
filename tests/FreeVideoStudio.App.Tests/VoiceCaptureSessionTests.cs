// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FreeVideoStudio.App.Services;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// VOCAPTURE_01 / VOASYNC_02 — the capture-device owner, exercised with fake devices. No test here
/// touches a real microphone. The one rule every test also checks: the monitor and the recorder
/// never hold the device at the same time (<see cref="FakeMicrophone.Violations"/>).
/// </summary>
public sealed class VoiceCaptureSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // ── 1 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task MonitoringToRecording_ReleasesMonitorFirst()
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());

        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        Assert.True(session.IsMonitorOpen);
        Assert.Equal(VoiceCaptureState.Monitoring, session.State);

        // Deliberately WITHOUT StopMonitorAsync: the session must release the monitor itself.
        var open = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
        Assert.NotNull(open);
        var result = await open!.WaitAsync(Timeout);

        Assert.Equal(RecordingOpenOutcome.Opened, result.Outcome);
        Assert.Equal(VoiceCaptureState.Recording, session.State);
        Assert.False(session.IsMonitorOpen);
        int monitorClose = devices.Mic.Log.IndexOf("monitor.close");
        int recorderOpen = devices.Mic.Log.IndexOf("recorder.open");
        Assert.True(monitorClose >= 0 && recorderOpen > monitorClose, string.Join(",", devices.Mic.Log));
        Assert.Equal(0, devices.Mic.Violations);
    }

    // ── 2 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RecordingToMonitoring_ReleasesRecorderFirst()
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        await session.StartRecordingAsync(devices.TempPath(), 0, _ => { })!.WaitAsync(Timeout);

        devices.Mic.Log.Clear();
        var take = session.FinalizeRecordingAsync(_ => { });
        Assert.NotNull(take);
        Task monitorBack = session.StartMonitorAsync(0);   // issued immediately, like the window does

        await monitorBack.WaitAsync(Timeout);
        await session.WhenFinalizationsSettled().WaitAsync(Timeout);

        Assert.Equal(new[] { "recorder.close", "monitor.open" }, devices.Mic.Log.ToArray());
        Assert.Equal(VoiceCaptureState.Monitoring, session.State);
        Assert.True(session.IsMonitorOpen);
        Assert.Equal(0, devices.Mic.Violations);
    }

    // ── 3 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RapidRecordClicks_CannotDoubleOpenTheDevice()
    {
        var devices = new FakeDevices();
        devices.OpenGate.Reset();   // hold the first open inside the driver
        using var session = new VoiceCaptureSession(devices, a => a());

        var first = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
        var others = Enumerable.Range(0, 10).Select(_ => session.StartRecordingAsync(devices.TempPath(), 0, _ => { })).ToList();

        Assert.NotNull(first);
        Assert.All(others, Assert.Null);
        Assert.True(session.IsOpeningRecorder);

        devices.OpenGate.Set();
        Assert.Equal(RecordingOpenOutcome.Opened, (await first!.WaitAsync(Timeout)).Outcome);
        Assert.Null(session.StartRecordingAsync(devices.TempPath(), 0, _ => { }));   // live: still refused

        Assert.Equal(1, devices.RecordersCreated);
        Assert.Equal(0, devices.Mic.Violations);
    }

    // ── 4 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task DisposeWhileRecording_DrainsExactlyOnce()
    {
        var devices = new FakeDevices();
        var session = new VoiceCaptureSession(devices, a => a());
        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        await session.StartRecordingAsync(devices.TempPath(), 0, _ => { })!.WaitAsync(Timeout);

        session.Dispose();
        session.Dispose();
        await session.DisposeAsync().AsTask().WaitAsync(Timeout);

        var recorder = Assert.Single(devices.Recorders);
        Assert.Equal(1, recorder.Drains);
        Assert.Equal(VoiceCaptureState.Disposed, session.State);
        Assert.Null(session.FinalizeRecordingAsync(_ => { }));
        Assert.Null(devices.Mic.Owner);
        Assert.Equal(1, devices.Monitor!.Disposals);
        Assert.Equal(0, devices.Mic.Violations);
    }

    // ── 5 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task DeviceOpenFailure_ProducesDeterministicState()
    {
        var devices = new FakeDevices { FailNextOpen = true };
        using var session = new VoiceCaptureSession(devices, a => a());
        await session.StartMonitorAsync(0).WaitAsync(Timeout);

        RecordingOpenResult? posted = null;
        var result = await session.StartRecordingAsync(devices.TempPath(), 0, r => posted = r)!.WaitAsync(Timeout);

        Assert.Equal(RecordingOpenOutcome.Failed, result.Outcome);
        Assert.Same(result, posted);
        Assert.Equal(VoiceCaptureState.Faulted, session.State);
        Assert.Equal("device busy", session.LastFault?.Message);
        Assert.False(session.IsRecorderLive);
        Assert.False(session.IsOpeningRecorder);
        Assert.False(session.IsMonitorOpen);
        Assert.Null(devices.Mic.Owner);
        Assert.Equal(1, Assert.Single(devices.Recorders).Disposals);

        // Recovery is explicit and lands in a known state.
        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        Assert.Equal(VoiceCaptureState.Monitoring, session.State);
        Assert.Null(session.LastFault);
        Assert.Equal("monitor", devices.Mic.Owner);
        Assert.Equal(0, devices.Mic.Violations);
    }

    // ── 6 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ClosingWhileFinalizationPending_WaitsForTheDrainAndTheVerdict()
    {
        var devices = new FakeDevices();
        var session = new VoiceCaptureSession(devices, a => a());
        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        await session.StartRecordingAsync(devices.TempPath(), 0, _ => { })!.WaitAsync(Timeout);

        devices.StopGate.Reset();   // the drain is in flight
        bool verdictRan = false;
        Assert.NotNull(session.FinalizeRecordingAsync(_ => verdictRan = true));
        Task settled = session.WhenFinalizationsSettled();
        Task closed = session.DisposeAsync().AsTask();

        await Task.Delay(150);
        Assert.True(session.HasPendingFinalizations);
        Assert.False(settled.IsCompleted);
        Assert.False(closed.IsCompleted);
        Assert.False(verdictRan);

        devices.StopGate.Set();
        await settled.WaitAsync(Timeout);
        Assert.True(verdictRan);   // the verdict lands BEFORE the settle completes
        await closed.WaitAsync(Timeout);

        Assert.False(session.HasPendingFinalizations);
        Assert.Equal(1, Assert.Single(devices.Recorders).Drains);
        Assert.True(devices.Mic.Log.IndexOf("recorder.close") < devices.Mic.Log.IndexOf("monitor.dispose"));
        Assert.Equal(0, devices.Mic.Violations);
    }

    // ── 7 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Disposal_UnsubscribesEveryHandler()
    {
        var devices = new FakeDevices();
        var session = new VoiceCaptureSession(devices, a => a());
        var levels = new ConcurrentBag<string>();
        session.MonitorLevel += (_, _) => levels.Add("monitor");
        session.RecordingLevel += (_, _) => levels.Add("recorder");

        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        devices.Monitor!.Raise(0.5f);
        await session.StartRecordingAsync(devices.TempPath(), 0, _ => { })!.WaitAsync(Timeout);
        var recorder = Assert.Single(devices.Recorders);
        recorder.Raise(0.5f);
        Assert.Equal(1, devices.Monitor.Subscribers);
        Assert.Equal(1, recorder.Subscribers);
        Assert.Equal(new[] { "monitor", "recorder" }, levels.OrderBy(s => s).ToArray());

        await session.DisposeAsync().AsTask().WaitAsync(Timeout);

        Assert.Equal(0, devices.Monitor.Subscribers);
        Assert.Equal(0, recorder.Subscribers);
        levels.Clear();
        devices.Monitor.Raise(0.9f);
        recorder.Raise(0.9f);
        Assert.Empty(levels);
    }

    [Fact]
    public async Task StopWhileOpening_CancelsAndDeletesTheTakeFile()
    {
        var devices = new FakeDevices();
        devices.OpenGate.Reset();
        using var session = new VoiceCaptureSession(devices, a => a());
        string path = devices.TempPath();

        var open = session.StartRecordingAsync(path, 0, _ => { })!;
        Assert.Null(session.FinalizeRecordingAsync(_ => { }));   // nothing live yet: cancels the open
        devices.OpenGate.Set();

        Assert.Equal(RecordingOpenOutcome.Cancelled, (await open.WaitAsync(Timeout)).Outcome);
        // TryDeleteFile (ISSUE_05) deletes on a retrying background task; give it its turn.
        for (int i = 0; i < 100 && File.Exists(path); i++) await Task.Delay(20);
        Assert.False(File.Exists(path));
        Assert.False(session.IsRecorderLive);
        Assert.Equal(1, Assert.Single(devices.Recorders).Drains);
        Assert.Null(devices.Mic.Owner);
    }

    // ── 8 ─────────────────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public async Task NoBlockingDeviceOperation_RunsOnTheAvaloniaUiThread()
    {
        Assert.True(Dispatcher.UIThread.CheckAccess());
        int uiThread = Environment.CurrentManagedThreadId;

        var devices = new FakeDevices();
        var session = new VoiceCaptureSession(devices, work => Dispatcher.UIThread.Post(work));
        var callbackThreads = new ConcurrentBag<int>();

        await session.StartMonitorAsync(0);
        await session.StopMonitorAsync();
        await session.StartMonitorAsync(1);
        await session.StartRecordingAsync(devices.TempPath(), 1, _ => callbackThreads.Add(Environment.CurrentManagedThreadId))!;
        Assert.NotNull(session.FinalizeRecordingAsync(_ => callbackThreads.Add(Environment.CurrentManagedThreadId)));
        await session.WhenFinalizationsSettled();
        await session.StartRecordingAsync(devices.TempPath(), 1, _ => callbackThreads.Add(Environment.CurrentManagedThreadId))!;
        await session.DisposeAsync();

        Assert.NotEmpty(devices.DeviceCallThreads);
        Assert.DoesNotContain(uiThread, devices.DeviceCallThreads);
        Assert.All(callbackThreads, t => Assert.Equal(uiThread, t));   // verdicts come back to the UI thread
        Assert.Equal(0, devices.Mic.Violations);
    }

    // ── VOCAPTURE_02 — settlement contract: the returned Task completes only AFTER onSettled ─────

    [Fact]
    public async Task DeviceOpenFailure_ImmediateDispatch_CallbackAlwaysPrecedesCompletion()
    {
        // The original race was intermittent; repeat it so a regression cannot pass by luck.
        for (int i = 0; i < 200; i++)
        {
            var devices = new FakeDevices { FailNextOpen = true };
            using var session = new VoiceCaptureSession(devices, a => a());
            int calls = 0;
            RecordingOpenResult? posted = null;

            var result = await session.StartRecordingAsync(devices.TempPath(), 0, r => { posted = r; Interlocked.Increment(ref calls); })!.WaitAsync(Timeout);

            Assert.Same(result, posted);
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.Equal(RecordingOpenOutcome.Failed, result.Outcome);
            Assert.Equal(VoiceCaptureState.Faulted, session.State);
            Assert.Equal(1, Assert.Single(devices.Recorders).Disposals);
        }
    }

    [Fact]
    public async Task DeviceOpenFailure_AsyncDispatch_CallbackPublishedBeforeTaskCompletes()
    {
        using var poster = new AsyncPoster(TimeSpan.FromMilliseconds(50));
        var devices = new FakeDevices { FailNextOpen = true };
        using var session = new VoiceCaptureSession(devices, poster.Post);
        await session.StartMonitorAsync(0).WaitAsync(Timeout);

        int calls = 0, callbackThread = -1;
        RecordingOpenResult? posted = null;
        var result = await session.StartRecordingAsync(devices.TempPath(), 0, r =>
        {
            posted = r;
            callbackThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref calls);
        })!.WaitAsync(Timeout);

        Assert.Same(result, posted);                       // published before the await resumed
        Assert.Equal(poster.ThreadId, callbackThread);     // on the post target, not the chain
        Assert.Equal(RecordingOpenOutcome.Failed, result.Outcome);
        Assert.Equal(VoiceCaptureState.Faulted, session.State);
        Assert.Equal("device busy", session.LastFault?.Message);
        Assert.Equal(1, Assert.Single(devices.Recorders).Disposals);
        Assert.Null(devices.Mic.Owner);

        await Task.Delay(150);
        Assert.Equal(1, Volatile.Read(ref calls));         // no double callback
        Assert.Equal(0, devices.Mic.Violations);
    }

    [Fact]
    public async Task SuccessfulOpen_AsyncDispatch_CallbackPublishedBeforeTaskCompletes()
    {
        using var poster = new AsyncPoster(TimeSpan.FromMilliseconds(50));
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, poster.Post);
        await session.StartMonitorAsync(0).WaitAsync(Timeout);

        int calls = 0;
        RecordingOpenResult? posted = null;
        var result = await session.StartRecordingAsync(devices.TempPath(), 0, r => { posted = r; Interlocked.Increment(ref calls); })!.WaitAsync(Timeout);

        Assert.Same(result, posted);
        Assert.Equal(RecordingOpenOutcome.Opened, result.Outcome);
        Assert.Equal(VoiceCaptureState.Recording, session.State);
        int monitorClose = devices.Mic.Log.IndexOf("monitor.close");
        int recorderOpen = devices.Mic.Log.IndexOf("recorder.open");
        Assert.True(monitorClose >= 0 && recorderOpen > monitorClose, string.Join(",", devices.Mic.Log));   // VOASYNC_02
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(0, devices.Mic.Violations);
    }

    [Fact]
    public async Task Finalize_AsyncDispatch_VerdictPublishedBeforeTaskAndSettleComplete()
    {
        using var poster = new AsyncPoster(TimeSpan.FromMilliseconds(50));
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, poster.Post);
        await session.StartRecordingAsync(devices.TempPath(), 0, _ => { })!.WaitAsync(Timeout);

        int calls = 0;
        CapturedTake? verdict = null;
        var take = session.FinalizeRecordingAsync(c => { verdict = c; Interlocked.Increment(ref calls); });
        Assert.NotNull(take);
        Task settled = session.WhenFinalizationsSettled();

        var captured = await take!.WaitAsync(Timeout);
        Assert.Same(captured, verdict);
        Assert.True(settled.IsCompleted);
        Assert.False(session.HasPendingFinalizations);
        Assert.Equal(1, Assert.Single(devices.Recorders).Drains);
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task ThrowingCallback_AsyncDispatch_StillCompletesExactlyOnce()
    {
        using var poster = new AsyncPoster(TimeSpan.Zero);
        var devices = new FakeDevices { FailNextOpen = true };
        using var session = new VoiceCaptureSession(devices, poster.Post);

        int calls = 0;
        var result = await session.StartRecordingAsync(devices.TempPath(), 0, _ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("window bug");
        })!.WaitAsync(Timeout);

        Assert.Equal(RecordingOpenOutcome.Failed, result.Outcome);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(VoiceCaptureState.Faulted, session.State);
    }

    [Fact]
    public async Task RefusedPost_CompletesWithoutCallback_NoHang()
    {
        var devices = new FakeDevices { FailNextOpen = true };
        using var session = new VoiceCaptureSession(devices, _ => throw new InvalidOperationException("dispatcher shut down"));

        int calls = 0;
        var result = await session.StartRecordingAsync(devices.TempPath(), 0, _ => Interlocked.Increment(ref calls))!.WaitAsync(Timeout);

        Assert.Equal(RecordingOpenOutcome.Failed, result.Outcome);
        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Equal(VoiceCaptureState.Faulted, session.State);
        Assert.Equal(1, Assert.Single(devices.Recorders).Disposals);
    }

    /// <summary>A UI-thread stand-in: runs posted work later, in order, on one dedicated thread.</summary>
    private sealed class AsyncPoster : IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private readonly TimeSpan _delay;

        public AsyncPoster(TimeSpan delay)
        {
            _delay = delay;
            _thread = new Thread(Pump) { IsBackground = true, Name = "FakeUiThread" };
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public void Post(Action work) => _queue.Add(work);

        private void Pump()
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                if (_delay > TimeSpan.Zero) Thread.Sleep(_delay);
                try { work(); }
                catch (Exception) { /* a throwing callback must not kill the fake UI thread */ }
            }
        }

        public void Dispose() => _queue.CompleteAdding();
    }

    // ── fakes ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>One physical capture endpoint. Records every open/close and every double-hold.</summary>
    private sealed class FakeMicrophone
    {
        private readonly object _gate = new();
        public List<string> Log { get; } = new();
        public string? Owner { get; private set; }
        public int Violations { get; private set; }

        public void Open(string who)
        {
            lock (_gate)
            {
                if (Owner != null && Owner != who) Violations++;
                Owner = who;
                Log.Add(who + ".open");
            }
        }

        public void Close(string who)
        {
            lock (_gate)
            {
                if (Owner == who) Owner = null;
                Log.Add(who + ".close");
            }
        }

        public void Note(string entry) { lock (_gate) Log.Add(entry); }
    }

    private sealed class FakeDevices : IVoiceCaptureDeviceFactory
    {
        public FakeMicrophone Mic { get; } = new();
        public FakeMonitor? Monitor { get; private set; }
        public List<FakeRecorder> Recorders { get; } = new();
        public int RecordersCreated => Recorders.Count;
        public bool FailNextOpen { get; set; }
        public ManualResetEventSlim OpenGate { get; } = new(true);
        public ManualResetEventSlim StopGate { get; } = new(true);
        public ConcurrentBag<int> DeviceCallThreads { get; } = new();
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "FvsVoiceCapture_" + Guid.NewGuid().ToString("N"));

        public string TempPath()
        {
            Directory.CreateDirectory(_dir);
            return Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".wav");
        }

        public void Touch() => DeviceCallThreads.Add(Environment.CurrentManagedThreadId);

        public IMicMonitorDevice CreateMonitor() => Monitor = new FakeMonitor(this);

        public IVoiceRecorderDevice CreateRecorder(string outputPath, int deviceNumber)
        {
            Touch();
            var r = new FakeRecorder(this, outputPath, FailNextOpen);
            FailNextOpen = false;
            lock (Recorders) Recorders.Add(r);
            return r;
        }
    }

    private sealed class FakeMonitor(FakeDevices devices) : IMicMonitorDevice
    {
        private EventHandler<float>? _level;
        public int Subscribers { get; private set; }
        public int Disposals { get; private set; }
        public bool IsRunning { get; private set; }

        public event EventHandler<float>? LevelChanged
        {
            add { _level += value; Subscribers++; }
            remove { _level -= value; Subscribers--; }
        }

        public void Raise(float level) => _level?.Invoke(this, level);

        public void Start(int deviceNumber)
        {
            devices.Touch();
            if (IsRunning) return;
            devices.Mic.Open("monitor");
            IsRunning = true;
        }

        public void Stop()
        {
            devices.Touch();
            if (!IsRunning) return;
            IsRunning = false;
            devices.Mic.Close("monitor");
        }

        public void Dispose()
        {
            Stop();
            Disposals++;
            devices.Mic.Note("monitor.dispose");
        }
    }

    private sealed class FakeRecorder(FakeDevices devices, string path, bool fail) : IVoiceRecorderDevice
    {
        private EventHandler<float>? _volume;
        private bool _open;
        public int Subscribers { get; private set; }
        public int Drains { get; private set; }
        public int Disposals { get; private set; }
        public long BytesCaptured => 88200;
        public int BuffersSeen => 20;
        public float PeakSeen => 0.5f;

        public event EventHandler<float>? VolumeChanged
        {
            add { _volume += value; Subscribers++; }
            remove { _volume -= value; Subscribers--; }
        }

        public void Raise(float level) => _volume?.Invoke(this, level);

        public void StartRecording()
        {
            devices.Touch();
            devices.OpenGate.Wait(TimeSpan.FromSeconds(10));
            if (fail) throw new InvalidOperationException("device busy");
            devices.Mic.Open("recorder");
            File.WriteAllText(path, "wav");
            _open = true;
        }

        public void StopRecording()
        {
            devices.Touch();
            if (!_open) return;
            devices.StopGate.Wait(TimeSpan.FromSeconds(10));
            _open = false;
            Drains++;
            devices.Mic.Close("recorder");
        }

        public void Dispose()
        {
            StopRecording();   // mirrors VoiceRecorder.Dispose
            Disposals++;
        }
    }
}
