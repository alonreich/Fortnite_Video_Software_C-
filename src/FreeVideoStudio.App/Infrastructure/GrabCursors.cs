// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>
/// GRABCURSOR_01 — the open hand ("you can pick this up") and the closed hand ("you are holding it")
/// cursors of a draggable item (Video-Merger-Migration.md D23). Windows has no stock grab cursors,
/// so both are drawn once from vector shapes (white hand, dark outline, 24×24, hotspot in the palm)
/// and cached. If drawing fails for any reason the stock Hand / SizeAll cursors are used instead —
/// a missing custom cursor must never leave the pointer invisible.
/// </summary>
internal static class GrabCursors
{
    private static Cursor? _open;
    private static Cursor? _closed;

    public static Cursor Open => _open ??= Build(closed: false) ?? new Cursor(StandardCursorType.Hand);
    public static Cursor Closed => _closed ??= Build(closed: true) ?? new Cursor(StandardCursorType.SizeAll);

    private static Cursor? Build(bool closed)
    {
        try
        {
            const int size = 24;
            var bmp = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
            using (var ctx = bmp.CreateDrawingContext())
            {
                Geometry hand = closed ? Fist() : OpenHand();
                ctx.DrawGeometry(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(20, 20, 20)), 1.3), hand);
            }
            return new Cursor(bmp, new PixelPoint(12, 12));
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return null;
        }
    }

    private static Geometry Union(params Geometry[] parts)
    {
        Geometry g = parts[0];
        for (int i = 1; i < parts.Length; i++) g = new CombinedGeometry(GeometryCombineMode.Union, g, parts[i]);
        return g;
    }

    private static Geometry R(double x, double y, double w, double h, double r) => new RectangleGeometry(new Rect(x, y, w, h), r, r);

    /// <summary>Palm, four raised fingers and the thumb out to the side.</summary>
    private static Geometry OpenHand() => Union(
        R(6.5, 10.5, 12.5, 11, 3.5),     // palm
        R(6.5, 4.0, 3.0, 10, 1.5),       // index
        R(9.8, 2.5, 3.0, 11, 1.5),       // middle
        R(13.1, 3.0, 3.0, 10.5, 1.5),    // ring
        R(16.2, 5.0, 2.8, 9, 1.4),       // little
        R(2.8, 10.0, 5.5, 3.2, 1.6));    // thumb

    /// <summary>A closed fist: palm with the knuckles on top and the thumb folded across.</summary>
    private static Geometry Fist() => Union(
        R(5.5, 9.0, 13.5, 11.5, 3.5),    // fist body
        R(6.0, 7.0, 3.2, 4.5, 1.6),      // knuckles
        R(9.3, 6.5, 3.2, 4.5, 1.6),
        R(12.6, 6.8, 3.2, 4.5, 1.6),
        R(15.7, 7.6, 3.0, 4.0, 1.5),
        R(4.0, 12.5, 8.5, 3.2, 1.6));    // thumb across the front
}
