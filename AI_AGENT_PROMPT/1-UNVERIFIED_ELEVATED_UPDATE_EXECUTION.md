> **STATUS: APPLIED 2026-09-19.** The fix described below is already in the tree. This file is
> retained as the rationale record for the change, NOT as an outstanding task. Do not re-run it.
> Verification (section 5) has NOT been executed — the test cases listed there are still outstanding.

# TASK SPECIFICATION: 1 - UNVERIFIED_ELEVATED_UPDATE_EXECUTION

## 1. TARGET CONTEXT & SCOPE
- File Path: `src/FortniteVideoSoftware.App/Services/UpdateService.cs`
- Target Range: Lines 417 to 506 (`DownloadVerifyLaunchAsync`), with supporting reads at lines 330-386 (`QueryLatestReleaseAsync`)
- Defect Classification: Data Corruption / Catastrophic Failure Mode (unverified code execution with elevation + path traversal)

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism:
  1. **Single trust anchor.** `release.DownloadUrl` and `release.Sha256Hex` are both parsed out of the SAME `api.github.com` JSON document (lines 368-383). The SHA-256 comparison at line 486 therefore proves only "these bytes match what that JSON claimed"; it proves nothing about publisher authenticity. Any party that can produce that response body (repo/account compromise, a CI token leak, a corporate TLS-terminating proxy, a mis-issued certificate) controls both the payload and the fingerprint that validates it. The verified artifact is then launched at line 502 with `UseShellExecute = true` and `--install --auto-update`, which the code's own comment states forces the preserve-settings answer to YES silently and triggers a UAC elevation. Net result: attacker-supplied code running as Administrator on the end user's machine with one consent click.
  2. **The verification primitive already exists and is not used.** `build/FvsBuild/CodeSigning.cs` Authenticode-signs `FortniteVideoSoftware.exe` (`signtool sign`, then `signtool verify /pa`, line 50) when `FVS_SIGN_PFX` is set. Nothing on the receiving side ever checks the downloaded file's signature, publisher subject, or certificate chain before executing it.
  3. **Unsanitized tag flows into a filesystem path.** Line 421 does `Path.Combine(Path.GetTempPath(), DownloadFolderRootName, release.Tag)` and line 424 creates it. `release.Tag` is raw `tag_name` from the same attacker-influenced JSON. The only gate is `DeploymentLifecycle.TryParseVersion` (DeploymentLifecycle.cs:356-372), which does `TrimStart('v','V')` then `TakeWhile(char.IsDigit(c) || c == '.')` — it validates a PREFIX and discards the remainder. `9.9.9\..\..\Microsoft\Windows\Start Menu\Programs\Startup` parses successfully as `9.9.9` and is then used verbatim as a directory name, so `Directory.CreateDirectory` + the `.part` write land outside the intended `%TEMP%\FVS_AutoUpdate\` root. Path.Combine does not reject `..` segments.
  4. **TOCTOU between verify and launch.** The hash is computed over `partPath` (line 481), the file is then renamed (line 491), and a DIFFERENT path (`finalPath`) is executed at line 502. Nothing re-verifies after the rename. In a directory the traversal above can place anywhere, the executed bytes are not provably the verified bytes.
- Target Pattern: Authenticode chain verification (`X509Certificate.CreateFromSignedFile` / WinVerifyTrust) pinned to the expected publisher subject + thumbprint, performed on the exact path that will be executed; strict allowlist validation of the download host; and a `release.Tag` -> filesystem-safe slug projection that rejects rather than truncates.

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior:
  - The "no published fingerprint => refuse" rule (lines 476-479) MUST remain. Adding signature verification does not make the hash check redundant; keep both, and keep the refusal when either is absent.
  - The strictly-greater version comparison (lines 128-142) and the "equal tag / older tag stay silent" false-positive suppression are documented, deliberate behavior. Do not relax them.
  - The skipped-tag memory (`SkippedTagFile`) compares the RAW tag string. If you introduce a sanitized slug, keep the raw tag for comparison and persistence; only the filesystem path may use the slug, or previously skipped releases will be re-offered.
  - The 45-second per-chunk stall guard (lines 455-463), the cancel-honouring `ThrowIfCancellationRequested` at line 494, and `TryDeleteFile(partPath)` on both the cancel and failure paths must survive.
  - `RuntimeLog.IsDevMode` and `AutoUpdateChecks` early-outs must keep short-circuiting before any network call.
  - Failure must stay silent-and-logged, never a thrown dialog on a background path — the class contract is that a broken suggestor can never break the app.
- Public Interface Parity: Maintain exact public signatures, return types, and exceptions unless explicitly instructed. `UpdateService` is `internal static`; `RunStartupCheckAsync` and `CheckManualAsync` signatures are consumed by MainWindow and the About tab and must not change.

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. Isolate the target execution path within lines 417-506.
2. Add a `SanitizeTagForPath(string tag)` helper. Accept ONLY `[A-Za-z0-9._-]`, reject (do not strip) any string containing a path separator, a `..` segment, a drive colon, or a reserved Windows device name; on rejection, log and abort the download. Use the sanitized value for `folder` at line 421 only. After building `folder`, assert `Path.GetFullPath(folder).StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), DownloadFolderRootName)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)` and abort if it does not.
3. In `QueryLatestReleaseAsync`, validate `browser_download_url` before returning it: the parsed `Uri` must be `https`, and its `Host` must be in an explicit allowlist (`github.com`, `objects.githubusercontent.com`, `release-assets.githubusercontent.com`). Anything else logs one line and returns null.
4. After the rename at line 491 and IMMEDIATELY BEFORE `Process.Start` at line 502, verify `finalPath`:
   - Re-hash `finalPath` (not `partPath`) and compare against `release.Sha256Hex` again, closing the rename TOCTOU.
   - Verify the Authenticode signature of `finalPath` and require that the signing certificate's thumbprint (or, at minimum, its Subject) matches a constant compiled into the app, matching the certificate `build/FvsBuild/CodeSigning.cs` signs with. On any mismatch, unverifiable chain, or absent signature: delete the file, log, show the existing failure dialog, and DO NOT execute.
   - If the project ships unsigned (`FVS_SIGN_PFX` unset, per CodeSigning.cs:18), this check must be a documented, explicit, compile-time opt-out constant — never a silent runtime fallback that passes when verification is simply unavailable.
5. Wire the preserved legacy behaviors into the new execution path: all new failure modes route through the same `catch (Exception ex)` block (lines 511+) so they produce the existing user-facing dialog and `RuntimeLog.Fail("UPDATE", ...)` line, and all delete `partPath`/`finalPath`.
6. Ensure deterministic error propagation and structured logging without silent swallows: every refusal logs exactly one `RuntimeLog.Fail("UPDATE", ...)` naming which gate rejected (host, tag, hash, signature), never a bare return.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: Zero compiler/linter warnings or type errors.
- Behavioral Validation:
  - Unit test `SanitizeTagForPath` against `v1.2.3`, `1.2.3`, `9.9.9\..\..\evil`, `9.9.9/../../evil`, `C:1.2.3`, `1.2.3 `, `CON`, and the empty string; assert traversal candidates are REJECTED, not truncated to `9.9.9`.
  - Integration test: point the probe at a local fixture serving a valid JSON with (a) a non-github host, (b) a correct hash but an unsigned/wrong-signer binary, (c) a fully valid signed binary. Assert cases (a) and (b) never reach `Process.Start` and leave no file on disk, and case (c) still installs.
  - Regression test: the existing "no digest => refuse", "equal version => silent", and "cancel mid-download => nothing installed" behaviors still pass.
- Performance Check: Verified elimination of lock contention, allocation spikes, or thread blocks — the added hash/signature passes run off the UI thread and must not add a synchronous block to the dispatcher.
