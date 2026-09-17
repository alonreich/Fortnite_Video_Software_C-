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

    private sealed record UpdateRelease(string Tag, string DownloadUrl, string? Sha256Hex, long Size, string? ReleaseNotes);

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
        if (Interlocked.CompareExchange(ref _checkInProgress, 1, 0) != 0) return;
        try
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
            await Task.Delay(StartupGracePeriod).ConfigureAwait(false);

            bool ownerStillVisible = await Dispatcher.UIThread.InvokeAsync(() => owner.IsVisible);
            if (!ownerStillVisible) return;

            if (!ThrottlePermitsCheck()) return;

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
                catch (Exception ex) { choiceReady.SetException(ex); }
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
            statusCallback?.Invoke("An update check is already in progress...");
            return;
        }

        try
        {
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
                catch (Exception ex) { choiceReady.SetException(ex); }
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
        }
        finally
        {
            _ = Interlocked.Exchange(ref _checkInProgress, 0);
        }
    }

    public static string GetSkippedVersion()
    {
        try { return UiStateStore.ReadText(SkippedTagFile).Trim(); }
        catch { return string.Empty; }
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
            foreach (JsonNode? candidate in root?["assets"]?.AsArray() ?? [])
            {
                if (candidate?["name"]?.GetValue<string>()?.Equals(ExpectedAssetName, StringComparison.OrdinalIgnoreCase) == true)
                {
                    asset = candidate;
                    break;
                }
            }

            string? url = asset?["browser_download_url"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(url))
            {
                RuntimeLog.Fail("UPDATE", $"Release {tag} carries no '{ExpectedAssetName}' asset; staying silent.");
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
            return new UpdateRelease(tag, url, sha256, size, releaseNotes);
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
        catch
        {
            return false;
        }
    }

    private static async Task DownloadVerifyLaunchAsync(Window owner, UpdateRelease release)
    {
        PurgeOldDownloadFolders();

        string folder = Path.Combine(Path.GetTempPath(), DownloadFolderRootName, release.Tag);
        string finalPath = Path.Combine(folder, ExpectedAssetName);
        string partPath = finalPath + ".part";
        Directory.CreateDirectory(folder);

        using var cts = new CancellationTokenSource();
        var progressWindow = new UpdateDownloadWindow();
        progressWindow.CancelRequested += () => cts.Cancel();

        Task dialogTask = Task.CompletedTask;
        var dialogShown = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try { dialogTask = progressWindow.ShowDialog(owner); dialogShown.SetResult(null); }
            catch (Exception ex) { dialogShown.SetException(ex); }
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

            // Honour a Cancel clicked during verification/handoff — never install past a cancel.
            cts.Token.ThrowIfCancellationRequested();

            Dispatcher.UIThread.Post(() => progressWindow.MarkHandoffToInstaller());
            RuntimeLog.Info("UPDATE", $"Download of {release.Tag} verified (sha256 {actualHash[..12]}…). Handing off to installer with --auto-update.");

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
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UPDATE", $"Update download/verify failed: {ex.Message}");
            TryDeleteFile(partPath);
            NativeDialog.ShowError(
                "The update could not be downloaded." + Environment.NewLine + Environment.NewLine +
                $"Reason: {ex.Message}" + Environment.NewLine + Environment.NewLine +
                "Your current version was not changed. The app will offer the update again on a later start.",
                "Update Failed");
        }
        finally
        {
            Dispatcher.UIThread.Post(() => { try { progressWindow.Close(); } catch { /* already closed */ } });
        }

        await dialogTask.ConfigureAwait(false);
    }

    private static void ReportDownloadProgress(UpdateDownloadWindow window, long copied, long total)
    {
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
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
