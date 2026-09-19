# SPECIFICATION 03: FFMPEG EXPORT PIPELINE

## Code Mini-Map: Bound Source Files & Symbols

> **⚠ CO-GOVERNED rows are bound by EVERY spec listed on them.** Reading only this one is not compliance (`SPEC_GOVERNANCE.md` §2).
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `src/FortniteVideoSoftware.App/ViewModels/QualityLadder.cs` | `QualityLadder`, `Tier` | `Tiers`, `TargetMbFor`, `DefaultIndex`, `OriginalIndex`, `ColorFor` | The quality dial's tiers and the tier -> target-megabytes model. |
| `src/FortniteVideoSoftware.App/ViewModels/ExportViewModel.cs` | `ExportViewModel` | `QualitySliderValue`, `EstimatedFileSizeText`, `EstimatedFileSizeDescription` | Quality selection and bound output-size readout. |
| `src/FortniteVideoSoftware.App/MainWindow.SizeEstimate.cs` | `MainWindow` | `CaptureSizeRequest`, `RequestSizeEstimate` | Immutable estimate inputs and UI publication. |
| `src/FortniteVideoSoftware.App/Services/OutputSizeEstimator.cs` | `OutputSizeEstimator` | `EstimateMainAsync`, `EstimateMergerAsync`, `CalculateMain`, `CalculateMerger`, `ReadMediaAsync` | Shared estimates and bounded media metadata cache. |
| `src/FortniteVideoSoftware.Core/Media/OutputFileSize.cs` | `OutputFileSize` | `FormatMegabytes`, `MergerConstantQuality`, `MergerTargetKbps` | MB/GB/TB formatting and shared merger encoder settings. |
| `src/FortniteVideoSoftware.Core/Media/ProcessWorker.cs` | `ProcessWorker`, `ExportPayload`, `ProgressInfo` | `ExecuteAsync`, `BuildFilterGraph`, `UpdateMonotonicProgress`, `ParseProgress`, `AppContext.BaseDirectory` + `backend/` probe | Core FFmpeg rendering orchestrator, command builder, and progress monitor. |
| `src/FortniteVideoSoftware.Core/Media/GpuCapabilityProbe.cs` | `GpuCapabilityProbe` | `ProbeEncoders`, `HasNvenc`, `HasAmf`, `FallbackToCpu` | Hardware GPU encoder detection and automated fallback logic. |
| `src/FortniteVideoSoftware.Core/Media/HardwareScanner.cs` | `HardwareScanner` | `ScanGpu`, `IsRdpSession`, `FixRdpWddmRegistry` | Hardware capability enumeration, RDP session detection, and registry auto-fix. |
| `src/FortniteVideoSoftware.Core/Media/GranularSpeedBuilder.cs` | `GranularSpeedBuilder`, `ChunkSpec` | `Build`, `BuildAtempoChain`, `ZoomRampSeconds = 0.5`, `ZoomRampRequiredGap = 1.0` | Filtergraph chunk splitter, setpts/atempo chain compiler, and freeze pad synthesis. |
| `src/FortniteVideoSoftware.Core/Media/MobileFilterBuilder.cs` | `MobileFilterBuilder` | `Build`, `Apply23Crop`, `ApplyExtrudedBorders`, `BuildWatermarkFilter` | 9:16 portrait video transform, background extrusion, and HUD positioning. |
| `src/FortniteVideoSoftware.Core/Media/ZoomPreviewSimulator.cs` | `ZoomPreviewSimulator`, `ZoomCropResult` | `Compute`, `ApplySlowRamp`, `ClearLiveZoomCrop` | CPU/GPU live zoom simulation matching export filtergraph parity. |
| `src/FortniteVideoSoftware.App/Infrastructure/MemePreviewDirector.cs` | `MemePreviewDirector` | `AttachHost`, `ExecuteCutaway`, `IsActive`, `MemeSwapOverlay`, `MemeRebuildOverlay` | Live preview cutaway playback coordination and libmpv loadfile director. |
| `src/FortniteVideoSoftware.Core/Media/MergerWorker.cs` | `MergerWorker` | `ExecuteMergeAsync`, `BuildMergeConcatGraph`, `ResampleTo60Fps` | Multi-clip concatenation, CFR resampling, and duration-weighted bitrate calculation. |
| `src/FortniteVideoSoftware.Core/Media/TextOverlayGenerator.cs` | `TextOverlayGenerator` | `RenderTextBitmap`, `WrapText`, `GeneratePng` | High-DPI title text bitmap generation for top-void rendering. |
| `src/FortniteVideoSoftware.Core/Media/FfmpegDiagnosticCollector.cs` | `FfmpegDiagnosticCollector` | `CaptureStderr`, `AnalyzeFailure`, `FormatDiagnosticReport` | Export failure classification and diagnostic report generation. |

---

## 1. Hardware Encoding & Gatekeeper  {#FFM-HWENC}
* **Probe Hierarchy:** `GpuCapabilityProbe` inspects installed display adapters for active NVIDIA NVENC (`h264_nvenc`) or AMD AMF (`h264_amf`) hardware encoders using test encode probes.
* **Graceful CPU Fallback:** Missing drivers, unaccelerated GPUs, or VM/RDP sessions silently fallback to software CPU encoding (`libx264` with `-preset veryfast -crf 20`).
* **RDP Detection & Registry Auto-Fix:**
  * When a Remote Desktop session is detected without WDDM hardware acceleration, the UI displays red indicator badges: `RDP SESSION` and `RDP: CPU BLOCKED`.
  * The "Auto-Fix" action issues an elevated PowerShell command modifying the Windows Registry:
    ```cmd
    reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services" /v "bEnumerateHWDuringRDP" /t REG_DWORD /d 1 /f
    ```
  * System requires a session reconnect to unlock NVENC/AMF over RDP.

---

## 2. Memory-Safe Dynamic Zoom Filtergraph  {#FFM-ZOOMGRAPH}
* **Absolute Ban on `zoompan`:** The FFmpeg `zoompan` filter is strictly banned suite-wide due to severe native heap leaks that crash long-form renders.
* **Filtergraph Implementation:**
  Dynamic scaling, padding, cropping, and contrast-adaptive sharpening (CAS) are executed via:
  ```text
  pad=iw+3840:ih+2160:(ow-iw)/2:(oh-ih)/2:color=black,
  scale={targetW}:{targetH}:eval=frame,
  crop={ZoomW}:{ZoomH}:{ZoomX}:{ZoomY},
  cas=0.5,
  scale={srcW}:{srcH}:force_original_aspect_ratio=decrease,
  pad={srcW}:{srcH}:(ow-iw)/2:(oh-ih)/2:color=black
  ```
* **Upscale Ceiling & Sharpening:**
  * Upscale ceiling is clamped to:
    $$\text{MaxScale} = \min\left(\frac{1920}{\text{ZoomW}}, \frac{1080}{\text{ZoomH}}\right)$$
  * Mandatory AMD Contrast Adaptive Sharpening (`cas=0.5`) counteracts bilinear softness during upscale.
* **Chunk Normalization Invariant:** All video chunks are strictly normalized prior to concatenation:
  ```text
  fps={targetFps}:round=near:start_time=0
  ```

---

## 3. Concat Framing & Sizing  {#FFM-CONCAT}
* **CFR 60fps Normalization:** All merger clips and speed chunks are resampled to constant 60fps CFR prior to concat to eliminate A/V presentation timestamp drift.
* **Bitrate Conservation:**
  * Target video bitrate (`-b:v`) is calculated as the duration-weighted average of all input streams:
    $$\text{Bitrate}_{\text{target}} = \frac{\sum (D_i \times B_i)}{\sum D_i}$$
  * `-maxrate` and `-bufsize` match the peak clip bitrate among inputs.
* **Audio Edge Fades (SPLICE_01 / SPLICE_02):** Injects an audio edge fade at each end of every concatenated chunk to eliminate acoustic transients:
  ```text
  afade=t=in:st=0:d={fade},afade=t=out:st={dur-fade}:d={fade}
  ```
  * Nominal fade is $8\text{ms}$ (`SpliceFadeSec = 0.008`) — under half a frame at 60fps.
  * **Capped at 2% of the chunk at each end:** $\text{fade} = \min(0.008,\ D_{\text{chunk}} / 50)$. 8ms was sized for joins about a second apart; with `SegGapMs = 0` blocks can touch, and a run of short blocks put 16ms of ramp on a 200ms chunk — 8% of it, heard as a gargle rather than a de-click. Chunks of 400ms and up are unaffected.
  * Chunks shorter than $3 \times$ the nominal fade get no fade at all — too brief for a click to register.

---

## 4. Meme Concat Architecture  {#FFM-MEMECONCAT}
* **Aspect Normalization:**
  * Non-portrait memes are center-cropped to 2:3 with black horizontal padding to 1080 x 1920.
  * Native 9:16 memes bypass the 2:3 crop and scale directly to 1080 x 1920.
* **Even-Dimension Mask:** All meme frames are bitwise-masked to even pixel boundaries:
  ```text
  scale=w='bitand(iw,-2)':h='bitand(ih,-2)'
  ```
* **Silent Meme Audio Synthesis:** Memes lacking audio streams inject synthesized silence (`anullsrc` at 48kHz stereo) for the exact duration of the meme.
* **Still Image Looping:** Static image memes (`.png`, `.jpg`, `.webp`) are looped at the project target frame rate:
  ```cmd
  -loop 1 -framerate {targetFps} -t 4.0 -i "{memePath}"
  ```

---

## 5. Live Meme Cutaway Preview (`MemePreviewDirector`)  {#FFM-MEMEPREVIEW}
* **Media Swap Lifecycle:**
  1. Forward playback crosses `AtSourceSecRelative`: libmpv issues `loadfile` to swap to meme media.
  2. Plays meme media until its duration expires.
  3. libmpv issues `loadfile` restoring gameplay footage paused at the exact anchor frame.
* **Player Loop Guard:** UI tick loop skips execution while `MemePreviewDirector.IsActive` is `true`.
* **Directional Trigger:** Scrubbing over an anchor frame holds gameplay still; cutaways trigger on forward playback only.
* **Companion Audio Muting (MEME_07):** The meme carries its OWN sound. While `IsActive` is `true`, the background music bed and EVERY voice-over take PAUSE, and resume the moment gameplay returns. This follows structurally from the host tick early-returning — but it is a hard requirement, not a side effect: without it the game audio, the meme audio, the voice-over and the music all play simultaneously in preview.
* **Global Property Save/Restore:** Four mpv globals survive `loadfile` and are saved and restored around every cutaway: `speed` (forced to `1.0` — a meme inside a 2x block would otherwise preview at 2x and export at 1x), `video-crop` and `vf` (a zoom crop belongs to the gameplay; the export splices the meme UNCROPPED), and `image-display-duration` (a still meme would otherwise flash past in mpv's default 1s instead of holding its 4s).
* **Visual Overlays:**
  * Displays `MemeSwapOverlay` during asynchronous file swaps.
  * Blocks the window with `MemeRebuildOverlay` (220ms settle timeout) during timeline rebuilds to prevent visual flicker.

---

## 6. Fades & Intros  {#FFM-FADES}
* **Buffer Margins:** Fade in/out operations require a 0.5s pre/post buffer margin.
* **Speed-Scaled Video Fades:** Video fade duration scales proportionally with speed factor:
  $$\text{FadeDuration}_{\text{video}} = \text{PadStartHumanSec} \times \text{SpeedFactor}$$
* **Thumbnail Blackout Prevention:** To prevent pitch-black cover frames when fade-in is enabled, the export injects a 0.1s frozen action frame at t=0 before the fade-in commences:
  ```text
  tpad=start_duration=0.1:start_mode=clone
  ```
* **Meme Fade Rules:** Leading and trailing memes receive output tail fades; middle memes receive zero fades.

---

## 7. Monotonic Progress Tracking  {#FFM-PROGRESS}
Render progress tracking is cost-weighted across three sequential phases and must be mathematically monotonic (P_n+1 >= P_n):

```
[Phase 1: Loudnorm] ---> [Phase 2: Video Encoding] ---> [Phase 3: Mux & Thumb]
      (0% - 8%)                 (8% - 96%)                   (96% - 100%)
```

* **Phase Allocation:**
  * **Phase 1 (Audio Loudness Analysis):** 0% - 8%
  * **Phase 2 (Video & Filtergraph Encoding):** 8% - 96% (cost-weighted for dynamic zoom, CAS sharpening, and mobile crops)
  * **Phase 3 (Muxing & Thumbnail Generation):** 96% - 100%
* **Monotonic Invariant:**
  $$P_{n+1} = \max(P_n, P_{\text{calculated}})$$
  Progress bars are strictly prevented from snapping or lerping backward across multi-pass operations. UI progress values lerp smoothly on ~ 33ms intervals.

---

## 8. Phase 3 Thumbnail Strip Temporal Padding  {#FFM-THUMBSTRIP}
* **The Defect It Prevents:** The `tile=15x1` thumbnail-strip extractor samples only the FIRST physical input clip, while the requested UI timeline duration equals the SUM of all merged/queued videos. Unpadded, every sample past the first clip's end returns nothing and the strip renders pitch-black voids — which users read as a corrupted or prematurely-ended file.
* **Mandatory Filter Order:** `tpad` MUST be injected BEFORE the `fps` sampler, never after:
  ```text
  tpad=stop_mode=clone:stop_duration=1000,fps={sampleRate},scale=...,tile=15x1
  ```
* **Why `stop_duration=1000`:** The final physical frame is cloned for an effectively unbounded 1000 seconds, so the sampler always finds a frame no matter how long the combined timeline is. The value is a ceiling, not a duration — extraction stops when the requested sample count is met.
* **Scope:** Add Music Wizard phase 3 strip generation and the Video Merger's multi-clip preview grid. Distinct from the 0.1s `tpad=start_duration` intro clone in §6 (FFM-FADES), which exists to prevent black COVER frames.

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

⚠️ **SAFE ONLY BECAUSE THE EXPORT IS TWO-PASS VBR.** `ProcessWorker` runs an analysis pass and allocates bits by complexity, so a smaller target does not starve the moving footage — the freeze simply stops being paid for. Under a fixed-bitrate single-pass encode this discount would take bits AWAY from the motion and must not be applied.
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
* Merger at 100% uses the duration-weighted video bitrate and the SAME bitrate clamp as `MergerWorker`. Below 100%, its rough prediction follows the export's constant-quality curve, with an estimated factor of `2^((15-CQ)/6)`. Source dimensions and frame rate adjust that prediction for 1080p60 output.
* Audio counts once as the exported soundtrack. Merger always writes 192 kbps AAC; mixing in music does not add the original music files' bytes. Rough constant-quality predictions include 1% container overhead. These predictions cannot guarantee a final size without encoding the full content.
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

## 9. Production Binary Discovery Hierarchy  {#FFM-BINPATH}
* **Strict Search Order:** `ProcessWorker` resolves `ffmpeg.exe` / `ffprobe.exe` in this order and stops at the first hit:
  1. `AppContext.BaseDirectory` + `backend\` — **ALWAYS PROBED FIRST.**
  2. Development sandbox paths (`bin\Debug\...\backend\`, injected by MSBuild during `dev.cmd`).
  3. Only then any wider fallback.
* **Why The Order Is Non-Negotiable:** The production MSI installs the binaries into `backend\` beside the executable. Probing anything else first lets a production build either fail to locate its executables or silently bind a system-wide FFmpeg of unknown version and build flags — a filtergraph this suite depends on (`cas`, `acrossover`, `sidechaincompress`) may not exist in that binary.
* **Dev/Prod Parity:** Because MSBuild mirrors the installer's `backend\` layout into `bin\Debug`, the identical lookup satisfies both environments with no conditional compilation.
* **`ffplay.exe` Is Not Copied:** It is unused — music preview runs through an isolated `MpvIpcClient`.

---

## 10. Centralized Meme Library, Probe & Cloud Delta Sync  {#FFM-MEMELIB}
* **Default Asset Directory:**
  ```text
  %USERPROFILE%\Videos\Fortnite Video Software\Memes
  ```
  Resolved via `Environment.SpecialFolder.MyVideos + @"\Fortnite Video Software\Memes"`, overridable in global settings.
* **Boot Scan Contract:** Scans `.mp4`, `.png`, `.jpg` and **SKIPS 0-byte files**. Each survivor is probed for native dimensions and its aspect ratio (Width / Height) is computed and cached for the portrait validation rule in `04_UI_UX_AVALONIA_SPEC.md` §10 (UI-MEMESELECT).
* **Directory Management:** Settings exposes the path plus `Open Folder` / `Change Folder`. A change updates global config and triggers a re-scan; an `UnauthorizedAccessException` reverts the path in a try-catch rather than leaving the app pointed at an unreadable folder.
* **Dynamic Cloud Retrieval (Delta Sync):** `"Download more memes..."` in the meme selector opens a confirmation dialog, then enumerates the public Git provider's directory contents over its API. **Only files MISSING locally are downloaded** — a full re-pull is forbidden. The selector refreshes on completion.
* **External Meme Ingestion:** A meme chosen from outside the directory is COPIED into the active directory, and its path is serialized into the recovery state and verified for existence on boot.
* **Runtime Logging:** `MemeSelected` is logged with `FileType`, `FilePath`, `Width`, `Height`, `AspectRatio`.
* **Image Meme Duration:** `.jpg` / `.png` are assigned `memeDuration = 4.0` seconds via `-loop 1 -framerate {targetFps}` … `-t 4.0`.
