// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
namespace FortniteVideoSoftware.Core.Media;

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

    public static int MergerTargetKbps(double averageSourceKbps)
        => Math.Max(800, (int)Math.Min(EncoderManager.MaxBitrateKbps, averageSourceKbps));

    public static double FromBitrate(double videoKbps, double videoSeconds, double audioKbps, double audioSeconds)
        => (videoKbps * videoSeconds + audioKbps * audioSeconds) * 1000 / 8 / (1024 * 1024);
}
