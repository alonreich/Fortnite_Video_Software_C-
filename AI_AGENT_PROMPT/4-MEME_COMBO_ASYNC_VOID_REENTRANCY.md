> **STATUS: APPLIED 2026-09-19.** The fix described below is already in the tree. This file is
> retained as the rationale record for the change, NOT as an outstanding task. Do not re-run it.
> Verification (section 5) has NOT been executed — the test cases listed there are still outstanding.

# TASK SPECIFICATION: 4 - MEME_COMBO_ASYNC_VOID_REENTRANCY

## 1. TARGET CONTEXT & SCOPE
- File Path: `src/FortniteVideoSoftware.App/MainWindow.axaml.cs`
- Target Range: Lines 330 to 367 (`PopulateMemeComboBox`, `ApplyMemeItemsToCombo`), with subscription sites at `src/FortniteVideoSoftware.App/MainWindow.Wireup.cs:1039-1044` and the post-download call in `RunCloudMemeSyncAsync`
- Defect Classification: Race Condition (out-of-order async completion overwrites current state; unobserved exception on an `async void` event handler)

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism:
  1. **No single-flight gate, no generation token.** `private async void PopulateMemeComboBox()` (line 330) awaits `MemeManagementService.ScanMemesAsync()` (line 335) and then unconditionally assigns the result to the `_memeItems` field and rebuilds the ComboBox's `ItemsSource`. There are at least three concurrent entry points into it — `this.Loaded` (MainWindow.Wireup.cs:1044), the static `MemeDirectory.Changed` event (MainWindow.Wireup.cs:1039, raised from `SettingsWindow.axaml.cs:697`), and `RunCloudMemeSyncAsync` after a download batch — and each returns to the caller at the first `await`. Two overlapping invocations therefore both resume, and whichever `ScanMemesAsync` finishes LAST wins the write, regardless of which was started last. Because a scan's duration is proportional to library size (see Rank 3), the SLOWER, older scan reliably wins when the fast one was started second: the user changes the meme folder, the new folder's short scan completes first, and the old folder's long scan then overwrites `_memeItems` with the previous directory's contents. The combo then lists memes that are no longer in the active directory, and selecting one hands a stale `FullPath` into the export payload.
  2. **`_pendingMemeRestorePath` is consumed by the wrong completion.** Lines 338-345 read the field, null it, and search `_memeItems` for a match. This is a read-modify-write of shared state with no ordering guarantee: the FIRST invocation to resume clears the field, so if a second, later invocation is the one that ultimately wins the `_memeItems` write, the recovery-restore selection is silently dropped. The user's restored project comes back with no meme selected and no error anywhere.
  3. **`async void` on an event handler swallows nothing and observes nothing.** `MemeDirectory.NotifyChanged` (SettingsManager.cs:293) wraps the `Changed?.Invoke()` in `try/catch -> RuntimeLog.Swallowed`, but that catch only covers the SYNCHRONOUS prefix of the handler — everything up to the first `await`. Any exception thrown after line 335 resumes (for example from `ApplyMemeItemsToCombo`: `cb.ItemTemplate` assignment, `MemeManagementService.CreateMemeItemTemplate`, or the `ItemsSource` set) escapes an `async void` method onto the dispatcher as an unhandled exception and terminates the process. The subscription at MainWindow.Wireup.cs:1039 (`MemeDirectory.Changed += PopulateMemeComboBox`) binds the `async void` method group directly to the event, which is exactly the shape that makes this unobservable.
  4. **`ItemsSource` is rebuilt wholesale on every pass.** `ApplyMemeItemsToCombo` (lines 347-367) allocates a fresh `List<MemeItem>`, reassigns `ItemTemplate`, and reassigns `ItemsSource` on every invocation, forcing Avalonia to tear down and re-realise every container. Under the reentrancy above this happens two or more times back to back, and the `prev`-based selection restore (lines 358-366) is then racing its own second run.
- Target Pattern: A monotonic generation counter (`Interlocked.Increment`) captured before the `await` and re-checked after it, gating every write to `_memeItems`, `cb.ItemsSource`, and `_pendingMemeRestorePath`; an `async Task` core with a thin `async void` adapter that has a terminal catch; and `Dispatcher.UIThread.VerifyAccess()` before touching controls.

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior:
  - **The two synthetic download-action rows** appended in `ApplyMemeItemsToCombo` (lines 355-358: `IsDownloadAction = true` for `"mp4"` and `"jpeg"`) must remain the last two entries, must keep their exact `DownloadCategory` strings, and must never be selectable as a real meme. The selection-restore filter already excludes them via `!m.IsDownloadAction` (line 362) and `!prev.IsDownloadAction` (line 360) — both guards must survive.
  - **`preserveSelection` semantics**: the caller-controlled flag and the `FullPath`-ordinal-ignore-case re-match (lines 359-365) are how the user's current pick survives a rescan. Do not replace `FullPath` matching with reference or index matching.
  - **The `_pendingMemeRestorePath` dual match**: lines 341-343 match on EITHER `FullPath` OR `Path.GetFileName(p)`, ordinal-ignore-case. The filename fallback is what lets a recovered project re-bind a meme after the user moved their meme folder. Keep both arms and their order.
  - **Null-control tolerance**: both methods early-return when `FindControl<ComboBox>("MemeComboBox")` is null (lines 332-333, 349-350). This path is reachable during teardown and must stay non-throwing.
  - The unsubscribe on `Closed` (MainWindow.Wireup.cs:1040) must continue to detach the handler from the static event; do not change the handler identity in a way that breaks `-=` symmetry.
- Public Interface Parity: Maintain exact public signatures, return types, and exceptions unless explicitly instructed. If `PopulateMemeComboBox` is re-shaped, keep a parameterless member with the same name and an `Action`-compatible signature so `MemeDirectory.Changed += PopulateMemeComboBox` and its matching `-=` both still bind.

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. Isolate the target execution path within lines 330-367.
2. Introduce `private int _memeScanGeneration;`. Extract the body into `private async Task PopulateMemeComboBoxAsync()`:
   - `int generation = Interlocked.Increment(ref _memeScanGeneration);` BEFORE the await.
   - `var scanned = await MemeManagementService.ScanMemesAsync();`
   - `if (Volatile.Read(ref _memeScanGeneration) != generation) return;` — a superseded scan writes nothing at all: not `_memeItems`, not `ItemsSource`, not `_pendingMemeRestorePath`.
   - Only past that gate, assign `_memeItems`, call `ApplyMemeItemsToCombo(preserveSelection: true)`, and run the `_pendingMemeRestorePath` block.
3. Keep `private void PopulateMemeComboBox()` as the event-compatible member, implemented as a fire-and-forget adapter with a terminal handler so nothing can reach the dispatcher unobserved:
   `_ = PopulateMemeComboBoxAsync().ContinueWith(t => RuntimeLog.Fail("Memes", t.Exception!), TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);`
   Do not leave any `async void` in this path.
4. Add `Dispatcher.UIThread.VerifyAccess()` (or marshal via `Dispatcher.UIThread.InvokeAsync`) at the top of `ApplyMemeItemsToCombo`, since `MemeDirectory.Changed` is a static event with no documented thread affinity and is raised from `SettingsWindow`.
5. Wire the preserved legacy behaviors into the new execution path: the download-action rows, the `preserveSelection` `FullPath` re-match, and the `_pendingMemeRestorePath` `FullPath`-or-filename dual match all move across unchanged and now execute only on the winning generation.
6. Ensure deterministic error propagation and structured logging without silent swallows: log one `RuntimeLog.Info("Memes", ...)` line when a scan is discarded as superseded, naming both generation numbers, so the race is observable rather than inferred.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: Zero compiler/linter warnings or type errors; no `async void` remains in the meme-combo path.
- Behavioral Validation:
  - Test with an injectable scan delegate: start scan A (slow, returns library X), then scan B (fast, returns library Y). Assert the final `_memeItems` is Y and that A's completion performs no write.
  - Test that `_pendingMemeRestorePath` is consumed exactly once, by the winning generation, and that the restored selection matches on `FullPath` and, separately, on filename-only.
  - Test that an exception thrown from the template factory is routed to `RuntimeLog.Fail` and does not reach `AppDomain.CurrentDomain.UnhandledException`.
  - Regression: the two download-action rows are still present, last, and unselectable as memes; the current selection still survives a rescan that leaves the file in place.
- Performance Check: Verified elimination of lock contention, allocation spikes, or thread blocks — a superseded scan must perform ZERO `ItemsSource` reassignments, measurable as container-realisation count in the test harness.
