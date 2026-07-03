using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FortniteVideoSoftware.App.Controls;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.App.Infrastructure;
using FortniteVideoSoftware.Core.Media;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Threading;
using System;

namespace FortniteVideoSoftware.App;

public partial class MainWindow : Window
{
    private MpvVideoView? _videoHost;
    private bool _isSeeking = false;
    private double? _nextSeekTarget = null;

    private double _trimStartMs = 0;
    private double _trimEndMs = 0;
    private double _thumbnailPosMs = 0;
    private bool _thumbnailSet = false;

    private double _freezeTimeMs = -1;
    private double _freezeDurationS = 1.0;
    private bool _isCurrentlyFrozen = false;
    private DateTime _freezeStartTime;
    private double _previousVolume = 100;

    private bool _trimStartSet = false;   // Tracks whether MARK START has been explicitly or auto-set
    private bool _trimEndSet = false;      // Tracks whether MARK END has been explicitly set

    private void SetTrimStart(double valueMs)
    {
        _trimStartMs = valueMs;
        if (_musicWizardResult != null && _isMusicActive)
        {
            double dur = _musicWizardResult.TimelineEndSeconds - _musicWizardResult.TimelineStartSeconds;
            _musicWizardResult.TimelineStartSeconds = valueMs / 1000.0;
            _musicWizardResult.TimelineEndSeconds = (valueMs / 1000.0) + dur;
        }
    }
    private bool _draggingStartMarker = false;
    private bool _draggingEndMarker = false;
    private MusicWizardResult? _musicWizardResult;
    private System.Diagnostics.Process? _musicPreviewProcess;
    private bool _isMusicPreviewPlaying = false;
    private double _lastMusicPreviewSyncTime = -1;
    private DispatcherTimer? _playbackTimer;
    private bool _isTimerUpdatingSlider = false;
    private readonly ApplicationPaths _paths = ApplicationPaths.CreateDefault();

    private bool _isMusicBlockFocused = false;
    private Avalonia.Controls.Shapes.Rectangle? _musicBlockRectRef;
    private Avalonia.Controls.Control? _musicStartPopupRef;
    private Avalonia.Controls.Control? _musicEndPopupRef;
    private DispatcherTimer? _marchingAntsTimer;
    private double _marchingAntsOffset = 0;
    private bool _isThumbnailMarkerSelected = false;
    private bool _isDraggingThumbnailMarker = false;
    private Avalonia.Controls.Shapes.Rectangle? _thumbnailMarkerIconAntsRef;
    private Avalonia.Controls.Shapes.Rectangle? _thumbnailMarkerLineAntsRef;

    // Granular speed segments set via the Granular Speed Editor dialog
    private readonly System.Collections.Generic.List<SpeedSegment> _speedSegments = new();
    private double _baseSpeed = SpeedPresetButtons.NativeDefaultSpeed;
    private bool _isTimelineDrawn = false;
    private string _loadedVideoPath = string.Empty;
    private string _hardwareMode = "CPU";
    private System.Threading.CancellationTokenSource? _processCts;

    // Crash recovery manager — saves/restores editing session state across crashes
    private readonly RecoveryManager _recovery = new RecoveryManager();
    private bool _isGranularSpeedActive = false;
    private bool _isMusicActive = false;
    private bool _isRestoring = false;

    public MainWindow()
    {
        RuntimeLog.Info("UI", "Initializing MainWindow");
        InitializeComponent();

        this.AddHandler(DragDrop.DragEnterEvent, OnVideoDragEnter);
        this.AddHandler(DragDrop.DragOverEvent, OnVideoDragOver);
        this.AddHandler(DragDrop.DragLeaveEvent, OnVideoDragLeave);
        this.AddHandler(DragDrop.DropEvent, OnVideoDrop);

        var overlay = this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer");
        if (overlay != null)
        {
            overlay.CancelRequested += (s, e) =>
            {
                if (_processCts != null && !_processCts.IsCancellationRequested)
                {
                    _processCts.Cancel();
                }
            };
        }

        this.Loaded += (s, e) => InitializeMpv();

        SettingsManager.Load();

        var settingsBtn = this.FindControl<Button>("SettingsOverlayBtn");
        if (settingsBtn != null)
        {
            settingsBtn.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked Settings Overlay button.");
                var settingsWin = new FortniteVideoSoftware.App.Controls.SettingsWindow();
                bool changed = await settingsWin.ShowDialog<bool>(this);
                if (changed) UpdateTooltips();
            };
        }

        UpdateTooltips();

        FortniteVideoSoftware.App.Infrastructure.WindowManager.RegisterWindow(this);

        // Issue #19: Removed duplicate timer creation that caused double UI updates and visual flicker
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();

        // Fix: Enable title bar drag-to-move for borderless window
        AttachTitleBarDrag();

        var canvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineMarkersCanvas");
        if (canvas != null)
        {
            canvas.SizeChanged += (s, e) => UpdateTimelineMarkers();
        }

        this.PointerPressed += (s, e) =>
        {
            if (_isMusicBlockFocused)
            {
                _isMusicBlockFocused = false;
                UpdateTimelineMarkers();
            }
            if (_isThumbnailMarkerSelected && !_isDraggingThumbnailMarker)
            {
                _isThumbnailMarkerSelected = false;
                UpdateTimelineMarkers();
            }
        };

        _marchingAntsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _marchingAntsTimer.Tick += (s, e) =>
        {
            bool hasThumbnailMarkerAnts = (_isThumbnailMarkerSelected || _isDraggingThumbnailMarker) &&
                                          _thumbnailMarkerIconAntsRef != null &&
                                          _thumbnailMarkerLineAntsRef != null;
            if (hasThumbnailMarkerAnts)
            {
                _marchingAntsOffset += 1;
                if (_marchingAntsOffset > 1000) _marchingAntsOffset = 0;
                _thumbnailMarkerIconAntsRef!.StrokeDashOffset = _marchingAntsOffset;
                _thumbnailMarkerLineAntsRef!.StrokeDashOffset = _marchingAntsOffset;
            }

            if (_isMusicBlockFocused && _musicBlockRectRef != null)
            {
                _marchingAntsOffset += 1;
                if (_marchingAntsOffset > 1000) _marchingAntsOffset = 0;
                _musicBlockRectRef.StrokeDashOffset = _marchingAntsOffset;
            }
        };
        _marchingAntsTimer.Start();

        var slider = this.FindControl<Slider>("TimelineSlider");

        var cancelButton = this.FindControl<Button>("CancelButton");
        if (cancelButton != null)
        {
            cancelButton.Click += (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked Cancel button, closing app.");
                Close();
            };
        }

        var processButton = this.FindControl<Button>("ProcessButton");
        if (processButton != null)
        {
            processButton.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked PROCESS button.");
                processButton.IsEnabled = false;
                processButton.Content = "PROCESSING...";
                await ProcessVideoAsync(processButton);
                // processButton state is restored inside the worker.Finished callback
            };
        }

        // Removed success actions from main window

        // ---- Granular Speed button ----
        var granularButton = this.FindControl<Button>("GranularButton");
        if (granularButton != null)
        {
            granularButton.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked GRANULAR SPEED button.");

                // If granular speeds are active, this click removes them all
                if (_isGranularSpeedActive)
                {
                    _speedSegments.Clear();
                    SetGranularButtonActive(false);
                    // Reset MPV playback speed back to base speed
                    _lastAppliedSpeed = _baseSpeed;
                    if (_videoHost?.IpcClient != null)
                        _ = _videoHost.IpcClient.SetPropertyAsync("speed",
                            _baseSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                    UpdateEstimatedQuality();
                    ShowTacticalFeedback("Speed segments removed");
                    UpdateTimelineMarkers(); // Redraw timeline to remove segment overlays
                    RuntimeLog.Info("UI", "User removed all granular speed segments via REMOVE SPEEDS button.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(_loadedVideoPath))
                {
                    // No video loaded yet — show feedback toast
                    ShowTacticalFeedback("Load a video first!");
                    PlayUiSound();
                    return;
                }

                // Pause main player while dialog is open
                if (_videoHost?.IpcClient != null)
                    _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");

                EnsureTrimPointsSet();

                SetTimelinePopupsVisible(false);

                var editor = new GranularSpeedEditorWindow(
                    _loadedVideoPath,
                    _trimStartMs,
                    _trimEndMs > 0 ? _trimEndMs : (_videoHost?.IpcClient?.Duration ?? 0) * 1000,
                    _speedSegments,
                    _baseSpeed,
                    _freezeTimeMs,
                    _freezeDurationS);

                await editor.ShowDialog(this);

                SetTimelinePopupsVisible(true);
                if (_musicBlockRectRef != null) _musicBlockRectRef.IsVisible = true;

                if (editor.Accepted)
                {
                    _speedSegments.Clear();
                    _speedSegments.AddRange(editor.ResultSegments);
                    _baseSpeed = editor.ResultBaseSpeed;
                    _freezeTimeMs = editor.ResultFreezeTimeMs;
                    _freezeDurationS = editor.ResultFreezeDurationS;

                    int count = _speedSegments.Count;
                    RuntimeLog.Info("UI", $"Granular editor closed. {count} segment(s) saved. Base speed={_baseSpeed:F2}x");

                    // Toggle button state: red "REMOVE SPEEDS" when segments exist, normal otherwise
                    SetGranularButtonActive(count > 0);

                    ShowTacticalFeedback(count > 0
                        ? $"{count} speed segment{(count == 1 ? "" : "s")} saved"
                        : "Granular segments cleared");

                    UpdateEstimatedQuality();
                    UpdateTimelineMarkers(); // Redraw timeline to show segment overlays
                }
            };
        }

        var uploadButton = this.FindControl<Button>("UploadButton");
        if (uploadButton != null)
        {
            uploadButton.Click += (s, e) => { RuntimeLog.Info("UI", "User clicked Upload Video button"); OnUploadVideoClicked(s, e); };
        }

        var centerUploadButton = this.FindControl<Button>("CenterUploadButton");
        if (centerUploadButton != null)
        {
            centerUploadButton.Click += (s, e) => { RuntimeLog.Info("UI", "User clicked Center Upload Video button"); OnUploadVideoClicked(s, e); };
        }

        var videoMergerButton = this.FindControl<Button>("VideoMergerButton");
        if (videoMergerButton != null)
        {
            // Issue #3: Open as modal dialog instead of killing the process — preserves all session state
            videoMergerButton.Click += (s, e) =>
            {
                SaveRecoveryState();
                RuntimeLog.Info("UI", "Opening Video Merger app and closing Main app.");
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "FortniteVideoSoftware.exe";
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath, "--merger") { UseShellExecute = false });
                if (p != null)
                {
                    Task.Run(() =>
                    {
                        try { p.WaitForInputIdle(5000); Task.Delay(500).Wait(); } catch { }
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => this.Close());
                    });
                }
                else
                {
                    this.Close();
                }
            };
        }

        var cropSettingsButton = this.FindControl<Button>("CropSettingsButton");
        if (cropSettingsButton != null)
        {
            // Issue #3: Open as modal dialog instead of killing the process — preserves all session state
            cropSettingsButton.Click += (s, e) =>
            {
                SaveRecoveryState();
                RuntimeLog.Info("UI", "Opening Crop Tools app and closing Main app.");
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "FortniteVideoSoftware.exe";
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath, "--crop-tool") { UseShellExecute = false });
                if (p != null)
                {
                    Task.Run(() =>
                    {
                        try { p.WaitForInputIdle(5000); Task.Delay(500).Wait(); } catch { }
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => this.Close());
                    });
                }
                else
                {
                    this.Close();
                }
            };
        }

        var playPauseButton = this.FindControl<Button>("PlayPauseButton");
        if (playPauseButton != null)
        {
            playPauseButton.Click += (s, e) =>
            {
                RuntimeLog.Info("UI", "User toggled Play/Pause state.");
                if (_videoHost?.IpcClient != null) 
                {
                    if (_isCurrentlyFrozen)
                    {
                        _isCurrentlyFrozen = false;
                        _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
                        return;
                    }
                    _ = _videoHost.IpcClient.SetPropertyAsync("pause", _videoHost.IpcClient.IsPaused ? "no" : "yes");
                }
            };
        }

        var setThumbnailButton = this.FindControl<Button>("SetThumbnailButton");
        if (setThumbnailButton != null)
        {
            setThumbnailButton.Click += (s, e) =>
            {
                var txt = this.FindControl<TextBlock>("SetThumbnailText");
                if (_thumbnailSet)
                {
                    _thumbnailSet = false;
                    _thumbnailPosMs = 0;
                    _isThumbnailMarkerSelected = false;
                    _isDraggingThumbnailMarker = false;
                    setThumbnailButton.Classes.Remove("Danger");
                    setThumbnailButton.Classes.Add("Primary");
                    if (txt != null) txt.Text = "SET THUMBNAIL";
                    ShowTacticalFeedback("Thumbnail removed");
                }
                else
                {
                    double time = GetCurrentMpvTime();
                    _thumbnailPosMs = time * 1000;
                    _thumbnailSet = true;
                    setThumbnailButton.Classes.Remove("Primary");
                    setThumbnailButton.Classes.Add("Danger");
                    if (txt != null) txt.Text = "REMOVE THUMBNAIL";

                    PlayUiSound();
                    ShowTacticalFeedback($"📸 {TimeSpan.FromSeconds(time):mm\\:ss\\.ff}");
                    ShowTimelineGlow(_thumbnailPosMs, Avalonia.Media.Brushes.DeepSkyBlue);
                }

                UpdateTimelineMarkers();
                UpdateEstimatedQuality();
                SaveRecoveryState();
            };
        }

        var markStartButton = this.FindControl<Button>("MarkStartButton");
        if (markStartButton != null)
        {
            markStartButton.Click += (s, e) =>
            {
                EnsureTrimPointsSet();
                RuntimeLog.Info("UI", $"User clicked MARK START at {TimeSpan.FromMilliseconds(_trimStartMs):hh\\:mm\\:ss\\.ff}.");
                double time = GetCurrentMpvTime();
                SetTrimStart(time * 1000);
                _trimStartSet = true;
                markStartButton.Content = $"START: {TimeSpan.FromSeconds(time):hh\\:mm\\:ss}";

                PlayUiSound();
                ShowTacticalFeedback($"🏁 {TimeSpan.FromSeconds(time):mm\\:ss\\.ff}");
                ShowTimelineGlow(_trimStartMs, Avalonia.Media.Brushes.SeaGreen);
                UpdateTimelineMarkers();
                UpdateEstimatedQuality();
                SaveRecoveryState();
            };
        }

        var markEndButton = this.FindControl<Button>("MarkEndButton");
        if (markEndButton != null)
        {
            markEndButton.Click += (s, e) =>
            {
                EnsureTrimPointsSet();
                RuntimeLog.Info("UI", $"User clicked MARK END at {TimeSpan.FromMilliseconds(_trimEndMs):hh\\:mm\\:ss\\.ff}.");
                double time = GetCurrentMpvTime();
                _trimEndMs = time * 1000;
                markEndButton.Content = $"END: {TimeSpan.FromSeconds(time):hh\\:mm\\:ss}";

                // Pause the video automatically
                if (_videoHost?.IpcClient != null)
                {
                    _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");
                }

                PlayUiSound();
                ShowTacticalFeedback($"🏁 {TimeSpan.FromSeconds(time):mm\\:ss\\.ff}");
                ShowTimelineGlow(_trimEndMs, Avalonia.Media.Brushes.SeaGreen);
                UpdateTimelineMarkers();
                UpdateEstimatedQuality();
                SaveRecoveryState();
            };
        }

        var timelineSlider = this.FindControl<Slider>("TimelineSlider");
        if (timelineSlider != null)
        {
            timelineSlider.ValueChanged += (s, e) =>
            {
                if (!_isTimerUpdatingSlider)
                {
                    double duration = _videoHost?.IpcClient?.Duration ?? 0.0;
                    if (duration > 0)
                    {
                        double targetTime = (e.NewValue / 100.0) * duration;
                        _ = SeekInternal(targetTime);
                    }
                }
            };

            var timelineOverlay = this.FindControl<Border>("TimelineOverlay");
            if (timelineOverlay != null && canvas != null)
            {
                timelineOverlay.PointerPressed += (s, e) => SeekTimelineFromPointer(e, canvas, timelineSlider);
            }
        }

        var mainSpeedSlider = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("MainSpeedSlider");
        if (mainSpeedSlider != null)
        {
            mainSpeedSlider.SetRange(1, 40);
            var speedLabels = new System.Collections.Generic.List<string>();
            for (int i = 1; i <= 40; i++) speedLabels.Add((i / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "x");
            mainSpeedSlider.SetLabels(speedLabels);
            mainSpeedSlider.Value = 11; // 1.1x
            SpeedPresetButtons.ConfigureBaseButton(this, SpeedPresetButtons.NativeDefaultSpeed, "Set speed to the app default 1.1x");
            SpeedPresetButtons.WirePresetButtons(this, SpeedPresetButtons.NativeDefaultSpeed, ApplyMainSpeedPreset);
            mainSpeedSlider.ValueChanged += (s, e) =>
            {
                _baseSpeed = e / 10.0;
                if (_videoHost?.IpcClient != null)
                    _ = _videoHost?.IpcClient?.SetPropertyAsync("speed", _baseSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                UpdateEstimatedQuality();
                UpdateSpeedLabel();
                SaveRecoveryState();
            };
            mainSpeedSlider.ValueChangeCompleted += (s, e) =>
            {
                RuntimeLog.Info("UI", $"Speed slider final resting value: {e / 10.0:F1}x");
            };
            UpdateSpeedLabel();
        }

        var volumeSlider = this.FindControl<Slider>("VolumeSlider");
        var volumeBadgeText = this.FindControl<TextBlock>("VolumeBadgeText");
        var volumeSpeakerIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("VolumeSpeakerIcon");
        if (volumeSlider != null && volumeBadgeText != null)
        {
            volumeSlider.PropertyChanged += (s, e) =>
            {
                if (e.Property == Slider.ValueProperty && e.NewValue != null)
                {
                    int vol = System.Convert.ToInt32(e.NewValue);
                    volumeBadgeText.Text = $"{vol}%";
                    if (_videoHost?.IpcClient != null)
                    {
                        _ = _videoHost?.IpcClient?.SetPropertyAsync("volume", vol.ToString());
                    }
                    if (volumeSpeakerIcon != null)
                    {
                        if (vol == 0)
                        {
                            volumeSpeakerIcon.Data = Avalonia.Media.Geometry.Parse("M3,7 L6,7 L10,3 L10,13 L6,9 L3,9 Z M12,5 L16,13 M16,5 L12,13");
                        }
                        else
                        {
                            volumeSpeakerIcon.Data = Avalonia.Media.Geometry.Parse("M3,7 L6,7 L10,3 L10,13 L6,9 L3,9 Z M13,5 A4,4 0 0,1 13,11 M16,2 A8,8 0 0,1 16,14");
                        }
                    }
                }
            };

            // Wire mute/unmute handlers to the enlarged SpeakerHitBox (35x35 square) which sits
            // on top (ZIndex 10001) for easy clicking. The icon Path itself is non-hit-testable;
            // the hit box receives all pointer/keyboard input.
            var speakerHitBox = this.FindControl<Border>("SpeakerHitBox");
            if (speakerHitBox != null)
            {
                speakerHitBox.PointerPressed += SpeakerIcon_PointerPressed;
                speakerHitBox.KeyDown += SpeakerIcon_KeyDown;
            }

            volumeSlider.PointerReleased += (s, e) =>
            {
                try
                {
                    var state = System.IO.File.Exists(_paths.SessionStateFile)
                        ? System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(System.IO.File.ReadAllText(_paths.SessionStateFile)) ?? new System.Text.Json.Nodes.JsonObject()
                        : new System.Text.Json.Nodes.JsonObject();
                    state["MainVolume"] = volumeSlider.Value;
                    System.IO.File.WriteAllText(_paths.SessionStateFile, state.ToJsonString());
                }
                catch { }
            };
        }

        var qualitySlider = this.FindControl<SpinningWheelSlider>("QualitySlider");
        if (qualitySlider != null)
        {
            qualitySlider.SetRange(0, 20);
            var labels = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 20; i++) labels.Add($"{5 + i * 5}MB");
            labels.Add("ORIGINAL QUALITY");
            qualitySlider.SetLabels(labels);
            qualitySlider.Value = 7; // Default 40MB
            qualitySlider.ValueChanged += (s, v) =>
            {
                var qs = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("QualitySlider");
                if (qs != null) Avalonia.Controls.ToolTip.SetTip(qs, $"Target Size: {labels[v]}");
                UpdateEstimatedQuality();
                SaveRecoveryState();
            };
            qualitySlider.ValueChangeCompleted += (s, v) =>
            {
                RuntimeLog.Info("UI", $"Quality slider final resting value: {(v < 20 ? $"{5 + v * 5}MB" : "ORIGINAL QUALITY")}");
            };
        }


        var addMusicButton = this.FindControl<Button>("AddMusicButton");
        if (addMusicButton != null)
        {
            addMusicButton.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked ADD MUSIC button.");
                RuntimeLog.Info("UI", "User clicked ADD MUSIC button (launching Wizard).");

                // If music is active, this click removes it
                if (_isMusicActive)
                {
                    _musicWizardResult = null;
                    SetMusicButtonActive(false);
                    UpdateEstimatedQuality();
                    ShowTacticalFeedback("Music removed");
                    UpdateTimelineMarkers();
                    RuntimeLog.Info("UI", "User removed background music via REMOVE MUSIC button.");
                    return;
                }

                EnsureTrimPointsSet();

                if (_videoHost?.IpcClient != null)
                    _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");

                SetTimelinePopupsVisible(false);
                var wizard = new MusicWizardWindow(_loadedVideoPath, _trimStartMs, _trimEndMs > 0 ? _trimEndMs : (_videoHost?.IpcClient?.Duration ?? 0) * 1000);
                await wizard.ShowDialog(this);
                SetTimelinePopupsVisible(true);

                if (wizard.Result != null)
                {
                    _musicWizardResult = wizard.Result;
                    NormalizeMusicPlacement(_musicWizardResult);

                    RuntimeLog.Info("UI", $"User added music via wizard: {_musicWizardResult.MusicFilePath}, ducking={_musicWizardResult.EnableDucking}");

                    // Toggle button to red "REMOVE MUSIC" state
                    SetMusicButtonActive(true);

                    var volSlider = this.FindControl<Slider>("VolumeSlider");
                    if (volSlider != null)
                    {
                        volSlider.Value = _musicWizardResult.VideoVolume * 100.0;
                    }

                    UpdateTimelineMarkers(); // Force redraw UI
                }
            };
        }

        var mobileCheckbox = this.FindControl<CheckBox>("MobileCheckbox") ?? this.FindControl<CheckBox>("PortraitModeCheckbox");

        if (mobileCheckbox != null)
        {
            UpdatePortraitOverlay();
            mobileCheckbox.IsCheckedChanged += (s, e) => { UpdatePortraitOverlay(); SaveRecoveryState(); };
        }

        // Hook up remaining checkboxes for crash-recovery persistence
        var bossHpCb = this.FindControl<CheckBox>("BossHpCheckbox");
        if (bossHpCb != null) bossHpCb.IsCheckedChanged += (s, e) => SaveRecoveryState();

        var teammatesCb = this.FindControl<CheckBox>("TeammatesCheckbox");
        if (teammatesCb != null) teammatesCb.IsCheckedChanged += (s, e) => SaveRecoveryState();

        var noFadeCb = this.FindControl<CheckBox>("NoFadeCheckbox");
        if (noFadeCb != null) noFadeCb.IsCheckedChanged += (s, e) => SaveRecoveryState();

        var portraitTextInput = this.FindControl<TextBox>("PortraitTextInput");
        if (portraitTextInput != null) portraitTextInput.TextChanged += (s, e) => SaveRecoveryState();

        var volSliderForRecovery = this.FindControl<Slider>("VolumeSlider");
        if (volSliderForRecovery != null) volSliderForRecovery.PropertyChanged += (s, e) =>
        {
            if (e.Property == Slider.ValueProperty) SaveRecoveryState();
        };

        // Add global input filter
        UpdateTooltips();
        AddHandler(InputElement.KeyDownEvent, GlobalKeyDownHandler, RoutingStrategies.Tunnel);

        this.Loaded += async (s, e) => {
            // ---- Crash Recovery: check for previous crash BEFORE acquiring lock ----
            bool hadFault = _recovery.CheckFault();
            _recovery.AcquireLock();
            if (hadFault)
            {
                RuntimeLog.Info("RECOVERY", "Previous crash detected. Prompting user for recovery.");
                // Show native dialog BEFORE the full UI loads so user can choose
                bool shouldRestore = NativeDialog.ShowQuestion(
                    "The app was closed unexpectedly during your last session.\n\n" +
                    "Would you like to restore your previous work? This includes your video, " +
                    "trim points, speed settings, music, and all edits exactly as you left them.\n\n" +
                    "Click Yes to recover, or No to start fresh.",
                    "Fortnite Video Software - Session Recovery");

                if (shouldRestore)
                {
                    RuntimeLog.Info("RECOVERY", "User chose to restore previous session.");
                    await RestoreRecoveryStateAsync();
                }
                else
                {
                    RuntimeLog.Info("RECOVERY", "User chose to start fresh. Discarding recovery state.");
                    _recovery.ClearState();
                }
            }

            var store = new FortniteVideoSoftware.Core.Ipc.StateTransferStore();
            var state = await store.LoadAsync();
            var newObj = new System.Text.Json.Nodes.JsonObject();
            var preserveKeys = new[] { "MainWindowBounds", "VideoMergerBounds", "CropToolBounds", "GranularBounds", "MusicWizardBounds", "SettingsBounds" };
            foreach (var key in preserveKeys) {
                if (state.TryGetPropertyValue(key, out var gb)) {
                    newObj[key] = gb?.DeepClone();
                }
            }
            await store.SaveAsync(newObj);
            await WindowBoundsHelper.LoadBoundsAsync(this, "MainWindowBounds");
            await InitializeHardwareScanAsync();
            UpdatePortraitOverlay();
        };

        // MainWindow bounds saving moved to async OnClosing override below
    }

    private void SpeakerIcon_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ToggleMuteFromSpeakerIcon();
    }

    private void SpeakerIcon_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            ToggleMuteFromSpeakerIcon();
            e.Handled = true;
        }
    }

    private void ToggleMuteFromSpeakerIcon()
    {
        var volumeSlider = this.FindControl<Slider>("VolumeSlider");
        if (volumeSlider != null)
        {
            if (volumeSlider.Value > 0)
            {
                _previousVolume = volumeSlider.Value;
                volumeSlider.Value = 0;
            }
            else
            {
                volumeSlider.Value = _previousVolume > 0 ? _previousVolume : 100;
            }
            SaveRecoveryState();
        }
    }

    private void SeekTimelineFromPointer(PointerPressedEventArgs e, Canvas timelineCanvas, Slider timelineSlider)
    {
        if (_draggingStartMarker || _draggingEndMarker) return;

        double duration = _videoHost?.IpcClient?.Duration ?? 0.0;
        double width = timelineCanvas.Bounds.Width;
        if (duration <= 0 || width <= 0) return;

        double x = Math.Clamp(e.GetPosition(timelineCanvas).X, 0, width);
        double sliderValue = (x / width) * 100.0;
        double targetTime = (sliderValue / 100.0) * duration;

        try
        {
            _isTimerUpdatingSlider = true;
            timelineSlider.Value = sliderValue;
        }
        finally
        {
            _isTimerUpdatingSlider = false;
        }

        _ = SeekInternal(targetTime);
        e.Handled = true;
    }

    private void UpdateTooltips()
    {
        var kb = SettingsManager.Instance.KeyBinds;

        var playPauseBtn = this.FindControl<Button>("PlayPauseButton");
        if (playPauseBtn != null) ToolTip.SetTip(playPauseBtn, $"Play or pause the video ({kb.PlayPause})");

        var markStartBtn = this.FindControl<Button>("MarkStartButton");
        if (markStartBtn != null) ToolTip.SetTip(markStartBtn, $"Mark the beginning of your clip ({kb.MarkStart})");

        var markEndBtn = this.FindControl<Button>("MarkEndButton");
        if (markEndBtn != null) ToolTip.SetTip(markEndBtn, $"Mark the end of your clip ({kb.MarkEnd})");
    }



    private void OnVideoDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data.GetFiles() != null)
        {
            var popup = this.FindControl<Avalonia.Controls.Primitives.Popup>("DragOverlayPopup");
            if (popup != null) popup.IsOpen = true;
        }
    }

    private void OnVideoDragLeave(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var popup = this.FindControl<Avalonia.Controls.Primitives.Popup>("DragOverlayPopup");
        if (popup != null) popup.IsOpen = false;
    }

    private void OnVideoDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.GetFiles() != null)
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private async void OnVideoDrop(object? sender, DragEventArgs e)
    {
        var popup = this.FindControl<Avalonia.Controls.Primitives.Popup>("DragOverlayPopup");
        if (popup != null) popup.IsOpen = false;

        var files = e.Data.GetFiles();
        if (files == null)
        {
            return;
        }

        foreach (var file in files)
        {
            string path = file.Path.LocalPath;
            if (IsSupportedVideoPath(path))
            {
                await LoadVideoIntoEditorAsync(path, "dropped");
                return;
            }
        }

        ShowTacticalFeedback("Drop an MP4, MKV, AVI, or MOV file.");
        PlayUiSound();
    }

    private async void OnUploadVideoClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var options = new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Open Video",
            AllowMultiple = false,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Video Files") { Patterns = new[] { "*.mp4", "*.mkv", "*.avi", "*.mov" } } }
        };

        try
        {
            string startPath = "";
            try
            {
                string stateFile = _paths.SessionStateFile;
                if (File.Exists(stateFile))
                {
                    string json = File.ReadAllText(stateFile);
                    var state = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(json);
                    if (state != null && state["UploadVideoDirectory"] != null)
                    {
                        startPath = state["UploadVideoDirectory"]!.ToString();
                    }
                }

                if (string.IsNullOrEmpty(startPath) || !Directory.Exists(startPath))
                {
                    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string myVideos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                    string myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    string[] probes = new[]
                    {
                        System.IO.Path.Combine(localAppData, "Temp", "Highlights", "Fortnite"),
                        System.IO.Path.Combine(localAppData, "Temp", "Highlights"),
                        System.IO.Path.Combine(localAppData, "NVIDIA Corporation", "GeForce Experience", "Highlights"),
                        System.IO.Path.Combine(myVideos, "Highlights", "Fortnite"),
                        System.IO.Path.Combine(myVideos, "Fortnite"),
                        System.IO.Path.Combine(myVideos, "Highlights"),
                        System.IO.Path.Combine(myDocuments, "Highlights")
                    };

                    startPath = myVideos; // fallback
                    foreach (var probe in probes)
                    {
                        if (System.IO.Directory.Exists(probe))
                        {
                            startPath = probe;
                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(startPath) && Directory.Exists(startPath))
                {
                    try { Environment.CurrentDirectory = startPath; } catch { }
                    options.SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(startPath));
                }
            }
            catch { }
        }
        catch { }

        SetTimelinePopupsVisible(false);
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(options);
        SetTimelinePopupsVisible(true);

        if (files.Count > 0)
        {
            await LoadVideoIntoEditorAsync(files[0].Path.LocalPath, "uploaded");
        }
    }

    private static bool IsSupportedVideoPath(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".mp4" || ext == ".mkv" || ext == ".avi" || ext == ".mov";
    }

    private async Task LoadVideoIntoEditorAsync(string path, string source)
    {
        if (!IsSupportedVideoPath(path) || !File.Exists(path))
        {
            ShowTacticalFeedback("Choose an MP4, MKV, AVI, or MOV file.");
            PlayUiSound();
            return;
        }

        RuntimeLog.Info("UI", $"User {source} video: {path}");
        ShowTacticalFeedback("Loading video...");

        var videoHost = _videoHost;
        if (videoHost?.IpcClient == null)
        {
            ShowTacticalFeedback("Video player is still starting.");
            PlayUiSound();
            return;
        }

        _loadedVideoPath = path;
        _isTimelineDrawn = false;
        SaveUploadDirectory(path);
        ResetEditingStateForNewVideo();

        double previousDuration = videoHost.IpcClient.Duration;
        videoHost.IsVisible = true;
        await videoHost.IpcClient.LoadFileAsync(path);
        await videoHost.IpcClient.SetPropertyAsync("pause", "no");

        if (!await WaitForVideoMetadataAsync(previousDuration))
        {
            ShowTacticalFeedback("Video preview failed to load.");
            PlayUiSound();
            return;
        }

        ApplyDefaults();
        UpdateSpeedLabel();
        UpdateEstimatedQuality();
        UpdatePortraitOverlay();

        var uploadOverlay = this.FindControl<Border>("UploadOverlay");
        if (uploadOverlay != null) uploadOverlay.IsVisible = false;

        var timelineOverlay = this.FindControl<Border>("TimelineOverlay");
        if (timelineOverlay != null) timelineOverlay.IsVisible = true;

        EnableEditingControls();
        SaveRecoveryState();
    }

    private void SaveUploadDirectory(string path)
    {
        try
        {
            var state = File.Exists(_paths.SessionStateFile)
                ? System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(File.ReadAllText(_paths.SessionStateFile)) ?? new System.Text.Json.Nodes.JsonObject()
                : new System.Text.Json.Nodes.JsonObject();
            state["UploadVideoDirectory"] = Path.GetDirectoryName(path);
            File.WriteAllText(_paths.SessionStateFile, state.ToJsonString());
        }
        catch { }
    }

    private void ResetEditingStateForNewVideo()
    {
        _speedSegments.Clear();
        _musicWizardResult = null;
        StopMusicPreview();
        SetMusicButtonActive(false);
        _trimStartMs = 0;
        _trimEndMs = 0;
        _trimStartSet = false;
        _trimEndSet = false;
        _thumbnailSet = false;
        _thumbnailPosMs = 0;
        _freezeTimeMs = -1;
        _freezeDurationS = 1.0;

        var thumbBtn = this.FindControl<Button>("SetThumbnailButton");
        if (thumbBtn != null) { thumbBtn.Classes.Remove("Danger"); thumbBtn.Classes.Add("Primary"); }
        var thumbTxt = this.FindControl<TextBlock>("SetThumbnailText");
        if (thumbTxt != null) thumbTxt.Text = "SET THUMBNAIL";
        var markStartReset = this.FindControl<Button>("MarkStartButton");
        if (markStartReset != null) markStartReset.Content = "MARK START";
        var markEndReset = this.FindControl<Button>("MarkEndButton");
        if (markEndReset != null) markEndReset.Content = "MARK END";
        SetGranularButtonActive(false);
    }

    private async Task<bool> WaitForVideoMetadataAsync(double previousDuration)
    {
        if (_videoHost?.IpcClient == null) return false;

        for (int i = 0; i < 30; i++)
        {
            double duration = _videoHost.IpcClient.Duration;
            if (duration > 0 && (previousDuration <= 0 || Math.Abs(duration - previousDuration) > 0.01 || i >= 10))
            {
                return true;
            }

            await Task.Delay(100);
        }

        return _videoHost.IpcClient.Duration > 0;
    }

    /// <summary>
    /// Applies user-configured default values from Settings to the UI controls.
    /// </summary>
    private void ApplyDefaults()
    {
        var d = SettingsManager.Instance.Defaults;
        _baseSpeed = d.DefaultSpeed;
        var speedSliderReset = this.FindControl<SpinningWheelSlider>("MainSpeedSlider");
        if (speedSliderReset != null) speedSliderReset.Value = (int)Math.Round(_baseSpeed * 10.0, MidpointRounding.AwayFromZero);
        if (_videoHost?.IpcClient != null)
            _ = _videoHost.IpcClient.SetPropertyAsync("speed", _baseSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));

        var qs = this.FindControl<SpinningWheelSlider>("QualitySlider");
        if (qs != null) qs.Value = d.QualityIndex;

        var vol = this.FindControl<Slider>("VolumeSlider");
        if (vol != null) 
        {
            try
            {
                var state = System.IO.File.Exists(_paths.SessionStateFile)
                    ? System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(System.IO.File.ReadAllText(_paths.SessionStateFile)) ?? new System.Text.Json.Nodes.JsonObject()
                    : new System.Text.Json.Nodes.JsonObject();
                if (state.ContainsKey("MainVolume"))
                {
                    vol.Value = state["MainVolume"]?.GetValue<double>() ?? 100.0;
                }
                else
                {
                    vol.Value = 100.0;
                }
            }
            catch { vol.Value = 100.0; }
        }

        var portrait = this.FindControl<CheckBox>("PortraitModeCheckbox");
        if (portrait != null) portrait.IsChecked = d.PortraitMode;

        var bossHp = this.FindControl<CheckBox>("BossHpCheckbox");
        if (bossHp != null) bossHp.IsChecked = d.BossHp;

        var teammates = this.FindControl<CheckBox>("TeammatesCheckbox");
        if (teammates != null) teammates.IsChecked = d.ShowTeammates;

        var noFade = this.FindControl<CheckBox>("NoFadeCheckbox");
        if (noFade != null) noFade.IsChecked = d.NoFade;
    }

    /// <summary>
    /// Enables all editing controls and right-pane checkboxes after a video is uploaded.
    /// This reverts the initial disabled state set in XAML (IsEnabled="False").
    /// </summary>
    private void EnableEditingControls()
    {
        var gb = this.FindControl<Button>("GranularButton");
        if (gb != null) gb.IsEnabled = true;

        var thumbnail = this.FindControl<Button>("SetThumbnailButton");
        if (thumbnail != null) thumbnail.IsEnabled = true;

        var markStart = this.FindControl<Button>("MarkStartButton");
        if (markStart != null) markStart.IsEnabled = true;

        var playPause = this.FindControl<Button>("PlayPauseButton");
        if (playPause != null) playPause.IsEnabled = true;

        var markEnd = this.FindControl<Button>("MarkEndButton");
        if (markEnd != null) markEnd.IsEnabled = true;

        var process = this.FindControl<Button>("ProcessButton");
        if (process != null) process.IsEnabled = true;

        // Item #1: ADD MUSIC button
        var addMusic = this.FindControl<Button>("AddMusicButton");
        if (addMusic != null) addMusic.IsEnabled = true;

        // Right-pane checkboxes
        var portrait = this.FindControl<CheckBox>("PortraitModeCheckbox");
        if (portrait != null) portrait.IsEnabled = true;

        var bossHp = this.FindControl<CheckBox>("BossHpCheckbox");
        if (bossHp != null) bossHp.IsEnabled = true;

        var teammates = this.FindControl<CheckBox>("TeammatesCheckbox");
        if (teammates != null) teammates.IsEnabled = true;

        var noFade = this.FindControl<CheckBox>("NoFadeCheckbox");
        if (noFade != null) noFade.IsEnabled = true;

        // Issue #15: Enable sliders and update tooltips now that video is loaded
        var qs = this.FindControl<SpinningWheelSlider>("QualitySlider");
        if (qs != null)
        {
            qs.IsEnabled = true;
            qs.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            Avalonia.Controls.ToolTip.SetTip(qs, "Target maximum file size in MB");
        }

        var ss = this.FindControl<SpinningWheelSlider>("MainSpeedSlider");
        if (ss != null)
        {
            ss.IsEnabled = true;
            ss.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            Avalonia.Controls.ToolTip.SetTip(ss, "Playback speed multiplier");
        }

        var speedPresets = this.FindControl<StackPanel>("MainSpeedPresetsPanel");
        if (speedPresets != null) speedPresets.IsEnabled = true;
    }

    private void UpdatePortraitOverlay()
    {
        var mobileCheckbox = this.FindControl<CheckBox>("MobileCheckbox") ?? this.FindControl<CheckBox>("PortraitModeCheckbox");
        var portraitTextInput = this.FindControl<TextBox>("PortraitTextInput");

        bool isPortrait = mobileCheckbox?.IsChecked == true;

        if (portraitTextInput != null)
            portraitTextInput.IsVisible = isPortrait;

        // NOTE: PortraitDimmingGrid is kept hidden — the native MPV HWND paints over
        // all Avalonia overlays on Windows, so the grid is invisible. The actual portrait
        // dim guide is rendered exclusively via MPV's vf drawbox filter below.

        // Issue #2: Apply portrait dim via MPV video filters (vf), calculated from the
        // ACTUAL video dimensions (iw/ih in FFmpeg = intrinsic video pixel dimensions, NOT
        // the preview box). The dim bands are always proportional to the real video content.
        //
        // MATH — matches the export pipeline's "Portrait Canvas Trick" (see CoordinateMath.cs):
        // The export scales source to fill 1280×1920 internal space, then center-crops 1280px wide.
        // For the original video, the surviving horizontal width = ih × (1280/1920) = ih × (2/3).
        // So the clear center strip = ih*2/3, and each dim band = (iw - ih*2/3) / 2 = iw/2 - ih/3.
        //
        // Verification for 1920×1080: band_w = 960 - 360 = 600px, clear center = 720px.
        // (720/1920 = 37.5%, matching 1280/3413.33 from the scale→crop pipeline.)
        if (_videoHost?.IpcClient != null)
        {
            if (isPortrait)
            {
                // Left band: x=0, width = iw/2 - ih/3
                // Right band: x = iw/2 + ih/3, width = iw/2 - ih/3
                // (For 1920x1080: band_w = 960 - 360 = 600px, clear center = 720px)
                var dimFilter = "lavfi=[drawbox=x=0:y=0:w=iw/2-ih/3:h=ih:color=black@0.4:t=fill,drawbox=x=iw/2+ih/3:y=0:w=iw/2-ih/3:h=ih:color=black@0.4:t=fill]";
                _ = _videoHost.IpcClient.SetPropertyAsync("vf", dimFilter);
            }
            else
            {
                _ = _videoHost.IpcClient.SetPropertyAsync("vf", "");
            }
        }

        // Issue #3: Always recalculate quality when portrait mode changes
        UpdateEstimatedQuality();
    }

    private void PlayUiSound()
    {
        Task.Run(() =>
        {
            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
        });
    }

    /// <summary>
    /// Shows tactical feedback text (e.g., "🏁 01:23.45") that MUST be visible on top of the
    /// playing video. Because MpvVideoView extends NativeControlHost, the native MPV HWND
    /// paints over ALL Avalonia overlays on Windows — including MPV's own OSD layer (which
    /// does not render in --wid embedded mode). The ONLY reliable way to show overlay text
    /// on top of the embedded native video surface is to use an Avalonia Popup, which creates
    /// a SEPARATE native popup window (WS_POPUP) with its own z-order that floats above the
    /// MPV HWND.
    ///
    /// The Popup is positioned at bottom-center of the video area (VerticalOffset=-80 from the
    /// bottom edge of VideoAreaBorder), matching the original bottom-center feedback position.
    /// Styling: golden text (#facc15) on 50% semi-transparent dark blue (#0f1b3a) background.
    /// </summary>
    private void ShowTacticalFeedback(string text)
    {
        var popup = this.FindControl<Popup>("FeedbackPopup");
        var popupText = this.FindControl<TextBlock>("FeedbackPopupText");
        var popupBorder = this.FindControl<Border>("FeedbackPopupBorder");
        var videoBorder = this.FindControl<Border>("VideoAreaBorder");
        FloatingFeedback.Show(popup, popupBorder, popupText, videoBorder, text);
    }

    private void ShowTimelineGlow(double timeMs, Avalonia.Media.IBrush color)
    {
        var canvas = this.FindControl<Canvas>("TimelineMarkersCanvas");
        if (canvas == null || _videoHost?.IpcClient == null) return;

        double duration = _videoHost.IpcClient.Duration;
        if (duration <= 0) return;

        double canvasWidth = canvas.Bounds.Width;
        double targetX = (timeMs / 1000.0 / duration) * canvasWidth;

        var glow = new Avalonia.Controls.Shapes.Rectangle
        {
            Fill = color,
            Width = 2,
            Height = canvas.Bounds.Height,
            Opacity = 0.8,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(glow, targetX - 1);
        canvas.Children.Add(glow);

        Task.Run(async () =>
        {
            double w = 2;
            double op = 0.8;
            while (op > 0)
            {
                w += 2;
                op -= 0.05;
                await Task.Delay(16);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    glow.Width = w;
                    glow.Opacity = op;
                    Canvas.SetLeft(glow, targetX - (w / 2));
                });
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => canvas.Children.Remove(glow));
        });
    }

    private void AttachThumbnailCameraMarkerInteractions(Control marker, Canvas timelineCanvas, double durationSeconds)
    {
        marker.PointerEntered += (_, _) => SetTimelineCameraHover(marker, true);
        marker.PointerExited += (_, _) =>
        {
            if (!_isDraggingThumbnailMarker)
            {
                SetTimelineCameraHover(marker, false);
            }
        };
        marker.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(marker).Properties.IsLeftButtonPressed)
            {
                return;
            }

            _isThumbnailMarkerSelected = true;
            _isDraggingThumbnailMarker = true;
            if (_thumbnailMarkerIconAntsRef != null) _thumbnailMarkerIconAntsRef.IsVisible = true;
            if (_thumbnailMarkerLineAntsRef != null) _thumbnailMarkerLineAntsRef.IsVisible = true;
            marker.Focus();
            SetTimelineCameraHover(marker, true);
            MoveThumbnailMarkerToCanvasX(e.GetPosition(timelineCanvas).X, timelineCanvas, durationSeconds, marker, seekPreview: true);
            e.Pointer.Capture(marker);
            e.Handled = true;
        };
        marker.PointerMoved += (_, e) =>
        {
            if (!_isDraggingThumbnailMarker)
            {
                return;
            }

            MoveThumbnailMarkerToCanvasX(e.GetPosition(timelineCanvas).X, timelineCanvas, durationSeconds, marker, seekPreview: true);
            e.Handled = true;
        };
        marker.PointerReleased += (_, e) =>
        {
            if (!_isDraggingThumbnailMarker)
            {
                return;
            }

            MoveThumbnailMarkerToCanvasX(e.GetPosition(timelineCanvas).X, timelineCanvas, durationSeconds, marker, seekPreview: true);
            _isDraggingThumbnailMarker = false;
            _isThumbnailMarkerSelected = true;
            e.Pointer.Capture(null);
            SetTimelineCameraHover(marker, false);
            UpdateTimelineMarkers();
            UpdateEstimatedQuality();
            SaveRecoveryState();
            e.Handled = true;
        };
    }

    private void MoveThumbnailMarkerToCanvasX(double canvasX, Canvas timelineCanvas, double durationSeconds, Control marker, bool seekPreview)
    {
        double width = timelineCanvas.Bounds.Width;
        if (durationSeconds <= 0 || width <= 0)
        {
            return;
        }

        double clampedX = Math.Clamp(canvasX, 0, width);
        _thumbnailPosMs = (clampedX / width) * durationSeconds * 1000.0;
        _thumbnailSet = true;
        Canvas.SetLeft(marker, ClampTimelineCameraLeft(clampedX, width));

        if (seekPreview)
        {
            SeekMainPreviewToMarkerMs(_thumbnailPosMs);
        }
    }

    private void MoveThumbnailMarkerByPixels(int pixelDelta)
    {
        var canvas = this.FindControl<Canvas>("TimelineMarkersCanvas");
        double duration = _videoHost?.IpcClient?.Duration ?? 0.0;
        double width = canvas?.Bounds.Width ?? 0.0;
        if (!_thumbnailSet || canvas == null || duration <= 0 || width <= 0)
        {
            return;
        }

        double deltaMs = (duration * 1000.0 / width) * pixelDelta;
        _thumbnailPosMs = Math.Clamp(_thumbnailPosMs + deltaMs, 0, duration * 1000.0);
        SeekMainPreviewToMarkerMs(_thumbnailPosMs);
        UpdateTimelineMarkers();
        UpdateEstimatedQuality();
        SaveRecoveryState();
    }

    private void SeekMainPreviewToMarkerMs(double markerMs)
    {
        if (_videoHost?.IpcClient == null)
        {
            return;
        }

        _isCurrentlyFrozen = false;
        _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
        _ = _videoHost.IpcClient.SendCommandAsync(
            "seek",
            (markerMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "absolute");
    }

    private double CalculateEffectiveDurationMs(double trimStartMs, double trimEndMs, double baseSpeed)
    {
        if (_speedSegments == null || _speedSegments.Count == 0)
        {
            return (trimEndMs - trimStartMs) / baseSpeed;
        }

        double totalMs = 0.0;
        double cursor = trimStartMs;

        var sortedSegments = new System.Collections.Generic.List<FortniteVideoSoftware.Core.Media.SpeedSegment>(_speedSegments);
        sortedSegments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

        foreach (var seg in sortedSegments)
        {
            double segStart = Math.Max(trimStartMs, Math.Max(seg.StartMs, cursor));
            double segEnd = Math.Min(trimEndMs, seg.EndMs);
            if (segEnd <= segStart) continue;

            if (segStart > cursor)
            {
                totalMs += (segStart - cursor) / baseSpeed;
            }
            if (Math.Abs(seg.Speed) < 0.001)
            {
                totalMs += (segEnd - segStart);
            }
            else
            {
                totalMs += (segEnd - segStart) / Math.Max(0.001, seg.Speed);
            }
            cursor = Math.Max(cursor, segEnd);
        }
        if (cursor < trimEndMs)
        {
            totalMs += (trimEndMs - cursor) / baseSpeed;
        }
        return Math.Max(1.0, totalMs);
    }

    private void UpdateEstimatedQuality()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            var label = this.FindControl<TextBlock>("QualityLabel");
            var slider = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("QualitySlider");
            if (label == null || slider == null || _videoHost?.IpcClient == null) return;

            double duration = _videoHost.IpcClient.Duration * 1000.0;
            if (duration <= 0) { label.Text = ""; return; }

            int idx = slider.Value;
            double targetMb = 5 + idx * 5;
            if (idx >= 20) {
                label.Text = "Max CQ";
                label.Foreground = Avalonia.Media.Brush.Parse("#2ecc71");
                return;
            }

            double end = _trimEndMs > 0 ? _trimEndMs : duration;
            double start = _trimStartMs > 0 ? _trimStartMs : 0;

            double actualDurationMs = CalculateEffectiveDurationMs(start, end, _baseSpeed);
            double durSec = Math.Max(0.1, actualDurationMs / 1000.0) + 0.1;

            double audioKbps = 192;
            if (targetMb * 1024 < durSec * 48) audioKbps = 64;

            int w = 1920;
            int h = 1080;
            var cb = this.FindControl<CheckBox>("PortraitModeCheckbox");
            if (cb != null && cb.IsChecked == true)
            {
                w = 1080;
                h = 1920;
            }

            double videoKbps = ((targetMb * 8192.0) - (audioKbps * durSec)) / durSec;
            if (videoKbps < 100) videoKbps = 100;

            double bpp = (videoKbps * 1000.0) / (w * h * 60.0);
            if (cb == null || cb.IsChecked != true)
            {
                bpp /= 1.5;
            }

            string desc = "Standard";
            string color = "White";

            var spectrum = new (double th, string d, string c)[] {
                (0.02, "Unwatchable", "#e74c3c"),
                (0.04, "Pixelated", "#e74c3c"),
                (0.06, "Blurry", "#e74c3c"),
                (0.1, "Clear", "White"),
                (0.15, "Sharp", "#2ecc71"),
                (0.25, "Crisp-Clear", "#2ecc71"),
                (99.0, "Lifelike", "#2ecc71")
            };

            for (int i=0; i<spectrum.Length; i++) {
                if (bpp < spectrum[i].th) {
                    desc = spectrum[i].d;
                    color = spectrum[i].c;
                    double prev = i > 0 ? spectrum[i-1].th : 0.0;
                    double mid = (spectrum[i].th + prev) / 2.0;
                    if (spectrum[i].th < 90.0) {
                        desc += bpp < mid ? "-" : "+";
                    }
                    break;
                }
            }

            label.Text = desc;
            label.Foreground = Avalonia.Media.Brush.Parse(color);
        });
    }

    private void UpdateSpeedLabel()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            var label = this.FindControl<TextBlock>("MainSpeedLabel");
            if (label == null) return;

            double speed = _baseSpeed;
            string desc;
            string color;

            if (speed <= 0.5) { desc = "Slow Motion"; color = "#3498db"; }
            else if (speed <= 0.8) { desc = "Cinematic"; color = "#3498db"; }
            else if (speed < 1.05) { desc = "Normal"; color = "White"; }
            else if (speed <= 1.2) { desc = "Slight Boost"; color = "#f1c40f"; }
            else if (speed <= 1.5) { desc = "Fast"; color = "#f39c12"; }
            else if (speed <= 2.0) { desc = "Very Fast"; color = "#e67e22"; }
            else if (speed <= 3.0) { desc = "Turbo"; color = "#e74c3c"; }
            else { desc = "Extreme"; color = "#e74c3c"; }

            label.Text = $"{speed:F1}x — {desc}";
            label.Foreground = Avalonia.Media.Brush.Parse(color);
        });
    }

    private void ApplyMainSpeedPreset(double speed)
    {
        var speedSlider = this.FindControl<SpinningWheelSlider>("MainSpeedSlider");
        SpeedPresetButtons.SetSpinningWheelValue(speedSlider, speed);

        _baseSpeed = Math.Clamp(speed, 0.1, 4.0);
        if (_videoHost?.IpcClient != null)
        {
            _ = _videoHost.IpcClient.SetPropertyAsync(
                "speed",
                _baseSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        }

        UpdateEstimatedQuality();
        UpdateSpeedLabel();
        SaveRecoveryState();
    }

    private void SetTimelinePopupsVisible(bool visible)
    {
        var canvas = this.FindControl<Canvas>("TimelineMarkersCanvas");
        if (canvas != null)
        {
            foreach (var child in canvas.Children)
            {
                if (child is Avalonia.Controls.Primitives.Popup popup)
                {
                    popup.IsOpen = visible;
                }
            }
        }
        if (_musicStartPopupRef != null) _musicStartPopupRef.IsVisible = visible;
        if (_musicEndPopupRef != null) _musicEndPopupRef.IsVisible = visible;
    }

    private void UpdateTimelineMarkers()
    {
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineMarkersCanvas");
        var scaleCanvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineScaleCanvas");
        if (canvas == null || _videoHost?.IpcClient == null) return;

        double duration = _videoHost.IpcClient.Duration;

        if (duration <= 0) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            canvas.Children.Clear();
            scaleCanvas?.Children.Clear();
            double canvasWidth = canvas.Bounds.Width;
            if (canvasWidth <= 0) return;
            double ClampLabelLeft(double desired, double approxWidth)
                => Math.Max(0, Math.Min(Math.Max(0, canvasWidth - approxWidth), desired));
            const double trimMarkerWidth = 3.0;
            const double trimMarkerTop = -8.0;
            double trimMarkerHeight = Math.Max(1, canvas.Bounds.Height);

            // ── DRAW TRIM REGION (bottom layer — semi-transparent gray bar) ──
            if (_trimStartSet && _trimEndMs > _trimStartMs)
            {
                double regStartX = (_trimStartMs / 1000.0 / duration) * canvasWidth;
                double regEndX = (_trimEndMs / 1000.0 / duration) * canvasWidth;
                var regionRect = new Avalonia.Controls.Shapes.Rectangle
                {
                    Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(180, 128, 128, 128)),
                    Width = Math.Max(2, regEndX - regStartX),
                    Height = trimMarkerHeight,
                    IsHitTestVisible = false
                };
                Avalonia.Controls.Canvas.SetLeft(regionRect, regStartX);
                Avalonia.Controls.Canvas.SetTop(regionRect, trimMarkerTop);
                canvas.Children.Add(regionRect);
            }

            // ── DRAW MUSIC OVERLAY ──


            // ── DRAW SPEED SEGMENTS (above region, below ticks/markers) ──
            if (_speedSegments != null && _speedSegments.Count > 0)
            {
                foreach (var seg in _speedSegments)
                {
                    double segStartX = (seg.StartMs / 1000.0 / duration) * canvasWidth;
                    double segEndX = (seg.EndMs / 1000.0 / duration) * canvasWidth;
                    double segW = Math.Max(2, segEndX - segStartX);

                    // Color scheme: blue=freeze, red=slower than base, green=base or faster
                    Avalonia.Media.Color segColor;
                    if (seg.Speed < 0.01)
                        segColor = Avalonia.Media.Color.FromArgb(120, 96, 165, 250);   // blue — freeze frame
                    else if (seg.Speed < _baseSpeed - 0.0001)
                        segColor = Avalonia.Media.Color.FromArgb(100, 239, 68, 68);    // red — slower than base
                    else
                        segColor = Avalonia.Media.Color.FromArgb(100, 34, 197, 94);    // green — base speed or faster

                    var segRect = new Avalonia.Controls.Shapes.Rectangle
                    {
                        Width = segW,
                        Height = trimMarkerHeight,
                        Fill = new Avalonia.Media.SolidColorBrush(segColor),
                        IsHitTestVisible = false
                    };
                    Avalonia.Controls.Canvas.SetLeft(segRect, segStartX);
                    Avalonia.Controls.Canvas.SetTop(segRect, trimMarkerTop);
                    canvas.Children.Add(segRect);
                }
            }

            // DRAW TIMELINE SCALES
            double tickInterval = 5;
            if (duration > 3600) tickInterval = 300;
            else if (duration > 1800) tickInterval = 60;
            else if (duration > 300) tickInterval = 30;
            else if (duration > 60) tickInterval = 10;

            for (double t = 0; t <= duration; t += tickInterval)
            {
                double tx = (t / duration) * canvasWidth;

                var tickLine = new Avalonia.Controls.Shapes.Rectangle { Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(60, 255, 255, 255)), Width = 1, Height = canvas.Bounds.Height, IsHitTestVisible = false };
                Avalonia.Controls.Canvas.SetLeft(tickLine, tx);
                canvas.Children.Add(tickLine);

                bool shouldShowTickLabel = t > 0.001 && duration - t > 0.001;
                if (scaleCanvas != null && shouldShowTickLabel)
                {
                    var tickText = new TextBlock {
                        Text = TimeSpan.FromSeconds(t).ToString(t >= 3600 ? "h\\:mm\\:ss" : "m\\:ss"),
                        Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(180, 255, 255, 255)),
                        FontSize = 9
                    };
                    Avalonia.Controls.Canvas.SetLeft(tickText, ClampLabelLeft(tx + 2, 36));
                    Avalonia.Controls.Canvas.SetTop(tickText, 0);
                    scaleCanvas.Children.Add(tickText);
                }
            }

            if (_thumbnailSet)
            {
                double thumbMs = Math.Clamp(_thumbnailPosMs, 0, duration * 1000.0);
                double thumbX = (thumbMs / 1000.0 / duration) * canvasWidth;

                // redundant thumbLine removed

                var cameraIcon = CreateTimelineCameraIcon(
                    _isThumbnailMarkerSelected || _isDraggingThumbnailMarker,
                    _marchingAntsOffset,
                    out _thumbnailMarkerIconAntsRef,
                    out _thumbnailMarkerLineAntsRef);
                Avalonia.Controls.Canvas.SetTop(cameraIcon, -79);
                Avalonia.Controls.Canvas.SetLeft(cameraIcon, ClampTimelineCameraLeft(thumbX, canvasWidth));
                AttachThumbnailCameraMarkerInteractions(cameraIcon, canvas, duration);
                canvas.Children.Add(cameraIcon);
            }

            if (_trimStartSet)
            {
                double startX = (_trimStartMs / 1000.0 / duration) * canvasWidth;
                var startRect = new Avalonia.Controls.Shapes.Rectangle { Fill = Avalonia.Media.Brushes.SeaGreen, Width = trimMarkerWidth, Height = trimMarkerHeight, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast) };
                Avalonia.Controls.Canvas.SetLeft(startRect, startX - (trimMarkerWidth / 2.0));
                Avalonia.Controls.Canvas.SetTop(startRect, trimMarkerTop);

                startRect.PointerEntered += (s,e) => startRect.Fill = Avalonia.Media.Brushes.MediumSeaGreen;
                startRect.PointerExited += (s,e) => startRect.Fill = Avalonia.Media.Brushes.SeaGreen;
                startRect.PointerPressed += (s,e) => { _draggingStartMarker = true; e.Pointer.Capture(startRect); e.Handled = true; };
                startRect.PointerReleased += (s,e) => { _draggingStartMarker = false; e.Pointer.Capture(null); UpdateEstimatedQuality(); SaveRecoveryState(); };
                startRect.PointerMoved += (s,e) => {
                    if (_draggingStartMarker) {
                        double pos = e.GetPosition(canvas).X;
                        double val = (pos / canvas.Bounds.Width) * duration * 1000;
                        if (val < 0) val = 0;
                        if (val > _trimEndMs && _trimEndMs > 0) val = _trimEndMs;
                        SetTrimStart(val);
                        Avalonia.Controls.Canvas.SetLeft(startRect, ((_trimStartMs / 1000.0 / duration) * canvasWidth) - (trimMarkerWidth / 2.0));
                        Avalonia.Controls.Canvas.SetTop(startRect, trimMarkerTop);
                    }
                };
                canvas.Children.Add(startRect);

                var startText = new TextBlock { Text = "START", Foreground = Avalonia.Media.Brushes.SeaGreen, FontSize = 9, FontWeight = Avalonia.Media.FontWeight.Bold, Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#80000000")), Padding = new Avalonia.Thickness(2,0) };
                if (scaleCanvas != null)
                {
                    Avalonia.Controls.Canvas.SetLeft(startText, ClampLabelLeft(startX + 5, 36));
                    Avalonia.Controls.Canvas.SetTop(startText, 0);
                    scaleCanvas.Children.Add(startText);
                }

                startRect.PointerMoved += (s,e) => {
                    if (_draggingStartMarker) {
                        startText.Text = TimeSpan.FromMilliseconds(_trimStartMs).ToString("hh\\:mm\\:ss\\.ff");
                        Avalonia.Controls.Canvas.SetLeft(startText, ClampLabelLeft(((_trimStartMs / 1000.0 / duration) * canvasWidth) + 5, 78));
                        Avalonia.Controls.Canvas.SetTop(startText, 0);
                    }
                };
            }

            if (_trimEndMs > 0)
            {
                double endX = (_trimEndMs / 1000.0 / duration) * canvasWidth;
                var endRect = new Avalonia.Controls.Shapes.Rectangle { Fill = Avalonia.Media.Brushes.SeaGreen, Width = trimMarkerWidth, Height = trimMarkerHeight, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast) };
                Avalonia.Controls.Canvas.SetLeft(endRect, endX - (trimMarkerWidth / 2.0));
                Avalonia.Controls.Canvas.SetTop(endRect, trimMarkerTop);

                endRect.PointerEntered += (s,e) => endRect.Fill = Avalonia.Media.Brushes.MediumSeaGreen;
                endRect.PointerExited += (s,e) => endRect.Fill = Avalonia.Media.Brushes.SeaGreen;
                endRect.PointerPressed += (s,e) => { _draggingEndMarker = true; e.Pointer.Capture(endRect); e.Handled = true; };
                endRect.PointerReleased += (s,e) => { _draggingEndMarker = false; e.Pointer.Capture(null); UpdateEstimatedQuality(); SaveRecoveryState(); };
                endRect.PointerMoved += (s,e) => {
                    if (_draggingEndMarker) {
                        double pos = e.GetPosition(canvas).X;
                        _trimEndMs = (pos / canvas.Bounds.Width) * duration * 1000;
                        if (_trimEndMs > duration * 1000) _trimEndMs = duration * 1000;
                        if (_trimEndMs < _trimStartMs) _trimEndMs = _trimStartMs;
                        Avalonia.Controls.Canvas.SetLeft(endRect, ((_trimEndMs / 1000.0 / duration) * canvasWidth) - (trimMarkerWidth / 2.0));
                        Avalonia.Controls.Canvas.SetTop(endRect, trimMarkerTop);
                    }
                };
                canvas.Children.Add(endRect);

                var endText = new TextBlock { Text = "END", Foreground = Avalonia.Media.Brushes.SeaGreen, FontSize = 9, FontWeight = Avalonia.Media.FontWeight.Bold, Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#80000000")), Padding = new Avalonia.Thickness(2,0) };
                if (scaleCanvas != null)
                {
                    Avalonia.Controls.Canvas.SetLeft(endText, ClampLabelLeft(endX - 28, 28));
                    Avalonia.Controls.Canvas.SetTop(endText, 0);
                    scaleCanvas.Children.Add(endText);
                }

                endRect.PointerMoved += (s,e) => {
                    if (_draggingEndMarker) {
                        endText.Text = TimeSpan.FromMilliseconds(_trimEndMs).ToString("hh\\:mm\\:ss\\.ff");
                        Avalonia.Controls.Canvas.SetLeft(endText, ClampLabelLeft(((_trimEndMs / 1000.0 / duration) * canvasWidth) - 55, 78));
                        Avalonia.Controls.Canvas.SetTop(endText, 0);
                    }
                };
            }

            if (_freezeTimeMs >= 0)
            {
                double freezeX = (_freezeTimeMs / 1000.0 / duration) * canvasWidth;
                var freezeCam = CreateTimelineCameraIcon();
                Avalonia.Controls.Canvas.SetTop(freezeCam, -79);
                Avalonia.Controls.Canvas.SetLeft(freezeCam, ClampTimelineCameraLeft(freezeX, canvasWidth));
                canvas.Children.Add(freezeCam);
            }

            // ── DRAW MUSIC OVERLAY (Drawn last to be on top) ──
            if (_musicWizardResult != null && !string.IsNullOrEmpty(_musicWizardResult.MusicFilePath))
            {
                double mStartX = (_musicWizardResult.TimelineStartSeconds / duration) * canvasWidth;
                double mEndX = (_musicWizardResult.TimelineEndSeconds / duration) * canvasWidth;

                var musicRect = new Avalonia.Controls.Shapes.Rectangle
                {
                    Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(80, 255, 105, 180)), // Increased transparency (120 -> 80)
                    Width = Math.Max(2, mEndX - mStartX),
                    Height = trimMarkerHeight,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    IsHitTestVisible = true
                };

                if (_isMusicBlockFocused)
                {
                    musicRect.Stroke = Avalonia.Media.Brushes.Yellow;
                    musicRect.StrokeThickness = 1;
                    musicRect.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(2, 2);
                    musicRect.StrokeDashOffset = _marchingAntsOffset;
                }

                _musicBlockRectRef = musicRect;

                Avalonia.Controls.Canvas.SetLeft(musicRect, mStartX);
                Avalonia.Controls.Canvas.SetTop(musicRect, trimMarkerTop);
                canvas.Children.Add(musicRect);

                var startNoteText = new TextBlock { Text = "♫", FontFamily = new Avalonia.Media.FontFamily("Segoe UI Symbol"), Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 105, 180)), FontSize = 40, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Effect = new Avalonia.Media.DropShadowDirectionEffect { Color = Avalonia.Media.Colors.Black, BlurRadius = 4, Opacity = 0.8 } };
                var musicStartBorder = new Avalonia.Controls.Border {
                    Background = Avalonia.Media.Brushes.Transparent,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast),
                    Padding = new Avalonia.Thickness(4, 4),
                    Child = startNoteText
                };
                
                // Glowing hover effect
                musicStartBorder.PointerEntered += (s, e) => {
                    startNoteText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 20, 147)); // DeepPink (brighter)
                };
                musicStartBorder.PointerExited += (s, e) => {
                    startNoteText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 105, 180));
                };
                
                _musicStartPopupRef = musicStartBorder; // Keep ref as Control instead of Popup
                Avalonia.Controls.Canvas.SetTop(musicStartBorder, -15);
                Avalonia.Controls.Canvas.SetLeft(musicStartBorder, mStartX - 20);
                canvas.Children.Add(musicStartBorder);

                var endNoteText = new TextBlock { Text = "♫", FontFamily = new Avalonia.Media.FontFamily("Segoe UI Symbol"), Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 105, 180)), FontSize = 40, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Effect = new Avalonia.Media.DropShadowDirectionEffect { Color = Avalonia.Media.Colors.Black, BlurRadius = 4, Opacity = 0.8 } };
                var musicEndBorder = new Avalonia.Controls.Border {
                    Background = Avalonia.Media.Brushes.Transparent,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast),
                    Padding = new Avalonia.Thickness(4, 4),
                    Child = endNoteText
                };
                
                musicEndBorder.PointerEntered += (s, e) => {
                    endNoteText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 20, 147));
                };
                musicEndBorder.PointerExited += (s, e) => {
                    endNoteText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 105, 180));
                };

                _musicEndPopupRef = musicEndBorder;
                Avalonia.Controls.Canvas.SetTop(musicEndBorder, -15);
                Avalonia.Controls.Canvas.SetLeft(musicEndBorder, mEndX - 20);
                canvas.Children.Add(musicEndBorder);

                bool draggingMusicStart = false;
                bool draggingMusicEnd = false;
                bool draggingMusicBlock = false;
                double dragStartPointerX = 0;
                double dragInitialStartSec = 0;
                double dragInitialEndSec = 0;

                musicStartBorder.PointerPressed += (s, e) => {
                    _isMusicBlockFocused = false;
                    draggingMusicStart = true;
                    e.Pointer.Capture(musicStartBorder);
                    e.Handled = true;
                };
                musicStartBorder.PointerReleased += (s, e) => {
                    draggingMusicStart = false;
                    e.Pointer.Capture(null);
                    UpdateTimelineMarkers();
                    SaveRecoveryState();
                };
                musicStartBorder.PointerMoved += (s, e) => {
                    if (draggingMusicStart) {
                        double currentX = e.GetPosition(canvas).X;

                        // Magnetic Snap to MARK START
                        double markStartX = (_trimStartMs / 1000.0 / duration) * canvasWidth;
                        if (Math.Abs(currentX - markStartX) < 10) currentX = markStartX;

                        double newStart = (currentX / canvasWidth) * duration;
                        if (newStart < 0) newStart = 0;
                        if (newStart >= _musicWizardResult.TimelineEndSeconds - 0.5) newStart = _musicWizardResult.TimelineEndSeconds - 0.5;
                        _musicWizardResult.TimelineStartSeconds = newStart;
                        double nx = (newStart / duration) * canvasWidth;
                        Avalonia.Controls.Canvas.SetLeft(musicRect, nx);
                        Avalonia.Controls.Canvas.SetLeft(musicStartBorder, nx - 20);
                        musicRect.Width = Math.Max(2, ((_musicWizardResult.TimelineEndSeconds / duration) * canvasWidth) - nx);
                    }
                };

                musicEndBorder.PointerPressed += (s, e) => {
                    _isMusicBlockFocused = false;
                    draggingMusicEnd = true;
                    e.Pointer.Capture(musicEndBorder);
                    e.Handled = true;
                };
                musicEndBorder.PointerReleased += (s, e) => {
                    draggingMusicEnd = false;
                    e.Pointer.Capture(null);
                    UpdateTimelineMarkers();
                    SaveRecoveryState();
                };
                musicEndBorder.PointerMoved += (s, e) => {
                    if (draggingMusicEnd) {
                        double currentX = e.GetPosition(canvas).X;

                        // Magnetic Snap to MARK END
                        double markEndX = (_trimEndMs / 1000.0 / duration) * canvasWidth;
                        if (Math.Abs(currentX - markEndX) < 10) currentX = markEndX;

                        double newEnd = (currentX / canvasWidth) * duration;
                        if (newEnd > duration) newEnd = duration;
                        if (newEnd <= _musicWizardResult.TimelineStartSeconds + 0.5) newEnd = _musicWizardResult.TimelineStartSeconds + 0.5;
                        _musicWizardResult.TimelineEndSeconds = newEnd;
                        double nx = (newEnd / duration) * canvasWidth;
                        Avalonia.Controls.Canvas.SetLeft(musicEndBorder, nx - 20);
                        musicRect.Width = Math.Max(2, nx - ((_musicWizardResult.TimelineStartSeconds / duration) * canvasWidth));
                    }
                };

                musicRect.PointerPressed += (s, e) => {
                    if (!_isMusicBlockFocused)
                    {
                        _isMusicBlockFocused = true;
                        musicRect.Stroke = Avalonia.Media.Brushes.Yellow;
                        musicRect.StrokeThickness = 1;
                        musicRect.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(2, 2);
                    }
                    draggingMusicBlock = true;
                    dragStartPointerX = e.GetPosition(canvas).X;
                    dragInitialStartSec = _musicWizardResult.TimelineStartSeconds;
                    dragInitialEndSec = _musicWizardResult.TimelineEndSeconds;
                    e.Pointer.Capture(musicRect);
                    e.Handled = true;
                };
                musicRect.PointerReleased += (s, e) => {
                    draggingMusicBlock = false;
                    e.Pointer.Capture(null);
                    UpdateTimelineMarkers();
                    SaveRecoveryState();
                };
                musicRect.PointerMoved += (s, e) => {
                    if (draggingMusicBlock) {
                        double currentX = e.GetPosition(canvas).X;
                        double dxSeconds = ((currentX - dragStartPointerX) / canvasWidth) * duration;
                        double dur = dragInitialEndSec - dragInitialStartSec;
                        double rawNewStart = dragInitialStartSec + dxSeconds;
                        double rawNewEnd = dragInitialEndSec + dxSeconds;

                        // Magnetic Snapping logic for the whole block
                        double markStartSec = _trimStartMs / 1000.0;
                        double markEndSec = _trimEndMs / 1000.0;

                        double distStartToMarkStart = Math.Abs((rawNewStart / duration) * canvasWidth - (markStartSec / duration) * canvasWidth);
                        double distEndToMarkEnd = Math.Abs((rawNewEnd / duration) * canvasWidth - (markEndSec / duration) * canvasWidth);

                        double newStart = rawNewStart;
                        double newEnd = rawNewEnd;

                        if (distStartToMarkStart < 10 && distStartToMarkStart <= distEndToMarkEnd)
                        {
                            newStart = markStartSec;
                            newEnd = newStart + dur;
                        }
                        else if (distEndToMarkEnd < 10)
                        {
                            newEnd = markEndSec;
                            newStart = newEnd - dur;
                        }

                        if (newStart < 0) {
                            newStart = 0;
                            newEnd = dur;
                        }
                        if (newEnd > duration) {
                            newEnd = duration;
                            newStart = duration - dur;
                        }

                        _musicWizardResult.TimelineStartSeconds = newStart;
                        _musicWizardResult.TimelineEndSeconds = newEnd;

                        double nStartX = (newStart / duration) * canvasWidth;
                        double nEndX = (newEnd / duration) * canvasWidth;

                        Avalonia.Controls.Canvas.SetLeft(musicRect, nStartX);
                        Avalonia.Controls.Canvas.SetLeft(musicStartBorder, nStartX - 20);
                        Avalonia.Controls.Canvas.SetLeft(musicEndBorder, nEndX - 20);
                    }
                };
            }
        });
    }

    public static Control CreateTimelineCameraIcon()
    {
        return CreateTimelineCameraIcon(false, 0, out _, out _);
    }

    public static Control CreateTimelineCameraIcon(
        bool isSelected,
        double marchingAntsOffset,
        out Avalonia.Controls.Shapes.Rectangle iconAnts,
        out Avalonia.Controls.Shapes.Rectangle lineAnts)
    {
        var icon = new Border
        {
            Width = 36,
            Height = 28,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(220, 15, 23, 42)),
            BorderBrush = Avalonia.Media.Brushes.Gold,
            BorderThickness = new Avalonia.Thickness(2),
            CornerRadius = new Avalonia.CornerRadius(4),
            IsHitTestVisible = false
        };

        var hoverIconGlow = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = "TimelineCameraIconGlow",
            Width = 44,
            Height = 36,
            Stroke = Avalonia.Media.Brushes.Gold,
            StrokeThickness = 2,
            Opacity = 0,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(hoverIconGlow, 4);
        Avalonia.Controls.Canvas.SetTop(hoverIconGlow, 24);

        var hoverLineGlow = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = "TimelineCameraLineGlow",
            Width = 8,
            Height = 53,
            Stroke = Avalonia.Media.Brushes.Gold,
            StrokeThickness = 2,
            Opacity = 0,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(hoverLineGlow, 20);
        Avalonia.Controls.Canvas.SetTop(hoverLineGlow, 53);

        var canvas = new Canvas { Width = 36, Height = 28 };
        var top = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = 12,
            Height = 6,
            Fill = Avalonia.Media.Brushes.Gold,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(top, 6);
        Avalonia.Controls.Canvas.SetTop(top, 2);
        canvas.Children.Add(top);

        var lens = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 12,
            Height = 12,
            Stroke = Avalonia.Media.Brushes.Gold,
            StrokeThickness = 2.8,
            Fill = Avalonia.Media.Brushes.Transparent,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(lens, 12);
        Avalonia.Controls.Canvas.SetTop(lens, 10);
        canvas.Children.Add(lens);

        var flash = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 4,
            Height = 4,
            Fill = Avalonia.Media.Brushes.Gold,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(flash, 26);
        Avalonia.Controls.Canvas.SetTop(flash, 8);
        canvas.Children.Add(flash);

        icon.Child = canvas;

        var outerCanvas = new Canvas
        {
            Width = 52,
            Height = 103,
            ClipToBounds = false,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Focusable = true
        };

        var hitBox = new Border
        {
            Width = 52,
            Height = 103,
            Background = Avalonia.Media.Brushes.Transparent,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        Avalonia.Controls.Canvas.SetLeft(hitBox, 0);
        Avalonia.Controls.Canvas.SetTop(hitBox, 0);
        outerCanvas.Children.Add(hitBox);
        outerCanvas.Children.Add(hoverIconGlow);
        outerCanvas.Children.Add(hoverLineGlow);
        Avalonia.Controls.Canvas.SetTop(icon, 28);
        Avalonia.Controls.Canvas.SetLeft(icon, 8);
        outerCanvas.Children.Add(icon);

        var line = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = 2,
            Height = 47,
            Fill = Avalonia.Media.Brushes.Gold,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetTop(line, 56);
        Avalonia.Controls.Canvas.SetLeft(line, 23);
        outerCanvas.Children.Add(line);

        iconAnts = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = "TimelineCameraIconAnts",
            Width = 36,
            Height = 28,
            Stroke = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#334155")),
            StrokeThickness = 1,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(3, 2),
            StrokeDashOffset = marchingAntsOffset,
            IsVisible = isSelected,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(iconAnts, 8);
        Avalonia.Controls.Canvas.SetTop(iconAnts, 28);
        outerCanvas.Children.Add(iconAnts);

        lineAnts = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = "TimelineCameraLineAnts",
            Width = 6,
            Height = 49,
            Stroke = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#334155")),
            StrokeThickness = 1,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(3, 2),
            StrokeDashOffset = marchingAntsOffset,
            IsVisible = isSelected,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(lineAnts, 21);
        Avalonia.Controls.Canvas.SetTop(lineAnts, 55);
        outerCanvas.Children.Add(lineAnts);

        return outerCanvas;
    }

    public static double ClampTimelineCameraLeft(double markerCenterX, double canvasWidth)
    {
        const double markerWidth = 52.0;
        return Math.Max(0, Math.Min(Math.Max(0, canvasWidth - markerWidth), markerCenterX - markerWidth / 2.0));
    }

    public static void SetTimelineCameraHover(Control marker, bool isHovered)
    {
        double opacity = isHovered ? 0.38 : 0.0;
        SetTimelineCameraHoverRecursive(marker, opacity);
    }

    private static void SetTimelineCameraHoverRecursive(Control control, double opacity)
    {
        if (control.Name is "TimelineCameraIconGlow" or "TimelineCameraLineGlow")
        {
            control.Opacity = opacity;
        }

        if (control is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is Control childControl)
                {
                    SetTimelineCameraHoverRecursive(childControl, opacity);
                }
            }
        }
    }

    private void NormalizeMusicPlacement(MusicWizardResult result)
    {
        result.OffsetSeconds = Math.Max(0, result.OffsetSeconds);

        double videoStartSec = _trimStartSet ? _trimStartMs / 1000.0 : 0.0;
        double videoEndSec = _trimEndMs > _trimStartMs
            ? _trimEndMs / 1000.0
            : (_videoHost?.IpcClient?.Duration ?? 0.0);

        if (videoEndSec <= videoStartSec)
            videoEndSec = videoStartSec + Math.Max(1.0, _videoHost?.IpcClient?.Duration ?? 1.0);

        if (result.TimelineEndSeconds <= result.TimelineStartSeconds)
        {
            result.TimelineStartSeconds = videoStartSec;
            result.TimelineEndSeconds = videoEndSec;
        }

        result.TimelineStartSeconds = Math.Clamp(result.TimelineStartSeconds, 0, Math.Max(0, videoEndSec - 0.5));
        result.TimelineEndSeconds = Math.Clamp(result.TimelineEndSeconds, result.TimelineStartSeconds + 0.5, videoEndSec);
    }

    private void StartMusicPreview(double currentVideoTimeSec)
    {
        if (_musicWizardResult == null || string.IsNullOrEmpty(_musicWizardResult.MusicFilePath)) return;

        double offsetFromMusicStart = _musicWizardResult.OffsetSeconds + (currentVideoTimeSec - _musicWizardResult.TimelineStartSeconds);
        if (offsetFromMusicStart < 0) offsetFromMusicStart = 0;
        if (_musicWizardResult.MusicDurationSeconds > 0 && offsetFromMusicStart >= _musicWizardResult.MusicDurationSeconds)
            return;

        string ffplayExe = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? System.AppContext.BaseDirectory, "binaries", "ffplay.exe");
        if (!System.IO.File.Exists(ffplayExe)) ffplayExe = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? System.AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "ffplay.exe");
        if (!System.IO.File.Exists(ffplayExe)) ffplayExe = "ffplay.exe";

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffplayExe,
            Arguments = $"-nodisp -autoexit -ss {offsetFromMusicStart.ToString(System.Globalization.CultureInfo.InvariantCulture)} -volume {(_musicWizardResult.MusicVolume * 100).ToString(System.Globalization.CultureInfo.InvariantCulture)} \"{_musicWizardResult.MusicFilePath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            _musicPreviewProcess = System.Diagnostics.Process.Start(psi);
            _isMusicPreviewPlaying = true;
            _lastMusicPreviewSyncTime = currentVideoTimeSec;
        }
        catch { }
    }

    private void StopMusicPreview()
    {
        if (_musicPreviewProcess != null)
        {
            try
            {
                if (!_musicPreviewProcess.HasExited)
                    _musicPreviewProcess.Kill();
            }
            catch { }
            _musicPreviewProcess = null;
        }
        _isMusicPreviewPlaying = false;
    }

    private void EnsureTrimPointsSet()
    {
        if (!_trimStartSet && _videoHost?.IpcClient != null)
        {
            double dur = _videoHost.IpcClient.Duration;
            if (dur > 0)
            {
                _trimStartSet = true;
                _trimEndSet = true;
                _trimStartMs = 0;
                _trimEndMs = dur * 1000.0;
                var markStartBtn = this.FindControl<Button>("MarkStartButton");
                if (markStartBtn != null) markStartBtn.Content = "MARK START [00:00:00.00]";
                var markEndBtn = this.FindControl<Button>("MarkEndButton");
                if (markEndBtn != null) markEndBtn.Content = $"MARK END [{TimeSpan.FromSeconds(dur).ToString("hh\\:mm\\:ss\\.ff")}]";
                UpdateTimelineMarkers();
                SaveRecoveryState();
            }
        }
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_videoHost?.IpcClient == null) return;

        var canvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineMarkersCanvas");
        if (canvas != null && canvas.Children.Count == 0)
        {
            UpdateTimelineMarkers();
        }

        // Toggle visibility of drawn play/pause icons instead of text content
        var playIcon = this.FindControl<Avalonia.Controls.Shapes.Polygon>("PlayIcon");
        var pauseIcon = this.FindControl<StackPanel>("PauseIcon");
        if (playIcon != null && pauseIcon != null)
        {
            bool isPaused = _videoHost.IpcClient.IsPaused;
            if (_isCurrentlyFrozen) isPaused = false; // keep showing Pause bars during freeze frame!
            playIcon.IsVisible = isPaused;   // show play triangle when paused
            pauseIcon.IsVisible = !isPaused;  // show pause bars when playing
        }

        double time = _videoHost.IpcClient.CurrentTime;
        double dur = _videoHost.IpcClient.Duration;
        double displayTime = dur > 0 ? Math.Clamp(time, 0, dur) : Math.Max(0, time);

        if (_musicWizardResult != null && !string.IsNullOrEmpty(_musicWizardResult.MusicFilePath))
        {
            bool isPaused = _videoHost.IpcClient.IsPaused;
            if (_isCurrentlyFrozen) isPaused = false; // ensure music continues playing seamlessly during freeze
            double songTime = _musicWizardResult.OffsetSeconds + (time - _musicWizardResult.TimelineStartSeconds);
            bool songHasAudio = _musicWizardResult.MusicDurationSeconds <= 0 || songTime < _musicWizardResult.MusicDurationSeconds;
            bool shouldPlayMusic = !isPaused && time >= _musicWizardResult.TimelineStartSeconds && time <= _musicWizardResult.TimelineEndSeconds && songHasAudio;

            if (shouldPlayMusic && _isMusicPreviewPlaying && Math.Abs(time - _lastMusicPreviewSyncTime) > 0.5)
            {
                StopMusicPreview();
            }

            if (shouldPlayMusic && !_isMusicPreviewPlaying)
            {
                StartMusicPreview(time);
            }
            else if (!shouldPlayMusic && _isMusicPreviewPlaying)
            {
                StopMusicPreview();
            }

            if (_isMusicPreviewPlaying)
            {
                _lastMusicPreviewSyncTime = time;
            }
        }

        // ── Apply granular speed segments in real-time ──
        // When speed segments exist, dynamically adjust MPV playback speed
        // based on the current position so the preview matches the final export
        double currentAbsMs = time * 1000.0;

        // Single instance Freeze Frame playback logic
        if (_freezeTimeMs >= 0 && !_isCurrentlyFrozen && !_videoHost.IpcClient.IsPaused)
        {
            if (currentAbsMs >= _freezeTimeMs && currentAbsMs <= _freezeTimeMs + 150)
            {
                _isCurrentlyFrozen = true;
                _freezeStartTime = DateTime.UtcNow;
                _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
                return;
            }
        }
        else if (_isCurrentlyFrozen)
        {
            if ((DateTime.UtcNow - _freezeStartTime).TotalSeconds >= _freezeDurationS)
            {
                _isCurrentlyFrozen = false;
                _ = _videoHost.IpcClient.SetPropertyAsync("pause", "no");
            }
            else
            {
                return; // UI global timecode stops counting
            }
        }

        if (!_videoHost.IpcClient.IsPaused && _speedSegments.Count > 0)
        {
            double targetSpeed = GetSpeedForPosition(currentAbsMs);
            if (Math.Abs(targetSpeed - _lastAppliedSpeed) > 0.001)
            {
                _lastAppliedSpeed = targetSpeed;
                _ = _videoHost.IpcClient.SetPropertyAsync("speed",
                    targetSpeed.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        var timeElapsed = this.FindControl<TextBlock>("TimeElapsed");
        if (timeElapsed != null) timeElapsed.Text = TimeSpan.FromSeconds(displayTime).ToString("hh\\:mm\\:ss");

        var timelineSlider = this.FindControl<Slider>("TimelineSlider");
        if (timelineSlider != null && dur > 0)
        {
            if (!_isTimelineDrawn)
            {
                UpdateTimelineMarkers();
                _isTimelineDrawn = true;
            }
            _isTimerUpdatingSlider = true;
            timelineSlider.Value = Math.Clamp((time / dur) * 100.0, 0.0, 100.0);
            _isTimerUpdatingSlider = false;

            var timeRemaining = this.FindControl<TextBlock>("TimeRemaining");
            if (timeRemaining != null) timeRemaining.Text = "-" + TimeSpan.FromSeconds(Math.Max(0, dur - displayTime)).ToString("hh\\:mm\\:ss");
        }

        if (_videoHost.IpcClient.IsEof)
        {
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
        }
    }

    private async Task InitializeHardwareScanAsync()
    {
        var hwLabel = this.FindControl<TextBlock>("HardwareStatusLabel");
        if (hwLabel != null)
        {
            try
            {
                string ffmpegExe = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "backend", "ffmpeg.exe");
                if (!File.Exists(ffmpegExe)) ffmpegExe = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "ffmpeg.exe");
                if (!File.Exists(ffmpegExe)) ffmpegExe = "ffmpeg.exe";
                string mode = await HardwareScanner.ScanAsync(ffmpegExe);
                _hardwareMode = mode;

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    RuntimeLog.Info("Hardware", $"Hardware scan completed: {mode}");
                    if (mode == "CPU")
                    {
                        RuntimeLog.Fail("Hardware", "No supported hardware encoder detected; CPU fallback active.");
                        hwLabel.Text = "HW: CPU Only";
                        hwLabel.Foreground = Avalonia.Media.Brushes.Gray;
                    }
                    else
                    {
                        hwLabel.Text = $"HW: {mode} (Ready)";
                        hwLabel.Foreground = Avalonia.Media.SolidColorBrush.Parse("#00783C");
                    }
                });
            }
            catch
            {
                _hardwareMode = "CPU";
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    RuntimeLog.Fail("Hardware", "Hardware scan failed, falling back to CPU Only.");
                    hwLabel.Text = "HW: CPU Only";
                    hwLabel.Foreground = Avalonia.Media.Brushes.Gray;
                });
            }
        }
    }

    private double GetCurrentMpvTime() { return _videoHost?.IpcClient?.CurrentTime ?? 0.0; }

    /// <summary>
    /// Looks up the playback speed for a given absolute position (in ms).
    /// Returns the segment's speed if the position falls within a speed segment,
    /// otherwise returns the base speed. Freeze segments (speed ≈ 0) return 0.
    /// </summary>
    private double GetSpeedForPosition(double positionMs)
    {
        foreach (var seg in _speedSegments)
        {
            if (positionMs >= seg.StartMs && positionMs < seg.EndMs)
            {
                return seg.Speed; // 0 = freeze frame
            }
        }
        return _baseSpeed;
    }

    private void GlobalKeyDownHandler(object? sender, KeyEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox or NumericUpDown)
        {
            return; // Let textboxes handle their own typing
        }

        if (_isThumbnailMarkerSelected && _thumbnailSet && e.Key is Key.Left or Key.Right)
        {
            MoveThumbnailMarkerByPixels(e.Key == Key.Left ? -1 : 1);
            e.Handled = true;
            return;
        }

        var kb = SettingsManager.Instance.KeyBinds;

        if (e.Key == Key.Delete)
        {
            // Silent Deletion
            if (FocusManager?.GetFocusedElement() is ListBox listBox && listBox.SelectedItem is string filePath)
            {
                try { System.IO.File.Delete(filePath); } catch { }
            }
        }
        var playPause = new Avalonia.Input.KeyGesture(kb.PlayPause);
        var markStart = new Avalonia.Input.KeyGesture(kb.MarkStart);
        var markEnd = new Avalonia.Input.KeyGesture(kb.MarkEnd);
        var seekFwd = new Avalonia.Input.KeyGesture(kb.SeekForward);
        var seekBack = new Avalonia.Input.KeyGesture(kb.SeekBackward);
        var fineSeekFwdCtrl = new Avalonia.Input.KeyGesture(kb.FineSeekForward, Avalonia.Input.KeyModifiers.Control);
        var fineSeekFwdShift = new Avalonia.Input.KeyGesture(kb.FineSeekForward, Avalonia.Input.KeyModifiers.Shift);
        var fineSeekBackCtrl = new Avalonia.Input.KeyGesture(kb.FineSeekBackward, Avalonia.Input.KeyModifiers.Control);
        var fineSeekBackShift = new Avalonia.Input.KeyGesture(kb.FineSeekBackward, Avalonia.Input.KeyModifiers.Shift);
        var volUp = new Avalonia.Input.KeyGesture(kb.VolumeUp);
        var volDown = new Avalonia.Input.KeyGesture(kb.VolumeDown);
        var aggVolUpCtrl = new Avalonia.Input.KeyGesture(kb.AggressiveVolumeUp, Avalonia.Input.KeyModifiers.Control);
        var aggVolDownCtrl = new Avalonia.Input.KeyGesture(kb.AggressiveVolumeDown, Avalonia.Input.KeyModifiers.Control);

        if (playPause.Matches(e))
        {
            if (_isMusicBlockFocused)
            {
                _isMusicBlockFocused = false;
                UpdateTimelineMarkers();
            }
            if (_videoHost?.IpcClient != null)
            {
                bool isPaused = _videoHost.IpcClient.IsPaused;
                _ = _videoHost.IpcClient.SetPropertyAsync("pause", isPaused ? "no" : "yes");
                e.Handled = true;
            }
        }
        else if (_isMusicBlockFocused && _musicWizardResult != null && (e.Key == Key.Left || e.Key == Key.Right))
        {
            double duration = _videoHost?.IpcClient?.Duration ?? 0;
            if (duration > 0)
            {
                double dur = _musicWizardResult.TimelineEndSeconds - _musicWizardResult.TimelineStartSeconds;
                double offset = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 0.05 : ((_trimEndMs - _trimStartMs) / 1000.0) * 0.01;

                double newStart = _musicWizardResult.TimelineStartSeconds + (e.Key == Key.Left ? -offset : offset);
                double newEnd = newStart + dur;

                if (newStart < 0) {
                    newStart = 0;
                    newEnd = dur;
                }
                if (newEnd > duration) {
                    newEnd = duration;
                    newStart = duration - dur;
                }

                _musicWizardResult.TimelineStartSeconds = newStart;
                _musicWizardResult.TimelineEndSeconds = newEnd;
                UpdateTimelineMarkers();
                SaveRecoveryState();
                e.Handled = true;
            }
        }
        else if (fineSeekFwdCtrl.Matches(e) || fineSeekFwdShift.Matches(e))
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("frame-step");
            e.Handled = true;
        }
        else if (fineSeekBackCtrl.Matches(e) || fineSeekBackShift.Matches(e))
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("frame-back-step");
            e.Handled = true;
        }
        else if (seekFwd.Matches(e))
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("seek", 5);
            e.Handled = true;
        }
        else if (seekBack.Matches(e))
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("seek", -5);
            e.Handled = true;
        }
        else if (aggVolUpCtrl.Matches(e))
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("add", "volume", 10);
            e.Handled = true;
        }
        else if (aggVolDownCtrl.Matches(e))
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("add", "volume", -10);
            e.Handled = true;
        }
        else if (volUp.Matches(e))
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("add", "volume", 2);
            e.Handled = true;
        }
        else if (volDown.Matches(e))
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("add", "volume", -2);
            e.Handled = true;
        }
        else if (markStart.Matches(e))
        {
            var btn = this.FindControl<Button>("MarkStartButton");
            btn?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        }
        else if (markEnd.Matches(e))
        {
            var btn = this.FindControl<Button>("MarkEndButton");
            btn?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        }
    }

    private async Task ProcessVideoAsync(Button processButton)
    {
        if (_videoHost?.IpcClient != null)
        {
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
        }

        if (string.IsNullOrEmpty(_loadedVideoPath) || !File.Exists(_loadedVideoPath))
        {
            ShowTacticalFeedback("No valid video loaded to process!");
            PlayUiSound();
            processButton.IsEnabled = true;
            processButton.Content = "PROCESS";
            return;
        }

        _processCts = new System.Threading.CancellationTokenSource();

        await Task.Yield();

        try
        {
            RuntimeLog.Info("Process", "Starting video processing pipeline via ProcessWorker.");
            SetTimelinePopupsVisible(false);

            var paths = ApplicationPaths.CreateDefault();
            var worker = new ProcessWorker(paths);

            if (_videoHost != null) _videoHost.IsVisible = false;
            this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer")?.StartOverlay();

            // Hide music note popups during processing — they are OS-level WS_POPUP windows
            // that float above the PhaseOverlayControl regardless of ZIndex
            var markersCanvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineMarkersCanvas");
            if (markersCanvas != null)
            {
                foreach (var child in markersCanvas.Children)
                {
                    if (child is Avalonia.Controls.Primitives.Popup popup)
                        popup.IsOpen = false;
                }
            }

            // Wire up progress and completion
            worker.ProgressUpdate += percent =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    processButton.Content = $"PROCESSING... {percent}%";
                });
            };

            worker.PhaseUpdate += (phase, title, progress) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer")?.UpdatePhase(phase, title, progress);
                });
            };

            worker.Finished += async (success, message) =>
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer")?.StopOverlay();
                    if (_videoHost != null) _videoHost.IsVisible = true;
                    // Do not restore popups after processing completes
                    if (success)
                    {
                        RuntimeLog.Success("Process", $"Video processing completed successfully. Saved to: {message}");
                        ShowTacticalFeedback("Processing complete");
                        PlayUiSound();
                        var dlg = new FortniteVideoSoftware.App.Controls.FinishedDialogWindow();
                        dlg.SetOutputPath(message);
                        await dlg.ShowDialog(this);
                        if (dlg.DialogResult == 1)
                        {
                            Close();
                        }
                    }
                    else
                    {
                        RuntimeLog.Fail("Process", $"Video processing failed: {message}");
                        ShowTacticalFeedback("Processing failed");
                        PlayUiSound();
                    }
                    processButton.IsEnabled = true;
                    processButton.Content = "PROCESS";
                });
            };

            // Use the scanner result directly; the label is display-only.
            string hwMode = _hardwareMode;

            // Configure ProcessWorker parameters
            worker.InputPath = _loadedVideoPath;
            worker.StartTimeMs = _trimStartMs;

            // If end time is 0, use full video duration
            double duration = GetCurrentMpvTime(); // Default fallback
            duration = _videoHost?.IpcClient?.Duration ?? 0.0;

            worker.EndTimeMs = _trimEndMs > 0 ? _trimEndMs : duration * 1000;
            var allSegments = new System.Collections.Generic.List<SpeedSegment>(_speedSegments);
            if (_freezeTimeMs >= 0)
            {
                allSegments.Add(new SpeedSegment((int)_freezeTimeMs, (int)(_freezeTimeMs + _freezeDurationS * 1000.0), 0.0));
            }
            worker.SpeedSegments = allSegments;
            worker.SpeedFactor = _baseSpeed;
            worker.ThumbnailPosMs = _thumbnailSet ? _thumbnailPosMs : 0;
            if (_thumbnailSet && _thumbnailPosMs > 0)
            {
                worker.IntroAbsTimeMs = _thumbnailPosMs;
                worker.IntroStillSec = 0.1;
            }
            
            worker.HardwareStrategy = hwMode;
            worker.IsMobileFormat = this.FindControl<CheckBox>("MobileCheckbox")?.IsChecked ?? this.FindControl<CheckBox>("PortraitModeCheckbox")?.IsChecked ?? true;
            worker.IsBossHp = this.FindControl<CheckBox>("BossHpCheckbox")?.IsChecked ?? false;
            worker.ShowTeammates = this.FindControl<CheckBox>("TeammatesCheckbox")?.IsChecked ?? false;

            worker.PortraitText = this.FindControl<TextBox>("PortraitTextInput")?.Text;

            // Convert the OUTPUT FILE SIZE slider index (0-20) to the actual target MB.
            // Slider index 0-19 maps to 5MB..100MB via (5 + idx*5); index 20 = "ORIGINAL
            // QUALITY" which uses CQ mode (no MB target). Previously the raw index was passed
            // directly as TargetMbOverride AND QualityLevel was never set, causing FFmpeg to
            // target tiny file sizes (e.g. idx 7 → 7 MB instead of the displayed 40 MB) and
            // producing extremely low-quality output regardless of slider position.
            int qualityIdx = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("QualitySlider")?.Value ?? 7;
            worker.QualityLevel = qualityIdx;
            worker.TargetMbOverride = qualityIdx >= 20 ? null : (double)(5 + qualityIdx * 5);

            // Pass Music Wizard config
            if (_musicWizardResult != null && !string.IsNullOrEmpty(_musicWizardResult.MusicFilePath) && File.Exists(_musicWizardResult.MusicFilePath))
            {
                double startDelay = _musicWizardResult.TimelineStartSeconds - (worker.StartTimeMs / 1000.0);
                if (startDelay < 0) startDelay = 0;
                double dur = _musicWizardResult.TimelineEndSeconds - _musicWizardResult.TimelineStartSeconds;
                if (dur <= 0) dur = 1.0;

                // Determine fade-out: only apply when the music end note aligns with MARK END.
                // If the user dragged the right ♫ note before MARK END, the music cuts instantly.
                double videoEndSec = worker.EndTimeMs / 1000.0;
                bool applyFadeOut = Math.Abs(_musicWizardResult.TimelineEndSeconds - videoEndSec) < 0.05;

                worker.MusicTracks = new System.Collections.Generic.List<MusicTrack>
                {
                    new MusicTrack(_musicWizardResult.MusicFilePath, _musicWizardResult.OffsetSeconds, dur, startDelay, applyFadeOut)
                };

                worker.MusicConfig = new System.Text.Json.Nodes.JsonObject
                {
                    ["ducking_threshold"] = _musicWizardResult.EnableDucking ? 0.15 : 1.0,
                    ["ducking_ratio"] = _musicWizardResult.EnableDucking ? 2.5 : 1.0,
                    ["main_vol"] = _musicWizardResult.VideoVolume,
                    ["music_vol"] = _musicWizardResult.MusicVolume,
                    ["carving_enabled"] = _musicWizardResult.EnableCarving
                };
            }

            // Start processing asynchronously but we don't block the UI thread
            _ = worker.RunAsync(_processCts.Token);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Process", ex);
            ShowTacticalFeedback("Error during process launch.");
            PlayUiSound();
            processButton.IsEnabled = true;
            processButton.Content = "PROCESS";
        }
    }

    private async void InitializeMpv()
    {
        _videoHost = this.FindControl<MpvVideoView>("VideoHost");
        if (_videoHost != null)
        {
            string mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "frontend", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath)) mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath))
            {
                mpvPath = "mpv.exe"; // Fallback to PATH
            }
            await _videoHost.StartMpvProcessAsync(mpvPath);
            if (_videoHost.IpcClient != null)
            {
                _videoHost.IpcClient.SeekCompleted += () => {
                    Avalonia.Threading.Dispatcher.UIThread.Post(async () => {
                        _isSeeking = false;
                        if (_nextSeekTarget.HasValue) {
                            double target = _nextSeekTarget.Value;
                            _nextSeekTarget = null;
                            await SeekInternal(target);
                        }
                    });
                };
            }
        }
    }

    private async Task SeekInternal(double time) {
        if (_isSeeking) {
            _nextSeekTarget = time;
            return;
        }
        _isSeeking = true;
        if (_videoHost?.IpcClient != null) {
            await _videoHost.IpcClient.SendCommandAsync("seek", time, "absolute");
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void OnSuccessAction(string action)
    {
        if (action == "whatsapp")
        {
            Process.Start(new ProcessStartInfo("cmd", "/c start whatsapp://send?text=CheckOutThisVideo") { CreateNoWindow = true });
        }
        else if (action == "folder")
        {
            Process.Start(new ProcessStartInfo("explorer.exe", ".") { CreateNoWindow = true });
        }

        // Success Auto-Exit: When the export finishes, clicking "SHARE VIA WHATSAPP" or "OPEN FOLDER"
        // MUST execute the OS command and then immediately invoke Environment.Exit(0).
        Environment.Exit(0);
    }

    private bool _isSafeToClose = false;
    private double _lastAppliedSpeed = SpeedPresetButtons.NativeDefaultSpeed;  // Tracks last speed sent to MPV to avoid redundant IPC calls

    protected override async void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        StopMusicPreview();
        // If the background work is done, allow the window to close normally
        if (_isSafeToClose)
        {
            base.OnClosing(e);
            return;
        }

        // STOP the synchronous UI-blocking close
        e.Cancel = true;

        // Hide the window instantly so the app feels incredibly fast and responsive
        this.Hide();

        RuntimeLog.Info("UI", "Closing MainWindow. Saving state and cleaning up asynchronously.");

        try
        {
            // Perform the heavy Mutex locking and file I/O ASYNCHRONOUSLY
            await WindowBoundsHelper.SaveBoundsAsync(this, "MainWindowBounds");

            // Clean up crash-recovery lock so the next launch is treated as fresh
            _recovery.CleanupLock();

            if (_videoHost?.IpcClient != null)
            {
                await _videoHost.IpcClient.SendCommandAsync("stop");
                _videoHost.IpcClient.Dispose();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UI", $"Error saving state during close: {ex.Message}");
        }
        finally
        {
            // Mark as safe and forcefully end the process tree
            _isSafeToClose = true;
            Environment.Exit(0);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Environment.Exit(0);
    }

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<Border>("TitleBarBorder");
        if (titleBar != null)
        {
            titleBar.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    try { BeginMoveDrag(e); } catch { }
                }
            };
        }
    }

    // ============================================================
    //  GRANULAR SPEED & MUSIC — Button State Toggles
    // ============================================================

    /// <summary>
    /// Toggles the GRANULAR SPEED button between its normal (blue "GRANULAR SPEED")
    /// and active (red "REMOVE SPEEDS") states. Saves recovery state on change.
    /// </summary>
    private void SetGranularButtonActive(bool active)
    {
        var btn = this.FindControl<Button>("GranularButton");
        if (btn == null) return;
        _isGranularSpeedActive = active;
        if (active)
        {
            btn.Classes.Remove("Primary");
            btn.Classes.Add("Danger");
            btn.Content = "REMOVE SPEEDS";
            ToolTip.SetTip(btn, "Click here to delete all the speed changes you made. This wipes out every speed segment in one go.");
        }
        else
        {
            btn.Classes.Remove("Danger");
            btn.Classes.Add("Primary");
            btn.Content = "GRANULAR SPEED";
            ToolTip.SetTip(btn, "Adjust granular speed settings");
        }
        SaveRecoveryState();
    }

    /// <summary>
    /// Toggles the ADD MUSIC button between its normal (blue "ADD MUSIC")
    /// and active (red "REMOVE MUSIC") states. Saves recovery state on change.
    /// </summary>
    private void SetMusicButtonActive(bool active)
    {
        var btn = this.FindControl<Button>("AddMusicButton");
        if (btn == null) return;
        _isMusicActive = active;
        if (active)
        {
            btn.Classes.Remove("Primary");
            btn.Classes.Add("Danger");
            btn.Content = "REMOVE MUSIC";
            ToolTip.SetTip(btn, "Click here to remove the background music you added. This deletes the music track from your video.");
        }
        else
        {
            btn.Classes.Remove("Danger");
            btn.Classes.Add("Primary");
            btn.Content = "ADD MUSIC";
            ToolTip.SetTip(btn, "Add background music to the video");
        }
        SaveRecoveryState();
    }

    // ============================================================
    //  CRASH RECOVERY — Save / Restore editing session state
    // ============================================================

    /// <summary>
    /// Serializes all editing-session state to the crash-recovery file
    /// (recovery_v2.json) so it can be restored if the app crashes.
    /// Called on every meaningful state change (trim, speed, quality, etc.).
    /// </summary>
    private void SaveRecoveryState()
    {
        if (_isRestoring) return;
        try
        {
            var qs = this.FindControl<SpinningWheelSlider>("QualitySlider");
            var state = new System.Text.Json.Nodes.JsonObject
            {
                ["loadedVideoPath"] = _loadedVideoPath,
                ["trimStartMs"] = _trimStartMs,
                ["trimStartSet"] = _trimStartSet,
                ["trimEndMs"] = _trimEndMs,
                ["trimEndSet"] = _trimEndSet,
                ["thumbnailPosMs"] = _thumbnailPosMs,
                ["thumbnailSet"] = _thumbnailSet,
                ["baseSpeed"] = _baseSpeed,
                ["qualitySliderValue"] = qs?.Value ?? 7,
                ["isGranularSpeedActive"] = _isGranularSpeedActive,
                ["isMusicActive"] = _isMusicActive,
                // Additional UI state for complete recovery
                ["volume"] = this.FindControl<Slider>("VolumeSlider")?.Value ?? 100,
                ["portraitMode"] = this.FindControl<CheckBox>("PortraitModeCheckbox")?.IsChecked ?? true,
                ["bossHp"] = this.FindControl<CheckBox>("BossHpCheckbox")?.IsChecked ?? false,
                ["showTeammates"] = this.FindControl<CheckBox>("TeammatesCheckbox")?.IsChecked ?? false,
                ["noFade"] = this.FindControl<CheckBox>("NoFadeCheckbox")?.IsChecked ?? false,
                ["portraitText"] = this.FindControl<TextBox>("PortraitTextInput")?.Text ?? "",
                ["freezeTimeMs"] = _freezeTimeMs,
                ["freezeDurationS"] = _freezeDurationS
            };

            RuntimeLog.Info("RECOVERY", "Preparing crash recovery state dump...");
            if (_freezeTimeMs >= 0)
                RuntimeLog.Info("RECOVERY", $"Crash Recovery Prep: Serialized Freeze parameters [Timestamp={_freezeTimeMs}ms, Duration={_freezeDurationS}s]");

            // Serialize speed segments
            var segArray = new System.Text.Json.Nodes.JsonArray();
            foreach (var seg in _speedSegments)
            {
                segArray.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["startMs"] = seg.StartMs,
                    ["endMs"] = seg.EndMs,
                    ["speed"] = seg.Speed
                });
            }
            state["speedSegments"] = segArray;

            // Serialize music result if present
            if (_musicWizardResult != null)
            {
                state["musicResult"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["musicFilePath"] = _musicWizardResult.MusicFilePath,
                    ["offsetSeconds"] = _musicWizardResult.OffsetSeconds,
                    ["timelineStartSeconds"] = _musicWizardResult.TimelineStartSeconds,
                    ["timelineEndSeconds"] = _musicWizardResult.TimelineEndSeconds,
                    ["musicDurationSeconds"] = _musicWizardResult.MusicDurationSeconds,
                    ["enableDucking"] = _musicWizardResult.EnableDucking,
                    ["enableCarving"] = _musicWizardResult.EnableCarving,
                    ["videoVolume"] = _musicWizardResult.VideoVolume,
                    ["musicVolume"] = _musicWizardResult.MusicVolume
                };
            }

            _recovery.SaveStateAsync(state);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("RECOVERY", $"Failed to save session state: {ex.Message}");
        }
    }

    /// <summary>
    /// Restores all editing-session state from the crash-recovery file.
    /// Called at startup when a previous crash is detected.
    /// </summary>
    private async Task RestoreRecoveryStateAsync()
    {
        _isRestoring = true;
        try
        {
            var state = _recovery.LoadState();
            if (state == null)
            {
                RuntimeLog.Info("RECOVERY", "No recovery state file found. Nothing to restore.");
                return;
            }

            RuntimeLog.Info("RECOVERY", "Beginning state restoration...");

            // Restore trim points
            _trimStartMs = state["trimStartMs"]?.GetValue<double>() ?? 0;
            _trimStartSet = state["trimStartSet"]?.GetValue<bool>() ?? false;
            _trimEndMs = state["trimEndMs"]?.GetValue<double>() ?? 0;
            _trimEndSet = state["trimEndSet"]?.GetValue<bool>() ?? false;
            _thumbnailPosMs = state["thumbnailPosMs"]?.GetValue<double>() ?? 0;
            _thumbnailSet = state["thumbnailSet"]?.GetValue<bool>() ?? _thumbnailPosMs > 0;
            
            var thumbBtnRest = this.FindControl<Button>("SetThumbnailButton");
            var thumbTxtRest = this.FindControl<TextBlock>("SetThumbnailText");
            if (thumbBtnRest != null && _thumbnailSet)
            {
                thumbBtnRest.Classes.Remove("Primary");
                thumbBtnRest.Classes.Add("Danger");
                if (thumbTxtRest != null) thumbTxtRest.Text = "REMOVE THUMBNAIL";
            }

            // Restore MARK START/END button labels
            var markStartBtn = this.FindControl<Button>("MarkStartButton");
            if (markStartBtn != null && _trimStartSet)
                markStartBtn.Content = $"START: {TimeSpan.FromMilliseconds(_trimStartMs):hh\\:mm\\:ss}";
            var markEndBtn = this.FindControl<Button>("MarkEndButton");
            if (markEndBtn != null && _trimEndMs > 0)
                markEndBtn.Content = $"END: {TimeSpan.FromMilliseconds(_trimEndMs):hh\\:mm\\:ss}";

            // Restore base speed
            _baseSpeed = state["baseSpeed"]?.GetValue<double>() ?? SpeedPresetButtons.NativeDefaultSpeed;
            var speedSlider = this.FindControl<SpinningWheelSlider>("MainSpeedSlider");
            if (speedSlider != null) speedSlider.Value = (int)Math.Round(_baseSpeed * 10.0, MidpointRounding.AwayFromZero);

            // Restore quality slider (file size)
            int qualityVal = state["qualitySliderValue"]?.GetValue<int>() ?? 7;
            var qualitySliderRestore = this.FindControl<SpinningWheelSlider>("QualitySlider");
            if (qualitySliderRestore != null) qualitySliderRestore.Value = qualityVal;

            // Restore freeze state
            _freezeTimeMs = state["freezeTimeMs"]?.GetValue<double>() ?? -1;
            _freezeDurationS = state["freezeDurationS"]?.GetValue<double>() ?? 1.0;
            if (_freezeTimeMs >= 0)
                RuntimeLog.Info("RECOVERY", $"Crash Recovery Restore: Successfully reinstated Freeze parameters [Timestamp={_freezeTimeMs}ms, Duration={_freezeDurationS}s]");

            // Restore speed segments
            _speedSegments.Clear();
            if (state["speedSegments"] is System.Text.Json.Nodes.JsonArray segArray)
            {
                foreach (var segNode in segArray)
                {
                    var segObj = segNode?.AsObject();
                    if (segObj != null)
                    {
                        _speedSegments.Add(new SpeedSegment(
                            segObj["startMs"]?.GetValue<double>() ?? 0,
                            segObj["endMs"]?.GetValue<double>() ?? 0,
                            segObj["speed"]?.GetValue<double>() ?? 1.0));
                    }
                }
            }

            // Restore granular speed button state (set flag directly to avoid save loop)
            _isGranularSpeedActive = state["isGranularSpeedActive"]?.GetValue<bool>() ?? false;
            var granularBtnRestore = this.FindControl<Button>("GranularButton");
            if (granularBtnRestore != null && _isGranularSpeedActive)
            {
                granularBtnRestore.Classes.Remove("Primary");
                granularBtnRestore.Classes.Add("Danger");
                granularBtnRestore.Content = "REMOVE SPEEDS";
                ToolTip.SetTip(granularBtnRestore, "Click here to delete all the speed changes you made. This wipes out every speed segment in one go.");
            }

            // Restore music result
            _musicWizardResult = null;
            if (state["musicResult"] is System.Text.Json.Nodes.JsonObject musicObj)
            {
                _musicWizardResult = new MusicWizardResult
                {
                    MusicFilePath = musicObj["musicFilePath"]?.ToString() ?? "",
                    OffsetSeconds = musicObj["offsetSeconds"]?.GetValue<double>() ?? 0.0,
                    TimelineStartSeconds = musicObj["timelineStartSeconds"]?.GetValue<double>() ?? 0.0,
                    TimelineEndSeconds = musicObj["timelineEndSeconds"]?.GetValue<double>() ?? 0.0,
                    MusicDurationSeconds = musicObj["musicDurationSeconds"]?.GetValue<double>() ?? 0.0,
                    EnableDucking = musicObj["enableDucking"]?.GetValue<bool>() ?? true,
                    EnableCarving = musicObj["enableCarving"]?.GetValue<bool>() ?? true,
                    VideoVolume = musicObj["videoVolume"]?.GetValue<double>() ?? 1.0,
                    MusicVolume = musicObj["musicVolume"]?.GetValue<double>() ?? 1.0
                };
                NormalizeMusicPlacement(_musicWizardResult);
            }

            // Restore music button state (set flag directly to avoid save loop)
            _isMusicActive = state["isMusicActive"]?.GetValue<bool>() ?? false;
            var musicBtnRestore = this.FindControl<Button>("AddMusicButton");
            if (musicBtnRestore != null && _isMusicActive)
            {
                musicBtnRestore.Classes.Remove("Primary");
                musicBtnRestore.Classes.Add("Danger");
                musicBtnRestore.Content = "REMOVE MUSIC";
                ToolTip.SetTip(musicBtnRestore, "Click here to remove the background music you added. This deletes the music track from your video.");
            }

            // Restore additional UI state: volume, checkboxes, portrait text
            double vol = state["volume"]?.GetValue<double>() ?? 100;
            var volSliderRestore = this.FindControl<Slider>("VolumeSlider");
            if (volSliderRestore != null) volSliderRestore.Value = vol;

            bool portraitMode = state["portraitMode"]?.GetValue<bool>() ?? true;
            var portraitCbRestore = this.FindControl<CheckBox>("PortraitModeCheckbox");
            if (portraitCbRestore != null) portraitCbRestore.IsChecked = portraitMode;

            bool bossHp = state["bossHp"]?.GetValue<bool>() ?? false;
            var bossHpCbRestore = this.FindControl<CheckBox>("BossHpCheckbox");
            if (bossHpCbRestore != null) bossHpCbRestore.IsChecked = bossHp;

            bool showTeammates = state["showTeammates"]?.GetValue<bool>() ?? false;
            var teammatesCbRestore = this.FindControl<CheckBox>("TeammatesCheckbox");
            if (teammatesCbRestore != null) teammatesCbRestore.IsChecked = showTeammates;

            bool noFade = state["noFade"]?.GetValue<bool>() ?? false;
            var noFadeCbRestore = this.FindControl<CheckBox>("NoFadeCheckbox");
            if (noFadeCbRestore != null) noFadeCbRestore.IsChecked = noFade;

            string portraitText = state["portraitText"]?.ToString() ?? "";
            var portraitTextRestore = this.FindControl<TextBox>("PortraitTextInput");
            if (portraitTextRestore != null) portraitTextRestore.Text = portraitText;

            // Restore loaded video — wait for MPV to be ready first
            string? videoPath = state["loadedVideoPath"]?.ToString();
            if (!string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath))
            {
                _loadedVideoPath = videoPath;
                _isTimelineDrawn = false;

                // Wait for MPV IPC client to be ready (InitializeMpv runs in parallel)
                for (int i = 0; i < 50; i++)
                {
                    if (_videoHost?.IpcClient != null) break;
                    await Task.Delay(100);
                }

                if (_videoHost?.IpcClient != null)
                {
                    _ = _videoHost.IpcClient.LoadFileAsync(videoPath);
                    _ = _videoHost.IpcClient.SetPropertyAsync("speed",
                        _baseSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                }
            if (_videoHost != null) _videoHost.IsVisible = true;
            UpdatePortraitOverlay();

            var uploadOverlay = this.FindControl<Border>("UploadOverlay");
            if (uploadOverlay != null) uploadOverlay.IsVisible = false;

            // Re-apply portrait dimming filter after loading a new video.
            // MPV resets all video filters when a new file is loaded.
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500); // Wait for MPV to finish loading
                Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdatePortraitOverlay());
            });

                // Enable all editing controls
                EnableEditingControls();

                // Refresh derived UI
                UpdateEstimatedQuality();
                UpdateSpeedLabel();
                UpdateTimelineMarkers();

                RuntimeLog.Success("RECOVERY", $"Session restored. Video={videoPath}, Trim={_trimStartMs}ms-{_trimEndMs}ms, Segments={_speedSegments.Count}, GranularActive={_isGranularSpeedActive}, MusicActive={_isMusicActive}");
                ShowTacticalFeedback("Previous session restored after crash");
            }
            else
            {
                RuntimeLog.Info("RECOVERY", "Video file no longer exists. Skipping video restore; other settings were restored.");
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("RECOVERY", $"Failed to restore session state: {ex.Message}");
        }
        finally
        {
            _isRestoring = false;
            SaveRecoveryState();
        }
    }
}

