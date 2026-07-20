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

// NOTE: a native vectored-exception handler was intentionally REMOVED here. .NET uses hardware
// access violations (0xC0000005) internally for implicit null-reference checks, so a process-wide
// VEH fires on ordinary managed execution; doing managed work inside it corrupts the runtime
// (ExecutionEngineException / 0x80131506). Native crashes are captured safely and retroactively
// by the startup Event Viewer digest (CrashLogDigest) instead.

AppDomain.CurrentDomain.UnhandledException += (s, e) =>
{
    string detail = e.ExceptionObject is Exception ex
        ? $"{ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex}"
        : $"Non-Exception object: {e.ExceptionObject}";
    // Synchronous, queue-bypassing write FIRST so the line survives even if the process is
    // terminating faster than the async log queue can drain; then the normal path (which also
    // feeds the live on-screen log viewer).
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
RuntimeLog.Info("PROCESS START", $"pid={Environment.ProcessId}; exe={Environment.ProcessPath}; args={string.Join(" ", args)}");

// Fold any Windows Event Viewer crash entries for this app (native fast-fails that never
// reached our log) into the .log file. Main-app launch only, so the 3 sibling processes
// don't each import the same events. Fire-and-forget — never delays startup.
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
RuntimeLog.Info("PROCESS EXIT", $"exitCode={exitCode}");
return exitCode;

static async Task<int> RunAsync(string[] args)
{
    string command = args.FirstOrDefault() ?? "run-ui";

    // Windows Explorer "Open With" / file-association launches pass the video file
    // path as the first argument. Without this check the path fell through to the
    // command switch below, hit PrintUsage and the process exited silently
    // ("nothing happens"). Detect it, stash it, and boot the normal UI instead.
    if (OpenWithLaunch.IsVideoFilePath(command))
    {
        OpenWithLaunch.PendingVideoPath = System.IO.Path.GetFullPath(command);
        RuntimeLog.Info("OPEN WITH", $"Launched with video file argument: {OpenWithLaunch.PendingVideoPath}");
        return await RunUiAsync(args);
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
    RuntimeLog.Success("PATHS RESOLVED", $"programData={paths.ProgramDataRoot}; tempLog={RuntimeLog.LogPath}");

    RuntimeLog.Info("BOOTSTRAP", "Ensuring writable ProgramData directories.");
    paths.EnsureWritableDirectories();
    RuntimeLog.Success("PROGRAMDATA READY", $"logs={paths.LogsDirectory}; temp={paths.TempDirectory}");

    RuntimeLog.Info("BOOTSTRAP", "Loading session_state.json through named mutex.");
    JsonObject state = await new StateTransferStore(paths).LoadAsync();
    RuntimeLog.Success("SESSION STATE READY", $"path={paths.SessionStateFile}; properties={state.Count}");

    RuntimeLog.Info("BOOTSTRAP", "Loading crops_coordinations.conf through named mutex.");
    JsonObject crops = await new CropConfigStore(paths).LoadAsync();
    RuntimeLog.Success("CROP CONFIG READY", $"path={paths.CropCoordinatesFile}; schema={crops["schema_version"]}");

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
    RuntimeLog.Info("WRITE STATE", $"source={source}");
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
        string uiTempFolder = Path.Combine(ApplicationPaths.CreateDefault().TempDirectory, "FortniteVideoSoftware_SetupUI_" + Environment.ProcessId);
        Directory.CreateDirectory(uiTempFolder);
        DeploymentLifecycle.ExtractAvaloniaDependencies(uiTempFolder);
        NativeHelpers.SetDllDirectory(uiTempFolder);
    }

    RuntimeLog.Info("RUN UI", "Running bootstrapper before UI.");
    await BootstrapAsync(showDialog: false);
    
    RuntimeLog.Info("RUN UI", "Starting Avalonia App.");

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