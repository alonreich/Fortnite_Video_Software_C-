
namespace FreeVideoStudio.App.Services;

/// <summary>
/// TOOLNAV_05 — THE TYPED, IN-MEMORY NAVIGATION CONTRACT (Architectural Fix #3).
///
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// WHAT THIS SEPARATES. Three concerns that used to share one transport are now explicit:
/// <list type="number">
/// <item><b>SAME-PROCESS TOOL LAUNCH/HANDOFF</b> (MainWindow → CropToolWindow, MainWindow →
/// VideoMergerWindow): these records. Typed in-memory objects, handed to the tool window's
/// constructor. No JSON serialisation, no named pipe, no disk, no cross-process mutex.</item>
/// <item><b>DURABLE USER/UI PREFERENCES</b> (window bounds, output directories, volumes): still
/// persisted through <c>StateTransferStore</c>, but read from the in-process snapshot
/// (<see cref="InProcessSessionState"/>), never synchronously loaded during window construction.</item>
/// <item><b>REAL CROSS-PROCESS IPC</b> (CLI <c>read-state</c>/<c>write-state</c>, a separately
/// launched <c>--crop-tool</c>/<c>--merger</c> process handing back to a NEW main app process):
/// the named-pipe subsystem, unchanged and still load-bearing.</item>
/// </list>
/// </para>
///
/// <para>
/// All launch contexts and the result are <b>immutable records</b> on purpose: a tool window can
/// never mutate the owner's copy, and two tool windows opened from one context can never observe
/// each other's edits through a shared mutable object.
/// </para>
/// </summary>
public abstract record ToolLaunchContext
{
    protected ToolLaunchContext(string? initialVideoPath)
    {
        InitialVideoPath = initialVideoPath;
    }

    /// <summary>The clip the editor had loaded when the tool was opened, if any.</summary>
    public string? InitialVideoPath { get; init; }
}

/// <summary>
/// TOOLNAV_05 — what the editor hands the Crop Tool when it opens it in this process. The old
/// multi-process flow serialised exactly these fields into a <c>HandoffPayload</c> and wrote them
/// through the IPC state store; they now travel as this record. (The crop window deliberately does
/// NOT auto-load <see cref="ToolLaunchContext.InitialVideoPath"/> — GATE_01 requires a profile
/// choice first, and the old IPC payload had no reader either, so wiring it in now would be new
/// behaviour, not a fix.)
/// </summary>
public sealed record CropToolLaunchContext : ToolLaunchContext
{
    public CropToolLaunchContext(string? initialVideoPath, double? trimStartMs, double? trimEndMs)
        : base(initialVideoPath)
    {
        TrimStartMs = trimStartMs;
        TrimEndMs = trimEndMs;
    }

    /// <summary>The editor's MARK START position, in milliseconds, when the tool was opened.</summary>
    public double? TrimStartMs { get; init; }

    /// <summary>The editor's MARK END position, in milliseconds, when the tool was opened.</summary>
    public double? TrimEndMs { get; init; }
}

/// <summary>
/// TOOLNAV_05 — what the editor hands the Video Merger. The merger needs no starting clip today;
/// the record exists so its launch path is typed the same way as the Crop Tool's and can grow
/// without growing the IPC contract.
/// </summary>
public sealed record MergerLaunchContext : ToolLaunchContext
{
    public MergerLaunchContext() : base((string?)null)
    {
    }
}

/// <summary>
/// TOOLNAV_05 — the in-memory return leg. Set by a tool window before it closes when the owner is
/// in the same process; consumed by <see cref="ToolNavigator"/> and handed to the owner through the
/// <c>onToolReturned</c> callback. This is what replaces the <c>returned_from_crop_tool</c>
/// sentinel that used to be written into the persistent session state purely to signal between
/// two windows of ONE process.
/// </summary>
public sealed record ToolNavigationResult(string ToolName, bool ReturnedToOwner, string? SelectedClipPath)
{
    /// <summary>The tool closed without an explicit return (cancelled, X button, teardown).</summary>
    public static ToolNavigationResult Cancelled(string toolName)
        => new(toolName, ReturnedToOwner: false, SelectedClipPath: null);
}

/// <summary>
/// TOOLNAV_05 — implemented by tool windows so <see cref="ToolNavigator"/> can collect the typed
/// result without depending on any concrete window class.
/// </summary>
public interface IToolNavigationResultSource
{
    /// <summary>The typed result produced by this tool's return path, or null if it never ran.</summary>
    ToolNavigationResult? NavigationResult { get; }
}