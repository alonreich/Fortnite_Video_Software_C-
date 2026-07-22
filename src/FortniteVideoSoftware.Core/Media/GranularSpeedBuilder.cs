
using System.Globalization;
using System.Text;

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

    public static (string filterGraph, string videoLabel, string hudLabel, string audioLabel, double finalDuration, Func<double, double> timeMapper) Build(
        double totalDurationMs,
        List<SpeedSegment>? segments,
        double baseSpeed = 1.0,
        double sourceCutStartMs = 0,
        string inputVideoLabel = "[0:v]",
        string? inputAudioLabel = "[0:a]",
        string targetFps = "60")
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

            double availBefore = z.StartSec - prevEnd;
            double stealBefore = 0.0;
            if (availBefore >= 0.5) stealBefore = Math.Min(1.0, availBefore);

            double availAfter = nextStart - z.EndSec;
            double stealAfter = 0.0;
            if (availAfter >= 0.5) stealAfter = Math.Min(1.0, availAfter);

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
            string vChain = $"{inputVideoLabel}setpts='(PTS-STARTPTS)/{baseSpeed:F4}'[v_speed_out]";
            string vChainHud = $"{inputVideoLabel}setpts='(PTS-STARTPTS)/{baseSpeed:F4}'[v_hud_out]";
            var audioFilters = BuildAtempoChain(baseSpeed);
            string aChain = !string.IsNullOrEmpty(inputAudioLabel)
                ? $"{inputAudioLabel}aresample=48000:async=1,asetpts=PTS-STARTPTS,{string.Join(",", audioFilters)}[a_speed_out]"
                : $"anullsrc=r=48000:cl=stereo,atrim=duration={totalDurationSec / baseSpeed:F4},asetpts=PTS-STARTPTS[a_speed_out]";
            return (string.Join(";", [.. preChainParts, vChain, vChainHud, aChain]), "[v_speed_out]", "[v_hud_out]", "[a_speed_out]",
                totalDurationSec / baseSpeed, TimeMapper);
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
            vSplitsHud += $"[v_split_hud_{i}]";
        }
        fullParts.Add($"{inputVideoLabel}split={nChunks * 2}{vSplitsMain}{vSplitsHud}");

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

                string preScale = $"scale={resW}:{resH}:force_original_aspect_ratio=decrease,pad={resW}:{resH}:(ow-iw)/2:(oh-ih)/2,";
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
                        double cropX = resW + cxTarget - cropW / 2.0;
                        double cropY = resH + cyTarget - cropH / 2.0;
                        zoomFilter = $"{preScale}pad=iw+{resW * 2}:ih+{resH * 2}:{resW}:{resH}:color=black,crop=w='{cropW.ToString(CultureInfo.InvariantCulture)}':h='{cropH.ToString(CultureInfo.InvariantCulture)}':x='{cropX.ToString(CultureInfo.InvariantCulture)}':y='{cropY.ToString(CultureInfo.InvariantCulture)}',cas=0.5,scale={outW}:{outH},";
                    }
                    else
                    {
                        double targetZ = Math.Min(resW / zc.W, resH / zc.H);
                        double cxTarget = zc.X + zc.W / 2.0;
                        double cyTarget = zc.Y + zc.H / 2.0;
                        double zVal = 1.0 + (targetZ - 1.0) * p1;
                        double xOrig = (resW + resW / 2.0) + (cxTarget - resW / 2.0) * p1 - (resW / zVal) / 2.0;
                        double yOrig = (resH + resH / 2.0) + (cyTarget - resH / 2.0) * p1 - (resH / zVal) / 2.0;
                        double cropX = xOrig * zVal;
                        double cropY = yOrig * zVal;
                        zoomFilter = $"{preScale}pad=iw+{resW * 2}:ih+{resH * 2}:{resW}:{resH}:color=black,scale=w='iw*({zVal.ToString(CultureInfo.InvariantCulture)})':h='ih*({zVal.ToString(CultureInfo.InvariantCulture)})':eval=frame,crop=w={resW}:h={resH}:x='{cropX.ToString(CultureInfo.InvariantCulture)}':y='{cropY.ToString(CultureInfo.InvariantCulture)}',cas=0.5,scale={outW}:{outH},";
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
                        double cropX = resW + cxTarget - cropW / 2.0;
                        double cropY = resH + cyTarget - cropH / 2.0;
                        zoomFilter = $"{preScale}pad=iw+{resW * 2}:ih+{resH * 2}:{resW}:{resH}:color=black,crop=w='{cropW.ToString(CultureInfo.InvariantCulture)}':h='{cropH.ToString(CultureInfo.InvariantCulture)}':x='{cropX.ToString(CultureInfo.InvariantCulture)}':y='{cropY.ToString(CultureInfo.InvariantCulture)}',cas=0.5,scale={outW}:{outH},";
                    }
                    else
                    {
                        double realDur = chunk.End - chunk.Start;
                        double targetZ = Math.Min(resW / zc.W, resH / zc.H);
                        double cxTarget = zc.X + zc.W / 2.0;
                        double cyTarget = zc.Y + zc.H / 2.0;

                        string pExpr = Math.Abs(p1 - p2) < 0.001 ? p1.ToString(CultureInfo.InvariantCulture)
                                     : $"{p1.ToString(CultureInfo.InvariantCulture)}+({(p2 - p1).ToString(CultureInfo.InvariantCulture)})*(t/{realDur.ToString(CultureInfo.InvariantCulture)})";

                        string zExpr = $"1.0+({(targetZ - 1.0).ToString(CultureInfo.InvariantCulture)})*({pExpr})";
                        string cxExpr = $"(({resW} + {resW / 2.0}) + ({(cxTarget - resW / 2.0).ToString(CultureInfo.InvariantCulture)})*({pExpr}) - ({resW}/({zExpr}))/2.0)";
                        string cyExpr = $"(({resH} + {resH / 2.0}) + ({(cyTarget - resH / 2.0).ToString(CultureInfo.InvariantCulture)})*({pExpr}) - ({resH}/({zExpr}))/2.0)";

                        string cropX = $"({cxExpr})*({zExpr})";
                        string cropY = $"({cyExpr})*({zExpr})";

                        zoomFilter = $"{preScale}pad=iw+{resW * 2}:ih+{resH * 2}:{resW}:{resH}:color=black,scale=w='iw*({zExpr})':h='ih*({zExpr})':eval=frame,crop=w={resW}:h={resH}:x='{cropX}':y='{cropY}',cas=0.5,scale={outW}:{outH},";
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

                fullParts.Add(
                    $"{vSrcHud}trim=start={chunk.Start:F4}:duration={sampleWindowActual:F4}," +
                    $"setpts=PTS-STARTPTS," +
                    $"select='lte(n\\,0)'," +
                    $"format=yuv420p,setsar=1," +
                    $"tpad=stop_mode=clone:stop_duration={freezeQuantDur.ToString("F5", CultureInfo.InvariantCulture)}," +
                    $"fps={targetFps}:round=near," +
                    $"setpts=N/({targetFps})/TB," +
                    $"trim=duration={freezeQuantDur.ToString("F5", CultureInfo.InvariantCulture)},setpts=PTS-STARTPTS{vChunkHudLabel}");

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

                fullParts.Add(
                    $"{vSrcHud}trim=start={chunk.Start:F4}:end={chunk.End:F4}," +
                    $"setpts='PTS-({chunk.Start:F4}/TB)'," +
                    $"setpts='PTS/{chunk.Speed:F4}'," +
                    $"fps={targetFps}:start_time=0:round=near," +
                    $"format=yuv420p,setsar=1{vChunkHudLabel}");

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
            vHudPads.Add(vChunkHudLabel);
            aPads.Add(aChunkLabel);
        }

        fullParts.Add($"{string.Join("", vMainPads)}concat=n={nChunks}:v=1:a=0[v_speed_concat]");
        fullParts.Add($"{string.Join("", vHudPads)}concat=n={nChunks}:v=1:a=0[v_hud_concat]");
        fullParts.Add($"{string.Join("", aPads)}concat=n={nChunks}:v=0:a=1[a_speed_concat]");

        fullParts.Add("[v_speed_concat]setpts=PTS-STARTPTS[v_speed_out]");
        fullParts.Add("[v_hud_concat]setpts=PTS-STARTPTS[v_hud_out]");
        fullParts.Add("[a_speed_concat]aresample=48000:async=1:min_comp=0.01,asetpts=PTS-STARTPTS[a_speed_out]");

        return (string.Join(";", fullParts), "[v_speed_out]", "[v_hud_out]", "[a_speed_out]",
            finalDuration, TimeMapper);
    }

    public static List<string> BuildAtempoChain(double speed)
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

