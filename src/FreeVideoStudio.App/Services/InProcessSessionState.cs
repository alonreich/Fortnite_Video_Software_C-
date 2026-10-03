// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// UISTATE_01 — THE TYPED READ-ONLY SNAPSHOT SERVICE FOR UI CONSTRUCTION (Architectural Fix #3).
///
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// WHAT THIS REPLACES. Window construction used to call <c>StateTransferStore.LoadSync()</c>, which
/// can: inspect/start the named-pipe server (including <c>TryStart</c>'s 1-second ready wait),
/// create directories, connect to the pipe as a client, and acquire a NAMED SYSTEM MUTEX with a
/// default timeout of 15 SECONDS — all on the Avalonia UI thread, in a constructor. Every window
/// that called <c>WindowBoundsHelper.Track</c> paid that price before it appeared.
///
/// THE CONTRACT HERE IS THE OPPOSITE, IN ORDER:
/// <list type="number">
/// <item><b>In-process live state.</b> <c>BootstrapAsync</c> (Program.cs) runs BEFORE any window
/// exists and loads the session state asynchronously, which starts the in-process
/// <see cref="NamedPipeStateServer"/> holding it in memory. When that server is running,
/// <see cref="ReadSnapshot"/> is a lock + deep clone — pure memory, no pipe, no mutex, no I/O.</item>
/// <item><b>Real cross-process case.</b> No in-process server means this is a standalone tool
/// process next to a running main app; a 30 ms fast client probe fetches the LIVE state from
/// that other process. Bounded, and only reached when the cross-process case is genuine.</item>
/// <item><b>Last resort.</b> One unlocked direct file read of <c>session_state.json</c>.</item>
/// </list>
///
/// This is a read path only. Writes (bounds, preferences) stay debounced/asynchronous through
/// <c>StateTransferStore</c>, which keeps the server's in-memory state — and therefore this
/// snapshot — current. This class holds NO state of its own; it is a pure function.
/// </para>
/// </summary>
public static class InProcessSessionState
{
    /// <summary>
    /// UISTATE_01 — a read-only snapshot of the session state, safe to call from UI construction.
    /// Never starts a server, never waits on the IPC ready event, never takes the named mutex.
    /// </summary>
    public static JsonObject ReadSnapshot()
    {
        var server = NamedPipeStateServer.ActiveInstance;
        if (server != null && server.IsRunning)
        {
            return server.GetState();
        }

        var ipcState = NamedPipeStateClient.GetStateSync(NamedPipeStateClient.FastProbeTimeout, System.Threading.CancellationToken.None);
        if (ipcState != null)
        {
            return ipcState;
        }

        return StateTransferStore.LoadFromDiskDirect(ApplicationPaths.CreateDefault());
    }
}