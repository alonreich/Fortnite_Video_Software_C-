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
    private const int BubbleCount = 35; // Dense enough to feel underwater, but spread out
    private const double PhysicsHz = 60.0;
    private const double FixedDeltaSeconds = 1.0 / PhysicsHz;

    private readonly Bubble[] _bubbles = new Bubble[BubbleCount];
    private readonly Random _rng = new();
    private readonly Stopwatch _clock = new();
    private double _lastFrameSeconds;
    private double _accumulator;
    private bool _running;

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
        ClipToBounds = false; // Allow bubbles to drift over/under adjacent panels naturally

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
        foreach (var b in _bubbles)
        {
            b.Y -= b.BaseSpeed;
            b.X += Math.Sin(b.Y * 7.0 + now * b.WobbleSpeed + b.Seed) * b.WobbleAmount;
            
            // Seamless horizontal wrapping
            if (b.X < -0.1) b.X = 1.1;
            if (b.X > 1.1) b.X = -0.1;

            if (b.Y < -0.15)
            {
                ResetBubble(b, randomY: false);
            }
        }
    }

    private void ResetBubble(Bubble b, bool randomY)
    {
        b.X = -0.1 + _rng.NextDouble() * 1.2;
        b.Y = randomY ? -0.1 + _rng.NextDouble() * 1.3 : 1.15 + _rng.NextDouble() * 0.2;

        // Extremely randomized "anomalies" - reduced maximum sizes so they don't look broken
        double anomalyRoll = _rng.NextDouble();
        if (anomalyRoll > 0.96) b.Size = 25.0 + _rng.NextDouble() * 15.0; // Massive rare anomaly
        else if (anomalyRoll > 0.85) b.Size = 12.0 + _rng.NextDouble() * 12.0; // Large chunk
        else b.Size = 3.0 + _rng.NextDouble() * 10.0; // Standard ambient particles

        // Larger bubbles rise faster, but overall speed is very slow
        b.BaseSpeed = (0.1 + _rng.NextDouble() * (b.Size / 15.0)) / 1000.0; 
        
        b.WobbleSpeed = 0.2 + _rng.NextDouble() * 0.8;
        b.WobbleAmount = (0.2 + _rng.NextDouble() * 1.0) / 400.0;
        b.Seed = _rng.NextDouble() * Math.PI * 2;
        
        // Softer opacity for better realism, but high enough to actually be visible
        b.Opacity = b.Size > 20.0 ? 0.08 + _rng.NextDouble() * 0.08 : 0.15 + _rng.NextDouble() * 0.15;
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

            // Only fade in at the bottom. Do not fade out at the top so it doesn't look cut off under the menu!
            double edgeFade = Math.Clamp((1.2 - b.Y) * 5.0, 0, 1);
            double alpha = b.Opacity * edgeFade;
            if (alpha <= 0.001) continue;

            double cx = b.X * w;
            double cy = b.Y * h;

            // Draw sharp glass outer rim
            double thickness = Math.Max(0.8, b.Size * 0.03); // Delicate glass rim
            var bubbleStroke = new ImmutableSolidColorBrush(Color.FromArgb((byte)(alpha * 255), 255, 255, 255));
            context.DrawEllipse(null, new ImmutablePen(bubbleStroke, thickness), new Point(cx, cy), b.Size, b.Size);
            
            // 3D Glass Specular Reflection: Sharp, offset highlight (identitical to FluidVolumeSlider)
            var hiOpacity = Math.Min(1.0, alpha + 0.4);
            var hiBrush = new ImmutableSolidColorBrush(Color.FromArgb((byte)(hiOpacity * 255), 255, 255, 255));
            context.DrawEllipse(hiBrush, null, new Point(cx - b.Size * 0.3, cy - b.Size * 0.3), b.Size * 0.35, b.Size * 0.35);
        }
    }
}
