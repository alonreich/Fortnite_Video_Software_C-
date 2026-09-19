> **STATUS: APPLIED 2026-09-19.** The fix described below is in the tree. This file is retained as
> the rationale record for the change, NOT as an outstanding task. Do not re-run it.
> Verification (section 5) has NOT been executed — the test cases listed there are still outstanding,
> and the solution has NOT been compiled since the edit (no .NET SDK was available to the auditor).

# TASK SPECIFICATION: 3 - RESCUE_PATH_TOCTOU_DUPLICATION

## 1. TARGET CONTEXT & SCOPE
- File Path: `src/FortniteVideoSoftware.Core/Media/ProcessWorker.cs` (primary), `src/FortniteVideoSoftware.Core/Media/MergerWorker.cs` (identical twin)
- Target Range: `ProcessWorker.cs` lines 2325-2352 (`TryRescueFinishedRender`), call site line 2086; `MergerWorker.cs` lines 1354-1381 (`TryRescueFinishedRender`), call site line 805. Reference-only: `ProcessWorker.cs:2353-2398` (`ResolveOutputPath`, the OUTPATH_01 fix these two never received).
- Defect Classification: Data Corruption (TOCTOU on the last surviving copy of a finished render)

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism:
  1. **`File.Exists` loop, then `File.Move` — the exact defect OUTPATH_01 documents and fixed twenty lines below.** The rescue picks a name with `while (File.Exists(rescued)) { rescued = ...-{n}.mp4; n++; }` and only afterwards calls `File.Move(corePath, rescued)`. The comment on `ResolveOutputPath` (ProcessWorker.cs:2353-2372) spells out why that shape is wrong: "This was a File.Exists scan: check, then return the name, then write to it much later. Two pipelines running at once ... both saw the same index free". `ResolveOutputPath` was rewritten to `FileMode.CreateNew` + `FileShare.None` — an atomic create-or-fail at the filesystem level. `TryRescueFinishedRender` was not.
  2. **The consequence here is strictly worse than it was for `ResolveOutputPath`.** `ResolveOutputPath` feeds a `File.Move(..., overwrite: true)`. This one calls the two-argument `File.Move(string, string)`, which **throws `IOException` when the destination exists**. The throw is caught at line 2346, `null` is returned, and the finished render — the only copy, which is precisely why it is being rescued — is left at a path the failing pipeline is about to clean up. The user loses a completed export, silently, with one `CoreLogger.Fail` line as the only trace.
  3. **The collision window is real, not theoretical.** The name is `Fortnite-Video-RECOVERED-{yyyyMMdd-HHmmss}.mp4` — one-second granularity, shared by every process using the same `_paths.TempDirectory`. Cancel-then-restart (the scenario OUTPATH_01 was written for) and Main App + Merger running concurrently both put two rescues inside the same second.
  4. **The `n` loop is unbounded.** `ResolveOutputPath` explicitly added an iteration ceiling so "a directory that cannot be written to (permissions, a full disk, an offline network share) fails loudly after a bounded number of attempts instead of spinning forever inside the export." The rescue loop has no ceiling: on a directory where `File.Exists` keeps returning true it spins indefinitely inside a failure handler.
  5. **Two byte-identical copies.** `ProcessWorker.TryRescueFinishedRender` and `MergerWorker.TryRescueFinishedRender` differ only in the filename prefix (`Fortnite-Video-RECOVERED-` vs `Merged-Videos-RECOVERED-`), the parameter name, and the log tag. This is the same duplication that let `ReadExitCodeSafely` diverge between the same two files (see Finding 1); any fix applied to one copy will silently miss the other.
- Target Pattern: The project's own OUTPATH_01 primitive — atomic name reservation via `new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None)` with a bounded attempt ceiling — followed by `File.Move(source, reserved, overwrite: true)`. Extracted once into a shared static helper (e.g. `Core/Media/RescuePath.cs` or an overload of the existing reservation helper) and called by both workers with their prefix and log tag as parameters.

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior:
  - **`if (!File.Exists(corePath)) return null;`** is the "nothing to rescue" short-circuit and must stay the first statement.
  - **`Directory.CreateDirectory(_paths.TempDirectory)`** must stay: the temp root is not presumed to exist, and callers reach this method on failure paths where it may have been cleaned.
  - **The method is total and never throws.** It is invoked from inside failure handling; an exception escaping it would replace a partial failure with a total one. Keep the outer `try`/`catch` and the `null` return.
  - **The two filename prefixes are user-visible and must not be unified.** `Fortnite-Video-RECOVERED-` and `Merged-Videos-RECOVERED-` tell the user which tool produced the file; a shared helper must take the prefix as a parameter, not impose one.
  - **The log tags `"Output"` and `"Merger"` and the two distinct messages** ("Could not preserve the finished render" / "Could not preserve the finished merge") are how crash digests attribute the failure. Preserve both.
  - **The `-{n}` suffix format stays** so existing user folders and any support documentation keep matching.
  - **The reserved zero-byte placeholder left by `FileMode.CreateNew` must be overwritten**, not deleted-then-moved: deleting it reopens the very race being closed. Use `File.Move(source, reserved, overwrite: true)`.
- Public Interface Parity: `private string? TryRescueFinishedRender(string)` keeps its signature, its nullable return, and its non-throwing contract in both classes.

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. Extract one shared static helper that takes `(string sourcePath, string tempDirectory, string filenamePrefix, string logTag)` and returns `string?`.
2. Inside it, reserve the destination with `FileMode.CreateNew` + `FileShare.None` in a loop bounded by the same ceiling `ResolveOutputPath` uses; treat `IOException` on `CreateNew` as "taken, try the next index" and any other exception as a hard failure.
3. Dispose the reservation stream, then `File.Move(sourcePath, reserved, overwrite: true)` so the zero-byte placeholder is replaced rather than raced.
4. On exhausting the ceiling, log through the caller-supplied tag and return `null` — the existing contract.
5. Replace both method bodies with a call to the helper, passing each caller's prefix and tag.
6. Audit for any third copy of the `File.Exists`-loop-then-`Move` shape elsewhere in `Core/Media` and fold it into the same helper.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: Zero compiler/linter warnings or type errors.
- Behavioral Validation:
  - Two concurrent rescues within the same clock second (Main App export + Merger, or cancel-then-restart) both succeed and produce two distinct files. Neither returns `null`, and neither overwrites the other.
  - A pre-existing `Fortnite-Video-RECOVERED-<stamp>.mp4` at the target name does NOT cause the rescue to fail.
  - A read-only or full temp directory fails after the bounded number of attempts, logs once, and returns `null` — it does not spin.
  - The rescued file is byte-identical to the source.
  - `MergerWorker`'s rescue still produces `Merged-Videos-RECOVERED-` names under the `"Merger"` log tag; `ProcessWorker`'s still produces `Fortnite-Video-RECOVERED-` under `"Output"`.
- Performance Check: No unbounded loop remains on any failure path; no new lock or blocking wait is introduced inside failure handling.
