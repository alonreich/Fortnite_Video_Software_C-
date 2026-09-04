using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>How a floating notice is coloured. Nothing else about it changes.</summary>
public enum NoticeKind
{
    /// <summary>Something happened and it worked. Green.</summary>
    Success,
    /// <summary>Neutral state change or a hint. Blue.</summary>
    Info,
    /// <summary>The user tried something that will not work. Amber. This is the historical default.</summary>
    Warning,
    /// <summary>Something failed. Red.</summary>
    Error
}

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// ISSUE_09 + ISSUE_14 — ONE NOTIFICATION STYLE FOR THE WHOLE SUITE, AND THE GHOST CANVAS IT USES.
///
/// WHAT WAS WRONG (ISSUE_09). Six windows told the user things in five different ways:
///   Main App        a Popup floating up over the video, fading out   (the one people liked)
///   Speed Editor    the same Popup, plus a status line at the bottom
///   Music Wizard    a hand-rolled grey Border sliding in from the top for 2.5s
///   Video Merger    a text line under the queue
///   Crop Tools      a text line at the bottom
///   Voice Over      a status label
/// So "did that work?" had a different answer, in a different place, on every screen — and the
/// Music Wizard's version silently showed NOTHING if its host panel could not be found.
///
/// WHAT THIS IS. The float-up-and-fade notice, extracted from the Main App and made reusable. Any
/// window can raise one with a single call and no XAML.
///
/// WHY IT IS A CANVAS AND NOT THE OLD Popup (ISSUE_14, ghost 1). The old FloatingFeedback needed
/// THREE named XAML controls per window (a Popup, its Border, its TextBlock) and an OS-level popup
/// window, which is exactly why no other screen could reuse it. Meanwhile MainWindow.axaml carried
/// an element called `OverlayCanvas`, commented "for Pop-up Badges and Custom Floating Animations",
/// that NOTHING in the entire source tree ever drew into — a sheet of clear glass built for this
/// job and then never connected. It is connected now: when a window has an `OverlayCanvas` this
/// draws into it, and when it does not, one is created on the fly. The dead element became the
/// engine, and the per-window Popups are gone.
///
/// HARD RULES.
///   (F1) NEVER throws into a caller. A missing notification must not break the action it was
///        reporting on.
///   (F2) The host canvas is ALWAYS IsHitTestVisible=false. These float over live controls,
///        including the timeline; one must never eat a click.
///   (F3) ZIndex sits below CoachOverlay (int.MaxValue) on purpose, so a notice cannot cover the
///        walkthrough that is teaching the user what the notice means.
///   (F4) Identical text inside DedupeWindow is dropped. Several callers fire on drag ticks, and
///        without this a drag would stack forty copies of the same sentence up the screen.
///   (F5) At most MaxConcurrent are alive at once; the oldest is retired early. A burst must
///        degrade to "the last few", never to an unreadable pile.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class FloatingNotice
{
    /// <summary>Identical text inside this window is treated as a repeat and dropped (F4).</summary>
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromMilliseconds(1400);

    /// <summary>How long the notice holds still and fully opaque before it starts to rise.</summary>
    private const int HoldMs = 900;

    /// <summary>Frames of the rise-and-fade. 45 x 16ms ≈ 0.72s, matching the old Popup animation.</summary>
    private const int FadeFrames = 45;

    private const int MaxConcurrent = 3;

    private const string PreferredHostName = "OverlayCanvas";

    private sealed class HostState
    {
        public Canvas? Canvas;
        public string LastText = "";
        public DateTime LastShown = DateTime.MinValue;
        public readonly List<Control> Live = new();
    }

    private static readonly ConditionalWeakTable<Window, HostState> Hosts = new();


    /// <summary>
    /// Floats a short message up over the window and fades it out. Safe to call from any thread and
    /// at any point in a window's life, including before it has been shown.
    /// </summary>
    public static void Show(Window? window, string text, NoticeKind kind = NoticeKind.Warning)
    {
        if (window == null || string.IsNullOrWhiteSpace(text)) return;

        try
        {
            if (Dispatcher.UIThread.CheckAccess()) ShowCore(window, text, kind, null);
            else Dispatcher.UIThread.Post(() => ShowCore(window, text, kind, null));
        }
        catch (Exception ex) { SafeLog($"Show failed: {ex.Message}"); }
    }

    /// <summary>
    /// ANCHOR_01 — floats the message NEXT TO a specific control instead of over the middle of the
    /// window.
    ///
    /// WHY THIS IS OPT-IN AND NOT THE DEFAULT: every screen in the suite shares this component, and
    /// a centred notice is the right answer for messages about the project as a whole ("export
    /// finished", "nothing to undo"). It is the WRONG answer for a message about a button the user
    /// just pressed — the eye is on the button, and the words appear somewhere else entirely. This
    /// overload is for that second case.
    ///
    /// Falls back to the centred placement whenever the anchor cannot be located: not attached to
    /// a visual tree yet, zero-sized mid-layout, or in a different window. A notice must never be
    /// lost because the thing it points at moved.
    /// </summary>
    public static void ShowAt(Window? window, Control? anchor, string text, NoticeKind kind = NoticeKind.Warning)
    {
        if (window == null || string.IsNullOrWhiteSpace(text)) return;

        try
        {
            if (Dispatcher.UIThread.CheckAccess()) ShowCore(window, text, kind, anchor);
            else Dispatcher.UIThread.Post(() => ShowCore(window, text, kind, anchor));
        }
        catch (Exception ex) { SafeLog($"ShowAt failed: {ex.Message}"); }
    }

    /// <summary>Convenience wrappers so call sites read as English rather than as an enum.</summary>
    public static void Success(Window? window, string text) => Show(window, text, NoticeKind.Success);
    public static void Info(Window? window, string text) => Show(window, text, NoticeKind.Info);
    public static void Warn(Window? window, string text) => Show(window, text, NoticeKind.Warning);
    public static void Error(Window? window, string text) => Show(window, text, NoticeKind.Error);

    /// <summary>
    /// Drops every notice currently on screen for this window and forgets the dedupe state.
    /// Call from OnClosed so a rising notice cannot outlive its window.
    /// </summary>
    public static void Clear(Window? window)
    {
        if (window == null) return;
        try
        {
            if (!Hosts.TryGetValue(window, out HostState? st) || st == null) return;
            if (st.Canvas != null)
            {
                foreach (Control c in st.Live.ToArray()) st.Canvas.Children.Remove(c);
            }
            st.Live.Clear();
            st.LastText = "";
            st.LastShown = DateTime.MinValue;
        }
        catch (Exception ex) { SafeLog($"Clear failed: {ex.Message}"); }
    }

    private static void ShowCore(Window window, string text, NoticeKind kind, Control? anchor)
    {
        try
        {
            HostState st = Hosts.GetValue(window, _ => new HostState());

            DateTime now = DateTime.UtcNow;
            if (string.Equals(st.LastText, text, StringComparison.Ordinal) && now - st.LastShown < DedupeWindow) return;
            st.LastText = text;
            st.LastShown = now;

            Canvas? host = EnsureHost(window, st);
            if (host == null) return;

            while (st.Live.Count >= MaxConcurrent)
            {
                Control oldest = st.Live[0];
                st.Live.RemoveAt(0);
                host.Children.Remove(oldest);
            }

            (IBrush accent, IBrush fill) = Palette(window, kind);

            var label = new TextBlock
            {
                Text = text,
                Foreground = accent,
                // ANCHOR_01: an anchored notice sits beside a control, so it is sized like a
                // tooltip. The centred one is a full-window banner and keeps its 24pt.
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(anchor != null ? 12 : 24),
                FontWeight = FontWeight.Bold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var pill = new Border
            {
                Background = fill,
                BorderBrush = accent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = anchor != null ? new Thickness(12, 7) : new Thickness(24, 12),
                MaxWidth = anchor != null ? 340 : 620,
                IsHitTestVisible = false,
                Opacity = 0,
                Child = label
            };

            host.Children.Add(pill);
            st.Live.Add(pill);
            Place(host, pill, anchor);
            _ = AnimateAsync(st, host, pill, anchor);
        }
        catch (Exception ex) { SafeLog($"ShowCore failed: {ex.Message}"); }
    }

    /// <summary>
    /// Centres the pill horizontally and parks it just below the middle of the host, returning the
    /// travel it should make on its way out.
    ///
    /// ⚠️ WHY THIS IS A SEPARATE METHOD AND IS CALLED TWICE. A pill inside a Canvas gets no layout
    /// slot, so its Bounds are 0 until it has been arranged — and on the FIRST notice of a session
    /// the host canvas itself may have been created microseconds ago and still measure 0x0 too.
    /// Placing once from those zeros parks the very first message the user ever sees in the
    /// top-left corner. So it is placed immediately (best effort, using fallbacks), and placed
    /// AGAIN on the first animation frame once real sizes exist. Do not collapse this back inline.
    /// </summary>
    private static (double StartY, double EndY) Place(Canvas host, Border pill, Control? anchor)
    {
        // ANCHOR_01 — park it just ABOVE the anchor, horizontally centred on it, and let it drift
        // up only a short way so it never leaves the control's neighbourhood. Any failure to map
        // the anchor's position falls through to the centred placement below.
        if (anchor != null)
        {
            try
            {
                if (anchor.GetVisualRoot() != null && anchor.Bounds.Width > 0)
                {
                    Point? tl = anchor.TranslatePoint(new Point(0, 0), host);
                    if (tl.HasValue)
                    {
                        double hostWa = host.Bounds.Width;
                        pill.Measure(new Size(hostWa > 0 ? hostWa : 340, double.PositiveInfinity));
                        double pwA = pill.DesiredSize.Width;
                        double phA = pill.DesiredSize.Height;

                        double ax = tl.Value.X + (anchor.Bounds.Width - pwA) / 2.0;
                        double ay = tl.Value.Y - phA - 10;

                        // If there is no room above, sit under it instead.
                        if (ay < 4) ay = tl.Value.Y + anchor.Bounds.Height + 10;

                        // Keep it inside the host no matter where the control is.
                        if (hostWa > 0) ax = Math.Max(4, Math.Min(hostWa - pwA - 4, ax));
                        else ax = Math.Max(4, ax);

                        double hostHa = host.Bounds.Height;
                        if (hostHa > 0) ay = Math.Max(4, Math.Min(hostHa - phA - 4, ay));
                        else ay = Math.Max(4, ay);

                        Canvas.SetLeft(pill, ax);
                        Canvas.SetTop(pill, ay);
                        return (ay, Math.Max(4, ay - 26));
                    }
                }
            }
            catch (Exception ex) { SafeLog($"Anchored placement failed, centring instead: {ex.Message}"); }
        }

        double hostW = host.Bounds.Width;
        double hostH = host.Bounds.Height;

        pill.Measure(new Size(hostW > 0 ? hostW : 900, double.PositiveInfinity));
        double pw = pill.DesiredSize.Width;
        double ph = pill.DesiredSize.Height;

        double hw = hostW > 0 ? hostW : pw;
        double hh = hostH > 0 ? hostH : ph * 6;

        double x = Math.Max(8, (hw - pw) / 2);
        double startY = Math.Max(8, hh * 0.55);
        double endY = Math.Max(8, hh * 0.55 - hh / 3.0 - 40);

        Canvas.SetLeft(pill, x);
        Canvas.SetTop(pill, startY);
        return (startY, endY);
    }

    private static async Task AnimateAsync(HostState st, Canvas host, Border pill, Control? anchor)
    {
        try
        {
            pill.Opacity = 1;

            await Task.Delay(1);
            if (!st.Live.Contains(pill)) return;
            (double startY, double endY) = Place(host, pill, anchor);

            await Task.Delay(HoldMs);

            for (int i = 1; i <= FadeFrames; i++)
            {
                if (!st.Live.Contains(pill)) return;

                double progress = (double)i / FadeFrames;
                double eased = 1.0 - Math.Pow(1.0 - progress, 2.0);
                pill.Opacity = 1.0 - progress;
                Canvas.SetTop(pill, startY + (endY - startY) * eased);
                await Task.Delay(16);
            }
        }
        catch (Exception ex) { SafeLog($"Animation failed: {ex.Message}"); }
        finally
        {
            try
            {
                st.Live.Remove(pill);
                host.Children.Remove(pill);
            }
            catch (Exception ex) { SafeLog($"Notice cleanup failed: {ex.Message}"); }
        }
    }

    /// <summary>
    /// ISSUE_14 — this is where the ghost gets its job. A window that declares an `OverlayCanvas`
    /// (MainWindow does; it was dead) has its notices drawn there. Any other window gets an
    /// equivalent canvas created on demand, so no screen needs XAML changes to gain notifications.
    /// </summary>
    private static Canvas? EnsureHost(Window window, HostState st)
    {
        if (st.Canvas != null && st.Canvas.Parent != null) return st.Canvas;

        Canvas? declared = window.FindControl<Canvas>(PreferredHostName);
        if (declared != null)
        {
            declared.IsHitTestVisible = false;
            st.Canvas = declared;
            return declared;
        }

        Panel? root = ResolveHostPanel(window);
        if (root == null)
        {
            SafeLog("No host panel — notice suppressed.");
            return null;
        }

        var created = new Canvas
        {
            Name = PreferredHostName,
            IsHitTestVisible = false,
            ZIndex = int.MaxValue - 1000
        };
        CoverWholeHost(root, created);
        root.Children.Add(created);
        st.Canvas = created;
        return created;
    }

    /// <summary>
    /// Mirrors CoachOverlay.ResolveHostPanel — CropToolWindow's root Content is a Border, so a
    /// bare `window.Content as Panel` silently disables the feature on a whole application.
    /// </summary>
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

    private static (IBrush Accent, IBrush Fill) Palette(Control? host, NoticeKind kind)
    {
        string accentKey = kind switch
        {
            NoticeKind.Success => "AppSuccessBrush",
            NoticeKind.Info => "AppInfoBrush",
            NoticeKind.Error => "AppDangerBrush",
            _ => "AppWarningBrush"
        };

        IBrush accent = Res(host, accentKey, new SolidColorBrush(Color.Parse("#facc15")));
        IBrush fill = Res(host, "AppOverlayBrush", new SolidColorBrush(Color.Parse("#aa000000")));
        return (accent, fill);
    }

    private static IBrush Res(Control? host, string key, IBrush fallback)
        => Infrastructure.ThemeResources.Brush(host, key, fallback);

    private static void SafeLog(string message)
    {
        try { RuntimeLog.Info("NOTICE", message); }
        catch (Exception) { }
    }
}
