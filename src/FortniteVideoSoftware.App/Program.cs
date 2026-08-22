using System.Text.Json.Nodes;
using FortniteVideoSoftware.App;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Ipc;
using Avalonia;

if (DeploymentLifecycle.ShouldHandle(args))
{
    return await DeploymentLifecycle.RunAsync(args);
}

FortniteVideoSoftware.Core.Infrastructure.CoreLogger.InfoAction = RuntimeLog.Info;
FortniteVideoSoftware.Core.Infrastructure.CoreLogger.FailAction = RuntimeLog.Fail;
FortniteVideoSoftware.Core.Infrastructure.CoreLogger.DebugAction = RuntimeLog.Debug;
FortniteVideoSoftware.Core.Infrastructure.CoreLogger.AppendAction = RuntimeLog.AppendRaw;
RuntimeLog.InitializeAppName(args);
RuntimeLog.ResetForProcess();


AppDomain.CurrentDomain.UnhandledException += (s, e) =>
{
    string detail = e.ExceptionObject is Exception ex
        ? $"{ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex}"
        : $"Non-Exception object: {e.ExceptionObject}";
    RuntimeLog.EmergencyWrite("FATAL UNHANDLED", detail);
    RuntimeLog.Fail("FATAL UNHANDLED", detail);
};

TaskScheduler.UnobservedTaskException += (s, e) =>
{
    if (e.Exception?.ToString().Contains("OpenSharedResource failed") == true)
    {
        RuntimeLog.Info("MPV-Interop", "Ignored benign OpenSharedResource Avalonia rendering exception on finalizer thread.");
    }
    else
    {
        RuntimeLog.Fail("FATAL UNOBSERVED TASK", e.Exception ?? new Exception("Unknown unobserved task exception"));
    }
    e.SetObserved();
};
RuntimeLog.Info("PROCESS START", $"pid={Environment.ProcessId}; exe={System.IO.Path.GetFileName(Environment.ProcessPath ?? "FortniteVideoSoftware.exe")}; args_count={args.Length}");
RuntimeLog.Debug("PROCESS START", $"exe={Environment.ProcessPath}; args={string.Join(" ", args)}");

bool isSiblingProcess = args.Any(a =>
    a.Equals("--merger", StringComparison.OrdinalIgnoreCase) ||
    a.Equals("--crop-tool", StringComparison.OrdinalIgnoreCase) ||
    a.Equals("--install-worker", StringComparison.OrdinalIgnoreCase) ||
    a.Equals("--cleanup-worker", StringComparison.OrdinalIgnoreCase));
if (!isSiblingProcess)
{
    _ = CrashLogDigest.RunAsync();
}

string baseDir = System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory;
NativeHelpers.SetDllDirectory(Path.Combine(baseDir, "frontend"));


int exitCode = await RunAsync(args);
SingleInstanceGuard.Release();
RuntimeLog.Info("PROCESS EXIT", $"exitCode={exitCode}");
return exitCode;

static async Task<int> RunAsync(string[] args)
{
    string command = args.FirstOrDefault() ?? "run-ui";

    if (OpenWithLaunch.IsVideoFilePath(command))
    {
        OpenWithLaunch.PendingVideoPath = System.IO.Path.GetFullPath(command);
        RuntimeLog.Info("OPEN WITH", $"Launched with video file argument: {System.IO.Path.GetFileName(OpenWithLaunch.PendingVideoPath)}");
        RuntimeLog.Debug("OPEN WITH", $"Full launched video path: {OpenWithLaunch.PendingVideoPath}");

        if (!SingleInstanceGuard.TryAcquire(args))
        {
            RuntimeLog.Info("OPEN WITH", "Video path handed to the already-running instance; this process is exiting.");
            return 0;
        }

        return await RunUiAsync(args);
    }

    if (command == "run-ui" && SingleInstanceGuard.AppliesTo(args) && !SingleInstanceGuard.TryAcquire(args))
    {
        RuntimeLog.Info("SingleInstance", "Focused the running instance; this process is exiting.");
        return 0;
    }

    try
    {
        RuntimeLog.Info("COMMAND START", command);
        return command switch
        {
            "run-ui" => await RunUiAsync(args),
            "bootstrap" => await BootstrapAsync(showDialog: true),
            "paths" => PrintPaths(),
            "read-state" => await ReadStateAsync(),
            "write-state" => await WriteStateAsync(args.Skip(1).ToArray()),
            "clear-state" => await ClearStateAsync(),
            "read-crops" => await ReadCropsAsync(),
            "write-crops-defaults" => await WriteCropsDefaultsAsync(),
            "phase1-gate" => await Phase1Gate.RunAsync(),
            "phase1-worker" => await Phase1Gate.RunWorkerAsync(args.Skip(1).ToArray()),
            "phase2-crash" => await Phase2Gate.SimulateCrashAsync(),
            "phase2-check" => await Phase2Gate.CheckRecoveryAsync(),
            "phase3-gate" => await Phase3Gate.RunAsync(),
            "phase4-gate" => await Phase4Gate.RunAsync(),
            "--install" => await RunUiAsync(args),
            "--install-worker" => await RunUiAsync(args),
            "--uninstall" => await RunUiAsync(args),
            "--cleanup-worker" => await RunUiAsync(args),
            "--crop-tool" => await RunUiAsync(args),
            "--merger" => await RunUiAsync(args),
            _ => PrintUsage(command)
        };
    }
    catch (Exception ex)
    {
        RuntimeLog.Fail("UNHANDLED EXCEPTION", ex);
        if (args.Length == 0)
        {
            NativeDialog.ShowError(
                "Fortnite Video Software failed during startup." + Environment.NewLine +
                $"Log: {RuntimeLog.LogPath}" + Environment.NewLine + Environment.NewLine +
                ex.Message);
        }

        Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
        return 1;
    }
}

static async Task<int> BootstrapAsync(bool showDialog)
{
    RuntimeLog.Info("BOOTSTRAP", "Resolving application paths.");
    ApplicationPaths paths = ApplicationPaths.CreateDefault();
    RuntimeLog.Success("PATHS RESOLVED", "ProgramData, temp, and log paths resolved.");
    RuntimeLog.Debug("PATHS RESOLVED", $"programData={paths.ProgramDataRoot}; tempLog={RuntimeLog.LogPath}");

    RuntimeLog.Info("BOOTSTRAP", "Ensuring writable ProgramData directories.");
    paths.EnsureWritableDirectories();
    RuntimeLog.Success("PROGRAMDATA READY", "Writable ProgramData directories are ready.");
    RuntimeLog.Debug("PROGRAMDATA READY", $"logs={paths.LogsDirectory}; temp={paths.TempDirectory}");

    RuntimeLog.Info("BOOTSTRAP", "Loading session_state.json through named mutex.");
    JsonObject state = await new StateTransferStore(paths).LoadAsync();
    RuntimeLog.Success("SESSION STATE READY", $"properties={state.Count}");
    RuntimeLog.Debug("SESSION STATE READY", $"path={paths.SessionStateFile}; properties={state.Count}");

    RuntimeLog.Info("BOOTSTRAP", "Loading crops_coordinations.conf through named mutex.");
    JsonObject crops = await new CropConfigStore(paths).LoadAsync();
    RuntimeLog.Success("CROP CONFIG READY", $"schema={crops["schema_version"]}");
    RuntimeLog.Debug("CROP CONFIG READY", $"path={paths.CropCoordinatesFile}; schema={crops["schema_version"]}");

    RuntimeLog.Info("BOOTSTRAP", "Checking Phase 1 executable health.");
    RuntimeLog.Success("BOOTSTRAP COMPLETE", "Core IPC/config layer is available. Avalonia UI is ready.");

    if (showDialog)
    {
        NativeDialog.ShowInfo(
            "Fortnite Video Software core bootstrap completed." + Environment.NewLine +
            $"Log: {RuntimeLog.LogPath}");
    }

    return 0;
}

static int PrintPaths()
{
    RuntimeLog.Info("PATHS", "Printing resolved paths.");
    ApplicationPaths paths = ApplicationPaths.CreateDefault();
    paths.EnsureWritableDirectories();

    Console.WriteLine($"programDataRoot={paths.ProgramDataRoot}");
    Console.WriteLine($"sessionState={paths.SessionStateFile}");
    Console.WriteLine($"cropCoordinates={paths.CropCoordinatesFile}");
    Console.WriteLine($"logs={paths.LogsDirectory}");
    Console.WriteLine($"temp={paths.TempDirectory}");
    Console.WriteLine($"installerReport={paths.InstallerReportFile}");
    RuntimeLog.Success("PATHS", "Resolved paths printed successfully.");
    return 0;
}

static async Task<int> ReadStateAsync()
{
    RuntimeLog.Info("READ STATE", "Loading session state.");
    JsonObject state = await new StateTransferStore().LoadAsync();
    Console.WriteLine(state.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    RuntimeLog.Success("READ STATE", $"properties={state.Count}");
    return 0;
}

static async Task<int> WriteStateAsync(string[] args)
{
    string source = args.FirstOrDefault() ?? "manual";
    RuntimeLog.Info("WRITE STATE", $"source={System.IO.Path.GetFileName(source)}");
    RuntimeLog.Debug("WRITE STATE", $"source={source}");
    JsonObject payload = new()
    {
        ["source"] = source,
        ["pid"] = Environment.ProcessId,
        ["written_utc"] = DateTimeOffset.UtcNow.ToString("O")
    };

    await new StateTransferStore().SaveAsync(payload);
    Console.WriteLine("state written");
    RuntimeLog.Success("WRITE STATE", "session_state.json written successfully.");
    return 0;
}

static async Task<int> ClearStateAsync()
{
    RuntimeLog.Info("CLEAR STATE", "Deleting session state.");
    await new StateTransferStore().ClearAsync();
    Console.WriteLine("state cleared");
    RuntimeLog.Success("CLEAR STATE", "session_state.json cleared successfully.");
    return 0;
}

static async Task<int> ReadCropsAsync()
{
    RuntimeLog.Info("READ CROPS", "Loading crop config.");
    JsonObject config = await new CropConfigStore().LoadAsync();
    Console.WriteLine(config.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    RuntimeLog.Success("READ CROPS", $"schema={config["schema_version"]}");
    return 0;
}

static async Task<int> WriteCropsDefaultsAsync()
{
    RuntimeLog.Info("WRITE CROPS", "Writing default crop config.");
    await new CropConfigStore().SaveAsync(CropConfigDefaults.Create());
    Console.WriteLine("crop defaults written");
    RuntimeLog.Success("WRITE CROPS", "crops_coordinations.conf defaults written successfully.");
    return 0;
}

static int PrintUsage(string command)
{
    RuntimeLog.Fail("UNKNOWN COMMAND", command);
    Console.Error.WriteLine($"Unknown command: {command}");
    Console.Error.WriteLine("Commands: bootstrap, paths, read-state, write-state, clear-state, read-crops, write-crops-defaults, phase1-gate, phase2-crash, phase2-check, run-ui");
    return 2;
}

static async Task<int> RunUiAsync(string[] args)
{
    bool isWorker = args.Any(a => a.Equals("--install-worker", StringComparison.OrdinalIgnoreCase) || 
                                  a.Equals("--cleanup-worker", StringComparison.OrdinalIgnoreCase));
    if (isWorker)
    {
        var workerPaths = ApplicationPaths.CreateDefault();
        string uiTempFolder = Path.Combine(workerPaths.TempDirectory, "FortniteVideoSoftware_SetupUI_" + Environment.ProcessId);

        PurgeStaleSetupUiFolders(workerPaths.TempDirectory, uiTempFolder);

        Directory.CreateDirectory(uiTempFolder);
        DeploymentLifecycle.ExtractAvaloniaDependencies(uiTempFolder);
        NativeHelpers.SetDllDirectory(uiTempFolder);
    }

    RuntimeLog.Info("RUN UI", "Running bootstrapper before UI.");
    await BootstrapAsync(showDialog: false);

    if (OperatingSystem.IsWindows())
        ShellFileAssociation.EnsureRegistered();
    
    RuntimeLog.Info("RUN UI", "Starting Avalonia App.");
    RuntimeLog.Info("BUILDTAG", "FVS-SWPREVIEW-BUILDCHECK-2026A :: CPU software-preview + frame-gate build ACTIVE");

    var builder = Avalonia.AppBuilder.Configure<FortniteVideoSoftware.App.AvaloniaApp>()
        .UsePlatformDetect()
        .WithInterFont();

    if (RuntimeLog.IsDevMode)
    {
        builder = builder.LogToTrace(Avalonia.Logging.LogEventLevel.Verbose);
        RuntimeLog.Info("RUN UI", "Dev mode: Avalonia verbose logging enabled.");
    }
    else
    {
        builder = builder.LogToTrace(Avalonia.Logging.LogEventLevel.Warning);
    }

    return builder.StartWithClassicDesktopLifetime(args);
}

static void PurgeStaleSetupUiFolders(string tempRoot, string keepFolder)
{
    const string prefix = "FortniteVideoSoftware_SetupUI_";
    try
    {
        if (!Directory.Exists(tempRoot)) return;

        string currentName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

        foreach (string folder in Directory.GetDirectories(tempRoot, prefix + "*"))
        {
            try
            {
                if (string.Equals(Path.GetFullPath(folder), Path.GetFullPath(keepFolder), StringComparison.OrdinalIgnoreCase))
                    continue;

                string suffix = Path.GetFileName(folder)[prefix.Length..];
                if (int.TryParse(suffix, out int ownerPid) && IsLiveWorker(ownerPid, currentName))
                {
                    RuntimeLog.Info("TEMP CLEANUP", $"Left {Path.GetFileName(folder)} alone — its installer is still running.");
                    continue;
                }

                Directory.Delete(folder, recursive: true);
                RuntimeLog.Info("TEMP CLEANUP", $"Removed leftover installer folder {Path.GetFileName(folder)}.");
            }
            catch (Exception ex)
            {
                RuntimeLog.Debug("TEMP CLEANUP", $"Could not remove {folder}: {ex.Message}");
            }
        }
    }
    catch (Exception ex)
    {
        RuntimeLog.Debug("TEMP CLEANUP", $"Sweep skipped: {ex.Message}");
    }

    static bool IsLiveWorker(int pid, string currentName)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return string.Equals(p.ProcessName, currentName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

internal static class NativeHelpers
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    public static extern bool SetDllDirectory(string lpPathName);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr AddDllDirectory(string newDirectory);
}


public partial class Program
{
    [System.Runtime.InteropServices.UnmanagedCallersOnly(EntryPoint = "NvOptimusEnablement")]
    public static uint NvOptimusEnablement() => 1;

    [System.Runtime.InteropServices.UnmanagedCallersOnly(EntryPoint = "AmdPowerXpressRequestHighPerformance")]
    public static int AmdPowerXpressRequestHighPerformance() => 1;
}
