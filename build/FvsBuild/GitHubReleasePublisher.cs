using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;

namespace FvsBuild;

/// <summary>
/// Replaces the one-and-only GitHub release with what was just built (old
/// :PUBLISH_RELEASE). Only reached when the build succeeded. Nothing is hardcoded:
/// the repository is resolved from this folder's git remote via `gh repo view`, every
/// pre-existing release is enumerated and removed so "latest and only" stays true,
/// and the upload is verified BY HASH, not exit code - `gh release upload` once
/// reported success while serving a two-day-old binary.
/// </summary>
/// <summary>Publish a complete verified draft before changing the latest release.
/// Old releases and tags remain available to interrupted downloads and older clients.</summary>
internal static class GitHubReleasePublisher
{
    public static bool Publish(string exePath, string tag, BuildLog log)
    {
        if (Cli.FindOnPath("gh.exe") is null || Cli.RunQuiet("gh", ["auth", "status"]) != 0)
        {
            log.Warn("[PUBLISH] GitHub CLI is unavailable or is not signed in. The existing release was not changed.");
            return false;
        }
        if (!Cli.TryCapture("gh", ["repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner"], out var repositories) || repositories.Count != 1)
            return false;
        string repo = repositories[0];
        if (!File.Exists(exePath))
        {
            log.Warn("[PUBLISH] The installer is missing. Nothing was published.");
            return false;
        }

        // REBRAND_03 — old-brand clients look for the previous download name in the latest
        // release. That alias is created here, in a private temp folder, for the duration of the
        // upload only, so no previous-brand file ever persists in the repository or obj/.
        string aliasDirectory = Path.Combine(Path.GetTempPath(), "fvs-release-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(aliasDirectory);
            string alias = Path.Combine(aliasDirectory, LegacyProductIdentity.DownloadName);
            File.Copy(exePath, alias, overwrite: true);
            return PublishAssets(ReleaseAssets(exePath, alias), tag, repo, exePath, log);
        }
        finally
        {
            try { if (Directory.Exists(aliasDirectory)) Directory.Delete(aliasDirectory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn("[PUBLISH] Temporary release alias could not be removed: " + ex.Message);
            }
        }
    }

    private static bool PublishAssets(string[] assets, string tag, string repo, string exePath, BuildLog log)
    {
        if (assets.Any(path => !File.Exists(path)))
        {
            log.Warn("[PUBLISH] A release asset is missing. Nothing was published.");
            return false;
        }
        List<string> create = ["release", "create", tag];
        create.AddRange(assets);
        create.AddRange(["--repo", repo, "--title", $"Free Video Studio {tag}", "--notes",
            $"Free Video Studio {tag}. Existing settings are preserved during migration.", "--draft"]);
        if (Cli.RunStreaming("gh", create, log) != 0)
        {
            log.Warn("[PUBLISH] Draft upload failed. The existing latest release is still available.");
            return false;
        }
        if (!Cli.TryCapture("gh", ["release", "view", tag, "--repo", repo, "--json", "assets"], out var response) ||
            !VerifyAssets(assets, string.Join('\n', response)))
        {
            log.Warn("[PUBLISH] Asset verification failed. The draft was kept unpublished for inspection.");
            return false;
        }
        if (Cli.RunStreaming("gh", ["release", "edit", tag, "--repo", repo, "--draft=false", "--latest"], log) != 0)
        {
            log.Warn("[PUBLISH] The verified draft could not be published. Previous releases were preserved.");
            return false;
        }
        log.Success($"SUCCESS: verified release {tag} is now latest.");
        log.Info($"Download: https://github.com/{repo}/releases/latest/download/{Path.GetFileName(exePath)}");
        return true;
    }

    internal static string[] ReleaseAssets(string exePath, string legacyAliasPath) =>
    [
        exePath,
        legacyAliasPath,
        Path.Combine("obj", "ReleaseAssets", "FreeVideoStudio.App.update.zip"),
        Path.Combine("obj", "ReleaseAssets", RuntimePayloadManifest.FileName)
    ];

    internal static bool VerifyAssets(IEnumerable<string> paths, string response)
    {
        try
        {
            if (JsonNode.Parse(response)?["assets"] is not JsonArray assets) return false;
            foreach (string path in paths)
            {
                JsonNode?[] matches = assets.Where(a => a?["name"]?.GetValue<string>() == Path.GetFileName(path)).ToArray();
                if (matches.Length != 1) return false;
                string? digest = matches[0]?["digest"]?.GetValue<string>();
                using var stream = File.OpenRead(path);
                string expected = "sha256:" + Convert.ToHexString(SHA256.HashData(stream));
                if (!string.Equals(digest, expected, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
