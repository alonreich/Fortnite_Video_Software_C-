
using System.Globalization;
using System.Text;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// A speed or freeze segment for the granular timeline.
/// </summary>
public record SpeedSegment(double StartMs, double EndMs, double Speed);

/// <summary>
/// Builds the FFmpeg filter_complex for granular speed control.
/// 
/// Rules (MANDATORY from architecture contract):
/// - Segments are "Solid Objects" — overlapping is prohibited
/// - Minimum segment duration: 10ms
/// - Freeze frame: inserts still at playhead, shifts gameplay forward (does NOT discard)
/// - Audio: atempo chained for values outside [0.5, 2.0]
/// </summary>
public class GranularSpeedBuilder
{
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
    /// Builds the complete granular speed filter chain.
    /// Returns (filterString, videoOutputLabel, audioOutputLabel, finalDurationSec, timeMapper).
    /// Exact port of build_granular_speed_chain().
    /// </summary>
    public static (string filterGraph, string videoLabel, string audioLabel, double finalDuration, Func<double, double> timeMapper) Build(
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
        if (segments != null)
        {
            foreach (var seg in segments)
            {
                double start = ToClipRelative(seg.StartMs / 1000.0);
                double end = ToClipRelative(seg.EndMs / 1000.0);
                double speed = seg.Speed;
                if (end <= start + 0.001) continue;
                normalizedSegments.Add((start, end, speed));
            }
        }
        normalizedSegments.Sort((a, b) => a.start.CompareTo(b.start));

        var sourceChunks = new List<(double start, double end, double speed)>();
        double currentSec = 0;

        var speedSegs = normalizedSegments.Where(s => Math.Abs(s.speed) > 0.001).ToList();
        foreach (var seg in speedSegs)
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

        var freezes = normalizedSegments.Where(s => Math.Abs(s.speed) < 0.001)
            .OrderBy(f => f.start).ToList();

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

        foreach (var f in freezes)
        {
            double fStart = Math.Max(sourceCursor, f.start);
            double fEnd = Math.Max(fStart, f.end);
            if (fEnd <= sourceCursor + 0.001) continue;

            if (fStart > sourceCursor + 0.001)
                AppendSourceRange(sourceCursor, fStart);

            double fDur = Math.Max(0.001, fEnd - fStart);
            chunks.Add(new ChunkSpec(fStart, fStart + 0.001, 0, fDur));
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
            var audioFilters = BuildAtempoChain(baseSpeed);
            string aChain = !string.IsNullOrEmpty(inputAudioLabel)
                ? $"{inputAudioLabel}asetpts=PTS-STARTPTS,{string.Join(",", audioFilters)},aresample=48000:async=1[a_speed_out]"
                : $"anullsrc=r=48000:cl=stereo,atrim=duration={totalDurationSec / baseSpeed:F4},asetpts=PTS-STARTPTS[a_speed_out]";
            return (string.Join(";", [.. preChainParts, vChain, aChain]), "[v_speed_out]", "[a_speed_out]",
                totalDurationSec / baseSpeed, TimeMapper);
        }

        var fullParts = new List<string>(preChainParts);
        var vAPads = new List<string>();
        double finalDuration = 0;

        string vSplits = "";
        for (int i = 0; i < nChunks; i++) vSplits += $"[v_split_{i}]";
        fullParts.Add($"{inputVideoLabel}split={nChunks}{vSplits}");

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
            string vSrc = $"[v_split_{i}]";
            string? aSrc = !string.IsNullOrEmpty(inputAudioLabel) ? $"[a_split_{i}]" : null;
            string vChunkLabel = $"[v_chunk_{i}]";
            string aChunkLabel = $"[a_chunk_{i}]";

            if (Math.Abs(chunk.Speed) < 0.001)
            {
                double dur = chunk.FreezeDur;
                int targetFrameCount = Math.Max(1, (int)Math.Round(dur * fpsValue));
                int loopFrames = Math.Max(0, targetFrameCount - 1);
                double sampleWindow = Math.Max(4.0 / fpsValue, 0.20);
                double sampleUntil = Math.Min(totalDurationSec, chunk.Start + sampleWindow);
                double sampleWindowActual = Math.Max(1.0 / fpsValue, sampleUntil - chunk.Start);

                fullParts.Add(
                    $"{vSrc}trim=start={chunk.Start:F4}:duration={sampleWindowActual:F4}," +
                    $"setpts=PTS-STARTPTS," +
                    $"select='lte(n\\,0)'," +
                    $"format=yuv420p,setsar=1," +
                    $"loop=loop={loopFrames}:size=1:start=0," +
                    $"fps={targetFps}:round=near," +
                    $"setpts=N/({targetFps})/TB," +
                    $"trim=duration={dur:F4},setpts=PTS-STARTPTS{vChunkLabel}");

                if (!string.IsNullOrEmpty(aSrc))
                    fullParts.Add($"{aSrc}anullsink");

                fullParts.Add($"anullsrc=r=48000:cl=stereo," +
                              $"atrim=duration={dur:F4},asetpts=PTS-STARTPTS{aChunkLabel}");

                finalDuration += dur;
                FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Info("FFmpeg", $"FFmpeg Instructions: Freeze Frame detected at {chunk.Start:F4}s. Built chunked Granular Speed complex filter. " +
                                          $"Math: Held video frame for exactly {dur:F4}s. Offset PTS translated forward. " +
                                          $"Audio Routing: Muted/silenced original audio by generating anullsrc spanning [{chunk.Start:F4}s -> {chunk.Start + dur:F4}s].");
            }
            else
            {
                double outDur = (chunk.End - chunk.Start) / chunk.Speed;
                fullParts.Add(
                    $"{vSrc}trim=start={chunk.Start:F4}:end={chunk.End:F4}," +
                    $"setpts=PTS-STARTPTS," +
                    $"setpts='PTS/{chunk.Speed:F4}'," +
                    $"format=yuv420p,setsar=1{vChunkLabel}");

                var audioFilters = BuildAtempoChain(chunk.Speed);
                if (!string.IsNullOrEmpty(aSrc))
                {
                    fullParts.Add(
                        $"{aSrc}atrim=start={chunk.Start:F4}:end={chunk.End:F4}," +
                        $"asetpts=PTS-STARTPTS," +
                        $"{string.Join(",", audioFilters)}," +
                        $"asetpts=PTS-STARTPTS," +
                        $"aresample=48000:async=1:min_comp=0.001{aChunkLabel}");
                }
                else
                {
                    fullParts.Add($"anullsrc=r=48000:cl=stereo," +
                                  $"atrim=duration={outDur:F4},asetpts=PTS-STARTPTS{aChunkLabel}");
                }

                finalDuration += outDur;
            }

            vAPads.Add($"{vChunkLabel}{aChunkLabel}");
        }

        fullParts.Add($"{string.Join("", vAPads)}concat=n={nChunks}:v=1:a=1[v_speed_concat][a_speed_concat]");
        fullParts.Add("[v_speed_concat]setpts=PTS-STARTPTS[v_speed_out]");
        fullParts.Add("[a_speed_concat]aresample=48000:async=1:min_comp=0.01," +
                      "asetpts=PTS-STARTPTS[a_speed_out]");

        return (string.Join(";", fullParts), "[v_speed_out]", "[a_speed_out]",
            finalDuration, TimeMapper);
    }

    /// <summary>
    /// Builds the atempo filter chain for a given speed.
    /// FFmpeg atempo limits: [0.5, 2.0] per filter. Chain for values outside.
    /// </summary>
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

    private record ChunkSpec(double Start, double End, double Speed, double FreezeDur = 0);
}
