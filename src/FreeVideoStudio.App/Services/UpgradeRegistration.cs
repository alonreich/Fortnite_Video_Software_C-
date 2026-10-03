// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;
using Microsoft.Win32;

namespace FreeVideoStudio.App.Services;

/// <summary>UPGRADE_08 — snapshot shell state, replace owned links, and restore it on rollback.</summary>
internal sealed class UpgradeRegistration(bool machine, string backupDirectory, string[] installRoots)
{
    public bool DesktopAvailable => machine ? KnownFolders.GetPublicDesktop() != null : KnownFolders.GetDesktop() != null;
    internal const string ActiveSetupKey = @"SOFTWARE\Microsoft\Active Setup\Installed Components\{24CA6D78-3483-4D3A-B797-110F10804E39}";
    private string SnapshotPath => Path.Combine(backupDirectory, "shell-state.json");
    private JsonObject ReadSnapshot() => AtomicJsonFile.ReadObject(SnapshotPath)
        ?? throw new IOException("The shortcut recovery record could not be read. It has been preserved.");
    private RegistryHive Hive => machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
    private static readonly string[] Extensions = [".mp4", ".mkv", ".avi", ".mov"];

    private IEnumerable<string> KeyPaths()
    {
        foreach (string name in new[] { DeploymentFootprint.DisplayName, "FreeVideoStudio", LegacyProductIdentity.DisplayName, LegacyProductIdentity.CompactName })
        {
            yield return @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + name;
            yield return @"SOFTWARE\" + name;
        }
        if (machine) yield return ActiveSetupKey;
        else
        {
            foreach (string name in new[] { "FreeVideoStudio", LegacyProductIdentity.CompactName })
            {
                yield return @"Software\Classes\" + name + ".Video";
                yield return @"Software\Classes\Applications\" + name + ".exe";
            }
            foreach (string ext in Extensions) yield return @"Software\Classes\" + ext + @"\OpenWithProgids";
        }
    }

    private string[] Folders() => (machine
        ? new[] { KnownFolders.GetPublicDesktop(), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) }
        : new[] { KnownFolders.GetDesktop(), Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.Startup) })
        .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private bool OwnsTarget(string target) => installRoots.Any(root =>
        InstallDiscovery.Executables.Append("Uninstall.exe").Any(name =>
            string.Equals(Path.Combine(root, name), target, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// NOSPACE_01 — rewrites this user's EXISTING shortcuts whose target is inside one of
    /// <paramref name="movedRoots"/> so they point at the current install. Creates nothing,
    /// deletes nothing, and leaves unrelated shortcuts alone. Returns how many were rewritten.
    /// </summary>
    public static async Task<int> RetargetUserLinksAsync(string[] movedRoots)
    {
        var probe = new UpgradeRegistration(false, Path.GetTempPath(), movedRoots);
        int changed = 0;
        foreach (string folder in probe.Folders())
        foreach (string link in Directory.EnumerateFiles(folder, "*.lnk"))
        {
            try
            {
                string target = await ReadTargetAsync(link).ConfigureAwait(false);
                if (!probe.OwnsTarget(target)) continue;
                // An uninstall shortcut is machine-wide and rewritten by the elevated worker.
                if (string.Equals(Path.GetFileName(target), DeploymentFootprint.UninstallExeName, StringComparison.OrdinalIgnoreCase)) continue;
                string temporary = link + ".upgrade.lnk";
                await ShellLinkWriter.CreateAsync(temporary, DeploymentFootprint.InstallPath, InstallDiscovery.Destination,
                    DeploymentFootprint.InstallPath + ",0", DeploymentFootprint.DisplayName).ConfigureAwait(false);
                File.Move(temporary, link, overwrite: true);
                changed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RuntimeLog.WarnThrottled("Upgrade", $"Shortcut could not be repointed; it will be retried: {ex.Message}");
            }
        }
        return changed;
    }

    public async Task CaptureAsync()
    {
        Directory.CreateDirectory(backupDirectory);
        var keys = new JsonArray();
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(Hive, view);
            foreach (string path in KeyPaths())
            {
                using RegistryKey? key = root.OpenSubKey(path);
                keys.AddNode((JsonNode)new JsonObject { ["view"] = (int)view, ["path"] = path, ["data"] = key == null ? null : CaptureKey(key) });
            }
        }
        var links = new JsonArray();
        foreach (string folder in Folders())
        {
            foreach (string path in Directory.EnumerateFiles(folder, "*.lnk"))
            {
                try
                {
                    if (!OwnsTarget(await ReadTargetAsync(path).ConfigureAwait(false))) continue;
                    links.AddNode((JsonNode)new JsonObject { ["path"] = path, ["bytes"] = Convert.ToBase64String(File.ReadAllBytes(path)) });
                }
                catch (Exception ex) when (!machine && ex is IOException or UnauthorizedAccessException)
                {
                    RuntimeLog.WarnThrottled("Upgrade", $"Shortcut is offline or inaccessible; it will be retried: {ex.Message}");
                }
            }
        }
        AtomicJsonFile.WriteObject(SnapshotPath, new JsonObject { ["keys"] = keys, ["links"] = links, ["created"] = new JsonArray() });
    }

    public async Task ApplyAsync()
    {
        JsonObject snapshot = ReadSnapshot();
        var known = new HashSet<string>(SavedLinks(), StringComparer.OrdinalIgnoreCase);
        foreach (string folder in Folders())
        foreach (string link in Directory.EnumerateFiles(folder, "*.lnk"))
        {
            if (!known.Contains(link) && OwnsTarget(await ReadTargetAsync(link).ConfigureAwait(false)))
                snapshot["links"]!.AsArray().AddNode((JsonNode)new JsonObject { ["path"] = link, ["bytes"] = Convert.ToBase64String(File.ReadAllBytes(link)) });
        }
        AtomicJsonFile.WriteObject(SnapshotPath, snapshot);
        if (machine)
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (RegistryKey key = root.CreateSubKey(DeploymentFootprint.UninstallKeyPath))
            {
                key.SetValue("DisplayName", DeploymentFootprint.DisplayName);
                key.SetValue("DisplayVersion", DeploymentLifecycle.GetCurrentVersion());
                key.SetValue("Publisher", "Alon Reich");
                key.SetValue("InstallLocation", InstallDiscovery.Destination);
                key.SetValue("DisplayIcon", DeploymentFootprint.InstallPath + ",0");
                key.SetValue("UninstallString", $"\"{DeploymentFootprint.UninstallPath}\" --uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            using (RegistryKey setup = root.CreateSubKey(ActiveSetupKey))
            {
                setup.SetValue("", DeploymentFootprint.DisplayName + " user migration");
                setup.SetValue("Version", DeploymentLifecycle.GetCurrentVersion().Replace('.', ','));
                setup.SetValue("StubPath", $"\"{DeploymentFootprint.InstallPath}\" --complete-user-migration");
            }
            await ReplaceAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Free Video Studio.lnk")).ConfigureAwait(false);
            string? publicDesktop = KnownFolders.GetPublicDesktop();
            if (publicDesktop != null && SavedLinks().Any(p => Path.GetDirectoryName(p) == publicDesktop))
                await ReplaceAsync(Path.Combine(publicDesktop, "Free Video Studio.lnk")).ConfigureAwait(false);
        }
        else
        {
            WriteOpenWith();
            string? desktop = KnownFolders.GetDesktop();
            string? shared = KnownFolders.GetPublicDesktop();
            bool publicIcon = shared != null && File.Exists(Path.Combine(shared, "Free Video Studio.lnk"));
            if (desktop != null && !publicIcon)
                await ReplaceAsync(Path.Combine(desktop, "Free Video Studio.lnk")).ConfigureAwait(false);
            if (!File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Free Video Studio.lnk")))
                await ReplaceAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Free Video Studio.lnk")).ConfigureAwait(false);
        }
    }

    public async Task RemoveLegacyAsync()
    {
        bool replacement = new[] { KnownFolders.GetDesktop(), KnownFolders.GetPublicDesktop() }
            .Where(p => p != null).Any(p => File.Exists(Path.Combine(p!, "Free Video Studio.lnk")));
        foreach (string path in SavedLinks())
        {
            if (!File.Exists(path)) continue;
            if (!replacement && Folders().Any(p => string.Equals(p, Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase))) continue;
            string target = await ReadTargetAsync(path).ConfigureAwait(false);
            if (!OwnsTarget(target) || target.Equals(DeploymentFootprint.InstallPath, StringComparison.OrdinalIgnoreCase)) continue;
            File.Delete(path);
        }
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(Hive, view);
            foreach (string name in new[] { LegacyProductIdentity.DisplayName, LegacyProductIdentity.CompactName })
            {
                RemoveCapturedKey(root, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + name);
                RemoveCapturedKey(root, view, @"SOFTWARE\" + name);
            }
        }
        if (!machine)
        {
            using RegistryKey classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            string oldProgId = LegacyProductIdentity.CompactName + ".Video";
            using (RegistryKey? old = classes.OpenSubKey(oldProgId, writable: true))
            {
                if (old != null)
                {
                    old.SetValue("FriendlyTypeName", DeploymentFootprint.DisplayName);
                    using RegistryKey cmd = old.CreateSubKey(@"shell\open\command");
                    cmd.SetValue("", $"\"{DeploymentFootprint.InstallPath}\" \"%1\"");
                    using RegistryKey icon = old.CreateSubKey("DefaultIcon");
                    icon.SetValue("", $"\"{DeploymentFootprint.InstallPath}\",0");
                }
            }
            classes.DeleteSubKeyTree(@"Applications\" + LegacyProductIdentity.DownloadName, false);
            foreach (string ext in Extensions)
            {
                using RegistryKey? key = classes.OpenSubKey(ext + @"\OpenWithProgids", true);
                key?.DeleteValue(oldProgId, false);
            }
        }
    }

    private void RemoveCapturedKey(RegistryKey root, RegistryView view, string path)
    {
        JsonNode? saved = ReadSnapshot()["keys"]!.AsArray().FirstOrDefault(x =>
            x!["view"]!.GetValue<int>() == (int)view && x["path"]!.GetValue<string>() == path);
        using RegistryKey? current = root.OpenSubKey(path);
        if (current == null || saved?["data"] == null) return;
        if (JsonNode.DeepEquals(CaptureKey(current), saved["data"])) root.DeleteSubKeyTree(path, false);
    }

    public async Task RestoreAsync()
    {
        if (!File.Exists(SnapshotPath)) return;
        JsonObject snapshot = ReadSnapshot();
        var allowed = new HashSet<string>(KeyPaths(), StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? entry in snapshot["keys"]!.AsArray())
        {
            string path = entry!["path"]!.GetValue<string>();
            if (!allowed.Contains(path)) throw new IOException("Unexpected registry path in upgrade backup.");
            using RegistryKey root = RegistryKey.OpenBaseKey(Hive, (RegistryView)entry["view"]!.GetValue<int>());
            if (path.EndsWith(@"\OpenWithProgids", StringComparison.OrdinalIgnoreCase))
            {
                using RegistryKey associations = root.CreateSubKey(path);
                foreach (string name in new[] { "FreeVideoStudio.Video", LegacyProductIdentity.CompactName + ".Video" })
                {
                    associations.DeleteValue(name, false);
                    JsonNode? original = entry["data"]?["values"]?.AsArray().FirstOrDefault(v => v!["name"]!.GetValue<string>() == name);
                    if (original != null)
                        RestoreKey(associations, new JsonObject { ["values"] = new JsonArray(original.DeepClone()), ["children"] = new JsonObject() });
                }
                continue;
            }
            root.DeleteSubKeyTree(path, false);
            if (entry["data"] is JsonObject data)
            {
                using RegistryKey key = root.CreateSubKey(path);
                RestoreKey(key, data);
            }
        }
        foreach (JsonNode? entry in snapshot["created"]!.AsArray())
        {
            string path = entry!.GetValue<string>();
            if (File.Exists(path) && OwnsTarget(await ReadTargetAsync(path).ConfigureAwait(false))) File.Delete(path);
        }
        foreach (JsonNode? entry in snapshot["links"]!.AsArray())
        {
            string path = entry!["path"]!.GetValue<string>();
            if (File.Exists(path) && !OwnsTarget(await ReadTargetAsync(path).ConfigureAwait(false))) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Convert.FromBase64String(entry["bytes"]!.GetValue<string>()));
        }
    }

    private IEnumerable<string> SavedLinks() => ReadSnapshot()["links"]!.AsArray()
        .Select(x => x!["path"]!.GetValue<string>());

    private async Task ReplaceAsync(string path)
    {
        if (File.Exists(path) && !OwnsTarget(await ReadTargetAsync(path).ConfigureAwait(false)))
            throw new IOException($"A different shortcut already uses '{path}'. It was left unchanged.");
        JsonObject snapshot = ReadSnapshot();
        if (!SavedLinks().Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            snapshot["created"]!.AsArray().AddNode((JsonNode?)JsonValue.Create(path));
            AtomicJsonFile.WriteObject(SnapshotPath, snapshot);
        }
        string temporary = path + ".upgrade.lnk";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await ShellLinkWriter.CreateAsync(temporary, DeploymentFootprint.InstallPath, InstallDiscovery.Destination,
            DeploymentFootprint.InstallPath + ",0", DeploymentFootprint.DisplayName).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private static void WriteOpenWith()
    {
        using RegistryKey classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
        foreach (string name in new[] { "FreeVideoStudio.Video", @"Applications\FreeVideoStudio.exe" })
        {
            using RegistryKey key = classes.CreateSubKey(name);
            key.SetValue("FriendlyAppName", DeploymentFootprint.DisplayName);
            key.SetValue("FriendlyTypeName", DeploymentFootprint.DisplayName);
            using RegistryKey cmd = key.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{DeploymentFootprint.InstallPath}\" \"%1\"");
            using RegistryKey icon = key.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{DeploymentFootprint.InstallPath}\",0");
        }
        foreach (string ext in Extensions)
        {
            using RegistryKey key = classes.CreateSubKey(ext + @"\OpenWithProgids");
            key.SetValue("FreeVideoStudio.Video", Array.Empty<byte>(), RegistryValueKind.None);
        }
    }

    private static JsonObject CaptureKey(RegistryKey key)
    {
        var values = new JsonArray();
        foreach (string name in key.GetValueNames())
        {
            object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            JsonNode? data = value switch
            {
                string s => JsonValue.Create(s), int n => JsonValue.Create(n), long n => JsonValue.Create(n),
                byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
                string[] strings => new JsonArray(strings.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                _ => throw new IOException("Unsupported registry value in installation backup.")
            };
            values.AddNode((JsonNode)new JsonObject { ["name"] = name, ["kind"] = (int)key.GetValueKind(name), ["value"] = data });
        }
        var children = new JsonObject();
        foreach (string name in key.GetSubKeyNames())
        {
            using RegistryKey child = key.OpenSubKey(name)!;
            children[name] = CaptureKey(child);
        }
        return new JsonObject { ["values"] = values, ["children"] = children };
    }

    private static void RestoreKey(RegistryKey key, JsonObject data)
    {
        foreach (JsonNode? row in data["values"]!.AsArray())
        {
            var kind = (RegistryValueKind)row!["kind"]!.GetValue<int>();
            JsonNode value = row["value"]!;
            object restored = kind switch
            {
                RegistryValueKind.DWord => value.GetValue<int>(), RegistryValueKind.QWord => value.GetValue<long>(),
                RegistryValueKind.Binary or RegistryValueKind.None => Convert.FromBase64String(value.GetValue<string>()),
                RegistryValueKind.MultiString => value.AsArray().Select(x => x!.GetValue<string>()).ToArray(),
                RegistryValueKind.String or RegistryValueKind.ExpandString => value.GetValue<string>(),
                _ => throw new IOException("Unsupported registry backup kind.")
            };
            key.SetValue(row["name"]!.GetValue<string>(), restored, kind);
        }
        foreach (var child in data["children"]!.AsObject())
        {
            using RegistryKey subkey = key.CreateSubKey(child.Key);
            RestoreKey(subkey, child.Value!.AsObject());
        }
    }

    internal static Task<string> ReadTargetAsync(string path) => RunPowerShellAsync(
        "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('" + path.Replace("'", "''", StringComparison.Ordinal) + "'); [Console]::Write($s.TargetPath)");

    internal static async Task<string> RunPowerShellAsync(string script)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("powershell.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; " + script)) })
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new IOException("Windows shortcut operation timed out.");
        }
        if (process.ExitCode != 0) throw new IOException(await error.ConfigureAwait(false));
        return (await output.ConfigureAwait(false)).Trim();
    }
}
