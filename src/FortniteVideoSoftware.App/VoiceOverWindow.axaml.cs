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

    public static readonly Avalonia.StyledProperty<bool> IsDuckAudioProperty =
        Avalonia.AvaloniaProperty.Register<VoiceOverWindow, bool>(nameof(IsDuckAudio), false);
    public bool IsDuckAudio
    {
        get => GetValue(IsDuckAudioProperty);
        set => SetValue(IsDuckAudioProperty, value);
    }

    private MpvVideoView? _videoHost;
    private VoiceRecorder? _recorder;

    /// <summary>
    /// AUDIO_01: live while the microphone is open. Disposing it re-enables UI sounds.
    /// Held as a field rather than a `using` because recording spans two separate user actions
    /// (press to start, press again to stop).
    /// </summary>
    private IDisposable? _uiSoundMute;
    private readonly ApplicationPaths _paths = ApplicationPaths.CreateDefault();
    private string _videoPath = "";
    private string _outputWavPath = "";

    private bool _isRecording = false;
    private bool _isMpvReady = false;
    private bool _isClosing = false;
    private bool _isSeeking = false;
    private double? _nextSeekTarget = null;
    private DispatcherTimer _timer;

    private List<float> _waveformSamples = new();
    private double _smoothedVolume = 0;
    private double _peakVolume = 0;

    private string? _tempThumbPath;
    private string? _tempWavePath;
    private System.Threading.CancellationTokenSource? _generationCts;


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
    private double? _dragSeekTimeSec = null;
    private bool _isVKeyPressed = false;
    private bool _isSpaceKeyPressed = false;

    private class VoiceOverSession
    {
        public string WavPath { get; set; } = "";
        public double StartSec { get; set; }
        public double EndSec { get; set; }
        public double TrimLeftSec { get; set; } = 0;
        public double TrimRightSec { get; set; } = 0;
        public bool IsMuted { get; set; } = false;
        public double RenderStartSec => StartSec + TrimLeftSec;
        public double RenderEndSec => EndSec - TrimRightSec;
    }
    private List<VoiceOverSession> _sessions = new();
    private VoiceOverSession? _currentSession;
    private VoiceOverSession? _selectedSession;
    private VoiceOverSession? _draggingSession;
    private bool _isDraggingStartEdge;
    private bool _isDraggingEndEdge;
    private readonly List<Line> _waveformLinePool = new();
    private Rectangle? _currentSessionRegionRect;
    private Polygon? _playheadCaret;
    private Line? _rulerPlayheadLine;
    private int _renderedSessionCount = -1;
    private double _renderedRulerWidth = -1;
    private double _renderedRulerHeight = -1;

    private sealed class PreviewPlayer : IDisposable
    {
        public NAudio.Wave.AudioFileReader Reader { get; }
        public NAudio.Wave.WaveOutEvent Player { get; }
        public VoiceOverSession Session { get; }

        public PreviewPlayer(VoiceOverSession session, float previewGain = 1.0f)
        {
            Session = session;
            Reader = new NAudio.Wave.AudioFileReader(session.WavPath);

            Reader.Volume = previewGain;

            Player = new NAudio.Wave.WaveOutEvent();
            Player.Init(Reader);
        }

        public void Dispose()
        {
            try { Player.Stop(); Player.Dispose(); } catch (System.Exception) { }
            try { Reader.Dispose(); } catch (System.Exception) { }
        }
    }
    private List<PreviewPlayer> _previewPlayers = new();

    private readonly Dictionary<string, float> _takePreviewGain = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _takeGainPending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// PREVIEW_03 — the clip's measured loudness, supplied by the Main App.
    ///
    /// The export lifts the game bus to TargetLufs and aims the voice at the same figure, so the
    /// two land together. This window plays the video RAW through mpv, so without this the voice
    /// would be normalised against a game that had not moved — the voice would sound too loud here
    /// by exactly the boost the export was going to apply, and the user would turn it down to
    /// compensate for a problem that only exists in the preview.
    ///
    /// The offset shifts the VOICE down by whatever boost the video is missing, reproducing the
    /// export's relationship. NAudio has no 0-100 ceiling, but going DOWN is still the right
    /// direction: it keeps the preview honest without ever amplifying a noisy take.
    /// </summary>
    public double? SourceMeasuredLufs { get; set; }

    private double VoicePreviewOffsetDb => 0.0;

    private float GetTakePreviewGain(VoiceOverSession session)
    {
        return 1.0f;
    }




    private Button? _micRecordButton;
    private Button? _playPauseButton;
    private Button? _applyButton;
    private Button? _cancelButton;
    private ComboBox? _micDeviceComboBox;
    private TextBlock? _recordingStatusText;
    private TextBlock? _voiceOverHintText;
    private TextBlock? _thumbFallbackText;
    private TextBlock? _waveformFallbackText;
    private Ellipse? _recordingLight;

    private Border? _eqMeterTrack;
    private Canvas? _timelineRulerCanvas;
    private Canvas? _liveWaveformMonitor;
    private Canvas? _waveformCanvas;
    private Grid? _thumbnailLaneGrid;
    private Grid? _waveformLaneGrid;
    private Image? _thumbnailLaneImage;
    private Image? _waveformLaneImage;
    private Border? _thumbLoadingOverlay;
    private Border? _waveformLoadingOverlay;
    private Avalonia.Controls.Shapes.Path? _playIcon;
    private Avalonia.Controls.Shapes.Path? _pauseIcon;
    private CheckBox? _duckAudioCb;
    private Border? _thumbPlayheadLine;
    private Border? _wavePlayheadLine;

    private FortniteVideoSoftware.Core.Media.OutputTimeline? _timeline;
    public VoiceOverResult? Result { get; private set; }
    public VoiceOverResult? InitialState { get; set; }

    public class VoiceOverResult
    {
        public string? VoiceOverWavPath { get; set; }
        public double VoiceOverStartTimestampSec { get; set; }
        public List<VoiceOverTake> VoiceOverTakes { get; set; } = new();
        public bool DuckAudio { get; set; }
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

    private bool _isSafeToClose = false;

    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isSafeToClose) return;

        if (_isRecording || HasSavedVoiceOverSession())
        {
            e.Cancel = true;
            var dialog = new FortniteVideoSoftware.App.Controls.ConfirmDialogWindow();
            dialog.SetTitle("DISCARD RECORDINGS?");
            dialog.SetMessage("You have unsaved voiceover takes. Are you sure you want to discard them and close?");
            dialog.SetButtonText("DISCARD", "KEEP EDITING");
            var top = Avalonia.Controls.TopLevel.GetTopLevel(this) as Window;
            if (top != null)
            {
                await dialog.ShowDialog(top);
                if (dialog.Result)
                {
                    _isSafeToClose = true;
                    Close();
                }
            }
            return;
        }

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

        Loaded += (_, _) => Controls.CoachOverlay.Register(this, Controls.CoachTours.VoiceOverKey, Controls.CoachTours.VoiceOver);

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
                        _videoHost.IpcClient.SeekCompleted -= OnSeekCompleted;
                        _videoHost.IpcClient.SeekCompleted += OnSeekCompleted;
                        
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

                        double videoDuration = _videoHost.IpcClient.Duration;
                        double effectiveDuration = (_trimEndSec > 0 ? _trimEndSec : videoDuration) - _trimStartSec;
                        if (effectiveDuration <= 0) effectiveDuration = videoDuration;
                        _timeline = FortniteVideoSoftware.Core.Media.OutputTimeline.Create(
                            effectiveDuration * 1000.0,
                            _speedSegments,
                            _baseSpeed,
                            _trimStartSec * 1000.0);
                        
                        previewReady = true;
                        
                        if (InitialState != null)
                        {
                            var cb = this.FindControl<CheckBox>("DuckAudioCb");
                            if (cb != null) cb.IsChecked = InitialState.DuckAudio;
                            if (InitialState.VoiceOverTakes != null)
                            {
                                foreach (var t in InitialState.VoiceOverTakes)
                                {
                                    if (System.IO.File.Exists(t.Path))
                                    {
                                        double dur = 0.1;
                                        try { using var af = new NAudio.Wave.AudioFileReader(t.Path); dur = af.TotalTime.TotalSeconds; } catch {}
                                        _sessions.Add(new VoiceOverSession { WavPath = t.Path, StartSec = t.StartSec, EndSec = t.StartSec + dur });
                                        _renderedSessionCount = -1;
                                    }
                                }
                            }
                            UpdatePlayheadUI();
                        }
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("VoiceOver", $"Preview startup failed. Recording disabled, auto-ducking remains available. {ex.Message}");
                    _videoHost.Dispose();
                    if (_recordingStatusText != null)
                    {
                        _recordingStatusText.Text = "PREVIEW OFF";
                        _recordingStatusText.Foreground = GetAppBrush("AppWarningBrush", Brushes.Yellow);
                    }
                    UpdateApplyState(previewReady ? null : "Preview could not start on this graphics session.");
                }

                // ══════════════════════════════════════════════════════════════════════════════
                // VOICE_01 — THE TRANSPORT WAS PERMANENTLY DEAD ON THIS SCREEN.
                // `_isMpvReady` gates PlayPauseButton and MicRecordButton (UpdateTransportState),
                // and both start IsEnabled="False" in XAML. The field was declared `= false` and
                // READ in four places — but never once ASSIGNED. A refactor left the readiness
                // signal in this local `previewReady` and orphaned the field the UI depends on, so
                // Play and Record could never enable and the only working control was CANCEL.
                // Nothing about it looks broken at a glance, which is why it survived: the flag
                // exists, is named correctly, and is wired to exactly the right controls.
                // ══════════════════════════════════════════════════════════════════════════════
                _isMpvReady = previewReady;

                UpdateTransportState();
                UpdateApplyState(previewReady ? null : "Preview could not start on this graphics session.");

                if (previewReady)
                {
                    _ = GenerateLanesAsync();
                }
            }
        };
    }

    /// <summary>VOICE_02 — the take count the current player list was built for. -1 = invalid.</summary>
    private int _previewPlayersBuiltForCount = -1;

    private void StopPreviewPlayers()
    {
        foreach (var player in _previewPlayers)
        {
            player.Dispose();
        }
        _previewPlayers.Clear();
        _previewPlayersBuiltForCount = -1;
    }

    private void UpdatePreviewPlayers()
    {
        if (_videoHost?.IpcClient == null) return;
        
        bool isMpvPaused = _videoHost.IpcClient.IsPaused;
        bool shouldPlay = (!isMpvPaused || _isCurrentlyFrozen) && !_isRecording;
        double time = _videoHost.IpcClient.CurrentTime;
        
        double elapsedFreezeSec = 0;
        if (_isCurrentlyFrozen)
        {
            elapsedFreezeSec = (DateTime.UtcNow - _freezeStartTime).TotalSeconds;
        }

        // VOICE_02 — THE REBUILD TRIGGER MUST NOT BE THE LIVE PLAYER COUNT. It compared
        // `_sessions.Count != _previewPlayers.Count`, so a take whose WAV was missing or whose
        // WaveOutEvent failed to open left the two counts permanently unequal: every 50 ms tick
        // disposed and recreated EVERY player, nothing survived long enough to be heard, and
        // the log filled with the same failure. Track what the last rebuild was FOR, not what
        // it achieved. `_previewPlayersBuiltForCount` is reset to -1 by StopPreviewPlayers so
        // an external invalidation (MeasureTakeGainAsync) still forces exactly one rebuild.
        if (_sessions.Count != _previewPlayersBuiltForCount)
        {
            StopPreviewPlayers();
            foreach (var session in _sessions)
            {
                if (System.IO.File.Exists(session.WavPath))
                {
                    try { _previewPlayers.Add(new PreviewPlayer(session, GetTakePreviewGain(session))); }
                    catch (System.Exception __ex)
                    {
                        RuntimeLog.Fail("VoiceOver", $"A recorded take could not be opened for preview: {__ex.Message}");
                        RuntimeLog.Swallowed(__ex);
                    }
                }
            }
            _previewPlayersBuiltForCount = _sessions.Count;
        }

        foreach (var player in _previewPlayers)
        {
            var take = player.Session;
            if (take.IsMuted)
            {
                if (player.Player.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                    player.Player.Pause();
                continue;
            }

            bool shouldPlayVoice = shouldPlay && time >= take.RenderStartSec && time <= take.RenderEndSec;
            
            double mappedTime = _timeline != null ? _timeline.SourceToOutput(time) : time;
            mappedTime += elapsedFreezeSec;
            
            double mappedStart = _timeline != null ? _timeline.SourceToOutput(take.StartSec) : take.StartSec;
            double mappedOffset = mappedTime - mappedStart;
            
            if (shouldPlayVoice && player.Player.PlaybackState != NAudio.Wave.PlaybackState.Playing)
            {
                if (mappedOffset >= 0 && mappedOffset < player.Reader.TotalTime.TotalSeconds)
                {
                    try { player.Reader.CurrentTime = TimeSpan.FromSeconds(mappedOffset); } catch (System.Exception __ex) { RuntimeLog.SwallowedThrottled(__ex); }
                }
                player.Player.Play();
            }
            else if (!shouldPlayVoice && player.Player.PlaybackState == NAudio.Wave.PlaybackState.Playing)
            {
                player.Player.Pause();
            }
            else if (shouldPlayVoice && player.Player.PlaybackState == NAudio.Wave.PlaybackState.Playing)
            {
                double expectedPos = mappedOffset;
                double actualPos = player.Reader.CurrentTime.TotalSeconds;
                if (Math.Abs(expectedPos - actualPos) > 0.15)
                {
                    try { player.Reader.CurrentTime = TimeSpan.FromSeconds(Math.Max(0, expectedPos)); } catch (System.Exception __ex) { RuntimeLog.SwallowedThrottled(__ex); }
                }
            }
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        PumpRecordArming();
        UpdateWaveformUI();
        UpdatePlayPauseIconUI();
        UpdatePlayheadUI();
        UpdatePreviewPlayers();
        UpdateSmoothEqMeter();
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
                try { BeginMoveDrag(e); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            }
        };
    }

    private void CacheControls()
    {
        _videoHost = this.FindControl<MpvVideoView>("VideoHost");
        WirePreviewDetach();
        _micRecordButton = this.FindControl<Button>("MicRecordButton");
        _pauseResumeButton = this.FindControl<Button>("RecordPauseButton");
        if (_pauseResumeButton != null) _pauseResumeButton.Click += ToggleRecordPause;
        _playPauseButton = this.FindControl<Button>("PlayPauseButton");
        _applyButton = this.FindControl<Button>("ApplyButton");
        _cancelButton = this.FindControl<Button>("CancelButton");

        var voHelpButton = this.FindControl<Button>("VoiceOverHelpButton");
        if (voHelpButton != null) voHelpButton.Click += (_, _) => Controls.CoachOverlay.Replay(this);

        _micDeviceComboBox = this.FindControl<ComboBox>("MicDeviceComboBox");
        _recordingStatusText = this.FindControl<TextBlock>("RecordingStatusText");
        _voiceOverHintText = this.FindControl<TextBlock>("VoiceOverHintText");
        _thumbFallbackText = this.FindControl<TextBlock>("ThumbFallbackText");
        _waveformFallbackText = this.FindControl<TextBlock>("WaveformFallbackText");
        _recordingLight = this.FindControl<Ellipse>("RecordingLight");
        _eqMeterCanvas = this.FindControl<Canvas>("EqMeterCanvas");
        _eqMeterTrack = this.FindControl<Border>("EqMeterTrack");
        _timelineRulerCanvas = this.FindControl<Canvas>("TimelineRulerCanvas");
        _liveWaveformMonitor = this.FindControl<Canvas>("LiveWaveformMonitor");
        _waveformCanvas = this.FindControl<Canvas>("WaveformCanvas");
        _thumbnailLaneGrid = this.FindControl<Grid>("ThumbnailLaneGrid");
        _waveformLaneGrid = this.FindControl<Grid>("WaveformLaneGrid");
        _thumbnailLaneImage = this.FindControl<Image>("ThumbnailLaneImage");
        _waveformLaneImage = this.FindControl<Image>("WaveformLaneImage");
        _thumbLoadingOverlay = this.FindControl<Border>("ThumbLoadingOverlay");
        _waveformLoadingOverlay = this.FindControl<Border>("WaveformLoadingOverlay");
        _playIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon");
        _pauseIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PauseIcon");
        _duckAudioCb = this.FindControl<CheckBox>("DuckAudioCb");
        _thumbPlayheadLine = this.FindControl<Border>("ThumbPlayheadLine");
        _wavePlayheadLine = this.FindControl<Border>("WavePlayheadLine");
        
        var selectedTakeToolbar = this.FindControl<Border>("SelectedTakeToolbar");
        var muteTakeBtn = this.FindControl<Button>("MuteTakeButton");
        var deleteTakeBtn = this.FindControl<Button>("DeleteTakeButton");

        if (muteTakeBtn != null)
        {
            muteTakeBtn.Click += (s, e) =>
            {
                if (_selectedSession != null)
                {
                    _selectedSession.IsMuted = !_selectedSession.IsMuted;
                    muteTakeBtn.Content = _selectedSession.IsMuted ? "UNMUTE" : "MUTE";
                    muteTakeBtn.Classes.Remove("Secondary");
                    muteTakeBtn.Classes.Remove("Primary");
                    muteTakeBtn.Classes.Add(_selectedSession.IsMuted ? "Primary" : "Secondary");
                    _renderedSessionCount = -1;
                    UpdatePlayheadUI();
                    UpdateApplyState();
                    Controls.FloatingNotice.Info(this, _selectedSession.IsMuted ? "Take muted" : "Take unmuted");
                }
            };
        }

        if (deleteTakeBtn != null)
        {
            deleteTakeBtn.Click += (s, e) =>
            {
                if (_selectedSession != null)
                {
                    bool isInitial = InitialState?.VoiceOverTakes?.Any(t => string.Equals(t.Path, _selectedSession.WavPath, StringComparison.OrdinalIgnoreCase)) == true;
                    if (!isInitial) TryDeleteFile(_selectedSession.WavPath);
                    _sessions.Remove(_selectedSession);
                    _selectedSession = null;
                    if (selectedTakeToolbar != null) selectedTakeToolbar.IsVisible = false;
                    _renderedSessionCount = -1;
                    UpdatePlayheadUI();
                    UpdateApplyState();
                    Controls.FloatingNotice.Show(this, "Take deleted");
                }
            };
        }
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
            catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        };
    }

    private void WireTimelineSeekSurface(Control surface)
    {
        surface.PointerPressed += (s, e) =>
        {
            surface.Focus();
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
        if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            if (_selectedSession != null)
            {
                bool isInitial = InitialState?.VoiceOverTakes?.Any(t => string.Equals(t.Path, _selectedSession.WavPath, StringComparison.OrdinalIgnoreCase)) == true;
                if (!isInitial) TryDeleteFile(_selectedSession.WavPath);
                _sessions.Remove(_selectedSession);
                _selectedSession = null;
                _renderedSessionCount = -1;
                UpdatePlayheadUI();
                UpdateApplyState();
                e.Handled = true;
                return;
            }
        }

        if ((_isRecording && !_recordPaused) || _videoHost?.IpcClient == null) return;

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
        
        if (_isSeeking)
        {
            _nextSeekTarget = seconds;
            return;
        }

        _isSeeking = true;
        try
        {
            _ = _videoHost.IpcClient.SendCommandAsync("seek", seconds, "absolute");
        }
        catch
        {
            _isSeeking = false;
        }
    }

    private void OnSeekCompleted()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _isSeeking = false;
            if (_nextSeekTarget.HasValue)
            {
                double target = _nextSeekTarget.Value;
                _nextSeekTarget = null;
                SeekToAbsolute(target);
            }
        });
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
        if (_duckAudioCb != null)
        {
            _duckAudioCb.IsCheckedChanged += (_, _) => UpdateApplyState();
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
        if (_playPauseButton != null)
        {
            _playPauseButton.IsEnabled = _isMpvReady && !_isRecording;
            _playPauseButton.IsVisible = !_isRecording;
        }
        if (_micDeviceComboBox != null) _micDeviceComboBox.IsEnabled = hasInputDevice && !_isRecording;

        if (_pauseResumeButton != null)
        {
            _pauseResumeButton.IsVisible = _isRecording;
            _pauseResumeButton.IsEnabled = _isRecording && !_recordArming;
            var rpi = this.FindControl<Avalonia.Controls.Shapes.Path>("RecordPauseIcon");
            var rri = this.FindControl<Avalonia.Controls.Shapes.Path>("RecordResumeIcon");
            if (rpi != null) rpi.IsVisible = !_recordPaused;
            if (rri != null) rri.IsVisible = _recordPaused;
        }

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
            if (!session.IsMuted &&
                session.EndSec > session.StartSec &&
                !string.IsNullOrWhiteSpace(session.WavPath) &&
                System.IO.File.Exists(session.WavPath))
            {
                return true;
            }
        }
        return false;
    }

    private bool HasApplicableVoiceEffect()
    {
        return _isRecording || HasSavedVoiceOverSession();
    }

    private void UpdateApplyState(string? message = null)
    {
        bool canApply = HasApplicableVoiceEffect();
        string? effectiveMessage = message;
        if (effectiveMessage == null && !canApply && !VoiceRecorder.HasInputDevice)
        {
            effectiveMessage = "No microphone input detected. You must record a take to apply voiceover.";
        }

        if (_applyButton != null)
        {
            _applyButton.IsEnabled = canApply;
            ToolTip.SetTip(_applyButton, canApply
                ? "Apply recorded voiceover"
                : "Record a take before applying");
        }

        if (_voiceOverHintText != null)
        {
            _voiceOverHintText.Text = effectiveMessage ?? (canApply
                ? "Ready to apply the current voiceover changes. Cancel discards unapplied takes."
                : "Record a take before applying. Cancel discards unapplied takes.");
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

        RetireGenerationCts(_generationCts);
        _generationCts = new System.Threading.CancellationTokenSource();
        var token = _generationCts.Token;

        if (_thumbFallbackText != null) _thumbFallbackText.IsVisible = false;
        if (_waveformFallbackText != null) _waveformFallbackText.IsVisible = false;
        if (_thumbLoadingOverlay != null) _thumbLoadingOverlay.IsVisible = true;
        if (_waveformLoadingOverlay != null) _waveformLoadingOverlay.IsVisible = true;

        string localVideoPath = _videoPath ?? "";
        double localTrimStart = _trimStartSec;

        // ══════════════════════════════════════════════════════════════════════════════════════
        // STRIP_01 — this was a SECOND, WORSE copy of the filmstrip command, inline. It:
        //   * decoded the entire range to keep 15 frames, exactly like the shared generator used
        //     to (that is the delay the user reported on this screen);
        //   * asked `fps` for 16 frames while tiling 15, so the strip never contained the frames
        //     it claimed to;
        //   * wrote into the OS temp directory rather than the app's, outside the job cleanup;
        //   * did not redirect stderr, so every failure was silent;
        //   * paid `-hwaccel auto` initialisation for a fifteen-frame job — the same CUDA decoder
        //     init that logs `cuvidCreateDecoder ... CUDA_ERROR_INVALID_VALUE` on this hardware.
        // It now calls the one implementation, which picks its own strategy by sample spacing.
        // ══════════════════════════════════════════════════════════════════════════════════════
        string thumbTempDir = FortniteVideoSoftware.Core.Infrastructure.ApplicationPaths.CreateDefault().TempDirectory;
        // PREWARM_01 — take the background-rendered strip if the Main App already built this
        // exact range. Null on any mismatch, in which case we stream it as usual.
        var warmedStrip = FortniteVideoSoftware.App.Services.FilmstripPrewarm.TryTake(
            localVideoPath, localTrimStart, durationSec);
        bool warmedMounted = false;
        if (warmedStrip != null)
        {
            if (_thumbnailLaneImage != null)
            {
                (_thumbnailLaneImage.Source as IDisposable)?.Dispose();
                _thumbnailLaneImage.Source = warmedStrip;
                if (_thumbLoadingOverlay != null) _thumbLoadingOverlay.IsVisible = false;
                if (_thumbFallbackText != null) _thumbFallbackText.IsVisible = false;
                warmedMounted = true;
                CoreLogger.Info("VoiceOver", "Thumbnail lane served from the background prewarm.");
            }
            else
            {
                // LEAK_01 — TryTake TRANSFERS OWNERSHIP. With no Image to mount it on we are
                // holding an orphan that nothing else will ever dispose, and reporting success
                // for a lane that was never shown. Release it and render normally.
                try { warmedStrip.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            }
        }

        // STRIP_03 — stream the lane so the first frame is on screen in ~0.2s instead of after the
        // whole strip is rendered. The callbacks are marshalled to the UI thread by StreamAsync.
        var thumbTask = warmedMounted ? Task.FromResult(true) : Task.Run(async () =>
        {
            try
            {
                return await ThumbnailStripGenerator.StreamAsync(
                    ffmpeg, localVideoPath, localTrimStart, durationSec, token,
                    onReady: wb =>
                    {
                        if (_thumbnailLaneImage == null) return;
                        (_thumbnailLaneImage.Source as IDisposable)?.Dispose();
                        _thumbnailLaneImage.Source = wb;
                        if (_thumbLoadingOverlay != null) _thumbLoadingOverlay.IsVisible = false;
                        if (_thumbFallbackText != null) _thumbFallbackText.IsVisible = false;
                    },
                    onFrame: () => _thumbnailLaneImage?.InvalidateVisual(),
                    logTag: "VoiceOver");
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                CoreLogger.Fail("VoiceOver", $"Thumbnail lane generation failed: {ex.Message}");
                return false;
            }
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

        bool thumbStreamed = await thumbTask;
        string? wavePath = await waveTask;

        if (token.IsCancellationRequested) return;

        // STRIP_03 — the stream is the normal path; this only runs when it yielded no frames.
        string? thumbPath = null;
        if (!thumbStreamed)
        {
            try
            {
                thumbPath = await ThumbnailStripGenerator.GenerateAsync(
                    ffmpeg, localVideoPath, thumbTempDir, localTrimStart, durationSec, token,
                    logTag: "VoiceOver");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                CoreLogger.Fail("VoiceOver", $"Thumbnail lane fallback failed: {ex.Message}");
            }
        }

        bool thumbLoaded = thumbStreamed;
        bool waveformLoaded = false;

        if (thumbPath != null && _thumbnailLaneImage != null)
        {
            try
            {
                using var fs = System.IO.File.OpenRead(thumbPath);
                (_thumbnailLaneImage.Source as IDisposable)?.Dispose();
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
                (_waveformLaneImage.Source as IDisposable)?.Dispose();
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

        var toolbar = this.FindControl<Border>("SelectedTakeToolbar");
        if (toolbar != null) toolbar.IsVisible = _selectedSession != null;
        var muteBtn = this.FindControl<Button>("MuteTakeButton");
        if (muteBtn != null && _selectedSession != null)
        {
            muteBtn.Content = _selectedSession.IsMuted ? "UNMUTE" : "MUTE";
            muteBtn.Classes.Remove("Secondary");
            muteBtn.Classes.Remove("Primary");
            muteBtn.Classes.Add(_selectedSession.IsMuted ? "Primary" : "Secondary");
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
            double startFrac = (session.RenderStartSec - _trimStartSec) / effectiveDuration;
            double endFrac = (session.RenderEndSec - _trimStartSec) / effectiveDuration;
            double x1 = Math.Clamp(startFrac * width, 0, width);
            double x2 = Math.Clamp(endFrac * width, 0, width);
            if (x2 <= x1) continue;

            bool isSelected = session == _selectedSession;

            var region = new Rectangle
            {
                Opacity = isSelected ? 0.8 : (session.IsMuted ? 0.2 : 0.4),
                Width = x2 - x1,
                Height = height,
                IsHitTestVisible = true,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            region[!Rectangle.FillProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(session.IsMuted ? "AppBorderBrush" : "AppDangerBrush");
            
            region.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(ruler).Properties.IsLeftButtonPressed)
                {
                    ruler.Focus();
                    _selectedSession = session;
                    _renderedSessionCount = -1;
                    UpdatePlayheadUI();
                    e.Handled = true;
                }
            };
            
            Canvas.SetLeft(region, x1);
            Canvas.SetTop(region, 0);
            ruler.Children.Add(region);

            if (isSelected)
            {
                var leftHandle = new Rectangle
                {
                    Width = 10,
                    Height = height,
                    Fill = Avalonia.Media.Brushes.LimeGreen,
                    IsHitTestVisible = true,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast)
                };
                leftHandle.PointerPressed += (s, e) =>
                {
                    if (e.GetCurrentPoint(ruler).Properties.IsLeftButtonPressed)
                    {
                        _draggingSession = session;
                        _isDraggingStartEdge = true;
                        e.Handled = true;
                    }
                };
                Canvas.SetLeft(leftHandle, Math.Max(0, x1 - 5));
                Canvas.SetTop(leftHandle, 0);
                ruler.Children.Add(leftHandle);

                var rightHandle = new Rectangle
                {
                    Width = 10,
                    Height = height,
                    Fill = Avalonia.Media.Brushes.LimeGreen,
                    IsHitTestVisible = true,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast)
                };
                rightHandle.PointerPressed += (s, e) =>
                {
                    if (e.GetCurrentPoint(ruler).Properties.IsLeftButtonPressed)
                    {
                        _draggingSession = session;
                        _isDraggingEndEdge = true;
                        e.Handled = true;
                    }
                };
                Canvas.SetLeft(rightHandle, Math.Min(width - 10, x2 - 5));
                Canvas.SetTop(rightHandle, 0);
                ruler.Children.Add(rightHandle);
            }
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


    private void SeekTimelineFromPointer(Avalonia.Input.PointerEventArgs e, Avalonia.Controls.Control timelineCanvas, bool force)
    {
        if (e.Handled) return;

        if (_draggingSession != null)
        {
            if (force && e.RoutedEvent == Avalonia.Input.InputElement.PointerReleasedEvent)
            {
                _draggingSession = null;
                _isDraggingStartEdge = false;
                _isDraggingEndEdge = false;
                e.Handled = true;
                return;
            }

            if (_videoHost?.IpcClient == null) return;
            double vDur = _videoHost.IpcClient.Duration;
            double eDur = (_trimEndSec > 0 ? _trimEndSec : vDur) - _trimStartSec;
            double w = timelineCanvas.Bounds.Width;
            double pointX = e.GetCurrentPoint(timelineCanvas).Position.X;
            
            double dragTargetTime = _trimStartSec + (pointX / w) * eDur;

            if (_isDraggingStartEdge)
            {
                double maxStart = _draggingSession.RenderEndSec - 0.1;
                double newStart = Math.Clamp(dragTargetTime, _draggingSession.StartSec, maxStart);
                _draggingSession.TrimLeftSec = newStart - _draggingSession.StartSec;
            }
            else if (_isDraggingEndEdge)
            {
                double minEnd = _draggingSession.RenderStartSec + 0.1;
                double newEnd = Math.Clamp(dragTargetTime, minEnd, _draggingSession.EndSec);
                _draggingSession.TrimRightSec = _draggingSession.EndSec - newEnd;
            }

            _renderedSessionCount = -1;
            UpdatePlayheadUI();
            e.Handled = true;
            return;
        }

        if (_isRecording && !_recordPaused) return;
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
            _lastTimelineSeekUtc = DateTime.UtcNow;
            SeekToAbsolute(targetTime);
            e.Handled = true;
            return;
        }

        _dragSeekTimeSec = targetTime;
        _lastTimelineSeekUtc = DateTime.UtcNow;
        SeekToAbsolute(targetTime);
        e.Handled = true;
        
        _ = Task.Delay(300).ContinueWith(_ => Dispatcher.UIThread.Post(() => 
        {
            if (Math.Abs((_dragSeekTimeSec ?? 0) - targetTime) < 0.001) 
            {
                _dragSeekTimeSec = null;
            }
        }));
    }

    private void OnKeyUpHandler(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Avalonia.Controls.TextBox or Avalonia.Controls.NumericUpDown)
            return;

        if (e.Key == Avalonia.Input.Key.V)
        {
            _isVKeyPressed = false;
        }

        var kb = FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.KeyBinds;
        var playPause = new Avalonia.Input.KeyGesture(kb.PlayPause);
        
        if (playPause.Matches(e) || e.Key is Avalonia.Input.Key.Space or Avalonia.Input.Key.Left or Avalonia.Input.Key.Right)
        {
            if (playPause.Matches(e) || e.Key == Avalonia.Input.Key.Space)
            {
                _isSpaceKeyPressed = false;
            }
            e.Handled = true;
        }
    }

    private void OnKeyDownHandler(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Avalonia.Controls.TextBox or Avalonia.Controls.NumericUpDown)
            return;

        if (e.Key == Avalonia.Input.Key.V)
        {
            if (!_isVKeyPressed)
            {
                _isVKeyPressed = true;
                ToggleRecord(null, null);
            }
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
            if (!_isSpaceKeyPressed)
            {
                _isSpaceKeyPressed = true;
                if (_isRecording) ToggleRecordPause(null, null);
                else TogglePreviewPlayback(null, null);
            }
            e.Handled = true;
        }
        else if (fineSeekFwdCtrl.Matches(e) || fineSeekFwdShift.Matches(e))
        {
            if (_isRecording && !_recordPaused) return;
            _ = _videoHost?.IpcClient?.SendCommandAsync("frame-step");
            e.Handled = true;
        }
        else if (fineSeekBackCtrl.Matches(e) || fineSeekBackShift.Matches(e))
        {
            if (_isRecording && !_recordPaused) return;
            _ = _videoHost?.IpcClient?.SendCommandAsync("frame-back-step");
            e.Handled = true;
        }
        else if (seekFwd.Matches(e))
        {
            if (_isRecording && !_recordPaused) return;
            _ = _videoHost?.IpcClient?.SendCommandAsync("seek", "5", "relative");
            e.Handled = true;
        }
        else if (seekBack.Matches(e))
        {
            if (_isRecording && !_recordPaused) return;
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
        try
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
        catch (Exception ex)
        {
            RuntimeLog.Fail("VoiceOver", $"Error toggling preview playback: {ex.Message}");
        }
    }


    /// <summary>True between "user pressed record" and "the video clock actually moved".</summary>
    private bool _recordArming;

    /// <summary>Video position observed on the previous arming tick, to detect real movement.</summary>
    private double _armPrevTime;

    /// <summary>Give up arming if the clock never moves (end of clip, stuck decoder).</summary>
    private DateTime _armDeadlineUtc;

    /// <summary>True while the user has paused an in-progress recording session.</summary>
    private bool _recordPaused;

    private Button? _pauseResumeButton;

    private void StartRecordingAndPlayback()
    {
        if (!VoiceRecorder.HasInputDevice)
        {
            ShowMicrophoneUnavailable("No microphone input device is available.");
            return;
        }

        double currentPreviewTime = _videoHost?.IpcClient?.CurrentTime ?? _trimStartSec;
        double recordingStart = NormalizePreviewPlaybackPosition(currentPreviewTime);
        if (Math.Abs(recordingStart - currentPreviewTime) > 0.01)
        {
            _ = _videoHost?.IpcClient?.SetPropertyAsync("time-pos", recordingStart.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        _isCurrentlyFrozen = false;
        _lastFreezeTriggerMs = -1;
        ApplyPreviewSpeedForPosition(recordingStart * 1000.0);

        _waveformSamples.Clear();
        _recordPaused = false;
        _isRecording = true;

        if (!ArmTakeSegment(recordingStart)) return;

        UpdateRecordingUi("ARMING", "AppWarningBrush");
        UpdateApplyState("Recording starts the moment the video rolls. Apply will save the take and close.");
    }

    /// <summary>
    /// Opens a NEW take: fresh WAV file, fresh session, and arms the anchor. Called both when
    /// recording starts and on every resume, which is what keeps segments independent.
    /// Returns false if the take could not be prepared (caller has already been told why).
    /// </summary>
    private bool ArmTakeSegment(double provisionalStartSec)
    {
        ReleaseRecorder();

        _outputWavPath = CreateTempVoiceOverPath();
        _currentSession = new VoiceOverSession
        {
            WavPath = _outputWavPath,
            StartSec = provisionalStartSec
        };

        _uiSoundMute?.Dispose();
        _uiSoundMute = UiSoundEffect.Suppress();

        _recordArming = true;
        _armPrevTime = _videoHost?.IpcClient?.CurrentTime ?? provisionalStartSec;
        _armDeadlineUtc = DateTime.UtcNow.AddSeconds(3);

        _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "no");

        if (_recordingLight != null) _recordingLight.Opacity = 0.6;
        // VOICE_02 — the `recording` class is now owned solely by UpdateRecordingUi and means
        // MICROPHONE OPEN. Arming is not recording; adding it here lit the pulse before a
        // single sample had been captured, which is the opposite of an honest indicator.
        UpdateTransportState();
        return true;
    }

    /// <summary>
    /// Runs on the 50 ms UI timer while armed. Opens the microphone on the first tick where the
    /// video clock has genuinely moved, and stamps the take's anchor from that same reading.
    /// </summary>
    private void PumpRecordArming()
    {
        if (!_recordArming) return;

        var ipc = _videoHost?.IpcClient;
        if (ipc == null) return;

        // VOICE_02 — THIS BRANCH USED TO RETURN WITHOUT CHECKING THE DEADLINE. A preview that
        // reported paused or frozen from the instant record was pressed left the window armed
        // FOREVER: the status text sat on "ARMING", the microphone was never opened, and the
        // empty WAV was silently discarded by FinalizeCurrentTake. Both "it does not show that
        // it is recording" and "there is no voice on playback" come out of this one path.
        // The deadline must be evaluated on EVERY tick, not only on the clock-stalled branch.
        if (_isCurrentlyFrozen || ipc.IsPaused)
        {
            _armPrevTime = ipc.CurrentTime;
            if (DateTime.UtcNow > _armDeadlineUtc)
            {
                RuntimeLog.Fail("VoiceOver", "Recording could not start: the preview never left the paused/frozen state.");
                _recordArming = false;
                AbortActiveTake();
                ShowMicrophoneUnavailable("The video would not start playing, so recording was cancelled.");
            }
            return;
        }

        double now = ipc.CurrentTime;
        if (now <= _armPrevTime + 1e-4)
        {
            if (DateTime.UtcNow > _armDeadlineUtc)
            {
                RuntimeLog.Fail("VoiceOver", "Recording could not start: the video clock never advanced.");
                _recordArming = false;
                AbortActiveTake();
                ShowMicrophoneUnavailable("The video would not start playing, so recording was cancelled.");
            }
            _armPrevTime = now;
            return;
        }

        var recorder = new VoiceRecorder(_outputWavPath, GetSelectedMicrophoneDeviceIndex());
        recorder.VolumeChanged += OnVolumeChanged;
        try
        {
            recorder.StartRecording();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("VoiceOver", $"Microphone recording could not start. {ex.Message}");
            recorder.VolumeChanged -= OnVolumeChanged;
            recorder.Dispose();
            _recordArming = false;
            AbortActiveTake();
            ShowMicrophoneUnavailable("Microphone recording could not start on this PC.");
            return;
        }

        _recorder = recorder;
        _recordArming = false;

        if (_currentSession != null) _currentSession.StartSec = now;
        RuntimeLog.Info("VoiceOver", $"Take armed and anchored at {now:0.###}s (source time).");

        UpdateRecordingUi("RECORDING", "AppDangerBrush");
        UpdateApplyState("Recording in progress. Apply will save the current take and close.");
    }

    /// <summary>Pause: close the current take cleanly and stop the video. Resume opens a new one.</summary>
    private void PauseRecordingSegment()
    {
        if (!_isRecording || _recordPaused) return;

        _recordArming = false;
        FinalizeCurrentTake();
        _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");

        _recordPaused = true;
        UpdateRecordingUi("PAUSED", "AppWarningBrush");
        UpdateTransportState();
        UpdateApplyState("Recording paused. Resume to add another part, or Apply to save what you have.");
    }

    /// <summary>Resume: a brand-new take, re-armed and re-anchored, so drift cannot accumulate.</summary>
    private void ResumeRecordingSegment()
    {
        if (!_isRecording || !_recordPaused) return;
        if (!VoiceRecorder.HasInputDevice)
        {
            ShowMicrophoneUnavailable("No microphone input device is available.");
            return;
        }

        _recordPaused = false;
        double resumeAt = _videoHost?.IpcClient?.CurrentTime ?? _trimStartSec;
        if (!ArmTakeSegment(resumeAt)) return;

        UpdateRecordingUi("ARMING", "AppWarningBrush");
        UpdateApplyState("Recording resumes the moment the video rolls again.");
    }

    private void ToggleRecordPause(object? sender, Avalonia.Interactivity.RoutedEventArgs? e)
    {
        if (!_isRecording) return;
        if (_recordPaused) ResumeRecordingSegment();
        else PauseRecordingSegment();
    }

    /// <summary>
    /// Closes the open take and keeps it only if the microphone actually captured something.
    /// A take that was still ARMING never opened the mic, so its empty WAV is discarded — that
    /// is what stops a stray arm/abort cycle from injecting a zero-length take into the export.
    /// </summary>
    private void FinalizeCurrentTake()
    {
        bool captured = _recorder != null;
        ReleaseRecorder();

        if (_currentSession == null) return;

        double dur = 0;
        try 
        { 
            if (System.IO.File.Exists(_currentSession.WavPath))
            {
                using var af = new NAudio.Wave.AudioFileReader(_currentSession.WavPath); 
                dur = af.TotalTime.TotalSeconds; 
            }
        } 
        catch {}
        
        _currentSession.EndSec = _currentSession.StartSec + dur;

        if (captured &&
            _currentSession.EndSec > _currentSession.StartSec + 0.05 &&
            System.IO.File.Exists(_currentSession.WavPath))
        {
            _sessions.Add(_currentSession);
            _renderedSessionCount = -1;
            RuntimeLog.Info("VoiceOver",
                $"Take saved: {_currentSession.StartSec:0.###}s -> {_currentSession.EndSec:0.###}s (source time).");
            Controls.FloatingNotice.Success(this, $"Take saved — {_currentSession.EndSec - _currentSession.StartSec:0.0}s");
        }
        else
        {
            TryDeleteFile(_currentSession.WavPath);
            _lastTakeWasRejected = true;
            Controls.FloatingNotice.Error(this, "That take was too short to keep");
        }

        _currentSession = null;
    }

    /// <summary>Throws away the take being armed (never captured anything).</summary>
    private void AbortActiveTake()
    {
        ReleaseRecorder();
        if (_currentSession != null)
        {
            TryDeleteFile(_currentSession.WavPath);
            _currentSession = null;
        }
        _isRecording = false;
        _recordPaused = false;
        _uiSoundMute?.Dispose();
        _uiSoundMute = null;
    }

    private void ReleaseRecorder()
    {
        if (_recorder == null) return;
        _recorder.VolumeChanged -= OnVolumeChanged;
        try { _recorder.StopRecording(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        _recorder.Dispose();
        _recorder = null;
    }

    /// <summary>Set by FinalizeCurrentTake when a take was discarded, so the UI can explain why.</summary>
    private bool _lastTakeWasRejected;

    private void UpdateRecordingUi(string status, string brushKey)
    {
        // VOICE_02 — THE PULSE IS THE INDICATION. Opacity alone (0.2 / 0.6 / 1.0 on a
        // permanently red 16px dot) made ARMING and PAUSED identical and RECORDING barely
        // distinguishable from idle, and the `recording` class the mic button has always
        // toggled matched NO style in the whole source tree, so the button never changed at
        // all. The class now drives the two animations in AvaloniaApp.axaml.
        bool live = status == "RECORDING";
        if (_recordingLight != null)
        {
            _recordingLight.Opacity = live ? 1.0 : 0.6;
            _recordingLight.Classes.Remove("recording");
            if (live) _recordingLight.Classes.Add("recording");
        }
        if (_micRecordButton != null)
        {
            _micRecordButton.Classes.Remove("recording");
            if (live) _micRecordButton.Classes.Add("recording");
        }
        if (_recordingStatusText != null)
        {
            _recordingStatusText.Text = status;
            _recordingStatusText.Foreground = GetAppBrush(brushKey, Brushes.White);
        }
        if (_pauseResumeButton != null)
        {
            _pauseResumeButton.IsVisible = _isRecording;
            var rpi = this.FindControl<Avalonia.Controls.Shapes.Path>("RecordPauseIcon");
            var rri = this.FindControl<Avalonia.Controls.Shapes.Path>("RecordResumeIcon");
            if (rpi != null) rpi.IsVisible = !_recordPaused;
            if (rri != null) rri.IsVisible = _recordPaused;
            ToolTip.SetTip(_pauseResumeButton, _recordPaused
                ? "Carry on recording from here. The new part is anchored to the video on its own, so it stays in sync."
                : "Stop recording and pause the video. You can resume and keep going.");
        }
    }

    private void ShowMicrophoneUnavailable(string message)
    {
        _isRecording = false;
        if (_recordingLight != null)
        {
            _recordingLight.Classes.Remove("recording");
            _recordingLight.Opacity = 0.2;
        }
        if (_recordingStatusText != null)
        {
            _recordingStatusText.Text = "NO MIC";
            _recordingStatusText.Foreground = GetAppBrush("AppWarningBrush", Brushes.Yellow);
        }
        if (_micRecordButton != null) _micRecordButton.Classes.Remove("recording");
        UpdateTransportState();
        UpdateApplyState(message);
    }

    /// <summary>
    /// ISSUE_05 — deletes a temp take, tolerating a briefly-still-held file handle.
    ///
    /// The recorder now closes its WAV writer in an ordered shutdown, but Windows can hold a
    /// handle open for a few more milliseconds after the last Dispose. A single attempt therefore
    /// used to lose the race now and then and leave abandoned takes accumulating in the temp
    /// folder forever, because every failure was swallowed by a bare `catch`.
    /// </summary>
    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        Task.Run(() =>
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    if (!System.IO.File.Exists(path)) return;
                    System.IO.File.Delete(path);
                    return;
                }
                catch (System.IO.IOException) when (attempt < 3)
                {
                    System.Threading.Thread.Sleep(60);
                }
                catch (UnauthorizedAccessException) when (attempt < 3)
                {
                    System.Threading.Thread.Sleep(60);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Debug("VoiceOver", $"Could not delete temp take '{System.IO.Path.GetFileName(path)}': {ex.Message}");
                    return;
                }
            }

            RuntimeLog.Debug("VoiceOver", $"Temp take '{System.IO.Path.GetFileName(path)}' is still locked; leaving it for temp cleanup.");
        });
    }

    private void StopRecordingAndPlayback()
    {
        if (!_isRecording) return;

        _isRecording = false;
        _recordArming = false;
        _recordPaused = false;
        _isCurrentlyFrozen = false;

        _lastTakeWasRejected = false;
        FinalizeCurrentTake();

        _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");

        _uiSoundMute?.Dispose();
        _uiSoundMute = null;

        if (_recordingLight != null)
        {
            _recordingLight.Classes.Remove("recording");
            _recordingLight.Opacity = 0.2;
        }
        if (_recordingStatusText != null)
        {
            _recordingStatusText.Text = "READY";
            _recordingStatusText.Foreground = GetAppBrush("AppTextPrimaryBrush", Brushes.White);
        }
        if (_pauseResumeButton != null) _pauseResumeButton.IsVisible = false;

        if (_micRecordButton != null) _micRecordButton.Classes.Remove("recording");
        UpdateTransportState();
        UpdateApplyState(_lastTakeWasRejected && !HasSavedVoiceOverSession()
            ? "Recording was too short to apply. Record another take or choose a mute option."
            : null);
    }

    private void OnVolumeChanged(object? sender, float volume)
    {
        _peakVolume = Math.Max(_peakVolume, volume);
        Dispatcher.UIThread.Post(() =>
        {
            if (_isRecording)
            {
                _waveformSamples.Add(volume);
            }
        });
    }

    private Canvas? _eqMeterCanvas;
    private Avalonia.Controls.Shapes.Path? _eqPath;
    private Random _eqRandom = new Random();

    private void UpdateSmoothEqMeter()
    {
        if (_eqMeterCanvas == null) return;

        double currentPeak = _peakVolume;
        _peakVolume = 0;

        if (currentPeak > _smoothedVolume)
        {
            _smoothedVolume = currentPeak;
        }
        else
        {
            _smoothedVolume = Math.Max(0, _smoothedVolume - 0.05);
        }

        if (_eqPath == null)
        {
            _eqPath = new Avalonia.Controls.Shapes.Path
            {
                Stroke = Avalonia.Application.Current?.FindResource("AppSuccessBrush") as Avalonia.Media.IBrush,
                StrokeThickness = 4,
                IsHitTestVisible = false
            };
            _eqMeterCanvas.Children.Add(_eqPath);
        }

        if (_smoothedVolume > 0.9)
            _eqPath.Stroke = Avalonia.Application.Current?.FindResource("AppWarningBrush") as Avalonia.Media.IBrush;
        else
            _eqPath.Stroke = Avalonia.Application.Current?.FindResource("AppSuccessBrush") as Avalonia.Media.IBrush;

        double width = _eqMeterTrack != null && _eqMeterTrack.Bounds.Width > 0 ? _eqMeterTrack.Bounds.Width : 250;
        double height = 30;
        int numBars = (int)(width / 8);
        
        var geometry = new Avalonia.Media.StreamGeometry();
        using (var context = geometry.Open())
        {
            double x = 4;
            for (int i = 0; i < numBars; i++)
            {
                double targetAmp = _smoothedVolume * 2.0; 
                double randomized = targetAmp * (0.3 + _eqRandom.NextDouble() * 0.7);
                double barHeight = Math.Min(height - 4, randomized * height);
                if (barHeight < 2) barHeight = 2;

                double centerY = height / 2;
                context.BeginFigure(new Avalonia.Point(x, centerY + barHeight / 2), false);
                context.LineTo(new Avalonia.Point(x, centerY - barHeight / 2));
                x += 8;
            }
        }
        _eqPath.Data = geometry;
    }

    private Avalonia.Controls.Shapes.Path? _waveformPath;
    
    private void UpdateWaveformUI()
    {
        if (_liveWaveformMonitor == null || !_isRecording)
        {
            if (_waveformPath != null) _waveformPath.IsVisible = false;
            return;
        }

        if (_waveformPath == null)
        {
            _waveformPath = new Avalonia.Controls.Shapes.Path
            {
                Stroke = Avalonia.Application.Current?.FindResource("AppSuccessBrush") as Avalonia.Media.IBrush,
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            _liveWaveformMonitor.Children.Add(_waveformPath);
        }

        _waveformPath.IsVisible = true;

        int maxLines = Math.Max(0, (int)(_liveWaveformMonitor.Bounds.Width / 2));
        int visibleLines = Math.Min(maxLines, _waveformSamples.Count);
        int startIdx = Math.Max(0, _waveformSamples.Count - visibleLines);
        double x = 0;
        double laneHeight = Math.Max(1, _liveWaveformMonitor.Bounds.Height);
        double centerY = laneHeight / 2;

        var geometry = new Avalonia.Media.StreamGeometry();
        using (var context = geometry.Open())
        {
            for (int i = 0; i < visibleLines; i++)
            {
                double height = Math.Max(2, _waveformSamples[startIdx + i] * laneHeight);
                context.BeginFigure(new Avalonia.Point(x, centerY - height / 2), false);
                context.LineTo(new Avalonia.Point(x, centerY + height / 2));
                x += 2;
            }
        }
        _waveformPath.Data = geometry;
    }

    private async void ApplyAndClose()
    {
        if (_isRecording)
        {
            StopRecordingAndPlayback();
        }
        _recorder?.StopRecording();

        bool duckAudio = _duckAudioCb?.IsChecked == true;

        if (!HasSavedVoiceOverSession())
        {
            Result = null;
            UpdateApplyState("Nothing to apply yet. Record a take before applying.");
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
                if (session.IsMuted ||
                    session.EndSec <= session.StartSec ||
                    string.IsNullOrWhiteSpace(session.WavPath) ||
                    !System.IO.File.Exists(session.WavPath))
                {
                    continue;
                }

                try
                {
                    string persistedPath = CreatePersistedVoiceOverPath();
                    
                    if (session.TrimLeftSec > 0 || session.TrimRightSec > 0)
                    {
                        double realTrimLeft = _timeline != null ? Math.Max(0, _timeline.SourceToOutput(session.StartSec + session.TrimLeftSec) - _timeline.SourceToOutput(session.StartSec)) : session.TrimLeftSec;
                        double realTrimRight = _timeline != null ? Math.Max(0, _timeline.SourceToOutput(session.EndSec) - _timeline.SourceToOutput(session.EndSec - session.TrimRightSec)) : session.TrimRightSec;
                        double totalRealDur = _timeline != null ? Math.Max(0, _timeline.SourceToOutput(session.EndSec) - _timeline.SourceToOutput(session.StartSec)) : (session.EndSec - session.StartSec);
                        double realDur = Math.Max(0.1, totalRealDur - realTrimLeft - realTrimRight);

                        var startInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = ResolveBinaryPath("ffmpeg.exe", "backend"),
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        startInfo.ArgumentList.Add("-y");
                        startInfo.ArgumentList.Add("-i");
                        startInfo.ArgumentList.Add(session.WavPath);
                        startInfo.ArgumentList.Add("-ss");
                        startInfo.ArgumentList.Add(realTrimLeft.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                        startInfo.ArgumentList.Add("-t");
                        startInfo.ArgumentList.Add(realDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                        startInfo.ArgumentList.Add("-c");
                        startInfo.ArgumentList.Add("copy");
                        startInfo.ArgumentList.Add(persistedPath);
                        using var proc = System.Diagnostics.Process.Start(startInfo);
                        if (proc == null) throw new Exception($"FFmpeg trim process failed to start for take {i + 1}");
                        await proc.WaitForExitAsync();
                        if (proc.ExitCode != 0 || !System.IO.File.Exists(persistedPath) || new System.IO.FileInfo(persistedPath).Length == 0)
                        {
                            throw new Exception($"FFmpeg trim failed with code {proc.ExitCode} for take {i + 1}");
                        }
                    }
                    else
                    {
                        await Task.Run(() => File.Copy(session.WavPath, persistedPath, overwrite: true));
                    }
                    persistedTakes.Add(new VoiceOverTake(persistedPath, session.RenderStartSec));
                    keepPaths.Add(persistedPath);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("VoiceOver", $"Voiceover take {i + 1} could not be persisted. {ex.Message}");
                    Result = null;
                    if (_applyButton != null) _applyButton.Content = "APPLY & CLOSE";
                    UpdateApplyState($"Voiceover take {i + 1} could not be saved. Record another take.");
                    return;
                }
            }

            if (_applyButton != null) _applyButton.Content = "APPLY & CLOSE";
            DeleteSessionFilesExcept(keepPaths);
        }

        if (persistedTakes.Count == 0)
        {
            Result = null;
            UpdateApplyState("Voiceover audio could not be prepared. Record another take.");
            return;
        }

        string? finalWav = persistedTakes.Count > 0 ? persistedTakes[0].Path : null;
        double finalStart = persistedTakes.Count > 0 ? persistedTakes[0].StartSec : 0;

        Result = new VoiceOverResult
        {
            VoiceOverWavPath = finalWav,
            VoiceOverStartTimestampSec = finalStart,
            VoiceOverTakes = persistedTakes,
            DuckAudio = duckAudio
        };

        _isSafeToClose = true;
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
        
        if (InitialState?.VoiceOverTakes != null)
        {
            foreach (var take in InitialState.VoiceOverTakes)
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

    /// <summary>
    /// LEAK_02 — cancel a superseded generation, then dispose it OFF the interface thread.
    ///
    /// ⚠️ NEVER call <c>CancellationTokenSource.Dispose()</c> directly from an event handler here.
    /// Dispose BLOCKS until every callback raised by Cancel() has finished, and those callbacks
    /// marshal back to the interface thread — so the interface thread ends up waiting for itself.
    /// That is the exact deadlock that froze the Granular Speed Editor earlier (see CANCEL_01);
    /// it is a real, reproduced bug in this codebase, not a theoretical one.
    ///
    /// Handing the disposal to a worker thread keeps the tidy-up without the wait: nothing on that
    /// thread is holding the interface hostage, so Cancel's callbacks are free to complete.
    /// </summary>
    private static void RetireGenerationCts(System.Threading.CancellationTokenSource? cts)
    {
        if (cts == null) return;
        try { cts.Cancel(); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try { cts.Dispose(); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        });
    }

    protected override void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
        {
            // ZOOMHANG_01 - Must dispose before Hide()/OnClosed() collapses bounds to avoid OpenGL deadlock
            _videoHost?.Dispose();
            _videoHost = null;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Controls.CoachOverlay.Cancel(this);
        Controls.FloatingNotice.Clear(this);
        _isClosing = true;
        _generationCts?.Cancel();
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        if (_recorder != null)
        {
            _recorder.VolumeChanged -= OnVolumeChanged;
            _recorder.Dispose();
        }

        _uiSoundMute?.Dispose();
        _uiSoundMute = null;

        DeleteUnappliedVoiceOverFiles();
        StopPreviewPlayers();
        
        if (_tempThumbPath != null && System.IO.File.Exists(_tempThumbPath))
        {
            try { System.IO.File.Delete(_tempThumbPath); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }
        if (_tempWavePath != null && System.IO.File.Exists(_tempWavePath))
        {
            try { System.IO.File.Delete(_tempWavePath); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }

        base.OnClosed(e);
    }

    private PreviewDetachController? _previewDetach;

    private void WirePreviewDetach()
    {
        var btn = this.FindControl<Button>("VoiceOverDetachPreviewBtn");
        if (btn == null) return;

        _previewDetach = new PreviewDetachController(
            this,
            PreviewDetachController.VoiceOverKey,
            "Preview Monitor — Voice Over",
            () => _videoHost);

        _previewDetach.StateChanged += detached =>
        {
            var watermark = this.FindControl<Avalonia.Controls.Border>("VoiceOverPreviewDetachedWatermark");
            if (watermark != null) watermark.IsVisible = detached;
            _previewDetach!.SyncButton(btn);
        };

        _previewDetach.DetachUnavailable += why => RuntimeLog.Info("UI", why);

        btn.Click += (_, _) => _previewDetach.Toggle();
        _previewDetach.SyncButton(btn);
    }
}
