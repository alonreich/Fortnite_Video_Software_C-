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

    private bool _gpuLiveZoomPreview;
    private string _lastLiveCrop = "";

    private int _selectedSegmentIndex = -1;
    private DispatcherTimer? _marchingAntsTimer;
    private double _marchingAntsOffset = 0;

    private DispatcherTimer? _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _isTimelineDrawn = false;
    private double _lastAppliedSpeed = 1.0;

    public bool Accepted { get; private set; }
    public IReadOnlyList<SpeedSegment> ResultSegments => _segments
        .Select(s => s with
        {
            StartMs = s.StartMs + (int)_trimStartMs,
            EndMs = s.EndMs + (int)_trimStartMs,
            ZoomStartMs = s.ZoomStartMs.HasValue ? s.ZoomStartMs.Value + _trimStartMs : (double?)null,
            ZoomEndMs = s.ZoomEndMs.HasValue ? s.ZoomEndMs.Value + _trimStartMs : (double?)null
        })
        .ToList()
        .AsReadOnly();

    private bool _isMobileFormat;
    private string _originalResolution = "1920x1080";
    public double ResultBaseSpeed => _baseSpeed;
    public double ResultFreezeTimeMs => _freezeTimeMs;
    public double ResultFreezeDurationS => _freezeDurationS;

    private double _freezeTimeMs = -1;
    private double _freezeDurationS = 1.0;
    private double _selectedFreezePresetS = -1.0;
    private double _lastFreezeTriggerAbsMs = -10000;
    private double _prevFreezeTickAbsMs = -1;
    private bool _isCurrentlyFrozen = false;
    private DateTime _freezeStartTime;
    private bool _isFreezeCameraSelected = false;
    private bool _isDraggingFreezeCamera = false;
    private int _draggingSegmentIndex = -1;
    private enum SegDragMode { None, Move, ResizeStart, ResizeEnd }
    private SegDragMode _segDragMode = SegDragMode.None;
    private double _dragOrigStartMs;
    private double _dragOrigEndMs;
    private double _dragStartPointerMs;
    private bool _isCanvasScrubbing;
    private const int SegMinWidthMs = 200;
    private const int SegGapMs = 1000;
    private Avalonia.Controls.Shapes.Rectangle? _freezeCameraIconAntsRef;
    private Avalonia.Controls.Shapes.Rectangle? _freezeCameraLineAntsRef;
    private Avalonia.Controls.Shapes.Rectangle? _selectedSegmentBorderRef;
    private DispatcherTimer? _freezePulseTimer;

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
    public GranularSpeedEditorWindow(string videoPath, double trimStartMs = 0, double trimEndMs = 0, IEnumerable<SpeedSegment>? existingSegments = null, double baseSpeed = 1.1, double freezeTimeMs = -1, double freezeDurationS = 1.0, bool isMobileFormat = false, string originalResolution = "1920x1080")
    {
        _videoPath = videoPath;
        _trimStartMs = trimStartMs;
        _trimEndMs = trimEndMs;
        _baseSpeed = baseSpeed;
        _freezeTimeMs = freezeTimeMs;
        _freezeDurationS = freezeDurationS;
        _isMobileFormat = isMobileFormat;
        if (!string.IsNullOrWhiteSpace(originalResolution)) _originalResolution = originalResolution;
        _selectedFreezePresetS = -1.0;

        try { _gpuLiveZoomPreview = FortniteVideoSoftware.Core.Media.VideoRenderMode.Current.UseHardwareAcceleration; }
        catch { _gpuLiveZoomPreview = false; }
        RuntimeLog.Info("Granular", $"Live zoom preview path: {(_gpuLiveZoomPreview ? "GPU (mpv video-crop simulation)" : "CPU (yellow box overlay only)")}");

        InitializeComponent();

        this.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, (s, e) =>
        {
            if (_isCanvasScrubbing)
            {
                _isCanvasScrubbing = false;
                e.Pointer.Capture(null);
            }
            if (_draggingSegmentIndex != -1)
            {
                var finishedMode = _segDragMode;
                int finishedIdx = _draggingSegmentIndex;
                if (_segDragMode != SegDragMode.None && _draggingSegmentIndex < _segments.Count)
                {
                    var seg = _segments[_draggingSegmentIndex];
                    RuntimeLog.Info("Granular", $"Segment #{_draggingSegmentIndex + 1} settled: rel {FormatMs(seg.StartMs)}–{FormatMs(seg.EndMs)} @ {seg.Speed:0.0}x (abs {FormatMs(seg.StartMs + _trimStartMs)}–{FormatMs(seg.EndMs + _trimStartMs)}).");
                    SetStatus($"Segment #{_draggingSegmentIndex + 1} set to {FormatMs(seg.StartMs)}–{FormatMs(seg.EndMs)} @ {seg.Speed:0.0}x.");
                }
                _segDragMode = SegDragMode.None;
                _draggingSegmentIndex = -1;
                e.Pointer.Capture(null);
                HideDragReadout();
                RefreshSegmentList();
                RedrawTimeline();

                if (finishedMode != SegDragMode.None && finishedIdx >= 0 && finishedIdx < _segments.Count
                    && _videoHost?.IpcClient?.IsPaused == true)
                {
                    var fseg = _segments[finishedIdx];
                    bool didChange = Math.Abs(_dragOrigStartMs - fseg.StartMs) > 1.0 || Math.Abs(_dragOrigEndMs - fseg.EndMs) > 1.0;
                    if (didChange)
                    {
                        double seekRelSec = (finishedMode == SegDragMode.ResizeEnd ? fseg.EndMs : fseg.StartMs) / 1000.0;
                        _ = SeekInternal(seekRelSec);
                    }
                }
            }
            if (_isDraggingFreezeCamera)
            {
                _isDraggingFreezeCamera = false;
                RefreshSegmentList();
                RedrawTimeline();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble);

        FortniteVideoSoftware.App.WindowBoundsHelper.Track(this, "GranularBounds");
        FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged += OnGlobalMasterVolumeChanged;
        
        _pendingSpeed = baseSpeed;
        _lastAppliedSpeed = baseSpeed;

        var initialSpeedSlider = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("PendingSpeedSlider"); if(initialSpeedSlider!=null)initialSpeedSlider.SetRange(1, 40);
        SpeedPresetButtons.SetSpinningWheelValue(initialSpeedSlider, _pendingSpeed);
        var initialSpeedLabel = this.FindControl<TextBlock>("PendingSpeedLabel");
        if (initialSpeedLabel != null) initialSpeedLabel.Text = $"{_pendingSpeed:0.0}x";
        
        if (existingSegments != null)
        {
            foreach (var seg in existingSegments)
            {
                int relStart = (int)(seg.StartMs - _trimStartMs);
                int relEnd = (int)(seg.EndMs - _trimStartMs);
                if (relEnd > 0 && relStart < (int)(_trimEndMs - _trimStartMs))
                {
                    relStart = Math.Max(0, relStart);
                    int maxEnd = _trimEndMs > 0 ? (int)(_trimEndMs - _trimStartMs) : int.MaxValue;
                    relEnd = Math.Min(relEnd, maxEnd);
                    _segments.Add(new SpeedSegment(relStart, relEnd, seg.Speed,
                        seg.ZoomX, seg.ZoomY, seg.ZoomW, seg.ZoomH, seg.ZoomOrigRes, seg.ZoomSlow,
                        seg.ZoomStartMs.HasValue ? seg.ZoomStartMs.Value - _trimStartMs : (double?)null,
                        seg.ZoomEndMs.HasValue ? seg.ZoomEndMs.Value - _trimStartMs : (double?)null));
                }
            }
        }

        this.Loaded += (s, e) => InitializeMpv();
        WireUpControls();
        WireZoomControls();
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
                if (txt != null) txt.Text = " UNFREEZE IMAGE ";
            }
        }

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
                RuntimeLog.Info("Granular", $"Using MPV: {System.IO.Path.GetFileName(mpvPath)}");
                RuntimeLog.Debug("Granular", $"Using MPV path: {mpvPath}");
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

                await LoadVideoAsync();
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

    private void GranularKeyUpHandler(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Avalonia.Controls.TextBox or Avalonia.Controls.NumericUpDown)
            return;

        var kb = FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.KeyBinds;
        var playPause = new Avalonia.Input.KeyGesture(kb.PlayPause);
        var markStart = new Avalonia.Input.KeyGesture(kb.MarkStart);
        var markEnd = new Avalonia.Input.KeyGesture(kb.MarkEnd);

        if (playPause.Matches(e) || markStart.Matches(e) || markEnd.Matches(e) || e.Key is Avalonia.Input.Key.Space or Avalonia.Input.Key.Left or Avalonia.Input.Key.Right)
        {
            e.Handled = true;
        }
    }

    private void GranularKeyDownHandler(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Avalonia.Controls.TextBox or Avalonia.Controls.NumericUpDown)
            return;

        var kb = FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.KeyBinds;

        if (_isFreezeCameraSelected && _freezeTimeMs >= 0 && e.Key is Avalonia.Input.Key.Left or Avalonia.Input.Key.Right)
        {
            int frames = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) || e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift) ? 10 : 1;
            MoveFreezeCameraByFrames(e.Key == Avalonia.Input.Key.Left ? -frames : frames);
            e.Handled = true;
            return;
        }

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
            double currentAbs = _videoHost?.IpcClient?.CurrentTime ?? 0;
            double trimEndSec = (_trimEndMs > 0) ? _trimEndMs / 1000.0 : double.MaxValue;
            double target = Math.Min(currentAbs + 5, trimEndSec);
            _ = SeekInternal(target - (_trimStartMs / 1000.0));
            e.Handled = true;
        }
        else if (seekBack.Matches(e))
        {
            double currentAbs = _videoHost?.IpcClient?.CurrentTime ?? 0;
            double target = Math.Max(currentAbs - 5, _trimStartMs / 1000.0);
            _ = SeekInternal(target - (_trimStartMs / 1000.0));
            e.Handled = true;
        }
    }

    private async Task SeekInternal(double time) {
        if (_isSeeking) { _nextSeekTarget = time; return; }
        _isSeeking = true;
        double absTime = (_trimStartMs / 1000.0) + time;
        double trimEndSec = (_trimEndMs > 0) ? _trimEndMs / 1000.0 : double.MaxValue;
        absTime = Math.Min(absTime, trimEndSec);
        absTime = Math.Max(absTime, _trimStartMs / 1000.0);
        if (_videoHost?.IpcClient != null) await _videoHost.IpcClient.SendCommandAsync("seek", absTime.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
    }

    private async Task LoadVideoAsync()
    {
        if (string.IsNullOrWhiteSpace(_videoPath) || _videoHost?.IpcClient == null) return;

        double startSec = _trimStartMs / 1000.0;

        await _videoHost.IpcClient.LoadFileAsync(_videoPath, startSec);
        await _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
        RuntimeLog.Info("Granular", $"Loaded preview video at {startSec:0.###}s.");
    }

    private void WireUpControls()
    {
        // The seek canvas covers the slider and takes all pointer input, so it is the canvas that
        // has to report hover/drag to the knob. See Controls/TimelineKnob.cs.
        Controls.TimelineKnob.Attach(
            this.FindControl<Avalonia.Controls.Canvas>("GranularTimelineCanvas"),
            this.FindControl<Slider>("GranularTimeline"));

        var canvas = this.FindControl<Avalonia.Controls.Canvas>("GranularTimelineCanvas");
        if (canvas != null)
        {
            canvas.SizeChanged += (s, e) => RedrawTimeline();

            canvas.IsHitTestVisible = true;
            canvas.Background = Avalonia.Media.Brushes.Transparent;

            canvas.PointerPressed += (s, e) =>
            {
                double dur = GetDuration();
                if (dur <= 0) return;
                double w = canvas.Bounds.Width;
                if (w <= 0) return;
                double totalMs = dur * 1000.0;
                double msPerPx = totalMs / w;
                double pointerMs = Math.Clamp(e.GetPosition(canvas).X * msPerPx, 0, totalMs);
                double edgeMs = 8.0 * msPerPx;

                int hitIdx = -1;
                SegDragMode mode = SegDragMode.None;
                for (int i = 0; i < _segments.Count; i++)
                {
                    var sg = _segments[i];
                    if (Math.Abs(pointerMs - sg.StartMs) <= edgeMs) { hitIdx = i; mode = SegDragMode.ResizeStart; break; }
                    if (Math.Abs(pointerMs - sg.EndMs) <= edgeMs) { hitIdx = i; mode = SegDragMode.ResizeEnd; break; }
                    if (pointerMs > sg.StartMs && pointerMs < sg.EndMs) { hitIdx = i; mode = SegDragMode.Move; break; }
                }

                if (hitIdx >= 0 && mode != SegDragMode.None)
                {
                    _selectedSegmentIndex = hitIdx;
                    var seg = _segments[hitIdx];
                    _pendingSpeed = seg.Speed;

                    var speedSlider = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("PendingSpeedSlider");
                    var speedLbl = this.FindControl<TextBlock>("PendingSpeedLabel");
                    if (speedSlider != null && seg.Speed >= 0.01) SpeedPresetButtons.SetSpinningWheelValue(speedSlider, seg.Speed);
                    if (speedLbl != null) speedLbl.Text = $"{seg.Speed:0.0}x";
                    UpdateDeleteButtonVisibility();

                    _draggingSegmentIndex = hitIdx;
                    _segDragMode = mode;
                    _dragOrigStartMs = seg.StartMs;
                    _dragOrigEndMs = seg.EndMs;
                    _dragStartPointerMs = pointerMs;
                    e.Pointer.Capture(canvas);
                    SetStatus(mode == SegDragMode.Move
                        ? $"Moving segment #{hitIdx + 1} — release to set."
                        : $"Resizing segment #{hitIdx + 1} — release to set.");
                    UpdateDragReadout(seg.StartMs, seg.EndMs);
                    RedrawTimeline();
                    e.Handled = true;
                    return;
                }

                if (_isFreezeCameraSelected)
                {
                    _isFreezeCameraSelected = false;
                    _isDraggingFreezeCamera = false;
                }
                if (_selectedSegmentIndex >= 0)
                {
                    _selectedSegmentIndex = -1;
                    UpdateDeleteButtonVisibility();
                }

                _isCanvasScrubbing = true;
                e.Pointer.Capture(canvas);
                var tl = this.FindControl<Slider>("GranularTimeline");
                if (tl != null) tl.Value = (pointerMs / totalMs) * 1000.0;
                RedrawTimeline();
                e.Handled = true;
            };

            canvas.PointerMoved += (s, e) =>
            {
                double dur = GetDuration();
                if (dur <= 0) return;
                double w = canvas.Bounds.Width;
                if (w <= 0) return;
                double totalMs = dur * 1000.0;
                double msPerPx = totalMs / w;
                double pointerMs = Math.Clamp(e.GetPosition(canvas).X * msPerPx, 0, totalMs);

                if (_isCanvasScrubbing)
                {
                    var tl = this.FindControl<Slider>("GranularTimeline");
                    if (tl != null) tl.Value = (pointerMs / totalMs) * 1000.0;
                    e.Handled = true;
                    return;
                }

                if (_draggingSegmentIndex < 0 || _draggingSegmentIndex >= _segments.Count || _segDragMode == SegDragMode.None)
                    return;

                int idx = _draggingSegmentIndex;

                double lowerBound = 0;
                double upperBound = totalMs;
                for (int j = 0; j < _segments.Count; j++)
                {
                    if (j == idx) continue;
                    if (_segments[j].EndMs <= _dragOrigStartMs)
                        lowerBound = Math.Max(lowerBound, _segments[j].EndMs + SegGapMs);
                    if (_segments[j].StartMs >= _dragOrigEndMs)
                        upperBound = Math.Min(upperBound, _segments[j].StartMs - SegGapMs);
                }

                double newStart = _dragOrigStartMs;
                double newEnd = _dragOrigEndMs;

                upperBound = Math.Max(upperBound, lowerBound + SegMinWidthMs);

                if (_segDragMode == SegDragMode.Move)
                {
                    double width = Math.Max(SegMinWidthMs, _dragOrigEndMs - _dragOrigStartMs);
                    double delta = pointerMs - _dragStartPointerMs;
                    newStart = Math.Clamp(_dragOrigStartMs + delta, lowerBound, Math.Max(lowerBound, upperBound - width));
                    newEnd = newStart + width;
                }
                else if (_segDragMode == SegDragMode.ResizeStart)
                {
                    newStart = Math.Clamp(pointerMs, lowerBound, Math.Max(lowerBound, _dragOrigEndMs - SegMinWidthMs));
                    newEnd = _dragOrigEndMs;
                }
                else if (_segDragMode == SegDragMode.ResizeEnd)
                {
                    newEnd = Math.Clamp(pointerMs, Math.Min(upperBound, _dragOrigStartMs + SegMinWidthMs), upperBound);
                    newStart = _dragOrigStartMs;
                }

                double snapTol = 8.0 * msPerPx;
                var snapTl = this.FindControl<Slider>("GranularTimeline");
                double playheadMs = snapTl != null ? (snapTl.Value / 1000.0) * totalMs : -1;
                double NearestSnap(double ms)
                {
                    double best = ms, bestD = snapTol;
                    void Try(double t) { if (t < 0) return; double d = Math.Abs(ms - t); if (d < bestD) { bestD = d; best = t; } }
                    Try(0); Try(totalMs); Try(playheadMs);
                    for (int j = 0; j < _segments.Count; j++) { if (j == idx) continue; Try(_segments[j].StartMs); Try(_segments[j].EndMs); }
                    return best;
                }
                double segWidth = newEnd - newStart;
                if (_segDragMode == SegDragMode.Move)
                {
                    double sS = NearestSnap(newStart), sE = NearestSnap(newEnd);
                    if (Math.Abs(sS - newStart) <= Math.Abs(sE - newEnd) && sS != newStart)
                        { newStart = Math.Clamp(sS, lowerBound, Math.Max(lowerBound, upperBound - segWidth)); newEnd = newStart + segWidth; }
                    else if (sE != newEnd)
                        { newEnd = Math.Clamp(sE, Math.Min(upperBound, lowerBound + segWidth), upperBound); newStart = newEnd - segWidth; }
                }
                else if (_segDragMode == SegDragMode.ResizeStart)
                    newStart = Math.Clamp(NearestSnap(newStart), lowerBound, Math.Max(lowerBound, newEnd - SegMinWidthMs));
                else if (_segDragMode == SegDragMode.ResizeEnd)
                    newEnd = Math.Clamp(NearestSnap(newEnd), Math.Min(upperBound, newStart + SegMinWidthMs), upperBound);

                _segments[idx] = _segments[idx] with { StartMs = newStart, EndMs = newEnd };
                UpdateDragReadout(newStart, newEnd);
                UpdateDraggingVisuals(idx, newStart, newEnd);
                double followRelSec = (_segDragMode == SegDragMode.ResizeEnd ? newEnd : newStart) / 1000.0;
                _ = SeekInternal(followRelSec);
                e.Handled = true;
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

        var playPause = this.FindControl<Button>("GranularPlayPause");
        playPause?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            RuntimeLog.Info("UI", "User toggled Play/Pause in Granular Speed Editor.");
            if (_isCurrentlyFrozen)
            {
                _isCurrentlyFrozen = false;
                return;
            }
            if (_videoHost?.IpcClient != null) _ = _videoHost.IpcClient.SetPropertyAsync("pause", _videoHost.IpcClient.IsPaused ? "no" : "yes");
        });


        var markStart = this.FindControl<Button>("MarkStartBtn");
        markStart?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            RuntimeLog.Info("UI", "User clicked Mark Start in Granular Speed Editor.");

            int currentMs = (int)(GetCurrentTime() * 1000);

            int? overlapIdx = FindSegmentAtPosition(currentMs);
            if (overlapIdx.HasValue)
            {
                var overlapping = _segments[overlapIdx.Value];
                ShowFeedback($"⚠ Inside segment #{overlapIdx.Value + 1}! Delete it first.");
                SetStatus($"Cannot mark here — overlaps segment #{overlapIdx.Value + 1} [{FormatMs(overlapping.StartMs)} – {FormatMs(overlapping.EndMs)}]. Delete it first.");
                return;
            }

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

            if (_pendingStartMs >= 0 && currentMs <= _pendingStartMs)
            {
                ShowFeedback("⚠ END can't be before START");
                SetStatus($"Cannot mark END at {FormatMs(currentMs)} — it must be AFTER the START at {FormatMs(_pendingStartMs)}.");
                return;
            }

            _pendingEndMs = currentMs;

            _selectedSegmentIndex = -1;

            if (_pendingStartMs < 0)
            {
                int prevEndMs = -1;
                foreach (var s in _segments)
                {
                    if (s.EndMs <= _pendingEndMs && (int)s.EndMs > prevEndMs)
                        prevEndMs = (int)s.EndMs;
                }

                if (prevEndMs < 0)
                {
                    _pendingStartMs = 0;
                }
                else
                {
                    _pendingStartMs = prevEndMs + 1000;
                    if (_pendingStartMs > _pendingEndMs)
                    {
                        _pendingStartMs = prevEndMs;
                    }
                }
            }

            if (_videoHost?.IpcClient != null)
            {
                _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");
                
                var playIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon");
                var pauseIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PauseIcon");
                if (playIcon != null && pauseIcon != null)
                {
                    playIcon.IsVisible = true;
                    pauseIcon.IsVisible = false;
                }
            }

            ShowFeedback($"SEGMENT ADDED: {FormatMs(_pendingEndMs)}");
            
            AddPendingSegment();

            if (_segments.Count > 0)
            {
                _selectedSegmentIndex = _segments.Count - 1;
            }
            UpdateDeleteButtonVisibility();
            RedrawTimeline();
        });

        var speedSlider = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("PendingSpeedSlider");
        if (speedSlider != null)
        {
            speedSlider.ValueChanged += (_, e) =>
            {
                _pendingSpeed = Math.Round(e / 10.0, 2);
                var lbl = this.FindControl<TextBlock>("PendingSpeedLabel");
                if (lbl != null) lbl.Text = $"{_pendingSpeed:0.0}x";

                if (_selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count)
                {
                    var seg = _segments[_selectedSegmentIndex];
                    _segments[_selectedSegmentIndex] = seg with { Speed = _pendingSpeed };
                    RefreshSegmentList();
                    RedrawTimeline();
                }
            };
        }

        WireUpSpeedPresets(speedSlider);

        WireUpFreezeImage();

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

        var acceptBtn = this.FindControl<Button>("AcceptGranularBtn");
        if (acceptBtn != null) acceptBtn.Click += (s, e) => {
            RuntimeLog.Info("UI", "User clicked Accept in Granular Speed Editor.");
            Accepted = true;
            Close();
        };

        var confirmCancel = this.FindControl<Button>("ConfirmCancelGranularBtn");
        confirmCancel?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            var btn = this.FindControl<Button>("CancelGranularBtn");
            btn?.Flyout?.Hide();
            RuntimeLog.Info("UI", "User confirmed Cancel in Granular Speed Editor.");
            Close();
        });

        UpdateTooltips();
        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, GranularKeyDownHandler, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(Avalonia.Input.InputElement.KeyUpEvent, GranularKeyUpHandler, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void WireUpFreezeImage()
    {
        var freezeImageToggle = this.FindControl<Button>("FreezeImageToggle");

        var freezePresets = new[] {
            this.FindControl<Button>("FreezePreset05"), this.FindControl<Button>("FreezePreset10"), this.FindControl<Button>("FreezePreset15"),
            this.FindControl<Button>("FreezePreset20"), this.FindControl<Button>("FreezePreset25"), this.FindControl<Button>("FreezePreset30")
        };
        double[] presetValues = { 0.5, 1.0, 1.5, 2.0, 2.5, 3.0 };

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
                    preset.Classes.Remove("Primary");
                    preset.Background = selectedBg;
                    preset.BorderBrush = selectedBorder;
                    preset.Foreground = selectedFg;
                }
                else
                {
                    preset.ClearValue(Avalonia.Controls.Button.BackgroundProperty);
                    preset.ClearValue(Avalonia.Controls.Button.BorderBrushProperty);
                    preset.ClearValue(Avalonia.Controls.Button.ForegroundProperty);
                }
            }
        }

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

                bool isSelected = (Math.Abs(_selectedFreezePresetS - presetValues[j]) < 0.01);
                if (isSelected) continue;

                if (j == stepperIndex)
                {
                    b.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(120, 90, 26));
                    b.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(250, 197, 22));
                    b.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(255, 247, 237));
                }
                else
                {
                    b.ClearValue(Avalonia.Controls.Button.BackgroundProperty);
                    b.ClearValue(Avalonia.Controls.Button.BorderBrushProperty);
                    b.ClearValue(Avalonia.Controls.Button.ForegroundProperty);
                }
            }
        };

        void SetControlsEnabledDuringFreezePrompt(bool enabled)
        {
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

                    _freezePulseTimer?.Stop();

                    SetFreezePresetSelection(presetIndex);

                    var hint = this.FindControl<TextBlock>("FreezeHintLabel");
                    if (hint != null) hint.IsVisible = false;
                    var hintBottom = this.FindControl<TextBlock>("FreezeHintLabelBottom");
                    if (hintBottom != null) hintBottom.IsVisible = false;

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
                        
                        _freezePulseCount = 0;
                        _freezePulseTimer?.Start();

                        var hint = this.FindControl<TextBlock>("FreezeHintLabel");
                        if (hint != null) hint.IsVisible = true;
                        var hintBottom = this.FindControl<TextBlock>("FreezeHintLabelBottom");
                        if (hintBottom != null) hintBottom.IsVisible = true;

                        SetControlsEnabledDuringFreezePrompt(false);

                        FortniteVideoSoftware.App.RuntimeLog.Info("GRANULAR_EDITOR", "State Change: User clicked 'Freeze Image' toggle but no preset was selected. Showing hint + gentle pulse + greying out other controls.");
                    }

                    double currentAbsMs = (_videoHost?.IpcClient?.CurrentTime ?? 0) * 1000.0;
                    if (_videoHost != null && _videoHost.IpcClient != null) {
                        _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
                    }
                    if (currentAbsMs < _trimStartMs) currentAbsMs = _trimStartMs;
                    if (_trimEndMs > 0 && currentAbsMs > _trimEndMs) currentAbsMs = _trimEndMs;
                    _freezeTimeMs = currentAbsMs;

                    _freezeDurationS = promptPreset ? Infrastructure.SettingsManager.Instance.Defaults.DefaultFreezeDurationS : _selectedFreezePresetS;

                    var icon = this.FindControl<TextBlock>("FreezeImageToggleIcon");
                    var txt = this.FindControl<TextBlock>("FreezeImageToggleText");
                    if (icon != null) icon.Text = "🔓";
                    if (txt != null) txt.Text = "UNFREEZE IMAGE";
                    freezeImageToggle.Classes.Remove("Primary");
                    freezeImageToggle.Classes.Add("Danger");

                    RedrawTimeline();
                    FortniteVideoSoftware.App.RuntimeLog.Info("GRANULAR_EDITOR", $"State Change: User clicked 'Freeze Image' toggle. Button set to State 2 (Active/Red - UNFREEZE IMAGE).");

                    if (!promptPreset)
                    {
                        ShowFeedback($"FREEZE CREATED: {_freezeDurationS:0.0}s");
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
                    if (txt != null) txt.Text = " FREEZE IMAGE ";
                    freezeImageToggle.Classes.Remove("Danger");
                    freezeImageToggle.Classes.Add("Primary");

                    ShowFeedback("FREEZE IMAGE REMOVED");

                    _freezePulseTimer?.Stop();
                    var hint = this.FindControl<TextBlock>("FreezeHintLabel");
                    if (hint != null) hint.IsVisible = false;
                    var hintBottom = this.FindControl<TextBlock>("FreezeHintLabelBottom");
                    if (hintBottom != null) hintBottom.IsVisible = false;

                    foreach (var b in freezePresets)
                    {
                        if (b == null) continue;
                        b.ClearValue(Avalonia.Controls.Button.BackgroundProperty);
                        b.ClearValue(Avalonia.Controls.Button.BorderBrushProperty);
                        b.ClearValue(Avalonia.Controls.Button.ForegroundProperty);
                    }

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
            SpeedPresetButtons.SetSpinningWheelValue(speedSlider, s);
            _pendingSpeed = s;
            var lbl = this.FindControl<TextBlock>("PendingSpeedLabel");
            if (lbl != null) lbl.Text = $"{s:0.0}x";

            if (_selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count)
            {
                var seg = _segments[_selectedSegmentIndex];
                _segments[_selectedSegmentIndex] = seg with { Speed = s };
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

        bool segSelected = _selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count;

        if (deleteSegBtn != null)
            deleteSegBtn.IsVisible = segSelected;

        if (clearAllBtn != null)
            clearAllBtn.IsVisible = _segments.Count > 0;

        var zoomBtn = this.FindControl<Button>("ZoomSegmentBtn");
        if (zoomBtn != null)
        {
            zoomBtn.IsVisible = segSelected;
            if (!segSelected && _zoomModeActive) ExitZoomMode();
        }
        SyncZoomModeChecksFromSegment();
    }

    private void AddPendingSegment()
    {
        if (_pendingStartMs < 0 || _pendingEndMs < 0)
        {
            SetStatus("Mark a START and END time first.");
            return;
        }

        int start = Math.Min(_pendingStartMs, _pendingEndMs);
        int end   = Math.Max(_pendingStartMs, _pendingEndMs);

        if (end - start < 10)
        {
            SetStatus("Segment must be at least 10 ms long.");
            return;
        }

        foreach (var seg in _segments)
        {
            if (start < seg.EndMs && end > seg.StartMs)
            {
                SetStatus($"Overlap with existing segment [{FormatMs(seg.StartMs)} – {FormatMs(seg.EndMs)}]. Adjust times.");
                return;
            }
            if (start < seg.EndMs + SegGapMs && end > seg.StartMs - SegGapMs)
            {
                SetStatus($"Too close to segment [{FormatMs(seg.StartMs)} – {FormatMs(seg.EndMs)}]. Keep a {SegGapMs}ms gap.");
                return;
            }
        }

        double speed = _pendingSpeed;
        _segments.Add(new SpeedSegment(start, end, speed));
        _segments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

        _pendingStartMs = -1;
        _pendingEndMs   = -1;
        RefreshSegmentList();
        SetStatus($"Segment added: {FormatMs(start)} – {FormatMs(end)} @ {speed:0.0}x");
    }

    private void RefreshSegmentList()
    {
        var panel = this.FindControl<ListBox>("SegmentsPanel");
        if (panel == null) return;
        panel.Items.Clear();

        var countLbl = this.FindControl<TextBlock>("SegmentCountLabel");
        if (countLbl != null)
            countLbl.Text = _segments.Count == 0 ? "No segments" : $"{_segments.Count} segment{(_segments.Count == 1 ? "" : "s")}";

        for (int i = 0; i < _segments.Count; i++)
        {
            int idx = i;
            var seg = _segments[i];
            bool isSelected = idx == _selectedSegmentIndex;

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

            var info = new TextBlock
            {
                Text = $"#{idx + 1}  {FormatMs(seg.StartMs)} → {FormatMs(seg.EndMs)}\n{seg.Speed:0.0}x speed{(seg.ZoomW.HasValue ? "  [ZOOMED]" : "")}",
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e2e8f0")),
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(10.5),
                FontFamily = new Avalonia.Media.FontFamily("Consolas"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };

            var delBtn = new Button
            {
                Content = "✕",
                Width = 26,
                Height = 26,
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(11),
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
            panel.Items.Add(border);
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

    private void MoveFreezeCameraByFrames(int frameDelta)
    {
        double duration = GetDuration();
        if (_freezeTimeMs < 0 || duration <= 0)
        {
            return;
        }

        double fps = 60.0;
        double deltaMs = (1000.0 / fps) * frameDelta;
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

    private bool _redrawQueued;


    private void UpdateDraggingVisuals(int segIndex, double newStartMs, double newEndMs)
    {
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("GranularTimelineCanvas");
        if (canvas == null) return;
        double w = canvas.Bounds.Width;
        double dur = GetDuration();
        if (dur <= 0 || w <= 0) return;
        
        double x1 = (newStartMs / 1000.0 / dur) * w;
        double x2 = (newEndMs / 1000.0 / dur) * w;
        double segW = Math.Max(2, x2 - x1);
        
        foreach (Avalonia.Controls.Control child in canvas.Children)
        {
            if (child.Name == $"SegRect_{segIndex}" || child.Name == $"SegBorder_{segIndex}")
            {
                Avalonia.Controls.Canvas.SetLeft(child, x1);
                child.Width = segW;
            }
            else if (child.Name == $"SegEdgeStart_{segIndex}")
            {
                Avalonia.Controls.Canvas.SetLeft(child, x1 - 12);
            }
            else if (child.Name == $"SegEdgeEnd_{segIndex}")
            {
                Avalonia.Controls.Canvas.SetLeft(child, x2 - 12);
            }
        }
    }

    private void RedrawTimeline()
    {
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("GranularTimelineCanvas");
        var scaleCanvas = this.FindControl<Avalonia.Controls.Canvas>("GranularTimelineScaleCanvas");
        if (canvas == null) return;

        if (_redrawQueued) return;
        _redrawQueued = true;

        Dispatcher.UIThread.Post(() =>
        {
            _redrawQueued = false;
            canvas.Children.Clear();
            scaleCanvas?.Children.Clear();
            double dur = GetDuration();
            double w = canvas.Bounds.Width;
            double h = Math.Max(canvas.Bounds.Height, 28);
            if (dur <= 0 || w <= 0) return;


            for (int i = 0; i < _segments.Count; i++)
            {
                var seg = _segments[i];
                double x1 = (seg.StartMs / 1000.0 / dur) * w;
                double x2 = (seg.EndMs   / 1000.0 / dur) * w;
                bool isSelected = i == _selectedSegmentIndex;

                var rect = new Avalonia.Controls.Shapes.Rectangle
                {
                    Name = $"SegRect_{i}",
                    Width  = Math.Max(2, x2 - x1),
                    Height = h,
                    Fill   = new Avalonia.Media.SolidColorBrush(GetSegmentOverlayColor(seg)),
                    IsHitTestVisible = false
                };
                Avalonia.Controls.Canvas.SetLeft(rect, x1);
                Avalonia.Controls.Canvas.SetTop(rect, 0);
                canvas.Children.Add(rect);

                if (seg.ZoomW.HasValue)
                {
                    double zsMs = seg.ZoomStartMs ?? seg.StartMs;
                    double zeMs = seg.ZoomEndMs ?? seg.EndMs;
                    
                    double zx1 = (zsMs / 1000.0 / dur) * w;
                    double zx2 = (zeMs / 1000.0 / dur) * w;

                    var zLine = new Avalonia.Controls.Shapes.Line
                    {
                        StartPoint = new Avalonia.Point(zx1, -40),
                        EndPoint = new Avalonia.Point(zx2, -40),
                        Stroke = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#80d946ef")),
                        StrokeThickness = 6,
                        IsHitTestVisible = false
                    };
                    canvas.Children.Add(zLine);

                    var zStartCam = MainWindow.CreateZoomTimelineCameraIcon(isSelected, _marchingAntsOffset, out var ants1, out var antsLine1);
                    Avalonia.Controls.Canvas.SetTop(zStartCam, -80);
                    Avalonia.Controls.Canvas.SetLeft(zStartCam, MainWindow.ClampTimelineCameraLeft(zx1, w));
                    canvas.Children.Add(zStartCam);

                    var zEndCam = MainWindow.CreateZoomTimelineCameraIcon(isSelected, _marchingAntsOffset, out var ants2, out var antsLine2);
                    Avalonia.Controls.Canvas.SetTop(zEndCam, -80);
                    Avalonia.Controls.Canvas.SetLeft(zEndCam, MainWindow.ClampTimelineCameraLeft(zx2, w));
                    canvas.Children.Add(zEndCam);

                    AttachZoomMarkerInteractions(zStartCam, i, true, canvas, dur);
                    AttachZoomMarkerInteractions(zEndCam, i, false, canvas, dur);
                }

                if (isSelected)
                {
                    var antsBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#fde047"));
                    double segW = Math.Max(2, x2 - x1);
                    
                    var borderRect = new Avalonia.Controls.Shapes.Rectangle
                    {
                        Name = $"SegBorder_{i}",
                        Width = segW,
                        Height = h,
                        Stroke = antsBrush,
                        StrokeThickness = 1,
                        StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(2, 2),
                        StrokeDashOffset = _marchingAntsOffset,
                        IsHitTestVisible = false
                    };
                    Avalonia.Controls.Canvas.SetLeft(borderRect, x1);
                    Avalonia.Controls.Canvas.SetTop(borderRect, 0);
                    canvas.Children.Add(borderRect);
                    _selectedSegmentBorderRef = borderRect;

                    AddSegmentEdgeMarker(canvas, i, isStart: true,  markerX: x1, h: h, canvasWidth: w, durationSeconds: dur);
                    AddSegmentEdgeMarker(canvas, i, isStart: false, markerX: x2, h: h, canvasWidth: w, durationSeconds: dur);
                }
                else
                {
                    if (_selectedSegmentBorderRef != null && _selectedSegmentIndex == -1)
                        _selectedSegmentBorderRef = null;
                }
            }

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

                var tickText = new TextBlock
                {
                    Text = TimeSpan.FromSeconds(t).ToString(t >= 3600 ? "h\\:mm\\:ss" : "m\\:ss"),
                    Foreground = Avalonia.Media.Brushes.White,
                    FontSize = Infrastructure.ThemeManager.ScaledFontSize(9),
                    Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(180, 0, 0, 0)),
                    Padding = new Thickness(2, 0),
                    IsHitTestVisible = false
                };
                Avalonia.Controls.Canvas.SetLeft(tickText, tx + 2);
                Avalonia.Controls.Canvas.SetTop(tickText, 0);
                if (scaleCanvas != null) scaleCanvas.Children.Add(tickText);
                else canvas.Children.Add(tickText);
            }

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
        });
    }

    private bool _isDraggingZoomMarker;

    private void AttachZoomMarkerInteractions(Control marker, int segIndex, bool isStart, Avalonia.Controls.Canvas timelineCanvas, double durationSeconds)
    {
        marker.PointerEntered += (_, _) => MainWindow.SetTimelineCameraHover(marker, true);
        marker.PointerExited += (_, _) =>
        {
            if (!_isDraggingZoomMarker) MainWindow.SetTimelineCameraHover(marker, false);
        };
        marker.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(marker).Properties.IsLeftButtonPressed) return;
            _selectedSegmentIndex = segIndex;
            _isDraggingZoomMarker = true;
            marker.Focus();
            MainWindow.SetTimelineCameraHover(marker, true);
            e.Pointer.Capture(marker);
            e.Handled = true;
        };
        marker.PointerMoved += (_, e) =>
        {
            if (!_isDraggingZoomMarker || _selectedSegmentIndex != segIndex) return;
            double canvasX = e.GetPosition(timelineCanvas).X;
            double width = timelineCanvas.Bounds.Width;
            double clampedX = Math.Clamp(canvasX, 0, width);
            double newMs = (clampedX / width) * durationSeconds * 1000.0;
            
            var seg = _segments[segIndex];
            if (isStart)
            {
                double currentEnd = seg.ZoomEndMs ?? seg.EndMs;
                _segments[segIndex] = seg with { ZoomStartMs = Math.Min(newMs, currentEnd - 10) };
            }
            else
            {
                double currentStart = seg.ZoomStartMs ?? seg.StartMs;
                _segments[segIndex] = seg with { ZoomEndMs = Math.Max(newMs, currentStart + 10) };
            }
            
            RedrawTimeline();
            e.Handled = true;
        };
        marker.PointerReleased += (_, e) =>
        {
            if (!_isDraggingZoomMarker || _selectedSegmentIndex != segIndex) return;
            _isDraggingZoomMarker = false;
            e.Pointer.Capture(null);
            MainWindow.SetTimelineCameraHover(marker, false);
            RedrawTimeline();
            
            var seg = _segments[segIndex];
            _ = SeekInternal((isStart ? (seg.ZoomStartMs ?? seg.StartMs) : (seg.ZoomEndMs ?? seg.EndMs)) / 1000.0);
            e.Handled = true;
        };
    }

    /// <summary>
    /// Grabbable vertical edge marker for the SELECTED speed segment — visual and drag
    /// behavior copied from the Main App's MARK START/END trim markers (24px hitbox,
    /// 3px SeaGreen stick, SizeWestEast cursor, hover highlight).
    /// The press routes into the EXISTING segment drag pipeline (ResizeStart/ResizeEnd,
    /// capture to the canvas), so the 1000ms neighbour gap, 200ms minimum width, the
    /// RuntimeLog "settled" entry, status text, segment list refresh, live preview
    /// speed mapping, Main App recovery persistence on Accept, and the FFmpeg export
    /// all flow through the exact same code path as block-edge resizing.
    /// </summary>
    private void AddSegmentEdgeMarker(Avalonia.Controls.Canvas canvas, int segIndex, bool isStart, double markerX, double h, double canvasWidth, double durationSeconds)
    {
        var hitBox = new Avalonia.Controls.Border
        {
            Name = isStart ? $"SegEdgeStart_{segIndex}" : $"SegEdgeEnd_{segIndex}",
            Width = 24,
            Height = h,
            Background = Avalonia.Media.Brushes.Transparent,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast),
            ZIndex = 110
        };
        var stick = new Avalonia.Controls.Shapes.Rectangle
        {
            Fill = Avalonia.Media.Brushes.SeaGreen,
            Width = 3,
            Height = h,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };
        hitBox.Child = stick;
        ToolTip.SetTip(hitBox, isStart
            ? "Drag left/right to move this segment's START"
            : "Drag left/right to move this segment's END");

        Avalonia.Controls.Canvas.SetLeft(hitBox, markerX - 12);
        Avalonia.Controls.Canvas.SetTop(hitBox, 0);

        hitBox.PointerEntered += (_, _) => { hitBox.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(40, 46, 139, 87)); stick.Fill = Avalonia.Media.Brushes.MediumSeaGreen; };
        hitBox.PointerExited += (_, _) => { hitBox.Background = Avalonia.Media.Brushes.Transparent; stick.Fill = Avalonia.Media.Brushes.SeaGreen; };

        hitBox.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
            if (segIndex < 0 || segIndex >= _segments.Count) return;
            var seg = _segments[segIndex];

            _selectedSegmentIndex = segIndex;
            _draggingSegmentIndex = segIndex;
            _segDragMode = isStart ? SegDragMode.ResizeStart : SegDragMode.ResizeEnd;
            _dragOrigStartMs = seg.StartMs;
            _dragOrigEndMs = seg.EndMs;
            double totalMs = durationSeconds * 1000.0;
            _dragStartPointerMs = canvasWidth > 0
                ? Math.Clamp((e.GetPosition(canvas).X / canvasWidth) * totalMs, 0, totalMs)
                : 0;

            e.Pointer.Capture(canvas);
            SetStatus($"Resizing segment #{segIndex + 1} — release to set.");
            e.Handled = true;
        };

        canvas.Children.Add(hitBox);
    }

    private bool _zoomModeActive;
    private enum ZoomDrag { None, Draw, Move, ResizeTL, ResizeTR, ResizeBL, ResizeBR }
    private ZoomDrag _zoomDrag = ZoomDrag.None;
    private Avalonia.Point _zoomDragStart;
    private Avalonia.Rect _zoomStartRect;
    private Avalonia.Rect _zoomUiRect;
    private bool _hasZoomBox;
    private readonly Avalonia.Controls.Shapes.Rectangle[] _zoomDim = new Avalonia.Controls.Shapes.Rectangle[4];
    private Avalonia.Controls.Shapes.Rectangle? _zoomBoxRect;
    private readonly Avalonia.Controls.Shapes.Rectangle[] _zoomHandles = new Avalonia.Controls.Shapes.Rectangle[4];
    private Avalonia.Controls.Border? _zoomTutorial;
    private DispatcherTimer? _zoomTutorialTimer;
    private const double ZoomHandlePx = 16;
    private const double ZoomHandleVisualPx = 13.6;
    private const double ZoomFloorW = 240.0;
    private const double ZoomFloorH = 135.0;

    private double ZoomAspect => _isMobileFormat ? (2.0 / 3.0) : (16.0 / 9.0);

    private void WireZoomControls()
    {
        var zoomBtn = this.FindControl<Button>("ZoomSegmentBtn");
        if (zoomBtn != null) zoomBtn.Click += (_, __) => ToggleZoomMode();

        var canvas = this.FindControl<Avalonia.Controls.Canvas>("ZoomOverlayCanvas");
        if (canvas != null)
        {
            canvas.PointerPressed += ZoomCanvas_PointerPressed;
            canvas.PointerMoved += ZoomCanvas_PointerMoved;
            canvas.PointerReleased += ZoomCanvas_PointerReleased;
        }

        var slowCb = this.FindControl<RadioButton>("SlowZoomCheck");
        var instCb = this.FindControl<RadioButton>("InstantZoomCheck");
        if (slowCb != null) slowCb.IsCheckedChanged += (_, __) => OnZoomModeChanged(fromSlow: true);
        if (instCb != null) instCb.IsCheckedChanged += (_, __) => OnZoomModeChanged(fromSlow: false);

        var helpBtn = this.FindControl<Button>("HelpButton");
        var helpClose = this.FindControl<Button>("HelpCloseButton");
        var helpOverlay = this.FindControl<Avalonia.Controls.Grid>("HelpOverlay");
        if (helpBtn != null) helpBtn.Click += (_, __) => { if (helpOverlay != null) helpOverlay.IsVisible = true; };
        if (helpClose != null) helpClose.Click += (_, __) => { if (helpOverlay != null) helpOverlay.IsVisible = false; };
        if (helpOverlay != null)
            helpOverlay.PointerPressed += (s, e) => { if (ReferenceEquals(e.Source, helpOverlay)) helpOverlay.IsVisible = false; };

        SyncZoomModeChecksFromSegment();

        var portraitGrid = this.FindControl<Avalonia.Controls.Grid>("GranularPortraitDimmingGrid");
        if (portraitGrid != null) portraitGrid.IsVisible = _isMobileFormat;
    }

    private void UpdateDragReadout(double startMs, double endMs)
    {
        var badge = this.FindControl<Border>("DragReadoutBadge");
        var txt = this.FindControl<TextBlock>("DragReadoutText");
        if (badge == null || txt == null) return;
        double lenS = Math.Max(0, endMs - startMs) / 1000.0;
        txt.Text = $"Start {FormatMs(startMs)}    End {FormatMs(endMs)}    Length {lenS:0.00}s";
        badge.IsVisible = true;
    }

    private void HideDragReadout()
    {
        var badge = this.FindControl<Border>("DragReadoutBadge");
        if (badge != null) badge.IsVisible = false;
    }

    private bool _syncingZoomChecks;

    /// <summary>true when the user has selected the SLOW (gradual) zoom ramp; false = INSTANT.</summary>
    private bool ZoomSlowSelected => this.FindControl<RadioButton>("SlowZoomCheck")?.IsChecked == true;

    private void OnZoomModeChanged(bool fromSlow)
    {
        if (_syncingZoomChecks) return;
        var slowCb = this.FindControl<RadioButton>("SlowZoomCheck");
        var instCb = this.FindControl<RadioButton>("InstantZoomCheck");
        if (slowCb == null || instCb == null) return;

        bool slow = fromSlow ? (slowCb.IsChecked == true) : (instCb.IsChecked != true);
        _syncingZoomChecks = true;
        slowCb.IsChecked = slow;
        instCb.IsChecked = !slow;
        _syncingZoomChecks = false;

        if (_selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count)
        {
            var seg = _segments[_selectedSegmentIndex];
            if (seg.ZoomW.HasValue && seg.ZoomSlow != slow)
            {
                _segments[_selectedSegmentIndex] = seg with { ZoomSlow = slow };
                RuntimeLog.Info("Granular", $"Zoom ramp mode → {(slow ? "SLOW" : "INSTANT")} on segment #{_selectedSegmentIndex + 1}.");
                RefreshSegmentList();
                RedrawTimeline();
            }
        }
    }

    /// <summary>Reflect the ramp mode into the checkboxes: an existing zoom keeps its own mode;
    /// a segment with no zoom yet (or no selection) shows the global Settings default.</summary>
    private void SyncZoomModeChecksFromSegment()
    {
        bool slow;
        if (_selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count
            && _segments[_selectedSegmentIndex].ZoomW.HasValue)
            slow = _segments[_selectedSegmentIndex].ZoomSlow;
        else
            slow = Infrastructure.SettingsManager.Instance.Defaults.DefaultZoomSlow;
        var slowCb = this.FindControl<RadioButton>("SlowZoomCheck");
        var instCb = this.FindControl<RadioButton>("InstantZoomCheck");
        _syncingZoomChecks = true;
        if (slowCb != null) slowCb.IsChecked = slow;
        if (instCb != null) instCb.IsChecked = !slow;
        _syncingZoomChecks = false;
    }

    /// <summary>The video's actual letterboxed rect inside the overlay canvas (aspect-fit).</summary>
    private Avalonia.Rect GetVideoDisplayRect(Avalonia.Controls.Canvas canvas)
    {
        var (sw, sh) = FortniteVideoSoftware.Core.Media.CoordinateMath.GetResolutionInts(_originalResolution);
        double srcAspect = sh > 0 ? (double)sw / sh : 16.0 / 9.0;
        double cw = canvas.Bounds.Width, ch = canvas.Bounds.Height;
        if (cw <= 1 || ch <= 1) return new Avalonia.Rect(0, 0, Math.Max(1, cw), Math.Max(1, ch));
        double vidW, vidH;
        if (cw / ch > srcAspect) { vidH = ch; vidW = ch * srcAspect; }
        else { vidW = cw; vidH = cw / srcAspect; }
        return new Avalonia.Rect((cw - vidW) / 2.0, (ch - vidH) / 2.0, vidW, vidH);
    }

    private void ToggleZoomMode()
    {
        if (_selectedSegmentIndex < 0 || _selectedSegmentIndex >= _segments.Count) return;
        if (_zoomModeActive) _ = ApplyZoomWithBusyOverlayAsync();
        else EnterZoomMode();
    }

    /// <summary>
    /// APPLY ZOOM-IN commit path. On the GPU preview path, stall the UI behind a
    /// blocking "Applying changes..." overlay while the simulated crop is primed in
    /// mpv (buffering round-trip), per project_structure.txt Section 8 item (b).
    /// On CPU-only machines this is a plain ExitZoomMode (no overlay, no simulation).
    /// </summary>
    private async System.Threading.Tasks.Task ApplyZoomWithBusyOverlayAsync()
    {
        ExitZoomMode();
        if (!_gpuLiveZoomPreview) return;

        var busy = this.FindControl<Border>("ZoomApplyBusyOverlay");
        if (busy != null) busy.IsVisible = true;
        try
        {
            UpdateLiveZoomCrop();

            string want = _lastLiveCrop;
            for (int i = 0; i < 20 && want.Length > 0; i++)
            {
                var applied = _videoHost?.IpcClient?.GetPropertyString("video-crop");
                if (!string.IsNullOrEmpty(applied) && applied != "no") break;
                await System.Threading.Tasks.Task.Delay(50);
            }
            await System.Threading.Tasks.Task.Delay(150);
            RuntimeLog.Info("Granular", "APPLY ZOOM-IN: GPU live zoom preview primed.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Granular", $"GPU live zoom prime failed (falling back to box overlay): {ex.Message}");
        }
        finally
        {
            if (busy != null) busy.IsVisible = false;
        }
    }

    /// <summary>Removes any simulated live crop from the mpv preview.</summary>
    private void ClearLiveZoomCrop()
    {
        if (!_gpuLiveZoomPreview || _lastLiveCrop.Length == 0) return;
        _lastLiveCrop = "";
        _ = _videoHost?.IpcClient?.SetPropertyAsync("video-crop", "");
    }

    /// <summary>
    /// GPU live zoom preview engine. Mirrors GranularSpeedBuilder's export math 1:1:
    /// steal windows (>=0.5s available, capped at 1.0s, vs neighboring zooms only),
    /// linear ramp progress, zVal = 1 + (targetZ - 1) * p with the view center lerping
    /// from frame center to the zoom-box center. The equivalent visible source region
    /// is applied via mpv `video-crop` ("WxH+X+Y"); mpv's own aspect-fit letterboxing
    /// then matches the export's force_original_aspect_ratio=decrease + pad stage.
    /// NOTE: near frame edges the export shows black padding where this preview clamps
    /// the crop inside the frame — an accepted, minor visual difference.
    /// </summary>
    private void UpdateLiveZoomCrop()
    {
        if (!_gpuLiveZoomPreview || _videoHost?.IpcClient == null) return;
        if (_zoomModeActive) { ClearLiveZoomCrop(); return; }

        var (sw, sh) = FortniteVideoSoftware.Core.Media.CoordinateMath.GetResolutionInts(_originalResolution);
        if (sw <= 0 || sh <= 0) return;

        double tSec = Math.Max(0, (_videoHost.IpcClient.CurrentTime * 1000.0) - _trimStartMs) / 1000.0;
        double durSec = Math.Max(0.1, ((_trimEndMs > 0 ? _trimEndMs : _videoHost.IpcClient.Duration * 1000.0) - _trimStartMs) / 1000.0);

        var zooms = new List<(double zs, double ze, SpeedSegment seg)>();
        foreach (var s in _segments)
            if (s.ZoomW.HasValue && s.ZoomH.HasValue && s.ZoomX.HasValue && s.ZoomY.HasValue)
                zooms.Add(((s.ZoomStartMs ?? s.StartMs) / 1000.0, (s.ZoomEndMs ?? s.EndMs) / 1000.0, s));
        zooms.Sort((a, b) => a.zs.CompareTo(b.zs));

        double p = 0; SpeedSegment? active = null;
        for (int i = 0; i < zooms.Count; i++)
        {
            var (zs, ze, seg) = zooms[i];
            if (tSec >= zs && tSec <= ze) { p = 1.0; active = seg; break; }
            if (!seg.ZoomSlow) continue;

            double prevEnd = i > 0 ? zooms[i - 1].ze : 0.0;
            double nextStart = i < zooms.Count - 1 ? zooms[i + 1].zs : durSec;
            double availBefore = zs - prevEnd;
            double stealBefore = availBefore >= 0.5 ? Math.Min(1.0, availBefore) : 0.0;
            double availAfter = nextStart - ze;
            double stealAfter = availAfter >= 0.5 ? Math.Min(1.0, availAfter) : 0.0;

            if (stealBefore > 0 && tSec >= zs - stealBefore && tSec < zs) { p = (tSec - (zs - stealBefore)) / stealBefore; active = seg; break; }
            if (stealAfter > 0 && tSec > ze && tSec <= ze + stealAfter) { p = 1.0 - (tSec - ze) / stealAfter; active = seg; break; }
        }

        if (active == null || p <= 0.001) { ClearLiveZoomCrop(); return; }

        double targetZ = Math.Min((double)sw / active.ZoomW!.Value, (double)sh / active.ZoomH!.Value);
        double zVal = 1.0 + (targetZ - 1.0) * p;
        double cx = sw / 2.0 + ((active.ZoomX!.Value + active.ZoomW!.Value / 2.0) - sw / 2.0) * p;
        double cy = sh / 2.0 + ((active.ZoomY!.Value + active.ZoomH!.Value / 2.0) - sh / 2.0) * p;
        double visW = sw / zVal, visH = sh / zVal;
        double x = Math.Clamp(cx - visW / 2.0, 0, Math.Max(0, sw - visW));
        double y = Math.Clamp(cy - visH / 2.0, 0, Math.Max(0, sh - visH));

        int iw = Math.Max(2, (int)Math.Round(visW / 2.0) * 2);
        int ih = Math.Max(2, (int)Math.Round(visH / 2.0) * 2);
        int ix = Math.Max(0, Math.Min(sw - iw, (int)Math.Round(x)));
        int iy = Math.Max(0, Math.Min(sh - ih, (int)Math.Round(y)));

        string crop = $"{iw}x{ih}+{ix}+{iy}";
        if (crop == _lastLiveCrop) return;
        _lastLiveCrop = crop;
        _ = _videoHost.IpcClient.SetPropertyAsync("video-crop", crop);
    }

    private void EnterZoomMode()
    {
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("ZoomOverlayCanvas");
        var zoomBtn = this.FindControl<Button>("ZoomSegmentBtn");
        if (canvas == null) return;

        ClearLiveZoomCrop();

        _zoomModeActive = true;
        EnsureZoomVisuals(canvas);
        canvas.IsVisible = true;
        canvas.IsHitTestVisible = true;

        if (zoomBtn != null)
        {
            zoomBtn.Content = "APPLY ZOOM-IN";
            zoomBtn.Classes.Remove("Primary");
            if (!zoomBtn.Classes.Contains("Success")) zoomBtn.Classes.Add("Success");
        }

        var seg = _segments[_selectedSegmentIndex];
        var vid = GetVideoDisplayRect(canvas);
        var (sw, sh) = FortniteVideoSoftware.Core.Media.CoordinateMath.GetResolutionInts(_originalResolution);
        if (seg.ZoomW.HasValue && seg.ZoomH.HasValue && seg.ZoomX.HasValue && seg.ZoomY.HasValue && sw > 0 && sh > 0)
        {
            double sx = vid.Width / sw, sy = vid.Height / sh;
            _zoomUiRect = new Avalonia.Rect(vid.X + seg.ZoomX.Value * sx, vid.Y + seg.ZoomY.Value * sy,
                                            seg.ZoomW.Value * sx, seg.ZoomH.Value * sy);
            _hasZoomBox = true;
        }
        else { _hasZoomBox = false; }

        RenderZoomBox();
        MaybeShowZoomTutorial(canvas);
        SetStatus("Zoom: click-drag to draw a box; drag center to pan, corners to resize.");
    }

    private void ExitZoomMode()
    {
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("ZoomOverlayCanvas");
        var zoomBtn = this.FindControl<Button>("ZoomSegmentBtn");
        _zoomModeActive = false;
        _zoomDrag = ZoomDrag.None;
        if (canvas != null) canvas.IsVisible = false;
        HideZoomTutorial();
        if (zoomBtn != null)
        {
            zoomBtn.Content = "ZOOM-IN";
            zoomBtn.Classes.Remove("Success");
            if (!zoomBtn.Classes.Contains("Primary")) zoomBtn.Classes.Add("Primary");
        }
    }

    private void EnsureZoomVisuals(Avalonia.Controls.Canvas canvas)
    {
        if (_zoomBoxRect != null) return;
        var dimBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#80000000"));
        for (int i = 0; i < 4; i++)
        {
            _zoomDim[i] = new Avalonia.Controls.Shapes.Rectangle { Fill = dimBrush, IsHitTestVisible = false };
            canvas.Children.Add(_zoomDim[i]);
        }
        _zoomBoxRect = new Avalonia.Controls.Shapes.Rectangle
        {
            Stroke = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#fde047")),
            StrokeThickness = 2,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 },
            Fill = Avalonia.Media.Brushes.Transparent,
            IsHitTestVisible = false
        };
        canvas.Children.Add(_zoomBoxRect);
        for (int i = 0; i < 4; i++)
        {
            _zoomHandles[i] = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = ZoomHandleVisualPx, Height = ZoomHandleVisualPx,
                Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1e40af")),
                Stroke = Avalonia.Media.Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false
            };
            canvas.Children.Add(_zoomHandles[i]);
        }

        // IDEA_6: the portrait "brick wall". In mobile format the export centre-crops the frame to
        // a 2:3 slice (see CoordinateMath / the Portrait Canvas Trick) and everything outside it is
        // discarded. The rubberband was already geometrically clamped to that slice — see the
        // _isMobileFormat branch in ZoomCanvas_PointerMoved — but nothing DREW it, so the user had
        // no way to know why the box refused to go further, and could not tell which parts of the
        // picture survive. These two edge lines mark the surviving slice.
        // The two discarded side columns, shaded so the user can SEE what portrait throws away.
        // ZIndex -1 keeps them under the zoom dimmer, the box and the handles; the canvas's own
        // Background sits behind everything, so nothing here can hide the picture's centre.
        for (int i = 0; i < 2; i++)
        {
            var shade = new Avalonia.Controls.Shapes.Rectangle
            {
                Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#99000000")),
                IsHitTestVisible = false,
                IsVisible = false,
                ZIndex = -1
            };
            _portraitShade[i] = shade;
            canvas.Children.Add(shade);
        }

        for (int i = 0; i < 2; i++)
        {
            var edge = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = 2,
                Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#38bdf8")),
                IsHitTestVisible = false,
                IsVisible = false,
                ZIndex = 5
            };
            _portraitEdges[i] = edge;
            canvas.Children.Add(edge);
        }
    }

    /// <summary>IDEA_6 — left/right markers for the surviving portrait slice. Null until
    /// <see cref="EnsureZoomVisuals"/> runs; only ever visible when _isMobileFormat is true.</summary>
    private readonly Avalonia.Controls.Shapes.Rectangle?[] _portraitEdges = new Avalonia.Controls.Shapes.Rectangle?[2];

    /// <summary>IDEA_6 — shading over the two columns portrait mode discards.</summary>
    private readonly Avalonia.Controls.Shapes.Rectangle?[] _portraitShade = new Avalonia.Controls.Shapes.Rectangle?[2];

    /// <summary>
    /// IDEA_6 — positions the portrait boundary markers. Uses the SAME expression as the clamp in
    /// ZoomCanvas_PointerMoved (vid.Height * 2/3, centred) so the line the user sees is exactly the
    /// wall the drag hits. If those two ever disagree, the guide is lying — keep them together.
    /// </summary>
    private void RenderPortraitBoundary(Avalonia.Controls.Canvas canvas)
    {
        if (_portraitEdges[0] == null || _portraitEdges[1] == null
            || _portraitShade[0] == null || _portraitShade[1] == null) return;

        void HideAll()
        {
            _portraitEdges[0]!.IsVisible = false;
            _portraitEdges[1]!.IsVisible = false;
            _portraitShade[0]!.IsVisible = false;
            _portraitShade[1]!.IsVisible = false;
        }

        if (!_isMobileFormat) { HideAll(); return; }

        var vid = GetVideoDisplayRect(canvas);
        if (vid.Width <= 0 || vid.Height <= 0) { HideAll(); return; }

        double cw = canvas.Bounds.Width, ch = canvas.Bounds.Height;
        double portraitW = vid.Height * (2.0 / 3.0);
        double left = vid.X + (vid.Width - portraitW) / 2.0;
        double right = left + portraitW;

        // The shading runs from the CANVAS edge to the boundary line, and over the CANVAS height —
        // not the video rect.
        //
        // This is the fix for "the dimming stops before the extreme edges". Anchoring the shade to
        // the video rect leaves the letterbox/pillarbox margins around the picture undimmed, so the
        // far left and right of the panel stayed bright while the middle darkened correctly. Those
        // margins are just as discarded as the rest, so covering the whole canvas is both correct
        // and what the eye expects. Clamped so a canvas narrower than the video cannot produce a
        // negative width.
        void Shade(Avalonia.Controls.Shapes.Rectangle r, double x, double w)
        {
            r.IsVisible = true;
            r.Width = Math.Max(0, w);
            r.Height = Math.Max(0, ch);
            Avalonia.Controls.Canvas.SetLeft(r, Math.Max(0, x));
            Avalonia.Controls.Canvas.SetTop(r, 0);
        }

        Shade(_portraitShade[0]!, 0, left);
        Shade(_portraitShade[1]!, right, cw - right);

        // The boundary lines stay tied to the VIDEO height — they mark the edge of the surviving
        // picture, so drawing them through the letterbox bars would be misleading.
        void Place(Avalonia.Controls.Shapes.Rectangle edge, double x)
        {
            edge.IsVisible = true;
            edge.Height = vid.Height;
            Avalonia.Controls.Canvas.SetLeft(edge, x - 1);
            Avalonia.Controls.Canvas.SetTop(edge, vid.Y);
        }

        Place(_portraitEdges[0]!, left);
        Place(_portraitEdges[1]!, right);
    }

    private bool _isZoomRenderPending = false;
    private void RenderZoomBox()
    {
        if (_isZoomRenderPending) return;
        _isZoomRenderPending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _isZoomRenderPending = false;
            var canvas = this.FindControl<Avalonia.Controls.Canvas>("ZoomOverlayCanvas");
            if (canvas == null || _zoomBoxRect == null) return;
            double cw = canvas.Bounds.Width, ch = canvas.Bounds.Height;

            // IDEA_6: drawn on every render pass — including the no-box branch below — so the
            // surviving-slice guide is visible from the moment zoom mode opens, before the user
            // has drawn anything.
            RenderPortraitBoundary(canvas);

            if (!_hasZoomBox)
            {
                _zoomBoxRect.IsVisible = false;
                foreach (var h in _zoomHandles) h.IsVisible = false;
                for (int i = 0; i < 4; i++) if (_zoomDim[i] != null) { _zoomDim[i].Width = 0; _zoomDim[i].Height = 0; }
                return;
            }

            var r = _zoomUiRect;
            _zoomBoxRect.IsVisible = true;
            _zoomBoxRect.Width = r.Width; _zoomBoxRect.Height = r.Height;
            Avalonia.Controls.Canvas.SetLeft(_zoomBoxRect, r.X);
            Avalonia.Controls.Canvas.SetTop(_zoomBoxRect, r.Y);

            void Dim(int i, double x, double y, double w, double h)
            {
                var d = _zoomDim[i]; if (d == null) return;
                d.Width = Math.Max(0, w); d.Height = Math.Max(0, h);
                Avalonia.Controls.Canvas.SetLeft(d, x); Avalonia.Controls.Canvas.SetTop(d, y);
            }
            Dim(0, 0, 0, cw, r.Y);
            Dim(1, 0, r.Bottom, cw, ch - r.Bottom);
            Dim(2, 0, r.Y, r.X, r.Height);
            Dim(3, r.Right, r.Y, cw - r.Right, r.Height);

            var corners = new[] { new Avalonia.Point(r.X, r.Y), new Avalonia.Point(r.Right, r.Y),
                                  new Avalonia.Point(r.X, r.Bottom), new Avalonia.Point(r.Right, r.Bottom) };
            for (int i = 0; i < 4; i++)
            {
                _zoomHandles[i].IsVisible = true;
                Avalonia.Controls.Canvas.SetLeft(_zoomHandles[i], corners[i].X - ZoomHandleVisualPx / 2);
                Avalonia.Controls.Canvas.SetTop(_zoomHandles[i], corners[i].Y - ZoomHandleVisualPx / 2);
            }
        }, Avalonia.Threading.DispatcherPriority.Render);
    }

    private void ZoomCanvas_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!_zoomModeActive || sender is not Avalonia.Controls.Canvas canvas) return;
        if (!e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
        HideZoomTutorial();

        var p = e.GetPosition(canvas);
        _zoomDragStart = p;
        _zoomStartRect = _zoomUiRect;

        if (_hasZoomBox)
        {
            var r = _zoomUiRect;
            double hz = ZoomHandlePx;
            bool NearCorner(Avalonia.Point c) => Math.Abs(p.X - c.X) <= hz && Math.Abs(p.Y - c.Y) <= hz;
            if (NearCorner(new Avalonia.Point(r.X, r.Y))) _zoomDrag = ZoomDrag.ResizeTL;
            else if (NearCorner(new Avalonia.Point(r.Right, r.Y))) _zoomDrag = ZoomDrag.ResizeTR;
            else if (NearCorner(new Avalonia.Point(r.X, r.Bottom))) _zoomDrag = ZoomDrag.ResizeBL;
            else if (NearCorner(new Avalonia.Point(r.Right, r.Bottom))) _zoomDrag = ZoomDrag.ResizeBR;
            else if (r.Contains(p)) { _zoomDrag = ZoomDrag.Move; canvas.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand); }
            else _zoomDrag = ZoomDrag.Draw;
        }
        else _zoomDrag = ZoomDrag.Draw;

        e.Pointer.Capture(canvas);
        e.Handled = true;
    }

    /// <summary>§7B/§7C: cursor to show while hovering (not dragging) the zoom rubber-band.</summary>
    private Avalonia.Input.StandardCursorType ZoomHoverCursor(Avalonia.Point p)
    {
        if (_hasZoomBox)
        {
            var r = _zoomUiRect;
            double hz = ZoomHandlePx;
            bool Near(Avalonia.Point c) => Math.Abs(p.X - c.X) <= hz && Math.Abs(p.Y - c.Y) <= hz;
            if (Near(new Avalonia.Point(r.X, r.Y)) || Near(new Avalonia.Point(r.Right, r.Bottom)))
                return Avalonia.Input.StandardCursorType.TopLeftCorner;
            if (Near(new Avalonia.Point(r.Right, r.Y)) || Near(new Avalonia.Point(r.X, r.Bottom)))
                return Avalonia.Input.StandardCursorType.TopRightCorner;
            if (r.Contains(p)) return Avalonia.Input.StandardCursorType.Hand;
        }
        return Avalonia.Input.StandardCursorType.Cross;
    }

    private void ZoomCanvas_PointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (!_zoomModeActive || sender is not Avalonia.Controls.Canvas canvas) return;

        if (_zoomDrag == ZoomDrag.None)
        {
            canvas.Cursor = new Avalonia.Input.Cursor(ZoomHoverCursor(e.GetPosition(canvas)));
            return;
        }

        var vid = GetVideoDisplayRect(canvas);
        var bounds = vid;
        if (_isMobileFormat)
        {
            double portraitW = vid.Height * (2.0 / 3.0);
            bounds = new Avalonia.Rect(vid.X + (vid.Width - portraitW) / 2.0, vid.Y, portraitW, vid.Height);
        }

        var p = e.GetPosition(canvas);
        double aspect = ZoomAspect;

        double scale = vid.Width / Math.Max(1, GetSourceW());
        double minW = Math.Max(ZoomFloorW, ZoomFloorH * aspect) * scale;
        double minH = minW / aspect;

        if (_zoomDrag == ZoomDrag.Draw || _zoomDrag == ZoomDrag.ResizeBR || _zoomDrag == ZoomDrag.ResizeTR
            || _zoomDrag == ZoomDrag.ResizeBL || _zoomDrag == ZoomDrag.ResizeTL)
        {
            Avalonia.Point anchor = _zoomDrag switch
            {
                ZoomDrag.ResizeBR => _zoomStartRect.TopLeft,
                ZoomDrag.ResizeTR => new Avalonia.Point(_zoomStartRect.X, _zoomStartRect.Bottom),
                ZoomDrag.ResizeBL => new Avalonia.Point(_zoomStartRect.Right, _zoomStartRect.Y),
                ZoomDrag.ResizeTL => _zoomStartRect.BottomRight,
                _ => _zoomDragStart
            };
            double w = Math.Abs(p.X - anchor.X);
            double h = w / aspect;
            if (w < minW) { w = minW; h = minH; }
            double x = p.X >= anchor.X ? anchor.X : anchor.X - w;
            double y = p.Y >= anchor.Y ? anchor.Y : anchor.Y - h;
            _zoomUiRect = ClampToVideo(new Avalonia.Rect(x, y, w, h), bounds, aspect, minW, minH);
            _hasZoomBox = true;
        }
        else if (_zoomDrag == ZoomDrag.Move)
        {
            double dx = p.X - _zoomDragStart.X, dy = p.Y - _zoomDragStart.Y;
            double nx = Math.Clamp(_zoomStartRect.X + dx, bounds.X, bounds.Right - _zoomStartRect.Width);
            double ny = Math.Clamp(_zoomStartRect.Y + dy, bounds.Y, bounds.Bottom - _zoomStartRect.Height);
            _zoomUiRect = new Avalonia.Rect(nx, ny, _zoomStartRect.Width, _zoomStartRect.Height);
        }

        RenderZoomBox();
        e.Handled = true;
    }

    private void ZoomCanvas_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (sender is Avalonia.Controls.Canvas canvas) { e.Pointer.Capture(null); canvas.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Cross); }
        if (!_zoomModeActive) return;
        bool wasDraw = _zoomDrag == ZoomDrag.Draw;
        bool wasResize = _zoomDrag is ZoomDrag.ResizeTL or ZoomDrag.ResizeTR or ZoomDrag.ResizeBL or ZoomDrag.ResizeBR;
        _zoomDrag = ZoomDrag.None;
        if (!_hasZoomBox) return;
        CommitZoomToSegment(wasDraw ? "Created" : wasResize ? "Resized" : "Moved");
        e.Handled = true;
    }

    /// <summary>Clamp a candidate box into the video rect, preserving aspect and floor.</summary>
    private Avalonia.Rect ClampToVideo(Avalonia.Rect r, Avalonia.Rect vid, double aspect, double minW, double minH)
    {
        double w = Math.Min(r.Width, vid.Width);
        double h = w / aspect;
        if (h > vid.Height) { h = vid.Height; w = h * aspect; }
        if (w < minW) { w = minW; h = minH; }
        double x = Math.Clamp(r.X, vid.X, vid.Right - w);
        double y = Math.Clamp(r.Y, vid.Y, vid.Bottom - h);
        return new Avalonia.Rect(x, y, w, h);
    }

    private int GetSourceW() => FortniteVideoSoftware.Core.Media.CoordinateMath.GetResolutionInts(_originalResolution).w;

    private static int Even(int v) => v % 2 == 0 ? v : v - 1;

    private void CommitZoomToSegment(string action)
    {
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("ZoomOverlayCanvas");
        if (canvas == null || _selectedSegmentIndex < 0 || _selectedSegmentIndex >= _segments.Count) return;
        var vid = GetVideoDisplayRect(canvas);
        var (sw, sh) = FortniteVideoSoftware.Core.Media.CoordinateMath.GetResolutionInts(_originalResolution);
        if (vid.Width < 1 || vid.Height < 1 || sw <= 0 || sh <= 0) return;

        double sx = sw / vid.Width, sy = sh / vid.Height;
        int zx = Even(Math.Clamp((int)Math.Round((_zoomUiRect.X - vid.X) * sx), 0, sw - 2));
        int zy = Even(Math.Clamp((int)Math.Round((_zoomUiRect.Y - vid.Y) * sy), 0, sh - 2));
        int zw = Even(Math.Clamp((int)Math.Round(_zoomUiRect.Width * sx), 2, sw - zx));
        int zh = Even(Math.Clamp((int)Math.Round(_zoomUiRect.Height * sy), 2, sh - zy));

        var seg = _segments[_selectedSegmentIndex];
        bool slow = ZoomSlowSelected;
        _segments[_selectedSegmentIndex] = seg with
        {
            ZoomX = zx, ZoomY = zy, ZoomW = zw, ZoomH = zh, ZoomOrigRes = $"{sw}x{sh}", ZoomSlow = slow
        };

        RuntimeLog.Info("Granular", $"Zoom {action} on segment #{_selectedSegmentIndex + 1} (start {FormatMs(seg.StartMs)}): X={zx} Y={zy} W={zw} H={zh} src={sw}x{sh} mobile={_isMobileFormat} ramp={(slow ? "SLOW" : "INSTANT")}.");
        RefreshSegmentList();
        RedrawTimeline();
    }

    private const string ZoomTutorialCounterFile = "zoom_tutorial.txt";

    private static int ReadZoomTutorialCount()
        => FortniteVideoSoftware.Core.Infrastructure.UiStateStore.ReadInt(ZoomTutorialCounterFile);

    private static void WriteZoomTutorialCount(int n)
        => FortniteVideoSoftware.Core.Infrastructure.UiStateStore.WriteInt(ZoomTutorialCounterFile, n);

    private static bool _zoomTutorialShownThisSession = false;
    private void MaybeShowZoomTutorial(Avalonia.Controls.Canvas canvas)
    {
        if (_zoomTutorialShownThisSession) return;
        if (ReadZoomTutorialCount() >= 3) return;
        _zoomTutorialShownThisSession = true;
        WriteZoomTutorialCount(ReadZoomTutorialCount() + 1);

        if (_zoomTutorial == null)
        {
            _zoomTutorial = new Avalonia.Controls.Border
            {
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#B0000000")),
                BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#fde047")),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20, 12),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = "CLICK AND DRAG TO DRAW ZOOM BOX",
                    Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#fde047")),
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    FontSize = Infrastructure.ThemeManager.ScaledFontSize(16)
                }
            };
            canvas.Children.Add(_zoomTutorial);
        }
        _zoomTutorial.IsVisible = true;
        _zoomTutorial.Opacity = 1;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_zoomTutorial == null || !_zoomModeActive) return;
            _zoomTutorial.Measure(Avalonia.Size.Infinity);
            double tw = _zoomTutorial.DesiredSize.Width;
            Avalonia.Controls.Canvas.SetLeft(_zoomTutorial, Math.Max(0, (canvas.Bounds.Width - tw) / 2));
            Avalonia.Controls.Canvas.SetTop(_zoomTutorial, 40);

            _zoomTutorialTimer?.Stop();
            _zoomTutorialTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _zoomTutorialTimer.Tick += (_, __) => HideZoomTutorial();
            _zoomTutorialTimer.Start();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void HideZoomTutorial()
    {
        _zoomTutorialTimer?.Stop();
        _zoomTutorialTimer = null;
        if (_zoomTutorial != null) { _zoomTutorial.IsVisible = false; }
    }

    /// <summary>§5 Live playhead sync: show the yellow box only while the caret is inside a zoomed segment.</summary>
    private void UpdateZoomPlayheadOverlay()
    {
        if (_zoomModeActive) return;
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("ZoomOverlayCanvas");
        if (canvas == null || _videoHost?.IpcClient == null) return;

        if (_gpuLiveZoomPreview)
        {
            if (canvas.IsVisible) canvas.IsVisible = false;
            return;
        }

        double tRelMs = Math.Max(0, (_videoHost.IpcClient.CurrentTime * 1000.0) - _trimStartMs);
        SpeedSegment? active = null;
        foreach (var s in _segments)
            if (s.ZoomW.HasValue && tRelMs >= s.StartMs && tRelMs <= s.EndMs) { active = s; break; }

        if (active == null) { if (canvas.IsVisible) { canvas.IsVisible = false; } return; }

        EnsureZoomVisuals(canvas);
        var vid = GetVideoDisplayRect(canvas);
        var (sw, sh) = FortniteVideoSoftware.Core.Media.CoordinateMath.GetResolutionInts(_originalResolution);
        if (sw <= 0 || sh <= 0) return;
        double scx = vid.Width / sw, scy = vid.Height / sh;
        _zoomUiRect = new Avalonia.Rect(vid.X + active.ZoomX!.Value * scx, vid.Y + active.ZoomY!.Value * scy,
                                        active.ZoomW!.Value * scx, active.ZoomH!.Value * scy);
        _hasZoomBox = true;
        canvas.IsVisible = true;
        canvas.IsHitTestVisible = false;
        RenderZoomBox();
        foreach (var h in _zoomHandles) h.IsVisible = false;
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_videoHost?.IpcClient == null) return;

        if (_videoHost.IpcClient.VideoWidth > 0 && _videoHost.IpcClient.VideoHeight > 0)
        {
            string liveRes = $"{_videoHost.IpcClient.VideoWidth}x{_videoHost.IpcClient.VideoHeight}";
            if (liveRes != _originalResolution) _originalResolution = liveRes;
        }
        UpdateZoomPlayheadOverlay();
        UpdateLiveZoomCrop();

        double t = _videoHost.IpcClient.CurrentTime;
        double fullDur = _videoHost.IpcClient.Duration;

        double trimEndSec = (_trimEndMs > 0) ? _trimEndMs / 1000.0 : fullDur;
        if (t >= trimEndSec && !_videoHost.IpcClient.IsPaused)
        {
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
            _ = _videoHost.IpcClient.SetPropertyAsync("time-pos", trimEndSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

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

        double currentRelMs = relTime * 1000.0;
        double currentAbsMs = currentRelMs + _trimStartMs;

        double prevFreezeTickAbsMs = _prevFreezeTickAbsMs;
        _prevFreezeTickAbsMs = currentAbsMs;

        if (_freezeTimeMs >= 0 && !_isCurrentlyFrozen && !_videoHost.IpcClient.IsPaused)
        {
            if (prevFreezeTickAbsMs >= 0 && prevFreezeTickAbsMs < _freezeTimeMs && currentAbsMs >= _freezeTimeMs
                && Math.Abs(currentAbsMs - _lastFreezeTriggerAbsMs) > 500)
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
                return;
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

        var playIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon");
        var pauseIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PauseIcon");
        if (playIcon != null && pauseIcon != null)
        {
            bool isPaused = _videoHost.IpcClient.IsPaused;
            if (_isCurrentlyFrozen) isPaused = false;
            playIcon.IsVisible = isPaused;
            pauseIcon.IsVisible = !isPaused;
        }

        var elapsed = this.FindControl<TextBlock>("GranularTimeElapsed");
        if (elapsed != null) elapsed.Text = FormatSec(relTime);

        var slider = this.FindControl<Slider>("GranularTimeline");
        if (slider != null && trimDurSec > 0 && !slider.IsPointerOver)
        {
            _isSeeking = true;
            slider.Value = (relTime / trimDurSec) * 1000.0;
            _isSeeking = false;
        }

    }

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
                return seg.Speed;
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
            return Avalonia.Media.Color.FromArgb(230, 96, 165, 250);
        }
        else if (speed < baseSpd - 0.0001)
        {
            double factor = Math.Clamp((baseSpd - speed) / Math.Max(0.001, baseSpd - 0.1), 0.0, 1.0);
            byte alpha = (byte)(51 + factor * (230 - 51));
            return Avalonia.Media.Color.FromArgb(alpha, 239, 68, 68);
        }
        else
        {
            double factor = Math.Clamp((speed - baseSpd) / Math.Max(0.001, 4.1 - baseSpd), 0.0, 1.0);
            byte alpha = (byte)(51 + factor * (230 - 51));
            return Avalonia.Media.Color.FromArgb(alpha, 34, 197, 94);
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

    protected override async void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        try { _marchingAntsTimer?.Stop(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
        try { _playbackTimer?.Stop(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
        try { _freezePulseTimer?.Stop(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
        try { _zoomTutorialTimer?.Stop(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
        if (_isSafeToClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        FortniteVideoSoftware.App.WindowBoundsHelper.SaveBoundsSync(this, "GranularBounds");

        this.Hide();

        RuntimeLog.Info("Granular", "Granular Speed Editor closing. Stopping timers and saving bounds.");
        ClearLiveZoomCrop();
        _playbackTimer?.Stop();

        _isSafeToClose = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(Close);
    }

    protected override void OnClosed(EventArgs e)
    {
        FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged -= OnGlobalMasterVolumeChanged;
        RuntimeLog.Info("Granular", "Granular Speed Editor closed. Disposing resources.");

        _playbackTimer?.Stop();
        _marchingAntsTimer?.Stop();
        _freezePulseTimer?.Stop();
        _zoomTutorialTimer?.Stop();
        _videoHost?.Dispose();
        _videoHost = null;
        base.OnClosed(e);
    }

    private void OnGlobalMasterVolumeChanged(int masterVolumePercentage)
    {
        if (_videoHost?.IpcClient != null)
        {
            _ = _videoHost.IpcClient.SetPropertyAsync("volume", masterVolumePercentage.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
                this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                e.Handled = true;
            };
            titleBar.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount < 2)
                {
                    try { BeginMoveDrag(e); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
                }
            };
        }
    
}
}