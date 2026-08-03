using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Ipc;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App.Infrastructure;

public static class MaskOverlayManager
{
    public static string ProfilesDirectory => Path.Combine(ApplicationPaths.CreateDefault().ProgramDataRoot, "MaskProfiles");

    /// <summary>
    /// ISSUE_3: all reads/writes of the shared crops_coordinations.conf must hold the
    /// same named system mutex that CropConfigStore/StateTransferStore use, because
    /// Main App and Crop Tools are both alive during process handoff.
    /// </summary>
    private static IDisposable AcquireConfigLock()
    {
        return NamedSystemMutex.Acquire(
            StateTransferStore.MutexName,
            StateTransferStore.DefaultMutexTimeout);
    }

    /// <summary>
    /// ISSUE_6: profile names are user input that becomes a file name. Strips invalid
    /// filename characters and rejects names that would resolve outside the profiles
    /// directory. Returns null when no safe name can be produced.
    /// </summary>
    public static string? SanitizeProfileName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return null;

        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new string(rawName.Trim().Where(c => !invalid.Contains(c)).ToArray()).Trim();
        if (cleaned.Length == 0 || cleaned.All(c => c == '.')) return null;

        string fullPath = Path.GetFullPath(Path.Combine(ProfilesDirectory, cleaned + ".json"));
        string root = Path.GetFullPath(ProfilesDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;

        return cleaned;
    }

    public static void EnsureDefaults()
    {
        if (!Directory.Exists(ProfilesDirectory))
            Directory.CreateDirectory(ProfilesDirectory);

        string[] defaultProfiles = { "Fortnite", "Counter Strike", "Battlefield", "DJI Drones", "Apex Legends", "Call of Duty" };
        foreach (var p in defaultProfiles)
        {
            var pPath = Path.Combine(ProfilesDirectory, p + ".json");
            if (!File.Exists(pPath))
            {
                if (p == "Fortnite" && File.Exists(ApplicationPaths.CreateDefault().CropCoordinatesFile))
                {
                    JsonObject? existingConfig = null;
                    try
                    {
                        using (AcquireConfigLock())
                        {
                            existingConfig = AtomicJsonFile.ReadObject(ApplicationPaths.CreateDefault().CropCoordinatesFile);
                        }
                    }
                    catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }

                    if (existingConfig != null)
                    {
                        AtomicJsonFile.WriteObject(pPath, existingConfig);
                        continue;
                    }
                }

                var def = CropConfigDefaults.Create();
                AtomicJsonFile.WriteObject(pPath, def);
            }
        }

        if (!File.Exists(ApplicationPaths.CreateDefault().CropCoordinatesFile))
        {
            ApplyProfile(SettingsManager.Instance.ActiveMaskOverlay);
        }
    }

    public static List<string> GetAvailableProfiles()
    {
        EnsureDefaults();
        var files = Directory.GetFiles(ProfilesDirectory, "*.json");
        var list = new List<string>();
        foreach (var f in files)
        {
            list.Add(Path.GetFileNameWithoutExtension(f));
        }
        return list;
    }

    public static void ApplyProfile(string profileName)
    {
        try
        {
            EnsureDefaults();
            var pPath = Path.Combine(ProfilesDirectory, profileName + ".json");
            if (!File.Exists(pPath))
            {
                RuntimeLog.Info("MASK PROFILE", $"Profile '{profileName}' not found. Keeping current crop configuration.");
                return;
            }

            var config = AtomicJsonFile.ReadObject(pPath);
            if (config == null) return;

            var sanitized = HudConfig.Sanitize(config, migrateLegacy: true);
            using (AcquireConfigLock())
            {
                AtomicJsonFile.WriteObject(ApplicationPaths.CreateDefault().CropCoordinatesFile, sanitized);
            }

            SettingsManager.Instance.ActiveMaskOverlay = profileName;
            SettingsManager.Save();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MASK PROFILE", $"ApplyProfile('{profileName}') failed: {ex.Message}");
        }
    }

    public static void SyncActiveProfileFromCurrentConfig()
    {
        try
        {
            var active = SettingsManager.Instance.ActiveMaskOverlay;
            if (string.IsNullOrWhiteSpace(active)) return;

            var pPath = Path.Combine(ProfilesDirectory, active + ".json");
            JsonObject? current;
            using (AcquireConfigLock())
            {
                current = AtomicJsonFile.ReadObject(ApplicationPaths.CreateDefault().CropCoordinatesFile);
            }

            if (current != null)
            {
                if (!Directory.Exists(ProfilesDirectory)) Directory.CreateDirectory(ProfilesDirectory);
                AtomicJsonFile.WriteObject(pPath, current);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MASK PROFILE", $"Profile sync failed: {ex.Message}");
        }
    }

    public static void CreateNewProfile(string newName)
    {
        string? safeName = SanitizeProfileName(newName);
        if (safeName == null) return;
        EnsureDefaults();

        var pPath = Path.Combine(ProfilesDirectory, safeName + ".json");
        JsonObject? current;
        using (AcquireConfigLock())
        {
            current = AtomicJsonFile.ReadObject(ApplicationPaths.CreateDefault().CropCoordinatesFile);
        }
        current ??= CropConfigDefaults.Create();

        AtomicJsonFile.WriteObject(pPath, current);

        SettingsManager.Instance.ActiveMaskOverlay = safeName;
        SettingsManager.Save();
    }
}
