// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace FortniteVideoSoftware.App.Tests;

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
    // ════════════════════════════════════════════════════════════════════════════════════════
    // RULE 1 — DOUBLEFIRE_01. One activation path per control.
    // 04_UI_UX_AVALONIA_SPEC.md §4 (UI-SAFEGUARDS): "A control that carries a Command must NOT
    // also carry a Click handler, and vice versa." On a toggle the second call undoes the first,
    // so the button appears to do nothing. The spec records that this cost several rounds of
    // diagnosis on PlayPauseButton and was found only from a transport trace.
    // ════════════════════════════════════════════════════════════════════════════════════════
    [Fact]
    public void NoControlCarriesBothCommandAndClick()
    {
        var offenders = new List<string>();

        foreach (string file in RepoRoot.SourceFiles(".axaml"))
        {
            string text = File.ReadAllText(file);

            // Match a single XAML element, opening angle bracket to its close, non-greedy.
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

    // ════════════════════════════════════════════════════════════════════════════════════════
    // RULE 2 — Invariant #5, Zero Raw Hex Styling (README.md §2, 04 §1 UI-THEME).
    // "All Avalonia styles, controls, and dynamic templates must resolve colors exclusively
    // through named DynamicResource tokens." A hex literal in shared styling is how a control
    // stops following the theme, and it is invisible until someone switches to light mode.
    // ════════════════════════════════════════════════════════════════════════════════════════
    [Fact]
    public void NoRawHexColoursInSharedStyling()
    {
        var offenders = new List<string>();

        foreach (string file in RepoRoot.SourceFiles(".axaml"))
        {
            // AvaloniaApp.axaml IS the token registry — it is where the hex values are DEFINED,
            // and defining them somewhere is the entire point of forbidding them everywhere else.
            if (Path.GetFileName(file).Equals("AvaloniaApp.axaml", StringComparison.OrdinalIgnoreCase))
                continue;

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                // #AARRGGBB or #RRGGBB inside an attribute value.
                Match m = Regex.Match(line, @"=""\s*(#[0-9A-Fa-f]{6,8})\s*""");
                if (!m.Success) continue;

                // 04 §1 carves out exactly one literal: AppOnAccentTextBrush is pure white on
                // saturated brand buttons, "to guarantee readability". Allowed, and only that.
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

    // ════════════════════════════════════════════════════════════════════════════════════════
    // RULE 3 — Invariant #4, the zoompan ban.
    // README.md §2: "The deprecated FFmpeg zoompan filter is banned suite-wide due to fatal
    // native heap leaks." A ban with no enforcement is a ban that survives exactly as long as
    // nobody is in a hurry.
    // ════════════════════════════════════════════════════════════════════════════════════════
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

                // Prose about the ban is how the ban is communicated; it must not trip it.
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

    // ════════════════════════════════════════════════════════════════════════════════════════
    // RULE 4 — FAULTTIER_01. An empty catch block must at least say why it is empty.
    //
    // The audit counted 917 catch blocks in ~90,000 lines; 87 of them have a literally empty
    // body. 28 carry a comment explaining that there is genuinely nothing left to do (the
    // WinVerifyTrust CLOSE pairing, logger self-guards) — those are fine and stay. The other 59
    // discard the failure with no record and no explanation.
    //
    // A RATCHET, not a clean sheet: 59 sites cannot be triaged correctly in one change, and each
    // one needs a human decision about which tier it belongs to. Every later phase drives this
    // number down as it touches those files. A permanently red test gets deleted, so this test is
    // green today and can only get stricter.
    //
    // NOTE the comment/string blanking below. Without it this rule matches `catch { }` written
    // inside a doc comment — which it did on first draft, reporting IFaultSink.cs's own
    // documentation as an offender.
    // ════════════════════════════════════════════════════════════════════════════════════════
    [Fact]
    public void UnexplainedEmptyCatchBlocksDoNotIncrease()
    {
        const int Baseline = 59;

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

                // Did the ORIGINAL source carry an explanation inside the block?
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

    // ════════════════════════════════════════════════════════════════════════════════════════
    // RULE 5 — every production source file carries the governance sentinel.
    // SPEC_GOVERNANCE.md §4 requires the [SPEC CONTRACT] block at line 1 of every .cs and .axaml
    // code-behind under src/. It is the mechanism that routes the next reader to the right spec,
    // so a file missing it is a file the routing cannot see.
    // ════════════════════════════════════════════════════════════════════════════════════════
    [Fact]
    public void EveryProductionSourceFileCarriesTheSpecContract()
    {
        var offenders = new List<string>();
        IReadOnlyList<string> all = RepoRoot.SourceFiles(".cs");

        foreach (string file in all)
        {
            // Generated Avalonia partials are written by the build, not by us.
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

    // ════════════════════════════════════════════════════════════════════════════════════════
    // RULE 6 — ASYNCUI_01. No blocking wait on an async result in the App layer.
    // The audit counted 51 uses of .Result / .Wait() / GetAwaiter().GetResult() alongside 106
    // Dispatcher.UIThread call sites. On the UI thread each one is a deadlock of exactly the
    // shape SEEKSTORM_01 describes: the UI thread waiting on work that needs the UI thread.
    //
    // This is a RATCHET, not a clean sheet. The baseline is recorded below and may only ever go
    // DOWN. A clean-sheet assertion on day one would be a failing test nobody can fix in one
    // sitting, and a permanently failing test is a test that gets deleted.
    // ════════════════════════════════════════════════════════════════════════════════════════
    [Fact]
    public void BlockingWaitsOnAsyncCodeDoNotIncrease()
    {
        // Measured against this tree. Five sites, all in teardown/extract paths.
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

    // ════════════════════════════════════════════════════════════════════════════════════════
    // RULE 7 — ASYNCUI_02. `async void` only on real event handlers.
    // An async void that throws bypasses every catch in the call stack and lands in
    // AppDomain.UnhandledException — i.e. it takes the process down from a background
    // continuation, with a log line written by the emergency handler and nothing on screen.
    // Also a ratchet: 36 at the time of writing.
    // ════════════════════════════════════════════════════════════════════════════════════════
    [Fact]
    public void AsyncVoidMethodsDoNotIncrease()
    {
        // Measured against this tree (an earlier grep said 36; that count included comments).
        const int Baseline = 31;

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

    // ════════════════════════════════════════════════════════════════════════════════════════
    // RULE 8 — COMPOSITION_02. AppServices.Current is a migration shim that must shrink.
    // It is a service locator, kept only because ~25,000 lines of code-behind are constructed by
    // Avalonia's lifetime and cannot take constructor arguments. Without a ratchet it becomes
    // permanent, because reaching for a static is always the shortest path.
    // ════════════════════════════════════════════════════════════════════════════════════════
    [Fact]
    public void ServiceLocatorUsageDoesNotIncrease()
    {
        // Rises only when a legacy window is wired up; falls to 0 as each view-model is extracted.
        //
        // 2 — both in MainWindow, wiring the ProjectSession (PROJSESSION_01) and its document-
        // applied handler. MainWindow is constructed by Avalonia's desktop lifetime and cannot
        // take constructor arguments, which is the entire reason the shim exists. Both sites go
        // away when MainViewModel takes the session in its constructor.
        //
        // 3 — plus the ToolNavigator construction in SwitchToCompanionAppAsync (TOOLNAV_01).
        const int Baseline = 3;

        var sites = new List<string>();

        foreach (string file in RepoRoot.SourceFiles(".cs"))
        {
            // The shim's own declaration and its doc comment obviously mention it.
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

    // ════════════════════════════════════════════════════════════════════════════════════════
    // RULE 9 — SYS-DEVBUILD. Every dev.cmd sentinel must still resolve.
    // A meta-test on the sentinel list itself. VERIFY_PATCHES already halts the build when a tag
    // is missing from its file — but that check only runs on Windows, in dev.cmd, and the spec
    // records that it once silently skipped two of its own entries. This asserts the same thing
    // from CI, on any platform, with the file and tag named.
    // ════════════════════════════════════════════════════════════════════════════════════════
    [Fact]
    public void EveryDevCmdSentinelStillResolves()
    {
        string devCmd = Path.Combine(RepoRoot.Path, "dev.cmd");
        Assert.True(File.Exists(devCmd), "dev.cmd not found at the repository root.");

        var missing = new List<string>();
        int checkedCount = 0;

        foreach (string raw in File.ReadAllLines(devCmd))
        {
            Match m = Regex.Match(raw.Trim(), @"^""(?<tag>[A-Z0-9_]+)=(?<path>[^""]+)""$");
            if (!m.Success) continue;

            checkedCount++;
            string tag = m.Groups["tag"].Value;
            string rel = m.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar);
            string full = Path.Combine(RepoRoot.Path, rel);

            if (!File.Exists(full)) { missing.Add($"{tag} → {rel} (file not found)"); continue; }
            if (!File.ReadAllText(full).Contains(tag, StringComparison.Ordinal))
                missing.Add($"{tag} → {rel} (tag absent — the fix it guards may have been reverted)");
        }

        Assert.True(checkedCount > 0, "Parsed zero sentinels out of dev.cmd — the VERIFY_PATCHES list format changed.");
        Assert.True(missing.Count == 0,
            $"SYS-DEVBUILD — {missing.Count} of {checkedCount} fix sentinels no longer resolve:"
          + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    // RULE 10 — MVVM_01. Imperative control lookups may not grow.
    //
    // The App layer resolves controls by name 976 times (FindControl / this.Get<T>) against 69
    // {Binding} expressions and 2 classes implementing INotifyPropertyChanged. That is WinForms
    // written in Avalonia: state lives in whichever window owns the control that produced it,
    // which is why GranularSpeedEditorWindow.axaml.cs is 8,061 lines, CropToolWindow 6,484 and
    // MusicWizardWindow 5,707, and why the App layer has no unit tests — there is nothing to
    // construct without a live visual tree.
    //
    // It is also how QUALITY_04 happened: a named control with a literal value and no writer is
    // invisible to the compiler, so a dead readout looked MISSING rather than broken. A binding
    // would have failed loudly.
    //
    // A ratchet, because 976 sites cannot be converted without a compiler in the loop, and each
    // conversion is a judgement about whether the state belongs in a view-model or is genuinely
    // view-local (canvas geometry, pointer capture, drag state — those stay). The number may only
    // fall. Lower the baseline in the same change that lowers the count.
    // ════════════════════════════════════════════════════════════════════════════════════════
    [Fact]
    public void ImperativeControlLookupsDoNotIncrease()
    {
        // Measured with comments and string literals blanked, so prose about the rule
        // cannot inflate it.
        const int Baseline = 973;

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

    // ════════════════════════════════════════════════════════════════════════════════════════
    // RULE 11 — MVVM_02. No NEW thousand-line window code-behind.
    //
    // The five existing offenders are grandfathered at their current size and may only shrink.
    // The rule that matters is the one about files that do not exist yet: a window created after
    // this test cannot reach four figures, because by the time anyone notices, extracting it is
    // the multi-week job the existing five already represent.
    // ════════════════════════════════════════════════════════════════════════════════════════
    [Fact]
    public void WindowCodeBehindDoesNotGrow()
    {
        // Grandfathered maxima, measured. These may be lowered, never raised.
        var ceilings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["GranularSpeedEditorWindow.axaml.cs"] = 8062,
            ["CropToolWindow.axaml.cs"]            = 6496,
            ["MusicWizardWindow.axaml.cs"]         = 5715,
            ["VoiceOverWindow.axaml.cs"]           = 3647,
            ["MainWindow.axaml.cs"]                = 3420,
            ["VideoMergerWindow.axaml.cs"]         = 2251,
            ["PhaseOverlayControl.axaml.cs"]       = 2417,
            ["SettingsWindow.axaml.cs"]            = 1041,
        };

        // Anything not grandfathered gets the real limit.
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

    // ── helpers ─────────────────────────────────────────────────────────────────────────────


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
            // Verbatim string: @"...", where "" is an escaped quote.
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
