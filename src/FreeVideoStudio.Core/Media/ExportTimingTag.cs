// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// TIMINGTAG_02 — the frame-exact timing an export carries (Video-Merger-Migration.md P1, D11/D12).
/// Frames are counted at the export's own constant frame rate (<see cref="FpsNum"/>/<see cref="FpsDen"/>).
/// <para>
/// Layout of an exported file, in frames: <c>[intro][fade-in ... body ... fade-out]</c>. The fade-in
/// starts on the first frame AFTER the intro, and the fade-out ends on the last frame.
/// </para>
/// <para>
/// A NULL fade means UNKNOWN (not written, e.g. a meme sits at that edge, or an older/merged file).
/// ZERO means "this file has no such fade". Consumers must treat unknown as "no fade information",
/// never as a fade.
/// </para>
/// </summary>
public readonly record struct ExportTiming(int FpsNum, int FpsDen, int IntroFrames, int? FadeInFrames, int? FadeOutFrames)
{
    public double Fps => FpsDen > 0 ? (double)FpsNum / FpsDen : 0;
    public double IntroSec => FramesToSec(IntroFrames);
    public double FramesToSec(int frames) => Fps > 0 ? frames / Fps : 0;

    /// <summary>Whole frames for <paramref name="seconds"/> at <paramref name="fps"/> (nearest).</summary>
    public static int SecToFrames(double seconds, double fps)
        => seconds <= 0 || fps <= 0 ? 0 : (int)Math.Round(seconds * fps, MidpointRounding.AwayFromZero);

    /// <summary>Parses an FFmpeg frame-rate expression ("60", "30000/1001", "59.94").</summary>
    public static bool TryParseFps(string? text, out int num, out int den)
    {
        num = 0; den = 1;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        int slash = t.IndexOf('/');
        if (slash > 0)
        {
            if (int.TryParse(t[..slash], NumberStyles.Integer, CultureInfo.InvariantCulture, out num)
                && int.TryParse(t[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out den)
                && num > 0 && den > 0)
                return true;
            num = 0; den = 1;
            return false;
        }
        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out num) && num > 0) { den = 1; return true; }
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d > 0 && d < 1000)
        {
            num = (int)Math.Round(d * 1000); den = 1000;
            return true;
        }
        num = 0;
        return false;
    }
}

/// <summary>
/// TIMINGTAG_02 — reads and writes the <c>fvs_timing</c> container tag:
/// <c>v=2;fps=60/1;intro=6;fadein=30;fadeout=45</c>. Unknown keys are ignored (forward compatible);
/// a missing <c>fadein</c>/<c>fadeout</c> key means unknown. Parsing is culture-invariant and never throws.
/// </summary>
public static class ExportTimingTag
{
    public const string Key = "fvs_timing";

    /// <summary>Largest intro accepted, in seconds (mirrors <see cref="IntroTag.MaxPlausibleIntroSec"/>).</summary>
    public const double MaxIntroSec = IntroTag.MaxPlausibleIntroSec;

    /// <summary>Largest single fade accepted, in seconds. Anything longer is treated as corrupt.</summary>
    public const double MaxFadeSec = 30.0;

    public static string Format(ExportTiming t)
    {
        var sb = new StringBuilder("v=2");
        sb.Append(";fps=").Append(t.FpsNum.ToString(CultureInfo.InvariantCulture)).Append('/').Append(t.FpsDen.ToString(CultureInfo.InvariantCulture));
        sb.Append(";intro=").Append(Math.Max(0, t.IntroFrames).ToString(CultureInfo.InvariantCulture));
        if (t.FadeInFrames is int fi) sb.Append(";fadein=").Append(Math.Max(0, fi).ToString(CultureInfo.InvariantCulture));
        if (t.FadeOutFrames is int fo) sb.Append(";fadeout=").Append(Math.Max(0, fo).ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>Parses a tag value. False when absent, not v2, or implausible for a file of <paramref name="fileDurationSec"/>.</summary>
    public static bool TryParse(string? raw, double fileDurationSec, out ExportTiming timing)
    {
        timing = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        int version = 0, num = 0, den = 1, intro = 0;
        int? fadeIn = null, fadeOut = null;
        bool haveFps = false;
        foreach (string part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string k = part[..eq].Trim().ToLowerInvariant();
            string v = part[(eq + 1)..].Trim();
            switch (k)
            {
                case "v":
                    if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out version)) return false;
                    break;
                case "fps":
                    haveFps = ExportTiming.TryParseFps(v, out num, out den);
                    if (!haveFps) return false;
                    break;
                case "intro":
                    if (!TryFrames(v, out intro)) return false;
                    break;
                case "fadein":
                    if (!TryFrames(v, out int fi)) return false;
                    fadeIn = fi;
                    break;
                case "fadeout":
                    if (!TryFrames(v, out int fo)) return false;
                    fadeOut = fo;
                    break;
            }
        }

        if (version != 2 || !haveFps) return false;
        var t = new ExportTiming(num, den, intro, fadeIn, fadeOut);
        if (t.IntroSec > MaxIntroSec) return false;
        if (fileDurationSec > 0 && t.IntroSec > fileDurationSec / 2.0) return false;
        if (fadeIn is int a && t.FramesToSec(a) > MaxFadeSec) return false;
        if (fadeOut is int b && t.FramesToSec(b) > MaxFadeSec) return false;
        if (fileDurationSec > 0 && t.FramesToSec(intro + (fadeIn ?? 0) + (fadeOut ?? 0)) > fileDurationSec + 0.001) return false;
        timing = t;
        return true;
    }

    /// <summary>
    /// Reads timing from ffprobe's <c>format.tags</c>: the v2 key when present and valid, else the
    /// v1 <see cref="IntroTag.Key"/> (seconds; fps unknown → returned as frames at 1000/1 "millisecond
    /// frames", fades unknown). Null when the file carries neither.
    /// </summary>
    public static ExportTiming? Read(JsonNode? formatTags, double fileDurationSec)
    {
        if (formatTags is not JsonObject tags) return null;
        string? v2 = null, v1 = null;
        foreach (var kv in tags)
        {
            if (string.Equals(kv.Key, Key, StringComparison.OrdinalIgnoreCase)) v2 = kv.Value?.ToString();
            else if (string.Equals(kv.Key, IntroTag.Key, StringComparison.OrdinalIgnoreCase)) v1 = kv.Value?.ToString();
        }
        if (TryParse(v2, fileDurationSec, out var t)) return t;
        double sec = IntroTag.Validate(v1, fileDurationSec);
        if (sec > 0) return new ExportTiming(1000, 1, ExportTiming.SecToFrames(sec, 1000), null, null);
        return null;
    }

    private static bool TryFrames(string v, out int frames)
        => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out frames) && frames >= 0 && frames < 1_000_000;
}
