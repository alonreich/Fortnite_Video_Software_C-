# Checkpoint: Structured FFmpeg Export Errors

## 1. Initial State & Baseline Verification
- Baseline regression test suite `tests\MediaPipelineChecks\Program.cs` executed.
- All 13 media pipeline checks PASSED (exit code 0):
  1. Crop recovery
  2. FFmpeg filters across regional settings (en-US, de-DE, fr-FR, ar-SA)
  3. Intel low-quality preset parsing
  4. Export mix ignores preview master; Wizard levels change each channel
  5. Actual mpv preview audio matches linear levels
  6. New mpv audio previews inherit preview master
  7. Complete Main export in de-DE with muted preview
  8. Complete Merger export with Wizard gameplay mute
  9. GPU: unsupported effects retain complete graph and audio stays independent
  10. GPU: complete Main export keeps intro and speed changes in VRAM
  11. GPU: complete Merger scales and joins multiple clips in VRAM
  12. GPU: portrait crop and fades retain effects with hardware encoding
  13. GPU: unsupported hardware decoding retries once and retains NVENC
- Artifact directory: `tests\MediaPipelineChecks\artifacts\20260909-051148`

## 2. Architecture & Design Implementation
1. **Strongly typed `ExportFailure` model in `Core/Media`**:
   - `src\FortniteVideoSoftware.Core\Media\ExportFailure.cs`:
     - `ExportFailureCategory`: `Unknown`, `MissingEncoder`, `CorruptInput`, `DiskFull`, `AccessDenied`, `StartupFailure`, `EncodingFailure`, `Timeout`, `Cancellation`, `DestinationError`.
     - `ExportStage`: `Preflight`, `Analysis`, `Encoding`, `Concatenation`, `TwoPassTail`, `Finalizing`, `Thumbnail`.
     - `ExportAttemptIdentity`: Record identifying attempt index, operation, encoder, and human-readable description.
     - `ExportFailure`: Record containing `Category`, `Stage`, `Attempt`, nullable `ProcessExitCode`, nullable `NativeErrorCode`, nullable `NativeErrorSource` ("Win32", "FFmpeg"), `Summary` (plain English), `SpecificCause`, `DiagnosticLines`, `EarlierAttempts`, and `FormatDiagnosticReport()`.
2. **Diagnostic Collector & Classifier in `Core/Media`**:
   - `src\FortniteVideoSoftware.Core\Media\FfmpegDiagnosticCollector.cs`:
     - Concurrently drains stderr without blocking execution; captures explicit error codes (e.g., `-28`, `-13`, `-1094995529`, `-9`); preserves early significant error lines (up to 30) alongside bounded recent tail (100 lines), so high-frequency progress ticker lines (`frame=`, `size=`) do not evict early root causes.
   - `src\FortniteVideoSoftware.Core\Media\FfmpegErrorClassifier.cs`:
     - Conservative classification priority:
       1. Cancellation & Timeout flags.
       2. Win32 process startup exceptions (codes 2, 5, etc.).
       3. Explicit parsed FFmpeg error codes (distinguished strictly from OS exit codes).
       4. Verified stderr diagnostic signatures (driver, missing codec, corrupt header).
       5. Honest `ExportFailureCategory.Unknown` when evidence is ambiguous (no guessing or fabricating root causes).
3. **Preserve Retry Behavior in Workers**:
   - `ProcessWorker.cs` and `MergerWorker.cs`:
     - Both track per-attempt diagnostics and previous attempt history (`EarlierAttempts`).
     - Stale failure diagnostics are cleared upon successful retry (`LastFailure = null`, `FailureDetail = null`), ensuring no error dialog appears when GPU decoding fails but fallback encoding succeeds.
     - When all fallbacks fail, composite failure records the sequence of all attempts.
     - Handled preflight checks (encoder discovery, disk space) and finalization (destination file moves) with structured failure categories.
4. **UI Integration & Shared Log Isolation**:
   - `src\FortniteVideoSoftware.App\Models\ExportPayload.cs`: `ExportResult` holds `ExportFailure? Failure`.
   - `src\FortniteVideoSoftware.App\Services\MainMediaController.cs`: Propagates structured `LastFailure` to `ExportResult`.
   - `src\FortniteVideoSoftware.App\ErrorReporter.cs`:
     - Added typed overloads: `ShowAsync(Window? owner, ExportFailure failure)` and `Show(Window? owner, ExportFailure failure)`.
     - Removed `ScanForCause(ReadLogTail(200))` in `ExtractRootCause`: export errors are classified strictly from their own captured diagnostics, eliminating false attribution to unrelated log entries.
   - `MainWindow.axaml.cs` & `VideoMergerWindow.axaml.cs`: Updated to dispatch typed `ExportFailure` directly to `ErrorReporter`.

## 3. Test Verification Results
- **Managed Builds**:
  - `FortniteVideoSoftware.Core.csproj`: Build succeeded, 0 warnings, 0 errors.
  - `FortniteVideoSoftware.App.csproj`: Build succeeded, 0 warnings, 0 errors.
- **MediaPipelineChecks Suite (`tests\MediaPipelineChecks\Program.cs`)**:
  - Ran all 23 checks (13 pre-existing + 10 targeted tests).
  - All 23 checks PASSED (Exit code: 0):
    1. Crop recovery: newest valid backup, unchanged backups, defaults only as last resort
    2. FFmpeg filters remain identical across regional settings
    3. Intel low-quality preset is accepted by the bundled FFmpeg
    4. Export mix ignores preview master; Wizard levels change each audio channel
    5. Actual mpv preview audio matches linear Wizard/export levels
    6. New mpv audio previews inherit the preview master
    7. Complete Main export works with decimal commas and muted preview
    8. Complete Merger export honors Wizard gameplay mute without music
    9. GPU: unsupported effects retain their complete graph and audio stays independent
    10. GPU: complete Main export keeps intro and speed changes in VRAM
    11. GPU: complete Merger scales and joins multiple clips in VRAM
    12. GPU: portrait crop and fades retain their effects with hardware encoding
    13. GPU: unsupported hardware decoding retries once and retains NVENC
    14. Export errors: missing encoder classification
    15. Export errors: corrupt input classification and explicit code
    16. Export errors: disk full classification
    17. Export errors: access denied classification
    18. Export errors: startup failure (invalid binary path) classification
    19. Export errors: timeout and cancellation classification
    20. Export errors: early useful error preserved despite 100+ trailing diagnostic lines
    21. Export errors: unfamiliar error wording produces honest Unknown category
    22. Export errors: GPU failure followed by successful fallback leaves LastFailure null
    23. Export errors: old unrelated log entry in shared log cannot become current cause
  - Artifact directory: `tests\MediaPipelineChecks\artifacts\20260909-053121`
- **NativeAOT Verification**:
  - Executed offline NativeAOT publish:
    `dotnet publish src\FortniteVideoSoftware.App\FortniteVideoSoftware.App.csproj --no-restore -c Release -r win-x64 -p:NuGetAudit=false -p:SuppressTrimAnalysisWarnings=false -p:SuppressAotAnalysisWarnings=false -o tests\MediaPipelineChecks\artifacts\aot-verification -v:minimal`
  - Output binary: `tests\MediaPipelineChecks\artifacts\aot-verification\FortniteVideoSoftware.App.exe` (49,233,408 bytes).
  - Exit code: 0.
  - Trim/AOT analysis: 0 warnings generated by `FortniteVideoSoftware.Core` or any newly added code; only pre-existing package warnings (`Avalonia.Win32`, `NAudio.Core`, `SettingsManager` JSON reflection).
