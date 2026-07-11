using System;
using System.Collections.Generic;
using System.IO;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Ipc;

namespace FortniteVideoSoftware.App.Infrastructure;

public static class MaskOverlayManager
{
    public static string ProfilesDirectory => Path.Combine(ApplicationPaths.CreateDefault().ProgramDataRoot, "MaskProfiles");

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
                    var existingConfig = FortniteVideoSoftware.Core.Infrastructure.AtomicJsonFile.ReadObject(ApplicationPaths.CreateDefault().CropCoordinatesFile);
                    if (existingConfig != null)
                    {
                        FortniteVideoSoftware.Core.Infrastructure.AtomicJsonFile.WriteObject(pPath, existingConfig);
                        continue;
                    }
                }

                var def = CropConfigDefaults.Create();
                FortniteVideoSoftware.Core.Infrastructure.AtomicJsonFile.WriteObject(pPath, def);
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
        foreach(var f in files)
        {
            list.Add(Path.GetFileNameWithoutExtension(f));
        }
        return list;
    }

    public static void ApplyProfile(string profileName)
    {
        var pPath = Path.Combine(ProfilesDirectory, profileName + ".json");
        if (File.Exists(pPath))
        {
            var config = FortniteVideoSoftware.Core.Infrastructure.AtomicJsonFile.ReadObject(pPath);
            if (config != null)
            {
                FortniteVideoSoftware.Core.Infrastructure.AtomicJsonFile.WriteObject(ApplicationPaths.CreateDefault().CropCoordinatesFile, config);
                SettingsManager.Instance.ActiveMaskOverlay = profileName;
                SettingsManager.Save();
            }
        }
    }

    public static void SyncActiveProfileFromCurrentConfig()
    {
        var active = SettingsManager.Instance.ActiveMaskOverlay;
        if (string.IsNullOrWhiteSpace(active)) return;

        var pPath = Path.Combine(ProfilesDirectory, active + ".json");
        var current = FortniteVideoSoftware.Core.Infrastructure.AtomicJsonFile.ReadObject(ApplicationPaths.CreateDefault().CropCoordinatesFile);
        if (current != null)
        {
            if (!Directory.Exists(ProfilesDirectory)) Directory.CreateDirectory(ProfilesDirectory);
            FortniteVideoSoftware.Core.Infrastructure.AtomicJsonFile.WriteObject(pPath, current);
        }
    }

    public static void CreateNewProfile(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        EnsureDefaults();
        
        var pPath = Path.Combine(ProfilesDirectory, newName + ".json");
        var current = FortniteVideoSoftware.Core.Infrastructure.AtomicJsonFile.ReadObject(ApplicationPaths.CreateDefault().CropCoordinatesFile);
        if (current == null) current = CropConfigDefaults.Create();
        
        FortniteVideoSoftware.Core.Infrastructure.AtomicJsonFile.WriteObject(pPath, current);
        
        SettingsManager.Instance.ActiveMaskOverlay = newName;
        SettingsManager.Save();
    }
}
