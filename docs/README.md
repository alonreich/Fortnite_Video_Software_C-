# FREE VIDEO STUDIO: ARCHITECTURAL SPECIFICATIONS

## 1. Mission Architecture
Free Video Studio is a specialized, hardware-accelerated desktop video editing suite built with C# and Avalonia UI on .NET 9 (Native AOT compatible). The system transforms raw 16:9 widescreen gameplay footage into master-quality 9:16 portrait montages, mobile highlights, and social video deliverables with zero manual keyframing.

### Product and repository identity

`FreeVideoStudio.sln` contains the renamed `src/FreeVideoStudio.App`, `src/FreeVideoStudio.Core`, `tests/FreeVideoStudio.App.Tests` and `tests/FreeVideoStudio.Core.Tests` projects. The application assembly is `FreeVideoStudio`; its root namespace remains `FreeVideoStudio.App`. The core assembly and namespace are `FreeVideoStudio.Core`. `FvsBuild`, `FvsVerify`, `.fvsproj` and the existing `FVS_*` environment variables retain their internal names.

Use [distribution identity](09_DISTRIBUTION_AND_RELEASE.md#DIST-IDENTITY) for packaging names and local validation commands, and [storage migration](05_SYSTEM_LIFECYCLE_STORAGE.md#SYS-REBRAND) for canonical AppData paths and legacy compatibility. `project_structure.txt` at the repository root is the compact entry point. Repository links are relative to the checkout; the existing GitHub repository address is unchanged by the local rebrand.

---

> ### 🛑 CRITICAL MANDATE FOR ALL AI AGENTS: NEVER EXECUTE `Build.cmd`
> Autonomous AI agents are **STRICTLY FORBIDDEN from ever executing `.\Build.cmd` or `Build.cmd`**.
> `Build.cmd` triggers `build/FvsBuild` in production release mode, which creates git release tags and **OFFICIALLY PUBLISHES A NEW VERSION DIRECTLY TO GITHUB CLOUD** for public download worldwide.
> If an agent must test compilation, packaging, or NativeAOT publish, it **MUST ONLY USE `.\dev_build.cmd`** (local build only, passing `--dev`, never touches GitHub or publishes to the cloud).

## 2. The 9 North Star Architectural Invariants
All subsystems, controls, and rendering components across `src/` must strictly enforce these nine non-negotiable architectural pillars:

1. **Single Binary Executable Mandate:** Zero loose companion assets (`.gif`, `.png`, `.wav`, `.ico`) alongside the output binary. All UI overlays, guide indicators, brand icons, and animations must be generated dynamically via code or vector path geometry (`PathGeometry`) in memory.
2. **Absolute Authority for Time:** `src/FreeVideoStudio.Core/Media/OutputTimeline.cs` is the authoritative mathematical model for single-clip linear edits and per-clip segment durations. For multi-clip NLE virtual timelines, `src/FreeVideoStudio.Core/Media/CompositeTimeline.cs` and `MergeEdl.cs` constitute the sole mathematical authority mapping source µs, 60 fps merged frames, and output seconds across `edl://` libmpv preview playback and FFmpeg multi-clip rendering.
3. **Strict A/V Process Isolation:** Master preview volume (Windows OS PID session) and preview playback remain completely decoupled from FFmpeg export filtergraphs. Master preview slider adjustments must never alter export loudness.
4. **Leak-Free Render Pipelines:** The deprecated FFmpeg `zoompan` filter is banned suite-wide due to fatal native heap leaks. Dynamic zooms must be achieved via frame-evaluated padding, dynamic scaling, cropping, and contrast-adaptive sharpening (`cas=0.5`).
5. **Zero Raw Hex Styling:** All Avalonia styles, controls, and dynamic templates must resolve colors exclusively through named `DynamicResource` tokens in `AvaloniaApp.axaml`. Hardcoded hex values in shared styling are strictly prohibited.
6. **Thread-Bound Safety Contracts & GPU Preview Lock:** UI dispatchers must never block on native audio/video subsystem calls. WASAPI audio capture lifecycles run on an isolated serialized worker thread; SkiaSharp snapshot encoding and heavy image decodes execute off the UI thread.
   * ⚠️ **MANDATORY ARCHITECTURAL LOCK — libmpv PREVIEW ENGINE:** libmpv's Render API (`mpv_render_context_create`) does NOT support Direct3D 11 (`"d3d11"` returns `-19 / MPV_ERROR_NOT_IMPLEMENTED`). The `WGL_NV_DX_interop` bridge (rendering into OpenGL FBOs shared directly with Direct3D 11 textures in GPU VRAM) combined with non-blocking 0 ms 16-slot ring probing is the **frozen, permanently locked hardware architecture**. It provides zero-copy VRAM throughput, unlocked 120/240 FPS playback, and unobstructed Avalonia XAML overlays without Win32 HWND airspace occlusion. Attempting to bypass WGL with non-existent libmpv D3D11 APIs guarantees a pitch-black screen (audio-only), and re-introducing blocking KeyedMutex timeouts (e.g. 1,000 ms `AcquireSync`) reintroduces 1 FPS lock contention. Modifying this architecture or reintroducing blocking mutex waits is strictly prohibited.
7. **Monotonic Progress Guarantee:** Render progress tracking must be cost-weighted and mathematically monotonic (P(n+1) >= P(n)). Progress bars may never snap, stutter, or lerp backward across multi-pass operations.
8. **Every Rule That Can Be A Test Is A Test — AND A MACHINE RUNS THEM:** A specification paragraph only protects the codebase if the next person reads it. Where a rule can be mechanically asserted — one activation path per control, no raw hex in styling, no `zoompan`, no unexplained empty catch — it lives in `tests/FreeVideoStudio.App.Tests/ArchitectureRuleTests.cs` and the prose explains *why*. A sentinel proves a fix has not been deleted; a test proves it has not been broken. ⚠️ And neither proves anything until something runs them without being asked: `.github/workflows/ci.yml` (`SYS-CI`) is what makes the ratchets real. Before it existed the suite had been red on two genuine shipped bugs — the undo re-entrancy guard (`UNDO_23`) and the project fingerprint round-trip (`PROJ_10`) — for long enough that nobody looked, because five Windows-only tests were permanently red beside them.
9. **No Failure Is Silent:** Every caught exception is classified through `IFaultSink` as Recoverable, Degraded or Fatal (`08_APPLICATION_COMPOSITION.md` §2). `catch { }` and `catch (Exception ex) { Log(ex); }` are not error handling — they leave the user to guess whether they mis-clicked.

---

## 3. Domain Routing

Route by FILE (below) or by SYMBOL (`INDEX.md`). Full paths live in each spec's Code Mini-Map — basenames here are unique across `src/`.
`⚠` = CO-GOVERNED by more than one spec: read EVERY spec that lists it (`INDEX.md` §1 names them).

**[`01_TIMELINE_COORDINATE_MATH.md`](01_TIMELINE_COORDINATE_MATH.md)** — Timeline & coordinate math — 16:9->9:16 geometry, OutputTimeline chunk model, markers, freezes, cuts, zoom spans, meme anchors, hitboxes.

```
CanvasMath.cs  ⚠CompositeTimeline.cs  CoordinateMath.cs  ⚠GranularSpeedEditorWindow.axaml.cs
⚠GranularSpeedEditorWindow.Merge.cs  HudAutoDetector.cs  HudConfig.cs  HudImageOps.cs  KineticScrubController.cs
⚠MainWindow.axaml.cs  MainWindow.Canvas.cs  ⚠MainWindow.Controls.cs  MainWindow.Shortcuts.cs
MainWindow.Wireup.cs  ⚠MergeClipGraph.cs  ⚠MergedTimeline.cs  ⚠MergeEditorBridge.cs
⚠MergeEdl.cs  ⚠MergerPreviewPlan.cs  ⚠MergerSession.cs  ⚠MusicPadAlignment.cs
⚠MusicWizardWindow.axaml.cs  ⚠OutputTimeline.cs  ⚠PhoneFrameMockup.axaml.cs  TimelineKnob.cs
TimelineLanesControl.axaml.cs  TimelineViewModel.cs  ⚠VideoMergerWindow.EdlPreview.cs
⚠VideoMergerWindow.History.cs  ⚠VideoMergerWindow.Lanes.cs  ⚠VideoMergerWindow.Session.cs
⚠VideoMergerWindow.Timeline.cs  ⚠VideoMergerWindow.TimelineSelect.cs  ⚠VoiceOverWindow.axaml.cs
```

**[`02_AUDIO_ENGINE_MASTERING.md`](02_AUDIO_ENGINE_MASTERING.md)** — Audio engine & mastering — PID preview volume, LUFS targets, sidechain ducking, Voice Over Studio, WASAPI threading, music bed fades.

```
AudioFilterChain.cs  AudioLoudnessProbe.cs  ⚠FluidVolumeSlider.cs  ⚠MainWindow.axaml.cs
⚠MainWindow.Controls.cs  ⚠MemeLoudness.cs  MicLevelMonitor.cs  MpvIpcClient.cs
⚠MusicPadAlignment.cs  ⚠MusicWizardWindow.axaml.cs  VoiceOverPreviewPlayer.cs
⚠VoiceOverWindow.axaml.cs  VoiceRecorder.cs  WavAudioReader.cs  WaveformGenerator.cs
```

**[`03_FFMPEG_EXPORT_PIPELINE.md`](03_FFMPEG_EXPORT_PIPELINE.md)** — FFmpeg export pipeline — encoder discovery, zoom filtergraph, concat/bitrate, meme concat & cutaway preview, fades, progress, binary paths, meme library.

```
⚠CompositeTimeline.cs  EncoderManager.cs  ExportColorPolicy.cs  ExportTimingTag.cs
ExportViewModel.cs  FfmpegDiagnosticCollector.cs  ⚠FfmpegJobLifetime.cs  FramePtsProbe.cs
GpuCapabilityProbe.cs  GranularSpeedBuilder.cs  HardwareCapability.cs  HardwareScanner.cs  IntroTag.cs
MainWindow.SizeEstimate.cs  MemeAssets.cs  MemeCatalog.cs  ⚠MemeLoudness.cs  MemePreviewDirector.cs
MergeClipAnalyzer.cs  ⚠MergeClipGraph.cs  ⚠MergedTimeline.cs  ⚠MergeEditorBridge.cs
⚠MergeEdl.cs  ⚠MergerPreviewPlan.cs  MergerWorker.cs  MobileFilterBuilder.cs
⚠MusicPadAlignment.cs  OutputFileSize.cs  OutputSizeEstimator.cs  ProcessWorker.cs
QualityLadder.cs  TextOverlayGenerator.cs  TwoPassEncoding.cs  ⚠VideoMergerWindow.EdlPreview.cs
⚠VideoMergerWindow.Timeline.cs  ZoomPreviewSimulator.cs
```

**[`04_UI_UX_AVALONIA_SPEC.md`](04_UI_UX_AVALONIA_SPEC.md)** — UI/UX & Avalonia — design tokens, high-DPI layout, tooltips, confirmations, coach tours, granular editor layout, undo/redo, detachable previews, merger queue.

```
AmbientBubblesBackground.cs  AvaloniaApp.axaml  CoachOverlay.cs  ConfirmDialogWindow.axaml.cs
FloatingNotice.cs  ⚠FluidVolumeSlider.cs  GrabCursors.cs  ⚠GranularSpeedEditorWindow.axaml.cs
⚠GranularSpeedEditorWindow.History.cs  ⚠GranularSpeedEditorWindow.Merge.cs  ⚠IUserNotifier.cs
⚠LaneDiskCache.cs  MainViewModel.cs  ⚠MainWindow.axaml.cs  ⚠MainWindow.Controls.cs  MemePickerWindow.axaml
MemeWallControl.axaml  ⚠PhoneFrameMockup.axaml.cs  PreviewDetachController.cs  PreviewMonitorWindow.axaml
ProgressiveLanes.cs  SettingsWindow.axaml.cs  SpinningWheelSlider.cs  UpdateAvailableWindow.axaml.cs
⚠VideoMergerWindow.EdlPreview.cs  ⚠VideoMergerWindow.Lanes.cs  VideoMergerWindow.Playhead.cs
⚠VideoMergerWindow.Session.cs  ⚠VideoMergerWindow.TimelineSelect.cs  ViewModelBase.cs  WaveformPeaks.cs
⚠WindowBoundsHelper.cs  WindowResizeGrip.cs
```

**[`05_SYSTEM_LIFECYCLE_STORAGE.md`](05_SYSTEM_LIFECYCLE_STORAGE.md)** — System lifecycle & storage — mutexes, logging pipeline, window bounds, deferred-close contract, crash recovery, atomic writes, dev build harness & fix sentinels, signing.

```
AppDataPaths.cs  AppDataPathsTests.cs  LegacyAppDataNames.txt
⚠ApplicationPaths.cs  ⚠AtomicJsonFile.cs  ⚠Build.cmd  dev_build.cmd  ⚠CodeSigning.cs  CooperativeShutdownGate.cs
CoreLogger.cs  CrashLogDigest.cs  CropConfigDefaults.cs  CropConfigStore.cs  DeploymentLifecycle.cs
⚠dev.cmd  DiskSpaceGuard.cs  FvsBuild/Program.cs  ⚠GranularSpeedEditorWindow.axaml.cs
IpcProtocol.cs  ⚠LaneDiskCache.cs  LatestEstimateWorker.cs  ⚠MainWindow.ToolReturn.cs
MaskOverlayManager.cs  MergerAutosaveStore.cs  ⚠MergerSession.cs  NamedPipeStateClient.cs
NamedPipeStateServer.cs  ProjectRecoveryService.cs  ⚠RecoveryManager.cs  RuntimeLog.cs
SettingsManager.cs  SingleInstanceGuard.cs  StateTransferStore.cs  ⚠StorageProviderFilePicker.cs  ⚠ToolNavigator.cs
UiStateStore.cs  ⚠UpdateService.cs  ⚠VideoMergerWindow.Session.cs  ⚠WindowBoundsHelper.cs
```

**[`06_PROJECT_DOCUMENT_MODEL.md`](06_PROJECT_DOCUMENT_MODEL.md)** — Project document model — the saveable `.fvsproj`, schema versioning & the amputation rule, AOT-safe JSON, atomic persistence & backup, source integrity, recent projects, trim/AOT analyser policy.

```
AotJson.cs  ⚠AtomicJsonFile.cs  FreeVideoStudio.App.csproj  ⚠IProjectStore.cs
⚠MainWindow.Project.cs  ⚠MergeEdl.cs  ⚠OutputTimeline.cs  ⚠ProjectDocument.cs  ProjectSerializer.cs
⚠ProjectSession.cs  ProjectStore.cs  RecentProjects.cs  ⚠VideoMergerWindow.History.cs
```

**[`07_UNDO_AND_HISTORY.md`](07_UNDO_AND_HISTORY.md)** — Undo, redo & edit history — the four inherited rules (gesture coalescing, ceiling on push, redo invalidation, no-op rejection), immutable state contract, re-entrancy guard, persistence.

```
⚠GranularSpeedEditorWindow.axaml.cs  ⚠GranularSpeedEditorWindow.History.cs  ⚠MainWindow.Project.cs
⚠ProjectDocument.cs  ⚠ProjectSession.cs  UndoSidecarStore.cs  UndoStack.cs
```

**[`08_APPLICATION_COMPOSITION.md`](08_APPLICATION_COMPOSITION.md)** — Application composition, seams & fault reporting — the composition root, the service interfaces, fault tiers (Recoverable/Degraded/Fatal), and the architecture tests that enforce the other specs' rules.

```
AppServices.cs  ArchitectureRuleTests.cs  ⚠CodeSigning.cs  ⚠dev.cmd  Fault.cs  FaultCounters.cs
Faults.cs  ⚠FfmpegJobLifetime.cs  ⚠.github/workflows/ci.yml  IClock.cs  IFaultSink.cs
IFilePickerService.cs  ⚠IProjectStore.cs  ⚠IUserNotifier.cs  ⚠MainWindow.Project.cs
⚠MainWindow.ToolReturn.cs  ⚠ProjectSession.cs  ⚠StorageProviderFilePicker.cs  ⚠ToolNavigator.cs
UserFacingFaultSink.cs  WindowsOnlyFactAttribute.cs
```

**[`09_DISTRIBUTION_AND_RELEASE.md`](09_DISTRIBUTION_AND_RELEASE.md)** — Distribution, update size & repository weight — the 322MB installer, the runtime/app package split, the fingerprint that decides which one a patch downloads, LFS enforcement and the history-rewrite runbook.

```
⚠Build.cmd  dev_build.cmd  .gitattributes  ⚠.github/workflows/ci.yml  .github/workflows/lfs-guard.yml
RuntimePayloadManifest.cs  Staging.cs  ⚠UpdateService.cs
```

---
## 4. Agent Navigation & Entry Protocol
1. **Entry Rule:** Always read [`SPEC_GOVERNANCE.md`](SPEC_GOVERNANCE.md) before performing any code generation or inspection.
2. **Context Routing:** Know the FILE -> use §3 above. Know only a SYMBOL, CONSTANT or TAG (e.g. `SnapInsertionPoint`, `QuietBoostReductionFactor`, `ZOOMLIVE_07`) -> grep [`INDEX.md`](INDEX.md). Read the ONE spec you land on; do not pre-load the others.
3. **Co-Governed Files (⚠):** A file listed under more than one spec is bound by ALL of them. Reading one is NOT compliance — this is the exact leakage `SPEC_GOVERNANCE.md` §2 exists to prevent.
4. **Cite Anchors, Not Numbers:** Quote the stable `{#ANCHOR}` id (e.g. `FFM-BINPATH`) in the Proof-of-Read header. Section numbers shift as specs grow.
5. **Land The Sentinel With The Fix:** Any bug fix or safety invariant guarded mechanically gets a `TAG=path` line in `build/sentinels.txt` in the SAME change (checked by `build/FvsVerify`, which `dev.cmd`'s `VERIFY_PATCHES` runs, and by `ArchitectureRuleTests.EveryFixSentinelStillResolves` in CI). Distinguish mechanical fix-sentinels (`build/sentinels.txt`) from spec section anchors (`docs/INDEX.md` `{#ANCHOR}` tags): mechanical sentinels fail compilation/verification if their in-code token is deleted, while spec anchors route architectural requirements. That section also states why a correct source file is not evidence that the running binary contains the fix.

