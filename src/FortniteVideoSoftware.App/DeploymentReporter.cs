namespace FortniteVideoSoftware.App;

/// <summary>
/// Installer / uninstaller progress reporting.
///
/// ISSUE_2 (audit round 7) — THIS TYPE MUST NOT TOUCH THE LOG FILE DIRECTLY.
///
/// It used to call File.WriteAllText / File.AppendAllText on
/// <see cref="DeploymentFootprint.InstallReportPath"/>. That path is the SAME file RuntimeLog
/// writes to whenever the app is not running from the install directory (see RuntimeLog.LogPath).
/// Three separate faults followed:
///
///  1. No lock. RuntimeLog serialises every process through
///     Global\FortniteVideoSoftwareLogMutex; these calls did not participate, so the two writers
///     raced. File.AppendAllText opens with FileShare.Read, which blocks RuntimeLog's writer from
///     opening the file for Write — RuntimeLog's batch then failed and was discarded, and a
///     rotation File.Move attempted in that window failed too.
///  2. No size cap and no rotation, so installer output bypassed the 10 MB ceiling entirely.
///  3. No error handling, and ResetAsync TRUNCATED the file. ResetAsync is called up to five times
///     in one install/uninstall flow (DeploymentLifecycle 75, 83, 103, 158, 229), so a single run
///     could wipe the user's application log repeatedly. Worse, an IOException from these writes
///     propagated out of AppendFatalAsync, which DeploymentLifecycle invokes from inside catch
///     blocks (846, 862, 889) — a failure to record an error could take the installer down while
///     it was reporting that error.
///
/// Everything now goes through RuntimeLog.AppendRaw, which inherits the shared mutex, the size
/// cap, rotation, retention and the never-throw contract. Console output is unchanged, and the
/// OnProgress event that drives DeploymentProgressWindow is unchanged.
/// </summary>
internal static class DeploymentReporter
{
    public static event Action<string, int?>? OnProgress;

    /// <summary>
    /// Writes a session banner. Despite the name this APPENDS — it must never truncate, because
    /// the target file is the shared application log (see the type summary).
    /// </summary>
    public static async Task ResetAsync(string sessionName)
    {
        try
        {
            string directory = Path.GetDirectoryName(DeploymentFootprint.InstallReportPath) ?? DeploymentFootprint.TempRoot;
            Directory.CreateDirectory(directory);
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

        string header =
            "==============================================================================" + Environment.NewLine +
            "Fortnite Video Software Deployment Report" + Environment.NewLine +
            $"Session : {sessionName}" + Environment.NewLine +
            $"Started : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}" + Environment.NewLine +
            $"Machine : {Environment.MachineName}" + Environment.NewLine +
            $"User    : {Environment.UserName}" + Environment.NewLine +
            $"PID     : {Environment.ProcessId}" + Environment.NewLine +
            $"Report  : {DeploymentFootprint.InstallReportPath}" + Environment.NewLine +
            "==============================================================================" + Environment.NewLine;

        RuntimeLog.AppendRaw(header.TrimEnd('\r', '\n'));

        Console.Write(header);
        await Task.CompletedTask;
    }

    public static Task StepAsync(string phase, string detail, int? percent)
    {
        return WriteAsync("OK", phase, detail, percent);
    }

    public static Task FailAsync(string phase, string detail, int? percent)
    {
        return WriteAsync("FAIL", phase, detail, percent);
    }

    public static async Task AppendFatalAsync(string phase, Exception exception)
    {
        await WriteAsync("FAIL", phase, exception.ToString(), null).ConfigureAwait(false);
    }

    private static Task WriteAsync(string level, string phase, string detail, int? percent)
    {
        string progress = percent.HasValue ? $"{percent.Value,3}% " : "    ";
        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {progress}{phase} - {detail}{Environment.NewLine}";

        RuntimeLog.AppendRaw(line.TrimEnd('\r', '\n'));

        Console.Write(line);

        try { OnProgress?.Invoke(detail, percent); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        return Task.CompletedTask;
    }
}
