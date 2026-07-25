using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.App;

/// <summary>
/// On startup, sweeps the Windows Application event log for THIS app's crash / hang
/// entries (Application Error, .NET Runtime, Application Hang, Windows Error Reporting)
/// that occurred since the last sweep and folds them into RuntimeLog — so native crashes
/// that only Windows Error Reporting saw (the ones you'd otherwise dig out of Event Viewer
/// by hand) end up in the same .log file as everything else.
///
/// NativeAOT-safe: shells out to the built-in <c>wevtutil.exe</c> instead of the
/// reflection-heavy <c>System.Diagnostics.EventLog</c> managed API. Reading the Application
/// log does not require elevation.
/// </summary>
internal static class CrashLogDigest
{
    /// <summary>Fire-and-forget: runs off the UI/startup thread so it never delays launch.</summary>
    public static Task RunAsync() => Task.Run(Run);

    private static async Task Run()
    {
        try
        {
            string markerPath = RuntimeLog.LogPath + ".crashdigest";
            DateTime sinceUtc = ReadMarker(markerPath);
            DateTime sweepStartedUtc = DateTime.UtcNow;

            string exe = Path.GetFileName(Environment.ProcessPath ?? "FortniteVideoSoftware.App.exe");
            string sinceIso = sinceUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

            string query =
                "*[System[(Provider[@Name='Application Error'] or Provider[@Name='.NET Runtime'] " +
                "or Provider[@Name='Application Hang'] or Provider[@Name='Windows Error Reporting']) " +
                $"and TimeCreated[@SystemTime>='{sinceIso}']]]";

            var psi = new ProcessStartInfo
            {
                FileName = "wevtutil.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("qe");
            psi.ArgumentList.Add("Application");
            psi.ArgumentList.Add("/q:" + query);
            psi.ArgumentList.Add("/f:text");
            psi.ArgumentList.Add("/rd:true");
            psi.ArgumentList.Add("/c:100");

            using var proc = Process.Start(psi);
            if (proc == null) return;

            var readOutput = proc.StandardOutput.ReadToEndAsync();
            var readError = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(15000))
            {
                try { proc.Kill(); } catch { }
            }

            string output = await readOutput;
            _ = await readError;

            int ingested = 0;
            if (!string.IsNullOrWhiteSpace(output))
            {
                foreach (string block in SplitEvents(output))
                {
                    if (block.IndexOf("FortniteVideoSoftware", StringComparison.OrdinalIgnoreCase) < 0
                        && block.IndexOf(exe, StringComparison.OrdinalIgnoreCase) < 0
                        && block.IndexOf("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) < 0
                        && block.IndexOf("ffprobe.exe", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    RuntimeLog.Fail("EVENTLOG CRASH", Condense(block));
                    ingested++;
                }
            }

            if (ingested > 0)
                RuntimeLog.Info("EVENTLOG DIGEST",
                    $"Folded {ingested} crash/error event(s) from Windows Event Viewer (since {sinceIso}) into this log.");

            WriteMarker(markerPath, sweepStartedUtc);
        }
        catch
        {
        }
    }

    private static IEnumerable<string> SplitEvents(string text)
    {
        var sb = new StringBuilder();
        foreach (string line in text.Split('\n'))
        {
            if (line.StartsWith("Event[", StringComparison.Ordinal) && sb.Length > 0)
            {
                yield return sb.ToString();
                sb.Clear();
            }
            sb.Append(line).Append('\n');
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>Collapse a wevtutil text block into one readable line, dropping boilerplate.</summary>
    private static string Condense(string block)
    {
        var keep = new List<string>();
        foreach (string raw in block.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("Event[", StringComparison.Ordinal)) continue;
            if (StartsWithAny(line, "Log Name:", "Level:", "Opcode:", "Task:", "Keyword:",
                    "User:", "User Name:", "Computer:", "Record Id:", "Correlation:", "Execution:")) continue;
            if (line.Equals("Description:", StringComparison.OrdinalIgnoreCase)) continue;
            keep.Add(line);
        }

        string joined = string.Join(" | ", keep);
        const int cap = 1200;
        if (joined.Length > cap) joined = joined.Substring(0, cap) + " …(truncated)";
        return joined;
    }

    private static bool StartsWithAny(string line, params string[] prefixes)
    {
        foreach (string p in prefixes)
            if (line.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static DateTime ReadMarker(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string s = File.ReadAllText(path).Trim();
                if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime dt))
                    return dt.ToUniversalTime();
            }
        }
        catch { }
        return DateTime.UtcNow.AddDays(-3);
    }

    private static void WriteMarker(string path, DateTime utc)
    {
        try { File.WriteAllText(path, utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)); }
        catch { }
    }
}
