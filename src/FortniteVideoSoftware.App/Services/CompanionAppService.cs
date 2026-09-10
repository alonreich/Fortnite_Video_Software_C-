using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Ipc;

namespace FortniteVideoSoftware.App.Services;

public sealed class CompanionAppService
{
    private readonly ApplicationPaths _paths;
    private bool _handoffInProgress;

    public CompanionAppService(ApplicationPaths? paths = null)
    {
        _paths = paths ?? ApplicationPaths.CreateDefault();
    }

    public bool IsHandoffInProgress => _handoffInProgress;

    public async Task<bool> SwitchToCompanionAppAsync(
        string commandLineSwitch,
        string toolName,
        HandoffPayload handoffPayload,
        Action onShutdownVideoPipeline)
    {
        if (_handoffInProgress) return false;
        _handoffInProgress = true;

        RuntimeLog.Info("UI", $"Opening {toolName} and closing Main app.");

        try
        {
            var store = new StateTransferStore(_paths);
            await store.SendHandoffAsync(handoffPayload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug("IPC", $"Handoff state sync: {ex.Message}");
        }

        string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "FortniteVideoSoftware.exe";

        Process? child = null;
        try
        {
            child = Process.Start(new ProcessStartInfo(exePath, commandLineSwitch) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UI", $"Could not launch {toolName}: {ex.Message}");
            _handoffInProgress = false;
            return false;
        }

        onShutdownVideoPipeline();

        if (child != null)
        {
            for (int i = 0; i < 40; i++)
            {
                try
                {
                    if (child.HasExited) break;
                    child.Refresh();
                    if (child.MainWindowHandle != IntPtr.Zero) break;
                }
                catch (Exception ex)
                {
                    RuntimeLog.Debug("UI", $"Handoff poll for {toolName}: {ex.Message}");
                    break;
                }
                await Task.Delay(50);
            }
        }

        Environment.Exit(0);
        return true;
    }
}
