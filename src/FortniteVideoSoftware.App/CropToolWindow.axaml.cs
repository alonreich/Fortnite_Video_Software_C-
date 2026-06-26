using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Ipc;
using FortniteVideoSoftware.Core.Media;
using System.Diagnostics;
using System.IO;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Input;

namespace FortniteVideoSoftware.App;

public partial class CropToolWindow : Window
{
    private bool _isDragging = false;
    private Point _pointerStartPoint;
    private Rectangle? _activeCropRect;
    private Rectangle? _selectedCropRect;
    private Point _rectStartPos;
    private MpvVideoView? _bgVideo;
    private bool _isSafeToClose = false;
    private const double SnapGridSize = 10;
    private readonly ObservableCollection<string> _layerNames = new();
    private readonly Dictionary<string, Rectangle> _cropRects = new();

    private async void InitializeMpv()
    {
        _bgVideo = this.FindControl<MpvVideoView>("BackgroundVideo");
        if (_bgVideo != null)
        {
            string mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "backend", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath)) mpvPath = "mpv.exe";
            await _bgVideo.StartMpvProcessAsync(mpvPath);
        }
    }

    protected override async void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
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
            await WindowBoundsHelper.SaveBoundsAsync(this, "CropToolBounds");

            if (_bgVideo?.IpcClient != null)
            {
                await _bgVideo.IpcClient.SendCommandAsync("stop");
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Error saving state during close: {ex.Message}");
        }
        finally
        {
            // Mark as safe and programmatically re-trigger the close
            _isSafeToClose = true;
            this.Close();
        }
    }

    public CropToolWindow()
    {
        InitializeComponent();
        this.Loaded += (s, e) => InitializeMpv();
        AttachTitleBarDrag();

        this.Loaded += async (s, e) => {
            await WindowBoundsHelper.LoadBoundsAsync(this, "CropToolBounds");
        };

        var returnBtn = this.FindControl<Button>("ReturnButton");
        if (returnBtn != null)
        {
            returnBtn.Click += async (s, e) =>
            {
                await ReturnToMainAppAsync();
            };
        }

        var saveBtn = this.FindControl<Button>("SaveButton");
        if (saveBtn != null)
        {
            saveBtn.Click += async (s, e) =>
            {
                saveBtn.Content = "SAVING...";
                saveBtn.IsEnabled = false;
                await SaveConfigAsync();
                await ReturnToMainAppAsync();
            };
        }

        var openVideoBtn = this.FindControl<Button>("OpenVideoButton");
        if (openVideoBtn != null)
        {
            openVideoBtn.Click += async (s, e) =>
            {
                var files = await this.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Open Reference Video",
                    AllowMultiple = false,
                    FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Video Files") { Patterns = new[] { "*.mp4", "*.mkv", "*.avi", "*.mov" } } }
                });

                if (files != null && files.Count > 0)
                {
                    string path = files[0].Path.LocalPath;
                    if (_bgVideo?.IpcClient != null)
                    {
                        await _bgVideo.IpcClient.LoadFileAsync(path);
                    }
                }
            };
        }

        var layerList = this.FindControl<ListBox>("LayerList");
        if (layerList != null)
        {
            layerList.ItemsSource = _layerNames;
            layerList.SelectionChanged += (s, e) =>
            {
                if (layerList.SelectedItem is string role && _cropRects.TryGetValue(role, out var rect))
                {
                    SelectCropRect(rect);
                }
            };
        }

        var showPlaceholders = this.FindControl<CheckBox>("ShowPlaceholders");
        if (showPlaceholders != null)
        {
            showPlaceholders.IsCheckedChanged += (s, e) => ApplyPlaceholderVisibility();
        }

        var canvas = this.FindControl<Canvas>("PortraitCanvas");
        if (canvas != null)
        {
            canvas.PointerPressed += Canvas_PointerPressed;
            canvas.PointerMoved += Canvas_PointerMoved;
            canvas.PointerReleased += Canvas_PointerReleased;
            
            // Add standard layout regions for dragging
            AddCropRect("Loot", 800, 150, 250, 400, Brushes.LimeGreen);
            AddCropRect("Stats", 20, 150, 300, 200, Brushes.Aqua);
            AddCropRect("HP", 300, 1500, 480, 80, Brushes.Red);
            AddCropRect("Team", 20, 360, 250, 300, Brushes.Magenta);
            AddCropRect("Spectating", 350, 1200, 380, 60, Brushes.Yellow);
            if (_layerNames.Count > 0 && layerList != null)
                layerList.SelectedIndex = 0;
        }
    }

    private void AddCropRect(string role, double x, double y, double w, double h, IBrush brush)
    {
        var canvas = this.FindControl<Canvas>("PortraitCanvas");
        if (canvas == null) return;

        var rect = new Rectangle
        {
            Width = w,
            Height = h,
            Fill = new SolidColorBrush(Colors.Green) { Opacity = 0.4 },
            Stroke = brush,
            StrokeThickness = 2,
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Tag = role,
            ZIndex = 50
        };

        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        canvas.Children.Add(rect);
        _cropRects[role] = rect;
        if (!_layerNames.Contains(role))
            _layerNames.Add(role);
    }

    private void Canvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var canvas = sender as Canvas;
        if (canvas == null) return;

        var point = e.GetPosition(canvas);
        if (e.Source is Rectangle r && r.Tag != null)
        {
            SelectCropRect(r);
            _activeCropRect = r;
            _isDragging = true;
            _pointerStartPoint = point;
            _rectStartPos = new Point(Canvas.GetLeft(r), Canvas.GetTop(r));
            e.Handled = true;
        }
        else
        {
            SelectCropRect(null);
        }
    }

    private void Canvas_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging || _activeCropRect == null || sender is not Canvas canvas) return;

        var point = e.GetPosition(canvas);
        var diffX = point.X - _pointerStartPoint.X;
        var diffY = point.Y - _pointerStartPoint.Y;

        double newX = _rectStartPos.X + diffX;
        double newY = _rectStartPos.Y + diffY;

        // Constraint to content area (1080x1620 starting at Y=150)
        newX = Math.Max(0, Math.Min(newX, CanvasMath.FinalWidth - _activeCropRect.Width));
        newY = Math.Max(CanvasMath.ContentOffsetY, Math.Min(newY, CanvasMath.FinalHeight - CanvasMath.ContentOffsetY - _activeCropRect.Height));

        if (IsSnapEnabled())
        {
            newX = Math.Round(newX / SnapGridSize) * SnapGridSize;
            newY = Math.Round(newY / SnapGridSize) * SnapGridSize;
            newX = Math.Max(0, Math.Min(newX, CanvasMath.FinalWidth - _activeCropRect.Width));
            newY = Math.Max(CanvasMath.ContentOffsetY, Math.Min(newY, CanvasMath.FinalHeight - CanvasMath.ContentOffsetY - _activeCropRect.Height));
        }

        Canvas.SetLeft(_activeCropRect, newX);
        Canvas.SetTop(_activeCropRect, newY);
    }

    private void Canvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDragging = false;
        _activeCropRect = null;
    }

    private void SelectCropRect(Rectangle? rect)
    {
        if (_selectedCropRect != null)
        {
            _selectedCropRect.StrokeThickness = 2;
            _selectedCropRect.Fill = new SolidColorBrush(Colors.Green) { Opacity = 0.4 };
        }

        _selectedCropRect = rect;

        if (_selectedCropRect != null)
        {
            _selectedCropRect.StrokeThickness = 4;
            _selectedCropRect.Fill = new SolidColorBrush(Colors.Gold) { Opacity = 0.32 };

            if (_selectedCropRect.Tag is string role)
            {
                var layerList = this.FindControl<ListBox>("LayerList");
                if (layerList != null && layerList.SelectedItem as string != role)
                    layerList.SelectedItem = role;
            }
        }
        else
        {
            var layerList = this.FindControl<ListBox>("LayerList");
            if (layerList != null && layerList.SelectedItem != null)
                layerList.SelectedItem = null;
        }
    }

    private bool IsSnapEnabled()
    {
        return this.FindControl<CheckBox>("SnapToggle")?.IsChecked == true;
    }

    private void ApplyPlaceholderVisibility()
    {
        bool visible = this.FindControl<CheckBox>("ShowPlaceholders")?.IsChecked != false;
        foreach (var rect in _cropRects.Values)
        {
            rect.IsVisible = visible;
        }
        if (!visible)
            SelectCropRect(null);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async Task SaveConfigAsync()
    {
        try
        {
            RuntimeLog.Info("CROP", "Saving crop coordinates.");
            var store = new CropConfigStore();
            var config = await store.LoadAsync();
            
            var canvas = this.FindControl<Canvas>("PortraitCanvas");
            if (canvas != null)
            {
                var cropsObj = config["crops_1080p"] as JsonObject ?? new JsonObject();
                
                foreach (var child in canvas.Children)
                {
                    if (child is Rectangle r && r.Tag is string role)
                    {
                        string techKey = role.ToLowerInvariant();
                        if (techKey == "unknown") continue;

                        int uiX = (int)Math.Round(Canvas.GetLeft(r));
                        int uiY = (int)Math.Round(Canvas.GetTop(r));
                        int uiW = (int)Math.Round(r.Width);
                        int uiH = (int)Math.Round(r.Height);

                        // 1. Math: UI to Internal space
                        var internalRect = CoordinateMath.TransformToContentAreaInt((uiX, uiY, uiW, uiH), "1920x1080");

                        // 2. Protect Drift (+1px left or right edge depending on region)
                        var finalCrop = CanvasMath.ProtectCropDrift(role, new JsonArray(internalRect.w, internalRect.h, internalRect.x, internalRect.y));
                        
                        cropsObj[techKey] = finalCrop;
                    }
                }
                config["crops_1080p"] = cropsObj;
            }

            config["schema_version"] = 4;
            await store.SaveAsync(config);
            RuntimeLog.Success("CROP", "Saved crop coordinates successfully.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", ex);
        }
    }

    private async Task ReturnToMainAppAsync()
    {
        try
        {
            var store = new StateTransferStore();
            var state = await store.LoadAsync();
            state["returned_from_crop_tool"] = true;
            await store.SaveAsync(state);
            RuntimeLog.Info("CROP", "Returning to parent main window.");
            Close();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", "Error returning to main window: " + ex.Message);
        }
        
    }

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<Border>("TitleBarBorder");
        if (titleBar != null)
        {
            titleBar.IsHitTestVisible = true;
            titleBar.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    try { BeginMoveDrag(e); } catch { }
                }
            };
        }
    }
}
