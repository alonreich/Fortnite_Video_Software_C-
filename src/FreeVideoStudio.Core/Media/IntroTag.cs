using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// SCRAPER_01 — THE HIDDEN "THIS FILE STARTS WITH A THUMBNAIL INTRO" TAG.
///
/// Every export that carries the 0.1 s still thumbnail intro (the first frame shown by SMS and
/// WhatsApp, so a shared clip never shows a black thumbnail) records the intro's exact length in
/// the container:
///
///     fvs_intro_sec = "0.100"
///
/// It is written as an MP4 mdta key (<c>-movflags use_metadata_tags</c>). Players, Explorer's
/// Details tab and share targets ignore it; ffprobe reports it under <c>format.tags</c>.
///
/// The Video Merger's Thumbnail Scraper trusts ONLY this tag. It never guesses from pixels: an
/// untagged file (anything exported before this build, or by another program) is merged as-is.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class IntroTag
{
    public const string Key = "fvs_intro_sec";

    /// <summary>The still-intro length every current export uses.</summary>
    public const double StandardIntroSec = 0.1;

    /// <summary>Largest value accepted as a genuine intro. Anything bigger is treated as corrupt.</summary>
    public const double MaxPlausibleIntroSec = 2.0;

    /// <summary><c>-movflags</c> value for every tagged MP4 output (faststart kept).</summary>
    public const string MovFlags = "+faststart+use_metadata_tags";

    /// <summary>
    /// The muxer arguments for an output file: the movflags pair, plus the tag pair when the file
    /// really starts with an intro. Always replaces a plain <c>-movflags +faststart</c> pair.
    /// </summary>
    public static string[] OutputArgs(double introSec)
        => introSec > 0.0005
            ? ["-metadata", $"{Key}={Format(introSec)}", "-movflags", MovFlags]
            : ["-movflags", "+faststart"];

    /// <summary>
    /// TIMINGTAG_02 — muxer arguments carrying BOTH the v1 seconds key (older Merger builds read it)
    /// and the frame-exact v2 <see cref="ExportTimingTag.Key"/>. Always uses mdta tags.
    /// </summary>
    public static string[] OutputArgs(ExportTiming timing)
    {
        var args = new System.Collections.Generic.List<string>(6);
        if (timing.IntroFrames > 0) args.AddRange(["-metadata", $"{Key}={Format(timing.IntroSec)}"]);
        args.AddRange(["-metadata", $"{ExportTimingTag.Key}={ExportTimingTag.Format(timing)}", "-movflags", MovFlags]);
        return args.ToArray();
    }

    public static string Format(double introSec) => introSec.ToString("F3", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads the tag from ffprobe's <c>format.tags</c> object. 0 when absent, unparsable, not
    /// positive, larger than <see cref="MaxPlausibleIntroSec"/>, or larger than half the file.
    /// </summary>
    public static double Read(JsonNode? formatTags, double fileDurationSec)
    {
        if (formatTags is not JsonObject tags) return 0;
        foreach (var kv in tags)
        {
            if (!string.Equals(kv.Key, Key, StringComparison.OrdinalIgnoreCase)) continue;
            return Validate(kv.Value?.ToString(), fileDurationSec);
        }
        return 0;
    }

    public static double Validate(string? raw, double fileDurationSec)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return 0;
        if (double.IsNaN(v) || v <= 0.0005 || v > MaxPlausibleIntroSec) return 0;
        if (fileDurationSec > 0 && v > fileDurationSec / 2.0) return 0;
        return v;
    }
}
