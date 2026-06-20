using System.Text;

namespace FortniteVideoSoftware.App;

internal static class DeploymentReporter
{
    private static readonly object Sync = new();

    public static event Action<string, int?>? OnProgress;

    public static async Task ResetAsync(string sessionName)
    {
        string directory = Path.GetDirectoryName(DeploymentFootprint.InstallReportPath) ?? DeploymentFootprint.TempRoot;
        Directory.CreateDirectory(directory);

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

        lock (Sync)
        {
            File.WriteAllText(DeploymentFootprint.InstallReportPath, header, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

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

        lock (Sync)
        {
            File.AppendAllText(DeploymentFootprint.InstallReportPath, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Console.Write(line);
        OnProgress?.Invoke(detail, percent);
        return Task.CompletedTask;
    }
}
