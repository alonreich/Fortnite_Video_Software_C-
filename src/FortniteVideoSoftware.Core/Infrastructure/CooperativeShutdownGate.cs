// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.Core.Infrastructure;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// PIPEDEDUP_01 — the single-flight gate around <see cref="GracefulProcessTerminator"/>, and the
/// one place the "stop an FFmpeg child safely" mechanics are written down.
///
/// WHAT WAS WRONG. <c>ProcessWorker</c> and <c>MergerWorker</c> run structurally identical FFmpeg
/// pipelines and each held its OWN copy of this logic: the same `_shutdownGate` /
/// `_shutdownTarget` / `_shutdownTask` triple, the same <c>BeginCooperativeShutdown</c>, the same
/// <c>AwaitActiveShutdownAsync</c>, the same <c>ReadExitCodeSafely</c>.
///
/// ⚠️ THAT DUPLICATION IS NOT A STYLE COMPLAINT. IT HAS ALREADY SHIPPED TWO DEFECTS:
///   1. FFMPEGSTOP_01 — <c>ReadExitCodeSafely</c> existed in both files with the same name, the
///      same signature and the same doc comment. MergerWorker's was hardened to route through
///      <see cref="GracefulProcessTerminator"/>; ProcessWorker's was left calling
///      <c>Kill(entireProcessTree: true)</c> raw. The PRIMARY export path therefore shipped with no
///      cooperative FFmpeg shutdown at all, producing unplayable MP4s (no moov atom) on cancel,
///      while the secondary path had the fix.
///   2. RESCUE_01 — <c>TryRescueFinishedRender</c> was a byte-identical copy in both files, and
///      both carried a File.Exists-then-File.Move race plus an unbounded index loop that the
///      neighbouring <c>ResolveOutputPath</c> already documented as a fixed defect.
///
/// Every future fix to either pipeline had a coin-flip chance of landing in only one copy. One
/// copy, here, is what stops that.
///
/// ⚠️ WHAT IS DELIBERATELY *NOT* UNIFIED. The two RunAsync pipelines are different jobs — the
/// editor export applies trim/cuts/speed/memes/overlays/voice-over to ONE source, the merger
/// concatenates N sources with normalisation. Only the PROCESS MECHANICS live here. Log tags and
/// the quit-command policy stay caller-supplied, because they genuinely differ per pipeline and
/// crash digests are read by tag.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
internal sealed class CooperativeShutdownGate
{
    private readonly object _shutdownGate = new();
    private Process? _shutdownTarget;
    private Task? _shutdownTask;

    /// <summary>
    /// Starts the bounded cooperative shutdown ladder for <paramref name="proc"/> exactly once,
    /// off the calling thread.
    ///
    /// <para>Fire-and-forget on purpose: the ladder is strictly bounded
    /// (<see cref="GracefulProcessTerminator.CooperativeGraceMs"/> +
    /// <see cref="GracefulProcessTerminator.HardKillConfirmMs"/>) but it DOES wait, and callers
    /// include <c>Cancel()</c> (UI thread) and cancellation-token registrations, which run
    /// synchronously on whichever thread cancels. Neither may block.</para>
    ///
    /// <para>Single-flight: Cancel(), the token registrations and Dispose() may all race each
    /// other, but only ONE ladder ever runs per process. Faults are observed so a background stop
    /// can never surface as an unobserved task exception.</para>
    /// </summary>
    internal void Begin(Process? proc, string logTag, bool attemptQuitCommand)
    {
        if (proc == null) return;

        Task? started;
        lock (_shutdownGate)
        {
            if (ReferenceEquals(_shutdownTarget, proc) && _shutdownTask != null) return;
            _shutdownTarget = proc;
            _shutdownTask = Task.Run(() =>
                GracefulProcessTerminator.TerminateAsync(proc, logTag, attemptQuitCommand: attemptQuitCommand));
            started = _shutdownTask;
        }

        _ = started.ContinueWith(
            static t => { if (t.IsFaulted && t.Exception != null) CoreLogger.Swallowed(t.Exception); },
            TaskScheduler.Default);
    }

    /// <summary>
    /// Awaits the in-flight shutdown ladder, if any. Bounded by the ladder itself; a no-op when no
    /// ladder is running. Callers await this before reading an exit code, so the code comes from a
    /// process that has actually finished finalizing its output rather than one still writing its
    /// trailer, and before deleting a scratch directory, so no file still has a live handle.
    /// </summary>
    internal async Task AwaitActiveAsync()
    {
        Task? pending;
        lock (_shutdownGate) { pending = _shutdownTask; }
        if (pending == null) return;
        try { await pending; }
        catch (Exception ex) { CoreLogger.Swallowed(ex); }
    }

    /// <summary>
    /// ISSUE_04 — reads a child process's exit code without ever throwing.
    ///
    /// Callers reach here after a CANCELLABLE wait whose OperationCanceledException is
    /// deliberately swallowed, so the process may still be dying (Kill is asynchronous) and
    /// <c>ExitCode</c> would throw InvalidOperationException. Give it a bounded grace period, then
    /// fall back to the -1 sentinel rather than letting that exception masquerade as a pipeline
    /// crash.
    ///
    /// <para>⚠️ <paramref name="attemptQuitCommand"/> IS CALLER POLICY AND THE TWO PIPELINES
    /// DISAGREE ON PURPOSE. MergerWorker passes false — its caller has already attempted the
    /// cooperative stop, so a second 'q' would only burn the grace budget. ProcessWorker passes
    /// true for encoders, because its cancellable wait can return with the ladder unfinished, and
    /// false for the analysis children (null-muxer probes, the single-frame thumbnail grab) that
    /// have nothing to finalize. Do NOT collapse this to one default.</para>
    /// </summary>
    internal static int ReadExitCodeSafely(Process proc, string logTag, int graceMs, bool attemptQuitCommand)
    {
        try
        {
            if (!proc.HasExited)
            {
                GracefulProcessTerminator.Terminate(
                    proc,
                    logTag,
                    attemptQuitCommand: attemptQuitCommand,
                    cooperativeGraceMs: graceMs,
                    hardKillConfirmMs: 2000);
            }
        }
        catch (Exception ex)
        {
            CoreLogger.Debug(logTag, $"Could not confirm process exit: {ex.Message}");
        }

        try { return proc.HasExited ? proc.ExitCode : -1; }
        catch (Exception ex)
        {
            CoreLogger.Debug(logTag, $"Exit code unavailable: {ex.Message}");
            return -1;
        }
    }
}
