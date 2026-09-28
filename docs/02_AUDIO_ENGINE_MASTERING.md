# SPECIFICATION 02: AUDIO ENGINE & MASTERING

## Code Mini-Map: Bound Source Files & Symbols

> **⚠ CO-GOVERNED rows are bound by EVERY spec listed on them.** Reading only this one is not compliance (`SPEC_GOVERNANCE.md` §2).
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `src/FortniteVideoSoftware.Core/Media/AudioFilterChain.cs` | `AudioFilterChain` | `MusicTrack`, `AudioFilterChain` | Authoritative audio filtergraph generation for export and mastering. |
| `src/FortniteVideoSoftware.Core/Media/AudioLoudnessProbe.cs` | `AudioLoudnessProbe` | `TargetLufs`, `PeakCeilingDbtp`, `QuietBoostReductionFactor`, `MusicBedLufs` | EBU R128 integrated loudness measurement and quiet boost attenuation. |
| `src/FortniteVideoSoftware.Core/Media/VoiceRecorder.cs` | `VoiceRecorder` | `StartRecording`, `StopRecording`, `GetInputDeviceNames`, `Dispose` | Low-latency WASAPI audio capture lifecycle on serialized worker thread. |
| `src/FortniteVideoSoftware.Core/Media/MicLevelMonitor.cs` | `MicLevelMonitor` | `Start`, `Stop`, `Dispose`, `MicLevelMonitor` | Idle microphone level polling with proactive endpoint release before recording. |
| `src/FortniteVideoSoftware.App/Controls/VoiceOverPreviewPlayer.cs` | `VoiceOverPreviewPlayer` | `Reload`, `Dispose`, `UpdatePlayback`, `DisposeTakes` | Timeline-synchronized voiceover take playback (plays takes only; waveforms are drawn by `VoiceOverWindow`). |
| `src/FortniteVideoSoftware.Core/Media/MpvIpcClient.cs` | `MpvIpcClient` | `SetGlobalMasterVolume`, `ObserveProperty`, `CurrentTime`, `TimePosChanged`, `GlobalMasterVolume`, `IsPaused`, `IsEof`, `SetPropertyAsync`, `SendCommandAsync` | IPC communication with libmpv, cached player state, and the shared master preview volume (`GlobalMasterVolume`). |
| `src/FortniteVideoSoftware.App/MainWindow.axaml.cs` | `MainWindow` | `VolumeSlider`, `OnGlobalMasterVolumeChanged`, `SaveRecoveryState`, `AttachPreviewMonitor` | Master preview volume scaling (master × wizard balance per player). **⚠ CO-GOVERNED BY: 01, 04, GOV**|
| `src/FortniteVideoSoftware.App/VoiceOverWindow.axaml.cs` | `VoiceOverWindow` | `UpdateReadyLamp`, `ReportMicHealth`, `RewindFromTimelineEnd`, `IsPreviewAtTimelineEnd` | Voice Over Studio UI, microphone health reporting, and 3-second preview abort guard. **⚠ CO-GOVERNED BY: 01**|
| `src/FortniteVideoSoftware.App/MusicWizardWindow.axaml.cs` | `MusicWizardWindow` | `Name`, `FilePath`, `Title`, `Artist` | Background music arrangement, track loudness balancing, and end-of-video snapping. **⚠ CO-GOVERNED BY: 01**|
| `src/FortniteVideoSoftware.App/Controls/FluidVolumeSlider.cs` | `FluidVolumeSlider` | `OnPointerMoved`, `Render`, `IsInteracting`, `FluidVolumeSlider` | Custom high-DPI tactile volume slider control. **⚠ CO-GOVERNED BY: 04**|

---

## 1. Master Application Volume Control  {#AUD-MASTERVOL}
* **Preview Only:** The vertical master volume slider sets `MpvIpcClient.GlobalMasterVolume` (0–100) via `SetGlobalMasterVolume`, which raises `GlobalMasterVolumeChanged`. It drives each preview player's own mpv `volume` property — not the Windows process volume — and has zero impact on FFmpeg export filtergraphs.
* **Proportional Scaling:** Each mpv player's volume is the master multiplied by that player's Music Wizard balance (`MainWindow.OnGlobalMasterVolumeChanged`; the balance is 1.0 when no music is active), converted through `ToMpvVolume` for mpv's cubic curve:
  $$V_{\text{preview, game}} = V_{\text{master}} \times V_{\text{game}}, \quad V_{\text{preview, music}} = V_{\text{master}} \times V_{\text{music}}$$
* **No Mute Cache:** The balance lives in the wizard result, not in the players, so 0% simply sends 0 to every player and any value above 0% re-applies the same product. Nothing is cached.

---

## 2. Audio Mastering & Normalization Pipeline  {#AUD-MASTERING}
* **Target Integrated Loudness:**
  * Gameplay audio bus normalizes to -14.0 LUFS (I = -14.0 LUFS, TP = -1.5 dBTP, LRA = 11.0).
  * Background Music bed normalizes to `MusicBedLufs` = -14.0 LUFS (equal to the game bus) before user faders are applied; a 50% fader lands it near -20 LUFS.
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
    highpass=f=200,lowpass=f=3500,agate=threshold=0.05:attack=5:release=100
    ```
  * The historical +10 dB boost is permanently removed; dynamic gain reduction is strictly capped at 20% (-2 dB) at peak gameplay events.
* **Low-End Preservation:** Music splits via `acrossover=split=250`. Only the high band is ducked; frequencies <= 250Hz bypass ducking and are summed back:
  ```text
  acrossover=split=250[mus_low][mus_high];[mus_high][trig_final]sidechaincompress=...[mus_high_ducked];[mus_low][mus_high_ducked]amix=inputs=2:weights='1 1':normalize=0
  ```
* **Ducking Is A User Switch (DUCKOFF_01):** There is no automatic bypass for silent game audio. The wizard's ducking checkbox (`ducking_enabled`) decides: off means the trigger bus, the crossover and the compressor are not built at all and the music reaches the mix untouched (not a ratio=1 bypass). Configs written before the key existed read as off when their ratio is the old bypass value 1.0.
* **Speech Carving (separate switch):** Independently of ducking, `carving_enabled` (default on) cuts the music bed at 2 kHz: `equalizer=f=2000:width_type=h:width=1800:g=-4`.

---

## 4. Voice Over Studio  {#AUD-VOICEOVER}
* **Normalization:** Each take is matched to the game bus loudness: `loudnorm=I={gameLufs}:LRA=11:TP=-1.5`, where `gameLufs` is `TargetLufs` (-14.0) when the second-pass loudnorm runs and otherwise the measured source loudness (clamped -70 to -5). It is optional: with the `AutoVoiceNormalization` setting off (default on) takes pass through unchanged.
* **Sidechain Integration:** Voiceover muxes into the game bus before sidechain trigger generation, ducking background music automatically.
* **Take Management:** Chunks render 40% semi-transparent red overlays on timeline with visual waveform rendering. Export mixes chunks via `adelay` and `amix`, each delayed to the take's position in OUTPUT time: `granularTimeMapper` maps the take start through speed segments and cuts (without it: take start minus extract start, divided by the base speed), and when the music is mixed after the meme splice the thumbnail intro and any memes inserted before the take are added. A take starting before the body is `atrim`med:
  $$\text{Delay}_{\text{ms}} = \left(\text{granularTimeMapper}(t_{\text{take\_start}}) + t_{\text{intro}} + t_{\text{memes before}}\right) \times 1000$$
* **Live Indicators:** Microphone open state triggers a 0.9s pulsing red glow on mic button and status light; arming displays static `ARMING`.
* **The READY Lamp Reports CAPABILITY, Not Enumeration (VOMON_02):** The green lamp right of Play/Pause is lit only when the studio can actually record:
  $$\text{READY} \iff \text{HasInputDevice} \land \text{PreviewUp} \land (\text{Recording} \lor \text{MonitorOpen})$$
  Enumeration alone is not enough. An endpoint that Windows LISTS but will not OPEN — held exclusively by another app, or blocked by microphone privacy — is the single most common cause of a silent take, and a lamp driven by enumeration shows green while the meter is dead and every take is empty.
  ⚠️ **It must be re-evaluated on the playback tick.** `StartMicMonitor` QUEUES the device open on the serialised audio chain (VOASYNC_02) and returns immediately, so the monitor is always still closed at that instant. A lamp written only at that call site can never become true.
* **Name The Failure, Do Not Swallow It (VOMON_02):** `MicLevelMonitor` only logs a failed open (Warn) and returns quietly, which on screen is indistinguishable from a quiet room. Two one-shot notices are raised the moment each is provable, and both are written to the runtime log:
  * the device could not be opened (raised once the queued open has actually had its turn on the chain);
  * the device is open and has delivered pure digital silence for **6 seconds**.
* **End Of Timeline (VOEND_01):** Parked on MARK END the caret is hidden, and the next PLAY **or RECORD** restarts from MARK START. RECORD especially: `EnforceTrimEndStop` kills a take on the tick after it arms, so without the rewind every take recorded from the end of the clip comes back empty. Full rule: `01_TIMELINE_COORDINATE_MATH.md` §8 (TL-ENDSTOP).
* **Preview Safety Timeout:** Recording aborts after 3.0 seconds if the video preview clock fails to advance, cleaning up empty takes and resetting UI state.
* **Playback Rebuild Key:** Player instances rebuild keyed on target take ID to avoid infinite re-instantiation loops.
* **Preview Player Synchronization:** `VoiceOverPreviewPlayer` only plays takes; it maps start timestamps through `timeMapper` across all windows, adjusting for granular speeds, and follows the video clock (`MpvIpcClient.CurrentTime` / `TimePosChanged`). Take waveforms are drawn by `VoiceOverWindow` with `StreamGeometry`; the studio ticks on a 50ms `DispatcherTimer`. Horizontal EQ meter animates via smoothed NAudio volume.
* **Studio Open Anchor:** Studio always opens at `MARK START` (trim-in point).
* **Idle Input Monitor (`MicLevelMonitor.cs`):** Configured for 44.1kHz mono, 50ms buffer. Active during idle; must stop and release WASAPI endpoint before `VoiceRecorder` opens the recording stream.
* **Voice Protection System (VOPROT_01):** Independent toggles protect the voice from the game and from the music. In the export, across every take (0.3s ramps either side) the protected bus gets `volume='1.0-0.85*pulse'` and `equalizer=f=2500:width_type=h:width=2200:g=-3` — an 85% duck plus a -3 dB carve at 2.5 kHz. Settings persist choices (`ON`, `OFF`, `REMEMBER LAST CHOICE`).
* **Deleted Footage Seek-Skip (CUTS_02):** Cut spans are drawn on the studio's own timeline and SKIPPED in a single seek during preview playback. The studio's playback tick performs that skip BEFORE anything else it does — a tick that has wandered into deleted footage is reasoning about a frame that is not in the finished video.
* **Cross-Cut Take Warning (CUTS_02):** A take whose recorded span crosses one or more cut boundaries MUST raise `"Skipped a deleted section"`. Voice-over is anchored to the video clock; speech recorded across frames that do not exist in the export desyncs severely at render time. The take is kept — the user is warned, not silently corrected.
* **Companion Audio Pauses With A Meme Cutaway (MEME_07):** While `MemePreviewDirector.IsActive`, every voice-over take and the music bed pause, and resume when gameplay returns. Cutaways are additionally SUSPENDED outright during arming and recording: cutting away mid-take would anchor speech to frames the take never heard. Authoritative rule: `03_FFMPEG_EXPORT_PIPELINE.md` §5 (FFM-MEMEPREVIEW).
* **Thread-Bound Safety Contract:** All WASAPI operations run on an isolated serialized background worker thread off the UI dispatcher.
* **Zoom Simulation:** Runs `ZoomPreviewSimulator` matching main preview. Suspended during meme cutaways; crop clears on window close.

---

## 5. Multi-Clip Concat Synchronization  {#AUD-CONCAT}
* **A/V Lock:** Input clips join as unified audio/video segments, re-zeroing together at boundaries.
* **Silence For Silent Clips:** A clip with no audio stream gets synthesized silence (`anullsrc`) for exactly its length, so every clip contributes an audio segment to the concat.
* **Exact Clip Length (CLIPFRAMES_01):** When the edit list describes the queue, every plain clip's video is bounded to the composite's frame count (`tpad=stop_mode=clone:stop=1,trim=end_frame={n}` — a cloned frame pads a short one) and its audio is padded and cut to the same length (`apad,atrim=end={n/60}`), so the file, the preview and the music agree to the frame and no desync accumulates across boundaries:
  $$n = \operatorname{round}(\text{OutputLength}_{\text{clip}} \times 60), \quad t_{\text{audio}} = n / 60$$
* **Merger Music On The Output Clock (MUSICMAP_01):** Music start/end placed on the merged clock (Music Wizard, Merger preview) is mapped through `CompositeTimeline.MergedSecToBodyOutputSec`, so speed ramps, freezes, memes and cuts before that moment move it exactly as they move the video. The music itself is never stretched. Without a matching edit list it falls back to merged seconds ÷ base speed.
* **Offset Rebase:** Clips with asynchronous audio/video start offsets are re-zeroed prior to concatenation.

---

## 6. Feature Dialogs & Wizard Logic  {#AUD-DIALOGS}
* **Three-Way Dialog (`ConfirmDialogWindow.AskEditOrRemoveAsync`):** Active buttons for Voice Over, Add Music, and Granular Speed open `EDIT` / `CANCEL` / `REMOVE` (red danger brush). Buttons are explicitly labeled `EDIT SPEEDS` / `EDIT MUSIC`. Unhandled closes default safely to Cancel.
* **Add Music Wizard:**
  * Phase 1 measures song length column to text width + 20px.
  * Coverage step hides if song duration exceeds video duration (min window height 875px when visible).
  * `Fit By End Of Video` (FITEND_01) works backwards from where the song really ends — the last moment the track is still at a third of its own average audible energy, plus a 0.35s cushion — so the video does not end in a fade-out tail or dead air. The start is snapped onto a beat up to 1s EARLIER (never later). If the song is too short to reach back that far it refuses with a warning and changes nothing:
    $$t_{\text{music\_start}} = \text{snap}_{\le}\left(t_{\text{song\_end}} - \text{Duration}_{\text{video}}\right)$$
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
  * **MIX Rack: Two Trays (SLIDER_06 / SLIDER_08):** The two faders share one rack (`AppPanelBrush`, 3px horizontal padding) and each sits in its own inset tray (`AppSurfaceBrush`, 1px `AppBorderBrush`, 4px radius, 0px horizontal padding), so the pair reads as one control while each channel is unmistakably its own slot. The rack is pure overhead — every pixel it takes comes off the video preview beside it.
    The two trays share an edge: no spacer, their own 1px borders separate them. Each tray carries its full channel name (`VIDEO` / `MUSIC`) as a caption ABOVE the fader and the bare number below it; a CompactSlider is wide enough to hold both at no cost. Full wording lives in each tray's tooltip.
    **The floor on its width is the THUMB (SLIDER_07).** The faders carry `Classes="CompactSlider"` — the suite's narrow thumb — and carry **no `Width` or `MinWidth` at all**: a vertical Slider given any explicit width puts its rail against one edge and slices the knob in half. See `04_UI_UX_AVALONIA_SPEC.md` §1 (UI-THEME) for why.
    **The knob paints over the caption and the value (SLIDER_09).** The knob grows past the ends of its track at 0% and 100%. The Slider is the LAST child of its tray's grid (still `Grid.Row="1"`) so it paints after both labels — a high `ZIndex` alone did not lift it above them — and the labels are `IsHitTestVisible="False"` so they cannot swallow a press aimed at the knob.
    ⚠️ `ClipToBounds="False"` is load-bearing on the TRAYS as well as the rack (SLIDER_02/04): the thumb grows to `scale(2.0)` while held and paints outside its own track, and a clipping tray shears the knob off at the sides. The overflow IS the press feedback — do not clip it away.
  * **Step 1 Song Table (LIST_05 / LIST_06):** The heading strip and the list share one 1px frame whose width is the measured sum of the columns, so the table closes on all four sides and its right edge lands where the last column ends.
    **Scrollbar Gutter (LIST_07):** rows stretch to the full list width, so the last column and the row hairline otherwise run straight into the vertical scrollbar and the thumb sits ON the table content. The row template carries a 14px right margin that ends the content — and every rule in it — before the gutter, and `TrackRowFurnitureWidthPx` reserves that gap **plus** the scrollbar width so the frame encloses both. Change one and the other must follow.
    ⚠️ **A width measurement may never read back a control it sizes.** The column widths are clamped against `Step1Panel` — sized by the window and by nothing the measurement writes — never against the `ListBox`, which now sits inside a frame derived from those same widths. Reading it back closes the loop: columns pulse every layout pass and the scrollbar thumb is re-laid-out under the pointer, so dragging it jumps instead of scrolling. The frame uses `MaxWidth` (a constraint) rather than `Width` (an input), the scrollbar is permanently `Visible` so its gutter cannot change width mid-measurement, and the measurement is non-reentrant and writes only on a real change.

---

## 7. Background Music Bed Fades & Meme Continuation  {#AUD-MUSICFADE}
* **Music Fades Are Independent Of Video Fades:** The master `Enable Fades` toggle governs the VIDEO only. `AudioFilterChain` gives the music bed a linear fade-in at its very start and a linear fade-out at its very end (`EdgeFadeSec` = 1.5s), each capped at half the track. Either edge fade can be off (ISSUE_04): with the music-start note marker dragged right of MARK START the music enters at full level, and with the music-end marker dragged right of MARK END it is cut dead at MARK END.
* **Song-To-Song Crossfade:** Between songs the outgoing song fades out over its last 7s (`CrossfadeOutSec`) and the incoming song starts 3s (`CrossfadeInSec`) before that end, fading in across the overlap; both are capped at half a track.
* **Continuation Across A Meme:** With `KeepMusicDuringMeme` on and memes present, the music is mixed AFTER the meme splice: each track's duration STRETCHES by the total meme length (D'_music = D_music + D_meme) and its start shifts by the meme time inserted before it. A master audio fade (at most 1s, and at most half the last meme) is synthesized over the FINAL FRAMES only when `Enable Fades` is on and the video ends on a meme of at least 0.5s.
* **Speed Independence:** Music fade lengths are human-time constants and are NOT multiplied by the segment speed factor. Only VIDEO fade padding is speed-scaled (PadStartHumanSec x S — see `03_FFMPEG_EXPORT_PIPELINE.md` §6, FFM-FADES).

---

## 8. Preview Audio Follows The Video In Output Time  {#AUD-PREVIEWSYNC}
* **MUSICPAD_01 — the fade-in pad is part of the export body, so the music moves with it.** The Main App places the music in output seconds from MARK START (the preview's clock), but with fades on `ProcessWorker` extracts up to 1 s BEFORE MARK START (`padStartHumanSec`) and mixes the music into that padded body. Every track was therefore one pad EARLY in the file (measured 1.000 s on a 3–9 s trim at 1.1x: onset 1.923 s vs 2.918 s). `MusicPadAlignment.Align` (called in ProcessWorker right after the track list is built) adds the lead pad to every track's delay; a bed that starts ON MARK START with the lead fade pre-rolls into the fade-in from earlier in the song (song stays frame-aligned); a bed that reaches MARK END with the tail fade runs on through the fade-out pad. Voice-over takes were already measured from the padded start. Harness after the fix: onset 2.923 s vs 2.918 s predicted.
* **MUSICSYNC_01:** the music bed plan (`MusicBedPlan.Build`) is shared by the export and the live preview. The preview locates "which file, which second" from `TimelineViewModel.PreviewSourceToOutputSeconds`, the same OutputTimeline mapping as the export, non-mutating and cached. Before, the preview used raw source seconds and played one file, so it disagreed with the export by the base speed, every cut and freeze, and every track after the first.
* **MUSICSYNC_02:** ⚠ **THE MUSIC ALWAYS PLAYS AT ITS OWN NORMAL SPEED (1.0x). Only the video speed changes.** The music player's `speed` is pinned to 1.0 on every start and never adjusted. Drift is corrected by SEEKING only (`PreviewAudioSync`): 0.12 s tolerance, confirmed on 2 consecutive ticks. Voice-over takes use `PreviewAudioSync.CreateVoicePlayer` (120 ms buffering) and compensate the reader lead.
