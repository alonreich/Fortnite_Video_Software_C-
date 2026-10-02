using FreeVideoStudio.Core.Infrastructure;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class AppDataPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FvsMigrationTests_" + Guid.NewGuid().ToString("N"));

    public AppDataPathsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void CopiesNestedPresetsAndRecoveryWithoutChangingLegacyData()
    {
        string legacy = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(Path.Combine(legacy, "profiles", "nested"));
        File.WriteAllText(Path.Combine(legacy, "profiles", "nested", "crop.json"), "preset");
        File.WriteAllText(Path.Combine(legacy, "recovery_v2.json.bak"), "backup");
        string destination = AppDataPaths.MigrateDirectory(_root, "legacy");
        Assert.Equal(Path.Combine(_root, "FreeVideoStudio"), destination);
        Assert.Equal("preset", File.ReadAllText(Path.Combine(destination, "profiles", "nested", "crop.json")));
        Assert.Equal("backup", File.ReadAllText(Path.Combine(destination, "recovery_v2.json.bak")));
        Assert.Equal("backup", File.ReadAllText(Path.Combine(legacy, "recovery_v2.json.bak")));
    }

    [Fact]
    public void ExistingDestinationIsNeverMergedOrOverwritten()
    {
        string destination = AppDataPaths.MigrateDirectory(_root, "legacy");
        Directory.CreateDirectory(Path.Combine(_root, "legacy"));
        File.WriteAllText(Path.Combine(_root, "legacy", "settings.json"), "old");
        File.WriteAllText(Path.Combine(destination, "settings.json"), "new");
        File.WriteAllText(Path.Combine(_root, "legacy", "extra.json"), "old");
        Assert.Equal(destination, AppDataPaths.MigrateDirectory(_root, "legacy"));
        Assert.Equal("new", File.ReadAllText(Path.Combine(destination, "settings.json")));
        Assert.False(File.Exists(Path.Combine(destination, "extra.json")));
    }

    [Fact]
    public void FailedCopyLeavesDestinationAbsentAndCanRetry()
    {
        string legacy = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(legacy);
        string file = Path.Combine(legacy, "settings.json");
        File.WriteAllText(file, "saved");
        using (FileStream locked = new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(legacy, AppDataPaths.MigrateDirectory(_root, "legacy"));
            Assert.False(Directory.Exists(Path.Combine(_root, "FreeVideoStudio")));
        }
        string destination = AppDataPaths.MigrateDirectory(_root, "legacy");
        Assert.Equal("saved", File.ReadAllText(Path.Combine(destination, "settings.json")));
        Assert.Empty(Directory.GetDirectories(_root, "*.migration-*"));
    }

    [Fact]
    public void BothLegacyRootsAreCopiedWithMostRecentRootTakingPrecedence()
    {
        foreach (string name in new[] { "recent", "older" })
        {
            Directory.CreateDirectory(Path.Combine(_root, name));
            File.WriteAllText(Path.Combine(_root, name, "settings.json"), name);
        }
        File.WriteAllText(Path.Combine(_root, "older", "backup.json"), "saved");
        string destination = AppDataPaths.MigrateDirectory(_root, "recent", "older");
        Assert.Equal("recent", File.ReadAllText(Path.Combine(destination, "settings.json")));
        Assert.Equal("saved", File.ReadAllText(Path.Combine(destination, "backup.json")));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
