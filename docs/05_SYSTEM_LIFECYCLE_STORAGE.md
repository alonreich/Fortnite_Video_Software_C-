# SPECIFICATION 05: SYSTEM LIFECYCLE & STORAGE

## Code Mini-Map: Bound Source Files & Symbols

> **⚠ CO-GOVERNED rows are bound by EVERY spec listed on them.** Reading only this one is not compliance (`SPEC_GOVERNANCE.md` §2).
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `src/FortniteVideoSoftware.App/DeploymentLifecycle.cs` | `DeploymentLifecycle` | `AcquireMutex`, `ClaimOrphanedMutex`, `ExecuteInstall`, `Uninstall` | OS installation/uninstallation mutex and single-instance lifecycle guard. |
| `src/FortniteVideoSoftware.App/RuntimeLog.cs` | `RuntimeLog`, `CoreLogger` | `BlockingCollection<string>`, `LogMutex`, `RotateLogs`, `RetentionDays = 14` | Decoupled asynchronous producer-consumer logging pipeline. |
| `src/FortniteVideoSoftware.Core/Infrastructure/RecoveryManager.cs` | `RecoveryManager` | `SaveState`, `LoadState`, `CheckFault`, `IsSafeModeActive`, `SchemaVersion = 1` | Continuous project session serialization, crash detection, and safe-mode recovery. **⚠ CO-GOVERNED BY: GOV**|
| `src/FortniteVideoSoftware.Core/Infrastructure/AtomicJsonFile.cs` | `AtomicJsonFile` | `WriteObject`, `ReadObject`, `FileOptions.WriteThrough`, `File.Move` | Thread-safe, power-outage-safe atomic JSON file writing and parsing. |
| `src/FortniteVideoSoftware.Core/Infrastructure/ApplicationPaths.cs` | `ApplicationPaths` | `ProgramDataRoot`, `RecoveryStateFile`, `SessionStateFile`, `EnsureWritableDirectories` | System directory resolution, temp workspace paths, and sentinel lock files. **⚠ CO-GOVERNED BY: GOV**|
| `src/FortniteVideoSoftware.Core/Infrastructure/UiStateStore.cs` | `UiStateStore` | `ReadInt`, `WriteInt`, `ReadString`, `WriteString` | Lightweight persistent key-value configuration and coach tour launch counts. |
| `src/FortniteVideoSoftware.App/WindowBoundsHelper.cs` | `WindowBoundsHelper` | `Track`, `RestoreBounds`, `SaveBoundsSync`, `DebounceMs = 700` | Multi-display window geometry tracking and per-screen bounds persistence. **⚠ CO-GOVERNED BY: 04**|
| `src/FortniteVideoSoftware.App/GranularSpeedEditorWindow.axaml.cs` | `GranularSpeedEditorWindow` | `OnClosing`, `OnClosed`, `_isSafeToClose`, `ResultSegments` | Deferred-close dispatcher contract governing dialog resolution and edit hand-off. **⚠ CO-GOVERNED BY: 01, 04**|
| `src/FortniteVideoSoftware.App/Infrastructure/MaskOverlayManager.cs` | `MaskOverlayManager` | `ApplyProfile`, `EnsureDefaults`, `RotateBackups`, `CascadeBak` | 5-tier `.bak` rotation cascade and HUD profile configuration. |
| `src/FortniteVideoSoftware.App/Services/ProjectRecoveryService.cs` | `ProjectRecoveryService` | `SerializeState`, `SaveState`, `HasUnsavedWork`, `RestoreRecoveryState` | Main App state serialization bridge for project recovery. |
| `Build.cmd` | Build Script | `FVS_SIGN_PFX`, `FVS_SIGN_PASS`, `AuthenticodeSign` | Release compilation orchestration and mandatory Authenticode digital signing. |
| `dev.cmd` | Developer Harness | `VERIFY_PATCHES`, `CHECK_TAG`, `NUKE_BUILD`, `KILL_STALE`, `TRACE`, `FVS_DEV_LOG_DIR`, `FVS_PROGRAMDATA_ROOT` | Sandboxed dev launch, stale-process purge, cache nuke, pre-build fix verification, and in-repo trace logging. |

---

## 1. Concurrency & System Mutex Locks  {#SYS-MUTEX}
* **Deployment Lifecycle Mutex:** `DeploymentLifecycle` maintains a named global system `MUTEX` (`Global\FortniteVideoSoftwareInstallMutex`) preventing concurrent installation, uninstallation, or multi-instance deployment corruptions. Orphaned mutexes left behind by abruptly terminated processes are claimed forcefully after verification of process exit.
* **Cross-Process Logging Mutex:** `Global\FortniteVideoSoftwareLogMutex` serializes log appends across the Main App, Video Merger, and Crop Tool processes on a shared file append stream handle.

---

## 2. High-Volume Asynchronous Logging Engine  {#SYS-LOGGING}
* **Pipeline Architecture:**
  * Decoupled producer-consumer pipeline using `BlockingCollection<string>` bounded at 10,000 entries.
  * Under saturation, excess log entries are dropped with a single consolidated dropped-events warning to prevent native memory exhaustion.
  * Disk writes execute on a dedicated background Task (`Task.Run`).
  * UI batches display logs through a thread-safe `ConcurrentQueue` on a 1-second dispatcher timer.
* **UI Memory Streams:** Visual logging textboxes in diagnostic dialogs rigidly enforce a 100-line bounded FIFO queue to prevent UI thread heap leaks.
* **Log Rotation & Retention:**
  * Active log file is capped at 10 MB.
  * Rotates automatically before overflow using millisecond timestamp + GUID naming:
    ```text
    FortniteVideoSoftware_{yyyyMMdd_HHmmss_fff}_{Guid:N}.log
    ```
  * Retention policy trims oldest files when exceeding 5 files, 50 MB total directory size, or 14 days of age.
* **Privacy & Security Gating:**
  * Full FFmpeg command-line arguments and complete filesystem paths log at `DEBUG` level only (enabled via `FVS_DEV_LOG_DIR`).
  * Production `INFO` logs sanitize sensitive paths, recording file basenames, exit codes, GPU capability discovery, and encoder fallback events.
* **Progress Spam Filtering:** Filters out FFmpeg `frame=... size=... time=... bitrate=...` progress stderr spam to protect log bounds while preserving initialization probes and error banners.
* **Error Isolation:** Disk write paths swallow I/O exceptions; `LogAppended` event callbacks are exception-guarded to ensure logging failures never crash host worker threads.

---

## 3. Window State & Directory Memory  {#SYS-WINSTATE}
* **Window State Persistence:**
  * `WindowBoundsHelper.Track()` manages window geometry with a 700ms-debounced background save.
  * Granular Speed Editor enforces a minimum floor of `MinWidth=900` / `MinHeight=600` with no hardcoded fixed size.
  * Window repositioning re-applies after the Avalonia `Opened` event to counter per-monitor DPI scaling handshakes.
  * Off-screen window recovery: Recenters on the primary display only when the window's bounding box is fully outside all active virtual screen boundaries.
* **First-Run Geometry Is Derived From The Display (FIRSTFIT_01):** A window with nothing saved must NOT open at whatever its content and `MinWidth`/`MinHeight` happen to add up to — that number was chosen against one developer's monitor, and it fills a 1080p laptop while opening as a small box in the middle of a 4K desktop.
  On the first open only, `Track(..., fitDisplayOnFirstRun: true)` sizes the window to cover **80% of the primary display's WORKING AREA in a landscape 16:9 rectangle**, centred on that area:
  $$\text{bound} = (0.8\,W_{\text{usable}},\; 0.8\,H_{\text{usable}}), \qquad w = \text{bound}_W,\; h = \frac{w}{16/9}$$
  $$\text{if } h > \text{bound}_H: \quad h = \text{bound}_H,\; w = h \times \tfrac{16}{9}$$
  Fitting INSIDE the 80% box (rather than scaling a full-screen 16:9 fit) is what holds on both monitor shapes: on a 16:9 display the height binds and the window is exactly 80% tall; on an ultrawide the width binds first and the window stays 16:9 instead of becoming a letterbox slot.
  * **Precedence, in order:** the window's OWN saved bounds → the seed window's bounds (WINSEED_01) → the display fit. It is a first-run DEFAULT, not a policy: the moment the user moves or resizes, the debounced save writes that window's key and step 1 wins forever after.
  * **`WorkingArea`, not `Bounds`.** `Bounds` includes the taskbar, so 80% of it can still put an edge underneath it.
  * **DIPs, not pixels.** `Width`/`Height` are device-independent; `WorkingArea` is physical pixels. The area is divided by the screen's scaling going in and multiplied back to centre. Skipping that makes the window 150% too large on a 150%-scaled display — the exact machines this exists for.
  * **`MinWidth`/`MinHeight` still win.** A floor larger than the computed rectangle (the Music Wizard's 1300x730 on a small laptop) clamps up and stops being 16:9. Correct: a usable window in the wrong ratio beats a correctly-shaped one that cannot lay out its contents.
  * **Opt-IN.** Applied to the Main App, Granular Speed Editor, Voice Over Studio, Add Music wizard, Video Merger and Crop Tools. NOT to the Settings dialog, and never to the detached preview console, which opens over its owning parent by UI-DETACH.
* **Deferred-Close Dispatcher Contract (⚠️ EXPORT-CRITICAL):**
  Every tracked window closes in two turns: `OnClosing` sets `e.Cancel = true`, saves bounds, then — **MANDATORY** — sets `_isSafeToClose = true` and re-posts `Close()` on the NEXT dispatcher turn. A window that cancels the close without re-posting it only HIDES.
  * **The historic defect this encodes:** the Granular Speed Editor was the one sibling that never re-posted. `await editor.ShowDialog(this)` therefore never returned, the `MainWindow` continuation that copies `editor.ResultSegments` into `_speedSegments` never ran, and **EVERY granular edit — speed segments, freezes AND zooms — was silently discarded at export**, with FFmpeg receiving a uniform base-speed graph. Nothing failed loudly; the feature simply did nothing.
  * **Required chain:** `OnClosing` (`_isSafeToClose = true` + re-post `Close`) → `ShowDialog` resolves → `ResultSegments` → `_speedSegments` → `BuildExportSpeedSegments()` → `worker.SpeedSegments` → `GranularSpeedBuilder` split/concat graph.
  * **Teardown ordering:** re-posting also lets `OnClosed` actually run, which disposes the editor's mpv preview host instead of leaking it on every open. The preview is shut down BEFORE the window is hidden, and every render-thread/UI-thread hand-off is bounded by a timeout with a defined give-up behaviour — this is what closes the OpenGL teardown deadlock window.
* **Persistent Directory Memory:**
  1. `Upload Video`: Probes `LocalAppData\Temp\Highlights\Fortnite` first; falls back to Windows user `Videos` folder.
  2. `Background Music`: Defaults to Windows user `Music` folder.
  * User-selected directories are written to configuration immediately upon selection, even if the file picker dialog is subsequently cancelled.

---

## 4. Crash Recovery & State Reset  {#SYS-RECOVERY}
* **Post-Processing Reset:** Clicking "New File" executes the "Upload Video" command, triggering garbage collection of prior timeline chunks, speed segments, audio waveforms, and markers before opening the file picker.
* **Continuous Session Serialization:**
  * `RecoveryManager` maintains continuous session state serialization to `recovery_v2.json`.
  * The boot sequence evaluates `CheckFault()` and prompts the user to restore unclosed sessions.
  * Corrupted, truncated, or incompatible JSON recovery files are discarded cleanly to prevent startup crash loops.
* **Granular Session Preservation Invariant:**
  App-level background saves (e.g., volume slider changes or timeline scrubbing) must preserve any active `granular_session` sub-object present on disk, ensuring in-flight Granular Speed Editor edits are never wiped while open.
* **Atomic Persistence Protocol:**
  All disk saves execute via `AtomicJsonFile.WriteObject`:
  1. Write payload to a unique GUID temporary file in the target directory using `FileOptions.WriteThrough`.
  2. Flush file stream to physical disk: `stream.Flush(flushToDisk: true)`.
  3. Replace the target file atomically via `File.Move(tempPath, path, overwrite: true)`.
  Eliminates half-baked, partial, or corrupted states during power outages or system crashes.
* **Config Backup Cascade:** `crops_coordinations.conf` enforces a 5-tier `.bak` cascade prior to writes:
  $$\text{.bak4} \to \text{.bak5}, \quad \text{.bak3} \to \text{.bak4}, \quad \text{.bak2} \to \text{.bak3}, \quad \text{.bak1} \to \text{.bak2}, \quad \text{current} \to \text{.bak1}$$

---

## 4a. Developer Build Harness & Fix Sentinels  {#SYS-DEVBUILD}
`dev.cmd` is the only supported way to run a development build. Three guarantees, in order, before every mode:

1. **KILL_STALE** — kills every process whose executable lives under the repo (the app, its companion windows, and orphaned `mpv.exe` / `ffmpeg.exe` children), then shuts down the Roslyn/MSBuild servers. An orphaned app holds a lock on `bin\`, which is what makes the next build silently reuse a stale binary.
2. **NUKE_BUILD** — deletes `bin` and `obj` for EVERY project under `src\` and `tests\`. There is therefore no such thing as a stale-cache explanation for a missing fix in a `dev.cmd` build.
3. **VERIFY_PATCHES** — greps each source file for the **fix sentinel** tag that sits beside a specific fix, and halts loudly if one is absent. Fixes have been reverted between a commit and a build more than once, silently producing a binary without them and costing a full test cycle to discover.

* **Every fix that costs a test cycle to re-diagnose earns a sentinel.** Add a `CHECK_TAG` line for its tag when the fix lands, in the same change — not later.
* **`dev.cmd trace` — a log that can leave the machine (TRANSPORT_TRACE_01).** Identical to the default watch mode except `FVS_DEV_LOG_DIR` points at `.devlogs\` inside the repo instead of `%TMP%`. The rule that dev logs never land in the project tree exists so an ordinary run cannot litter it and so a log can never be committed; this mode is opt-in, announces itself, and `.devlogs/` is gitignored, so neither risk applies.
  It exists because **a log nobody can reach is a log nobody can read.** A fault that cannot be reproduced from source is diagnosed from a log, and a log sitting in a temp folder on one machine is unavailable to whoever is helping.
* **Instrument before the third guess.** The main window writes one `TRANSPORT` line for every play and pause it issues — who issued it, and the player state at that instant (`t`, `dur`, `eof`, `pausedBefore`, `frozen`, `freezeAt`, `freezeArmed`, `endParked`, `seeking`). It is per transport change, not per tick, so it is cheap enough to leave in permanently. A transport fault that survives two source-level fixes is not a reading problem; ship the trace and let the log name the line.
* ⚠️ **The default mode is `dotnet watch` hot reload, and hot reload cannot apply structural edits** — new fields, new methods, changed signatures. A fix that adds either is NOT in the running process until `dev.cmd` is stopped and restarted, however many times the file was saved. When a change does not appear, restart before re-diagnosing: the source on disk being correct is not evidence that the running binary contains it.

---

## 5. Binary Metadata & Authenticode Signing Mandate  {#SYS-SIGNING}
* **Win32 Executable Metadata:** The compiled `.exe` embeds complete production metadata (Product Name, Publisher, Assembly Version, File Version, Legal Copyright).
* **Authenticode Integrity Enforcement:**
  `Build.cmd` executes Authenticode signing when `FVS_SIGN_PFX` and `FVS_SIGN_PASS` environment variables are detected.
* **Mandatory Signing Failure Abort:**
  If certificate signing environment variables are present but the signing tool (`signtool.exe`) fails or returns a non-zero exit code, the build script MUST FAIL IMMEDIATELY. Silently producing or packaging an unsigned binary when signing was explicitly requested is classified as a severe security failure.