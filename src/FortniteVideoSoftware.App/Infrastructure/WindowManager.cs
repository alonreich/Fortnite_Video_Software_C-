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

                // If window is minimized or closed, Bounds might be zero
                if (window.Bounds.Width <= 0 || window.Bounds.Height <= 0) return;

                var state = new JsonObject
                {
                    ["Width"] = window.Bounds.Width,
                    ["Height"] = window.Bounds.Height,
                    ["X"] = window.Position.X,
                    ["Y"] = window.Position.Y
                };
                
                Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
                File.WriteAllText(stateFile, state.ToJsonString());
            }
            catch { }
        }

        public static void SaveAll()
        {
            foreach (var w in _activeWindows.ToList())
            {
                SaveWindowState(w);
            }
        }

        public static void RegisterWindow(Window window)
        {
            if (!_activeWindows.Contains(window)) _activeWindows.Add(window);

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
                        double w = state["Width"]?.GetValue<double>() ?? window.Width;
                        double h = state["Height"]?.GetValue<double>() ?? window.Height;
                        int x = state["X"]?.GetValue<int>() ?? int.MinValue;
                        int y = state["Y"]?.GetValue<int>() ?? int.MinValue;

                        if (w > 0) window.Width = w;
                        if (h > 0) window.Height = h;

                        if (x != int.MinValue && y != int.MinValue)
                        {
                            var p = new Avalonia.PixelPoint(x, y);
                            bool isOnScreen = false;

                            if (window.Screens != null && window.Screens.ScreenCount > 0)
                            {
                                foreach (var screen in window.Screens.All)
                                {
                                    var inflatedBounds = new Avalonia.PixelRect(
                                        screen.Bounds.X - 50, 
                                        screen.Bounds.Y - 50, 
                                        screen.Bounds.Width + 100, 
                                        screen.Bounds.Height + 100);
                                    if (inflatedBounds.Contains(p))
                                    {
                                        isOnScreen = true;
                                        break;
                                    }
                                }
                            }
                            else
                            {
                                // If screens are not initialized yet, assume it's on screen and restore it anyway
                                isOnScreen = true;
                            }

                            if (isOnScreen)
                            {
                                window.Position = p;
                                window.WindowStartupLocation = WindowStartupLocation.Manual;
                            }
                            else
                            {
                                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                            }
                        }
                        else
                        {
                            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
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
                _activeWindows.Remove(window);
            };
        }
    }
}
