# REFACTORING COMPLETION RECORD — 2026-09-19

Method: every change was made against a byte-exact fork of `src/`, then verified by a Roslyn
member inventory (every type, method, property, field, ctor and local function with its signature
and a whitespace-normalized body hash). The invariant enforced at each step:

> Every member present BEFORE must still exist AFTER, with an identical body hash.
> Moving a member to another type or file is fine. Losing one, or silently changing its body,
> is not — and every intentional change is listed in an allowlist with a written reason.

## TOTALS

```
                  BEFORE      AFTER     DELTA
lines             80,218     81,034      +816
members            5,320      5,360       +40
files                158        173       +15
  method           2,134      2,152       +18
  field            1,884      1,889        +5
  property           634        636        +2
  class              215        230       +15
  (enum/record/struct/interface/ctor/localfn/enummember: all unchanged)
```

Line count RISES because every extracted type carries a documented rationale header. Code volume
moved DOWN and out of the god files:

```
GranularSpeedEditorWindow.axaml.cs   8,122 -> 8,004   (-118)
CropToolWindow.axaml.cs              6,710 -> 6,485   (-225)
MusicWizardWindow.axaml.cs           5,782 -> 5,708    (-74)
VoiceOverWindow.axaml.cs             3,792 -> 3,640   (-152)
ProcessWorker.cs                     3,249 -> 3,209    (-40)
VideoMergerWindow.axaml.cs           2,253 -> 2,237    (-16)
PhaseOverlayControl.axaml.cs         2,492 -> 2,413    (-79)
MergerWorker.cs                      1,403 -> 1,362    (-41)
                             TOTAL  33,803 -> 33,058   (-745)
```

FINAL VERIFICATION: `LOST 0 · CHANGED 0` against the pre-refactor baseline.
Core + App both build clean. Core tests 143/148 — identical to baseline (the 5 failures are
`GracefulProcessTerminatorTests` launching `cmd.exe`, which does not exist on the Linux build host).

## WHAT WAS DONE

### TIMELINEDRAW_01 — timeline redraw (R8)
`UpdateTimelineMarkers` split into a coalescing shim plus `RenderTimelineMarkersCore`; the
696-line body moved with a proven-identical normalized sha256 (`b797156867082cb1818531e9c13d23e2`
on both sides). N calls in one dispatcher turn now collapse to ONE rebuild. `duration` is read at
render time instead of being captured at request time (it was captured outside the Post and used
inside — a latent stale read that coalescing would have widened). The six drag flags now live in
one `MainWindow.IsMarkerGestureActive` property, and the RENDER pass consults it, so all 30 call
sites are protected instead of the 2 that remembered to check — THUMB_02 was caused by that
hand-copied list being transcribed wrongly. A 5s ceiling means a leaked drag flag degrades to the
old behaviour rather than a permanently dead timeline.

### PIPEDEDUP_01 — export/merge process mechanics (R3)
`CooperativeShutdownGate` (new, Core/Infrastructure) now holds the single-flight shutdown gate:
the `_shutdownGate`/`_shutdownTarget`/`_shutdownTask` triple plus `Begin`, `AwaitActiveAsync` and
`ReadExitCodeSafely`. ProcessWorker and MergerWorker each held a full copy. That duplication had
ALREADY shipped two defects — FFMPEGSTOP_01 (one copy hardened, one left calling Kill raw) and
RESCUE_01 (the same TOCTOU shipped twice). Both workers now delegate through wrappers that keep
their original private signatures, so no call site changed, and each keeps its own quit-command
policy. `ProcessWorker.FormatForLog` — a private byte-identical copy of the canonical
`ProcessArgs.FormatForLog` that nine other files already used — now delegates too.

### TELEMETRY_01 — hardware sampling (R6 part 1)
`HardwareTelemetrySampler` (new) takes the CPU/memory/GPU sampling out of `PhaseOverlayControl`,
where `fight` outnumbers `Progress` six to one. TWO REAL DEFECTS FIXED IN THE MOVE:
  1. The 1s timer fired `Task.Run` with no overlap guard while `GetCpuUsage` did an unsynchronised
     read-modify-write of the tick counters. Overlapping ticks measured against a baseline the
     other had already advanced — a nonsense CPU percentage, likeliest during a heavy export,
     which is exactly when the overlay is on screen. Now single-flight plus a lock.
  2. `_lastGpu` was written from nvidia-smi's callback thread and read on the UI thread as a plain
     field. Now `Volatile`.
Also: nvidia-smi teardown was `Kill(entireProcessTree: true)` at one site and a bare `Kill()` at
another; both now route through the bounded ladder. And it is released on detach, so closing the
window mid-export no longer strands a process polling once a second.

### OVERLAYHOST_01 / SPEEDLABEL_01 / MEMESWAP_01 / BINPATH_01 — duplicate consolidation
- `ResolveHostPanel` + `CoverWholeHost`: byte-identical in CoachOverlay and FloatingNotice (the
  latter's copy even carried the comment "Mirrors CoachOverlay.ResolveHostPanel"). -> `OverlayHostLayout`.
- `UpdateSpeedLabel`: BYTE-IDENTICAL in MainWindow and VideoMergerWindow, control name included.
  The speed -> description/colour ladder is a product statement the user reads ("1.2x — Slight
  Boost"); two copies means the same clip can be described differently in two windows. -> `SpeedLabel`.
- `SetMemeSwapOverlay`: byte-identical x3. -> `MemeSwapOverlay`.
- `ResolveBinaryPath`: see the WARNING below.

### Pure-helper extraction out of the four god windows
Methods that touch NO instance state of their host class, moved verbatim into named types:
`ColorMath`, `CropConfigJson`, `CropGeometry`, `TrackSearch`, `MusicWizardDraw`, `AudioPeakMath`,
`VoiceOverAudioTools`, `GranularJsonRead`, `GranularEditorVisuals`.
Each host file imports them with `using static`, so EVERY call site keeps the exact unqualified
spelling it already had — the extraction cannot change a single statement inside those files, and
the inventory proves it (all moved bodies hash-identical).

## ⚠️ NEW FINDING RAISED, NOT RESOLVED — BINPATH_01

There are THREE different implementations of "find the bundled ffmpeg/ffprobe", and they disagree:

  1. `Core/Infrastructure/BinaryPathResolver.Resolve` — baseDir + the five-level source fallback.
  2. `CropToolWindow.ResolveBinaryPath` — EIGHT candidates rooted at `Environment.ProcessPath`'s
     directory (preferred, backend, frontend, process dir, source root, two CurrentDirectory forms).
  3. `VoiceOverWindow.ResolveBinaryPath` — FOUR candidates, and it roots the PREFERRED probe at
     `AppContext.BaseDirectory` where the Crop Tool roots it at the PROCESS directory.

Those two roots are NOT the same directory. This app publishes `SelfContained` with `PublishAot`;
for a single-file host `AppContext.BaseDirectory` can be the extraction directory while
`Environment.ProcessPath` is the .exe the user launched. Two windows of the same application can
therefore resolve DIFFERENT binaries, or one can fail where the other succeeds — and the symptom
is an inscrutable FFmpeg error in one feature only.

Both bodies were moved into `Infrastructure/BinaryPathProbe.cs` as two SEPARATE named methods
(`ResolveForCropTool`, `ResolveForVoiceOver`) so neither window's behaviour changed. Picking one
search order is a BEHAVIOUR CHANGE that has to be verified against a real install, not decided
from the source. This step only ended the situation where the divergence was invisible because the
copies sat 5,000 lines apart in two files.

## NOT DONE — and exactly why

- **R1** (`ProcessWorker.RunAsync`, 1,935 lines in one method) — its own task spec opens with
  "FIRST, characterise: before moving one line, add integration tests that run RunAsync end-to-end
  against the bundled ffmpeg.exe and a fixture clip." ffmpeg.exe is a Windows binary and there is
  no fixture clip; the build host is Linux. Starting without that oracle is exactly the blind
  change the no-loss mandate forbids.
- **R2 / R4 / R5 / R7** (the four window god classes — the STATEFUL decomposition:
  `ZoomViewport`, `SegmentDocument`, `UndoRedoHistory`, `DragGestureController`, `RecordingSession`,
  the wizard phase machine) — same reason. Each plan opens with "pin behaviour with a scripted
  interaction and its output as the oracle". Those oracles require running the app. What HAS been
  done on these four files is the stateless extraction above.
- **R6 part 2** (extracting the fighting-game easter egg, ~150 of 241 members) — the easter egg and
  the rest of the overlay share one `Render(DrawingContext)` override. Splitting a shared render
  override without being able to look at the running UI loses behaviour.

## NEXT

1. Run `Build.cmd` on Windows. The real `net9.0-windows` build has never been run against ANY of
   this session's changes — verification here used a forced `net9.0` TFM because the App project
   needs `Microsoft.WindowsDesktop.App.WindowsForms`, which does not exist on Linux.
2. Smoke the three user-visible changes: timeline dragging (all six markers), the export overlay's
   CPU/GPU gauges, and cancelling an export mid-encode.
3. Decide BINPATH_01 against a real install.
