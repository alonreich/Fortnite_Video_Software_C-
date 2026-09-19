> **STATUS: OPEN.** Refactoring plan. Nothing here has been applied.

# TASK SPECIFICATION: R5 - MUSIC_WIZARD_GOD_CLASS

## 1. TARGET CONTEXT & SCOPE
- File Path: `src/FortniteVideoSoftware.App/MusicWizardWindow.axaml.cs`
- Target Range: whole file — **5,782 lines, 93 fields, 139 methods, 52 event subscriptions.** Worst methods: `SharedInit` 559 (line 863), `LoadPhase3DataAsync` 151, `AnalyzeAudioEnergyAsync` 128, `RecalculateTrackNameColumnWidth` 118, `ResumeFromInitialStateAsync` 100.
- Defect Classification: Modernization Bottleneck (single-responsibility violation; multi-phase wizard state in one scope)

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism: measured clusters:
  ```
    Track 438 | Preview 349 | Phase 322 | Waveform 88 | Volume 79
    IpcClient 68 | Timer 32 | Ffmpeg 31 | Beat 26 | Fade 22 | Loudness 11
  ```
  A **multi-phase wizard** — the one UI shape that most obviously wants per-phase state objects — is implemented as one class where every phase's fields coexist for the whole lifetime. `Phase` appears 322 times because the code constantly re-asks "which phase am I in?" instead of being handed a phase that knows. `SharedInit` at 559 lines initialises all phases at once.
  This file also spawns raw ffmpeg processes of its own (lines ~1933 and ~3326) with hand-rolled `Kill(entireProcessTree: true)` teardown at lines ~2031 and ~3358, duplicating the `GracefulProcessTerminator` ladder that `Core` already owns. It is a THIRD copy of the process-mechanics problem R3 addresses, sitting in the UI layer.
- ⚠️ `docs/INDEX.md` marks this file `⚠ CO-GOVERNED BY 01 02` — timeline maths AND audio mastering.
- Target Pattern: an explicit phase state machine (`IWizardPhase` with `Enter`/`Leave`/`Validate`), each phase owning its own fields and controls; audio analysis (`AnalyzeAudioEnergyAsync`, beat detection, waveform) moved into `Core/Media` beside `AudioLoudnessProbe` and `WaveformGenerator` where it is testable without a window; all ffmpeg invocation routed through `AsyncProcessRunner`/`GracefulProcessTerminator` instead of hand-rolled.

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior:
  - `AUTOPREVIEW_01` — "reaching for the volume IS asking for sound", hooked on `PointerPressed` and deliberately NOT on value change (the value also moves on startup seeding and on cross-window volume broadcasts). Preserve that distinction exactly.
  - The session-state write on `PointerReleased` (not per-tick) — this is what keeps a volume drag from becoming a disk-write storm. Do not move it to a value-changed handler.
  - Audio mastering invariants in `docs/02_AUDIO_ENGINE_MASTERING.md` — loudness targets, fade curves and duck amounts are calibrated numbers, not defaults.
  - `ResumeFromInitialStateAsync` — the wizard can be re-entered mid-flow from a restored session. Every extracted phase must survive being entered with pre-populated state, not only from scratch.
  - WASAPI 3-second preview clock abort.
- Public Interface Parity: constructor, `Result` payload and the `MainWindow`/`VideoMergerWindow` entry points stay identical (both call it — see `MainWindow.Wireup.cs:933` and `VideoMergerWindow.axaml.cs:388`).

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. Pin behaviour: a scripted run through every phase, plus a resume-from-mid-flow run, with the resulting `MusicWizardResult` as the oracle.
2. Extract the pure audio work FIRST — `AnalyzeAudioEnergyAsync` and beat/waveform helpers into `Core/Media`. Immediate unit-test payoff, near-zero UI risk.
3. Replace the two hand-rolled ffmpeg spawn/kill sites with `AsyncProcessRunner` + `GracefulProcessTerminator` (same fix as FFMPEGSTOP_01, third copy).
4. Introduce `IWizardPhase`; migrate ONE phase at a time out of `SharedInit`, verifying the resume oracle each time.
5. Every `+=` gets a matching `-=`; there are 52 today.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: zero new warnings.
- Behavioral: both oracles reproduce; a mid-flow resume from a pre-refactor session state still works.
- Structural: no type above 1,200 lines, no method above 120 lines; zero raw `Process.Kill` in this file.
- Performance: audio analysis wall-clock no worse than baseline.
