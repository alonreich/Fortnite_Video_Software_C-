# SPECIFICATION 09: DISTRIBUTION, UPDATE SIZE & REPOSITORY WEIGHT

## Code Mini-Map: Bound Source Files & Symbols

> **⚠ CO-GOVERNED rows are bound by EVERY spec listed on them.** Reading only this one is not compliance (`SPEC_GOVERNANCE.md` §2).

| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `src/FreeVideoStudio.Core/Infrastructure/RuntimePayloadManifest.cs` | `RuntimePayloadManifest` | `FromFolder`, `Read`, `Write`, `SYS-PAYLOADSPLIT` | Runtime fingerprint. |
| `src/FreeVideoStudio.App/Services/UpdateService.cs` | `UpdateService` | `AppOnlyAssetName`, `RuntimeAlreadyMatchesAsync`, `SYS-PAYLOADSPLIT` | Which package to download. **⚠ CO-GOVERNED BY: 05** |
| `build/FvsBuild/GitHubReleasePublisher.cs` | `GitHubReleasePublisher` | `Publish`, `ReleaseAssets`, `VerifyAssets`, `REBRAND_03` | Draft-verify-publish; creates the previous-brand download alias transiently. |
| `build/FvsBuild/Staging.cs` | `Staging` | `CreatePayloadZip`, `SYS-PAYLOADSPLIT` | Writes the manifest before zipping the payload; produces the app-only archive and manifest in `obj\ReleaseAssets`. |
| `.github/workflows/ci.yml` | CI | `SYS-CI`, `aot-publish` | Runs the ratchets; reports the app publish size. **⚠ CO-GOVERNED BY: 08** |
| `.github/workflows/lfs-guard.yml` | LFS guard | `SYS-REPOWEIGHT` | Proves LFS is real and stops new large files. |
| `.gitattributes` | EOL + LFS policy | `EOL_01`, `SYS-REPOWEIGHT` | What is stored where. |
| `Build.cmd` | Production Release Script | `dotnet run build\FvsBuild` | **STRICT AGENT BAN**: Deploys release assets and publishes publicly to GitHub Cloud. Never run by autonomous agents. **⚠ CO-GOVERNED BY: 05, GOV** |
| `dev_build.cmd` | Local Dev Build Harness | `dotnet run build\FvsBuild -- --dev` | Local compilation check and NativeAOT publish verification. Zero git tags, zero GitHub Cloud publishing. **⚠ CO-GOVERNED BY: 05, GOV** |

---

## 0. Build Harness Disclaimers & Agent Execution Rules  {#DIST-AGENTS}

> [!CAUTION]
> ### STRICT BAN ON `Build.cmd` FOR AI AGENTS
> **Autonomous AI agents are strictly forbidden from executing `.\Build.cmd` or `Build.cmd`.**
> `Build.cmd` is reserved exclusively for intentional human-triggered production deployments. Running `Build.cmd` compiles `FreeVideoStudio.exe` and **immediately deploys/replaces the public release on GitHub Cloud**, making the resulting binary live and downloadable to all end users.
>
> **For all local compilation checks and build verification, agents MUST use `.\dev_build.cmd`:**
> * `.\dev_build.cmd` passes the `--dev` flag to `FvsBuild`.
> * It performs local compilation only.
> * It never creates git release tags and **never touches GitHub or publishes to the cloud**.
> * Testing compilation with `.\dev_build.cmd` is permitted; running `.\Build.cmd` is never permitted under any circumstances.

---

## Product and build identity  {#DIST-IDENTITY}

| Scope | Canonical identity |
| :--- | :--- |
| Display name and Win32 product metadata | `Free Video Studio` |
| Install, data and media folder names | `FreeVideoStudio` — never the spaced display name (`05` SYS-NOSPACE) |
| Solution | `FreeVideoStudio.sln` |
| App project | `src/FreeVideoStudio.App/FreeVideoStudio.App.csproj` |
| App assembly / package ID / manifest identity | `FreeVideoStudio` |
| App C# and XAML root namespace | `FreeVideoStudio.App` |
| Core project | `src/FreeVideoStudio.Core/FreeVideoStudio.Core.csproj` |
| Core assembly / package ID / root namespace | `FreeVideoStudio.Core` |
| Test projects and assemblies | `FreeVideoStudio.App.Tests`, `FreeVideoStudio.Core.Tests` |
| Standalone installer output | `compiled/FreeVideoStudio.exe` |
| Raw payload executable and installed executable | `FreeVideoStudio.exe` |
| Embedded payload resource | `FreeVideoStudio.App.payload.zip` (the explicit app root namespace is retained) |
| App-only archive | `obj/ReleaseAssets/FreeVideoStudio.App.update.zip`, containing a signed compact installer named `FreeVideoStudio.exe` |
| Runtime manifest | `runtime.manifest.json` inside the payload and under `obj/ReleaseAssets` |

The internal tool names `FvsBuild` and `FvsVerify` are unchanged. The GitHub repository is `alonreich/Free_Video_Studio`; GitHub redirects the previous repository address, so old clients still reach `releases/latest`. Game-specific crop profiles and recording-discovery folders remain gameplay identifiers, not product branding. Historical storage names are compatibility data in `LegacyAppDataNames.txt` (`05` SYS-REBRAND). The full old→new map and update flow are in `REBRAND_MIGRATION.md`.

Run the local verification gates from the repository root:

```powershell
.\dev_build.cmd
dotnet build FreeVideoStudio.sln -c Release -warnaserror
dotnet test tests/FreeVideoStudio.Core.Tests/FreeVideoStudio.Core.Tests.csproj
dotnet test tests/FreeVideoStudio.App.Tests/FreeVideoStudio.App.Tests.csproj
dotnet run --project build/FvsVerify/FvsVerify.csproj -v q
dotnet test tests/FreeVideoStudio.Core.Tests/FreeVideoStudio.Core.Tests.csproj --filter RebrandTests
```

`RebrandTests.PreviousBrandNameAppearsOnlyInTheMigrationAllowList` fails if the previous product name appears outside `LegacyAppDataNames.txt` and `REBRAND_MIGRATION.md`. Project GUIDs stay unchanged; solution paths, project references, source-generated serializer types, XAML namespaces and sentinel paths use the renamed projects. `ProductionNamespacesUseTheProductRoot` enforces production namespace declarations; `AppDataPathsTests` guards migration behavior. Build output must contain only the standalone executable, with release sidecars under `obj/ReleaseAssets`.

---

## 1. The Measured Problem  {#DIST-PROBLEM}

Numbers taken on 2026-09-21, against `arch/04-mvvm`:

| Thing | Size | What it means |
| :--- | ---: | :--- |
| `compiled/FreeVideoStudio.exe` | **322 MB** | what a user downloads to install |
| `binaries/` (FFmpeg + libmpv) | 368 MB | of which `avcodec-62.dll` alone is 97 MB |
| `mp3/` + `meme/` in the repo (`meme/` replaced `mp4/` + `jpeg/`, MEMEFOLDER_01) | 269 MB | starter media, committed |
| `.git` | **2.7 GB** | a fresh clone |

Two separate defects hide in that table, and they need separate fixes.

* **Every patch costs 322 MB.** `UpdateService` fetches one release asset — the whole installer —
  and reinstalls. A one-line typo fix therefore reships FFmpeg. The download timeout is thirty
  minutes, which is an admission of how long this takes.

  ⚠️ The damage is not bandwidth, it is **delivery**. On a metered or slow connection the rational
  response is to turn updates off, and a user who has turned updates off does not receive the next
  fix either. Every shipped fix becomes a fix most users never get.

* **`git-lfs` is declared and was not installed.** `.gitattributes` routes `*.dll`, `*.exe`,
  `*.zip`, `*.mp3`, `*.mp4` through LFS. On a machine without `git-lfs` that declaration does
  nothing except break: `git status` in this working tree failed with
  `git-lfs filter-process: 1: git-lfs: not found`. A clone in that state gets pointer files, and
  the build fails later with a missing codec rather than with the real reason.

---

## 2. Why The Binary Is 322 MB  {#DIST-WHYBIG}

Invariant #1 — *Single Binary Executable Mandate, zero loose companion assets* — is the direct
cause. `payload.zip` (FFmpeg, libmpv, starter media) is embedded as a managed resource in a
NativeAOT executable and extracted to the install folder on first run.

⚠️ **The mandate is worth re-examining, and this spec does not pretend otherwise.** It buys "one
file on disk", a property no user of a video editor has asked for. It costs: a 322 MB download, a
322 MB re-download per patch, an extract-on-first-run step with its own failure modes, the ban on a
DI container (`COMPOSITION_01` cites `TrimMode=full` as the reason), and hand-written AOT-safe JSON
(`PROJ-AOT`). Four architectural taxes for one cosmetic win.

**This spec does not repeal it.** Repealing it is a product decision with a migration behind it —
installer, upgrade path, signing story. What this spec does is remove the part of the cost that
recurs: the *repeat* download.

---

## 3. The Split: One Runtime, Many App Patches  {#DIST-SPLIT}

* **`SYS-PAYLOADSPLIT` — a release may publish two packages and one tiny sidecar.**

  | Asset | Contents | When it is used |
  | :--- | :--- | :--- |
  | `FreeVideoStudio.exe` | everything (322 MB) | first install, or the runtime changed |
  | `FreeVideoStudio.App.update.zip` | the application only | the runtime already matches |
  | `runtime.manifest.json` | a fingerprint, a few hundred bytes | always read first |

* **The fingerprint includes content hashes of reusable payload files.** It is computed during
  packaging, over ordinal-sorted paths, lengths and SHA-256 hashes. Application executables,
  manifests and debug symbols are excluded; codec libraries and starter assets are included.
  An equal-size binary or asset change therefore selects the full installer. Startup checks read
  the stored sidecar; the user-approved update also verifies installed file bytes before reuse.
* **⚠️ EVERY UNCERTAINTY RESOLVES TO THE BIG DOWNLOAD.** `RuntimeAlreadyMatchesAsync` returns true
  only when the release published a small package AND advertised a fingerprint AND it matches what
  is installed. No small package, no sidecar, an unreadable local manifest, a network failure, a
  cancel, or a genuine mismatch — all return false.

  The asymmetry is the whole design. Wrongly choosing the big download costs bandwidth. Wrongly
  choosing the small one installs an application against codec binaries it was not built for, and
  that fails **at export time, on the user's machine, after they have done the work**.

* **The fingerprint decides WHAT to fetch and never WHETHER TO TRUST IT.** Whatever is downloaded
  is still SHA-256 verified against the release digest and still Authenticode-checked
  (`05` §5 SYS-SIGNING, `UPDATETRUST_02`). A manifest is a hint, and hints are not a security
  boundary.

* **Current packaging behavior.** `Staging.CreatePayloadZip` writes `runtime.manifest.json` into the staging folder **before** building `payload.zip`, so fresh installs receive the fingerprint. It also writes `obj/ReleaseAssets/runtime.manifest.json` and builds `obj/ReleaseAssets/FreeVideoStudio.App.update.zip` containing the signed compact installer. These artifacts stay outside `compiled/`, which contains only the standalone installer.

* **Operational compact updates.** `FromFolder` excludes the root application and uninstaller.
  Before selecting a compact update, the updater verifies the installed file manifest, requires
  the canonical install location, a published package digest and a matching runtime fingerprint.
  It fetches the selected asset and verifies its digest. The archive must contain exactly one
  installer executable, which is Authenticode-checked before launch. The installer embeds the
  new complete manifest and refuses to reuse any installed file with different bytes.
* **Build ordering.** Publish and sign the raw app; stage dependencies; generate manifests and
  the full payload; publish/sign the full installer; replace the embedded payload with the app
  and manifests only; publish/sign the compact installer; archive it; restore the full payload.
  Both installers use the same source and transactional worker. `compiled` still contains only
  the full standalone executable; sidecars live in `obj/ReleaseAssets`. The legacy filename alias is
  never staged (REBRAND_03): `Staging` deletes any stale copy, and `GitHubReleasePublisher` creates it in
  a private temp folder for the upload only and deletes it afterwards.
* **Old updater bridge (REBRAND_03).** The legacy executable filename, taken from `LegacyProductIdentity`,
  remains a release asset containing identical bytes to the full installer (created transiently at publish time). This lets old
  clients discover the rebrand without maintaining a separate app. Do not remove this alias
  until dropping automatic migration from those released clients is an explicit product decision.
* **Publication transaction.** Upload all four assets to a draft, verify every asset by name and
  SHA-256, then publish it as latest. Failed uploads stay unpublished. Existing releases and tags
  are retained, supporting interrupted downloads and recovery; publication never deletes them first.

---

## 4. Repository Weight  {#DIST-REPOWEIGHT}

* **`SYS-REPOWEIGHT` — LFS is enforced, not just declared.** `.github/workflows/lfs-guard.yml`
  asserts three things on every push: `git lfs version` succeeds, no tracked file is still a
  pointer after checkout, and no file over 5 MB is committed outside LFS.

  ⚠️ The third check is the one that matters long-term. LFS only helps going FORWARD; a history
  rewrite cannot stop the next large file, and this can.

* **The 2.7 GB is history, and history is the user's decision.** The media in `mp3/`, `mp4/` and
  `jpeg/` (now merged into `meme/`, MEMEFOLDER_01) was committed before LFS was configured, so the blobs are in every clone forever. Fixing
  it means rewriting history, which changes every commit hash and breaks every existing clone,
  fork, branch and open PR.

  **This is deliberately not automated.** The runbook:

  1. Everyone pushes and merges outstanding work. A rewrite orphans anything not on the remote.
  2. `git clone --mirror` the repository and keep that copy untouched until the new one is proven.
  3. `git filter-repo --path mp3/ --path mp4/ --path jpeg/ --path meme/ --path binaries/ --invert-paths`
     (`filter-repo`, not `filter-branch` — the latter is slow and its author recommends against it).
  4. Re-add the media through LFS in a single fresh commit, or move it to release assets — the
     starter media is shipped in `payload.zip` and does not need to be in the source tree at all.
     `.gitattributes` now routes `meme/*.png`, `meme/*.jpg` and `meme/*.jpeg` through LFS as well
     (MEMEFOLDER_01); images elsewhere in the tree are deliberately not affected.
  5. `git push --force --all` and `--tags`, then everyone re-clones. Not "pulls" — **re-clones**.

  ⚠️ Expected result is a repository in the low hundreds of MB. ⚠️ Expected cost is that every
  existing clone must be discarded. Do not start this on a Friday.

---

## 5. What CI Watches  {#DIST-CIWATCH}

`SYS-CI`'s `aot-publish` job prints the fifteen largest files in the publish output and the total
size on every push to `main`.

⚠️ **It measures only the app's `dotnet publish` folder, NOT the user download.** CI never runs
`FvsBuild`, so no `payload.zip` exists and none is embedded (`EmbeddedResource Include="payload.zip"`
is conditional on the file existing). The printed total is the app alone — roughly what an app-only
patch would cost — while the 322 MB installer (FFmpeg, libmpv, starter media) is not measured
anywhere in CI. A number nobody prints is a number nobody defends; this one is only half printed.
