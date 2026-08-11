namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// LIVE ZOOM PREVIEW — ONE IMPLEMENTATION, SHARED BY EVERY PREVIEW SURFACE IN THE SUITE.
///
/// Turns "where is the playhead" into the mpv <c>video-crop</c> string that makes the on-screen
/// preview show exactly what the exported file will show, including the SLOW glide.
///
/// ── WHY THIS CLASS EXISTS ────────────────────────────────────────────────────────────────────
/// The Granular editor grew its own copy of the export's ramp maths. Adding the same preview to
/// the Main App and to Music Wizard phase 3 would have meant a THIRD and FOURTH copy of:
///   the steal-window rules · the two-Slow-zooms gap rule · the progress ramp · the centre lerp ·
///   the even-pixel rounding · the edge clamp
/// Four copies of one algorithm is four chances for the preview to stop matching the export, and
/// a preview that lies is worse than no preview. There is now exactly ONE copy, here, and it
/// reads its timing constants straight from <see cref="GranularSpeedBuilder"/> — the export engine
/// itself. Preview and export cannot drift apart without a compile error.
///
/// ⚠️ IF YOU CHANGE THE RAMP RULES, CHANGE THEM IN <see cref="GranularSpeedBuilder"/> AND THIS
/// FILE FOLLOWS AUTOMATICALLY. Never hard-code 0.5 / 1.0 in a window.
///
/// ── GPU ONLY. THIS IS DELIBERATE. ────────────────────────────────────────────────────────────
/// Callers must gate on <c>VideoRenderMode.Current.UseHardwareAcceleration</c>. On the GPU path
/// mpv applies the crop inside its own renderer — a texture-coordinate change, effectively free.
/// On the CPU fallback path (hwdec=no, software scale into a WriteableBitmap) every crop CHANGE
/// forces mpv to rebuild its software scaler, and a 0.5s glide produces ~10 of them; on machines
/// that by definition have no GPU that is a visible hitch. CPU-only machines therefore keep the
/// static guide rectangle and nothing else — an owner decision, not an oversight.
///
/// ── KNOWN, ACCEPTED DIFFERENCE FROM THE EXPORT ───────────────────────────────────────────────
/// Near the frame edges the export PADS with black where this preview CLAMPS the crop inside the
/// picture. So a zoom box pushed hard against an edge previews very slightly differently from the
/// rendered file. Documented in project_structure.txt; do not "fix" it by letting the crop leave
/// the frame — mpv rejects an out-of-bounds crop and the preview would simply stop updating.
/// </summary>
public static class ZoomPreviewSimulator
{
    /// <summary>Result of a simulation tick. <see cref="Crop"/> is empty when no crop applies.</summary>
    public readonly record struct Result(string Crop, double Progress)
    {
        public static readonly Result None = new(string.Empty, 0.0);
        public bool HasCrop => !string.IsNullOrEmpty(Crop);
    }

    /// <summary>
    /// Computes the mpv <c>video-crop</c> value for a moment in the clip.
    /// </summary>
    /// <param name="segments">
    /// The speed segments carrying the zoom boxes. Segments without a complete zoom are ignored.
    /// </param>
    /// <param name="tSec">
    /// Playhead position in seconds, RELATIVE TO THE TRIM START — the same origin the export uses.
    /// Callers holding an absolute player time must subtract their trim start first.
    /// </param>
    /// <param name="durSec">Trimmed clip length in seconds. Used as the "clip end" boundary.</param>
    /// <returns><see cref="Result.None"/> when the picture should be uncropped.</returns>
    /// <param name="portraitMode">
    /// PORTRAIT_01 — true when the export will run the Portrait Canvas Trick.
    ///
    /// ⚠️ THIS IS NOT OPTIONAL POLISH; WITHOUT IT THE PREVIEW LIES. In portrait the export does
    /// THREE things: (1) crop a window around the zoom box, WIDENED to the source aspect ratio,
    /// (2) scale that back to full frame, (3) centre-crop to the 2:3 slice, keeping only the
    /// middle 1280 of the 3414-wide internal space (= 720 of 1920 source px). Steps 1 and 3
    /// cancel, so the net result is exactly the box the user drew.
    ///
    /// The preview used to perform step 1 ONLY. Measured on a 1920x1080 source with a 480x720 box:
    /// the export showed a 480px-wide region, the preview showed 1280px — 2.67x too wide. The
    /// centres matched at every position, so nothing drifted sideways; the FRAMING was simply
    /// wrong, which reads to the eye as "not the same".
    ///
    /// It also means a portrait preview must be cropped to the 2:3 slice even when NO zoom is
    /// active, because that is what the export always produces.
    /// </param>
    /// <param name="srcW">Source width. Required for the no-zoom portrait slice, where there is no zoom box to read it from.</param>
    /// <param name="srcH">Source height.</param>
    public static Result Compute(IReadOnlyList<SpeedSegment>? segments, double tSec, double durSec,
                                 bool portraitMode = false, int srcW = 0, int srcH = 0)
    {
        // PORTRAIT_01: with no zoom at all the export still delivers the 2:3 slice, so the preview
        // must show it too. This is the "truthful throughout" case.
        if (portraitMode && srcW > 0 && srcH > 0 && (segments == null || segments.Count == 0))
            return PortraitSliceOnly(srcW, srcH);

        if (segments == null || segments.Count == 0) return Result.None;

        var zooms = new List<(double zs, double ze, SpeedSegment seg)>();
        foreach (var s in segments)
        {
            if (!s.ZoomW.HasValue || !s.ZoomH.HasValue || !s.ZoomX.HasValue || !s.ZoomY.HasValue) continue;
            if (string.IsNullOrEmpty(s.ZoomOrigRes)) continue;

            double zs = (s.ZoomStartMs ?? s.StartMs) / 1000.0;
            double ze = (s.ZoomEndMs ?? s.EndMs) / 1000.0;
            if (ze > zs + 0.001) zooms.Add((zs, ze, s));
        }
        if (zooms.Count == 0) return Result.None;

        zooms.Sort((a, b) => a.zs.CompareTo(b.zs));

        double p = 0;
        SpeedSegment? active = null;

        for (int i = 0; i < zooms.Count; i++)
        {
            var (zs, ze, seg) = zooms[i];

            // Inside the marked range the zoom is HELD at full strength, Slow or not.
            if (tSec >= zs && tSec <= ze) { p = 1.0; active = seg; break; }

            // Only a SLOW zoom reaches outside its marked range.
            if (!seg.ZoomSlow) continue;

            double prevEnd = i > 0 ? zooms[i - 1].ze : 0.0;
            double nextStart = i < zooms.Count - 1 ? zooms[i + 1].zs : durSec;

            // Two adjacent SLOW zooms both want to borrow from the gap between them, so they need
            // twice the ramp. An Instant zoom or a clip edge borrows nothing and needs only one.
            bool prevIsSlow = i > 0 && zooms[i - 1].seg.ZoomSlow;
            bool nextIsSlow = i < zooms.Count - 1 && zooms[i + 1].seg.ZoomSlow;

            double requiredBefore = prevIsSlow
                ? GranularSpeedBuilder.ZoomRampRequiredGapBetweenSlowZooms
                : GranularSpeedBuilder.ZoomRampMinAvailableSeconds;
            double requiredAfter = nextIsSlow
                ? GranularSpeedBuilder.ZoomRampRequiredGapBetweenSlowZooms
                : GranularSpeedBuilder.ZoomRampMinAvailableSeconds;

            double stealBefore = (zs - prevEnd) >= requiredBefore ? GranularSpeedBuilder.ZoomRampSeconds : 0.0;
            double stealAfter = (nextStart - ze) >= requiredAfter ? GranularSpeedBuilder.ZoomRampSeconds : 0.0;

            if (stealBefore > 0 && tSec >= zs - stealBefore && tSec < zs)
            {
                p = (tSec - (zs - stealBefore)) / stealBefore;
                active = seg;
                break;
            }
            if (stealAfter > 0 && tSec > ze && tSec <= ze + stealAfter)
            {
                p = 1.0 - (tSec - ze) / stealAfter;
                active = seg;
                break;
            }
        }

        if (active == null || p <= 0.001)
        {
            // Between zooms (or before the first one) portrait still shows the slice.
            if (portraitMode)
            {
                int fw = srcW, fh = srcH;
                if (fw <= 0 || fh <= 0)
                {
                    foreach (var s in segments)
                        if (!string.IsNullOrEmpty(s.ZoomOrigRes))
                        { (fw, fh) = CoordinateMath.GetResolutionInts(s.ZoomOrigRes!); break; }
                }
                if (fw > 0 && fh > 0) return PortraitSliceOnly(fw, fh);
            }
            return Result.None;
        }

        var (sw, sh) = CoordinateMath.GetResolutionInts(active.ZoomOrigRes!);
        if (sw <= 0 || sh <= 0) return Result.None;

        // Same shape as the export: zoom factor ramps 1 -> targetZ, and the view centre travels
        // from the frame centre to the box centre on the same progress value.
        double targetZ = Math.Min((double)sw / active.ZoomW!.Value, (double)sh / active.ZoomH!.Value);
        double zVal = 1.0 + (targetZ - 1.0) * p;
        if (zVal < 1.0) zVal = 1.0;

        double cx = sw / 2.0 + ((active.ZoomX!.Value + active.ZoomW!.Value / 2.0) - sw / 2.0) * p;
        double cy = sh / 2.0 + ((active.ZoomY!.Value + active.ZoomH!.Value / 2.0) - sh / 2.0) * p;

        double visW = sw / zVal, visH = sh / zVal;
        double x = cx - visW / 2.0;
        double y = cy - visH / 2.0;

        // ── PORTRAIT_01: apply the export's THIRD step, the 2:3 centre-crop ──────────────────
        // The window computed above is the export's widened intermediate. The export then scales
        // it back to full frame and keeps only the central slice, which nets out to the drawn box.
        // Reproduce that here or the preview shows the intermediate and looks 2.67x too wide.
        // The portrait crop trims HORIZONTALLY ONLY (crop=1280:1920 keeps the full height), so y
        // and visH are untouched.
        if (portraitMode)
        {
            double survW = PortraitSurvivingWidth(sw, sh);
            double k = visW / sw;                       // 1 final px == k source px
            x += (sw - survW) / 2.0 * k;
            visW = survW * k;
        }

        x = Math.Clamp(x, 0, Math.Max(0, sw - visW));
        y = Math.Clamp(y, 0, Math.Max(0, sh - visH));

        // Even dimensions: mpv rejects odd crops on chroma-subsampled formats.
        int iw = Math.Max(2, (int)Math.Round(visW / 2.0) * 2);
        int ih = Math.Max(2, (int)Math.Round(visH / 2.0) * 2);
        int ix = Math.Max(0, Math.Min(sw - iw, (int)Math.Round(x)));
        int iy = Math.Max(0, Math.Min(sh - ih, (int)Math.Round(y)));

        return new Result($"{iw}x{ih}+{ix}+{iy}", p);
    }

    /// <summary>
    /// PORTRAIT_01 — how much of the SOURCE width survives the portrait centre-crop.
    ///
    /// Derived from `CoordinateConstants` rather than hard-coded, so it tracks the Portrait Canvas
    /// Trick automatically: the frame is scaled up until it covers the 1280x1920 internal space,
    /// then 1280px is cropped from the middle. Undoing that scale gives the surviving source width
    /// — 720px on a 1920x1080 source, which is the number mandate #3 quotes.
    /// </summary>
    private static double PortraitSurvivingWidth(double sw, double sh)
    {
        double scale = Math.Max(CoordinateConstants.InternalW / sw, CoordinateConstants.InternalH / sh);
        return CoordinateConstants.InternalW / scale;
    }

    /// <summary>
    /// PORTRAIT_01 — the crop for "portrait, but no zoom happening right now".
    /// Exactly the 2:3 slice the export always delivers, full height.
    /// </summary>
    private static Result PortraitSliceOnly(int sw, int sh)
    {
        if (sw <= 0 || sh <= 0) return Result.None;

        double survW = PortraitSurvivingWidth(sw, sh);
        int iw = Math.Max(2, (int)Math.Round(survW / 2.0) * 2);
        int ih = Math.Max(2, (int)Math.Round(sh / 2.0) * 2);
        int ix = Math.Max(0, Math.Min(sw - iw, (int)Math.Round((sw - survW) / 2.0)));

        // Progress 0: no zoom is in effect, this is just the portrait framing.
        return new Result($"{iw}x{ih}+{ix}+0", 0.0);
    }
}
