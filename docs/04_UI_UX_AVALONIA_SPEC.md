# SPECIFICATION 04: UI/UX & AVALONIA SYSTEM SPECIFICATION

## Code Mini-Map: Bound Source Files & Symbols

> **⚠ CO-GOVERNED rows are bound by EVERY spec listed on them.** Reading only this one is not compliance (`SPEC_GOVERNANCE.md` §2).
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| src/FortniteVideoSoftware.App/AvaloniaApp.axaml | AppStyles | AppPrimaryButtonGradient, AppZoomBrush, AppDangerBrush, SliderThumb | Global styling dictionary, design tokens, and control templates. |
| src/FortniteVideoSoftware.App/Controls/CoachOverlay.cs | CoachOverlay, CoachTours | Register, StartTour, ResolveHostPanel, OnRenderTick | In-memory 30Hz vector walkthrough overlay and first-run user guidance. |
| src/FortniteVideoSoftware.App/Controls/FloatingNotice.cs | FloatingNotice | Notify, NotifyError, OverlayCanvas, LayoutPass | Semantic floating pill notices with double layout pass rendering. |
| src/FortniteVideoSoftware.App/Controls/AmbientBubblesBackground.cs | AmbientBubblesBackground | BubbleCount = 35, PhysicsStep = 30Hz, Anomalies, ThrottledPaint | Ambient floating bubble wallpaper with 30fps throttled physics. |
| src/FortniteVideoSoftware.App/Controls/FluidVolumeSlider.cs | FluidVolumeSlider, Tactile | OnPointerMoved, VolumeChanged, EnableGlobalRipple, AppTubeBrush | Custom high-DPI tactile volume slider control. **⚠ CO-GOVERNED BY: 02**|
| src/FortniteVideoSoftware.App/Controls/ConfirmDialogWindow.axaml.cs | ConfirmDialogWindow | AskEditOrRemoveAsync, SetButtonText, OnConfirm | Destructive action confirmation dialog with loss itemization. |
| src/FortniteVideoSoftware.App/Controls/SpinningWheelSlider.cs | SpinningWheelSlider | OnPointerWheelChanged, SetValueSmooth, SnapToTick | Precision wheel slider for speed, quality, and fine numeric tuning. |
| src/FortniteVideoSoftware.App/MainWindow.axaml.cs | MainWindow | SizeToContent, MinWidth, MinHeight, BeginMoveDrag | Main window UI coordination, fluid container resizing, and borderless dragging. **⚠ CO-GOVERNED BY: 01, 02, GOV**|
| src/FortniteVideoSoftware.App/GranularSpeedEditorWindow.axaml.cs | GranularSpeedEditorWindow | PushUndo, Undo, Redo, MaxUndoDepth = 40 | Granular Speed Editor 3-column layout and 40-deep immutable undo/redo engine. **⚠ CO-GOVERNED BY: 01, 05**|
| src/FortniteVideoSoftware.App/Controls/PhoneFrameMockup.axaml.cs | PhoneFrameMockup | DimmerFlanks, CenterClearSlice = 720 | 9:16 phone frame mockup layout and flank dimming. **⚠ CO-GOVERNED BY: 01**|
| src/FortniteVideoSoftware.App/WindowBoundsHelper.cs | WindowBoundsHelper | Track, RestoreBounds, SaveBoundsSync, DebounceMs = 700 | Multi-window bounds and screen placement persistence. **⚠ CO-GOVERNED BY: 05**|
| src/FortniteVideoSoftware.App/Controls/WindowResizeGrip.cs | WindowResizeGrip | Attach, GripGeometry, TryInject, ResolveBrush | The one bottom-right resize affordance, shared by every window in the suite. |
| src/FortniteVideoSoftware.App/Controls/SettingsWindow.axaml.cs | SettingsWindow | SelectTab, ShowAboutAsync, BuildAboutUi | Suite-wide preferences, About identity, hardware acceleration readout, and manual update checks. |
| src/FortniteVideoSoftware.App/Controls/UpdateAvailableWindow.axaml.cs | UpdateAvailableWindow | AskAsync, SetVersions, UpdateChoice | 4-way update suggestion modal with scrollable release notes and non-nagging choices. |

---

## 1. Theme Governance & Token Mandate  {#UI-THEME}
* **Zero Raw Hex Styling:** Hardcoded hex color codes in shared styling, controls, and dynamic templates are strictly forbidden. All brushes, borders, and shadows must resolve through named DynamicResource keys in AvaloniaApp.axaml.
* **Token Registry Standards:**
  * Primary actions: AppPrimaryButtonGradient, AppPrimaryBrush
  * Zoom and crop boxes: AppZoomBrush, AppZoomGlow
  * Destructive triggers: AppDangerButtonGradient, AppDangerBrush
  * Glow shadows: AppDropIndicatorShadow, AppPhaseGlowShadow, AppRecordingGlowShadow
  * Table rules: AppBorderBrush (the frame around a table), AppTableHairlineBrush (rules INSIDE it — deliberately ~10% alpha, enough to guide the eye across a row, not enough to compete with the frame). Defined for both themes: low-alpha white on the dark ground, low-alpha ink on the light one.
* **High-Contrast Typography:** Saturated brand buttons (TikTok, YouTube Shorts, Instagram) and action buttons force AppOnAccentTextBrush (pure white, #FFFFFFFF) to guarantee readability.
* **Unified Slider Thumb Specification:** Exactly one Slider /template/ Thumb style block is permitted suite-wide, enforcing circular discs with subtle elevation shadows.
* **A Vertical Slider's Width Is Not A Free Parameter (SLIDER_07) — VERIFIED ON SCREEN:** Fluent's vertical Slider template is a Grid whose TRACK column is `Auto` and whose neighbouring tick-bar column is `*`. The star column absorbs every pixel beyond the track's natural size, so **the track does not centre — it is pushed against one edge**, and the Thumb, which centres on the TRACK, ends up centred on that edge with half its dot outside the Slider's own bounds. Whatever sits beside it covers that half.
  Consequences, both observed:
  * Setting `Width` LARGER than the track buys dead space on one side and a knob hard against the other — not a wider grab area.
  * Setting `Width` SMALLER (or trimming the thumb's width to match) slices the knob in half down its middle. A first attempt at a narrow `MixFader` class did exactly this and shipped a visibly bisected dot.

  So the only honest lever on a vertical fader's footprint is the THUMB, and the suite already has one: `Slider.CompactSlider` (44px thumb), whose own note names "vertical volume" as its case. **Leave vertical sliders to size themselves — set neither `Width` nor `MinWidth`** — and do not re-introduce a thumb-width override without first replacing the template's column layout so the track genuinely centres.
  ⚠️ The thumb's WIDTH and HEIGHT are not interchangeable either: on a vertical slider the HEIGHT is the travel axis, the grab target and the rail inset (half of it). Changing it breaks the rail.
* **The Grown Knob Outranks Everything Around It (SLIDER_08):** `ClipToBounds="False"` only buys the knob permission to PAINT outside its parents; it says nothing about paint ORDER. At 0% and 100% the thumb grows past the ends of its own track, straight over whatever sits above and below it — a caption, a value readout, a frame edge — and siblings declared later paint on top of it, so the knob comes out sliced even though nothing is clipping it.
  A slider whose knob grows on press therefore needs **both**: `ClipToBounds="False"` on the slider, its template parts and every hosting container, AND a `ZIndex` on the slider that beats its siblings. The thumb's own `ZIndex` inside the template is not enough — `ZIndex` only orders siblings within one parent, and the slider's siblings are outside the template.
  Ancestors are not a problem (a child always paints over its own parent's background); only SIBLINGS are.
* **Among Siblings, DECLARATION ORDER Is The Mechanism — ZIndex Is Not (SLIDER_09):** A high `ZIndex` on the growing slider was tried first and did **not** lift it above the caption above it and the value below it. The control that must paint last has to be **written last** in its panel; `ZIndex` is agreement, not the lever.
  The fix keeps `Grid.Row` exactly as it was — the LAYOUT slot does not move, only the control's place in the paint sequence — so nothing about the arrangement changes. Concretely: caption, value, **then** the slider, all three still on their own rows.
  ⚠️ This is invisible in code review: reordering two siblings that sit in different Grid rows looks like a pure no-op. Any panel relying on it needs a comment saying so, or the next tidy-up silently re-breaks it.
* **A Named Control With A Literal Value And No Writer Is A Dead Readout (QUALITY_04):** `QualityLabel` was declared `Text=""` in XAML — a literal, not a binding — and nothing in code ever assigned to it. The value behind it was computed correctly on every edit and went nowhere, so the feature looked *missing* rather than broken, which is the harder failure to spot.
  Two rules follow:
  1. A control that displays computed state must either BIND to it or be written by exactly ONE named method. `Text=""` with no writer is the signature of this bug — if a readout is ever blank when it should not be, find its writer first.
  2. **Prime it at startup.** The readout and its tooltip are refreshed once at wire-up, because otherwise nothing runs until the control is TOUCHED — and the user who never touches it is precisely the user the default exists for.

  Where a compiled binding would need a converter for a value the view-model produces as a string (a colour name or hex), one assignment in the method that already owns the refresh is less machinery and cannot silently unbind itself.
* **One Writer For Text And Tooltip (QUALITY_04):** the quality dial's tooltip was set in a `ValueChanged` handler while its readout was set elsewhere. Two places writing about one piece of state is how they drift. Both now come from a single pass so the words and the number can never disagree.
  `SIZEESTIMATE_01`: the main size label and tooltip now bind to `ExportViewModel` properties published by the shared background estimator. The caption says `ESTIMATED FILE SIZE` and sits beside the PROCESS/export button in the same wrapping group; values use MB/GB/TB and an approximation mark. Initial loading may say `Calculating…`; ordinary edits retain the current number until the next result to avoid flashing the layout.
* **A Label's Layout Slot Still Takes Input:** a `TextBlock` overlapping an interactive control swallows presses aimed at it even though nothing is visible there. Decorative text over or beside a control gets `IsHitTestVisible="False"`.
* **Press Feedback:** Installed globally via Tactile.EnableGlobalRipple on the Button class. Tactile ripple is suppressed on disabled buttons or via Tactile.IsRippleSuppressed="True". Custom controls bind to AppTube* / AppDial* tokens.

---

## 2. High-DPI Scaling & Fluid Layouts  {#UI-DPI}
* **First-Run Size Comes From The Display (FIRSTFIT_01):** `MinWidth`/`MinHeight` are a FLOOR, never a default size. The first open of every editing window is computed from the display — 80% of its working area, landscape 16:9, centred — and every open after that is whatever the user left it at. Authoritative rule and the maths: `05_SYSTEM_LIFECYCLE_STORAGE.md` §3 (SYS-WINSTATE).
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
* **No Measurement May Read Back What It Sizes (LIST_06) — SUITE-WIDE:** Any code that MEASURES content and then WRITES a size must clamp against a container the write cannot affect. Measuring a control whose own width derives from the value being computed closes a layout loop: the value oscillates every pass, and any scrollbar in that subtree is re-laid-out under the pointer, so dragging it jumps instead of scrolling and the user sees the columns pulse.
  The three defences, all required:
  1. clamp against an ancestor sized by the WINDOW (e.g. the step's own panel), never against the control being sized;
  2. express the derived size as a CONSTRAINT (`MaxWidth`) rather than an input (`Width`), and reserve scrollbar gutters permanently (`ScrollViewer.VerticalScrollBarVisibility="Visible"`) so a scrollbar appearing cannot change the available width mid-measurement;
  3. make the measurement non-reentrant and write only on a real change — an identical assignment still invalidates layout, which keeps the size-changed events that call it firing forever.
  Worked example: `02_AUDIO_ENGINE_MASTERING.md` §6 (AUD-DIALOGS), Step 1 song table.

---

## 3. UI Prompts, Sliders, & Text Simulation  {#UI-PROMPTS}
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

## 4. Tooltips, Safeguards, & Confirmations  {#UI-SAFEGUARDS}
* **Colloquial Tooltip Standard:** Written for a 14-year-old audience. Technical jargon (e.g., "temporal interpolation", "quantization matrix") is banned in user-facing tooltips (e.g., "Throw this piece into the trash", "Turn Magnetic Pull on or off").
* **Crop Tool Visual Safeguards:** "Finish & Save" displays a blocking "Thinking..." spinner, followed by an auto-closing (2.5s) Summary Overlay.
* **Crop save wording (CROPSAVEPROMPT_02):** Name the destination profile and offer `Save changes` (primary save action) / `Back to editing` (secondary). Going back, Escape, and closing the prompt leave the current edits open. Never call the cancel action `KEEP IT`: it does not tell the user which version they are keeping. Actual deletion remains a danger action.
* **Unsaved crop edits (CROPUNSAVED_01):** Switching profiles, returning to the Main App, and closing Crop Tools offer `Save changes` (green), `Back to editing` (neutral), and `Discard changes` (red). Enter, Escape, and closing the prompt return to editing. This includes deleting the last layer. Failed saves block departure, and timers/preview teardown start only after departure is approved. Saving succeeds only when both the live configuration and the named profile have been written.
* **Temporary crop selection zoom (CROPZOOMRESET_01):** Once a HUD quick selection is successfully added, restore the landscape viewport's zoom, fit mode, and scroll offset from before auto-zoom. Cancel restores the same state. A failed addition keeps its selection, and a manually zoomed view that did not trigger auto-zoom is preserved.
* **HUD controls (SPECTATINGDEFAULT_01 / NO_BOSS_HP_01):** New projects start with Spectating Eye on. Explicit saved on/off choices restore faithfully. No Mask forces HUD controls off and remains a clean initial state. Boss HP has no control, setting, detection role, or export flag; legacy `boss_hp` layer keys are ignored.
* **Destructive Confirmations:**
  * Settings toggle defaults to protected (ConfirmDestructiveActions = true).
  * Confirmation dialogs itemize exactly what will be discarded (segments, cuts, memes, take count) with clear escape buttons (KEEP IT, STAY HERE).
  * Cut confirmation defaults to OFF.
  * Destructive actions must never execute in outer button click handlers that own flyouts.
* **One Transport Per Surface (MAINEND_01):** Where a screen offers both an on-screen PLAY button and a keyboard shortcut, both must call the SAME method. Two copies of `SetPropertyAsync("pause", ...)` drift: a fix applied to one leaves the other trapped, and the user cannot tell which control is misbehaving. Behaviour: `01_TIMELINE_COORDINATE_MATH.md` §8 (TL-ENDSTOP).
* **ONE ACTIVATION PATH PER CONTROL (DOUBLEFIRE_01) — NON-NEGOTIABLE:** Avalonia raises **both** `Command` and `Click` on a single button press. A control that carries a `Command` must NOT also carry a `Click` handler, and vice versa.
  On an ordinary button a duplicate activation is merely wasteful. **On a TOGGLE it is invisible and catastrophic**, because the second call undoes the first:

  | press | call 1 | call 2 | what the user sees |
  | :--- | :--- | :--- | :--- |
  | PLAY | paused → **play** | playing → **pause** | one frame, then stopped. "The button does nothing." |

  The two wirings typically live in different files, and neither is wrong on its own — only their sum is, which is why this is not visible to reading. It cost several rounds of diagnosis on `PlayPauseButton` (`KEYFOCUS_01` added the Command and left the old Click attached) and was found only from a transport trace showing PLAY and PAUSE from one click.
  Diagnostic tells: the action appears to do nothing; an equivalent NON-toggle control on the same screen works (an unconditional `pause=no` is idempotent, so MARK START kept playing while PLAY could not).
  `TryExecutePlayPause` carries a 60 ms coalescing guard that LOGS and drops a second activation. It is a net, not a licence: if that line appears in a log, find the duplicate wiring and delete it.
* **Empty Selection Guidance:** Clicking ZOOM-IN or DELETE PARTS without a selected timeline range displays:
  > *"You did not selected an area on time the timeline yet!"*
  Pauses for 1.0s, and triggers an automated vector cursor walkthrough: Mark Start -> Play -> Mark End.

---

## 5. Walkthroughs & Notifications  {#UI-COACH}
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

## 6. Granular Speed Layout & Undo/Redo  {#UI-GRANULAR}
* **Layout Geometry:**
  * Transport row right flank houses ZOOM-IN exclusively.
  * Speed wheel row is structured as a 3-column grid:
    * Left column: ADD/REMOVE MEME
    * Center column: Spinning wheel and speed presets
    * Right column: DELETE PARTS (with solid disc scissors icon)
* **Abandoned Zoom Block Lifecycle (ZOOMLIVE_07):**
  * An UNTOUCHED zoom box commits nothing. The faint suggestion box raised by ZOOM-IN becomes real only on the first drag or resize.
  * If pressing ZOOM-IN auto-created a temporary 1x speed segment to hang the zoom on, that segment MUST BE DELETED when the user leaves zoom mode without touching the box — by toggling ZOOM-IN off, by pressing Escape, or by clicking a different block. Without this the timeline silently accumulates invisible dummy 1x segments the user never asked for and cannot see.
  * **The cleanup must key on the block being LEFT, not the block being selected.** Leaving zoom mode is part of selecting another block, so deciding from the CURRENT selection deletes the block the user just clicked. The abandoned block is tracked by its own index; the box is closed BEFORE the selection is re-pointed; and both the stored selection index and the incoming index are shifted down when the removal occurred earlier in the list.
* **Seam Arbitration (SEAM_01):** Blocks may touch (`SegGapMs = 0`). Where two edges share a pixel, the pointer's side of the seam decides which one the drag grabs — never the selection, never draw order. Full rule: `01_TIMELINE_COORDINATE_MATH.md` §3 (TL-MARKERS).
* **Seek Coalescing (SEEKSTORM_01):** Nothing that talks to mpv may run per `PointerMoved`. Drag-follow seeks are gated to one per `SeekCoalesceMs = 60` and the pending target is flushed by a timer. The gate is TIME-based and must never be re-coupled to an in-flight flag — mpv clears that flag in 1–3ms, which silently disabled the throttle and let one drag issue 310 seeks in 1.74s, deadlocking the UI thread against the render thread.
* **Undo/Redo Engine:**
  * Ctrl+Z / Ctrl+Y and dedicated UI buttons operate on an immutable state stack capped at 40 snapshots (MaxUndoDepth = 40).
  * Holds value types and immutable records only; UI controls, bitmaps, and IPC handles are strictly excluded.
  * Playhead does not move during undo/redo operations.
  * Undo history clears when the window is accepted or closed.
* **Full-Card Clickable RadioButton Hitbox (ZOOMCARD_01):** The "How should the zoom arrive?" dialog replaces stock Avalonia RadioButton layout with a full-surface card `ControlTemplate`. The entire card area (padding, badges, text headers, descriptions) serves as the click target with visual hover elevation, eliminating narrow bullet hitboxes.
* **Persistent Style Dialog & Playhead Exit Dismissal (ZOOMSTYLE_02):** The zoom arrival dialog remains open while aiming, resizing, or dragging the rubberband box. The dialog and rubberband box cleanly unbind and disappear the moment the playhead exits the active zoom segment across all transport actions (timeline click-to-seek, scrubbing, and continuous playback).
* **Live GPU Crop & Slow Glide Preview (ZOOMPREVIEW_01):** Real-time preview coordinates in `UpdateLiveZoomCrop` calculate off the exact timeline playhead position during pause and respect dynamic hardware resolution, rendering instantaneous snappy crops and smooth slow glides directly in mpv.
* **Export Auto-Commit Guard (ZOOMCOMMIT_01):** Default placed zoom boxes are auto-committed prior to zoom mode toggle, Accept button click, seek-exit, and transport play, ensuring placed zoom boxes are never omitted from exported FFmpeg scripts.

---

## 7. Borderless Dialogs & Live Wallpaper  {#UI-BORDERLESS}
* **Ambient Bubbles Background:**
  * Exactly 35 background particles (BubbleCount = 35).
  * Particle opacity ranges between 5% - 10%.
  * Diameters range between 3px - 13px (with rare visual anomalies up to 40px).
  * Physics timestep locked to 30Hz; paint calls throttled to 30fps; IsHitTestVisible="False".
* **Borderless Window Dragging:**
  * Windows without OS titlebars (ExtendClientAreaTitleBarHeightHint="0") allow dragging from background areas via BeginMoveDrag on left mouse down (ClickCount < 2).
  * Bypasses child Button, Slider, and TextBox hit areas to prevent dragging during text selection.

---

## 7a. The Resize Grip Is Part Of A Borderless Window  {#UI-RESIZEGRIP}
Every window sets `ExtendClientAreaToDecorationsHint="True"`, so **the OS draws no resize frame**. The only thing telling a user a window can be resized — and the only thing they can grab — is what the app draws itself. A borderless window without a grip is not "clean"; it is a window most users believe is a fixed size.

* **One implementation, attached by every window.** `Controls/WindowResizeGrip.Attach(window, tooltip)` is the only way a window gets a grip. It adopts the `ResizeGrip` Border where the XAML already declares one and builds one where it does not, so the two cannot drift apart.
* **Why this is a shared class and not twenty lines per window.** It *was* twenty lines per window, and the copies had already diverged into the worst possible split:

  | window | before |
  | :--- | :--- |
  | Voice Over Studio | grip drawn, wired — worked |
  | Granular Speed Editor | grip drawn, **never wired** — a dead decoration |
  | Main App, Music Wizard, Video Merger, Crop Tools, Settings | no grip at all |

  A control that is drawn but does nothing when grabbed is worse than no control: it spends the user's trust and teaches them the corner does not work. Same rule as UI-DETACH's "Never A Dead Click".
* **A grip is offered only where it is real.** `Attach` returns without drawing anything when `CanResize` is false, and the drag is refused while the window is maximized — dragging a maximized corner fights the window manager into a half-restored state.
* **Hit-testing:** the grip's `Background` is `Transparent`, never null. A null background is not hit-testable, so the mark would be visible and unclickable — the dead decoration again, by another route.
* **Layering:** `ZIndex = int.MaxValue`. The corner stays grabbable even under a "please wait" overlay or a floating notice, which is exactly when a user is most likely to want the window bigger.
* **Windows rooted on a single control** (Crop Tools roots a Border) are wrapped in a Grid so the grip has a sibling slot. Wrapping preserves the window's name scope, so every existing `FindControl` keeps working.
* **North Star 1 and 5:** the mark is `PathGeometry` built in memory — no image asset beside the binary — and its stroke resolves through the `AppBorderBrush` token, never a literal.

---

## 8. Detachable Preview Console  {#UI-DETACH}
* **Scope:** Main App, Granular Speed Editor, Music Wizard Phase 3, Voice Over Studio, and Video Merger.
* **Window Memory:** Geometry, display device ID, and maximize state persist independently per screen via WindowBoundsHelper. First launch centers over the owning parent window.
* **Interactivity Safeguards:**
  * Monitor controls disable during media loading.
  * Controls hide during active zoom box drawing.
  * **Stage Unity:** In the Granular Speed Editor, detaching transfers the video player AND its zoom guideline box, dimmer overlay and phone frame mockup TOGETHER into the floating window. Those overlays are positioned in the VIDEO's own coordinate space — moving the picture alone leaves the zoom box and dimmers drawn over an empty panel.
  * **Never A Dead Click:** The button is greyed with an explanatory tooltip when there is nothing to pop out; it does not exist at all in the Video Merger until at least one video is queued; and it sits BELOW any blocking "please wait" or confirmation overlay so it can never be clicked through a screen that is deliberately refusing input.
  * **Re-entry Guard:** Returning to Music Wizard phase 3 while detached pulls the preview home before rebuilding, so a second player is never created alongside the first.
  * Detached preview automatically returns home to the parent window before parent window teardown.

---

## 9. Video Merger Queue Interaction Contract  {#UI-MERGERQUEUE}
* **Selection Model:** The queue list is `SelectionMode="Multiple"`. Multi-selection is performed EXCLUSIVELY via `Ctrl+Click` (individual) and `Shift+Click` (contiguous range).
* **Rubber-Band Drag-Select Is STRICTLY FORBIDDEN:** Dragging on list items is reserved for REORDERING the queue and for accepting external file drops. Enabling the default canvas drag-selection behaviour breaks clip reordering outright — the two gestures are the same gesture.
* **Dual Drop Paths (neither may be removed):** Avalonia OLE drag & drop accepts MULTIPLE external files at once with the same duplicate detection as the Upload Files button, while internal item-reorder drags continue to work. The legacy `WM_DROPFILES` interop fallback exists because Windows UIPI silently blocks OLE drops into an elevated process.
* **Title Truncation:** Long filenames use `TextTrimming="CharacterEllipsis"` with the full path exposed as the item tooltip. Font size does NOT shrink — titles never clip outside the `VIDEOS LIST` container and never wrap.

---

## 10. Meme Selector Portrait Validation  {#UI-MEMESELECT}
* **Aspect Warning Rule:** In the meme selector (`MemeComboBox` DataTemplate), when `PortraitModeCheckbox` is CHECKED and a meme's cached aspect ratio is **greater than `0.85f`**, that item's text renders in RED with the tooltip `"Fit for landscape"`:
  $$\text{Warn} \iff \text{PortraitMode} \land \left(\frac{W_{\text{meme}}}{H_{\text{meme}}} > 0.85\right)$$
* **Live Refresh:** The styling re-evaluates dynamically — toggling `PortraitModeCheckbox` restyles the open list immediately; it is not computed once at load.
* **Intent:** It is a WARNING, not a block. The user may still pick the meme; they are told up front that a wide landscape clip will be heavily cropped or letterboxed on the 9:16 canvas, instead of discovering it after an export.
* **Data Source:** Aspect ratios come from the boot probe described in `03_FFMPEG_EXPORT_PIPELINE.md` §10 (FFM-MEMELIB).

---

## 11. Settings Window, About Tab & Universal Version Title Bar  {#UI-SETTINGS-ABOUT}
* **Dedicated About Tab:** Application identity, versioning, system runtime metadata, and update controls reside inside a dedicated `About` tab in `SettingsWindow.axaml`. The updates checkbox is removed from Confirmation Dialogs to ensure cohesive information architecture.
* **System & Hardware Status Readouts:** Displays .NET 9.0 NativeAOT runtime details, OS version, architecture, active video encoder hardware capability (e.g. `Auto (Hardware Acceleration Preferred)`), and ProgramData storage root.
* **Manual Update Trigger:** The `Check For Updates Now` button executes on-demand checking (`UpdateService.CheckManualAsync`), providing inline status feedback and bypassing the 24-hour startup probe throttle.
* **Skipped Release Filter Management:** Displays skipped release tags with a `Clear Skip` action button allowing users to re-enable skipped update prompts without modifying raw files.
* **Direct Navigation Routes:**
  * Clicking `File -> About` or `Help -> About` in `MainWindow.axaml` and `VideoMergerWindow.axaml` invokes `SettingsWindow.ShowAboutAsync(owner)`, opening Settings directly to the About tab.
  * Clicking `Help -> Check for Updates...` directly executes `UpdateService.CheckManualAsync(owner)`.
* **Universal Title Bar Versioning:** Custom title bars in `MainWindow` and `VideoMergerWindow` dynamically format window titles as `Fortnite Video Software v{version}` and `Fortnite Video Software - Merger v{version}` via `DeploymentLifecycle.GetCurrentVersion()`.
* **Update Suggestion Dialog (UpdateAvailableWindow):**
  * Houses scrollable release notes ("What's New in this Release") parsed from GitHub release `body`.
  * Clarifies dismissal copy to `"Not Now (Remind me later)"` to distinguish transient postponement from version skipping.
