> **STATUS: OPEN.** Refactoring plan. Nothing here has been applied.

# TASK SPECIFICATION: R7 - VOICEOVER_GOD_CLASS

## 1. TARGET CONTEXT & SCOPE
- File Path: `src/FortniteVideoSoftware.App/VoiceOverWindow.axaml.cs`
- Target Range: whole file — **3,792 lines, 116 fields, 96 methods, 31 event subscriptions.**
- Defect Classification: Modernization Bottleneck (single-responsibility violation around live device I/O)

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism: measured clusters:
  ```
    Record 343 | Take 270 | Preview 157 | Mic 140 | Player 72
    Waveform 51 | Duck 38 | Drag 37 | Timer 23 | Level 18
  ```
  116 fields for a window whose real job is: capture from a microphone, keep a list of takes, play one back, and hand a result to the caller. The concern that makes this rank above its line count is that it owns **live unmanaged audio device handles** (`VoiceRecorder`, `MicLevelMonitor`, NAudio `WaveOut`/readers) alongside UI state. Device lifecycle bugs are the kind that survive testing and strand a microphone hot after the window closes.
- ⚠️ `docs/INDEX.md` marks this file `⚠ CO-GOVERNED` — timeline maths AND audio.
- Target Pattern: `TakeLibrary` (the takes list + their WAV file lifecycle), `RecordingSession` (device open/close, level metering, RAII disposal), `TakePlaybackController` (the NAudio player pair), leaving the window as presentation. Device ownership becomes explicit and single-owner.

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior:
  - Take WAV files are GUID-named in the temp directory AND in a voice-over directory (two paths, lines ~939 and ~946) with different retention rules. Deleting the wrong one destroys the user's recording.
  - `MpvIpcClient.GlobalMasterVolumeChanged` subscription at line 492 with its `-=` at line 3731 — symmetric today; keep it symmetric.
  - Duck-audio and protect-from-music flags are serialised into the recovery payload and consumed by `ProcessWorker`'s audio graph. Their meaning is a cross-layer contract.
  - `ApplyAndClose` is `async void` (line ~3499). Convert it to an `async Task` core with a thin adapter carrying a terminal catch — the same shape MEMECOMBO_01 established — but do NOT change when it closes the window.
  - Every device handle must be disposed on every exit path including an exception during recording. RAII or explicit `finally`; never a bare field assignment.
- Public Interface Parity: constructor, `Result` payload and the `MainWindow` entry point stay identical.

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. Pin behaviour: record two takes, delete one, preview the other, apply — assert the resulting `VoiceOverResult` and which WAV files survive on disk.
2. Extract `TakeLibrary` first (pure list + file lifecycle, no devices).
3. Extract `RecordingSession` with explicit `IDisposable` ownership of the capture device and the level monitor. Add a test that asserts the device is released when the session is disposed mid-recording.
4. Extract `TakePlaybackController`.
5. Fix the `async void` on `ApplyAndClose` as part of step 4.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: zero new warnings.
- Behavioral: oracle reproduces; no WAV the user still needs is deleted; closing the window mid-recording leaves no device open (verify with the OS mixer, not only with code reading).
- Structural: no type above 1,200 lines; window's own private field count below 50; zero `async void` outside thin adapters with terminal catches.
- Performance: mic level meter update rate unchanged.
