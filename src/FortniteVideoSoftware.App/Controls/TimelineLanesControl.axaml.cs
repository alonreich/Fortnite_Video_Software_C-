using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
/// </summary>
public partial class TimelineLanesControl : UserControl
{
    /// <summary>Raised continuously while the user scrubs — caret drag, ruler click, lane click.</summary>
    public event Action<double>? SeekRequested;

    private double _durationSec;
    private double _positionSec;
    private bool _caretDragging;

    public TimelineLanesControl()
    {
        InitializeComponent();

        // ⚠️ REDRAW ON RESIZE. Its absence is why the ruler could stay permanently blank: the
        // first layout pass has width 0, the draw bails out, and without this nothing ever asks
        // again.
        var lanes = this.FindControl<Grid>("LanesGrid");
        if (lanes != null) lanes.SizeChanged += (_, _) => Refresh();

        WireSeek(this.FindControl<Canvas>("RulerSeekCanvas"));
        WireSeek(this.FindControl<Canvas>("LaneASeekCanvas"));
        WireSeek(this.FindControl<Canvas>("LaneBSeekCanvas"));
        WireCaretDrag();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // ⚠️ THE XAML PANELS ARE NAMED `LaneAHostPanel` / `LaneBHostPanel`, NOT `LaneAHost`.
    // Avalonia's name generator emits a member for EVERY `Name=` in the XAML, so naming them
    // `LaneAHost` collided head-on with these two properties (CS0102, "already contains a
    // definition"). Keep the XAML names and the public API names distinct — renaming either side
    // back will break the build again.

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
            UpdateCaret();
            UpdateClocks();
        }
    }

    /// <summary>Redraws ruler, gridlines, caret and clocks. Safe to call at any time.</summary>
    public void Refresh()
    {
        DrawRuler();
        UpdateCaret();
        UpdateClocks();
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // TIME FORMAT — one rule for the whole suite.
    // ─────────────────────────────────────────────────────────────────────────────────────────

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
        // LEFT counts UP (time into the clip), RIGHT counts DOWN (time left) — confirmed with the
        // owner, and the same in both windows.
        if (elapsed != null) elapsed.Text = FormatClock(_positionSec);
        if (remaining != null) remaining.Text = FormatClock(Math.Max(0, _durationSec - _positionSec));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // RULER + GRIDLINES
    // ─────────────────────────────────────────────────────────────────────────────────────────

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

            // Full-height tick, not the old 4px grey hairline.
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
            // Nudge the first label right and the last one left so neither is clipped at the edge.
            double lx = x + 3;
            if (t <= 0.0001) lx = 2;
            else if (t >= _durationSec - interval * 0.5) lx = Math.Max(2, x - 34);
            Canvas.SetLeft(label, lx);
            Canvas.SetTop(label, 4);
            ruler.Children.Add(label);

            // Gridline dropping through BOTH lanes.
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

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // CARET
    // ─────────────────────────────────────────────────────────────────────────────────────────

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
            badge.Margin = new Thickness(Math.Clamp(x + 8, 0, Math.Max(0, w - 60)), 2, 0, 0);
        }
    }

    private void WireCaretDrag()
    {
        var grab = this.FindControl<Border>("CaretGrab");
        var line = this.FindControl<Border>("CaretLine");
        var host = this.FindControl<Panel>("CaretHost");
        if (grab == null || line == null || host == null) return;

        // The grab column is hit-testable even though its parent Panel is not — that is deliberate,
        // so the caret can be grabbed while the rest of the overlay stays transparent to clicks.
        host.IsHitTestVisible = false;
        grab.IsHitTestVisible = true;

        void SetHighlight(bool on)
        {
            // Slight grow + glow, shown on hover AND held for the whole drag until release.
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
            // Handled so the seek surfaces underneath never see it — grabbing the playhead must
            // not also register as a click-to-seek.
            e.Handled = true;
        };

        grab.PointerMoved += (_, e) =>
        {
            if (!_caretDragging) return;
            double w = host.Bounds.Width;
            if (w <= 0 || _durationSec <= 0) return;
            double frac = Math.Clamp(e.GetPosition(host).X / w, 0, 1);
            _positionSec = frac * _durationSec;
            UpdateCaret();
            UpdateClocks();
            SeekRequested?.Invoke(_positionSec);   // live scrub
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

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // CLICK / DRAG TO SEEK
    // ─────────────────────────────────────────────────────────────────────────────────────────

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
            UpdateCaret();
            UpdateClocks();
            SeekRequested?.Invoke(_positionSec);
        }

        surface.PointerPressed += (_, e) => { dragging = true; e.Pointer.Capture(surface); Apply(e); e.Handled = true; };
        surface.PointerMoved += (_, e) => { if (dragging) { Apply(e); e.Handled = true; } };
        surface.PointerReleased += (_, e) => { if (!dragging) return; dragging = false; e.Pointer.Capture(null); e.Handled = true; };
    }
}
