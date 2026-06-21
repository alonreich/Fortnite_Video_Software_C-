using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;

namespace FortniteVideoSoftware.App;

public static class WindowBoundsHelper
{
    public static async Task LoadBoundsAsync(Window window, string key)
    {
        try
        {
            var store = new Core.Ipc.StateTransferStore();
            var state = await store.LoadAsync();
            if (state.TryGetPropertyValue(key, out var boundsNode) && boundsNode is JsonObject boundsObj)
            {
                if (boundsObj.TryGetPropertyValue("X", out var x) && x != null && boundsObj.TryGetPropertyValue("Y", out var y) && y != null)
                {
                    int px = (int)x;
                    int py = (int)y;
                    
                    bool intersectsAny = false;
                    foreach (var screen in window.Screens.All)
                    {
                        if (screen.Bounds.Contains(new PixelPoint(px, py)))
                        {
                            intersectsAny = true;
                            break;
                        }
                    }
                    
                    if (!intersectsAny && window.Screens.Primary != null)
                    {
                        px = window.Screens.Primary.Bounds.X + 50;
                        py = window.Screens.Primary.Bounds.Y + 50;
                    }
                    
                    window.Position = new PixelPoint(px, py);
                }
                if (boundsObj.TryGetPropertyValue("Width", out var w) && w != null && boundsObj.TryGetPropertyValue("Height", out var h) && h != null)
                {
                    window.Width = (double)w;
                    window.Height = (double)h;
                }
                if (boundsObj.TryGetPropertyValue("WindowState", out var stateNode) && stateNode != null)
                {
                    window.WindowState = (WindowState)(int)stateNode;
                }
            }
        }
        catch { }
    }

    public static async Task SaveBoundsAsync(Window window, string key)
    {
        try
        {
            var store = new Core.Ipc.StateTransferStore();
            var updates = new JsonObject { [key] = GetBoundsObj(window) };
            await store.UpdatePropertiesAsync(updates);
        }
        catch { }
    }

    public static void SaveBoundsSync(Window window, string key)
    {
        try
        {
            var store = new Core.Ipc.StateTransferStore();
            var updates = new JsonObject { [key] = GetBoundsObj(window) };
            store.UpdatePropertiesAsync(updates).GetAwaiter().GetResult();
        }
        catch { }
    }

    private static JsonObject GetBoundsObj(Window window)
    {
        return new JsonObject
        {
            ["X"] = window.Position.X,
            ["Y"] = window.Position.Y,
            ["Width"] = window.Bounds.Width,
            ["Height"] = window.Bounds.Height,
            ["WindowState"] = (int)window.WindowState
        };
    }
}
