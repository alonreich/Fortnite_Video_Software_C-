using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Project;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// RECOVERYDOC_08 - ONE-WAY legacy recovery migration: old recovery_v2 payload - canonical
/// <see cref="ProjectDocument"/>.
/// </summary>
/// <remarks>
/// WHY A READER AND NOT A WRITER: users arriving from a crash written by an older build have a
/// legacy <c>schemaVersion:1</c> payload on disk. It is parsed ONCE, reconstructed into a real
/// document, applied through the normal restore path, and the very next autosave overwrites the
/// file in the canonical envelope format. The legacy shape is never written again - maintaining
/// two writers is precisely the defect this mission removes.
/// <para>
/// DOCUMENTED LIMITATIONS (the legacy schema genuinely never carried these): Mask (the legacy
/// payload recorded HUD toggle positions but not the mask snapshot, so a migrated session falls
/// back to whatever mask is machine-active now, exactly as the old restore did); Merge/EDL (the
/// merger queue lived in the merger window and was never in the legacy payload); and the source
/// fingerprint (size/mtime/fps/width/height were never recorded - the migrated document records
/// the path and the measured duration only, so source-integrity checks report Unknown instead of
/// nagging). Everything the legacy payload DID carry - trims, base speed, speed segments with
/// zoom boxes, cuts, meme placements, music bed and voice-over placement, export quality and
/// portrait mode - is reconstructed faithfully.
/// </para>
/// <para>
/// NO REFLECTION: JsonNode only (PROJ_02 / AOTSAFETY_01).
/// </para>
/// </remarks>
public static class LegacyRecoveryMigrator
{
    /// <summary>
    /// Converts a legacy payload. Returns a document plus the crash-only transient metadata that
    /// sat beside the project fields in the old file. False with <paramref name="error"/> set
    /// means the payload carried no project worth restoring.
    /// </summary>
    public static bool TryMigrate(
        JsonObject legacy,
        out ProjectDocument? document,
        out JsonObject? transientMetadata,
        out string? error)
    {
        document = null;
        transientMetadata = null;
        error = null;

        int schemaVersion = ReadInt(legacy, "schemaVersion", 0);
        if (schemaVersion < 1)
        {
            error = $"Legacy recovery schema version {schemaVersion} is outdated or missing.";
            return false;
        }

        string? videoPath = ReadString(legacy, "loadedVideoPath");
        if (string.IsNullOrWhiteSpace(videoPath))
        {
            error = "The legacy recovery payload recorded no video.";
            return false;
        }

        var transient = new JsonObject();
        foreach (string key in LegacyTransientKeys)
        {
            if (legacy[key] is { } node)
                transient[key] = node.DeepClone();
        }
        transientMetadata = transient;

        document = BuildDocument(legacy, videoPath!);
        return true;
    }

    private static ProjectDocument BuildDocument(JsonObject legacy, string videoPath)
    {
        double trimStartMs = ReadDouble(legacy, "trimStartMs", 0);
        bool trimStartSet = ReadBool(legacy, "trimStartSet", false);
        double trimEndMs = ReadDouble(legacy, "trimEndMs", 0);
        bool trimEndSet = ReadBool(legacy, "trimEndSet", false);
        double cutStartMs = trimStartSet ? trimStartMs : 0;

        double baseSpeed = ReadDouble(legacy, "baseSpeed", 1.1);
        if (double.IsNaN(baseSpeed) || double.IsInfinity(baseSpeed) || baseSpeed < 0.1 || baseSpeed > 4.0)
            baseSpeed = 1.1;

        return new ProjectDocument
        {
            Source = new SourceClip
            {
                FilePath = videoPath,
                DurationMs = trimEndSet && trimEndMs > 0 ? trimEndMs : 0,
            },
            Title = System.IO.Path.GetFileNameWithoutExtension(videoPath),
            BaseSpeed = baseSpeed,
            SourceCutStartMs = cutStartMs,
            TrimmedDurationMs = trimEndSet && trimEndMs > cutStartMs ? trimEndMs - cutStartMs : 0,
            Segments = ReadSegments(legacy),
            Cuts = ReadCuts(legacy, cutStartMs),
            Memes = ReadMemes(legacy),
            Audio = ReadAudio(legacy),
            Export = new ProjectExport
            {
                QualityIndex = ReadInt(legacy, "qualitySliderValue", -1),
                PortraitMode = ReadBool(legacy, "portraitMode", true),
                HardwareMode = "Auto",
            },
        };
    }

    private static List<SpeedSegment> ReadSegments(JsonObject legacy)
    {
        var segments = new List<SpeedSegment>();
        if (legacy["speedSegments"] is not JsonArray segArray) return segments;
        foreach (JsonNode? node in segArray)
        {
            if (node is not JsonObject o) continue;
            double start = ReadDouble(o, "startMs", double.NaN);
            double end = ReadDouble(o, "endMs", double.NaN);
            if (double.IsNaN(start) || double.IsNaN(end) || end <= start) continue;
            segments.Add(new SpeedSegment(
                start, end, ReadDouble(o, "speed", 1.0),
                ReadNullableInt(o, "zoomX"), ReadNullableInt(o, "zoomY"),
                ReadNullableInt(o, "zoomW"), ReadNullableInt(o, "zoomH"),
                ReadString(o, "zoomOrigRes"), ReadBool(o, "zoomSlow", false),
                ReadNullableDouble(o, "zoomStartMs"), ReadNullableDouble(o, "zoomEndMs")));
        }
        return segments;
    }

    /// <summary>
    /// Legacy cuts are ABSOLUTE source milliseconds; the document stores CLIP-RELATIVE seconds
    /// (OutputTimeline.Cut) - the same conversion ProjectSession.Capture performs.
    /// </summary>
    private static List<OutputTimeline.Cut> ReadCuts(JsonObject legacy, double cutStartMs)
    {
        var cuts = new List<OutputTimeline.Cut>();
        if (legacy["cuts"] is not JsonArray cutArray) return cuts;
        foreach (JsonNode? node in cutArray)
        {
            if (node is not JsonObject o) continue;
            double start = ReadDouble(o, "startMs", double.NaN);
            double end = ReadDouble(o, "endMs", double.NaN);
            if (double.IsNaN(start) || double.IsNaN(end) || end <= start) continue;
            cuts.Add(new OutputTimeline.Cut(
                Math.Max(0, (start - cutStartMs) / 1000.0),
                Math.Max(0, (end - cutStartMs) / 1000.0)));
        }
        return cuts;
    }

    private static List<MemePlacement> ReadMemes(JsonObject legacy)
    {
        var memes = new List<MemePlacement>();
        if (legacy["memePlacements"] is not JsonArray memeArray) return memes;
        int i = 0;
        foreach (JsonNode? node in memeArray)
        {
            if (node is not JsonObject o) continue;
            string? path = ReadString(o, "path");
            double at = ReadDouble(o, "atSourceSec", -1);
            double dur = ReadDouble(o, "durationSec", 0);
            string? id = ReadString(o, "id");
            if (string.IsNullOrWhiteSpace(path) || at < 0 || dur <= 0.01) continue;
            memes.Add(new MemePlacement(
                path!, at, dur,
                string.IsNullOrWhiteSpace(id) ? MemePlacement.NewId(i) : id!));
            i++;
        }
        return memes;
    }

    private static ProjectAudio ReadAudio(JsonObject legacy)
    {
        JsonObject? music = legacy["musicResult"] as JsonObject;
        return new ProjectAudio
        {
            MusicFilePath = ReadString(music, "musicFilePath"),
            MusicStartSec = ReadDouble(music, "offsetSeconds", 0),
            MusicVolume = ReadDouble(music, "musicVolume", 1.0),
            VideoVolume = ReadDouble(music, "videoVolume", 1.0),
            SidechainDucking = ReadBool(music, "enableDucking", false),
            VoiceOverFilePath = ReadString(legacy, "voiceOverWavPath"),
            VoiceOverAtOutputSec = ReadDouble(legacy, "voiceOverStartSec", 0),
            VoiceOverVolume = 1.0,
        };
    }


    /// is reconstructed into the ProjectDocument instead - this list is the inventory of what
    /// stayed transient, never a second project schema.
    /// </summary>
    private static readonly string[] LegacyTransientKeys =
    {
        "thumbnailPosMs", "thumbnailSet", "freezeTimeMs", "freezeDurationS",
        "isGranularSpeedActive", "volume", "showTeammates", "showSpectating",
        "enableFade", "addMeme", "memeFile", "memeFilePath", "portraitText",
        "voiceOverTakes", "voiceOverDuckAudio", "voiceOverProtectFromMusic",
        "musicResult",
    };

    private static string? ReadString(JsonObject? o, string key)
        => o is null || o[key] is not JsonValue v ? null
        : v.TryGetValue(out string? s) ? s : o[key]?.ToString();

    private static double ReadDouble(JsonObject? o, string key, double fallback)
    {
        if (o is null || o[key] is not JsonValue v) return fallback;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out decimal m)) return (double)m;
        if (v.TryGetValue(out string? s) && double.TryParse(s,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double parsed)) return parsed;
        return fallback;
    }

    private static double? ReadNullableDouble(JsonObject? o, string key)
    {
        if (o is null || o[key] is not JsonValue v) return null;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out decimal m)) return (double)m;
        return null;
    }

    private static int? ReadNullableInt(JsonObject? o, string key)
    {
        if (o is null || o[key] is not JsonValue v) return null;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out long l)) return (int)l;
        if (v.TryGetValue(out double d)) return (int)Math.Round(d);
        return null;
    }

    private static int ReadInt(JsonObject? o, string key, int fallback)
    {
        if (o is null || o[key] is not JsonValue v) return fallback;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out long l)) return (int)l;
        if (v.TryGetValue(out double d)) return (int)Math.Round(d);
        return fallback;
    }

    private static bool ReadBool(JsonObject? o, string key, bool fallback)
        => o is null || o[key] is not JsonValue v ? fallback
        : v.TryGetValue(out bool b) ? b : fallback;
}
