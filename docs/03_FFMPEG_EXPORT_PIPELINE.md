# SPECIFICATION 03: FFMPEG EXPORT PIPELINE

## Code Mini-Map: Bound Source Files & Symbols

> **⚠ CO-GOVERNED rows are bound by EVERY spec listed on them.** Reading only this one is not compliance (`SPEC_GOVERNANCE.md` §2).
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `src/FreeVideoStudio.App/ViewModels/QualityLadder.cs` | `QualityLadder`, `Tier` | `Tiers`, `TargetMbFor`, `DefaultIndex`, `OriginalIndex`, `ColorFor` | The quality dial's tiers and the tier -> target-megabytes model. |
| `src/FreeVideoStudio.App/ViewModels/ExportViewModel.cs` | `ExportViewModel` | `QualitySliderValue`, `EstimatedFileSizeText`, `EstimatedFileSizeDescription`, `ResolveHardwareMode` | Quality selection and bound output-size readout. |
| `src/FreeVideoStudio.App/MainWindow.SizeEstimate.cs` | `MainWindow` | `CaptureSizeRequest`, `RequestSizeEstimate`, `MainWindow` | Immutable estimate inputs and UI publication. |
| `src/FreeVideoStudio.App/Services/OutputSizeEstimator.cs` | `OutputSizeEstimator` | `EstimateMainAsync`, `EstimateMergerAsync`, `CalculateMain`, `CalculateMerger`, `ReadMediaAsync` | Shared estimates and bounded media metadata cache. |
| `src/FreeVideoStudio.Core/Media/OutputFileSize.cs` | `OutputFileSize` | `FormatMegabytes`, `MergerConstantQuality`, `MergerTargetKbps`, `FromBitrate` | MB/GB/TB formatting and shared merger encoder settings. |
| `src/FreeVideoStudio.Core/Media/ProcessWorker.cs` | `ProcessWorker`, `VoiceOverTake` | `CancelledMessage`, `Cancel`, `RunAsync`, `Dispose` | Core FFmpeg rendering orchestrator, command builder, and progress monitor. |
| `src/FreeVideoStudio.Core/Media/GpuCapabilityProbe.cs` | `GpuCapabilityProbe` | `Probe`, `Result`, `IGpuCapabilityProbe`, `WindowsGpuCapabilityProbe` | PREVIEW GPU check only (D3D11 device, feature level, real adapter; logs RDP) — hardware vs CPU software preview. Does not test encoders. |
| `src/FreeVideoStudio.Core/Media/HardwareScanner.cs` | `HardwareScanner` | `ScanFailed`, `ScanSharedAsync`, `ScanAsync`, `HardwareScanner` | Boot encoder scan: one-frame test encode per encoder (NVIDIA → AMD → INTEL); result shared suite-wide. |
| `src/FreeVideoStudio.Core/Media/GranularSpeedBuilder.cs` | `GranularSpeedBuilder`, `ChunkSpec` | `Build`, `BuildAtempoChain`, `HighChunkCountWarnThreshold`, `SpliceFadeSec` | Filtergraph chunk splitter, setpts/atempo chain compiler, and freeze pad synthesis. |
| `src/FreeVideoStudio.Core/Media/AudioTempoFilterBuilder.cs` | `AudioTempoFilterBuilder`, `AudioTempoEngine` | `Build`, `Segment`, `EngineFor`, `BuildAtempoChain`, `EnsureProbedAsync`, `HelpListsRequiredOptions`, `RubberbandOptions` | TEMPO_01 — the one audio tempo policy for every export route (Rubber Band below 1.0x when verified, else atempo). |
| `src/FreeVideoStudio.Core/Media/CornerMemeOverlayGraph.cs` | `CornerMemeOverlayGraph`, `CornerMemeInput`, `CornerMemeOverlayResult` | `Build`, `InputArgs` | MEMEMODE_01 — corner-overlay memes over a running stream (Main App export + Merger effects clips), zero added duration. |
| `src/FreeVideoStudio.Core/Media/MobileFilterBuilder.cs` | `MobileFilterBuilder` | `Build`, `LayerSpec`, `MobileFilterBuilder` | 9:16 portrait video transform, background extrusion, and HUD positioning. |
| `src/FreeVideoStudio.Core/Media/ZoomPreviewSimulator.cs` | `ZoomPreviewSimulator`, `Result` | `Compute`, `ZoomPreviewSimulator`, `struct` | CPU/GPU live zoom simulation matching export filtergraph parity. |
| `src/FreeVideoStudio.Core/Media/AiTrajectorySmoother.cs` | `AiTrajectorySmoother`, `AiTrackingKeyframe`, `SmoothedTrajectory` | `SmoothTrajectory`, `ToJson`, `FromJson` | Universal AI tracking trajectory interpolation and dynamic zoom keyframe generation. **⚠ CO-GOVERNED BY: 01**|
| `src/FreeVideoStudio.App/Services/GeminiTrackingService.cs` | `GeminiTrackingService` | `TrackSubjectAsync`, `PostWithRetryAsync` | Gemini Vision model dispatch and inline MP4 tracking payload generation. **⚠ CO-GOVERNED BY: 01, 04, 05**|
| `src/FreeVideoStudio.App/Infrastructure/MemePreviewDirector.cs` | `MemePreviewDirector` | `IsActive`, `SetMemes`, `NotifySeek`, `Tick` | Live preview cutaway playback coordination and libmpv loadfile director. |
| `src/FreeVideoStudio.Core/Media/OutputFileNaming.cs` | `OutputFileNaming` | `Sanitize`, `NumberedFileName`, `MainDefaultBaseName`, `MergerDefaultBaseName`, `MainRecoveredPrefix`, `OUTNAME_01` | User-editable automatic export file names for both tools. |
| `src/FreeVideoStudio.Core/Media/MergerWorker.cs` | `MergerWorker` | `CancelledMessage`, `Cancel`, `RunAsync`, `Dispose` | Multi-clip concatenation, CFR resampling, and duration-weighted bitrate calculation. |
| `src/FreeVideoStudio.Core/Media/TextOverlayGenerator.cs` | `TextOverlayGenerator` | `WrapText`, `GeneratePng`, `TextOverlayGenerator` | High-DPI title text bitmap generation for top-void rendering. |
| `src/FreeVideoStudio.Core/Media/FfmpegDiagnosticCollector.cs` | `FfmpegDiagnosticCollector` | `AddStderrLine`, `GetDiagnosticLines`, `GetTailLines`, `ExplicitErrorCode` | Export failure classification and diagnostic report generation. |
| `src/FreeVideoStudio.Core/Media/EncoderManager.cs` | `EncoderManager` | `EncoderPreference`, `AvailableEncoders`, `PrimaryEncoder`, `GetInitialEncoder`, `GetFallbackList`, `GetCodecFlags`, `GetDecodeFlags`, `MaxBitrateKbps` | Export-time encoder list (`ffmpeg -encoders`), NVENC → AMF → QSV → libx264 fallback order, per-encoder rate-control flags. |
| `src/FreeVideoStudio.Core/Media/ExportEncoderStrategy.cs` | `ExportEncoderStrategy` | `Resolve` | Centralized suite-wide hardware encoder decision engine (Settings override → boot scan cache → export-time probe). |
| `src/FreeVideoStudio.Core/Media/TwoPassEncoding.cs` | `TwoPassEncoding` | `MasterCodecArgs`, `PassArgs`, `Cleanup` | libx264 two-pass size targeting (scratch master, pass 1/2 args), shared by both workers. |
| `src/FreeVideoStudio.Core/Media/FfmpegJobLifetime.cs` | `FfmpegJobLifetime` | `SetCurrentProcess`, `TakeCurrentProcess`, `PeekCurrentProcess`, `Cancel`, `DisposeJob`, `EmitFinished`, `FinishEmitted` | PIPELIFE_01 — one shared FFmpeg job lifetime (process gate, cancel, dispose, finish) for both workers. **⚠ CO-GOVERNED BY: 08**|
| `src/FreeVideoStudio.Core/Media/ExportColorPolicy.cs` | `ExportColorPolicy`, `VideoColorInfo` | `BuildConversionChain`, `OutputTagArgs`, `HdrToneMapChain`, `IsHdr`, `IsFullRange` | COLOR_01 — SDR BT.709 TV-range conversion and output colour tags. |
| `src/FreeVideoStudio.Core/Media/IntroTag.cs` | `IntroTag` | `Key`, `StandardIntroSec`, `OutputArgs`, `Read`, `Validate` | SCRAPER_01 — `fvs_intro_sec` tag and the muxer args that write both tags. |
| `src/FreeVideoStudio.Core/Media/ExportTimingTag.cs` | `ExportTimingTag`, `ExportTiming` | `Key`, `Format`, `TryParse`, `Read`, `SecToFrames` | TIMINGTAG_02 — frame-exact `fvs_timing` tag (intro, fade-in, fade-out). |
| `src/FreeVideoStudio.Core/Media/MergeClipAnalyzer.cs` | `MergeClipAnalyzer`, `MergeClipInfo` | `AnalyzeAsync`, `TryGetCompleted` | SCRAPER_03 — background per-file probe (duration, tags, audio, size), cached. |
| `src/FreeVideoStudio.Core/Media/FramePtsProbe.cs` | `FramePtsProbe` | `ProbeAsync`, `Parse`, `IntroCutUs` | FRAMESNAP_01 — real frame pts for the intro cut. |
| `src/FreeVideoStudio.Core/Media/MergedTimeline.cs` | `MergedTimeline`, `MergedClip`, `MergeClipSource` | `Build`, `Remap`, `ToMerged`, `TotalSec`, `Composite` | SCRAPER_02 — the Merger's merged clock. **⚠ CO-GOVERNED BY: 01**|
| `src/FreeVideoStudio.Core/Media/CompositeTimeline.cs` | `CompositeTimeline`, `CompositeClip` | `Build`, `TotalOutputSec`, `ClipsOutputSec`, `MergedSecToBodyOutputSec`, `MemeAtRelSec`, `MergeFps` | COMPOSITE_01 — the Merger's one time mapper (output length, frame counts, music by output time). **⚠ CO-GOVERNED BY: 01**|
| `src/FreeVideoStudio.Core/Media/MergeEdl.cs` | `MergeEdl`, `EdlClip`, `EdlEffects`, `EdlMeme`, `EdlMusic` | `Clips`, `BaseSpeed`, `ToJson`, `FromJson` | The Merger's edit list read by `MergerWorker.Edl`. **⚠ CO-GOVERNED BY: 01, 06**|
| `src/FreeVideoStudio.Core/Media/MergeClipGraph.cs` | `MergeClipGraph`, `MergeMemeInput`, `MergeClipGraphResult` | `Build` | MERGEGRAPH_01 — one Merger clip with granular effects and memes. **⚠ CO-GOVERNED BY: 01**|
| `src/FreeVideoStudio.Core/Media/MemeLoudness.cs` | `MemeLoudness` | `MeasureLufsAsync`, `GainFor`, `Chain`, `SpliceFade` | MEMELEVEL_02 — meme matched to the gameplay it interrupts (both apps); SPLICE_03 de-click fades. **⚠ CO-GOVERNED BY: 02**|
| `src/FreeVideoStudio.Core/Media/AudioGraphPruner.cs` | `AudioGraphPruner` | `Prune` | PREVIEWMIX_01 — the export graph's audio half, for the preview. **⚠ CO-GOVERNED BY: 02**|
| `src/FreeVideoStudio.Core/Media/MusicPadAlignment.cs` | `MusicPadAlignment` | `Align` | MUSICPAD_01 — shifts Main App music by the fade-in pad. **⚠ CO-GOVERNED BY: 01, 02**|
| `src/FreeVideoStudio.Core/Media/HardwareCapability.cs` | `HardwareCapability`, `HardwareCapabilityCache` | `Detect`, `LoadCached`, `Persist`, `SchemaVersion` (1) | Cached hardware acceleration capabilities profile and encoder feature levels. |
| `src/FreeVideoStudio.App/MemeCatalog.cs` | `MemeCatalog`, `MemeItem`, `MemeCategory` | `ScanAsync`, `SyncMemesAsync`, `SyncSongsFromCloudAsync`, `CloudMemeFolder`, `ExtensionsFor` | Meme folder scan/probe and the cloud delta-sync from the single repository `meme/` folder (MEMEFOLDER_01, MEMESYNC_01..03). |
| `src/FreeVideoStudio.App/Infrastructure/MemeAssets.cs` | `MemeAssets` | `StarterFiles`, `DeliverStarter`, `SongCategory`, `MemeCategoryFolder`, `DefaultsToStart` | First-run delivery of the shipped starter songs and memes (STARTER_01, STARTERLIST_01). |

---

## 1. Hardware Encoding & Gatekeeper  {#FFM-HWENC}
* **Unified Encoder Strategy (`ExportEncoderStrategy.Resolve`):** Every export surface in the suite (Main App and Video Merger) determines its encoder by calling `ExportEncoderStrategy.Resolve`. Precedence: (1) Explicit user setting override (Settings ▸ Performance: "NVIDIA", "AMD", "INTEL", "CPU"), (2) Suite-wide boot hardware scan result (`HardwareCapability.cs` via `HardwareScanner`), (3) Export-time fallback probe via `EncoderManager` (NVENC → AMF → QSV → `libx264`).
* **Probe Hierarchy:** `HardwareScanner` test-encodes one black frame with NVIDIA NVENC (`h264_nvenc`), AMD AMF (`h264_amf`) and Intel QSV (`h264_qsv`), in that order, and caches the first working hardware encoder in `HardwareCapability`. At export `EncoderManager` reads `ffmpeg -encoders` and validates codec flags. `GpuCapabilityProbe` does NOT test encoders: it only decides hardware vs CPU software PREVIEW (D3D11 device/feature level, real adapter) and logs RDP sessions.
* **Graceful CPU Fallback:** Missing drivers, unaccelerated GPUs, or VM/RDP sessions fall back to software CPU encoding (`libx264`). With a size target it runs two-pass (`TwoPassEncoding`); otherwise CRF 23/20/17 by quality level (presets veryfast/fast/medium).
* **RDP Detection & Registry Auto-Fix:**
  * When a Remote Desktop session is detected and `fEnableWddmDriver` is missing or 0 (`ExportViewModel.CheckRdpGpuBlocked`), the UI displays red indicator badges: `RDP SESSION` and `RDP: CPU BLOCKED`.
  * The "Auto-Fix" action (`ExportViewModel.AutoFixRdpGpuPolicyAsync`) runs an elevated (`runas`) PowerShell command that sets two DWORDs under `HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services`:
    ```text
    fEnableWddmDriver = 1
    fEnableAVC444ModeOnHWEncoder = 1
    ```
  * System requires a session reconnect to unlock NVENC/AMF over RDP.

---

## 2. Memory-Safe Dynamic Zoom Filtergraph  {#FFM-ZOOMGRAPH}
* **Absolute Ban on `zoompan`:** The FFmpeg `zoompan` filter is strictly banned suite-wide due to severe native heap leaks that crash long-form renders.
* **Filtergraph Implementation (`GranularSpeedBuilder`):** every zoom chunk first fits the frame to the zoom's OWN resolution (`{resW}x{resH}` = the zoom's `ZoomOrigRes`), then takes one of two paths:
  * **Constant zoom** (instant zoom, the held body of a slow zoom, freezes — `BuildConstantZoomFilter`): the visible region is computed directly (`CoordinateMath.SnapZoomWindow`), no per-frame scale:
    ```text
    scale={resW}:{resH}:force_original_aspect_ratio=decrease,pad={resW}:{resH}:(ow-iw)/2:(oh-ih)/2,
    pad={canvasW}:{canvasH}:{padX}:{padY}:black,
    crop={cropW}:{cropH}:{cropX}:{cropY},
    cas=0.5,
    scale={resW}:{resH}
    ```
  * **Slow ramp** (the ≤0.5 s glide in/out of a slow zoom): pad to a `2·resW × 2·resH` canvas, optional downscale when the working frame would exceed 100 Mpx (`MaxZoomWorkingPixels`), then `scale=w='iw*(z)':h='ih*(z)':eval=frame` → `crop` → `cas=0.5` → `scale={resW}:{resH}`, with z and the crop centre as time expressions.
  * **AI Smart Tracking Trajectory** (`SpeedSegment.AiTrackingTrajectory`): dynamic multi-keyframe trajectory compiled by `AiTrajectorySmoother`. Keyframes specify optical coordinates $(C_x, C_y)$ and dimensions $(C_w, C_h)$ at $0.5\,\text{s}$ sampled intervals. `GranularSpeedBuilder` compiles continuous piece-wise linear time expressions for scale and crop. Live preview parity is maintained bit-for-bit via `ZoomPreviewSimulator.Compute`. Banned `zoompan` filter remains strictly avoided.
* **Zoom Limit & Sharpening:**
  * Full zoom is measured against the zoom's own resolution:
    $$z_{\text{target}} = \min\left(\frac{\text{resW}}{\text{ZoomW}}, \frac{\text{resH}}{\text{ZoomH}}\right)$$
  * Mandatory AMD Contrast Adaptive Sharpening (`cas=0.5`) counteracts softness during upscale.
* **Chunk Normalization Invariant:** All video chunks are strictly normalized prior to concatenation:
  ```text
  fps={targetFps}:round=near:start_time=0
  ```

---

## 3. Concat Framing & Sizing  {#FFM-CONCAT}
* **CFR Normalization:** The Merger resamples every clip and meme to 60 fps CFR (`fps=60`) before concat. The Main App resamples speed chunks, intro and memes to `fps={targetFps}`; `ProcessWorker` sets `targetFps = "60"`, so both apps currently export 60 fps.
* **Merger speed — two routes:** a clip WITHOUT effects gets `setpts=PTS/{speed}` + the TEMPO_01 audio tempo filter (§3a FFM-TEMPO); a clip WITH effects goes through the granular engine (`MergeClipGraph` → `GranularSpeedBuilder.Build`, MERGEGRAPH_01).
* **Merger Bitrate (100%):**
  * Target video bitrate (`-b:v`) is the duration-weighted average of all input streams, clamped to 800–100 000 kbps (`OutputFileSize.MergerTargetKbps`):
    $$\text{Bitrate}_{\text{target}} = \frac{\sum (D_i \times B_i)}{\sum D_i}$$
  * `-maxrate` = max(target, peak clip bitrate capped at 100 000); `-bufsize` = 2 × maxrate (capped at 100 000).
  * Below 100% see MERGEQUALITY_01 (§8a).
* **Audio Edge Fades (SPLICE_01 / SPLICE_02):** Injects an audio edge fade at each end of every concatenated chunk to eliminate acoustic transients:
  ```text
  afade=t=in:st=0:d={fade},afade=t=out:st={dur-fade}:d={fade}
  ```
  * Nominal fade is $8\text{ms}$ (`SpliceFadeSec = 0.008`) — under half a frame at 60fps.
  * **Capped at 2% of the chunk at each end:** $\text{fade} = \min(0.008,\ D_{\text{chunk}} / 50)$. 8ms was sized for joins about a second apart; with `SegGapMs = 0` blocks can touch, and a run of short blocks put 16ms of ramp on a 200ms chunk — 8% of it, heard as a gargle rather than a de-click. Chunks of 400ms and up are unaffected.
  * Chunks shorter than $3 \times$ the nominal fade get no fade at all — too brief for a click to register.

---

## 3a. Audio Tempo Policy (TEMPO_01)  {#FFM-TEMPO}
* **One builder, every route.** `AudioTempoFilterBuilder` is the ONLY place a tempo filter is spelled. Main App base speed (`ProcessWorker`), every granular chunk (`GranularSpeedBuilder.Build`), Merger plain clips (`MergerWorker`) and Merger effects clips (`MergeClipGraph` → `GranularSpeedBuilder.Build`) all call `AudioTempoFilterBuilder.Segment`. The rendered preview mix (PREVIEWMIX_01) runs the same `ProcessWorker`, so it hears the same engine. `GranularSpeedBuilder.BuildAtempoChain` is now a forwarder to the atempo-only fallback, kept for the gates that check atempo's element bounds. `AudioTempoFilterBuilderTests.NoExportRouteSpellsATempoFilterItself` enforces this.
* **Policy:**
  | Rate | Filter |
  | :--- | :--- |
  | 1.0x | none (AVSYNC_01) |
  | < 1.0x, capability present | `rubberband=tempo={s:F4}:transients=mixed` |
  | < 1.0x, capability absent or unknown | the atempo chain (0.5 steps, then the remainder) |
  | > 1.0x | the atempo chain, unchanged (2.0 steps, then the remainder) |
  Impossible rates (≤ 0, NaN, ∞) fall back to 1.0x, loudly (ISSUE_04); others are clamped to 0.01–100 (both filters' range).
* **Capability probe (once per FFmpeg binary, cached for the process).** `EnsureProbedAsync(_ffmpegPath)` runs at the start of `ProcessWorker.RunAsync` and `MergerWorker.RunAsync`, before any graph is built: (1) `ffmpeg -hide_banner -h filter=rubberband` must list `tempo`, `transients` and `mixed` — the exact syntax emitted; (2) a 0.25 s 440 Hz tone must pass through `rubberband=tempo=0.5000:transients=mixed` with exit code 0. Any failure, timeout or exception = ABSENT (atempo). Never probed (unit tests, gates) = ABSENT, so an unprobed graph is the old, always-valid one. The bundled build (`N-119166-g1e5c65f539-20250408`, `--enable-librubberband`) carries the option table: tempo, pitch, transients (crisp/mixed/smooth), detector, phase, window, smoothing, formant, pitchq, channels.
* **Why `transients=mixed` (measured on a reference BtbN build with an identical option table; 48 kHz).** Steady 440 Hz tone, zero-crossing pitch / off-band floor: default `crisp` 437.4 Hz / -12 dB at 0.5x (0.3–0.6 % flat at every slowdown) — rejected; `mixed` 440.00 Hz at 0.25/0.5/0.75x; `smooth` 440.00 Hz / -56 dB but a percussive hit's 10–90 % rise grew to 59 ms at 0.25x (crisp/mixed 10–11 ms). Game audio is mostly transients, so `mixed`.
* **Duration and splices are owned by the callers, after the tempo filter.** Granular chunks: tempo → `apad,atrim=duration={quantised}` → SPLICE_01 fade. Merger plain clips: tempo → clip loudness → CLIPFRAMES_01 `apad,atrim=end=` → SPLICE_03 fade. Measured unbounded output: rubberband exact at 0.25–4x; atempo up to 1 % short (0.25x: 15.84 s of 16 s). Graph `finalDuration` is identical with either engine.
* **Timing (measured, not compensated).** Energy-centroid offset of a 1 kHz burst vs prediction: rubberband `mixed` within ±20 ms with ±0.6 ms spread across hits; atempo -10…-50 ms (0.75x…0.25x) with up to ±11 ms spread. No compensation filter is added.
* **Speed-ups keep atempo:** no measurement justified a change (TEMPO_01 policy).
* **Not in this policy:** the live mpv preview players use mpv's own `speed`; music is never tempo-changed (MUSICSYNC_02).
* **GPU route:** `rubberband` is in `ExportVideoPipeline`'s CPU audio-filter table, so a slowdown does not push the video off the resident CUDA route.

---

## 4. Meme Concat Architecture  {#FFM-MEMECONCAT}
* **Aspect Normalization:** Memes are never cropped. Each is fitted inside the OUTPUT canvas and padded with black (`scale={canvas}:force_original_aspect_ratio=decrease,pad={canvas}:(ow-iw)/2:(oh-ih)/2`). Main App canvas: 1080 x 1920 in portrait, else the source resolution rounded down to even; Merger: 1080 x 1920 or 1920 x 1080.
* **Even Dimensions:** Main App meme frames then pass `scale=w=floor(iw/2)*2:h=floor(ih/2)*2,format=yuv420p,setsar=1`.
* **Meme Loudness (MEMELEVEL_02):** A meme with sound gets gain = measured gameplay − measured meme (clamped -24…+12 dB; either unmeasured → as recorded), no per-meme limiter (the always-on safety limiter covers the mix), and 8 ms de-click fades on both sides of the splice (SPLICE_03). Same in the Main App and the Merger — see `02_AUDIO_ENGINE_MASTERING.md` AUD-MASTERING.
* **Silent Meme Audio Synthesis:** Memes lacking audio streams inject synthesized silence (`anullsrc` at 48kHz stereo) for the exact duration of the meme.
* **Still Image Looping:** Static image memes (`.png`, `.jpg`, `.jpeg`) are looped at the export frame rate:
  ```cmd
  -loop 1 -framerate {targetFps} -t 4.0 -i "{memePath}"
  ```

---

## 4a. Corner Overlay Memes (MEMEMODE_01)  {#FFM-MEMECORNER}
* **Full screen = the existing splice/concat path (§4). Corner = an `overlay` over the running stream.** `ProcessWorker` resolves every placement and routes corner overlays into `cornerMemes`; `memes` (and so the meme cuts, `MemeTimeInsertedBefore` for music and voice-over, the end-pad rule, the timing tag and the preview-mix map) holds ONLY full-screen memes, so a corner meme can never shift anything.
* **Where:** Main App — on `[v_render_out]` (intro + body, final canvas: 1080×1920 portrait, else the even source size) and the matching audio, BEFORE full-screen memes are spliced in. Merger — on each effects clip's body (`[c{i}_body_v]`/`[c{i}_body_a]`), before its full-screen memes (`MergeClipGraph` step 3b, canvas = the merge canvas).
* **Graph (`CornerMemeOverlayGraph.Build`), per meme with visible interval [S,E):**
  ```text
  [m:v]trim=duration={E-S},setpts=PTS-STARTPTS,fps={fps}:round=near,
       scale={B}:{B}:force_original_aspect_ratio=decrease,scale=w=floor(iw/2)*2:h=floor(ih/2)*2,setsar=1,
       format=yuva420p,setpts=PTS+S/TB[ov]
  [main][ov]overlay=x={X}:y={Y}:eof_action=pass:enable='between(t,S,E)'
  sound on:  [m:a]atrim=duration={E-S},…,{MEMELEVEL_02 gain}{SPLICE_03 fades},adelay=delays={S·1000}:all=1
             [main_a][m_a…]amix=inputs=n+1:normalize=0:duration=first:dropout_transition=0
  ```
  B = round(min(W,H)·{0.30 | 0.42 | 0.55}) (Small/Medium/Large, even), margin = round(min(W,H)·0.03); X/Y = margin or `W-w-margin` / `H-h-margin` by corner (`MemeOverlayLayout`, shared with every preview).
* **Zero added duration, by construction:** the stream is the main input of every `overlay` (`eof_action=pass`) and the first input of the `amix` (`duration=first`). Harness (reference FFmpeg): 10 s main + 4 s meme at 3–7 s → 10.000 s, 600 frames; meme pixels only inside the window.
* **Inputs** follow the full-screen meme inputs (images `-loop 1 -framerate F -t D`); the preview-mix render (PREVIEWMIX_01) opens the same slots, so the corner meme's sound is heard in the preview mix.
* **Sound off** (`PlaySound=false`): no audio chain (a full-screen meme with sound off gets silence of its length).

---

## 5. Live Meme Cutaway Preview (`MemePreviewDirector`)  {#FFM-MEMEPREVIEW}
* **Media Swap Lifecycle:**
  1. Forward playback crosses `AtSourceSecRelative`: libmpv issues `loadfile` to swap to meme media.
  2. Plays meme media until its duration expires.
  3. libmpv issues `loadfile` restoring gameplay footage paused at the exact anchor frame.
* **Player Loop Guard:** UI tick loop skips execution while `MemePreviewDirector.IsActive` is `true`.
* **Full screen only (MEMEMODE_01):** `SetMemes` ignores corner overlays. Those are drawn by `CornerMemeOverlayPresenter` over the video host on the host's GAMEPLAY clock — never a pause, seek or `loadfile` — and hidden while a cutaway is active (04 UI-MEMESELECT).
* **Directional Trigger:** Scrubbing over an anchor frame holds gameplay still; cutaways trigger on forward playback only.
* **Companion Audio Muting (MEME_07):** The meme carries its OWN sound. While `IsActive` is `true`, the background music bed and EVERY voice-over take PAUSE, and resume the moment gameplay returns. This follows structurally from the host tick early-returning — but it is a hard requirement, not a side effect: without it the game audio, the meme audio, the voice-over and the music all play simultaneously in preview.
* **Global Property Save/Restore:** Four mpv globals survive `loadfile` and are saved and restored around every cutaway: `speed` (forced to `1.0` — a meme inside a 2x block would otherwise preview at 2x and export at 1x), `video-crop` and `vf` (a zoom crop belongs to the gameplay; the export splices the meme UNCROPPED), and `image-display-duration` (a still meme would otherwise flash past in mpv's default 1s instead of holding its 4s).
* **Visual Overlays:**
  * Displays `MemeSwapOverlay` during asynchronous file swaps.
  * Blocks the window with `MemeRebuildOverlay` (220ms settle timeout) during timeline rebuilds to prevent visual flicker.

---

## 6. Fades & Intros  {#FFM-FADES}
* **Fade Pads:** With fades on, the export takes extra footage before MARK START / after MARK END: pad = min(1.0 s, available footage ÷ speed) in output seconds; a pad under 0.5 s becomes 0 (no fade on that edge). Source footage used = pad × speed.
* **Fade Length = The Pad:** `fade`/`afade` in and out last exactly the pad (`padStartHumanSec` / `padEndHumanSec`), not pad × speed.
* **MUSICPAD_01:** the music is shifted by the lead pad (`MusicPadAlignment.Align`) so it lands where the preview showed it — see `02_AUDIO_ENGINE_MASTERING.md` MUSICPAD_01.
* **Thumbnail Blackout Prevention:** To prevent pitch-black cover frames (e.g. after a fade-in), every Main App export starts with a still intro (`IntroStillSec`, 0.1 s unless a custom thumbnail sets another length) placed in front of the faded body. It is a SEPARATE input (the source seeked to the thumbnail frame, else MARK START), whose first frame is looped (`select='eq(n\,0)',loop=…`) and `concat`enated before the body, with matching silence on the audio.
* **Meme Fade Rules:** Leading and trailing memes receive output tail fades; middle memes receive zero fades.

---

## 7. Monotonic Progress Tracking  {#FFM-PROGRESS}
Render progress tracking is cost-weighted across sequential phases and must be mathematically monotonic (P_n+1 >= P_n):

```
[Phase 2: Video & Audio Encoding] ---> [Phase 3: Mux & Thumb]
           (0% - 96%)                       (96% - 100%)
```

* **Phase Allocation:**
  * **Phase 1 (Audio Peak Analysis):** a quick measurement of the exported range (only when the peak tamer is on or a meme has sound; cached), labelled "Analyzing Audio Peaks"; encoding still starts at 0%. No normalization pass exists (LOUDSTD_REMOVED_01).
  * **Phase 2 (Video & Filtergraph Encoding):** 0% - 96% (cost-weighted for dynamic zoom, CAS sharpening, speed segments, and mobile crops)
  * **Phase 3 (Muxing & Thumbnail Generation):** 96% - 100%
* **Monotonic Invariant:**
  $$P_{n+1} = \max(P_n, P_{\text{calculated}})$$
  Progress bars are strictly prevented from snapping or lerping backward across multi-pass operations. UI progress values lerp smoothly on ~ 33ms intervals.

---

## 8. Phase 3 Thumbnail Strip Temporal Padding  {#FFM-THUMBSTRIP}
* **Main path (`ThumbnailStripGenerator`, STRIP_01):** when there are at least 2 s of video per thumbnail, the file is opened N times, each input keyframe-seeked (`-noaccurate_seek -ss`) to its sample, one frame taken from each (`trim=end_frame=1`) and the frames joined with `hstack`. No `tpad` is involved.
* **Sweep (short ranges and the fallback when the seeked path fails):** one range is decoded and sampled; if the last sample falls past the real end, `tpad` clones the final frame so the strip has no black gap. `tpad` comes AFTER the `fps` sampler and lasts 1 s:
  ```text
  fps=fps={N/dur}:round=up,scale=-1:60,tpad=stop_mode=clone:stop_duration=1,tile=Nx1
  ```
  `StreamAsync` (frame-by-frame lanes) uses the same `fps → scale → tpad=…:stop_duration=1` order.
* **Scope:** Music Wizard phase 3, Granular Speed Editor, Voice Over and the Video Merger lanes. Distinct from the 0.1s still intro in §6 (FFM-FADES), which exists to prevent black COVER frames.

---

## 8a. The Quality Dial Asks For Quality  {#FFM-QUALITY}
**The dial's value is a QUALITY TIER. The file size is computed from it and displayed. Never the reverse.**

* **The defect this replaced.** The dial's value WAS the file size (`targetMb = 5 + idx x 5`, 5-100MB). The app then derived the bits-per-pixel that size bought and reported what it had turned out to be — `Blurry-`, `Sharp+`. The one thing a user cares about was an OUTPUT of the control, so reaching a wanted quality meant guessing a size, reading the verdict, and guessing again.
* **Why one guess was never enough.** Bits-per-pixel depends on DURATION. The same 40MB is "Lifelike" on a ten-second clip and "Pixelated" on a three-minute one, so the dial position meant something different in every project and the guessing restarted with each clip. Tiers are duration-independent by construction: "Sharp" is the same Sharp at 10 seconds and at 3 minutes — only the predicted megabytes move.
* **The ladder (18 stops, worst to best):** Pixelated · Blurry · Low · Okay · Good− · Good · Good+ · Sharp− · Sharp · Sharp+ · High− · High · High+ · Ultra− · Ultra · Ultra+ · Premium HQ · **Original**.
  Steps are geometric at roughly 15-20% of bits-per-pixel each, because perceived quality tracks bitrate logarithmically — equal absolute steps would feel enormous at the bottom and identical at the top. The `−`/`+` stops are REAL positions the user selects, not suffixes computed from where a size happened to land.
* **`Original` has no size target at all.** `TargetMbFor` returns null, which signals constant-quality export. The size readout may show a rough prediction from source bitrate, dimensions, frame rate, output duration and audio; that prediction MUST NEVER become an encoder size cap.
* **The maths is the old maths, inverted term for term** — including the 1.5 landscape divisor and the 60fps basis:
  $$\text{videoKbps} = \text{bpp} \times k \times \frac{W \times H \times 60}{1000}, \qquad k = \begin{cases} 1.0 & \text{portrait} \\ 1.5 & \text{landscape} \end{cases}$$
  $$\text{targetMB} = \frac{(\text{videoKbps} \times t_{\text{billable}}) + (\text{audioKbps} \times t_{\text{sec}})}{8192}$$
  ⚠️ The 1.5 is **not** cosmetic: the old forward pass divided landscape's bits-per-pixel by it before naming the result, so landscape must carry 1.5x the bitrate to earn the same word. Dropping it silently re-grades every landscape export by two or three tiers.
  ⚠️ Audio follows the old rule in the old order: assume 192 kbps, fall back to 64 kbps only if the resulting file would be too small to afford it.
  ⚠️ VIDEO is billed on $t_{\text{billable}}$ (freeze-discounted, below); AUDIO is billed on the FULL $t_{\text{sec}}$. A frozen picture still has a soundtrack running under it.
* **The export contract did not change.** `ProcessWorker` receives "target megabytes, or null for constant quality". `OutputSizeEstimator.CalculateMain` supplies both the readout and the target. Export captures fresh inputs and awaits this calculation, rather than using the last asynchronous UI result. Do NOT pass a tier index into the encoder.
* **Default is `Sharp`,** not a megabyte figure. A size default produces a different quality for every clip length, which is the whole defect. The default tier lives in Settings as **Default Video Quality** (`DefaultValues.QualityIndex`, an index into the ladder) and a NEW project always starts there — never on whatever the previous project happened to use.

### The Worker's Quality Level Is Not The Tier Index (QUALITY_02) — NON-NEGOTIABLE
`VideoConfig.GetQualitySettings` (Core, deliberately unchanged) branches on `q >= 20`:

| `q` | `keepHighestRes` | `targetMB` |
| :--- | :--- | :--- |
| `>= 20` | **true** | the override — `null` means constant quality |
| `< 20` | false | override ?? `5 + q * 5` |

The old dial was 0-20, so its top stop WAS 20 and fell into the first branch by construction. The tier ladder tops out at **17**, which lands in the SECOND branch — so passing the raw tier index strips `Original` of `keepHighestRes` AND, if the override were ever null, caps it at `5 + 17 * 5 = 90 MB`. **The one tier whose entire promise is "no limit, best possible" becomes the most limited stop on the dial.**

`QualityLadder.ToWorkerQualityLevel` maps `Original -> 20` at the App boundary. Mapping here rather than moving the Core threshold keeps the worker's contract intact — it is shared with the Video Merger — and puts the translation at the one layer that knows tiers exist.

### A Frozen Second Is Not A Normal Second (QUALITY_03)
A freeze holds ONE still picture; every frame after the first is a near-empty P-frame. Counted as full seconds, a 3-second freeze inflated the estimate by 3 seconds of motion footage that will never be encoded, and the export aimed at a size it did not need.

$$t_{\text{billable}} = (t_{\text{sec}} - t_{\text{freeze}}) + (t_{\text{freeze}} \times 0.15)$$

⚠️ **ONLY FULLY SAFE ON CPU (TWO-PASS VBR).** With a size target, `libx264` runs an analysis pass and allocates bits by complexity, so a smaller target does not starve the moving footage — the freeze simply stops being paid for. A GPU encoder (NVENC/AMF/QSV) with a size target is single-pass CBR: there the discount takes bits AWAY from the motion. The discount is currently applied on both routes.
⚠️ Size estimates derive duration and held-frame seconds from the SAME `OutputTimeline`: total output seconds and the sum of its freeze chunks. The older `TimelineViewModel` duration walks must not drive output-size estimates: they treated freezes as replacements and ignored cuts when speed segments existed.

### No Marks Set Means The Whole Video (QUALITY_05)
Before MARK START or MARK END is pressed, `TrimEndMs` is 0 and the older duration calculation hit its 1 ms floor, so the estimate read as nothing on a freshly loaded clip. `CaptureSizeRequest` resolves an unmarked start to 0 and an unmarked end to the known video duration. If that duration is still unavailable, `OutputSizeEstimator.CalculateMain` resolves the end from source metadata. Each explicitly marked boundary remains in effect.

⚠️ **READ-ONLY FALLBACK.** `EnsureTrimPointsSet` applies the same rule but MUTATES — it stamps `IsTrimStartSet` / `IsTrimEndSet` true, which changes what the marker buttons and the export do next. A passive size calculation that silently marked a clip as trimmed would be a far worse defect than the blank label it fixes. Resolve the two numbers locally; write nothing.

With **no video loaded at all**, the size readout shows an em dash, never a zero-byte estimate.

### Live size estimates in Main App and Video Merger (SIZEESTIMATE_01) {#FFM-SIZEESTIMATE}
* Main estimates include trim bounds, base speed, granular speeds, cuts, freeze insertions, the 0.1s intro and meme durations. Explicit meme placements take precedence over the legacy start/end meme, matching export. Portrait mode and quality use the existing quality ladder.
* Main's label binds to `Export.EstimatedFileSizeText` with a matching descriptive tooltip. Both apps format approximate sizes as MB, GB or TB. Unknown or incomplete media details show an em dash, never a misleading partial total.
* Main's first estimate uses known timeline inputs. Merger can reuse metadata from the last completed queue estimate. A background pass checks cached file identity and probes missing/changed media, then refines the number. Normal edits reuse metadata. No trial encode is started during editing.
* Opening a video initializes its known duration without marking trim points. On recovery, if player duration is not ready and no end is marked, the background estimate resolves the end from probed source metadata without changing the editor's trim selections.
* Merger estimate = the 100% bitrate (duration-weighted video bitrate, SAME clamp as `MergerWorker`) × the quality ratio (MERGEQUALITY_01; 1.0 at 100%, floor 300 kbps below it).
* **MERGEQUALITY_01 — below 100% is always smaller (P11).** The old path encoded below 100% with an UNCAPPED constant quality (CQ 16–35); on high-motion gameplay CQ 16–17 out-spent the 100% bitrate, so 95% produced a bigger file than 100%. Now below 100% the Merger encodes single-pass VBR at `MergerTargetKbps(avg) × MergerQualityRatio(p)` (ratio = 2^((15−CQ(p))/6): 0.79 at 95%, 0.28 at 50%, 0.10 at 5%), maxrate ≤ min(100% peak, 2× target); the estimate uses the same number. CQ remains only when the source bitrate is unknown. Harness (2 × 3 s 1080p60 noise clips, 16.9 MB): 100% 17.1 MB, 95% 13.9 MB, 50% 4.8 MB.
* **MERGESIZE_01 — the Merger estimate uses the edit list's length.** When the captured `MergeEdl` describes the queue, `MergerSizeRequest.OutputSeconds` = `CompositeTimeline.Build(edl).TotalOutputSec` (base speed, granular speeds, freezes, memes, cuts, removed intros, custom-thumbnail still) and replaces `files ÷ speed`; TOTAL LENGTH shows the same number. Every changed capture re-requests the estimate (deduped).
* Audio counts once as the exported soundtrack. Merger always writes 192 kbps AAC; mixing in music does not add the original music files' bytes. Every Merger estimate (and the Main App's `Original` prediction) includes 1% container overhead. These predictions cannot guarantee a final size without encoding the full content.
* Worker lifetime and stale-result guarantees are specified in `05_SYSTEM_LIFECYCLE_STORAGE.md#SYS-SIZEESTIMATE`.

---

## 8b. Export Is Single-Flight, And Its Child Processes Are Owned  {#FFM-EXPORTLIFETIME}
* **`EXPORTSESSION_01` — the PROCESS button is not a lock.** Export previously had exactly one mutual
  exclusion mechanism — `processButton.IsEnabled` — and the overlay's `CancelRequested` handler
  defeated it by re-enabling the button and dismissing the overlay **in the same breath as** signalling
  cancellation, while FFmpeg was still being killed (`ReadExitCodeSafely` alone grants 5 s of grace
  plus 2 more) and a multi-gigabyte job temp directory was still being deleted. Three consequences:
  two FFmpeg pipelines ran concurrently; the second export disposed the `CancellationTokenSource` the
  first worker still held registrations on; and both pipelines resolved the same output filename.
  * Cancel is now a **state transition**, not a UI reset: it signals the token and shows
    `CANCELLING...`. The overlay is dismissed and the button re-armed in exactly one place — the
    `finally` of `MainWindow.ProcessVideoAsync`, after the pipeline `Task` has completed.
  * `ProcessVideoAsync` is a thin single-flight wrapper guarded by `_exportRunning`; all pipeline
    content lives in `ProcessVideoCoreAsync`. **The wrapper creates the `CancellationTokenSource` and
    is the only code permitted to dispose it**, and only after the work `Task` has completed.
  * `MainWindow.OnClosing` waits (bounded, 3 s) on `_exportInFlight` before teardown.
* **`CANCELREG_01` — `RunAsync`'s cancellation registration lives INSIDE its `try`.** Taken outside it,
  an `ObjectDisposedException` from a already-disposed source escaped `RunAsync` without ever reaching
  `EmitFinished`, so the controller's `TaskCompletionSource` never completed and the awaiting UI hung
  forever with the overlay already gone. `RunAsync`'s `finally` additionally asserts that
  `EmitFinished` has fired, reporting a failure rather than allowing any silent return to wedge a caller.
* **`OUTPATH_01` — `ResolveOutputPath` RESERVES, it does not test.** A `File.Exists` scan is a TOCTOU:
  two pipelines both saw the same index free and the later `File.Move(..., overwrite: true)` silently
  destroyed the earlier render. The name is now claimed with `FileMode.CreateNew` + `FileShare.None`
  (an atomic filesystem-level create-or-fail), bounded at 10,000 attempts; the zero-byte placeholder is
  overwritten by the pipeline's own move, and removed if that move fails.
* **`WORKERLIFETIME_01` / `WORKERLIFETIME_02` — `ProcessWorker` is `IDisposable` and MUST be disposed.**
  `MainMediaController` let every instance fall out of scope, which made `ISSUE_11`'s kill-the-FFmpeg-tree
  backstop unreachable code — the exact orphan it describes (fans at full tilt, pegged CPU, nothing on
  screen). The worker is now `using`-scoped as the outermost scope so disposal happens strictly after
  `await tcs.Task`, the `ct.Register` handle is `using`-scoped instead of discarded, the
  `TaskCompletionSource` is created `RunContinuationsAsynchronously`, and a fault continuation on
  `RunAsync` converts any escape into a reported failure instead of a hang.
* **`PROCGATE_01` / `PROCGATE_02` — the live child process is handed between threads under a gate.**
  `_currentProcess` was a non-volatile field (its two neighbours were already `volatile`) tested for
  null and then `Kill`ed as two separate reads, so a cancel could land in the window where the export
  thread had nulled and disposed it — `ObjectDisposedException`, swallowed, cancel silently lost, after
  the log had already announced *"Terminating FFmpeg process tree."* All access is now through
  `SetCurrentProcess` / `TakeCurrentProcess` / `PeekCurrentProcess` under `_procGate`, held for a
  reference copy only and never across a `Kill`, a `Dispose` or any I/O. `TakeCurrentProcess` claims the
  reference and clears the slot atomically, so exactly one caller can ever dispose a given `Process`.
  The **thumbnail grab** — previously the one child process published nowhere, registered against no
  token and unknown to `ChildProcessTracker` — is now wired like every other.
* **`PIPEDRAIN_01` — drain before dispose, on every path including cancellation.** The two-pass tail
  returned early on `OperationCanceledException` straight into a `finally` that disposed the `Process`,
  closing the `StandardOutput`/`StandardError` pipe handles while both reader tasks were still inside
  `ReadLineAsync`. Both faulted unobserved and the anonymous pipe pair survived to finalization. Both
  readers are now awaited (bounded at 5 s) before the process is disposed, exactly as the main encode
  loop already did.

---

## 8c. Automatic Output File Names  {#FFM-OUTNAME}
* **`OUTNAME_01` — one editable base name per tool.** `ProcessWorker.OutputBaseName` and
  `MergerWorker.OutputBaseName` come from `AppSettings.MainOutputBaseName` (default
  `FreeVideoStudio`) and `AppSettings.MergerOutputBaseName` (default `Merged-Videos`), edited in
  Settings › Output Files (`04` UI-SETTINGS-ABOUT). The finished file is
  `OutputFileNaming.NumberedFileName(base, N)` = `<base>-<N>.mp4`, N = first free index from 1.
  The Main App still RESERVES the name (OUTPATH_01); the Merger keeps its existence scan.
* **The base name is user input in a path.** Both workers re-run `OutputFileNaming.Sanitize`
  (defence in depth): invalid file-name characters and separators become `_`, control characters
  are dropped, leading spaces/dots and trailing dots/spaces/dashes/underscores are trimmed,
  reserved device names (`CON`, `NUL`, `COM1`…) and empty results fall back to the default, and
  the length is clamped to 80. A bad setting can never fail an export or leave its folder.
* **Rescued renders keep fixed prefixes** (`FreeVideoStudio-RECOVERED-`, `Merged-Videos-RECOVERED-`,
  RESCUE_01) so the user and the crash digest can always tell which tool produced them.

---

## 9. Production Binary Discovery Hierarchy  {#FFM-BINPATH}
* **Strict Search Order:** `BinaryPathResolver.Resolve(name, "backend", "binaries")` (used by `ProcessWorker`, `MergerWorker`, `EncoderManager` and the App) checks, from the exe's folder (`Environment.ProcessPath`, else `AppContext.BaseDirectory`), and stops at the first hit:
  1. `backend\` — **ALWAYS PROBED FIRST.**
  2. `..\..\..\..\..\backend\`
  3. `binaries\`, then `..\..\..\..\..\binaries\`
  4. Nothing found → the bare name (`ffmpeg.exe`) is returned, so Windows searches the system PATH. ⚠ RISK: this can bind a system FFmpeg of unknown version — exactly what the next bullet forbids.
* **Why The Order Is Non-Negotiable:** The production MSI installs the binaries into `backend\` beside the executable. Probing anything else first lets a production build either fail to locate its executables or silently bind a system-wide FFmpeg of unknown version and build flags — a filtergraph this suite depends on (`cas`, `acrossover`, `sidechaincompress`) may not exist in that binary.
* **Dev/Prod Parity:** Because MSBuild mirrors the installer's `backend\` layout into `bin\Debug`, the identical lookup satisfies both environments with no conditional compilation.
* **`ffplay.exe` Is Not Copied:** It is unused — music preview runs through an isolated `MpvIpcClient`.

---

## 10. Centralized Meme Library, Probe & Cloud Delta Sync  {#FFM-MEMELIB}
* **Default Asset Directory (MEMEFOLDER_02):**
  ```text
  %USERPROFILE%\Videos\FreeVideoStudio\Memes
  ```
  Resolved via `MemeDirectory.GetDefault()` = `MyVideos\` + `ApplicationPaths.AppDirectoryName` + `\Memes`, overridable in global settings. Folder names never contain spaces (NOSPACE_01, `05` SYS-NOSPACE).
  * **Move of the old default:** builds before MEMEFOLDER_02 used `Videos\Free Video Studio\Memes`. `MemeDirectory.EnsureLegacyDefaultMigrated` runs once per process INSIDE `GetActive()` (so no caller ever sees a half-moved folder) and moves every file into the new folder. Never overwrites: an identical duplicate in the old folder is deleted, a different same-named file stays in the old folder and is logged. Empty old folders are removed. A Settings override pointing AT the old default is cleared; any other custom folder is untouched. The move is appended to `migration-paths.json` (UPGRADE_05), and the recovery restore paths call `MigrationPathResolver.ResolveSavedFile`, so projects and recovery files that name a meme by its old full path still open it.
* **Boot Scan Contract:** Scans `.mp4`, `.mkv`, `.avi`, `.png`, `.jpg`, `.jpeg` (`MemeCatalog`) and **SKIPS 0-byte files**. Each survivor is probed for native dimensions and its aspect ratio (Width / Height) is computed and cached for the portrait validation rule in `04_UI_UX_AVALONIA_SPEC.md` §10 (UI-MEMESELECT).
* **Directory Management:** Settings exposes the path plus `Open Folder` / `Change Folder`. A change updates global config and triggers a re-scan; an `UnauthorizedAccessException` reverts the path in a try-catch rather than leaving the app pointed at an unreadable folder.
* **Dynamic Cloud Retrieval (Delta Sync):** `"Download more meme videos..."` / `"Download more meme pictures..."` in the meme selector and the `⬇ More videos` / `⬇ More pictures` buttons in the meme picker open a confirmation dialog, then list the repository folder. **Only files MISSING locally (or present only as an LFS pointer / 0 bytes) are downloaded** — a full re-pull is forbidden. Every open meme list refreshes on completion through `MemeDirectory.NotifyChanged`.
  * **One repository folder (MEMEFOLDER_01):** all memes live in `meme/` (`MemeCatalog.CloudMemeFolder`); the former `mp4/` and `jpeg/` folders are gone. `MemeCategory.Video` / `MemeCategory.Image` (MEMECAT_01) filter that folder by extension (`MemeCatalog.ExtensionsFor`). Clients older than MEMEFOLDER_01 get a "not found" error and must update.
  * **Listing (MEMESYNC_01):** `GET api.github.com/repos/{owner}/{repo}/git/trees/HEAD:meme` — up to 100,000 entries with blob sizes. The contents API it replaced stopped at 1,000 entries with no paging.
  * **A failed listing is an error (MEMESYNC_02):** 404 → "The online library could not be found. Update Free Video Studio…"; 403/429 → rate-limit message; any other status → error. It is never reported as "you already have everything".
  * **LFS first (MEMESYNC_03):** a blob of at most 1,024 bytes is an LFS pointer, so it is fetched from `media.githubusercontent.com/media/{owner}/{repo}/HEAD/meme/{name}` first, otherwise from `raw.githubusercontent.com/{owner}/{repo}/HEAD/meme/{name}`; the other host is the fallback (`media.` answers 404 for a non-LFS file). A result that is still a pointer or empty counts as a failure. Downloads land in `{name}.part` and are renamed only when complete.
* **Starter set (STARTER_01 / STARTERLIST_01):** the installer ships `starter\meme` (both Robert De Niro versions — Landscape and Portrait — plus the Trump and "I will find you" clips and the three pictures) and `starter\mp3`. `MemeAssets.StarterFiles` and `Staging.StarterMeme` must list the same names (`MemeLibraryTests.StarterListsMatchTheStagingLists`); a missing file halts staging. Delivery happens once (`starter_meme.delivered`); an existing `starter_mp4.delivered` or `starter_jpeg.delivered` marker counts as delivered, so a user's deletions stay deleted and new starter files reach existing users through "Download more".
* **External Meme Ingestion:** A meme chosen from outside the directory is COPIED into the active directory, and its path is serialized into the recovery state and verified for existence on boot.
* **Runtime Logging:** `MemeSelected` is logged with `FileType`, `FilePath`, `Width`, `Height`, `AspectRatio`.
* **Image Meme Duration:** `.png` / `.jpg` / `.jpeg` are assigned `memeDuration = 4.0` seconds via `-loop 1 -framerate {targetFps}` … `-t 4.0`.

---

## 11. Colour Management  {#FFM-COLOR}
* **COLOR_01:** every delivered file is SDR BT.709, TV range, and TAGGED (`ExportColorPolicy.OutputTagArgs` in `EncoderManager.GetCodecFlags` and `TwoPassEncoding`).
* `MediaProber.GetVideoColorInfoAsync` reads `pix_fmt`/`color_*`. `ExportColorPolicy.BuildConversionChain` decides:
  * SDR 709 limited → nothing added (GPU route kept).
  * full range or BT.601 matrix → `colorspace=…:range=tv`.
  * HDR (PQ/HLG) → `zscale`+`tonemap=hable` when the bundled FFmpeg lists both filters, else Degraded (export continues).
* Insertion point: `ProcessWorker`, right after the timing stage (speed/cuts/CFR) on BOTH the main and HUD branches, and BEFORE fades, intro, portrait crop, HUD overlays and memes. `MergerWorker` applies it per input before concat.
* Any conversion filter is outside `ExportVideoPipeline`'s CUDA table, so HDR/full-range sources take the CPU filter route automatically.

---

## 12. Thumbnail Intro Tag & The Merger's Thumbnail Scraper  {#FFM-SCRAPER}
* **SCRAPER_01 — the tag.** Every output that starts with the 0.1 s still thumbnail intro (the frame SMS/WhatsApp show, so a shared clip is never a black thumbnail) is stamped `fvs_intro_sec=<seconds, F3>` via `IntroTag.OutputArgs` (`-metadata … -movflags +faststart+use_metadata_tags`). Written at EVERY final write: `ProcessWorker` single-pass, slow pass 2 and the two-pass tail; `MergerWorker` the same three. A plain remux DROPS mdta keys, so no path may rely on the tag surviving a copy.
* **Detection is tag-only.** `IntroTag.Read` accepts `0 < v ≤ 2.0` and `v ≤ duration/2`, else 0. Untagged files (older exports, other programs) are never cut. No pixel heuristics.
* **SCRAPER_02 — `MergedTimeline` is the single clock** for the merger preview, the Music Wizard's lanes (`MusicWizardWindow.MergerClipWindows`), the merger music preview and the export (`MergerWorker.ClipIntroSkipSec`). Merged seconds = kept clip content end to end, 1.0x, EXCLUDING the synthetic custom-thumbnail intro.
  * Clips 2..N: tagged intro removed while the scraper is on.
  * Clip 1: kept, UNLESS a custom thumbnail (**SCRAPER_04** — SET / MOVE HERE / REMOVE THUMBNAIL + draggable camera marker; the frame is clamped out of any removed intro) is set → removed regardless of the scraper, replaced by a 0.1 s still of the chosen frame (`ThumbnailClipIndex`/`ThumbnailSourceSec`), prepended with silence AFTER speed, concat and the music mix (music never moves).
  * Fades are untouched. A removal that would leave < 0.05 s of content is refused.
  * `Remap` re-anchors a merged position through (clip, source second). The music window is remapped whenever the layout changes, so a song starts on the same clip moment in preview and file.
* **SCRAPER_03 — analysis is background.** `MergeClipAnalyzer`: one ffprobe per file on the thread pool, 2..4 in parallel, cached by path+size+mtime. The UI awaits finished tasks only; MERGE/ADD MUSIC await `EnsureTimelineReadyAsync`.
* **Merged output tag:** custom thumbnail → 0.100; clip 1 intro kept and untrimmed → its tag ÷ speed; else no intro — `fvs_intro_sec` is omitted, but `fvs_timing` is always written (`intro=0`).
* **SCRAPER_05 — setting** `MergerThumbnailScraper` (default ON; settings schema v8 forces it ON for upgraders; v9 = REMOVEUX_01). Surfaces: merger bottom-left checkbox, Settings › Defaults › Video Merger.
* **TIMINGTAG_02 — frame-exact timing tag.** Every export also carries `fvs_timing=v=2;fps=N/D;intro=F;fadein=F;fadeout=F` (frames at the export CFR). File layout: `[intro][fade-in … body … fade-out]`. A fade key is OMITTED (unknown) when a meme sits at that edge; `0` means no fade. `ExportTimingTag.Read` prefers v2 and falls back to the v1 seconds key. Merged outputs write fades too (OUTTAG_01).
* **FRAMESNAP_01 — cuts land on real frame pts.** When a queued clip's timing tag has `intro=F>0`, `MergeClipAnalyzer` runs `FramePtsProbe` (ffprobe `frame=best_effort_timestamp_time`, first F+16 packets, sorted, µs relative to `format.start_time`) and the intro cut is the pts of frame F, not F/fps. Merger trims are written with 6 decimals and the start backed off by `TrimStartEpsilonSec` (0.5 ms) so the frame at the cut is kept. Verified on 60, 59.94 and VFR clips (a nominal F/fps cut drops one real frame on the VFR clip).
* **CLIPFRAMES_01 — every plain clip has exactly the composite's frame count (P9).** `fps=60` over a whole file emits up to the last frame's END, one frame more than `round(keep × 60)` on e.g. a 59.94 source. When `MergerWorker.Edl` describes the queue, each clip WITHOUT effects gets `tpad=stop_mode=clone:stop=1,trim=end_frame=N` after `fps=60` and its audio `apad,atrim=end=N/60` (silence of N/60 when it has none), N = `round(clip.OutputLengthSec × 60)`. Harness (odd lengths, 59.94/30/60 sources, scraper on): plain 380/380 frames at 1.0x and 253/253 at 1.5x, effects chain 410/410 and 293/293, audio = video.
* **MERGEGRAPH_01 — Merger clips with granular effects (P7.1).** `MergerWorker.Edl` (set from the Merger's edit list; ignored unless it has the same clips in the same order as `InputFiles`). A clip WITHOUT effects keeps the plain chain unchanged. A clip WITH effects goes through `MergeClipGraph.Build`: trim kept window (FRAMESNAP epsilon) → the Main App's `GranularSpeedBuilder.Build` in SOURCE pixels (speed segments absolute, freezes = speed-0 segments, zoom, cuts clip-relative, base speed outside segments = D19, no HUD branch) with every internal label prefixed `c{i}_` → canvas chain + fps=60 → memes spliced at their output times (Build's time mapper; AtStart 0 / AtEnd fade-out start / Mid; memes fit-padded to the canvas, CFR, own audio padded or silence) → `concat` → `[v{i}][a{i}]`. Meme files are extra inputs after the thumbnail input (images `-loop 1 -framerate 60 -t D`); a missing meme file is skipped and logged. `outputDuration` = `CompositeTimeline.ClipsOutputSec` when any clip has effects. Harness (container): 3 clips (cut + image meme / 0.5x ramp / freeze + video meme AtEnd) → 15.800 s exactly as predicted, audio = video, no intro frames, every run the expected frame count; 4-clip variant within +1 frame. **MEMELEVEL_01 (P9):** a meme with sound is measured once per file (`MemeLoudness.GainDbAsync` → `AudioLoudnessProbe`) and gets the Main App's rule — gain = TargetLufs − measured, clamped [MinMusicGainDb, MaxMusicGainDb], `volume=` when |gain| > 0.01 dB, then `alimiter=limit=-2.0dB` (always on a meme with sound); unmeasurable = as recorded + limiter. P9.2 harness: 3 clips (cut + AtStart video meme / 0.5x ramp + Mid video meme / freeze + AtEnd image meme) → 16.500 s = prediction, audio = video; meme -20.6 LUFS raised (+6.6 dB), clip audio unchanged.
* **MUSICMAP_01 — music by output time (P7.2, D8).** The Merger converts the music window's merged-clock start/end to finished-body seconds with `CompositeTimeline.MergedSecToBodyOutputSec` (effects and base speed before that moment move it; the thumbnail intro is excluded because it is prepended after the mix). The music itself is never stretched. Harness: music placed at merged 6.0 s after a cut and a 1.5 s meme → heard from 8.0 s (predicted 8.0000) to 11.05 s.
* **OUTTAG_01 — merged output fades (P7.3).** The merged file's `fvs_timing` carries clip 1's fade-in and the last clip's fade-out (60 fps frames, divided by the base speed) when they survive unchanged: tag known, kept window still contains the fade, no granular effects on that clip. Otherwise the key is omitted (unknown). Harness: `v=2;fps=60/1;intro=6;fadein=30;fadeout=60`.
