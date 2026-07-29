using System.Runtime.InteropServices;

namespace FortniteVideoSoftware.Core.Infrastructure;

/// <summary>
/// Resolves Windows known folders through the SHELL API rather than through
/// <c>Environment.GetFolderPath</c>.
///
/// WHY THIS EXISTS (do not "simplify" back to Environment.GetFolderPath):
///   1. There is NO <c>SpecialFolder</c> value for Downloads at all — the old code guessed
///      <c>%USERPROFILE%\Downloads</c>, which is simply wrong on any machine where the user
///      moved the folder.
///   2. OneDrive "Backup / Manage folders" redirects Desktop, Documents and Pictures into
///      <c>%USERPROFILE%\OneDrive\...</c>. <c>Environment.GetFolderPath</c> reads a cached,
///      stale value in several of those cases; <c>SHGetKnownFolderPath</c> always returns the
///      live redirected target.
///   3. Group Policy folder redirection (corporate/domain machines) points these at a UNC
///      share. Only the shell API knows.
///
/// Every method is total: it NEVER throws and NEVER returns a path it has not verified, so a
/// missing/redirected/offline folder degrades to null for the caller to handle — it must never
/// crash the app.
/// </summary>
public static class KnownFolders
{
    private static readonly Guid DownloadsGuid = new("374DE290-123F-4565-9164-39C4925E467B");
    private static readonly Guid VideosGuid = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");
    private static readonly Guid DesktopGuid = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    // FOLDERID_PublicDesktop — C:\Users\Public\Desktop. Machine-wide, never redirected.
    private static readonly Guid PublicDesktopGuid = new("C4AA340D-F20F-4863-AFEF-F87EF2E6BA25");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        in Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);

    private const uint KfFlagDontVerify = 0x00004000;

    /// <summary>
    /// The user's real Downloads folder, honouring moves, OneDrive redirection and Group
    /// Policy redirection. Returns null if it cannot be resolved or does not exist on disk.
    /// </summary>
    public static string? GetDownloads() => Resolve(DownloadsGuid);

    /// <summary>The user's real Videos folder, or null.</summary>
    public static string? GetVideos() => Resolve(VideosGuid);

    /// <summary>
    /// The CURRENT user's real Desktop folder, honouring OneDrive "Backup / Manage folders"
    /// redirection (<c>%USERPROFILE%\OneDrive\Desktop</c>, or a localised/business variant such as
    /// <c>OneDrive - Contoso\Desktop</c>) and Group Policy folder redirection to a UNC share.
    /// Returns null if it cannot be resolved or does not exist on disk.
    ///
    /// ⚠️ ELEVATION WARNING — READ BEFORE USING THIS IN THE INSTALLER.
    /// "Current user" means the user this PROCESS runs as. The installer self-elevates with
    /// <c>Verb = "runas"</c>, and when the person who launched it is a standard user, Windows runs
    /// the elevated worker as a DIFFERENT (administrator) account. Calling this from the elevated
    /// worker therefore returns the ADMINISTRATOR'S desktop, and the shortcut lands somewhere the
    /// user will never see.
    ///
    /// The installer must call this BEFORE it elevates and pass the result to the elevated worker
    /// (see <c>DeploymentLifecycle.DesktopShortcutArgument</c>). Do not "simplify" that away.
    /// </summary>
    public static string? GetDesktop() => Resolve(DesktopGuid);

    /// <summary>
    /// The All-Users / Public desktop (<c>C:\Users\Public\Desktop</c>), used as the installer's
    /// fallback when the invoking user's desktop cannot be determined.
    ///
    /// This one is deliberately NOT subject to redirection — it is a machine location, so it
    /// resolves identically from an elevated process. That is exactly why it makes a safe
    /// fallback, and also why it is not a substitute for <see cref="GetDesktop"/>.
    /// </summary>
    public static string? GetPublicDesktop() => Resolve(PublicDesktopGuid);

    /// <summary>
    /// Legacy "User Shell Folders" value names for the folders we resolve.
    ///
    /// WHY THIS MAP EXISTS: the registry fallback in <see cref="Resolve"/> looks values up by
    /// <c>{GUID}</c>, which only works for the newer known folders — Downloads really is stored as
    /// <c>{374DE290-...}</c>. The older ones are stored under their historical NAME instead
    /// ("Desktop", "My Video", "Common Desktop"), so a GUID-only lookup silently found nothing and
    /// the fallback was dead code for them. Both spellings are tried.
    /// </summary>
    private static string[] LegacyShellFolderNames(Guid folderId)
    {
        if (folderId == DesktopGuid) return ["Desktop"];
        if (folderId == PublicDesktopGuid) return ["Common Desktop"];
        if (folderId == VideosGuid) return ["My Video"];
        if (folderId == DownloadsGuid) return ["{374DE290-123F-4565-9164-39C4925E467B}"];
        return [];
    }

    /// <summary>Public/Common folders live in HKLM; per-user folders live in HKCU.</summary>
    private static bool IsMachineFolder(Guid folderId) => folderId == PublicDesktopGuid;

    private static string? Resolve(Guid folderId)
    {
        if (!OperatingSystem.IsWindows()) return null;

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
        catch
        {
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
        catch
        {
        }

        return null;
    }

    private static string? ReadUserShellFolder(Guid folderId)
    {
        if (!OperatingSystem.IsWindows()) return null;

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
            catch
            {
            }
        }

        return null;
    }

    /// <summary>
    /// True only if the directory exists AND this process can actually create a file in it.
    /// A path can exist and still be unwritable (read-only network share, ACL-locked folder,
    /// OneDrive "files on-demand" placeholder that is offline).
    /// </summary>
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
        catch
        {
            return false;
        }
    }
}
