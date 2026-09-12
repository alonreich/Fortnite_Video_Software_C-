# SPECIFICATION 02: AUDIO ENGINE & MASTERING

## Code Mini-Map: Bound Source Files & Symbols

> **⚠ CO-GOVERNED rows are bound by EVERY spec listed on them.** Reading only this one is not compliance (`SPEC_GOVERNANCE.md` §2).
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `src/FortniteVideoSoftware.Core/Media/AudioFilterChain.cs` | `AudioFilterChain` | `BuildSidechainGraph`, `ApplyCrossover`, `BuildLoudnormFilter`, `BuildAtempoFilter` | Authoritative audio filtergraph generation for export and mastering. |
| `src/FortniteVideoSoftware.Core/Media/AudioLoudnessProbe.cs` | `AudioLoudnessProbe` | `QuietBoostReductionFactor = 0.70`, `TargetLufs = -14.0`, `MeasureLoudness` | EBU R128 integrated loudness measurement and quiet boost attenuation. |
| `src/FortniteVideoSoftware.Core/Media/VoiceRecorder.cs` | `VoiceRecorder` | `StartRecordingAsync`, `StopRecordingAsync`, `WorkerThread`, `WasapiCapture` | Low-latency WASAPI audio capture lifecycle on serialized worker thread. |
| `src/FortniteVideoSoftware.Core/Media/MicLevelMonitor.cs` | `MicLevelMonitor` | `StartMonitoring`, `StopMonitoring`, `AudioLevelChanged`, `SampleRate = 44100` | Idle microphone level polling with proactive endpoint release before recording. |
| `src/FortniteVideoSoftware.App/Controls/VoiceOverPreviewPlayer.cs` | `VoiceOverPreviewPlayer` | `SyncPlayback`, `ApplyTimeMapping`, `Reload`, `RenderWaveform` | Timeline-synchronized voiceover take preview and vector waveform rendering. |
| `src/FortniteVideoSoftware.Core/Media/MpvIpcClient.cs` | `MpvIpcClient` | `SetMasterVolume`, `ObserveProperty`, `TimePos`, `GlobalMasterVolume`, `IsPaused`, `IsEof`, `SetPropertyAsync`, `SendCommandAsync` | IPC communication with libmpv, cached player state, and OS PID volume management. |
| `src/FortniteVideoSoftware.App/MainWindow.axaml.cs` | `MainWindow` | `VolumeSlider`, `_muteCache`, `OnVolumeChanged`, `SaveRecoveryState` | Master preview volume scaling and proportional mute vector caching. **⚠ CO-GOVERNED BY: 01, 04, GOV**|
| `src/FortniteVideoSoftware.App/VoiceOverWindow.axaml.cs` | `VoiceOverWindow` | `PerformFFTAnalysis`, `OnArmingTimeout`, `UpdateVisualizer`, `VoiceProtectionSystem`, `UpdateReadyLamp`, `ReportMicHealth`, `RewindFromTimelineEnd`, `IsPreviewAtTimelineEnd` | Voice Over Studio UI, demographic frequency analysis, microphone health reporting, and 3-second preview abort guard. **⚠ CO-GOVERNED BY: 01**|
| `src/FortniteVideoSoftware.App/MusicWizardWindow.axaml.cs` | `MusicWizardWindow` | `ApplyMusicBed`, `CalculateEndFit`, `MeasureColumns`, `FitByEndOfVideo` | Background music arrangement, track loudness balancing, and end-of-video snapping. **⚠ CO-GOVERNED BY: 01**|
| `src/FortniteVideoSoftware.App/Controls/FluidVolumeSlider.cs` | `FluidVolumeSlider`, `Tactile` | `OnPointerMoved`, `VolumeChanged`, `AppTubeBrush`, `EnableGlobalRipple` | Custom high-DPI tactile volume slider control. **⚠ CO-GOVERNED BY: 04**|

---

## 1. Master Application Volume Control  {#AUD-MASTERVOL}
* **Strict Process Isolation:** The vertical master volume slider controls OS process (PID) volume for preview playback only via Windows audio session APIs (`MpvIpcClient.SetMasterVolume`). It has zero impact on FFmpeg export filtergraphs.
* **Proportional Scaling:** Master volume acts as a scalar multiplier over the relative balance between gameplay and music tracks:
  $$V_{\text{preview, game}} = V_{\text{master}} \times V_{\text{game}}, \quad V_{\text{preview, music}} = V_{\text{master}} \times V_{\text{music}}$$
* **Zero-Scaling Mute Cache:** Dragging volume to 0% caches the active balance vector:
  $$\vec{V}_{\text{mute}} = \begin{bmatrix} V_{\text{game}} \\ V_{\text{music}} \end{bmatrix}$$
  Returning above 0% restores the exact proportional offset without bias drift.

---

## 2. Audio Mastering & Normalization Pipeline  {#AUD-MASTERING}
* **Target Integrated Loudness:**
  * Gameplay audio bus normalizes to -14.0 LUFS (I = -14.0 LUFS, TP = -1.5 dBTP, LRA = 11.0).
  * Background Music bed normalizes to -20.0 LUFS before user faders are applied.
* **Quiet Boost Ceiling & Attenuation:**
  Gameplay boost is capped via `AudioLoudnessProbe.QuietBoostReductionFactor = 0.70`. The engine delivers only 30% of positive lift to prevent amplifying background noise floors:
  $$\text{Gain}_{\text{boost}} = \text{TargetLUFS} - \text{MeasuredLUFS}$$
  $$\text{Gain}_{\text{applied}} = \begin{cases} \text{Gain}_{\text{boost}} \times (1.0 - 0.70) = 0.30 \times \text{Gain}_{\text{boost}}, & \text{if } \text{Gain}_{\text{boost}} > 0 \\ \text{Gain}_{\text{boost}}, & \text{if } \text{Gain}_{\text{boost}} \le 0 \end{cases}$$
  *(Example: A calculated +18 dB lift delivers +5.4 dB, landing at -26.6 LUFS. Downward cuts on hot audio apply 100%.)*
* **Sum Mix Normalization:** Lift adjustments apply to the finished mix sum, never the gameplay bus alone, locking relative voice and music balance.
* **Pre-Export Measurement Across DELETE PARTS:** EBU R128 integrated loudness is measured over ONE contiguous range. When the project carries `Cut` chunks, the surviving source ranges MUST be trimmed and concatenated FIRST, and the probe run against that concatenation:
  $$\text{MeasuredLUFS} = \text{R128}\left(\bigoplus_{r \in \text{SurvivingRanges}} \text{Audio}(r)\right)$$
  Probing the raw source measures audio the viewer never hears. A cut that removed 20 seconds of loud combat would suppress the quiet-boost lift for the whole video and ship the export under-levelled. The probe input is always the finished audio body, never the original file on disk. Cut normalization rules: `01_TIMELINE_COORDINATE_MATH.md` §5 (TL-CUTS).
* **Preview Balance Contract:** Sidechain dynamic ducking and EQ speech carving are export-only. Live preview reflects static fader balance across separate media players.

---

## 2a. Cached Player State Must Not Lag The Command  {#AUD-IPCSTATE}
`MpvIpcClient` caches player state (`IsPaused`, `IsEof`, `CurrentTime`, `Duration`) from **asynchronous** libmpv property observers. Every consumer polls that cache from a UI tick. The rule that makes the cache safe:

> **A field the caller can invalidate by issuing a command is written LOCALLY at the moment that command is sent, not when the observer eventually agrees.**

`IsPaused` has always obeyed this. `IsEof` did not, and the consequence was a hard lock (MPVEOF_01):

* `MainWindow`'s tick ends with an end-of-file guard that pauses the player.
* PLAY issues `pause=no`; the 100 ms tick fires before mpv's `eof-reached=no` event lands; the tick reads a stale `true` and pauses again.
* One frame, then paused, indefinitely — reachable from **any** route that leaves the playhead at the end: playing to it, seeking to it, or dragging a marker there.

`IsEof` is therefore cleared locally on `pause=no` and on every `seek` command. mpv still owns the truth and the observer restores `true` the moment the file really does end. Anchoring rule for the surfaces that consume it: `01_TIMELINE_COORDINATE_MATH.md` §8 (TL-ENDSTOP).

---

## 3. Sidechain Compression & Dynamic Ducking  {#AUD-SIDECHAIN}
* **Compressor Filter Specification:**
  ```text
  sidechaincompress=threshold=0.15:ratio=1.13:attack=1:release=800:detection=peak
  ```
* **Trigger Conditioning:**
  * Gameplay trigger passes through a 200Hz - 3.5kHz bandpass and noise gate:
    ```text
    highpass=f=200,lowpass=f=3500,agate=threshold=0.02:ratio=2:attack=5:release=50
    ```
  * The historical +10 dB boost is permanently removed; dynamic gain reduction is strictly capped at 20% (-2 dB) at peak gameplay events.
* **Low-End Preservation:** Music splits via `acrossover=250Hz`. Frequencies <= 250Hz bypass ducking entirely:
  ```text
  asplit[m_duck][m_pass];[m_duck]highpass=f=250[m_high];[m_pass]lowpass=f=250[m_low]
  ```
* **Silence Bypass:** Silent or absent gameplay audio bypasses sidechain compression entirely to prevent pumping artifacts.

---

## 4. Voice Over Studio & Demographics  {#AUD-VOICEOVER}
* **Demographic FFT Probing:** Offline 15-second FFT analysis probes fundamental vocal frequencies:
  * Adult Male: ~ 125 Hz
  * Adult Female: ~ 210 Hz
  * Child: ~ 325 Hz
  Unlocks selective muting checkboxes in the studio interface.
* **Normalization & Limiting:** Normalizes voiceover tracks to -14.0 LUFS matching the normalized game bus; True Peak brick-wall limiter prevents clipping on shouting bursts.
* **Sidechain Integration:** Voiceover muxes into the game bus before sidechain trigger generation, ducking background music automatically.
* **Take Management:** Chunks render 40% semi-transparent red overlays on timeline with visual waveform rendering. Export mixes chunks via `adelay` offsets relative to trim start and `amix`:
  $$\text{Delay}_{\text{ms}} = (t_{\text{take\_start}} - t_{\text{trim\_start}}) \times 1000$$
* **Live Indicators:** Microphone open state triggers a 0.9s pulsing red glow on mic button and status light; arming displays static `ARMING`.
* **The READY Lamp Reports CAPABILITY, Not Enumeration (VOMON_02):** The green lamp right of Play/Pause is lit only when the studio can actually record:
  $$\text{READY} \iff \text{HasInputDevice} \land \text{PreviewUp} \land (\text{Recording} \lor \text{MonitorOpen})$$
  Enumeration alone is not enough. An endpoint that Windows LISTS but will not OPEN — held exclusively by another app, or blocked by microphone privacy — is the single most common cause of a silent take, and a lamp driven by enumeration shows green while the meter is dead and every take is empty.
  ⚠️ **It must be re-evaluated on the playback tick.** `StartMicMonitor` QUEUES the device open on the serialised audio chain (VOASYNC_02) and returns immediately, so the monitor is always still closed at that instant. A lamp written only at that call site can never become true.
* **Name The Failure, Do Not Swallow It (VOMON_02):** `MicLevelMonitor` logs a failed open at Debug level and returns quietly, which is indistinguishable from a quiet room. Two one-shot notices are raised the moment each is provable, and both are written to the runtime log:
  * the device could not be opened (raised once the queued open has actually had its turn on the chain);
  * the device is open and has delivered pure digital silence for **6 seconds**.
* **End Of Timeline (VOEND_01):** Parked on MARK END the caret is hidden, and the next PLAY **or RECORD** restarts from MARK START. RECORD especially: `EnforceTrimEndStop` kills a take on the tick after it arms, so without the rewind every take recorded from the end of the clip comes back empty. Full rule: `01_TIMELINE_COORDINATE_MATH.md` §8 (TL-ENDSTOP).
* **Preview Safety Timeout:** Recording aborts after 3.0 seconds if the video preview clock fails to advance, cleaning up empty takes and resetting UI state.
* **Playback Rebuild Key:** Player instances rebuild keyed on target take ID to avoid infinite re-instantiation loops.
* **Preview Player Synchronization:** `VoiceOverPreviewPlayer` maps start timestamps through `timeMapper` across all windows, adjusting for granular speeds. Waveforms update via 50ms dispatcher utilizing `PathGeometry`. Horizontal EQ meter animates via smoothed NAudio volume.
* **Studio Open Anchor:** Studio always opens at `MARK START` (trim-in point).
* **Idle Input Monitor (`MicLevelMonitor.cs`):** Configured for 44.1kHz mono, 50ms buffer. Active during idle; must stop and release WASAPI endpoint before `VoiceRecorder` opens the recording stream.
* **Voice Protection System:** Independent toggles duck game audio and music by 85% and apply a 4 dB / 2 kHz carving cut during take playback. Settings persist choices (`ON`, `OFF`, `REMEMBER LAST CHOICE`).
* **Deleted Footage Seek-Skip (CUTS_02):** Cut spans are drawn on the studio's own timeline and SKIPPED in a single seek during preview playback. The studio's playback tick performs that skip BEFORE anything else it does — a tick that has wandered into deleted footage is reasoning about a frame that is not in the finished video.
* **Cross-Cut Take Warning (CUTS_02):** A take whose recorded span crosses one or more cut boundaries MUST raise `"Skipped a deleted section"`. Voice-over is anchored to the video clock; speech recorded across frames that do not exist in the export desyncs severely at render time. The take is kept — the user is warned, not silently corrected.
* **Companion Audio Pauses With A Meme Cutaway (MEME_07):** While `MemePreviewDirector.IsActive`, every voice-over take and the music bed pause, and resume when gameplay returns. Cutaways are additionally SUSPENDED outright during arming and recording: cutting away mid-take would anchor speech to frames the take never heard. Authoritative rule: `03_FFMPEG_EXPORT_PIPELINE.md` §5 (FFM-MEMEPREVIEW).
* **Thread-Bound Safety Contract:** All WASAPI operations run on an isolated serialized background worker thread off the UI dispatcher.
* **Zoom Simulation:** Runs `ZoomPreviewSimulator` matching main preview. Suspended during meme cutaways; crop clears on window close.

---

## 5. Multi-Clip Concat Synchronization  {#AUD-CONCAT}
* **A/V Lock:** Input clips join as unified audio/video segments, re-zeroing together at boundaries.
* **Silence Injection:** Shorter audio streams are padded with synthesized silence (`anullsrc`) at boundary points to prevent progressive A/V desync:
  $$\Delta t_{\text{pad}} = \text{Duration}_{\text{video}} - \text{Duration}_{\text{audio}}$$
* **Offset Rebase:** Clips with asynchronous audio/video start offsets are re-zeroed prior to concatenation.

---

## 6. Feature Dialogs & Wizard Logic  {#AUD-DIALOGS}
* **Three-Way Dialog (`ConfirmDialogWindow.AskEditOrRemoveAsync`):** Active buttons for Voice Over, Add Music, and Granular Speed open `EDIT` / `CANCEL` / `REMOVE` (red danger brush). Buttons are explicitly labeled `EDIT SPEEDS` / `EDIT MUSIC`. Unhandled closes default safely to Cancel.
* **Add Music Wizard:**
  * Phase 1 measures song length column to text width + 20px.
  * Coverage step hides if song duration exceeds video duration (min window height 875px when visible).
  * `Fit By End Of Video` aligns video tail with track end:
    $$t_{\text{music\_start}} = \max(0, \text{Duration}_{\text{video}} - \text{Duration}_{\text{song}})$$
  * Phase 3 preview timeline feeds from `OutputTimeline` including cuts and memes.
  * **Reopening With EDIT MUSIC (RESUME_01):** Reopening restores the track, offsets, sliders and checkboxes and lands on phase 3, and the track item is **synthesised** when the folder scan has not found the file — resuming must not depend on a background scan, and a song the user already used must not become unreachable because its folder changed.
    That synthesis is what made a list rebuild dangerous. `ApplyTrackFilterAndSort` used to call `OnTrackSelected(null)` whenever the selected path was not in the visible set, and a synthesised track is never in the visible set. The moment the asynchronous scan finished and re-ran that method the whole resumed session was thrown away: `_selectedTrack` went null (so the transport disabled itself and the start marker had nothing to draw), the waveform render version was bumped, and the render still in flight failed its own staleness guard, **deleted the PNG it had just produced** and returned. Step 2, reached with Back, was blank with no error anywhere.
    Three rules follow, and all three are load-bearing:
    1. **A list rebuild may clear the LIST'S highlight, never the wizard's configured track.** Rebuilds happen on a search keystroke, a sort change and every folder rescan; none of them is a user decision. Only a real click may change `_selectedTrack`.
    2. **Re-selecting the SAME song is not a change.** `OnTrackSelected` early-outs on a matching path, so a rebuild that re-highlights the current track cannot bump the render version, kill an in-flight waveform, or discard the user's scrub position and their answer to the coverage question.
    3. **Step 2 renders its waveform wherever it becomes current**, not only on the way forward from the song list. Step 2 is reachable forwards from phase 1 and BACKWARDS from phase 3, and reopening makes Back the first time that user ever sees it.
  * **Phase 3 Is Playable Before It Is Decorated (P3ASYNC_01):** `_phase3Ready` gates play, scrub and NEXT. It is set as soon as the mpv host has the file — the ruler comes from `OutputTimeline` and the caret from the player clock, and neither needs a single decoded frame. The filmstrip and the music waveform run as **detached background workers** that fill in left-to-right underneath a timeline the user is already scrubbing.
    ⚠️ Each worker re-checks `loadVersion != _phase3LoadVersion` before touching the UI: they outlive the load method, so leaving or re-entering phase 3 can otherwise land a strip from a previous selection in the current lanes. Each worker clears its OWN loading overlay; the load method's `finally` must not, or a spinner is taken down while ffmpeg is still decoding.
  * **Phase 3 Layout (LAYOUT_03):** The vertical MIX sliders occupy the VIDEO row only, so their frame's top and bottom edges land exactly on the video preview box. The ruler and both 60px lanes span the full window width. A `RowSpan` on the slider column puts it back over the timeline and is a regression.
  * **MIX Rack: Two Trays, Minimum Width (SLIDER_06):** The two faders share one rack (`AppPanelBrush`), and each sits in its own inset tray (`AppSurfaceBrush`, 1px `AppBorderBrush`, 5px radius) so the pair reads as one control while each channel is unmistakably its own slot.
    The rack is pure overhead — every pixel it takes comes off the video preview beside it — and its width was set by the **labels**, not the faders: `"Video 100%"` measures wider than the fader under it, so the text drove the column and the fader wasted the remainder. The channel name is now the tray's caption ABOVE the fader and the value below is the bare number, so nothing in a tray is wider than the fader itself (34px). With a 5px inter-tray gap and 5px rack padding the rack is roughly a quarter narrower.
    **The floor on its width is the THUMB, and it cannot be argued with (SLIDER_07).** The faders carry `Classes="CompactSlider"` — the suite's narrow thumb — and carry **no `Width` or `MinWidth` at all**: a vertical Slider given any explicit width puts its rail against one edge and slices the knob in half, which is exactly what a first attempt at a 30px fader shipped. See `04_UI_UX_AVALONIA_SPEC.md` §1 (UI-THEME) for why. Everything else in the rack is squeezed instead: 0px horizontal tray padding, a 3px inter-tray gap and 3px rack padding. Captions are abbreviated for the same reason the value label lost its word — at 9px bold `VIDEO` measures wider than the fader and would become the thing setting the tray width. Full wording lives in each tray's tooltip.
    **Trays share an edge and the knob outranks them (SLIDER_08).** There is no spacer between the two trays — their own 1px borders separate them, which reads better than border-gap-border and is the last horizontal pixel available without touching the faders. Captions are spelled out in full again: they were abbreviated only to stay narrower than a 30px fader, and a CompactSlider is wide enough to hold `VIDEO` and `MUSIC` at no cost. Each fader carries a high `ZIndex` so the knob, which grows past the ends of its track at 0% and 100%, paints OVER the caption above it and the value below it instead of being cut by them.
    ⚠️ `ClipToBounds="False"` is load-bearing on the TRAYS as well as the rack (SLIDER_02/04): the thumb grows to `scale(2.0)` while held and paints outside its own track, and a clipping tray shears the knob off at the sides. The overflow IS the press feedback — do not clip it away.
  * **Step 1 Song Table (LIST_05 / LIST_06):** The heading strip and the list share one 1px frame whose width is the measured sum of the columns, so the table closes on all four sides and its right edge lands where the last column ends.
    **Scrollbar Gutter (LIST_07):** rows stretch to the full list width, so the last column and the row hairline otherwise run straight into the vertical scrollbar and the thumb sits ON the table content. The row template carries a 14px right margin that ends the content — and every rule in it — before the gutter, and `TrackRowFurnitureWidthPx` reserves that gap **plus** the scrollbar width so the frame encloses both. Change one and the other must follow.
    ⚠️ **A width measurement may never read back a control it sizes.** The column widths are clamped against `Step1Panel` — sized by the window and by nothing the measurement writes — never against the `ListBox`, which now sits inside a frame derived from those same widths. Reading it back closes the loop: columns pulse every layout pass and the scrollbar thumb is re-laid-out under the pointer, so dragging it jumps instead of scrolling. The frame uses `MaxWidth` (a constraint) rather than `Width` (an input), the scrollbar is permanently `Visible` so its gutter cannot change width mid-measurement, and the measurement is non-reentrant and writes only on a real change.

---

## 7. Background Music Bed Fades & Meme Continuation  {#AUD-MUSICFADE}
* **Music Fades Are Independent Of Video Fades:** The master `Enable Fades` toggle governs the VIDEO only. Background music generated by `AudioFilterChain` ALWAYS receives a linear 1.5s fade-in and a linear 1.5s fade-out, whether or not video fades are enabled and regardless of their length:
  ```text
  afade=t=in:st={musicStartSec}:d=1.5,afade=t=out:st={musicEndSec-1.5}:d=1.5
  ```
* **Continuation Across A Meme (mandatory triple):** When a meme is appended, inserted or spliced and the music is permitted to play over it, three things happen TOGETHER:
  1. The music track dynamically STRETCHES its duration to cover the meme's output span (D'_music = D_music + D_meme across the covered region).
  2. The track's own internal 1.5s fade-out is DISABLED.
  3. A master audio fade is synthesized and synchronized exactly to the meme's FINAL FRAMES.
  All three are required. Omitting (1) cuts the music dead the instant gameplay ends; omitting (2) or (3) leaves the bed's own fade landing mid-meme and clashing violently with the meme's own audio at its tail.
* **Speed Independence:** Music fade lengths are human-time constants and are NOT multiplied by the segment speed factor. Only VIDEO fade padding is speed-scaled (PadStartHumanSec x S — see `03_FFMPEG_EXPORT_PIPELINE.md` §6, FFM-FADES).
