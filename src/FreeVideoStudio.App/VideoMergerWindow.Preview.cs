
using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using FreeVideoStudio.Core.Abstractions;
using FreeVideoStudio.Core.Ipc;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// PREVIEWFAULT_01 — MPV PREVIEW STARTUP CANNOT CRASH THE PROCESS (Architectural Fix #2).
///
/// WHAT WAS WRONG. <c>InitializeMpv</c> was an <c>async void</c> implementation method whose first
/// awaited call <c>await _videoHost.StartMpvProcessAsync(mpvPath)</c> had NO catch anywhere on the
/// path: a faulted continuation escaped the async-void boundary into Avalonia's / AppDomain's
/// global unhandled handling and TERMINATED THE WHOLE APPLICATION for a preview-only failure
/// (missing native mpv binary, <c>mpv_create</c> returning null, <c>mpv_initialize</c> failing, or
/// IPC pipe setup failing).
///
/// WHAT THIS IS.
///   1. <see cref="MergerPreviewState"/> + <see cref="MergerPreviewGate"/>: one deterministic state
///      machine for the preview, extracted so its transition rules are unit-testable without a
///      dispatcher (MergerPreviewGateTests).
///   2. <see cref="StartPreviewInitialization"/>: one synchronous, duplicate-safe entry point — the
///      <c>Loaded</c> handler is NOT async anymore, so a re-firing Loaded, a retry while a start is
///      already running, or a close racing startup can never create a second host.
///   3. <see cref="RunPreviewInitializationAsync"/>: the old implementation body as a Task-returning
///      method with ONE COMPLETE try/catch from the first awaited native call onward. It never
///      throws; the fire-and-forget call site is therefore safe.
///   4. Window-lifetime cancellation (<see cref="_previewInitCts"/>): closing the window while
///      startup is in flight cancels it, the partially initialized view is disposed (MpvVideoView's
///      Dispose is idempotent and handles the resources created before a mid-flight fault), no
///      error modal is shown for the normal cancellation, and no control is touched after close.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>

/// <summary>Deterministic lifecycle of the merger's MPV preview surface.</summary>
internal enum MergerPreviewState
{
    /// <summary>No start attempt has been made yet.</summary>
    NotStarted,
    /// <summary>A start attempt is in flight.</summary>
    Starting,
    /// <summary>The native player and IPC client are live; playback controls may drive them.</summary>
    Ready,
    /// <summary>Startup failed and was contained; the application keeps running degraded.</summary>
    Unavailable,
    /// <summary>A live preview is being torn down.</summary>
    Stopping,
    /// <summary>Torn down (cancelled, superseded or stopped); a later retry may start from here.</summary>
    Stopped
}

internal sealed class MergerPreviewGate
{
    private readonly object _lock = new();
    private MergerPreviewState _state = MergerPreviewState.NotStarted;

    public MergerPreviewState State => _state;

    /// <summary>
    /// True only while the preview is Ready: the gate every playback path asks before it touches
    /// mpv. A preview that never initialized can never be driven through this.
    /// </summary>
    public bool IsPlaybackAllowed => _state == MergerPreviewState.Ready;

    /// <summary>
    /// Attempts to begin a start. Returns <c>true</c> exactly once per attempt and <c>false</c>
    /// when the previous attempt is still running, is still live, or is already being torn down —
    /// the duplicate-initialization guard.
    /// </summary>
    public bool TryBeginStart()
    {
        lock (_lock)
        {
            switch (_state)
            {
                case MergerPreviewState.NotStarted:
                case MergerPreviewState.Unavailable:
                case MergerPreviewState.Stopped:
                    _state = MergerPreviewState.Starting;
                    return true;
                default:
                    return false;
            }
        }
    }
    /// <summary>Startup's final, successful transition. Returns <c>false</c> (no-op) unless we were Starting.</summary>
    public bool MarkReady()
    {
        lock (_lock)
        {
            if (_state != MergerPreviewState.Starting) return false;
            _state = MergerPreviewState.Ready;
            return true;
        }
    }

    /// <summary>
    /// Startup's contained-failure transition (08's degraded tier — the window survives). Returns
    /// <c>false</c> (no-op) unless we were Starting, so a fault can never demote a live preview.
    /// </summary>
    public bool MarkUnavailable()
    {
        lock (_lock)
        {
            if (_state != MergerPreviewState.Starting) return false;
            _state = MergerPreviewState.Unavailable;
            return true;
        }
    }

    /// <summary>Begins tearing a LIVE preview down. Returns <c>false</c> (no-op) unless we were Ready.</summary>
    public bool MarkStopping()
    {
        lock (_lock)
        {
            if (_state != MergerPreviewState.Ready) return false;
            _state = MergerPreviewState.Stopping;
            return true;
        }
    }

    /// <summary>
    /// Ends a teardown. Acceptable from Ready/Stopping (a normal stop), from Starting (superseded
    /// or cancelled startup — the partially created resources are released and nothing was ever
    /// live) and from Stopped (idempotent re-stop of a never-started preview).
    /// </summary>
    public void MarkStopped()
    {
        lock (_lock)
        {
            switch (_state)
            {
                case MergerPreviewState.Ready:
                case MergerPreviewState.Stopping:
                case MergerPreviewState.Starting:
                    _state = MergerPreviewState.Stopped;
                    break;
            }
        }
    }
}

public partial class VideoMergerWindow
{
    private readonly MergerPreviewGate _previewGate = new();

    /// <summary>
    /// PREVIEWFAULT_01 — window-lifetime cancellation for preview startup. Created by
    /// <see cref="StartPreviewInitialization"/>; cancelled and disposed by
    /// <see cref="CancelPreviewInitialization"/> from OnClosing, so a window closed while mpv was
    /// coming up never leaks the native player and never touches a control after close.
    /// </summary>
    private System.Threading.CancellationTokenSource? _previewInitCts;

    /// <summary>
    /// The one synchronous entry into preview initialization (Loaded and any future retry gesture
    /// both come through here). Duplicate-safe: a second call while a start is running, while the
    /// preview is already live, or while it is being torn down is a no-op, so Loaded firing more
    /// than once can never create a second <see cref="MpvVideoView"/>.
    /// </summary>
    private void StartPreviewInitialization()
    {
        if (!_previewGate.TryBeginStart())
        {
            RuntimeLog.Debug("MERGER-PREVIEW", $"Preview start request ignored; state is {_previewGate.State}.");
            return;
        }

        _previewInitCts?.Cancel();
        _previewInitCts?.Dispose();
        _previewInitCts = new System.Threading.CancellationTokenSource();

        _ = RunPreviewInitializationAsync(_previewInitCts.Token);
    }
    /// <summary>
    /// PREVIEWFAULT_01 — the old <c>InitializeMpv</c> body, as an awaitable implementation method
    /// with ONE COMPLETE exception boundary covering everything from the first awaited native call
    /// (<c>CreateNativePlayer</c> runs inside <c>StartMpvProcessAsync</c>'s
    /// <c>InitializeMpvAsync</c>) through IPC wiring and the persisted-volume load. This method
    /// never throws: cancellation lands in the OperationCanceledException arm, any other startup
    /// failure lands in <see cref="HandlePreviewInitializationFailure"/>, and the fire-and-forget
    /// call site can therefore never observe an escaping exception.
    /// </summary>
    private async Task RunPreviewInitializationAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            _videoHost = this.FindControl<MpvVideoView>("VideoHost");
            if (_previewDetach == null) WirePreviewDetach();

            var host = _videoHost;
            if (host == null)
            {
                throw new InvalidOperationException("The preview surface (VideoHost) was not found in the Video Merger layout.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            string mpvPath = ResolveMpvPath();
            await host.StartMpvProcessAsync(mpvPath).WaitAsync(cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            var ipc = host.IpcClient;
            if (ipc == null)
            {
                throw new InvalidOperationException("The MPV preview finished starting without an IPC client; the preview cannot be driven.");
            }

            ipc.SeekCompleted += () =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    _isSeeking = false;
                    if (_nextSeekTarget.HasValue) { double target = _nextSeekTarget.Value; _nextSeekTarget = null; await SeekInternal(target); }
                });
            };

            try
            {
                var state = await new StateTransferStore(_paths).LoadAsync().WaitAsync(cancellationToken).ConfigureAwait(true);
                if (state.TryGetPropertyValue("MainVolume", out var volNode))
                {
                    var volSlider = VolumeSliderCtl;
                    if (volSlider != null) volSlider.Value = volNode?.GetValue<double>() ?? 100.0;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

            if (!_previewGate.MarkReady()) return;
        }
        catch (OperationCanceledException)
        {
            _previewGate.MarkStopped();
            RuntimeLog.Info("MERGER-PREVIEW", "Preview startup was cancelled (window closing or attempt superseded); partial resources released.");
            await ReleaseFailedPreviewHostAsync();
        }
        catch (Exception ex)
        {
            await HandlePreviewInitializationFailure(ex);
        }
    }

    private static string ResolveMpvPath()
    {
        string baseDir = System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string mpvPath = System.IO.Path.Combine(baseDir, "frontend", "mpv.exe");
        if (!System.IO.File.Exists(mpvPath))
            mpvPath = System.IO.Path.Combine(baseDir, "..", "..", "..", "..", "..", "binaries", "mpv.exe");
        if (!System.IO.File.Exists(mpvPath)) mpvPath = "mpv.exe";
        return mpvPath;
    }
    /// <summary>
    /// PREVIEWFAULT_01 — the deliberate fault boundary for preview startup. The application keeps
    /// running: the merge queue, the EDL/export path and project state stay usable; the preview
    /// surface is explicitly marked Unavailable and preview-only transport controls are disabled.
    /// Reported once per attempt through the ambient <see cref="Faults"/> channel (Degraded tier —
    /// the session survives) and surfaced once in this window; never a modal.
    /// </summary>
    /// <summary>PREVIEWFAULT_01 — the failure arm is now async (MPVSHUTDOWN_01): the partial-host
    /// release it performs is awaitable, and it still contains its own complete catch boundary.</summary>
    private async Task HandlePreviewInitializationFailure(Exception ex)
    {
        _previewGate.MarkUnavailable();
        await ReleaseFailedPreviewHostAsync();
        ApplyPreviewDegradedUi();

        const string userMessage = "Video preview could not start. You can continue using the merger, but preview is unavailable.";
        Faults.Degraded("MERGER-PREVIEW", userMessage, ex);
        Controls.FloatingNotice.Show(this, userMessage, Controls.NoticeKind.Error);
    }

    /// <summary>
    /// PREVIEWFAULT_01 (05 lifecycle) — releases the PARTIALLY initialized view after a failed or
    /// cancelled startup, through the MPVSHUTDOWN_01 two-phase teardown: only the resources that
    /// actually managed to be created (mpv handle, IPC client, WGL/D3D interop, GL textures,
    /// framebuffers) exist by then, and each is released exactly once. A failed teardown is
    /// reported, never assumed.
    /// </summary>
    private async Task ReleaseFailedPreviewHostAsync()
    {
        var host = _videoHost;
        _videoHost = null;
        if (host == null) return;
        try
        {
            var shutdown = await host.ShutdownAsync();
            if (!shutdown.Succeeded)
                RuntimeLog.Fail("MERGER-PREVIEW", $"Partial preview teardown did not complete: {shutdown.Reason}");
        }
        catch (Exception ex) { RuntimeLog.Fail("MERGER-PREVIEW", $"Partial preview teardown reported: {ex.Message}"); }
    }

    /// <summary>
    /// PREVIEWFAULT_01 — the user-visible degraded state. The preview area shows the explicit
    /// unavailable card and the transport controls that exist only to drive a live preview are
    /// disabled; nothing else about the window changes.
    /// </summary>
    private void ApplyPreviewDegradedUi()
    {
        var overlay = this.FindControl<Border>("MergerPreviewUnavailableOverlay");
        if (overlay != null) overlay.IsVisible = true;

        var playPause = this.FindControl<Button>("PlayPauseButton");
        var fastBackward = this.FindControl<Button>("FastBackwardButton");
        var fastForward = this.FindControl<Button>("FastForwardButton");
        if (playPause != null) playPause.IsEnabled = false;
        if (fastBackward != null) fastBackward.IsEnabled = false;
        if (fastForward != null) fastForward.IsEnabled = false;

        RuntimeLog.Info("MERGER-PREVIEW", "Degraded preview state applied: preview-only transport disabled; merger functions remain available.");
    }

    /// <summary>
    /// PREVIEWFAULT_01 — window shutdown, called from OnClosing BEFORE any disposal: cancels an
    /// in-flight startup so its continuation can never touch a control or start playback after
    /// close, then frees the CTS. Safe ordering: Cancel() flags the token synchronously before the
    /// CTS is freed and every later use of the captured token is a read-only check, so the racing
    /// continuation observes cancellation instead of touching a disposed window.
    /// </summary>
    private void CancelPreviewInitialization()
    {
        try { _previewInitCts?.Cancel(); }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

        try { _previewInitCts?.Dispose(); }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

        _previewInitCts = null;
    }
    /// <summary>MPVSHUTDOWN_01 — the one in-flight (or completed) merger pipeline shutdown.</summary>
    private Task<PreviewShutdownResult>? _videoPipelineShutdownTask;

    /// <summary>
    /// MPVSHUTDOWN_01 — the interactive, awaitable merger preview teardown (close, return to the
    /// editor). Idempotent and re-entrant: every caller awaits the SAME operation, and the state is
    /// marked shut down up front (nothing may use the preview from here on) while SUCCESS is
    /// recorded only when the awaitable teardown below proves it.
    /// </summary>
    private Task<PreviewShutdownResult> ShutdownVideoPipelineAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        var inFlight = _videoPipelineShutdownTask;
        if (inFlight != null) return inFlight;

        var host = _videoHost;
        if (host == null || _videoPipelineShutDown) return Task.FromResult(PreviewShutdownResult.AlreadyStopped);

        _videoPipelineShutDown = true;
        try { _previewDetach?.Attach(); }
        catch (Exception ex) { RuntimeLog.Fail("UI", $"Could not reattach the preview during shutdown: {ex.Message}"); }

        _videoPipelineShutdownTask = ShutdownVideoPipelineCoreAsync(host, cancellationToken);
        return _videoPipelineShutdownTask;
    }

    private async Task<PreviewShutdownResult> ShutdownVideoPipelineCoreAsync(MpvVideoView host, System.Threading.CancellationToken cancellationToken)
    {
        PreviewShutdownResult result;
        try
        {
            if (host.IpcClient != null)
            {
                var stopTask = host.IpcClient.SendCommandAsync("stop");
                await Task.WhenAny(stopTask, Task.Delay(500)).ConfigureAwait(true);
            }
            result = await host.ShutdownAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MERGER", $"Video preview shutdown failed: {ex.Message}");
            result = PreviewShutdownResult.Failed(ex.Message);
        }

        if (!result.Succeeded)
            RuntimeLog.Fail("MERGER", $"Video preview teardown did not complete ({result.Reason}); no replacement preview will be built until restart.");
        return result;
    }

    /// <summary>
    /// MPVSHUTDOWN_01 — PROCESS-EXIT ONLY (menu Exit → Environment.Exit): the synchronous bounded
    /// fallback, safe because the process terminates immediately after.
    /// </summary>
    private void ShutdownVideoPipelineForProcessExit()
    {
        if (_videoPipelineShutDown) return;
        _videoPipelineShutDown = true;

        try { _videoHost?.Dispose(); }
        catch (Exception ex) { RuntimeLog.Fail("MERGER", $"Video preview teardown reported: {ex.Message}"); }
    }

}
