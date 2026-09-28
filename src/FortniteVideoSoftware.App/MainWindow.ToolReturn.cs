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

namespace FortniteVideoSoftware.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// TOOLRETURN_01 — THE PREVIEW COMES BACK WHEN A TOOL CLOSES.
///
/// Opening the Video Merger or Crop Tools releases the editor's mpv and D3D device first
/// (TOOLNAV_02, <see cref="ShutdownVideoPipeline"/>). An <see cref="MpvVideoView"/> is dead once
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
