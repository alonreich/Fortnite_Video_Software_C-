using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace FortniteVideoSoftware.Core.Infrastructure;

public interface IShellFolderResolver
{
    string? GetDownloads();
    string? GetVideos();
    string? GetDesktop();
    string? GetPublicDesktop();
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public partial class WindowsShellFolderResolver : IShellFolderResolver
{
    private static readonly Guid DownloadsGuid = new("374DE290-123F-4565-9164-39C4925E467B");
    private static readonly Guid VideosGuid = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");
    private static readonly Guid DesktopGuid = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    private static readonly Guid PublicDesktopGuid = new("C4AA340D-F20F-4863-AFEF-F87EF2E6BA25");

    [LibraryImport("shell32.dll", SetLastError = false)]
    private static partial int SHGetKnownFolderPath(
        in Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);

    private const uint KfFlagDontVerify = 0x00004000;

    public string? GetDownloads() => Resolve(DownloadsGuid);
    public string? GetVideos() => Resolve(VideosGuid);
    public string? GetDesktop() => Resolve(DesktopGuid);
    public string? GetPublicDesktop() => Resolve(PublicDesktopGuid);

    private static string[] LegacyShellFolderNames(Guid folderId)
    {
        if (folderId == DesktopGuid) return ["Desktop"];
        if (folderId == PublicDesktopGuid) return ["Common Desktop"];
        if (folderId == VideosGuid) return ["My Video"];
        if (folderId == DownloadsGuid) return ["{374DE290-123F-4565-9164-39C4925E467B}"];
        return [];
    }

    private static bool IsMachineFolder(Guid folderId) => folderId == PublicDesktopGuid;

    private static string? Resolve(Guid folderId)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int hr = SHGetKnownFolderPath(in folderId, KfFlagDontVerify, IntPtr.Zero, out buffer);
            if (hr >= 0 && buffer != IntPtr.Zero)
            {
                string? path = Marshal.PtrToStringUni(buffer);
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    return path;
                }
            }
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("KnownFolders", $"SHGetKnownFolderPath failed for {folderId}: {ex.Message}");
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeCoTaskMem(buffer);
        }

        try
        {
            string? fromRegistry = ReadUserShellFolder(folderId);
            if (!string.IsNullOrWhiteSpace(fromRegistry))
            {
                string expanded = Environment.ExpandEnvironmentVariables(fromRegistry!);
                if (Directory.Exists(expanded)) return expanded;
            }
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("KnownFolders", $"Registry fallback expansion failed for {folderId}: {ex.Message}");
        }

        return null;
    }

    private static string? ReadUserShellFolder(Guid folderId)
    {
        var valueNames = new List<string> { "{" + folderId.ToString().ToUpperInvariant() + "}" };
        foreach (string legacy in LegacyShellFolderNames(folderId))
        {
            if (!valueNames.Contains(legacy, StringComparer.OrdinalIgnoreCase))
            {
                valueNames.Add(legacy);
            }
        }

        var root = IsMachineFolder(folderId)
            ? Microsoft.Win32.Registry.LocalMachine
            : Microsoft.Win32.Registry.CurrentUser;

        foreach (string subKey in new[]
                 {
                     @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
                     @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders"
                 })
        {
            try
            {
                using var key = root.OpenSubKey(subKey);
                if (key is null) continue;

                foreach (string valueName in valueNames)
                {
                    if (key.GetValue(valueName) is string s && !string.IsNullOrWhiteSpace(s))
                    {
                        return s;
                    }
                }
            }
            catch (System.Security.SecurityException ex)
            {
                CoreLogger.Debug("KnownFolders", $"SecurityException reading registry key {subKey}: {ex.Message}");
            }
            catch (Exception ex)
            {
                CoreLogger.Debug("KnownFolders", $"Failed reading registry key {subKey}: {ex.Message}");
            }
        }

        return null;
    }
}

public class FallbackShellFolderResolver : IShellFolderResolver
{
    public string? GetDownloads()
    {
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home)) return Path.Combine(home, "Downloads");
        }
        catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
        return null;
    }

    public string? GetVideos() => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    public string? GetDesktop() => Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    public string? GetPublicDesktop() => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
}

public static class KnownFolders
{
    private static readonly IShellFolderResolver _resolver = OperatingSystem.IsWindows()
        ? new WindowsShellFolderResolver()
        : new FallbackShellFolderResolver();

    public static string? GetDownloads() => _resolver.GetDownloads();
    public static string? GetVideos() => _resolver.GetVideos();
    public static string? GetDesktop() => _resolver.GetDesktop();
    public static string? GetPublicDesktop() => _resolver.GetPublicDesktop();

    public static bool IsWritableDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;

        try
        {
            if (!Directory.Exists(directory)) return false;

            string probe = Path.Combine(directory, ".fvs_write_probe_" + Guid.NewGuid().ToString("N")[..8]);
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                                           FileOptions.DeleteOnClose))
            {
                fs.WriteByte(0);
            }
            return true;
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("KnownFolders", $"Directory '{directory}' is not writable: {ex.Message}");
            return false;
        }
    }
}
