using System;
using System.Linq;
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
            ApplyBounds(window, state, key);
        }
        catch { }
    }

    /// <summary>
    /// Full window-state persistence: loads saved bounds at construction, re-applies
    /// the position right after the window opens (the OS/per-monitor DPI handshake can
    /// silently move a freshly shown window — the main cause of "my window keeps
    /// resetting"), and saves continuously (debounced) on every move/resize/state
    /// change so bounds survive crashes and force-kills, not just clean closes.
    /// Positions spanning multiple displays (e.g. 70% on display 1, 30% on display 2)
    /// are preserved as-is: bounds are only reset when the window would be COMPLETELY
    /// off every connected screen (e.g. a monitor was unplugged).
    /// </summary>
    public static void Track(Window window, string key)
    {
        PixelPoint? appliedPosition = null;
        try
        {
            var store = new Core.Ipc.StateTransferStore();
            var state = store.LoadSync();
            ApplyBounds(window, state, key);
            if (window.WindowStartupLocation == WindowStartupLocation.Manual)
            {
                appliedPosition = window.Position;
            }
        }
        catch { }

        bool opened = false;
        Avalonia.Threading.DispatcherTimer? debounce = null;

        void ScheduleSave()
        {
            if (!opened) return;
            if (debounce == null)
            {
                debounce = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                debounce.Tick += (_, _) =>
                {
                    debounce!.Stop();
                    SaveBoundsSync(window, key);
                };
            }
            debounce.Stop();
            debounce.Start();
        }

        window.Opened += (_, _) =>
        {
            try
            {
                // Re-assert the saved position AFTER the window is shown: Windows may
                // have adjusted it during Show() due to per-monitor DPI scaling.
                if (appliedPosition.HasValue && window.WindowState == WindowState.Normal
                    && window.Position != appliedPosition.Value)
                {
                    window.Position = appliedPosition.Value;
                }
            }
            catch { }
            opened = true;
        };

        window.PositionChanged += (_, _) => ScheduleSave();
        window.SizeChanged += (_, _) => ScheduleSave();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty) ScheduleSave();
        };
        window.Closing += (_, _) =>
        {
            try { debounce?.Stop(); } catch { }
            SaveBoundsSync(window, key);
        };
    }

    public static async Task LoadBoundsAsync(Window window, string key)
    {
        try
        {
            var store = new Core.Ipc.StateTransferStore();
            var state = await store.LoadAsync().ConfigureAwait(false);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                ApplyBounds(window, state, key);
            });
        }
        catch { }
    }

    private static void ApplyBounds(Window window, JsonObject state, string key)
    {
        if (state.TryGetPropertyValue(key, out var boundsNode) && boundsNode is JsonObject boundsObj)
        {
            // ISSUE 4: Prevent stutter jumping by enforcing Manual startup location early
            window.WindowStartupLocation = WindowStartupLocation.Manual;

            double savedWidth = window.Width;
            double savedHeight = window.Height;
            bool hasSavedSize = false;

            if (boundsObj.TryGetPropertyValue("Width", out var w) && w != null && boundsObj.TryGetPropertyValue("Height", out var h) && h != null)
            {
                savedWidth = Math.Max(window.MinWidth > 0 ? window.MinWidth : 320, (double)w);
                savedHeight = Math.Max(window.MinHeight > 0 ? window.MinHeight : 240, (double)h);
                hasSavedSize = true;
            }

            if (hasSavedSize)
            {
                window.Width = savedWidth;
                window.Height = savedHeight;
                window.SizeToContent = SizeToContent.Manual;
            }

            if (boundsObj.TryGetPropertyValue("X", out var x) && x != null && boundsObj.TryGetPropertyValue("Y", out var y) && y != null)
            {
                int px = (int)x;
                int py = (int)y;

                // ISSUE 3: Check if visible on ANY screen. Don't aggressively clamp.
                var screens = window.Screens.All;
                double scaling = screens.FirstOrDefault()?.Scaling ?? 1.0;
                int pixelWidth = Math.Max(1, (int)(savedWidth * scaling));
                int pixelHeight = Math.Max(1, (int)(savedHeight * scaling));

                bool isVisible = screens.Any(s => s.Bounds.Intersects(new PixelRect(px, py, pixelWidth, pixelHeight)));

                if (!isVisible)
                {
                    // Window is completely off-screen (e.g. monitor disconnected). Center on primary screen.
                    var primary = window.Screens.Primary ?? screens.FirstOrDefault();
                    if (primary != null)
                    {
                        px = primary.Bounds.X + Math.Max(0, (primary.Bounds.Width - pixelWidth) / 2);
                        py = primary.Bounds.Y + Math.Max(0, (primary.Bounds.Height - pixelHeight) / 2);
                    }
                }

                // Applying position after size helps prevent OS from aggressively snapping straddled windows
                window.Position = new PixelPoint(px, py);
            }

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

    public static async Task SaveBoundsAsync(Window window, string key)
    {
        try
        {
            var store = new Core.Ipc.StateTransferStore();
            var state = await store.LoadAsync().ConfigureAwait(false);
            var updates = new JsonObject { [key] = UpdateBoundsObj(window, state, key) };
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
            var state = store.LoadSync();
            var updates = new JsonObject { [key] = UpdateBoundsObj(window, state, key) };
            store.UpdatePropertiesSync(updates);
        }
        catch { }
    }

    private static JsonObject UpdateBoundsObj(Window window, JsonObject state, string key)
    {
        JsonObject boundsObj = new JsonObject();
        
        // Preserve existing coordinates if currently Maximized/Minimized (ISSUE 2)
        if (state.TryGetPropertyValue(key, out var existingNode) && existingNode is JsonObject existingObj)
        {
            if (existingObj.TryGetPropertyValue("X", out var x)) boundsObj["X"] = x?.DeepClone();
            if (existingObj.TryGetPropertyValue("Y", out var y)) boundsObj["Y"] = y?.DeepClone();
            if (existingObj.TryGetPropertyValue("Width", out var w)) boundsObj["Width"] = w?.DeepClone();
            if (existingObj.TryGetPropertyValue("Height", out var h)) boundsObj["Height"] = h?.DeepClone();
        }

        boundsObj["WindowState"] = (int)window.WindowState;

        if (window.WindowState == WindowState.Normal)
        {
            boundsObj["X"] = window.Position.X;
            boundsObj["Y"] = window.Position.Y;
            // ISSUE 1: Save outer Width/Height instead of inner Bounds, to prevent shrinking loop
            double w = double.IsNaN(window.Width) ? window.Bounds.Width : window.Width;
            double h = double.IsNaN(window.Height) ? window.Bounds.Height : window.Height;
            boundsObj["Width"] = w;
            boundsObj["Height"] = h;
        }

        return boundsObj;
    }
}
