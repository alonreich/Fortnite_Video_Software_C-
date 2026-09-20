// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Invariants, constants, and threading models must match spec bit-for-bit.


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

    public long SafeDen => Den == 0 ? 1 : Den;

    public static readonly Frac Zero = new(0, 1);
    public static readonly Frac One = new(1, 1);

    public Frac(long num, long den)
    {
        if (den == 0) throw new DivideByZeroException("Denominator is zero.");
        Int128 n = num;
        Int128 d = den;
        if (d < 0) { n = -n; d = -d; }
        Int128 g = Gcd128(n, d);
        Num = (long)(n / g);
        Den = (long)(d / g);
    }

    public static Frac FromDouble(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return Zero;
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

    /// <summary>
    /// Parses "30", "30000/1001" or "29.97". Values arrive from ffprobe/ffmpeg output, so nothing
    /// here may throw on malformed text — an unparseable or zero-denominator expression yields
    /// <see cref="Zero"/> and lets the caller apply its own default.
    /// </summary>
    public static Frac FromString(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Zero;
        s = s.Trim();

        var ci = System.Globalization.CultureInfo.InvariantCulture;

        if (s.Contains('/'))
        {
            var parts = s.Split('/');
            if (parts.Length != 2) return Zero;
            if (!long.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Integer, ci, out long n)) return Zero;
            if (!long.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Integer, ci, out long d)) return Zero;
            if (d == 0) return Zero;
            return new Frac(n, d);
        }

        if (s.Contains('.') || s.Contains('e') || s.Contains('E'))
        {
            return double.TryParse(s, System.Globalization.NumberStyles.Float, ci, out double dv)
                ? FromDouble(dv)
                : Zero;
        }

        return long.TryParse(s, System.Globalization.NumberStyles.Integer, ci, out long iv)
            ? new Frac(iv, 1)
            : Zero;
    }

    private static Frac FromWide(Int128 num, Int128 den)
    {
        if (den == Int128.Zero) throw new DivideByZeroException("Denominator is zero.");
        if (den < Int128.Zero) { num = -num; den = -den; }

        Int128 g = Gcd128(num < Int128.Zero ? -num : num, den);
        if (g > Int128.One) { num /= g; den /= g; }

        if (num >= long.MinValue && num <= long.MaxValue && den <= long.MaxValue)
            return new Frac((long)num, (long)den);

        return FromDouble((double)num / (double)den);
    }

    private static Int128 Gcd128(Int128 a, Int128 b)
    {
        if (a < Int128.Zero) a = -a;
        if (b < Int128.Zero) b = -b;
        while (b != Int128.Zero) { Int128 t = a % b; a = b; b = t; }
        return a == Int128.Zero ? Int128.One : a;
    }

    public static Frac operator +(Frac a, Frac b) => FromWide((Int128)a.Num * b.SafeDen + (Int128)b.Num * a.SafeDen, (Int128)a.SafeDen * b.SafeDen);
    public static Frac operator -(Frac a, Frac b) => FromWide((Int128)a.Num * b.SafeDen - (Int128)b.Num * a.SafeDen, (Int128)a.SafeDen * b.SafeDen);
    public static Frac operator *(Frac a, Frac b) => FromWide((Int128)a.Num * b.Num, (Int128)a.SafeDen * b.SafeDen);
    public static Frac operator /(Frac a, Frac b) => b.Num == 0 ? Zero : FromWide((Int128)a.Num * b.SafeDen, (Int128)a.SafeDen * b.Num);
    public static Frac operator -(Frac a) => new(-a.Num, a.SafeDen);
    public static bool operator ==(Frac a, Frac b) => a.Num == b.Num && a.SafeDen == b.SafeDen;
    public static bool operator !=(Frac a, Frac b) => !(a == b);

    public static bool operator <(Frac a, Frac b) => (Int128)a.Num * b.SafeDen < (Int128)b.Num * a.SafeDen;
    public static bool operator >(Frac a, Frac b) => (Int128)a.Num * b.SafeDen > (Int128)b.Num * a.SafeDen;
    public static bool operator <=(Frac a, Frac b) => (Int128)a.Num * b.SafeDen <= (Int128)b.Num * a.SafeDen;
    public static bool operator >=(Frac a, Frac b) => (Int128)a.Num * b.SafeDen >= (Int128)b.Num * a.SafeDen;

    public bool Equals(Frac other) => Num == other.Num && SafeDen == other.SafeDen;
    public override bool Equals(object? obj) => obj is Frac f && Equals(f);
    public override int GetHashCode() => HashCode.Combine(Num, SafeDen);
    public int CompareTo(Frac other)
    {
        Int128 left = (Int128)Num * other.SafeDen;
        Int128 right = (Int128)other.Num * SafeDen;
        return left < right ? -1 : left > right ? 1 : 0;
    }
    public double ToDouble() => (double)Num / SafeDen;
    public override string ToString() => SafeDen == 1 ? Num.ToString() : $"{Num}/{SafeDen}";
}


public static class CoordinateConstants
{
    public const int PortraitW = 1080;
    public const int PortraitH = 1920;
    public const int InternalW = 1280;
    public const int InternalH = 1920;
    public const int UIPaddingTop = 150;
    public const int UIPaddingBottom = 150;
    public const int UIContentH = 1620;

    public const int TargetW = InternalW;
    public const int TargetH = InternalH;

    public const int ContentW = PortraitW;
    public const int ContentH = UIContentH;

    public const int PaddingTop = UIPaddingTop;

    public static readonly Frac BackendScale = new(InternalW, PortraitW);
    public static readonly Frac UIToInternalScale = BackendScale;
}

public readonly record struct ZoomWindow(int CropW, int CropH, int PadX, int PadY, int CanvasW, int CanvasH, int CropX, int CropY);


public static class CoordinateMath
{

    public static int FracFloor(Frac v)
    {
        long q = v.Num / v.SafeDen;
        long r = v.Num % v.SafeDen;
        if (r != 0 && ((r < 0) != (v.SafeDen < 0))) q--;
        return (int)q;
    }

    public static int FracCeil(Frac v)
    {
        long q = v.Num / v.SafeDen;
        long r = v.Num % v.SafeDen;
        if (r != 0 && ((r < 0) == (v.SafeDen < 0))) q++;
        return (int)q;
    }

    public static int EvenDown(int v) => v % 2 == 0 ? v : v - 1;
    public static int EvenUp(int v) => v % 2 == 0 ? v : v + 1;


    public static int ScaleRound(Frac val)
    {
        if (val.Num >= 0)
            return FracFloor(val + new Frac(1, 2));
        return -FracFloor(-val + new Frac(1, 2));
    }

    public static int ScaleRound(double d) => ScaleRound(Frac.FromDouble(d));


    public static (int x, int y, int w, int h) OutwardRoundRect(Frac x, Frac y, Frac w, Frac h)
    {
        int ix = FracFloor(x);
        int iy = FracFloor(y);
        int iw = FracCeil(x + w) - ix;
        int ih = FracCeil(y + h) - iy;
        return (ix, iy, Math.Max(1, iw), Math.Max(1, ih));
    }


    /// <summary>
    /// Parses "1920x1080" into numbers, falling back to 1920x1080 for anything unusable.
    ///
    /// ISSUE_2 — two guards that were missing. This value is not always produced in-process: zoom
    /// settings persist it as plain TEXT in the recovery JSON on disk (SpeedSegment.ZoomOrigRes),
    /// and it comes back out through GranularSpeedBuilder at export time. A damaged or hand-edited
    /// file could therefore feed this anything.
    ///
    ///   * ZERO. The regex happily matches "0x0". A zero then reached ScalePlan's
    ///     `new Frac(InternalW, inW)`, and the Frac constructor throws DivideByZeroException on a
    ///     zero denominator. Both dimensions must be positive to be a usable resolution.
    ///   * OVERFLOW. int.Parse throws OverflowException on a run of digits too large for an int.
    ///     TryParse simply fails instead.
    ///
    /// Both faults threw out of the export path and out of crash recovery — and until ISSUE_1 was
    /// fixed, a throw during recovery deleted the user's saved session. Falling back is always
    /// safer here than throwing: a wrong-but-sane resolution produces a slightly wrong crop, while
    /// an exception loses the whole job.
    /// </summary>
    public static (int w, int h) GetResolutionInts(string? resStr)
    {
        if (string.IsNullOrWhiteSpace(resStr)) return (1920, 1080);

        var match = Regex.Match(resStr, @"(\d+)\s*[x:X\s]\s*(\d+)");
        if (match.Success
            && int.TryParse(match.Groups[1].Value, out int w)
            && int.TryParse(match.Groups[2].Value, out int h)
            && w > 0 && h > 0)
        {
            return (w, h);
        }

        return (1920, 1080);
    }


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


    public static (Frac x, Frac y, Frac w, Frac h) TransformToContentArea(
        (double x, double y, double w, double h) rect, string originalResolution, string? driftType = null)
    {
        var fx = Frac.FromDouble(rect.x);
        var fy = Frac.FromDouble(rect.y);
        var fw = Frac.FromDouble(rect.w);
        var fh = Frac.FromDouble(rect.h);

        if (driftType == "left")
        {
            fx += Frac.One;
            fw -= Frac.One;
        }
        else if (driftType == "right")
        {
            fw -= Frac.One;
        }

        var (_, _, cropX, cropY, scale) = ScalePlan(originalResolution);

        var internalX = (fx * scale) - new Frac(cropX, 1);
        var internalY = (fy * scale) - new Frac(cropY, 1);
        var internalW = fw * scale;
        var internalH = fh * scale;

        var uiScale = CoordinateConstants.UIToInternalScale;
        return (internalX / uiScale, internalY / uiScale, internalW / uiScale, internalH / uiScale);
    }


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


    /// <summary>
    /// Content-space rect -> SOURCE-video rect, as whole even pixels.
    ///
    /// ISSUE_3 — ONE-WAY CONTRACT. This rounds strictly OUTWARD (floor the origin, ceil the far
    /// edge, then EvenDown/EvenUp) and that is deliberate: the FFmpeg crop it feeds must fully
    /// COVER the region the user selected, because a crop that lands one pixel short permanently
    /// clips a column of HUD pixels out of the export. <see cref="TransformToContentAreaInt"/>
    /// rounds outward for the same reason.
    ///
    /// The consequence is that the two integer wrappers are NOT inverses of each other — composing
    /// them grows the rect by up to 2px per axis. The exact-rational
    /// <see cref="TransformToContentArea"/> / <see cref="InverseTransformFromContentArea"/> pair IS
    /// symmetric; only the integer snapping is lossy.
    ///
    /// This is safe today because nothing in the pipeline iterates the composition: the Crop Tools
    /// editor never rehydrates saved config entries into editable items (existing layers are drawn
    /// as read-only placeholders by LoadExistingPlaceholdersAsync), undo/redo stores the SourceRect
    /// verbatim in ItemSnapshot, and the exporter transforms once per render.
    ///
    /// DO NOT feed the output of one of these back into the other in a loop, and do not "fix" the
    /// rounding to nearest — that would silently change the crop geometry of every existing saved
    /// profile.
    /// </summary>
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


    /// <summary>
    /// SOURCE-video rect -> content-space (1080x1620) rect, as whole pixels.
    /// ISSUE_3: rounds outward via <see cref="OutwardRoundRect"/>. See the one-way contract note on
    /// <see cref="InverseTransformFromContentAreaInt"/> — these two must not be composed in a loop.
    /// </summary>
    public static (int x, int y, int w, int h) TransformToContentAreaInt(
        (int x, int y, int w, int h) rect, string originalResolution, string? driftType = null)
    {
        var (fx, fy, fw, fh) = TransformToContentArea((rect.x, rect.y, rect.w, rect.h), originalResolution, driftType);
        return OutwardRoundRect(fx, fy, fw, fh);
    }


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

        // ══════════════════════════════════════════════════════════════════════════════════════
        // RATIOLOCK_01 — THE 27-PIXEL POSITION GRID IS GONE.
        //
        // This used to snap X and Y onto a grid of BackendScale.Den (= 27) content pixels, for the
        // same reason the size quantizer used 32: a content coordinate that is a multiple of 27
        // converts to backend space (x * 32/27) as an exact integer.
        //
        // It is the single largest contributor to the composer feeling broken. Dragging an element
        // moved it in 27-pixel lurches, and it applied whether or not the SNAP checkbox was ticked
        // — so the control that was supposed to turn snapping off could not, because this snap was
        // underneath it and invisible. Every resize went through here too (the anchor is clamped),
        // so it staircased the resize as well.
        //
        // And it was never needed: MobileFilterBuilder already rounds the backend position it
        // computes from these values (ScaleRound on lxRaw/lyRaw), so a non-multiple costs at most
        // half a backend pixel. What is left here is the clamp that actually matters — the element
        // must stay inside the 1080-wide frame and inside the content band between the two 150px
        // text strips.
        // ══════════════════════════════════════════════════════════════════════════════════════
        int rawX = ScaleRound(Max(Frac.Zero, Min(fx, maxX)));
        int rawY = ScaleRound(Max(minY, Min(fy, maxY)));

        int limitX = Math.Max(0, CoordinateConstants.PortraitW - ScaleRound(fw));
        int limitY = Math.Max(paddingTopUi, CoordinateConstants.PortraitH - paddingBottomUi - ScaleRound(fh));

        return (Math.Clamp(rawX, 0, limitX), Math.Clamp(rawY, paddingTopUi, limitY));
    }


    public static (int w, int h, int x, int y) ClampContentCrop((int w, int h, int x, int y) rect)
    {
        int w = Math.Max(0, Math.Min(CoordinateConstants.ContentW * 3, rect.w));
        int h = Math.Max(0, Math.Min(CoordinateConstants.ContentH, rect.h));
        int y = Math.Max(0, Math.Min(CoordinateConstants.ContentH - (h > 0 ? h : CoordinateConstants.ContentH), rect.y));
        int x = Math.Max(-CoordinateConstants.ContentW * 2, Math.Min(CoordinateConstants.ContentW * 3, rect.x));
        return (w, h, x, y);
    }


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

    /// <summary>
    /// RATIOLOCK_01 — the smallest step a HUD layer's backend size may move in.
    ///
    /// TWO, not thirty-two. See <see cref="QuantizeBackendSizeInternal"/> for why the 32 went and
    /// what replaced the property it was protecting. Two rather than one because ffmpeg's scale
    /// filter feeds a yuv420p chain, whose chroma planes are half-resolution: an odd layer size is
    /// legal but makes the encoder round it anyway, in a place this code cannot see.
    /// </summary>
    public const int BackendSizeStep = 2;

    /// <summary>
    /// ISSUE_4 — THE single quantizer for HUD layer size, in BACKEND (1280x1920 internal) pixels.
    ///
    /// ══════════════════════════════════════════════════════════════════════════════════════
    /// RATIOLOCK_01 — WHAT CHANGED AND WHY THE OLD 32-PIXEL GRID HAD TO GO.
    ///
    /// This method used to round WIDTH and HEIGHT INDEPENDENTLY to multiples of 32 backend pixels
    /// (= <c>BackendScale.Num</c>, since 1280/1080 reduces to 32/27). Two faults came out of that,
    /// and both were reported from the composer as feel problems rather than as maths problems:
    ///
    ///   * THE RATIO WAS DESTROYED. Two axes rounded separately do not keep their proportion. A
    ///     300x60 element became 297x54 — a 10% vertical squash — and a 100x40 element became
    ///     81x27, a 33% squash. The user drew a box around a health bar on the frozen frame and
    ///     the composer showed them a different shape. No amount of care with the mouse could fix
    ///     it, because the distortion was applied AFTER the mouse.
    ///   * THE RESIZE STAIRCASED. 32 backend pixels is 27 content pixels, so dragging a corner
    ///     moved the edge in 27-pixel jumps. That is the "jumpy, stuttery, unstable" feel: the box
    ///     does not follow the pointer, it lurches after it.
    ///
    /// The 32 existed for one reason, stated in the old comment: a backend size that is a multiple
    /// of 32 converts to <c>(rw / 32) * 27</c> content pixels EXACTLY, with no rounding. That is a
    /// nicety, not a requirement — <see cref="QuantizeBackendSize"/> has always passed that
    /// conversion through <see cref="ScaleRound"/>, and MobileFilterBuilder never converts a layer
    /// size back to content space at all: it hands <c>rw</c> and <c>rh</c> straight to ffmpeg's
    /// scale filter in backend pixels. Giving up exactness costs at most half a content pixel of
    /// display rounding. Keeping it cost the user the shape of every element they cropped.
    ///
    /// SO: the WIDTH is rounded to <see cref="BackendSizeStep"/>, and the HEIGHT is then derived
    /// from the width by the EXACT source ratio (contentH : contentW) rather than being rounded on
    /// its own. The ratio is therefore locked by construction, to within one backend pixel, at
    /// every size — which is the property the composer, the preview and the export all needed and
    /// none of them had.
    ///
    /// Rounding stays exact rational half-up via <see cref="ScaleRound"/>, NOT
    /// <c>Math.Round(double)</c>: 32/27 has no exact binary representation and banker's rounding
    /// would put preview and export half a pixel apart at random sizes.
    /// ══════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    public static (int backendW, int backendH) QuantizeBackendSizeInternal(int contentW, int contentH, Frac scaleFrac)
    {
        Frac backendScale = CoordinateConstants.BackendScale;
        int step = BackendSizeStep;

        int safeW = Math.Max(1, contentW);
        int safeH = Math.Max(1, contentH);

        Frac rawW = new Frac(safeW, 1) * scaleFrac * backendScale;

        int rw = Math.Max(step, ScaleRound(rawW / new Frac(step, 1)) * step);

        // The height is NOT quantized on its own — that is the whole point. It is the width put
        // through the source rectangle's exact ratio, then snapped to the same even step.
        Frac exactH = new Frac(rw, 1) * new Frac(safeH, safeW);
        int rh = Math.Max(step, ScaleRound(exactH / new Frac(step, 1)) * step);

        return (rw, rh);
    }

    /// <summary>
    /// Same quantization as <see cref="QuantizeBackendSizeInternal"/>, expressed in UI/content
    /// (1080x1620) pixels. Used by the Crop Tools preview and by HudConfig. The division is exact
    /// because the backend size is always a multiple of 32.
    /// </summary>
    public static (int width, int height) QuantizeBackendSize(int contentW, int contentH, Frac scaleFrac)
    {
        Frac backendScale = CoordinateConstants.BackendScale;
        var (rw, rh) = QuantizeBackendSizeInternal(contentW, contentH, scaleFrac);

        int width = ScaleRound(new Frac(rw, 1) / backendScale);
        int height = ScaleRound(new Frac(rh, 1) / backendScale);

        return (width, height);
    }

    public static int EvenDim(double v)
    {
        int i = (int)v;
        if (i < 2) return 2;
        return i - (i % 2);
    }

    public static double ZoomPadMargin(double cropExtent)
    {
        double needed = Math.Max(0.0, cropExtent) / 2.0 + 2.0;
        return EvenDim(Math.Ceiling(needed));
    }

    public static int SnapExtent(double raw, int centre)
    {
        int lo = (int)Math.Floor(raw);
        lo -= lo & 1;
        if (lo < 2) lo = 2;
        int pick = ((centre - lo / 2) & 1) == 0 ? lo : lo + 2;
        return pick < 2 ? 2 : pick;
    }

    /// <summary>
    /// DRIFT_01 — snaps zoom crop window onto the grid FFmpeg actually uses.
    /// Emits a window that is whole and chroma-aligned, preserving the centre exactly.
    /// </summary>
    public static ZoomWindow SnapZoomWindow(double cropWRaw, double cropHRaw, double resW, double resH,
                                            double cxTarget, double cyTarget)
    {
        int cx = (int)Math.Round(cxTarget, MidpointRounding.AwayFromZero);
        int cy = (int)Math.Round(cyTarget, MidpointRounding.AwayFromZero);

        int w = SnapExtent(cropWRaw, cx);
        int h = SnapExtent(cropHRaw, cy);

        int padX = (int)ZoomPadMargin(w);
        int padY = (int)ZoomPadMargin(h);

        return new ZoomWindow(w, h, padX, padY,
                              (int)resW + 2 * padX, (int)resH + 2 * padY,
                              padX + cx - w / 2, padY + cy - h / 2);
    }

    private static Frac Max(Frac a, Frac b) => a > b ? a : b;
    private static Frac Min(Frac a, Frac b) => a < b ? a : b;
}
