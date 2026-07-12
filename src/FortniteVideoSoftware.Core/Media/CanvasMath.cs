using System.Text.Json.Nodes;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// Canvas math constants and exact backend rounding helpers.
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

    public static JsonArray ProtectCropDrift(string regionName, JsonArray crop)
    {
        if (crop.Count != 4) return crop;

        int width = ReadCropInt(crop, 0);
        int height = ReadCropInt(crop, 1);
        int x = ReadCropInt(crop, 2);
        int y = ReadCropInt(crop, 3);

        string? driftType = HudConfig.CropDriftType(regionName);
        if (driftType == "left")
        {
            x -= 1;
            width += 1;
        }
        else if (driftType == "right")
        {
            width += 1;
        }

        return new JsonArray(Math.Max(1, width), Math.Max(1, height), x, y);
    }

    private static int ReadCropInt(JsonArray crop, int index)
    {
        if (index < 0 || index >= crop.Count)
        {
            return 0;
        }

        try
        {
            return CoordinateMath.ScaleRound(Frac.FromString(crop[index]?.ToString() ?? "0"));
        }
        catch
        {
            return 0;
        }
    }
}
