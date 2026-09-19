// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using Avalonia.Media;

namespace FortniteVideoSoftware.App.Infrastructure;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// COLORMATH_01 — HSV/RGB conversion, relative luminance and contrast-pair selection.
///
/// These four methods were private statics on <c>CropToolWindow</c>, a 6,710-line class that also
/// owns a HUD role taxonomy, an ffmpeg snapshot service, a resize-handle drag state machine, a
/// zoom viewport and cross-process config persistence. They touch none of that state — they are
/// arithmetic on three doubles — so they belong where they can be read and unit-tested without a
/// window.
///
/// ⚠️ BODIES MOVED VERBATIM. <c>RelativeLuminance</c> implements the sRGB transfer function used
/// by the WCAG contrast formula; the 0.03928 threshold, the 1/12.92 linear segment and the
/// 2.4 exponent are the specification's constants, not tunables. <c>OppositeOf</c>'s choice of
/// pairing is a design decision about legibility of role labels over arbitrary video frames.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
internal static class ColorMath
{
    internal static (double h, double s, double v) RgbToHsv(double r, double g, double b)
    {
        double rn = r / 255.0, gn = g / 255.0, bn = b / 255.0;
        double max = Math.Max(rn, Math.Max(gn, bn));
        double min = Math.Min(rn, Math.Min(gn, bn));
        double delta = max - min;

        double h;
        if (delta < 1e-6) h = 0;
        else if (max == rn) h = 60 * (((gn - bn) / delta) % 6);
        else if (max == gn) h = 60 * (((bn - rn) / delta) + 2);
        else h = 60 * (((rn - gn) / delta) + 4);

        if (h < 0) h += 360;
        return (h, max <= 1e-6 ? 0 : delta / max, max);
    }

    internal static Color HsvToColor(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0 % 2) - 1));
        double m = v - c;

        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return Color.FromRgb(
            (byte)Math.Clamp(Math.Round((r + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((g + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((b + m) * 255), 0, 255));
    }

    /// <summary>BANDCONTRAST_01 — WCAG relative luminance, 0 (black) to 1 (white).</summary>
    internal static double RelativeLuminance(double r, double g, double b)
    {
        static double Channel(double v)
        {
            double n = v / 255.0;
            return n <= 0.03928 ? n / 12.92 : Math.Pow((n + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }

    /// <summary>BANDCONTRAST_01 — complementary hue, inverted value, guaranteed to separate.</summary>
    internal static Color OppositeOf(double r, double g, double b)
    {
        var (h, sat, _) = RgbToHsv(r, g, b);
        double lum = RelativeLuminance(r, g, b);

        // A near-neutral region has no meaningful complement — the hue is numerical noise and would
        // flicker as the average shifted. Red is the defined default for exactly this case.
        double hue = sat < 0.12 ? 0.0 : (h + 180.0) % 360.0;
        double value = lum > 0.45 ? 0.20 : 1.00;

        Color candidate = HsvToColor(hue, 1.0, value);

        // Last check on the numbers rather than on the theory: if the complement still does not
        // separate (a mid-luminance, mid-saturation wash), fall back to flat black or flat white,
        // which always does.
        double candidateLum = RelativeLuminance(candidate.R, candidate.G, candidate.B);
        double ratio = (Math.Max(candidateLum, lum) + 0.05) / (Math.Min(candidateLum, lum) + 0.05);
        if (ratio < 3.0)
        {
            candidate = lum > 0.45 ? Color.FromRgb(0, 0, 0) : Color.FromRgb(255, 255, 255);
        }

        return candidate;
    }
}
