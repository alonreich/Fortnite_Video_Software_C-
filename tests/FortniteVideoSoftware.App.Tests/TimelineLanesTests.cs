using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FortniteVideoSoftware.App.Controls;
using FortniteVideoSoftware.Core.Media;
using Xunit;

namespace FortniteVideoSoftware.App.Tests;

public sealed class TimelineLanesTests
{
    [AvaloniaFact]
    public void TimelineFilmstrip_Measure_ReturnsExpectedDimensions()
    {
        var filmstrip = new TimelineFilmstrip
        {
            Width = 800,
            Height = 60
        };

        filmstrip.Measure(new Size(1000, 100));

        Assert.Equal(800, filmstrip.DesiredSize.Width);
        Assert.Equal(60, filmstrip.DesiredSize.Height);
    }

    [AvaloniaFact]
    public void TimelineFilmstrip_Measure_FallsBackTo60HeightWhenUnset()
    {
        var filmstrip = new TimelineFilmstrip();

        filmstrip.Measure(new Size(500, double.PositiveInfinity));

        Assert.Equal(500, filmstrip.DesiredSize.Width);
        Assert.Equal(60, filmstrip.DesiredSize.Height);
    }

    [Fact]
    public void TimelineFilmstrip_SourceRect_ComputesProportionalSlice()
    {
        var chunk = new OutputTimeline.Chunk(
            SourceStartSec: 10.0,
            SourceEndSec: 20.0,
            Speed: 1.0,
            FreezeHoldSec: 0.0
        );

        var pixels = new PixelSize(1920, 1080);
        double sourceDuration = 100.0;

        Rect rect = TimelineFilmstrip.SourceRect(chunk, pixels, sourceDuration);

        // 10s / 100s * 1920 = 192
        Assert.Equal(192, rect.X);
        Assert.Equal(0, rect.Y);
        // (20s - 10s) / 100s * 1920 = 192
        Assert.Equal(192, rect.Width);
        Assert.Equal(1080, rect.Height);
    }

    [Fact]
    public void TimelineFilmstrip_SourceRect_FreezeChunkClampsTo16By9AspectRatio()
    {
        var chunk = new OutputTimeline.Chunk(
            SourceStartSec: 5.0,
            SourceEndSec: 5.0,
            Speed: 0.0,
            FreezeHoldSec: 2.0
        );

        var pixels = new PixelSize(1920, 1080);
        double sourceDuration = 60.0;

        Rect rect = TimelineFilmstrip.SourceRect(chunk, pixels, sourceDuration);

        Assert.True(rect.Width > 0);
        Assert.Equal(1080, rect.Height);
        // x is 5/60 * 1920 = 160; Width is clamped to pixels.Width - x = 1920 - 160 = 1760
        Assert.Equal(1760, rect.Width);
    }

    [AvaloniaFact]
    public void TimelineLanesControl_LanesMaintain60PxMinHeight()
    {
        var control = new TimelineLanesControl();
        var window = new Window
        {
            Width = 1200,
            Height = 400,
            Content = control
        };
        window.Show();

        var laneA = control.FindControl<Border>("LaneABorder");
        var laneB = control.FindControl<Border>("LaneBBorder");

        Assert.NotNull(laneA);
        Assert.NotNull(laneB);

        Assert.Equal(60, laneA.Height);
        Assert.Equal(60, laneA.MinHeight);
        Assert.Equal(60, laneB.Height);
        Assert.Equal(60, laneB.MinHeight);

        window.Close();
    }
}
