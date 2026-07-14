using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;

namespace FortniteVideoSoftware.App;

public static class WindowBoundsHelper
{
    public static void LoadBoundsSync(Window window, string key)
    {
        try
        {
            var store = new Core.Ipc.StateTransferStore();
            var state = store.LoadSync();
            if (state.TryGetPropertyValue(key, out var boundsNode) && boundsNode is JsonObject boundsObj)
            {
                double savedWidth = window.Width;
                double savedHeight = window.Height;
                bool hasSavedSize = false;
                if (boundsObj.TryGetPropertyValue("Width", out var w) && w != null && boundsObj.TryGetPropertyValue("Height", out var h) && h != null)
                {
                    savedWidth = Math.Max(window.MinWidth > 0 ? window.MinWidth : 320, (double)w);
                    savedHeight = Math.Max(window.MinHeight > 0 ? window.MinHeight : 240, (double)h);
                    hasSavedSize = true;
                }

                if (boundsObj.TryGetPropertyValue("X", out var x) && x != null && boundsObj.TryGetPropertyValue("Y", out var y) && y != null)
                {
                    int px = (int)x;
                    int py = (int)y;

                    var screens = window.Screens.All.ToList();
                    var targetScreen = screens.FirstOrDefault(screen =>
                        screen.Bounds.Intersects(new PixelRect(px, py, Math.Max(1, (int)savedWidth), Math.Max(1, (int)savedHeight))));

                    if (targetScreen == null)
                    {
                        targetScreen = window.Screens.Primary ?? screens.FirstOrDefault();
                        if (targetScreen != null)
                        {
                            px = targetScreen.Bounds.X + 50;
                            py = targetScreen.Bounds.Y + 50;
                        }
                    }

                    if (targetScreen != null)
                    {
                        savedWidth = Math.Min(savedWidth, targetScreen.Bounds.Width);
                        savedHeight = Math.Min(savedHeight, targetScreen.Bounds.Height);
                        px = Math.Max(targetScreen.Bounds.X, Math.Min(px, targetScreen.Bounds.X + Math.Max(0, targetScreen.Bounds.Width - (int)savedWidth)));
                        py = Math.Max(targetScreen.Bounds.Y, Math.Min(py, targetScreen.Bounds.Y + Math.Max(0, targetScreen.Bounds.Height - (int)savedHeight)));
                    }

                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Position = new PixelPoint(px, py);
                }
                window.Width = savedWidth;
                window.Height = savedHeight;
                if (hasSavedSize) window.SizeToContent = SizeToContent.Manual;
                if (boundsObj.TryGetPropertyValue("WindowState", out var stateNode) && stateNode != null)
                {
                    window.WindowState = (WindowState)(int)stateNode;
                }
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }
        catch { }
    }

    public static async Task LoadBoundsAsync(Window window, string key)
    {
        try
        {
            var store = new Core.Ipc.StateTransferStore();
            var state = await store.LoadAsync().ConfigureAwait(false);
            if (state.TryGetPropertyValue(key, out var boundsNode) && boundsNode is JsonObject boundsObj)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    double savedWidth = window.Width;
                    double savedHeight = window.Height;
                    bool hasSavedSize = false;
                    if (boundsObj.TryGetPropertyValue("Width", out var w) && w != null && boundsObj.TryGetPropertyValue("Height", out var h) && h != null)
                    {
                        savedWidth = Math.Max(window.MinWidth > 0 ? window.MinWidth : 320, (double)w);
                        savedHeight = Math.Max(window.MinHeight > 0 ? window.MinHeight : 240, (double)h);
                        hasSavedSize = true;
                    }

                    if (boundsObj.TryGetPropertyValue("X", out var x) && x != null && boundsObj.TryGetPropertyValue("Y", out var y) && y != null)
                    {
                        int px = (int)x;
                        int py = (int)y;

                        var screens = window.Screens.All.ToList();
                        var targetScreen = screens.FirstOrDefault(screen =>
                            screen.Bounds.Intersects(new PixelRect(px, py, Math.Max(1, (int)savedWidth), Math.Max(1, (int)savedHeight))));

                        if (targetScreen == null)
                        {
                            targetScreen = window.Screens.Primary ?? screens.FirstOrDefault();
                            if (targetScreen != null)
                            {
                                px = targetScreen.Bounds.X + 50;
                                py = targetScreen.Bounds.Y + 50;
                            }
                        }

                        if (targetScreen != null)
                        {
                            savedWidth = Math.Min(savedWidth, targetScreen.Bounds.Width);
                            savedHeight = Math.Min(savedHeight, targetScreen.Bounds.Height);
                            px = Math.Max(targetScreen.Bounds.X, Math.Min(px, targetScreen.Bounds.X + Math.Max(0, targetScreen.Bounds.Width - (int)savedWidth)));
                            py = Math.Max(targetScreen.Bounds.Y, Math.Min(py, targetScreen.Bounds.Y + Math.Max(0, targetScreen.Bounds.Height - (int)savedHeight)));
                        }

                        window.WindowStartupLocation = WindowStartupLocation.Manual;
                        window.Position = new PixelPoint(px, py);
                    }
                    window.Width = savedWidth;
                    window.Height = savedHeight;
                    if (hasSavedSize) window.SizeToContent = SizeToContent.Manual;
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
