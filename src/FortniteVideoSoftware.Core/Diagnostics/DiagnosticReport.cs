// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace FortniteVideoSoftware.Core.Diagnostics;

/// <summary>
/// SYS-DIAGREPORT — THE BUNDLE A USER CAN ACTUALLY SEND YOU.
///
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// <b>WHY THIS EXISTS.</b> The fault tiers (<c>FAULTTIER_01</c>) route every classified failure to
/// a log under <c>%ProgramData%</c>. That is the right destination for the failure and the wrong
/// destination for the DIAGNOSIS: no user has ever navigated to ProgramData, found a rotating log
/// directory, picked the right file and attached it to a bug report. So every encoder failure on
/// every GPU that is not the developer's own has been, in practice, invisible.
/// </para>
///
/// <para>
/// ⚠️ THIS MATTERS MORE HERE THAN IN MOST APPLICATIONS. The core risk in this product is hardware
/// variance: <c>HardwareScanner</c> picks between NVENC, AMF, QSV and d3d11va at runtime, and that
/// matrix is validated on exactly one machine — the one it was written on. "Export fails on some
/// AMD cards" is unactionable. "Export fails on gfx1030 with amf, falling back to libx264, exit
/// code 1, filtergraph stage 3" is a fix.
/// </para>
///
/// <para>
/// <b>WHAT THIS CLASS IS AND IS NOT.</b> It BUILDS a report and writes it to a file the user can
/// attach. It does not upload anything, and there is deliberately no network code here:
/// auto-upload is a consent problem, a privacy problem and a hosting problem, and none of those
/// need solving before the diagnosis problem is. A file the user can find, read in full, and
/// choose to send is the honest version of telemetry.
/// </para>
///
/// <para>
/// ⚠️ <b>THE USER CAN READ EVERY BYTE OF IT, AND THAT CONSTRAINS WHAT GOES IN.</b> It is plain
/// text, not an opaque blob. Paths are reduced to their file names, because a full path carries a
/// Windows user name (<c>C:\Users\alonreich\...</c>) and nothing in a diagnosis needs it. The
/// suite's own log privacy gate (05 §2 SYS-LOGGING) already makes this decision for log lines;
/// this applies the same rule to everything else in the bundle.
/// </para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class DiagnosticReport
{
    private readonly List<(string Section, string Body)> _sections = new();

    /// <summary>Adds a titled block. Order is preserved; the first sections are the ones read first.</summary>
    public DiagnosticReport Add(string section, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        _sections.Add((section, body ?? string.Empty));
        return this;
    }

    /// <summary>
    /// SYS-DIAGREPORT — the machine profile. This is the half of the report that makes a bug
    /// reproducible rather than merely believable.
    /// </summary>
    public DiagnosticReport AddMachineProfile(string appVersion, string? detectedEncoder)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"app version      : {appVersion}");
        sb.AppendLine($"os               : {Environment.OSVersion.VersionString}");
        sb.AppendLine($"64-bit os        : {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"cpu cores        : {Environment.ProcessorCount}");
        sb.AppendLine($"clr              : {Environment.Version}");
        sb.AppendLine($"culture          : {CultureInfo.CurrentCulture.Name}");

        // ⚠️ NOT Environment.UserName, NOT the machine name, NOT the user profile path. None of
        // them help reproduce anything and all of them identify a person.
        sb.AppendLine($"encoder chosen   : {detectedEncoder ?? "(not probed yet)"}");

        try
        {
            sb.AppendLine($"working set      : {Environment.WorkingSet / (1024 * 1024)} MB");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"working set      : unavailable ({ex.GetType().Name})");
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(ex);   // FAULTTIER_02 — no failure is silent.
        }

        return Add("MACHINE", sb.ToString());
    }

    /// <summary>
    /// SYS-DIAGREPORT — the tail of a log file.
    ///
    /// <para>
    /// The TAIL, not the whole file: a log can be tens of megabytes and the useful part of it is
    /// always the end. A report nobody will open because it is 40MB is the same as no report.
    /// </para>
    /// </summary>
    public DiagnosticReport AddLogTail(string logPath, int maxLines = 400)
    {
        string name = Path.GetFileName(logPath);

        try
        {
            if (!File.Exists(logPath)) return Add($"LOG {name}", "(no log file)");

            // Read from the end without loading the whole file: a ring buffer of the last N lines.
            var ring = new string[maxLines];
            int count = 0;

            using (var reader = new StreamReader(new FileStream(
                logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    ring[count % maxLines] = line;
                    count++;
                }
            }

            var sb = new StringBuilder();
            int start = count > maxLines ? count - maxLines : 0;
            if (start > 0) sb.AppendLine($"... {start} earlier line(s) omitted ...");
            for (int i = start; i < count; i++) sb.AppendLine(Redact(ring[i % maxLines]));

            return Add($"LOG {name}", sb.ToString());
        }
        catch (Exception ex)
        {
            // A report that cannot read one log still ships with the rest. This is the one place
            // where continuing past a failure is right: the alternative is no diagnosis at all.
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(ex);   // FAULTTIER_02 — no failure is silent.
            return Add($"LOG {name}", $"(could not be read: {ex.GetType().Name} — {ex.Message})");
        }
    }

    /// <summary>
    /// SYS-DIAGREPORT — removes the Windows user name from a line.
    ///
    /// <para>
    /// ⚠️ Targeted, not clever. It rewrites <c>C:\Users\someone\</c> to <c>C:\Users\&lt;user&gt;\</c>
    /// and leaves everything else alone. A general-purpose scrubber that tried to guess at
    /// "sensitive" content would mangle filtergraphs and exit codes, which are the entire point of
    /// the report — and would still miss things. One known pattern, removed reliably.
    /// </para>
    /// </summary>
    public static string Redact(string? line)
    {
        if (string.IsNullOrEmpty(line)) return string.Empty;

        const string marker = @"\Users\";
        int i = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return line;

        var sb = new StringBuilder(line.Length);
        int from = 0;

        while (i >= 0)
        {
            int nameStart = i + marker.Length;
            int nameEnd = line.IndexOf('\\', nameStart);
            if (nameEnd < 0) nameEnd = line.Length;

            sb.Append(line, from, nameStart - from).Append("<user>");
            from = nameEnd;

            i = line.IndexOf(marker, from, StringComparison.OrdinalIgnoreCase);
        }

        sb.Append(line, from, line.Length - from);
        return sb.ToString();
    }

    /// <summary>Renders the whole report as the text the user sees and sends.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine("FORTNITE VIDEO SOFTWARE — DIAGNOSTIC REPORT");
        sb.AppendLine($"generated {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();
        sb.AppendLine("This file is plain text and contains no personal information beyond what is");
        sb.AppendLine("shown below. Read it before sending it anywhere.");
        sb.AppendLine();

        foreach ((string section, string body) in _sections)
        {
            sb.AppendLine(new string('=', 94));
            sb.AppendLine(section);
            sb.AppendLine(new string('=', 94));
            sb.AppendLine(body.TrimEnd());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// SYS-DIAGREPORT — machine-readable twin of the text above, for a future upload endpoint or an
    /// issue-tracker integration. Kept in step by construction: both render the same sections.
    /// </summary>
    public JsonObject ToJson()
    {
        JsonObject sections = new();
        foreach ((string section, string body) in _sections) sections[section] = body;

        return new JsonObject
        {
            ["generated_utc"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["sections"] = sections,
        };
    }
}
