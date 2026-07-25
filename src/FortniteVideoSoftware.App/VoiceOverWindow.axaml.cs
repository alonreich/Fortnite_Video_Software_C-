using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using FortniteVideoSoftware.App.Controls;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.App;

public partial class VoiceOverWindow : Window
{
    private const string BoundsKey = "VoiceOverWindowBounds";

    public static readonly Avalonia.StyledProperty<bool> IsMuteMaleProperty =
        Avalonia.AvaloniaProperty.Register<VoiceOverWindow, bool>(nameof(IsMuteMale), false);
    public bool IsMuteMale
    {
        get => GetValue(IsMuteMaleProperty);
        set => SetValue(IsMuteMaleProperty, value);
    }

    public static readonly Avalonia.StyledProperty<bool> IsMuteFemaleProperty =
        Avalonia.AvaloniaProperty.Register<VoiceOverWindow, bool>(nameof(IsMuteFemale), false);
    public bool IsMuteFemale
    {
        get => GetValue(IsMuteFemaleProperty);
        set => SetValue(IsMuteFemaleProperty, value);
    }

    public static readonly Avalonia.StyledProperty<bool> IsMuteChildProperty =
        Avalonia.AvaloniaProperty.Register<VoiceOverWindow, bool>(nameof(IsMuteChild), false);
    public bool IsMuteChild
    {
        get => GetValue(IsMuteChildProperty);
        set => SetValue(IsMuteChildProperty, value);
    }

    private MpvVideoView? _videoHost;
    private VoiceRecorder? _recorder;
    private readonly ApplicationPaths _paths = ApplicationPaths.CreateDefault();
    private string _videoPath = "";
    private string _outputWavPath = "";

    private bool _isRecording = false;
    private bool _isMpvReady = false;
    private bool _isClosing = false;
    private DispatcherTimer _timer;

    private List<float> _waveformSamples = new();

    private string? _tempThumbPath;
    private string? _tempWavePath;
    private System.Threading.CancellationTokenSource? _generationCts;
    private System.Threading.CancellationTokenSource? _probeCts;

    private double _trimStartSec = 0;
    private double _trimEndSec = 0;
    private readonly List<SpeedSegment> _speedSegments = new();
    private double _baseSpeed = 1.0;
    private double _lastAppliedSpeed = 1.0;
    private bool _isCurrentlyFrozen;
    private DateTime _freezeStartTime;
    private double _currentFreezeDurationMs;
    private double _lastFreezeTriggerMs = -1;
    private DateTime _lastTimelineSeekUtc = DateTime.MinValue;
    private const int TimelineDragSeekThrottleMs = 80;
    private double? _dragSeekTimeSec = null;

    private class VoiceOverSession
    {
        public string WavPath { get; set; } = "";
        public double StartSec { get; set; }
        public double EndSec { get; set; }
    }
    private List<VoiceOverSession> _sessions = new();
    private VoiceOverSession? _currentSession;
    private readonly List<Line> _waveformLinePool = new();
    private Rectangle? _currentSessionRegionRect;
    private Polygon? _playheadCaret;
    private Line? _rulerPlayheadLine;
    private int _renderedSessionCount = -1;
    private double _renderedRulerWidth = -1;
    private double _renderedRulerHeight = -1;

    private Button? _micRecordButton;
    private Button? _playPauseButton;
    private Button? _applyButton;
    private Button? _cancelButton;
    private ComboBox? _micDeviceComboBox;
    private TextBlock? _recordingStatusText;
    private TextBlock? _voiceOverHintText;
    private TextBlock? _probingStatusText;
    private TextBlock? _thumbFallbackText;
    private TextBlock? _waveformFallbackText;
    private Ellipse? _recordingLight;
    private Rectangle? _eqMeter;
    private Border? _eqMeterTrack;
    private Canvas? _timelineRulerCanvas;
    private Canvas? _waveformCanvas;
    private Grid? _thumbnailLaneGrid;
    private Grid? _waveformLaneGrid;
    private Image? _thumbnailLaneImage;
    private Image? _waveformLaneImage;
    private Border? _thumbLoadingOverlay;
    private Border? _waveformLoadingOverlay;
    // ISSUE_06 (audit round 4): icons converted to shared Path geometries in XAML
    private Avalonia.Controls.Shapes.Path? _playIcon;
    private Avalonia.Controls.Shapes.Path? _pauseIcon;
    private CheckBox? _muteMaleCb;
    private CheckBox? _muteFemaleCb;
    private CheckBox? _muteChildCb;
    private Border? _thumbPlayheadLine;
    private Border? _wavePlayheadLine;
    private double _detectedMaleHz;
    private double _detectedFemaleHz;
    private double _detectedChildHz;

    public VoiceOverResult? Result { get; private set; }

    public class VoiceOverResult
    {
        public string? VoiceOverWavPath { get; set; }
        public double VoiceOverStartTimestampSec { get; set; }
        public List<VoiceOverTake> VoiceOverTakes { get; set; } = new();
        public bool MuteMale { get; set; }
        public bool MuteFemale { get; set; }
        public bool MuteChild { get; set; }
        public double MaleFrequencyHz { get; set; }
        public double FemaleFrequencyHz { get; set; }
        public double ChildFrequencyHz { get; set; }
    }

    public VoiceOverWindow()
    {
        InitializeComponent();
        CacheControls();
        FortniteVideoSoftware.App.WindowBoundsHelper.Track(this, BoundsKey);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        Closing += OnWindowClosing;
        AttachTitleBarDrag();
        AttachResizeGrip();
        PopulateMicrophoneDevices();
        WireEffectStateControls();
        UpdateTransportState();
        UpdateApplyState();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosing) return;
        WindowBoundsHelper.SaveBoundsSync(this, BoundsKey);
    }

    public VoiceOverWindow(
        string videoPath,
        double startPosSec,
        double trimStartMs = 0,
        double trimEndMs = 0,
        IEnumerable<SpeedSegment>? speedSegments = null,
        double baseSpeed = 1.0) : this()
    {
        _videoPath = videoPath;
        _trimStartSec = trimStartMs / 1000.0;
        _trimEndSec = trimEndMs / 1000.0;
        _baseSpeed = Math.Clamp(baseSpeed, 0.1, 4.0);
        _lastAppliedSpeed = _baseSpeed;
        if (speedSegments != null)
        {
            foreach (var segment in speedSegments)
            {
                _speedSegments.Add(segment);
            }
            _speedSegments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        }
        _paths.EnsureWritableDirectories();
        _outputWavPath = CreateTempVoiceOverPath();


        if (_micRecordButton != null) _micRecordButton.Click += ToggleRecord;
        if (_playPauseButton != null) _playPauseButton.Click += TogglePreviewPlayback;
        if (_applyButton != null) _applyButton.Click += (s, e) => ApplyAndClose();
        if (_cancelButton != null) _cancelButton.Click += (s, e) => Close();

        _timer.Tick += Timer_Tick;
        _timer.Start();
        _timer.Start();

        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, OnKeyDownHandler, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(Avalonia.Input.InputElement.KeyUpEvent, OnKeyUpHandler, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        if (_timelineRulerCanvas != null)
        {
            WireTimelineSeekSurface(_timelineRulerCanvas);
        }

        if (_thumbnailLaneGrid != null)
        {
            WireTimelineSeekSurface(_thumbnailLaneGrid);
        }

        if (_waveformLaneGrid != null)
        {
            WireTimelineSeekSurface(_waveformLaneGrid);
        }

        Loaded += async (_, _) =>
        {

            if (_videoHost != null)
            {
                bool previewReady = false;
                try
                {
                    string mpvPath = ResolveBinaryPath("mpv.exe", "frontend");
                    await _videoHost.StartMpvProcessAsync(mpvPath).WaitAsync(TimeSpan.FromSeconds(8));

                    if (_videoHost.IpcClient != null)
                    {
                        double initialPos = NormalizePreviewPlaybackPosition(startPosSec);
                        await _videoHost.IpcClient.LoadFileAsync(_videoPath, initialPos);
                        await _videoHost.IpcClient.SetPropertyAsync("pause", "yes");

                        if (_trimStartSec > 0 || _trimEndSec > 0)
                        {
                             await _videoHost.IpcClient.SetPropertyAsync("ab-loop-a", _trimStartSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
                             if (_trimEndSec > 0)
                             {
                                 await _videoHost.IpcClient.SetPropertyAsync("ab-loop-b", _trimEndSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
                             }
                             initialPos = NormalizePreviewPlaybackPosition(initialPos);
                        }

                        if (initialPos > 0)
                            await _videoHost.IpcClient.SetPropertyAsync("time-pos", initialPos.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        await _videoHost.IpcClient.SetPropertyAsync("speed", GetSpeedForPosition(initialPos * 1000.0).ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture));
                        RuntimeLog.Info("VoiceOver", $"Preview loaded at {initialPos:0.###}s; trimStart={_trimStartSec:0.###}, trimEnd={_trimEndSec:0.###}.");
                        previewReady = true;
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("VoiceOver", $"Preview startup failed. Recording disabled, voice detection remains available. {ex.Message}");
                    _videoHost.Dispose();
                    if (_recordingStatusText != null)
                    {
                        _recordingStatusText.Text = "PREVIEW OFF";
                        _recordingStatusText.Foreground = GetAppBrush("AppWarningBrush", Brushes.Yellow);
                    }
                    UpdateApplyState("Preview could not start on this graphics session. Voice detection still works.");
                }

                _isMpvReady = previewReady;
                UpdateTransportState();
                UpdateApplyState(previewReady ? null : "Preview could not start on this graphics session. Voice detection still works.");

                _ = RunFrequencyProber();
                if (previewReady)
                {
                    _ = GenerateLanesAsync();
                }
            }
        };
    }
    private void Timer_Tick(object? sender, EventArgs e)
    {
        UpdateWaveformUI();
        UpdatePlayPauseIconUI();
        UpdatePlayheadUI();
    }


    private static string ResolveBinaryPath(string fileName, string preferredSubdirectory)
    {
        string processDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string baseDir = AppContext.BaseDirectory;
        string sourceRootCandidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, "..", "..", "..", "..", "..", "binaries", fileName));
        
        string preferredPath = System.IO.Path.Combine(baseDir, preferredSubdirectory, fileName);
        if (System.IO.File.Exists(preferredPath)) return preferredPath;
        
        string rootPath = System.IO.Path.Combine(baseDir, fileName);
        if (System.IO.File.Exists(rootPath)) return rootPath;
        
        string debugPath = System.IO.Path.Combine(processDir, fileName);
        if (System.IO.File.Exists(debugPath)) return debugPath;

        if (System.IO.File.Exists(sourceRootCandidate)) return sourceRootCandidate;

        return fileName;
    }

    private string CreateTempVoiceOverPath()
    {
        Directory.CreateDirectory(_paths.TempDirectory);
        return System.IO.Path.Combine(_paths.TempDirectory, $"voiceover_{Guid.NewGuid():N}.wav");
    }

    private string CreatePersistedVoiceOverPath()
    {
        string voiceOverDir = System.IO.Path.Combine(_paths.ProgramDataRoot, "voiceovers");
        Directory.CreateDirectory(voiceOverDir);
        return System.IO.Path.Combine(voiceOverDir, $"voiceover_{Guid.NewGuid():N}.wav");
    }

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<Border>("TitleBarBorder");
        if (titleBar == null) return;

        titleBar.IsHitTestVisible = true;
        titleBar.DoubleTapped += (s, e) =>
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
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

    private void CacheControls()
    {
        _videoHost = this.FindControl<MpvVideoView>("VideoHost");
        _micRecordButton = this.FindControl<Button>("MicRecordButton");
        _playPauseButton = this.FindControl<Button>("PlayPauseButton");
        _applyButton = this.FindControl<Button>("ApplyButton");
        _cancelButton = this.FindControl<Button>("CancelButton");
        _micDeviceComboBox = this.FindControl<ComboBox>("MicDeviceComboBox");
        _recordingStatusText = this.FindControl<TextBlock>("RecordingStatusText");
        _voiceOverHintText = this.FindControl<TextBlock>("VoiceOverHintText");
        _probingStatusText = this.FindControl<TextBlock>("ProbingStatusText");
        _thumbFallbackText = this.FindControl<TextBlock>("ThumbFallbackText");
        _waveformFallbackText = this.FindControl<TextBlock>("WaveformFallbackText");
        _recordingLight = this.FindControl<Ellipse>("RecordingLight");
        _eqMeter = this.FindControl<Rectangle>("EqMeter");
        _eqMeterTrack = this.FindControl<Border>("EqMeterTrack");
        _timelineRulerCanvas = this.FindControl<Canvas>("TimelineRulerCanvas");
        _waveformCanvas = this.FindControl<Canvas>("WaveformCanvas");
        _thumbnailLaneGrid = this.FindControl<Grid>("ThumbnailLaneGrid");
        _waveformLaneGrid = this.FindControl<Grid>("WaveformLaneGrid");
        _thumbnailLaneImage = this.FindControl<Image>("ThumbnailLaneImage");
        _waveformLaneImage = this.FindControl<Image>("WaveformLaneImage");
        _thumbLoadingOverlay = this.FindControl<Border>("ThumbLoadingOverlay");
        _waveformLoadingOverlay = this.FindControl<Border>("WaveformLoadingOverlay");
        _playIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon");
        _pauseIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PauseIcon");
        _muteMaleCb = this.FindControl<CheckBox>("MuteMaleCb");
        _muteFemaleCb = this.FindControl<CheckBox>("MuteFemaleCb");
        _muteChildCb = this.FindControl<CheckBox>("MuteChildCb");
        _thumbPlayheadLine = this.FindControl<Border>("ThumbPlayheadLine");
        _wavePlayheadLine = this.FindControl<Border>("WavePlayheadLine");
    }

    private void PopulateMicrophoneDevices()
    {
        if (_micDeviceComboBox == null) return;

        var devices = VoiceRecorder.GetInputDeviceNames();
        _micDeviceComboBox.ItemsSource = devices;
        _micDeviceComboBox.IsEnabled = devices.Count > 0;
        _micDeviceComboBox.SelectedIndex = devices.Count > 0 ? 0 : -1;
        ToolTip.SetTip(_micDeviceComboBox, devices.Count > 0
            ? "Choose which microphone records the voiceover"
            : "No microphone input device detected");
    }

    private int GetSelectedMicrophoneDeviceIndex()
    {
        if (_micDeviceComboBox == null || _micDeviceComboBox.SelectedIndex < 0)
        {
            return 0;
        }

        return Math.Max(0, _micDeviceComboBox.SelectedIndex);
    }

    private IBrush GetAppBrush(string resourceKey, IBrush fallback)
    {
        if (Application.Current?.TryFindResource(resourceKey, ActualThemeVariant, out var value) == true &&
            value is IBrush brush)
        {
            return brush;
        }

        return fallback;
    }

    private Color GetAppColor(string resourceKey, Color fallback)
    {
        if (Application.Current?.TryFindResource(resourceKey, ActualThemeVariant, out var value) == true &&
            value is ISolidColorBrush brush)
        {
            return brush.Color;
        }

        return fallback;
    }

    private SolidColorBrush CreateAppOverlayBrush(string resourceKey, byte alpha, Color fallback)
    {
        var color = GetAppColor(resourceKey, fallback);
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    private void AttachResizeGrip()
    {
        var resizeGrip = this.FindControl<Border>("ResizeGrip");
        if (resizeGrip == null) return;

        resizeGrip.Cursor = new Cursor(StandardCursorType.BottomRightCorner);
        resizeGrip.PointerPressed += (s, e) =>
        {
            if (WindowState == WindowState.Maximized) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            try
            {
                BeginResizeDrag(WindowEdge.SouthEast, e);
                e.Handled = true;
            }
            catch { }
        };
    }

    private void WireTimelineSeekSurface(Control surface)
    {
        surface.PointerPressed += (s, e) =>
        {
            e.Pointer.Capture(surface);
            SeekTimelineFromPointer(e, surface, force: true);
        };
        surface.PointerMoved += (s, e) =>
        {
            if (e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed)
            {
                SeekTimelineFromPointer(e, surface, force: false);
            }
        };
        surface.PointerReleased += (s, e) =>
        {
            e.Pointer.Capture(null);
            SeekTimelineFromPointer(e, surface, force: true);
        };
        surface.KeyDown += TimelineSurface_KeyDown;
    }

    private void TimelineSurface_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_isRecording || _videoHost?.IpcClient == null) return;

        switch (e.Key)
        {
            case Key.Left:
                SeekBySeconds(-1);
                e.Handled = true;
                break;
            case Key.Right:
                SeekBySeconds(1);
                e.Handled = true;
                break;
            case Key.Home:
                SeekToAbsolute(_trimStartSec);
                e.Handled = true;
                break;
            case Key.End:
                SeekToAbsolute(GetEffectiveTimelineEnd());
                e.Handled = true;
                break;
        }
    }

    private void SeekBySeconds(double seconds)
    {
        if (_videoHost?.IpcClient == null) return;
        double target = Math.Clamp(_videoHost.IpcClient.CurrentTime + seconds, _trimStartSec, GetEffectiveTimelineEnd());
        SeekToAbsolute(target);
    }

    private void SeekToAbsolute(double seconds)
    {
        if (_videoHost?.IpcClient == null) return;
        _isCurrentlyFrozen = false;
        _lastFreezeTriggerMs = -1;
        ApplyPreviewSpeedForPosition(seconds * 1000.0);
        _ = _videoHost.IpcClient.SetPropertyAsync("time-pos", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private double GetEffectiveTimelineEnd()
    {
        if (_videoHost?.IpcClient == null) return _trimEndSec > 0 ? _trimEndSec : _trimStartSec;
        double videoDuration = _videoHost.IpcClient.Duration;
        return _trimEndSec > 0 ? _trimEndSec : Math.Max(_trimStartSec, videoDuration);
    }

    private double NormalizePreviewPlaybackPosition(double seconds)
    {
        double start = Math.Max(0, _trimStartSec);
        double end = _trimEndSec > start ? _trimEndSec : double.PositiveInfinity;

        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < start)
        {
            return start;
        }

        if (!double.IsInfinity(end) && seconds >= end - 0.05)
        {
            return start;
        }

        return seconds;
    }

    private SpeedSegment? FindFreezeSegment(double positionMs)
    {
        foreach (var seg in _speedSegments)
        {
            if (Math.Abs(seg.Speed) < 0.001 &&
                positionMs >= seg.StartMs &&
                positionMs < seg.EndMs)
            {
                return seg;
            }
        }

        return null;
    }

    private double GetSpeedForPosition(double positionMs)
    {
        foreach (var seg in _speedSegments)
        {
            if (positionMs >= seg.StartMs && positionMs < seg.EndMs)
            {
                return Math.Abs(seg.Speed) < 0.001 ? _baseSpeed : Math.Clamp(seg.Speed, 0.1, 4.0);
            }
        }

        return _baseSpeed;
    }

    private void ApplyPreviewSpeedForPosition(double positionMs)
    {
        if (_videoHost?.IpcClient == null) return;
        double targetSpeed = GetSpeedForPosition(positionMs);
        if (Math.Abs(targetSpeed - _lastAppliedSpeed) <= 0.001) return;

        _lastAppliedSpeed = targetSpeed;
        _ = _videoHost.IpcClient.SetPropertyAsync("speed",
            targetSpeed.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture));
    }

    private void UpdatePreviewSpeedAndFreeze(double currentTimeSec)
    {
        if (_videoHost?.IpcClient == null) return;

        double currentAbsMs = currentTimeSec * 1000.0;
        if (_isCurrentlyFrozen)
        {
            if ((DateTime.UtcNow - _freezeStartTime).TotalSeconds >= Math.Max(0.05, (_currentFreezeDurationMs / 1000.0)))
            {
                _isCurrentlyFrozen = false;
                _ = _videoHost.IpcClient.SetPropertyAsync("pause", "no");
            }
            return;
        }

        if (_videoHost.IpcClient.IsPaused) return;

        var freeze = FindFreezeSegment(currentAbsMs);
        if (freeze != null && Math.Abs(freeze.StartMs - _lastFreezeTriggerMs) > 1.0)
        {
            _lastFreezeTriggerMs = freeze.StartMs;
            _currentFreezeDurationMs = Math.Max(50.0, freeze.EndMs - freeze.StartMs);
            _isCurrentlyFrozen = true;
            _freezeStartTime = DateTime.UtcNow;
            _ = _videoHost.IpcClient.SetPropertyAsync("time-pos", (freeze.StartMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture));
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
            return;
        }
        if (freeze == null && _lastFreezeTriggerMs >= 0 && Math.Abs(currentAbsMs - _lastFreezeTriggerMs) > 1000.0)
        {
            _lastFreezeTriggerMs = -1;
        }

        ApplyPreviewSpeedForPosition(currentAbsMs);
    }

    private void WireEffectStateControls()
    {
        foreach (var checkBox in new[] { _muteMaleCb, _muteFemaleCb, _muteChildCb })
        {
            if (checkBox != null)
            {
                checkBox.IsCheckedChanged += (_, _) => UpdateApplyState();
            }
        }
    }

    private void UpdateTransportState()
    {
        bool hasInputDevice = VoiceRecorder.HasInputDevice;
        if (_micRecordButton != null)
        {
            _micRecordButton.IsEnabled = _isMpvReady && (_isRecording || hasInputDevice);
            ToolTip.SetTip(_micRecordButton, hasInputDevice
                ? "Start or stop voiceover recording (V)"
                : "No microphone input device detected");
        }
        if (_playPauseButton != null) _playPauseButton.IsEnabled = _isMpvReady && !_isRecording;
        if (_micDeviceComboBox != null) _micDeviceComboBox.IsEnabled = hasInputDevice && !_isRecording;

        if (!hasInputDevice && !_isRecording && _recordingStatusText != null)
        {
            _recordingStatusText.Text = "NO MIC";
            _recordingStatusText.Foreground = GetAppBrush("AppWarningBrush", Brushes.Yellow);
        }
    }

    private bool HasSavedVoiceOverSession()
    {
        foreach (var session in _sessions)
        {
            if (session.EndSec > session.StartSec &&
                !string.IsNullOrWhiteSpace(session.WavPath) &&
                System.IO.File.Exists(session.WavPath))
            {
                return true;
            }
        }
        return false;
    }

    private bool HasMuteSelection()
    {
        return _muteMaleCb?.IsChecked == true ||
               _muteFemaleCb?.IsChecked == true ||
               _muteChildCb?.IsChecked == true;
    }

    private bool HasApplicableVoiceEffect()
    {
        return _isRecording || HasSavedVoiceOverSession() || HasMuteSelection();
    }

    private void UpdateApplyState(string? message = null)
    {
        bool canApply = HasApplicableVoiceEffect();
        string? effectiveMessage = message;
        if (effectiveMessage == null && !canApply && !VoiceRecorder.HasInputDevice)
        {
            effectiveMessage = "No microphone input detected. You can still choose a detected voice mute option.";
        }

        if (_applyButton != null)
        {
            _applyButton.IsEnabled = canApply;
            ToolTip.SetTip(_applyButton, canApply
                ? "Apply recorded voiceover and selected voice muting"
                : "Record a take or choose a mute option before applying");
        }

        if (_voiceOverHintText != null)
        {
            _voiceOverHintText.Text = effectiveMessage ?? (canApply
                ? "Ready to apply the current voiceover changes. Cancel discards unapplied takes."
                : "Record a take or choose a detected voice mute option before applying. Cancel discards unapplied takes.");
            _voiceOverHintText.Foreground = canApply
                ? GetAppBrush("AppTextPrimaryBrush", Brushes.White)
                : GetAppBrush("AppTextMutedBrush", Brushes.Gray);
        }
    }


    private async Task GenerateLanesAsync()
    {
        if (_videoHost?.IpcClient == null) return;
        
        while (_videoHost.IpcClient.Duration <= 0)
        {
            await Task.Delay(100);
            if (_isClosing) return;
        }

        double videoDuration = _videoHost.IpcClient.Duration;
        double durationSec = (_trimEndSec > 0 ? _trimEndSec : videoDuration) - _trimStartSec;
        if (durationSec <= 0) durationSec = videoDuration;
        string ffmpeg = ResolveBinaryPath("ffmpeg.exe", "backend");

        _generationCts = new System.Threading.CancellationTokenSource();
        var token = _generationCts.Token;

        if (_thumbFallbackText != null) _thumbFallbackText.IsVisible = false;
        if (_waveformFallbackText != null) _waveformFallbackText.IsVisible = false;
        if (_thumbLoadingOverlay != null) _thumbLoadingOverlay.IsVisible = true;
        if (_waveformLoadingOverlay != null) _waveformLoadingOverlay.IsVisible = true;

        string localVideoPath = _videoPath ?? "";
        double localTrimStart = _trimStartSec;

        var thumbTask = Task.Run(async () =>
        {
            string? tempPng = null;
            System.Diagnostics.Process? process = null;
            try
            {
                tempPng = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fvs_thumb_{Guid.NewGuid():N}.png");
                double fps = 16.0 / (durationSec > 0 ? durationSec : 10);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-y -hide_banner -loglevel error -hwaccel auto -ss {localTrimStart.ToString(System.Globalization.CultureInfo.InvariantCulture)} -t {durationSec.ToString(System.Globalization.CultureInfo.InvariantCulture)} -i \"{localVideoPath}\" -vf \"fps={fps.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)},scale=-1:60,tile=15x1\" -frames:v 1 \"{tempPng}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                process = System.Diagnostics.Process.Start(psi);
                if (process != null) await process.WaitForExitAsync(token);
                if (process?.ExitCode == 0 && System.IO.File.Exists(tempPng)) return tempPng;
            }
            catch (Exception ex)
            {
                CoreLogger.Fail("VoiceOver", $"Thumbnail lane generation failed: {ex.Message}");
            }
            finally
            {
                if (process != null)
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    process.Dispose();
                }
            }
            return null;
        });

        var waveTask = Task.Run(async () =>
        {
            try
            {
                return await FortniteVideoSoftware.Core.Media.WaveformGenerator.GenerateWaveformImageAsync(
                        ffmpeg, localVideoPath, 1200, 60, localTrimStart, durationSec, token);
            }
            catch (Exception ex)
            {
                CoreLogger.Fail("VoiceOver", $"Waveform lane generation failed: {ex.Message}");
            }
            return null;
        });

        string? thumbPath = await thumbTask;
        string? wavePath = await waveTask;

        if (token.IsCancellationRequested) return;

        bool thumbLoaded = false;
        bool waveformLoaded = false;

        if (thumbPath != null && _thumbnailLaneImage != null)
        {
            try
            {
                using var fs = System.IO.File.OpenRead(thumbPath);
                _thumbnailLaneImage.Source = new Bitmap(fs);
                _tempThumbPath = thumbPath;
                thumbLoaded = true;
            }
            catch (Exception ex)
            {
                CoreLogger.Fail("VoiceOver", $"Thumbnail lane image load failed: {ex.Message}");
            }
        }
        if (_thumbLoadingOverlay != null) _thumbLoadingOverlay.IsVisible = false;
        if (_thumbFallbackText != null)
        {
            _thumbFallbackText.Text = thumbLoaded ? "" : "Frame preview unavailable.";
            _thumbFallbackText.IsVisible = !thumbLoaded;
        }

        if (wavePath != null && _waveformLaneImage != null)
        {
            try
            {
                using var fs = System.IO.File.OpenRead(wavePath);
                _waveformLaneImage.Source = new Bitmap(fs);
                _tempWavePath = wavePath;
                waveformLoaded = true;
            }
            catch (Exception ex)
            {
                CoreLogger.Fail("VoiceOver", $"Waveform lane image load failed: {ex.Message}");
            }
        }
        if (_waveformLoadingOverlay != null) _waveformLoadingOverlay.IsVisible = false;
        if (_waveformFallbackText != null)
        {
            _waveformFallbackText.Text = waveformLoaded ? "" : "Waveform unavailable.";
            _waveformFallbackText.IsVisible = !waveformLoaded;
        }
    }

    private void UpdatePlayPauseIconUI()
    {
        if (_videoHost?.IpcClient == null) return;
        bool isPaused = _videoHost.IpcClient.IsPaused && !_isCurrentlyFrozen;
        if (_playIcon != null) _playIcon.IsVisible = isPaused;
        if (_pauseIcon != null) _pauseIcon.IsVisible = !isPaused;
    }

    private void UpdatePlayheadUI()
    {
        if (_videoHost?.IpcClient == null) return;
        double currentTime = _videoHost.IpcClient.CurrentTime;
        double videoDuration = _videoHost.IpcClient.Duration;
        if (videoDuration <= 0) return;
        UpdatePreviewSpeedAndFreeze(currentTime);
        
        double effectiveDuration = (_trimEndSec > 0 ? _trimEndSec : videoDuration) - _trimStartSec;
        if (effectiveDuration <= 0) effectiveDuration = videoDuration;

        double visualTime = _dragSeekTimeSec ?? currentTime;
        double relativeTime = visualTime - _trimStartSec;
        double fraction = Math.Clamp(relativeTime / effectiveDuration, 0, 1);

        if (_timelineRulerCanvas != null && _timelineRulerCanvas.Bounds.Width > 0)
        {
            double width = _timelineRulerCanvas.Bounds.Width;
            double height = Math.Max(1, _timelineRulerCanvas.Bounds.Height);

            if (_renderedSessionCount != _sessions.Count ||
                Math.Abs(_renderedRulerWidth - width) > 0.5 ||
                Math.Abs(_renderedRulerHeight - height) > 0.5)
            {
                RebuildRulerSessionRegions(_timelineRulerCanvas, effectiveDuration, width, height);
            }

            EnsureRulerDynamicVisuals(_timelineRulerCanvas, height);
            UpdateCurrentRecordingRegion(_timelineRulerCanvas, effectiveDuration, width, height, fraction);

            double caretX = Math.Clamp(fraction * width, 0, width);
            if (_playheadCaret != null)
            {
                Canvas.SetLeft(_playheadCaret, caretX);
                Canvas.SetTop(_playheadCaret, Math.Max(0, height - 10));
            }
            if (_rulerPlayheadLine != null)
            {
                _rulerPlayheadLine.StartPoint = new Avalonia.Point(0, 0);
                _rulerPlayheadLine.EndPoint = new Avalonia.Point(0, height);
                Canvas.SetLeft(_rulerPlayheadLine, caretX);
            }
        }

        if (_thumbnailLaneGrid != null && _thumbnailLaneGrid.Bounds.Width > 0)
        {
            if (_thumbPlayheadLine != null)
            {
                double x = fraction * _thumbnailLaneGrid.Bounds.Width;
                _thumbPlayheadLine.Margin = new Thickness(x, 0, 0, 0);
            }
        }

        if (_waveformLaneGrid != null && _waveformLaneGrid.Bounds.Width > 0)
        {
            if (_wavePlayheadLine != null)
            {
                double x = fraction * _waveformLaneGrid.Bounds.Width;
                _wavePlayheadLine.Margin = new Thickness(x, 0, 0, 0);
            }
        }
    }

    private void RebuildRulerSessionRegions(Canvas ruler, double effectiveDuration, double width, double height)
    {
        ruler.Children.Clear();
        _currentSessionRegionRect = null;
        _playheadCaret = null;
        _rulerPlayheadLine = null;

        foreach (var session in _sessions)
        {
            double startFrac = (session.StartSec - _trimStartSec) / effectiveDuration;
            double endFrac = (session.EndSec - _trimStartSec) / effectiveDuration;
            double x1 = Math.Clamp(startFrac * width, 0, width);
            double x2 = Math.Clamp(endFrac * width, 0, width);
            if (x2 <= x1) continue;

            var region = new Rectangle
            {
                Opacity = 0.4,
                Width = x2 - x1,
                Height = height,
                IsHitTestVisible = false
            };
            region[!Rectangle.FillProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppDangerBrush");
            Canvas.SetLeft(region, x1);
            Canvas.SetTop(region, 0);
            ruler.Children.Add(region);
        }

        _renderedSessionCount = _sessions.Count;
        _renderedRulerWidth = width;
        _renderedRulerHeight = height;
    }

    private void EnsureRulerDynamicVisuals(Canvas ruler, double height)
    {
        if (_currentSessionRegionRect == null)
        {
            _currentSessionRegionRect = new Rectangle
            {
                Opacity = 0.4,
                Height = height,
                IsHitTestVisible = false,
                IsVisible = false
            };
            _currentSessionRegionRect[!Rectangle.FillProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppDangerBrush");
            ruler.Children.Add(_currentSessionRegionRect);
        }

        if (_playheadCaret == null)
        {
            _playheadCaret = new Polygon
            {
                Points = new List<Avalonia.Point> { new(-6, 0), new(6, 0), new(0, 10) },
                IsHitTestVisible = false
            };
            _playheadCaret[!Polygon.FillProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppDangerBrush");
            ruler.Children.Add(_playheadCaret);
        }

        if (_rulerPlayheadLine == null)
        {
            _rulerPlayheadLine = new Line
            {
                StrokeThickness = 2,
                IsHitTestVisible = false
            };
            _rulerPlayheadLine[!Line.StrokeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppDangerBrush");
            ruler.Children.Add(_rulerPlayheadLine);
        }
    }

    private void UpdateCurrentRecordingRegion(Canvas ruler, double effectiveDuration, double width, double height, double currentFraction)
    {
        if (_currentSessionRegionRect == null) return;

        if (!_isRecording || _currentSession == null)
        {
            _currentSessionRegionRect.IsVisible = false;
            return;
        }

        double startFrac = (_currentSession.StartSec - _trimStartSec) / effectiveDuration;
        double x1 = Math.Clamp(startFrac * width, 0, width);
        double x2 = Math.Clamp(currentFraction * width, 0, width);
        if (x2 <= x1)
        {
            _currentSessionRegionRect.IsVisible = false;
            return;
        }

        _currentSessionRegionRect.IsVisible = true;
        _currentSessionRegionRect.Width = x2 - x1;
        _currentSessionRegionRect.Height = height;
        Canvas.SetLeft(_currentSessionRegionRect, x1);
        Canvas.SetTop(_currentSessionRegionRect, 0);
    }

    private async Task RunFrequencyProber()
    {
        _probeCts?.Cancel();
        _probeCts?.Dispose();
        _probeCts = new CancellationTokenSource();
        var token = _probeCts.Token;
        FrequencyProber.DemographicResult result;
        try
        {
            int probeSeconds = 15;
            if (_trimEndSec > _trimStartSec)
            {
                probeSeconds = Math.Max(1, (int)Math.Ceiling(Math.Min(15, _trimEndSec - _trimStartSec)));
            }
            result = await Task.Run(() => FrequencyProber.Probe(_videoPath, probeSeconds, token, _trimStartSec), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("VoiceOver", $"Voice frequency probe failed: {ex.Message}");
            Dispatcher.UIThread.Post(() =>
            {
                if (_isClosing) return;
                if (_probingStatusText != null)
                {
                    _probingStatusText.Text = "Voice detection unavailable. Recording still works.";
                    _probingStatusText.Foreground = GetAppBrush("AppWarningBrush", Brushes.Yellow);
                }

                if (_muteMaleCb != null) _muteMaleCb.IsVisible = false;
                if (_muteFemaleCb != null) _muteFemaleCb.IsVisible = false;
                if (_muteChildCb != null) _muteChildCb.IsVisible = false;
                UpdateApplyState("Voice detection could not run. You can still record a voiceover take.");
            });
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosing) return;
            var found = new List<string>();
            _detectedMaleHz = result.AdultMaleFrequencyHz;
            _detectedFemaleHz = result.AdultFemaleFrequencyHz;
            _detectedChildHz = result.ChildFrequencyHz;

            if (result.HasAdultMale) found.Add($"Male {result.AdultMaleConfidence:P0} @ {result.AdultMaleFrequencyHz:0}Hz");
            if (result.HasAdultFemale) found.Add($"Female {result.AdultFemaleConfidence:P0} @ {result.AdultFemaleFrequencyHz:0}Hz");
            if (result.HasChild) found.Add($"Child {result.ChildConfidence:P0} @ {result.ChildFrequencyHz:0}Hz");

            if (_probingStatusText != null)
            {
                _probingStatusText.Text = found.Count > 0
                    ? $"Candidate voice ranges: {string.Join(", ", found)}"
                    : "No strong voice-frequency candidates detected.";
                _probingStatusText.Foreground = found.Count > 0
                    ? GetAppBrush("AppTextPrimaryBrush", Brushes.White)
                    : GetAppBrush("AppTextMutedBrush", Brushes.Gray);
            }

            if (_muteMaleCb != null)
            {
                _muteMaleCb.IsVisible = result.HasAdultMale;
                _muteMaleCb.Content = result.HasAdultMale
                    ? $"Reduce Adult Male Range ({result.AdultMaleFrequencyHz:0}Hz)"
                    : "Reduce Adult Male Range";
                _muteMaleCb.IsChecked = false;
            }
            if (_muteFemaleCb != null)
            {
                _muteFemaleCb.IsVisible = result.HasAdultFemale;
                _muteFemaleCb.Content = result.HasAdultFemale
                    ? $"Reduce Female Range ({result.AdultFemaleFrequencyHz:0}Hz)"
                    : "Reduce Female Range";
                _muteFemaleCb.IsChecked = false;
            }
            if (_muteChildCb != null)
            {
                _muteChildCb.IsVisible = result.HasChild;
                _muteChildCb.Content = result.HasChild
                    ? $"Reduce Child Range ({result.ChildFrequencyHz:0}Hz)"
                    : "Reduce Child Range";
                _muteChildCb.IsChecked = false;
            }
            UpdateApplyState();
        });
    }

    private void SeekTimelineFromPointer(Avalonia.Input.PointerEventArgs e, Avalonia.Controls.Control timelineCanvas, bool force)
    {
        if (e.Handled) return;
        if (_isRecording) return;
        if (_videoHost?.IpcClient == null) return;
        double videoDuration = _videoHost.IpcClient.Duration;
        double effectiveDuration = (_trimEndSec > 0 ? _trimEndSec : videoDuration) - _trimStartSec;
        if (effectiveDuration <= 0) effectiveDuration = videoDuration;
        double width = timelineCanvas.Bounds.Width;
        if (effectiveDuration <= 0 || width <= 0) return;

        double x = Math.Clamp(e.GetPosition(timelineCanvas).X, 0, width);
        double targetTime = _trimStartSec + (x / width) * effectiveDuration;
        
        if (!force)
        {
            _dragSeekTimeSec = targetTime;
            e.Handled = true;
            return;
        }

        _dragSeekTimeSec = null;
            _lastTimelineSeekUtc = DateTime.UtcNow;
        SeekToAbsolute(targetTime);
        e.Handled = true;
    }

    private void OnKeyUpHandler(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Avalonia.Controls.TextBox or Avalonia.Controls.NumericUpDown)
            return;

        var kb = FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.KeyBinds;
        var playPause = new Avalonia.Input.KeyGesture(kb.PlayPause);
        
        if (playPause.Matches(e) || e.Key is Avalonia.Input.Key.Space or Avalonia.Input.Key.Left or Avalonia.Input.Key.Right)
        {
            e.Handled = true;
        }
    }

    private void OnKeyDownHandler(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Avalonia.Controls.TextBox or Avalonia.Controls.NumericUpDown)
            return;

        if (e.Key == Avalonia.Input.Key.V)
        {
            ToggleRecord(null, null);
            e.Handled = true;
            return;
        }

        var kb = FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.KeyBinds;
        var playPause = new Avalonia.Input.KeyGesture(kb.PlayPause);
        var seekFwd = new Avalonia.Input.KeyGesture(kb.SeekForward);
        var seekBack = new Avalonia.Input.KeyGesture(kb.SeekBackward);
        var fineSeekFwdCtrl = new Avalonia.Input.KeyGesture(kb.FineSeekForward, Avalonia.Input.KeyModifiers.Control);
        var fineSeekFwdShift = new Avalonia.Input.KeyGesture(kb.FineSeekForward, Avalonia.Input.KeyModifiers.Shift);
        var fineSeekBackCtrl = new Avalonia.Input.KeyGesture(kb.FineSeekBackward, Avalonia.Input.KeyModifiers.Control);
        var fineSeekBackShift = new Avalonia.Input.KeyGesture(kb.FineSeekBackward, Avalonia.Input.KeyModifiers.Shift);

        if (playPause.Matches(e))
        {
            TogglePreviewPlayback(null, null);
            e.Handled = true;
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
            _ = _videoHost?.IpcClient?.SendCommandAsync("seek", "5", "relative");
            e.Handled = true;
        }
        else if (seekBack.Matches(e))
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("seek", "-5", "relative");
            e.Handled = true;
        }
    }

    private void ToggleRecord(object? sender, Avalonia.Interactivity.RoutedEventArgs? e)
    {
        if (!_isMpvReady) return;

        if (_isRecording)
        {
            StopRecordingAndPlayback();
        }
        else
        {
            StartRecordingAndPlayback();
        }
    }

    private async void TogglePreviewPlayback(object? sender, Avalonia.Interactivity.RoutedEventArgs? e)
    {
        if (!_isMpvReady || _isRecording || _videoHost?.IpcClient == null) return;

        bool shouldPlay = _videoHost.IpcClient.IsPaused;
        if (shouldPlay)
        {
            double current = _videoHost.IpcClient.CurrentTime;
            double safeStart = NormalizePreviewPlaybackPosition(current);
            if (Math.Abs(safeStart - current) > 0.01)
            {
                await _videoHost.IpcClient.SetPropertyAsync("time-pos", safeStart.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            _isCurrentlyFrozen = false;
            _lastFreezeTriggerMs = -1;
            ApplyPreviewSpeedForPosition(safeStart * 1000.0);
            RuntimeLog.Info("VoiceOver", $"Preview playback requested at {safeStart:0.###}s.");
        }

        await _videoHost.IpcClient.SetPropertyAsync("pause", shouldPlay ? "no" : "yes");
        UpdatePlayPauseIconUI();
    }

    private void StartRecordingAndPlayback()
    {
        if (!VoiceRecorder.HasInputDevice)
        {
            ShowMicrophoneUnavailable("No microphone input device is available.");
            return;
        }

        if (_recorder != null)
        {
            _recorder.VolumeChanged -= OnVolumeChanged;
            _recorder.Dispose();
        }
        _outputWavPath = CreateTempVoiceOverPath();

        double currentPreviewTime = _videoHost?.IpcClient?.CurrentTime ?? _trimStartSec;
        double recordingStart = NormalizePreviewPlaybackPosition(currentPreviewTime);
        if (Math.Abs(recordingStart - currentPreviewTime) > 0.01)
        {
            _ = _videoHost?.IpcClient?.SetPropertyAsync("time-pos", recordingStart.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        _isCurrentlyFrozen = false;
        _lastFreezeTriggerMs = -1;
        ApplyPreviewSpeedForPosition(recordingStart * 1000.0);

        _currentSession = new VoiceOverSession {
            WavPath = _outputWavPath,
            StartSec = recordingStart
        };

        _recorder = new VoiceRecorder(_outputWavPath, GetSelectedMicrophoneDeviceIndex());
        _recorder.VolumeChanged += OnVolumeChanged;

        try
        {
            _recorder.StartRecording();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("VoiceOver", $"Microphone recording could not start. {ex.Message}");
            _recorder.VolumeChanged -= OnVolumeChanged;
            _recorder.Dispose();
            _recorder = null;
            _currentSession = null;
            _isRecording = false;
            TryDeleteFile(_outputWavPath);
            ShowMicrophoneUnavailable("Microphone recording could not start on this PC.");
            return;
        }

        _isRecording = true;
        _waveformSamples.Clear();
        _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "no");

        if (_recordingLight != null) _recordingLight.Opacity = 1.0;
        if (_recordingStatusText != null)
        {
            _recordingStatusText.Text = "RECORDING";
            _recordingStatusText.Foreground = GetAppBrush("AppDangerBrush", Brushes.Red);
        }
        
        if (_micRecordButton != null && !_micRecordButton.Classes.Contains("recording")) _micRecordButton.Classes.Add("recording");
        UpdateTransportState();
        UpdateApplyState("Recording in progress. Apply will save the current take and close.");
    }

    private void ShowMicrophoneUnavailable(string message)
    {
        _isRecording = false;
        if (_recordingLight != null) _recordingLight.Opacity = 0.2;
        if (_recordingStatusText != null)
        {
            _recordingStatusText.Text = "NO MIC";
            _recordingStatusText.Foreground = GetAppBrush("AppWarningBrush", Brushes.Yellow);
        }
        if (_micRecordButton != null) _micRecordButton.Classes.Remove("recording");
        UpdateTransportState();
        UpdateApplyState($"{message} You can still use voice mute options if detection finds them.");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private void StopRecordingAndPlayback()
    {
        if (!_isRecording) return;

        _isRecording = false;
        _isCurrentlyFrozen = false;
        _recorder?.StopRecording();
        _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");

        bool rejectedShortRecording = false;
        if (_currentSession != null)
        {
            _currentSession.EndSec = _videoHost?.IpcClient?.CurrentTime ?? _currentSession.StartSec;
            if (_currentSession.EndSec > _currentSession.StartSec + 0.05 &&
                System.IO.File.Exists(_currentSession.WavPath))
            {
                _sessions.Add(_currentSession);
                _renderedSessionCount = -1;
            }
            else
            {
                rejectedShortRecording = true;
            }
            _currentSession = null;
        }

        if (_recordingLight != null) _recordingLight.Opacity = 0.2;
        if (_recordingStatusText != null)
        {
            _recordingStatusText.Text = "PAUSED";
            _recordingStatusText.Foreground = GetAppBrush("AppTextPrimaryBrush", Brushes.White);
        }
        
        if (_micRecordButton != null) _micRecordButton.Classes.Remove("recording");
        UpdateTransportState();
        UpdateApplyState(rejectedShortRecording
            ? "Recording was too short to apply. Record another take or choose a mute option."
            : null);
    }

    private void OnVolumeChanged(object? sender, float volume)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_eqMeter != null)
            {
                double meterWidth = _eqMeterTrack != null && _eqMeterTrack.Bounds.Width > 0
                    ? _eqMeterTrack.Bounds.Width
                    : 150;
                _eqMeter.Width = Math.Min(meterWidth, volume * meterWidth * 2.0);
                if (volume > 0.8) _eqMeter[!Rectangle.FillProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppDangerBrush");
                else if (volume > 0.5) _eqMeter[!Rectangle.FillProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppWarningBrush");
                else _eqMeter[!Rectangle.FillProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppSuccessBrush");
            }

            if (_isRecording)
            {
                _waveformSamples.Add(volume);
            }
        });
    }

    private void UpdateWaveformUI()
    {
        if (_waveformCanvas == null || !_isRecording) return;

        int maxLines = Math.Max(0, (int)(_waveformCanvas.Bounds.Width / 2));
        int visibleLines = Math.Min(maxLines, _waveformSamples.Count);
        EnsureWaveformLinePool(_waveformCanvas, visibleLines);
        
        int startIdx = Math.Max(0, _waveformSamples.Count - visibleLines);
        double x = 0;
        double laneHeight = Math.Max(1, _waveformCanvas.Bounds.Height);
        double centerY = laneHeight / 2;

        for (int i = 0; i < visibleLines; i++)
        {
            double height = Math.Max(2, _waveformSamples[startIdx + i] * laneHeight);
            var line = _waveformLinePool[i];
            line.IsVisible = true;
            line.StartPoint = new Point(x, centerY - height / 2);
            line.EndPoint = new Point(x, centerY + height / 2);
            x += 2;
        }

        for (int i = visibleLines; i < _waveformLinePool.Count; i++)
        {
            _waveformLinePool[i].IsVisible = false;
        }
    }

    private void EnsureWaveformLinePool(Canvas canvas, int requiredLines)
    {
        while (_waveformLinePool.Count < requiredLines)
        {
            var line = new Line
            {
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            line[!Line.StrokeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppSuccessBrush");
            _waveformLinePool.Add(line);
            canvas.Children.Add(line);
        }
    }

    private void ApplyAndClose()
    {
        if (_isRecording)
        {
            StopRecordingAndPlayback();
        }
        _recorder?.StopRecording();

        bool muteMale = _muteMaleCb?.IsChecked == true;
        bool muteFemale = _muteFemaleCb?.IsChecked == true;
        bool muteChild = _muteChildCb?.IsChecked == true;
        bool hasMuteEffect = muteMale || muteFemale || muteChild;

        if (!HasSavedVoiceOverSession() && !hasMuteEffect)
        {
            Result = null;
            UpdateApplyState("Nothing to apply yet. Record a take or choose a detected voice mute option.");
            return;
        }
        
        var persistedTakes = new List<VoiceOverTake>();
        var keepPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_sessions.Count > 0)
        {
            if (_applyButton != null) { _applyButton.IsEnabled = false; _applyButton.Content = "SAVING..."; }

            for (int i = 0; i < _sessions.Count; i++)
            {
                var session = _sessions[i];
                if (session.EndSec <= session.StartSec ||
                    string.IsNullOrWhiteSpace(session.WavPath) ||
                    !System.IO.File.Exists(session.WavPath))
                {
                    continue;
                }

                try
                {
                    string persistedPath = CreatePersistedVoiceOverPath();
                    File.Copy(session.WavPath, persistedPath, overwrite: true);
                    persistedTakes.Add(new VoiceOverTake(persistedPath, session.StartSec));
                    keepPaths.Add(persistedPath);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("VoiceOver", $"Voiceover take {i + 1} could not be persisted. {ex.Message}");
                    Result = null;
                    if (_applyButton != null) _applyButton.Content = "APPLY & CLOSE";
                    UpdateApplyState($"Voiceover take {i + 1} could not be saved. Record another take or choose a mute option.");
                    return;
                }
            }

            if (_applyButton != null) _applyButton.Content = "APPLY & CLOSE";
            DeleteSessionFilesExcept(keepPaths);
        }

        if (persistedTakes.Count == 0 && !hasMuteEffect)
        {
            Result = null;
            UpdateApplyState("Voiceover audio could not be prepared. Record another take or choose a mute option.");
            return;
        }

        string? finalWav = persistedTakes.Count > 0 ? persistedTakes[0].Path : null;
        double finalStart = persistedTakes.Count > 0 ? persistedTakes[0].StartSec : 0;

        Result = new VoiceOverResult
        {
            VoiceOverWavPath = finalWav,
            VoiceOverStartTimestampSec = finalStart,
            VoiceOverTakes = persistedTakes,
            MuteMale = muteMale,
            MuteFemale = muteFemale,
            MuteChild = muteChild,
            MaleFrequencyHz = _detectedMaleHz,
            FemaleFrequencyHz = _detectedFemaleHz,
            ChildFrequencyHz = _detectedChildHz
        };

        Close();
    }

    private void DeleteSessionFilesExcept(IReadOnlySet<string> keepPaths)
    {
        foreach (var session in _sessions)
        {
            if (!keepPaths.Contains(session.WavPath))
            {
                TryDeleteFile(session.WavPath);
            }
        }
        if (!keepPaths.Contains(_outputWavPath))
        {
            TryDeleteFile(_outputWavPath);
        }
    }

    private void DeleteUnappliedVoiceOverFiles()
    {
        var appliedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Result?.VoiceOverWavPath))
        {
            appliedPaths.Add(Result!.VoiceOverWavPath!);
        }
        if (Result?.VoiceOverTakes != null)
        {
            foreach (var take in Result.VoiceOverTakes)
            {
                if (!string.IsNullOrWhiteSpace(take.Path))
                {
                    appliedPaths.Add(take.Path);
                }
            }
        }

        foreach (var session in _sessions)
        {
            if (!appliedPaths.Contains(session.WavPath))
            {
                TryDeleteFile(session.WavPath);
            }
        }

        if (_currentSession != null)
        {
            if (!appliedPaths.Contains(_currentSession.WavPath))
            {
                TryDeleteFile(_currentSession.WavPath);
            }
        }

        if (!appliedPaths.Contains(_outputWavPath))
        {
            TryDeleteFile(_outputWavPath);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosing = true;
        _generationCts?.Cancel();
        _probeCts?.Cancel();
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        if (_recorder != null)
        {
            _recorder.VolumeChanged -= OnVolumeChanged;
            _recorder.Dispose();
        }
        DeleteUnappliedVoiceOverFiles();
        
        if (_tempThumbPath != null && System.IO.File.Exists(_tempThumbPath))
        {
            try { System.IO.File.Delete(_tempThumbPath); } catch { }
        }
        if (_tempWavePath != null && System.IO.File.Exists(_tempWavePath))
        {
            try { System.IO.File.Delete(_tempWavePath); } catch { }
        }

        _probeCts?.Dispose();
        _videoHost?.Dispose();
        base.OnClosed(e);
    }
}
