> **STATUS: OPEN.** Refactoring plan. Nothing here has been applied.

# TASK SPECIFICATION: R3 - EXPORT_MERGE_PIPELINE_DUPLICATION

## 1. TARGET CONTEXT & SCOPE
- File Path: `src/FortniteVideoSoftware.Core/Media/ProcessWorker.cs` and `src/FortniteVideoSoftware.Core/Media/MergerWorker.cs`
- Target Range: whole-file pairing. `ProcessWorker.RunAsync` (1,935 lines) vs `MergerWorker.RunAsync` (695 lines); the twin helpers `ReadExitCodeSafely`, `TryRescueFinishedRender`, `EmitFinished`, `Cancel`, `Dispose`, `BeginCooperativeShutdown`, `RunTwoPassTailAsync`.
- Defect Classification: Logic Consolidation (duplicated logic with **demonstrated divergence**)

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism: these two classes run structurally identical FFmpeg pipelines — start a process, redirect `-progress pipe:1`, drain stdout/stderr on two reader tasks, classify failure via `FfmpegErrorClassifier`, run a two-pass tail, rescue a finished render, clean up a scratch job directory — and each holds its own copy of that code.
- **THIS IS NOT A THEORETICAL RISK; IT HAS ALREADY HAPPENED TWICE, AND BOTH INSTANCES WERE SHIPPED:**
  1. `ReadExitCodeSafely` — same name, same signature, same doc comment in both files. `MergerWorker`'s was hardened to route through `GracefulProcessTerminator`; `ProcessWorker`'s was left calling `Kill(entireProcessTree: true)` raw. That divergence is Finding 1 (`FFMPEGSTOP_01`), and it meant the PRIMARY export path shipped without cooperative FFmpeg shutdown while the secondary one had it.
  2. `TryRescueFinishedRender` — byte-identical copies differing only in a filename prefix and a log tag. Both carried the `File.Exists`-loop-then-`File.Move` race and the unbounded index loop that `ResolveOutputPath` (twenty lines below one of them) documents as a fixed defect. That is Finding 3 (`RESCUE_01`).
- Every future fix to either pipeline has the same 50% chance of landing in only one copy.
- Target Pattern: a shared `FfmpegJobRunner` in `Core/Media` owning process start, stdin redirection, the two reader tasks, the shutdown ladder, exit-code reading and rescue; the two workers keep ONLY what genuinely differs — their argument construction and their job semantics.

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior:
  - **THE TWO PIPELINES ARE NOT THE SAME JOB.** The editor export does trim/cuts/speed/memes/overlays/voice-over against ONE source; the merger concatenates N sources with normalisation. Do not unify `RunAsync`. Unify the PROCESS MECHANICS underneath them only.
  - Log tags must stay distinct (`"FFmpeg"` vs `"FFmpeg MERGE"`, `"Output"` vs `"Merger"`) — crash digests and the dev log are read by tag.
  - User-visible rescue filename prefixes stay distinct (already parameterised in `RescuedOutputPath`, RESCUE_01 — follow that precedent exactly).
  - `MergerWorker` drains its readers with a `GracefulProcessTerminator.HardKillConfirmMs` timeout and `ProcessWorker`'s two-pass path uses a 5s `WaitAsync` drain cap. Pick one and document why; do not silently adopt the shorter.
  - `CancelledMessage` string constants differ per worker and appear in the UI. Keep both.
- Public Interface Parity: both workers keep every public member exactly as-is. This is an internal collaborator extraction; no caller changes.

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. Start with the helpers that are already proven identical: `ReadExitCodeSafely`, `BeginCooperativeShutdown`/`AwaitActiveShutdownAsync`. `RescuedOutputPath` (RESCUE_01) is the template — one internal static/instance collaborator, caller-supplied tag and prefix.
2. Then extract the process-run core: `ProcessStartInfo` construction (including `RedirectStandardInput = true`, FFMPEGSTOP_01), `ChildProcessTracker.AddProcess`, the cancellation registration, both reader tasks, `WaitForExitAsync`, ladder await, exit code.
3. Leave `RunTwoPassTailAsync` for last — the two versions have different return types (`bool` vs `(bool, ExportFailure?)`) and genuinely different failure reporting.
4. After each extraction, grep both files for any remaining pair of same-named private methods and either unify or add an in-code comment stating why they must differ.
5. Add one test that asserts the shared runner's shutdown ladder is used — a regression guard against the exact divergence that caused Finding 1.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: zero new warnings.
- Behavioral: existing 148 Core tests pass; an editor export and a merge both produce byte-identical output to the pre-refactor baseline.
- Structural: `grep` finds no method name defined privately in BOTH `ProcessWorker.cs` and `MergerWorker.cs` without an explicit "differs because…" comment.
- Performance: no additional process spawns; no extra allocation per progress line.
