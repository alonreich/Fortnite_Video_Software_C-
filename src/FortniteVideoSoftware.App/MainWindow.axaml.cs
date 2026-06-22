using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FortniteVideoSoftware.App.Controls;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Media;
using System.Diagnostics;
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
    private bool _isSeeking = false;
    private double? _nextSeekTarget = null;

    private double _trimStartMs = 0;
    private double _trimEndMs = 0;
    private double _thumbnailPosMs = 0;
    private bool _draggingStartMarker = false;
    private bool _draggingEndMarker = false;
    private MusicWizardResult? _musicWizardResult;
    private DispatcherTimer? _playbackTimer;
    private bool _isTimerUpdatingSlider = false;
    private readonly ApplicationPaths _paths = ApplicationPaths.CreateDefault();

    // Granular speed segments set via the Granular Speed Editor dialog
    private readonly System.Collections.Generic.List<SpeedSegment> _speedSegments = new();
    private double _baseSpeed = 1.0;
    private bool _isTimelineDrawn = false;
    private string _loadedVideoPath = string.Empty;

    public MainWindow()
    {
        RuntimeLog.Info("UI", "Initializing MainWindow");
        InitializeComponent();
        
        // Smart OS Theme Detection
        if (Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Styling.ThemeVariant.Light)
        {
            var mainBorder = this.FindControl<Avalonia.Controls.Border>("MainBorder");
            var titleBarBorder = this.FindControl<Avalonia.Controls.Border>("TitleBarBorder");
            var titleBarText = this.FindControl<Avalonia.Controls.TextBlock>("TitleBarText");
            
            if (mainBorder != null) mainBorder.BorderBrush = Avalonia.Media.Brush.Parse("#334155");
            if (titleBarBorder != null) titleBarBorder.Background = Avalonia.Media.Brush.Parse("#0f172a");
            if (titleBarText != null) titleBarText.Foreground = Avalonia.Media.Brushes.White;
        }

        this.Loaded += (s, e) => InitializeMpv();
        
        SettingsManager.Load();
        
        var settingsBtn = this.FindControl<Button>("SettingsOverlayBtn");
        if (settingsBtn != null)
        {
            settingsBtn.Click += async (s, e) => 
            {
                var settingsWin = new FortniteVideoSoftware.App.Controls.SettingsWindow();
                bool changed = await settingsWin.ShowDialog<bool>(this);
                if (changed) UpdateTooltips();
            };
        }
        
        UpdateTooltips();
        
        LoadWindowState();

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();

        var canvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineMarkersCanvas");
        if (canvas != null)
        {
            canvas.SizeChanged += (s, e) => UpdateTimelineMarkers();
        }

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
                // processButton state is restored inside the worker.Finished callback
            };
        }

        // Removed success actions from main window

        // ---- Granular Speed button ----
        var granularButton = this.FindControl<Button>("GranularButton");
        if (granularButton != null)
        {
            granularButton.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked GRANULAR SPEED button.");

                if (string.IsNullOrWhiteSpace(_loadedVideoPath))
                {
                    // No video loaded yet — show feedback toast
                    ShowToast("Load a video first!", true);
                    PlayUiSound();
                    return;
                }

                // Pause main player while dialog is open
                if (_videoHost?.IpcClient != null)
                    _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");

                var editor = new GranularSpeedEditorWindow(
                    _loadedVideoPath,
                    _speedSegments,
                    _baseSpeed);

                await editor.ShowDialog(this);

                if (editor.Accepted)
                {
                    _speedSegments.Clear();
                    _speedSegments.AddRange(editor.ResultSegments);
                    _baseSpeed = editor.ResultBaseSpeed;

                    int count = _speedSegments.Count;
                    RuntimeLog.Info("UI", $"Granular editor closed. {count} segment(s) saved. Base speed={_baseSpeed:F2}x");

                    // Update button label to reflect active state
                    granularButton.Content = count > 0
                        ? $"GRANULAR ({count} seg)"
                        : "GRANULAR SPEED";

                    ShowToast(count > 0
                        ? $"✔ {count} speed segment{(count == 1 ? "" : "s")} saved"
                        : "Granular segments cleared");
                }
            };
        }

        var uploadButton = this.FindControl<Button>("UploadButton");
        if (uploadButton != null)
        {
            uploadButton.Click += OnUploadVideoClicked;
        }
        
        var centerUploadButton = this.FindControl<Button>("CenterUploadButton");
        if (centerUploadButton != null)
        {
            centerUploadButton.Click += OnUploadVideoClicked;
        }

        var videoMergerButton = this.FindControl<Button>("VideoMergerButton");
        if (videoMergerButton != null)
        {
            videoMergerButton.Click += (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked VIDEO MERGER button.");
                string exePath = Environment.ProcessPath ?? "FortniteVideoSoftware.App.exe";
                Process.Start(new ProcessStartInfo(exePath, "--merger") { UseShellExecute = true });
                Environment.Exit(0);
            };
        }

        var cropSettingsButton = this.FindControl<Button>("CropSettingsButton");
        if (cropSettingsButton != null)
        {
            cropSettingsButton.Click += (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked CROP SETTINGS button.");
                string exePath = Environment.ProcessPath ?? "FortniteVideoSoftware.App.exe";
                Process.Start(new ProcessStartInfo(exePath, "--crop-tool") { UseShellExecute = true });
                Environment.Exit(0);
            };
        }

        var playPauseButton = this.FindControl<Button>("PlayPauseButton");
        if (playPauseButton != null)
        {
            playPauseButton.Click += (s, e) => 
            {
                RuntimeLog.Info("UI", "User toggled Play/Pause state.");
                // Toggle MPV pause state
                if (_videoHost?.IpcClient != null) _ = _videoHost.IpcClient.SetPropertyAsync("pause", _videoHost.IpcClient.IsPaused ? "no" : "yes");
            };
        }
        
        var setThumbnailButton = this.FindControl<Button>("SetThumbnailButton");
        if (setThumbnailButton != null)
        {
            setThumbnailButton.Click += (s, e) => 
            {
                RuntimeLog.Info("UI", $"User clicked SET THUMBNAIL at {TimeSpan.FromMilliseconds(_thumbnailPosMs):hh\\:mm\\:ss\\.ff}.");
                double time = GetCurrentMpvTime();
                _thumbnailPosMs = time * 1000;
                
                PlayUiSound();
                ShowPopupBadge(setThumbnailButton, $"✔ {TimeSpan.FromSeconds(time):mm\\:ss\\.ff}", Avalonia.Media.Brushes.DeepSkyBlue);
                ShowTimelineGlow(_thumbnailPosMs, Avalonia.Media.Brushes.DeepSkyBlue);
                UpdateTimelineMarkers();
                UpdateEstimatedQuality();
            };
        }

        var markStartButton = this.FindControl<Button>("MarkStartButton");
        if (markStartButton != null)
        {
            markStartButton.Click += (s, e) => 
            {
                RuntimeLog.Info("UI", $"User clicked MARK START at {TimeSpan.FromMilliseconds(_trimStartMs):hh\\:mm\\:ss\\.ff}.");
                double time = GetCurrentMpvTime();
                _trimStartMs = time * 1000;
                markStartButton.Content = $"START: {TimeSpan.FromSeconds(time):hh\\:mm\\:ss}";
                
                PlayUiSound();
                ShowPopupBadge(markStartButton, $"✔ {TimeSpan.FromSeconds(time):mm\\:ss\\.ff}", Avalonia.Media.Brushes.LimeGreen);
                ShowTimelineGlow(_trimStartMs, Avalonia.Media.Brushes.LimeGreen);
                UpdateTimelineMarkers();
                UpdateEstimatedQuality();
            };
        }

        var markEndButton = this.FindControl<Button>("MarkEndButton");
        if (markEndButton != null)
        {
            markEndButton.Click += (s, e) => 
            {
                RuntimeLog.Info("UI", $"User clicked MARK END at {TimeSpan.FromMilliseconds(_trimEndMs):hh\\:mm\\:ss\\.ff}.");
                double time = GetCurrentMpvTime();
                _trimEndMs = time * 1000;
                markEndButton.Content = $"END: {TimeSpan.FromSeconds(time):hh\\:mm\\:ss}";
                
                // Smart feature: Set start to 0 if not set yet
                if (_trimStartMs <= 0)
                {
                    _trimStartMs = 0;
                    if (markStartButton != null)
                        markStartButton.Content = $"START: {TimeSpan.FromSeconds(0):hh\\:mm\\:ss}";
                }

                // Pause the video automatically
                if (_videoHost?.IpcClient != null)
                {
                    _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");
                }

                PlayUiSound();
                ShowPopupBadge(markEndButton, $"✔ {TimeSpan.FromSeconds(time):mm\\:ss\\.ff}", Avalonia.Media.Brushes.Tomato);
                ShowTimelineGlow(_trimEndMs, Avalonia.Media.Brushes.Tomato);
                UpdateTimelineMarkers();
                UpdateEstimatedQuality();
            };
        }
        
        var timelineSlider = this.FindControl<Slider>("TimelineSlider");
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

        var mainSpeedSlider = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("MainSpeedSlider");
        if (mainSpeedSlider != null)
        {
            mainSpeedSlider.SetRange(1, 40);
            var speedLabels = new System.Collections.Generic.List<string>();
            for (int i = 1; i <= 40; i++) speedLabels.Add((i / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "x");
            mainSpeedSlider.SetLabels(speedLabels);
            mainSpeedSlider.Value = 11; // 1.1x
            mainSpeedSlider.ValueChanged += (s, e) => 
            {
                _baseSpeed = e / 10.0;
                if (_videoHost?.IpcClient != null)
                    _ = _videoHost?.IpcClient?.SetPropertyAsync("speed", _baseSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                UpdateEstimatedQuality();
            };
        }

        var volumeSlider = this.FindControl<Slider>("VolumeSlider");
        var volumeBadgeText = this.FindControl<TextBlock>("VolumeBadgeText");
        if (volumeSlider != null && volumeBadgeText != null)
        {
            volumeSlider.PropertyChanged += (s, e) =>
            {
                if (e.Property == Slider.ValueProperty && e.NewValue != null)
                {
                    int vol = System.Convert.ToInt32(e.NewValue);
                    volumeBadgeText.Text = $"{vol}%";
                    if (_videoHost?.IpcClient != null)
                    {
                        _ = _videoHost?.IpcClient?.SetPropertyAsync("volume", vol.ToString());
                    }
                }
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
            qualitySlider.Value = 7; // Default 40MB
            qualitySlider.ValueChanged += (s, v) => 
            {
                var qs = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("QualitySlider");
                if (qs != null) Avalonia.Controls.ToolTip.SetTip(qs, $"Target Size: {labels[v]}");
                UpdateEstimatedQuality();
            };
        }
        
        
        var addMusicButton = this.FindControl<Button>("AddMusicButton");
        if (addMusicButton != null)
        {
            addMusicButton.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked ADD MUSIC button (launching Wizard).");
                var wizard = new MusicWizardWindow();
                await wizard.ShowDialog(this);
                
                if (wizard.Result != null)
                {
                    _musicWizardResult = wizard.Result;
                    RuntimeLog.Info("UI", $"User added music via wizard: {_musicWizardResult.MusicFilePath}, ducking={_musicWizardResult.EnableDucking}");
                    addMusicButton.Content = "🎵 " + System.IO.Path.GetFileName(_musicWizardResult.MusicFilePath);

                    var volSlider = this.FindControl<Slider>("VolumeSlider");
                    if (volSlider != null)
                    {
                        volSlider.Value = _musicWizardResult.VideoVolume * 100.0;
                    }
                }
            };
        }

        var mobileCheckbox = this.FindControl<CheckBox>("MobileCheckbox") ?? this.FindControl<CheckBox>("PortraitModeCheckbox");
        
        if (mobileCheckbox != null)
        {
            UpdatePortraitOverlay();
            mobileCheckbox.IsCheckedChanged += (s, e) => UpdatePortraitOverlay();
        }
        
        // Add global input filter
        UpdateTooltips();
        AddHandler(InputElement.KeyDownEvent, GlobalKeyDownHandler, RoutingStrategies.Tunnel);
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
        this.Loaded += async (s, e) => {
            var store = new FortniteVideoSoftware.Core.Ipc.StateTransferStore();
            var state = await store.LoadAsync();
            var newObj = new System.Text.Json.Nodes.JsonObject();
            var preserveKeys = new[] { "MainWindowBounds", "VideoMergerBounds", "CropToolBounds", "GranularBounds", "MusicWizardBounds" };
            foreach (var key in preserveKeys) {
                if (state.TryGetPropertyValue(key, out var gb)) {
                    newObj[key] = gb?.DeepClone();
                }
            }
            await store.SaveAsync(newObj);
            await WindowBoundsHelper.LoadBoundsAsync(this, "MainWindowBounds");
            await InitializeHardwareScanAsync();
            UpdatePortraitOverlay();
        };

        this.Closing += (s, e) => {
            try { WindowBoundsHelper.SaveBoundsSync(this, "MainWindowBounds"); } catch {}
        };
    }

    private async void OnUploadVideoClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        
        var options = new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Open Video",
            AllowMultiple = false,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Video Files") { Patterns = new[] { "*.mp4", "*.mkv", "*.avi", "*.mov" } } }
        };

        try
        {
            if (File.Exists(_paths.SessionStateFile))
            {
                var state = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(File.ReadAllText(_paths.SessionStateFile));
                if (state != null && state["UploadVideoDirectory"]?.ToString() is string startPath && Directory.Exists(startPath))
                {
                    options.SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(startPath));
                }
            }
        }
        catch { }
        
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(options);

        if (files.Count > 0)
        {
            string path = files[0].Path.LocalPath;
            _loadedVideoPath = path;
            _isTimelineDrawn = false;
            RuntimeLog.Info("UI", $"User uploaded video: {path}");
            
            try
            {
                var state = File.Exists(_paths.SessionStateFile) 
                    ? System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(File.ReadAllText(_paths.SessionStateFile)) ?? new System.Text.Json.Nodes.JsonObject()
                    : new System.Text.Json.Nodes.JsonObject();
                state["UploadVideoDirectory"] = Path.GetDirectoryName(path);
                File.WriteAllText(_paths.SessionStateFile, state.ToJsonString());
            }
            catch { }

            _ = _videoHost?.IpcClient?.LoadFileAsync(path);
            _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "no");
            
            if (_videoHost != null) _videoHost.IsVisible = true;
            UpdatePortraitOverlay();

            var uploadOverlay = this.FindControl<Border>("UploadOverlay");
            if (uploadOverlay != null) uploadOverlay.IsVisible = false;

            var gb = this.FindControl<Button>("GranularButton");
            if (gb != null) gb.IsEnabled = true;
            
            var qs = this.FindControl<SpinningWheelSlider>("QualitySlider");
            if (qs != null) qs.IsEnabled = true;
        }
    }

    private void UpdatePortraitOverlay()
    {
        var mobileCheckbox = this.FindControl<CheckBox>("MobileCheckbox") ?? this.FindControl<CheckBox>("PortraitModeCheckbox");
        var portraitTextInput = this.FindControl<TextBox>("PortraitTextInput");
        var dimmingGrid = this.FindControl<Grid>("PortraitDimmingGrid");

        if (mobileCheckbox != null)
        {
            if (portraitTextInput != null) 
            {
                portraitTextInput.Opacity = mobileCheckbox.IsChecked == true ? 1 : 0;
                portraitTextInput.IsHitTestVisible = mobileCheckbox.IsChecked == true;
            }
            if (dimmingGrid != null) dimmingGrid.IsVisible = mobileCheckbox.IsChecked == true;

            if (_videoHost?.IpcClient != null)
            {
                if (mobileCheckbox.IsChecked == true)
                {
                    _ = _videoHost?.IpcClient?.SetPropertyAsync("vf", "drawbox=x=0:y=0:w=iw/6:h=ih:color=black@0.6:t=fill,drawbox=x=iw*5/6:y=0:w=iw/6:h=ih:color=black@0.6:t=fill");
                }
                else
                {
                    _ = _videoHost?.IpcClient?.SetPropertyAsync("vf", "");
                }
            }
        }
    }

    private void PlayUiSound()
    {
        Task.Run(() => 
        {
            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
        });
    }

    private void ShowToast(string message, bool isError = false)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            var host = this.FindControl<StackPanel>("ToastHost");
            if (host == null) return;

            var toast = new Border
            {
                Background = isError ? Avalonia.Media.Brushes.DarkRed : Avalonia.Media.Brushes.DarkSlateGray,
                BorderBrush = isError ? Avalonia.Media.Brushes.Red : Avalonia.Media.Brushes.LightBlue,
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(10),
                Padding = new Avalonia.Thickness(15, 10),
                Child = new TextBlock 
                { 
                    Text = message, 
                    Foreground = Avalonia.Media.Brushes.White, 
                    FontWeight = Avalonia.Media.FontWeight.SemiBold 
                },
                Opacity = 0
            };

            host.Children.Add(toast);

            double t = 0;
            while (t < 0.2)
            {
                t += 0.05;
                toast.Opacity = t / 0.2;
                await Task.Delay(16);
            }
            toast.Opacity = 1;

            await Task.Delay(3000);

            t = 0;
            while (t < 0.3)
            {
                t += 0.05;
                toast.Opacity = 1.0 - (t / 0.3);
                await Task.Delay(16);
            }

            host.Children.Remove(toast);
        });
    }

    private void ShowPopupBadge(Control targetControl, string text, Avalonia.Media.IBrush color)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            var canvas = this.FindControl<Canvas>("OverlayCanvas");
            if (canvas == null) return;

            var pos = targetControl.TranslatePoint(new Avalonia.Point(targetControl.Bounds.Width / 2, 0), canvas);
            if (pos == null) return;

            var badge = new Border
            {
                Background = color,
                CornerRadius = new Avalonia.CornerRadius(4),
                Padding = new Avalonia.Thickness(8, 4),
                Child = new TextBlock 
                { 
                    Text = text, 
                    Foreground = Avalonia.Media.Brushes.White, 
                    FontSize = 11, 
                    FontWeight = Avalonia.Media.FontWeight.Bold 
                },
                Opacity = 1
            };

            canvas.Children.Add(badge);
            await Task.Delay(1); 
            
            double startX = pos.Value.X - (badge.Bounds.Width / 2);
            double startY = pos.Value.Y - 10;
            
            Canvas.SetLeft(badge, startX);
            Canvas.SetTop(badge, startY);

            double t = 0;
            while (t < 1.0)
            {
                t += 0.05;
                Canvas.SetTop(badge, startY - (t * 20));
                badge.Opacity = 1.0 - t;
                await Task.Delay(16);
            }

            canvas.Children.Remove(badge);
        });
    }

    private void ShowTimelineGlow(double timeMs, Avalonia.Media.IBrush color)
    {
        var canvas = this.FindControl<Canvas>("TimelineMarkersCanvas");
        if (canvas == null || _videoHost?.IpcClient == null) return;

        double duration = _videoHost.IpcClient.Duration;
        if (duration <= 0) return;

        double canvasWidth = canvas.Bounds.Width;
        double targetX = (timeMs / 1000.0 / duration) * canvasWidth;

        var glow = new Avalonia.Controls.Shapes.Rectangle
        {
            Fill = color,
            Width = 2,
            Height = canvas.Bounds.Height,
            Opacity = 0.8
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

    private void UpdateEstimatedQuality()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            var label = this.FindControl<TextBlock>("EstimatedQualityText");
            var slider = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("QualitySlider");
            if (label == null || slider == null || _videoHost?.IpcClient == null) return;
            var labels = new System.Collections.Generic.List<string> { "5MB", "10MB", "15MB", "20MB", "25MB", "30MB", "35MB", "40MB", "45MB", "50MB", "60MB", "70MB", "80MB", "100MB", "150MB", "200MB", "300MB", "400MB", "500MB", "1GB", "CQ High" };
            
            double targetMb = 40;
            if (slider.Value >= 0 && slider.Value < labels.Count)
            {
                string lbl = labels[slider.Value];
                if (lbl.Contains("MB")) double.TryParse(lbl.Replace("MB", ""), out targetMb);
                else if (lbl.Contains("GB")) { double.TryParse(lbl.Replace("GB", ""), out targetMb); targetMb *= 1024; }
                else targetMb = 1000; // CQ High
            }
            double duration = GetCurrentMpvTime(); // Fallback
            duration = _videoHost?.IpcClient?.Duration ?? 0.0;
            if (duration <= 0) return;
            
            double end = _trimEndMs > 0 ? _trimEndMs / 1000.0 : duration;
            double start = _trimStartMs > 0 ? _trimStartMs / 1000.0 : 0;
            double actualDuration = (end - start) / _baseSpeed;
            if (actualDuration <= 0) actualDuration = 0.1;
            
            double kbps = (targetMb * 8192) / actualDuration;
            
            string qText = "Quality: Low";
            var color = Avalonia.Media.Brushes.Tomato;
            
            if (kbps > 20000) { qText = "Quality: Amazing"; color = Avalonia.Media.Brushes.DeepSkyBlue; }
            else if (kbps > 10000) { qText = "Quality: Great"; color = Avalonia.Media.Brushes.LimeGreen; }
            else if (kbps > 5000) { qText = "Quality: Good"; color = Avalonia.Media.Brushes.Yellow; }
            else if (kbps > 2000) { qText = "Quality: Okay"; color = Avalonia.Media.Brushes.Orange; }
            
            label.Text = qText;
            label.Foreground = color;
        });
    }

    private void UpdateTimelineMarkers()
    {
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineMarkersCanvas");
        if (canvas == null || _videoHost?.IpcClient == null) return;

        double duration = _videoHost.IpcClient.Duration;

        if (duration <= 0) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            canvas.Children.Clear();
            double canvasWidth = canvas.Bounds.Width;
            if (canvasWidth <= 0) return;

            // DRAW TIMELINE SCALES
            double tickInterval = 5;
            if (duration > 3600) tickInterval = 300;
            else if (duration > 1800) tickInterval = 60;
            else if (duration > 300) tickInterval = 30;
            else if (duration > 60) tickInterval = 10;

            for (double t = 0; t <= duration; t += tickInterval)
            {
                double tx = (t / duration) * canvasWidth;
                
                var tickLine = new Avalonia.Controls.Shapes.Rectangle { Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(60, 255, 255, 255)), Width = 1, Height = canvas.Bounds.Height };
                Avalonia.Controls.Canvas.SetLeft(tickLine, tx);
                canvas.Children.Add(tickLine);

                var tickText = new TextBlock { 
                    Text = TimeSpan.FromSeconds(t).ToString(t >= 3600 ? "h\\:mm\\:ss" : "m\\:ss"), 
                    Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(180, 255, 255, 255)), 
                    FontSize = 9 
                };
                Avalonia.Controls.Canvas.SetLeft(tickText, tx + 2);
                Avalonia.Controls.Canvas.SetTop(tickText, 0);
                canvas.Children.Add(tickText);
            }

            if (_trimStartMs > 0)
            {
                double startX = (_trimStartMs / 1000.0 / duration) * canvasWidth;
                var startRect = new Avalonia.Controls.Shapes.Rectangle { Fill = Avalonia.Media.Brushes.LimeGreen, Width = 6, Height = canvas.Bounds.Height, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast) };
                Avalonia.Controls.Canvas.SetLeft(startRect, startX - 3);
                
                startRect.PointerEntered += (s,e) => startRect.Fill = Avalonia.Media.Brushes.LightGreen;
                startRect.PointerExited += (s,e) => startRect.Fill = Avalonia.Media.Brushes.LimeGreen;
                startRect.PointerPressed += (s,e) => { _draggingStartMarker = true; e.Pointer.Capture(startRect); };
                startRect.PointerReleased += (s,e) => { _draggingStartMarker = false; e.Pointer.Capture(null); UpdateEstimatedQuality(); };
                startRect.PointerMoved += (s,e) => {
                    if (_draggingStartMarker) {
                        double pos = e.GetPosition(canvas).X;
                        _trimStartMs = (pos / canvas.Bounds.Width) * duration * 1000;
                        if (_trimStartMs < 0) _trimStartMs = 0;
                        if (_trimStartMs > _trimEndMs && _trimEndMs > 0) _trimStartMs = _trimEndMs;
                        Avalonia.Controls.Canvas.SetLeft(startRect, ((_trimStartMs / 1000.0 / duration) * canvasWidth) - 3);
                    }
                };
                canvas.Children.Add(startRect);
                
                var startText = new TextBlock { Text = TimeSpan.FromMilliseconds(_trimStartMs).ToString("hh\\:mm\\:ss"), Foreground = Avalonia.Media.Brushes.LimeGreen, FontSize = 10, Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#80000000")), Padding = new Avalonia.Thickness(2) };
                Avalonia.Controls.Canvas.SetLeft(startText, startX + 5);
                Avalonia.Controls.Canvas.SetTop(startText, canvas.Bounds.Height - 15);
                canvas.Children.Add(startText);
                
                startRect.PointerMoved += (s,e) => {
                    if (_draggingStartMarker) {
                        startText.Text = TimeSpan.FromMilliseconds(_trimStartMs).ToString("hh\\:mm\\:ss\\.ff");
                        Avalonia.Controls.Canvas.SetLeft(startText, ((_trimStartMs / 1000.0 / duration) * canvasWidth) + 5);
                        Avalonia.Controls.Canvas.SetTop(startText, e.GetPosition(canvas).Y - 20);
                    }
                };
            }

            if (_trimEndMs > 0)
            {
                double endX = (_trimEndMs / 1000.0 / duration) * canvasWidth;
                var endRect = new Avalonia.Controls.Shapes.Rectangle { Fill = Avalonia.Media.Brushes.Tomato, Width = 6, Height = canvas.Bounds.Height, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast) };
                Avalonia.Controls.Canvas.SetLeft(endRect, endX - 3);
                
                endRect.PointerEntered += (s,e) => endRect.Fill = Avalonia.Media.Brushes.OrangeRed;
                endRect.PointerExited += (s,e) => endRect.Fill = Avalonia.Media.Brushes.Tomato;
                endRect.PointerPressed += (s,e) => { _draggingEndMarker = true; e.Pointer.Capture(endRect); };
                endRect.PointerReleased += (s,e) => { _draggingEndMarker = false; e.Pointer.Capture(null); UpdateEstimatedQuality(); };
                endRect.PointerMoved += (s,e) => {
                    if (_draggingEndMarker) {
                        double pos = e.GetPosition(canvas).X;
                        _trimEndMs = (pos / canvas.Bounds.Width) * duration * 1000;
                        if (_trimEndMs > duration * 1000) _trimEndMs = duration * 1000;
                        if (_trimEndMs < _trimStartMs) _trimEndMs = _trimStartMs;
                        Avalonia.Controls.Canvas.SetLeft(endRect, ((_trimEndMs / 1000.0 / duration) * canvasWidth) - 3);
                    }
                };
                canvas.Children.Add(endRect);
                
                var endText = new TextBlock { Text = TimeSpan.FromMilliseconds(_trimEndMs).ToString("hh\\:mm\\:ss"), Foreground = Avalonia.Media.Brushes.Tomato, FontSize = 10, Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#80000000")), Padding = new Avalonia.Thickness(2) };
                Avalonia.Controls.Canvas.SetLeft(endText, endX - 45);
                Avalonia.Controls.Canvas.SetTop(endText, canvas.Bounds.Height - 15);
                canvas.Children.Add(endText);
                
                endRect.PointerMoved += (s,e) => {
                    if (_draggingEndMarker) {
                        endText.Text = TimeSpan.FromMilliseconds(_trimEndMs).ToString("hh\\:mm\\:ss\\.ff");
                        Avalonia.Controls.Canvas.SetLeft(endText, ((_trimEndMs / 1000.0 / duration) * canvasWidth) - 55);
                        Avalonia.Controls.Canvas.SetTop(endText, e.GetPosition(canvas).Y - 20);
                    }
                };
            }             
                if (_trimStartMs > 0 && _trimEndMs > _trimStartMs)
                {
                    double startX = (_trimStartMs / 1000.0 / duration) * canvasWidth;
                    var endX = (_trimEndMs / 1000.0 / duration) * canvasWidth;
                    var regionRect = new Avalonia.Controls.Shapes.Rectangle { Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(128, 128, 128, 128)), Width = endX - startX, Height = canvas.Bounds.Height };
                    Avalonia.Controls.Canvas.SetLeft(regionRect, startX);
                    canvas.Children.Add(regionRect);
                }
        });
    }
    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_videoHost?.IpcClient == null) return;

        var canvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineMarkersCanvas");
        if (canvas != null && canvas.Children.Count == 0)
        {
            UpdateTimelineMarkers();
        }

        var playPauseButton = this.FindControl<Button>("PlayPauseButton");
        if (playPauseButton != null)
        {
            if (_videoHost.IpcClient.IsPaused)
            {
                if (playPauseButton.Content?.ToString() != "▶")
                    playPauseButton.Content = "▶";
            }
            else
            {
                if (playPauseButton.Content?.ToString() != "⏸")
                    playPauseButton.Content = "⏸";
            }
        }

        double time = _videoHost.IpcClient.CurrentTime;
        double dur = _videoHost.IpcClient.Duration;
        
        var timeElapsed = this.FindControl<TextBlock>("TimeElapsed");
        if (timeElapsed != null) timeElapsed.Text = TimeSpan.FromSeconds(time).ToString("hh\\:mm\\:ss");
        
        var timelineSlider = this.FindControl<Slider>("TimelineSlider");
        if (timelineSlider != null && dur > 0)
        {
            if (!_isTimelineDrawn)
            {
                UpdateTimelineMarkers();
                _isTimelineDrawn = true;
            }
            _isTimerUpdatingSlider = true;
            timelineSlider.Value = (time / dur) * 100.0;
            _isTimerUpdatingSlider = false;
            
            var timeRemaining = this.FindControl<TextBlock>("TimeRemaining");
            if (timeRemaining != null) timeRemaining.Text = "-" + TimeSpan.FromSeconds(dur - time).ToString("hh\\:mm\\:ss");
        }
        
        if (_videoHost.IpcClient.IsEof)
        {
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
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
                
                Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                {
                    RuntimeLog.Info("Hardware", $"Hardware scan completed: {mode}");
                    hwLabel.Text = $"HW: {mode} (Ready)";
                    hwLabel.Foreground = Avalonia.Media.Brushes.LimeGreen;
                });
            }
            catch
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                {
                    RuntimeLog.Fail("Hardware", "Hardware scan failed, falling back to CPU Only.");
                    hwLabel.Text = "HW: CPU Only";
                    hwLabel.Foreground = Avalonia.Media.Brushes.Gray;
                });
            }
        }
    }
    
    private double GetCurrentMpvTime() { return _videoHost?.IpcClient?.CurrentTime ?? 0.0; }
    
    private void GlobalKeyDownHandler(object? sender, KeyEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox or NumericUpDown)
        {
            return; // Let textboxes handle their own typing
        }

        var kb = SettingsManager.Instance.KeyBinds;

        if (e.Key == Key.Delete)
        {
            // Silent Deletion
            if (FocusManager?.GetFocusedElement() is ListBox listBox && listBox.SelectedItem is string filePath)
            {
                try { System.IO.File.Delete(filePath); } catch { }
            }
        }
        else if (e.Key == kb.PlayPause)
        {
            if (_videoHost?.IpcClient != null)
            {
                bool isPaused = _videoHost.IpcClient.IsPaused;
                _ = _videoHost.IpcClient.SetPropertyAsync("pause", isPaused ? "no" : "yes");
                e.Handled = true;
            }
        }
        else if (e.Key == kb.SeekForward)
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("seek", 5);
            e.Handled = true;
        }
        else if (e.Key == kb.SeekBackward)
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("seek", -5);
            e.Handled = true;
        }
        else if (e.Key == kb.VolumeUp)
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("add", "volume", 2);
            e.Handled = true;
        }
        else if (e.Key == kb.VolumeDown)
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("add", "volume", -2);
            e.Handled = true;
        }
        else if (e.Key == kb.MarkStart)
        {
            var btn = this.FindControl<Button>("MarkStartButton");
            btn?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        }
        else if (e.Key == kb.MarkEnd)
        {
            var btn = this.FindControl<Button>("MarkEndButton");
            btn?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        }
    }

        private async Task ProcessVideoAsync(Button processButton)
    {
        if (string.IsNullOrEmpty(_loadedVideoPath) || !File.Exists(_loadedVideoPath))
        {
            ShowToast("No valid video loaded to process!", true);
            PlayUiSound();
            processButton.IsEnabled = true;
            processButton.Content = "PROCESS";
            return;
        }

        await Task.Yield();

        try
        {
            RuntimeLog.Info("Process", "Starting video processing pipeline via ProcessWorker.");
            
            var paths = ApplicationPaths.CreateDefault();
            var worker = new ProcessWorker(paths);
            
            if (_videoHost != null) _videoHost.IsVisible = false;
            this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer")?.StartOverlay();

            // Wire up progress and completion
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
                    this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer")?.StopOverlay();
                    if (_videoHost != null) _videoHost.IsVisible = true;
                    if (success)
                    {
                        RuntimeLog.Success("Process", $"Video processing completed successfully. Saved to: {message}");
                        ShowToast("Processing Complete!", false);
                        PlayUiSound();
                        var dlg = new FortniteVideoSoftware.App.Controls.FinishedDialogWindow();
                        dlg.SetOutputPath(message);
                        await dlg.ShowDialog(this);
                        if (dlg.DialogResult == 1)
                        {
                            Close();
                        }
                    }
                    else
                    {
                        RuntimeLog.Fail("Process", $"Video processing failed: {message}");
                        ShowToast("Processing Failed", true);
                        PlayUiSound();
                    }
                    processButton.IsEnabled = true;
                    processButton.Content = "PROCESS";
                });
            };

            // Retrieve hardware strategy from status label
            string hwMode = "CPU";
            var hwLabel = this.FindControl<TextBlock>("HardwareStatusLabel");
            if (hwLabel != null && hwLabel.Text != null)
            {
                if (hwLabel.Text.Contains("NVIDIA")) hwMode = "NVIDIA";
                else if (hwLabel.Text.Contains("AMD")) hwMode = "AMD";
                else if (hwLabel.Text.Contains("INTEL")) hwMode = "INTEL";
            }

            // Configure ProcessWorker parameters
            worker.InputPath = _loadedVideoPath;
            worker.StartTimeMs = _trimStartMs;
            
            // If end time is 0, use full video duration
            double duration = GetCurrentMpvTime(); // Default fallback
            duration = _videoHost?.IpcClient?.Duration ?? 0.0;

            worker.EndTimeMs = _trimEndMs > 0 ? _trimEndMs : duration * 1000;
            worker.SpeedSegments = new System.Collections.Generic.List<SpeedSegment>(_speedSegments);
            worker.SpeedFactor = _baseSpeed;
            worker.ThumbnailPosMs = _thumbnailPosMs;
            worker.HardwareStrategy = hwMode;
            worker.IsMobileFormat = this.FindControl<CheckBox>("MobileCheckbox")?.IsChecked ?? this.FindControl<CheckBox>("PortraitModeCheckbox")?.IsChecked ?? true;
            worker.IsBossHp = this.FindControl<CheckBox>("BossHpCheckbox")?.IsChecked ?? false;
            worker.ShowTeammates = this.FindControl<CheckBox>("TeammatesCheckbox")?.IsChecked ?? false;
            
            worker.PortraitText = this.FindControl<TextBox>("PortraitTextInput")?.Text;
            worker.TargetMbOverride = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("QualitySlider")?.Value;

            // Pass Music Wizard config
            if (_musicWizardResult != null && !string.IsNullOrEmpty(_musicWizardResult.MusicFilePath) && File.Exists(_musicWizardResult.MusicFilePath))
            {
                worker.MusicTracks = new System.Collections.Generic.List<MusicTrack>
                {
                    new MusicTrack(_musicWizardResult.MusicFilePath, _musicWizardResult.OffsetSeconds, worker.EndTimeMs / 1000.0)
                };

                worker.MusicConfig = new System.Text.Json.Nodes.JsonObject
                {
                    ["ducking_threshold"] = _musicWizardResult.EnableDucking ? 0.15 : 1.0,
                    ["ducking_ratio"] = _musicWizardResult.EnableDucking ? 2.5 : 1.0,
                    ["main_vol"] = _musicWizardResult.VideoVolume,
                    ["music_vol"] = _musicWizardResult.MusicVolume,
                    ["carving_enabled"] = _musicWizardResult.EnableCarving
                };
            }

            // Start processing asynchronously but we don't block the UI thread
            _ = worker.RunAsync(new CancellationTokenSource().Token);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Process", ex);
            ShowToast("Error during process launch.", true);
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
                mpvPath = "mpv.exe"; // Fallback to PATH
            }
            await _videoHost.StartMpvProcessAsync(mpvPath);
            if (_videoHost.IpcClient != null)
            {
                _videoHost.IpcClient.SeekCompleted += () => {
                    Avalonia.Threading.Dispatcher.UIThread.Post(async () => {
                        _isSeeking = false;
                        if (_nextSeekTarget.HasValue) {
                            double target = _nextSeekTarget.Value;
                            _nextSeekTarget = null;
                            await SeekInternal(target);
                        }
                    });
                };
            }
        }
    }

    private async Task SeekInternal(double time) {
        if (_isSeeking) {
            _nextSeekTarget = time;
            return;
        }
        _isSeeking = true;
        if (_videoHost?.IpcClient != null) {
            await _videoHost.IpcClient.SendCommandAsync("seek", time, "absolute");
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
        
        // Success Auto-Exit: When the export finishes, clicking "SHARE VIA WHATSAPP" or "OPEN FOLDER" 
        // MUST execute the OS command and then immediately invoke Environment.Exit(0).
        Environment.Exit(0);
    }

    protected override void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        if (_videoHost?.IpcClient != null)
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("stop");
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        RuntimeLog.Info("UI", "Closing MainWindow and terminating MPV handle.");
        SaveWindowState();
        
        if (_videoHost?.IpcClient != null)
        {
            _videoHost.IpcClient.Dispose();
        }
        base.OnClosed(e);
        Environment.Exit(0);
    }

    private void LoadWindowState()
    {
        try
        {
            if (File.Exists(_paths.WindowStateFile))
            {
                string json = File.ReadAllText(_paths.WindowStateFile);
                var state = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(json);
                if (state != null)
                {
                    double w = state["Width"]?.GetValue<double>() ?? 1200;
                    double h = state["Height"]?.GetValue<double>() ?? 800;
                    int x = state["X"]?.GetValue<int>() ?? 0;
                    int y = state["Y"]?.GetValue<int>() ?? 0;

                    this.Width = w;
                    this.Height = h;
                    
                    var p = new Avalonia.PixelPoint(x, y);
                    bool isOnScreen = false;
                    
                    if (this.Screens != null)
                    {
                        foreach (var screen in this.Screens.All)
                        {
                            if (screen.Bounds.Contains(p))
                            {
                                isOnScreen = true;
                                break;
                            }
                        }
                    }

                    if (isOnScreen)
                    {
                        this.Position = p;
                        this.WindowStartupLocation = WindowStartupLocation.Manual;
                    }
                    else
                    {
                        this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    }
                }
            }
            else
            {
                this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }
        catch
        {
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private void SaveWindowState()
    {
        try
        {
            _paths.EnsureWritableDirectories();
            var state = new System.Text.Json.Nodes.JsonObject
            {
                ["Width"] = this.Bounds.Width,
                ["Height"] = this.Bounds.Height,
                ["X"] = this.Position.X,
                ["Y"] = this.Position.Y
            };
            File.WriteAllText(_paths.WindowStateFile, state.ToJsonString());
        }
        catch { }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            try { BeginMoveDrag(e); } catch { }
        }
    }
}

