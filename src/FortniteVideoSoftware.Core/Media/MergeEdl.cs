// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/06_PROJECT_DOCUMENT_MODEL.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FortniteVideoSoftware.Core.Media;

// ══════════════════════════════════════════════════════════════════════════════════════════════
// MERGEEDL_01 — THE VIDEO MERGER'S EDIT DECISION LIST (Video-Merger-Migration.md P2.1).
//
// The whole merge is DATA: which clips, in which order, which part of each, and which effects.
// Nothing is rendered until MERGE. Preview, music, undo, autosave and export all read this one
// object, so they cannot disagree.
//
// TIME UNIT: SOURCE MICROSECONDS (long) inside each clip's own file. Not seconds (double drift),
// and not frame indexes (gameplay captures can be variable frame rate, so "frame 600" has no fixed
// time). P2.2 snaps every stored microsecond to a REAL frame timestamp of that file.
//
// ANCHORS: everything that must survive a layout change (reorder, scraper toggle, custom
// thumbnail) is stored as (ClipId, SourceUs). ClipId is a GUID given when a clip enters the queue,
// so the same file added twice gets two identities.
//
// EQUALITY: records that hold lists implement structural Equals/GetHashCode by hand. A record's
// generated Equals compares lists BY REFERENCE, which silently breaks UndoStack's "no-op edit"
// detection (see UNDOEQ_01 in ProjectDocument).
// ══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Where a meme sits inside its clip (user decision D4).</summary>
public enum EdlMemePlacement
{
    /// <summary>Inserted at <see cref="EdlMeme.AtUs"/> inside the clip.</summary>
    Mid = 0,
    /// <summary>Plays before the clip; the clip (and its fade-in) starts right after the meme.</summary>
    AtStart = 1,
    /// <summary>Inserted where the clip's fade-out begins (or at the clip's end when there is no fade info).</summary>
    AtEnd = 2,
}

/// <summary>A point inside one clip: (clip identity, microseconds in that clip's source file).</summary>
public readonly record struct EdlAnchor(Guid ClipId, long SourceUs);

/// <summary>
/// Zoom/pan rectangle in the SOURCE pixels of its clip, recorded with that clip's frame size
/// (<paramref name="SourceW"/> x <paramref name="SourceH"/>) — exactly what the granular editor draws on.
/// D10 (zoom applies to what the viewer sees) is honoured at export: P7.1 maps the rectangle through
/// the clip's fit onto the output canvas. Storing source px (not canvas px) keeps the zoom correct if
/// the user later switches the output canvas between portrait and landscape (P6.3 decision).
/// </summary>
public sealed record EdlZoom(int X, int Y, int W, int H, bool Slow, long? StartUs = null, long? EndUs = null, int SourceW = 0, int SourceH = 0);

/// <summary>A speed segment inside one clip (mirrors the Main App's <see cref="SpeedSegment"/>, in source µs).</summary>
public sealed record EdlSpeedSegment(long StartUs, long EndUs, double Speed, EdlZoom? Zoom = null);

/// <summary>A freeze frame inside one clip: hold the frame at <see cref="AtUs"/> for <see cref="DurationSec"/> output seconds.</summary>
public sealed record EdlFreeze(long AtUs, double DurationSec);

/// <summary>A meme inside one clip.</summary>
public sealed record EdlMeme(string Id, string FilePath, EdlMemePlacement Placement, long AtUs, double DurationSec);

/// <summary>A removed part of one clip (D18, DELETE PARTS), in that clip's source µs.</summary>
public readonly record struct EdlCut(long StartUs, long EndUs);

/// <summary>
/// All effects of one clip. Every effect stays inside its clip (user decision D4).
/// EDLNULL_01 — list/object properties are NULL-PROOF (an init that receives null keeps the empty default,
/// so the backing fields that record equality compares are never null): the source-generated JSON reader
/// assigns default(T) to every init property a file does not mention, so a file written before a field existed (e.g. `Cuts`,
/// added in P6.2) came back with null lists. Every Equals on that edit list then threw, the Merger's
/// autosave stopped updating (the queue on disk froze), and a later IsEmpty crashed the app.
/// </summary>
public sealed record EdlEffects
{
    private readonly IReadOnlyList<EdlSpeedSegment> _speed = Array.Empty<EdlSpeedSegment>();
    public IReadOnlyList<EdlSpeedSegment> Speed { get => _speed; init => _speed = value ?? Array.Empty<EdlSpeedSegment>(); }
    private readonly IReadOnlyList<EdlFreeze> _freezes = Array.Empty<EdlFreeze>();
    public IReadOnlyList<EdlFreeze> Freezes { get => _freezes; init => _freezes = value ?? Array.Empty<EdlFreeze>(); }
    private readonly IReadOnlyList<EdlMeme> _memes = Array.Empty<EdlMeme>();
    public IReadOnlyList<EdlMeme> Memes { get => _memes; init => _memes = value ?? Array.Empty<EdlMeme>(); }
    private readonly IReadOnlyList<EdlCut> _cuts = Array.Empty<EdlCut>();
    public IReadOnlyList<EdlCut> Cuts { get => _cuts; init => _cuts = value ?? Array.Empty<EdlCut>(); }

    public static readonly EdlEffects None = new();

    [JsonIgnore]
    public bool IsEmpty => Speed.Count == 0 && Freezes.Count == 0 && Memes.Count == 0 && Cuts.Count == 0;

    public bool Equals(EdlEffects? other)
        => other is not null
           && Speed.SequenceEqual(other.Speed)
           && Freezes.SequenceEqual(other.Freezes)
           && Memes.SequenceEqual(other.Memes)
           && Cuts.SequenceEqual(other.Cuts);

    public override int GetHashCode() => HashCode.Combine(EdlHash.Of(Speed, Freezes, Memes), EdlHash.Of(Cuts, Array.Empty<int>(), Array.Empty<int>()));
}

/// <summary>
/// One queued clip. <see cref="InUs"/>/<see cref="OutUs"/> are the USER's window in source µs
/// (OutUs 0 = end of file). Intro removal is NOT baked in here: it is derived from
/// <see cref="Timing"/> + the scraper/thumbnail rules when the timeline is built, so toggling the
/// scraper never rewrites clips.
/// </summary>
public sealed record EdlClip
{
    public Guid ClipId { get; init; } = Guid.NewGuid();
    private readonly string _path = string.Empty;
    public string Path { get => _path; init => _path = value ?? string.Empty; }

    /// <summary>File identity at the time it was queued (detects a changed/replaced file on restore).</summary>
    public long SizeBytes { get; init; }
    public long LastWriteUtcTicks { get; init; }

    /// <summary>Probed length of the file in µs (0 = not analysed yet).</summary>
    public long DurationUs { get; init; }

    public long InUs { get; init; }
    public long OutUs { get; init; }

    /// <summary>TIMINGTAG_02 timing read from the file (intro + fades), null when untagged.</summary>
    public ExportTiming? Timing { get; init; }

    /// <summary>
    /// FRAMESNAP_01 — real pts (µs) of the first frame after the tagged intro, probed in the
    /// background. 0 = not probed; <see cref="CompositeTimeline"/> then falls back to <see cref="Timing"/>.
    /// </summary>
    public long IntroCutUs { get; init; }

    private readonly EdlEffects _effects = EdlEffects.None;
    public EdlEffects Effects { get => _effects; init => _effects = value ?? EdlEffects.None; }
}

/// <summary>The custom thumbnail: the frame at this anchor becomes the merged video's cover still.</summary>
public sealed record EdlThumbnail(EdlAnchor At);

/// <summary>The music bed, anchored to video moments so speed edits never move it off them (D8).</summary>
public sealed record EdlMusic
{
    private readonly IReadOnlyList<string> _filePaths = Array.Empty<string>();
    public IReadOnlyList<string> FilePaths { get => _filePaths; init => _filePaths = value ?? Array.Empty<string>(); }
    private readonly IReadOnlyList<double> _durationsSec = Array.Empty<double>();
    public IReadOnlyList<double> DurationsSec { get => _durationsSec; init => _durationsSec = value ?? Array.Empty<double>(); }
    public double OffsetSec { get; init; }
    public EdlAnchor Start { get; init; }
    public EdlAnchor End { get; init; }
    public double MusicVolume { get; init; } = 1.0;
    public double VideoVolume { get; init; } = 1.0;
    public bool Loop { get; init; }
    public bool Ducking { get; init; } = true;
    public bool Carving { get; init; } = true;

    public bool Equals(EdlMusic? other)
        => other is not null
           && FilePaths.SequenceEqual(other.FilePaths)
           && DurationsSec.SequenceEqual(other.DurationsSec)
           && OffsetSec == other.OffsetSec
           && Start == other.Start && End == other.End
           && MusicVolume == other.MusicVolume && VideoVolume == other.VideoVolume
           && Loop == other.Loop && Ducking == other.Ducking && Carving == other.Carving;

    public override int GetHashCode()
        => HashCode.Combine(EdlHash.Of(FilePaths, DurationsSec, Array.Empty<int>()), OffsetSec, Start, End, MusicVolume, VideoVolume, HashCode.Combine(Loop, Ducking, Carving));
}

/// <summary>The whole merge.</summary>
public sealed record MergeEdl
{
    /// <summary>Schema version of this record. Bump with a migration in P3.1 when the shape changes.</summary>
    public int Version { get; init; } = 1;

    private readonly IReadOnlyList<EdlClip> _clips = Array.Empty<EdlClip>();
    public IReadOnlyList<EdlClip> Clips { get => _clips; init => _clips = value ?? Array.Empty<EdlClip>(); }
    public bool ScraperEnabled { get; init; } = true;
    public double BaseSpeed { get; init; } = 1.0;
    public EdlThumbnail? Thumbnail { get; init; }
    public EdlMusic? Music { get; init; }

    public static readonly MergeEdl Empty = new();

    public bool Equals(MergeEdl? other)
        => other is not null
           && Version == other.Version
           && Clips.SequenceEqual(other.Clips)
           && ScraperEnabled == other.ScraperEnabled
           && BaseSpeed == other.BaseSpeed
           && Equals(Thumbnail, other.Thumbnail)
           && Equals(Music, other.Music);

    public override int GetHashCode()
        => HashCode.Combine(Version, EdlHash.Of(Clips, Array.Empty<int>(), Array.Empty<int>()), ScraperEnabled, BaseSpeed, Thumbnail, Music);

    /// <summary>Index of the clip with <paramref name="clipId"/>, or -1.</summary>
    public int IndexOf(Guid clipId)
    {
        for (int i = 0; i < Clips.Count; i++) if (Clips[i].ClipId == clipId) return i;
        return -1;
    }

    public string ToJson() => JsonSerializer.Serialize(this, MergeEdlJsonContext.Default.MergeEdl);

    /// <summary>Parses JSON; null when the text is not a valid EDL (never throws).</summary>
    public static MergeEdl? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            // EDLNULL_01 — the source-generated reader assigns default(T) to every init property the
            // file does not mention (it does not run initializers). A file written before a field
            // existed would silently load e.g. ScraperEnabled=false, BaseSpeed=0, volumes=0. So every
            // object is first completed with the missing keys from a freshly constructed default.
            if (JsonNode.Parse(json) is not JsonObject root) return null;
            FillMissing(root, Template.Edl);
            if (root["Clips"] is JsonArray clips)
            {
                foreach (var c in clips)
                {
                    if (c is not JsonObject clip) continue;
                    FillMissing(clip, Template.Clip, "ClipId");
                    if (clip["ClipId"] is null) clip["ClipId"] = Guid.NewGuid().ToString();
                    if (clip["Effects"] is JsonObject fx) FillMissing(fx, Template.Effects);
                }
            }
            if (root["Music"] is JsonObject music) FillMissing(music, Template.Music);
            return root.Deserialize(MergeEdlJsonContext.Default.MergeEdl);
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static void FillMissing(JsonObject target, JsonObject defaults, string? skip = null)
    {
        foreach (var kv in defaults)
        {
            if (kv.Key == skip || target.ContainsKey(kv.Key)) continue;
            target[kv.Key] = kv.Value?.DeepClone();
        }
    }

    /// <summary>Default objects, serialized once, used to complete older files (EDLNULL_01).</summary>
    private static class Template
    {
        public static readonly JsonObject Edl = Of(JsonSerializer.SerializeToNode(new MergeEdl(), MergeEdlJsonContext.Default.MergeEdl));
        public static readonly JsonObject Clip = Of(JsonSerializer.SerializeToNode(new EdlClip(), MergeEdlJsonContext.Default.EdlClip));
        public static readonly JsonObject Effects = Of(JsonSerializer.SerializeToNode(new EdlEffects(), MergeEdlJsonContext.Default.EdlEffects));
        public static readonly JsonObject Music = Of(JsonSerializer.SerializeToNode(new EdlMusic(), MergeEdlJsonContext.Default.EdlMusic));

        private static JsonObject Of(JsonNode? node)
        {
            var o = node as JsonObject ?? new JsonObject();
            o.Remove("Clips");   // a template never contributes content, only defaults for missing scalars/lists
            return o;
        }
    }
}

internal static class EdlHash
{
    public static int Of<TA, TB, TC>(IReadOnlyList<TA> a, IReadOnlyList<TB> b, IReadOnlyList<TC> c)
    {
        var h = new HashCode();
        h.Add(a.Count); foreach (var x in a) h.Add(x);
        h.Add(b.Count); foreach (var x in b) h.Add(x);
        h.Add(c.Count); foreach (var x in c) h.Add(x);
        return h.ToHashCode();
    }
}

/// <summary>AOT-safe (source-generated) JSON for <see cref="MergeEdl"/>. Enums as strings for readable autosave files.</summary>
[JsonSourceGenerationOptions(WriteIndented = false, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MergeEdl))]
[JsonSerializable(typeof(EdlClip))]
[JsonSerializable(typeof(EdlEffects))]
[JsonSerializable(typeof(EdlMusic))]
public partial class MergeEdlJsonContext : JsonSerializerContext { }
