// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.LogicalTree;  // QUEUEMENU_01 - GetLogicalDescendants, for wiring the flyout's buttons
using Avalonia.VisualTree;   // QUEUEMENU_01 - FindAncestorOfType, for aiming the right-click menu
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Ipc;
using FortniteVideoSoftware.Core.Media;
using System.Diagnostics;
using System.IO;
using System;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Threading.Tasks;

using System.Globalization;
namespace FortniteVideoSoftware.App;

public class FileNameConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly FileNameConverter Instance = new();
    public object? Convert(object? v, Type t, object? p, CultureInfo c) => v is string s ? System.IO.Path.GetFileName(s) : v?.ToString() ?? "";
    public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => v;
}

internal sealed record VideoFileFingerprint(string Path, long SizeBytes, DateTime LastWriteUtc, string Sha256);

public partial class VideoMergerWindow : Window
{

    public double MergerVolume { get; set; } = 100;
    private MpvVideoView? _videoHost;
    private bool _isSeeking = false;
    private double? _nextSeekTarget = null;
    private bool _isTimerUpdatingSlider = false;
    private bool _isTimelineDrawn = false;
    public ObservableCollection<string> VideoQueue { get; } = new();

    public static readonly Avalonia.StyledProperty<bool> HasVideosProperty =
        Avalonia.AvaloniaProperty.Register<VideoMergerWindow, bool>(nameof(HasVideos), false);
    public bool HasVideos
    {
        get => GetValue(HasVideosProperty);
        set => SetValue(HasVideosProperty, value);
    }

    public static readonly Avalonia.StyledProperty<double> QualitySliderValueProperty =
        Avalonia.AvaloniaProperty.Register<VideoMergerWindow, double>(nameof(QualitySliderValue), 100.0);
    public double QualitySliderValue
    {
        get => GetValue(QualitySliderValueProperty);
        set => SetValue(QualitySliderValueProperty, value);
    }

    public static readonly Avalonia.StyledProperty<double> MainSpeedSliderValueProperty =
        Avalonia.AvaloniaProperty.Register<VideoMergerWindow, double>(nameof(MainSpeedSliderValue), 1.0);
    public double MainSpeedSliderValue
    {
        get => GetValue(MainSpeedSliderValueProperty);
        set => SetValue(MainSpeedSliderValueProperty, value);
    }

    private MusicWizardResult? _musicResult;

    private bool _isSafeToClose = false;
    private MergerWorker? _activeMergerWorker;
    private System.Threading.CancellationTokenSource? _mergeCts;

    private Avalonia.Threading.DispatcherTimer? _playbackTimer;
    private readonly ApplicationPaths _paths = ApplicationPaths.CreateDefault();

    private double _baseSpeed = 1.0;
    private double _previousVolume = 100;

    private string? _outputDirectory;
    private string _ffprobePath = "";

    private double _cachedTotalDurationSec = 0;
    private Services.OutputSizeEstimator? _mergerSizeEstimator;
    private Services.LatestEstimateWorker<Services.MergerSizeRequest>? _mergerSizeWorker;
    private Services.MergerSizeRequest? _lastMergerSizeRequest;
    private Services.EstimateMedia[]? _knownSizeSources;

    private bool _musicIsStale = false;
    private string _musicQueueSignature = "";

    private readonly object _videoFingerprintLock = new();
    private readonly Dictionary<string, Task<VideoFileFingerprint?>> _videoFingerprintTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.SemaphoreSlim _videoHashSemaphore = new(1, 1);

    private readonly System.Threading.SemaphoreSlim _externalDropGate = new(1, 1);
    private readonly FortniteVideoSoftware.Core.Infrastructure.RecoveryManager _recovery = new FortniteVideoSoftware.Core.Infrastructure.RecoveryManager(FortniteVideoSoftware.Core.Infrastructure.ApplicationPaths.CreateDefault());

    public VideoMergerWindow()
    {
        InitializeComponent();
        Title = $"Fortnite Video Software - Merger v{DeploymentLifecycle.GetCurrentVersion()}";

        // GRIP_01 — the bottom-right resize corner. These windows are borderless, so the OS
        // draws no resize frame: without this there is nothing to grab and nothing telling the
        // user the Video Merger can be resized at all. One shared implementation — see
        // Controls/WindowResizeGrip.cs for why it is not per-window code.
        Controls.WindowResizeGrip.Attach(this, "Drag to resize the Video Merger");
        _recovery.AcquireLock();
        FortniteVideoSoftware.App.WindowBoundsHelper.Track(this, "VideoMergerBounds", fitDisplayOnFirstRun: true);   // FIRSTFIT_01

        _ffprobePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "backend", "ffprobe.exe");
        if (!System.IO.File.Exists(_ffprobePath))
            _ffprobePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "ffprobe.exe");
        if (!System.IO.File.Exists(_ffprobePath)) _ffprobePath = "ffprobe.exe";

        this.Loaded += (s, e) => Controls.CoachOverlay.Register(this, Controls.CoachTours.MergerKey, Controls.CoachTours.Merger);

        this.Loaded += async (s, e) => {
            InitializeMpv();
        };

        _playbackTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();

        InitializeControls();
        WireUpEvents();
        InitializeSliders();
        _ = LoadOutputDirectoryAsync();

        FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged += OnGlobalMasterVolumeChanged;
        this.Closed += (s, e) => { FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged -= OnGlobalMasterVolumeChanged; };

        Win32FileDropInterop.Attach(this, paths => _ = AddExternalVideosAsync(paths));
    }

    private void InitializeControls()
    {
        var videoList = VideoListCtl;
        if (videoList != null)
        {
            videoList.ItemsSource = VideoQueue;
            videoList.SelectionChanged += (s, e) =>
            {
                // AUTOPREVIEW_01 — highlighting a clip IS the preview gesture. See StartAutoPreview.
                if (videoList.SelectedItem is string path && _videoHost?.IpcClient != null)
                {
                    StartAutoPreview(path);
                }
                UpdateQueueState();
                UpdatePreviewAvailable();
            };
            videoList.AddHandler(Avalonia.Input.DragDrop.DragOverEvent, VideoList_DragOver);
            videoList.AddHandler(Avalonia.Input.DragDrop.DragLeaveEvent, VideoList_DragLeave);
            videoList.AddHandler(Avalonia.Input.DragDrop.DropEvent, VideoList_Drop);
            videoList.PointerPressed += VideoList_PointerPressed;
            videoList.PointerMoved += VideoList_PointerMoved;
            videoList.PointerReleased += VideoList_PointerReleased;
        }
        VideoQueue.CollectionChanged += VideoQueue_CollectionChanged;

        var timelineSlider = this.FindControl<Slider>("TimelineSlider");
        var timelineOverlay = this.FindControl<Border>("TimelineOverlay");
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineMarkersCanvas");

        Controls.TimelineKnob.Attach(canvas, timelineSlider);

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
        }
        if (timelineOverlay != null && canvas != null && timelineSlider != null)
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

    private void SeekTimelineFromPointer(Avalonia.Input.PointerEventArgs e, Avalonia.Controls.Canvas timelineCanvas, Slider timelineSlider)
    {
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

    private void WireUpEvents()
    {
        var returnBtn = this.FindControl<MenuItem>("MenuReturnToApp");
        if (returnBtn != null) returnBtn.Click += (s, e) => ReturnToMainApp();

        var menuExit = this.FindControl<MenuItem>("MenuExit");
        if (menuExit != null) menuExit.Click += (s, e) => { ShutdownVideoPipeline(); Environment.Exit(0); };

        var mergerHelpBtn = this.FindControl<Button>("MergerHelpButton");
        if (mergerHelpBtn != null) mergerHelpBtn.Click += (s, e) => Controls.CoachOverlay.Replay(this);

        var returnToMainBtn = this.FindControl<Button>("ReturnToMainAppButton");
        if (returnToMainBtn != null) returnToMainBtn.Click += (s, e) => ReturnToMainApp();

        var playPauseBtn = this.FindControl<Button>("PlayPauseButton");
        if (playPauseBtn != null)
        {
            playPauseBtn.Click += (s, e) =>
            {
                TakeManualControl();   // AUTOPREVIEW_01
                if (_videoHost?.IpcClient != null)
                    _ = _videoHost.IpcClient.SetPropertyAsync("pause", _videoHost.IpcClient.IsPaused ? "no" : "yes");
            };
        }

        var fastBackwardBtn = this.FindControl<Button>("FastBackwardButton");
        if (fastBackwardBtn != null)
        {
            fastBackwardBtn.Click += (s, e) =>
            {
                TakeManualControl();   // AUTOPREVIEW_01
                if (_videoHost?.IpcClient != null)
                {
                    double target = Math.Max(0, _videoHost.IpcClient.CurrentTime - 10);
                    _ = SeekInternal(target);
                }
            };
        }

        var fastForwardBtn = this.FindControl<Button>("FastForwardButton");
        if (fastForwardBtn != null)
        {
            fastForwardBtn.Click += (s, e) =>
            {
                TakeManualControl();   // AUTOPREVIEW_01
                if (_videoHost?.IpcClient != null)
                {
                    double dur = _videoHost.IpcClient.Duration;
                    double target = dur > 0 ? Math.Min(dur, _videoHost.IpcClient.CurrentTime + 10) : _videoHost.IpcClient.CurrentTime + 10;
                    _ = SeekInternal(target);
                }
            };
        }

        var overlayLayer = OverlayLayerCtl;
        if (overlayLayer != null)
        {
            overlayLayer.CancelRequested += (_, _) =>
            {
                if (_mergeCts != null && !_mergeCts.IsCancellationRequested)
                {
                    RuntimeLog.Info("MERGER", "User requested merge cancellation.");
                    SetQueueStatus("Canceling merge...", false);
                    _mergeCts.Cancel();
                    _activeMergerWorker?.Cancel();
                }
            };
        }

        var addBtn = this.FindControl<Button>("AddVideoButton");
        if (addBtn != null) addBtn.Click += (s, e) => OnAddVideoClicked();

        var menuAddVideo = this.FindControl<MenuItem>("MenuAddVideo");
        if (menuAddVideo != null) menuAddVideo.Click += (s, e) => OnAddVideoClicked();

        var menuOutputFolder = MenuOutputFolderCtl;
        if (menuOutputFolder != null) menuOutputFolder.Click += async (s, e) => OnChooseOutputFolder();

        var menuSettings = this.FindControl<MenuItem>("MenuSettings");
        if (menuSettings != null)
        {
            menuSettings.Click += async (s, e) =>
            {
                var settingsWin = new FortniteVideoSoftware.App.Controls.SettingsWindow();
                await settingsWin.ShowDialog<bool>(this);
            };
        }

        var menuAbout = this.FindControl<MenuItem>("MenuAbout");
        if (menuAbout != null)
        {
            menuAbout.Click += async (s, e) =>
            {
                await FortniteVideoSoftware.App.Controls.SettingsWindow.ShowAboutAsync(this);
            };
        }

        var menuRemoveSelected = this.FindControl<MenuItem>("MenuRemoveSelected");
        if (menuRemoveSelected != null)
        {
            menuRemoveSelected.Click += async (s, e) =>
            {
                if (!FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.ConfirmVideoMergerRemove)
                {
                    ExecuteRemoveSelected();
                }
                else
                {
                    var dlg = new FortniteVideoSoftware.App.Controls.ConfirmDialogWindow();
                    dlg.SetTitle("Remove Selected");
                    dlg.SetMessage("Remove the selected video(s) from the queue?\nThis cannot be undone.");
                    dlg.SetButtonText("YES, REMOVE", "CANCEL");
                    await dlg.ShowDialog(this);
                    if (dlg.Result)
                        ExecuteRemoveSelected();
                }
            };
        }

        var menuClearAll = this.FindControl<MenuItem>("MenuClearAll");
        if (menuClearAll != null)
        {
            menuClearAll.Click += async (s, e) =>
            {
                if (!FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.ConfirmVideoMergerClearAll)
                {
                    VideoQueue.Clear();
                }
                else
                {
                    var dlg = new FortniteVideoSoftware.App.Controls.ConfirmDialogWindow();
                    dlg.SetTitle("Clear All");
                    dlg.SetMessage("Remove ALL videos from the queue and start over?\nThis cannot be undone.");
                    dlg.SetButtonText("YES, CLEAR ALL", "CANCEL");
                    await dlg.ShowDialog(this);
                    if (dlg.Result)
                        VideoQueue.Clear();
                }
            };
        }

        var addMusicBtn = AddMusicButtonCtl;
        if (addMusicBtn != null)
        {
            addMusicBtn.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked Add Music in Video Merger.");
                if (VideoQueue.Count < 1)
                {
                    UpdateQueueState();
                    SetQueueStatus("Add at least one video before adding music.", true);
                    return;
                }

                var wizard = new MusicWizardWindow(VideoQueue.ToList(), _cachedTotalDurationSec, _baseSpeed);
                await wizard.ShowDialog(this);

                if (wizard.Result != null)
                {
                    _musicResult = wizard.Result;
                    _musicQueueSignature = string.Join("|", VideoQueue);
                    _musicIsStale = false;
                    addMusicBtn.Classes.Clear();
                    addMusicBtn.Classes.Add("Primary");
                    addMusicBtn.Content = "MUSIC ADDED";
                    ToolTip.SetTip(addMusicBtn, "Music: " + System.IO.Path.GetFileName(_musicResult.MusicFilePath));
                    UpdateEstimatedSize();
                }
            };
        }

        var flyoutScrim = this.FindControl<Border>("FlyoutScrim");
        var removeBtn = this.FindControl<Button>("RemoveVideoButton");
        if (removeBtn != null)
        {
            if (removeBtn.Flyout != null)
            {
                removeBtn.Flyout.Opened += (s, e) =>
                {
                    if (flyoutScrim != null) flyoutScrim.IsVisible = true;
                };
                removeBtn.Flyout.Closed += (s, e) =>
                {
                    if (flyoutScrim != null) flyoutScrim.IsVisible = false;
                };
            }

            if (flyoutScrim != null)
            {
                flyoutScrim.PointerPressed += (s, e) =>
                {
                    removeBtn.Flyout?.Hide();
                };
            }

            removeBtn.Click += (s, e) =>
            {
                if (!FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.ConfirmVideoMergerRemove)
                {
                    removeBtn.Flyout?.Hide();
                    if (flyoutScrim != null) flyoutScrim.IsVisible = false;
                    ExecuteRemoveSelected();
                }
            };
        }
        
        var confirmRemoveBtn = this.FindControl<Button>("ConfirmRemoveVideoButton");
        if (confirmRemoveBtn != null)
        {
            confirmRemoveBtn.Click += (s, e) =>
            {
                removeBtn?.Flyout?.Hide();
                if (flyoutScrim != null) flyoutScrim.IsVisible = false;
                ExecuteRemoveSelected();
            };
        }

        var moveUpBtn = this.FindControl<Button>("MoveUpButton");
        if (moveUpBtn != null) moveUpBtn.Click += (s, e) => MoveVideo(-1);

        var moveDownBtn = this.FindControl<Button>("MoveDownButton");
        if (moveDownBtn != null) moveDownBtn.Click += (s, e) => MoveVideo(1);

        var mergeBtn = this.FindControl<Button>("MergeButton");
        if (mergeBtn != null) mergeBtn.Click += async (s, e) => await OnMergeClicked(mergeBtn);

        // MERGERBOTTOM_01 — the SET IN / SET OUT / FULL CLIP wiring is gone with the buttons.
        // Per-clip trimming has left this screen; BuildClipTrimList still hands MergerWorker one
        // entry per clip so nothing downstream changed shape, but every entry is now full-length.

        WireQueueContextMenu();

        WireUpVolumeSlider();
        AttachTitleBarDrag();
        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, MergerKeyDownHandler, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        UpdateQueueState();
    }
    
    private void ExecuteRemoveSelected()
    {
        var videoList = VideoListCtl;
        if (videoList?.SelectedItems != null && videoList.SelectedItems.Count > 0)
        {
            var itemsToRemove = videoList.SelectedItems.Cast<string>().ToList();
            int lastIndex = -1;
            foreach (var item in itemsToRemove)
            {
                lastIndex = Math.Max(lastIndex, VideoQueue.IndexOf(item));
                VideoQueue.Remove(item);
            }
            if (VideoQueue.Count > 0)
            {
                videoList.SelectedItems.Clear();
                videoList.SelectedIndex = Math.Min(Math.Max(0, lastIndex - itemsToRemove.Count + 1), VideoQueue.Count - 1);
            }
        }
    }

    private void VideoQueue_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateQueueState();
        InvalidateMusicIfStale();
        DebouncedQualityProbe();

        // PROJ_11 — publish the queue so the project document can record it. Before this the queue
        // existed ONLY in this collection, so closing the Merger and saving produced a .fvsproj
        // that silently described a single-clip edit.
        PublishQueueToProject();
    }

    /// <summary>
    /// PROJ_11 — hands the current queue to <see cref="Services.ToolNavigator"/>, which is where
    /// <c>ProjectSession.Capture</c> reads it from. Called on every queue change AND once on close,
    /// because the case being fixed is precisely "the user closed the Merger and then saved".
    /// </summary>
    private void PublishQueueToProject()
        => Services.ToolNavigator.PublishMergeQueue(VideoQueue.ToList(), _baseSpeed);

    private void MoveVideo(int direction)
    {
        var videoList = VideoListCtl;
        if (videoList?.SelectedItems == null || videoList.SelectedItems.Count == 0) return;

        var selectedItems = videoList.SelectedItems.Cast<string>().ToList();
        var selectedIndices = selectedItems.Select(x => VideoQueue.IndexOf(x)).Where(x => x >= 0).OrderBy(x => x).ToList();
        
        if (selectedIndices.Count == 0) return;
        if (direction < 0 && selectedIndices.First() == 0) return; 
        if (direction > 0 && selectedIndices.Last() == VideoQueue.Count - 1) return;

        if (direction < 0)
        {
            foreach (int idx in selectedIndices)
            {
                var item = VideoQueue[idx];
                VideoQueue.RemoveAt(idx);
                VideoQueue.Insert(idx - 1, item);
            }
        }
        else
        {
            selectedIndices.Reverse();
            foreach (int idx in selectedIndices)
            {
                var item = VideoQueue[idx];
                VideoQueue.RemoveAt(idx);
                VideoQueue.Insert(idx + 1, item);
            }
        }
        
        videoList.SelectedItems.Clear();
        foreach (var item in selectedItems)
        {
            videoList.SelectedItems.Add(item);
        }
    }

    private void InitializeSliders()
    {
        var qualitySlider = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("QualitySlider");
        if (qualitySlider != null)
        {
            qualitySlider.SetRange(0, 19);
            var qualityLabels = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 20; i++) qualityLabels.Add($"{(i + 1) * 5}%");
            qualitySlider.SetLabels(qualityLabels);
            qualitySlider.Value = 19;
            qualitySlider.ValueChanged += (s, v) => { DebouncedQualityProbe(); };
            qualitySlider.ValueChangeCompleted += (s, v) => RuntimeLog.Info("UI", $"Quality slider set to {(v + 1) * 5}%");
        }
        UpdateQualityProbe();

        var speedSlider = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("MainSpeedSlider");
        if (speedSlider != null)
        {
            speedSlider.SetRange(1, 40);
            var speedLabels = new System.Collections.Generic.List<string>();
            for (int i = 1; i <= 40; i++) speedLabels.Add((i / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "x");
            speedSlider.SetLabels(speedLabels);
            speedSlider.Value = 10;
            SpeedPresetButtons.ConfigureBaseButton(this, 1.0, "Set speed to the default 1.0x");
            SpeedPresetButtons.WirePresetButtons(this, 1.0, ApplySpeedPreset);
            speedSlider.ValueChanged += (s, v) =>
            {
                _baseSpeed = v / 10.0;
                if (_videoHost?.IpcClient != null)
                    _ = _videoHost?.IpcClient?.SetPropertyAsync("speed", _baseSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                UpdateSpeedLabel();
                UpdateEstimatedSize();
                UpdateNormalizeInfo();
            };
        }
        UpdateSpeedLabel();
        UpdateNormalizeInfo();
    }

    /// <summary>
    /// ISSUE_010: shared worker coalesces rapid edits and reuses cached media details.
    /// </summary>
    private void DebouncedQualityProbe() => UpdateEstimatedSize();

    /// <summary>
    /// ISSUE_001: Called when the video queue changes. If music was already set up for
    /// a different queue, marks it as stale and warns the user.
    /// </summary>
    private void InvalidateMusicIfStale()
    {
        if (_musicResult == null) return;

        string currentSig = string.Join("|", VideoQueue);
        if (currentSig != _musicQueueSignature)
        {
            _musicIsStale = true;
            var addMusicBtn = AddMusicButtonCtl;
            if (addMusicBtn != null)
            {
                addMusicBtn.Content = "⚠ MUSIC STALE — RE-SETUP";
                addMusicBtn.Classes.Clear();
                addMusicBtn.Classes.Add("Danger");
                ToolTip.SetTip(addMusicBtn, "The video queue changed after music was set up. Click to re-configure music.");
            }
            SetQueueStatus("⚠ Music setup is stale — queue changed. Re-run Add Music before merging.", true);
        }

        UpdatePreviewAvailable();
        UpdateEstimatedSize();
    }

    /// <summary>
    /// ISSUE_005: Shows normalization info so the user knows the merge normalizes to 1080p60.
    /// </summary>
    private void UpdateNormalizeInfo()
    {
        var info = this.FindControl<TextBlock>("NormalizeInfoText");
        if (info == null) return;
        if (VideoQueue.Count > 0)
            info.Text = "Output: 1920×1080 @ 60fps";
        else
            info.Text = "";
    }

    /// <summary>
    /// ISSUE_004: Updates the output path display.
    /// </summary>
    private void UpdateOutputPathDisplay()
    {
        var pathText = this.FindControl<TextBlock>("OutputPathText");
        if (pathText == null) return;
        if (!string.IsNullOrEmpty(_outputDirectory))
            pathText.Text = "→ " + System.IO.Path.GetFileName(_outputDirectory);
    }

    /// <summary>
    /// ISSUE_006: Shows/hides the no-video overlay and enables/disables transport controls.
    /// </summary>
    private void UpdatePreviewAvailable()
    {
        var vl = VideoListCtl;
        bool hasVideo = _videoHost?.IpcClient != null && vl?.SelectedItem is string && VideoQueue.Count > 0;
        var noVideo = this.FindControl<Border>("NoVideoOverlay");
        var timelineOverlay = this.FindControl<Border>("TimelineOverlay");
        var playBtn = this.FindControl<Button>("PlayPauseButton");
        var ffBtn = this.FindControl<Button>("FastForwardButton");
        var fbBtn = this.FindControl<Button>("FastBackwardButton");

        if (noVideo != null) noVideo.IsVisible = !hasVideo;
        if (timelineOverlay != null) timelineOverlay.IsVisible = hasVideo;
        if (playBtn != null) playBtn.IsEnabled = hasVideo;
        if (ffBtn != null) ffBtn.IsEnabled = hasVideo;
        if (fbBtn != null) fbBtn.IsEnabled = hasVideo;

        // MERGERBOTTOM_01 — the SET IN / SET OUT / FULL CLIP enable loop that used to close this
        // method is gone with the buttons themselves.
    }

    private void ApplySpeedPreset(double speed)
    {
        var speedSlider = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("MainSpeedSlider");
        SpeedPresetButtons.SetSpinningWheelValue(speedSlider, speed);
        _baseSpeed = Math.Clamp(speed, 0.1, 4.0);
        if (_videoHost?.IpcClient != null)
            _ = _videoHost.IpcClient.SetPropertyAsync("speed", _baseSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        UpdateSpeedLabel();
        UpdateEstimatedSize();
    }

    private void UpdateQualityProbe() => UpdateEstimatedSize();

    private List<string> SnapshotVideoQueue() => new(VideoQueue);

    // SIZEESTIMATE_01 / MERGERESTIMATE_01 — one shared background estimator, with a bounded
    // metadata cache. Quality and speed changes reuse the probes instead of launching new ones.
    private void UpdateEstimatedSize()
    {
        _mergerSizeEstimator ??= new Services.OutputSizeEstimator(() => _ffprobePath);
        _mergerSizeWorker ??= new Services.LatestEstimateWorker<Services.MergerSizeRequest>(
            _mergerSizeEstimator.EstimateMergerAsync, PaintSizeEstimate,
            action => Avalonia.Threading.Dispatcher.UIThread.Post(action), Services.OutputSizeEstimator.QuickMergerEstimate);
        int quality = (this.FindControl<Controls.SpinningWheelSlider>("QualitySlider")?.Value + 1) * 5 ?? 100;
        var request = new Services.MergerSizeRequest(VideoQueue.ToArray(), _baseSpeed, quality, _knownSizeSources);
        if (_lastMergerSizeRequest is { } previous && previous.Speed == request.Speed &&
            previous.Quality == request.Quality && previous.Paths.SequenceEqual(request.Paths)) return;
        bool queueChanged = _lastMergerSizeRequest == null || !_lastMergerSizeRequest.Paths.SequenceEqual(request.Paths);
        _lastMergerSizeRequest = request;
        if (queueChanged)
        {
            var size = this.FindControl<TextBlock>("EstimatedSizeText");
            if (size != null) size.Text = request.Paths.Length == 0 ? "—" : "Calculating…";
            var length = this.FindControl<TextBlock>("EstimatedLengthText");
            if (length != null) length.Text = "—";
            _cachedTotalDurationSec = 0;
        }
        _mergerSizeWorker.Request(request);
    }

    private void PaintSizeEstimate(Services.OutputSizeEstimate estimate)
    {
        var size = this.FindControl<TextBlock>("EstimatedSizeText");
        if (size != null)
        {
            size.Text = estimate.Text;
            ToolTip.SetTip(size, "Estimated size of the finished video, including sound. The actual file size may differ.");
        }
        var length = this.FindControl<TextBlock>("EstimatedLengthText");
        if (length != null) length.Text = estimate.DurationSeconds > 0 ? FormatDuration(estimate.DurationSeconds) : "—";
        _cachedTotalDurationSec = estimate.Sources?.Sum(s => s.Duration) ?? 0;
        _knownSizeSources = estimate.Sources?.ToArray();
        _clipDurations.Clear();
        if (estimate.Sources != null)
            foreach (var media in estimate.Sources) _clipDurations[media.Path] = media.Duration;

        int quality = _lastMergerSizeRequest?.Quality ?? 100;
        var label = this.FindControl<TextBlock>("QualityLabel");
        if (label == null) return;
        if (quality >= 100) { label.Text = "100% — Lossless"; return; }
        if (!estimate.Megabytes.HasValue) { label.Text = $"{quality}%"; return; }
        double bpp = estimate.VideoKbps * 1000 / (1920.0 * 1080 * 60 * 1.5);
        string description = bpp switch
        {
            < 0.02 => "Unwatchable", < 0.04 => "Pixelated", < 0.06 => "Blurry",
            < 0.1 => "Clear", < 0.15 => "Sharp", < 0.25 => "Crisp-Clear", _ => "Lifelike"
        };
        label.Text = $"{quality}% — {description}";
    }

    /// <summary>MERGERESTIMATE_01 — total duration in h:mm:ss, or m:ss below one hour.</summary>
    private static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0) return "\u2014";

        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes}:{ts.Seconds:00}";
    }

    /// <summary>
    /// SPEEDLABEL_01 — the speed → description/colour ladder was duplicated BYTE FOR BYTE between
    /// this window and the other one, control name included. It is a product decision the user
    /// reads ("1.2x — Slight Boost"), so the two windows must never be able to disagree about it.
    /// One ladder now, in <see cref="Infrastructure.SpeedLabel"/>.
    /// </summary>
    private void UpdateSpeedLabel() => Infrastructure.SpeedLabel.Apply(this, _baseSpeed);

    private void WireUpVolumeSlider()
    {
        var volumeSlider = VolumeSliderCtl;
        var volumeBadgeText = this.FindControl<TextBlock>("VolumeBadgeText");
        var volumeSpeakerIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("VolumeSpeakerIcon");
        if (volumeSlider != null && volumeBadgeText != null)
        {
            volumeSlider.Value = FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolume;
            volumeBadgeText.Text = $"{FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolume}%";

            volumeSlider.PropertyChanged += (s, e) =>
            {
                if (e.Property == Slider.ValueProperty && e.NewValue != null)
                {
                    int vol = System.Convert.ToInt32(e.NewValue);
                    volumeBadgeText.Text = $"{vol}%";
                    ApplyMasterVolume(vol);
                    if (volumeSpeakerIcon != null)
                    {
                        volumeSpeakerIcon.Data = vol == 0
                            ? Avalonia.Media.Geometry.Parse("M3,7 L6,7 L10,3 L10,13 L6,9 L3,9 Z M12,5 L16,13 M16,5 L12,13")
                            : Avalonia.Media.Geometry.Parse("M3,7 L6,7 L10,3 L10,13 L6,9 L3,9 Z M13,5 A4,4 0 0,1 13,11 M16,2 A8,8 0 0,1 16,14");
                    }
                }
            };

            var speakerHitBox = this.FindControl<Button>("SpeakerHitBox");
            if (speakerHitBox != null) speakerHitBox.Click += (s, e) => { TakeManualControl(); ToggleMute(); };   // AUTOPREVIEW_01

            // AUTOPREVIEW_01 — reaching for the volume IS asking for sound. Hooked on
            // PointerPressed rather than on the value change, because the value also moves when the
            // slider is seeded at startup and when the Main App broadcasts a volume change from
            // another window; neither of those is this user, in this window, wanting audio now.
            volumeSlider.PointerPressed += (s, e) => TakeManualControl();

            volumeSlider.PointerReleased += (s, e) =>
            {
                try { new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths).UpdatePropertiesSync(new System.Text.Json.Nodes.JsonObject { ["MainVolume"] = volumeSlider.Value }); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            };
        }
    }

    private void ToggleMute()
    {
        var volumeSlider = VolumeSliderCtl;
        if (volumeSlider != null)
        {
            if (volumeSlider.Value > 0) { _previousVolume = volumeSlider.Value; volumeSlider.Value = 0; }
            else { volumeSlider.Value = _previousVolume > 0 ? _previousVolume : 100; }
        }
    }

    private void ApplyMasterVolume(int masterVolumePercentage)
    {
        FortniteVideoSoftware.Core.Media.MpvIpcClient.SetGlobalMasterVolume(masterVolumePercentage);
    }

    private void OnGlobalMasterVolumeChanged(int masterVolumePercentage)
    {
        if (_videoHost?.IpcClient != null)
        {
            _ = _videoHost.IpcClient.SetPreviewVolumeAsync(masterVolumePercentage);
        }
    }

    private async void OnAddVideoClicked()
    {
        RuntimeLog.Info("UI", "User clicked Add Video in Video Merger.");
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var options = new FilePickerOpenOptions
        {
            Title = "Add Videos to Merger",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("Video Files") { Patterns = new[] { "*.mp4", "*.mkv", "*.avi", "*.mov" } } }
        };

        string startPath = "";
        try
        {
            var state = await new StateTransferStore(_paths).LoadAsync();
            if (state != null && state.TryGetPropertyValue("MergerUploadDirectory", out var node) && node != null)
            {
                string sp = node.ToString();
                if (System.IO.Directory.Exists(sp)) startPath = sp;
            }
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

        if (string.IsNullOrEmpty(startPath) || !System.IO.Directory.Exists(startPath))
            startPath = GetDownloadsPath();

        if (!string.IsNullOrEmpty(startPath) && Directory.Exists(startPath))
        {
            try { options.SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(startPath)); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(options);

        if (files.Count > 0)
        {
            try
            {
                string? directory = System.IO.Path.GetDirectoryName(files[0].Path.LocalPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    await new StateTransferStore(_paths).UpdatePropertiesAsync(new System.Text.Json.Nodes.JsonObject { ["MergerUploadDirectory"] = directory });
            }
            catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }

        int addedCount = 0;
        int skippedCount = 0;

        foreach (var file in files)
        {
            string path = NormalizeVideoPath(file.Path.LocalPath);
            if (!await ShouldAddVideoToQueueAsync(path))
            {
                skippedCount++;
                continue;
            }

            VideoQueue.Add(path);
            addedCount++;
        }

        if (addedCount > 0)
        {
            var vl = VideoListCtl;
            if (vl != null && vl.SelectedIndex < 0 && VideoQueue.Count > 0) vl.SelectedIndex = 0;
        }

        string status = files.Count == 0
            ? "No files selected."
            : skippedCount > 0
                ? $"{addedCount} video file(s) added. {skippedCount} duplicate file(s) skipped."
                : $"{addedCount} video file(s) added.";
        SetQueueStatus(status, false);
    }

    private async Task<bool> ShouldAddVideoToQueueAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SetQueueStatus("Skipped a missing video file.", true);
            return false;
        }

        string normalizedPath = NormalizeVideoPath(path);
        string? exactDuplicatePath = VideoQueue.FirstOrDefault(existing => SameVideoPath(existing, normalizedPath));
        if (exactDuplicatePath != null)
            return await ConfirmExactDuplicateAsync(normalizedPath);

        string? contentDuplicatePath = await FindContentDuplicatePathAsync(normalizedPath);
        if (contentDuplicatePath != null)
            return await ConfirmContentDuplicateAsync(normalizedPath, contentDuplicatePath);

        return true;
    }

    private async Task<string?> FindContentDuplicatePathAsync(string candidatePath)
    {
        if (!TryGetVideoFileSnapshot(candidatePath, out long candidateSize, out _))
            return null;

        var sameSizeQueuedPaths = VideoQueue
            .Select(NormalizeVideoPath)
            .Where(existing => !SameVideoPath(existing, candidatePath))
            .Where(existing => TryGetVideoFileSnapshot(existing, out long existingSize, out _) && existingSize == candidateSize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sameSizeQueuedPaths.Count == 0)
            return null;

        SetQueueStatus($"Checking duplicate content for {Path.GetFileName(candidatePath)}...", false);

        var candidateTask = GetOrStartVideoFingerprintAsync(candidatePath);
        var queuedTasks = sameSizeQueuedPaths.Select(GetOrStartVideoFingerprintAsync).ToArray();

        var candidateFingerprint = await candidateTask;
        if (candidateFingerprint == null)
            return null;

        var queuedFingerprints = await Task.WhenAll(queuedTasks);
        foreach (var queuedFingerprint in queuedFingerprints)
        {
            if (queuedFingerprint == null)
                continue;

            if (queuedFingerprint.SizeBytes == candidateFingerprint.SizeBytes &&
                string.Equals(queuedFingerprint.Sha256, candidateFingerprint.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return queuedFingerprint.Path;
            }
        }

        return null;
    }

    private async Task<bool> ConfirmExactDuplicateAsync(string duplicatePath)
    {
        var dlg = new FortniteVideoSoftware.App.Controls.ConfirmDialogWindow();
        dlg.SetTitle("Duplicate Video Detected");
        dlg.SetMessage(
            $"This exact video is already in the queue:\n\n" +
            $"{Path.GetFileName(duplicatePath)}\n\n" +
            "Adding it again will repeat the same clip in the final merged video.\n\n" +
            "Do you want to add it anyway?");
        dlg.SetButtonText("ADD ANYWAY", "SKIP DUPLICATE");
        await dlg.ShowDialog<bool>(this);
        return dlg.Result;
    }

    private async Task<bool> ConfirmContentDuplicateAsync(string candidatePath, string queuedPath)
    {
        var dlg = new FortniteVideoSoftware.App.Controls.ConfirmDialogWindow();
        dlg.SetTitle("Duplicate Video Content Detected");
        dlg.SetMessage(
            "This file appears to be the same video as one already in the queue.\n\n" +
            $"New file:\n{Path.GetFileName(candidatePath)}\n\n" +
            $"Already queued:\n{Path.GetFileName(queuedPath)}\n\n" +
            "The file names or locations are different, but the file size and content hash match. " +
            "Adding it again will repeat the same clip in the final merged video.\n\n" +
            "Do you want to add it anyway?");
        dlg.SetButtonText("ADD ANYWAY", "SKIP DUPLICATE");
        await dlg.ShowDialog<bool>(this);
        return dlg.Result;
    }

    private Task<VideoFileFingerprint?> GetOrStartVideoFingerprintAsync(string path)
    {
        string normalizedPath = NormalizeVideoPath(path);
        if (!TryGetVideoFileSnapshot(normalizedPath, out long sizeBytes, out DateTime lastWriteUtc))
            return Task.FromResult<VideoFileFingerprint?>(null);

        lock (_videoFingerprintLock)
        {
            if (_videoFingerprintTasks.TryGetValue(normalizedPath, out var existingTask))
                return existingTask;

            var task = CreateVideoFingerprintAsync(normalizedPath);
            _videoFingerprintTasks[normalizedPath] = task;
            return task;
        }
    }

    private async Task<VideoFileFingerprint?> CreateVideoFingerprintAsync(string path)
    {
        try
        {
            await _videoHashSemaphore.WaitAsync();
            try
            {
                if (!TryGetVideoFileSnapshot(path, out _, out _))
                    return null;

                string sha256 = await ComputeSha256Async(path);
                if (!TryGetVideoFileSnapshot(path, out long sizeBytes, out DateTime lastWriteUtc))
                    return null;

                return new VideoFileFingerprint(path, sizeBytes, lastWriteUtc, sha256);
            }
            finally
            {
                _videoHashSemaphore.Release();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("VideoMerger", $"Could not fingerprint {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
        finally
        {
            lock (_videoFingerprintLock)
            {
                _videoFingerprintTasks.Remove(path);
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            useAsync: true);

        using var sha256 = SHA256.Create();
        byte[] hash = await sha256.ComputeHashAsync(stream);
        return Convert.ToHexString(hash);
    }

    private static bool TryGetVideoFileSnapshot(string path, out long sizeBytes, out DateTime lastWriteUtc)
    {
        try
        {
            var info = new FileInfo(NormalizeVideoPath(path));
            if (info.Exists)
            {
                sizeBytes = info.Length;
                lastWriteUtc = info.LastWriteTimeUtc;
                return true;
            }
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

        sizeBytes = 0;
        lastWriteUtc = default;
        return false;
    }

    private static bool SameVideoPath(string left, string right)
    {
        return string.Equals(NormalizeVideoPath(left), NormalizeVideoPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVideoPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (System.Exception swallowed2)
        {
            global::FortniteVideoSoftware.App.RuntimeLog.Swallowed(swallowed2);   // FAULTTIER_02 — no failure is silent.
            return path.Trim();
        }
    }

    private async Task LoadOutputDirectoryAsync()
    {
        _outputDirectory = GetDownloadsPath();
        UpdateOutputPathDisplay();

        try
        {
            var state = await new StateTransferStore(_paths).LoadAsync();
            if (state != null && state.TryGetPropertyValue("MergerOutputDirectory", out var node) && node != null)
            {
                string dir = node.ToString();
                if (Directory.Exists(dir))
                {
                    _outputDirectory = dir;
                    var btn = MenuOutputFolderCtl;
                    if (btn != null) btn.Header = $"Output Folder: {System.IO.Path.GetFileName(dir)}";
                    UpdateOutputPathDisplay();
                    return;
                }
            }
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    /// <summary>
    /// ISSUE_04 — the Merger's default output folder.
    /// Order: the folder saved in the Merger's OWN settings, then the shell-resolved Downloads
    /// folder (correct when Downloads has been moved or OneDrive-redirected — the old
    /// %USERPROFILE%\Downloads guess was not), then Videos, then Documents.
    /// Returns empty when nothing is usable; callers then prompt via OutputFolderResolver
    /// instead of writing to a folder that does not exist.
    /// </summary>
    private static string GetDownloadsPath()
    {
        string configured = Infrastructure.SettingsManager.Instance.MergerOutputDirectory;
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return configured;

        string? downloads = FortniteVideoSoftware.Core.Infrastructure.KnownFolders.GetDownloads();
        if (!string.IsNullOrWhiteSpace(downloads)) return downloads!;

        string? videos = FortniteVideoSoftware.Core.Infrastructure.KnownFolders.GetVideos();
        if (!string.IsNullOrWhiteSpace(videos)) return videos!;

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Directory.Exists(documents) ? documents : string.Empty;
    }

    private async void OnChooseOutputFolder()
    {
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var options = new FolderPickerOpenOptions { Title = "Choose Output Folder for Merged Videos", AllowMultiple = false };
        if (!string.IsNullOrEmpty(_outputDirectory))
        {
            try { options.SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(_outputDirectory)); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
        if (result != null && result.Count > 0)
        {
            string picked = result[0].Path.LocalPath;

            if (!FortniteVideoSoftware.Core.Infrastructure.KnownFolders.IsWritableDirectory(picked))
            {
                await ErrorReporter.ShowAsync(this, "Folder not usable",
                    "That folder cannot be written to, so it was not saved. Pick a different folder.",
                    $"Selected merger output folder is not writable: {picked}");
                return;
            }

            _outputDirectory = picked;

            try { await new StateTransferStore(_paths).UpdatePropertiesAsync(new System.Text.Json.Nodes.JsonObject { ["MergerOutputDirectory"] = _outputDirectory }); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            Infrastructure.SettingsManager.Instance.MergerOutputDirectory = _outputDirectory;
            Infrastructure.SettingsManager.Save();

            var btn = MenuOutputFolderCtl;
            if (btn != null) btn.Header = $"Output Folder: {System.IO.Path.GetFileName(_outputDirectory)}";
            UpdateOutputPathDisplay();
        }
    }

    private async Task OnMergeClicked(Button mergeBtn)
    {
        if (VideoQueue.Count < 1)
        {
            UpdateQueueState();
            SetQueueStatus("Add at least one video before processing.", true);
            return;
        }

        if (_musicIsStale) { SetQueueStatus("⚠ Music setup is stale. Re-run Add Music before merging.", true); return; }

        string? resolvedOutputDir = await Infrastructure.OutputFolderResolver.ResolveAsync(
            this, Infrastructure.OutputFolderResolver.AppScope.Merger);
        if (resolvedOutputDir == null)
        {
            SetQueueStatus("Merge cancelled — no output folder was chosen.", true);
            return;
        }
        _outputDirectory = resolvedOutputDir;
        UpdateOutputPathDisplay();

        mergeBtn.IsEnabled = false;
        mergeBtn.Content = "MERGING...";
        SetQueueStatus("Merge in progress. Keep this window open.", false);

        if (_videoHost?.IpcClient != null)
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");

        await Task.Yield();

        try
        {
            _mergeCts = new System.Threading.CancellationTokenSource();
            var qs = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("QualitySlider");
            int qualityPercent = qs != null ? (qs.Value + 1) * 5 : 100;

            var mergeQueueSnapshot = SnapshotVideoQueue();
            bool allPortrait = true;
            bool allLandscape = true;
            foreach (var path in mergeQueueSnapshot)
            {
                if (!System.IO.File.Exists(path)) continue;
                var prober = new FortniteVideoSoftware.Core.Media.MediaProber(_ffprobePath, path);
                var (w, h) = await prober.GetResolutionAsync();
                if (h > w) allLandscape = false;
                if (w > h) allPortrait = false;
            }

            var targetRatio = FortniteVideoSoftware.Core.Media.MergerWorker.TargetAspectRatio.Landscape16x9;

            if (allPortrait && !allLandscape) targetRatio = FortniteVideoSoftware.Core.Media.MergerWorker.TargetAspectRatio.Portrait9x16;
            else if (allLandscape && !allPortrait) targetRatio = FortniteVideoSoftware.Core.Media.MergerWorker.TargetAspectRatio.Landscape16x9;
            else
            {
                var dialog = new FortniteVideoSoftware.App.Controls.ConfirmDialogWindow();
                dialog.SetTitle("Mixed Aspect Ratios");
                dialog.SetMessage("Your queue contains a mix of portrait and landscape videos.\n\nChoose 'Portrait (9:16)' to aggressively crop the sides of landscape videos, or 'Landscape (16:9)' to add black padding.");
                dialog.SetButtonText("Portrait (9:16)", "Landscape (16:9)");
                await dialog.ShowDialog(this);
                targetRatio = dialog.Result ? FortniteVideoSoftware.Core.Media.MergerWorker.TargetAspectRatio.Portrait9x16 : FortniteVideoSoftware.Core.Media.MergerWorker.TargetAspectRatio.Landscape16x9;
            }

            string mergerStrategy = FortniteVideoSoftware.Core.Media.ExportEncoderStrategy.Resolve(
                FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.VideoEncoderOverride,
                bootScanResult: null,
                ffmpegPath: FortniteVideoSoftware.Core.Infrastructure.BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries"));

            var worker = new FortniteVideoSoftware.Core.Media.MergerWorker { InputFiles = new List<string>(VideoQueue), OutputDirectory = _outputDirectory, SpeedFactor = _baseSpeed, QualityPercent = qualityPercent, OutputRatio = targetRatio, AutoSpikeFlattening = FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.Defaults.AutoSpikeFlattening, HardwareStrategy = mergerStrategy };

            worker.ClipTrims = BuildClipTrimList();
            _activeMergerWorker = worker;

            // Only the Wizard owns export gain; the bubble slider controls monitoring.
            double currentMainVol = _musicResult?.VideoVolume ?? 1.0;

            if (_musicResult != null)
            {
                var musicPaths = (_musicResult.MusicFilePaths.Count > 0
                        ? _musicResult.MusicFilePaths
                        : new List<string> { _musicResult.MusicFilePath })
                    .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    .ToList();

                if (musicPaths.Count > 0)
                {
                    double speedFactor = _baseSpeed > 0.01 ? _baseSpeed : 1.0;
                    double timelineStartSec = _musicResult.TimelineStartSeconds / speedFactor;
                    double timelineEndSec = _musicResult.TimelineEndSeconds / speedFactor;
                    for (int i = 0; i < musicPaths.Count; i++)
                    {
                        double offsetSeconds = i == 0 ? _musicResult.OffsetSeconds : 0.0;
                        double durationSeconds = i == 0 && _musicResult.MusicDurationSeconds > 0
                            ? Math.Max(0.01, _musicResult.MusicDurationSeconds - Math.Max(0, offsetSeconds))
                            : 0.0;
                        worker.MusicTracks.Add(new MusicTrack(musicPaths[i], offsetSeconds, durationSeconds));
                    }

                    worker.MusicConfig = new System.Text.Json.Nodes.JsonObject
                    {
                        // DUCKOFF_01 — see MainWindow. The Merger must send the same flag or an
                        // unchecked box would still build the full ducking apparatus here.
                        ["ducking_enabled"] = _musicResult.EnableDucking,
                        ["ducking_threshold"] = _musicResult.EnableDucking ? FortniteVideoSoftware.Core.Media.SidechainCompressNode.TunedThreshold : FortniteVideoSoftware.Core.Media.SidechainCompressNode.BypassThreshold,
                        ["ducking_ratio"] = _musicResult.EnableDucking ? FortniteVideoSoftware.Core.Media.SidechainCompressNode.TunedRatio : FortniteVideoSoftware.Core.Media.SidechainCompressNode.BypassRatio,
                        ["main_vol"] = currentMainVol,
                        ["music_vol"] = _musicResult.MusicVolume,
                        ["carving_enabled"] = _musicResult.EnableCarving,
                        ["timeline_start_sec"] = timelineStartSec,
                        ["timeline_end_sec"] = Math.Max(timelineStartSec + 0.01, timelineEndSec),
                        ["loop_music"] = _musicResult.LoopMusic
                    };
                }
            }

            if (worker.MusicConfig == null)
            {
                worker.MusicConfig = new System.Text.Json.Nodes.JsonObject { ["main_vol"] = currentMainVol };
            }
            else
            {
                worker.MusicConfig["main_vol"] = currentMainVol;
            }

            OverlayLayerCtl?.StartOverlay();

            worker.ProgressUpdate += percent => Avalonia.Threading.Dispatcher.UIThread.Post(() => 
            {
                mergeBtn.Content = $"MERGING... {percent}%";
                OverlayLayerCtl?.UpdatePhase(1, "Merging Videos...", percent);
            });

            worker.Finished += async (success, msg) =>
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    OverlayLayerCtl?.StopOverlay();
                    mergeBtn.IsEnabled = true;
                    mergeBtn.Content = "MERGE VIDEOS";
                    UpdateQueueState();
                    if (success)
                    {
                        SetQueueStatus("Merge completed successfully.", false);
                        var dlg = new FortniteVideoSoftware.App.Controls.FinishedDialogWindow();
                        dlg.SetOutputPath(msg);
                        await dlg.ShowDialog(this);
                        if (dlg.DialogResult == 1) Close();
                    }
                    else if (worker.WasCanceled)
                    {
                        SetQueueStatus("Merge cancelled.", false);
                    }
                    else
                    {
                        SetQueueStatus("Merge failed. See the error dialog for details.", true);
                        if (worker.LastFailure != null)
                        {
                            await ErrorReporter.ShowAsync(this, worker.LastFailure);
                        }
                        else
                        {
                            await ErrorReporter.ShowAsync(this, "Merge failed",
                                "The videos could not be merged, so no file was written.",
                                worker.FailureDetail ?? msg);
                        }
                    }
                });
            };

            await Task.Run(() => worker.RunAsync(_mergeCts.Token), _mergeCts.Token);
        }
        catch (OperationCanceledException swallowed)
        {
            OverlayLayerCtl?.StopOverlay();
            mergeBtn.IsEnabled = true;
            mergeBtn.Content = "MERGE VIDEOS";
            UpdateQueueState();
            SetQueueStatus("Merge cancelled.", false);
            global::FortniteVideoSoftware.App.RuntimeLog.Swallowed(swallowed);   // FAULTTIER_02 — no failure is silent.
        }
        catch (Exception ex)
        {
            OverlayLayerCtl?.StopOverlay();
            mergeBtn.IsEnabled = true;
            mergeBtn.Content = "MERGE VIDEOS";
            UpdateQueueState();
            SetQueueStatus("Merge error. See the error dialog for details.", true);

            var failure = FortniteVideoSoftware.Core.Media.FfmpegErrorClassifier.ClassifyException(ex,
            FortniteVideoSoftware.Core.Media.ExportStage.Preflight,
            new FortniteVideoSoftware.Core.Media.ExportAttemptIdentity { AttemptIndex = 1, Operation = "MergeSetup", Description = "Merge preparation" });
            await ErrorReporter.ShowAsync(this, failure);
            global::FortniteVideoSoftware.App.RuntimeLog.Swallowed(ex);   // FAULTTIER_02 — no failure is silent.
        }
        finally
        {
            _activeMergerWorker?.Dispose();
            _activeMergerWorker = null;
            _mergeCts?.Dispose();
            _mergeCts = null;
        }
    }

    private void UpdateQueueState()
    {
        var videoList = VideoListCtl;
        int selectedIndex = videoList?.SelectedIndex ?? -1;
        int count = VideoQueue.Count;

        var mergeBtn = this.FindControl<Button>("MergeButton");
        if (mergeBtn != null)
        {
            mergeBtn.IsEnabled = count >= 1;
            ToolTip.SetTip(mergeBtn, count >= 1 ? "Merge/Process all listed videos" : "Add at least one video to enable processing");
        }

        var addMusicBtn = AddMusicButtonCtl;
        if (addMusicBtn != null)
        {
            var txt = this.FindControl<TextBlock>("AddMusicText");
            bool canAddMusic = count >= 1;
            addMusicBtn.IsEnabled = canAddMusic;
            if (!canAddMusic)
            {
                _musicResult = null;
                _musicIsStale = false;
                _musicQueueSignature = "";
                addMusicBtn.Classes.Clear();
                addMusicBtn.Classes.Add("Primary");
                if (txt != null) txt.Text = " ADD MUSIC ";
                ToolTip.SetTip(addMusicBtn, "Add at least one video before adding music");
            }
            else if (_musicIsStale)
            {
                addMusicBtn.Classes.Clear();
                addMusicBtn.Classes.Add("Danger");
                if (txt != null) txt.Text = " ⚠ MUSIC STALE — RE-SETUP ";
                ToolTip.SetTip(addMusicBtn, "The video queue changed after music was set up. Click to re-configure music.");
            }
            else if (_musicResult != null)
            {
                addMusicBtn.Classes.Clear();
                addMusicBtn.Classes.Add("Primary");
                if (txt != null) txt.Text = " MUSIC ADDED ";
                ToolTip.SetTip(addMusicBtn, "Music: " + System.IO.Path.GetFileName(_musicResult.MusicFilePath));
            }
            else
            {
                addMusicBtn.Classes.Clear();
                addMusicBtn.Classes.Add("Primary");
                if (txt != null) txt.Text = " ADD MUSIC ";
                ToolTip.SetTip(addMusicBtn, "Add background music to the merged video");
            }
        }

        var selectedIndices = videoList?.SelectedItems?.Cast<string>().Select(x => VideoQueue.IndexOf(x)).Where(x => x >= 0).ToList() ?? new System.Collections.Generic.List<int>();
        var removeBtn = this.FindControl<Button>("RemoveVideoButton");
        if (removeBtn != null) removeBtn.IsEnabled = selectedIndices.Count > 0;
        var moveUpBtn = this.FindControl<Button>("MoveUpButton");
        if (moveUpBtn != null) moveUpBtn.IsEnabled = selectedIndices.Count > 0 && selectedIndices.Min() > 0;
        var moveDownBtn = this.FindControl<Button>("MoveDownButton");
        if (moveDownBtn != null) moveDownBtn.IsEnabled = selectedIndices.Count > 0 && selectedIndices.Max() < count - 1;

        var emptyText = this.FindControl<TextBlock>("EmptyQueueText");
        if (emptyText != null) emptyText.IsVisible = count == 0;

        if (_musicIsStale) { SetQueueStatus("⚠ Music setup is stale — queue changed. Re-run Add Music before merging.", true); }
        else if (count == 0) SetQueueStatus("Waiting for videos.", false);
        else if (count == 1) SetQueueStatus("Ready to process 1 video.", false);
        else SetQueueStatus($"Ready to merge {count} videos.", false);
        UpdateNormalizeInfo();
        UpdateOutputPathDisplay();
        UpdatePreviewAvailable();
    }

    /// <summary>
    /// ISSUE_09 — writes the queue status line AND floats the suite-wide notice.
    /// The line stays because it is the durable record of the queue's state; the notice is what
    /// makes a change register while the user is looking at the video, not at the list.
    /// Every call site here is a discrete queue event, and FloatingNotice dedupes the one message
    /// that RefreshActionButtons re-emits on every selection change.
    ///
    /// ISSUE_10 — the two literal hexes that used to be here (#fecaca / #94a3b8) are gone. They were
    /// pale-on-dark by construction and unreadable on the Light theme's near-white panel.
    /// </summary>
    private void SetQueueStatus(string message, bool isError)
    {
        var status = this.FindControl<TextBlock>("QueueStatusText");
        if (status != null)
        {
            status.Text = message;
            status.Foreground = Infrastructure.ThemeResources.Brush(
                                    status,
                                    isError ? "AppDangerBrush" : "AppTextMutedBrush",
                                    new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(isError ? "#dc2626" : "#b6c2d0")));
        }

        Controls.FloatingNotice.Show(this, message,
            isError ? Controls.NoticeKind.Error : Controls.NoticeKind.Info);
    }

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<Avalonia.Controls.Border>("TitleBarBorder");
        if (titleBar != null)
        {
            titleBar.IsHitTestVisible = true;
            titleBar.DoubleTapped += (s, e) =>
            {
                this.WindowState = this.WindowState == Avalonia.Controls.WindowState.Maximized ? Avalonia.Controls.WindowState.Normal : Avalonia.Controls.WindowState.Maximized;
                e.Handled = true;
            };
            titleBar.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount < 2) { try { BeginMoveDrag(e); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); } }
            };
        }
    }

    private void MergerKeyDownHandler(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (FortniteVideoSoftware.App.Controls.PhaseOverlayControl.FightInputActive) return;

        if (Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Avalonia.Controls.TextBox or Avalonia.Controls.NumericUpDown) return;
        var fEl = Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (fEl is Avalonia.Controls.Button sb && sb.Name == "SpeakerHitBox" && e.Key == Avalonia.Input.Key.Space) return;

        // AUTOPREVIEW_01 — the keyboard transport is the same gesture as the buttons, so it takes
        // manual control the same way. Up/Down below do NOT: they reorder the queue, which is
        // browsing the list rather than watching the video.
        if (e.Key == Avalonia.Input.Key.Space)
        {
            TakeManualControl();
            if (_videoHost?.IpcClient != null) _ = _videoHost.IpcClient.SetPropertyAsync("pause", _videoHost.IpcClient.IsPaused ? "no" : "yes");
            e.Handled = true;
        }
        else if (e.Key == Avalonia.Input.Key.Left) { TakeManualControl(); _ = _videoHost?.IpcClient?.SendCommandAsync("seek", -5); e.Handled = true; }
        else if (e.Key == Avalonia.Input.Key.Right) { TakeManualControl(); _ = _videoHost?.IpcClient?.SendCommandAsync("seek", 5); e.Handled = true; }
        else if (e.Key == Avalonia.Input.Key.Up) { MoveVideo(-1); e.Handled = true; }
        else if (e.Key == Avalonia.Input.Key.Down) { MoveVideo(1); e.Handled = true; }
    }

    private void InitializeComponent() { AvaloniaXamlLoader.Load(this); }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // AUTOPREVIEW_01 — BROWSING THE QUEUE PLAYS IT, SILENTLY, FROM THE MIDDLE, AND KEEPS GOING.
    //
    // THE PROBLEM. Selecting a clip loaded it and pressed play — at full volume, from frame zero,
    // and it stopped dead at the end of that one clip. Frame zero of a gameplay capture is a
    // loading screen or a black fade, so the one frame the preview showed was the one frame that
    // says nothing about the clip. And clicking down a queue of twelve meant twelve bursts of
    // full-volume game audio, each one starting over the top of the last.
    //
    // THE BEHAVIOUR NOW, and the reasoning for each half of it:
    //
    //   SILENT WHILE BROWSING. Highlighting a clip plays it muted. Browsing is a LOOKING gesture —
    //   the user is answering "is this the right clip", which is a question about the picture. The
    //   moment they touch a transport control they have stopped browsing and started WATCHING, so
    //   the sound comes on and stays on for the rest of the session (_previewMuted is sticky; see
    //   TakeManualControl). The audio controls count as the same intent: someone reaching for the
    //   volume slider or the speaker while muted is asking for sound, and leaving them muted after
    //   they drag it is a bug, not a feature.
    //
    //   FROM THE MIDDLE. The midpoint of a clip is the part that is actually representative of it:
    //   no intro, no fade, no end card. Where the duration is already known the seek is handed to
    //   mpv as loadfile's start position, so playback OPENS at the middle with no visible jump;
    //   where it is not known yet the tick below performs it as soon as mpv reports a duration, and
    //   caches that duration so the same clip opens instantly the next time.
    //
    //   THROUGH TO THE END OF THE LIST. On EOF the selection advances one row, which re-enters this
    //   same path, so the queue plays itself down to the last clip in the order shown and then
    //   stops. That order is the merge order, so this doubles as a rehearsal of the finished video.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sticky. Starts muted; the first deliberate transport or audio action clears it for good.
    /// </summary>
    private bool _previewMuted = true;

    /// <summary>
    /// The clip whose midpoint seek has not landed yet, or null. Doubles as the "a load is still in
    /// flight" flag that stops <see cref="PlaybackTimer_Tick"/> mistaking a stale EOF for the end of
    /// the new clip.
    /// </summary>
    private string? _pendingMiddleSeekPath;

    /// <summary>
    /// Tick budget for the midpoint seek. mpv reports Duration within a tick or two of opening a
    /// local file; if it has not after this many, the clip is unusual (a stream, a broken index)
    /// and playing it from the start is a better outcome than never playing it.
    /// </summary>
    private int _middleSeekTicksLeft;

    private const int MiddleSeekTickBudget = 40;   // 40 x 100ms = 4s

    /// <summary>True while an auto-preview is running and EOF should advance to the next clip.</summary>
    private bool _autoAdvanceArmed;

    /// <summary>
    /// Ticks to ignore EOF for after a load. mpv's IsEof belongs to whatever file it last finished,
    /// and it stays true across the gap between loadfile being sent and the new file actually being
    /// open — so without this, selecting a clip while the previous one had ended would read that
    /// stale EOF on the very next tick and skip straight past the clip the user just clicked.
    /// Covers both load paths: the cached-duration one clears the pending-seek flag immediately and
    /// would otherwise have no guard at all.
    /// </summary>
    private int _autoAdvanceGraceTicks;

    private const int AutoAdvanceGraceTickCount = 8;   // 8 x 100ms = 0.8s

    /// <summary>
    /// Per-clip durations in seconds, filled by <see cref="PaintSizeEstimate"/> (which already
    /// measures every clip for the size estimate) and topped up from mpv whenever a clip plays. It
    /// exists so the midpoint seek can usually be handed to loadfile directly instead of being
    /// performed after the fact — that is the difference between opening at the middle and visibly
    /// jumping there half a second in.
    /// </summary>
    private readonly Dictionary<string, double> _clipDurations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// AUTOPREVIEW_01 — loads a clip and starts it playing, muted (unless the user has taken
    /// control) and from its midpoint.
    /// </summary>
    private void StartAutoPreview(string path)
    {
        var ipc = _videoHost?.IpcClient;
        if (ipc == null || string.IsNullOrWhiteSpace(path)) return;

        _isTimelineDrawn = false;
        this.FindControl<Avalonia.Controls.Canvas>("TimelineScaleCanvas")?.Children.Clear();

        // mute is a PLAYER property, not a per-file one, so it survives loadfile. Set anyway on
        // every load: it is idempotent, and it is the one line that must never be missed.
        _ = ipc.SetPropertyAsync("mute", _previewMuted ? "yes" : "no");

        double? startAt = null;
        if (_clipDurations.TryGetValue(path, out double known) && known > 1.0)
        {
            startAt = known / 2.0;
            _pendingMiddleSeekPath = null;          // handled by loadfile; nothing left to do
            _middleSeekTicksLeft = 0;
        }
        else
        {
            // Duration unknown. Let it open at the start and have the tick move it as soon as mpv
            // knows how long the file is — see the midpoint block in PlaybackTimer_Tick.
            _pendingMiddleSeekPath = path;
            _middleSeekTicksLeft = MiddleSeekTickBudget;
        }

        _autoAdvanceArmed = true;
        _autoAdvanceGraceTicks = AutoAdvanceGraceTickCount;
        _ = ipc.LoadFileAsync(path, startAt);
        _ = ipc.SetPropertyAsync("pause", "no");
    }

    /// <summary>
    /// AUTOPREVIEW_01 — the user has stopped browsing and started watching, so the sound comes on.
    ///
    /// Called from every transport control and every audio control. NOT called from the list
    /// selection, which is the browsing gesture this whole mode exists to serve, and not from a
    /// timeline scrub either — hunting for a frame is still looking, not watching.
    ///
    /// Sticky by design: having asked for sound once, the user should not have to ask again on the
    /// next clip they click.
    /// </summary>
    private void TakeManualControl()
    {
        if (!_previewMuted) return;

        _previewMuted = false;
        _ = _videoHost?.IpcClient?.SetPropertyAsync("mute", "no");
        RuntimeLog.Info("MERGER", "User took manual control of the preview — sound on for the rest of the session.");
        Controls.FloatingNotice.Show(this, "Sound on.", Controls.NoticeKind.Info);
    }

    /// <summary>
    /// AUTOPREVIEW_01 — moves the highlight to the next clip in the list when the current one ends.
    ///
    /// Moves the SELECTION rather than loading the next path directly, for two reasons: the list
    /// then shows what is playing (otherwise the highlight and the picture disagree, which is worse
    /// than not advancing at all), and the selection handler is already the one road into
    /// <see cref="StartAutoPreview"/>, so there is no second copy of the open-a-clip logic to drift.
    ///
    /// Stops at the end of the list. It does not wrap: a queue that loops forever is a queue the
    /// user has to actively stop, and there is no obvious control here for stopping it.
    /// </summary>
    private void AdvanceToNextClip()
    {
        _autoAdvanceArmed = false;   // re-armed by StartAutoPreview if there is a next clip

        var list = VideoListCtl;
        if (list == null) return;

        int index = list.SelectedIndex;
        if (index < 0 || index >= VideoQueue.Count - 1)
        {
            RuntimeLog.Info("MERGER", "Auto-preview reached the last clip in the queue.");
            return;
        }

        RuntimeLog.Debug("MERGER", $"Auto-preview advancing to clip {index + 2} of {VideoQueue.Count}.");
        list.SelectedIndex = index + 1;   // fires SelectionChanged -> StartAutoPreview
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_videoHost?.IpcClient == null) return;

        // ── AUTOPREVIEW_01, part 1: the midpoint seek for a clip whose length was not known when
        //    it was opened. Runs once, as soon as mpv reports a duration, then caches it so this
        //    clip is opened at its middle directly next time.
        if (_pendingMiddleSeekPath != null)
        {
            double knownDuration = _videoHost.IpcClient.Duration;
            if (knownDuration > 1.0)
            {
                _clipDurations[_pendingMiddleSeekPath] = knownDuration;
                _pendingMiddleSeekPath = null;
                _middleSeekTicksLeft = 0;
                _ = SeekInternal(knownDuration / 2.0);
            }
            else if (--_middleSeekTicksLeft <= 0)
            {
                RuntimeLog.Debug("MERGER", $"No duration from mpv for '{System.IO.Path.GetFileName(_pendingMiddleSeekPath)}'; leaving the preview at the start.");
                _pendingMiddleSeekPath = null;
            }
        }

        // ── AUTOPREVIEW_01, part 2: end of clip -> next clip.
        //    Gated on the midpoint seek having finished, because mpv can still be reporting the
        //    PREVIOUS file's EOF while the new one is opening, and acting on that would skip a clip
        //    the instant it was selected.
        if (_autoAdvanceGraceTicks > 0) _autoAdvanceGraceTicks--;

        if (_autoAdvanceArmed && _autoAdvanceGraceTicks == 0 && _pendingMiddleSeekPath == null && _videoHost.IpcClient.IsEof)
        {
            AdvanceToNextClip();
        }

        var playIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon");
        var pauseIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PauseIcon");
        if (playIcon != null && pauseIcon != null)
        {
            playIcon.IsVisible = _videoHost.IpcClient.IsPaused;
            pauseIcon.IsVisible = !_videoHost.IpcClient.IsPaused;
        }

        double time = _videoHost.IpcClient.CurrentTime;
        double dur = _videoHost.IpcClient.Duration;
        double displayTime = dur > 0 ? Math.Clamp(time, 0, dur) : Math.Max(0, time);

        var timeElapsed = this.FindControl<TextBlock>("TimeElapsed");
        if (timeElapsed != null)
            timeElapsed.Text = dur >= 3600 || displayTime >= 3600 ? TimeSpan.FromSeconds(displayTime).ToString("hh\\:mm\\:ss") : TimeSpan.FromSeconds(displayTime).ToString("mm\\:ss");

        var timeRemaining = this.FindControl<TextBlock>("TimeRemaining");
        if (timeRemaining != null)
        {
            double remaining = Math.Max(0, dur - displayTime);
            timeRemaining.Text = "-" + (dur >= 3600 || remaining >= 3600 ? TimeSpan.FromSeconds(remaining).ToString("hh\\:mm\\:ss") : TimeSpan.FromSeconds(remaining).ToString("mm\\:ss"));
        }

        var timelineSlider = this.FindControl<Slider>("TimelineSlider");
        if (timelineSlider != null && dur > 0)
        {
            _isTimerUpdatingSlider = true;
            timelineSlider.Value = Math.Clamp((time / dur) * 100.0, 0.0, 100.0);
            _isTimerUpdatingSlider = false;
        }

        var canvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineMarkersCanvas");
        var scaleCanvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineScaleCanvas");
        if (canvas != null && dur > 0 && !_isTimelineDrawn)
        {
            DrawTimelineScale(scaleCanvas, canvas.Bounds.Width, dur);
            _isTimelineDrawn = true;
        }
    }

    private void DrawTimelineScale(Avalonia.Controls.Canvas? scaleCanvas, double canvasWidth, double duration)
    {
        if (scaleCanvas == null || canvasWidth <= 0) return;
        scaleCanvas.Children.Clear();
        double tickInterval = 5;
        if (duration > 3600) tickInterval = 300;
        else if (duration > 1800) tickInterval = 60;
        else if (duration > 300) tickInterval = 30;
        else if (duration > 60) tickInterval = 10;

        for (double t = 0; t <= duration; t += tickInterval)
        {
            double tx = (t / duration) * canvasWidth;
            if (t > 0.001 && duration - t > 0.001)
            {
                var tickText = new TextBlock { Text = TimeSpan.FromSeconds(t).ToString(t >= 3600 ? "h\\:mm\\:ss" : "m\\:ss"), Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(180, 255, 255, 255)), FontSize = Infrastructure.ThemeManager.ScaledFontSize(9) };
                Avalonia.Controls.Canvas.SetLeft(tickText, Math.Max(0, Math.Min(Math.Max(0, canvasWidth - 36), tx + 2)));
                Avalonia.Controls.Canvas.SetTop(tickText, 0);
                scaleCanvas.Children.Add(tickText);
            }
        }
    }

    private async void InitializeMpv()
    {
        _videoHost = this.FindControl<MpvVideoView>("VideoHost");
        WirePreviewDetach();
        if (_videoHost != null)
        {
            string mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "frontend", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath))
                mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath)) mpvPath = "mpv.exe";
            await _videoHost.StartMpvProcessAsync(mpvPath);
            if (_videoHost.IpcClient != null)
            {
                _videoHost.IpcClient.SeekCompleted += () => {
                    Avalonia.Threading.Dispatcher.UIThread.Post(async () => {
                        _isSeeking = false;
                        if (_nextSeekTarget.HasValue) { double target = _nextSeekTarget.Value; _nextSeekTarget = null; await SeekInternal(target); }
                    });
                };
                try
                {
                    var state = await new StateTransferStore(_paths).LoadAsync();
                    if (state.TryGetPropertyValue("MainVolume", out var volNode))
                    {
                        var volSlider = VolumeSliderCtl;
                        if (volSlider != null) volSlider.Value = volNode?.GetValue<double>() ?? 100.0;
                    }
                }
                catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            }
        }
    }

    private async Task SeekInternal(double time)
    {
        if (_isSeeking) { _nextSeekTarget = time; return; }
        _isSeeking = true;
        if (_videoHost?.IpcClient != null) await _videoHost.IpcClient.SendCommandAsync("seek", time, "absolute");
    }

    private void ReturnToMainApp()
    {
        try
        {
            var store = new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths);
            store.SendHandoffSync(new FortniteVideoSoftware.Core.Ipc.HandoffPayload
            {
                SourceProcess = "VideoMerger",
                TargetProcess = "MainWindow"
            });
        }
        catch (System.Exception ex) { RuntimeLog.Debug("IPC", $"Merger return handoff: {ex.Message}"); }

        _recovery.ReleaseLockOnly();
        ShutdownVideoPipeline();

        // TOOLNAV_04 — see CropToolWindow. Opened in-process, the editor is hidden behind this
        // window; relaunching the exe would produce a second application alongside it.
        if (Services.ToolNavigator.OpenedInProcess)
        {
            RuntimeLog.Info("MERGER", "Opened in-process — closing to reveal the editor (TOOLNAV_04).");
            Close();
            return;
        }

        string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "FortniteVideoSoftware.exe";
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath, "run-ui") { UseShellExecute = false });
        if (p != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    for (int i = 0; i < 40; i++)
                    {
                        if (p.HasExited) break;
                        p.Refresh();
                        if (p.MainWindowHandle != IntPtr.Zero) break;
                        await Task.Delay(50);
                    }
                }
                catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                Environment.Exit(0);
            });
        }
        else Environment.Exit(0);
    }

    public void OnSuccessAction(string action)
    {
        if (action == "whatsapp") Process.Start(new ProcessStartInfo("cmd", "/c start whatsapp://send?text=CheckOutThisVideo") { CreateNoWindow = true });
        else if (action == "folder") Process.Start(new ProcessStartInfo("explorer.exe", _outputDirectory ?? ".") { CreateNoWindow = true });
        Close();
    }

    private bool _videoPipelineShutDown;

    private void ShutdownVideoPipeline()
    {
        if (_videoPipelineShutDown) return;
        _videoPipelineShutDown = true;

        try { _previewDetach?.Attach(); }
        catch (Exception ex) { RuntimeLog.Fail("UI", $"Could not reattach the preview during shutdown: {ex.Message}"); }

        try { _videoHost?.Dispose(); }
        catch (Exception ex) { RuntimeLog.Fail("UI", $"Video preview teardown reported: {ex.Message}"); }
    }

    protected override async void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        _mergerSizeWorker?.Dispose();
        try { _playbackTimer?.Stop(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        if (_isSafeToClose) { base.OnClosing(e); return; }
        e.Cancel = true;
        if (_mergerSizeWorker != null)
            await Task.WhenAny(_mergerSizeWorker.Completion, Task.Delay(1000));
        try { await WindowBoundsHelper.SaveBoundsAsync(this, "VideoMergerBounds"); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        this.Hide();
        try
        {
            if (_videoHost?.IpcClient != null)
            {
                var stopTask = _videoHost.IpcClient.SendCommandAsync("stop");
                var timeoutTask = Task.Delay(500);
                await Task.WhenAny(stopTask, timeoutTask);
            }
            ShutdownVideoPipeline();
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        finally { _isSafeToClose = true; this.Close(); }
    }

    /// <summary>
    /// MERGERBOTTOM_01 — one trim entry per clip, all of them full-length.
    ///
    /// WHAT WAS HERE. A <c>_clipTrims</c> dictionary plus SetClipIn / SetClipOut / ClearClipTrim /
    /// UpdateTrimStatus / SetTrimStatus / SelectedQueuePath, driven by the SET IN, SET OUT and FULL
    /// CLIP buttons and reported in a ClipTrimStatusText line. All of that UI is gone from this
    /// screen, so the dictionary could only ever have been empty and every one of those methods was
    /// unreachable code writing to a TextBlock that no longer exists.
    ///
    /// <see cref="MergerWorker"/> still takes a trim list and still expects exactly one entry per
    /// queued clip — its indexing is positional — so the list itself stays. A
    /// <c>ClipTrim(0, 0)</c> means "use the whole clip", which is what every clip now does.
    /// If per-clip trimming ever returns, it returns as a real editor on the preview, not as three
    /// buttons that silently mutate a dictionary the user cannot see.
    /// </summary>
    private List<FortniteVideoSoftware.Core.Media.MergerWorker.ClipTrim> BuildClipTrimList()
    {
        var list = new List<FortniteVideoSoftware.Core.Media.MergerWorker.ClipTrim>(VideoQueue.Count);
        for (int i = 0; i < VideoQueue.Count; i++)
        {
            list.Add(new FortniteVideoSoftware.Core.Media.MergerWorker.ClipTrim(0, 0));
        }
        return list;
    }

    /// <summary>
    /// QUEUEMENU_01 — wires the three buttons inside the right-click menu.
    ///
    /// NOT via <c>this.FindControl</c>, and that is the whole point of this method existing. A
    /// Flyout's content is constructed when the AXAML loads but is never attached to the window's
    /// VISUAL tree until the flyout is first opened, and whether its names reach the window's
    /// NameScope is an implementation detail of the XAML loader rather than a contract. A
    /// FindControl that silently returns null gives three menu items that look perfectly normal and
    /// do nothing at all — the exact failure mode this codebase has been bitten by before (see
    /// ISSUE_09's dead `TextBox.error` class in AvaloniaApp.axaml).
    ///
    /// Walking the flyout content's own LOGICAL tree needs no name scope and cannot be defeated by
    /// virtualisation: the Border, its StackPanel and the Buttons are real objects from the moment
    /// the AXAML is parsed. Matching on Button.Name keeps the AXAML the single place the names are
    /// written down.
    /// </summary>
    private void WireQueueContextMenu()
    {
        var list = VideoListCtl;
        if (list?.ContextFlyout is not Flyout flyout || flyout.Content is not Control content) return;

        int wired = 0;
        foreach (Button button in content.GetLogicalDescendants().OfType<Button>())
        {
            switch (button.Name)
            {
                case "CtxMoveUpButton":
                    button.Click += (s, e) => { CloseQueueContextFlyout(); MoveVideo(-1); };
                    wired++;
                    break;
                case "CtxMoveDownButton":
                    button.Click += (s, e) => { CloseQueueContextFlyout(); MoveVideo(1); };
                    wired++;
                    break;
                case "CtxRemoveButton":
                    button.Click += (s, e) => { CloseQueueContextFlyout(); ExecuteRemoveSelected(); };
                    wired++;
                    break;
            }
        }

        if (wired != 3)
        {
            // Loud, because a half-wired context menu is invisible until a user right-clicks and
            // nothing happens. If this ever fires, the AXAML and this switch have drifted apart.
            RuntimeLog.Fail("Merger", $"Queue context menu wired {wired}/3 actions — check the button names in VideoMergerWindow.axaml.");
        }
    }

    /// <summary>
    /// QUEUEMENU_01 — closes the right-click menu after one of its buttons has been pressed.
    ///
    /// A <see cref="Flyout"/> hosting hand-built content (rather than a MenuFlyout hosting
    /// MenuItems) has no idea that a click on one of its buttons means "the user is finished", so
    /// without this the menu stays open over the list it has just reordered — which looks exactly
    /// like the click did nothing. Walks up from the button to the flyout's popup host rather than
    /// holding a field, because the flyout is created by the AXAML and belongs to the ListBox.
    /// </summary>
    private void CloseQueueContextFlyout()
    {
        var list = VideoListCtl;
        list?.ContextFlyout?.Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        // PROJ_11 — the last word on the queue, before this window and its collection stop
        // existing. Everything below is teardown; this is the one line that makes the user's
        // merge survive the window it was assembled in.
        PublishQueueToProject();

        _mergerSizeWorker?.Dispose();
        Controls.CoachOverlay.Cancel(this);
        Controls.FloatingNotice.Clear(this);
        base.OnClosed(e);
    }

    private Avalonia.Point? _videoDragStartPoint;
    private bool _isVideoDragging;

    private void VideoList_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(sender as Avalonia.Controls.Control);
        if (point.Properties.IsLeftButtonPressed) { _videoDragStartPoint = point.Position; return; }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // QUEUEMENU_01 — A RIGHT-CLICK HAS TO AIM AT SOMETHING.
        //
        // Avalonia does NOT move the selection on a right-press: ContextFlyout opens on the
        // ContextRequested event, which fires on RELEASE, and by then the selection is still
        // whatever the user last left-clicked. Without this, right-clicking clip 5 would open a
        // menu whose "Remove from list" removed clip 2 — the classic context-menu-aimed-at-the-
        // wrong-row bug, and a destructive one here.
        //
        // PointerPressed runs BEFORE ContextRequested, so setting the selection here means the menu
        // is already pointing at the right row by the time it appears. A right-press INSIDE an
        // existing multi-selection leaves that selection alone, because "remove these five" is a
        // real intent and stamping it down to one row would quietly destroy it.
        // ══════════════════════════════════════════════════════════════════════════════════════
        if (!point.Properties.IsRightButtonPressed) return;

        var list = VideoListCtl;
        if (list == null) return;

        var item = (e.Source as Avalonia.Controls.Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item?.DataContext is not string clicked) return;

        if (list.SelectedItems != null && list.SelectedItems.Contains(clicked)) return;

        list.SelectedItems?.Clear();
        list.SelectedItem = clicked;
    }

    private async void VideoList_PointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (_videoDragStartPoint.HasValue && !_isVideoDragging)
        {
            var point = e.GetCurrentPoint(sender as Avalonia.Controls.Control);
            var diff = point.Position - _videoDragStartPoint.Value;
            if (Math.Abs(diff.X) > 3 || Math.Abs(diff.Y) > 3)
            {
                var source = e.Source as Avalonia.Controls.Control;
                if (source?.DataContext is string itemText)
                {
                    _isVideoDragging = true;
                    var videoList = VideoListCtl;
                    if (videoList != null)
                    {
                        foreach (var container in videoList.GetRealizedContainers().Cast<ListBoxItem>())
                            if (container.DataContext is string s && s == itemText) { container.Opacity = 0.3; break; }
                    }
                    var dragData = new Avalonia.Input.DataObject();
                    dragData.Set("VideoItem", itemText);
                    await Avalonia.Input.DragDrop.DoDragDrop(e, dragData, Avalonia.Input.DragDropEffects.Move);
                    _videoDragStartPoint = null;
                    _isVideoDragging = false;
                    if (videoList != null) foreach (var container in videoList.GetRealizedContainers().Cast<ListBoxItem>()) container.Opacity = 1.0;
                }
            }
        }
    }

    private void VideoList_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e) { _videoDragStartPoint = null; }

    private void VideoList_DragOver(object? sender, Avalonia.Input.DragEventArgs e)
    {
        if (e.Data.Contains("VideoItem")) { e.DragEffects = Avalonia.Input.DragDropEffects.Move; SetVideoListDragState(true); ShowDropIndicator(e); }
        else if (e.Data.Contains(Avalonia.Input.DataFormats.Files) || e.Data.GetFiles()?.Any() == true)
        {
            e.DragEffects = Avalonia.Input.DragDropEffects.Copy;
            SetVideoListDragState(true);
            HideDropIndicator();
        }
        else { e.DragEffects = Avalonia.Input.DragDropEffects.None; SetVideoListDragState(false); HideDropIndicator(); }
    }

    private void VideoList_DragLeave(object? sender, Avalonia.Input.DragEventArgs e) { SetVideoListDragState(false); HideDropIndicator(); }

    private void VideoList_Drop(object? sender, Avalonia.Input.DragEventArgs e)
    {
        SetVideoListDragState(false);
        HideDropIndicator();
        if (e.Data.Contains("VideoItem"))
        {
            string? itemToMove = e.Data.Get("VideoItem") as string;
            if (itemToMove == null) return;
            int targetIndex = ComputeDropIndex(e);
            int oldIndex = VideoQueue.IndexOf(itemToMove);
            if (oldIndex >= 0 && targetIndex >= 0 && targetIndex != oldIndex)
            {
                VideoQueue.RemoveAt(oldIndex);
                if (targetIndex > oldIndex) targetIndex--;
                VideoQueue.Insert(Math.Clamp(targetIndex, 0, VideoQueue.Count), itemToMove);
            }
            var videoList = VideoListCtl;
            if (videoList != null) foreach (var container in videoList.GetRealizedContainers().Cast<ListBoxItem>()) container.Opacity = 1.0;
        }
        else
        {
            var dropped = e.Data.GetFiles();
            if (dropped != null)
            {
                _ = AddExternalVideosAsync(dropped.Select(f => f.Path.LocalPath).ToArray());
            }
        }
    }

    /// <summary>
    /// Adds externally provided video files (Explorer drag &amp; drop via OLE or the
    /// WM_DROPFILES fallback) to the queue with the exact same validation and
    /// duplicate detection as the Upload Files button.
    /// </summary>
    private async Task AddExternalVideosAsync(string[] paths)
    {
        await _externalDropGate.WaitAsync();
        try
        {
            await AddExternalVideosCoreAsync(paths);
        }
        finally
        {
            _externalDropGate.Release();
        }
    }

    private async Task AddExternalVideosCoreAsync(string[] paths)
    {
        int addedCount = 0;
        int skippedCount = 0;
        int unsupportedCount = 0;

        foreach (var rawPath in paths)
        {
            string ext = System.IO.Path.GetExtension(rawPath).ToLowerInvariant();
            if (ext is not (".mp4" or ".mkv" or ".avi" or ".mov"))
            {
                unsupportedCount++;
                continue;
            }

            string path = NormalizeVideoPath(rawPath);
            if (!await ShouldAddVideoToQueueAsync(path))
            {
                skippedCount++;
                continue;
            }

            VideoQueue.Add(path);
            addedCount++;
        }

        if (addedCount > 0)
        {
            var vl = VideoListCtl;
            if (vl != null && vl.SelectedIndex < 0 && VideoQueue.Count > 0) vl.SelectedIndex = 0;
        }

        string status = addedCount == 0 && skippedCount == 0 && unsupportedCount > 0
            ? "Drop MP4, MKV, AVI, or MOV files."
            : skippedCount > 0 || unsupportedCount > 0
                ? $"{addedCount} video file(s) added. {skippedCount + unsupportedCount} file(s) skipped."
                : $"{addedCount} video file(s) added.";
        SetQueueStatus(status, addedCount == 0);
        RuntimeLog.Info("UI", $"Added {addedCount} external video file(s) via drag & drop.");
    }

    private int ComputeDropIndex(Avalonia.Input.DragEventArgs e)
    {
        var videoList = VideoListCtl;
        if (videoList == null) return 0;
        var pos = e.GetPosition(videoList);
        var containers = videoList.GetRealizedContainers().Cast<ListBoxItem>().ToList();
        for (int idx = 0; idx < containers.Count; idx++)
        {
            var bounds = containers[idx].Bounds;
            if (pos.X >= bounds.Left && pos.X <= bounds.Right &&
                pos.Y >= bounds.Top && pos.Y <= bounds.Bottom)
            {
                double midY = bounds.Top + bounds.Height / 2.0;
                return pos.Y < midY ? idx : idx + 1;
            }
        }
        var lastBounds = containers.Last().Bounds;
        var endPos = e.GetPosition(videoList);
        return endPos.Y > lastBounds.Bottom ? VideoQueue.Count : 0;
    }

    private void ShowDropIndicator(Avalonia.Input.DragEventArgs e)
    {
        var indicator = this.FindControl<Border>("DropIndicator");
        var videoList = VideoListCtl;
        if (indicator == null || videoList == null) return;
        int targetIndex = ComputeDropIndex(e);
        var containers = videoList.GetRealizedContainers().Cast<ListBoxItem>().ToList();
        if (containers.Count == 0) return;
        double y = 0;
        if (targetIndex >= VideoQueue.Count)
        {
            var last = containers.LastOrDefault(c => c.DataContext is string dc && VideoQueue.IndexOf(dc) == VideoQueue.Count - 1);
            if (last != null) y = last.Bounds.Bottom;
        }
        else
        {
            var target = containers.FirstOrDefault(c => c.DataContext is string dc && VideoQueue.IndexOf(dc) == targetIndex);
            if (target != null) y = target.Bounds.Top;
        }
        indicator.IsVisible = true;
        indicator.Margin = new Avalonia.Thickness(4, y - 1.5, 4, 0);
        indicator.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
    }

    private void HideDropIndicator() { var indicator = this.FindControl<Border>("DropIndicator"); if (indicator != null) indicator.IsVisible = false; }

    private void SetVideoListDragState(bool active)
    {
        var frame = this.FindControl<Border>("VideoListFrame");
        if (frame == null) return;
        frame.Background = active
            ? Infrastructure.ThemeResources.Brush(frame, "AppPanelBrush", new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#334155")))
            : Infrastructure.ThemeResources.Brush(frame, "AppSurfaceBrush", new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1e293b")));
        frame.BorderBrush = active
            ? Infrastructure.ThemeResources.Brush(frame, "AppFocusInnerBrush", new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#38bdf8")))
            : Infrastructure.ThemeResources.Brush(frame, "AppBorderBrush", new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#475569")));
}

    private PreviewDetachController? _previewDetach;

    private void WirePreviewDetach()
    {
        var btn = this.FindControl<Button>("MergerDetachPreviewBtn");
        if (btn == null) return;

        _previewDetach = new PreviewDetachController(
            this,
            PreviewDetachController.MergerKey,
            "Preview Monitor — Video Merger",
            () => _videoHost);

        _previewDetach.StateChanged += detached =>
        {
            var watermark = this.FindControl<Avalonia.Controls.Border>("MergerPreviewDetachedWatermark");
            if (watermark != null) watermark.IsVisible = detached;
            _previewDetach!.SyncButton(btn);
        };

        _previewDetach.DetachUnavailable += why => RuntimeLog.Info("UI", why);

        btn.Click += (_, _) => _previewDetach.Toggle();
        _previewDetach.SyncButton(btn);
    }
}
