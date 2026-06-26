using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;

namespace FortniteVideoSoftware.App;

internal static class FloatingFeedback
{
    public static void Show(Popup? popup, Border? popupBorder, TextBlock? popupText, Control? placementTarget, string text)
    {
        if (popup == null || popupBorder == null || popupText == null)
        {
            return;
        }

        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            popupText.Text = text;
            popupBorder.Opacity = 0;
            if (placementTarget != null)
            {
                popup.PlacementTarget = placementTarget;
            }

            double videoHeight = placementTarget?.Bounds.Height ?? 600;
            double startOffset = -80;
            double endOffset = -(videoHeight / 3.0);

            popup.IsOpen = false;
            popup.VerticalOffset = startOffset;
            popup.IsOpen = true;

            double fadeIn = 0;
            while (fadeIn < 1.0)
            {
                fadeIn += 0.12;
                popupBorder.Opacity = fadeIn;
                await Task.Delay(16);
            }

            popupBorder.Opacity = 1;
            await Task.Delay(400);

            double progress = 0;
            while (progress < 1.0)
            {
                progress += 0.025;
                double eased = 1.0 - Math.Pow(1.0 - progress, 2.0);
                popupBorder.Opacity = 1.0 - progress;
                popup.VerticalOffset = startOffset + (endOffset - startOffset) * eased;
                await Task.Delay(16);
            }

            popupBorder.Opacity = 0;
            popup.VerticalOffset = startOffset;
            popup.IsOpen = false;
        });
    }
}
