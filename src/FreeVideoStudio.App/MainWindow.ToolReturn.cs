// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;

namespace FreeVideoStudio.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// TOOLRETURN_01 — THE PREVIEW COMES BACK WHEN A TOOL CLOSES.
///
/// Opening the Video Merger or Crop Tools releases the editor's mpv and D3D device first
/// (TOOLNAV_02, <see cref="ShutdownVideoPipeline"/>). An <see cref="MpvVideoView"/> is dead once
/// (TOOLNAV_02, <see cref="ShutdownVideoPipelineAsync"/> — MPVSHUTDOWN_01: awaitable, so the UI
/// thread never joins a render worker). An <see cref="MpvVideoView"/> is dead once
/// disposed, and the editor passed <c>restoreVideoPipeline: null</c> ("rebuilt lazily") — but
/// nothing rebuilt it. After returning, <c>IpcClient</c> stayed null forever: Upload Video logged
/// the pick, showed "Video player is still starting." and never played anything.
///
/// Now the close path swaps a FRESH <see cref="MpvVideoView"/> into the old one's slot, starts it
/// (off the UI thread, as at startup), and reloads the clip that was open, paused at MARK START.
/// The named XAML reference (#VideoHost) would keep pointing at the dead control, so the software-
/// fallback badge is bound in code to whichever host is current.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class MainWindow
{
    private IDisposable? _fallbackBadgeBinding;

    /// <summary>MPVSHUTDOWN_01 — the one in-flight (or completed) pipeline shutdown operation.</summary>
    private Task<PreviewShutdownResult>? _videoPipelineShutdownTask;

    /// <summary>True only when the native preview stack was PROVEN released. Never set on failure.</summary>
    private bool _videoPipelineShutdownSucceeded;

    /// <summary>
    /// TOOLNAV_05 — the in-memory return leg (Architectural Fix #3). ToolNavigator hands back the
    /// typed result the tool window published when it closed (SwitchToCompanionAppAsync passes
    /// this as the <c>onToolReturned</c> callback). This replaces the
    /// <c>returned_from_crop_tool</c> sentinel that the old flow wrote into the persistent session
    /// state purely so this window could read it back on the next startup.
    /// </summary>
    private void OnToolNavigationResult(Services.ToolNavigationResult result)
    {
        if (!result.ReturnedToOwner)
        {
            RuntimeLog.Info("HANDOFF", $"{result.ToolName} closed without an explicit return; the editor is unchanged.");
            return;
        }

        RuntimeLog.Info("HANDOFF",
            $"Returned from {result.ToolName}. Crop overlay configuration reloaded from the active profile." +
            (string.IsNullOrWhiteSpace(result.SelectedClipPath)
                ? string.Empty
                : $" Clip in the tool: {System.IO.Path.GetFileName(result.SelectedClipPath)}."));
    }

    /// <summary>
    /// MPVSHUTDOWN_01 — the interactive, awaitable preview teardown (tool navigation, anything that
    /// is not a process exit). Re-entrant and idempotent: every caller awaits the SAME operation,
    /// the state is not marked successful before completion, and the host reference is kept (the
    /// dead control still owns its layout slot until <see cref="RestoreVideoPipelineAfterTool"/>
    /// swaps a fresh one in). The process-exit paths use ShutdownVideoPipelineForProcessExit.
    /// </summary>
    private Task<PreviewShutdownResult> ShutdownVideoPipelineAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        var inFlight = _videoPipelineShutdownTask;
        if (inFlight != null) return inFlight;

        var host = _videoHost;
        if (host == null || _videoPipelineShutdownSucceeded)
            return Task.FromResult(PreviewShutdownResult.AlreadyStopped);

        _videoPipelineShutDown = true;
        _videoPipelineShutdownTask = ShutdownVideoPipelineCoreAsync(host, cancellationToken);
        return _videoPipelineShutdownTask;
    }

    private async Task<PreviewShutdownResult> ShutdownVideoPipelineCoreAsync(MpvVideoView host, System.Threading.CancellationToken cancellationToken)
    {
        PreviewShutdownResult result;
        try
        {
            result = await host.ShutdownAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UI", $"Video preview shutdown failed: {ex.Message}");
            result = PreviewShutdownResult.Failed(ex.Message);
        }

        if (!result.Succeeded)
        {
            RuntimeLog.Fail("UI", $"Video preview teardown did not complete ({result.Reason}); the preview stays down and no replacement will be built.");
            return result;
        }

        _videoPipelineShutdownSucceeded = true;
        try { _musicPreviewIpcClient?.Dispose(); }
        catch (Exception ex) { RuntimeLog.Fail("UI", $"Music preview teardown reported: {ex.Message}"); }
        _musicPreviewIpcClient = null;
        DisposePreviewMix();   // PREVIEWMIX_01
        return result;
    }

    /// <summary>Starts the current preview host (startup and after a tool closes).</summary>
    private async Task StartVideoHostAsync()
    {
        _videoHost ??= this.FindControl<MpvVideoView>("VideoHost");
        var host = _videoHost;
        if (host == null) return;
        BindFallbackBadge(host);

        string baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string mpvPath = Path.Combine(baseDir, "frontend", "mpv.exe");
        if (!File.Exists(mpvPath)) mpvPath = Path.Combine(baseDir, "..", "..", "..", "..", "..", "binaries", "mpv.exe");
        if (!File.Exists(mpvPath)) mpvPath = "mpv.exe";
        try
        {
            await host.StartMpvProcessAsync(mpvPath);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UI", $"Video preview initialization failed. {ex}");
            ShowTacticalFeedback("Video preview unavailable on this hardware.");
            PlayUiSound();
            return;
        }

        if (host.IpcClient != null)
        {
            host.IpcClient.SeekCompleted -= OnSeekCompleted;
            host.IpcClient.SeekCompleted += OnSeekCompleted;
        }
    }

    private void BindFallbackBadge(MpvVideoView host)
    {
        _fallbackBadgeBinding?.Dispose();
        var badge = this.FindControl<Border>("PreviewFallbackBadge");
        _fallbackBadgeBinding = badge?.Bind(IsVisibleProperty, host.GetObservable(MpvVideoView.IsSoftwareFallbackActiveProperty));
    }

    /// <summary>ToolNavigator's restore hook: the tool closed and this window is visible again.</summary>
    private void RestoreVideoPipelineAfterTool()
    {
        if (!_videoPipelineShutDown) return;
        if (MpvVideoView.HasUnverifiedTeardown)
        {
            RuntimeLog.Fail("UI", "Video preview stays unavailable: the previous preview could not be proven shut down. Restart the app to restore it.");
            ShowTacticalFeedback("Video preview unavailable. Restart the app to restore it.");
            PlayUiSound();
            return;
        }
        var old = _videoHost;
        if (old?.Parent is not Panel parent)
        {
            RuntimeLog.Fail("UI", "Could not bring the video preview back after the tool closed (its slot was not found). Restart the app to get the preview back.");
            ShowTacticalFeedback("Video preview could not restart. Restart the app.");
            return;
        }

        int index = parent.Children.IndexOf(old);
        var host = new MpvVideoView { Name = "VideoHost", IsVisible = old.IsVisible };
        host.SetValue(Controls.Tactile.IsParallaxTiltEnabledProperty, true);
        parent.Children.RemoveAt(index);
        parent.Children.Insert(index, host);

        _videoHost = host;
        _musicPreviewIpcClient = null;   // disposed with the pipeline; recreated on next use
        _liveGameFilter = null;          // PEAKSAFE_01 — the new player has no audio filter yet
        _isSeeking = false;
        _nextSeekTarget = null;
        _videoPipelineShutDown = false;
        RuntimeLog.Info("UI", "Video preview restarted after returning from a tool (TOOLRETURN_01).");
        _ = ReviveLoadedClipAsync(host);
    }

    private async Task ReviveLoadedClipAsync(MpvVideoView host)
    {
        try
        {
            await StartVideoHostAsync();
            var ipc = host.IpcClient;
            if (ipc == null || !ReferenceEquals(host, _videoHost)) return;
            ApplyPortraitModeToActiveHost();

            string? path = _loadedVideoPath;
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!File.Exists(path))
            {
                RuntimeLog.Warn("UI", $"The clip that was open is no longer on disk; the preview stays empty: {path}");
                return;
            }
            double startSec = _trimStartSet ? _trimStartMs / 1000.0 : 0;
            await ipc.LoadFileAsync(path, startSec);
            await ipc.SetPropertyAsync("pause", "yes");
            await ipc.SetPropertyAsync("speed", _baseSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            RuntimeLog.Info("UI", $"Reloaded {Path.GetFileName(path)} into the preview, paused at {startSec:F3}s.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UI", $"The video preview could not reload the open clip after the tool closed: {ex.Message}");
        }
    }
}
