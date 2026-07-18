using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Ipc;
using FortniteVideoSoftware.Core.Media;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using IOPath = System.IO.Path;

namespace FortniteVideoSoftware.App;

public partial class CropToolWindow : Window
{
    private const double PortraitWidth = CoordinateConstants.PortraitW;
    private const double PortraitHeight = CoordinateConstants.PortraitH;
    private const double ContentTop = CoordinateConstants.UIPaddingTop;
    private const double ContentBottom = CoordinateConstants.PortraitH - CoordinateConstants.UIPaddingBottom;
    private const double MinSelectionSize = 10;
    private const double MinItemSize = 20;
    private const double HandleSize = 24;
    private const double SnapThreshold = 8;

    private readonly ApplicationPaths _paths = ApplicationPaths.CreateDefault();
    private readonly string? _initialVideoPath;
    private readonly ObservableCollection<LayerEntry> _layers = new();
    private readonly List<CropEditorItem> _items = new();
    private readonly List<Control> _placeholderControls = new();
    private readonly List<Control> _candidateControls = new();
    private readonly List<Control> _guideControls = new();
    private readonly List<string> _tempFiles = new();
    private readonly Stack<EditorSnapshot> _undoStack = new();
    private readonly Stack<EditorSnapshot> _redoStack = new();

    private MpvVideoView? _videoHost;
    private Canvas? _sourceCanvas;
    private Canvas? _portraitCanvas;
    private Image? _snapshotImage;
    private Image? _composerBackgroundImage;
    private TextBox? _roleTextBox;
    private ListBox? _layerList;
    private Slider? _timelineSlider;
    private TextBlock? _currentTimeLabel;
    private TextBlock? _totalTimeLabel;
    private TextBlock? _statusLabel;
    private TextBlock? _goalLabel;
    private TextBlock? _selectionInfo;
    private ProgressBar? _wizardProgress;

    private Rectangle? _selectionRect;
    private SourceRect? _sourceSelection;
    private Point _sourceSelectionStart;
    private bool _isDrawingSourceSelection;

    private CropEditorItem? _selectedItem;
    private CropEditorItem? _activeEditItem;
    private ComposerEditMode _composerEditMode = ComposerEditMode.None;
    private Point _editPointerStart;
    private double _editStartX;
    private double _editStartY;
    private double _editStartWidth;
    private double _editStartHeight;
    private EditorSnapshot? _editStartSnapshot;

    private string? _videoPath;
    private string? _snapshotPath;
    private string _originalResolution = "1920x1080";
    private int _snapshotWidth = 1920;
    private int _snapshotHeight = 1080;
    private double _durationMs;
    private bool _isTimelineUpdating;
    private bool _isTimerUpdatingSlider;
    private bool _isSeeking;
    private double? _nextSeekTarget;
    private Avalonia.Threading.DispatcherTimer? _playheadBadgeTimer;
    private bool _isMpvStarted;
    private bool _dirty;
    private bool _restoringSnapshot;
    private bool _suppressLayerSelection;
    private bool _isSafeToClose;
    private DispatcherTimer? _timelineTimer;

    private static readonly HudRole[] Roles =
    [
        new("loot", "Loot Area", 10, 680, 1370),
        new("stats", "Mini Map + Stats", 30, 730, 150),
        new("normal_hp", "Own Health Bar (HP)", 20, 30, 1620),
        new("boss_hp", "Boss HP (For When You Are The Boss Character)", 20, 30, 1620),
        new("team", "Teammates health Bars (HP)", 40, 30, 250),
        new("spectating", "Spectating Eye", 100, 30, 1300),
    ];

    private static readonly Dictionary<string, HudRole> RoleByKey = Roles.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

    public CropToolWindow() : this(null)
    {
    }

    public CropToolWindow(string? initialVideoPath)
    {
        _initialVideoPath = string.IsNullOrWhiteSpace(initialVideoPath) ? null : initialVideoPath;

        InitializeComponent();
        FortniteVideoSoftware.App.WindowBoundsHelper.Track(this, "CropToolBounds");
        FindControls();
        AttachTitleBarDrag();
        WireEvents();
        InitializeHistory();
        SetWizardState(1, "Upload Video", "Open a reference clip to start.");

        Loaded += async (_, _) =>
        {
            await InitializeMpvAsync();
            await LoadExistingPlaceholdersAsync();

            if (!string.IsNullOrWhiteSpace(_initialVideoPath) && File.Exists(_initialVideoPath))
            {
                await LoadVideoAsync(_initialVideoPath, startPaused: true);
            }
        };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void FindControls()
    {
        _videoHost = this.FindControl<MpvVideoView>("VideoHost");
        _sourceCanvas = this.FindControl<Canvas>("SourceCanvas");
        _portraitCanvas = this.FindControl<Canvas>("PortraitCanvas");
        _snapshotImage = this.FindControl<Image>("SnapshotImage");
        _composerBackgroundImage = this.FindControl<Image>("ComposerBackgroundImage");
        _roleTextBox = this.FindControl<TextBox>("RoleTextBox");
        _layerList = this.FindControl<ListBox>("LayerList");
        _timelineSlider = this.FindControl<Slider>("TimelineSlider");
        _currentTimeLabel = this.FindControl<TextBlock>("CurrentTimeLabel");
        _totalTimeLabel = this.FindControl<TextBlock>("TotalTimeLabel");
        _statusLabel = this.FindControl<TextBlock>("StatusLabel");
        _goalLabel = this.FindControl<TextBlock>("GoalLabel");
        _selectionInfo = this.FindControl<TextBlock>("SelectionInfo");
        _wizardProgress = this.FindControl<ProgressBar>("WizardProgress");
    }

    private void WireEvents()
    {
        if (_sourceCanvas != null)
        {
            _sourceCanvas.PointerPressed += SourceCanvas_PointerPressed;
            _sourceCanvas.PointerMoved += SourceCanvas_PointerMoved;
            _sourceCanvas.PointerReleased += SourceCanvas_PointerReleased;
        }

        if (_portraitCanvas != null)
        {
            _portraitCanvas.PointerPressed += (_, e) =>
            {
                if (ReferenceEquals(e.Source, _portraitCanvas))
                {
                    SelectItem(null);
                }
            };
        }

        if (_layerList != null)
        {
            _layerList.ItemsSource = _layers;
            _layerList.SelectionChanged += (_, _) =>
            {
                if (_suppressLayerSelection)
                {
                    return;
                }

                if (_layerList.SelectedItem is LayerEntry entry)
                {
                    SelectItem(_items.FirstOrDefault(i => i.RoleKey == entry.RoleKey), updateLayerList: false);
                }
            };
        }

        ButtonClick("OpenVideoButton", async (_, _) => await OpenVideoAsync());
        ButtonClick("SnapshotButton", async (_, _) => await TakeSnapshotAsync());
        ButtonClick("MagicWandButton", (_, _) => ShowMagicWandCandidates());
        ButtonClick("BackToVideoButton", (_, _) => ShowVideoPanel());
        ButtonClick("PlayPauseButton", async (_, _) => await TogglePlayPauseAsync());
        ButtonClick("AddSelectionButton", async (_, _) => await AddCurrentSelection());
        
        ButtonClick("DeleteMenuButton", (_, _) =>
        {
            if (!FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.ConfirmCropToolDelete)
            {
                var btn = this.FindControl<Button>("DeleteMenuButton");
                btn?.Flyout?.Hide();
                DeleteSelectedItem();
            }
        });
        
        ButtonClick("ConfirmDeleteButton", (_, _) =>
        {
            DeleteSelectedItem();
            var btn = this.FindControl<Button>("DeleteMenuButton");
            btn?.Flyout?.Hide();
        });
        ButtonClick("UndoButton", (_, _) => Undo());
        ButtonClick("RedoButton", (_, _) => Redo());
        ButtonClick("RaiseButton", (_, _) => MoveSelectedLayer(1));
        ButtonClick("LowerButton", (_, _) => MoveSelectedLayer(-1));
        
        ButtonClick("ResetMenuButton", (_, _) =>
        {
            if (!FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.ConfirmCropToolReset)
            {
                var btn = this.FindControl<Button>("ResetMenuButton");
                btn?.Flyout?.Hide();
                ResetWorkingState();
            }
        });
        
        ButtonClick("ConfirmResetButton", (_, _) =>
        {
            ResetWorkingState();
            var btn = this.FindControl<Button>("ResetMenuButton");
            btn?.Flyout?.Hide();
        });
        ButtonClick("ReturnButton", async (_, _) => await ReturnToMainAppAsync());
        ButtonClick("SaveButton", async (button, _) => await SaveAndReturnAsync(button));
        BuildMaskOverlayUi();

        var showPlaceholders = this.FindControl<CheckBox>("ShowPlaceholders");
        if (showPlaceholders != null)
        {
            showPlaceholders.IsCheckedChanged += (_, _) =>
            {
                bool visible = showPlaceholders.IsChecked == true;
                foreach (Control control in _placeholderControls)
                {
                    control.IsVisible = visible;
                }

                var slider = this.FindControl<Slider>("PlaceholderOpacitySlider");
                if (slider != null)
                {
                    slider.IsVisible = visible;
                }
            };
        }

        var opacitySlider = this.FindControl<Slider>("PlaceholderOpacitySlider");
        if (opacitySlider != null)
        {
            opacitySlider.PropertyChanged += (_, e) =>
            {
                if (e.Property == Slider.ValueProperty)
                {
                    UpdatePlaceholderOpacity((byte)Math.Round(opacitySlider.Value));
                }
            };
        }

        if (_timelineSlider != null)
        {
            _timelineSlider.PropertyChanged += (s, e) =>
            {
                if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty && e.NewValue is double newValue && !_isTimerUpdatingSlider)
                {
                    double duration = _videoHost?.IpcClient?.Duration ?? 0.0;
                    if (duration > 0 && _videoHost?.IpcClient != null)
                    {
                        double targetTime = (newValue / 100.0) * duration;
                        ShowPlayheadBadge(targetTime, newValue);
                    }
                }
            };
            
            _timelineSlider.PointerReleased += (s, e) =>
            {
                double duration = _videoHost?.IpcClient?.Duration ?? 0.0;
                if (duration > 0 && _videoHost?.IpcClient != null)
                {
                    double targetTime = (_timelineSlider.Value / 100.0) * duration;
                    _ = SeekInternal(targetTime);
                }
            };
        }

        var timelinePanel = this.FindControl<Border>("TimelinePanel");
        var timelineCanvas = this.FindControl<Canvas>("CropTimelineMarkersCanvas");
        if (timelinePanel != null && timelineCanvas != null && _timelineSlider != null)
        {
            timelinePanel.PointerPressed += (s, e) => SeekTimelineFromPointer(e, timelineCanvas, _timelineSlider);
        }

        _timelineTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timelineTimer.Tick += (_, _) => UpdateTimelineUi();
        _timelineTimer.Start();
    }

    private void BuildMaskOverlayUi()
    {
        var combo = this.FindControl<ComboBox>("CropToolMaskOverlayCombo");
        if (combo != null)
        {
            var profiles = FortniteVideoSoftware.App.Infrastructure.MaskOverlayManager.GetAvailableProfiles();
            combo.ItemsSource = profiles;
            combo.SelectedItem = FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.ActiveMaskOverlay;

            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedItem is string selected && selected != FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.ActiveMaskOverlay)
                {
                    // ISSUE_2: switching profiles overwrites the shared conf and wipes the
                    // composer. Never discard unsaved work without asking. Reverting the
                    // selection below cannot recurse: the equality guard above short-circuits.
                    if (_dirty && _items.Count > 0)
                    {
                        bool discard = NativeDialog.ShowQuestion(
                            "You have unsaved mask changes.\n\nSwitching profiles will discard them.\n\nClick Yes to discard the changes and switch, or No to stay on the current profile.",
                            "Unsaved Changes");
                        if (!discard)
                        {
                            combo.SelectedItem = FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.ActiveMaskOverlay;
                            return;
                        }
                    }

                    FortniteVideoSoftware.App.Infrastructure.MaskOverlayManager.ApplyProfile(selected);
                    ResetWorkingState();
                    _ = LoadExistingPlaceholdersAsync();
                    SetStatus("Profile Loaded: " + selected);
                }
            };
        }

        var btn = this.FindControl<Button>("CreateMaskOverlayBtn");
        var txt = this.FindControl<TextBox>("NewMaskOverlayTextBox");
        if (btn != null && txt != null)
        {
            btn.Click += async (s, e) =>
            {
                var newName = txt.Text?.Trim();
                if (string.IsNullOrWhiteSpace(newName)) return;

                // ISSUE_6: profile names become file names — validate before any disk work.
                var safeName = FortniteVideoSoftware.App.Infrastructure.MaskOverlayManager.SanitizeProfileName(newName);
                if (safeName == null)
                {
                    SetStatus("Invalid profile name. Avoid characters like \\ / : * ? \" < > |.");
                    return;
                }

                try
                {
                    // ISSUE_8: create the new profile (which switches the active profile)
                    // BEFORE saving, so SaveConfigAsync syncs the current layout into the
                    // NEW profile and the old profile keeps its untouched baseline.
                    FortniteVideoSoftware.App.Infrastructure.MaskOverlayManager.CreateNewProfile(safeName);
                    if (_items.Count > 0)
                    {
                        await SaveConfigAsync();
                    }

                    if (combo != null)
                    {
                        var updatedProfiles = FortniteVideoSoftware.App.Infrastructure.MaskOverlayManager.GetAvailableProfiles();
                        combo.ItemsSource = updatedProfiles;
                        combo.SelectedItem = safeName;
                    }
                    txt.Text = "";
                    SetStatus("New overlay created: " + safeName);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("CROP", $"Create profile failed: {ex.Message}");
                    SetStatus("Could not create the profile. See runtime log.");
                }
            };
        }
    }

    private void ButtonClick(string name, EventHandler<RoutedEventArgs> handler)
    {
        var button = this.FindControl<Button>(name);
        if (button != null)
        {
            button.Click += handler;
        }
    }

    private void InitializeHistory()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        _undoStack.Push(CaptureSnapshot());
        RefreshUndoRedoButtons();
    }

    private async Task InitializeMpvAsync()
    {
        if (_isMpvStarted || _videoHost == null)
        {
            return;
        }

        _isMpvStarted = true;
        string mpvPath = ResolveBinaryPath("mpv.exe", "frontend");
        RuntimeLog.Info("CROP", $"Using MPV at: {mpvPath}");
        await _videoHost.StartMpvProcessAsync(mpvPath);
    }

    private async Task OpenVideoAsync()
    {
        var options = new FilePickerOpenOptions
        {
            Title = "Open Reference Video",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Video Files")
                {
                    Patterns = ["*.mp4", "*.mkv", "*.avi", "*.mov", "*.webm", "*.m4v"]
                }
            ]
        };

        var paths = FortniteVideoSoftware.Core.Infrastructure.ApplicationPaths.CreateDefault();
        try
        {
            string? startPath = null;
            if (System.IO.File.Exists(paths.SessionStateFile))
            {
                var state = FortniteVideoSoftware.Core.Infrastructure.AtomicJsonFile.ReadObject(paths.SessionStateFile);
                if (state != null && state.TryGetPropertyValue("CropToolUploadDirectory", out var node) && node != null)
                {
                    startPath = node.ToString();
                }
            }

            if (string.IsNullOrEmpty(startPath) || !System.IO.Directory.Exists(startPath))
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string myVideos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                string myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string[] probes = new[]
                {
                    System.IO.Path.Combine(localAppData, "Temp", "Highlights", "Fortnite"),
                    System.IO.Path.Combine(localAppData, "Temp", "Highlights"),
                    System.IO.Path.Combine(localAppData, "NVIDIA Corporation", "GeForce Experience", "Highlights"),
                    System.IO.Path.Combine(myVideos, "Highlights", "Fortnite"),
                    System.IO.Path.Combine(myVideos, "Fortnite"),
                    System.IO.Path.Combine(myVideos, "Highlights"),
                    System.IO.Path.Combine(myDocuments, "Highlights")
                };

                startPath = myVideos;
                foreach (var probe in probes)
                {
                    if (System.IO.Directory.Exists(probe))
                    {
                        startPath = probe;
                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(startPath) && Directory.Exists(startPath))
            {
                try { Environment.CurrentDirectory = startPath; } catch { }
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(new Uri(startPath));
            }
        }
        catch (Exception ex) { RuntimeLog.Info("CROP", $"Could not read suggested start location: {ex.Message}"); }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(options);

        if (files.Count == 0)
        {
            return;
        }

        try
        {
            string? directory = System.IO.Path.GetDirectoryName(files[0].Path.LocalPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                await new StateTransferStore(paths)
                    .UpdatePropertiesAsync(new System.Text.Json.Nodes.JsonObject
                    {
                        ["CropToolUploadDirectory"] = directory
                    });
            }
        }
        catch (Exception ex) { RuntimeLog.Info("CROP", $"Could not save upload directory preference: {ex.Message}"); }

        await LoadVideoAsync(files[0].Path.LocalPath, startPaused: true);
    }
    private async Task LoadVideoAsync(string path, bool startPaused)
    {
        if (!File.Exists(path))
        {
            SetStatus("Video file does not exist.");
            return;
        }

        await InitializeMpvAsync();

        _videoPath = path;
        _snapshotPath = null;
        _durationMs = 0;
        ClearSourceSelection();
        ClearMagicWandCandidates();
        ShowVideoPanel();

        SetWizardState(2, "Find HUD Frame", "Loading video metadata...");
        SetEnabled("PlayPauseButton", true);
        SetEnabled("SnapshotButton", false);
        SetVisible("TimelinePanel", true);
        SetVisible("UploadHint", false);

        if (_videoHost?.IpcClient != null)
        {
            await _videoHost.IpcClient.LoadFileAsync(path);
            if (startPaused)
            {
                await _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
            }
        }

        try
        {
            var prober = new MediaProber(ResolveBinaryPath("ffprobe.exe", "backend"), path);
            _durationMs = Math.Max(0, await prober.GetDurationAsync() * 1000.0);
            _originalResolution = await prober.GetResolutionStringAsync();
            var (w, h) = CoordinateMath.GetResolutionInts(_originalResolution);
            _snapshotWidth = w;
            _snapshotHeight = h;

            double aspectRatio = h > 0 ? (double)w / h : 1.777;
            if (Math.Abs(aspectRatio - (16.0 / 9.0)) > 0.05)
            {
                NativeDialog.ShowError($"The selected video has a resolution of {w}x{h} (Aspect Ratio: {aspectRatio:F2}).\n\nThe crop tool requires a standard 16:9 resolution (e.g., 1920x1080, 2560x1440, 3840x2160) to generate accurate HUD overlay masks. Non-16:9 videos will result in misaligned overlays on standard videos.\n\nPlease upload a 16:9 video for crop configuration.", "Unsupported Aspect Ratio");
                SetWizardState(2, "Find HUD Frame", $"Error: Non 16:9 video ({w}x{h}) rejected.");
                return;
            }

            if (_timelineSlider != null)
            {
                _isTimelineUpdating = true;
                _timelineSlider.Minimum = 0;
                _timelineSlider.Maximum = 100;
                _timelineSlider.Value = 0;
                _isTimelineUpdating = false;
            }

            RuntimeLog.Info("CROP", $"Video loaded: {path} | Resolution: {_originalResolution} | Duration: {_durationMs:F0}ms");
            SetEnabled("SnapshotButton", true);
            SetWizardState(2, "Find HUD Frame", $"Frame ready ({_originalResolution}).");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Video probe failed: {ex.Message}");
            SetEnabled("SnapshotButton", true);
            SetWizardState(2, "Find HUD Frame", "Metadata fallback is 1920x1080.");
        }
    }

    private async Task TogglePlayPauseAsync()
    {
        if (_videoHost?.IpcClient == null)
        {
            return;
        }

        bool currentlyPaused = _videoHost.IpcClient.IsPaused;
        await _videoHost.IpcClient.SetPropertyAsync("pause", currentlyPaused ? "no" : "yes");
        SetContent("PlayPauseButton", currentlyPaused ? "PAUSE" : "PLAY");
    }

    private async Task TakeSnapshotAsync()
    {
        if (string.IsNullOrWhiteSpace(_videoPath) || !File.Exists(_videoPath))
        {
            SetStatus("Open a video first.");
            return;
        }

        SetEnabled("SnapshotButton", false);
        SetContent("SnapshotButton", "CAPTURING...");
        SetWizardState(3, "Refine Box", "Capturing snapshot...");

        try
        {
            _paths.EnsureWritableDirectories();
            string output = IOPath.Combine(_paths.TempDirectory, $"crop_snapshot_{Guid.NewGuid():N}.png");
            string tempOutput = output + ".tmp.png";
            double seconds = GetCurrentTimeMs() / 1000.0;

            await CaptureCurrentPreviewFrameAsync(tempOutput);

            if (!File.Exists(tempOutput) || new FileInfo(tempOutput).Length < 100)
            {
                throw new IOException("Snapshot file was not created.");
            }

            if (File.Exists(output))
            {
                File.Delete(output);
            }

            File.Move(tempOutput, output);
            _tempFiles.Add(output);
            RuntimeLog.Info("CROP", $"Snapshot captured: {output} at {seconds:F3}s from {_videoPath}");
            await LoadSnapshotAsync(output);
            ShowSnapshotPanel();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Snapshot failed: {ex.Message}");
            SetWizardState(2, "Find HUD Frame", "Snapshot failed. Try another frame.");
        }
        finally
        {
            SetEnabled("SnapshotButton", true);
            SetContent("SnapshotButton", "START CROPPING");
        }
    }

    private async Task LoadSnapshotAsync(string path)
    {
        _snapshotPath = path;

        bool wantComposerBg = _composerBackgroundImage != null;

        // ISSUE_2: decode, scale and PNG-encode off the UI thread so the window stays
        // responsive on large (1440p/4K) snapshots. Only the Source assignments below,
        // after the await, touch UI controls (on the UI thread).
        var (snapshotBitmap, composerBitmap, composerPreviewPath) = await Task.Run(() =>
        {
            using (SKBitmap bitmap = SKBitmap.Decode(path) ?? throw new IOException("Could not decode snapshot."))
            {
                _snapshotWidth = bitmap.Width;
                _snapshotHeight = bitmap.Height;
                _originalResolution = $"{_snapshotWidth}x{_snapshotHeight}";
            }

            Bitmap snap;
            using (var snapStream = File.OpenRead(path))
                snap = new Bitmap(snapStream);

            Bitmap? bg = null;
            string? previewPath = null;
            if (wantComposerBg)
            {
                previewPath = CreateComposerBackgroundPreview(path);
                using var bgStream = File.OpenRead(previewPath);
                bg = new Bitmap(bgStream);
            }

            return (snap, bg, previewPath);
        });

        if (composerPreviewPath != null)
        {
            _tempFiles.Add(composerPreviewPath);
        }

        if (_snapshotImage != null)
        {
            _snapshotImage.Source = snapshotBitmap;
            _snapshotImage.Width = _snapshotWidth;
            _snapshotImage.Height = _snapshotHeight;
        }

        if (_sourceCanvas != null)
        {
            _sourceCanvas.Width = _snapshotWidth;
            _sourceCanvas.Height = _snapshotHeight;
        }

        if (composerBitmap != null && _composerBackgroundImage != null)
        {
            _composerBackgroundImage.Source = composerBitmap;
        }

        ClearSourceSelection();
        ClearMagicWandCandidates();
        SetWizardState(3, "Refine Box", $"Draw a HUD box on the {_originalResolution} snapshot.");
    }

    private async Task CaptureCurrentPreviewFrameAsync(string outputPath)
    {
        if (_videoHost?.IpcClient == null)
        {
            throw new InvalidOperationException("Video preview is not ready.");
        }

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        await _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
        await _videoHost.IpcClient.SendCommandAsync("screenshot-to-file", outputPath, "video");
        await WaitForFileAsync(outputPath, TimeSpan.FromSeconds(5));
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (File.Exists(path))
            {
                try
                {
                    if (new FileInfo(path).Length > 100)
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                }
            }

            await Task.Delay(50, CancellationToken.None);
        }

        throw new TimeoutException("Timed out waiting for the current preview frame snapshot.");
    }

    private string CreateComposerBackgroundPreview(string snapshotPath)
    {
        _paths.EnsureWritableDirectories();
        string output = IOPath.Combine(_paths.TempDirectory, $"crop_canvas_trick_{Guid.NewGuid():N}.png");

        using SKBitmap source = SKBitmap.Decode(snapshotPath) ?? throw new IOException("Could not decode snapshot.");
        using var internalBitmap = new SKBitmap(
            CoordinateConstants.InternalW,
            CoordinateConstants.InternalH,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using (var internalCanvas = new SKCanvas(internalBitmap))
        {
            internalCanvas.Clear(SKColors.Black);
            double scale = Math.Max(
                (double)CoordinateConstants.InternalW / source.Width,
                (double)CoordinateConstants.InternalH / source.Height);
            double scaledW = source.Width * scale;
            double scaledH = source.Height * scale;
            float left = (float)((CoordinateConstants.InternalW - scaledW) / 2.0);
            float top = (float)((CoordinateConstants.InternalH - scaledH) / 2.0);
            var dst = new SKRect(left, top, left + (float)scaledW, top + (float)scaledH);
            internalCanvas.DrawBitmap(source, dst);
            internalCanvas.Flush();
        }

        using var finalBitmap = new SKBitmap(
            CoordinateConstants.PortraitW,
            CoordinateConstants.PortraitH,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using (var finalCanvas = new SKCanvas(finalBitmap))
        {
            finalCanvas.Clear(SKColors.Black);
            var contentDst = new SKRect(
                0,
                CoordinateConstants.UIPaddingTop,
                CoordinateConstants.PortraitW,
                CoordinateConstants.PortraitH - CoordinateConstants.UIPaddingBottom);
            finalCanvas.DrawBitmap(internalBitmap, contentDst);

            using var paint = new SKPaint { Color = new SKColor(255, 0, 0, 64), Style = SKPaintStyle.Fill };
            finalCanvas.DrawRect(0, 0, CoordinateConstants.PortraitW, CoordinateConstants.UIPaddingTop, paint);
            finalCanvas.DrawRect(0, CoordinateConstants.PortraitH - CoordinateConstants.UIPaddingBottom, CoordinateConstants.PortraitW, CoordinateConstants.UIPaddingBottom, paint);

            finalCanvas.Flush();
        }

        using SKImage image = SKImage.FromBitmap(finalBitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream fs = File.OpenWrite(output);
        data.SaveTo(fs);
        return output;
    }

    private void ShowVideoPanel()
    {
        SetVisible("VideoPanel", true);
        SetVisible("SnapshotPanel", false);
        SetVisible("MagicWandButton", false);
        SetVisible("BackToVideoButton", false);
        if (!string.IsNullOrWhiteSpace(_videoPath))
        {
            SetWizardState(2, "Find HUD Frame", "Find a clear HUD frame and start cropping.");
        }
    }

    private void ShowSnapshotPanel()
    {
        SetVisible("VideoPanel", false);
        SetVisible("SnapshotPanel", true);
        SetVisible("MagicWandButton", true);
        SetVisible("BackToVideoButton", true);
        SetWizardState(3, "Refine Box", "Draw a source HUD box, choose a role, then add it.");
    }

    private void SourceCanvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_snapshotPath == null || _sourceCanvas == null)
        {
            return;
        }

        if (e.Source is Control control && control.Tag is CandidateSpec candidate)
        {
            SetSourceSelection(candidate.Rect, candidate.RoleKey);
            e.Handled = true;
            return;
        }

        Point p = ClampToSnapshot(e.GetPosition(_sourceCanvas));
        _sourceCanvas.Focus();
        _sourceSelectionStart = p;
        _isDrawingSourceSelection = true;
        EnsureSelectionRectangle();
        UpdateSelectionRect(new Rect(p, new Size(1, 1)));
        e.Pointer.Capture(_sourceCanvas);
        e.Handled = true;
    }

    private void SourceCanvas_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDrawingSourceSelection || _sourceCanvas == null)
        {
            return;
        }

        Point p = ClampToSnapshot(e.GetPosition(_sourceCanvas));
        Rect rect = NormalizeRect(_sourceSelectionStart, p);
        UpdateSelectionRect(rect);
        e.Handled = true;
    }

    private void SourceCanvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDrawingSourceSelection || _sourceCanvas == null)
        {
            return;
        }

        _isDrawingSourceSelection = false;
        e.Pointer.Capture(null);

        Point p = ClampToSnapshot(e.GetPosition(_sourceCanvas));
        SourceRect rect = ToSourceRect(NormalizeRect(_sourceSelectionStart, p));
        if (rect.Width < MinSelectionSize || rect.Height < MinSelectionSize)
        {
            ClearSourceSelection();
        }
        else
        {
            SetSourceSelection(rect, SuggestRole(rect).Key);
        }

        e.Handled = true;
    }

    private void EnsureSelectionRectangle()
    {
        if (_sourceCanvas == null || _selectionRect != null)
        {
            return;
        }

        _selectionRect = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.Parse("#2ecc71")),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(45, 46, 204, 113)),
            IsHitTestVisible = false,
            ZIndex = 500
        };
        _sourceCanvas.Children.Add(_selectionRect);
    }

    private void UpdateSelectionRect(Rect rect)
    {
        EnsureSelectionRectangle();
        if (_selectionRect == null)
        {
            return;
        }

        rect = rect.Intersect(new Rect(0, 0, _snapshotWidth, _snapshotHeight));
        Canvas.SetLeft(_selectionRect, rect.X);
        Canvas.SetTop(_selectionRect, rect.Y);
        _selectionRect.Width = Math.Max(1, rect.Width);
        _selectionRect.Height = Math.Max(1, rect.Height);
    }

    private void SetSourceSelection(SourceRect rect, string? roleKey = null)
    {
        _sourceSelection = rect;
        UpdateSelectionRect(new Rect(rect.X, rect.Y, rect.Width, rect.Height));
        SetEnabled("AddSelectionButton", true);
        if (_selectionInfo != null)
        {
            _selectionInfo.Text = $"{rect.Width} x {rect.Height} at {rect.X}, {rect.Y}";
        }

        HudRole role = roleKey != null && RoleByKey.TryGetValue(roleKey, out var found)
            ? found
            : SuggestRole(rect);
        if (_roleTextBox != null)
        {
            _roleTextBox.Text = role.DisplayName;
        }
    }

    private void ClearSourceSelection()
    {
        _sourceSelection = null;
        if (_selectionRect != null && _sourceCanvas != null)
        {
            _sourceCanvas.Children.Remove(_selectionRect);
        }

        _selectionRect = null;
        SetEnabled("AddSelectionButton", false);
        if (_selectionInfo != null)
        {
            _selectionInfo.Text = "Draw a box around a HUD element, then assign its role.";
        }
    }

    private async Task AddCurrentSelection()
    {
        if (_sourceSelection == null || string.IsNullOrWhiteSpace(_snapshotPath))
        {
            return;
        }

        string roleName = _roleTextBox?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(roleName))
        {
            SetStatus("Please enter a name for the HUD element.");
            return;
        }

        string roleKey = roleName.ToLowerInvariant().Replace(" ", "_");
        HudRole role = RoleByKey.TryGetValue(roleKey, out var existingRole) 
            ? existingRole 
            : new HudRole(roleKey, roleName, 50, -1, -1);

        SourceRect sourceRect = ClampSourceRect(_sourceSelection.Value);
        if (sourceRect.Width < MinSelectionSize || sourceRect.Height < MinSelectionSize)
        {
            SetStatus("Selection is too small.");
            return;
        }

        try
        {
            var contentRect = CoordinateMath.TransformToContentAreaInt(
                (sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height),
                _originalResolution);
            if (contentRect.w < 2 || contentRect.h < 2)
            {
                SetStatus("Selection does not map to a visible portrait area.");
                return;
            }

            // ISSUE_2: the crop decodes the full (possibly 4K) snapshot; run it off the UI thread.
            string snapshotPath = _snapshotPath;
            string cropPath = await Task.Run(() => CropSnapshotRegionForExportPreview(snapshotPath, sourceRect, role.Key));
            _tempFiles.Add(cropPath);

            CropEditorItem? existing = _items.FirstOrDefault(i => i.RoleKey == role.Key);
            if (existing != null)
            {
                RemoveItem(existing);
            }

            var initialSize = QuantizeItemSize(sourceRect, contentRect.w, role.Key);
            int width = initialSize.width;
            int height = initialSize.height;

            int initialX = role.DefaultX >= 0 ? (int)role.DefaultX : contentRect.x;
            // ISSUE_7: contentRect.y is content-space (0..1620); overlay positions are
            // portrait-space (150..1770). Add the 150px text-strip offset so the new
            // element spawns exactly over its source location.
            int initialY = role.DefaultY >= 0 ? (int)role.DefaultY : contentRect.y + CoordinateConstants.UIPaddingTop;

            (int x, int y) = ClampOverlay(initialX, initialY, width, height);

            int z = role.DefaultZ;
            if (_items.Any(i => i.Z == z))
            {
                z = _items.Max(i => i.Z) + 1;
            }

            var item = CreateItem(new ItemSnapshot(role.Key, role.DisplayName, sourceRect, cropPath, x, y, width, height, z));
            _items.Add(item);
            SelectItem(item);
            RefreshLayerList();
            MarkDirty();
            PushHistory();
            RuntimeLog.Info("CROP", $"Added HUD element: {role.DisplayName} (role={role.Key}, source={sourceRect.Width}x{sourceRect.Height} at ({sourceRect.X},{sourceRect.Y}), z={z})");
            ClearSourceSelection();
            ClearMagicWandCandidates();
            SetWizardState(4, "Portrait Composer", $"Adjust {role.DisplayName}, then finish and save.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Add selection failed: {ex.Message}");
            SetStatus("Could not add that selection.");
        }
    }

    private string CropSnapshotRegionForExportPreview(string snapshotPath, SourceRect sourceRect, string roleKey)
    {
        var contentRect = CoordinateMath.TransformToContentAreaInt(
            (sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height),
            _originalResolution);
        var exportRect = CoordinateMath.InverseTransformFromContentAreaInt(
            (contentRect.x, contentRect.y, contentRect.w, contentRect.h),
            _originalResolution,
            HudConfig.CropDriftType(roleKey));
        return CropSnapshotRegion(snapshotPath, new SourceRect(exportRect.x, exportRect.y, exportRect.w, exportRect.h));
    }

    private string CropSnapshotRegion(string snapshotPath, SourceRect rect)
    {
        _paths.EnsureWritableDirectories();
        string output = IOPath.Combine(_paths.TempDirectory, $"crop_item_{Guid.NewGuid():N}.png");

        using SKBitmap source = SKBitmap.Decode(snapshotPath) ?? throw new IOException("Could not decode snapshot.");
        SourceRect clamped = ClampSourceRect(rect, source.Width, source.Height);

        using var target = new SKBitmap(clamped.Width, clamped.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.Transparent);

        var src = new SKRect(clamped.X, clamped.Y, clamped.X + clamped.Width, clamped.Y + clamped.Height);
        var dst = new SKRect(0, 0, clamped.Width, clamped.Height);
        canvas.DrawBitmap(source, src, dst);
        canvas.Flush();

        using SKImage image = SKImage.FromBitmap(target);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream fs = File.OpenWrite(output);
        data.SaveTo(fs);
        return output;
    }

    private CropEditorItem CreateItem(ItemSnapshot snapshot)
    {
        var root = new Canvas
        {
            Width = snapshot.Width,
            Height = snapshot.Height,
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Focusable = true,
            Tag = null,
            ZIndex = snapshot.Z
        };
        Avalonia.Automation.AutomationProperties.SetName(root, $"{snapshot.DisplayName} crop item");

        var image = new Image
        {
            Width = snapshot.Width,
            Height = snapshot.Height,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false
        };

        if (File.Exists(snapshot.CropImagePath))
        {
            using var fs = File.OpenRead(snapshot.CropImagePath);
            image.Source = new Bitmap(fs);
        }

        var border = new Rectangle
        {
            Width = snapshot.Width,
            Height = snapshot.Height,
            Stroke = Brushes.Black,
            StrokeThickness = 3,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };

        var tlHandle = CreateHandle(StandardCursorType.SizeAll);
        var brHandle = CreateHandle(StandardCursorType.SizeAll);

        var labelText = new TextBlock
        {
            Text = "",
            Foreground = Brushes.White,
            FontSize = Infrastructure.ThemeManager.ScaledFontSize(18),
            FontWeight = FontWeight.Bold,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(210, 0, 0, 0)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3),
            Child = labelText,
            IsHitTestVisible = false
        };

        root.Children.Add(image);
        root.Children.Add(border);
        root.Children.Add(tlHandle);
        root.Children.Add(brHandle);
        root.Children.Add(label);

        var item = new CropEditorItem
        {
            RoleKey = snapshot.RoleKey,
            DisplayName = snapshot.DisplayName,
            SourceRect = snapshot.SourceRect,
            CropImagePath = snapshot.CropImagePath,
            X = snapshot.X,
            Y = snapshot.Y,
            Width = snapshot.Width,
            Height = snapshot.Height,
            Z = snapshot.Z,
            Root = root,
            Image = image,
            Border = border,
            TopLeftHandle = tlHandle,
            BottomRightHandle = brHandle,
            LabelHost = label,
            LabelText = labelText
        };

        root.Tag = item;
        tlHandle.Tag = ResizeHandle.TopLeft;
        brHandle.Tag = ResizeHandle.BottomRight;

        root.PointerPressed += Item_PointerPressed;
        root.PointerMoved += Item_PointerMoved;
        root.PointerReleased += Item_PointerReleased;

        if (_portraitCanvas != null)
        {
            _portraitCanvas.Children.Add(root);
        }

        ApplyItemLayout(item);
        UpdateItemVisual(item);
        return item;
    }

    private Rectangle CreateHandle(StandardCursorType cursor)
    {
        return new Rectangle
        {
            Width = HandleSize,
            Height = HandleSize,
            Fill = Brushes.Red,
            Stroke = Brushes.White,
            StrokeThickness = 2,
            Cursor = new Cursor(cursor),
            IsVisible = false,
            ZIndex = 20
        };
    }

    private void Item_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Canvas root || root.Tag is not CropEditorItem item || _portraitCanvas == null)
        {
            return;
        }

        SelectItem(item);
        root.Focus();
        _activeEditItem = item;
        _editStartSnapshot = CaptureSnapshot();
        _editPointerStart = e.GetPosition(_portraitCanvas);
        _editStartX = item.X;
        _editStartY = item.Y;
        _editStartWidth = item.Width;
        _editStartHeight = item.Height;

        if (e.Source is Control source && source.Tag is ResizeHandle handle)
        {
            _composerEditMode = handle == ResizeHandle.TopLeft ? ComposerEditMode.ResizeTopLeft : ComposerEditMode.ResizeBottomRight;
        }
        else
        {
            _composerEditMode = ComposerEditMode.Drag;
        }

        if (this.FindControl<Grid>("RuleOfThirdsGrid") is Grid grid)
            grid.Opacity = 1;

        e.Pointer.Capture(root);
        e.Handled = true;
    }

    private void Item_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_activeEditItem == null || _composerEditMode == ComposerEditMode.None || _portraitCanvas == null)
        {
            return;
        }

        Point p = e.GetPosition(_portraitCanvas);
        double dx = p.X - _editPointerStart.X;
        double dy = p.Y - _editPointerStart.Y;

        if (_composerEditMode == ComposerEditMode.Drag)
        {
            double x = _editStartX + dx;
            double y = _editStartY + dy;
            (x, y) = SnapPosition(_activeEditItem, x, y, _activeEditItem.Width, _activeEditItem.Height);
            (int ix, int iy) = ClampOverlay(x, y, _activeEditItem.Width, _activeEditItem.Height);
            _activeEditItem.X = ix;
            _activeEditItem.Y = iy;
        }
        else if (_composerEditMode == ComposerEditMode.ResizeBottomRight)
        {
            ResizeFromBottomRight(_activeEditItem, dx);
        }
        else if (_composerEditMode == ComposerEditMode.ResizeTopLeft)
        {
            ResizeFromTopLeft(_activeEditItem, dx);
        }

        ApplyItemLayout(_activeEditItem);
        _dirty = true;
        RefreshActionButtons();
        e.Handled = true;
    }

    private void Item_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_activeEditItem == null)
        {
            return;
        }

        e.Pointer.Capture(null);
        ClearGuides();

        if (this.FindControl<Grid>("RuleOfThirdsGrid") is Grid grid)
            grid.Opacity = 0;

        EditorSnapshot current = CaptureSnapshot();
        if (_editStartSnapshot != null && !SnapshotsEqual(_editStartSnapshot, current))
        {
            MarkDirty();
            PushHistory(current);
            RefreshLayerList();
        }

        _activeEditItem = null;
        _composerEditMode = ComposerEditMode.None;
        _editStartSnapshot = null;
        e.Handled = true;
    }

    private void ResizeFromBottomRight(CropEditorItem item, double dx)
    {
        double aspect = _editStartHeight / Math.Max(1, _editStartWidth);
        double width = Math.Max(MinItemSize, _editStartWidth + dx);
        double height = width * aspect;

        if (_editStartX + width > PortraitWidth)
        {
            width = PortraitWidth - _editStartX;
            height = width * aspect;
        }

        if (_editStartY + height > ContentBottom)
        {
            height = ContentBottom - _editStartY;
            width = height / aspect;
        }

        var quantized = QuantizeItemSize(item.SourceRect, width, item.RoleKey);
        item.Width = quantized.width;
        item.Height = quantized.height;
    }

    private void ResizeFromTopLeft(CropEditorItem item, double dx)
    {
        double aspect = _editStartHeight / Math.Max(1, _editStartWidth);
        double anchorRight = _editStartX + _editStartWidth;
        double anchorBottom = _editStartY + _editStartHeight;

        double width = Math.Max(MinItemSize, _editStartWidth - dx);
        double height = width * aspect;
        double x = anchorRight - width;
        double y = anchorBottom - height;

        if (x < 0)
        {
            width = anchorRight;
            height = width * aspect;
            x = 0;
            y = anchorBottom - height;
        }

        if (y < ContentTop)
        {
            height = anchorBottom - ContentTop;
            width = height / aspect;
            y = ContentTop;
            x = anchorRight - width;
        }

        var quantized = QuantizeItemSize(item.SourceRect, width, item.RoleKey);
        item.Width = quantized.width;
        item.Height = quantized.height;
        item.X = Math.Max(0, RoundPixel(anchorRight - item.Width));
        item.Y = Math.Max((int)ContentTop, RoundPixel(anchorBottom - item.Height));
    }

    private void ApplyItemLayout(CropEditorItem item)
    {
        var quantized = QuantizeItemSize(item.SourceRect, item.Width, item.RoleKey);
        item.Width = quantized.width;
        item.Height = quantized.height;
        (item.X, item.Y) = ClampOverlay(item.X, item.Y, item.Width, item.Height);

        item.Root.Width = item.Width;
        item.Root.Height = item.Height;
        item.Root.ZIndex = item.Z;
        Canvas.SetLeft(item.Root, item.X);
        Canvas.SetTop(item.Root, item.Y);

        item.Image.Width = item.Width;
        item.Image.Height = item.Height;
        item.Border.Width = item.Width;
        item.Border.Height = item.Height;

        Canvas.SetLeft(item.TopLeftHandle, -HandleSize / 2);
        Canvas.SetTop(item.TopLeftHandle, -HandleSize / 2);
        Canvas.SetLeft(item.BottomRightHandle, item.Width - HandleSize / 2);
        Canvas.SetTop(item.BottomRightHandle, item.Height - HandleSize / 2);

        double labelWidth = Math.Clamp(Math.Max(96, item.Width), 96, PortraitWidth - 20);
        double labelLeft = (item.Width - labelWidth) / 2;
        labelLeft = Math.Clamp(labelLeft, -item.X + 10, PortraitWidth - item.X - labelWidth - 10);
        item.LabelHost.Width = labelWidth;
        Canvas.SetLeft(item.LabelHost, labelLeft);
        Canvas.SetTop(item.LabelHost, item.Y > PortraitHeight / 2 ? -34 : item.Height + 10);
        item.LabelText.Text = item.DisplayName.ToUpperInvariant();
    }

    private (int width, int height, double scale) QuantizeItemSize(SourceRect sourceRect, double desiredWidth, string? roleKey = null)
    {
        // ISSUE_1: The scale denominator MUST be the same crops_1080p width that
        // SaveConfigAsync stores, because the exporter computes
        // rw = storedCropW × scale × backendScale (MobileFilterBuilder.Build).
        // Using any other width base makes the exported mask a different size
        // than the preview showed.
        var contentRect = CoordinateMath.TransformToContentAreaInt(
            (sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height),
            _originalResolution);

        int contentW = Math.Max(2, contentRect.w);
        int contentH = Math.Max(2, contentRect.h);

        double maxDesW = Math.Max(MinItemSize, desiredWidth);
        // ISSUE_10: HudConfig.Sanitize rounds the stored scale to 4 decimals on load,
        // so preview math must use the identical 4-decimal value or EvenCeil can flip.
        double quantizedScale = Math.Round(Math.Max(0.0001, maxDesW / contentW), 4, MidpointRounding.AwayFromZero);
        Frac scaleFrac = Frac.FromDouble(quantizedScale);

        Frac backendScale = CoordinateConstants.BackendScale;
        int rw = Math.Max(2, CanvasMath.EvenCeil(new Frac(contentW, 1) * scaleFrac * backendScale));
        // ISSUE_1 (pixel-perfect): height must use the IDENTICAL basis the exporter uses
        // (MobileFilterBuilder.Build: rh = EvenCeil(contentH * scale * backendScale)).
        // Deriving rh from the already-even-ceiled rw diverged from export by up to ~2px.
        int rh = Math.Max(2, CanvasMath.EvenCeil(new Frac(contentH, 1) * scaleFrac * backendScale));

        int width = CoordinateMath.ScaleRound(new Frac(rw, 1) / backendScale);
        int height = CoordinateMath.ScaleRound(new Frac(rh, 1) / backendScale);

        return (width, height, quantizedScale);
    }

    private void SelectItem(CropEditorItem? item, bool updateLayerList = true)
    {
        _selectedItem = item;
        foreach (CropEditorItem editorItem in _items)
        {
            UpdateItemVisual(editorItem);
        }

        if (updateLayerList && _layerList != null)
        {
            _suppressLayerSelection = true;
            _layerList.SelectedItem = _layers.FirstOrDefault(l => item != null && l.RoleKey == item.RoleKey);
            _suppressLayerSelection = false;
        }

        RefreshActionButtons();
    }

    private void UpdateItemVisual(CropEditorItem item)
    {
        bool selected = ReferenceEquals(item, _selectedItem);
        item.Border.Stroke = selected ? Brushes.Gold : Brushes.Black;
        item.Border.StrokeThickness = selected ? 4 : 3;
        item.TopLeftHandle.IsVisible = selected;
        item.BottomRightHandle.IsVisible = selected;
        item.LabelHost.Background = selected
            ? new SolidColorBrush(Color.FromArgb(220, 113, 63, 18))
            : new SolidColorBrush(Color.FromArgb(210, 0, 0, 0));
    }

    private void DeleteSelectedItem()
    {
        if (_selectedItem == null)
        {
            return;
        }

        RemoveItem(_selectedItem);
        SelectItem(null);
        RefreshLayerList();
        MarkDirty();
        PushHistory();
    }

    private void RemoveItem(CropEditorItem item)
    {
        if (_portraitCanvas != null)
        {
            _portraitCanvas.Children.Remove(item.Root);
        }

        _items.Remove(item);
    }

    private void MoveSelectedLayer(int delta)
    {
        if (_selectedItem == null)
        {
            return;
        }

        _selectedItem.Z += delta;
        if (_selectedItem.Z < 1)
        {
            _selectedItem.Z = 1;
        }

        ApplyItemLayout(_selectedItem);
        RefreshLayerList();
        MarkDirty();
        PushHistory();
    }

    private void RefreshLayerList()
    {
        string? selectedKey = _selectedItem?.RoleKey;
        _layers.Clear();
        foreach (CropEditorItem item in _items.OrderByDescending(i => i.Z).ThenBy(i => i.DisplayName))
        {
            _layers.Add(new LayerEntry(item.RoleKey, item.DisplayName, item.Z));
        }

        if (_layerList != null && selectedKey != null)
        {
            _layerList.SelectedItem = _layers.FirstOrDefault(l => l.RoleKey == selectedKey);
        }

        RefreshActionButtons();
    }

    private async Task LoadExistingPlaceholdersAsync()
    {
        if (_portraitCanvas == null)
        {
            return;
        }

        foreach (Control control in _placeholderControls)
        {
            _portraitCanvas.Children.Remove(control);
        }
        _placeholderControls.Clear();

        try
        {
            JsonObject config = await new CropConfigStore(_paths).LoadAsync();
            JsonObject crops = EnsureObject(config, "crops_1080p");
            JsonObject scales = EnsureObject(config, "scales");
            JsonObject overlays = EnsureObject(config, "overlays");
            JsonObject zOrders = EnsureObject(config, "z_orders");
            bool visible = this.FindControl<CheckBox>("ShowPlaceholders")?.IsChecked == true;
            byte alpha = (byte)Math.Round(this.FindControl<Slider>("PlaceholderOpacitySlider")?.Value ?? 95);

            foreach (HudRole role in Roles)
            {
                if (crops[role.Key] is not JsonArray crop || crop.Count < 4 ||
                    overlays[role.Key] is not JsonObject overlay)
                {
                    continue;
                }

                int w = ReadInt(crop[0], 0);
                int h = ReadInt(crop[1], 0);
                if (w <= 1 || h <= 1)
                {
                    continue;
                }

                double scale = ReadDouble(scales[role.Key], 1.0);
                int scaledW = Math.Max(2, CoordinateMath.ScaleRound(Frac.FromDouble(w * scale)));
                int scaledH = Math.Max(2, CoordinateMath.ScaleRound(Frac.FromDouble(h * scale)));
                double x = ReadDouble(overlay["x"], role.DefaultX);
                double y = ReadDouble(overlay["y"], role.DefaultY);
                int z = ReadInt(zOrders[role.Key], role.DefaultZ);

                var rect = new Rectangle
                {
                    Width = scaledW,
                    Height = scaledH,
                    Fill = new SolidColorBrush(Color.FromArgb(alpha, 0, 255, 0)),
                    Stroke = new SolidColorBrush(Color.Parse("#22c55e")),
                    StrokeThickness = 2,
                    IsHitTestVisible = false,
                    IsVisible = visible,
                    ZIndex = Math.Max(1, z)
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                _portraitCanvas.Children.Add(rect);
                _placeholderControls.Add(rect);

                var label = new TextBlock
                {
                    Text = role.DisplayName,
                    Foreground = Brushes.LightGreen,
                    FontSize = Infrastructure.ThemeManager.ScaledFontSize(13),
                    FontWeight = FontWeight.Bold,
                    IsHitTestVisible = false,
                    IsVisible = visible,
                    ZIndex = Math.Max(1, z) + 1
                };
                Canvas.SetLeft(label, x);
                Canvas.SetTop(label, Math.Max(ContentTop, y - 20));
                _portraitCanvas.Children.Add(label);
                _placeholderControls.Add(label);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Failed to load placeholders: {ex.Message}");
        }
    }

    private void UpdatePlaceholderOpacity(byte alpha)
    {
        foreach (Control control in _placeholderControls)
        {
            if (control is Rectangle rect)
            {
                rect.Fill = new SolidColorBrush(Color.FromArgb(alpha, 0, 255, 0));
            }
        }
    }

    private void ShowMagicWandCandidates()
    {
        if (_sourceCanvas == null || _snapshotPath == null)
        {
            SetStatus("Take a snapshot first.");
            return;
        }

        ClearMagicWandCandidates();

        CandidateSpec[] candidates =
        [
            CandidateFromRatio("stats", 0.65, 0.02, 0.32, 0.28),
            CandidateFromRatio("normal_hp", 0.02, 0.72, 0.36, 0.12),
            CandidateFromRatio("loot", 0.58, 0.72, 0.38, 0.18),
            CandidateFromRatio("team", 0.02, 0.06, 0.25, 0.30),
            CandidateFromRatio("spectating", 0.47, 0.05, 0.08, 0.08),
            CandidateFromRatio("boss_hp", 0.34, 0.04, 0.32, 0.08),
        ];

        foreach (CandidateSpec candidate in candidates)
        {
            var rect = new Rectangle
            {
                Width = candidate.Rect.Width,
                Height = candidate.Rect.Height,
                Stroke = new SolidColorBrush(Color.Parse("#e91e63")),
                StrokeThickness = 3,
                Fill = new SolidColorBrush(Color.FromArgb(24, 233, 30, 99)),
                Tag = candidate,
                Cursor = new Cursor(StandardCursorType.Hand),
                ZIndex = 450
            };

            Canvas.SetLeft(rect, candidate.Rect.X);
            Canvas.SetTop(rect, candidate.Rect.Y);
            _sourceCanvas.Children.Add(rect);
            _candidateControls.Add(rect);
        }

        SetWizardState(3, "Refine Box", "Magic Wand candidates shown. Click a box or draw manually.");
    }

    private CandidateSpec CandidateFromRatio(string roleKey, double x, double y, double w, double h)
    {
        var rect = new SourceRect(
            (int)Math.Round(_snapshotWidth * x),
            (int)Math.Round(_snapshotHeight * y),
            (int)Math.Round(_snapshotWidth * w),
            (int)Math.Round(_snapshotHeight * h));
        return new CandidateSpec(roleKey, ClampSourceRect(rect));
    }

    private void ClearMagicWandCandidates()
    {
        if (_sourceCanvas == null)
        {
            _candidateControls.Clear();
            return;
        }

        foreach (Control control in _candidateControls)
        {
            _sourceCanvas.Children.Remove(control);
        }
        _candidateControls.Clear();
    }

    private async Task SaveAndReturnAsync(object? sender)
    {
        Button? button = sender as Button;
        if (button != null)
        {
            button.IsEnabled = false;
        }

        var thinkingOverlay = this.FindControl<Grid>("ThinkingOverlay");
        if (thinkingOverlay != null) thinkingOverlay.IsVisible = true;
        
        await Task.Delay(100);

        bool saved = await SaveConfigAsync();

        if (thinkingOverlay != null) thinkingOverlay.IsVisible = false;

        if (saved)
        {
            var summaryOverlay = this.FindControl<Grid>("SummaryOverlay");
            if (summaryOverlay != null)
            {
                var summaryContent = this.FindControl<StackPanel>("SummaryContent");
                if (summaryContent != null)
                {
                    summaryContent.Children.Clear();
                    var headerModBorder = new Border { Background = SolidColorBrush.Parse("#1e10b981"), Padding = new Thickness(8), CornerRadius = new CornerRadius(4) };
                    headerModBorder.Child = new TextBlock { Text = "  MODIFIED ELEMENTS", Foreground = SolidColorBrush.Parse("#10b981"), FontWeight = FontWeight.Bold, FontSize = Infrastructure.ThemeManager.ScaledFontSize(14) };
                    summaryContent.Children.Add(headerModBorder);
                    foreach(var item in _items)
                    {
                        summaryContent.Children.Add(new TextBlock { Text = $"  ✓  {item.DisplayName}", Foreground = SolidColorBrush.Parse("#94a3b8"), FontSize = Infrastructure.ThemeManager.ScaledFontSize(16) });
                    }
                    
                    var existingKeys = _items.Select(x => x.RoleKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var untouched = Roles.Where(r => !existingKeys.Contains(r.Key)).ToList();
                    
                    if (untouched.Count > 0)
                    {
                        summaryContent.Children.Add(new Border { Height = 20 });
                        var headerUnBorder = new Border { Background = SolidColorBrush.Parse("#1e9ca3af"), Padding = new Thickness(8), CornerRadius = new CornerRadius(4) };
                        headerUnBorder.Child = new TextBlock { Text = "  UNTOUCHED (DEFAULTS)", Foreground = SolidColorBrush.Parse("#9ca3af"), FontWeight = FontWeight.Bold, FontSize = Infrastructure.ThemeManager.ScaledFontSize(14) };
                        summaryContent.Children.Add(headerUnBorder);
                        foreach(var u in untouched)
                        {
                            summaryContent.Children.Add(new TextBlock { Text = $"  •  {u.DisplayName}", Foreground = SolidColorBrush.Parse("#9ca3af"), FontSize = Infrastructure.ThemeManager.ScaledFontSize(15) });
                        }
                    }
                }
                
                summaryOverlay.IsVisible = true;
                await Task.Delay(2500);
            }
            await ReturnToMainAppAsync();
            return;
        }

        if (button != null)
        {
            button.IsEnabled = true;
            button.Content = "FINISH & SAVE";
        }
        RefreshActionButtons();
    }

    private async Task<bool> SaveConfigAsync()
    {
        if (_items.Count == 0)
        {
            SetStatus("No HUD elements are currently placed.");
            return false;
        }

        try
        {
            RuntimeLog.Info("CROP", "Saving crop coordinates.");
            var store = new CropConfigStore(_paths);
            
            try
            {
                string confPath = _paths.CropCoordinatesFile;
                if (System.IO.File.Exists(confPath))
                {
                    for (int i = 4; i >= 1; i--)
                    {
                        string oldB = $"{confPath}.bak{i}";
                        string newB = $"{confPath}.bak{i + 1}";
                        if (System.IO.File.Exists(oldB)) System.IO.File.Move(oldB, newB, true);
                    }
                    System.IO.File.Copy(confPath, $"{confPath}.bak1", true);
                    RuntimeLog.Info("CROP", $"Rotation backup created: {confPath}.bak1");
                }
            }
            catch (Exception backupErr)
            {
                RuntimeLog.Info("CROP", $"Failed to create rotation backup: {backupErr.Message}");
            }

            JsonObject config = await store.LoadAsync();

            JsonObject crops = EnsureObject(config, "crops_1080p");
            JsonObject scales = EnsureObject(config, "scales");
            JsonObject overlays = EnsureObject(config, "overlays");
            JsonObject zOrders = EnsureObject(config, "z_orders");

            foreach (CropEditorItem item in _items)
            {
                var quantized = QuantizeItemSize(item.SourceRect, item.Width, item.RoleKey);
                item.Width = quantized.width;
                item.Height = quantized.height;
                ApplyItemLayout(item);

                var transformed = CoordinateMath.TransformToContentAreaInt(
                    (item.SourceRect.X, item.SourceRect.Y, item.SourceRect.Width, item.SourceRect.Height),
                    _originalResolution);

                int cropW = Math.Max(2, transformed.w);
                int cropH = Math.Max(2, transformed.h);
                double scale = quantized.scale;
                (int ox, int oy) = ClampOverlay(item.X, item.Y, item.Width, item.Height);

                crops[item.RoleKey] = new JsonArray(cropW, cropH, transformed.x, transformed.y);
                scales[item.RoleKey] = scale;
                overlays[item.RoleKey] = new JsonObject
                {
                    ["x"] = ox,
                    ["y"] = oy
                };
                zOrders[item.RoleKey] = item.Z;
                RuntimeLog.Info("CROP", $"  Save item: {item.RoleKey} crop=[{cropW}x{cropH}+{transformed.x}+{transformed.y}] scale={scale:F4} overlay=({ox},{oy}) z={item.Z}");
            }

            RuntimeLog.Info("CROP", $"Saving {_items.Count} item(s) to config (schema v{CropConfigDefaults.SchemaVersion}).");
            config["schema_version"] = CropConfigDefaults.SchemaVersion;
            config["coordinate_space"] = CropConfigDefaults.CoordinateSpace;
            await store.SaveAsync(config);

            FortniteVideoSoftware.App.Infrastructure.MaskOverlayManager.SyncActiveProfileFromCurrentConfig();

            _dirty = false;
            RefreshActionButtons();
            RuntimeLog.Success("CROP", "Saved crop coordinates successfully.");
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", ex);
            SetStatus("Save failed. See runtime log.");
            return false;
        }
    }

    private async Task ReturnToMainAppAsync()
    {
        try
        {
            var store = new StateTransferStore(_paths);
            await store.UpdatePropertiesAsync(new JsonObject
            {
                ["returned_from_crop_tool"] = true
            });
            RuntimeLog.Info("CROP", "Returning to Main app.");
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "FortniteVideoSoftware.exe";
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath) { UseShellExecute = false });
            if (p != null)
            {
                _ = Task.Run(() =>
                {
                    try { p.WaitForInputIdle(5000); Task.Delay(500).Wait(); } catch { }
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => Close());
                });
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Close());
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", "Error returning to main window: " + ex.Message);
            Close();
        }
    }

    private void ResetWorkingState()
    {
        ClearSourceSelection();
        ClearMagicWandCandidates();

        foreach (CropEditorItem item in _items.ToList())
        {
            RemoveItem(item);
        }
        SelectItem(null);
        RefreshLayerList();
        _dirty = false;
        InitializeHistory();
        RefreshActionButtons();
        SetStatus("Working crop items cleared.");
    }

    private void Undo()
    {
        if (_undoStack.Count <= 1)
        {
            return;
        }

        EditorSnapshot current = _undoStack.Pop();
        _redoStack.Push(current);
        RestoreSnapshot(_undoStack.Peek());
        _dirty = true;
        RefreshUndoRedoButtons();
    }

    private void Redo()
    {
        if (_redoStack.Count == 0)
        {
            return;
        }

        EditorSnapshot snapshot = _redoStack.Pop();
        _undoStack.Push(snapshot);
        RestoreSnapshot(snapshot);
        _dirty = true;
        RefreshUndoRedoButtons();
    }

    private void PushHistory()
    {
        PushHistory(CaptureSnapshot());
    }

    private void PushHistory(EditorSnapshot snapshot)
    {
        if (_restoringSnapshot)
        {
            return;
        }

        if (_undoStack.Count == 0 || !SnapshotsEqual(_undoStack.Peek(), snapshot))
        {
            _undoStack.Push(snapshot);
            _redoStack.Clear();
        }

        RefreshUndoRedoButtons();
    }

    private EditorSnapshot CaptureSnapshot()
    {
        return new EditorSnapshot(_items
            .OrderBy(i => i.RoleKey, StringComparer.Ordinal)
            .Select(i => new ItemSnapshot(i.RoleKey, i.DisplayName, i.SourceRect, i.CropImagePath, i.X, i.Y, i.Width, i.Height, i.Z))
            .ToList());
    }

    private void RestoreSnapshot(EditorSnapshot snapshot)
    {
        _restoringSnapshot = true;
        try
        {
            foreach (CropEditorItem item in _items.ToList())
            {
                RemoveItem(item);
            }

            foreach (ItemSnapshot itemSnapshot in snapshot.Items)
            {
                _items.Add(CreateItem(itemSnapshot));
            }

            SelectItem(null);
            RefreshLayerList();
            MarkDirty(pushHistory: false);
        }
        finally
        {
            _restoringSnapshot = false;
        }
    }

    private static bool SnapshotsEqual(EditorSnapshot a, EditorSnapshot b)
    {
        if (a.Items.Count != b.Items.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Items.Count; i++)
        {
            ItemSnapshot x = a.Items[i];
            ItemSnapshot y = b.Items[i];
            if (x.RoleKey != y.RoleKey ||
                x.CropImagePath != y.CropImagePath ||
                !x.SourceRect.Equals(y.SourceRect) ||
                x.X != y.X ||
                x.Y != y.Y ||
                x.Width != y.Width ||
                x.Height != y.Height ||
                x.Z != y.Z)
            {
                return false;
            }
        }

        return true;
    }

    private void MarkDirty(bool pushHistory = false)
    {
        _dirty = true;
        if (pushHistory)
        {
            PushHistory();
        }
        RefreshActionButtons();
    }

    private void RefreshActionButtons()
    {
        SetEnabled("SaveButton", _items.Count > 0 && _dirty);
        SetEnabled("DeleteSelectedButton", _selectedItem != null);
        SetEnabled("RaiseButton", _selectedItem != null);
        SetEnabled("LowerButton", _selectedItem != null);
        RefreshUndoRedoButtons();
    }

    private void RefreshUndoRedoButtons()
    {
        SetEnabled("UndoButton", _undoStack.Count > 1);
        SetEnabled("RedoButton", _redoStack.Count > 0);
    }

    private async Task SeekInternal(double time)
    {
        if (_isSeeking)
        {
            _nextSeekTarget = time;
            return;
        }
        _isSeeking = true;
        try
        {
            if (_videoHost?.IpcClient != null)
            {
                await _videoHost.IpcClient.SendCommandAsync("seek", time, "absolute");
            }
        }
        finally
        {
            _isSeeking = false;
            if (_nextSeekTarget.HasValue)
            {
                double target = _nextSeekTarget.Value;
                _nextSeekTarget = null;
                _ = SeekInternal(target);
            }
        }
    }

    private void SeekTimelineFromPointer(PointerPressedEventArgs e, Canvas timelineCanvas, Slider timelineSlider)
    {
        if (e.Handled) return;

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
        ShowPlayheadBadge(targetTime, sliderValue);
        e.Handled = true;
    }

    private void ShowPlayheadBadge(double timeSeconds, double sliderValuePercentage)
    {
        var badge = this.FindControl<Avalonia.Controls.Border>("PlayheadBadge");
        var text = this.FindControl<Avalonia.Controls.TextBlock>("PlayheadBadgeText");
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("CropTimelineMarkersCanvas");

        if (badge != null && text != null && canvas != null)
        {
            text.Text = FormatTime(timeSeconds * 1000.0);
            double canvasWidth = canvas.Bounds.Width;
            double x = (sliderValuePercentage / 100.0) * canvasWidth;
            Avalonia.Controls.Canvas.SetLeft(badge, x - 25);
            badge.Opacity = 1.0;

            if (_playheadBadgeTimer == null)
            {
                _playheadBadgeTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _playheadBadgeTimer.Tick += (s, ev) =>
                {
                    _playheadBadgeTimer.Stop();
                    badge.Opacity = 0.0;
                };
            }
            _playheadBadgeTimer.Stop();
            _playheadBadgeTimer.Start();
        }
    }

    private void UpdateTimelineUi()
    {
        if (_timelineSlider == null)
        {
            return;
        }

        double currentMs = GetCurrentTimeMs();
        if (_durationMs <= 0)
        {
            _durationMs = Math.Max(0, (_videoHost?.IpcClient?.Duration ?? 0) * 1000.0);
        }

        _isTimerUpdatingSlider = true;
        try
        {
            if (_durationMs > 0)
            {
                double targetPercentage = (currentMs / _durationMs) * 100.0;
                if (Math.Abs(_timelineSlider.Value - targetPercentage) > 0.5 && !_isSeeking)
                {
                    _timelineSlider.Value = Math.Max(0, Math.Min(targetPercentage, 100.0));
                }
            }
        }
        finally
        {
            _isTimerUpdatingSlider = false;
        }

        if (_currentTimeLabel != null)
        {
            _currentTimeLabel.Text = FormatTime(currentMs);
        }
        if (_totalTimeLabel != null)
        {
            _totalTimeLabel.Text = FormatTime(_durationMs);
        }

        if (_videoHost?.IpcClient != null)
        {
            SetContent("PlayPauseButton", _videoHost.IpcClient.IsPaused ? "PLAY" : "PAUSE");
        }
    }

    private double GetCurrentTimeMs()
    {
        if (_videoHost?.IpcClient?.CurrentTime > 0)
        {
            return _videoHost.IpcClient.CurrentTime * 1000.0;
        }
        if (_timelineSlider != null && _durationMs > 0)
        {
            return (_timelineSlider.Value / 100.0) * _durationMs;
        }

        return 0;
    }

    private (double x, double y) SnapPosition(CropEditorItem item, double x, double y, double width, double height)
    {
        ClearGuides();
        if (this.FindControl<CheckBox>("SnapToggle")?.IsChecked != true)
        {
            return (x, y);
        }

        var xTargets = new List<(double value, string label)>
        {
            (0, "Canvas Left"),
            (PortraitWidth / 2, "Canvas Center"),
            (PortraitWidth, "Canvas Right")
        };
        var yTargets = new List<(double value, string label)>
        {
            (ContentTop, "Content Top"),
            (PortraitHeight / 2, "Canvas Center"),
            (ContentBottom, "Content Bottom")
        };

        foreach (CropEditorItem other in _items.Where(i => !ReferenceEquals(i, item)))
        {
            xTargets.Add((other.X, other.DisplayName + " Left"));
            xTargets.Add((other.X + other.Width / 2, other.DisplayName + " Center"));
            xTargets.Add((other.X + other.Width, other.DisplayName + " Right"));

            yTargets.Add((other.Y, other.DisplayName + " Top"));
            yTargets.Add((other.Y + other.Height / 2, other.DisplayName + " Center"));
            yTargets.Add((other.Y + other.Height, other.DisplayName + " Bottom"));
        }

        (double snappedX, double? guideX) = SnapAxis(x, width, xTargets);
        (double snappedY, double? guideY) = SnapAxis(y, height, yTargets);

        if (guideX.HasValue)
        {
            DrawGuide(vertical: true, guideX.Value);
        }
        if (guideY.HasValue)
        {
            DrawGuide(vertical: false, guideY.Value);
        }

        return (snappedX, snappedY);
    }

    private static (double pos, double? guide) SnapAxis(double pos, double size, List<(double value, string label)> targets)
    {
        double start = pos;
        double center = pos + size / 2;
        double end = pos + size;
        double bestDistance = SnapThreshold + 1;
        double bestPos = pos;
        double? bestGuide = null;

        foreach ((double target, _) in targets)
        {
            Check(start, target, target);
            Check(center, target, target - size / 2);
            Check(end, target, target - size);
        }

        return (bestPos, bestGuide);

        void Check(double current, double guide, double candidatePos)
        {
            double distance = Math.Abs(current - guide);
            if (distance < bestDistance && distance <= SnapThreshold)
            {
                bestDistance = distance;
                bestPos = candidatePos;
                bestGuide = guide;
            }
        }
    }

    private void DrawGuide(bool vertical, double value)
    {
        if (_portraitCanvas == null)
        {
            return;
        }

        var line = new Line
        {
            StartPoint = vertical ? new Point(value, 0) : new Point(0, value),
            EndPoint = vertical ? new Point(value, PortraitHeight) : new Point(PortraitWidth, value),
            Stroke = new SolidColorBrush(Color.Parse("#7dd3fc")),
            StrokeThickness = 2,
            IsHitTestVisible = false,
            ZIndex = 1200
        };

        _portraitCanvas.Children.Add(line);
        _guideControls.Add(line);
    }

    private void ClearGuides()
    {
        if (_portraitCanvas == null)
        {
            _guideControls.Clear();
            return;
        }

        foreach (Control control in _guideControls)
        {
            _portraitCanvas.Children.Remove(control);
        }
        _guideControls.Clear();
    }

    private (int x, int y) ClampOverlay(double x, double y, double width, double height)
    {
        return CoordinateMath.ClampOverlayPosition(x, y, width, height);
    }

    private static int RoundPixel(double value)
    {
        return CoordinateMath.ScaleRound(Frac.FromDouble(value));
    }

    private Point ClampToSnapshot(Point point)
    {
        return new Point(
            Math.Max(0, Math.Min(point.X, _snapshotWidth)),
            Math.Max(0, Math.Min(point.Y, _snapshotHeight)));
    }

    private static Rect NormalizeRect(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X);
        double y = Math.Min(a.Y, b.Y);
        double w = Math.Abs(a.X - b.X);
        double h = Math.Abs(a.Y - b.Y);
        return new Rect(x, y, w, h);
    }

    private SourceRect ToSourceRect(Rect rect)
    {
        int x = (int)Math.Floor(rect.X);
        int y = (int)Math.Floor(rect.Y);
        int right = (int)Math.Ceiling(rect.Right);
        int bottom = (int)Math.Ceiling(rect.Bottom);
        return ClampSourceRect(new SourceRect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y)));
    }

    private SourceRect ClampSourceRect(SourceRect rect)
    {
        return ClampSourceRect(rect, _snapshotWidth, _snapshotHeight);
    }

    private static SourceRect ClampSourceRect(SourceRect rect, int width, int height)
    {
        int x = Math.Max(0, Math.Min(rect.X, Math.Max(0, width - 1)));
        int y = Math.Max(0, Math.Min(rect.Y, Math.Max(0, height - 1)));
        int w = Math.Max(1, Math.Min(rect.Width, width - x));
        int h = Math.Max(1, Math.Min(rect.Height, height - y));
        return new SourceRect(x, y, w, h);
    }

    private HudRole SuggestRole(SourceRect rect)
    {
        double cx = rect.X + rect.Width / 2.0;
        double cy = rect.Y + rect.Height / 2.0;
        bool right = cx > _snapshotWidth / 2.0;
        bool bottom = cy > _snapshotHeight / 2.0;

        return new HudRole("custom_element", "Custom Element", 50, -1, -1);
    }

    private static JsonObject EnsureObject(JsonObject config, string section)
    {
        if (config[section] is JsonObject obj)
        {
            return obj;
        }

        obj = new JsonObject();
        config[section] = obj;
        return obj;
    }

    private static int ReadInt(JsonNode? node, int fallback)
    {
        try
        {
            return node?.GetValue<int>() ?? fallback;
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("CROP", $"JSON int parse fallback to {fallback}: {ex.Message}");
            return fallback;
        }
    }

    private static double ReadDouble(JsonNode? node, double fallback)
    {
        try
        {
            return node?.GetValue<double>() ?? fallback;
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("CROP", $"JSON double parse fallback to {fallback}: {ex.Message}");
            return fallback;
        }
    }

    private static string FormatTime(double millis)
    {
        if (!double.IsFinite(millis) || millis < 0)
        {
            millis = 0;
        }

        TimeSpan ts = TimeSpan.FromMilliseconds(millis);
        return ts.TotalHours >= 1
            ? ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : ts.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private void SetWizardState(int step, string goal, string status)
    {
        if (_wizardProgress != null)
        {
            _wizardProgress.Value = Math.Max(0, Math.Min(100, step * 20));
        }
        if (_goalLabel != null)
        {
            _goalLabel.Text = $"Goal: {goal}";
        }
        SetStatus(status);
    }

    private void SetStatus(string text)
    {
        if (_statusLabel != null)
        {
            _statusLabel.Text = text;
        }
    }

    private void SetEnabled(string name, bool enabled)
    {
        if (this.FindControl<Control>(name) is { } control)
        {
            control.IsEnabled = enabled;
        }
    }

    private void SetVisible(string name, bool visible)
    {
        if (this.FindControl<Control>(name) is { } control)
        {
            control.IsVisible = visible;
        }
    }

    private void SetContent(string name, object content)
    {
        if (this.FindControl<Button>(name) is { } button)
        {
            button.Content = content;
        }
    }

    private static string ResolveBinaryPath(string fileName, string preferredSubdirectory)
    {
        string processDir = IOPath.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string baseDir = AppContext.BaseDirectory;
        string sourceRootCandidate = IOPath.GetFullPath(IOPath.Combine(baseDir, "..", "..", "..", "..", "..", "binaries", fileName));

        string[] candidates =
        [
            IOPath.Combine(processDir, preferredSubdirectory, fileName),
            IOPath.Combine(processDir, "backend", fileName),
            IOPath.Combine(processDir, "frontend", fileName),
            IOPath.Combine(processDir, fileName),
            sourceRootCandidate,
            IOPath.Combine(Environment.CurrentDirectory, "binaries", fileName),
            IOPath.Combine(Environment.CurrentDirectory, preferredSubdirectory, fileName),
            fileName
        ];

        return candidates.FirstOrDefault(File.Exists) ?? fileName;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            DeleteSelectedItem();
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.Y && e.KeyModifiers.HasFlag(KeyModifiers.Control)) ||
            (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
        {
            Redo();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Undo();
            e.Handled = true;
            return;
        }

        if (_selectedItem != null && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                double resizeStep = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 2;
                double delta = e.Key is Key.Left or Key.Up ? -resizeStep : resizeStep;
                ResizeFromBottomRight(_selectedItem, delta);
                ApplyItemLayout(_selectedItem);
                MarkDirty();
                PushHistory();
                e.Handled = true;
                return;
            }

            double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 5 : 1;
            double dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
            double dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
            (_selectedItem.X, _selectedItem.Y) = ClampOverlay(_selectedItem.X + dx, _selectedItem.Y + dy, _selectedItem.Width, _selectedItem.Height);
            ApplyItemLayout(_selectedItem);
            MarkDirty();
            PushHistory();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_isSafeToClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        FortniteVideoSoftware.App.WindowBoundsHelper.SaveBoundsSync(this, "CropToolBounds");
        Hide();

        try
        {
            _timelineTimer?.Stop();


            if (_videoHost?.IpcClient != null)
            {
                await _videoHost.IpcClient.SendCommandAsync("stop");
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Error during close: {ex.Message}");
        }
        finally
        {
            CleanupTempFiles();
            _isSafeToClose = true;
            Close();
        }
    }

    private void CleanupTempFiles()
    {
        foreach (string path in _tempFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Info("CROP", $"Temp file cleanup skipped: {ex.Message}");
            }
        }
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
            titleBar.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount < 2)
                {
                    try { BeginMoveDrag(e); } catch { }
                }
            };
        }
    }

    private sealed class HudRole
    {
        public HudRole(string key, string displayName, int defaultZ, double defaultX, double defaultY)
        {
            Key = key;
            DisplayName = displayName;
            DefaultZ = defaultZ;
            DefaultX = defaultX;
            DefaultY = defaultY;
        }

        public string Key { get; }
        public string DisplayName { get; }
        public int DefaultZ { get; }
        public double DefaultX { get; }
        public double DefaultY { get; }

        public override string ToString() => DisplayName;
    }

    private sealed record LayerEntry(string RoleKey, string DisplayName, int Z)
    {
        public override string ToString() => $"{DisplayName}  z:{Z}";
    }

    private readonly record struct SourceRect(int X, int Y, int Width, int Height);

    private sealed record CandidateSpec(string RoleKey, SourceRect Rect);

    private sealed record ItemSnapshot(
        string RoleKey,
        string DisplayName,
        SourceRect SourceRect,
        string CropImagePath,
        int X,
        int Y,
        int Width,
        int Height,
        int Z);

    private sealed record EditorSnapshot(List<ItemSnapshot> Items);

    private sealed class CropEditorItem
    {
        public required string RoleKey { get; init; }
        public required string DisplayName { get; init; }
        public required SourceRect SourceRect { get; set; }
        public required string CropImagePath { get; init; }
        public required int X { get; set; }
        public required int Y { get; set; }
        public required int Width { get; set; }
        public required int Height { get; set; }
        public required int Z { get; set; }
        public required Canvas Root { get; init; }
        public required Image Image { get; init; }
        public required Rectangle Border { get; init; }
        public required Rectangle TopLeftHandle { get; init; }
        public required Rectangle BottomRightHandle { get; init; }
        public required Border LabelHost { get; init; }
        public required TextBlock LabelText { get; init; }
    }

    private enum ComposerEditMode
    {
        None,
        Drag,
        ResizeTopLeft,
        ResizeBottomRight
    }

    private enum ResizeHandle
    {
        TopLeft,
        BottomRight
    }
}
