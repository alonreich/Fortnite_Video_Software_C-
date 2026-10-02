using System.IO.Compression;
using FreeVideoStudio.App.Services;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class CompactUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fvs-patch-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("../FreeVideoStudio.exe")]
    [InlineData("nested/FreeVideoStudio.exe")]
    [InlineData("Uninstall.exe")]
    public void RejectsUnexpectedExecutablePaths(string entry)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "update.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry(entry).Open())) writer.Write("payload");
        Assert.Throws<IOException>(() => UpdateService.ExtractCompactInstaller(path, _root));
    }

    [Fact]
    public void RejectsAdditionalDllBeforeExtractingAnyExecutable()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "update.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("FreeVideoStudio.exe").Open())) writer.Write("payload");
            zip.CreateEntry("unexpected.dll");
        }
        Assert.Throws<IOException>(() => UpdateService.ExtractCompactInstaller(path, _root));
        Assert.False(File.Exists(Path.Combine(_root, "FreeVideoStudio.exe")));
    }

    [Fact]
    public void ExtractsOnlyTheInstallerAndRefusesToOverwriteAnExistingFile()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "update.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("FreeVideoStudio.exe").Open())) writer.Write("payload");
        Assert.Equal("payload", File.ReadAllText(UpdateService.ExtractCompactInstaller(path, _root)));
        Assert.Throws<IOException>(() => UpdateService.ExtractCompactInstaller(path, _root));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{\"SchemaVersion\":99999}")]
    public void DamagedOrNewerSettingsStopMigrationWithoutChangingTheirBytes(string contents)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "settings.json"); File.WriteAllText(path, contents);
        Assert.Throws<IOException>(() => UpgradeCoordinator.ValidateSettingsFile(path));
        Assert.Equal(contents, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_root));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
