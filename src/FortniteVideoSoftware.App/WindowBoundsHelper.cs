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

                    WindowSnapshot snapshot;
                    try { snapshot = WindowSnapshot.Capture(window); }
                    catch { return; }

                    _ = Task.Run(() => SaveSnapshot(snapshot, key));
                };
            }
            debounce.Stop();
            debounce.Start();
        }

        window.Opened += (_, _) =>
        {
            try
            {
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

                var screens = window.Screens.All;
                double scaling = screens.FirstOrDefault()?.Scaling ?? 1.0;
                int pixelWidth = Math.Max(1, (int)(savedWidth * scaling));
                int pixelHeight = Math.Max(1, (int)(savedHeight * scaling));

                bool isVisible = screens.Any(s => s.Bounds.Intersects(new PixelRect(px, py, pixelWidth, pixelHeight)));

                if (!isVisible)
                {
                    var primary = window.Screens.Primary ?? screens.FirstOrDefault();
                    if (primary != null)
                    {
                        px = primary.Bounds.X + Math.Max(0, (primary.Bounds.Width - pixelWidth) / 2);
                        py = primary.Bounds.Y + Math.Max(0, (primary.Bounds.Height - pixelHeight) / 2);
                    }
                }

                window.Position = new PixelPoint(px, py);
            }

            if (boundsObj.TryGetPropertyValue("WindowState", out var stateNode) && stateNode != null)
            {
                var savedState = (WindowState)(int)stateNode;
                window.WindowState = savedState == WindowState.Maximized
                    ? WindowState.Maximized
                    : WindowState.Normal;
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
            var timeout = Core.Ipc.StateTransferStore.InteractiveMutexTimeout;
            var store = new Core.Ipc.StateTransferStore();
            var state = store.LoadSync(default, timeout);
            var updates = new JsonObject { [key] = UpdateBoundsObj(window, state, key) };
            store.UpdatePropertiesSync(updates, default, timeout);
        }
        catch { }
    }

    /// <summary>
    /// ISSUE_10 — a plain value copy of everything the persistence layer needs from a Window.
    ///
    /// Window properties may only be touched on the UI thread, so they are captured there and
    /// this snapshot is what crosses onto the background thread that performs the file work. It
    /// also means the values saved are the ones that were true when the debounce fired, not
    /// whatever the window happens to be doing when the write finally reaches disk.
    /// </summary>
    private readonly struct WindowSnapshot
    {
        public readonly int X, Y;
        public readonly double Width, Height;
        public readonly WindowState State;

        private WindowSnapshot(int x, int y, double w, double h, WindowState state)
        {
            X = x; Y = y; Width = w; Height = h; State = state;
        }

        public static WindowSnapshot Capture(Window window)
        {
            double w = double.IsNaN(window.Width) ? window.Bounds.Width : window.Width;
            double h = double.IsNaN(window.Height) ? window.Bounds.Height : window.Height;
            return new WindowSnapshot(window.Position.X, window.Position.Y, w, h, window.WindowState);
        }
    }

    /// <summary>
    /// ISSUE_10 — performs the read-modify-write for a captured snapshot. Safe to call from any
    /// thread because it never touches the Window. Used by the debounced background save.
    /// </summary>
    private static void SaveSnapshot(WindowSnapshot snapshot, string key)
    {
        try
        {
            var store = new Core.Ipc.StateTransferStore();
            var state = store.LoadSync();
            var updates = new JsonObject { [key] = BuildBoundsObj(snapshot, state, key) };
            store.UpdatePropertiesSync(updates);
        }
        catch { }
    }

    /// <summary>
    /// ISSUE_08/ISSUE_10 — the single place that decides what a persisted bounds entry contains,
    /// shared by the background debounce save and the synchronous close-time save.
    /// </summary>
    private static JsonObject BuildBoundsObj(WindowSnapshot snapshot, JsonObject state, string key)
    {
        JsonObject boundsObj = new JsonObject();

        if (state.TryGetPropertyValue(key, out var existingNode) && existingNode is JsonObject existingObj)
        {
            if (existingObj.TryGetPropertyValue("X", out var x)) boundsObj["X"] = x?.DeepClone();
            if (existingObj.TryGetPropertyValue("Y", out var y)) boundsObj["Y"] = y?.DeepClone();
            if (existingObj.TryGetPropertyValue("Width", out var w)) boundsObj["Width"] = w?.DeepClone();
            if (existingObj.TryGetPropertyValue("Height", out var h)) boundsObj["Height"] = h?.DeepClone();
        }

        WindowState persistedState = snapshot.State == WindowState.Minimized
            ? WindowState.Normal
            : snapshot.State;
        boundsObj["WindowState"] = (int)persistedState;

        if (snapshot.State == WindowState.Normal)
        {
            boundsObj["X"] = snapshot.X;
            boundsObj["Y"] = snapshot.Y;
            boundsObj["Width"] = snapshot.Width;
            boundsObj["Height"] = snapshot.Height;
        }

        return boundsObj;
    }

    /// <summary>
    /// UI-thread entry point: snapshots the window and defers to the single shared builder, so
    /// the debounced background save and the close-time save can never diverge.
    /// </summary>
    private static JsonObject UpdateBoundsObj(Window window, JsonObject state, string key)
    {
        return BuildBoundsObj(WindowSnapshot.Capture(window), state, key);
    }
}
