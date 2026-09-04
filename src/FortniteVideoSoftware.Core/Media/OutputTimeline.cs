using System;
using System.Collections.Generic;
using System.Linq;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// TIME_01 — THE SINGLE SOURCE OF TRUTH FOR "HOW LONG IS THE FINISHED VIDEO, AND WHICH SOURCE
/// FRAME IS PLAYING AT A GIVEN MOMENT OF IT".
///
/// <para>
/// Before this type existed the same mathematics was written by hand in THREE places:
/// <list type="bullet">
///   <item><description><c>GranularSpeedBuilder.CreateTimeMapper</c> — source to output.</description></item>
///   <item><description><c>GranularSpeedBuilder.Build</c>'s local <c>TimeMapper</c> — source to output, again.</description></item>
///   <item><description><c>MusicWizardWindow.MapPhase3OutputToSourceRelativeSeconds</c> — output to source.</description></item>
/// </list>
/// They drifted, exactly as duplicated maths always does. The Music Wizard's copy treated a freeze
/// as REPLACING the footage underneath it (<c>cursor = Math.Max(cursor, segEnd)</c>), while the
/// export treats a freeze as an INSERTION that holds a frame and then carries on playing from the
/// same spot (<c>sourceCursor = fStart</c>). Measured consequence, with the export as ground truth:
/// </para>
/// <code>
/// scenario                    EXPORT truth   Wizard said       ERROR
/// no segments                     60.000s      60.000s     +0.000s
/// slow-mo 10-20s @0.5x            70.000s      70.000s     +0.000s
/// FREEZE 5-8s (3s)                63.000s      60.000s     -3.000s
/// FREEZE 1.5s @20s                61.500s      60.000s     -1.500s
/// two freezes                     63.000s      60.000s     -3.000s
/// freeze + slow-mo                73.000s      70.000s     -3.000s
/// base 2x + freeze                33.000s      31.500s     -1.500s
/// </code>
/// <para>
/// The export behaviour is the correct one and is what this type implements: a 1.5 second freeze
/// makes the finished video 1.5 seconds longer, and nothing is skipped.
/// </para>
///
/// <para>
/// TIME UNITS AND FRAMES OF REFERENCE — read before touching anything here:
/// <list type="bullet">
///   <item><description><b>Absolute source seconds</b> — a position in the ORIGINAL file, including
///   any trim offset. <see cref="SourceToOutput"/> takes these.</description></item>
///   <item><description><b>Clip-relative source seconds</b> — measured from the trim-in point, so
///   the clip always starts at 0. <see cref="OutputToSourceRelative"/> returns these.</description></item>
///   <item><description><b>Output seconds</b> — a position in the FINISHED video, after speed
///   changes and freezes have stretched it.</description></item>
/// </list>
/// This asymmetry is not a design choice, it is the contract the six existing callers already rely
/// on, preserved deliberately so this refactor changes no behaviour outside the Music Wizard.
/// </para>
///
/// <para>
/// Phase 3 of MEME_TIMELINE_PLAN.txt will add meme insertions to this same chunk list. That is the
/// entire reason the model is shaped around a chunk list rather than a closed-form formula.
/// </para>
/// </summary>
public sealed class OutputTimeline
{
    /// <summary>
    /// One continuous stretch of the finished video.
    /// <para>
    /// A normal chunk plays source footage from <see cref="SourceStartSec"/> to
    /// <see cref="SourceEndSec"/> at <see cref="Speed"/>. A freeze chunk holds the single frame at
    /// <see cref="SourceStartSec"/> for <see cref="FreezeHoldSec"/> and consumes NO source time —
    /// which is why its <see cref="SourceEndSec"/> is only a hair past its start.
    /// </para>
    /// </summary>
    public readonly record struct Chunk(
        double SourceStartSec,
        double SourceEndSec,
        double Speed,
        double FreezeHoldSec,
        string? InsertionId = null,
        bool FromSegment = false,
        bool IsCut = false)
    {
        /// <summary>A held frame from the source. Occupies output time, consumes no source time.</summary>
        public bool IsFreeze => Math.Abs(Speed) < 0.001 && InsertionId == null && !IsCut;

        /// <summary>
        /// MEME_04 — FOREIGN content spliced in (a meme). Like a freeze it occupies output time and
        /// consumes no source time, but unlike a freeze the picture is NOT from the source file, so
        /// the preview cannot show it by seeking mpv.
        /// </summary>
        public bool IsInsertion => InsertionId != null;

        /// <summary>Either kind of block that adds output time without advancing the source.</summary>
        public bool HoldsSource => Math.Abs(Speed) < 0.001 && !IsCut;

        /// <summary>
        /// CUT_01 — REMOVED footage. The exact mirror image of a freeze: a freeze consumes no
        /// source time and occupies output time, a cut consumes source time and occupies NONE.
        /// It is kept in the chunk list rather than deleted so the timeline can still answer
        /// "is this source moment gone?" and "where does the footage resume?" — the preview and
        /// the export both need that, and so does every ruler drawn against source time.
        /// </summary>
        public bool IsCutChunk => IsCut;

        /// <summary>How many seconds of FINISHED video this chunk occupies.</summary>
        public double OutputLengthSec =>
            IsCut ? 0.0 : (HoldsSource ? FreezeHoldSec : (SourceEndSec - SourceStartSec) / Speed);
    }

    /// <summary>
    /// CUT_01 — a stretch of footage the user deleted from the middle of the clip.
    /// CLIP-RELATIVE seconds, like <see cref="Insertion.AtSourceSec"/>.
    /// </summary>
    public readonly record struct Cut(double StartSec, double EndSec)
    {
        public double LengthSec => Math.Max(0, EndSec - StartSec);
    }

    /// <summary>
    /// CUT_01 — two cuts closer than this are merged into one. Below roughly a third of a second
    /// the gap between them is not a piece of video anyone meant to keep, and each surviving
    /// sliver would cost a whole parallel branch in the export graph
    /// (see GranularSpeedBuilder.HighChunkCountWarnThreshold).
    /// </summary>
    public const double CutMergeGapSec = 0.30;

    /// <summary>CUT_01 — a cut shorter than this is discarded as a mis-click / sub-frame drag.</summary>
    public const double MinCutLengthSec = 0.04;

    /// <summary>
    /// MEME_04 — a meme spliced into the middle of the video. <paramref name="AtSourceSec"/> is
    /// CLIP-RELATIVE and must already be snapped (see <see cref="SnapInsertionPoint"/>).
    /// </summary>
    public readonly record struct Insertion(double AtSourceSec, double DurationSec, string Id);

    private readonly List<Chunk> _chunks;
    private readonly double _totalSourceSec;
    private readonly double _originSec;
    private readonly List<Cut> _cuts;

    /// <summary>Length of the trimmed source clip, in seconds. Speed and freezes do not affect it.</summary>
    public double TotalSourceSeconds => _totalSourceSec;

    /// <summary>
    /// Length of the FINISHED video in seconds, with every speed change and freeze applied.
    /// This is the number every ruler in the application should be drawn against.
    /// </summary>
    public double TotalOutputSeconds { get; }

    /// <summary>Trim-in point in seconds — the offset between absolute and clip-relative source time.</summary>
    public double SourceCutStartSeconds => _originSec;

    public IReadOnlyList<Chunk> Chunks => _chunks;

    private OutputTimeline(List<Chunk> chunks, double totalSourceSec, double originSec, List<Cut>? cuts = null)
    {
        _chunks = chunks;
        _totalSourceSec = totalSourceSec;
        _originSec = originSec;
        _cuts = cuts ?? new List<Cut>();

        double total = 0;
        foreach (var c in chunks) total += c.OutputLengthSec;
        TotalOutputSeconds = Math.Max(0, total);
    }

    /// <summary>
    /// Builds the timeline. The chunk construction below is a faithful move of the logic that was
    /// in <c>GranularSpeedBuilder.CreateTimeMapper</c>; it is deliberately NOT "tidied up", because
    /// its exact ordering and its 0.001 epsilons are what make it agree with the exported graph.
    /// </summary>
    public static OutputTimeline Create(
        double totalDurationMs,
        IReadOnlyList<SpeedSegment>? segments,
        double baseSpeed = 1.0,
        double sourceCutStartMs = 0,
        IReadOnlyList<Insertion>? insertions = null,
        IReadOnlyList<Cut>? cuts = null)
    {
        double totalDurationSec = totalDurationMs / 1000.0;
        double timelineOriginSec = sourceCutStartMs / 1000.0;

        double ToClipRel(double absSec)
        {
            double rel = absSec - timelineOriginSec;
            return Math.Max(0, Math.Min(rel, totalDurationSec));
        }

        var normalizedSegments = new List<(double start, double end, double speed)>();
        if (segments != null)
        {
            foreach (var seg in segments)
            {
                double start = ToClipRel(seg.StartMs / 1000.0);
                double end = ToClipRel(seg.EndMs / 1000.0);
                if (end <= start + 0.001) continue;
                normalizedSegments.Add((start, end, seg.Speed));
            }
        }
        normalizedSegments.Sort((a, b) => a.start.CompareTo(b.start));

        var sourceChunks = new List<(double start, double end, double speed, bool fromSeg)>();
        double currentSec = 0;
        foreach (var seg in normalizedSegments.Where(s => Math.Abs(s.speed) > 0.001))
        {
            double sStart = Math.Max(seg.start, currentSec);
            if (seg.end <= sStart + 0.001) continue;
            if (sStart > currentSec + 0.001)
                sourceChunks.Add((currentSec, sStart, baseSpeed, false));
            sourceChunks.Add((sStart, seg.end, seg.speed, true));
            currentSec = seg.end;
        }
        if (currentSec < totalDurationSec - 0.001)
            sourceChunks.Add((currentSec, totalDurationSec, baseSpeed, false));

        // CUT_01 — normalise the cut list ONCE, here, so every consumer downstream sees the same
        // clean set: clip-relative, clamped, ordered, overlaps and near-touching pairs merged, and
        // sub-frame slivers dropped. Doing this at construction is what lets the rest of the method
        // treat cuts as simple non-overlapping holes.
        var normalizedCuts = NormalizeCuts(cuts, totalDurationSec);

        var chunks = new List<Chunk>();
        double sourceCursor = 0;

        bool InsideCut(double at)
        {
            foreach (var c in normalizedCuts)
                if (at > c.StartSec - 0.0005 && at < c.EndSec - 0.0005) return true;
            return false;
        }

        double PushPastCut(double at)
        {
            foreach (var c in normalizedCuts)
                if (at > c.StartSec - 0.0005 && at < c.EndSec - 0.0005) return c.EndSec;
            return at;
        }

        // CUT_01 — every source range that reaches the chunk list flows through here, so this is
        // the one place that has to know about holes. A range overlapping a cut is emitted as
        // [surviving][cut][surviving], keeping the chunks in strict source order, which is the
        // invariant SourceToOutput and OutputToSourceRelative both walk on.
        void AppendSourceRange(double rangeStart, double rangeEnd)
        {
            foreach (var sc in sourceChunks)
            {
                double overlapStart = Math.Max(rangeStart, sc.start);
                double overlapEnd = Math.Min(rangeEnd, sc.end);
                if (overlapEnd <= overlapStart + 0.001) continue;

                double cursor = overlapStart;
                foreach (var cut in normalizedCuts)
                {
                    if (cut.EndSec <= cursor + 0.0005) continue;
                    if (cut.StartSec >= overlapEnd - 0.0005) break;

                    double holeStart = Math.Max(cursor, cut.StartSec);
                    double holeEnd = Math.Min(overlapEnd, cut.EndSec);
                    if (holeEnd <= holeStart + 0.0005) continue;

                    if (holeStart > cursor + 0.001)
                        chunks.Add(new Chunk(cursor, holeStart, sc.speed, 0, null, sc.fromSeg));

                    chunks.Add(new Chunk(holeStart, holeEnd, sc.speed, 0, null, sc.fromSeg, IsCut: true));
                    cursor = holeEnd;
                }

                if (overlapEnd > cursor + 0.001)
                    chunks.Add(new Chunk(cursor, overlapEnd, sc.speed, 0, null, sc.fromSeg));
            }
        }

        foreach (var freeze in normalizedSegments.Where(s => Math.Abs(s.speed) < 0.001).OrderBy(f => f.start))
        {
            double fStart = Math.Max(sourceCursor, freeze.start);
            double fEnd = Math.Max(fStart, freeze.end);
            if (fEnd <= sourceCursor + 0.001) continue;

            // CUT_01 — a freeze whose held frame was deleted has no frame to hold. Dropping it is
            // the only coherent answer: keeping it would hold a frame the user removed, and
            // snapping it elsewhere would silently move an effect they placed deliberately.
            if (InsideCut(fStart)) continue;

            if (fStart > sourceCursor + 0.001)
                AppendSourceRange(sourceCursor, fStart);

            double fDur = Math.Max(0.001, fEnd - fStart);
            chunks.Add(new Chunk(fStart, fStart + 0.001, 0, fDur));
            sourceCursor = fStart;
        }
        if (totalDurationSec > sourceCursor + 0.001)
            AppendSourceRange(sourceCursor, totalDurationSec);

        if (insertions != null && insertions.Count > 0)
        {
            var ordered = new List<Insertion>(insertions);
            ordered.Sort((a, b) => a.AtSourceSec.CompareTo(b.AtSourceSec));

            foreach (var ins in ordered)
            {
                if (ins.DurationSec <= 0.001) continue;
                double at = Math.Max(0, Math.Min(ins.AtSourceSec, totalDurationSec));

                // CUT_01 — unlike a freeze, a meme is FOREIGN footage: it does not depend on the
                // frame underneath it, so a cut cannot invalidate it. Slide it to the join instead
                // of dropping it, and the user keeps their meme at the nearest surviving moment.
                at = PushPastCut(at);

                for (int i = 0; i < chunks.Count; i++)
                {
                    var c = chunks[i];
                    if (c.HoldsSource) continue;
                    if (at > c.SourceStartSec + 0.0005 && at < c.SourceEndSec - 0.0005)
                    {
                        chunks[i] = new Chunk(c.SourceStartSec, at, c.Speed, 0, null, c.FromSegment);
                        chunks.Insert(i + 1, new Chunk(at, c.SourceEndSec, c.Speed, 0, null, c.FromSegment));
                        break;
                    }
                }

                int idx = chunks.Count;
                for (int i = 0; i < chunks.Count; i++)
                {
                    if (chunks[i].SourceStartSec >= at - 0.0005 && !chunks[i].HoldsSource) { idx = i; break; }
                }

                chunks.Insert(Math.Clamp(idx, 0, chunks.Count),
                    new Chunk(at, at + 0.001, 0, ins.DurationSec, ins.Id));
            }
        }

        return new OutputTimeline(chunks, totalDurationSec, timelineOriginSec, normalizedCuts);
    }

    /// <summary>
    /// CUT_01 — clamp, order, merge and de-sliver a raw cut list.
    ///
    /// Merging is not cosmetic. Two cuts separated by a 0.1s sliver would leave that sliver as its
    /// own chunk, which costs a whole parallel branch in the export graph and shows up as a
    /// one-frame flash nobody wanted. Overlapping cuts must merge for a harder reason: the chunk
    /// walk in <see cref="AppendSourceRangeDoc"/> assumes holes never overlap, and two overlapping
    /// holes would emit chunks out of source order and corrupt every mapping built on them.
    ///
    /// Public and static so the UI can run the SAME normalisation while the user is still dragging,
    /// and show them exactly the cuts the export will make.
    /// </summary>
    public static List<Cut> NormalizeCuts(IReadOnlyList<Cut>? cuts, double totalDurationSec)
    {
        var result = new List<Cut>();
        if (cuts == null || cuts.Count == 0) return result;

        var ordered = new List<Cut>();
        foreach (var c in cuts)
        {
            double start = Math.Max(0, Math.Min(c.StartSec, totalDurationSec));
            double end = Math.Max(0, Math.Min(c.EndSec, totalDurationSec));
            if (end < start) (start, end) = (end, start);
            if (end - start < MinCutLengthSec) continue;
            ordered.Add(new Cut(start, end));
        }
        if (ordered.Count == 0) return result;

        ordered.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));

        var current = ordered[0];
        for (int i = 1; i < ordered.Count; i++)
        {
            var next = ordered[i];
            if (next.StartSec <= current.EndSec + CutMergeGapSec)
            {
                if (next.EndSec > current.EndSec) current = new Cut(current.StartSec, next.EndSec);
            }
            else
            {
                result.Add(current);
                current = next;
            }
        }
        result.Add(current);

        return result;
    }

    /// <summary>Doc anchor only — see the chunk-splitting loop inside <see cref="Create"/>.</summary>
    private static void AppendSourceRangeDoc() { }

    /// <summary>Clamps an ABSOLUTE source position into clip-relative seconds.</summary>
    public double ToClipRelative(double absSourceSec)
    {
        double rel = absSourceSec - _originSec;
        return Math.Max(0, Math.Min(rel, _totalSourceSec));
    }

    /// <summary>
    /// ABSOLUTE source seconds -> FINISHED-VIDEO seconds.
    /// Byte-for-byte the behaviour of the old <c>CreateTimeMapper</c> lambda.
    /// </summary>
    public double SourceToOutput(double absSourceSec)
    {
        double target = ToClipRelative(absSourceSec);
        double mapped = 0;

        foreach (var ch in _chunks)
        {
            // CUT_01 — a cut adds ZERO output time. If the requested moment is before the cut we
            // are done; if it is at, inside, or past it, the cut contributes nothing and we carry
            // on. A moment INSIDE a cut therefore maps to the join — the instant of finished video
            // where the footage resumes — which is the only sensible answer for a deleted frame,
            // and is what makes voice-overs and memes land correctly across a cut for free.
            if (ch.IsCut)
            {
                if (target <= ch.SourceStartSec) break;
                continue;
            }

            if (ch.HoldsSource)
            {
                if (target >= ch.SourceStartSec) mapped += ch.FreezeHoldSec;
                continue;
            }

            if (target <= ch.SourceStartSec) break;
            if (target >= ch.SourceEndSec) mapped += (ch.SourceEndSec - ch.SourceStartSec) / ch.Speed;
            else
            {
                mapped += (target - ch.SourceStartSec) / ch.Speed;
                break;
            }
        }

        return Math.Max(0, mapped);
    }

    /// <summary>
    /// FINISHED-VIDEO seconds -> CLIP-RELATIVE source seconds. The exact inverse of
    /// <see cref="SourceToOutput"/> outside freezes.
    ///
    /// <para>
    /// DELIBERATELY LOSSY INSIDE A FREEZE, AND THAT IS CORRECT. While a frame is held, every moment
    /// of finished video maps to the SAME source instant, so this returns that instant. Callers that
    /// need to know whether the playhead is sitting inside a held frame must ask
    /// <see cref="IsHoldingFrameAt"/> rather than trying to detect it from the returned value.
    /// </para>
    ///
    /// <para>
    /// CUT_01 — AMBIGUOUS AT A JOIN, BY THE SAME LOGIC. One instant of finished video sits at both
    /// the last frame before a cut and the first frame after it; two source positions map to it and
    /// the inverse has to pick one. It picks the EARLIER side, matching the freeze convention above.
    /// That is wrong for a player, which wants to seek and keep rolling forward, so the preview must
    /// compose the two calls:
    /// <code>NextSurvivingSource(OutputToSourceRelative(t))</code>
    /// which resolves a join to the resuming side and can never return deleted footage. Do not
    /// "fix" the boundary here instead — the freeze and normal-chunk cases share this loop.
    /// </para>
    /// </summary>
    public double OutputToSourceRelative(double outputSec)
    {
        double target = Math.Max(0, outputSec);
        double acc = 0;

        foreach (var ch in _chunks)
        {
            // CUT_01 — a cut occupies no output time, so no output position can land in it. Without
            // this skip, a query landing exactly on the join (target == acc) would match the cut's
            // zero-length window and return the deleted footage's start instead of the frame that
            // actually plays there.
            if (ch.IsCut) continue;

            double outLen = ch.OutputLengthSec;
            if (target <= acc + outLen)
            {
                if (ch.HoldsSource) return Math.Clamp(ch.SourceStartSec, 0, _totalSourceSec);
                return Math.Clamp(ch.SourceStartSec + (target - acc) * ch.Speed, 0, _totalSourceSec);
            }
            acc += outLen;
        }

        return _totalSourceSec;
    }

    /// <summary>FINISHED-VIDEO seconds -> ABSOLUTE source seconds, trim offset included.</summary>
    public double OutputToSourceAbsolute(double outputSec) => _originSec + OutputToSourceRelative(outputSec);

    /// <summary>
    /// True when the finished video is holding a still frame at this moment. The preview uses this
    /// to keep its own clock running while mpv sits on one frame, instead of assuming playback
    /// stalled.
    /// </summary>
    public bool IsHoldingFrameAt(double outputSec)
    {
        double target = Math.Max(0, outputSec);
        double acc = 0;

        foreach (var ch in _chunks)
        {
            if (ch.IsCut) continue;   // CUT_01 — zero output length, never the chunk on screen.

            double outLen = ch.OutputLengthSec;
            if (target <= acc + outLen) return ch.IsFreeze;
            acc += outLen;
        }

        return false;
    }

    /// <summary>
    /// MEME_04 — the Id of the meme playing at this moment of the finished video, or null if the
    /// gameplay itself is on screen. The preview needs this because a meme is NOT in the source
    /// file: seeking mpv cannot show it, so the UI must draw a marker instead.
    /// </summary>
    public string? InsertionAt(double outputSec)
    {
        double target = Math.Max(0, outputSec);
        double acc = 0;

        foreach (var ch in _chunks)
        {
            if (ch.IsCut) continue;   // CUT_01 — zero output length, never the chunk on screen.

            double outLen = ch.OutputLengthSec;
            if (target <= acc + outLen) return ch.InsertionId;
            acc += outLen;
        }

        return null;
    }

    /// <summary>
    /// MEME_04 / D8 — moves a requested insertion point FORWARD to the end of any freeze or speed
    /// segment it lands inside, so a meme never interrupts one. The UI must draw its marker at the
    /// returned value, not at the raw click, or the meme lands somewhere the user never saw.
    /// </summary>
    public double SnapInsertionPoint(double sourceRelSec)
    {
        double at = Math.Clamp(sourceRelSec, 0, _totalSourceSec);

        // CUT_01 — clear any hole FIRST. A point inside deleted footage is not a place a meme can
        // sit, and pushing it out before the segment check means the segment snap below then works
        // on a point that actually survives.
        at = NextSurvivingSource(at);

        foreach (var ch in _chunks)
        {
            if (ch.IsInsertion || ch.IsFreeze || ch.IsCut || !ch.FromSegment) continue;
            if (at > ch.SourceStartSec + 0.0005 && at < ch.SourceEndSec - 0.0005)
                return ch.SourceEndSec;
        }

        return at;
    }

    /// <summary>
    /// MEME_04 / D7 — true when a meme already occupies this snapped point. Two memes may not share
    /// one insertion point; the caller blocks the placement and tells the user.
    /// </summary>
    public bool HasInsertionAtSource(double snappedSourceRelSec)
    {
        foreach (var ch in _chunks)
            if (ch.IsInsertion && Math.Abs(ch.SourceStartSec - snappedSourceRelSec) < 0.0015) return true;
        return false;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // CUT_01 — the public cut surface. Everything above answers questions about time; these
    // answer questions about ABSENCE, which is what the marker UI and the preview both need.
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The normalised cuts this timeline was built with, clip-relative and non-overlapping.</summary>
    public IReadOnlyList<Cut> Cuts => _cuts;

    /// <summary>True when this clip has any deleted footage at all. The cheap early-out.</summary>
    public bool HasCuts => _cuts.Count > 0;

    /// <summary>
    /// CUT_01 — seconds of source footage that actually survive to the finished video.
    /// <see cref="TotalSourceSeconds"/> deliberately still reports the FULL trimmed clip, because
    /// every existing caller uses it as the domain of source time; this is the separate question
    /// "how much of it is left", which is what a duration readout wants.
    /// </summary>
    public double SurvivingSourceSeconds
    {
        get
        {
            double removed = 0;
            foreach (var c in _cuts) removed += c.LengthSec;
            return Math.Max(0, _totalSourceSec - removed);
        }
    }

    /// <summary>Total seconds removed by cuts.</summary>
    public double RemovedSourceSeconds => Math.Max(0, _totalSourceSec - SurvivingSourceSeconds);

    /// <summary>
    /// True when this CLIP-RELATIVE source moment was deleted. The boundaries are deliberately
    /// asymmetric — the start belongs to the cut, the end belongs to the surviving footage — so
    /// that <see cref="NextSurvivingSource"/> is idempotent and cannot loop.
    /// </summary>
    public bool IsCutAtSource(double sourceRelSec)
    {
        foreach (var c in _cuts)
            if (sourceRelSec > c.StartSec - 0.0005 && sourceRelSec < c.EndSec - 0.0005) return true;
        return false;
    }

    /// <summary>
    /// CUT_01 — the first surviving source moment at or after <paramref name="sourceRelSec"/>.
    ///
    /// This is what the preview seeks to when playback reaches deleted footage: the player never
    /// decodes a removed frame, it jumps the hole in one seek. Cuts are non-overlapping and ordered
    /// after normalisation, so one forward pass is enough and the result can never fall inside
    /// another cut.
    /// </summary>
    public double NextSurvivingSource(double sourceRelSec)
    {
        double at = Math.Clamp(sourceRelSec, 0, _totalSourceSec);
        foreach (var c in _cuts)
        {
            if (c.EndSec <= at + 0.0005) continue;
            if (at > c.StartSec - 0.0005 && at < c.EndSec - 0.0005) return Math.Min(c.EndSec, _totalSourceSec);
            if (c.StartSec > at) break;
        }
        return at;
    }

    /// <summary>
    /// CUT_01 — where each cut sits in FINISHED-VIDEO seconds, for drawing the scissors markers.
    /// Every cut collapses to a single instant on the output ruler, which is precisely why a cut
    /// must be drawn as a fixed-width marker and can never be a drag-resizable block.
    /// </summary>
    public List<double> CutMarkerOutputPositions()
    {
        var marks = new List<double>();
        foreach (var c in _cuts) marks.Add(SourceToOutput(_originSec + c.StartSec));
        return marks;
    }

    /// <summary>
    /// CUT_01 — how many extra export chunks these cuts cost, for the pre-export RAM warning.
    /// A cut lands inside a stretch of footage and splits it in two, so it adds ONE branch — half
    /// what a speed segment costs (which adds itself plus the gap after it). A cut touching the
    /// very start or end of the clip splits nothing and is free.
    /// </summary>
    public int ExtraChunkCost()
    {
        int cost = 0;
        foreach (var c in _cuts)
        {
            bool atStart = c.StartSec <= 0.001;
            bool atEnd = c.EndSec >= _totalSourceSec - 0.001;
            if (!atStart && !atEnd) cost++;
        }
        return cost;
    }
}
