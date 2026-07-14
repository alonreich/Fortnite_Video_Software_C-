using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace FortniteVideoSoftware.App;

public static class RuntimeLog
{
    private static readonly object Sync = new();
    private static bool _initialized;
    private static bool _exitRegistered;
    private static string _appName = "[MAIN APP]";
    private const long MaxLogSize = 10 * 1024 * 1024;
    private static string? _cachedLogPath;
    private static Mutex? _globalMutex;
    private static readonly BlockingCollection<string> _logQueue = new();
    private static long _estimatedSize = -1;

    static RuntimeLog()
    {
        Task.Factory.StartNew(ProcessLogQueue, TaskCreationOptions.LongRunning);
    }
    
    public static event Action<string>? LogAppended;

    public static string LogPath
    {
        get
        {
            if (_cachedLogPath != null) return _cachedLogPath;

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
            if (_initialized) return;
            try
            {
                string lp = LogPath;
            }
            catch { /* ignore — counter starts at 0 */ }

            if (!_exitRegistered)
            {
                AppDomain.CurrentDomain.ProcessExit += (s, e) => WriteExitSeparator();
                _exitRegistered = true;
            }
            _initialized = true;
        }

        string header = Environment.NewLine +
            $"{_appName} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [INFO] BOOTSTRAP - Started (PID: {Environment.ProcessId}, User: {Environment.UserName})" + Environment.NewLine;
        SafeWrite(header);
    }

    private static void WriteExitSeparator()
    {
        string separator = Environment.NewLine + 
                           "---------------------------------------------------------------------------------------------------------------------------" + Environment.NewLine + 
                           Environment.NewLine + 
                           "---------------------------------------------------------------------------------------------------------------------------" + Environment.NewLine;
        SafeWrite(separator);
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
        SafeWrite(line + Environment.NewLine);
        LogAppended?.Invoke(line);
    }

    public static void Fail(string step, Exception exception)
    {
        Write("FAIL", step, $"{exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception}");
    }

    private static void Write(string level, string step, string detail)
    {
        if (!_initialized)
        {
            ResetForProcess();
        }

        string line = $"{_appName} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{level}] {step} - {detail}{Environment.NewLine}";
        SafeWrite(line);
        LogAppended?.Invoke(line.TrimEnd());
    }

    private static void SafeWrite(string text)
    {
        _logQueue.Add(text);
    }

    private static void ProcessLogQueue()
    {
        _globalMutex ??= new Mutex(false, "Global\\FortniteVideoSoftwareLogMutex");
        
        foreach (string firstText in _logQueue.GetConsumingEnumerable())
        {
            var sb = new StringBuilder();
            sb.Append(firstText);
            
            while (_logQueue.TryTake(out string? moreText))
            {
                sb.Append(moreText);
            }
            
            string combinedText = sb.ToString();

            bool acquired = false;
            try
            {
                try { acquired = _globalMutex.WaitOne(2000); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) continue;

                if (_estimatedSize < 0)
                {
                    var fi = new FileInfo(LogPath);
                    _estimatedSize = fi.Exists ? fi.Length : 0;
                }

                if (_estimatedSize >= MaxLogSize)
                {
                    string oldLog = LogPath + $".{DateTimeOffset.Now.ToUnixTimeSeconds()}.old";
                    if (File.Exists(LogPath)) File.Move(LogPath, oldLog);
                    _estimatedSize = 0;
                }

                using var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs, new UTF8Encoding(false));
                sw.Write(combinedText);
                _estimatedSize += Encoding.UTF8.GetByteCount(combinedText);
            }
            catch (Exception)
            {
            }
            finally
            {
                if (acquired) _globalMutex.ReleaseMutex();
            }
        }
    }
}
