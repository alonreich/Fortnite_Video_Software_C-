> **STATUS: APPLIED 2026-09-19.** The fix described below is already in the tree. This file is
> retained as the rationale record for the change, NOT as an outstanding task. Do not re-run it.
> Verification (section 5) has NOT been executed — the test cases listed there are still outstanding.

# TASK SPECIFICATION: 2 - MPV_SAFETY_CTS_DISPOSE_RACE

## 1. TARGET CONTEXT & SCOPE
- File Path: `src/FortniteVideoSoftware.Core/Media/MPVSafetyManager.cs`
- Target Range: Lines 84 to 137 (`SeekProcessorLoop` and `Dispose`), with the thread construction at lines 28-34 and 43-49
- Defect Classification: Race Condition / Catastrophic Failure Mode (unhandled exception from `async void` terminates the process; native handle use-after-free)

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism:
  1. **CTS disposed under live consumers.** `Dispose()` (lines 132-136) executes `_cts.Cancel()` and `_cts.Dispose()` back to back with nothing in between. At that instant two threads are still bound to `_cts.Token`: `WorkerLoop` polls `_cts.IsCancellationRequested` on a 500 ms `Thread.Sleep` cycle (lines 55-79), and `SeekProcessorLoop` is suspended inside `_seekChannel.Reader.ReadAllAsync(_cts.Token)` (line 90) or `Task.Delay(50 - elapsedMs, _cts.Token)` (line 95). Cancellation callbacks and token registrations are still unwinding when the source's internal state is freed, so those consumers observe `ObjectDisposedException`, not `OperationCanceledException`. This is the exact defect the sibling class documents and fixed under `IPCTEARDOWN_01` (`Ipc/NamedPipeStateServer.cs`): signal, wait for the consumer to actually exit, and only then free what it was using.
  2. **The exception escapes into nothing.** `SeekProcessorLoop` is declared `private async void` (line 84) and its only handler is `catch (OperationCanceledException)` (lines 126-129). `ObjectDisposedException` is not caught. An exception escaping an `async void` method is rethrown on the captured `SynchronizationContext` — here there is none, because the method is started from a raw `Thread` (lines 43-47) — so it lands on the thread pool as an unobserved top-level throw and terminates the process under the .NET default policy. `WorkerLoop` by contrast DOES guard this exact case (`catch (ObjectDisposedException) { }`, line 80), which shows the hazard was recognised on one loop and missed on the other.
  3. **Native handle with no ownership contract.** `_mpvHandle` (line 9) is a bare `nint` copied in at construction. `MpvWrapper.mpv_command_string(_mpvHandle, ...)` at line 111 runs on the seek thread with no check that the handle is still live and no coordination with whoever owns it. `MpvIpcClient.Dispose` (Media/MpvIpcClient.cs) goes to considerable lengths — `mpv_wakeup`, a 3-second `Join`, and the `_eventLoopExited` flag — precisely so it never destroys a handle underneath a live thread; this class has none of that. A `mpv_command_string` on a destroyed handle is a native access violation (0xC0000005), not a managed exception, so no `catch` in this file can contain it.
  4. **Dispose never joins.** Neither `_workerThread` nor the inner `seekThread` is joined, so `Dispose()` returns while both are still running and still touching `_cts` and `_mpvHandle`.
  5. **Watchdog is decorative.** The "MPV WATCHDOG TRIPPED" branch (lines 66-76) resets `_isSeeking` and writes to `Console.Error` — in a windowed Avalonia app there is no console, so a tripped watchdog produces no artifact anywhere, and it takes no corrective action on the stuck seek. It does not route through `CoreLogger`, unlike every other diagnostic in this assembly.
- Target Pattern: The `IPCTEARDOWN_01` ordering already proven in `Ipc/NamedPipeStateServer.Dispose` — stop producing, signal cancellation, WAIT (bounded) for every consumer thread to exit, then dispose the source. Plus: `async Task` + an explicit `Task` handle instead of `async void`, an ownership flag mirroring `MpvIpcClient._ownsHandle`/`_eventLoopExited`, and `CoreLogger` in place of `Console.Error`.

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior:
  - The bounded channel with `BoundedChannelFullMode.DropOldest` and capacity 1 (lines 22-26) is the coalescing policy for rapid scrubbing: only the NEWEST seek target survives a burst. Preserve capacity 1 and DropOldest exactly; a larger buffer or DropWrite would reintroduce the seek storm this class exists to absorb.
  - The 50 ms debounce floor between issued seeks (lines 91-97) and the 2.5 s stuck-seek threshold (lines 61-65) are tuned constants. Keep both values; only change how the watchdog REPORTS and RECOVERS, not when it trips.
  - `RequestSeek` must remain fire-and-forget and non-blocking (`TryWrite`, line 39) — it is called from input handlers and must never block a UI thread.
  - Both threads must stay `IsBackground = true` with their existing names (`MPV_Safety_Watchdog`, `MPV_Seek_Processor`); the names appear in crash digests.
  - The `absolute-percent` seek semantics and `"F1"` / `InvariantCulture` formatting (lines 109-111) are load-bearing — mpv parses the command string with the invariant decimal point.
  - `Dispose()` must remain idempotent and must never throw, including when called twice or after the handle owner has already gone away.
- Public Interface Parity: Maintain exact public signatures, return types, and exceptions unless explicitly instructed. `MPVSafetyManager(nint)`, `RequestSeek(double)`, and `Dispose()` are consumed by `src/FortniteVideoSoftware.App/Phase3Gate.cs` (lines 10, 21) and must keep their shapes.

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. Isolate the target execution path within lines 84-137.
2. Convert `SeekProcessorLoop` from `async void` to `private async Task SeekProcessorLoop()` and store the returned `Task` in a field. Start it with `Task.Run` (or keep the dedicated thread via `TaskCreationOptions.LongRunning`) rather than `new Thread(...)` so the loop is awaitable. Widen its catch to `catch (OperationCanceledException) { }` plus `catch (ObjectDisposedException) { }` plus a terminal `catch (Exception ex) { CoreLogger.Fail("MPV", ...) }` so no throw can ever escape.
3. Implement the teardown ordering, exactly as `NamedPipeStateServer.Dispose` documents under `IPCTEARDOWN_01`:
   a. Guard re-entry with an `_isDisposed` flag set via `Interlocked.Exchange`.
   b. `_seekChannel.Writer.TryComplete()` — stop producing.
   c. `_cts.Cancel()` — signal.
   d. `_workerThread.Join(TimeSpan.FromSeconds(2))` and await/wait the seek loop `Task` with the same bounded ceiling; log one line via `CoreLogger.Debug` if either misses the deadline.
   e. ONLY THEN `_cts.Dispose()`.
4. Add a handle-lifetime guard mirroring `MpvIpcClient`: a `volatile bool _handleValid` (or an `Invalidate()` method the owner calls before destroying the handle) checked immediately before `mpv_command_string` at line 111, and set false as the first action of `Dispose()`. If a bounded join times out, follow the `MpvIpcClient` precedent — abandon the handle and log, never command through it.
5. Replace `Console.Error.WriteLine("MPV WATCHDOG TRIPPED: ...")` at line 74 with `CoreLogger.Fail("MPV", ...)` so a tripped watchdog is visible in the runtime log, and record the stuck seek's target time in the message.
6. Ensure deterministic error propagation and structured logging without silent swallows: the bare `catch { ... _isSeeking = false; }` at lines 114-120 must log the swallowed exception through `CoreLogger.Swallowed(ex)`. Note that the `catch` and the `finally` below it both clear `_isSeeking` under the lock — collapse to the `finally` alone.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: Zero compiler/linter warnings or type errors. `CS1998`/`VSTHRD100`-class warnings about `async void` must be gone.
- Behavioral Validation:
  - New test: construct the manager over a stub handle, drive `RequestSeek` at >100 Hz from another thread, call `Dispose()` mid-burst, and assert the process does not raise an unhandled exception and both named threads have exited within the join ceiling. Run it under a first-chance `AppDomain.CurrentDomain.UnhandledException` assertion.
  - New test: `Dispose()` called twice, and `RequestSeek` called after `Dispose()`, are both no-ops that do not throw.
  - Regression: a burst of 50 `RequestSeek` calls inside 100 ms still results in ONE issued `mpv_command_string`, proving DropOldest + the 50 ms debounce are intact.
- Performance Check: Verified elimination of lock contention, allocation spikes, or thread blocks — `RequestSeek` must remain allocation-free and non-blocking on the caller's thread.
