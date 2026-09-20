# FORTNITE VIDEO SOFTWARE: ARCHITECTURAL SPECIFICATIONS

## 1. Mission Architecture
Fortnite Video Software is a specialized, hardware-accelerated desktop video editing suite built with C# and Avalonia UI on .NET 9 (Native AOT compatible). The system transforms raw 16:9 widescreen gameplay footage into master-quality 9:16 portrait montages, mobile highlights, and social video deliverables with zero manual keyframing.

---

## 2. The 7 North Star Architectural Invariants
All subsystems, controls, and rendering components across `src/` must strictly enforce these seven non-negotiable architectural pillars:

1. **Single Binary Executable Mandate:** Zero loose companion assets (`.gif`, `.png`, `.wav`, `.ico`) alongside the output binary. All UI overlays, guide indicators, brand icons, and animations must be generated dynamically via code or vector path geometry (`PathGeometry`) in memory.
2. **Absolute Authority for Time:** `src/FortniteVideoSoftware.Core/Media/OutputTimeline.cs` is the sole mathematical model for output durations and frame-to-output conversions across both live preview playback and FFmpeg rendering.
3. **Strict A/V Process Isolation:** Master preview volume (Windows OS PID session) and preview playback remain completely decoupled from FFmpeg export filtergraphs. Master preview slider adjustments must never alter export loudness.
4. **Leak-Free Render Pipelines:** The deprecated FFmpeg `zoompan` filter is banned suite-wide due to fatal native heap leaks. Dynamic zooms must be achieved via frame-evaluated padding, dynamic scaling, cropping, and contrast-adaptive sharpening (`cas=0.5`).
5. **Zero Raw Hex Styling:** All Avalonia styles, controls, and dynamic templates must resolve colors exclusively through named `DynamicResource` tokens in `AvaloniaApp.axaml`. Hardcoded hex values in shared styling are strictly prohibited.
6. **Thread-Bound Safety Contracts:** UI dispatchers must never block on native audio/video subsystem calls. WASAPI audio capture lifecycles run on an isolated serialized worker thread; SkiaSharp snapshot encoding and heavy image decodes execute off the UI thread.
7. **Monotonic Progress Guarantee:** Render progress tracking must be cost-weighted and mathematically monotonic (P(n+1) >= P(n)). Progress bars may never snap, stutter, or lerp backward across multi-pass operations.

---

## 3. Domain Routing

Route by FILE (below) or by SYMBOL (`INDEX.md`). Full paths live in each spec's Code Mini-Map — basenames here are unique across `src/`.
`⚠` = CO-GOVERNED by more than one spec: read EVERY spec that lists it (`INDEX.md` §1 names them).

**[`01_TIMELINE_COORDINATE_MATH.md`](file:///C:/Fortnite_Video_Software%20-%20C%23/docs/01_TIMELINE_COORDINATE_MATH.md)** — Timeline & coordinate math — 16:9->9:16 geometry, OutputTimeline chunk model, markers, freezes, cuts, zoom spans, meme anchors, hitboxes.

```
CanvasMath.cs  CoordinateMath.cs  ⚠GranularSpeedEditorWindow.axaml.cs  KineticScrubController.cs
MainWindow.Canvas.cs  MainWindow.Shortcuts.cs  MainWindow.Wireup.cs  ⚠MainWindow.axaml.cs
⚠MusicWizardWindow.axaml.cs  OutputTimeline.cs  ⚠PhoneFrameMockup.axaml.cs  TimelineKnob.cs
TimelineLanesControl.axaml.cs  ⚠VoiceOverWindow.axaml.cs
```

**[`02_AUDIO_ENGINE_MASTERING.md`](file:///C:/Fortnite_Video_Software%20-%20C%23/docs/02_AUDIO_ENGINE_MASTERING.md)** — Audio engine & mastering — PID preview volume, LUFS targets, sidechain ducking, Voice Over Studio, WASAPI threading, music bed fades.

```
AudioFilterChain.cs  AudioLoudnessProbe.cs  ⚠FluidVolumeSlider.cs  ⚠MainWindow.axaml.cs
MicLevelMonitor.cs  MpvIpcClient.cs  ⚠MusicWizardWindow.axaml.cs  VoiceOverPreviewPlayer.cs
⚠VoiceOverWindow.axaml.cs  VoiceRecorder.cs
```

**[`03_FFMPEG_EXPORT_PIPELINE.md`](file:///C:/Fortnite_Video_Software%20-%20C%23/docs/03_FFMPEG_EXPORT_PIPELINE.md)** — FFmpeg export pipeline — encoder discovery, zoom filtergraph, concat/bitrate, meme concat & cutaway preview, fades, progress, binary paths, meme library.

```
ExportViewModel.cs  FfmpegDiagnosticCollector.cs  GpuCapabilityProbe.cs  GranularSpeedBuilder.cs
HardwareScanner.cs  QualityLadder.cs  MainWindow.SizeEstimate.cs  OutputFileSize.cs  OutputSizeEstimator.cs
MemePreviewDirector.cs  MergerWorker.cs  MobileFilterBuilder.cs  ProcessWorker.cs
TextOverlayGenerator.cs  ZoomPreviewSimulator.cs
```

**[`04_UI_UX_AVALONIA_SPEC.md`](file:///C:/Fortnite_Video_Software%20-%20C%23/docs/04_UI_UX_AVALONIA_SPEC.md)** — UI/UX & Avalonia — design tokens, high-DPI layout, tooltips, confirmations, coach tours, granular editor layout, undo/redo, detachable previews, merger queue.

```
AmbientBubblesBackground.cs  AvaloniaApp.axaml  CoachOverlay.cs  ConfirmDialogWindow.axaml.cs
WindowResizeGrip.cs
FloatingNotice.cs  ⚠FluidVolumeSlider.cs  ⚠GranularSpeedEditorWindow.axaml.cs
⚠MainWindow.axaml.cs  ⚠PhoneFrameMockup.axaml.cs  SpinningWheelSlider.cs  ⚠WindowBoundsHelper.cs
```

**[`05_SYSTEM_LIFECYCLE_STORAGE.md`](file:///C:/Fortnite_Video_Software%20-%20C%23/docs/05_SYSTEM_LIFECYCLE_STORAGE.md)** — System lifecycle & storage — mutexes, logging pipeline, window bounds, deferred-close contract, crash recovery, atomic writes, dev build harness & fix sentinels, signing.

```
⚠ApplicationPaths.cs  AtomicJsonFile.cs  Build.cmd  DeploymentLifecycle.cs  dev.cmd
⚠GranularSpeedEditorWindow.axaml.cs  MaskOverlayManager.cs  ProjectRecoveryService.cs
⚠RecoveryManager.cs  RuntimeLog.cs  UiStateStore.cs  ⚠WindowBoundsHelper.cs  LatestEstimateWorker.cs
```

**[`06_PROJECT_DOCUMENT_MODEL.md`](file:///C:/Fortnite_Video_Software%20-%20C%23/docs/06_PROJECT_DOCUMENT_MODEL.md)** — Project document model — the saveable `.fvsproj`, schema versioning & the amputation rule, AOT-safe JSON, atomic persistence & backup, source integrity, recent projects, trim/AOT analyser policy.

```
⚠AtomicJsonFile.cs  FortniteVideoSoftware.App.csproj  ⚠OutputTimeline.cs  ProjectDocument.cs
ProjectSerializer.cs  ProjectStore.cs  RecentProjects.cs
```

---
## 4. Agent Navigation & Entry Protocol
1. **Entry Rule:** Always read [`SPEC_GOVERNANCE.md`](file:///C:/Fortnite_Video_Software%20-%20C%23/docs/SPEC_GOVERNANCE.md) before performing any code generation or inspection.
2. **Context Routing:** Know the FILE -> use §3 above. Know only a SYMBOL, CONSTANT or TAG (e.g. `SnapInsertionPoint`, `QuietBoostReductionFactor`, `ZOOMLIVE_07`) -> grep [`INDEX.md`](file:///C:/Fortnite_Video_Software%20-%20C%23/docs/INDEX.md). Read the ONE spec you land on; do not pre-load the others.
3. **Co-Governed Files (⚠):** A file listed under more than one spec is bound by ALL of them. Reading one is NOT compliance — this is the exact leakage `SPEC_GOVERNANCE.md` §2 exists to prevent.
4. **Cite Anchors, Not Numbers:** Quote the stable `{#ANCHOR}` id (e.g. `FFM-BINPATH`) in the Proof-of-Read header. Section numbers shift as specs grow.
5. **Land The Sentinel With The Fix:** Any fix worth an engineering tag gets a `CHECK_TAG` line in `dev.cmd`'s `VERIFY_PATCHES` in the SAME change, so a revert halts the build instead of surviving to the next test cycle — `05_SYSTEM_LIFECYCLE_STORAGE.md` §4a (SYS-DEVBUILD). That section also states why a correct source file is not evidence that the running binary contains the fix.
