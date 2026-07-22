using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FortniteVideoSoftware.App.Infrastructure;

namespace FortniteVideoSoftware.App;

/// <summary>
/// Meme System §1: one entry in the active meme directory, carrying the probed native
/// dimensions and aspect ratio for the §2 UI guardrail. ToString() returns the file NAME so
/// legacy string-based recovery/state comparisons keep working against combo items.
/// </summary>
public sealed class MemeItem
{
    public string FileName { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsImage { get; init; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>§1 math: (float)Width / (float)Height. 0 when the probe failed.</summary>
    public float AspectRatio => Height > 0 ? (float)Width / Height : 0f;
    /// <summary>Sentinel for the §4 "Download more memes..." action row (never exportable).</summary>
    public bool IsDownloadAction { get; init; }

    public override string ToString() => FileName;
}

/// <summary>
/// Meme System §1 (scan + dimension probe) and §4 (cloud delta-sync from GitHub).
/// All methods are exception-hardened: a failed probe yields Width/Height = 0 (item still
/// usable), and a failed sync reports gracefully without touching the UI thread.
/// </summary>
public static class MemeCatalog
{
    private static readonly string[] VideoExts = { ".mp4", ".mkv", ".avi" };
    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg" };

    private const string CloudOwner = "alonreich";
    private const string CloudRepo = "Fortnite_Video_Software_C-";
    private static readonly string[] CloudFolders = { "mp4", "jpeg" };

    /// <summary>
    /// §1 File Ingestion: scans the ACTIVE meme directory for supported formats, skipping
    /// zero-byte files, and probes each file's native dimensions (ffprobe for videos,
    /// SkiaSharp for images). Runs fully off the UI thread.
    /// §3 Exception Handling: UnauthorizedAccessException propagates so the Settings flow
    /// can block the path change and revert; all other per-file errors are swallowed.
    /// </summary>
    public static async Task<List<MemeItem>> ScanAsync(string directory, string ffprobePath)
    {
        return await Task.Run(() =>
        {
            var items = new List<MemeItem>();
            if (!Directory.Exists(directory)) return items;

            string[] files = Directory.GetFiles(directory);

            foreach (string f in files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                bool isVideo = VideoExts.Contains(ext);
                bool isImage = ImageExts.Contains(ext);
                if (!isVideo && !isImage) continue;

                try { if (new FileInfo(f).Length == 0) continue; } catch { continue; }

                var item = new MemeItem { FileName = Path.GetFileName(f), FullPath = f, IsImage = isImage };
                try
                {
                    if (isImage)
                    {
                        using var codec = SkiaSharp.SKCodec.Create(f);
                        if (codec != null) { item.Width = codec.Info.Width; item.Height = codec.Info.Height; }
                    }
                    else
                    {
                        var (w, h) = new FortniteVideoSoftware.Core.Media.MediaProber(ffprobePath, f)
                            .GetResolutionAsync().GetAwaiter().GetResult();
                        item.Width = w; item.Height = h;
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Info("Memes", $"Dimension probe failed for '{item.FileName}': {ex.Message}");
                }
                items.Add(item);
            }
            return items;
        });
    }

    /// <summary>
    /// §4 Cloud sync: lists the repo's meme folders via the GitHub contents API, downloads
    /// only files that don't exist locally in <paramref name="targetDirectory"/> (delta sync).
    /// Returns (downloadedCount, errorMessage) — errorMessage is null on success; on HTTP 403
    /// (rate limit) or any network failure it returns the §4 mandated user-facing message.
    /// </summary>
    public static async Task<(int downloaded, string? error)> SyncFromCloudAsync(string targetDirectory)
    {
        int downloaded = 0;
        try
        {
            Directory.CreateDirectory(targetDirectory);
            var local = new HashSet<string>(
                Directory.GetFiles(targetDirectory).Select(f => Path.GetFileName(f)!),
                StringComparer.OrdinalIgnoreCase);

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FortniteVideoSoftware");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            foreach (string folder in CloudFolders)
            {
                string url = $"https://api.github.com/repos/{CloudOwner}/{CloudRepo}/contents/{folder}";
                using var resp = await http.GetAsync(url);
                if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    RuntimeLog.Fail("Memes", "Cloud sync halted: GitHub API rate limit (HTTP 403).");
                    return (downloaded, "Sync temporarily unavailable");
                }
                if (!resp.IsSuccessStatusCode)
                {
                    RuntimeLog.Fail("Memes", $"Cloud sync: listing '{folder}' failed with HTTP {(int)resp.StatusCode}.");
                    continue;
                }

                var arr = JsonNode.Parse(await resp.Content.ReadAsStringAsync()) as JsonArray;
                if (arr == null) continue;

                foreach (var node in arr)
                {
                    string? name = node?["name"]?.ToString();
                    string? dl = node?["download_url"]?.ToString();
                    string? type = node?["type"]?.ToString();
                    if (name == null || dl == null || type != "file") continue;

                    string ext = Path.GetExtension(name).ToLowerInvariant();
                    if (!VideoExts.Contains(ext) && !ImageExts.Contains(ext)) continue;
                    if (local.Contains(name)) continue;

                    string dest = Path.Combine(targetDirectory, name);
                    string tmp = dest + ".part";
                    using (var s = await http.GetStreamAsync(dl))
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                    {
                        await s.CopyToAsync(fs);
                    }
                    File.Move(tmp, dest, overwrite: true);
                    local.Add(name);
                    downloaded++;
                    RuntimeLog.Info("Memes", $"Cloud sync: downloaded '{name}'.");
                }
            }
            RuntimeLog.Info("Memes", $"Cloud sync complete: {downloaded} new meme(s).");
            return (downloaded, null);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Memes", $"Cloud sync failed: {ex.Message}");
            return (downloaded, "Sync temporarily unavailable");
        }
    }
}
