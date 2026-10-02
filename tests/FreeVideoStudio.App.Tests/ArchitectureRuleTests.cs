
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FvsVerify;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// ARCHTEST_01 — THE SPECS' RULES, MADE EXECUTABLE.
///
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// WHY THIS FILE EXISTS. <c>docs/</c> holds 2,030 lines of specification, and it is good — but read
/// it closely and most of it is a post-mortem diary. <c>DOUBLEFIRE_01</c> (a Command and a Click
/// both firing, "invisible to reading"), <c>SLIDER_09</c> (sibling declaration order, "invisible in
/// code review"), <c>QUALITY_04</c> (a readout with no writer, "looked missing rather than
/// broken"), <c>SEEKSTORM_01</c> (310 seeks in 1.74s). Every one was found by a human running the
/// app, sometimes over several diagnosis cycles, and then fenced off with a paragraph.
///
/// A paragraph only works if the next person reads it. A test works whether they do or not, and
/// costs milliseconds. Every rule below is one that a machine can check and a reviewer reliably
/// cannot — that is the entry criterion for this file. Rules requiring judgement stay in prose.
///
/// These run on SOURCE TEXT, deliberately. The defects are all things that compile.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </para>
///
/// <para>
/// <b>HOW TO ADD A RULE.</b> When a fix earns a <c>CHECK_TAG</c> sentinel in <c>dev.cmd</c>, ask
/// whether it could instead be a test here. A sentinel proves a fix has not been DELETED; a test
/// proves it has not been BROKEN. Prefer the test; keep the sentinel when the fix is a
/// configuration value or a comment-documented ordering that no assertion can see.
/// </para>
/// </summary>
public sealed class ArchitectureRuleTests
{
    [Fact]
    public void ProductionNamespacesUseTheProductRoot()
    {
        foreach (string file in Directory.EnumerateFiles(Path.Combine(RepoRoot.Path, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                                 && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
        {
            foreach (Match declaration in Regex.Matches(File.ReadAllText(file), @"(?m)^namespace\s+([\w.]+)"))
                Assert.StartsWith("FreeVideoStudio.", declaration.Groups[1].Value);
        }
    }

    [Fact]
    public void NoControlCarriesBothCommandAndClick()
    {
        var offenders = new List<string>();

        foreach (string file in RepoRoot.SourceFiles(".axaml"))
        {
            string text = File.ReadAllText(file);

            foreach (Match element in Regex.Matches(text, @"<[A-Za-z][^<>]*?/?>", RegexOptions.Singleline))
            {
                string e = element.Value;
                bool hasCommand = Regex.IsMatch(e, @"\sCommand\s*=");
                bool hasClick = Regex.IsMatch(e, @"\sClick\s*=");

                if (hasCommand && hasClick)
                {
                    int line = text.Take(element.Index).Count(c => c == '\n') + 1;
                    offenders.Add($"{RepoRoot.Relative(file)}:{line}  {Condense(e)}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "DOUBLEFIRE_01 — these controls carry BOTH Command and Click. Avalonia raises both on one "
          + "press, so a toggle silently undoes itself and the control 'does nothing'. Keep exactly one:"
          + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void NoRawHexColoursInSharedStyling()
    {
        var offenders = new List<string>();

        foreach (string file in RepoRoot.SourceFiles(".axaml"))
        {
            if (Path.GetFileName(file).Equals("AvaloniaApp.axaml", StringComparison.OrdinalIgnoreCase))
                continue;

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                Match m = Regex.Match(line, @"=""\s*(#[0-9A-Fa-f]{6,8})\s*""");
                if (!m.Success) continue;

                if (line.Contains("AppOnAccentTextBrush", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.Contains("ARCHTEST_ALLOW_HEX", StringComparison.Ordinal)) continue;

                offenders.Add($"{RepoRoot.Relative(file)}:{i + 1}  {m.Groups[1].Value}  {Condense(line)}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Invariant #5 (Zero Raw Hex Styling) — resolve these through a named DynamicResource token "
          + "in AvaloniaApp.axaml. If a literal is genuinely correct, annotate the line with "
          + "ARCHTEST_ALLOW_HEX and say why:"
          + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void ZoompanFilterIsNeverEmitted()
    {
        var offenders = new List<string>();

        foreach (string file in RepoRoot.SourceFiles(".cs"))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!line.Contains("zoompan", StringComparison.OrdinalIgnoreCase)) continue;

                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("///") || trimmed.StartsWith("*")) continue;

                offenders.Add($"{RepoRoot.Relative(file)}:{i + 1}  {Condense(line)}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Invariant #4 — the FFmpeg zoompan filter is banned suite-wide (fatal native heap leaks). "
          + "Use frame-evaluated padding, dynamic scaling, cropping and cas=0.5 instead:"
          + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void UnexplainedEmptyCatchBlocksDoNotIncrease()
    {
        const int Baseline = 25;

        var offenders = new List<string>();

        foreach (string file in RepoRoot.SourceFiles(".cs"))
        {
            string raw = File.ReadAllText(file);
            string code = BlankCommentsAndStrings(raw);

            foreach (Match m in Regex.Matches(
                         code,
                         @"catch\s*(\([^)]*\))?\s*(when\s*\([^)]*\)\s*)?\{(?<body>[^{}]*)\}",
                         RegexOptions.Singleline))
            {
                if (!string.IsNullOrWhiteSpace(m.Groups["body"].Value)) continue;

                string original = raw.Substring(m.Index, m.Length);
                if (original.Contains("//", StringComparison.Ordinal)
                 || original.Contains("/*", StringComparison.Ordinal)) continue;

                int line = code.Take(m.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{RepoRoot.Relative(file)}:{line}");
            }
        }

        Assert.True(offenders.Count <= Baseline,
            $"FAULTTIER_01 — unexplained empty catch blocks went UP: {offenders.Count} found, baseline "
          + $"{Baseline}. Report the failure through IFaultSink (Recoverable / Degraded / Fatal), or "
          + "write a comment inside the block saying why there is nothing to do. If you reduced the "
          + "count, lower the baseline in this test in the same change:"
          + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void EveryCatchBlockReportsSomewhere()
    {
        string[] reportingPath =
        {
            "src/FreeVideoStudio.App/RuntimeLog.cs",
            "src/FreeVideoStudio.Core/Infrastructure/CoreLogger.cs",
            "src/FreeVideoStudio.App/Services/UserFacingFaultSink.cs",
            "src/FreeVideoStudio.Core/Abstractions/IFaultSink.cs",
            "src/FreeVideoStudio.Core/Abstractions/Faults.cs",
            "src/FreeVideoStudio.App/Controls/FloatingNotice.cs",
            "src/FreeVideoStudio.App/NativeDialog.cs",
        };

        const int Baseline = 8;

        var offenders = new List<string>();

        foreach (string file in RepoRoot.SourceFiles(".cs"))
        {
            string relative = RepoRoot.Relative(file).Replace('\\', '/');
            if (reportingPath.Any(a => relative.EndsWith(a, StringComparison.OrdinalIgnoreCase))) continue;

            string raw = File.ReadAllText(file);
            string code = BlankCommentsAndStrings(raw);

            foreach (Match m in Regex.Matches(code, @"\bcatch\b\s*(\([^()]*\))?\s*(when\s*\([^)]*\)\s*)?\{"))
            {
                int start = m.Index + m.Length;
                int depth = 1, j = start;
                while (j < code.Length && depth > 0)
                {
                    if (code[j] == '{') depth++;
                    else if (code[j] == '}') depth--;
                    j++;
                }
                if (depth != 0) continue;

                string body = code[start..(j - 1)];

                string clause = m.Groups[1].Success ? m.Groups[1].Value : string.Empty;
                if (clause.Contains("OperationCanceledException", StringComparison.Ordinal)
                 || clause.Contains("TaskCanceledException", StringComparison.Ordinal)) continue;

                bool reports =
                    body.Contains(".Swallowed(", StringComparison.Ordinal)
                 || body.Contains("Faults.", StringComparison.Ordinal)
                 || body.Contains(".Report(", StringComparison.Ordinal)
                 || Regex.IsMatch(body, @"\b(RuntimeLog|CoreLogger)\s*\.")
                 || Regex.IsMatch(body, @"\bthrow\b")
                 || Regex.IsMatch(body, @"FloatingNotice|Notify|NotifyError|Alert");

                if (reports) continue;

                int line = code.Take(m.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{relative}:{line}");
            }
        }

        Assert.True(offenders.Count <= Baseline,
            $"FAULTTIER_02 — catch blocks that report NOTHING went UP: {offenders.Count} found, "
          + $"baseline {Baseline}. Invariant #9: no failure is silent. Classify it "
          + "(Faults.Recoverable / Degraded / Fatal), or if the outcome really is unchanged call "
          + "RuntimeLog.Swallowed(ex) / CoreLogger.Swallowed(ex), which routes to the sink at the "
          + "Recoverable tier. If you reduced the count, lower the baseline in the same change:"
          + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void EveryProductionSourceFileCarriesTheSpecContract()
    {
        var offenders = new List<string>();
        IReadOnlyList<string> all = RepoRoot.SourceFiles(".cs");

        foreach (string file in all)
        {
            if (file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)) continue;

            string head = ReadFirstLines(file, 6);
            if (!head.Contains("[SPEC CONTRACT]", StringComparison.Ordinal))
                offenders.Add(RepoRoot.Relative(file));
        }

        Assert.True(offenders.Count == 0,
            "SPEC_GOVERNANCE.md §4 — these files are missing the [SPEC CONTRACT] sentinel block at the "
          + $"top ({offenders.Count} of {all.Count}). Add the three-line block naming the spec that "
          + "governs the file:"
          + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void BlockingWaitsOnAsyncCodeDoNotIncrease()
    {
        const int Baseline = 5;

        var sites = new List<string>();

        foreach (string file in RepoRoot.SourceFiles(".cs"))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("///") || trimmed.StartsWith("*")) continue;

                if (Regex.IsMatch(line, @"\.GetAwaiter\(\)\s*\.GetResult\(\)")
                 || Regex.IsMatch(line, @"\.Wait\(\s*\)")
                 || Regex.IsMatch(line, @"(?<!\w)(?:Task|task|_task|\))\.Result(?!\w)"))
                {
                    sites.Add($"{RepoRoot.Relative(file)}:{i + 1}  {Condense(line)}");
                }
            }
        }

        Assert.True(sites.Count <= Baseline,
            $"ASYNCUI_01 — blocking waits on async code went UP: {sites.Count} found, baseline {Baseline}. "
          + "Await it, or move the work off the UI thread. If you genuinely reduced the count, lower "
          + "the baseline in this test in the same change:"
          + Environment.NewLine + string.Join(Environment.NewLine, sites));
    }

    [Fact]
    public void AsyncVoidMethodsDoNotIncrease()
    {
        const int Baseline = 30;

        var sites = new List<string>();

        foreach (string file in RepoRoot.SourceFiles(".cs"))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!Regex.IsMatch(lines[i], @"\basync\s+void\b")) continue;

                string trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("///")) continue;

                sites.Add($"{RepoRoot.Relative(file)}:{i + 1}  {Condense(lines[i])}");
            }
        }

        Assert.True(sites.Count <= Baseline,
            $"ASYNCUI_02 — `async void` count went UP: {sites.Count} found, baseline {Baseline}. Return "
          + "Task unless this is an event handler bound directly to an Avalonia event; if it is a "
          + "handler, its whole body must sit inside one try/catch that reports through IFaultSink:"
          + Environment.NewLine + string.Join(Environment.NewLine, sites));
    }

    [Fact]
    public void ServiceLocatorUsageDoesNotIncrease()
    {
        const int Baseline = 3;

        var sites = new List<string>();

        foreach (string file in RepoRoot.SourceFiles(".cs"))
        {
            if (Path.GetFileName(file).Equals("AppServices.cs", StringComparison.OrdinalIgnoreCase)) continue;

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("AppServices.Current", StringComparison.Ordinal)) continue;

                string trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("///")) continue;

                sites.Add($"{RepoRoot.Relative(file)}:{i + 1}");
            }
        }

        Assert.True(sites.Count <= Baseline,
            $"COMPOSITION_02 — AppServices.Current usage went UP: {sites.Count} found, baseline {Baseline}. "
          + "New code takes its dependencies as constructor parameters. This shim is scheduled for "
          + "deletion at the end of the view-model extraction phase; raise the baseline ONLY when "
          + "wiring an existing legacy window, and lower it whenever one is migrated:"
          + Environment.NewLine + string.Join(Environment.NewLine, sites));
    }


    [Fact]
    public void EveryFixSentinelStillResolves()
    {
        IReadOnlyList<Sentinel> sentinels = SentinelList.Load(RepoRoot.Path, out IReadOnlyList<string> malformed);
        IReadOnlyList<SentinelResult> failures = SentinelList.Check(RepoRoot.Path, sentinels);

        Assert.True(sentinels.Count > 0,
            $"Parsed zero sentinels out of {SentinelList.RelativePath} — the format changed and the "
          + "whole mechanism is silently off.");

        Assert.True(malformed.Count == 0 && failures.Count == 0,
            SentinelList.FormatFailures(malformed, failures));
    }

    [Fact]
    public void DevCmdDelegatesTheSentinelCheckRatherThanParsingIt()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot.Path, "dev.cmd"));

        Assert.DoesNotContain("for %%P in (", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CHECK_TAG", text, StringComparison.Ordinal);
        Assert.Contains("FvsVerify", text, StringComparison.Ordinal);

        Assert.Contains("if errorlevel 1 exit /b 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ImperativeControlLookupsDoNotIncrease()
    {
        const int Baseline = 700;

        int count = 0;
        var perFile = new List<string>();

        foreach (string file in RepoRoot.SourceFiles(".cs"))
        {
            string code = BlankCommentsAndStrings(File.ReadAllText(file));
            int n = Regex.Matches(code, @"\bFindControl\s*<|\bthis\s*\.\s*Get\s*<").Count;
            if (n == 0) continue;

            count += n;
            perFile.Add($"{n,5}  {RepoRoot.Relative(file)}");
        }

        perFile.Sort((a, b) => string.CompareOrdinal(b, a));

        Assert.True(count <= Baseline,
            $"MVVM_01 — imperative control lookups went UP: {count} found, baseline {Baseline}. New "
          + "state belongs in a view-model with a binding, not in a FindControl against a named "
          + "control. If you reduced the count, lower the baseline in this test:"
          + Environment.NewLine + string.Join(Environment.NewLine, perFile));
    }

    [Fact]
    public void WindowCodeBehindDoesNotGrow()
    {
        var ceilings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["GranularSpeedEditorWindow.axaml.cs"] = 8075,
            ["CropToolWindow.axaml.cs"]            = 6490,
            ["MusicWizardWindow.axaml.cs"]         = 5545,
            ["VoiceOverWindow.axaml.cs"]           = 3645,
            ["MainWindow.axaml.cs"]                = 3260,
            ["VideoMergerWindow.axaml.cs"]         = 2240,
            ["PhaseOverlayControl.axaml.cs"]       = 2425,
            ["SettingsWindow.axaml.cs"]            = 940,
        };

        const int LimitForNewFiles = 1000;

        var offenders = new List<string>();

        foreach (string file in RepoRoot.SourceFiles(".axaml.cs"))
        {
            string name = Path.GetFileName(file);
            int lines = File.ReadAllLines(file).Length;
            int ceiling = ceilings.TryGetValue(name, out int c) ? c : LimitForNewFiles;

            if (lines > ceiling)
                offenders.Add($"{name}: {lines} lines (ceiling {ceiling})");
        }

        Assert.True(offenders.Count == 0,
            "MVVM_02 — window code-behind grew past its ceiling. Move the new state into a "
          + "view-model and bind to it; if you legitimately shrank a grandfathered file, lower its "
          + "ceiling in this test in the same change:"
          + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }




    /// <summary>
    /// Replaces the contents of comments and string literals with spaces, preserving offsets and
    /// line breaks so match positions still map to real line numbers.
    ///
    /// <para>Necessary because every rule here matches CODE patterns, and this codebase documents
    /// its rules by quoting the offending pattern in a doc comment. Without this, the docs trip
    /// the tests that enforce them.</para>
    /// </summary>
    private static string BlankCommentsAndStrings(string text)
    {
        char[] buffer = text.ToCharArray();
        int i = 0, n = text.Length;

        while (i < n)
        {
            if (text[i] == '@' && i + 1 < n && text[i + 1] == '"')
            {
                i += 2;
                while (i < n)
                {
                    if (text[i] == '"')
                    {
                        if (i + 1 < n && text[i + 1] == '"') { buffer[i] = ' '; buffer[i + 1] = ' '; i += 2; continue; }
                        i++; break;
                    }
                    if (text[i] != '\n') buffer[i] = ' ';
                    i++;
                }
                continue;
            }

            if (text[i] == '"')
            {
                i++;
                while (i < n && text[i] != '"')
                {
                    if (text[i] == '\\') { buffer[i] = ' '; i++; if (i < n) { buffer[i] = ' '; i++; } continue; }
                    if (text[i] != '\n') buffer[i] = ' ';
                    i++;
                }
                i++;
                continue;
            }

            if (text[i] == '\'')
            {
                i++;
                while (i < n && text[i] != '\'')
                {
                    if (text[i] == '\\') { buffer[i] = ' '; i++; }
                    if (i < n) { buffer[i] = ' '; i++; }
                }
                i++;
                continue;
            }

            if (i + 1 < n && text[i] == '/' && text[i + 1] == '/')
            {
                while (i < n && text[i] != '\n') { buffer[i] = ' '; i++; }
                continue;
            }

            if (i + 1 < n && text[i] == '/' && text[i + 1] == '*')
            {
                while (i < n && !(i + 1 < n && text[i] == '*' && text[i + 1] == '/'))
                {
                    if (text[i] != '\n') buffer[i] = ' ';
                    i++;
                }
                if (i + 1 < n) { buffer[i] = ' '; buffer[i + 1] = ' '; i += 2; }
                continue;
            }

            i++;
        }

        return new string(buffer);
    }

    private static string Condense(string s)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length <= 140 ? s : s[..140] + "…";
    }

    private static string ReadFirstLines(string path, int count)
    {
        using var reader = new StreamReader(path);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < count; i++)
        {
            string? line = reader.ReadLine();
            if (line is null) break;
            sb.AppendLine(line);
        }
        return sb.ToString();
    }
}
