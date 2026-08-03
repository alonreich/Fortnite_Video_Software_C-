using System;
using System.Diagnostics;
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
        
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start process: {psi.FileName}");
            }
            
            try { ChildProcessTracker.AddProcess(process); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }

            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);
            
            string output = await outputTask;
            string stderr = await stderrTask;

            return (process.ExitCode, output, stderr);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
            throw;
        }
    }
}
