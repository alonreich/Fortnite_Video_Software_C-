using System.Text.Json.Nodes;

namespace FortniteVideoSoftware.Core.Ipc;

public static class CropConfigDefaults
{
    public const int SchemaVersion = 2;
    public const string CoordinateSpace = "content_1080x1620";

    public static readonly string[] RequiredSections =
    [
        "crops_1080p",
        "scales",
        "overlays",
        "z_orders"
    ];

    public static JsonObject Create()
    {
        return new JsonObject
        {
            ["schema_version"] = SchemaVersion,
            ["coordinate_space"] = CoordinateSpace,
            ["crops_1080p"] = new JsonObject
            {
                ["loot"] = Rect(0, 0, 0, 0),
                ["stats"] = Rect(0, 0, 0, 0),
                ["normal_hp"] = Rect(0, 0, 0, 0),
                ["boss_hp"] = Rect(0, 0, 0, 0),
                ["team"] = Rect(0, 0, 0, 0),
                ["spectating"] = Rect(0, 0, 0, 0)
            },
            ["scales"] = new JsonObject
            {
                ["loot"] = 1.0,
                ["stats"] = 1.0,
                ["team"] = 1.0,
                ["normal_hp"] = 1.0,
                ["boss_hp"] = 1.0,
                ["spectating"] = 1.0
            },
            ["overlays"] = new JsonObject
            {
                ["loot"] = Point(680, 1370),
                ["stats"] = Point(730, 150),
                ["team"] = Point(30, 250),
                ["normal_hp"] = Point(30, 1620),
                ["boss_hp"] = Point(30, 1620),
                ["spectating"] = Point(30, 1300)
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
