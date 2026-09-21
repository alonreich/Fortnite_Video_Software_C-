// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/07_UNDO_AND_HISTORY.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using FortniteVideoSoftware.Core.Media;
using FortniteVideoSoftware.Core.Project;
using FortniteVideoSoftware.Core.Undo;
using Xunit;

namespace FortniteVideoSoftware.Core.Tests;

/// <summary>
/// UNDO_24 — the sidecar described in <c>07_UNDO_AND_HISTORY.md</c> §4 and listed as open work in
/// §5. History that survives closing the application, kept out of the <c>.fvsproj</c> because the
/// project file is meant to be portable and small.
/// </summary>
public sealed class UndoSidecarStoreTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "fvs_history_" + Guid.NewGuid().ToString("N"));

    private readonly string _projectPath;

    public UndoSidecarStoreTests()
        => _projectPath = Path.Combine(_folder, "montage.fvsproj");

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
        catch (IOException) { /* a temp folder the OS is still holding; harmless. */ }
    }

    private UndoSidecarStore NewStore() => new(_folder);

    private static ProjectDocument Doc(double baseSpeed, string title) => new()
    {
        Source = new SourceClip { FilePath = @"C:\clips\raw.mp4", DurationMs = 60_000 },
        BaseSpeed = baseSpeed,
        Title = title,
    };

    private static UndoStack<ProjectDocument> StackWith(params double[] speeds)
    {
        var stack = new UndoStack<ProjectDocument>(Doc(1.0, "start"));
        foreach (double s in speeds) stack.Apply(Doc(s, $"speed {s}"), $"set speed {s}");
        return stack;
    }

    // ── The point of the whole class ────────────────────────────────────────────────────────

    /// <summary>
    /// UNDO_24 — §4's actual promise: history survives an app restart. Before the sidecar existed,
    /// quitting the application discarded every step of how a montage was built, with no warning
    /// and nothing to press Ctrl+Z against on the way back in.
    /// </summary>
    [Fact]
    public void HistorySurvivesARoundTripThroughDisk()
    {
        UndoStack<ProjectDocument> original = StackWith(1.5, 2.0);
        Assert.True(NewStore().Save(_projectPath, "fp-1", original));

        UndoSidecar? loaded = NewStore().Load(_projectPath, "fp-1");
        Assert.NotNull(loaded);

        var restored = new UndoStack<ProjectDocument>(Doc(2.0, "current"));
        restored.Restore(loaded!.Undo, loaded.Redo);

        Assert.Equal(2, restored.UndoCount);
        Assert.Equal("set speed 2", restored.NextUndoLabel);

        restored.Undo();
        Assert.Equal(1.5, restored.Current.BaseSpeed);
        restored.Undo();
        Assert.Equal(1.0, restored.Current.BaseSpeed);
    }

    [Fact]
    public void RedoBranchSurvivesToo()
    {
        UndoStack<ProjectDocument> original = StackWith(1.5, 2.0);
        original.Undo();
        Assert.Equal(1, original.RedoCount);

        NewStore().Save(_projectPath, "fp-1", original);
        UndoSidecar? loaded = NewStore().Load(_projectPath, "fp-1");

        Assert.NotNull(loaded);
        Assert.Single(loaded!.Redo);
    }

    // ── Refusals — all of which mean "start empty" ──────────────────────────────────────────

    [Fact]
    public void NoSidecarIsNotAnError()
        => Assert.Null(NewStore().Load(_projectPath, "fp-1"));

    /// <summary>
    /// UNDO_24 — the fingerprint check. A project edited elsewhere, or restored from a backup, has
    /// a history that no longer describes it. Replaying those snapshots would hand the user a
    /// document they never authored — quietly, which is the worst version of wrong.
    /// </summary>
    [Fact]
    public void AHistoryForADifferentVersionOfTheProjectIsDiscarded()
    {
        NewStore().Save(_projectPath, "fp-1", StackWith(1.5));
        Assert.Null(NewStore().Load(_projectPath, "fp-CHANGED"));
    }

    [Fact]
    public void ASidecarFromAnotherSchemaIsDiscarded()
    {
        var store = NewStore();
        store.Save(_projectPath, "fp-1", StackWith(1.5));

        string path = store.PathFor(_projectPath);
        string text = File.ReadAllText(path).Replace(
            $"\"schema_version\": {UndoSidecarStore.SchemaVersion}",
            "\"schema_version\": 99");
        File.WriteAllText(path, text);

        Assert.Null(NewStore().Load(_projectPath, "fp-1"));
    }

    [Fact]
    public void AnUnparseableSidecarIsDiscardedRatherThanThrown()
    {
        var store = NewStore();
        Directory.CreateDirectory(_folder);
        File.WriteAllText(store.PathFor(_projectPath), "{ this is not json");

        Assert.Null(store.Load(_projectPath, "fp-1"));
    }

    [Fact]
    public void AnEmptyHistoryIsNotWorthRestoring()
    {
        NewStore().Save(_projectPath, "fp-1", new UndoStack<ProjectDocument>(Doc(1.0, "start")));
        Assert.Null(NewStore().Load(_projectPath, "fp-1"));
    }

    // ── Identity and bounds ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// UNDO_24 — two projects called <c>montage.fvsproj</c> in two folders are two projects.
    /// Sharing one history between them would hand a user edits they never made.
    /// </summary>
    [Fact]
    public void TwoProjectsWithTheSameFileNameDoNotShareAHistory()
    {
        var store = NewStore();
        string a = Path.Combine(_folder, "a", "montage.fvsproj");
        string b = Path.Combine(_folder, "b", "montage.fvsproj");

        Assert.NotEqual(store.PathFor(a), store.PathFor(b));
    }

    [Fact]
    public void TheSamePathSpeltDifferentlyIsTheSameProject()
    {
        var store = NewStore();
        string a = Path.Combine(_folder, "montage.fvsproj");
        string b = Path.Combine(_folder, ".", "montage.fvsproj");

        Assert.Equal(store.PathFor(a), store.PathFor(b));
    }

    /// <summary>
    /// U2 — the ceiling applies to the FILE. A cap enforced only in memory is a cap a second code
    /// path walks straight past, and a sidecar is exactly that second path.
    /// </summary>
    [Fact]
    public void TheStoredHistoryIsCapped()
    {
        var stack = new UndoStack<ProjectDocument>(Doc(1.0, "start"), maxDepth: 500);
        for (int i = 1; i <= UndoSidecarStore.MaxEntries + 25; i++)
            stack.Apply(Doc(1.0 + (i / 100.0), $"s{i}"), $"edit {i}");

        NewStore().Save(_projectPath, "fp-1", stack);
        UndoSidecar? loaded = NewStore().Load(_projectPath, "fp-1");

        Assert.NotNull(loaded);
        Assert.Equal(UndoSidecarStore.MaxEntries, loaded!.Undo.Count);

        // Trimmed from the OLDEST end, matching U2 — the newest steps are the ones a user reaches.
        Assert.Equal($"edit {UndoSidecarStore.MaxEntries + 25}", loaded.Undo[^1].Label);
    }

    [Fact]
    public void DeleteRemovesTheSidecar()
    {
        var store = NewStore();
        store.Save(_projectPath, "fp-1", StackWith(1.5));
        Assert.True(File.Exists(store.PathFor(_projectPath)));

        store.Delete(_projectPath);

        Assert.False(File.Exists(store.PathFor(_projectPath)));
        Assert.Null(store.Load(_projectPath, "fp-1"));
    }

    /// <summary>
    /// UNDO_24 — one unreadable entry costs that step, not the session. The stack is a list of
    /// independent states rather than a chain, so dropping one and keeping the rest is strictly
    /// better than discarding everything.
    /// </summary>
    [Fact]
    public void OneCorruptEntryDoesNotDiscardTheRest()
    {
        var store = NewStore();
        store.Save(_projectPath, "fp-1", StackWith(1.5, 2.0, 2.5));

        string path = store.PathFor(_projectPath);
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var undo = root["undo"]!.AsArray();
        undo[1]!.AsObject()["state"] = new System.Text.Json.Nodes.JsonObject { ["format"] = "not-a-project" };
        File.WriteAllText(path, root.ToJsonString());

        UndoSidecar? loaded = store.Load(_projectPath, "fp-1");

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Undo.Count);
    }
}
