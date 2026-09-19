> **STATUS: OPEN.** Refactoring plan. Nothing here has been applied.

# TASK SPECIFICATION: R1 - EXPORT_PIPELINE_GOD_METHOD

## 1. TARGET CONTEXT & SCOPE
- File Path: `src/FortniteVideoSoftware.Core/Media/ProcessWorker.cs`
- Target Range: Lines 474-2408 — `RunAsync`, a **single 1,935-line method**. Secondary: `PerformLoudnormPassAsync` (179 lines) and the nested local function `RunFfmpegOnce` inside `RunAsync`.
- Defect Classification: Modernization Bottleneck (single-responsibility violation at method scope; untestable core)

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism: `ProcessWorker.cs` is 3,249 lines with only 35 fields and 27 methods — it is NOT a god class. **60% of the entire file is one method.** Measured method sizes in this file:
  ```
    1935  RunAsync                    (line 474)
     179  PerformLoudnormPassAsync
      31  ReadExitCodeSafely
      28  Dispose
  ```
  Inside those 1,935 lines, in one scope, live: temp job directory creation, HUD/mask resolution, text overlay PNG generation, meme probing and concatenation, the cut/trim timeline maths, the filter-graph assembly, the NVENC complexity probe, the loudnorm two-pass, encoder fallback laddering, the size-target retry loop, the two-pass master/tail routing, thumbnail extraction, output path reservation, and the failure classification. Every one of those reaches the same local-variable bag (`tempJobDir`, `corePath`, `twoPassMasterPath`, `graphIsMaster`, `slowStage`, `attemptCounter`, `currentEncoder`, `lastError`, `collector`, …) and the same closure-captured `cancellationToken`.
- **This is where the bugs actually were.** Both Finding 1 (`FFMPEGSTOP_01`) and Finding 3 (`RESCUE_01`) were defects inside or immediately serving this method, and the reason they survived is that nothing here can be read, reviewed or tested in isolation. `tests/FortniteVideoSoftware.Core.Tests` has 148 tests and **not one of them touches `RunAsync`** — it cannot be called without a real video file, a real ffmpeg binary, and a real filesystem.
- Target Pattern: a **pipeline of named stages behind an explicit context object**. `ExportJobContext` (paths, cancellation, progress sink, diagnostics collector) passed to `IExportStage` implementations — `PrepareScratchStage`, `OverlayAssetStage`, `MemeAssemblyStage`, `FilterGraphStage`, `ComplexityProbeStage`, `LoudnormStage`, `EncodeStage`, `TwoPassTailStage`, `ThumbnailStage`, `PublishStage` — each independently unit-testable with a fake process runner. `RunAsync` becomes the ~40-line loop that walks the stage list and handles failure/retry.

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior — every one of these is load-bearing and documented in-file; each must land in exactly one stage with its comment intact:
  - `PROCGATE_01` process-reference gate and `FFMPEGSTOP_01` shutdown ladder (the context object owns the live process; stages never touch `_currentProcess` directly).
  - `T01` SLOW-route split (used only when the temp drive cannot hold the scratch master) and `DiskSpaceGuard.HasRoomFor` gating.
  - `PROBE_01` NVENC complexity probe and its calibration margin model.
  - `CUT_01` surviving-range arithmetic, including the sub-frame range drop and the 0.02s epsilon.
  - `OUTPATH_01` atomic output name reservation.
  - `ISSUE_12` `CompletionWarning` (export succeeded, thumbnail did not).
  - The encoder fallback ladder and the two-attempt size-target retry — the ORDER of encoder attempts is calibrated, not arbitrary.
  - `FfmpegErrorClassifier` attempt identities (`ExportAttemptIdentity.AttemptIndex`) must keep incrementing across stages exactly as today, or the diagnostic reports renumber.
- Public Interface Parity: `RunAsync(CancellationToken)`, `Cancel()`, `Dispose()`, and every public property/event (`ProgressUpdate`, `PhaseUpdate`, `Finished`, `LastFailure`, `FailureDetail`, `CompletionWarning`, `WasCanceled`, `UsedGpuVideoProcessing`, `LastReportedSpeed`) keep exact signatures. **Callers must not need editing.**

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. FIRST, characterise: before moving one line, add integration tests that run `RunAsync` end-to-end against the bundled `binaries/ffmpeg.exe` and a 3-second fixture clip, asserting the emitted progress phase sequence, the final output path, and the failure payload for a forced-failure case. **These tests are the safety net; do not start step 2 without them.**
2. Introduce `ExportJobContext` holding what is today's local-variable bag. Change nothing else. Compile, run tests.
3. Extract ONE stage at a time, lowest-risk first (`ThumbnailStage`, then `PublishStage`, then `ComplexityProbeStage`, then `LoudnormStage`). Compile and run the characterisation tests after EACH extraction. Never batch two extractions.
4. Extract `EncodeStage` and `TwoPassTailStage` LAST — they own the process lifecycle and the retry ladder, and they are where FFMPEGSTOP_01 lives.
5. Keep `RunAsync`'s outer try/catch/finally (the `tempJobDir` cleanup and the `await AwaitActiveShutdownAsync()` that precedes it) at the top level. It must still run on every path.
6. Do NOT introduce a DI container. Stages are constructed explicitly by `RunAsync`; this assembly is `IsAotCompatible` and must stay reflection-free.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: zero new warnings; `IsAotCompatible` still satisfied (no reflection, no dynamic).
- Behavioral: the characterisation suite from step 1 passes unchanged at every commit. Byte-identical output file for a fixed input, fixed seed and fixed encoder.
- Structural: no method in `ProcessWorker.cs` exceeds 120 lines; each stage has at least one unit test that does not require a real ffmpeg binary.
- Performance: export wall-clock within 2% of the pre-refactor baseline on the same clip and hardware.
