# SPECIFICATION 01: TIMELINE & COORDINATE MATH

## Code Mini-Map: Bound Source Files & Symbols

> **⚠ CO-GOVERNED rows are bound by EVERY spec listed on them.** Reading only this one is not compliance (`SPEC_GOVERNANCE.md` §2).
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `src/FreeVideoStudio.Core/Media/CoordinateMath.cs` | `CoordinateConstants`, `CoordinateMath`, `Frac` | `CoordinateConstants.PortraitW`, `PortraitH`, `InternalW`, `InternalH`, `BackendScale`; `Frac.ScaleRound`, `SnapZoomWindow` | Canvas size constants (`CoordinateConstants`) and the exact-fraction transforms between 16:9 landscape source and 9:16 portrait export. |
| `src/FreeVideoStudio.Core/Media/OutputTimeline.cs` | `OutputTimeline`, `Chunk`, `Cut`, `Insertion` | `SourceToOutput`, `OutputToSourceRelative`, `SnapInsertionPoint`, `NormalizeCuts`, `InsertionAt`, `Create` | Authoritative mathematical model for single-clip linear output durations and frame-to-output conversions. |
| `src/FreeVideoStudio.Core/Media/MemePlacement.cs` | `MemePlacement`, `MemePresentationMode`, `MemeOverlayCorner`, `MemeOverlaySize`, `MemeOverlayLayout` | `ToInsertions`, `InlineOnly`, `CornerOnly`, `OutputDurationSec`, `VisibleInterval`, `Place`, `ScaleFilter` | MEMEMODE_01 — a meme is a full-screen insertion or a zero-duration corner overlay; the one overlay geometry. **⚠ CO-GOVERNED BY: 03**|
| `src/FreeVideoStudio.Core/Media/HudAutoDetector.cs` | `HudAutoDetector`, `OnlineFrameAccumulator` | `DetectHudRegions`, `ScanFrame`, `SampleFramesAsync` | Automated HUD element detection and coordinate bounds discovery via constant-memory online sequential streaming (≤ 25 MB footprint). |
| `src/FreeVideoStudio.Core/Media/HudConfig.cs` | `HudConfig` | `Sanitize`, `HudKeys`, `GetContentCrop` | HUD configuration schema validation, profile definitions, and content-space coordinates. |
| `src/FreeVideoStudio.Core/Media/HudImageOps.cs` | `HudImageOps` | `Crop`, `Threshold`, `MatchTemplate`, `ComputeWelfordVariance` | Low-level pixel buffer extraction, template matching, and online streaming variance accumulation. |
| `src/FreeVideoStudio.Core/Media/AiTrajectorySmoother.cs` | `AiTrajectorySmoother`, `AiTrackingKeyframe`, `SmoothedTrajectory` | `SmoothTrajectory`, `ToJson`, `FromJson` | Universal AI tracking trajectory interpolation, velocity-adaptive scale breathing, deadband jitter filtering, and boundary anchoring. **⚠ CO-GOVERNED BY: 03**|
| `src/FreeVideoStudio.App/Services/GeminiTrackingService.cs` | `GeminiTrackingService`, `DiscoveredSubject`, `RawSubjectWaypoint` | `ExtractThreeCandidateFramesAsync`, `DiscoverSubjectsAsync`, `DiscoverSubjectsWithLookaheadAsync`, `TrackSubjectAsync`, `IsMainPlayerCharacter`, `IsInanimateScenery`, `CropSubjectJpeg`, `SanitizeWaypointsAgainstLocalPlayer`, `PostWithRetryAsync` | Multi-angle candidate extraction, adaptive forward lookahead probing, multimodal visual reference crop anchoring, defensive local player waypoint sanitization, Gemini Vision API dispatch, transient retry loop, inanimate scenery purge, and Fortnite 3rd-person avatar exclusion. **⚠ CO-GOVERNED BY: 03, 04, 05**|
| `src/FreeVideoStudio.App/GranularSpeedEditorWindow.AiZoom.cs` | `GranularSpeedEditorWindow` (Partial) | `OnAiSmartZoomClickedAsync`, `ExecuteAiTrackingAsync`, `CancelAiTracking` | Granular AI tracking dispatch, multi-angle wizard invocation, status overlay, and instant loop preview verification. **⚠ CO-GOVERNED BY: 04**|
| `src/FreeVideoStudio.Core/Media/CanvasMath.cs` | `CanvasMath` | `FinalWidth`, `FinalHeight`, `ContentWidth`, `ContentHeight`, `BackendWidth`, `BackendHeight`, `ProtectCropDrift` | Canvas sizes (re-exported from `CoordinateConstants`) and HUD crop-drift rounding. |
| `src/FreeVideoStudio.App/MainWindow.axaml.cs` | `MainWindow` | `SourceMsToOutputSeconds`, `UpdateTimelineMarkers`, `UpdateThumbnailButtonState`, `TogglePlayPauseTransport`, `MainTimelineEndSeconds`, `IsMainPreviewAtEnd`, `_lastFreezeTriggerMs`, `_mainEndParkIssued` | Master timeline UI coordination, playhead tracking, transport, freeze trigger and double-counting avoidance. **⚠ CO-GOVERNED BY: 02, 04, GOV**|
| `src/FreeVideoStudio.App/MainWindow.Canvas.cs` | `MainWindow` (Partial) | `AttachThumbnailCameraMarkerInteractions`, `EndThumbnailMarkerDrag`, `SeekMainPreviewToMarkerMs`, `TimelineCameraStickName` | Primary timeline canvas drawing, hitbox evaluation, marker drag lifecycle, and caret positioning. |
| `src/FreeVideoStudio.App/MainWindow.Wireup.cs` | `MainWindow` (Partial) | `WireComponents`, `markerDragActive`, `MainWindow` | Control wiring for the main timeline: transport, marker drags and the thumbnail button's click handler (THUMB_01). |
| `src/FreeVideoStudio.App/MainWindow.Controls.cs` | `MainWindow` (Partial) | `SetThumbnailButtonCtl`, `PlayPauseButtonCtl` | Cached control accessors (MVVM_03) for the thumbnail and transport buttons. **⚠ CO-GOVERNED BY: 02, 04, GOV**|
| `src/FreeVideoStudio.App/MainWindow.Shortcuts.cs` | `MainWindow` (Partial) | `ExecuteMarkStart`, `ExecuteMarkEnd`, `MainWindow` | Keyboard transport and marker shortcuts. Must share the transport with the on-screen controls. |
| `src/FreeVideoStudio.App/GranularSpeedEditorWindow.axaml.cs` | `GranularSpeedEditorWindow`, `SegDragMode` | `ZoomCanvas_PointerMoved`, `ZoomCanvas_PointerReleased`, `RelayoutFrameLane`, `SelectSegment`, `ClampZoomInsideItsBlock`, `EndUndoGesture` | Granular speed timeline editing, rubberband sweeps, freeze & zoom manipulation. **⚠ CO-GOVERNED BY: 04, 05**|
| `src/FreeVideoStudio.App/Controls/PhoneFrameMockup.axaml.cs` | `PhoneFrameMockup` | `PhoneFrameMockup` | High-fidelity 9:16 phone mockup preview with 600px semi-transparent flanks. **⚠ CO-GOVERNED BY: 04**|
| `src/FreeVideoStudio.App/Controls/TimelineKnob.cs` | `TimelineKnob` | `Attach` | High-precision timeline knob physics and collision boundaries. |
| `src/FreeVideoStudio.App/Controls/KineticScrubController.cs` | `KineticScrubController` | `OnTick`, `Release`, `Cancel`, `IsFlinging`, `SeekRequested`, `FlingSettled` | Kinetic inertia scrubbing across timeline and preview views. |
| `src/FreeVideoStudio.App/Controls/TimelineLanesControl.axaml.cs` | `TimelineLanesControl` | `MaxZoomFactor`, `Refresh`, `FormatClock`, `MarkerHeadroomPx` | Multi-lane timeline rendering for speed, memes, and cuts. |
| `src/FreeVideoStudio.App/VoiceOverWindow.axaml.cs` | `VoiceOverWindow` | `_timeline`, `ApplyMasterVolume`, `Dispose`, `IsDuckAudio` | Audio take timeline alignment excluding memes to prevent double-counting. **⚠ CO-GOVERNED BY: 02**|
| `src/FreeVideoStudio.App/MusicWizardWindow.axaml.cs` | `MusicWizardWindow` | `OutputTimeline`, `TimelineStartSeconds`, `Name`, `FilePath` | Phase 3 music timeline synchronization and end-fit calculations. **⚠ CO-GOVERNED BY: 02**|
| `src/FreeVideoStudio.App/ViewModels/TimelineViewModel.cs` | `TimelineViewModel` | `SourceMsToOutputSeconds`, `PreviewSourceToOutputSeconds` | Main App source-ms to output-seconds mapping for export and live preview; meme-blind by design (§2 Double-Counting Guard). |
| `src/FreeVideoStudio.Core/Media/MergeEdl.cs` | `MergeEdl`, `EdlClip`, `EdlEffects`, `EdlAnchor`, `EdlZoom`, `EdlMeme` | `FromJson`, `ToJson`, `MergeEdlJsonContext` | The Video Merger's edit list in source µs (§10 MERGEEDL_01, EDLNULL_01). **⚠ CO-GOVERNED BY: 06**|
| `src/FreeVideoStudio.Core/Media/CompositeTimeline.cs` | `CompositeTimeline`, `CompositeClip` | `Build`, `KeepWindow`, `IntroCutUs`, `UsToFrames`, `FramesToUs`, `MergedSecToBodyOutputSec`, `SyntheticIntroSec` | Three clocks, one mapper: source µs, merged frames, output seconds (§10 COMPOSITE_01). **⚠ CO-GOVERNED BY: 03**|
| `src/FreeVideoStudio.Core/Media/MergedTimeline.cs` | `MergedTimeline`, `MergedClip`, `MergeClipSource` | `Build`, `Composite`, `ToMerged`, `MergedLengthSec`, `MergedEndSec` | Seconds adapter over `CompositeTimeline` (§10 P2.4). **⚠ CO-GOVERNED BY: 03**|
| `src/FreeVideoStudio.Core/Media/MergeEditorBridge.cs` | `MergeEditorSource`, `MergeEditorClip`, `MergeEditorState` | `Build`, `MpvUrl`, `ToEditor`, `FromEditor`, `ExtraFreezes`, `PlacementAt` | One granular editor over the whole merge (§10 MERGEEDIT_01). **⚠ CO-GOVERNED BY: 03**|
| `src/FreeVideoStudio.Core/Media/MergerPreviewPlan.cs` | `MergerPreviewPlan`, `PreviewStep`, `PreviewStepKind` | `Build`, `SpeedAt`, `OutputSecAt`, `MemePlacements` | Merger preview parity with the export (§10 MERGEPREVIEW_01). **⚠ CO-GOVERNED BY: 03**|
| `src/FreeVideoStudio.Core/Media/MusicPadAlignment.cs` | `MusicPadAlignment` | `Align` | Shifts music tracks by the fade-in pad so the export matches the preview (MUSICPAD_01). **⚠ CO-GOVERNED BY: 02, 03**|
| `src/FreeVideoStudio.App/VideoMergerWindow.EdlPreview.cs` | `VideoMergerWindow` (Partial) | `EnsureEdlLoaded`, `LoadEdlAsync`, `EnsurePreviewPlan`, `TickPreviewEffects`, `TickMergerMemes`, `PreviewOutputSec` | The Merger preview as ONE EDL (§10 MERGEPREVIEW_EDL_01). **⚠ CO-GOVERNED BY: 03**|
| `src/FreeVideoStudio.App/VideoMergerWindow.Timeline.cs` | `VideoMergerWindow` (Partial) | `SeekMerged`, `TickMergedPlayback`, `DrawMergedTimeline`, `PreviewDurationSec`, `PreviewPositionSec` | Merged-clock playback, seeking and timeline drawing. **⚠ CO-GOVERNED BY: 03**|
| `src/FreeVideoStudio.App/GranularSpeedEditorWindow.Merge.cs` | `GranularSpeedEditorWindow` (Partial) | `InitializeMergeMode`, `DrawMergeDividers`, `BuildMergeFrameLaneAsync` | The granular editor in merge mode: clip dividers and the merged frame lane. **⚠ CO-GOVERNED BY: 04**|

---

## 1. 16:9 to 9:16 Portrait Canvas Trick & FOV Geometry  {#TL-PORTRAIT}
* **Source Resolution Mandate:** Native source footage must be 16:9 landscape (1920 x 1080, 2560 x 1440, or 3840 x 2160).
* **Surviving Center Slice:** The active gameplay slice is strictly 720 source pixels wide:
  $$\text{SurvivingSourceWidth} = \frac{1280 \text{ (internal portrait width)}}{1.7777\dots \text{ (16:9 aspect ratio)}} = 720 \text{ source px}$$
  `CoordinateMath.cs` is the absolute mathematical authority. The dimmer must never be locked to 1280 source pixels.
* **Canvas Voids & Airspace Priority:** Scaling to 1280px width inside a 1080p width constraint alters the aspect ratio, creating exactly 150px black voids at the top and bottom of the final 1080 x 1920 canvas:
  $$\text{PadTop} = 150\text{px}, \quad \text{PadBottom} = 150\text{px}, \quad \text{ContentHeight} = 1620\text{px}$$
  * The top 150px void is reserved for the mobile status bar (clock at 9:41, battery, 5G, Wi-Fi arches) and SkiaSharp title text overlay.
  * The bottom 150px void houses mobile navigation controls.
  * Neither void element may ever overlap the centered 720px gameplay area.
* **Dimmer Mask:** `PhoneFrameMockup.axaml` dims inactive flanks via two 600px semi-transparent (`#99000000`) blocks on a 1920px canvas, leaving the 720px center clear:
  $$\text{FlankWidth} = \frac{1920 - 720}{2} = 600\text{px}$$
  Side dimming dynamically stretches on window resize to prevent distortion of the phone frame.
* **HUD Overlay Math Parity:** Live preview and FFmpeg export derive HUD dimensions from an identical basis:
  $$\text{ItemSize} = \text{contentH} \times \text{scale} \times \text{backendScale}$$
  Quantized via `CropToolWindow.QuantizeItemSize` (a private method of the Crop Tool; preview) and `MobileFilterBuilder.Build` (export). Heavy SkiaSharp image decodes and crop operations run off the UI thread.
  `RATIOLOCK_01`: backend HUD sizes use even pixels with a 2px minimum. Width follows the requested scale; height follows the source aspect ratio within 1 backend pixel. Preview rounds the backend dimensions to the nearest content pixel. Overlay positions are clamped and rounded to individual content pixels, never forced onto a 27px grid.
* **No Mask Profile:** Read-only profile exporting 1080 x 1920 with zero HUD overlays. Injects explicit zero-size rectangles for all 5 HUD keys to prevent default restoration by the sanitizer. In-game overlay controls are hidden and forced off. Portrait source footage is cover-scaled and center-cropped to the 2:3 content area.

---

## 2. Authoritative OutputTimeline Model  {#TL-OUTPUTTIMELINE}
`src/FreeVideoStudio.Core/Media/OutputTimeline.cs` is the mathematical authority mapping source time to finished output time for single-clip linear editing and per-clip segment evaluations across preview playback and FFmpeg rendering. For multi-clip NLE virtual timelines, `CompositeTimeline.cs` and `MergeEdl.cs` govern the composite multi-clip mapping across `edl://` libmpv preview and FFmpeg concat filtergraphs (see §10 `TL-COMPOSITE`).

### Chunk Classification
$$\text{TotalOutputSeconds} = \sum_{c \in \text{Chunks}} \text{Duration}(c)$$

1. **`Normal`:** Consumes source time; consumes output time scaled by speed factor S:
   $$\Delta t_{\text{out}} = \frac{t_{\text{end}} - t_{\text{start}}}{S}$$
2. **`Freeze`:** Consumes 0 source time; occupies output time equal to hold duration D_freeze (0.2s <= D_freeze <= 10.0s). Synthesizes a `Speed = 0` segment on export.
3. **`Cut` (Delete Parts):** Consumes source time; occupies 0 output time:
   $$\Delta t_{\text{out}} = 0$$
4. **`Insertion` (Meme Cutaway):** Consumes 0 source time; occupies output time equal to meme duration:
   $$\Delta t_{\text{out}} = D_{\text{meme}}$$
   Only FULL SCREEN memes are insertions (MEMEMODE_01). A CORNER OVERLAY meme is never an `Insertion`:
   $$\Delta t_{\text{out}}(\text{corner}) = 0$$
   `MemePlacement.ToInsertions` drops it, so `OutputTimeline`, `ProjectDocument.BuildTimeline`, `CompositeTimeline`, the editors and every size estimate agree by construction.

### Freeze Insertion Invariant
A freeze is an insertion, never a replacement. Freezing at t_anchor for duration D holds the exact video frame at t_anchor for D seconds, and resumes playback from t_anchor:
$$\text{TotalOutputSeconds}' = \text{TotalOutputSeconds} + D_{\text{freeze}}$$

### Caret Crossing Holds
Output-to-source mapping inside a hold is many-to-one:
$$\forall t_{\text{out}} \in [t_{\text{freeze\_start}}, t_{\text{freeze\_end}}]: \quad \text{OutputToSourceRelative}(t_{\text{out}}) = t_{\text{anchor}}$$
Playhead position is supplied directly and advances continuously in real time across the frozen span. Clicking inside a frozen span centers the playhead at the clicked coordinate, not the edge.

### Double-Counting Guard
Memes are anchored to clip-relative source seconds (`AtSourceSecRelative`). The Main App's mappings `TimelineViewModel.SourceMsToOutputSeconds` (export) and `TimelineViewModel.PreviewSourceToOutputSeconds` (live preview) — `MainWindow.SourceMsToOutputSeconds` only delegates to the former — and the Voice Over studio's `VoiceOverWindow._timeline` deliberately omit memes from their `OutputTimeline` instances because independent offsets are applied during export (`MemeTimeInsertedBefore`). Memes must never be passed into `OutputTimeline` at these call sites.

---

## 3. Marker & Speed Segment Physics  {#TL-MARKERS}
* **Marker Collision:**
  $$0 \le t_{\text{start}} \le t_{\text{end}} - \Delta t_{\text{min}} \quad (\Delta t_{\text{min}} = 1\text{ ms or } 1\text{ frame})$$
  Markers act as impenetrable hard limits.
* **Event Dispatching:** Dragging markers updates state on `PointerReleased` only. Pointer drag continuously drives throttled seeks without modifying play/pause state. Button presses while paused seek to mark; button presses while playing mark without pausing. Clicking a Granular Speed Segment selects it without moving the playhead.
* **Speed Segment Barrier:** Neighboring speed blocks may touch but never overlap (`SegGapMs = 0`):
  $$t_{\text{start}, i+1} - t_{\text{end}, i} \ge 0\text{ms}$$
  **SEAM_01:** two edges within `SeamEpsilonMs = 1` ms are one seam. A grab on a seam takes the edge on the pointer's side (left of it = the earlier block's END, right of it = the later block's START), and the canvas hit test and the edge sticks apply the same rule. Segments slide freely across the Freeze Camera marker. Minimum block duration is 200ms.
* **Proportional Thirds Hitbox:** A block's grab zone for each edge is:
  $$\text{GrabZone} = \min\left(8\text{px}, \frac{\text{BlockWidth}}{3}\right)$$
  Nearest edge wins when adjacent.
* **Trim-Marker Clones:** Active speed blocks render SeaGreen vertical start/end edge sticks (24px hitbox, 3px stroke). Dragging routes to the identical block-edge resize pipeline, so it obeys the same neighbor barrier (blocks may touch) and 200ms minimum width. Swept rubber-band blocks arrive pre-selected.

---

## 4. Frozen Segment Object  {#TL-FREEZE}
* **Duration Bounds:**
  $$0.2\text{s} \le t_{\text{freeze}} \le 10.0\text{s}$$
  Preset buttons offer fixed lengths: 0.5s, 1.0s, 1.5s, 2.0s, 2.5s, 3.0s.
* **Visual Representation:** Thumbnail lane displays the held frame with a cool blue wash, diagonal hatching, solid boundary posts, and a centered `❄ FROZEN X.Xs` label (dropped if block width < 60px).
* **Interaction:** Double yellow camera popsicles extend to the bottom timeline lane. Dragging popsicles resizes from that edge; dragging body moves anchor time; Arrow keys trim (END focused) or slide (body focused) by 1 frame.
* **Layer Priority:** Frozen band sits on top of speed blocks and intercepts clicks first. Dragging left tracks leading edge; dragging right tracks trailing edge.
* **One Hold Per Pass (FREEZE_01) — NON-NEGOTIABLE:** The main screen's playback tick triggers the hold from a **150 ms window** around the anchor:
  $$\text{Trigger} \iff t_{\text{playhead}} \in [t_{\text{anchor}},\; t_{\text{anchor}} + 150\text{ms}]$$
  The hold PAUSES the player, so the clock does not advance while it runs, and on release playback resumes for exactly one tick — **one frame, 16–33 ms** — before the test runs again. Without a record of having already fired, that lands back inside the same 150 ms window and re-freezes. The result is a player that advances a single frame and pauses itself, five to nine times over, with no way out.
  `MainWindow._lastFreezeTriggerMs` records WHICH anchor has been consumed (the anchor time, not a bool), so moving the freeze re-arms it for free. Re-arming is otherwise **deliberate only**:
  * the playhead leaves the window with real clearance (`< t_anchor - 250ms` or `> t_anchor + 400ms`), so a frame of jitter cannot re-arm it; or
  * an explicit seek (`SeekInternal`) — a scrub is a new pass over the timeline.

  ⚠️ Resuming playback is NOT a re-arm. That is the loop. `VoiceOverWindow` has carried this guard since VOFIX_01; the main screen simply never had it.

---

## 5. DELETE PARTS (Cut Normalization)  {#TL-CUTS}
* **Merge & Discard Rules:**
  * Adjacent cuts closer than 0.30s merge into one:
    $$t_{\text{start}, i+1} - t_{\text{end}, i} < 0.30\text{s} \implies \text{Merge}$$
  * Overlapping cuts merge.
  * Cuts < 0.04s are discarded.
* **Safety Floor:** At least 0.5s of clip footage must survive:
  $$\text{SurvivingSourceSeconds} \ge 0.5\text{s}$$
* **Segment Reconciliation:** Cuts clear fully contained speed blocks, trim partial overlaps, drop remnants < 200ms, and clear freezes whose anchor was cut. Spanning blocks are preserved.
* **Playback Skip:** Playback loops across Main, Speed Editor, VoiceOver, and Music Wizard skip cut spans in a single seek on tick.
* **Audio Crossfade:** FFmpeg export injects an 8ms audio edge crossfade at each cut boundary to eliminate jump-cut pops.

---

## 6. Live Zoom-In Geometry & Workflow  {#TL-ZOOM}
* **Data Model:** `SpeedSegment` carries `ZoomX, ZoomY, ZoomW, ZoomH, ZoomOrigRes, ZoomSlow, ZoomStartMs, ZoomEndMs`.
* **Bounds & Clamping:** Floor constraint of at most 8x upscale (`MaxZoomUpscale = 8`): the box is never narrower than 1/8 of the width the viewer finally sees, in source pixels — the full source width in landscape, the surviving slice (`InternalW / scale`, 720px on 1080p) in portrait. A smaller box is snapped up on commit. Clamped to 16:9 (landscape) or 2:3 (portrait mapping directly to surviving 720px width via 2.0/3.0 ratio):
  $$\text{Aspect} = \begin{cases} 16/9, & \text{Landscape} \\ 2/3, & \text{Portrait} \end{cases}$$
* **Color Token Mandate:** Zoom box, handles, and timeline markers use `AppZoomBrush` exclusively.
* **Interactive Gestures (ZOOMLIVE):** Non-destructive toggle; faint suggestion on click; commits on first drag/resize. Selecting a segment pauses playback on its first frame and mounts the box. Magnifiers own clicks while box is open; block edges stand down. Resizing block pulls zoom span within bounds.
* **Slow Zoom Ramp Constraints:**
  * Interpolates over 0.5s pre-roll and 0.5s post-roll outside `ZoomStartMs`/`ZoomEndMs`.
  * All-or-nothing: Requires 0.5s free buffer; otherwise snaps instantly.
  * Adjacent slow zooms require a 1.0s gap (`ZoomRampRequiredGapBetweenSlowZooms = 1.0s`); if < 1.0s, both drop to Instant.
  * Switching from Instant to Slow refuses if gap < 1.0s.

---

## 7. Meme Placement & Scrubbing Physics  {#TL-MEME}
* **Two Presentation Modes (MEMEMODE_01) — NON-NEGOTIABLE:**
  * `InlineFullScreen` (default, and every meme saved before MEMEMODE_01): interrupts the gameplay; adds `DurationSec` to the output. All rules below apply.
  * `CornerOverlay`: plays OVER the running gameplay in one of four corners (`MemeOverlayCorner`, default BottomRight), Small / Medium / Large (`MemeOverlaySize`, default Medium), with optional sound (`PlaySound`). It adds ZERO output seconds and owns only a VISIBILITY INTERVAL on the GAMEPLAY clock (output seconds with full-screen memes not counted):
    $$[\,t_s,\ t_e\,) = [\,\text{SourceToOutput}_{\text{gameplay}}(t_{\text{anchor}}),\ \min(t_s + D_{\text{meme}},\ T_{\text{gameplay}})\,)$$
    (`MemePlacement.VisibleInterval`; clipped, never lengthening). Music, voice-over, cuts and other memes after it do not move. A full-screen cutaway inside the interval pauses it with the gameplay (the export overlays before splicing).
  * Corner overlays are NOT snapped by D7/D8 (only pushed out of deleted footage), may overlap anything, and in the Merger are always `EdlMemePlacement.Mid` at their exact source µs. Switching a meme FULL SCREEN → CORNER removes its length; CORNER → FULL SCREEN adds it back and re-applies D7/D8 at the anchor.
* **Placement:** Anchored to clip-relative source seconds (`AtSourceSecRelative`). Placed on finished timeline in Speed Editor. `MemePlacement.Id` is a generated alphanumeric identifier, never derived from filename.
* **Snapping:** `SnapInsertionPoint` pushes placement forward past cuts, freezes, or speed blocks. Memes cannot interrupt speed blocks or share timestamps. Cuts delete contained memes.
* **Marker Count Is Ruler-Dependent (MEME_06) — NON-NEGOTIABLE:**
  * **Granular Speed Editor (OUTPUT-time ruler):** a meme occupies D_meme output seconds, so it draws as a BAND with TWO clown popsicles, one at each boundary. Neither head is individually draggable; only the whole block moves, from its middle (a meme's length is the file's own length, so a resize grip would advertise a control that cannot act).
  * **Main App (SOURCE-time ruler):** a meme occupies ZERO source seconds — its start and end are the SAME instant. It MUST render as exactly **ONE display-only clown head**, placed at the gameplay moment it interrupts. Two heads would land on the identical pixel with no band between them, which confuses the user and corrupts marker hit testing. Moving and removing happen only in the Speed Editor.
* **Hitbox:** Rendered band has an invisible 18px minimum click floor (`MemeGrabMinWidthPx = 18`).
* **Scrubbing Physics:** Dragging sets caret to output start:
  $$t_{\text{caret}} = \text{SourceToOutput}(t_{\text{anchor}}) - D_{\text{meme}}$$
  Preview updates to source anchor. Canvas retains pointer capture. Ruler pivot updates live during drag. `BaseTimeline` cache is not cleared on move. Caret stays sticky on release until playback resumes.

---

## 8. End Of Timeline: Stop, Then Restart  {#TL-ENDSTOP}
The last frame is a **stop**, and the next transport press is a **restart**. Every preview surface obeys this identically — the main screen (`MAINEND_01`), the Voice Over studio (`VOEND_01`).

* **Park once, not once per tick.** A playback tick that re-issues `pause=yes` on every pass while the player sits at the end will land on top of a PLAY the user has just requested. The stop is a one-shot, re-armed by any seek or any play.
* **PLAY at the end means MARK START, never "resume".** Unpausing a player with nothing left to play advances one frame and stops again. Parked at the end:
  $$t_{\text{playhead}} \ge t_{\text{end}} - 0.05\text{s}, \quad t_{\text{end}} = \begin{cases} t_{\text{MARK END}}, & \text{trimmed} \\ \text{Duration}, & \text{otherwise} \end{cases}$$
  the next PLAY seeks to MARK START first. RECORD in the Voice Over studio does the same, otherwise the trim-end stop kills the take on the tick after it arms and every take comes out empty.
* **One transport, all controls.** The on-screen button and the keyboard shortcut route through a single method (`MainWindow.TogglePlayPauseTransport`). A second bare `SetPropertyAsync("pause", "no")` on any play path reintroduces the trap on whichever control skipped it.
* **One transport is not enough — it must also be reached ONCE per press.** A toggle wired to both `Command` and `Click` fires twice and cancels itself, which looks exactly like a playback fault and is not one: `04_UI_UX_AVALONIA_SPEC.md` §4 (UI-SAFEGUARDS, DOUBLEFIRE_01). Before suspecting the player, check whether an unconditional transport on the same screen (MARK START) still works. If it does, the fault is the toggle's wiring, not the clock.
* **Clamping is not rewinding.** `NormalizePreviewPlaybackPosition` clamps to just inside the end (`end - 0.05`) — still inside the stop window. Clamping constrains a position; it does not decide where playback should begin. The restart is the caller's job.

---

## 9. Hitbox Priorities & Time Badges  {#TL-HITBOX}
* **Z-Index Hierarchy (Highest to Lowest):**
  1. Music Note Symbols
  2. Yellow Camera Overlays
  3. Slider Playhead Caret
  4. MARK START / MARK END markers
* **Exclusive Focus:** Exactly one object holds focus with crawling marching ants. Released via `Esc`, right-click down, or selecting another object. `Esc` is consumed only if an element holds focus. Right-click release is detected on pointer-down.
* **Audio Marker Precision:** Vertical lines from music note icons drop to exact millisecond coordinates with sub-pixel rendering, bypassing video frame snapping.
* **Dynamic Time Badge:** Attached to caret playhead; 1-pixel movement resets 1.0s timer and restores 100% opacity; fades to 0% after 1.0s idle.
* **Thumbnail Selection:** Defaults to 66% mark:
  $$t_{\text{thumb\_default}} = t_{\text{start}} + \frac{2}{3}(t_{\text{end}} - t_{\text{start}})$$
  Manual override locks position. Button cycles `SET THUMBNAIL` / `MOVE THUMBNAIL HERE` / `REMOVE THUMBNAIL` based on playhead position. `Shift + Left/Right` steps by exact frames via mpv `container-fps`.
* **Marker Drag Lifecycle (THUMB_02):** Every draggable timeline marker must end its drag on **three** events, not one:
  1. `PointerReleased` — the ordinary exit;
  2. `PointerMoved` with the left button no longer held — the button was released somewhere the control never saw;
  3. `PointerCaptureLost` — **the only event guaranteed to arrive** when the captured control is removed from the tree, the window loses focus, or the pointer is stolen.

  `UpdateTimelineMarkers` clears and rebuilds the marker canvas from a **posted** callback, so a rebuild can land between a press and its release and destroy the control holding capture. With only (1), the drag flag stays raised for the rest of the session; the rebuilt marker's move handler then runs on a bare hover and re-seeks and re-pauses the player on every mouse movement.
  A marker being dragged must also appear in `markerDragActive`, which is what suppresses the canvas rebuild for the duration of a gesture. A marker missing from that set is the bug above waiting to happen.

## 10. Video Merger: EDL and Composite Timeline  {#TL-COMPOSITE}
Owner of the Video Merger's time maths (Video-Merger-Migration.md P2). The Main App's clocks (§1–§9) are unchanged.
* **EDLNULL_01 — old files must load safely.** The source-generated JSON reader assigns default(T) to every `init` property a file does not mention (initializers do not run). So (1) every list/object property of the EDL records is null-proof (a null init keeps the empty default; record equality compares non-null fields), and (2) `MergeEdl.FromJson` first completes every object with the missing keys of a freshly constructed default (`ScraperEnabled`, `BaseSpeed`, volumes, a fresh `ClipId`). Found in the field: an autosave written before `Cuts` existed made every capture throw, the Merger's autosave froze on an old queue, and a later remove crashed the app.
* **MERGEEDL_01 — the merge is data.** `MergeEdl` (clips, order, user windows, effects, thumbnail, music, base speed) is the only state. Time unit inside a clip is **source µs** (`long`), anchored as `EdlAnchor(ClipId, SourceUs)`; `ClipId` is a GUID given on queueing. Records holding lists implement structural equality by hand (UNDOEQ_01). JSON is source-generated (`MergeEdlJsonContext`).
* **COMPOSITE_01 — three clocks, one mapper.** `CompositeTimeline.Build(edl)`:
  1. **Source µs** per clip (real frame pts, FRAMESNAP_01; `EdlClip.IntroCutUs`).
  2. **Merged frame** — integer frames at 60 fps, kept content end to end at 1.0x. Each clip's frame count is `round(keptUs × 60 / 10^6)` on its own (what the export's per-clip `fps=60` emits), so any number of clips adds up with zero drift.
  3. **Output sec** — per clip the Main App's `OutputTimeline` (speed segments, freezes as speed-0 segments, memes as insertions, cuts as holes — D18) over a clip length of `frames / 60`, with the global `BaseSpeed` applied per clip to footage OUTSIDE speed segments (D19: segment speeds are absolute, freezes/memes are real-time holds — the Main App editor's rule); clips summed; then `+ SyntheticIntroSec` (0.1 s when a custom thumbnail is set).
* **Intro removal is derived, never stored:** clip 1 only when a custom thumbnail is set; clips 2..N while the scraper is on; refused when < 50 ms would remain. A position inside a removed intro maps to the first kept frame.
* **Meme placement (D4):** `AtStart` → before the clip (its fade-in follows); `Mid` → at `AtUs`; `AtEnd` → where the fade-out begins when the kept window reaches the file end and the tag's `fadeout` is known, else at the clip end.
* **Remap:** through `(ClipId, SourceUs)`, so positions survive scraper toggles, a new thumbnail and reordering; `null` (stale) when the clip was removed.
* **`MergedTimeline` is a seconds adapter (P2.4).** It builds a `MergeEdl` from its `MergeClipSource` list (clip ids derived from the queue index), reads windows, intro removal and integer frame boundaries from `CompositeTimeline` (`Composite` property), and keeps only the continuous in-clip playhead maths. `MergedClip.MergedLengthSec` = frames / 60; `MergedEndSec` uses it; `ToMerged` never leaves the clip's merged slice. Delete the adapter once all merger callers read `Composite`.
* **MERGEEDIT_01 — one granular editor over the whole merge (D16).** `MergeEditorSource.Build(composite)` (in `MergeEditorBridge.cs`) turns the merge into ONE editor source: `MpvUrl` is an inline `edl://` of every clip's kept window (length-prefixed paths `%bytes%path`, start = keep-in, length = `MergedFrames / 60`, so mpv's time-pos IS the merged clock — P4.1 PASS), plus per-clip merged-ms spans and fade lengths. `ToEditor(edl)` maps every clip's effects into merged ms (the v1 editor shows ONE freeze; the others are counted in `ExtraFreezes` and kept). `FromEditor(state, edl)` puts results back: segments/cuts crossing a boundary are SPLIT per clip (identical output), a freeze or meme belongs to the clip under it (a boundary belongs to the clip that starts there), meme placement by position (D17: within the clip's fade-in or first frame = AtStart; within its fade-out or last frame = AtEnd, stored at the fade-out start; else Mid). Zoom stays in the clip's SOURCE px with its size (`EdlZoom.SourceW/H`). `ToEditor` must run before `FromEditor` on the same source.
* **MERGEPREVIEW_EDL_01 — the Merger preview is ONE EDL (P4.2).** Once the queue is analysed, `VideoMergerWindow.EdlPreview.cs` loads `MergeEditorSource.Build(_timeline.Composite).MpvUrl` (mpv time-pos = merged seconds, `hr-seek=yes`). The URL is compared every tick; a new layout reloads it at the same (file, source second). Before analysis the per-file preview is used.
* **MERGEPREVIEW_01 — preview parity (P8.1).** `MergerPreviewPlan.Build(composite)` reads the export's `OutputTimeline.Chunks` of every clip into merged-time steps (Play at absolute speed / Cut / Freeze / Meme). The tick sets mpv `speed` = `SpeedAt` (re-asserted after any EDL change), seeks past cuts, and pauses for each hold once per pass (seek re-arms: holds before the target count as done, one AT the target plays). `OutputSecAt(merged)` = `CompositeTimeline.MergedSecToBodyOutputSec` (T8.1a) and drives the music preview, which advances through holds and never changes speed. Pressing the transport during a hold pauses. Memes (P8.2): `MergerPreviewPlan.MemePlacements` (merged clock, origin 0) drive the Main App's `MemePreviewDirector` with the EDL URL as the gameplay source (MEME_07: park, play the meme file behind the MemeSwapOverlay veil, reload the same EDL at the anchor); the Merger tick returns early while it is active and seeks are ignored; the music stops during the meme and resumes at the output moment after it.

---

## 11. Universal AI Smart Tracking Trajectory & Dynamic Camera Math  {#TL-AITRACKING}
Governs automated camera tracking zooms produced by Gemini Vision API and smoothed via `AiTrajectorySmoother.cs`.
* **Coordinate Invariant (Normalized 0-1000 Grid to Source Canvas Pixels):**
  Gemini Vision yields waypoints in integer coordinates normalized to $[0, 1000]$ where $(0, 0)$ is top-left and $(1000, 1000)$ is bottom-right:
  $$C_{x,\text{px}} = \frac{C_x}{1000.0} \times W_{\text{src}}, \qquad C_{y,\text{px}} = \frac{C_y}{1000.0} \times H_{\text{src}}$$
  $$W_{\text{target,px}} = \frac{W}{1000.0} \times W_{\text{src}}, \qquad H_{\text{target,px}} = \frac{H}{1000.0} \times H_{\text{src}}$$
* **Velocity-Adaptive Dynamic Camera Breathing:**
  The tracking camera breathes dynamically with target velocity rather than holding a rigid zoom multiplier:
  * Low target velocity ($v \le v_{\text{slow}} = 80\,\text{px/s}$): camera maintains tight zoom $S_{\text{base}}$ (default $2.2\times$).
  * High target velocity ($v \ge v_{\text{fast}} = 450\,\text{px/s}$): camera proportionally zooms out towards $S_{\text{min}}$ (default $1.3\times$) to keep action in frame without violent whips or motion-blur disorientation:
    $$S(v) = S_{\text{base}} - \left(\frac{v - v_{\text{slow}}}{v_{\text{fast}} - v_{\text{slow}}}\right) \times (S_{\text{base}} - S_{\text{min}}), \quad \text{clamped to } [S_{\text{min}}, S_{\text{base}}]$$
* **Target Occlusion, Death, or Exit Easing:**
  When a target becomes occluded, dies, or exits the frame (`Visible = false`), the camera does NOT snap or freeze. It initiates a smooth $0.4\,\text{s}$ exponential ease-out back to full-frame ($1.0\times$ scale, $C_x = W_{\text{src}}/2, C_y = H_{\text{src}}/2$).
* **Humanoid Vertical Framing Bias:**
  In third-person and first-person gaming footage, humanoid targets have height dominating width ($H > 1.15 \times W$). To frame the upper torso and head naturally above center rather than dead-centering on the character's pelvis or feet:
  $$C_{y,\text{optical}} = C_y - 0.12 \times H_{\text{target}}$$
* **Deadband Jitter Filter:**
  To eliminate AI bounding-box micro-jitter on stationary or slowly shifting targets, position deltas below $2.0\%$ of source dimensions are suppressed via deadband hysteresis:
  $$\Delta C_x < 0.02 \times W_{\text{src}} \implies \text{keep previous } C_x$$
* **Fortnite Third-Person Local Player Exclusion Invariant (`IsMainPlayerCharacter` & `TrackSubjectAsync`):**
  In Fortnite (and 3rd-person shooters generally), the game camera is locked directly behind the player's own avatar. Tracking the player's own back is redundant and disorienting. Candidates meeting either of these criteria are strictly excluded from detection and tracking:
  1. Semantic match: labels containing `"main player"`, `"local player"`, `"primary character"`, `"self"`, `"protagonist"`, `"user character"`, `"camera character"`, `"own character"`, or `"my player"`.
  2. Spatial 3rd-person avatar anchor: $Y_{\max} \ge 750$, $250 \le C_x \le 650$, and $\text{Height} \ge 300$ (or massive anchor $Y_{\max} \ge 850, 300 \le C_x \le 600, \text{Height} \ge 400$).
  Dense tracking prompts enforce the same exclusion: Gemini is strictly forbidden from switching to or outputting waypoints for the local player's avatar in the foreground.
* **Multimodal Visual Reference Crop Anchoring (`CropSubjectJpeg`):**
  When a target is confirmed in the character picker wizard, its bounding box is cropped from the clearest candidate angle frame using SkiaSharp (with 15% contextual padding margin) and encoded as a JPEG. This image is injected directly into Gemini's multimodal tracking payload alongside the sliced video clip. The prompt anchors Gemini to this exact visual reference, preventing identity hijacking during fast glides, weapon swaps, or scene changes.
* **Defensive Waypoint Sanitizer (`SanitizeWaypointsAgainstLocalPlayer`):**
  To protect against transient AI hallucinations where a dense waypoint locks onto the player's avatar during aerial landing, all waypoints undergo spatial validation. If the confirmed target was external, any waypoint with $Y_{\max} \ge 720, 240 \le C_x \le 660, H \ge 260$ is suppressed (`Visible = false`), activating the 1.0s inertial hold and ease-out fallback to smoothly track past the foreground character without whipping.
* **Inanimate Scenery & Geometry Purge (`IsInanimateScenery`):**
  Static landscape geometry (buildings, towers, statues, houses, roofs, terrain, trees) is strictly purged from discovery options unless an explicit combatant or player qualifier (`"opponent"`, `"enemy"`, `"glider"`, `"player"`, `"combatant"`) is present.
* **Adaptive Forward Lookahead Probing (`DiscoverSubjectsWithLookaheadAsync`):**
  When a user selects a segment where opponents are initially distant, airborne, or not yet in clear view (such as gliding towards an encounter), the initial 3-frame scan may resolve 0 external targets. Rather than aborting, the discovery engine automatically executes up to 2 forward lookahead probes into the combat action:
  $$\Delta t_1 = \max(2.0, 0.75 \times D_{\text{segment}}), \qquad \Delta t_2 = \max(4.0, 1.50 \times D_{\text{segment}})$$
  When visible combatants are resolved in the forward probe frames (e.g. inside translucent bubble shields or on rooftops), the probe stills and targets are handed to the multi-angle character picker wizard. Once confirmed by the user, the target is locked and tracked throughout the marked speed segment.
* **Translucent Shield Bubble & Combat Climax Vision Prompting:**
  Vision prompts explicitly command Gemini to scan roofs, piers, platforms, and translucent shield bubble domes for distant or crouched opponents holding weapons.
* **HUD Clearance Clamping:**
  When `avoidHud = true`, crop windows are clamped away from Fortnite's standard HUD zones (bottom-left health/shield bar and bottom-right weapon inventory).
* **Strict Boundary Anchoring:**
  Smoothed trajectories are strictly anchored at $t = 0.0$ and $t = D_{\text{segment}}$, guaranteeing deterministic interpolation across the entire segment duration.
* **Portrait Mode Center Slice Invariant (`PORTRAIT_01`):**
  When exporting in portrait mode (9:16), if the zoom box width $W_{\text{crop}}$ fits within the surviving portrait width ($W_{\text{crop}} \le W_{\text{surv}}$), `cropX` is strictly clamped within the active center slice $[(W_{\text{src}} - W_{\text{surv}})/2, (W_{\text{src}} + W_{\text{surv}})/2 - W_{\text{crop}}]$, guaranteeing that the tracking camera never reveals out-of-bounds void areas.

---

## 12. HUD Auto-Detector Streaming Memory Optimization  {#TL-HUDSTREAMING}
Governs automated computer vision HUD discovery in `HudAutoDetector.cs` and `HudImageOps.cs`.
* **Constant Memory Streaming Mandate:**
  Legacy implementations buffered up to 400 uncompressed BGR24 frames ($960 \times 540 \times 3 \approx 1.55\,\text{MB/frame}$) into an unbounded `List<byte[]>`, exceeding $600\,\text{MB}$ on the Large Object Heap (LOH) and triggering out-of-memory aborts and multi-second GC pauses on 8 GB systems.
* **Online Sequential Stream Architecture:**
  Full-array buffering is replaced with sequential single-frame streaming (`SampleFramesAsync`):
  * Active memory footprint is strictly bounded to $\le 25\,\text{MB}$ total, regardless of clip duration or frame count.
  * Temporal variance is accumulated online in a single pass using Welford's algorithm (`OnlineFrameAccumulator`):
    $$M_{k} = M_{k-1} + \frac{x_k - M_{k-1}}{k}, \qquad S_{k} = S_{k-1} + (x_k - M_{k-1})(x_k - M_k), \qquad \sigma^2 = \frac{S_n}{n - 1}$$
  * Temporal median and stability masks are computed on downscaled streaming buffers, releasing each raw video frame back to the system immediately after consumption.
* **Stretched-Resolution HUD Aspect Ratio Tolerance:**
  In competitive gaming resolutions where the video source aspect ratio deviates from 16:9 (e.g., 4:3 stretched like 1440x1080, 16:10 like 1680x1050, or 21:9 ultrawide), HUD bounding-box geometry is scaled by the canvas stretch factor:
  $$\text{stretchFactor} = \frac{W_{\text{src}} / H_{\text{src}}}{16.0 / 9.0}$$
  $$\text{minAspect} = \begin{cases} \text{spec.MinAspect} \times \text{stretchFactor}, & \text{if } \text{stretchFactor} < 1.0 \\ \text{spec.MinAspect}, & \text{otherwise} \end{cases}$$
  $$\text{maxAspect} = \begin{cases} \text{spec.MaxAspect} \times \text{stretchFactor}, & \text{if } \text{stretchFactor} > 1.0 \\ \text{spec.MaxAspect}, & \text{otherwise} \end{cases}$$
  $$\text{idealAspect} = \text{spec.IdealAspect} \times \text{stretchFactor}$$
  This prevents valid HUD elements (weapons, mini-map, bars) from being discarded by aspect ratio filters when playing on non-16:9 stretched resolutions.

