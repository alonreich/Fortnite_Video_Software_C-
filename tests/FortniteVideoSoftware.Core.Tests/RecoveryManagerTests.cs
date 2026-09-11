using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Media;
using Xunit;

namespace FortniteVideoSoftware.Core.Tests;

public class RecoveryManagerTests : IDisposable
{
    private readonly string _testDir;
    private readonly ApplicationPaths _paths;
    private readonly RecoveryManager _recovery;

    public RecoveryManagerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "FortniteVideoSoftware_RecoveryTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _paths = new ApplicationPaths(_testDir);
        _recovery = new RecoveryManager(_paths);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void SaveState_And_LoadState_RoundTripsSuccessfully()
    {
        var state = new JsonObject
        {
            ["loadedVideoPath"] = "C:\\test\\video.mp4",
            ["trimStartMs"] = 1000.0,
            ["trimEndMs"] = 5000.0,
            ["baseSpeed"] = 1.25,
            ["freezeTimeMs"] = 2500.0,
            ["freezeDurationS"] = 1.5
        };

        _recovery.SaveState(state);

        var loaded = _recovery.LoadState();
        Assert.NotNull(loaded);
        Assert.Equal("C:\\test\\video.mp4", loaded["loadedVideoPath"]?.ToString());
        Assert.Equal(1000.0, loaded["trimStartMs"]?.GetValue<double>());
        Assert.Equal(5000.0, loaded["trimEndMs"]?.GetValue<double>());
        Assert.Equal(1.25, loaded["baseSpeed"]?.GetValue<double>());
        Assert.Equal(2500.0, loaded["freezeTimeMs"]?.GetValue<double>());
        Assert.Equal(1.5, loaded["freezeDurationS"]?.GetValue<double>());
    }

    [Fact]
    public void SaveState_PreservesExistingGranularSession()
    {
        // 1. Initial state with granular_session written (as happens while editor is open)
        var initialState = new JsonObject
        {
            ["loadedVideoPath"] = "C:\\test\\video.mp4",
            ["baseSpeed"] = 1.0,
            ["granular_session"] = new JsonObject
            {
                ["open"] = true,
                ["video_path"] = "C:\\test\\video.mp4",
                ["trim_start_ms"] = 0.0,
                ["trim_end_ms"] = 10000.0,
                ["freeze_time_ms"] = 3000.0,
                ["freeze_duration_s"] = 2.0
            }
        };

        _recovery.SaveState(initialState);

        // 2. An app-level background save happens (which does not have granular_session in its payload)
        var appSaveState = new JsonObject
        {
            ["loadedVideoPath"] = "C:\\test\\video.mp4",
            ["baseSpeed"] = 1.0,
            ["volume"] = 0.8
        };

        _recovery.SaveState(appSaveState);

        // 3. Load state and verify granular_session survived untouched
        var reloaded = _recovery.LoadState();
        Assert.NotNull(reloaded);
        Assert.True(reloaded.ContainsKey("granular_session"));

        var granNode = reloaded["granular_session"]?.AsObject();
        Assert.NotNull(granNode);
        Assert.True(granNode["open"]?.GetValue<bool>());
        Assert.Equal(3000.0, granNode["freeze_time_ms"]?.GetValue<double>());
        Assert.Equal(2.0, granNode["freeze_duration_s"]?.GetValue<double>());
    }

    [Fact]
    public void LoadState_WithCorruptedJson_ReturnsNullWithoutThrowing()
    {
        _paths.EnsureWritableDirectories();
        File.WriteAllText(_paths.RecoveryStateFile, "{\"invalid_json\": [ unfinished...");

        var loaded = _recovery.LoadState();
        Assert.Null(loaded);
    }

    [Fact]
    public void GranularSession_SpeedSegmentWithZoom_RoundTripsAccurately()
    {
        var seg = new SpeedSegment(
            StartMs: 2500,
            EndMs: 6000,
            Speed: 0.25,
            ZoomX: 120,
            ZoomY: 80,
            ZoomW: 1280,
            ZoomH: 720,
            ZoomOrigRes: "1920x1080",
            ZoomSlow: true,
            ZoomStartMs: 2700,
            ZoomEndMs: 5800);

        var segNode = new JsonObject
        {
            ["start_ms"] = seg.StartMs,
            ["end_ms"] = seg.EndMs,
            ["speed"] = seg.Speed,
            ["zoom_x"] = seg.ZoomX,
            ["zoom_y"] = seg.ZoomY,
            ["zoom_w"] = seg.ZoomW,
            ["zoom_h"] = seg.ZoomH,
            ["zoom_orig_res"] = seg.ZoomOrigRes,
            ["zoom_slow"] = seg.ZoomSlow,
            ["zoom_start_ms"] = seg.ZoomStartMs,
            ["zoom_end_ms"] = seg.ZoomEndMs
        };

        var segsArray = new JsonArray { segNode };
        var root = new JsonObject
        {
            ["granular_session"] = new JsonObject
            {
                ["open"] = true,
                ["segments"] = segsArray
            }
        };

        _recovery.SaveState(root);

        var loaded = _recovery.LoadState();
        Assert.NotNull(loaded);
        var loadedSegs = loaded["granular_session"]?["segments"]?.AsArray();
        Assert.NotNull(loadedSegs);
        Assert.Single(loadedSegs);

        var item = loadedSegs[0]?.AsObject();
        Assert.NotNull(item);
        Assert.Equal(2500, item["start_ms"]?.GetValue<double>());
        Assert.Equal(6000, item["end_ms"]?.GetValue<double>());
        Assert.Equal(0.25, item["speed"]?.GetValue<double>());
        Assert.Equal(120, item["zoom_x"]?.GetValue<int>());
        Assert.Equal(80, item["zoom_y"]?.GetValue<int>());
        Assert.Equal(1280, item["zoom_w"]?.GetValue<int>());
        Assert.Equal(720, item["zoom_h"]?.GetValue<int>());
        Assert.Equal("1920x1080", item["zoom_orig_res"]?.GetValue<string>());
        Assert.True(item["zoom_slow"]?.GetValue<bool>());
        Assert.Equal(2700, item["zoom_start_ms"]?.GetValue<double>());
        Assert.Equal(5800, item["zoom_end_ms"]?.GetValue<double>());
    }
}
