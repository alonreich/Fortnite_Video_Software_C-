using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.App;

public static class Phase2Gate
{
    public static Task<int> SimulateCrashAsync()
    {
        Console.WriteLine("Simulating crash...");
        ApplicationPaths paths = ApplicationPaths.CreateDefault();
        RecoveryManager recovery = new(paths);
        
        recovery.AcquireLock();
        // Create dummy state
        recovery.SaveState(new System.Text.Json.Nodes.JsonObject { ["status"] = "crashed" }, 1);
        
        Console.WriteLine($"Lock acquired at {paths.AppSessionLockFile}, PID {Environment.ProcessId}");
        Console.WriteLine("Exiting without cleanup to simulate crash.");
        
        // Disable cleanup to simulate crash
        recovery.SetSkipCleanup(true);
        Environment.ExitCode = 1;
        return Task.FromResult(1);
    }
    
    public static Task<int> CheckRecoveryAsync()
    {
        Console.WriteLine("Checking for recovery...");
        ApplicationPaths paths = ApplicationPaths.CreateDefault();
        RecoveryManager recovery = new(paths);
        
        bool hasFault = recovery.CheckFault();
        Console.WriteLine($"CheckFault returned: {hasFault}");
        
        if (hasFault)
        {
            Console.WriteLine("Restore dialog would be triggered.");
            recovery.CleanupLock();
            return Task.FromResult(0);
        }
        else
        {
            Console.WriteLine("Failed to detect crash.");
            return Task.FromResult(1);
        }
    }
}
