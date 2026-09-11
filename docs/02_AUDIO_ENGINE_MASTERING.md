# SPECIFICATION 02: AUDIO ENGINE & MASTERING

## Code Mini-Map: Bound Source Files & Symbols
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `src/FortniteVideoSoftware.Core/Media/AudioFilterChain.cs` | `AudioFilterChain` | `BuildSidechainGraph`, `ApplyCrossover`, `BuildLoudnormFilter`, `BuildAtempoFilter` | Authoritative audio filtergraph generation for export and mastering. |
| `src/FortniteVideoSoftware.Core/Media/AudioLoudnessProbe.cs` | `AudioLoudnessProbe` | `QuietBoostReductionFactor = 0.70`, `TargetLufs = -14.0`, `MeasureLoudness` | EBU R128 integrated loudness measurement and quiet boost attenuation. |
| `src/FortniteVideoSoftware.Core/Media/VoiceRecorder.cs` | `VoiceRecorder` | `StartRecordingAsync`, `StopRecordingAsync`, `WorkerThread`, `WasapiCapture` | Low-latency WASAPI audio capture lifecycle on serialized worker thread. |
| `src/FortniteVideoSoftware.Core/Media/MicLevelMonitor.cs` | `MicLevelMonitor` | `StartMonitoring`, `StopMonitoring`, `AudioLevelChanged`, `SampleRate = 44100` | Idle microphone level polling with proactive endpoint release before recording. |
| `src/FortniteVideoSoftware.App/Controls/VoiceOverPreviewPlayer.cs` | `VoiceOverPreviewPlayer` | `SyncPlayback`, `ApplyTimeMapping`, `Reload`, `RenderWaveform` | Timeline-synchronized voiceover take preview and vector waveform rendering. |
| `src/FortniteVideoSoftware.Core/Media/MpvIpcClient.cs` | `MpvIpcClient` | `SetMasterVolume`, `ObserveProperty`, `TimePos`, `GlobalMasterVolume` | IPC communication with libmpv and OS PID volume management. |
| `src/FortniteVideoSoftware.App/MainWindow.axaml.cs` | `MainWindow` | `VolumeSlider`, `_muteCache`, `OnVolumeChanged`, `SaveRecoveryState` | Master preview volume scaling and proportional mute vector caching. |
| `src/FortniteVideoSoftware.App/VoiceOverWindow.axaml.cs` | `VoiceOverWindow` | `PerformFFTAnalysis`, `OnArmingTimeout`, `UpdateVisualizer`, `VoiceProtectionSystem` | Voice Over Studio UI, demographic frequency analysis, and 3-second preview abort guard. |
| `src/FortniteVideoSoftware.App/MusicWizardWindow.axaml.cs` | `MusicWizardWindow` | `ApplyMusicBed`, `CalculateEndFit`, `MeasureColumns`, `FitByEndOfVideo` | Background music arrangement, track loudness balancing, and end-of-video snapping. |
| `src/FortniteVideoSoftware.App/Controls/FluidVolumeSlider.cs` | `FluidVolumeSlider`, `Tactile` | `OnPointerMoved`, `VolumeChanged`, `AppTubeBrush`, `EnableGlobalRipple` | Custom high-DPI tactile volume slider control. |

---

## 1. Master Application Volume Control
* **Strict Process Isolation:** The vertical master volume slider controls OS process (PID) volume for preview playback only via Windows audio session APIs (`MpvIpcClient.SetMasterVolume`). It has zero impact on FFmpeg export filtergraphs.
* **Proportional Scaling:** Master volume acts as a scalar multiplier over the relative balance between gameplay and music tracks:
  $$V_{\text{preview, game}} = V_{\text{master}} \times V_{\text{game}}, \quad V_{\text{preview, music}} = V_{\text{master}} \times V_{\text{music}}$$
* **Zero-Scaling Mute Cache:** Dragging volume to 0% caches the active balance vector:
  $$\vec{V}_{\text{mute}} = \begin{bmatrix} V_{\text{game}} \\ V_{\text{music}} \end{bmatrix}$$
  Returning above 0% restores the exact proportional offset without bias drift.

---

## 2. Audio Mastering & Normalization Pipeline
* **Target Integrated Loudness:**
  * Gameplay audio bus normalizes to $-14.0\text{ LUFS}$ ($I = -14.0\text{ LUFS}, \text{TP} = -1.5\text{ dBTP}, \text{LRA} = 11.0$).
  * Background Music bed normalizes to $-20.0\text{ LUFS}$ before user faders are applied.
* **Quiet Boost Ceiling & Attenuation:**
  Gameplay boost is capped via `AudioLoudnessProbe.QuietBoostReductionFactor = 0.70`. The engine delivers only 30% of positive lift to prevent amplifying background noise floors:
  $$\text{Gain}_{\text{boost}} = \text{TargetLUFS} - \text{MeasuredLUFS}$$
  $$\text{Gain}_{\text{applied}} = \begin{cases} \text{Gain}_{\text{boost}} \times (1.0 - 0.70) = 0.30 \times \text{Gain}_{\text{boost}}, & \text{if } \text{Gain}_{\text{boost}} > 0 \\ \text{Gain}_{\text{boost}}, & \text{if } \text{Gain}_{\text{boost}} \le 0 \end{cases}$$
  *(Example: A calculated $+18\text{ dB}$ lift delivers $+5.4\text{ dB}$, landing at $-26.6\text{ LUFS}$. Downward cuts on hot audio apply 100%.)*
* **Sum Mix Normalization:** Lift adjustments apply to the finished mix sum, never the gameplay bus alone, locking relative voice and music balance.
* **Preview Balance Contract:** Sidechain dynamic ducking and EQ speech carving are export-only. Live preview reflects static fader balance across separate media players.

---

## 3. Sidechain Compression & Dynamic Ducking
* **Compressor Filter Specification:**
  ```text
  sidechaincompress=threshold=0.15:ratio=1.13:attack=1:release=800:detection=peak
  ```
* **Trigger Conditioning:**
  * Gameplay trigger passes through a $200\text{Hz} - 3.5\text{kHz}$ bandpass and noise gate:
    ```text
    highpass=f=200,lowpass=f=3500,agate=threshold=0.02:ratio=2:attack=5:release=50
    ```
  * The historical $+10\text{ dB}$ boost is permanently removed; dynamic gain reduction is strictly capped at 20% ($-2\text{ dB}$) at peak gameplay events.
* **Low-End Preservation:** Music splits via `acrossover=250Hz`. Frequencies $\le 250\text{Hz}$ bypass ducking entirely:
  ```text
  asplit[m_duck][m_pass];[m_duck]highpass=f=250[m_high];[m_pass]lowpass=f=250[m_low]
  ```
* **Silence Bypass:** Silent or absent gameplay audio bypasses sidechain compression entirely to prevent pumping artifacts.

---

## 4. Voice Over Studio & Demographics
* **Demographic FFT Probing:** Offline 15-second FFT analysis probes fundamental vocal frequencies:
  * Adult Male: $\approx 125\text{ Hz}$
  * Adult Female: $\approx 210\text{ Hz}$
  * Child: $\approx 325\text{ Hz}$
  Unlocks selective muting checkboxes in the studio interface.
* **Normalization & Limiting:** Normalizes voiceover tracks to $-14.0\text{ LUFS}$ matching the normalized game bus; True Peak brick-wall limiter prevents clipping on shouting bursts.
* **Sidechain Integration:** Voiceover muxes into the game bus before sidechain trigger generation, ducking background music automatically.
* **Take Management:** Chunks render 40% semi-transparent red overlays on timeline with visual waveform rendering. Export mixes chunks via `adelay` offsets relative to trim start and `amix`:
  $$\text{Delay}_{\text{ms}} = (t_{\text{take\_start}} - t_{\text{trim\_start}}) \times 1000$$
* **Live Indicators:** Microphone open state triggers a 0.9s pulsing red glow on mic button and status light; arming displays static `ARMING`.
* **Preview Safety Timeout:** Recording aborts after 3.0 seconds if the video preview clock fails to advance, cleaning up empty takes and resetting UI state.
* **Playback Rebuild Key:** Player instances rebuild keyed on target take ID to avoid infinite re-instantiation loops.
* **Preview Player Synchronization:** `VoiceOverPreviewPlayer` maps start timestamps through `timeMapper` across all windows, adjusting for granular speeds. Waveforms update via 50ms dispatcher utilizing `PathGeometry`. Horizontal EQ meter animates via smoothed NAudio volume.
* **Studio Open Anchor:** Studio always opens at `MARK START` (trim-in point).
* **Idle Input Monitor (`MicLevelMonitor.cs`):** Configured for $44.1\text{kHz}$ mono, $50\text{ms}$ buffer. Active during idle; must stop and release WASAPI endpoint before `VoiceRecorder` opens the recording stream.
* **Voice Protection System:** Independent toggles duck game audio and music by 85% and apply a $4\text{ dB} / 2\text{ kHz}$ carving cut during take playback. Settings persist choices (`ON`, `OFF`, `REMEMBER LAST CHOICE`).
* **Thread-Bound Safety Contract:** All WASAPI operations run on an isolated serialized background worker thread off the UI dispatcher.
* **Zoom Simulation:** Runs `ZoomPreviewSimulator` matching main preview. Suspended during meme cutaways; crop clears on window close.

---

## 5. Multi-Clip Concat Synchronization
* **A/V Lock:** Input clips join as unified audio/video segments, re-zeroing together at boundaries.
* **Silence Injection:** Shorter audio streams are padded with synthesized silence (`anullsrc`) at boundary points to prevent progressive A/V desync:
  $$\Delta t_{\text{pad}} = \text{Duration}_{\text{video}} - \text{Duration}_{\text{audio}}$$
* **Offset Rebase:** Clips with asynchronous audio/video start offsets are re-zeroed prior to concatenation.

---

## 6. Feature Dialogs & Wizard Logic
* **Three-Way Dialog (`ConfirmDialogWindow.AskEditOrRemoveAsync`):** Active buttons for Voice Over, Add Music, and Granular Speed open `EDIT` / `CANCEL` / `REMOVE` (red danger brush). Buttons are explicitly labeled `EDIT SPEEDS` / `EDIT MUSIC`. Unhandled closes default safely to Cancel.
* **Add Music Wizard:**
  * Phase 1 measures song length column to text width $+ 20\text{px}$.
  * Coverage step hides if song duration exceeds video duration (min window height $875\text{px}$ when visible).
  * `Fit By End Of Video` aligns video tail with track end:
    $$t_{\text{music\_start}} = \max(0, \text{Duration}_{\text{video}} - \text{Duration}_{\text{song}})$$
  * Phase 3 preview timeline feeds from `OutputTimeline` including cuts and memes.