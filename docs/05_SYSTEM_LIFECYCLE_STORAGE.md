# SPECIFICATION 05: SYSTEM LIFECYCLE & STORAGE

## Code Mini-Map: Bound Source Files & Symbols
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `src/FortniteVideoSoftware.App/DeploymentLifecycle.cs` | `DeploymentLifecycle` | `AcquireMutex`, `ClaimOrphanedMutex`, `ExecuteInstall`, `Uninstall` | OS installation/uninstallation mutex and single-instance lifecycle guard. |
| `src/FortniteVideoSoftware.App/RuntimeLog.cs` | `RuntimeLog`, `CoreLogger` | `BlockingCollection<string>`, `LogMutex`, `RotateLogs`, `RetentionDays = 14` | Decoupled asynchronous producer-consumer logging pipeline. |
| `src/FortniteVideoSoftware.Core/Infrastructure/RecoveryManager.cs` | `RecoveryManager` | `SaveState`, `LoadState`, `CheckFault`, `IsSafeModeActive`, `SchemaVersion = 1` | Continuous project session serialization, crash detection, and safe-mode recovery. |
| `src/FortniteVideoSoftware.Core/Infrastructure/AtomicJsonFile.cs` | `AtomicJsonFile` | `WriteObject`, `ReadObject`, `FileOptions.WriteThrough`, `File.Move` | Thread-safe, power-outage-safe atomic JSON file writing and parsing. |
| `src/FortniteVideoSoftware.Core/Infrastructure/ApplicationPaths.cs` | `ApplicationPaths` | `ProgramDataRoot`, `RecoveryStateFile`, `SessionStateFile`, `EnsureWritableDirectories` | System directory resolution, temp workspace paths, and sentinel lock files. |
| `src/FortniteVideoSoftware.Core/Infrastructure/UiStateStore.cs` | `UiStateStore` | `ReadInt`, `WriteInt`, `ReadString`, `WriteString` | Lightweight persistent key-value configuration and coach tour launch counts. |
| `src/FortniteVideoSoftware.App/WindowBoundsHelper.cs` | `WindowBoundsHelper` | `Track`, `RestoreBounds`, `SaveBoundsSync`, `DebounceMs = 700` | Multi-display window geometry tracking and per-screen bounds persistence. |
| `src/FortniteVideoSoftware.App/Infrastructure/MaskOverlayManager.cs` | `MaskOverlayManager` | `ApplyProfile`, `EnsureDefaults`, `RotateBackups`, `CascadeBak` | 5-tier `.bak` rotation cascade and HUD profile configuration. |
| `src/FortniteVideoSoftware.App/Services/ProjectRecoveryService.cs` | `ProjectRecoveryService` | `SerializeState`, `SaveState`, `HasUnsavedWork`, `RestoreRecoveryState` | Main App state serialization bridge for project recovery. |
| `Build.cmd` | Build Script | `FVS_SIGN_PFX`, `FVS_SIGN_PASS`, `AuthenticodeSign` | Release compilation orchestration and mandatory Authenticode digital signing. |

---

## 1. Concurrency & System Mutex Locks
* **Deployment Lifecycle Mutex:** `DeploymentLifecycle` maintains a named global system `MUTEX` (`Global\FortniteVideoSoftwareInstallMutex`) preventing concurrent installation, uninstallation, or multi-instance deployment corruptions. Orphaned mutexes left behind by abruptly terminated processes are claimed forcefully after verification of process exit.
* **Cross-Process Logging Mutex:** `Global\FortniteVideoSoftwareLogMutex` serializes log appends across the Main App, Video Merger, and Crop Tool processes on a shared file append stream handle.

---

## 2. High-Volume Asynchronous Logging Engine
* **Pipeline Architecture:**
  * Decoupled producer-consumer pipeline using `BlockingCollection<string>` bounded at 10,000 entries.
  * Under saturation, excess log entries are dropped with a single consolidated dropped-events warning to prevent native memory exhaustion.
  * Disk writes execute on a dedicated background Task (`Task.Run`).
  * UI batches display logs through a thread-safe `ConcurrentQueue` on a 1-second dispatcher timer.
* **UI Memory Streams:** Visual logging textboxes in diagnostic dialogs rigidly enforce a 100-line bounded FIFO queue to prevent UI thread heap leaks.
* **Log Rotation & Retention:**
  * Active log file is capped at $10\text{ MB}$.
  * Rotates automatically before overflow using millisecond timestamp + GUID naming:
    ```text
    FortniteVideoSoftware_{yyyyMMdd_HHmmss_fff}_{Guid:N}.log
    ```
  * Retention policy trims oldest files when exceeding 5 files, $50\text{ MB}$ total directory size, or 14 days of age.
* **Privacy & Security Gating:**
  * Full FFmpeg command-line arguments and complete filesystem paths log at `DEBUG` level only (enabled via `FVS_DEV_LOG_DIR`).
  * Production `INFO` logs sanitize sensitive paths, recording file basenames, exit codes, GPU capability discovery, and encoder fallback events.
* **Progress Spam Filtering:** Filters out FFmpeg `frame=... size=... time=... bitrate=...` progress stderr spam to protect log bounds while preserving initialization probes and error banners.
* **Error Isolation:** Disk write paths swallow I/O exceptions; `LogAppended` event callbacks are exception-guarded to ensure logging failures never crash host worker threads.

---

## 3. Window State & Directory Memory
* **Window State Persistence:**
  * `WindowBoundsHelper.Track()` manages window geometry with a $700\text{ms}$-debounced background save.
  * Granular Speed Editor enforces a minimum floor of `MinWidth=900` / `MinHeight=600` with no hardcoded fixed size.
  * Window repositioning re-applies after the Avalonia `Opened` event to counter per-monitor DPI scaling handshakes.
  * Off-screen window recovery: Recenters on the primary display only when the window's bounding box is fully outside all active virtual screen boundaries.
* **Persistent Directory Memory:**
  1. `Upload Video`: Probes `LocalAppData\Temp\Highlights\Fortnite` first; falls back to Windows user `Videos` folder.
  2. `Background Music`: Defaults to Windows user `Music` folder.
  * User-selected directories are written to configuration immediately upon selection, even if the file picker dialog is subsequently cancelled.

---

## 4. Crash Recovery & State Reset
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

## 5. Binary Metadata & Authenticode Signing Mandate
* **Win32 Executable Metadata:** The compiled `.exe` embeds complete production metadata (Product Name, Publisher, Assembly Version, File Version, Legal Copyright).
* **Authenticode Integrity Enforcement:**
  `Build.cmd` executes Authenticode signing when `FVS_SIGN_PFX` and `FVS_SIGN_PASS` environment variables are detected.
* **Mandatory Signing Failure Abort:**
  If certificate signing environment variables are present but the signing tool (`signtool.exe`) fails or returns a non-zero exit code, the build script MUST FAIL IMMEDIATELY. Silently producing or packaging an unsigned binary when signing was explicitly requested is classified as a severe security failure.