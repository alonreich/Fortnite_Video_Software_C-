// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace FortniteVideoSoftware.App.Infrastructure;

/// <summary>Where a meme plays relative to the gameplay.</summary>
public enum MemePlacement
{
    /// <summary>After the video. The conventional case and the default for anything unlisted.</summary>
    End = 0,
    /// <summary>Before the video — intro-style memes.</summary>
    Start = 1,
}

/// <summary>
/// MEME_02 — remembers, per meme file, whether the user wants it at the Start or the End.
///
/// ⚠️ WHY A REMEMBERED CHOICE RATHER THAN A NAMING RULE. The alternative was encoding it in the
/// filename (`… [start].mp4`), which is ugly in the user's folder, breaks the instant anyone
/// renames a file, and forces the app to rewrite files it does not own. This keeps the decision
/// in the app's own settings and leaves the user's media untouched.
///
/// THE PRECEDENCE IS DELIBERATE:
///   1. the user's own choice for this exact file, if they have ever made one — always wins;
///   2. otherwise the shipped default (MemeAssets.DefaultsToStart), which knows the bundled memes;
///   3. otherwise End, the safe conventional case, which is what every new/unknown meme gets.
/// Nothing can work out "this is an intro meme" from the video itself — it is a judgement about
/// meaning — so the only honest options are "the user said so" or "we shipped an opinion".
/// </summary>
public static class MemePlacementStore
{
    private const string FileName = "meme_placement.json";

    private static string StorePath =>
        Path.Combine(FortniteVideoSoftware.Core.Infrastructure.ApplicationPaths.CreateDefault().UiStateDirectory,
                     FileName);

    /// <summary>
    /// ATOMICSTATE_01 — the gate around <see cref="_cache"/>.
    ///
    /// <para>Every caller today is on the UI thread (<c>MainWindow.Export.cs</c>,
    /// <c>MainWindow.Wireup.cs</c>), but NOTHING in this type's signature, name or documentation
    /// said so, and the hazard is not a benign one: <see cref="_cache"/> is a plain
    /// <see cref="Dictionary{TKey,TValue}"/> that <see cref="Set"/> mutates in place while
    /// <see cref="Get"/> may be reading it, and the lazy publish at the end of
    /// <see cref="LoadUnlocked"/> was a non-atomic check-build-publish that two callers could run
    /// concurrently. A single background caller — a future export payload built off the UI
    /// thread, say — turns that into dictionary corruption or an InvalidOperationException raised
    /// from inside a failure path.</para>
    ///
    /// <para>⚠️ DISK I/O IS DELIBERATELY OUTSIDE THIS LOCK. <see cref="Get"/> is on the export
    /// payload path; a slow or contended disk in <see cref="Set"/> must never serialise it. The
    /// pattern is the one <c>MemeDimensionCache</c> already uses: build the snapshot under the
    /// lock, commit it outside.</para>
    /// </summary>
    private static readonly object _sync = new();

    private static Dictionary<string, MemePlacement>? _cache;

    /// <summary>
    /// ATOMICSTATE_01 — the caller MUST hold <see cref="_sync"/>. Returns the live cache
    /// dictionary, so every read of the returned map also has to happen under that lock.
    /// </summary>
    private static Dictionary<string, MemePlacement> LoadUnlocked()
    {
        if (_cache != null) return _cache;
        var map = new Dictionary<string, MemePlacement>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string path = StorePath;
            if (File.Exists(path))
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject obj)
                {
                    foreach (var kv in obj)
                    {
                        string? raw = kv.Value?.ToString();
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        // ⚠️ LENIENT BY CONTRACT. Files written by older builds carry "1", "Start"
                        // and "true" for the same meaning. Tightening this silently resets choices
                        // users have already made. Anything unrecognised is End, the safe default.
                        map[kv.Key] = raw.Equals("1", StringComparison.Ordinal) ||
                                      raw.Equals("Start", StringComparison.OrdinalIgnoreCase) ||
                                      raw.Equals("true", StringComparison.OrdinalIgnoreCase)
                            ? MemePlacement.Start
                            : MemePlacement.End;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("Meme", $"Could not read remembered meme placements: {ex.Message}");
        }
        _cache = map;
        return map;
    }

    /// <summary>Resolves where this meme should play. See the precedence note on the class.</summary>
    public static MemePlacement Get(string memePath)
    {
        if (string.IsNullOrWhiteSpace(memePath)) return MemePlacement.End;
        string key = Path.GetFileName(memePath);

        // ATOMICSTATE_01 — the lookup happens INSIDE the lock. Returning the dictionary and
        // probing it outside would reintroduce the exact read-during-mutation race.
        lock (_sync)
        {
            if (LoadUnlocked().TryGetValue(key, out var chosen)) return chosen;
        }

        return MemeAssets.DefaultsToStart(memePath)
            ? MemePlacement.Start
            : MemePlacement.End;
    }

    /// <summary>True when the shipped opinion disagrees with what the user is about to do.</summary>
    public static bool ContradictsShippedDefault(string memePath, MemePlacement chosen) =>
        MemeAssets.DefaultsToStart(memePath) && chosen == MemePlacement.End;

    /// <summary>
    /// Remembers the user's choice for this file. Their choice wins from now on.
    ///
    /// ATOMICSTATE_01 — the commit was <c>File.WriteAllText</c>, which opens the target with
    /// truncation. Because this method rewrites the WHOLE map on every call, a crash or power
    /// loss inside that window did not cost the one entry being changed — it cost EVERY
    /// remembered placement the user had ever set, leaving a correctly named but truncated or
    /// zero-length file behind. <see cref="AtomicJsonFile.WriteText"/> is the protocol
    /// <c>docs/05_SYSTEM_LIFECYCLE_STORAGE.md#SYS-RECOVERY</c> mandates and that every other
    /// state writer in this solution already uses: unique GUID temp file in the TARGET directory
    /// opened WriteThrough, <c>Flush(flushToDisk: true)</c>, then an atomic same-volume rename.
    ///
    /// ⚠️ THE ON-DISK VALUES STAY "Start" / "End", not 1/0 and not booleans, so a file written
    /// here is still readable by an older build.
    ///
    /// ⚠️ A FAILED WRITE LEAVES THE IN-MEMORY CHOICE APPLIED. That is the pre-existing behaviour
    /// and it is kept deliberately: the user's click is honoured for this session, and the next
    /// successful Set rewrites the whole map and heals the divergence.
    /// </summary>
    public static void Set(string memePath, MemePlacement placement)
    {
        if (string.IsNullOrWhiteSpace(memePath)) return;
        try
        {
            string payload;

            // Snapshot under the gate; commit to disk outside it. See _sync.
            lock (_sync)
            {
                var map = LoadUnlocked();
                map[Path.GetFileName(memePath)] = placement;

                var obj = new JsonObject();
                foreach (var kv in map) obj[kv.Key] = kv.Value == MemePlacement.Start ? "Start" : "End";
                payload = obj.ToJsonString();
            }

            string path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            FortniteVideoSoftware.Core.Infrastructure.AtomicJsonFile.WriteText(path, payload);
            RuntimeLog.Info("Meme", $"Remembered '{Path.GetFileName(memePath)}' plays at the {placement}.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("Meme", $"Could not remember the meme placement: {ex.Message}");
        }
    }
}
