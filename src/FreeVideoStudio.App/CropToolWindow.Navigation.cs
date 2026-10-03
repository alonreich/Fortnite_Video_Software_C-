// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
namespace FreeVideoStudio.App;

/// <summary>
/// TOOLNAV_05 — the in-process navigation surface of the Crop Tool (Architectural Fix #3), kept in
/// its own partial per MVVM_02: window code-behind ceilings may only shrink, and new behaviour
/// goes in a new file. See Services/ToolLaunchContracts.cs for the contract itself.
/// </summary>
public partial class CropToolWindow
{
    /// <summary>
    /// TOOLNAV_05 — IN-PROCESS CONSTRUCTION. ToolNavigator hands the typed, immutable launch
    /// context straight to this constructor; the standalone <c>--crop-tool</c> path keeps the
    /// parameterless constructor. NOTE: the context deliberately does NOT feed
    /// <c>_initialVideoPath</c> — GATE_01 requires a deliberate profile choice before any clip
    /// is loaded, and the old IPC handoff payload never had a reader either, so auto-loading the
    /// editor's clip here would be new behaviour, not a fix.
    /// </summary>
    public CropToolWindow(Services.CropToolLaunchContext? launchContext) : this((string?)null)
    {
        LaunchContext = launchContext;
    }

    /// <summary>TOOLNAV_05 — the typed, immutable context this window was launched with (in-process mode only).</summary>
    public Services.CropToolLaunchContext? LaunchContext { get; }

    /// <summary>
    /// TOOLNAV_05 — the in-memory return leg. Set on the explicit "return to the main app" path,
    /// read by ToolNavigator when this window closes. Replaces the persisted
    /// <c>returned_from_crop_tool</c> sentinel that used to signal the same thing between two
    /// windows of one process.
    /// </summary>
    public Services.ToolNavigationResult? NavigationResult { get; private set; }
}