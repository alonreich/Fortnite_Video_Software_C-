using System.Security.Cryptography;

namespace FvsBuild;

/// <summary>
/// Replaces the one-and-only GitHub release with what was just built (old
/// :PUBLISH_RELEASE). Only reached when the build succeeded. Nothing is hardcoded:
/// the repository is resolved from this folder's git remote via `gh repo view`, every
/// pre-existing release is enumerated and removed so "latest and only" stays true,
/// and the upload is verified BY HASH, not exit code - `gh release upload` once
/// reported success while serving a two-day-old binary.
/// </summary>
internal static class GitHubReleasePublisher
{
    public static bool Publish(string exePath, string tag, BuildLog log)
    {
        if (Cli.FindOnPath("gh.exe") is null)
        {
            log.Warn("[PUBLISH] STOPPED: GitHub CLI (gh) is not installed or not on PATH.");
            log.Warn("[PUBLISH] Fix: install from https://cli.github.com  - or run  .\\Build.cmd --no-publish");
            return false;
        }
        log.Info("[PUBLISH] 1/7 GitHub CLI found.                                    [OK]");

        if (Cli.RunQuiet("gh", ["auth", "status"]) != 0)
        {
            log.Warn("[PUBLISH] STOPPED: gh is installed but not signed in.");
            log.Warn("[PUBLISH] Fix: run  gh auth login");
            return false;
        }
        log.Info("[PUBLISH] 2/7 GitHub sign-in valid.                                [OK]");

        if (!Cli.TryCapture("gh", ["repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner"], out List<string> repoLines) ||
            repoLines.Count == 0)
        {
            log.Warn("[PUBLISH] STOPPED: could not work out the GitHub repository for this folder.");
            log.Warn("[PUBLISH] Fix: confirm  git remote -v  points at GitHub and you can reach the network.");
            return false;
        }
        string repo = repoLines[0];
        log.Info($"[PUBLISH] 3/7 Target repository: {repo}            [OK]");

        string? localHash = TrySha256Hex(exePath);
        if (localHash is null)
        {
            log.Warn("[PUBLISH] STOPPED: could not fingerprint the freshly built exe.");
            return false;
        }
        log.Info($"[PUBLISH] 4/7 Built exe fingerprint + tag {tag} ready.        [OK]");

        // THERE IS EXACTLY ONE INSTALLER, EVERYWHERE, ALWAYS. :VALIDATE_COMPILED_OUTPUT
        // already proved compiled\ holds one exe; here every previous release and tag is
        // deleted (--cleanup-tag only removes tags the deleted release owned, so a
        // leftover hand-made tag is swept separately), and the slate is RE-READ before
        // publishing onto it because a silently failed delete once counted as success.
        int removed = 0;
        if (!Cli.TryCapture("gh", ["release", "list", "--repo", repo, "--limit", "200", "--json", "tagName", "--jq", ".[].tagName"], out List<string> tags))
        {
            tags = [];
        }
        foreach (string tagName in tags)
        {
            log.Info($"[PUBLISH]     removing previous release {tagName}");
            _ = Cli.RunQuiet("gh", ["release", "delete", tagName, "--repo", repo, "--cleanup-tag", "--yes"]);
            removed++;
        }

        _ = Cli.RunQuiet("git", ["fetch", "--tags", "--prune", "--prune-tags"]);
        if (Cli.TryCapture("git", ["tag", "--list"], out List<string> localTags))
        {
            foreach (string tagName in localTags)
            {
                _ = Cli.RunQuiet("git", ["push", "origin", "--delete", tagName]);
                _ = Cli.RunQuiet("git", ["tag", "-d", tagName]);
            }
        }

        if (!Cli.TryCapture("gh", ["release", "list", "--repo", repo, "--limit", "200", "--json", "tagName", "--jq", ".[].tagName"], out List<string> survivors))
        {
            survivors = [];
        }
        if (survivors.Count > 0)
        {
            log.Warn("[PUBLISH] STOPPED: these releases could not be removed: " + string.Join(' ', survivors));
            log.Warn("[PUBLISH] Publishing now would leave more than one installer live. Remove them by hand.");
            return false;
        }
        log.Info($"[PUBLISH] 5/7 Removed {removed} previous release(s) and all tags.   [OK]");

        string notes = $"Automated NativeAOT release published by FvsBuild on {tag}. SHA256 {localHash}";
        if (Cli.RunQuiet("gh", ["release", "create", tag, exePath, "--repo", repo,
                "--title", $"Fortnite Video Software {tag}", "--notes", notes, "--latest"]) != 0)
        {
            log.Warn($"[PUBLISH] STOPPED: creating release {tag} failed.");
            log.Warn("[PUBLISH] Your build is fine - only the upload failed. Retry, or publish by hand.");
            return false;
        }
        log.Info($"[PUBLISH] 6/7 Release {tag} created and asset uploaded.        [OK]");

        if (!Cli.TryCapture("gh", ["release", "view", tag, "--repo", repo, "--json", "assets", "--jq", ".assets[0].digest"], out List<string> digestLines) ||
            digestLines.Count == 0)
        {
            log.Warn("[PUBLISH] STOPPED: could not read the published asset digest.");
            return false;
        }
        string remoteHash = digestLines[0].Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(remoteHash, localHash, StringComparison.OrdinalIgnoreCase))
        {
            log.Warn("[PUBLISH] STOPPED: the uploaded asset does NOT match the file that was just built.");
            log.Warn($"[PUBLISH]     built    : {localHash}");
            log.Warn($"[PUBLISH]     published: {remoteHash}");
            log.Warn("[PUBLISH] The release is serving the WRONG binary - fix before telling anyone about it.");
            return false;
        }
        log.Info("[PUBLISH] 7/7 Published asset hash matches the built exe.          [OK]");

        log.Info(string.Empty);
        log.Success($"SUCCESS: release {tag} is live and is the only release.");
        log.Info($"Download: https://github.com/{repo}/releases/latest/download/{Path.GetFileName(exePath)}");
        return true;
    }

    private static string? TrySha256Hex(string path)
    {
        try
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
