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
    private readonly double _trimStartMs;
    private readonly double _trimEndMs;

    private readonly List<SpeedSegment> _segments = new();

    private int _pendingStartMs = -1;
    private int _pendingEndMs   = -1;
    private double _pendingSpeed = 1.1;
    private double _baseSpeed    = 1.1;

    private bool _isSafeToClose = false;

    // Issue #4: Selected segment for inline editing
    private int _selectedSegmentIndex = -1;
    private DispatcherTimer? _marchingAntsTimer;
    private double _marchingAntsOffset = 0;

    private DispatcherTimer? _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _isTimelineDrawn = false;
    private double _lastAppliedSpeed = 1.0;  // Tracks last speed sent to MPV to avoid redundant IPC

    // The result the caller reads after the dialog closes
    public bool Accepted { get; private set; }
    public IReadOnlyList<SpeedSegment> ResultSegments => _segments
        .Select(s => new SpeedSegment(s.StartMs + (int)_trimStartMs, s.EndMs + (int)_trimStartMs, s.Speed))
        .ToList()
        .AsReadOnly();
    public double ResultBaseSpeed => _baseSpeed;
    public double ResultFreezeTimeMs => _freezeTimeMs;
    public double ResultFreezeDurationS => _freezeDurationS;

    private double _freezeTimeMs = -1;
    private double _freezeDurationS = 1.0;
    private double _selectedFreezePresetS = -1.0;
    private double _lastFreezeTriggerAbsMs = -10000;
    private bool _isCurrentlyFrozen = false;
    private DateTime _freezeStartTime;
    private bool _isFreezeCameraSelected = false;
    private bool _isDraggingFreezeCamera = false;
    private Avalonia.Controls.Shapes.Rectangle? _freezeCameraIconAntsRef;
    private Avalonia.Controls.Shapes.Rectangle? _freezeCameraLineAntsRef;
    private Avalonia.Controls.Shapes.Rectangle? _selectedSegmentBorderRef;
    // Freeze preset gentle pulse timer — keeps the duration buttons gently glowing until the user picks one.
    private DispatcherTimer? _freezePulseTimer;
    private double _freezePulseOffset = 0;

    // ------------------------------------------------------------------ ctor
    /// <summary>
    /// Parameterless ctor required by Avalonia's XAML runtime loader.
    /// Do not call directly — use the overload that accepts a video path.
    /// </summary>
    public GranularSpeedEditorWindow() : this(string.Empty, 0, 0) { }

    /// <summary>
    /// Creates the Granular Speed Editor constrained to a trim region.
    /// The editor will only show/seek between trimStartMs and trimEndMs.
    /// Segments are stored in absolute video timestamps.
    /// </summary>
    public GranularSpeedEditorWindow(string videoPath, double trimStartMs = 0, double trimEndMs = 0, IEnumerable<SpeedSegment>? existingSegments = null, double baseSpeed = 1.1, double freezeTimeMs = -1, double freezeDurationS = 1.0)
    {
        _videoPath = videoPath;
        _trimStartMs = trimStartMs;
        _trimEndMs = trimEndMs;
        _baseSpeed = baseSpeed;
        _freezeTimeMs = freezeTimeMs;
        _freezeDurationS = freezeDurationS;
        _selectedFreezePresetS = -1.0;

        InitializeComponent();
        FortniteVideoSoftware.App.Infrastructure.WindowManager.RegisterWindow(this);
        
        _pendingSpeed = baseSpeed;
        _lastAppliedSpeed = baseSpeed;

        var initialSpeedSlider = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("PendingSpeedSlider"); if(initialSpeedSlider!=null)initialSpeedSlider.SetRange(1, 40);
        SpeedPresetButtons.SetSpinningWheelValue(initialSpeedSlider, _pendingSpeed);
        var initialSpeedLabel = this.FindControl<TextBlock>("PendingSpeedLabel");
        if (initialSpeedLabel != null) initialSpeedLabel.Text = $"{_pendingSpeed:0.0}x";
        
        if (existingSegments != null)
        {
            // Convert incoming segments from absolute → relative to trim region
            foreach (var seg in existingSegments)
            {
                int relStart = (int)(seg.StartMs - _trimStartMs);
                int relEnd = (int)(seg.EndMs - _trimStartMs);
                // Only include segments that fall within the trim region
                if (relEnd > 0 && relStart < (int)(_trimEndMs - _trimStartMs))
                {
                    relStart = Math.Max(0, relStart);
                    int maxEnd = _trimEndMs > 0 ? (int)(_trimEndMs - _trimStartMs) : int.MaxValue;
                    relEnd = Math.Min(relEnd, maxEnd);
                    _segments.Add(new SpeedSegment(relStart, relEnd, seg.Speed));
                }
            }
        }

        this.Loaded += (s, e) => InitializeMpv();
        WireUpControls();
        AttachTitleBarDrag();
        RefreshSegmentList();
        UpdateDeleteButtonVisibility();

        if (_freezeTimeMs >= 0)
        {
            var toggle = this.FindControl<Button>("FreezeImageToggle");
            if (toggle != null)
            {
                toggle.Classes.Remove("Primary");
                toggle.Classes.Add("Danger");
                var icon = this.FindControl<TextBlock>("FreezeImageToggleIcon");
                var txt = this.FindControl<TextBlock>("FreezeImageToggleText");
                if (icon != null) icon.Text = "🔓";
                if (txt != null) txt.Text = "UNFREEZE IMAGE";
            }
        }

        // Issue #4: Marching ants animation timer for selected segment
        _marchingAntsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _marchingAntsTimer.Tick += (_, _) => {
            _marchingAntsOffset = (_marchingAntsOffset + 1) % 8;
            if (_isDraggingFreezeCamera && _freezeCameraIconAntsRef != null && _freezeCameraLineAntsRef != null)
            {
                _freezeCameraIconAntsRef.StrokeDashOffset = _marchingAntsOffset;
                _freezeCameraLineAntsRef.StrokeDashOffset = _marchingAntsOffset;
            }
            if (_selectedSegmentBorderRef != null)
            {
                _selectedSegmentBorderRef.StrokeDashOffset = _marchingAntsOffset;
            }
        };
        _marchingAntsTimer.Start();

        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();

        var fvPopup = this.FindControl<Avalonia.Controls.Primitives.Popup>("FreezeValidationPopup");
        var gfPopup = this.FindControl<Avalonia.Controls.Primitives.Popup>("GranularFeedbackPopup");
        var targetBorder = this.FindControl<Avalonia.Controls.Border>("GranularVideoAreaBorder");
        if (fvPopup != null && targetBorder != null) fvPopup.PlacementTarget = targetBorder;
        if (gfPopup != null && targetBorder != null) gfPopup.PlacementTarget = targetBorder;
    }

    // ------------------------------------------------------------------ init
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void InitializeMpv()
    {
        _videoHost = this.FindControl<MpvVideoView>("GranularVideoHost");
        if (_videoHost != null)
        {
            RuntimeLog.Info("Granular", "Initializing MPV video host for Granular Speed Editor.");
            string mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "frontend", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath)) mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "backend", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath)) mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath))
            {
                RuntimeLog.Fail("Granular", "Could not locate mpv.exe for Granular Speed Editor. Using PATH fallback.");
                mpvPath = "mpv.exe";
            }
            else
            {
                RuntimeLog.Info("Granular", $"Using MPV at: {mpvPath}");
            }
            await _videoHost.StartMpvProcessAsync(mpvPath);

            if (_videoHost.IpcClient != null)
            {
                RuntimeLog.Info("Granular", "MPV IPC client connected. Attaching seek handler.");
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
            else
            {
                RuntimeLog.Fail("Granular", "MPV IPC client is null after starting MPV process.");
            }
        }
        else
        {
            RuntimeLog.Fail("Granular", "Could not find GranularVideoHost control in XAML.");
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

        if (_isFreezeCameraSelected && _freezeTimeMs >= 0 && e.Key is Avalonia.Input.Key.Left or Avalonia.Input.Key.Right)
        {
            MoveFreezeCameraByPixels(e.Key == Avalonia.Input.Key.Left ? -1 : 1);
            e.Handled = true;
            return;
        }

        // Issue #4: Delete key removes the selected segment
        if (e.Key == Avalonia.Input.Key.Delete || e.Key == Avalonia.Input.Key.Back)
        {
            if (_selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count)
            {
                _segments.RemoveAt(_selectedSegmentIndex);
                _selectedSegmentIndex = -1;
                RefreshSegmentList();
                RedrawTimeline();
                UpdateDeleteButtonVisibility();
                SetStatus("Selected segment deleted.");
                e.Handled = true;
                return;
            }
        }

        var playPause = new Avalonia.Input.KeyGesture(kb.PlayPause);
        var markStart = new Avalonia.Input.KeyGesture(kb.MarkStart);
        var markEnd = new Avalonia.Input.KeyGesture(kb.MarkEnd);
        var seekFwd = new Avalonia.Input.KeyGesture(kb.SeekForward);
        var seekBack = new Avalonia.Input.KeyGesture(kb.SeekBackward);
        var fineSeekFwdCtrl = new Avalonia.Input.KeyGesture(kb.FineSeekForward, Avalonia.Input.KeyModifiers.Control);
        var fineSeekFwdShift = new Avalonia.Input.KeyGesture(kb.FineSeekForward, Avalonia.Input.KeyModifiers.Shift);
        var fineSeekBackCtrl = new Avalonia.Input.KeyGesture(kb.FineSeekBackward, Avalonia.Input.KeyModifiers.Control);
        var fineSeekBackShift = new Avalonia.Input.KeyGesture(kb.FineSeekBackward, Avalonia.Input.KeyModifiers.Shift);

        if (playPause.Matches(e))
        {
            var btn = this.FindControl<Button>("GranularPlayPause");
            btn?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        }
        else if (markStart.Matches(e))
        {
            var btn = this.FindControl<Button>("MarkStartBtn");
            btn?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        }
        else if (markEnd.Matches(e))
        {
            var btn = this.FindControl<Button>("MarkEndBtn");
            btn?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        }
        else if (fineSeekFwdCtrl.Matches(e) || fineSeekFwdShift.Matches(e))
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("frame-step");
            e.Handled = true;
        }
        else if (fineSeekBackCtrl.Matches(e) || fineSeekBackShift.Matches(e))
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("frame-back-step");
            e.Handled = true;
        }
        else if (seekFwd.Matches(e))
        {
            // Seek forward but clamp to trim region end
            double currentAbs = _videoHost?.IpcClient?.CurrentTime ?? 0;
            double trimEndSec = (_trimEndMs > 0) ? _trimEndMs / 1000.0 : double.MaxValue;
            double target = Math.Min(currentAbs + 5, trimEndSec);
            _ = SeekInternal(target - (_trimStartMs / 1000.0));
            e.Handled = true;
        }
        else if (seekBack.Matches(e))
        {
            // Seek backward but clamp to trim region start
            double currentAbs = _videoHost?.IpcClient?.CurrentTime ?? 0;
            double target = Math.Max(currentAbs - 5, _trimStartMs / 1000.0);
            _ = SeekInternal(target - (_trimStartMs / 1000.0));
            e.Handled = true;
        }
    }

    private async Task SeekInternal(double time) {
        if (_isSeeking) { _nextSeekTarget = time; return; }
        _isSeeking = true;
        // `time` is relative to the trim region; convert to absolute video position
        double absTime = (_trimStartMs / 1000.0) + time;
        double trimEndSec = (_trimEndMs > 0) ? _trimEndMs / 1000.0 : double.MaxValue;
        absTime = Math.Min(absTime, trimEndSec);
        absTime = Math.Max(absTime, _trimStartMs / 1000.0);
        if (_videoHost?.IpcClient != null) await _videoHost.IpcClient.SendCommandAsync("seek", absTime.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
    }

    private void LoadVideo()
    {
        if (string.IsNullOrWhiteSpace(_videoPath) || _videoHost?.IpcClient == null) return;

        double startSec = _trimStartMs / 1000.0;
        
        // LoadFileAsync accepts startTime and sets 'start' property before loading.
        _ = _videoHost.IpcClient.LoadFileAsync(_videoPath, startSec);
        _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
    }

    private void WireUpControls()
    {
        // ---- Timeline slider ----
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("GranularTimelineCanvas");
        if (canvas != null)
        {
            canvas.SizeChanged += (s, e) => RedrawTimeline();

            // Issue #4: Click-to-select on timeline segments
            canvas.IsHitTestVisible = true;
            canvas.PointerPressed += (s, e) =>
            {
                double dur = GetDuration();
                if (dur <= 0) return;
                double w = canvas.Bounds.Width;
                if (w <= 0) return;
                double clickX = e.GetPosition(canvas).X;
                double clickSec = (clickX / w) * dur;
                int clickMs = (int)(clickSec * 1000);

                // Find segment at click position
                int foundIdx = -1;
                for (int i = 0; i < _segments.Count; i++)
                {
                    if (clickMs >= _segments[i].StartMs && clickMs <= _segments[i].EndMs)
                    {
                        foundIdx = i;
                        break;
                    }
                }

                if (foundIdx >= 0)
                {
                    _selectedSegmentIndex = foundIdx;
                    var seg = _segments[foundIdx];
                    _pendingSpeed = seg.Speed;

                    // Sync slider to selected segment's speed
                    var speedSlider = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("PendingSpeedSlider");
                    var speedLbl = this.FindControl<TextBlock>("PendingSpeedLabel");
                    if (speedSlider != null && seg.Speed >= 0.01) SpeedPresetButtons.SetSpinningWheelValue(speedSlider, seg.Speed);
                    if (speedLbl != null) speedLbl.Text = $"{seg.Speed:0.0}x";

                    UpdateDeleteButtonVisibility();
                    RedrawTimeline();
                    SetStatus($"Selected segment #{foundIdx + 1}. Change speed to edit it.");
                    e.Handled = true;
                }
                else
                {
                    // Click on empty area deselects
                    if (_isFreezeCameraSelected)
                    {
                        _isFreezeCameraSelected = false;
                        _isDraggingFreezeCamera = false;
                    }
                    if (_selectedSegmentIndex >= 0)
                    {
                        _selectedSegmentIndex = -1;
                        UpdateDeleteButtonVisibility();
                        RedrawTimeline();
                    }
                    else if (_isFreezeCameraSelected == false)
                    {
                        RedrawTimeline();
                    }
                }
            };
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
            if (_isCurrentlyFrozen)
            {
                // User clicked "Pause" while the timeline is faking playback during a freeze.
                // Cancel the freeze tracking and leave MPV paused natively.
                _isCurrentlyFrozen = false;
                return;
            }
            if (_videoHost?.IpcClient != null) _ = _videoHost.IpcClient.SetPropertyAsync("pause", _videoHost.IpcClient.IsPaused ? "no" : "yes");
        });


        // ---- Mark Start / End ----
        var markStart = this.FindControl<Button>("MarkStartBtn");
        markStart?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            RuntimeLog.Info("UI", "User clicked Mark Start in Granular Speed Editor.");

            int currentMs = (int)(GetCurrentTime() * 1000);

            // Prevent marking inside an existing segment (no overlaps allowed)
            int? overlapIdx = FindSegmentAtPosition(currentMs);
            if (overlapIdx.HasValue)
            {
                var overlapping = _segments[overlapIdx.Value];
                ShowFeedback($"⚠ Inside segment #{overlapIdx.Value + 1}! Delete it first.");
                SetStatus($"Cannot mark here — overlaps segment #{overlapIdx.Value + 1} [{FormatMs(overlapping.StartMs)} – {FormatMs(overlapping.EndMs)}]. Delete it first.");
                return;
            }

            // Issue #4: Starting new marking deselects any selected segment
            _selectedSegmentIndex = -1;
            UpdateDeleteButtonVisibility();

            _pendingStartMs = currentMs;
            ShowFeedback($"START: {FormatMs(_pendingStartMs)}");
            RedrawTimeline();
        });

        var markEndBtn = this.FindControl<Button>("MarkEndBtn");
        markEndBtn?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            RuntimeLog.Info("UI", "User clicked Mark End in Granular Speed Editor.");

            int currentMs = (int)(GetCurrentTime() * 1000);

            // Prevent marking END inside an existing segment (unless we're just setting end
            // with a valid start that doesn't overlap)
            if (_pendingStartMs < 0)
            {
                int? overlapIdx = FindSegmentAtPosition(currentMs);
                if (overlapIdx.HasValue)
                {
                    var overlapping = _segments[overlapIdx.Value];
                    ShowFeedback($"⚠ Inside segment #{overlapIdx.Value + 1}! Delete it first.");
                    SetStatus($"Cannot mark here — overlaps segment #{overlapIdx.Value + 1} [{FormatMs(overlapping.StartMs)} – {FormatMs(overlapping.EndMs)}]. Delete it first.");
                    return;
                }
            }

            _pendingEndMs = currentMs;
            
            // Issue #4: Starting new marking deselects any selected segment
            _selectedSegmentIndex = -1;

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

            // Issue #4: Auto-select the newly created segment (last one added)
            if (_segments.Count > 0)
            {
                _selectedSegmentIndex = _segments.Count - 1;
            }
            UpdateDeleteButtonVisibility();
            RedrawTimeline();
        });

        // ---- Pending speed slider ----
        var speedSlider = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("PendingSpeedSlider");
        if (speedSlider != null)
        {
            speedSlider.ValueChanged += (_, e) =>
            {
                _pendingSpeed = Math.Round(e / 10.0, 2);
                var lbl = this.FindControl<TextBlock>("PendingSpeedLabel");
                if (lbl != null) lbl.Text = $"{_pendingSpeed:0.0}x";

                // Issue #4: If a segment is selected, update its speed live
                if (_selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count)
                {
                    var seg = _segments[_selectedSegmentIndex];
                    _segments[_selectedSegmentIndex] = new SpeedSegment(seg.StartMs, seg.EndMs, _pendingSpeed);
                    RefreshSegmentList();
                    RedrawTimeline();
                }
            };
        }

        // Issue #7: Speed preset buttons
        WireUpSpeedPresets(speedSlider);

        // Freeze Image logic
        WireUpFreezeImage();

        // ---- Delete single segment (Issue #6) ----
        var deleteSegBtn = this.FindControl<Button>("DeleteSegmentBtn");
        deleteSegBtn?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            if (_selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count)
            {
                _segments.RemoveAt(_selectedSegmentIndex);
                _selectedSegmentIndex = -1;
                RefreshSegmentList();
                RedrawTimeline();
                UpdateDeleteButtonVisibility();
                SetStatus("Selected segment deleted.");
            }
        });

        // ---- Clear all ----
        var clearBtn = this.FindControl<Button>("ClearAllSegmentsBtn");
        clearBtn?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            RuntimeLog.Info("UI", "User clicked Clear All in Granular Speed Editor.");
            _segments.Clear();
            _selectedSegmentIndex = -1;
            _pendingStartMs = -1;
            _pendingEndMs = -1;
            RefreshSegmentList();
            RedrawTimeline();
            UpdateDeleteButtonVisibility();
            SetStatus("All segments and pending selections cleared.");
        });

        // ---- Accept / Cancel ----
        var acceptBtn = this.FindControl<Button>("AcceptGranularBtn");
        if (acceptBtn != null) acceptBtn.Click += (s, e) => {
            RuntimeLog.Info("UI", "User clicked Accept in Granular Speed Editor.");
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

    private void WireUpFreezeImage()
    {
        var freezeImageToggle = this.FindControl<Button>("FreezeImageToggle");

        var freezePresets = new[] {
            this.FindControl<Button>("FreezePreset05"), this.FindControl<Button>("FreezePreset10"), this.FindControl<Button>("FreezePreset15"),
            this.FindControl<Button>("FreezePreset20"), this.FindControl<Button>("FreezePreset25"), this.FindControl<Button>("FreezePreset30")
        };
        double[] presetValues = { 0.5, 1.0, 1.5, 2.0, 2.5, 3.0 };

        // ── IDEA 1: Selected preset turns GREEN (matching BaseSpeedPreset theme) ──
        // Green palette: bg=#14532d, border=#22c55e, fg=#86efac (same as the main app base speed button)
        var selectedBg = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#14532d"));
        var selectedBorder = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#22c55e"));
        var selectedFg = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#86efac"));

        void SetFreezePresetSelection(int selectedIndex)
        {
            for (int j = 0; j < freezePresets.Length; j++)
            {
                var preset = freezePresets[j];
                if (preset == null) continue;

                if (j == selectedIndex)
                {
                    // Selected button = GREEN (Idea 1)
                    preset.Classes.Remove("Primary");
                    preset.Background = selectedBg;
                    preset.BorderBrush = selectedBorder;
                    preset.Foreground = selectedFg;
                }
                else
                {
                    // Non-selected = clear overrides, back to default SpeedPreset style
                    preset.ClearValue(Avalonia.Controls.Button.BackgroundProperty);
                    preset.ClearValue(Avalonia.Controls.Button.BorderBrushProperty);
                    preset.ClearValue(Avalonia.Controls.Button.ForegroundProperty);
                }
            }
        }

        // ── IDEA 3: Gentle pulse timer — keeps buttons softly glowing until user picks one ──
        // Slow, gentle sine wave (~1.5s per cycle). Not fast or jarring.
        int stepperIndex = 0;
        int _freezePulseCount = 0;
        _freezePulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _freezePulseTimer.Tick += (_, _) =>
        {
            stepperIndex = (stepperIndex + 1) % freezePresets.Length;
            _freezePulseCount++;

            var hint1 = this.FindControl<TextBlock>("FreezeHintLabel");
            var hint2 = this.FindControl<TextBlock>("FreezeHintLabelBottom");
            double newOpacity = (stepperIndex % 2 == 0) ? 1.0 : 0.0;
            if (hint1 != null) hint1.Opacity = newOpacity;
            if (hint2 != null) hint2.Opacity = newOpacity;

            // Task 7: 10-iteration blink animation loop
            if (_freezePulseCount >= 20)
            {
                _freezePulseTimer?.Stop();
                if (hint1 != null) hint1.Opacity = 1.0;
                if (hint2 != null) hint2.Opacity = 1.0;
            }

            for (int j = 0; j < freezePresets.Length; j++)
            {
                var b = freezePresets[j];
                if (b == null) continue;

                // Only stepper pulse buttons that are NOT selected (green) and NOT disabled
                bool isSelected = (Math.Abs(_selectedFreezePresetS - presetValues[j]) < 0.01);
                if (isSelected) continue;

                if (j == stepperIndex)
                {
                    // Stepper Highlight (Bright Amber)
                    b.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(120, 90, 26));
                    b.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(250, 197, 22));
                    b.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(255, 247, 237));
                }
                else
                {
                    // Reset to default style so it doesn't stay highlighted
                    b.ClearValue(Avalonia.Controls.Button.BackgroundProperty);
                    b.ClearValue(Avalonia.Controls.Button.BorderBrushProperty);
                    b.ClearValue(Avalonia.Controls.Button.ForegroundProperty);
                }
            }
        };

        // ── Helper: Grey out (disable) all other buttons until a duration is picked (Idea 5) ──
        void SetControlsEnabledDuringFreezePrompt(bool enabled)
        {
            // Grey out: MARK START, MARK END, PLAY/PAUSE, speed slider, speed presets, delete/clear
            var controlsToToggle = new Control?[] {
                this.FindControl<Button>("MarkStartBtn"),
                this.FindControl<Button>("MarkEndBtn"),
                this.FindControl<Button>("GranularPlayPause"),
                this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("PendingSpeedSlider"),
                this.FindControl<StackPanel>("SpeedPresetsPanel"),
                this.FindControl<Button>("DeleteSegmentBtn"),
                this.FindControl<Button>("ClearAllSegmentsBtn"),
            };
            foreach (var c in controlsToToggle)
            {
                if (c is Avalonia.Input.InputElement input) input.IsEnabled = enabled;
            }
        }

        for (int i = 0; i < freezePresets.Length; i++)
        {
            var btn = freezePresets[i];
            var val = presetValues[i];
            int presetIndex = i;
            if (btn != null)
            {
                btn.Click += (_, _) =>
                {
                    _selectedFreezePresetS = val;

                    // Stop the gentle pulse (Idea 3)
                    _freezePulseTimer?.Stop();

                    // Apply GREEN selection (Idea 1)
                    SetFreezePresetSelection(presetIndex);

                    // Hide the hint label (Idea 5)
                    var hint = this.FindControl<TextBlock>("FreezeHintLabel");
                    if (hint != null) hint.IsVisible = false;
                    var hintBottom = this.FindControl<TextBlock>("FreezeHintLabelBottom");
                    if (hintBottom != null) hintBottom.IsVisible = false;

                    // Un-grey all other controls (Idea 5)
                    SetControlsEnabledDuringFreezePrompt(true);

                    var popup = this.FindControl<Avalonia.Controls.Primitives.Popup>("FreezeValidationPopup");
                    if (popup != null) popup.IsOpen = false;

                    if (_freezeTimeMs >= 0)
                    {
                        _freezeDurationS = val;
                        RedrawTimeline();
                        FortniteVideoSoftware.App.RuntimeLog.Info("GRANULAR_EDITOR", $"State Change: User clicked freeze preset button. Set freeze duration to {val}s.");
                        ShowFeedback($"FREEZE CREATED: {val:0.0}s");
                    }
                };
            }
        }

        if (freezeImageToggle != null)
        {
            freezeImageToggle.Click += async (_, _) =>
            {
                if (_freezeTimeMs < 0)
                {
                    bool promptPreset = (_selectedFreezePresetS < 0);

                    if (promptPreset)
                    {
                        ShowFeedback("SELECT FREEZE DURATION");
                        
                        // ── IDEA 3: Start gentle pulse ──
                        _freezePulseCount = 0;
                        _freezePulseTimer?.Start();

                        // ── IDEA 5: Show hint label + grey out other controls ──
                        var hint = this.FindControl<TextBlock>("FreezeHintLabel");
                        if (hint != null) hint.IsVisible = true;
                        var hintBottom = this.FindControl<TextBlock>("FreezeHintLabelBottom");
                        if (hintBottom != null) hintBottom.IsVisible = true;

                        SetControlsEnabledDuringFreezePrompt(false);

                        FortniteVideoSoftware.App.RuntimeLog.Info("GRANULAR_EDITOR", "State Change: User clicked 'Freeze Image' toggle but no preset was selected. Showing hint + gentle pulse + greying out other controls.");
                    }

                    // Place freeze at current ABSOLUTE playback time
                    double currentAbsMs = (_videoHost?.IpcClient?.CurrentTime ?? 0) * 1000.0;
                    if (_videoHost != null && _videoHost.IpcClient != null) {
                        _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
                    }
                    if (currentAbsMs < _trimStartMs) currentAbsMs = _trimStartMs;
                    if (_trimEndMs > 0 && currentAbsMs > _trimEndMs) currentAbsMs = _trimEndMs;
                    _freezeTimeMs = currentAbsMs;

                    // If no preset was selected, default duration to 1.0s (will be overridden when they click)
                    _freezeDurationS = promptPreset ? 1.0 : _selectedFreezePresetS;

                    var icon = this.FindControl<TextBlock>("FreezeImageToggleIcon");
                    var txt = this.FindControl<TextBlock>("FreezeImageToggleText");
                    if (icon != null) icon.Text = "🔓";
                    if (txt != null) txt.Text = "UNFREEZE IMAGE";
                    freezeImageToggle.Classes.Remove("Primary");
                    freezeImageToggle.Classes.Add("Danger");

                    RedrawTimeline();
                    FortniteVideoSoftware.App.RuntimeLog.Info("GRANULAR_EDITOR", $"State Change: User clicked 'Freeze Image' toggle. Button set to State 2 (Active/Red - UNFREEZE IMAGE).");

                    // If the user had ALREADY picked a preset before clicking FREEZE IMAGE,
                    // show the selection immediately (no prompt needed)
                    if (!promptPreset)
                    {
                        ShowFeedback($"FREEZE CREATED: {_freezeDurationS:0.0}s");
                        // Find the index matching the selected value and apply green
                        for (int k = 0; k < presetValues.Length; k++)
                        {
                            if (Math.Abs(presetValues[k] - _selectedFreezePresetS) < 0.01)
                            {
                                SetFreezePresetSelection(k);
                                break;
                            }
                        }
                    }
                }
                else
                {
                    _freezeTimeMs = -1;
                    _selectedFreezePresetS = -1.0;
                    _isFreezeCameraSelected = false;
                    _isDraggingFreezeCamera = false;
                    var icon = this.FindControl<TextBlock>("FreezeImageToggleIcon");
                    var txt = this.FindControl<TextBlock>("FreezeImageToggleText");
                    if (icon != null) icon.Text = "📸";
                    if (txt != null) txt.Text = "FREEZE IMAGE";
                    freezeImageToggle.Classes.Remove("Danger");
                    freezeImageToggle.Classes.Add("Primary");

                    ShowFeedback("FREEZE IMAGE REMOVED");

                    // Stop pulse and hide hint if still running
                    _freezePulseTimer?.Stop();
                    var hint = this.FindControl<TextBlock>("FreezeHintLabel");
                    if (hint != null) hint.IsVisible = false;
                    var hintBottom = this.FindControl<TextBlock>("FreezeHintLabelBottom");
                    if (hintBottom != null) hintBottom.IsVisible = false;

                    // Clear all preset styling back to default
                    foreach (var b in freezePresets)
                    {
                        if (b == null) continue;
                        b.ClearValue(Avalonia.Controls.Button.BackgroundProperty);
                        b.ClearValue(Avalonia.Controls.Button.BorderBrushProperty);
                        b.ClearValue(Avalonia.Controls.Button.ForegroundProperty);
                    }

                    // Ensure controls are re-enabled
                    SetControlsEnabledDuringFreezePrompt(true);

                    RedrawTimeline();
                    FortniteVideoSoftware.App.RuntimeLog.Info("GRANULAR_EDITOR", $"State Change: User clicked 'Unfreeze Image' toggle. Button released to State 1 (Default/Blue - FREEZE IMAGE). Existing freeze instance was deleted from the timeline.");
                }
            };
        }
    }

    private void WireUpSpeedPresets(FortniteVideoSoftware.App.Controls.SpinningWheelSlider? speedSlider)
    {
        SpeedPresetButtons.ConfigureBaseButton(
            this,
            _baseSpeed,
            $"Set speed to Main screen base speed {SpeedPresetButtons.FormatSpeed(_baseSpeed)}");

        SpeedPresetButtons.WirePresetButtons(this, _baseSpeed, s =>
        {
            var freezeCheck = this.FindControl<Avalonia.Controls.Primitives.ToggleButton>("FreezeFrameCheck");
            if (freezeCheck != null && freezeCheck.IsChecked == true)
            {
                freezeCheck.IsChecked = false;
            }

            SpeedPresetButtons.SetSpinningWheelValue(speedSlider, s);
            _pendingSpeed = s;
            var lbl = this.FindControl<TextBlock>("PendingSpeedLabel");
            if (lbl != null) lbl.Text = $"{s:0.0}x";

            if (_selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count)
            {
                var seg = _segments[_selectedSegmentIndex];
                _segments[_selectedSegmentIndex] = new SpeedSegment(seg.StartMs, seg.EndMs, s);
                RefreshSegmentList();
                RedrawTimeline();
            }
        });
    }

    /// <summary>
    /// Issue #6: Update visibility of DELETE SEGMENT and CLEAR ALL buttons.
    /// DELETE SEGMENT: visible only when a segment is selected.
    /// CLEAR ALL: visible when any segment exists.
    /// </summary>
    private void UpdateDeleteButtonVisibility()
    {
        var deleteSegBtn = this.FindControl<Button>("DeleteSegmentBtn");
        var clearAllBtn = this.FindControl<Button>("ClearAllSegmentsBtn");

        if (deleteSegBtn != null)
            deleteSegBtn.IsVisible = _selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count;

        if (clearAllBtn != null)
            clearAllBtn.IsVisible = _segments.Count > 0;
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
        RefreshSegmentList();
        SetStatus($"Segment added: {FormatMs(start)} – {FormatMs(end)} @ {speed:0.0}x");
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
            bool isSelected = idx == _selectedSegmentIndex;

            // Row border — highlight if selected
            var border = new Border
            {
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(isSelected ? "#3d4f63" : "#1e293b")),
                BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(isSelected ? "#fde047" : "#334155")),
                BorderThickness = new Thickness(isSelected ? 2 : 1),
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(0, 2),
                Padding = new Thickness(8, 6),
                Focusable = true,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            ToolTip.SetTip(border, $"Select segment #{idx + 1}");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            // Info text
            var info = new TextBlock
            {
                Text = $"#{idx + 1}  {FormatMs(seg.StartMs)} → {FormatMs(seg.EndMs)}\n{seg.Speed:0.0}x speed",
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e2e8f0")),
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
            Avalonia.Automation.AutomationProperties.SetName(delBtn, $"Delete segment {idx + 1}");
            ToolTip.SetTip(delBtn, $"Delete segment #{idx + 1}");
            delBtn.Click += (_, e) =>
            {
                e.Handled = true;
                _segments.RemoveAt(idx);
                if (_selectedSegmentIndex == idx) _selectedSegmentIndex = -1;
                else if (_selectedSegmentIndex > idx) _selectedSegmentIndex--;
                RefreshSegmentList();
                RedrawTimeline();
                UpdateDeleteButtonVisibility();
                SetStatus("Segment removed.");
            };

            // Issue #4: Click border to select this segment
            void SelectThisSegment()
            {
                _selectedSegmentIndex = idx;
                _pendingSpeed = seg.Speed;

                var speedSlider = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("PendingSpeedSlider");
                var speedLbl = this.FindControl<TextBlock>("PendingSpeedLabel");
                if (speedSlider != null) SpeedPresetButtons.SetSpinningWheelValue(speedSlider, seg.Speed);
                if (speedLbl != null) speedLbl.Text = $"{seg.Speed:0.0}x";

                RefreshSegmentList();
                UpdateDeleteButtonVisibility();
                RedrawTimeline();
                SetStatus($"Selected segment #{idx + 1}. Change speed or press DELETE to remove.");
            }

            border.PointerEntered += (_, _) =>
            {
                if (_selectedSegmentIndex != idx)
                    border.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#26364a"));
            };
            border.PointerExited += (_, _) =>
            {
                if (_selectedSegmentIndex != idx)
                    border.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1e293b"));
            };
            border.GotFocus += (_, _) =>
            {
                if (_selectedSegmentIndex != idx)
                    border.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#38bdf8"));
            };
            border.LostFocus += (_, _) =>
            {
                if (_selectedSegmentIndex != idx)
                    border.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#334155"));
            };
            border.PointerPressed += (_, _) => SelectThisSegment();
            border.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter || e.Key == Avalonia.Input.Key.Space)
                {
                    SelectThisSegment();
                    e.Handled = true;
                }
            };

            Grid.SetColumn(info, 0);
            Grid.SetColumn(delBtn, 1);
            grid.Children.Add(info);
            grid.Children.Add(delBtn);
            border.Child = grid;
            panel.Children.Add(border);
        }
    }

    private void AttachFreezeCameraMarkerInteractions(Control marker, Canvas timelineCanvas, double durationSeconds)
    {
        marker.PointerEntered += (_, _) => MainWindow.SetTimelineCameraHover(marker, true);
        marker.PointerExited += (_, _) =>
        {
            if (!_isDraggingFreezeCamera)
            {
                MainWindow.SetTimelineCameraHover(marker, false);
            }
        };
        marker.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(marker).Properties.IsLeftButtonPressed)
            {
                return;
            }

            _isFreezeCameraSelected = true;
            _isDraggingFreezeCamera = true;
            if (_freezeCameraIconAntsRef != null) _freezeCameraIconAntsRef.IsVisible = true;
            if (_freezeCameraLineAntsRef != null) _freezeCameraLineAntsRef.IsVisible = true;
            marker.Focus();
            MainWindow.SetTimelineCameraHover(marker, true);
            MoveFreezeCameraToCanvasX(e.GetPosition(timelineCanvas).X, timelineCanvas, durationSeconds, marker, seekPreview: true);
            e.Pointer.Capture(marker);
            e.Handled = true;
        };
        marker.PointerMoved += (_, e) =>
        {
            if (!_isDraggingFreezeCamera)
            {
                return;
            }

            MoveFreezeCameraToCanvasX(e.GetPosition(timelineCanvas).X, timelineCanvas, durationSeconds, marker, seekPreview: false);
            e.Handled = true;
        };
        marker.PointerReleased += (_, e) =>
        {
            if (!_isDraggingFreezeCamera)
            {
                return;
            }

            MoveFreezeCameraToCanvasX(e.GetPosition(timelineCanvas).X, timelineCanvas, durationSeconds, marker, seekPreview: true);
            _isDraggingFreezeCamera = false;
            _isFreezeCameraSelected = true;
            e.Pointer.Capture(null);
            MainWindow.SetTimelineCameraHover(marker, false);
            RedrawTimeline();
            SetStatus($"Freeze moved to {FormatMs(_freezeTimeMs - _trimStartMs)}.");
            e.Handled = true;
        };
    }

    private void MoveFreezeCameraToCanvasX(double canvasX, Canvas timelineCanvas, double durationSeconds, Control marker, bool seekPreview)
    {
        double width = timelineCanvas.Bounds.Width;
        if (durationSeconds <= 0 || width <= 0)
        {
            return;
        }

        double clampedX = Math.Clamp(canvasX, 0, width);
        double relMs = (clampedX / width) * durationSeconds * 1000.0;
        _freezeTimeMs = _trimStartMs + relMs;
        Avalonia.Controls.Canvas.SetLeft(marker, MainWindow.ClampTimelineCameraLeft(clampedX, width));

        if (seekPreview)
        {
            SeekGranularPreviewToFreezeMarker();
        }
    }

    private void MoveFreezeCameraByPixels(int pixelDelta)
    {
        var canvas = this.FindControl<Canvas>("GranularTimelineCanvas");
        double duration = GetDuration();
        double width = canvas?.Bounds.Width ?? 0.0;
        if (_freezeTimeMs < 0 || canvas == null || duration <= 0 || width <= 0)
        {
            return;
        }

        double deltaMs = (duration * 1000.0 / width) * pixelDelta;
        double minMs = _trimStartMs;
        double maxMs = _trimStartMs + duration * 1000.0;
        _freezeTimeMs = Math.Clamp(_freezeTimeMs + deltaMs, minMs, maxMs);
        SeekGranularPreviewToFreezeMarker();
        RedrawTimeline();
        SetStatus($"Freeze moved to {FormatMs(_freezeTimeMs - _trimStartMs)}.");
    }

    private void SeekGranularPreviewToFreezeMarker()
    {
        if (_videoHost?.IpcClient == null || _freezeTimeMs < 0)
        {
            return;
        }

        _isCurrentlyFrozen = false;
        _lastFreezeTriggerAbsMs = -10000;
        _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
        _ = _videoHost.IpcClient.SendCommandAsync(
            "seek",
            (_freezeTimeMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "absolute");
    }

    // ------------------------------------------------------------------ timeline overlay
    private void RedrawTimeline()
    {
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("GranularTimelineCanvas");
        var scaleCanvas = this.FindControl<Avalonia.Controls.Canvas>("GranularTimelineScaleCanvas");
        if (canvas == null) return;

        Dispatcher.UIThread.Post(() =>
        {
            canvas.Children.Clear();
            scaleCanvas?.Children.Clear();
            double dur = GetDuration();
            double w = canvas.Bounds.Width;
            double h = Math.Max(canvas.Bounds.Height, 28);
            if (dur <= 0 || w <= 0) return;

            // ── DRAW ORDER: segments FIRST (bottom z-order), ticks LAST (top z-order) ──
            // This ensures the X-axis tick marks/labels are always visible on top.

            // --- Phase 1: Draw each segment as a colored bar (bottom layer) ---
            for (int i = 0; i < _segments.Count; i++)
            {
                var seg = _segments[i];
                double x1 = (seg.StartMs / 1000.0 / dur) * w;
                double x2 = (seg.EndMs   / 1000.0 / dur) * w;
                bool isSelected = i == _selectedSegmentIndex;

                // Background bar — color based on speed relative to base speed (NOT selection state)
                var rect = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width  = Math.Max(2, x2 - x1),
                    Height = h,
                    Fill   = new Avalonia.Media.SolidColorBrush(GetSegmentOverlayColor(seg)),
                    IsHitTestVisible = false
                };
                Avalonia.Controls.Canvas.SetLeft(rect, x1);
                Avalonia.Controls.Canvas.SetTop(rect, 0);
                canvas.Children.Add(rect);

                // Issue #4: Marching ants border for selected segment (1px yellow, animated)
                if (isSelected)
                {
                    var antsBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#fde047"));
                    double segW = Math.Max(2, x2 - x1);
                    
                    var borderRect = new Avalonia.Controls.Shapes.Rectangle
                    {
                        Width = segW,
                        Height = h,
                        Stroke = antsBrush,
                        StrokeThickness = 1,
                        StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(4, 4),
                        StrokeDashOffset = _marchingAntsOffset,
                        IsHitTestVisible = false
                    };
                    Avalonia.Controls.Canvas.SetLeft(borderRect, x1);
                    Avalonia.Controls.Canvas.SetTop(borderRect, 0);
                    canvas.Children.Add(borderRect);
                    _selectedSegmentBorderRef = borderRect;
                }
                else
                {
                    if (_selectedSegmentBorderRef != null && _selectedSegmentIndex == -1)
                        _selectedSegmentBorderRef = null;
                }
            }

            // --- Phase 2: Draw ticks/labels ON TOP of segments (top layer) ---
            double tickInterval = 5;
            if (dur > 3600) tickInterval = 300;
            else if (dur > 1800) tickInterval = 60;
            else if (dur > 300) tickInterval = 30;
            else if (dur > 60) tickInterval = 10;

            for (double t = 0; t <= dur; t += tickInterval)
            {
                double tx = (t / dur) * w;

                var tickLine = new Avalonia.Controls.Shapes.Rectangle
                {
                    Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(140, 255, 255, 255)),
                    Width = 1,
                    Height = scaleCanvas != null ? Math.Max(1, scaleCanvas.Bounds.Height) : 14,
                    IsHitTestVisible = false
                };
                Avalonia.Controls.Canvas.SetLeft(tickLine, tx);
                Avalonia.Controls.Canvas.SetTop(tickLine, 0);
                if (scaleCanvas != null) scaleCanvas.Children.Add(tickLine);
                else canvas.Children.Add(tickLine);

                // Tick label with dark background for readability over segments
                var tickText = new TextBlock
                {
                    Text = TimeSpan.FromSeconds(t).ToString(t >= 3600 ? "h\\:mm\\:ss" : "m\\:ss"),
                    Foreground = Avalonia.Media.Brushes.White,
                    FontSize = 9,
                    Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(180, 0, 0, 0)),
                    Padding = new Thickness(2, 0),
                    IsHitTestVisible = false
                };
                Avalonia.Controls.Canvas.SetLeft(tickText, tx + 2);
                Avalonia.Controls.Canvas.SetTop(tickText, 0);
                if (scaleCanvas != null) scaleCanvas.Children.Add(tickText);
                else canvas.Children.Add(tickText);
            }

            // Draw pending start marker
            if (_pendingStartMs >= 0)
            {
                double px = (_pendingStartMs / 1000.0 / dur) * w;
                var line = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = 2, Height = h,
                    Fill = Avalonia.Media.Brushes.SeaGreen,
                    IsHitTestVisible = false
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
                    Fill = Avalonia.Media.Brushes.SeaGreen,
                    IsHitTestVisible = false
                };
                Avalonia.Controls.Canvas.SetLeft(line, px);
                canvas.Children.Add(line);
            }

            // ── Phase 4: Draw Freeze Frame Camera (top-most layer) ──
            if (_freezeTimeMs >= 0)
            {
                double freezeRelMs = Math.Clamp(_freezeTimeMs - _trimStartMs, 0, dur * 1000.0);
                double freezeX = (freezeRelMs / (dur * 1000.0)) * w;
                var freezeCam = MainWindow.CreateTimelineCameraIcon(
                    _isFreezeCameraSelected || _isDraggingFreezeCamera,
                    _marchingAntsOffset,
                    out var iconAnts,
                    out var lineAnts);
                _freezeCameraIconAntsRef = iconAnts;
                _freezeCameraLineAntsRef = lineAnts;
                ToolTip.SetTip(freezeCam, $"Freeze: {_freezeDurationS}s");
                Avalonia.Controls.Canvas.SetTop(freezeCam, -79);
                Avalonia.Controls.Canvas.SetLeft(freezeCam, MainWindow.ClampTimelineCameraLeft(freezeX, w));
                AttachFreezeCameraMarkerInteractions(freezeCam, canvas, dur);
                canvas.Children.Add(freezeCam);
            }
            else
            {
                _freezeCameraIconAntsRef = null;
                _freezeCameraLineAntsRef = null;
            }
            // ── End of Phase 4 ──
        });
    }

    // ------------------------------------------------------------------ playback timer
    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_videoHost?.IpcClient == null) return;

        double t = _videoHost.IpcClient.CurrentTime;
        double fullDur = _videoHost.IpcClient.Duration;

        // ── Trim Region Constraint ──
        // Auto-pause when playback reaches trimEnd so user never sees content beyond their clip
        double trimEndSec = (_trimEndMs > 0) ? _trimEndMs / 1000.0 : fullDur;
        if (t >= trimEndSec && !_videoHost.IpcClient.IsPaused)
        {
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
            _ = _videoHost.IpcClient.SetPropertyAsync("time-pos", trimEndSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Clamp current time to trim region for display purposes
        double trimStartSec = _trimStartMs / 1000.0;
        double relTime = Math.Max(0, t - trimStartSec);
        double trimDurSec = Math.Max(0.1, trimEndSec - trimStartSec);

        if (fullDur > 0)
        {
            if (!_isTimelineDrawn)
            {
                RedrawTimeline();
                _isTimelineDrawn = true;
            }
        }

        // ── Apply granular speed segments in real-time during preview ──
        // Segments are stored relative to trim start, and relTime is also relative.
        // So we check if the current relative position falls within any segment.
        double currentRelMs = relTime * 1000.0;
        double currentAbsMs = currentRelMs + _trimStartMs;

        // Single instance Freeze Frame playback logic
        if (_freezeTimeMs >= 0 && !_isCurrentlyFrozen && !_videoHost.IpcClient.IsPaused)
        {
            if (currentAbsMs >= _freezeTimeMs && currentAbsMs <= _freezeTimeMs + 150 && Math.Abs(currentAbsMs - _lastFreezeTriggerAbsMs) > 1000)
            {
                _isCurrentlyFrozen = true;
                _lastFreezeTriggerAbsMs = _freezeTimeMs;
                _freezeStartTime = DateTime.UtcNow;
                _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
                _ = _videoHost.IpcClient.SetPropertyAsync("time-pos", (_freezeTimeMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
        }
        else if (_isCurrentlyFrozen)
        {
            if ((DateTime.UtcNow - _freezeStartTime).TotalSeconds >= _freezeDurationS)
            {
                _isCurrentlyFrozen = false;
                _ = _videoHost.IpcClient.SetPropertyAsync("pause", "no");
            }
            else
            {
                return; // UI global timecode stops counting
            }
        }

        if (!_videoHost.IpcClient.IsPaused && _segments.Count > 0)
        {
            double targetSpeed = GetEditorSpeedForPosition(currentRelMs);
            if (Math.Abs(targetSpeed - _lastAppliedSpeed) > 0.001)
            {
                _lastAppliedSpeed = targetSpeed;
                _ = _videoHost.IpcClient.SetPropertyAsync("speed",
                    targetSpeed.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        // Update play/pause button label
        var playIcon = this.FindControl<Avalonia.Controls.Shapes.Polygon>("PlayIcon");
        var pauseIcon = this.FindControl<StackPanel>("PauseIcon");
        if (playIcon != null && pauseIcon != null)
        {
            bool isPaused = _videoHost.IpcClient.IsPaused;
            if (_isCurrentlyFrozen) isPaused = false; // Spoof playing state during artificial freeze hold
            playIcon.IsVisible = isPaused;
            pauseIcon.IsVisible = !isPaused;
        }

        // Show time relative to the trim region (0:00 = MARK START)
        var elapsed = this.FindControl<TextBlock>("GranularTimeElapsed");
        if (elapsed != null) elapsed.Text = FormatSec(relTime);

        // Slider maps to the trim region, not the full video
        var slider = this.FindControl<Slider>("GranularTimeline");
        if (slider != null && trimDurSec > 0 && !slider.IsPointerOver)
        {
            _isSeeking = true;
            slider.Value = (relTime / trimDurSec) * 1000.0;
            _isSeeking = false;
        }

    }

    // ------------------------------------------------------------------ helpers
    /// <summary>
    /// Returns the current playback position relative to the trim region.
    /// 0.0 = MARK START position.
    /// </summary>
    private double GetCurrentTime()
    {
        if (_videoHost?.IpcClient == null) return 0;
        double absTime = _videoHost.IpcClient.CurrentTime;
        double relTime = absTime - (_trimStartMs / 1000.0);
        double trimEndSec = (_trimEndMs > 0) ? _trimEndMs / 1000.0 : double.MaxValue;
        // Clamp to valid trim range
        if (relTime < 0) relTime = 0;
        if (relTime > trimEndSec - (_trimStartMs / 1000.0)) relTime = trimEndSec - (_trimStartMs / 1000.0);
        return relTime;
    }

    /// <summary>
    /// Looks up the playback speed for a given relative position (in ms from trim start).
    /// Returns the segment's speed if the position falls within a speed segment,
    /// otherwise returns the base speed. Freeze segments (speed ≈ 0) return 0.
    /// </summary>
    private double GetEditorSpeedForPosition(double relPosMs)
    {
        foreach (var seg in _segments)
        {
            if (relPosMs >= seg.StartMs && relPosMs < seg.EndMs)
            {
                return seg.Speed; // 0 = freeze frame
            }
        }
        return _baseSpeed;
    }

    /// <summary>
    /// Returns the duration of the trim region (not the full video).
    /// </summary>
    private int? FindSegmentAtPosition(int positionMs)
    {
        for (int i = 0; i < _segments.Count; i++)
        {
            if (positionMs >= _segments[i].StartMs && positionMs <= _segments[i].EndMs)
                return i;
        }
        return null;
    }

    private double GetDuration()
    {
        if (_videoHost?.IpcClient == null) return 0;
        double fullDur = _videoHost.IpcClient.Duration;
        double trimEndSec = (_trimEndMs > 0) ? _trimEndMs / 1000.0 : fullDur;
        double trimDur = Math.Max(0.1, trimEndSec - (_trimStartMs / 1000.0));

        // Update duration label to show trim-relative duration
        var durLbl = this.FindControl<TextBlock>("GranularDuration");
        if (durLbl != null && durLbl.Text != FormatSec(trimDur))
            Dispatcher.UIThread.Post(() => durLbl.Text = FormatSec(trimDur));
        return trimDur;
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

    /// <summary>
    /// Returns the timeline overlay color for a speed segment, based on its speed
    /// relative to the base (natural) speed:
    ///   • Freeze (≈0x)    → blue
    ///   • Below base speed → red
    ///   • ≥ base speed     → green
    /// The color is independent of selection state so that live edits recolor
    /// immediately even while a segment is highlighted.
    /// </summary>
    private Avalonia.Media.Color GetSegmentOverlayColor(SpeedSegment seg)
    {
        double speed = seg.Speed;
        double baseSpd = _baseSpeed;
        
        if (speed < 0.01)
        {
            // Freeze frame: extreme slow
            return Avalonia.Media.Color.FromArgb(230, 96, 165, 250); // 90% blue
        }
        else if (speed < baseSpd - 0.0001)
        {
            // SLOW: BaseSpeed down to 0.1 maps to ~20% (51) -> 90% (230)
            double factor = Math.Clamp((baseSpd - speed) / Math.Max(0.001, baseSpd - 0.1), 0.0, 1.0);
            byte alpha = (byte)(51 + factor * (230 - 51));
            return Avalonia.Media.Color.FromArgb(alpha, 239, 68, 68); // Red
        }
        else
        {
            // FAST: BaseSpeed up to 4.1 maps to ~20% (51) -> 90% (230)
            double factor = Math.Clamp((speed - baseSpd) / Math.Max(0.001, 4.1 - baseSpd), 0.0, 1.0);
            byte alpha = (byte)(51 + factor * (230 - 51));
            return Avalonia.Media.Color.FromArgb(alpha, 34, 197, 94); // Green
        }
    }

    private void ShowFeedback(string text)
    {
        var popup = this.FindControl<Avalonia.Controls.Primitives.Popup>("GranularFeedbackPopup");
        var popupBorder = this.FindControl<Border>("GranularFeedbackPopupBorder");
        var popupText = this.FindControl<TextBlock>("GranularFeedbackPopupText");
        var videoBorder = this.FindControl<Border>("GranularVideoAreaBorder");
        FloatingFeedback.Show(popup, popupBorder, popupText, videoBorder, text);
    }

    private void SetStatus(string msg)
    {
        var lbl = this.FindControl<TextBlock>("BottomStatusLabel");
        if (lbl != null) lbl.Text = msg;
    }

    // ------------------------------------------------------------------ cleanup
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
        FortniteVideoSoftware.App.Infrastructure.WindowManager.SaveAll();

        // Save bounds BEFORE hiding the window, because hiding resets Position/Size on Windows
        try
        {
            string appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            string settingsDir = System.IO.Path.Combine(appData, "FortniteVideoSoftware", "Settings");
            System.IO.Directory.CreateDirectory(settingsDir);
            string boundsFile = System.IO.Path.Combine(settingsDir, "Bounds.json");
            
            var state = System.IO.File.Exists(boundsFile)
                ? System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(System.IO.File.ReadAllText(boundsFile)) ?? new System.Text.Json.Nodes.JsonObject()
                : new System.Text.Json.Nodes.JsonObject();

            state["GranularWidth"] = this.Bounds.Width;
            state["GranularHeight"] = this.Bounds.Height;
            state["GranularX"] = this.Position.X;
            state["GranularY"] = this.Position.Y;

            System.IO.File.WriteAllText(boundsFile, state.ToJsonString());
        }
        catch { }

        // Hide the window instantly so the app feels incredibly fast and responsive
        this.Hide();

        RuntimeLog.Info("Granular", "Granular Speed Editor closing. Stopping timers and saving bounds async.");
        _playbackTimer?.Stop();
        _marchingAntsTimer?.Stop();

        try
        {
            // Perform the heavy Mutex locking and file I/O ASYNCHRONOUSLY


            if (_videoHost?.IpcClient != null)
            {
                await _videoHost.IpcClient.SendCommandAsync("stop");
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Granular", $"Error saving state during close: {ex.Message}");
        }
        finally
        {
            // Mark as safe and programmatically re-trigger the close
            _isSafeToClose = true;
            this.Close();
        }
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        
        // Task 6: Granular Speed Editor State Persistence
        try
        {
            string appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            string settingsDir = System.IO.Path.Combine(appData, "FortniteVideoSoftware", "Settings");
            string boundsFile = System.IO.Path.Combine(settingsDir, "Bounds.json");
            
            double targetW = 1600;
            double targetH = 840;
            double targetX = double.NaN;
            double targetY = double.NaN;

            if (System.IO.File.Exists(boundsFile))
            {
                var state = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(System.IO.File.ReadAllText(boundsFile));
                if (state != null)
                {
                    targetW = state["GranularWidth"]?.GetValue<double>() ?? targetW;
                    targetH = state["GranularHeight"]?.GetValue<double>() ?? targetH;
                    targetX = state["GranularX"]?.GetValue<double>() ?? double.NaN;
                    targetY = state["GranularY"]?.GetValue<double>() ?? double.NaN;
                }
            }

            var screens = this.Screens.All.ToList();
            var screen = !double.IsNaN(targetX) && !double.IsNaN(targetY)
                ? screens.FirstOrDefault(s => s.WorkingArea.Intersects(new Avalonia.PixelRect((int)targetX, (int)targetY, Math.Max(1, (int)Math.Ceiling(targetW)), Math.Max(1, (int)Math.Ceiling(targetH)))))
                : null;
            screen ??= this.Screens.ScreenFromVisual(this) ?? this.Screens.Primary ?? screens.FirstOrDefault();
            if (screen != null)
            {
                double workW = screen.WorkingArea.Width;
                double workH = screen.WorkingArea.Height;
                targetW = System.Math.Min(System.Math.Max(320, targetW), System.Math.Max(1, workW - 40));
                targetH = System.Math.Min(System.Math.Max(240, targetH), System.Math.Max(1, workH - 40));

                this.Width = targetW;
                this.Height = targetH;
                
                if (!double.IsNaN(targetX) && !double.IsNaN(targetY))
                {
                    int widthPx = Math.Max(1, (int)Math.Ceiling(targetW));
                    int heightPx = Math.Max(1, (int)Math.Ceiling(targetH));
                    int pxX = Math.Max(screen.WorkingArea.X, Math.Min((int)targetX, screen.WorkingArea.Right - widthPx));
                    int pxY = Math.Max(screen.WorkingArea.Y, Math.Min((int)targetY, screen.WorkingArea.Bottom - heightPx));
                    this.Position = new Avalonia.PixelPoint(pxX, pxY);
                    this.WindowStartupLocation = WindowStartupLocation.Manual;
                }
                else
                {
                    this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                }
            }
        }
        catch { }

    }

    protected override void OnClosed(EventArgs e)
    {
        RuntimeLog.Info("Granular", "Granular Speed Editor closed. Disposing resources.");

        _playbackTimer?.Stop();
        _marchingAntsTimer?.Stop();
        base.OnClosed(e);
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








