using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;

namespace FvsBuild;

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
        string[] assets = ReleaseAssets(exePath);
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

    internal static string[] ReleaseAssets(string exePath) =>
    [
        exePath,
        Path.Combine("obj", "ReleaseAssets", LegacyProductIdentity.DownloadName),
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
