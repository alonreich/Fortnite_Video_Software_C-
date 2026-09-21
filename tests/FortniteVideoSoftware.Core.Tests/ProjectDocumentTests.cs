using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Media;
using FortniteVideoSoftware.Core.Project;
using Xunit;

namespace FortniteVideoSoftware.Core.Tests;

/// <summary>
/// PROJ_01..PROJ_09. These tests exist because a project file is the one artefact in the product
/// whose loss is unrecoverable, and because the risk called out when this feature was proposed was
/// specifically "version 2 of the app cannot open version 1's projects". The round-trip and
/// forward-compatibility cases below are that risk, written down.
/// </summary>
public class ProjectDocumentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fvsproj_tests_" + Guid.NewGuid().ToString("N"));

    public ProjectDocumentTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string PathIn(string name) => Path.Combine(_dir, name);

    private static ProjectDocument SampleDocument() => new()
    {
        Source = new SourceClip
        {
            FilePath = @"C:\clips\gameplay.mp4",
            DurationMs = 60_000,
            Width = 1920,
            Height = 1080,
            Fps = 60,
            SizeBytes = 123_456_789,
            ModifiedUtcSeconds = 1_700_000_000,
        },
        Title = "Victory Royale",
        BaseSpeed = 1.0,
        SourceCutStartMs = 2_000,
        TrimmedDurationMs = 50_000,
        Segments = new List<SpeedSegment>
        {
            new(10_000, 20_000, 0.5),
            new(25_000, 25_100, 0.0),                                    // a freeze
            new(30_000, 35_000, 2.0, 100, 200, 640, 360, "1920x1080", true, 30_000, 31_000),
        },
        Cuts = new List<OutputTimeline.Cut> { new(40.0, 42.0) },
        Memes = new List<MemePlacement>
        {
            new(@"C:\memes\boom.mp4", 12.5, 3.0, "meme_a"),
        },
        Audio = new ProjectAudio
        {
            MusicFilePath = @"C:\mp3\hero.mp3",
            MusicStartSec = 4.5,
            MusicVolume = 0.8,
            VideoVolume = 0.6,
            SidechainDucking = true,
            VoiceOverFilePath = @"C:\vo\take3.wav",
            VoiceOverAtOutputSec = 7.25,
            VoiceOverVolume = 0.9,
        },
        Export = new ProjectExport
        {
            QualityIndex = 3,
            TargetMegabytes = 48.5,
            HardwareMode = "GPU",
            OutputDirectory = @"C:\out",
            PortraitMode = true,
        },
    };

    // ── Round trip ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        ProjectDocument original = SampleDocument();

        JsonObject json = ProjectSerializer.Write(original);
        ProjectDocument? loaded = ProjectSerializer.Read(json, out string? error);

        Assert.Null(error);
        Assert.NotNull(loaded);

        Assert.Equal(original.Title, loaded!.Title);
        Assert.Equal(original.BaseSpeed, loaded.BaseSpeed);
        Assert.Equal(original.SourceCutStartMs, loaded.SourceCutStartMs);
        Assert.Equal(original.TrimmedDurationMs, loaded.TrimmedDurationMs);

        Assert.Equal(original.Source.FilePath, loaded.Source.FilePath);
        Assert.Equal(original.Source.DurationMs, loaded.Source.DurationMs);
        Assert.Equal(original.Source.Width, loaded.Source.Width);
        Assert.Equal(original.Source.Height, loaded.Source.Height);
        Assert.Equal(original.Source.Fps, loaded.Source.Fps);
        Assert.Equal(original.Source.SizeBytes, loaded.Source.SizeBytes);
        Assert.Equal(original.Source.ModifiedUtcSeconds, loaded.Source.ModifiedUtcSeconds);

        Assert.Equal(original.Segments.Count, loaded.Segments.Count);
        for (int i = 0; i < original.Segments.Count; i++)
            Assert.Equal(original.Segments[i], loaded.Segments[i]);

        Assert.Equal(original.Cuts, loaded.Cuts);
        Assert.Equal(original.Memes, loaded.Memes);
        Assert.Equal(original.Audio, loaded.Audio);
        Assert.Equal(original.Export, loaded.Export);
    }

    /// <summary>
    /// PROJ_10 — THE REGRESSION TEST FOR THE BUG THAT MADE THE INTEGRITY WARNING MEANINGLESS.
    ///
    /// <para>
    /// <c>SizeBytes</c> and <c>ModifiedUtcSeconds</c> are <see langword="long"/>. The writer stored
    /// them as long-backed <c>JsonValue</c>s; the reader asked for <see langword="double"/>, which
    /// on a long-backed value returns false, so both came back 0 on EVERY load. Those two fields
    /// are the whole source fingerprint, so <c>CheckSource</c> compared a real file against a
    /// stored zero and shouted "this clip changed" at every project anyone ever reopened.
    /// </para>
    ///
    /// <para>The values below are deliberately past 2^53 in the timestamp and well past
    /// <see cref="int"/> in the size, so a reader that routes through <see langword="double"/> or
    /// truncates to <see langword="int"/> fails here rather than silently rounding.</para>
    /// </summary>
    [Fact]
    public void RoundTrip_PreservesSixtyFourBitSourceFingerprint()
    {
        ProjectDocument original = SampleDocument() with
        {
            Source = SampleDocument().Source with
            {
                SizeBytes = 9_007_199_254_740_995L,   // 2^53 + 3: unrepresentable as a double
                ModifiedUtcSeconds = 4_102_444_800L,  // 2100-01-01, past int.MaxValue
            },
        };

        JsonObject json = ProjectSerializer.Write(original);
        ProjectDocument? loaded = ProjectSerializer.Read(json, out string? error);

        Assert.Null(error);
        Assert.NotNull(loaded);
        Assert.Equal(original.Source.SizeBytes, loaded!.Source.SizeBytes);
        Assert.Equal(original.Source.ModifiedUtcSeconds, loaded.Source.ModifiedUtcSeconds);
    }

    /// <summary>
    /// PROJ_10 — the reader stays forgiving about HOW a number was stored. A hand-edited file, or
    /// one written by a future build that widened a field, must still load.
    /// </summary>
    [Fact]
    public void Read_AcceptsNumbersStoredAsAnyNumericKind()
    {
        JsonObject json = ProjectSerializer.Write(SampleDocument());
        JsonObject source = (JsonObject)json["source"]!;

        source["size_bytes"] = JsonValue.Create("123456789");     // string
        source["duration_ms"] = JsonValue.Create(42);             // int where a double is expected
        source["width"] = JsonValue.Create(1920L);                // long where an int is expected

        ProjectDocument? loaded = ProjectSerializer.Read(json, out string? error);

        Assert.Null(error);
        Assert.Equal(123456789L, loaded!.Source.SizeBytes);
        Assert.Equal(42.0, loaded.Source.DurationMs);
        Assert.Equal(1920, loaded.Source.Width);
    }

    // ── PROJ_11 — the mask and the merge queue ──────────────────────────────────────────────

    private static JsonObject SampleMaskConfig() => new()
    {
        ["schema_version"] = 4,
        ["crops_1080p"] = new JsonObject
        {
            ["loot"] = new JsonObject { ["w"] = 520, ["h"] = 176, ["x"] = 1416, ["y"] = 1389 },
            ["stats"] = new JsonObject { ["w"] = 329, ["h"] = 234, ["x"] = 1619, ["y"] = 30 },
        },
    };

    /// <summary>
    /// PROJ_11 — the mask survives a round trip INCLUDING its configuration.
    ///
    /// <para>
    /// The name alone would not be enough: a profile is editable, so "Apex Legends" on this machine
    /// in May is not necessarily "Apex Legends" as it was in March. Storing the resolved config is
    /// what makes the project reproduce its own export.
    /// </para>
    /// </summary>
    [Fact]
    public void RoundTrip_PreservesTheHudMask()
    {
        JsonObject config = SampleMaskConfig();
        ProjectDocument original = SampleDocument() with
        {
            Mask = new ProjectMask("Apex Legends", ProjectMask.ComputeFingerprint(config), config),
        };

        ProjectDocument? loaded = ProjectSerializer.Read(ProjectSerializer.Write(original), out string? error);

        Assert.Null(error);
        Assert.NotNull(loaded!.Mask);
        Assert.Equal("Apex Legends", loaded.Mask!.ProfileName);
        Assert.Equal(original.Mask!.Fingerprint, loaded.Mask.Fingerprint);
        Assert.True(loaded.Mask.MatchesLive(config));
    }

    /// <summary>
    /// PROJ_11 — the whole point of the fingerprint. A profile edited without being renamed is the
    /// case a name comparison cannot catch, and it is the common case: users tune a mask in place.
    /// </summary>
    [Fact]
    public void AnEditedProfileNoLongerMatchesTheStoredMask()
    {
        JsonObject config = SampleMaskConfig();
        var mask = new ProjectMask("Apex Legends", ProjectMask.ComputeFingerprint(config), config);

        JsonObject edited = SampleMaskConfig();
        edited["crops_1080p"]!["stats"]!["y"] = 31;   // one pixel

        Assert.True(mask.MatchesLive(config));
        Assert.False(mask.MatchesLive(edited));
    }

    /// <summary>
    /// PROJ_11 — the fingerprint is content-addressed, not text-addressed. Two configs that would
    /// produce the same filtergraph must agree, or the mismatch warning fires on key ordering and
    /// gets ignored — which is how warnings die.
    /// </summary>
    [Fact]
    public void TheFingerprintIgnoresKeyOrder()
    {
        JsonObject a = new() { ["alpha"] = 1, ["beta"] = 2 };
        JsonObject b = new() { ["beta"] = 2, ["alpha"] = 1 };

        Assert.Equal(ProjectMask.ComputeFingerprint(a), ProjectMask.ComputeFingerprint(b));
    }

    /// <summary>
    /// PROJ_11 — a hand-edited file whose checksum no longer describes its own config. The CONFIG
    /// is the work; the fingerprint is a checksum of it. Recomputing keeps the "does the live
    /// profile still match" comparison honest instead of comparing against a stale claim.
    /// </summary>
    [Fact]
    public void AStoredFingerprintThatDoesNotDescribeItsConfigIsRecomputed()
    {
        JsonObject config = SampleMaskConfig();
        JsonObject json = ProjectSerializer.Write(SampleDocument() with
        {
            Mask = new ProjectMask("Apex Legends", ProjectMask.ComputeFingerprint(config), config),
        });

        ((JsonObject)json["mask"]!)["fingerprint"] = "DEADBEEFDEADBEEF";

        ProjectDocument? loaded = ProjectSerializer.Read(json, out _);

        Assert.Equal(ProjectMask.ComputeFingerprint(config), loaded!.Mask!.Fingerprint);
        Assert.True(loaded.Mask.MatchesLive(config));
    }

    [Fact]
    public void RoundTrip_PreservesTheMergeQueueInOrder()
    {
        ProjectDocument original = SampleDocument() with
        {
            Merge = new ProjectMerge
            {
                BaseSpeed = 1.25,
                Clips = new[]
                {
                    new MergeClip(@"C:\clips\a.mp4", 0, 0),
                    new MergeClip(@"C:\clips\b.mp4", 3.5, 12.25),
                    new MergeClip(@"C:\clips\c.mp4", 1, 0),
                },
            },
        };

        ProjectDocument? loaded = ProjectSerializer.Read(ProjectSerializer.Write(original), out string? error);

        Assert.Null(error);
        Assert.NotNull(loaded!.Merge);
        Assert.Equal(1.25, loaded.Merge!.BaseSpeed);
        Assert.Equal(original.Merge!.Clips, loaded.Merge.Clips);
    }

    /// <summary>
    /// A queue entry with no path is not a clip. Restoring it as an empty row would leave the user
    /// hunting for something to delete before the merge would run.
    /// </summary>
    [Fact]
    public void MergeEntriesWithoutAPathAreDropped()
    {
        JsonObject json = ProjectSerializer.Write(SampleDocument() with
        {
            Merge = new ProjectMerge { Clips = new[] { new MergeClip(@"C:\clips\a.mp4", 0, 0) } },
        });

        ((JsonArray)json["merge"]!["clips"]!).Add((JsonNode)new JsonObject { ["path"] = "  " });

        ProjectDocument? loaded = ProjectSerializer.Read(json, out _);

        Assert.Single(loaded!.Merge!.Clips);
    }

    /// <summary>
    /// PROJ_02 / PROJ_11 — a schema-1 file has neither key, and that is not a failure. It opens as
    /// a single-clip project with no mask recorded, which is exactly what it was.
    /// </summary>
    [Fact]
    public void ASchemaOneFileLoadsWithNoMaskAndNoMerge()
    {
        JsonObject json = ProjectSerializer.Write(SampleDocument());
        json["schema_version"] = 1;
        json.Remove("mask");
        json.Remove("merge");

        ProjectDocument? loaded = ProjectSerializer.Read(json, out string? error);

        Assert.Null(error);
        Assert.Null(loaded!.Mask);
        Assert.Null(loaded.Merge);
    }

    [Fact]
    public void RoundTrip_ProducesIdenticalTimeline()
    {
        // The real contract: not "the fields match" but "the finished video is the same video".
        ProjectDocument original = SampleDocument();
        ProjectDocument loaded = ProjectSerializer.Read(ProjectSerializer.Write(original), out _)!;

        OutputTimeline a = original.BuildTimeline();
        OutputTimeline b = loaded.BuildTimeline();

        Assert.Equal(a.TotalOutputSeconds, b.TotalOutputSeconds, 6);
        Assert.Equal(a.TotalSourceSeconds, b.TotalSourceSeconds, 6);
        Assert.Equal(a.Chunks.Count, b.Chunks.Count);
        for (int i = 0; i < a.Chunks.Count; i++)
            Assert.Equal(a.Chunks[i], b.Chunks[i]);
    }

    // ── Compatibility ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Read_FutureSchema_IsRefusedWithAnExplanation()
    {
        JsonObject json = ProjectSerializer.Write(SampleDocument());
        json["schema_version"] = ProjectDocument.SchemaVersion + 1;

        ProjectDocument? loaded = ProjectSerializer.Read(json, out string? error);

        Assert.Null(loaded);
        Assert.NotNull(error);
        Assert.Contains("newer version", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_ForeignFile_IsRefusedAndDoesNotThrow()
    {
        JsonObject notAProject = new() { ["hello"] = "world" };

        ProjectDocument? loaded = ProjectSerializer.Read(notAProject, out string? error);

        Assert.Null(loaded);
        Assert.Equal("That is not a project file.", error);
    }

    [Fact]
    public void Read_UnknownFieldsSurviveASaveByAnOlderBuild()
    {
        // The amputation risk: a newer build adds "colour_grade"; this build must carry it through
        // an open-and-save untouched rather than deleting the user's work silently.
        JsonObject json = ProjectSerializer.Write(SampleDocument());
        json["colour_grade"] = new JsonObject { ["lut"] = "teal_orange", ["strength"] = 0.4 };

        ProjectDocument loaded = ProjectSerializer.Read(json, out _)!;
        JsonObject rewritten = ProjectSerializer.Write(loaded);

        Assert.NotNull(rewritten["colour_grade"]);
        Assert.Equal("teal_orange", rewritten["colour_grade"]!["lut"]!.GetValue<string>());
        Assert.Equal(0.4, rewritten["colour_grade"]!["strength"]!.GetValue<double>());
    }

    [Fact]
    public void Read_MissingOptionalSectionsFallBackToDefaults()
    {
        JsonObject json = ProjectSerializer.Write(SampleDocument());
        json.Remove("audio");
        json.Remove("export");
        json.Remove("memes");
        json.Remove("cuts");

        ProjectDocument? loaded = ProjectSerializer.Read(json, out string? error);

        Assert.Null(error);
        Assert.NotNull(loaded);
        Assert.Empty(loaded!.Memes);
        Assert.Empty(loaded.Cuts);
        Assert.Equal(1.0, loaded.Audio.MusicVolume);
        Assert.Equal("Auto", loaded.Export.HardwareMode);
    }

    [Fact]
    public void Read_WrongTypedValuesDoNotThrow()
    {
        JsonObject json = ProjectSerializer.Write(SampleDocument());
        json["base_speed"] = "not a number";
        json["source"]!["width"] = "wide";

        ProjectDocument? loaded = ProjectSerializer.Read(json, out string? error);

        Assert.Null(error);
        Assert.NotNull(loaded);
        Assert.Equal(1.0, loaded!.BaseSpeed);   // fell back
        Assert.Equal(0, loaded.Source.Width);
    }

    [Fact]
    public void Read_MalformedEntriesAreDroppedNotFatal()
    {
        JsonObject json = ProjectSerializer.Write(SampleDocument());
        ((JsonArray)json["memes"]!).Add(new JsonObject { ["at_source_sec"] = 5.0 }); // no path, no id
        ((JsonArray)json["cuts"]!).Add(new JsonObject { ["start_sec"] = 1.0 });      // no end

        ProjectDocument loaded = ProjectSerializer.Read(json, out _)!;

        Assert.Single(loaded.Memes);
        Assert.Single(loaded.Cuts);
    }

    // ── Disk ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Save_AppendsTheExtensionWhenTheUserOmitsIt()
    {
        string requested = PathIn("montage");

        ProjectIoResult result = ProjectStore.Save(SampleDocument(), requested);

        Assert.True(result.Success);
        Assert.EndsWith(ProjectDocument.FileExtension, result.Path!);
        Assert.True(File.Exists(result.Path!));
    }

    [Fact]
    public void SaveThenLoad_FromDisk_Survives()
    {
        string path = PathIn("montage.fvsproj");
        Assert.True(ProjectStore.Save(SampleDocument(), path).Success);

        ProjectDocument? loaded = ProjectStore.Load(path, out string? error, out bool fromBackup);

        Assert.Null(error);
        Assert.False(fromBackup);
        Assert.NotNull(loaded);
        Assert.Equal("Victory Royale", loaded!.Title);
        Assert.Single(loaded.Memes);
    }

    [Fact]
    public void Save_KeepsOnePreviousGenerationAsBackup()
    {
        string path = PathIn("montage.fvsproj");
        ProjectStore.Save(SampleDocument() with { Title = "first" }, path);
        ProjectStore.Save(SampleDocument() with { Title = "second" }, path);

        Assert.True(File.Exists(path + ProjectStore.BackupSuffix));

        ProjectDocument? backup = ProjectStore.Load(path + ProjectStore.BackupSuffix, out _, out _);
        Assert.Equal("first", backup!.Title);
    }

    [Fact]
    public void Load_DamagedLiveFile_RecoversFromBackupAndSaysSo()
    {
        string path = PathIn("montage.fvsproj");
        ProjectStore.Save(SampleDocument() with { Title = "good" }, path);
        ProjectStore.Save(SampleDocument() with { Title = "also good" }, path);   // creates .bak

        File.WriteAllText(path, "{ this is not json");

        ProjectDocument? loaded = ProjectStore.Load(path, out string? error, out bool fromBackup);

        Assert.Null(error);
        Assert.True(fromBackup);
        Assert.Equal("good", loaded!.Title);
    }

    [Fact]
    public void Load_MissingFile_ReportsItRatherThanThrowing()
    {
        ProjectDocument? loaded = ProjectStore.Load(PathIn("nope.fvsproj"), out string? error, out _);

        Assert.Null(loaded);
        Assert.NotNull(error);
    }

    // ── Source integrity ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CheckSource_MissingFile_IsMissing()
    {
        ProjectDocument doc = SampleDocument();
        Assert.Equal(SourceIntegrity.Missing, doc.CheckSource());
    }

    [Fact]
    public void CheckSource_UnfingerprintedOlderProject_IsUnknownNotChanged()
    {
        string clip = PathIn("clip.mp4");
        File.WriteAllText(clip, "pretend video");

        ProjectDocument doc = SampleDocument() with
        {
            Source = new SourceClip { FilePath = clip, DurationMs = 1000, SizeBytes = 0, ModifiedUtcSeconds = 0 },
        };

        Assert.Equal(SourceIntegrity.Unknown, doc.CheckSource());
    }

    [Fact]
    public void CheckSource_SameFile_IsIntact_DifferentSize_IsChanged()
    {
        string clip = PathIn("clip.mp4");
        File.WriteAllText(clip, "pretend video");
        FileInfo info = new(clip);

        ProjectDocument doc = SampleDocument() with
        {
            Source = new SourceClip
            {
                FilePath = clip,
                DurationMs = 1000,
                SizeBytes = info.Length,
                ModifiedUtcSeconds = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(),
            },
        };
        Assert.Equal(SourceIntegrity.Intact, doc.CheckSource());

        ProjectDocument stale = doc with { Source = doc.Source with { SizeBytes = info.Length + 999 } };
        Assert.Equal(SourceIntegrity.Changed, stale.CheckSource());
    }

    // ── Recent list ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Recent_TouchMovesToTopAndDeduplicatesCaseInsensitively()
    {
        RecentProjects recent = new(PathIn("recent.json"));

        recent.Touch(@"C:\a.fvsproj", "A");
        recent.Touch(@"C:\b.fvsproj", "B");
        recent.Touch(@"c:\A.FVSPROJ", "A again");

        IReadOnlyList<RecentProject> list = recent.Read();
        Assert.Equal(2, list.Count);
        Assert.Equal("A again", list[0].Title);
        Assert.Equal("B", list[1].Title);
    }

    [Fact]
    public void Recent_IsCappedAndKeepsTheNewest()
    {
        RecentProjects recent = new(PathIn("recent.json"));
        for (int i = 0; i < RecentProjects.MaxEntries + 5; i++)
            recent.Touch($@"C:\p{i}.fvsproj", $"P{i}");

        IReadOnlyList<RecentProject> list = recent.Read();
        Assert.Equal(RecentProjects.MaxEntries, list.Count);
        Assert.Equal($"P{RecentProjects.MaxEntries + 4}", list[0].Title);
    }

    [Fact]
    public void Recent_MissingFileIsFlaggedNotDropped()
    {
        RecentProjects recent = new(PathIn("recent.json"));
        recent.Touch(PathIn("gone.fvsproj"), "Gone");

        RecentProject entry = Assert.Single(recent.Read());
        Assert.False(entry.Exists);
    }
}
