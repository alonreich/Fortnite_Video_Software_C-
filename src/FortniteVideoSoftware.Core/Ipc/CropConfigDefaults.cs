using System.Text.Json.Nodes;

namespace FortniteVideoSoftware.Core.Ipc;

public static class CropConfigDefaults
{
    public const int SchemaVersion = 3; // Bumped from 2 → invalidates all-zero-crop configs
    public const string CoordinateSpace = "content_1080x1620";

    public static readonly string[] RequiredSections =
    [
        "crops_1080p",
        "scales",
        "overlays",
        "z_orders"
    ];

    /// <summary>
    /// Creates the default crop configuration.
    /// IMPORTANT: The crop rectangles (crops_1080p) use [width, height, x, y] format
    /// in content-space (1080x1620) coordinates. These MUST be non-zero, otherwise
    /// MobileFilterBuilder rejects all layers and no HUD overlays (HP, loot, minimap)
    /// are rendered in the portrait output.
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
                // [width, height, x, y] — content-space 1080x1620
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