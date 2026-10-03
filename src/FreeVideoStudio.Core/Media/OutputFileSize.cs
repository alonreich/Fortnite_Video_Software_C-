// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
namespace FreeVideoStudio.Core.Media;

public static class OutputFileSize
{
    public static string FormatMegabytes(double? megabytes)
    {
        if (megabytes is not { } mb || !double.IsFinite(mb) || mb < 0) return "—";
        if (mb >= 1024 * 1024) return $"{mb / (1024 * 1024):0.0} TB";
        if (mb >= 1024) return $"{mb / 1024:0.0} GB";
        return mb >= 10 ? $"{mb:0} MB" : $"{mb:0.0} MB";
    }

    // SIZEESTIMATE_01 — the same constant-quality value used by MergerWorker.
    public static int MergerConstantQuality(int percent)
        => percent >= 100 ? 15 : Math.Max(15, 35 - (int)((percent - 5) * 20.0 / 95.0));

    /// <summary>
    /// MERGEQUALITY_01 (Video-Merger-Migration.md P11) — share of the 100% bitrate a quality below 100%
    /// targets: 2^((15 − CQ)/6), the constant-quality curve (≈0.79 at 95%, ≈0.28 at 50%, ≈0.10 at 5%).
    /// Below 100% the Merger now ENCODES to this bitrate (VBR) instead of an uncapped CQ, and the size
    /// estimate uses the same number, so a lower setting can never produce a bigger file than 100%.
    /// </summary>
    public static double MergerQualityRatio(int percent)
        => percent >= 100 ? 1.0 : Math.Pow(2, (15 - MergerConstantQuality(percent)) / 6.0);

    public static int MergerTargetKbps(double averageSourceKbps)
        => Math.Max(800, (int)Math.Min(EncoderManager.MaxBitrateKbps, averageSourceKbps));

    public static double FromBitrate(double videoKbps, double videoSeconds, double audioKbps, double audioSeconds)
        => (videoKbps * videoSeconds + audioKbps * audioSeconds) * 1000 / 8 / (1024 * 1024);
}
