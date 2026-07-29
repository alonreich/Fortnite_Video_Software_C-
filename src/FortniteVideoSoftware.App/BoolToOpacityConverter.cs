using System;
using System.Globalization;

namespace FortniteVideoSoftware.App;

/// <summary>
/// Maps a bool to an Opacity so a control can FADE in and out instead of popping.
///
/// Binding `IsVisible` to the same flag would work, but a collapsed element cannot animate —
/// it would blink in and out. Binding Opacity keeps the element in the layout at a stable size
/// (no reflow of its neighbours when it appears) and lets a `DoubleTransition` carry it
/// smoothly. Pair it with `IsHitTestVisible="False"` on decorative readouts so the invisible
/// state cannot swallow clicks.
///
/// Follows the same `.Instance` singleton pattern as <see cref="FileNameConverter"/>.
/// </summary>
public sealed class BoolToOpacityConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly BoolToOpacityConverter Instance = new();

    /// <summary>Optional ConverterParameter overrides the visible opacity (e.g. "0.85").</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double on = 1.0;
        if (parameter is string s &&
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            on = Math.Clamp(parsed, 0.0, 1.0);
        }

        return value is bool b && b ? on : 0.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
