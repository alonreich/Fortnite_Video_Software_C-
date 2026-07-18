using System;
using System.IO;

namespace FortniteVideoSoftware.App;

/// <summary>
/// Support for launching the app via Windows Explorer "Open With" (or any shell
/// file association). Windows starts the exe with the video file path as the first
/// command-line argument. Program.cs detects that pattern, stashes the path here,
/// and MainWindow loads it once the UI is ready.
/// </summary>
public static class OpenWithLaunch
{
    /// <summary>Video file path passed on the command line, pending load by MainWindow.</summary>
    public static string? PendingVideoPath { get; set; }

    /// <summary>True when the argument is an existing file with a supported video extension.</summary>
    public static bool IsVideoFilePath(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return false;
        try
        {
            if (!File.Exists(arg)) return false;
            string ext = Path.GetExtension(arg).ToLowerInvariant();
            return ext is ".mp4" or ".mkv" or ".avi" or ".mov";
        }
        catch
        {
            return false;
        }
    }
}
