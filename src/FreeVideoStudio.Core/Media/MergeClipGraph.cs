using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace FreeVideoStudio.Core.Media;

/// <summary>A meme input already opened by the worker, for one clip's graph.</summary>
/// <param name="InputIndex">FFmpeg input index of the meme file.</param>
/// <param name="AtRelSec">Where it is inserted, seconds from the clip's kept-in point (see <see cref="CompositeTimeline.MemeAtRelSec"/>).</param>
/// <param name="GainDb">MEMELEVEL_01 — loudness gain to the Main App's meme target (<see cref="MemeLoudness"/>); 0 = as recorded.</param>
public sealed record MergeMemeInput(int InputIndex, bool IsImage, bool HasAudio, double DurationSec, double AtRelSec, double GainDb = 0);

/// <summary>What one clip contributes to the merge graph.</summary>
public sealed record MergeClipGraphResult(IReadOnlyList<string> Filters, string VideoLabel, string AudioLabel, double DurationSec, int MemeCount);

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// MERGEGRAPH_01 — ONE CLIP WITH GRANULAR EFFECTS, AS FFMPEG FILTERS (Video-Merger-Migration.md P7.1).
///
/// The Main App's export engine for speed/freeze/zoom/cuts (<see cref="GranularSpeedBuilder"/>) is
/// reused as-is, per clip, so a ramp in the Merger renders exactly like the same ramp in the Main
/// App. Order matters and is fixed:
///   1. trim the clip's kept window (FRAMESNAP_01 epsilon) — timestamps start at 0;
///   2. granular chain in SOURCE pixels (zoom rectangles are source px, so no scaling before it);
///   3. the clip's canvas chain (colour + scale/pad|crop + setsar) and fps=60 — same as plain clips;
///   4. memes spliced into the clip's own output (D4: effects stay inside their clip).
/// <see cref="GranularSpeedBuilder.Build"/> names its pads with fixed labels, so every label of its
/// graph is prefixed per clip; many clips then share one filter graph safely.
/// Pure: no process, no disk. Clips without effects never come here (their chain is unchanged).
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class MergeClipGraph
{
    private static readonly Regex LabelPattern = new(@"\[([A-Za-z_][A-Za-z0-9_]*)\]", RegexOptions.CultureInvariant);

    /// <summary>A piece shorter than one output frame is not emitted.</summary>
    private const double MinPieceSec = 1.0 / CompositeTimeline.MergeFps;

    public static MergeClipGraphResult Build(
        int clip,
        string inputVideo,
        string? inputAudio,
        double keepStartSec,
        double keepEndSec,
        EdlEffects fx,
        double baseSpeed,
        string canvasChain,
        string memeCanvasChain,
        IReadOnlyList<MergeMemeInput> memes)
    {
        var ci = CultureInfo.InvariantCulture;
        string p = $"c{clip}_";
        var filters = new List<string>();
        double origin = keepStartSec;
        double keepSec = Math.Max(0.001, keepEndSec - keepStartSec);
        double speed = baseSpeed > 0.001 && double.IsFinite(baseSpeed) ? baseSpeed : 1.0;

        string ts = MergerWorker.TrimStartSec(keepStartSec).ToString("F6", ci);
        string te = keepEndSec.ToString("F6", ci);
        filters.Add($"{inputVideo}trim=start={ts}:end={te},setpts=PTS-STARTPTS[{p}src_v]");
        string? srcAudio = null;
        if (inputAudio != null)
        {
            filters.Add($"{inputAudio}atrim=start={ts}:end={te},asetpts=PTS-STARTPTS,aformat=sample_fmts=fltp:channel_layouts=stereo:sample_rates=48000[{p}src_a]");
            srcAudio = $"[{p}src_a]";
        }

        var segments = new List<SpeedSegment>(fx.Speed.Count + fx.Freezes.Count);
        foreach (var s in fx.Speed)
        {
            var z = s.Zoom;
            segments.Add(new SpeedSegment(s.StartUs / 1000.0, s.EndUs / 1000.0, s.Speed,
                z?.X, z?.Y, z?.W, z?.H,
                z is { SourceW: > 0, SourceH: > 0 } ? $"{z.SourceW}x{z.SourceH}" : null,
                z?.Slow ?? false,
                z?.StartUs is long zs ? zs / 1000.0 : null,
                z?.EndUs is long ze ? ze / 1000.0 : null,
                z?.AiTrackingTrajectory));
        }
        foreach (var f in fx.Freezes)
            segments.Add(new SpeedSegment(f.AtUs / 1000.0, f.AtUs / 1000.0 + f.DurationSec * 1000.0, 0));
        long keepInUs = (long)Math.Round(origin * 1_000_000.0);
        var cuts = new List<OutputTimeline.Cut>(fx.Cuts.Count);
        foreach (var k in fx.Cuts) cuts.Add(new OutputTimeline.Cut((k.StartUs - keepInUs) / 1_000_000.0, (k.EndUs - keepInUs) / 1_000_000.0));

        var (graph, gV, _, gA, bodyDur, mapper) = GranularSpeedBuilder.Build(
            keepSec * 1000.0, segments, speed, origin * 1000.0,
            $"[{p}src_v]", srcAudio, "60", needHudBranch: false, cuts: cuts);

        string Prefix(string text) => LabelPattern.Replace(text, m =>
            m.Groups[1].Value.StartsWith(p, StringComparison.Ordinal) ? m.Value : $"[{p}{m.Groups[1].Value}]");
        filters.AddRange(Prefix(graph).Split(';', StringSplitOptions.RemoveEmptyEntries));
        gV = Prefix(gV);
        gA = Prefix(gA);

        filters.Add($"{gV}{canvasChain},fps=60:start_time=0:round=near[{p}body_v]");
        filters.Add($"{gA}aformat=sample_fmts=fltp:channel_layouts=stereo:sample_rates=48000[{p}body_a]");
        string bodyV = $"[{p}body_v]", bodyA = $"[{p}body_a]";

        var placed = memes
            .Where(m => m.DurationSec > 0.001)
            .Select(m => (Meme: m, Cut: SnapFrame(Math.Clamp(BodyOutputSec(m, mapper, keepStartSec), 0, bodyDur))))
            .OrderBy(x => x.Cut)
            .ToList();
        if (placed.Count == 0)
            return new MergeClipGraphResult(filters, bodyV, bodyA, bodyDur, 0);

        var cutPoints = new List<double>();
        foreach (var (_, cut) in placed)
        {
            if (cut < MinPieceSec || cut > bodyDur - MinPieceSec) continue;
            if (cutPoints.Count > 0 && cut - cutPoints[^1] < MinPieceSec) continue;
            cutPoints.Add(cut);
        }
        double NearestCut(double cut)
        {
            if (cut < MinPieceSec) return 0;
            if (cut > bodyDur - MinPieceSec) return bodyDur;
            return cutPoints.OrderBy(c => Math.Abs(c - cut)).First();
        }

        var bounds = new List<double> { 0 };
        bounds.AddRange(cutPoints);
        bounds.Add(bodyDur);
        int pieces = bounds.Count - 1;

        var pieceV = new string[pieces];
        var pieceA = new string[pieces];
        if (pieces == 1)
        {
            pieceV[0] = bodyV;
            pieceA[0] = bodyA;
        }
        else
        {
            filters.Add($"{bodyV}split={pieces}{string.Concat(Enumerable.Range(0, pieces).Select(k => $"[{p}pv{k}_in]"))}");
            filters.Add($"{bodyA}asplit={pieces}{string.Concat(Enumerable.Range(0, pieces).Select(k => $"[{p}pa{k}_in]"))}");
            for (int k = 0; k < pieces; k++)
            {
                string a = bounds[k].ToString("F6", ci), b = bounds[k + 1].ToString("F6", ci);
                filters.Add($"[{p}pv{k}_in]trim=start={a}:end={b},setpts=PTS-STARTPTS[{p}pv{k}]");
                filters.Add($"[{p}pa{k}_in]atrim=start={a}:end={b},asetpts=PTS-STARTPTS[{p}pa{k}]");
                pieceV[k] = $"[{p}pv{k}]";
                pieceA[k] = $"[{p}pa{k}]";
            }
        }

        var memeLabels = new List<(double At, string V, string A, double Dur)>();
        for (int k = 0; k < placed.Count; k++)
        {
            var m = placed[k].Meme;
            string d = m.DurationSec.ToString("F6", ci);
            string mv = $"[{p}m{k}_v]", ma = $"[{p}m{k}_a]";
            filters.Add($"[{m.InputIndex}:v]trim=duration={d},setpts=PTS-STARTPTS,{memeCanvasChain},setsar=1,fps=60:start_time=0:round=near," +
                        $"tpad=stop_mode=clone:stop_duration={d},trim=duration={d},setpts=PTS-STARTPTS{mv}");
            filters.Add(m.HasAudio && !m.IsImage
                ? $"[{m.InputIndex}:a]atrim=duration={d},asetpts=PTS-STARTPTS,aformat=sample_fmts=fltp:channel_layouts=stereo:sample_rates=48000{MemeLoudness.Chain(m.GainDb)},apad,atrim=duration={d}{ma}"
                : $"anullsrc=r=48000:cl=stereo,atrim=duration={d},asetpts=PTS-STARTPTS{ma}");
            memeLabels.Add((NearestCut(placed[k].Cut), mv, ma, m.DurationSec));
        }

        var order = new List<(string V, string A)>();
        double total = bodyDur;
        for (int k = 0; k <= pieces; k++)
        {
            double at = bounds[k];
            foreach (var ml in memeLabels.Where(x => Math.Abs(x.At - at) < 1e-9))
            {
                order.Add((ml.V, ml.A));
                total += ml.Dur;
            }
            if (k < pieces) order.Add((pieceV[k], pieceA[k]));
        }
        filters.Add($"{string.Concat(order.Select(o => o.V + o.A))}concat=n={order.Count}:v=1:a=1[{p}fx_v][{p}fx_a]");
        return new MergeClipGraphResult(filters, $"[{p}fx_v]", $"[{p}fx_a]", total, placed.Count);
    }

    /// <summary>The meme's cut point in this clip's BODY output (before any meme), in seconds.</summary>
    private static double BodyOutputSec(MergeMemeInput m, Func<double, double> mapper, double keepStartSec)
        => m.AtRelSec <= 0.0005 ? 0 : mapper(keepStartSec + m.AtRelSec);

    private static double SnapFrame(double sec) => Math.Round(sec * CompositeTimeline.MergeFps) / CompositeTimeline.MergeFps;
}
