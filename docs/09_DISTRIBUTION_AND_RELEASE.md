# SPECIFICATION 09: DISTRIBUTION, UPDATE SIZE & REPOSITORY WEIGHT

## Code Mini-Map: Bound Source Files & Symbols

> **⚠ CO-GOVERNED rows are bound by EVERY spec listed on them.** Reading only this one is not compliance (`SPEC_GOVERNANCE.md` §2).

| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `src/FortniteVideoSoftware.Core/Infrastructure/RuntimePayloadManifest.cs` | `RuntimePayloadManifest` | `FromFolder`, `Read`, `Write`, `SYS-PAYLOADSPLIT` | Runtime fingerprint. |
| `src/FortniteVideoSoftware.App/Services/UpdateService.cs` | `UpdateService` | `AppOnlyAssetName`, `RuntimeAlreadyMatchesAsync`, `SYS-PAYLOADSPLIT` | Which package to download. **⚠ CO-GOVERNED BY: 05** |
| `build/FvsBuild/Staging.cs` | `Staging` | `CreatePayloadZip`, `SYS-PAYLOADSPLIT` | Writes the manifest into the payload and beside `compiled\`. |
| `.github/workflows/ci.yml` | CI | `SYS-CI`, `aot-publish` | Runs the ratchets; reports payload size. |
| `.github/workflows/lfs-guard.yml` | LFS guard | `SYS-REPOWEIGHT` | Proves LFS is real and stops new large files. |
| `.gitattributes` | EOL + LFS policy | `EOL_01`, `SYS-REPOWEIGHT` | What is stored where. |

---

## 1. The Measured Problem  {#DIST-PROBLEM}

Numbers taken on 2026-09-21, against `arch/04-mvvm`:

| Thing | Size | What it means |
| :--- | ---: | :--- |
| `compiled/FortniteVideoSoftware.exe` | **322 MB** | what a user downloads to install |
| `binaries/` (FFmpeg + libmpv) | 368 MB | of which `avcodec-62.dll` alone is 97 MB |
| `mp3/` + `mp4/` + `jpeg/` in the repo | 269 MB | starter media, committed |
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
  | `FortniteVideoSoftware.exe` | everything (322 MB) | first install, or the runtime changed |
  | `FortniteVideoSoftware.App.update.zip` | the application only | the runtime already matches |
  | `runtime.manifest.json` | a fingerprint, a few hundred bytes | always read first |

* **The fingerprint is names and sizes, not content hashes.** `RuntimePayloadManifest.FromFolder`
  lists every `.dll`/`.exe`/`.com`, sorts ordinally by relative path, and hashes
  `path:length` lines. Hashing 368 MB on every update check would be a worse bug than the one being
  fixed; two different FFmpeg builds do not coincidentally keep every file at the same byte length.

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

* **A release that publishes only the installer behaves exactly as before.** That is deliberate:
  the updater change ships ahead of the release-pipeline change that starts producing the small
  package, and does nothing until one appears.

* **Open work.** The build writes `compiled\runtime.manifest.json` and stages a copy beside the
  installed binaries. Producing and uploading `FortniteVideoSoftware.App.update.zip` itself is a
  release-pipeline change in `GitHubReleasePublisher` and is **not yet done** — until it is, the
  consumer side is inert, correct, and tested.

---

## 4. Repository Weight  {#DIST-REPOWEIGHT}

* **`SYS-REPOWEIGHT` — LFS is enforced, not just declared.** `.github/workflows/lfs-guard.yml`
  asserts three things on every push: `git lfs version` succeeds, no tracked file is still a
  pointer after checkout, and no file over 5 MB is committed outside LFS.

  ⚠️ The third check is the one that matters long-term. LFS only helps going FORWARD; a history
  rewrite cannot stop the next large file, and this can.

* **The 2.7 GB is history, and history is the user's decision.** The media in `mp3/`, `mp4/` and
  `jpeg/` was committed before LFS was configured, so the blobs are in every clone forever. Fixing
  it means rewriting history, which changes every commit hash and breaks every existing clone,
  fork, branch and open PR.

  **This is deliberately not automated.** The runbook:

  1. Everyone pushes and merges outstanding work. A rewrite orphans anything not on the remote.
  2. `git clone --mirror` the repository and keep that copy untouched until the new one is proven.
  3. `git filter-repo --path mp3/ --path mp4/ --path jpeg/ --path binaries/ --invert-paths`
     (`filter-repo`, not `filter-branch` — the latter is slow and its author recommends against it).
  4. Re-add the media through LFS in a single fresh commit, or move it to release assets — the
     starter media is shipped in `payload.zip` and does not need to be in the source tree at all.
  5. `git push --force --all` and `--tags`, then everyone re-clones. Not "pulls" — **re-clones**.

  ⚠️ Expected result is a repository in the low hundreds of MB. ⚠️ Expected cost is that every
  existing clone must be discarded. Do not start this on a Friday.

---

## 5. What CI Watches  {#DIST-CIWATCH}

`SYS-CI`'s `aot-publish` job prints the fifteen largest files in the publish output and the total
payload size on every push to `main`.

⚠️ That number is the one that decides what every user downloads for a one-line fix, and before this
it appeared nowhere — it was discovered by looking at the file on disk, months after it grew. A
number nobody prints is a number nobody defends.
