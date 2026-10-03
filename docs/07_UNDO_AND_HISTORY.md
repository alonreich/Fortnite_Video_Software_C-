# SPECIFICATION 07: UNDO, REDO & EDIT HISTORY

## Code Mini-Map: Bound Source Files & Symbols
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `src/FreeVideoStudio.Core/Undo/UndoStack.cs` | `UndoStack<T>`, `UndoEntry<T>` | `Apply`, `Undo`, `Redo`, `Reset`, `Restore`, `ReplaceCurrent`, `EndGesture`, `NextUndoLabel`, `NextRedoLabel`, `DefaultMaxDepth`, `GestureIdleMs` | Application-wide history |
| ⚠ `src/FreeVideoStudio.Core/Project/ProjectDocument.cs` | `ProjectDocument` | immutable state carried by the stack | CO-GOVERNED by `06_PROJECT_DOCUMENT_MODEL.md` |
| ⚠ `src/FreeVideoStudio.App/GranularSpeedEditorWindow.axaml.cs` | `PushUndo`, `CaptureSnapshot`, `_undoStack` | the ORIGIN of U1–U4; to be migrated onto `UndoStack<T>` | CO-GOVERNED by `01`, `04`, `05` |
| ⚠ `src/FreeVideoStudio.App/GranularSpeedEditorWindow.History.cs` | `GranularSpeedEditorWindow` (Partial) | `ParkHistoryForReopen`, `AdoptParkedHistory`, `_parkedHistory`, `HistoryKey` | CO-GOVERNED by `04` |
| `src/FreeVideoStudio.Core/Undo/UndoSidecarStore.cs` | `UndoSidecarStore`, `UndoSidecar` | `Save`, `Load`, `Delete`, `PathFor`, `MaxEntries`, `SchemaVersion` | Per-machine history sidecar (UNDO_24) |
| ⚠ `src/FreeVideoStudio.App/Services/ProjectSession.cs` | `ProjectSession` | `BeginHistory`, `PushEdit`, `EndGesture`, `Undo`, `Redo`, `NextUndoLabel`, `NextRedoLabel` | CO-GOVERNED by `06`, `08` |
| ⚠ `src/FreeVideoStudio.App/MainWindow.Project.cs` | `MainWindow` (Partial) | `PushProjectEdit`, `EndProjectGesture`, `BeginProjectHistory`, `OnProjectDocumentApplied` | CO-GOVERNED by `06`, `08` |

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
* **`UNDO_20` — one entry point records an edit.** `MainWindow.PushProjectEdit(label, gestureKey)` → `ProjectSession.PushEdit`
  records the state AFTER the edit with the label the user reads in the undo notice; every Main App
  edit goes through it (the Video Merger's equivalent is `MERGEUNDO_01`, `docs/06` item 5).

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
`UndoStack<T>` exists and is unit-tested (`tests/FreeVideoStudio.Core.Tests/UndoStackTests.cs`,
covering U1–U4, re-entrancy, reset and restore).

**Closed since this list was written:**

1. **`UNDO_23` — the re-entrancy guard did not cover the notification, which is the only part that
   mattered.** `_restoring` was reset in a `finally` that ran BEFORE `Changed` was raised. `Changed`
   is the handler that repopulates the controls, and a control raising its own change event calls
   `Apply` straight back in — so every Ctrl+Z recorded the echo as a fresh edit and undo could never
   reach the beginning. The guard now spans the invoke, in `Undo`, `Redo`, `Reset` and `Restore`.
   ⚠️ `UndoStackTests.RestoringDoesNotRecordHistory` was RED and had been for long enough that
   nobody looked — see `08` §3 and `CITEST_01` for why.
2. **`UNDO_24` — the sidecar from §4 exists** (`UndoSidecarStore`), keyed by a hash of the
   project's full path, fingerprinted against the document it describes, capped at `MaxEntries` on
   write as well as on restore, and wired to project open and save. A history that belongs to a
   different version of the project is discarded rather than replayed: replaying it walks the user
   into a document that never existed on this timeline, silently.
3. **`UNDO_25` — the Granular editor's history now survives closing the window.** `OnClosed` called
   `ClearUndoHistory("editor closed")`, reasoning that "nothing survives the window that owned
   them". That is true of native handles and false of the user's work: snapshots are plain data
   (U1). Ten minutes of speed ramps died whenever someone closed the editor to glance at the main
   timeline. The history is now parked, keyed by clip path — restoring it into a DIFFERENT clip
   would apply segment boundaries measured against another video's duration.
4. ~~Ctrl+Z / Ctrl+Y in the main window~~ — done (`PROJSESSION_04`).
5. **`MEMEMODE_01` — every meme property change is undoable.** Mode, corner, size and sound are fields of the
   `MemePlacement` / `EdlMeme` records, so the Speed Editor snapshot, `ProjectDocument` and `MergeEdl` equality
   all see them (U4 stays real). The editor pushes `"change meme"` once per popup confirmation, after which the
   Main App records the returned memes with `PushProjectEdit` and the Merger with `RecordHistory`
   (`MemePresentationModeTests.EveryPropertyChange_IsAnUndoableStep`).

**Still not done:**

1. Migrating `GranularSpeedEditorWindow.PushUndo` onto `UndoStack<ProjectDocument>`. The editor
   keeps its own stack over `EditorSnapshot`, which carries editor-local state (the freeze, the
   selected segment) that `ProjectDocument` does not model. `UNDO_25` closes the user-visible half
   of this — history no longer dies with the window — but there are still two implementations.
2. Undo/Redo in the Music Wizard. It has none. (The Video Merger has it — `MERGEUNDO_01`, `docs/06`
   item 5. The Crop Tool keeps its own `Stack<EditorSnapshot>` undo/redo — Undo/Redo buttons,
   Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z — not yet on `UndoStack<T>`.)
3. Undo/Redo menu labels driven by `NextUndoLabel` / `NextRedoLabel` in the main window. The
   Granular editor already does this.

---

## 6. Edit-State Equality & Gesture Keys (UNDOEQ_01 / UNDOEQ_02)  {#UNDO-EQUALITY}
* **UNDOEQ_01:** `ProjectDocument` overrides `Equals`/`GetHashCode`. Lists are compared element-wise, the mask by `ProfileName`+`Fingerprint`, and `Merge` element-wise. `CreatedUtc`, `ModifiedUtc`, `Title` and `UnknownFields` are excluded. The compiler default compared the per-capture arrays BY REFERENCE and included `UtcNow` stamps, so U4 never fired in production.
* **UNDOEQ_02:** `SaveRecoveryState(label:, gestureKey:)`. Continuous controls pass a key (`speed-dial`, `quality-dial`, `music-nudge`) and call `EndProjectGesture()` on `ValueChangeCompleted`. Every call site names its action.
* Tests: `ProjectDocumentTests.UndoEq_*`.
