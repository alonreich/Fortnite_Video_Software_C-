// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FvsVerify;

/// <summary>SYS-VERIFYTOOL — one parsed line of <c>build/sentinels.txt</c>.</summary>
/// <param name="Tag">The comment tag searched for, verbatim.</param>
/// <param name="RelativePath">Repository-relative path, forward slashes.</param>
/// <param name="LineNumber">Where it sits in the data file, so a failure can be edited immediately.</param>
public readonly record struct Sentinel(string Tag, string RelativePath, int LineNumber);

/// <summary>SYS-VERIFYTOOL — why one sentinel failed.</summary>
public enum SentinelFailure
{
    /// <summary>The file named by the entry does not exist.</summary>
    FileMissing,

    /// <summary>The file exists and does not contain the tag — the fix was reverted, or the sentinel is stale.</summary>
    TagMissing,
}

/// <summary>SYS-VERIFYTOOL — one failing sentinel and the reason.</summary>
public readonly record struct SentinelResult(Sentinel Sentinel, SentinelFailure Failure);

/// <summary>
/// SYS-VERIFYTOOL — THE FIX-SENTINEL CHECK, AS CODE THAT CAN BE TESTED.
///
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// <b>WHY THIS CLASS EXISTS.</b> This check used to be a <c>for %%P in (...)</c> loop inside
/// <c>dev.cmd</c>. Over its life that host broke it three separate times and every failure was
/// silent — <c>VERIFYLOOP_01</c> (label seeks by byte offset skipped two entries),
/// <c>LISTCOMMENT_01</c> (<c>REM</c> is not a comment inside a FOR list, so annotations became
/// hundreds of bogus entries) and <c>BATCHPARENS_01</c> (one round bracket in an annotation closes
/// the list early and the next word runs as a command). On top of all three, <c>VERIFYHALT_01</c>:
/// the result was assigned and never read, so for its entire existence the subroutine checked
/// everything and reported nothing.
/// </para>
///
/// <para>
/// ⚠️ None of that is a flaw in the sentinel idea. It is what happens when a list of 164 strings,
/// a comment syntax and a file-search are expressed in a language with no list type, no comments
/// inside a list and no escaping — AND the result is unreachable by any test. The rule this class
/// exists to satisfy is Invariant #8: <i>every rule that can be a test is a test.</i> The parsing
/// and the checking are ordinary functions here, so <c>SentinelListTests</c> can assert that a
/// malformed line is rejected, that a comment is skipped, and that a missing tag is reported —
/// none of which was expressible against a batch subroutine.
/// </para>
///
/// <para>
/// The same functions back both callers: <c>FvsVerify</c> halts the local build before it starts,
/// and <c>ArchitectureRuleTests.EveryFixSentinelStillResolves</c> fails CI. They read one file, so
/// they cannot drift apart — which the old arrangement, with the list in dev.cmd and a partial
/// copy of the intent in a test, could and did.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </para>
/// </summary>
public static class SentinelList
{
    /// <summary>Where the data file lives, relative to the repository root.</summary>
    public const string RelativePath = "build/sentinels.txt";

    /// <summary>
    /// Parses the data file. Comments (<c>#</c>) and blank lines are skipped; everything else must
    /// be <c>TAG=path</c>.
    /// </summary>
    /// <param name="malformed">
    /// Lines that are neither a comment nor a well-formed entry, as "line N: text". A malformed
    /// line is a HARD FAILURE, never a skip: silently ignoring input it did not understand is
    /// precisely how the batch version checked 47 of 49 sentinels and called it a pass.
    /// </param>
    public static IReadOnlyList<Sentinel> Parse(IEnumerable<string> lines, out IReadOnlyList<string> malformed)
    {
        ArgumentNullException.ThrowIfNull(lines);

        List<Sentinel> parsed = new();
        List<string> bad = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        int lineNumber = 0;

        foreach (string raw in lines)
        {
            lineNumber++;
            string line = raw.Trim();

            if (line.Length == 0 || line[0] == '#') continue;

            int eq = line.IndexOf('=');
            if (eq <= 0 || eq == line.Length - 1)
            {
                bad.Add($"line {lineNumber}: {line}");
                continue;
            }

            string tag = line[..eq].Trim();
            string path = line[(eq + 1)..].Trim();

            // A padded tag matches nothing and would report every file as reverted — the exact
            // trap the old list warned about in prose and could not enforce.
            if (tag.Length == 0 || path.Length == 0 || tag.Any(char.IsWhiteSpace))
            {
                bad.Add($"line {lineNumber}: {line}");
                continue;
            }

            // Backslashes would work on Windows and fail in CI on Linux. Catch it here rather than
            // as a mystery [no-file] on one platform only.
            if (path.Contains('\\'))
            {
                bad.Add($"line {lineNumber}: use forward slashes in paths — {line}");
                continue;
            }

            string key = tag + "\u0000" + path;
            if (!seen.Add(key)) continue;   // the same pair twice is harmless; check it once.

            parsed.Add(new Sentinel(tag, path, lineNumber));
        }

        malformed = bad;
        return parsed;
    }

    /// <summary>Parses the data file at <paramref name="repositoryRoot"/>.</summary>
    public static IReadOnlyList<Sentinel> Load(string repositoryRoot, out IReadOnlyList<string> malformed)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);
        string path = Path.Combine(repositoryRoot, RelativePath.Replace('/', Path.DirectorySeparatorChar));
        return Parse(File.ReadAllLines(path), out malformed);
    }

    /// <summary>
    /// Checks every sentinel against the working tree. Returns the failures, in file order.
    /// An empty result is the only passing result.
    /// </summary>
    public static IReadOnlyList<SentinelResult> Check(string repositoryRoot, IEnumerable<Sentinel> sentinels)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);
        ArgumentNullException.ThrowIfNull(sentinels);

        List<SentinelResult> failures = new();

        // One read per FILE, not one per sentinel. 164 sentinels hit ~60 distinct files, and
        // CropToolWindow.axaml.cs alone is named 26 times.
        Dictionary<string, string?> cache = new(StringComparer.OrdinalIgnoreCase);

        foreach (Sentinel s in sentinels)
        {
            if (!cache.TryGetValue(s.RelativePath, out string? text))
            {
                string full = Path.Combine(repositoryRoot, s.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                text = File.Exists(full) ? File.ReadAllText(full) : null;
                cache[s.RelativePath] = text;
            }

            if (text is null)
            {
                failures.Add(new SentinelResult(s, SentinelFailure.FileMissing));
                continue;
            }

            if (!text.Contains(s.Tag, StringComparison.Ordinal))
                failures.Add(new SentinelResult(s, SentinelFailure.TagMissing));
        }

        return failures;
    }

    /// <summary>
    /// The human-readable halt message. Shared so the local build and CI say the SAME thing —
    /// a developer who has seen it once on their machine recognises it in a CI log.
    /// </summary>
    public static string FormatFailures(IReadOnlyList<string> malformed, IReadOnlyList<SentinelResult> failures)
    {
        ArgumentNullException.ThrowIfNull(malformed);
        ArgumentNullException.ThrowIfNull(failures);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("FIX SENTINEL CHECK FAILED.");
        sb.AppendLine();

        if (malformed.Count > 0)
        {
            sb.AppendLine($"  {SentinelList.RelativePath} has {malformed.Count} line(s) that are neither a comment");
            sb.AppendLine("  nor TAG=path. Nothing is skipped silently here, so fix them:");
            foreach (string m in malformed) sb.AppendLine($"    {m}");
            sb.AppendLine();
        }

        if (failures.Count > 0)
        {
            sb.AppendLine($"  {failures.Count} sentinel(s) did not resolve:");
            foreach (SentinelResult f in failures)
            {
                string why = f.Failure == SentinelFailure.FileMissing ? "file does not exist" : "tag not found in file";
                sb.AppendLine($"    {f.Sentinel.Tag,-24} {f.Sentinel.RelativePath}   [{why}]  ({RelativePath}:{f.Sentinel.LineNumber})");
            }
            sb.AppendLine();
            sb.AppendLine("  Either the fix was REVERTED — restore it — or the fix was SUPERSEDED and the");
            sb.AppendLine("  sentinel is stale. A stale sentinel is retired, not deleted: replace its line with");
            sb.AppendLine("  '# RETIRED <TAG> — <why>' so the next person finds out what happened to it.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Walks up from <paramref name="start"/> looking for the repository root, identified by the
    /// sentinel file itself. Lets the tool be run from anywhere — a wrong working directory used to
    /// present as every sentinel reporting [no-file] at once.
    /// </summary>
    public static string? FindRepositoryRoot(string start)
    {
        ArgumentNullException.ThrowIfNull(start);
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "build", "sentinels.txt"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
