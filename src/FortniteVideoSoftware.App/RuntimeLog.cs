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
    private const int MaxOldLogs = 5;
    private const long MaxTotalOldBytes = 50L * 1024 * 1024;
    private static readonly TimeSpan MaxOldAge = TimeSpan.FromDays(14);
    private static string? _cachedLogPath;
    private static Mutex? _globalMutex;
    private static readonly BlockingCollection<string> _logQueue = new(10000);
    private static long _estimatedSize = -1;
    private static long _droppedCount;
    private static readonly string _sessionId = Guid.NewGuid().ToString("N")[..8];
    public static string SessionId => _sessionId;
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
                    try { _globalMutex?.Dispose(); } catch { }
                };
                _exitRegistered = true;
            }
            _initialized = true;
        }

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
        string header = Environment.NewLine +
            $"{_appName} v{version} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [INFO] BOOTSTRAP - Started (PID: {Environment.ProcessId}, Session: {_sessionId})" + Environment.NewLine;
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
        try { LogAppended?.Invoke(line); } catch { }
    }

    /// <summary>
    /// Synchronous, queue-BYPASSING, best-effort direct write to the log file, flushed to
    /// disk immediately. Crash handlers must use this: a native access violation (0xC0000005)
    /// fast-fails the process before the async <see cref="ProcessLogQueue"/> can drain, so a
    /// normal Info/Fail would never reach disk. This writes straight through so the crash line
    /// survives the process dying. Uses the pre-resolved cached path to avoid taking locks in
    /// a possibly-corrupted process state.
    /// </summary>
    public static void EmergencyWrite(string step, string detail)
    {
        bool acquired = false;
        try
        {
            try
            {
                _globalMutex ??= new Mutex(false, "Global\\FortniteVideoSoftwareLogMutex");
                acquired = _globalMutex.WaitOne(500);
            }
            catch (AbandonedMutexException) { acquired = true; }
            catch { }

            string? path = _cachedLogPath;
            if (path == null)
            {
                string? devLogDir = Environment.GetEnvironmentVariable("FVS_DEV_LOG_DIR");
                if (!string.IsNullOrWhiteSpace(devLogDir)) path = Path.Combine(devLogDir, "Fortnite_Video_Software_DEV.log");
                else if (DeploymentFootprint.IsRunningFromInstallPath()) path = Path.Combine(FortniteVideoSoftware.Core.Infrastructure.ApplicationPaths.CreateDefault().LogsDirectory, "Fortnite_Video_Software.log");
                else path = DeploymentFootprint.InstallReportPath;
            }
            
            if (!acquired) path += $".crash.{Environment.ProcessId}.log";

            string line = $"{_appName} [s:{_sessionId}] {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [FATAL] {step} - {detail}{Environment.NewLine}";
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);
        }
        catch { }
        finally
        {
            if (acquired && _globalMutex != null)
            {
                try { _globalMutex.ReleaseMutex(); } catch { }
            }
        }
    }

    public static void Fail(string step, Exception exception)
    {
        Write("FAIL", step, $"{exception.GetType().Name}: {exception.Message}");
        Debug(step, $"{exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception}");
    }

    private static void Write(string level, string step, string detail)
    {
        if (!_initialized)
        {
            ResetForProcess();
        }

        string line = $"{_appName} [s:{_sessionId}] {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{level}] {step} - {detail}{Environment.NewLine}";
        SafeWrite(line);
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
            _cachedLogPath = LogPath + $".{Environment.ProcessId}.local.log";
            _globalMutex = new Mutex(false);
        }
        
        foreach (string firstText in _logQueue.GetConsumingEnumerable())
        {
            try
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
                    combinedText = $"{_appName} [s:{_sessionId}] {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [WARN] LOGGER - {dropped} log line(s) dropped due to queue saturation.{Environment.NewLine}" + combinedText;
                }

                bool written = false;
                while (!written)
                {
                    bool acquired = false;
                    try
                    {
                        try { acquired = _globalMutex.WaitOne(5000); } catch (AbandonedMutexException) { acquired = true; }
                        if (!acquired)
                        {
                            if (_logQueue.IsAddingCompleted) break;
                            Thread.Sleep(500);
                            continue;
                        }

                        var fi = new FileInfo(LogPath);
                        _estimatedSize = fi.Exists ? fi.Length : 0;
                        long incomingBytes = Encoding.UTF8.GetByteCount(combinedText);

                        if (_estimatedSize + incomingBytes >= MaxLogSize)
                        {
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

                        int burstCount = 0;
                        while (burstCount < 500 && _logQueue.TryTake(out string? followUp))
                        {
                            sw.Write(followUp);
                            _estimatedSize += Encoding.UTF8.GetByteCount(followUp);
                            burstCount++;
                        }
                        sw.Flush();
                        written = true;
                    }
                    catch (Exception)
                    {
                        break;
                    }
                    finally
                    {
                        if (acquired) _globalMutex.ReleaseMutex();
                    }
                }
            }
            catch { }
        }
    }

    private static void CleanupOldLogs()
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            var oldLogs = Directory.GetFiles(dir, "*.*")
                                   .Where(f => (f.EndsWith(".old", StringComparison.OrdinalIgnoreCase) && f.Contains("Fortnite_Video_Software")) ||
                                               (f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && f.Contains("mpv_debug_")))
                                   .Select(f => new FileInfo(f))
                                   .OrderByDescending(f => f.CreationTimeUtc)
                                   .ToList();

            DateTime cutoff = DateTime.UtcNow - MaxOldAge;
            long runningTotal = 0;
            for (int i = 0; i < oldLogs.Count; i++)
            {
                FileInfo f = oldLogs[i];
                runningTotal += f.Length;
                bool overCount = i >= MaxOldLogs * 2;
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
