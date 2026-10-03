// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>MUSICSYNC_01 — the preview's output clock is the export's output clock.</summary>
public sealed class PreviewTimeMappingTests
{
    private static TimelineViewModel Timeline()
    {
        var t = new TimelineViewModel
        {
            LoadedVideoDurationMs = 120_000,
            TrimStartMs = 10_000,
            IsTrimStartSet = true,
            TrimEndMs = 100_000,
            IsTrimEndSet = true,
            BaseSpeed = 1.1,
        };
        t.Cuts.Add(new CutRange(30_000, 50_000));   // 20 s removed before the playhead
        return t;
    }

    [Fact]
    public void PreviewMappingEqualsTheExportMapping()
    {
        var t = Timeline();
        foreach (double sourceMs in new[] { 10_000.0, 25_000.0, 55_000.0, 80_000.0, 99_000.0 })
        {
            double preview = t.PreviewSourceToOutputSeconds(sourceMs);
            double export = t.SourceMsToOutputSeconds(sourceMs);
            Assert.Equal(export, preview, 6);
        }
    }

    [Fact]
    public void AtBaseSpeedWithACutTheOutputClockIsNotTheSourceClock()
    {
        // The old music preview used (source - trimStart) directly: 70 s here. The export places
        // music at (70 - 20 cut) / 1.1 ≈ 45.45 s. That gap is the bug.
        var t = Timeline();
        Assert.Equal(50.0 / 1.1, t.PreviewSourceToOutputSeconds(80_000), 3);
    }

    [Fact]
    public void PreviewMappingNeverStampsTrimMarks()
    {
        var t = new TimelineViewModel { LoadedVideoDurationMs = 60_000, BaseSpeed = 1.0 };
        _ = t.PreviewSourceToOutputSeconds(5_000);
        Assert.False(t.IsTrimStartSet);
        Assert.False(t.IsTrimEndSet);
    }
}
