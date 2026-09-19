> **STATUS: OPEN.** Refactoring plan. Nothing here has been applied.

# TASK SPECIFICATION: R6 - PROGRESS_OVERLAY_CARRIES_A_GAME

## 1. TARGET CONTEXT & SCOPE
- File Path: `src/FortniteVideoSoftware.App/Controls/PhaseOverlayControl.axaml.cs`
- Target Range: whole file — **2,492 lines, 117 fields, 79 methods, 25 event subscriptions** in a control whose stated job is "show export progress".
- Defect Classification: Modernization Bottleneck (two unrelated products in one control; highest not-its-job ratio in the codebase)

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism: measured clusters:
  ```
    fight 251 | Log 125 | Skit 103 | Taunt 74 | Boss 48 | Progress 40
    Gpu 29 | Anim 29 | Taskbar 26 | Cpu 25 | Projectile 12 | Graph 8 | Combo 7
  ```
  **`fight` outnumbers `Progress` six to one.** This control is a progress overlay bolted to a fighting-game easter egg (boss phases, taunts, combos, projectiles, skits, KO states, camera shake, fighter glow) plus a live hardware telemetry widget (an `nvidia-smi` child process, `GetSystemTimes` CPU sampling, `GlobalMemoryStatusEx`) plus a log tail plus Win32 taskbar progress. 117 fields for four unrelated jobs.
  Concrete consequences already present in the file:
  - `OnTick` (1s `DispatcherTimer`) fires `Task.Run` with no overlap guard, and `GetCpuUsage` does an unsynchronised read-modify-write of `_lastIdle`/`_lastSys` from thread-pool threads. Two overlapping ticks — likeliest exactly when the pool is busy, i.e. during the export this overlay is watching — produce garbage CPU readings.
  - `_lastGpu` is written from the `nvidia-smi` `OutputDataReceived` thread and read on the UI thread with no synchronisation.
  - The easter-egg animation state and the telemetry state share one field bag, so a change to either can perturb the other.
  These are individually cosmetic — which is precisely why they were never worth a separate finding, and precisely why the file keeps growing.
- Target Pattern: three separate types. `ExportProgressOverlay` (progress bar, phase text, taskbar, log tail — the actual product). `HardwareTelemetrySampler` (a `Core`-side sampler with proper synchronisation and an overlap guard, reusable and testable). `FighterEasterEgg` (a self-contained control the overlay hosts, or does not).

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior:
  - **THE EASTER EGG IS A FEATURE, NOT DEAD CODE.** Do not delete it. Extract it intact, including boss-phase probability (`_rand.NextDouble() < 0.05`), taunt selection, skit timing and KO states.
  - `ISSUE_7` — the `MaxPendingLogs` ceiling on `_pendingLogs` and BOTH release points for the static `RuntimeLog.LogAppended` subscription (`StopOverlay` AND `OnDetachedFromVisualTree`). A static event subscription that outlives its control keeps the control alive for the session; the double release is deliberate, and unsubscribing twice is harmless.
  - `AppendLog` runs on RuntimeLog's writer thread, must stay cheap, and must NEVER log anything itself — `RuntimeLog` invokes `LogAppended` synchronously from inside `Write`, so logging there recurses.
  - `AmbientBubblesBackground.GloballySuspended = true` during an export — a deliberate CPU concession while encoding.
  - `nvidia-smi` is started through `ChildProcessTracker` and killed on restart; keep both, and route its teardown through `GracefulProcessTerminator` while you are there.
  - `TaskbarProgress` generation counter (`_taskbarGeneration`) exists to stop a stale async callback resetting a newer job's taskbar state.
- Public Interface Parity: `StartOverlay()`, `StopOverlay()`, `AppendLog(string)` and the progress/phase entry points keep exact signatures — `MainWindow` and `VideoMergerWindow` both host this control.

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. Extract `HardwareTelemetrySampler` FIRST and fix its concurrency while moving it: an `Interlocked`/`SemaphoreSlim` overlap guard on the tick so two samples can never interleave, and `volatile`/`Interlocked` on `_lastGpu`. This is the only part of R6 that fixes a real (if cosmetic) defect.
2. Extract `FighterEasterEgg` as its own control with its own fields. The overlay hosts it and feeds it only `percent` and terminal state.
3. What remains IS `ExportProgressOverlay`. It should be a few hundred lines.
4. Route the `nvidia-smi` process teardown through `GracefulProcessTerminator` (`attemptQuitCommand: false`).
5. Keep the log hand-off queue and its ceiling exactly as-is; move it with its comment.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: zero new warnings.
- Behavioral: an export shows the same progress, the same log tail, the same taskbar behaviour and the same easter egg. Closing the host window mid-export strands no `nvidia-smi` process and no `LogAppended` subscription.
- Structural: `ExportProgressOverlay` under 600 lines; no shared field between telemetry and easter egg; CPU sample correctness verified under forced thread-pool starvation.
- Performance: no additional per-tick allocation; `AppendLog` still allocation-light on the logger thread.
