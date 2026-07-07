using FortniteVideoSoftware.Core.Ipc;
using System.Text.Json.Nodes;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// Canvas math constants and drift protection.
/// Mirrors exact constants from coordinate_math.py and filter_mobile.py.
/// </summary>
public static class CanvasMath
{
    public const int FinalWidth = CoordinateConstants.PortraitW;
    public const int FinalHeight = CoordinateConstants.PortraitH;
    
    public const int ContentWidth = CoordinateConstants.ContentW;
    public const int ContentHeight = CoordinateConstants.ContentH;
    public const int ContentOffsetY = CoordinateConstants.PaddingTop;
    
    public const int BackendWidth = CoordinateConstants.InternalW;
    public const int BackendHeight = CoordinateConstants.InternalH;

    public static Frac BackendScale => CoordinateConstants.BackendScale;
    public static double BackendScaleDouble => (double)CoordinateConstants.InternalW / CoordinateConstants.PortraitW;

    /// <summary>
    /// Even-ceiling helper for Frac, used by mobile filter scaling.
    /// Matches Python's _even_ceil(_fraction(value)).
    /// </summary>
    public static int EvenCeil(Frac value)
    {
        int n = CoordinateMath.FracCeil(value);
        return n % 2 == 0 ? n : n + 1;
    }

    /// <summary>
    /// Apply +1px drift protection to a crop rect in content coordinates.
    /// Left-edge regions: stats, hp, team, spectating → +1px LEFT (x += 1, w -= 1)
    /// Right-edge region: loot → +1px RIGHT (w -= 1)
    /// This protects thin HUD borders from clipping during rounding transforms.
    /// </summary>
    public static JsonArray ProtectCropDrift(string regionName, JsonArray crop)
    {
        if (crop.Count != 4) return crop;
        
        int width = crop[0]!.GetValue<int>();
        int height = crop[1]!.GetValue<int>();
        int x = crop[2]!.GetValue<int>();
        int y = crop[3]!.GetValue<int>();

        string? driftType = HudConfig.CropDriftType(regionName);
        if (driftType == "left")
        {
            x += 1;
            width -= 1;
        }
        else if (driftType == "right")
        {
            width -= 1;
        }

        return new JsonArray(width, height, x, y);
    }
}
