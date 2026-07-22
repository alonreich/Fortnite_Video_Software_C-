using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using System;
using System.Diagnostics;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>
/// Ambient "underwater" anomaly background layer.
/// Extremely randomized bubbles mimicking the 3D hollow refractive style 
/// of the FluidVolumeSlider, creating a deep-sea atmosphere.
/// </summary>
public sealed class AmbientBubblesBackground : Control
{
    private const int BubbleCount = 35;
    private const double PhysicsHz = 60.0;
    private const double FixedDeltaSeconds = 1.0 / PhysicsHz;

    private readonly Bubble[] _bubbles = new Bubble[BubbleCount];
    private readonly Random _rng = new();
    private readonly Stopwatch _clock = new();
    private double _lastFrameSeconds;
    private double _accumulator;
    private bool _running;
    
    private Point _mousePos = new Point(-1000, -1000);
    private Avalonia.PixelPoint _lastWindowPos;
    private double _gravityShearX;
    private double _gravityShearY;

    private sealed class Bubble
    {
        public double X;
        public double Y;
        public double Size;
        public double BaseSpeed;
        public double WobbleSpeed;
        public double WobbleAmount;
        public double Seed;
        public double Opacity;
    }

    public AmbientBubblesBackground()
    {
        IsHitTestVisible = false;
        ClipToBounds = false;

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
        _accumulator = 0;
        _running = true;
        var top = TopLevel.GetTopLevel(this);
        if (top != null)
        {
            top.PointerMoved += OnTopLevelPointerMoved;
            if (top is Window w)
            {
                _lastWindowPos = w.Position;
            }
            top.RequestAnimationFrame(OnAnimationFrame);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top != null)
        {
            top.PointerMoved -= OnTopLevelPointerMoved;
        }
        base.OnDetachedFromVisualTree(e);
        _running = false;
        _clock.Stop();
    }

    private void OnTopLevelPointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        _mousePos = e.GetPosition(this);
    }

    private void OnAnimationFrame(TimeSpan _)
    {
        if (!_running) return;

        double now = NowSeconds;
        double frameDelta = Math.Min(now - _lastFrameSeconds, 0.25);
        _lastFrameSeconds = now;
        _accumulator += frameDelta;

        while (_accumulator >= FixedDeltaSeconds)
        {
            StepPhysics(now);
            _accumulator -= FixedDeltaSeconds;
        }

        if (IsEffectivelyVisible)
        {
            InvalidateVisual();
        }

        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnAnimationFrame);
    }

    private void StepPhysics(double now)
    {
        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w <= 1 || h <= 1) return;

        var top = TopLevel.GetTopLevel(this);
        if (top is Window win)
        {
            var curPos = win.Position;
            double dx = curPos.X - _lastWindowPos.X;
            double dy = curPos.Y - _lastWindowPos.Y;
            _lastWindowPos = curPos;

            _gravityShearX += (-dx * 0.005 - _gravityShearX) * 0.1;
            _gravityShearY += (-dy * 0.005 - _gravityShearY) * 0.1;
        }

        _gravityShearX *= 0.95;
        _gravityShearY *= 0.95;

        foreach (var b in _bubbles)
        {
            double bx = b.X * w;
            double by = b.Y * h;

            double distX = bx - _mousePos.X;
            double distY = by - _mousePos.Y;
            double distSq = distX * distX + distY * distY;
            double repulsionRadius = 150.0;
            double repulsionRadiusSq = repulsionRadius * repulsionRadius;

            if (distSq < repulsionRadiusSq && distSq > 0.1)
            {
                double force = (1.0 - distSq / repulsionRadiusSq) * 15.0;
                double length = Math.Sqrt(distSq);
                b.X += (distX / length) * force / w;
                b.Y += (distY / length) * force / h;
            }

            b.Y -= b.BaseSpeed - (_gravityShearY * b.BaseSpeed * 50.0);
            b.X += Math.Sin(b.Y * 7.0 + now * b.WobbleSpeed + b.Seed) * b.WobbleAmount + (_gravityShearX * b.BaseSpeed * 50.0);
            
            if (b.X < -0.2) b.X = 1.2;
            if (b.X > 1.2) b.X = -0.2;

            if (b.Y < -0.25 || b.Y > 1.35)
            {
                ResetBubble(b, randomY: false);
            }
        }
    }

    private void ResetBubble(Bubble b, bool randomY)
    {
        b.X = -0.1 + _rng.NextDouble() * 1.2;
        b.Y = randomY ? -0.1 + _rng.NextDouble() * 1.3 : 1.15 + _rng.NextDouble() * 0.2;

        double anomalyRoll = _rng.NextDouble();
        if (anomalyRoll > 0.96) b.Size = 25.0 + _rng.NextDouble() * 15.0;
        else if (anomalyRoll > 0.85) b.Size = 12.0 + _rng.NextDouble() * 12.0;
        else b.Size = 3.0 + _rng.NextDouble() * 10.0;

        b.BaseSpeed = (0.1 + _rng.NextDouble() * (b.Size / 15.0)) / 1000.0; 
        
        b.WobbleSpeed = 0.2 + _rng.NextDouble() * 0.8;
        b.WobbleAmount = (0.2 + _rng.NextDouble() * 1.0) / 400.0;
        b.Seed = _rng.NextDouble() * Math.PI * 2;
        
        b.Opacity = 0.05 + _rng.NextDouble() * 0.05;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w <= 1 || h <= 1) return;

        foreach (var b in _bubbles)
        {
            if (b.Y > 1.25 || b.Y < -0.25) continue;

            double edgeFade = Math.Clamp((1.2 - b.Y) * 5.0, 0, 1);
            double alpha = b.Opacity * edgeFade;
            if (alpha <= 0.001) continue;

            double cx = b.X * w;
            double cy = b.Y * h;

            var bubbleGradient = new ImmutableRadialGradientBrush(
                new[] {
                    new ImmutableGradientStop(0.0, Color.FromArgb(0, 255, 255, 255)),
                    new ImmutableGradientStop(0.7, Color.FromArgb((byte)(alpha * 80), 255, 255, 255)),
                    new ImmutableGradientStop(1.0, Color.FromArgb((byte)(alpha * 255), 255, 255, 255))
                },
                center: new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                gradientOrigin: new RelativePoint(0.3, 0.3, RelativeUnit.Relative),
                radius: 0.5);

            context.DrawEllipse(bubbleGradient, null, new Point(cx, cy), b.Size, b.Size);
            
            var hiOpacity = Math.Min(1.0, alpha + 0.2);
            var hiBrush = new ImmutableRadialGradientBrush(
                new[] {
                    new ImmutableGradientStop(0.0, Color.FromArgb((byte)(hiOpacity * 255), 255, 255, 255)),
                    new ImmutableGradientStop(1.0, Color.FromArgb(0, 255, 255, 255))
                },
                center: new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                gradientOrigin: new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                radius: 0.5);
                
            context.DrawEllipse(hiBrush, null, new Point(cx - b.Size * 0.35, cy - b.Size * 0.35), b.Size * 0.25, b.Size * 0.25);
        }
    }
}
