// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;

namespace FortniteVideoSoftware.App.ViewModels;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════
/// QUALITY_01 — THE DIAL ASKS FOR QUALITY. THE FILE SIZE IS THE ANSWER, NOT THE QUESTION.
///
/// WHAT WAS WRONG. The quality wheel's value WAS a file size: `targetMb = 5 + idx * 5`, 5MB to
/// 100MB. The app then worked out the bits-per-pixel that size bought and told the user what it
/// had turned out to be — "Blurry-", "Sharp+". So the one thing a user actually cares about was
/// an OUTPUT of the control, and the only way to reach a quality they wanted was to guess a size,
/// read the verdict, and guess again.
///
/// It was worse than one round of guessing, because bits-per-pixel depends on DURATION. The same
/// 40MB is "Lifelike" on a ten-second clip and "Pixelated" on a three-minute one, so the slider
/// position meant something different in every project and the guessing started again each time.
/// That is precisely the "I have to keep fiddling with it" the complaint describes.
///
/// WHAT IT IS NOW. The wheel picks a TIER. The tier is a bits-per-pixel target, which is a
/// property of how the picture LOOKS and is independent of how long the clip is, so "sharp" is
/// the same sharp on a 10-second clip and a 3-minute one. The megabytes are computed from it and
/// shown, so the user still learns the cost — they just no longer have to solve for it.
///
/// ⚠️ THE EXPORT PIPELINE IS UNTOUCHED. It has always received a target-megabyte number
/// (`ProcessWorker`'s target size). This class computes that same number; nothing downstream of
/// the dial knows the difference. Do NOT "simplify" by passing a tier index into the encoder.
///
/// ⚠️ THE MATHS IS THE OLD MATHS, RUN BACKWARDS. Every line below is the inverse of what
/// ExportViewModel.UpdateEstimatedQuality used to do, including the 1.5 landscape divisor and the
/// 60fps basis. It is inverted rather than reinvented on purpose: the words users already know
/// ("sharp", "clear") keep meaning what they meant, and a calibration bug cannot be introduced by
/// a well-meaning rewrite of the model.
/// ══════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class QualityLadder
{
    /// <summary>One stop on the dial: what the user reads, and the bits-per-pixel it targets.</summary>
    public readonly record struct Tier(string Name, double Bpp);

    /// <summary>
    /// The ladder, worst to best. Steps are roughly GEOMETRIC (~15-20% of bits-per-pixel each),
    /// because perceived quality tracks bitrate logarithmically — equal *absolute* steps would
    /// feel enormous at the bottom and indistinguishable at the top.
    ///
    /// The anchors are the OLD spectrum's own thresholds, so the familiar words still land where
    /// they always did: "okay" opens the band the old code called Clear (0.06), "sharp" sits in
    /// the middle of the old Sharp band (0.10-0.15), "high" in the old Crisp-Clear band
    /// (0.15-0.25), and everything above it is the old Lifelike range spread out into steps that
    /// were previously one undifferentiated label.
    ///
    /// ⚠️ `Original` carries NO bits-per-pixel figure and NO size cap. It is the constant-quality
    /// path (the old "Max CQ"), where the encoder decides the bitrate and the file is as large as
    /// it needs to be. TargetMbFor returns null for it, which is the signal the export layer
    /// already understands as "no target size".
    /// </summary>
    public static readonly Tier[] Tiers =
    {
        new("Pixelated",  0.024),
        new("Blurry",     0.036),
        new("Low",        0.048),
        new("Okay",       0.060),
        new("Good −",     0.070),
        new("Good",       0.081),
        new("Good +",     0.093),
        new("Sharp −",    0.108),
        new("Sharp",      0.125),
        new("Sharp +",    0.145),
        new("High −",     0.168),
        new("High",       0.195),
        new("High +",     0.226),
        new("Ultra −",    0.265),
        new("Ultra",      0.315),
        new("Ultra +",    0.380),
        new("Premium HQ", 0.470),
        new("Original",   0.0),
    };

    /// <summary>The constant-quality stop at the top of the dial: no target size at all.</summary>
    public static readonly int OriginalIndex = Tiers.Length - 1;

    public static readonly int MaxIndex = Tiers.Length - 1;

    /// <summary>
    /// QUALITY_01 — the dial lands on "Sharp" out of the box.
    ///
    /// The old default was index 7, which was 40 MEGABYTES — a size, so the quality it produced
    /// was a different answer for every clip length. A quality default is the same answer every
    /// time, which is the entire point of the change, and it is chosen so most users never need
    /// to touch the dial at all.
    /// </summary>
    public static readonly int DefaultIndex = 8;

    public static int ClampIndex(int index) => Math.Clamp(index, 0, MaxIndex);

    /// <summary>
    /// ══════════════════════════════════════════════════════════════════════════════════════
    /// QUALITY_02 — THE NUMBER THE EXPORT WORKER EXPECTS IS NOT THE DIAL'S INDEX.
    ///
    /// `VideoConfig.GetQualitySettings` (Core, unchanged) branches on `q >= 20`:
    ///     q >= 20  ->  keepHighestRes = TRUE,  targetMB = the override (null = constant quality)
    ///     q <  20  ->  keepHighestRes = FALSE, targetMB = override ?? (5 + q * 5)
    ///
    /// The old dial was 0-20, so its top stop WAS 20 and fell into the first branch naturally.
    /// The tier ladder tops out at 17, which lands in the SECOND branch — so without this mapping
    /// `Original` would quietly lose `keepHighestRes` AND, if the override were ever null, be
    /// capped at `5 + 17 * 5 = 90 MB`. The one tier whose entire promise is "no limit, best
    /// possible" would have been the most limited stop on the dial.
    ///
    /// Mapping here rather than changing the Core threshold keeps the worker's contract intact
    /// (it is governed by 03_FFMPEG_EXPORT_PIPELINE.md and shared with the Video Merger) and puts
    /// the translation at the one boundary that knows about tiers.
    /// ══════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    public const int WorkerConstantQualityLevel = 20;

    public static int ToWorkerQualityLevel(int tierIndex)
        => IsOriginal(tierIndex) ? WorkerConstantQualityLevel : ClampIndex(tierIndex);

    /// <summary>
    /// ══════════════════════════════════════════════════════════════════════════════════════
    /// QUALITY_03 — A FROZEN SECOND IS NOT A NORMAL SECOND.
    ///
    /// A freeze holds ONE still picture. The encoder spends almost nothing on it: every frame
    /// after the first is a near-empty P-frame. Counted as a full second, a 3-second freeze
    /// inflated the estimate by 3 seconds' worth of motion footage that will never be encoded,
    /// so the number under the dial read high and the export aimed at a size it did not need.
    ///
    /// ⚠️ THIS IS SAFE ONLY BECAUSE THE EXPORT IS TWO-PASS VBR. ProcessWorker runs an analysis
    /// pass and allocates bits by complexity, so handing it a smaller target does NOT starve the
    /// moving footage — the freeze simply stops being paid for. Under a fixed-bitrate single-pass
    /// encode this discount would take bits away from the motion instead, and must not be applied.
    /// ══════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    public const double FreezeSecondCostFactor = 0.15;

    /// <summary>QUALITY_03 — seconds that actually cost bits, freezes discounted.</summary>
    public static double BillableSeconds(double totalSec, double freezeSec)
    {
        if (totalSec <= 0) return 0;
        freezeSec = Math.Clamp(freezeSec, 0, totalSec);
        return (totalSec - freezeSec) + (freezeSec * FreezeSecondCostFactor);
    }

    public static string NameOf(int index) => Tiers[ClampIndex(index)].Name;

    public static bool IsOriginal(int index) => ClampIndex(index) == OriginalIndex;

    /// <summary>
    /// QUALITY_01 — the target file size, in megabytes, that buys this tier for THIS clip.
    /// Returns null for `Original`, which has no size target.
    ///
    /// The inverse of the old forward calculation, term for term:
    ///     forward:  videoKbps = ((targetMb x 8192) - (audioKbps x durSec)) / durSec
    ///               bpp       = (videoKbps x 1000) / (w x h x 60)          [ / 1.5 if landscape ]
    ///     inverse:  videoKbps = bpp x 1.5? x w x h x 60 / 1000
    ///               targetMb  = durSec x (videoKbps + audioKbps) / 8192
    /// </summary>
    /// <param name="durationSec">Finished clip length in seconds, already speed/cut adjusted.</param>
    /// <param name="isPortrait">Portrait skips the 1.5 divisor, exactly as the old code did.</param>
    /// <param name="freezeSec">
    /// QUALITY_03 — how many of those seconds are held still frames. Discounted, not free: a
    /// freeze still carries audio and container overhead.
    /// </param>
    public static double? TargetMbFor(int index, double durationSec, int width, int height, bool isPortrait, double freezeSec = 0)
    {
        index = ClampIndex(index);
        if (index == OriginalIndex) return null;
        if (durationSec <= 0 || width <= 0 || height <= 0) return null;

        double labelBpp = Tiers[index].Bpp;

        // ⚠️ The 1.5 is NOT cosmetic. The old code divided landscape's bits-per-pixel by it before
        // naming the result, i.e. landscape has to carry 1.5x the bitrate to earn the same word.
        // Dropping it here would silently re-grade every landscape export by two or three tiers.
        double rawBpp = isPortrait ? labelBpp : labelBpp * 1.5;

        double videoKbps = rawBpp * width * height * 60.0 / 1000.0;
        if (videoKbps < 100) videoKbps = 100;

        // Same audio rule as the old forward pass, applied in the same order: assume the good
        // bitrate, then drop to 64k only if the resulting file would be too small to afford it.
        double audioKbps = 192;

        // QUALITY_03 — VIDEO is billed on discounted seconds; AUDIO is billed on all of them.
        // A frozen picture still has a soundtrack running under it.
        double videoSec = BillableSeconds(durationSec, freezeSec);

        double targetMb = ((videoKbps * videoSec) + (audioKbps * durationSec)) / 8192.0;
        if (targetMb * 1024 < durationSec * 48)
        {
            audioKbps = 64;
            targetMb = ((videoKbps * videoSec) + (audioKbps * durationSec)) / 8192.0;
        }

        return targetMb;
    }

    /// <summary>
    /// QUALITY_01 — the colour the size readout is painted in. Bands follow the words, so the
    /// user gets the same "this is bad / this is fine / this is great" signal the old label gave.
    /// </summary>
    public static string ColorFor(int index)
    {
        index = ClampIndex(index);
        if (index <= 2) return "#e74c3c";                 // Pixelated..Low — genuinely poor
        if (index <= 6) return "White";                   // Okay..Good+   — unremarkable, fine
        return "#2ecc71";                                 // Sharp- and up — good
    }

    /// <summary>QUALITY_01 — "≈ 171 MB", or "1.9 GB" once megabytes stop being readable.</summary>
    public static string FormatSize(double megabytes)
    {
        if (megabytes >= 1024.0) return $"{megabytes / 1024.0:0.00} GB";
        if (megabytes >= 100.0) return $"{megabytes:0} MB";
        return $"{megabytes:0.0} MB";
    }
}
