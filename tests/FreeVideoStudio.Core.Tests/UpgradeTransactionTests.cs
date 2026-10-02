using System.IO.Compression;
using FreeVideoStudio.Core.Infrastructure;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class UpgradeTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fvs-upgrade-tests-" + Guid.NewGuid().ToString("N"));
    private string Folder(string name) { string path = Path.Combine(_root, name); Directory.CreateDirectory(path); return path; }
    private static void Put(string root, string name, string text)
    { string path = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }

    [Theory]
    [InlineData("backup:previous-0")]
    [InlineData("backup:previous-1")]
    [InlineData("activated")]
    public void PowerLossAtEveryRenameRestoresBothOriginalTrees(string stop)
    {
        string current = Folder("current"), old = Folder("old"), store = Folder("backup");
        Put(current, "settings.json", "new settings"); Put(old, "settings.json", "old settings");
        var transaction = DirectoryUpgrade.Create(store, current, [old], preferLegacy: true);
        transaction.Prepare();
        Assert.Throws<IOException>(() => transaction.Activate(step => { if (step == stop) throw new IOException("Power loss"); }));
        var recovered = DirectoryUpgrade.Open(transaction.DirectoryPath, [old, current]);
        recovered.Rollback(); recovered.Rollback();
        Assert.Equal("new settings", File.ReadAllText(Path.Combine(current, "settings.json")));
        Assert.Equal("old settings", File.ReadAllText(Path.Combine(old, "settings.json")));
        Assert.False(recovered.PruneBackup(DateTimeOffset.UtcNow.AddYears(1)));
    }

    [Theory]
    [InlineData(true, "old settings", "new settings")]
    [InlineData(false, "new settings", "old settings")]
    public void ChosenSourceWinsAndConflictingDataSurvivesBackupExpiry(bool legacy, string chosen, string conflict)
    {
        string current = Folder("current"), old = Folder("old");
        Put(current, "settings.json", "new settings"); Put(old, "settings.json", "old settings");
        Put(old, "nested/profile.json", "custom profile");
        var transaction = DirectoryUpgrade.Create(Folder("backup"), current, [old], legacy);
        transaction.Prepare(); transaction.Activate();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        transaction.Confirm(now); transaction.Confirm(now);
        Assert.False(transaction.PruneBackup(now.AddDays(29)));
        Assert.True(transaction.PruneBackup(now.AddDays(31)));
        Assert.Equal(chosen, File.ReadAllText(Path.Combine(current, "settings.json")));
        Assert.Equal(conflict, File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(current, "MigrationConflicts"), "settings.json", SearchOption.AllDirectories))));
        Assert.Equal("custom profile", File.ReadAllText(Path.Combine(current, "nested/profile.json")));
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void FailedCopyNeverActivatesPartialUserData()
    {
        string old = Folder("old"), target = Path.Combine(_root, "new");
        Put(old, "settings.json", "original");
        var tx = DirectoryUpgrade.Create(Folder("store"), target, [old]);
        Assert.Throws<IOException>(() => tx.Prepare(_ => throw new IOException("Disk full")));
        tx.Rollback();
        Assert.False(Directory.Exists(target));
        Assert.Equal("original", File.ReadAllText(Path.Combine(old, "settings.json")));
    }

    [Fact]
    public void RollbackDoesNotOverwriteFilesCreatedAfterTheSwitch()
    {
        string old = Folder("old"), target = Path.Combine(_root, "new");
        Put(old, "settings.json", "original");
        var tx = DirectoryUpgrade.Create(Folder("store"), target, [old]); tx.Prepare(); tx.Activate();
        Put(old, "settings.json", "concurrent data");
        Assert.Throws<IOException>(() => tx.Rollback());
        Assert.Equal("concurrent data", File.ReadAllText(Path.Combine(old, "settings.json")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(tx.DirectoryPath, "previous-0", "settings.json")));
    }

    [Fact]
    public void OverlappingRootsAndJournalTraversalAreRejected()
    {
        string old = Folder("old"), nested = Folder("old/nested"), target = Folder("new"), store = Folder("store");
        Assert.Throws<IOException>(() => DirectoryUpgrade.Create(store, target, [old, nested]));
        var tx = DirectoryUpgrade.Create(store, target, [old]);
        string journalPath = Path.Combine(tx.DirectoryPath, "journal.json");
        var journal = AtomicJsonFile.ReadObject(journalPath)!;
        journal["Originals"]![0]!["Backup"] = "../../outside";
        AtomicJsonFile.WriteObject(journalPath, journal);
        Assert.Throws<IOException>(() => DirectoryUpgrade.Open(tx.DirectoryPath, [target, old]));
    }

    [Fact]
    public void CompactPayloadReusesOnlyBytesMatchingTheNewSignedManifest()
    {
        string payload = Folder("payload"), installed = Folder("installed");
        Put(payload, InstallPayload.ExecutableName, "new app");
        Put(payload, "backend/runtime.dll", "runtime");
        Put(installed, "backend/runtime.dll", "runtime");
        InstallPayload.WriteManifest(payload);
        using var zip = Compact(payload);
        string target = Path.Combine(_root, "extracted");
        InstallPayload.Extract(zip, target, installed);
        InstallPayload.Verify(target);
        Assert.Equal("new app", File.ReadAllText(Path.Combine(target, InstallPayload.ExecutableName)));
        Put(installed, "backend/runtime.dll", "damaged");
        zip.Position = 0;
        Assert.Throws<IOException>(() => InstallPayload.Extract(zip, Path.Combine(_root, "bad"), installed));
    }

    [Theory]
    [InlineData("../outside.exe")]
    [InlineData("/outside.exe")]
    [InlineData("C:/outside.exe")]
    [InlineData("folder\\outside.exe")]
    [InlineData("file.exe:stream")]
    public void ArchiveCannotWriteOutsideStaging(string entry)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry(entry).Open())) writer.Write("bad");
        stream.Position = 0;
        Assert.Throws<IOException>(() => InstallPayload.Extract(stream, Path.Combine(_root, "extract")));
    }

    [Fact]
    public void AppAndUninstallerSizeDoNotChangeRuntimeFingerprint()
    {
        string a = Folder("a"), b = Folder("b");
        Put(a, "runtime.dll", "runtime"); Put(b, "runtime.dll", "runtime");
        Put(a, InstallPayload.ExecutableName, "old"); Put(b, InstallPayload.ExecutableName, "new app with more code");
        Put(b, "Uninstall.exe", "uninstaller");
        Assert.Equal(RuntimePayloadManifest.FromFolder(a), RuntimePayloadManifest.FromFolder(b));
    }

    [Fact]
    public void EqualSizeRuntimeChangesRequireAFullDownload()
    {
        string a = Folder("a"), b = Folder("b");
        Put(a, "runtime.dll", "version1"); Put(b, "runtime.dll", "version2");
        Assert.NotEqual(RuntimePayloadManifest.FromFolder(a).Fingerprint, RuntimePayloadManifest.FromFolder(b).Fingerprint);
    }

    private static MemoryStream Compact(string payload)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (string name in new[] { InstallPayload.ExecutableName, InstallPayload.ManifestName })
                zip.CreateEntryFromFile(Path.Combine(payload, name), name);
        stream.Position = 0; return stream;
    }

    [Fact]
    public void ProjectPathMigrationDoesNotRewriteTheOriginalOrUnknownMetadata()
    {
        string old = Path.Combine(_root, "old"), current = Path.Combine(_root, "new");
        string media = Path.Combine(old, "custom.wav");
        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["audio"] = new System.Text.Json.Nodes.JsonObject { ["voice_over_path"] = media },
            ["future_metadata"] = media
        };
        string before = json.ToJsonString();
        var resolved = MigrationPathResolver.ResolveProject(json, [new(old, current)]);
        Assert.Equal(Path.Combine(current, "custom.wav"), resolved["audio"]!["voice_over_path"]!.GetValue<string>());
        Assert.Equal(media, resolved["future_metadata"]!.GetValue<string>());
        Assert.Equal(before, json.ToJsonString());
        Assert.Equal(old + "-other/file", MigrationPathResolver.Resolve(old + "-other/file", [new(old, current)]));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
