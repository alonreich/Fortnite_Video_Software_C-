
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using static FreeVideoStudio.App.Infrastructure.GranularEditorVisuals;

namespace FreeVideoStudio.App;

/// <summary>
/// Universal AI Smart Tracking Zoom integration for <see cref="GranularSpeedEditorWindow"/>.
/// Handles multi-angle candidate frame discovery, interactive character selection wizard,
/// Gemini vision tracking dispatch, real-time thinking overlay, and instant loop preview verification.
/// </summary>
public partial class GranularSpeedEditorWindow
{
    private CancellationTokenSource? _aiTrackingCts;

    private bool _isAiPreviewLooping;
    private int _aiPreviewSegmentIndex = -1;
    private SpeedSegment? _aiPreviewOriginalSegment;
    private SpeedSegment? _aiPreviewCandidateSegment;

    /// <summary>
    /// Wires AI Tracking UI controls, button handlers, loop preview verification hooks, and overlay cancel hooks.
    /// </summary>
    private void WireAiTrackingControls()
    {
        var btn = AiSmartZoomBtnCtl;
        if (btn != null)
        {
            btn.Click += async (_, _) => await OnAiSmartZoomClickedAsync();
        }

        var cancelBtn = AiTrackingCancelBtnCtl;
        if (cancelBtn != null)
        {
            cancelBtn.Click += (_, _) => CancelAiTracking();
        }

        var acceptBtn = AiTrackingAcceptBtnCtl;
        if (acceptBtn != null)
        {
            acceptBtn.Click += (_, _) => EndAiPreviewLoop(accept: true);
        }

        var discardBtn = AiTrackingDiscardBtnCtl;
        if (discardBtn != null)
        {
            discardBtn.Click += (_, _) => EndAiPreviewLoop(accept: false);
        }

        if (_playbackTimer != null)
        {
            _playbackTimer.Tick += (_, _) => OnAiTrackingPlaybackTick();
        }

        this.Closing += (_, _) => EndAiPreviewLoop(accept: false);

        UpdateAiSmartZoomBtnVisualState();
    }

    /// <summary>
    /// Updates the visual state and label of the AI Smart Zoom button based on whether
    /// the currently selected speed segment has an active AI tracking trajectory.
    /// </summary>
    private void UpdateAiSmartZoomBtnVisualState()
    {
        var btn = AiSmartZoomBtnCtl;
        if (btn == null) return;

        bool hasAiZoom = _selectedSegmentIndex >= 0 &&
                         _selectedSegmentIndex < _segments.Count &&
                         !string.IsNullOrEmpty(_segments[_selectedSegmentIndex].AiTrackingTrajectory);

        if (hasAiZoom)
        {
            btn.Content = "EDIT AI ZOOM";
            btn.Classes.Remove("Primary");
            if (!btn.Classes.Contains("Success")) btn.Classes.Add("Success");
        }
        else
        {
            btn.Content = "AI SMART ZOOM";
            btn.Classes.Remove("Success");
            if (!btn.Classes.Contains("Primary")) btn.Classes.Add("Primary");
        }
    }

    /// <summary>
    /// Cancels any currently executing AI subject tracking job cleanly and reverts any active loop preview.
    /// </summary>
    private void CancelAiTracking()
    {
        _aiTrackingCts?.Cancel();
        EndAiPreviewLoop(accept: false);
    }

    /// <summary>
    /// Invoked on every playback timer tick to loop the preview over the target segment during verification (Requirement 9.a).
    /// </summary>
    private void OnAiTrackingPlaybackTick()
    {
        if (!_isAiPreviewLooping || _aiPreviewSegmentIndex < 0 || _aiPreviewSegmentIndex >= _segments.Count) return;
        if (_videoHost?.IpcClient == null) return;

        double curPlaybackRelMs = Math.Max(0, (_videoHost.IpcClient.CurrentTime * 1000.0) - _trimStartMs);
        var seg = _segments[_aiPreviewSegmentIndex];
        if (curPlaybackRelMs >= seg.EndMs || curPlaybackRelMs < seg.StartMs - 50)
        {
            SetPlayheadFromScrub(seg.StartMs);
            if (_videoHost.IpcClient.IsPaused)
            {
                _ = _videoHost.IpcClient.SetPropertyAsync("pause", "no");
            }
        }
    }

    /// <summary>
    /// Finalizes or discards the active loop preview verification state.
    /// </summary>
    private void EndAiPreviewLoop(bool accept)
    {
        if (!_isAiPreviewLooping) return;
        _isAiPreviewLooping = false;

        var bar = AiTrackingPreviewBarCtl;
        if (bar != null) bar.IsVisible = false;

        if (accept)
        {
            if (_aiPreviewCandidateSegment != null && _aiPreviewSegmentIndex >= 0 && _aiPreviewSegmentIndex < _segments.Count)
            {
                PushUndo("apply AI smart zoom");
                _segments[_aiPreviewSegmentIndex] = _aiPreviewCandidateSegment;
                RefreshSegmentList();
                RedrawTimeline();
                UpdateDeleteButtonVisibility();
                PulseZoomConfirmFeedback();
                Notify("AI Smart Tracking Zoom applied successfully!");
            }
        }
        else
        {
            if (_aiPreviewOriginalSegment != null && _aiPreviewSegmentIndex >= 0 && _aiPreviewSegmentIndex < _segments.Count)
            {
                _segments[_aiPreviewSegmentIndex] = _aiPreviewOriginalSegment;
                RefreshSegmentList();
                RedrawTimeline();
                UpdateDeleteButtonVisibility();
                Notify("AI Smart Tracking preview discarded.");
            }
            if (_videoHost?.IpcClient != null)
            {
                _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
            }
        }

        _aiPreviewOriginalSegment = null;
        _aiPreviewCandidateSegment = null;
        _aiPreviewSegmentIndex = -1;
        UpdateAiSmartZoomBtnVisualState();
    }

    /// <summary>
    /// Main click handler for the AI SMART ZOOM / EDIT AI ZOOM button.
    /// Executes the multi-frame discovery scan, character picker wizard dialog,
    /// dense Gemini vision tracking, and instant loop preview.
    /// </summary>
    private async Task OnAiSmartZoomClickedAsync()
    {
        if (_isAiPreviewLooping)
        {
            EndAiPreviewLoop(accept: false);
        }

        bool configured = await Controls.AiSetupWizardWindow.EnsureApiKeyConfiguredAsync(this);
        if (!configured) return;

        double dur = GetDuration();
        if (dur <= 0)
        {
            NotifyError("Load a video first.");
            return;
        }

        if (_selectedSegmentIndex < 0 || _selectedSegmentIndex >= _segments.Count)
        {
            if (!EnsureZoomTargetSegment()) return;
        }

        if (_selectedSegmentIndex < 0 || _selectedSegmentIndex >= _segments.Count) return;
        var seg = _segments[_selectedSegmentIndex];

        var canvas = ZoomOverlayCanvasCtl;
        var (sw, sh) = CoordinateMath.GetResolutionInts(_originalResolution);
        if (sw <= 0 || sh <= 0) { sw = 1920; sh = 1080; }

        int ymin = 0, xmin = 0, ymax = 1000, xmax = 1000;
        if (_hasZoomBox && canvas != null)
        {
            var vid = GetVideoDisplayRect(canvas);
            if (vid.Width > 0 && vid.Height > 0)
            {
                double sx = sw / vid.Width, sy = sh / vid.Height;
                int zx = Even(Math.Clamp((int)Math.Round((_zoomUiRect.X - vid.X) * sx), 0, sw - 2));
                int zy = Even(Math.Clamp((int)Math.Round((_zoomUiRect.Y - vid.Y) * sy), 0, sh - 2));
                int zw = Even(Math.Clamp((int)Math.Round(_zoomUiRect.Width * sx), 2, sw - zx));
                int zh = Even(Math.Clamp((int)Math.Round(_zoomUiRect.Height * sy), 2, sh - zy));

                ymin = Math.Clamp((int)Math.Round(zy * 1000.0 / sh), 0, 1000);
                xmin = Math.Clamp((int)Math.Round(zx * 1000.0 / sw), 0, 1000);
                ymax = Math.Clamp((int)Math.Round((zy + zh) * 1000.0 / sh), 0, 1000);
                xmax = Math.Clamp((int)Math.Round((zx + zw) * 1000.0 / sw), 0, 1000);
            }
        }
        else if (seg.ZoomW.HasValue && seg.ZoomH.HasValue && seg.ZoomX.HasValue && seg.ZoomY.HasValue)
        {
            ymin = Math.Clamp((int)Math.Round(seg.ZoomY.Value * 1000.0 / sh), 0, 1000);
            xmin = Math.Clamp((int)Math.Round(seg.ZoomX.Value * 1000.0 / sw), 0, 1000);
            ymax = Math.Clamp((int)Math.Round((seg.ZoomY.Value + seg.ZoomH.Value) * 1000.0 / sh), 0, 1000);
            xmax = Math.Clamp((int)Math.Round((seg.ZoomX.Value + seg.ZoomW.Value) * 1000.0 / sw), 0, 1000);
        }
        else
        {
            xmin = 375; xmax = 625;
            ymin = 375; ymax = 625;
        }

        if (_zoomModeActive)
        {
            ExitZoomMode();
        }

        var settings = SettingsManager.Instance;
        string apiKey = settings.GeminiApiKey;
        string modelName = string.IsNullOrWhiteSpace(settings.GeminiModelName) ? "gemini-2.5-flash" : settings.GeminiModelName;
        double baseScale = settings.AiZoomBaseScale > 0 ? settings.AiZoomBaseScale : 2.2;
        double minScale = settings.AiZoomMinScale > 0 ? settings.AiZoomMinScale : 1.3;
        double deadbandPercent = settings.AiZoomDeadbandPercent > 0 ? settings.AiZoomDeadbandPercent : 2.0;
        bool avoidHud = settings.AiZoomAvoidHud;
        bool portraitMode = _isMobileFormat;

        string targetVideoPath = _videoPath;
        double sourceStartSec = (_trimStartMs + seg.StartMs) / 1000.0;
        double sourceDurationSec = (seg.EndMs - seg.StartMs) / 1000.0;

        if (IsMergeMode && _mergeSource != null)
        {
            foreach (var clip in _mergeSource.Clips)
            {
                if (seg.StartMs >= clip.StartMs && seg.StartMs < clip.EndMs)
                {
                    targetVideoPath = clip.Path;
                    sourceStartSec = (clip.KeepInUs / 1_000_000.0) + ((seg.StartMs - clip.StartMs) / 1000.0);
                    break;
                }
            }
        }

        var overlay = AiTrackingProgressOverlayCtl;
        if (overlay != null) overlay.IsVisible = true;
        if (AiTrackingStatusLabelCtl != null) AiTrackingStatusLabelCtl.Text = "Phase 1: Extracting 3 multi-angle candidate frames...";
        if (AiTrackingConsoleTextCtl != null) AiTrackingConsoleTextCtl.Text = "";

        _aiTrackingCts?.Cancel();
        _aiTrackingCts = new CancellationTokenSource();
        var token = _aiTrackingCts.Token;

        var statusProgress = new Progress<string>(status =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (AiTrackingStatusLabelCtl != null)
                    AiTrackingStatusLabelCtl.Text = status;
            });
        });

        Action<string> consoleLog = msg =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (AiTrackingConsoleTextCtl != null)
                {
                    AiTrackingConsoleTextCtl.Text = string.IsNullOrEmpty(AiTrackingConsoleTextCtl.Text)
                        ? msg
                        : AiTrackingConsoleTextCtl.Text + "\n" + msg;
                }
                AiTrackingConsoleScrollCtl?.ScrollToEnd();
            });
        };

        string f1 = string.Empty, f2 = string.Empty, f3 = string.Empty;
        try
        {
            if (AiTrackingStatusLabelCtl != null)
                AiTrackingStatusLabelCtl.Text = "Phase 1: Scanning candidate frames for targets...";

            var (discoveredSubjects, frame1, frame2, frame3, t1, t2, t3) = await GeminiTrackingService.DiscoverSubjectsWithLookaheadAsync(
                targetVideoPath,
                sourceStartSec,
                sourceDurationSec,
                dur,
                apiKey,
                modelName,
                consoleLog,
                token);
            f1 = frame1; f2 = frame2; f3 = frame3;

            if (token.IsCancellationRequested) return;

            if (overlay != null) overlay.IsVisible = false;

            DiscoveredSubject? confirmedSubject = null;
            if (discoveredSubjects.Count > 0)
            {
                var picker = new Controls.AiSubjectPickerWindow(f1, f2, f3, t1, t2, t3, discoveredSubjects);
                bool? confirmed = await picker.ShowDialog<bool>(this);
                if (confirmed != true || picker.SelectedSubject == null)
                {
                    Notify("AI Smart Tracking cancelled by user.");
                    return;
                }
                confirmedSubject = picker.SelectedSubject;
                ymin = confirmedSubject.Ymin;
                xmin = confirmedSubject.Xmin;
                ymax = confirmedSubject.Ymax;
                xmax = confirmedSubject.Xmax;
            }
            else
            {
                await ErrorReporter.ShowAsync(
                    this,
                    "No Enemy Targets Detected",
                    "No enemy opponents or targets were detected in this video segment (including forward combat probes).\n\n" +
                    "In third-person games like Fortnite, the main player character is automatically excluded from tracking so the camera does not track your own avatar.\n\n" +
                    "Try selecting a segment where an enemy player or target is clearly visible, or adjust the zoom box manually.");
                return;
            }

            if (token.IsCancellationRequested) return;

            if (overlay != null) overlay.IsVisible = true;
            if (AiTrackingStatusLabelCtl != null)
            {
                AiTrackingStatusLabelCtl.Text = confirmedSubject != null
                    ? $"Phase 3: Dense tracking target '{confirmedSubject.Label}'..."
                    : "Phase 3: Dense target tracking in progress...";
            }

            string? bestFrame = confirmedSubject != null
                ? (confirmedSubject.FrameIndex switch
                {
                    1 => !string.IsNullOrEmpty(f1) ? f1 : f2,
                    3 => !string.IsNullOrEmpty(f3) ? f3 : f2,
                    _ => !string.IsNullOrEmpty(f2) ? f2 : f1
                })
                : (!string.IsNullOrEmpty(f1) ? f1 : null);

            var trajectory = await Task.Run(async () =>
            {
                return await GeminiTrackingService.TrackSubjectAsync(
                    videoPath: targetVideoPath,
                    sourceStartSec: sourceStartSec,
                    sourceDurationSec: sourceDurationSec,
                    initialBox: (ymin, xmin, ymax, xmax),
                    sourceWidth: sw,
                    sourceHeight: sh,
                    apiKey: apiKey,
                    modelName: modelName,
                    baseScale: baseScale,
                    minScale: minScale,
                    deadbandPercent: deadbandPercent,
                    avoidHud: avoidHud,
                    portraitMode: portraitMode,
                    confirmedTarget: confirmedSubject,
                    referenceFramePath: bestFrame,
                    statusProgress: statusProgress,
                    consoleLog: consoleLog,
                    cancellationToken: token).ConfigureAwait(false);
            }, token);

            if (token.IsCancellationRequested) return;

            if (trajectory.Keyframes.Count > 0)
            {
                var first = trajectory.Keyframes[0];
                int kx = Even(Math.Clamp((int)Math.Round(first.CropX), 0, sw - 2));
                int ky = Even(Math.Clamp((int)Math.Round(first.CropY), 0, sh - 2));
                int kw = Even(Math.Clamp((int)Math.Round(first.CropW), 2, sw - kx));
                int kh = Even(Math.Clamp((int)Math.Round(first.CropH), 2, sh - ky));

                _aiPreviewOriginalSegment = seg;
                _aiPreviewSegmentIndex = _selectedSegmentIndex;
                _aiPreviewCandidateSegment = seg with
                {
                    ZoomX = kx,
                    ZoomY = ky,
                    ZoomW = kw,
                    ZoomH = kh,
                    ZoomOrigRes = $"{sw}x{sh}",
                    ZoomStartMs = seg.StartMs,
                    ZoomEndMs = seg.EndMs,
                    ZoomSlow = false,
                    AiTrackingTrajectory = trajectory.ToJson()
                };

                _segments[_selectedSegmentIndex] = _aiPreviewCandidateSegment;
                _isAiPreviewLooping = true;

                if (AiTrackingPreviewBarCtl != null) AiTrackingPreviewBarCtl.IsVisible = true;

                SetPlayheadFromScrub(seg.StartMs);
                if (_videoHost?.IpcClient != null)
                {
                    _ = _videoHost.IpcClient.SetPropertyAsync("pause", "no");
                }

                Notify("AI Smart Tracking ready! Review loop preview. Click ACCEPT & APPLY or DISCARD.");
            }
            else
            {
                await ErrorReporter.ShowAsync(
                    this,
                    "AI Tracking Inconclusive",
                    "Gemini AI tracked the segment but produced no confident trajectory keyframes for the selected target.\n\n" +
                    "Try selecting a clearer segment where the opponent is unobstructed, or adjust the zoom box manually.");
            }
        }
        catch (OperationCanceledException)
        {
            Notify("AI Tracking was cancelled.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("AiTracking", ex);
            await ErrorReporter.ShowAsync(
                this,
                "AI Smart Tracking Error",
                $"An error occurred while tracking with Gemini AI:\n{ex.Message}",
                ex.ToString());
        }
        finally
        {
            CleanupTempFrames(f1, f2, f3);
            if (overlay != null) overlay.IsVisible = false;
            _aiTrackingCts = null;
        }
    }

    /// <summary>
    /// Cleans up temporary candidate angle still images from disk.
    /// </summary>
    private static void CleanupTempFrames(params string[] paths)
    {
        foreach (var p in paths)
        {
            if (string.IsNullOrEmpty(p)) continue;
            try
            {
                if (File.Exists(p)) File.Delete(p);
            }
            catch (Exception ex)
            {
                RuntimeLog.Swallowed(ex);
            }
        }
    }
}
