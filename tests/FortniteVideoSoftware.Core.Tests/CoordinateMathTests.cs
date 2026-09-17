using System;
using FortniteVideoSoftware.Core.Media;
using Xunit;

namespace FortniteVideoSoftware.Core.Tests;

public class CoordinateMathTests
{
    // =========================================================================
    // 1. Frac Rational Arithmetic Tests
    // =========================================================================

    [Fact]
    public void Frac_ConstructAndNormalize_SimplifiesCorrectly()
    {
        var f1 = new Frac(2, 4);
        Assert.Equal(1, f1.Num);
        Assert.Equal(2, f1.Den);

        var f2 = new Frac(-6, -9);
        Assert.Equal(2, f2.Num);
        Assert.Equal(3, f2.Den);

        var f3 = new Frac(10, -20);
        Assert.Equal(-1, f3.Num);
        Assert.Equal(2, f3.Den);

        Assert.Throws<DivideByZeroException>(() => new Frac(5, 0));
    }

    [Fact]
    public void Frac_ArithmeticOperators_CalculateAccurately()
    {
        var a = new Frac(1, 3);
        var b = new Frac(1, 6);

        var sum = a + b;
        Assert.Equal(new Frac(1, 2), sum);

        var diff = a - b;
        Assert.Equal(new Frac(1, 6), diff);

        var prod = a * b;
        Assert.Equal(new Frac(1, 18), prod);

        var quot = a / b;
        Assert.Equal(new Frac(2, 1), quot);

        var divZero = a / Frac.Zero;
        Assert.Equal(Frac.Zero, divZero);

        var neg = -a;
        Assert.Equal(new Frac(-1, 3), neg);
    }

    [Fact]
    public void Frac_ComparisonAndEquality_BehavesDeterministically()
    {
        var small = new Frac(1, 4);
        var equalSmall = new Frac(2, 8);
        var large = new Frac(1, 2);

        Assert.True(small == equalSmall);
        Assert.False(small != equalSmall);
        Assert.True(small.Equals(equalSmall));
        Assert.Equal(small.GetHashCode(), equalSmall.GetHashCode());

        Assert.True(small < large);
        Assert.True(small <= large);
        Assert.True(large > small);
        Assert.True(large >= small);
        Assert.False(small > large);
        Assert.Equal(-1, small.CompareTo(large));
        Assert.Equal(1, large.CompareTo(small));
        Assert.Equal(0, small.CompareTo(equalSmall));
    }

    [Theory]
    [InlineData("30", 30, 1)]
    [InlineData("30000/1001", 30000, 1001)]
    [InlineData("60/1", 60, 1)]
    [InlineData("0", 0, 1)]
    [InlineData("29.97", 2997, 100)]
    [InlineData("  16 / 9  ", 16, 9)]
    public void Frac_FromString_ParsesValidRepresentations(string input, long expectedNum, long expectedDen)
    {
        var frac = Frac.FromString(input);
        Assert.Equal(expectedNum, frac.Num);
        Assert.Equal(expectedDen, frac.Den);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid")]
    [InlineData("10/0")]
    [InlineData("1/2/3")]
    public void Frac_FromString_MalformedInputs_YieldZeroWithoutThrowing(string? input)
    {
        var frac = Frac.FromString(input!);
        Assert.Equal(Frac.Zero, frac);
    }

    [Fact]
    public void Frac_FromDouble_HandlesEdgeCases()
    {
        Assert.Equal(Frac.Zero, Frac.FromDouble(double.NaN));
        Assert.Equal(Frac.Zero, Frac.FromDouble(double.PositiveInfinity));
        Assert.Equal(Frac.Zero, Frac.FromDouble(double.NegativeInfinity));
        Assert.Equal(Frac.Zero, Frac.FromDouble(0.0));

        var half = Frac.FromDouble(0.5);
        Assert.Equal(new Frac(1, 2), half);

        var negThird = Frac.FromDouble(-0.75);
        Assert.Equal(new Frac(-3, 4), negThird);
    }

    [Fact]
    public void Frac_ToStringAndToDouble_ConvertsFaithfully()
    {
        var f1 = new Frac(7, 1);
        Assert.Equal("7", f1.ToString());
        Assert.Equal(7.0, f1.ToDouble());

        var f2 = new Frac(3, 4);
        Assert.Equal("3/4", f2.ToString());
        Assert.Equal(0.75, f2.ToDouble());
    }

    // =========================================================================
    // 2. Rounding and Parity Utilities
    // =========================================================================

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(-1, -2)]
    [InlineData(-2, -2)]
    public void EvenDown_SnapsDownToEven(int input, int expected)
    {
        Assert.Equal(expected, CoordinateMath.EvenDown(input));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(-1, 0)]
    [InlineData(-2, -2)]
    public void EvenUp_SnapsUpToEven(int input, int expected)
    {
        Assert.Equal(expected, CoordinateMath.EvenUp(input));
    }

    [Theory]
    [InlineData(0.0, 2)]
    [InlineData(1.5, 2)]
    [InlineData(2.1, 2)]
    [InlineData(3.9, 2)]
    [InlineData(4.0, 4)]
    [InlineData(5.8, 4)]
    public void EvenDim_EnforcesMinimumTwoAndEven(double input, int expected)
    {
        Assert.Equal(expected, CoordinateMath.EvenDim(input));
    }

    [Fact]
    public void FracFloorAndCeil_RoundsDeterministically()
    {
        Assert.Equal(2, CoordinateMath.FracFloor(new Frac(7, 3)));
        Assert.Equal(3, CoordinateMath.FracCeil(new Frac(7, 3)));

        Assert.Equal(-3, CoordinateMath.FracFloor(new Frac(-7, 3)));
        Assert.Equal(-2, CoordinateMath.FracCeil(new Frac(-7, 3)));

        Assert.Equal(2, CoordinateMath.FracFloor(new Frac(6, 3)));
        Assert.Equal(2, CoordinateMath.FracCeil(new Frac(6, 3)));
    }

    [Fact]
    public void ScaleRound_HalfUpSymmetricRounding()
    {
        Assert.Equal(2, CoordinateMath.ScaleRound(new Frac(3, 2))); // 1.5 -> 2
        Assert.Equal(1, CoordinateMath.ScaleRound(new Frac(14, 10))); // 1.4 -> 1
        Assert.Equal(-2, CoordinateMath.ScaleRound(new Frac(-3, 2))); // -1.5 -> -2
        Assert.Equal(-1, CoordinateMath.ScaleRound(new Frac(-14, 10))); // -1.4 -> -1
    }

    // =========================================================================
    // 3. Resolution Parsing
    // =========================================================================

    [Theory]
    [InlineData("1920x1080", 1920, 1080)]
    [InlineData("2560X1440", 2560, 1440)]
    [InlineData("3840:2160", 3840, 2160)]
    [InlineData("3440 1440", 3440, 1440)]
    [InlineData(" 1280 x 720 ", 1280, 720)]
    public void GetResolutionInts_ParsesStandardFormats(string input, int expectedW, int expectedH)
    {
        var (w, h) = CoordinateMath.GetResolutionInts(input);
        Assert.Equal(expectedW, w);
        Assert.Equal(expectedH, h);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0x0")]
    [InlineData("0x1080")]
    [InlineData("1920x0")]
    [InlineData("-1920x1080")]
    [InlineData("9999999999999999999999999999x1080")]
    [InlineData("not_a_resolution")]
    public void GetResolutionInts_UnusableInput_FallsBackSafely(string? input)
    {
        var (w, h) = CoordinateMath.GetResolutionInts(input);
        Assert.Equal(1920, w);
        Assert.Equal(1080, h);
    }

    // =========================================================================
    // 4. ScalePlan Across Aspect Ratios
    // =========================================================================

    [Fact]
    public void ScalePlan_1080p_ScalesToCover1280x1920()
    {
        var (scaledW, scaledH, cropX, cropY, scale) = CoordinateMath.ScalePlan("1920x1080");

        Assert.Equal(new Frac(16, 9), scale);
        Assert.Equal(3414, scaledW);
        Assert.Equal(1920, scaledH);
        Assert.Equal(1066, cropX);
        Assert.Equal(0, cropY);
        Assert.Equal(0, cropX % 2);
        Assert.Equal(0, cropY % 2);
    }

    [Fact]
    public void ScalePlan_1440p_ScalesIdenticallyToPortraitTarget()
    {
        var (scaledW, scaledH, cropX, cropY, scale) = CoordinateMath.ScalePlan("2560x1440");

        Assert.Equal(new Frac(4, 3), scale);
        Assert.Equal(3414, scaledW);
        Assert.Equal(1920, scaledH);
        Assert.Equal(1066, cropX);
        Assert.Equal(0, cropY);
    }

    [Fact]
    public void ScalePlan_4K_ScalesIdenticallyToPortraitTarget()
    {
        var (scaledW, scaledH, cropX, cropY, scale) = CoordinateMath.ScalePlan("3840x2160");

        Assert.Equal(new Frac(8, 9), scale);
        Assert.Equal(3414, scaledW);
        Assert.Equal(1920, scaledH);
        Assert.Equal(1066, cropX);
        Assert.Equal(0, cropY);
    }

    [Fact]
    public void ScalePlan_Ultrawide21by9_CropsSymmetrically()
    {
        var (scaledW, scaledH, cropX, cropY, scale) = CoordinateMath.ScalePlan("2560x1080");

        Assert.Equal(new Frac(16, 9), scale);
        Assert.Equal(4552, scaledW);
        Assert.Equal(1920, scaledH);
        Assert.True(cropX > 0);
        Assert.Equal(1636, cropX);
        Assert.Equal(0, cropY);
        Assert.Equal(0, cropX % 2);
    }

    [Fact]
    public void ScalePlan_MalformedResolution_DoesNotThrowDivideByZero()
    {
        var plan = CoordinateMath.ScalePlan("0x0");
        Assert.Equal(3414, plan.scaledW);
        Assert.Equal(1920, plan.scaledH);
        Assert.Equal(1066, plan.cropX);
        Assert.Equal(0, plan.cropY);
    }

    // =========================================================================
    // 5. HUD dimensions stay even for export and preserve the source aspect ratio.
    // =========================================================================

    [Theory]
    [InlineData(64, 64, 1, 1)]
    [InlineData(134, 202, 1, 1)]
    [InlineData(300, 150, 4, 3)]
    [InlineData(1080, 1620, 1, 1)]
    [InlineData(10, 10, 1, 1)]
    public void QuantizeBackendSizeInternal_PreservesAspectAndEvenDimensions(
        int contentW, int contentH, long scaleNum, long scaleDen)
    {
        var scaleFrac = new Frac(scaleNum, scaleDen);
        var (bw, bh) = CoordinateMath.QuantizeBackendSizeInternal(contentW, contentH, scaleFrac);

        Assert.True(bw >= 2);
        Assert.True(bh >= 2);
        Assert.Equal(0, bw % 2);
        Assert.Equal(0, bh % 2);
        double desiredWidth = contentW * scaleFrac.ToDouble() * CoordinateConstants.BackendScale.ToDouble();
        Assert.InRange(Math.Abs(bw - desiredWidth), 0, 1.000001);
        Assert.InRange(Math.Abs(bh - bw * (double)contentH / contentW), 0, 1.000001);
    }

    [Theory]
    [InlineData(64, 64)]
    [InlineData(134, 202)]
    [InlineData(300, 150)]
    [InlineData(1080, 1620)]
    public void QuantizeBackendSize_PreviewMatchesExportWithinHalfAPixel(int contentW, int contentH)
    {
        var (width, height) = CoordinateMath.QuantizeBackendSize(contentW, contentH, Frac.One);

        var (rw, rh) = CoordinateMath.QuantizeBackendSizeInternal(contentW, contentH, Frac.One);
        double backendScale = CoordinateConstants.BackendScale.ToDouble();
        Assert.InRange(Math.Abs(width - rw / backendScale), 0, 0.500001);
        Assert.InRange(Math.Abs(height - rh / backendScale), 0, 0.500001);
    }

    // =========================================================================
    // 6. SnapZoomWindow, ZoomPadMargin & SnapExtent
    // =========================================================================

    [Fact]
    public void ZoomPadMargin_MeetsMinimumFormulaAndEvenAlignment()
    {
        double margin100 = CoordinateMath.ZoomPadMargin(100);
        // needed = 100/2 + 2 = 52 -> EvenDim(52) = 52
        Assert.Equal(52.0, margin100);

        double margin101 = CoordinateMath.ZoomPadMargin(101);
        // needed = 101/2 + 2 = 52.5 -> ceil = 53 -> EvenDim(53) = 52
        Assert.True(margin101 % 2 == 0);
    }

    [Fact]
    public void SnapExtent_ParityMatchesWindowCentre()
    {
        // centre = 1255 (odd)
        // raw = 359.1111
        int snappedOdd = CoordinateMath.SnapExtent(359.1111, 1255);
        Assert.Equal(0, snappedOdd % 2);
        Assert.Equal(0, (1255 - snappedOdd / 2) % 2); // Parity check: centre - extent / 2 is even

        // centre = 1256 (even)
        int snappedEven = CoordinateMath.SnapExtent(359.1111, 1256);
        Assert.Equal(0, snappedEven % 2);
        Assert.Equal(0, (1256 - snappedEven / 2) % 2);
    }

    [Fact]
    public void SnapZoomWindow_DriftPrevention_ChromaGridAlignment()
    {
        // Real-world DRIFT_01 scenario: 2560x1440, 134x202 box at X=1188, Y=600
        double resW = 2560;
        double resH = 1440;
        double targetZ = resH / 202.0; // ~7.1287
        double cropWRaw = resW / targetZ; // ~359.1111
        double cropHRaw = 202.0;
        double cxTarget = 1188 + 134 / 2.0; // 1255
        double cyTarget = 600 + 202 / 2.0;  // 701

        var zwin = CoordinateMath.SnapZoomWindow(cropWRaw, cropHRaw, resW, resH, cxTarget, cyTarget);

        // 1. Parity and Chroma grid alignment (all dimensions and offsets must be even integers)
        Assert.Equal(0, zwin.CropW % 2);
        Assert.Equal(0, zwin.CropH % 2);
        Assert.Equal(0, zwin.CropX % 2);
        Assert.Equal(0, zwin.CropY % 2);
        Assert.Equal(0, zwin.PadX % 2);
        Assert.Equal(0, zwin.PadY % 2);

        // 2. Exact center preservation
        int cx = (int)Math.Round(cxTarget, MidpointRounding.AwayFromZero);
        int cy = (int)Math.Round(cyTarget, MidpointRounding.AwayFromZero);
        Assert.Equal(zwin.PadX + cx - zwin.CropW / 2, zwin.CropX);
        Assert.Equal(zwin.PadY + cy - zwin.CropH / 2, zwin.CropY);

        // 3. Canvas dimensions encompass source plus padding
        Assert.Equal((int)resW + 2 * zwin.PadX, zwin.CanvasW);
        Assert.Equal((int)resH + 2 * zwin.PadY, zwin.CanvasH);

        // 4. Extent is within 2px of raw calculation
        Assert.True(Math.Abs(zwin.CropW - cropWRaw) <= 2.5);
        Assert.True(Math.Abs(zwin.CropH - cropHRaw) <= 2.5);
    }

    // =========================================================================
    // 7. Coordinate Transforms and Boundary Snapping
    // =========================================================================

    [Fact]
    public void TransformToContentArea_ExactRational_IsSymmetric()
    {
        var originalRect = (x: 100.0, y: 150.0, w: 400.0, h: 300.0);
        string res = "1920x1080";

        var (fx, fy, fw, fh) = CoordinateMath.TransformToContentArea(originalRect, res);
        var (ix, iy, iw, ih) = CoordinateMath.InverseTransformFromContentArea(
            (fx.ToDouble(), fy.ToDouble(), fw.ToDouble(), fh.ToDouble()), res);

        Assert.Equal(originalRect.x, ix.ToDouble(), 4);
        Assert.Equal(originalRect.y, iy.ToDouble(), 4);
        Assert.Equal(originalRect.w, iw.ToDouble(), 4);
        Assert.Equal(originalRect.h, ih.ToDouble(), 4);
    }

    [Fact]
    public void InverseTransformFromContentAreaInt_RoundsOutwardAndKeepsEvenParity()
    {
        var contentRect = (x: 50, y: 100, w: 200, h: 150);
        var sourceRect = CoordinateMath.InverseTransformFromContentAreaInt(contentRect, "1920x1080");

        Assert.True(sourceRect.x >= 0);
        Assert.True(sourceRect.y >= 0);
        Assert.True(sourceRect.w >= 2);
        Assert.True(sourceRect.h >= 2);
        Assert.Equal(0, sourceRect.x % 2);
        Assert.Equal(0, sourceRect.y % 2);
        Assert.Equal(0, sourceRect.w % 2);
        Assert.Equal(0, sourceRect.h % 2);
    }

    [Fact]
    public void ClampContentCrop_BoundsWithinLimits()
    {
        var clamped = CoordinateMath.ClampContentCrop((-5000, -100, 10000, 5000));
        Assert.True(clamped.w <= CoordinateConstants.ContentW * 3);
        Assert.True(clamped.h <= CoordinateConstants.ContentH);
        Assert.True(clamped.x >= -CoordinateConstants.ContentW * 2);
        Assert.True(clamped.y >= 0);
    }

    [Fact]
    public void ClampOverlayPosition_PreservesPositionToTheNearestPixel()
    {
        var (x, y) = CoordinateMath.ClampOverlayPosition(123.4, 250.7, 200, 100);

        Assert.Equal(123, x);
        Assert.Equal(251, y);
    }
}
