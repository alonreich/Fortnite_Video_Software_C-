using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FortniteVideoSoftware.Core.Infrastructure;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace FortniteVideoSoftware.App;

public class MusicTrackItem
{
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
}

public class MusicWizardResult
{
    public string MusicFilePath { get; set; } = string.Empty;
    public double OffsetSeconds { get; set; } = 0.0;
    public bool EnableDucking { get; set; } = true;
    public bool EnableCarving { get; set; } = true;
    public double VideoVolume { get; set; } = 1.0;
    public double MusicVolume { get; set; } = 1.0;
}

public partial class MusicWizardWindow : Window
{
    public ObservableCollection<MusicTrackItem> AvailableTracks { get; } = new();
    public MusicWizardResult? Result { get; private set; }

    private int _currentStep = 1;
    private readonly ApplicationPaths _paths = ApplicationPaths.CreateDefault();

    public MusicWizardWindow()
    {
        InitializeComponent();

        // Smart OS Theme Detection
        if (Avalonia.Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Light)
        {
            var mainBorder = this.FindControl<Avalonia.Controls.Border>("MainBorder");
            var titleBarBorder = this.FindControl<Avalonia.Controls.Border>("TitleBarBorder");
            
            if (mainBorder != null) mainBorder.BorderBrush = Avalonia.Media.Brush.Parse("#334155");
            if (titleBarBorder != null) titleBarBorder.Background = Avalonia.Media.Brush.Parse("#0f172a");
        }

        this.Loaded += async (s, e) => {
            await WindowBoundsHelper.LoadBoundsAsync(this, "MusicWizardBounds");
        };

        this.Closing += (s, e) => {
            WindowBoundsHelper.SaveBoundsSync(this, "MusicWizardBounds");
        };

        var listbox = this.FindControl<ListBox>("MusicListBox");
        if (listbox != null)
        {
            listbox.ItemsSource = AvailableTracks;
            LoadMusicDirectory();
        }

        var changeFolderBtn = this.FindControl<Button>("ChangeFolderBtn");
        if (changeFolderBtn != null)
        {
            changeFolderBtn.Click += async (s, e) =>
            {
                var result = await this.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Select Music Folder"
                });
                if (result != null && result.Count > 0)
                {
                    string selectedPath = result[0].Path.LocalPath;
                    try
                    {
                        System.Text.Json.Nodes.JsonObject state;
                        if (File.Exists(_paths.SessionStateFile))
                            state = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(File.ReadAllText(_paths.SessionStateFile)) ?? new System.Text.Json.Nodes.JsonObject();
                        else
                            state = new System.Text.Json.Nodes.JsonObject();

                        state["CustomMusicDirectory"] = selectedPath;
                        File.WriteAllText(_paths.SessionStateFile, state.ToJsonString());
                    }
                    catch { }

                    ScanDirectoryForMusic(selectedPath);
                }
            };
        }

        var offsetSlider = this.FindControl<Slider>("OffsetSlider");
        if (offsetSlider != null)
        {
            offsetSlider.PropertyChanged += (s, e) =>
            {
                if (e.Property == Slider.ValueProperty)
                {
                    var lbl = this.FindControl<TextBlock>("OffsetLabel");
                    if (lbl != null) lbl.Text = $"{offsetSlider.Value:F1}s";
                    UpdatePlayhead();
                }
            };
        }

        var canvas = this.FindControl<Canvas>("WaveformCanvas");
        if (canvas != null)
        {
            canvas.PointerPressed += (s, e) =>
            {
                var pt = e.GetCurrentPoint(canvas);
                if (pt.Properties.IsLeftButtonPressed)
                {
                    _isDraggingWaveform = true;
                    SetOffsetFromPointer(pt.Position.X, canvas.Bounds.Width);
                }
            };

            canvas.PointerMoved += (s, e) =>
            {
                if (_isDraggingWaveform)
                {
                    var pt = e.GetCurrentPoint(canvas);
                    SetOffsetFromPointer(pt.Position.X, canvas.Bounds.Width);
                }
            };

            canvas.PointerReleased += (s, e) =>
            {
                _isDraggingWaveform = false;
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
                    if (lbl != null) lbl.Text = $"Video Volume: {videoVolSlider.Value:0}%";
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
            Close();
        };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
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
            nextBtn.Content = _currentStep == 3 ? "FINISH" : "NEXT";
            nextBtn.Background = _currentStep == 3 ? Avalonia.Media.Brushes.Green : Avalonia.Media.Brushes.RoyalBlue;
        }
    }

    private async void OnNextClicked(object? sender, RoutedEventArgs e)
    {
        var listbox = this.FindControl<ListBox>("MusicListBox");
        var selectedTrack = listbox?.SelectedItem as MusicTrackItem;

        if (_currentStep == 1)
        {
            if (selectedTrack == null)
            {
                RuntimeLog.Fail("MUSIC_WIZARD", "No track selected in Step 1.");
                return;
            }

            // Probe duration
            var ffprobePath = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "backend", "ffprobe.exe");
            if (!File.Exists(ffprobePath)) ffprobePath = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "ffprobe.exe");
            if (!File.Exists(ffprobePath)) ffprobePath = "ffprobe.exe";
            
            var prober = new FortniteVideoSoftware.Core.Media.MediaProber(ffprobePath, selectedTrack.FilePath);
            double duration = await prober.GetDurationAsync();
            _trackDuration = Math.Max(1.0, duration);

            var offsetSlider = this.FindControl<Slider>("OffsetSlider");
            if (offsetSlider != null)
            {
                offsetSlider.Minimum = -_trackDuration;
                offsetSlider.Maximum = _trackDuration;
                offsetSlider.Value = 0;
            }

            _currentStep = 2;
        }
        else if (_currentStep == 2)
        {
            _currentStep = 3;
            _ = RenderWaveformAsync(selectedTrack?.FilePath);
        }
        else if (_currentStep == 3)
        {
            var offsetSlider = this.FindControl<Slider>("OffsetSlider");
            var duckingCheck = this.FindControl<CheckBox>("DuckingCheckBox");
            var carvingCheck = this.FindControl<CheckBox>("CarvingCheckBox");
            var videoVolSlider = this.FindControl<Slider>("VideoVolSlider");
            var musicVolSlider = this.FindControl<Slider>("MusicVolSlider");

            Result = new MusicWizardResult
            {
                MusicFilePath = selectedTrack?.FilePath ?? "",
                OffsetSeconds = offsetSlider?.Value ?? 0.0,
                EnableDucking = duckingCheck?.IsChecked ?? true,
                EnableCarving = carvingCheck?.IsChecked ?? true,
                VideoVolume = (videoVolSlider?.Value ?? 100.0) / 100.0,
                MusicVolume = (musicVolSlider?.Value ?? 100.0) / 100.0
            };

            RuntimeLog.Success("MUSIC_WIZARD", $"Wizard completed. Track: {Result.MusicFilePath}, Offset: {Result.OffsetSeconds:F2}s, Ducking: {Result.EnableDucking}, Carving: {Result.EnableCarving}, VideoVol: {Result.VideoVolume}, MusicVol: {Result.MusicVolume}");
            Close();
            return;
        }

        UpdateStepVisibility();
    }

    private string? _lastWaveformFile;
    private double _trackDuration = 100.0;
    private bool _isDraggingWaveform;

    private async Task RenderWaveformAsync(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;

        var waveformImage = this.FindControl<Image>("WaveformImage");
        var loadingText = this.FindControl<TextBlock>("WaveformLoadingText");
        
        if (waveformImage == null || loadingText == null) return;

        loadingText.IsVisible = true;
        waveformImage.Source = null;

        var ffmpegPath = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "backend", "ffmpeg.exe");
        if (!File.Exists(ffmpegPath)) ffmpegPath = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "ffmpeg.exe");
        if (!File.Exists(ffmpegPath)) ffmpegPath = "ffmpeg.exe";

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

    private void UpdatePlayhead()
    {
        var canvas = this.FindControl<Canvas>("WaveformCanvas");
        var offsetSlider = this.FindControl<Slider>("OffsetSlider");
        if (canvas == null || offsetSlider == null) return;

        double songStartTime = offsetSlider.Value < 0 ? -offsetSlider.Value : 0;
        double fraction = songStartTime / Math.Max(0.1, _trackDuration);
        double xPos = canvas.Bounds.Width * fraction;

        canvas.Children.Clear();
        var line = new Avalonia.Controls.Shapes.Line
        {
            StartPoint = new Avalonia.Point(xPos, 0),
            EndPoint = new Avalonia.Point(xPos, canvas.Bounds.Height),
            Stroke = Avalonia.Media.Brushes.Red,
            StrokeThickness = 2
        };
        canvas.Children.Add(line);
    }

    private void SetOffsetFromPointer(double x, double width)
    {
        if (width <= 0) return;
        double fraction = x / width;
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        
        var offsetSlider = this.FindControl<Slider>("OffsetSlider");
        if (offsetSlider != null)
        {
            offsetSlider.Value = -(_trackDuration * fraction);
        }
    }

    private void OnBackClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentStep > 1)
        {
            _currentStep--;
            UpdateStepVisibility();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_lastWaveformFile != null && File.Exists(_lastWaveformFile))
        {
            try { File.Delete(_lastWaveformFile); } catch { }
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

    private void ScanDirectoryForMusic(string directoryPath)
    {
        AvailableTracks.Clear();
        if (Directory.Exists(directoryPath))
        {
            var exts = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".m4a", ".aac", ".ogg" };
            var files = Directory.GetFiles(directoryPath).Where(f => exts.Contains(Path.GetExtension(f)));
            foreach (var f in files)
            {
                AvailableTracks.Add(new MusicTrackItem { Name = Path.GetFileName(f), FilePath = f });
            }
        }
    }
}

