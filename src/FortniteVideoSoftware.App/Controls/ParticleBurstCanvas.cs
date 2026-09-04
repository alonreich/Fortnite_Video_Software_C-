using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>
/// Event-driven particle burst layer for satisfying visual feedback on key actions.
/// Render-only (zero layout passes), fixed-timestep physics, self-halting when idle.
/// Architecturally identical to AmbientBubblesBackground and the Tick-Based Fight Canvas.
/// </summary>
public sealed class ParticleBurstCanvas : Control
{
    private const double PhysicsHz = 60.0;
    private const double FixedDeltaSeconds = 1.0 / PhysicsHz;
    private const int MaxParticles = 150;

    private readonly List<Particle> _particles = new(MaxParticles);
    private readonly Random _rng = new();
    private readonly Stopwatch _clock = new();
    private double _lastFrameSeconds;
    private double _accumulator;
    private bool _running;
    private bool _animating;

    private sealed class Particle
    {
        public double X;
        public double Y;
        public double Vx;
        public double Vy;
        public double Life;
        public double MaxLife;
        public double Size;
        public double Rotation;
        public double RotSpeed;
        public Color Color;
        public ParticleKind Kind;
    }

    private enum ParticleKind { Spark, Confetti, Ring }

    /// <summary>
    /// Preset burst configurations for different action types.
    /// </summary>
    public enum BurstPreset
    {
        /// <summary>Expanding ripple + few sparks — for marker drops.</summary>
        MarkerDrop,
        /// <summary>Radial burst from center — for zoom/speed apply.</summary>
        ZoomApply,
        /// <summary>Full-screen confetti shower — for render complete.</summary>
        RenderComplete,
        /// <summary>Small pop — for toggles and button presses.</summary>
        TogglePop
    }

    public ParticleBurstCanvas()
    {
        IsHitTestVisible = false;
        ClipToBounds = false;
    }

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
        _animating = false;
        _particles.Clear();
        _clock.Stop();
    }

    /// <summary>
    /// Emit a burst at the given anchor point (in local control coordinates) with the specified preset.
    /// </summary>
    public void Burst(Point anchor, BurstPreset preset)
    {
        switch (preset)
        {
            case BurstPreset.MarkerDrop:
                // TONE_01: the marker burst is the app's success colour, so it must follow the
                // token. The lighter spark tint is derived from it rather than being a second
                // literal that could drift out of step with the ring.
                var okC = TokenColour("AppSuccessColor", Color.FromRgb(63, 156, 107));
                EmitRing(anchor, Color.FromArgb(180, okC.R, okC.G, okC.B), maxRadius: 60, ringCount: 2);
                EmitSparks(anchor, Lighten(okC, 220, 45), sparkCount: 8, speedMin: 40, speedMax: 120);
                break;
            case BurstPreset.ZoomApply:
                EmitSparks(anchor, Color.FromArgb(220, 217, 70, 239), sparkCount: 16, speedMin: 60, speedMax: 200);
                EmitRing(anchor, Color.FromArgb(160, 30, 64, 175), maxRadius: 80, ringCount: 1);
                break;
            case BurstPreset.RenderComplete:
                EmitConfetti(anchor, count: 50);
                break;
            case BurstPreset.TogglePop:
                EmitRing(anchor, Color.FromArgb(140, 96, 165, 250), maxRadius: 30, ringCount: 1);
                EmitSparks(anchor, Color.FromArgb(200, 147, 197, 253), sparkCount: 5, speedMin: 20, speedMax: 60);
                break;
        }

        if (!_animating)
        {
            _animating = true;
            _clock.Restart();
            _lastFrameSeconds = 0;
            _accumulator = 0;
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnAnimationFrame);
        }
    }

    private void EmitRing(Point center, Color color, double maxRadius, int ringCount)
    {
        for (int i = 0; i < ringCount; i++)
        {
            var p = new Particle
            {
                X = center.X,
                Y = center.Y,
                Vx = 0,
                Vy = 0,
                Life = 0,
                MaxLife = 0.5 + i * 0.15,
                Size = maxRadius,
                Rotation = 0,
                RotSpeed = 0,
                Color = color,
                Kind = ParticleKind.Ring
            };
            if (_particles.Count < MaxParticles) _particles.Add(p);
        }
    }

    private void EmitSparks(Point center, Color color, int sparkCount, double speedMin, double speedMax)
    {
        for (int i = 0; i < sparkCount; i++)
        {
            double angle = _rng.NextDouble() * Math.PI * 2;
            double speed = speedMin + _rng.NextDouble() * (speedMax - speedMin);
            var p = new Particle
            {
                X = center.X,
                Y = center.Y,
                Vx = Math.Cos(angle) * speed,
                Vy = Math.Sin(angle) * speed,
                Life = 0,
                MaxLife = 0.4 + _rng.NextDouble() * 0.5,
                Size = 2 + _rng.NextDouble() * 4,
                Rotation = _rng.NextDouble() * Math.PI * 2,
                RotSpeed = (_rng.NextDouble() - 0.5) * 10,
                Color = color,
                Kind = ParticleKind.Spark
            };
            if (_particles.Count < MaxParticles) _particles.Add(p);
        }
    }

    /// <summary>TONE_01 — a theme colour for the burst palette, with a safe fallback.</summary>
    private Color TokenColour(string key, Color fallback)
        => Infrastructure.ThemeResources.Colour(this, key, fallback);

    /// <summary>TONE_01 — the same, pre-multiplied with a fixed alpha.</summary>
    private Color TokenColourA(string key, Color fallback, byte alpha)
    {
        var c = TokenColour(key, fallback);
        return Color.FromArgb(alpha, c.R, c.G, c.B);
    }

    /// <summary>TONE_01 — a lighter tint of a token colour, so sparks track their ring.</summary>
    private static Color Lighten(Color c, byte alpha, int by)
        => Color.FromArgb(alpha,
                          (byte)Math.Min(255, c.R + by),
                          (byte)Math.Min(255, c.G + by),
                          (byte)Math.Min(255, c.B + by));

    private void EmitConfetti(Point center, int count)
    {
        var colors = new[]
        {
            TokenColourA("AppSuccessColor", Color.FromRgb(63, 156, 107), 230),   // TONE_01
            Color.FromArgb(230, 59, 130, 246),
            Color.FromArgb(230, 250, 204, 21),
            TokenColourA("AppDangerColor", Color.FromRgb(168, 50, 50), 230),     // TONE_01
            Color.FromArgb(230, 168, 85, 247),
            Color.FromArgb(230, 236, 72, 153),
        };

        for (int i = 0; i < count; i++)
        {
            double angle = -Math.PI / 2 + (_rng.NextDouble() - 0.5) * Math.PI;
            double speed = 150 + _rng.NextDouble() * 350;
            var p = new Particle
            {
                X = center.X + (_rng.NextDouble() - 0.5) * 200,
                Y = center.Y,
                Vx = Math.Cos(angle) * speed,
                Vy = Math.Sin(angle) * speed,
                Life = 0,
                MaxLife = 1.0 + _rng.NextDouble() * 1.0,
                Size = 4 + _rng.NextDouble() * 6,
                Rotation = _rng.NextDouble() * Math.PI * 2,
                RotSpeed = (_rng.NextDouble() - 0.5) * 15,
                Color = colors[_rng.Next(colors.Length)],
                Kind = ParticleKind.Confetti
            };
            if (_particles.Count < MaxParticles) _particles.Add(p);
        }
    }

    private void OnAnimationFrame(TimeSpan _)
    {
        if (!_running) return;

        double now = _clock.Elapsed.TotalSeconds;
        double frameDelta = Math.Min(now - _lastFrameSeconds, 0.25);
        _lastFrameSeconds = now;
        _accumulator += frameDelta;

        while (_accumulator >= FixedDeltaSeconds)
        {
            StepPhysics(FixedDeltaSeconds);
            _accumulator -= FixedDeltaSeconds;
        }

        if (IsEffectivelyVisible && _particles.Count > 0)
        {
            InvalidateVisual();
        }

        if (_particles.Count == 0)
        {
            _animating = false;
            return;
        }

        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnAnimationFrame);
    }

    private void StepPhysics(double dt)
    {
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Life += dt;

            if (p.Life >= p.MaxLife)
            {
                _particles.RemoveAt(i);
                continue;
            }

            switch (p.Kind)
            {
                case ParticleKind.Spark:
                    p.X += p.Vx * dt;
                    p.Y += p.Vy * dt;
                    p.Vy += 200 * dt;
                    p.Vx *= 0.96;
                    p.Rotation += p.RotSpeed * dt;
                    break;
                case ParticleKind.Confetti:
                    p.X += p.Vx * dt;
                    p.Y += p.Vy * dt;
                    p.Vy += 400 * dt;
                    p.Vx *= 0.99;
                    p.Rotation += p.RotSpeed * dt;
                    break;
                case ParticleKind.Ring:
                    break;
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        foreach (var p in _particles)
        {
            double t = p.Life / p.MaxLife;
            double alpha = 1.0 - t;

            switch (p.Kind)
            {
                case ParticleKind.Spark:
                {
                    byte a = (byte)(alpha * p.Color.A);
                    var color = Color.FromArgb(a, p.Color.R, p.Color.G, p.Color.B);
                    context.DrawEllipse(new SolidColorBrush(color), null, new Point(p.X, p.Y), p.Size, p.Size);
                    break;
                }
                case ParticleKind.Confetti:
                {
                    byte a = (byte)(alpha * p.Color.A);
                    var color = Color.FromArgb(a, p.Color.R, p.Color.G, p.Color.B);
                    var brush = new ImmutableSolidColorBrush(color);
                    var rect = new RoundedRect(new Rect(-p.Size, -p.Size * 0.5, p.Size * 2, p.Size), 1);
                    using (context.PushTransform(Matrix.CreateTranslation(p.X, p.Y)))
                    using (context.PushTransform(Matrix.CreateRotation(p.Rotation)))
                    {
                        context.DrawRectangle(brush, null, rect);
                    }
                    break;
                }
                case ParticleKind.Ring:
                {
                    double radius = p.Size * (0.2 + t * 0.8);
                    byte a = (byte)(alpha * p.Color.A * 0.6);
                    var color = Color.FromArgb(a, p.Color.R, p.Color.G, p.Color.B);
                    var pen = new ImmutablePen(new ImmutableSolidColorBrush(color), thickness: 2 * (1.0 - t * 0.5));
                    context.DrawEllipse(null, pen, new Point(p.X, p.Y), radius, radius);
                    break;
                }
            }
        }
    }
}