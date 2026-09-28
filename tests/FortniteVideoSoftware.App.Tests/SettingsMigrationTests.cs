using System;
using System.IO;
using FortniteVideoSoftware.App.Infrastructure;
using FortniteVideoSoftware.Core.Infrastructure;
using Xunit;

namespace FortniteVideoSoftware.App.Tests;

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
        // Arrange: An older schema v5 settings JSON without AutoUpdateChecks or explicitly false
        string v5Json = """
        {
            "SchemaVersion": 5,
            "MainOutputDirectory": "C:\\Videos",
            "MergerOutputDirectory": "C:\\Merged",
            "AutoUpdateChecks": false
        }
        """;
        File.WriteAllText(_settingsFile, v5Json);

        // Act
        SettingsManager.Load();

        // Assert
        Assert.Equal(SettingsManager.CurrentSchemaVersion, SettingsManager.Instance.SchemaVersion);
        Assert.True(SettingsManager.Instance.AutoUpdateChecks);

        // Verify disk contents were saved with SchemaVersion = 7 and AutoUpdateChecks = true
        string savedJson = File.ReadAllText(_settingsFile);
        Assert.Contains($"\"SchemaVersion\": {SettingsManager.CurrentSchemaVersion}", savedJson);
        Assert.Contains("\"AutoUpdateChecks\": true", savedJson);
    }

    [Fact]
    public void UpgradeFromOlderConfigLackingAutoUpdate_SetsAutoUpdateChecksTrue_AndPersists()
    {
        // Arrange: An older config where AutoUpdateChecks key never existed
        string olderJson = """
        {
            "SchemaVersion": 6,
            "MainOutputDirectory": "C:\\Videos",
            "MergerOutputDirectory": "C:\\Merged"
        }
        """;
        File.WriteAllText(_settingsFile, olderJson);

        // Act
        SettingsManager.Load();

        // Assert
        Assert.True(SettingsManager.Instance.AutoUpdateChecks);
        string savedJson = File.ReadAllText(_settingsFile);
        Assert.Contains($"\"SchemaVersion\": {SettingsManager.CurrentSchemaVersion}", savedJson);
        Assert.Contains("\"AutoUpdateChecks\": true", savedJson);
    }

    [Fact]
    public void FreshInstallWithoutConfig_CreatesInitialConfigFile_WithAutoUpdateChecksTrue()
    {
        // Arrange: Ensure no settings file exists
        if (File.Exists(_settingsFile)) File.Delete(_settingsFile);

        // Act
        SettingsManager.Load();

        // Assert
        Assert.True(File.Exists(_settingsFile));
        Assert.True(SettingsManager.Instance.AutoUpdateChecks);
        string savedJson = File.ReadAllText(_settingsFile);
        Assert.Contains("\"AutoUpdateChecks\": true", savedJson);
    }

    [Fact]
    public void MigrateFromSchema7_ForcesThumbnailScraperOn()
    {
        // SCRAPER_05 — an upgrader who had the flag OFF (or never had it) gets it ON.
        File.WriteAllText(_settingsFile, """
        {
            "SchemaVersion": 7,
            "AutoUpdateChecks": true,
            "MergerThumbnailScraper": false
        }
        """);

        SettingsManager.Load();

        Assert.Equal(9, SettingsManager.CurrentSchemaVersion);
        Assert.True(SettingsManager.Instance.MergerThumbnailScraper);
        Assert.Contains("\"MergerThumbnailScraper\": true", File.ReadAllText(_settingsFile));
    }

    [Fact]
    public void MigrateFromSchema8_TurnsTheMergerRemoveConfirmOff_Once()
    {
        // REMOVEUX_01 — an upgrader who had the confirm ON gets it OFF once; a v9 file keeps the user's choice.
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
