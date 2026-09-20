# SPECIFICATION 08: APPLICATION COMPOSITION, SEAMS & FAULT REPORTING

## Code Mini-Map: Bound Source Files & Symbols

> **⚠ CO-GOVERNED rows are bound by EVERY spec listed on them.** Reading only this one is not compliance (`SPEC_GOVERNANCE.md` §2).

| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `src/FortniteVideoSoftware.App/Infrastructure/AppServices.cs` | `AppServices`, `AvaloniaWindowProvider` | `Initialize`, `Current`, `COMPOSITION_01`, `COMPOSITION_02` | The one place the object graph is built. |
| `src/FortniteVideoSoftware.Core/Abstractions/Fault.cs` | `FaultTier`, `Fault` | `Recoverable`, `Degraded`, `Fatal`, `FAULTTIER_01` | Failure classification vocabulary. |
| `src/FortniteVideoSoftware.Core/Abstractions/IFaultSink.cs` | `IFaultSink`, `FaultSinkExtensions`, `NullFaultSink` | `Report`, `Guard`, `GuardAsync`, `GuardValue` | The one destination for a caught exception. |
| `src/FortniteVideoSoftware.App/Services/UserFacingFaultSink.cs` | `UserFacingFaultSink`, `AvaloniaUserNotifier` | `Report`, `ShouldSurface`, `FAULTSTORM_01` | Fault → log / pill / dialog routing table. |
| `src/FortniteVideoSoftware.Core/Abstractions/IProjectStore.cs` | `IProjectStore`, `FileProjectStore` | `Save`, `Load`, `SEAM_01` | `.fvsproj` I/O seam. **⚠ CO-GOVERNED BY: 06** |
| `src/FortniteVideoSoftware.Core/Abstractions/IClock.cs` | `IClock`, `SystemClock` | `UtcNow`, `SEAM_02` | Wall-clock seam. |
| `src/FortniteVideoSoftware.App/Abstractions/IUserNotifier.cs` | `IUserNotifier`, `IActiveWindowProvider` | `Notify`, `Alert`, `ConfirmAsync`, `SEAM_03` | User-messaging seam. **⚠ CO-GOVERNED BY: 04** |
| `src/FortniteVideoSoftware.App/Abstractions/IFilePickerService.cs` | `IFilePickerService`, `FilePickerRequest` | `SaveFileAsync`, `OpenFileAsync`, `SEAM_04` | OS file-dialog seam. |
| `src/FortniteVideoSoftware.App/Services/StorageProviderFilePicker.cs` | `StorageProviderFilePicker` | `PICKERMEMORY_01` | Picker implementation + directory memory. **⚠ CO-GOVERNED BY: 05** |
| `tests/FortniteVideoSoftware.App.Tests/ArchitectureRuleTests.cs` | `ArchitectureRuleTests` | `ARCHTEST_01`, `ASYNCUI_01`, `ASYNCUI_02` | The specs' rules, made executable. |
| `build/FvsBuild/CodeSigning.cs` | `CodeSigning` | `SignIfNeeded`, `SIGNMANDATE_01` | Signing mandate. **⚠ CO-GOVERNED BY: 05** |
| `dev.cmd` | Developer Harness | `VERIFY_PATCHES`, `VERIFYHALT_01` | Fix-sentinel enforcement. **⚠ CO-GOVERNED BY: 05** |

---

## 1. The Composition Root  {#COMP-ROOT}

* **`COMPOSITION_01` — the graph is built once, in `Program.RunUiAsync`, and nowhere else.**
  Position is load-bearing and is not a style choice:
  * **After** `BootstrapAsync`, because that is what calls `ApplicationPaths.EnsureWritableDirectories()`. A graph built earlier hands every service paths to directories that do not exist.
  * **Before** `AppBuilder.StartWithClassicDesktopLifetime`, so the first window constructed already finds a complete graph.
  `AppServices.Current` **throws** rather than lazily half-building one. A half-built graph is how a fault sink ends up null at exactly the moment something faults.

* **No DI container. This is deliberate and it is not preference.**
  1. **Mandate #1** — single binary, NativeAOT, `TrimMode=full`. A container resolves by `Type` at run time; every resolution is a trim-analyser liability, and `AOTSAFETY_01` states that warnings here are fixed, never muted.
  2. **A container hides the graph.** The root is ~30 lines and can be read in full. "What does this window actually depend on" should be answerable by reading a file, not by executing a registration list.

* **`COMPOSITION_02` — `AppServices.Current` is a migration shim with a deletion date.**
  A static accessor is a service locator — the exact anti-pattern the root exists to retire. It is tolerated only because ~25,000 lines of window code-behind are constructed by Avalonia's lifetime and by XAML, neither of which can pass constructor arguments.
  * **New code** takes dependencies as **constructor parameters**. It must never read `Current`.
  * **Legacy window code-behind** may read `Current` until its view-model is extracted.
  * `Current` is **deleted** at the end of the view-model extraction phase.
  * `ArchitectureRuleTests.ServiceLocatorUsageDoesNotIncrease` holds the line: the count may fall, never rise. Raise the baseline **only** when wiring an existing legacy window, and lower it whenever one is migrated.

* **The seams, and why each exists.** A seam is added when it removes a reason a view-model cannot be constructed in a test — not for symmetry.

  | Seam | Interface | Substitutes |
  | :--- | :--- | :--- |
  | `SEAM_01` | `IProjectStore` | the real filesystem, so "what does the UI do when the save fails" is testable |
  | `SEAM_02` | `IClock` | wall time, so the 24h update throttle and the 14-day log trim are testable without sleeping |
  | `SEAM_03` | `IUserNotifier` / `IActiveWindowProvider` | a live Avalonia `Window`, so a view-model needs no dispatcher |
  | `SEAM_04` | `IFilePickerService` | native OS dialogs, so Save/Open Project can be regression-tested |

  ⚠️ `FileProjectStore` is a **forward**, not a second implementation. The atomic-write protocol, the `.bak` cascade and the amputation rule stay in `ProjectStore`/`ProjectSerializer` under spec 06. `05_SYSTEM_LIFECYCLE_STORAGE.md` §4c is explicit: *never reimplement this sequence at a call site*.

---

## 2. Fault Tiers — Silence Is Not An Option  {#COMP-FAULTS}

* **`FAULTTIER_01` — the measured problem.** An audit of `src/` counted **917 catch blocks in ~90,000 lines** (one per 98), of which **368** are bare `catch (Exception)`, **391** sites carry a "best-effort"/"ignore" comment, and **87** have a literally empty body — **59 of those with no comment at all**. The dominant shape was `catch (Exception ex) { RuntimeLog.Fail("AREA", ex); }`: a log line the user will never open, followed by the program continuing as though nothing happened.

  A failed thumbnail, a failed ffprobe and a failed settings write all presented identically to the person using the app — **as nothing happening**. The user is left to guess whether they mis-clicked.

* **Every catch answers one question:** *what does the person in front of this window need to know?* There are exactly three answers, and no fourth door:

  | Tier | Bar for using it | What the user sees |
  | :--- | :--- | :--- |
  | `Recoverable` | the user's **outcome is unchanged** — a retry worked, a documented default took over | nothing; DEBUG log only |
  | `Degraded` | something perceptible stopped; the session is intact | a `FloatingNotice` naming **what stopped AND what still works** |
  | `Fatal` | the thing they asked for cannot happen, or their work is at risk | a modal dialog with the message and the log path |

  ⚠️ The bar for `Recoverable` is "the outcome is unchanged", **not** "we kept running". If the feature the user asked for did not happen, it is `Degraded`, however gracefully the code coped.

* **`OperationCanceledException` is re-thrown by `GuardAsync`, never reported.** A cancel is the user getting what they asked for. Reporting it as a fault is how a Cancel button ends up showing an error pill.

* **`FAULTSTORM_01` — the reporter needs its own flood control.** Degraded faults are raised from worker threads, and a failing per-frame operation raises one at frame rate. `FloatingNotice` dedupes identical *text* inside 1.4s (F4), but a message embedding a changing value defeats that. The sink gates on `(Tier, Area, UserMessage)` for **8s** (degraded) and **60s** (fatal — a repeat modal is a trap the user cannot click out of). **A suppressed repeat still reaches the log**, so nothing is lost for diagnosis. The gate dictionary is bounded at 256 entries because message text embedding a filename mints a new key every time.

* **`UserFacingFaultSink` must never throw.** A reporter that can fault is a reporter that call sites wrap in `try { } catch { }` — the exact shape being retired. Its outermost guard writes through `RuntimeLog.EmergencyWrite` and returns.

* **Migration is a ratchet, not a sweep.** 59 unexplained empty catches cannot be triaged correctly in one change; each needs a human decision about tier. `ArchitectureRuleTests.UnexplainedEmptyCatchBlocksDoNotIncrease` is green today at that baseline and can only get stricter. **A permanently red test gets deleted, so no rule here starts red.**

---

## 3. Executable Rules — Tests Instead Of Paragraphs  {#COMP-ARCHTEST}

* **`ARCHTEST_01` — why this exists.** `docs/` holds ~2,000 lines of specification, and most of it is a post-mortem diary: `DOUBLEFIRE_01` ("invisible to reading"), `SLIDER_09` ("invisible in code review"), `QUALITY_04` ("looked missing rather than broken"), `SEEKSTORM_01` (310 seeks in 1.74s). Each was found by a human running the app, sometimes over several diagnosis cycles, then fenced off with a paragraph.

  **A paragraph only works if the next person reads it. A test works whether they do or not.**

* **Entry criterion for a rule here:** a machine can check it and a reviewer reliably cannot. Rules requiring judgement stay in prose.

* **The rules run on source TEXT, deliberately** — every defect they catch is something the compiler is happy with. They strip comments and string literals first (`BlankCommentsAndStrings`), because this codebase documents its rules by quoting the offending pattern; without that, the docs trip the tests that enforce them.

* **Current rules and their standing:**

  | Rule | Enforces | Standing |
  | :--- | :--- | :--- |
  | `NoControlCarriesBothCommandAndClick` | `DOUBLEFIRE_01` (04 §4) | **clean — 0** |
  | `NoRawHexColoursInSharedStyling` | Invariant #5 (04 §1) | **clean — 0** (10 fixed; see §4) |
  | `ZoompanFilterIsNeverEmitted` | Invariant #4 | **clean — 0** |
  | `UnexplainedEmptyCatchBlocksDoNotIncrease` | `FAULTTIER_01` | ratchet, baseline **59** |
  | `EveryProductionSourceFileCarriesTheSpecContract` | `SPEC_GOVERNANCE.md` §4 | **clean — 0** (121 fixed; see §4) |
  | `BlockingWaitsOnAsyncCodeDoNotIncrease` | `ASYNCUI_01` | ratchet, baseline **5** |
  | `AsyncVoidMethodsDoNotIncrease` | `ASYNCUI_02` | ratchet, baseline **31** |
  | `ServiceLocatorUsageDoesNotIncrease` | `COMPOSITION_02` | ratchet, baseline **0** |
  | `EveryDevCmdSentinelStillResolves` | `SYS-DEVBUILD` | **clean — 133** |

* **Sentinel or test?** When a fix earns a `CHECK_TAG` in `dev.cmd`, ask whether it could be a test instead. **A sentinel proves a fix has not been DELETED; a test proves it has not been BROKEN.** Prefer the test. Keep the sentinel when the fix is a configuration value or a comment-documented ordering that no assertion can see.

* **`ASYNCUI_01` / `ASYNCUI_02`.** 5 blocking waits (`.Result` / `.Wait()` / `GetAwaiter().GetResult()`) and 31 `async void` methods, against 106 `Dispatcher.UIThread` call sites. On the UI thread a blocking wait is a deadlock of exactly the shape `SEEKSTORM_01` describes: the UI thread waiting on work that needs the UI thread. An `async void` that throws bypasses every catch in the stack and lands in `AppDomain.UnhandledException` — the process goes down from a background continuation, with nothing on screen. An `async void` that survives review must be an event handler bound directly to an Avalonia event, **and its whole body must sit inside one try/catch reporting through `IFaultSink`**.

---

## 4. Findings Closed By This Specification  {#COMP-FINDINGS}

* **`SPEC_GOVERNANCE.md` §4 was unenforced: 121 of 188 source files carried no `[SPEC CONTRACT]` sentinel** — 64% of the codebase was invisible to the routing protocol that governs it. All 188 now carry it, mapped to the governing spec via `docs/README.md` §3; co-governed files list every binding spec and say that reading one is not compliance.

* **`VERIFYHALT_01` — `VERIFY_PATCHES` was a no-op.** The subroutine built its `MISSING` list correctly and then returned. `MISSING` was **assigned in two places and read in none**, so all 133 fix sentinels were checked and the answer discarded. The mechanism `05` §4a calls "halts loudly if one is absent" could not halt, and could not be loud.
  Two defects, both closed: the unread variable, and `exit /b 1` inside a `call`ed subroutine returning from the *subroutine* rather than the script — so the call site now tests `if errorlevel 1`.
  ⚠️ `VERIFYLOOP_01` had already learned this exact lesson once, about two silently skipped entries, and its fix left the reporting half unwritten. **A guard that cannot fail is worse than no guard**, because it is trusted.

* **`STRIPCOST_01` was stale and nothing could say so.** Its tag no longer existed in `GranularSpeedEditorWindow.axaml.cs`. Not a revert: commit `ad0b7bd` deleted the per-slot `Image` path entirely and replaced it with `Controls/TimelineFilmstrip`, which draws through `DrawingContext.DrawImage` with explicit source and destination rects — no layout box to oversize, no 32768px bitmap for Skia to rasterise. The defect is structurally unreachable, so the sentinel is **retired** with a note, following the `WIZPROGRESS_01` precedent.

* **Invariant #5 had 10 live violations**, all raw hex in shared styling:
  * Six scrims (`#E6000000` ×4, `#F0000000`, `#90000000`) across five windows, each window keeping its own copy of "dim what is behind this" with nothing enforcing that the four identical ones stayed identical. Now `AppScrimSoftBrush` / `AppScrimBrush` / `AppScrimStrongBrush`, deliberately **theme-invariant** like the existing `AppOverlayBrush`: a scrim's job is to push content away from the eye, and on a light ground a light scrim does not do that. Pick by intent — *soft* (flyout open, context still legible), *base* (blocking operation owns the window), *strong* (a rebuild is discarding what is behind it).
  * Four `ZOOMCARD_01` states in the Granular editor's "How should the zoom arrive?" dialog (`#222234` rest, `#181824` bullet well, `#2f2f45` hover, `#3a2b48` checked) — **dark-theme values in a suite that ships a Light variant**, so in Light mode the dialog rendered as a block of near-black cards. Invisible to anyone who never switched theme. Now `AppZoomCard*Brush`, defined in both `ThemeDictionaries`. The Light `checked` state is a desaturated tint because the card border already carries the full accent, and two saturated accents stacked read as a rendering fault rather than a selection.

* **`SIGNMANDATE_01` / `UPDATETRUST_02` — the signing gap.** See `05_SYSTEM_LIFECYCLE_STORAGE.md` §5 (SYS-SIGNING), amended by this change.
