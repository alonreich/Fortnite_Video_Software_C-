
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;
using Microsoft.Win32;

namespace FreeVideoStudio.App.Services;

/// <summary>UPGRADE_09 — privileged work is restricted to verified machine installation roots.</summary>
internal static class UpgradeInstallWorker
{
    internal static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (!IsElevated() || args.Length != 3 || !int.TryParse(args[1], out int port) || port is < 1 or > 65535 ||
            args[2].Length != 64 || args[2].Any(c => !Uri.IsHexDigit(c))) return 2;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
        using var channel = new UpgradeChannel(client);
        await channel.SendAsync("hello", args[2]).ConfigureAwait(false);
        using var gate = new Semaphore(1, 1, DeploymentFootprint.InstallerGateName);
        using var legacyGate = new Semaphore(1, 1, @"Global\" + LegacyProductIdentity.CompactName + "_InstallerGate");
        bool ownsGate = gate.WaitOne(0), ownsLegacy = false;
        DirectoryUpgrade? transaction = null;
        UpgradeRegistration? registration = null;
        try
        {
            if (!ownsGate || !(ownsLegacy = legacyGate.WaitOne(0)))
                throw new IOException("Another installation or uninstall is running. Try again when it finishes.");
            UpgradeFiles.RequirePlainPath(InstallDiscovery.Store);
            await RecoverInterruptedAsync().ConfigureAwait(false);
            string mode = await channel.ExpectAsync("mode").ConfigureAwait(false);
            if (mode == "recover")
            {
                await channel.SendAsync("recovered").ConfigureAwait(false);
                return 0;
            }
            if (mode != "install") throw new IOException("Unknown installation request.");
            string[] roots = InstallDiscovery.FindRoots();
            InstallDiscovery.EnsureIdle(roots, Environment.ProcessId);
            transaction = DirectoryUpgrade.Create(InstallDiscovery.Store, InstallDiscovery.Destination,
                roots.Where(r => !r.Equals(InstallDiscovery.Destination, StringComparison.OrdinalIgnoreCase)));
            registration = new UpgradeRegistration(true, transaction.DirectoryPath, roots);
            await registration.CaptureAsync().ConfigureAwait(false);
            await channel.SendAsync("ready", new JsonObject
            {
                ["transaction"] = transaction.DirectoryPath,
                ["roots"] = new JsonArray(roots.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray())
            }.ToJsonString()).ConfigureAwait(false);
            await channel.ExpectAsync("begin").ConfigureAwait(false);
            using Stream payload = typeof(UpgradeInstallWorker).Assembly.GetManifestResourceStream("FreeVideoStudio.App.payload.zip")
                ?? throw new IOException("This file does not contain a full installation payload. Download the full installer.");
            long unpacked;
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true)) unpacked = archive.Entries.Sum(x => x.Length);
            payload.Position = 0;
            InstallDiscovery.EnsureSpace(InstallDiscovery.Destination, roots, checked(unpacked * 2));
            string extracted = Path.Combine(transaction.DirectoryPath, "payload");
            InstallPayload.Extract(payload, extracted, InstallDiscovery.Destination);
            transaction.Prepare(candidate =>
            {
                foreach (string file in UpgradeFiles.Files(extracted))
                    UpgradeFiles.CopyVerified(file, Path.Combine(candidate, Path.GetRelativePath(extracted, file)), overwrite: true);
                UpgradeFiles.CopyVerified(Path.Combine(candidate, InstallPayload.ExecutableName), Path.Combine(candidate, "Uninstall.exe"), overwrite: true);
                InstallPayload.Verify(candidate);
            }, relative => !LegacyProductIdentity.ExecutableNames.Contains(relative, StringComparer.OrdinalIgnoreCase));
            InstallDiscovery.EnsureIdle(roots, Environment.ProcessId);
            transaction.Activate();
            await RunProbeAsync().ConfigureAwait(false);
            await registration.ApplyAsync().ConfigureAwait(false);
            await channel.SendAsync("installed").ConfigureAwait(false);
            string pidText = await channel.ExpectAsync("healthy", TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            {
                if (!int.TryParse(pidText, out int pid)) throw new IOException("Invalid first-launch confirmation.");
                using Process app = Process.GetProcessById(pid);
                if (app.HasExited || !string.Equals(app.MainModule?.FileName, DeploymentFootprint.InstallPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The new installed app did not confirm a successful startup.");
            }
            transaction.Confirm(DateTimeOffset.UtcNow);
            try { await registration.RemoveLegacyAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { RuntimeLog.Fail("Upgrade cleanup", cleanup.ToString()); }
            await channel.SendAsync("committed").ConfigureAwait(false);
            await MaintainAsync(ownsInstallerGate: true).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Upgrade", ex.ToString());
            try
            {
                if (transaction?.Phase != "Committed")
                {
                    transaction?.Rollback();
                    if (registration != null) await registration.RestoreAsync().ConfigureAwait(false);
                }
            }
            catch (Exception rollback)
            {
                RuntimeLog.Fail("Upgrade rollback", rollback.ToString());
                ex = new IOException($"Update stopped. Your backup is preserved at {transaction?.DirectoryPath}. Rollback needs another install attempt: {rollback.Message}", ex);
            }
            try { await channel.SendAsync("error", ex.Message).ConfigureAwait(false); }
            catch (Exception connection) { RuntimeLog.Swallowed(connection); }
            return 1;
        }
        finally
        {
            if (ownsLegacy) legacyGate.Release();
            if (ownsGate) gate.Release();
        }
    }

    private static async Task RunProbeAsync()
    {
        using var process = Process.Start(new ProcessStartInfo(DeploymentFootprint.InstallPath, "--upgrade-probe")
            { UseShellExecute = false, CreateNoWindow = true }) ?? throw new IOException("Could not verify the new executable.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new IOException("The new executable did not finish its installation check.");
        }
        if (process.ExitCode != 0) throw new IOException("The new executable failed its installation check.");
    }

    internal static int Probe()
    {
        try
        {
            InstallPayload.Verify(AppContext.BaseDirectory);
            _ = System.Text.Json.JsonSerializer.Deserialize("{}", Infrastructure.SettingsJsonContext.Default.AppSettings);
            NativeHelpers.SetDllDirectory(Path.Combine(AppContext.BaseDirectory, "frontend"));
            nint library = System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "frontend", "libmpv-2.dll"));
            System.Runtime.InteropServices.NativeLibrary.Free(library);
            using var probe = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "backend", "ffprobe.exe"), "-version")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })
                ?? throw new IOException("Could not check the media runtime.");
            _ = probe.StandardOutput.ReadToEndAsync();
            _ = probe.StandardError.ReadToEndAsync();
            if (!probe.WaitForExit(30000)) { probe.Kill(); throw new IOException("The media runtime check timed out."); }
            if (probe.ExitCode != 0) throw new IOException("The media runtime check failed.");
            return 0;
        }
        catch (Exception ex) { RuntimeLog.Fail("Upgrade probe", ex.ToString()); return 1; }
    }

    private static IEnumerable<(DirectoryUpgrade Transaction, string[] Roots)> Transactions()
    {
        if (!Directory.Exists(InstallDiscovery.Store)) yield break;
        UpgradeFiles.RequirePlainPath(InstallDiscovery.Store);
        foreach (string directory in Directory.EnumerateDirectories(InstallDiscovery.Store))
        {
            string path = Path.Combine(directory, "journal.json");
            if (!File.Exists(path)) continue;
            JsonObject journal = AtomicJsonFile.ReadObject(path) ?? throw new IOException("An installation recovery record is unreadable. Backups were preserved.");
            var roots = journal["Originals"]!.AsArray().Select(x => x!["Path"]!.GetValue<string>())
                .Append(InstallDiscovery.Destination).ToArray();
            if (journal["Destination"]!.GetValue<string>() != InstallDiscovery.Destination)
                throw new IOException("Unexpected destination in protected installation journal.");
            yield return (DirectoryUpgrade.Open(directory, roots), roots);
        }
    }

    private static async Task RecoverInterruptedAsync()
    {
        foreach (var (transaction, roots) in Transactions())
        {
            if (transaction.Phase is "Committed" or "Pruned" or "RolledBack") continue;
            InstallDiscovery.EnsureIdle(roots, Environment.ProcessId);
            transaction.Rollback();
            await new UpgradeRegistration(true, transaction.DirectoryPath, roots).RestoreAsync().ConfigureAwait(false);
        }
    }

    public static async Task<int> MaintainAsync(bool ownsInstallerGate = false)
    {
        if (!IsElevated()) return 2;
        using var gate = new Semaphore(1, 1, DeploymentFootprint.InstallerGateName);
        if (!ownsInstallerGate && !gate.WaitOne(0)) return 2;
        try
        {
            foreach (var (transaction, roots) in Transactions())
            {
                if (transaction.Phase is "Committed" or "Pruned")
                {
                    await new UpgradeRegistration(true, transaction.DirectoryPath, roots).RemoveLegacyAsync().ConfigureAwait(false);
                    UpgradeFiles.DeleteTree(Path.Combine(transaction.DirectoryPath, "payload"), transaction.DirectoryPath);
                }
                transaction.PruneBackup(DateTimeOffset.UtcNow);
            }
            string xml = await TaskCommandAsync("/Query", "/TN", LegacyProductIdentity.CompactName, "/XML").ConfigureAwait(false);
            var oldExecutables = Transactions().SelectMany(x => x.Roots)
                .SelectMany(root => LegacyProductIdentity.ExecutableNames.Select(name => Path.Combine(root, name)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(xml) && System.Xml.Linq.XDocument.Parse(xml).Descendants()
                    .Where(x => x.Name.LocalName == "Command").Any(x => oldExecutables.Contains(x.Value.Trim('"'))))
                _ = await TaskCommandAsync("/Delete", "/TN", LegacyProductIdentity.CompactName, "/F").ConfigureAwait(false);
            _ = await TaskCommandAsync("/Create", "/TN", DeploymentFootprint.ScheduledTaskName, "/SC", "DAILY", "/ST", "12:00",
                "/RU", "SYSTEM", "/RL", "HIGHEST", "/TR", $"\"{DeploymentFootprint.InstallPath}\" --upgrade-maintenance", "/F").ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) { RuntimeLog.WarnThrottled("Upgrade maintenance", ex.Message); return 1; }
        finally { if (!ownsInstallerGate) gate.Release(); }
    }

    private static async Task<string> TaskCommandAsync(params string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("schtasks.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        string error = await errors.ConfigureAwait(false);
        if (process.ExitCode != 0) RuntimeLog.Info("Upgrade task", error);
        return await output.ConfigureAwait(false);
    }
}
