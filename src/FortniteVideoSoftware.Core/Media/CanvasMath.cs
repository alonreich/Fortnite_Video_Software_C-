using FortniteVideoSoftware.Core.Ipc;
using System.Text.Json.Nodes;

namespace FortniteVideoSoftware.Core.Media;

public static class CanvasMath
{
    public const int FinalWidth = 1080;
    public const int FinalHeight = 1920;
    
    public const int ContentWidth = 1080;
    public const int ContentHeight = 1620;
    public const int ContentOffsetY = 150;
    
    public const int BackendWidth = 1280;
    public const int BackendHeight = 1920;

    public static double BackendScale => (double)BackendWidth / ContentWidth;

    public static JsonArray ProtectCropDrift(string regionName, JsonArray crop)
    {
        if (crop.Count != 4) return crop;
        
        int width = crop[0]!.GetValue<int>();
        int height = crop[1]!.GetValue<int>();
        int x = crop[2]!.GetValue<int>();
        int y = crop[3]!.GetValue<int>();

        if (regionName is "stats" or "normal_hp" or "boss_hp" or "team" or "spectating")
        {
            // +1px on LEFT edge
            x += 1;
            width -= 1;
        }
        else if (regionName == "loot")
        {
            // +1px on RIGHT edge
            width -= 1;
        }

        return new JsonArray(width, height, x, y);
    }
}
