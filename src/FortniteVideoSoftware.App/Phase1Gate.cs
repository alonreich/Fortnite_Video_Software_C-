using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Ipc;

namespace FortniteVideoSoftware.App;

public static class Phase1Gate
{
    private const int WorkerCount = 2;
    private const int WorkerIterations = 40;

    public static async Task<int> RunAsync()
    {
        string root = CreateGateRoot();
        ResetGateRoot(root);

        ApplicationPaths paths = new(root);
        StateTransferStore stateStore = new(paths);
        CropConfigStore cropStore = new(paths);

        await stateStore.ClearAsync();
        await cropStore.SaveAsync(CropConfigDefaults.Create());

        List<Process> workers = [];
        for (int i = 0; i < WorkerCount; i++)
        {
            workers.Add(StartWorker(root, ((char)('A' + i)).ToString()));
        }

        var stdoutTasks = workers.Select(w => w.StandardOutput.ReadToEndAsync()).ToList();
        var stderrTasks = workers.Select(w => w.StandardError.ReadToEndAsync()).ToList();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
        try
        {
            foreach (Process worker in workers)
            {
                await worker.WaitForExitAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            foreach (Process worker in workers)
            {
                try { worker.Kill(true); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            }
            throw new TimeoutException("Background workers timed out after 45 seconds.");
        }

        int failures = 0;
        for (int i = 0; i < workers.Count; i++)
        {
            Process worker = workers[i];
            string stdout = await stdoutTasks[i];
            string stderr = await stderrTasks[i];

            if (worker.ExitCode != 0)
            {
                failures++;
                Console.Error.WriteLine($"worker pid={worker.Id} exit={worker.ExitCode}");
                Console.Error.WriteLine(stderr);
            }
            else if (!string.IsNullOrWhiteSpace(stdout))
            {
                Console.WriteLine(stdout.Trim());
            }

            worker.Dispose();
        }

        JsonObject state = await stateStore.LoadAsync();
        JsonObject crops = await cropStore.LoadAsync();

        Console.WriteLine($"gateRoot={root}");
        Console.WriteLine($"stateFile={paths.SessionStateFile}");
        Console.WriteLine($"cropFile={paths.CropCoordinatesFile}");
        Console.WriteLine($"stateProperties={state.Count}");
        Console.WriteLine($"cropSchema={crops["schema_version"]}");
        Console.WriteLine($"bak1Exists={File.Exists(paths.CropCoordinatesFile + ".bak1")}");

        if (failures > 0)
        {
            return 1;
        }

        for (int i = 0; i < WorkerCount; i++)
        {
            string workerKey = $"worker_{(char)('A' + i)}";
            if (!state.ContainsKey(workerKey))
            {
                Console.Error.WriteLine($"missing final state key: {workerKey}");
                return 1;
            }
        }

        return 0;
    }

    public static async Task<int> RunWorkerAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("phase1-worker requires <worker-id> <iterations>");
            return 2;
        }

        string workerId = args[0];
        int iterations = int.Parse(args[1]);

        StateTransferStore stateStore = new();
        CropConfigStore cropStore = new();

        for (int i = 0; i < iterations; i++)
        {
            JsonObject update = new()
            {
                [$"worker_{workerId}"] = i,
                [$"worker_{workerId}_pid"] = Environment.ProcessId,
                [$"worker_{workerId}_utc"] = DateTimeOffset.UtcNow.ToString("O")
            };

            await stateStore.UpdatePropertiesAsync(update);

            JsonObject config = await cropStore.LoadAsync();
            config["phase1_gate"] = new JsonObject
            {
                ["worker"] = workerId,
                ["iteration"] = i,
                ["pid"] = Environment.ProcessId
            };
            await cropStore.SaveAsync(config);
        }

        Console.WriteLine($"worker {workerId} completed {iterations} iterations");
        return 0;
    }

    private static Process StartWorker(string root, string workerId)
    {
        ProcessStartInfo startInfo = CreateStartInfo(workerId);
        startInfo.Environment[ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable] = root;

        Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException($"Failed to start phase1 worker {workerId}.");

        return process;
    }

    private static ProcessStartInfo CreateStartInfo(string workerId)
    {
        string processPath = Environment.ProcessPath ?? string.Empty;
        string assemblyPath = Path.Combine(AppContext.BaseDirectory, $"{typeof(Phase1Gate).Assembly.GetName().Name}.dll");

        ProcessStartInfo startInfo;
        if (processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            startInfo = new ProcessStartInfo(processPath)
            {
                UseShellExecute = false
            };
        }
        else
        {
            startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add("phase1-worker");
        startInfo.ArgumentList.Add(workerId);
        startInfo.ArgumentList.Add(WorkerIterations.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        return startInfo;
    }

    private static string CreateGateRoot()
    {
        string commonProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(commonProgramData))
        {
            commonProgramData = Environment.GetEnvironmentVariable("PROGRAMDATA") ?? Path.GetTempPath();
        }

        return Path.GetFullPath(Path.Combine(commonProgramData, ApplicationPaths.AppDirectoryName, "Phase1Gate"));
    }

    private static void ResetGateRoot(string root)
    {
        string allowedParent = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ApplicationPaths.AppDirectoryName));

        string fullRoot = Path.GetFullPath(root);
        if (!fullRoot.StartsWith(allowedParent, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(fullRoot) != "Phase1Gate")
        {
            throw new InvalidOperationException($"Refusing to reset unexpected gate directory: {fullRoot}");
        }

        if (Directory.Exists(fullRoot))
        {
            Directory.Delete(fullRoot, recursive: true);
        }

        Directory.CreateDirectory(fullRoot);
    }
}
