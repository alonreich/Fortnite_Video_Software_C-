using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>
/// LANECACHE_02 (Video-Merger-Migration.md P11) — the Merger's per-clip filmstrip frames (PNG) and
/// waveform peaks (float32) on disk, so a clip is decoded ONCE, ever: reopening the Merger, reordering,
/// resizing and restoring a session all paint from here. Files are named by a hash of the lane cache key
/// (file path + size + write time + kept window), so an edited source file simply misses. Pruned to
/// <see cref="MaxFiles"/> (oldest first) once per process, in the background. Every call is best-effort:
/// a cache failure is logged and treated as a miss, never as an error. Call only off the UI thread.
/// </summary>
internal static class LaneDiskCache
{
    private const int MaxFiles = 800;
    private static int _pruned;
    private static readonly Lazy<string> Dir = new(() => ApplicationPaths.CreateDefault().LaneCacheDirectory);

    public static string PathFor(string key, string extension)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(Dir.Value, Convert.ToHexString(hash, 0, 16) + extension);
    }

    public static byte[]? TryRead(string key, string extension)
    {
        try
        {
            string p = PathFor(key, extension);
            if (!File.Exists(p)) return null;
            try { File.SetLastWriteTimeUtc(p, DateTime.UtcNow); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            return File.ReadAllBytes(p);
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return null;
        }
    }

    public static void Write(string key, string extension, byte[] data)
    {
        try
        {
            Directory.CreateDirectory(Dir.Value);
            string p = PathFor(key, extension);
            string tmp = p + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, p, overwrite: true);
            PruneOnce();
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    private static void PruneOnce()
    {
        if (Interlocked.Exchange(ref _pruned, 1) != 0) return;
        try
        {
            var files = new DirectoryInfo(Dir.Value).GetFiles();
            if (files.Length <= MaxFiles) return;
            foreach (var f in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxFiles))
            {
                try { f.Delete(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
    }
}
