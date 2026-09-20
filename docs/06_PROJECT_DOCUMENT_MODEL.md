# SPECIFICATION 06: PROJECT DOCUMENT MODEL & SAVEABLE WORK

## Code Mini-Map: Bound Source Files & Symbols
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `src/FortniteVideoSoftware.Core/Project/ProjectDocument.cs` | `ProjectDocument`, `SourceClip`, `ProjectAudio`, `ProjectExport`, `SourceIntegrity` | `SchemaVersion`, `MinimumReadableSchemaVersion`, `BuildTimeline`, `CheckSource`, `EffectiveDurationMs`, `UnknownFields` | The saveable document |
| `src/FortniteVideoSoftware.Core/Project/ProjectSerializer.cs` | `ProjectSerializer` | `Write`, `Read`, `KnownKeys` | AOT-safe JSON mapping |
| `src/FortniteVideoSoftware.Core/Project/ProjectStore.cs` | `ProjectStore`, `ProjectIoResult` | `Save`, `Load`, `NormalizeExtension`, `BackupSuffix` | Disk persistence |
| `src/FortniteVideoSoftware.Core/Project/RecentProjects.cs` | `RecentProjects`, `RecentProject` | `Read`, `Touch`, `Prune`, `MaxEntries` | Recent list |
| ⚠ `src/FortniteVideoSoftware.Core/Media/OutputTimeline.cs` | `OutputTimeline` | `Create`, `Chunk`, `Cut`, `Insertion` | CO-GOVERNED by `01_TIMELINE_COORDINATE_MATH.md` |
| ⚠ `src/FortniteVideoSoftware.Core/Infrastructure/AtomicJsonFile.cs` | `AtomicJsonFile` | `ReadObject`, `WriteObject` | CO-GOVERNED by `05_SYSTEM_LIFECYCLE_STORAGE.md` |
| `src/FortniteVideoSoftware.App/FortniteVideoSoftware.App.csproj` | build configuration | `AOTSAFETY_01`, `SuppressTrimAnalysisWarnings`, `SuppressAotAnalysisWarnings` | Publish-time safety analysis |

---

## 1. Why This Domain Exists  {#PROJ-WHY}
Until this specification the application could not save work. The only state that reached disk was
`recovery_v2.json`, which answers *"the app died, what was on screen?"* — a crash artefact, not a
document. Closing the app deliberately destroyed the session: an hour of markers, cuts, speed ramps,
zooms and meme placements lived only in the live window.

A `.fvsproj` file is the user's work. It is the one artefact in this product whose loss cannot be
regenerated from anything else, which is why the rules below are stricter than elsewhere.

---

## 2. The Document Is The Inputs, Never The Outputs  {#PROJ-INPUTS}
`ProjectDocument` stores exactly the input surface of `OutputTimeline.Create` plus the settings that
decide how those chunks render. Nothing derived is ever written to the file.

* **PROJ_01 — Completeness test.** `BuildTimeline()` reconstructs the authoritative timeline from
  the document alone. If a field is needed to rebuild the finished video and is not in the document,
  that is a silent data-loss bug; if a field is not needed, it does not belong.
* ⚠ **NORTH STAR #2 APPLIES.** No cached `TotalOutputSeconds`. Storing a derived duration creates a
  second answer to "how long is the video", which goes stale the moment the maths is corrected in a
  later build — and is then believed over the real model. `SourceClip.DurationMs` is the one measured
  value stored, and it is a property of the FILE, not of the edit.
* ⚠ **NORTH STAR #3 APPLIES.** `ProjectAudio.MusicVolume` and `VideoVolume` are EXPORT mix levels and
  belong in the document. The master PREVIEW volume is a property of this machine's playback session
  and must never be stored — saving it would carry one machine's monitoring level into another
  machine's export, exactly the coupling that invariant forbids.
* **PROJ_04 — One way in.** `BuildTimeline()` is the only sanctioned route from a loaded project to a
  timeline. A screen that re-derives chunks from its own copies of these lists re-introduces the
  duplicated-maths defect `OutputTimeline`'s own header documents.

---

## 3. Schema Versioning & The Amputation Rule  {#PROJ-SCHEMA}
* **PROJ_02 — Two numbers.** `SchemaVersion` is what this build WRITES. `MinimumReadableSchemaVersion`
  is the oldest it can still READ. Bump `SchemaVersion` only when the MEANING of an existing field
  changes; adding a new optional field needs no bump, because the reader tolerates missing keys.
* **A file from the future is refused, in words.** A schema above `SchemaVersion` is rejected with a
  message naming both numbers and telling the user to update. Reading it on a best-effort basis would
  reinterpret fields whose meaning changed and hand back a montage they did not author.
* **PROJ_03 — THE AMPUTATION RULE (non-negotiable).** Unrecognised top-level keys are captured into
  `ProjectDocument.UnknownFields` and written back out verbatim. Without this, opening a v2 file in a
  v1 build and saving it silently deletes every v2 field. The user performed one ordinary
  open-and-save and lost work the newer build could have read. Unknown keys are emitted FIRST on
  write so a known key always wins a collision.
* **The reader is forgiving; the writer is explicit.** Every `Read*` helper absorbs a missing key, an
  explicit null and a wrong JSON type. Hand-edited and half-written files exist, and an exception
  thrown from deep inside a load is indistinguishable, to the user, from the app losing their work.
  A project that opens with one setting reset is worth infinitely more than a project that refuses
  to open. Malformed list entries (a meme with no path or no id, a cut with no end) are dropped, not
  carried as half-placements the user cannot see or delete.

---

## 4. No Reflection. Ever.  {#PROJ-AOT}
* **PROJ_02 — Hand-written mapping is mandatory.** The product ships NativeAOT with `TrimMode=full`.
  Reflection-based `System.Text.Json` compiles, passes in Debug, and throws `NotSupportedException`
  only in the published .exe on the user's machine. `ProjectSerializer` touches nothing but
  `JsonNode` / `JsonObject`. Source-generated `JsonSerializerContext` (as `Ipc/IpcProtocol.cs` uses)
  is the only acceptable alternative. `JsonSerializer.Serialize(document)` is forbidden here.
* **AOTSAFETY_01 — The analysers stay on.** `SuppressTrimAnalysisWarnings` and
  `SuppressAotAnalysisWarnings` are `false` in `FortniteVideoSoftware.App.csproj`, and `IL2104` is
  no longer in `NoWarn`. Setting them back to `true` does not make the app AOT-safe; it makes the app
  silent about not being AOT-safe. These are warnings, not errors — the build still succeeds. Each
  one is fixed or annotated at the call site with a reason. `IlcTrimMetadata` is a separate
  size/behaviour switch, is unrelated, and stays `false`.

### What the analysers caught the moment they were switched back on
Every item below is a REAL finding that was invisible while the suppressions were `true`. None was
fixed by muting it again — the rule is fix, or annotate one statement with a reason.

* **AOTSAFETY_02 — `JsonArray.Add<T>(T)` (13 call sites).** The generic overload carries
  `RequiresUnreferencedCode`/`RequiresDynamicCode` because `T` could be an arbitrary POCO. Passing a
  `JsonNode` never reflects, but C# picks the generic anyway: `JsonArray` implements
  `ICollection<JsonNode?>.Add` EXPLICITLY, so the safe non-generic overload is invisible on the type.
  `Infrastructure/AotJson.AddNode` casts to the interface and binds to the unannotated method.
  ⚠ Use `AddNode`, never a `#pragma`: the extension makes the safety PROVABLE, where a suppression
  merely asserts it and goes on hiding the next call — possibly one that really does pass a POCO.
* **AOTSAFETY_03 — `Marshal.SizeOf(Type)`** in `HardwareTelemetrySampler.GetMemUsage`. Asks the
  runtime to build marshalling code for a reflectively-known type, which does not exist after AOT
  compilation. `Marshal.SizeOf<T>()` is computed at compile time and yields the identical size.
* **AOTSAFETY_04 — `SettingsManager.Save` was on the reflection path.** It already had a
  source-generated `SettingsJsonContext` AND assigned it as `TypeInfoResolver`, yet still called
  `JsonSerializer.Serialize<TValue>(TValue, JsonSerializerOptions)` — an overload that stays
  annotated regardless of the resolver, because it cannot prove which resolver arrives at run time.
  Now passes the generated `JsonTypeInfo` directly (`IndentedContext.AppSettings`), cached in a
  static because a context allocates a full options graph and `Save()` runs on every change.
  This is the shape to copy: having a context is not the same as USING it.
* **AOTSAFETY_05 — `X509Certificate.CreateFromSignedFile` (SYSLIB0057).** The only suppression in the
  codebase, scoped to one statement with the reason recorded. The obsoletion points at
  `X509CertificateLoader`, which loads certificate FILES and has no equivalent for extracting an
  embedded signer certificate from a signed PE — which is what SYS-SIGNING needs. Revisit if .NET
  ships a replacement.

---

## 5. Persistence Protocol  {#PROJ-DISK}
* **PROJ_08 — Atomic or nothing.** Every write goes through `AtomicJsonFile.WriteObject`, per
  `05_SYSTEM_LIFECYCLE_STORAGE.md#SYS-ATOMICWRITE` ("not optional, and it is not per-caller"):
  GUID temp file in the target directory, `FileOptions.WriteThrough`, `Flush(flushToDisk: true)`,
  atomic `File.Move(overwrite: true)`. Never `File.WriteAllText`.
* **One generation of backup.** The previous file becomes `<name>.fvsproj.bak` BEFORE the new bytes
  are written — taken afterwards it would back up the save that just happened. The crop config keeps
  a five-tier cascade because it is machine state the user never sees; a project is different, and a
  folder holding five numbered copies of every montage is its own kind of damage. A zero-length live
  file is never promoted over a good backup. A failed backup never blocks a save.
* **Damaged live file falls back to the backup, and SAYS SO.** `Load` reports `loadedFromBackup`.
  A user shown yesterday's version without being told will keep editing and re-save over the good
  copy.
* **Failure is returned, never thrown.** A save that throws out of a close handler is how an app
  loses the very work the user was protecting. Callers render `ProjectIoResult.Error` into
  "could not save, here is why, your edits are still open" — and per
  `04_UI_UX_AVALONIA_SPEC.md#UI-SAFEGUARDS` (CROPUNSAVED_01) a failed save BLOCKS departure.
* **Extension is normalised on save.** A bare name would produce a file the shell cannot associate
  and the open dialog's filter will not show — which reads to the user as "my save vanished".
* ⚠ **THREADING.** All of `ProjectStore` and `RecentProjects` performs synchronous disk I/O and MUST
  NOT run on the Avalonia dispatcher (North Star #6). Call via `Task.Run`; marshal only the result
  back.

---

## 6. Source Integrity  {#PROJ-INTEGRITY}
* **PROJ_05 — Paths outlive the files they point at.** `CheckSource()` returns `Intact`, `Changed`,
  `Missing` or `Unknown` so the app can say *"that video has been replaced, your cuts may not line
  up"* instead of rendering markers against different footage.
* **Fingerprint, not hash.** Size plus last-write-time at whole-second resolution. Hashing a 4 GB
  capture on every open would cost more than the entire load. Sub-second drift is ignored: copying a
  file between filesystems perturbs it without the bytes differing, and a false warning teaches users
  to dismiss the real one.
* **An absent fingerprint is `Unknown`, not `Changed`.** Older projects have no fingerprint, and
  nagging about every one of them trains the warning away.
* **`Missing` never blocks opening.** The project loads so the user can relink.

---

## 7. Recent Projects  {#PROJ-RECENT}
* **PROJ_09 — Saving is only half the feature.** A user who can save but must hunt through Explorer
  has been given a filing chore, not a document model.
* **Capped at `MaxEntries = 10`** — the number a person can recognise in a menu. Longer, and it is a
  second file browser with worse sorting.
* **De-duplicated case-insensitively.** Windows paths differing only in case are the same file;
  showing both reads as the app being confused.
* **Missing files are FLAGGED, NOT DROPPED.** `RecentProject.Exists` lets the UI grey the row and
  explain. A project on an unplugged external drive is not a project the user deleted, and quietly
  removing the row is how a user concludes the app lost their montage. `Prune` runs only when the
  user asks.
* **A damaged recent list is cosmetic.** It must never stop the app starting, and never surface as an
  error to dismiss on every launch.

---

## 8. Open Work Bound To This Spec  {#PROJ-TODO}
The model and its persistence exist and are unit-tested
(`tests/FortniteVideoSoftware.Core.Tests/ProjectDocumentTests.cs`). NOT yet done, and required before
the feature is user-visible:

1. `MainWindow` command wiring: New / Open / Save / Save As / Recent, with the dirty flag and the
   unsaved-changes prompt modelled on CROPUNSAVED_01.
2. `RecoveryManager` demoted to autosave OF THIS DOCUMENT rather than a parallel state format.
3. `.fvsproj` shell association and icon (`ShellFileAssociation.cs`), plus open-with launch.
4. Title-bar dirty indicator, per `04_UI_UX_AVALONIA_SPEC.md#UI-SETTINGS-ABOUT` title formatting.
