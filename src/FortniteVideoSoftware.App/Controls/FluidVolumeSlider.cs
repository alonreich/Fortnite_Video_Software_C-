using System;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>
/// Custom-rendered vertical fluid volume slider — a 3D laboratory test tube filled
/// with animated liquid, bubbles and an elastic "gooey" playhead stick.
///
/// Implemented per .\Vertical_Volume_Slider.txt:
///  - Fixed 60Hz timestep accumulator: physics speed is identical on 60/144/240Hz/VRR.
///  - Strict geometry clipping of fluid + bubbles to the inner tube capsule.
///  - Fluid surface from 5 desynchronized sine/cosine waves with irrational multipliers.
///  - High-entropy bubble particle system, agitation scales with drag velocity.
///  - Elastic stick bends toward the pointer (quadratic bezier) and relaxes with a
///    damped harmonic oscillator (~1.0–1.5s), no artificial vibration.
///  - Percentage text inside the tube: above fluid when &lt;= 80, inside fluid when &gt; 80.
///  - 25/50/75 markers painted on the glass.
///
/// Derives from <see cref="Slider"/> ON PURPOSE: every existing call site keeps
/// working unchanged (FindControl&lt;Slider&gt;("VolumeSlider"), Slider.ValueProperty
/// PropertyChanged, PointerReleased persistence, mute toggle setting Value).
/// Because the style key is this concrete type, the Fluent Slider template does NOT
/// apply — the control is fully custom-drawn in <see cref="Render"/>.
///
/// AOT-safe: no reflection, no dynamic XAML.
///
/// ROLLBACK: see old_code\volume_slider_rollback\ROLLBACK_INSTRUCTIONS.txt to restore
/// the previous standard Slider without touching any other feature.
/// </summary>
public class FluidVolumeSlider : Slider
{
    // ---- Layout constants (narrow horizontal footprint) ----
    private const double TubeWidth = 20.0;
    private const double StickOverhang = 8.0;   // stick extends this much past each tube edge
    private const double VerticalPad = 6.0;

    // ---- Fixed timestep (CRITICAL: refresh-rate independence) ----
    private const double FixedDeltaSeconds = 1.0 / 60.0;
    private double _accumulator;
    private readonly Stopwatch _clock = new();
    private double _lastFrameSeconds;
    private bool _running;

    // ---- Physics state ----
    private double _currentVolume = 100;   // displayed fluid level (0..100)
    private double _targetVolume = 100;    // where the fluid wants to be
    private double _velocity;
    private double _stickBend;
    private double _bendVelocity;
    private double _releaseTimeSeconds = -10;
    private bool _isDragging;
    private double _turbulence = 0.02;
    private double _waveDecay;
    private bool _selfUpdate;
    private bool _stateInitialized;

    // ---- Bubble particle system (high-entropy randomization) ----
    private const int BubbleCount = 30;
    private readonly Bubble[] _bubbles = new Bubble[BubbleCount];
    private readonly Random _rng = new();

    private sealed class Bubble
    {
        public double X;            // 0..1 across inner tube
        public double Y;            // 0..1 from tube top
        public double Size;
        public double BaseSpeed;
        public double WobbleSpeed;
        public double WobbleAmount;
        public double Seed;
        public double Opacity;
    }

    // ---- Cached immutable drawing resources ----
    private static readonly ImmutableSolidColorBrush s_white = new(Colors.White);
    private static readonly IBrush s_glassBrush = new ImmutableLinearGradientBrush(
        new[]
        {
            new ImmutableGradientStop(0.0, Color.Parse("#020203")),
            new ImmutableGradientStop(0.5, Color.Parse("#0d0d12")),
            new ImmutableGradientStop(1.0, Color.Parse("#020203")),
        },
        startPoint: new RelativePoint(0, 0.5, RelativeUnit.Relative),
        endPoint: new RelativePoint(1, 0.5, RelativeUnit.Relative));
    private static readonly IBrush s_fluidBrush = new ImmutableLinearGradientBrush(
        new[]
        {
            new ImmutableGradientStop(0.0, Color.Parse("#001824")),
            new ImmutableGradientStop(0.5, Color.Parse("#006699")),
            new ImmutableGradientStop(1.0, Color.Parse("#001824")),
        },
        startPoint: new RelativePoint(0, 0.5, RelativeUnit.Relative),
        endPoint: new RelativePoint(1, 0.5, RelativeUnit.Relative));
    private static readonly IBrush s_innerShadowBrush = new ImmutableLinearGradientBrush(
        new[]
        {
            new ImmutableGradientStop(0.0, Color.Parse("#66000000")),
            new ImmutableGradientStop(0.18, Color.Parse("#00000000")),
            new ImmutableGradientStop(0.82, Color.Parse("#00000000")),
            new ImmutableGradientStop(1.0, Color.Parse("#66000000")),
        },
        startPoint: new RelativePoint(0.5, 0, RelativeUnit.Relative),
        endPoint: new RelativePoint(0.5, 1, RelativeUnit.Relative));
    private static readonly ImmutableSolidColorBrush s_specularLeft = new(Colors.White, 0.16);
    private static readonly ImmutableSolidColorBrush s_specularRight = new(Colors.White, 0.07);
    private static readonly ImmutablePen s_stickGlowPen = new(new ImmutableSolidColorBrush(Color.Parse("#3300ffff")), 6);
    private static readonly ImmutablePen s_stickPen = new(new ImmutableSolidColorBrush(Color.Parse("#00ffff")), 2);
    private static readonly ImmutablePen s_markerDarkPen = new(new ImmutableSolidColorBrush(Color.Parse("#59000000")), 1);
    private static readonly ImmutablePen s_markerLightPen = new(new ImmutableSolidColorBrush(Color.Parse("#2effffff")), 1);
    private static readonly ImmutableSolidColorBrush s_markerTextBrush = new(Colors.White, 0.30);
    private static readonly ImmutableSolidColorBrush s_shadowTextBrush = new(Colors.Black, 0.85);
    private static readonly Typeface s_typeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

    public FluidVolumeSlider()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        ClipToBounds = false;

        for (int i = 0; i < BubbleCount; i++)
        {
            _bubbles[i] = new Bubble();
            ResetBubble(_bubbles[i], randomY: true);
        }
    }

    // ------------------------------------------------------------------
    //  Value synchronization (external writers: mute toggle, recovery, keys)
    // ------------------------------------------------------------------
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty && !_selfUpdate)
        {
            _targetVolume = Math.Clamp(Value, 0, 100);
            if (!_stateInitialized)
            {
                _currentVolume = _targetVolume;
                _stateInitialized = true;
            }
            // External jumps (mute/unmute) agitate the fluid via the spring physics.
            _releaseTimeSeconds = NowSeconds;
        }
    }

    // ------------------------------------------------------------------
    //  Animation loop
    // ------------------------------------------------------------------
    private double NowSeconds => _clock.Elapsed.TotalSeconds;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _currentVolume = _targetVolume = Math.Clamp(Value, 0, 100);
        _stateInitialized = true;
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
        double frameDelta = Math.Min(now - _lastFrameSeconds, 0.25); // clamp pauses/hitches
        _lastFrameSeconds = now;
        _accumulator += frameDelta;

        // Fixed Timestep Accumulator: physics executes exactly 60 times per second
        // regardless of monitor refresh rate (144Hz / 240Hz / VRR safe).
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
        // Fluid level: damped spring toward the target volume.
        const double spring = 0.08;
        const double friction = 0.86;
        double accel = (_targetVolume - _currentVolume) * spring;
        _velocity += accel;
        _velocity *= friction;
        _currentVolume = Math.Clamp(_currentVolume + _velocity, 0, 100);

        // Elastic stick: bends toward the pointer while dragging, relaxes with a
        // slower damped harmonic oscillator (~1.0–1.5s, stutter-free) on release.
        double targetBend = 0;
        if (_isDragging)
        {
            targetBend = Math.Clamp((_targetVolume - _currentVolume) * -1.5, -40, 40);
        }

        double bendSpring = _isDragging ? 0.08 : 0.05;
        double bendFriction = _isDragging ? 0.86 : 0.92;
        double bendAccel = (targetBend - _stickBend) * bendSpring;
        _bendVelocity += bendAccel;
        _bendVelocity *= bendFriction;
        _stickBend += _bendVelocity;

        // Agitation: rolling boil while dragging / fast moves, simmer at rest.
        if (_isDragging || Math.Abs(_velocity) > 0.1)
        {
            _turbulence = 1.0;
            _waveDecay = 1.0;
            _releaseTimeSeconds = now;
        }
        else
        {
            double elapsed = now - _releaseTimeSeconds;
            if (elapsed < 1.5)
            {
                double progress = elapsed / 1.5;
                _turbulence = Math.Max(0.02, 1.0 - progress);
                _waveDecay = 1.0 - progress;
            }
            else
            {
                _turbulence = 0.02;
                _waveDecay = 0.12; // base simmer at rest
            }
        }

        // Bubbles (particle system).
        foreach (var b in _bubbles)
        {
            b.Y -= b.BaseSpeed * (1 + _turbulence * 3);
            b.X += Math.Sin(b.Y * 9.0 + now * b.WobbleSpeed + b.Seed) * b.WobbleAmount;
            b.X = Math.Clamp(b.X, 0.06, 0.94);

            double surfaceNorm = 1.0 - _currentVolume / 100.0;
            if (b.Y < surfaceNorm - 0.03)
            {
                ResetBubble(b, randomY: false);
            }
        }
    }

    private void ResetBubble(Bubble b, bool randomY)
    {
        double surfaceNorm = 1.0 - _currentVolume / 100.0;
        b.X = 0.1 + _rng.NextDouble() * 0.8;
        b.Y = randomY
            ? surfaceNorm + _rng.NextDouble() * Math.Max(0.001, 1.0 - surfaceNorm)
            : 1.02;
        b.Size = 0.8 + _rng.NextDouble() * 2.4;
        b.BaseSpeed = (0.05 + _rng.NextDouble() * 0.2) / 100.0;
        b.WobbleSpeed = 0.5 + _rng.NextDouble() * 2.2;
        b.WobbleAmount = (0.1 + _rng.NextDouble() * 0.6) / 220.0;
        b.Seed = _rng.NextDouble() * Math.PI * 2;
        b.Opacity = 0.25 + _rng.NextDouble() * 0.5;
    }

    // ------------------------------------------------------------------
    //  Pointer interaction
    // ------------------------------------------------------------------
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEnabled) return;
        _isDragging = true;
        e.Pointer.Capture(this);
        UpdateVolumeFromPointer(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_isDragging)
        {
            UpdateVolumeFromPointer(e.GetPosition(this).Y);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            _releaseTimeSeconds = NowSeconds;
            e.Pointer.Capture(null);
        }
        // Deliberately AFTER clearing drag state: existing code-behind subscribes to
        // PointerReleased to persist the final volume value.
        base.OnPointerReleased(e);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_isDragging)
        {
            _isDragging = false;
            _releaseTimeSeconds = NowSeconds;
        }
    }

    private void UpdateVolumeFromPointer(double y)
    {
        GetTubeMetrics(out _, out double tubeY, out _, out double tubeHeight);
        if (tubeHeight <= 0) return;
        double v = Math.Clamp(100.0 - (y - tubeY) / tubeHeight * 100.0, 0, 100);
        _targetVolume = v;
        _selfUpdate = true;
        try
        {
            SetCurrentValue(ValueProperty, Math.Round(v));
        }
        finally
        {
            _selfUpdate = false;
        }
    }

    // ------------------------------------------------------------------
    //  Rendering
    // ------------------------------------------------------------------
    private void GetTubeMetrics(out double tubeX, out double tubeY, out double tubeWidth, out double tubeHeight)
    {
        tubeWidth = TubeWidth;
        tubeX = (Bounds.Width - tubeWidth) / 2.0;
        tubeY = VerticalPad;
        tubeHeight = Math.Max(0, Bounds.Height - VerticalPad * 2);
    }

    public override void Render(DrawingContext context)
    {
        GetTubeMetrics(out double tubeX, out double tubeY, out double tubeWidth, out double tubeHeight);
        if (tubeHeight < 24 || Bounds.Width < TubeWidth) return;

        double radius = tubeWidth / 2.0;
        double centerX = tubeX + radius;

        // --- Glass tube: vertical capsule (fully rounded semi-circle caps top + bottom) ---
        var capsule = new StreamGeometry();
        using (var gc = capsule.Open())
        {
            gc.BeginFigure(new Point(tubeX, tubeY + radius), true);
            gc.ArcTo(new Point(tubeX + tubeWidth, tubeY + radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            gc.LineTo(new Point(tubeX + tubeWidth, tubeY + tubeHeight - radius));
            gc.ArcTo(new Point(tubeX, tubeY + tubeHeight - radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            gc.EndFigure(true);
        }
        context.DrawGeometry(s_glassBrush, null, capsule);

        double fluidY = tubeY + tubeHeight - _currentVolume / 100.0 * tubeHeight;
        double now = NowSeconds;

        // --- Fluid + bubbles, strictly clipped to the tube capsule ---
        using (context.PushGeometryClip(capsule))
        {
            // Fluid body with composite wave surface (5 desynchronized waves,
            // irrational frequency multipliers so the pattern never repeats).
            double dynamicAmp = (1 + Math.Abs(_velocity) * 0.4) * Math.Max(_waveDecay, 0.0);
            var fluid = new StreamGeometry();
            using (var gc = fluid.Open())
            {
                gc.BeginFigure(new Point(tubeX, SurfaceY(tubeX)), true);
                for (double x = tubeX + 1; x <= tubeX + tubeWidth; x += 1.0)
                {
                    gc.LineTo(new Point(x, SurfaceY(x)));
                }
                gc.LineTo(new Point(tubeX + tubeWidth, tubeY + tubeHeight));
                gc.LineTo(new Point(tubeX, tubeY + tubeHeight));
                gc.EndFigure(true);
            }
            context.DrawGeometry(s_fluidBrush, null, fluid);

            double SurfaceY(double x)
            {
                double nx = (x - tubeX) / tubeWidth;
                double off =
                    Math.Sin(now * 5.00 + nx * 5.0) * 1.9 +
                    Math.Cos(now * 3.00 * Math.Sqrt(2) + nx * 12.0) * 1.1 +
                    Math.Sin(now * 7.30 * 1.6180339887 + nx * 8.0) * 0.7 +
                    Math.Cos(now * 2.10 * Math.PI + nx * 17.0) * 0.5 +
                    Math.Sin(now * 9.70 * Math.Sqrt(3) + nx * 3.0) * 0.4;
                return fluidY + off * dynamicAmp;
            }

            // Bubbles.
            foreach (var b in _bubbles)
            {
                double bx = tubeX + b.X * tubeWidth;
                double by = tubeY + b.Y * tubeHeight;
                if (by < fluidY - 2) continue; // never render above the fluid surface
                double opacity = b.Opacity * (0.5 + _turbulence * 0.5);
                var brush = new ImmutableSolidColorBrush(Colors.White, opacity);
                context.DrawEllipse(brush, null, new Point(bx, by), b.Size, b.Size);
                // tiny highlight for a 3D look
                var hi = new ImmutableSolidColorBrush(Colors.White, Math.Min(1.0, opacity + 0.25));
                context.DrawEllipse(hi, null, new Point(bx - b.Size * 0.25, by - b.Size * 0.25), b.Size * 0.3, b.Size * 0.3);
            }

            // Inner shadow (deep multi-stop shading top/bottom of the glass).
            context.DrawGeometry(s_innerShadowBrush, null, capsule);

            // Specular highlights along the curved left/right edges.
            context.DrawRectangle(s_specularLeft, null, new RoundedRect(new Rect(tubeX + 1.5, tubeY + radius * 0.6, 2.4, tubeHeight - radius * 1.2), 1.2));
            context.DrawRectangle(s_specularRight, null, new RoundedRect(new Rect(tubeX + tubeWidth - 3.6, tubeY + radius * 0.8, 1.8, tubeHeight - radius * 1.6), 0.9));

            // 25 / 50 / 75 markers painted on the glass (3D: dark line + light line).
            for (int m = 25; m <= 75; m += 25)
            {
                double my = tubeY + tubeHeight - m / 100.0 * tubeHeight;
                context.DrawLine(s_markerDarkPen, new Point(tubeX + 3, my + 1), new Point(tubeX + tubeWidth - 3, my + 1));
                context.DrawLine(s_markerLightPen, new Point(tubeX + 3, my), new Point(tubeX + tubeWidth - 3, my));
                var markerText = new FormattedText(
                    m.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, s_typeface, 6.5, s_markerTextBrush);
                context.DrawText(markerText, new Point(tubeX + tubeWidth - markerText.Width - 3.5, my - markerText.Height - 0.5));
            }
        }

        // --- Elastic playhead stick (drawn OUTSIDE the clip so it overhangs the tube) ---
        double stickHalf = radius + StickOverhang;
        var stick = new StreamGeometry();
        using (var gc = stick.Open())
        {
            gc.BeginFigure(new Point(centerX - stickHalf, fluidY), false);
            gc.QuadraticBezierTo(new Point(centerX, fluidY + _stickBend), new Point(centerX + stickHalf, fluidY));
            gc.EndFigure(false);
        }
        context.DrawGeometry(null, s_stickGlowPen, stick);
        context.DrawGeometry(null, s_stickPen, stick);

        // --- Percentage text inside the tube (white + heavy dark drop shadow) ---
        string label = ((int)Math.Round(_targetVolume)).ToString(CultureInfo.InvariantCulture) + "%";
        var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, s_typeface, 10, s_white);
        double textY = _targetVolume <= 80
            ? fluidY - ft.Height - 8          // empty void above the fluid
            : fluidY + 10;                    // pushed into the fluid below the surface
        textY = Math.Clamp(textY, tubeY + 2, tubeY + tubeHeight - ft.Height - 2);
        double textX = centerX - ft.Width / 2.0;

        var shadow = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, s_typeface, 10, s_shadowTextBrush);
        context.DrawText(shadow, new Point(textX + 1, textY + 1));
        context.DrawText(shadow, new Point(textX - 1, textY + 1));
        context.DrawText(shadow, new Point(textX + 1, textY - 1));
        context.DrawText(shadow, new Point(textX - 1, textY - 1));
        context.DrawText(ft, new Point(textX, textY));
    }
}
