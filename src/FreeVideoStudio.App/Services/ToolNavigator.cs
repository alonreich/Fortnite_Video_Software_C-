
using System;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using FreeVideoStudio.Core.Abstractions;
using FreeVideoStudio.Core.Project;

namespace FreeVideoStudio.App.Services;

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
/// <item><b>The launch state now travels as a typed in-memory record (TOOLNAV_05).</b> This class
/// used to serialise a <c>HandoffPayload</c> through <c>StateTransferStore</c> on
/// every open — a named-pipe/disk round trip to pass data between two windows of ONE process,
/// which is the Architectural Fix #3 boundary violation. The tool windows now receive a
/// <see cref="ToolLaunchContext"/> directly, and the return leg is an immutable
/// <see cref="ToolNavigationResult"/> handed back through the <c>onToolReturned</c> callback —
/// no <c>returned_from_crop_tool</c> sentinel in the persistent session state. The IPC handoff
/// API itself remains for the REAL cross-process return (a standalone tool process relaunching
/// the main app).</item>
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
    private readonly IFaultSink _faults;

    private bool _navigating;

    public ToolNavigator(IFaultSink faults)
    {
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
    /// PROJ_11 — THE MERGE QUEUE, SOMEWHERE THE PROJECT CAN SEE IT.
    ///
    /// <para>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// <b>THE DEFECT THIS CLOSES.</b> The queue lived in <c>VideoMergerWindow</c>'s own
    /// <c>ObservableCollection&lt;string&gt;</c> and nowhere else. A user could add eight clips,
    /// order them, close the Merger, and save the project — and the <c>.fvsproj</c> recorded a
    /// single-clip edit with no trace that a merge had ever been assembled. Reopening produced a
    /// document quietly missing most of the work, with nothing to indicate it.
    /// </para>
    ///
    /// <para>
    /// ⚠️ STATIC, FOR THE SAME REASON <see cref="OpenedInProcess"/> IS. The Merger window is
    /// constructed by this navigator and does not receive it — it is legacy code-behind under the
    /// COMPOSITION_02 migration, and Avalonia's lifetime cannot pass constructor arguments. The
    /// queue must outlive the window that owns it, because "the user closed the Merger and THEN
    /// saved" is the exact case being fixed. Written and read on the UI thread only.
    /// </para>
    ///
    /// <para>
    /// This moves with the view-model extraction: once the Merger has one, the queue lives there
    /// and the session reads it through the same callback, unchanged.
    /// </para>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    private static ProjectMerge? _mergeQueue;

    /// <summary>
    /// PROJ_12 / MERGESESSION_01 — the Merger's full edit list. Stores the legacy clip list too (from
    /// the EDL windows) so a reader that only knows <see cref="ProjectMerge.Clips"/> still sees the queue.
    /// </summary>
    public static void PublishMergeEdl(FreeVideoStudio.Core.Media.MergeEdl edl)
    {
        if (edl.Clips.Count == 0)
        {
            _mergeQueue = null;
            return;
        }
        var clips = new List<MergeClip>(edl.Clips.Count);
        foreach (var c in edl.Clips) clips.Add(new MergeClip(c.Path, c.InUs / 1_000_000.0, c.OutUs / 1_000_000.0));
        _mergeQueue = new ProjectMerge { Clips = clips, BaseSpeed = edl.BaseSpeed, Edl = edl };
    }

    /// <summary>PROJ_11 — what <c>ProjectSession.Capture</c> reads. Null means "no merge queued".</summary>
    public static ProjectMerge? ReadMergeQueue() => _mergeQueue;

    /// <summary>
    /// PROJ_11 — restores a queue from an opened project, so the Merger shows what the DOCUMENT
    /// says rather than whatever was last assembled in this process. Without it, opening a project
    /// could silently inherit another project's clips.
    /// </summary>
    public static void RestoreMergeQueue(ProjectMerge? merge)
        => _mergeQueue = merge is { HasClips: true } ? merge : null;

    /// <summary>
    /// Opens <paramref name="tool"/> in this process, hiding <paramref name="owner"/> until it
    /// closes.
    /// </summary>
    /// <param name="shutdownVideoPipeline">
    /// Invoked and AWAITED BEFORE the tool window is constructed (TOOLNAV_02). Must release mpv
    /// and its D3D device; the tool starts its own and the two must not overlap. MPVSHUTDOWN_01:
    /// the contract is awaitable and reports failure — if the old preview cannot be PROVEN torn
    /// down, the tool is not opened and the user is told, because a second native preview stack
    /// beside an abandoned one is exactly the leak this navigator must never cause.
    /// </param>
    /// <param name="restoreVideoPipeline">
    /// Invoked after the tool closes and the owner is visible again, so the editor can bring its
    /// preview back. May be null when the owner rebuilds it lazily.
    /// </param>
    /// <param name="onToolReturned">
    /// TOOLNAV_05 — invoked (UI thread, inside the tool window's Closed handler) with the typed
    /// result the tool produced. This is the in-memory return leg: it replaces the
    /// <c>returned_from_crop_tool</c> sentinel that used to be persisted into the session state
    /// purely so the same process could read it back on startup.
    /// </param>
    /// <returns>True when the tool window was opened.</returns>
    public async Task<bool> OpenAsync(
        Tool tool,
        Window owner,
        ToolLaunchContext context,
        Func<System.Threading.CancellationToken, Task<PreviewShutdownResult>> shutdownVideoPipeline,
        Action? restoreVideoPipeline = null,
        Action<ToolNavigationResult>? onToolReturned = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(shutdownVideoPipeline);

        if (_navigating || IsToolOpen) return false;
        _navigating = true;

        try
        {
            string toolName = tool == Tool.CropTool ? "Crop Tools" : "Video Merger";


            var shutdown = await shutdownVideoPipeline(System.Threading.CancellationToken.None);
            if (!shutdown.Succeeded)
            {
                _faults.Fatal("UI",
                    $"{toolName} was not opened: the current video preview could not be shut down safely. " +
                    "Your work is unchanged — restart the app to restore the preview, then try again.",
                    technicalDetail: shutdown.Reason);
                RuntimeLog.Fail("UI", $"Tool navigation cancelled: preview teardown failed ({shutdown.Reason}).");
                return false;
            }

            Window toolWindow;
            try
            {
                toolWindow = tool == Tool.CropTool
                    ? new CropToolWindow(context as CropToolLaunchContext)
                    : new VideoMergerWindow(context as MergerLaunchContext);
            }
            catch (Exception ex)
            {
                _faults.Fatal("UI", $"{toolName} could not be opened, so nothing has changed.", ex);
                restoreVideoPipeline?.Invoke();
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
                return false;
            }

            IsToolOpen = true;
            OpenedInProcess = true;
            RuntimeLog.Info("UI", $"Opening {toolName} in-process; main window hidden (TOOLNAV_01).");

            toolWindow.Closed += (_, _) =>
            {
                IsToolOpen = false;
                OpenedInProcess = false;

                var result = (toolWindow as IToolNavigationResultSource)?.NavigationResult
                    ?? ToolNavigationResult.Cancelled(toolName);
                onToolReturned?.Invoke(result);

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
