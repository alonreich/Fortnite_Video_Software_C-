using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using System;
using System.Diagnostics;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>
/// Very gentle ambient "live wallpaper" bubble layer, echoing the FluidVolumeSlider's
/// bubble particle system across the app's theme background.
///
/// Design constraints (deliberate — do not "enhance"):
///  - EXTREMELY subtle: max ~7% opacity, tiny sizes, slow drift. It must never
///    compete with real UI content or distract from editing.
///  - Fully decoupled animation (RequestAnimationFrame + fixed 30Hz timestep),
///    matching the FluidVolumeSlider pattern: no layout passes are ever triggered,
///    only render invalidation, and only while the control is effectively visible.
///  - IsHitTestVisible is hard-disabled: the layer can never steal pointer input.
///  - No caching/memoization of app data — purely stateless visual particles.
/// </summary>
public sealed class AmbientBubblesBackground : Control
{
    private const int BubbleCount = 12;               // sparse on purpose
    private const double PhysicsHz = 30.0;            // gentle half-rate physics
    private const double FixedDeltaSeconds = 1.0 / PhysicsHz;
    private const double RenderIntervalSeconds = 1.0 / 30.0; // cap paints at ~30fps

    private readonly Bubble[] _bubbles = new Bubble[BubbleCount];
    private readonly Random _rng = new();
    private readonly Stopwatch _clock = new();
    private double _lastFrameSeconds;
    private double _lastRenderSeconds;
    private double _accumulator;
    private bool _running;

    private sealed class Bubble
    {
        public double X;            // 0..1 across the control
        public double Y;            // 0..1 from the top (rises toward 0)
        public double Size;         // radius in px
        public double BaseSpeed;    // normalized rise per physics step
        public double WobbleSpeed;
        public double WobbleAmount;
        public double Seed;
        public double Opacity;      // peak opacity (very low)
    }

    // Soft info-blue reads gently on BOTH the dark slate and light surfaces,
    // unlike pure white (invisible on light) or black (harsh on dark).
    private static readonly ImmutableSolidColorBrush s_bubbleBrush =
        new(Color.Parse("#38bdf8"));
    private static readonly ImmutableSolidColorBrush s_specularBrush =
        new(Colors.White);

    public AmbientBubblesBackground()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;

        for (int i = 0; i < BubbleCount; i++)
        {
            _bubbles[i] = new Bubble();
            ResetBubble(_bubbles[i], randomY: true);
        }
    }

    private double NowSeconds => _clock.Elapsed.TotalSeconds;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _clock.Restart();
        _lastFrameSeconds = 0;
        _lastRenderSeconds = 0;
        _accumulator = 0;
        _running = true;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnAnimationFrame);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _running = false;
        _clock.Stop();
    }

    private void OnAnimationFrame(TimeSpan _)
    {
        if (!_running) return;

        double now = NowSeconds;
        double frameDelta = Math.Min(now - _lastFrameSeconds, 0.25); // clamp hitches
        _lastFrameSeconds = now;
        _accumulator += frameDelta;

        // Fixed timestep: identical drift on 60/144/240Hz/VRR monitors.
        while (_accumulator >= FixedDeltaSeconds)
        {
            StepPhysics(now);
            _accumulator -= FixedDeltaSeconds;
        }

        // Throttled repaint keeps the ambient layer near-free on the GPU.
        if (IsEffectivelyVisible && now - _lastRenderSeconds >= RenderIntervalSeconds)
        {
            _lastRenderSeconds = now;
            InvalidateVisual();
        }

        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnAnimationFrame);
    }

    private void StepPhysics(double now)
    {
        foreach (var b in _bubbles)
        {
            b.Y -= b.BaseSpeed;
            b.X += Math.Sin(b.Y * 7.0 + now * b.WobbleSpeed + b.Seed) * b.WobbleAmount;
            b.X = Math.Clamp(b.X, 0.02, 0.98);

            if (b.Y < -0.05)
            {
                ResetBubble(b, randomY: false);
            }
        }
    }

    private void ResetBubble(Bubble b, bool randomY)
    {
        b.X = 0.03 + _rng.NextDouble() * 0.94;
        b.Y = randomY ? _rng.NextDouble() : 1.05;
        b.Size = 2.0 + _rng.NextDouble() * 5.0;                  // 2–7 px radius
        b.BaseSpeed = (0.55 + _rng.NextDouble() * 1.2) / 1000.0; // ~30–90s full climb
        b.WobbleSpeed = 0.25 + _rng.NextDouble() * 0.9;
        b.WobbleAmount = (0.05 + _rng.NextDouble() * 0.30) / 400.0;
        b.Seed = _rng.NextDouble() * Math.PI * 2;
        b.Opacity = 0.025 + _rng.NextDouble() * 0.045;           // peak 2.5–7%
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w <= 1 || h <= 1) return;

        foreach (var b in _bubbles)
        {
            if (b.Y > 1.02 || b.Y < -0.02) continue;

            // Fade in near the bottom and dissolve near the top — no popping.
            double edgeFade = Math.Clamp(Math.Min((1.02 - b.Y) * 6.0, b.Y * 6.0), 0, 1);
            double alpha = b.Opacity * edgeFade;
            if (alpha <= 0.001) continue;

            double cx = b.X * w;
            double cy = b.Y * h;

            using (context.PushOpacity(alpha))
            {
                context.DrawEllipse(s_bubbleBrush, null, new Point(cx, cy), b.Size, b.Size);
                // Tiny off-center specular glint (mirrors the volume-tube bubbles).
                context.DrawEllipse(
                    s_specularBrush, null,
                    new Point(cx - b.Size * 0.30, cy - b.Size * 0.30),
                    b.Size * 0.28, b.Size * 0.28);
            }
        }
    }
}
