using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.Core.Infrastructure;

/// <summary>
/// Cooperative, strictly bounded shutdown ladder for subordinate native processes
/// (FFmpeg, FFprobe, wevtutil, ...).
///
/// PROBLEM this solves: calling <c>Process.Kill(entireProcessTree: true)</c> the instant
/// cancellation or a timeout fires terminates the child mid-write, which can leave orphaned
/// grandchild processes, corrupted half-written output files, and unreleased file locks on disk.
///
/// ESCALATION LADDER (every step is time-bounded, so a stuck child can never hang a caller):
///   1. Already exited → return immediately.
///   2. CLEAN STOP — when the child's stdin is redirected, write FFmpeg's interactive quit
///      command ('q') and flush. An interactive encoder then finalizes its output (moov
///      trailer, indexes) and exits on its own.
///   3. Await a voluntary exit for <see cref="CooperativeGraceMs"/> (default 1500 ms).
///   4. ESCALATE — <c>Process.Kill(entireProcessTree: true)</c>, then await exit
///      confirmation for <see cref="HardKillConfirmMs"/> (default 2000 ms).
///
/// The Windows Job Object assigned by <see cref="ChildProcessTracker"/> is untouched and
/// remains the ULTIMATE safeguard against orphaned processes: if even the hard kill fails,
/// the job object tears the whole tree down when this application exits.
/// </summary>
public static class GracefulProcessTerminator
{
    /// <summary>Cooperative grace period: how long the child gets to react to the quit command.</summary>
    public const int CooperativeGraceMs = 1500;

    /// <summary>Fallback limit: how long to await exit confirmation after a hard kill.</summary>
    public const int HardKillConfirmMs = 2000;

    /// <summary>
    /// Async variant of the escalation ladder. Never throws — every failure is swallowed and
    /// logged, because shutdown paths must not surface new exceptions at the caller.
    /// </summary>
    /// <param name="process">The subordinate process to stop. May be null (no-op).</param>
    /// <param name="logTag">Tag used for CoreLogger lines.</param>
    /// <param name="attemptQuitCommand">
    /// Write FFmpeg's 'q' quit command when (and only when) the child's stdin is redirected.
    /// Pass false for non-interactive tools that would only choke on stray stdin.
    /// </param>
    /// <param name="cooperativeGraceMs">Grace period before escalating (default 1500 ms).</param>
    /// <param name="hardKillConfirmMs">Exit-confirmation budget after the kill (default 2000 ms).</param>
    public static async Task TerminateAsync(
        Process? process,
        string logTag,
        bool attemptQuitCommand = true,
        int cooperativeGraceMs = CooperativeGraceMs,
        int hardKillConfirmMs = HardKillConfirmMs)
    {
        if (process is null) return;

        try
        {
            if (HasExitedSafe(process)) return;

            if (attemptQuitCommand)
            {
                TryWriteQuitCommand(process, logTag);
            }

            if (await WaitForExitBoundedAsync(process, cooperativeGraceMs).ConfigureAwait(false))
            {
                CoreLogger.Info(logTag, "Cooperative stop succeeded — process exited cleanly.");
                return;
            }

            CoreLogger.Info(logTag,
                $"Cooperative grace period elapsed ({cooperativeGraceMs} ms) — escalating to hard termination of the process tree.");
            KillProcessTree(process, logTag);

            if (await WaitForExitBoundedAsync(process, hardKillConfirmMs).ConfigureAwait(false))
            {
                CoreLogger.Info(logTag, "Hard termination confirmed — process tree has exited.");
            }
            else
            {
                CoreLogger.Fail(logTag,
                    $"Process did not confirm exit within {hardKillConfirmMs} ms after a hard kill; the ChildProcessTracker job object remains as the final safeguard.");
            }
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
        }
    }

    /// <summary>
    /// Synchronous, equally bounded variant of the ladder for teardown paths that cannot
    /// await (e.g. <c>IDisposable.Dispose</c>). Worst case it blocks for
    /// <paramref name="cooperativeGraceMs"/> + <paramref name="hardKillConfirmMs"/>.
    /// </summary>
    public static void Terminate(
        Process? process,
        string logTag,
        bool attemptQuitCommand = true,
        int cooperativeGraceMs = CooperativeGraceMs,
        int hardKillConfirmMs = HardKillConfirmMs)
    {
        if (process is null) return;

        try
        {
            if (HasExitedSafe(process)) return;

            if (attemptQuitCommand)
            {
                TryWriteQuitCommand(process, logTag);
            }

            if (WaitForExitBounded(process, cooperativeGraceMs))
            {
                CoreLogger.Info(logTag, "Cooperative stop succeeded — process exited cleanly.");
                return;
            }

            CoreLogger.Info(logTag,
                $"Cooperative grace period elapsed ({cooperativeGraceMs} ms) — escalating to hard termination of the process tree.");
            KillProcessTree(process, logTag);

            if (WaitForExitBounded(process, hardKillConfirmMs))
            {
                CoreLogger.Info(logTag, "Hard termination confirmed — process tree has exited.");
            }
            else
            {
                CoreLogger.Fail(logTag,
                    $"Process did not confirm exit within {hardKillConfirmMs} ms after a hard kill; the ChildProcessTracker job object remains as the final safeguard.");
            }
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
        }
    }

    /// <summary>
    /// Clean stop signal: writes FFmpeg's interactive quit command ('q') to the child's
    /// redirected stdin and flushes. No-op (returns false) when stdin is not redirected or
    /// the process is already gone; never throws.
    /// </summary>
    private static bool TryWriteQuitCommand(Process process, string logTag)
    {
        try
        {
            // StandardInput throws when stdin was not redirected — check the start info first.
            if (!process.StartInfo.RedirectStandardInput) return false;
            if (HasExitedSafe(process)) return false;

            StreamWriter stdin = process.StandardInput;
            stdin.Write('q');
            stdin.Flush();
            CoreLogger.Debug(logTag, "Wrote the FFmpeg quit command ('q') to the child's stdin.");
            return true;
        }
        catch (Exception ex)
        {
            CoreLogger.Debug(logTag,
                $"Could not write the stdin quit command ({ex.GetType().Name}: {ex.Message}) — falling through to hard termination.");
            return false;
        }
    }

    private static async Task<bool> WaitForExitBoundedAsync(Process process, int timeoutMs)
    {
        try
        {
            if (HasExitedSafe(process)) return true;
            using var cts = new CancellationTokenSource(timeoutMs);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception)
        {
            return true; // disposed / handle gone — nothing left to wait for
        }
    }

    private static bool WaitForExitBounded(Process process, int timeoutMs)
    {
        try
        {
            return process.WaitForExit(timeoutMs);
        }
        catch (Exception)
        {
            return true; // disposed / handle gone — nothing left to wait for
        }
    }

    private static void KillProcessTree(Process process, string logTag)
    {
        try
        {
            if (!HasExitedSafe(process))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Raced to exit on its own — that is a success, not a failure.
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
        }
    }

    private static bool HasExitedSafe(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception)
        {
            return true; // disposed / no handle — treat as gone
        }
    }
}
