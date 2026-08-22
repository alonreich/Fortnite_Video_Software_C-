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

    /// <summary>
    /// ISSUE_1 — hard ceiling on ONE log entry.
    ///
    /// Without this the 10 MB file cap is not enforceable: a single caller can hand the queue an
    /// arbitrarily large string (the biggest real one today is the 400-line FFmpeg stderr dump in
    /// ProcessWorker, roughly 40 KB), and one entry larger than MaxLogSize would blow the cap the
    /// instant it is written to a freshly rotated, empty file. Oversized entries are truncated
    /// with an explicit marker rather than dropped, so evidence is never silently lost.
    /// </summary>
    private const int MaxSingleEntryBytes = 64 * 1024;

    /// <summary>
    /// ISSUE_1 — hard ceiling on ONE write batch.
    ///
    /// The drain loop used to empty the ENTIRE queue (up to 10,000 entries) into one StringBuilder
    /// before the size check ran, so the batch itself was unbounded in both memory and bytes
    /// written. Bounding the batch is what makes the "rotate before the cap is exceeded" check
    /// meaningful: after a rotation the new file receives at most this much.
    /// </summary>
    private const long MaxBatchBytes = 1L * 1024 * 1024;

    private static string? _cachedLogPath;
    private static Mutex? _globalMutex;
    private static readonly BlockingCollection<string> _logQueue = new(10000);
    private static long _estimatedSize = -1;
    private static long _droppedCount;

    /// <summary>ISSUE_4 — batches lost to disk errors since the last successful write.</summary>
    private static long _failedBatchCount;

    /// <summary>ISSUE_3 — retention sweep runs once per process, on the writer thread.</summary>
    private static bool _startupCleanupDone;
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
                    try { _logQueue.CompleteAdding(); } catch (System.Exception) { /* L7/ISSUE_13: the logger may NEVER log its own failure — that recurses. Loss is surfaced by the _failedBatchCount warning instead. */ }
                    _processTask?.Wait(3000);
                    try { _globalMutex?.Dispose(); } catch (System.Exception) { /* L7/ISSUE_13: the logger may NEVER log its own failure — that recurses. Loss is surfaced by the _failedBatchCount warning instead. */ }
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

    /// <summary>
    /// ISSUE_13 — the App-side twin of <see cref="FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed"/>.
    /// Read the long note on that method before changing either: they are one contract in two
    /// assemblies, because Core cannot reference this class.
    /// INFO gets the exception type, message and source location; the full stack is DEBUG (dev only),
    /// per logging rule L8. Never throws (rule L3).
    /// </summary>
    public static void Swallowed(
        Exception ex,
        [System.Runtime.CompilerServices.CallerMemberName] string member = "",
        [System.Runtime.CompilerServices.CallerFilePath] string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        try
        {
            string where = $"{System.IO.Path.GetFileName(file)}:{line} {member}()";
            Info("SWALLOWED", $"{where} — {ex.GetType().Name}: {ex.Message}");
            Debug("SWALLOWED", $"{where}{Environment.NewLine}{ex}");
        }
        catch (Exception)
        {
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long LastTicks, int Suppressed)> _throttleState = new();
    private static readonly long ThrottleTicks = TimeSpan.FromSeconds(30).Ticks;

    public static void SwallowedThrottled(
        Exception ex,
        [System.Runtime.CompilerServices.CallerMemberName] string member = "",
        [System.Runtime.CompilerServices.CallerFilePath] string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        try
        {
            string where = $"{System.IO.Path.GetFileName(file)}:{line} {member}()";
            long now = DateTime.UtcNow.Ticks;

            bool emit = false;
            int suppressed = 0;
            _throttleState.AddOrUpdate(
                where,
                _ => { emit = true; return (now, 0); },
                (_, prev) =>
                {
                    if (now - prev.LastTicks >= ThrottleTicks)
                    {
                        emit = true;
                        suppressed = prev.Suppressed;
                        return (now, 0);
                    }
                    return (prev.LastTicks, prev.Suppressed + 1);
                });

            if (!emit) return;

            string tail = suppressed > 0 ? $" (+{suppressed} identical in the last 30s)" : string.Empty;
            Info("SWALLOWED", $"{where} — {ex.GetType().Name}: {ex.Message}{tail}");
            Debug("SWALLOWED", $"{where}{Environment.NewLine}{ex}");
        }
        catch (Exception)
        {
        }
    }

    public static void AppendRaw(string line)
    {
        SafeWrite(line + Environment.NewLine);
        try { LogAppended?.Invoke(line); } catch (System.Exception) { /* L7/ISSUE_13: the logger may NEVER log its own failure — that recurses. Loss is surfaced by the _failedBatchCount warning instead. */ }
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
            catch (System.Exception) { /* L7/ISSUE_13: the logger may NEVER log its own failure — that recurses. Loss is surfaced by the _failedBatchCount warning instead. */ }

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
        catch (System.Exception) { /* L7/ISSUE_13: the logger may NEVER log its own failure — that recurses. Loss is surfaced by the _failedBatchCount warning instead. */ }
        finally
        {
            if (acquired && _globalMutex != null)
            {
                try { _globalMutex.ReleaseMutex(); } catch (System.Exception) { /* L7/ISSUE_13: the logger may NEVER log its own failure — that recurses. Loss is surfaced by the _failedBatchCount warning instead. */ }
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
        try { LogAppended?.Invoke(line.TrimEnd()); } catch (System.Exception) { /* L7/ISSUE_13: the logger may NEVER log its own failure — that recurses. Loss is surfaced by the _failedBatchCount warning instead. */ }
    }

    /// <summary>
    /// The ONE funnel every log line passes through. ISSUE_1: oversized entries are truncated here
    /// rather than at each call site, so no future caller can bypass the file-size guarantee.
    /// </summary>
    private static void SafeWrite(string text)
    {
        if (_logQueue.IsAddingCompleted) return;

        try
        {
            if (text.Length > MaxSingleEntryBytes)
            {
                int originalBytes = Encoding.UTF8.GetByteCount(text);
                if (originalBytes > MaxSingleEntryBytes)
                {
                    int keep = Math.Min(text.Length, MaxSingleEntryBytes / 4);
                    text = text[..keep] +
                           $"... [truncated {originalBytes - Encoding.UTF8.GetByteCount(text[..keep])} bytes]" +
                           Environment.NewLine;
                }
            }

            if (!_logQueue.TryAdd(text))
                Interlocked.Increment(ref _droppedCount);
        }
        catch (System.Exception) { /* L7/ISSUE_13: the logger may NEVER log its own failure — that recurses. Loss is surfaced by the _failedBatchCount warning instead. */ }
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
        
        if (!_startupCleanupDone)
        {
            _startupCleanupDone = true;
            CleanupOldLogs();
        }

        foreach (string firstText in _logQueue.GetConsumingEnumerable())
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append(firstText);
                long batchBytes = Encoding.UTF8.GetByteCount(firstText);

                while (batchBytes < MaxBatchBytes && _logQueue.TryTake(out string? moreText))
                {
                    sb.Append(moreText);
                    batchBytes += Encoding.UTF8.GetByteCount(moreText);
                }

                string combinedText = sb.ToString();

                long dropped = Interlocked.Exchange(ref _droppedCount, 0);
                if (dropped > 0)
                {
                    combinedText = $"{_appName} [s:{_sessionId}] {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [WARN] LOGGER - {dropped} log line(s) dropped due to queue saturation.{Environment.NewLine}" + combinedText;
                }

                long failed = Interlocked.Exchange(ref _failedBatchCount, 0);
                if (failed > 0)
                {
                    combinedText = $"{_appName} [s:{_sessionId}] {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [WARN] LOGGER - {failed} log batch(es) were lost to a file write error; logging has recovered.{Environment.NewLine}" + combinedText;
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
                            catch (System.Exception) { /* L7/ISSUE_13: the logger may NEVER log its own failure — that recurses. Loss is surfaced by the _failedBatchCount warning instead. */ }
                        }

                        using var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        using var sw = new StreamWriter(fs, new UTF8Encoding(false));
                        sw.Write(combinedText);
                        _estimatedSize += incomingBytes;

                        int burstCount = 0;
                        while (burstCount < 500
                               && _estimatedSize < MaxLogSize
                               && _logQueue.TryTake(out string? followUp))
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
                        Interlocked.Increment(ref _failedBatchCount);
                        break;
                    }
                    finally
                    {
                        if (acquired) _globalMutex.ReleaseMutex();
                    }
                }
            }
            catch (System.Exception) { /* L7/ISSUE_13: the logger may NEVER log its own failure — that recurses. Loss is surfaced by the _failedBatchCount warning instead. */ }
        }
    }

    private static void CleanupOldLogs()
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            bool IsRotatedLog(string f) =>
                f.EndsWith(".old", StringComparison.OrdinalIgnoreCase) && f.Contains("Fortnite_Video_Software");

            bool IsAuxiliaryLog(string f) =>
                (f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && f.Contains("mpv_debug_")) ||
                f.Contains(".crash.", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".local.log", StringComparison.OrdinalIgnoreCase);

            var oldLogs = Directory.GetFiles(dir, "*.*")
                                   .Where(f => IsRotatedLog(f) || IsAuxiliaryLog(f))
                                   .Select(f => new FileInfo(f))
                                   .OrderByDescending(f => f.CreationTimeUtc)
                                   .ToList();

            DateTime cutoff = DateTime.UtcNow - MaxOldAge;
            long runningTotal = 0;
            int rotatedKept = 0;

            for (int i = 0; i < oldLogs.Count; i++)
            {
                FileInfo f = oldLogs[i];
                runningTotal += f.Length;

                bool rotated = IsRotatedLog(f.FullName);
                bool overCount = false;
                if (rotated)
                {
                    overCount = rotatedKept >= MaxOldLogs;
                    if (!overCount) rotatedKept++;
                }

                bool overSize = runningTotal > MaxTotalOldBytes;
                bool tooOld = f.CreationTimeUtc < cutoff;
                if (overCount || overSize || tooOld)
                {
                    try { f.Delete(); } catch (System.Exception) { /* L7/ISSUE_13: the logger may NEVER log its own failure — that recurses. Loss is surfaced by the _failedBatchCount warning instead. */ }
                }
            }
        }
        catch (System.Exception) { /* L7/ISSUE_13: the logger may NEVER log its own failure — that recurses. Loss is surfaced by the _failedBatchCount warning instead. */ }
    }
}
