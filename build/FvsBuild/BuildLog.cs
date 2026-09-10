using System.Text;

namespace FvsBuild;

/// <summary>
/// Mirrors every orchestrator line to the console AND .\build.log, preserving the
/// historical log contract: after a run the FIRST line of build.log is the verdict
/// (OK / WARN / FAIL). The old BuildLog.ps1 wrapper did this from outside the batch
/// through a cmd pipe; doing it inside the orchestrator removes that shell coupling
/// and keeps the log working identically in CI where no console exists.
/// </summary>
internal sealed class BuildLog : IDisposable
{
    private enum Level { Info, Warning, Error, Success, Banner }

    private readonly string _path;
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private int _errorCount;
    private int _warningCount;

    public BuildLog(string path)
    {
        _path = path;
        // Legacy artifact of the pre-C# pipeline; harmless if absent.
        try { if (File.Exists("build.result.txt")) File.Delete("build.result.txt"); } catch (IOException) { }
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public int ErrorCount { get { lock (_gate) return _errorCount; } }
    public int WarningCount { get { lock (_gate) return _warningCount; } }

    public void Info(string line) => Write(line, Level.Info);
    public void Warn(string line) => Write(line, Level.Warning);
    public void Error(string line) => Write(line, Level.Error);
    public void Success(string line) => Write(line, Level.Success);
    public void Banner(string line) => Write(line, Level.Banner);

    private void Write(string line, Level level)
    {
        lock (_gate)
        {
            if (level == Level.Error) _errorCount++;
            if (level == Level.Warning) _warningCount++;
            try
            {
                ConsoleColor previous = Console.ForegroundColor;
                Console.ForegroundColor = level switch
                {
                    Level.Error => ConsoleColor.Red,
                    Level.Warning => ConsoleColor.Yellow,
                    Level.Success => ConsoleColor.Green,
                    Level.Banner => ConsoleColor.Cyan,
                    _ => previous,
                };
                Console.WriteLine(line);
                Console.ForegroundColor = previous;
            }
            catch (IOException)
            {
                // Console handle redirected or gone (CI); the log file still gets the line.
                Console.WriteLine(line);
            }
            _writer.WriteLine(line);
        }
    }

    /// <summary>Writes the one-line verdict as the new first line of the log file.</summary>
    public void WriteVerdictAndClose(int exitCode, TimeSpan duration)
    {
        string label = exitCode != 0 || ErrorCount > 0 ? "FAIL"
            : WarningCount > 0 ? "WARN"
            : "OK";
        string detail = label switch
        {
            "FAIL" => $"- exit code {exitCode}",
            "WARN" => $"- {WarningCount} warning line(s)",
            _ => "- clean success",
        };
        string first = $"{label} {detail} - Fortnite Video Software build {DateTime.Now:yyyy-MM-dd HH:mm:ss} " +
                       $"({(int)duration.TotalMinutes}m{duration.Seconds:D2}s)";
        lock (_gate)
        {
            _writer.Dispose();
            string body = File.ReadAllText(_path);
            File.WriteAllText(_path, first + Environment.NewLine + body, new UTF8Encoding(false));
        }
    }

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }
}
