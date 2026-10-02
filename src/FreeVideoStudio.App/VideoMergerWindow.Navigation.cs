
namespace FreeVideoStudio.App;

/// <summary>
/// TOOLNAV_05 — the in-process navigation surface of the Video Merger (Architectural Fix #3),
/// kept in its own partial per MVVM_02: window code-behind ceilings may only shrink, and new
/// behaviour goes in a new file. See Services/ToolLaunchContracts.cs for the contract itself.
/// </summary>
public partial class VideoMergerWindow
{
    /// <summary>
    /// TOOLNAV_05 — IN-PROCESS CONSTRUCTION. ToolNavigator hands the typed, immutable launch
    /// context straight to this constructor; the standalone <c>--merger</c> path keeps the
    /// parameterless constructor. Nothing is serialised, sent over a pipe, or written to disk to
    /// get here.
    /// </summary>
    public VideoMergerWindow(Services.MergerLaunchContext? launchContext) : this()
    {
        LaunchContext = launchContext;
    }

    /// <summary>TOOLNAV_05 — the typed, immutable context this window was launched with (in-process mode only).</summary>
    public Services.MergerLaunchContext? LaunchContext { get; }

    /// <summary>
    /// TOOLNAV_05 — the in-memory return leg. Set on the explicit "return to the main app" path,
    /// read by ToolNavigator when this window closes. Replaces the IPC handoff write that used to
    /// run even when the owner was in THIS process.
    /// </summary>
    public Services.ToolNavigationResult? NavigationResult { get; private set; }
}