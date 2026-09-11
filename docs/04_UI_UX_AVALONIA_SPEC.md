# SPECIFICATION 04: UI/UX & AVALONIA SYSTEM SPECIFICATION

## Code Mini-Map: Bound Source Files & Symbols
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| src/FortniteVideoSoftware.App/AvaloniaApp.axaml | AppStyles | AppPrimaryButtonGradient, AppZoomBrush, AppDangerBrush, SliderThumb | Global styling dictionary, design tokens, and control templates. |
| src/FortniteVideoSoftware.App/Controls/CoachOverlay.cs | CoachOverlay, CoachTours | Register, StartTour, ResolveHostPanel, OnRenderTick | In-memory 30Hz vector walkthrough overlay and first-run user guidance. |
| src/FortniteVideoSoftware.App/Controls/FloatingNotice.cs | FloatingNotice | Notify, NotifyError, OverlayCanvas, LayoutPass | Semantic floating pill notices with double layout pass rendering. |
| src/FortniteVideoSoftware.App/Controls/AmbientBubblesBackground.cs | AmbientBubblesBackground | BubbleCount = 35, PhysicsStep = 30Hz, Anomalies, ThrottledPaint | Ambient floating bubble wallpaper with 30fps throttled physics. |
| src/FortniteVideoSoftware.App/Controls/FluidVolumeSlider.cs | FluidVolumeSlider, Tactile | OnPointerMoved, VolumeChanged, EnableGlobalRipple, AppTubeBrush | Custom high-DPI tactile volume slider control. |
| src/FortniteVideoSoftware.App/Controls/ConfirmDialogWindow.axaml.cs | ConfirmDialogWindow | AskEditOrRemoveAsync, SetButtonText, OnConfirm | Destructive action confirmation dialog with loss itemization. |
| src/FortniteVideoSoftware.App/Controls/SpinningWheelSlider.cs | SpinningWheelSlider | OnPointerWheelChanged, SetValueSmooth, SnapToTick | Precision wheel slider for speed, quality, and fine numeric tuning. |
| src/FortniteVideoSoftware.App/MainWindow.axaml.cs | MainWindow | SizeToContent, MinWidth, MinHeight, BeginMoveDrag | Main window UI coordination, fluid container resizing, and borderless dragging. |
| src/FortniteVideoSoftware.App/GranularSpeedEditorWindow.axaml.cs | GranularSpeedEditorWindow | PushUndo, Undo, Redo, MaxUndoDepth = 40 | Granular Speed Editor 3-column layout and 40-deep immutable undo/redo engine. |
| src/FortniteVideoSoftware.App/Controls/PhoneFrameMockup.axaml.cs | PhoneFrameMockup | DimmerFlanks, CenterClearSlice = 720 | 9:16 phone frame mockup layout and flank dimming. |
| src/FortniteVideoSoftware.App/WindowBoundsHelper.cs | WindowBoundsHelper | Track, RestoreBounds, SaveBoundsSync, DebounceMs = 700 | Multi-window bounds and screen placement persistence. |

---

## 1. Theme Governance & Token Mandate
* **Zero Raw Hex Styling:** Hardcoded hex color codes in shared styling, controls, and dynamic templates are strictly forbidden. All brushes, borders, and shadows must resolve through named DynamicResource keys in AvaloniaApp.axaml.
* **Token Registry Standards:**
  * Primary actions: AppPrimaryButtonGradient, AppPrimaryBrush
  * Zoom and crop boxes: AppZoomBrush, AppZoomGlow
  * Destructive triggers: AppDangerButtonGradient, AppDangerBrush
  * Glow shadows: AppDropIndicatorShadow, AppPhaseGlowShadow, AppRecordingGlowShadow
* **High-Contrast Typography:** Saturated brand buttons (TikTok, YouTube Shorts, Instagram) and action buttons force AppOnAccentTextBrush (pure white, #FFFFFFFF) to guarantee readability.
* **Unified Slider Thumb Specification:** Exactly one Slider /template/ Thumb style block is permitted suite-wide, enforcing circular discs with subtle elevation shadows.
* **Press Feedback:** Installed globally via Tactile.EnableGlobalRipple on the Button class. Tactile ripple is suppressed on disabled buttons or via Tactile.IsRippleSuppressed="True". Custom controls bind to AppTube* / AppDial* tokens.

---

## 2. High-DPI Scaling & Fluid Layouts
* **Root Window Constraints:**
  All application windows enforce:
  `xml
  SizeToContent="WidthAndHeight" MinWidth="900" MinHeight="600"
  `
  Hardcoded root Width and Height properties are strictly banned.
* **Container Expansion:**
  * Action rows, headers, and time badges use Auto or * grid tracks with hard MinWidth/MinHeight floors.
  * Scrubber row height is locked to 44px.
  * Inner timeline canvas rows are locked to 32px.
  * Action WrapPanel children maintain an 8px vertical gap and 12px horizontal gap.

---

## 3. UI Prompts, Sliders, & Text Simulation
* **Freeze Duration Prompt:**
  * Centralized popup displays SELECT FREEZE DURATION.
  * Transport row left flank hosts the freeze toggle button and 6 duration presets (0.5s, 1.0s, 1.5s, 2.0s, 2.5s, 3.0s) arranged as two rows of three matching speed presets.
  * Gentle pulse/blink animation terminates automatically after 15s or 10 blinks to prevent memory leaks and dispatcher saturation.
* **Timeline Overlay Jitter Mitigation:** Camera overlay render coordinates are decoupled from playback timers; mouse tracking uses absolute canvas coordinates.
* **Dynamic Control Visibility:**
  * Speed slider (right of PROCESS) and Output Size slider (left) start collapsed.
  * Unhide immediately when a video is loaded.
  * Persist visible until application restart.
* **Text Wrapping Simulation:** Live preview clones backend FFmpeg wrap and scale algorithms. Text renders in the top-center of the video canvas to guarantee scale and wrap fidelity despite top void omission.

---

## 4. Tooltips, Safeguards, & Confirmations
* **Colloquial Tooltip Standard:** Written for a 14-year-old audience. Technical jargon (e.g., "temporal interpolation", "quantization matrix") is banned in user-facing tooltips (e.g., "Throw this piece into the trash", "Turn Magnetic Pull on or off").
* **Crop Tool Visual Safeguards:** "Finish & Save" displays a blocking "Thinking..." spinner, followed by an auto-closing (2.5s) Summary Overlay.
* **Destructive Confirmations:**
  * Settings toggle defaults to protected (ConfirmDestructiveActions = true).
  * Confirmation dialogs itemize exactly what will be discarded (segments, cuts, memes, take count) with clear escape buttons (KEEP IT, STAY HERE).
  * Cut confirmation defaults to OFF.
  * Destructive actions must never execute in outer button click handlers that own flyouts.
* **Empty Selection Guidance:** Clicking ZOOM-IN or DELETE PARTS without a selected timeline range displays:
  > *"You did not selected an area on time the timeline yet!"*
  Pauses for 1.0s, and triggers an automated vector cursor walkthrough: Mark Start -> Play -> Mark End.

---

## 5. Walkthroughs & Notifications
* **CoachOverlay Tour Engine:**
  * In-memory vector tours drawn on a 30Hz DispatcherTimer.
  * Automatically shows first 3 launches per screen (tracked in UiStateStore); permanent replay available via the ? titlebar button.
  * Tour layer blocks underlying hit-testing.
  * Unwraps decorators via ResolveHostPanel.
* **FloatingNotice System:**
  * Semantic pill notifications in OverlayCanvas using 4 distinct semantic tokens (Info, Success, Warning, Danger).
  * Deduplicates identical notices within a 1.4s window.
  * Enforces a maximum concurrency ceiling of 3 visible notices.
  * Executes a double layout pass to prevent top-left rendering flashes before layout computation finishes.

---

## 6. Granular Speed Layout & Undo/Redo
* **Layout Geometry:**
  * Transport row right flank houses ZOOM-IN exclusively.
  * Speed wheel row is structured as a 3-column grid:
    * Left column: ADD/REMOVE MEME
    * Center column: Spinning wheel and speed presets
    * Right column: DELETE PARTS (with solid disc scissors icon)
* **Undo/Redo Engine:**
  * Ctrl+Z / Ctrl+Y and dedicated UI buttons operate on an immutable state stack capped at 40 snapshots (MaxUndoDepth = 40).
  * Holds value types and immutable records only; UI controls, bitmaps, and IPC handles are strictly excluded.
  * Playhead does not move during undo/redo operations.
  * Undo history clears when the window is accepted or closed.

---

## 7. Borderless Dialogs & Live Wallpaper
* **Ambient Bubbles Background:**
  * Exactly 35 background particles (BubbleCount = 35).
  * Particle opacity ranges between 5% - 10%.
  * Diameters range between 3px - 13px (with rare visual anomalies up to 40px).
  * Physics timestep locked to 30Hz; paint calls throttled to 30fps; IsHitTestVisible="False".
* **Borderless Window Dragging:**
  * Windows without OS titlebars (ExtendClientAreaTitleBarHeightHint="0") allow dragging from background areas via BeginMoveDrag on left mouse down (ClickCount < 2).
  * Bypasses child Button, Slider, and TextBox hit areas to prevent dragging during text selection.

---

## 8. Detachable Preview Console
* **Scope:** Main App, Granular Speed Editor, Music Wizard Phase 3, Voice Over Studio, and Video Merger.
* **Window Memory:** Geometry, display device ID, and maximize state persist independently per screen via WindowBoundsHelper. First launch centers over the owning parent window.
* **Interactivity Safeguards:**
  * Monitor controls disable during media loading.
  * Controls hide during active zoom box drawing.
  * Detached preview automatically returns home to the parent window before parent window teardown.