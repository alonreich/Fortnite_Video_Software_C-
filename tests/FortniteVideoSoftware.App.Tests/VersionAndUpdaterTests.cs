using System;
using System.IO;
using FortniteVideoSoftware.App;
using FortniteVideoSoftware.App.Services;
using FortniteVideoSoftware.Core.Infrastructure;
using Xunit;

namespace FortniteVideoSoftware.App.Tests;

public sealed class VersionAndUpdaterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _prevOverride;

    public VersionAndUpdaterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FvsUpdaterTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _prevOverride = Environment.GetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable);
        Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, _tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, _prevOverride);
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    [Theory]
    [InlineData("v2026.09.12.0159", 2026, 9, 12, 159)]
    [InlineData("V2026.09.17.0026", 2026, 9, 17, 26)]
    [InlineData("2026.09.17.0026", 2026, 9, 17, 26)]
    [InlineData("2026.09.17.0026+abc123commit", 2026, 9, 17, 26)]
    [InlineData("1.0.0.0", 1, 0, 0, 0)]
    public void TryParseVersion_ValidVersions_ParsesCorrectly(string input, int major, int minor, int build, int rev)
    {
        bool success = DeploymentLifecycle.TryParseVersion(input, out Version parsed);

        Assert.True(success);
        Assert.NotNull(parsed);
        Assert.Equal(major, parsed.Major);
        Assert.Equal(minor, parsed.Minor);
        Assert.Equal(build, parsed.Build);
        Assert.Equal(rev, parsed.Revision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    public void TryParseVersion_InvalidVersions_ReturnsFalse(string? input)
    {
        bool success = DeploymentLifecycle.TryParseVersion(input, out Version parsed);

        Assert.False(success);
        Assert.Equal(new Version(0, 0), parsed);
    }

    [Fact]
    public void TryParseVersion_Comparison_StrictlyNewerBehavesAsExpected()
    {
        Assert.True(DeploymentLifecycle.TryParseVersion("v2026.09.17.0026", out Version newer));
        Assert.True(DeploymentLifecycle.TryParseVersion("v2026.09.12.0159", out Version older));
        Assert.True(DeploymentLifecycle.TryParseVersion("2026.09.12.0159", out Version sameAsOlder));

        Assert.True(newer.CompareTo(older) > 0);
        Assert.True(older.CompareTo(newer) < 0);
        Assert.Equal(0, older.CompareTo(sameAsOlder));
    }

    [Fact]
    public void GetCurrentVersion_ReturnsValidParsableVersion()
    {
        string current = DeploymentLifecycle.GetCurrentVersion();

        Assert.False(string.IsNullOrWhiteSpace(current));
        bool success = DeploymentLifecycle.TryParseVersion(current, out Version parsed);
        Assert.True(success);
        Assert.True(parsed.Major >= 1);
    }

    [Fact]
    public void SkippedVersion_GetAndClear_WorksWithUiStateStore()
    {
        // Act: Initially empty
        UpdateService.ClearSkippedVersion();
        string initial = UpdateService.GetSkippedVersion();
        Assert.Equal(string.Empty, initial);

        // Act: Store skipped tag into state
        UiStateStore.WriteText("update_skipped_tag.txt", "v2026.09.99.9999");
        string readBack = UpdateService.GetSkippedVersion();
        Assert.Equal("v2026.09.99.9999", readBack);

        // Act: Clear skipped tag
        UpdateService.ClearSkippedVersion();
        string cleared = UpdateService.GetSkippedVersion();
        Assert.Equal(string.Empty, cleared);
    }
}
