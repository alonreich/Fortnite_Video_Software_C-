// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Diagnostics;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// PIPELIFE_01 — THE ONE COPY OF "AN FFMPEG JOB'S LIFETIME".
///
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// WHAT THIS ENDS. <c>ProcessWorker</c> (172KB) and <c>MergerWorker</c> (69KB) are two independent
/// FFmpeg pipelines. Their filtergraph construction is genuinely different and stays separate —
/// that is not duplication, it is two different jobs. What WAS duplicated is the part that has
/// nothing to do with filtergraphs: owning the child process reference, the cancel flag, the
/// single-flight finish notification, and the teardown ladder.
///
/// That shared part had already drifted once, and the repository carries the receipts:
/// <c>PIPEDEDUP_01</c> notes that the twin <c>ReadExitCodeSafely</c> pair "silently diverged
/// between these two files", and <c>FFMPEGSTOP_01</c> notes that <c>ProcessWorker</c> "was a bare
/// WaitForExit -&gt; Kill(tree) -&gt; WaitForExit while the sibling copy in MergerWorker had
/// already been hardened". Each time, one twin was fixed and the other was not.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </para>
///
/// <para>
/// <b>⚠️ PIPELIFE_02 — AND IT HAD DRIFTED AGAIN, IN THE SAME DIRECTION.</b>
/// <c>PROCGATE_01</c> fixed a use-after-dispose race in <c>ProcessWorker</c>: the process field was
/// read twice — once to null-test, once to act on — so the export thread could run
/// <c>_currentProcess = null; proc.Dispose();</c> in between, and the teardown then called
/// <c>Kill()</c> on a disposed <see cref="Process"/>. The fix was a lock-guarded gate with an
/// atomic take.
///
/// <c>MergerWorker</c> never received it. At the time of writing its <c>_currentProcess</c> is a
/// plain unsynchronised field, its <c>Cancel()</c> passes that field straight to the shutdown
/// ladder, and its <c>Dispose()</c> does exactly the two-read null-test-then-use the fix was
/// written to eliminate. Routing both through this type closes it in the Merger and makes a third
/// divergence impossible, because there is no longer a second copy to diverge.
/// </para>
///
/// <para>
/// <b>THREADING.</b> Every member is safe from any thread. <see cref="Cancel"/> is called from the
/// UI thread, cancellation-token registrations run on whichever thread cancelled, and the pipeline
/// itself publishes from a worker — all three touch the process slot, which is the entire reason
/// it is gated rather than volatile. A volatile field would give a consistent READ but still allow
/// two callers to both believe they own the disposal.
/// </para>
/// </summary>
public sealed class FfmpegJobLifetime
{
    private readonly object _procGate = new();
    private readonly CooperativeShutdownGate _shutdown = new();
    private readonly string _logTag;
    private readonly string _processLabel;

    private Process? _currentProcess;
    private volatile bool _isCanceled;
    private bool _finishEmitted;

    /// <param name="logTag">The <c>CoreLogger</c> subsystem tag: "Process" or "Merger".</param>
    /// <param name="processLabel">
    /// How the child is named in shutdown logs: "FFmpeg" or "FFmpeg MERGE". Preserved verbatim
    /// from each pipeline, because the crash digest and the user's log are read by these strings.
    /// </param>
    public FfmpegJobLifetime(string logTag, string processLabel)
    {
        _logTag = logTag;
        _processLabel = processLabel;
    }

    /// <summary>True when this job ended because the user stopped it, not because it failed.</summary>
    public bool WasCanceled => _isCanceled;

    /// <summary>
    /// Marks the job cancelled WITHOUT touching the process. For cancellation-token registrations,
    /// which only need the flag — the ladder is started by <see cref="Cancel"/>.
    /// </summary>
    public void MarkCanceled() => _isCanceled = true;

    /// <summary>PROCGATE_01 — publish the live child process. Pipeline thread only.</summary>
    public void SetCurrentProcess(Process? proc)
    {
        lock (_procGate) { _currentProcess = proc; }
    }

    /// <summary>
    /// PROCGATE_01 — claim the live child process AND clear the slot in one atomic step, so exactly
    /// one caller can ever be responsible for disposing it. Every teardown site uses this instead
    /// of the old <c>_currentProcess = null; proc.Dispose();</c> pair.
    /// </summary>
    public Process? TakeCurrentProcess()
    {
        lock (_procGate) { Process? p = _currentProcess; _currentProcess = null; return p; }
    }

    /// <summary>PROCGATE_01 — one consistent read, for callers that only observe.</summary>
    public Process? PeekCurrentProcess()
    {
        lock (_procGate) { return _currentProcess; }
    }

    /// <summary>
    /// FFMPEGSTOP_01 — starts the bounded cooperative shutdown ladder exactly once, off the calling
    /// thread. Single-flight: <see cref="Cancel"/>, token registrations and <see cref="DisposeJob"/>
    /// may all race, but only one ladder runs per process.
    /// </summary>
    public void BeginCooperativeShutdown(Process? proc, bool attemptQuitCommand = true)
        => _shutdown.Begin(proc, _processLabel, attemptQuitCommand);

    /// <summary>Awaits the in-flight shutdown ladder, if any. Bounded by the ladder itself.</summary>
    public Task AwaitActiveShutdownAsync() => _shutdown.AwaitActiveAsync();

    /// <summary>
    /// The user pressed Cancel. Returns immediately — the ladder runs off-thread — so a cancel can
    /// never hang the UI, but FFmpeg still gets the chance to finalize its container instead of
    /// being shot mid-write.
    /// </summary>
    /// <param name="idleMessage">Logged when no encode was actually running.</param>
    /// <param name="stoppingMessage">Logged when a child process is being stopped.</param>
    public void Cancel(string stoppingMessage, string idleMessage)
    {
        _isCanceled = true;

        // PROCGATE_01 — ONE consistent read, then act on that single reference. Never re-read the
        // field between the null test and the kill; that is the race this whole gate exists for.
        Process? proc = PeekCurrentProcess();

        bool encoding = false;
        if (proc != null)
        {
            try { encoding = !proc.HasExited; }
            catch (Exception ex) { CoreLogger.Swallowed(ex); }
        }

        CoreLogger.Info(_logTag, encoding ? stoppingMessage : idleMessage);

        // The ObjectDisposedException / InvalidOperationException cases a raw Kill would have to
        // catch by hand are handled inside the ladder: it re-checks HasExited and swallows.
        if (proc != null && encoding) BeginCooperativeShutdown(proc);
    }

    /// <summary>
    /// ISSUE_11 — disposing the worker also STOPS the encoder.
    ///
    /// <para>
    /// Callers are expected to call <see cref="Cancel"/> first, but nothing enforces it, so any
    /// path that disposed a worker without cancelling left FFmpeg grinding at full CPU on a render
    /// whose progress window had already gone — the machine stayed hot and loud for a file nobody
    /// would ever receive. Killing the tree here makes teardown self-sufficient.
    /// </para>
    ///
    /// <para>
    /// ⚠️ SYNCHRONOUS on purpose, matching both pipelines' previous behaviour: this returns only
    /// once the tree is dead or the bounded confirmation window has elapsed. Worst case it blocks
    /// for <c>CooperativeGraceMs + HardKillConfirmMs</c>, the same ceiling every other teardown in
    /// this solution accepts, and the ChildProcessTracker job object remains the final safeguard.
    /// </para>
    /// </summary>
    public void DisposeJob()
    {
        // PROCGATE_01 — atomic take, so this can never race the pipeline thread into a double
        // Dispose of the same Process. MergerWorker.Dispose did the two-read version until
        // PIPELIFE_02 routed it here.
        Process? proc = TakeCurrentProcess();
        if (proc == null) return;

        try
        {
            if (!proc.HasExited)
            {
                _isCanceled = true;
                CoreLogger.Info(_logTag,
                    "Worker disposed while the encoder was still running — stopping the FFmpeg process tree (cooperative quit, then hard kill).");
                GracefulProcessTerminator.Terminate(proc, _processLabel, attemptQuitCommand: true);
            }
        }
        catch (Exception ex) { CoreLogger.Swallowed(ex); }

        try { proc.Dispose(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }
    }

    /// <summary>
    /// ISSUE_04 — fires the caller's completion notification exactly once.
    ///
    /// <para>
    /// Single-flight matters more than it looks: <c>CANCELREG_01</c> records a hang in which
    /// <c>EmitFinished</c> never fired at all, leaving the controller's TaskCompletionSource
    /// uncompleted and the caller's await pending forever — overlay gone, PROCESS button dead, no
    /// error on screen. The failure modes at both ends of this call are unrecoverable from the UI,
    /// so it is owned in one place.
    /// </para>
    /// </summary>
    public void EmitFinished(Action<bool, string>? finished, bool success, string message)
    {
        if (_finishEmitted) return;
        _finishEmitted = true;
        finished?.Invoke(success, message);
    }

    /// <summary>True once the completion notification has been delivered.</summary>
    public bool FinishEmitted => _finishEmitted;
}
