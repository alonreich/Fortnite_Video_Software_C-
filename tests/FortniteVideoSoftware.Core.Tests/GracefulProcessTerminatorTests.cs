using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Media;
using Xunit;

namespace FortniteVideoSoftware.Core.Tests;

/// <summary>
/// Covers the cooperative shutdown ladder (GracefulProcessTerminator) and the drain-safe
/// AsyncProcessRunner that uses it. Children are real OS processes (cmd.exe / powershell.exe /
/// ping.exe) so the escalation, the stdin quit command, and the exit confirmations are
/// exercised exactly as they behave against ffmpeg.exe in production.
/// </summary>
public sealed class GracefulProcessTerminatorTests
{
    private static string CmdPath =>
        Environment.ExpandEnvironmentVariables("%SystemRoot%\\System32\\cmd.exe");

    private static string PowerShellPath =>
        Environment.ExpandEnvironmentVariables("%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe");

    private static Process StartCmd(string arguments, bool redirectInput = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = CmdPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
        };
        return Process.Start(psi)!;
    }

    [Fact]
    public async Task TerminateAsync_NullProcess_CompletesImmediately()
    {
        var stopwatch = Stopwatch.StartNew();

        await GracefulProcessTerminator.TerminateAsync(null, "test");

        Assert.True(stopwatch.ElapsedMilliseconds < 500,
            $"Null process must be an immediate no-op (took {stopwatch.ElapsedMilliseconds} ms).");
    }

    [Fact]
    public async Task TerminateAsync_AlreadyExitedProcess_ReturnsWithoutBurningGraceBudgets()
    {
        using var proc = StartCmd("/c exit 0");
        Assert.True(proc.WaitForExit(10_000), "Test child never exited on its own.");

        var stopwatch = Stopwatch.StartNew();
        await GracefulProcessTerminator.TerminateAsync(
            proc, "test", cooperativeGraceMs: 5000, hardKillConfirmMs: 5000);

        Assert.True(stopwatch.ElapsedMilliseconds < 2000,
            $"An already-exited process must return immediately, not consume the grace/confirm budgets (took {stopwatch.ElapsedMilliseconds} ms).");
    }

    [Fact]
    public async Task TerminateAsync_StuckNonInteractiveProcess_HardKillsTreeAndConfirmsExit()
    {
        // ~59 s of guaranteed runtime: still alive when the short grace period below expires.
        using var proc = StartCmd("/c ping -n 60 127.0.0.1 > nul");

        var stopwatch = Stopwatch.StartNew();
        await GracefulProcessTerminator.TerminateAsync(
            proc,
            "test",
            attemptQuitCommand: false,
            cooperativeGraceMs: 200,
            hardKillConfirmMs: 3000);
        stopwatch.Stop();

        Assert.True(proc.HasExited, "The stuck process tree must be dead after the ladder completes.");
        Assert.True(stopwatch.ElapsedMilliseconds >= 200,
            "The cooperative grace period must actually be honored before escalation.");
        Assert.True(stopwatch.ElapsedMilliseconds < 5000,
            $"The whole ladder must stay bounded (took {stopwatch.ElapsedMilliseconds} ms).");
    }

    [Fact]
    public async Task TerminateAsync_InteractiveChild_QuitCommandCausesVoluntaryExit()
    {
        // The child records the FIRST single stdin character it reads, then exits on its own
        // — mirroring how ffmpeg treats an interactive 'q'. If the quit command is written
        // and flushed correctly, the file ends up containing the char code of 'q' (113).
        string marker = Path.Combine(Path.GetTempPath(), "fvs_quit_test_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments =
                    "-NoProfile -NonInteractive -Command \"[Console]::In.Read() | " +
                    "Set-Content -Path '" + marker + "'; Start-Sleep -Milliseconds 200\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi)!;

            // The grace period is generous on purpose: if the quit command works, the child
            // exits long before it elapses, so the hard kill is never reached.
            await GracefulProcessTerminator.TerminateAsync(
                proc, "test", attemptQuitCommand: true, cooperativeGraceMs: 8000, hardKillConfirmMs: 3000);

            Assert.True(proc.HasExited);
            Assert.True(File.Exists(marker),
                "The child never read from stdin — the quit command was not written/flushed.");
            Assert.Equal("113", File.ReadAllText(marker).Trim());
        }
        finally
        {
            try { if (File.Exists(marker)) File.Delete(marker); } catch { }
        }
    }

    [Fact]
    public async Task RunAsync_CompletesNormally_AndReturnsFullyDrainedOutput()
    {
        var psi = new ProcessStartInfo
        {
            FileName = CmdPath,
            Arguments = "/c echo hello-runner",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        (int exitCode, string stdout, string stderr) = await AsyncProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(15));

        Assert.Equal(0, exitCode);
        Assert.Contains("hello-runner", stdout);
    }

    [Fact]
    public async Task RunAsync_Cancellation_StopsChildWithinBoundedTime_AndRethrows()
    {
        var psi = new ProcessStartInfo
        {
            FileName = CmdPath,
            Arguments = "/c ping -n 60 127.0.0.1",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var cts = new CancellationTokenSource(300);
        var stopwatch = Stopwatch.StartNew();

        // cmd.exe is not ffmpeg, so the runner skips the stdin quit command and escalates:
        // 1500 ms grace → Kill(entireProcessTree) → exit confirmation → readers drained → rethrow.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AsyncProcessRunner.RunAsync(psi, timeout: null, cts.Token));

        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds >= 300,
            "The cancellation must have been observed while the child was running.");
        Assert.True(stopwatch.ElapsedMilliseconds < 8000,
            $"Cancellation shutdown must stay bounded (took {stopwatch.ElapsedMilliseconds} ms).");
    }
}
