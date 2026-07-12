
using System.Text.Json;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

public class VideoConfig
{
    public int DefaultMainWidth = 1280;
    public int DefaultMainHeight = 1920;
    public int MobileMainWidth = 1080;
    public int MobileMainHeight = 1920;
    public double FadeDuration = 1.0;
    public int WrapAtPx = 1100;
    public int SafeMaxPx = 1200;
    public int BaseFontSize = 110;
    public int MinFontSize = 36;
    public int LineSpacing = -10;
    public double MeasureFudge = 1.12;
    public int ShadowPadPx = 5;

    /// <summary>
    /// Returns (keepHighestRes, targetMB, qualityLevel).
    /// Exact port of get_quality_settings().
    /// </summary>
    public (bool keepHighestRes, double? targetMB, int qualityLevel) GetQualitySettings(int qualityLevel, double? targetMbOverride = null)
    {
        int q = qualityLevel;
        bool keepHighestRes;
        double? targetMB;

        if (q >= 20)
        {
            keepHighestRes = true;
            targetMB = targetMbOverride;
        }
        else
        {
            keepHighestRes = false;
            targetMB = targetMbOverride ?? (5 + q * 5);
        }

        return (keepHighestRes, targetMB, q);
    }

    /// <summary>
    /// Load and validate mobile coordinates from crops_coordinations.conf.
    /// Uses self-healing: if missing/corrupt, falls back to defaults → backup rotation.
    /// </summary>
    public static async Task<JsonObject> GetMobileCoordinatesAsync(ApplicationPaths? paths = null)
    {
        paths ??= ApplicationPaths.CreateDefault();
        var store = new Ipc.CropConfigStore(paths);
        return HudConfig.Sanitize(await store.LoadAsync(), migrateLegacy: false);
    }
}
