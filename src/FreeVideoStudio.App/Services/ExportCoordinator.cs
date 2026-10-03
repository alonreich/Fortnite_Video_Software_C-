// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Abstractions;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// EXPORTSESSION_02 — the export lifecycle, moved out of MainWindow so it can be tested without a
/// window (EXPORTSESSION_01's guarantees, unchanged, now owned here).
///
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// THE RULES CARRIED OVER FROM EXPORTSESSION_01, AND WHERE EACH ONE LIVES NOW:
///
///   • One export at a time. <see cref="StartAsync"/> rejects while Running OR Cancelling — the
///     Cancelling window is the one the old processButton.IsEnabled "lock" let a second pipeline in.
///   • Cancel is a STATE TRANSITION (Running -> Cancelling), not a UI reset. Idle is reached in
///     exactly one place: <see cref="FinishAsync"/>, after the runner's Task has completed.
///   • The CancellationTokenSource is created in <see cref="StartAsync"/> and disposed ONLY in
///     <see cref="FinishAsync"/>, after the runner's Task has completed. ProcessWorker holds live
///     registrations on it until then; disposing earlier is the CANCELREG_01 hang.
///   • Shutdown requests cancellation and waits, bounded. A session that outlives the bound stays
///     Cancelling — the state never claims Idle on the pipeline's behalf.
///
/// Cancel signals the token UNDER the gate, and Finish clears the source under the same gate before
/// disposing it, so a cancel can never land on a disposed source (no ObjectDisposedException race).
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class ExportCoordinator : IExportCoordinator
{
    private const string Area = "EXPORT";

    private readonly IFaultSink _faults;
    private readonly object _gate = new();

    private ExportState _state = ExportState.Idle;
    private CancellationTokenSource? _cts;
    private Task? _inFlight;
    private bool _cancelRequested;

    public ExportCoordinator(IFaultSink faults)
        => _faults = faults ?? throw new ArgumentNullException(nameof(faults));

    public event EventHandler<ExportState>? StateChanged;
    public event EventHandler<ExportCompletion>? Completed;

    public ExportState State
    {
        get { lock (_gate) { return _state; } }
    }

    public Task<ExportCompletion> StartAsync(ExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Runner);

        var settled = new TaskCompletionSource<ExportCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource cts;

        lock (_gate)
        {
            if (_state != ExportState.Idle)
            {
                return Task.FromResult(new ExportCompletion(ExportOutcome.Rejected, null, false));
            }

            cts = new CancellationTokenSource();
            _cts = cts;
            _cancelRequested = false;
            _state = ExportState.Running;
            _inFlight = settled.Task;
        }

        RaiseStateChanged(ExportState.Running);

        // The runner is invoked synchronously on the caller's thread so a UI runner starts on the
        // UI thread, exactly as ProcessVideoCoreAsync always did.
        _ = RunSessionAsync(request, cts, settled);
        return settled.Task;
    }

    public bool Cancel()
    {
        lock (_gate)
        {
            if (_state != ExportState.Running || _cts == null) return false;

            _state = ExportState.Cancelling;
            _cancelRequested = true;

            try
            {
                _cts.Cancel();
            }
            catch (Exception ex)
            {
                // A registered callback threw (ProcessWorker.Cancel is itself guarded, so this is a
                // defect elsewhere). The token IS cancelled; the session still unwinds to Idle.
                _faults.Degraded(Area,
                    "Stopping the export hit an error. It is still being stopped; the editor keeps working.", ex);
            }
        }

        RaiseStateChanged(ExportState.Cancelling);
        return true;
    }

    public async Task<bool> ShutdownAsync(TimeSpan timeout)
    {
        Cancel();

        Task? inFlight;
        lock (_gate) { inFlight = _inFlight; }
        if (inFlight == null) return true;

        if (!inFlight.IsCompleted)
        {
            await Task.WhenAny(inFlight, Task.Delay(timeout)).ConfigureAwait(false);
        }

        if (!inFlight.IsCompleted)
        {
            _faults.Recoverable(Area,
                $"Shutdown: the export did not stop within {timeout.TotalMilliseconds:F0} ms; continuing teardown. State stays Cancelling.");
            return false;
        }

        return true;
    }

    private async Task RunSessionAsync(
        ExportRequest request, CancellationTokenSource cts, TaskCompletionSource<ExportCompletion> settled)
    {
        ExportOutcome outcome;
        Exception? error = null;

        try
        {
            outcome = await request.Runner.RunAsync(cts.Token);
        }
        catch (OperationCanceledException oce)
        {
            outcome = ExportOutcome.Cancelled;
            error = oce;
        }
        catch (Exception ex)
        {
            // Not swallowed: returned in ExportCompletion.Error, which the caller surfaces to the user
            // (MainWindow: ErrorReporter "Export failed"). This breadcrumb records it in the fault
            // counters even if a future caller forgets to.
            outcome = ExportOutcome.Failed;
            error = ex;
            _faults.Recoverable(Area, $"Export runner ({request.Origin}) threw; returned to the caller as Failed.", ex);
        }

        settled.TrySetResult(Finish(cts, outcome, error));
    }

    /// <summary>
    /// EXPORTSESSION_02 — THE ONE COMPLETION TRANSITION. Runs once per accepted session, strictly
    /// after the runner's Task has completed: Idle, then dispose the source, then notify.
    /// </summary>
    private ExportCompletion Finish(CancellationTokenSource cts, ExportOutcome outcome, Exception? error)
    {
        bool cancelRequested;
        lock (_gate)
        {
            cancelRequested = _cancelRequested;
            if (ReferenceEquals(_cts, cts)) _cts = null;
            _inFlight = null;
            _state = ExportState.Idle;
        }

        // Disposed only now: every registration the runner took on this token is released by the
        // time its Task has completed, and Cancel can no longer reach it (cleared under the gate).
        try { cts.Dispose(); }
        catch (Exception ex) { _faults.Recoverable(Area, "CancellationTokenSource.Dispose threw after the export finished.", ex); }

        var completion = new ExportCompletion(outcome, error, cancelRequested);

        RaiseStateChanged(ExportState.Idle);

        try { Completed?.Invoke(this, completion); }
        catch (Exception ex)
        {
            _faults.Degraded(Area,
                "The export finished, but its result could not be shown. The file on disk is unaffected and the editor keeps working.", ex);
        }

        return completion;
    }

    private void RaiseStateChanged(ExportState state)
    {
        try { StateChanged?.Invoke(this, state); }
        catch (Exception ex)
        {
            _faults.Degraded(Area,
                "The export status display could not update. The export itself is unaffected.", ex);
        }
    }
}
