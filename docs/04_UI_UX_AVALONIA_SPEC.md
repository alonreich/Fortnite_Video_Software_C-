# SPECIFICATION 04: UI/UX & AVALONIA SYSTEM SPECIFICATION

## Code Mini-Map: Bound Source Files & Symbols

> **⚠ CO-GOVERNED rows are bound by EVERY spec listed on them.** Reading only this one is not compliance (`SPEC_GOVERNANCE.md` §2).
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| src/FreeVideoStudio.App/AvaloniaApp.axaml | AppStyles | AppPrimaryButtonGradient, AppZoomBrush, AppDangerBrush, `Slider /template/ Thumb` style | Global styling dictionary, design tokens, and control templates. |
| src/FreeVideoStudio.App/Controls/CoachOverlay.cs | CoachOverlay, CoachStep, CoachGesture | Register, Replay, PlayOnce, ResolveHostPanel, Tick, TickMs = 33 | In-memory 30Hz vector walkthrough overlay and first-run user guidance. |
| src/FreeVideoStudio.App/Controls/FloatingNotice.cs | FloatingNotice, NoticeKind | Show, ShowAt, Success, Info, Warn, Error, OverlayCanvas, MaxConcurrent = 3 | Semantic floating pill notices with double layout pass rendering. |
| src/FreeVideoStudio.App/Controls/AmbientBubblesBackground.cs | AmbientBubblesBackground | BubbleCount = 35, PhysicsHz = 60, RenderHz = 30, StepPhysics, ResetBubble | Ambient floating bubble wallpaper: 60 Hz fixed-step physics, painting throttled to 30 fps. |
| src/FreeVideoStudio.App/Controls/FluidVolumeSlider.cs | FluidVolumeSlider | OnPointerMoved, StepPhysics, IsInteracting, RefreshGlassTheme, AppTubeGlassBaseColor, AppTubeGlassEdgeColor, AppTubeInnerShadowColor | Custom high-DPI tactile volume slider control. **⚠ CO-GOVERNED BY: 02**|
| src/FreeVideoStudio.App/Controls/ConfirmDialogWindow.axaml.cs | ConfirmDialogWindow | AskAsync, AskEditOrRemoveAsync, AskSaveChangesAsync, SetButtonText, UseDestructiveStyling | Destructive action confirmation dialog with loss itemization. |
| src/FreeVideoStudio.App/Controls/SpinningWheelSlider.cs | SpinningWheelSlider | OnPointerWheelChanged, SetRange, SetLabels, BeginSettle, NearestDetent | Precision wheel slider for speed, quality, and fine numeric tuning. |
| src/FreeVideoStudio.App/MainWindow.axaml.cs | MainWindow | AttachTitleBarDrag, BeginMoveDrag | Main window UI coordination, fluid container resizing, and borderless dragging. **⚠ CO-GOVERNED BY: 01, 02, GOV**|
| src/FreeVideoStudio.App/CropToolWindow.Volume.cs | CropToolWindow (Partial) | WireUpVolumeSlider, ApplyCurrentVolumeToMpvAsync | Master volume slider integration, video unmuting, and global sync in Crop Tools. **⚠ CO-GOVERNED BY: 02**|
| src/FreeVideoStudio.App/VideoMergerWindow.VolumeSync.cs | VideoMergerWindow (Partial) | ApplyPreviewPlayersVolume | Master volume slider sync across merger queue preview players. **⚠ CO-GOVERNED BY: 02**|
| src/FreeVideoStudio.App/GranularSpeedEditorWindow.axaml.cs | GranularSpeedEditorWindow | PushUndo, PerformUndo, PerformRedo, MaxUndoDepth = 40 | Granular Speed Editor 3-column layout and 40-deep immutable undo/redo engine. **⚠ CO-GOVERNED BY: 01, 05**|
| src/FreeVideoStudio.App/GranularSpeedEditorWindow.AiZoom.cs | GranularSpeedEditorWindow (Partial) | OnAiSmartZoomClickedAsync, ExecuteAiTrackingAsync, CancelAiTracking, AiTrackingStatusLabelCtl, AiTrackingPreviewBarCtl | AI tracking dispatch, interactive subject wizard, thinking overlay, and instant loop preview bar. **⚠ CO-GOVERNED BY: 01**|
| src/FreeVideoStudio.App/Controls/AiSubjectPickerWindow.axaml.cs | AiSubjectPickerWindow | SelectSubject, HoverSubject, ClearHover, OnFrameTabSelected | Multi-angle character picker wizard, 3-angle thumbnail selector tabs, and target selection grid. |
| src/FreeVideoStudio.App/Controls/AiSetupWizardWindow.axaml.cs | AiSetupWizardWindow | OpenGoogleAiStudio, TestAndSaveKeyAsync | First-time setup wizard for Gemini API key configuration, instant ping test, and settings save. |
| src/FreeVideoStudio.App/Controls/SettingsWindow.AiTracking.cs | SettingsWindow (Partial) | InitializeAiTrackingTab, SaveAiTrackingSettings, TestAiTrackingKeyAsync | Dedicated AI tracking tab in Preferences, API key input, model picker, and connection testing. |
| src/FreeVideoStudio.App/Controls/PhoneFrameMockup.axaml.cs | PhoneFrameMockup | PortraitImageControl, AppPortraitMaskBrush flanks, 600 · 720 · 600 columns | 9:16 phone frame mockup layout and flank dimming. **⚠ CO-GOVERNED BY: 01**|
| src/FreeVideoStudio.App/WindowBoundsHelper.cs | WindowBoundsHelper | Track, ApplyBounds, SaveBoundsAsync, SaveBoundsSync, SaveSnapshot (700 ms debounce) | Multi-window bounds and screen placement persistence. **⚠ CO-GOVERNED BY: 05**|
| src/FreeVideoStudio.App/Controls/WindowResizeGrip.cs | WindowResizeGrip | Attach, GripGeometry, TryInject, ResolveBrush | The one bottom-right resize affordance, shared by every window that has a grip (Crop Tools, Granular, Merger, Voice Over, Music Wizard, Finished dialog). |
| src/FreeVideoStudio.App/Controls/SettingsWindow.Output.cs | SettingsWindow (Partial) | BuildOutputFilesUi, ApplyPendingOutputSettings, SyncMergerOutputDirectoryState, OUTNAME_01 | Output Files tab: per-tool save folder and automatic file name. |
| src/FreeVideoStudio.App/Controls/SettingsWindow.axaml.cs | SettingsWindow | SelectTab, ShowAboutAsync, BuildAboutUi | Suite-wide preferences, About identity, hardware acceleration readout, and manual update checks. |
| src/FreeVideoStudio.App/Controls/UpdateAvailableWindow.axaml.cs | UpdateAvailableWindow | AskAsync, SetVersions, UpdateChoice | Update suggestion modal: three buttons plus close, scrollable release notes, non-nagging choices. |
| src/FreeVideoStudio.App/VideoMergerWindow.TimelineSelect.cs | VideoMergerWindow | AttachClipChip, IsOnSeekRows, SelectQueueRow, TimelineBlocks, ChipDragThresholdPx = 6 | Merger timeline selection ants, thumbnail-block select/drag reorder (ANTS_01, MERGERUX_01). **⚠ CO-GOVERNED BY: 01**|
| src/FreeVideoStudio.App/VideoMergerWindow.Session.cs | VideoMergerWindow | InitializeMergerGranular, InitializeLanes, InitializeTimelineSelection | Merger window session wiring. **⚠ CO-GOVERNED BY: 01, 05**|
| src/FreeVideoStudio.App/VideoMergerWindow.EdlPreview.cs | VideoMergerWindow | ClearPreviewSurface, EdlLoadGraceTicks = 8, MergerPreviewPlan | One-EDL merge preview and effects preview (MERGEPREVIEW_EDL_01, MERGEPREVIEW_01, EMPTYQUEUE_01). **⚠ CO-GOVERNED BY: 01, 03**|
| src/FreeVideoStudio.App/VideoMergerWindow.Lanes.cs | VideoMergerWindow | InitializeLanes, LaneCacheSize = 128, LaneParallelism = 2, BuildFilmTileAsync, BuildWaveTileAsync | Merger filmstrip and waveform lanes (LANES_01, LANECACHE_02). **⚠ CO-GOVERNED BY: 01**|
| src/FreeVideoStudio.App/VideoMergerWindow.Playhead.cs | VideoMergerWindow | AttachMergerPlayhead, PositionPlayhead, AntsThickness = 1.25, PlayheadWidth = 2 | Merger red playhead line over every timeline row (MERGERPLAYHEAD_01). |
| src/FreeVideoStudio.App/Infrastructure/GrabCursors.cs | GrabCursors | Open, Closed | Vector-drawn 24×24 open/closed-hand cursors (GRABCURSOR_01). |
| src/FreeVideoStudio.App/Infrastructure/LaneDiskCache.cs | LaneDiskCache | MaxFiles = 800 | On-disk lane cache under `ApplicationPaths.LaneCacheDirectory` (LANECACHE_02). **⚠ CO-GOVERNED BY: 05**|
| src/FreeVideoStudio.Core/Media/ProgressiveLanes.cs | LanePlanner, ThumbGrid, ProgressiveLaneRunner, LaneCache<T> | Plan, Slots, Pick, Generation, Cancel, MaxFrames = 90 | Lane tile planning, fixed thumbnail grid, progressive runner, LRU cache. |
| src/FreeVideoStudio.Core/Media/WaveformPeaks.cs | WaveformPeaks | SampleRate = 4000, PeaksPerSecond = 40, MaxPeaks = 6000 | Vector waveform peaks for the Merger waveform lane (LANECACHE_02). |
| src/FreeVideoStudio.App/PreviewDetachController.cs | PreviewDetachController | Attach, Detach, IsDetached | Multi-monitor video preview decoupling and full-screen window lifecycle. |
| src/FreeVideoStudio.App/PreviewMonitorWindow.axaml.cs | PreviewMonitorWindow | AttachHost, ReleaseHost | Dedicated secondary monitor video preview window. |
| src/FreeVideoStudio.App/Controls/MemePickerWindow.axaml.cs | MemePickerWindow, MemePickerRow | PickAsync, SetItems, LatestItems | Modal meme picker for ADD MEME: thumbnails, search, portrait warning, download buttons, live re-scan (MEMEPICK_01/02). |
| src/FreeVideoStudio.App/ViewModels/MemeChoiceViewModel.cs | MemeChoiceViewModel | Mode, Corner, Size, PlaySound, FullScreenLengthText, ApplyTo, LoadFrom | MEMEMODE_01 — the popup's FULL SCREEN / CORNER OVERLAY cards, bound. **⚠ CO-GOVERNED BY: 01**|
| src/FreeVideoStudio.App/Infrastructure/CornerMemeOverlayPresenter.cs | CornerMemeOverlayPresenter, CornerMemeFrames, CornerMemeSpan | Attach, SetSpans, Update, Hide, SplitPngStream | MEMEMODE_01 — corner memes previewed over the video host, same geometry as the export. **⚠ CO-GOVERNED BY: 03**|
| src/FreeVideoStudio.App/GranularSpeedEditorWindow.Memes.cs | GranularSpeedEditorWindow (Partial) | AddMemeAsync, EditMemeAsync, DrawCornerMemeBands, UpdateEditorCornerMemes | MEMEMODE_01 — add/change memes, corner bands, corner preview. **⚠ CO-GOVERNED BY: 01, 07**|
| src/FreeVideoStudio.App/MainWindow.CornerMemes.cs | MainWindow (Partial) | UpdateCornerMemeOverlay | MEMEMODE_01 — main preview corner memes. **⚠ CO-GOVERNED BY: 01**|
| src/FreeVideoStudio.App/VideoMergerWindow.CornerMemes.cs | VideoMergerWindow (Partial) | UpdateMergerCornerMemes | MEMEMODE_01 — Merger preview corner memes. **⚠ CO-GOVERNED BY: 01**|
| src/FreeVideoStudio.App/Infrastructure/MemeThumbnailCache.cs | MemeThumbnailCache | GetAsync, ThumbWidth = 160 | Bounded, cached meme preview pictures (image decode / one ffmpeg frame). |
| src/FreeVideoStudio.App/ViewModels/MainViewModel.cs | MainViewModel | IsPortraitMode, IsVideoLoaded, PlaybackTimeText | Core application view model driving top-level UI states and tool bindings. |
| src/FreeVideoStudio.App/ViewModels/ViewModelBase.cs | ViewModelBase | RaiseAndSetIfChanged, PropertyChanged | Base MVVM reactive observable notification implementation. |

---

## 1. Theme Governance & Token Mandate  {#UI-THEME}
* **Zero Raw Hex Styling:** Hardcoded hex color codes in shared styling, controls, and dynamic templates are strictly forbidden. All brushes, borders, and shadows must resolve through named DynamicResource keys in AvaloniaApp.axaml.
* **Token Registry Standards:**
  * Primary actions: AppPrimaryButtonGradient
  * Zoom and crop boxes: AppZoomBrush
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
* **Root Window Constraints:** every window sets its own `MinWidth`/`MinHeight` in its XAML: Main App 800×600, Crop Tools 800×600, Settings 800×600, Granular Speed Editor 900×600, Voice Over Studio 980×640, Video Merger 1200×700, Music Wizard 1300×730, Preview Monitor 480×300, Meme Picker 520×460, deployment progress 560×360.
  Editing windows never use `SizeToContent` and set no root `Width`/`Height` (their first size is FIRSTFIT_01's). `SizeToContent` is for dialogs only: `WidthAndHeight` on Confirm, Error, Audio Fix and Finished; `Height` on Cloud Sync, Update Available and Update Download (those may set a root `Width`).
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
* **Unified Master Fluid Volume Slider Rail:**
  * The vertical "test tube" fluid volume slider (`FluidVolumeSlider`) is embedded in all three core windows: `MainWindow.axaml` (right video rail), `VideoMergerWindow.axaml` (merger preview rail), and `CropToolWindow.axaml` (right video panel rail).
  * All three racks are wired by ONE helper, `Infrastructure.MasterVolumeUi.Bind` (VOLSHARED_01), to `MpvIpcClient.GlobalMasterVolume` + `GlobalMuted`, and repaint (slider, % badge or `MUTE`, speaker icon; dimmed slider while muted) on every change from any window, keyboard, mouse wheel (VOLWHEEL_01: 5%/notch, Shift 1%) or the Windows Volume Mixer (VOLSYNC_01). A per-binding re-entrancy guard prevents feedback storms.
  * The speaker button is the SHARED mute (VOLMUTE_01), remembered across launches (`PreviewMuted`). Audio rules: `02_AUDIO_ENGINE_MASTERING.md` AUD-MASTERVOL.
  * Crop Tool video preview applies global master volume upon loading rather than force-muting.
* **Audio Ingestion Prompts & Settings:**
  * The legacy "Video volume is too quiet or too loud" prompt is removed from both upload ingestion and Preferences/Settings.
  * Harsh peak burst detection (`ForHarshPeaks`) is preserved, offering the user peak softening if severe spikes occur.
  * Ingestion of clips lacking an audio stream displays a non-blocking `FloatingNotice` (`NoticeKind.Info`) without disrupting the upload workflow.

---

## 4. Tooltips, Safeguards, & Confirmations  {#UI-SAFEGUARDS}
* **Colloquial Tooltip Standard:** Written for a 14-year-old audience. Technical jargon (e.g., "temporal interpolation", "quantization matrix") is banned in user-facing tooltips (e.g., "Throw this piece into the trash", "Turn Magnetic Pull on or off").
* **Crop Tool Visual Safeguards:** "Finish & Save" displays a blocking "Thinking..." spinner, followed by an auto-closing (2.5s) Summary Overlay.
* **Crop save wording (CROPSAVEPROMPT_02):** Name the destination profile and offer `Save changes` (primary save action) / `Back to editing` (secondary). Going back, Escape, and closing the prompt leave the current edits open. Never call the cancel action `KEEP IT`: it does not tell the user which version they are keeping. Actual deletion remains a danger action.
* **Unsaved crop edits (CROPUNSAVED_01):** Switching profiles, returning to the Main App, and closing Crop Tools offer `Save changes` (green), `Back to editing` (neutral), and `Discard changes` (red). Enter, Escape, and closing the prompt return to editing. This includes deleting the last layer. Failed saves block departure, and timers/preview teardown start only after departure is approved. Saving succeeds only when both the live configuration and the named profile have been written.
* **Temporary crop selection zoom (CROPZOOMRESET_01):** Once a HUD quick selection is successfully added, restore the landscape viewport's zoom, fit mode, and scroll offset from before auto-zoom. Cancel restores the same state. A failed addition keeps its selection, and a manually zoomed view that did not trigger auto-zoom is preserved.
* **HUD controls (SPECTATINGDEFAULT_01 / NO_BOSS_HP_01):** New projects start with Spectating Eye on. Explicit saved on/off choices restore faithfully. No Mask forces HUD controls off and remains a clean initial state. Boss HP has no control, setting, detection role, or export flag; legacy `boss_hp` layer keys are ignored.
* **Destructive Confirmations:**
  * No global switch: each destructive action has its own `Confirm…` setting (`SettingsManager`) with its own default. ON: ConfirmVideoMergerClearAll, ConfirmCropToolReset, ConfirmCropToolDelete, ConfirmGranularDeleteSegment, ConfirmGranularClearAll, ConfirmMainAppCancel, ConfirmMainAppSwitchTool. OFF: ConfirmVideoMergerRemove (REMOVEUX_01; settings schema v9 turns it off once for upgraders), ConfirmMainAppCut, ConfirmVoiceOverDeleteTake, ConfirmFinishedDialogExit.
  * Confirmation dialogs itemize exactly what will be discarded (segments, cuts, memes, take count) with clear escape buttons (KEEP IT in the Granular Speed Editor, STAY HERE in the Main App; Crop Tools uses `Back to editing`, CROPSAVEPROMPT_02).
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
  * Semantic pill notifications in OverlayCanvas using 4 distinct semantic kinds (`NoticeKind`: Info, Success, Warning, Error).
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
* **Marching-Ants Rubber-Band (ZOOMANTS_01):** The zoom rubber-band is a LIVE animated outline, not a static dash, and it rides the SAME `_marchingAntsOffset` as the freeze markers and the selected-segment border so every outline in the window crawls in step. It is yellow (`AppZoomAntsColor`) as a deliberate exception to IDEA_6, which had removed yellow because users could not tell zoom from a speed segment; what makes the exception safe is the MOTION — a moving hairline is identified by its animation, which no static block has. The corner handles wear the same yellow, because IDEA_6's surviving half is that zoom may not speak in two colours at once; their white edging stays, or the grab points vanish over pale video.
  ⚠ **DASH PERIOD MUST DIVIDE THE OFFSET WRAP.** `_marchingAntsOffset` advances as `(offset + 1) % 8`. The `{2,2}` dash has period 4, and 4 divides 8, so the loop is seamless. The earlier `{4,3}` pattern has period 7 and visibly jumped every eighth tick. Never change one without the other.
* **Rubber-Band Weight (ZOOMANTS_02):** `ZoomBandThicknessPx` (currently `2.5`) is the single tunable for the band's stroke width; the original 1px hairline was hard to see against bright gameplay and nearly invisible mid-drag.
  ⚠ **DASHES ARE MEASURED IN MULTIPLES OF THE STROKE THICKNESS, NOT IN PIXELS.** Avalonia scales both `StrokeDashArray` and `StrokeDashOffset` by the thickness, so raising this value lengthens dashes and gaps by the same factor — intended, because a thick line wearing 1px dashes reads as a smudge rather than as ants. The ZOOMANTS_01 invariant SURVIVES any thickness change, because both the dash period and the offset wrap are expressed in thickness units: `{2,2}` keeps its period of 4 units and 4 keeps dividing 8. Changing the thickness is safe; changing the dash array is not.
* **Export Auto-Commit Guard (ZOOMCOMMIT_01):** Default placed zoom boxes are auto-committed prior to zoom mode toggle, Accept button click, seek-exit, and transport play, ensuring placed zoom boxes are never omitted from exported FFmpeg scripts.

---

## 7. Borderless Dialogs & Live Wallpaper  {#UI-BORDERLESS}
* **Ambient Bubbles Background:**
  * Exactly 35 background particles (BubbleCount = 35).
  * Particle opacity ranges between 5% - 10%.
  * Diameters range between 3px - 13px (with rare visual anomalies up to 40px).
  * Physics fixed timestep 60 Hz (`PhysicsHz`); painting throttled to 30 fps (`RenderHz`); IsHitTestVisible="False".
* **Borderless Window Dragging:**
  * Windows without OS titlebars (ExtendClientAreaTitleBarHeightHint="0") allow dragging from background areas via BeginMoveDrag on left mouse down (ClickCount < 2).
  * Bypasses child Button, Slider, and TextBox hit areas to prevent dragging during text selection.

---

## 7a. The Resize Grip Is Part Of A Borderless Window  {#UI-RESIZEGRIP}
Every window sets `ExtendClientAreaToDecorationsHint="True"`, so **the OS draws no resize frame**. The only thing telling a user a window can be resized — and the only thing they can grab — is what the app draws itself. A borderless window without a grip is not "clean"; it is a window most users believe is a fixed size.

* **One implementation, attached by every window that has a grip.** Crop Tools, Granular Speed Editor, Video Merger, Voice Over Studio, Music Wizard and the Finished dialog attach it; the Main App and Settings (and the Preview Monitor, Meme Picker and deployment progress windows) have no grip. `Controls/WindowResizeGrip.Attach(window, tooltip)` is the only way a window gets a grip. It adopts the `ResizeGrip` Border where the XAML already declares one and builds one where it does not, so the two cannot drift apart.
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

## 7b. GPU Swap-Chain Slot Ownership & Present Serialisation  {#UI-GPUSLOT}
* **`GPUSLOT_01` — a swap-chain slot has ONE owner, and a generation stamp proves it.**
  `MpvVideoView._importedImages` was a bare `ICompositionImportedGpuImage?[]` written by **three**
  threads with no lock, no `volatile` and no `Interlocked`: the render thread (import / replace a lost
  image), the UI thread inside `ImportAndPresentTexture`'s fire-and-forget continuation, and teardown.
  The continuation's catch blocks disposed `_importedImages[index]` rather than the `image` they had
  actually failed on — by then possibly a **different, newly imported** object. A failed present could
  therefore destroy the live image the render thread was about to draw with, and the render thread
  could hand a freshly disposed import to `UpdateWithKeyedMutexAsync`: a use-after-dispose across a COM
  boundary on the hot path. The silent swallow of `COMException 0x80070057` (`E_INVALIDARG`) was the
  field evidence of it, and that swallow is now logged (throttled) so the residual rate is measurable.
  * Slots hold an `ImportedImageSlot { Image, Generation }`; every successful import takes the next
    `Interlocked.Increment` of `_imageGeneration`.
  * The **render thread is the sole importer and sole publisher** (`Interlocked.Exchange`).
  * The **UI thread is a pure observer**: it may remove a slot only via `Interlocked.CompareExchange`
    against the exact slot it was given (`TryRetireSlot`), and may dispose only if that proves it won.
  * Teardown claims slots with `Interlocked.Exchange`. **All** disposal goes through the single
    `DisposeImportedImageOnUiThread` funnel. Never assign an element with a bare array write.
* **`GPUPRESENT_01` — one present in flight per slot, and frames are dropped, never queued.**
  `ImportAndPresentTexture` posts to the UI thread and does not await, so the render thread could issue
  present N+1 for a slot while present N was still running. Two overlapping `UpdateWithKeyedMutexAsync`
  calls on the same `IDXGIKeyedMutex` with the same `(ConsumerKey, ProducerKey)` pair produce an
  unordered `AcquireSync`/`ReleaseSync` sequence, and an `AcquireSync` for a key no producer will
  release is an **unbounded block on the UI thread** — the one deadlock `ZOOMHANG_01`'s render-thread
  timeout cannot save you from, because it is the UI thread that stops. A per-slot `SemaphoreSlim`
  (`_presentGates`) is taken with `Wait(0)`; a frame that cannot take its permit is **dropped** (counted
  in `DroppedPresentCount`, logged once), because at 60fps the next frame is 16 ms away and a queue here
  is latency the user sees. The permit is released in the continuation's `finally`, and by the poster
  itself if the dispatcher post throws.
* **`ZOOMHANG_01` is unchanged and still required.** The render thread's 750 ms bounded wait on
  `gpu.ImportImage`, and its once-only `_importTimeoutLogged`, stay exactly as written.

## 7c. The Granular Editor Opens Without Blocking The Dispatcher  {#UI-GRANOPEN}
* **`GRANPROBE_01` — `GranularSpeedEditorWindow.CreateAsync` is the supported entry point.**
  The constructor used to run `prober.GetDurationAsync()` and then `Task.Wait(500 ms)` — a blocking wait
  on the Avalonia dispatcher, up to half a second of frozen UI on every open, and a hard deadlock had
  any continuation inside `MediaProber` ever captured the UI `SynchronizationContext`. It violated
  `README.md` North Star Invariant 6 outright.
* **A factory, NOT two-phase initialisation.** `_trimEndMs` must be final before
  `TryRehydrateGranularRecovery()` (`RECOVERY_03`) runs and before any UI is built, so the duration is
  resolved **before the object exists** and passed in as `preProbedDurationSec`. The constructor keeps
  its "fully initialised on exit" contract, which is what the deferred-close chain
  (`05_SYSTEM_LIFECYCLE_STORAGE.md#SYS-WINSTATE`) depends on. An `Initialize()`-after-construction shape
  is explicitly rejected.
* **A failed or slow probe is not fatal.** `CreateAsync` bounds the wait at 10 s; `MediaProber` runs
  ffprobe through `AsyncProcessRunner`, which carries its own 15 s timeout, registers the child with
  `ChildProcessTracker` and terminates it through the graceful ladder while draining both pipes — so an
  overrun cannot orphan an ffprobe. On failure the window opens on the caller's trim window, exactly as
  it did when the old 500 ms wait expired. The difference is that the UI stayed responsive.

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
* **One EDL, the export's schedule (MERGEPREVIEW_EDL_01 / MERGEPREVIEW_01):** once the queue is analysed the preview plays the whole merge as ONE inline mpv EDL (`VideoMergerWindow.EdlPreview.cs`), so mpv's clock is the merged clock and a new layout (reorder, remove, scraper, thumbnail) reloads it at the same moment of the same file; effects play live from `MergerPreviewPlan` (the export's own chunks: speed per stretch, cut jumps, freeze holds, meme cut-aways). Clock rules: `01_TIMELINE_COORDINATE_MATH.md` §10 (TL-COMPOSITE).
* **Filmstrip + waveform lanes (LANES_01):** two thin lanes (`MergerFilmstripLane` 34 px, `MergerWaveformLane` 18 px) sit under the merged timeline, one tile per clip over that clip's slice of the merge. Tiles are planned LEFT→RIGHT (`LanePlanner`) and built by one `ProgressiveLaneRunner` per lane (max 2 ffmpeg each, strict start order); filmstrip tiles paint frame by frame (`ThumbnailStripGenerator.StreamAsync`, keyframes), waveform tiles are vector `WaveformPeaks` envelopes. Nothing waits on them and playback is NOT throttled for them (D9: they fill while the preview plays). Caching, keys and the resize/reorder debounce are LANECACHE_02's (below); the new run cancels the old one and a superseded tile never paints (generation check). Lanes are hidden until the merged timeline exists and are not hit-testable.
* **LANECACHE_02 — a clip is decoded once, ever (P11, 2026-09-27).** The lane cache key is the CLIP (file identity + kept window), never the width or the position. Filmstrip: a fixed grid per clip (`ThumbGrid`: 1 frame/s, max 90) generated by `ThumbnailStripGenerator.StreamAsync` (keyframes only), cached in memory (LRU 128) and on disk (`ApplicationPaths.LaneCacheDirectory`, PNG, `LaneDiskCache`, pruned to 800 files); drawn as `ThumbGrid.Slots` slots of lane-height × 16:9, each a `CroppedBitmap` of the nearest grid frame (`ThumbGrid.Pick`). Waveform: `WaveformPeaks` (mono 4 kHz PCM once → 40 peaks/s, max 6000, cached in memory + disk as float32) drawn as a vector envelope scaled to the clip's loudest peak and stretched to the clip's width. A resize or a reorder only re-places cached pieces (150 ms debounce); ffmpeg runs only for a clip never seen before.
* **REMOVEUX_01 — removal is instant and undoable (P11).** `ConfirmVideoMergerRemove` defaults to OFF (settings schema v9 turns it off once for upgraders); when a user turns it on (Settings › Confirmation Dialogs: "Video Merger: ask before removing clips"), the confirm uses `UseDestructiveStyling` (red, not the Enter key). Every user removal (not undo/redo/restore) shows one notice: "Clip removed from the list. Press Ctrl+Z to undo."
* **EMPTYQUEUE_01 — no clips, no picture (P11).** When the queue becomes empty the Merger stops mpv, forgets the EDL, stops the music preview and HIDES the video surface (the NO VIDEO LOADED card is semi-transparent and showed a ghost of the last frame); the surface returns with the first clip.
* **One selection, one order, two views (ANTS_01, D14):** the list and the merged timeline show the same `VideoQueue`. Selected clips get the SAME yellow marching ants on their timeline block (`Rectangle.TimelineAnts`, `AppWarningBrush`, dash 4,2, offset 0→6 in 0.6 s) as in the list; the timeline redraws on every list `SelectionChanged`. The clip labels (number · name) on the upper timeline are NOT interactive; the thumbnail blocks (`MergerClipBlocksLane`) are the handles and own the gesture rules (MERGERUX_01, below): drag sideways past 6 px = reorder with a yellow insertion bar, drop = `VideoQueue.Move(from, TimelineReorder.TargetIndex(...))`. While a block is pressed the timeline is not rebuilt (THUMB_02). Reorders from either view flow through the queue, so ids, autosave and undo follow.
* **Granular edit on the whole merge (MERGEEDIT_02, D16):** the GRANULAR SPEED button (`MergerGranularButton`, upper row right of the transport; enabled once the merged timeline matches the queue; Primary "GRANULAR SPEED", or Success "EDIT SPEEDS" when any clip has effects) opens the SAME `GranularSpeedEditorWindow` with `videoPath` = the merge's inline `edl://` URL, trim 0..merged length and every effect in merged ms (`MergeEditorSource`). In that mode (`IsMergeMode`) the editor keeps no crash-recovery session and no parked history (the Merger autosaves and undoes the EDL), stitches its film lane from one keyframe strip per clip into one composite bitmap, and draws numbered clip dividers at their OUTPUT positions (they follow speed edits). Accept = one Merger undo step "granular edit"; Cancel changes nothing. The editor's code-behind did not grow: three in-place seams + `GranularSpeedEditorWindow.Merge.cs`.
* **MERGERUX_01 — one job per area (P10, user decisions D22–D25, 2026-09-27):**
  * The UPPER timeline (time scale + scrub row) and the WAVEFORM lane SEEK (click or drag); the thumbnail blocks between them never do (`IsOnSeekRows`, MERGERPLAYHEAD_01 added the waveform on 2026-09-28); the clip labels on the scrub row are centred over their clip and are NOT handles (not hit-testable).
  * **MERGERPLAYHEAD_01 (user 2026-09-28):** the playhead is a 2 px red line (`AppPlayheadBrush` #ef4444) with a small downward cap, from the top of the time scale down through the scrub row, the thumbnail blocks and the waveform (`VideoMergerWindow.Playhead.cs`, one non-hit-testable layer spanning every row of the timeline grid, ZIndex 60). It follows the scrub slider's value (set by the tick and by a pointer scrub, so it moves on press) and uses the seek's own mapping (x / column width × duration), so it sits exactly over the matching thumbnail and waveform pixel. The slider's resting dot is hidden on this one slider only (`Slider#TimelineSlider /template/ Thumb` Opacity 0 in `VideoMergerWindow.axaml`); the slider still carries value, keyboard and automation. The selection ants are 1.25 px (`AntsThickness`, was 2) on both the upper timeline and the blocks, so they read gentler.
  * The thumbnail BLOCKS only select and move: hover = soft white glow + open-hand cursor, press/drag = closed-hand cursor (`GRABCURSOR_01`, `Infrastructure/GrabCursors.cs`, vector-drawn 24×24, stock Hand/SizeAll fallback); a plain click SELECTS WITHOUT SEEKING (`SelectQueueRow(i, preview:false)`); press + 6 px = drag, the block follows the pointer and the gold bar shows the landing slot; right-click selects without seeking.
  * After ANY reorder (list or blocks) the playhead stays on the same frame of the same clip (D22): the EDL reload remaps it through (file, source second); `TrackClipIds` marks a Move so the list's re-selection does not start a preview.
  * The list's press/move/release handlers are registered with `handledEventsToo` (the ListBoxItem handles the left press for selection — with `+=` the OLE drag never started). The playhead only re-selects a row when it CROSSES into another clip, never every tick and never during a press/drag (`QueueGestureActive`); a seek holds its target for 3 ticks while mpv still reports the old position.
  * Upper row = the Main App's upper row: SET THUMBNAIL left (identical button: Secondary, 16,8, image icon · text · icon, font 9, same SET / MOVE HERE / REMOVE states and class logic), transport centre, GRANULAR SPEED right (same GRANULAR SPEED / EDIT SPEEDS + Primary/Success logic) with the output path beside it; TOTAL SIZE / TOTAL LENGTH sit left of OUTPUT QUALITY in row 1.
  * X-axis: faint grid line at every tick (Main App spacing: 5/10/30/60/300 s) on the scrub row, labels at every tick plus 0:00 and the total. The filmstrip/blocks lanes start 8 px into the scrub row's dead lower band (below the resting knob) so the gap is gone without overlapping hitboxes (lanes are on top there).
* **Clip actions on both views (CLIPACTIONS_01, D20):** the Delete key (not while typing or merging) and "Remove from list" remove the SELECTED rows by index (a file queued twice loses only the selected copy); a confirm dialog appears only when `ConfirmVideoMergerRemove` is on; Ctrl+Z restores the clip with its id and effects. Right-click on a clip block selects it (P10: the timeline chips are no longer handles) and opens Move earlier / Move later / Remove. The filmstrip lane carries one outlined, draggable clip BLOCK per clip (`MergerClipBlocksLane`; the blocks own the click/drag/ants/right-click rules, MERGERUX_01); the scrubber stays for seeking. Every queue reorder uses `VideoQueue.Move` (never RemoveAt+Insert, which re-identifies the clip and drops its effects), and a queue change rebuilds the merged timeline synchronously from the analysis cache (no I/O) and repaints lanes from their tile cache at once; uncached work stays in the background workers.

---

## 10. Meme Selector Portrait Validation  {#UI-MEMESELECT}
* **Aspect Warning Rule:** In the meme selector (`MemeComboBox` DataTemplate), when `PortraitModeCheckbox` is CHECKED and a meme's cached aspect ratio is **greater than `0.85f`**, that item's text renders in RED with the tooltip `"Fit for landscape"`:
  $$\text{Warn} \iff \text{PortraitMode} \land \left(\frac{W_{\text{meme}}}{H_{\text{meme}}} > 0.85\right)$$
* **Live Refresh:** The styling re-evaluates dynamically — toggling `PortraitModeCheckbox` restyles the open list immediately; it is not computed once at load.
* **Intent:** It is a WARNING, not a block. The user may still pick the meme; they are told up front that a wide landscape clip will be heavily cropped or letterboxed on the 9:16 canvas, instead of discovering it after an export.
* **Data Source:** Aspect ratios come from the boot probe described in `03_FFMPEG_EXPORT_PIPELINE.md` §10 (FFM-MEMELIB).
* **Meme Picker (MEMEPICK_01/02):** ADD MEME in the Granular Speed Editor opens `MemePickerWindow`. It applies the SAME warning rule (portrait = the editor's portrait format), shows a preview picture per meme (`MemeThumbnailCache`: images decoded to 160 px wide, videos one ffmpeg frame piped as PNG, at most 3 at once, cached per path+size+mtime), filters by a name search, and carries `⬇ More videos` / `⬇ More pictures`. The list handed over is shown at once and re-scanned in the background; a download anywhere raises `MemeDirectory.Changed`, which re-scans too, and the newest list is handed back to the editor (`LatestItems`). The former Meme Wall (`MemeWallControl`, IDEA 008) was deleted: nothing ever made it visible (MEMEWALL_01).
* **One Popup, Two Cards (MEMEMODE_01):** `MemePickerWindow` is ONE modal screen: choose the meme, then two obvious cards — **FULL SCREEN** "Interrupt the gameplay" / "Video becomes +X.X sec longer" (the chosen meme's length; a picture is 4.0 s, a video is probed off the UI thread) and **CORNER OVERLAY** "Keep gameplay running" / "Video length stays the same". Choosing the corner reveals four corner buttons, Small / Medium / Large and "Play meme sound". Defaults: Full Screen, Bottom Right, Medium, sound on. Bound to `MemeChoiceViewModel` (MVVM_01: no new `FindControl`). `PickAsync(…, existing:)` reopens the same screen for a placed meme (double-click its band in the Speed Editor) to change file, mode, corner, size or sound; every change is one undo step. Corner memes draw as a thin dashed strip at the top of the Speed Editor lane over the gameplay they cover (click selects, REMOVE MEME deletes). Previews (Main App, Speed Editor, Merger) draw them with `CornerMemeOverlayPresenter` — simultaneous, never pausing/seeking/swapping the player; their sound is heard in the rendered preview mix (PREVIEWMIX_01).
* **Music During A Meme (MEMEMUSIC_01):** whether the music bed keeps playing under a meme (`KeepMusicDuringMeme`, `02` AUD-MUSICFADE) is asked with the themed `ConfirmDialogWindow` (DIALOG_01), not the Win32 box. Picking a meme asks only while the answer is unknown; finishing the Music Wizard with a meme selected asks again, since that is where the music is reconfigured.

---

## 11. Settings Window, About Tab & Universal Version Title Bar  {#UI-SETTINGS-ABOUT}
* **Dedicated About Tab:** Application identity, versioning, system runtime metadata, and update controls reside inside a dedicated `About` tab in `SettingsWindow.axaml`. The updates checkbox is removed from Confirmation Dialogs to ensure cohesive information architecture.
* **System & Hardware Status Readouts:** Displays .NET 9.0 NativeAOT runtime details, OS version, architecture, active video encoder hardware capability (e.g. `Auto (Hardware Acceleration Preferred)`), and the per-user storage root (`ApplicationPaths.ProgramDataRoot` = `%LOCALAPPDATA%\FreeVideoStudio`, USERSCOPE_01; `FVS_PROGRAMDATA_ROOT` overrides it).
* **Output Files Tab (OUTNAME_01):** Per tool (Main App, Video Merger): the save folder (read-only
  path box, `CHANGE FOLDER...` picker that accepts only writable folders, `USE DOWNLOADS` to clear it)
  and the automatic file name (text box, `DEFAULT` reset, live preview `name-1.mp4, name-2.mp4,
  name-3.mp4`). Values are pending until SAVE and are committed in the single SETTX_01 transaction;
  a changed merger folder is also written to the IPC state store the merger reads on open
  (`03` FFM-OUTNAME, ISSUE_04).
* **Manual Update Trigger:** The `Check For Updates Now` button executes on-demand checking (`UpdateService.CheckManualAsync`), providing inline status feedback and bypassing the 24-hour startup probe throttle.
* **Skipped Release Filter Management:** Displays skipped release tags with a `Clear Skip` action button allowing users to re-enable skipped update prompts without modifying raw files.
* **Direct Navigation Routes:**
  * Clicking `File -> About` or `Help -> About` in `MainWindow.axaml` and `VideoMergerWindow.axaml` invokes `SettingsWindow.ShowAboutAsync(owner)`, opening Settings directly to the About tab.
  * Clicking `Help -> Check for Updates...` directly executes `UpdateService.CheckManualAsync(owner)`.
* **Tool Window Identity:** The Granular Speed Editor uses `Free Video Studio - Speed Editor`; Crop Tools uses `Free Video Studio - Crop Tool`. The merger uses `Free Video Studio - Video Merger` before its versioned title is applied. XAML classes and XML namespaces use `FreeVideoStudio.App`.
* **Universal Title Bar Versioning:** Custom title bars in `MainWindow` and `VideoMergerWindow` dynamically format window titles as `Free Video Studio v{version}` and `Free Video Studio - Video Merger v{version}` via `DeploymentLifecycle.GetCurrentVersion()`.
* **Update Suggestion Dialog (UpdateAvailableWindow):**
  * Houses scrollable release notes ("What's New in this Release") parsed from GitHub release `body`.
  * Three buttons plus the title-bar close: `Update now` (`UpdateChoice.UpdateNow`), `Skip this version` (`SkipThisVersion`: this release is never offered again, newer ones are) and `Never tell me about updates again` (`NeverTellMeAgain`: turns `AutoUpdateChecks` off). Closing without choosing is `Dismissed`: nothing is stored, and a later start may offer the same release again.

---

## 12. Present Permit Before Keyed Mutex (GPUPRESENT_02)  {#UI-GPUPRESENT2}
* **Defect:** GPUPRESENT_01 checked the per-slot present permit AFTER `ReleaseSync(ConsumerKey)`. A dropped frame left the texture on key 1 with no consumer, and every later lap paid a 1000 ms `AcquireSync` timeout while holding `_renderLock`. Each UI stall of about 250 ms poisoned one more slot, so the preview decayed towards 1 fps until a resize.
* **Rule:** `UpdateSurface` takes the permit FIRST and non-blockingly probes `TryAcquireProducerKey` with a 0 ms timeout across the 16-slot ring. If a slot's producer key cannot be acquired immediately (the compositor is still actively sampling it), its permit is released on the spot and the search loop hops to the next candidate in the 16-slot pool without stalling `_renderLock`. Holding the permit proves no present is in flight, so a texture found on ConsumerKey is an orphan and `TryAcquireProducerKey` reclaims it (`AcquireSync(1,0)` + `ReleaseSync(0)`). If all 16 slots are genuinely in flight under extreme UI lag, `PumpEmptyRender` advances libmpv's frame clock via `MPV_RENDER_PARAM_SKIP_RENDERING=1` without blocking. `ImportAndPresentTexture` receives the permit and owns releasing it on every path.
* Avalonia's `UpdateWithKeyedMutexAsync` runs as a compositor server job, so its task completes after the compositor's acquire/release. A released permit therefore means the compositor is finished with the slot.
* ⚠️ **IMMUTABLE ARCHITECTURAL LOCK — libmpv PREVIEW PIPELINE (DO NOT MODIFY):**
  * `libmpv`'s Render API (`mpv_render_context_create`) does NOT support Direct3D 11 (`"d3d11"` returns `-19 / MPV_ERROR_NOT_IMPLEMENTED`).
  * The `WGL_NV_DX_interop` bridge (OpenGL FBO rendering directly to D3D11 shared textures inside GPU VRAM) is the **sole proven zero-copy pipeline** that permits interactive Avalonia XAML overlays without Win32 HWND airspace occlusion.
  * Attempting to replace WGL with non-existent libmpv D3D11 APIs results in a pitch-black screen with audio only.
  * Reintroducing any blocking wait on `AcquireSync` (e.g. `KeyedMutexWaitMs > 0`) or taking locks across timeouts causes 1 FPS degradation and `0x80070057` COM exceptions.
  * The non-blocking 0 ms 16-slot ring probing architecture is frozen and locked against future modifications.

---

## 13. Universal AI Smart Tracking Zoom Workflow & Interactive Subject Picker  {#UI-AIZOOM}
Governs the user interface, dialogs, visual overlays, and review flow for Universal AI Smart Tracking Zoom in `GranularSpeedEditorWindow`.
* **5-Phase User Experience Pipeline:**
  1. **Trigger & Configuration Gate:** User selects a speed segment and clicks `AI SMART ZOOM` on the toolbar. If no Gemini API key is configured, the editor opens `AiSetupWizardWindow` (step-by-step guidance with direct hyperlink to Google AI Studio, key test validation ping, and automatic persistence).
  2. **Phase 1 (Slicing):** Displays the centered status overlay with real-time feedback while an ultrafast 640p MP4 preview slice of the segment is extracted.
  3. **Phase 2 (Multi-Angle Discovery with Adaptive Forward Lookahead):**
     * 3 candidate still frames (start $15\%$, middle $50\%$, end $85\%$) are dispatched to Gemini Vision API to discover visible subjects.
     * If 0 opponents are resolved in the initial window (e.g. gliding towards an encounter), the system automatically executes up to 2 forward lookahead probes into the combat action ($+\max(2.0, 0.75 D)$, $+\max(4.0, 1.5 D)$) to detect visible opponents before failing.
  4. **Phase 3 (Interactive Subject Picker Wizard - `AiSubjectPickerWindow`):**
     * Displays a clean, high-DPI modal dialog with the 3 frames organized as switchable thumbnail selector tabs (`Frame 1`, `Frame 2`, `Frame 3`).
     * Candidate targets are listed in a selectable grid with descriptive visual labels (e.g. "Opponent with Shotgun", "Enemy on Roof", "Opponent in Bubble Shield").
     * Moving the pointer over a subject card or clicking it draws an interactive neon glowing highlight box on the active preview image.
     * User clicks `CONFIRM & TRACK TARGET` to proceed or `CANCEL` to abort cleanly.
  5. **Phase 4 (Dense Tracking with Visual Anchor & Local Player Exclusion):**
     * The confirmed target's bounding box is cropped from the clearest candidate angle frame into a JPEG thumbnail (`CropSubjectJpeg`) and attached to Gemini's multimodal payload.
     * The dense tracking prompt commands Gemini to focus exclusively on the visual reference subject and enforces the mandatory Fortnite 3rd-person exclusion rule against tracking the player's own avatar in the foreground.
     * The defensive sanitizer (`SanitizeWaypointsAgainstLocalPlayer`) suppresses any waypoint that momentarily latches onto the local player's avatar, enabling `AiTrajectorySmoother`'s inertial hold to maintain smooth tracking on the external opponent without camera whipping.
     * Overlay reappears displaying real-time progress: `"Dense tracking target '{label}'..."` with cancel capability via `CancellationTokenSource`.
  6. **Phase 5 (Instant Loop Preview Verification):**
     * Rather than destructively committing changes, the editor immediately enters a loop preview mode playing the tracked segment with dynamic camera cropping applied.
     * The `AiTrackingPreviewBarCtl` appears above the timeline with two explicit actions: `ACCEPT & APPLY` (commits the trajectory to the project) or `DISCARD` (reverts to original segment state).
* **Third-Person Avatar Exclusion UX & Scenery Purge:**
  * In third-person games like Fortnite, the camera is locked behind the local player's avatar. Offering the player's own back as a tracking target is prohibited.
  * Inanimate scenery (buildings, towers, statues, trees, landscape) is automatically purged from target options.
  * If a segment contains no external opponents or targets (even after forward combat lookahead), the system displays an informative modal dialog explaining why the local player is excluded and prompts the user to select a clip with a visible opponent or set zoom manually.
* **Persistent Modal Error Dialog Mandate:**
  * AI tracking failures, quota limits, or server errors must NEVER use auto-fading toasts (`FloatingNotice.Show` / `NotifyError`).
  * Failures route strictly to `ErrorReporter.ShowAsync(this, ...)` / `ErrorDialogWindow`, presenting a persistent modal dialog with clear plain-English root causes (e.g. rate limit explanations, invalid key guidance) and a `SHOW LOGS` action button that stays open until dismissed.
* **Visual Hierarchy & Scaling:**
  * The AI tracking thinking overlay (`AiTrackingOverlayCtl`) is centered and sized proportionally to the editor window, preventing cramped or unreadable text across 1080p, 1440p, and 4K displays.

