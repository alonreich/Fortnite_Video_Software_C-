// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Collections.Concurrent;
using System.Diagnostics;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Abstractions;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// EXPORTSESSION_02 — the export lifecycle owner, exercised with a fake runner. No FFmpeg, no
/// window, no dispatcher: the lifecycle guarantees of EXPORTSESSION_01 are proven here directly.
/// </summary>
public sealed class ExportCoordinatorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // ── 1 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task SecondExport_IsRejected_WhileRunning()
    {
        var h = new Harness();
        var first = h.Coordinator.StartAsync(h.Request(h.Runner));
        Assert.Equal(ExportState.Running, h.Coordinator.State);

        var second = new FakeRunner();
        var rejected = await h.Coordinator.StartAsync(h.Request(second)).WaitAsync(Timeout);

        Assert.Equal(ExportOutcome.Rejected, rejected.Outcome);
        Assert.Equal(0, second.Invocations);
        Assert.Equal(ExportState.Running, h.Coordinator.State);
        Assert.False(h.Runner.Token.IsCancellationRequested);   // the running session is untouched

        h.Runner.Complete(ExportOutcome.Succeeded);
        Assert.Equal(ExportOutcome.Succeeded, (await first.WaitAsync(Timeout)).Outcome);
    }

    // ── 2 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task SecondExport_IsRejected_WhileCancelling()
    {
        var h = new Harness();
        var first = h.Coordinator.StartAsync(h.Request(h.Runner));
        Assert.True(h.Coordinator.Cancel());
        Assert.Equal(ExportState.Cancelling, h.Coordinator.State);

        var second = new FakeRunner();
        var rejected = await h.Coordinator.StartAsync(h.Request(second)).WaitAsync(Timeout);

        Assert.Equal(ExportOutcome.Rejected, rejected.Outcome);
        Assert.Equal(0, second.Invocations);
        Assert.Equal(ExportState.Cancelling, h.Coordinator.State);

        h.Runner.Complete(ExportOutcome.Cancelled);
        await first.WaitAsync(Timeout);

        // Only once Idle is a new export accepted.
        var third = new FakeRunner();
        var accepted = h.Coordinator.StartAsync(h.Request(third));
        Assert.Equal(1, third.Invocations);
        third.Complete(ExportOutcome.Succeeded);
        Assert.Equal(ExportOutcome.Succeeded, (await accepted.WaitAsync(Timeout)).Outcome);
    }

    // ── 3 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Cancel_DoesNotTransitionToIdle_BeforeTheRunnerExits()
    {
        var h = new Harness();
        var session = h.Coordinator.StartAsync(h.Request(h.Runner));

        Assert.True(h.Coordinator.Cancel());
        Assert.True(h.Runner.Token.IsCancellationRequested);

        // The worker is still "killing FFmpeg / deleting temp": give it real time.
        await Task.Delay(200);
        Assert.Equal(ExportState.Cancelling, h.Coordinator.State);
        Assert.False(session.IsCompleted);
        Assert.Equal(0, h.CompletedCount);
        Assert.DoesNotContain(ExportState.Idle, h.Transitions);

        Assert.False(h.Coordinator.Cancel());   // a second Cancel is not a second transition

        h.Runner.Complete(ExportOutcome.Cancelled);
        var done = await session.WaitAsync(Timeout);

        Assert.Equal(ExportState.Idle, h.Coordinator.State);
        Assert.Equal(new[] { ExportState.Running, ExportState.Cancelling, ExportState.Idle }, h.Transitions.ToArray());
        Assert.True(done.CancelRequested);
    }

    // ── 4 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TokenSource_IsNotDisposed_UntilTheRunnerHasFinished()
    {
        var h = new Harness();
        var session = h.Coordinator.StartAsync(h.Request(h.Runner));
        var token = h.Runner.Token;

        h.Coordinator.Cancel();

        // While the runner is alive its token must stay fully usable: WaitHandle and Register both
        // throw ObjectDisposedException on a disposed source (the CANCELREG_01 hang).
        Exception? whileRunning = Record.Exception(() =>
        {
            _ = token.WaitHandle;
            using var reg = token.Register(() => { });
        });
        Assert.Null(whileRunning);

        h.Runner.Complete(ExportOutcome.Cancelled);
        await session.WaitAsync(Timeout);

        // ...and it IS disposed afterwards — by the coordinator, the only owner.
        Assert.Throws<ObjectDisposedException>(() => _ = token.WaitHandle);
    }

    // ── 5 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task SuccessfulExport_ReturnsToIdle_ExactlyOnce()
    {
        var h = new Harness();
        var session = h.Coordinator.StartAsync(h.Request(h.Runner));
        h.Runner.Complete(ExportOutcome.Succeeded);
        var done = await session.WaitAsync(Timeout);

        Assert.Equal(ExportOutcome.Succeeded, done.Outcome);
        Assert.Null(done.Error);
        Assert.False(done.CancelRequested);
        h.AssertSingleIdle();
    }

    // ── 6 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task FailedExport_ReturnsToIdle_ExactlyOnce()
    {
        var h = new Harness();
        var session = h.Coordinator.StartAsync(h.Request(h.Runner));
        h.Runner.Complete(ExportOutcome.Failed);
        Assert.Equal(ExportOutcome.Failed, (await session.WaitAsync(Timeout)).Outcome);
        h.AssertSingleIdle();
    }

    [Fact]
    public async Task ThrowingRunner_IsFailed_NotSilent_AndReturnsToIdle_ExactlyOnce()
    {
        var h = new Harness();
        var boom = new InvalidOperationException("graph build exploded");
        var session = h.Coordinator.StartAsync(h.Request(h.Runner));
        h.Runner.Fail(boom);
        var done = await session.WaitAsync(Timeout);

        Assert.Equal(ExportOutcome.Failed, done.Outcome);
        Assert.Same(boom, done.Error);                                   // handed to the caller
        Assert.Contains(h.Faults.Captured, f => ReferenceEquals(f.Exception, boom));   // and recorded
        h.AssertSingleIdle();
    }

    [Fact]
    public async Task SynchronouslyThrowingRunner_IsFailed_AndReturnsToIdle()
    {
        var h = new Harness();
        var boom = new InvalidOperationException("threw before its first await");
        var runner = new DelegateExportRunner(_ => throw boom);
        var done = await h.Coordinator.StartAsync(h.Request(runner)).WaitAsync(Timeout);

        Assert.Equal(ExportOutcome.Failed, done.Outcome);
        Assert.Same(boom, done.Error);
        h.AssertSingleIdle();
    }

    // ── 7 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task CancelledExport_ReturnsToIdle_ExactlyOnce()
    {
        var h = new Harness();
        var session = h.Coordinator.StartAsync(h.Request(h.Runner));
        h.Coordinator.Cancel();
        h.Runner.Complete(ExportOutcome.Cancelled);
        var done = await session.WaitAsync(Timeout);

        Assert.Equal(ExportOutcome.Cancelled, done.Outcome);
        h.AssertSingleIdle();
    }

    [Fact]
    public async Task RunnerThrowingOperationCanceled_IsCancelled_NotFailed_AndNotReported()
    {
        var h = new Harness();
        var session = h.Coordinator.StartAsync(h.Request(h.Runner));
        h.Coordinator.Cancel();
        h.Runner.Fail(new OperationCanceledException(h.Runner.Token));
        var done = await session.WaitAsync(Timeout);

        Assert.Equal(ExportOutcome.Cancelled, done.Outcome);
        Assert.IsAssignableFrom<OperationCanceledException>(done.Error);
        Assert.Empty(h.Faults.Captured);   // FAULTTIER_01: a cancel is never a fault
        h.AssertSingleIdle();
    }

    // ── 8 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Shutdown_RequestsCancellation_AndIsBounded()
    {
        var h = new Harness();
        var session = h.Coordinator.StartAsync(h.Request(h.Runner));   // ignores its token: never exits

        var sw = Stopwatch.StartNew();
        bool stopped = await h.Coordinator.ShutdownAsync(TimeSpan.FromMilliseconds(150)).WaitAsync(Timeout);
        sw.Stop();

        Assert.False(stopped);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"shutdown took {sw.Elapsed}");
        Assert.True(h.Runner.Token.IsCancellationRequested);
        Assert.Equal(ExportState.Cancelling, h.Coordinator.State);   // never claims Idle for the pipeline
        Assert.Equal(0, h.CompletedCount);

        h.Runner.Complete(ExportOutcome.Cancelled);
        await session.WaitAsync(Timeout);
        Assert.True(await h.Coordinator.ShutdownAsync(TimeSpan.FromMilliseconds(150)).WaitAsync(Timeout));
        h.AssertSingleIdle();
    }

    [Fact]
    public async Task Shutdown_ReturnsAsSoonAsTheRunnerStops()
    {
        var h = new Harness();
        var runner = new FakeRunner { ExitOnCancel = true };
        var session = h.Coordinator.StartAsync(h.Request(runner));

        var sw = Stopwatch.StartNew();
        bool stopped = await h.Coordinator.ShutdownAsync(TimeSpan.FromSeconds(30)).WaitAsync(Timeout);

        Assert.True(stopped);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(ExportOutcome.Cancelled, (await session.WaitAsync(Timeout)).Outcome);
        Assert.Equal(ExportState.Idle, h.Coordinator.State);
    }

    [Fact]
    public async Task Shutdown_WhenIdle_ReturnsImmediately()
    {
        var h = new Harness();
        Assert.True(await h.Coordinator.ShutdownAsync(TimeSpan.FromSeconds(30)).WaitAsync(Timeout));
        Assert.False(h.Coordinator.Cancel());
        Assert.Empty(h.Transitions);
    }

    // ── 9 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task CompletionNotification_FiresOnce_PerAcceptedSession()
    {
        var h = new Harness();
        var session = h.Coordinator.StartAsync(h.Request(h.Runner));

        // Hammer the gate and Cancel from several threads while it runs.
        var rejects = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            h.Coordinator.Cancel();
            return h.Coordinator.StartAsync(h.Request(new FakeRunner()));
        })));
        Assert.All(rejects, r => Assert.Equal(ExportOutcome.Rejected, r.Outcome));

        h.Runner.Complete(ExportOutcome.Cancelled);
        var done = await session.WaitAsync(Timeout);
        await Task.Delay(50);

        Assert.Equal(1, h.CompletedCount);
        Assert.Same(done, h.LastCompletion);
        Assert.Equal(1, h.Transitions.Count(s => s == ExportState.Cancelling));
        h.AssertSingleIdle();
    }

    [Fact]
    public async Task ThrowingCompletionHandler_IsReported_AndStillReachesIdle()
    {
        var h = new Harness();
        h.Coordinator.Completed += (_, _) => throw new InvalidOperationException("listener bug");
        var session = h.Coordinator.StartAsync(h.Request(h.Runner));
        h.Runner.Complete(ExportOutcome.Succeeded);

        Assert.Equal(ExportOutcome.Succeeded, (await session.WaitAsync(Timeout)).Outcome);
        Assert.Equal(ExportState.Idle, h.Coordinator.State);
        Assert.Contains(h.Faults.Captured, f => f.Tier == FaultTier.Degraded);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public readonly RecordingFaultSink Faults = new();
        public readonly ExportCoordinator Coordinator;
        public readonly FakeRunner Runner = new();
        public readonly ConcurrentQueue<ExportState> TransitionLog = new();
        private int _completed;

        public Harness()
        {
            Coordinator = new ExportCoordinator(Faults);
            Coordinator.StateChanged += (_, s) => TransitionLog.Enqueue(s);
            Coordinator.Completed += (_, c) => { Interlocked.Increment(ref _completed); LastCompletion = c; };
        }

        public List<ExportState> Transitions => TransitionLog.ToList();
        public int CompletedCount => Volatile.Read(ref _completed);
        public ExportCompletion? LastCompletion { get; private set; }

        public ExportRequest Request(IExportRunner runner) => new("test", runner);

        public void AssertSingleIdle()
        {
            Assert.Equal(ExportState.Idle, Coordinator.State);
            Assert.Equal(1, Transitions.Count(s => s == ExportState.Idle));
            Assert.Equal(1, Transitions.Count(s => s == ExportState.Running));
            Assert.Equal(1, CompletedCount);
        }
    }

    /// <summary>A pipeline that runs until the test says otherwise (ignoring its token by default).</summary>
    private sealed class FakeRunner : IExportRunner
    {
        private readonly TaskCompletionSource<ExportOutcome> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _invocations;

        public bool ExitOnCancel { get; init; }
        public int Invocations => Volatile.Read(ref _invocations);
        public CancellationToken Token { get; private set; }

        public Task<ExportOutcome> RunAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocations);
            Token = cancellationToken;
            if (ExitOnCancel)
                cancellationToken.Register(() => _exit.TrySetResult(ExportOutcome.Cancelled));
            return _exit.Task;
        }

        public void Complete(ExportOutcome outcome) => _exit.TrySetResult(outcome);
        public void Fail(Exception ex) => _exit.TrySetException(ex);
    }

    private sealed class RecordingFaultSink : IFaultSink
    {
        private readonly ConcurrentQueue<Fault> _faults = new();
        public IReadOnlyList<Fault> Captured => _faults.ToArray();
        public void Report(Fault fault) => _faults.Enqueue(fault);
    }
}
