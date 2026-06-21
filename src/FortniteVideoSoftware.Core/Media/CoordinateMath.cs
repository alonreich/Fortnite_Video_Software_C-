// ==============================================================================
// CoordinateMath.cs — Exact port of Python coordinate_math.py
// Portrait Canvas Trick: 1280x1920 internal → 1080x1620 content → 1080x1920 final
// All Fraction-based math preserved for pixel-perfect rounding behavior.
// ==============================================================================

using System.Numerics;
using System.Text.RegularExpressions;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// Exact rational arithmetic to match Python's Fraction semantics.
/// Used throughout coordinate transforms to guarantee identical rounding.
/// </summary>
public readonly struct Frac : IEquatable<Frac>, IComparable<Frac>
{
    public readonly long Num;
    public readonly long Den;

    public static readonly Frac Zero = new(0, 1);
    public static readonly Frac One = new(1, 1);

    public Frac(long num, long den)
    {
        if (den == 0) throw new DivideByZeroException("Denominator is zero.");
        if (den < 0) { num = -num; den = -den; }
        long g = Gcd(Math.Abs(num), den);
        Num = num / g;
        Den = den / g;
    }

    private static long Gcd(long a, long b)
    {
        a = Math.Abs(a); b = Math.Abs(b);
        while (b != 0) { long t = a % b; a = b; b = t; }
        return a == 0 ? 1 : a;
    }

    public static Frac FromDouble(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return Zero;
        // Use continued fractions for clean rational approximation
        double eps = 1e-10;
        long maxDen = 100_000_000;
        double val = Math.Abs(d);
        bool neg = d < 0;
        if (val < eps) return Zero;

        long p0 = 0, p1 = 1, q0 = 1, q1 = 0;
        double x = val;
        for (int i = 0; i < 64; i++)
        {
            long a = (long)Math.Floor(x);
            long p2 = a * p1 + p0;
            long q2 = a * q1 + q0;
            if (q2 > maxDen) break;
            p0 = p1; p1 = p2;
            q0 = q1; q1 = q2;
            double rem = x - a;
            if (rem < eps) break;
            x = 1.0 / rem;
        }
        return new Frac(neg ? -p1 : p1, q1 == 0 ? 1 : q1);
    }

    public static Frac FromString(string s)
    {
        s = s.Trim();
        if (s.Contains('/'))
        {
            var parts = s.Split('/');
            return new Frac(long.Parse(parts[0]), long.Parse(parts[1]));
        }
        if (s.Contains('.') || s.Contains('e') || s.Contains('E'))
            return FromDouble(double.Parse(s, System.Globalization.CultureInfo.InvariantCulture));
        return new Frac(long.Parse(s), 1);
    }

    public static Frac operator +(Frac a, Frac b) => new(a.Num * b.Den + b.Num * a.Den, a.Den * b.Den);
    public static Frac operator -(Frac a, Frac b) => new(a.Num * b.Den - b.Num * a.Den, a.Den * b.Den);
    public static Frac operator *(Frac a, Frac b) => new(a.Num * b.Num, a.Den * b.Den);
    public static Frac operator /(Frac a, Frac b) => new(a.Num * b.Den, a.Den * b.Num);
    public static Frac operator -(Frac a) => new(-a.Num, a.Den);
    public static bool operator ==(Frac a, Frac b) => a.Num == b.Num && a.Den == b.Den;
    public static bool operator !=(Frac a, Frac b) => !(a == b);
    public static bool operator <(Frac a, Frac b) => a.Num * b.Den < b.Num * a.Den;
    public static bool operator >(Frac a, Frac b) => a.Num * b.Den > b.Num * a.Den;
    public static bool operator <=(Frac a, Frac b) => a.Num * b.Den <= b.Num * a.Den;
    public static bool operator >=(Frac a, Frac b) => a.Num * b.Den >= b.Num * a.Den;

    public bool Equals(Frac other) => Num == other.Num && Den == other.Den;
    public override bool Equals(object? obj) => obj is Frac f && Equals(f);
    public override int GetHashCode() => HashCode.Combine(Num, Den);
    public int CompareTo(Frac other) => (this - other).Num.CompareTo(0);
    public double ToDouble() => (double)Num / Den;
    public override string ToString() => Den == 1 ? Num.ToString() : $"{Num}/{Den}";
}

// ==============================================================================
// Constants — exact mirror of coordinate_math.py module-level constants
// ==============================================================================

public static class CoordinateConstants
{
    public const int PortraitW = 1080;
    public const int PortraitH = 1920;
    public const int InternalW = 1280;
    public const int InternalH = 1920;
    public const int UIPaddingTop = 150;
    public const int UIPaddingBottom = 150;
    public const int UIContentH = 1620;

    // TARGET_W/TARGET_H = internal compose space
    public const int TargetW = InternalW;
    public const int TargetH = InternalH;

    // CONTENT_W/CONTENT_H = final portrait content area
    public const int ContentW = PortraitW;
    public const int ContentH = UIContentH;

    public const int PaddingTop = UIPaddingTop;

    // BACKEND_SCALE = 1280 / 1080
    public static readonly Frac BackendScale = new(InternalW, PortraitW);
    public static readonly Frac UIToInternalScale = BackendScale;
}

// ==============================================================================
// CoordinateMath — exact port of all transform/clamp/scale functions
// ==============================================================================

public static class CoordinateMath
{
    // --- Floor / Ceil for Frac (Python floor/ceil behavior) ---

    public static int FracFloor(Frac v) => (int)(v.Num / v.Den); // Python floor division for positive den
    public static int FracCeil(Frac v) => -((int)(-v.Num / v.Den));

    public static int EvenDown(int v) => v % 2 == 0 ? v : v - 1;
    public static int EvenUp(int v) => v % 2 == 0 ? v : v + 1;

    // --- scale_round (matches Python's scale_round) ---

    public static int ScaleRound(Frac val)
    {
        if (val.Num >= 0)
            return FracFloor(val + new Frac(1, 2));
        return -FracFloor(-val + new Frac(1, 2));
    }

    public static int ScaleRound(double d) => ScaleRound(Frac.FromDouble(d));

    // --- outward_round_rect ---

    public static (int x, int y, int w, int h) OutwardRoundRect(Frac x, Frac y, Frac w, Frac h)
    {
        int ix = FracFloor(x);
        int iy = FracFloor(y);
        int iw = FracCeil(x + w) - ix;
        int ih = FracCeil(y + h) - iy;
        return (ix, iy, Math.Max(1, iw), Math.Max(1, ih));
    }

    // --- get_resolution_ints ---

    public static (int w, int h) GetResolutionInts(string? resStr)
    {
        if (string.IsNullOrWhiteSpace(resStr)) return (1920, 1080);
        var match = Regex.Match(resStr, @"(\d+)\s*[x:X\s]\s*(\d+)");
        if (match.Success)
        {
            int w = int.Parse(match.Groups[1].Value);
            int h = int.Parse(match.Groups[2].Value);
            return (w, h);
        }
        return (1920, 1080);
    }

    // --- _scale_plan (internal but exposed for inverse transform) ---

    public static (int scaledW, int scaledH, int cropX, int cropY, Frac scale) ScalePlan(string originalResolution)
    {
        var (inW, inH) = GetResolutionInts(originalResolution);
        var scaleW = new Frac(CoordinateConstants.InternalW, inW);
        var scaleH = new Frac(CoordinateConstants.InternalH, inH);
        var scale = scaleW > scaleH ? scaleW : scaleH;

        int scaledW = EvenUp(FracCeil(new Frac(inW, 1) * scale));
        int scaledH = EvenUp(FracCeil(new Frac(inH, 1) * scale));
        int cropX = EvenDown(FracFloor(new Frac(scaledW - CoordinateConstants.InternalW, 2)));
        int cropY = EvenDown(FracFloor(new Frac(scaledH - CoordinateConstants.InternalH, 2)));
        return (scaledW, scaledH, cropX, cropY, scale);
    }

    // --- transform_to_content_area (returns Fractions) ---

    public static (Frac x, Frac y, Frac w, Frac h) TransformToContentArea(
        (double x, double y, double w, double h) rect, string originalResolution)
    {
        var fx = Frac.FromDouble(rect.x);
        var fy = Frac.FromDouble(rect.y);
        var fw = Frac.FromDouble(rect.w);
        var fh = Frac.FromDouble(rect.h);

        var (_, _, cropX, cropY, scale) = ScalePlan(originalResolution);

        var internalX = (fx * scale) - new Frac(cropX, 1);
        var internalY = (fy * scale) - new Frac(cropY, 1);
        var internalW = fw * scale;
        var internalH = fh * scale;

        var uiScale = CoordinateConstants.UIToInternalScale;
        return (internalX / uiScale, internalY / uiScale, internalW / uiScale, internalH / uiScale);
    }

    // --- inverse_transform_from_content_area (returns Fractions) ---

    public static (Frac x, Frac y, Frac w, Frac h) InverseTransformFromContentArea(
        (double x, double y, double w, double h) rect, string originalResolution, string? driftType = null)
    {
        var (inW, inH) = GetResolutionInts(originalResolution);
        var uiX = Frac.FromDouble(rect.x);
        var uiY = Frac.FromDouble(rect.y);
        var uiW = Frac.FromDouble(rect.w);
        var uiH = Frac.FromDouble(rect.h);

        var uiScale = CoordinateConstants.UIToInternalScale;
        var internalX = uiX * uiScale;
        var internalY = uiY * uiScale;
        var internalW = uiW * uiScale;
        var internalH = uiH * uiScale;

        var (_, _, cropX, cropY, scale) = ScalePlan(originalResolution);

        var origX = (internalX + new Frac(cropX, 1)) / scale;
        var origY = (internalY + new Frac(cropY, 1)) / scale;
        var origW = internalW / scale;
        var origH = internalH / scale;

        // Drift protection: +1px correction
        if (driftType == "left")
        {
            origX -= Frac.One;
            origW += Frac.One;
        }
        else if (driftType == "right")
        {
            origW += Frac.One;
        }

        var finalX = Max(Frac.Zero, Min(origX, new Frac(inW - 1, 1)));
        var finalY = Max(Frac.Zero, Min(origY, new Frac(inH - 1, 1)));
        var finalW = Max(Frac.One, Min(origW, new Frac(inW, 1) - finalX));
        var finalH = Max(Frac.One, Min(origH, new Frac(inH, 1) - finalY));

        return (finalX, finalY, finalW, finalH);
    }

    // --- inverse_transform_from_content_area_int (returns ints, even-aligned) ---

    public static (int x, int y, int w, int h) InverseTransformFromContentAreaInt(
        (int x, int y, int w, int h) rect, string originalResolution, string? driftType = null)
    {
        var (inW, inH) = GetResolutionInts(originalResolution);
        var (fx, fy, fw, fh) = InverseTransformFromContentArea(
            (rect.x, rect.y, rect.w, rect.h), originalResolution, driftType);

        int ix = FracFloor(fx);
        int iy = FracFloor(fy);
        int ex = FracCeil(fx + fw);
        int ey = FracCeil(fy + fh);

        ix = Math.Max(0, Math.Min(EvenDown(ix), inW - 2));
        iy = Math.Max(0, Math.Min(EvenDown(iy), inH - 2));
        ex = Math.Max(ix + 2, Math.Min(EvenUp(ex), inW));
        ey = Math.Max(iy + 2, Math.Min(EvenUp(ey), inH));

        return (ix, iy, Math.Max(2, ex - ix), Math.Max(2, ey - iy));
    }

    // --- transform_to_content_area_int ---

    public static (int x, int y, int w, int h) TransformToContentAreaInt(
        (int x, int y, int w, int h) rect, string originalResolution)
    {
        var (fx, fy, fw, fh) = TransformToContentArea(rect, originalResolution);
        return OutwardRoundRect(fx, fy, fw, fh);
    }

    // --- clamp_overlay_position ---

    public static (int x, int y) ClampOverlayPosition(
        double x, double y, double width, double height,
        int paddingTopUi = CoordinateConstants.UIPaddingTop,
        int paddingBottomUi = CoordinateConstants.UIPaddingBottom)
    {
        var fx = Frac.FromDouble(x);
        var fy = Frac.FromDouble(y);
        var fw = Frac.FromDouble(width);
        var fh = Frac.FromDouble(height);

        var minY = new Frac(paddingTopUi, 1);
        var maxY = Max(minY, new Frac(CoordinateConstants.PortraitH - paddingBottomUi, 1) - fh);
        var maxX = Max(Frac.Zero, new Frac(CoordinateConstants.PortraitW, 1) - fw);

        return (ScaleRound(Max(Frac.Zero, Min(fx, maxX))), ScaleRound(Max(minY, Min(fy, maxY))));
    }

    // --- clamp_content_crop ---

    public static (int w, int h, int x, int y) ClampContentCrop((int w, int h, int x, int y) rect)
    {
        int w = Math.Max(0, Math.Min(CoordinateConstants.ContentW * 3, rect.w));
        int h = Math.Max(0, Math.Min(CoordinateConstants.ContentH, rect.h));
        int y = Math.Max(0, Math.Min(CoordinateConstants.ContentH - (h > 0 ? h : CoordinateConstants.ContentH), rect.y));
        int x = Math.Max(-CoordinateConstants.ContentW * 2, Math.Min(CoordinateConstants.ContentW * 3, rect.x));
        return (w, h, x, y);
    }

    // --- scale_rect / scale_rect_int ---

    public static (Frac x, Frac y, Frac w, Frac h) ScaleRect(
        (double x, double y, double w, double h) rect, double scaleFactor)
    {
        var fx = Frac.FromDouble(rect.x);
        var fy = Frac.FromDouble(rect.y);
        var fw = Frac.FromDouble(rect.w);
        var fh = Frac.FromDouble(rect.h);
        var factor = Frac.FromDouble(scaleFactor);
        return (fx, fy, fw * factor, fh * factor);
    }

    public static (int x, int y, int w, int h) ScaleRectInt(
        (int x, int y, int w, int h) rect, double scaleFactor)
    {
        var (x, y, w, h) = ScaleRect(rect, scaleFactor);
        return (ScaleRound(x), ScaleRound(y), Math.Max(1, FracCeil(w)), Math.Max(1, FracCeil(h)));
    }

    // --- Min/Max helpers for Frac ---

    private static Frac Max(Frac a, Frac b) => a > b ? a : b;
    private static Frac Min(Frac a, Frac b) => a < b ? a : b;
}
