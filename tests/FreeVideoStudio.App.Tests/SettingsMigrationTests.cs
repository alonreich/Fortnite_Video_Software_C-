using System;
using System.IO;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Infrastructure;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class SettingsMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _prevOverride;
    private readonly string _settingsFile;

    public SettingsMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FvsSettingsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _prevOverride = Environment.GetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable);
        Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, _tempDir);
        _settingsFile = Path.Combine(_tempDir, "settings.json");
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

    [Fact]
    public void MigrateFromSchema5_SetsAutoUpdateChecksTrue_AndPersistsSchema7()
    {
        string v5Json = """
        {
            "SchemaVersion": 5,
            "MainOutputDirectory": "C:\\Videos",
            "MergerOutputDirectory": "C:\\Merged",
            "AutoUpdateChecks": false
        }
        """;
        File.WriteAllText(_settingsFile, v5Json);

        SettingsManager.Load();

        Assert.Equal(SettingsManager.CurrentSchemaVersion, SettingsManager.Instance.SchemaVersion);
        Assert.True(SettingsManager.Instance.AutoUpdateChecks);

        string savedJson = File.ReadAllText(_settingsFile);
        Assert.Contains($"\"SchemaVersion\": {SettingsManager.CurrentSchemaVersion}", savedJson);
        Assert.Contains("\"AutoUpdateChecks\": true", savedJson);
    }

    [Fact]
    public void UpgradeFromOlderConfigLackingAutoUpdate_SetsAutoUpdateChecksTrue_AndPersists()
    {
        string olderJson = """
        {
            "SchemaVersion": 6,
            "MainOutputDirectory": "C:\\Videos",
            "MergerOutputDirectory": "C:\\Merged"
        }
        """;
        File.WriteAllText(_settingsFile, olderJson);

        SettingsManager.Load();

        Assert.True(SettingsManager.Instance.AutoUpdateChecks);
        string savedJson = File.ReadAllText(_settingsFile);
        Assert.Contains($"\"SchemaVersion\": {SettingsManager.CurrentSchemaVersion}", savedJson);
        Assert.Contains("\"AutoUpdateChecks\": true", savedJson);
    }

    [Fact]
    public void FreshInstallWithoutConfig_CreatesInitialConfigFile_WithAutoUpdateChecksTrue()
    {
        if (File.Exists(_settingsFile)) File.Delete(_settingsFile);

        SettingsManager.Load();

        Assert.True(File.Exists(_settingsFile));
        Assert.True(SettingsManager.Instance.AutoUpdateChecks);
        string savedJson = File.ReadAllText(_settingsFile);
        Assert.Contains("\"AutoUpdateChecks\": true", savedJson);
    }

    [Fact]
    public void MigrateFromSchema7_ForcesThumbnailScraperOn()
    {
        File.WriteAllText(_settingsFile, """
        {
            "SchemaVersion": 7,
            "AutoUpdateChecks": true,
            "MergerThumbnailScraper": false
        }
        """);

        SettingsManager.Load();

        Assert.Equal(10, SettingsManager.CurrentSchemaVersion);
        Assert.True(SettingsManager.Instance.MergerThumbnailScraper);
        Assert.Contains("\"MergerThumbnailScraper\": true", File.ReadAllText(_settingsFile));
    }

    [Fact]
    public void MigrateFromSchema9_InitializesAiTrackingDefaults()
    {
        File.WriteAllText(_settingsFile, """
        {
            "SchemaVersion": 9,
            "AutoUpdateChecks": true
        }
        """);

        SettingsManager.Load();

        Assert.Equal(10, SettingsManager.CurrentSchemaVersion);
        Assert.Equal("gemini-2.5-flash", SettingsManager.Instance.GeminiModelName);
        Assert.Equal(2.2, SettingsManager.Instance.AiZoomBaseScale);
        Assert.Equal(1.3, SettingsManager.Instance.AiZoomMinScale);
        Assert.True(SettingsManager.Instance.AiZoomAvoidHud);
        Assert.Equal(2.0, SettingsManager.Instance.AiZoomDeadbandPercent);
    }

    [Fact]
    public void MigrateFromSchema8_TurnsTheMergerRemoveConfirmOff_Once()
    {
        File.WriteAllText(_settingsFile, """
        {
            "SchemaVersion": 8,
            "AutoUpdateChecks": true,
            "ConfirmVideoMergerRemove": true
        }
        """);
        SettingsManager.Load();
        Assert.False(SettingsManager.Instance.ConfirmVideoMergerRemove);

        File.WriteAllText(_settingsFile, """
        {
            "SchemaVersion": 9,
            "AutoUpdateChecks": true,
            "ConfirmVideoMergerRemove": true
        }
        """);
        SettingsManager.Load();
        Assert.True(SettingsManager.Instance.ConfirmVideoMergerRemove);
    }

    [Fact]
    public void Schema8_UserChoiceOff_IsRespected()
    {
        File.WriteAllText(_settingsFile, """
        {
            "SchemaVersion": 8,
            "AutoUpdateChecks": true,
            "MergerThumbnailScraper": false
        }
        """);

        SettingsManager.Load();

        Assert.False(SettingsManager.Instance.MergerThumbnailScraper);
    }

    [Fact]
    public void FreshInstall_ThumbnailScraperOn()
    {
        SettingsManager.Load();
        Assert.True(SettingsManager.Instance.MergerThumbnailScraper);
    }
}
