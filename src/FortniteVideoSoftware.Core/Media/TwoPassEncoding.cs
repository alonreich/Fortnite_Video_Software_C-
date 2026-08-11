using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// T01 — shared pieces of the two-pass size-targeting route, used by BOTH <see cref="ProcessWorker"/>
/// (Main App) and <see cref="MergerWorker"/> (Video Merger). Crop Tools never encodes video and has
/// no use for any of this.
///
/// ── WHAT TWO-PASS ACTUALLY DOES ──────────────────────────────────────────────────────────────
/// Pass 1 encodes the whole timeline and writes a per-frame COMPLEXITY MAP. Pass 2 reads that map
/// and spends the SAME total bit budget where it is actually needed — taking bits from a static
/// lobby and giving them to a firefight. This is fundamentally different from `-rc-lookahead`,
/// which only sees ~32 frames (half a second at 60fps) and therefore can never make a trade
/// across the length of a clip.
///
/// Nothing in here touches pixels. No blur, no smoothing, no filter changes — only bit allocation.
///
/// ── WHERE IT APPLIES, AND WHERE IT MUST NOT ──────────────────────────────────────────────────
/// (1) A BITRATE BUDGET MUST EXIST. Two-pass redistributes a fixed budget; CRF/CQ exports have no
///     budget, so a second pass costs time and changes nothing. Main App: the MB quality slider,
///     never ORIGINAL QUALITY. Merger: 100% "Lossless" only, never the CQ range below it.
/// (2) libx264 ONLY. `h264_nvenc` does not implement libavcodec's stats-file two-pass; its
///     equivalent is `-multipass` + `-rc-lookahead`, both already configured in
///     <see cref="EncoderManager"/> and explicitly not to be altered. Do not try to wire
///     `-pass 1`/`-pass 2` into the NVENC/AMF/QSV paths — FFmpeg will accept the flags and
///     silently ignore them, which is worse than not offering the feature.
///
/// ── THE SCRATCH MASTER AND MANDATE #1 ────────────────────────────────────────────────────────
/// A naive two-pass re-runs the ENTIRE filter graph for both passes, and in this codebase the
/// graph (14-18 way split, lanczos portrait scale, zoom pads, HUD overlays) dominates wall clock —
/// that costs ~2x. The FAST route renders the graph ONCE into a near-lossless master and points
/// both passes at that file: ~1.3-1.4x instead of ~2x.
///
/// ⚠️ `project_structure.txt` mandate #1 forbids caches for video pipelines. The master is NOT a
/// cache. It is created inside the job's own temp directory, consumed by the two passes, and
/// deleted in the same job — it is never reused across exports and never outlives the job folder.
/// Do NOT evolve it into anything that survives an export, and do not delete this feature on the
/// assumption that it violates the mandate.
///
/// When the staging drive cannot hold the master (see <c>DiskSpaceGuard.HasRoomFor</c>) callers
/// fall back to the SLOW route — the graph genuinely runs twice — rather than abandoning the
/// accurate size targeting the user asked for.
/// </summary>
public static class TwoPassEncoding
{
    /// <summary>
    /// CRF of the scratch master. Visually lossless and far above anything the quality slider can
    /// request (a 5-100 MB target over a typical clip is ~1-24 Mbit/s; the master is ~30 Mbit/s),
    /// so generation loss across the two encodes is not measurable. Do NOT raise this to "save
    /// space" — the file is deleted at the end of the job and its whole purpose is transparency.
    /// </summary>
    public const string MasterCrf = "14";

    /// <summary>
    /// Codec flags for the scratch master.
    ///
    /// `veryfast` is deliberate: this stage is bounded by the filter graph, not by the encoder, so
    /// spending encoder effort here buys nothing and directly lengthens the export. A short GOP
    /// with `keyint_min=1` keeps the master cheap to seek for the two passes that read it.
    /// </summary>
    public static List<string> MasterCodecArgs() =>
    [
        "-c:v", "libx264",
        "-preset", "veryfast",
        "-crf", MasterCrf,
        "-pix_fmt", "yuv420p",
        "-g", "60", "-keyint_min", "1",
        "-profile:v", "high", "-level:v", "5.1",
    ];

    /// <summary>
    /// Codec flags for pass 1 (analysis) or pass 2 (real encode).
    ///
    /// Both passes MUST use identical encoder settings apart from the pass number — x264 validates
    /// the stats file against them, and a mismatch either aborts pass 2 or silently degrades it.
    /// If you change `-preset`, `-bf`, `-profile:v` or `-level:v` here, they change for both by
    /// construction. Keep it that way.
    /// </summary>
    public static List<string> PassArgs(int bitrateKbps, int passNumber, string passLogPrefix)
    {
        int kbps = Math.Min(EncoderManager.MaxBitrateKbps, Math.Max(300, bitrateKbps));
        return
        [
            "-c:v", "libx264",
            "-preset", "medium",
            "-b:v", $"{kbps}k",
            "-pass", passNumber.ToString(),
            "-passlogfile", passLogPrefix,
            "-bf", "2",
            "-profile:v", "high", "-level:v", "5.1",
            "-pix_fmt", "yuv420p",
        ];
    }

    /// <summary>
    /// Removes the scratch master and x264's stats files.
    ///
    /// x264 writes BOTH <c>&lt;prefix&gt;-0.log</c> and <c>&lt;prefix&gt;-0.log.mbtree</c>. A stale
    /// stats file left behind is the dangerous case: pass 2 of a LATER attempt would happily read a
    /// complexity map built for a different bitrate or a different video and produce a quietly
    /// worse file with no error anywhere. Callers therefore also run this BEFORE each attempt, not
    /// only after.
    ///
    /// Everything lives in the job's temp directory and dies with it, so this is belt-and-braces —
    /// but the belt matters.
    /// </summary>
    public static void Cleanup(string masterPath, string passLogPrefix)
    {
        foreach (string path in new[]
                 {
                     masterPath,
                     passLogPrefix + "-0.log",
                     passLogPrefix + "-0.log.mbtree",
                     passLogPrefix + ".log",
                     passLogPrefix + ".log.mbtree",
                 })
        {
            if (string.IsNullOrEmpty(path)) continue;
            try { if (File.Exists(path)) File.Delete(path); }
            catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
        }
    }
}
