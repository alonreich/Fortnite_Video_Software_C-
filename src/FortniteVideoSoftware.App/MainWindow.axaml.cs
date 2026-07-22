using Avalonia.Platform.Storage;
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
using System.Collections.Generic;
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

    private PreviewMonitorWindow? _detachedPreviewWindow = null;
    public bool IsPreviewDetached => _detachedPreviewWindow != null;

    public MpvVideoView? ActiveVideoHost
    {
        get
        {
            return _videoHost;
        }
    }
    private bool _isSeeking = false;
    private double? _nextSeekTarget = null;

    private double _trimStartMs = 0;
    private double _trimEndMs = 0;
    private bool _trimEndSet = false;

    private VoiceOverWindow.VoiceOverResult? _voiceOverResult;
    private NAudio.Wave.WaveOutEvent? _voiceOverPlayer;
    private NAudio.Wave.AudioFileReader? _voiceOverReader;
    private readonly List<VoiceOverPreviewTake> _voiceOverPreviewTakes = new();
    private Func<double, double>? _voiceOverPreviewTimeMapper;

    private sealed class VoiceOverPreviewTake
    {
        public required VoiceOverTake Take { get; init; }
        public required NAudio.Wave.WaveOutEvent Player { get; init; }
        public required NAudio.Wave.AudioFileReader Reader { get; init; }
        public double StartProjectSec { get; set; }
    }

    private double _thumbnailPosMs = 0;
    private bool _thumbnailSet = false;

    private double _freezeTimeMs = -1;
    private double _freezeDurationS = 1.0;
    private bool _isCurrentlyFrozen = false;
    private DateTime _freezeStartTime;
    private double _previousVolume = 100;

    private bool _trimStartSet = false;

    private string FormatTime(TimeSpan time, bool includeMilliseconds = false)
    {
        double dur = ActiveVideoHost?.IpcClient?.Duration ?? 0;
        bool showHours = dur >= 3600 || time.TotalHours >= 1;

        if (showHours)
            return includeMilliseconds ? time.ToString("hh\\:mm\\:ss\\.ff") : time.ToString("hh\\:mm\\:ss");
        else
            return includeMilliseconds ? time.ToString("mm\\:ss\\.ff") : time.ToString("mm\\:ss");
    }

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

    private void UpdateDraggingVisuals(double canvasWidth, double duration)
    {
        if (duration <= 0) return;
        if (_regionRectRef != null)
        {
            double regStartX = (_trimStartMs / 1000.0 / duration) * canvasWidth;
            double regEndX = _trimEndMs > 0 ? (_trimEndMs / 1000.0 / duration) * canvasWidth : canvasWidth;
            Avalonia.Controls.Canvas.SetLeft(_regionRectRef, regStartX);
            _regionRectRef.Width = Math.Max(0, regEndX - regStartX);
        }
        if (_musicWizardResult != null && !string.IsNullOrEmpty(_musicWizardResult.MusicFilePath))
        {
            double mStartX = (_musicWizardResult.TimelineStartSeconds / duration) * canvasWidth;
            double mEndX = (_musicWizardResult.TimelineEndSeconds / duration) * canvasWidth;
            if (_musicStartPopupRef != null) Avalonia.Controls.Canvas.SetLeft(_musicStartPopupRef, mStartX - 26);
            if (_musicEndPopupRef != null) Avalonia.Controls.Canvas.SetLeft(_musicEndPopupRef, mEndX - 26);
            if (_musicBlockRectRef != null)
            {
                Avalonia.Controls.Canvas.SetLeft(_musicBlockRectRef, mStartX);
                _musicBlockRectRef.Width = Math.Max(2, mEndX - mStartX);
            }
        }
    }
    private bool _draggingStartMarker = false;
    private bool _draggingEndMarker = false;
    private bool _qualitySliderInitialized = false;
    private bool _draggingMusicStart = false;
    private bool _draggingMusicEnd = false;
    private bool _draggingMusicBlock = false;
    private double _playingMusicTimelineStartSeconds = -1;
    private MusicWizardResult? _musicWizardResult;
    private FortniteVideoSoftware.Core.Media.MpvIpcClient? _musicPreviewIpcClient;
    private bool _isMusicPreviewPlaying = false;
    private double _lastMusicPreviewSyncTime = -1;
    private DispatcherTimer? _playbackTimer;
    private bool _isTimerUpdatingSlider = false;
    private readonly ApplicationPaths _paths = ApplicationPaths.CreateDefault();

    private bool _isMusicBlockFocused = false;
    private Avalonia.Controls.Shapes.Rectangle? _musicBlockRectRef;
    private Avalonia.Controls.Control? _musicStartPopupRef;
    private Avalonia.Controls.Control? _musicEndPopupRef;
    private Avalonia.Controls.Shapes.Rectangle? _regionRectRef;
    private DispatcherTimer? _marchingAntsTimer;
    private double _marchingAntsOffset = 0;
    private bool _isThumbnailMarkerSelected = false;
    private bool _isDraggingThumbnailMarker = false;
    private Avalonia.Controls.Shapes.Rectangle? _thumbnailMarkerIconAntsRef;
    private Avalonia.Controls.Shapes.Rectangle? _thumbnailMarkerLineAntsRef;
    private bool? _keepMusicDuringMeme;

    private readonly System.Collections.Generic.List<SpeedSegment> _speedSegments = new();
    private double _baseSpeed = SpeedPresetButtons.NativeDefaultSpeed;
    private bool _isTimelineDrawn = false;
    private string _loadedVideoPath = string.Empty;
    private string _hardwareMode = "CPU";
    private System.Threading.CancellationTokenSource? _processCts;

    private readonly RecoveryManager _recovery = new RecoveryManager();
    private bool _isGranularSpeedActive = false;
    private bool _isMusicActive = false;
    private bool _isRestoring = false;

    public static readonly StyledProperty<string> OverlayTextProperty =
        AvaloniaProperty.Register<MainWindow, string>(nameof(OverlayText), "");

    public string OverlayText
    {
        get => GetValue(OverlayTextProperty);
        set => SetValue(OverlayTextProperty, value);
    }

    public static readonly StyledProperty<bool> IsPortraitModeProperty =
        AvaloniaProperty.Register<MainWindow, bool>(nameof(IsPortraitMode), true);
    public bool IsPortraitMode
    {
        get => GetValue(IsPortraitModeProperty);
        set => SetValue(IsPortraitModeProperty, value);
    }

    public static readonly StyledProperty<bool> IsBossHpProperty =
        AvaloniaProperty.Register<MainWindow, bool>(nameof(IsBossHp), false);
    public bool IsBossHp
    {
        get => GetValue(IsBossHpProperty);
        set => SetValue(IsBossHpProperty, value);
    }

    public static readonly StyledProperty<bool> IsTeammatesProperty =
        AvaloniaProperty.Register<MainWindow, bool>(nameof(IsTeammates), false);
    public bool IsTeammates
    {
        get => GetValue(IsTeammatesProperty);
        set => SetValue(IsTeammatesProperty, value);
    }

    public static readonly StyledProperty<bool> IsSpectatingProperty =
        AvaloniaProperty.Register<MainWindow, bool>(nameof(IsSpectating), false);
    public bool IsSpectating
    {
        get => GetValue(IsSpectatingProperty);
        set => SetValue(IsSpectatingProperty, value);
    }

    public static readonly StyledProperty<bool> IsEnableFadeProperty =
        AvaloniaProperty.Register<MainWindow, bool>(nameof(IsEnableFade), false);
    public bool IsEnableFade
    {
        get => GetValue(IsEnableFadeProperty);
        set => SetValue(IsEnableFadeProperty, value);
    }

    public static readonly StyledProperty<bool> IsAddMemeProperty =
        AvaloniaProperty.Register<MainWindow, bool>(nameof(IsAddMeme), false);
    public bool IsAddMeme
    {
        get => GetValue(IsAddMemeProperty);
        set => SetValue(IsAddMemeProperty, value);
    }

    public MainWindow()
    {
        RuntimeLog.Info("UI", "Initializing MainWindow");
        InitializeComponent();

        this.AddHandler(DragDrop.DragEnterEvent, OnVideoDragEnter);
        this.AddHandler(DragDrop.DragOverEvent, OnVideoDragOver);
        this.AddHandler(DragDrop.DragLeaveEvent, OnVideoDragLeave);
        this.AddHandler(DragDrop.DropEvent, OnVideoDrop);

        Win32FileDropInterop.Attach(this, paths => _ = HandleExternalFileDropAsync(paths));

        var overlay = this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer");
        if (overlay != null)
        {
            overlay.CancelRequested += (s, e) =>
            {
                if (_processCts != null && !_processCts.IsCancellationRequested)
                {
                    _processCts.Cancel();
                    overlay.StopOverlay();
                    if (ActiveVideoHost != null) ActiveVideoHost.IsVisible = true;
                    var btn = this.FindControl<Button>("ProcessButton");
                    if (btn != null)
                    {
                        btn.IsEnabled = true;
                        btn.Content = "PROCESS";
                    }
                    ShowTacticalFeedback("Processing Cancelled");
                    PlayUiSound();
                }
            };
        }

        this.Loaded += (s, e) => InitializeMpv();

        this.AddHandler(InputElement.KeyDownEvent, (s, e) =>
        {
            if (e.Key == Key.Space)
            {
                var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
                if (focused is TextBox) return;

                e.Handled = true;
                var playPauseButton = this.FindControl<Button>("PlayPauseButton");
                if (playPauseButton != null && playPauseButton.IsEnabled)
                {
                    if (ActiveVideoHost?.IpcClient != null) 
                    {
                        if (_isCurrentlyFrozen)
                        {
                            _isCurrentlyFrozen = false;
                            _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
                            return;
                        }
                        _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", ActiveVideoHost.IpcClient.IsPaused ? "no" : "yes");
                    }
                }
            }
        }, RoutingStrategies.Tunnel);

        SettingsManager.Load();
        ThemeManager.ApplyFromSettings();
        FortniteVideoSoftware.App.Infrastructure.MaskOverlayManager.ApplyProfile(FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.ActiveMaskOverlay);

        var settingsBtn = this.FindControl<MenuItem>("MenuSettingsBtn");
        if (settingsBtn != null)
        {
            settingsBtn.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked Settings menu button.");
                var settingsWin = new FortniteVideoSoftware.App.Controls.SettingsWindow();
                bool changed = await settingsWin.ShowDialog<bool>(this);
                if (changed) UpdateTooltips();
            };
        }

        var menuUploadVideo = this.FindControl<MenuItem>("MenuUploadVideo");
        if (menuUploadVideo != null) menuUploadVideo.Click += OnUploadVideoClicked;

        var menuTogglePreview = this.FindControl<MenuItem>("MenuTogglePreviewMonitor");
        if (menuTogglePreview != null) menuTogglePreview.Click += async (s, e) => 
        {
            if (_detachedPreviewWindow == null)
            {
                await DetachPreviewMonitor();
                menuTogglePreview.Header = "Attach Preview Monitor";
            }
            else
            {
                await AttachPreviewMonitor();
                menuTogglePreview.Header = "Detach Preview Monitor";
            }
        };

        var detachOverlayBtn = this.FindControl<Button>("DetachOverlayButton");
        if (detachOverlayBtn != null) detachOverlayBtn.Click += async (s, e) => 
        {
            if (_detachedPreviewWindow == null)
            {
                await DetachPreviewMonitor();
                menuTogglePreview!.Header = "Attach Preview Monitor";
                detachOverlayBtn.Content = "◱ Attach Monitor";
            }
            else
            {
                await AttachPreviewMonitor();
                menuTogglePreview!.Header = "Detach Preview Monitor";
                detachOverlayBtn.Content = "◳ Detach Monitor";
            }
        };

        var menuExportConfig = this.FindControl<MenuItem>("MenuExportConfig");
        if (menuExportConfig != null) menuExportConfig.Click += OnExportConfigClicked;

        var menuImportConfig = this.FindControl<MenuItem>("MenuImportConfig");
        if (menuImportConfig != null) menuImportConfig.Click += OnImportConfigClicked;

        var menuExit = this.FindControl<MenuItem>("MenuExit");
        if (menuExit != null) menuExit.Click += (s, e) => Close();

        var menuCropSettings = this.FindControl<MenuItem>("MenuCropSettings");
        if (menuCropSettings != null) menuCropSettings.Click += (s, e) =>
        {
            SaveRecoveryState(sync: true);
            _recovery.ReleaseLockOnly();
            RuntimeLog.Info("UI", "Opening Crop Tools app and closing Main app.");
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "FortniteVideoSoftware.exe";
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath, "--crop-tool") { UseShellExecute = false });
            if (p != null) Task.Run(async () => { try { p.WaitForInputIdle(5000); await Task.Delay(500); } catch { } Environment.Exit(0); });
        };

        var menuVideoMerger = this.FindControl<MenuItem>("MenuVideoMerger");
        if (menuVideoMerger != null) menuVideoMerger.Click += (s, e) =>
        {
            SaveRecoveryState(sync: true);
            _recovery.ReleaseLockOnly();
            RuntimeLog.Info("UI", "Opening Video Merger app and closing Main app.");
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "FortniteVideoSoftware.exe";
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath, "--merger") { UseShellExecute = false });
            if (p != null) Task.Run(async () => { try { p.WaitForInputIdle(5000); await Task.Delay(500); } catch { } Environment.Exit(0); });
        };

        UpdateTooltips();

        FortniteVideoSoftware.App.WindowBoundsHelper.Track(this, "MainWindowBounds");

        FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged += OnGlobalMasterVolumeChanged;

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();

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
            };
        }


        var granularButton = this.FindControl<Button>("GranularButton");
        if (granularButton != null)
        {
            granularButton.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked GRANULAR SPEED button.");

                if (_isGranularSpeedActive)
                {
                    _speedSegments.Clear();
                    _freezeTimeMs = -1;
                    SetGranularButtonActive(false);
                    _lastAppliedSpeed = _baseSpeed;
                    if (ActiveVideoHost?.IpcClient != null)
                        _ = ActiveVideoHost.IpcClient.SetPropertyAsync("speed",
                            _baseSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                    InvalidateVoiceOverRecordingForTimingChange();
                    UpdateEstimatedQuality();
                    ShowTacticalFeedback("Speed segments removed");
                    UpdateTimelineMarkers();
                    RuntimeLog.Info("UI", "User removed all granular speed segments via REMOVE SPEEDS button.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(_loadedVideoPath))
                {
                    ShowTacticalFeedback("Load a video first!");
                    PlayUiSound();
                    return;
                }

                if (ActiveVideoHost?.IpcClient != null)
                    _ = ActiveVideoHost?.IpcClient?.SetPropertyAsync("pause", "yes");

                EnsureTrimPointsSet();

                SetTimelinePopupsVisible(false);

                bool isMobileForZoom = this.FindControl<ToggleSwitch>("PortraitModeCheckbox")?.IsChecked == true;
                var zoomIpc = ActiveVideoHost?.IpcClient;
                string zoomSrcRes = (zoomIpc != null && zoomIpc.VideoWidth > 0 && zoomIpc.VideoHeight > 0)
                    ? $"{zoomIpc.VideoWidth}x{zoomIpc.VideoHeight}"
                    : "1920x1080";
                var editor = new GranularSpeedEditorWindow(
                    _loadedVideoPath,
                    _trimStartMs,
                    _trimEndMs > 0 ? _trimEndMs : (zoomIpc?.Duration ?? 0) * 1000,
                    _speedSegments,
                    _baseSpeed,
                    _freezeTimeMs,
                    _freezeDurationS,
                    isMobileForZoom,
                    zoomSrcRes);

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
                    RuntimeLog.Info("UI", $"Granular editor closed. {count} segment(s) saved. Base speed={_baseSpeed:F2}x. FreezeTimeMs={_freezeTimeMs}");

                    bool hasGranularEdits = count > 0 || _freezeTimeMs >= 0;

                    SetGranularButtonActive(hasGranularEdits);

                    ShowTacticalFeedback(hasGranularEdits
                        ? "Granular settings applied"
                        : "Granular segments cleared");

                    InvalidateVoiceOverRecordingForTimingChange();
                    UpdateEstimatedQuality();
                    UpdateTimelineMarkers();
                    SaveRecoveryState();
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
            videoMergerButton.Click += (s, e) =>
            {
                SaveRecoveryState(sync: true);
                _recovery.ReleaseLockOnly();
                RuntimeLog.Info("UI", "Opening Video Merger app and closing Main app.");
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "FortniteVideoSoftware.exe";
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath, "--merger") { UseShellExecute = false });
                if (p != null)
                {
                    Task.Run(async () =>
                    {
                        try { p.WaitForInputIdle(5000); await Task.Delay(500); } catch { }
                        Environment.Exit(0);
                    });
                }
                else
                {
                    Environment.Exit(0);
                }
            };
        }

        var cropSettingsButton = this.FindControl<Button>("CropSettingsButton");
        if (cropSettingsButton != null)
        {
            cropSettingsButton.Click += (s, e) =>
            {
                SaveRecoveryState(sync: true);
                _recovery.ReleaseLockOnly();
                RuntimeLog.Info("UI", "Opening Crop Tools app and closing Main app.");
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "FortniteVideoSoftware.exe";
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath, "--crop-tool") { UseShellExecute = false });
                if (p != null)
                {
                    Task.Run(async () =>
                    {
                        try { p.WaitForInputIdle(5000); await Task.Delay(500); } catch { }
                        Environment.Exit(0);
                    });
                }
                else
                {
                    Environment.Exit(0);
                }
            };
        }

        var playPauseButton = this.FindControl<Button>("PlayPauseButton");
        if (playPauseButton != null)
        {
            playPauseButton.Click += (s, e) =>
            {
                RuntimeLog.Info("UI", "User toggled Play/Pause state.");
                if (ActiveVideoHost?.IpcClient != null) 
                {
                    if (_isCurrentlyFrozen)
                    {
                        _isCurrentlyFrozen = false;
                        _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
                        return;
                    }
                    _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", ActiveVideoHost.IpcClient.IsPaused ? "no" : "yes");
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

        var voiceOverButton = this.FindControl<Button>("VoiceOverButton");
        if (voiceOverButton != null)
        {
            voiceOverButton.Click += async (s, e) =>
            {
                if (string.IsNullOrEmpty(_loadedVideoPath)) return;

                if (_voiceOverResult != null)
                {
                    var confirm = new ConfirmDialogWindow();
                    confirm.SetTitle("Remove Voice Over?");
                    confirm.SetMessage("This will remove the applied voiceover or voice-frequency muting from the current edit.");
                    confirm.SetButtonText("REMOVE", "KEEP");
                    await confirm.ShowDialog(this);
                    if (!confirm.Result)
                    {
                        ShowTacticalFeedback("Voice Over Kept");
                        return;
                    }

                    ApplyVoiceOverState(null);
                    ShowTacticalFeedback("Voice Over Removed");
                    return;
                }

                bool wasPlaying = ActiveVideoHost?.IpcClient?.IsPaused == false;
                if (wasPlaying) _ = ActiveVideoHost?.IpcClient?.SetPropertyAsync("pause", "yes");

                var dialog = new VoiceOverWindow(
                    _loadedVideoPath,
                    GetCurrentMpvTime(),
                    _trimStartMs,
                    _trimEndSet ? _trimEndMs : 0,
                    BuildExportSpeedSegments(),
                    _baseSpeed);
                try
                {
                    await dialog.ShowDialog(this);

                    if (HasVoiceOverEffect(dialog.Result))
                    {
                        ApplyVoiceOverState(dialog.Result);
                        ShowTacticalFeedback(HasVoiceOverWav(dialog.Result) ? "Voice Over Applied" : "Voice Effects Applied");
                    }
                }
                finally
                {
                    if (wasPlaying && ActiveVideoHost?.IpcClient != null)
                    {
                        _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "no");
                    }
                }
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
                markStartButton.Content = $"START: {FormatTime(TimeSpan.FromSeconds(time))}";

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
                if (time * 1000.0 <= _trimStartMs)
                {
                    ShowTacticalFeedback("⚠ END must be after START");
                    RuntimeLog.Info("UI", $"MARK END blocked: {TimeSpan.FromSeconds(time):hh\\:mm\\:ss\\.ff} is not after START {TimeSpan.FromMilliseconds(_trimStartMs):hh\\:mm\\:ss\\.ff}.");
                    return;
                }
                _trimEndMs = time * 1000;
                markEndButton.Content = $"END: {FormatTime(TimeSpan.FromSeconds(time))}";

                if (_videoHost?.IpcClient?.IsPaused == false)
                {
                    _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
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
                    double duration = ActiveVideoHost?.IpcClient?.Duration ?? 0.0;
                    if (duration > 0)
                    {
                        double targetTime = (e.NewValue / 100.0) * duration;
                        _ = SeekInternal(targetTime);
                        ShowPlayheadBadge(targetTime, e.NewValue);
                    }
                }
            };

            var timelineOverlay = this.FindControl<Border>("TimelineOverlay");
            if (timelineOverlay != null && canvas != null)
            {
                bool isScrubbing = false;
                timelineOverlay.PointerPressed += (s, e) => {
                    if (e.GetCurrentPoint(timelineOverlay).Properties.IsLeftButtonPressed) {
                        isScrubbing = true;
                        e.Pointer.Capture(timelineOverlay);
                        SeekTimelineFromPointer(e, canvas, timelineSlider);
                    }
                };
                timelineOverlay.PointerMoved += (s, e) => {
                    if (isScrubbing && e.GetCurrentPoint(timelineOverlay).Properties.IsLeftButtonPressed) {
                        SeekTimelineFromPointer(e, canvas, timelineSlider);
                    }
                };
                timelineOverlay.PointerReleased += (s, e) => {
                    isScrubbing = false;
                    e.Pointer.Capture(null);
                };
            }
        }

        var mainSpeedSlider = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("MainSpeedSlider");
        if (mainSpeedSlider != null)
        {
            mainSpeedSlider.SetRange(1, 40);
            var speedLabels = new System.Collections.Generic.List<string>();
            for (int i = 1; i <= 40; i++) speedLabels.Add((i / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "x");
            mainSpeedSlider.SetLabels(speedLabels);
            mainSpeedSlider.Value = 11;
            SpeedPresetButtons.ConfigureBaseButton(this, SpeedPresetButtons.NativeDefaultSpeed, "Set speed to the app default 1.1x");
            SpeedPresetButtons.WirePresetButtons(this, SpeedPresetButtons.NativeDefaultSpeed, ApplyMainSpeedPreset);
            mainSpeedSlider.ValueChanged += (s, e) =>
            {
                double previousSpeed = _baseSpeed;
                _baseSpeed = e / 10.0;
                if (ActiveVideoHost?.IpcClient != null)
                    _ = ActiveVideoHost?.IpcClient?.SetPropertyAsync("speed", _baseSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                if (Math.Abs(previousSpeed - _baseSpeed) > 0.001)
                    InvalidateVoiceOverRecordingForTimingChange();
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
                    ApplyMasterVolume(vol);
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

            var speakerHitBox = this.FindControl<Button>("SpeakerHitBox");
            if (speakerHitBox != null)
            {
                speakerHitBox.Click += SpeakerIcon_Click;
            }

            volumeSlider.PointerReleased += (s, e) =>
            {
                try
                {
                    new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths)
                        .UpdatePropertiesSync(new System.Text.Json.Nodes.JsonObject
                        {
                            ["MainVolume"] = volumeSlider.Value
                        });
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
            qualitySlider.Value = 7;
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

                if (ActiveVideoHost?.IpcClient != null)
                    _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "yes");

                SetTimelinePopupsVisible(false);
                var wizard = new MusicWizardWindow(
                    _loadedVideoPath,
                    _trimStartMs,
                    _trimEndMs > 0 ? _trimEndMs : (ActiveVideoHost?.IpcClient?.Duration ?? 0) * 1000,
                    _baseSpeed,
                    BuildExportSpeedSegments());
                await wizard.ShowDialog(this);
                SetTimelinePopupsVisible(true);

                if (wizard.Result != null)
                {
                    _musicWizardResult = wizard.Result;
                    NormalizeMusicPlacement(_musicWizardResult);

                    RuntimeLog.Info("UI", $"User added music via wizard: {Path.GetFileName(_musicWizardResult.MusicFilePath)}, ducking={_musicWizardResult.EnableDucking}");
                    RuntimeLog.Debug("UI", $"Music path added via wizard: {_musicWizardResult.MusicFilePath}");

                    SetMusicButtonActive(true);

                    var addMemeCb = this.FindControl<ToggleSwitch>("AddMemeCheckbox");
                    var memeCb = this.FindControl<ComboBox>("MemeComboBox");
                    if (addMemeCb?.IsChecked == true && memeCb?.SelectedItem != null)
                    {
                        _keepMusicDuringMeme = NativeDialog.ShowQuestion(
                            "Would you like the background music to keep on playing as the meme plays or not?\n\nYes! Keep the music playing in the background as the meme video plays.\nNO! Stop the music as the meme plays.",
                            "Background Music Behavior"
                        );
                    }

                    var volSlider = this.FindControl<Avalonia.Controls.Slider>("VolumeSlider");
                    if (volSlider != null)
                    {
                        ApplyMasterVolume((int)volSlider.Value);
                    }

                    UpdateTimelineMarkers();
                }
            };
        }

        var mobileCheckbox = (Avalonia.Controls.Primitives.ToggleButton?)this.FindControl<CheckBox>("MobileCheckbox") ?? this.FindControl<ToggleSwitch>("PortraitModeCheckbox");

        if (mobileCheckbox != null)
        {
            UpdatePortraitOverlay();
            mobileCheckbox.IsCheckedChanged += (s, e) =>
            {
                UpdatePortraitOverlay();
                ApplyMemeItemsToCombo(preserveSelection: true);
                SaveRecoveryState();
            };
        }

        var bossHpCb = this.FindControl<ToggleSwitch>("BossHpCheckbox");
        if (bossHpCb != null) bossHpCb.IsCheckedChanged += (s, e) => SaveRecoveryState();

        var teammatesCb = this.FindControl<ToggleSwitch>("TeammatesCheckbox");
        if (teammatesCb != null) teammatesCb.IsCheckedChanged += (s, e) => SaveRecoveryState();

        var spectatingCb = this.FindControl<ToggleSwitch>("SpectatingCheckbox");
        if (spectatingCb != null) spectatingCb.IsCheckedChanged += (s, e) => SaveRecoveryState();

        var enableFadeCb = this.FindControl<ToggleSwitch>("EnableFadeCheckbox");
        if (enableFadeCb != null) enableFadeCb.IsCheckedChanged += (s, e) => SaveRecoveryState();

        var portraitTextInput = this.FindControl<TextBox>("PortraitTextInput");
        if (portraitTextInput != null) portraitTextInput.TextChanged += (s, e) => { UpdatePortraitOverlay(); ScheduleRecoveryStateSave(); };

        var addMemeCb = this.FindControl<ToggleSwitch>("AddMemeCheckbox");
        if (addMemeCb != null)
        {
            addMemeCb.IsCheckedChanged += (s, e) => {
                if (addMemeCb.IsChecked == true) {
                    var cb = this.FindControl<ComboBox>("MemeComboBox");
                    if (cb != null) cb.IsDropDownOpen = true;
                }
                SaveRecoveryState();
            };
        }
        
        var memeCb = this.FindControl<ComboBox>("MemeComboBox");
        if (memeCb != null) memeCb.SelectionChanged += (s, e) => {
            if (memeCb.SelectedItem is MemeItem action && action.IsDownloadAction)
            {
                memeCb.SelectedItem = e.RemovedItems != null && e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null;
                _ = RunCloudMemeSyncAsync();
                return;
            }

            if (memeCb.SelectedItem is MemeItem sel)
            {
                RuntimeLog.Info("Memes",
                    $"MemeSelected: type={(sel.IsImage ? "image" : "video")}, file={sel.FileName}, " +
                    $"width={sel.Width}, height={sel.Height}, aspect={sel.AspectRatio:0.###}");
                RuntimeLog.Debug("Memes", $"MemeSelected full path: {sel.FullPath}");
            }

            SaveRecoveryState();
            if (addMemeCb?.IsChecked == true && memeCb.SelectedItem != null && _musicWizardResult != null && !string.IsNullOrEmpty(_musicWizardResult.MusicFilePath))
            {
                _keepMusicDuringMeme = NativeDialog.ShowQuestion(
                    "Would you like the background music to keep on playing as the meme plays or not?\n\nYes! Keep the music playing in the background as the meme video plays.\nNO! Stop the music as the meme plays.",
                    "Background Music Behavior"
                );
            }
        };

        var volSliderForRecovery = this.FindControl<Slider>("VolumeSlider");
        if (volSliderForRecovery != null) volSliderForRecovery.PropertyChanged += (s, e) =>
        {
            if (e.Property == Slider.ValueProperty) ScheduleRecoveryStateSave();
        };

        UpdateTooltips();
        AddHandler(InputElement.KeyDownEvent, GlobalKeyDownHandler, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyUpEvent, GlobalKeyUpHandler, RoutingStrategies.Tunnel);

        Infrastructure.MemeDirectory.Changed += PopulateMemeComboBox;
        this.Closed += (_, _) => Infrastructure.MemeDirectory.Changed -= PopulateMemeComboBox;

        this.Loaded += async (s, e) => {
            _ = PushAssetsAsync();
            PopulateMemeComboBox();

            bool hadFault = _recovery.CheckFault();
            if (hadFault)
            {
                RuntimeLog.Info("RECOVERY", "Previous crash detected. Prompting user for recovery.");
                bool shouldRestore = await Task.Run(() => NativeDialog.ShowQuestion(
                    "The app was closed unexpectedly during your last session.\n\n" +
                    "Would you like to restore your previous work? This includes your video, " +
                    "trim points, speed settings, music, and all edits exactly as you left them.\n\n" +
                    "Click Yes to recover, or No to start fresh.",
                    "Fortnite Video Software - Session Recovery"));

                _recovery.AcquireLock();

                if (shouldRestore)
                {
                    RuntimeLog.Info("RECOVERY", "User chose to restore previous session.");
                    _recovery.ActivateSafeMode();
                    await RestoreRecoveryStateAsync();
                    _recovery.DeactivateSafeMode();
                }
                else
                {
                    RuntimeLog.Info("RECOVERY", "User chose to start fresh. Discarding recovery state.");
                    _recovery.ClearState();
                }
            }
            else
            {
                _recovery.AcquireLock();
            }

            var store = new FortniteVideoSoftware.Core.Ipc.StateTransferStore();
            var state = await store.LoadAsync();

            try
            {
                if (state.TryGetPropertyValue("returned_from_crop_tool", out var rfct) && rfct?.GetValue<bool>() == true)
                {
                    RuntimeLog.Info("HANDOFF", "Returned from Crop Tools. Crop overlay configuration reloaded from the active profile.");
                }
            }
            catch { }

            var newObj = new System.Text.Json.Nodes.JsonObject();
            var preserveKeys = new[] { "schema_version", "MainWindowBounds", "PreviewMonitorWindowBounds", "VideoMergerBounds", "CropToolBounds", "GranularBounds", "MusicWizardBounds", "SettingsBounds", "VoiceOverWindowBounds", "UploadVideoDirectory", "MergerUploadDirectory", "MergerOutputDirectory", "CropToolUploadDirectory", "CustomMusicDirectory", "WizardVideoVolume", "WizardMusicVolume", "MainVolume" };
            foreach (var key in preserveKeys) {
                if (state.TryGetPropertyValue(key, out var gb)) {
                    newObj[key] = gb?.DeepClone();
                }
            }
            await store.SaveAsync(newObj);

            await InitializeHardwareScanAsync();
            UpdatePortraitOverlay();

            string? pendingOpenWith = OpenWithLaunch.PendingVideoPath;
            if (!string.IsNullOrEmpty(pendingOpenWith))
            {
                OpenWithLaunch.PendingVideoPath = null;
                await LoadVideoIntoEditorAsync(pendingOpenWith, "opened-with");
            }
        };

    }

    private List<MemeItem> _memeItems = new();
    private string? _pendingMemeRestorePath;

    private string ResolveMemeFfprobePath()
    {
        return BinaryPathResolver.Resolve("ffprobe.exe", "backend", "binaries");
    }

    private async Task<double> ProbeMusicDurationSecondsAsync(string musicPath)
    {
        if (string.IsNullOrWhiteSpace(musicPath) || !File.Exists(musicPath))
            return 0.0;

        try
        {
            var prober = new MediaProber(ResolveMemeFfprobePath(), musicPath);
            return await prober.GetDurationAsync();
        }
        catch
        {
            return 0.0;
        }
    }

    /// <summary>§1 scan + §2 guardrail: rebuilds the meme list from the ACTIVE directory with
    /// probed aspect ratios, red-marks portrait-unfit items, and appends the §4 cloud row.</summary>
    private async void PopulateMemeComboBox()
    {
        var cb = this.FindControl<ComboBox>("MemeComboBox");
        if (cb == null) return;

        string memeFolder = Infrastructure.MemeDirectory.GetActive();
        try
        {
            _memeItems = await MemeCatalog.ScanAsync(memeFolder, ResolveMemeFfprobePath());
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Memes", $"Meme directory scan failed for '{Path.GetFileName(memeFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}': {ex.Message}");
            RuntimeLog.Debug("Memes", $"Meme directory scan failed full path: {memeFolder}");
            _memeItems = new List<MemeItem>();
        }

        ApplyMemeItemsToCombo(preserveSelection: true);

        if (_pendingMemeRestorePath != null)
        {
            string p = _pendingMemeRestorePath;
            _pendingMemeRestorePath = null;
            var match = _memeItems.FirstOrDefault(m => string.Equals(m.FullPath, p, StringComparison.OrdinalIgnoreCase)
                                                    || string.Equals(m.FileName, Path.GetFileName(p), StringComparison.OrdinalIgnoreCase));
            if (match != null) cb.SelectedItem = match;
        }
    }

    /// <summary>§2 The Guardrail: portrait mode + landscape/square meme (ratio > 0.85) renders RED
    /// with a "Fit for landscape" tooltip. Re-invoked when PortraitModeCheckbox toggles.</summary>
    private void ApplyMemeItemsToCombo(bool preserveSelection)
    {
        var cb = this.FindControl<ComboBox>("MemeComboBox");
        if (cb == null) return;

        bool isPortrait = this.FindControl<ToggleSwitch>("PortraitModeCheckbox")?.IsChecked ?? true;

        cb.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<MemeItem>((item, _) =>
        {
            if (item == null) return new TextBlock();
            var tb = new TextBlock { Text = item.FileName, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            if (item.IsDownloadAction)
            {
                tb.Text = "⬇ Download more memes...";
                tb.FontWeight = Avalonia.Media.FontWeight.Bold;
                tb.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#38bdf8"));
            }
            else if (isPortrait && item.AspectRatio > 0.85f)
            {
                tb.Foreground = Avalonia.Media.Brushes.Red;
                ToolTip.SetTip(tb, "Fit for landscape");
            }
            return tb;
        });

        var prev = preserveSelection ? cb.SelectedItem as MemeItem : null;
        var list = new List<MemeItem>(_memeItems) { new MemeItem { IsDownloadAction = true, FileName = "Download more memes..." } };
        cb.ItemsSource = list;
        if (prev != null && !prev.IsDownloadAction)
        {
            var again = list.FirstOrDefault(m => !m.IsDownloadAction &&
                string.Equals(m.FullPath, prev.FullPath, StringComparison.OrdinalIgnoreCase));
            if (again != null) cb.SelectedItem = again;
        }
    }

    /// <summary>§4 consent + delta sync + silent §1 rescan.</summary>
    private async Task RunCloudMemeSyncAsync()
    {
        var dlg = new Controls.ConfirmDialogWindow();
        dlg.SetTitle("Download more memes?");
        dlg.SetMessage("This will connect to the internet and download new meme files from the official meme library into your meme folder. Continue?");
        dlg.SetButtonText("DOWNLOAD", "CANCEL");
        await dlg.ShowDialog(this);
        if (!dlg.Result) return;

        ShowTacticalFeedback("Downloading memes…");
        var (count, error) = await MemeCatalog.SyncFromCloudAsync(Infrastructure.MemeDirectory.GetActive());
        if (error != null)
        {
            ShowTacticalFeedback($"⚠ {error}");
            return;
        }
        ShowTacticalFeedback(count > 0 ? $"✔ {count} new meme(s) added" : "✔ Meme library is up to date");
        PopulateMemeComboBox();
    }

    private async Task PushAssetsAsync()
    {
        try
        {
            await Task.Run(() => {
                string musicFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                string videosFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                string memeFolder = Infrastructure.MemeDirectory.GetActive();

                if (!Directory.Exists(memeFolder)) Directory.CreateDirectory(memeFolder);

                string legacyMeme = Path.Combine(videosFolder, "MEME");
                if (Directory.Exists(legacyMeme) &&
                    !string.Equals(Path.GetFullPath(legacyMeme), Path.GetFullPath(memeFolder), StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var f in Directory.GetFiles(legacyMeme))
                    {
                        try
                        {
                            string dest = Path.Combine(memeFolder, Path.GetFileName(f));
                            if (!File.Exists(dest)) File.Copy(f, dest);
                        }
                        catch { }
                    }
                }

                string baseDir = System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory;
                string appRoot = Path.Combine(baseDir, "..", "..", "..", "..", "..");
                if (!Directory.Exists(Path.Combine(appRoot, "mp3"))) appRoot = baseDir;
                var music1 = Path.Combine(appRoot, "mp3", "Cool Dance Background Music (No CopyRights).mp3");
                var music2 = Path.Combine(appRoot, "mp3", "Gorillaz vs. The Killers- Somebody Told Me to Feel Good.mp3");
                var meme1 = Path.Combine(appRoot, "mp4", "Dexter Sargent Duke.mp4");
                var meme2 = Path.Combine(appRoot, "mp4", "Crying.mp4");
                var meme3 = Path.Combine(appRoot, "mp4", "Donald Trump - He Died like a Dog.mp4");
                var meme4 = Path.Combine(appRoot, "mp4", "What the fuck am I doing here (Robert Deniro).mp4");

                if (File.Exists(music1) && !File.Exists(Path.Combine(musicFolder, Path.GetFileName(music1))))
                    File.Copy(music1, Path.Combine(musicFolder, Path.GetFileName(music1)));
                if (File.Exists(music2) && !File.Exists(Path.Combine(musicFolder, Path.GetFileName(music2))))
                    File.Copy(music2, Path.Combine(musicFolder, Path.GetFileName(music2)));
                
                if (File.Exists(meme1) && !File.Exists(Path.Combine(memeFolder, Path.GetFileName(meme1))))
                    File.Copy(meme1, Path.Combine(memeFolder, Path.GetFileName(meme1)));
                if (File.Exists(meme2) && !File.Exists(Path.Combine(memeFolder, Path.GetFileName(meme2))))
                    File.Copy(meme2, Path.Combine(memeFolder, Path.GetFileName(meme2)));
                if (File.Exists(meme3) && !File.Exists(Path.Combine(memeFolder, Path.GetFileName(meme3))))
                    File.Copy(meme3, Path.Combine(memeFolder, Path.GetFileName(meme3)));
                if (File.Exists(meme4) && !File.Exists(Path.Combine(memeFolder, Path.GetFileName(meme4))))
                    File.Copy(meme4, Path.Combine(memeFolder, Path.GetFileName(meme4)));

                var imageSourceDir = Path.Combine(appRoot, "jpeg");
                if (Directory.Exists(imageSourceDir))
                {
                    foreach (var img in Directory.GetFiles(imageSourceDir))
                    {
                        string ext = Path.GetExtension(img).ToLowerInvariant();
                        if (ext != ".jpg" && ext != ".jpeg" && ext != ".png") continue;
                        string imgDest = Path.Combine(memeFolder, Path.GetFileName(img));
                        if (!File.Exists(imgDest)) File.Copy(img, imgDest);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("STARTUP", $"Failed to push assets: {ex.Message}");
        }
    }

    private void SpeakerIcon_Click(object? sender, RoutedEventArgs e)
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

    private void SeekTimelineFromPointer(Avalonia.Input.PointerEventArgs e, Canvas timelineCanvas, Slider timelineSlider)
    {
        if (e.Handled) return;
        if (_draggingStartMarker || _draggingEndMarker) return;

        double duration = ActiveVideoHost?.IpcClient?.Duration ?? 0.0;
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
        ShowPlayheadBadge(targetTime, sliderValue);
        e.Handled = true;
    }

    private Avalonia.Threading.DispatcherTimer? _playheadBadgeTimer;
    private void ShowPlayheadBadge(double timeSeconds, double sliderValuePercentage)
    {
        var badge = this.FindControl<Avalonia.Controls.Border>("PlayheadBadge");
        var text = this.FindControl<Avalonia.Controls.TextBlock>("PlayheadBadgeText");
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineMarkersCanvas");

        if (badge != null && text != null && canvas != null)
        {
            text.Text = FormatTime(TimeSpan.FromSeconds(timeSeconds), true);
            double canvasWidth = canvas.Bounds.Width;
            double x = (sliderValuePercentage / 100.0) * canvasWidth;
            Avalonia.Controls.Canvas.SetLeft(badge, x - 25);
            badge.Opacity = 1.0;

            if (_playheadBadgeTimer == null)
            {
                _playheadBadgeTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _playheadBadgeTimer.Tick += (s, ev) =>
                {
                    _playheadBadgeTimer.Stop();
                    badge.Opacity = 0.0;
                };
            }
            _playheadBadgeTimer.Stop();
            _playheadBadgeTimer.Start();
        }
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
        if (e.Data.Contains(Avalonia.Input.DataFormats.Files) || e.Data.Contains(Avalonia.Input.DataFormats.FileNames) || e.Data.GetFiles()?.Any() == true)
        {
            e.DragEffects = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;
            var uploadOverlay = this.FindControl<Border>("UploadOverlay");
            if (uploadOverlay != null && uploadOverlay.IsVisible)
            {
                uploadOverlay.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#40FF1493"));
            }
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnVideoDragLeave(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var uploadOverlay = this.FindControl<Border>("UploadOverlay");
        if (uploadOverlay != null && uploadOverlay.IsVisible)
        {
            uploadOverlay.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#cc1e293b"));
        }
    }

    private void OnVideoDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(Avalonia.Input.DataFormats.Files) || e.Data.Contains(Avalonia.Input.DataFormats.FileNames) || e.Data.GetFiles()?.Any() == true)
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private async void OnVideoDrop(object? sender, DragEventArgs e)
    {
        var uploadOverlay = this.FindControl<Border>("UploadOverlay");
        if (uploadOverlay != null && uploadOverlay.IsVisible)
        {
            uploadOverlay.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#cc1e293b"));
        }

        var files = e.Data.GetFiles();
        if (files == null)
        {
            return;
        }

        await HandleExternalFileDropAsync(files.Select(f => f.Path.LocalPath).ToArray());
    }

    /// <summary>
    /// Shared entry point for externally dropped files (Avalonia OLE drop and the
    /// WM_DROPFILES fallback). Loads the first supported video straight into the
    /// preview player, exactly as if it had been uploaded.
    /// </summary>
    private async Task HandleExternalFileDropAsync(string[] paths)
    {
        foreach (var path in paths)
        {
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
            Title = "Open Video (select multiple — the newest file is loaded)",
            AllowMultiple = true,
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
                    var state = FortniteVideoSoftware.Core.Infrastructure.AtomicJsonFile.ReadObject(stateFile);
                    if (state != null && state.TryGetPropertyValue("UploadVideoDirectory", out var node) && node != null)
                    {
                        startPath = node.ToString();
                    }
                }

                if (string.IsNullOrEmpty(startPath) || !Directory.Exists(startPath))
                {
                    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string myVideos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                    string myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    string hardcodedVideos = System.IO.Path.Combine(userProfile, "Videos");
                    string[] probes = new[]
                    {
                        System.IO.Path.Combine(myVideos, "Fortnite"),
                        System.IO.Path.Combine(hardcodedVideos, "Fortnite"),
                        System.IO.Path.Combine(myVideos, "Highlights", "Fortnite"),
                        System.IO.Path.Combine(hardcodedVideos, "Highlights", "Fortnite"),
                        System.IO.Path.Combine(myVideos, "Highlights", "Fortnite"),
                        System.IO.Path.Combine(localAppData, "Temp", "Highlights", "Fortnite"),
                        System.IO.Path.Combine(localAppData, "Temp", "Highlights"),
                        System.IO.Path.Combine(localAppData, "NVIDIA Corporation", "GeForce Experience", "Highlights"),
                        System.IO.Path.Combine(myVideos, "Highlights"),
                        System.IO.Path.Combine(myDocuments, "Highlights")
                    };

                    startPath = myVideos;
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
                    try 
                    {
                        var uri = new Uri(startPath);
                        options.SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(uri);
                    } 
                    catch { }
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
            string selectedPath = files
                .Select(f => f.Path.LocalPath)
                .Where(File.Exists)
                .OrderByDescending(p => { try { return File.GetLastWriteTimeUtc(p); } catch { return DateTime.MinValue; } })
                .FirstOrDefault() ?? files[0].Path.LocalPath;

            if (files.Count > 1)
            {
                RuntimeLog.Info("UI", $"User marked {files.Count} files; loading newest by modified time: {Path.GetFileName(selectedPath)}");
                RuntimeLog.Debug("UI", $"Full path: {selectedPath}");
            }

            try
            {
                string dir = Path.GetDirectoryName(selectedPath) ?? "";
                if (!string.IsNullOrEmpty(dir))
                {
                    System.Text.Json.Nodes.JsonObject state;
                    state = new System.Text.Json.Nodes.JsonObject
                    {
                        ["UploadVideoDirectory"] = dir
                    };
                    new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths).UpdatePropertiesSync(state);
                }
            }
            catch { }

            await LoadVideoIntoEditorAsync(selectedPath, "uploaded");
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

        RuntimeLog.Info("UI", $"User {source} video: {Path.GetFileName(path)}");
        RuntimeLog.Debug("UI", $"Full path: {path}");
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
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths)
                    .UpdatePropertiesSync(new System.Text.Json.Nodes.JsonObject
                    {
                        ["UploadVideoDirectory"] = directory
                    });
            }
        }
        catch { }
    }

    private void ResetProjectStateToUpload()
    {
        _loadedVideoPath = string.Empty;
        if (ActiveVideoHost?.IpcClient != null)
        {
            _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
            ActiveVideoHost.IsVisible = false;
        }

        ResetEditingStateForNewVideo();
        UpdateTimelineMarkers();
        
        var uploadOverlay = this.FindControl<Border>("UploadOverlay");
        if (uploadOverlay != null) uploadOverlay.IsVisible = true;

        var timelineOverlay = this.FindControl<Border>("TimelineOverlay");
        if (timelineOverlay != null) timelineOverlay.IsVisible = false;

        var process = this.FindControl<Button>("ProcessButton");
        if (process != null) process.IsEnabled = false;
        var playPause = this.FindControl<Button>("PlayPauseButton");
        if (playPause != null) playPause.IsEnabled = false;
        var markStart = this.FindControl<Button>("MarkStartButton");
        if (markStart != null) markStart.IsEnabled = false;
        var markEnd = this.FindControl<Button>("MarkEndButton");
        if (markEnd != null) markEnd.IsEnabled = false;
        var thumb = this.FindControl<Button>("SetThumbnailButton");
        if (thumb != null) thumb.IsEnabled = false;
        var addMusic = this.FindControl<Button>("AddMusicButton");
        if (addMusic != null) addMusic.IsEnabled = false;
        var gran = this.FindControl<Button>("GranularButton");
        if (gran != null) gran.IsEnabled = false;
        var voBtn = this.FindControl<Button>("VoiceOverButton");
        if (voBtn != null) voBtn.IsEnabled = false;
        
        SaveRecoveryState();
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
        ApplyVoiceOverState(null, isRestore: true);

        var addMemeCb = this.FindControl<ToggleSwitch>("AddMemeCheckbox");
        if (addMemeCb != null) addMemeCb.IsChecked = false;

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
        if (ActiveVideoHost?.IpcClient == null) return false;

        for (int i = 0; i < 30; i++)
        {
            double duration = ActiveVideoHost.IpcClient.Duration;
            if (duration > 0 && (previousDuration <= 0 || Math.Abs(duration - previousDuration) > 0.01 || i >= 10))
            {
                return true;
            }

            await Task.Delay(100);
        }

        return ActiveVideoHost.IpcClient.Duration > 0;
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
        if (ActiveVideoHost?.IpcClient != null)
            _ = ActiveVideoHost.IpcClient.SetPropertyAsync("speed", _baseSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));

        var qs = this.FindControl<SpinningWheelSlider>("QualitySlider");
        if (qs != null && !_qualitySliderInitialized) 
        {
            qs.Value = d.QualityIndex;
            _qualitySliderInitialized = true;
        }

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

        var portrait = this.FindControl<ToggleSwitch>("PortraitModeCheckbox");
        if (portrait != null) portrait.IsChecked = d.PortraitMode;

        var bossHp = this.FindControl<ToggleSwitch>("BossHpCheckbox");
        if (bossHp != null) bossHp.IsChecked = d.BossHp;

        var teammates = this.FindControl<ToggleSwitch>("TeammatesCheckbox");
        if (teammates != null) teammates.IsChecked = d.ShowTeammates;

        var enableFade = this.FindControl<ToggleSwitch>("EnableFadeCheckbox");
        if (enableFade != null) enableFade.IsChecked = d.EnableFade;
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
        
        var voiceOver = this.FindControl<Button>("VoiceOverButton");
        if (voiceOver != null) voiceOver.IsEnabled = true;

        var markEnd = this.FindControl<Button>("MarkEndButton");
        if (markEnd != null) markEnd.IsEnabled = true;

        var process = this.FindControl<Button>("ProcessButton");
        if (process != null) process.IsEnabled = true;

        var addMusic = this.FindControl<Button>("AddMusicButton");
        if (addMusic != null) addMusic.IsEnabled = true;

        var portrait = this.FindControl<ToggleSwitch>("PortraitModeCheckbox");
        if (portrait != null) portrait.IsEnabled = true;

        var bossHp = this.FindControl<ToggleSwitch>("BossHpCheckbox");
        if (bossHp != null) bossHp.IsEnabled = true;

        var teammates = this.FindControl<ToggleSwitch>("TeammatesCheckbox");
        if (teammates != null) teammates.IsEnabled = true;

        var spectatingToggle = this.FindControl<ToggleSwitch>("SpectatingCheckbox");
        if (spectatingToggle != null) spectatingToggle.IsEnabled = true;

        var enableFade = this.FindControl<ToggleSwitch>("EnableFadeCheckbox");
        if (enableFade != null) enableFade.IsEnabled = true;

        var addMemeCb = this.FindControl<ToggleSwitch>("AddMemeCheckbox");
        if (addMemeCb != null) addMemeCb.IsEnabled = true;

        var qualityPanel = this.FindControl<StackPanel>("QualityPanel");
        if (qualityPanel != null) qualityPanel.IsVisible = true;
        var speedPanel = this.FindControl<Grid>("SpeedPanel");
        if (speedPanel != null) speedPanel.IsVisible = true;

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

        var speedPresets = this.FindControl<Grid>("MainSpeedPresetsPanel");
        if (speedPresets != null) speedPresets.IsEnabled = true;
    }

    private void UpdatePortraitOverlay()
    {
        var mobileCheckbox = (Avalonia.Controls.Primitives.ToggleButton?)this.FindControl<CheckBox>("MobileCheckbox") ?? this.FindControl<ToggleSwitch>("PortraitModeCheckbox");
        var portraitTextInput = this.FindControl<TextBox>("PortraitTextInput");

        bool isPortrait = mobileCheckbox?.IsChecked == true;

        if (portraitTextInput != null)
            portraitTextInput.IsVisible = isPortrait;

        var previewPortraitImage = this.FindControl<FortniteVideoSoftware.App.Controls.PhoneFrameMockup>("PhoneFrame")?.PortraitImageControl;
        if (previewPortraitImage != null && portraitTextInput != null && isPortrait)
        {
            try
            {
                string tempPngPath = System.IO.Path.Combine(_paths.TempDirectory, "preview_portrait_text.png");
                
                FortniteVideoSoftware.Core.Media.TextOverlayGenerator.GeneratePng(portraitTextInput.Text ?? "", tempPngPath);
                
                using (var stream = System.IO.File.OpenRead(tempPngPath))
                {
                    var bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                    var oldBitmap = previewPortraitImage.Source as Avalonia.Media.Imaging.Bitmap;
                    previewPortraitImage.Source = bitmap;
                    oldBitmap?.Dispose();
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("UI", ex);
            }
        }
        
        ApplyPortraitModeToActiveHost();
    }

    private void ApplyPortraitModeToActiveHost()
    {
        var mobileCheckbox = (Avalonia.Controls.Primitives.ToggleButton?)this.FindControl<CheckBox>("MobileCheckbox") ?? this.FindControl<ToggleSwitch>("PortraitModeCheckbox");
        bool isPortrait = mobileCheckbox?.IsChecked == true;
        bool isVideoLoaded = !string.IsNullOrEmpty(_loadedVideoPath);
        RuntimeLog.Info("UI", $"ApplyPortraitModeToActiveHost: evaluated isPortrait={isPortrait}, isVideoLoaded={isVideoLoaded}");

        var portraitDimmingGrid = this.FindControl<Grid>("PortraitDimmingGrid");
        if (portraitDimmingGrid != null)
        {
            portraitDimmingGrid.IsVisible = isPortrait && (_detachedPreviewWindow == null) && isVideoLoaded;
        }

        if (_videoHost != null)
        {
            _videoHost.RenderTransform = null;
        }
        
        if (_detachedPreviewWindow != null)
        {
            _detachedPreviewWindow.TogglePortraitOverlay(isPortrait && isVideoLoaded);
            
            var previewPortraitImage = this.FindControl<FortniteVideoSoftware.App.Controls.PhoneFrameMockup>("PhoneFrame")?.PortraitImageControl;
            if (isPortrait && isVideoLoaded && previewPortraitImage != null)
            {
                _detachedPreviewWindow.SetSkiaTextPlaceholder(previewPortraitImage.Source as Avalonia.Media.Imaging.Bitmap);
            }
        }
        
        if (ActiveVideoHost?.IpcClient != null)
        {
            _ = ActiveVideoHost.IpcClient.SetPropertyAsync("vf", "");
        }

        UpdateEstimatedQuality();
    }

    private void PlayUiSound()
    {
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
        if (canvas == null || ActiveVideoHost?.IpcClient == null) return;

        double duration = ActiveVideoHost.IpcClient.Duration;
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

            MoveThumbnailMarkerToCanvasX(e.GetPosition(timelineCanvas).X, timelineCanvas, durationSeconds, marker, seekPreview: false);
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

    private void MoveThumbnailMarkerByFrames(int frameDelta)
    {
        double duration = ActiveVideoHost?.IpcClient?.Duration ?? 0.0;
        if (!_thumbnailSet || duration <= 0)
        {
            return;
        }

        double fps = 60.0;
        double deltaMs = (1000.0 / fps) * frameDelta;
        _thumbnailPosMs = Math.Clamp(_thumbnailPosMs + deltaMs, 0, duration * 1000.0);
        SeekMainPreviewToMarkerMs(_thumbnailPosMs);
        UpdateTimelineMarkers();
        UpdateEstimatedQuality();
        SaveRecoveryState();
    }

    private void SeekMainPreviewToMarkerMs(double markerMs)
    {
        if (ActiveVideoHost?.IpcClient == null)
        {
            return;
        }

        _isCurrentlyFrozen = false;
        _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
        _ = SeekInternal(markerMs / 1000.0);
    }

    public async Task AttachPreviewMonitor()
    {
        if (_detachedPreviewWindow == null) return;
        
        RuntimeLog.Info("UI", "Attaching Preview Monitor to main window.");
        
        var w = _detachedPreviewWindow;
        _detachedPreviewWindow = null;
        
        if (_videoHost != null)
        {
            w.VideoContainerControl.Child = null;

            var parentGrid = this.FindControl<Grid>("MainVideoGrid");
            if (parentGrid != null)
            {
                _videoHost.Margin = new Avalonia.Thickness(0);
                parentGrid.Children.Insert(0, _videoHost);
            }
        }

        w.Close();
        
        var detachOverlayBtn = this.FindControl<Button>("DetachOverlayButton");
        if (detachOverlayBtn != null) detachOverlayBtn.Content = "◳ Detach Monitor";

        if (_videoHost != null)
        {
            _videoHost.IsVisible = true;
            var watermark = this.FindControl<Border>("PreviewDetachedWatermark");
            if (watermark != null) watermark.IsVisible = false;

            ApplyPortraitModeToActiveHost();
        }
    }

    private async Task DetachPreviewMonitor()
    {
        if (_detachedPreviewWindow != null || string.IsNullOrEmpty(_loadedVideoPath)) return;

        RuntimeLog.Info("UI", "Detaching Preview Monitor to floating window.");
        
        if (_videoHost != null)
        {
            if (_videoHost.Parent is Avalonia.Controls.Panel parentPanel)
            {
                parentPanel.Children.Remove(_videoHost);
            }
            else if (_videoHost.Parent is Avalonia.Controls.Decorator decorator)
            {
                decorator.Child = null;
            }
            else if (_videoHost.Parent is Avalonia.Controls.ContentControl cc)
            {
                cc.Content = null;
            }
            else if (_videoHost.Parent is Avalonia.Controls.Control controlParent && controlParent.Parent is Avalonia.Controls.Panel gp)
            {
                gp.Children.Remove(_videoHost);
            }
        }

        var watermark = this.FindControl<Border>("PreviewDetachedWatermark");
        if (watermark != null) watermark.IsVisible = true;

        var detachOverlayBtn = this.FindControl<Button>("DetachOverlayButton");
        if (detachOverlayBtn != null) detachOverlayBtn.Content = "◱ Attach Monitor";

        _detachedPreviewWindow = new PreviewMonitorWindow();
        
        if (_videoHost != null)
        {
            _videoHost.Margin = new Avalonia.Thickness(0);
            _detachedPreviewWindow.VideoContainerControl.Child = _videoHost;
        }

        _detachedPreviewWindow.ParentMainWindow = this;

        if (_detachedPreviewWindow.WindowStartupLocation == Avalonia.Controls.WindowStartupLocation.CenterScreen)
        {
            _detachedPreviewWindow.WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.Manual;
            double w = double.IsNaN(_detachedPreviewWindow.Width) ? 1280 : _detachedPreviewWindow.Width;
            double h = double.IsNaN(_detachedPreviewWindow.Height) ? 750 : _detachedPreviewWindow.Height;
            
            int px = this.Position.X + (int)((this.Bounds.Width - w) / 2);
            int py = Math.Max(0, this.Position.Y + (int)((this.Bounds.Height - h) / 2) - 150);
            
            if (this.Screens?.Primary != null)
            {
                var screen = this.Screens.Primary.Bounds;
                px = Math.Max(screen.X, Math.Min(px, screen.X + screen.Width - (int)w));
                py = Math.Max(screen.Y, Math.Min(py, screen.Y + screen.Height - (int)h));
            }
            
            _detachedPreviewWindow.Position = new Avalonia.PixelPoint(px, py);
        }

        _detachedPreviewWindow.Show(this);

        ApplyPortraitModeToActiveHost();
    }

    private async Task InitializeMpvInstanceAsync(MpvVideoView view)
    {
        string mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "frontend", "mpv.exe");
        if (!System.IO.File.Exists(mpvPath)) mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "mpv.exe");
        if (!System.IO.File.Exists(mpvPath)) mpvPath = "mpv.exe";
        
        await view.StartMpvProcessAsync(mpvPath);
        if (view.IpcClient != null)
        {
            view.IpcClient.SeekCompleted -= OnSeekCompleted;
            view.IpcClient.SeekCompleted += OnSeekCompleted;
        }
    }

    private double CalculateEffectiveDurationMs(double trimStartMs, double trimEndMs, double baseSpeed)
    {
        return CalculateEffectiveDurationMs(trimStartMs, trimEndMs, baseSpeed, BuildExportSpeedSegments());
    }

    private System.Collections.Generic.List<FortniteVideoSoftware.Core.Media.SpeedSegment> BuildExportSpeedSegments()
    {
        var segments = new System.Collections.Generic.List<FortniteVideoSoftware.Core.Media.SpeedSegment>(_speedSegments);
        if (_freezeTimeMs >= 0)
        {
            segments.Add(new SpeedSegment((int)_freezeTimeMs, (int)(_freezeTimeMs + _freezeDurationS * 1000.0), 0.0));
        }
        return segments;
    }

    private static double CalculateEffectiveDurationMs(
        double trimStartMs,
        double trimEndMs,
        double baseSpeed,
        System.Collections.Generic.IReadOnlyList<FortniteVideoSoftware.Core.Media.SpeedSegment>? speedSegments)
    {
        if (speedSegments == null || speedSegments.Count == 0)
        {
            return (trimEndMs - trimStartMs) / baseSpeed;
        }

        double totalMs = 0.0;
        double cursor = trimStartMs;

        var sortedSegments = new System.Collections.Generic.List<FortniteVideoSoftware.Core.Media.SpeedSegment>(speedSegments);
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
            if (label == null || slider == null || ActiveVideoHost?.IpcClient == null) return;

            double duration = ActiveVideoHost.IpcClient.Duration * 1000.0;
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
            var cb = this.FindControl<ToggleSwitch>("PortraitModeCheckbox");
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

        double previousSpeed = _baseSpeed;
        _baseSpeed = Math.Clamp(speed, 0.1, 4.0);
        if (ActiveVideoHost?.IpcClient != null)
        {
            _ = ActiveVideoHost.IpcClient.SetPropertyAsync(
                "speed",
                _baseSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (Math.Abs(previousSpeed - _baseSpeed) > 0.001)
            InvalidateVoiceOverRecordingForTimingChange();
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
        var bottomCanvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineBottomCanvas");
        var scaleCanvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineScaleCanvas");
        if (canvas == null || ActiveVideoHost?.IpcClient == null) return;

        double duration = ActiveVideoHost.IpcClient.Duration;

        if (duration <= 0) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            canvas.Children.Clear();
            bottomCanvas?.Children.Clear();
            scaleCanvas?.Children.Clear();
            double canvasWidth = canvas.Bounds.Width;
            if (canvasWidth <= 0) return;
            double ClampLabelLeft(double desired, double approxWidth)
                => Math.Max(0, Math.Min(Math.Max(0, canvasWidth - approxWidth), desired));
            const double trimMarkerWidth = 3.0;
            const double trimMarkerTop = 0.0;
            double trimMarkerHeight = Math.Max(1, canvas.Bounds.Height);

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
                _regionRectRef = regionRect;
            }


            if (_speedSegments != null && _speedSegments.Count > 0)
            {
                foreach (var seg in _speedSegments)
                {
                    double segStartX = (seg.StartMs / 1000.0 / duration) * canvasWidth;
                    double segEndX = (seg.EndMs / 1000.0 / duration) * canvasWidth;
                    double segW = Math.Max(2, segEndX - segStartX);

                    Avalonia.Media.Color segColor;
                    double speed = seg.Speed;
                    double baseSpd = _baseSpeed;

                    if (speed < 0.01)
                    {
                        segColor = Avalonia.Media.Color.FromArgb(230, 96, 165, 250);
                    }
                    else if (speed < baseSpd - 0.0001)
                    {
                        double factor = Math.Clamp((baseSpd - speed) / Math.Max(0.001, baseSpd - 0.1), 0.0, 1.0);
                        byte alpha = (byte)(51 + factor * (230 - 51));
                        segColor = Avalonia.Media.Color.FromArgb(alpha, 239, 68, 68);
                    }
                    else
                    {
                        double factor = Math.Clamp((speed - baseSpd) / Math.Max(0.001, 4.1 - baseSpd), 0.0, 1.0);
                        byte alpha = (byte)(51 + factor * (230 - 51));
                        segColor = Avalonia.Media.Color.FromArgb(alpha, 34, 197, 94);
                    }

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

            if (_freezeTimeMs >= 0)
            {
                double freezeX = (_freezeTimeMs / 1000.0 / duration) * canvasWidth;
                var freezeCamera = CreateTimelineCameraIcon(false, 0, out _, out _);
                Avalonia.Controls.Canvas.SetTop(freezeCamera, -79);
                Avalonia.Controls.Canvas.SetLeft(freezeCamera, ClampTimelineCameraLeft(freezeX, canvasWidth));
                Avalonia.Controls.ToolTip.SetTip(freezeCamera, $"Freeze Image set at {FormatTime(TimeSpan.FromMilliseconds(_freezeTimeMs))} for {_freezeDurationS:0.0}s");
                canvas.Children.Add(freezeCamera);
                
                var freezeLine = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = 4,
                    Height = trimMarkerHeight,
                    Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(96, 165, 250)),
                    IsHitTestVisible = false
                };
                Avalonia.Controls.Canvas.SetLeft(freezeLine, freezeX);
                Avalonia.Controls.Canvas.SetTop(freezeLine, trimMarkerTop);
                canvas.Children.Add(freezeLine);
            }

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
                        FontSize = Infrastructure.ThemeManager.ScaledFontSize(9)
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
                var startHitBox = new Avalonia.Controls.Border {
                    Width = 24, Height = trimMarkerHeight, Background = Avalonia.Media.Brushes.Transparent,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast)
                };
                var startRect = new Avalonia.Controls.Shapes.Rectangle { Fill = Avalonia.Media.Brushes.SeaGreen, Width = trimMarkerWidth, Height = trimMarkerHeight, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
                startHitBox.Child = startRect;

                Avalonia.Controls.Canvas.SetLeft(startHitBox, startX - 12);
                Avalonia.Controls.Canvas.SetTop(startHitBox, trimMarkerTop);

                startHitBox.PointerEntered += (s,e) => { startHitBox.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(40, 46, 139, 87)); startRect.Fill = Avalonia.Media.Brushes.MediumSeaGreen; };
                startHitBox.PointerExited += (s,e) => { startHitBox.Background = Avalonia.Media.Brushes.Transparent; startRect.Fill = Avalonia.Media.Brushes.SeaGreen; };
                startHitBox.PointerPressed += (s,e) => { _draggingStartMarker = true; e.Pointer.Capture(startHitBox); e.Handled = true; };
                
                canvas.Children.Add(startHitBox);

                var startText = new TextBlock { Text = "START", Foreground = Avalonia.Media.Brushes.SeaGreen, FontSize = Infrastructure.ThemeManager.ScaledFontSize(9), FontWeight = Avalonia.Media.FontWeight.Bold, Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#80000000")), Padding = new Avalonia.Thickness(2,0) };
                if (scaleCanvas != null)
                {
                    Avalonia.Controls.Canvas.SetLeft(startText, ClampLabelLeft(startX + 5, 36));
                    Avalonia.Controls.Canvas.SetTop(startText, 0);
                    scaleCanvas.Children.Add(startText);
                }

                startHitBox.PointerMoved += (s,e) => {
                    if (_draggingStartMarker) {
                        var pt = e.GetPosition(canvas);
                        double newX = Math.Max(0, Math.Min(pt.X, canvasWidth));
                        
                        double currentEndSec = _trimEndMs / 1000.0;
                        double currentEndX = (currentEndSec / duration) * canvasWidth;
                        
                        if (_trimEndMs > 0 && newX >= currentEndX) {
                            newX = currentEndX - 1;
                        }
                        
                        double newStartSec = (newX / canvasWidth) * duration;
                        _trimStartMs = newStartSec * 1000.0;
                        Avalonia.Controls.Canvas.SetLeft(startHitBox, newX - 12);
                        Avalonia.Controls.Canvas.SetLeft(startText, ClampLabelLeft(newX + 5, 36));
                        UpdateDraggingVisuals(canvasWidth, duration);
                    }
                };

                startHitBox.PointerReleased += (s,e) => {
                    if (_draggingStartMarker) {
                        _draggingStartMarker = false;
                        e.Pointer.Capture(null);

                        SetTrimStart(_trimStartMs);
                        RuntimeLog.Info("UI", $"Trim START marker dragged to {TimeSpan.FromMilliseconds(_trimStartMs):hh\\:mm\\:ss\\.ff}.");

                        if (ActiveVideoHost?.IpcClient?.IsPaused == true)
                        {
                            _ = SeekInternal(_trimStartMs / 1000.0);
                        }

                        var markStartBtn = this.FindControl<Avalonia.Controls.Button>("MarkStartButton");
                        if (markStartBtn != null) markStartBtn.Content = "START: " + FormatTime(TimeSpan.FromMilliseconds(_trimStartMs));
                        PlayUiSound();
                        ShowTacticalFeedback("🏁 " + TimeSpan.FromMilliseconds(_trimStartMs).ToString("mm\\:ss\\.ff"));
                        ShowTimelineGlow(_trimStartMs, Avalonia.Media.Brushes.SeaGreen);
                        UpdateTimelineMarkers();
                        UpdateEstimatedQuality(); 
                        SaveRecoveryState(); 
                        UpdateDraggingVisuals(canvasWidth, duration);
                    }
                };
            }

            if (_trimEndMs > 0)
            {
                double endX = (_trimEndMs / 1000.0 / duration) * canvasWidth;
                var endHitBox = new Avalonia.Controls.Border {
                    Width = 24, Height = trimMarkerHeight, Background = Avalonia.Media.Brushes.Transparent,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast)
                };
                var endRect = new Avalonia.Controls.Shapes.Rectangle { Fill = Avalonia.Media.Brushes.SeaGreen, Width = trimMarkerWidth, Height = trimMarkerHeight, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
                endHitBox.Child = endRect;

                Avalonia.Controls.Canvas.SetLeft(endHitBox, endX - 12);
                Avalonia.Controls.Canvas.SetTop(endHitBox, trimMarkerTop);

                endHitBox.PointerEntered += (s,e) => { endHitBox.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(40, 46, 139, 87)); endRect.Fill = Avalonia.Media.Brushes.MediumSeaGreen; };
                endHitBox.PointerExited += (s,e) => { endHitBox.Background = Avalonia.Media.Brushes.Transparent; endRect.Fill = Avalonia.Media.Brushes.SeaGreen; };

                endHitBox.PointerPressed += (s,e) => {
                    if (e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) {
                        _draggingEndMarker = true;
                        e.Pointer.Capture(endHitBox);
                        UpdateDraggingVisuals(canvasWidth, duration);
                    }
                };
                canvas.Children.Add(endHitBox);

                var endText = new TextBlock { Text = "END", Foreground = Avalonia.Media.Brushes.SeaGreen, FontSize = Infrastructure.ThemeManager.ScaledFontSize(9), FontWeight = Avalonia.Media.FontWeight.Bold, Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#80000000")), Padding = new Avalonia.Thickness(2,0) };
                if (scaleCanvas != null)
                {
                    Avalonia.Controls.Canvas.SetLeft(endText, ClampLabelLeft(endX - 28, 28));
                    Avalonia.Controls.Canvas.SetTop(endText, 0);
                    scaleCanvas.Children.Add(endText);
                }

                endHitBox.PointerMoved += (s,e) => {
                    if (_draggingEndMarker) {
                        var pt = e.GetPosition(canvas);
                        double newX = Math.Max(0, Math.Min(pt.X, canvasWidth));
                        
                        double currentStartSec = _trimStartMs / 1000.0;
                        double currentStartX = (currentStartSec / duration) * canvasWidth;
                        
                        if (newX <= currentStartX) {
                            newX = currentStartX + 1;
                        }
                        
                        double newEndSec = (newX / canvasWidth) * duration;
                        _trimEndMs = newEndSec * 1000.0;
                        Avalonia.Controls.Canvas.SetLeft(endHitBox, newX - 12);
                        if (scaleCanvas != null) Avalonia.Controls.Canvas.SetLeft(endText, ClampLabelLeft(newX - 28, 28));
                        UpdateDraggingVisuals(canvasWidth, duration);
                    }
                };
                endHitBox.PointerReleased += (s,e) => {
                    if (_draggingEndMarker) {
                        _draggingEndMarker = false;
                        e.Pointer.Capture(null);
                        RuntimeLog.Info("UI", $"Trim END marker dragged to {TimeSpan.FromMilliseconds(_trimEndMs):hh\\:mm\\:ss\\.ff}.");

                        if (ActiveVideoHost?.IpcClient?.IsPaused == true)
                        {
                            _ = SeekInternal(_trimEndMs / 1000.0);
                        }

                        var markEndBtn = this.FindControl<Avalonia.Controls.Button>("MarkEndButton");
                        if (markEndBtn != null) markEndBtn.Content = "END: " + FormatTime(TimeSpan.FromMilliseconds(_trimEndMs));
                        PlayUiSound();
                        ShowTacticalFeedback("🏁 " + TimeSpan.FromMilliseconds(_trimEndMs).ToString("mm\\:ss\\.ff"));
                        ShowTimelineGlow(_trimEndMs, Avalonia.Media.Brushes.SeaGreen);
                        UpdateTimelineMarkers();
                        UpdateEstimatedQuality(); 
                        SaveRecoveryState(); 
                        UpdateDraggingVisuals(canvasWidth, duration);
                    }
                };
            }

            if (_musicWizardResult != null && !string.IsNullOrEmpty(_musicWizardResult.MusicFilePath))
            {
                double mStartX = (_musicWizardResult.TimelineStartSeconds / duration) * canvasWidth;
                double mEndX = (_musicWizardResult.TimelineEndSeconds / duration) * canvasWidth;

                var musicRect = new Avalonia.Controls.Shapes.Rectangle
                {
                    Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(80, 255, 105, 180)),
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

                var removeMusicMenu = new Avalonia.Controls.ContextMenu();
                var removeMusicItem = new Avalonia.Controls.MenuItem { Header = "Remove Music", Icon = new TextBlock { Text = "🗑️", Margin = new Avalonia.Thickness(0,0,5,0) } };
                removeMusicItem.Click += (s, ev) => {
                    _musicWizardResult = null;
                    SetMusicButtonActive(false);
                    UpdateTimelineMarkers();
                };
                removeMusicMenu.ItemsSource = new[] { removeMusicItem };
                musicRect.ContextMenu = removeMusicMenu;

                musicRect.KeyDown += (s, e) => {
                    if (e.Key == Avalonia.Input.Key.Delete) {
                        _musicWizardResult = null;
                        SetMusicButtonActive(false);
                        UpdateTimelineMarkers();
                    }
                };

                Avalonia.Controls.Canvas.SetLeft(musicRect, mStartX);
                Avalonia.Controls.Canvas.SetTop(musicRect, trimMarkerTop);
                if (bottomCanvas != null) bottomCanvas.Children.Add(musicRect);
                else canvas.Children.Add(musicRect);

                var startNoteText = new TextBlock { Text = "♪", FontFamily = new Avalonia.Media.FontFamily("Segoe UI Symbol"), Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 105, 180)), FontSize = Infrastructure.ThemeManager.ScaledFontSize(52), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Width = 52, TextAlignment = Avalonia.Media.TextAlignment.Center, Effect = new Avalonia.Media.DropShadowDirectionEffect { Color = Avalonia.Media.Colors.Black, BlurRadius = 4, Opacity = 0.8 }, IsHitTestVisible = false };
                var startStick = new Avalonia.Controls.Shapes.Rectangle { Width = 4, Height = 40, Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 105, 180)), IsHitTestVisible = false };
                
                var startHitBox = new Avalonia.Controls.Border {
                    Width = 52,
                    Height = 41,
                    Background = Avalonia.Media.Brushes.Transparent,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast)
                };

                var startCanvas = new Avalonia.Controls.Canvas { Width = 52, Height = 80, ClipToBounds = false };
                Avalonia.Controls.Canvas.SetLeft(startNoteText, 0);
                Avalonia.Controls.Canvas.SetTop(startStick, 58);
                Avalonia.Controls.Canvas.SetLeft(startStick, 24);
                Avalonia.Controls.Canvas.SetTop(startHitBox, 14);
                Avalonia.Controls.Canvas.SetLeft(startHitBox, 0);

                startCanvas.Children.Add(startNoteText);
                startCanvas.Children.Add(startStick);
                startCanvas.Children.Add(startHitBox);

                var musicStartBorder = new Avalonia.Controls.Border {
                    Width = 52,
                    Height = 80,
                    Child = startCanvas
                };
                
                startHitBox.PointerEntered += (s, e) => {
                    startNoteText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 180, 0));
                    startNoteText.Effect = new Avalonia.Media.DropShadowDirectionEffect { Color = Avalonia.Media.Color.FromArgb(255, 255, 180, 0), BlurRadius = 15, Opacity = 0.9 };
                };
                startHitBox.PointerExited += (s, e) => {
                    startNoteText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 105, 180));
                    startNoteText.Effect = new Avalonia.Media.DropShadowDirectionEffect { Color = Avalonia.Media.Colors.Black, BlurRadius = 4, Opacity = 0.8 };
                };

                _musicStartPopupRef = musicStartBorder;
                Avalonia.Controls.Canvas.SetTop(musicStartBorder, -72);
                Avalonia.Controls.Canvas.SetLeft(musicStartBorder, mStartX - 26);
                musicStartBorder.ZIndex = 100;
                canvas.Children.Add(musicStartBorder);

                var endNoteText = new TextBlock { Text = "♪", FontFamily = new Avalonia.Media.FontFamily("Segoe UI Symbol"), Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 105, 180)), FontSize = Infrastructure.ThemeManager.ScaledFontSize(52), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Width = 52, TextAlignment = Avalonia.Media.TextAlignment.Center, Effect = new Avalonia.Media.DropShadowDirectionEffect { Color = Avalonia.Media.Colors.Black, BlurRadius = 4, Opacity = 0.8 }, IsHitTestVisible = false };
                var endStick = new Avalonia.Controls.Shapes.Rectangle { Width = 4, Height = 40, Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 105, 180)), IsHitTestVisible = false };
                
                var endHitBox = new Avalonia.Controls.Border {
                    Width = 52,
                    Height = 41,
                    Background = Avalonia.Media.Brushes.Transparent,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast)
                };

                var endCanvas = new Avalonia.Controls.Canvas { Width = 52, Height = 80, ClipToBounds = false };
                Avalonia.Controls.Canvas.SetLeft(endNoteText, 0);
                Avalonia.Controls.Canvas.SetTop(endStick, 58);
                Avalonia.Controls.Canvas.SetLeft(endStick, 24);
                Avalonia.Controls.Canvas.SetTop(endHitBox, 14);
                Avalonia.Controls.Canvas.SetLeft(endHitBox, 0);

                endCanvas.Children.Add(endNoteText);
                endCanvas.Children.Add(endStick);
                endCanvas.Children.Add(endHitBox);

                var musicEndBorder = new Avalonia.Controls.Border {
                    Width = 52,
                    Height = 80,
                    Child = endCanvas
                };

                endHitBox.PointerEntered += (s, e) => {
                    endNoteText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 180, 0));
                    endNoteText.Effect = new Avalonia.Media.DropShadowDirectionEffect { Color = Avalonia.Media.Color.FromArgb(255, 255, 180, 0), BlurRadius = 15, Opacity = 0.9 };
                };
                endHitBox.PointerExited += (s, e) => {
                    endNoteText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 105, 180));
                    endNoteText.Effect = new Avalonia.Media.DropShadowDirectionEffect { Color = Avalonia.Media.Colors.Black, BlurRadius = 4, Opacity = 0.8 };
                };

                _musicEndPopupRef = musicEndBorder;
                Avalonia.Controls.Canvas.SetTop(musicEndBorder, -72);
                Avalonia.Controls.Canvas.SetLeft(musicEndBorder, mEndX - 26);
                musicEndBorder.ZIndex = 100;
                canvas.Children.Add(musicEndBorder);

                double dragStartPointerX = 0;
                double dragInitialStartSec = 0;
                double dragInitialEndSec = 0;

                startHitBox.PointerPressed += (s, e) => {
                    _isMusicBlockFocused = false;
                    _draggingMusicStart = true;
                    e.Pointer.Capture(startHitBox);
                    e.Handled = true;
                };
                startHitBox.PointerReleased += (s, e) => {
                    _draggingMusicStart = false;
                    e.Pointer.Capture(null);
                    UpdateTimelineMarkers();
                    SaveRecoveryState();
                };
                startHitBox.PointerMoved += (s, e) => {
                    if (_draggingMusicStart) {
                        double currentX = e.GetPosition(canvas).X;

                        double markStartX = (_trimStartMs / 1000.0 / duration) * canvasWidth;
                        if (currentX < markStartX) currentX = markStartX;
                        if (Math.Abs(currentX - markStartX) < 10) currentX = markStartX;

                        double newStart = (currentX / canvasWidth) * duration;
                        if (newStart < 0) newStart = 0;
                        if (newStart >= _musicWizardResult.TimelineEndSeconds - 0.5) newStart = _musicWizardResult.TimelineEndSeconds - 0.5;
                        _musicWizardResult.TimelineStartSeconds = newStart;
                        double nx = (newStart / duration) * canvasWidth;
                        Avalonia.Controls.Canvas.SetLeft(musicRect, nx);
                        Avalonia.Controls.Canvas.SetLeft(musicStartBorder, nx - 26);
                        musicRect.Width = Math.Max(2, ((_musicWizardResult.TimelineEndSeconds / duration) * canvasWidth) - nx);
                    }
                };

                endHitBox.PointerPressed += (s, e) => {
                    _isMusicBlockFocused = false;
                    _draggingMusicEnd = true;
                    e.Pointer.Capture(endHitBox);
                    e.Handled = true;
                };
                endHitBox.PointerReleased += (s, e) => {
                    _draggingMusicEnd = false;
                    e.Pointer.Capture(null);
                    UpdateTimelineMarkers();
                    SaveRecoveryState();
                };
                endHitBox.PointerMoved += (s, e) => {
                    if (_draggingMusicEnd) {
                        double currentX = e.GetPosition(canvas).X;

                        double markEndX = (_trimEndMs / 1000.0 / duration) * canvasWidth;
                        if (currentX > markEndX) currentX = markEndX;
                        if (Math.Abs(currentX - markEndX) < 10) currentX = markEndX;

                        double newEnd = (currentX / canvasWidth) * duration;
                        if (newEnd > duration) newEnd = duration;
                        if (newEnd <= _musicWizardResult.TimelineStartSeconds + 0.5) newEnd = _musicWizardResult.TimelineStartSeconds + 0.5;
                        _musicWizardResult.TimelineEndSeconds = newEnd;
                        double nx = (newEnd / duration) * canvasWidth;
                        Avalonia.Controls.Canvas.SetLeft(musicEndBorder, nx - 26);
                        musicRect.Width = Math.Max(2, nx - ((_musicWizardResult.TimelineStartSeconds / duration) * canvasWidth));
                    }
                };

                musicRect.PointerPressed += (s, e) => {
                    if (e.GetCurrentPoint(canvas).Properties.IsRightButtonPressed) return;

                    if (!_isMusicBlockFocused)
                    {
                        _isMusicBlockFocused = true;
                        musicRect.Stroke = Avalonia.Media.Brushes.Yellow;
                        musicRect.StrokeThickness = 1;
                        musicRect.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(2, 2);
                    }
                    if (e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed)
                    {
                        _draggingMusicBlock = true;
                        dragStartPointerX = e.GetPosition(canvas).X;
                        dragInitialStartSec = _musicWizardResult.TimelineStartSeconds;
                        dragInitialEndSec = _musicWizardResult.TimelineEndSeconds;
                        e.Pointer.Capture(musicRect);
                        e.Handled = true;
                    }
                };
                musicRect.PointerReleased += (s, e) => {
                    _draggingMusicBlock = false;
                    e.Pointer.Capture(null);
                    UpdateTimelineMarkers();
                    SaveRecoveryState();
                };
                musicRect.PointerMoved += (s, e) => {
                    if (_draggingMusicBlock) {
                        double currentX = e.GetPosition(canvas).X;
                        double dxSeconds = ((currentX - dragStartPointerX) / canvasWidth) * duration;
                        double dur = dragInitialEndSec - dragInitialStartSec;
                        double rawNewStart = dragInitialStartSec + dxSeconds;
                        double rawNewEnd = dragInitialEndSec + dxSeconds;

                        double markStartSec = _trimStartMs / 1000.0;
                        double markEndSec = _trimEndMs / 1000.0;

                        if (rawNewStart < markStartSec) {
                            rawNewStart = markStartSec;
                            rawNewEnd = rawNewStart + dur;
                        }
                        if (rawNewEnd > markEndSec) {
                            rawNewEnd = markEndSec;
                            rawNewStart = rawNewEnd - dur;
                        }

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

    public static Control CreateZoomTimelineCameraIcon()
    {
        return CreateZoomTimelineCameraIcon(false, 0, out _, out _);
    }

    public static Control CreateZoomTimelineCameraIcon(
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
            BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#d946ef")),
            BorderThickness = new Avalonia.Thickness(2),
            CornerRadius = new Avalonia.CornerRadius(4),
            IsHitTestVisible = false
        };

        var hoverIconGlow = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = "TimelineCameraIconGlow",
            Width = 44,
            Height = 36,
            Stroke = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#d946ef")),
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
            Stroke = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#d946ef")),
            StrokeThickness = 2,
            Opacity = 0,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(hoverLineGlow, 20);
        Avalonia.Controls.Canvas.SetTop(hoverLineGlow, 53);

        var txt = new Avalonia.Controls.TextBlock
        {
            Text = "🔍",
            FontSize = 14,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#d946ef")),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        icon.Child = txt;

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
            Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#d946ef")),
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
            : (ActiveVideoHost?.IpcClient?.Duration ?? 0.0);

        if (videoEndSec <= videoStartSec)
            videoEndSec = videoStartSec + Math.Max(1.0, ActiveVideoHost?.IpcClient?.Duration ?? 1.0);

        if (result.TimelineEndSeconds <= result.TimelineStartSeconds)
        {
            result.TimelineStartSeconds = videoStartSec;
            result.TimelineEndSeconds = videoEndSec;
        }

        result.TimelineStartSeconds = Math.Clamp(result.TimelineStartSeconds, 0, Math.Max(0, videoEndSec - 0.5));
        result.TimelineEndSeconds = Math.Clamp(result.TimelineEndSeconds, result.TimelineStartSeconds + 0.5, videoEndSec);
    }

    private async void StartMusicPreview(double currentVideoTimeSec)
    {
        if (_musicWizardResult == null || string.IsNullOrEmpty(_musicWizardResult.MusicFilePath)) return;

        double offsetFromMusicStart = _musicWizardResult.OffsetSeconds + (currentVideoTimeSec - _musicWizardResult.TimelineStartSeconds);
        if (offsetFromMusicStart < 0) offsetFromMusicStart = 0;
        if (_musicWizardResult.MusicDurationSeconds > 0 && offsetFromMusicStart >= _musicWizardResult.MusicDurationSeconds)
            return;

        string mpvExe = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? System.AppContext.BaseDirectory, "binaries", "mpv.exe");
        if (!System.IO.File.Exists(mpvExe)) mpvExe = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? System.AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "mpv.exe");
        if (!System.IO.File.Exists(mpvExe)) mpvExe = "mpv.exe";

        if (_musicPreviewIpcClient == null)
        {
            _musicPreviewIpcClient = new FortniteVideoSoftware.Core.Media.MpvIpcClient();
            await _musicPreviewIpcClient.StartAudioOnlyAsync(mpvExe);
        }

        var volSlider = this.FindControl<Avalonia.Controls.Slider>("VolumeSlider");
        double masterVol = volSlider?.Value ?? 100.0;
        double effectiveMusicVol = masterVol * _musicWizardResult.MusicVolume;

        await _musicPreviewIpcClient.SetPropertyAsync("volume", ((int)effectiveMusicVol).ToString(System.Globalization.CultureInfo.InvariantCulture));
        await _musicPreviewIpcClient.LoadFileAsync(_musicWizardResult.MusicFilePath, offsetFromMusicStart);
        await _musicPreviewIpcClient.SetPropertyAsync("pause", "no");

        _isMusicPreviewPlaying = true;
        _lastMusicPreviewSyncTime = currentVideoTimeSec;
        _playingMusicTimelineStartSeconds = _musicWizardResult.TimelineStartSeconds;
    }

    private async void StopMusicPreview()
    {
        if (_musicPreviewIpcClient != null)
        {
            await _musicPreviewIpcClient.SetPropertyAsync("pause", "yes");
            await _musicPreviewIpcClient.SendCommandAsync("stop");
        }
        _isMusicPreviewPlaying = false;
    }

    private void ApplyMasterVolume(int masterVolumePercentage)
    {
        FortniteVideoSoftware.Core.Media.MpvIpcClient.SetGlobalMasterVolume(masterVolumePercentage);
    }

    private void OnGlobalMasterVolumeChanged(int masterVolumePercentage)
    {
        double videoBase = 1.0;
        double musicBase = 1.0;

        if (_musicWizardResult != null && _isMusicActive)
        {
            videoBase = _musicWizardResult.VideoVolume;
            musicBase = _musicWizardResult.MusicVolume;
        }

        double effectiveVideoVol = masterVolumePercentage * videoBase;
        double effectiveMusicVol = masterVolumePercentage * musicBase;

        if (ActiveVideoHost?.IpcClient != null)
        {
            _ = ActiveVideoHost.IpcClient.SetPropertyAsync("volume", ((int)effectiveVideoVol).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (_musicPreviewIpcClient != null)
        {
            _ = _musicPreviewIpcClient.SetPropertyAsync("volume", ((int)effectiveMusicVol).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private void EnsureTrimPointsSet()
    {
        if (!_trimStartSet && ActiveVideoHost?.IpcClient != null)
        {
            double dur = ActiveVideoHost.IpcClient.Duration;
            if (dur > 0)
            {
                _trimStartSet = true;
                _trimEndSet = true;
                _trimStartMs = 0;
                _trimEndMs = dur * 1000.0;
                var markStartBtn = this.FindControl<Button>("MarkStartButton");
                if (markStartBtn != null) markStartBtn.Content = "MARK START [" + FormatTime(TimeSpan.Zero) + "]";
                var markEndBtn = this.FindControl<Button>("MarkEndButton");
                if (markEndBtn != null) markEndBtn.Content = $"MARK END [{FormatTime(TimeSpan.FromSeconds(dur))}]";
                UpdateTimelineMarkers();
                SaveRecoveryState();
            }
        }
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (ActiveVideoHost?.IpcClient == null) return;

        var canvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineMarkersCanvas");
        if (canvas != null && canvas.Children.Count == 0)
        {
            UpdateTimelineMarkers();
        }

        var playIcon = this.FindControl<Avalonia.Controls.Shapes.Polygon>("PlayIcon");
        var pauseIcon = this.FindControl<StackPanel>("PauseIcon");
        if (playIcon != null && pauseIcon != null)
        {
            bool isPaused = ActiveVideoHost.IpcClient.IsPaused;
            if (_isCurrentlyFrozen) isPaused = false;
            playIcon.IsVisible = isPaused;
            pauseIcon.IsVisible = !isPaused;
        }

        double time = ActiveVideoHost.IpcClient.CurrentTime;
        double dur = ActiveVideoHost.IpcClient.Duration;
        double displayTime = dur > 0 ? Math.Clamp(time, 0, dur) : Math.Max(0, time);

        if (_musicWizardResult != null && !string.IsNullOrEmpty(_musicWizardResult.MusicFilePath))
        {
            bool isPaused = ActiveVideoHost.IpcClient.IsPaused;
            if (_isCurrentlyFrozen) isPaused = false;
            double songTime = _musicWizardResult.OffsetSeconds + (time - _musicWizardResult.TimelineStartSeconds);
            bool songHasAudio = _musicWizardResult.MusicDurationSeconds <= 0 || songTime < _musicWizardResult.MusicDurationSeconds;
            bool shouldPlayMusic = !isPaused && time >= _musicWizardResult.TimelineStartSeconds && time <= _musicWizardResult.TimelineEndSeconds && songHasAudio;
            bool isDraggingAnyMarker = _draggingStartMarker || _draggingEndMarker || _draggingMusicStart || _draggingMusicEnd || _draggingMusicBlock;

            if (shouldPlayMusic && _isMusicPreviewPlaying && (Math.Abs(time - _lastMusicPreviewSyncTime) > 0.5 || Math.Abs(_musicWizardResult.TimelineStartSeconds - _playingMusicTimelineStartSeconds) > 0.05))
            {
                StopMusicPreview();
            }

            if (shouldPlayMusic && !_isMusicPreviewPlaying && !isDraggingAnyMarker)
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

        if (_voiceOverResult != null && _voiceOverPreviewTakes.Count > 0)
        {
            bool isPaused = ActiveVideoHost.IpcClient.IsPaused;
            if (_isCurrentlyFrozen) isPaused = false;

            double editedTime = GetEditedPreviewTimeSeconds(time);
            foreach (var take in _voiceOverPreviewTakes)
            {
                double voiceTime = editedTime - take.StartProjectSec;
                bool shouldPlayVoice = !isPaused && voiceTime >= 0 && voiceTime <= take.Reader.TotalTime.TotalSeconds;

                if (shouldPlayVoice && take.Player.PlaybackState != NAudio.Wave.PlaybackState.Playing)
                {
                    take.Reader.CurrentTime = TimeSpan.FromSeconds(voiceTime);
                    take.Player.Play();
                }
                else if (!shouldPlayVoice && take.Player.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                {
                    take.Player.Pause();
                }
                else if (shouldPlayVoice && take.Player.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                {
                    if (Math.Abs(take.Reader.CurrentTime.TotalSeconds - voiceTime) > 0.5)
                    {
                        take.Reader.CurrentTime = TimeSpan.FromSeconds(voiceTime);
                    }
                }
            }
        }

        double currentAbsMs = time * 1000.0;

        if (_freezeTimeMs >= 0 && !_isCurrentlyFrozen && !ActiveVideoHost.IpcClient.IsPaused)
        {
            if (currentAbsMs >= _freezeTimeMs && currentAbsMs <= _freezeTimeMs + 150)
            {
                _isCurrentlyFrozen = true;
                _freezeStartTime = DateTime.UtcNow;
                _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
                return;
            }
        }
        else if (_isCurrentlyFrozen)
        {
            if ((DateTime.UtcNow - _freezeStartTime).TotalSeconds >= _freezeDurationS)
            {
                _isCurrentlyFrozen = false;
                _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "no");
            }
            else
            {
                return;
            }
        }

        if (!ActiveVideoHost.IpcClient.IsPaused && _speedSegments.Count > 0)
        {
            double targetSpeed = GetSpeedForPosition(currentAbsMs);
            if (Math.Abs(targetSpeed - _lastAppliedSpeed) > 0.001)
            {
                _lastAppliedSpeed = targetSpeed;
                _ = ActiveVideoHost.IpcClient.SetPropertyAsync("speed",
                    targetSpeed.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        var timeElapsed = this.FindControl<TextBlock>("TimeElapsed");
        if (timeElapsed != null) timeElapsed.Text = FormatTime(TimeSpan.FromSeconds(displayTime));

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
            if (timeRemaining != null) timeRemaining.Text = "-" + FormatTime(TimeSpan.FromSeconds(Math.Max(0, dur - displayTime)));
        }

        if (ActiveVideoHost.IpcClient.IsEof)
        {
            _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
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
                    
                    bool isRdpBlocked = CheckRdpGpuBlocked();
                    var rdpWarningBtn = this.FindControl<Avalonia.Controls.Button>("RdpWarningButton");
                    var rdpOverlay = this.FindControl<Avalonia.Controls.TextBlock>("RdpWarningOverlay");
                    if (rdpWarningBtn != null) rdpWarningBtn.IsVisible = isRdpBlocked;
                    if (rdpOverlay != null) rdpOverlay.IsVisible = isRdpBlocked;

                    if (isRdpBlocked)
                    {
                        RuntimeLog.Fail("Hardware", "RDP Session detected with blocked GPU.");
                        hwLabel.Text = "RDP: CPU BLOCKED";
                        hwLabel.Foreground = Avalonia.Media.Brushes.Red;
                    }
                    else if (mode == "CPU")
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private bool CheckRdpGpuBlocked()
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            return false;

        bool isRdp = false;
        try { isRdp = GetSystemMetrics(0x1000) != 0; } catch { }
        if (!isRdp)
        {
            var clientName = Environment.GetEnvironmentVariable("CLIENTNAME");
            if (!string.IsNullOrEmpty(clientName) && !clientName.Equals("Console", StringComparison.OrdinalIgnoreCase))
                isRdp = true;
        }

        if (!isRdp) return false;

        try
        {
            var val = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services", "fEnableWddmDriver", null);
            if (val == null || (val is int i && i == 0))
            {
                return true;
            }
        }
        catch { return true; }
        return false;
    }

    private void RdpFixButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"$path = 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows NT\\Terminal Services'; if (!(Test-Path $path)) { New-Item -Path $path -Force }; Set-ItemProperty -Path $path -Name 'fEnableWddmDriver' -Value 1 -Type DWord; Set-ItemProperty -Path $path -Name 'fEnableAVC444ModeOnHWEncoder' -Value 1 -Type DWord;\"",
                UseShellExecute = true,
                Verb = "runas"
            };
            System.Diagnostics.Process.Start(psi)?.WaitForExit();
            
            var btn = this.FindControl<Avalonia.Controls.Button>("RdpFixButton");
            if (btn != null)
            {
                btn.Content = "Fix Applied! PLEASE RESTART YOUR COMPUTER.";
                btn.IsEnabled = false;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            RuntimeLog.Fail("RDP", "UAC prompt was denied by the user.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("RDP", $"Failed to apply registry fix: {ex.Message}");
        }
    }

    private double GetCurrentMpvTime() { return ActiveVideoHost?.IpcClient?.CurrentTime ?? 0.0; }

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
                return seg.Speed;
            }
        }
        return _baseSpeed;
    }

    private void GlobalKeyUpHandler(object? sender, KeyEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox or NumericUpDown)
            return;

        var kb = SettingsManager.Instance.KeyBinds;
        var playPause = new Avalonia.Input.KeyGesture(kb.PlayPause);
        var markStart = new Avalonia.Input.KeyGesture(kb.MarkStart);
        var markEnd = new Avalonia.Input.KeyGesture(kb.MarkEnd);

        if (playPause.Matches(e) || markStart.Matches(e) || markEnd.Matches(e) || e.Key is Key.Space or Key.Left or Key.Right)
        {
            e.Handled = true;
        }
    }

    private void GlobalKeyDownHandler(object? sender, KeyEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox or NumericUpDown)
        {
            return;
        }

        if (_isThumbnailMarkerSelected && _thumbnailSet && e.Key is Key.Left or Key.Right)
        {
            int frames = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
            MoveThumbnailMarkerByFrames(e.Key == Key.Left ? -frames : frames);
            e.Handled = true;
            return;
        }

        var kb = SettingsManager.Instance.KeyBinds;

        if (e.Key == Key.Delete)
        {
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
            if (ActiveVideoHost?.IpcClient != null)
            {
                bool isPaused = ActiveVideoHost.IpcClient.IsPaused;
                _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", isPaused ? "no" : "yes");
                e.Handled = true;
            }
        }
        else if (_isMusicBlockFocused && _musicWizardResult != null && (e.Key == Key.Left || e.Key == Key.Right))
        {
            double duration = ActiveVideoHost?.IpcClient?.Duration ?? 0;
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
            _ = ActiveVideoHost?.IpcClient?.SendCommandAsync("frame-step");
            e.Handled = true;
        }
        else if (fineSeekBackCtrl.Matches(e) || fineSeekBackShift.Matches(e))
        {
            _ = ActiveVideoHost?.IpcClient?.SendCommandAsync("frame-back-step");
            e.Handled = true;
        }
        else if (seekFwd.Matches(e))
        {
            _ = ActiveVideoHost?.IpcClient?.SendCommandAsync("seek", 5);
            e.Handled = true;
        }
        else if (seekBack.Matches(e))
        {
            _ = ActiveVideoHost?.IpcClient?.SendCommandAsync("seek", -5);
            e.Handled = true;
        }
        else if (aggVolUpCtrl.Matches(e))
        {
            _ = ActiveVideoHost?.IpcClient?.SendCommandAsync("add", "volume", 10);
            e.Handled = true;
        }
        else if (aggVolDownCtrl.Matches(e))
        {
            _ = ActiveVideoHost?.IpcClient?.SendCommandAsync("add", "volume", -10);
            e.Handled = true;
        }
        else if (volUp.Matches(e))
        {
            _ = ActiveVideoHost?.IpcClient?.SendCommandAsync("add", "volume", 2);
            e.Handled = true;
        }
        else if (volDown.Matches(e))
        {
            _ = ActiveVideoHost?.IpcClient?.SendCommandAsync("add", "volume", -2);
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
        if (ActiveVideoHost?.IpcClient != null)
        {
            _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
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

            if (ActiveVideoHost != null) ActiveVideoHost.IsVisible = false;
            this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer")?.StartOverlay();

            var markersCanvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineMarkersCanvas");
            if (markersCanvas != null)
            {
                foreach (var child in markersCanvas.Children)
                {
                    if (child is Avalonia.Controls.Primitives.Popup popup)
                        popup.IsOpen = false;
                }
            }

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
                    if (_processCts != null && _processCts.IsCancellationRequested && !success)
                    {
                        RuntimeLog.Info("Process", "Worker cleaned up after cancellation.");
                        worker.Dispose();
                        return;
                    }

                    this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer")?.StopOverlay();
                    if (ActiveVideoHost != null) ActiveVideoHost.IsVisible = true;
                    if (success)
                    {
                        RuntimeLog.Success("Process", $"Video processing completed successfully. Saved to: {Path.GetFileName(message)}");
                        RuntimeLog.Debug("Process", $"Video processing output path: {message}");
                        ShowTacticalFeedback("Processing complete");
                        PlayUiSound();
                        var dlg = new FortniteVideoSoftware.App.Controls.FinishedDialogWindow();
                        dlg.SetOutputPath(message);
                        await dlg.ShowDialog(this);
                        if (dlg.DialogResult == 1)
                        {
                            Close();
                        }
                        else if (dlg.DialogResult == 2)
                        {
                            ResetProjectStateToUpload();
                            OnUploadVideoClicked(null, new Avalonia.Interactivity.RoutedEventArgs());
                        }
                    }
                    else
                    {
                        string safeMessage = message.Contains(@":\", StringComparison.Ordinal) || message.Contains(":/", StringComparison.Ordinal)
                            ? "details are available in debug log"
                            : message;
                        RuntimeLog.Fail("Process", $"Video processing failed: {safeMessage}");
                        RuntimeLog.Debug("Process", $"Video processing failure detail: {message}");
                        ShowTacticalFeedback("Processing failed");
                        PlayUiSound();
                    }
                    processButton.IsEnabled = true;
                    processButton.Content = "PROCESS";
                    worker.Dispose();
                });
            };

            string hwMode = _hardwareMode;

            worker.InputPath = _loadedVideoPath;
            worker.StartTimeMs = _trimStartMs;

            double duration = GetCurrentMpvTime();
            duration = ActiveVideoHost?.IpcClient?.Duration ?? 0.0;

            worker.EndTimeMs = _trimEndMs > 0 ? _trimEndMs : duration * 1000;
            var allSegments = BuildExportSpeedSegments();
            worker.SpeedSegments = allSegments;
            worker.SpeedFactor = _baseSpeed;
            worker.ThumbnailPosMs = _thumbnailSet ? _thumbnailPosMs : 0;
            worker.AutoVoiceNormalization = SettingsManager.Instance.Defaults.AutoVoiceNormalization;
            worker.AutoSpikeFlattening = SettingsManager.Instance.Defaults.AutoSpikeFlattening;
            if (_voiceOverResult != null)
            {
                worker.VoiceOverWavPath = _voiceOverResult.VoiceOverWavPath;
                worker.VoiceOverStartSec = _voiceOverResult.VoiceOverStartTimestampSec;
                worker.VoiceOverTakes = GetExistingVoiceOverTakes(_voiceOverResult);
                worker.VoiceOverMuteMale = _voiceOverResult.MuteMale;
                worker.VoiceOverMuteFemale = _voiceOverResult.MuteFemale;
                worker.VoiceOverMuteChild = _voiceOverResult.MuteChild;
                worker.VoiceOverMuteMaleHz = _voiceOverResult.MaleFrequencyHz;
                worker.VoiceOverMuteFemaleHz = _voiceOverResult.FemaleFrequencyHz;
                worker.VoiceOverMuteChildHz = _voiceOverResult.ChildFrequencyHz;
            }
            if (_thumbnailSet && _thumbnailPosMs > 0)
            {
                worker.IntroAbsTimeMs = _thumbnailPosMs;
                worker.IntroStillSec = 0.1;
            }
            else
            {
                double tStart = _trimStartMs > 0 ? _trimStartMs : 0;
                double tEnd = _trimEndMs > 0 ? _trimEndMs : duration * 1000.0;
                worker.IntroAbsTimeMs = tStart + (tEnd - tStart) * (2.0 / 3.0);
                worker.IntroStillSec = 0.1;
            }
            
            worker.HardwareStrategy = hwMode;
            worker.IsMobileFormat = this.FindControl<ToggleSwitch>("PortraitModeCheckbox")?.IsChecked ?? true;
            worker.IsBossHp = this.FindControl<ToggleSwitch>("BossHpCheckbox")?.IsChecked ?? false;
            worker.EnableFades = this.FindControl<ToggleSwitch>("EnableFadeCheckbox")?.IsChecked ?? true;
            worker.ShowTeammates = this.FindControl<ToggleSwitch>("TeammatesCheckbox")?.IsChecked ?? false;
            worker.ShowSpectating = this.FindControl<ToggleSwitch>("SpectatingCheckbox")?.IsChecked ?? false;

            var addMemeCb = this.FindControl<ToggleSwitch>("AddMemeCheckbox");
            if (addMemeCb != null && addMemeCb.IsChecked == true)
            {
                var memeCb = this.FindControl<ComboBox>("MemeComboBox");
                if (memeCb?.SelectedItem is MemeItem memeSel && !memeSel.IsDownloadAction && File.Exists(memeSel.FullPath))
                {
                    worker.MemeFile = memeSel.FullPath;
                }
            }

            worker.PortraitText = this.FindControl<TextBox>("PortraitTextInput")?.Text;

            int qualityIdx = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("QualitySlider")?.Value ?? 7;
            worker.QualityLevel = qualityIdx;
            worker.TargetMbOverride = qualityIdx >= 20 ? null : (double)(5 + qualityIdx * 5);

            if (_musicWizardResult != null && !string.IsNullOrEmpty(_musicWizardResult.MusicFilePath) && File.Exists(_musicWizardResult.MusicFilePath))
            {
                double musicStartMs = Math.Max(worker.StartTimeMs, _musicWizardResult.TimelineStartSeconds * 1000.0);
                double musicEndMs = Math.Min(worker.EndTimeMs, _musicWizardResult.TimelineEndSeconds * 1000.0);
                double startDelay = CalculateEffectiveDurationMs(worker.StartTimeMs, musicStartMs, _baseSpeed, allSegments) / 1000.0;
                double outputEndSec = CalculateEffectiveDurationMs(worker.StartTimeMs, musicEndMs, _baseSpeed, allSegments) / 1000.0;
                double totalOutputDurationSec = CalculateEffectiveDurationMs(worker.StartTimeMs, worker.EndTimeMs, _baseSpeed, allSegments) / 1000.0;
                double dur = outputEndSec - startDelay;
                if (dur <= 0) dur = 1.0;

                bool applyFadeOut = Math.Abs(outputEndSec - totalOutputDurationSec) < 0.05;

                worker.MusicTracks = new System.Collections.Generic.List<MusicTrack>();
                var musicPaths = (_musicWizardResult.MusicFilePaths.Count > 0
                    ? _musicWizardResult.MusicFilePaths
                    : new System.Collections.Generic.List<string> { _musicWizardResult.MusicFilePath })
                    .Where(p => !string.IsNullOrWhiteSpace(p) && System.IO.File.Exists(p)).ToList();

                for (int i = 0; i < musicPaths.Count; i++)
                {
                    double offset = i == 0 ? _musicWizardResult.OffsetSeconds : 0.0;
                    double knownDuration = i < _musicWizardResult.MusicDurationsSeconds.Count
                        ? _musicWizardResult.MusicDurationsSeconds[i]
                        : (i == 0 ? _musicWizardResult.MusicDurationSeconds : 0.0);
                    if (knownDuration <= 0)
                    {
                        knownDuration = await ProbeMusicDurationSecondsAsync(musicPaths[i]);
                    }

                    double availableDuration = knownDuration > 0
                        ? Math.Max(0.0, knownDuration - offset)
                        : dur;
                    double takeDuration = Math.Min(dur, availableDuration);
                    if (takeDuration <= 0.01)
                        continue;

                    worker.MusicTracks.Add(new MusicTrack(musicPaths[i], offset, takeDuration, startDelay, applyFadeOut));
                    dur -= takeDuration;
                    if (dur <= 0.01)
                        break;
                }

                worker.MusicConfig = new System.Text.Json.Nodes.JsonObject
                {
                    ["ducking_threshold"] = _musicWizardResult.EnableDucking ? 0.15 : 1.0,
                    ["ducking_ratio"] = _musicWizardResult.EnableDucking ? 2.5 : 1.0,
                    ["main_vol"] = _musicWizardResult.VideoVolume,
                    ["music_vol"] = _musicWizardResult.MusicVolume,
                    ["carving_enabled"] = _musicWizardResult.EnableCarving
                };
            }
            else
            {
                var volSlider = this.FindControl<Avalonia.Controls.Slider>("VolumeSlider");
                if (volSlider != null)
                {
                    worker.MusicConfig = new System.Text.Json.Nodes.JsonObject
                    {
                        ["main_vol"] = volSlider.Value / 100.0
                    };
                }
            }

            if (!string.IsNullOrEmpty(worker.MemeFile) && worker.MusicTracks != null && worker.MusicTracks.Count > 0 && _keepMusicDuringMeme.HasValue)
            {
                worker.KeepMusicDuringMeme = _keepMusicDuringMeme.Value;
            }

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
                mpvPath = "mpv.exe";
            }
            try
            {
                await _videoHost.StartMpvProcessAsync(mpvPath);
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("UI", $"Video preview initialization failed. {ex}");
                ShowTacticalFeedback("Video preview unavailable on this hardware.");
                PlayUiSound();
                return;
            }

            if (_videoHost.IpcClient != null)
            {
                _videoHost.IpcClient.SeekCompleted -= OnSeekCompleted;
                _videoHost.IpcClient.SeekCompleted += OnSeekCompleted;
            }
        }
    }


    private void OnSeekCompleted()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () => {
            _isSeeking = false;
            if (_nextSeekTarget.HasValue) {
                double target = _nextSeekTarget.Value;
                _nextSeekTarget = null;
                await SeekInternal(target);
            }
        });
    }

    private async Task SeekInternal(double time) {
        if (_isSeeking) {
            _nextSeekTarget = time;
            return;
        }
        _isSeeking = true;
        if (ActiveVideoHost?.IpcClient != null) {
            await ActiveVideoHost.IpcClient.SendCommandAsync("seek", time, "absolute");
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

        Environment.Exit(0);
    }

    private bool _isSafeToClose = false;
    private double _lastAppliedSpeed = SpeedPresetButtons.NativeDefaultSpeed;

    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        ForceReleaseDragStates(e.Pointer);
    }

    protected override void OnPointerCaptureLost(Avalonia.Input.PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        ForceReleaseDragStates(e.Pointer);
    }

    private void ForceReleaseDragStates(Avalonia.Input.IPointer pointer)
    {
        bool needsUpdate = false;
        if (_draggingMusicStart || _draggingMusicEnd || _draggingMusicBlock)
        {
            _draggingMusicStart = false;
            _draggingMusicEnd = false;
            _draggingMusicBlock = false;
            needsUpdate = true;
        }
        
        if (needsUpdate)
        {
            try { pointer?.Capture(null); } catch { }
            UpdateTimelineMarkers();
            SaveRecoveryState();
        }
    }

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Avalonia.Input.Key.Delete && _isMusicBlockFocused)
        {
            _musicWizardResult = null;
            SetMusicButtonActive(false);
            UpdateTimelineMarkers();
            e.Handled = true;
        }
    }

    protected override async void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        StopMusicPreview();
        if (_isSafeToClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;

        RuntimeLog.Info("UI", "Closing MainWindow. Saving state and cleaning up asynchronously.");

        try
        {
            await WindowBoundsHelper.SaveBoundsAsync(this, "MainWindowBounds");

            this.Hide();

            _recovery.CleanupLock();

            if (ActiveVideoHost?.IpcClient != null)
            {
                await ActiveVideoHost.IpcClient.SendCommandAsync("stop");
                ActiveVideoHost.IpcClient.Dispose();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UI", $"Error saving state during close: {ex.Message}");
        }
        finally
        {
            _isSafeToClose = true;
            Environment.Exit(0);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged -= OnGlobalMasterVolumeChanged;
        DisposeVoiceOverPreviewTakes();
        _musicPreviewIpcClient?.Dispose();
        Environment.Exit(0);
    }

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<Border>("TitleBarBorder");
        if (titleBar != null)
        {
            titleBar.DoubleTapped += (s, e) =>
            {
                this.WindowState = this.WindowState == Avalonia.Controls.WindowState.Maximized 
                    ? Avalonia.Controls.WindowState.Normal 
                    : Avalonia.Controls.WindowState.Maximized;
                e.Handled = true;
            };
            titleBar.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount < 2)
                {
                    try { BeginMoveDrag(e); } catch { }
                }
            };
        }
    }


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
        var txt = this.FindControl<TextBlock>("AddMusicText");
        
        _isMusicActive = active;
        if (active)
        {
            btn.Classes.Remove("Primary");
            btn.Classes.Add("Danger");
            if (txt != null) txt.Text = " REMOVE MUSIC ";
            ToolTip.SetTip(btn, "Click here to remove the background music you added. This deletes the music track from your video.");
        }
        else
        {
            btn.Classes.Remove("Danger");
            btn.Classes.Add("Primary");
            if (txt != null) txt.Text = " ADD MUSIC ";
            ToolTip.SetTip(btn, "Add background music to the video");
        }
        SaveRecoveryState();
    }


    /// <summary>
    /// Serializes all editing-session state to the crash-recovery file
    /// (recovery_v2.json) so it can be restored if the app crashes.
    /// Called on every meaningful state change (trim, speed, quality, etc.).
    /// </summary>
    private static bool HasVoiceOverWav(VoiceOverWindow.VoiceOverResult? result)
    {
        foreach (var take in GetExistingVoiceOverTakes(result))
        {
            return true;
        }

        return false;
    }

    private static List<VoiceOverTake> GetExistingVoiceOverTakes(VoiceOverWindow.VoiceOverResult? result)
    {
        var takes = new List<VoiceOverTake>();
        if (result == null) return takes;

        if (result.VoiceOverTakes != null)
        {
            foreach (var take in result.VoiceOverTakes)
            {
                if (!string.IsNullOrWhiteSpace(take.Path) && System.IO.File.Exists(take.Path))
                {
                    takes.Add(take);
                }
            }
        }

        if (takes.Count == 0 &&
            !string.IsNullOrWhiteSpace(result.VoiceOverWavPath) &&
            System.IO.File.Exists(result.VoiceOverWavPath))
        {
            takes.Add(new VoiceOverTake(result.VoiceOverWavPath!, result.VoiceOverStartTimestampSec));
        }

        return takes;
    }

    private static bool HasVoiceOverEffect(VoiceOverWindow.VoiceOverResult? result)
    {
        return HasVoiceOverWav(result) ||
               result?.MuteMale == true ||
               result?.MuteFemale == true ||
               result?.MuteChild == true;
    }

    private void DisposeVoiceOverPreviewTakes()
    {
        foreach (var take in _voiceOverPreviewTakes)
        {
            try { take.Player.Dispose(); } catch { }
            try { take.Reader.Dispose(); } catch { }
        }
        _voiceOverPreviewTakes.Clear();
    }

    private Func<double, double> GetVoiceOverPreviewTimeMapper()
    {
        if (_voiceOverPreviewTimeMapper != null)
        {
            return _voiceOverPreviewTimeMapper;
        }

        double durationMs = ActiveVideoHost?.IpcClient?.Duration > 0
            ? ActiveVideoHost.IpcClient.Duration * 1000.0
            : Math.Max(_trimEndMs, _trimStartMs + 1000.0);
        double endMs = _trimEndMs > _trimStartMs ? _trimEndMs : durationMs;
        double totalMs = Math.Max(1.0, endMs - _trimStartMs);

        _voiceOverPreviewTimeMapper = GranularSpeedBuilder.CreateTimeMapper(
            totalMs,
            BuildExportSpeedSegments(),
            _baseSpeed,
            _trimStartMs);

        foreach (var take in _voiceOverPreviewTakes)
        {
            take.StartProjectSec = _voiceOverPreviewTimeMapper(take.Take.StartSec);
        }

        return _voiceOverPreviewTimeMapper;
    }

    private double GetEditedPreviewTimeSeconds(double sourceTimeSec)
    {
        var mapper = GetVoiceOverPreviewTimeMapper();
        if (_isCurrentlyFrozen && _freezeTimeMs >= 0)
        {
            double freezeBaseSec = mapper(_freezeTimeMs / 1000.0);
            double elapsedFreezeSec = Math.Clamp((DateTime.UtcNow - _freezeStartTime).TotalSeconds, 0, Math.Max(0, _freezeDurationS));
            return freezeBaseSec + elapsedFreezeSec;
        }

        return mapper(sourceTimeSec);
    }

    private void ApplyVoiceOverState(VoiceOverWindow.VoiceOverResult? result, bool isRestore = false)
    {
        _voiceOverResult = HasVoiceOverEffect(result) ? result : null;
        var btn = this.FindControl<Button>("VoiceOverButton");
        var text = this.FindControl<TextBlock>("VoiceOverText");

        _voiceOverPlayer?.Dispose();
        _voiceOverPlayer = null;
        _voiceOverReader?.Dispose();
        _voiceOverReader = null;
        DisposeVoiceOverPreviewTakes();
        _voiceOverPreviewTimeMapper = null;

        if (_voiceOverResult != null)
        {
            var takes = GetExistingVoiceOverTakes(_voiceOverResult);
            bool hasWav = takes.Count > 0;
            if (btn != null)
            {
                btn.Classes.Remove("VoiceOverEntry");
                btn.Classes.Remove("Primary");
                if (!btn.Classes.Contains("Danger")) btn.Classes.Add("Danger");
                ToolTip.SetTip(btn, hasWav ? "Remove the current voiceover" : "Remove voice frequency muting");
            }
            if (text != null) text.Text = hasWav ? "REMOVE VOICE" : "VOICE FX ON";

            if (hasWav)
            {
                foreach (var take in takes)
                {
                    try
                    {
                        var reader = new NAudio.Wave.AudioFileReader(take.Path);
                        var player = new NAudio.Wave.WaveOutEvent();
                        player.Init(reader);
                        _voiceOverPreviewTakes.Add(new VoiceOverPreviewTake
                        {
                            Take = take,
                            Reader = reader,
                            Player = player
                        });
                    }
                    catch (Exception ex)
                    {
                        CoreLogger.Fail("MainWindow", $"Failed to load VoiceOver preview take '{Path.GetFileName(take.Path)}': {ex.Message}");
                        CoreLogger.Debug("MainWindow", $"Failed to load VoiceOver preview take path '{take.Path}': {ex}");
                    }
                }
            }
        }
        else
        {
            _voiceOverResult = null;
            if (btn != null)
            {
                btn.Classes.Remove("Danger");
                btn.Classes.Remove("Primary");
                if (!btn.Classes.Contains("VoiceOverEntry")) btn.Classes.Add("VoiceOverEntry");
                ToolTip.SetTip(btn, "Open Voice Over Studio");
            }
            if (text != null) text.Text = "VOICE OVER";
        }

        if (!isRestore) SaveRecoveryState();
    }

    private void InvalidateVoiceOverRecordingForTimingChange()
    {
        if (!HasVoiceOverWav(_voiceOverResult)) return;

        var muteOnly = new VoiceOverWindow.VoiceOverResult
        {
            MuteMale = _voiceOverResult!.MuteMale,
            MuteFemale = _voiceOverResult.MuteFemale,
            MuteChild = _voiceOverResult.MuteChild,
            MaleFrequencyHz = _voiceOverResult.MaleFrequencyHz,
            FemaleFrequencyHz = _voiceOverResult.FemaleFrequencyHz,
            ChildFrequencyHz = _voiceOverResult.ChildFrequencyHz
        };

        ApplyVoiceOverState(HasVoiceOverEffect(muteOnly) ? muteOnly : null);
        ShowTacticalFeedback("Voice Over recording cleared after timing changed");
    }

    private Avalonia.Threading.DispatcherTimer? _recoveryDebounceTimer;

    /// <summary>
    /// Debounced recovery save for high-frequency UI changes (volume drags, typing).
    /// Coalesces rapid changes into a single disk write ~600ms after the last change,
    /// instead of a full serialize + fsync on every value tick (ISSUE_104).
    /// </summary>
    private void ScheduleRecoveryStateSave()
    {
        if (_recoveryDebounceTimer == null)
        {
            _recoveryDebounceTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600)
            };
            _recoveryDebounceTimer.Tick += (s, e) =>
            {
                _recoveryDebounceTimer!.Stop();
                SaveRecoveryState();
            };
        }
        _recoveryDebounceTimer.Stop();
        _recoveryDebounceTimer.Start();
    }

    private void SaveRecoveryState(bool sync = false)
    {
        if (_isRestoring) return;
        try
        {
            bool hasWork = _trimStartSet || _trimEndSet || _thumbnailSet || 
                           _isGranularSpeedActive || _isMusicActive || _voiceOverResult != null ||
                           _speedSegments.Count > 0 || _freezeTimeMs >= 0 || 
                           Math.Abs(_baseSpeed - 1.0) > 0.01 || 
                           !string.IsNullOrWhiteSpace(this.FindControl<TextBox>("PortraitTextInput")?.Text);

            if (!hasWork)
            {
                _recovery.ClearState();
                return;
            }

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
                ["volume"] = this.FindControl<Slider>("VolumeSlider")?.Value ?? 100,
                ["portraitMode"] = this.FindControl<ToggleSwitch>("PortraitModeCheckbox")?.IsChecked ?? true,
                ["bossHp"] = this.FindControl<ToggleSwitch>("BossHpCheckbox")?.IsChecked ?? false,
                ["showTeammates"] = this.FindControl<ToggleSwitch>("TeammatesCheckbox")?.IsChecked ?? false,
                ["showSpectating"] = this.FindControl<ToggleSwitch>("SpectatingCheckbox")?.IsChecked ?? false,
                ["enableFade"] = this.FindControl<ToggleSwitch>("EnableFadeCheckbox")?.IsChecked ?? true,
                ["addMeme"] = this.FindControl<ToggleSwitch>("AddMemeCheckbox")?.IsChecked ?? false,
                ["memeFile"] = this.FindControl<ComboBox>("MemeComboBox")?.SelectedItem?.ToString() ?? "",
                ["memeFilePath"] = (this.FindControl<ComboBox>("MemeComboBox")?.SelectedItem as MemeItem)?.FullPath ?? "",
                ["portraitText"] = this.FindControl<TextBox>("PortraitTextInput")?.Text ?? "",
                ["freezeTimeMs"] = _freezeTimeMs,
                ["freezeDurationS"] = _freezeDurationS
            };

            if (_voiceOverResult != null)
            {
                state["voiceOverWavPath"] = _voiceOverResult.VoiceOverWavPath;
                state["voiceOverStartSec"] = _voiceOverResult.VoiceOverStartTimestampSec;
                state["voiceOverMuteMale"] = _voiceOverResult.MuteMale;
                state["voiceOverMuteFemale"] = _voiceOverResult.MuteFemale;
                state["voiceOverMuteChild"] = _voiceOverResult.MuteChild;
                state["voiceOverMuteMaleHz"] = _voiceOverResult.MaleFrequencyHz;
                state["voiceOverMuteFemaleHz"] = _voiceOverResult.FemaleFrequencyHz;
                state["voiceOverMuteChildHz"] = _voiceOverResult.ChildFrequencyHz;

                var voiceTakeArray = new System.Text.Json.Nodes.JsonArray();
                foreach (var take in GetExistingVoiceOverTakes(_voiceOverResult))
                {
                    voiceTakeArray.Add(new System.Text.Json.Nodes.JsonObject
                    {
                        ["path"] = take.Path,
                        ["startSec"] = take.StartSec
                    });
                }
                state["voiceOverTakes"] = voiceTakeArray;
            }

            var segArray = new System.Text.Json.Nodes.JsonArray();
            foreach (var seg in _speedSegments)
            {
                var segObj = new System.Text.Json.Nodes.JsonObject
                {
                    ["startMs"] = seg.StartMs,
                    ["endMs"] = seg.EndMs,
                    ["speed"] = seg.Speed
                };
                if (seg.ZoomW.HasValue)
                {
                    segObj["zoomX"] = seg.ZoomX;
                    segObj["zoomY"] = seg.ZoomY;
                    segObj["zoomW"] = seg.ZoomW;
                    segObj["zoomH"] = seg.ZoomH;
                    segObj["zoomOrigRes"] = seg.ZoomOrigRes;
                    segObj["zoomSlow"] = seg.ZoomSlow;
                    if (seg.ZoomStartMs.HasValue) segObj["zoomStartMs"] = seg.ZoomStartMs.Value;
                    if (seg.ZoomEndMs.HasValue) segObj["zoomEndMs"] = seg.ZoomEndMs.Value;
                }
                segArray.Add(segObj);
            }
            state["speedSegments"] = segArray;

            if (_musicWizardResult != null)
            {
                var pathsArray = new System.Text.Json.Nodes.JsonArray();
                if (_musicWizardResult.MusicFilePaths != null)
                {
                    foreach (var path in _musicWizardResult.MusicFilePaths)
                    {
                        pathsArray.Add(System.Text.Json.Nodes.JsonValue.Create(path));
                    }
                }
                var durationsArray = new System.Text.Json.Nodes.JsonArray();
                if (_musicWizardResult.MusicDurationsSeconds != null)
                {
                    foreach (var durationSec in _musicWizardResult.MusicDurationsSeconds)
                    {
                        durationsArray.Add(System.Text.Json.Nodes.JsonValue.Create(durationSec));
                    }
                }
                state["musicResult"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["musicFilePath"] = _musicWizardResult.MusicFilePath,
                    ["musicFilePaths"] = pathsArray,
                    ["musicDurationsSeconds"] = durationsArray,
                    ["offsetSeconds"] = _musicWizardResult.OffsetSeconds,
                    ["timelineStartSeconds"] = _musicWizardResult.TimelineStartSeconds,
                    ["timelineEndSeconds"] = _musicWizardResult.TimelineEndSeconds,
                    ["musicDurationSeconds"] = _musicWizardResult.MusicDurationSeconds,
                    ["enableDucking"] = _musicWizardResult.EnableDucking,
                    ["enableCarving"] = _musicWizardResult.EnableCarving,
                    ["videoVolume"] = _musicWizardResult.VideoVolume,
                    ["musicVolume"] = _musicWizardResult.MusicVolume,
                    ["loopMusic"] = _musicWizardResult.LoopMusic
                };
            }

            if (sync)
                _recovery.SaveState(state);
            else
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

            var markStartBtn = this.FindControl<Button>("MarkStartButton");
            if (markStartBtn != null && _trimStartSet)
                markStartBtn.Content = $"START: {FormatTime(TimeSpan.FromMilliseconds(_trimStartMs))}";
            var markEndBtn = this.FindControl<Button>("MarkEndButton");
            if (markEndBtn != null && _trimEndMs > 0)
                markEndBtn.Content = $"END: {FormatTime(TimeSpan.FromMilliseconds(_trimEndMs))}";

            _baseSpeed = state["baseSpeed"]?.GetValue<double>() ?? SpeedPresetButtons.NativeDefaultSpeed;
            var speedSlider = this.FindControl<SpinningWheelSlider>("MainSpeedSlider");
            if (speedSlider != null) speedSlider.Value = (int)Math.Round(_baseSpeed * 10.0, MidpointRounding.AwayFromZero);

            int qualityVal = state["qualitySliderValue"]?.GetValue<int>() ?? 7;
            var qualitySliderRestore = this.FindControl<SpinningWheelSlider>("QualitySlider");
            if (qualitySliderRestore != null) qualitySliderRestore.Value = qualityVal;

            _freezeTimeMs = state["freezeTimeMs"]?.GetValue<double>() ?? -1;
            _freezeDurationS = state["freezeDurationS"]?.GetValue<double>() ?? 1.0;
            if (_freezeTimeMs >= 0)
                RuntimeLog.Info("RECOVERY", $"Crash Recovery Restore: Successfully reinstated Freeze parameters [Timestamp={_freezeTimeMs}ms, Duration={_freezeDurationS}s]");

            _speedSegments.Clear();
            if (state.TryGetPropertyValue("speedSegments", out var speedsNode) && speedsNode is System.Text.Json.Nodes.JsonArray speedsArray)
            {
                foreach (var segNode in speedsArray)
                {
                    var segObj = segNode?.AsObject();
                    if (segObj != null)
                    {
                        _speedSegments.Add(new SpeedSegment(
                            segObj["startMs"]?.GetValue<double>() ?? 0,
                            segObj["endMs"]?.GetValue<double>() ?? 0,
                            segObj["speed"]?.GetValue<double>() ?? 1.0,
                            segObj["zoomX"]?.GetValue<int>(),
                            segObj["zoomY"]?.GetValue<int>(),
                            segObj["zoomW"]?.GetValue<int>(),
                            segObj["zoomH"]?.GetValue<int>(),
                            segObj["zoomOrigRes"]?.GetValue<string>(),
                            segObj["zoomSlow"]?.GetValue<bool>() ?? false,
                            segObj.ContainsKey("zoomStartMs") ? segObj["zoomStartMs"]?.GetValue<double>() : null,
                            segObj.ContainsKey("zoomEndMs") ? segObj["zoomEndMs"]?.GetValue<double>() : null));
                    }
                }
            }

            _isGranularSpeedActive = state["isGranularSpeedActive"]?.GetValue<bool>() ?? false;
            var granularBtnRestore = this.FindControl<Button>("GranularButton");
            if (granularBtnRestore != null && _isGranularSpeedActive)
            {
                granularBtnRestore.Classes.Remove("Primary");
                granularBtnRestore.Classes.Add("Danger");
                granularBtnRestore.Content = "REMOVE SPEEDS";
                ToolTip.SetTip(granularBtnRestore, "Click here to delete all the speed changes you made. This wipes out every speed segment in one go.");
            }

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
                    MusicVolume = musicObj["musicVolume"]?.GetValue<double>() ?? 1.0,
                    LoopMusic = musicObj["loopMusic"]?.GetValue<bool>() ?? false
                };
                if (musicObj["musicFilePaths"] is System.Text.Json.Nodes.JsonArray pathsArr)
                {
                    foreach (var node in pathsArr)
                    {
                        var path = node?.ToString();
                        if (!string.IsNullOrEmpty(path)) _musicWizardResult.MusicFilePaths.Add(path);
                    }
                }
                if (musicObj["musicDurationsSeconds"] is System.Text.Json.Nodes.JsonArray durationsArr)
                {
                    foreach (var node in durationsArr)
                    {
                        if (node != null && double.TryParse(node.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double durationSec))
                            _musicWizardResult.MusicDurationsSeconds.Add(durationSec);
                    }
                }
                NormalizeMusicPlacement(_musicWizardResult);
            }

            _isMusicActive = state["isMusicActive"]?.GetValue<bool>() ?? false;
            var musicBtnRestore = this.FindControl<Button>("AddMusicButton");
            if (musicBtnRestore != null && _isMusicActive)
            {
                musicBtnRestore.Classes.Remove("Primary");
                musicBtnRestore.Classes.Add("Danger");
                musicBtnRestore.Content = "REMOVE MUSIC";
                ToolTip.SetTip(musicBtnRestore, "Click here to remove the background music you added. This deletes the music track from your video.");
            }

            double vol = state["volume"]?.GetValue<double>() ?? 100;
            var volSliderRestore = this.FindControl<Slider>("VolumeSlider");
            if (volSliderRestore != null) volSliderRestore.Value = vol;

            bool portraitMode = state["portraitMode"]?.GetValue<bool>() ?? true;
            var portraitCbRestore = this.FindControl<ToggleSwitch>("PortraitModeCheckbox");
            if (portraitCbRestore != null) portraitCbRestore.IsChecked = portraitMode;

            bool bossHp = state["bossHp"]?.GetValue<bool>() ?? false;
            var bossHpCbRestore = this.FindControl<ToggleSwitch>("BossHpCheckbox");
            if (bossHpCbRestore != null) bossHpCbRestore.IsChecked = bossHp;

            bool showTeammates = state["showTeammates"]?.GetValue<bool>() ?? false;
            var teammatesCbRestore = this.FindControl<ToggleSwitch>("TeammatesCheckbox");
            if (teammatesCbRestore != null) teammatesCbRestore.IsChecked = showTeammates;

            bool showSpectating = state["showSpectating"]?.GetValue<bool>() ?? false;
            var spectatingCbRestore = this.FindControl<ToggleSwitch>("SpectatingCheckbox");
            if (spectatingCbRestore != null) spectatingCbRestore.IsChecked = showSpectating;

            var enableFadeCbRestore = this.FindControl<ToggleSwitch>("EnableFadeCheckbox");
            if (enableFadeCbRestore != null && state.ContainsKey("enableFade"))
                enableFadeCbRestore.IsChecked = state["enableFade"]?.GetValue<bool>() ?? true;

            string portraitText = (string?)state["portraitText"] ?? "";
            var portraitTextRestore = this.FindControl<TextBox>("PortraitTextInput");
            if (portraitTextRestore != null) portraitTextRestore.Text = portraitText;

            bool addMeme = state["addMeme"]?.GetValue<bool>() ?? false;
            var addMemeCbRestore = this.FindControl<ToggleSwitch>("AddMemeCheckbox");
            if (addMemeCbRestore != null) addMemeCbRestore.IsChecked = addMeme;

            string memeFilePath = (string?)state["memeFilePath"] ?? "";
            string memeFile = (string?)state["memeFile"] ?? "";
            string restoreTarget = !string.IsNullOrEmpty(memeFilePath) ? memeFilePath
                : (!string.IsNullOrEmpty(memeFile) ? Path.Combine(Infrastructure.MemeDirectory.GetActive(), memeFile) : "");
            if (!string.IsNullOrEmpty(restoreTarget))
            {
                if (File.Exists(restoreTarget))
                {
                    _pendingMemeRestorePath = restoreTarget;
                    var memeCbRestore = this.FindControl<ComboBox>("MemeComboBox");
                    var immediate = _memeItems.FirstOrDefault(m =>
                        string.Equals(m.FullPath, restoreTarget, StringComparison.OrdinalIgnoreCase));
                    if (memeCbRestore != null && immediate != null)
                    {
                        memeCbRestore.SelectedItem = immediate;
                        _pendingMemeRestorePath = null;
                    }
                }
                else
                {
                    RuntimeLog.Fail("Memes", $"Recovery meme skipped — file no longer exists: {Path.GetFileName(restoreTarget)}");
                    RuntimeLog.Debug("Memes", $"Recovery meme missing full path: {restoreTarget}");
                }
            }

            string? videoPath = (string?)state["loadedVideoPath"];
            if (!string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath))
            {
                _loadedVideoPath = videoPath;
                _isTimelineDrawn = false;

                for (int i = 0; i < 50; i++)
                {
                    if (ActiveVideoHost?.IpcClient != null) break;
                    await Task.Delay(100);
                }

                if (ActiveVideoHost?.IpcClient != null)
                {
                    _ = ActiveVideoHost.IpcClient.LoadFileAsync(videoPath);
                    _ = ActiveVideoHost.IpcClient.SetPropertyAsync("speed",
                        _baseSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                }
            if (ActiveVideoHost != null) ActiveVideoHost.IsVisible = true;
            UpdatePortraitOverlay();

            var uploadOverlay = this.FindControl<Border>("UploadOverlay");
            if (uploadOverlay != null) uploadOverlay.IsVisible = false;

            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdatePortraitOverlay());
            });

                EnableEditingControls();

                UpdateEstimatedQuality();
                UpdateSpeedLabel();
                UpdateTimelineMarkers();

                if (state.TryGetPropertyValue("voiceOverWavPath", out var wavPathNode) || state.ContainsKey("voiceOverMuteMale") || state.ContainsKey("voiceOverTakes"))
                {
                    var wavPath = wavPathNode?.GetValue<string>();
                    bool muteMale = state.TryGetPropertyValue("voiceOverMuteMale", out var mNode) && mNode != null && mNode.GetValue<bool>();
                    bool muteFemale = state.TryGetPropertyValue("voiceOverMuteFemale", out var fNode) && fNode != null && fNode.GetValue<bool>();
                    bool muteChild = state.TryGetPropertyValue("voiceOverMuteChild", out var cNode) && cNode != null && cNode.GetValue<bool>();
                    double muteMaleHz = state.TryGetPropertyValue("voiceOverMuteMaleHz", out var mhzNode) && mhzNode != null ? mhzNode.GetValue<double>() : 0;
                    double muteFemaleHz = state.TryGetPropertyValue("voiceOverMuteFemaleHz", out var fhzNode) && fhzNode != null ? fhzNode.GetValue<double>() : 0;
                    double muteChildHz = state.TryGetPropertyValue("voiceOverMuteChildHz", out var chzNode) && chzNode != null ? chzNode.GetValue<double>() : 0;
                    var restoredTakes = new List<VoiceOverTake>();
                    if (state.TryGetPropertyValue("voiceOverTakes", out var takesNode) &&
                        takesNode is System.Text.Json.Nodes.JsonArray takesArray)
                    {
                        foreach (var takeNode in takesArray)
                        {
                            if (takeNode is not System.Text.Json.Nodes.JsonObject takeObj) continue;
                            string? takePath = takeObj["path"]?.GetValue<string>();
                            double takeStart = takeObj["startSec"]?.GetValue<double>() ?? 0;
                            if (!string.IsNullOrWhiteSpace(takePath) && System.IO.File.Exists(takePath))
                            {
                                restoredTakes.Add(new VoiceOverTake(takePath, takeStart));
                            }
                        }
                    }

                    if (restoredTakes.Count > 0 || (!string.IsNullOrEmpty(wavPath) && System.IO.File.Exists(wavPath)) || muteMale || muteFemale || muteChild)
                    {
                        var resultObj = new VoiceOverWindow.VoiceOverResult
                        {
                            VoiceOverWavPath = wavPath ?? "",
                            VoiceOverStartTimestampSec = state.TryGetPropertyValue("voiceOverStartSec", out var sNode) && sNode != null ? sNode.GetValue<double>() : 0,
                            VoiceOverTakes = restoredTakes,
                            MuteMale = muteMale,
                            MuteFemale = muteFemale,
                            MuteChild = muteChild,
                            MaleFrequencyHz = muteMaleHz,
                            FemaleFrequencyHz = muteFemaleHz,
                            ChildFrequencyHz = muteChildHz
                        };
                        ApplyVoiceOverState(resultObj, true);
                    }
                }

                RuntimeLog.Success("RECOVERY", $"Session restored. Video={Path.GetFileName(videoPath)}, Trim={_trimStartMs}ms-{_trimEndMs}ms, Segments={_speedSegments.Count}, GranularActive={_isGranularSpeedActive}, MusicActive={_isMusicActive}");
                RuntimeLog.Debug("RECOVERY", $"Session restored video path: {videoPath}");
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
            _recovery.ClearState();
        }
        finally
        {
            _isRestoring = false;
        }
    }

    private async void OnExportConfigClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var options = new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Export Configuration",
            DefaultExtension = "json",
            SuggestedFileName = "FortniteVideoSoftware_Config.json",
            FileTypeChoices = new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("JSON Configuration File") { Patterns = new[] { "*.json" } }
            }
        };

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(options);
        if (file != null)
        {
            try
            {
                string stateFile = _paths.SessionStateFile;
                if (System.IO.File.Exists(stateFile))
                {
                    System.IO.File.Copy(stateFile, file.Path.LocalPath, true);
                    NativeDialog.ShowInfo("Configuration successfully backed up.");
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("Config", $"Export failed: {ex.Message}");
                NativeDialog.ShowError($"Failed to export configuration: {ex.Message}", "Export Failed");
            }
        }
    }

    private async void OnImportConfigClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var options = new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Import Configuration",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("JSON Configuration File") { Patterns = new[] { "*.json" } }
            }
        };

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(options);
        if (files.Count > 0)
        {
            try
            {
                string json = System.IO.File.ReadAllText(files[0].Path.LocalPath);

                var state = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(json);
                if (state == null) throw new InvalidOperationException("File is empty or invalid JSON.");
                if (state["schema_version"] == null) throw new InvalidOperationException("Missing schema_version lock. This is not a valid Fortnite Video Software configuration file.");

                System.IO.File.WriteAllText(_paths.SessionStateFile, json);

                NativeDialog.ShowInfo("Configuration successfully restored!\n\nThe application will now close to apply changes. Please restart it manually.");

                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("Config", $"Import failed: {ex.Message}");
                NativeDialog.ShowError($"Invalid configuration file: {ex.Message}", "Import Failed");
            }
        }
    }
}
