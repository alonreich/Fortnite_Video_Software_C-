using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FortniteVideoSoftware.App;

public static class RuntimeLog
{
    private static readonly object Sync = new();
    private static bool _initialized;
    private static bool _exitRegistered;
    private static string _appName = "[MAIN APP]";
    private const long MaxLogSize = 10 * 1024 * 1024; // 10 MB
    private static string? _cachedLogPath;
    private static Mutex? _globalMutex;
    
    public static event Action<string>? LogAppended;

    public static string LogPath
    {
        get
        {
            if (_cachedLogPath != null) return _cachedLogPath;

            // DEV MODE: If FVS_DEV_LOG_DIR is set (by dev.cmd), route ALL logs
            // exclusively to the dev log directory. Never write to %TMP%,
            // %PROGRAMDATA%, or the project root in dev mode.
            string? devLogDir = Environment.GetEnvironmentVariable("FVS_DEV_LOG_DIR");
            if (!string.IsNullOrWhiteSpace(devLogDir))
            {
                Directory.CreateDirectory(devLogDir);
                _cachedLogPath = Path.Combine(devLogDir, "Fortnite_Video_Software_DEV.log");
                return _cachedLogPath;
            }

            if (DeploymentFootprint.IsRunningFromInstallPath())
            {
                var paths = FortniteVideoSoftware.Core.Infrastructure.ApplicationPaths.CreateDefault();
                paths.EnsureWritableDirectories();
                _cachedLogPath = Path.Combine(paths.LogsDirectory, "Fortnite_Video_Software.log");
            }
            else
            {
                _cachedLogPath = DeploymentFootprint.InstallReportPath;
            }
            return _cachedLogPath;
        }
    }

    /// <summary>
    /// Returns true if the app is running in dev mode (FVS_DEV_LOG_DIR env var is set).
    /// Used to enable verbose MPV debug logging and other dev-only diagnostics.
    /// </summary>
    public static bool IsDevMode => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FVS_DEV_LOG_DIR"));

    /// <summary>
    /// Returns the dev log directory if in dev mode, otherwise null.
    /// </summary>
    public static string? DevLogDir => Environment.GetEnvironmentVariable("FVS_DEV_LOG_DIR");

    public static void InitializeAppName(string[] args)
    {
        if (args.Any(a => a.Equals("--crop-tool", StringComparison.OrdinalIgnoreCase)))
            _appName = "[CROP TOOLS]";
        else if (args.Any(a => a.Equals("--merger", StringComparison.OrdinalIgnoreCase)))
            _appName = "[VIDEO MERGER]";
        else
            _appName = "[MAIN APP]";
    }

    public static void ResetForProcess()
    {
        lock (Sync)
        {
            // Seed the byte counter from the current file size
            try
            {
                string lp = LogPath;
            }
            catch { /* ignore — counter starts at 0 */ }

            string header = Environment.NewLine +
                $"{_appName} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [INFO] BOOTSTRAP - Started (PID: {Environment.ProcessId}, User: {Environment.UserName})" + Environment.NewLine;

            SafeWrite(header);
            _initialized = true;

            if (!_exitRegistered)
            {
                AppDomain.CurrentDomain.ProcessExit += (s, e) => WriteExitSeparator();
                _exitRegistered = true;
            }
        }
    }

    private static void WriteExitSeparator()
    {
        lock (Sync)
        {
            string separator = Environment.NewLine + 
                               "---------------------------------------------------------------------------------------------------------------------------" + Environment.NewLine + 
                               Environment.NewLine + 
                               "---------------------------------------------------------------------------------------------------------------------------" + Environment.NewLine;
            SafeWrite(separator);
        }
    }

    public static void Info(string step, string detail)
    {
        Write("INFO", step, detail);
    }

    public static void Success(string step, string detail)
    {
        Write("SUCCESS", step, detail);
    }

    public static void Fail(string step, string detail)
    {
        Write("FAIL", step, detail);
    }

    public static void AppendRaw(string line)
    {
        lock (Sync)
        {
            SafeWrite(line + Environment.NewLine);
            LogAppended?.Invoke(line);
        }
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

            string line = $"{_appName} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{level}] {step} - {detail}{Environment.NewLine}";
            SafeWrite(line);
            LogAppended?.Invoke(line.TrimEnd());
        }
    }

    private static void SafeWrite(string text)
    {
        _globalMutex ??= new Mutex(false, "Global\\FortniteVideoSoftwareLogMutex");

        bool acquired = false;
        try
        {
            try { acquired = _globalMutex.WaitOne(2000); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return; // Fail safe

            RotateLogIfNeeded();

            using var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var sw = new StreamWriter(fs, new UTF8Encoding(false));
            sw.Write(text);
        }
        catch (Exception)
        {
            // Ignore write errors to prevent crashing the app
        }
        finally
        {
            if (acquired) _globalMutex.ReleaseMutex();
        }
    }

    private static void RotateLogIfNeeded()
    {
        try
        {
            string lp = LogPath;
            if (!File.Exists(lp)) return;
            var fileInfo = new FileInfo(lp);
            if (fileInfo.Length < MaxLogSize) return;

            // Log exceeds 10MB, rotate it to prevent memory spikes
            string oldLog = lp + ".old";
            if (File.Exists(oldLog)) File.Delete(oldLog);
            File.Move(lp, oldLog);
        }
        catch
        {
            // Ignore rotation errors (e.g. file locked)
        }
    }
}
