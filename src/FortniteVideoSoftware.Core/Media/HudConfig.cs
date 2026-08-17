
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

    /// <summary>
    /// IDEA_1 — optional source-pixel crop rectangles. NOT in <see cref="RequiredSections"/>: the
    /// exporter never reads it, only the Crop Tool editor does. <see cref="Sanitize"/> deep-clones
    /// the document, so the section survives untouched; the loop at the end only prunes entries
    /// whose matching content crop has been cleared, so a deleted layer cannot leave an orphan.
    /// </summary>
    public const string SourceCropsSection = CropConfigDefaults.SourceCropsSection;

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
    ///
    /// ISSUE_6 — scope note. "map"/"minimap" are matched here but are NOT in
    /// <see cref="HudKeys"/> and have no entry in CropConfigDefaults, so no shipped profile
    /// exercises that branch. It is still live and correct for user-created layers: the Crop Tool
    /// accepts a free-text element name, <see cref="Sanitize"/> builds its key set from
    /// <see cref="HudKeys"/> UNION every key present in the config, and the substring fallback
    /// below catches names such as "minimap_left". Keep both the exact and the substring match —
    /// removing them would silently drop the +1px LEFT bias from any custom map layer.
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
        int version = ReadSchemaVersion(config);
        return space == HudCoordinateSpace && version >= HudSchemaVersion;
    }

    /// <summary>
    /// CROPCHK_01 — READ THE VERSION WITHOUT CARING WHICH NUMERIC TYPE IT WAS STORED AS.
    ///
    /// This used to be <c>(int)(config["schema_version"]?.GetValue&lt;long&gt;() ?? 0)</c>, and it
    /// threw on EVERY export: "A value of type 'System.Int32' cannot be converted to a
    /// 'System.Int64'". <see cref="System.Text.Json"/> will not widen an Int32 node to Int64 —
    /// <c>GetValue&lt;T&gt;</c> demands the exact stored type — and the config is written with an
    /// Int32 schema_version (the log line "CROP CONFIG READY - schema=4" is that value).
    ///
    /// ⚠️ THE DAMAGE WAS SILENT AND TOTAL. <c>IsCurrentSpace</c> is reached from
    /// <c>HudConfig.Validate</c>, whose only caller wraps it in a try/catch that logs at INFO and
    /// carries on (VideoConfig.GetMobileCoordinatesAsync). So the crop-configuration check — added
    /// specifically because a nonsense crop config used to reach export unnoticed — has never once
    /// run. Every export logged "Crop configuration check could not run" and nobody read it.
    ///
    /// Parsed off the raw text instead, so Int32, Int64 and a quoted "4" all work and a malformed
    /// value degrades to 0 (treated as out of date) rather than throwing.
    ///
    /// ⚠️ DO NOT reintroduce a typed GetValue&lt;T&gt; on a JSON number that other code writes.
    /// The identical trap was already fixed once in HardwareCapability (GetValue&lt;bool&gt;).
    /// </summary>
    private static int ReadSchemaVersion(JsonObject config)
    {
        var node = config["schema_version"];
        if (node == null) return 0;
        return int.TryParse(node.ToString(), System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;
    }

    /// <summary>
    /// Whether the crop Y values are already content-relative.
    ///
    /// CRITICAL — this deliberately ignores schema_version and looks ONLY at coordinate_space.
    /// The one-time `y -= 150` migration converts canvas-space Y (0 at the top of the 1920 canvas)
    /// into content-space Y (0 at the top of the 1620 content area). That is a property of the
    /// COORDINATE SPACE, not of the schema number.
    ///
    /// Sanitize used to gate that subtraction on <see cref="IsCurrentSpace"/>, which also requires
    /// `version >= HudSchemaVersion`. Bumping the schema to 4 for IDEA_1's crops_source section
    /// would therefore have made every existing v3 file look un-migrated and subtracted 150px from
    /// every crop a SECOND time — silently shifting every saved mask up by 150 pixels on first
    /// load. Any future schema bump would have done the same. Keying on the space string alone
    /// makes the migration idempotent no matter how many times the version changes.
    /// </summary>
    private static bool IsContentSpace(JsonObject config)
        => config["coordinate_space"]?.ToString() == HudCoordinateSpace;

    /// <summary>
    /// Exact port of sanitize_hud_config. Deep-clones, validates, and fixes all sections.
    /// Migrates legacy coordinates if not in current space.
    /// </summary>
    public static JsonObject Sanitize(JsonObject? config, bool migrateLegacy = true)
    {
        config ??= new JsonObject();
        JsonObject clean = config.DeepClone().AsObject();
        // See IsContentSpace: this must NOT depend on schema_version, or a version bump re-runs the
        // one-time -150px Y migration and shifts every saved mask.
        bool currentSpace = IsContentSpace(clean);

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

        // IDEA_1: keep the optional source-crop section honest. An entry whose content crop has
        // been cleared to 0x0 (the way CropToolWindow marks a deleted layer) must not keep a live
        // source rect behind it, or reopening the editor would resurrect the deleted box.
        if (clean[SourceCropsSection] is JsonObject sourceCrops)
        {
            foreach (string key in new List<string>(sourceCrops.Select(kvp => kvp.Key)))
            {
                var contentRect = cropsObj[key] as JsonArray;
                bool contentAlive = contentRect != null
                                    && contentRect.Count >= 4
                                    && ReadArrayInt(contentRect, 0) >= 1
                                    && ReadArrayInt(contentRect, 1) >= 1;

                if (!contentAlive) sourceCrops.Remove(key);
            }
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
