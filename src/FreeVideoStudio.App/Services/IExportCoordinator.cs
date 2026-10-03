// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// EXPORTSESSION_02 — the three states an export session can be in. There is no fourth.
///
/// <para>
/// <c>Running -> Cancelling -> Idle</c>. Cancelling is NOT Idle: the token has been signalled but the
/// pipeline (FFmpeg kill grace, reader-pipe drain, job temp directory delete) has not finished
/// unwinding. Nothing may re-arm PROCESS until the coordinator itself reports Idle.
/// </para>
/// </summary>
public enum ExportState
{
    Idle,
    Running,
    Cancelling
}

/// <summary>EXPORTSESSION_02 — how one export session ended.</summary>
public enum ExportOutcome
{
    /// <summary>The render finished and was delivered.</summary>
    Succeeded,

    /// <summary>The render failed, or the runner threw (see <see cref="ExportCompletion.Error"/>).</summary>
    Failed,

    /// <summary>The user (or shutdown) cancelled, and the pipeline has stopped.</summary>
    Cancelled,

    /// <summary>
    /// Pre-flight did not start a render: nothing loaded, no output folder chosen, or the user backed
    /// out of the high-segment-count prompt. The runner has already told the user why.
    /// </summary>
    Declined,

    /// <summary>
    /// The single-flight gate refused this request because another session was Running or
    /// Cancelling. No state transition happened and no completion notification fired.
    /// </summary>
    Rejected
}

/// <summary>
/// EXPORTSESSION_02 — the work an export session performs. The coordinator owns WHEN it runs and
/// for how long its token lives; the runner owns WHAT it does (payload, FFmpeg graph, encoder
/// policy, dialogs). The token is valid — never disposed — until the returned Task has completed.
/// </summary>
public interface IExportRunner
{
    Task<ExportOutcome> RunAsync(CancellationToken cancellationToken);
}

/// <summary>Adapts a delegate to <see cref="IExportRunner"/> (the Main App's PROCESS pipeline).</summary>
public sealed class DelegateExportRunner : IExportRunner
{
    private readonly Func<CancellationToken, Task<ExportOutcome>> _run;

    public DelegateExportRunner(Func<CancellationToken, Task<ExportOutcome>> run)
        => _run = run ?? throw new ArgumentNullException(nameof(run));

    public Task<ExportOutcome> RunAsync(CancellationToken cancellationToken) => _run(cancellationToken);
}

/// <summary>EXPORTSESSION_02 — one immutable request to export. Created by the window, consumed once.</summary>
/// <param name="Origin">Who asked (for the log), e.g. "PROCESS button".</param>
/// <param name="Runner">The pipeline to run under the coordinator's token.</param>
public sealed record ExportRequest(string Origin, IExportRunner Runner);

/// <summary>EXPORTSESSION_02 — the final result of one session, delivered exactly once.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Error">
/// The exception the runner threw, if any. An <see cref="OperationCanceledException"/> here means
/// the runner was cancelled by throwing; any other type means it failed by throwing and the caller
/// MUST surface it (the coordinator never swallows it).
/// </param>
/// <param name="CancelRequested">True when <see cref="IExportCoordinator.Cancel"/> reached this session.</param>
public sealed record ExportCompletion(ExportOutcome Outcome, Exception? Error, bool CancelRequested);

/// <summary>
/// EXPORTSESSION_02 — EXPORT LIFECYCLE OWNER (03 §8b FFM-EXPORTLIFETIME).
///
/// <para>
/// Owns the Idle/Running/Cancelling state, the <see cref="CancellationTokenSource"/>, the in-flight
/// Task, the single-flight gate, Cancel, the final result and the exactly-once completion
/// transition. Does NOT own FFmpeg graph construction, encoder policy, ProcessWorker or any control.
/// </para>
/// </summary>
public interface IExportCoordinator
{
    /// <summary>Current state. Thread-safe snapshot.</summary>
    ExportState State { get; }

    /// <summary>Raised on every state transition, on the thread that caused it.</summary>
    event EventHandler<ExportState>? StateChanged;

    /// <summary>Raised exactly once per accepted session, after the Idle transition.</summary>
    event EventHandler<ExportCompletion>? Completed;

    /// <summary>
    /// Starts a session if Idle; otherwise returns <see cref="ExportOutcome.Rejected"/> immediately
    /// without touching the running session. The returned Task completes after the session is Idle
    /// and its token source disposed. It never faults: a runner exception is returned in
    /// <see cref="ExportCompletion.Error"/>.
    /// </summary>
    Task<ExportCompletion> StartAsync(ExportRequest request);

    /// <summary>
    /// Running -> Cancelling: signals the token. Does NOT transition to Idle; that happens only when
    /// the runner's Task completes. Returns true only for the call that performed the transition.
    /// </summary>
    bool Cancel();

    /// <summary>
    /// Shutdown: requests cancellation and waits — bounded by <paramref name="timeout"/> — for the
    /// session to reach Idle. Returns true when nothing is running on return.
    /// </summary>
    Task<bool> ShutdownAsync(TimeSpan timeout);
}
