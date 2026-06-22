using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FortniteVideoSoftware.Core.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace FortniteVideoSoftware.App;

/// <summary>
/// Granular Speed Editor dialog window.
/// Mirrors the Python GranularSpeedEditor: lets the user mark time ranges and assign
/// a playback speed (including freeze-frame at 0x) to each segment.
/// </summary>
public partial class GranularSpeedEditorWindow : Window
{
    // ------------------------------------------------------------------ state
    private MpvVideoView? _videoHost;
    private bool _isSeeking = false;
    private double? _nextSeekTarget = null;
    private readonly string _videoPath;

    private readonly List<SpeedSegment> _segments = new();

    private int _pendingStartMs = -1;
    private int _pendingEndMs   = -1;
    private double _pendingSpeed = 1.1;
    private double _baseSpeed    = 1.1;

    private DispatcherTimer? _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
    private DispatcherTimer? _feedbackTimer;
    private bool _isTimelineDrawn = false;

    // The result the caller reads after the dialog closes
    public bool Accepted { get; private set; }
    public IReadOnlyList<SpeedSegment> ResultSegments => _segments.AsReadOnly();
    public double ResultBaseSpeed => _baseSpeed;

    // ------------------------------------------------------------------ ctor
    /// <summary>
    /// Parameterless ctor required by Avalonia's XAML runtime loader.
    /// Do not call directly — use the overload that accepts a video path.
    /// </summary>
    public GranularSpeedEditorWindow() : this(string.Empty) { }

    public GranularSpeedEditorWindow(string videoPath, IReadOnlyList<SpeedSegment>? existingSegments = null, double baseSpeed = 1.1)
    {
        _videoPath = videoPath;
        _baseSpeed = baseSpeed;

        InitializeComponent();
        
        // Smart OS Theme Detection
        if (Avalonia.Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Light)
        {
            var mainBorder = this.FindControl<Avalonia.Controls.Border>("MainBorder");
            var titleBarBorder = this.FindControl<Avalonia.Controls.Border>("TitleBarBorder");
            
            if (mainBorder != null) mainBorder.BorderBrush = Avalonia.Media.Brush.Parse("#334155");
            if (titleBarBorder != null) titleBarBorder.Background = Avalonia.Media.Brush.Parse("#0f172a");
        }

        if (existingSegments != null)
            _segments.AddRange(existingSegments);

        this.Loaded += (s, e) => InitializeMpv();
        WireUpControls();
        AttachTitleBarDrag();
        RefreshSegmentList();

        this.Loaded += async (s, e) => {
            await WindowBoundsHelper.LoadBoundsAsync(this, "GranularBounds");
        };

        this.Closing += (s, e) => {
            WindowBoundsHelper.SaveBoundsSync(this, "GranularBounds");
        };

        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();

        // Feedback banner auto-hide timer
        _feedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        _feedbackTimer.Tick += (_, _) => { HideFeedback(); _feedbackTimer?.Stop(); };
    }

    // ------------------------------------------------------------------ init
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void InitializeMpv()
    {
        _videoHost = this.FindControl<MpvVideoView>("VideoHost");
        if (_videoHost != null)
        {
            string mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "backend", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath)) mpvPath = "mpv.exe";
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

                LoadVideo();
            }
        }
    }

    private void UpdateTooltips()
    {
        var kb = FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.KeyBinds;
        var playBtn = this.FindControl<Button>("GranularPlayPause");
        if (playBtn != null) ToolTip.SetTip(playBtn, $"Play or pause the video ({kb.PlayPause})");
        
        var startBtn = this.FindControl<Button>("MarkStartBtn");
        if (startBtn != null) ToolTip.SetTip(startBtn, $"Mark the start of the segment ({kb.MarkStart})");
        
        var endBtn = this.FindControl<Button>("MarkEndBtn");
        if (endBtn != null) ToolTip.SetTip(endBtn, $"Mark the end of the segment ({kb.MarkEnd})");
    }

    private void GranularKeyDownHandler(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Avalonia.Controls.TextBox or Avalonia.Controls.NumericUpDown)
            return;

        var kb = FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.KeyBinds;

        if (e.Key == kb.PlayPause)
        {
            var btn = this.FindControl<Button>("GranularPlayPause");
            btn?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        }
        else if (e.Key == kb.MarkStart)
        {
            var btn = this.FindControl<Button>("MarkStartBtn");
            btn?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        }
        else if (e.Key == kb.MarkEnd)
        {
            var btn = this.FindControl<Button>("MarkEndBtn");
            btn?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
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
    }

    private async Task SeekInternal(double time) {
        if (_isSeeking) { _nextSeekTarget = time; return; }
        _isSeeking = true;
        if (_videoHost?.IpcClient != null) await _videoHost.IpcClient.SendCommandAsync("seek", time, "absolute");
    }

    private void LoadVideo()
    {
        if (string.IsNullOrWhiteSpace(_videoPath) || _videoHost?.IpcClient == null) return;

        // Show filename in header
        var label = this.FindControl<TextBlock>("VideoFileLabel");
        if (label != null) label.Text = System.IO.Path.GetFileName(_videoPath);

        _ = _videoHost.IpcClient.LoadFileAsync(_videoPath);
        _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
    }

    private void WireUpControls()
    {
        // ---- Timeline slider ----
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("GranularTimelineCanvas");
        if (canvas != null)
        {
            canvas.SizeChanged += (s, e) => RedrawTimeline();
        }

        var timeline = this.FindControl<Slider>("GranularTimeline");
        if (timeline != null)
        {
            timeline.PropertyChanged += (_, e) =>
            {
                if (e.Property == Slider.ValueProperty && !_isSeeking && _videoHost?.IpcClient != null)
                {
                    double pct = timeline.Value / 1000.0;
                    double dur = GetDuration();
                    if (dur > 0)
                        _ = SeekInternal(pct * dur);
                }
            };
        }

        // ---- Play/Pause ----
        var playPause = this.FindControl<Button>("GranularPlayPause");
        playPause?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            RuntimeLog.Info("UI", "User toggled Play/Pause in Granular Speed Editor.");
            if (_videoHost?.IpcClient != null) _ = _videoHost.IpcClient.SetPropertyAsync("pause", _videoHost.IpcClient.IsPaused ? "no" : "yes");
        });


        // ---- Mark Start / End ----
        var markStart = this.FindControl<Button>("MarkStartBtn");
        markStart?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            RuntimeLog.Info("UI", "User clicked Mark Start in Granular Speed Editor.");
            _pendingStartMs = (int)(GetCurrentTime() * 1000);
            var lbl = this.FindControl<TextBlock>("PendingStartLabel");
            if (lbl != null) lbl.Text = FormatMs(_pendingStartMs);
            ShowFeedback($"START: {FormatMs(_pendingStartMs)}");
            RedrawTimeline();
        });

        var markEndBtn = this.FindControl<Button>("MarkEndBtn");
        markEndBtn?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            RuntimeLog.Info("UI", "User clicked Mark End in Granular Speed Editor.");
            _pendingEndMs = (int)(GetCurrentTime() * 1000);
            
            // Logic for auto-calculating start:
            if (_pendingStartMs < 0)
            {
                if (_segments.Count == 0)
                {
                    _pendingStartMs = 0;
                }
                else
                {
                    _pendingStartMs = (int)_segments.Last().EndMs + 1000;
                    if (_pendingStartMs > _pendingEndMs) 
                    {
                        // If 1s gap makes it larger than end, fallback to end (though user should avoid this)
                        _pendingStartMs = (int)_segments.Last().EndMs;
                    }
                }
            }

            if (_videoHost?.IpcClient != null)
            {
                _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");
            }

            ShowFeedback($"SEGMENT ADDED: {FormatMs(_pendingEndMs)}");
            
            // Automatically add segment
            AddPendingSegment();
            RedrawTimeline();
        });

        // ---- Pending speed slider ----
        var speedSlider = this.FindControl<Slider>("PendingSpeedSlider");
        if (speedSlider != null)
        {
            speedSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property == Slider.ValueProperty)
                {
                    _pendingSpeed = Math.Round(speedSlider.Value, 2);
                    var lbl = this.FindControl<TextBlock>("PendingSpeedLabel");
                    if (lbl != null) lbl.Text = $"{_pendingSpeed:F2}x";
                }
            };
        }

        // Freeze frame checkbox syncs slider to 0
        var freezeCheck = this.FindControl<Avalonia.Controls.Primitives.ToggleButton>("FreezeFrameCheck");
        if (freezeCheck != null && speedSlider != null)
        {
            freezeCheck.IsCheckedChanged += (_, _) =>
            {
                bool frozen = freezeCheck.IsChecked == true;
                speedSlider.IsEnabled = !frozen;
                if (frozen) { speedSlider.Value = 0.1; _pendingSpeed = 0.0; }
                var lbl = this.FindControl<TextBlock>("PendingSpeedLabel");
                if (lbl != null) lbl.Text = frozen ? "0.00x (FREEZE)" : $"{_pendingSpeed:F2}x";
            };
        }

        // Add segment button logic removed from UI, keeping AddPendingSegment internally

        // ---- Clear all ----
        var clearBtn = this.FindControl<Button>("ClearAllSegmentsBtn");
        clearBtn?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            RuntimeLog.Info("UI", "User clicked Clear All in Granular Speed Editor.");
            _segments.Clear();
            _pendingStartMs = -1;
            _pendingEndMs = -1;
            RedrawTimeline();
            SetStatus("All segments and pending selections cleared.");
        });

        // ---- Accept / Cancel ----
        var acceptBtn = this.FindControl<Button>("AcceptGranularBtn");
        if (acceptBtn != null) acceptBtn.Click += (s, e) => {
            RuntimeLog.Info("UI", "User clicked Accept in Granular Speed Editor.");
            if (_pendingStartMs >= 0 && _pendingEndMs >= 0)
            {
                AddPendingSegment();
            }
            Accepted = true;
            Close();
        };

        var cancel = this.FindControl<Button>("CancelGranularBtn");
        cancel?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            RuntimeLog.Info("UI", "User clicked Cancel in Granular Speed Editor.");
            Close();
        });

        UpdateTooltips();
        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, GranularKeyDownHandler, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    // ------------------------------------------------------------------ segment management
    private void AddPendingSegment()
    {
        if (_pendingStartMs < 0 || _pendingEndMs < 0)
        {
            SetStatus("Mark a START and END time first.");
            return;
        }

        int start = Math.Min(_pendingStartMs, _pendingEndMs);
        int end   = Math.Max(_pendingStartMs, _pendingEndMs);

        if (end - start < 10) // 10 ms minimum per spec
        {
            SetStatus("Segment must be at least 10 ms long.");
            return;
        }

        // Collision check: no overlaps allowed (spec §3.1)
        foreach (var seg in _segments)
        {
            if (start < seg.EndMs && end > seg.StartMs)
            {
                SetStatus($"Overlap with existing segment [{FormatMs(seg.StartMs)} – {FormatMs(seg.EndMs)}]. Adjust times.");
                return;
            }
        }

        double speed = this.FindControl<Avalonia.Controls.Primitives.ToggleButton>("FreezeFrameCheck")?.IsChecked == true ? 0.0 : _pendingSpeed;
        _segments.Add(new SpeedSegment(start, end, speed));
        _segments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

        // Reset pending markers
        _pendingStartMs = -1;
        _pendingEndMs   = -1;
        RedrawTimeline();
        SetStatus($"Segment added: {FormatMs(start)} – {FormatMs(end)} @ {speed:F2}x");
    }

    private void RefreshSegmentList()
    {
        var panel = this.FindControl<StackPanel>("SegmentsPanel");
        if (panel == null) return;
        panel.Children.Clear();

        var countLbl = this.FindControl<TextBlock>("SegmentCountLabel");
        if (countLbl != null)
            countLbl.Text = _segments.Count == 0 ? "No segments" : $"{_segments.Count} segment{(_segments.Count == 1 ? "" : "s")}";

        for (int i = 0; i < _segments.Count; i++)
        {
            int idx = i;
            var seg = _segments[i];
            bool isFreeze = seg.Speed < 0.01;

            // Row border
            var border = new Border
            {
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1e293b")),
                BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#334155")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(0, 2),
                Padding = new Thickness(8, 6),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            // Info text
            var info = new TextBlock
            {
                Text = $"#{idx + 1}  {FormatMs(seg.StartMs)} → {FormatMs(seg.EndMs)}\n" +
                       (isFreeze ? "❄ FREEZE FRAME" : $"{seg.Speed:F2}x speed"),
                Foreground = isFreeze
                    ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#60a5fa"))
                    : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e2e8f0")),
                FontSize = 10.5,
                FontFamily = new Avalonia.Media.FontFamily("Consolas"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };

            // Delete button
            var delBtn = new Button
            {
                Content = "✕",
                Width = 26,
                Height = 26,
                FontSize = 11,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7f1d1d")),
                Foreground = Avalonia.Media.Brushes.White,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(4, 0, 0, 0)
            };
            delBtn.Click += (_, e) =>
            {
                e.Handled = true;
                _segments.RemoveAt(idx);
                RefreshSegmentList();
                RedrawTimeline();
                SetStatus("Segment removed.");
            };

            border.PointerPressed += (_, _) =>
            {
                _pendingStartMs = (int)seg.StartMs;
                _pendingEndMs = (int)seg.EndMs;
                _pendingSpeed = seg.Speed;

                var startLbl = this.FindControl<TextBlock>("PendingStartLabel");
                var endLbl = this.FindControl<TextBlock>("PendingEndLabel");
                if (startLbl != null) startLbl.Text = FormatMs(_pendingStartMs);
                if (endLbl != null) endLbl.Text = FormatMs(_pendingEndMs);
                
                var freezeCheck = this.FindControl<Avalonia.Controls.Primitives.ToggleButton>("FreezeFrameCheck");
                var speedSlider = this.FindControl<Slider>("PendingSpeedSlider");
                if (freezeCheck != null) freezeCheck.IsChecked = seg.Speed < 0.01;
                if (speedSlider != null && seg.Speed >= 0.01) speedSlider.Value = seg.Speed;

                _segments.RemoveAt(idx);
                RefreshSegmentList();
                RedrawTimeline();
                SetStatus("Editing segment... Adjusted markers loaded.");
            };

            Grid.SetColumn(info, 0);
            Grid.SetColumn(delBtn, 1);
            grid.Children.Add(info);
            grid.Children.Add(delBtn);
            border.Child = grid;
            panel.Children.Add(border);
        }
    }

    // ------------------------------------------------------------------ timeline overlay
    private void RedrawTimeline()
    {
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("GranularTimelineCanvas");
        if (canvas == null) return;

        Dispatcher.UIThread.Post(() =>
        {
            canvas.Children.Clear();
            double dur = GetDuration();
            double w = canvas.Bounds.Width;
            double h = Math.Max(canvas.Bounds.Height, 28);
            if (dur <= 0 || w <= 0) return;

            // DRAW TIMELINE SCALES
            double tickInterval = 5;
            if (dur > 3600) tickInterval = 300;
            else if (dur > 1800) tickInterval = 60;
            else if (dur > 300) tickInterval = 30;
            else if (dur > 60) tickInterval = 10;

            for (double t = 0; t <= dur; t += tickInterval)
            {
                double tx = (t / dur) * w;
                
                var tickLine = new Avalonia.Controls.Shapes.Rectangle { Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(60, 255, 255, 255)), Width = 1, Height = h };
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

            // Draw each segment as a colored bar
            foreach (var seg in _segments)
            {
                double x1 = (seg.StartMs / 1000.0 / dur) * w;
                double x2 = (seg.EndMs   / 1000.0 / dur) * w;
                bool isFreeze = seg.Speed < 0.01;

                var rect = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width  = Math.Max(2, x2 - x1),
                    Height = h,
                    Fill   = new Avalonia.Media.SolidColorBrush(
                        isFreeze
                            ? Avalonia.Media.Color.FromArgb(120, 96, 165, 250)
                            : Avalonia.Media.Color.FromArgb(100, 34, 197, 94))
                };
                Avalonia.Controls.Canvas.SetLeft(rect, x1);
                Avalonia.Controls.Canvas.SetTop(rect, 0);
                canvas.Children.Add(rect);
            }

            // Draw pending start marker
            if (_pendingStartMs >= 0)
            {
                double px = (_pendingStartMs / 1000.0 / dur) * w;
                var line = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = 2, Height = h,
                    Fill = Avalonia.Media.Brushes.SeaGreen
                };
                Avalonia.Controls.Canvas.SetLeft(line, px);
                canvas.Children.Add(line);
            }

            // Draw pending end marker
            if (_pendingEndMs >= 0)
            {
                double px = (_pendingEndMs / 1000.0 / dur) * w;
                var line = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = 2, Height = h,
                    Fill = Avalonia.Media.Brushes.SeaGreen
                };
                Avalonia.Controls.Canvas.SetLeft(line, px);
                canvas.Children.Add(line);
            }
        });
    }

    // ------------------------------------------------------------------ playback timer
    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_videoHost?.IpcClient == null) return;

        // Update play/pause button label
        var ppBtn = this.FindControl<Button>("GranularPlayPause");
        if (ppBtn != null)
        {
            string desired = (_videoHost.IpcClient.IsPaused) ? "\u25BA PLAY" : "\u23F8 PAUSE";
            if (ppBtn.Content?.ToString() != desired) ppBtn.Content = desired;
        }

        // Update time label & slider
        double t = _videoHost.IpcClient.CurrentTime;
        double dur = _videoHost.IpcClient.Duration;
        if (dur > 0)
        {
            if (!_isTimelineDrawn)
            {
                RedrawTimeline();
                _isTimelineDrawn = true;
            }
        }

        var elapsed = this.FindControl<TextBlock>("GranularTimeElapsed");
        if (elapsed != null) elapsed.Text = FormatSec(t);

        var slider = this.FindControl<Slider>("GranularTimeline");
        if (slider != null && dur > 0 && !slider.IsPointerOver)
        {
            _isSeeking = true;
            slider.Value = (t / dur) * 1000.0;
            _isSeeking = false;
        }

        // Keep canvas updated as playhead moves
        RedrawTimeline();
    }

    // ------------------------------------------------------------------ helpers
    private double GetCurrentTime()
    {
        if (_videoHost?.IpcClient == null) return 0;
        return _videoHost.IpcClient.CurrentTime;
    }

    private double GetDuration()
    {
        if (_videoHost?.IpcClient == null) return 0;
        double v = _videoHost.IpcClient.Duration;
        
        // Update duration label when we first get a valid value
        var durLbl = this.FindControl<TextBlock>("GranularDuration");
        if (durLbl != null && durLbl.Text != FormatSec(v))
            Dispatcher.UIThread.Post(() => durLbl.Text = FormatSec(v));
        return v;
    }

    private static string FormatMs(double ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms < 0 ? 0 : ms);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    private static string FormatSec(double sec)
    {
        var ts = TimeSpan.FromSeconds(sec < 0 ? 0 : sec);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    private void ShowFeedback(string text)
    {
        var fb = this.FindControl<Border>("GranularFeedback");
        var tb = this.FindControl<TextBlock>("GranularFeedbackText");
        if (fb != null && tb != null)
        {
            tb.Text = text;
            fb.IsVisible = true;
            _feedbackTimer?.Stop();
            _feedbackTimer?.Start();
        }
    }

    private void HideFeedback()
    {
        var fb = this.FindControl<Border>("GranularFeedback");
        if (fb != null) fb.IsVisible = false;
    }

    private void SetStatus(string msg)
    {
        var lbl = this.FindControl<TextBlock>("BottomStatusLabel");
        if (lbl != null) lbl.Text = msg;
    }

    // ------------------------------------------------------------------ cleanup
    protected override void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        if (_videoHost?.IpcClient != null)
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("stop");
        }
        base.OnClosing(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = LoadBoundsAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _ = SaveBoundsAsync();
        _playbackTimer?.Stop();
        _feedbackTimer?.Stop();

        if (_videoHost?.IpcClient != null)
        {
            
        }
        base.OnClosed(e);
    }

    private async Task LoadBoundsAsync()
    {
        try
        {
            var store = new FortniteVideoSoftware.Core.Ipc.StateTransferStore();
            var state = await store.LoadAsync();
            if (state.TryGetPropertyValue("GranularBounds", out var boundsNode) && boundsNode is System.Text.Json.Nodes.JsonObject boundsObj)
            {
                if (boundsObj.TryGetPropertyValue("X", out var x) && boundsObj.TryGetPropertyValue("Y", out var y))
                {
                    this.Position = new Avalonia.PixelPoint((int)(x ?? 0), (int)(y ?? 0));
                }
                if (boundsObj.TryGetPropertyValue("Width", out var w) && boundsObj.TryGetPropertyValue("Height", out var h))
                {
                    this.Width = (double)(w ?? 0);
                    this.Height = (double)(h ?? 0);
                }
                if (boundsObj.TryGetPropertyValue("WindowState", out var stateNode))
                {
                    this.WindowState = (WindowState)(int)(stateNode ?? 0);
                }
            }
        }
        catch { }
    }

    private async Task SaveBoundsAsync()
    {
        try
        {
            var store = new FortniteVideoSoftware.Core.Ipc.StateTransferStore();
            var boundsObj = new System.Text.Json.Nodes.JsonObject
            {
                ["X"] = this.Position.X,
                ["Y"] = this.Position.Y,
                ["Width"] = this.Bounds.Width,
                ["Height"] = this.Bounds.Height,
                ["WindowState"] = (int)this.WindowState
            };
            var updates = new System.Text.Json.Nodes.JsonObject
            {
                ["GranularBounds"] = boundsObj
            };
            await store.UpdatePropertiesAsync(updates);
        }
        catch { }
    }

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<Border>("TitleBarBorder");
        if (titleBar != null)
        {
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
