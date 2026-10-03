using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// TEMPO_01 — 03 FFM-TEMPO. One tempo policy for every export route: none at 1.0x, Rubber Band
/// below 1.0x when the bundled FFmpeg genuinely supports it, the atempo chain otherwise.
/// </summary>
public class AudioTempoFilterBuilderTests
{
    public static IEnumerable<object[]> Rates => new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 2.0, 4.0 }.Select(r => new object[] { r });

    // ── policy ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.25, new[] { "rubberband=tempo=0.2500:transients=mixed" })]
    [InlineData(0.5, new[] { "rubberband=tempo=0.5000:transients=mixed" })]
    [InlineData(0.75, new[] { "rubberband=tempo=0.7500:transients=mixed" })]
    [InlineData(1.0, new string[0])]
    [InlineData(1.25, new[] { "atempo=1.2500" })]
    [InlineData(2.0, new[] { "atempo=2.0000" })]
    [InlineData(4.0, new[] { "atempo=2.0", "atempo=2.0000" })]
    public void CapabilityPresent_SlowdownsUseRubberBand_SpeedupsKeepAtempo(double rate, string[] expected)
        => Assert.Equal(expected, AudioTempoFilterBuilder.Build(rate, rubberbandAvailable: true));

    [Theory]
    [InlineData(0.25, new[] { "atempo=0.5", "atempo=0.5000" })]
    [InlineData(0.5, new[] { "atempo=0.5000" })]
    [InlineData(0.75, new[] { "atempo=0.7500" })]
    [InlineData(1.0, new string[0])]
    [InlineData(1.25, new[] { "atempo=1.2500" })]
    [InlineData(2.0, new[] { "atempo=2.0000" })]
    [InlineData(4.0, new[] { "atempo=2.0", "atempo=2.0000" })]
    public void CapabilityAbsent_EveryRateIsTheExistingAtempoChain(double rate, string[] expected)
    {
        Assert.Equal(expected, AudioTempoFilterBuilder.Build(rate, rubberbandAvailable: false));
        Assert.Equal(expected, AudioTempoFilterBuilder.BuildAtempoChain(rate));
        Assert.Equal(expected, GranularSpeedBuilder.BuildAtempoChain(rate));
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void EngineChoice_MatchesThePolicyTable(double rate)
    {
        var present = AudioTempoFilterBuilder.EngineFor(rate, true);
        var absent = AudioTempoFilterBuilder.EngineFor(rate, false);
        if (rate == 1.0) { Assert.Equal(AudioTempoEngine.None, present); Assert.Equal(AudioTempoEngine.None, absent); }
        else if (rate < 1.0) { Assert.Equal(AudioTempoEngine.Rubberband, present); Assert.Equal(AudioTempoEngine.Atempo, absent); }
        else { Assert.Equal(AudioTempoEngine.Atempo, present); Assert.Equal(AudioTempoEngine.Atempo, absent); }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ImpossibleRates_FallBackToNoFilter(double rate)   // ISSUE_04 — must not hang either
    {
        Assert.Empty(AudioTempoFilterBuilder.Build(rate, true));
        Assert.Empty(AudioTempoFilterBuilder.Build(rate, false));
    }

    [Fact]
    public void Segment_NeverLeavesADanglingComma()   // AVSYNC_01
    {
        Assert.Equal("", AudioTempoFilterBuilder.Segment(1.0, leadingComma: true, rubberbandAvailable: true));
        Assert.Equal("", AudioTempoFilterBuilder.Segment(1.0, leadingComma: false, rubberbandAvailable: false));
        Assert.Equal(",rubberband=tempo=0.5000:transients=mixed", AudioTempoFilterBuilder.Segment(0.5, true, true));
        Assert.Equal("atempo=2.0,atempo=2.0000,", AudioTempoFilterBuilder.Segment(4.0, false, true));
    }

    // ── capability ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Unprobed_IsAbsent_AndAScopedOverrideIsLocalToItsFlow()
    {
        Assert.Equal("atempo=0.5000", string.Join(",", AudioTempoFilterBuilder.Build(0.5, false)));
        using (AudioTempoFilterBuilder.OverrideCapability(true))
            Assert.StartsWith("rubberband=", AudioTempoFilterBuilder.Build(0.5)[0]);
        using (AudioTempoFilterBuilder.OverrideCapability(false))
            Assert.StartsWith("atempo=", AudioTempoFilterBuilder.Build(0.5)[0]);
    }

    /// <summary>Verbatim `ffmpeg -hide_banner -h filter=rubberband` option table (identical in the bundled build's strings).</summary>
    internal const string RealHelp = """
        Filter rubberband
          Apply time-stretching and pitch-shifting.
        rubberband AVOptions:
           tempo             <double>     ..F.A....T. set tempo scale factor (from 0.01 to 100) (default 1)
           pitch             <double>     ..F.A....T. set pitch scale factor (from 0.01 to 100) (default 1)
           transients        <int>        ..F.A...... set transients (from 0 to INT_MAX) (default crisp)
             crisp           0            ..F.A......
             mixed           256          ..F.A......
             smooth          512          ..F.A......
           detector          <int>        ..F.A...... set detector (from 0 to INT_MAX) (default compound)
        """;

    [Fact]
    public void HelpParse_AcceptsTheRealOptionTable_RejectsAbsenceAndMissingSyntax()
    {
        Assert.True(AudioTempoFilterBuilder.HelpListsRequiredOptions(RealHelp));
        Assert.False(AudioTempoFilterBuilder.HelpListsRequiredOptions("Unknown filter 'rubberband'."));
        Assert.False(AudioTempoFilterBuilder.HelpListsRequiredOptions(""));
        Assert.False(AudioTempoFilterBuilder.HelpListsRequiredOptions(RealHelp.Replace("mixed ", "other ")));
        Assert.False(AudioTempoFilterBuilder.HelpListsRequiredOptions(RealHelp.Replace("tempo  ", "speed  ")));
    }

    [Fact]
    public async Task Probe_MissingBinary_IsAbsent_NeverThrows()
    {
        Assert.False(await AudioTempoFilterBuilder.EnsureProbedAsync(""));
        Assert.False(await AudioTempoFilterBuilder.EnsureProbedAsync(Path.Combine(Path.GetTempPath(), "no-such-ffmpeg-" + Guid.NewGuid().ToString("N"))));
        Assert.True(AudioTempoFilterBuilder.IsProbed);
    }

    // ── one policy on every route (Main / Granular / Merger plain / Merger effects) ────────

    [Theory]
    [MemberData(nameof(Rates))]
    public void GranularAndMergerEffectsClips_EmitTheSameTempoFilter(double rate)
    {
        foreach (bool cap in new[] { true, false })
        {
            using var _ = AudioTempoFilterBuilder.OverrideCapability(cap);
            string expected = AudioTempoFilterBuilder.Segment(rate, leadingComma: false, cap);

            var main = GranularSpeedBuilder.Build(4000, [new SpeedSegment(1000, 3000, rate)], 1.0, 0, "[0:v]", "[0:a]", "60", needHudBranch: false);
            var merger = MergeClipGraph.Build(0, "[0:v]", "[0:a]", 0.0, 4.0,
                new EdlEffects { Speed = new[] { new EdlSpeedSegment(1_000_000, 3_000_000, rate) } },
                1.0, "scale=1920:1080,setsar=1", "scale=1920:1080", Array.Empty<MergeMemeInput>());
            string mergerGraph = string.Join(";", merger.Filters);

            Assert.Equal(TempoFilters(main.filterGraph), TempoFilters(mergerGraph));
            if (expected.Length > 0) Assert.Contains(expected, main.filterGraph);
            if (rate == 1.0) Assert.Empty(TempoFilters(main.filterGraph));
            Assert.Equal(main.finalDuration, merger.DurationSec, 2);
        }
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void TheEngineNeverChangesTheGraphDuration_TheBoundAndSpliceFadeFollowTheTempo(double rate)
    {
        var withRb = Built(rate, true);
        var without = Built(rate, false);
        Assert.Equal(without.finalDuration, withRb.finalDuration, 9);
        Assert.Equal(3.0 + 2.0 / rate, withRb.finalDuration, 2);

        foreach (string chunk in withRb.filterGraph.Split(';').Where(c => c.Contains("atrim=start=")))
        {
            int tempo = Math.Max(chunk.IndexOf("rubberband=", StringComparison.Ordinal), chunk.IndexOf("atempo=", StringComparison.Ordinal));
            int bound = chunk.IndexOf("apad,atrim=duration=", StringComparison.Ordinal);
            Assert.True(bound > 0, chunk);
            if (tempo >= 0) Assert.True(tempo < bound, $"tempo after the duration bound: {chunk}");
            int fade = chunk.IndexOf("afade=t=in", StringComparison.Ordinal);
            if (fade >= 0) Assert.True(fade > bound, $"splice fade before the bound: {chunk}");
        }
    }

    [Fact]
    public void NoExportRouteSpellsATempoFilterItself()
    {
        string repo = FindRepoRoot();
        string src = Path.Combine(repo, "src");
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            .Where(f => Path.GetFileName(f) != "AudioTempoFilterBuilder.cs")
            // Phase4Gate PARSES atempo elements to check their [0.5, 2.0] bounds; it builds no export graph.
            .Where(f => Path.GetFileName(f) != "Phase4Gate.cs")
            .Where(f => Regex.IsMatch(File.ReadAllText(f), "\"[^\"\\n]*(atempo=|rubberband=)"))
            .Select(f => Path.GetRelativePath(repo, f)).ToList();
        Assert.True(offenders.Count == 0, "TEMPO_01 — a tempo filter literal outside AudioTempoFilterBuilder: " + string.Join(", ", offenders));

        string Read(string rel) => File.ReadAllText(Path.Combine(src, "FreeVideoStudio.Core", "Media", rel));
        Assert.Contains("AudioTempoFilterBuilder.Segment(SpeedFactor", Read("ProcessWorker.cs"));
        Assert.Contains("AudioTempoFilterBuilder.Segment(speedFactor", Read("MergerWorker.cs"));
        Assert.Contains("AudioTempoFilterBuilder.Segment(chunk.Speed", Read("GranularSpeedBuilder.cs"));
        Assert.Contains("AudioTempoFilterBuilder.Segment(baseSpeed", Read("GranularSpeedBuilder.cs"));
        Assert.Contains("GranularSpeedBuilder.Build(", Read("MergeClipGraph.cs"));
        Assert.Contains("AudioTempoFilterBuilder.EnsureProbedAsync(_ffmpegPath)", Read("ProcessWorker.cs"));
        Assert.Contains("AudioTempoFilterBuilder.EnsureProbedAsync(_ffmpegPath)", Read("MergerWorker.cs"));
    }

    // ── real FFmpeg: capability, duration, pitch (bundled binary on Windows; FVS_TEST_FFMPEG elsewhere) ──

    [MediaFact]
    public async Task RealFfmpeg_ProbeFindsRubberBand()
        => Assert.True(await AudioTempoFilterBuilder.EnsureProbedAsync(MediaFactAttribute.Ffmpeg!));

    [MediaTheory]
    [InlineData(0.25, true)] [InlineData(0.5, true)] [InlineData(0.75, true)] [InlineData(1.0, true)]
    [InlineData(1.25, true)] [InlineData(2.0, true)] [InlineData(4.0, true)]
    [InlineData(0.25, false)] [InlineData(0.5, false)] [InlineData(0.75, false)]
    public void RealFfmpeg_GranularGraph_ExactDuration_And440HzStays440Hz(double rate, bool cap)
    {
        // Known fixture: a 440 Hz sine under a 4 s clip whose whole length runs at `rate`.
        (string filterGraph, string videoLabel, string hudLabel, string audioLabel, double finalDuration, Func<double, double> timeMapper) g;
        using (AudioTempoFilterBuilder.OverrideCapability(cap))
            g = GranularSpeedBuilder.Build(4000, [new SpeedSegment(0, 4000, rate)], 1.0, 0, "[0:v]", "[1:a]", "30",
                needHudBranch: false);
        string graph = g.filterGraph;
        if (cap && rate < 1.0) Assert.Contains("rubberband=", graph);

        string raw = Path.Combine(Path.GetTempPath(), $"fvs-tempo-{Guid.NewGuid():N}.f32");
        try
        {
            var (exit, err) = RunFfmpeg("-hide_banner", "-nostdin", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc2=s=160x90:r=30:d=4",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=4",
                "-filter_complex", graph,
                "-map", g.videoLabel, "-f", "null", "-",
                "-map", g.audioLabel, "-ac", "1", "-ar", "48000", "-f", "f32le", raw);
            Assert.True(exit == 0, err);

            byte[] bytes = File.ReadAllBytes(raw);
            float[] s = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, s, 0, s.Length * 4);

            double expectedSec = 4.0 / rate;
            Assert.Equal(expectedSec, g.finalDuration, 3);
            Assert.True(Math.Abs(s.Length / 48000.0 - g.finalDuration) <= 0.001,
                $"audio {s.Length / 48000.0:F5}s vs graph {g.finalDuration:F5}s");

            double hz = ZeroCrossingHz(s, (int)(s.Length * 0.25), (int)(s.Length * 0.75));
            Assert.True(Math.Abs(hz - 440.0) <= 1.0, $"{(cap ? "rubberband" : "atempo")} @ {rate}x: {hz:F2} Hz");
        }
        finally { try { File.Delete(raw); } catch (IOException) { /* temp file; nothing to do */ } }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static (string filterGraph, double finalDuration) Built(double rate, bool cap)
    {
        using var _ = AudioTempoFilterBuilder.OverrideCapability(cap);
        var b = GranularSpeedBuilder.Build(5000, [new SpeedSegment(1000, 3000, rate)], 1.0, 0, "[0:v]", "[0:a]", "60", needHudBranch: false);
        return (b.filterGraph, b.finalDuration);
    }

    private static List<string> TempoFilters(string graph)
        => Regex.Matches(graph, @"(rubberband|atempo)=[^,;\[]+").Select(m => m.Value).ToList();

    private static double ZeroCrossingHz(float[] s, int a, int b)
    {
        var x = new List<double>();
        for (int i = a; i < b - 1; i++)
            if (s[i] < 0 && s[i + 1] >= 0) x.Add(i + (-s[i]) / (s[i + 1] - s[i]));
        return (x.Count - 1) / ((x[^1] - x[0]) / 48000.0);
    }

    private static (int exit, string err) RunFfmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo(MediaFactAttribute.Ffmpeg!) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, errTask.GetAwaiter().GetResult());
    }

    internal static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FreeVideoStudio.sln"))) return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}

/// <summary>
/// CITEST_01 — runs only where a real FFmpeg exists: the bundled <c>binaries\ffmpeg.exe</c> on
/// Windows, or the binary named by <c>FVS_TEST_FFMPEG</c> anywhere. Otherwise SKIPS, never fails.
/// </summary>
public sealed class MediaFactAttribute : FactAttribute
{
    public static readonly string? Ffmpeg = Resolve();

    public MediaFactAttribute()
    {
        if (Ffmpeg == null) Skip = "No FFmpeg: set FVS_TEST_FFMPEG, or run on Windows with binaries\\ffmpeg.exe.";
    }

    internal static string? Resolve()
    {
        string? env = Environment.GetEnvironmentVariable("FVS_TEST_FFMPEG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            string bundled = Path.Combine(AudioTempoFilterBuilderTests.FindRepoRoot(), "binaries", "ffmpeg.exe");
            return File.Exists(bundled) ? bundled : null;
        }
        catch (InvalidOperationException) { return null; }
    }
}

public sealed class MediaTheoryAttribute : TheoryAttribute
{
    public MediaTheoryAttribute()
    {
        if (MediaFactAttribute.Ffmpeg == null) Skip = "No FFmpeg: set FVS_TEST_FFMPEG, or run on Windows with binaries\\ffmpeg.exe.";
    }
}
