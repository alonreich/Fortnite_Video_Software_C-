using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
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

    /// <summary>
    /// DOWNLOAD_01 — which library this action row fetches: "mp4" for video memes, "jpeg" for
    /// image memes. Empty on a real meme. Songs have their own button in the Music Wizard.
    /// The two used to be a SINGLE row that pulled both folders, so a user who wanted one more
    /// reaction image had to download every video meme too, with no way to tell them apart while
    /// it ran.
    /// </summary>
    public string DownloadCategory { get; init; } = "";

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

    private static readonly string[] AudioExts = { ".mp3", ".wav", ".m4a", ".ogg", ".flac" };

    private const string CloudOwner = "alonreich";
    private const string CloudRepo = "Fortnite_Video_Software_C-";

    /// <summary>ISSUE_10 — repo folders holding meme assets (video + image).</summary>
    private static readonly string[] MemeCloudFolders = { "mp4", "jpeg" };

    /// <summary>
    /// ISSUE_10 — repo folder holding the song library.
    /// project_structure.txt documents mp3\ as an identically LFS-distributed asset folder, but
    /// the sync only ever covered mp4 and jpeg, so there was no way for a user to get the songs
    /// — the music library was bring-your-own with no in-app path to the shared collection.
    /// </summary>
    private static readonly string[] SongCloudFolders = { "mp3" };

    /// <summary>ISSUE_11 — per-file progress for the sync UI.</summary>
    /// <param name="FileName">The file currently being fetched.</param>
    /// <param name="Completed">How many files have finished so far.</param>
    /// <param name="Total">Total files this sync will fetch (0 until the listing completes).</param>
    public readonly record struct SyncProgress(string FileName, int Completed, int Total);

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

                if (isVideo && (item.Width <= 0 || item.Height <= 0))
                {
                    RuntimeLog.Fail("Memes", $"Excluding unreadable video meme '{item.FileName}' (failed to probe; would crash export).");
                    continue;
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
    /// <summary>True if the file is a Git-LFS pointer (small text starting with the LFS spec line).</summary>
    private static bool IsLfsPointer(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length is <= 0 or > 1024) return false;
            using var r = new StreamReader(path);
            char[] buf = new char[64];
            int n = r.Read(buf, 0, buf.Length);
            return n > 0 && new string(buf, 0, n).StartsWith("version https://git-lfs.github.com/spec", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>ISSUE_10 — downloads missing MEME assets (mp4 + jpeg folders).</summary>
    public static Task<(int downloaded, string? error)> SyncFromCloudAsync(
        string targetDirectory,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => SyncFoldersAsync(targetDirectory, MemeCloudFolders,
                            VideoExts.Concat(ImageExts).ToArray(), "Memes", progress, cancellationToken);


    /// <summary>DOWNLOAD_01 — video memes only (repo `mp4\`).</summary>
    public static Task<(int downloaded, string? error)> SyncVideoMemesAsync(
        string targetDirectory,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => SyncFoldersAsync(targetDirectory, new[] { "mp4" }, VideoExts, "Video memes", progress, cancellationToken);

    /// <summary>DOWNLOAD_01 — image memes only (repo `jpeg\`).</summary>
    public static Task<(int downloaded, string? error)> SyncImageMemesAsync(
        string targetDirectory,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => SyncFoldersAsync(targetDirectory, new[] { "jpeg" }, ImageExts, "Image memes", progress, cancellationToken);

    /// <summary>
    /// ISSUE_10 — downloads missing SONGS (mp3 folder) into the user's music directory.
    /// Mirrors the meme sync exactly, including the Git-LFS pointer handling, because the mp3
    /// files are LFS-tracked too and would otherwise land as unplayable 130-byte text files.
    /// </summary>
    public static Task<(int downloaded, string? error)> SyncSongsFromCloudAsync(
        string targetDirectory,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => SyncFoldersAsync(targetDirectory, SongCloudFolders, AudioExts, "Songs", progress, cancellationToken);

    /// <summary>
    /// Shared delta-sync engine.
    ///
    /// ISSUE_11 — three things the original lacked:
    ///   * <paramref name="progress"/>: the listing is enumerated FIRST so a real total is known,
    ///     then each file reports as it completes. Previously the UI sat silent for minutes.
    ///   * <paramref name="cancellationToken"/>: the user can abandon a slow sync. A cancelled
    ///     transfer leaves no partial file behind (.part is deleted).
    ///   * distinct error messages: rate-limit, offline, and per-file failures no longer all
    ///     collapse into the single string "Sync temporarily unavailable".
    /// </summary>
    private static async Task<(int downloaded, string? error)> SyncFoldersAsync(
        string targetDirectory,
        string[] cloudFolders,
        string[] acceptedExtensions,
        string logTag,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
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

            var pending = new List<(string Name, string Url)>();

            foreach (string folder in cloudFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string url = $"https://api.github.com/repos/{CloudOwner}/{CloudRepo}/contents/{folder}";

                using var listingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                listingCts.CancelAfter(TimeSpan.FromSeconds(30));

                using var resp = await http.GetAsync(url, listingCts.Token);

                if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    RuntimeLog.Fail(logTag, "Cloud sync halted: GitHub API rate limit (HTTP 403).");
                    return (downloaded,
                        "GitHub is rate-limiting this connection right now. Try again in an hour.");
                }
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    RuntimeLog.Fail(logTag, $"Cloud sync: folder '{folder}' does not exist in the repository.");
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                {
                    RuntimeLog.Fail(logTag, $"Cloud sync: listing '{folder}' failed with HTTP {(int)resp.StatusCode}.");
                    continue;
                }

                if (JsonNode.Parse(await resp.Content.ReadAsStringAsync(cancellationToken)) is not JsonArray arr) continue;

                foreach (var node in arr)
                {
                    string? name = node?["name"]?.ToString();
                    string? dl = node?["download_url"]?.ToString();
                    string? type = node?["type"]?.ToString();
                    if (name == null || dl == null || type != "file") continue;

                    if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) continue;

                    string ext = Path.GetExtension(name).ToLowerInvariant();
                    if (!acceptedExtensions.Contains(ext)) continue;

                    string existing = Path.Combine(targetDirectory, name);
                    if (local.Contains(name) && File.Exists(existing)
                        && new FileInfo(existing).Length > 0 && !IsLfsPointer(existing))
                        continue;

                    pending.Add((name, dl));
                }
            }

            int total = pending.Count;
            RuntimeLog.Info(logTag, $"Cloud sync: {total} file(s) to fetch.");
            progress?.Report(new SyncProgress(string.Empty, 0, total));

            if (total == 0) return (0, null);

            int failures = 0;
            foreach ((string name, string dl) in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new SyncProgress(name, downloaded, total));

                string dest = Path.Combine(targetDirectory, name);
                string tmp = dest + ".part";

                try
                {
                    using (var s = await http.GetStreamAsync(dl, cancellationToken))
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                    {
                        await s.CopyToAsync(fs, cancellationToken);
                    }

                    if (IsLfsPointer(tmp))
                    {
                        string mediaUrl = dl.Replace("raw.githubusercontent.com", "media.githubusercontent.com/media");
                        using (var s2 = await http.GetStreamAsync(mediaUrl, cancellationToken))
                        using (var fs2 = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                        {
                            await s2.CopyToAsync(fs2, cancellationToken);
                        }
                    }

                    if (IsLfsPointer(tmp) || new FileInfo(tmp).Length == 0)
                    {
                        try { File.Delete(tmp); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                        failures++;
                        RuntimeLog.Fail(logTag, $"Cloud sync: '{name}' skipped (still an LFS pointer / empty after fetch).");
                        continue;
                    }

                    File.Move(tmp, dest, overwrite: true);
                    local.Add(name);
                    downloaded++;
                    RuntimeLog.Info(logTag, $"Cloud sync: downloaded '{name}' ({new FileInfo(dest).Length} bytes).");
                    progress?.Report(new SyncProgress(name, downloaded, total));
                }
                catch (OperationCanceledException)
                {
                    try { File.Delete(tmp); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                    throw;
                }
                catch (Exception exFile)
                {
                    try { File.Delete(tmp); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                    failures++;
                    RuntimeLog.Fail(logTag, $"Cloud sync: '{name}' failed: {exFile.Message}");
                }
            }

            RuntimeLog.Info(logTag, $"Cloud sync complete: {downloaded} new file(s), {failures} failure(s).");

            if (downloaded == 0 && failures > 0)
            {
                return (0, $"All {failures} download(s) failed. Check your internet connection and try again.");
            }
            if (failures > 0)
            {
                return (downloaded, $"{downloaded} downloaded, but {failures} file(s) could not be fetched.");
            }
            return (downloaded, null);
        }
        catch (OperationCanceledException)
        {
            RuntimeLog.Info(logTag, $"Cloud sync cancelled by the user after {downloaded} file(s).");
            return (downloaded, null);
        }
        catch (HttpRequestException ex)
        {
            RuntimeLog.Fail(logTag, $"Cloud sync network failure: {ex.Message}");
            return (downloaded, "Could not reach GitHub. Check your internet connection and try again.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail(logTag, $"Cloud sync failed: {ex.Message}");
            return (downloaded, $"The download could not be completed: {ex.Message}");
        }
    }
}
