# SPECIFICATION 07: UNDO, REDO & EDIT HISTORY

## Code Mini-Map: Bound Source Files & Symbols
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `src/FortniteVideoSoftware.Core/Undo/UndoStack.cs` | `UndoStack<T>`, `UndoEntry<T>` | `Apply`, `Undo`, `Redo`, `Reset`, `Restore`, `EndGesture`, `DefaultMaxDepth`, `GestureIdleMs` | Application-wide history |
| ⚠ `src/FortniteVideoSoftware.Core/Project/ProjectDocument.cs` | `ProjectDocument` | immutable state carried by the stack | CO-GOVERNED by `06_PROJECT_DOCUMENT_MODEL.md` |
| ⚠ `src/FortniteVideoSoftware.App/GranularSpeedEditorWindow.axaml.cs` | `PushUndo`, `CaptureSnapshot`, `_undoStack` | the ORIGIN of U1–U4; to be migrated onto `UndoStack<T>` | CO-GOVERNED by `01`, `04`, `05` |

---

## 1. Why This Domain Exists  {#UNDO-WHY}
Undo lived in exactly one window. The Granular Speed Editor's `PushUndo` was genuinely well built —
gesture coalescing, de-duplication, a ceiling enforced on push, redo invalidation. The Crop Tool,
Music Wizard, meme placement, the Video Merger and the main window had **nothing**.

And even in the editor, the forty states were discarded when the window was accepted or closed
(`04_UI_UX_AVALONIA_SPEC.md` §6 UI-GRANULAR). Noticing a mistake five seconds too late made it
permanent.

`UndoStack<T>` generalises that editor's behaviour to `ProjectDocument` so every screen inherits one
history instead of each growing its own half-version.

---

## 2. The Four Rules Are Inherited, Not Invented  {#UNDO-RULES}
These come from the editor's `PushUndo`. They were paid for in bug reports. Do not "simplify" them.

* **U1 — COALESCE A GESTURE.** A drag raises hundreds of changes. Pushes sharing a `gestureKey`
  within `GestureIdleMs = 900` collapse into the ONE state from before the gesture began, so a single
  Ctrl+Z undoes the whole drag.
  ⚠ The idle clock is refreshed **even when the push is dropped**, so the window tracks the LAST
  movement and a slow drag never splits in two. `EndGesture()` on pointer-release is what keeps two
  successive drags of the same handle separately undoable.
* **U2 — CEILING ON PUSH.** `MaxDepth = 40` (the editor's `MaxUndoDepth`) is applied as the entry
  goes on, never afterwards. Trimming later leaves a window where a large snapshot is alive for
  nothing.
* **U3 — A NEW EDIT BURNS THE REDO BRANCH.** Editing after undoing makes every redo state
  unreachable. Keeping them offers a "redo" that would silently discard the edit just made.
* **U4 — NEVER PUSH A NO-OP.** Equal states mean nothing happened. Growing the stack anyway produces
  the worst undo bug there is: a Ctrl+Z that visibly does nothing, which users read as undo being
  broken and then stop trusting.
  ⚠ Checked BEFORE gesture bookkeeping, so a no-op cannot open a gesture window and swallow the
  user's next real edit.

---

## 3. State Contract & Threading  {#UNDO-STATE}
* ⚠ **`T` MUST BE IMMUTABLE WITH VALUE EQUALITY.** The stack holds a list of states; mutable ones
  make every entry the same object and undo restores the present — the exact defect this prevents.
  A `record` of value types and immutable collections qualifies; `ProjectDocument` is built for it.
  UI controls, bitmaps, streams and IPC handles are forbidden (UI-GRANULAR's rule, generalised).
  U4 depends on `Equals` being a real state comparison — a reference-equality default silently
  disables it.
* ⚠ **LABELS NAME THE ACTION, NOT THE STATE.** "delete segment" means Ctrl+Z brings the segment
  back. Easy to get backwards; the result is an undo menu naming the wrong operation. The label is
  shown to the user — "Undo move zoom box", never a bare "Undo", because a user who cannot see what
  they are about to lose will not press the button.
* ⚠ **RE-ENTRANCY IS GUARDED.** Restoring a state must not record history. In a real UI, undo
  updates controls whose change events call `Apply`; without the `_restoring` guard the history
  grows on every Ctrl+Z and undo can never reach the beginning.
* ⚠ **NOT THREAD-SAFE, BY DESIGN.** History is UI-thread state. Locking it would invite the
  dispatcher-blocking North Star #6 forbids. Dispatcher only.
* **`Reset` is for loading a different project, not for ordinary edits.** A "cheap" reset to avoid a
  snapshot is how a user loses the ability to undo the step that mattered.

---

## 4. Persistence  {#UNDO-PERSIST}
`UndoEntries` / `RedoEntries` expose the history oldest-first, and `Restore` rebuilds it — this is
what lets undo survive a window close, a screen switch and an app restart, which §1 names as the
defect to fix. `Restore` trims from the OLDEST end, matching U2.

⚠ **HISTORY DOES NOT BELONG INSIDE THE `.fvsproj`.** It is per-machine, disposable, and would bloat
a file that is meant to be portable and shareable — sending a colleague a montage should not send
them forty snapshots of how it was made. It belongs in a sidecar keyed to the project.

---

## 5. Open Work Bound To This Spec  {#UNDO-TODO}
`UndoStack<T>` exists and is unit-tested (`tests/FortniteVideoSoftware.Core.Tests/UndoStackTests.cs`,
covering U1–U4, re-entrancy, reset and restore). NOT yet done:

1. The sidecar store described in §4, and its wiring to project open/close.
2. Migrating `GranularSpeedEditorWindow.PushUndo` onto `UndoStack<ProjectDocument>` — the editor
   keeps its own stack until then, and until it moves, closing that window still discards history.
3. Ctrl+Z / Ctrl+Y and Undo/Redo buttons in the main window, Crop Tool, Music Wizard, meme placement
   and the Video Merger, all bound to the one stack.
4. Undo/Redo menu labels driven by `NextUndoLabel` / `NextRedoLabel`.
