# SPECIFICATION 01: TIMELINE & COORDINATE MATH

## Code Mini-Map: Bound Source Files & Symbols
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `src/FortniteVideoSoftware.Core/Media/CoordinateMath.cs` | `CoordinateMath` | `SourceToPortraitCoords`, `QuantizeItemSize`, `SurvivingSourceWidth = 720`, `ContentAspect = 2.0/3.0` | Core mathematical transforms between 16:9 landscape source and 9:16 portrait export. |
| `src/FortniteVideoSoftware.Core/Media/OutputTimeline.cs` | `OutputTimeline`, `TimelineChunk`, `ChunkKind`, `Cut` | `SourceToOutput`, `OutputToSource`, `SnapInsertionPoint`, `NormalizeCuts`, `InsertionAt`, `Create` | Sole authoritative mathematical model for output durations and frame-to-output conversions. |
| `src/FortniteVideoSoftware.Core/Media/CanvasMath.cs` | `CanvasMath` | `OutXToSrcMs`, `SrcMsToOutX`, `ClampToClip`, `TimeSpanToPixels` | Bidirectional pixel-to-time timeline coordinate conversions. |
| `src/FortniteVideoSoftware.App/MainWindow.axaml.cs` | `MainWindow` | `SourceMsToOutputSeconds`, `UpdateMarkerFollow`, `UpdateTimelineMarkers`, `UpdateThumbnailButtonState` | Master timeline UI coordination, playhead tracking, and double-counting avoidance. |
| `src/FortniteVideoSoftware.App/MainWindow.Canvas.cs` | `MainWindow` (Partial) | `RedrawTimelineCanvas`, `UpdateMarkerRects`, `OnScrubberPointerMoved` | Primary timeline canvas drawing, hitbox evaluation, and caret positioning. |
| `src/FortniteVideoSoftware.App/GranularSpeedEditorWindow.axaml.cs` | `GranularSpeedEditorWindow`, `SegDragMode` | `OnPointerMoved`, `OnPointerReleased`, `RelayoutFrameLane`, `SelectSegment`, `ClampZoomInsideItsBlock`, `EndUndoGesture` | Granular speed timeline editing, rubberband sweeps, freeze & zoom manipulation. |
| `src/FortniteVideoSoftware.App/Controls/PhoneFrameMockup.axaml.cs` | `PhoneFrameMockup` | `DimmerFlanks`, `CenterClearSlice = 720`, `CanvasWidth = 1920`, `CanvasHeight = 1080` | High-fidelity 9:16 phone mockup preview with 600px semi-transparent flanks. |
| `src/FortniteVideoSoftware.App/Controls/TimelineKnob.cs` | `TimelineKnob` | `OnPointerPressed`, `OnPointerMoved`, `HitTestGrabZone` | High-precision timeline knob physics and collision boundaries. |
| `src/FortniteVideoSoftware.App/Controls/KineticScrubController.cs` | `KineticScrubController` | `OnScrubTick`, `ApplyFriction`, `Velocity`, `InertiaDecay` | Kinetic inertia scrubbing across timeline and preview views. |
| `src/FortniteVideoSoftware.App/Controls/TimelineLanesControl.axaml.cs` | `TimelineLanesControl` | `RedrawLanes`, `RenderTicks`, `SubPixelXOffset` | Multi-lane timeline rendering for speed, memes, and cuts. |
| `src/FortniteVideoSoftware.App/VoiceOverWindow.axaml.cs` | `VoiceOverWindow` | `_timeline`, `SourceMsToOutputSeconds`, `ReloadTimeline` | Audio take timeline alignment excluding memes to prevent double-counting. |
| `src/FortniteVideoSoftware.App/MusicWizardWindow.axaml.cs` | `MusicWizardWindow` | `OutputTimeline`, `FitByEndOfVideo`, `TimelineStartSeconds` | Phase 3 music timeline synchronization and end-fit calculations. |

---

## 1. 16:9 to 9:16 Portrait Canvas Trick & FOV Geometry
* **Source Resolution Mandate:** Native source footage must be 16:9 landscape ($1920 \times 1080$, $2560 \times 1440$, or $3840 \times 2160$).
* **Surviving Center Slice:** The active gameplay slice is strictly 720 source pixels wide:
  $$\text{SurvivingSourceWidth} = \frac{1280 \text{ (internal portrait width)}}{1.7777\dots \text{ (16:9 aspect ratio)}} = 720 \text{ source px}$$
  `CoordinateMath.cs` is the absolute mathematical authority. The dimmer must never be locked to 1280 source pixels.
* **Canvas Voids & Airspace Priority:** Scaling to 1280px width inside a 1080p width constraint alters the aspect ratio, creating exactly 150px black voids at the top and bottom of the final $1080 \times 1920$ canvas:
  $$\text{PadTop} = 150\text{px}, \quad \text{PadBottom} = 150\text{px}, \quad \text{ContentHeight} = 1620\text{px}$$
  * The top 150px void is reserved for the mobile status bar (clock at 9:41, battery, 5G, Wi-Fi arches) and SkiaSharp title text overlay.
  * The bottom 150px void houses mobile navigation controls.
  * Neither void element may ever overlap the centered 720px gameplay area.
* **Dimmer Mask:** `PhoneFrameMockup.axaml` dims inactive flanks via two $600\text{px}$ semi-transparent (`#99000000`) blocks on a $1920\text{px}$ canvas, leaving the $720\text{px}$ center clear:
  $$\text{FlankWidth} = \frac{1920 - 720}{2} = 600\text{px}$$
  Side dimming dynamically stretches on window resize to prevent distortion of the phone frame.
* **HUD Overlay Math Parity:** Live preview and FFmpeg export derive HUD dimensions from an identical basis:
  $$\text{ItemSize} = \text{contentH} \times \text{scale} \times \text{backendScale}$$
  Quantized via `CoordinateMath.QuantizeItemSize` (preview) and `MobileFilterBuilder.Build` (export). Heavy SkiaSharp image decodes and crop operations run off the UI thread.
* **No Mask Profile:** Read-only profile exporting $1080 \times 1920$ with zero HUD overlays. Injects explicit zero-size rectangles for all 6 HUD keys to prevent default restoration by the sanitizer. In-game overlay controls are hidden and forced off. Portrait source footage is cover-scaled and center-cropped to the 2:3 content area.

---

## 2. Authoritative OutputTimeline Model
`src/FortniteVideoSoftware.Core/Media/OutputTimeline.cs` is the sole mathematical authority mapping source time to finished output time across preview playback and FFmpeg rendering.

### Chunk Classification
$$\text{TotalOutputSeconds} = \sum_{c \in \text{Chunks}} \text{Duration}(c)$$

1. **`Normal`:** Consumes source time; consumes output time scaled by speed factor $S$:
   $$\Delta t_{\text{out}} = \frac{t_{\text{end}} - t_{\text{start}}}{S}$$
2. **`Freeze`:** Consumes 0 source time; occupies output time equal to hold duration $D_{\text{freeze}}$ ($0.2\text{s} \le D_{\text{freeze}} \le 10.0\text{s}$). Synthesizes a `Speed = 0` segment on export.
3. **`Cut` (Delete Parts):** Consumes source time; occupies 0 output time:
   $$\Delta t_{\text{out}} = 0$$
4. **`Insertion` (Meme Cutaway):** Consumes 0 source time; occupies output time equal to meme duration:
   $$\Delta t_{\text{out}} = D_{\text{meme}}$$

### Freeze Insertion Invariant
A freeze is an insertion, never a replacement. Freezing at $t_{\text{anchor}}$ for duration $D$ holds the exact video frame at $t_{\text{anchor}}$ for $D$ seconds, and resumes playback from $t_{\text{anchor}}$:
$$\text{TotalOutputSeconds}' = \text{TotalOutputSeconds} + D_{\text{freeze}}$$

### Caret Crossing Holds
Output-to-source mapping inside a hold is many-to-one:
$$\forall t_{\text{out}} \in [t_{\text{freeze\_start}}, t_{\text{freeze\_end}}]: \quad \text{OutputToSource}(t_{\text{out}}) = t_{\text{anchor}}$$
Playhead position is supplied directly and advances continuously in real time across the frozen span. Clicking inside a frozen span centers the playhead at the clicked coordinate, not the edge.

### Double-Counting Guard
Memes are anchored to clip-relative source seconds (`AtSourceSecRelative`). `MainWindow.SourceMsToOutputSeconds` and the `VoiceOverStudio` timeline deliberately omit memes from their `OutputTimeline` instances because independent offsets are applied during export (`MemeTimeInsertedBefore`). Memes must never be passed into `OutputTimeline` at these two call sites.

---

## 3. Marker & Speed Segment Physics
* **Marker Collision:**
  $$0 \le t_{\text{start}} \le t_{\text{end}} - \Delta t_{\text{min}} \quad (\Delta t_{\text{min}} = 1\text{ ms or } 1\text{ frame})$$
  Markers act as impenetrable hard limits.
* **Event Dispatching:** Dragging markers updates state on `PointerReleased` only. Pointer drag continuously drives throttled seeks without modifying play/pause state. Button presses while paused seek to mark; button presses while playing mark without pausing. Clicking a Granular Speed Segment selects it without moving the playhead.
* **Speed Segment Barrier:** Speed blocks enforce a strict 1000ms minimum gap barrier between neighboring segments:
  $$t_{\text{start}, i+1} - t_{\text{end}, i} \ge 1000\text{ms}$$
  Segments slide freely across the Freeze Camera marker. Minimum block duration is $200\text{ms}$.
* **Proportional Thirds Hitbox:** A block's grab zone for each edge is:
  $$\text{GrabZone} = \min\left(8\text{px}, \frac{\text{BlockWidth}}{3}\right)$$
  Nearest edge wins when adjacent.
* **Trim-Marker Clones:** Active speed blocks render SeaGreen vertical start/end edge sticks (24px hitbox, 3px stroke). Dragging routes to the identical block-edge resize pipeline and clamps to 1000ms. Swept rubber-band blocks arrive pre-selected.

---

## 4. Frozen Segment Object
* **Duration Bounds:**
  $$0.2\text{s} \le t_{\text{freeze}} \le 10.0\text{s}$$
  Preset buttons offer fixed lengths: $0.5\text{s}, 1.0\text{s}, 1.5\text{s}, 2.0\text{s}, 2.5\text{s}, 3.0\text{s}$.
* **Visual Representation:** Thumbnail lane displays the held frame with a cool blue wash, diagonal hatching, solid boundary posts, and a centered `❄ FROZEN X.Xs` label (dropped if block width $< 60\text{px}$).
* **Interaction:** Double yellow camera popsicles extend to the bottom timeline lane. Dragging popsicles resizes from that edge; dragging body moves anchor time; Arrow keys trim (END focused) or slide (body focused) by 1 frame.
* **Layer Priority:** Frozen band sits on top of speed blocks and intercepts clicks first. Dragging left tracks leading edge; dragging right tracks trailing edge.

---

## 5. DELETE PARTS (Cut Normalization)
* **Merge & Discard Rules:**
  * Adjacent cuts closer than $0.30\text{s}$ merge into one:
    $$t_{\text{start}, i+1} - t_{\text{end}, i} < 0.30\text{s} \implies \text{Merge}$$
  * Overlapping cuts merge.
  * Cuts $< 0.04\text{s}$ are discarded.
* **Safety Floor:** At least $0.5\text{s}$ of clip footage must survive:
  $$\text{SurvivingSourceSeconds} \ge 0.5\text{s}$$
* **Segment Reconciliation:** Cuts clear fully contained speed blocks, trim partial overlaps, drop remnants $< 200\text{ms}$, and clear freezes whose anchor was cut. Spanning blocks are preserved.
* **Playback Skip:** Playback loops across Main, Speed Editor, VoiceOver, and Music Wizard skip cut spans in a single seek on tick.
* **Audio Crossfade:** FFmpeg export injects an 8ms audio edge crossfade at each cut boundary to eliminate jump-cut pops.

---

## 6. Live Zoom-In Geometry & Workflow
* **Data Model:** `SpeedSegment` carries `ZoomX, ZoomY, ZoomW, ZoomH, ZoomOrigRes, ZoomSlow, ZoomStartMs, ZoomEndMs`.
* **Bounds & Clamping:** Floor constraint of $240 \times 135$ source pixels (`ZoomFloorW`/`ZoomFloorH`). Clamped to 16:9 (landscape) or 2:3 (portrait mapping directly to surviving 720px width via $2.0/3.0$ ratio):
  $$\text{Aspect} = \begin{cases} 16/9, & \text{Landscape} \\ 2/3, & \text{Portrait} \end{cases}$$
* **Color Token Mandate:** Zoom box, handles, and timeline markers use `AppZoomBrush` exclusively.
* **Interactive Gestures (ZOOMLIVE):** Non-destructive toggle; faint suggestion on click; commits on first drag/resize. Selecting a segment pauses playback on its first frame and mounts the box. Magnifiers own clicks while box is open; block edges stand down. Resizing block pulls zoom span within bounds.
* **Slow Zoom Ramp Constraints:**
  * Interpolates over 0.5s pre-roll and 0.5s post-roll outside `ZoomStartMs`/`ZoomEndMs`.
  * All-or-nothing: Requires 0.5s free buffer; otherwise snaps instantly.
  * Adjacent slow zooms require a 1.0s gap (`ZoomRampRequiredGapBetweenSlowZooms = 1.0s`); if $< 1.0\text{s}$, both drop to Instant.
  * Switching from Instant to Slow refuses if gap $< 1.0\text{s}$.

---

## 7. Meme Placement & Scrubbing Physics
* **Placement:** Anchored to clip-relative source seconds (`AtSourceSecRelative`). Placed on finished timeline in Speed Editor. `MemePlacement.Id` is a generated alphanumeric identifier, never derived from filename.
* **Snapping:** `SnapInsertionPoint` pushes placement forward past cuts, freezes, or speed blocks. Memes cannot interrupt speed blocks or share timestamps. Cuts delete contained memes.
* **Hitbox:** Rendered band has an invisible 18px minimum click floor (`MemeGrabMinWidthPx = 18`).
* **Scrubbing Physics:** Dragging sets caret to output start:
  $$t_{\text{caret}} = \text{SourceToOutput}(t_{\text{anchor}}) - D_{\text{meme}}$$
  Preview updates to source anchor. Canvas retains pointer capture. Ruler pivot updates live during drag. `BaseTimeline` cache is not cleared on move. Caret stays sticky on release until playback resumes.

---

## 8. Hitbox Priorities & Time Badges
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