// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Text.Json.Nodes;
using System.Collections.Generic;
using FortniteVideoSoftware.Core.Ipc;

namespace FortniteVideoSoftware.Core.Media;

public static class HudConfig
{
    public static readonly string[] RequiredSections = ["crops_1080p", "scales", "overlays", "z_orders"];
    public static readonly string[] HudKeys = ["loot", "stats", "normal_hp", "team", "spectating"];

    // NO_BOSS_HP_01 — old documents may contain this key, but it must never become a custom layer.
    public static bool IsRetiredRole(string key)
        => string.Equals(key, "boss_hp", StringComparison.OrdinalIgnoreCase);

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
        if (key is "stats" or "normal_hp" or "team" or "spectating" or "map" or "minimap")
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

    /// <summary>
    /// ZEROSCALE_01 — a scale that is not strictly positive is corruption, never intent.
    ///
    /// The fallback was declared but only ever returned for a THROWN parse. A value that parsed
    /// cleanly to zero or to a negative — "0", "0/1", "-1/2", a JSON 0, or anything Frac.FromString
    /// resolves to zero rather than rejecting — was returned as-is. Sanitize then fed it to
    /// QuantizeBackendSize, whose Math.Max(factor, ...) floor turned the element into a 32x32
    /// backend sliver (27x27 in content space), and wrote that back to the file. The element did
    /// not disappear, which would at least have been legible: it became a permanent postage stamp
    /// that no amount of resizing in the editor could explain.
    ///
    /// "Switched off" is expressed by a zero CROP RECT, never by a zero scale — see NOMASK_01 in
    /// Validate below — so nothing is lost by refusing one here.
    /// </summary>
    private static Frac ToScale(JsonNode? value)
    {
        var fallback = Frac.One;
        if (value is null) return fallback;

        Frac parsed;
        try
        {
            if (value.AsValue().TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
                parsed = Frac.FromString(s);
            else if (value.AsValue().TryGetValue(out double d))
                parsed = Frac.FromDouble(d);
            else
                parsed = Frac.FromString(value.ToString());
        }
        catch { return fallback; }

        return parsed > Frac.Zero ? parsed : fallback;
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
        bool currentSpace = IsContentSpace(clean);

        foreach (string section in RequiredSections.Append(SourceCropsSection))
        {
            if (clean[section] is not JsonObject entries) continue;
            foreach (string key in entries.Select(kvp => kvp.Key).Where(IsRetiredRole).ToList())
                entries.Remove(key);
        }

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

            // NOMASK_01 — A FULLY ZERO RECT IS "LAYER SWITCHED OFF", NOT A FAULT.
            // This is the documented representation: CropConfigDefaults.Create's remarks say a
            // zero-size rect is the canonical way to express a disabled layer, CropToolWindow
            // .SaveConfig writes [0,0,0,0] when the user deletes one, MobileFilterBuilder
            // .RegisterLayer skips anything with w < 1 || h < 1, and the Crop Tools ghost renderer
            // skips w <= 1 || h <= 1. Such a rect can never reach the filter graph.
            // Flagging it was wrong for EVERY profile — deleting a layer produced a bogus
            // "Invalid crop dimensions" issue for it on every export — and the reserved
            // "No Mask Profile" is six of them by design.
            // A PARTIALLY zero rect (one axis only) is still a real fault and still flagged.
            if (w == 0 && h == 0) continue;

            if (w <= 0 || h <= 0 || h > CoordinateConstants.ContentH)
                issues.Add($"Invalid crop dimensions for '{kvp.Key}'");
        }

        // ─────────────────────────────────────────────────────────────────────────────────────
        // CONFIGVAL_01 — the four sections are ONE document, and only one of them was checked.
        //
        // Everything above inspects crops_1080p and nothing else. A file could name a layer in
        // "scales" that "crops_1080p" has never heard of, carry a scale of "0" or "-1/2", hold an
        // overlay with no x, or list the same element twice under two capitalisations — and
        // Validate reported the document clean. The fault then surfaced as a wrongly placed or
        // postage-stamp-sized layer in the finished video, with nothing in the log pointing at the
        // config file.
        //
        // These checks read the RAW document, not `sanitized`. Sanitize exists precisely to paper
        // over this class of damage — it unions the key sets across all four sections, coerces
        // every scale to a usable Frac and every overlay to an {x,y} pair — so validating its
        // output would report every file as clean by construction. Validate's job is to tell the
        // user what is wrong with the file they have; Sanitize's job is to keep the export running
        // anyway. They must not be asked the same question.
        // ─────────────────────────────────────────────────────────────────────────────────────
        var rawCrops = config["crops_1080p"] as JsonObject;
        var rawScales = config["scales"] as JsonObject;
        var rawOverlays = config["overlays"] as JsonObject;
        var rawZOrders = config["z_orders"] as JsonObject;

        var cropKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rawCrops != null)
        {
            foreach (var kvp in rawCrops) cropKeys.Add(kvp.Key);
        }

        if (rawScales != null)
        {
            foreach (var kvp in rawScales)
            {
                if (rawCrops != null && !cropKeys.Contains(kvp.Key))
                    issues.Add($"Scale for '{kvp.Key}' has no matching crop entry");

                JsonNode? scaleNode = kvp.Value;
                if (scaleNode is null)
                {
                    issues.Add($"Missing scale for '{kvp.Key}'");
                    continue;
                }

                // ZEROSCALE_01 — a non-positive scale does not remove the layer, it collapses it to
                // a 32x32 backend sliver (the Math.Max floor in QuantizeBackendSize), which reads as
                // a rendering bug rather than as bad data. "Switched off" is a zero CROP RECT.
                Frac parsedScale;
                try { parsedScale = ParseScaleStrict(scaleNode); }
                catch { issues.Add($"Unreadable scale for '{kvp.Key}'"); continue; }

                if (parsedScale <= Frac.Zero)
                    issues.Add($"Invalid scale for '{kvp.Key}' (must be greater than zero)");
            }
        }

        if (rawOverlays != null)
        {
            foreach (var kvp in rawOverlays)
            {
                if (rawCrops != null && !cropKeys.Contains(kvp.Key))
                    issues.Add($"Overlay position for '{kvp.Key}' has no matching crop entry");

                if (kvp.Value is not JsonObject ovObj)
                {
                    issues.Add($"Invalid overlay position for '{kvp.Key}'");
                    continue;
                }

                if (ovObj["x"] is null || ovObj["y"] is null)
                    issues.Add($"Overlay position for '{kvp.Key}' is missing x or y");
            }
        }

        if (rawZOrders != null)
        {
            foreach (var kvp in rawZOrders)
            {
                if (rawCrops != null && !cropKeys.Contains(kvp.Key))
                    issues.Add($"Z order for '{kvp.Key}' has no matching crop entry");

                JsonNode? zNode = kvp.Value;
                if (zNode is null)
                {
                    issues.Add($"Missing z order for '{kvp.Key}'");
                    continue;
                }

                if (!int.TryParse(zNode.ToString(), System.Globalization.NumberStyles.Integer,
                                  System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    issues.Add($"Z order for '{kvp.Key}' is not a whole number");
                }
            }
        }

        // The same fault seen from the other side: a layer in crops_1080p with no companion entry
        // is exported with a default scale, position or z that the user never chose.
        foreach (string key in cropKeys)
        {
            if (rawScales != null && !HasKeyIgnoreCase(rawScales, key))
                issues.Add($"Missing scale for '{key}'");

            if (rawOverlays != null && !HasKeyIgnoreCase(rawOverlays, key))
                issues.Add($"Missing overlay position for '{key}'");

            if (rawZOrders != null && !HasKeyIgnoreCase(rawZOrders, key))
                issues.Add($"Missing z order for '{key}'");
        }

        // KEYCASE_01 — JsonObject indexes ordinally while every element-key comparison in the Crop
        // Tool editor is OrdinalIgnoreCase, so "Loot" and "loot" are two entries to the file and one
        // element to the user. Whichever the reader reaches first wins, and the other is edited
        // forever without effect.
        foreach (string section in RequiredSections)
        {
            if (config[section] is not JsonObject sectionObj) continue;

            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in sectionObj)
            {
                if (seen.TryGetValue(kvp.Key, out string? first))
                    issues.Add($"Duplicate key in '{section}': '{first}' and '{kvp.Key}' are the same element");
                else
                    seen[kvp.Key] = kvp.Key;
            }
        }

        // IDEA_1 — crops_source is optional, but a malformed entry is still a fault: the Crop Tool
        // reads it in preference to crops_1080p when rehydrating an element for editing, so a bad
        // rect here is what the user is handed to edit.
        if (config[SourceCropsSection] is JsonObject sourceSection)
        {
            foreach (var kvp in sourceSection)
            {
                if (rawCrops != null && !cropKeys.Contains(kvp.Key))
                {
                    issues.Add($"Source crop for '{kvp.Key}' has no matching crop entry");
                    continue;
                }

                if (kvp.Value is not JsonArray srcRect || srcRect.Count < 4)
                {
                    issues.Add($"Invalid source crop data for '{kvp.Key}'");
                    continue;
                }

                int sw = ReadArrayInt(srcRect, 0);
                int sh = ReadArrayInt(srcRect, 1);
                int sx = ReadArrayInt(srcRect, 2);
                int sy = ReadArrayInt(srcRect, 3);

                // A zero rect here mirrors the "layer switched off" crop and is not a fault.
                if (sw == 0 && sh == 0 && sx == 0 && sy == 0) continue;

                if (sw <= 0 || sh <= 0)
                    issues.Add($"Invalid source crop dimensions for '{kvp.Key}'");

                if (sx < 0 || sy < 0)
                    issues.Add($"Negative source crop origin for '{kvp.Key}'");
            }
        }

        return issues;
    }

    /// <summary>
    /// CONFIGVAL_01 — parses a scale WITHOUT ToScale's fallback, so Validate can tell "this value is
    /// bad" from "this value is fine". ToScale deliberately swallows both cases into 1/1 because its
    /// caller (Sanitize) has to produce something renderable; Validate has to produce the truth.
    /// </summary>
    private static Frac ParseScaleStrict(JsonNode value)
    {
        if (value.AsValue().TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
            return Frac.FromString(text);

        if (value.AsValue().TryGetValue(out double d))
            return Frac.FromDouble(d);

        return Frac.FromString(value.ToString());
    }

    /// <summary>CONFIGVAL_01 — JsonObject.ContainsKey is ordinal; element keys are not.</summary>
    private static bool HasKeyIgnoreCase(JsonObject section, string key)
    {
        if (section.ContainsKey(key)) return true;

        foreach (var kvp in section)
        {
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
