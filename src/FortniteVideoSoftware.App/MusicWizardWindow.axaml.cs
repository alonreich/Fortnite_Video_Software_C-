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
using System.Threading.Tasks;

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
    public double OffsetSeconds { get; set; } = 0.0;
    public double TimelineStartSeconds { get; set; } = 0.0;
    public double TimelineEndSeconds { get; set; } = 0.0;
    public bool EnableDucking { get; set; } = true;
    public bool EnableCarving { get; set; } = true;
    public double VideoVolume { get; set; } = 1.0;
    public double MusicVolume { get; set; } = 1.0;
    public double MusicDurationSeconds { get; set; } = 0.0;
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
    private Process? _previewProcess;
    private bool _isPreviewPlaying = false;
    private double _previewCurrentOffset = 0.0;
    private DateTime? _previewStartTime = null;
    private double _musicWizardOffset = 0.0;
    private Avalonia.Threading.DispatcherTimer? _playheadTimer;

    public MusicWizardWindow()
    {
        InitializeComponent();

        _playheadTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _playheadTimer.Tick += (s, e) => { if (_isPreviewPlaying) UpdatePlayhead(); };
        _playheadTimer.Start();

        this.Loaded += async (s, e) => {
            await WindowBoundsHelper.LoadBoundsAsync(this, "MusicWizardBounds");
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

        // Improvement #9: Drag-and-drop support
        AddHandler(DragDrop.DropEvent, OnFileDrop);

        var changeFolderBtn = this.FindControl<Button>("ChangeFolderBtn");
        if (changeFolderBtn != null)
        {
            changeFolderBtn.Click += async (s, e) =>
            {
                var musicPath = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                var musicFolder = await this.StorageProvider.TryGetFolderFromPathAsync(new Uri(musicPath));
                
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
                        System.Text.Json.Nodes.JsonObject state;
                        if (File.Exists(_paths.SessionStateFile))
                            state = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(File.ReadAllText(_paths.SessionStateFile)) ?? new System.Text.Json.Nodes.JsonObject();
                        else
                            state = new System.Text.Json.Nodes.JsonObject();

                        state["CustomMusicDirectory"] = selectedFolderPath;
                        File.WriteAllText(_paths.SessionStateFile, state.ToJsonString());
                    }
                    catch { }

                    ScanDirectoryForMusic(selectedFolderPath);
                }
            };
        }

        var selectFileBtn = this.FindControl<Button>("SelectFileBtn");
        if (selectFileBtn != null)
        {
            selectFileBtn.Click += async (s, e) =>
            {
                var musicPath = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                var musicFolder = await this.StorageProvider.TryGetFolderFromPathAsync(new Uri(musicPath));
                
                var result = await this.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Select specific MP3 file",
                    SuggestedStartLocation = musicFolder,
                    FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("MP3 Music Files") { Patterns = new[] { "*.mp3", "*.wav", "*.m4a", "*.aac", "*.ogg" } } },
                    AllowMultiple = false
                });
                
                if (result != null && result.Count > 0)
                {
                    string selectedFilePath = result[0].Path.LocalPath;
                    string selectedFolderPath = System.IO.Path.GetDirectoryName(selectedFilePath) ?? selectedFilePath;
                    
                    try
                    {
                        System.Text.Json.Nodes.JsonObject state;
                        if (File.Exists(_paths.SessionStateFile))
                            state = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(File.ReadAllText(_paths.SessionStateFile)) ?? new System.Text.Json.Nodes.JsonObject();
                        else
                            state = new System.Text.Json.Nodes.JsonObject();

                        state["CustomMusicDirectory"] = selectedFolderPath;
                        File.WriteAllText(_paths.SessionStateFile, state.ToJsonString());
                    }
                    catch { }

                    ScanDirectoryForMusic(selectedFolderPath);
                    
                    var track = AvailableTracks.FirstOrDefault(t => t.FilePath.Equals(selectedFilePath, StringComparison.OrdinalIgnoreCase));
                    if (track != null)
                    {
                        var musicListBox = this.FindControl<ListBox>("MusicListBox");
                        if (musicListBox != null)
                        {
                            musicListBox.SelectedItem = track;
                        }
                    }
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
        }

        var videoVolSlider = this.FindControl<Slider>("VideoVolSlider");
        if (videoVolSlider != null)
        {
            videoVolSlider.PropertyChanged += (s, e) =>
            {
                if (e.Property == Slider.ValueProperty)
                {
                    var lbl = this.FindControl<TextBlock>("VideoVolLabel");
                    if (lbl != null) lbl.Text = $"Game Volume: {videoVolSlider.Value:0}%";
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
                }
            };
        }

        // Improvement #2: Fix the broken PLAY button — actual audio preview
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
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // ============================================================
    // Improvement #1: Step Progress Bar updates
    // ============================================================
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
                // Completed
                dots[i].Item1!.Background = Avalonia.Media.Brush.Parse("#22c55e");
                dots[i].Item2!.Text = "✓";
                dots[i].Item2!.Foreground = Avalonia.Media.Brushes.White;
                dots[i].Item3!.Foreground = Avalonia.Media.Brush.Parse("#94a3b8");
            }
            else if (i == _currentStep - 1)
            {
                // Current
                dots[i].Item1!.Background = Avalonia.Media.Brush.Parse("#3b82f6");
                dots[i].Item2!.Text = (i + 1).ToString();
                dots[i].Item2!.Foreground = Avalonia.Media.Brushes.White;
                dots[i].Item3!.Foreground = Avalonia.Media.Brush.Parse("#60a5fa");
                dots[i].Item3!.FontWeight = Avalonia.Media.FontWeight.Bold;
            }
            else
            {
                // Future
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
        this.FindControl<Grid>("Step2Panel")!.IsVisible = _currentStep == 2;
        this.FindControl<Grid>("Step3Panel")!.IsVisible = _currentStep == 3;

        var backBtn = this.FindControl<Button>("BackBtn");
        if (backBtn != null) backBtn.IsEnabled = _currentStep > 1;

        var nextBtn = this.FindControl<Button>("NextBtn");
        if (nextBtn != null)
        {
            nextBtn.Content = _currentStep == 3 ? "APPLY" : "NEXT";
        }

        UpdateStepProgress();
    }

    // ============================================================
    // Improvement #7: Visual feedback when no track selected
    // ============================================================
    private void OnTrackSelected(MusicTrackItem? track)
    {
        _selectedTrack = track;
        UpdateNextButtonState();
    }

    private void UpdateNextButtonState()
    {
        var nextBtn = this.FindControl<Button>("NextBtn");
        if (nextBtn == null) return;

        // On Step 1, disable NEXT if no track is selected
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
        else
        {
            nextBtn.IsEnabled = true;
            nextBtn.Opacity = 1.0;
            ToolTip.SetTip(nextBtn, _currentStep == 3 ? "Apply music settings to your video" : "Proceed to the next step");
        }
    }

    // ============================================================
    // Improvement #3: Selected track banner + #5: Merged steps
    // ============================================================
    private async void OnNextClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentStep == 1)
        {
            if (_selectedTrack == null)
            {
                // Improvement #7: Visual feedback (shouldn't happen since button is disabled)
                ShowToast("⚠ Please select a music track first!");
                return;
            }

            // Probe duration
            var ffprobePath = ResolveFfprobePath();
            var prober = new FortniteVideoSoftware.Core.Media.MediaProber(ffprobePath, _selectedTrack.FilePath);
            double duration = await prober.GetDurationAsync();
            _trackDuration = Math.Max(1.0, duration);

            _musicWizardOffset = 0;
            _previewCurrentOffset = 0;
            var lbl = this.FindControl<TextBlock>("OffsetLabel");
            if (lbl != null) lbl.Text = $"{_musicWizardOffset:F1}s";
            
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                DrawTimelineScale();
                UpdatePlayhead();
            });

            // Improvement #3: Show selected track name
            var selectedLabel = this.FindControl<TextBlock>("SelectedTrackLabel");
            if (selectedLabel != null) selectedLabel.Text = _selectedTrack.Name;

            // Render waveform (now on Step 2)
            _ = RenderWaveformAsync(_selectedTrack.FilePath);

            _currentStep = 2;
        }
        else if (_currentStep == 2)
        {
            // Populate Step 3 review summary (Improvement #10)
            PopulateSummary();

            StopPreview();
            _currentStep = 3;
        }
        else if (_currentStep == 3)
        {
            // Finalize — create result and close
            var duckingCheck = this.FindControl<CheckBox>("DuckingCheckBox");
            var carvingCheck = this.FindControl<CheckBox>("CarvingCheckBox");
            var videoVolSlider = this.FindControl<Slider>("VideoVolSlider");
            var musicVolSlider = this.FindControl<Slider>("MusicVolSlider");

            Result = new MusicWizardResult
            {
                MusicFilePath = _selectedTrack?.FilePath ?? "",
                OffsetSeconds = _musicWizardOffset,
                EnableDucking = duckingCheck?.IsChecked ?? true,
                EnableCarving = carvingCheck?.IsChecked ?? true,
                VideoVolume = (videoVolSlider?.Value ?? 100.0) / 100.0,
                MusicVolume = (musicVolSlider?.Value ?? 100.0) / 100.0,
                MusicDurationSeconds = _selectedTrack?.DurationSec ?? 0.0
            };

            RuntimeLog.Success("MUSIC_WIZARD", $"Wizard completed. Track: {Result.MusicFilePath}, Offset: {Result.OffsetSeconds:F2}s, Ducking: {Result.EnableDucking}, Carving: {Result.EnableCarving}, VideoVol: {Result.VideoVolume}, MusicVol: {Result.MusicVolume}");
            _isSafeToClose = true;
            Close();
            return;
        }

        UpdateStepVisibility();
        UpdateNextButtonState();
    }

    /// <summary>
    /// Improvement #10: Populate the final review summary card.
    /// </summary>
    private void PopulateSummary()
    {
        var summaryTrack = this.FindControl<TextBlock>("SummaryTrack");
        if (summaryTrack != null) summaryTrack.Text = _selectedTrack?.Name ?? "—";

        var summaryOffset = this.FindControl<TextBlock>("SummaryOffset");
        if (summaryOffset != null) summaryOffset.Text = $"{_musicWizardOffset:F1}s";

        var videoVolSlider = this.FindControl<Slider>("VideoVolSlider");
        var musicVolSlider = this.FindControl<Slider>("MusicVolSlider");
        var summaryVolume = this.FindControl<TextBlock>("SummaryVolume");
        if (summaryVolume != null)
            summaryVolume.Text = $"Music {(musicVolSlider?.Value ?? 100):0}% / Game {(videoVolSlider?.Value ?? 100):0}%";

        var duckingCheck = this.FindControl<CheckBox>("DuckingCheckBox");
        var carvingCheck = this.FindControl<CheckBox>("CarvingCheckBox");
        var summaryAutoMix = this.FindControl<TextBlock>("SummaryAutoMix");
        if (summaryAutoMix != null)
        {
            bool autoMix = (duckingCheck?.IsChecked ?? true) || (carvingCheck?.IsChecked ?? true);
            summaryAutoMix.Text = autoMix ? "ON" : "OFF";
            summaryAutoMix.Foreground = autoMix ? Avalonia.Media.Brush.Parse("#2ecc71") : Avalonia.Media.Brushes.Gray;
        }
    }

    private string ResolveFfprobePath()
    {
        var ffprobePath = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "backend", "ffprobe.exe");
        if (!File.Exists(ffprobePath)) ffprobePath = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "ffprobe.exe");
        if (!File.Exists(ffprobePath)) ffprobePath = "ffprobe.exe";
        return ffprobePath;
    }

    private string ResolveFfmpegPath()
    {
        var ffmpegPath = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "backend", "ffmpeg.exe");
        if (!File.Exists(ffmpegPath)) ffmpegPath = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "ffmpeg.exe");
        if (!File.Exists(ffmpegPath)) ffmpegPath = "ffmpeg.exe";
        return ffmpegPath;
    }

    // ============================================================
    // Improvement #2: PLAY button — real audio preview
    // ============================================================
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

        double startOffset = _previewCurrentOffset;

        StartPreviewInternal(startOffset);
    }

    private void SkipPreview(double offsetSeconds)
    {
        if (_selectedTrack == null) return;
        
        bool wasPlaying = _isPreviewPlaying;
        StopPreview(); // Accurately calculates the exact current position and stops playback
        
        _previewCurrentOffset += offsetSeconds;
        if (_previewCurrentOffset < 0) _previewCurrentOffset = 0;
        if (_previewCurrentOffset > _selectedTrack.DurationSec) _previewCurrentOffset = _selectedTrack.DurationSec;
        
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

        Process? currentProcess = null;
        try
        {
            var ffplayPath = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "backend", "ffplay.exe");
            if (!File.Exists(ffplayPath)) ffplayPath = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "ffplay.exe");
            if (!File.Exists(ffplayPath)) ffplayPath = "ffplay.exe";

            var musicVolSlider = this.FindControl<Slider>("MusicVolSlider");
            double musicVol = (musicVolSlider?.Value ?? 100.0);

            var psi = new ProcessStartInfo
            {
                FileName = ffplayPath,
                Arguments = $"-nodisp -autoexit -t 10 -ss {startOffset.ToString(System.Globalization.CultureInfo.InvariantCulture)} -volume {musicVol.ToString(System.Globalization.CultureInfo.InvariantCulture)} \"{_selectedTrack!.FilePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _previewProcess = Process.Start(psi);
            currentProcess = _previewProcess;
            if (currentProcess != null)
            {
                await currentProcess.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Preview failed: {ex.Message}");
            ShowToast("⚠ Preview playback failed");
        }
        finally
        {
            // Only stop if this specific process is still the active one
            if (_previewProcess == currentProcess)
            {
                StopPreview();
            }
        }
    }

    private void StopPreview()
    {
        if (_previewStartTime.HasValue)
        {
            _previewCurrentOffset += (DateTime.UtcNow - _previewStartTime.Value).TotalSeconds;
            if (_selectedTrack != null && _previewCurrentOffset > _selectedTrack.DurationSec)
                _previewCurrentOffset = _selectedTrack.DurationSec;
            _previewStartTime = null;
        }

        if (_previewProcess != null && !_previewProcess.HasExited)
        {
            try { _previewProcess.Kill(entireProcessTree: true); } catch { }
        }
        _previewProcess = null;
        _isPreviewPlaying = false;

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

    private void UpdatePlayhead()
    {
        var canvas = this.FindControl<Canvas>("WaveformCanvas");
        var timelineCanvas = this.FindControl<Canvas>("TimelineMarkersCanvas");
        if (canvas == null) return;

        double offsetTime = _musicWizardOffset < 0 ? -_musicWizardOffset : 0;
        double offsetFraction = offsetTime / Math.Max(0.1, _trackDuration);
        double offsetXPos = canvas.Bounds.Width * offsetFraction;

        double currentTime = _previewCurrentOffset;
        if (_isPreviewPlaying && _previewStartTime.HasValue)
        {
            currentTime += (DateTime.UtcNow - _previewStartTime.Value).TotalSeconds;
        }
        double playheadFraction = currentTime / Math.Max(0.1, _trackDuration);
        if (playheadFraction > 1.0) playheadFraction = 1.0;
        double playheadXPos = canvas.Bounds.Width * playheadFraction;

        canvas.Children.Clear();
        
        // Draw static offset line
        var offsetLine = new Avalonia.Controls.Shapes.Line
        {
            StartPoint = new Avalonia.Point(offsetXPos, 0),
            EndPoint = new Avalonia.Point(offsetXPos, canvas.Bounds.Height),
            Stroke = Avalonia.Media.Brushes.Gray,
            StrokeThickness = 2,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(new[] { 2.0, 2.0 }),
            IsHitTestVisible = false
        };
        canvas.Children.Add(offsetLine);

        // Draw moving playhead line
        var playheadLine = new Avalonia.Controls.Shapes.Line
        {
            StartPoint = new Avalonia.Point(playheadXPos, 0),
            EndPoint = new Avalonia.Point(playheadXPos, canvas.Bounds.Height),
            Stroke = Avalonia.Media.Brushes.Red,
            StrokeThickness = 2,
            IsHitTestVisible = false
        };
        canvas.Children.Add(playheadLine);

        if (timelineCanvas != null)
        {
            timelineCanvas.Children.Clear();
            double txPos = timelineCanvas.Bounds.Width * playheadFraction;
            var tLine = new Avalonia.Controls.Shapes.Line
            {
                StartPoint = new Avalonia.Point(txPos, 0),
                EndPoint = new Avalonia.Point(txPos, timelineCanvas.Bounds.Height),
                Stroke = Avalonia.Media.Brushes.Red,
                StrokeThickness = 2,
                IsHitTestVisible = false
            };
            timelineCanvas.Children.Add(tLine);
        }
    }

    private void SetOffsetFromPointer(double x, double width)
    {
        if (width <= 0) return;
        double fraction = x / width;
        fraction = Math.Clamp(fraction, 0.0, 1.0);

        _musicWizardOffset = -(_trackDuration * fraction);
        
        var lbl = this.FindControl<TextBlock>("OffsetLabel");
        if (lbl != null) lbl.Text = $"{_musicWizardOffset:F1}s";
        
        bool wasPlaying = _isPreviewPlaying;
        if (wasPlaying) StopPreview();
        
        _previewCurrentOffset = _trackDuration * fraction;
        
        if (wasPlaying) StartPreviewInternal(_previewCurrentOffset);
        else UpdatePlayhead();
    }

    private void OnBackClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentStep > 1)
        {
            StopPreview();
            _currentStep--;
            UpdateStepVisibility();
            UpdateNextButtonState();
        }
    }

    // ============================================================
    // Improvement #9: Drag-and-drop support
    // ============================================================
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
    }

    protected override async void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        StopPreview();

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

        try
        {
            // Perform the heavy Mutex locking and file I/O ASYNCHRONOUSLY
            await WindowBoundsHelper.SaveBoundsAsync(this, "MusicWizardBounds");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Error saving state during close: {ex.Message}");
        }
        finally
        {
            // Mark as safe and programmatically re-trigger the close
            _isSafeToClose = true;
            this.Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_lastWaveformFile != null && File.Exists(_lastWaveformFile))
        {
            try { File.Delete(_lastWaveformFile); } catch { }
        }
        StopPreview();
        base.OnClosed(e);
    }

    private void LoadMusicDirectory()
    {
        string? targetDir = null;
        try
        {
            if (File.Exists(_paths.SessionStateFile))
            {
                string json = File.ReadAllText(_paths.SessionStateFile);
                var state = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(json);
                targetDir = state?["CustomMusicDirectory"]?.ToString();
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

    // ============================================================
    // Improvement #6: Show track duration & file size
    // ============================================================
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
                // File size
                var fileInfo = new FileInfo(item.FilePath);
                double sizeMb = fileInfo.Length / (1024.0 * 1024.0);
                item.SizeText = sizeMb >= 1.0 ? $"{sizeMb:F1} MB" : $"{fileInfo.Length / 1024.0:F0} KB";

                // Duration via ffprobe
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

                // Refresh the list item display
                Dispatcher.UIThread.Post(() =>
                {
                    var idx = AvailableTracks.IndexOf(item);
                    if (idx >= 0)
                    {
                        // Swap to trigger collection changed notification
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
            // Simple inline toast at the top of the wizard
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

            // Use the main panel as the overlay host
            var hostPanel = this.FindControl<Panel>("Step1Panel")?.Parent as Panel;
            if (hostPanel == null) return;

            int topZIndex = hostPanel.Children.Count == 0
                ? 999
                : Math.Max(999, hostPanel.Children.Max(child => child.ZIndex) + 1);
            toast.ZIndex = topZIndex;
            hostPanel.Children.Add(toast);

            // Fade in
            for (double o = 0; o <= 1; o += 0.1)
            {
                toast.Opacity = o;
                await Task.Delay(16);
            }
            toast.Opacity = 1;

            await Task.Delay(2500);

            // Fade out
            for (double o = 1; o >= 0; o -= 0.1)
            {
                toast.Opacity = o;
                await Task.Delay(16);
            }

            hostPanel.Children.Remove(toast);
        });
    }
}
