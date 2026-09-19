// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;

namespace FortniteVideoSoftware.App.Infrastructure;

/// <summary>
/// TRACKSEARCH_01 — the music-library filter predicate, lifted out of <c>MusicWizardWindow</c>.
///
/// Three private statics on a 5,782-line class in which the identifier <c>Phase</c> appears 322
/// times. Matching a search box against a track is not a wizard-phase concern and needs no window
/// to test: given a query and a track, does it match. Bodies moved verbatim.
///
/// ⚠️ MATCHING IS DELIBERATELY FORGIVING. Users type part of an artist, part of a title, or a
/// fragment in the wrong case. Tightening this to a prefix or exact match would make the library
/// feel broken rather than precise.
/// </summary>
internal static class TrackSearch
{
    internal static string NormalizeSearchQuery(string query)
    {
        return (query ?? string.Empty).Trim().Replace("*", string.Empty, StringComparison.Ordinal);
    }

    internal static bool ContainsIgnoreCase(string source, string query)
    {
        return !string.IsNullOrEmpty(source) &&
            source.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TrackMatchesSearch(MusicTrackItem track, string rawQuery)
    {
        string query = NormalizeSearchQuery(rawQuery);
        if (query.Length == 0)
            return true;

        return ContainsIgnoreCase(track.Title, query)
            || ContainsIgnoreCase(Path.GetFileNameWithoutExtension(track.Name), query)
            || ContainsIgnoreCase(track.Name, query)
            || ContainsIgnoreCase(track.Artist, query)
            || ContainsIgnoreCase(track.Album, query);
    }
}
