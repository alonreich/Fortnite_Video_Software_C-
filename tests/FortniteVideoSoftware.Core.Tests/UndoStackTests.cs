using System;
using System.Collections.Generic;
using System.Threading;
using FortniteVideoSoftware.Core.Undo;
using Xunit;

namespace FortniteVideoSoftware.Core.Tests;

/// <summary>
/// UNDO_10 / UNDO_11. These pin the four rules (U1..U4) that were lifted from the Granular Speed
/// Editor's PushUndo. They were paid for in bug reports once; these tests are what stop the
/// generalisation from quietly losing any of them.
/// </summary>
public class UndoStackTests
{
    /// <summary>Immutable with value equality — the contract UndoStack requires of its state type.</summary>
    private sealed record Doc(string Text, int Number = 0);

    private static UndoStack<Doc> NewStack(string initial = "a") => new(new Doc(initial));

    // ── Basic movement ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Apply_ThenUndo_RestoresThePreviousState()
    {
        UndoStack<Doc> s = NewStack("a");
        s.Apply(new Doc("b"), "type b");

        Assert.Equal(new Doc("b"), s.Current);
        Assert.True(s.CanUndo);
        Assert.False(s.CanRedo);

        s.Undo();
        Assert.Equal(new Doc("a"), s.Current);
        Assert.False(s.CanUndo);
        Assert.True(s.CanRedo);
    }

    [Fact]
    public void Redo_ReturnsToTheUndoneState()
    {
        UndoStack<Doc> s = NewStack("a");
        s.Apply(new Doc("b"), "type b");
        s.Undo();
        s.Redo();

        Assert.Equal(new Doc("b"), s.Current);
        Assert.True(s.CanUndo);
        Assert.False(s.CanRedo);
    }

    [Fact]
    public void UndoAndRedo_OnEmptyHistory_ReturnNullAndDoNotThrow()
    {
        UndoStack<Doc> s = NewStack("a");
        Assert.Null(s.Undo());
        Assert.Null(s.Redo());
        Assert.Equal(new Doc("a"), s.Current);
    }

    [Fact]
    public void Labels_NameTheActionNotTheState()
    {
        UndoStack<Doc> s = NewStack("a");
        s.Apply(new Doc("b"), "delete segment");

        Assert.Equal("delete segment", s.NextUndoLabel);
        s.Undo();
        Assert.Equal("delete segment", s.NextRedoLabel);
    }

    // ── U1: gesture coalescing ──────────────────────────────────────────────────────────────────

    [Fact]
    public void U1_OneDragCollapsesToASingleUndoEntry()
    {
        UndoStack<Doc> s = NewStack("start");

        // 200 pointer-move updates, as a real drag produces.
        for (int i = 1; i <= 200; i++)
            s.Apply(new Doc("drag", i), "move zoom box", "zoom-edge");

        Assert.Equal(1, s.UndoCount);
        Assert.Equal(new Doc("drag", 200), s.Current);

        // One Ctrl+Z undoes the WHOLE drag, back to before it began.
        s.Undo();
        Assert.Equal(new Doc("start"), s.Current);
    }

    [Fact]
    public void U1_DifferentGestureKeysDoNotMerge()
    {
        UndoStack<Doc> s = NewStack("a");
        s.Apply(new Doc("b"), "move zoom box", "zoom-edge");
        s.Apply(new Doc("c"), "resize segment", "seg-edge");

        Assert.Equal(2, s.UndoCount);
    }

    [Fact]
    public void U1_EndGestureSplitsTwoDragsOfTheSameHandle()
    {
        UndoStack<Doc> s = NewStack("a");
        s.Apply(new Doc("b"), "move zoom box", "zoom-edge");
        s.EndGesture();                                        // pointer released
        s.Apply(new Doc("c"), "move zoom box", "zoom-edge");   // second, separate drag

        Assert.Equal(2, s.UndoCount);
    }

    [Fact]
    public void U1_GestureExpiresAfterTheIdleWindow()
    {
        UndoStack<Doc> s = NewStack("a");
        s.Apply(new Doc("b"), "move", "k");
        Thread.Sleep(UndoStack<Doc>.GestureIdleMs + 150);
        s.Apply(new Doc("c"), "move", "k");

        Assert.Equal(2, s.UndoCount);
    }

    [Fact]
    public void U1_DiscreteActionAfterAGestureIsItsOwnEntry()
    {
        UndoStack<Doc> s = NewStack("a");
        s.Apply(new Doc("b"), "move", "k");
        s.Apply(new Doc("c"), "delete segment");   // null key ends the gesture
        s.Apply(new Doc("d"), "move", "k");        // same key, but the gesture was closed

        Assert.Equal(3, s.UndoCount);
    }

    // ── U2: ceiling ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void U2_DepthIsCappedAndTheOldestEntriesAreDropped()
    {
        UndoStack<Doc> s = new(new Doc("s0"), maxDepth: 5);
        for (int i = 1; i <= 20; i++) s.Apply(new Doc($"s{i}"), $"edit {i}");

        Assert.Equal(5, s.UndoCount);

        // Undoing all the way back reaches the oldest SURVIVING state, not the original.
        while (s.CanUndo) s.Undo();
        Assert.Equal(new Doc("s15"), s.Current);
    }

    [Fact]
    public void U2_DefaultDepthMatchesTheGranularEditor()
    {
        Assert.Equal(40, UndoStack<Doc>.DefaultMaxDepth);
        Assert.Equal(40, NewStack().MaxDepth);
    }

    // ── U3: redo invalidation ───────────────────────────────────────────────────────────────────

    [Fact]
    public void U3_EditingAfterUndoDiscardsTheRedoBranch()
    {
        UndoStack<Doc> s = NewStack("a");
        s.Apply(new Doc("b"), "to b");
        s.Apply(new Doc("c"), "to c");
        s.Undo();
        Assert.True(s.CanRedo);

        s.Apply(new Doc("d"), "to d");

        Assert.False(s.CanRedo);
        Assert.Null(s.Redo());
        Assert.Equal(new Doc("d"), s.Current);
    }

    // ── U4: no-op rejection ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void U4_ApplyingAnIdenticalStateDoesNotGrowTheStack()
    {
        UndoStack<Doc> s = NewStack("a");

        Assert.False(s.Apply(new Doc("a"), "no change"));
        Assert.Equal(0, s.UndoCount);
        Assert.False(s.CanUndo);
    }

    [Fact]
    public void U4_ANoOpDoesNotOpenAGestureWindow()
    {
        // The trap: if a no-op registered its gesture key, the user's next REAL edit would be
        // swallowed into it and become un-undoable on its own.
        UndoStack<Doc> s = NewStack("a");
        s.Apply(new Doc("a"), "no change", "k");
        s.Apply(new Doc("b"), "real edit", "k");

        Assert.Equal(1, s.UndoCount);
        s.Undo();
        Assert.Equal(new Doc("a"), s.Current);
    }

    // ── Re-entrancy ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RestoringDoesNotRecordHistory()
    {
        // A Changed handler that writes back into the stack — the shape of a real UI, where undo
        // updates controls whose change events call Apply. Without the guard the history grows on
        // every Ctrl+Z and undo can never reach the beginning.
        UndoStack<Doc> s = NewStack("a");
        s.Apply(new Doc("b"), "to b");

        s.Changed += (_, _) => s.Apply(new Doc("echo"), "control echoed");
        s.Undo();

        Assert.Equal(new Doc("a"), s.Current);
        Assert.Equal(0, s.UndoCount);
    }

    /// <summary>
    /// UNDO_23 — the same re-entrancy trap on the REDO side. The guard was reset in a
    /// <c>finally</c> that ran before <c>Changed</c> was raised, so the handler's write-back was
    /// recorded as a fresh edit — which also wipes the redo branch (U3) and makes redo a one-shot.
    /// </summary>
    [Fact]
    public void RedoingDoesNotRecordHistory()
    {
        UndoStack<Doc> s = NewStack("a");
        s.Apply(new Doc("b"), "to b");
        s.Undo();

        s.Changed += (_, _) => s.Apply(new Doc("echo"), "control echoed");
        s.Redo();

        Assert.Equal(new Doc("b"), s.Current);
        Assert.Equal(1, s.UndoCount);
        Assert.Equal(0, s.RedoCount);
    }

    /// <summary>
    /// UNDO_23 — <c>Reset</c> raises <c>Changed</c> too, and the handler is repopulating controls
    /// for a DIFFERENT document. Anything it echoes back is not an edit of that document; if it
    /// were recorded, every project open would start with a phantom undo entry that reverts the
    /// user to a document they never had.
    /// </summary>
    [Fact]
    public void ResettingDoesNotRecordHistory()
    {
        UndoStack<Doc> s = NewStack("a");
        s.Apply(new Doc("b"), "to b");

        s.Changed += (_, _) => s.Apply(new Doc("echo"), "control echoed");
        s.Reset(new Doc("loaded"));

        Assert.Equal(new Doc("loaded"), s.Current);
        Assert.Equal(0, s.UndoCount);
        Assert.Equal(0, s.RedoCount);
    }

    [Fact]
    public void Changed_FiresOnApplyUndoRedoAndReset()
    {
        UndoStack<Doc> s = NewStack("a");
        int fired = 0;
        s.Changed += (_, _) => fired++;

        s.Apply(new Doc("b"), "to b");
        s.Undo();
        s.Redo();
        s.Reset(new Doc("z"));

        Assert.Equal(4, fired);
    }

    // ── Reset ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsBothBranchesAndDoesNotRecordHistory()
    {
        UndoStack<Doc> s = NewStack("a");
        s.Apply(new Doc("b"), "to b");
        s.Undo();

        s.Reset(new Doc("loaded project"));

        Assert.Equal(new Doc("loaded project"), s.Current);
        Assert.False(s.CanUndo);
        Assert.False(s.CanRedo);
    }

    // ── Persistence round trip ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Restore_RebuildsHistorySoUndoSurvivesAWindowClose()
    {
        UndoStack<Doc> before = NewStack("a");
        before.Apply(new Doc("b"), "to b");
        before.Apply(new Doc("c"), "to c");
        before.Undo();

        UndoStack<Doc> after = new(before.Current);
        after.Restore(before.UndoEntries, before.RedoEntries);

        Assert.True(after.CanUndo);
        Assert.True(after.CanRedo);
        Assert.Equal("to b", after.NextUndoLabel);

        after.Undo();
        Assert.Equal(new Doc("a"), after.Current);
    }

    [Fact]
    public void Restore_TrimsAnOverlongPersistedHistoryFromTheOldestEnd()
    {
        List<UndoEntry<Doc>> tooMany = new();
        for (int i = 0; i < 100; i++)
            tooMany.Add(new UndoEntry<Doc>(new Doc($"s{i}"), $"edit {i}", 0));

        UndoStack<Doc> s = new(new Doc("now"), maxDepth: 5);
        s.Restore(tooMany, Array.Empty<UndoEntry<Doc>>());

        Assert.Equal(5, s.UndoCount);
        Assert.Equal("edit 99", s.NextUndoLabel);
    }
}
