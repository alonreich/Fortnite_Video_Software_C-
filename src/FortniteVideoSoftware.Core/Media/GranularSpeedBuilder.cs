
using System.Globalization;
using System.Text;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// A speed or freeze segment for the granular timeline.
/// </summary>
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

    public static Func<double, double> CreateTimeMapper(
        double totalDurationMs,
        IReadOnlyList<SpeedSegment>? segments,
        double baseSpeed = 1.0,
        double sourceCutStartMs = 0)
    {
        double totalDurationSec = totalDurationMs / 1000.0;
        double timelineOriginSec = sourceCutStartMs / 1000.0;

        double ToClipRelative(double absSec)
        {
            double rel = absSec - timelineOriginSec;
            return Math.Max(0, Math.Min(rel, totalDurationSec));
        }

        var normalizedSegments = new List<(double start, double end, double speed)>();
        if (segments != null)
        {
            foreach (var seg in segments)
            {
                double start = ToClipRelative(seg.StartMs / 1000.0);
                double end = ToClipRelative(seg.EndMs / 1000.0);
                if (end <= start + 0.001) continue;
                normalizedSegments.Add((start, end, seg.Speed));
            }
        }
        normalizedSegments.Sort((a, b) => a.start.CompareTo(b.start));

        var sourceChunks = new List<(double start, double end, double speed)>();
        double currentSec = 0;
        foreach (var seg in normalizedSegments.Where(s => Math.Abs(s.speed) > 0.001))
        {
            double sStart = Math.Max(seg.start, currentSec);
            if (seg.end <= sStart + 0.001) continue;
            if (sStart > currentSec + 0.001)
                sourceChunks.Add((currentSec, sStart, baseSpeed));
            sourceChunks.Add((sStart, seg.end, seg.speed));
            currentSec = seg.end;
        }
        if (currentSec < totalDurationSec - 0.001)
            sourceChunks.Add((currentSec, totalDurationSec, baseSpeed));

        var chunks = new List<ChunkSpec>();
        double sourceCursor = 0;

        void AppendSourceRange(double rangeStart, double rangeEnd)
        {
            foreach (var sc in sourceChunks)
            {
                double overlapStart = Math.Max(rangeStart, sc.start);
                double overlapEnd = Math.Min(rangeEnd, sc.end);
                if (overlapEnd > overlapStart + 0.001)
                    chunks.Add(new ChunkSpec(overlapStart, overlapEnd, sc.speed));
            }
        }

        foreach (var freeze in normalizedSegments.Where(s => Math.Abs(s.speed) < 0.001).OrderBy(f => f.start))
        {
            double fStart = Math.Max(sourceCursor, freeze.start);
            double fEnd = Math.Max(fStart, freeze.end);
            if (fEnd <= sourceCursor + 0.001) continue;

            if (fStart > sourceCursor + 0.001)
                AppendSourceRange(sourceCursor, fStart);

            double fDur = Math.Max(0.001, fEnd - fStart);
            chunks.Add(new ChunkSpec(fStart, fStart + 0.001, 0, fDur));
            sourceCursor = fStart;
        }
        if (totalDurationSec > sourceCursor + 0.001)
            AppendSourceRange(sourceCursor, totalDurationSec);

        return timelineSec =>
        {
            double target = ToClipRelative(timelineSec);
            double mapped = 0;
            foreach (var ch in chunks)
            {
                if (Math.Abs(ch.Speed) < 0.001)
                {
                    if (target >= ch.Start) mapped += ch.FreezeDur;
                    continue;
                }

                if (target <= ch.Start) break;
                if (target >= ch.End) mapped += (ch.End - ch.Start) / ch.Speed;
                else
                {
                    mapped += (target - ch.Start) / ch.Speed;
                    break;
                }
            }

            return Math.Max(0, mapped);
        };
    }

    /// <summary>
    /// ISSUE_08 — chunk count above which the graph's parallel branches are likely to buffer an
    /// uncomfortable number of uncompressed frames in RAM (see <see cref="Build"/> remarks).
    /// Exposed so the UI can warn BEFORE the render starts rather than after the machine swaps.
    /// </summary>
    public const int HighChunkCountWarnThreshold = 24;

    private const double MaxZoomWorkingPixels = 100_000_000.0;

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // SLOW-ZOOM RAMP TIMING — THE SINGLE SOURCE OF TRUTH FOR BOTH THE EXPORT AND THE PREVIEW.
    //
    // A SLOW zoom does not ease inside the range the user marked. It BORROWS footage from either
    // side: the glide-in happens in the seconds BEFORE the zoom start, the glide-out in the
    // seconds AFTER the zoom end. The marked range itself is held at full zoom throughout.
    //
    // ⚠️ `GranularSpeedEditorWindow.UpdateLiveZoomCrop` MUST read these same two constants. The
    // live mpv preview simulates this ramp 1:1, and if the two ever disagree the user is shown a
    // zoom that is not the one that gets exported. That is the whole reason these are `public`
    // rather than private — do NOT copy the numbers into the App layer.
    // ═════════════════════════════════════════════════════════════════════════════════════════

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
        bool needHudBranch = true)
    {
        double totalDurationSec = totalDurationMs / 1000.0;
        double timelineOriginSec = sourceCutStartMs / 1000.0;
        var preChainParts = new List<string>();

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

            // Free footage on each side, measured against the NEIGHBOURING ZOOMS only (plus the
            // clip start and clip end). Speed segments are deliberately not considered — a glide
            // may run through a slow-motion or fast-forward stretch.
            //
            // The ramp is ALL-OR-NOTHING at a fixed 0.5s. Previously it was `Math.Min(1.0, avail)`,
            // which produced a variable-length glide (anywhere from 0.5s to 1.0s) that the user
            // could neither predict nor see.
            //
            // OPTION A — how much room is required depends on WHO is next door:
            //   * another SLOW zoom  -> 1.0s, because it wants to borrow from the same gap.
            //   * an INSTANT zoom, the clip start, or the clip end -> 0.5s. None of those borrow
            //     anything, so there is nothing to contend with.
            // When two Slow zooms are closer than 1.0s NEITHER gets a ramp on the facing side and
            // both snap — symmetric, predictable, and free of the half-zoom hop that the old
            // overlapping-windows behaviour produced.
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

        double TimeMapper(double timelineSec)
        {
            double target = ToClipRelative(timelineSec);
            double mapped = 0;
            foreach (var ch in chunks)
            {
                if (Math.Abs(ch.Speed) < 0.001)
                {
                    if (target >= ch.Start) mapped += ch.FreezeDur;
                    continue;
                }
                if (target <= ch.Start) break;
                if (target >= ch.End) mapped += (ch.End - ch.Start) / ch.Speed;
                else { mapped += (target - ch.Start) / ch.Speed; break; }
            }
            return Math.Max(0, mapped);
        }

        int nChunks = chunks.Count;

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
                string p1Str = p1.ToString(CultureInfo.InvariantCulture);
                
                if (Math.Abs(chunk.Speed) < 0.001)
                {
                    if (p1 >= 0.999 && !zc.Slow)
                    {
                        double targetZ = Math.Min(resW / zc.W, resH / zc.H);
                        double cxTarget = zc.X + zc.W / 2.0;
                        double cyTarget = zc.Y + zc.H / 2.0;
                        double cropW = resW / targetZ;
                        double cropH = resH / targetZ;
                        // G05: INSTANT zoom — the crop window is a FIXED cropW x cropH and its
                        // centre can travel anywhere in [0, resW] x [0, resH]. The provably
                        // minimal safe margin is therefore cropW/2 (see ZoomPadMargin), not the
                        // old blanket resW/2. At 2x zoom that is a 25% smaller working frame; at
                        // 4x, 37% smaller. Geometry is unchanged — the discarded band was black.
                        double padX = ZoomPadMargin(cropW);
                        double padY = ZoomPadMargin(cropH);
                        double canvasW = resW + 2.0 * padX;
                        double canvasH = resH + 2.0 * padY;
                        double cropX = padX + cxTarget - cropW / 2.0;
                        double cropY = padY + cyTarget - cropH / 2.0;
                        zoomFilter = new FilterChain()
                            .AddRaw(preScale)
                            .AddNode(new PadFilterNode { Width = canvasW.ToString(CultureInfo.InvariantCulture), Height = canvasH.ToString(CultureInfo.InvariantCulture), X = padX.ToString(CultureInfo.InvariantCulture), Y = padY.ToString(CultureInfo.InvariantCulture), Color = "black" })
                            .AddNode(new CropFilterNode { Width = cropW.ToString(CultureInfo.InvariantCulture), Height = cropH.ToString(CultureInfo.InvariantCulture), X = cropX.ToString(CultureInfo.InvariantCulture), Y = cropY.ToString(CultureInfo.InvariantCulture) })
                            .AddNode(new CasFilterNode { Strength = 0.5 })
                            .AddNode(new ScaleFilterNode { Width = outW.ToString(CultureInfo.InvariantCulture), Height = outH.ToString(CultureInfo.InvariantCulture) })
                            .ToFFmpegString() + ",";
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
                        double targetZ = Math.Min(resW / zc.W, resH / zc.H);
                        double cxTarget = zc.X + zc.W / 2.0;
                        double cyTarget = zc.Y + zc.H / 2.0;
                        double cropW = resW / targetZ;
                        double cropH = resH / targetZ;
                        // G05: INSTANT zoom on a speed-adjusted chunk — same reasoning as above.
                        double padX = ZoomPadMargin(cropW);
                        double padY = ZoomPadMargin(cropH);
                        double canvasW = resW + 2.0 * padX;
                        double canvasH = resH + 2.0 * padY;
                        double cropX = padX + cxTarget - cropW / 2.0;
                        double cropY = padY + cyTarget - cropH / 2.0;
                        zoomFilter = new FilterChain()
                            .AddRaw(preScale)
                            .AddNode(new PadFilterNode { Width = canvasW.ToString(CultureInfo.InvariantCulture), Height = canvasH.ToString(CultureInfo.InvariantCulture), X = padX.ToString(CultureInfo.InvariantCulture), Y = padY.ToString(CultureInfo.InvariantCulture), Color = "black" })
                            .AddNode(new CropFilterNode { Width = cropW.ToString(CultureInfo.InvariantCulture), Height = cropH.ToString(CultureInfo.InvariantCulture), X = cropX.ToString(CultureInfo.InvariantCulture), Y = cropY.ToString(CultureInfo.InvariantCulture) })
                            .AddNode(new CasFilterNode { Strength = 0.5 })
                            .AddNode(new ScaleFilterNode { Width = outW.ToString(CultureInfo.InvariantCulture), Height = outH.ToString(CultureInfo.InvariantCulture) })
                            .ToFFmpegString() + ",";
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

                        // G05: SLOW RAMP — the ONLY site that legitimately needs the full
                        // half-frame margin. Here the zoom factor is animated from 1.0 up to
                        // targetZ, so at the START of the ramp the crop window is the ENTIRE
                        // frame (cropW == resW) and ZoomPadMargin would return exactly resW/2
                        // anyway. Written out explicitly so nobody "optimises" it to the instant
                        // path's tighter margin and reintroduces the out-of-bounds crop crashes
                        // that changelog item (10) fixed.
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

                        string cropX = $"(({cxExpr})*({zExpr})*{sStr})";
                        string cropY = $"(({cyExpr})*({zExpr})*{sStr})";

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
                    fullParts.Add(
                        $"{aSrc}atrim=start={chunk.Start:F4}:end={chunk.End:F4}," +
                        $"aresample=48000:async=1:min_comp=0.001," +
                        $"asetpts='PTS-({chunk.Start:F4}/TB)'," +
                        $"{string.Join(",", audioFilters)}," +
                        $"apad,atrim=duration={quantizedDur.ToString("F5", CultureInfo.InvariantCulture)}," +
                        $"asetpts=PTS-STARTPTS{aChunkLabel}");
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

        double cropW = resW / zVal;
        double cropH = resH / zVal;
        // G05: this builds a CONSTANT crop for a fixed zoom progress `p`, so cropW/cropH do not
        // change across the chunk and the minimal safe margin is cropW/2 (see ZoomPadMargin).
        // At p=0 (zVal=1) this degrades gracefully to resW/2 — the old value — so the
        // no-zoom-yet case is byte-identical.
        double padX = ZoomPadMargin(cropW);
        double padY = ZoomPadMargin(cropH);
        double canvasW = resW + 2.0 * padX;
        double canvasH = resH + 2.0 * padY;

        double cropX = padX + viewCx - cropW / 2.0;
        double cropY = padY + viewCy - cropH / 2.0;

        var ci = CultureInfo.InvariantCulture;
        return new FilterChain()
            .AddRaw(preScale)
            .AddNode(new PadFilterNode { Width = canvasW.ToString(CultureInfo.InvariantCulture), Height = canvasH.ToString(CultureInfo.InvariantCulture), X = padX.ToString(CultureInfo.InvariantCulture), Y = padY.ToString(CultureInfo.InvariantCulture), Color = "black" })
            .AddNode(new CropFilterNode { Width = cropW.ToString(CultureInfo.InvariantCulture), Height = cropH.ToString(CultureInfo.InvariantCulture), X = cropX.ToString(CultureInfo.InvariantCulture), Y = cropY.ToString(CultureInfo.InvariantCulture) })
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

