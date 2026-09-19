> **STATUS: APPLIED 2026-09-19.** The fix described below is already in the tree. This file is
> retained as the rationale record for the change, NOT as an outstanding task. Do not re-run it.
> Verification (section 5) has NOT been executed — the test cases listed there are still outstanding.

# TASK SPECIFICATION: 3 - MEME_SCAN_SERIAL_FFPROBE_STORM

## 1. TARGET CONTEXT & SCOPE
- File Path: `src/FortniteVideoSoftware.App/MemeCatalog.cs`
- Target Range: Lines 81 to 130 (`ScanAsync`), with call sites at `src/FortniteVideoSoftware.App/Services/MemeManagementService.cs:24-36` and `src/FortniteVideoSoftware.App/MainWindow.axaml.cs:335`
- Defect Classification: Resource Starvation / Blocking I/O (serial process-spawn storm on the startup path, no persistence, no bounded concurrency)

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism:
  1. **One child process per video file, strictly serial.** The `foreach` at line 90 walks every file in the active meme directory and, for each video, constructs a fresh `MediaProber` and calls `.GetResolutionAsync().GetAwaiter().GetResult()` (lines 109-110). `MediaProber.ProbeAsync` starts a full `ffprobe` process with `-show_format -show_streams` and a 15-second timeout. On Windows, `CreateProcess` plus image load plus ffprobe's own demuxer probe costs on the order of 60-150 ms per file even when warm. The loop has no parallelism, no bounded worker pool, and no batching, so wall-clock cost is strictly linear in the size of the library: N files ≈ N × 100 ms, and a single unreadable or network-backed file costs the full 15-second timeout on its own.
  2. **`MediaProber`'s cache is per-instance and immediately discarded.** `MediaProber` holds `_probeData` behind a `SemaphoreSlim` (MediaProber.cs:14-15, 24-29), but line 109 allocates a NEW `MediaProber` inside the loop body and drops it on the next iteration. The memoisation the type provides is therefore dead code in this path — nothing is ever reused within a scan, and nothing at all survives between scans.
  3. **No persistence across scans.** `ScanAsync` reads the directory from scratch every call. Nothing keys on (path, size, mtime), so re-probing an unchanged file is pure waste. The full cost is paid again on every single repopulate.
  4. **The path is re-entered often, and at the worst moments.** `PopulateMemeComboBox` calls it from `this.Loaded` (MainWindow.Wireup.cs:1044 — i.e. on the app's cold-start critical path), from the static `MemeDirectory.Changed` event (MainWindow.Wireup.cs:1039), and from `RunCloudMemeSyncAsync` (MainWindow.axaml.cs:~376) — which runs immediately AFTER a cloud download has just added a batch of new files, so the largest library is scanned at the moment it is largest.
  5. **Blocking sync-over-async inside `Task.Run`.** `.GetAwaiter().GetResult()` at line 110 parks a thread-pool thread for the entire duration of each probe. Repeated from a `Task.Run` body, this is thread-pool starvation pressure: the pool's hill-climbing injection rate is roughly one new thread per 500 ms, so a scan of a large library holds a worker for many seconds while every other queued `Task.Run` in the app (filmstrip prewarm, size estimation, loudness probes) waits behind it.
- Target Pattern: A persisted, mtime/size-keyed dimension cache (an `AtomicJsonFile`-backed sidecar in the app's own ProgramData root, consistent with `SYS-ATOMICWRITE`) plus a bounded-concurrency probe pass (`Parallel.ForEachAsync` with `MaxDegreeOfParallelism` derived from `Environment.ProcessorCount`) that awaits `ProbeAsync` properly instead of blocking a pool thread.

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior:
  - **Zero-byte skip** (line 98: `try { if (new FileInfo(f).Length == 0) continue; } catch { continue; }`) — this is the Git-LFS-pointer / partial-download guard. It must run before any probe and must still swallow its own `FileInfo` failure as a skip.
  - **Unreadable-video exclusion** (lines 121-125): a video whose probed `Width`/`Height` is `<= 0` is dropped with a `RuntimeLog.Fail` line and the explicit reason "would crash export". This is a documented export-crash guard. It MUST still fire, including for files served from cache — a cached entry with non-positive dimensions must be treated as a cache miss and re-probed, then excluded if it fails again.
  - **Images are asymmetric to videos**: images are probed with `SkiaSharp.SKCodec` in-process (lines 104-105) and are NOT subject to the `<= 0` exclusion. Do not unify the two branches.
  - **Exception policy per the XML doc at lines 74-80**: `UnauthorizedAccessException` from the directory enumeration must PROPAGATE so the Settings meme-folder change flow can block and revert the path; every per-file probe error stays swallowed into `RuntimeLog.Info`. Do not wrap the whole body in a catch-all.
  - **Ordering**: results are ordered by `Path.GetFileName` under `StringComparer.OrdinalIgnoreCase` (line 90). A parallel probe pass must re-impose this exact ordering before returning, or the combo box order becomes nondeterministic.
  - **Missing directory returns empty, never throws** (line 86).
  - The supported-extension sets `VideoExts` / `ImageExts` and the `.ToLowerInvariant()` extension comparison must be unchanged.
- Public Interface Parity: Maintain exact public signatures, return types, and exceptions unless explicitly instructed. `public static async Task<List<MemeItem>> ScanAsync(string directory, string ffprobePath)` must keep its signature; `MemeManagementService.ScanMemesAsync()` must keep returning `Task<List<MemeItem>>`.

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. Isolate the target execution path within lines 81-130.
2. Split the body into three phases: (a) synchronous enumeration + filtering (extension, zero-byte skip) producing an ordered candidate list; (b) a dimension-resolution pass; (c) the existing exclusion + assembly pass.
3. Implement the dimension cache:
   - Back it with a sidecar JSON written through `FortniteVideoSoftware.Core.Infrastructure.AtomicJsonFile.WriteObject` into the app's existing ProgramData root — never a bare `File.WriteAllText`, per the `ATOMICTEXT_01` / `SYS-ATOMICWRITE` note in `AtomicJsonFile.cs`.
   - Key each entry on full path + file length + `LastWriteTimeUtc` ticks. Any mismatch is a miss.
   - Bound the cache with a deterministic eviction policy (drop entries whose file no longer exists on load; hard-cap the entry count with LRU on write) so it cannot grow without limit.
   - Treat a cached entry with `Width <= 0` or `Height <= 0` as a miss, so the export-crash guard is never satisfied from stale data.
4. Replace the serial blocking probe with `await Parallel.ForEachAsync(misses, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 6) }, async (f, ct) => ...)` that `await`s `MediaProber.GetResolutionAsync()` directly. Delete the `.GetAwaiter().GetResult()` at line 110 — no sync-over-async remains. Keep the per-file `try/catch` -> `RuntimeLog.Info` inside the delegate so one bad file cannot fault the whole pass.
5. Wire the preserved legacy behaviors into the new execution path: re-sort by filename ordinal-ignore-case after the parallel pass; run the `<= 0` exclusion and its `RuntimeLog.Fail` line over the merged (cached + freshly probed) results; keep images on the in-process Skia path outside the parallel probe.
6. Ensure deterministic error propagation and structured logging without silent swallows: emit one `RuntimeLog.Info("Memes", ...)` summary per scan reporting files seen, cache hits, probes run, and elapsed milliseconds, so a future regression in this path is visible in the log rather than felt as a slow start.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: Zero compiler/linter warnings or type errors; no remaining `.GetAwaiter().GetResult()` in `MemeCatalog.cs`.
- Behavioral Validation:
  - Test with a fixture directory containing a valid video, a valid image, a zero-byte file, a file with an unsupported extension, and a video whose probe fails: assert the zero-byte and unsupported files are skipped, the failing video is EXCLUDED with the `RuntimeLog.Fail` line, and ordering is filename-ordinal-ignore-case.
  - Test cache correctness: scan twice with no changes and assert zero `ffprobe` invocations on the second pass; then touch one file's mtime and assert exactly one re-probe.
  - Test that `UnauthorizedAccessException` on the directory still propagates out of `ScanAsync`.
- Performance Check: Verified elimination of lock contention, allocation spikes, or thread blocks. Benchmark a 200-file library: first scan must improve by at least the parallelism factor versus the serial baseline, the second scan must complete in under 100 ms, and no more than the configured `MaxDegreeOfParallelism` `ffprobe` processes may be alive at any instant (assert via a counting semaphore in the test harness).
