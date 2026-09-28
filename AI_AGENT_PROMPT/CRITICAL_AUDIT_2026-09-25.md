> **STATUS: ALL 9 APPLIED 2026-09-25.** Tags: AOTSAFETY_03, GPUPRESENT_02, EDITHOT_01/02, UNDOEQ_01/02,
> MUSICSYNC_01/02 (music ALWAYS plays at 1.0x; sync corrects by seeking only), COLOR_01, LOGVIS_01,
> WRITEORDER_01/02, USERSCOPE_01. Specs updated (02/03/04/05/06/07/08, INDEX.md) and sentinels added.
> Verified in a Linux container: Core + App compile clean; the NativeAOT publish (TFM forced to net9.0)
> succeeds under TreatWarningsAsErrors; Core tests 235/235 pass; App tests 70/72 (the 2 failures were
> already failing before these changes: RealMediaProbe needs ffprobe.exe, and DevCmdDelegates… fails
> because dev.cmd mentions CHECK_TAG in a comment). NOT yet verified: the Windows Build.cmd run and a
> runtime smoke test.
> Issue 9 note: RecoveryManager.CheckFault was left unchanged. With a per-user root, the lock PID
> always belongs to the same user, so treating an access-denied PID as a crash is correct again.

# CRITICAL ARCHITECTURE AUDIT — 2026-09-25

**Scope:** `src/` (Core + App, 95,307 C# lines), `docs/01–09`, `SPEC_GOVERNANCE.md`, `AI_AGENT_PROMPT/*`, `build/FvsBuild`, the live `build.log`.
**Method:** static trace of the threading, persistence, preview, export and build paths. Nothing was compiled or run; the only runtime evidence is the `build.log` from 2026-09-25 13:10.

**Excluded as already tracked (not re-raised):** R1–R8 god-class/god-method plans (all OPEN), BINPATH_01, and the nine `APPLIED` fixes in `AI_AGENT_PROMPT/1-…4-*`.

## Documented Architectural Traps Pre-Check

The fixes below touch these documented rules and fragile areas:

| Trap | Where it's documented | Touched by |
|---|---|---|
| AOTSAFETY_01: do not mute the trim/AOT analysers | `App.csproj` | Issue 1 |
| GPUPRESENT_01, GPUSLOT_01, ZOOMHANG_01, FREEZEDIAG_03/04 | `MpvVideoView.cs` | Issue 2 |
| North Star #6: the UI thread never blocks | `docs/README.md` §2 | Issue 3 |
| UNDO_20 / PROJSESSION_03 (one edit hook) and PROJ_11 (mask captured on every edit) | `MainWindow.axaml.cs`, `ProjectSession.cs` | Issues 3 and 4 |
| U1 gesture coalescing, U4 no-op rejection, value-equality contract | `docs/07_UNDO_AND_HISTORY.md` | Issue 4 |
| North Star #2: OutputTimeline is the only authority for time. CUTS_02 | `docs/README.md`, `MainWindow.Export.cs` | Issue 5 |
| North Star #4 and the GPU-resident route | `ExportVideoPipeline.cs` | Issue 6 |
| North Star #9, FAULTTIER_01/02, L7/ISSUE_13 | `docs/08_APPLICATION_COMPOSITION.md` | Issue 7 |
| RECOVERY_03/04, FLUSHCEILING_01, SYS-ATOMICWRITE | `docs/05_SYSTEM_LIFECYCLE_STORAGE.md` | Issue 8 |
| `ApplicationPaths.cs` is co-governed (05 and 06) | `docs/README.md` §3 | Issue 9 |

---

### Issue 1: Release Pipeline Cannot Produce a Binary (AOT Analyser Warnings Escalated to Errors)

**Documented Fragility Warning:** This touches **AOTSAFETY_01** ("THE TRIM AND AOT ANALYSERS ARE ON. DO NOT SET THESE BACK TO true"). The fix must **not** re-suppress the analysers.

**The Problem (Plain English):**
The factory's quality inspector was told to reject any box with a warning sticker. The factory then started accepting parts from outside suppliers that always come with a sticker. So every box is now rejected, and nothing ships. Your latest build (`build.log`, 13:10 today) failed with `BUILD FAILED - nothing was published`.

**Industry Standard Benchmark:**
Resolve and Premiere pipelines, and .NET AOT products generally, treat analyser output from their **own** code as a gate. Third-party findings go on a tracked allow-list and are not a build breaker. The user benefit is that releases keep flowing while the app's own reflection bugs are still caught.

**Senior Technical Breakdown:**
- **The trigger:** `build/FvsBuild/Staging.cs:108` passes `-p:TreatWarningsAsErrors=true` to the NativeAOT publish. Meanwhile `App.csproj:100-101` sets `SuppressTrimAnalysisWarnings=false` and `SuppressAotAnalysisWarnings=false`. The csproj comment claims "Warnings here are WARNINGS, not errors: the build still succeeds". The staging publish contradicts that.
- **Where the errors come from:** ILC emits `IL2104`/`IL3053` for `Avalonia.Controls.DataGrid`, `SkiaSharp`, `Avalonia.Base`, `Avalonia.Skia`, `Avalonia.Win32`, `SharpGen.Runtime`, `NAudio.Core` and `NAudio.Wasapi`. It also emits `IL2026` from Avalonia 11.0.10's `ObservableStreamPlugin`. All of them are promoted to errors, and `ilc` exits with -1.
- **Aggravating factor 1:** `App.csproj:47-50` sets `TrimmerRootAssembly` on DataGrid, ColorPicker, Fluent and SkiaSharp. Rooting a whole assembly forces analysis of every member, which is what produces the IL2104 summaries.
- **Aggravating factor 2:** `Avalonia.Controls.DataGrid` has **zero** references in `src/` (no `.cs`, no `.axaml`, no `StyleInclude`). It is pure analyser noise.

**Adversarial Trade-offs & Retention Case:**
The author escalated warnings so that no trim warning could ever ship silently. That is the correct instinct for first-party code. The risk of the fix is that a blanket `NoWarn` or suppression would recreate the pre-AOTSAFETY_01 blind spot.

**AI Agent Implementation Specification:**
```plaintext
TASK & MISSION GOAL:
Restore a green NativeAOT publish without muting first-party trim/AOT analysis.

CURRENT PROBLEM & ROOT CAUSE:
Staging.Publish escalates ALL warnings. Third-party assembly summaries (IL2104/IL3053) and
Avalonia 11.0.10's internal IL2026 therefore become fatal. Unused rooted assemblies amplify this.

ARCHITECTURAL MIGRATION:
Migrate away from: global TreatWarningsAsErrors over third-party IL summaries.
Migrate towards:   first-party IL warnings stay errors; third-party summaries sit on an explicit,
                   commented allow-list that the architecture tests ratchet.

TECHNICAL IMPLEMENTATION REQUIREMENTS:
1. TARGET FILES:
   - src/FortniteVideoSoftware.App/FortniteVideoSoftware.App.csproj
   - build/FvsBuild/Staging.cs (read-only unless step 4 needs it)
   - tests/FortniteVideoSoftware.App.Tests/ArchitectureRuleTests.cs (new ratchet)
2. Remove <PackageReference Avalonia.Controls.DataGrid> and its <TrimmerRootAssembly>. Re-check
   whether each remaining TrimmerRootAssembly is needed (ColorPicker, Fluent, SkiaSharp). Keep a
   root only where a runtime failure proves it is needed, and comment it.
3. Add <WarningsNotAsErrors>$(WarningsNotAsErrors);IL2104;IL3053</WarningsNotAsErrors> with an
   AOTSAFETY_03 comment. These are the per-assembly SUMMARY codes and never point at FVS code.
4. For IL2026 from Avalonia.Data.Core.Plugins.ObservableStreamPlugin: upgrade Avalonia
   11.0.10 -> the current 11.x line (all Avalonia packages in lockstep) and re-run the publish.
   Only if it persists, add a scoped ILLink.Descriptors / UnconditionalSuppressMessage at an FVS
   entry point with a written proof. Never add IL2026 to WarningsNotAsErrors globally.
5. Add a test that fails if SuppressTrimAnalysisWarnings/SuppressAotAnalysisWarnings becomes
   true, or if WarningsNotAsErrors contains anything beyond IL2104;IL3053.
6. Error policy: the build must still fail on any IL2xxx/IL3xxx whose origin is an FVS assembly.

INVARIANT PRESERVATION MANDATES:
- Single-binary mandate (#1) and PublishAot/SelfContained unchanged.
- IlcTrimMetadata stays false.

DEFINITION OF DONE:
- Build.cmd exits 0 and produces compiled\FortniteVideoSoftware.exe.
- Zero IL warnings attributed to FortniteVideoSoftware.* assemblies.
- A smoke launch of the published .exe opens the main window, color picker and settings.
```

---

### Issue 2: Dropped Present Orphans the Keyed Mutex, and the Preview Freezes Progressively

**Documented Fragility Warning:** This touches **GPUPRESENT_01 / GPUSLOT_01 / ZOOMHANG_01 / FREEZEDIAG_03-04**, the WGL↔D3D11↔Avalonia zero-copy path. Any change must keep frames in VRAM and keep the per-slot present gate.

**The Problem (Plain English):**
There are 16 trays on a conveyor. The cook puts food on a tray and flips its sign to "for the waiter". If the waiter is busy, the frame is thrown away, **but the sign stays on "for the waiter"**. Nobody ever flips it back. The next time that tray comes round, the cook waits a full second for it, gives up, and moves on. Each such event permanently poisons another tray. Over time the preview drops from 60 fps to a 1-second stutter on every frame.

**Industry Standard Benchmark:**
Resolve and Premiere keep swap-chain slot ownership in one state machine. A slot is only handed to the consumer once the consumer has committed to taking it. When a present is refused, the producer takes the slot back. Playback degrades gracefully by dropping frames, never by building up stalls.

**Senior Technical Breakdown:**
The failing sequence in `MpvVideoView`, in order:
1. `UpdateSurface` advances `_currentBufferIndex` (line 941).
2. It calls `AcquireSync(ProducerKey=0, 1000ms)` (line 965) and renders.
3. The `finally` calls `ReleaseSync(frameReady ? ConsumerKey : ProducerKey)` (line 1045). The slot now waits on key 1.
4. **Only then** does `ImportAndPresentTexture` run `gate.Wait(0)` (line 1319). On failure it only increments `_droppedPresentCount` and returns.

The result is that the texture sits at key 1 with no pending `UpdateWithKeyedMutexAsync(…, 1, 0)` to consume it.

How the gate stays taken while the texture is free: the gate is released in the **UI-thread continuation** after the compositor finishes. The compositor has already released key 0 on its own render thread. So whenever the UI thread stalls for more than about 250 ms (16 slots at 60 fps), the producer re-acquires key 0 while the gate is still held, releases to key 1, and drops the frame. The slot is now orphaned. A 250 ms stall is easy to hit: GC, timeline rebuilds, or the mutex waits in Issue 3.

What an orphaned slot costs: from then on, every visit to that index makes `AcquireSync(0)` time out after **1000 ms** while holding `_renderLock`. `PumpEmptyRender` does not recover the slot. Only `EnsureRenderTexture` does, and that runs only on a resize.

The `UpdateWithKeyedMutexAsync` exception paths (line 1339 catch blocks) orphan the slot the same way whenever the failure happens before the consumer acquires.

**Adversarial Trade-offs & Retention Case:**
The gate check was placed after rendering because rendering first minimises latency, and GPUPRESENT_01 was written to stop overlapping presents, not to handle mutex ownership. Moving the gate check changes frame-drop behaviour on the hottest path, so it must be validated on NVIDIA and AMD WGL_NV_DX_interop drivers.

**AI Agent Implementation Specification:**
```plaintext
TASK & MISSION GOAL:
Guarantee that no swap-chain slot can ever be left at ConsumerKey without a consumer in flight.

CURRENT PROBLEM & ROOT CAUSE:
The present gate is checked AFTER ReleaseSync(ConsumerKey). A dropped present or a failed
UpdateWithKeyedMutexAsync strands the texture at key 1, and every later AcquireSync(0) on that
slot times out after 1000 ms while holding _renderLock.

ARCHITECTURAL MIGRATION:
Migrate away from: render -> release to consumer -> try gate -> drop.
Migrate towards:   try gate -> acquire key 0 -> render -> release to key 1 -> present (gate
                   owned). On ANY path that does not reach the consumer: reclaim with
                   AcquireSync(ConsumerKey, 0) + ReleaseSync(ProducerKey).

TECHNICAL IMPLEMENTATION REQUIREMENTS:
1. TARGET FILES:
   - src/FortniteVideoSoftware.App/MpvVideoView.cs (UpdateSurface, ImportAndPresentTexture)
   - New private helper: ReclaimSlotToProducer(int index) (render thread, _renderLock held)
2. In UpdateSurface, take _presentGates[_currentBufferIndex].Wait(0) BEFORE AcquireSync.
   If the gate is busy, skip the slot (count a drop, PumpEmptyRender) without touching the mutex.
3. Pass gate ownership into ImportAndPresentTexture (remove its internal Wait(0)). Release the
   gate only in the UI continuation, as today.
4. In the UI continuation's catch blocks, where the consumer never acquired: post a
   render-thread reclaim request (a Channel<int> or flag array) and let the render thread call
   ReclaimSlotToProducer under _renderLock. D3D11 keyed-mutex calls stay off the UI thread.
5. If AcquireSync(ProducerKey) returns WAIT_TIMEOUT (0x102) twice in a row for the same slot,
   log once (RuntimeLog.Fail, tag GPUPRESENT_02) and rebuild that slot's texture and interop
   registration instead of retrying forever.
6. Error policy: never throw from the render loop. Log each HRESULT once per slot via
   SwallowedThrottled plus one FAIL line.

INVARIANT PRESERVATION MANDATES:
- Frames stay in VRAM (no CPU readback). SwapChainSize=16. ConsumerKey=1 and ProducerKey=0 unchanged.
- GPUSLOT_01 rule: the render thread is the only importer. The UI thread may only CompareExchange-retire.
- Frame budget: no path may block the render thread for more than one KeyedMutexWaitMs per slot rebuild.

DEFINITION OF DONE:
- Stress test: at 60 fps playback, block the UI thread for 400 ms every 2 s over 5 min. Result:
  zero "LOCK WAIT" lines, DroppedPresentCount increases, and render FPS recovers within 1 s of
  each stall.
- An instrumented counter of slots found at key 1 on producer entry stays at 0.
```

---

### Issue 3: Every Edit Tick Runs a Cross-Process Mutex, Disk I/O and SHA-256 on the UI Thread

**Documented Fragility Warning:** This touches **North Star #6**, **UNDO_20/PROJSESSION_03** (the single edit hook) and **PROJ_11** (the mask is captured on every edit boundary).

**The Problem (Plain English):**
Every time you nudge the speed or quality dial by one notch, the app walks to a shared filing cabinet in another building, waits for its key, photocopies a file, fingerprints the copy, and starts a new crash-backup write. It does all of this while your hand is still on the dial. If anyone else holds that key, your dial freezes for up to 15 seconds.

**Industry Standard Benchmark:**
Resolve and Premiere keep edit state in memory. Autosave and recovery are background write-behind jobs with coalescing. The UI thread never touches a cross-process lock or the disk during a gesture, so dials and drags stay at display rate.

**Senior Technical Breakdown:**
The call chain, starting from one dial notch:
1. `mainSpeedSlider.ValueChanged` (`MainWindow.Wireup.cs:766`) and `qualitySlider.ValueChanged` (`:845`), plus about 40 other sites, call `SaveRecoveryState()`.
2. That calls `PushProjectEdit("edit")` (`MainWindow.axaml.cs:3087`), which calls `ProjectSession.PushEdit`, which calls `Capture()`.

Inside `Capture()`, every tick:
- `SourceClip.Probe` does a `FileInfo` stat.
- `ReadLiveMaskSafely()` calls `MaskOverlayManager.ReadLiveMask`, which does:
  - `NamedSystemMutex.Acquire("Global\FvsStateTransferMutex", 15 s)`. It polls `WaitOne(100)`.
  - `AtomicJsonFile.ReadObject(crops_coordinations.conf)`.
  - `DeepClone`, then `ProjectMask.ComputeFingerprint`, which canonicalises and runs SHA-256.

After `Capture()`, `SerializeState` rebuilds the full recovery JSON, and `SaveStateAsync` queues a `WriteThrough`, `Flush(true)` and `File.Move` for each tick.

The same mutex is held by:
- `NamedPipeStateServer.FlushToDiskSafe`, which includes `File.Move` retries of up to about 300 ms of `Thread.Sleep`;
- every `StateTransferStore` operation;
- `CropConfigStore`;
- `SettingsManager`.

Lock contention on the UI thread therefore depends on disk latency, antivirus scans and sibling processes. On timeout, a `LockException` is turned into a Degraded notice by `GuardValue`.

`volumeSlider.PointerReleased` (`Wireup.cs:812`) also runs `StateTransferStore.UpdatePropertiesSync` synchronously on the UI thread.

**Adversarial Trade-offs & Retention Case:**
Capturing the live mask on every edit guarantees the `.fvsproj` always records the exact HUD mask (PROJ_11), and it was the simplest correct way to do that. Moving the capture off the hot path creates a staleness window, so the cached mask must be invalidated explicitly whenever the Crop Tool writes.

**AI Agent Implementation Specification:**
```plaintext
TASK & MISSION GOAL:
Make the per-edit hook O(memory): no disk, no named mutex, no hashing on the UI thread.

CURRENT PROBLEM & ROOT CAUSE:
PushEdit -> Capture -> ReadLiveMask takes a Global named mutex (15 s timeout) and reads and
hashes a file on every slider tick. SaveRecoveryState also serialises and queues a durable
write per tick.

ARCHITECTURAL MIGRATION:
Migrate away from: synchronous capture of external state inside the edit hook.
Migrate towards:   an in-memory LiveMaskCache (versioned, invalidated by Crop Tool writes and
                   profile switches) plus a coalescing RecoveryWriteBehind (Channel<Snapshot>,
                   capacity 1, DropOldest, 750 ms debounce, final flush on close).

TECHNICAL IMPLEMENTATION REQUIREMENTS:
1. TARGET FILES:
   - src/FortniteVideoSoftware.App/Infrastructure/MaskOverlayManager.cs (add LiveMaskCache)
   - src/FortniteVideoSoftware.App/Services/ProjectSession.cs (Capture uses the cache)
   - src/FortniteVideoSoftware.App/MainWindow.axaml.cs (SaveRecoveryState -> debounced)
   - New: src/FortniteVideoSoftware.App/Services/RecoveryWriteBehind.cs
2. LiveMaskCache: holds (ProjectMask, version). Refresh it off the UI thread (Task.Run) when
   CropConfigStore/MaskOverlayManager writes, when the profile changes, and once at startup.
   Capture() reads the cached reference only. Use Volatile.Read, with no lock on the UI thread.
3. SourceClip.Probe: compute once per LoadedVideoPath and reuse it. Re-probe only on video load
   or Save.
4. SaveRecoveryState: keep the undo push synchronous (memory only). Replace the immediate
   SerializeState + SaveState with RecoveryWriteBehind.Request(() => SerializeState(...)).
   The snapshot build still runs on the UI thread (it reads VMs), but only at debounce fire.
   Keep sync: true callers (shutdown, :2921) on the immediate path.
5. Move volumeSlider.PointerReleased UpdatePropertiesSync to UpdatePropertiesAsync (fire and
   forget, with a logged continuation).
6. Error policy: cache refresh failures are Degraded ("HUD mask not recorded") once per 60 s.
   A write-behind IOException or UnauthorizedAccessException is Degraded with the path.

INVARIANT PRESERVATION MANDATES:
- North Star #6: zero named-mutex or file calls reachable from any ValueChanged/PointerMoved handler.
- PROJ_11: a saved .fvsproj still records the mask active at save time. Force a synchronous
  refresh inside WriteTo (Save is user-initiated, not a gesture).
- RECOVERY_03/04 semantics (what counts as work) unchanged.

DEFINITION OF DONE:
- Architecture test: no call path from SaveRecoveryState to NamedSystemMutex.Acquire.
- A speed-dial sweep 1.0x -> 4.0x logs no LOCK WAIT, and UI frame time stays under 16 ms
  (Avalonia DevTools / ETW).
- Recovery file writes during a sweep: at most 1 per 750 ms.
```

---

### Issue 4: Undo No-Op Rejection Is Dead, and Slider Ticks Flood the 40-Step History

**Documented Fragility Warning:** This touches **`docs/07_UNDO_AND_HISTORY.md` U1 and U4** and the explicit contract: *"U4 depends on `Equals` being a real state comparison — a reference-equality default silently disables it."*

**The Problem (Plain English):**
The undo list is meant to skip duplicates and merge a whole dial drag into one step. Both features are silently off. Dragging the speed dial from 1.1x to 3.0x fills about 19 of the 40 undo slots and pushes out your real edits. Clicking a toggle that changes nothing also adds an "edit" that, when undone, visibly does nothing. The spec calls this "the worst undo bug there is".

**Industry Standard Benchmark:**
Premiere and Resolve record one history entry per completed gesture (mouse-up) and never record a no-op. The user benefit is that Ctrl+Z always does something visible and reverses one whole action.

**Senior Technical Breakdown:**
1. `ProjectDocument` is a `sealed record` (`ProjectDocument.cs:51`) but it has no value equality in practice:
   - `Segments`, `Cuts` and `Memes` are `IReadOnlyList<>` filled with a fresh `.ToArray()` each `Capture()`, so the compiler-generated `Equals` compares array references.
   - `ModifiedUtc = _clock.UtcNow` (`ProjectSession.cs:564`) and the default `CreatedUtc = UtcNow` differ on every capture.
   - `ProjectMask.Config` is a fresh `JsonObject` from `DeepClone`.
   So `UndoStack.Apply`'s `if (Equals(Current, next)) return false;` (`UndoStack.cs:144`) can never be true.
2. `PushProjectEdit` has a `gestureKey` parameter whose own XML doc warns *"without it a single trim drag leaves forty entries on the stack"*. **No caller passes it.** The only call is `PushProjectEdit("edit")`.
3. `MaxDepth = 40` means one dial sweep evicts most of the real history. `UndoSidecarStore` then saves the flood to disk.
4. No test covers `ProjectDocument` equality, so the U4 tests pass on `UndoStack<T>` with test types but never on the production `T`.

**Adversarial Trade-offs & Retention Case:**
Records with arrays were chosen for immutability and cheap snapshots, and nobody noticed that record equality is shallow over collections. Adding structural equality affects every consumer that compares documents (the sidecar fingerprint, fromBackup logic), so it must exclude timestamps deliberately.

**AI Agent Implementation Specification:**
```plaintext
TASK & MISSION GOAL:
Make U4 (no-op rejection) and U1 (gesture coalescing) actually work for ProjectDocument in
MainWindow.

CURRENT PROBLEM & ROOT CAUSE:
Record equality uses reference comparison for collection properties and includes volatile
timestamps. The edit hook never passes a gesture key.

ARCHITECTURAL MIGRATION:
Migrate away from: compiler-generated record Equals and key-less pushes.
Migrate towards:   an explicit IEquatable<ProjectDocument> "edit-state equality" (structural,
                   timestamp-free) plus gesture keys on all continuous controls and EndGesture on
                   release.

TECHNICAL IMPLEMENTATION REQUIREMENTS:
1. TARGET FILES:
   - src/FortniteVideoSoftware.Core/Project/ProjectDocument.cs
   - src/FortniteVideoSoftware.App/MainWindow.axaml.cs (SaveRecoveryState signature)
   - src/FortniteVideoSoftware.App/MainWindow.Wireup.cs, MainWindow.Canvas.cs (call sites)
   - tests/FortniteVideoSoftware.Core.Tests/ProjectDocumentTests.cs (new equality tests)
2. Override Equals(ProjectDocument?) and GetHashCode:
   - SequenceEqual over Segments, Cuts and Memes.
   - Member-wise over Source, Audio, Export and Merge.
   - For Mask, compare Fingerprint only.
   - EXCLUDE CreatedUtc, ModifiedUtc, Title and UnknownFields.
   - Use a tolerance of 0.001 ms or exact on doubles, consistent with the HistoryFingerprint
     F3 formatting.
3. Add an optional gestureKey to SaveRecoveryState(..., string? gestureKey = null) and pass it
   through to PushProjectEdit. Keys: "speed-dial", "quality-dial", "trim-start", "trim-end",
   "music-start", "music-end", "music-block", "volume". Call EndProjectGesture() in
   ValueChangeCompleted/PointerReleased handlers.
4. Give each call site a specific label per 07 §3 (e.g. "change speed", "move trim start").
5. Concurrency: UI thread only (07 §3). No locks.
6. Error policy: none new. Equality must never throw (null-safe on Mask and Merge).

INVARIANT PRESERVATION MANDATES:
- U1-U4 semantics exactly as in 07. MaxDepth 40 unchanged.
- The UndoSidecarStore format is unchanged. HistoryFingerprint is unchanged.

DEFINITION OF DONE:
- Test: two Capture() calls with no state change are Equal. Apply returns false.
- Test: 20 speed-dial ticks with one gesture key followed by EndGesture give exactly 1 undo entry.
- Manual: toggling a checkbox on then off gives 2 entries, never an empty-effect undo.
```

---

### Issue 5: Preview Audio Has No Master Clock, and the Music Preview Bypasses OutputTimeline

**Documented Fragility Warning:** This touches **North Star #2** ("OutputTimeline.cs is the sole mathematical model … across both live preview playback and FFmpeg rendering") and **CUTS_02** (music is placed in output time).

**The Problem (Plain English):**
The export lays the music along the **finished** video's clock. The preview plays it against the **raw** footage's clock, at normal speed, next to video running at 1.1x (the default) with cuts skipped. Start playback halfway through and the song is seconds off from what will export. You line up a beat drop by ear in the preview, and the exported video misses it. Voice-over takes are allowed to drift up to half a second before correction, and they start late by the audio buffer (about 300 ms).

**Industry Standard Benchmark:**
Every commercial NLE drives all preview audio from one master clock tied to the output timeline. Audio buffers are scheduled against that clock and drift is corrected continuously at sample level. The user benefit is that preview equals export for sync.

**Senior Technical Breakdown:**
- **Music start position:** `StartMusicPreview` (`MainWindow.axaml.cs:1642`) computes `OffsetSeconds + (sourceTime − TimelineStartSeconds)`. `TimelineStartSeconds` is in **source** ms. The export (`MainWindow.Export.cs:224-240`) maps it through `SourceMsToOutputSeconds` / OutputTimeline.
- **Music playback rate:** music runs at 1.0x wall clock in a separate libmpv instance. The video runs at `_baseSpeed` (`SetPropertyAsync("speed")`) plus speed segments and cuts.
- **Music resync:** resync only triggers when the per-tick `time` jumps more than 0.5 s (`:1859`), so drift never corrects during continuous playback.
- **Voice-over (main window and editors):** VO uses the output mapper (which is correct). But it tolerates 0.5 s of error (`MainWindow.axaml.cs:1901`, `VoiceOverPreviewPlayer.cs:144`) on independent `WaveOutEvent`s whose default `DesiredLatency` is 300 ms. Play() therefore starts audibly late, with no latency compensation.
- **Architecture:** there are three unsynchronised clocks (the video mpv, the music mpv, and NAudio WinMM). None is slaved to the others.

**Adversarial Trade-offs & Retention Case:**
A separate audio-only mpv was the fastest way to get music audible with zero mixing code, and the 0.5 s threshold avoids audible seek-stutter from over-correction. Replacing it touches three windows (Main, Granular, Music Wizard), and audio glitch regressions are very noticeable.

**AI Agent Implementation Specification:**
```plaintext
TASK & MISSION GOAL:
Drive all preview audio (music bed + VO takes) from ONE clock expressed in OUTPUT seconds via
OutputTimeline, with bounded drift.

CURRENT PROBLEM & ROOT CAUSE:
Music offset is computed in source seconds at 1x wall clock. VO drift tolerance is 0.5 s with
300 ms uncompensated output latency. There is no master clock.

ARCHITECTURAL MIGRATION:
Migrate away from: independent players started and stopped on UI ticks.
Migrate towards:   PreviewAudioClock (output-time, derived from mpv time-pos through the
                   OutputTimeline mapper). Each audio player is a follower that corrects by
                   seek (>80 ms error) or by rate nudge (mpv "speed" 0.97-1.03 for music) below
                   that.

TECHNICAL IMPLEMENTATION REQUIREMENTS:
1. TARGET FILES:
   - src/FortniteVideoSoftware.App/MainWindow.axaml.cs (StartMusicPreview, tick handler)
   - src/FortniteVideoSoftware.App/Controls/VoiceOverPreviewPlayer.cs
   - New: src/FortniteVideoSoftware.App/Services/PreviewAudioClock.cs
2. Music position = OffsetSeconds + (OutputTimeline.SourceToOutput(sourceTime) −
   OutputTimeline.SourceToOutput(TimelineStartSeconds)). Use the SAME helper as
   MainWindow.Export (SourceMsToOutputSeconds). Do not reimplement it (North Star #2).
3. Music player speed = 1.0 (output time runs at wall rate). Correct drift every tick:
   |err| > 0.08 s -> seek; else set speed = clamp(1 + err*0.5, 0.97, 1.03).
4. VO: set WaveOutEvent.DesiredLatency = 60 and NumberOfBuffers = 3. Compensate the start by
   seeking to (voiceTime + latency). Lower the correction threshold to 0.08 s.
5. Concurrency: clock reads on the UI tick. VoiceOverPreviewPlayer keeps its single-reader
   Channel. No locks in NAudio callbacks.
6. Error policy: player failures are Degraded ("music preview unavailable — export unaffected").

INVARIANT PRESERVATION MANDATES:
- North Star #3: the master preview volume stays decoupled from export loudness.
- Export path untouched. This is preview-only.
- A/V sync budget: |audio − video| ≤ 80 ms steady state, ≤ 150 ms within 1 s of a seek.

DEFINITION OF DONE:
- Test (pure math): at base speed 1.1x with a 20 s cut, the preview music position equals the
  export startDelay mapping to within 1 ms.
- Manual: a clap-track clip exported vs previewed has an offset ≤ 1 frame.
```

---

### Issue 6: No Colour Management in Export (HDR Not Tonemapped, Output Untagged)

WARNING: FACT CHECK REQUIRED. The code facts are verified. The exact visual result depends on the FFmpeg build's colour-tag propagation and on whether users record HDR (NVIDIA/AMD HDR capture produces HEVC Main10 PQ). Verify with a real HDR Fortnite clip.

**Documented Fragility Warning:** This touches the GPU-resident route in `ExportVideoPipeline` (`scale_cuda` substitution) and **North Star #4** (the filter-graph rules).

**The Problem (Plain English):**
If a clip was recorded in HDR, the preview looks correct because mpv tone-maps it automatically. The export squeezes the HDR colours into normal video with no conversion, so the result comes out grey and washed-out, or mislabelled. Normal clips are also exported without a colour label, so each phone or site guesses. What you see is not what you get.

**Industry Standard Benchmark:**
Resolve and Premiere run a colour-managed pipeline. They read input primaries, transfer and range, tone-map HDR→SDR (BT.2390/Hable) when delivering SDR, convert to BT.709 limited range, and **tag** the output (`colour_primaries/transfer/matrix = BT.709, range = tv`). The GPU does the conversion as a shader pass. The user benefit is identical colour on every platform.

**Senior Technical Breakdown:**
- The whole codebase contains **zero** occurrences of `tonemap`, `zscale`, `colorspace`, `color_primaries`, `color_trc`, `color_range` or `bt709`. Every encoder path only forces `-pix_fmt yuv420p` (`EncoderManager.cs:271/295/315/332/348`, `TwoPassEncoding.cs:71/96`).
- GPU route: `format=yuv420p` → `scale_cuda=format=yuv420p` (`ExportVideoPipeline.cs:103`) truncates P010 BT.2020/PQ to 8-bit with no transfer conversion.
- CPU route: `format=yuv420p` (`MobileFilterBuilder.cs:182`, `ProcessWorker.cs:1258/1284`) does the same through swscale. The input colour properties may then pass through to the encoder, which would tag 8-bit output as PQ/BT.2020.
- Full-range sources (a common OBS setting) keep `pc` range and are handed to platforms that often ignore the full-range flag, which crushes shadows.
- There is no probe of `color_transfer/color_primaries/color_range` in `MediaProber`, so the pipeline cannot branch on them.

**Adversarial Trade-offs & Retention Case:**
Untagged yuv420p "just works" for the dominant case (SDR BT.709 limited-range capture) and keeps the filter graph small and GPU-resident. Adding `zscale`/`tonemap` forces a CPU round trip unless `tonemap_cuda`/`libplacebo` is available, which risks the zero-copy route and export speed.

**AI Agent Implementation Specification:**
```plaintext
TASK & MISSION GOAL:
Deterministic BT.709 limited-range SDR output, tagged, with HDR sources tone-mapped.

CURRENT PROBLEM & ROOT CAUSE:
No colour probe, no conversion, no output tags. scale_cuda/format only change the pixel format.

ARCHITECTURAL MIGRATION:
Migrate away from: an implicit pixel-format-only pipeline.
Migrate towards:   MediaProber exposes ColorInfo{primaries, transfer, matrix, range, bitDepth}.
                   ExportColorPolicy picks the route:
                   SDR709   -> unchanged graph + output tags
                   FullRange -> scale=out_range=tv (scale_cuda … when GPU)
                   HDR (smpte2084/arib-std-b67) -> GPU: tonemap_cuda or libplacebo if present in
                   the bundled ffmpeg (probe -filters once), else CPU zscale+tonemap=hable
                   (logged Degraded "HDR clip: slower export for correct colours").

TECHNICAL IMPLEMENTATION REQUIREMENTS:
1. TARGET FILES:
   - src/FortniteVideoSoftware.Core/Media/MediaProber.cs (read color_* fields)
   - New: src/FortniteVideoSoftware.Core/Media/ExportColorPolicy.cs
   - src/FortniteVideoSoftware.Core/Media/ExportVideoPipeline.cs (GPU substitutions)
   - src/FortniteVideoSoftware.Core/Media/EncoderManager.cs, TwoPassEncoding.cs (tags)
   - src/FortniteVideoSoftware.Core/Media/MergerWorker.cs (same policy per input; normalise
     before concat)
2. Always append: -colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv.
3. Probe the bundled ffmpeg's filter list once at startup (the HardwareScanner pattern) and cache it.
4. MergerWorker: per-input normalisation to the same ColorInfo before concat. Mixed HDR/SDR
   inputs must not concat raw.
5. Memory/AOT: pure string building. No reflection.
6. Error policy: a missing HDR filter is Degraded plus the CPU fallback, never a silent untagged output.

INVARIANT PRESERVATION MANDATES:
- Zero-copy GPU route is preserved for SDR709 (the dominant case).
- The zoompan ban (#4) and cas=0.5 are unaffected. The filter graph stays deterministic.

DEFINITION OF DONE:
- MediaPipelineChecks: an HDR10 fixture gives an output with ffprobe color_transfer=bt709 and
  mean luma within 5% of the mpv tonemapped reference. An SDR fixture's output is tagged bt709/tv.
- A full-range fixture gives Y min ≥ 16 and max ≤ 235.
```

---

### Issue 7: All "Recoverable" Faults and CoreLogger.Debug Diagnostics Vanish in Production

**Documented Fragility Warning:** This touches **North Star #9**, **FAULTTIER_01/02** and the **L7/ISSUE_13** logger recursion rule. The spec text says Recoverable is "DEBUG log only", and it also claims these failures "now EXIST: classified, counted, in the diagnostic bundle". In shipped builds they do not.

**The Problem (Plain English):**
The black-box flight recorder only records while a mechanic is in the cockpit. In the shipped app, 558 places that catch an error write **nothing at all**. That includes GPU render-loop failures and failed state flushes. When a user reports "the preview went black", the log is empty.

**Industry Standard Benchmark:**
Production NLEs always keep a bounded, rate-limited WARN/INFO breadcrumb for handled exceptions (Resolve's log bundle, Premiere's dump files). Verbose DEBUG is the only thing gated. The user benefit is that support can diagnose field failures without a repro.

**Senior Technical Breakdown:**
- `RuntimeLog.Swallowed`, `SwallowedThrottled` and `CoreLogger.Swallowed` call `Faults.Recoverable`, which reaches `UserFacingFaultSink` (`:84`) and then `RuntimeLog.Debug`. `RuntimeLog.Debug` does `if (!IsDevMode) return;` (`RuntimeLog.cs:192`), and `IsDevMode` requires `FVS_DEV_LOG_DIR`.
- `CoreLogger.DebugAction = RuntimeLog.Debug` (`Program.cs:18`), so 82 `CoreLogger.Debug` failure lines are also dropped. Examples: `NamedPipeStateServer` "Background disk flush error", "IPC listener did not stop", `VoiceRecorder` "StopRecording threw", and `MicLevelMonitor` device failures.
- Nothing counts faults, so `DiagnosticBundle` cannot report them either.
- The logger is already an async bounded queue with rotation, so a throttled production breadcrumb costs almost nothing.

**Adversarial Trade-offs & Retention Case:**
Keeping Recoverable at DEBUG protects a 10 MB rolling log from per-frame spam (the render loop can throw at 60 Hz). Promoting it without per-site throttling would flood rotation and evict the useful lines.

**AI Agent Implementation Specification:**
```plaintext
TASK & MISSION GOAL:
Every Recoverable fault leaves a production breadcrumb (throttled per call site) and is counted.

CURRENT PROBLEM & ROOT CAUSE:
The Recoverable tier and CoreLogger.Debug map to RuntimeLog.Debug, which is a no-op outside dev mode.

ARCHITECTURAL MIGRATION:
Migrate away from: tier -> Debug (dev-only).
Migrate towards:   tier -> RuntimeLog.Info("[RECOVERABLE] area — where — type: message"),
                   throttled per call site (30 s window with a suppressed count, reusing the
                   SwallowedThrottled mechanism). Full stack trace stays DEBUG-only. A
                   FaultCounters ConcurrentDictionary<area,long> is included in DiagnosticBundle.

TECHNICAL IMPLEMENTATION REQUIREMENTS:
1. TARGET FILES:
   - src/FortniteVideoSoftware.App/Services/UserFacingFaultSink.cs
   - src/FortniteVideoSoftware.App/RuntimeLog.cs (expose the throttle helper; no logic change to the queue)
   - src/FortniteVideoSoftware.App/Program.cs (CoreLogger.DebugAction: keep Debug; add a
     CoreLogger.WarnAction mapped to throttled Info)
   - src/FortniteVideoSoftware.Core/Infrastructure/CoreLogger.cs (Warn)
   - src/FortniteVideoSoftware.App/Services/DiagnosticBundle.cs (counters)
2. Reclassify CoreLogger.Debug calls whose message describes a FAILURE (grep "error|failed|threw|
   did not") to CoreLogger.Warn. Leave trace-only Debug lines alone.
3. Concurrency: ConcurrentDictionary plus Interlocked. The sink must never block (it is called
   from the render and audio threads).
4. The logger must never log its own failure (L7/ISSUE_13).
5. Budget: at most 1 line per call site per 30 s. Queue capacity (10000) unchanged.

INVARIANT PRESERVATION MANDATES:
- The user-visible behaviour of the Recoverable tier is unchanged (no UI).
- Log rotation (10 MB, 5 files, 50 MB, 14 days) unchanged.

DEFINITION OF DONE:
- Release build without FVS_DEV_LOG_DIR: a forced exception in MpvVideoView.UpdateSurface
  gives exactly one [RECOVERABLE] line per 30 s with a suppression count.
- ArchitectureRuleTests: UserFacingFaultSink's Recoverable branch must not call RuntimeLog.Debug alone.
```

---

### Issue 8: Write-Behind Persistence Has No Ordering Barrier (Stale State Resurrection)

**Documented Fragility Warning:** This touches **RECOVERY_03/04**, **FLUSHCEILING_01** and **SYS-ATOMICWRITE**.

**The Problem (Plain English):**
Two couriers deliver versions of the same letter. Whoever arrives last wins, even when they carry the older version. When you undo back to an empty project, the app throws the backup away, then a courier who was already on the way drops the old backup back in. The same happens at clean shutdown. For the shared settings file, an older snapshot can overwrite a newer one.

**Industry Standard Benchmark:**
Autosave in professional NLEs uses a single serialised writer with monotonic versions. A delete is just another versioned command in the same queue. The user benefit is that what is on disk is always the latest intent.

**Senior Technical Breakdown:**
**RecoveryManager:**
- `SaveStateAsync` (`:335`) is `Task.Run(SaveState(state, seq))`.
- `ClearState` (`:457`) calls `File.Delete` **outside** `_saveLock` and without advancing the sequence. It is called from `SaveRecoveryState` when `!HasUnsavedWork()` and from `CleanupLock` at shutdown.
- A queued save that runs after the delete re-creates `recovery_v2.json`.
- `_latestCommittedSave` is per instance while `_saveLock` is static. Three instances exist: MainWindow `_recovery`, `ProjectRecoveryService`, and the granular editor. Sequence ordering therefore does not span writers.

**NamedPipeStateServer:**
- `FlushToDiskSafe` (`:397`) snapshots under `_stateLock`, then acquires the named mutex **after** releasing it.
- The debounce timer and the forced `Task.Run(FlushToDiskSafe)` (`:382`) can run concurrently. `NamedSystemMutex` polls with `WaitOne(100)`, which gives no FIFO ordering, so the older snapshot S1 can be written after S2.
- `Dispose()`'s final flush returns early (`!_isDirty`) while an in-flight flush that holds the newest state is still running.

**Adversarial Trade-offs & Retention Case:**
Fire-and-forget `Task.Run` plus a static lock was a quick way to keep disk I/O off the UI thread, and the sequence check handled the common double-save case. A single writer queue changes shutdown timing, so it must still flush synchronously on the close path.

**AI Agent Implementation Specification:**
```plaintext
TASK & MISSION GOAL:
One ordered writer per persisted file. Deletes and saves are totally ordered by a monotonic
version.

CURRENT PROBLEM & ROOT CAUSE:
An unsequenced delete races queued saves. Concurrent flushes race for the mutex after
snapshotting.

ARCHITECTURAL MIGRATION:
Migrate away from: Task.Run per save, unlocked ClearState, snapshot-then-lock flushes.
Migrate towards:   PersistenceQueue<T> (Channel, SingleReader) carrying Save(v, payload) |
                   Delete(v). The writer applies an op only if v > lastApplied, and DrainAsync()
                   runs on shutdown.

TECHNICAL IMPLEMENTATION REQUIREMENTS:
1. TARGET FILES:
   - src/FortniteVideoSoftware.Core/Infrastructure/RecoveryManager.cs
   - src/FortniteVideoSoftware.Core/Ipc/NamedPipeStateServer.cs
   - New: src/FortniteVideoSoftware.Core/Infrastructure/PersistenceQueue.cs
2. RecoveryManager: make the version counter static (process-wide, Interlocked). Route
   SaveStateAsync, SaveState(sync), ClearState and UpdateGranularSession through the queue.
   Sync callers enqueue and await completion (bounded 3 s).
3. CleanupLock: DrainAsync().Wait(3 s) BEFORE deleting, then enqueue Delete with the highest version.
4. NamedPipeStateServer: serialise FlushToDiskSafe with a private SemaphoreSlim(1,1) held across
   snapshot + mutex + write. Stamp a monotonic _stateVersion under _stateLock and skip the write
   if version <= _lastFlushedVersion. Dispose waits (2 s) for any in-flight flush, then flushes
   the latest.
5. File I/O: keep AtomicJsonFile (GUID temp, WriteThrough, Move) and EnsureWritableDirectories.
6. Error policy: IOException and UnauthorizedAccessException are Degraded once per 60 s with the
   path. LockException is retried by the next version.

INVARIANT PRESERVATION MANDATES:
- RECOVERY_03: granular_session read-modify-write semantics unchanged.
- FLUSHCEILING_01: a 3 s maximum dirty window.
- No UI-thread waits longer than 3 s on shutdown.

DEFINITION OF DONE:
- Test: enqueue Save(1), Delete(2) and a delayed Save(1). The file must be absent.
- Test: 1,000 randomised concurrent UpdateProperties + flushes. The final file equals the final
  in-memory state.
```

---

### Issue 9: Per-User Sessions Share Machine-Global State Files and a Global Mutex

WARNING: FACT CHECK REQUIRED. The cross-user `Mutex` open failure depends on the default DACL applied to the creator's token. Verify on a two-account machine.

**Documented Fragility Warning:** This touches the **co-governed `ApplicationPaths.cs`** (specs 05 and 06).

**The Problem (Plain English):**
Two people share a PC. Each can run the app at the same time (the single-instance lock is per user), but both write the same crash-backup, session and lock files in `C:\ProgramData`. User B can be offered user A's "crashed" session, which includes A's video paths. B's app takes over A's session lock, and when A exits cleanly it deletes the recovery state B is using. B's app may also fail to open A's `Global\` mutex at all, which breaks settings and mask reads on every edit.

**Industry Standard Benchmark:**
Desktop NLEs keep user session state, recovery and preferences in per-user `%LOCALAPPDATA%`/`%APPDATA%`. Only true machine data (codecs, licences) lives in ProgramData. Locks are per-user or per-session. The user benefit is isolation and privacy between accounts.

**Senior Technical Breakdown:**
- `ApplicationPaths.CreateDefault` → `CommonApplicationData\Fortnite Video Software` holds `recovery_v2.json`, `session_state.json`, `app_session.lock`, `safe_mode.sentinel` and `crops_coordinations.conf`. It is made writable by `icacls … /grant *S-1-5-32-545:(OI)(CI)F`, launched **fire-and-forget** (`ApplicationPaths.cs:122`). That is a TOCTOU: the first writes race the ACL change.
- `SingleInstanceGuard` is `Local\…_{SID}`, so concurrent instances across users or sessions are explicitly allowed. `IpcProtocol.ServerMutexName` is user-scoped too. But `StateTransferStore.MutexName = @"Global\FvsStateTransferMutex"` is unscoped.
- `RecoveryManager.CheckFault`: for another user's live PID, `proc.StartTime` throws `Win32Exception` (access denied). The exception is swallowed, the code falls through to "crash detected" and returns `true`.
- `new Mutex(false, "Global\…")` against another user's object with the default DACL throws `UnauthorizedAccessException` from the constructor. `NamedSystemMutex` does not catch it, so it propagates to `ReadLiveMask` (every edit, see Issue 3), `StateTransferStore` and `SettingsManager`.

**Adversarial Trade-offs & Retention Case:**
ProgramData was likely chosen so the elevated installer and the app share one known path and the Crop Tool, Merger and main app see the same config. Moving to per-user paths needs a one-time migration and changes where logs live, which support documentation references.

**AI Agent Implementation Specification:**
```plaintext
TASK & MISSION GOAL:
Isolate all mutable session state and locks per Windows user. Keep only installer or machine
data in ProgramData.

CURRENT PROBLEM & ROOT CAUSE:
Session, recovery and lock files live in ProgramData with Users:F. The state mutex is
Global\ and unscoped. The PID liveness check fails open across users.

ARCHITECTURAL MIGRATION:
Migrate away from: CommonApplicationData root + Global\ state mutex.
Migrate towards:   %LOCALAPPDATA%\Fortnite Video Software (per user) + Local\FvsStateTransferMutex_{SID}.
                   A one-time migration copies the legacy ProgramData files for the first user
                   who launches and leaves a marker.

TECHNICAL IMPLEMENTATION REQUIREMENTS:
1. TARGET FILES:
   - src/FortniteVideoSoftware.Core/Infrastructure/ApplicationPaths.cs
   - src/FortniteVideoSoftware.Core/Ipc/StateTransferStore.cs (MutexName)
   - src/FortniteVideoSoftware.Core/Infrastructure/RecoveryManager.cs (CheckFault)
   - src/FortniteVideoSoftware.App/DeploymentLifecycle.cs / DeploymentFootprint.cs
     (uninstall cleanup of per-user dirs)
   - src/FortniteVideoSoftware.Core/Infrastructure/NamedSystemMutex.cs
2. Remove the icacls grant. Per-user dirs need no ACL change.
3. MutexName: $"Local\\FvsStateTransferMutex_{UserScope}" (reuse IpcProtocol.UserScope).
4. CheckFault: treat Win32Exception on StartTime as "process alive, not ours" (return false).
   Only an ArgumentException (the PID no longer exists) counts as a crash signal.
5. NamedSystemMutex: catch UnauthorizedAccessException and WaitHandleCannotBeOpenedException
   and rethrow as LockException with a clear message.
6. Honour FVS_PROGRAMDATA_ROOT unchanged for dev and tests.

INVARIANT PRESERVATION MANDATES:
- Atomic write protocol unchanged. Crop Tool, Merger and main app (same user) still share one root.
- The migration runs once, is idempotent, and never deletes the legacy files.

DEFINITION OF DONE:
- Two accounts via fast user switching, both running: no cross-offered recovery, no
  LockException, and each exits cleanly without touching the other's files.
- Tests: ApplicationPaths default root is under LocalApplicationData. MutexName contains the SID.
```
