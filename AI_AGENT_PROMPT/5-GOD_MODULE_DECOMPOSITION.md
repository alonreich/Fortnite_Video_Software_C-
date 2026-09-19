> **STATUS: SUPERSEDED 2026-09-19.** This file lumped the four window monoliths into one task.
> It is replaced by the per-file, per-measurement plans R2 (Granular editor), R4 (Crop tool),
> R5 (Music wizard), R7 (Voice-over), plus R1/R3 (export pipeline), R6 (progress overlay) and
> R8 (timeline rebuild). Read those instead; this is retained only for its original rationale.

> **STATUS: OPEN.** Ranked last deliberately — highest regression risk, lowest immediate severity.

# TASK SPECIFICATION: 5 - GOD_MODULE_DECOMPOSITION

## 1. TARGET CONTEXT & SCOPE
- File Path: `src/FortniteVideoSoftware.App/GranularSpeedEditorWindow.axaml.cs` (primary), then
  `src/FortniteVideoSoftware.App/CropToolWindow.axaml.cs`,
  `src/FortniteVideoSoftware.App/MusicWizardWindow.axaml.cs`,
  `src/FortniteVideoSoftware.App/VoiceOverWindow.axaml.cs`
- Target Range: whole-file (8121 / 6709 / 5781 / 3791 lines; 381 / 321 / 277 / 218 members; 209 / 197 / 156 / 109 methods)
- Defect Classification: Modernization Bottleneck (single-responsibility violation; shared-mutable-state surface)

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism: each of these four files is ONE `partial class` in ONE file, mixing presentation, gesture state machines, undo history, playback transport, timers, disk persistence and business rules. The measured cluster weights in `GranularSpeedEditorWindow.axaml.cs` are: Zoom 408 references, Drag 260, Meme 144, Canvas 114, Undo 82, IpcClient 77, Timer 56, RedrawTimeline 50, Recovery 45, frameLane 21, Redo 19. Every one of those clusters reaches the same ~381-member private field bag, so any two are one field away from coupling. That is what makes each of Findings 1-4 the shape it was: unsynchronised shared state with no owner, reachable from anywhere in the type.
- **THE PROJECT HAS ALREADY SOLVED THIS ONCE.** `MainWindow` is the same magnitude of work (7521 lines total) but is split across **seven** partial files (`MainWindow.axaml.cs` 3323, `.Canvas.cs` 1581, `.Wireup.cs` 1168, `.Export.cs` 533, `.Restore.cs` 470, `.Shortcuts.cs` 379, `.SizeEstimate.cs` 67) AND thirteen extracted collaborators with their own state (`Services/MainMediaController.cs`, `FilmstripPrewarm.cs`, `OutputSizeEstimator.cs`, `ProjectRecoveryService.cs`, `EditorTimelineCache.cs`, `KeyboardShortcutService.cs`, `LatestEstimateWorker.cs`, `Infrastructure/MemePreviewDirector.cs`, `MaskOverlayManager.cs`, `MemePlacementStore.cs`, `OutputFolderResolver.cs`, plus `PreviewDetachController.cs` and `WindowBoundsHelper.cs`). This task is not inventing a pattern; it is applying the project's own established one to the four windows that never received it.
- Target Pattern: **extraction of collaborators that own their state**, not a file split. Splitting a 8121-line `partial class` into four 2000-line `partial class` files changes nothing architecturally — all four fragments still share every private field, so the coupling, the race surface and the reasoning cost are identical. Partial files are a navigation aid and should be treated as exactly that.

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block. **This is the single highest-regression-risk task in the backlog and the reason it is ranked last rather than first.**
- Explicitly Guarded Behavior:
  - **`docs/INDEX.md` §1 is a routing table keyed by FILE NAME.** `GranularSpeedEditorWindow.axaml.cs` is marked `⚠ CO-GOVERNED` by specs `01`, `04` and `05`; `CropToolWindow`, `MusicWizardWindow` (`⚠ 01 02`) and `VoiceOverWindow` (`⚠ 01 02`) likewise. Every new file this task creates MUST be added to that table with its governing specs in the SAME commit, and every spec's Code Mini-Map that names a moved symbol must be updated. A split that leaves INDEX.md stale silently breaks the project's own compliance mechanism (`SPEC_GOVERNANCE.md` §2).
  - **Behavioural invariants are encoded in comments, not tests.** `MaxUndoDepth = 40` (04 §6 UI-GRANULAR), `SEAM_01`, `SEEKSTORM_01`, `ZOOMLIVE_07`, `ZOOMCARD_01`, `ZOOMSTYLE_02`, `ZOOMPREVIEW_01`, `ZOOMCOMMIT_01`, `GRANPROBE_01`, `LAYOUTLOOP_02`, `TL-FREEZE`, `TL-ENDSTOP`. Each tag MUST move WITH the code it annotates, not be summarised or dropped.
  - **Thread affinity.** These windows resume `await`s on the Avalonia dispatcher and rely on it. An extracted collaborator must not silently change which thread mutates control state; assert with `Dispatcher.UIThread.CheckAccess()` at every new boundary.
  - **Teardown ordering.** `OnClosing` in each window already performs a specific, load-bearing sequence (cancel → bounded wait → save bounds → hide → dispose media). Extracted collaborators must be disposed at the same point in that sequence, not at construction-scope end.
  - **Event unsubscribe symmetry.** `MpvIpcClient.GlobalMasterVolumeChanged`, `MemeDirectory.Changed` and `RuntimeLog.LogAppended` are STATIC events with matched `+=`/`-=` pairs. Moving a handler into a collaborator must move BOTH halves and keep the handler identity stable, or the window leaks for the life of the process.
- Public Interface Parity: each window's constructor signature, its `Result`-style output property, and its `ShowDialog` contract are consumed by `MainWindow.Wireup.cs` and `VideoMergerWindow`. None may change.

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
Do this as an ordered sequence of INDEPENDENT commits, each shippable and each verified before the next begins. Do NOT attempt a big-bang split.

1. **Commit 0 — navigation only, zero behaviour change.** Split `GranularSpeedEditorWindow.axaml.cs` into partial files along the existing `#region`/comment-banner boundaries, mirroring MainWindow's naming: `.Zoom.cs`, `.Drag.cs`, `.Canvas.cs`, `.Meme.cs`, `.Transport.cs`, `.Wireup.cs`. Move code ONLY — no signature changes, no field moves, no logic edits. Update `docs/INDEX.md` §1 in the same commit. Verify with a diff that the union of the new files is a pure permutation of the old file's lines.
2. **Commit 1 — extract the undo history.** This is the safest real extraction: a pure data structure with no UI coupling and a documented cap. Create `src/FortniteVideoSoftware.App/Services/GranularUndoStack.cs` owning the history list, the `MaxUndoDepth = 40` ceiling (carry the `04 §6 UI-GRANULAR` tag verbatim) and the gesture coalescing (`PushUndo` / `BeginUndoGesture` / `EndUndoGesture`). The window keeps one field of the new type.
3. **Commit 2 — extract the zoom controller.** The largest cluster (408 references) and the one with the most invariant tags. Create `GranularZoomController` owning live-zoom crop state, the ramp constants (`ZoomRampSeconds = 0.5`, `ZoomRampRequiredGap = 1.0`), and the preview/commit transition. Carry `ZOOMLIVE_07`, `ZOOMCARD_01`, `ZOOMSTYLE_02`, `ZOOMPREVIEW_01`, `ZOOMCOMMIT_01` with their code. Expose the state the renderer needs as a read-only snapshot struct, not as mutable fields.
4. **Commit 3 — extract the drag/gesture state machine.** 260 references, and the cluster most likely to hold latent TOCTOU: `SegDragMode`, marker hit-testing (`THUMB_01`, `THUMB_02`, `TL-HITBOX`), `HitTestGrabZone`, pointer capture. Create `SegmentDragController`. Its state must become private to that type — this is the commit that actually shrinks the shared-field bag.
5. **Commit 4 — reuse, do not re-implement.** The meme cluster (144 references) overlaps `Infrastructure/MemePreviewDirector.cs` and `Infrastructure/MemePlacementStore.cs`, which already exist and already serve MainWindow. Route the Granular editor through those rather than extracting a fifth meme owner. Likewise route transport (77 `IpcClient` references) through the existing `Services/MainMediaController.cs` shape.
6. **Repeat 0-4 for `CropToolWindow`, then `MusicWizardWindow`, then `VoiceOverWindow`**, in that order (descending size). Stop after each commit and ship.
7. Ensure deterministic error propagation and structured logging without silent swallows: every extracted collaborator logs through `RuntimeLog` with its own step tag, and none of them introduces a bare `catch { }`.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: Zero compiler/linter warnings or type errors after EVERY commit, not only at the end.
- Behavioral Validation:
  - Commit 0 is verified mechanically: `sort` the old file's non-blank lines and the concatenation of the new partials' non-blank lines; the two must be identical. Any difference is a behaviour change smuggled into a move commit.
  - `tests/FortniteVideoSoftware.Core.Tests` and `tests/FortniteVideoSoftware.App.Tests` pass after each commit.
  - Manual gate per commit, because these paths have no automated UI coverage: open the Granular editor, place two zoom blocks with a gap below `ZoomRampRequiredGap`, drag a segment seam, undo 45 times (assert it stops at 40), redo to the top, add a meme, scrub to the end and press play (the `TL-ENDSTOP` / `MPVEOF_01` interaction), then close the window mid-preview.
  - `docs/INDEX.md` §1 lists every new file with its governing specs, and no spec's Code Mini-Map names a symbol at a path that no longer exists.
- Performance Check: Verified elimination of lock contention, allocation spikes, or thread blocks — specifically, no extraction may add a dispatcher round trip to a per-frame path (`RedrawTimeline`, `RelayoutFrameLane`, pointer-move handlers). Measure before and after with the existing `TRANSPORT_TRACE_01` dev instrumentation.
