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
        bool FromSegment = false)
    {
        /// <summary>A held frame from the source. Occupies output time, consumes no source time.</summary>
        public bool IsFreeze => Math.Abs(Speed) < 0.001 && InsertionId == null;

        /// <summary>
        /// MEME_04 — FOREIGN content spliced in (a meme). Like a freeze it occupies output time and
        /// consumes no source time, but unlike a freeze the picture is NOT from the source file, so
        /// the preview cannot show it by seeking mpv.
        /// </summary>
        public bool IsInsertion => InsertionId != null;

        /// <summary>Either kind of block that adds output time without advancing the source.</summary>
        public bool HoldsSource => Math.Abs(Speed) < 0.001;

        /// <summary>How many seconds of FINISHED video this chunk occupies.</summary>
        public double OutputLengthSec =>
            HoldsSource ? FreezeHoldSec : (SourceEndSec - SourceStartSec) / Speed;
    }

    /// <summary>
    /// MEME_04 — a meme spliced into the middle of the video. <paramref name="AtSourceSec"/> is
    /// CLIP-RELATIVE and must already be snapped (see <see cref="SnapInsertionPoint"/>).
    /// </summary>
    public readonly record struct Insertion(double AtSourceSec, double DurationSec, string Id);

    private readonly List<Chunk> _chunks;
    private readonly double _totalSourceSec;
    private readonly double _originSec;

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

    private OutputTimeline(List<Chunk> chunks, double totalSourceSec, double originSec)
    {
        _chunks = chunks;
        _totalSourceSec = totalSourceSec;
        _originSec = originSec;

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
        IReadOnlyList<Insertion>? insertions = null)
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

        var chunks = new List<Chunk>();
        double sourceCursor = 0;

        void AppendSourceRange(double rangeStart, double rangeEnd)
        {
            foreach (var sc in sourceChunks)
            {
                double overlapStart = Math.Max(rangeStart, sc.start);
                double overlapEnd = Math.Min(rangeEnd, sc.end);
                if (overlapEnd > overlapStart + 0.001)
                    chunks.Add(new Chunk(overlapStart, overlapEnd, sc.speed, 0, null, sc.fromSeg));
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

        return new OutputTimeline(chunks, totalDurationSec, timelineOriginSec);
    }

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
    /// </summary>
    public double OutputToSourceRelative(double outputSec)
    {
        double target = Math.Max(0, outputSec);
        double acc = 0;

        foreach (var ch in _chunks)
        {
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

        foreach (var ch in _chunks)
        {
            if (ch.IsInsertion || ch.IsFreeze || !ch.FromSegment) continue;
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
}
