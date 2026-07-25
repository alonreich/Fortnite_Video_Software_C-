using System;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace FortniteVideoSoftware.App;

/// <summary>
/// Registers the app in Windows Explorer's "Open with" list for video files, so the
/// user can right-click an .mp4/.mkv/.avi/.mov -> "Open with" -> Fortnite Video Software,
/// and the file loads in the Main App exactly like a normal upload (Program.cs already
/// detects a video path as the first CLI arg and stashes it in OpenWithLaunch).
///
/// This uses HKEY_CURRENT_USER only — NO admin/UAC required — and is fully idempotent:
/// it re-runs cheaply on every launch and only rewrites when the exe path or version
/// stamp changes. DeploymentLifecycle's uninstall registry purge covers HKCU cleanup.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShellFileAssociation
{
    // ProgID: our own file "class" that owns the open command + icon.
    private const string ProgId = "FortniteVideoSoftware.Video";
    private const string AppExeKey = "FortniteVideoSoftware.exe";
    private static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".avi", ".mov" };

    // Bump when the registration shape changes so existing users get re-stamped.
    private const string StampVersion = "1";

    /// <summary>
    /// Ensures the "Open with" association exists for the current executable. Safe to call
    /// on every UI launch; swallows all errors (a missing association is never fatal).
    /// </summary>
    public static void EnsureRegistered()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return;

            string command = $"\"{exePath}\" \"%1\"";
            string stamp = $"{StampVersion}|{exePath}";

            using RegistryKey classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");

            // Skip the full rewrite if nothing changed since last launch.
            using (RegistryKey? existing = classes.OpenSubKey(ProgId))
            {
                if (existing?.GetValue("_fvs_stamp") as string == stamp)
                    return;
            }

            // 1) ProgID: friendly name, icon, and the open command.
            using (RegistryKey prog = classes.CreateSubKey(ProgId))
            {
                prog.SetValue("", "Fortnite Video");
                prog.SetValue("FriendlyTypeName", "Fortnite Video Software");
                prog.SetValue("_fvs_stamp", stamp);
                using (RegistryKey icon = prog.CreateSubKey("DefaultIcon"))
                    icon.SetValue("", $"\"{exePath}\",0");
                using (RegistryKey cmd = prog.CreateSubKey(@"shell\open\command"))
                    cmd.SetValue("", command);
            }

            // 2) Applications\<exe> entry — this is what populates "Open with" and
            //    "Choose another app", scoped to the supported video types.
            using (RegistryKey app = classes.CreateSubKey($@"Applications\{AppExeKey}"))
            {
                app.SetValue("FriendlyAppName", "Fortnite Video Software");
                using (RegistryKey cmd = app.CreateSubKey(@"shell\open\command"))
                    cmd.SetValue("", command);
                using (RegistryKey supported = app.CreateSubKey("SupportedTypes"))
                    foreach (string ext in VideoExtensions)
                        supported.SetValue(ext, "");
            }

            // 3) Advertise the ProgID under each extension's OpenWithProgids so the app
            //    surfaces in the right-click "Open with" submenu (without hijacking the
            //    user's existing default handler).
            foreach (string ext in VideoExtensions)
            {
                using RegistryKey extKey = classes.CreateSubKey($@"{ext}\OpenWithProgids");
                extKey.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            RuntimeLog.Info("OPEN WITH", "Registered Explorer 'Open with' association (HKCU) for .mp4/.mkv/.avi/.mov.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("OPEN WITH", $"Association registration skipped: {ex.Message}");
        }
    }
}
