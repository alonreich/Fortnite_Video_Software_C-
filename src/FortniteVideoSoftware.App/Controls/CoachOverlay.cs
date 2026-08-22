using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FortniteVideoSoftware.Core.Infrastructure;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using ShapePath = Avalonia.Controls.Shapes.Path;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>The gesture the ghost cursor performs over a step's target control.</summary>
public enum CoachGesture
{
    /// <summary>Cursor hovers and gently bobs. For "this is where X lives".</summary>
    Point,
    /// <summary>Cursor sits still while a ring pulses out of it. For "press this".</summary>
    Click,
    /// <summary>Cursor sweeps left to right across the target. For sliders, timelines, scrubbers.</summary>
    DragHorizontal,
    /// <summary>Cursor drags diagonally while a dashed rectangle grows behind it. For the zoom box.</summary>
    DrawBox,
    /// <summary>Cursor drops in from above onto the target. For drag-and-drop targets.</summary>
    DropIn
}

/// <summary>One step of a screen's walkthrough.</summary>
/// <param name="Title">Short headline, six words or fewer.</param>
/// <param name="Body">Plain English. Assume the reader has never edited video before.</param>
/// <param name="TargetName">
/// x:Name of the control to spotlight, or null for a full-screen step. A name that does not resolve
/// is handled gracefully at run time rather than throwing — a walkthrough must never be able to take
/// the application down.
/// </param>
/// <param name="Gesture">Which animation the ghost cursor performs.</param>
public sealed record CoachStep(string Title, string Body, string? TargetName = null, CoachGesture Gesture = CoachGesture.Point);

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// ISSUE_04 — THE SUITE'S ONE ONBOARDING WALKTHROUGH.
///
/// WHY THIS EXISTS. Before it, the entire three-app suite had exactly ONE help surface: a static
/// card in the Granular Speed Editor behind a small "?" button. Every other screen — the Main App,
/// the Video Merger, the Crop Tools, the Music Wizard, the Voice Over recorder — handed a brand new
/// user a dense wall of buttons and nothing but hover tooltips. The repository contains zero
/// animated assets, and mandate #2 forbids shipping loose files beside the .exe anyway, so a folder
/// of tutorial GIFs was never an option. This DRAWS the walkthrough instead: vector shapes, one
/// DispatcherTimer, and the host window's own live controls as the stage. Nothing extra to ship,
/// nothing to re-record when a button moves, and it follows the real layout at any font scale,
/// theme or window size.
///
/// HOW IT BEHAVES.
///   * Auto-shows the first `maxAutoShows` times a screen is opened (default 3), counted per screen
///     in UiStateStore so all three processes agree.
///   * The counter is spent only when a step is ACTUALLY PAINTED — see _shownAtLeastOneStep. The old
///     zoom banner incremented its counter before it laid itself out, so a user could burn all three
///     showings without ever seeing anything. Do not move that increment earlier.
///   * Replayable forever from each screen's "?" button. It is never a one-time thing.
///   * SKIP / Escape means "stop interrupting me" and burns the remaining automatic showings.
///
/// HARD RULES.
///   (C1) NEVER let this throw into a caller. Every public entry point and the timer tick are
///        wrapped. A walkthrough that crashes the editor is far worse than no walkthrough.
///   (C2) The overlay is fully hit-testable on purpose. It is a guided tour, not a tooltip: a stray
///        click must not reach the live controls underneath and change the user's project.
///   (C3) The spotlight is FOUR dim rectangles around the target, not one geometry with a hole cut
///        in it. Four rectangles cannot produce a degenerate geometry when a target is clipped,
///        off-screen or zero-sized — which is exactly what happens mid-resize.
///   (C4) Target rectangles are recomputed on EVERY tick. These windows are resizable, the Settings
///        font scale changes control sizes, and the detachable preview reflows the layout
///        underneath. A rect captured once drifts off its control within seconds.
///   (C5) The host panel is resolved through ResolveHostPanel, NOT `window.Content as Panel`.
///        CropToolWindow's root Content is a Border, so the naive cast silently disabled the whole
///        walkthrough on that app.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class CoachOverlay
{
    private const int DefaultMaxAutoShows = 3;
    private const double TickMs = 33;

    private sealed class Session
    {
        public string ScreenKey = "";
        public IReadOnlyList<CoachStep> Steps = Array.Empty<CoachStep>();
        public int Index;
        public Panel Root;
        public Grid Overlay;
        public DispatcherTimer? Timer;
        public double Phase;
        public bool ShownAtLeastOneStep;

        public Border FullDim, DimTop, DimLeft, DimRight, DimBottom, Highlight, Card;
        public Canvas CursorLayer;
        public ShapePath Cursor;
        public Ellipse Ring;
        public Rectangle BoxTrace;
        public TextBlock StepCounter, TitleText, BodyText;
        public Button BackBtn, NextBtn, SkipBtn;
        public EventHandler<KeyEventArgs>? KeyHandler;

        public Session(Panel root, Grid overlay, Border fullDim, Border dimTop, Border dimLeft,
                       Border dimRight, Border dimBottom, Border highlight, Border card,
                       Canvas cursorLayer, ShapePath cursor, Ellipse ring, Rectangle boxTrace,
                       TextBlock stepCounter, TextBlock titleText, TextBlock bodyText,
                       Button backBtn, Button nextBtn, Button skipBtn)
        {
            Root = root; Overlay = overlay;
            FullDim = fullDim; DimTop = dimTop; DimLeft = dimLeft; DimRight = dimRight;
            DimBottom = dimBottom; Highlight = highlight; Card = card;
            CursorLayer = cursorLayer; Cursor = cursor; Ring = ring; BoxTrace = boxTrace;
            StepCounter = stepCounter; TitleText = titleText; BodyText = bodyText;
            BackBtn = backBtn; NextBtn = nextBtn; SkipBtn = skipBtn;
        }
    }

    private sealed class RegisteredTour
    {
        public string ScreenKey = "";
        public IReadOnlyList<CoachStep> Steps = Array.Empty<CoachStep>();
        public int MaxAutoShows = DefaultMaxAutoShows;
    }

    private static readonly ConditionalWeakTable<Window, RegisteredTour> Tours = new();
    private static readonly ConditionalWeakTable<Window, Session> Active = new();

    private static string CounterFile(string screenKey) => $"coach_{screenKey}.txt";


    /// <summary>
    /// Registers a screen's walkthrough and runs it if the user has not yet used up their automatic
    /// showings. Safe to call from a window's Loaded handler on every open.
    /// </summary>
    public static void Register(Window window, string screenKey, IReadOnlyList<CoachStep> steps, int maxAutoShows = DefaultMaxAutoShows)
    {
        try
        {
            if (window == null || string.IsNullOrWhiteSpace(screenKey) || steps == null || steps.Count == 0) return;

            Tours.Remove(window);
            Tours.Add(window, new RegisteredTour { ScreenKey = screenKey, Steps = steps, MaxAutoShows = maxAutoShows });

            if (UiStateStore.ReadInt(CounterFile(screenKey), 0) >= maxAutoShows) return;

            Dispatcher.UIThread.Post(() => Start(window, screenKey, steps, isAutomatic: true), DispatcherPriority.Loaded);
        }
        catch (Exception ex) { SafeLog($"Register failed for '{screenKey}': {ex.Message}"); }
    }

    /// <summary>Replays the walkthrough registered for this window, ignoring the auto-show counter.</summary>
    public static void Replay(Window window)
    {
        try
        {
            if (window == null) return;
            if (!Tours.TryGetValue(window, out RegisteredTour? tour) || tour == null) return;
            Start(window, tour.ScreenKey, tour.Steps, isAutomatic: false);
        }
        catch (Exception ex) { SafeLog($"Replay failed: {ex.Message}"); }
    }

    /// <summary>Closes the walkthrough if one is running on this window. Call from OnClosing.</summary>
    public static void Cancel(Window window)
    {
        try { Finish(window, markSeen: false); }
        catch (Exception ex) { SafeLog($"Cancel failed: {ex.Message}"); }
    }

    /// <summary>True when this screen's walkthrough has already used up its automatic showings.</summary>
    public static bool HasBeenSeen(string screenKey, int maxAutoShows = DefaultMaxAutoShows)
        => UiStateStore.ReadInt(CounterFile(screenKey), 0) >= maxAutoShows;

    private static Panel? ResolveHostPanel(Window window)
    {
        if (window.Content is Panel direct) return direct;
        if (window.Content is Decorator dec && dec.Child is Panel decChild) return decChild;
        if (window.Content is ContentControl cc && cc.Content is Panel ccChild) return ccChild;
        return null;
    }

    private static void CoverWholeHost(Panel host, Control child)
    {
        if (host is Grid g)
        {
            Grid.SetRow(child, 0);
            Grid.SetColumn(child, 0);
            Grid.SetRowSpan(child, Math.Max(1, g.RowDefinitions.Count));
            Grid.SetColumnSpan(child, Math.Max(1, g.ColumnDefinitions.Count));
        }
    }

    private static void Start(Window window, string screenKey, IReadOnlyList<CoachStep> steps, bool isAutomatic)
    {
        Panel? root = ResolveHostPanel(window);
        if (root == null)
        {
            SafeLog($"No host panel for '{screenKey}' — walkthrough skipped.");
            return;
        }
        if (Active.TryGetValue(window, out Session? running) && running != null) return;

        Session s = BuildSession(window, root, screenKey, steps);

        CoverWholeHost(root, s.Overlay);
        root.Children.Add(s.Overlay);
        Active.Remove(window);
        Active.Add(window, s);

        s.KeyHandler = (_, e) =>
        {
            if (e.Key == Key.Escape) { Finish(window, markSeen: true); e.Handled = true; }
            else if (e.Key is Key.Enter or Key.Space or Key.Right) { Advance(window, +1); e.Handled = true; }
            else if (e.Key == Key.Left) { Advance(window, -1); e.Handled = true; }
        };
        window.AddHandler(InputElement.KeyDownEvent, s.KeyHandler, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        s.Timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        s.Timer.Tick += (_, _) => Tick(window, s);
        s.Timer.Start();

        ApplyStep(window, s);
        SafeLog($"Walkthrough started for '{screenKey}' ({(isAutomatic ? "automatic" : "replay")}), {steps.Count} step(s).");
    }

    private static Session BuildSession(Window window, Panel root, string screenKey, IReadOnlyList<CoachStep> steps)
    {
        IBrush dim = Res(window, "AppDimmerHeavyBrush", new SolidColorBrush(Color.Parse("#C8000000")));
        IBrush accent = Res(window, "AppAccentBrush", new SolidColorBrush(Color.Parse("#60a5fa")));
        IBrush surface = Res(window, "AppSurfaceBrush", new SolidColorBrush(Color.Parse("#1e293b")));
        IBrush textPrimary = Res(window, "AppTextPrimaryBrush", new SolidColorBrush(Color.Parse("#ffffff")));
        IBrush textMuted = Res(window, "AppTextMutedBrush", new SolidColorBrush(Color.Parse("#b6c2d0")));

        var fullDim = new Border { Background = dim, IsVisible = false };
        var dimTop = NewDimBlock(dim);
        var dimLeft = NewDimBlock(dim);
        var dimRight = NewDimBlock(dim);
        var dimBottom = NewDimBlock(dim);

        var highlight = new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false
        };

        var boxTrace = new Rectangle
        {
            Stroke = accent,
            StrokeThickness = 2,
            StrokeDashArray = new AvaloniaList<double> { 5, 4 },
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
            IsVisible = false
        };

        var ring = new Ellipse
        {
            Stroke = accent,
            StrokeThickness = 3,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
            Width = 10,
            Height = 10,
            IsVisible = false
        };

        var cursor = new ShapePath
        {
            Data = Geometry.Parse("M 0,0 L 0,17 L 4.2,13.2 L 6.8,19 L 9.6,17.7 L 7,12 L 12.4,11.7 Z"),
            Fill = Brushes.White,
            Stroke = Brushes.Black,
            StrokeThickness = 1,
            IsHitTestVisible = false,
            IsVisible = false
        };

        var cursorLayer = new Canvas { IsHitTestVisible = false };
        cursorLayer.Children.Add(boxTrace);
        cursorLayer.Children.Add(ring);
        cursorLayer.Children.Add(cursor);

        var stepCounter = new TextBlock { FontSize = Sz(11), FontWeight = FontWeight.Bold, Foreground = accent };
        var titleText = new TextBlock { FontSize = Sz(18), FontWeight = FontWeight.Bold, Foreground = textPrimary, TextWrapping = TextWrapping.Wrap };
        var bodyText = new TextBlock { FontSize = Sz(13), Foreground = textMuted, TextWrapping = TextWrapping.Wrap };

        var skipBtn = NewButton("SKIP", "Secondary", 90);
        var backBtn = NewButton("BACK", "Secondary", 90);
        var nextBtn = NewButton("NEXT", "Primary", 130);

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,8,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        Grid.SetColumn(skipBtn, 0); buttons.Children.Add(skipBtn);
        Grid.SetColumn(backBtn, 2); buttons.Children.Add(backBtn);
        Grid.SetColumn(nextBtn, 4); buttons.Children.Add(nextBtn);

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(stepCounter);
        stack.Children.Add(titleText);
        stack.Children.Add(bodyText);
        stack.Children.Add(buttons);

        var card = new Border
        {
            Background = surface,
            BorderBrush = accent,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(22),
            MaxWidth = 430,
            MinWidth = 330,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = stack
        };

        var overlay = new Grid
        {
            ZIndex = int.MaxValue,
            Background = Brushes.Transparent,
            Focusable = true
        };
        overlay.Children.Add(fullDim);
        overlay.Children.Add(dimTop);
        overlay.Children.Add(dimLeft);
        overlay.Children.Add(dimRight);
        overlay.Children.Add(dimBottom);
        overlay.Children.Add(highlight);
        overlay.Children.Add(cursorLayer);
        overlay.Children.Add(card);

        var s = new Session(root, overlay, fullDim, dimTop, dimLeft, dimRight, dimBottom, highlight,
                            card, cursorLayer, cursor, ring, boxTrace, stepCounter, titleText,
                            bodyText, backBtn, nextBtn, skipBtn)
        {
            ScreenKey = screenKey,
            Steps = steps,
            Index = 0
        };

        skipBtn.Click += (_, _) => Finish(window, markSeen: true);
        backBtn.Click += (_, _) => Advance(window, -1);
        nextBtn.Click += (_, _) => Advance(window, +1);

        return s;
    }

    private static Border NewDimBlock(IBrush dim) => new()
    {
        Background = dim,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        IsVisible = false
    };

    private static Button NewButton(string content, string styleClass, double minWidth)
    {
        var b = new Button
        {
            Content = content,
            MinHeight = 44,
            MinWidth = minWidth,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        b.Classes.Add(styleClass);
        return b;
    }

    private static void ApplyStep(Window window, Session s)
    {
        if (s.Index < 0) s.Index = 0;
        if (s.Index >= s.Steps.Count) { Finish(window, markSeen: true); return; }

        CoachStep step = s.Steps[s.Index];
        s.StepCounter.Text = $"STEP {s.Index + 1} OF {s.Steps.Count}";
        s.TitleText.Text = step.Title;
        s.BodyText.Text = step.Body;
        s.BackBtn.IsVisible = s.Index > 0;
        s.NextBtn.Content = s.Index == s.Steps.Count - 1 ? "GOT IT" : "NEXT";
        s.SkipBtn.IsVisible = s.Index < s.Steps.Count - 1;
        s.Phase = 0;
    }

    private static void Advance(Window window, int delta)
    {
        if (!Active.TryGetValue(window, out Session? s) || s == null) return;
        s.Index += delta;
        if (s.Index >= s.Steps.Count) { Finish(window, markSeen: true); return; }
        ApplyStep(window, s);
    }

    private static void Tick(Window window, Session s)
    {
        try
        {
            if (s.Index >= s.Steps.Count) return;
            s.Phase += TickMs / 1000.0;

            CoachStep step = s.Steps[s.Index];
            Rect? target = ResolveTarget(window, s, step.TargetName);

            double ow = s.Overlay.Bounds.Width;
            double oh = s.Overlay.Bounds.Height;
            if (ow <= 0 || oh <= 0) return;

            if (target is { } r && r.Width > 4 && r.Height > 4)
            {
                ShowSpotlight(s, r, ow, oh);
                AnimateGesture(s, step.Gesture, r);
                PlaceCard(s, r, ow, oh);
            }
            else
            {
                ShowFullDim(s);
                HideCursor(s);
                CentreCard(s, ow, oh);
            }

            if (!s.ShownAtLeastOneStep)
            {
                s.ShownAtLeastOneStep = true;
                int seen = UiStateStore.ReadInt(CounterFile(s.ScreenKey), 0);
                UiStateStore.WriteInt(CounterFile(s.ScreenKey), seen + 1);
            }
        }
        catch (Exception ex)
        {
            SafeLog($"Walkthrough tick failed, closing it: {ex.Message}");
            Finish(window, markSeen: false);
        }
    }

    private static Rect? ResolveTarget(Window window, Session s, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        Control? c = window.FindControl<Control>(name!);
        if (c == null || !c.IsVisible || c.Bounds.Width <= 0 || c.Bounds.Height <= 0) return null;

        Point? tl = c.TranslatePoint(new Point(0, 0), s.Overlay);
        if (tl == null) return null;
        return new Rect(tl.Value.X, tl.Value.Y, c.Bounds.Width, c.Bounds.Height);
    }

    private static void ShowSpotlight(Session s, Rect r, double ow, double oh)
    {
        const double pad = 6;
        double x = Math.Max(0, r.X - pad);
        double y = Math.Max(0, r.Y - pad);
        double w = Math.Min(ow - x, r.Width + pad * 2);
        double h = Math.Min(oh - y, r.Height + pad * 2);

        s.FullDim.IsVisible = false;

        Set(s.DimTop, 0, 0, ow, y);
        Set(s.DimBottom, 0, y + h, ow, Math.Max(0, oh - (y + h)));
        Set(s.DimLeft, 0, y, x, h);
        Set(s.DimRight, x + w, y, Math.Max(0, ow - (x + w)), h);

        s.Highlight.IsVisible = true;
        s.Highlight.Margin = new Thickness(x, y, 0, 0);
        s.Highlight.Width = w;
        s.Highlight.Height = h;
        s.Highlight.Opacity = 0.55 + 0.45 * (0.5 + 0.5 * Math.Sin(s.Phase * 3.0));
    }

    private static void Set(Border b, double x, double y, double w, double h)
    {
        b.IsVisible = w > 0 && h > 0;
        b.Margin = new Thickness(x, y, 0, 0);
        b.Width = Math.Max(0, w);
        b.Height = Math.Max(0, h);
    }

    private static void ShowFullDim(Session s)
    {
        s.FullDim.IsVisible = true;
        s.DimTop.IsVisible = false;
        s.DimLeft.IsVisible = false;
        s.DimRight.IsVisible = false;
        s.DimBottom.IsVisible = false;
        s.Highlight.IsVisible = false;
    }

    private static void HideCursor(Session s)
    {
        s.Cursor.IsVisible = false;
        s.Ring.IsVisible = false;
        s.BoxTrace.IsVisible = false;
    }

    private static void AnimateGesture(Session s, CoachGesture gesture, Rect r)
    {
        s.Cursor.IsVisible = true;
        double loop = (s.Phase % 2.4) / 2.4;
        double ease = 0.5 - 0.5 * Math.Cos(loop * Math.PI * 2);

        double cx = r.X + r.Width / 2;
        double cy = r.Y + r.Height / 2;
        double px;
        double py;
        s.BoxTrace.IsVisible = false;

        switch (gesture)
        {
            case CoachGesture.DragHorizontal:
                px = r.X + r.Width * (0.12 + 0.76 * ease);
                py = cy;
                break;

            case CoachGesture.DrawBox:
            {
                double x0 = r.X + r.Width * 0.28;
                double y0 = r.Y + r.Height * 0.26;
                double x1 = r.X + r.Width * 0.72;
                double y1 = r.Y + r.Height * 0.74;
                px = x0 + (x1 - x0) * ease;
                py = y0 + (y1 - y0) * ease;
                s.BoxTrace.IsVisible = true;
                s.BoxTrace.Width = Math.Max(1, px - x0);
                s.BoxTrace.Height = Math.Max(1, py - y0);
                Canvas.SetLeft(s.BoxTrace, x0);
                Canvas.SetTop(s.BoxTrace, y0);
                break;
            }

            case CoachGesture.DropIn:
                px = cx;
                py = r.Y - 40 + (r.Height / 2 + 40) * ease;
                break;

            case CoachGesture.Click:
                px = cx;
                py = cy;
                break;

            default:
                px = cx;
                py = cy + Math.Sin(s.Phase * 3.4) * 5;
                break;
        }

        Canvas.SetLeft(s.Cursor, px);
        Canvas.SetTop(s.Cursor, py);

        double ringT = (s.Phase % 1.2) / 1.2;
        double ringSize = 12 + ringT * 46;
        s.Ring.IsVisible = true;
        s.Ring.Width = ringSize;
        s.Ring.Height = ringSize;
        s.Ring.Opacity = Math.Max(0, 0.75 - ringT * 0.75);
        Canvas.SetLeft(s.Ring, px - ringSize / 2);
        Canvas.SetTop(s.Ring, py - ringSize / 2);
    }

    private static void PlaceCard(Session s, Rect r, double ow, double oh)
    {
        (double cw, double ch) = CardSize(s, ow, oh);
        const double gap = 20;

        double y = r.Bottom + gap;
        if (y + ch > oh - 10) y = r.Y - gap - ch;
        if (y < 10) y = Math.Max(10, (oh - ch) / 2);

        double x = r.X + r.Width / 2 - cw / 2;
        x = Math.Clamp(x, 10, Math.Max(10, ow - cw - 10));

        s.Card.Margin = new Thickness(x, y, 0, 0);
    }

    private static void CentreCard(Session s, double ow, double oh)
    {
        (double cw, double ch) = CardSize(s, ow, oh);
        s.Card.Margin = new Thickness(Math.Max(10, (ow - cw) / 2), Math.Max(10, (oh - ch) / 2), 0, 0);
    }

    /// <summary>
    /// Prefers the card's real laid-out bounds and only forces a Measure on the very first frames,
    /// before layout has run. Calling Measure on every tick with a constraint the layout system did
    /// not choose invites layout thrash on a 30Hz timer.
    /// </summary>
    private static (double Width, double Height) CardSize(Session s, double ow, double oh)
    {
        double cw = s.Card.Bounds.Width;
        double ch = s.Card.Bounds.Height;
        if (cw <= 1 || ch <= 1)
        {
            s.Card.Measure(new Size(ow, oh));
            cw = s.Card.DesiredSize.Width;
            ch = s.Card.DesiredSize.Height;
        }
        return (Math.Max(cw, s.Card.MinWidth), Math.Max(ch, 100));
    }

    private static void Finish(Window window, bool markSeen)
    {
        try
        {
            if (!Active.TryGetValue(window, out Session? s) || s == null) return;
            Active.Remove(window);

            s.Timer?.Stop();
            s.Timer = null;

            if (s.KeyHandler != null)
            {
                try { window.RemoveHandler(InputElement.KeyDownEvent, s.KeyHandler); }
                catch (Exception ex) { SafeLog($"Could not detach walkthrough key handler: {ex.Message}"); }
                s.KeyHandler = null;
            }

            s.Root.Children.Remove(s.Overlay);

            if (markSeen) UiStateStore.WriteInt(CounterFile(s.ScreenKey), int.MaxValue / 2);
        }
        catch (Exception ex) { SafeLog($"Finish failed: {ex.Message}"); }
    }

    private static IBrush Res(Control? host, string key, IBrush fallback)
        => Infrastructure.ThemeResources.Brush(host, key, fallback);

    private static double Sz(double baseSize) => Infrastructure.ThemeManager.ScaledFontSize(baseSize);

    private static void SafeLog(string message)
    {
        try { RuntimeLog.Info("COACH", message); }
        catch (Exception) { /* a walkthrough must never fail because logging failed */ }
    }
}
