using SkiaSharp;
using System.IO;

namespace FortniteVideoSoftware.Core.Media;

public static class TextOverlayGenerator
{
    public static void GeneratePng(string text, string outputPath, int width = 1080, int height = 150, int fontSize = 40, int padding = 10)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // Calculate background rectangle
        // For simplicity, we just use the bounds based on the text length and font size
        using var font = new SKFont(SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold), fontSize);
        using var paint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true
        };

        var textBounds = new SKRect();
        font.MeasureText(text, out textBounds, paint);
        
        float bgWidth = textBounds.Width + (padding * 2);
        float bgHeight = textBounds.Height + (padding * 2);
        
        float xOffset = (width - bgWidth) / 2f;
        float yOffset = height - bgHeight - padding;

        using var bgPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 180), // Semi-transparent black
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        var rect = new SKRect(xOffset, yOffset, xOffset + bgWidth, yOffset + bgHeight);
        canvas.DrawRoundRect(rect, 8, 8, bgPaint);

        // Draw text
        float textX = xOffset + padding;
        float textY = yOffset + padding - textBounds.Top; // adjust for font baseline
        canvas.DrawText(text, textX, textY, SKTextAlign.Left, font, paint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.OpenWrite(outputPath);
        data.SaveTo(stream);
    }
}
