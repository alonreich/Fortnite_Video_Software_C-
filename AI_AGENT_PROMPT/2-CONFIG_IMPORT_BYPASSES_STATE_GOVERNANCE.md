> **STATUS: APPLIED 2026-09-19.** The fix described below is in the tree. This file is retained as
> the rationale record for the change, NOT as an outstanding task. Do not re-run it.
> Verification (section 5) has NOT been executed — the test cases listed there are still outstanding,
> and the solution has NOT been compiled since the edit (no .NET SDK was available to the auditor).

# TASK SPECIFICATION: 2 - CONFIG_IMPORT_BYPASSES_STATE_GOVERNANCE

## 1. TARGET CONTEXT & SCOPE
- File Path: `src/FortniteVideoSoftware.App/Services/SettingsBackupService.cs`
- Target Range: Lines 20-46 (`ExportSettingsAsync`) and 49-89 (`ImportSettingsAsync`); the write is line 75, the validation is line 73, the exit is line 81. Secondary read-only references: `src/FortniteVideoSoftware.Core/Ipc/StateTransferStore.cs:145-260, 386-435, 483-588` and `src/FortniteVideoSoftware.Core/Infrastructure/AtomicJsonFile.cs:84-120`.
- Defect Classification: Data Corruption (unvalidated, non-atomic, unlocked write to the governed session-state file)

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism: `ImportSettingsAsync` takes a file the user picked off disk and writes its bytes verbatim into `paths.SessionStateFile`. Four independent guards that every other writer to that file is required to pass are skipped in one line.
  1. **Schema validation is skipped.** The only check is `if (state["schema_version"] == null) throw` (line 73). `StateTransferStore` exists to stop exactly this: `SanitizeObject` (line 386) walks every property through `TryAcceptProperty` (435) and `ValidateKnownProperty` (483), which enforce per-key types, numeric bounds, `BoundsKeys` shape (`ValidateBoundsObject`, 575) and `SubprocessStateKeys` shape (`ValidateSubprocessState`, 556). A file with the right two words at the top and arbitrary junk below is accepted here and lands on disk unfiltered. Every later reader — the Main App, the Merger, the Crop Tool, `WindowBoundsHelper` — then consumes values that no validator ever saw.
  2. **The cross-process mutex is skipped.** Every read and write in `StateTransferStore` is wrapped in `NamedSystemMutex.Acquire(MutexName, ...)` (lines 96, 128, 165, 202, 244). This write takes nothing. A sibling process mid-`LoadUnlocked` under that mutex gets a torn read, and its next save overwrites the import entirely.
  3. **Atomicity and durability are skipped.** `File.WriteAllText` is not the protocol `docs/05_SYSTEM_LIFECYCLE_STORAGE.md#SYS-RECOVERY` mandates and that `AtomicJsonFile.WriteCore` implements (unique GUID temp file in the target directory -> `FileOptions.WriteThrough` -> `Flush(flushToDisk: true)` -> atomic `File.Move(overwrite: true)`). A failure mid-write leaves the user with neither their old configuration nor the imported one. This is the precise hazard `MemeDimensionCache.cs:45` already calls out by name: "which is what leaves a correctly named but zero-filled file".
  4. **`Environment.Exit(0)` six lines later (line 81) removes the last chance to recover.** `File.WriteAllText` returns when the bytes reach the OS cache, not the platter. `Environment.Exit` runs no finalizers and flushes no OS write cache. On a machine that loses power in that window the file is whatever NTFS happened to have committed.
  5. **A live `NamedPipeStateServer` silently wins anyway.** `StateTransferStore.SaveAsync`/`LoadAsync` prefer `NamedPipeStateServer.ActiveInstance` over the file (lines 70-75, 146-151). If this process or a sibling is hosting that server, it holds session state in memory and will overwrite the imported file on its next save. The import can therefore appear to succeed and be silently discarded, which is worse than failing.
  6. **`ExportSettingsAsync` has the mirror defect.** `File.Copy(stateFile, ...)` at line 37 takes no mutex, so the backup the user just made can be a torn snapshot of a file another process was mid-write on.
- Target Pattern: Parse -> validate through `StateTransferStore.SanitizeObjectInternal` (the public test seam already exposed at line 53) -> write through `StateTransferStore.SaveAsync`/`SaveSync`, which already owns the mutex, the sanitiser and `AtomicJsonFile`. For the export side, read through `StateTransferStore.LoadSync()` and serialise that, rather than byte-copying a file that may be mid-write.

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior:
  - **The `schema_version` gate (line 73) stays, with its exact user-facing message** ("Missing schema_version lock. This is not a valid Fortnite Video Software configuration file."). It is the only thing distinguishing a real backup from an arbitrary `.json`, and its wording is what the user sees. Sanitisation is ADDED after it, not substituted for it.
  - **`onBeforeExit()` must still run before `Environment.Exit(0)`.** Its callers pass `_recovery.MarkCleanShutdownIntent(); ShutdownVideoPipeline();` (MainWindow.axaml.cs:3000-3005). RECOVERY_02 requires `MarkCleanShutdownIntent` to be recorded before the process dies, or the next launch reports a crash that never happened.
  - **Both methods are non-throwing at the UI boundary.** Every failure path currently ends at `RuntimeLog.Fail("Config", ...)` plus `NativeDialog.ShowError(...)` and returns `false`. A new validation rejection must take that same path, not surface an exception into the dialog handler.
  - **Empty/invalid JSON still reports "File is empty or invalid JSON."** — preserve that distinct message.
  - **`ExportSettingsAsync`'s "Configuration successfully backed up." confirmation only fires when a file actually existed** (`if (File.Exists(stateFile))`). Silent no-op on a missing state file is existing behaviour; do not turn it into an error.
- Public Interface Parity: `ExportSettingsAsync(Window, ApplicationPaths)` and `ImportSettingsAsync(Window, ApplicationPaths, Action)` keep exact signatures, `Task`/`Task<bool>` return types, and their current non-throwing contract.

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. In `ImportSettingsAsync`, after the existing `schema_version` check at line 73, pass the parsed `JsonObject` through `StateTransferStore.SanitizeObjectInternal(state, "config-import")`. Log every rejected key via `RuntimeLog` so a stripped import is diagnosable rather than silent.
2. Replace `File.WriteAllText(paths.SessionStateFile, json)` (line 75) with `await new StateTransferStore(paths).SaveAsync(sanitized)`, so the write inherits the named mutex, the sanitiser and `AtomicJsonFile`'s WriteThrough + flush + atomic rename. Do not hand-roll a second copy of that protocol here.
3. Before writing, stop any `NamedPipeStateServer.ActiveInstance` in this process (or route the write through it) so the in-memory copy cannot overwrite the import on its next save.
4. Keep `onBeforeExit()` immediately before `Environment.Exit(0)`, and only reach the exit after the save has been confirmed complete.
5. In `ExportSettingsAsync`, replace the raw `File.Copy` (line 37) with a `StateTransferStore(paths).LoadSync()` + serialise-to-the-chosen-path, so the backup is a consistent snapshot taken under the mutex.
6. Propagate failures through the existing `RuntimeLog.Fail` + `NativeDialog.ShowError` path only.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: Zero compiler/linter warnings or type errors.
- Behavioral Validation:
  - Import a valid backup: settings restore exactly as before; a round trip export -> import is a byte-stable no-op on the sanitised document.
  - Import a file containing `{"schema_version":1,"UnknownKey":{"deeply":"nested"},"MainVolume":"not-a-number"}`: the unknown key and the malformed value are rejected and logged; the app still starts cleanly afterwards; no exception reaches the dialog.
  - Import with no `schema_version`: the original message is still shown verbatim and the on-disk state file is UNCHANGED.
  - Kill the process during the write (debugger or fault injection): the state file is either fully the old content or fully the new content — never truncated, never zero length.
  - With a second app instance running: the import is not silently discarded by the other process's next save.
  - After import, `CheckFault()` on the next launch does NOT report a crash (RECOVERY_02 intent marker survived).
- Performance Check: No blocking disk or mutex wait on the UI thread beyond the existing `StateTransferStore` timeouts; no new lock is introduced and the named mutex is never held across a user prompt.
