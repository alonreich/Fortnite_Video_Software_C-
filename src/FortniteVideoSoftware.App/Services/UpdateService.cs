// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using FortniteVideoSoftware.App.Controls;
using FortniteVideoSoftware.App.Infrastructure;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.App.Services;

/// <summary>
/// AUTO-UPDATE SUGGESTOR — the whole feature behind the "Automatically check for updates"
/// checkbox in Settings.
///
/// HOW IT PROBES (and how it avoids false positives):
/// * FvsBuild publishes exactly ONE GitHub release, always named "latest", always carrying a
///   single FortniteVideoSoftware.exe whose SHA-256 digest GitHub serves in the release JSON
///   (GitHubReleasePublisher verifies the upload with that same digest). The probe therefore
///   reads the releases/latest API endpoint — the "static location" that always describes the
///   newest build — and never guesses from file dates or sizes.
/// * Drafts and prereleases are excluded by the /latest endpoint itself and re-checked here as
///   defense in depth.
/// * Versions are compared as parsed Version objects, STRICTLY greater. An equal tag ("same
///   version already installed") and an older tag (user runs a newer build than is published)
///   both stay perfectly silent.
/// * Any surprise — unparsable tag, missing asset, HTTP error, timeout, offline machine — logs
///   one line and stays silent. The suggestor must never interrupt a user over its own problems.
///
/// HOW IT NAGS (and how it stops):
/// * At most ONE probe per 24h (UiStateStore timestamp inside the preserved ProgramData root).
/// * "No. I like this version." = dismissed; the same release may be offered again on a later
///   start (the user said not-now, not never).
/// * "Skip this version" remembers THE TAG in UiStateStore; that exact release is never offered
///   again, any strictly newer one is.
/// * "Never tell me about updates again" writes AutoUpdateChecks=false into settings.json, so
///   the Settings checkbox always tells the truth about the feature's state.
///
/// HOW IT INSTALLS:
/// * Downloads to %TEMP%\FVS_AutoUpdate\&lt;tag&gt;\, verifies the SHA-256 against GitHub's
///   published digest and REFUSES to run anything that does not match.
/// * Launches the downloaded exe with "--install --auto-update". DeploymentLifecycle reads that
///   flag and forces the "preserve your settings?" answer to YES without showing the question,
///   then relaunches the app. The manual double-click install path is untouched and still asks.
/// </summary>
internal static class UpdateService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/alonreich/Fortnite_Video_Software_C-/releases/latest";
    private const string ExpectedAssetName = "FortniteVideoSoftware.exe";

    /// <summary>
    /// SYS-PAYLOADSPLIT — the app-only update package, when a release publishes one.
    ///
    /// <para>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// <b>WHY A SECOND ASSET EXISTS.</b> <see cref="ExpectedAssetName"/> is a 322 MB NativeAOT
    /// installer carrying FFmpeg and libmpv as an embedded payload. Downloading it to deliver a
    /// one-line fix costs every user 322 MB, over a 30-minute timeout, to reinstall codec DLLs that
    /// did not change. On a metered connection that is a reason to turn updates off — which turns
    /// every shipped fix into a fix most users never get.
    /// </para>
    ///
    /// <para>
    /// This asset carries the application only. It is used when, and only when, the runtime already
    /// installed on this machine is the one the release expects — proven by comparing
    /// <c>RuntimePayloadManifest</c> fingerprints. Anything else, including "I could not read the
    /// manifest", falls back to the full installer: a bigger download is always safe, a smaller one
    /// is not.
    /// </para>
    ///
    /// <para>⚠️ The size decision is the ONLY thing the fingerprint controls. Whatever is
    /// downloaded is still SHA-256 verified and still Authenticode-checked before it is run
    /// (UPDATETRUST_02). A fingerprint is a hint about what to fetch, never a reason to trust it.
    /// </para>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    private const string AppOnlyAssetName = "FortniteVideoSoftware.App.update.zip";

    /// <summary>
    /// SYS-PAYLOADSPLIT — the release's runtime fingerprint, published as a tiny sidecar asset so
    /// the updater can read it without downloading either package.
    /// </summary>
    private const string RuntimeManifestAssetName = "runtime.manifest.json";

    /// <summary>
    /// UPDATETRUST_01 — the ONLY hosts an update asset may be fetched from.
    ///
    /// <para>The asset URL used to be taken from the release JSON verbatim and handed straight to
    /// <c>HttpClient</c>. A response body that named any other host would have been fetched without
    /// comment, and since the SHA-256 that "verifies" the download comes out of that SAME document,
    /// nothing downstream would have objected either. Pinning the host removes the easiest half of
    /// that pairing: an attacker now has to be GitHub, not merely be believed by us.</para>
    ///
    /// <para>⚠️ This gates the URL AS PUBLISHED IN THE JSON. HttpClient still follows GitHub's
    /// redirect to its asset CDN without re-checking the hop, which is why the CDN hosts are listed
    /// too and why this is a defence-in-depth control rather than the primary one — the re-hash and
    /// the Authenticode check before <c>Process.Start</c> are what actually decide.</para>
    /// </summary>
    private static readonly string[] AllowedAssetHosts =
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com"
    };
    private const string UserAgent = "FortniteVideoSoftware-Updater";
    private const string DownloadFolderRootName = "FVS_AutoUpdate";
    private const string LastCheckFile = "update_last_check_utc.txt";
    private const string SkippedTagFile = "update_skipped_tag.txt";

    private static readonly TimeSpan StartupGracePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinimumIntervalBetweenChecks = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    private static readonly HttpClient Http = CreateHttpClient();
    private static int _checkInProgress;

    private sealed record UpdateRelease(
        string Tag,
        string DownloadUrl,
        string? Sha256Hex,
        long Size,
        string? ReleaseNotes,
        string? AppOnlyUrl = null,
        string? AppOnlySha256Hex = null,
        long AppOnlySize = 0,
        string? RuntimeManifestUrl = null)
    {
        /// <summary>SYS-PAYLOADSPLIT — true when this release published a small app-only package.</summary>
        public bool HasAppOnlyPackage => !string.IsNullOrWhiteSpace(AppOnlyUrl) && AppOnlySize > 0;

        /// <summary>What the user is about to spend, in the units they think in.</summary>
        public string DescribeDownloadSize(bool appOnly)
            => FortniteVideoSoftware.Core.Infrastructure.RuntimePayloadManifest.FormatBytes(
                   appOnly ? AppOnlySize : Size);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = DownloadTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// Entry point, hooked from MainWindow.Opened. Fire-and-forget by design; every failure is
    /// swallowed into RuntimeLog so a broken suggestor can never break the app itself.
    /// </summary>
    public static async Task RunStartupCheckAsync(Window owner)
    {
        // The master switch. OFF means: no network call, no prompt, no nag — ever.
        if (!SettingsManager.Instance.AutoUpdateChecks)
        {
            RuntimeLog.Info("UPDATE", "Update checks are disabled in Settings; staying silent.");
            return;
        }

        // dev.cmd runs with FVS_DEV_LOG_DIR set; a developer's machine must never be offered
        // a release probe against its own un-versioned local build.
        if (RuntimeLog.IsDevMode)
        {
            RuntimeLog.Info("UPDATE", "Dev mode detected; update check skipped.");
            return;
        }

        // Let the window settle first — the suggestor must never compete with startup work
        // or recovery prompts for the user's attention.
        try
        {
            await Task.Delay(StartupGracePeriod).ConfigureAwait(false);
        }
        catch (System.Exception swallowed4)
        {
            global::FortniteVideoSoftware.App.RuntimeLog.Swallowed(swallowed4);   // FAULTTIER_02 — no failure is silent.
            return;
        }

        bool ownerStillVisible = await Dispatcher.UIThread.InvokeAsync(() => owner.IsVisible);
        if (!ownerStillVisible) return;

        if (!ThrottlePermitsCheck()) return;

        if (Interlocked.CompareExchange(ref _checkInProgress, 1, 0) != 0)
        {
            RuntimeLog.Info("UPDATE", "Another update check is already in progress; skipping startup check.");
            return;
        }

        try
        {
            RuntimeLog.Info("UPDATE", "Running startup update probe against GitHub releases...");
            UpdateRelease? release = await QueryLatestReleaseAsync().ConfigureAwait(false);
            if (release is null) return;

            if (!DeploymentLifecycle.TryParseVersion(release.Tag, out Version remote) ||
                !TryGetLocalVersion(out Version local))
            {
                RuntimeLog.Fail("UPDATE", $"Could not compare versions (running build vs tag '{release.Tag}'); no prompt shown.");
                return;
            }

            // STRICTLY newer only. Equal ("already have it") and older ("running a newer build")
            // are the two false positives this feature must never produce.
            if (remote.CompareTo(local) <= 0)
            {
                RuntimeLog.Info("UPDATE", $"Already up to date (installed {local}, latest {remote}).");
                return;
            }

            RuntimeLog.Info("UPDATE", $"Newer version found: {remote} (installed {local}). Prompting user.");

            string skipped = UiStateStore.ReadText(SkippedTagFile).Trim();
            if (string.Equals(skipped, release.Tag, StringComparison.OrdinalIgnoreCase))
            {
                RuntimeLog.Info("UPDATE", $"Release {release.Tag} was skipped by the user; staying quiet until a newer one appears.");
                return;
            }

            // Same UI-thread marshalling pattern MainWindow uses (Post + completion source):
            // DispatcherOperation shapes differ per InvokeAsync overload, so we don't touch them.
            var choiceReady = new TaskCompletionSource<UpdateChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(async () =>
            {
                try { choiceReady.SetResult(await UpdateAvailableWindow.AskAsync(owner, local, release.Tag, release.ReleaseNotes)); }
                catch (Exception ex)
                {
                    choiceReady.SetException(ex);
                    global::FortniteVideoSoftware.App.RuntimeLog.Swallowed(ex);   // FAULTTIER_02 — no failure is silent.
                }
            });
            UpdateChoice choice = await choiceReady.Task.ConfigureAwait(false);

            switch (choice)
            {
                case UpdateChoice.SkipThisVersion:
                    UiStateStore.WriteText(SkippedTagFile, release.Tag);
                    RuntimeLog.Info("UPDATE", $"User skipped {release.Tag}. Only a strictly newer release will be offered again.");
                    break;

                case UpdateChoice.NeverTellMeAgain:
                    SettingsManager.Instance.AutoUpdateChecks = false;
                    SettingsManager.Save();
                    RuntimeLog.Info("UPDATE", "User chose 'never tell me about updates again' — Settings checkbox now reflects OFF.");
                    break;

                case UpdateChoice.UpdateNow:
                    await DownloadVerifyLaunchAsync(owner, release).ConfigureAwait(false);
                    break;

                // NotNow / Dismissed: nothing is stored; a later start may offer the same release again.
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UPDATE", $"Startup update check failed (staying silent): {ex.Message}");
        }
        finally
        {
            _ = Interlocked.Exchange(ref _checkInProgress, 0);
        }
    }

    /// <summary>
    /// Explicit manual check triggered on user demand (e.g. from the About tab or Help menu).
    /// Bypasses the 24-hour throttle and auto-update toggle since the user explicitly requested it.
    /// </summary>
    public static async Task CheckManualAsync(Window owner, Action<string>? statusCallback = null)
    {
        if (Interlocked.CompareExchange(ref _checkInProgress, 1, 0) != 0)
        {
            RuntimeLog.Info("UPDATE", "Manual update check requested while an update check is already in progress.");
            FloatingNotice.Warn(owner, "An update check is already in progress...");
            statusCallback?.Invoke("An update check is already in progress...");
            return;
        }

        try
        {
            FloatingNotice.Info(owner, "Checking GitHub for updates...");
            statusCallback?.Invoke("Checking GitHub for updates...");
            RuntimeLog.Info("UPDATE", "Manual update check initiated by user.");

            UpdateRelease? release = await QueryLatestReleaseAsync().ConfigureAwait(false);
            if (release is null)
            {
                statusCallback?.Invoke("Could not connect to GitHub or find release.");
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    NativeDialog.ShowError("Could not retrieve update information from GitHub.\r\nPlease check your network connection and try again.", "Update Check Failed");
                });
                return;
            }

            if (!DeploymentLifecycle.TryParseVersion(release.Tag, out Version remote) ||
                !TryGetLocalVersion(out Version local))
            {
                statusCallback?.Invoke($"Could not compare versions ({release.Tag}).");
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    NativeDialog.ShowError($"Could not determine version compatibility (installed build vs release tag '{release.Tag}').", "Update Check Failed");
                });
                return;
            }

            if (remote.CompareTo(local) <= 0)
            {
                statusCallback?.Invoke($"Up to date (v{local}).");
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    NativeDialog.ShowInfo($"You are already running the latest version of Fortnite Video Software (v{local}).");
                });
                return;
            }

            statusCallback?.Invoke($"Update available: {release.Tag}");

            var choiceReady = new TaskCompletionSource<UpdateChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(async () =>
            {
                try { choiceReady.SetResult(await UpdateAvailableWindow.AskAsync(owner, local, release.Tag, release.ReleaseNotes)); }
                catch (Exception ex)
                {
                    choiceReady.SetException(ex);
                    global::FortniteVideoSoftware.App.RuntimeLog.Swallowed(ex);   // FAULTTIER_02 — no failure is silent.
                }
            });
            UpdateChoice choice = await choiceReady.Task.ConfigureAwait(false);

            switch (choice)
            {
                case UpdateChoice.SkipThisVersion:
                    UiStateStore.WriteText(SkippedTagFile, release.Tag);
                    RuntimeLog.Info("UPDATE", $"User skipped {release.Tag} via manual check.");
                    statusCallback?.Invoke($"Skipped {release.Tag}");
                    break;

                case UpdateChoice.NeverTellMeAgain:
                    SettingsManager.Instance.AutoUpdateChecks = false;
                    SettingsManager.Save();
                    RuntimeLog.Info("UPDATE", "User disabled auto updates via prompt.");
                    statusCallback?.Invoke("Auto updates disabled in Settings.");
                    break;

                case UpdateChoice.UpdateNow:
                    statusCallback?.Invoke("Starting download...");
                    await DownloadVerifyLaunchAsync(owner, release).ConfigureAwait(false);
                    break;

                case UpdateChoice.NotNow:
                case UpdateChoice.Dismissed:
                    statusCallback?.Invoke("Update postponed.");
                    break;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UPDATE", $"Manual update check failed: {ex.Message}");
            statusCallback?.Invoke($"Check failed: {ex.Message}");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                NativeDialog.ShowError($"Update check failed: {ex.Message}", "Update Check Failed");
            });
        }
        finally
        {
            _ = Interlocked.Exchange(ref _checkInProgress, 0);
        }
    }

    public static string GetSkippedVersion()
    {
        try { return UiStateStore.ReadText(SkippedTagFile).Trim(); }
        catch (System.Exception swallowed6)
        {
            global::FortniteVideoSoftware.App.RuntimeLog.Swallowed(swallowed6);   // FAULTTIER_02 — no failure is silent.
            return string.Empty;
        }
    }

    public static void ClearSkippedVersion()
    {
        try
        {
            UiStateStore.WriteText(SkippedTagFile, string.Empty);
            RuntimeLog.Info("UPDATE", "Skipped version cleared by user.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UPDATE", $"Could not clear skipped version: {ex.Message}");
        }
    }

    /// <summary>At most one probe per 15m, so rapid restarts do not hammer GitHub rate limits.</summary>
    private static bool ThrottlePermitsCheck()
    {
        string last = UiStateStore.ReadText(LastCheckFile);
        if (DateTime.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime when) &&
            DateTime.UtcNow - when < MinimumIntervalBetweenChecks)
        {
            RuntimeLog.Info("UPDATE", $"Last update check was at {when:u} (throttled for {MinimumIntervalBetweenChecks.TotalMinutes:0}m); skipping startup check.");
            return false;
        }

        UiStateStore.WriteText(LastCheckFile, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return true;
    }

    private static async Task<UpdateRelease?> QueryLatestReleaseAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(ProbeTimeout);
            using var response = await Http.GetAsync(LatestReleaseApiUrl, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                RuntimeLog.Info("UPDATE", $"Release probe returned HTTP {(int)response.StatusCode}; staying silent.");
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var root = JsonNode.Parse(json)?.AsObject();

            string? tag = root?["tag_name"]?.GetValue<string>();
            string? releaseNotes = root?["body"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(tag))
            {
                RuntimeLog.Fail("UPDATE", "Release JSON carried no tag_name; staying silent.");
                return null;
            }

            // /releases/latest never returns drafts or prereleases; this is defense in depth.
            if (root?["draft"]?.GetValue<bool>() == true || root?["prerelease"]?.GetValue<bool>() == true)
            {
                RuntimeLog.Info("UPDATE", "Latest release is a draft/prerelease; staying silent.");
                return null;
            }

            JsonNode? asset = null;
            JsonNode? appOnlyAsset = null;
            string? runtimeManifestUrl = null;

            foreach (JsonNode? candidate in root?["assets"]?.AsArray() ?? [])
            {
                string? name = candidate?["name"]?.GetValue<string>();
                if (name is null) continue;

                if (name.Equals(ExpectedAssetName, StringComparison.OrdinalIgnoreCase))
                    asset = candidate;
                else if (name.Equals(AppOnlyAssetName, StringComparison.OrdinalIgnoreCase))
                    appOnlyAsset = candidate;          // SYS-PAYLOADSPLIT
                else if (name.Equals(RuntimeManifestAssetName, StringComparison.OrdinalIgnoreCase))
                    runtimeManifestUrl = candidate?["browser_download_url"]?.GetValue<string>();
            }

            string? url = asset?["browser_download_url"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(url))
            {
                RuntimeLog.Fail("UPDATE", $"Release {tag} carries no '{ExpectedAssetName}' asset; staying silent.");
                return null;
            }

            // UPDATETRUST_01 — refuse an asset URL that is not HTTPS to a pinned GitHub host.
            if (!IsAllowedAssetUrl(url!))
            {
                RuntimeLog.Fail("UPDATE", $"Release {tag} points its asset at an unexpected location; refusing to download it.");
                return null;
            }

            // UPDATETRUST_01 — the tag becomes a directory name below. Reject it here, while we can
            // still stay silent, rather than at download time.
            if (!TrySanitizeTagForPath(tag!, out _))
            {
                RuntimeLog.Fail("UPDATE", $"Release tag '{tag}' is not usable as a folder name; staying silent.");
                return null;
            }

            // digest looks like "sha256:<hex>" — the publisher already trusts this exact value.
            string? digest = asset?["digest"]?.GetValue<string>();
            string? sha256 = null;
            if (!string.IsNullOrEmpty(digest))
            {
                int colon = digest.IndexOf(':');
                sha256 = colon >= 0 ? digest[(colon + 1)..].ToLowerInvariant() : null;
            }

            long size = asset?["size"]?.GetValue<long>() ?? 0;

            // SYS-PAYLOADSPLIT — the small package is optional. A release that does not publish one
            // behaves exactly as before, which is what makes this safe to ship ahead of the build
            // change that starts producing it.
            string? appOnlyUrl = appOnlyAsset?["browser_download_url"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(appOnlyUrl) && !IsAllowedAssetUrl(appOnlyUrl!))
            {
                RuntimeLog.Fail("UPDATE",
                    $"Release {tag} points its app-only package at an unexpected location; ignoring it "
                  + "and using the full installer.");
                appOnlyUrl = null;
            }

            if (!string.IsNullOrWhiteSpace(runtimeManifestUrl) && !IsAllowedAssetUrl(runtimeManifestUrl!))
                runtimeManifestUrl = null;

            string? appOnlyDigest = appOnlyAsset?["digest"]?.GetValue<string>();
            string? appOnlySha = null;
            if (!string.IsNullOrEmpty(appOnlyDigest))
            {
                int c2 = appOnlyDigest.IndexOf(':');
                appOnlySha = c2 >= 0 ? appOnlyDigest[(c2 + 1)..].ToLowerInvariant() : null;
            }

            return new UpdateRelease(
                tag, url, sha256, size, releaseNotes,
                appOnlyUrl,
                appOnlySha,
                appOnlyAsset?["size"]?.GetValue<long>() ?? 0,
                runtimeManifestUrl);
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("UPDATE", $"Release probe failed (offline or blocked?): {ex.Message}");
            return null;
        }
    }

    /// <summary>Reads the current running version using DeploymentLifecycle and Win32 file version.</summary>
    private static bool TryGetLocalVersion(out Version version)
    {
        version = new Version(0, 0);
        try
        {
            string current = DeploymentLifecycle.GetCurrentVersion();
            if (DeploymentLifecycle.TryParseVersion(current, out version))
            {
                return true;
            }

            string? exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return false;
            return DeploymentLifecycle.TryParseVersion(FileVersionInfo.GetVersionInfo(exe).FileVersion, out version);
        }
        catch (System.Exception swallowed3)
        {
            global::FortniteVideoSoftware.App.RuntimeLog.Swallowed(swallowed3);   // FAULTTIER_02 — no failure is silent.
            return false;
        }
    }

    /// <summary>
    /// SYS-PAYLOADSPLIT — DECIDES WHETHER THIS MACHINE NEEDS THE 322 MB INSTALLER OR JUST THE APP.
    ///
    /// <para>
    /// Returns true only when the release published an app-only package AND advertised a runtime
    /// fingerprint AND that fingerprint matches what is installed here. Every other path — no small
    /// package, no advertised fingerprint, an unreadable local manifest, a network failure reading
    /// the sidecar, or a genuine mismatch — returns false and the user gets the full installer.
    /// </para>
    ///
    /// <para>⚠️ THE ASYMMETRY IS THE WHOLE DESIGN. Wrongly choosing the big download costs
    /// bandwidth. Wrongly choosing the small one installs an application against codec binaries it
    /// was not built for, which fails at export time, on the user's machine, after they have done
    /// the work. So every uncertainty resolves to the installer.</para>
    /// </summary>
    private static async Task<bool> RuntimeAlreadyMatchesAsync(UpdateRelease release)
    {
        if (!release.HasAppOnlyPackage || string.IsNullOrWhiteSpace(release.RuntimeManifestUrl))
            return false;

        try
        {
            var installed = FortniteVideoSoftware.Core.Infrastructure.RuntimePayloadManifest.Read(
                AppContext.BaseDirectory);
            if (installed is null)
            {
                RuntimeLog.Info("UPDATE",
                    "No local runtime manifest, so the installed runtime cannot be proven current; "
                  + "using the full installer.");
                return false;
            }

            using var cts = new CancellationTokenSource(ProbeTimeout);
            string json = await Http.GetStringAsync(release.RuntimeManifestUrl!, cts.Token).ConfigureAwait(false);

            var advertised = FortniteVideoSoftware.Core.Infrastructure.RuntimePayloadManifest.FromJson(
                JsonNode.Parse(json)?.AsObject());
            if (advertised is null) return false;

            bool match = string.Equals(installed.Fingerprint, advertised.Fingerprint, StringComparison.Ordinal);

            RuntimeLog.Info("UPDATE", match
                ? $"Runtime already matches release {release.Tag} ({installed.Fingerprint}); downloading the "
                + $"app only ({release.DescribeDownloadSize(appOnly: true)} instead of "
                + $"{release.DescribeDownloadSize(appOnly: false)})."
                : $"Runtime differs from release {release.Tag} (local {installed.Fingerprint}, release "
                + $"{advertised.Fingerprint}); the full installer is required.");

            return match;
        }
        catch (OperationCanceledException)
        {
            // A cancel is not a fault (FAULTTIER_01) and is not a reason to pick the small package.
            return false;
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("UPDATE",
                $"Could not compare runtime fingerprints ({ex.GetType().Name}); using the full installer.");
            return false;
        }
    }

    private static async Task DownloadVerifyLaunchAsync(Window owner, UpdateRelease release)
    {
        PurgeOldDownloadFolders();

        // SYS-PAYLOADSPLIT — resolved here, once, before anything is fetched, so the size the user
        // is told about below is the size that is actually downloaded.
        bool appOnly = await RuntimeAlreadyMatchesAsync(release).ConfigureAwait(false);
        if (appOnly)
        {
            RuntimeLog.Info("UPDATE",
                $"Update {release.Tag}: app-only package selected "
              + $"({release.DescribeDownloadSize(true)} rather than {release.DescribeDownloadSize(false)}).");
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // UPDATETRUST_01 — THE TAG IS UNTRUSTED INPUT AND IT IS ABOUT TO BECOME A DIRECTORY NAME.
        //
        // WHAT WAS WRONG: this was Path.Combine(GetTempPath(), DownloadFolderRootName, release.Tag)
        // with release.Tag straight out of the GitHub JSON. The only gate upstream is
        // DeploymentLifecycle.TryParseVersion, which does TrimStart('v','V') then
        // TakeWhile(IsDigit || '.') — it validates a PREFIX and silently discards the rest. So
        // "9.9.9\..\..\Microsoft\Windows\Start Menu\Programs\Startup" parses happily as 9.9.9 and
        // was then used verbatim as a folder name. Path.Combine does not reject "..", so the .exe
        // landed wherever the tag pointed — a persistence primitive, one JSON field wide.
        //
        // The fix rejects rather than strips (a stripped traversal silently collides with another
        // release's folder) and then ASSERTS containment on the resolved path, so even a sanitiser
        // bug cannot put a file outside the download root.
        // ══════════════════════════════════════════════════════════════════════════════════════
        if (!TrySanitizeTagForPath(release.Tag, out string tagFolderName))
        {
            RuntimeLog.Fail("UPDATE", $"Refusing to download release '{release.Tag}': the tag is not a usable folder name.");
            return;
        }

        string downloadRoot = Path.Combine(Path.GetTempPath(), DownloadFolderRootName);
        string folder = Path.Combine(downloadRoot, tagFolderName);

        string resolvedRoot = Path.GetFullPath(downloadRoot);
        string resolvedFolder = Path.GetFullPath(folder);
        if (!resolvedFolder.StartsWith(
                resolvedRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            RuntimeLog.Fail("UPDATE", "Refusing to download: the resolved update folder escapes the download root.");
            return;
        }

        folder = resolvedFolder;
        string finalPath = Path.Combine(folder, ExpectedAssetName);
        string partPath = finalPath + ".part";
        Directory.CreateDirectory(folder);

        using var cts = new CancellationTokenSource();
        UpdateDownloadWindow? progressWindow = null;
        Task dialogTask = Task.CompletedTask;
        var dialogShown = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                progressWindow = new UpdateDownloadWindow();
                progressWindow.CancelRequested += () => cts.Cancel();
                dialogTask = progressWindow.ShowDialog(owner);
                dialogShown.SetResult(null);
            }
            catch (Exception ex)
            {
                dialogShown.SetException(ex);
                global::FortniteVideoSoftware.App.RuntimeLog.Swallowed(ex);   // FAULTTIER_02 — no failure is silent.
            }
        });
        await dialogShown.Task.ConfigureAwait(false);

        try
        {
            using var response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? release.Size;

            await using Stream source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            await using var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024);
            byte[] buffer = new byte[1024 * 1024];
            long copied = 0;
            DateTime lastReport = DateTime.MinValue;

            // Network guard: 45-second per-chunk read stall timeout to prevent hanging indefinitely
            while (true)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                readCts.CancelAfter(TimeSpan.FromSeconds(45));

                int read = await source.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
                if (read <= 0) break;

                await target.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                copied += read;
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 200)
                {
                    lastReport = DateTime.UtcNow;
                    ReportDownloadProgress(progressWindow, copied, total);
                }
            }
            await target.FlushAsync(cts.Token).ConfigureAwait(false);
            ReportDownloadProgress(progressWindow, copied, total > 0 ? total : copied);

            if (string.IsNullOrWhiteSpace(release.Sha256Hex))
            {
                throw new InvalidOperationException("The release has no published fingerprint, so the download cannot be verified. Nothing was installed.");
            }

            string actualHash;
            await using (FileStream verifyStream = File.OpenRead(partPath))
            {
                actualHash = Convert.ToHexString(SHA256.HashData(verifyStream)).ToLowerInvariant();
            }

            if (!string.Equals(actualHash, release.Sha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The downloaded file does not match the fingerprint the publisher uploaded. The file was deleted and nothing was installed.");
            }

            File.Move(partPath, finalPath, overwrite: true);

            // ══════════════════════════════════════════════════════════════════════════════════
            // UPDATETRUST_01 — RE-HASH THE PATH WE ARE ACTUALLY GOING TO EXECUTE.
            //
            // The hash above was computed over partPath; the file then got RENAMED and a DIFFERENT
            // path is launched below. That rename window was a time-of-check/time-of-use gap: the
            // bytes that were verified and the bytes that run were never proven to be the same
            // bytes. Re-hashing finalPath costs one sequential read of a file already in the page
            // cache and closes the gap completely.
            // ══════════════════════════════════════════════════════════════════════════════════
            string finalHash;
            await using (FileStream finalStream = File.OpenRead(finalPath))
            {
                finalHash = Convert.ToHexString(SHA256.HashData(finalStream)).ToLowerInvariant();
            }

            if (!string.Equals(finalHash, release.Sha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The installer changed on disk after it was verified. Nothing was installed.");
            }

            // ══════════════════════════════════════════════════════════════════════════════════
            // UPDATETRUST_01 — PROVE THE PUBLISHER, NOT JUST THE BYTES.
            //
            // The SHA-256 above and the URL it validates come out of the SAME JSON document. That
            // pair proves transport integrity and nothing more: whoever can produce that response
            // controls both halves at once. This is the only check that asks "did WE sign this?",
            // and it runs on the exact path about to be executed with elevation.
            //
            // See AuthenticodeVerifier for why the anchor is the running process rather than a
            // hardcoded thumbprint, and why an unsigned running build degrades to hash-only
            // LOUDLY instead of failing closed.
            // ══════════════════════════════════════════════════════════════════════════════════
            var verdict = AuthenticodeVerifier.EvaluateUpdateCandidate(
                finalPath, Environment.ProcessPath, out string trustDetail);

            switch (verdict)
            {
                case AuthenticodeVerifier.TrustVerdict.Rejected:
                    TryDeleteFile(finalPath);
                    throw new InvalidOperationException(
                        "The downloaded installer failed its signature check and was deleted. Nothing was installed." +
                        Environment.NewLine + trustDetail);

                // ══════════════════════════════════════════════════════════════════════════
                // UPDATETRUST_02 — NoAnchor IS A REFUSAL, NOT A WARNING.
                //
                // This branch used to log one line and fall through to Process.Start with
                // --install --auto-update, i.e. it executed the downloaded binary elevated on the
                // strength of a SHA-256 read out of the same GitHub JSON document that supplied
                // the URL. That hash proves transport integrity and nothing else. Anyone able to
                // produce that response body — a compromised repo or CI token, a TLS-terminating
                // proxy, a mis-issued certificate — controls the payload AND the fingerprint that
                // validates it, in one move.
                //
                // AuthenticodeVerifier's own class comment names this as the attack it exists to
                // close. Keeping a fall-through for unsigned builds meant it was never closed in
                // production, because production WAS the unsigned build (SIGNMANDATE_01).
                //
                // Refusing costs the one thing a warning was protecting: in-app auto-update for
                // installs that are themselves unsigned. That is a real regression and it is the
                // correct trade — the user is told exactly what happened and sent to the release
                // page to install the signed build by hand, ONCE. From then on they have an
                // anchor and auto-update works normally and verifiably.
                //
                // FVS_ALLOW_UNSIGNED_UPDATE=1 restores the old behaviour for developers testing
                // the update path against unsigned local builds. It is read from the environment
                // on purpose: it cannot be set by a downloaded payload, a settings file or a
                // server response, so nothing an attacker controls can re-open this door.
                // ══════════════════════════════════════════════════════════════════════════
                case AuthenticodeVerifier.TrustVerdict.NoAnchor:
                    if (!string.Equals(Environment.GetEnvironmentVariable("FVS_ALLOW_UNSIGNED_UPDATE"), "1", StringComparison.Ordinal))
                    {
                        TryDeleteFile(finalPath);
                        RuntimeLog.Fail("UPDATE",
                            "REFUSED — " + trustDetail +
                            " The installer was deleted rather than executed with elevation on an unverifiable fingerprint (UPDATETRUST_02).");
                        throw new InvalidOperationException(
                            "This build is not digitally signed, so the downloaded update could not be checked against a publisher." +
                            Environment.NewLine + Environment.NewLine +
                            "Nothing was installed and the download was deleted. To update safely, download the latest release " +
                            "manually from the project's releases page and run it once — after that, updates will verify and " +
                            "install automatically.");
                    }

                    RuntimeLog.Fail("UPDATE",
                        "SIGNATURE CHECK SKIPPED — " + trustDetail +
                        " FVS_ALLOW_UNSIGNED_UPDATE=1 is set, so the update was accepted on its published fingerprint alone. " +
                        "DEVELOPER OVERRIDE — never set this on an end-user machine.");
                    break;

                default:
                    RuntimeLog.Info("UPDATE", "Publisher check passed: " + trustDetail);
                    break;
            }

            // Honour a Cancel clicked during verification/handoff — never install past a cancel.
            cts.Token.ThrowIfCancellationRequested();

            Dispatcher.UIThread.Post(() => progressWindow?.MarkHandoffToInstaller());
            RuntimeLog.Info("UPDATE", $"Download of {release.Tag} verified (sha256 {finalHash[..12]}…). Handing off to installer with --auto-update.");

            // --auto-update makes DeploymentLifecycle force the preserve-settings answer to YES
            // without asking, then relaunch the app. Windows will still show its own UAC consent
            // once — that is OS security and cannot (and should not) be bypassed by any app.
            Process.Start(new ProcessStartInfo(finalPath, "--install --auto-update") { UseShellExecute = true });

            SettingsManager.Save();
            await Task.Delay(1500).ConfigureAwait(false);   // let the handoff state render before exit
            Environment.Exit(0);
        }
        catch (OperationCanceledException)
        {
            RuntimeLog.Info("UPDATE", "Update download cancelled by the user; current install untouched.");
            TryDeleteFile(partPath);
            // UPDATETRUST_01 — a cancel after the rename must not leave a runnable installer behind.
            TryDeleteFile(finalPath);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UPDATE", $"Update download/verify failed: {ex.Message}");
            TryDeleteFile(partPath);
            // UPDATETRUST_01 — every rejection path removes the artifact, including one rejected
            // AFTER the rename (bad re-hash, failed signature check).
            TryDeleteFile(finalPath);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                NativeDialog.ShowError(
                    "The update could not be downloaded." + Environment.NewLine + Environment.NewLine +
                    $"Reason: {ex.Message}" + Environment.NewLine + Environment.NewLine +
                    "Your current version was not changed. The app will offer the update again on a later start.",
                    "Update Failed");
            });
        }
        finally
        {
            Dispatcher.UIThread.Post(() => { try { progressWindow?.Close(); } catch (System.Exception swallowed5)
            {
                global::FortniteVideoSoftware.App.RuntimeLog.Swallowed(swallowed5);   // FAULTTIER_02 — no failure is silent.
            } });
        }

        await dialogTask.ConfigureAwait(false);
    }

    private static void ReportDownloadProgress(UpdateDownloadWindow? window, long copied, long total)
    {
        if (window == null) return;
        double fraction = total > 0 ? Math.Clamp((double)copied / total, 0, 1) : 0;
        string text = total > 0
            ? $"{fraction:P0}  —  {copied / (1024 * 1024)} MB of {total / (1024 * 1024)} MB"
            : $"{copied / (1024 * 1024)} MB downloaded";
        Dispatcher.UIThread.Post(() => window.SetProgress(fraction, text));
    }

    /// <summary>
    /// Removes download leftovers from previous updates. Best-effort: a folder still locked by a
    /// running installer is left for the next attempt.
    /// </summary>
    /// <summary>
    /// UPDATETRUST_01 — true only for an HTTPS URL whose host is one of
    /// <see cref="AllowedAssetHosts"/> (exact match or a subdomain of one).
    /// </summary>
    private static bool IsAllowedAssetUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        string host = uri.Host;
        foreach (string allowed in AllowedAssetHosts)
        {
            if (string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase)) return true;
            if (host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// UPDATETRUST_01 — projects an untrusted release tag onto a filesystem-safe folder name, or
    /// REFUSES.
    ///
    /// <para><b>It rejects; it does not strip.</b> Silently removing the offending characters would
    /// map two different tags onto one folder, which is its own (quieter) correctness bug — and it
    /// is exactly the "validate a prefix, discard the rest" mistake in
    /// <c>DeploymentLifecycle.TryParseVersion</c> that let a traversal through in the first place.</para>
    ///
    /// <para>⚠️ The RAW tag stays authoritative everywhere else. <c>SkippedTagFile</c> persists and
    /// compares the raw string, so a sanitised value must never be written there or a release the
    /// user skipped would be offered again on the next start.</para>
    /// </summary>
    private static bool TrySanitizeTagForPath(string? tag, out string folderName)
    {
        folderName = string.Empty;
        if (string.IsNullOrWhiteSpace(tag)) return false;

        string candidate = tag!.Trim();
        if (candidate.Length == 0 || candidate.Length > 64) return false;

        // Leading/trailing dots and any ".." run are traversal or Windows-illegal names.
        if (candidate.StartsWith('.') || candidate.EndsWith('.')) return false;
        if (candidate.Contains("..", StringComparison.Ordinal)) return false;

        foreach (char c in candidate)
        {
            bool ok = (c >= 'a' && c <= 'z')
                   || (c >= 'A' && c <= 'Z')
                   || (c >= '0' && c <= '9')
                   || c == '.' || c == '-' || c == '_';
            if (!ok) return false;
        }

        // Reserved Windows device names, with or without an extension.
        string stem = candidate;
        int dot = stem.IndexOf('.');
        if (dot >= 0) stem = stem[..dot];
        foreach (string reserved in ReservedDeviceNames)
        {
            if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase)) return false;
        }

        folderName = candidate;
        return true;
    }

    private static readonly string[] ReservedDeviceNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static void PurgeOldDownloadFolders()
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), DownloadFolderRootName);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug("UPDATE", $"Could not purge old update downloads: {ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (System.Exception swallowed2)
        {
            global::FortniteVideoSoftware.App.RuntimeLog.Swallowed(swallowed2);   // FAULTTIER_02 — no failure is silent.
        }
    }
}
