# Video Merger Migration: Virtual Timeline (EDL) + Granular Editing

> ## ⛔ READ THIS BANNER FIRST — AI AGENT HANDOFF PROTOCOL
>
> This file is the **single source of truth** for this migration. Several AI agents will work on it
> one after another, because each agent's context/token budget runs out. Treat every agent as one
> synced unit. The next agent must be able to continue **exactly** where the previous one stopped.
>
> **When you START a session:**
> 1. Read this whole file, top to bottom, before touching code.
> 2. Read `§11 PROGRESS LOG` (bottom). The LAST entry says where work stopped.
> 3. Find the first task whose status is `[~] IN PROGRESS` or `[!] BLOCKED`. Resume it. If none, take
>    the first `[ ] TODO` task in phase order. Never skip ahead past an unfinished task's
>    dependencies (`Depends on:` line).
> 4. Re-verify the working tree before you trust any status (see `§3 ENVIRONMENT`, "Drift check").
>    If a `[x] DONE` task's tests no longer pass, set it back to `[~]` and log why.
>
> **🔁 CONTINUOUS VERBOSE DOCUMENTATION — MANDATORY, NOT OPTIONAL:**
> Document AS YOU GO, never "at the end". At every moment this file must answer, for a stranger:
> (a) what is DONE and verified (with the test command and result), (b) what is IN PROGRESS and
> exactly where (file, method, the next concrete step), (c) what is NOT YET TOUCHED, (d) which new or
> existing logic is UNTESTED, (e) every decision, assumption and gotcha discovered. Update the task's
> `Status`, its `Notes:` and `§11 PROGRESS LOG` after EVERY step (file created or edited, test written,
> test run, decision taken), and sync this file to the device immediately (§3). Also log non-plan work
> the user asks for mid-migration. If it is not written here, it did not happen.
>
> **While you WORK:**
> 5. Before editing code for a task, set its status to `[~] IN PROGRESS` and add a log line
>    `START <task-id>` to `§11`. Save and sync this file to the device immediately (§3).
> 6. After EVERY meaningful step (file created, test written, test passing, design decision made),
>    append one line to `§11`. Small and often. Your budget can end at any moment. Anything not
>    written here is lost.
> 7. Record new facts you discover (file paths, line numbers, gotchas, API semantics) in the task's
>    `Notes:` field, so the next agent does not rediscover them.
>
> **Declaring a task DONE — all four are required:**
> 8. Every item in the task's `Tests to declare success` list is run and passes. Record the exact
>    command and result in `§11`. "It compiles" is never success.
> 9. The standard gates in `§4` pass (build, AOT/TWAE, unit tests, sentinels, architecture rules).
> 10. The changed files are synced to the user's device and md5-verified (§3).
> 11. Status set to `[x] DONE`, with the date and a one-line summary in `§11`.
>
> **If you cannot finish:**
> 12. Leave the task `[~] IN PROGRESS`. Write in `§11` exactly what is done, what is half-done (with
>     file + method names), and the next concrete step. Never leave the code base uncompilable on
>     the device. If a change is half-applied and does not build, either finish it or revert it
>     before your session ends.
>
> **Hard rules (from the user, non-negotiable):**
> - Never suppress, mute, or downgrade compiler/trim/AOT warnings. Fix them at the source.
> - Never block the UI thread. Heavy work goes to background workers and syncs back when ready.
> - Music always plays at its own normal speed. Never time-stretch music. Only video speed changes.
> - Ask the user when intent is ambiguous. Do not guess product behaviour.
> - The user wants short, direct, condensed replies, with a clear bottom line: done / not done /
>   waiting for user.
> - Code-edit presentation for the user (when showing code): FILE, OLD/CURRENT CODE (exactly 2 lines
>   of context above and below), NEW REPLACEMENT CODE (same context). No truncation or placeholders.
>
> **Status legend:** `[ ] TODO` · `[~] IN PROGRESS` · `[x] DONE` · `[!] BLOCKED (reason)` · `[-] DROPPED (reason)`

---

## 0. ▶ RESUME HERE — SNAPSHOT (overwrite this section on every hand-off; §11 stays the full history)

**Updated:** 2026-09-28 01:00 (+03) by agent-1.

P0–P9 DONE. P10 code DONE (manual M-P10 pending). P11 code DONE (manual M-P11 pending). P12 (user 2026-09-28 00:31: gentler ants, waveform seeks, red playhead line) code DONE (manual M-P12 pending) — see P10/P11/P12 in §7.

**Open-issues list (user 00:31 item 1):** sent to the user for confirm/deny, NOT resolved yet (numbered 1–18 in the chat reply; same content as the follow-ups at the bottom of `Docs-Audit-2026-09-27.md` plus the older open items).

**Docs audit (user 10:54 "fix all, #84 review"):** 92 of 93 items APPLIED to docs/ (01–09, README, GOVERNANCE, INDEX); docs only, no src change. #84 HELD: exact old/new text sent to the user (A = GOVERNANCE line 7 "7"→"9"; B = README §2 invariant 3 volume wording). Follow-ups needing a user decision are listed at the bottom of `Docs-Audit-2026-09-27.md` (src header mismatches, stale code comments, ProcessWorker fps hard-coded 60, freeze discount on GPU, R9 defects 76–78 documented as OPEN).

**Waiting for the user:** confirm/deny open-issues 1–18; #84 A/B ✅/❌; manual M-P10 / M-P11 / M-P12 (dev.cmd); `Build.cmd --no-publish`; MUSICPAD_01 re-test; R9 decision; whether to fix the follow-ups in code.

---

## 1. GOAL (one paragraph)

Turn the **Video Merger** (and ONLY the Merger; the Main App changes only in the tiny export-tag
task P1) from "join clips end to end" into a **virtual, non-destructive timeline** (an EDL: a list of
"clip X from frame A to frame B, plus effects"). The user edits the merged video as if it were one long
video: speed ramps, freeze frames, zoom/pan, memes. They get an instant, seamless preview, background
thumbnails and waveform, save/restore before merging, and undo/redo. The final export renders from the
ORIGINAL clips in one FFmpeg pass, frame-exact, with the preview and the export always agreeing to the
frame. No proxy file is ever written.

---

## 2. USER DECISIONS (binding, do not re-ask)

| # | Decision | Answer |
|---|---|---|
| D1 | Architecture for the Merger | Virtual timeline (EDL). No physical proxy file. Merger only. |
| D2 | Preview | Try mpv's inline `edl://` (one seamless virtual file). Short spike first. If it fails the criteria in P4, keep the current per-clip file switching. |
| D3 | Granular Speed Editor in the Merger | Yes: variable speed, freeze frames, zoom/pan, mid-timeline memes. Reuse the Main App's editor, do not fork a copy. |
| D4 | Effect scope | Each effect stays **inside one clip**. It can be mid-clip, at the clip's END (a meme is inserted **before the fade-out begins**) or at its START (a meme is inserted before the clip, and the clip's **fade-in plays immediately after the meme**). |
| D5 | Speed/freeze over fades | **Allowed.** |
| D6 | Save/restore | Yes. All Merger edits survive closing the app **before** MERGE is pressed (autosave and restore). |
| D7 | Undo/redo | Yes, in the Merger. |
| D8 | Music | Always normal speed. If the video BEFORE the music start is sped up or slowed, the music still starts on the **same video moment** (so in the finished file it moves earlier or later). |
| D9 | Filmstrip + waveform | Yes, along the whole merged timeline. Built by background parallel workers. Thumbnails appear **left to right as each is ready**, never blocking. Everything is usable immediately. |
| D10 | Mixed portrait/landscape | Zoom/pan applies to what the user sees in the FINISHED video (output canvas). |
| D11 | Intro tag precision | Store the intro as a **frame count + frame rate** (exact). Old seconds-tag files still work. |
| D12 | Fade info | The Main App saves each export's fade-in and fade-out lengths (either can be 0: start only, end only, both, or none). |
| D13 | Intro removal rule (unchanged from the Scraper) | Remove ALL intros except clip 1's intro. Clip 1's intro is also removed and replaced when the user picks a custom thumbnail in the Merger. |
| D14 | Selection feedback (2026-09-26) | A highlighted clip shows the yellow gently moving marching ants in BOTH the right-hand list and the timeline. Moving a clip in either list (vertical list / horizontal timeline blocks) moves it in the other. |
| D15 | Publishing (2026-09-26) | `.\Build.cmd` ALONE publishes to GitHub and replaces the previous release (the publisher already deletes old releases). **Option A chosen:** publish even with the local dev certificate (SIGNLOCAL_02). Users get the update notification; in-app install sends them to the release page until a public certificate is used. |
| D16 | Granular editor host (2026-09-26) | **Whole merge at once.** ONE Granular Speed Editor over the entire merge (not per clip). The editor plays the merge as one virtual file (mpv `edl://`), shows clip dividers, and every effect is normalised into the clip it belongs to on Accept (D4 holds by construction). |
| D17 | Meme Start/Mid/End (2026-09-26) | **By position.** A meme dropped inside a clip's fade-in region (or on its first frame) = AtStart; inside its fade-out region (or on its last frame) = AtEnd (inserted before the fade-out); anywhere else = Mid. |
| D18 | Cuts in the Merger (2026-09-26) | **Allowed.** DELETE PARTS works in the merge editor like the Main App; `EdlEffects.Cuts`; preview and export skip them. |
| D20 | Clip actions (2026-09-26) | Delete key + right-click Remove in the list AND on the timeline; the filmstrip lane shows clips as draggable, selectable blocks synced with the list; reorders repaint instantly from caches, never blocking the UI. |
| D21 | Session restore (2026-09-26) | Keep the SILENT restore of the last Merger session, but probe every file first; if any are missing or changed, show an approval dialog naming each file and its folder (CONTINUE WITHOUT THEM / START FRESH). After a successful MERGE: no preference → the session is kept (unchanged). |
| D19 | Base speed semantics (agent decision, 2026-09-26) | Same as the Main App editor the user edits with: a speed segment's speed is ABSOLUTE; the global base speed applies only to footage outside segments; freezes/memes are real-time holds. (P2.3 first divided the whole output by BaseSpeed; changed in P6.2.) |

---

## 3. ENVIRONMENT, SYNC AND DRIFT CHECK (how the previous agents worked)

- **User's repo (source of truth, Windows):** `C:\Fortnite_Video_Software - C#`
  - Reachable from the cloud container ONLY through the device bridge tools (`mcp__remote-devices__*`):
    `device_bash` runs in a Linux VM with the folder mounted at `$HOME/mnt/Fortnite_Video_Software - C#`.
    `device_commit_files` writes files; its paths are Windows paths.
  - The device may disconnect. If a bridge call fails, retry once, then tell the user.
- **Agent's working mirror (cloud container):** `/home/claude/fvs` is a git repo mirroring the user's
  `src/ tests/ build/ docs/ dev.cmd` (plus this file). Commit locally after each task
  (`git -c core.autocrlf=false commit`). Git is local only. Never push.
  - If the container was reset and `/home/claude/fvs` is missing: rebuild the mirror by staging
    the needed files from the device (`device_stage_files`) into the container.
- **Line endings:** files are a MIX of LF and CRLF, some with a UTF-8 BOM. Preserve each file's
  original EOL and BOM when editing. Safe edit helper (Python): read bytes, detect BOM and CRLF,
  normalise to LF, replace, restore CRLF and BOM, write bytes. Never use `git stash` on this repo:
  EOL normalisation corrupted a stash once.
- **Version stamps:** `Build.cmd` rewrites `<Version>` lines in both `.csproj` files, plus
  `version.txt` and `Directory.Build.props`, on every run. Before overwriting a `.csproj` on the
  device, copy the device's current version numbers into your copy.
- **Sync procedure (every task):**
  1. List changed files: `git diff --name-only HEAD~1 HEAD` (plus untracked).
  2. **Drift check:** md5 each target file ON THE DEVICE and compare with your pre-change base
     (`git show HEAD~1:<file> | md5sum`). If a device file differs (the user edited it), stage it into
     the container, merge, and only then write.
  3. Copy the files to `/mnt/user-data/outputs/<batch>/...` and call `device_commit_files` with
     `stagedPath` + Windows `devicePath`.
  4. Re-md5 on the device and compare with local. They must match.
- **Helper scripts (container, recreate if missing):**
  - `b.sh`: `dotnet build src/FortniteVideoSoftware.App/FortniteVideoSoftware.App.csproj -c Debug -p:EnableWindowsTargeting=true -p:ApplicationIcon= -v q -nologo`
  - `t.sh`: builds `tests/FortniteVideoSoftware.App.Tests` with
    `-p:EnableWindowsTargeting=true -p:ApplicationIcon= -p:RuntimeIdentifier=linux-x64 -p:SelfContained=false`.
    Then removes `Microsoft.WindowsDesktop.App` from the test `runtimeconfig.json` `frameworks` list
    if present, and runs App tests (`--no-build`) and Core tests.
  - `aot.sh`: copies `src` and `Directory.*` to `/tmp/aotrepro`, forces TFM `net9.0` and RID `linux-x64`,
    and runs `dotnet publish -c Release -p:PublishAot=true -p:TreatWarningsAsErrors=true` (with
    `NoWarn=CS1998;CS0414;CA1416;CS0162` for the Linux repro only). Must produce **zero** IL warnings.
  - `dotnet` is at `/root/.dotnet`. `ffmpeg` and `ffprobe` are at `/usr/bin` (used by the merge
    integration harness, see P7).
- **Known environment-only test failure:** `OutputSizeProbeTests.RealMediaProbeRecovers...` needs
  the repo's `binaries\` folder (absent in the container). It is expected to pass on Windows.

---

## 4. STANDARD GATES (every task, before `[x] DONE`)

1. `b.sh`: 0 errors, 0 warnings.
2. Release Windows-TFM build with TWAE:
   `dotnet build src/FortniteVideoSoftware.App/FortniteVideoSoftware.App.csproj -c Release -p:EnableWindowsTargeting=true -p:ApplicationIcon= -p:TreatWarningsAsErrors=true`
   must give 0/0.
3. `TWAE=true aot.sh`: zero output lines (no IL warnings, no errors).
4. `t.sh`: all Core tests pass; all App tests pass except the one environment-only test in §3.
5. `dotnet run --project build/FvsVerify` prints "all present" (sentinels). Add a sentinel line in
   `build/sentinels.txt` for every new tag ID you introduce.
6. `ArchitectureRuleTests`:
   - window code-behind ceilings (`*.axaml.cs` line counts) must not grow. Put new behaviour in NEW
     partial files (`VideoMergerWindow.<Feature>.cs`); ceilings may only go DOWN.
   - no new `async void`.
   - no new blocking waits (`.Result`, `.Wait()`, `GetAwaiter().GetResult()` on `Task`-named
     expressions). Use a local variable if you must read a completed task's result.
7. Specs: every new tag ID gets a line in `docs/INDEX.md` and a bullet in the owning `docs/0x_*.md`
   section.
8. Sync to the device and md5-verify (§3).

---

## 5. CURRENT STATE (what already exists, 2026-09-26)

Already built and synced (do NOT redo):

| Area | What exists | Where |
|---|---|---|
| Intro tag v1 | Main App and Merger exports stamp `fvs_intro_sec=0.100` (mp4 `mdta` key via `-movflags +faststart+use_metadata_tags`) | `Core/Media/IntroTag.cs`; stamped in `ProcessWorker` (single-pass, slow pass 2, two-pass tail) and `MergerWorker` |
| Clip analysis | One background ffprobe per file, cached by path+size+mtime, 2–4 in parallel | `Core/Media/MergeClipAnalyzer.cs` (`MergeClipInfo`: duration, intro sec, audio, w/h) |
| Merged timeline (seconds) | `MergedTimeline`: clip windows, `Locate(merged)→(clip,src)`, `ToMerged`, `Remap`, `Signature`, `ContentStarts()`; custom-thumbnail rule | `Core/Media/MergedTimeline.cs` (tests: `Core.Tests/ThumbnailScraperTests.cs`) |
| Scraper UI | Bottom-left "Thumbnail Scraper" checkbox + Settings ▸ Defaults; settings schema **v8** forces it on | `VideoMergerWindow.Scraper.cs`, `Controls/SettingsWindow.Merger.cs`, `Infrastructure/SettingsManager.cs` |
| Merged preview | One long timeline, dividers + clip labels (✂ = intro cut), per-file switching at clip ends, `SeekPreview`/`PreviewPositionSec`/`PreviewDurationSec` | `VideoMergerWindow.Timeline.cs` |
| Custom thumbnail | SET / MOVE HERE / REMOVE THUMBNAIL button + draggable camera marker; export prepends a 0.1 s still after the music mix | `VideoMergerWindow.Timeline.cs`, `MergerWorker` (`ThumbnailClipIndex`, `ThumbnailSourceSec`) |
| Music preview in Merger | Audio-only mpv, `MusicBedPlan`, seek-only drift correction, speed pinned to 1.0 | `VideoMergerWindow.MusicPreview.cs` |
| Music Wizard lanes | Wizard uses the scraped clip windows | `MusicWizardWindow.Merger.cs` (`MergerClipWindows`) |
| Double-click clip | Opens that file in the Windows default player | `VideoMergerWindow.Scraper.cs` |
| Zero-warning AOT | Avalonia 11.3.22, SkiaSharp 3.119.2, NAudio.Core + NAudio.WinMM only (`WavAudioReader`), first-party D3D11 interop (no Vortice) | `AOTCLEAN_01..04`, `docs/06 §10` |
| Build signing | Local dev cert in `ssl-certificate\`; the build signs with it and refuses to publish | `SIGNLOCAL_01` (`build/FvsBuild/CodeSigning.cs`, `Program.cs`) |
| ILC crash retry | Retries publish once with `IlcSingleThreaded` on the ILC internal-crash signature | `ILCCRASH_01` (`build/FvsBuild/Staging.cs`) |
| Release sidecar | `runtime.manifest.json` → `obj\ReleaseAssets\` (not `compiled\`) | `RELEASEASSETS_01` |

**Last user-confirmed build state (2026-09-25):** a `Build.cmd` run compiled and signed successfully,
then failed on `runtime.manifest.json` inside `compiled\`. `RELEASEASSETS_01` fixes that; a re-run
has not yet been confirmed. On the device, the Avalonia 11.3 upgrade, the D3D11 interop and
`WavAudioReader` are **untested at runtime** (preview picture, voice-over playback, drag-reorder).

**Existing Main App building blocks this migration must REUSE (read these before designing):**
- `Core/Media/GranularSpeedBuilder.cs`: `SpeedSegment(StartMs, EndMs, Speed, ZoomX/Y/W/H, ZoomOrigRes, ZoomSlow, ZoomStartMs, ZoomEndMs)`, and the source→output timing math (TIME_01).
- `Core/Media/OutputTimeline.cs`: the single source→output time authority ("North Star #2").
- `Core/Media/MemePlacement.cs`: `MemePlacement(FilePath, AtSourceSecRelative, DurationSec, Id)`. Memes are CLIP-RELATIVE source seconds.
- `Core/Media/ProcessWorker.cs`: the full single-video chain (timing stage → colour → fades → intro → portrait/HUD → memes → voice-over). Fades: `padStartHumanSec` and `padEndHumanSec` (output seconds), applied to the body BEFORE the intro concat.
- `GranularSpeedEditorWindow.axaml.cs` (≈8 000 lines, ceiling 8075): ctor
  `(videoPath, trimStartMs, trimEndMs, existingSegments, baseSpeed, freezeTimeMs, freezeDurationS, isMobileFormat, originalResolution, voiceOverResult, existingCuts, existingMemes, preProbedDurationSec)`.
  It is built around ONE source file.
- `Core/Undo/UndoStack.cs` (`UndoStack<T>`, rules U1–U4, max depth 40) and `Core/Project/ProjectDocument.cs` (`ProjectMerge { Clips, BaseSpeed }`, structural `Equals`).
- `Core/Infrastructure/RecoveryManager.cs` (versioned writes), `App/MainWindow.Recovery.cs` (the 750 ms write-behind pattern to copy).

---

## 6. TARGET ARCHITECTURE (short)

**Three clocks (never mix them):**
1. **Source frame:** frame index inside one file (from real probed timestamps; files can be VFR).
2. **Merged frame:** kept content of all clips end to end, 1.0x, in frames at the MERGE fps (60, the
   export's CFR). Integer arithmetic only.
3. **Output time:** the finished file, after per-clip speed/freeze/memes, the global base speed and
   the synthetic thumbnail intro. Computed by ONE composite mapper that is shared by preview, music
   and export.

**Data model (`Core`, pure, unit-testable, no UI):**
- `MergeEdl` (new): an ordered list of `EdlClip { ClipId (stable GUID), Path, FileIdentity(size,mtime),
  SourceWindow (frame in/out after intro removal), FadeInFrames, FadeOutFrames, Effects }`, plus
  `Thumbnail (ClipId, sourceFrame)`, `BaseSpeed`, `ScraperEnabled`, `Music (anchor ClipId + source
  frame, end anchor, offset, volumes, loop...)`.
- `EdlClip.Effects`: clip-local speed segments, freezes, zoom/pan and memes, stored the way the Main
  App stores them BUT anchored to `(ClipId, source frame)`. Memes carry a placement kind:
  `Mid | AtStart (before clip, fade-in follows) | AtEnd (before fade-out begins)`.
- `CompositeTimeline` (new): built from `MergeEdl`. Provides merged↔(clip, source) and merged↔output,
  and per-clip `OutputTimeline`s. It REPLACES the seconds-based math inside `MergedTimeline`. Keep
  `MergedTimeline` as a thin adapter until every caller has moved over.

**Preview:** mpv receives one inline `edl://` URL built from the EDL windows (if P4 passes), so it
plays the merge as one virtual file. Speed and freeze in preview follow the Main App's preview model
(per-segment `speed` property changes plus timed pauses). Memes use the Main App's
`MemePreviewDirector`.

**Export:** `MergerWorker` builds one FFmpeg graph. For each clip:
trim to its source window → the clip's own granular chain (reuse `GranularSpeedBuilder`) → meme
insertions (Mid/AtStart/AtEnd) → canvas normalise (scale/pad/crop + colour, existing code) → the
clip's fades stay as they are in the pixels. Then concat all clips → global base speed → music mix
(music placed by output time from the composite mapper, 1.0x) → limiter → optional thumbnail intro
→ encode. Stamp the timing tag (P1) on the result.

---

## 7. PHASES AND TASKS

> Order matters. Each task lists **Depends on**. Test IDs are referenced in the progress log.

### P0 — Handoff document
- **P0.1 Write this plan file and sync it to the device root.** Status: `[x] DONE`
  - Tests to declare success: the file exists on the device at `C:\Fortnite_Video_Software - C#\Video-Merger-Migration.md` and its md5 matches the container copy.

### P1 — Timing tag v2 (Main App export + Merger reader) — D11, D12
- **P1.1 Define the tag v2 format** · Status: `[x] DONE` (2026-09-26) · Depends on: —
  - One mp4 metadata key `fvs_timing` = `v=2;fps=<num>/<den>;intro=<frames>;fadein=<frames>;fadeout=<frames>`.
    Frames are at the EXPORT's output fps (the Main App exports CFR). Missing or 0 = none.
  - Keep writing `fvs_intro_sec` for backward compatibility (older builds of the Merger read it).
  - Files: `Core/Media/IntroTag.cs` → extend or rename to `ExportTimingTag` (keep the `IntroTag` API as a
    wrapper so existing callers compile). Parse must be culture-invariant and tolerant (unknown keys ignored).
  - Tests to declare success:
    - T1.1a unit: round-trip format ↔ parse for all fade combinations (none / in / out / both).
    - T1.1b unit: v1-only file (`fvs_intro_sec=0.100`) parses as intro = round(0.1 × fps) frames with fps unknown → falls back to seconds.
    - T1.1c unit: garbage values rejected (negative, > limits, non-numeric), intro ≤ half the file.
- **P1.2 Main App stamps tag v2** · `[x] DONE` (2026-09-26) · Depends on: P1.1
  - `ProcessWorker`: at every final write (single-pass, slow pass 2, two-pass tail) pass intro frames =
    round(introDurationSec × fps), fade-in frames = round(padStartHumanSec × fps) and fade-out frames =
    round(padEndHumanSec × fps), where fps is the export's target fps (`targetFps`). Values are 0 when
    `EnableFades` is off or a pad is 0.
  - Notes: the fades are applied to the body BEFORE the intro concat, so in the FILE the fade-in starts
    at frame `intro`. The fade-out ends at the last frame.
  - Tests to declare success:
    - T1.2a integration (container ffmpeg harness): render a short clip through `ProcessWorker`-equivalent args (or build a minimal harness that calls the real `ProcessWorker` against a synthetic source), then read `format.tags.fvs_timing` with ffprobe and assert the values.
    - T1.2b: a clip exported with fades off → `fadein=0;fadeout=0`.
- **P1.3 Merger reads tag v2** · `[x] DONE` (2026-09-26) · Depends on: P1.1
  - `MergeClipAnalyzer` → `MergeClipInfo` gains `Fps`, `IntroFrames`, `FadeInFrames`, `FadeOutFrames` (v1 fallback).
  - Tests: T1.3a unit (parser on ffprobe JSON samples); T1.3b harness: tagged synthetic clip analysed correctly.
  - Notes (P1 as built): `Core/Media/ExportTimingTag.cs` (`ExportTiming` record struct + `ExportTimingTag.Format/TryParse/Read`).
    `IntroTag.OutputArgs(ExportTiming)` writes BOTH `fvs_intro_sec` (v1) and `fvs_timing` (v2). `ProcessWorker._exportTiming`
    is computed just before `corePath`; a fade is written as UNKNOWN (key omitted) when a meme sits at that edge.
    `MergerWorker.MergedOutputTiming()` writes intro only (fades unknown until P7.3). `MediaProber.GetExportTimingAsync()`;
    `MergeClipInfo.Timing` (init property). v1-only files parse as fps 1000/1 "millisecond frames", fades null.
    Tests: `Core.Tests/ExportTimingTagTests.cs`. Harness (container, `/tmp/sc/harness`, ffmpeg symlinked as `bin/Debug/net9.0/backend/ffmpeg.exe`):
    real `ProcessWorker` export with fades → `v=2;fps=60/1;intro=6;fadein=60;fadeout=60`; YAVG confirms the fade-in is frames 6..66
    and the fade-out is the last 60 frames. Without fades → `fadein=0;fadeout=0`. `MergeClipAnalyzer` reads both files correctly.

### P2 — Frame-exact core model — D4, D11, D13
- **P2.1 `MergeEdl` + `EdlClip` records (pure Core)** · `[x] DONE` (2026-09-26) · Depends on: P1.3
  - Stable `ClipId` (GUID) per queued clip. The same file added twice gets two ids. Structural equality (the UndoStack needs it, see UNDOEQ_01).
  - Tests: T2.1a equality/hash; T2.1b JSON round-trip (AOT source-gen `JsonSerializerContext`, no reflection).
  - Notes (as built): `src/FortniteVideoSoftware.Core/Media/MergeEdl.cs`. Types: `MergeEdl` (Version=1, Clips, ScraperEnabled,
    BaseSpeed, Thumbnail, Music; `ToJson()`/`FromJson()`; `IndexOf(Guid)`), `EdlClip` (ClipId GUID, Path, SizeBytes,
    LastWriteUtcTicks, DurationUs, InUs, OutUs(0=end), Timing(ExportTiming?), Effects), `EdlEffects` (Speed/Freezes/Memes),
    `EdlSpeedSegment(StartUs, EndUs, Speed, Zoom?)`, `EdlZoom` (OUTPUT-canvas px, D10), `EdlFreeze(AtUs, DurationSec)`,
    `EdlMeme(Id, FilePath, Placement, AtUs, DurationSec)`, `EdlMemePlacement {Mid, AtStart, AtEnd}`, `EdlAnchor(ClipId, SourceUs)`,
    `EdlThumbnail(At)`, `EdlMusic` (paths, durations, offset, Start/End anchors, volumes, loop, ducking, carving).
    Intro removal is NOT stored per clip. It is derived at timeline-build time from Timing + ScraperEnabled + Thumbnail.
    JSON: `MergeEdlJsonContext` (source-gen, string enums, nulls omitted). Tests: `Core.Tests/MergeEdlTests.cs` (6 tests).
- **P2.2 Frame snapping** · `[x] DONE` · Depends on: P2.1
  - Resolve source windows to REAL frame timestamps. Probe the pts of frame `introFrames` (for example
    `ffprobe -read_intervals %+#N -show_frames -select_streams v`) in the background analyzer. Store the
    exact start pts. Export trims at that pts; preview seeks there with `hr-seek`.
  - Tests: T2.2a harness: a synthetic 60 fps clip with a 6-frame red intro → the first kept frame is not red, and no real frame is lost (compare frame counts). T2.2b: a 59.94 fps clip. T2.2c: a VFR clip (generate with `setpts` jitter).
  - Design (FRAMESNAP_01): new `Core/Media/FramePtsProbe.cs`. `ProbeAsync(ffprobe, path, frames)` runs
    `ffprobe -select_streams v:0 -show_entries frame=best_effort_timestamp_time:format=start_time -of json -read_intervals %+#<frames+16>`
    and returns sorted pts in µs relative to `format.start_time` (ffmpeg's default input offset, so trim filters see the same clock).
    Pure helper `IntroCutUs(pts, introFrames)` = pts[introFrames] (null when too few frames).
    `MergeClipAnalyzer`: when `Timing.IntroFrames > 0`, IntroSec = snapped cut (fallback: tag seconds). Logged.
    `MergerWorker` trims: F3 → F6, and trim/atrim START minus 0.5 ms epsilon (`TrimStartEpsilonSec`) so the frame
    whose pts equals the cut is kept even after decimal rounding (at <2000 fps it can never pull in the previous frame).
    Preview seek with hr-seek belongs to P4/P8 (not changed here).
  - Notes (as built): files FramePtsProbe.cs (new), MergeClipAnalyzer.cs, MergerWorker.cs (`TrimStartEpsilonSec`, `TrimStartSec()`),
    Core.Tests/FramePtsProbeTests.cs (9 cases), docs/03 §12, INDEX, sentinels (244). Harness `/tmp/sc/p22` (clips c60, c5994, cvfr;
    120 frames, 6 red): all three → 114 frames, 0 red. Control: nominal 6/60 cut on cvfr → 113 frames (loses a real frame).
    Known limit: ffprobe reads the first F+16 packets only; enough for B-frame delay of x264/NVENC defaults.
- **P2.3 `CompositeTimeline` (merged frames ↔ clip/source ↔ output)** · `[x] DONE` · Depends on: P2.1
  - Integer merged frames at 60 fps. Per-clip `OutputTimeline` for clip effects. Global base speed. Synthetic intro offset.
  - Design (COMPOSITE_01): new `Core/Media/CompositeTimeline.cs`, built from `MergeEdl` (`Build(edl)`), pure, immutable.
    `EdlClip` gains `IntroCutUs` (snapped pts of the first kept frame from FRAMESNAP_01; 0 = fall back to `Timing.IntroSec`).
    Per clip (`CompositeClip`): keep window [KeepInUs, KeepOutUs) = user window minus the intro when removal applies
    (clip 1: only with a custom thumbnail; clips 2..N: when ScraperEnabled; refused if < 50 ms would remain);
    `MergedStartFrame`/`MergedFrames` (integer, 60 fps, per-clip rounding = what fps=60 per clip gives the export);
    `Output` = the Main App's `OutputTimeline.Create` (baseSpeed 1, origin KeepIn) fed with the clip's speed segments,
    freezes (speed-0 segments) and memes (Insertions: AtStart → 0, Mid → AtUs, AtEnd → start of the fade-out when the
    kept window reaches the file end and FadeOutFrames is known, else the clip end). Clip output lengths are summed
    (pre base speed); global output sec = SyntheticIntroSec + cumulative / BaseSpeed.
    API: `Locate(frame)→EdlAnchor?`, `ToMerged(anchor)→long?` (null = clip gone), `MergedToOutputSec`, `OutputSecToMerged`,
    `AnchorToOutputSec`, `Remap(from, frame)→long?` (through ClipId, so it survives reorder; null = stale).
  - Notes (as built): `CompositeTimeline.cs` (+ `CompositeClip` record, `ClipIndexAt`, `OutputSecToAnchor`, `KeepWindow`,
    `IntroCutUs(EdlClip)`, `UsToFrames`/`FramesToUs`), `MergeEdl.cs` (+`EdlClip.IntroCutUs`), `Core.Tests/CompositeTimelineTests.cs`
    (6 tests: T2.3a×2, T2.3b×2, T2.3c, degenerate), docs/01 §10 TL-COMPOSITE, INDEX, sentinels 246 (also added the missing
    MERGEEDL_01 INDEX/sentinel lines from P2.1).
    FINDING: each clip's OUTPUT length must be `MergedFrames/60`, not its raw µs length; raw µs drifted 44 ms over 200 clips.
    CLOSED (P9, CLIPFRAMES_01): was +1 frame on the plain chain (59.94 odd-length clip); now `trim=end_frame` per clip. Original note: the export's per-clip `fps=60:round=near` frame count must equal `MergedFrames` — verify in the harness
    (odd lengths, 59.94 sources); if it differs by one frame, trim to exactly `MergedFrames` with `trim=end_frame`.
    Today's MergerWorker applies the merge speed PER CLIP before fps=60; D-design applies BaseSpeed after concat (P7.1).
  - Tests: T2.3a property tests: `ToMerged(Locate(x)) == x`, monotonic mapping, no drift after 200 clips. T2.3b: speed segment and freeze inside clip 3 shift the output time of every later clip by exactly the expected frames. T2.3c: `Remap` across layout changes (scraper toggle, thumbnail set, reorder impossible → stale).
- **P2.4 Retire seconds math in `MergedTimeline`** · `[x] DONE` · Depends on: P2.3
  - Make `MergedTimeline` an adapter over `CompositeTimeline`. Existing `ThumbnailScraperTests` must still pass.
  - Notes (as built): `MergedTimeline.Build` now builds a `MergeEdl` (ClipId = `new Guid(index,0,…)`, IntroCutUs = TaggedIntroSec)
    and a `CompositeTimeline` (exposed as `Composite`). `MergedClip` gained optional `MergedLengthSec` (frames/60, default -1 =
    source length); `MergedEndSec` and `TotalSec` use the frame boundaries; `ToMerged` clamps to the clip's merged end.
    No App code changed (no caller constructs `MergedClip`). ThumbnailScraperTests unchanged and pass. New test T2.4a
    (odd lengths; adapter boundaries == composite frames; Locate/ToMerged inverse). docs/01 §10 bullet; sentinel added.

### P3 — Persistence and undo — D6, D7
- **P3.1 Project model** · `[x] DONE` · Depends on: P2.1
  - `ProjectMerge` gains `Edl` (the full `MergeEdl`). Bump the project schema version with a migration (old `Clips` list → EDL with no effects). Structural `Equals` updated.
  - Design (PROJ_12): `ProjectMerge.Edl` (MergeEdl?, optional). Serializer writes `merge.edl` = the EDL's own source-gen JSON as a
    JsonNode, and still writes `merge.clips` (legacy readers / diagnostics). Read: a valid `merge.edl` wins; otherwise
    `ProjectMerge.ToEdl()` migrates `Clips` (InUs/OutUs from seconds, no effects, DETERMINISTIC ClipIds `new Guid(i+1,0,…)` so
    migrating twice is equal). `HasClips` true when either holds clips. `SchemaVersion` 2 → 3: an older build would drop
    `merge.edl` (only ROOT unknown keys are preserved), so refusing is safer than silent loss of effects.
  - Notes (as built): ProjectDocument.cs (`SchemaVersion = 3`, `ProjectMerge.Edl`, `ToEdl()`, Equals includes Edl, HasClips),
    ProjectSerializer.cs (write `merge.edl` via `JsonNode.Parse(edl.ToJson())`; read via `MergeEdl.FromJson`; empty EDL → null).
    Tests in ProjectDocumentTests.cs: T31a (schema-2 migration, deterministic), T31b (every effect kind + thumbnail + music +
    timing round-trip), T31c (corrupt edl → fallback). docs/06 PROJ_12, INDEX, sentinels 249.
    USER-VISIBLE: .fvsproj files saved by this build are schema 3; OLDER builds refuse them ("Update the app").
    NOT YET WIRED: nothing in the App sets `ProjectMerge.Edl` yet (ToolNavigator still stores Clips) → P3.2.
  - Tests: T3.1a migration of an old document; T3.1b round-trip with every effect kind.
- **P3.2 Autosave and restore before MERGE** · `[x] DONE` (manual M-P3.2 passed by the user 2026-09-26) · Depends on: P3.1
  - Copy the Main App's write-behind pattern (`MainWindow.Recovery.cs`: 750 ms debounce, versioned
    `RecoveryManager` writes, off the UI thread). On Merger open with a saved EDL: restore the queue,
    effects, thumbnail, music and scraper state. Missing or changed files (size/mtime) → mark the clip
    missing, never crash.
  - Tests: T3.2a unit (serialise → restore equals); T3.2b App test: edit → simulated close → reopen → identical EDL; T3.2c: a changed file is flagged.
  - Design (MERGESESSION_01):
    * Core `Media/MergerSession.cs` (pure): `ClipIdList` keeps one GUID per queue row in step with the ObservableCollection
      events (Add/Remove/Move/Replace/Reset), because the queue is `ObservableCollection<string>` and the same file may be
      queued twice. `MergerUiState` (paths, ids, scraper, base speed, thumb path+source sec, music as `MergerMusicState` in
      merged seconds) → `MergerSession.Capture(...)` → `MergeEdl`. Carries forward from the previous EDL, per ClipId, what the
      UI does not own yet (Effects, In/Out, and analysis values while analysis is still running). Music merged seconds ↔
      anchors through the `MergedTimeline` when it matches the queue, otherwise the previous anchors are kept.
      `MergerSession.Plan(edl, fileId)` → `MergerRestorePlan` (clips whose file is gone are dropped and listed in `Missing`;
      size/mtime mismatch → kept, listed in `Changed`, cached analysis cleared; thumbnail/music anchors on dropped clips fixed).
    * Core `Infrastructure/MergerAutosaveStore.cs`: 750 ms debounce, process-wide version counter (WRITEORDER_01 style),
      `AtomicJsonFile.WriteText`, all I/O on the thread pool, `Load()` never throws, `Clear()` versioned. File:
      `ApplicationPaths.MergerSessionFile` = `<root>\merger_session.json`. An empty queue clears it. Kept after MERGE.
    * App `VideoMergerWindow.Session.cs` (new partial; `.axaml.cs` must not grow): ids tracker hooked to the queue events;
      `NoteEdlChanged()` on queue change, scraper toggle, thumbnail change, music placed, speed change, timeline rebuilt;
      publishes `ProjectMerge{Clips, Edl}` to ToolNavigator and schedules the autosave. On Loaded with an empty queue:
      restore from ToolNavigator's project EDL, else from the autosave file. Scraper restore sets the session value and
      checkbox only (does NOT rewrite the global setting).
    * T3.2b is done at Core level (store instance A writes, instance B reads = simulated close/reopen): there is no
      headless test host for `VideoMergerWindow` (it needs mpv).
  - Notes (as built): Core `MergerSession.cs` (ClipIdList, MergerUiState, MergerMusicState, MergerRestorePlan, Capture/Plan/
    MusicFromEdl), `MergerAutosaveStore.cs`, `ApplicationPaths.MergerSessionFile`. App `VideoMergerWindow.Session.cs`
    (InitializeSession/TrackClipIds/NoteEdlChanged/CaptureEdlNow/RestoreSessionAsync/ApplyRestorePlanAsync; `CurrentEdl` for P3.3).
    Hooks (axaml.cs line count unchanged at 2255): `PublishQueueToProject` → `NoteEdlChanged()`; `OnClosed` → `CaptureEdlNow(flush:true)`;
    `UpdateSpeedLabel` → also `NoteEdlChanged()`. Scraper.cs: `InitializeScraper` calls `InitializeSession()`; `ApplyTimeline`,
    `OnMusicPlaced`, thumbnail move → `NoteEdlChanged()`. ToolNavigator: `PublishMergeQueue` REMOVED, replaced by
    `PublishMergeEdl(edl)`. Stale music keeps its last saved placement. Tests: Core.Tests/MergerSessionTests.cs (5).
    Side effect: opening a schema-2 project and then the Merger adds an Edl → the project becomes dirty (expected: the
    document gained information).
  - Manual check M-P3.2 (user, Windows): queue 3 clips, set thumbnail, add music, speed 1.5x → close Merger → reopen
    → everything back. Rename one file → reopen → it is left out and named in the status line.
- **P3.3 Undo/redo** · `[x] DONE` (manual M-P3.3 passed by the user 2026-09-26) · Depends on: P3.1
  - `UndoStack<MergeEdl>` in a new partial `VideoMergerWindow.History.cs`. Gesture coalescing for drags (the U1–U4 rules). Ctrl+Z / Ctrl+Y. Covers queue reorder/add/remove, scraper toggle, thumbnail, music window and every effect.
  - Tests: T3.3a unit (the stack rules); T3.3b App test: 5 edits → undo ×5 → equals the original → redo ×5 → equals the final.
  - Design (MERGEUNDO_01):
    * History holds `MergerSession.UserEdit(edl)` = the EDL with ANALYSIS fields stripped (DurationUs, Timing, IntroCutUs,
      SizeBytes, LastWriteUtcTicks), so a finished background analysis is never an undo step (U4 no-op).
    * `MergerSession.DescribeChange(prev, next)` → (label, gestureKey): clips add/remove/reorder, scraper, thumbnail
      set/remove/move (gesture "thumb"), music add/remove/change, speed (gesture "speed"), clip effects.
    * `ClipIdList.Apply(NotifyCollectionChangedEventArgs, count)` moves the event mapping into Core (App calls it).
      `MergerSession.SyncQueue(ObservableCollection<string>, ClipIdList, targetClips)` turns the live queue into the target
      with Move/Insert/RemoveAt (keeps the preview's clip, no Clear/Reset) and `ClipIdList.SetAt` for re-inserted ids.
    * `UndoStack<T>.ReplaceCurrent(state)` (new, Core): after applying an undo/redo target, the window re-captures; tiny
      normalisations (µs rounding of music anchors) replace Current instead of pushing a phantom entry.
    * App `VideoMergerWindow.History.cs`: `_history` (UndoStack<MergeEdl>), Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z via a tunnel
      KeyDown handler (skipped while a TextBox has focus or a MERGE runs), `ApplyEdlStateAsync(target)` (scraper, speed,
      thumbnail, queue sync, music), notice "Undo: <label>". Reset to the restored state after P3.2 restore.
    * T3.3b done at Core level (UndoStack<MergeEdl> + projections + SyncQueue on a real ObservableCollection).
  - Notes (as built): MergerSession.cs (+UserEdit, DescribeChange, SyncQueue, ClipIdList.Apply/SetAt, KeepOrLocate = stable
    music anchors), UndoStack.cs (+ReplaceCurrent), NEW App/VideoMergerWindow.History.cs (_history, InitializeHistory,
    RecordHistory, ResetHistory, EndHistoryGesture, HistoryKeyDown, StepHistoryAsync, ApplyEdlStateAsync), Session.cs
    (TrackClipIds → ClipIdList.Apply; CaptureEdlNow → RecordHistory; restore → ApplyEdlStateAsync + ResetHistory),
    Timeline.cs (EndThumbMarkerDrag → EndHistoryGesture). Music is cleared BEFORE the queue sync (else queue events flag
    it stale) and re-derived from anchors after. Tests: Core.Tests/MergerUndoTests.cs (5). docs/06 item 5, INDEX, sentinels 257.
  - Manual check M-P3.3 (user, Windows): add 3 clips, reorder, toggle scraper, change speed (sweep = 1 step), set thumbnail
    → Ctrl+Z ×5 back to 3 clips in the original order → Ctrl+Y ×5 forward. The previewed clip keeps playing across reorders.

### P4 — Seamless preview spike (mpv EDL) — D2
- **P4.1 Spike** · `[x] DONE — PASS (container, mpv 0.37; Windows confirmation folded into M-P4.2)` · Depends on: P2.2
  - Build an inline `edl://path,start,length;path,start,length...` from the EDL and load it into the
    Merger's `MpvVideoView`. Paths containing `,` `;` `%` must use mpv's length-prefixed form
    `%<len>%<path>`.
  - Pass criteria (ALL): (1) no visible gap or black flash at boundaries; (2) the first shown frame of
    each clip is the first kept frame (intro never visible); (3) audio has no click or desync > 1 frame
    across 10 boundaries; (4) seeking to any merged time lands on the exact frame (`hr-seek=yes`);
    (5) works with mixed resolutions and fps in one EDL.
  - This needs the USER to run it on Windows: provide a checklist and ask for results. Record them in §11.
  - RESULT (2026-09-26, container `/tmp/sc/p4`, mpv 0.37 encode mode `--o`): EDL of L60.mp4 (h264 1920x1080 60 fps, 6-frame red
    intro), P30.mp4 (HEVC 1080x1920 30 fps, 3-frame red intro), Y5994.mp4 (h264 1280x720 59.94). Windows cut after the intros.
    (1) boundaries at exactly 2.900 / 5.800 s, no black frame; (2) zero red frames; (3) audio continuous (silencedetect: only the
    23 ms encoder priming at 0); (4) `--start=4.0 --hr-seek=yes` lands in clip 2 (1080x1920); (5) mixed codec/res/fps OK.
    Frame counts: 174 @60, 87 @30, 173 @59.94 = exact. ALL PASS. Note: ffmpeg's concat demuxer (ffconcat) FAILS on mixed
    codecs, so ffmpeg-side tools (filmstrip) must work per clip and stitch.
- **P4.2 Integrate or fall back** · `[x] DONE — integrated (manual M-P4.2 user-passed 2026-09-26)` (2026-09-26) · Depends on: P4.1
  - PASS → replace the per-file switching in `VideoMergerWindow.Timeline.cs` with one EDL load; merged time = mpv `time-pos`.
  - FAIL → keep the switching and document why in Notes.
  - Design (MERGEPREVIEW_EDL_01, agent-1 2026-09-26): NEW partial `VideoMergerWindow.EdlPreview.cs` owns playback in merged mode.
    URL = `MergeEditorSource.Build(_timeline.Composite).MpvUrl` (same clock as `_timeline`: segment length = merged frames/60).
    Loaded once per layout (URL string compared each tick); a layout change (reorder/remove/scraper/thumbnail) reloads at the SAME
    moment of the SAME file (old timeline Locate → path → new index → ToMerged). `hr-seek=yes` set on load. Position = mpv time-pos
    (grace ticks after a load while mpv still reports the old position). Selection follows Locate(pos). End → pause; Play after the
    end restarts at 0. Timeline.cs keeps its public surface (SeekMerged, LoadMergedClip → seek, StartMergedPreview) so callers do
    not change. Legacy per-file preview stays for the window BEFORE analysis lands.
  - Notes (as built): Timeline.cs merged-mode block rewritten (SeekMerged = one seek or a load; LoadMergedClip → SeekMerged;
    FollowPlayhead; TickMergedPlayback: EnsureEdlLoaded → EnsurePreviewPlan → position → TickPreviewEffects → end → UI → music).
    Removed: `_loadTargetSrc`, `ClipLoadGraceTickCount`, `LoadThenApplyPauseAsync`, the per-clip end→next-clip switch and the intro
    guard seek. `_clipLoadGraceTicks` is reused as the EDL load grace (MusicPreview reads it). StartMergedPreview (not merged) calls
    `ForgetLoadedEdl()` because the legacy path is about to load one file. NEW EdlPreview.cs: `EnsureEdlLoaded`, `LoadEdlAsync`
    (hr-seek, mute, loadfile url start, pause), `CurrentMomentOnNewLayout`, `EdlPositionSec`. Untested at runtime (no UI in the
    container): needs M-P4.2.
  - Manual M-P4.2 (user, Windows): add 3+ clips (mixed portrait/landscape if possible) → play across every boundary: no black
    flash, no intro frame, audio continuous; click a clip in the list → plays from its middle; drag the scrubber → exact frame;
    reorder / remove / toggle the scraper while playing → keeps playing on the same moment; play to the end → stops; Play again
    → restarts from 0; the list highlight follows the playhead.

### P5 — Background filmstrip and waveform — D9
- **P5.1 Progressive filmstrip** · `[x] DONE` (manual T5.1b passed 2026-09-26) · Depends on: P2.3
  - A lane under the merged timeline. Workers (bounded parallelism) extract thumbnails per clip window
    using `ThumbnailStripGenerator.StreamAsync`. Tiles paint LEFT→RIGHT as each is ready. Cache by
    (file identity, window, width). Cancel and reuse on queue changes. Never block the UI or delay playback.
  - Tests: T5.1a unit: the tile scheduler orders left→right and cancels stale requests; T5.1b manual: add 20 clips → the UI stays responsive (preview plays while tiles fill).
- **P5.2 Progressive waveform** · `[x] DONE` (manual T5.2b passed 2026-09-26) · Depends on: P5.1
  - Per-clip waveform images (existing `WaveformGenerator`), stitched in merged order with the same scheduler.
  - Tests: T5.2a scheduler reuse; T5.2b manual responsiveness.
  - Design P5.1+P5.2 (LANES_01):
    * Core `Media/ProgressiveLanes.cs`: `LaneClip` (index, path, keep window, merged span, file identity), `LaneTile`
      (clip, source window, x0/x1 px, frames, cache key), `LanePlanner.Plan(clips, totalSec, widthPx, tileWidthPx, kind)`
      → tiles LEFT→RIGHT (clips < 2 px skipped; frames = clamp(round(w / tileW), 1, 60); waveform width bucketed to
      16 px so small resizes reuse the cache). `ProgressiveLaneRunner.RunAsync(items, work, maxParallel)`: starts items
      strictly in order with bounded parallelism; a new run CANCELS the previous one; per-item failures are logged.
    * XAML: two thin lanes under the merged timeline in the preview's timeline bar (`MergerFilmstripLane` 34 px,
      `MergerWaveformLane` 18 px), not hit-testable, hidden until the merged timeline exists.
    * App `VideoMergerWindow.Lanes.cs`: `ScheduleLaneRefresh()` (called when the merged drawing key changes, 250 ms
      debounce) → plan both lanes → paint cached tiles at once → run the rest (filmstrip: `ThumbnailStripGenerator.StreamAsync`,
      keyframes only, image repainted per frame; waveform: `WaveformGenerator` PNG → Bitmap off the UI thread, temp PNG
      deleted; clips without audio skipped). Max 2 parallel per lane, no playback yield (D9: fill WHILE playing).
      In-memory cache (LRU, 96 per lane) keyed by file identity + window + frames/width. Cancelled on close.
  - Notes (as built): Core ProgressiveLanes.cs (LaneClip, LaneTile, LanePlanner, ProgressiveLaneRunner — work is invoked
    directly so the synchronous start is in list order; a slot granted as the run is superseded is re-checked — LaneCache<T>).
    App: VideoMergerWindow.axaml (grid rows 2+3), VideoMergerWindow.Lanes.cs, Timeline.cs (DrawMergedTimeline → ScheduleLaneRefresh),
    Scraper.cs (ApplyTimeline → ScheduleLaneRefresh), Session.cs (InitializeLanes). Controls.cs gained cached accessors
    ScraperCheckBoxCtl / MarkersCanvasCtl / FilmLaneCtl / WaveLaneCtl (MVVM_01 lookup ceiling 700; now 687).
    Evicted cache bitmaps are NOT disposed (they may still be on screen; GC reclaims). Tests: ProgressiveLanesTests.cs (6,
    stress-run 15x clean). docs/04 §9, INDEX, sentinels 260.
  - Manual T5.1b/T5.2b (user): add ~20 clips → preview keeps playing while the filmstrip fills left→right and the waveform
    follows; reorder a clip → lanes repaint instantly from cache; resize the window → lanes replan. Judge the look (height).

- **P5.3 Selection sync + marching ants + drag-reorder in BOTH lists** · `[x] DONE` (manual T5.3c passed 2026-09-26) · Depends on: P4.2 — D14 (dependency relaxed: works on today's per-clip preview; P4.2 does not change it)
  - Selecting a clip in the right-hand list OR on the timeline highlights it in BOTH places with the yellow, gently
    animated marching-ants border (the list already has `Rectangle.MarchingAnts`; the timeline block needs the same).
  - Timeline clip blocks can be dragged horizontally to reorder. Dragging in either list reorders the other live
    (one source of truth: `VideoQueue`/`MergeEdl` order).
  - Tests: T5.3a App test: select in list → timeline selection index matches, and vice versa; T5.3b App test: a
    reorder via the timeline drag handler updates `VideoQueue` and the list; T5.3c manual checklist (animation visible).
  - Design (ANTS_01):
    * Every SELECTED clip's block on the merged timeline gets a yellow (AppWarningBrush) dashed border animated by the
      same StrokeDashOffset 0→6 / 0.6 s loop as the list (`Rectangle.TimelineAnts` style in VideoMergerWindow.axaml).
      The drawing key includes the selected indices; a list SelectionChanged handler (new partial) forces a redraw.
    * The clip CHIP (number · name label at the top of each block) becomes the timeline's handle: click = select that
      clip in the list (so AUTOPREVIEW plays it, exactly like a list click); drag horizontally (> 6 px) = reorder, with a
      yellow insertion bar; release = `VideoQueue.Move(from, to)`. Clicking anywhere else on the timeline still SEEKS.
      One source of truth (`VideoQueue`): the list and the timeline both redraw from it, ids follow (ClipIdList), undo
      records "reorder clips". Redraws are suppressed while a chip is captured (THUMB_02 lesson).
    * Pure Core `TimelineReorder.TargetIndex(blocks, from, x)` (insertion index by block midpoints) — unit-tested
      (T5.3b at Core level; no headless host for the window).
  - Notes (as built): Core TimelineReorder.cs (TargetIndex, InsertionX) + TimelineReorderTests.cs (8). App: VideoMergerWindow.axaml
    (`Rectangle.TimelineAnts` style), NEW VideoMergerWindow.TimelineSelect.cs (InitializeTimelineSelection, SelectionKey,
    DrawTimelineSelection, AttachClipChip, ShowInsertBar, EndClipChip, SelectQueueRow), Timeline.cs (draw key + chip + ants,
    no redraw while `_draggingClipChip`), Session.cs (init). docs/04 §9, INDEX, sentinels 264.
  - Manual T5.3c (user): select a clip in the list → its timeline block shows moving yellow ants; click a chip on the timeline
    → that row is selected + previewed; drag a chip sideways → yellow bar, drop → list order changes; drag in the list →
    timeline order changes; Ctrl+Z undoes either reorder; clicking the timeline outside a chip still seeks.

### P6 — Granular editing in the Merger — D3, D4, D5, D10
- **P6.1 Decide the editor host** · `[x] DONE — user chose WHOLE MERGE (D16), memes by position (D17), cuts allowed (D18)` · Depends on: P2.3
  - Investigate `GranularSpeedEditorWindow`: what does it assume about ONE source file (duration, mpv
    load, filmstrip, memes, voice-over)? Preferred: a small `IEditorSource` abstraction (single file for
    the Main App, EDL for the Merger) injected into the SAME window. Clip boundaries show as dividers,
    and effect creation is clamped to the clip under the cursor (D4). The window's code-behind ceiling
    (8075) must NOT grow: new behaviour goes in partial files.
  - Deliverable: a design note here in Notes, and ASK THE USER if any UX choice is unclear.
  - Investigation (2026-09-26, facts): `GranularSpeedEditorWindow.axaml.cs` = 8068 lines (ceiling). Opened via
    `CreateAsync(videoPath, trimStartMs, trimEndMs, segments, baseSpeed, freezeTimeMs, freezeDurationS, isMobileFormat,
    originalResolution, voiceOver, cuts, memes)` + `AvailableMemes`; results read from `Accepted`, `ResultSegments` (abs ms),
    `ResultMemes` (clip-relative s), `ResultCuts` (abs ms), `ResultBaseSpeed`, `ResultFreezeTimeMs` (abs), `ResultFreezeDurationS`.
    ONE source everywhere: `_videoPath`, `_trimStartMs/_trimEndMs`, mpv `LoadFileAsync(_videoPath)`, filmstrip, MemePreviewDirector,
    voice-over, recovery + parked history keyed by path. Zoom lives INSIDE SpeedSegment (source px + ZoomOrigRes). ONE freeze
    only (scalars). Cuts supported. No source abstraction, no clip boundaries. Caller example: MainWindow.Wireup.cs:449-525.
  - Design proposal: PER-CLIP HOST. The Merger opens the SAME editor on ONE clip (the selected one) with that clip's kept
    window as the trim, its EdlEffects converted in, and converts the results back into that ClipId's EdlEffects on Accept
    (one undo step "edit clip"). D4 (effects stay inside one clip) is then true by construction; the editor file does not
    change. Mappings: SpeedSegment ↔ EdlSpeedSegment (+zoom: editor source px → EdlZoom via the clip's canvas fit, P6.3);
    freeze scalars ↔ EdlFreeze[0] (further freezes preserved); MemePlacement ↔ EdlMeme; cuts need `EdlEffects.Cuts` (new).
    The editor's own base-speed wheel is hidden/ignored for merger clips (BaseSpeed is global, EDL.BaseSpeed).
    Rejected: one editor over the whole merge (rewrites most of the 8068-line window; breaks the ceiling rule).
  - Open UX questions (asked 2026-09-26): Q1 host per clip vs whole merge; Q2 meme AtStart/AtEnd by position vs explicit;
    Q3 allow DELETE PARTS (cuts) inside a merger clip.
- **P6.2 Core: base speed semantics (D19) + cuts + source/effects mappers** · `[x] DONE` · Depends on: P6.1
  - `CompositeTimeline`: per-clip `OutputTimeline.Create(..., baseSpeed: edl.BaseSpeed, cuts)`; no global division.
    `EdlEffects.Cuts` (source µs ranges). Update P2.3 tests to the D19 numbers.
  - `MergeEditorSource` (Core): from a CompositeTimeline → mpv inline EDL URL whose segment LENGTHS are `MergedFrames/60` (so
    mpv time == merged clock), clip boundaries in merged ms, per-clip info (path, keep window, fades, resolution).
  - `MergeEffectsMapper` (Core): EDL effects → editor inputs in MERGED ms (segments, first freeze, cuts, memes) and editor
    results → per-clip `EdlEffects`: segments/cuts crossing a boundary are SPLIT per clip; a freeze/meme belongs to the clip
    under it; meme placement by D17; zoom kept in SOURCE px (see P6.3). One freeze for the whole merge in v1 (editor limit).
  - Tests: round trip EDL → editor → EDL; boundary splitting; D17 placements for all fade combinations; D19 timing.
  - Notes (as built): CompositeTimeline.cs (base speed per clip via `OutputTimeline.Create(..., baseSpeed, ..., cuts)`, no global
    division; T2.3b updated to D19: 29.5 s), MergeEdl.cs (`EdlCut`, `EdlEffects.Cuts`, `EdlZoom.SourceW/SourceH` = zoom kept in the
    clip's SOURCE px), NEW MergeEditorBridge.cs (`MergeEditorClip`, `MergeEditorState`, `MergeEditorSource` with Build/MpvUrl/
    ClipAt/ToEditor/FromEditor/PlacementAt/Split; MERGEEDIT_01), NEW Core.Tests/MergeEditorBridgeTests.cs (13 cases; one
    expectation corrected: a 4 s span at 0.5x adds 4 s, not 2). One mapper class instead of the planned two (source + mapper
    share the clip spans). docs/01 §10, INDEX, sentinels 265. `ToEditor` must be called before `FromEditor` (ExtraFreezes).
- **P6.3 Editor seams (App)** · `[x] DONE (manual M-P6 user-passed 2026-09-26)` · Depends on: P6.2
  - New partial `GranularSpeedEditorWindow.Merge.cs`: `MergeSource` (null = Main App, unchanged). In-place edits only in
    `.axaml.cs` (line count must not grow): mpv load path, the File.Exists guard, filmstrip (merge → stitched per-clip strip),
    meme preview source path, recovery + parked history disabled in merge mode (the Merger autosaves). Clip dividers drawn on
    a new overlay canvas in the editor XAML. Zoom: EdlZoom stores SOURCE px of its clip + that clip's size (robust to the user
    later switching the output canvas); P7.1 transforms source → canvas (scale/pad or scale/crop). Tests: transform unit tests.
- **P6.4 Merger entry point** · `[x] DONE (manual M-P6 user-passed 2026-09-26)` · Depends on: P6.3
  - A GRANULAR EDIT button in the Merger (new partial). Opens the editor with the merge; on Accept the per-clip effects replace
    the EDL's (one undo step "edit effects"), autosave follows. Disabled until the merged timeline is ready.
  - Manual M-P6: speed ramp across a boundary, freeze, cut, memes at start/mid/end of clips, zoom on a portrait clip.
  - Design P6.3+P6.4 (MERGEEDIT_02), from the investigation:
    * The editor is opened with `videoPath = MergeEditorSource.MpvUrl` (`edl://…`), trim 0..TotalMs, and the bridge's merged-ms
      state. trimEnd > 0 → CreateAsync does not probe. mpv `LoadFileAsync` passes the URL through (no File checks). The meme
      preview director reloads `_videoPath` → works with the URL. `_originalResolution` already follows mpv's LIVE video size
      every tick, so a zoom drawn while clip i is on screen records clip i's size in `ZoomOrigRes` (= EdlZoom.SourceW/H).
    * `IsMergeMode` = `_videoPath` starts with `edl://` (known in the constructor, before recovery runs).
    * Seams in `GranularSpeedEditorWindow.axaml.cs`, line count UNCHANGED (8068): (1) `TryRehydrateGranularRecovery`
      ignores sessions in merge mode; (2) `ScheduleGranularRecoverySave` returns in merge mode (the Merger autosaves the EDL);
      (3) `BuildFrameLaneAsync`: the File.Exists guard line hands merge mode to `BuildMergeFrameLaneAsync`.
      `GranularSpeedEditorWindow.History.cs`: `HistoryKey` empty in merge mode (no parked history keyed by a URL).
    * NEW partial `GranularSpeedEditorWindow.Merge.cs`: `MergeSource` property (set before ShowDialog; hooks Opened);
      merge filmstrip = per-clip `StreamAsync` strips (frames ∝ clip length, total clamp(TotalSec/2, 15, 90)) blitted into ONE
      composite WriteableBitmap mounted at once and repainted as frames land; clip dividers on an overlay canvas in LaneAHost,
      redrawn (coalesced) whenever the segment canvas rebuilds (its Children change) or resizes, at `SrcMsToX(clip.StartMs)`
      (output-time positions, so they follow speed edits).
    * P6.4 NEW partial `VideoMergerWindow.Effects.cs` + a GRANULAR EDIT button under ADD MUSIC: waits for the merged timeline,
      pauses the preview, scans memes (MemeManagementService), builds the bridge, opens the editor; on Accept → `FromEditor`,
      replaces `_lastEdl`, applies the base speed to the wheel, captures with the undo label "granular edit" (one step).
  - Notes (as built): editor .axaml.cs still 8068 lines (seams at TryRehydrateGranularRecovery, ScheduleGranularRecoverySave,
    BuildFrameLaneAsync); .History.cs HistoryKey; NEW GranularSpeedEditorWindow.Merge.cs (IsMergeMode, MergeSource, dividers,
    BuildMergeFrameLaneAsync, MountMergeLane, BlitStrip). Merger: .axaml button `MergerGranularButton`, NEW VideoMergerWindow.Effects.cs
    (InitializeMergerGranular, UpdateMergerGranularButton, OpenMergeGranularEditorAsync), .History.cs `_nextHistoryLabel`,
    .Session.cs init, .Scraper.cs ApplyTimeline → UpdateMergerGranularButton. Integration check (container): the bridge URL for
    c60/c5994/cvfr played by mpv 0.37 = one 5.79 s file; red (intro) frames only 0..5 = clip 1's intro, kept by rule.
    ⚠ UNTIL P7/P8: effects are saved in the EDL (autosave, project, undo) but the Merger's own preview and MERGE export do not
    apply them yet (P7.1 export graph, P8 preview parity).
  - Manual M-P6 (user): open GRANULAR EDIT on 3 clips → merge plays as one file with numbered dividers and a film lane; add a speed
    ramp across a divider, a freeze, a cut, memes at a clip start / middle / end, a zoom on a portrait clip → ACCEPT → reopen:
    everything is back where it was; Ctrl+Z in the Merger removes the whole granular edit, Ctrl+Y brings it back.

### P5b — Clip actions on both views (user request 2026-09-26, D20)
- **P5.4 Delete + context menu + instant reorder + clip blocks** · `[x] DONE (manual M-P5.4 user-passed 2026-09-26)` · Depends on: P5.3
  - D20 (user request, agent design approved by "your opinion" ask): select ONE (or more) clip in the list OR on the timeline →
    Delete key or right-click → "Remove from list" removes it (confirm dialog only if the existing setting
    `ConfirmVideoMergerRemove` is on; Ctrl+Z brings it back). The list already had a right-click flyout; the timeline gets the
    same actions (Move earlier / Move later / Remove) as a context menu on the clip chip and on the clip BLOCK.
  - Clip blocks (agent opinion, adopted): the FILMSTRIP lane is the clip strip — each clip is a rectangular block over its
    thumbnails (thin outline, yellow marching ants when selected), click = select, drag sideways = reorder with the insertion
    bar, right-click = menu. The scrubber above stays for seeking only (the chip remains a small handle there too).
  - Instant, non-freezing sync: a queue change rebuilds the merged timeline SYNCHRONOUSLY from the analysis cache when every
    clip is already analysed (pure math, microseconds — no I/O), then repaints the timeline and the lanes from the tile cache at
    once; anything not cached (new clips) still runs in the background workers (analysis, filmstrip, waveform).
  - BUG FOUND + FIX: list reorders (drag-drop, Move up/down, Up/Down keys) used `RemoveAt` + `Insert`, which the ClipIdList sees
    as delete + add → the moved clip got a NEW id and lost its granular effects (and undo said "change clips"). They now use
    `VideoQueue.Move`, which keeps the id.
  - Files: VideoMergerWindow.axaml.cs (3 reorder sites → Move; line count goes DOWN), VideoMergerWindow.axaml (blocks canvas),
    NEW VideoMergerWindow.ClipActions.cs, VideoMergerWindow.TimelineSelect.cs, VideoMergerWindow.Session.cs, docs/04, INDEX, sentinels.
  - Notes (as built): tag CLIPACTIONS_01. axaml.cs 2255 → 2246 lines (ceiling lowered in ArchitectureRuleTests). Controls.cs
    `BlocksLaneCtl`. TimelineSelect.cs: `DrawClipBlocks` (called at the end of DrawTimelineSelection), right-click branch in
    `AttachClipChip` (chips AND blocks). ClipActions.cs: `ClipActionsKeyDown` (Delete), `RequestRemoveSelectedAsync`,
    `RemoveQueueRows` (by index), `ShowClipContextMenu`, `RebuildTimelineFromCacheNow` (called from Session.cs TrackClipIds).
    Regression test MergerUndoTests.D20_ReorderMustUseMove_RemoveInsertLosesTheClipId. The old Edit ▸ Remove Selected menu
    still removes by value (unchanged code path; duplicates of one file remove the first copy) — left as is.
  - Manual M-P5.4 (user): select a clip → Delete removes it (Ctrl+Z restores, effects intact); right-click a clip block / chip →
    menu works; drag a block in the filmstrip lane → list follows; drag in the list → timeline + lanes follow instantly with no
    freeze; move a clip that has granular effects → effects stay on it (bug fix).

### P7 — Export from the EDL
- **P7.1 Per-clip graph builder** · `[x] DONE (manual M-P7 user-passed 2026-09-26)` · Depends on: P6.2, P6.3, P6.4, P2.2
  - New pure builder (`Core/Media/MergeGraphBuilder.cs`) that emits the FFmpeg filter graph string from
    `MergeEdl`, reusing `GranularSpeedBuilder` per clip. `MergerWorker` calls it. Keep the existing
    encoder, fallback, two-pass and GPU/CPU route logic untouched.
  - Design (MERGEGRAPH_01): NEW Core `MergeClipGraph.Build(...)` for ONE clip WITH effects (clips without effects keep today's
    chain byte-for-byte): `[i:v]trim(keep window, FRAMESNAP epsilon),setpts` + `[i:a]atrim,aformat` → the Main App's
    `GranularSpeedBuilder.Build` (speed segments with ABSOLUTE speed, freezes as speed-0 segments, zoom in SOURCE px before any
    scaling, cuts clip-relative, base speed outside segments = D19, needHudBranch=false) → every internal label of that graph is
    PREFIXED `c{i}_` (Build uses fixed names; many clips share one graph) → canvas chain (colour + scale/pad|crop + setsar) + fps=60
    → meme splice: body output cut at each meme's output time (Build's own time mapper; AtStart = 0, AtEnd = fade-out start, Mid =
    AtUs; D4), pieces via split/asplit + trim/atrim, memes normalised (fit-pad to the canvas, fps 60, duration D, silence when no
    audio), `concat` → `[v{i}][a{i}]`. `MergerWorker.Edl` (set by the App) supplies effects; meme files become extra inputs after
    the thumbnail input (images `-loop 1 -framerate 60 -t D`). `outputDuration` = `CompositeTimeline.ClipsOutputSec` when the EDL
    matches the queue (so music/progress/bitrate use the real length). Meme loudness matching (Main App) is NOT done in v1.
  - Tests: T7.1a golden filter-graph snapshots; T7.1b harness (container ffmpeg, as done for the Scraper in `/tmp/sc`): 3 tagged clips with a speed ramp in clip 2, a freeze in clip 3 and memes at start/mid/end → output duration equals the `CompositeTimeline` prediction to ±1 frame, no intro frames present (red-frame detector), audio length = video length ±20 ms.
- **P7.2 Music by output time** · `[x] DONE` · Depends on: P7.1, P2.3 — D8
  - The music start/end are anchored to (ClipId, source frame) and converted to output time by the composite mapper, in preview AND export. The music stays at 1.0x.
  - Tests: T7.2a unit: a speed-up before the anchor moves the music earlier in output time by the exact amount; T7.2b harness: a sine-tone music track → onset detected at the predicted output time ±1 frame.
- **P7.3 Output tag** · `[x] DONE` · Depends on: P1.1, P7.1
  - The merged file carries `fvs_timing` (the intro of the merged output; fades = clip 1's fade-in and the last clip's fade-out when kept).
  - Notes P7 (as built): NEW Core MergeClipGraph.cs (MERGEGRAPH_01); MergerWorker.cs (`Edl`, `EdlMatchesInputs`, meme inputs,
    effects branch in the per-clip loop, outputDuration from CompositeTimeline, OUTTAG_01 `ResolveOutputFades` + tag fades);
    CompositeTimeline.cs (`MemeAtRelSec` shared with the export, `MergedSecToBodyOutputSec` = MUSICMAP_01); App Scraper.cs
    (`worker.Edl = CurrentEdl` after a fresh capture), axaml.cs (2 in-place lines: music start/end via `MusicExportSec`),
    Effects.cs (`MusicExportSec`). Tests: MergeClipGraphTests (4), CompositeTimelineTests.T72a. Harness `/tmp/sc/p7` +
    `/tmp/sc/harness` (run the apphost `./harness`, not `dotnet harness.dll`, so `backend/ffmpeg.exe` resolves):
    3 clips → 15.800 s = prediction; 4 clips → 19.717 vs 19.700 (+1 frame, within spec; an experiment moving the granular
    origin to the epsilon instant made it +4 and was reverted); music onset 8.0 s = prediction; tag fades 30/60.
    KNOWN LIMITS: meme loudness is not matched (Main App measures it) (FIXED by MEMELEVEL_01, P9); the output-size estimate still ignores effects (FIXED by MERGESIZE_01, P9); the
    Merger's own preview does not play effects yet (P8).
  - Manual M-P7 (user): merge 3 clips with a speed ramp, a freeze, a cut, memes at start/mid/end and music → the file plays the
    effects exactly where the Granular editor showed them, the music starts on the chosen video moment, no intro flashes.

### P8 — Preview parity for effects
- **P8.1 Speed/freeze in preview** · `[x] DONE (manual M-P8.1 user-passed 2026-09-26)` (2026-09-26) · Depends on: P4.2, P6.2
  - Notes: NEW Core `Media/MergerPreviewPlan.cs` (MERGEPREVIEW_01): `PreviewStep` list (Play/Cut/Freeze/Meme) read from every clip's
    `OutputTimeline.Chunks` (the export's own chunks) mapped to merged seconds; `SpeedAt`, `CutResumeAt`, `NextHold(from,to,afterIndex)`,
    `LastStepIndexBefore`, `OutputSecAt` (= `CompositeTimeline.MergedSecToBodyOutputSec`). Tests `Core.Tests/MergerPreviewPlanTests.cs` (3):
    T8.1a total + every 7th frame output second equal the composite export clock; schedule positions; no-effects case.
    App plan: tick applies mpv `speed` = SpeedAt (re-asserted after any EDL change), seeks past cuts, pauses for freezes (wall clock,
    FREEZE_01-style consume-once + re-arm on seek), music preview clock = OutputSecAt + hold elapsed (so music keeps running
    through a freeze exactly like the file).
  - As built: EdlPreview.cs `EnsurePreviewPlan` (plan only when `_lastEdl` paths == timeline paths AND total merged frames agree),
    `RearmPreviewEffects`, `TickPreviewEffects` (hold → pause + seek back to the held frame; transport pressed during a hold =
    pause; cut → SeekInternal(resume); speed re-assert), `PreviewOutputSec`. MusicPreview.cs: `UpdateMergerMusicPreview(outputSec,…)`,
    music start/bed via `_plan.OutputSecAt` (fallback ÷ base speed). Session.cs `NoteEdlChanged` resets `_appliedSpeed`.
    Memes are HELD AS A FREEZE of their length until P8.2. docs/01 §10, INDEX, sentinels 290.
  - Manual M-P8.1 (user): GRANULAR EDIT → a 0.5x ramp, a freeze and a cut in different clips → ACCEPT → play in the Merger: slow
    part slow, freeze holds for its length while the music keeps going, the cut is jumped over; seek back before the freeze →
    it holds again; change the speed wheel → footage outside the ramp follows it, the ramp keeps its own speed.
  - Use the Main App's preview approach (mpv `speed` per segment, timed freeze holds) driven by the composite timeline.
  - Tests: T8.1a unit: the preview schedule equals the export timing; T8.1b manual checklist for the user.
- **P8.2 Memes in preview** · `[x] DONE (manual M-P8.2 user-passed 2026-09-26)` (2026-09-26) · Depends on: P8.1, P6.4
  - Reuse `MemePreviewDirector`. Tests: manual checklist.
  - As built: Core `MergerPreviewPlan.MemePlacements` (merged clock, origin 0) + `NextHold(..., includeMemes)`; test extended.
    EdlPreview.cs `TickMergerMemes()` creates the director lazily (client, `() => _edlUrl`, `() => 0.0`, `MemeSwapOverlay.Set`,
    "MERGER"); MemeStarted stops the music preview, MemeEnded re-asserts the speed schedule; Suspended while loading / holding /
    dragging. Timeline.cs: the tick calls it right after EnsurePreviewPlan and RETURNS while active (MEME_07 rule; no EDL reload under
    a meme); SeekMerged ignores seeks while active. VideoMergerWindow.axaml: `MemeSwapOverlay` veil (copy of the Main App's).
    RearmPreviewEffects → `NotifySeek()`. Memes no longer freeze-held by P8.1 logic.
  - Manual M-P8.2 (user): memes at a clip start / middle / end → play across them: the veil shows, the meme plays with its sound,
    the merge resumes on the same frame; the music stops during the meme and comes back in step; scrubbing across a meme never
    fires it.

### P9 — Docs, sentinels, final verification
- **P9.1 Spec sections** · `[x] DONE` (2026-09-26 23:45) · Depends on: all above
  - Add a new `docs/0x` section "Merger EDL", plus INDEX entries and sentinels for every tag ID used (suggested IDs: `MERGEEDL_01..`, `TIMINGTAG_02`, `MERGEPERSIST_01`, `MERGEUNDO_01`, `MERGEPREVIEW_EDL_01`, `MERGESTRIP_01`, `MERGEGRANULAR_01..04`, `MERGEGRAPH_01`).
  - As built (IDs actually used, mapped to the suggested ones): MERGEEDL_01 + EDLNULL_01 + COMPOSITE_01 (docs/01 §10
    TL-COMPOSITE); TIMINGTAG_02 + FRAMESNAP_01 + CLIPFRAMES_01 + MERGEGRAPH_01 + MEMELEVEL_01 + OUTTAG_01 + MUSICMAP_01 (docs/03 §12);
    MERGEPERSIST → MERGESESSION_01 + RESTOREMISS_01 (docs/05 §4); MERGEUNDO_01 (docs/06 item 5, docs/07 cross-ref); MERGEPREVIEW_EDL_01 +
    MERGEPREVIEW_01 (docs/01 §10); MERGESTRIP → LANES_01 (docs/04 §9); MERGEGRANULAR → MERGEEDIT_01/02 (docs/01 §10, docs/04 §9);
    D20 → CLIPACTIONS_01 + ANTS_01 (docs/04 §9); MERGESIZE_01 (docs/03 FFM-SIZEESTIMATE); TOOLRETURN_01 (docs/05 §3).
    Audit (scripted): every tag added to build/sentinels.txt since the baseline has an INDEX row and a docs mention; every tag
    first introduced in code during the migration has a sentinel and an INDEX row (0 gaps). Pre-existing gaps closed on the way:
    UNDO_20 (docs/07 §2), SCRAPER_04 (docs/03 §12). INDEX §1 (file → spec) gained all 21 new source files; its stale "122 files
    absent" count now says "many files".
- **P9.2 Full verification** · `[x] DONE (agent side)` (2026-09-26 23:55) — user `Build.cmd --no-publish` run requested
  - All §4 gates, all harness tests, and a user-run `Build.cmd --no-publish` on Windows, plus the user's manual checklist (preview, speed, freeze, zoom on mixed orientations, memes, music timing, save/restore, undo/redo, filmstrip progressive fill). Record the results in §11.
  - Results (2026-09-26 23:55, commit after P9.1): Debug build 0 errors / 0 warnings · Release TreatWarningsAsErrors 0/0 ·
    NativeAOT trim/AOT analysis (aot.sh, TWAE) 0 lines · Core tests 358 pass / 5 skipped · App tests 80/81 — the 1 failure is
    `OutputSizeProbeTests.RealMediaProbe…` = "The repository's media binaries are required" (Windows ffprobe.exe not in the container;
    env-only, unchanged since baseline) · FvsVerify 301 fix sentinels present · harness `fps 1.0` plain 380/380 frames + fx 410/410,
    `fps 1.5` 253/253 + 293/293, `p92` 16.500 s = prediction (990 frames, audio = video, memes loudness-matched).
    User manual checklist §9: ALL passed (2026-09-26 22:34).
  - Remaining for the user: run `Build.cmd --no-publish` on Windows once (full signed build with the new files) and report.
  - Optional follow-ups (NOT required by any D-decision, logged so nobody rediscovers them): (a) delete the `MergedTimeline`
    seconds adapter once every Merger caller reads `Composite` (docs/01 §10 says so); (b) R9 `runtime.manifest.json` upload (ask the
    user); (c) the Granular editor shows ONE freeze for the whole merge (extra freezes are kept, `ExtraFreezes`) — multi-freeze UI would
    be an editor feature, not a migration item.

---

### P10 — Merger timeline UX round (user report 2026-09-27 09:51, dev.cmd)
User items: (1) drag & drop reorder does not work in the right list NOR on the thumbnail blocks — both must work; playhead
behaviour on reorder to be defined; (2) SET THUMBNAIL must be IDENTICAL to the Main App (icon, style, size, position,
behaviour); (3) GRANULAR SPEED EDITOR button in the same position as the Main App; (4) huge vertical gap between the timeline
and the thumbnails — reduce without hitbox collisions; (5) clip file-name labels on the timeline centred, not left;
(6) the timeline x-axis lacks the clear grid + time labels of the Main App / Add Music timelines.
Decisions (AskUserQuestion 2026-09-27 09:55):
- **D22** After a reorder the playhead FOLLOWS THE CLIP: same frame of the same clip, wherever it moved.
- **D23** Thumbnail blocks: plain click = SELECT ONLY (never seeks). Seek/scrub ONLY on the upper timeline. Hover = slight
  glow + open-hand cursor; pressing/dragging = closed (grab) hand.
- **D24** Drag starts on press + ~6 px move (list AND blocks). No long-press.
- **D25** (item 6) Timeline x-axis: grid lines + time labels like the Main App / Add Music timelines.
- Items 2–5 are parity/layout requests (no choice needed): copy the Main App exactly.
Root causes found for (1) (code reading + dev log 09:39–09:51: no "Timeline drag" and no "move clips" undo entry = no
reorder ever committed):
- R-a LIST: `VideoList_PointerPressed` is attached with `+=` (ignores handled events). The ListBoxItem handles the left press
  for selection, so `_videoDragStartPoint` is never set and the OLE drag never starts. → register with `handledEventsToo`.
- R-b BOTH: P4.2's `FollowPlayhead` calls `SyncListSelection` EVERY tick, and right after a seek mpv still reports the OLD
  position for a tick → the selection is yanked back to the old row (→ another preview seek, a timeline redraw that
  destroys the pressed block and drops its pointer capture). → sync only when the playhead CROSSES into another clip, never
  while a press/drag is in progress, and hold the seek target during a short seek grace.
- Also seen in that log: "Preview effects wait: the edit list and the analysed timeline differ in length" right after adding
  clips (the capture ran before the analysis) → recapture when the analysed timeline lands.
Tasks:
- **P10.1 Reorder fixes (R-a, R-b) + D22/D23/D24 + block hover/grab cursor + seek only on the upper timeline** · `[x] DONE (manual M-P10 pending)`
- **P10.2 SET THUMBNAIL parity** · `[x] DONE (manual M-P10 pending)`
- **P10.3 GRANULAR button position parity** · `[x] DONE (manual M-P10 pending)`
- **P10.4 Vertical gap timeline ↔ thumbnails** · `[x] DONE (manual M-P10 pending)`
- **P10.5 Centred clip labels (labels become non-interactive: the upper timeline only seeks, D23)** · `[x] DONE (manual M-P10 pending)`
- **P10.6 Grid + time labels on the x-axis (D25)** · `[x] DONE (manual M-P10 pending)`
- **P10.7 Recapture the EDL when the analysis lands** · `[x] DONE` — finding: ApplyTimeline ALREADY posts NoteEdlChanged; the warning was
  the one tick before the posted capture runs. Now only a mismatch persisting ~2 s (20 ticks) is logged, and the plan re-evaluates every tick meanwhile.
- As built (P10): TimelineSelect.cs (MERGERUX_01 header, QueueGestureActive, IsOnSeekRows, block glow/cursors/ghost-follow,
  SelectQueueRow(i, preview) + _selectWithoutPreview), Timeline.cs (SeekGraceTicks, FollowPlayhead only on crossing, labels centred
  + non-interactive, DrawTimelineGrid, DrawTimelineScale end labels, StartMergedPreview honours _selectWithoutPreview), EdlPreview.cs
  (mismatch counter), Session.cs (Move → no preview), axaml.cs (list AddHandler handledEventsToo; overlay press only on seek rows;
  line count unchanged 2246), VideoMergerWindow.axaml (row 0 = SET THUMBNAIL · transport · output path + GRANULAR SPEED; row 1 =
  TOTAL SIZE/LENGTH · OUTPUT QUALITY · MERGE · SPEED; lanes Margin -8; granular button removed from the rail), Effects.cs (Main App
  button states), NEW Infrastructure/GrabCursors.cs (GRABCURSOR_01). docs/04 §9, INDEX, sentinels 308. Gates: b.sh 0/0, aot 0,
  Core 361, App 80/81 (env-only). NOT runtime-tested here (UI).
- Manual M-P10 (user, dev.cmd): (1) drag a row in the right list → order changes, playhead stays on its clip; (2) hover a
  thumbnail block → glow + open hand; press → closed hand; drag → block follows, gold bar, drop reorders, playhead stays;
  plain click on a block → selected, playhead does NOT move; (3) click/drag on the upper timeline seeks; clicks on the thumbnails
  never seek; (4) SET THUMBNAIL looks/sits like the Main App (left), GRANULAR SPEED on the right; (5) gap between scrubber and
  thumbnails small, knob still grabbable; (6) labels centred; grid lines + 0:00 / total labels on the axis.

---

### P11 — Merger round 2 (user report 2026-09-27 10:11, dev.cmd)
User items: (1) resizing the window (and reordering) rebuilds the filmstrip thumbnails every time, slowly — research how top
apps do it; A: faster generation (quality can be low), B: no regeneration on resize or reorder; (2) OUTPUT QUALITY below 100%
produces a LARGER file than 100% while TOTAL SIZE predicts smaller; (3) Delete-key confirm shows "YES, REMOVE" in green (must be
red — destructive); after a removal show a clear notice how to undo; the confirm must be OFF by default (also for existing
installs), with a Settings switch for users who want it; (4) removing the last clip returns the NO VIDEO LOADED screen but a
ghost of the last frame stays in the player.
Root causes / findings:
- (1) `LanePlanner` puts the FRAME COUNT (derived from the on-screen width) and the waveform WIDTH in the cache key, so every
  width change is a cache miss and a new ffmpeg run per clip; the waveform is a PNG rendered at the lane width. Also the lane
  width changes whenever the preview/overlay re-lays out (dev log 09:40–09:51: a refresh per click). Research: editors keep a
  per-CLIP thumbnail cache independent of zoom/width and only re-pick frames on redraw (Kdenlive keeps per-project
  `videothumbs`/`audiothumbs` caches); ffmpeg is fastest with keyframe-only decoding / input seeking and tiny output frames.
  → LANECACHE_02: per clip ONE fixed frame grid (source-time step, ≤ 90 frames) generated once, cached in memory AND on disk
  (PNG, keyed by file identity + kept window), drawn by picking the nearest cached frame for every on-screen slot (CroppedBitmap,
  no pixel work, no ffmpeg on resize/reorder); waveform = PEAKS extracted once (fixed rate, cached in memory + disk) drawn as a
  vector geometry stretched to any width.
- (2) Below 100% the export used pure constant quality (CQ 16–35) with NO bitrate ceiling: high-motion gameplay at CQ 16–17
  easily exceeds the source bitrate the 100% path targets. → MERGEQUALITY_01: below 100% the export targets
  (100% bitrate × ratio(p)) in VBR with a ceiling, ratio(p) = 2^((15 − CQ(p))/6) (the curve the estimate already used), and the
  estimate uses exactly the same number → monotonic sizes, 95% < 100% always.
- (3) The dialog's confirm button uses the default (Success) class; setting `ConfirmVideoMergerRemove` defaults to TRUE.
  → REMOVEUX_01: Danger button; default FALSE + settings schema bump forcing it FALSE once for existing installs; FloatingNotice
  "Removed … — press Ctrl+Z to undo" after every removal (the Merger's undo is Ctrl+Z; Ctrl+Y / Ctrl+Shift+Z redo).
- (4) The empty-queue overlay is semi-transparent over the mpv surface, which still holds the last frame. → EMPTYQUEUE_01: when
  the queue empties, stop mpv, forget the EDL and hide the video surface; show it again when a clip arrives.
Tasks:
- **P11.1 LANECACHE_02 thumbnails (fixed grid, memory+disk cache, slot picking)** · `[x] DONE (manual M-P11 pending)`
- **P11.2 LANECACHE_02 waveform peaks (vector, memory+disk cache)** · `[x] DONE (manual M-P11 pending)`
- **P11.3 MERGEQUALITY_01 below-100% bitrate target + matching estimate** · `[x] DONE` (harness: 100% 17.1 MB, 95% 13.9 MB, 50% 4.8 MB from 16.9 MB of sources)
- **P11.4 REMOVEUX_01 red confirm, default off (+migration), undo notice, Settings label** · `[x] DONE (manual M-P11 pending)`
- **P11.5 EMPTYQUEUE_01 no ghost frame** · `[x] DONE (manual M-P11 pending)`
- As built (P11): Core ProgressiveLanes.cs (LanePlanner key = clip, ThumbGrid), NEW Core Media/WaveformPeaks.cs, Core
  ApplicationPaths.LaneCacheDirectory, NEW App Infrastructure/LaneDiskCache.cs, App VideoMergerWindow.Lanes.cs rewritten
  (PlaceFilmTile slots via CroppedBitmap, PlaceWaveTile vector envelope, disk-first builders, CurrentTile), Core OutputFileSize
  .MergerQualityRatio + MergerWorker below-100% VBR target, App OutputSizeEstimator (same number), SettingsManager v9 +
  ConfirmVideoMergerRemove=false, SettingsWindow.axaml label, ClipActions.cs + axaml.cs destructive styling, Session.cs
  (NoteClipsRemoved notice, empty → ClearPreviewSurface, first clip → surface shown), EdlPreview.cs ClearPreviewSurface.
  Tests: ProgressiveLanesTests (+2, keys width/position-independent), OutputSizeEstimateTests (+1), SettingsMigrationTests (+1).
  Harness /tmp/sc/harness modes `peaks` (80 peaks for 2 s, 1.3 s incl. process start) and `quality`. docs/03/04/05, INDEX,
  sentinels 322. Gates: b.sh 0/0, aot 0, Core 363, App 82/83 (env-only).
- Manual M-P11 (user, dev.cmd): add clips → the filmstrip/waveform fill once; resize the window repeatedly → they re-lay out
  instantly, no rebuilding; reorder → instant; close and reopen the Merger → instant from disk. Merge at 95% → file smaller
  than at 100%; the estimate matches. Select a clip + Delete → removed at once, notice "Press Ctrl+Z to undo"; Ctrl+Z brings
  it back; Settings › Confirmation Dialogs → turn the Merger remove question on → the dialog's YES, REMOVE is red. Remove the
  last clip → clean NO VIDEO LOADED screen, no ghost frame; add a clip → the picture is back.

---

### P12 — Merger round 3 (user report 2026-09-28 00:31, dev.cmd)
- User asked: (a) thinner, gentler yellow marching ants on the upper timeline AND the thumbnail blocks; (b) the waveform lane must click/drag-seek too;
  (c) the playhead must be a red line through the whole timeline (scale → scrub row → thumbnails → waveform), draggable/seekable on the scale/scrub row
  and the waveform; the thumbnail lane keeps its own job (select/reorder).
- **P12.1 MERGERPLAYHEAD_01** · `[x] DONE (manual M-P12 pending)`
- As built: NEW `VideoMergerWindow.Playhead.cs` (`AttachMergerPlayhead`/`PositionPlayhead`: one non-hit-testable Canvas added in code to the timeline Grid,
  RowSpan = all rows, ZIndex 60; 2 px `AppPlayheadBrush` #ef4444 line + 10×6 downward cap; follows `TimelineSlider.Value` via PropertyChanged and the layer's
  SizeChanged; x = value fraction × column width = the seek's own mapping). `IsOnSeekRows` (TimelineSelect.cs) now also accepts presses on the visible
  waveform lane (instance method, uses `WaveLaneCtl`); the blocks lane is still on top and handles its own presses first. `AntsThickness = 1.25` (was 2)
  for both ants rectangles (dash array unchanged → dashes scale down with the stroke). `VideoMergerWindow.axaml`: `Slider#TimelineSlider /template/ Thumb`
  Opacity 0 (Merger only). `AvaloniaApp.axaml`: token `AppPlayheadBrush`. The hook is on the SAME line as `TimelineKnob.Attach` in `axaml.cs` (ceiling 2246 kept).
  No new FindControl (ratchet). Docs: 04 §9 + mini-map, INDEX, README, sentinels (+5 → 327).
- Manual M-P12 (user, dev.cmd): ants thinner on both views; red line from the time labels to the bottom of the waveform, moves while playing;
  click/drag on the time scale, the scrub row or the waveform seeks and the line follows the pointer; press on a thumbnail block still selects/drags the
  clip (no seek); no dot left on the scrub row; resize the window → the line stays on the same moment.

## 8. RISKS AND OPEN QUESTIONS

| # | Risk / question | Plan |
|---|---|---|
| R1 | mpv EDL boundary accuracy is not documented | Spike P4.1 with explicit pass criteria; fallback exists |
| R2 | `GranularSpeedEditorWindow` is 8 000 lines and single-file-centric | P6.1 investigation plus an abstraction; ask the user if the UX needs to change |
| R3 | VFR sources make seconds-based cuts inexact | P2.2 frame-pts snapping |
| R4 | 0.1 s ≠ whole frames at 59.94 fps | Tag v2 stores frames (P1) |
| R5 | Semantics of `SpeedSegment.Speed` vs the global base speed (absolute or multiplied?) | CHECK the Main App before P6.2 and mirror it EXACTLY; record the finding in P6.2 Notes |
| R6 | Runtime of the Avalonia 11.3 / D3D11 interop / `WavAudioReader` changes is not yet user-verified on Windows | Ask the user for the preview/voice-over/drag test result before starting P4 |
| R7 | Memory/CPU with 50+ clips (filmstrip, waveform, probes) | Bounded worker pools, caches, cancellation (P5) |
| R9 | `runtime.manifest.json` (obj\ReleaseAssets) is NOT uploaded by `GitHubReleasePublisher`, so the patch-update path stays inert | Small follow-up task (not part of the Merger migration); ask the user before doing it |
| R8 | Publishing with the LOCAL dev certificate (D15) | RESOLVED 2026-09-26: option A → `SIGNLOCAL_02` in `build/FvsBuild/Program.cs` publishes dev-signed builds with a warning. Notification works; in-app install stays refused on user PCs (UPDATETRUST_02). Full auto-install needs a publicly trusted certificate (option C, later). VERIFIED 2026-09-26 12:25: `Build.cmd` built, signed and published `v2026.09.26.1215` and removed `v2026.09.18.0937`; the asset hash matched. |

---

## 9. MANUAL TEST CHECKLIST FOR THE USER (filled by agents as features land)

- [x] Main App: preview shows the picture; voice-over takes play; merger drag-reorder works (R6) — user confirmed 2026-09-26
- [x] Merger: preview plays across clips seamlessly (P4)
- [x] Merger: filmstrip/waveform fill left→right while you can already play/edit (P5)
- [x] Merger: one seamless preview across clips, reorder while playing (M-P4.2)
- [x] Merger: speed ramp inside a clip, preview = export (P6/P8, M-P8.1)
- [x] Merger: freeze inside a clip (P6/P8)
- [x] Merger: zoom on a portrait clip inside a landscape merge (P6.3)
- [x] Merger: memes at start / middle / end of a clip, fades correct (P6.4); preview plays them (M-P8.2)
- [x] Merger: music starts on the chosen video moment after speed edits (P7.2)
- [x] Merger: close before merging → reopen → everything restored (P3.2)
- [x] Merger: undo/redo of every edit type (P3.3)
- [x] Main App: open Merger or Crop Tools → return → Upload Video plays; the clip that was open is back, paused at MARK START (TOOLRETURN_01)

---

## 10. CONVENTIONS

- New code goes in NEW partial files for windows (`VideoMergerWindow.<Feature>.cs`) and pure logic in `Core`.
- Every new file starts with the repo's `[SPEC CONTRACT] STRICT GOVERNANCE` header pointing at the owning doc.
- Comments explain WHY, tagged with the task ID (e.g. `// MERGEEDL_01 — ...`).
- AOT: no reflection, no `dynamic`, JSON via source-generated contexts, no `System.Linq.Expressions`.
- Threading: probes, thumbnails and waveforms on the thread pool with bounded `SemaphoreSlim`; UI updates via `Dispatcher.UIThread.Post`; version counters drop stale results (see `ScheduleTimelineRebuild`).

---

## 11. PROGRESS LOG (append only, newest at the bottom)

Format: `YYYY-MM-DD HH:MM (tz) · agent · task · event · details`

- 2026-09-26 11:55 (+03) · agent-1 · P0.1 · DONE · Plan written from the user's decisions D1–D13. Nothing in P1–P9 has started. The next agent starts at **P1.1**. Before that, ask the user for the R6 runtime check result and the outcome of the pending `Build.cmd` re-run (`RELEASEASSETS_01`).
- 2026-09-26 12:05 (+03) · agent-1 · — · USER · R6 confirmed OK (preview, voice-over, drag-reorder). Answered "go". New requirements recorded as D14 (→ P5.3) and D15 (→ R8, open).
- 2026-09-26 12:05 (+03) · agent-1 · P1.1 · START
- 2026-09-26 12:40 (+03) · agent-1 · P1.1–P1.3 · DONE · ExportTimingTag + ProcessWorker/MergerWorker stamping + analyzer reading. Gates: b.sh 0/0, Release TWAE 0/0, aot.sh TWAE 0 lines, Core 281 pass, App 79/80 (env-only OutputSizeProbe), sentinels 240 OK. Harness T1.2a/b and T1.3b passed (see P1.3 Notes).
- 2026-09-26 12:40 (+03) · agent-1 · NEXT · Waiting on R8 (publishing choice). Code work continues with **P2.1** (MergeEdl records).
- 2026-09-26 12:15 (+03) · agent-1 · R8/D15 · DONE (awaiting user verification) · User chose A. `SIGNLOCAL_02`: `build/FvsBuild/Program.cs` no longer refuses to publish a dev-signed build (warning only); `CodeSigning.cs` messages, `docs/05 §5`, INDEX, sentinels and `ssl-certificate/README.md` updated. FvsBuild builds 0/0; sentinels 241 OK. The user must run `Build.cmd` (no flags) to confirm the GitHub release is replaced.
- 2026-09-26 12:15 (+03) · agent-1 · P0.1 · UPDATE · Banner: added the mandatory continuous verbose documentation rule (user request).
- 2026-09-26 12:15 (+03) · agent-1 · NEXT · **P2.1** (MergeEdl records) is the next code task. Not started.
- 2026-09-26 12:25 (+03) · agent-1 · R8/D15 · VERIFIED · The user ran `Build.cmd`: build OK, dev-signed and verified, release `v2026.09.26.1215` published, old `v2026.09.18.0937` and its tag removed, asset hash matched. Added R9 (manifest not uploaded).
- 2026-09-26 12:25 (+03) · agent-1 · NEXT · **P2.1** (MergeEdl records). Not started.
- 2026-09-26 12:30 (+03) · agent-1 · P2.1 · START · Design: time unit = SOURCE MICROSECONDS (long), not frame index, because sources can be VFR; P2.2 snaps these to real frame pts. File: `src/FortniteVideoSoftware.Core/Media/MergeEdl.cs` + `MergeEdlJsonContext` (source-gen). Records: MergeEdl, EdlClip, EdlEffects, EdlSpeedSegment, EdlZoom, EdlFreeze, EdlMeme (+EdlMemePlacement Mid/AtStart/AtEnd), EdlAnchor(ClipId, SourceUs), EdlThumbnail, EdlMusic. Hand-written structural Equals for records holding lists (UNDOEQ_01 style).
- 2026-09-26 12:40 (+03) · agent-1 · P2.1 · DONE · MergeEdl.cs + MergeEdlTests.cs; Core tests 289 pass; Core Release TWAE 0/0. (Full gates run at the end of P2; sync done with P2.)
- 2026-09-26 12:40 (+03) · agent-1 · P2.2 · START · Frame snapping via ffprobe frame pts.
- 2026-09-26 12:55 (+03) · agent-1 · P2.2 · DESIGN · FRAMESNAP_01 logged in P2.2 Notes. Files to touch: NEW Core/Media/FramePtsProbe.cs, MergeClipAnalyzer.cs, MergerWorker.cs (trim precision), NEW Core.Tests/FramePtsProbeTests.cs, docs/03 + INDEX + sentinels.
- 2026-09-26 13:20 (+03) · agent-1 · P2.2 · DONE · Gates: b.sh 0/0, Release TWAE 0/0, aot.sh 0 lines, Core 298 pass, App 79/80 (env-only OutputSizeProbe), FvsVerify 244 OK. T2.2a/b/c pass. Next: P2.3 CompositeTimeline.
- 2026-09-26 13:30 (+03) · agent-1 · P2.3 · START · Design COMPOSITE_01 logged in P2.3. Files: NEW Core/Media/CompositeTimeline.cs, MergeEdl.cs (+IntroCutUs), NEW Core.Tests/CompositeTimelineTests.cs, docs/01 + INDEX + sentinels.
- 2026-09-26 13:55 (+03) · agent-1 · P2.3 · DONE · Gates: b.sh 0/0, Release TWAE 0/0, aot.sh 0 lines, Core 304 pass, App 79/80 (env-only), FvsVerify 246 OK. Next: P2.4 (MergedTimeline → adapter over CompositeTimeline).
- 2026-09-26 14:10 (+03) · agent-1 · P2.4 · DONE · Gates: b.sh 0/0, Release TWAE 0/0, aot.sh 0 lines, Core 305 pass, App 79/80 (env-only). P2 COMPLETE. Next: P3.1 persistence (P4.1 spike needs the user's machine).
- 2026-09-26 14:20 (+03) · agent-1 · P3.1 · START · Design PROJ_12 logged. Files: Core/Project/ProjectDocument.cs, ProjectSerializer.cs, Core.Tests/ProjectDocumentTests.cs (T3.1a/b), docs/06 + INDEX + sentinels.
- 2026-09-26 14:40 (+03) · agent-1 · P3.1 · DONE · Gates: b.sh 0/0, Release TWAE 0/0, aot.sh 0 lines, Core 308 pass, App 79/80 (env-only), FvsVerify 249 OK. Next: P3.2 autosave/restore.
- 2026-09-26 13:20 (+03) · agent-1 · P3.2 · START · Design MERGESESSION_01 logged in P3.2. Files: NEW Core/Media/MergerSession.cs, NEW Core/Infrastructure/MergerAutosaveStore.cs, ApplicationPaths.cs, NEW App/VideoMergerWindow.Session.cs, small hooks in VideoMergerWindow.*.cs, ToolNavigator.cs, NEW Core.Tests/MergerSessionTests.cs, docs/05 + INDEX + sentinels.
- 2026-09-26 13:50 (+03) · agent-1 · P3.2 · DONE · Gates: b.sh 0/0, Release TWAE 0/0, aot.sh 0 lines, Core 313 pass, App 79/80 (env-only; architecture ceilings OK), FvsVerify 254 OK. Manual M-P3.2 pending. Next: P3.3 undo/redo.
- 2026-09-26 14:00 (+03) · agent-1 · P3.3 · START · Design MERGEUNDO_01 logged. Files: Core/Media/MergerSession.cs, Core/Undo/UndoStack.cs (+ReplaceCurrent), NEW App/VideoMergerWindow.History.cs, VideoMergerWindow.Session.cs, NEW Core.Tests/MergerUndoTests.cs, docs/06 + INDEX + sentinels.
- 2026-09-26 14:30 (+03) · agent-1 · P3.3 · DONE · Gates: b.sh 0/0, Release TWAE 0/0, aot.sh 0 lines, Core 318 pass, App 79/80 (env-only), FvsVerify 257 OK. P3 COMPLETE (manual checks M-P3.2/M-P3.3 pending). Next: P5 (P4.1 needs the user's PC).
- 2026-09-26 13:45 (+03) · agent-1 · P3 · VERIFIED · User ran M-P3.2 and M-P3.3 on Windows: "all well".
- 2026-09-26 13:50 (+03) · agent-1 · P5.1+P5.2 · START · Design LANES_01 logged. Files: NEW Core/Media/ProgressiveLanes.cs, VideoMergerWindow.axaml, NEW App/VideoMergerWindow.Lanes.cs, VideoMergerWindow.Timeline.cs (1 hook), NEW Core.Tests/ProgressiveLanesTests.cs, docs/04 + INDEX + sentinels.
- 2026-09-26 14:15 (+03) · agent-1 · P5.1+P5.2 · DONE · Gates: b.sh 0/0, Release TWAE 0/0, aot.sh 0 lines, Core 324 pass, App 79/80 (env-only), FvsVerify 260 OK. Manual T5.1b/T5.2b pending. Next: P5.3 (marching ants + two-way drag).
- 2026-09-26 14:25 (+03) · agent-1 · P5.3 · START · Design ANTS_01 logged. Files: VideoMergerWindow.axaml (style), NEW App/VideoMergerWindow.TimelineSelect.cs, Timeline.cs (chip + ants hooks, key), Session.cs (init), NEW Core/Media/TimelineReorder.cs + tests, docs/04 + INDEX + sentinels.
- 2026-09-26 14:40 (+03) · agent-1 · P5.3 · DONE · Gates: b.sh 0/0, Release TWAE 0/0, aot.sh 0 lines, Core 332 pass, App 79/80 (env-only), FvsVerify 264 OK. P5 COMPLETE (manual T5.1b/T5.2b/T5.3c pending). Next: P6.1 granular editor host.
- 2026-09-26 14:35 (+03) · agent-1 · P5 · VERIFIED · User ran T5.1b/T5.2b/T5.3c on Windows: "ALL WELL".
- 2026-09-26 14:45 (+03) · agent-1 · P6.1 · INVESTIGATED · Editor facts + per-clip host proposal written in P6.1; 3 UX questions put to the user. BLOCKED until answered.
- 2026-09-26 15:00 (+03) · agent-1 · P6.1 · DONE · User answers: whole merge (D16), memes by position (D17), cuts yes (D18). Agent decision D19 (base speed = Main App semantics). P6 restructured (P6.2 core, P6.3 editor seams, P6.4 Merger button).
- 2026-09-26 15:00 (+03) · agent-1 · P4.1 · DONE · mpv EDL spike PASS in the container (details in P4.1). ffconcat fails on mixed codecs → filmstrip stitched per clip.
- 2026-09-26 15:05 (+03) · agent-1 · P6.2 · START.
- 2026-09-26 16:15 (+03) · agent-1 · P6.2 · DONE · Resumed uncommitted P6.2 work after a context reset; fixed one wrong test expectation. Gates: b.sh 0/0, Release TWAE 0/0, aot.sh 0 lines, Core 346 pass, App 79/80 (env-only), FvsVerify 265 OK. Next: P6.3 editor seams.
- 2026-09-26 16:25 (+03) · agent-1 · P6.3+P6.4 · START · Design MERGEEDIT_02 logged. Files: GranularSpeedEditorWindow.axaml.cs (3 in-place lines), .History.cs, NEW .Merge.cs, VideoMergerWindow.axaml (button), NEW VideoMergerWindow.Effects.cs, .History.cs (label override), docs/04 + INDEX + sentinels.
- 2026-09-26 16:50 (+03) · agent-1 · P6.3+P6.4 · DONE · Gates: b.sh 0/0, Release TWAE 0/0, aot.sh 0 lines, Core 346 pass, App 79/80 (env-only; editor ceiling 8068 unchanged), FvsVerify OK. mpv integration check PASS. Manual M-P6 pending. Next: P7.1 export graph from the EDL.
- 2026-09-26 17:00 (+03) · agent-1 · P6 · VERIFIED · User: M-P6 "all well". New request logged as D20 / P5.4 (delete + menus + instant reorder + clip blocks). Reorder id-loss bug found (RemoveAt+Insert).
- 2026-09-26 17:25 (+03) · agent-1 · P5.4 · DONE · Gates: b.sh 0/0, Release TWAE 0/0, aot.sh 0 lines, Core 347 pass, App 79/80 (env-only), FvsVerify OK. Manual M-P5.4 pending. Next: P7.1.
- 2026-09-26 19:15 (+03) · agent-1 · P5.4 · VERIFIED · User: "CONFIRMED". P7.1 · START · Design MERGEGRAPH_01 logged. Files: NEW Core/Media/MergeClipGraph.cs, MergerWorker.cs, CompositeTimeline.cs (meme source helper), App VideoMergerWindow.Scraper.cs (worker.Edl), NEW Core.Tests/MergeClipGraphTests.cs, docs/03 + INDEX + sentinels.
- 2026-09-26 19:55 (+03) · agent-1 · P7.1-P7.3 · DONE · Gates: b.sh 0/0, Release TWAE 0/0, aot.sh 0 lines, Core 352 pass, App 79/80 (env-only), FvsVerify 277 OK. Harness T7.1b/T7.2b PASS. Manual M-P7 pending. Next: P8 preview parity, then P4.2.
- 2026-09-26 20:20 (+03) · agent-1 · BUGFIX EDLNULL_01 · User log 19:48: the Merger reopened with 7 OLD clips, then crashed on Remove.
  Root cause: the autosave (merger_session.json) was written before `EdlEffects.Cuts` existed (P6.2). The source-generated reader
  leaves missing init properties at default(T) (null lists, false/0 scalars), so every `CaptureEdlNow` threw in EdlEffects.Equals
  (swallowed at DEBUG level) → the autosave and the project stopped following the queue → the next open restored the stale list →
  Remove → ApplyTimeline → UpdateMergerGranularButton → IsEmpty NRE → unhandled → process exit 1. The 7 files still exist (all probed OK).
  Fix: null-proof EDL properties + FromJson fills missing keys from defaults (MergeEdl.cs); capture failures now WARN (Session.cs);
  button paint and the instant rebuild inside CollectionChanged can no longer throw out (Effects.cs, Session.cs). Test
  MergeEdlTests.OldAutosaveShapes_LoadWithEmptyLists_AndCompare. Gates: b.sh 0/0, Release TWAE 0/0, aot.sh 0, Core 353, App 79/80.
  OPEN QUESTION to the user: keep silent auto-restore, ask before restoring, and/or clear the session after a successful MERGE.
- 2026-09-26 20:40 (+03) · agent-1 · RESTOREMISS_01 · DONE · User decision D21. Session.cs ApplyRestorePlanAsync shows the approval dialog; MergerSession.DescribeRestoreProblems + test. Gates: b.sh 0/0, Release TWAE 0/0, aot.sh 0, Core 354, App 79/80. Next: user re-test, then P8.
- 2026-09-26 21:10 (+03) · agent-1 · TOOLRETURN_01 · DONE · User bug: after returning from the Merger (also Crop Tools) the Main App player was dead; Upload logged the pick but nothing played. Root cause (baseline, not the migration): ToolNavigator disposes the main MpvVideoView (TOOLNAV_02) and MainWindow passed restoreVideoPipeline: null, so IpcClient stayed null. Fix: new MainWindow.ToolReturn.cs swaps in a fresh MpvVideoView, starts it (StartVideoHostAsync, the old InitializeMpv body), reloads the open clip paused at MARK START; fallback badge now bound in code (axaml Name=PreviewFallbackBadge). Files: MainWindow.ToolReturn.cs (new), MainWindow.axaml.cs (InitializeMpv one-liner, restore hook), MainWindow.axaml, docs/05 §3, sentinels. Gates: b.sh 0/0, aot.sh 0, Core 354, App 79/80, sentinels ok. Waiting: user re-test.
- 2026-09-26 21:15 (+03) · agent-1 · — · USER · "continue to p8 but document first". Added §0 RESUME HERE snapshot. P8 depends on P4.2, so P4.2 goes first.
- 2026-09-26 21:15 (+03) · agent-1 · P4.2 · START · Reading VideoMergerWindow.Timeline.cs (per-file switching) to design the single EDL load.
- 2026-09-26 21:35 (+03) · agent-1 · P8.1 · Core MergerPreviewPlan.cs + MergerPreviewPlanTests (3/3 pass, T8.1a PASS). Next: App EdlPreview.cs (P4.2 + P8.1 wiring).
- 2026-09-26 21:50 (+03) · agent-1 · P4.2 + P8.1 · DONE (manual pending) · NEW App VideoMergerWindow.EdlPreview.cs; Timeline.cs merged block rewritten; MusicPreview.cs output clock; Session.cs speed re-assert; docs/01 §10, INDEX, sentinels 290. Gates: b.sh 0/0, aot.sh 0, Core 357, App 79/80. Next: P8.2.
- 2026-09-26 22:05 (+03) · agent-1 · P8.2 · DONE (manual pending) · MemePreviewDirector reused over the EDL (EdlPreview.cs TickMergerMemes, Timeline.cs early return + seek guard, axaml MemeSwapOverlay, Core MemePlacements). Gates: b.sh 0/0, aot.sh 0, Core 357, App 79/80, sentinels 292. Next: user manual checks, then P9.
- 2026-09-26 22:40 (+03) · agent-1 · — · USER · All manual checks passed ("all done tested it looks good"): M-P4.2, M-P5.4, M-P6, M-P7, M-P8.1, M-P8.2, TOOLRETURN_01. §9 all ticked. Instruction: finish the job, document each step, do not stop.
- 2026-09-26 22:40 (+03) · agent-1 · P9 · START · Finishing list written in §0 (known limits A/B, P2.3 OPEN note, P9.1 audit, P9.2).
- 2026-09-26 22:55 (+03) · agent-1 · P9 limit A · DONE · MERGESIZE_01: OutputSizeEstimator.MergerSizeRequest.OutputSeconds + CalculateMerger(outputSeconds); Merger Effects.cs EdlDescribesQueue/EdlOutputSec (MusicExportSec refactored onto it); axaml.cs request line + dedupe (line count unchanged 2246); Scraper.cs PaintMergedLength; Session.cs changed capture → estimate. Test OutputSizeEstimateTests.Merger_UsesTheEditListLength_WhenKnown. docs/03 FFM-SIZEESTIMATE, INDEX, sentinels. App 80/81 (env-only), Core 357.
- 2026-09-26 23:10 (+03) · agent-1 · P9 limit B · DONE · MEMELEVEL_01: NEW Core Media/MemeLoudness.cs (GainDbAsync/GainFor/Chain = ProcessWorker's rule, which is left untouched); MergerWorker measures each meme file once (MemeInputFile.GainDb) → MergeMemeInput.GainDb → MergeClipGraph meme audio `volume` + `alimiter`. Test MergeClipGraphTests.MemeLevel_MatchesTheMainAppRule. Harness /tmp/sc/harness (Program.cs rewritten for P9.2; run `DOTNET_ROOT=/root/.dotnet ./harness` in bin/Debug/net9.0; old EDLNULL harness saved /tmp/sc/Program.edlnull.bak): 3 clips cut+AtStart video meme / ramp+Mid video meme / freeze+AtEnd image meme → 16.500 s output = 16.500 predicted, audio 16.500; meme -20.6 LUFS raised. Core 358, App 80/81.
- 2026-09-26 23:30 (+03) · agent-1 · P9 item 3 · DONE · CLIPFRAMES_01 (MergerWorker plain chain): harness `fps` mode (/tmp/sc/p9 Y 59.94 2.386 s / P 30 fps 1.933 s / L 60 fps 2.017 s, re-encoded from /tmp/sc/p4) found plain = 381 frames vs 380 predicted; fixed with tpad+trim=end_frame and apad+atrim per clip → 380/380 (1.0x), 253/253 (1.5x); effects chain already exact 410/410, 293/293; p92 re-run 16.500 s = prediction, 990 frames. Harness: /tmp/sc/harness/Program.cs modes `fps [speed]` and `p92` (container only). Core 358, App 80/81.
- 2026-09-26 23:45 (+03) · agent-1 · P9.1 · DONE · Tag audit scripted (sentinels-since-baseline ⇒ INDEX + docs; code tags first seen in migration ⇒ sentinel + INDEX): 0 gaps. Added: docs/07 UNDO_20 bullet, docs/03 SCRAPER_04 label, INDEX TOOLRETURN_01 + 21 file rows. P9.2 START.
- 2026-09-26 23:55 (+03) · agent-1 · P9.2 · DONE (agent side) · Debug 0/0, Release TWAE 0/0, aot.sh 0, Core 358/5 skip, App 80/81 (OutputSizeProbe needs repo media binaries — env-only), FvsVerify 301, harness fps1.0/fps1.5/p92 exact. §0 set to MIGRATION COMPLETE. Waiting: user Build.cmd --no-publish, R9.
- 2026-09-27 00:05 (+03) · agent-1 · FINAL SYNC CHECK · Whole tree (src, tests, docs, build + this file; 347 files, bin/obj excluded) md5-identical device ↔ container (aggregate 6dc5bda5… before this line). Only difference found was the version stamp in the two .csproj files (device build stamped 2026.09.26.1215); the container mirror was aligned to the device. Nothing pending on the agent side.
- 2026-09-26 23:30 (+03) · agent-1 · BUGFIX MUSICPAD_01 (Main App, non-plan, user report 23:07: pink music notes land differently in the file than in the preview) · ROOT CAUSE: with fades on, ProcessWorker mixes the music into a body that starts at the fade-in pad (up to 1 s before MARK START) while the UI measures the music delay from MARK START → music 1 pad early (and ends 1 pad early). Base speed 1.1x was NOT the cause (preview and export share OutputTimeline incl. base speed). Proof: harness `music` mode (/tmp/sc/mus: 12 s testsrc + silent audio, 1 kHz tone; trim 3–9 s, 1.1x, music at 5–8 s) → onset 1.923 s vs 2.918 s expected; after fix 2.923 s. FIX: NEW Core Media/MusicPadAlignment.cs + one call in ProcessWorker after the track list; lead-in pre-roll + tail run-on refinements. Tests MusicPadAlignmentTests (3). docs/02 AUD-PREVIEWSYNC, INDEX, sentinels. Core 361, App 80/81.
- 2026-09-27 10:05 (+03) · agent-1 · P10 · START · User report 09:51 (6 items) + decisions D22–D25 recorded; root causes R-a/R-b for the broken drag found by code reading + dev log.
- 2026-09-27 10:40 (+03) · agent-1 · P10.1–P10.7 · DONE (manual M-P10 pending) · see P10 "As built". Gates: b.sh 0/0, aot 0, Core 361, App 80/81, sentinels 308.
- 2026-09-27 10:25 (+03) · agent-1 · P11 · START · User report 10:11 (4 items); root causes + design recorded under P11. Research: sebi.io (input seeking 3.8x faster than fps filter), Kdenlive per-clip thumbnail caches.
- 2026-09-27 11:10 (+03) · agent-1 · P11.1–P11.5 · DONE (manual M-P11 pending) · see P11 "As built". Gates: b.sh 0/0, aot 0, Core 363, App 82/83, sentinels 322.
- 2026-09-27 11:40 (+03) · agent-1 · DOCS AUDIT · 4 read-only audits (01+02, 03+09, 04+05, 06-08+README+GOV+INDEX) → 93 findings in Docs-Audit-2026-09-27.md; nothing edited. Two are real code defects (09: runtime fingerprint written after the zip; updater never fetches the app-only package — relates to R9). Waiting for user confirm/deny.
- 2026-09-27 17:10 (+03) · agent-1 · DOCS AUDIT APPLY · 92/93 applied by 6 doc agents (01/06/07, 02, 03, 04, 05/08/09, README+GOV+INDEX), each claim re-verified against code; INDEX +55 sentinel rows, all anchors resolve; README §3 routing regenerated from mini-maps. #84 held for user review. Gates: FvsVerify 322 OK, Core 363, App 82/83 (OutputSizeProbe env-only).
- 2026-09-28 01:00 (+03) · agent-1 · P12.1 MERGERPLAYHEAD_01 · DONE (manual M-P12 pending) · red playhead line, waveform seeks, ants 1.25 px. Gates: b.sh 0/0, Release TWAE 0/0, aot 0, Core 363, App 82/83 (env-only), sentinels 327. Open-issues list 1–18 sent to user (not resolved).
