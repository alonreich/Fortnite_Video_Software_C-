// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using FortniteVideoSoftware.Core.Abstractions;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Ipc;

namespace FortniteVideoSoftware.App.Services;

/// <summary>
/// TOOLNAV_01 — OPENING A COMPANION TOOL NO LONGER KILLS THE APPLICATION.
///
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// WHAT THIS REPLACES. <c>CompanionAppService.SwitchToCompanionAppAsync</c> implemented
/// "open the Crop Tool" as:
/// <code>
///     serialise state over a named pipe
///     Process.Start(sameExe, "--crop-tool")
///     poll up to 2s for the child's window handle
///     Environment.Exit(0)          // &lt;-- the application ends
/// </code>
/// Navigation by process suicide. The user watched the app vanish from the taskbar and a
/// different window appear in its place, losing window focus, z-order and any state that was not
/// in the handoff payload. A failure to launch left them with nothing at all.
///
/// It also made an entire IPC subsystem load-bearing for something that is not inter-process:
/// <c>StateTransferStore</c> (21KB), <c>NamedPipeStateServer</c> (20KB), <c>IpcProtocol</c> (13KB)
/// and the crop config stores exist largely to carry state across a boundary that only existed
/// because the process exited.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </para>
///
/// <para>
/// <b>TOOLNAV_02 — WHAT IS DELIBERATELY KEPT.</b> Two behaviours of the old path were load-bearing
/// and are preserved exactly:
/// <list type="number">
/// <item><b>The video pipeline is still shut down</b> before the tool opens. mpv holds a D3D11
/// device, a libmpv IPC pipe and decoder threads; the Crop Tool and the Merger each start their
/// own. Two live pipelines contending for the GPU is the most likely reason the original author
/// reached for a process boundary in the first place, and nothing here re-introduces that.</item>
/// <item><b>The handoff payload is still written through <see cref="StateTransferStore"/>.</b> The
/// tool windows read their starting state from it on construction. Keeping that path means those
/// windows need NO changes for this to work — the process boundary goes away, the data contract
/// does not. Removing the IPC layer is a later, separate change with its own blast radius.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>TOOLNAV_03 — the main window is HIDDEN, never closed.</b> Closing it would run the
/// deferred-close contract (05 §3), tear down the editor and discard the document session. Hiding
/// keeps the window, its view-models and its undo history alive and costs nothing, and the tool's
/// <c>Closed</c> event brings it straight back with everything intact — which is the whole point:
/// the user's session survives a trip to the Crop Tool.
/// </para>
///
/// <para><b>THREADING.</b> UI thread only. Window construction and <c>Show</c> require it.</para>
/// </summary>
public sealed class ToolNavigator
{
    private readonly ApplicationPaths _paths;
    private readonly IFaultSink _faults;

    private bool _navigating;

    public ToolNavigator(ApplicationPaths paths, IFaultSink faults)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _faults = faults ?? throw new ArgumentNullException(nameof(faults));
    }

    /// <summary>The companion tools this navigator knows how to open.</summary>
    public enum Tool
    {
        CropTool,
        VideoMerger,
    }

    /// <summary>True while a tool window is open and the owner is hidden behind it.</summary>
    public bool IsToolOpen { get; private set; }

    /// <summary>
    /// TOOLNAV_04 — THE RETURN LEG HAS TO KNOW HOW IT GOT HERE.
    ///
    /// <para>
    /// Both tool windows return to the editor by <c>Process.Start(exe, "run-ui")</c> followed by
    /// <c>Environment.Exit(0)</c> — the mirror image of the outbound suicide, and correct while
    /// the editor really had exited. Now that the editor is merely hidden in THIS process, that
    /// return would start a SECOND copy of the application while the first is still running: two
    /// processes, two recovery locks, two settings writers, and the original editor's unsaved
    /// document stranded in a hidden window.
    /// </para>
    ///
    /// <para>
    /// Static because the tool windows are constructed by <c>ToolNavigator</c> but do not receive
    /// it — they are legacy code-behind under the COMPOSITION_02 migration. It is set on the way
    /// in and cleared when the tool closes, and it is only ever read on the UI thread.
    /// </para>
    /// </summary>
    public static bool OpenedInProcess { get; private set; }

    /// <summary>
    /// Opens <paramref name="tool"/> in this process, hiding <paramref name="owner"/> until it
    /// closes.
    /// </summary>
    /// <param name="shutdownVideoPipeline">
    /// Invoked BEFORE the tool window is constructed (TOOLNAV_02). Must release mpv and its D3D
    /// device; the tool starts its own and the two must not overlap.
    /// </param>
    /// <param name="restoreVideoPipeline">
    /// Invoked after the tool closes and the owner is visible again, so the editor can bring its
    /// preview back. May be null when the owner rebuilds it lazily.
    /// </param>
    /// <returns>True when the tool window was opened.</returns>
    public async Task<bool> OpenAsync(
        Tool tool,
        Window owner,
        HandoffPayload payload,
        Action shutdownVideoPipeline,
        Action? restoreVideoPipeline = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(shutdownVideoPipeline);

        // Re-entrancy: the button is clickable again for the frame between the click and the
        // window appearing, and a double-click used to start two processes.
        if (_navigating || IsToolOpen) return false;
        _navigating = true;

        try
        {
            string toolName = tool == Tool.CropTool ? "Crop Tools" : "Video Merger";

            // TOOLNAV_02 (2) — same data contract as the old process handoff, so the tool windows
            // are unchanged. A failure here is Degraded, not fatal: the tool still opens, it just
            // opens without the clip pre-selected, and saying so beats opening it blank in silence.
            await _faults.GuardAsync("IPC",
                $"{toolName} opened without your current clip pre-selected — you can pick it there.",
                async () => await new StateTransferStore(_paths).SendHandoffAsync(payload),
                FaultTier.Degraded);

            // TOOLNAV_02 (1) — release the GPU before the tool takes it.
            shutdownVideoPipeline();

            Window toolWindow;
            try
            {
                toolWindow = tool == Tool.CropTool
                    ? new CropToolWindow()
                    : new VideoMergerWindow();
            }
            catch (Exception ex)
            {
                // The old path could not reach this case: a failed Process.Start left the user
                // with a still-running app, but a tool that threw during construction took the
                // whole thing down. Here the owner is still alive and still visible.
                _faults.Fatal("UI", $"{toolName} could not be opened, so nothing has changed.", ex);
                restoreVideoPipeline?.Invoke();
                return false;
            }

            IsToolOpen = true;
            OpenedInProcess = true;      // TOOLNAV_04
            RuntimeLog.Info("UI", $"Opening {toolName} in-process; main window hidden (TOOLNAV_01).");

            // TOOLNAV_03 — hide, never close.
            toolWindow.Closed += (_, _) =>
            {
                IsToolOpen = false;
                OpenedInProcess = false;     // TOOLNAV_04

                _faults.Guard("UI",
                    "The editor could not be brought back automatically. It is still running — " +
                    "select it from the taskbar.",
                    () =>
                    {
                        owner.Show();
                        owner.Activate();
                        RestoreMainWindowReference(owner);
                        restoreVideoPipeline?.Invoke();
                    });

                RuntimeLog.Info("UI", $"{toolName} closed; main window restored.");
            };

            // The desktop lifetime's MainWindow decides what "the application's window" is for
            // shutdown purposes. Point it at the tool while it owns the screen, so closing the
            // tool with the owner hidden cannot terminate the process with the owner's document
            // still in memory.
            PointLifetimeAt(toolWindow);

            toolWindow.Show();
            owner.Hide();
            return true;
        }
        finally
        {
            _navigating = false;
        }
    }

    private void PointLifetimeAt(Window window)
        => _faults.Guard("UI", string.Empty, () =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = window;
        }, FaultTier.Recoverable);

    private void RestoreMainWindowReference(Window owner)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = owner;
    }
}
