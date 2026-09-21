// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/06_PROJECT_DOCUMENT_MODEL.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.Core.Project;

/// <summary>
/// PROJ_02 — TURNS A <see cref="ProjectDocument"/> INTO JSON AND BACK, WITHOUT REFLECTION.
///
/// <para>
/// ⚠️ WHY THIS IS HAND-WRITTEN AND NOT <c>JsonSerializer.Serialize(document)</c>: the product ships
/// as a NativeAOT single binary with <c>TrimMode=full</c>. Reflection-based System.Text.Json is not
/// AOT-safe — it compiles, it passes in Debug, and it throws <c>NotSupportedException</c> only in
/// the published .exe on the user's machine. The codebase already avoids this correctly
/// (<c>IpcJsonContext</c> uses source generation; <c>AtomicJsonFile</c> works in
/// <see cref="JsonObject"/>). A user's saved work is the last place in the application that may
/// depend on a mechanism that fails silently after publish, so this reader and writer touch nothing
/// but <see cref="JsonNode"/>.
/// </para>
///
/// <para>
/// ⚠️ THE READER IS DELIBERATELY FORGIVING AND THE WRITER IS DELIBERATELY EXPLICIT. Every
/// <c>Read*</c> helper below takes a default and never throws on a missing, null or wrong-typed
/// key. This is what makes "add a new optional field" a non-breaking change: an older file simply
/// does not have the key and gets the default. The one thing that IS refused is a schema number
/// from the future beyond what <see cref="ProjectDocument.SchemaVersion"/> understands — see
/// <see cref="Read"/>.
/// </para>
/// </summary>
public static class ProjectSerializer
{
    private const string KeyFormat = "format";
    private const string KeySchema = "schema_version";
    private const string KeyTitle = "title";
    private const string KeyCreated = "created_utc";
    private const string KeyModified = "modified_utc";
    private const string KeySource = "source";
    private const string KeyBaseSpeed = "base_speed";
    private const string KeyCutStart = "source_cut_start_ms";
    private const string KeyTrimmed = "trimmed_duration_ms";
    private const string KeySegments = "segments";
    private const string KeyCuts = "cuts";
    private const string KeyMemes = "memes";
    private const string KeyAudio = "audio";
    private const string KeyExport = "export";

    // PROJ_11 — schema 2. Both optional on read, so a v1 file loads with them absent.
    private const string KeyMask = "mask";
    private const string KeyMerge = "merge";

    /// <summary>
    /// PROJ_03 — the top-level keys this build understands. Anything else found on read is carried
    /// in <see cref="ProjectDocument.UnknownFields"/> and written back out untouched, so an older
    /// build cannot amputate a newer build's data by opening and saving a file.
    /// </summary>
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        KeyFormat, KeySchema, KeyTitle, KeyCreated, KeyModified, KeySource, KeyBaseSpeed,
        KeyCutStart, KeyTrimmed, KeySegments, KeyCuts, KeyMemes, KeyAudio, KeyExport,
        KeyMask, KeyMerge,
    };

    public static JsonObject Write(ProjectDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        JsonObject root = new();

        // PROJ_03 — unknown keys go down FIRST so that a known key always wins on a collision. If a
        // future build promotes one of these to a real field, this build's value for it is the one
        // that survives, which is the conservative direction.
        if (doc.UnknownFields != null)
        {
            foreach (KeyValuePair<string, JsonNode?> kv in doc.UnknownFields)
            {
                if (KnownKeys.Contains(kv.Key)) continue;
                root[kv.Key] = kv.Value?.DeepClone();
            }
        }

        root[KeyFormat] = ProjectDocument.FormatId;
        root[KeySchema] = ProjectDocument.SchemaVersion;
        root[KeyTitle] = doc.Title;
        root[KeyCreated] = doc.CreatedUtc.ToUnixTimeSeconds();
        root[KeyModified] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        root[KeySource] = new JsonObject
        {
            ["path"] = doc.Source.FilePath,
            ["duration_ms"] = doc.Source.DurationMs,
            ["width"] = doc.Source.Width,
            ["height"] = doc.Source.Height,
            ["fps"] = doc.Source.Fps,
            ["size_bytes"] = doc.Source.SizeBytes,
            ["modified_utc_seconds"] = doc.Source.ModifiedUtcSeconds,
        };

        root[KeyBaseSpeed] = doc.BaseSpeed;
        root[KeyCutStart] = doc.SourceCutStartMs;
        root[KeyTrimmed] = doc.TrimmedDurationMs;

        JsonArray segments = new();
        foreach (SpeedSegment s in doc.Segments)
        {
            JsonObject o = new()
            {
                ["start_ms"] = s.StartMs,
                ["end_ms"] = s.EndMs,
                ["speed"] = s.Speed,
                ["zoom_slow"] = s.ZoomSlow,
            };
            // Zoom fields are nullable as a set: a segment either carries a zoom box or it does
            // not. Writing explicit nulls would bloat every file for the common case, so they are
            // omitted and the reader treats absence as "no zoom".
            if (s.ZoomX.HasValue) o["zoom_x"] = s.ZoomX.Value;
            if (s.ZoomY.HasValue) o["zoom_y"] = s.ZoomY.Value;
            if (s.ZoomW.HasValue) o["zoom_w"] = s.ZoomW.Value;
            if (s.ZoomH.HasValue) o["zoom_h"] = s.ZoomH.Value;
            if (s.ZoomOrigRes != null) o["zoom_orig_res"] = s.ZoomOrigRes;
            if (s.ZoomStartMs.HasValue) o["zoom_start_ms"] = s.ZoomStartMs.Value;
            if (s.ZoomEndMs.HasValue) o["zoom_end_ms"] = s.ZoomEndMs.Value;
            segments.AddNode(o);   // AOTSAFETY_02
        }
        root[KeySegments] = segments;

        JsonArray cuts = new();
        foreach (OutputTimeline.Cut c in doc.Cuts)
            cuts.AddNode(new JsonObject { ["start_sec"] = c.StartSec, ["end_sec"] = c.EndSec });   // AOTSAFETY_02
        root[KeyCuts] = cuts;

        JsonArray memes = new();
        foreach (MemePlacement m in doc.Memes)
        {
            memes.AddNode(new JsonObject   // AOTSAFETY_02
            {
                ["path"] = m.FilePath,
                ["at_source_sec"] = m.AtSourceSecRelative,
                ["duration_sec"] = m.DurationSec,
                ["id"] = m.Id,
            });
        }
        root[KeyMemes] = memes;

        root[KeyAudio] = new JsonObject
        {
            ["music_path"] = doc.Audio.MusicFilePath,
            ["music_start_sec"] = doc.Audio.MusicStartSec,
            ["music_volume"] = doc.Audio.MusicVolume,
            ["video_volume"] = doc.Audio.VideoVolume,
            ["sidechain_ducking"] = doc.Audio.SidechainDucking,
            ["voice_over_path"] = doc.Audio.VoiceOverFilePath,
            ["voice_over_at_output_sec"] = doc.Audio.VoiceOverAtOutputSec,
            ["voice_over_volume"] = doc.Audio.VoiceOverVolume,
        };

        root[KeyExport] = new JsonObject
        {
            ["quality_index"] = doc.Export.QualityIndex,
            ["target_megabytes"] = doc.Export.TargetMegabytes,
            ["hardware_mode"] = doc.Export.HardwareMode,
            ["output_directory"] = doc.Export.OutputDirectory,
            ["portrait_mode"] = doc.Export.PortraitMode,
        };

        // PROJ_11 — the HUD mask. Absent, not null, when there is none: a v1 reader parks unknown
        // keys in UnknownFields, and an explicit null there would be carried back out as a null
        // "mask" key that a v2 reader then has to distinguish from "no mask". Omission is cleaner
        // and the reader treats both the same way.
        if (doc.Mask is { } mask)
        {
            root[KeyMask] = new JsonObject
            {
                ["profile_name"] = mask.ProfileName,
                ["fingerprint"] = mask.Fingerprint,
                // DeepClone, because the document is immutable and the caller keeps its instance.
                // Handing the live JsonObject to the writer would let a later edit of the config
                // mutate a document already on the undo stack.
                ["config"] = mask.Config?.DeepClone(),
            };
        }

        // PROJ_11 — the merge queue.
        if (doc.Merge is { HasClips: true } merge)
        {
            JsonArray clips = new();
            foreach (MergeClip c in merge.Clips)
            {
                // AOTSAFETY_06 — the local is typed JsonNode so this binds to JsonArray.Add(JsonNode?)
                // and NOT to the generic Add<T>, which carries RequiresUnreferencedCode /
                // RequiresDynamicCode and would emit IL2026 + IL3050. PROJ-AOT is explicit that trim
                // and AOT warnings here are FIXED, never suppressed: the warning is the analyser
                // correctly pointing out that a generic JsonValue.Create path cannot survive
                // TrimMode=full. The rest of this file already avoids it by construction; this call
                // site is new, so it had to be told.
                JsonNode clip = new JsonObject
                {
                    ["path"] = c.FilePath,
                    ["start_sec"] = c.StartSec,
                    ["end_sec"] = c.EndSec,
                };
                clips.Add(clip);
            }

            root[KeyMerge] = new JsonObject
            {
                ["base_speed"] = merge.BaseSpeed,
                ["clips"] = clips,
            };
        }

        return root;
    }

    /// <summary>
    /// PROJ_02 — parses a document. Returns <see langword="null"/> and sets <paramref name="error"/>
    /// when the payload is not a project this build can honour.
    /// <para>
    /// The two refusals are deliberate and are NOT the same as a parse failure: a file that is not
    /// a project at all, and a project written by a future build whose schema this one may
    /// misinterpret. Both are reported in words the UI can show verbatim. Everything else — missing
    /// keys, nulls, wrong types inside a known key — is absorbed with defaults, because a project
    /// that opens with one setting reset is worth infinitely more to a user than a project that
    /// refuses to open.
    /// </para>
    /// </summary>
    public static ProjectDocument? Read(JsonObject? root, out string? error)
    {
        error = null;
        if (root == null)
        {
            error = "That file is empty or is not valid JSON.";
            return null;
        }

        string? format = ReadString(root, KeyFormat, null);
        if (!string.Equals(format, ProjectDocument.FormatId, StringComparison.Ordinal))
        {
            error = "That is not a project file.";
            return null;
        }

        int schema = ReadInt(root, KeySchema, 1);
        if (schema > ProjectDocument.SchemaVersion)
        {
            error =
                $"This project was saved by a newer version of the app (format {schema}; " +
                $"this build understands {ProjectDocument.SchemaVersion}). Update the app to open it.";
            return null;
        }
        if (schema < ProjectDocument.MinimumReadableSchemaVersion)
        {
            error = $"This project uses a retired format (version {schema}) that this build can no longer read.";
            return null;
        }

        JsonObject? sourceObj = root[KeySource] as JsonObject;
        string sourcePath = ReadString(sourceObj, "path", string.Empty) ?? string.Empty;

        SourceClip clip = new()
        {
            FilePath = sourcePath,
            DurationMs = ReadDouble(sourceObj, "duration_ms", 0),
            Width = ReadInt(sourceObj, "width", 0),
            Height = ReadInt(sourceObj, "height", 0),
            Fps = ReadDouble(sourceObj, "fps", 0),
            SizeBytes = ReadLong(sourceObj, "size_bytes", 0),
            ModifiedUtcSeconds = ReadLong(sourceObj, "modified_utc_seconds", 0),
        };

        List<SpeedSegment> segments = new();
        if (root[KeySegments] is JsonArray segArray)
        {
            foreach (JsonNode? node in segArray)
            {
                if (node is not JsonObject o) continue;
                double start = ReadDouble(o, "start_ms", double.NaN);
                double end = ReadDouble(o, "end_ms", double.NaN);
                if (double.IsNaN(start) || double.IsNaN(end)) continue;

                segments.Add(new SpeedSegment(
                    start,
                    end,
                    ReadDouble(o, "speed", 1.0),
                    ReadNullableInt(o, "zoom_x"),
                    ReadNullableInt(o, "zoom_y"),
                    ReadNullableInt(o, "zoom_w"),
                    ReadNullableInt(o, "zoom_h"),
                    ReadString(o, "zoom_orig_res", null),
                    ReadBool(o, "zoom_slow", false),
                    ReadNullableDouble(o, "zoom_start_ms"),
                    ReadNullableDouble(o, "zoom_end_ms")));
            }
        }

        List<OutputTimeline.Cut> cuts = new();
        if (root[KeyCuts] is JsonArray cutArray)
        {
            foreach (JsonNode? node in cutArray)
            {
                if (node is not JsonObject o) continue;
                double start = ReadDouble(o, "start_sec", double.NaN);
                double end = ReadDouble(o, "end_sec", double.NaN);
                if (double.IsNaN(start) || double.IsNaN(end)) continue;
                cuts.Add(new OutputTimeline.Cut(start, end));
            }
        }

        List<MemePlacement> memes = new();
        if (root[KeyMemes] is JsonArray memeArray)
        {
            foreach (JsonNode? node in memeArray)
            {
                if (node is not JsonObject o) continue;
                string? path = ReadString(o, "path", null);
                string? id = ReadString(o, "id", null);
                // A meme without a path cannot be rendered and a meme without an id cannot be
                // addressed in the filter graph. Either one missing makes the entry meaningless,
                // so it is dropped rather than carried as a half-placement the user cannot see.
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(id)) continue;

                memes.Add(new MemePlacement(
                    path!,
                    ReadDouble(o, "at_source_sec", 0),
                    ReadDouble(o, "duration_sec", MemePlacement.StillImageDurationSec),
                    id!));
            }
        }

        JsonObject? audioObj = root[KeyAudio] as JsonObject;
        ProjectAudio audio = new()
        {
            MusicFilePath = ReadString(audioObj, "music_path", null),
            MusicStartSec = ReadDouble(audioObj, "music_start_sec", 0),
            MusicVolume = ReadDouble(audioObj, "music_volume", 1.0),
            VideoVolume = ReadDouble(audioObj, "video_volume", 1.0),
            SidechainDucking = ReadBool(audioObj, "sidechain_ducking", false),
            VoiceOverFilePath = ReadString(audioObj, "voice_over_path", null),
            VoiceOverAtOutputSec = ReadDouble(audioObj, "voice_over_at_output_sec", 0),
            VoiceOverVolume = ReadDouble(audioObj, "voice_over_volume", 1.0),
        };

        JsonObject? exportObj = root[KeyExport] as JsonObject;
        ProjectExport export = new()
        {
            QualityIndex = ReadInt(exportObj, "quality_index", -1),
            TargetMegabytes = ReadNullableDouble(exportObj, "target_megabytes"),
            HardwareMode = ReadString(exportObj, "hardware_mode", "Auto") ?? "Auto",
            OutputDirectory = ReadString(exportObj, "output_directory", null),
            PortraitMode = ReadBool(exportObj, "portrait_mode", true),
        };

        // PROJ_11 — the HUD mask. Absent in every v1 file, so null is the normal answer, not a fault.
        ProjectMask? mask = null;
        if (root[KeyMask] is JsonObject maskObj)
        {
            JsonObject? cfg = maskObj["config"] as JsonObject;
            string profile = ReadString(maskObj, "profile_name", string.Empty) ?? string.Empty;

            // ⚠️ The stored fingerprint is not trusted over the stored config. If a hand-edited file
            // carries a fingerprint that does not describe its own config, the CONFIG is the work
            // and the fingerprint is the checksum — recompute it, so the "is the live profile still
            // the one this was built with" test compares like with like.
            string stored = ReadString(maskObj, "fingerprint", string.Empty) ?? string.Empty;
            string actual = ProjectMask.ComputeFingerprint(cfg);

            mask = new ProjectMask(
                profile,
                string.IsNullOrEmpty(stored) || !string.Equals(stored, actual, StringComparison.Ordinal) ? actual : stored,
                cfg is null ? null : (JsonObject)cfg.DeepClone());
        }

        // PROJ_11 — the merge queue. An entry with no path is dropped rather than restored as an
        // empty row the user has to find and delete.
        ProjectMerge? merge = null;
        if (root[KeyMerge] is JsonObject mergeObj)
        {
            List<MergeClip> clips = new();
            if (mergeObj["clips"] is JsonArray clipArray)
            {
                foreach (JsonNode? node in clipArray)
                {
                    if (node is not JsonObject o) continue;
                    string clipPath = ReadString(o, "path", string.Empty) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(clipPath)) continue;
                    clips.Add(new MergeClip(clipPath, ReadDouble(o, "start_sec", 0), ReadDouble(o, "end_sec", 0)));
                }
            }

            if (clips.Count > 0)
                merge = new ProjectMerge { Clips = clips, BaseSpeed = ReadDouble(mergeObj, "base_speed", 1.0) };
        }

        JsonObject? unknown = null;
        foreach (KeyValuePair<string, JsonNode?> kv in root)
        {
            if (KnownKeys.Contains(kv.Key)) continue;
            unknown ??= new JsonObject();
            unknown[kv.Key] = kv.Value?.DeepClone();
        }

        return new ProjectDocument
        {
            Source = clip,
            Title = ReadString(root, KeyTitle, "Untitled") ?? "Untitled",
            CreatedUtc = ReadTimestamp(root, KeyCreated),
            ModifiedUtc = ReadTimestamp(root, KeyModified),
            BaseSpeed = ReadDouble(root, KeyBaseSpeed, 1.0),
            SourceCutStartMs = ReadDouble(root, KeyCutStart, 0),
            TrimmedDurationMs = ReadDouble(root, KeyTrimmed, 0),
            Mask = mask,
            Merge = merge,
            Segments = segments,
            Cuts = cuts,
            Memes = memes,
            Audio = audio,
            Export = export,
            UnknownFields = unknown,
        };
    }

    // ── Tolerant readers ────────────────────────────────────────────────────────────────────────
    // Every one of these absorbs a missing key, an explicit null, and a value of the wrong JSON
    // type. That last case is the one that matters: hand-edited and half-written files exist, and
    // a InvalidOperationException thrown from deep inside a load is indistinguishable to the user
    // from the app losing their work.

    private static string? ReadString(JsonObject? o, string key, string? fallback)
    {
        if (o == null || o[key] is not JsonValue v) return fallback;
        return v.TryGetValue(out string? s) ? s : fallback;
    }

    /// <summary>
    /// PROJ_10 — EVERY NUMERIC READ TRIES EVERY NUMERIC BACKING, BECAUSE A <c>JsonValue</c> KNOWS
    /// WHAT CLR TYPE IT WAS BUILT FROM AND WILL NOT CONVERT.
    ///
    /// <para>
    /// ⚠️ THE BUG THIS CLOSES WAS SILENT DATA LOSS IN THE SOURCE FINGERPRINT.
    /// <c>Write</c> stores <c>doc.Source.SizeBytes</c> — a <see cref="long"/> — so the node is a
    /// <c>JsonValue</c> backed by <see cref="long"/>. <c>Read</c> asked it for a
    /// <see cref="double"/>. <c>JsonValue.TryGetValue&lt;double&gt;</c> on a long-backed value
    /// returns <see langword="false"/>: it is an exact-type accessor, not a numeric converter.
    /// So the fallback won, and <c>SizeBytes</c> and <c>ModifiedUtcSeconds</c> came back as
    /// <b>0 on every load</b> — while the writer kept faithfully saving the real values.
    /// </para>
    ///
    /// <para>
    /// The damage was not the two fields. Those two fields ARE the source-integrity fingerprint
    /// (<c>PROJ_05</c> / §6 PROJ-INTEGRITY). <c>CheckSource</c> compared a real file's size against
    /// a stored 0 and answered <c>Changed</c> for every project ever reopened — so the warning that
    /// exists to tell a user their source clip was re-encoded or replaced fired constantly and
    /// meant nothing. A warning that is always on is a warning that is off.
    /// </para>
    ///
    /// <para>
    /// The reader stays forgiving (§2: absorb a missing key, a null, a wrong type) — it just no
    /// longer treats "stored as a different numeric type than I asked for" as absent.
    /// </para>
    /// </summary>
    private static double ReadDouble(JsonObject? o, string key, double fallback)
    {
        if (o == null || o[key] is not JsonValue v) return fallback;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out decimal m)) return (double)m;
        if (v.TryGetValue(out string? s) && double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double parsed)) return parsed;
        return fallback;
    }

    /// <summary>
    /// PROJ_10 — the 64-bit read. <c>(long)ReadDouble(...)</c> is not a substitute: a double carries
    /// 53 bits of mantissa, so a file size or a Unix timestamp past 2^53 would round on the way
    /// through. These two fields are compared for EQUALITY by the integrity check, and a value that
    /// rounds is a value that never matches.
    /// </summary>
    private static long ReadLong(JsonObject? o, string key, long fallback)
    {
        if (o == null || o[key] is not JsonValue v) return fallback;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out double d)) return (long)Math.Round(d);
        if (v.TryGetValue(out decimal m)) return (long)m;
        if (v.TryGetValue(out string? s) && long.TryParse(s, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out long parsed)) return parsed;
        return fallback;
    }

    /// <summary>PROJ_10 — see <see cref="ReadDouble"/>. Same exact-type trap, same widening.</summary>
    private static double? ReadNullableDouble(JsonObject? o, string key)
    {
        if (o == null || o[key] is not JsonValue v) return null;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out decimal m)) return (double)m;
        return null;
    }

    /// <summary>PROJ_10 — see <see cref="ReadDouble"/>. Same exact-type trap, same widening.</summary>
    private static int ReadInt(JsonObject? o, string key, int fallback)
    {
        if (o == null || o[key] is not JsonValue v) return fallback;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out long l)) return (int)l;
        if (v.TryGetValue(out double d)) return (int)Math.Round(d);
        if (v.TryGetValue(out string? s) && int.TryParse(s, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int parsed)) return parsed;
        return fallback;
    }

    /// <summary>PROJ_10 — see <see cref="ReadDouble"/>. Same exact-type trap, same widening.</summary>
    private static int? ReadNullableInt(JsonObject? o, string key)
    {
        if (o == null || o[key] is not JsonValue v) return null;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out long l)) return (int)l;
        if (v.TryGetValue(out double d)) return (int)Math.Round(d);
        return null;
    }

    private static bool ReadBool(JsonObject? o, string key, bool fallback)
    {
        if (o == null || o[key] is not JsonValue v) return fallback;
        return v.TryGetValue(out bool b) ? b : fallback;
    }

    private static DateTimeOffset ReadTimestamp(JsonObject? o, string key)
    {
        double seconds = ReadDouble(o, key, 0);
        if (seconds <= 0) return DateTimeOffset.UtcNow;
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)seconds);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            CoreLogger.Swallowed(ex);
            return DateTimeOffset.UtcNow;
        }
    }
}
