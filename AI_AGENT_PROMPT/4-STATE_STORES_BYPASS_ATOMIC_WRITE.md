> **STATUS: APPLIED 2026-09-19.** The fix described below is in the tree. This file is retained as
> the rationale record for the change, NOT as an outstanding task. Do not re-run it.
> Verification (section 5) has NOT been executed — the test cases listed there are still outstanding,
> and the solution has NOT been compiled since the edit (no .NET SDK was available to the auditor).

# TASK SPECIFICATION: 4 - STATE_STORES_BYPASS_ATOMIC_WRITE

## 1. TARGET CONTEXT & SCOPE
- File Path: `src/FortniteVideoSoftware.App/Infrastructure/MemePlacementStore.cs` (primary), `src/FortniteVideoSoftware.Core/Infrastructure/UiStateStore.cs` (same defect class)
- Target Range: `MemePlacementStore.cs` lines 40-72 (`_cache` / `Load`) and 91-111 (`Set`, write at line 104); `UiStateStore.cs` lines 100-110 (`WriteText`, write at line 104). Reference-only: `src/FortniteVideoSoftware.Core/Infrastructure/AtomicJsonFile.cs:56-120` and `src/FortniteVideoSoftware.App/Infrastructure/MemeDimensionCache.cs:40-50`.
- Defect Classification: Data Corruption (non-atomic whole-file rewrite of persisted user state)

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism:
  1. **`Set` rewrites the ENTIRE map on every single call, non-atomically.** Line 100 rebuilds a fresh `JsonObject` from every entry in `map`, and line 104 commits it with `File.WriteAllText(path, obj.ToJsonString())`. `File.WriteAllText` opens with truncation: from the moment it opens until the last byte is written, the on-disk file is shorter than the document it is replacing. A crash, a forced process kill or a power loss inside that window leaves a truncated or zero-length `meme_placement.json` — and because the write is whole-map, the loss is not the one entry being changed, it is **every remembered placement the user has ever set**.
  2. **This is the exact failure the project already named and fixed elsewhere.** `MemeDimensionCache.cs:45` documents it verbatim: "`File.WriteAllText`, which is what leaves a correctly named but zero-filled file after a ...". `AtomicJsonFile.WriteCore` is the sanctioned protocol required by `docs/05_SYSTEM_LIFECYCLE_STORAGE.md#SYS-RECOVERY`: unique GUID temp file in the TARGET directory -> `FileOptions.WriteThrough` -> `Flush(flushToDisk: true)` -> `File.Move(temp, path, overwrite: true)` (atomic same-volume NTFS rename). `StateTransferStore`, `RecoveryManager`, `SettingsManager`, `CropConfigStore` and `MemeDimensionCache` all route through it. These two stores do not, and there is no comment in either file claiming an exemption.
  3. **No durability barrier.** `File.WriteAllText` returns when the bytes reach the OS cache, not the platter — the identical point `SettingsManager.cs:658` makes about its own former implementation. `Environment.Exit(0)` (used on several app shutdown paths) runs no flush, so an acknowledged save can still be absent after a hard power loss.
  4. **`_cache` is unsynchronised static mutable state with a non-atomic publish.** `private static Dictionary<string, MemePlacement>? _cache;` (line 40) is read at line 44, populated at 45-69 and published at line 70. `Load()` is not reentrancy-safe and takes no lock: two concurrent callers each build a full map and the second publish wins, and `Set` mutates the published dictionary in place at line 97 while any concurrent `Get` (line 80) is enumerating it. Today every caller (`MainWindow.Export.cs:373`, `MainWindow.Wireup.cs:1133, 1153`) is on the UI thread, so this is currently latent — but nothing in the type's signature, name or documentation says so, and the first background caller turns it into a `Dictionary` corruption or an `InvalidOperationException` from a failure path.
  5. **`UiStateStore.WriteText` (line 104) has defects 1 and 3 identically**, in a `Core/Infrastructure` type governed by spec 05, with `AtomicJsonFile` sitting in the same namespace.
- Target Pattern: `AtomicJsonFile.WriteObject` / `AtomicJsonFile.WriteText` (the `ATOMICTEXT_01` overload, added precisely so callers holding a fully formed document do not have to reshape it) for the commit, plus a `lock` or `Lazy<T>` around `_cache`'s initialisation and mutation so the type is correct independently of which thread calls it.

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior:
  - **MEME_02's three-level precedence (documented at lines 25-30) is the contract of this type and must be bit-for-bit identical after the change:** (1) the user's own choice for this exact file always wins; (2) otherwise `MemeAssets.DefaultsToStart`; (3) otherwise `MemePlacement.End`. `Get`'s early return for null/whitespace (line 77) and its `Path.GetFileName` keying (line 78) are part of that contract.
  - **`StringComparer.OrdinalIgnoreCase` on both the in-memory map (line 45) and the key lookup** — Windows paths are case-insensitive and existing files on users' disks depend on this. Do not change the comparer.
  - **The lenient legacy value parser (lines 57-61) must survive unchanged.** It accepts `"1"`, `"Start"` and `"true"` as Start and treats everything else as End. Files written by older builds contain these variants; tightening this silently resets users' choices.
  - **Writes stay `"Start"` / `"End"` (line 100)**, not `1`/`0` and not booleans, so files written by the new code are still readable by an older build.
  - **Both methods are total and non-throwing.** Every failure currently degrades to `RuntimeLog.Info("Meme", ...)` and continues with defaults; a read failure must still yield "no remembered choices", never an exception at the call site.
  - **`UiStateStore.WriteText` must remain "silently no-ops on failure"** (its documented contract) and `ReadText` must keep returning `fallback` on any problem.
  - **`MigrateLegacyFilesOnce()` must still run exactly once, from `PathFor`** (UiStateStore.cs:76) — do not reorder it out of the write path.
- Public Interface Parity: `MemePlacementStore.Get(string)`, `.Set(string, MemePlacement)`, `.ContradictsShippedDefault(string, MemePlacement)` and `UiStateStore.ReadText/WriteText/ReadInt/WriteInt` keep exact signatures, return types and non-throwing contracts. The `MemePlacement` enum's numeric values (`End = 0`, `Start = 1`) are persisted semantics — do not renumber.

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. Add a `private static readonly object _sync = new();` to `MemePlacementStore` and place `Load()`'s check-build-publish (lines 44-71) and `Set`'s map mutation + document build (lines 96-100) inside it. Follow `MemeDimensionCache`'s pattern: build the snapshot under the lock, do the disk write OUTSIDE it so a slow disk never serialises `Get` on the export path.
2. Replace line 104 with `AtomicJsonFile.WriteText(path, obj.ToJsonString())` (the `ATOMICTEXT_01` overload) so the temp-file + `WriteThrough` + `Flush(flushToDisk: true)` + atomic rename protocol is used. Do not hand-roll it.
3. Keep `Directory.CreateDirectory(Path.GetDirectoryName(path)!)` before the write — `AtomicJsonFile` places its temp file in the target directory and needs it to exist.
4. Apply step 2 to `UiStateStore.WriteText` (line 104), preserving its no-op-on-failure contract and leaving `PathFor`/`MigrateLegacyFilesOnce` untouched.
5. Verify no other `File.WriteAllText` on a persisted state file remains outside `AtomicJsonFile`. `RecoveryManager`'s sentinel/lock/intent markers (lines 57, 220, 253) are deliberately excluded: they are single-line ownership stamps whose absence is the safe default, and they must NOT be converted.
6. Add the `// [SPEC CONTRACT] STRICT GOVERNANCE:` header block to `MemePlacementStore.cs` if it is missing — `SPEC_GOVERNANCE.md` §4 requires it on every production source file under `src/`.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: Zero compiler/linter warnings or type errors.
- Behavioral Validation:
  - Round trip: `Set(fileA, Start)`, restart the process, `Get(fileA)` returns `Start`.
  - Precedence intact: a bundled meme listed in `MemeAssets.DefaultsToStart` with an explicit user `End` returns `End`; an unknown meme with no stored entry returns `End`; a bundled meme with no stored entry returns `Start`.
  - Legacy parse intact: a hand-written file containing `"1"`, `"true"` and `"Start"` for three different keys resolves all three to `Start`; case-differing keys still resolve.
  - Kill the process during `Set` (debugger or fault injection): `meme_placement.json` is either fully the old document or fully the new one — never truncated, never zero length, and no `.tmp` residue accumulates in the UI-state directory.
  - The same truncation test passes for `UiStateStore.WriteText`.
  - A `Get` concurrent with a `Set` (forced onto a background thread in a test) neither throws nor returns a torn map.
- Performance Check: No disk I/O executes while `_sync` is held; `Get` on the export hot path takes the lock only for a dictionary lookup; no new allocation per `Get`.
