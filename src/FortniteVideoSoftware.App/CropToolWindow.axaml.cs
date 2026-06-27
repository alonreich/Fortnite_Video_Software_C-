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
using System.Diagnostics;
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
    private const double HandleSize = 18;
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
    private ComboBox? _roleCombo;
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
    private static readonly Dictionary<string, HudRole> RoleByDisplay = Roles.ToDictionary(r => r.DisplayName, StringComparer.OrdinalIgnoreCase);

    public CropToolWindow() : this(null)
    {
    }

    public CropToolWindow(string? initialVideoPath)
    {
        _initialVideoPath = string.IsNullOrWhiteSpace(initialVideoPath) ? null : initialVideoPath;

        InitializeComponent();
        FindControls();
        AttachTitleBarDrag();
        WireEvents();
        InitializeRoles();
        InitializeHistory();
        SetWizardState(1, "Upload Video", "Open a reference clip to start.");

        Loaded += async (_, _) =>
        {
            await WindowBoundsHelper.LoadBoundsAsync(this, "CropToolBounds");
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
        _roleCombo = this.FindControl<ComboBox>("RoleCombo");
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
        ButtonClick("AddSelectionButton", (_, _) => AddCurrentSelection());
        ButtonClick("DeleteSelectedButton", (_, _) => DeleteSelectedItem());
        ButtonClick("UndoButton", (_, _) => Undo());
        ButtonClick("RedoButton", (_, _) => Redo());
        ButtonClick("RaiseButton", (_, _) => MoveSelectedLayer(1));
        ButtonClick("LowerButton", (_, _) => MoveSelectedLayer(-1));
        ButtonClick("ResetButton", (_, _) => ResetWorkingState());
        ButtonClick("ReturnButton", async (_, _) => await ReturnToMainAppAsync());
        ButtonClick("SaveButton", async (button, _) => await SaveAndReturnAsync(button));

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
            _timelineSlider.PropertyChanged += async (_, e) =>
            {
                if (e.Property == Slider.ValueProperty && !_isTimelineUpdating && _durationMs > 0 && _videoHost?.IpcClient != null)
                {
                    double seconds = Math.Max(0, Math.Min(_timelineSlider.Value, _durationMs)) / 1000.0;
                    await _videoHost.IpcClient.SendCommandAsync("seek", seconds, "absolute", "exact");
                }
            };
        }

        _timelineTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timelineTimer.Tick += (_, _) => UpdateTimelineUi();
        _timelineTimer.Start();
    }

    private void ButtonClick(string name, EventHandler<RoutedEventArgs> handler)
    {
        var button = this.FindControl<Button>(name);
        if (button != null)
        {
            button.Click += handler;
        }
    }

    private void InitializeRoles()
    {
        if (_roleCombo != null)
        {
            _roleCombo.ItemsSource = Roles;
            _roleCombo.SelectedIndex = 0;
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
            if (System.IO.File.Exists(paths.SessionStateFile))
            {
                var state = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(System.IO.File.ReadAllText(paths.SessionStateFile));
                if (state != null && state["CropToolUploadDirectory"]?.ToString() is string startPath && System.IO.Directory.Exists(startPath))
                {
                    options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(new Uri(startPath));
                }
            }
        }
        catch { }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(options);

        if (files.Count == 0)
        {
            return;
        }

        try
        {
            var state = System.IO.File.Exists(paths.SessionStateFile) 
                ? System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(System.IO.File.ReadAllText(paths.SessionStateFile)) ?? new System.Text.Json.Nodes.JsonObject()
                : new System.Text.Json.Nodes.JsonObject();
            state["CropToolUploadDirectory"] = System.IO.Path.GetDirectoryName(files[0].Path.LocalPath);
            System.IO.File.WriteAllText(paths.SessionStateFile, state.ToJsonString());
        }
        catch { }

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

            if (_timelineSlider != null)
            {
                _isTimelineUpdating = true;
                _timelineSlider.Minimum = 0;
                _timelineSlider.Maximum = Math.Max(1000, _durationMs);
                _timelineSlider.Value = 0;
                _isTimelineUpdating = false;
            }

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

            string ffmpegPath = ResolveBinaryPath("ffmpeg.exe", "backend");
            string args = $"-ss {seconds.ToString("0.000", CultureInfo.InvariantCulture)} -i \"{_videoPath}\" -frames:v 1 -q:v 2 -y \"{tempOutput}\"";
            await RunProcessAsync(ffmpegPath, args, TimeSpan.FromSeconds(20));

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

        await Task.Run(() =>
        {
            using SKBitmap bitmap = SKBitmap.Decode(path) ?? throw new IOException("Could not decode snapshot.");
            _snapshotWidth = bitmap.Width;
            _snapshotHeight = bitmap.Height;
            _originalResolution = $"{_snapshotWidth}x{_snapshotHeight}";
        });

        using var fs = File.OpenRead(path);
        var snapshotBitmap = new Bitmap(fs);

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

        if (_composerBackgroundImage != null)
        {
            using var bgStream = File.OpenRead(path);
            _composerBackgroundImage.Source = new Bitmap(bgStream);
        }

        ClearSourceSelection();
        ClearMagicWandCandidates();
        SetWizardState(3, "Refine Box", $"Draw a HUD box on the {_originalResolution} snapshot.");
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
        if (_roleCombo != null)
        {
            _roleCombo.SelectedItem = role;
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

    private void AddCurrentSelection()
    {
        if (_sourceSelection == null || string.IsNullOrWhiteSpace(_snapshotPath))
        {
            return;
        }

        HudRole role = _roleCombo?.SelectedItem as HudRole ?? SuggestRole(_sourceSelection.Value);
        SourceRect sourceRect = ClampSourceRect(_sourceSelection.Value);
        if (sourceRect.Width < MinSelectionSize || sourceRect.Height < MinSelectionSize)
        {
            SetStatus("Selection is too small.");
            return;
        }

        try
        {
            string cropPath = CropSnapshotRegion(_snapshotPath, sourceRect);
            _tempFiles.Add(cropPath);

            CropEditorItem? existing = _items.FirstOrDefault(i => i.RoleKey == role.Key);
            if (existing != null)
            {
                RemoveItem(existing);
            }

            double width = sourceRect.Width;
            double height = sourceRect.Height;
            (double x, double y) = ClampOverlay(role.DefaultX, role.DefaultY, width, height);

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
            Tag = null,
            ZIndex = snapshot.Z
        };

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
            FontSize = 18,
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
            (x, y) = ClampOverlay(x, y, _activeEditItem.Width, _activeEditItem.Height);
            _activeEditItem.X = x;
            _activeEditItem.Y = y;
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

        item.Width = Math.Max(MinItemSize, width);
        item.Height = Math.Max(MinItemSize, height);
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

        item.X = Math.Max(0, x);
        item.Y = Math.Max(ContentTop, y);
        item.Width = Math.Max(MinItemSize, width);
        item.Height = Math.Max(MinItemSize, height);
    }

    private void ApplyItemLayout(CropEditorItem item)
    {
        item.Width = Math.Max(MinItemSize, item.Width);
        item.Height = Math.Max(MinItemSize, item.Height);
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

        item.LabelHost.Width = Math.Max(160, item.Width);
        Canvas.SetLeft(item.LabelHost, (item.Width - item.LabelHost.Width) / 2);
        Canvas.SetTop(item.LabelHost, item.Y > PortraitHeight / 2 ? -34 : item.Height + 10);
        item.LabelText.Text = item.DisplayName.ToUpperInvariant();
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
                    FontSize = 13,
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
            button.Content = "SAVING...";
        }

        bool saved = await SaveConfigAsync();
        if (saved)
        {
            await ReturnToMainAppAsync();
            return;
        }

        if (button != null)
        {
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
            JsonObject config = await store.LoadAsync();

            JsonObject crops = EnsureObject(config, "crops_1080p");
            JsonObject scales = EnsureObject(config, "scales");
            JsonObject overlays = EnsureObject(config, "overlays");
            JsonObject zOrders = EnsureObject(config, "z_orders");

            foreach (CropEditorItem item in _items)
            {
                var transformed = CoordinateMath.TransformToContentAreaInt(
                    (item.SourceRect.X, item.SourceRect.Y, item.SourceRect.Width, item.SourceRect.Height),
                    _originalResolution);

                int cropW = Math.Max(2, transformed.w);
                int cropH = Math.Max(2, transformed.h);
                double scaleX = item.Width / cropW;
                double scaleY = item.Height / cropH;
                double scale = Math.Max(0.0001, Math.Round((scaleX + scaleY) / 2.0, 4));
                (double ox, double oy) = ClampOverlay(item.X, item.Y, item.Width, item.Height);

                crops[item.RoleKey] = new JsonArray(cropW, cropH, transformed.x, transformed.y);
                scales[item.RoleKey] = scale;
                overlays[item.RoleKey] = new JsonObject
                {
                    ["x"] = (int)Math.Round(ox),
                    ["y"] = (int)Math.Round(oy)
                };
                zOrders[item.RoleKey] = item.Z;
            }

            config["schema_version"] = CropConfigDefaults.SchemaVersion;
            config["coordinate_space"] = CropConfigDefaults.CoordinateSpace;
            await store.SaveAsync(config);

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
            RuntimeLog.Info("CROP", "Returning to parent main window.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", "Error returning to main window: " + ex.Message);
        }

        Close();
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
                Math.Abs(x.X - y.X) > 0.01 ||
                Math.Abs(x.Y - y.Y) > 0.01 ||
                Math.Abs(x.Width - y.Width) > 0.01 ||
                Math.Abs(x.Height - y.Height) > 0.01 ||
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
            if (_durationMs > 0)
            {
                _timelineSlider.Maximum = _durationMs;
            }
        }

        _isTimelineUpdating = true;
        if (_durationMs > 0 && Math.Abs(_timelineSlider.Value - currentMs) > 100)
        {
            _timelineSlider.Value = Math.Max(0, Math.Min(currentMs, _durationMs));
        }
        _isTimelineUpdating = false;

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
        return _timelineSlider?.Value ?? 0;
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

    private (double x, double y) ClampOverlay(double x, double y, double width, double height)
    {
        double maxX = Math.Max(0, PortraitWidth - width);
        double maxY = Math.Max(ContentTop, ContentBottom - height);
        return (Math.Max(0, Math.Min(x, maxX)), Math.Max(ContentTop, Math.Min(y, maxY)));
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

        if (!bottom && !right) return RoleByKey["team"];
        if (!bottom && right) return RoleByKey["stats"];
        if (bottom && right) return RoleByKey["loot"];
        return RoleByKey["normal_hp"];
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
        catch
        {
            return fallback;
        }
    }

    private static double ReadDouble(JsonNode? node, double fallback)
    {
        try
        {
            return node?.GetValue<double>() ?? fallback;
        }
        catch
        {
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

    private static async Task RunProcessAsync(string fileName, string arguments, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            throw new TimeoutException($"{IOPath.GetFileName(fileName)} timed out.");
        }

        string stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{IOPath.GetFileName(fileName)} failed with exit code {process.ExitCode}: {stderr}");
        }
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
        Hide();

        try
        {
            _timelineTimer?.Stop();
            await WindowBoundsHelper.SaveBoundsAsync(this, "CropToolBounds");

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
            catch
            {
            }
        }
    }

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<Border>("TitleBarBorder");
        if (titleBar != null)
        {
            titleBar.IsHitTestVisible = true;
            titleBar.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
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
        double X,
        double Y,
        double Width,
        double Height,
        int Z);

    private sealed record EditorSnapshot(List<ItemSnapshot> Items);

    private sealed class CropEditorItem
    {
        public required string RoleKey { get; init; }
        public required string DisplayName { get; init; }
        public required SourceRect SourceRect { get; set; }
        public required string CropImagePath { get; init; }
        public required double X { get; set; }
        public required double Y { get; set; }
        public required double Width { get; set; }
        public required double Height { get; set; }
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
