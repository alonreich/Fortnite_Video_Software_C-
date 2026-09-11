# SPECIFICATION 03: FFMPEG EXPORT PIPELINE

## Code Mini-Map: Bound Source Files & Symbols
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `src/FortniteVideoSoftware.Core/Media/ProcessWorker.cs` | `ProcessWorker`, `ExportPayload`, `ProgressInfo` | `ExecuteAsync`, `BuildFilterGraph`, `UpdateMonotonicProgress`, `ParseProgress` | Core FFmpeg rendering orchestrator, command builder, and progress monitor. |
| `src/FortniteVideoSoftware.Core/Media/GpuCapabilityProbe.cs` | `GpuCapabilityProbe` | `ProbeEncoders`, `HasNvenc`, `HasAmf`, `FallbackToCpu` | Hardware GPU encoder detection and automated fallback logic. |
| `src/FortniteVideoSoftware.Core/Media/HardwareScanner.cs` | `HardwareScanner` | `ScanGpu`, `IsRdpSession`, `FixRdpWddmRegistry` | Hardware capability enumeration, RDP session detection, and registry auto-fix. |
| `src/FortniteVideoSoftware.Core/Media/GranularSpeedBuilder.cs` | `GranularSpeedBuilder`, `ChunkSpec` | `Build`, `BuildAtempoChain`, `ZoomRampSeconds = 0.5`, `ZoomRampRequiredGap = 1.0` | Filtergraph chunk splitter, setpts/atempo chain compiler, and freeze pad synthesis. |
| `src/FortniteVideoSoftware.Core/Media/MobileFilterBuilder.cs` | `MobileFilterBuilder` | `Build`, `Apply23Crop`, `ApplyExtrudedBorders`, `BuildWatermarkFilter` | 9:16 portrait video transform, background extrusion, and HUD positioning. |
| `src/FortniteVideoSoftware.Core/Media/ZoomPreviewSimulator.cs` | `ZoomPreviewSimulator`, `ZoomCropResult` | `Compute`, `ApplySlowRamp`, `ClearLiveZoomCrop` | CPU/GPU live zoom simulation matching export filtergraph parity. |
| `src/FortniteVideoSoftware.App/Infrastructure/MemePreviewDirector.cs` | `MemePreviewDirector` | `AttachHost`, `ExecuteCutaway`, `MemeSwapOverlay`, `MemeRebuildOverlay` | Live preview cutaway playback coordination and libmpv loadfile director. |
| `src/FortniteVideoSoftware.Core/Media/MergerWorker.cs` | `MergerWorker` | `ExecuteMergeAsync`, `BuildMergeConcatGraph`, `ResampleTo60Fps` | Multi-clip concatenation, CFR resampling, and duration-weighted bitrate calculation. |
| `src/FortniteVideoSoftware.Core/Media/TextOverlayGenerator.cs` | `TextOverlayGenerator` | `RenderTextBitmap`, `WrapText`, `GeneratePng` | High-DPI title text bitmap generation for top-void rendering. |
| `src/FortniteVideoSoftware.Core/Media/FfmpegDiagnosticCollector.cs` | `FfmpegDiagnosticCollector` | `CaptureStderr`, `AnalyzeFailure`, `FormatDiagnosticReport` | Export failure classification and diagnostic report generation. |

---

## 1. Hardware Encoding & Gatekeeper
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

## 2. Memory-Safe Dynamic Zoom Filtergraph
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

## 3. Concat Framing & Sizing
* **CFR 60fps Normalization:** All merger clips and speed chunks are resampled to constant 60fps CFR prior to concat to eliminate A/V presentation timestamp drift.
* **Bitrate Conservation:**
  * Target video bitrate (`-b:v`) is calculated as the duration-weighted average of all input streams:
    $$\text{Bitrate}_{\text{target}} = \frac{\sum (D_i \times B_i)}{\sum D_i}$$
  * `-maxrate` and `-bufsize` match the peak clip bitrate among inputs.
* **Audio Edge Fades:** Injects an 8ms audio edge crossfade at each cut boundary to eliminate acoustic transients:
  ```text
  afade=t=in:st=0:d=0.008,afade=t=out:st={dur-0.008}:d=0.008
  ```

---

## 4. Meme Concat Architecture
* **Aspect Normalization:**
  * Non-portrait memes are center-cropped to 2:3 with black horizontal padding to $1080 \times 1920$.
  * Native 9:16 memes bypass the 2:3 crop and scale directly to $1080 \times 1920$.
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

## 5. Live Meme Cutaway Preview (`MemePreviewDirector`)
* **Media Swap Lifecycle:**
  1. Forward playback crosses `AtSourceSecRelative`: libmpv issues `loadfile` to swap to meme media.
  2. Plays meme media until its duration expires.
  3. libmpv issues `loadfile` restoring gameplay footage paused at the exact anchor frame.
* **Player Loop Guard:** UI tick loop skips execution while `MemePreviewDirector.IsActive` is `true`.
* **Directional Trigger:** Scrubbing over an anchor frame holds gameplay still; cutaways trigger on forward playback only.
* **Visual Overlays:**
  * Displays `MemeSwapOverlay` during asynchronous file swaps.
  * Blocks the window with `MemeRebuildOverlay` (220ms settle timeout) during timeline rebuilds to prevent visual flicker.

---

## 6. Fades & Intros
* **Buffer Margins:** Fade in/out operations require a $0.5\text{s}$ pre/post buffer margin.
* **Speed-Scaled Video Fades:** Video fade duration scales proportionally with speed factor:
  $$\text{FadeDuration}_{\text{video}} = \text{PadStartHumanSec} \times \text{SpeedFactor}$$
* **Thumbnail Blackout Prevention:** To prevent pitch-black cover frames when fade-in is enabled, the export injects a $0.1\text{s}$ frozen action frame at $t=0$ before the fade-in commences:
  ```text
  tpad=start_duration=0.1:start_mode=clone
  ```
* **Meme Fade Rules:** Leading and trailing memes receive output tail fades; middle memes receive zero fades.

---

## 7. Monotonic Progress Tracking
Render progress tracking is cost-weighted across three sequential phases and must be mathematically monotonic ($P_{n+1} \ge P_n$):

```
[Phase 1: Loudnorm] ---> [Phase 2: Video Encoding] ---> [Phase 3: Mux & Thumb]
      (0% - 8%)                 (8% - 96%)                   (96% - 100%)
```

* **Phase Allocation:**
  * **Phase 1 (Audio Loudness Analysis):** $0\% - 8\%$
  * **Phase 2 (Video & Filtergraph Encoding):** $8\% - 96\%$ (cost-weighted for dynamic zoom, CAS sharpening, and mobile crops)
  * **Phase 3 (Muxing & Thumbnail Generation):** $96\% - 100\%$
* **Monotonic Invariant:**
  $$P_{n+1} = \max(P_n, P_{\text{calculated}})$$
  Progress bars are strictly prevented from snapping or lerping backward across multi-pass operations. UI progress values lerp smoothly on $\approx 33\text{ms}$ intervals.