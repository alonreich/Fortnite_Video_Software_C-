// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// AOTSAFETY_03 / AOTCLEAN_01 — pins the release-build analyser policy in FreeVideoStudio.App.csproj.
///
/// <para>
/// AOTCLEAN_01 (2026-09-25) replaced the interim policy described below. Every IL finding is now
/// fatal and none is muted or collapsed: the findings were fixed at the source (Avalonia 11.3,
/// SkiaSharp 3, NAudio.Core/WinMM only, first-party D3D11 interop instead of Vortice/SharpGen).
/// </para>
///
/// <para>
/// From 2026-09-20 every Build.cmd run failed. AOTSAFETY_01 turned the trim/AOT analysers on, and
/// Staging.Publish runs with TreatWarningsAsErrors=true. Third-party summary warnings (IL2104/IL3053)
/// and two IL2026 lines from inside Avalonia 11.0 became fatal. The fix keeps first-party findings
/// fatal and lets third-party summaries through. These tests stop either half from drifting: re-muting the
/// analysers, or widening the non-fatal list until first-party findings slip through too.
/// </para>
/// </summary>
public sealed class AotSafetyRuleTests
{
    private static XDocument LoadAppProject()
        => XDocument.Load(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "FreeVideoStudio.App.csproj"));

    [Fact]
    public void TrimAndAotAnalysersStayOn()
    {
        var doc = LoadAppProject();
        foreach (string name in new[] { "SuppressTrimAnalysisWarnings", "SuppressAotAnalysisWarnings" })
        {
            var values = doc.Descendants(name).Select(e => e.Value.Trim()).ToList();
            Assert.NotEmpty(values);
            Assert.All(values, v => Assert.Equal("false", v, ignoreCase: true));
        }
    }

    /// <summary>AOTCLEAN_01 — no IL code is ever downgraded from an error.</summary>
    [Fact]
    public void NoTrimOrAotCodeIsNonFatal()
    {
        var doc = LoadAppProject();
        var codes = doc.Descendants("WarningsNotAsErrors")
            .SelectMany(e => e.Value.Split(';'))
            .Select(c => c.Trim())
            .Where(c => c.StartsWith("IL", System.StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(codes);
    }

    /// <summary>AOTCLEAN_01 — no IL code is muted anywhere (global NoWarn or inside a target).</summary>
    [Fact]
    public void NoTrimOrAotCodeIsMuted()
    {
        var doc = LoadAppProject();
        var muted = doc.Descendants("NoWarn")
            .SelectMany(e => e.Value.Split(';'))
            .Select(c => c.Trim())
            .Where(c => c.StartsWith("IL", System.StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(muted);
    }

    /// <summary>AOTCLEAN_01 — third-party findings are never collapsed into a summary line.</summary>
    [Fact]
    public void ThirdPartyFindingsAreNeverCollapsed()
    {
        var doc = LoadAppProject();
        Assert.DoesNotContain(doc.Descendants("TrimmerSingleWarn"), e => e.Value.Trim().Equals("true", System.StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>AOTCLEAN_02/03 — the packages whose code NativeAOT cannot run stay out.</summary>
    [Theory]
    [InlineData("NAudio")]            // umbrella: pulls Wasapi (COM MediaFoundation) and WinForms
    [InlineData("NAudio.Wasapi")]
    [InlineData("Vortice.Direct3D11")] // SharpGen.Runtime reflection vtables
    [InlineData("Vortice.DXGI")]
    public void AotHostilePackagesStayOut(string package)
    {
        foreach (string project in new[] { "FreeVideoStudio.App", "FreeVideoStudio.Core" })
        {
            var doc = XDocument.Load(Path.Combine(RepoRoot.Path, "src", project, project + ".csproj"));
            Assert.DoesNotContain(doc.Descendants("PackageReference"), e => (string?)e.Attribute("Include") == package);
        }
    }

    [Fact]
    public void UnusedAvaloniaControlPackagesStayOutOfRelease()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "FreeVideoStudio.App.csproj"));
        Assert.DoesNotMatch(new Regex(@"<(PackageReference|TrimmerRootAssembly)\s+Include=""Avalonia\.Controls\.DataGrid"""), text);

        var diagnostics = LoadAppProject().Descendants("PackageReference")
            .Where(e => (string?)e.Attribute("Include") == "Avalonia.Diagnostics")
            .ToList();
        Assert.All(diagnostics, e => Assert.Contains("Debug", (string?)e.Attribute("Condition") ?? string.Empty));
    }
}
