
using System.Text.Json.Nodes;
using System.Collections.Generic;
using FortniteVideoSoftware.Core.Ipc;

namespace FortniteVideoSoftware.Core.Media;

public static class HudConfig
{
    public static readonly string[] RequiredSections = ["crops_1080p", "scales", "overlays", "z_orders"];
    public static readonly string[] HudKeys = ["loot", "stats", "normal_hp", "boss_hp", "team", "spectating"];

    public const string HudCoordinateSpace = "content_1080x1620";
    public const int HudSchemaVersion = CropConfigDefaults.SchemaVersion;

    public static readonly Dictionary<string, int> ZDefaults = new()
    {
        ["loot"] = 10,
        ["normal_hp"] = 20,
        ["boss_hp"] = 20,
        ["stats"] = 30,
        ["team"] = 40,
        ["spectating"] = 100,
    };


    public static JsonObject CreateDefault()
    {
        return CropConfigDefaults.Create();
    }

    /// <summary>
    /// Returns the drift correction type: "left" for stats/map/hp/team/spec, "right" for loot.
    /// Enforces +1px Left bias for Map, Stats, and Health, and +1px Right bias for Loot.
    /// </summary>
    public static string? CropDriftType(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (key is "stats" or "normal_hp" or "boss_hp" or "team" or "spectating" or "map" or "minimap")
            return "left";
        if (key == "loot")
            return "right";

        string lower = key.ToLowerInvariant();
        if (lower.Contains("map") || lower.Contains("stats") || lower.Contains("hp") || lower.Contains("health") || lower.Contains("team") || lower.Contains("spectat"))
            return "left";
        if (lower.Contains("loot"))
            return "right";

        return null;
    }

    private static int ToInt(JsonNode? value, int defaultValue = 0)
    {
        if (value is null) return defaultValue;
        try { return CoordinateMath.ScaleRound(Frac.FromString(value.ToString())); }
        catch { return defaultValue; }
    }

    private static Frac ToScale(JsonNode? value)
    {
        var fallback = Frac.One;
        if (value is null) return fallback;
        try
        {
            if (value.AsValue().TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
                return Frac.FromString(s);
            if (value.AsValue().TryGetValue(out double d))
                return Frac.FromDouble(d);
            return Frac.FromString(value.ToString());
        }
        catch { return fallback; }
    }

    private static int ReadArrayInt(JsonArray arr, int index, int defaultValue = 0)
    {
        if (index < 0 || index >= arr.Count)
        {
            return defaultValue;
        }

        return ToInt(arr[index], defaultValue);
    }

    private static bool IsCurrentSpace(JsonObject config)
    {
        string? space = config["coordinate_space"]?.ToString();
        int version = (int)(config["schema_version"]?.GetValue<long>() ?? 0);
        return space == HudCoordinateSpace && version >= HudSchemaVersion;
    }

    /// <summary>
    /// Exact port of sanitize_hud_config. Deep-clones, validates, and fixes all sections.
    /// Migrates legacy coordinates if not in current space.
    /// </summary>
    public static JsonObject Sanitize(JsonObject? config, bool migrateLegacy = true)
    {
        config ??= new JsonObject();
        JsonObject clean = config.DeepClone().AsObject();
        bool currentSpace = IsCurrentSpace(clean);

        foreach (string section in RequiredSections)
        {
            if (clean[section] is not JsonObject)
                clean[section] = new JsonObject();
        }

        var keys = new HashSet<string>(HudKeys);
        foreach (string section in RequiredSections)
        {
            if (clean[section] is JsonObject sectionObj)
            {
                foreach (var kvp in sectionObj)
                    keys.Add(kvp.Key);
            }
        }

        clean["schema_version"] = HudSchemaVersion;
        clean["coordinate_space"] = HudCoordinateSpace;

        var cropsObj = clean["crops_1080p"]!.AsObject();
        var scalesObj = clean["scales"]!.AsObject();
        var defaults = CreateDefault();
        var defaultCrops = defaults["crops_1080p"]!.AsObject();
        var defaultScales = defaults["scales"]!.AsObject();

        foreach (string key in keys)
        {
            var rectNode = cropsObj[key];
            int[] rect;
            if (rectNode is JsonArray arr && arr.Count >= 4)
            {
                rect = [
                    ReadArrayInt(arr, 0),
                    ReadArrayInt(arr, 1),
                    ReadArrayInt(arr, 2),
                    ReadArrayInt(arr, 3)
                ];
            }
            else if (defaultCrops[key] is JsonArray defArr && defArr.Count >= 4)
            {
                rect = [
                    ReadArrayInt(defArr, 0),
                    ReadArrayInt(defArr, 1),
                    ReadArrayInt(defArr, 2),
                    ReadArrayInt(defArr, 3)
                ];
            }
            else
            {
                rect = [0, 0, 0, 0];
            }

            int w = ToInt(JsonValue.Create(rect[0]), 0);
            int h = ToInt(JsonValue.Create(rect[1]), 0);
            int x = ToInt(JsonValue.Create(rect[2]), 0);
            int y = ToInt(JsonValue.Create(rect[3]), 0);

            if (migrateLegacy && !currentSpace && h > 0)
            {
                y -= CoordinateConstants.UIPaddingTop;
            }

            var clamped = CoordinateMath.ClampContentCrop((w, h, x, y));
            cropsObj[key] = new JsonArray(clamped.w, clamped.h, clamped.x, clamped.y);

            Frac scale = ToScale(scalesObj[key] ?? defaultScales[key] ?? JsonValue.Create("1/1"));
            scalesObj[key] = scale.ToString();
        }

        var overlaysObj = clean["overlays"]!.AsObject();
        var zOrdersObj = clean["z_orders"]!.AsObject();
        var defaultOverlays = defaults["overlays"]!.AsObject();

        foreach (string key in keys)
        {
            var overlayNode = overlaysObj[key];
            int ox, oy;
            if (overlayNode is JsonObject ov)
            {
                ox = ToInt(ov["x"] ?? JsonValue.Create(0), 0);
                oy = ToInt(ov["y"] ?? JsonValue.Create(CoordinateConstants.UIPaddingTop), CoordinateConstants.UIPaddingTop);
            }
            else if (defaultOverlays[key] is JsonObject defOv)
            {
                ox = ToInt(defOv["x"] ?? JsonValue.Create(0), 0);
                oy = ToInt(defOv["y"] ?? JsonValue.Create(CoordinateConstants.UIPaddingTop), CoordinateConstants.UIPaddingTop);
            }
            else
            {
                ox = 0;
                oy = CoordinateConstants.UIPaddingTop;
            }

            var crop = cropsObj[key] as JsonArray;
            int cropW = crop != null ? ReadArrayInt(crop, 0) : 0;
            int cropH = crop != null ? ReadArrayInt(crop, 1) : 0;
            Frac scaleVal = ToScale(scalesObj[key]);
            var (width, height) = CoordinateMath.QuantizeBackendSize(cropW, cropH, scaleVal);

            var (cx, cy) = CoordinateMath.ClampOverlayPosition(ox, oy, width, height);
            overlaysObj[key] = new JsonObject { ["x"] = cx, ["y"] = cy };

            int zDef = ZDefaults.GetValueOrDefault(key, 10);
            zOrdersObj[key] = ToInt(zOrdersObj[key] ?? JsonValue.Create(zDef), zDef);
        }

        return clean;
    }

    /// <summary>
    /// Validates config and returns list of issues. Exact port of validate_hud_config().
    /// </summary>
    public static List<string> Validate(JsonObject config)
    {
        var issues = new List<string>();

        if (!IsCurrentSpace(config))
            issues.Add("HUD coordinate schema requires migration");

        foreach (string section in RequiredSections)
        {
            if (config[section] is not JsonObject)
                issues.Add($"Invalid section: {section}");
        }

        JsonObject sanitized = Sanitize(config);
        var crops = sanitized["crops_1080p"]?.AsObject();
        if (crops == null || crops.Count == 0)
            issues.Add("Missing crop data");

        foreach (var kvp in crops ?? [])
        {
            if (kvp.Value is not JsonArray rect || rect.Count < 4)
            {
                issues.Add($"Invalid crop data for '{kvp.Key}'");
                continue;
            }
            int w = ReadArrayInt(rect, 0);
            int h = ReadArrayInt(rect, 1);
            if (w <= 0 || h <= 0 || h > CoordinateConstants.ContentH)
                issues.Add($"Invalid crop dimensions for '{kvp.Key}'");
        }

        return issues;
    }
}
