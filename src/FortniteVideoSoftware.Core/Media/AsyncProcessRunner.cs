// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

public static class AsyncProcessRunner
{
    public static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(
        ProcessStartInfo psi, 
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout.HasValue)
        {
            cts.CancelAfter(timeout.Value);
        }

        using var process = new Process { StartInfo = psi };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {psi.FileName}");
        }

        try { ChildProcessTracker.AddProcess(process); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

        // Reader tasks deliberately carry NO cancellation token: they run to EOF when the child
        // exits and closes its pipes, so both streams always drain fully before this Process
        // object is disposed — no abandoned pipe buffers, no lost stderr tail.
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation or timeout: stop the child through the bounded cooperative ladder
            // (stdin 'q' quit command → 1500 ms grace → Kill(entireProcessTree) → 2000 ms exit
            // confirmation) instead of an instant hard kill, then make sure both reader tasks
            // have completed before the Process object leaves scope. The original
            // OperationCanceledException is re-thrown so caller semantics are unchanged.
            await GracefulProcessTerminator.TerminateAsync(
                process,
                BuildLogTag(psi),
                attemptQuitCommand: WantsStdinQuitCommand(psi)).ConfigureAwait(false);
            await DrainReadersAsync(outputTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        string output = await outputTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        return (process.ExitCode, output, stderr);
    }

    private static string BuildLogTag(ProcessStartInfo psi) =>
        "Process(" + (string.IsNullOrWhiteSpace(psi.FileName)
            ? "?"
            : Path.GetFileNameWithoutExtension(psi.FileName)) + ")";

    /// <summary>
    /// FFmpeg-style interactive tools accept 'q' on stdin as a clean quit; anything else
    /// (and any run without redirected stdin) goes straight to the hard-kill escalation.
    /// </summary>
    private static bool WantsStdinQuitCommand(ProcessStartInfo psi) =>
        psi.RedirectStandardInput
        && !string.IsNullOrEmpty(psi.FileName)
        && psi.FileName.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Waits for both pipe readers to finish, but never longer than
    /// <see cref="GracefulProcessTerminator.HardKillConfirmMs"/> — a caller waiting on a
    /// cancellation must stay strictly bounded.
    /// </summary>
    private static async Task DrainReadersAsync(Task<string> outputTask, Task<string> stderrTask)
    {
        try
        {
            Task drain = Task.WhenAll(outputTask, stderrTask);
            Task finished = await Task.WhenAny(
                drain,
                Task.Delay(GracefulProcessTerminator.HardKillConfirmMs)).ConfigureAwait(false);

            if (finished != drain)
            {
                CoreLogger.Info("AsyncProcessRunner",
                    $"Output readers did not drain within {GracefulProcessTerminator.HardKillConfirmMs} ms; continuing shutdown.");
                _ = drain.ContinueWith(
                    static t => { if (t.IsFaulted && t.Exception != null) CoreLogger.Swallowed(t.Exception); },
                    TaskScheduler.Default);
            }
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
        }
    }
}

