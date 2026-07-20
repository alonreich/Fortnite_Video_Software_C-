using Avalonia.Controls;

using Avalonia.Interactivity;

using Avalonia.Markup.Xaml;

using Avalonia.Input;

using Avalonia.Threading;

using FortniteVideoSoftware.Core.Infrastructure;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;


namespace FortniteVideoSoftware.App;


public class MusicTrackItem : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string DurationText { get; set; } = "";
    public string SizeText { get; set; } = "";
    public double DurationSec { get; set; } = 0.0;
    public long LastModifiedTicks { get; set; } = 0;
    
    private bool _isRecent;
    public bool IsRecent 
    { 
        get => _isRecent; 
        set 
        { 
            if (_isRecent != value) 
            { 
                _isRecent = value; 
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsRecent))); 
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(PinText))); 
            } 
        } 
    }
    public string PinText => IsRecent ? "RECENT" : "";
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public class MusicQueueItem
{
    public int Order { get; set; }
    public string OrderText => $"{Order}.";
    public string Name { get; set; } = string.Empty;
    public string DurationText { get; set; } = "";
}


public class MusicWizardResult
{
    public string MusicFilePath { get; set; } = string.Empty;
    public System.Collections.Generic.List<string> MusicFilePaths { get; set; } = new();
    public System.Collections.Generic.List<double> MusicDurationsSeconds { get; set; } = new();
    public double OffsetSeconds { get; set; } = 0.0;
    public double SongStartSeconds
    {
        get => OffsetSeconds;
        set => OffsetSeconds = value;
    }
    public double TimelineStartSeconds { get; set; } = 0.0;
    public double TimelineEndSeconds { get; set; } = 0.0;
    public bool EnableDucking { get; set; } = true;

    public bool EnableCarving { get; set; } = true;

    public double VideoVolume { get; set; } = 1.0;

    public double MusicVolume { get; set; } = 1.0;

    public double MusicDurationSeconds { get; set; } = 0.0;

    public bool LoopMusic { get; set; } = false;

}


public partial class MusicWizardWindow : Window

{

    public ObservableCollection<MusicTrackItem> AvailableTracks { get; } = new();

    public ObservableCollection<MusicQueueItem> AutoFillQueueItems { get; } = new();

    public MusicWizardResult? Result { get; private set; }


    private int _currentStep = 1;

    private readonly ApplicationPaths _paths = ApplicationPaths.CreateDefault();

    private bool _isSafeToClose = false;

    private string? _lastWaveformFile;

    private double _trackDuration = 100.0;

    private MusicTrackItem? _selectedTrack;

    private FortniteVideoSoftware.Core.Media.MpvIpcClient? _audioIpcClient;
    private bool _isPreviewPlaying = false;
    private double _previewCurrentOffset = 0.0;
    private DateTime? _previewStartTime = null;
    private DateTime? _phase3PreviewClockStartTime = null;
    private double _phase3PreviewClockStartOffsetSec = 0.0;
    private double _songStartSeconds = 0.0;
    private Avalonia.Threading.DispatcherTimer? _playheadTimer;
    private Avalonia.Controls.Shapes.Line? _waveformOffsetLine;
    private Avalonia.Controls.Shapes.Line? _waveformPlayheadLine;
    private Avalonia.Controls.Shapes.Line? _timelinePlayheadLine;

    private string _videoPath = "";
    private double _trimStartMs = 0;
    private double _trimEndMs = 0;
    private double _actualVideoDurationMs = 0;
    private string? _lastLoadedTrackPath;
    private string? _lastConfiguredTrackPath;
    private readonly System.Collections.Generic.List<string> _pendingAutoFillMusicPaths = new();
    private readonly System.Collections.Generic.List<MusicTrackItem> _allTracks = new();
    private readonly System.Collections.Generic.HashSet<string> _recentMusicPaths = new(StringComparer.OrdinalIgnoreCase);
    private string _musicSearchText = string.Empty;
    private string _musicSortMode = "Name";
    private bool _autoFillUseVisibleTracks = true;
    private CancellationTokenSource? _phase3LoadCts;
    private int _phase3LoadVersion;
    private bool _phase3Ready;
    private double _phase3VideoDurationSec = 60.0;
    private System.Collections.Generic.List<string> _lastPhase3ThumbFiles = new();
    private string? _lastPhase3WaveFile;
    private System.Collections.Generic.List<string>? _mergerVideos = null;
    private bool _isMergerMode = false;
    private double _phase3BaseSpeed = 1.0;
    private readonly System.Collections.Generic.List<FortniteVideoSoftware.Core.Media.SpeedSegment> _phase3SpeedSegments = new();
    private int _waveformRenderVersion = 0;
    private readonly System.Collections.Generic.List<double> _phase3ClipDurationsSec = new();
    private CancellationTokenSource? _audioAnalysisCts;
    private CancellationTokenSource? _musicScanCts;
    private readonly SemaphoreSlim _trackProbeGate = new(4, 4);
    private int _musicScanVersion;
    private int _phase3MusicSyncInFlight;
    private string? _phase3PreviewMusicPath;
    private double _phase3PreviewMusicSegmentStartSec = double.NaN;

    private FortniteVideoSoftware.App.MpvVideoView? WizardVideoHost => this.FindControl<Avalonia.Controls.Border>("VideoHostBorder")?.Child as FortniteVideoSoftware.App.MpvVideoView;

    private sealed class AudioEnergyAnalysis
    {
        public double BucketSeconds { get; init; }
        public double DurationSeconds { get; init; }
        public double[] Energy { get; init; } = Array.Empty<double>();
        public System.Collections.Generic.List<double> PeakTimesSeconds { get; init; } = new();
    }

    private sealed class Phase3MusicPreviewSegment
    {
        public string Path { get; init; } = string.Empty;
        public double TimelineStartSec { get; init; }
        public double TimelineEndSec { get; init; }
        public double FileStartSec { get; init; }
    }


    public MusicWizardWindow()

    {
        InitializeComponent();
        FortniteVideoSoftware.App.WindowBoundsHelper.Track(this, "MusicWizardBounds");
        _playheadTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _playheadTimer.Tick += PlayheadTimer_Tick;
    }

    private void PlayheadTimer_Tick(object? sender, EventArgs e)
    {
        if (_isPreviewPlaying)
        {
            SyncPhase3VideoPreviewClock();
            QueuePhase3MusicPreviewSync();
            EnforcePhase3PreviewEnd();
            UpdatePlayhead();
        }
    }


    public MusicWizardWindow(System.Collections.Generic.List<string> mergerVideos, double totalDurationSec) : this()
    {
        _mergerVideos = mergerVideos;
        _isMergerMode = true;
        _videoPath = mergerVideos.FirstOrDefault() ?? "";
        _trimStartMs = 0;
        _trimEndMs = totalDurationSec * 1000.0;
        _playheadTimer?.Start();
        SharedInit();
    }

    public MusicWizardWindow(System.Collections.Generic.List<string> mergerVideos, double totalDurationSec, double baseSpeed) : this()
    {
        _mergerVideos = mergerVideos;
        _isMergerMode = true;
        _videoPath = mergerVideos.FirstOrDefault() ?? "";
        _trimStartMs = 0;
        _trimEndMs = totalDurationSec * 1000.0;
        ConfigurePhase3Timeline(baseSpeed, null);
        _playheadTimer?.Start();
        SharedInit();
    }

    public MusicWizardWindow(
        string videoPath,
        double trimStartMs,
        double trimEndMs,
        double baseSpeed = 1.0,
        System.Collections.Generic.IReadOnlyList<FortniteVideoSoftware.Core.Media.SpeedSegment>? speedSegments = null) : this()
    {
        _videoPath = videoPath;
        _trimStartMs = trimStartMs;
        _trimEndMs = trimEndMs;
        ConfigurePhase3Timeline(baseSpeed, speedSegments);
        _playheadTimer?.Start();
        SharedInit();
    }

    private void ConfigurePhase3Timeline(
        double baseSpeed,
        System.Collections.Generic.IReadOnlyList<FortniteVideoSoftware.Core.Media.SpeedSegment>? speedSegments)
    {
        _phase3BaseSpeed = baseSpeed > 0.001 ? baseSpeed : 1.0;
        _phase3SpeedSegments.Clear();
        if (speedSegments != null)
            _phase3SpeedSegments.AddRange(speedSegments);
    }

    private void OnGlobalMasterVolumeChanged(int volume)
    {
        if (WizardVideoHost?.IpcClient != null)
            _ = WizardVideoHost.IpcClient.SetPropertyDoubleAsync("volume", GetPreviewVideoVolume(volume));
        if (_audioIpcClient != null)
            _ = _audioIpcClient.SetPropertyDoubleAsync("volume", GetPreviewMusicVolume(volume));
    }

    private void SharedInit()
    {
        if (FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.Defaults.RememberMusicVolumes)
        {
            try
            {
                var state = new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths).LoadSync();
                var vSlider = this.FindControl<Avalonia.Controls.Slider>("VideoVolSlider");
                var mSlider = this.FindControl<Avalonia.Controls.Slider>("MusicVolSlider");

                if (vSlider != null && state.TryGetPropertyValue("WizardVideoVolume", out var vvNode) && vvNode != null)
                    vSlider.Value = vvNode.GetValue<double>();

                if (mSlider != null && state.TryGetPropertyValue("WizardMusicVolume", out var mvNode) && mvNode != null)
                    mSlider.Value = mvNode.GetValue<double>();
            }
            catch { }
        }

        FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged += OnGlobalMasterVolumeChanged;

        this.Closing += (s, e) => {
            WindowBoundsHelper.SaveBoundsSync(this, "MusicWizardBounds");
        };

        LoadRecentMusicPins();

        var listbox = this.FindControl<ListBox>("MusicListBox");

        if (listbox != null)

        {

            listbox.ItemsSource = AvailableTracks;

            listbox.SelectionChanged += (s, e) => OnTrackSelected(listbox.SelectedItem as MusicTrackItem);

            listbox.DoubleTapped += (s, e) =>

            {

                if (listbox.SelectedItem != null && _currentStep == 1)

                {

                    RuntimeLog.Info("UI", "User double-clicked a track to proceed in Music Wizard.");

                    OnNextClicked(listbox, new RoutedEventArgs());

                }

            };

            LoadMusicDirectory();

        }

        var queueList = this.FindControl<ListBox>("AutoFillQueueList");
        if (queueList != null)
        {
            queueList.ItemsSource = AutoFillQueueItems;
        }

        var searchBox = this.FindControl<TextBox>("MusicSearchBox");
        if (searchBox != null)
        {
            searchBox.TextChanged += (s, e) =>
            {
                _musicSearchText = searchBox.Text ?? string.Empty;
                ApplyTrackFilterAndSort();
            };
            searchBox.KeyDown += OnMusicSearchKeyDown;
            Dispatcher.UIThread.Post(() => searchBox.Focus(), DispatcherPriority.Input);
        }

        var clearSearchBtn = this.FindControl<Button>("ClearSearchBtn");
        if (clearSearchBtn != null)
        {
            clearSearchBtn.Click += (s, e) =>
            {
                if (searchBox != null)
                {
                    searchBox.Text = string.Empty;
                    searchBox.Focus();
                }
            };
        }

        var sortCombo = this.FindControl<ComboBox>("MusicSortComboBox");
        if (sortCombo != null)
        {
            sortCombo.SelectionChanged += (s, e) =>
            {
                if (sortCombo.SelectedItem is ComboBoxItem item && item.Content != null)
                {
                    _musicSortMode = item.Content.ToString() ?? "Name";
                    ApplyTrackFilterAndSort();
                }
            };
        }


        AddHandler(DragDrop.DropEvent, OnFileDrop);

        var loopCheck = this.FindControl<CheckBox>("LoopMusicCheckBox");
        if (loopCheck != null)
        {
            loopCheck.IsCheckedChanged += (s, e) =>
            {
                UpdateCoverageBar();
                UpdateAutoFillQueuePreview();
                UpdateProblemFlags();
            };
        }

        var duckingCheck = this.FindControl<CheckBox>("DuckingCheckBox");
        if (duckingCheck != null)
        {
            duckingCheck.IsCheckedChanged += (s, e) =>
            {
                UpdateDuckingCompareButton();
                ApplyPreviewMusicVolume();
                UpdateProblemFlags();
            };
        }

        var visibleOnlyCheck = this.FindControl<CheckBox>("AutoFillVisibleOnlyCheckBox");
        if (visibleOnlyCheck != null)
        {
            visibleOnlyCheck.IsCheckedChanged += (s, e) =>
            {
                _autoFillUseVisibleTracks = visibleOnlyCheck.IsChecked ?? true;
            };
        }

        this.FindControl<Button>("QueueMoveUpBtn")!.Click += (s, e) => MoveQueuedTrack(-1);
        this.FindControl<Button>("QueueMoveDownBtn")!.Click += (s, e) => MoveQueuedTrack(1);
        this.FindControl<Button>("QueueRemoveBtn")!.Click += (s, e) => RemoveSelectedQueuedTrack();

        var autoFillBtn = this.FindControl<Button>("AutoFillSongsBtn");
        if (autoFillBtn != null)
        {
            autoFillBtn.Click += (s, e) =>
            {
                if (_selectedTrack != null)
                {
                    BuildAutoFillQueue();
                }
            };
        }

        var beatSnapBtn = this.FindControl<Button>("BeatSnapBtn");
        if (beatSnapBtn != null)
            beatSnapBtn.Click += async (s, e) => await SnapSongStartToBeatAsync(beatSnapBtn);

        var smartFitBtn = this.FindControl<Button>("SmartFitBtn");
        if (smartFitBtn != null)
            smartFitBtn.Click += async (s, e) => await ApplySmartFitAsync(smartFitBtn);

        var duckingCompareBtn = this.FindControl<Button>("DuckingCompareBtn");
        if (duckingCompareBtn != null)
        {
            duckingCompareBtn.Click += (s, e) =>
            {
                var check = this.FindControl<CheckBox>("DuckingCheckBox");
                if (check != null)
                    check.IsChecked = !(check.IsChecked ?? true);
            };
        }

        var changeFolderBtn = this.FindControl<Button>("ChangeFolderBtn");

        if (changeFolderBtn != null)

        {

            changeFolderBtn.Click += async (s, e) =>

            {

                var musicPath = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

                try
                {
                    if (File.Exists(_paths.SessionStateFile))
                    {
                        var state = FortniteVideoSoftware.Core.Infrastructure.AtomicJsonFile.ReadObject(_paths.SessionStateFile);
                        if (state != null && state.TryGetPropertyValue("CustomMusicDirectory", out var node) && node != null)
                        {
                            string customPath = node.ToString();
                            if (Directory.Exists(customPath))
                            {
                                musicPath = customPath;
                            }
                        }
                    }
                }
                catch { }

                Avalonia.Platform.Storage.IStorageFolder? musicFolder = null;
                try
                {
                    var uri = new Uri(musicPath);
                    musicFolder = await this.StorageProvider.TryGetFolderFromPathAsync(uri);
                }
                catch { }

                var result = await this.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions

                {

                    Title = "Select Music Folder",

                    SuggestedStartLocation = musicFolder,

                    AllowMultiple = false

                });


                if (result != null && result.Count > 0)

                {

                    string selectedFolderPath = result[0].Path.LocalPath;


                    try

                    {

                        await new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths)
                            .UpdatePropertiesAsync(new System.Text.Json.Nodes.JsonObject
                            {
                                ["CustomMusicDirectory"] = selectedFolderPath
                            });

                    }

                    catch { }


                    await ScanDirectoryForMusicAsync(selectedFolderPath);

                }

            };

        }


        var timelineMarkersCanvas = this.FindControl<Canvas>("TimelineMarkersCanvas");

        if (timelineMarkersCanvas != null)

        {

            timelineMarkersCanvas.SizeChanged += (s, e) => { DrawTimelineScale(); UpdatePlayhead(); };

            timelineMarkersCanvas.PointerPressed += (s, e) =>

            {

                var pt = e.GetCurrentPoint(timelineMarkersCanvas);

                if (pt.Properties.IsLeftButtonPressed)

                {

                    SetOffsetFromPointer(pt.Position.X, timelineMarkersCanvas.Bounds.Width);

                }

            };

            timelineMarkersCanvas.KeyDown += (s, e) => HandleSongOffsetKeyDown(e);

        }


        var canvas = this.FindControl<Canvas>("WaveformCanvas");

        if (canvas != null)

        {

            canvas.SizeChanged += (s, e) => { UpdatePlayhead(); };

            canvas.PointerPressed += (s, e) =>

            {

                var pt = e.GetCurrentPoint(canvas);

                if (pt.Properties.IsLeftButtonPressed)

                {

                    SetOffsetFromPointer(pt.Position.X, canvas.Bounds.Width);

                }

            };

            canvas.KeyDown += (s, e) => HandleSongOffsetKeyDown(e);

        }


        var phase3SeekCanvas = this.FindControl<Canvas>("Phase3SeekCanvas");
        if (phase3SeekCanvas != null)
        {
            phase3SeekCanvas.PointerPressed += (s, e) =>

            {

                var pt = e.GetCurrentPoint(phase3SeekCanvas);

                if (pt.Properties.IsLeftButtonPressed && phase3SeekCanvas.Bounds.Width > 0)

                {

                    double fraction = pt.Position.X / phase3SeekCanvas.Bounds.Width;

                    fraction = Math.Clamp(fraction, 0.0, 1.0);


                    double duration = GetPhase3VideoDurationSeconds();
                    double videoRelativeSec = duration * fraction;


                    bool wasPlaying = _isPreviewPlaying;
                    StopPreview();

                    _previewCurrentOffset = _songStartSeconds + videoRelativeSec;
                    SeekPhase3VideoHost(videoRelativeSec, forcePause: !wasPlaying);

                    if (wasPlaying) StartPreviewInternal(_previewCurrentOffset);
                    else UpdatePlayhead();
                }

            };

            phase3SeekCanvas.KeyDown += (s, e) => HandlePhase3SeekKeyDown(e);
        }

        var phase3WaveformClip = this.FindControl<Canvas>("Phase3WaveformClip");
        if (phase3WaveformClip != null)
        {
            phase3WaveformClip.SizeChanged += (s, e) => UpdatePhase3WaveformLaneWidth();
        }


        var videoVolSlider = this.FindControl<Slider>("VideoVolSlider");

        if (videoVolSlider != null)

        {

            videoVolSlider.PropertyChanged += (s, e) =>

            {

                if (e.Property == Slider.ValueProperty)

                {

                    var lbl = this.FindControl<TextBlock>("VideoVolLabel");

                    if (lbl != null) lbl.Text = $"Video Volume: {videoVolSlider.Value:0}%";


                    if (_currentStep == 3)

                    {

                        var wizardVideoHost = WizardVideoHost;

                        if (wizardVideoHost?.IpcClient != null)

                            _ = wizardVideoHost.IpcClient.SetPropertyDoubleAsync("volume", GetPreviewVideoVolume());

                        SaveWizardVolumes();
                        UpdateProblemFlags();

                    }

                }

            };

        }


        var musicVolSlider = this.FindControl<Slider>("MusicVolSlider");

        if (musicVolSlider != null)

        {

            musicVolSlider.PropertyChanged += (s, e) =>

            {

                if (e.Property == Slider.ValueProperty)

                {

                    var lbl = this.FindControl<TextBlock>("MusicVolLabel");

                    if (lbl != null) lbl.Text = $"Music Volume: {musicVolSlider.Value:0}%";


                    if (_audioIpcClient != null)

                    {

                        ApplyPreviewMusicVolume();
                        
                        SaveWizardVolumes();
                        UpdateProblemFlags();

                    }

                }

            };

        }


        var playBtn = this.FindControl<Button>("PlayBtn");

        if (playBtn != null)

        {

            playBtn.Click += (s, e) => TogglePreview();

        }


        var skipBackBtn = this.FindControl<Button>("SkipBackBtn");

        if (skipBackBtn != null) skipBackBtn.Click += (s, e) => SkipPreview(-30);


        var skipForwardBtn = this.FindControl<Button>("SkipForwardBtn");

        if (skipForwardBtn != null) skipForwardBtn.Click += (s, e) => SkipPreview(30);


        var nextBtn = this.FindControl<Button>("NextBtn");

        if (nextBtn != null) nextBtn.Click += (s, e) =>

        {

            RuntimeLog.Info("UI", "User clicked Next in Music Wizard.");

            OnNextClicked(s, e);

        };


        var backBtn = this.FindControl<Button>("BackBtn");

        if (backBtn != null) backBtn.Click += (s, e) =>

        {

            RuntimeLog.Info("UI", "User clicked Back in Music Wizard.");

            OnBackClicked(s, e);

        };


        var cancelBtn = this.FindControl<Button>("CancelBtn");

        if (cancelBtn != null) cancelBtn.Click += (s, e) =>

        {

            RuntimeLog.Info("UI", "User clicked Cancel in Music Wizard.");

            StopPreview();

            Close();

        };


        UpdateNextButtonState();
        UpdatePreviewControlsState();
        UpdateDuckingCompareButton();
        UpdateProblemFlags();
        AttachTitleBarDrag();
    }
    private void InitializeComponent()

    {

        AvaloniaXamlLoader.Load(this);

    }


    private void UpdateStepProgress()

    {

        var dots = new[] {

            (this.FindControl<Avalonia.Controls.Border>("Step1Dot"),

             this.FindControl<TextBlock>("Step1Icon"),

             this.FindControl<TextBlock>("Step1Label")),

            (this.FindControl<Avalonia.Controls.Border>("Step2Dot"),

             this.FindControl<TextBlock>("Step2Icon"),

             this.FindControl<TextBlock>("Step2Label")),

            (this.FindControl<Avalonia.Controls.Border>("Step3Dot"),

             this.FindControl<TextBlock>("Step3Icon"),

             this.FindControl<TextBlock>("Step3Label")),

        };


        for (int i = 0; i < 3; i++)

        {

            if (dots[i].Item1 == null || dots[i].Item2 == null || dots[i].Item3 == null) continue;

            if (i < _currentStep - 1)

            {


                dots[i].Item1!.Background = Avalonia.Media.Brush.Parse("#22c55e");

                dots[i].Item2!.Text = "✓";

                dots[i].Item2!.Foreground = Avalonia.Media.Brushes.White;

                dots[i].Item3!.Foreground = Avalonia.Media.Brush.Parse("#94a3b8");

            }

            else if (i == _currentStep - 1)

            {


                dots[i].Item1!.Background = Avalonia.Media.Brush.Parse("#3b82f6");

                dots[i].Item2!.Text = (i + 1).ToString();

                dots[i].Item2!.Foreground = Avalonia.Media.Brushes.White;

                dots[i].Item3!.Foreground = Avalonia.Media.Brush.Parse("#60a5fa");

                dots[i].Item3!.FontWeight = Avalonia.Media.FontWeight.Bold;

            }

            else

            {


                dots[i].Item1!.Background = Avalonia.Media.Brush.Parse("#334155");

                dots[i].Item2!.Text = (i + 1).ToString();

                dots[i].Item2!.Foreground = Avalonia.Media.Brush.Parse("#94a3b8");

                dots[i].Item3!.Foreground = Avalonia.Media.Brush.Parse("#94a3b8");

                dots[i].Item3!.FontWeight = Avalonia.Media.FontWeight.Normal;

            }

        }

    }


    private void UpdateStepVisibility()
    {
        this.FindControl<Grid>("Step1Panel")!.IsVisible = _currentStep == 1;
        this.FindControl<Control>("Step2Panel")!.IsVisible = _currentStep == 2;
        this.FindControl<Grid>("Step3Panel")!.IsVisible = _currentStep == 3;

        var helperPanel = this.FindControl<Avalonia.Controls.StackPanel>("MultiSongHelperPanel");
        if (helperPanel != null)
        {
            helperPanel.IsVisible = _currentStep == 2 && _isMergerMode;
        }

        var backBtn = this.FindControl<Button>("BackBtn");

        if (backBtn != null) backBtn.IsEnabled = _currentStep > 1;


        var nextBtn = this.FindControl<Button>("NextBtn");

        if (nextBtn != null)

        {

            nextBtn.Content = _currentStep == 3 ? "APPLY" : "NEXT";

        }


        // Window sizing preserved natively; bounds tracked by WindowBoundsHelper.

        UpdateFinalPlacementSummary();
        UpdateProblemFlags();
        UpdateDuckingCompareButton();
        UpdateStepProgress();
        UpdatePreviewControlsState();
        
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (this.Content is Avalonia.Controls.Control contentControl)
            {
                contentControl.InvalidateMeasure();
                contentControl.InvalidateArrange();
            }
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }


    private void OnTrackSelected(MusicTrackItem? track)
    {
        _selectedTrack = track;
        System.Threading.Interlocked.Increment(ref _waveformRenderVersion);
        ResetAutoFillQueueState();
        UpdateNextButtonState();
        UpdateFinalPlacementSummary();
        UpdatePreviewControlsState();
        UpdateCoverageBar();
        UpdateProblemFlags();
        SetSmartFitStatus("");
    }

    private void OnMusicSearchKeyDown(object? sender, KeyEventArgs e)
    {
        var listbox = this.FindControl<ListBox>("MusicListBox");
        if (listbox == null) return;

        if (e.Key == Key.Escape)
        {
            if (sender is TextBox searchBox)
                searchBox.Text = string.Empty;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down || e.Key == Key.Up)
        {
            if (AvailableTracks.Count == 0) return;
            int currentIndex = listbox.SelectedIndex;
            int nextIndex = e.Key == Key.Down
                ? Math.Min(AvailableTracks.Count - 1, currentIndex + 1)
                : Math.Max(0, currentIndex <= 0 ? 0 : currentIndex - 1);
            listbox.SelectedIndex = nextIndex;
            if (listbox.SelectedItem != null)
                listbox.ScrollIntoView(listbox.SelectedItem);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (listbox.SelectedItem == null && AvailableTracks.Count > 0)
                listbox.SelectedIndex = 0;
            if (listbox.SelectedItem != null && _currentStep == 1)
                OnNextClicked(listbox, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void ApplyTrackFilterAndSort()
    {
        string selectedPath = _selectedTrack?.FilePath ?? string.Empty;
        var visible = SortTracks(_allTracks.Where(t => TrackMatchesSearch(t, _musicSearchText))).ToList();

        AvailableTracks.Clear();
        foreach (var track in visible)
            AvailableTracks.Add(track);

        var listbox = this.FindControl<ListBox>("MusicListBox");
        if (listbox != null)
        {
            var selectedVisibleTrack = visible.FirstOrDefault(t =>
                string.Equals(t.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (selectedVisibleTrack != null)
            {
                listbox.SelectedItem = selectedVisibleTrack;
            }
            else if (!string.IsNullOrEmpty(selectedPath))
            {
                listbox.SelectedItem = null;
                OnTrackSelected(null);
            }
        }

        UpdateMusicEmptyState();
        UpdateMusicResultCount();
    }

    private System.Collections.Generic.IEnumerable<MusicTrackItem> SortTracks(System.Collections.Generic.IEnumerable<MusicTrackItem> tracks)
    {
        return _musicSortMode switch
        {
            "Newest" => tracks
                .OrderByDescending(t => t.IsRecent)
                .ThenByDescending(t => t.LastModifiedTicks)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
            "Shortest" => tracks
                .OrderByDescending(t => t.IsRecent)
                .ThenBy(t => t.DurationSec <= 0 ? double.MaxValue : t.DurationSec)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
            "Longest" => tracks
                .OrderByDescending(t => t.IsRecent)
                .ThenByDescending(t => t.DurationSec)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
            _ => tracks
                .OrderByDescending(t => t.IsRecent)
                .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static bool TrackMatchesSearch(MusicTrackItem track, string rawQuery)
    {
        string query = NormalizeSearchQuery(rawQuery);
        if (query.Length == 0)
            return true;

        return ContainsIgnoreCase(track.Title, query)
            || ContainsIgnoreCase(Path.GetFileNameWithoutExtension(track.Name), query)
            || ContainsIgnoreCase(track.Name, query)
            || ContainsIgnoreCase(track.Artist, query)
            || ContainsIgnoreCase(track.Album, query);
    }

    private static string NormalizeSearchQuery(string query)
    {
        return (query ?? string.Empty).Trim().Replace("*", string.Empty, StringComparison.Ordinal);
    }

    private static bool ContainsIgnoreCase(string source, string query)
    {
        return !string.IsNullOrEmpty(source) &&
            source.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateMusicResultCount()
    {
        var countText = this.FindControl<TextBlock>("MusicResultCountText");
        if (countText == null) return;

        if (_allTracks.Count == 0)
        {
            countText.Text = "0 songs";
        }
        else if (AvailableTracks.Count == _allTracks.Count)
        {
            countText.Text = $"{_allTracks.Count} songs";
        }
        else
        {
            countText.Text = $"{AvailableTracks.Count} of {_allTracks.Count} songs";
        }
    }

    private System.Collections.Generic.List<MusicTrackItem> GetAutoFillSourceTracks()
    {
        var source = _autoFillUseVisibleTracks ? AvailableTracks : SortTracks(_allTracks);
        return source
            .Where(t => !string.IsNullOrWhiteSpace(t.FilePath) && File.Exists(t.FilePath))
            .ToList();
    }

    private void SetSmartFitStatus(string message, bool isWarning = false)
    {
        var status = this.FindControl<TextBlock>("SmartFitStatusText");
        if (status == null) return;

        status.Text = message;
        status.Foreground = isWarning
            ? Avalonia.Media.Brushes.Orange
            : Avalonia.Media.Brushes.LightGreen;
    }

    private void CancelAudioAnalysis()
    {
        try { _audioAnalysisCts?.Cancel(); } catch { }
        try { _audioAnalysisCts?.Dispose(); } catch { }
        _audioAnalysisCts = null;
    }

    private void CancelMusicScan()
    {
        Interlocked.Increment(ref _musicScanVersion);
        try { _musicScanCts?.Cancel(); } catch { }
        try { _musicScanCts?.Dispose(); } catch { }
        _musicScanCts = null;
    }

    private async Task<AudioEnergyAnalysis?> AnalyzeAudioEnergyAsync(string audioPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            return null;

        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ResolveFfmpegPath(),
                Arguments = $"-nostdin -hide_banner -loglevel error -i \"{audioPath}\" -vn -ac 1 -ar 1000 -f s16le pipe:1",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            process = Process.Start(psi);
            if (process == null) return null;
            ChildProcessTracker.AddProcess(process);

            using var audioBytes = new MemoryStream();
            Task copyTask = process.StandardOutput.BaseStream.CopyToAsync(audioBytes, cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await copyTask;
            _ = await errorTask;

            if (process.ExitCode != 0)
                return null;

            byte[] data = audioBytes.ToArray();
            const int sampleRate = 1000;
            const double bucketSeconds = 0.05;
            int sampleCount = data.Length / 2;
            if (sampleCount <= 0)
                return null;

            int bucketSize = Math.Max(1, (int)Math.Round(sampleRate * bucketSeconds));
            int bucketCount = Math.Max(1, (int)Math.Ceiling(sampleCount / (double)bucketSize));
            var energy = new double[bucketCount];

            for (int bucket = 0; bucket < bucketCount; bucket++)
            {
                int start = bucket * bucketSize;
                int end = Math.Min(sampleCount, start + bucketSize);
                if (end <= start) continue;

                double sum = 0;
                for (int sampleIndex = start; sampleIndex < end; sampleIndex++)
                {
                    int byteIndex = sampleIndex * 2;
                    short sample = BitConverter.ToInt16(data, byteIndex);
                    sum += Math.Abs((int)sample) / 32768.0;
                }

                energy[bucket] = sum / (end - start);
            }

            double maxEnergy = energy.Length > 0 ? energy.Max() : 0.0;
            if (maxEnergy > 0.000001)
            {
                for (int i = 0; i < energy.Length; i++)
                    energy[i] /= maxEnergy;
            }

            double mean = energy.Length > 0 ? energy.Average() : 0.0;
            double variance = energy.Length > 0
                ? energy.Select(v => (v - mean) * (v - mean)).Average()
                : 0.0;
            double std = Math.Sqrt(variance);
            double threshold = Math.Max(mean + std * 0.65, 0.28);
            int minPeakSpacingBuckets = Math.Max(1, (int)Math.Round(0.28 / bucketSeconds));

            var peakIndexes = new System.Collections.Generic.List<int>();
            for (int i = 2; i < energy.Length - 2; i++)
            {
                if (energy[i] < threshold) continue;
                if (energy[i] < energy[i - 1] || energy[i] < energy[i + 1]) continue;

                if (peakIndexes.Count > 0 && i - peakIndexes[^1] < minPeakSpacingBuckets)
                {
                    if (energy[i] > energy[peakIndexes[^1]])
                        peakIndexes[^1] = i;
                }
                else
                {
                    peakIndexes.Add(i);
                }
            }

            return new AudioEnergyAnalysis
            {
                BucketSeconds = bucketSeconds,
                DurationSeconds = sampleCount / (double)sampleRate,
                Energy = energy,
                PeakTimesSeconds = peakIndexes.Select(i => i * bucketSeconds).ToList()
            };
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (process != null && !process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Audio energy analysis failed: {ex.Message}");
            return null;
        }
        finally
        {
            try { process?.Dispose(); } catch { }
        }
    }

    private static double? FindNearestPeakTime(AudioEnergyAnalysis analysis, double targetSeconds, double radiusSeconds)
    {
        double? best = null;
        double bestDistance = double.MaxValue;

        foreach (double peak in analysis.PeakTimesSeconds)
        {
            double distance = Math.Abs(peak - targetSeconds);
            if (distance <= radiusSeconds && distance < bestDistance)
            {
                best = peak;
                bestDistance = distance;
            }
        }

        return best;
    }

    private double FindSmartFitStart(AudioEnergyAnalysis analysis, double videoDurationSeconds)
    {
        double trackDuration = _trackDuration > 0 ? _trackDuration : analysis.DurationSeconds;
        if (trackDuration <= 0 || analysis.Energy.Length == 0)
            return 0.0;

        double usableWindowSeconds = Math.Min(videoDurationSeconds, trackDuration);
        double maxStartSeconds = Math.Max(0.0, trackDuration - usableWindowSeconds);
        if (maxStartSeconds <= 0.01)
            return 0.0;

        double bucketSeconds = analysis.BucketSeconds;
        int windowBuckets = Math.Max(1, Math.Min(analysis.Energy.Length, (int)Math.Round(usableWindowSeconds / bucketSeconds)));
        int maxStartBucket = Math.Min(analysis.Energy.Length - 1, (int)Math.Floor(maxStartSeconds / bucketSeconds));
        int stepBuckets = Math.Max(1, (int)Math.Round(0.10 / bucketSeconds));
        int earlyBuckets = Math.Max(1, (int)Math.Round(Math.Min(12.0, Math.Max(1.0, usableWindowSeconds * 0.25)) / bucketSeconds));

        var prefix = new double[analysis.Energy.Length + 1];
        for (int i = 0; i < analysis.Energy.Length; i++)
            prefix[i + 1] = prefix[i] + analysis.Energy[i];

        int bestBucket = 0;
        double bestScore = double.NegativeInfinity;

        for (int start = 0; start <= maxStartBucket; start += stepBuckets)
        {
            int end = Math.Min(analysis.Energy.Length, start + windowBuckets);
            if (end <= start) continue;

            int earlyEnd = Math.Min(end, start + earlyBuckets);
            double fullAverage = (prefix[end] - prefix[start]) / (end - start);
            double earlyAverage = (prefix[earlyEnd] - prefix[start]) / Math.Max(1, earlyEnd - start);
            double score = fullAverage * 0.65 + earlyAverage * 0.35;

            if (start < (int)Math.Round(1.0 / bucketSeconds) && earlyAverage < 0.04)
                score -= 0.05;

            if (score > bestScore)
            {
                bestScore = score;
                bestBucket = start;
            }
        }

        double startSeconds = bestBucket * bucketSeconds;
        double? snapped = FindNearestPeakTime(analysis, startSeconds, 1.0);
        return Math.Clamp(snapped ?? startSeconds, 0.0, maxStartSeconds);
    }

    private void ApplySongStartSeconds(double startSeconds, string statusMessage)
    {
        bool wasPlaying = _isPreviewPlaying;
        if (wasPlaying) StopPreview();

        _songStartSeconds = Math.Clamp(startSeconds, 0, Math.Max(0, _trackDuration - 0.01));
        ResetAutoFillQueueState();
        _previewCurrentOffset = _songStartSeconds;

        var lbl = this.FindControl<TextBlock>("OffsetLabel");
        if (lbl != null) lbl.Text = $"Song begins at {FormatSeconds(_songStartSeconds)}";

        DrawTimelineScale();
        DrawPhase3TimelineScale();
        UpdateFinalPlacementSummary();
        UpdateCoverageBar();
        UpdateProblemFlags();
        UpdatePlayhead();
        SetSmartFitStatus(statusMessage);

        if (wasPlaying)
            StartPreviewInternal(_previewCurrentOffset);
    }

    private async Task SnapSongStartToBeatAsync(Button button)
    {
        if (_selectedTrack == null || !File.Exists(_selectedTrack.FilePath))
        {
            ShowToast("Select a music track first.");
            return;
        }

        CancelAudioAnalysis();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _audioAnalysisCts = cts;
        button.IsEnabled = false;
        button.Opacity = 0.5;
        SetSmartFitStatus("Finding nearby beat...");

        try
        {
            var analysis = await AnalyzeAudioEnergyAsync(_selectedTrack.FilePath, cts.Token);
            if (analysis == null || analysis.PeakTimesSeconds.Count == 0)
            {
                SetSmartFitStatus("No clear beat found.", isWarning: true);
                return;
            }

            double radius = _songStartSeconds <= 0.05 ? 15.0 : 2.0;
            double? beatTime = FindNearestPeakTime(analysis, _songStartSeconds, radius);
            if (!beatTime.HasValue && _songStartSeconds <= 0.05)
            {
                foreach (double peakTime in analysis.PeakTimesSeconds)
                {
                    if (peakTime <= Math.Min(30.0, _trackDuration))
                    {
                        beatTime = peakTime;
                        break;
                    }
                }
            }

            if (!beatTime.HasValue)
            {
                SetSmartFitStatus("No strong beat near this point.", isWarning: true);
                return;
            }

            ApplySongStartSeconds(beatTime.Value, $"Snapped to {FormatSeconds(beatTime.Value)}.");
        }
        catch (OperationCanceledException)
        {
            SetSmartFitStatus("Beat scan timed out.", isWarning: true);
        }
        finally
        {
            if (ReferenceEquals(_audioAnalysisCts, cts))
                _audioAnalysisCts = null;
            cts.Dispose();
            button.IsEnabled = true;
            button.Opacity = 1.0;
        }
    }

    private async Task ApplySmartFitAsync(Button button)
    {
        if (_selectedTrack == null || !File.Exists(_selectedTrack.FilePath))
        {
            ShowToast("Select a music track first.");
            return;
        }

        CancelAudioAnalysis();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _audioAnalysisCts = cts;
        button.IsEnabled = false;
        button.Opacity = 0.5;
        SetSmartFitStatus("Finding a strong section...");

        try
        {
            var analysis = await AnalyzeAudioEnergyAsync(_selectedTrack.FilePath, cts.Token);
            double videoDuration = GetPhase3VideoDurationSeconds();
            double smartStart = analysis != null
                ? FindSmartFitStart(analysis, videoDuration)
                : 0.0;

            var duckingCheck = this.FindControl<CheckBox>("DuckingCheckBox");
            if (duckingCheck != null) duckingCheck.IsChecked = true;

            var carvingCheck = this.FindControl<CheckBox>("CarvingCheckBox");
            if (carvingCheck != null) carvingCheck.IsChecked = true;

            var musicVolSlider = this.FindControl<Slider>("MusicVolSlider");
            if (musicVolSlider != null && Math.Abs(musicVolSlider.Value - 100.0) < 0.5)
                musicVolSlider.Value = 85.0;

            ApplySongStartSeconds(smartStart, $"Smart Fit picked {FormatSeconds(smartStart)}.");

            if (_isMergerMode && GetQueuedMusicCoverageSeconds() < videoDuration - 0.5)
                BuildAutoFillQueue();

            UpdateDuckingCompareButton();
            ApplyPreviewMusicVolume();
            UpdateProblemFlags();
        }
        catch (OperationCanceledException)
        {
            SetSmartFitStatus("Smart Fit scan timed out.", isWarning: true);
        }
        finally
        {
            if (ReferenceEquals(_audioAnalysisCts, cts))
                _audioAnalysisCts = null;
            cts.Dispose();
            button.IsEnabled = true;
            button.Opacity = 1.0;
        }
    }

    private void BuildAutoFillQueue()
    {
        if (_selectedTrack == null)
            return;

        _pendingAutoFillMusicPaths.Clear();
        _pendingAutoFillMusicPaths.Add(_selectedTrack.FilePath);

        double targetDuration = GetPhase3VideoDurationSeconds();
        double coveredDuration = Math.Max(0, _selectedTrack.DurationSec - _songStartSeconds);
        foreach (var track in GetAutoFillSourceTracks().Where(t =>
            !string.Equals(t.FilePath, _selectedTrack.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            _pendingAutoFillMusicPaths.Add(track.FilePath);
            coveredDuration += Math.Max(1.0, track.DurationSec);
            if (coveredDuration >= targetDuration)
                break;
        }

        UpdateAutoFillQueuePreview();
        UpdateCoverageBar();
        UpdateFinalPlacementSummary();
        UpdateProblemFlags();
        DrawPhase3TimelineScale();

        var autoFillBtn = this.FindControl<Button>("AutoFillSongsBtn");
        if (autoFillBtn != null)
            autoFillBtn.Content = $"Auto-Filled {_pendingAutoFillMusicPaths.Count} Songs";
        ShowToast($"Auto-filled {_pendingAutoFillMusicPaths.Count} songs.");
    }

    private void ResetAutoFillQueueState()
    {
        _pendingAutoFillMusicPaths.Clear();
        AutoFillQueueItems.Clear();
        var queuePanel = this.FindControl<Grid>("AutoFillQueuePanel");
        if (queuePanel != null)
            queuePanel.IsVisible = false;
        var autoFillBtn = this.FindControl<Button>("AutoFillSongsBtn");
        if (autoFillBtn != null)
            autoFillBtn.Content = "Auto-Fill Remaining Time";
        UpdateAutoFillQueuePreview();
        UpdateProblemFlags();
    }

    private void UpdateAutoFillQueuePreview()
    {
        AutoFillQueueItems.Clear();

        var queuePanel = this.FindControl<Grid>("AutoFillQueuePanel");
        bool hasQueue = _pendingAutoFillMusicPaths.Count > 0;
        if (queuePanel != null)
            queuePanel.IsVisible = hasQueue;

        if (!hasQueue)
        {
            var remainingText = this.FindControl<TextBlock>("AutoFillRemainingText");
            if (remainingText != null)
                remainingText.Text = "";
            return;
        }

        double coveredDuration = 0.0;
        for (int i = 0; i < _pendingAutoFillMusicPaths.Count; i++)
        {
            string path = _pendingAutoFillMusicPaths[i];
            var track = FindTrackByPath(path);
            double offset = i == 0 ? _songStartSeconds : 0.0;
            double duration = Math.Max(0, (track?.DurationSec ?? 0.0) - offset);
            coveredDuration += duration;
            AutoFillQueueItems.Add(new MusicQueueItem
            {
                Order = i + 1,
                Name = track?.Name ?? Path.GetFileName(path),
                DurationText = duration > 0 ? FormatSeconds(duration) : "loading"
            });
        }

        double targetDuration = GetPhase3VideoDurationSeconds();
        double remaining = Math.Max(0, targetDuration - coveredDuration);
        var summaryText = this.FindControl<TextBlock>("AutoFillQueueSummaryText");
        if (summaryText != null)
            summaryText.Text = $"QUEUE: {AutoFillQueueItems.Count} song(s)";
        var uncoveredText = this.FindControl<TextBlock>("AutoFillRemainingText");
        if (uncoveredText != null)
            uncoveredText.Text = remaining <= 0.01
                ? "Coverage complete."
                : $"{FormatSeconds(remaining)} still uncovered.";
    }

    private void MoveQueuedTrack(int direction)
    {
        var queueList = this.FindControl<ListBox>("AutoFillQueueList");
        if (queueList == null || queueList.SelectedIndex <= 0)
        {
            ShowToast("Select an auto-fill song after the first track.");
            return;
        }

        int oldIndex = queueList.SelectedIndex;
        int newIndex = Math.Clamp(oldIndex + direction, 1, _pendingAutoFillMusicPaths.Count - 1);
        if (newIndex == oldIndex)
            return;

        string path = _pendingAutoFillMusicPaths[oldIndex];
        _pendingAutoFillMusicPaths.RemoveAt(oldIndex);
        _pendingAutoFillMusicPaths.Insert(newIndex, path);
        UpdateAutoFillQueuePreview();
        queueList.SelectedIndex = newIndex;
        UpdateCoverageBar();
        UpdateFinalPlacementSummary();
        UpdateProblemFlags();
        DrawPhase3TimelineScale();
    }

    private void RemoveSelectedQueuedTrack()
    {
        var queueList = this.FindControl<ListBox>("AutoFillQueueList");
        if (queueList == null || queueList.SelectedIndex <= 0)
        {
            ShowToast("Select an auto-fill song after the first track.");
            return;
        }

        int removedIndex = queueList.SelectedIndex;
        _pendingAutoFillMusicPaths.RemoveAt(removedIndex);
        UpdateAutoFillQueuePreview();
        if (_pendingAutoFillMusicPaths.Count > 1)
            queueList.SelectedIndex = Math.Min(removedIndex, _pendingAutoFillMusicPaths.Count - 1);
        UpdateCoverageBar();
        UpdateFinalPlacementSummary();
        UpdateProblemFlags();
        DrawPhase3TimelineScale();
    }

    private MusicTrackItem? FindTrackByPath(string path)
    {
        return _allTracks.FirstOrDefault(track =>
            string.Equals(track.FilePath, path, StringComparison.OrdinalIgnoreCase));
    }

    private void LoadRecentMusicPins()
    {
        try
        {
            var state = new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths).LoadSync();
            if (state["RecentMusicPaths"] is System.Text.Json.Nodes.JsonArray recentArray)
            {
                foreach (var node in recentArray)
                {
                    string? path = node?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(path))
                        _recentMusicPaths.Add(path);
                }
            }
        }
        catch { }
    }

    private void SaveRecentMusicPins(System.Collections.Generic.IEnumerable<string> selectedPaths)
    {
        try
        {
            var orderedPaths = selectedPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Concat(_recentMusicPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();

            _recentMusicPaths.Clear();
            foreach (string path in orderedPaths)
                _recentMusicPaths.Add(path);

            var recentArray = new System.Text.Json.Nodes.JsonArray();
            foreach (string path in orderedPaths)
                recentArray.Add(System.Text.Json.Nodes.JsonValue.Create(path));

            new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths)
                .UpdatePropertiesSync(new System.Text.Json.Nodes.JsonObject
                {
                    ["RecentMusicPaths"] = recentArray
                });
        }
        catch { }
    }

    private void HandleSongOffsetKeyDown(KeyEventArgs e)
    {
        double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 5.0 : 1.0;
        double maxStart = Math.Max(0, _trackDuration - 0.01);

        if (e.Key == Key.Left)
        {
            _songStartSeconds = Math.Clamp(_songStartSeconds - step, 0, maxStart);
        }
        else if (e.Key == Key.Right)
        {
            _songStartSeconds = Math.Clamp(_songStartSeconds + step, 0, maxStart);
        }
        else if (e.Key == Key.Home)
        {
            _songStartSeconds = 0;
        }
        else if (e.Key == Key.End)
        {
            _songStartSeconds = maxStart;
        }
        else
        {
            return;
        }

        ResetAutoFillQueueState();
        _previewCurrentOffset = _songStartSeconds;
        var lbl = this.FindControl<TextBlock>("OffsetLabel");
        if (lbl != null) lbl.Text = $"Song begins at {FormatSeconds(_songStartSeconds)}";
        UpdatePlayhead();
        UpdateFinalPlacementSummary();
        UpdateAutoFillQueuePreview();
        UpdateCoverageBar();
        UpdateProblemFlags();
        e.Handled = true;
    }

    private void HandlePhase3SeekKeyDown(KeyEventArgs e)
    {
        double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 5.0 : 1.0;
        double currentRelative = GetCurrentPhase3VideoRelativeSeconds();
        double duration = GetPhase3VideoDurationSeconds();

        if (e.Key == Key.Left)
        {
            SeekPhase3Relative(Math.Max(0, currentRelative - step));
        }
        else if (e.Key == Key.Right)
        {
            SeekPhase3Relative(Math.Min(duration, currentRelative + step));
        }
        else if (e.Key == Key.Home)
        {
            SeekPhase3Relative(0);
        }
        else if (e.Key == Key.End)
        {
            SeekPhase3Relative(duration);
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    private void SeekPhase3Relative(double videoRelativeSec)
    {
        double duration = GetPhase3VideoDurationSeconds();
        videoRelativeSec = Math.Clamp(videoRelativeSec, 0.0, duration);

        bool wasPlaying = _isPreviewPlaying;
        StopPreview();
        _previewCurrentOffset = _songStartSeconds + videoRelativeSec;
        SeekPhase3VideoHost(videoRelativeSec, forcePause: !wasPlaying);
        if (wasPlaying) StartPreviewInternal(_previewCurrentOffset);
        else UpdatePlayhead();
    }

    private void SeekPhase3VideoHost(double outputRelativeSec, bool forcePause)
    {
        var wizardVideoHost = WizardVideoHost;
        if (wizardVideoHost?.IpcClient == null) return;

        double sourceRelativeSec = MapPhase3OutputToSourceRelativeSeconds(outputRelativeSec);
        double sourceAbsSec = (_trimStartMs / 1000.0) + sourceRelativeSec;
        double speed = GetPhase3PreviewSpeedAtSourceRelativeSeconds(sourceRelativeSec);

        _ = wizardVideoHost.IpcClient.SetPropertyAsync("time-pos", sourceAbsSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _ = wizardVideoHost.IpcClient.SetPropertyAsync("speed", Math.Max(0.001, speed).ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture));
        if (forcePause || speed <= 0.001)
            _ = wizardVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
    }

    private void SyncPhase3VideoPreviewClock()
    {
        if (_currentStep != 3 || !_isPreviewPlaying) return;

        var wizardVideoHost = WizardVideoHost;
        if (wizardVideoHost?.IpcClient == null) return;

        double outputRelativeSec = GetCurrentPhase3VideoRelativeSeconds();
        double sourceRelativeSec = MapPhase3OutputToSourceRelativeSeconds(outputRelativeSec);
        double sourceAbsSec = (_trimStartMs / 1000.0) + sourceRelativeSec;
        double speed = GetPhase3PreviewSpeedAtSourceRelativeSeconds(sourceRelativeSec);

        if (speed <= 0.001)
        {
            _ = wizardVideoHost.IpcClient.SetPropertyAsync("time-pos", sourceAbsSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _ = wizardVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
            return;
        }

        _ = wizardVideoHost.IpcClient.SetPropertyAsync("speed", speed.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture));
        if (Math.Abs(wizardVideoHost.IpcClient.CurrentTime - sourceAbsSec) > 0.15)
            _ = wizardVideoHost.IpcClient.SetPropertyAsync("time-pos", sourceAbsSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _ = wizardVideoHost.IpcClient.SetPropertyAsync("pause", "no");
    }


    private void UpdateNextButtonState()

    {

        var nextBtn = this.FindControl<Button>("NextBtn");

        if (nextBtn == null) return;


        if (_currentStep == 1)

        {

            nextBtn.IsEnabled = _selectedTrack != null;

            if (_selectedTrack == null)

            {

                nextBtn.Opacity = 0.5;

                ToolTip.SetTip(nextBtn, "Please select a music track first");

            }

            else

            {

                nextBtn.Opacity = 1.0;

                ToolTip.SetTip(nextBtn, "Proceed to the next step");

            }

        }

        else if (_currentStep == 3)
        {
            nextBtn.IsEnabled = _phase3Ready;
            nextBtn.Opacity = _phase3Ready ? 1.0 : 0.5;
            ToolTip.SetTip(nextBtn, _phase3Ready ? "Apply music settings to your video" : "Wait for the final preview to finish loading");
        }
        else
        {
            nextBtn.IsEnabled = true;
            nextBtn.Opacity = 1.0;
            ToolTip.SetTip(nextBtn, "Proceed to the next step");
        }
    }


    private async void OnNextClicked(object? sender, RoutedEventArgs e)

    {

        if (_currentStep == 1)

        {

            if (_selectedTrack == null)

            {


                ShowToast("⚠ Please select a music track first!");

                return;

            }


            bool selectedTrackChanged = !string.Equals(_lastConfiguredTrackPath, _selectedTrack.FilePath, StringComparison.OrdinalIgnoreCase);

            var ffprobePath = ResolveFfprobePath();
            var prober = new FortniteVideoSoftware.Core.Media.MediaProber(ffprobePath, _selectedTrack.FilePath);
            double duration = await prober.GetDurationAsync();
            _trackDuration = Math.Max(1.0, duration > 0 ? duration : _selectedTrack.DurationSec);
            _selectedTrack.DurationSec = _trackDuration;

            if (selectedTrackChanged)
            {
                _songStartSeconds = 0;
                _lastLoadedTrackPath = null;
                _lastConfiguredTrackPath = _selectedTrack.FilePath;
            }
            _songStartSeconds = Math.Clamp(_songStartSeconds, 0, Math.Max(0, _trackDuration - 0.01));
            _previewCurrentOffset = _songStartSeconds;
            var lbl = this.FindControl<TextBlock>("OffsetLabel");
            if (lbl != null) lbl.Text = $"Song begins at {FormatSeconds(_songStartSeconds)}";


            Avalonia.Threading.Dispatcher.UIThread.Post(() => {

                DrawTimelineScale();

                UpdatePlayhead();

            });


            var selectedLabel = this.FindControl<TextBlock>("SelectedTrackLabel");

            if (selectedLabel != null) selectedLabel.Text = _selectedTrack.Name;


            _ = RenderWaveformAsync(_selectedTrack.FilePath);


            _currentStep = 2;

        }

        else if (_currentStep == 2)
        {
            StopPreview();
            CancelPhase3Load();
            _phase3Ready = false;
            _previewCurrentOffset = _songStartSeconds;
            _currentStep = 3;
            UpdateStepVisibility();
            UpdateNextButtonState();

            _phase3LoadCts = new CancellationTokenSource();
            int loadVersion = ++_phase3LoadVersion;
            await LoadPhase3DataAsync(_phase3LoadCts.Token, loadVersion);
            return;
        }
        else if (_currentStep == 3)
        {
            if (!_phase3Ready)
            {
                ShowToast("Final preview is still loading.");
                return;
            }

            var duckingCheck = this.FindControl<CheckBox>("DuckingCheckBox");
            var carvingCheck = this.FindControl<CheckBox>("CarvingCheckBox");
            var videoVolSlider = this.FindControl<Slider>("VideoVolSlider");
            var musicVolSlider = this.FindControl<Slider>("MusicVolSlider");
            double timelineStartSec = _trimStartMs / 1000.0;
            double timelineEndSec = timelineStartSec + GetPhase3SourceDurationSeconds();
            var resultMusicPaths = _pendingAutoFillMusicPaths.Count > 0
                ? new System.Collections.Generic.List<string>(_pendingAutoFillMusicPaths)
                : new System.Collections.Generic.List<string> { _selectedTrack?.FilePath ?? "" };

            Result = new MusicWizardResult
            {
                MusicFilePath = _selectedTrack?.FilePath ?? "",
                MusicFilePaths = resultMusicPaths,
                MusicDurationsSeconds = resultMusicPaths.Select(GetKnownTrackDurationSeconds).ToList(),
                OffsetSeconds = _songStartSeconds,
                TimelineStartSeconds = timelineStartSec,
                TimelineEndSeconds = timelineEndSec,
                EnableDucking = duckingCheck?.IsChecked ?? true,
                EnableCarving = carvingCheck?.IsChecked ?? true,
                VideoVolume = (videoVolSlider?.Value ?? 100.0) / 100.0,
                MusicVolume = (musicVolSlider?.Value ?? 100.0) / 100.0,
                MusicDurationSeconds = _trackDuration,
                LoopMusic = this.FindControl<CheckBox>("LoopMusicCheckBox")?.IsChecked ?? false
            };

            SaveRecentMusicPins(resultMusicPaths);
            RuntimeLog.Success("MUSIC_WIZARD", $"Wizard completed. Track: {Path.GetFileName(Result.MusicFilePath)}, SongStart: {Result.OffsetSeconds:F2}s, Timeline: {Result.TimelineStartSeconds:F2}-{Result.TimelineEndSeconds:F2}s, Ducking: {Result.EnableDucking}, Carving: {Result.EnableCarving}, VideoVol: {Result.VideoVolume}, MusicVol: {Result.MusicVolume}");
            RuntimeLog.Debug("MUSIC_WIZARD", $"Wizard completed track path: {Result.MusicFilePath}");
            _isSafeToClose = true;

            Close();

            return;

        }


        UpdateStepVisibility();

        UpdateNextButtonState();

    }

    private double GetKnownTrackDurationSeconds(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return 0.0;
        if (_selectedTrack != null &&
            string.Equals(_selectedTrack.FilePath, filePath, StringComparison.OrdinalIgnoreCase) &&
            _trackDuration > 0)
        {
            return _trackDuration;
        }

        var item = AvailableTracks.FirstOrDefault(track =>
            string.Equals(track.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        item ??= _allTracks.FirstOrDefault(track =>
            string.Equals(track.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        return item?.DurationSec > 0 ? item.DurationSec : 0.0;
    }


    private async Task<string?> GenerateThumbnailsStripAsync(string ffmpegPath, string videoPath, double startSec, double durationSec, CancellationToken cancellationToken, int frames = 15)
    {
        string? tempPng = null;
        Process? process = null;
        try
        {
            tempPng = Path.Combine(_paths.TempDirectory, $"fvs_thumb_{Guid.NewGuid():N}.png");
            if (durationSec <= 0) durationSec = 10;
            frames = Math.Max(1, frames);
            
            double fps = (double)frames / durationSec;
            string startArg = startSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            string durationArg = durationSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            string fpsArg = fps.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            string filter = $"fps=fps={fpsArg}:round=up,scale=-1:60,tpad=stop_mode=clone:stop_duration=1,tile={frames}x1:margin=0:padding=0";

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-y -hide_banner -loglevel error -ss {startArg} -t {durationArg} -i \"{videoPath}\" -vf \"{filter}\" -frames:v 1 \"{tempPng}\"",

                UseShellExecute = false,

                CreateNoWindow = true,

                RedirectStandardOutput = true,

                RedirectStandardError = true

            };


            process = Process.Start(psi);
            if (process == null) return null;
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode == 0 && File.Exists(tempPng)) return tempPng;
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (process != null && !process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
            if (tempPng != null && File.Exists(tempPng))
            {
                try { File.Delete(tempPng); } catch { }
            }
            throw;
        }
        catch { }
        return null;
    }

    private async Task<string?> GeneratePhase3MusicSequenceWaveformAsync(
        string ffmpegPath,
        System.Collections.Generic.IReadOnlyList<Phase3MusicPreviewSegment> segments,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        string? tempPng = null;
        Process? process = null;
        try
        {
            var usableSegments = segments
                .Where(segment => segment.TimelineEndSec > segment.TimelineStartSec + 0.001 &&
                                  !string.IsNullOrWhiteSpace(segment.Path) &&
                                  File.Exists(segment.Path))
                .ToList();

            if (usableSegments.Count == 0)
                return null;

            tempPng = Path.Combine(_paths.TempDirectory, $"fvs_wave_sequence_{Guid.NewGuid():N}.png");

            string inputArgs = string.Join(" ", usableSegments.Select(segment => $"-i \"{segment.Path}\""));
            var filter = new System.Text.StringBuilder();
            for (int i = 0; i < usableSegments.Count; i++)
            {
                var segment = usableSegments[i];
                double duration = Math.Max(0.001, segment.TimelineEndSec - segment.TimelineStartSec);
                filter.Append($"[{i}:a]atrim=start={segment.FileStartSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:duration={duration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},");
                filter.Append($"asetpts=PTS-STARTPTS,aformat=channel_layouts=mono[a{i}];");
            }

            if (usableSegments.Count > 1)
            {
                filter.Append(string.Concat(Enumerable.Range(0, usableSegments.Count).Select(i => $"[a{i}]")));
                filter.Append($"concat=n={usableSegments.Count}:v=0:a=1[a_seq];");
                filter.Append($"[a_seq]volume=1.5,showwavespic=s={width}x{height}:colors=0x7DD3FC:draw=full[v_wave]");
            }
            else
            {
                filter.Append($"[a0]volume=1.5,showwavespic=s={width}x{height}:colors=0x7DD3FC:draw=full[v_wave]");
            }

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-y -hide_banner -loglevel error {inputArgs} -filter_complex \"{filter}\" -map \"[v_wave]\" -frames:v 1 \"{tempPng}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            process = Process.Start(psi);
            if (process == null) return null;
            ChildProcessTracker.AddProcess(process);

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            _ = await outputTask;
            _ = await errorTask;

            if (process.ExitCode == 0 && File.Exists(tempPng))
                return tempPng;

            if (File.Exists(tempPng))
                File.Delete(tempPng);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (process != null && !process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }

            if (tempPng != null && File.Exists(tempPng))
            {
                try { File.Delete(tempPng); } catch { }
            }

            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Failed to generate sequence waveform: {ex.Message}");
            if (tempPng != null && File.Exists(tempPng))
            {
                try { File.Delete(tempPng); } catch { }
            }
        }
        finally
        {
            try { process?.Dispose(); } catch { }
        }

        return null;
    }


    private string FindBinary(string name)
    {
        string basePath = System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var current = new DirectoryInfo(basePath);
        while (current != null)
        {

            string fPath = Path.Combine(current.FullName, "frontend", name);
            if (File.Exists(fPath)) return Path.GetFullPath(fPath);

            string bPath = Path.Combine(current.FullName, "backend", name);
            if (File.Exists(bPath)) return Path.GetFullPath(bPath);

            string srcPath = Path.Combine(current.FullName, "binaries", name);
            if (File.Exists(srcPath)) return Path.GetFullPath(srcPath);


            current = current.Parent;

        }

        return name;

    }


    private string ResolveMpvPath() 
    {
        string p = FindBinary("mpv.exe");
        if (p == "mpv.exe")
        {
            string fallback = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "mpv.exe");
            if (File.Exists(fallback)) return Path.GetFullPath(fallback);
        }
        return p;
    }

    private string ResolveFfprobePath() 
    {
        string p = FindBinary("ffprobe.exe");
        if (p == "ffprobe.exe")
        {
            string fallback = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "ffprobe.exe");
            if (File.Exists(fallback)) return Path.GetFullPath(fallback);
        }
        return p;
    }

    private string ResolveFfmpegPath() 
    {
        string p = FindBinary("ffmpeg.exe");
        if (p == "ffmpeg.exe")
        {
            string fallback = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "ffmpeg.exe");
            if (File.Exists(fallback)) return Path.GetFullPath(fallback);
        }
        return p;
    }


    private void SetLoadingOverlay(string name, bool isVisible)
    {
        var overlay = this.FindControl<Avalonia.Controls.Border>(name);
        if (overlay != null) overlay.IsVisible = isVisible;
    }

    private async Task LoadPhase3DataAsync(CancellationToken cancellationToken, int loadVersion)
    {
        SetPhase3Status("Loading final preview...");
        SetLoadingOverlay("Phase3VideoLoadingOverlay", true);
        SetLoadingOverlay("Phase3ThumbLoadingOverlay", true);
        SetLoadingOverlay("Phase3WaveLoadingOverlay", true);
        _phase3Ready = false;
        UpdateNextButtonState();
        UpdatePreviewControlsState();

        try
        {
            _phase3VideoDurationSec = GetPhase3VideoDurationSeconds();
            var thumbLaneGrid = this.FindControl<Avalonia.Controls.Grid>("ThumbnailLaneGrid");
            var waveLane = this.FindControl<Avalonia.Controls.Image>("WaveformLaneImage");
            if (thumbLaneGrid != null) { thumbLaneGrid.Children.Clear(); thumbLaneGrid.ColumnDefinitions.Clear(); }
            if (waveLane != null) waveLane.Source = null;
            _phase3ClipDurationsSec.Clear();

            var border = this.FindControl<Avalonia.Controls.Border>("VideoHostBorder");
            if (border != null && !string.IsNullOrEmpty(_videoPath))
            {
                if (border.Child is FortniteVideoSoftware.App.MpvVideoView oldHost)
                {
                    oldHost.Dispose();
                    border.Child = null;
                }

                var wizardVideoHost = new FortniteVideoSoftware.App.MpvVideoView
                {
                    Name = "WizardVideoHost",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
                };
                border.Child = wizardVideoHost;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Task.Delay(50, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (_currentStep != 3 || loadVersion != _phase3LoadVersion) return;

                await wizardVideoHost.StartMpvProcessAsync(ResolveMpvPath());

                if (wizardVideoHost.IpcClient == null)
                    throw new InvalidOperationException("Video preview did not start.");

                await wizardVideoHost.IpcClient.LoadFileAsync(_videoPath, _trimStartMs / 1000.0);
                await wizardVideoHost.IpcClient.SetPropertyAsync("time-pos", (_trimStartMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture));
                await wizardVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
                RuntimeLog.Info("MUSIC_WIZARD", "Phase 3 MPV preview video loaded.");

                var videoVolSlider = this.FindControl<Slider>("VideoVolSlider");
                if (videoVolSlider != null)
                    await wizardVideoHost.IpcClient.SetPropertyDoubleAsync("volume", GetPreviewVideoVolume());
            }
            SetLoadingOverlay("Phase3VideoLoadingOverlay", false);

            cancellationToken.ThrowIfCancellationRequested();

            if (thumbLaneGrid != null)
            {
                var ffmpeg = ResolveFfmpegPath();
                var videosToThumb = (_isMergerMode && _mergerVideos != null && _mergerVideos.Count > 0) 
                    ? _mergerVideos : new System.Collections.Generic.List<string> { _videoPath };
                
                foreach (var f in _lastPhase3ThumbFiles) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
                _lastPhase3ThumbFiles.Clear();

                double totalDur = 0;
                var videoDurs = new System.Collections.Generic.List<double>();
                foreach (var v in videosToThumb)
                {
                    double dur = 10.0;
                    if (!_isMergerMode) dur = _phase3VideoDurationSec;
                    else
                    {
                        var prober = new FortniteVideoSoftware.Core.Media.MediaProber(ffmpeg.Replace("ffmpeg.exe", "ffprobe.exe"), v);
                        try { dur = await prober.GetDurationAsync(); } catch { dur = 10.0; }
                    }
                    videoDurs.Add(dur);
                    totalDur += dur;
                }

                if (totalDur <= 0) totalDur = 1.0;
                _phase3ClipDurationsSec.Clear();
                _phase3ClipDurationsSec.AddRange(videoDurs.Select(d => Math.Max(0.1, d / Math.Max(0.001, _phase3BaseSpeed))));

                for (int i = 0; i < videosToThumb.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (loadVersion != _phase3LoadVersion || _currentStep != 3) return;

                    double vDur = videoDurs[i];
                    double fraction = vDur / totalDur;
                    int framesCount = Math.Max(1, (int)Math.Round(15 * fraction));

                    double startOffset = (_isMergerMode || i > 0) ? 0 : (_trimStartMs / 1000.0);

                    string? thumbPath = await GenerateThumbnailsStripAsync(ffmpeg, videosToThumb[i], startOffset, vDur, cancellationToken, framesCount);
                    if (thumbPath != null) _lastPhase3ThumbFiles.Add(thumbPath);

                    thumbLaneGrid.ColumnDefinitions.Add(new Avalonia.Controls.ColumnDefinition(vDur, Avalonia.Controls.GridUnitType.Star));

                    var img = new Avalonia.Controls.Image { Stretch = Avalonia.Media.Stretch.Fill };
                    Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(img, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);

                    if (thumbPath != null)
                    {
                        try {
                            var fs = File.OpenRead(thumbPath);
                            img.Source = new Avalonia.Media.Imaging.Bitmap(fs);
                            fs.Dispose();
                        } catch { }
                    }
                    Avalonia.Controls.Grid.SetColumn(img, i);
                    thumbLaneGrid.Children.Add(img);
                }
            }
            SetLoadingOverlay("Phase3ThumbLoadingOverlay", false);

            cancellationToken.ThrowIfCancellationRequested();

            if (waveLane != null && _selectedTrack != null && !string.IsNullOrEmpty(_selectedTrack.FilePath))
            {
                var previewSegments = BuildPhase3MusicPreviewSegments();
                double audibleMusicDuration = previewSegments.Count > 0
                    ? Math.Min(GetPhase3VideoDurationSeconds(), previewSegments[^1].TimelineEndSec)
                    : 0.0;
                waveLane.Width = double.NaN;
                waveLane.Margin = new Avalonia.Thickness(0, 0, 0, 0);

                if (audibleMusicDuration > 0.01)
                {
                    var ffmpeg = ResolveFfmpegPath();
                    bool useSequenceWaveform = previewSegments.Count > 1 || IsPhase3LoopMusicEnabled();
                    string? wavePath = useSequenceWaveform
                        ? await GeneratePhase3MusicSequenceWaveformAsync(ffmpeg, previewSegments, 1200, 60, cancellationToken)
                        : await FortniteVideoSoftware.Core.Media.WaveformGenerator.GenerateWaveformImageAsync(
                            ffmpeg, _selectedTrack.FilePath, 1200, 60, _songStartSeconds, audibleMusicDuration, cancellationToken);

                    if (loadVersion != _phase3LoadVersion || _currentStep != 3) return;
                    if (wavePath != null)
                    {
                        try
                        {
                            using var fs = File.OpenRead(wavePath);
                            waveLane.Source = new Avalonia.Media.Imaging.Bitmap(fs);
                            DeleteTempFile(ref _lastPhase3WaveFile);
                            _lastPhase3WaveFile = wavePath;
                            UpdatePhase3WaveformLaneWidth();
                        }
                        catch { }
                    }
                }
            }
            SetLoadingOverlay("Phase3WaveLoadingOverlay", false);

            if (loadVersion != _phase3LoadVersion || _currentStep != 3) return;
            _phase3Ready = true;
            SetPhase3Status("");
            UpdateFinalPlacementSummary();
            UpdateProblemFlags();
            DrawPhase3TimelineScale();
            UpdatePlayhead();
            UpdateNextButtonState();
            UpdatePreviewControlsState();
        }
        catch (OperationCanceledException)
        {
            SetPhase3Status("");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Failed to load phase 3 preview: {ex.Message}");
            SetPhase3Status("Final preview could not load. You can go back and try again.");
            _phase3Ready = false;
            UpdateNextButtonState();
            UpdatePreviewControlsState();
        }
        finally
        {
            SetLoadingOverlay("Phase3VideoLoadingOverlay", false);
            SetLoadingOverlay("Phase3ThumbLoadingOverlay", false);
            SetLoadingOverlay("Phase3WaveLoadingOverlay", false);

            if (loadVersion == _phase3LoadVersion)
            {
                DrawPhase3TimelineScale();
            }
        }
    }

    private void CancelPhase3Load()
    {
        _phase3LoadVersion++;
        try { _phase3LoadCts?.Cancel(); } catch { }
        try { _phase3LoadCts?.Dispose(); } catch { }
        _phase3LoadCts = null;
        _phase3Ready = false;
    }

    private void DisposePhase3VideoHost()
    {
        var border = this.FindControl<Avalonia.Controls.Border>("VideoHostBorder");
        if (border?.Child is FortniteVideoSoftware.App.MpvVideoView wizardVideoHost)
        {
            wizardVideoHost.Dispose();
            border.Child = null;
        }
    }

    private double GetPhase3VideoDurationSeconds()
    {
        double duration = _trimEndMs > _trimStartMs
            ? (_trimEndMs - _trimStartMs) / 1000.0
            : (_actualVideoDurationMs > _trimStartMs ? (_actualVideoDurationMs - _trimStartMs) / 1000.0 : 60.0);
        double effectiveDuration = CalculatePhase3EffectiveDurationSeconds(duration);
        return Math.Max(0.1, effectiveDuration);
    }

    private double GetPhase3SourceDurationSeconds()
    {
        double duration = _trimEndMs > _trimStartMs
            ? (_trimEndMs - _trimStartMs) / 1000.0
            : (_actualVideoDurationMs > _trimStartMs ? (_actualVideoDurationMs - _trimStartMs) / 1000.0 : 60.0);
        return Math.Max(0.1, duration);
    }

    private double CalculatePhase3EffectiveDurationSeconds(double sourceDurationSec)
    {
        double trimStartMs = _trimStartMs;
        double trimEndMs = trimStartMs + sourceDurationSec * 1000.0;
        if (_phase3SpeedSegments.Count == 0)
            return sourceDurationSec / Math.Max(0.001, _phase3BaseSpeed);

        double totalMs = 0.0;
        double cursor = trimStartMs;
        var sortedSegments = new System.Collections.Generic.List<FortniteVideoSoftware.Core.Media.SpeedSegment>(_phase3SpeedSegments);
        sortedSegments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

        foreach (var seg in sortedSegments)
        {
            double segStart = Math.Max(trimStartMs, Math.Max(seg.StartMs, cursor));
            double segEnd = Math.Min(trimEndMs, seg.EndMs);
            if (segEnd <= segStart) continue;

            if (segStart > cursor)
                totalMs += (segStart - cursor) / Math.Max(0.001, _phase3BaseSpeed);

            totalMs += Math.Abs(seg.Speed) < 0.001
                ? segEnd - segStart
                : (segEnd - segStart) / Math.Max(0.001, seg.Speed);

            cursor = Math.Max(cursor, segEnd);
        }

        if (cursor < trimEndMs)
            totalMs += (trimEndMs - cursor) / Math.Max(0.001, _phase3BaseSpeed);

        return Math.Max(0.001, totalMs / 1000.0);
    }

    private double MapPhase3OutputToSourceRelativeSeconds(double outputRelativeSec)
    {
        double sourceDurationSec = GetPhase3SourceDurationSeconds();
        double trimStartMs = _trimStartMs;
        double trimEndMs = trimStartMs + sourceDurationSec * 1000.0;
        double targetMs = Math.Clamp(outputRelativeSec, 0, GetPhase3VideoDurationSeconds()) * 1000.0;
        double outCursorMs = 0.0;

        var sortedSegments = new System.Collections.Generic.List<FortniteVideoSoftware.Core.Media.SpeedSegment>(_phase3SpeedSegments);
        sortedSegments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

        double cursor = trimStartMs;
        foreach (var seg in sortedSegments)
        {
            double segStart = Math.Max(trimStartMs, Math.Max(seg.StartMs, cursor));
            double segEnd = Math.Min(trimEndMs, seg.EndMs);
            if (segEnd <= segStart) continue;

            if (segStart > cursor)
            {
                double chunkOutMs = (segStart - cursor) / Math.Max(0.001, _phase3BaseSpeed);
                if (targetMs <= outCursorMs + chunkOutMs)
                    return Math.Clamp((cursor - trimStartMs) / 1000.0 + ((targetMs - outCursorMs) * _phase3BaseSpeed) / 1000.0, 0, sourceDurationSec);
                outCursorMs += chunkOutMs;
            }

            if (Math.Abs(seg.Speed) < 0.001)
            {
                double freezeOutMs = segEnd - segStart;
                if (targetMs <= outCursorMs + freezeOutMs)
                    return Math.Clamp((segStart - trimStartMs) / 1000.0, 0, sourceDurationSec);
                outCursorMs += freezeOutMs;
            }
            else
            {
                double chunkOutMs = (segEnd - segStart) / Math.Max(0.001, seg.Speed);
                if (targetMs <= outCursorMs + chunkOutMs)
                    return Math.Clamp((segStart - trimStartMs) / 1000.0 + ((targetMs - outCursorMs) * seg.Speed) / 1000.0, 0, sourceDurationSec);
                outCursorMs += chunkOutMs;
            }

            cursor = Math.Max(cursor, segEnd);
        }

        if (cursor < trimEndMs)
        {
            double chunkOutMs = (trimEndMs - cursor) / Math.Max(0.001, _phase3BaseSpeed);
            if (targetMs <= outCursorMs + chunkOutMs)
                return Math.Clamp((cursor - trimStartMs) / 1000.0 + ((targetMs - outCursorMs) * _phase3BaseSpeed) / 1000.0, 0, sourceDurationSec);
        }

        return sourceDurationSec;
    }

    private double GetPhase3PreviewSpeedAtSourceRelativeSeconds(double sourceRelativeSec)
    {
        double sourceAbsMs = _trimStartMs + sourceRelativeSec * 1000.0;
        foreach (var seg in _phase3SpeedSegments)
        {
            if (sourceAbsMs >= seg.StartMs && sourceAbsMs <= seg.EndMs)
                return Math.Max(0.0, seg.Speed);
        }

        return Math.Max(0.001, _phase3BaseSpeed);
    }

    private double GetAudibleMusicDurationSeconds()
    {
        double remainingSong = Math.Max(0, _trackDuration - _songStartSeconds);
        return Math.Min(GetPhase3VideoDurationSeconds(), remainingSong);
    }

    private double GetCurrentPhase3VideoRelativeSeconds()
    {
        double fallback = Math.Clamp(_previewCurrentOffset - _songStartSeconds, 0, GetPhase3VideoDurationSeconds());
        if (_currentStep == 3 && _isPreviewPlaying && _phase3PreviewClockStartTime.HasValue)
            return Math.Clamp(
                _phase3PreviewClockStartOffsetSec + (DateTime.UtcNow - _phase3PreviewClockStartTime.Value).TotalSeconds,
                0,
                GetPhase3VideoDurationSeconds());

        return fallback;
    }

    private void EnforcePhase3PreviewEnd()
    {
        if (_currentStep != 3 || !_isPreviewPlaying) return;

        double videoDuration = GetPhase3VideoDurationSeconds();
        if (GetCurrentPhase3VideoRelativeSeconds() < videoDuration - 0.03) return;

        _previewCurrentOffset = _songStartSeconds + videoDuration;
        var wizardVideoHost = WizardVideoHost;
        if (wizardVideoHost?.IpcClient != null)
        {
            double endTime = (_trimStartMs / 1000.0) + MapPhase3OutputToSourceRelativeSeconds(videoDuration);
            _ = wizardVideoHost.IpcClient.SetPropertyAsync("time-pos", endTime.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        StopPreview();
        UpdatePlayhead();
    }

    private void SaveWizardVolumes()
    {
        if (!FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.Defaults.RememberMusicVolumes) return;
        try
        {
            var videoVolSlider = this.FindControl<Avalonia.Controls.Slider>("VideoVolSlider");
            var musicVolSlider = this.FindControl<Avalonia.Controls.Slider>("MusicVolSlider");
            var updates = new System.Text.Json.Nodes.JsonObject();
            if (videoVolSlider != null) updates["WizardVideoVolume"] = videoVolSlider.Value;
            if (musicVolSlider != null) updates["WizardMusicVolume"] = musicVolSlider.Value;
            new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths).UpdatePropertiesSync(updates);
        }
        catch { }
    }

    private void UpdatePhase3WaveformLaneWidth()
    {
        var clip = this.FindControl<Canvas>("Phase3WaveformClip");
        var waveImg = this.FindControl<Avalonia.Controls.Image>("WaveformLaneImage");
        if (clip == null || waveImg == null) return;

        double videoDuration = GetPhase3VideoDurationSeconds();
        if (videoDuration <= 0) return;

        double ratio = GetQueuedMusicCoverageSeconds() / videoDuration;
        waveImg.Width = clip.Bounds.Width * ratio;
        waveImg.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        waveImg.Height = 60;
    }

    private void UpdateFinalPlacementSummary()
    {
        var label = this.FindControl<TextBlock>("FinalPlacementLabel");
        if (label == null) return;

        if (_selectedTrack == null)
        {
            label.Text = "Select a music track to continue.";
            return;
        }

        double trimStartSec = _trimStartMs / 1000.0;
        double trimEndSec = trimStartSec + GetPhase3VideoDurationSeconds();
        double audibleMusic = GetQueuedMusicCoverageSeconds();
        string endText = audibleMusic >= GetPhase3VideoDurationSeconds() - 0.01
            ? "Music is trimmed at the video end."
            : $"Only {FormatSeconds(audibleMusic)} of music remains after this song point.";

        string queueText = _pendingAutoFillMusicPaths.Count > 1
            ? $" Auto-Fill queue has {_pendingAutoFillMusicPaths.Count} songs."
            : "";
        label.Text = $"Song starts at {FormatSeconds(_songStartSeconds)}. Video range is {FormatSeconds(trimStartSec)} to {FormatSeconds(trimEndSec)}. {endText}{queueText}";
    }

    private void UpdateCoverageBar()
    {
        if (!_isMergerMode) return;

        double videoDuration = GetPhase3VideoDurationSeconds();
        double audibleMusic = GetQueuedMusicCoverageSeconds();

        var loopCheck = this.FindControl<CheckBox>("LoopMusicCheckBox");
        bool loopEnabled = loopCheck?.IsChecked ?? false;

        double coveragePercent = loopEnabled ? 100.0 : Math.Min(100.0, (audibleMusic / videoDuration) * 100.0);

        var fill = this.FindControl<Border>("CoverageBarFill");
        if (fill != null)
        {
            double panelWidth = this.FindControl<Avalonia.Controls.Control>("MultiSongHelperPanel")?.Bounds.Width ?? 200;
            fill.Width = Math.Max(0, panelWidth * (coveragePercent / 100.0) - 24);
            fill.Background = coveragePercent >= 99.9
                ? Avalonia.Media.Brush.Parse("#22c55e")
                : coveragePercent >= 50
                    ? Avalonia.Media.Brush.Parse("#facc15")
                    : Avalonia.Media.Brush.Parse("#ef4444");
        }

        var pctText = this.FindControl<TextBlock>("CoveragePercentText");
        if (pctText != null)
        {
            pctText.Text = $"{coveragePercent:0}%";
            pctText.Foreground = coveragePercent >= 99.9
                ? Avalonia.Media.Brush.Parse("#22c55e")
                : Avalonia.Media.Brush.Parse("#facc15");
        }

        var barText = this.FindControl<TextBlock>("CoverageBarText");
        if (barText != null)
        {
            barText.Text = loopEnabled
                ? "Music loops - full coverage"
                : $"{FormatSeconds(audibleMusic)} / {FormatSeconds(videoDuration)}";
        }

        var warningBanner = this.FindControl<Border>("CoverageWarningBanner");
        var warningText = this.FindControl<TextBlock>("CoverageWarningText");
        if (warningBanner != null && warningText != null)
        {
            if (coveragePercent >= 99.9 || loopEnabled)
            {
                warningBanner.IsVisible = false;
            }
            else
            {
                double uncovered = videoDuration - audibleMusic;
                warningBanner.IsVisible = true;
                warningText.Text = $"WARNING: Your music covers {coveragePercent:0}% of the video. The last {FormatSeconds(uncovered)} will have NO music. Add more songs, enable looping, or continue anyway.";
            }
        }
    }

    private double GetQueuedMusicCoverageSeconds()
    {
        var segments = BuildPhase3MusicPreviewSegments();
        if (segments.Count == 0)
            return 0.0;

        return Math.Min(GetPhase3VideoDurationSeconds(), segments[^1].TimelineEndSec);
    }

    private bool IsPhase3LoopMusicEnabled()
    {
        return _isMergerMode && (this.FindControl<CheckBox>("LoopMusicCheckBox")?.IsChecked ?? false);
    }

    private System.Collections.Generic.List<Phase3MusicPreviewSegment> BuildPhase3MusicPreviewSegments()
    {
        var segments = new System.Collections.Generic.List<Phase3MusicPreviewSegment>();
        if (_selectedTrack == null)
            return segments;

        double targetDuration = GetPhase3VideoDurationSeconds();
        if (targetDuration <= 0.01)
            return segments;

        var sourcePaths = (_pendingAutoFillMusicPaths.Count > 0
                ? _pendingAutoFillMusicPaths
                : new System.Collections.Generic.List<string> { _selectedTrack.FilePath })
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .ToList();

        if (sourcePaths.Count == 0)
            return segments;

        bool loopEnabled = IsPhase3LoopMusicEnabled();
        double cursor = 0.0;
        bool firstSegment = true;
        int guard = 0;

        do
        {
            bool addedAny = false;
            foreach (string path in sourcePaths)
            {
                if (cursor >= targetDuration - 0.001)
                    break;

                double fileStart = firstSegment ? _songStartSeconds : 0.0;
                double knownDuration = GetKnownTrackDurationSeconds(path);
                if (knownDuration <= 0.01 && string.Equals(path, _selectedTrack.FilePath, StringComparison.OrdinalIgnoreCase))
                    knownDuration = _trackDuration;
                if (knownDuration <= 0.01)
                {
                    firstSegment = false;
                    continue;
                }

                double availableDuration = Math.Max(0.0, knownDuration - fileStart);
                double takeDuration = Math.Min(availableDuration, targetDuration - cursor);
                firstSegment = false;

                if (takeDuration <= 0.001)
                    continue;

                segments.Add(new Phase3MusicPreviewSegment
                {
                    Path = path,
                    TimelineStartSec = cursor,
                    TimelineEndSec = cursor + takeDuration,
                    FileStartSec = fileStart
                });

                cursor += takeDuration;
                addedAny = true;
            }

            if (!loopEnabled || !addedAny)
                break;
        }
        while (cursor < targetDuration - 0.001 && ++guard < 1000);

        return segments;
    }

    private Phase3MusicPreviewSegment? FindPhase3MusicPreviewSegment(double outputRelativeSec)
    {
        foreach (var segment in BuildPhase3MusicPreviewSegments())
        {
            if (outputRelativeSec >= segment.TimelineStartSec &&
                outputRelativeSec < segment.TimelineEndSec - 0.005)
            {
                return segment;
            }
        }

        return null;
    }

    private async void QueuePhase3MusicPreviewSync()
    {
        if (_currentStep != 3 || !_isPreviewPlaying || _audioIpcClient == null)
            return;

        if (Interlocked.Exchange(ref _phase3MusicSyncInFlight, 1) == 1)
            return;

        try
        {
            await SyncPhase3MusicPreviewTrackAsync(forceReload: false);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Phase 3 music preview sync failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _phase3MusicSyncInFlight, 0);
        }
    }

    private async Task EnsureAudioPreviewClientAsync()
    {
        if (_audioIpcClient != null)
            return;

        _audioIpcClient = new FortniteVideoSoftware.Core.Media.MpvIpcClient();
        await _audioIpcClient.StartAudioOnlyAsync(ResolveMpvPath());
    }

    private async Task SyncPhase3MusicPreviewTrackAsync(bool forceReload)
    {
        if (_currentStep != 3 || !_isPreviewPlaying)
            return;

        await EnsureAudioPreviewClientAsync();

        if (_audioIpcClient == null)
            return;

        double outputRelativeSec = GetCurrentPhase3VideoRelativeSeconds();
        var segment = FindPhase3MusicPreviewSegment(outputRelativeSec);
        if (segment == null)
        {
            await _audioIpcClient.SetPropertyAsync("pause", "yes");
            _phase3PreviewMusicPath = null;
            _phase3PreviewMusicSegmentStartSec = double.NaN;
            return;
        }

        double audioStartOffset = Math.Max(0.0, segment.FileStartSec + outputRelativeSec - segment.TimelineStartSec);
        string targetPath = segment.Path.Replace("\\", "/");
        bool segmentChanged =
            !string.Equals(_phase3PreviewMusicPath, targetPath, StringComparison.OrdinalIgnoreCase) ||
            Math.Abs(_phase3PreviewMusicSegmentStartSec - segment.TimelineStartSec) > 0.001;

        if (forceReload || segmentChanged)
        {
            await _audioIpcClient.SetPropertyAsync("start", audioStartOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await _audioIpcClient.SendCommandAsync("loadfile", targetPath, "replace");
            _phase3PreviewMusicPath = targetPath;
            _phase3PreviewMusicSegmentStartSec = segment.TimelineStartSec;
        }
        else if (Math.Abs(_audioIpcClient.CurrentTime - audioStartOffset) > 0.5)
        {
            await _audioIpcClient.SendCommandAsync("seek", audioStartOffset.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
        }

        await _audioIpcClient.SetPropertyDoubleAsync("volume", GetPreviewMusicVolume());
        await _audioIpcClient.SetPropertyAsync("pause", "no");
    }

    private void UpdatePreviewControlsState()
    {
        bool enabled = _selectedTrack != null && (_currentStep == 2 || (_currentStep == 3 && _phase3Ready));
        foreach (string name in new[] { "PlayBtn", "SkipBackBtn", "SkipForwardBtn" })
        {
            var btn = this.FindControl<Button>(name);
            if (btn == null) continue;
            btn.IsEnabled = enabled;
            btn.Opacity = enabled ? 1.0 : 0.5;
        }
    }

    private void UpdateDuckingCompareButton()
    {
        var btn = this.FindControl<Button>("DuckingCompareBtn");
        if (btn == null) return;

        bool duckingEnabled = this.FindControl<CheckBox>("DuckingCheckBox")?.IsChecked ?? true;
        btn.Content = duckingEnabled ? "Export Ducking ON" : "Export Ducking OFF";
        btn.Classes.Clear();
        btn.Classes.Add(duckingEnabled ? "Primary" : "Secondary");
        ToolTip.SetTip(btn, duckingEnabled
            ? "FFmpeg will apply sidechain ducking during export; preview uses the volume sliders only."
            : "FFmpeg will export without sidechain ducking; preview uses the volume sliders only.");
    }

    private double GetPreviewVideoVolume(double? masterVolume = null)
    {
        double videoVolume = this.FindControl<Slider>("VideoVolSlider")?.Value ?? 100.0;
        double master = masterVolume ?? FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolume;
        return Math.Clamp(videoVolume * master / 100.0, 0.0, 100.0);
    }

    private double GetPreviewMusicVolume(double? masterVolume = null)
    {
        double musicVolume = this.FindControl<Slider>("MusicVolSlider")?.Value ?? 100.0;
        double master = masterVolume ?? FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolume;
        musicVolume = musicVolume * master / 100.0;
        return Math.Clamp(musicVolume, 0.0, 100.0);
    }

    private void ApplyPreviewMusicVolume()
    {
        if (_audioIpcClient == null) return;
        _ = _audioIpcClient.SetPropertyDoubleAsync("volume", GetPreviewMusicVolume());
    }

    private void UpdateProblemFlags()
    {
        var panel = this.FindControl<Border>("ProblemFlagsPanel");
        var text = this.FindControl<TextBlock>("ProblemFlagsText");
        if (panel == null || text == null) return;

        var flags = new System.Collections.Generic.List<string>();
        if (_selectedTrack == null)
        {
            panel.IsVisible = false;
            text.Text = "";
            return;
        }

        if (string.IsNullOrWhiteSpace(_selectedTrack.FilePath) || !File.Exists(_selectedTrack.FilePath))
            flags.Add("Music file is missing.");

        double videoDuration = GetPhase3VideoDurationSeconds();
        double coverage = GetQueuedMusicCoverageSeconds();
        bool loopEnabled = this.FindControl<CheckBox>("LoopMusicCheckBox")?.IsChecked ?? false;
        if (!loopEnabled && videoDuration > 0.1 && coverage < videoDuration - 0.5)
            flags.Add($"Music ends {FormatSeconds(videoDuration - coverage)} before the video ends.");

        if (_trackDuration <= 0.01)
            flags.Add("Song length is unknown.");
        else if (_songStartSeconds >= _trackDuration - 0.1)
            flags.Add("Song start is at the very end of the song.");

        double videoVolume = this.FindControl<Slider>("VideoVolSlider")?.Value ?? 100.0;
        double musicVolume = this.FindControl<Slider>("MusicVolSlider")?.Value ?? 100.0;
        if (musicVolume <= 1.0)
            flags.Add("Music volume is muted.");
        if (videoVolume <= 1.0)
            flags.Add("Original video audio is muted.");

        bool duckingEnabled = this.FindControl<CheckBox>("DuckingCheckBox")?.IsChecked ?? true;
        if (!duckingEnabled && videoVolume >= 70.0 && musicVolume >= 80.0)
            flags.Add("Ducking is off while both music and video audio are loud.");

        if (_isMergerMode && _mergerVideos != null && _phase3ClipDurationsSec.Count > 0 &&
            _phase3ClipDurationsSec.Count != _mergerVideos.Count)
        {
            flags.Add("Clip boundary preview could not confirm every merged clip duration.");
        }

        panel.IsVisible = _currentStep == 3 && flags.Count > 0;
        text.Text = string.Join(Environment.NewLine, flags.Select(flag => $"WARNING: {flag}"));
    }

    private void SetPhase3Status(string message)
    {
        var status = this.FindControl<TextBlock>("Phase3StatusLabel");
        if (status != null) status.Text = message;
    }

    private static string FormatSeconds(double seconds)
    {
        seconds = Math.Max(0, seconds);
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss\.ff")
            : ts.ToString(@"m\:ss\.ff");
    }

    private static void DeleteTempFile(ref string? path)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try { File.Delete(path); } catch { }
        }
        path = null;
    }

    private void TogglePreview()
    {
        if (_isPreviewPlaying)
        {
            StopPreview();
            return;

        }


        if (_selectedTrack == null || !File.Exists(_selectedTrack.FilePath))

        {

            ShowToast("⚠ Select a track first to preview!");
            return;
        }

        if (_currentStep == 3 && !_phase3Ready)
        {
            ShowToast("Final preview is still loading.");
            return;
        }

        double startOffset = _previewCurrentOffset;

        StartPreviewInternal(startOffset);
    }

    private void SkipPreview(double offsetSeconds)
    {
        if (_selectedTrack == null) return;
        if (_currentStep == 3 && !_phase3Ready) return;

        bool wasPlaying = _isPreviewPlaying;
        StopPreview();

        if (_currentStep == 3)
        {
            double videoRelative = Math.Clamp(GetCurrentPhase3VideoRelativeSeconds() + offsetSeconds, 0, GetPhase3VideoDurationSeconds());
            _previewCurrentOffset = _songStartSeconds + videoRelative;
            SeekPhase3VideoHost(videoRelative, forcePause: !wasPlaying);
        }
        else
        {
            _previewCurrentOffset += offsetSeconds;
            if (_previewCurrentOffset < 0) _previewCurrentOffset = 0;
            if (_previewCurrentOffset > _selectedTrack.DurationSec) _previewCurrentOffset = _selectedTrack.DurationSec;
        }

        if (wasPlaying)
        {
            StartPreviewInternal(_previewCurrentOffset);
        }

    }


    private async void StartPreviewInternal(double startOffset)

    {

        var playBtn = this.FindControl<Button>("PlayBtn");

        if (playBtn != null)

        {

            playBtn.Classes.Remove("Success");

            playBtn.Classes.Add("Danger");

            var playIcon = this.FindControl<Avalonia.Controls.Shapes.Polygon>("PlayIcon");

            var pauseIcon = this.FindControl<StackPanel>("PauseIcon");

            if (playIcon != null) playIcon.IsVisible = false;

            if (pauseIcon != null) pauseIcon.IsVisible = true;

        }

        _isPreviewPlaying = true;

        _previewCurrentOffset = startOffset;

        _previewStartTime = DateTime.UtcNow;
        _phase3PreviewClockStartTime = null;


        if (_currentStep == 3)
        {
            double outputRelativeSec = Math.Clamp(startOffset - _songStartSeconds, 0, GetPhase3VideoDurationSeconds());
            _phase3PreviewClockStartOffsetSec = outputRelativeSec;
            _phase3PreviewClockStartTime = DateTime.UtcNow;
            SeekPhase3VideoHost(outputRelativeSec, forcePause: false);
            SyncPhase3VideoPreviewClock();

        }


        try

        {

            await EnsureAudioPreviewClientAsync();
            var audioClient = _audioIpcClient;
            if (audioClient == null)
                return;


            double audioStartOffset = Math.Clamp(startOffset, 0, _trackDuration);
            if (_currentStep == 3)
            {
                await SyncPhase3MusicPreviewTrackAsync(forceReload: true);
                return;
            }

            string targetPath = _selectedTrack!.FilePath.Replace("\\", "/");
            if (_lastLoadedTrackPath != targetPath)
            {
                await audioClient.SetPropertyAsync("start", audioStartOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await audioClient.SendCommandAsync("loadfile", targetPath, "replace");
                _lastLoadedTrackPath = targetPath;
            }
            else
            {
                await audioClient.SendCommandAsync("seek", audioStartOffset.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
            }

            await audioClient.SetPropertyDoubleAsync("volume", GetPreviewMusicVolume());
            await audioClient.SetPropertyAsync("pause", "no");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Preview failed: {ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}");
            ShowToast($"⚠ Preview playback failed: {ex.Message}");
        }
    }

    private void StopPreview()
    {
        if (_previewStartTime.HasValue)
        {
            if (_currentStep == 3)
            {
                _previewCurrentOffset = _songStartSeconds + GetCurrentPhase3VideoRelativeSeconds();
                _phase3PreviewClockStartTime = null;
            }
            else
            {
                _previewCurrentOffset += (DateTime.UtcNow - _previewStartTime.Value).TotalSeconds;
            }

            if (_selectedTrack != null && _currentStep != 3 && _previewCurrentOffset > _selectedTrack.DurationSec)
                _previewCurrentOffset = _selectedTrack.DurationSec;
            _previewStartTime = null;
        }


        _isPreviewPlaying = false;
        _phase3PreviewMusicPath = null;
        _phase3PreviewMusicSegmentStartSec = double.NaN;


        if (_audioIpcClient != null)

        {

            _ = _audioIpcClient.SetPropertyAsync("pause", "yes");

        }


        if (_currentStep == 3)

        {

            var wizardVideoHost = WizardVideoHost;

            if (wizardVideoHost?.IpcClient != null)

                _ = wizardVideoHost.IpcClient.SetPropertyAsync("pause", "yes");

        }


        var playBtn = this.FindControl<Button>("PlayBtn");

        if (playBtn != null)

        {

            playBtn.Classes.Remove("Danger");

            playBtn.Classes.Add("Success");

            var playIcon = this.FindControl<Avalonia.Controls.Shapes.Polygon>("PlayIcon");

            var pauseIcon = this.FindControl<StackPanel>("PauseIcon");

            if (playIcon != null) playIcon.IsVisible = true;

            if (pauseIcon != null) pauseIcon.IsVisible = false;

        }

    }


    private async Task RenderWaveformAsync(string? filePath)

    {

        if (string.IsNullOrEmpty(filePath)) return;
        int renderVersion = System.Threading.Interlocked.Increment(ref _waveformRenderVersion);
        string requestedPath = filePath;


        var waveformImage = this.FindControl<Image>("WaveformImage");

        var loadingText = this.FindControl<TextBlock>("WaveformLoadingText");


        if (waveformImage == null || loadingText == null) return;


        loadingText.IsVisible = true;
        loadingText.Text = "Generating Waveform...";

        waveformImage.Source = null;


        var ffmpegPath = ResolveFfmpegPath();


        string? pngFile = await FortniteVideoSoftware.Core.Media.WaveformGenerator.GenerateWaveformImageAsync(ffmpegPath, filePath);

        if (renderVersion != _waveformRenderVersion ||
            !string.Equals(_selectedTrack?.FilePath, requestedPath, StringComparison.OrdinalIgnoreCase))
        {
            if (pngFile != null && File.Exists(pngFile))
            {
                try { File.Delete(pngFile); } catch { }
            }
            return;
        }

        loadingText.IsVisible = false;


        if (pngFile != null && File.Exists(pngFile))

        {

            try

            {

                using var fs = File.OpenRead(pngFile);

                var bitmap = new Avalonia.Media.Imaging.Bitmap(fs);

                waveformImage.Source = bitmap;


                if (_lastWaveformFile != null && File.Exists(_lastWaveformFile))

                {

                    try { File.Delete(_lastWaveformFile); } catch { }

                }

                _lastWaveformFile = pngFile;


                UpdatePlayhead();

            }

            catch (Exception ex)

            {

                RuntimeLog.Fail("MUSIC_WIZARD", "Failed to load waveform: " + ex.Message);

            }

        }

        else

        {

            loadingText.Text = "Failed to generate waveform.";

            loadingText.IsVisible = true;

        }

    }


    private void DrawTimelineScale()

    {

        var scaleCanvas = this.FindControl<Canvas>("TimelineScaleCanvas");

        if (scaleCanvas == null || _trackDuration <= 0) return;


        double canvasWidth = scaleCanvas.Bounds.Width;

        if (canvasWidth <= 0) return;


        scaleCanvas.Children.Clear();


        double interval = 10.0;

        if (_trackDuration > 300) interval = 60.0;

        else if (_trackDuration > 60) interval = 30.0;

        else if (_trackDuration < 30) interval = 5.0;


        for (double t = 0; t <= _trackDuration; t += interval)

        {

            double fraction = t / _trackDuration;

            double xPos = fraction * canvasWidth;


            var tickLine = new Avalonia.Controls.Shapes.Line

            {

                StartPoint = new Avalonia.Point(xPos, scaleCanvas.Bounds.Height - 4),

                EndPoint = new Avalonia.Point(xPos, scaleCanvas.Bounds.Height),

                Stroke = Avalonia.Media.Brushes.Gray,

                StrokeThickness = 1,

                IsHitTestVisible = false

            };

            scaleCanvas.Children.Add(tickLine);


            var tickLabel = new TextBlock

            {

                Text = TimeSpan.FromSeconds(t).ToString(@"m\:ss"),

                FontSize = Infrastructure.ThemeManager.ScaledFontSize(9),

                Foreground = Avalonia.Media.Brushes.Gray,

                IsHitTestVisible = false,

                RenderTransform = new Avalonia.Media.TranslateTransform(xPos - 10, -2)

            };

            scaleCanvas.Children.Add(tickLabel);

        }

    }


    private void DrawPhase3TimelineScale()

    {

        var scaleCanvas = this.FindControl<Canvas>("Phase3TimelineCanvas");

        if (scaleCanvas == null) return;


        double canvasWidth = scaleCanvas.Bounds.Width;

        if (canvasWidth <= 0) return;


        double videoDuration = GetPhase3VideoDurationSeconds();


        scaleCanvas.Children.Clear();


        double interval = 10.0;

        if (videoDuration > 300) interval = 60.0;

        else if (videoDuration > 60) interval = 30.0;

        else if (videoDuration < 30) interval = 5.0;


        for (double t = 0; t <= videoDuration; t += interval)

        {

            double fraction = t / videoDuration;

            double xPos = fraction * canvasWidth;


            var tickLine = new Avalonia.Controls.Shapes.Line

            {

                StartPoint = new Avalonia.Point(xPos, scaleCanvas.Bounds.Height - 4),

                EndPoint = new Avalonia.Point(xPos, scaleCanvas.Bounds.Height),

                Stroke = Avalonia.Media.Brushes.Gray,

                StrokeThickness = 1,

                IsHitTestVisible = false

            };

            scaleCanvas.Children.Add(tickLine);


            var tickLabel = new TextBlock

            {

                Text = TimeSpan.FromSeconds(t).ToString(@"m\:ss"),

                FontSize = Infrastructure.ThemeManager.ScaledFontSize(9),

                Foreground = Avalonia.Media.Brushes.Gray,

                IsHitTestVisible = false,

                RenderTransform = new Avalonia.Media.TranslateTransform(xPos - 10, -2)

            };

            scaleCanvas.Children.Add(tickLabel);
        }

        DrawPhase3MergerOverlays(canvasWidth);
    }

    private void DrawPhase3MergerOverlays(double canvasWidth)
    {
        if (!_isMergerMode) return;

        var scaleCanvas = this.FindControl<Canvas>("Phase3TimelineCanvas");
        var thumbCanvas = this.FindControl<Canvas>("ThumbnailOverlayCanvas");
        var waveCanvas = this.FindControl<Canvas>("WaveformOverlayCanvas");
        if (thumbCanvas != null) thumbCanvas.Children.Clear();
        if (waveCanvas != null) waveCanvas.Children.Clear();

        if (_mergerVideos != null && _mergerVideos.Count > 1 && _phase3ClipDurationsSec.Count > 1)
        {
            double totalClipDuration = _phase3ClipDurationsSec.Sum();
            if (totalClipDuration > 0.01)
            {
                double cursor = 0;
                for (int i = 1; i < _phase3ClipDurationsSec.Count; i++)
                {
                    cursor += _phase3ClipDurationsSec[i - 1];
                    double xPos = Math.Clamp((cursor / totalClipDuration) * canvasWidth, 0, canvasWidth);
                    if (thumbCanvas != null)
                        AddLaneBoundary(thumbCanvas, xPos, 60, Avalonia.Media.Brushes.White, 0.85);
                    if (waveCanvas != null)
                        AddLaneBoundary(waveCanvas, xPos, 60, Avalonia.Media.Brushes.White, 0.55);
                    if (scaleCanvas != null)
                    {
                        AddLaneBoundary(scaleCanvas, xPos, scaleCanvas.Bounds.Height, Avalonia.Media.Brushes.White, 0.70);
                        var label = new TextBlock
                        {
                            Text = $"CLIP {i + 1}",
                            FontSize = Infrastructure.ThemeManager.ScaledFontSize(8),
                            Foreground = Avalonia.Media.Brushes.White,
                            IsHitTestVisible = false,
                            RenderTransform = new Avalonia.Media.TranslateTransform(xPos + 4, 0)
                        };
                        scaleCanvas.Children.Add(label);
                    }
                }
            }
        }

        var songs = _pendingAutoFillMusicPaths;
        if (songs != null && songs.Count > 1 && waveCanvas != null)
        {
            double videoDuration = GetPhase3VideoDurationSeconds();
            double cursor = 0.0;
            for (int i = 1; i < songs.Count; i++)
            {
                var previousTrack = FindTrackByPath(songs[i - 1]);
                double offset = i == 1 ? _songStartSeconds : 0.0;
                cursor += Math.Max(0, (previousTrack?.DurationSec ?? 0.0) - offset);
                if (videoDuration <= 0.01 || cursor >= videoDuration) break;

                double xPos = Math.Clamp((cursor / videoDuration) * canvasWidth, 0, canvasWidth);
                AddLaneBoundary(waveCanvas, xPos, 60, Avalonia.Media.Brushes.LightGreen, 0.80);
            }
        }
    }

    private static void AddLaneBoundary(Canvas canvas, double xPos, double height, Avalonia.Media.IBrush brush, double opacity)
    {
        var border = new Avalonia.Controls.Border
        {
            Width = 2,
            Height = Math.Max(1, height),
            Background = brush,
            Opacity = opacity,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(border, Math.Max(0, xPos - 1));
        Canvas.SetTop(border, 0);
        canvas.Children.Add(border);
    }


    private static void EnsurePlayheadLine(
        Canvas canvas,
        ref Avalonia.Controls.Shapes.Line? line,
        Avalonia.Media.IBrush stroke,
        bool dashed)
    {
        if (line == null)
        {
            line = new Avalonia.Controls.Shapes.Line
            {
                Stroke = stroke,
                StrokeThickness = 2,
                IsHitTestVisible = false
            };

            if (dashed)
            {
                line.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(new[] { 2.0, 2.0 });
            }

            canvas.Children.Add(line);
        }
        else if (!canvas.Children.Contains(line))
        {
            canvas.Children.Add(line);
        }
    }

    private void UpdatePlayhead()

    {

        var canvas = this.FindControl<Canvas>("WaveformCanvas");

        var timelineCanvas = this.FindControl<Canvas>("TimelineMarkersCanvas");

        if (canvas == null) return;


        double offsetFraction = _songStartSeconds / Math.Max(0.1, _trackDuration);
        double offsetXPos = canvas.Bounds.Width * offsetFraction;

        double currentTime = _previewCurrentOffset;
        if (_currentStep == 3)
        {
            currentTime = _songStartSeconds + GetCurrentPhase3VideoRelativeSeconds();
        }
        else if (_isPreviewPlaying && _previewStartTime.HasValue)
        {
            currentTime += (DateTime.UtcNow - _previewStartTime.Value).TotalSeconds;
        }
        double playheadFraction = currentTime / Math.Max(0.1, _trackDuration);

        if (playheadFraction > 1.0) playheadFraction = 1.0;

        double playheadXPos = canvas.Bounds.Width * playheadFraction;


        EnsurePlayheadLine(
            canvas,
            ref _waveformOffsetLine,
            Avalonia.Media.Brushes.Gray,
            dashed: true);
        _waveformOffsetLine!.StartPoint = new Avalonia.Point(offsetXPos, 0);
        _waveformOffsetLine.EndPoint = new Avalonia.Point(offsetXPos, canvas.Bounds.Height);

        EnsurePlayheadLine(
            canvas,
            ref _waveformPlayheadLine,
            Avalonia.Media.Brushes.Red,
            dashed: false);
        _waveformPlayheadLine!.StartPoint = new Avalonia.Point(playheadXPos, 0);
        _waveformPlayheadLine.EndPoint = new Avalonia.Point(playheadXPos, canvas.Bounds.Height);


        if (timelineCanvas != null)

        {

            double txPos = timelineCanvas.Bounds.Width * playheadFraction;

            EnsurePlayheadLine(
                timelineCanvas,
                ref _timelinePlayheadLine,
                Avalonia.Media.Brushes.Red,
                dashed: false);
            _timelinePlayheadLine!.StartPoint = new Avalonia.Point(txPos, 0);
            _timelinePlayheadLine.EndPoint = new Avalonia.Point(txPos, timelineCanvas.Bounds.Height);

        }


        if (_currentStep == 3)

        {

            var p3Canvas = this.FindControl<Panel>("Phase3CaretCanvas");

            var p3Caret = this.FindControl<Border>("Phase3Caret");

            if (p3Canvas != null && p3Caret != null && p3Canvas.Bounds.Width > 0)

            {

                p3Caret.IsVisible = true;

                double videoDuration = GetPhase3VideoDurationSeconds();
                double vFraction = GetCurrentPhase3VideoRelativeSeconds() / videoDuration;
                if (vFraction < 0) vFraction = 0;
                if (vFraction > 1) vFraction = 1;


                double p3txPos = p3Canvas.Bounds.Width * vFraction;

                p3Caret.RenderTransform = new Avalonia.Media.TranslateTransform(p3txPos, 0);


                var p3CaretTextBorder = this.FindControl<Border>("Phase3CaretTextBorder");

                var p3CaretTimeText = this.FindControl<TextBlock>("Phase3CaretTimeText");

                if (p3CaretTextBorder != null && p3CaretTimeText != null)

                {

                    p3CaretTextBorder.IsVisible = true;

                    double currentVideoSec = videoDuration * vFraction;

                    p3CaretTimeText.Text = TimeSpan.FromSeconds(currentVideoSec).ToString(@"m\:ss\.ff");

                    p3CaretTextBorder.RenderTransform = new Avalonia.Media.TranslateTransform(p3txPos, 0);

                }

            }

        }

    }


    private void SetOffsetFromPointer(double x, double width)

    {

        if (width <= 0) return;

        double fraction = x / width;

        fraction = Math.Clamp(fraction, 0.0, 1.0);


        _songStartSeconds = Math.Clamp(_trackDuration * fraction, 0, Math.Max(0, _trackDuration - 0.01));

        var lbl = this.FindControl<TextBlock>("OffsetLabel");
        if (lbl != null) lbl.Text = $"Song begins at {FormatSeconds(_songStartSeconds)}";

        bool wasPlaying = _isPreviewPlaying;
        if (wasPlaying) StopPreview();

        _previewCurrentOffset = _songStartSeconds;

        if (wasPlaying) StartPreviewInternal(_previewCurrentOffset);
        else
        {
            UpdateFinalPlacementSummary();
            UpdateAutoFillQueuePreview();
            UpdateCoverageBar();
            UpdateProblemFlags();
            UpdatePlayhead();
        }
    }


    private void OnBackClicked(object? sender, RoutedEventArgs e)

    {

        if (_currentStep > 1)
        {
            if (_currentStep == 3)
            {
                StopPreview();
                CancelPhase3Load();
                DisposePhase3VideoHost();
                SetPhase3Status("");
                _previewCurrentOffset = _songStartSeconds;
            }
            else
            {
                StopPreview();
            }
            _currentStep--;
            UpdateStepVisibility();
            UpdateNextButtonState();
            UpdatePlayhead();
        }
    }


    private void OnFileDrop(object? sender, DragEventArgs e)

    {

        CancelMusicScan();

        var files = e.Data.GetFiles();

        if (files == null) return;


        var musicExts = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".m4a", ".aac", ".ogg" };

        var firstMusic = files.FirstOrDefault(f => musicExts.Contains(System.IO.Path.GetExtension(f.Name)));


        if (firstMusic != null)

        {

            string path = firstMusic.Path.LocalPath;

            var track = new MusicTrackItem

            {

                Name = Path.GetFileName(path),

                Title = Path.GetFileNameWithoutExtension(path),

                FilePath = path,

                DurationText = "Loading...",

                SizeText = "",

                LastModifiedTicks = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0,

                IsRecent = _recentMusicPaths.Contains(path)

            };


            _allTracks.Clear();

            _allTracks.Add(track);

            var searchBox = this.FindControl<TextBox>("MusicSearchBox");
            if (searchBox != null)
                searchBox.Text = string.Empty;
            ApplyTrackFilterAndSort();

            var listbox = this.FindControl<ListBox>("MusicListBox");

            if (listbox != null) listbox.SelectedIndex = 0;


            RuntimeLog.Info("MUSIC_WIZARD", $"File dropped: {Path.GetFileName(path)}");
            RuntimeLog.Debug("MUSIC_WIZARD", $"Dropped music path: {path}");

            _ = ProbeTrackInfoAsync(track);

            ShowToast("✔ Music file loaded!");

        }
        else
        {
            ShowToast("Drop an MP3, WAV, M4A, AAC, or OGG file.");
        }

        UpdateMusicEmptyState();

    }


    protected override async void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        CancelAudioAnalysis();
        CancelMusicScan();
        CancelPhase3Load();
        StopPreview();
        DisposePhase3VideoHost();

        if (_playheadTimer != null) { _playheadTimer.Stop(); _playheadTimer.Tick -= PlayheadTimer_Tick; _playheadTimer = null; }
        
        if (_isSafeToClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        this.Hide();
        
        FortniteVideoSoftware.App.WindowBoundsHelper.SaveBoundsSync(this, "MusicWizardBounds");
        _isSafeToClose = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(Close);

    }


    protected override void OnClosed(EventArgs e)

    {

        if (_playheadTimer != null) { _playheadTimer.Stop(); _playheadTimer.Tick -= PlayheadTimer_Tick; _playheadTimer = null; }
        FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged -= OnGlobalMasterVolumeChanged;
        CancelAudioAnalysis();
        CancelMusicScan();
        if (_lastWaveformFile != null && File.Exists(_lastWaveformFile))
        {
            try { File.Delete(_lastWaveformFile); } catch { }
        }
        foreach (var f in _lastPhase3ThumbFiles) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
        _lastPhase3ThumbFiles.Clear();
        DeleteTempFile(ref _lastPhase3WaveFile);
        StopPreview();
        DisposePhase3VideoHost();


        if (_audioIpcClient != null)

        {

            try { _audioIpcClient.Dispose(); } catch { }

            _audioIpcClient = null;

        }


        base.OnClosed(e);

    }


    private void LoadMusicDirectory()

    {

        string? targetDir = null;

        try

        {

            if (File.Exists(_paths.SessionStateFile))
            {
                var state = FortniteVideoSoftware.Core.Infrastructure.AtomicJsonFile.ReadObject(_paths.SessionStateFile);
                if (state != null && state.TryGetPropertyValue("CustomMusicDirectory", out var node) && node != null)
                {
                    targetDir = node.ToString();
                }
            }

        }

        catch { }


        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))

        {

            targetDir = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

        }


        _ = ScanDirectoryForMusicAsync(targetDir);

    }


    private async Task ScanDirectoryForMusicAsync(string directoryPath)

    {

        CancelMusicScan();
        var cts = new CancellationTokenSource();
        _musicScanCts = cts;
        int scanVersion = _musicScanVersion;

        _allTracks.Clear();
        AvailableTracks.Clear();
        ApplyTrackFilterAndSort();

        try
        {
            var tracks = await Task.Run(() =>
            {
                var found = new System.Collections.Generic.List<MusicTrackItem>();
                if (!Directory.Exists(directoryPath))
                    return found;

                var exts = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".m4a", ".aac", ".ogg" };

                foreach (string f in Directory.EnumerateFiles(directoryPath))
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (!exts.Contains(Path.GetExtension(f)))
                        continue;

                    var fileInfo = new FileInfo(f);
                    found.Add(new MusicTrackItem
                    {
                        Name = Path.GetFileName(f),
                        Title = Path.GetFileNameWithoutExtension(f),
                        FilePath = f,
                        DurationText = "Loading...",
                        SizeText = "",
                        LastModifiedTicks = fileInfo.LastWriteTimeUtc.Ticks,
                        IsRecent = _recentMusicPaths.Contains(f)
                    });
                }

                return found;
            }, cts.Token);

            if (cts.Token.IsCancellationRequested || scanVersion != _musicScanVersion)
                return;

            _allTracks.Clear();
            foreach (var item in tracks)
                _allTracks.Add(item);

            ApplyTrackFilterAndSort();

            foreach (var item in tracks)
                _ = ProbeTrackInfoAsync(item, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Failed to scan music directory: {ex.Message}");
            ApplyTrackFilterAndSort();
        }

    }

    private void UpdateMusicEmptyState()
    {
        var emptyText = this.FindControl<TextBlock>("EmptyMusicListText");
        if (emptyText != null)
        {
            emptyText.Text = _allTracks.Count == 0
                ? "No music files found. Change folder or drop an audio file here."
                : "No songs match the current search.";
            emptyText.IsVisible = AvailableTracks.Count == 0;
        }
    }


    /// <summary>

    /// Improvement #6: Probes duration and file size asynchronously for display in the list.

    /// </summary>

    private async Task ProbeTrackInfoAsync(MusicTrackItem item, CancellationToken cancellationToken = default)

    {

        try

        {

            await _trackProbeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileInfo = new FileInfo(item.FilePath);
                double sizeMb = fileInfo.Length / (1024.0 * 1024.0);
                string sizeText = sizeMb >= 1.0 ? $"{sizeMb:F1} MB" : $"{fileInfo.Length / 1024.0:F0} KB";

                var ffprobePath = ResolveFfprobePath();
                var prober = new FortniteVideoSoftware.Core.Media.MediaProber(ffprobePath, item.FilePath);
                double duration = await prober.GetDurationAsync().ConfigureAwait(false);

                string durationText;
                double durationSec = 0.0;
                if (duration > 0)
                {
                    durationSec = duration;
                    var ts = TimeSpan.FromSeconds(duration);
                    durationText = ts.TotalHours >= 1
                        ? ts.ToString(@"h\:mm\:ss")
                        : ts.ToString(@"m\:ss");
                }
                else
                {
                    durationText = "—";
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    item.SizeText = sizeText;
                    item.DurationSec = durationSec;
                    item.DurationText = durationText;

                    if (_musicSortMode == "Shortest" || _musicSortMode == "Longest")
                    {
                        ApplyTrackFilterAndSort();
                    }
                    else
                    {
                        var idx = AvailableTracks.IndexOf(item);

                        if (idx >= 0)
                        {
                            var tmp = AvailableTracks[idx];

                            AvailableTracks[idx] = tmp;
                        }
                    }

                    UpdateAutoFillQueuePreview();
                    UpdateCoverageBar();
                    UpdateProblemFlags();

                });
            }
            finally
            {
                _trackProbeGate.Release();
            }

        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)

        {

            RuntimeLog.Fail("MUSIC_WIZARD", $"Failed to probe {item.Name}: {ex.Message}");

            Dispatcher.UIThread.Post(() => item.DurationText = "—");

        }

    }


    private void ShowToast(string message)

    {

        Dispatcher.UIThread.Post(async () =>

        {


            var grid = this.FindControl<Grid>("Step1Panel")?.Parent as Panel;

            if (grid == null) return;


            var toast = new Avalonia.Controls.Border

            {

                Background = Avalonia.Media.Brush.Parse("#1e293b"),

                BorderBrush = Avalonia.Media.Brush.Parse("#3b82f6"),

                BorderThickness = new Avalonia.Thickness(1),

                CornerRadius = new Avalonia.CornerRadius(6),

                Padding = new Avalonia.Thickness(15, 8),

                Child = new TextBlock

                {

                    Text = message,

                    Foreground = Avalonia.Media.Brushes.White,

                    FontSize = Infrastructure.ThemeManager.ScaledFontSize(11),

                    FontWeight = Avalonia.Media.FontWeight.SemiBold

                },

                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,

                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,

                Opacity = 0

            };


            var hostPanel = this.FindControl<Panel>("Step1Panel")?.Parent as Panel;

            if (hostPanel == null) return;


            int topZIndex = hostPanel.Children.Count == 0

                ? 999

                : Math.Max(999, hostPanel.Children.Max(child => child.ZIndex) + 1);

            toast.ZIndex = topZIndex;

            hostPanel.Children.Add(toast);


            for (double o = 0; o <= 1; o += 0.1)

            {

                toast.Opacity = o;

                await Task.Delay(16);

            }

            toast.Opacity = 1;


            await Task.Delay(2500);


            for (double o = 1; o >= 0; o -= 0.1)

            {

                toast.Opacity = o;

                await Task.Delay(16);

            }


            hostPanel.Children.Remove(toast);

        });
    }

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<Border>("TitleBarBorder");
        if (titleBar != null)
        {
            titleBar.IsHitTestVisible = true;
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
}


