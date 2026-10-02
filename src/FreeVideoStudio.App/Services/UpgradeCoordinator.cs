
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

/// <summary>UPGRADE_10 — the original user's broker owns settings and first-launch confirmation.
/// Only the worker writes Program Files. A protected machine journal is the commit decision
/// for all participating user-data journals, including recovery after loss of the broker.</summary>
internal static class UpgradeCoordinator
{
    private static string Store => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeVideoStudioMigration");
    private static string SessionPath => Path.Combine(Store, "session.json");
    private static string UserGate => @"Local\FreeVideoStudio_UserMigration_" + WindowsIdentity.GetCurrent().User!.Value;

    public static bool HasPendingSession => File.Exists(SessionPath) &&
        AtomicJsonFile.ReadObject(SessionPath)?["phase"]?.GetValue<string>() == "Pending";

    public static bool HasPendingMachine => Directory.Exists(InstallDiscovery.Store) &&
        Directory.EnumerateDirectories(InstallDiscovery.Store).Any(p =>
            File.Exists(Path.Combine(p, "journal.json")) &&
            AtomicJsonFile.ReadObject(Path.Combine(p, "journal.json"))?["Phase"]?.GetValue<string>() is
                "Switching" or "AwaitingConfirmation" or "RollingBack");

    public static async Task<int> LaunchAsync(string[] args, bool recovery = false)
    {
        string stage = Path.Combine(Path.GetTempPath(), "FVS_Upgrade", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        string executable = Path.Combine(stage, InstallPayload.ExecutableName);
        UpgradeFiles.CopyVerified(Environment.ProcessPath!, executable);
        foreach (string dll in new[] { "libSkiaSharp.dll", "libHarfBuzzSharp.dll" })
        {
            string source = Path.Combine(AppContext.BaseDirectory, dll);
            if (File.Exists(source)) UpgradeFiles.CopyVerified(source, Path.Combine(stage, dll));
        }
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--upgrade-broker");
        if (recovery) start.ArgumentList.Add("--recover-only");
        foreach (string arg in args) start.ArgumentList.Add(arg);
        if (DeploymentFootprint.IsRunningFromInstallPath() && !args.Contains("--wait-pid"))
        {
            start.ArgumentList.Add("--wait-pid"); start.ArgumentList.Add(Environment.ProcessId.ToString());
        }
        using var process = Process.Start(start) ?? throw new IOException("Could not start the update.");
        if (DeploymentFootprint.IsRunningFromInstallPath()) return 0;
        await process.WaitForExitAsync().ConfigureAwait(false);
        try { UpgradeFiles.DeleteTree(stage, Path.GetDirectoryName(stage)!); }
        catch (IOException ex) { RuntimeLog.Swallowed(ex); }
        catch (UnauthorizedAccessException ex) { RuntimeLog.Swallowed(ex); }
        return process.ExitCode;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        using var gate = new Semaphore(1, 1, UserGate);
        if (!gate.WaitOne(0)) return 2;
        Process? worker = null, app = null;
        UpgradeChannel? channel = null, health = null;
        UserDataUpgrade? data = null;
        JsonObject? session = null;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var healthListener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            if (UpgradeInstallWorker.IsElevated())
                throw new IOException("Please run the installer normally, without 'Run as administrator'. Windows will ask for permission when needed.");
            await WaitForSourceExitAsync(args).ConfigureAwait(false);
            bool recovery = args.Contains("--recover-only", StringComparer.OrdinalIgnoreCase);
            listener.Start();
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            start.ArgumentList.Add("--upgrade-worker");
            start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(token);
            worker = Process.Start(start) ?? throw new IOException("Windows did not start the installer.");
            channel = await AcceptAsync(listener, token).ConfigureAwait(false);
            await channel.SendAsync("mode", recovery ? "recover" : "install").ConfigureAwait(false);
            if (recovery)
            {
                await channel.ExpectAsync("recovered").ConfigureAwait(false);
                await RecoverUserSessionAsync().ConfigureAwait(false);
                StartApp();
                return 0;
            }

            JsonObject ready = JsonNode.Parse(await channel.ExpectAsync("ready").ConfigureAwait(false))!.AsObject();
            string machine = ready["transaction"]!.GetValue<string>();
            RequireMachineJournal(machine);
            string[] roots = ready["roots"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
            await RecoverUserSessionAsync().ConfigureAwait(false);
            bool preferLegacy = !args.Contains("--source-current", StringComparer.OrdinalIgnoreCase) &&
                roots.Any(p => !p.Equals(InstallDiscovery.Destination, StringComparison.OrdinalIgnoreCase));
            Directory.CreateDirectory(Store);
            string shellBackup = Path.Combine(Store, "shell-" + Guid.NewGuid().ToString("N"));
            var shell = new UpgradeRegistration(false, shellBackup, roots);
            await shell.CaptureAsync().ConfigureAwait(false);
            session = new JsonObject
            {
                ["phase"] = "Pending", ["machine"] = machine, ["shell"] = shellBackup,
                ["roots"] = new JsonArray(roots.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                ["transactions"] = new JsonArray()
            };
            Save(session);
            data = UserDataUpgrade.Begin(UserDataUpgrade.CurrentRoots(), UserDataUpgrade.LocalRoot,
                preferLegacy: preferLegacy, prepared: transaction =>
                {
                    session["transactions"]!.AsArray().AddNode((JsonNode?)JsonValue.Create(transaction.DirectoryPath));
                    Save(session);
                });
            ValidateSettings();
            session["userReady"] = true; Save(session);
            await channel.SendAsync("begin").ConfigureAwait(false);
            await channel.ExpectAsync("installed").ConfigureAwait(false);
            await TryApplyShellAsync(shell, session).ConfigureAwait(false);

            healthListener.Start();
            string healthToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            app = StartApp(((IPEndPoint)healthListener.LocalEndpoint).Port, healthToken);
            health = await AcceptAsync(healthListener, healthToken).ConfigureAwait(false);
            string pid = await health.ExpectAsync("healthy").ConfigureAwait(false);
            if (pid != app.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) || app.HasExited)
                throw new IOException("The new app did not confirm its startup.");
            await channel.SendAsync("healthy", pid).ConfigureAwait(false);
            await channel.ExpectAsync("committed").ConfigureAwait(false);
            data.Confirm();
            session["phase"] = "Committed";
            Save(session);
            await TryRemoveShellAsync(shell, session).ConfigureAwait(false);
            await health.SendAsync("committed").ConfigureAwait(false);
            if (args.Contains("--no-launch", StringComparer.OrdinalIgnoreCase)) app.CloseMainWindow();
            return 0;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Upgrade", ex.ToString());
            bool committed = session?["machine"] is JsonValue m && IsMachineCommitted(m.GetValue<string>());
            if (!committed && app is { HasExited: false })
            {
                app.Kill(entireProcessTree: true);
                await app.WaitForExitAsync().ConfigureAwait(false);
            }
            channel?.Dispose(); channel = null;
            if (worker is { HasExited: false })
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                try { await worker.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException wait) { RuntimeLog.Swallowed(wait); }
            }
            if (worker == null || worker.HasExited)
            {
                try { await RecoverUserSessionAsync().ConfigureAwait(false); }
                catch (Exception restore) { RuntimeLog.Fail("Upgrade recovery", restore.ToString()); }
            }
            if (committed && health != null)
            {
                try { await health.SendAsync("committed").ConfigureAwait(false); }
                catch (Exception notify) { RuntimeLog.Swallowed(notify); }
            }
            NativeDialog.ShowError(committed
                ? "The new app is installed. Some cleanup will be retried when it next starts."
                : "The update could not finish. Your recovery backup has been kept.\n\n" + ex.Message +
                  "\n\nRun the installer again to retry or finish recovery.", "Free Video Studio Update");
            return 1;
        }
        finally
        {
            health?.Dispose(); channel?.Dispose(); worker?.Dispose(); app?.Dispose();
            listener.Stop(); healthListener.Stop(); gate.Release();
        }
    }

    internal static void ValidateSettings()
    {
        ValidateSettingsFile(Path.Combine(UserDataUpgrade.LocalRoot, "settings.json"));
    }

    internal static void ValidateSettingsFile(string path)
    {
        if (!File.Exists(path)) return;
        JsonObject json = AtomicJsonFile.ReadObject(path) ?? throw new IOException("Saved settings are unreadable. They have been preserved.");
        if (json["SchemaVersion"]?.GetValue<int>() > Infrastructure.SettingsManager.CurrentSchemaVersion)
            throw new IOException("These settings need a newer app version. They have been preserved.");
        _ = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(path), Infrastructure.SettingsJsonContext.Default.AppSettings)
            ?? throw new IOException("The saved settings could not be read. They have been preserved.");
    }

    private static void RequireMachineJournal(string directory)
    {
        if (!UpgradeFiles.IsWithin(directory, InstallDiscovery.Store) || Path.GetDirectoryName(directory) != InstallDiscovery.Store)
            throw new IOException("Unexpected installation recovery location.");
        UpgradeFiles.RequirePlainPath(directory);
    }

    private static bool IsMachineCommitted(string directory)
    {
        RequireMachineJournal(directory);
        return File.Exists(Path.Combine(directory, "journal.json")) && DirectoryUpgrade.IsCommitted(directory);
    }

    public static async Task RecoverUserSessionAsync()
    {
        if (!File.Exists(SessionPath)) return;
        JsonObject session = AtomicJsonFile.ReadObject(SessionPath) ?? throw new IOException("The migration record is unreadable. Recovery backups have been preserved.");
        if (session["phase"]?.GetValue<string>() != "Pending") return;
        bool committed = IsMachineCommitted(session["machine"]!.GetValue<string>()) && session["userReady"]?.GetValue<bool>() == true;
        foreach (JsonNode? node in session["transactions"]!.AsArray().Reverse())
        {
            string directory = node!.GetValue<string>();
            UserUpgradeRoot root = UserDataUpgrade.CurrentRoots().Single(r =>
                string.Equals(Path.GetDirectoryName(directory), r.Store, StringComparison.OrdinalIgnoreCase));
            DirectoryUpgrade transaction = DirectoryUpgrade.Open(directory, root.LegacyRoots.Append(root.Destination));
            if (committed) transaction.Confirm(DateTimeOffset.UtcNow);
            else transaction.Rollback();
        }
        if (!committed) await ShellFromSession(session).RestoreAsync().ConfigureAwait(false);
        session["phase"] = committed ? "Committed" : "RolledBack";
        Save(session);
    }

    private static UpgradeRegistration ShellFromSession(JsonObject session)
    {
        string path = session["shell"]!.GetValue<string>();
        if (Path.GetDirectoryName(path) != Store) throw new IOException("Unexpected shortcut recovery location.");
        return new UpgradeRegistration(false, path,
            session["roots"]!.AsArray().Select(p => p!.GetValue<string>()).ToArray());
    }

    public static async Task CompleteUserAsync()
    {
        using var gate = new Semaphore(1, 1, UserGate);
        if (!gate.WaitOne(0)) throw new IOException("An update is finishing for this Windows account. Please try opening the app again shortly.");
        try
        {
            await RecoverUserSessionAsync().ConfigureAwait(false);
            if (File.Exists(SessionPath) && AtomicJsonFile.ReadObject(SessionPath)?["phase"]?.GetValue<string>() == "Committed")
            {
                JsonObject session = AtomicJsonFile.ReadObject(SessionPath) ?? throw new IOException("The migration record is unreadable.");
                if (session["phase"]?.GetValue<string>() == "Committed" && session["shellPending"]?.GetValue<bool>() == true)
                {
                    var shell = ShellFromSession(session);
                    await TryApplyShellAsync(shell, session).ConfigureAwait(false);
                    await TryRemoveShellAsync(shell, session).ConfigureAwait(false);
                }
            }
            else
            {
                string? machine = Directory.Exists(InstallDiscovery.Store)
                    ? Directory.EnumerateDirectories(InstallDiscovery.Store)
                        .Where(p => File.Exists(Path.Combine(p, "journal.json")) && DirectoryUpgrade.IsCommitted(p))
                        .OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault() : null;
                if (machine != null)
                {
                    var journal = AtomicJsonFile.ReadObject(Path.Combine(machine, "journal.json"))!;
                    string[] roots = journal["Originals"]!.AsArray().Select(x => x!["Path"]!.GetValue<string>())
                        .Append(InstallDiscovery.Destination).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    InstallDiscovery.EnsureIdle(roots, Environment.ProcessId);
                    string shellPath = Path.Combine(Store, "shell-" + Guid.NewGuid().ToString("N"));
                    var shell = new UpgradeRegistration(false, shellPath, roots);
                    await shell.CaptureAsync().ConfigureAwait(false);
                    var session = new JsonObject
                    {
                        ["phase"] = "Pending", ["machine"] = machine, ["shell"] = shellPath,
                        ["roots"] = new JsonArray(roots.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                        ["transactions"] = new JsonArray()
                    };
                    Save(session);
                    try
                    {
                        var data = UserDataUpgrade.Begin(UserDataUpgrade.CurrentRoots(), UserDataUpgrade.LocalRoot,
                            prepared: transaction => { session["transactions"]!.AsArray().AddNode((JsonNode?)JsonValue.Create(transaction.DirectoryPath)); Save(session); });
                        ValidateSettings();
                        session["userReady"] = true; Save(session);
                        data.Confirm();
                        session["phase"] = "Committed"; Save(session);
                        await TryApplyShellAsync(shell, session).ConfigureAwait(false);
                        await TryRemoveShellAsync(shell, session).ConfigureAwait(false);
                    }
                    catch { await RecoverUserSessionAsync().ConfigureAwait(false); throw; }
                }
            }
            UserDataUpgrade.RecoverInterrupted(UserDataUpgrade.CurrentRoots());
        }
        finally { gate.Release(); }
    }

    private static async Task TryApplyShellAsync(UpgradeRegistration shell, JsonObject session)
    {
        session["shellPending"] = true; Save(session);
        session["shellApplied"] = false; Save(session);
        try { await shell.ApplyAsync().ConfigureAwait(false); session["shellApplied"] = true; Save(session); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { RuntimeLog.WarnThrottled("Upgrade shortcuts", ex.Message); }
    }

    private static async Task TryRemoveShellAsync(UpgradeRegistration shell, JsonObject session)
    {
        try
        {
            if (session["shellApplied"]?.GetValue<bool>() != true) return;
            await shell.RemoveLegacyAsync().ConfigureAwait(false);
            session["shellPending"] = !shell.DesktopAvailable; Save(session);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { session["shellPending"] = true; Save(session); RuntimeLog.WarnThrottled("Upgrade shortcuts", ex.Message); }
    }

    private static void Save(JsonObject session) => AtomicJsonFile.WriteObject(SessionPath, session);

    private static Process StartApp(int? port = null, string? token = null)
    {
        var start = new ProcessStartInfo(DeploymentFootprint.InstallPath) { UseShellExecute = false, WorkingDirectory = InstallDiscovery.Destination };
        start.ArgumentList.Add("run-ui");
        if (port.HasValue)
        {
            start.ArgumentList.Add("--upgrade-health"); start.ArgumentList.Add(port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(token!);
        }
        return Process.Start(start) ?? throw new IOException("Could not start the installed app.");
    }

    private static async Task<UpgradeChannel> AcceptAsync(TcpListener listener, string token)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        while (true)
        {
            var candidate = new UpgradeChannel(await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false));
            try
            {
                if (await candidate.ExpectAsync("hello", TimeSpan.FromSeconds(5)).ConfigureAwait(false) == token) return candidate;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { RuntimeLog.Swallowed(ex); }
            candidate.Dispose();
        }
    }

    private static async Task WaitForSourceExitAsync(string[] args)
    {
        int index = Array.IndexOf(args, "--wait-pid");
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out int pid)) return;
        try
        {
            using Process source = Process.GetProcessById(pid);
            await source.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (ArgumentException ex) { RuntimeLog.Info("Upgrade", "The source app already closed: " + ex.Message); }
    }

    public static async Task ConfirmWindowAsync(string[] args)
    {
        int index = Array.IndexOf(args, "--upgrade-health");
        if (index < 0 || index + 2 >= args.Length || !int.TryParse(args[index + 1], out int port)) return;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
        using var channel = new UpgradeChannel(client);
        await channel.SendAsync("hello", args[index + 2]).ConfigureAwait(false);
        if (Infrastructure.SettingsManager.LoadFailureMessage != null)
            throw new IOException("Saved settings did not load successfully.");
        await channel.SendAsync("healthy", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await channel.ExpectAsync("committed").ConfigureAwait(false);
    }
}
