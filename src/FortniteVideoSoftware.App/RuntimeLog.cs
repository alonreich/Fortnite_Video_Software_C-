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
    // ISSUE_4: retention limits — keep at most 5 rotated logs, no more than ~50 MB of them
    // in total, and none older than 14 days (whichever limit trims first).
    private const int MaxOldLogs = 5;
    private const long MaxTotalOldBytes = 50L * 1024 * 1024;
    private static readonly TimeSpan MaxOldAge = TimeSpan.FromDays(14);
    private static string? _cachedLogPath;
    private static Mutex? _globalMutex;
    private static readonly BlockingCollection<string> _logQueue = new(10000);
    private static long _estimatedSize = -1;
    private static long _droppedCount;
    private static readonly string _sessionId = Guid.NewGuid().ToString("N")[..8];
    private static Task? _processTask;

    static RuntimeLog()
    {
        _processTask = Task.Factory.StartNew(ProcessLogQueue, TaskCreationOptions.LongRunning);
    }
    
    public static event Action<string>? LogAppended;

    public static string LogPath
    {
        get
        {
            if (_cachedLogPath != null) return _cachedLogPath;

            lock (Sync)
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
                AppDomain.CurrentDomain.ProcessExit += (s, e) => {
                    SafeWrite($"{_appName} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [INFO] SHUTDOWN - Process exiting (PID: {Environment.ProcessId}, Session: {_sessionId})." + Environment.NewLine);
                    WriteExitSeparator();
                    try { _logQueue.CompleteAdding(); } catch { }
                    _processTask?.Wait(3000);
                };
                _exitRegistered = true;
            }
            _initialized = true;
        }

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
        string header = Environment.NewLine +
            $"{_appName} v{version} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [INFO] BOOTSTRAP - Started (PID: {Environment.ProcessId}, User: {Environment.UserName}, Session: {_sessionId})" + Environment.NewLine;
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

    /// <summary>
    /// Verbose diagnostics (full commands, full file paths). Persisted only in dev mode
    /// so the normal production log does not expose sensitive paths and commands.
    /// </summary>
    public static void Debug(string step, string detail)
    {
        if (!IsDevMode) return;
        Write("DEBUG", step, detail);
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
        // ISSUE_2: a throwing live-log listener must never disturb the code writing the log.
        try { LogAppended?.Invoke(line); } catch { }
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
        // ISSUE_2: a throwing live-log listener must never disturb the code writing the log.
        try { LogAppended?.Invoke(line.TrimEnd()); } catch { }
    }

    private static void SafeWrite(string text)
    {
        if (!_logQueue.IsAddingCompleted)
        {
            try
            {
                if (!_logQueue.TryAdd(text))
                    Interlocked.Increment(ref _droppedCount);
            }
            catch { }
        }
    }

    private static void ProcessLogQueue()
    {
        try
        {
            _globalMutex ??= new Mutex(false, "Global\\FortniteVideoSoftwareLogMutex");
        }
        catch
        {
            _globalMutex = new Mutex(false);
        }
        
        foreach (string firstText in _logQueue.GetConsumingEnumerable())
        {
            var sb = new StringBuilder();
            sb.Append(firstText);
            
            while (_logQueue.TryTake(out string? moreText))
            {
                sb.Append(moreText);
            }
            
            string combinedText = sb.ToString();

            long dropped = Interlocked.Exchange(ref _droppedCount, 0);
            if (dropped > 0)
            {
                combinedText = $"{_appName} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [WARN] LOGGER - {dropped} log line(s) dropped due to queue saturation.{Environment.NewLine}" + combinedText;
            }

            bool acquired = false;
            try
            {
                try { acquired = _globalMutex.WaitOne(5000); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                {
                    if (!_logQueue.IsAddingCompleted)
                    {
                        try { _logQueue.TryAdd(combinedText); } catch { }
                    }
                    Thread.Sleep(500);
                    continue;
                }

                var fi = new FileInfo(LogPath);
                _estimatedSize = fi.Exists ? fi.Length : 0;
                long incomingBytes = Encoding.UTF8.GetByteCount(combinedText);

                // Rotate BEFORE writing when this batch would push the file past the cap,
                // so a single active log file never grows meaningfully beyond MaxLogSize.
                if (_estimatedSize + incomingBytes >= MaxLogSize)
                {
                    // ISSUE_3: millisecond timestamp + GUID guarantees a unique name even when
                    // two of the apps rotate in the same second, so rotation never silently fails.
                    string oldLog = LogPath + $".{DateTimeOffset.Now.ToUnixTimeMilliseconds()}.{Guid.NewGuid():N}.old";
                    try
                    {
                        if (File.Exists(LogPath))
                        {
                            File.Move(LogPath, oldLog);
                            _estimatedSize = 0;
                            CleanupOldLogs();
                        }
                    }
                    catch { }
                }

                using var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs, new UTF8Encoding(false));
                sw.Write(combinedText);
                _estimatedSize += incomingBytes;
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

    private static void CleanupOldLogs()
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            var oldLogs = Directory.GetFiles(dir, "*.old")
                                   .Where(f => f.Contains("Fortnite_Video_Software"))
                                   .Select(f => new FileInfo(f))
                                   .OrderByDescending(f => f.CreationTimeUtc)
                                   .ToList();

            // ISSUE_4: trim by count, total size, AND age (whichever hits first).
            DateTime cutoff = DateTime.UtcNow - MaxOldAge;
            long runningTotal = 0;
            for (int i = 0; i < oldLogs.Count; i++)
            {
                FileInfo f = oldLogs[i];
                runningTotal += f.Length;
                bool overCount = i >= MaxOldLogs;
                bool overSize = runningTotal > MaxTotalOldBytes;
                bool tooOld = f.CreationTimeUtc < cutoff;
                if (overCount || overSize || tooOld)
                {
                    try { f.Delete(); } catch { }
                }
            }
        }
        catch { }
    }
}
