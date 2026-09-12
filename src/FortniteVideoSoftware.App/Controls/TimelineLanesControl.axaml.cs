using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using System;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>
/// LANES_03 — the shared two-lane timeline used by the Granular Speed Editor AND Music Wizard
/// phase 3.
///
/// ── WHAT IT OWNS ─────────────────────────────────────────────────────────────────────────────
/// The ruler, the gridlines, the caret (including drag-to-scrub), both clocks, and every
/// click-to-seek surface. The host window supplies ONLY its own lane content via
/// <see cref="LaneAHost"/> / <see cref="LaneBHost"/> and tells this control the duration and
/// position. Fix the caret once and both windows get it.
///
/// ── WHY THE HOSTS ARE PANELS, NOT TEMPLATED CONTENT ──────────────────────────────────────────
/// Both windows already build their lanes in code-behind (a drawing Canvas in Granular, Images in
/// phase 3). Exposing two plain Panels to add children to is far more predictable under NativeAOT
/// than templated ContentPresenters, and needs no data binding at all.
///
/// ── THE BUG THIS ALSO FIXES ──────────────────────────────────────────────────────────────────
/// Neither window's ruler reliably appeared. Three causes were possible and all three are closed
/// here: the ruler now has an EXPLICIT height (a Canvas reports zero desired size, so an Auto row
/// resting on MinHeight is fragile), it REDRAWS ON SIZE CHANGE (there was no SizeChanged handler
/// on either ruler canvas, so a first paint at width 0 left it permanently blank), and the ticks
/// are full-height and high-contrast rather than 4px grey hairlines.
///
/// ── ZOOM_01 — TIMELINE ZOOM (horizontal affine scaling) ─────────────────────────────────────
/// <see cref="ZoomFactor"/> (1.0–<see cref="MaxZoomFactor"/>) horizontally scales the whole
/// presentation layer by sizing <c>LanesGrid</c> to viewport-width × factor inside a horizontal
/// <see cref="ScrollViewer"/>. Ctrl+mouse-wheel zooms anchored at the cursor, the plain wheel
/// pans while zoomed, and the viewport auto-follows the playhead when it leaves view. Every
/// fraction-of-width computation in this control AND in the host windows becomes zoom-correct
/// with no other change, and no time model ever sees a zoom factor.
/// </summary>
public partial class TimelineLanesControl : UserControl
{
    /// <summary>Raised continuously while the user scrubs — caret drag, ruler click, lane click.</summary>
    public event Action<double>? SeekRequested;

    /// <summary>ZOOM_01 — raised whenever the timeline zoom changes (UI thread).</summary>
    public event Action<double>? ZoomChanged;

    /// <summary>ZOOM_01 — the zoom ceiling (directive: default 1.0, max 10.0).</summary>
    public const double MaxZoomFactor = 10.0;

    /// <summary>ZOOM_01 — multiplicative zoom step per Ctrl+wheel notch (1.25^10 ≈ 9.3).</summary>
    private const double ZoomWheelStep = 1.25;

    /// <summary>ZOOM_01 — pixels panned per plain-wheel notch while zoomed in.</summary>
    private const double WheelPanPx = 120.0;

    /// <summary>ZOOM_01 — dead band either side of the playhead before the viewport follows it.</summary>
    private const double CaretFollowMarginPx = 48.0;

    private double _durationSec;
    private double _positionSec;
    private bool _caretDragging;
    private double _zoomFactor = 1.0;

    /// <summary>
    /// ZOOM_01 — declared height of the row above the ruler that the floating camera markers paint
    /// into. 0 by default (Music Wizard phase 3 never floats markers); the Granular Speed Editor
    /// sets 56 to cover <c>FreezeMarkerOverlayTop = -52</c> plus its glow, and cancels the growth
    /// with a matching negative top margin so the on-screen ruler position does not move.
    /// </summary>
    public static readonly StyledProperty<double> MarkerHeadroomPxProperty =
        AvaloniaProperty.Register<TimelineLanesControl, double>(nameof(MarkerHeadroomPx));

    public double MarkerHeadroomPx
    {
        get => GetValue(MarkerHeadroomPxProperty);
        set => SetValue(MarkerHeadroomPxProperty, value);
    }

    /// <summary>
    /// ZOOM_01 — opt-in switch for the Ctrl+wheel zoom / wheel pan gestures. OFF by default so the
    /// Music Wizard phase 3 instance of this control behaves exactly as before; the Granular Speed
    /// Editor turns it on in <c>BuildLaneContent</c>.
    /// </summary>
    public bool ZoomGesturesEnabled { get; set; }

    public TimelineLanesControl()
    {
        InitializeComponent();

        var lanes = this.FindControl<Grid>("LanesGrid");
        if (lanes != null) lanes.SizeChanged += (_, _) => QueueRefresh();

        var scroll = this.FindControl<ScrollViewer>("LanesScroll");
        if (scroll != null)
        {
            // ZOOMSIZE_02 — SizeChanged ONLY. ScrollChanged must never resize the content:
            // scroll POSITION has no bearing on content WIDTH, and reacting to it re-armed the
            // resize loop on every pan.
            scroll.SizeChanged += (_, _) => ApplyZoomSizing();
        }

        // TUNNEL, not bubbling: the ScrollViewer consumes bubbling wheel events before the root
        // would see them. Tunneling lets the Ctrl+wheel zoom win the race deterministically.
        this.AddHandler(InputElement.PointerWheelChangedEvent, HandleTimelineWheel,
            RoutingStrategies.Tunnel);

        ApplyMarkerHeadroom();

        WireSeek(this.FindControl<Canvas>("RulerSeekCanvas"));
        WireSeek(this.FindControl<Canvas>("LaneASeekCanvas"));
        WireSeek(this.FindControl<Canvas>("LaneBSeekCanvas"));
        WireCaretDrag();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkerHeadroomPxProperty) ApplyMarkerHeadroom();
    }

    private void ApplyMarkerHeadroom()
    {
        var lanes = this.FindControl<Grid>("LanesGrid");
        if (lanes != null && lanes.RowDefinitions.Count > 0)
            lanes.RowDefinitions[0].Height = new GridLength(Math.Max(0, MarkerHeadroomPx));
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);


    /// <summary>Upper 60px lane. The host window adds its own content here.</summary>
    public Panel? LaneAHost => this.FindControl<Panel>("LaneAHostPanel");

    /// <summary>Lower 60px lane. The host window adds its own content here.</summary>
    public Panel? LaneBHost => this.FindControl<Panel>("LaneBHostPanel");

    /// <summary>
    /// MARKER_01 — the floating marker layer: above both seek surfaces, below the caret.
    ///
    /// <para>
    /// For markers that belong ON TOP OF the timeline rather than inside a lane — a camera head
    /// floating above the ruler with its stick dropping down through the lanes. Its width matches
    /// the lanes exactly, so an X computed against a lane canvas transfers here unchanged; its
    /// origin is the TOP OF THE RULER, so a marker that should float above the timeline is placed
    /// at a NEGATIVE Canvas.Top.
    /// </para>
    /// <para>
    /// ⚠️ Put ONLY markers here, and never give this canvas a Background. It is non-hit-testable
    /// where it is empty precisely so ordinary clicks fall through to the seek surfaces beneath;
    /// a background would make the entire timeline unclickable in one stroke.
    /// </para>
    /// </summary>
    public Canvas? MarkerOverlayHost => this.FindControl<Canvas>("MarkerOverlayCanvas");

    /// <summary>
    /// Whether clicking the UPPER lane seeks. OFF for Granular, whose upper lane runs its own
    /// pointer pipeline (block move, edge resize, drag-to-create) and must not be shadowed.
    /// </summary>
    public bool LaneASeekable
    {
        get => this.FindControl<Canvas>("LaneASeekCanvas")?.IsVisible ?? false;
        set { var c = this.FindControl<Canvas>("LaneASeekCanvas"); if (c != null) c.IsVisible = value; }
    }

    /// <summary>Whether clicking the LOWER lane seeks. ON in both windows today.</summary>
    public bool LaneBSeekable
    {
        get => this.FindControl<Canvas>("LaneBSeekCanvas")?.IsVisible ?? false;
        set { var c = this.FindControl<Canvas>("LaneBSeekCanvas"); if (c != null) c.IsVisible = value; }
    }

    /// <summary>Clip length in seconds. Setting it redraws the ruler and both clocks.</summary>
    public double DurationSeconds
    {
        get => _durationSec;
        set { _durationSec = Math.Max(0, value); Refresh(); }
    }

    /// <summary>
    /// Playhead position in seconds from the start of the trimmed clip.
    /// Ignored while the user is dragging the caret — otherwise playback would fight the pointer.
    /// </summary>
    public double PositionSeconds
    {
        get => _positionSec;
        set
        {
            if (_caretDragging) return;
            _positionSec = Math.Clamp(value, 0, Math.Max(0, _durationSec));
            EnsureCaretVisible();   // ZOOM_01 — follow the playhead when it leaves the viewport
            UpdateCaret();
            UpdateClocks();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // LAYOUTLOOP_01 — REFRESH MUST NEVER RUN INSIDE A LAYOUT PASS. THIS IS NOT A PERF TWEAK.
    //
    // `LanesGrid.SizeChanged` used to call Refresh() DIRECTLY. SizeChanged is raised from inside
    // Avalonia's arrange pass, and Refresh -> DrawRuler does `ruler.Children.Clear()` and then adds
    // a Rectangle per tick — i.e. it MUTATES THE VISUAL TREE WHILE THE TREE IS BEING ARRANGED.
    // That invalidates layout, which re-enters arrange, which raises SizeChanged again.
    //
    // Captured from a frozen process (dotnet-dump, 2026-09-12). UI thread, reading upward:
    //     LayoutManager.ExecuteArrangePass
    //       -> LayoutManager.Arrange  x6 nested
    //         -> Layoutable.ArrangeCore
    //           -> TimelineLanesControl.<.ctor>b__22_0(SizeChangedEventArgs)
    //             -> Refresh -> DrawRuler
    //               -> AvaloniaList.Clear -> Panel.ChildrenChanged -> SetVisualParent
    //                 -> Visual.OnDetachedFromVisualTreeCore
    //                   -> Trace.WriteLine -> OutputDebugString   (BLOCKING NATIVE CALL)
    //
    // Every detached child costs one OutputDebugString, which serialises on a global OS mutex and
    // is brutally slow while a debugger or dotnet watch is attached. Hundreds of ticks per redraw,
    // redrawing on every arrange, never converging: the window stops repainting and the app reads
    // as hard-frozen. It also explains the "timeline keeps expanding" — the ruler is being rebuilt
    // faster than the arrange pass can settle, so it never reaches a stable width.
    //
    // The fix is to leave the layout pass first: coalesce to ONE refresh and run it at Background
    // priority, after arrange has completed. _inRefresh additionally makes re-entry impossible even
    // if a future caller invokes Refresh() from inside a layout callback again.
    // ══════════════════════════════════════════════════════════════════════════════════════════
    private bool _refreshQueued;
    private bool _inRefresh;

    /// <summary>
    /// LAYOUTLOOP_01 — coalesced, deferred redraw. Safe to call from a layout/size callback.
    /// </summary>
    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            Refresh();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Redraws ruler, gridlines, caret and clocks. Safe to call at any time.</summary>
    public void Refresh()
    {
        // LAYOUTLOOP_01 — a redraw that re-enters itself would rebuild the ruler from inside its
        // own child-collection mutation. One redraw at a time, always.
        if (_inRefresh) return;
        _inRefresh = true;
        try
        {
            DrawRuler();
            UpdateCaret();
            UpdateClocks();
        }
        finally
        {
            _inRefresh = false;
        }
    }

    /// <summary>
    /// ZOOM_01 — the horizontal timeline zoom. 1.0 is the legacy 1:1 mapping; up to
    /// <see cref="MaxZoomFactor"/> the lanes content is laid out at viewport × factor inside the
    /// horizontal <c>LanesScroll</c> ScrollViewer, which supplies panning.
    /// </summary>
    public double ZoomFactor
    {
        get => _zoomFactor;
        set
        {
            double z = Math.Clamp(value, 1.0, MaxZoomFactor);
            _lastViewportW = 0;   // ZOOMSIZE_02 — the zoom changed, so re-size even at the same viewport
            if (z <= 1.0001) z = 1.0;
            if (Math.Abs(z - _zoomFactor) < 0.0001) return;

            _zoomFactor = z;
            ApplyZoomSizing();
            Refresh();
            if (z <= 1.0001 && this.FindControl<ScrollViewer>("LanesScroll") is { } s)
                s.Offset = new Vector(0, 0);
            ZoomChanged?.Invoke(z);
        }
    }

    /// <summary>
    /// ZOOM_01 — THE zoom multiplication. Sizes <c>LanesGrid</c> to viewport-width × ZoomFactor so
    /// the ScrollViewer gets its pan extent and every stretched layer (ruler, gridlines, both
    /// lanes, seek surfaces, marker overlay, caret host, and the host window's own lane content)
    /// reports the zoomed width through <c>Bounds.Width</c>. All fraction-based pixel math — this
    /// control's and the host windows' <c>SrcMsToX</c>/<c>XToSrcMs</c> pairs — therefore carries
    /// the factor exactly once, with no per-layer transform to drift out of agreement.
    /// </summary>
    private bool _inZoomSizing;

    /// <summary>ZOOMSIZE_02 — the width the content was last sized from; see ApplyZoomSizing.</summary>
    private double _lastViewportW;

    private void ApplyZoomSizing()
    {
        // LAYOUTLOOP_01 — ScrollChanged fires when this method changes lanes.Width, which would
        // call it again. The 0.5px guard below usually breaks that, but only AFTER a full layout
        // pass has run; the flag stops the re-entry outright.
        if (_inZoomSizing) return;

        var scroll = this.FindControl<ScrollViewer>("LanesScroll");
        var lanes = this.FindControl<Grid>("LanesGrid");
        if (scroll == null || lanes == null) return;

        // ══════════════════════════════════════════════════════════════════════════════════════
        // ZOOMSIZE_02 — MEASURE AGAINST Viewport. MEASURING AGAINST Bounds IS A RUNAWAY.
        //
        // LanesScroll lives in `ColumnDefinitions="Auto,*,Auto"`, column 1 — a STAR column, whose
        // width follows its content when the container is not itself width-constrained. So:
        //     contentW = Bounds.Width * zoom  ->  LanesGrid.Width = contentW
        //         ->  the star column grows  ->  Bounds.Width grows  ->  contentW grows ...
        // The ruler labels thin out to nothing, the film strip stretches without bound and the
        // window itself is dragged wider. (An earlier attempt measured from Bounds to stop a scrollbar
        // oscillation; it swapped a bounded wobble for an unbounded one. Reverted.)
        //
        // Viewport.Width cannot run away: it is what is actually VISIBLE, so it is capped by the
        // window no matter how wide the content becomes. The scrollbar wobble that attempt was
        // aimed at is handled instead by the two guards that survive from it, which are enough:
        // ScrollChanged no longer resizes anything, and an unchanged measurement is a no-op.
        // ══════════════════════════════════════════════════════════════════════════════════════
        double vp = scroll.Viewport.Width > 0 ? scroll.Viewport.Width : scroll.Bounds.Width;
        if (vp <= 0) return;

        // ZOOMSIZE_02 — if the width we measure from has not moved, there is nothing to react to.
        if (Math.Abs(vp - _lastViewportW) < 0.5 && _lastViewportW > 0) return;
        _lastViewportW = vp;

        // ZOOMSIZE_02 — a hard ceiling, independent of every calculation above. Nothing in this
        // control has any use for a lane wider than the viewport times the maximum zoom, and no
        // sequence of layout passes may ever produce one.
        double contentW = Math.Min(vp * _zoomFactor, vp * MaxZoomFactor);
        if (lanes.MaxWidth != contentW) lanes.MaxWidth = contentW;

        if (Math.Abs(lanes.Width - contentW) > 0.5)
        {
            _inZoomSizing = true;
            try { lanes.Width = contentW; }
            finally { _inZoomSizing = false; }
        }
    }

    /// <summary>
    /// ZOOM_01 — Ctrl+wheel zooms anchored at the cursor (the content fraction under the pointer
    /// stays under the pointer); the plain wheel pans horizontally while zoomed in. Registered as a
    /// TUNNEL handler so the ScrollViewer cannot swallow the gesture first.
    /// </summary>
    private void HandleTimelineWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!ZoomGesturesEnabled) return;
        var scroll = this.FindControl<ScrollViewer>("LanesScroll");
        if (scroll == null) return;
        double vp = scroll.Viewport.Width;
        if (vp <= 0) return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;

            double oldZoom = _zoomFactor;
            double newZoom = Math.Clamp(
                e.Delta.Y > 0 ? oldZoom * ZoomWheelStep : oldZoom / ZoomWheelStep,
                1.0, MaxZoomFactor);
            if (newZoom <= 1.0001) newZoom = 1.0;
            if (Math.Abs(newZoom - oldZoom) < 0.0001) return;

            double oldContentW = Math.Max(1, vp * oldZoom);
            double ptrVp = Math.Clamp(e.GetPosition(scroll).X, 0, vp);
            double frac = Math.Clamp((scroll.Offset.X + ptrVp) / oldContentW, 0, 1);

            _zoomFactor = newZoom;
            ApplyZoomSizing();

            double newContentW = vp * newZoom;
            scroll.Offset = newZoom <= 1.0001
                ? new Vector(0, 0)
                : new Vector(Math.Clamp(frac * newContentW - ptrVp, 0, Math.Max(0, newContentW - vp)), 0);

            Refresh();
            ZoomChanged?.Invoke(newZoom);
            return;
        }

        if (_zoomFactor > 1.0001 && Math.Abs(e.Delta.Y) > 0.001)
        {
            double maxOff = Math.Max(0, scroll.Extent.Width - vp);
            if (maxOff <= 0) return;
            e.Handled = true;
            // Notch conventions differ across devices (±1 per detent on most, ±120 on raw Win32
            // feeds, sub-1.0 on trackpads) — clamp to one WheelPanPx per event so every device
            // pans the same speed.
            double step = Math.Clamp(e.Delta.Y * WheelPanPx, -WheelPanPx, WheelPanPx);
            scroll.Offset = new Vector(
                Math.Clamp(scroll.Offset.X - step, 0, maxOff), 0);
        }
    }

    /// <summary>
    /// ZOOM_01 — pans the viewport so the playhead stays in view while scrubbing, dragging the
    /// caret or playing. At zoom 1 the extent equals the viewport and this is a guaranteed no-op,
    /// so non-zoomed hosts behave exactly as before.
    /// </summary>
    private void EnsureCaretVisible()
    {
        var scroll = this.FindControl<ScrollViewer>("LanesScroll");
        if (scroll == null) return;
        double vp = scroll.Viewport.Width;
        if (vp <= 0 || _durationSec <= 0) return;

        double contentW = Math.Max(1, vp * _zoomFactor);
        double x = Math.Clamp(_positionSec / _durationSec, 0, 1) * contentW;
        double off = scroll.Offset.X;

        double target = off;
        if (x < off + CaretFollowMarginPx) target = x - CaretFollowMarginPx;
        else if (x > off + vp - CaretFollowMarginPx) target = x - vp + CaretFollowMarginPx;

        double maxOff = Math.Max(0, scroll.Extent.Width - vp);
        target = Math.Clamp(target, 0, maxOff);
        if (Math.Abs(target - off) > 0.5) scroll.Offset = new Vector(target, 0);
    }


    /// <summary>
    /// MM:SS, escalating to HH:MM:SS only at one hour or more. Never milliseconds.
    /// Phase 3 previously used `m\:ss`, which printed "1:05" instead of "01:05" and could never
    /// show hours at all — so a long clip's ruler was simply wrong.
    /// </summary>
    public static string FormatClock(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds < 0 ? 0 : seconds);
        return ts.TotalHours >= 1.0
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private void UpdateClocks()
    {
        var elapsed = this.FindControl<TextBlock>("ElapsedLabel");
        var remaining = this.FindControl<TextBlock>("RemainingLabel");
        if (elapsed != null) elapsed.Text = FormatClock(_positionSec);
        if (remaining != null) remaining.Text = FormatClock(Math.Max(0, _durationSec - _positionSec));
    }


    private void DrawRuler()
    {
        var ruler = this.FindControl<Canvas>("RulerCanvas");
        var grid = this.FindControl<Canvas>("GridlinesCanvas");
        if (ruler == null) return;

        ruler.Children.Clear();
        grid?.Children.Clear();

        double w = ruler.Bounds.Width;
        double h = ruler.Bounds.Height > 0 ? ruler.Bounds.Height : 22;
        if (w <= 0 || _durationSec <= 0) return;

        double interval = ChooseInterval(_durationSec, w);
        var tickBrush = new SolidColorBrush(Color.FromArgb(200, 190, 200, 215));
        var gridBrush = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255));

        for (double t = 0; t <= _durationSec + 0.0001; t += interval)
        {
            double x = (t / _durationSec) * w;

            ruler.Children.Add(new Avalonia.Controls.Shapes.Rectangle
            {
                Width = 1,
                Height = h,
                Fill = tickBrush,
                IsHitTestVisible = false,
                [Canvas.LeftProperty] = x,
                [Canvas.TopProperty] = 0.0
            });

            var label = new TextBlock
            {
                Text = FormatClock(t),
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(9),
                Foreground = new SolidColorBrush(Color.FromArgb(230, 226, 232, 240)),
                IsHitTestVisible = false
            };
            double lx = x + 3;
            if (t <= 0.0001) lx = 2;
            else if (t >= _durationSec - interval * 0.5) lx = Math.Max(2, x - 34);
            Canvas.SetLeft(label, lx);
            Canvas.SetTop(label, 4);
            ruler.Children.Add(label);

            if (grid != null)
            {
                grid.Children.Add(new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = 1,
                    Height = Math.Max(0, grid.Bounds.Height),
                    Fill = gridBrush,
                    IsHitTestVisible = false,
                    [Canvas.LeftProperty] = x,
                    [Canvas.TopProperty] = 0.0
                });
            }
        }
    }

    /// <summary>
    /// Picks a tick spacing that keeps labels readable at the CURRENT width rather than at an
    /// assumed one — a 40s clip in a narrow window needs coarser ticks than the same clip
    /// maximised, and the old fixed 10/30/60 ladder ignored width entirely.
    /// </summary>
    private static double ChooseInterval(double durationSec, double widthPx)
    {
        double[] steps = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };
        double minLabelPx = 62;
        foreach (double s in steps)
        {
            if ((s / durationSec) * widthPx >= minLabelPx) return s;
        }
        return steps[^1];
    }


    private void UpdateCaret()
    {
        var host = this.FindControl<Panel>("CaretHost");
        var grab = this.FindControl<Border>("CaretGrab");
        var badge = this.FindControl<Border>("CaretBadge");
        var badgeText = this.FindControl<TextBlock>("CaretBadgeText");
        if (host == null || grab == null) return;

        double w = host.Bounds.Width;
        if (w <= 0 || _durationSec <= 0) { grab.IsVisible = false; return; }

        double x = Math.Clamp(_positionSec / _durationSec, 0, 1) * w;
        grab.IsVisible = true;
        grab.Margin = new Thickness(Math.Max(0, x - grab.Width / 2), 0, 0, 0);

        if (badge != null && badgeText != null)
        {
            badge.IsVisible = _caretDragging;
            badgeText.Text = FormatClock(_positionSec);

            // ZOOM_01 — clamp the badge to the VISIBLE right edge, not the content width, or at
            // high zoom it lands thousands of pixels past the viewport and never reappears. With
            // no scrolling in play this is exactly the old `w - 60` clamp.
            var scroll = this.FindControl<ScrollViewer>("LanesScroll");
            double visibleRight = w;
            if (scroll != null && scroll.Viewport.Width > 0)
                visibleRight = Math.Min(w, scroll.Offset.X + scroll.Viewport.Width);
            badge.Margin = new Thickness(Math.Clamp(x + 8, 0, Math.Max(0, visibleRight - 60)), 2, 0, 0);
        }
    }

    private void WireCaretDrag()
    {
        var grab = this.FindControl<Border>("CaretGrab");
        var line = this.FindControl<Border>("CaretLine");
        var host = this.FindControl<Panel>("CaretHost");
        if (grab == null || line == null || host == null) return;

        host.IsHitTestVisible = false;
        grab.IsHitTestVisible = true;

        void SetHighlight(bool on)
        {
            line.Width = on ? 5 : 3;
            line.Effect = on
                ? new Avalonia.Media.DropShadowEffect
                {
                    BlurRadius = 8,
                    OffsetX = 0,
                    OffsetY = 0,
                    Color = Colors.Red,
                    Opacity = 0.9
                }
                : null;
        }

        grab.PointerEntered += (_, _) => { if (!_caretDragging) SetHighlight(true); };
        grab.PointerExited += (_, _) => { if (!_caretDragging) SetHighlight(false); };

        grab.PointerPressed += (_, e) =>
        {
            _caretDragging = true;
            SetHighlight(true);
            e.Pointer.Capture(grab);
            e.Handled = true;
        };

        grab.PointerMoved += (_, e) =>
        {
            if (!_caretDragging) return;
            double w = host.Bounds.Width;
            if (w <= 0 || _durationSec <= 0) return;
            double frac = Math.Clamp(e.GetPosition(host).X / w, 0, 1);
            _positionSec = frac * _durationSec;
            EnsureCaretVisible();   // ZOOM_01 — edge-follow while dragging the caret
            UpdateCaret();
            UpdateClocks();
            SeekRequested?.Invoke(_positionSec);
            e.Handled = true;
        };

        grab.PointerReleased += (_, e) =>
        {
            if (!_caretDragging) return;
            _caretDragging = false;
            SetHighlight(grab.IsPointerOver);
            e.Pointer.Capture(null);
            UpdateCaret();
            e.Handled = true;
        };
    }


    private void WireSeek(Canvas? surface)
    {
        if (surface == null) return;
        bool dragging = false;

        void Apply(PointerEventArgs e)
        {
            double w = surface.Bounds.Width;
            if (w <= 0 || _durationSec <= 0) return;
            double frac = Math.Clamp(e.GetPosition(surface).X / w, 0, 1);
            _positionSec = frac * _durationSec;
            EnsureCaretVisible();   // ZOOM_01 — a seek surface drag can reach past the viewport edge
            UpdateCaret();
            UpdateClocks();
            SeekRequested?.Invoke(_positionSec);
        }

        surface.PointerPressed += (_, e) => { dragging = true; e.Pointer.Capture(surface); Apply(e); e.Handled = true; };
        surface.PointerMoved += (_, e) => { if (dragging) { Apply(e); e.Handled = true; } };
        surface.PointerReleased += (_, e) => { if (!dragging) return; dragging = false; e.Pointer.Capture(null); e.Handled = true; };
    }
}
