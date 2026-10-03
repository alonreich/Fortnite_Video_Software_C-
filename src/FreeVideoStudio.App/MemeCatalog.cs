// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.App.Infrastructure;

namespace FreeVideoStudio.App;

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
    /// DOWNLOAD_01 / MEMECAT_01 — which kind of meme this action row fetches. Null on a real meme.
    /// Songs have their own button in the Music Wizard.
    /// The two used to be a SINGLE row that pulled every meme, so a user who wanted one more
    /// reaction image had to download every video meme too. Both kinds now live in ONE repository
    /// folder (`meme/`, MEMEFOLDER_01) and the category filters it by extension.
    /// </summary>
    public MemeCategory? DownloadCategory { get; init; }

    public override string ToString() => FileName;
}

/// <summary>
/// MEMECAT_01 — the two kinds of meme the library holds. Replaces the "mp4" / "jpeg" strings that
/// were compared in three files and only meant something while the repository had one folder per
/// kind.
/// </summary>
public enum MemeCategory
{
    Video,
    Image,
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
    private const string CloudRepo = "Free_Video_Studio";

    /// <summary>
    /// MEMEFOLDER_01 — the ONE repository folder holding every meme, video and picture alike.
    /// It replaced the separate `mp4/` and `jpeg/` folders. Clients older than this change still
    /// ask for those folders and get a "not found" error — updating the app is the fix.
    /// </summary>
    public const string CloudMemeFolder = "meme";

    /// <summary>MEMECAT_01 — the file extensions that belong to a meme category.</summary>
    public static string[] ExtensionsFor(MemeCategory category) =>
        category == MemeCategory.Image ? ImageExts : VideoExts;

    /// <summary>MEMECAT_01 — user-facing plural label for a category.</summary>
    public static string LabelFor(MemeCategory category) =>
        category == MemeCategory.Image ? "meme pictures" : "meme videos";

    /// <summary>
    /// ISSUE_10 — repo folder holding the song library.
    /// project_structure.txt documents mp3\ as an identically LFS-distributed asset folder, but
    /// the sync only ever covered mp4 and jpeg, so there was no way for a user to get the songs
    /// — the music library was bring-your-own with no in-app path to the shared collection.
    /// </summary>
    private const string CloudSongFolder = "mp3";

    /// <summary>ISSUE_11 — per-file progress for the sync UI.</summary>
    /// <param name="FileName">The file currently being fetched.</param>
    /// <param name="Completed">How many files have finished so far.</param>
    /// <param name="Total">Total files this sync will fetch (0 until the listing completes).</param>
    public readonly record struct SyncProgress(string FileName, int Completed, int Total);

    /// <summary>
    /// §1 File Ingestion: scans the ACTIVE meme directory for supported formats, skipping
    /// zero-byte files, and resolves each file's native dimensions (ffprobe for videos,
    /// SkiaSharp for images). Runs fully off the UI thread.
    /// §3 Exception Handling: UnauthorizedAccessException propagates so the Settings flow
    /// can block the path change and revert; all other per-file errors are swallowed.
    ///
    /// <para>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// MEMESCAN_01 — WHY THIS IS THREE PHASES INSTEAD OF ONE LOOP.
    ///
    /// WHAT WAS WRONG: a single foreach spawned ONE ffprobe child process per video, STRICTLY
    /// SERIALLY, via <c>new MediaProber(...).GetResolutionAsync().GetAwaiter().GetResult()</c>.
    /// Three separate costs compounded:
    ///
    ///   1. Serial process launches. ~100 ms each on Windows (CreateProcess + image load + ffprobe's
    ///      own demuxer probe), and a single unreadable or network-backed file cost the full
    ///      15-second MediaProber timeout on its own, blocking every file behind it.
    ///   2. No memoisation, despite MediaProber having some. A NEW MediaProber was constructed
    ///      inside the loop body and dropped on the next iteration, so its SemaphoreSlim-guarded
    ///      _probeData cache never served a single hit on this path.
    ///   3. Sync-over-async inside Task.Run. .GetAwaiter().GetResult() parks a thread-pool worker
    ///      for the whole probe. The pool injects roughly one thread per 500 ms, so a large scan
    ///      starved every other queued Task.Run in the app — filmstrip prewarm, size estimation,
    ///      loudness probes — behind it.
    ///
    /// And this path is re-entered on window Loaded (cold start), on every MemeDirectory.Changed,
    /// and immediately after a cloud sync — i.e. right when the library is at its largest.
    ///
    /// THE FIX: enumerate cheaply, resolve dimensions from a persisted (length, mtime)-keyed cache
    /// and probe only the misses under a bounded worker pool with real awaits, then assemble.
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </para>
    /// </summary>
    public static async Task<List<MemeItem>> ScanAsync(string directory, string ffprobePath)
    {
        long startTicks = Environment.TickCount64;

        // ── PHASE A: enumeration + cheap filtering ──────────────────────────────────────────
        List<ScanCandidate> candidates = await Task.Run(() => EnumerateCandidates(directory)).ConfigureAwait(false);
        if (candidates.Count == 0) return new List<MemeItem>();

        // ── PHASE B: dimensions — cache first, bounded-concurrency probe for the misses ──────
        MemeDimensionCache cache = MemeDimensionCache.Load();

        var misses = new List<ScanCandidate>();
        foreach (ScanCandidate c in candidates)
        {
            if (cache.TryGet(c.FullPath, c.Length, c.MTimeTicks, out int cachedW, out int cachedH))
            {
                c.Width = cachedW;
                c.Height = cachedH;
            }
            else
            {
                misses.Add(c);
            }
        }

        if (misses.Count > 0)
        {
            // MEMESCAN_01 — the ceiling exists because each lane is a live ffprobe PROCESS, not a
            // thread. Saturating every core with child processes during app start would fight the
            // preview decode the user is actually looking at.
            int lanes = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

            await Parallel.ForEachAsync(
                misses,
                new ParallelOptions { MaxDegreeOfParallelism = lanes },
                async (c, ct) =>
                {
                    try
                    {
                        if (c.IsImage)
                        {
                            using var codec = SkiaSharp.SKCodec.Create(c.FullPath);
                            if (codec != null)
                            {
                                c.Width = codec.Info.Width;
                                c.Height = codec.Info.Height;
                            }
                        }
                        else
                        {
                            var (w, h) = await new FreeVideoStudio.Core.Media.MediaProber(ffprobePath, c.FullPath)
                                .GetResolutionAsync().ConfigureAwait(false);
                            c.Width = w;
                            c.Height = h;
                        }
                    }
                    catch (Exception ex)
                    {
                        // §3 — one bad file may never fault the whole pass.
                        RuntimeLog.Info("Memes", $"Dimension probe failed for '{c.FileName}': {ex.Message}");
                    }
                }).ConfigureAwait(false);

            foreach (ScanCandidate c in misses)
            {
                // Put() ignores non-positive dimensions, so a failed probe is never cached and the
                // export-crash guard below can never be satisfied from stale data.
                cache.Put(c.FullPath, c.Length, c.MTimeTicks, c.Width, c.Height);
            }

            cache.Save();
        }

        // ── PHASE C: exclusion + assembly, in the PHASE A order ─────────────────────────────
        var items = new List<MemeItem>(candidates.Count);
        int excluded = 0;

        foreach (ScanCandidate c in candidates)
        {
            // ⚠️ LOAD-BEARING GUARD. A video with no usable geometry crashes the export filter
            // graph. Images are deliberately NOT subject to this — a Skia decode failure leaves a
            // usable item that simply has no aspect-ratio hint for the §2 UI guardrail.
            if (!c.IsImage && (c.Width <= 0 || c.Height <= 0))
            {
                RuntimeLog.Fail("Memes", $"Excluding unreadable video meme '{c.FileName}' (failed to probe; would crash export).");
                excluded++;
                continue;
            }

            items.Add(new MemeItem
            {
                FileName = c.FileName,
                FullPath = c.FullPath,
                IsImage = c.IsImage,
                Width = c.Width,
                Height = c.Height
            });
        }

        RuntimeLog.Info("Memes",
            $"Meme scan: {candidates.Count} candidate(s), {candidates.Count - misses.Count} from cache, " +
            $"{misses.Count} probed, {excluded} excluded, {Environment.TickCount64 - startTicks} ms.");

        return items;
    }

    /// <summary>MEMESCAN_01 — one file that survived filtering, carried through the three phases.</summary>
    private sealed class ScanCandidate
    {
        public string FileName = "";
        public string FullPath = "";
        public bool IsImage;
        public long Length;
        public long MTimeTicks;
        public int Width;
        public int Height;
    }

    /// <summary>
    /// MEMESCAN_01 — PHASE A. Pure filesystem work, no child processes, no decoding.
    /// Establishes the canonical ordering (filename, ordinal-ignore-case) that PHASE C re-imposes
    /// after the unordered parallel probe.
    /// </summary>
    private static List<ScanCandidate> EnumerateCandidates(string directory)
    {
        var candidates = new List<ScanCandidate>();
        if (!Directory.Exists(directory)) return candidates;

        // §3 — UnauthorizedAccessException from here PROPAGATES on purpose: the Settings
        // meme-folder change flow catches it to block the path change and revert. Do NOT wrap this
        // in a catch-all.
        string[] files = Directory.GetFiles(directory);

        foreach (string f in files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            bool isVideo = VideoExts.Contains(ext);
            bool isImage = ImageExts.Contains(ext);
            if (!isVideo && !isImage) continue;

            long length;
            long mtimeTicks;
            try
            {
                var fi = new FileInfo(f);
                length = fi.Length;
                mtimeTicks = fi.LastWriteTimeUtc.Ticks;
            }
            catch (System.Exception swallowed2)
            {
                // Unreadable metadata — same disposition as the original zero-byte guard: skip.
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed2);   // FAULTTIER_02 — no failure is silent.
                continue;
            }

            // ⚠️ LOAD-BEARING GUARD. Zero bytes means an unresolved Git-LFS pointer or a
            // half-finished download. It must be skipped BEFORE anything tries to probe it.
            if (length == 0) continue;

            candidates.Add(new ScanCandidate
            {
                FileName = Path.GetFileName(f),
                FullPath = f,
                IsImage = isImage,
                Length = length,
                MTimeTicks = mtimeTicks
            });
        }

        return candidates;
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
        catch (System.Exception swallowed3)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed3);   // FAULTTIER_02 — no failure is silent.
            return false;
        }
    }

    /// <summary>
    /// DOWNLOAD_01 / MEMEFOLDER_01 — downloads the missing memes of ONE category from the single
    /// repository `meme/` folder into <paramref name="targetDirectory"/>.
    /// </summary>
    public static Task<(int downloaded, string? error)> SyncMemesAsync(
        string targetDirectory,
        MemeCategory category,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => SyncFolderAsync(targetDirectory, CloudMemeFolder, ExtensionsFor(category),
                           category == MemeCategory.Image ? "Image memes" : "Video memes", progress, cancellationToken);

    /// <summary>
    /// ISSUE_10 — downloads missing SONGS (mp3 folder) into the user's music directory.
    /// Mirrors the meme sync exactly, including the Git-LFS pointer handling, because the mp3
    /// files are LFS-tracked too and would otherwise land as unplayable 130-byte text files.
    /// </summary>
    public static Task<(int downloaded, string? error)> SyncSongsFromCloudAsync(
        string targetDirectory,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => SyncFolderAsync(targetDirectory, CloudSongFolder, AudioExts, "Songs", progress, cancellationToken);

    /// <summary>
    /// Shared delta-sync engine.
    ///
    /// ISSUE_11 — progress (the listing is enumerated FIRST so a real total is known), cancellation
    /// (a cancelled transfer leaves no partial file behind), and distinct error messages.
    ///
    /// <para>
    /// MEMESYNC_01 — WHY THE LISTING USES THE GIT TREES API AND NOT /contents.
    /// The contents API returns at most 1,000 entries per folder and has no paging, so a library
    /// past 1,000 files silently stopped growing. `git/trees/HEAD:{folder}` returns up to 100,000
    /// entries in one call (and says so with `truncated` beyond that), and it reports each blob's
    /// SIZE, which MEMESYNC_03 below depends on.
    /// </para>
    /// <para>
    /// MEMESYNC_02 — A MISSING FOLDER IS AN ERROR, NOT "NOTHING NEW". A 404 on the listing used to
    /// be logged and skipped, so the sync reported zero files to fetch and the UI said "You already
    /// have everything" — a false statement the user had no way to see through. Any listing failure
    /// now returns an error message.
    /// </para>
    /// <para>
    /// MEMESYNC_03 — LFS FILES GO STRAIGHT TO THE LFS HOST. Media is Git-LFS tracked, so a raw
    /// download of it returns a ~130-byte pointer file. The old engine downloaded that pointer
    /// first, noticed, and then downloaded the real file — two requests per file. A blob whose
    /// listed size is at most <see cref="LfsPointerMaxBytes"/> is a pointer, so its first request
    /// goes to media.githubusercontent.com. The other host stays as the fallback, because
    /// media.githubusercontent.com answers 404 for a file that is NOT in LFS.
    /// </para>
    /// </summary>
    private static async Task<(int downloaded, string? error)> SyncFolderAsync(
        string targetDirectory,
        string cloudFolder,
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
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FreeVideoStudio");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            cancellationToken.ThrowIfCancellationRequested();

            string listingUrl = $"https://api.github.com/repos/{CloudOwner}/{CloudRepo}/git/trees/HEAD:{cloudFolder}";
            using var listingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            listingCts.CancelAfter(TimeSpan.FromSeconds(30));

            JsonArray tree;
            using (var resp = await http.GetAsync(listingUrl, listingCts.Token))
            {
                if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                    (int)resp.StatusCode == 429)
                {
                    RuntimeLog.Fail(logTag, $"Cloud sync halted: GitHub API rate limit (HTTP {(int)resp.StatusCode}).");
                    return (0, "GitHub is rate-limiting this connection right now. Try again in an hour.");
                }
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // MEMESYNC_02 — never "you already have everything".
                    RuntimeLog.Fail(logTag, $"Cloud sync: folder '{cloudFolder}' does not exist in the repository.");
                    return (0, "The online library could not be found. Update Free Video Studio to the latest version and try again.");
                }
                if (!resp.IsSuccessStatusCode)
                {
                    RuntimeLog.Fail(logTag, $"Cloud sync: listing '{cloudFolder}' failed with HTTP {(int)resp.StatusCode}.");
                    return (0, $"The online library could not be listed (HTTP {(int)resp.StatusCode}). Try again later.");
                }

                JsonNode? root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
                if (root?["tree"] is not JsonArray arr)
                {
                    RuntimeLog.Fail(logTag, $"Cloud sync: listing '{cloudFolder}' had no tree.");
                    return (0, "The online library answered with something unexpected. Try again later.");
                }
                if (root["truncated"]?.GetValue<bool>() == true)
                    RuntimeLog.Fail(logTag, $"Cloud sync: listing '{cloudFolder}' was truncated by GitHub; only the listed files are fetched.");
                tree = arr;
            }

            var pending = new List<(string Name, long Size)>();
            foreach (var node in tree)
            {
                string? name = node?["path"]?.ToString();
                string? type = node?["type"]?.ToString();
                if (name == null || type != "blob") continue;
                if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) continue;
                if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;

                string ext = Path.GetExtension(name).ToLowerInvariant();
                if (!acceptedExtensions.Contains(ext)) continue;

                string existing = Path.Combine(targetDirectory, name);
                if (local.Contains(name) && File.Exists(existing)
                    && new FileInfo(existing).Length > 0 && !IsLfsPointer(existing))
                    continue;

                long size = 0;
                try { size = node?["size"]?.GetValue<long>() ?? 0; }
                catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                pending.Add((name, size));
            }

            int total = pending.Count;
            RuntimeLog.Info(logTag, $"Cloud sync: {total} file(s) to fetch from '{cloudFolder}'.");
            progress?.Report(new SyncProgress(string.Empty, 0, total));

            if (total == 0) return (0, null);

            int failures = 0;
            foreach ((string name, long size) in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new SyncProgress(name, downloaded, total));

                string dest = Path.Combine(targetDirectory, name);
                string tmp = dest + ".part";
                string escaped = Uri.EscapeDataString(name);
                string rawUrl = $"https://raw.githubusercontent.com/{CloudOwner}/{CloudRepo}/HEAD/{cloudFolder}/{escaped}";
                string lfsUrl = $"https://media.githubusercontent.com/media/{CloudOwner}/{CloudRepo}/HEAD/{cloudFolder}/{escaped}";

                // MEMESYNC_03 — a pointer-sized blob is an LFS file: ask the LFS host first.
                bool lfsFirst = size > 0 && size <= LfsPointerMaxBytes;
                string[] urls = lfsFirst ? new[] { lfsUrl, rawUrl } : new[] { rawUrl, lfsUrl };

                try
                {
                    bool ok = false;
                    foreach (string url in urls)
                    {
                        if (await TryDownloadAsync(http, url, tmp, cancellationToken)) { ok = true; break; }
                    }

                    if (!ok)
                    {
                        try { File.Delete(tmp); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                        failures++;
                        RuntimeLog.Fail(logTag, $"Cloud sync: '{name}' could not be fetched from either host.");
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

    /// <summary>MEMESYNC_03 — a Git-LFS pointer file is ~130 bytes; anything this small is one.</summary>
    private const long LfsPointerMaxBytes = 1024;

    /// <summary>
    /// MEMESYNC_03 — one attempt against one host. True only when <paramref name="tmp"/> now holds
    /// real, non-empty content (not an LFS pointer). A 404 is a normal "not on this host" answer.
    /// </summary>
    private static async Task<bool> TryDownloadAsync(HttpClient http, string url, string tmp, CancellationToken ct)
    {
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!resp.IsSuccessStatusCode) return false;
            using var s = await resp.Content.ReadAsStreamAsync(ct);
            using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write);
            await s.CopyToAsync(fs, ct);
        }
        return new FileInfo(tmp).Length > 0 && !IsLfsPointer(tmp);
    }
}
