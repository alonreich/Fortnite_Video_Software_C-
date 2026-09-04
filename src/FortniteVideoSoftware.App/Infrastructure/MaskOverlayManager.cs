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
    /// NOMASK_01 — the reserved, HUD-free profile. Name lives in Core so the Main App, the
    /// Settings window and the Crop Tools app all compare against the same literal.
    /// </summary>
    public static string NoMaskProfileName => CropConfigDefaults.NoMaskProfileName;

    /// <summary>NOMASK_01 — true when <paramref name="profileName"/> is the reserved HUD-free profile.</summary>
    public static bool IsNoMask(string? profileName)
        => string.Equals(profileName?.Trim(), CropConfigDefaults.NoMaskProfileName, StringComparison.OrdinalIgnoreCase);

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

        string[] defaultProfiles = { "Fortnite", "Counter Strike", "Battlefield", "DJI Drones", "Apex Legends", "Call of Duty", CropConfigDefaults.NoMaskProfileName };
        foreach (var p in defaultProfiles)
        {
            var pPath = Path.Combine(ProfilesDirectory, p + ".json");

            // NOMASK_01 — the reserved profile is SELF-HEALING, not merely seeded-if-absent.
            // Every other profile is the user's to shape; this one is a guarantee ("no HUD, ever"),
            // so a file that has drifted — hand-edited, or written by a build before the write
            // guards below existed — is rewritten from CreateNoMask rather than trusted.
            if (IsNoMask(p))
            {
                JsonObject? existing = null;
                if (File.Exists(pPath))
                {
                    try { existing = AtomicJsonFile.ReadObject(pPath); }
                    catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                }

                if (!CropConfigDefaults.IsHudFree(existing))
                {
                    if (existing != null)
                        RuntimeLog.Info("MASK PROFILE", $"'{p}' had HUD layers or missing keys. Restoring the HUD-free document.");
                    AtomicJsonFile.WriteObject(pPath, CropConfigDefaults.CreateNoMask());
                }
                continue;
            }

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
                    catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

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

            // NOMASK_01 — NEVER write the live crop config back into the reserved profile.
            // This method exists so Crop Tools edits follow the active profile. The Main App
            // blocks Crop Tools while the reserved profile is active, but this is the last line
            // of defence: one save through here would bake HUD layers into "No Mask Profile"
            // permanently, and the name would be a lie from then on.
            if (IsNoMask(active))
            {
                RuntimeLog.Info("MASK PROFILE", $"'{active}' is read-only. Live crop config NOT written back to it.");
                return;
            }

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

        // NOMASK_01 — the reserved name cannot be claimed by a user-created profile. Without this,
        // "Create new overlay" named "No Mask Profile" would snapshot the CURRENT crop config over
        // the reserved file and hand the user a fully-masked profile wearing the no-mask name.
        if (IsNoMask(safeName))
        {
            RuntimeLog.Info("MASK PROFILE", $"'{safeName}' is a reserved profile name. Creation refused.");
            return;
        }

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
