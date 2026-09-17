using System;
using System.Collections.Generic;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// MAGICWAND_02 — THE IMAGE PRIMITIVES THE HUD DETECTOR NEEDS, AND NOTHING ELSE.
///
/// WHY THIS FILE EXISTS AT ALL.
/// <see cref="HudAutoDetector"/> is a port of the old Python tool's <c>magic_wand.py</c>, which was
/// written against OpenCV. This solution has no OpenCV and is not going to get one: invariant 1 in
/// docs/README.md is the Single Binary Executable Mandate, and OpenCvSharp ships a ~40 MB native
/// blob per RID. SkiaSharp is already referenced but is a RASTERISER — it draws, it does not
/// analyse; it has no adaptive threshold, no morphology, no contours and no Canny.
///
/// So the eight operations the detector actually uses are implemented here, deliberately narrowly:
/// 8-bit single-channel, one anchor, one border rule. They are NOT a general image library and must
/// not grow into one. Each one documents which OpenCV call it stands in for and, where the two
/// differ, exactly how.
///
/// EVERYTHING IS ROW-MAJOR, <c>byte[]</c>, <c>index = y * width + x</c>, and every method returns a
/// NEW array rather than writing in place — the pipeline reuses its inputs (the same edge mask is
/// scored against four different roles), so in-place would be a very quiet bug.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
internal static class HudImageOps
{
    // ──────────────────────────────────────────────────────────────────────────────────────────
    // COLOUR CONVERSION
    // ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>cv2.cvtColor(src, COLOR_BGR2GRAY)</c>. Same ITU-R BT.601 luma weights OpenCV uses
    /// (0.114 B, 0.587 G, 0.299 R), same rounding.
    /// </summary>
    /// <param name="bgr">Interleaved BGR, 3 bytes per pixel.</param>
    public static byte[] BgrToGray(byte[] bgr, int width, int height)
    {
        var gray = new byte[width * height];
        for (int i = 0, p = 0; i < gray.Length; i++, p += 3)
        {
            // +0.5 then truncate == round-half-up, which is what OpenCV's fixed-point path does.
            gray[i] = (byte)((bgr[p] * 0.114 + bgr[p + 1] * 0.587 + bgr[p + 2] * 0.299) + 0.5);
        }
        return gray;
    }

    /// <summary>
    /// <c>cv2.cvtColor(src, COLOR_BGR2HSV)</c> in OpenCV's 8-bit convention: H is 0..179 (degrees
    /// halved so it fits a byte), S and V are 0..255. The detector's colour anchors
    /// (<see cref="HudAutoDetector.BuildColourAnchorMask"/>) are written in those units, straight
    /// out of the Python, so getting this convention right is not optional.
    /// </summary>
    /// <returns>Interleaved HSV, 3 bytes per pixel, same layout as the input.</returns>
    public static byte[] BgrToHsv(byte[] bgr, int width, int height)
    {
        var hsv = new byte[width * height * 3];
        for (int p = 0; p < hsv.Length; p += 3)
        {
            int b = bgr[p];
            int g = bgr[p + 1];
            int r = bgr[p + 2];

            int max = r > g ? (r > b ? r : b) : (g > b ? g : b);
            int min = r < g ? (r < b ? r : b) : (g < b ? g : b);
            int delta = max - min;

            int v = max;
            int sat = max == 0 ? 0 : (int)((delta * 255.0) / max + 0.5);

            double h;
            if (delta == 0) h = 0;
            else if (max == r) h = 60.0 * (g - b) / delta;
            else if (max == g) h = 120.0 + 60.0 * (b - r) / delta;
            else h = 240.0 + 60.0 * (r - g) / delta;

            if (h < 0) h += 360.0;

            hsv[p] = (byte)(h * 0.5 + 0.5);   // 0..179
            hsv[p + 1] = (byte)sat;
            hsv[p + 2] = (byte)v;
        }
        return hsv;
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────
    // BLUR + THRESHOLD
    // ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// OpenCV's <c>getGaussianKernel(n, sigma)</c>. When sigma is non-positive OpenCV derives it
    /// from the kernel size with this exact formula, and the detector relies on that default
    /// (<c>adaptiveThreshold(..., blockSize: 21, ...)</c> never passes a sigma), so it is
    /// reproduced rather than approximated.
    /// </summary>
    private static double[] GaussianKernel(int size, double sigma)
    {
        if (sigma <= 0) sigma = 0.3 * ((size - 1) * 0.5 - 1) + 0.8;

        var k = new double[size];
        int half = size / 2;
        double sum = 0;
        double twoSigmaSq = 2.0 * sigma * sigma;

        for (int i = 0; i < size; i++)
        {
            int d = i - half;
            k[i] = Math.Exp(-(d * d) / twoSigmaSq);
            sum += k[i];
        }
        for (int i = 0; i < size; i++) k[i] /= sum;
        return k;
    }

    /// <summary>
    /// Separable Gaussian blur with BORDER_REPLICATE, returning DOUBLES.
    ///
    /// Doubles, not bytes, because the only consumer is <see cref="AdaptiveThresholdGaussianInv"/>,
    /// which compares <c>src</c> against <c>localMean - C</c>. Rounding the mean to a byte first
    /// would quantise the very quantity the comparison turns on and flip pixels near the boundary.
    /// BORDER_REPLICATE is what <c>cv2.adaptiveThreshold</c> uses internally.
    /// </summary>
    private static double[] GaussianBlurToDouble(byte[] src, int width, int height, int size, double sigma)
    {
        double[] k = GaussianKernel(size, sigma);
        int half = size / 2;

        var tmp = new double[width * height];
        var dst = new double[width * height];

        // Horizontal pass.
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                double acc = 0;
                for (int t = -half; t <= half; t++)
                {
                    int sx = x + t;
                    if (sx < 0) sx = 0;
                    else if (sx >= width) sx = width - 1;
                    acc += k[t + half] * src[row + sx];
                }
                tmp[row + x] = acc;
            }
        }

        // Vertical pass.
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double acc = 0;
                for (int t = -half; t <= half; t++)
                {
                    int sy = y + t;
                    if (sy < 0) sy = 0;
                    else if (sy >= height) sy = height - 1;
                    acc += k[t + half] * tmp[sy * width + x];
                }
                dst[y * width + x] = acc;
            }
        }

        return dst;
    }

    /// <summary>Separable Gaussian blur returning bytes. Used by the circle hunter, which feeds a
    /// blurred 8-bit image to Sobel exactly as <c>cv2.GaussianBlur(img, (9,9), 2)</c> did.</summary>
    public static byte[] GaussianBlur(byte[] src, int width, int height, int size, double sigma)
    {
        double[] blurred = GaussianBlurToDouble(src, width, height, size, sigma);
        var dst = new byte[src.Length];
        for (int i = 0; i < dst.Length; i++)
        {
            int v = (int)(blurred[i] + 0.5);
            dst[i] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        }
        return dst;
    }

    /// <summary>
    /// <c>cv2.adaptiveThreshold(src, 255, ADAPTIVE_THRESH_GAUSSIAN_C, THRESH_BINARY_INV, blockSize, C)</c>.
    ///
    /// OpenCV computes <c>T(x,y) = gaussianMean(x,y) - C</c> and then, for BINARY_INV, emits
    /// <c>src > T ? 0 : 255</c>. Note the STRICT greater-than: a pixel exactly equal to the
    /// threshold is foreground. That asymmetry is preserved here because the HUD glyphs this is
    /// hunting are flat-shaded and produce long runs of exactly-equal pixels.
    ///
    /// The detector uses it to find DARK structure on a bright frame — HUD panels are typically
    /// dark plates — which is why the INV variant is the one that got ported.
    /// </summary>
    public static byte[] AdaptiveThresholdGaussianInv(byte[] src, int width, int height, int blockSize, double c)
    {
        if ((blockSize & 1) == 0) blockSize++;   // OpenCV requires odd; be forgiving rather than throw.

        double[] mean = GaussianBlurToDouble(src, width, height, blockSize, 0);
        var dst = new byte[src.Length];
        for (int i = 0; i < dst.Length; i++)
        {
            dst[i] = src[i] > mean[i] - c ? (byte)0 : (byte)255;
        }
        return dst;
    }

    /// <summary><c>cv2.threshold(src, thresh, 255, THRESH_BINARY)</c>: strictly greater wins.</summary>
    public static byte[] ThresholdBinary(byte[] src, byte thresh)
    {
        var dst = new byte[src.Length];
        for (int i = 0; i < dst.Length; i++) dst[i] = src[i] > thresh ? (byte)255 : (byte)0;
        return dst;
    }

    /// <summary>
    /// <c>cv2.normalize(src, None, 0, 255, NORM_MINMAX)</c> for a float map, rounded to bytes.
    /// A flat map (max == min) normalises to all-zero, matching OpenCV's behaviour when the range
    /// collapses.
    /// </summary>
    public static byte[] NormalizeMinMaxToByte(double[] src)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (double v in src)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }

        var dst = new byte[src.Length];
        double range = max - min;
        if (range <= double.Epsilon) return dst;

        for (int i = 0; i < src.Length; i++)
        {
            int v = (int)((src[i] - min) * 255.0 / range + 0.5);
            dst[i] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        }
        return dst;
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────
    // MORPHOLOGY
    // ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rectangular dilate — <c>cv2.dilate(src, getStructuringElement(MORPH_RECT, (w,h)))</c>,
    /// centre anchor.
    ///
    /// Separable (a rectangle is the product of a horizontal and a vertical line), so this is two
    /// linear passes rather than one quadratic one — at 960x540 with a 5x5 kernel that is the
    /// difference between 25 and 10 reads per pixel.
    ///
    /// BORDER: out-of-range samples are simply skipped. That is not laziness — it is precisely
    /// OpenCV's default <c>morphologyDefaultBorderValue()</c>, which is -infinity for dilate and
    /// +infinity for erode, i.e. a border that can never win the max (or the min).
    /// </summary>
    public static byte[] Dilate(byte[] src, int width, int height, int kw, int kh)
        => RankFilter(src, width, height, kw, kh, max: true);

    /// <summary>Rectangular erode — <c>cv2.erode</c> with a MORPH_RECT element, centre anchor.
    /// See <see cref="Dilate"/> for the border rule.</summary>
    public static byte[] Erode(byte[] src, int width, int height, int kw, int kh)
        => RankFilter(src, width, height, kw, kh, max: false);

    /// <summary><c>cv2.morphologyEx(src, MORPH_CLOSE, rect(kw,kh))</c> — dilate, then erode.
    /// Closes the gaps between the strokes of a glyph so a word becomes one blob.</summary>
    public static byte[] MorphClose(byte[] src, int width, int height, int kw, int kh)
        => Erode(Dilate(src, width, height, kw, kh), width, height, kw, kh);

    /// <summary><c>cv2.morphologyEx(src, MORPH_OPEN, rect(kw,kh))</c> — erode, then dilate.
    /// Removes speckle smaller than the kernel without shrinking what survives.</summary>
    public static byte[] MorphOpen(byte[] src, int width, int height, int kw, int kh)
        => Dilate(Erode(src, width, height, kw, kh), width, height, kw, kh);

    private static byte[] RankFilter(byte[] src, int width, int height, int kw, int kh, bool max)
    {
        int halfW = kw / 2;
        int halfH = kh / 2;

        var tmp = new byte[src.Length];
        var dst = new byte[src.Length];

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int lo = x - halfW; if (lo < 0) lo = 0;
                int hi = x + halfW; if (hi >= width) hi = width - 1;

                byte best = src[row + lo];
                for (int sx = lo + 1; sx <= hi; sx++)
                {
                    byte v = src[row + sx];
                    if (max ? v > best : v < best) best = v;
                }
                tmp[row + x] = best;
            }
        }

        for (int y = 0; y < height; y++)
        {
            int lo = y - halfH; if (lo < 0) lo = 0;
            int hi = y + halfH; if (hi >= height) hi = height - 1;

            for (int x = 0; x < width; x++)
            {
                byte best = tmp[lo * width + x];
                for (int sy = lo + 1; sy <= hi; sy++)
                {
                    byte v = tmp[sy * width + x];
                    if (max ? v > best : v < best) best = v;
                }
                dst[y * width + x] = best;
            }
        }

        return dst;
    }

    /// <summary><c>cv2.bitwise_or</c> over two masks of equal length.</summary>
    public static byte[] Or(byte[] a, byte[] b)
    {
        var dst = new byte[a.Length];
        for (int i = 0; i < dst.Length; i++) dst[i] = (byte)(a[i] | b[i]);
        return dst;
    }

    /// <summary><c>cv2.bitwise_and</c> over two masks of equal length.</summary>
    public static byte[] And(byte[] a, byte[] b)
    {
        var dst = new byte[a.Length];
        for (int i = 0; i < dst.Length; i++) dst[i] = (byte)(a[i] & b[i]);
        return dst;
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────
    // EDGES
    // ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>cv2.Canny(src, low, high)</c> with the default aperture 3 and <c>L2gradient=False</c>.
    ///
    /// Four stages, all of them OpenCV's:
    ///   1. Sobel 3x3 for gx and gy. NO blur first — <c>cv2.Canny</c> does not blur, and the caller
    ///      is responsible for that if it wants it. (The detector does not: it runs Canny on the
    ///      temporal MEDIAN, which is already about as denoised as a frame gets.)
    ///   2. Magnitude as |gx| + |gy| (the L1 norm), because L2gradient defaults to false.
    ///   3. Non-maximum suppression against the nearer of the four 45-degree sectors.
    ///   4. Hysteresis: seeds are pixels above <paramref name="high"/>; the flood keeps any
    ///      8-neighbour above <paramref name="low"/>. Iterative with an explicit stack, not
    ///      recursive — a long HUD edge on a 4K-sourced frame is thousands of pixels and would
    ///      overflow the call stack.
    /// </summary>
    public static byte[] Canny(byte[] src, int width, int height, int low, int high)
    {
        var mag = new int[width * height];
        var dir = new byte[width * height];   // 0 = horizontal, 1 = 45, 2 = vertical, 3 = 135

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;
                int tl = src[i - width - 1], t = src[i - width], tr = src[i - width + 1];
                int l = src[i - 1], r = src[i + 1];
                int bl = src[i + width - 1], b = src[i + width], br = src[i + width + 1];

                int gx = (tr + 2 * r + br) - (tl + 2 * l + bl);
                int gy = (bl + 2 * b + br) - (tl + 2 * t + tr);

                mag[i] = Math.Abs(gx) + Math.Abs(gy);

                // Sector by gradient angle, the usual four-way split at 22.5 degrees.
                int ax = Math.Abs(gx), ay = Math.Abs(gy);
                if (ay <= ax * 0.4142135623730951) dir[i] = 0;
                else if (ax <= ay * 0.4142135623730951) dir[i] = 2;
                else dir[i] = (byte)((gx > 0) == (gy > 0) ? 1 : 3);
            }
        }

        var keep = new bool[width * height];
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;
                int m = mag[i];
                if (m < low) continue;

                int a, b2;
                switch (dir[i])
                {
                    case 0: a = mag[i - 1]; b2 = mag[i + 1]; break;
                    case 2: a = mag[i - width]; b2 = mag[i + width]; break;
                    case 1: a = mag[i - width + 1]; b2 = mag[i + width - 1]; break;
                    default: a = mag[i - width - 1]; b2 = mag[i + width + 1]; break;
                }

                if (m >= a && m >= b2) keep[i] = true;
            }
        }

        var dst = new byte[width * height];
        var stack = new Stack<int>();

        for (int i = 0; i < dst.Length; i++)
        {
            if (keep[i] && mag[i] >= high && dst[i] == 0)
            {
                dst[i] = 255;
                stack.Push(i);

                while (stack.Count > 0)
                {
                    int p = stack.Pop();
                    int py = p / width, px = p - py * width;

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = py + dy;
                        if (ny < 1 || ny >= height - 1) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = px + dx;
                            if (nx < 1 || nx >= width - 1) continue;

                            int q = ny * width + nx;
                            if (dst[q] != 0 || !keep[q] || mag[q] < low) continue;

                            dst[q] = 255;
                            stack.Push(q);
                        }
                    }
                }
            }
        }

        return dst;
    }

    /// <summary>Sobel gx/gy on an 8-bit image, borders left at zero. Used by the circle hunter,
    /// which needs the gradient DIRECTION to vote along.</summary>
    public static (int[] Gx, int[] Gy) Sobel(byte[] src, int width, int height)
    {
        var gx = new int[width * height];
        var gy = new int[width * height];

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;
                int tl = src[i - width - 1], t = src[i - width], tr = src[i - width + 1];
                int l = src[i - 1], r = src[i + 1];
                int bl = src[i + width - 1], b = src[i + width], br = src[i + width + 1];

                gx[i] = (tr + 2 * r + br) - (tl + 2 * l + bl);
                gy[i] = (bl + 2 * b + br) - (tl + 2 * t + tr);
            }
        }

        return (gx, gy);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────
    // CONNECTED COMPONENTS
    // ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One foreground blob: its bounding box and how many pixels it actually contains.</summary>
    internal readonly record struct Blob(int X, int Y, int Width, int Height, int Area);

    /// <summary>
    /// Stands in for <c>cv2.findContours(mask, RETR_EXTERNAL, CHAIN_APPROX_SIMPLE)</c> followed by
    /// <c>boundingRect</c> and <c>contourArea</c> on each result.
    ///
    /// This is 8-connected connected-component labelling rather than border following, because the
    /// detector never looks at a contour's POINTS — it only ever asks for the bounding rectangle
    /// and the area, and for an outer contour those are exactly a component's bounding box and
    /// pixel count.
    ///
    /// ONE DELIBERATE DEVIATION, AND IT IS THE ONLY ONE: <c>cv2.contourArea</c> returns the area of
    /// the outer POLYGON, so it counts holes as filled; <see cref="Blob.Area"/> counts foreground
    /// pixels, so it does not. The single consumer is <c>fill_ratio = area / (w*h)</c>, one of eight
    /// weighted score terms, and it is weighted 9 out of ~130 in the role scorer. The effect is that
    /// a ring-shaped blob (the minimap's outer bezel, most obviously) scores slightly LOWER on
    /// fill than OpenCV would have given it. Two other terms — the colour-anchor ratio and the
    /// temporal-stability ratio, worth 44 and 16 — are computed over the bounding box and are
    /// unaffected, so ranking is dominated by them either way. Recorded here so the next reader
    /// does not go hunting for a bug that is a documented approximation.
    ///
    /// Iterative flood fill with an explicit stack; see <see cref="Canny"/> for why not recursion.
    /// </summary>
    public static List<Blob> FindBlobs(byte[] mask, int width, int height)
    {
        var blobs = new List<Blob>();
        var seen = new bool[mask.Length];
        var stack = new Stack<int>();

        for (int start = 0; start < mask.Length; start++)
        {
            if (mask[start] == 0 || seen[start]) continue;

            seen[start] = true;
            stack.Push(start);

            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            int area = 0;

            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int py = p / width, px = p - py * width;

                area++;
                if (px < minX) minX = px;
                if (px > maxX) maxX = px;
                if (py < minY) minY = py;
                if (py > maxY) maxY = py;

                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = py + dy;
                    if (ny < 0 || ny >= height) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = px + dx;
                        if (nx < 0 || nx >= width) continue;

                        int q = ny * width + nx;
                        if (seen[q] || mask[q] == 0) continue;

                        seen[q] = true;
                        stack.Push(q);
                    }
                }
            }

            blobs.Add(new Blob(minX, minY, maxX - minX + 1, maxY - minY + 1, area));
        }

        return blobs;
    }

    /// <summary>
    /// Non-zero count inside an axis-aligned window, the equivalent of
    /// <c>cv2.countNonZero(mask[y:y+h, x:x+w])</c>. Clamps to the image the way NumPy slicing does,
    /// and returns the CLAMPED window's size as the denominator so the caller's ratio stays a
    /// ratio even for a window that hangs off the edge.
    /// </summary>
    public static (int NonZero, int Total) CountNonZero(byte[] mask, int width, int height, int x, int y, int w, int h)
    {
        int x0 = Math.Max(0, x);
        int y0 = Math.Max(0, y);
        int x1 = Math.Min(width, x + w);
        int y1 = Math.Min(height, y + h);

        if (x1 <= x0 || y1 <= y0) return (0, 0);

        int count = 0;
        for (int yy = y0; yy < y1; yy++)
        {
            int row = yy * width;
            for (int xx = x0; xx < x1; xx++)
            {
                if (mask[row + xx] != 0) count++;
            }
        }

        return (count, (x1 - x0) * (y1 - y0));
    }
}
