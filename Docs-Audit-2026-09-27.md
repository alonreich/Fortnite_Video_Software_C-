# Docs audit — 2026-09-27 (user 10:54: fix all; #84 held for review) — APPLIED 2026-09-27 17:10

Four read-only audits compared every checkable claim in `docs/` with the code. Nothing was edited.
Format: **Now** = what the doc says · **Should** = what the code does. `✅/❌` column is for the user.

## 01 Timeline math
| # | Now (doc) | Should (code) | ✅/❌ |
|---|---|---|---|
| 1 | Speed blocks must stay 1 s apart; trim sticks clamp to 1 s | Blocks may touch (gap 0, SEAM_01) | ✅ applied |
| 2 | Portrait/Internal size constants are in `CoordinateMath`; `CanvasMath` converts pixels↔time | Constants are in `CoordinateConstants`; `CanvasMath` = canvas sizes + crop rounding | ✅ applied |
| 3 | `OutputTimeline.OutputToSource` | It is `OutputToSourceRelative` | ✅ applied |
| 4 | Mini-map: `SetThumbnailButton` in Wireup.cs; `KineticScrubController.Velocity` | Button is in MainWindow.Controls.cs; no `Velocity` (use `IsFlinging`/`SeekRequested`) | ✅ applied |
| 5 | HUD sizes quantized by `CoordinateMath.QuantizeItemSize` | It is a private method of `CropToolWindow` | ✅ applied |
| 6 | Zoom floor is 240×135 (`ZoomFloorW/H`) | No such constants; floor = max 8× upscale (`MaxZoomUpscale`) | ✅ applied |
| 7 | Mini-map has no Merger EDL files | Add CompositeTimeline, MergeEdl, MergedTimeline, MergeEditorBridge, MergerPreviewPlan, EdlPreview, Timeline, Granular…Merge, MusicPadAlignment | ✅ applied |
| 8 | `MergeEditorSource` (no file named) | Lives in `MergeEditorBridge.cs` | ✅ applied |
| 9 | "VoiceOverStudio" + `MainWindow.SourceMsToOutputSeconds` leave memes out | Real names: `VoiceOverWindow._timeline`, `TimelineViewModel.SourceMsToOutputSeconds/PreviewSourceToOutputSeconds` | ✅ applied |

## 02 Audio
| # | Now | Should | ✅/❌ |
|---|---|---|---|
| 10 | Music bed normalised to −20 LUFS | −14 LUFS (same as game); a 50 % fader ≈ −20 | ✅ applied |
| 11 | Master volume = Windows process volume + a mute cache | Each mpv player's volume = master × wizard balance; no mute cache | ✅ applied |
| 12 | Ducking gate `agate 0.02 ratio 2 release 50`; low band split by highpass/lowpass | `agate 0.05 attack 5 release 100`; `acrossover=split=250` | ✅ applied |
| 13 | Silent game audio bypasses ducking automatically | No auto-bypass; ducking is a user switch (DUCKOFF_01); a separate "carving" EQ (2 kHz −4 dB) exists | ✅ applied |
| 14 | Voice protection = 85 % duck + 4 dB at 2 kHz | 85 % duck + −3 dB at 2.5 kHz (VOPROT_01) | ✅ applied |
| 15 | Voice-over delay = take start − trim start | Take's OUTPUT-time position (speed, cuts, memes, intro included) | ✅ applied |
| 16 | Voice-over always normalised to −14 LUFS + limiter | Matched to the game loudness, optional (AutoVoiceNormalization), TP −1.5 | ✅ applied |
| 17 | "Demographic FFT" sorts voices 125/210/325 Hz | Not in the code at all — remove (or mark "not built") | ✅ applied |
| 18 | Mic failure logged at Debug; PathGeometry; preview player draws waveforms; `TimePos` | Warn; StreamGeometry; player only plays takes; `CurrentTime`/`TimePosChanged` | ✅ applied |
| 19 | Music always gets 1.5 s fades; with a meme its fade-out is disabled | Fades can be off (ISSUE_04), capped at half a track; 7 s/3 s crossfade between songs; meme: music stretched, master fade only when fades are on | ✅ applied |
| 20 | "Fit by end of video" = video length − song length | FITEND_01: finds the song's real end, snaps to a beat, refuses if too short | ✅ applied |
| 21 | Merger: short clip audio padded with silence; Merger music timing not described | Each clip's audio cut/padded to its exact frame length (CLIPFRAMES_01); Merger music uses the output clock (MUSICMAP_01) | ✅ applied |
| 22 | MIX rack: three contradictory generations of sizes | Keep only the current one (SLIDER_08) | ✅ applied |

## 03 Export
| # | Now | Should | ✅/❌ |
|---|---|---|---|
| 23 | Mini-map lacks the new Merger/export files | Add MergeClipGraph, CompositeTimeline, MergeEdl, MergedTimeline, MergeClipAnalyzer, FramePtsProbe, MemeLoudness, MusicPadAlignment, IntroTag, ExportTimingTag, ExportColorPolicy, EncoderManager, TwoPassEncoding, FfmpegJobLifetime | ✅ applied |
| 24 | `GpuCapabilityProbe` test-encodes NVENC/AMF | That is `HardwareScanner`/`EncoderManager` (also Intel QSV); GpuCapabilityProbe only checks the preview GPU / RDP | ✅ applied |
| 25 | CPU fallback = `veryfast -crf 20` | Two-pass when a size is targeted; otherwise CRF 23/20/17 by quality | ✅ applied |
| 26 | RDP auto-fix sets `bEnumerateHWDuringRDP` | Sets `fEnableWddmDriver=1` and `fEnableAVC444ModeOnHWEncoder=1` (PowerShell) | ✅ applied |
| 27 | Zoom filter chain + limit formula | Two real paths (constant zoom / slow ramp); limit uses the zoom's own resolution | ✅ applied |
| 28 | Everything resampled to 60 fps | Merger 60 fps; Main App uses the project's target fps | ✅ applied |
| 29 | Merger bitrate = average, maxrate = peak | Clamped 800–100 000 kbps, bufsize = 2× maxrate; below 100 % see MERGEQUALITY_01 | ✅ applied |
| 30 | Memes cropped to 2:3; `.webp` allowed | Memes fitted + padded to the output canvas; images `.png/.jpg/.jpeg`; loudness gain + limiter | ✅ applied |
| 31 | Fade length = pad × speed; intro via `tpad clone` | Fade length = the pad itself (0.5–1 s); intro = separate still input concatenated in front | ✅ applied |
| 32 | Nothing about music vs the fade-in pad | Add MUSICPAD_01 line | ✅ applied |
| 33 | Thumbnail strip: `tpad` before `fps`, 1000 s | `tpad` after `fps`, 1 s; main path is seeked inputs + `hstack` | ✅ applied |
| 34 | Freeze size discount safe "because two-pass VBR" | Only true on CPU; GPU with a size target is single-pass CBR | ✅ applied |
| 35 | Merger estimate below 100 % uses a CQ curve with size/fps factors; 1 % overhead only for CQ | 100 % bitrate × quality ratio (MERGEQUALITY_01); 1 % on every Merger estimate | ✅ applied |
| 36 | ffmpeg search order (exe\backend, bin\Debug…) | `BinaryPathResolver`: backend, ../../../../../backend, binaries…, then bare name = system PATH (risk) | ✅ applied |
| 37 | Meme library scans .mp4/.png/.jpg | .mp4/.mkv/.avi + .png/.jpg/.jpeg | ✅ applied |
| 38 | "Merged outputs write intro only (fades unknown until P7.3)" | P7.3 shipped: fades written (OUTTAG_01) | ✅ applied |
| 39 | Merged output "else untagged" | `fvs_timing` is always written; only `fvs_intro_sec` is omitted | ✅ applied |
| 40 | Merger speed: one route | Two routes: plain clips (setpts/atempo) vs clips with effects (granular engine) | ✅ applied |

## 04 UI
| # | Now | Should | ✅/❌ |
|---|---|---|---|
| 41 | Lanes: waveform PNGs, 250 ms, LRU 96, keyed by width | Superseded by LANECACHE_02 (vector peaks, 150 ms, LRU 128, keyed by clip) | ✅ applied |
| 42 | Timeline label (chip) is a drag handle | Labels are not interactive; the thumbnail blocks are the handles | ✅ applied |
| 43 | "GRANULAR EDIT button under ADD MUSIC" | GRANULAR SPEED, upper row, right of the transport | ✅ applied |
| 44 | Blocks follow "the chip's rules" | Blocks own the rules (MERGERUX_01) | ✅ applied |
| 45 | No mention of the single-EDL preview / effects preview | Add MERGEPREVIEW_EDL_01 / MERGEPREVIEW_01 line | ✅ applied |
| 46 | Mini-map has no Merger files | Add TimelineSelect, Session, EdlPreview, Lanes, GrabCursors, LaneDiskCache, ProgressiveLanes, WaveformPeaks | ✅ applied |
| 47 | Mini-map lists ~25 method names that do not exist (CoachOverlay, FloatingNotice, AmbientBubbles, FluidVolumeSlider, …) | Replace with the real member names | ✅ applied |
| 48 | Colour tokens `AppPrimaryBrush`, `AppZoomGlow` | Do not exist — remove | ✅ applied |
| 49 | All windows `SizeToContent`, min 900×600 | Editors have per-window minimums (Main 800×600, Merger 1200×700, Wizard 1300×730, …); SizeToContent only on dialogs | ✅ applied |
| 50 | Bubbles physics 30 Hz | Physics 60 Hz, painting 30 fps | ✅ applied |
| 51 | Notice kinds Info/Success/Warning/Danger | …/Error (not Danger) | ✅ applied |
| 52 | One `ConfirmDestructiveActions=true` switch; "KEEP IT" button | Separate `Confirm…` switches with their own defaults; no global one | ✅ applied |
| 53 | Every window has a resize grip | Main App and Settings do not (Crop, Granular, Merger, Voice Over, Wizard, Finished do) | ✅ applied |
| 54 | Update dialog has "Not Now (Remind me later)", 4-way | Three buttons: Update now / Skip / Never (+ close) | ✅ applied |
| 55 | About shows the "ProgramData" root | Per-user LocalAppData root (USERSCOPE_01) | ✅ applied |

## 05 Lifecycle & storage
| # | Now | Should | ✅/❌ |
|---|---|---|---|
| 56 | Installer mutex `…InstallMutex`, method `Uninstall` | Semaphore `…_InstallerGate`; method `RunUninstallWorkerAsync` | ✅ applied |
| 57 | Log/settings mutex names without user suffix | Names end with `_<user SID>` (per user) | ✅ applied |
| 58 | Rotated log name `FortniteVideoSoftware_{date}_{guid}.log` | `Fortnite_Video_Software.log.{unixMs}.{guid}.old` | ✅ applied |
| 59 | `MaskOverlayManager` rotates the 5 `.bak` files | `CropConfigStore` does | ✅ applied |
| 60 | Build.cmd signs; `AuthenticodeSign`; `Publish` in Program.cs | Build.cmd is a wrapper; signing in `CodeSigning.cs`; `Staging.Publish` | ✅ applied |
| 61 | Add a `CHECK_TAG` line in dev.cmd; four named tests | Add a `TAG=path` line to `build/sentinels.txt` (FvsVerify); tests are `EveryFixSentinelStillResolves` + `DevCmdDelegates…` | ✅ applied |
| 62 | Upload looks in `LocalAppData\Temp\Highlights\Fortnite` first | Last used folder → Videos\Fortnite → Videos\Highlights\Fortnite → Temp\Highlights | ✅ applied |
| 63 | settings/logs/Diagnostics under %ProgramData% | Under the per-user root | ✅ applied |
| 64 | Mini-map lacks new storage files | Add MergerAutosaveStore, MergerSession, MainWindow.ToolReturn, LaneDiskCache, LaneCacheDirectory, schema 9; CoreLogger its own row | ✅ applied |

## 06 Project document
| # | Now | Should | ✅/❌ |
|---|---|---|---|
| 65 | File time compared "to the second" | Up to 2 s drift tolerated | ✅ applied |
| 66 | Title-bar "unsaved" dot = not done | Done (" •" in the title) | ✅ applied |
| 67 | Mini-map missing files | Add MergeEdl, VideoMergerWindow.History, ProjectSession, MainWindow.Project, IProjectStore, AotJson | ✅ applied |
| 68 | Tag AOTSAFETY_03 used for two different fixes | Rename one | ✅ applied |

## 07 Undo
| # | Now | Should | ✅/❌ |
|---|---|---|---|
| 69 | Crop Tool, Music Wizard and Merger have no undo | Merger has it (MERGEUNDO_01); Crop has its own stack; only the Wizard lacks it | ✅ applied |
| 70 | Mini-map missing files/symbols | Add UndoSidecarStore, Granular…History, ProjectSession, MainWindow.Project; `ReplaceCurrent`, next-labels | ✅ applied |

## 08 Composition
| # | Now | Should | ✅/❌ |
|---|---|---|---|
| 71 | 11 architecture rules; `EveryDevCmdSentinelStillResolves` | 13 rules; `EveryFixSentinelStillResolves`, `EveryCatchBlockReportsSomewhere`, `DevCmdDelegates…` | ✅ applied |
| 72 | Empty-catch baseline 59; control-lookup baseline 973 | 25 and 700 | ✅ applied |
| 73 | "Five oversized files grandfathered" | Eight | ✅ applied |
| 74 | Fix sentinel = `CHECK_TAG` in dev.cmd | `build/sentinels.txt` + FvsVerify | ✅ applied |
| 75 | Mini-map missing files | Add Faults, FaultCounters, WindowsOnlyFact, ProjectSession, MainWindow.Project, ToolNavigator, MainWindow.ToolReturn, ci.yml | ✅ applied |

## 09 Distribution
| # | Now | Should | ✅/❌ |
|---|---|---|---|
| 76 | ⚠ The runtime fingerprint ships inside the installer | **Real bug:** it is written AFTER the zip is made, so users never get it | ✅ applied |
| 77 | Patch-update consumer "complete" | **Real gap:** the app never downloads the small app-only package | ✅ applied |
| 78 | Fingerprint covers runtime binaries | Also hashes the app .exe → a patch could never match | ✅ applied |
| 79 | CI `aot-publish` measures the user download | It measures only the app publish folder | ✅ applied |
| 80 | Media re-added "through LFS" | `.jpg/.jpeg/.png` have no LFS rule | ✅ applied |

## README / governance / INDEX
| # | Now | Should | ✅/❌ |
|---|---|---|---|
| 81 | README: add `CHECK_TAG` in dev.cmd | Add a line to `build/sentinels.txt` | ✅ applied |
| 82 | README routing lists miss the Merger EDL files | Add them | ✅ applied |
| 83 | README ⚠ marks inconsistent (8 wrong, 5 missing) | Match INDEX | ✅ applied |
| 84 | GOVERNANCE: "7 North Star invariants" | 9 | ⏸ HELD for user review |
| 85 | GOVERNANCE: fixed list of 7 multi-spec files | Point to INDEX ⚠ rows (there are many more) | ✅ applied |
| 86 | GOVERNANCE: `SaveRecoveryState`/`CheckFault` in MainWindow.axaml.cs | In MainWindow.Recovery.cs / Wireup.cs | ✅ applied |
| 87 | INDEX: 15 files list different specs than their own header | Make them agree | ✅ applied |
| 88 | INDEX: SettingsManager and UpdateService listed twice, differently | One row each | ✅ applied |
| 89 | INDEX: PROJ_10/11/12, MERGEUNDO_01, ProjectMerge… point to wrong 06 sections | 06 §8 | ✅ applied |
| 90 | INDEX: UNDO_23/24/25 → wrong 07 sections; UNDO_10/11/21/22 undocumented | 07 §5; add | ✅ applied |
| 91 | INDEX: CITEST_01, FAULTTIER_02, PIPELIFE, TOOLNAV_01, MVVM_03, SYS-CI point to wrong/missing places | Fix targets or add text | ✅ applied |
| 92 | INDEX: "mini-map only" tags not in any mini-map | Add or retarget | ✅ applied |
| 93 | INDEX: PROJ_06, PROJ_07, SWITCHPROMPT_01 missing | Add | ✅ applied |

## Follow-ups found while applying (docs now describe the CODE; these need a user decision, nothing changed in src/)
- **#84 held:** A = SPEC_GOVERNANCE line 7 "7" → "9"; B = README §2 invariant 3 "Windows OS PID session" → per-mpv-player `MpvIpcClient.GlobalMasterVolume` (same fact as #11). Waiting for user ✅/❌.
- **Main App export fps:** `ProcessWorker.cs` hard-codes `targetFps = "60"` (audit #28 assumed a project fps). Docs say 60.
- **Freeze size discount:** estimator applies it on GPU single-pass CBR routes too (possible over-discount). Docs state it.
- **09 R9 defects (76–78):** documented as OPEN KNOWN DEFECTS in 09 §3; code not changed.
- **Stale code comments:** `GranularSpeedEditorWindow.axaml.cs` ~4030/4221 still say 1000 ms neighbour gap (SegGapMs = 0); `VoiceOverWindow.axaml.cs:1194` says MicLevelMonitor logs "Debug" (it is Warn); `HardwareTelemetrySampler.cs` comment reuses tag AOTSAFETY_03.
- **Unused enum value:** `UpdateChoice.NotNow` (no button produces it).
- **src `[SPEC CONTRACT]` headers disagree with spec mini-maps (12 files):** GranularSpeedEditorWindow.axaml.cs (+07), MergeEdl.cs (+03), VideoMergerWindow.EdlPreview.cs (+04), VideoMergerWindow.Session.cs (+04), FfmpegJobLifetime.cs (+08), ApplicationPaths.cs (+GOV), UpdateService.cs (+09), ProjectDocument.cs (+07), IProjectStore.cs (+08), IUserNotifier.cs (+04), StorageProviderFilePicker.cs (+05), RuntimePayloadManifest.cs (header 05, mini-map 09); LaneDiskCache.cs (+05). VideoMergerWindow.Controls.cs header says 03 (every other *.Controls.cs says 04).
- **Doc gaps (tags only routed as `[code-only]` in INDEX):** Crop Tool has no 04 section (~40 tags); UNDO_10/11/21/22, PROJ_06/07, AOTSAFETY_06, PIPELIFE_02, PROJSESSION_02/03/06, TOOLNAV_04, LAYOUTLOOP_01/02, ZOOMSIZE_02, EDGEGUARD_01, DRAGCOST_01, TRACEFLOOD_01.
- INDEX: all 322 sentinel tags now have rows (55 added); every INDEX anchor resolves.
