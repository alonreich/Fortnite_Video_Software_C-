> **STATUS: OPEN.** Refactoring plan. Nothing here has been applied.
> Supersedes the window-decomposition half of `5-GOD_MODULE_DECOMPOSITION.md` with per-file measurements.

# TASK SPECIFICATION: R2 - GRANULAR_EDITOR_GOD_CLASS

## 1. TARGET CONTEXT & SCOPE
- File Path: `src/FortniteVideoSoftware.App/GranularSpeedEditorWindow.axaml.cs`
- Target Range: whole file — **8,122 lines, 181 fields, 190 methods, 59 event subscriptions, ONE class in ONE file.** Worst methods: `WireUpControls` 522 lines (line 1503), `RedrawTimeline` 274, `WireUpFreezeImage` 196, `PlaybackTimer_Tick` 172, `BuildLaneContent` 127, `RefreshSegmentList` 112.
- Defect Classification: Modernization Bottleneck (single-responsibility violation; unbounded shared-mutable-state surface)

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism: measured responsibility clusters, by identifier reference count in this one file:
  ```
    Zoom 1016 | Segment 667 | Drag 576 | Meme 502 | Canvas 307 | Undo 207
    IpcClient 87 | Timer 78 | Recovery 75 | Redraw 66 | Redo 63 | frameLane 47
  ```
  Twelve independent concerns — a continuous zoom gesture model, a speed-segment document, a pointer drag state machine, meme placement, canvas drawing, an undo/redo history, mpv transport, timers, crash recovery serialisation, and a frame-lane renderer — all reach the SAME 181-field private bag. Any two of them are one field away from coupling, and nothing in the type prevents it.
- **This shape is what produced the defects already on the backlog.** Findings 2 and 4 of the first audit were both unsynchronised shared state with no owner, reachable from anywhere in a type of this class. With 181 fields and 190 methods there is no reviewer who can hold the invariants in their head, so "does this change break something else?" is unanswerable by reading.
- ⚠️ `docs/INDEX.md` marks this file `⚠ CO-GOVERNED BY 01 04 05` — timeline coordinate maths, Avalonia UI/UX, AND lifecycle/storage. Three specs bind one file. That is the documentation telling you the same thing the measurements do.
- **THE PROJECT HAS ALREADY SOLVED THIS ONCE.** `MainWindow` is the same magnitude of work (7,521 lines) but is split across seven partial files AND thirteen extracted collaborators that own their own state (`MainMediaController`, `FilmstripPrewarm`, `OutputSizeEstimator`, `ProjectRecoveryService`, `EditorTimelineCache`, `KeyboardShortcutService`, `LatestEstimateWorker`, `MemePreviewDirector`, `MaskOverlayManager`, `MemePlacementStore`, `OutputFolderResolver`, `PreviewDetachController`, `WindowBoundsHelper`). This task applies the project's own established pattern to the window that never received it.
- Target Pattern: **extraction of collaborators that OWN their state**, not a file split. Splitting an 8,122-line `partial class` into four 2,000-line partial files changes nothing: all four fragments still share every private field, so the coupling, the race surface and the reasoning cost are identical. Partial files are a navigation aid and nothing more.

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block. **This is high-regression-risk work on the app's most intricate screen.**
- Explicitly Guarded Behavior:
  - `ZOOMLIVE` continuous-gesture zoom model — the live production invariant. Do NOT reintroduce anything resembling the legacy checkmark-based zoom model that `SPEC_GOVERNANCE.md` §5.2 says was purged.
  - Sub-pixel pointer-capture release behaviour, and every place a redraw is suppressed while a gesture holds capture — a rebuild during a drag destroys the control holding capture (the same hazard `THUMB_02` documents in MainWindow).
  - `GRANULARPERF_01` atomic editor-node update under the shared recovery save gate.
  - Undo/redo history semantics: what constitutes ONE undoable step is a product decision encoded across these 207 references, not a mechanical grouping. Changing step granularity is a user-visible regression even when nothing crashes.
  - Speed-segment maths must keep bit-for-bit parity with `docs/01_TIMELINE_COORDINATE_MATH.md`; `OutputTimeline`/`CoordinateMath` already own the formulas and have 148 tests behind them — extracted collaborators CALL them, they do not re-derive them.
  - WASAPI 3-second preview clock abort and render-thread OpenGL deadlock mitigations, wherever they are touched from here.
- Public Interface Parity: the window's constructor signature, its `Result`/return payload, and every member `MainWindow` calls stay identical.

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. FIRST, pin behaviour: record a scripted interaction (load clip → zoom → add three speed segments → drag one → undo → redo → place meme → save) and its resulting recovery JSON. That JSON is the regression oracle for every step below.
2. Extract in THIS order, lowest coupling first, one per commit, verifying the oracle after each:
   `UndoRedoHistory` → `SegmentDocument` (the speed-segment model + its maths delegation) → `ZoomViewport` (the ZOOMLIVE state) → `DragGestureController` → `FrameLaneRenderer` → `EditorTransportController` (mpv/IpcClient/timer) → `EditorRecoveryCoordinator`.
3. Each extracted type owns its own fields **privately** and exposes intent-named methods plus change events. If an extraction leaves a field behind in the window "just for now", that extraction is not done.
4. Only AFTER collaborators exist, split the residue into partial files for navigation. Not before — a split first makes the extraction harder to see.
5. Break up `WireUpControls` (522 lines) as part of step 2: each collaborator wires its own controls.
6. Every `+=` event subscription added must have a matching `-=` on window close. There are 59 subscriptions today; each extraction must carry its own unsubscribe.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: zero new warnings.
- Behavioral: the step-1 oracle JSON is byte-identical; a recovery file written by the pre-refactor build still restores correctly in the post-refactor build (forward compatibility of the recovery payload is mandatory).
- Structural: no type above 1,200 lines; no method above 120 lines; the window's own private field count below 60.
- Performance: timeline redraw and zoom gesture frame times no worse than baseline — measure, do not assume.
