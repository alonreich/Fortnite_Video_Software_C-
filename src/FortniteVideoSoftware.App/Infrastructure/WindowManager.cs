using Avalonia.Controls;
using System;
using System.Linq;
using Avalonia.Platform;
using System.Text.Json.Nodes;
using System.IO;

namespace FortniteVideoSoftware.App.Infrastructure
{
    public static class WindowManager
    {
        private static readonly object _lock = new();
        private static System.Collections.Generic.List<Window> _activeWindows = new();

        private static string GetStateFilePath(string windowName)
        {
            var appData = FortniteVideoSoftware.Core.Infrastructure.ApplicationPaths.CreateDefault().ProgramDataRoot;
            return Path.Combine(appData, $"{windowName}_state.json");
        }

        public static void SaveWindowState(Window window)
        {
            try
            {
                var windowName = window.GetType().Name;
                var stateFile = GetStateFilePath(windowName);

                if (window.Bounds.Width <= 0 || window.Bounds.Height <= 0) return;

                var state = new JsonObject();
                if (File.Exists(stateFile))
                {
                    try
                    {
                        var json = File.ReadAllText(stateFile);
                        state = System.Text.Json.JsonSerializer.Deserialize<JsonObject>(json) ?? new JsonObject();
                    }
                    catch { }
                }

                state["WindowState"] = (int)window.WindowState;

                if (window.WindowState == WindowState.Normal)
                {
                    state["Width"] = window.Bounds.Width;
                    state["Height"] = window.Bounds.Height;
                    state["X"] = window.Position.X;
                    state["Y"] = window.Position.Y;
                }
                
                Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
                string tempFile = stateFile + ".tmp";
                File.WriteAllText(tempFile, state.ToJsonString());
                File.Move(tempFile, stateFile, overwrite: true);
            }
            catch { }
        }

        public static void SaveAll()
        {
            List<Window> toSave;
            lock (_lock)
            {
                toSave = _activeWindows.ToList();
            }
            foreach (var w in toSave)
            {
                SaveWindowState(w);
            }
        }

        public static void RegisterWindow(Window window)
        {
            lock (_lock)
            {
                if (!_activeWindows.Contains(window)) _activeWindows.Add(window);
            }

            var windowName = window.GetType().Name;
            var stateFile = GetStateFilePath(windowName);

            try
            {
                if (File.Exists(stateFile))
                {
                    var json = File.ReadAllText(stateFile);
                    var state = System.Text.Json.JsonSerializer.Deserialize<JsonObject>(json);
                    if (state != null)
                    {
                        int windowStateInt = state["WindowState"]?.GetValue<int>() ?? (int)WindowState.Normal;

                        bool hasSavedSize = state["Width"] != null && state["Height"] != null;
                        double w = state["Width"]?.GetValue<double>() ?? window.Width;
                        double h = state["Height"]?.GetValue<double>() ?? window.Height;
                        if (double.IsNaN(w) || double.IsInfinity(w) || w <= 0) w = window.MinWidth > 0 ? window.MinWidth : 320;
                        if (double.IsNaN(h) || double.IsInfinity(h) || h <= 0) h = window.MinHeight > 0 ? window.MinHeight : 240;
                        w = Math.Max(window.MinWidth > 0 ? window.MinWidth : 320, w);
                        h = Math.Max(window.MinHeight > 0 ? window.MinHeight : 240, h);

                        int x = state["X"]?.GetValue<int>() ?? int.MinValue;
                        int y = state["Y"]?.GetValue<int>() ?? int.MinValue;

                        if (x != int.MinValue && y != int.MinValue && window.Screens != null && window.Screens.ScreenCount > 0)
                        {
                            var screens = window.Screens.All.ToList();
                            int rectWidth = Math.Max(1, (int)Math.Ceiling(w));
                            int rectHeight = Math.Max(1, (int)Math.Ceiling(h));
                            var savedRect = new Avalonia.PixelRect(x, y, rectWidth, rectHeight);
                            var targetScreen = screens.FirstOrDefault(screen => screen.Bounds.Intersects(savedRect));

                            if (targetScreen == null)
                            {
                                targetScreen = window.Screens.Primary ?? screens.FirstOrDefault();
                                if (targetScreen != null)
                                {
                                    x = targetScreen.Bounds.X + 50;
                                    y = targetScreen.Bounds.Y + 50;
                                }
                            }

                            if (targetScreen != null)
                            {
                                w = Math.Min(w, targetScreen.Bounds.Width);
                                h = Math.Min(h, targetScreen.Bounds.Height);
                                rectWidth = Math.Max(1, (int)Math.Ceiling(w));
                                rectHeight = Math.Max(1, (int)Math.Ceiling(h));
                                x = Math.Max(targetScreen.Bounds.X, Math.Min(x, targetScreen.Bounds.X + Math.Max(0, targetScreen.Bounds.Width - rectWidth)));
                                y = Math.Max(targetScreen.Bounds.Y, Math.Min(y, targetScreen.Bounds.Y + Math.Max(0, targetScreen.Bounds.Height - rectHeight)));
                                window.Width = w;
                                window.Height = h;
                                window.Position = new Avalonia.PixelPoint(x, y);
                                window.WindowStartupLocation = WindowStartupLocation.Manual;
                            }
                            else
                            {
                                window.Width = w;
                                window.Height = h;
                                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                            }
                        }
                        else
                        {
                            window.Width = w;
                            window.Height = h;
                            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                        }

                        if (windowStateInt == (int)WindowState.Maximized)
                        {
                            window.WindowState = WindowState.Maximized;
                        }
                    }
                }
                else
                {
                    window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                }
            }
            catch
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            window.Closing += (s, e) =>
            {
                SaveWindowState(window);
                lock (_lock)
                {
                    _activeWindows.Remove(window);
                }
            };
        }
    }
}
