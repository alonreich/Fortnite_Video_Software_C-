> **STATUS: OPEN.** Refactoring plan. Nothing here has been applied.

# TASK SPECIFICATION: R8 - TIMELINE_REBUILD_GOD_METHOD

## 1. TARGET CONTEXT & SCOPE
- File Path: `src/FortniteVideoSoftware.App/MainWindow.Canvas.cs`
- Target Range: Lines 320-1030 — `UpdateTimelineMarkers`, a **single 711-line method**, called from **31 sites** across five partial files.
- Defect Classification: Render/State Thrashing (full visual-tree teardown per call; event handlers re-attached inside a redraw)

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism:
  1. **It rebuilds the entire timeline visual tree from scratch.** `canvas.Children.Clear()`, `bottomCanvas?.Children.Clear()`, `scaleCanvas?.Children.Clear()`, then it re-allocates every rectangle, marker, label, cut glyph, meme block, music block and scale tick, and **re-subscribes 32 event handlers** (`PointerPressed`/`PointerMoved`/`PointerReleased` closures capturing the current `canvasWidth` and `duration`). Avalonia must tear down and re-realise every container on each call.
  2. **It defers itself.** The whole body runs inside `Dispatcher.UIThread.Post(...)` even though nearly every one of the 31 call sites is already on the UI thread. Two consequences: callers that do `UpdateTimelineMarkers(); UpdateDraggingVisuals(...)` operate on controls that have not been rebuilt yet, and **nothing coalesces** — N calls in one frame queue N full rebuilds.
  3. **The guard against rebuilding mid-gesture is manual and has already been missed once.** `MainWindow.Wireup.cs:262-267` maintains a hand-listed `markerDragActive` boolean, and its own comment records that the thumbnail marker was MISSING from that list — a rebuild during a gesture destroys the control holding pointer capture. Every new draggable element must be remembered in that list by hand.
  4. Both `MainWindow.Canvas.cs` and `GranularSpeedEditorWindow.RedrawTimeline` (274 lines) draw conceptually the same ruler with separate code.
- Target Pattern: a `TimelineMarkersRenderer` that (a) coalesces redraw requests to one per frame behind a single pending flag, (b) keeps handler subscription OUT of the draw path by attaching once to stable host controls and hit-testing by position, or by reusing pooled marker controls instead of recreating them, and (c) derives "is a gesture live" from a single `GestureState` object rather than a hand-maintained boolean list.

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior:
  - `THUMB_02` — the rebuild MUST stay suppressed while any marker drag holds pointer capture. Whatever replaces `markerDragActive` must be impossible to forget to update; that is the point of the change.
  - `CUT_01` — cuts are drawn as FIXED-WIDTH `CutMarkerWidth = 9.0` glyphs, never as zero-width blocks, and this canvas is a SOURCE-time ruler on which the deleted span IS shaded. Both halves of that decision are deliberate and documented in-method.
  - `TONE_01` — cut fill and edge colours derive from the `AppDangerColor` theme token so darkening the token darkens the markers. Do not inline colours.
  - `PlayheadBadge` is a XAML-declared control re-added after `Children.Clear()`; it must not be recreated.
  - The early returns `if (duration <= 0) return;` and `if (canvasWidth <= 0) return;` are load-bearing — the method runs before layout and before a clip is loaded.
  - `docs/INDEX.md` binds this file to `01_TIMELINE_COORDINATE_MATH.md`; all position maths must keep delegating to `CoordinateMath`/`CanvasMath`, never re-derive.
- Public Interface Parity: `UpdateTimelineMarkers()` keeps its exact signature — 31 call sites must not need editing.

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. FIRST, add coalescing with no other change: a `_timelineRedrawPending` flag so repeated calls in one frame collapse to one `Post`. Measure before/after. This alone is most of the win and carries almost no risk.
2. Replace the hand-listed `markerDragActive` with a `GestureState` object that every drag controller sets and clears, so a new draggable cannot be forgotten.
3. Split the 711-line body into per-band draw methods (`DrawTrimRegion`, `DrawCuts`, `DrawMemes`, `DrawMusicBlock`, `DrawScale`, `DrawThumbnailMarker`) — mechanical, one commit each.
4. Only THEN move handler subscription out of the draw path (pooled/reused marker controls, or host-level hit-testing). This is the highest-risk step; it is last on purpose.
5. Consider sharing the ruler drawing with `GranularSpeedEditorWindow.RedrawTimeline` ONLY after R2 has extracted that window's renderer — not before.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: zero new warnings.
- Behavioral: every marker still draws in the same place for the same project; dragging any marker (trim start, trim end, thumbnail, music start/end/block) never loses pointer capture; a rebuild triggered mid-drag is still suppressed.
- Structural: no method above 120 lines in this file; handler subscriptions in the draw path reduced to zero.
- Performance: measure redraw cost per call and allocation count per call before and after; N calls in one frame must produce exactly one rebuild.
