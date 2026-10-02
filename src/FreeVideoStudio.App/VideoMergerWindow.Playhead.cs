using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace FreeVideoStudio.App;

/// <summary>
/// MERGERPLAYHEAD_01 (user 2026-09-28) — the Merger's playhead is a red LINE from the top of the time scale down
/// through the scrub row, the thumbnail blocks and the waveform, instead of the slider's small resting dot.
///
/// <para>
/// It is purely a picture: one non-hit-testable layer spanning every row of the timeline grid, so it never steals a
/// press from the thumbnail blocks (which select and reorder clips). Seeking and dragging it happen where they always
/// did — the overlay's press/move handlers, on the scale + scrub row and (now) on the waveform lane
/// (<see cref="IsOnSeekRows"/>). The line follows the scrub slider's value, which both the playback tick and a
/// pointer scrub set, so it moves the instant the user presses, not a tick later.
/// </para>
///
/// <para>
/// The x position uses the SAME mapping as the seek (x / width of the timeline column × duration), so the line sits
/// exactly where a click would seek and exactly over the matching thumbnail and waveform pixel. The slider's own dot
/// was 22 px inset at each end (its rail), so it was never exactly aligned with the lanes below it.
/// </para>
/// </summary>
public partial class VideoMergerWindow
{
    /// <summary>Stroke of the yellow selection ants (upper timeline and thumbnail blocks). Was 2.</summary>
    private const double AntsThickness = 1.25;

    private const double PlayheadWidth = 2;

    private Canvas? _playheadLayer;
    private Rectangle? _playheadLine;
    private Avalonia.Controls.Shapes.Path? _playheadCap;

    /// <summary>Adds the playhead layer over every row of the timeline grid and keeps it on the slider's value.</summary>
    private void AttachMergerPlayhead(Canvas? seekRow, Slider? slider)
    {
        if (seekRow?.Parent is not Grid grid || slider == null || _playheadLayer != null) return;

        IBrush red = Infrastructure.ThemeResources.Brush(grid, "AppPlayheadBrush", Brushes.Red);
        _playheadLayer = new Canvas { IsHitTestVisible = false, ClipToBounds = false, ZIndex = 60 };
        Grid.SetRow(_playheadLayer, 0);
        Grid.SetRowSpan(_playheadLayer, Math.Max(1, grid.RowDefinitions.Count));

        _playheadLine = new Rectangle { Width = PlayheadWidth, Fill = red, IsHitTestVisible = false };
        _playheadCap = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M -5,0 L 5,0 L 0,6 Z"),
            Fill = red,
            IsHitTestVisible = false,
        };
        _playheadLayer.Children.Add(_playheadLine);
        _playheadLayer.Children.Add(_playheadCap);
        grid.Children.Add(_playheadLayer);

        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) PositionPlayhead(slider);
        };
        _playheadLayer.SizeChanged += (_, _) => PositionPlayhead(slider);
        PositionPlayhead(slider);
    }

    private void PositionPlayhead(Slider slider)
    {
        if (_playheadLayer == null || _playheadLine == null || _playheadCap == null) return;
        double w = _playheadLayer.Bounds.Width;
        double h = _playheadLayer.Bounds.Height;
        if (w <= 0 || h <= 0) return;

        double range = slider.Maximum - slider.Minimum;
        double f = range > 0 ? Math.Clamp((slider.Value - slider.Minimum) / range, 0, 1) : 0;
        double x = f * w;

        _playheadLine.Height = h;
        Canvas.SetLeft(_playheadLine, x - PlayheadWidth / 2);
        Canvas.SetTop(_playheadLine, 0);
        Canvas.SetLeft(_playheadCap, x);
        Canvas.SetTop(_playheadCap, 0);
    }
}
