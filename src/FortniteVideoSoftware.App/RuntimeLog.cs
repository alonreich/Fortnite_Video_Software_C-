using System.Text;

namespace FortniteVideoSoftware.App;

public static class RuntimeLog
{
    private static readonly object Sync = new();
    private static bool _initialized;

    public static string LogPath
    {
        get
        {
            if (DeploymentFootprint.IsRunningFromInstallPath())
            {
                var paths = FortniteVideoSoftware.Core.Infrastructure.ApplicationPaths.CreateDefault();
                paths.EnsureWritableDirectories();
                return Path.Combine(paths.LogsDirectory, "Fortnite_Video_Software.log");
            }
            return DeploymentFootprint.InstallReportPath;
        }
    }

    public static void ResetForProcess()
    {
        lock (Sync)
        {
            string header =
                "==============================================================================" + Environment.NewLine +
                "Fortnite Video Software Runtime Log" + Environment.NewLine +
                $"Started : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}" + Environment.NewLine +
                $"Machine : {Environment.MachineName}" + Environment.NewLine +
                $"User    : {Environment.UserName}" + Environment.NewLine +
                $"PID     : {Environment.ProcessId}" + Environment.NewLine +
                $"Log file: {LogPath}" + Environment.NewLine +
                "==============================================================================" + Environment.NewLine;

            File.WriteAllText(LogPath, header, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _initialized = true;
        }
    }

    public static void Info(string step, string detail)
    {
        Write("INFO", step, detail);
    }

    public static void Success(string step, string detail)
    {
        Write("OK", step, detail);
    }

    public static void Fail(string step, string detail)
    {
        Write("FAIL", step, detail);
    }

    public static void Fail(string step, Exception exception)
    {
        Write("FAIL", step, $"{exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception}");
    }

    private static void Write(string level, string step, string detail)
    {
        lock (Sync)
        {
            if (!_initialized)
            {
                ResetForProcess();
            }

            string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {step} - {detail}{Environment.NewLine}";
            File.AppendAllText(LogPath, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}
