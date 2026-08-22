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

    private static Dictionary<string, MemePlacement>? _cache;

    private static Dictionary<string, MemePlacement> Load()
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

        if (Load().TryGetValue(key, out var chosen)) return chosen;
        return MemeAssets.DefaultsToStart(memePath)
            ? MemePlacement.Start
            : MemePlacement.End;
    }

    /// <summary>True when the shipped opinion disagrees with what the user is about to do.</summary>
    public static bool ContradictsShippedDefault(string memePath, MemePlacement chosen) =>
        MemeAssets.DefaultsToStart(memePath) && chosen == MemePlacement.End;

    /// <summary>Remembers the user's choice for this file. Their choice wins from now on.</summary>
    public static void Set(string memePath, MemePlacement placement)
    {
        if (string.IsNullOrWhiteSpace(memePath)) return;
        try
        {
            var map = Load();
            map[Path.GetFileName(memePath)] = placement;

            var obj = new JsonObject();
            foreach (var kv in map) obj[kv.Key] = kv.Value == MemePlacement.Start ? "Start" : "End";

            string path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, obj.ToJsonString());
            RuntimeLog.Info("Meme", $"Remembered '{Path.GetFileName(memePath)}' plays at the {placement}.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("Meme", $"Could not remember the meme placement: {ex.Message}");
        }
    }
}
