
using System.Globalization;
using System.Text;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// A speed or freeze segment for the granular timeline.
/// </summary>
/// <summary>
/// CUT_01 — a stretch of footage the user deleted, in ABSOLUTE SOURCE MILLISECONDS.
///
/// ⚠️ TWO FRAMES OF REFERENCE, ON PURPOSE. This mirrors <see cref="SpeedSegment"/>: the UI and the
/// saved session speak absolute source milliseconds, because that is what trim handles and speed
/// segments already use and what survives a trim change. The Core timeline model speaks
/// CLIP-RELATIVE seconds (<see cref="OutputTimeline.Cut"/>), because that is what
/// <see cref="OutputTimeline.Insertion"/> uses. <see cref="ToClipRelative"/> is the ONE conversion
/// between them — do not hand a CutRange straight to OutputTimeline, and do not "simplify" either
/// type into the other's units.
/// </summary>
public readonly record struct CutRange(double StartMs, double EndMs)
{
    /// <summary>
    /// Converts to the Core timeline's clip-relative seconds, given the export's trim-in point.
    /// Clamping is left to <see cref="OutputTimeline.NormalizeCuts"/>, which owns every rule about
    /// ordering, merging and minimum length.
    /// </summary>
    public OutputTimeline.Cut ToClipRelative(double sourceCutStartMs) =>
        new((StartMs - sourceCutStartMs) / 1000.0, (EndMs - sourceCutStartMs) / 1000.0);

    /// <summary>Convenience for the common "convert a whole list" call at an export boundary.</summary>
    public static List<OutputTimeline.Cut> ToClipRelative(
        IReadOnlyList<CutRange>? cuts, double sourceCutStartMs)
    {
        var list = new List<OutputTimeline.Cut>();
        if (cuts == null) return list;
        foreach (var c in cuts) list.Add(c.ToClipRelative(sourceCutStartMs));
        return list;
    }
}

public record SpeedSegment(double StartMs, double EndMs, double Speed,
    int? ZoomX = null, int? ZoomY = null, int? ZoomW = null, int? ZoomH = null, string? ZoomOrigRes = null,
    bool ZoomSlow = false,
    double? ZoomStartMs = null,
    double? ZoomEndMs = null);

public class GranularSpeedBuilder
{
    private record ZoomConfig(double StartSec, double EndSec, int X, int Y, int W, int H, string Res, bool Slow);
    private record ZoomPhase(double Start, double End, ZoomConfig Config, double ProgStart, double ProgEnd);
    private record ChunkSpec(double Start, double End, double Speed, double FreezeDur = 0, ZoomPhase? Zoom = null);

    /// <summary>
    /// TIME_01 - ABSOLUTE source seconds to FINISHED-VIDEO seconds.
    ///
    /// <para>
    /// The mathematics no longer lives here. It moved to <see cref="OutputTimeline"/>, which is the
    /// only place in the application that knows how speed changes and freezes stretch time. This
    /// method survives purely as an adapter: six callers depend on its exact signature and on being
    /// handed a plain <see cref="Func{T, TResult}"/>.
    /// </para>
    /// </summary>
    public static Func<double, double> CreateTimeMapper(
        double totalDurationMs,
        IReadOnlyList<SpeedSegment>? segments,
        double baseSpeed = 1.0,
        double sourceCutStartMs = 0,
        IReadOnlyList<OutputTimeline.Cut>? cuts = null)
    {
        // CUT_01 — cuts ride the SAME mapper every other feature already uses. This is the whole
        // reason voice-overs, memes and the music timeline need no changes to survive a cut: they
        // are all stored in SOURCE time and converted through here, so removing footage slides
        // them exactly as slow-motion and freezes already do.
        return OutputTimeline.Create(totalDurationMs, segments, baseSpeed, sourceCutStartMs, null, cuts)
                             .SourceToOutput;
    }

    /// <summary>
    /// ISSUE_08 — chunk count above which the graph's parallel branches are likely to buffer an
    /// uncomfortable number of uncompressed frames in RAM (see <see cref="Build"/> remarks).
    /// Exposed so the UI can warn BEFORE the render starts rather than after the machine swaps.
    /// </summary>
    public const int HighChunkCountWarnThreshold = 24;

    /// <summary>
    /// SPLICE_01 — length of the de-click fade applied at each end of every concatenated audio
    /// chunk, in seconds. 8 ms is under half a frame at 60fps: long enough to turn a step
    /// discontinuity into a ramp the ear reads as continuous, short enough that no one perceives a
    /// gap. Raising this into the tens of milliseconds starts to be audible as a stutter at every
    /// speed change; lowering it below ~3 ms stops suppressing the click.
    /// </summary>
    public const double SpliceFadeSec = 0.008;

    /// <summary>
    /// SPLICE_01 — the fade pair for one audio chunk, or an empty string when the chunk is too
    /// short to carry one. A chunk shorter than twice the fade would have its fade-in and fade-out
    /// overlap, which attenuates the middle and audibly ducks very short chunks — so those are
    /// left alone; they are far too brief for a click to register anyway.
    /// </summary>
    private static string BuildSpliceFade(double chunkDurationSec)
    {
        if (chunkDurationSec <= SpliceFadeSec * 3) return string.Empty;

        double outStart = Math.Max(0, chunkDurationSec - SpliceFadeSec);
        return $",afade=t=in:st=0:d={SpliceFadeSec.ToString("F4", CultureInfo.InvariantCulture)}" +
               $",afade=t=out:st={outStart.ToString("F4", CultureInfo.InvariantCulture)}" +
               $":d={SpliceFadeSec.ToString("F4", CultureInfo.InvariantCulture)}";
    }

    private const double MaxZoomWorkingPixels = 100_000_000.0;


    /// <summary>
    /// How long the glide lasts, in seconds, on a side that has room for it.
    ///
    /// FIXED length, deliberately. It used to be "up to 1 second, or whatever is available if
    /// that is less" — which meant the same Slow setting produced a 1.0s glide in open footage
    /// and a 0.6s glide next to a neighbour, with no way for the user to know which they got.
    /// A full second also simply read as too slow for gameplay clips. It is now always exactly
    /// this value, or nothing at all.
    /// </summary>
    public const double ZoomRampSeconds = 0.5;

    /// <summary>
    /// Minimum free footage a side must have before it gets a glide at all.
    ///
    /// Equal to <see cref="ZoomRampSeconds"/> by construction: a side either has room for the
    /// whole ramp or it gets none. There is no partial ramp any more.
    /// A side with less than this snaps instantly — see the Slow-zoom notes in
    /// project_structure.txt Section 9.
    /// </summary>
    public const double ZoomRampMinAvailableSeconds = ZoomRampSeconds;

    /// <summary>
    /// OPTION A — free footage required between TWO ADJACENT **SLOW** ZOOMS before either of them
    /// is granted a ramp on the side that faces the other. Twice the ramp length, because BOTH
    /// zooms want to borrow from that one gap: the earlier zoom needs 0.5s after it to glide out,
    /// the later zoom needs 0.5s before it to glide in.
    ///
    /// THE BUG THIS FIXES: the two borrow windows used to be allowed to overlap. The chunk builder
    /// takes the FIRST matching zoom phase and stops looking, so the earlier zoom's glide-out won
    /// the whole contested stretch and the later zoom's glide-in was partly or entirely discarded.
    /// Measured results before the fix, with the zooms 0.75s apart: the picture glided out to
    /// normal, then JUMPED straight to half zoom, then glided the rest of the way in — a visible
    /// hop that looked like a rendering fault. At 0.5s apart the later zoom's glide vanished
    /// completely and it hard-cut in, despite the user having chosen Slow.
    ///
    /// The rule is now symmetric and all-or-nothing: if two Slow zooms are closer than this,
    /// NEITHER gets a ramp on the facing side and both snap. Predictable beats clever here — a
    /// clean cut reads as deliberate, a half-zoom hop reads as broken.
    ///
    /// ⚠️ ONLY APPLIES WHEN BOTH NEIGHBOURS ARE SLOW. An Instant zoom borrows nothing, so it never
    /// contests the gap and the ordinary <see cref="ZoomRampMinAvailableSeconds"/> applies against
    /// it. Same for the clip start and clip end. Do not widen this to all neighbours — that would
    /// deny ramps that are perfectly safe.
    /// </summary>
    public const double ZoomRampRequiredGapBetweenSlowZooms = ZoomRampSeconds * 2.0;

    /// <summary>ISSUE_02 — filter dimensions must be positive even integers.</summary>
    private static int EvenDim(double v)
    {
        int i = (int)Math.Round(v);
        if (i < 2) return 2;
        return i - (i % 2);
    }

    /// <summary>
    /// G05 — minimal safe black margin (per side) for a digital-zoom pan, in source pixels.
    ///
    /// WHY A MARGIN EXISTS AT ALL: the zoom is `pad -> crop`. The crop window follows the zoom
    /// box's centre, and that centre can sit anywhere from 0 to the frame edge — so the window
    /// half-hangs off the frame at the extremes. Without a black border FFmpeg throws an
    /// out-of-bounds `crop` error and the whole export dies (changelog item (10)).
    ///
    /// WHY THIS SIZE: for a FIXED crop window of width `cropExtent`, the furthest it can ever
    /// hang off either edge is exactly `cropExtent / 2`. Anything beyond that is black pixels
    /// that are allocated, cleared and then never sampled. The old code hardcoded HALF THE FULL
    /// FRAME regardless of zoom level, which — combined with the `pad=iw+` double-count bug in
    /// PadFilterNode — produced a 7680x4320 working frame for a 2560x1440 source.
    ///
    /// The +2px and the even-alignment are float-rounding insurance; `crop` is unforgiving and
    /// two wasted pixels cost nothing.
    /// </summary>
    private static double ZoomPadMargin(double cropExtent)
    {
        double needed = Math.Max(0.0, cropExtent) / 2.0 + 2.0;
        return EvenDim(Math.Ceiling(needed));
    }


    /// <summary>
    /// DRIFT_01 — SNAPS THE ZOOM CROP WINDOW ONTO THE GRID FFMPEG ACTUALLY USES.
    ///
    /// THE BUG THIS FIXES (measured, 2560x1440 portrait, 134x202 box at X=1188):
    /// `cropW = resW / targetZ` is almost never a whole number — here 359.1111 — and `cropX`
    /// inherits that fraction (1257.4444). `vf_crop` does NOT honour either: it TRUNCATES both to
    /// int and then masks them DOWN to the chroma grid (`&amp; ~1` for yuv420p). 359.1111 -> 358 and
    /// 1257.4444 -> 1256, so the sampled window slid 2px LEFT of the box the user drew. The zoom
    /// then magnifies that error by `targetZ` (~8x at the quality floor) and the portrait slice
    /// magnifies it again, landing as ~15 OUTPUT px of sideways drift on the finished 1080-wide
    /// file — the picture sits right of where it was framed and the right edge of the box is cut
    /// off. Verified against the live mpv preview: markers at source 1188/1255/1322 land at output
    /// 3.6/539.5/1075.4 in the preview but 17.4/556.4/off-frame in the export.
    ///
    /// WHY IT LOOKS PURELY SIDEWAYS: in portrait `targetZ` is always the HEIGHT ratio
    /// (`resH / zc.H`), so `cropH = resH / targetZ` is exactly `zc.H` and `cropY` is exactly an
    /// integer — vertical never rounds. Only the width is fractional. The drift is therefore
    /// horizontal by construction, which is precisely how it was reported.
    ///
    /// THE FIX: emit a window that is ALREADY whole and chroma-aligned, so FFmpeg has nothing left
    /// to snap. `cropW`/`cropH` go to a neighbouring EVEN integer chosen so that
    /// `centre - size / 2` is also even; with an even `pad` offset that makes `cropX`/`cropY` exact
    /// even integers. The CENTRE is preserved EXACTLY (it was the thing drifting); the size moves
    /// by at most 2px, i.e. a &lt;1% change in zoom strength, which is invisible.
    ///
    /// DO NOT go back to passing fractions to `crop` "because the expression parser accepts them".
    /// It accepts them and then throws the fraction away.
    /// </summary>
    private readonly record struct ZoomWindow(int CropW, int CropH, int PadX, int PadY, int CanvasW, int CanvasH, int CropX, int CropY);

    private static ZoomWindow SnapZoomWindow(double cropWRaw, double cropHRaw, double resW, double resH,
                                             double cxTarget, double cyTarget)
    {
        int cx = (int)Math.Round(cxTarget, MidpointRounding.AwayFromZero);
        int cy = (int)Math.Round(cyTarget, MidpointRounding.AwayFromZero);

        int w = SnapExtent(cropWRaw, cx);
        int h = SnapExtent(cropHRaw, cy);

        int padX = (int)ZoomPadMargin(w);
        int padY = (int)ZoomPadMargin(h);

        return new ZoomWindow(w, h, padX, padY,
                              (int)resW + 2 * padX, (int)resH + 2 * padY,
                              padX + cx - w / 2, padY + cy - h / 2);
    }

    /// <summary>
    /// DRIFT_01 — nearest EVEN extent whose half has the same parity as the window centre, so that
    /// `centre - extent / 2` is even and the crop origin lands on the chroma grid untouched.
    /// Of the two even values straddling <paramref name="raw"/> exactly one satisfies that, so the
    /// chosen extent is never more than 2px from the ideal.
    /// </summary>
    private static int SnapExtent(double raw, int centre)
    {
        int lo = (int)Math.Floor(raw);
        lo -= lo & 1;
        if (lo < 2) lo = 2;
        int pick = ((centre - lo / 2) & 1) == 0 ? lo : lo + 2;
        return pick < 2 ? 2 : pick;
    }

    /// <param name="needHudBranch">
    /// ISSUE_08 — whether the caller will actually CONSUME the HUD output label.
    ///
    /// The graph fans the decoded stream out with a `split` and gives every chunk its own
    /// branch. Because `concat` reads branch 1 to completion before touching branch 2, frames
    /// pushed down the not-yet-read branches sit in FFmpeg's link queues in RAM — so peak memory
    /// scales with the NUMBER OF BRANCHES.
    ///
    /// This flag halves that. The HUD copy only exists for the portrait/mobile cut-out overlay;
    /// in landscape the caller used to feed it straight into `nullsink`, which meant FFmpeg
    /// still allocated, filled and buffered an entire second set of branches purely to throw
    /// them away. Pass false and no HUD split/trim/concat is emitted at all: `split=n` instead
    /// of `split=2n`.
    ///
    /// When false the returned <c>hudLabel</c> is an empty string — callers must check it.
    /// </param>
    public static (string filterGraph, string videoLabel, string hudLabel, string audioLabel, double finalDuration, Func<double, double> timeMapper) Build(
        double totalDurationMs,
        List<SpeedSegment>? segments,
        double baseSpeed = 1.0,
        double sourceCutStartMs = 0,
        string inputVideoLabel = "[0:v]",
        string? inputAudioLabel = "[0:a]",
        string targetFps = "60",
        bool needHudBranch = true,
        IReadOnlyList<OutputTimeline.Cut>? cuts = null)
    {
        double totalDurationSec = totalDurationMs / 1000.0;
        double timelineOriginSec = sourceCutStartMs / 1000.0;
        var preChainParts = new List<string>();

        // CUT_01 — normalised through the SAME method OutputTimeline uses, so the graph this
        // builds and the timeline the UI draws can never disagree about where the holes are.
        var activeCuts = OutputTimeline.NormalizeCuts(cuts, totalDurationSec);

        bool InsideCut(double at)
        {
            foreach (var c in activeCuts)
                if (at > c.StartSec - 0.0005 && at < c.EndSec - 0.0005) return true;
            return false;
        }

        double ToClipRelative(double absSec)
        {
            double rel = absSec - timelineOriginSec;
            return Math.Max(0, Math.Min(rel, totalDurationSec));
        }

        var normalizedSegments = new List<(double start, double end, double speed)>();
        var zooms = new List<ZoomConfig>();

        if (segments != null)
        {
            foreach (var seg in segments)
            {
                double start = ToClipRelative(seg.StartMs / 1000.0);
                double end = ToClipRelative(seg.EndMs / 1000.0);
                if (end > start + 0.001)
                {
                    normalizedSegments.Add((start, end, seg.Speed));
                }

                if (seg.ZoomW.HasValue && seg.ZoomH.HasValue && seg.ZoomX.HasValue && seg.ZoomY.HasValue && !string.IsNullOrEmpty(seg.ZoomOrigRes))
                {
                    double zStart = ToClipRelative((seg.ZoomStartMs ?? seg.StartMs) / 1000.0);
                    double zEnd = ToClipRelative((seg.ZoomEndMs ?? seg.EndMs) / 1000.0);
                    if (zEnd > zStart + 0.001)
                    {
                        zooms.Add(new ZoomConfig(zStart, zEnd, seg.ZoomX.Value, seg.ZoomY.Value, seg.ZoomW.Value, seg.ZoomH.Value, seg.ZoomOrigRes, seg.ZoomSlow));
                    }
                }
            }
        }
        normalizedSegments.Sort((a, b) => a.start.CompareTo(b.start));
        zooms.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));

        var zoomPhases = new List<ZoomPhase>();
        for (int i = 0; i < zooms.Count; i++)
        {
            var z = zooms[i];
            if (!z.Slow)
            {
                zoomPhases.Add(new ZoomPhase(z.StartSec, z.EndSec, z, 1.0, 1.0));
                continue;
            }

            double prevEnd = (i > 0) ? zooms[i - 1].EndSec : 0.0;
            double nextStart = (i < zooms.Count - 1) ? zooms[i + 1].StartSec : totalDurationSec;

            bool prevIsSlowZoom = i > 0 && zooms[i - 1].Slow;
            bool nextIsSlowZoom = i < zooms.Count - 1 && zooms[i + 1].Slow;

            double requiredBefore = prevIsSlowZoom ? ZoomRampRequiredGapBetweenSlowZooms : ZoomRampMinAvailableSeconds;
            double requiredAfter = nextIsSlowZoom ? ZoomRampRequiredGapBetweenSlowZooms : ZoomRampMinAvailableSeconds;

            double availBefore = z.StartSec - prevEnd;
            double stealBefore = availBefore >= requiredBefore ? ZoomRampSeconds : 0.0;

            double availAfter = nextStart - z.EndSec;
            double stealAfter = availAfter >= requiredAfter ? ZoomRampSeconds : 0.0;

            if (stealBefore > 0)
            {
                zoomPhases.Add(new ZoomPhase(z.StartSec - stealBefore, z.StartSec, z, 0.0, 1.0));
            }
            zoomPhases.Add(new ZoomPhase(z.StartSec, z.EndSec, z, 1.0, 1.0));
            if (stealAfter > 0)
            {
                zoomPhases.Add(new ZoomPhase(z.EndSec, z.EndSec + stealAfter, z, 1.0, 0.0));
            }
        }

        var speedPhases = new List<(double start, double end, double speed)>();
        double currentSec = 0;

        var speedSegs = normalizedSegments.Where(s => Math.Abs(s.speed) > 0.001).ToList();
        foreach (var seg in speedSegs)
        {
            double sStart = Math.Max(seg.start, currentSec);
            if (seg.end <= sStart + 0.001) continue;
            if (sStart > currentSec + 0.001)
                speedPhases.Add((currentSec, sStart, baseSpeed));
            speedPhases.Add((sStart, seg.end, seg.speed));
            currentSec = seg.end;
        }
        if (currentSec < totalDurationSec - 0.001)
            speedPhases.Add((currentSec, totalDurationSec, baseSpeed));

        var freezes = normalizedSegments.Where(s => Math.Abs(s.speed) < 0.001).OrderBy(f => f.start).ToList();
        var chunks = new List<ChunkSpec>();
        double sourceCursor = 0;

        void AppendSourceRange(double rangeStart, double rangeEnd)
        {
            var subBounds = new HashSet<double> { rangeStart, rangeEnd };
            foreach (var sp in speedPhases)
            {
                if (sp.start > rangeStart && sp.start < rangeEnd) subBounds.Add(sp.start);
                if (sp.end > rangeStart && sp.end < rangeEnd) subBounds.Add(sp.end);
            }
            // CUT_01 — a cut edge is a subdivision boundary exactly like a speed or zoom edge.
            // Adding them here means the existing boundary machinery does the splitting, and the
            // only extra work below is DROPPING the sub-ranges that fall inside a hole. This is
            // also what makes "a cut through the middle of a slow-mo + zoom block" fall out for
            // free: the block was already going to be subdivided at those boundaries.
            foreach (var c in activeCuts)
            {
                if (c.StartSec > rangeStart && c.StartSec < rangeEnd) subBounds.Add(c.StartSec);
                if (c.EndSec > rangeStart && c.EndSec < rangeEnd) subBounds.Add(c.EndSec);
            }
            foreach (var zp in zoomPhases)
            {
                if (zp.Start > rangeStart && zp.Start < rangeEnd) subBounds.Add(zp.Start);
                if (zp.End > rangeStart && zp.End < rangeEnd) subBounds.Add(zp.End);
            }
            var blist = subBounds.ToList();
            blist.Sort();

            for (int i = 0; i < blist.Count - 1; i++)
            {
                double cStart = blist[i];
                double cEnd = blist[i + 1];
                if (cEnd <= cStart + 0.001) continue;
                double mid = (cStart + cEnd) / 2.0;

                // CUT_01 — THE ENTIRE REMOVAL, RIGHT HERE. A sub-range whose midpoint is inside a
                // cut never becomes a chunk, so it is never trimmed, never decoded, never handed to
                // concat, and never reaches the output file. Testing the MIDPOINT rather than an
                // edge is deliberate: edges are shared with the neighbouring surviving range, and
                // an edge test would drop the wrong side.
                if (InsideCut(mid)) continue;

                double cSpeed = baseSpeed;
                foreach (var sp in speedPhases)
                {
                    if (mid >= sp.start && mid <= sp.end) { cSpeed = sp.speed; break; }
                }

                ZoomPhase? cZoom = null;
                foreach (var zp in zoomPhases)
                {
                    if (mid >= zp.Start && mid <= zp.End) { cZoom = zp; break; }
                }

                chunks.Add(new ChunkSpec(cStart, cEnd, cSpeed, 0, cZoom));
            }
        }

        foreach (var f in freezes)
        {
            double fStart = Math.Max(sourceCursor, f.start);
            double fEnd = Math.Max(fStart, f.end);
            if (fEnd <= sourceCursor + 0.001) continue;

            // CUT_01 — must match OutputTimeline.Create exactly: a freeze whose held frame was
            // deleted is dropped. If these two ever disagree the exported length and the length the
            // UI predicts drift apart, which is the class of bug OutputTimeline was created to end.
            if (InsideCut(fStart)) continue;

            if (fStart > sourceCursor + 0.001)
                AppendSourceRange(sourceCursor, fStart);

            double fDur = Math.Max(0.001, fEnd - fStart);
            ZoomPhase? cZoom = null;
            foreach (var zp in zoomPhases)
            {
                if (fStart >= zp.Start && fStart <= zp.End) { cZoom = zp; break; }
            }
            chunks.Add(new ChunkSpec(fStart, fStart + 0.001, 0, fDur, cZoom));
            sourceCursor = fStart;
        }
        if (totalDurationSec > sourceCursor + 0.001)
            AppendSourceRange(sourceCursor, totalDurationSec);

        var sharedTimeline = OutputTimeline.Create(totalDurationMs, segments, baseSpeed, sourceCutStartMs, null, cuts);
        double TimeMapper(double timelineSec) => sharedTimeline.SourceToOutput(timelineSec);

        int nChunks = chunks.Count;

        // CUT_01 — a cut list that removes every surviving frame would fall through to the
        // "no chunks" fast path below and silently export the WHOLE clip, cuts ignored — the worst
        // possible failure, because it looks like the feature simply did not work. The UI refuses
        // to leave less than one frame, so reaching here means a bad saved state; fail loudly.
        if (nChunks == 0 && activeCuts.Count > 0)
        {
            CoreLogger.Fail("GranularSpeed",
                $"CUT: {activeCuts.Count} cut(s) removed every frame of the clip. Nothing left to " +
                "export. Ignoring the cut list for this render so the export still produces a file.");
            activeCuts.Clear();
            AppendSourceRange(0, totalDurationSec);
            nChunks = chunks.Count;
        }

        if (activeCuts.Count > 0)
        {
            CoreLogger.Info("GranularSpeed",
                $"CUT: {activeCuts.Count} cut(s) removing " +
                $"{activeCuts.Sum(c => c.LengthSec):F2}s of source. " +
                $"{nChunks} chunk(s) after removal.");
        }

        if (nChunks == 0)
        {
            var audioFilters = BuildAtempoChain(baseSpeed);
            string aChain = !string.IsNullOrEmpty(inputAudioLabel)
                ? $"{inputAudioLabel}aresample=48000:async=1,asetpts=PTS-STARTPTS,{string.Join(",", audioFilters)}[a_speed_out]"
                : $"anullsrc=r=48000:cl=stereo,atrim=duration={totalDurationSec / baseSpeed:F4},asetpts=PTS-STARTPTS[a_speed_out]";

            if (!needHudBranch)
            {
                string vOnly = $"{inputVideoLabel}setpts='(PTS-STARTPTS)/{baseSpeed:F4}'[v_speed_out]";
                return (string.Join(";", [.. preChainParts, vOnly, aChain]), "[v_speed_out]", "", "[a_speed_out]",
                    totalDurationSec / baseSpeed, TimeMapper);
            }

            string vChain = $"{inputVideoLabel}setpts='(PTS-STARTPTS)/{baseSpeed:F4}'[v_speed_out]";
            string vChainHud = $"{inputVideoLabel}setpts='(PTS-STARTPTS)/{baseSpeed:F4}'[v_hud_out]";
            return (string.Join(";", [.. preChainParts, vChain, vChainHud, aChain]), "[v_speed_out]", "[v_hud_out]", "[a_speed_out]",
                totalDurationSec / baseSpeed, TimeMapper);
        }

        int branchCount = needHudBranch ? nChunks * 2 : nChunks;
        CoreLogger.Info("GranularSpeed",
            $"Building graph with {nChunks} chunk(s) -> {branchCount} video branch(es) " +
            $"(HUD branch {(needHudBranch ? "ENABLED" : "SKIPPED — no consumer")}).");
        if (nChunks > HighChunkCountWarnThreshold)
        {
            CoreLogger.Fail("GranularSpeed",
                $"HIGH CHUNK COUNT: {nChunks} chunks means {branchCount} parallel filter branches. " +
                "FFmpeg buffers frames on every branch that concat has not reached yet, so peak RAM " +
                "grows with this number. If the export runs out of memory, reduce the number of speed segments.");
        }

        var fullParts = new List<string>(preChainParts);
        var vMainPads = new List<string>();
        var vHudPads = new List<string>();
        var aPads = new List<string>();
        double finalDuration = 0;

        string vSplitsMain = "";
        string vSplitsHud = "";
        for (int i = 0; i < nChunks; i++) {
            vSplitsMain += $"[v_split_main_{i}]";
            if (needHudBranch) vSplitsHud += $"[v_split_hud_{i}]";
        }
        fullParts.Add($"{inputVideoLabel}split={branchCount}{vSplitsMain}{vSplitsHud}");

        string? aSplits = null;
        if (!string.IsNullOrEmpty(inputAudioLabel))
        {
            aSplits = "";
            for (int i = 0; i < nChunks; i++) aSplits += $"[a_split_{i}]";
            fullParts.Add($"{inputAudioLabel}asplit={nChunks}{aSplits}");
        }

        double fpsValue = ParseFps(targetFps);

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            string vSrcMain = $"[v_split_main_{i}]";
            string vSrcHud = $"[v_split_hud_{i}]";
            string? aSrc = !string.IsNullOrEmpty(inputAudioLabel) ? $"[a_split_{i}]" : null;
            
            string vChunkMainLabel = $"[v_chunk_main_{i}]";
            string vChunkHudLabel = $"[v_chunk_hud_{i}]";
            string aChunkLabel = $"[a_chunk_{i}]";

            string zoomFilter = "";
            if (chunk.Zoom != null)
            {
                var zp = chunk.Zoom;
                var zc = zp.Config;
                var (outW, outH) = FortniteVideoSoftware.Core.Media.CoordinateMath.GetResolutionInts(zc.Res);
                double phaseDur = zp.End - zp.Start;
                double p1 = zp.ProgStart;
                double p2 = zp.ProgEnd;
                if (phaseDur > 0.001)
                {
                    p1 = zp.ProgStart + (zp.ProgEnd - zp.ProgStart) * ((chunk.Start - zp.Start) / phaseDur);
                    p2 = zp.ProgStart + (zp.ProgEnd - zp.ProgStart) * ((chunk.End - zp.Start) / phaseDur);
                }

                double resW = outW, resH = outH;
                if (!string.IsNullOrEmpty(zc.Res) && zc.Res.Contains('x'))
                {
                    var parts = zc.Res.Split('x');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int pw) && int.TryParse(parts[1], out int ph))
                    {
                        resW = pw; resH = ph;
                    }
                }

                string preScale = $"scale={resW}:{resH}:force_original_aspect_ratio=decrease,pad={resW}:{resH}:(ow-iw)/2:(oh-ih)/2";
                if (Math.Abs(chunk.Speed) < 0.001)
                {
                    if (p1 >= 0.999 && !zc.Slow)
                    {
                        zoomFilter = BuildConstantZoomFilter(preScale, zc, resW, resH, outW, outH, 1.0);
                    }
                    else
                    {
                        zoomFilter = BuildConstantZoomFilter(preScale, zc, resW, resH, outW, outH, p1);
                    }
                }
                else
                {
                    if (!zc.Slow)
                    {
                        zoomFilter = BuildConstantZoomFilter(preScale, zc, resW, resH, outW, outH, 1.0);
                    }
                    else if (Math.Abs(p1 - p2) < 0.001)
                    {
                        zoomFilter = BuildConstantZoomFilter(preScale, zc, resW, resH, outW, outH, p1);
                    }
                    else
                    {
                        double realDur = chunk.End - chunk.Start;
                        double targetZ = Math.Min(resW / zc.W, resH / zc.H);
                        double cxTarget = zc.X + zc.W / 2.0;
                        double cyTarget = zc.Y + zc.H / 2.0;

                        double padX = resW / 2.0;
                        double padY = resH / 2.0;
                        double canvasW = resW + 2.0 * padX;
                        double canvasH = resH + 2.0 * padY;

                        double peakPixels = (canvasW * targetZ) * (canvasH * targetZ);
                        double s = 1.0;
                        if (peakPixels > MaxZoomWorkingPixels)
                        {
                            s = Math.Sqrt(MaxZoomWorkingPixels / peakPixels);
                            CoreLogger.Info("GranularSpeed",
                                $"Slow-zoom ramp would need a {canvasW * targetZ:F0}x{canvasH * targetZ:F0} working frame " +
                                $"({peakPixels / 1e6:F0} Mpx) at {targetZ:F2}x zoom — working at {s * 100:F0}% scale to stay within " +
                                $"the {MaxZoomWorkingPixels / 1e6:F0} Mpx budget. Only the <=1s ramp is affected; the held zoom is full resolution.");
                        }

                        int workW = EvenDim(canvasW * s);
                        int workH = EvenDim(canvasH * s);
                        int cropW = EvenDim(resW * s);
                        int cropH = EvenDim(resH * s);
                        string sStr = s.ToString("0.000000", CultureInfo.InvariantCulture);

                        string pExpr = $"{p1.ToString("0.000000", CultureInfo.InvariantCulture)}+({(p2 - p1).ToString("0.000000", CultureInfo.InvariantCulture)})*(t/{realDur.ToString("0.000000", CultureInfo.InvariantCulture)})";

                        string zExpr = $"1.0+({(targetZ - 1.0).ToString("0.000000", CultureInfo.InvariantCulture)})*({pExpr})";
                        string cxExpr = $"({padX.ToString("0.000000", CultureInfo.InvariantCulture)}+{(resW / 2.0).ToString("0.000000", CultureInfo.InvariantCulture)}+({(cxTarget - resW / 2.0).ToString("0.000000", CultureInfo.InvariantCulture)})*({pExpr})-({resW.ToString("0.000000", CultureInfo.InvariantCulture)}/({zExpr}))/2.0)";
                        string cyExpr = $"({padY.ToString("0.000000", CultureInfo.InvariantCulture)}+{(resH / 2.0).ToString("0.000000", CultureInfo.InvariantCulture)}+({(cyTarget - resH / 2.0).ToString("0.000000", CultureInfo.InvariantCulture)})*({pExpr})-({resH.ToString("0.000000", CultureInfo.InvariantCulture)}/({zExpr}))/2.0)";

                        string cropX = $"round((({cxExpr})*({zExpr})*{sStr})/2)*2";
                        string cropY = $"round((({cyExpr})*({zExpr})*{sStr})/2)*2";

                        string preDownscale = s < 0.999 ? $"scale={workW}:{workH}," : "";

                        var chain = new FilterChain()
                            .AddRaw(preScale)
                            .AddNode(new PadFilterNode { Width = canvasW.ToString(CultureInfo.InvariantCulture), Height = canvasH.ToString(CultureInfo.InvariantCulture), X = padX.ToString(CultureInfo.InvariantCulture), Y = padY.ToString(CultureInfo.InvariantCulture), Color = "black" });
                        if (!string.IsNullOrEmpty(preDownscale))
                        {
                            if (preDownscale.EndsWith(",")) preDownscale = preDownscale.Substring(0, preDownscale.Length - 1);
                            chain.AddRaw(preDownscale);
                        }
                        chain.AddRaw($"scale=w='iw*({zExpr})':h='ih*({zExpr})':eval=frame")
                             .AddNode(new CropFilterNode { Width = cropW.ToString(CultureInfo.InvariantCulture), Height = cropH.ToString(CultureInfo.InvariantCulture), X = cropX, Y = cropY })
                             .AddNode(new CasFilterNode { Strength = 0.5 })
                             .AddNode(new ScaleFilterNode { Width = outW.ToString(CultureInfo.InvariantCulture), Height = outH.ToString(CultureInfo.InvariantCulture) });
                        zoomFilter = chain.ToFFmpegString() + ",";
                    }
                }
            }

            if (Math.Abs(chunk.Speed) < 0.001)
            {
                double dur = chunk.FreezeDur;
                int targetFrameCount = Math.Max(1, (int)Math.Round(dur * fpsValue));
                double sampleWindow = Math.Max(4.0 / fpsValue, 0.20);
                double sampleUntil = Math.Min(totalDurationSec, chunk.Start + sampleWindow);
                double sampleWindowActual = Math.Max(1.0 / fpsValue, sampleUntil - chunk.Start);

                double freezeQuantDur = fpsValue > 0 ? targetFrameCount / fpsValue : dur;

                fullParts.Add(
                    $"{vSrcMain}trim=start={chunk.Start:F4}:duration={sampleWindowActual:F4}," +
                    $"setpts=PTS-STARTPTS," +
                    $"select='lte(n\\,0)'," +
                    $"{zoomFilter}format=yuv420p,setsar=1," +
                    $"tpad=stop_mode=clone:stop_duration={freezeQuantDur.ToString("F5", CultureInfo.InvariantCulture)}," +
                    $"fps={targetFps}:round=near," +
                    $"setpts=N/({targetFps})/TB," +
                    $"trim=duration={freezeQuantDur.ToString("F5", CultureInfo.InvariantCulture)},setpts=PTS-STARTPTS{vChunkMainLabel}");

                if (needHudBranch)
                {
                    fullParts.Add(
                        $"{vSrcHud}trim=start={chunk.Start:F4}:duration={sampleWindowActual:F4}," +
                        $"setpts=PTS-STARTPTS," +
                        $"select='lte(n\\,0)'," +
                        $"format=yuv420p,setsar=1," +
                        $"tpad=stop_mode=clone:stop_duration={freezeQuantDur.ToString("F5", CultureInfo.InvariantCulture)}," +
                        $"fps={targetFps}:round=near," +
                        $"setpts=N/({targetFps})/TB," +
                        $"trim=duration={freezeQuantDur.ToString("F5", CultureInfo.InvariantCulture)},setpts=PTS-STARTPTS{vChunkHudLabel}");
                }

                if (!string.IsNullOrEmpty(aSrc))
                    fullParts.Add($"{aSrc}anullsink");

                fullParts.Add($"anullsrc=r=48000:cl=stereo," +
                              $"atrim=duration={freezeQuantDur.ToString("F5", CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS{aChunkLabel}");

                finalDuration += freezeQuantDur;
            }
            else
            {
                double outDur = (chunk.End - chunk.Start) / chunk.Speed;

                double quantizedDur = fpsValue > 0 ? Math.Round(outDur * fpsValue) / fpsValue : outDur;

                fullParts.Add(
                    $"{vSrcMain}trim=start={chunk.Start:F4}:end={chunk.End:F4}," +
                    $"setpts='PTS-({chunk.Start:F4}/TB)'," +
                    $"{zoomFilter}" +
                    $"setpts='PTS/{chunk.Speed:F4}'," +
                    $"fps={targetFps}:start_time=0:round=near," +
                    $"format=yuv420p,setsar=1{vChunkMainLabel}");

                if (needHudBranch)
                {
                    fullParts.Add(
                        $"{vSrcHud}trim=start={chunk.Start:F4}:end={chunk.End:F4}," +
                        $"setpts='PTS-({chunk.Start:F4}/TB)'," +
                        $"setpts='PTS/{chunk.Speed:F4}'," +
                        $"fps={targetFps}:start_time=0:round=near," +
                        $"format=yuv420p,setsar=1{vChunkHudLabel}");
                }

                var audioFilters = BuildAtempoChain(chunk.Speed);
                if (!string.IsNullOrEmpty(aSrc))
                {
                    // CUT_01 / SPLICE_01 — a few milliseconds of fade on each end of every audio
                    // chunk. `concat` butt-joins waveforms, and a butt-join between two unrelated
                    // instants is a step discontinuity, which is heard as a click or pop. This has
                    // ALWAYS been true at speed-change and freeze boundaries; cuts just make it far
                    // more likely, because the two sides of a cut are arbitrarily far apart.
                    // Deliberately shorter than one frame at 60fps, so it cannot be heard as a dip.
                    string spliceFade = BuildSpliceFade(quantizedDur);

                    fullParts.Add(
                        $"{aSrc}atrim=start={chunk.Start:F4}:end={chunk.End:F4}," +
                        $"aresample=48000:async=1:min_comp=0.001," +
                        $"asetpts='PTS-({chunk.Start:F4}/TB)'," +
                        $"{string.Join(",", audioFilters)}," +
                        $"apad,atrim=duration={quantizedDur.ToString("F5", CultureInfo.InvariantCulture)}," +
                        $"asetpts=PTS-STARTPTS{spliceFade}{aChunkLabel}");
                }
                else
                {
                    fullParts.Add($"anullsrc=r=48000:cl=stereo," +
                                  $"atrim=duration={quantizedDur.ToString("F5", CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS{aChunkLabel}");
                }

                finalDuration += quantizedDur;
            }

            vMainPads.Add(vChunkMainLabel);
            if (needHudBranch) vHudPads.Add(vChunkHudLabel);
            aPads.Add(aChunkLabel);
        }

        fullParts.Add($"{string.Join("", vMainPads)}concat=n={nChunks}:v=1:a=0[v_speed_concat]");
        if (needHudBranch)
        {
            fullParts.Add($"{string.Join("", vHudPads)}concat=n={nChunks}:v=1:a=0[v_hud_concat]");
        }
        fullParts.Add($"{string.Join("", aPads)}concat=n={nChunks}:v=0:a=1[a_speed_concat]");

        fullParts.Add("[v_speed_concat]setpts=PTS-STARTPTS[v_speed_out]");
        if (needHudBranch)
        {
            fullParts.Add("[v_hud_concat]setpts=PTS-STARTPTS[v_hud_out]");
        }
        fullParts.Add("[a_speed_concat]aresample=48000:async=1:min_comp=0.01,asetpts=PTS-STARTPTS[a_speed_out]");

        return (string.Join(";", fullParts), "[v_speed_out]", needHudBranch ? "[v_hud_out]" : "", "[a_speed_out]",
            finalDuration, TimeMapper);
    }

    /// <summary>
    /// ISSUE_02 — builds the zoom filter for a chunk whose ramp progress does NOT change across
    /// it. That covers the entire held body of every slow zoom (emitted with ProgStart =
    /// ProgEnd = 1.0) and every freeze frame.
    ///
    /// It computes the visible source region directly and scales THAT to the output, instead of
    /// magnifying the whole padded canvas and cropping a window out of the result. Both express
    /// the identical region — this was confirmed by rendering the old and new expressions through
    /// FFmpeg and comparing the framing, which matched exactly at p = 1.0 — but this form never
    /// materialises the giant intermediate frame, and resamples the picture once rather than
    /// twice, so it is marginally sharper as well.
    ///
    /// The padding is kept at +-resW/-+resH (the value the audited instant-zoom path uses) so the
    /// coordinate chain documented in project_structure.txt is unchanged; with no per-frame scale
    /// in this branch the padding costs nothing meaningful.
    /// </summary>
    private static string BuildConstantZoomFilter(
        string preScale, ZoomConfig zc, double resW, double resH, int outW, int outH, double p)
    {
        double targetZ = Math.Min(resW / zc.W, resH / zc.H);
        double cxTarget = zc.X + zc.W / 2.0;
        double cyTarget = zc.Y + zc.H / 2.0;

        double zVal = 1.0 + (targetZ - 1.0) * p;
        if (zVal < 1.0) zVal = 1.0;

        double viewCx = resW / 2.0 + (cxTarget - resW / 2.0) * p;
        double viewCy = resH / 2.0 + (cyTarget - resH / 2.0) * p;

        var zwin = SnapZoomWindow(resW / zVal, resH / zVal, resW, resH, viewCx, viewCy);

        return new FilterChain()
            .AddRaw(preScale)
            .AddNode(new PadFilterNode { Width = zwin.CanvasW.ToString(CultureInfo.InvariantCulture), Height = zwin.CanvasH.ToString(CultureInfo.InvariantCulture), X = zwin.PadX.ToString(CultureInfo.InvariantCulture), Y = zwin.PadY.ToString(CultureInfo.InvariantCulture), Color = "black" })
            .AddNode(new CropFilterNode { Width = zwin.CropW.ToString(CultureInfo.InvariantCulture), Height = zwin.CropH.ToString(CultureInfo.InvariantCulture), X = zwin.CropX.ToString(CultureInfo.InvariantCulture), Y = zwin.CropY.ToString(CultureInfo.InvariantCulture) })
            .AddNode(new CasFilterNode { Strength = 0.5 })
            .AddNode(new ScaleFilterNode { Width = outW.ToString(CultureInfo.InvariantCulture), Height = outH.ToString(CultureInfo.InvariantCulture) })
            .ToFFmpegString() + ",";
    }

    /// <summary>
    /// ISSUE_04 — converts a playback rate into FFmpeg's atempo chain.
    ///
    /// WHAT WAS WRONG: the two normalisation loops below divide by 0.5 and 2.0 respectively.
    /// For any speed that is zero or negative those divisions never move the value across the
    /// loop's exit threshold — 0/0.5 is 0 forever, -1/0.5 marches away to -infinity — so the
    /// loop spun endlessly while appending to `filters`. The app froze solid the moment the user
    /// pressed PROCESS and ate memory until it was killed, with no error and nothing in the log.
    /// Nothing inside this method guarded against it, and it is a public helper called from
    /// several places, so the guard belongs HERE rather than at each call site.
    ///
    /// Recovery restore was the realistic route in: it read the saved base speed straight out of
    /// the JSON with no range check (now also clamped at that call site).
    /// </summary>
    public static List<string> BuildAtempoChain(double speed)
    {
        if (double.IsNaN(speed) || double.IsInfinity(speed) || speed <= 0.0)
        {
            CoreLogger.Fail("GranularSpeed",
                $"Refusing to build an audio speed chain for an impossible rate ({speed}). " +
                "Falling back to normal speed (1.0x) so the export can still complete.");
            speed = 1.0;
        }

        speed = Math.Clamp(speed, 0.01, 100.0);

        return BuildAtempoChainCore(speed);
    }

    private static List<string> BuildAtempoChainCore(double speed)
    {
        var filters = new List<string>();
        double tmp = speed;

        while (tmp < 0.5) { filters.Add("atempo=0.5"); tmp /= 0.5; }
        while (tmp > 2.0) { filters.Add("atempo=2.0"); tmp /= 2.0; }
        filters.Add($"atempo={tmp:F4}");

        return filters;
    }

    private static double ParseFps(string fpsExpr)
    {
        try
        {
            var frac = Frac.FromString(fpsExpr);
            if (frac <= Frac.Zero) return 60.0;
            return Math.Min(60.0, frac.ToDouble());
        }
        catch { return 60.0; }
    }
}

