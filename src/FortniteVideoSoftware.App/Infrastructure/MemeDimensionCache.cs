// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.App.Infrastructure;

/// <summary>
/// MEMESCAN_01 — remembers the probed pixel dimensions of every meme file so the scan does not
/// launch an ffprobe process for a file it has already measured.
///
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// WHY THIS EXISTS. <c>MemeCatalog.ScanAsync</c> spawned ONE ffprobe child process per video meme,
/// strictly serially, with a 15-second timeout each, and it kept nothing. <c>MediaProber</c> does
/// memoise — but the scan allocated a fresh <c>MediaProber</c> inside the loop body and dropped it
/// on the next iteration, so that memoisation was dead code on this path. Every repopulate paid the
/// full cost again from zero.
///
/// The scan is re-entered on window <c>Loaded</c> (the cold-start critical path), on every
/// <c>MemeDirectory.Changed</c>, and immediately after a cloud sync — i.e. at the exact moment the
/// library has just grown to its largest. At roughly 100 ms per CreateProcess+probe on Windows, a
/// few hundred memes is a multi-second stall on app start, every start, forever.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </para>
///
/// <para>
/// <b>Invalidation is by (length, last-write-time), never by path alone.</b> A file that was
/// replaced in place — which is exactly what the cloud delta-sync does when it repairs a truncated
/// download — must be re-probed or the export would be handed stale geometry.
/// </para>
///
/// <para>
/// <b>⚠️ A cached non-positive dimension is treated as a MISS, never as a hit.</b>
/// <c>MemeCatalog</c> excludes any video whose probe returned <c>Width &lt;= 0</c> with the log line
/// "would crash export". If this cache could satisfy that guard from a stored zero, a file that
/// failed to probe once would be permanently, silently excluded — or worse, admitted with zero
/// geometry. Nothing useful is ever cached for a failed probe.
/// </para>
///
/// <para>
/// Written through <see cref="AtomicJsonFile.WriteObject"/> into the consolidated ProgramData root
/// (<c>uistate\</c>), per <c>docs/05_SYSTEM_LIFECYCLE_STORAGE.md#SYS-ATOMICWRITE</c> — never
/// <c>File.WriteAllText</c>, which is what leaves a correctly named but zero-filled file after a
/// power cut. It is inside the same root the installer's "preserve my settings" switch and the
/// uninstaller's zero-footprint sweep already know about, so neither needs a new special case.
/// </para>
///
/// <para>Every method is total: a corrupt, locked or missing cache degrades to "no hits" and the
/// scan simply probes. This is a performance cache and it may never fail an operation.</para>
/// </summary>
internal sealed class MemeDimensionCache
{
    private const int SchemaVersion = 1;
    private const string FileName = "meme_dimensions.json";

    /// <summary>
    /// MEMESCAN_01 — hard ceiling on retained entries, so an unbounded meme library cannot grow an
    /// unbounded cache file. Eviction is deterministic: prune entries whose file is gone, then keep
    /// the most recently used. A cache miss only ever costs one probe.
    /// </summary>
    private const int MaxEntries = 4000;

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    private sealed class Entry
    {
        public long Length;
        public long MTimeTicks;
        public int Width;
        public int Height;
        public long UsedTicks;
    }

    private static string CachePath
        => Path.Combine(ApplicationPaths.CreateDefault().UiStateDirectory, FileName);

    private MemeDimensionCache() { }

    public static MemeDimensionCache Load()
    {
        var cache = new MemeDimensionCache();
        try
        {
            JsonObject? root = AtomicJsonFile.ReadObject(CachePath);
            if (root == null) return cache;

            if (!root.TryGetPropertyValue("schema_version", out JsonNode? verNode) ||
                verNode is not JsonValue verVal ||
                !verVal.TryGetValue<int>(out int version) ||
                version != SchemaVersion)
            {
                // A different schema is discarded wholesale. This is a cache; re-probing is the
                // correct and cheap recovery, and a partial migration risks a stale dimension
                // reaching the export-crash guard.
                return cache;
            }

            if (root["entries"] is not JsonObject entries) return cache;

            foreach (KeyValuePair<string, JsonNode?> kv in entries)
            {
                if (kv.Value is not JsonObject e) continue;

                int w = ReadInt(e, "w");
                int h = ReadInt(e, "h");
                if (w <= 0 || h <= 0) continue;   // never resurrect a failed probe

                cache._entries[kv.Key] = new Entry
                {
                    Length = ReadLong(e, "len"),
                    MTimeTicks = ReadLong(e, "mtime"),
                    Width = w,
                    Height = h,
                    UsedTicks = ReadLong(e, "used")
                };
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }

        return cache;
    }

    /// <summary>
    /// True only when this exact file — same path, same length, same last-write-time — was measured
    /// before AND the stored measurement is usable (both dimensions positive).
    /// </summary>
    public bool TryGet(string fullPath, long length, long mTimeTicks, out int width, out int height)
    {
        width = 0;
        height = 0;

        lock (_sync)
        {
            if (!_entries.TryGetValue(fullPath, out Entry? e)) return false;
            if (e.Length != length || e.MTimeTicks != mTimeTicks) return false;
            if (e.Width <= 0 || e.Height <= 0) return false;

            e.UsedTicks = DateTime.UtcNow.Ticks;
            width = e.Width;
            height = e.Height;
            return true;
        }
    }

    /// <summary>Stores a SUCCESSFUL measurement. A non-positive dimension is silently ignored.</summary>
    public void Put(string fullPath, long length, long mTimeTicks, int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        lock (_sync)
        {
            _entries[fullPath] = new Entry
            {
                Length = length,
                MTimeTicks = mTimeTicks,
                Width = width,
                Height = height,
                UsedTicks = DateTime.UtcNow.Ticks
            };
            _dirty = true;
        }
    }

    /// <summary>
    /// Prunes entries whose file no longer exists, applies the <see cref="MaxEntries"/> ceiling by
    /// least-recently-used, and writes atomically. A no-op when nothing changed.
    /// </summary>
    public void Save()
    {
        try
        {
            List<KeyValuePair<string, Entry>> snapshot;
            lock (_sync)
            {
                if (!_dirty) return;
                _dirty = false;
                snapshot = new List<KeyValuePair<string, Entry>>(_entries);
            }

            // Pruning stats up to MaxEntries files. That is disk I/O and it is deliberately done
            // OUTSIDE the lock — TryGet is called from the scan's hot loop and must never queue
            // behind a few thousand File.Exists calls.
            var gone = new List<string>();
            for (int i = snapshot.Count - 1; i >= 0; i--)
            {
                bool missing;
                try { missing = !File.Exists(snapshot[i].Key); }
                catch (Exception ex) { RuntimeLog.Swallowed(ex); missing = false; }

                if (missing)
                {
                    gone.Add(snapshot[i].Key);
                    snapshot.RemoveAt(i);
                }
            }

            if (gone.Count > 0)
            {
                lock (_sync)
                {
                    foreach (string key in gone) _entries.Remove(key);
                }
            }

            if (snapshot.Count > MaxEntries)
            {
                snapshot.Sort(static (a, b) => b.Value.UsedTicks.CompareTo(a.Value.UsedTicks));
                snapshot.RemoveRange(MaxEntries, snapshot.Count - MaxEntries);
            }

            var entries = new JsonObject();
            foreach (KeyValuePair<string, Entry> kv in snapshot)
            {
                entries[kv.Key] = new JsonObject
                {
                    ["len"] = kv.Value.Length,
                    ["mtime"] = kv.Value.MTimeTicks,
                    ["w"] = kv.Value.Width,
                    ["h"] = kv.Value.Height,
                    ["used"] = kv.Value.UsedTicks
                };
            }

            var root = new JsonObject
            {
                ["schema_version"] = SchemaVersion,
                ["entries"] = entries
            };

            AtomicJsonFile.WriteObject(CachePath, root);
        }
        catch (Exception ex)
        {
            // A cache that cannot be written is a slower scan, never a failed one.
            RuntimeLog.Swallowed(ex);
        }
    }

    private static int ReadInt(JsonObject o, string key)
        => o.TryGetPropertyValue(key, out JsonNode? n) && n is JsonValue v && v.TryGetValue<int>(out int i) ? i : 0;

    private static long ReadLong(JsonObject o, string key)
        => o.TryGetPropertyValue(key, out JsonNode? n) && n is JsonValue v && v.TryGetValue<long>(out long l) ? l : 0;
}
