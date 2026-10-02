
using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.Undo;

/// <summary>One entry on the history: the state, and what the user did to leave it.</summary>
/// <typeparam name="T">An IMMUTABLE state type. See the warning on <see cref="UndoStack{T}"/>.</typeparam>
public readonly record struct UndoEntry<T>(T State, string Label, long AtUnixMs) where T : class;

/// <summary>
/// UNDO_10 — ONE HISTORY FOR THE WHOLE APPLICATION, OVER ONE IMMUTABLE STATE.
///
/// <para>
/// WHY THIS TYPE EXISTS: undo used to live in exactly one window. The Granular Speed Editor had a
/// genuinely good implementation — coalescing, de-duplication, a ceiling enforced on push, redo
/// invalidation — and the Crop Tool, Music Wizard, meme placement, the merger and the main window
/// had NOTHING. Worse, the editor's forty states were discarded the moment its window was accepted
/// or closed (04_UI_UX_AVALONIA_SPEC.md §6 UI-GRANULAR), so noticing a mistake five seconds too
/// late meant it was permanent.
/// </para>
///
/// <para>
/// The four rules below are lifted DELIBERATELY from that editor's <c>PushUndo</c> rather than
/// reinvented. They were paid for in bug reports; this type generalises them to
/// <c>ProjectDocument</c> so every screen inherits the same behaviour instead of each growing its
/// own half-version.
/// </para>
///
/// <list type="number">
///   <item><description><b>U1 COALESCE A GESTURE.</b> A drag raises hundreds of changes. Entries
///   sharing a <c>gestureKey</c> within <see cref="GestureIdleMs"/> collapse into the ONE state
///   from before the gesture began, so a single Ctrl+Z undoes the whole drag — not one pixel of
///   it. The idle clock is refreshed even when the push is dropped, so the window tracks the LAST
///   movement and a slow drag never splits in two.</description></item>
///   <item><description><b>U2 CEILING ON PUSH.</b> The cap is applied as the entry goes on, so the
///   list can never exceed <see cref="MaxDepth"/> even briefly. Trimming afterwards leaves a window
///   where a snapshot of a large document is alive for no reason.</description></item>
///   <item><description><b>U3 A NEW EDIT BURNS THE REDO BRANCH.</b> Editing after undoing makes
///   every redo state unreachable history. Keeping them offers the user a "redo" that would
///   silently discard the edit they just made.</description></item>
///   <item><description><b>U4 NEVER PUSH A NO-OP.</b> If the new state equals the current one,
///   nothing happened. Growing the stack anyway produces the worst undo bug there is: a Ctrl+Z that
///   visibly does nothing, which users read as undo being broken and stop trusting.</description></item>
/// </list>
///
/// <para>
/// ⚠️ <typeparamref name="T"/> MUST BE IMMUTABLE AND MUST HAVE VALUE EQUALITY. This holds a list of
/// states; if they are mutable, every entry is the same object and undo restores the present —
/// the defect the type exists to prevent. A C# <c>record</c> of value types and immutable
/// collections qualifies (<c>ProjectDocument</c> is exactly this). UI controls, bitmaps, streams
/// and IPC handles are forbidden, the rule UI-GRANULAR already states, generalised. U4 relies on
/// <see cref="object.Equals(object)"/> being a real state comparison — a reference-equality default
/// silently disables it.
/// </para>
///
/// <para>
/// ⚠️ NOT THREAD-SAFE, BY DESIGN. History is UI-thread state. Locking it would invite exactly the
/// dispatcher-blocking this codebase forbids (North Star #6). Call it from the dispatcher only.
/// </para>
/// </summary>
public sealed class UndoStack<T> where T : class
{
    /// <summary>
    /// UNDO_11 — matches the Granular editor's long-standing <c>MaxUndoDepth</c>. Forty is deep
    /// enough that nobody reaches the end in a session, and shallow enough that forty document
    /// snapshots cost nothing worth measuring.
    /// </summary>
    public const int DefaultMaxDepth = 40;

    /// <summary>
    /// U1 — two pushes sharing a gesture key closer together than this are the same gesture. 900ms
    /// is the editor's tested value: long enough to bridge a hesitation mid-drag, short enough that
    /// a deliberate second action is never swallowed into the first.
    /// </summary>
    public const int GestureIdleMs = 900;

    private readonly List<UndoEntry<T>> _undo = new();
    private readonly List<UndoEntry<T>> _redo = new();

    private string? _gestureKey;
    private long _gestureAtMs;
    private bool _restoring;

    public UndoStack(T initial, int maxDepth = DefaultMaxDepth)
    {
        Current = initial ?? throw new ArgumentNullException(nameof(initial));
        MaxDepth = maxDepth > 0 ? maxDepth : DefaultMaxDepth;
    }

    public int MaxDepth { get; }

    /// <summary>The live state. Only <see cref="Apply"/>, <see cref="Undo"/> and <see cref="Redo"/> move it.</summary>
    public T Current { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// What Ctrl+Z would reverse, for the menu item and the tooltip — "Undo move zoom box", never a
    /// bare "Undo". A user who cannot see what they are about to lose does not press the button.
    /// </summary>
    public string? NextUndoLabel => CanUndo ? _undo[^1].Label : null;

    public string? NextRedoLabel => CanRedo ? _redo[^1].Label : null;

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    /// <summary>Raised after any change to <see cref="Current"/>, <see cref="CanUndo"/> or <see cref="CanRedo"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Records <paramref name="label"/> as the way the user left the CURRENT state, then moves to
    /// <paramref name="next"/>.
    ///
    /// <para>
    /// ⚠️ THE LABEL DESCRIBES THE ACTION BEING TAKEN, NOT THE STATE BEING STORED. "delete segment"
    /// means pressing Ctrl+Z brings the segment back. This is the one thing that is easy to get
    /// backwards and produces an undo menu that names the wrong operation.
    /// </para>
    ///
    /// <para>
    /// <paramref name="gestureKey"/> groups a continuous gesture (a drag, a slider sweep). Pass the
    /// same key for every push in that gesture and <see langword="null"/> for a discrete action.
    /// </para>
    /// </summary>
    /// <returns><see langword="true"/> if a new history entry was created.</returns>
    public bool Apply(T next, string label, string? gestureKey = null)
    {
        ArgumentNullException.ThrowIfNull(next);

        if (_restoring) return false;

        if (Equals(Current, next)) return false;

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        bool sameGesture = false;
        if (gestureKey != null)
        {
            sameGesture = _gestureKey == gestureKey && (nowMs - _gestureAtMs) < GestureIdleMs;
            _gestureAtMs = nowMs;
            _gestureKey = gestureKey;
        }
        else
        {
            EndGesture();
        }

        if (sameGesture)
        {
            Current = next;
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        _undo.Add(new UndoEntry<T>(Current, label, nowMs));

        while (_undo.Count > MaxDepth) _undo.RemoveAt(0);

        _redo.Clear();

        Current = next;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Closes the current gesture so the next push starts a new history entry even if it carries the
    /// same key. Call on pointer-release: without it, two drags of the same handle within the idle
    /// window merge into one, and the user's second drag cannot be undone separately.
    /// </summary>
    public void EndGesture()
    {
        _gestureKey = null;
        _gestureAtMs = 0;
    }

    /// <summary>Steps back one entry. Returns the restored state, or <see langword="null"/> if there is nothing to undo.</summary>
    public T? Undo()
    {
        if (!CanUndo) return null;

        UndoEntry<T> entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        _redo.Add(new UndoEntry<T>(Current, entry.Label, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        _restoring = true;
        try
        {
            Current = entry.State;
            EndGesture();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _restoring = false;
        }

        return Current;
    }

    public T? Redo()
    {
        if (!CanRedo) return null;

        UndoEntry<T> entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        _undo.Add(new UndoEntry<T>(Current, entry.Label, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        while (_undo.Count > MaxDepth) _undo.RemoveAt(0);

        _restoring = true;
        try
        {
            Current = entry.State;
            EndGesture();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _restoring = false;
        }

        return Current;
    }

    /// <summary>
    /// Replaces the state WITHOUT recording history, and clears both branches.
    /// <para>
    /// This is for loading a different project or starting a new one — moments where the old
    /// history describes a document that is no longer open. It is NOT for ordinary edits: a
    /// "cheap" reset to avoid a snapshot is how a user loses the ability to undo the step that
    /// mattered.
    /// </para>
    /// </summary>
    public void Reset(T state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _restoring = true;
        try
        {
            Current = state;
            _undo.Clear();
            _redo.Clear();
            EndGesture();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>
    /// MERGEUNDO_01 — swaps <see cref="Current"/> for an EQUIVALENT normalised form WITHOUT touching
    /// either branch. For the moment right after a window applied an undo/redo target and re-read its
    /// own state: sub-microsecond rounding must not become a phantom entry that burns the redo branch
    /// (U3). Not for edits: a real change goes through <see cref="Apply"/>. Raises no event.
    /// </summary>
    public void ReplaceCurrent(T state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Current = state;
    }

    /// <summary>Oldest-first history, for persistence and for a history panel. Excludes <see cref="Current"/>.</summary>
    public IReadOnlyList<UndoEntry<T>> UndoEntries => _undo;

    public IReadOnlyList<UndoEntry<T>> RedoEntries => _redo;

    /// <summary>
    /// Rebuilds a stack from persisted entries (07 §4 UNDO-PERSIST). Entries beyond
    /// <see cref="MaxDepth"/> are dropped from the OLDEST end, matching U2.
    /// </summary>
    public void Restore(IReadOnlyList<UndoEntry<T>> undoEntries, IReadOnlyList<UndoEntry<T>> redoEntries)
    {
        _undo.Clear();
        _redo.Clear();
        if (undoEntries != null)
        {
            foreach (UndoEntry<T> e in undoEntries) _undo.Add(e);
            while (_undo.Count > MaxDepth) _undo.RemoveAt(0);
        }
        if (redoEntries != null)
        {
            foreach (UndoEntry<T> e in redoEntries) _redo.Add(e);
            while (_redo.Count > MaxDepth) _redo.RemoveAt(0);
        }
        EndGesture();

        _restoring = true;
        try { Changed?.Invoke(this, EventArgs.Empty); }
        finally { _restoring = false; }
    }
}
