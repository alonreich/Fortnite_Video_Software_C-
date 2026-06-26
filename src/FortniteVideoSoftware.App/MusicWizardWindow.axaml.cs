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
    private bool _isSafeToClose = false;
    private string? _lastWaveformFile;
    private double _trackDuration = 100.0;
    private bool _isDraggingWaveform;
    private MusicTrackItem? _selectedTrack;
    private Process? _previewProcess;
    private bool _isPreviewPlaying = false;

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

        var listbox = this.FindControl<ListBox>("MusicListBox");
        if (listbox != null)
        {
            listbox.ItemsSource = AvailableTracks;
            listbox.SelectionChanged += (s, e) => OnTrackSelected(listbox.SelectedItem as MusicTrackItem);
            LoadMusicDirectory();
        }

        // Improvement #9: Drag-and-drop support
        AddHandler(DragDrop.DropEvent, OnFileDrop);

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

            var offsetSlider = this.FindControl<Slider>("OffsetSlider");
            if (offsetSlider != null)
            {
                offsetSlider.Minimum = -_trackDuration;
                offsetSlider.Maximum = _trackDuration;
                offsetSlider.Value = 0;
            }

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
            var offsetSlider = this.FindControl<Slider>("OffsetSlider");
            var duckingCheck = this.FindControl<CheckBox>("DuckingCheckBox");
            var carvingCheck = this.FindControl<CheckBox>("CarvingCheckBox");
            var videoVolSlider = this.FindControl<Slider>("VideoVolSlider");
            var musicVolSlider = this.FindControl<Slider>("MusicVolSlider");

            Result = new MusicWizardResult
            {
                MusicFilePath = _selectedTrack?.FilePath ?? "",
                OffsetSeconds = offsetSlider?.Value ?? 0.0,
                EnableDucking = duckingCheck?.IsChecked ?? true,
                EnableCarving = carvingCheck?.IsChecked ?? true,
                VideoVolume = (videoVolSlider?.Value ?? 100.0) / 100.0,
                MusicVolume = (musicVolSlider?.Value ?? 100.0) / 100.0
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

        var offsetSlider = this.FindControl<Slider>("OffsetSlider");
        var summaryOffset = this.FindControl<TextBlock>("SummaryOffset");
        if (summaryOffset != null) summaryOffset.Text = $"{offsetSlider?.Value ?? 0:F1}s";

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
    private async void TogglePreview()
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

        var playBtn = this.FindControl<Button>("PlayBtn");
        if (playBtn != null)
        {
            playBtn.Content = "STOP PREVIEW";
            playBtn.Classes.Remove("Primary");
            playBtn.Classes.Add("Danger");
        }
        _isPreviewPlaying = true;

        try
        {
            // Play a 10-second preview of the music using ffplay (simpler than MPV for a quick audio clip)
            var ffplayPath = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "backend", "ffplay.exe");
            if (!File.Exists(ffplayPath)) ffplayPath = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "ffplay.exe");
            if (!File.Exists(ffplayPath)) ffplayPath = "ffplay.exe";

            var offsetSlider = this.FindControl<Slider>("OffsetSlider");
            double startOffset = Math.Max(0, offsetSlider?.Value ?? 0);

            // Launch ffplay with the music file (muted window, just audio)
            var psi = new ProcessStartInfo
            {
                FileName = ffplayPath,
                Arguments = $"-nodisp -autoexit -t 10 -ss {startOffset.ToString(System.Globalization.CultureInfo.InvariantCulture)} \"{_selectedTrack.FilePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _previewProcess = Process.Start(psi);
            if (_previewProcess != null)
            {
                await _previewProcess.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Preview failed: {ex.Message}");
            ShowToast("⚠ Preview playback failed");
        }
        finally
        {
            StopPreview();
        }
    }

    private void StopPreview()
    {
        if (_previewProcess != null && !_previewProcess.HasExited)
        {
            try { _previewProcess.Kill(entireProcessTree: true); } catch { }
        }
        _previewProcess = null;
        _isPreviewPlaying = false;

        var playBtn = this.FindControl<Button>("PlayBtn");
        if (playBtn != null)
        {
            playBtn.Content = "PLAY PREVIEW";
            playBtn.Classes.Remove("Danger");
            playBtn.Classes.Add("Primary");
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

            hostPanel.Children.Add(toast);
            toast.ZIndex = 999;

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
