using System;
using System.Globalization;
using Avalonia.Controls;
using FortniteVideoSoftware.App.Controls;

namespace FortniteVideoSoftware.App;

internal static class SpeedPresetButtons
{
    public const double NativeDefaultSpeed = 1.1;

    public static (string Name, double Speed)[] BuildPresetMap(double baseSpeed)
    {
        return
        [
            ("SpeedPreset01", 0.1),
            ("SpeedPreset05", 0.5),
            ("SpeedPreset10", 1.0),
            ("SpeedPresetBase", ClampSpeed(baseSpeed)),
            ("SpeedPreset15", 1.5),
            ("SpeedPreset20", 2.0),
            ("SpeedPreset25", 2.5),
            ("SpeedPreset30", 3.0),
            ("SpeedPreset35", 3.5),
            ("SpeedPreset40", 4.0),
        ];
    }

    public static void ConfigureBaseButton(Control owner, double baseSpeed, string tooltip)
    {
        Button? button = owner.FindControl<Button>("SpeedPresetBase");
        if (button == null)
        {
            return;
        }

        double speed = ClampSpeed(baseSpeed);
        button.Content = FormatSpeed(speed);
        ToolTip.SetTip(button, tooltip);
    }

    public static void WirePresetButtons(Control owner, double baseSpeed, Action<double> applySpeed)
    {
        foreach (var (name, speed) in BuildPresetMap(baseSpeed))
        {
            Button? button = owner.FindControl<Button>(name);
            if (button == null)
            {
                continue;
            }

            double capturedSpeed = speed;
            ToolTip.SetTip(button, $"Set speed to {FormatSpeed(capturedSpeed)}");
            button.Click += (_, _) => applySpeed(capturedSpeed);
        }
    }

    public static void SetSliderValue(Slider? slider, double speed)
    {
        if (slider == null)
        {
            return;
        }

        slider.Value = Math.Clamp(speed, slider.Minimum, slider.Maximum);
        slider.IsEnabled = true;
    }

    public static void SetSpinningWheelValue(SpinningWheelSlider? slider, double speed)
    {
        if (slider == null)
        {
            return;
        }

        slider.Value = (int)Math.Round(ClampSpeed(speed) * 10.0, MidpointRounding.AwayFromZero);
    }
    public static string FormatSpeed(double speed)
    {
        return speed.ToString("0.0x", CultureInfo.InvariantCulture);
    }

    private static double ClampSpeed(double speed) => Math.Clamp(speed, 0.1, 4.0);
}
