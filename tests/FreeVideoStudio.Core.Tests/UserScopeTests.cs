
using System;
using System.IO;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>USERSCOPE_01 — mutable state is per Windows user; the legacy machine root is migrated once.</summary>
public class UserScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fvs_userscope_" + Guid.NewGuid().ToString("N"));
    private string Legacy => Path.Combine(_root, "legacy");
    private string User => Path.Combine(_root, "user");

    public UserScopeTests() => Directory.CreateDirectory(Legacy);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void MigrationCopiesSettingsButNotLocksRecoveryLogsOrRecordings()
    {
        File.WriteAllText(Path.Combine(Legacy, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(Legacy, "crops_coordinations.conf"), "{}");
        File.WriteAllText(Path.Combine(Legacy, "app_session.lock"), "1:2");
        File.WriteAllText(Path.Combine(Legacy, "recovery_v2.json"), "{}");
        Directory.CreateDirectory(Path.Combine(Legacy, "MaskProfiles"));
        File.WriteAllText(Path.Combine(Legacy, "MaskProfiles", "Fortnite.json"), "{}");
        Directory.CreateDirectory(Path.Combine(Legacy, "logs"));
        File.WriteAllText(Path.Combine(Legacy, "logs", "x.log"), "log");
        Directory.CreateDirectory(Path.Combine(Legacy, "voiceovers"));
        File.WriteAllText(Path.Combine(Legacy, "voiceovers", "take.wav"), "wav");

        Assert.True(ApplicationPaths.MigrateLegacyMachineRoot(User, Legacy));

        Assert.True(File.Exists(Path.Combine(User, "settings.json")));
        Assert.True(File.Exists(Path.Combine(User, "crops_coordinations.conf")));
        Assert.True(File.Exists(Path.Combine(User, "MaskProfiles", "Fortnite.json")));
        Assert.False(File.Exists(Path.Combine(User, "app_session.lock")));
        Assert.False(File.Exists(Path.Combine(User, "recovery_v2.json")));
        Assert.False(Directory.Exists(Path.Combine(User, "logs")));
        Assert.False(Directory.Exists(Path.Combine(User, "voiceovers")));
        Assert.True(File.Exists(Path.Combine(Legacy, "settings.json")));
    }

    [Fact]
    public void MigrationRunsOnceAndNeverOverwritesUserState()
    {
        File.WriteAllText(Path.Combine(Legacy, "settings.json"), "legacy");
        Directory.CreateDirectory(User);
        File.WriteAllText(Path.Combine(User, "settings.json"), "mine");

        ApplicationPaths.MigrateLegacyMachineRoot(User, Legacy);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(User, "settings.json")));

        File.WriteAllText(Path.Combine(Legacy, "late.json"), "x");
        Assert.False(ApplicationPaths.MigrateLegacyMachineRoot(User, Legacy));
        Assert.False(File.Exists(Path.Combine(User, "late.json")));
    }

    [Fact]
    public void StateMutexIsScopedToThisUser()
    {
        Assert.StartsWith(@"Global\", StateTransferStore.MutexName);
        Assert.EndsWith("_" + IpcProtocol.UserScope, StateTransferStore.MutexName);
    }
}
