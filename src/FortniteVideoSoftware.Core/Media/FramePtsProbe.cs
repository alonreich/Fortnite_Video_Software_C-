// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// FRAMESNAP_01 — CUTS LAND ON REAL FRAME TIMESTAMPS (Video-Merger-Migration.md P2.2).
///
/// "Skip the first 6 frames" is only exact when the cut is the presentation time of frame #6 as
/// the file actually stores it. Seconds computed from a nominal rate drift on 59.94 fps and are
/// simply wrong on variable-frame-rate captures. So the analyzer reads the first frames' pts once,
/// in the background, and the cut is that pts.
///
/// CLOCK: pts are returned in µs relative to the container's <c>format.start_time</c>. That is the
/// clock ffmpeg's filters see by default (it offsets every input to start at 0), so a trim at this
/// value hits exactly that frame.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class FramePtsProbe
{
    /// <summary>Extra packets read beyond the frames wanted: decoder delay (B-frames) can hold some back.</summary>
    private const int PacketMargin = 16;

    /// <summary>
    /// Presentation timestamps (µs, sorted, relative to the container start) of at least the first
    /// <paramref name="frames"/> video frames. Empty on any failure. Never throws.
    /// </summary>
    public static async Task<IReadOnlyList<long>> ProbeAsync(string ffprobePath, string videoPath, int frames)
    {
        if (frames <= 0) return Array.Empty<long>();
        var args = new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "frame=best_effort_timestamp_time:format=start_time",
            "-of", "json",
            "-read_intervals", "%+#" + (frames + PacketMargin).ToString(CultureInfo.InvariantCulture),
            videoPath,
        };
        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        try
        {
            var result = await AsyncProcessRunner.RunAsync(psi, timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                CoreLogger.Fail("FramePts", $"ffprobe exit {result.ExitCode}: {result.StandardError}");
                return Array.Empty<long>();
            }
            return Parse(result.StandardOutput);
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return Array.Empty<long>();
        }
    }

    /// <summary>Parses ffprobe's JSON (see <see cref="ProbeAsync"/>). Empty on garbage. Never throws.</summary>
    public static IReadOnlyList<long> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<long>();
        try
        {
            var root = JsonNode.Parse(json);
            double start = ParseSec(root?["format"]?["start_time"]) ?? 0;
            var list = new List<long>();
            if (root?["frames"] is JsonArray arr)
            {
                foreach (var f in arr)
                {
                    if (ParseSec(f?["best_effort_timestamp_time"]) is double t)
                        list.Add((long)Math.Round((t - start) * 1_000_000.0));
                }
            }
            list.Sort();
            return list;
        }
        catch (JsonException) { return Array.Empty<long>(); }
    }

    /// <summary>
    /// The cut that removes exactly <paramref name="introFrames"/> frames: the pts of the first kept
    /// frame (index <paramref name="introFrames"/>). Null when there are not enough frames.
    /// </summary>
    public static long? IntroCutUs(IReadOnlyList<long> pts, int introFrames)
    {
        if (introFrames <= 0) return 0;
        if (pts.Count <= introFrames) return null;
        return Math.Max(0, pts[introFrames]);
    }

    private static double? ParseSec(JsonNode? node)
    {
        if (node is null) return null;
        string s = node.ToString();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;
    }
}
