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

    /// <summary>
    /// PROJ_03 — the top-level keys this build understands. Anything else found on read is carried
    /// in <see cref="ProjectDocument.UnknownFields"/> and written back out untouched, so an older
    /// build cannot amputate a newer build's data by opening and saving a file.
    /// </summary>
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        KeyFormat, KeySchema, KeyTitle, KeyCreated, KeyModified, KeySource, KeyBaseSpeed,
        KeyCutStart, KeyTrimmed, KeySegments, KeyCuts, KeyMemes, KeyAudio, KeyExport,
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
            SizeBytes = (long)ReadDouble(sourceObj, "size_bytes", 0),
            ModifiedUtcSeconds = (long)ReadDouble(sourceObj, "modified_utc_seconds", 0),
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

    private static double ReadDouble(JsonObject? o, string key, double fallback)
    {
        if (o == null || o[key] is not JsonValue v) return fallback;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out string? s) && double.TryParse(s, out double parsed)) return parsed;
        return fallback;
    }

    private static double? ReadNullableDouble(JsonObject? o, string key)
    {
        if (o == null || o[key] is not JsonValue v) return null;
        return v.TryGetValue(out double d) ? d : null;
    }

    private static int ReadInt(JsonObject? o, string key, int fallback)
    {
        if (o == null || o[key] is not JsonValue v) return fallback;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out double d)) return (int)Math.Round(d);
        return fallback;
    }

    private static int? ReadNullableInt(JsonObject? o, string key)
    {
        if (o == null || o[key] is not JsonValue v) return null;
        if (v.TryGetValue(out int i)) return i;
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
