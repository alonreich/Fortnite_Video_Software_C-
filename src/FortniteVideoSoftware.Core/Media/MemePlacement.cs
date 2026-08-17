using System;
using System.Collections.Generic;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// MEME_03 — ONE MEME, PLACED SOMEWHERE IN THE VIDEO.
///
/// <para>
/// Before this type the app modelled a meme as a single <c>string? MemeFile</c> plus a
/// <c>bool MemeAtStart</c>, so exactly one meme could exist and it could only sit at the very
/// beginning or the very end. D1 and D2 replace that: several memes, each at a chosen moment,
/// each a CUTAWAY — the gameplay pauses, the meme plays in full, the gameplay resumes from the
/// exact frame it stopped on, and the finished video grows by the meme's length.
/// </para>
///
/// <para>
/// <b><see cref="AtSourceSecRelative"/> is CLIP-RELATIVE SOURCE seconds</b> — measured from the
/// trim-in point, not from the start of the original file, and NOT a position in the finished
/// video. It is the moment of gameplay the meme interrupts. Converting to a position in the
/// finished video is <see cref="OutputTimeline"/>'s job and must not be done by hand.
/// </para>
///
/// <para>
/// ⚠️ THE POINT MUST ALREADY BE SNAPPED. D8 forbids a meme interrupting a freeze or a speed
/// segment, so the UI is required to pass the click through
/// <see cref="OutputTimeline.SnapInsertionPoint"/> and to draw its marker at the snapped value.
/// Storing the raw click would drop the meme somewhere the user never saw.
/// </para>
///
/// <para>
/// ⚠️ TWO MEMES MAY NOT SHARE A POINT (D7). Validate with
/// <see cref="OutputTimeline.HasInsertionAtSource"/> BEFORE accepting a placement and tell the
/// user a meme already sits there, rather than silently stacking them.
/// </para>
///
/// <para>
/// <see cref="Id"/> is a stable handle used to follow this placement through the timeline model
/// (<see cref="OutputTimeline.InsertionAt"/>) and into the FFmpeg graph's stream labels. It must be
/// unique within one export and safe to embed in a filter label, so it is generated, never typed.
/// </para>
///
/// <para>
/// <see cref="DurationSec"/> is resolved BEFORE the graph is built — probed for a video meme, or
/// the fixed still-image duration for a .jpg/.png. The timeline cannot be laid out without it,
/// because every downstream position depends on how much time this meme occupies.
/// </para>
/// </summary>
public sealed record MemePlacement(
    string FilePath,
    double AtSourceSecRelative,
    double DurationSec,
    string Id)
{
    /// <summary>Still images have no intrinsic duration; this is what they are given instead.</summary>
    public const double StillImageDurationSec = 4.0;

    /// <summary>
    /// Generates a filter-graph-safe identifier. Never derive one from the file name — meme files
    /// are user-supplied and routinely contain spaces, quotes, commas and brackets, every one of
    /// which breaks an FFmpeg filter label.
    /// </summary>
    public static string NewId(int index) => $"meme{index}";

    /// <summary>
    /// MEME_03 — BACK-COMPATIBILITY SHIM. Converts the legacy single-meme fields into the new list
    /// so that a payload written before this change still exports identically.
    /// <paramref name="clipDurationSecRelative"/> is the trimmed clip length; an END meme is placed
    /// at that instant, a START meme at 0.
    /// </summary>
    public static List<MemePlacement> FromLegacy(
        string? memeFile, bool memeAtStart, double durationSec, double clipDurationSecRelative)
    {
        var list = new List<MemePlacement>();
        if (string.IsNullOrWhiteSpace(memeFile) || durationSec <= 0) return list;

        list.Add(new MemePlacement(
            memeFile!,
            memeAtStart ? 0.0 : Math.Max(0.0, clipDurationSecRelative),
            durationSec,
            NewId(0)));
        return list;
    }

    /// <summary>
    /// Projects placements into the timeline model's own insertion type. Kept here rather than on
    /// <see cref="OutputTimeline"/> so the Core media model stays unaware of file paths.
    /// </summary>
    public static List<OutputTimeline.Insertion> ToInsertions(IReadOnlyList<MemePlacement>? placements)
    {
        var result = new List<OutputTimeline.Insertion>();
        if (placements == null) return result;

        foreach (var p in placements)
        {
            if (p.DurationSec <= 0.001) continue;
            result.Add(new OutputTimeline.Insertion(p.AtSourceSecRelative, p.DurationSec, p.Id));
        }
        return result;
    }
}
