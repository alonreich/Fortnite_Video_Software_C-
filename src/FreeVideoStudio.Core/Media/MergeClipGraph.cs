// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace FreeVideoStudio.Core.Media;

/// <summary>A meme input already opened by the worker, for one clip's graph.</summary>
/// <param name="InputIndex">FFmpeg input index of the meme file.</param>
/// <param name="AtRelSec">Where it is inserted, seconds from the clip's kept-in point (see <see cref="CompositeTimeline.MemeAtRelSec"/>).</param>
/// <param name="GainDb">MEMELEVEL_02 — gain that matches the meme to its host clip's gameplay loudness (<see cref="MemeLoudness.GainFor"/>); 0 = as recorded.</param>
/// <param name="Mode">MEMEMODE_01 — full-screen cutaway (spliced, the clip grows) or corner overlay (over the body, zero added length).</param>
public sealed record MergeMemeInput(int InputIndex, bool IsImage, bool HasAudio, double DurationSec, double AtRelSec, double GainDb = 0,
    MemePresentationMode Mode = MemePresentationMode.InlineFullScreen,
    MemeOverlayCorner Corner = MemeOverlayCorner.BottomRight,
    MemeOverlaySize Size = MemeOverlaySize.Medium,
    bool PlaySound = true)
{
    public bool IsCornerOverlay => Mode == MemePresentationMode.CornerOverlay;
}

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
        IReadOnlyList<MergeMemeInput> memes,
        string bodyAudioFilter = "",
        int canvasW = 1920,
        int canvasH = 1080)
    {
        var ci = CultureInfo.InvariantCulture;
        string p = $"c{clip}_";
        var filters = new List<string>();
        // The trim starts 0.5 ms early (FRAMESNAP_01) so the first kept frame survives. The granular
        // engine's origin stays the NOMINAL cut: measured in the harness, moving the origin to the
        // epsilon instant made chunk edges land worse (+3 frames on a freeze clip) than this (±1).
        double origin = keepStartSec;
        double keepSec = Math.Max(0.001, keepEndSec - keepStartSec);
        double speed = baseSpeed > 0.001 && double.IsFinite(baseSpeed) ? baseSpeed : 1.0;

        // 1. The kept window, starting at 0.
        string ts = MergerWorker.TrimStartSec(keepStartSec).ToString("F6", ci);
        string te = keepEndSec.ToString("F6", ci);
        filters.Add($"{inputVideo}trim=start={ts}:end={te},setpts=PTS-STARTPTS[{p}src_v]");
        string? srcAudio = null;
        if (inputAudio != null)
        {
            filters.Add($"{inputAudio}atrim=start={ts}:end={te},asetpts=PTS-STARTPTS,aformat=sample_fmts=fltp:channel_layouts=stereo:sample_rates=48000[{p}src_a]");
            srcAudio = $"[{p}src_a]";
        }

        // 2. The Main App's granular engine, in source pixels. Positions are ABSOLUTE source ms with
        //    the kept-in point as origin, exactly as ProcessWorker calls it.
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

        // 3. Canvas + CFR, same normalisation as a plain clip.
        filters.Add($"{gV}{canvasChain},fps=60:start_time=0:round=near[{p}body_v]");
        // CLIPLEVEL_01 / PEAKSAFE_01 — the clip's own loudness match and peak tamer (leading comma,
        // empty when neither is on) act on the GAMEPLAY body only, before memes are spliced in.
        filters.Add($"{gA}aformat=sample_fmts=fltp:channel_layouts=stereo:sample_rates=48000{bodyAudioFilter}[{p}body_a]");
        string bodyV = $"[{p}body_v]", bodyA = $"[{p}body_a]";

        // 3b. MEMEMODE_01 — corner overlays ride on the clip's BODY (before any full-screen meme is
        //     spliced in), at the body output time of their anchor. They add zero seconds: the body is
        //     the main input of every overlay and the first input of the amix (FFM-MEMECORNER).
        var cornerInputs = new List<CornerMemeInput>();
        foreach (var m in memes)
        {
            if (!m.IsCornerOverlay) continue;
            double start = SnapFrame(Math.Clamp(BodyOutputSec(m, mapper, keepStartSec), 0, bodyDur));
            var vis = MemePlacement.VisibleInterval(start, m.DurationSec, bodyDur);
            if (vis is not { } v) continue;
            cornerInputs.Add(new CornerMemeInput(m.InputIndex, m.IsImage, m.HasAudio, v.StartSec, v.EndSec,
                m.Corner, m.Size, m.PlaySound, m.GainDb));
        }
        if (cornerInputs.Count > 0)
        {
            var overlaid = CornerMemeOverlayGraph.Build(bodyV, bodyA, cornerInputs, canvasW, canvasH, "60", p);
            filters.AddRange(overlaid.Filters);
            bodyV = overlaid.VideoLabel;
            bodyA = overlaid.AudioLabel;
        }

        // 4. Full-screen memes, at their output times inside this clip.
        var placed = memes
            .Where(m => !m.IsCornerOverlay && m.DurationSec > 0.001)
            .Select(m => (Meme: m, Cut: SnapFrame(Math.Clamp(BodyOutputSec(m, mapper, keepStartSec), 0, bodyDur))))
            .OrderBy(x => x.Cut)
            .ToList();
        if (placed.Count == 0)
            return new MergeClipGraphResult(filters, bodyV, bodyA, bodyDur, cornerInputs.Count);

        // Distinct cut points; a cut within one frame of the start/end, or of the previous one, is merged.
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
            // SPLICE_03 — a meme butt-joins this body at its start or end: de-click both edges.
            filters.Add($"{bodyA}anull{MemeLoudness.SpliceFade(bodyDur)}[{p}pa0]");
            pieceA[0] = $"[{p}pa0]";
        }
        else
        {
            filters.Add($"{bodyV}split={pieces}{string.Concat(Enumerable.Range(0, pieces).Select(k => $"[{p}pv{k}_in]"))}");
            filters.Add($"{bodyA}asplit={pieces}{string.Concat(Enumerable.Range(0, pieces).Select(k => $"[{p}pa{k}_in]"))}");
            for (int k = 0; k < pieces; k++)
            {
                string a = bounds[k].ToString("F6", ci), b = bounds[k + 1].ToString("F6", ci);
                filters.Add($"[{p}pv{k}_in]trim=start={a}:end={b},setpts=PTS-STARTPTS[{p}pv{k}]");
                filters.Add($"[{p}pa{k}_in]atrim=start={a}:end={b},asetpts=PTS-STARTPTS{MemeLoudness.SpliceFade(bounds[k + 1] - bounds[k])}[{p}pa{k}]");
                pieceV[k] = $"[{p}pv{k}]";
                pieceA[k] = $"[{p}pa{k}]";
            }
        }

        // Meme streams, normalised to the canvas and CFR, silence when the meme has no sound.
        var memeLabels = new List<(double At, string V, string A, double Dur)>();
        for (int k = 0; k < placed.Count; k++)
        {
            var m = placed[k].Meme;
            string d = m.DurationSec.ToString("F6", ci);
            string mv = $"[{p}m{k}_v]", ma = $"[{p}m{k}_a]";
            filters.Add($"[{m.InputIndex}:v]trim=duration={d},setpts=PTS-STARTPTS,{memeCanvasChain},setsar=1,fps=60:start_time=0:round=near," +
                        $"tpad=stop_mode=clone:stop_duration={d},trim=duration={d},setpts=PTS-STARTPTS{mv}");
            filters.Add(m.HasAudio && !m.IsImage && m.PlaySound   // MEMEMODE_01 — sound off = silence of its length
                ? $"[{m.InputIndex}:a]atrim=duration={d},asetpts=PTS-STARTPTS,aformat=sample_fmts=fltp:channel_layouts=stereo:sample_rates=48000{MemeLoudness.Chain(m.GainDb)},apad,atrim=duration={d}{MemeLoudness.SpliceFade(m.DurationSec)}{ma}"
                : $"anullsrc=r=48000:cl=stereo,atrim=duration={d},asetpts=PTS-STARTPTS{ma}");
            memeLabels.Add((NearestCut(placed[k].Cut), mv, ma, m.DurationSec));
        }

        // Interleave: piece 0, memes at bound[1], piece 1, ... ; memes at 0 lead, memes at the end trail.
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
        return new MergeClipGraphResult(filters, $"[{p}fx_v]", $"[{p}fx_a]", total, placed.Count + cornerInputs.Count);
    }

    /// <summary>The meme's cut point in this clip's BODY output (before any meme), in seconds.</summary>
    private static double BodyOutputSec(MergeMemeInput m, Func<double, double> mapper, double keepStartSec)
        => m.AtRelSec <= 0.0005 ? 0 : mapper(keepStartSec + m.AtRelSec);

    private static double SnapFrame(double sec) => Math.Round(sec * CompositeTimeline.MergeFps) / CompositeTimeline.MergeFps;
}
