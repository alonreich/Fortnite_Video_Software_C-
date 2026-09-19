> **STATUS: APPLIED 2026-09-19.** The fix described below is in the tree. This file is retained as
> the rationale record for the change, NOT as an outstanding task. Do not re-run it.
> Verification (section 5) has NOT been executed — the test cases listed there are still outstanding,
> and the solution has NOT been compiled since the edit (no .NET SDK was available to the auditor).

# TASK SPECIFICATION: 1 - FFMPEG_UNGRACEFUL_KILL_CORRUPTION

## 1. TARGET CONTEXT & SCOPE
- File Path: `src/FortniteVideoSoftware.Core/Media/ProcessWorker.cs`
- Target Range: Lines 324-382 (`Cancel`, `ReadExitCodeSafely`), 1643-1702 (main encode `ProcessStartInfo` + cancellation registration), 2466-2492 (two-pass `ProcessStartInfo` + registration), 3094-3113 (`Dispose`). Secondary read-only references: `src/FortniteVideoSoftware.Core/Media/MergerWorker.cs:202-217, 1075-1084, 175, 1402` and `src/FortniteVideoSoftware.Core/Infrastructure/GracefulProcessTerminator.cs:1-60`.
- Defect Classification: Data Corruption (unfinalized MP4 container) / Catastrophic Failure Mode

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism:
  1. **The main export pipeline is structurally incapable of a clean stop.** Every `ProcessStartInfo` built in `ProcessWorker.cs` sets `RedirectStandardOutput` and `RedirectStandardError` and NEVER sets `RedirectStandardInput` (lines 1645-1646, 2469-2470, 2704, 2862, 2153). FFmpeg's cooperative quit is the interactive `q` command written to **stdin**. With stdin not redirected there is no channel to request it, so no code path in this file can ever ask FFmpeg to finalize its output.
  2. **Nine unconditional hard kills.** `Kill(entireProcessTree: true)` is the FIRST and ONLY action at lines 343, 365, 1700, 2180, 2491, 2717, 2875, 3107. `SIGKILL`-equivalent termination of an MP4 muxer mid-write means the `moov` atom is never written: the bytes on disk are a stream of `mdat` payload with no index. The file is the expected size and the expected name and will not play in any player.
  3. **The project already built the fix and this file never received it.** `GracefulProcessTerminator` (Core/Infrastructure) documents the exact failure: "terminates the child mid-write, which can leave orphaned grandchild processes, corrupted half-written output files, and unreleased file locks on disk." It is used by `CrashLogDigest.cs:76`, `DeploymentLifecycle.cs:1446`, `AsyncProcessRunner.cs:49`, `HudAutoDetector.cs:502`, and `MergerWorker.cs:175, 210, 1402`. `ProcessWorker.cs` — the primary user-facing export path — is the ONLY media worker that does not reference it at all.
  4. **The two implementations of `ReadExitCodeSafely` have diverged.** `MergerWorker.cs:202-217` routes through `GracefulProcessTerminator.Terminate(...)`. `ProcessWorker.cs:357-372` — same name, same signature, same doc intent — calls `proc.Kill(entireProcessTree: true)` raw. Two copies of one helper, one of which was fixed and one of which was not. There is nothing in either file that says they are meant to differ.
  5. **The corruption is then laundered as a success.** On the failure paths that reach `TryRescueFinishedRender` (line 2086 -> 2325), the killed, unfinalized file is `File.Move`d to `Fortnite-Video-RECOVERED-<stamp>.mp4` and reported to the user as a preserved render. The user is handed a corrupt file labelled as recovered work.
  6. **CORRECTION (verified during remediation).** An earlier draft of this finding claimed a kill during two-pass strands `*-0.log` / `*-0.log.mbtree`. That is WRONG and is retracted: the two-pass artifacts are written inside the per-job scratch directory (`tempJobDir`, `ProcessWorker.cs:1549-1550`) and `RunAsync`'s `finally` deletes that whole directory unconditionally. The REAL defect at that site is adjacent and was fixed instead: on Windows a file with a live handle cannot be deleted, process termination is asynchronous, so a cancel that reached the `finally` while FFmpeg was still dying made `Directory.Delete(tempJobDir, true)` fail, the failure was swallowed, and the two-pass scratch master — which can be gigabytes — was left in the temp root permanently. `await AwaitActiveShutdownAsync()` now precedes that delete.
- Target Pattern: `GracefulProcessTerminator.TerminateAsync` / `.Terminate` — the project's own bounded ladder (already-exited check -> `q` to stdin -> 1500 ms cooperative grace -> `Kill(entireProcessTree: true)` -> 2000 ms confirmation) — plus `RedirectStandardInput = true` on every FFmpeg `ProcessStartInfo` in this file so step 2 of that ladder is reachable.

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior:
  - **PROCGATE_01 (lines 296-322)** is load-bearing and must survive untouched. `SetCurrentProcess` / `TakeCurrentProcess` / `PeekCurrentProcess` exist because `_currentProcess = null; proc.Dispose();` raced `Cancel()`. The terminator must be called on the reference obtained from those accessors, never on a re-read of the field.
  - **`Cancel()`'s exception filters (lines 344-346)** — `ObjectDisposedException` and `InvalidOperationException` are caught deliberately and separately from the generic handler, with comments explaining each. Preserve both, and preserve `Cancel()`'s non-throwing contract.
  - **`_isCanceled` is `volatile` on purpose** (line 34, with the full rationale at lines 20-33). Do not change its declaration.
  - **`ReadExitCodeSafely` must remain total** — ISSUE_04: it is reached after a cancellable wait, the process may still be dying, and it must return the `-1` sentinel rather than let `InvalidOperationException` masquerade as a pipeline crash.
  - **`Dispose()`'s backstop kill (ISSUE_11 rationale, lines 3078-3093)** must still guarantee the tree dies. A cooperative grace period is acceptable there; silently returning with a live encoder is not.
  - **`ChildProcessTracker` Job Object assignment** (lines 1691, 2487) stays as the ultimate orphan safeguard and must not be removed or reordered relative to `Process.Start`.
  - **Redirecting stdin changes FFmpeg's behaviour.** FFmpeg reads stdin when it is a pipe. Every `ArgumentList` in this file must be audited for `-nostdin`; if present it must be REMOVED on the processes that are meant to accept `q`, and if absent nothing else may start consuming that pipe.
- Public Interface Parity: `Cancel()`, `Dispose()`, `RunAsync(CancellationToken)`, `WasCanceled`, `LastFailure`, `CompletionWarning` keep exact signatures, return types and exception contracts (`Cancel` and `Dispose` remain non-throwing).

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. Add `RedirectStandardInput = true` to the FFmpeg `ProcessStartInfo` at lines 1643-1650 and 2466-2473, mirroring `MergerWorker.cs:1081` including its comment. Leave the ffprobe/thumbnail `ProcessStartInfo` blocks (2153, 2704, 2862) non-interactive and pass `attemptQuitCommand: false` for those.
2. Replace the body of `ReadExitCodeSafely` (lines 359-370) with a call to `GracefulProcessTerminator.Terminate(proc, logTag, attemptQuitCommand: <true for encoders, false for probes>, cooperativeGraceMs: graceMs, hardKillConfirmMs: 2000)`, keeping the outer `try`/`catch` and the exit-code fallback at lines 374-379 verbatim.
3. Replace each `cancellationToken.Register(() => proc.Kill(...))` (lines 1698-1702, 2489-2492) with a registration that launches the async ladder without blocking the registration callback — follow `MergerWorker.cs:175` (`_shutdownTask = Task.Run(() => GracefulProcessTerminator.TerminateAsync(proc, tag, attemptQuitCommand: true))`) and await that task before the method returns so teardown is not abandoned.
4. Route `Cancel()` (lines 340-347) and `Dispose()` (lines 3104-3108) through the same ladder, preserving `Cancel`'s existing exception filters and `Dispose`'s guarantee that the tree is dead before it returns.
5. Ensure `CleanupTwoPassArtifacts` runs on the cancelled/killed two-pass path as well as the success path.
6. Do not introduce a second copy of the ladder: `ProcessWorker.ReadExitCodeSafely` and `MergerWorker.ReadExitCodeSafely` must end up byte-identical in behaviour. Prefer extracting one shared static helper over editing two copies.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: Zero compiler/linter warnings or type errors.
- Behavioral Validation:
  - Cancel a single-pass export at ~50% progress; the partial file MUST be a playable, `moov`-complete MP4 (verify with `ffprobe -v error -show_format` returning exit code 0), not a headerless `mdat`.
  - Cancel a two-pass export during pass 1 and during pass 2; no `*-0.log` / `*-0.log.mbtree` remain in the temp root.
  - Dispose a worker WITHOUT calling `Cancel()` first (ISSUE_11 case): no FFmpeg process survives, confirmed by process enumeration.
  - Cancel with FFmpeg already hung/unresponsive: the ladder MUST still return within `CooperativeGraceMs + HardKillConfirmMs` and the tree MUST be dead — the grace period must not become an unbounded wait.
  - `TryRescueFinishedRender` is never handed an unfinalized file on the cancellation path.
- Performance Check: Cancellation latency stays bounded at 1500 ms + 2000 ms worst case; no new blocking call on the UI thread; `_currentProcess` is never read outside the `_procGate` accessors.
