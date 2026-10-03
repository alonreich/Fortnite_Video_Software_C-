// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System.Diagnostics;
using FreeVideoStudio.Core.Infrastructure;
using Microsoft.Win32;

namespace FreeVideoStudio.App.Services;

internal static class InstallDiscovery
{
    public static string Destination => DeploymentFootprint.InstallFolder;
    public static string Store => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FreeVideoStudioMigration");
    public static string[] Executables => LegacyProductIdentity.ExecutableNames.Append(InstallPayload.ExecutableName).ToArray();

    /// <summary>
    /// NOSPACE_01 — install destinations a machine journal may legitimately name: today's
    /// space-free folder, and the spaced folder every journal written before NOSPACE_01 names.
    /// </summary>
    public static string[] KnownDestinations =>
        [UpgradeFiles.FullPath(Destination), UpgradeFiles.FullPath(DeploymentFootprint.LegacyInstallFolder)];

    /// <summary>
    /// NOSPACE_01 — where a COMPACT installer reuses unchanged runtime files from. It used to be
    /// <see cref="Destination"/> unconditionally, which does not exist yet when the update is also
    /// the one that moves the install out of the spaced folder: every compact update for such a
    /// machine would have failed with "Download the full installer". The first existing root that
    /// holds an install manifest wins, Destination first.
    /// </summary>
    public static string? ReuseRoot(IEnumerable<string> roots) =>
        new[] { Destination }.Concat(roots)
            .FirstOrDefault(r => File.Exists(Path.Combine(r, InstallPayload.ManifestName)));

    public static string[] FindRoots()
    {
        if (Directory.Exists(Destination) && Directory.EnumerateFileSystemEntries(Destination).Any() && !IsProductDirectory(Destination))
            throw new IOException("The installation folder contains files from an unknown installation. It was left unchanged.");
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Destination };
        foreach (Environment.SpecialFolder parent in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            // NOSPACE_01 — the spaced current-brand folder is a legacy root too: the install moves it
            // to the space-free Destination exactly like a previous-brand folder.
            foreach (string name in new[] { LegacyProductIdentity.DisplayName, LegacyProductIdentity.CompactName, DeploymentFootprint.LegacyInstallFolderName })
            {
                string path = Path.Combine(Environment.GetFolderPath(parent), name);
                if (IsProductDirectory(path)) roots.Add(UpgradeFiles.FullPath(path));
            }
        }
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            foreach (string name in new[] { LegacyProductIdentity.DisplayName, LegacyProductIdentity.CompactName, DeploymentFootprint.DisplayName })
            {
                using RegistryKey? key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + name);
                if (key?.GetValue("InstallLocation") is string path && IsProductDirectory(path))
                    roots.Add(UpgradeFiles.FullPath(path));
            }
        }
        foreach (string root in roots)
        foreach (string name in Executables)
        {
            string exe = Path.Combine(root, name);
            if (File.Exists(exe) && DeploymentLifecycle.TryParseVersion(FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "", out var installed) &&
                DeploymentLifecycle.TryParseVersion(DeploymentLifecycle.GetCurrentVersion(), out var candidate) && installed > candidate)
                throw new IOException("A newer version is already installed. Download the latest installer to continue.");
        }
        return roots.ToArray();
    }

    internal static bool IsProductDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path) ||
            string.Equals(UpgradeFiles.FullPath(path), Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase)) return false;
        UpgradeFiles.RequirePlainPath(path);
        foreach (string name in Executables)
        {
            string exe = Path.Combine(path, name);
            if (!File.Exists(exe)) continue;
            FileVersionInfo identity = FileVersionInfo.GetVersionInfo(exe);
            if (identity.CompanyName == "Alon Reich" && identity.ProductName is { } product &&
                (product == DeploymentFootprint.DisplayName || product == LegacyProductIdentity.DisplayName ||
                 product == "Clip Studio for Gameplay")) return true;
        }
        return false;
    }

    public static void EnsureIdle(IEnumerable<string> roots, params int[] excludedPids)
    {
        foreach (string name in Executables.Select(x => Path.GetFileNameWithoutExtension(x)!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (excludedPids.Contains(process.Id)) continue;
                    try
                    {
                        if (process.HasExited) continue;
                        string? executable = process.MainModule?.FileName;
                        if (executable != null && roots.Any(root => UpgradeFiles.IsWithin(executable, root)))
                            throw new IOException("Finish editing, exporting or recording and close all app windows, including other Windows sessions, before updating.");
                    }
                    catch (System.ComponentModel.Win32Exception ex)
                    {
                        throw new IOException("Another app process could not be checked. Close it or sign out of the other Windows account before updating.", ex);
                    }
                    catch (InvalidOperationException ex)
                    {
                        RuntimeLog.Info("Upgrade", $"Process exited during the idle check: {ex.Message}");
                    }
                }
            }
        }
    }

    public static void EnsureSpace(string root, IEnumerable<string> sourceRoots, long payloadBytes)
    {
        long bytes = payloadBytes;
        foreach (string source in sourceRoots.Where(Directory.Exists))
            foreach (string file in UpgradeFiles.Files(source)) bytes = checked(bytes + new FileInfo(file).Length * 2);
        var drive = new DriveInfo(Path.GetPathRoot(root)!);
        if (drive.AvailableFreeSpace < checked(bytes + 256L * 1024 * 1024))
            throw new IOException("There is not enough free disk space to install this update and keep a recovery backup.");
    }
}
