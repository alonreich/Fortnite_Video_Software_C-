# REBRAND RECORD: Fortnite Video Software → Free Video Studio  {#REBRAND}

> **STATUS: COMPLETE.** The product, every project, folder, file, namespace, installer, storage
> root, release asset and automatic file name is **Free Video Studio / `FreeVideoStudio`**.
> The previous name, **Fortnite Video Software** (`FortniteVideoSoftware`), survives in exactly
> two places, both on purpose:
>
> 1. `src/FreeVideoStudio.Core/Infrastructure/LegacyAppDataNames.txt` — the embedded migration
>    identity (`LegacyProductIdentity`). Without it an installed old-brand copy could not be found,
>    upgraded or cleaned up.
> 2. This file — the human record of the change.
>
> `RebrandTests.PreviousBrandNameAppearsOnlyInTheMigrationAllowList` (Core tests) fails the build
> if the old product name appears in any other source, test, build, script or doc file. Gameplay
> references to the *game* (crop profile `Fortnite.json`, `FORTNITEDEFAULT_02`, AI tracking prompts,
> `Videos\Highlights\<game>` discovery folders) are game identifiers, not branding, and stay.

## 1. Identity map  {#REBRAND-MAP}

| Item | Previous brand | Free Video Studio |
| :--- | :--- | :--- |
| Display name | Fortnite Video Software | Free Video Studio |
| Solution / projects | `FortniteVideoSoftware.*` | `FreeVideoStudio.sln`, `FreeVideoStudio.App`, `FreeVideoStudio.Core` (+ `.Tests`) |
| Installed executable | `FortniteVideoSoftware.exe` | `FreeVideoStudio.exe` (`compiled/FreeVideoStudio.exe`) |
| Install folder | `%ProgramFiles%\Fortnite Video Software` | `%ProgramFiles%\FreeVideoStudio` (was `%ProgramFiles%\Free Video Studio` until NOSPACE_01, `05` SYS-NOSPACE) |
| Per-user state | `%LOCALAPPDATA%` / `%APPDATA%` `\Fortnite Video Software`, `\FortniteVideoSoftware` | `%LOCALAPPDATA%\FreeVideoStudio`, `%APPDATA%\FreeVideoStudio` |
| Machine state (pre USERSCOPE_01) | `%ProgramData%\Fortnite Video Software` | none (per-user only) |
| Temp root | `%TEMP%\Fortnite_Video_Software` | `%TEMP%\FreeVideoStudio` |
| Main App export name | `Fortnite-Video-N.mp4` | `FreeVideoStudio-N.mp4` (editable, OUTNAME_01) |
| Video Merger export name | `Merged-Videos-N.mp4` | `Merged-Videos-N.mp4` (editable, OUTNAME_01) |
| Rescued render prefix | `Fortnite-Video-RECOVERED-` | `FreeVideoStudio-RECOVERED-` |
| GitHub repository | `alonreich/Fortnite_Video_Software_C-` | `alonreich/Free_Video_Studio` (GitHub redirects the old address) |
| Diagnostic report header | FORTNITE VIDEO SOFTWARE — DIAGNOSTIC REPORT | FREE VIDEO STUDIO — DIAGNOSTIC REPORT |

Internal tool names `FvsBuild`, `FvsVerify`, `.fvsproj` and `FVS_*` variables are abbreviations of
the new name and are unchanged.

## 2. What happens when an old-brand install updates  {#REBRAND-UPDATE}

Old-brand clients poll `releases/latest` of the old repository address. GitHub redirects it to
`alonreich/Free_Video_Studio`. The sequence is:

1. **Discovery (REBRAND_03, `09` DIST-SPLIT "Old updater bridge").** Every release also carries the
   full installer under the previous download name. `GitHubReleasePublisher` creates that alias in
   a private temp folder only for the upload and deletes it afterwards; it is never kept in `obj/`
   or the repository. The old client downloads it and starts it.
2. **Transactional install (`05` SYS-UPGRADE, UPGRADE_02…10).** `InstallDiscovery` finds the old
   Program Files folder by verified signature, `UpgradeInstallWorker` installs Free Video Studio,
   moves the old folder into a 30-day backup, and `UpgradeRegistration` replaces shortcuts, uninstall
   keys, Open-With and file-type registrations. Any failure rolls back to the old app intact.
3. **User data (`05` SYS-UPGRADE UPGRADE_04, SYS-REBRAND REBRAND_01).** Local and Roaming state is
   moved into `FreeVideoStudio` folders (settings, crop profiles, presets, recovery, UI state).
   If the transactional path did not run (manual install), `AppDataPaths` copies the legacy roots
   on first start. The machine root is copied once per user (USERSCOPE_01).
4. **Settings (`SettingsManager` schema 11).** Older settings files receive
   `MainOutputBaseName = "FreeVideoStudio"` and `MergerOutputBaseName = "Merged-Videos"`, so the
   first export after the update is `FreeVideoStudio-1.mp4`. Output folders are preserved.
5. **Residue removal (REBRAND_02, `LegacyResidueSweep`).** On every normal launch, once no
   old-brand install or process exists and no upgrade transaction is open:
   * legacy temp folders are deleted after their rescued renders (`*-RECOVERED-*.mp4`) are moved
     into `%TEMP%\FreeVideoStudio` and renamed `FreeVideoStudio-RECOVERED-…`;
   * a legacy per-user root is deleted only when every one of its files already exists in the new
     root; otherwise it is kept and retried next launch.
   The shared `%ProgramData%` legacy root is never deleted per user (other accounts may still need
   it); the uninstaller removes it together with all legacy user roots when the user chooses to
   remove data (`DeploymentFootprint.GetDirectoryPurgeTargets`).
6. **Backups.** Transaction backups under `FreeVideoStudioMigration` are pruned 30 days after a
   confirmed first launch.

## 3. Automatic file naming and save folders  {#REBRAND-OUTNAME}

Settings › **Output Files** edits, per tool:

* **Save folder** — `MainOutputDirectory` / `MergerOutputDirectory`. Empty = Windows Downloads
  (`OutputFolderResolver`, ISSUE_04).
* **File name** — `MainOutputBaseName` (default `FreeVideoStudio`) / `MergerOutputBaseName`
  (default `Merged-Videos`). Files are `<name>-1.mp4`, `<name>-2.mp4`, … using the first free
  number. `OutputFileNaming.Sanitize` removes path separators and invalid characters, reserved
  device names and trailing dots/spaces, and clamps to 80 characters; invalid input reverts to the
  default. Spec: `03` FFM-OUTNAME.

## 4. Local clean-up the build cannot do

Build artifacts created before the rebrand may still carry the old name on a developer machine.
They are build output, not source, and are safe to delete: `obj/` (including
`obj/ReleaseAssets/<previous download name>`; the next `dev_build.cmd` also removes it),
root `*.log` files, and root `ctx_*.txt` scratch files.

## 5. Verification  {#REBRAND-VERIFY}

```powershell
dotnet test tests/FreeVideoStudio.Core.Tests/FreeVideoStudio.Core.Tests.csproj --filter RebrandTests
dotnet run --project build/FvsVerify/FvsVerify.csproj -v q
```

Sentinels: `REBRAND_01` (AppDataPaths.cs), `REBRAND_02` (LegacyResidueSweep.cs),
`REBRAND_03` (GitHubReleasePublisher.cs), `OUTNAME_01` (OutputFileNaming.cs).
