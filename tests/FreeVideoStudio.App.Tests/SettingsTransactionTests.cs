// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Infrastructure;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// SETTX_02 — behavioural proof of the settings transaction: one named-mutex acquisition spans
/// read → migrate → patch → serialize → atomic write; unknown/future data survives; Instance is
/// published only after a durable write.
/// </summary>
public sealed class SettingsTransactionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _prevOverride;
    private readonly string _settingsFile;

    public SettingsTransactionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FvsSettingsTx_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _prevOverride = Environment.GetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable);
        Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, _tempDir);
        _settingsFile = Path.Combine(_tempDir, "settings.json");
        SettingsManager.TransactionReadCompletedForTests = null;
    }

    public void Dispose()
    {
        SettingsManager.TransactionReadCompletedForTests = null;
        Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, _prevOverride);
        try
        {
            if (File.Exists(_settingsFile)) File.SetAttributes(_settingsFile, FileAttributes.Normal);
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch (Exception) { /* best-effort temp cleanup */ }
    }

    private AppSettings ReadDisk()
        => JsonSerializer.Deserialize(File.ReadAllText(_settingsFile), SettingsJsonContext.Default.AppSettings)!;

    private JsonObject ReadDiskNode() => JsonNode.Parse(File.ReadAllText(_settingsFile))!.AsObject();

    private void WriteCurrentFile(string extraMembers = "")
        => File.WriteAllText(_settingsFile,
            "{ \"SchemaVersion\": " + SettingsManager.CurrentSchemaVersion + ", \"AutoUpdateChecks\": true, \"Volume\": 10" + extraMembers + " }");

    // 1 ─────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void SameProcessUnrelatedUpdates_BothSurvive()
    {
        WriteCurrentFile();
        SettingsManager.Load();

        const int Rounds = 20;
        using var start = new Barrier(2);
        bool allA = true, allB = true;
        var a = new Thread(() => { start.SignalAndWait(); for (int i = 1; i <= Rounds; i++) allA &= SettingsManager.Update(s => s.Volume = i); });
        var b = new Thread(() => { start.SignalAndWait(); for (int i = 1; i <= Rounds; i++) allB &= SettingsManager.Update(s => s.UiSoundVolume = i); });
        a.Start(); b.Start(); a.Join(); b.Join();

        Assert.True(allA);
        Assert.True(allB);
        AppSettings disk = ReadDisk();
        Assert.Equal(Rounds, disk.Volume);
        Assert.Equal(Rounds, disk.UiSoundVolume);
        Assert.Equal(Rounds, SettingsManager.Instance.Volume);
        Assert.Equal(Rounds, SettingsManager.Instance.UiSoundVolume);
    }

    [Fact]
    public void SiblingWriteBeforeTransaction_IsNotRevertedByStaleMemory()
    {
        WriteCurrentFile();
        SettingsManager.Load();                     // this process now holds Theme = Dark in memory
        Assert.Equal(ThemeMode.Dark, SettingsManager.Instance.ThemeMode);

        JsonObject sibling = ReadDiskNode();        // "process B" saves Theme = Light
        sibling["ThemeMode"] = (int)ThemeMode.Light;
        AtomicJsonFile.WriteText(_settingsFile, sibling.ToJsonString());

        Assert.True(SettingsManager.Update(s => s.Volume = 77));   // "process A" changes another field

        AppSettings disk = ReadDisk();
        Assert.Equal(ThemeMode.Light, disk.ThemeMode);
        Assert.Equal(77, disk.Volume);
        Assert.Equal(ThemeMode.Light, SettingsManager.Instance.ThemeMode);
    }

    // 2 ─────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void CrossProcessWriter_CannotCommitBetweenReadAndWrite()
    {
        WriteCurrentFile();
        SettingsManager.Load();

        // A Win32 named mutex is owned by a THREAD, so a second thread contends exactly like a
        // sibling process does.
        using var attempting = new ManualResetEventSlim(false);
        int competitorCommitted = 0;
        bool competitorCommittedInsideTransaction = true;
        Exception? competitorError = null;

        var competitor = new Thread(() =>
        {
            try
            {
                attempting.Set();
                using NamedSystemMutex guard = NamedSystemMutex.Acquire(
                    SettingsManager.SettingsMutexNameForTests, TimeSpan.FromSeconds(30));
                JsonObject doc = ReadDiskNode();
                doc["ThemeMode"] = (int)ThemeMode.Light;
                AtomicJsonFile.WriteText(_settingsFile, doc.ToJsonString());
                Interlocked.Exchange(ref competitorCommitted, 1);
            }
            catch (Exception ex) { competitorError = ex; }
        });

        SettingsManager.TransactionReadCompletedForTests = () =>
        {
            SettingsManager.TransactionReadCompletedForTests = null;
            competitor.Start();
            Assert.True(attempting.Wait(TimeSpan.FromSeconds(10)));
            Thread.Sleep(400);   // ample time for the competitor to commit IF the mutex allowed it
            competitorCommittedInsideTransaction = Volatile.Read(ref competitorCommitted) == 1;
        };

        Assert.True(SettingsManager.Update(s => s.Volume = 42));
        Assert.True(competitor.Join(TimeSpan.FromSeconds(30)));

        Assert.Null(competitorError);
        Assert.False(competitorCommittedInsideTransaction);
        Assert.Equal(1, competitorCommitted);

        // Neither change is lost: ours committed first, the sibling then read it and added its own.
        AppSettings disk = ReadDisk();
        Assert.Equal(42, disk.Volume);
        Assert.Equal(ThemeMode.Light, disk.ThemeMode);
    }

    // 3 ─────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void UnknownFutureRootProperty_SurvivesUpdate()
    {
        WriteCurrentFile(", \"FutureFeature\": { \"Mode\": \"x\", \"Level\": [1, 2, 3] }, \"FutureFlag\": true");
        SettingsManager.Load();

        Assert.True(SettingsManager.Update(s => s.Volume = 55));

        JsonObject disk = ReadDiskNode();
        Assert.Equal(55, (int)disk["Volume"]!);
        Assert.True((bool)disk["FutureFlag"]!);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("{ \"Mode\": \"x\", \"Level\": [1, 2, 3] }"), disk["FutureFeature"]));
    }

    [Fact]
    public void UnknownRootProperty_SurvivesLoadMigration()
    {
        File.WriteAllText(_settingsFile, "{ \"SchemaVersion\": 12, \"AutoUpdateChecks\": true, \"FutureFlag\": \"keep\" }");

        SettingsManager.Load();

        JsonObject disk = ReadDiskNode();
        Assert.Equal(SettingsManager.CurrentSchemaVersion, (int)disk["SchemaVersion"]!);
        Assert.Equal("keep", (string)disk["FutureFlag"]!);
    }

    // 4 ─────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void FutureSchemaVersion_IsNotDowngraded()
    {
        int future = SettingsManager.CurrentSchemaVersion + 50;
        string original = "{ \"SchemaVersion\": " + future + ", \"AutoUpdateChecks\": true, \"Volume\": 10, \"NewerThing\": 7 }";
        File.WriteAllText(_settingsFile, original);

        SettingsManager.Load();
        Assert.Equal(original, File.ReadAllText(_settingsFile));   // Load leaves a newer file untouched
        Assert.Equal(future, SettingsManager.Instance.SchemaVersion);

        Assert.True(SettingsManager.Update(s => s.Volume = 11));

        JsonObject disk = ReadDiskNode();
        Assert.Equal(future, (int)disk["SchemaVersion"]!);
        Assert.Equal(7, (int)disk["NewerThing"]!);
        Assert.Equal(11, (int)disk["Volume"]!);
        Assert.Equal(future, SettingsManager.Instance.SchemaVersion);
    }

    [Fact]
    public void OlderSchema_IsMigratedToCurrentByUpdate()
    {
        File.WriteAllText(_settingsFile, "{ \"SchemaVersion\": 8, \"AutoUpdateChecks\": true, \"ConfirmVideoMergerRemove\": true }");

        Assert.True(SettingsManager.Update(s => s.Volume = 3));

        AppSettings disk = ReadDisk();
        Assert.Equal(SettingsManager.CurrentSchemaVersion, disk.SchemaVersion);
        Assert.False(disk.ConfirmVideoMergerRemove);   // v9 migration ran inside the transaction
        Assert.Equal(3, disk.Volume);
    }

    // 5 ─────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void FailedDurableWrite_LeavesInstanceUnchanged_AndRaisesNothing()
    {
        // The product and this suite target Windows; POSIX rename replaces read-only/open files.
        if (!OperatingSystem.IsWindows()) return;

        WriteCurrentFile();
        SettingsManager.Load();
        AppSettings before = SettingsManager.Instance;
        string bytesBefore = File.ReadAllText(_settingsFile);

        int raised = 0;
        Action<AppSettings> onCommit = _ => Interlocked.Increment(ref raised);
        SettingsManager.Committed += onCommit;
        // Windows: a read-only target, held open without FILE_SHARE_DELETE, cannot be replaced by the
        // atomic rename — the durable write fails AFTER read/patch/serialize succeeded.
        File.SetAttributes(_settingsFile, FileAttributes.ReadOnly);
        var pin = new FileStream(_settingsFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Assert.False(SettingsManager.Update(s => s.Volume = 99));
        }
        finally
        {
            pin.Dispose();
            File.SetAttributes(_settingsFile, FileAttributes.Normal);
            SettingsManager.Committed -= onCommit;
        }

        Assert.Same(before, SettingsManager.Instance);
        Assert.Equal(10, SettingsManager.Instance.Volume);
        Assert.Equal(0, raised);
        Assert.Equal(bytesBefore, File.ReadAllText(_settingsFile));
        Assert.Empty(Directory.GetFiles(_tempDir, "settings.json.*.tmp"));
    }

    [Fact]
    public void LockHeldBySibling_LeavesInstanceUnchanged()
    {
        WriteCurrentFile();
        SettingsManager.Load();
        AppSettings before = SettingsManager.Instance;

        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            using NamedSystemMutex guard = NamedSystemMutex.Acquire(SettingsManager.SettingsMutexNameForTests, TimeSpan.FromSeconds(10));
            held.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        });
        holder.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            Assert.False(SettingsManager.Update(s => s.Volume = 99));
        }
        finally
        {
            release.Set();
            holder.Join();
        }

        Assert.Same(before, SettingsManager.Instance);
        Assert.Equal(10, ReadDisk().Volume);
    }

    [Fact]
    public void ThrowingPatch_LeavesInstanceAndDiskUnchanged()
    {
        WriteCurrentFile();
        SettingsManager.Load();
        AppSettings before = SettingsManager.Instance;
        string bytesBefore = File.ReadAllText(_settingsFile);

        Assert.False(SettingsManager.Update(s => { s.Volume = 1; throw new InvalidOperationException("boom"); }));

        Assert.Same(before, SettingsManager.Instance);
        Assert.Equal(bytesBefore, File.ReadAllText(_settingsFile));
    }

    [Fact]
    public void CorruptFile_IsQuarantined_NotOverwrittenFromStaleMemory()
    {
        WriteCurrentFile();
        SettingsManager.Load();
        Assert.True(SettingsManager.Update(s => s.Volume = 66));   // in-memory: Volume 66

        File.WriteAllText(_settingsFile, "{ this is not json");
        Assert.True(SettingsManager.Update(s => s.UiSoundVolume = 5));

        AppSettings disk = ReadDisk();
        Assert.Equal(5, disk.UiSoundVolume);
        Assert.Equal(new AppSettings().Volume, disk.Volume);        // defaults, NOT the stale 66
        string backup = Assert.Single(Directory.GetFiles(_tempDir, "settings.json.corrupt-*.bak"));
        Assert.Equal("{ this is not json", File.ReadAllText(backup));
        Assert.NotNull(SettingsManager.LoadFailureMessage);
    }

    // 6 ─────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void SettingsWindowSave_IsOneLogicalTransaction()
    {
        string save = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "Controls", "SettingsWindow.Save.cs"));
        Assert.Single(Regex.Matches(save, @"\bSettingsManager\s*\.\s*Update\s*\("));
        Assert.DoesNotMatch(@"\bSettingsManager\s*\.\s*(SetAutoUpdateChecks|Save)\s*\(", save);
        Assert.Empty(ArchitectureRuleTests.FindDirectSettingsWrites(save));

        // Nothing it calls inside the patch opens a second transaction.
        string output = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "Controls", "SettingsWindow.Output.cs"));
        Match apply = Regex.Match(output, @"void\s+ApplyPendingOutputSettings\s*\(AppSettings\s+s\)\s*\{(?<body>[^{}]*)\}");
        Assert.True(apply.Success);
        Assert.DoesNotContain("SettingsManager", apply.Groups["body"].Value);
    }

    // 7 ─────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void UpdateService_UsesTheTransactionHelpers()
    {
        string svc = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "Services", "UpdateService.cs"));
        Assert.True(Regex.Matches(svc, @"\bSettingsManager\s*\.\s*SetAutoUpdateChecks\s*\(").Count >= 1);
        Assert.DoesNotMatch(@"\bSettingsManager\s*\.\s*Save\s*\(", svc);
        Assert.Empty(ArchitectureRuleTests.FindDirectSettingsWrites(svc));

        string mgr = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "Infrastructure", "SettingsManager.cs"));
        Assert.Matches(@"public\s+static\s+bool\s+SetAutoUpdateChecks\s*\(\s*bool\s+enabled\s*\)\s*=>\s*Update\s*\(", mgr);
    }

    [Fact]
    public void SetAutoUpdateChecks_CommitsThroughTheTransaction()
    {
        WriteCurrentFile(", \"FutureFlag\": 1");
        SettingsManager.Load();

        Assert.True(SettingsManager.SetAutoUpdateChecks(false));

        JsonObject disk = ReadDiskNode();
        Assert.False((bool)disk["AutoUpdateChecks"]!);
        Assert.Equal(1, (int)disk["FutureFlag"]!);
        Assert.False(SettingsManager.Instance.AutoUpdateChecks);
    }
}
