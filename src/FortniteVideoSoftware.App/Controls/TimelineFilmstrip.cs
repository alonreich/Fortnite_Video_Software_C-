// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md, docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>GRANULARPERF_01 — one drawing surface, regardless of the number of timeline chunks.</summary>
public sealed class TimelineFilmstrip : Control
{
    // The owning window controls bitmap lifetime. A streamed frame only invalidates this visual.
    public Bitmap? Bitmap { get; set; }
    public OutputTimeline? Timeline { get; set; }
    public double SourceDurationSeconds { get; set; }

    public static Rect SourceRect(OutputTimeline.Chunk chunk, PixelSize pixels, double sourceDuration)
    {
        double duration = Math.Max(0.001, sourceDuration);
        double x = Math.Clamp(chunk.SourceStartSec / duration * pixels.Width, 0, Math.Max(0, pixels.Width - 1));
        double width = chunk.IsFreeze || chunk.SourceEndSec <= chunk.SourceStartSec
            ? Math.Min(pixels.Width, pixels.Height * 16.0 / 9.0)
            : (chunk.SourceEndSec - chunk.SourceStartSec) / duration * pixels.Width;
        return new Rect(x, 0, Math.Clamp(width, 1, pixels.Width - x), pixels.Height);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = !double.IsNaN(Width) && Width > 0 ? Width : (!double.IsInfinity(availableSize.Width) ? availableSize.Width : 0);
        double h = !double.IsNaN(Height) && Height > 0 ? Height : (!double.IsInfinity(availableSize.Height) ? availableSize.Height : 60);
        return new Size(w, h);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double w = Bounds.Width > 0 ? Bounds.Width : (!double.IsNaN(Width) ? Width : 0);
        double h = Bounds.Height > 0 ? Bounds.Height : (!double.IsNaN(Height) ? Height : 60);
        if (Bitmap is not { } bitmap || Timeline is not { } timeline || timeline.TotalOutputSeconds <= 0
            || w <= 0 || h <= 0) return;
        using (context.PushClip(new Rect(0, 0, w, h)))
        {
            double output = 0;
            foreach (var chunk in timeline.Chunks)
            {
                double x = output / timeline.TotalOutputSeconds * w;
                double width = chunk.OutputLengthSec / timeline.TotalOutputSeconds * w;
                output += chunk.OutputLengthSec;
                if (width <= 0) continue;
                // Draw only the source slice that is visible. No oversized Image layout boxes or textures.
                context.DrawImage(bitmap, SourceRect(chunk, bitmap.PixelSize, SourceDurationSeconds),
                    new Rect(x, 0, width, h));
            }
        }
    }
}
