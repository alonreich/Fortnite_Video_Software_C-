// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using Avalonia.Controls;
using Avalonia.Media;

namespace FortniteVideoSoftware.App.Infrastructure;

/// <summary>
/// MWDRAW_01 — the two canvas primitives the music wizard draws its timeline furniture with: the
/// playhead line and the lane boundaries. Pure "given a canvas and a coordinate, put a shape
/// there" helpers with no wizard state. Extracted from <c>MusicWizardWindow</c>; bodies verbatim.
/// </summary>
internal static class MusicWizardDraw
{
    internal static void EnsurePlayheadLine(
        Canvas canvas,
        ref Avalonia.Controls.Shapes.Line? line,
        Avalonia.Media.IBrush stroke,
        bool dashed)
    {
        if (line == null)
        {
            line = new Avalonia.Controls.Shapes.Line
            {
                Stroke = stroke,
                StrokeThickness = 2,
                IsHitTestVisible = false
            };

            if (dashed)
            {
                line.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(new[] { 2.0, 2.0 });
            }

            canvas.Children.Add(line);
        }
        else if (!canvas.Children.Contains(line))
        {
            canvas.Children.Add(line);
        }
    }

    internal static void AddLaneBoundary(Canvas canvas, double xPos, double height, Avalonia.Media.IBrush brush, double opacity)
    {
        var border = new Avalonia.Controls.Border
        {
            Width = 2,
            Height = Math.Max(1, height),
            Background = brush,
            Opacity = opacity,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(border, Math.Max(0, xPos - 1));
        Canvas.SetTop(border, 0);
        canvas.Children.Add(border);
    }
}
