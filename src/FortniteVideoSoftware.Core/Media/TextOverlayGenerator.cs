using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;
using System.IO;

namespace FortniteVideoSoftware.Core.Media;

public static class TextOverlayGenerator
{
    /// <summary>
    /// Renders the portrait caption to a transparent PNG.
    ///
    /// TEXTGEN_01: the defaults are BOUND to the portrait canvas, not typed as literals. This
    /// PNG is composited by <c>MobileFilterBuilder</c> with <c>overlay=0:0</c> onto the final
    /// portrait frame, so <paramref name="width"/> must equal the canvas width and
    /// <paramref name="height"/> must equal the top padding band the caption lives in. Writing
    /// 1080 and 150 here duplicated <see cref="CoordinateConstants.PortraitW"/> and
    /// <see cref="CoordinateConstants.UIPaddingTop"/>; changing either constant would then have
    /// silently misaligned the caption instead of failing.
    ///
    /// A <c>fontSize</c> parameter used to sit in this signature and was never read — the size
    /// is chosen by the auto-fit loop below, which starts at 110 and descends until the text
    /// fits BOTH the given width and height. It has been removed rather than left implying a
    /// control the caller never had.
    /// </summary>
    public static void GeneratePng(string text, string outputPath,
                                   int width = CoordinateConstants.PortraitW,
                                   int height = CoordinateConstants.UIPaddingTop,
                                   int padding = 10)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        if (string.IsNullOrWhiteSpace(text))
        {
            using var emptyImage = surface.Snapshot();
            using var emptyData = emptyImage.Encode(SKEncodedImageFormat.Png, 100);
            using var emptyStream = File.OpenWrite(outputPath);
            emptyData.SaveTo(emptyStream);
            return;
        }

        using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
        using var paint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            Typeface = typeface
        };

        int currentFontSize = 110;
        int minFontSize = 14;
        List<string> finalLines = new();

        while (currentFontSize >= minFontSize)
        {
            paint.TextSize = currentFontSize;
            finalLines = WrapText(text, paint, width - (padding * 4));
            
            float totalHeight = finalLines.Count * (currentFontSize * 1.2f);
            if (totalHeight <= height - (padding * 2))
            {
                break;
            }
            currentFontSize -= 4;
        }

        paint.TextSize = currentFontSize;

        bool hasHebrew = text.Any(c => c >= 0x0590 && c <= 0x05FF);

        float lineHeight = currentFontSize * 1.2f;
        float totalTextHeight = finalLines.Count * lineHeight;
        float bgHeight = totalTextHeight + (padding * 2);

        float maxLineWidth = 0;
        List<string> displayLines = new();
        foreach (var line in finalLines)
        {
            string displayLine = hasHebrew ? ReorderRtl(line) : line;
            displayLines.Add(displayLine);
            
            var bounds = new SKRect();
            paint.MeasureText(displayLine, ref bounds);
            if (bounds.Width > maxLineWidth) maxLineWidth = bounds.Width;
        }

        float bgWidth = maxLineWidth + (padding * 4);
        float xOffset = (width - bgWidth) / 2f;
        
        float yOffset = height - bgHeight - padding;
        if (yOffset < 0) yOffset = 0; 

        using var bgPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 180),
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        var rect = new SKRect(xOffset, yOffset, xOffset + bgWidth, yOffset + bgHeight);
        canvas.DrawRoundRect(rect, 12, 12, bgPaint);

        float textY = yOffset + padding + currentFontSize;

        foreach (var displayLine in displayLines)
        {
            var bounds = new SKRect();
            paint.MeasureText(displayLine, ref bounds);
            
            float textX = xOffset + (bgWidth - bounds.Width) / 2f;
            
            canvas.DrawText(displayLine, textX, textY, paint);
            textY += lineHeight;
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.OpenWrite(outputPath);
        data.SaveTo(stream);
    }

    private static List<string> WrapText(string text, SKPaint paint, float maxWidth)
    {
        var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var currentLine = "";

        foreach (var word in words)
        {
            var testLine = string.IsNullOrEmpty(currentLine) ? word : currentLine + " " + word;
            var bounds = new SKRect();
            paint.MeasureText(testLine, ref bounds);

            if (bounds.Width > maxWidth && !string.IsNullOrEmpty(currentLine))
            {
                lines.Add(currentLine);
                currentLine = word;
            }
            else
            {
                currentLine = testLine;
            }
        }
        if (!string.IsNullOrEmpty(currentLine))
        {
            lines.Add(currentLine);
        }
        return lines;
    }

    private static string ReorderRtl(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;
        
        var tokens = Regex.Split(input, @"([\s]+)");
        var result = new StringBuilder();
        var ltrBuffer = new List<string>();
        
        for (int i = tokens.Length - 1; i >= 0; i--)
        {
            string token = tokens[i];
            bool isHebrewWord = token.Any(c => c >= 0x0590 && c <= 0x05FF);
            
            if (isHebrewWord)
            {
                if (ltrBuffer.Count > 0)
                {
                    ltrBuffer.Reverse();
                    result.Append(string.Join("", ltrBuffer));
                    ltrBuffer.Clear();
                }

                char[] charArray = token.ToCharArray();
                Array.Reverse(charArray);
                result.Append(new string(charArray));
            }
            else
            {
                ltrBuffer.Add(token);
            }
        }

        if (ltrBuffer.Count > 0)
        {
            ltrBuffer.Reverse();
            result.Append(string.Join("", ltrBuffer));
        }

        return result.ToString();
    }
}
