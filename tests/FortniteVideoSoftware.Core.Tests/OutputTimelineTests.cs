using System;
using System.Collections.Generic;
using FortniteVideoSoftware.Core.Media;
using Xunit;

namespace FortniteVideoSoftware.Core.Tests;

public class OutputTimelineTests
{
    // =========================================================================
    // 1. Cut Normalization Tests
    // =========================================================================

    [Fact]
    public void NormalizeCuts_EmptyOrNull_ReturnsEmptyList()
    {
        Assert.Empty(OutputTimeline.NormalizeCuts(null, 60.0));
        Assert.Empty(OutputTimeline.NormalizeCuts(new List<OutputTimeline.Cut>(), 60.0));
    }

    [Fact]
    public void NormalizeCuts_DiscardsSubFrameDrags()
    {
        // MinCutLengthSec is 0.04s. A 0.02s cut must be dropped.
        var raw = new[]
        {
            new OutputTimeline.Cut(5.0, 5.02), // 0.02s -> drop
            new OutputTimeline.Cut(10.0, 10.05) // 0.05s -> keep
        };

        var normalized = OutputTimeline.NormalizeCuts(raw, 60.0);
        Assert.Single(normalized);
        Assert.Equal(10.0, normalized[0].StartSec, 3);
        Assert.Equal(10.05, normalized[0].EndSec, 3);
    }

    [Fact]
    public void NormalizeCuts_InvertedAndOutOfBounds_ClampsAndSwaps()
    {
        var raw = new[]
        {
            new OutputTimeline.Cut(15.0, 10.0), // Inverted: 15 -> 10
            new OutputTimeline.Cut(-5.0, 2.0),  // Negative start
            new OutputTimeline.Cut(55.0, 75.0)  // Beyond total duration (60s)
        };

        var normalized = OutputTimeline.NormalizeCuts(raw, 60.0);
        Assert.Equal(3, normalized.Count);

        Assert.Equal(0.0, normalized[0].StartSec, 3);
        Assert.Equal(2.0, normalized[0].EndSec, 3);

        Assert.Equal(10.0, normalized[1].StartSec, 3);
        Assert.Equal(15.0, normalized[1].EndSec, 3);

        Assert.Equal(55.0, normalized[2].StartSec, 3);
        Assert.Equal(60.0, normalized[2].EndSec, 3);
    }

    [Fact]
    public void NormalizeCuts_OverlappingAndNearTouchingCuts_MergesCorrectly()
    {
        // CutMergeGapSec is 0.30s.
        var raw = new[]
        {
            new OutputTimeline.Cut(2.0, 5.0),
            new OutputTimeline.Cut(4.0, 7.0),   // Overlaps [2, 5] -> merged to [2, 7]
            new OutputTimeline.Cut(7.2, 9.0),   // Gap is 0.2s (<= 0.3s) -> merged to [2, 9]
            new OutputTimeline.Cut(11.0, 13.0)  // Gap is 2.0s (> 0.3s) -> separate cut
        };

        var normalized = OutputTimeline.NormalizeCuts(raw, 60.0);
        Assert.Equal(2, normalized.Count);

        Assert.Equal(2.0, normalized[0].StartSec, 3);
        Assert.Equal(9.0, normalized[0].EndSec, 3);

        Assert.Equal(11.0, normalized[1].StartSec, 3);
        Assert.Equal(13.0, normalized[1].EndSec, 3);
    }

    // =========================================================================
    // 2. Normal Timeline & Speed Segments
    // =========================================================================

    [Fact]
    public void OutputTimeline_UniformSpeed_MapsBidirectionally()
    {
        double totalSec = 60.0;
        var timeline = OutputTimeline.Create(totalSec * 1000.0, segments: null, baseSpeed: 1.0);

        Assert.Equal(60.0, timeline.TotalSourceSeconds, 3);
        Assert.Equal(60.0, timeline.TotalOutputSeconds, 3);
        Assert.Equal(60.0, timeline.SurvivingSourceSeconds, 3);
        Assert.Equal(0.0, timeline.RemovedSourceSeconds, 3);
        Assert.False(timeline.HasCuts);

        for (double t = 0.0; t <= totalSec; t += 5.0)
        {
            double outSec = timeline.SourceToOutput(t);
            Assert.Equal(t, outSec, 3);
            double backSec = timeline.OutputToSourceRelative(outSec);
            Assert.Equal(t, backSec, 3);
        }
    }

    [Fact]
    public void OutputTimeline_BaseSpeed2X_HalvesOutputDuration()
    {
        var timeline = OutputTimeline.Create(60.0 * 1000.0, segments: null, baseSpeed: 2.0);

        Assert.Equal(60.0, timeline.TotalSourceSeconds, 3);
        Assert.Equal(30.0, timeline.TotalOutputSeconds, 3);

        Assert.Equal(0.0, timeline.SourceToOutput(0.0), 3);
        Assert.Equal(10.0, timeline.SourceToOutput(20.0), 3);
        Assert.Equal(30.0, timeline.SourceToOutput(60.0), 3);

        Assert.Equal(20.0, timeline.OutputToSourceRelative(10.0), 3);
    }

    [Fact]
    public void OutputTimeline_MultipleSpeedSegments_CalculatesExactAccrual()
    {
        // 0s - 10s: 1.0x (10s output)
        // 10s - 20s: 2.0x (5s output)
        // 20s - 30s: 0.5x (20s output)
        // Total: 30s source -> 35s output
        var segments = new List<SpeedSegment>
        {
            new SpeedSegment(10000, 20000, 2.0),
            new SpeedSegment(20000, 30000, 0.5)
        };

        var timeline = OutputTimeline.Create(30.0 * 1000.0, segments, baseSpeed: 1.0);

        Assert.Equal(30.0, timeline.TotalSourceSeconds, 3);
        Assert.Equal(35.0, timeline.TotalOutputSeconds, 3);

        Assert.Equal(5.0, timeline.SourceToOutput(5.0), 3);
        Assert.Equal(10.0, timeline.SourceToOutput(10.0), 3);
        Assert.Equal(12.5, timeline.SourceToOutput(15.0), 3); // 10 + 5/2 = 12.5
        Assert.Equal(15.0, timeline.SourceToOutput(20.0), 3); // 10 + 10/2 = 15
        Assert.Equal(25.0, timeline.SourceToOutput(25.0), 3); // 15 + 5/0.5 = 25
        Assert.Equal(35.0, timeline.SourceToOutput(30.0), 3); // 15 + 10/0.5 = 35

        // Invertible checks
        Assert.Equal(5.0, timeline.OutputToSourceRelative(5.0), 3);
        Assert.Equal(15.0, timeline.OutputToSourceRelative(12.5), 3);
        Assert.Equal(25.0, timeline.OutputToSourceRelative(25.0), 3);
    }

    // =========================================================================
    // 3. Freeze Chunk Translation
    // =========================================================================

    [Fact]
    public void OutputTimeline_FreezeSegment_ExtendsOutputAndHoldsSource()
    {
        // 60s total, freeze 5.0s to 8.0s (3.0s hold)
        var segments = new List<SpeedSegment>
        {
            new SpeedSegment(5000, 8000, 0.0) // Speed 0.0 = Freeze
        };

        var timeline = OutputTimeline.Create(60.0 * 1000.0, segments, baseSpeed: 1.0);

        // Freeze adds 3.0s to finished video without consuming source time
        Assert.Equal(60.0, timeline.TotalSourceSeconds, 3);
        Assert.Equal(63.0, timeline.TotalOutputSeconds, 3);

        // Before freeze: output matches source
        Assert.Equal(3.0, timeline.SourceToOutput(3.0), 3);
        Assert.False(timeline.IsHoldingFrameAt(3.0));

        // During freeze: finished video seconds 5.0s to 8.0s
        Assert.False(timeline.IsHoldingFrameAt(4.99));
        Assert.True(timeline.IsHoldingFrameAt(5.01));
        Assert.True(timeline.IsHoldingFrameAt(6.5));
        Assert.True(timeline.IsHoldingFrameAt(7.99));
        Assert.True(timeline.IsHoldingFrameAt(8.0));
        Assert.False(timeline.IsHoldingFrameAt(8.01));

        // In reverse: every moment in (5.0, 8.0] maps to held source instant 5.0s
        Assert.Equal(5.0, timeline.OutputToSourceRelative(5.0), 3);
        Assert.Equal(5.0, timeline.OutputToSourceRelative(6.5), 3);
        Assert.Equal(5.0, timeline.OutputToSourceRelative(8.0), 3);

        // After freeze: output is offset by +3.0s
        Assert.Equal(13.0, timeline.SourceToOutput(10.0), 3);
        Assert.Equal(10.0, timeline.OutputToSourceRelative(13.0), 3);
    }

    // =========================================================================
    // 4. Cut Chunk Translation and Join Snapping
    // =========================================================================

    [Fact]
    public void OutputTimeline_Cuts_ReducesOutputAndSnapsJoin()
    {
        // 60s total with a cut from 10s to 20s (10s cut)
        var cuts = new[] { new OutputTimeline.Cut(10.0, 20.0) };
        var timeline = OutputTimeline.Create(60.0 * 1000.0, segments: null, baseSpeed: 1.0, cuts: cuts);

        Assert.True(timeline.HasCuts);
        Assert.Equal(60.0, timeline.TotalSourceSeconds, 3);
        Assert.Equal(50.0, timeline.TotalOutputSeconds, 3);
        Assert.Equal(50.0, timeline.SurvivingSourceSeconds, 3);
        Assert.Equal(10.0, timeline.RemovedSourceSeconds, 3);

        // IsCutAtSource boundary behavior
        Assert.False(timeline.IsCutAtSource(9.9));
        Assert.True(timeline.IsCutAtSource(10.1));
        Assert.True(timeline.IsCutAtSource(15.0));
        Assert.True(timeline.IsCutAtSource(19.9));
        Assert.False(timeline.IsCutAtSource(20.1));

        // NextSurvivingSource advances past the hole
        Assert.Equal(5.0, timeline.NextSurvivingSource(5.0), 3);
        Assert.Equal(20.0, timeline.NextSurvivingSource(10.0), 3);
        Assert.Equal(20.0, timeline.NextSurvivingSource(15.0), 3);
        Assert.Equal(20.0, timeline.NextSurvivingSource(20.0), 3);
        Assert.Equal(25.0, timeline.NextSurvivingSource(25.0), 3);

        // SourceToOutput: moments inside a cut collapse to the cut join point (10.0s output)
        Assert.Equal(5.0, timeline.SourceToOutput(5.0), 3);
        Assert.Equal(10.0, timeline.SourceToOutput(10.0), 3);
        Assert.Equal(10.0, timeline.SourceToOutput(15.0), 3); // inside cut -> join
        Assert.Equal(10.0, timeline.SourceToOutput(20.0), 3); // resume -> join
        Assert.Equal(15.0, timeline.SourceToOutput(25.0), 3); // after cut -> 10 + (25 - 20) = 15

        // OutputToSourceRelative outside cuts
        Assert.Equal(5.0, timeline.OutputToSourceRelative(5.0), 3);
        Assert.Equal(22.5, timeline.OutputToSourceRelative(12.5), 3); // 20 + 2.5 = 22.5
        Assert.Equal(35.0, timeline.OutputToSourceRelative(25.0), 3);

        // Join marker position on output ruler
        var marks = timeline.CutMarkerOutputPositions();
        Assert.Single(marks);
        Assert.Equal(10.0, marks[0], 3);
    }

    [Fact]
    public void OutputTimeline_FreezeInsideCut_IsDropped()
    {
        // Freeze at 12-14s, but cut is 10-20s. The freeze must be dropped because its frame was deleted.
        var segments = new List<SpeedSegment>
        {
            new SpeedSegment(12000, 14000, 0.0) // Freeze
        };
        var cuts = new[] { new OutputTimeline.Cut(10.0, 20.0) };

        var timeline = OutputTimeline.Create(60.0 * 1000.0, segments, baseSpeed: 1.0, cuts: cuts);

        // 60s source - 10s cut = 50s output (freeze dropped)
        Assert.Equal(50.0, timeline.TotalOutputSeconds, 3);
        Assert.False(timeline.IsHoldingFrameAt(10.0));
    }

    [Fact]
    public void OutputTimeline_ExtraChunkCost_CalculatesBranchesAccurately()
    {
        // A cut inside footage costs 1 extra chunk (splits footage).
        var middleCut = new[] { new OutputTimeline.Cut(10.0, 20.0) };
        var tl1 = OutputTimeline.Create(60.0 * 1000.0, segments: null, cuts: middleCut);
        Assert.Equal(1, tl1.ExtraChunkCost());

        // A cut touching the start (0-10s) does not split and costs 0 extra chunks.
        var startCut = new[] { new OutputTimeline.Cut(0.0, 10.0) };
        var tl2 = OutputTimeline.Create(60.0 * 1000.0, segments: null, cuts: startCut);
        Assert.Equal(0, tl2.ExtraChunkCost());

        // A cut touching the end (50-60s) costs 0 extra chunks.
        var endCut = new[] { new OutputTimeline.Cut(50.0, 60.0) };
        var tl3 = OutputTimeline.Create(60.0 * 1000.0, segments: null, cuts: endCut);
        Assert.Equal(0, tl3.ExtraChunkCost());
    }

    // =========================================================================
    // 5. Meme Insertion Chunks
    // =========================================================================

    [Fact]
    public void OutputTimeline_Insertions_SlicesFinishedVideoAndReportsRanges()
    {
        var insertions = new[]
        {
            new OutputTimeline.Insertion(15.0, 4.0, "meme_intro")
        };

        var timeline = OutputTimeline.Create(60.0 * 1000.0, segments: null, baseSpeed: 1.0, insertions: insertions);

        // 60s source + 4s insertion = 64s finished video
        Assert.Equal(64.0, timeline.TotalOutputSeconds, 3);

        // Insertion presence check: occupies (15.0, 19.0]
        Assert.Null(timeline.InsertionAt(14.5));
        Assert.Null(timeline.InsertionAt(15.0)); // Boundary belongs to preceding gameplay chunk
        Assert.Equal("meme_intro", timeline.InsertionAt(15.01));
        Assert.Equal("meme_intro", timeline.InsertionAt(17.0));
        Assert.Equal("meme_intro", timeline.InsertionAt(19.0));
        Assert.Null(timeline.InsertionAt(19.01));

        // Insertion output ranges
        var ranges = timeline.InsertionOutputRanges();
        Assert.Single(ranges);
        Assert.Equal("meme_intro", ranges[0].Id);
        Assert.Equal(15.0, ranges[0].StartOutputSec, 3);
        Assert.Equal(19.0, ranges[0].EndOutputSec, 3);

        Assert.True(timeline.HasInsertionAtSource(15.0));
        Assert.False(timeline.HasInsertionAtSource(20.0));
    }

    [Fact]
    public void OutputTimeline_SnapInsertionPoint_PushesPastSegmentsAndCuts()
    {
        var segments = new List<SpeedSegment>
        {
            new SpeedSegment(10000, 20000, 2.0)
        };
        var cuts = new[] { new OutputTimeline.Cut(30.0, 40.0) };

        var timeline = OutputTimeline.Create(60.0 * 1000.0, segments, baseSpeed: 1.0, cuts: cuts);

        // Point inside speed segment (15s) snaps forward to segment end (20s)
        Assert.Equal(20.0, timeline.SnapInsertionPoint(15.0), 3);

        // Point inside cut (35s) snaps forward past cut (40s)
        Assert.Equal(40.0, timeline.SnapInsertionPoint(35.0), 3);

        // Point in plain footage stays untouched
        Assert.Equal(5.0, timeline.SnapInsertionPoint(5.0), 3);
    }

    [Fact]
    public void OutputTimeline_FreezeNearEndOfVideo_PreservesFullFreezeDuration()
    {
        // 10s video with 2.0s freeze at 9.5s
        var segments = new List<SpeedSegment>
        {
            new SpeedSegment(9500, 11500, 0.0)
        };
        var timeline = OutputTimeline.Create(10000.0, segments, baseSpeed: 1.0);
        Assert.Equal(12.0, timeline.TotalOutputSeconds, 2);
    }

    [Fact]
    public void OutputTimeline_FreezeAtLastFrame_NotDropped()
    {
        // 10s video with 1.5s freeze placed directly on last frame (10.0s)
        var segments = new List<SpeedSegment>
        {
            new SpeedSegment(10000, 11500, 0.0)
        };
        var timeline = OutputTimeline.Create(10000.0, segments, baseSpeed: 1.0);
        Assert.Equal(11.5, timeline.TotalOutputSeconds, 2);
    }

    [Fact]
    public void ZoomPreviewSimulator_WithTrimStartSec_CalculatesActiveCropCorrectly()
    {
        // Absolute segment from 32s to 35s with zoom, trim start at 30s
        var segments = new List<SpeedSegment>
        {
            new SpeedSegment(32000, 35000, 1.0, ZoomX: 100, ZoomY: 100, ZoomW: 800, ZoomH: 450, ZoomOrigRes: "1920x1080")
        };

        // Clip-relative time 2.5s corresponds to absolute 32.5s, inside the zoom
        var result = ZoomPreviewSimulator.Compute(segments, tSec: 2.5, durSec: 10.0,
            portraitMode: false, srcW: 1920, srcH: 1080, trimStartSec: 30.0);

        Assert.True(result.HasCrop);
        Assert.NotEmpty(result.Crop);
    }
}
