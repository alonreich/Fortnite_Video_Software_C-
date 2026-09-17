using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Ipc;
using FortniteVideoSoftware.Core.Media;
using Xunit;

namespace FortniteVideoSoftware.Core.Tests;

public sealed class CropConfigTests : IAsyncLifetime
{
    private readonly ApplicationPaths _paths = new(Path.Combine(Path.GetTempPath(), "FvsCropTests_" + Guid.NewGuid().ToString("N")));

    [Theory]
    [InlineData("loot", "[520,176,1416,1389]", "521/520", 540, 1462, 10, "[347,117,1544,926]")]
    [InlineData("stats", "[329,234,1619,30]", "59/47", 666, 150, 30, "[220,156,1678,20]")]
    [InlineData("normal_hp", "[496,92,-852,1465]", "255/248", 19, 1503, 20, "[331,61,31,977]")]
    [InlineData("team", "[275,183,-883,1254]", "278/275", 0, 150, 40, "[184,122,10,836]")]
    [InlineData("spectating", "[59,24,-844,1554]", "106/59", 676, 383, 100, "[40,16,36,1036]")]
    public void DefaultsMatchSavedApexLayout(string key, string crop, string scale, int x, int y, int z, string source)
    {
        var config = CropConfigDefaults.Create();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(crop), config["crops_1080p"]![key]));
        Assert.Equal(scale, config["scales"]![key]!.GetValue<string>());
        Assert.Equal(x, config["overlays"]![key]!["x"]!.GetValue<int>());
        Assert.Equal(y, config["overlays"]![key]!["y"]!.GetValue<int>());
        Assert.Equal(z, config["z_orders"]![key]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(source), config["crops_source"]![key]));
        Assert.True(CropConfigStore.IsUsableConfig(config));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("crop")]
    [InlineData("scale")]
    [InlineData("position")]
    [InlineData("source")]
    public async Task DamagedLiveFileRestoresNewestUsableBackupWithoutRotating(string damage)
    {
        _paths.EnsureWritableDirectories();
        var older = CropConfigDefaults.Create();
        older["marker"] = "older";
        var newest = CropConfigDefaults.Create();
        newest["marker"] = "newest";
        newest["schema_version"] = 3; // Valid older profiles remain recoverable.
        newest.Remove("crops_source");
        AtomicJsonFile.WriteObject(_paths.CropCoordinatesFile + ".bak3", older);
        AtomicJsonFile.WriteObject(_paths.CropCoordinatesFile + ".bak2", newest);
        File.WriteAllText(_paths.CropCoordinatesFile + ".bak1", "{broken");
        string originalBackup = File.ReadAllText(_paths.CropCoordinatesFile + ".bak2");

        var damaged = CropConfigDefaults.Create();
        switch (damage)
        {
            case "crop": damaged["crops_1080p"]!["loot"] = new JsonArray(520, 0, 1416, 1389); break;
            case "scale": damaged["scales"]!["loot"] = "1/0"; break;
            case "position": damaged["overlays"]!["loot"]!["x"] = "broken"; break;
            case "source": damaged["crops_source"]!["loot"] = "broken"; break;
        }
        File.WriteAllText(_paths.CropCoordinatesFile, damage == "json" ? "{broken" : damaged.ToJsonString());

        var restored = await new CropConfigStore(_paths).LoadAsync();
        Assert.Equal("newest", restored["marker"]!.GetValue<string>());
        Assert.Equal(originalBackup, File.ReadAllText(_paths.CropCoordinatesFile + ".bak2"));
        Assert.True(JsonNode.DeepEquals(newest, AtomicJsonFile.ReadObject(_paths.CropCoordinatesFile)));
    }

    [Fact]
    public async Task DamagedFileWithoutUsableBackupsUsesNewFortniteDefaults()
    {
        _paths.EnsureWritableDirectories();
        File.WriteAllText(_paths.CropCoordinatesFile, "{broken");
        for (int i = 1; i <= 5; i++) File.WriteAllText(_paths.CropCoordinatesFile + $".bak{i}", "{}");
        var restored = await new CropConfigStore(_paths).LoadAsync();
        Assert.True(JsonNode.DeepEquals(CropConfigDefaults.Create(), restored));
        Assert.True(JsonNode.DeepEquals(restored, AtomicJsonFile.ReadObject(_paths.CropCoordinatesFile)));
    }

    [Fact]
    public async Task InvalidSavePreservesLiveFileAndBackup()
    {
        var store = new CropConfigStore(_paths);
        var config = CropConfigDefaults.Create();
        await store.SaveAsync(config);
        await store.SaveAsync(config);
        string live = File.ReadAllText(_paths.CropCoordinatesFile);
        string backup = File.ReadAllText(_paths.CropCoordinatesFile + ".bak1");
        config["scales"]!["loot"] = "0";
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(config));
        Assert.Equal(live, File.ReadAllText(_paths.CropCoordinatesFile));
        Assert.Equal(backup, File.ReadAllText(_paths.CropCoordinatesFile + ".bak1"));
    }

    [Fact]
    public void RetiredBossLayerCannotReturnThroughSanitizingOrExport()
    {
        var config = CropConfigDefaults.Create();
        string expected = MobileFilterBuilder.Build("[main]", "[hud]", config, showTeammates: true).filterChain;
        config["crops_1080p"]!["BOSS_HP"] = new JsonArray(450, 150, 30, 1320);
        config["scales"]!["BOSS_HP"] = "1";
        config["overlays"]!["BOSS_HP"] = new JsonObject { ["x"] = 30, ["y"] = 1620 };
        config["z_orders"]!["BOSS_HP"] = 20;
        config["crops_source"]!["BOSS_HP"] = new JsonArray(300, 100, 620, 880);

        var sanitized = HudConfig.Sanitize(config);
        foreach (string section in CropConfigDefaults.RequiredSections.Append("crops_source"))
            Assert.DoesNotContain(sanitized[section]!.AsObject(), kvp => HudConfig.IsRetiredRole(kvp.Key));
        Assert.Equal(expected, MobileFilterBuilder.Build("[main]", "[hud]", config, showTeammates: true).filterChain);
        Assert.NotNull(config["crops_1080p"]!["BOSS_HP"]); // Sanitizing does not mutate its input.
    }

    [Fact]
    public async Task NoMaskSurvivesSaveLoadAndProducesNoHudLayers()
    {
        var config = HudConfig.Sanitize(CropConfigDefaults.CreateNoMask());
        var store = new CropConfigStore(_paths);
        await store.SaveAsync(config);
        var loaded = await store.LoadAsync();
        Assert.True(CropConfigDefaults.IsHudFree(loaded));
        string graph = MobileFilterBuilder.Build("[main]", "[hud]", loaded, showTeammates: true).filterChain;
        Assert.Contains("[hud]nullsink", graph);
        Assert.DoesNotContain("v_layer_in_", graph);
    }

    [Fact]
    public void SpectatingIsIncludedByDefaultAndCanBeTurnedOff()
    {
        var config = CropConfigDefaults.CreateNoMask();
        var defaults = CropConfigDefaults.Create();
        config["crops_1080p"]!["spectating"] = defaults["crops_1080p"]!["spectating"]!.DeepClone();
        Assert.Contains("v_layer_in_0", MobileFilterBuilder.Build("[main]", "[hud]", config, false).filterChain);
        Assert.Contains("[hud]nullsink", MobileFilterBuilder.Build("[main]", "[hud]", config, false, showSpectating: false).filterChain);
    }

    [Fact]
    public void MobilePortraitTextOverlayUsesShiftedIntroCanvas()
    {
        var config = CropConfigDefaults.CreateNoMask();
        var (graph, outLabel) = MobileFilterBuilder.Build("[main]", "[hud]", config, false, txtInputLabel: "[text]");
        Assert.Contains("drawbox=x=0:y=0:w=1080:h=1920:color=black:t=fill[v_bg_canvas]", graph);
        Assert.Contains("overlay=x=0:y='if(lt(t,0.11),320,150)'", graph);
        Assert.Contains("overlay=x=0:y='if(lt(t,0.11),170,0)'", graph);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Windows indexers and virus scanners can briefly hold a newly written temp directory.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(_paths.ProgramDataRoot)) Directory.Delete(_paths.ProgramDataRoot, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 5) { await Task.Delay(100); }
        }
    }
}
