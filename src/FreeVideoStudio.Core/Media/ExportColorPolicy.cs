// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// COLOR_01 — the colour description ffprobe reports for a video stream. Every field is the raw
/// ffprobe token (e.g. "smpte2084", "bt2020nc", "pc") or null when the file does not say.
/// </summary>
public sealed record VideoColorInfo(string? PixelFormat, string? Primaries, string? Transfer, string? Matrix, string? Range)
{
    public static readonly VideoColorInfo Unknown = new(null, null, null, null, null);

    /// <summary>PQ (HDR10 / HDR10+ / Dolby Vision base layer) or HLG.</summary>
    public bool IsHdr => Transfer is "smpte2084" or "arib-std-b67";

    /// <summary>Full-range ("pc"/JPEG) luma. yuvj* formats are full range by definition.</summary>
    public bool IsFullRange =>
        string.Equals(Range, "pc", StringComparison.OrdinalIgnoreCase)
        || (PixelFormat?.StartsWith("yuvj", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>An SD-era matrix explicitly declared (would be decoded with the wrong matrix if passed as BT.709).</summary>
    public bool IsBt601Matrix => Matrix is "bt470bg" or "smpte170m";

    public static VideoColorInfo FromStream(JsonNode? stream)
    {
        if (stream is null) return Unknown;
        static string? Read(JsonNode? n)
        {
            string? s = n?.ToString();
            return string.IsNullOrWhiteSpace(s) || s == "unknown" ? null : s;
        }
        return new VideoColorInfo(
            Read(stream["pix_fmt"]),
            Read(stream["color_primaries"]),
            Read(stream["color_transfer"]),
            Read(stream["color_space"]),
            Read(stream["color_range"]));
    }

    public override string ToString()
        => $"pix_fmt={PixelFormat ?? "?"} primaries={Primaries ?? "?"} transfer={Transfer ?? "?"} matrix={Matrix ?? "?"} range={Range ?? "?"}";
}

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// COLOR_01 — ONE DELIVERY TARGET: SDR, BT.709, LIMITED (TV) RANGE, AND SAYING SO IN THE FILE.
///
/// Before this the export had no colour handling at all. Every route only forced
/// <c>-pix_fmt yuv420p</c>, so:
///   • HDR captures (HEVC Main10, BT.2020 + PQ/HLG, which is what NVIDIA/AMD capture produce with
///     Windows HDR on) were squeezed to 8-bit with NO tone mapping. The result was grey,
///     washed-out footage or a file still flagged as PQ, while the mpv preview (which tone-maps
///     automatically) looked right. Preview ≠ export.
///   • full-range sources (a common OBS setting) kept full-range levels, which most social
///     platforms decode as limited range, so shadows crush and highlights clip;
///   • no output was tagged, so each player and platform guessed the matrix.
///
/// The policy:
///   • ALWAYS tag the output bt709 / bt709 / bt709 / tv (<see cref="OutputTagArgs"/>).
///   • SDR BT.709 limited range (the dominant case): no filter is added, so the GPU-resident route
///     stays zero-copy.
///   • Full range or a BT.601 matrix: one <c>colorspace</c> pass converts to BT.709 TV range.
///   • HDR: zscale → linear light → BT.709 primaries → Hable tone map → BT.709 TV range, when the
///     bundled FFmpeg has zscale. Without zscale no correct CPU tone map exists, so the export
///     proceeds and says so (Degraded), rather than failing.
///   • Any conversion filter is unknown to ExportVideoPipeline's CUDA substitution table, so an
///     HDR or full-range source automatically takes the CPU filter route. Correct colour beats a
///     fast wrong export.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class ExportColorPolicy
{
    /// <summary>Encoder-side colour description for every delivered file.</summary>
    public static readonly string[] OutputTagArgs =
        ["-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv"];

    /// <summary>HDR → SDR BT.709 via zscale (libzimg) + tonemap. Ends in 8-bit 4:2:0 TV range.</summary>
    public const string HdrToneMapChain =
        "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

    /// <summary>
    /// The filter chain (no pad labels) that brings <paramref name="info"/> to SDR BT.709 TV range,
    /// or null when the source already is. <paramref name="degradedReason"/> is set when a correct
    /// conversion was needed but is not possible with this FFmpeg build.
    /// </summary>
    public static string? BuildConversionChain(VideoColorInfo info, bool hasZscale, out string description, out string? degradedReason)
    {
        degradedReason = null;

        if (info.IsHdr)
        {
            if (hasZscale)
            {
                description = $"HDR source ({info.Transfer}) tone-mapped to SDR BT.709 (Hable, CPU).";
                return HdrToneMapChain;
            }

            description = $"HDR source ({info.Transfer}) exported WITHOUT tone mapping (bundled FFmpeg lacks zscale).";
            degradedReason =
                "This clip was recorded in HDR, and this build of the video engine cannot convert HDR colours, " +
                "so the export may look washed out. Trimming, speed, music and everything else are unaffected. " +
                "Recording with HDR off in your capture software avoids this.";
            return null;
        }

        if (info.IsFullRange || info.IsBt601Matrix)
        {
            string iall = info.Matrix switch
            {
                "bt470bg" => "bt601-6-625",
                "smpte170m" => "bt601-6-525",
                _ => "bt709",
            };
            string irange = info.IsFullRange ? "pc" : "tv";
            description = $"SDR source ({info}) converted to BT.709 TV range.";
            return $"colorspace=all=bt709:iall={iall}:irange={irange}:range=tv:format=yuv420p";
        }

        description = "SDR BT.709 source: colour passes through untouched (output tagged BT.709/TV).";
        return null;
    }

    private static readonly ConcurrentDictionary<string, Task<HashSet<string>>> FilterCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the FFmpeg at <paramref name="ffmpegPath"/> was built with <paramref name="filter"/>. Cached per binary.</summary>
    public static async Task<bool> HasFilterAsync(string ffmpegPath, string filter)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath)) return false;
        HashSet<string> filters = await FilterCache.GetOrAdd(ffmpegPath, ListFiltersAsync).ConfigureAwait(false);
        return filters.Contains(filter);
    }

    private static async Task<HashSet<string>> ListFiltersAsync(string ffmpegPath)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-filters");
            var result = await AsyncProcessRunner.RunAsync(psi, timeout: TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            foreach (string raw in result.StandardOutput.Split('\n'))
            {
                // " T.C zscale            V->V       Apply resizing, colorspace and bit depth conversion."
                string[] parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Length == 3) set.Add(parts[1]);
            }
            CoreLogger.Info("COLOR", $"FFmpeg filter inventory: {set.Count} filters (zscale={(set.Contains("zscale") ? "yes" : "no")}, tonemap={(set.Contains("tonemap") ? "yes" : "no")}).");
        }
        catch (Exception ex)
        {
            // An empty set means "assume no zscale": HDR clips degrade loudly instead of failing.
            CoreLogger.Fail("COLOR", $"Could not list FFmpeg filters ({ex.Message}); HDR tone mapping disabled for this session.");
        }
        return set;
    }
}
