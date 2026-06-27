using System.Text.Json.Nodes;
using System.Threading;
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
            var state = await store.LoadAsync().ConfigureAwait(false);
            if (state.TryGetPropertyValue(key, out var boundsNode) && boundsNode is JsonObject boundsObj)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
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
                        
                        window.WindowStartupLocation = WindowStartupLocation.Manual;
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
                });
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
            await store.UpdatePropertiesAsync(updates).ConfigureAwait(false);
        }
        catch { }
    }

    /// <summary>
    /// Synchronous save for use in Closing/Closed handlers.
    /// CRITICAL: Must NOT use Task.Run + GetAwaiter().GetResult() because the
    /// async continuation captures the UI SynchronizationContext, causing a
    /// classic async-over-sync deadlock when the UI thread is blocked.
    /// Instead, we perform the file I/O directly on the calling thread.
    /// </summary>
    public static void SaveBoundsSync(Window window, string key)
    {
        try
        {
            var store = new Core.Ipc.StateTransferStore();
            var updates = new JsonObject { [key] = GetBoundsObj(window) };
            store.UpdatePropertiesSync(updates);
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