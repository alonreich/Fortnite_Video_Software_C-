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
            // Spawn completely non-transparent (opaque / blocking) at the initial point.
            popupBorder.Opacity = 1;
            if (placementTarget != null)
            {
                popup.PlacementTarget = placementTarget;
            }

            double videoHeight = placementTarget?.Bounds.Height ?? 600;
            // Entire animation shifted 50px upward from the previous baseline
            // (was: startOffset = -80, endOffset = -(videoHeight / 3.0)).
            double startOffset = -130;
            double endOffset = -(videoHeight / 3.0) - 50;

            popup.IsOpen = false;
            popup.VerticalOffset = startOffset;
            popup.IsOpen = true;

            // Hold completely opaque for the first 0.3 seconds at the initial point.
            await Task.Delay(300);

            // Then gradually become more transparent while floating upwards until it vanishes.
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