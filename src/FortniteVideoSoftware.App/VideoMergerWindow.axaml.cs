using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
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
    private MpvVideoView? _videoHost;
    private bool _isSeeking = false;
    private double? _nextSeekTarget = null;
    private bool _isUserDraggingSlider = false;
    private bool _isTimerUpdatingSlider = false;
    private bool _isTimelineDrawn = false;
    public ObservableCollection<string> VideoQueue { get; } = new();

    private MusicWizardResult? _musicResult;

    private bool _isSafeToClose = false;
    private MergerWorker? _activeMergerWorker;
    private System.Threading.CancellationTokenSource? _mergeCts;

    private Avalonia.Threading.DispatcherTimer? _playbackTimer;
    private readonly ApplicationPaths _paths = ApplicationPaths.CreateDefault();

    private double _baseSpeed = 1.0;
    private double _lastAppliedSpeed = 1.0;
    private double _previousVolume = 100;

    private string? _outputDirectory;
    private string _ffprobePath = "";

    private double _cachedTotalDurationSec = 0;
    private double _cachedTotalSourceSizeMB = 0;
    private double _cachedLowestBitrate = 5000;

    private bool _musicIsStale = false;
    private string _musicQueueSignature = "";

    private int _probeVersion = 0;
    private System.Threading.CancellationTokenSource? _probeCts;

    private readonly object _videoFingerprintLock = new();
    private readonly Dictionary<string, VideoFileFingerprint> _videoFingerprintCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<VideoFileFingerprint?>> _videoFingerprintTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.SemaphoreSlim _videoHashSemaphore = new(1, 1);

    public VideoMergerWindow()
    {
        InitializeComponent();
        FortniteVideoSoftware.App.WindowBoundsHelper.LoadBoundsSync(this, "VideoMergerBounds");

        _ffprobePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "backend", "ffprobe.exe");
        if (!System.IO.File.Exists(_ffprobePath))
            _ffprobePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "ffprobe.exe");
        if (!System.IO.File.Exists(_ffprobePath)) _ffprobePath = "ffprobe.exe";

        this.Loaded += async (s, e) => {
            InitializeMpv();

        };

        _playbackTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();

        InitializeControls();
        WireUpEvents();
        InitializeSliders();
        LoadOutputDirectory();

        FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged += OnGlobalMasterVolumeChanged;
        this.Closed += (s, e) => { FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged -= OnGlobalMasterVolumeChanged; };
    }

    private void InitializeControls()
    {
        var videoList = this.FindControl<ListBox>("VideoList");
        if (videoList != null)
        {
            videoList.ItemsSource = VideoQueue;
            videoList.SelectionChanged += (s, e) =>
            {
                if (videoList.SelectedItem is string path && _videoHost?.IpcClient != null)
                {
                    _ = _videoHost?.IpcClient?.LoadFileAsync(path);
                    _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "no");
                    _isTimelineDrawn = false;
                    this.FindControl<Avalonia.Controls.Canvas>("TimelineScaleCanvas")?.Children.Clear();
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
            timelineOverlay.PointerPressed += (s, e) => SeekTimelineFromPointer(e, canvas, timelineSlider);
        }
    }

    private void SeekTimelineFromPointer(Avalonia.Input.PointerPressedEventArgs e, Avalonia.Controls.Canvas timelineCanvas, Slider timelineSlider)
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
        if (menuExit != null) menuExit.Click += (s, e) => Environment.Exit(0);

        var returnToMainBtn = this.FindControl<Button>("ReturnToMainAppButton");
        if (returnToMainBtn != null) returnToMainBtn.Click += (s, e) => ReturnToMainApp();

        var playPauseBtn = this.FindControl<Button>("PlayPauseButton");
        if (playPauseBtn != null)
        {
            playPauseBtn.Click += (s, e) =>
            {
                if (_videoHost?.IpcClient != null)
                    _ = _videoHost.IpcClient.SetPropertyAsync("pause", _videoHost.IpcClient.IsPaused ? "no" : "yes");
            };
        }

        var fastBackwardBtn = this.FindControl<Button>("FastBackwardButton");
        if (fastBackwardBtn != null)
        {
            fastBackwardBtn.Click += (s, e) =>
            {
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
                if (_videoHost?.IpcClient != null)
                {
                    double dur = _videoHost.IpcClient.Duration;
                    double target = dur > 0 ? Math.Min(dur, _videoHost.IpcClient.CurrentTime + 10) : _videoHost.IpcClient.CurrentTime + 10;
                    _ = SeekInternal(target);
                }
            };
        }

        var overlayLayer = this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer");
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

        var menuOutputFolder = this.FindControl<MenuItem>("MenuOutputFolder");
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

        var menuRemoveSelected = this.FindControl<MenuItem>("MenuRemoveSelected");
        if (menuRemoveSelected != null)
        {
            menuRemoveSelected.Click += (s, e) =>
            {
                if (!FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.ConfirmVideoMergerRemove)
                {
                    Avalonia.Controls.Primitives.FlyoutBase.GetAttachedFlyout(menuRemoveSelected)?.Hide();
                    ExecuteRemoveSelected();
                }
                else
                {
                    Avalonia.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(menuRemoveSelected);
                }
            };
        }
        
        var confirmMenuRemoveBtn = this.FindControl<Button>("ConfirmMenuRemoveButton");
        if (confirmMenuRemoveBtn != null)
        {
            confirmMenuRemoveBtn.Click += (s, e) =>
            {
                if (menuRemoveSelected != null)
                    Avalonia.Controls.Primitives.FlyoutBase.GetAttachedFlyout(menuRemoveSelected)?.Hide();
                ExecuteRemoveSelected();
            };
        }

        var menuClearAll = this.FindControl<MenuItem>("MenuClearAll");
        if (menuClearAll != null)
        {
            menuClearAll.Click += (s, e) =>
            {
                if (!FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.ConfirmVideoMergerClearAll)
                {
                    Avalonia.Controls.Primitives.FlyoutBase.GetAttachedFlyout(menuClearAll)?.Hide();
                    VideoQueue.Clear();
                }
                else
                {
                    Avalonia.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(menuClearAll);
                }
            };
        }
        
        var confirmMenuClearAllBtn = this.FindControl<Button>("ConfirmMenuClearAllButton");
        if (confirmMenuClearAllBtn != null)
        {
            confirmMenuClearAllBtn.Click += (s, e) =>
            {
                if (menuClearAll != null)
                    Avalonia.Controls.Primitives.FlyoutBase.GetAttachedFlyout(menuClearAll)?.Hide();
                VideoQueue.Clear();
            };
        }

        var addMusicBtn = this.FindControl<Button>("AddMusicButton");
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

                var wizard = new MusicWizardWindow(VideoQueue.ToList(), _cachedTotalDurationSec);
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

        var removeBtn = this.FindControl<Button>("RemoveVideoButton");
        if (removeBtn != null)
        {
            removeBtn.Click += (s, e) =>
            {
                if (!FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.ConfirmVideoMergerRemove)
                {
                    removeBtn.Flyout?.Hide();
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
                ExecuteRemoveSelected();
            };
        }

        var moveUpBtn = this.FindControl<Button>("MoveUpButton");
        if (moveUpBtn != null) moveUpBtn.Click += (s, e) => MoveVideo(-1);

        var moveDownBtn = this.FindControl<Button>("MoveDownButton");
        if (moveDownBtn != null) moveDownBtn.Click += (s, e) => MoveVideo(1);

        var mergeBtn = this.FindControl<Button>("MergeButton");
        if (mergeBtn != null) mergeBtn.Click += async (s, e) => await OnMergeClicked(mergeBtn);

        WireUpVolumeSlider();
        AttachTitleBarDrag();
        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, MergerKeyDownHandler, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        UpdateQueueState();
    }
    
    private void ExecuteRemoveSelected()
    {
        var videoList = this.FindControl<ListBox>("VideoList");
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

        if (e.NewItems == null) return;
        foreach (var item in e.NewItems)
        {
            if (item is string path)
                StartVideoFingerprintWarmup(path);
        }
    }

    private void MoveVideo(int direction)
    {
        var videoList = this.FindControl<ListBox>("VideoList");
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
    /// ISSUE_010: Debounces quality/size probes to prevent probe storms during rapid changes.
    /// Cancels any in-flight probe and waits 300ms before launching a new one.
    /// </summary>
    private async void DebouncedQualityProbe()
    {
        int version = ++_probeVersion;
        try { _probeCts?.Cancel(); } catch { }
        try { _probeCts?.Dispose(); } catch { }
        _probeCts = new System.Threading.CancellationTokenSource();
        var token = _probeCts.Token;

        try
        {
            await Task.Delay(300, token);
            if (version != _probeVersion || token.IsCancellationRequested) return;
            await UpdateQualityProbeAsync(token, version);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            CoreLogger.Fail("VideoMerger", $"Quality probe debounce failed: {ex.Message}");
        }
    }

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
            var addMusicBtn = this.FindControl<Button>("AddMusicButton");
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
        var vl = this.FindControl<ListBox>("VideoList");
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
    }

    /// <summary>
    /// ISSUE_008: Keyboard handler for speaker mute toggle.
    /// </summary>
    private void SpeakerHitBox_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter || e.Key == Avalonia.Input.Key.Space)
        {
            ToggleMute();
            e.Handled = true;
        }
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

    private void UpdateQualityProbe()
    {
        int version = ++_probeVersion;
        _ = UpdateQualityProbeAsync(System.Threading.CancellationToken.None, version);
    }

    private async Task UpdateQualityProbeAsync(System.Threading.CancellationToken cancellationToken, int probeVersion)
    {
        var qs = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("QualitySlider");
        var label = this.FindControl<TextBlock>("QualityLabel");
        if (qs == null || label == null) return;

        int qualityPercent = (qs.Value + 1) * 5;

        if (VideoQueue.Count == 0)
        {
            label.Text = qualityPercent >= 100 ? "100% — Lossless" : $"{qualityPercent}%";
            label.Foreground = Avalonia.Media.Brush.Parse("#c5dcf2");
            return;
        }

        if (qualityPercent >= 100)
        {
            label.Text = "100% — Lossless";
            label.Foreground = Avalonia.Media.Brush.Parse("#2ecc71");
            await ProbeAndUpdateSizeAsync(qualityPercent, cancellationToken, probeVersion);
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProbeAndUpdateSizeAsync(qualityPercent, cancellationToken, probeVersion);
            cancellationToken.ThrowIfCancellationRequested();
            if (probeVersion != _probeVersion) return;

            int lowestW = 1920, lowestH = 1080;
            double lowestBitrate = _cachedLowestBitrate;

            foreach (var path in VideoQueue)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (probeVersion != _probeVersion) return;
                if (!System.IO.File.Exists(path)) continue;
                var prober = new MediaProber(_ffprobePath, path);
                var (w, h) = await prober.GetResolutionAsync();
                cancellationToken.ThrowIfCancellationRequested();
                var data = await prober.ProbeAsync();
                cancellationToken.ThrowIfCancellationRequested();

                double vbitrate = 0;
                var streams = data["streams"]?.AsArray();
                if (streams != null)
                {
                    foreach (var stream in streams)
                    {
                        if (stream?["codec_type"]?.ToString() == "video")
                        {
                            var brNode = stream["bit_rate"];
                            if (brNode != null && double.TryParse(brNode.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double br))
                                vbitrate = br / 1000.0;
                        }
                    }
                }
                if (vbitrate <= 0)
                {
                    var fmtBr = data["format"]?["bit_rate"];
                    if (fmtBr != null && double.TryParse(fmtBr.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double br))
                        vbitrate = br / 1000.0 * 0.9;
                }

                if (w * h < lowestW * lowestH || (w * h == lowestW * lowestH && vbitrate < lowestBitrate))
                {
                    lowestW = w; lowestH = h; lowestBitrate = vbitrate > 0 ? vbitrate : 5000;
                }
            }

            if (lowestBitrate <= 0) lowestBitrate = 5000;

            double effectiveBitrate = lowestBitrate * (qualityPercent / 100.0);
            double bpp = (effectiveBitrate * 1000.0) / (lowestW * lowestH * 60.0);
            bpp /= 1.5;

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

            for (int i = 0; i < spectrum.Length; i++)
            {
                if (bpp < spectrum[i].th)
                {
                    desc = spectrum[i].d;
                    color = spectrum[i].c;
                    double prev = i > 0 ? spectrum[i - 1].th : 0.0;
                    double mid = (spectrum[i].th + prev) / 2.0;
                    if (spectrum[i].th < 90.0)
                        desc += bpp < mid ? "-" : "+";
                    break;
                }
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (probeVersion != _probeVersion) return;
                var lbl = this.FindControl<TextBlock>("QualityLabel");
                if (lbl != null)
                {
                    lbl.Text = $"{qualityPercent}% — {desc}";
                    lbl.Foreground = Avalonia.Media.Brush.Parse(color);
                }
            });
        }
        catch (OperationCanceledException) { }
        catch
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (probeVersion != _probeVersion) return;
                var lbl = this.FindControl<TextBlock>("QualityLabel");
                if (lbl != null)
                {
                    lbl.Text = $"{qualityPercent}%";
                    lbl.Foreground = Avalonia.Media.Brush.Parse("#c5dcf2");
                }
            });
        }
    }

    /// <summary>
    /// Probes all videos for duration, file size, and bitrate.
    /// Caches results for quality and size estimation.
    /// </summary>
    private async Task ProbeAndUpdateSizeAsync(int qualityPercent, System.Threading.CancellationToken cancellationToken, int probeVersion)
    {
        double totalDurationSec = 0;
        double totalSourceSizeBytes = 0;
        double lowestBitrate = double.MaxValue;

        foreach (var path in VideoQueue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (probeVersion != _probeVersion) return;
            if (!System.IO.File.Exists(path)) continue;
            try
            {
                var fi = new FileInfo(path);
                totalSourceSizeBytes += fi.Length;

                var prober = new MediaProber(_ffprobePath, path);
                double dur = await prober.GetDurationAsync();
                cancellationToken.ThrowIfCancellationRequested();
                totalDurationSec += dur;

                var data = await prober.ProbeAsync();
                cancellationToken.ThrowIfCancellationRequested();
                double vbitrate = 0;
                var streams = data["streams"]?.AsArray();
                if (streams != null)
                {
                    foreach (var stream in streams)
                    {
                        if (stream?["codec_type"]?.ToString() == "video")
                        {
                            var brNode = stream["bit_rate"];
                            if (brNode != null && double.TryParse(brNode.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double br))
                                vbitrate = br / 1000.0;
                        }
                    }
                }
                if (vbitrate > 0 && vbitrate < lowestBitrate) lowestBitrate = vbitrate;
            }
            catch { }
        }

        if (lowestBitrate == double.MaxValue || lowestBitrate <= 0) lowestBitrate = 5000;
        if (probeVersion != _probeVersion) return;

        _cachedTotalDurationSec = totalDurationSec;
        _cachedTotalSourceSizeMB = totalSourceSizeBytes / (1024.0 * 1024.0);
        _cachedLowestBitrate = lowestBitrate;

        UpdateEstimatedSize();
    }

    /// <summary>
    /// Updates the estimated output size text based on quality, speed, and source files.
    /// Called on every video add/remove, quality change, speed change, or music add.
    /// </summary>
    private void UpdateEstimatedSize()
    {
        var sizeLabel = this.FindControl<TextBlock>("EstimatedSizeText");
        if (sizeLabel == null) return;

        if (VideoQueue.Count == 0 || _cachedTotalSourceSizeMB <= 0)
        {
            sizeLabel.Text = "";
            return;
        }

        var qs = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("QualitySlider");
        int qualityPercent = qs != null ? (qs.Value + 1) * 5 : 100;

        double estimatedMB = _cachedTotalSourceSizeMB * (qualityPercent / 100.0);

        if (_baseSpeed > 0.01 && _cachedTotalDurationSec > 0)
        {
            double outputDurationSec = _cachedTotalDurationSec / _baseSpeed;
            double durationRatio = outputDurationSec / _cachedTotalDurationSec;
            estimatedMB *= durationRatio;
        }

        if (_musicResult != null)
        {
            var musicPaths = _musicResult.MusicFilePaths.Count > 0
                ? _musicResult.MusicFilePaths
                : new List<string> { _musicResult.MusicFilePath };

            foreach (var musicPath in musicPaths)
            {
                if (string.IsNullOrWhiteSpace(musicPath) || !File.Exists(musicPath))
                    continue;

                try
                {
                    var musicFi = new FileInfo(musicPath);
                    estimatedMB += musicFi.Length / (1024.0 * 1024.0);
                }
                catch { }
            }
        }

        string sizeText = estimatedMB >= 1024
            ? $"Est. Output: ~{estimatedMB / 1024.0:F1} GB"
            : $"Est. Output: ~{estimatedMB:F0} MB";

        sizeLabel.Text = sizeText;
        var sizeLabel2 = this.FindControl<TextBlock>("EstimatedSizeText2");
        if (sizeLabel2 != null) sizeLabel2.Text = sizeText;
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

    private void WireUpVolumeSlider()
    {
        var volumeSlider = this.FindControl<Slider>("VolumeSlider");
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

            var speakerHitBox = this.FindControl<Border>("SpeakerHitBox");
            if (speakerHitBox != null) speakerHitBox.PointerPressed += (s, e) => ToggleMute();

            volumeSlider.PointerReleased += (s, e) =>
            {
                try { new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths).UpdatePropertiesSync(new System.Text.Json.Nodes.JsonObject { ["MainVolume"] = volumeSlider.Value }); } catch { }
            };
        }
    }

    private void ToggleMute()
    {
        var volumeSlider = this.FindControl<Slider>("VolumeSlider");
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
            _ = _videoHost.IpcClient.SetPropertyAsync("volume", masterVolumePercentage.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
        catch { }

        if (string.IsNullOrEmpty(startPath) || !System.IO.Directory.Exists(startPath))
            startPath = GetDownloadsPath();

        if (!string.IsNullOrEmpty(startPath) && Directory.Exists(startPath))
        {
            try { options.SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(startPath)); } catch { }
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
            catch { }
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
            var vl = this.FindControl<ListBox>("VideoList");
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

    private void StartVideoFingerprintWarmup(string path)
    {
        _ = GetOrStartVideoFingerprintAsync(path);
    }

    private Task<VideoFileFingerprint?> GetOrStartVideoFingerprintAsync(string path)
    {
        string normalizedPath = NormalizeVideoPath(path);
        if (!TryGetVideoFileSnapshot(normalizedPath, out long sizeBytes, out DateTime lastWriteUtc))
            return Task.FromResult<VideoFileFingerprint?>(null);

        lock (_videoFingerprintLock)
        {
            if (_videoFingerprintCache.TryGetValue(normalizedPath, out var cached) &&
                cached.SizeBytes == sizeBytes &&
                cached.LastWriteUtc == lastWriteUtc)
            {
                return Task.FromResult<VideoFileFingerprint?>(cached);
            }

            if (_videoFingerprintTasks.TryGetValue(normalizedPath, out var existingTask))
                return existingTask;

            var task = CreateAndCacheVideoFingerprintAsync(normalizedPath);
            _videoFingerprintTasks[normalizedPath] = task;
            return task;
        }
    }

    private async Task<VideoFileFingerprint?> CreateAndCacheVideoFingerprintAsync(string path)
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

                var fingerprint = new VideoFileFingerprint(path, sizeBytes, lastWriteUtc, sha256);
                lock (_videoFingerprintLock)
                {
                    _videoFingerprintCache[path] = fingerprint;
                }

                RuntimeLog.Info("VideoMerger", $"Cached fingerprint for {Path.GetFileName(path)}.");
                return fingerprint;
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
        catch { }

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
        catch
        {
            return path.Trim();
        }
    }

    private void LoadOutputDirectory()
    {
        try
        {
            var state = new StateTransferStore(_paths).LoadAsync().GetAwaiter().GetResult();
            if (state != null && state.TryGetPropertyValue("MergerOutputDirectory", out var node) && node != null)
            {
                string dir = node.ToString();
                if (Directory.Exists(dir))
                {
                    _outputDirectory = dir;
                    var btn = this.FindControl<MenuItem>("MenuOutputFolder");
                    if (btn != null) btn.Header = $"Output Folder: {System.IO.Path.GetFileName(dir)}";
                    UpdateOutputPathDisplay();
                    return;
                }
            }
        }
        catch { }
        _outputDirectory = GetDownloadsPath();
        UpdateOutputPathDisplay();
    }

    private static string GetDownloadsPath()
    {
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(downloads)) downloads = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return downloads;
    }

    private async void OnChooseOutputFolder()
    {
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var options = new FolderPickerOpenOptions { Title = "Choose Output Folder for Merged Videos", AllowMultiple = false };
        if (!string.IsNullOrEmpty(_outputDirectory))
        {
            try { options.SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(_outputDirectory)); } catch { }
        }

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
        if (result != null && result.Count > 0)
        {
            _outputDirectory = result[0].Path.LocalPath;
            try { await new StateTransferStore(_paths).UpdatePropertiesAsync(new System.Text.Json.Nodes.JsonObject { ["MergerOutputDirectory"] = _outputDirectory }); } catch { }
            var btn = this.FindControl<MenuItem>("MenuOutputFolder");
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

            bool allPortrait = true;
            bool allLandscape = true;
            foreach (var path in VideoQueue)
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

            var worker = new FortniteVideoSoftware.Core.Media.MergerWorker { InputFiles = new List<string>(VideoQueue), OutputDirectory = _outputDirectory, SpeedFactor = _baseSpeed, QualityPercent = qualityPercent, OutputRatio = targetRatio };
            _activeMergerWorker = worker;

            var volSlider = this.FindControl<Avalonia.Controls.Slider>("VolumeSlider");
            double currentMainVol = volSlider != null ? volSlider.Value / 100.0 : 1.0;

            if (_musicResult != null)
            {
                var musicPaths = (_musicResult.MusicFilePaths.Count > 0
                        ? _musicResult.MusicFilePaths
                        : new List<string> { _musicResult.MusicFilePath })
                    .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    .ToList();

                if (musicPaths.Count > 0)
                {
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
                        ["ducking_threshold"] = _musicResult.EnableDucking ? 0.15 : 1.0,
                        ["ducking_ratio"] = _musicResult.EnableDucking ? 2.5 : 1.0,
                        ["main_vol"] = currentMainVol,
                        ["music_vol"] = _musicResult.MusicVolume,
                        ["carving_enabled"] = _musicResult.EnableCarving,
                        ["timeline_start_sec"] = _musicResult.TimelineStartSeconds,
                        ["timeline_end_sec"] = _musicResult.TimelineEndSeconds,
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

            this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer")?.StartOverlay();

            worker.ProgressUpdate += percent => Avalonia.Threading.Dispatcher.UIThread.Post(() => 
            {
                mergeBtn.Content = $"MERGING... {percent}%";
                this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer")?.UpdatePhase(1, "Merging Videos...", percent);
            });

            worker.Finished += async (success, msg) =>
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer")?.StopOverlay();
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
                    else SetQueueStatus("Merge failed: " + msg, true);
                });
            };

            await worker.RunAsync(_mergeCts.Token);
        }
        catch (Exception ex)
        {
            this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer")?.StopOverlay();
            mergeBtn.IsEnabled = true;
            mergeBtn.Content = "MERGE VIDEOS";
            UpdateQueueState();
            SetQueueStatus("Merge error: " + ex.Message, true);
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
        var videoList = this.FindControl<ListBox>("VideoList");
        int selectedIndex = videoList?.SelectedIndex ?? -1;
        int count = VideoQueue.Count;

        var mergeBtn = this.FindControl<Button>("MergeButton");
        if (mergeBtn != null)
        {
            mergeBtn.IsEnabled = count >= 1;
            ToolTip.SetTip(mergeBtn, count >= 1 ? "Merge/Process all listed videos" : "Add at least one video to enable processing");
        }

        var addMusicBtn = this.FindControl<Button>("AddMusicButton");
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

    private void SetQueueStatus(string message, bool isError)
    {
        var status = this.FindControl<TextBlock>("QueueStatusText");
        if (status == null) return;
        status.Text = message;
        status.Foreground = isError ? Avalonia.Media.Brush.Parse("#fecaca") : Avalonia.Media.Brush.Parse("#94a3b8");
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
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount < 2) { try { BeginMoveDrag(e); } catch { } }
            };
        }
    }

    private void MergerKeyDownHandler(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Avalonia.Controls.TextBox or Avalonia.Controls.NumericUpDown) return;
        var fEl = Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (fEl is Avalonia.Controls.Border sb && sb.Name == "SpeakerHitBox" && e.Key == Avalonia.Input.Key.Space) return;

        if (e.Key == Avalonia.Input.Key.Space)
        {
            if (_videoHost?.IpcClient != null) _ = _videoHost.IpcClient.SetPropertyAsync("pause", _videoHost.IpcClient.IsPaused ? "no" : "yes");
            e.Handled = true;
        }
        else if (e.Key == Avalonia.Input.Key.Left) { _ = _videoHost?.IpcClient?.SendCommandAsync("seek", -5); e.Handled = true; }
        else if (e.Key == Avalonia.Input.Key.Right) { _ = _videoHost?.IpcClient?.SendCommandAsync("seek", 5); e.Handled = true; }
        else if (e.Key == Avalonia.Input.Key.Up) { MoveVideo(-1); e.Handled = true; }
        else if (e.Key == Avalonia.Input.Key.Down) { MoveVideo(1); e.Handled = true; }
    }

    private void InitializeComponent() { AvaloniaXamlLoader.Load(this); }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_videoHost?.IpcClient == null) return;

        var playIcon = this.FindControl<Avalonia.Controls.Shapes.Polygon>("PlayIcon");
        var pauseIcon = this.FindControl<StackPanel>("PauseIcon");
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
                var tickText = new TextBlock { Text = TimeSpan.FromSeconds(t).ToString(t >= 3600 ? "h\\:mm\\:ss" : "m\\:ss"), Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(180, 255, 255, 255)), FontSize = 9 };
                Avalonia.Controls.Canvas.SetLeft(tickText, Math.Max(0, Math.Min(Math.Max(0, canvasWidth - 36), tx + 2)));
                Avalonia.Controls.Canvas.SetTop(tickText, 0);
                scaleCanvas.Children.Add(tickText);
            }
        }
    }

    private async void InitializeMpv()
    {
        _videoHost = this.FindControl<MpvVideoView>("VideoHost");
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
                        var volSlider = this.FindControl<Slider>("VolumeSlider");
                        if (volSlider != null) volSlider.Value = volNode?.GetValue<double>() ?? 100.0;
                    }
                }
                catch { }
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
        string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "FortniteVideoSoftware.exe";
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath, "run-ui") { UseShellExecute = false });
        if (p != null)
        {
            Task.Run(() => { try { p.WaitForInputIdle(5000); Task.Delay(500).Wait(); } catch { } Environment.Exit(0); });
        }
        else Environment.Exit(0);
    }

    public void OnSuccessAction(string action)
    {
        if (action == "whatsapp") Process.Start(new ProcessStartInfo("cmd", "/c start whatsapp://send?text=CheckOutThisVideo") { CreateNoWindow = true });
        else if (action == "folder") Process.Start(new ProcessStartInfo("explorer.exe", _outputDirectory ?? ".") { CreateNoWindow = true });
        Close();
    }

    protected override async void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        if (_isSafeToClose) { base.OnClosing(e); return; }
        e.Cancel = true;
        try { await WindowBoundsHelper.SaveBoundsAsync(this, "VideoMergerBounds"); } catch { }
        this.Hide();
        try
        {
            if (_videoHost?.IpcClient != null)
            {
                var stopTask = _videoHost.IpcClient.SendCommandAsync("stop");
                var timeoutTask = Task.Delay(2000);
                await Task.WhenAny(stopTask, timeoutTask);
            }
        }
        catch { }
        finally { _isSafeToClose = true; this.Close(); }
    }

    protected override void OnClosed(EventArgs e) { base.OnClosed(e); }

    private Avalonia.Point? _videoDragStartPoint;
    private bool _isVideoDragging;

    private void VideoList_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(sender as Avalonia.Controls.Control);
        if (point.Properties.IsLeftButtonPressed) _videoDragStartPoint = point.Position;
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
                    var videoList = this.FindControl<ListBox>("VideoList");
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
            var videoList = this.FindControl<ListBox>("VideoList");
            if (videoList != null) foreach (var container in videoList.GetRealizedContainers().Cast<ListBoxItem>()) container.Opacity = 1.0;
        }
    }

    private int ComputeDropIndex(Avalonia.Input.DragEventArgs e)
    {
        var videoList = this.FindControl<ListBox>("VideoList");
        if (videoList == null || VideoQueue.Count == 0) return 0;
        var containers = videoList.GetRealizedContainers().Cast<ListBoxItem>().ToList();
        if (containers.Count == 0) return VideoQueue.Count;
        foreach (var container in containers)
        {
            if (container.DataContext is not string dc) continue;
            int idx = VideoQueue.IndexOf(dc);
            if (idx < 0) continue;
            var bounds = container.Bounds;
            var pos = e.GetPosition(videoList);
            if (pos.Y >= bounds.Top && pos.Y <= bounds.Bottom)
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
        var videoList = this.FindControl<ListBox>("VideoList");
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
        frame.Background = Avalonia.Media.SolidColorBrush.Parse(active ? "#243447" : "#1e293b");
        frame.BorderBrush = Avalonia.Media.SolidColorBrush.Parse(active ? "#38bdf8" : "#475569");
    }
}
