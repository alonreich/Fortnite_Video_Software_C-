// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using FortniteVideoSoftware.Core.Media;   // Frac, CoordinateMath

namespace FortniteVideoSoftware.App.Infrastructure;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// CROPGEOM_01 — pure geometry for the crop editor: edge snapping, rectangle normalisation and
/// the aspect-locked diagonal drag delta.
///
/// Extracted from <c>CropToolWindow</c>. None of them reads window state.
///
/// ⚠️ SNAP DISTANCES AND THE ASPECT FORMULA ARE CALIBRATED FEEL, NOT ARBITRARY. <c>SnapAxis</c>
/// decides when a dragged edge "grabs"; changing its threshold changes how the tool feels under
/// the hand, which is a product decision. <c>DiagonalWidthDelta</c> resolves a two-axis pointer
/// delta into the single width change an aspect-locked resize allows — get it wrong and a corner
/// drag jitters. Bodies moved verbatim.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
internal static class CropGeometry
{
    /// <summary>
    /// CROPGEOM_01 — how close, in UI pixels, a dragged edge must come before it snaps.
    /// Moved with <see cref="SnapAxis"/>, its only consumer. This is calibrated feel: raising it
    /// makes edges grab from further away and makes fine positioning harder; lowering it makes
    /// snapping feel unreliable. Do not treat it as an arbitrary default.
    /// </summary>
    private const double SnapThreshold = 8;

    internal static (double pos, double? guide) SnapAxis(double pos, double size, List<(double value, string label)> targets)
    {
        double start = pos;
        double halfSize = CoordinateMath.ScaleRound(Frac.FromDouble(size / 2.0));
        double center = pos + halfSize;
        double end = pos + size;
        double bestDistance = SnapThreshold + 1;
        double bestPos = pos;
        double? bestGuide = null;

        foreach ((double target, _) in targets)
        {
            Check(start, target, target);
            Check(center, target, target - halfSize);
            Check(end, target, target - size);
        }

        return (bestPos, bestGuide);

        void Check(double current, double guide, double candidatePos)
        {
            double distance = Math.Abs(current - guide);
            if (distance < bestDistance && distance <= SnapThreshold)
            {
                bestDistance = distance;
                bestPos = candidatePos;
                bestGuide = guide;
            }
        }
    }

    internal static Rect NormalizeRect(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X);
        double y = Math.Min(a.Y, b.Y);
        double w = Math.Abs(a.X - b.X);
        double h = Math.Abs(a.Y - b.Y);
        return new Rect(x, y, w, h);
    }

    /// <summary>
    /// RESIZEFEEL_01 — how far a corner drag has moved ALONG the box's own diagonal.
    ///
    /// The old code passed only <c>dx</c> and ignored <c>dy</c> entirely, so dragging a corner
    /// straight down did nothing at all and dragging it diagonally moved the box by only the
    /// horizontal part of the gesture. That is most of the "unresponsive, unstable" feel: the box
    /// does not follow the pointer, it follows the pointer's shadow on the X axis.
    ///
    /// Projecting onto the diagonal is the right answer for a ratio-locked box, because the corner
    /// can only ever travel along that line. The pointer is then free to wander off it — the user
    /// drags roughly outwards and the box grows smoothly outwards — which is exactly how every
    /// other editor behaves.
    /// </summary>
    /// <param name="dx">Pointer delta on X since the gesture started.</param>
    /// <param name="dy">Pointer delta on Y since the gesture started.</param>
    /// <param name="aspect">height ÷ width of the locked ratio.</param>
    /// <returns>The change in WIDTH the drag represents.</returns>
    internal static double DiagonalWidthDelta(double dx, double dy, double aspect)
    {
        // Unit vector along the box diagonal, expressed per unit of width: (1, aspect).
        double len = Math.Sqrt(1.0 + aspect * aspect);
        if (len < 1e-6) return dx;

        // Dot the drag onto that direction, then convert back from diagonal distance into width.
        double alongDiagonal = (dx * 1.0 + dy * aspect) / len;
        return alongDiagonal / len;
    }
}
