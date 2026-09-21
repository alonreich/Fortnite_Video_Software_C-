// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FvsVerify;
using Xunit;

namespace FvsVerify.Tests;

/// <summary>
/// SYS-VERIFYTOOL — the tests that the batch version could not have.
///
/// <para>
/// Every case below corresponds to a way the old <c>for %%P in (...)</c> list failed in the field.
/// That is the point: the mechanism moved to C# so these could be written at all. A guard that
/// cannot be tested is a guard that is trusted without evidence, and this one was wrong for
/// months while being trusted.
/// </para>
/// </summary>
public sealed class SentinelListTests
{
    private static string RepoRoot =>
        SentinelList.FindRepositoryRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("Could not locate the repository root from the test binary.");

    // ── The real list ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// VERIFYHALT_01 — the check the whole mechanism exists for, run against the real working tree.
    /// This is the same assertion CI makes and the same one dev.cmd makes before a local build;
    /// all three read <c>build/sentinels.txt</c>, so they cannot disagree.
    /// </summary>
    [Fact]
    public void EveryFixSentinelStillResolves()
    {
        IReadOnlyList<Sentinel> sentinels = SentinelList.Load(RepoRoot, out IReadOnlyList<string> malformed);
        IReadOnlyList<SentinelResult> failures = SentinelList.Check(RepoRoot, sentinels);

        Assert.True(malformed.Count == 0 && failures.Count == 0,
            SentinelList.FormatFailures(malformed, failures));
    }

    /// <summary>
    /// The list is not allowed to quietly empty itself. A parser change that made every line look
    /// like a comment would turn this whole mechanism off while reporting a clean pass — which is
    /// exactly what LISTCOMMENT_01 and VERIFYLOOP_01 each did, in their own way.
    /// </summary>
    [Fact]
    public void TheListIsNotEmpty()
    {
        IReadOnlyList<Sentinel> sentinels = SentinelList.Load(RepoRoot, out _);
        Assert.True(sentinels.Count >= 100,
            $"Only {sentinels.Count} sentinels parsed out of {SentinelList.RelativePath}. The list held 164 "
          + "when it moved out of dev.cmd. A sudden drop means the format changed and entries are "
          + "being skipped, not that the fixes were retired.");
    }

    // ── Parsing ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CommentsAndBlankLinesAreSkipped()
    {
        IReadOnlyList<Sentinel> parsed = SentinelList.Parse(new[]
        {
            "# a comment with (round brackets), \"quotes\" and an = sign",
            "",
            "   ",
            "# RETIRED OLDTAG_01 — superseded by a rewrite",
            "TAG_01=src/Thing.cs",
        }, out IReadOnlyList<string> malformed);

        Assert.Empty(malformed);
        Sentinel only = Assert.Single(parsed);
        Assert.Equal("TAG_01", only.Tag);
        Assert.Equal("src/Thing.cs", only.RelativePath);
    }

    /// <summary>
    /// LISTCOMMENT_01, as a unit test. In the batch list a bare annotation became one list item per
    /// WORD, each checked as a sentinel — ~400 bogus entries. Here, anything that is not a comment
    /// and not TAG=path is reported, never silently dropped and never mistaken for a sentinel.
    /// </summary>
    [Fact]
    public void AMalformedLineIsReportedRatherThanSkipped()
    {
        IReadOnlyList<Sentinel> parsed = SentinelList.Parse(new[]
        {
            "TAG_01=src/Thing.cs",
            "REM --- Crop Tools rework, phase 3 ---",   // the exact shape that broke the batch list
            "=src/NoTag.cs",
            "TAG_02=",
        }, out IReadOnlyList<string> malformed);

        Assert.Single(parsed);
        Assert.Equal(3, malformed.Count);
    }

    /// <summary>
    /// A padded tag matches nothing and would report every file as reverted. The old list said so
    /// in a comment and could not enforce it.
    /// </summary>
    [Fact]
    public void ATagWithWhitespaceIsMalformed()
    {
        SentinelList.Parse(new[] { "TAG 01=src/Thing.cs" }, out IReadOnlyList<string> malformed);
        Assert.Single(malformed);
    }

    /// <summary>
    /// A backslash path works on Windows and fails on a Linux CI runner as a mystery [no-file].
    /// Rejecting it at parse time makes the failure say what is actually wrong.
    /// </summary>
    [Fact]
    public void ABackslashPathIsMalformed()
    {
        SentinelList.Parse(new[] { @"TAG_01=src\Thing.cs" }, out IReadOnlyList<string> malformed);
        string only = Assert.Single(malformed);
        Assert.Contains("forward slashes", only, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameTagAndPathTwiceIsCheckedOnce()
    {
        IReadOnlyList<Sentinel> parsed = SentinelList.Parse(new[]
        {
            "TAG_01=src/Thing.cs",
            "TAG_01=src/Thing.cs",
            "TAG_01=src/Other.cs",
        }, out _);

        Assert.Equal(2, parsed.Count);
    }

    // ── Checking ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AMissingFileAndAMissingTagAreDistinguished()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fvsverify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "src", "Present.cs"), "// nothing of interest here");

        IReadOnlyList<SentinelResult> failures = SentinelList.Check(dir, new[]
        {
            new Sentinel("TAG_01", "src/Present.cs", 1),
            new Sentinel("TAG_02", "src/Absent.cs", 2),
        });

        try
        {
            Assert.Equal(2, failures.Count);
            Assert.Equal(SentinelFailure.TagMissing, failures[0].Failure);
            Assert.Equal(SentinelFailure.FileMissing, failures[1].Failure);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void APresentTagPasses()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fvsverify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "src", "Present.cs"), "// TAG_01 — the fix this guards\nint x = 1;");

        try
        {
            Assert.Empty(SentinelList.Check(dir, new[] { new Sentinel("TAG_01", "src/Present.cs", 1) }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The failure text is what a developer acts on at 2am. It must name the tag, the file, the
    /// reason and the line in the data file to edit — the batch version printed a bare list of tags.
    /// </summary>
    [Fact]
    public void TheFailureMessageNamesTagFileReasonAndListLine()
    {
        string text = SentinelList.FormatFailures(
            Array.Empty<string>(),
            new[] { new SentinelResult(new Sentinel("ZOOMLIVE_07", "src/Editor.cs", 42), SentinelFailure.TagMissing) });

        Assert.Contains("ZOOMLIVE_07", text, StringComparison.Ordinal);
        Assert.Contains("src/Editor.cs", text, StringComparison.Ordinal);
        Assert.Contains("tag not found", text, StringComparison.Ordinal);
        Assert.Contains(":42", text, StringComparison.Ordinal);
        Assert.Contains("RETIRED", text, StringComparison.Ordinal);
    }
}
