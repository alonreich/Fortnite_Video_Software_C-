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


public class MusicTrackItem

{

    public string Name { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public string DurationText { get; set; } = "";

    public string SizeText { get; set; } = "";

    public double DurationSec { get; set; } = 0.0;

}


public class MusicWizardResult
{
    public string MusicFilePath { get; set; } = string.Empty;
    public System.Collections.Generic.List<string> MusicFilePaths { get; set; } = new();
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
    private CancellationTokenSource? _phase3LoadCts;
    private int _phase3LoadVersion;
    private bool _phase3Ready;
    private double _phase3VideoDurationSec = 60.0;
    private string? _lastPhase3ThumbFile;
    private string? _lastPhase3WaveFile;
    private System.Collections.Generic.List<string>? _mergerVideos = null;
    private bool _isMergerMode = false;

    private FortniteVideoSoftware.App.MpvVideoView? WizardVideoHost => this.FindControl<Avalonia.Controls.Border>("VideoHostBorder")?.Child as FortniteVideoSoftware.App.MpvVideoView;


    public MusicWizardWindow()

    {
        InitializeComponent();
        FortniteVideoSoftware.App.WindowBoundsHelper.LoadBoundsSync(this, "MusicWizardBounds");
        _playheadTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _playheadTimer.Tick += (s, e) =>
        {
            if (_isPreviewPlaying)
            {
                EnforcePhase3PreviewEnd();
                UpdatePlayhead();
            }
        };
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

    public MusicWizardWindow(string videoPath, double trimStartMs, double trimEndMs) : this()
    {
        _videoPath = videoPath;
        _trimStartMs = trimStartMs;
        _trimEndMs = trimEndMs;
        _playheadTimer?.Start();
        SharedInit();
    }

    private void OnGlobalMasterVolumeChanged(int volume)
    {
        var videoVolSlider = this.FindControl<Avalonia.Controls.Slider>("VideoVolSlider");
        var musicVolSlider = this.FindControl<Avalonia.Controls.Slider>("MusicVolSlider");
        
        double vBase = (videoVolSlider?.Value ?? 100.0) / 100.0;
        double mBase = (musicVolSlider?.Value ?? 100.0) / 100.0;
        
        if (WizardVideoHost?.IpcClient != null)
            _ = WizardVideoHost.IpcClient.SetPropertyAsync("volume", (volume * vBase).ToString("0"));
        if (_audioIpcClient != null)
            _ = _audioIpcClient.SetPropertyAsync("volume", (volume * mBase).ToString("0"));
    }

    private void SharedInit()
    {
        FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged += OnGlobalMasterVolumeChanged;

        this.Closing += (s, e) => {
            WindowBoundsHelper.SaveBoundsSync(this, "MusicWizardBounds");
        };

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


        AddHandler(DragDrop.DropEvent, OnFileDrop);

        var loopCheck = this.FindControl<CheckBox>("LoopMusicCheckBox");
        if (loopCheck != null)
        {
            loopCheck.IsCheckedChanged += (s, e) => UpdateCoverageBar();
        }

        var autoFillBtn = this.FindControl<Button>("AutoFillSongsBtn");
        if (autoFillBtn != null)
        {
            autoFillBtn.Click += (s, e) =>
            {
                if (_selectedTrack != null)
                {
                    _pendingAutoFillMusicPaths.Clear();
                    _pendingAutoFillMusicPaths.Add(_selectedTrack.FilePath);

                    double targetDuration = GetPhase3VideoDurationSeconds();
                    double coveredDuration = Math.Max(0, _selectedTrack.DurationSec - _songStartSeconds);
                    foreach (var track in AvailableTracks.Where(t => t.FilePath != _selectedTrack.FilePath))
                    {
                        _pendingAutoFillMusicPaths.Add(track.FilePath);
                        coveredDuration += Math.Max(1.0, track.DurationSec);
                        if (coveredDuration >= targetDuration)
                        {
                            break;
                        }
                    }

                    autoFillBtn.Content = $"Auto-Filled {_pendingAutoFillMusicPaths.Count} Songs";
                    ShowToast($"Auto-filled {_pendingAutoFillMusicPaths.Count} songs.");
                    DrawPhase3TimelineScale();
                }
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


                    ScanDirectoryForMusic(selectedFolderPath);

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
                    double targetTime = (_trimStartMs / 1000.0) + videoRelativeSec;

                    var wizardVideoHost = WizardVideoHost;
                    if (wizardVideoHost?.IpcClient != null)

                    {

                        _ = wizardVideoHost.IpcClient.SendCommandAsync("seek", targetTime.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");

                    }


                    bool wasPlaying = _isPreviewPlaying;
                    StopPreview();

                    _previewCurrentOffset = _songStartSeconds + videoRelativeSec;

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

                                                        _ = wizardVideoHost.IpcClient.SetPropertyAsync("volume", videoVolSlider.Value.ToString("0"));

                        SaveWizardVolumes();

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

                                                _ = _audioIpcClient.SetPropertyAsync("volume", musicVolSlider.Value.ToString("0"));
                        
                        SaveWizardVolumes();

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


        if (_currentStep == 3)
        {
            this.Width = 1200;
            this.Height = 850;
        }
        else

        {

            this.Width = 900;

            this.Height = 700;
        }

        UpdateFinalPlacementSummary();
        UpdateStepProgress();
        UpdatePreviewControlsState();

        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (this.Content is Avalonia.Controls.Control contentControl)
            {
                contentControl.InvalidateMeasure();
                contentControl.InvalidateArrange();
            }
            this.InvalidateMeasure();
            this.InvalidateArrange();
            this.UpdateLayout();
            
            double oldW = this.Width;
            this.Width = oldW + 1;
            await Task.Delay(50);
            this.Width = oldW;
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }


    private void OnTrackSelected(MusicTrackItem? track)
    {
        _selectedTrack = track;
        _pendingAutoFillMusicPaths.Clear();
        var autoFillBtn = this.FindControl<Button>("AutoFillSongsBtn");
        if (autoFillBtn != null)
        {
            autoFillBtn.Content = "Auto-Fill Remaining Time with Random Songs";
        }
        UpdateNextButtonState();
        UpdateFinalPlacementSummary();
        UpdatePreviewControlsState();
        UpdateCoverageBar();
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

        _pendingAutoFillMusicPaths.Clear();
        _previewCurrentOffset = _songStartSeconds;
        var lbl = this.FindControl<TextBlock>("OffsetLabel");
        if (lbl != null) lbl.Text = $"Song begins at {FormatSeconds(_songStartSeconds)}";
        UpdatePlayhead();
        UpdateFinalPlacementSummary();
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
        double targetTime = (_trimStartMs / 1000.0) + videoRelativeSec;

        var wizardVideoHost = WizardVideoHost;
        if (wizardVideoHost?.IpcClient != null)
        {
            _ = wizardVideoHost.IpcClient.SendCommandAsync("seek", targetTime.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
        }

        bool wasPlaying = _isPreviewPlaying;
        StopPreview();
        _previewCurrentOffset = _songStartSeconds + videoRelativeSec;
        if (wasPlaying) StartPreviewInternal(_previewCurrentOffset);
        else UpdatePlayhead();
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
            double timelineEndSec = timelineStartSec + GetPhase3VideoDurationSeconds();

            Result = new MusicWizardResult
            {
                MusicFilePath = _selectedTrack?.FilePath ?? "",
                MusicFilePaths = _pendingAutoFillMusicPaths.Count > 0
                    ? new System.Collections.Generic.List<string>(_pendingAutoFillMusicPaths)
                    : new System.Collections.Generic.List<string> { _selectedTrack?.FilePath ?? "" },
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

            RuntimeLog.Success("MUSIC_WIZARD", $"Wizard completed. Track: {Result.MusicFilePath}, SongStart: {Result.OffsetSeconds:F2}s, Timeline: {Result.TimelineStartSeconds:F2}-{Result.TimelineEndSeconds:F2}s, Ducking: {Result.EnableDucking}, Carving: {Result.EnableCarving}, VideoVol: {Result.VideoVolume}, MusicVol: {Result.MusicVolume}");
            _isSafeToClose = true;

            Close();

            return;

        }


        UpdateStepVisibility();

        UpdateNextButtonState();

    }


    private async Task<string?> GenerateThumbnailsStripAsync(string ffmpegPath, string videoPath, double startSec, double durationSec, CancellationToken cancellationToken)
    {
        string? tempPng = null;
        Process? process = null;
        try
        {
            tempPng = Path.Combine(_paths.TempDirectory, $"fvs_thumb_{Guid.NewGuid():N}.png");
            if (durationSec <= 0) durationSec = 10;
            
            double fps = 16.0 / durationSec;

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-y -hide_banner -loglevel error -ss {startSec} -t {durationSec} -i \"{videoPath}\" -vf \"fps={fps.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)},scale=-1:60,tile=15x1\" -frames:v 1 \"{tempPng}\"",

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
            var thumbLane = this.FindControl<Avalonia.Controls.Image>("ThumbnailLaneImage");
            var waveLane = this.FindControl<Avalonia.Controls.Image>("WaveformLaneImage");
            if (thumbLane != null) thumbLane.Source = null;
            if (waveLane != null) waveLane.Source = null;

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
                    await wizardVideoHost.IpcClient.SetPropertyAsync("volume", videoVolSlider.Value.ToString("0"));
            }
            SetLoadingOverlay("Phase3VideoLoadingOverlay", false);

            cancellationToken.ThrowIfCancellationRequested();

            if (thumbLane != null && !string.IsNullOrEmpty(_videoPath))
            {
                double start = _trimStartMs / 1000.0;
                var ffmpeg = ResolveFfmpegPath();
                string? thumbPath = await GenerateThumbnailsStripAsync(ffmpeg, _videoPath, start, _phase3VideoDurationSec, cancellationToken);
                if (loadVersion != _phase3LoadVersion || _currentStep != 3) return;
                if (thumbPath != null)
                {
                    try
                    {
                        using var fs = File.OpenRead(thumbPath);
                        thumbLane.Source = new Avalonia.Media.Imaging.Bitmap(fs);
                        DeleteTempFile(ref _lastPhase3ThumbFile);
                        _lastPhase3ThumbFile = thumbPath;
                    }
                    catch { }
                }
            }
            SetLoadingOverlay("Phase3ThumbLoadingOverlay", false);

            cancellationToken.ThrowIfCancellationRequested();

            if (waveLane != null && _selectedTrack != null && !string.IsNullOrEmpty(_selectedTrack.FilePath))
            {
                double audibleMusicDuration = GetAudibleMusicDurationSeconds();
                waveLane.Width = double.NaN;
                waveLane.Margin = new Avalonia.Thickness(0, 0, 0, 0);

                if (audibleMusicDuration > 0.01)
                {
                    var ffmpeg = ResolveFfmpegPath();
                    string? wavePath = await FortniteVideoSoftware.Core.Media.WaveformGenerator.GenerateWaveformImageAsync(
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
        return Math.Max(0.1, duration);
    }

    private double GetAudibleMusicDurationSeconds()
    {
        double remainingSong = Math.Max(0, _trackDuration - _songStartSeconds);
        return Math.Min(GetPhase3VideoDurationSeconds(), remainingSong);
    }

    private double GetCurrentPhase3VideoRelativeSeconds()
    {
        double fallback = Math.Clamp(_previewCurrentOffset - _songStartSeconds, 0, GetPhase3VideoDurationSeconds());
        var wizardVideoHost = WizardVideoHost;
        if (wizardVideoHost?.IpcClient == null)
            return fallback;

        double trimStartSec = _trimStartMs / 1000.0;
        double currentTime = wizardVideoHost.IpcClient.CurrentTime;
        if (currentTime >= trimStartSec - 0.05)
            return Math.Clamp(currentTime - trimStartSec, 0, GetPhase3VideoDurationSeconds());

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
            double endTime = (_trimStartMs / 1000.0) + videoDuration;
            _ = wizardVideoHost.IpcClient.SetPropertyAsync("time-pos", endTime.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        StopPreview();
        UpdatePlayhead();
    }

    private void SaveWizardVolumes()
    {
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

        double ratio = GetAudibleMusicDurationSeconds() / videoDuration;
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
        double audibleMusic = GetAudibleMusicDurationSeconds();
        string endText = audibleMusic >= GetPhase3VideoDurationSeconds() - 0.01
            ? "Music is trimmed at the video end."
            : $"Only {FormatSeconds(audibleMusic)} of music remains after this song point.";

        label.Text = $"Song starts at {FormatSeconds(_songStartSeconds)}. Video range is {FormatSeconds(trimStartSec)} to {FormatSeconds(trimEndSec)}. {endText}";
    }

    private void UpdateCoverageBar()
    {
        if (!_isMergerMode) return;

        double videoDuration = GetPhase3VideoDurationSeconds();
        double audibleMusic = GetAudibleMusicDurationSeconds();

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
            var wizardVideoHost = WizardVideoHost;
            if (wizardVideoHost?.IpcClient != null)
            {
                double target = (_trimStartMs / 1000.0) + videoRelative;
                _ = wizardVideoHost.IpcClient.SendCommandAsync("seek", target.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
            }
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


        if (_currentStep == 3)
        {
            var wizardVideoHost = WizardVideoHost;
            if (wizardVideoHost?.IpcClient != null)
            {
                double videoSeekTime = Math.Clamp(startOffset - _songStartSeconds, 0, GetPhase3VideoDurationSeconds());
                double videoStartPos = (_trimStartMs / 1000.0) + videoSeekTime;

                _ = wizardVideoHost.IpcClient.SetPropertyAsync("time-pos", videoStartPos.ToString(System.Globalization.CultureInfo.InvariantCulture));
                _ = wizardVideoHost.IpcClient.SetPropertyAsync("pause", "no");
            }

        }


        try

        {

            if (_audioIpcClient == null)

            {

                _audioIpcClient = new FortniteVideoSoftware.Core.Media.MpvIpcClient();

                await _audioIpcClient.StartAudioOnlyAsync(ResolveMpvPath());

            }


            double audioStartOffset = Math.Clamp(startOffset, 0, _trackDuration);
            if (_currentStep == 3 && audioStartOffset >= _trackDuration - 0.01)
            {
                if (_audioIpcClient != null)
                    await _audioIpcClient.SetPropertyAsync("pause", "yes");
                return;
            }

            string targetPath = _selectedTrack!.FilePath.Replace("\\", "/");
            if (_lastLoadedTrackPath != targetPath)
            {
                await _audioIpcClient.SetPropertyAsync("start", audioStartOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await _audioIpcClient.SendCommandAsync("loadfile", targetPath, "replace");
                _lastLoadedTrackPath = targetPath;
            }
            else
            {
                await _audioIpcClient.SendCommandAsync("seek", audioStartOffset.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
            }

            var musicVolSlider = this.FindControl<Slider>("MusicVolSlider");
            double musicVol = (musicVolSlider?.Value ?? 100.0);
            await _audioIpcClient.SetPropertyDoubleAsync("volume", musicVol);
            await _audioIpcClient.SetPropertyAsync("pause", "no");
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


        var waveformImage = this.FindControl<Image>("WaveformImage");

        var loadingText = this.FindControl<TextBlock>("WaveformLoadingText");


        if (waveformImage == null || loadingText == null) return;


        loadingText.IsVisible = true;
        loadingText.Text = "Generating Waveform...";

        waveformImage.Source = null;


        var ffmpegPath = ResolveFfmpegPath();


        string? pngFile = await FortniteVideoSoftware.Core.Media.WaveformGenerator.GenerateWaveformImageAsync(ffmpegPath, filePath);


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

                FontSize = 9,

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

                FontSize = 9,

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

        var thumbCanvas = this.FindControl<Canvas>("ThumbnailOverlayCanvas");
        var waveCanvas = this.FindControl<Canvas>("WaveformOverlayCanvas");
        if (thumbCanvas != null) thumbCanvas.Children.Clear();
        if (waveCanvas != null) waveCanvas.Children.Clear();

        if (_mergerVideos != null && _mergerVideos.Count > 1 && thumbCanvas != null)
        {
            for (int i = 1; i < _mergerVideos.Count; i++)
            {
                double xPos = (canvasWidth / _mergerVideos.Count) * i;
                var border = new Avalonia.Controls.Border { Width = 3, Height = 60, Background = Avalonia.Media.Brushes.Black };
                Canvas.SetLeft(border, xPos);
                Canvas.SetTop(border, 0);
                thumbCanvas.Children.Add(border);
            }
        }

        var songs = _pendingAutoFillMusicPaths;
        if (songs != null && songs.Count > 1 && waveCanvas != null)
        {
            for (int i = 1; i < songs.Count; i++)
            {
                double xPos = (canvasWidth / songs.Count) * i;
                var border = new Avalonia.Controls.Border { Width = 3, Height = 60, Background = Avalonia.Media.Brushes.Black };
                Canvas.SetLeft(border, xPos);
                Canvas.SetTop(border, 0);
                waveCanvas.Children.Add(border);
            }
        }
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

            var p3Canvas = this.FindControl<Canvas>("Phase3CaretCanvas");

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

                FilePath = path,

                DurationText = "Loading...",

                SizeText = ""

            };


            AvailableTracks.Clear();

            AvailableTracks.Add(track);

            var listbox = this.FindControl<ListBox>("MusicListBox");

            if (listbox != null) listbox.SelectedIndex = 0;


            RuntimeLog.Info("MUSIC_WIZARD", $"File dropped: {path}");

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
        CancelPhase3Load();
        StopPreview();
        DisposePhase3VideoHost();

        if (_isSafeToClose)

        {

            base.OnClosing(e);

            return;

        }


        e.Cancel = true;
        FortniteVideoSoftware.App.Infrastructure.WindowManager.SaveAll();


        this.Hide();


        try

        {


        }

        catch (Exception ex)

        {

            RuntimeLog.Fail("MUSIC_WIZARD", $"Error saving state during close: {ex.Message}");

        }

        finally

        {


            _isSafeToClose = true;

            this.Close();

        }

    }


    protected override void OnClosed(EventArgs e)

    {

        FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged -= OnGlobalMasterVolumeChanged;
        if (_lastWaveformFile != null && File.Exists(_lastWaveformFile))
        {
            try { File.Delete(_lastWaveformFile); } catch { }
        }
        DeleteTempFile(ref _lastPhase3ThumbFile);
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


        ScanDirectoryForMusic(targetDir);

    }


    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)

    {

        base.OnPointerPressed(e);

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)

        {

            try { BeginMoveDrag(e); } catch { }

        }

    }


    private void ScanDirectoryForMusic(string directoryPath)

    {

        AvailableTracks.Clear();

        if (Directory.Exists(directoryPath))

        {

            var exts = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".m4a", ".aac", ".ogg" };

            var files = Directory.GetFiles(directoryPath).Where(f => exts.Contains(Path.GetExtension(f)));

            foreach (var f in files)

            {

                var item = new MusicTrackItem

                {

                    Name = Path.GetFileName(f),

                    FilePath = f,

                    DurationText = "Loading...",

                    SizeText = ""

                };

                AvailableTracks.Add(item);

                _ = ProbeTrackInfoAsync(item);

            }

        }

        UpdateMusicEmptyState();

    }

    private void UpdateMusicEmptyState()
    {
        var emptyText = this.FindControl<TextBlock>("EmptyMusicListText");
        if (emptyText != null)
        {
            emptyText.IsVisible = AvailableTracks.Count == 0;
        }
    }


    /// <summary>

    /// Improvement #6: Probes duration and file size asynchronously for display in the list.

    /// </summary>

    private async Task ProbeTrackInfoAsync(MusicTrackItem item)

    {

        await Task.Run(() =>

        {

            try

            {


                var fileInfo = new FileInfo(item.FilePath);

                double sizeMb = fileInfo.Length / (1024.0 * 1024.0);

                item.SizeText = sizeMb >= 1.0 ? $"{sizeMb:F1} MB" : $"{fileInfo.Length / 1024.0:F0} KB";


                var ffprobePath = ResolveFfprobePath();

                var prober = new FortniteVideoSoftware.Core.Media.MediaProber(ffprobePath, item.FilePath);

                double duration = prober.GetDurationAsync().GetAwaiter().GetResult();

                if (duration > 0)

                {

                    item.DurationSec = duration;

                    var ts = TimeSpan.FromSeconds(duration);

                    item.DurationText = ts.TotalHours >= 1

                        ? ts.ToString(@"h\:mm\:ss")

                        : ts.ToString(@"m\:ss");

                }

                else

                {

                    item.DurationText = "—";

                }


                Dispatcher.UIThread.Post(() =>

                {

                    var idx = AvailableTracks.IndexOf(item);

                    if (idx >= 0)

                    {


                        var tmp = AvailableTracks[idx];

                        AvailableTracks[idx] = tmp;

                    }

                });

            }

            catch (Exception ex)

            {

                RuntimeLog.Fail("MUSIC_WIZARD", $"Failed to probe {item.Name}: {ex.Message}");

                item.DurationText = "—";

            }

        });

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

                    FontSize = 11,

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


