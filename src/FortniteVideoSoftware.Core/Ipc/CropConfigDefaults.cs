using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace FortniteVideoSoftware.Core.Ipc;

public static class CropConfigDefaults
{
    /// <summary>
    /// v4 (IDEA_1) added the optional "crops_source" section — see <see cref="SourceCropsSection"/>.
    /// It is purely additive: a v3 file loads unchanged and the source rects are derived once.
    /// </summary>
    public const int SchemaVersion = 4;

    /// <summary>
    /// The OLDEST schema this build can still read and upgrade in place.
    ///
    /// CRITICAL — <see cref="CropConfigStore.IsUsableConfig"/> used to compare against
    /// <see cref="SchemaVersion"/> itself, meaning any config written by an older build was
    /// declared unusable and REPLACED with factory defaults. Bumping the version to 4 for IDEA_1
    /// would therefore have destroyed every user's saved masks on first launch, and every future
    /// bump would do the same.
    ///
    /// 3 is the first version that stored crops in content space, so anything from 3 upward can be
    /// read as-is and upgraded by HudConfig.Sanitize. Only raise this if a change genuinely makes
    /// older data unreadable — and if you do, migrate rather than discard.
    /// </summary>
    public const int MinimumUsableSchemaVersion = 3;

    public const string CoordinateSpace = "content_1080x1620";

    /// <summary>
    /// NOMASK_01 — the RESERVED, HUD-FREE profile name.
    ///
    /// A profile by this name means "portrait conversion only": the Portrait Canvas Trick, the
    /// 150 / 1620 / 150 layout and the optional canvas-text strip, with ZERO HUD layers composited.
    /// It is produced by <see cref="CreateNoMask"/> and is read-only — MaskOverlayManager refuses
    /// to overwrite it and the Crop Tools app refuses to edit it — because the instant a layer is
    /// added to it, it stops being what its name promises.
    ///
    /// ⚠️ IT MUST STORE EXPLICIT ZERO RECTS, NOT AN EMPTY "crops_1080p".
    /// HudConfig.Sanitize builds its key set from HudKeys UNION the keys present in the file, and
    /// for any key whose rect is MISSING it falls back to CropConfigDefaults.Create()'s rect. An
    /// empty crops section would therefore be silently repopulated with the full Fortnite HUD on
    /// the next ApplyProfile. A present-but-zero rect is read as-is and is the documented way to
    /// express "this layer is switched off" (MobileFilterBuilder.RegisterLayer requires w >= 1 and
    /// h >= 1). DO NOT "simplify" CreateNoMask to an empty object.
    /// </summary>
    public const string NoMaskProfileName = "No Mask Profile";

    /// <summary>
    /// IDEA_1 — the AUTHORITATIVE crop rectangles, in SOURCE-video pixels, `[width, height, x, y]`
    /// to match "crops_1080p".
    ///
    /// WHY THIS EXISTS. "crops_1080p" is content-space, and converting content -> source and back
    /// again is LOSSY: CoordinateMath.TransformToContentAreaInt and
    /// InverseTransformFromContentAreaInt both round strictly OUTWARD on purpose (the export crop
    /// must COVER the selection or HUD pixels get clipped). Composing them therefore grows the rect
    /// by up to 2px per axis. That was harmless while saved layers could not be reopened for
    /// editing — nothing iterated the conversion. The moment the Crop Tool rehydrates a saved layer
    /// into an editable box, every open/save cycle would compound that growth.
    ///
    /// Storing the source rect removes the round trip entirely: editing reads these numbers
    /// directly and never converts backwards. "crops_1080p" stays the single input the exporter
    /// reads, so nothing downstream changes.
    ///
    /// MISSING IS LEGAL. A v3 file has no such section; the Crop Tool derives it once via the
    /// inverse transform (a single outward snap, exactly what happens today) and writes it back.
    /// </summary>
    public const string SourceCropsSection = "crops_source";

    public static readonly string[] RequiredSections =
    [
        "crops_1080p",
        "scales",
        "overlays",
        "z_orders"
    ];

    /// <summary>
    /// Creates the default crop configuration.
    ///
    /// The crop rectangles (crops_1080p) use [width, height, x, y] format in content-space
    /// (1080x1620) coordinates. A layer is rendered only when width and height are both >= 1:
    /// MobileFilterBuilder's RegisterLayer skips anything smaller, and the Crop Tools ghost
    /// renderer skips w &lt;= 1 || h &lt;= 1. A zero-size rect is therefore the canonical way to
    /// express "this layer is switched off" — see the deletion handling in
    /// CropToolWindow.SaveConfig.
    ///
    /// Negative X is normal and load-bearing, not a bug. Content X = 0 corresponds to source
    /// X ~= 599.6 for a 1920x1080 input, because the 1280-wide internal window is a centre crop of
    /// the 3414-wide scaled frame (see CoordinateMath.ScalePlan). HUD elements near the left edge
    /// of the source therefore land at negative content X, which is why
    /// CoordinateMath.ClampContentCrop deliberately permits a wide negative range.
    ///
    /// ISSUE_6: this comment previously listed "minimap" among the overlays these defaults render.
    /// It does not — the six keys below are the complete shipped set, and they match
    /// HudConfig.HudKeys exactly. HudConfig.CropDriftType still recognises "map"/"minimap" because
    /// the Crop Tool lets the user name a custom element freely and HudConfig.Sanitize admits
    /// arbitrary keys, so a user-created minimap layer does receive the +1px LEFT bias. No default
    /// minimap rectangle is shipped, and none has been invented here.
    ///
    /// Values ported from the original Python crops_coordinations.conf.
    /// </summary>
    public static JsonObject Create()
    {
        return new JsonObject
        {
            ["schema_version"] = SchemaVersion,
            ["coordinate_space"] = CoordinateSpace,
            ["crops_1080p"] = new JsonObject
            {
                ["loot"] = Rect(511, 103, 1420, 1462),
                ["stats"] = Rect(326, 233, 1620, 30),
                ["normal_hp"] = Rect(465, 71, -839, 1470),
                ["boss_hp"] = Rect(450, 150, 30, 1320),
                ["team"] = Rect(270, 181, -881, 1256),
                ["spectating"] = Rect(54, 22, -842, 1555)
            },
            ["scales"] = new JsonObject
            {
                ["loot"] = 1.0227,
                ["stats"] = 1.2694,
                ["team"] = 1.1253,
                ["normal_hp"] = 1.1107,
                ["boss_hp"] = 1.0,
                ["spectating"] = 1.2059
            },
            ["overlays"] = new JsonObject
            {
                ["loot"] = Point(539, 1406),
                ["stats"] = Point(666, 150),
                ["team"] = Point(0, 150),
                ["normal_hp"] = Point(9, 1419),
                ["boss_hp"] = Point(30, 1620),
                ["spectating"] = Point(18, 1524)
            },
            ["z_orders"] = new JsonObject
            {
                ["loot"] = 10,
                ["normal_hp"] = 20,
                ["boss_hp"] = 20,
                ["stats"] = 30,
                ["team"] = 40,
                ["spectating"] = 100
            }
        };
    }

    /// <summary>
    /// NOMASK_01 — the reserved "No Mask Profile" document: the exact same schema, scales,
    /// overlay positions and z-orders as <see cref="Create"/>, with EVERY crop rectangle zeroed.
    ///
    /// Derived from Create() on purpose so the six HudKeys are guaranteed present (see the
    /// remarks on <see cref="NoMaskProfileName"/> for why a missing key is not the same as a zero
    /// key). The scales/overlays/z_orders values are left intact and are simply never read: with
    /// no active layer, MobileFilterBuilder takes its zero-layer branch and composites nothing.
    ///
    /// "crops_source" is dropped — it only exists so the Crop Tool can rehydrate a saved layer for
    /// editing, and there is nothing here to rehydrate.
    /// </summary>
    public static JsonObject CreateNoMask()
    {
        var doc = Create();
        var crops = doc["crops_1080p"]!.AsObject();
        foreach (string key in new List<string>(crops.Select(kvp => kvp.Key)))
        {
            crops[key] = Rect(0, 0, 0, 0);
        }
        doc.Remove(SourceCropsSection);
        return doc;
    }

    /// <summary>
    /// NOMASK_01 — true when <paramref name="config"/> is a well-formed HUD-free document: every
    /// key Create() ships is PRESENT (so Sanitize cannot fall back to a default rect) and no rect
    /// anywhere in the section has both width and height >= 1 (so no layer can be composited).
    ///
    /// Used to self-heal the reserved profile in MaskOverlayManager.EnsureDefaults — a file that
    /// has drifted is rewritten rather than trusted.
    /// </summary>
    public static bool IsHudFree(JsonObject? config)
    {
        var crops = config?["crops_1080p"]?.AsObject();
        if (crops == null) return false;

        var required = Create()["crops_1080p"]!.AsObject();
        foreach (var kvp in required)
        {
            if (crops[kvp.Key] is not JsonArray) return false;
        }

        foreach (var kvp in crops)
        {
            if (kvp.Value is not JsonArray arr || arr.Count < 4) continue;
            if (int.TryParse(arr[0]?.ToString(), out int w)
                && int.TryParse(arr[1]?.ToString(), out int h)
                && w >= 1 && h >= 1)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Creates a rect array in [width, height, x, y] format matching the Python config.
    /// </summary>
    private static JsonArray Rect(int width, int height, int x, int y)
    {
        return JsonNode.Parse($"[{width},{height},{x},{y}]")!.AsArray();
    }

    private static JsonObject Point(int x, int y)
    {
        return new JsonObject
        {
            ["x"] = x,
            ["y"] = y
        };
    }
}