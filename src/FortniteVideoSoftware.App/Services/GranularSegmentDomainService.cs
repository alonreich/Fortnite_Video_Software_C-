using System;
using System.Collections.Generic;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App.Services;

public class GranularSegmentDomainService
{
    public const double SegGapMs = 50.0;
    public const double SegMinWidthMs = 100.0;

    public double ProjectTimeMsToCanvasX(double timeMs, double totalDurationMs, double canvasWidth)
    {
        if (totalDurationMs <= 0 || canvasWidth <= 0) return 0;
        return (timeMs / totalDurationMs) * canvasWidth;
    }

    public double ProjectCanvasXToTimeMs(double x, double totalDurationMs, double canvasWidth)
    {
        if (totalDurationMs <= 0 || canvasWidth <= 0) return 0;
        return (x / canvasWidth) * totalDurationMs;
    }

    public double ApplySnap(double timeMs, List<double> snapPoints, double snapThresholdMs)
    {
        double nearest = timeMs;
        double minDiff = snapThresholdMs;

        foreach (var point in snapPoints)
        {
            double diff = Math.Abs(timeMs - point);
            if (diff < minDiff)
            {
                minDiff = diff;
                nearest = point;
            }
        }
        return nearest;
    }

    public bool TryUpdateSegmentEdge(
        List<SpeedSegment> segments, 
        int index, 
        bool isStart, 
        double newTimeMs, 
        double totalDurationMs)
    {
        if (index < 0 || index >= segments.Count) return false;

        var seg = segments[index];
        double minLimit = 0;
        double maxLimit = totalDurationMs;

        if (isStart)
        {
            if (index > 0) minLimit = segments[index - 1].EndMs + SegGapMs;
            maxLimit = seg.EndMs - SegMinWidthMs;
            
            double clamped = Math.Clamp(newTimeMs, minLimit, maxLimit);
            segments[index] = seg with { StartMs = clamped };
        }
        else
        {
            minLimit = seg.StartMs + SegMinWidthMs;
            if (index < segments.Count - 1) maxLimit = segments[index + 1].StartMs - SegGapMs;
            
            double clamped = Math.Clamp(newTimeMs, minLimit, maxLimit);
            segments[index] = seg with { EndMs = clamped };
        }

        return true;
    }
}
