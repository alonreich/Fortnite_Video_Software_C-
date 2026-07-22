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
    private const double TubeWidth = 20.0;
    private const double StickOverhang = 8.0;
    private const double VerticalPad = 6.0;

    private const double FixedDeltaSeconds = 1.0 / 60.0;
    private double _accumulator;
    private readonly Stopwatch _clock = new();
    private double _lastFrameSeconds;
    private bool _running;
    private Avalonia.PixelPoint _lastWindowPos;
    private double _sloshTilt;
    private double _sloshWave;
    private double _simulatedPeak;

    private double _currentVolume = 100;
    private double _targetVolume = 100;
    private double _velocity;
    private double _stickBend;
    private double _bendVelocity;
    private double _ghostBend;
    private double _ghostBendVelocity;
    private double _releaseTimeSeconds = -10;
    private bool _isDragging;
    private double _turbulence = 0.02;
    private double _waveDecay;
    private bool _selfUpdate;
    private bool _stateInitialized;

    private const int BubbleCount = 30;
    private readonly Bubble[] _bubbles = new Bubble[BubbleCount];
    private readonly Random _rng = new();

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

    private static readonly ImmutableSolidColorBrush s_white = new(Colors.White);
    

    private static readonly IBrush s_innerShadowBrush = new ImmutableLinearGradientBrush(
        new[]
        {
            new ImmutableGradientStop(0.0, Color.Parse("#a6000000")),
            new ImmutableGradientStop(0.18, Color.Parse("#00000000")),
            new ImmutableGradientStop(0.82, Color.Parse("#00000000")),
            new ImmutableGradientStop(1.0, Color.Parse("#a6000000")),
        },
        startPoint: new RelativePoint(0.5, 0, RelativeUnit.Relative),
        endPoint: new RelativePoint(0.5, 1, RelativeUnit.Relative));
    private static readonly ImmutableSolidColorBrush s_specularLeft = new(Colors.White, 0.16);
    private static readonly ImmutableSolidColorBrush s_specularRight = new(Colors.White, 0.07);
    private static readonly ImmutablePen s_markerDarkPen = new(new ImmutableSolidColorBrush(Color.Parse("#59000000")), 1);
    private static readonly ImmutablePen s_markerLightPen = new(new ImmutableSolidColorBrush(Color.Parse("#2effffff")), 1);
    private static readonly ImmutableSolidColorBrush s_markerTextBrush = new(Colors.White, 0.30);
    private static readonly ImmutableSolidColorBrush s_shadowTextBrush = new(Colors.Black, 0.85);
    private static readonly Typeface s_typeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

    protected override Type StyleKeyOverride => typeof(FluidVolumeSlider);

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
            _releaseTimeSeconds = NowSeconds;
        }
    }

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
        var top = TopLevel.GetTopLevel(this);
        if (top != null)
        {
            if (top is Window w)
            {
                _lastWindowPos = w.Position;
            }
            top.RequestAnimationFrame(OnAnimationFrame);
        }
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
        var top = TopLevel.GetTopLevel(this);
        if (top is Window win)
        {
            var curPos = win.Position;
            double dx = curPos.X - _lastWindowPos.X;
            _lastWindowPos = curPos;
            
            _sloshTilt += (-dx * 2.5 - _sloshTilt) * 0.12;
            
            _sloshWave += (dx * 1.5 - _sloshWave) * 0.10;
        }
        _sloshTilt *= 0.90;
        _sloshWave *= 0.88;

        if (_targetVolume > 0)
        {
            double fakeAudio = Math.Max(0, Math.Sin(now * 15.3) * Math.Sin(now * 7.7) * Math.Cos(now * 2.1));
            fakeAudio = fakeAudio * fakeAudio * (_targetVolume / 100.0);
            _simulatedPeak += (fakeAudio * 35.0 - _simulatedPeak) * 0.3;
        }
        else
        {
            _simulatedPeak *= 0.8;
        }

        const double spring = 0.08;
        const double friction = 0.86;
        double accel = (_targetVolume - _currentVolume) * spring;
        _velocity += accel;
        _velocity *= friction;
        _currentVolume = Math.Clamp(_currentVolume + _velocity, 0, 100);

        double targetBend = 0;
        if (_isDragging)
        {
            targetBend = Math.Clamp((_targetVolume - _currentVolume) * -2.5, -60, 60);
        }

        double bendSpring = _isDragging ? 0.04 : 0.025;
        double bendFriction = _isDragging ? 0.90 : 0.96;
        double bendAccel = (targetBend - _stickBend) * bendSpring;
        _bendVelocity += bendAccel;
        _bendVelocity *= bendFriction;
        _stickBend += _bendVelocity;

        double ghostAccel = (_stickBend - _ghostBend) * 0.08;
        _ghostBendVelocity += ghostAccel;
        _ghostBendVelocity *= 0.85;
        _ghostBend += _ghostBendVelocity;

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
                _waveDecay = 0.12;
            }
        }

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
        b.Size = 1.4 + _rng.NextDouble() * 3.6;
        b.BaseSpeed = (0.05 + _rng.NextDouble() * 0.2) / 100.0;
        b.WobbleSpeed = 0.5 + _rng.NextDouble() * 2.2;
        b.WobbleAmount = (0.1 + _rng.NextDouble() * 0.6) / 220.0;
        b.Seed = _rng.NextDouble() * Math.PI * 2;
        b.Opacity = 0.25 + _rng.NextDouble() * 0.5;
    }

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

    private void GetTubeMetrics(out double tubeX, out double tubeY, out double tubeWidth, out double tubeHeight)
    {
        tubeWidth = Math.Max(18.0, (Bounds.Width - StickOverhang * 2 - 4.0) * 0.9); 
        tubeX = (Bounds.Width - tubeWidth) / 2.0;
        tubeY = VerticalPad;
        tubeHeight = Math.Max(0, Bounds.Height - VerticalPad * 2);
    }

    private StreamGeometry? _cachedCapsule;
    private double _lastTubeX = -1, _lastTubeY = -1, _lastTubeWidth = -1, _lastTubeHeight = -1;

    public override void Render(DrawingContext context)
    {
        GetTubeMetrics(out double tubeX, out double tubeY, out double tubeWidth, out double tubeHeight);
        if (tubeHeight < 24 || Bounds.Width < TubeWidth) return;

        double radius = tubeWidth / 2.0;
        double centerX = tubeX + radius;

        if (_cachedCapsule == null || _lastTubeX != tubeX || _lastTubeY != tubeY || _lastTubeWidth != tubeWidth || _lastTubeHeight != tubeHeight)
        {
            _cachedCapsule = new StreamGeometry();
            using (var gc = _cachedCapsule.Open())
            {
                gc.BeginFigure(new Point(tubeX, tubeY + radius), true);
                gc.ArcTo(new Point(tubeX + tubeWidth, tubeY + radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
                gc.LineTo(new Point(tubeX + tubeWidth, tubeY + tubeHeight - radius));
                gc.ArcTo(new Point(tubeX, tubeY + tubeHeight - radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
                gc.EndFigure(true);
            }
            _lastTubeX = tubeX; _lastTubeY = tubeY; _lastTubeWidth = tubeWidth; _lastTubeHeight = tubeHeight;
        }
        var capsule = _cachedCapsule;

        GetThermalColors(_currentVolume, out Color centerColor, out Color edgeColor);

        var fluidBrush = new ImmutableRadialGradientBrush(
            new[] {
                new ImmutableGradientStop(0.0, centerColor),
                new ImmutableGradientStop(1.0, edgeColor),
            },
            center: new RelativePoint(0.5, 0.7, RelativeUnit.Relative),
            gradientOrigin: new RelativePoint(0.5, 0.7, RelativeUnit.Relative),
            radius: 1.0);

        Color glassCenter = LerpColor(Color.Parse("#0d0d12"), centerColor, 0.15);
        var glassBrush = new ImmutableLinearGradientBrush(
            new[] {
                new ImmutableGradientStop(0.0, Color.Parse("#000000")),
                new ImmutableGradientStop(0.5, glassCenter),
                new ImmutableGradientStop(1.0, Color.Parse("#000000")),
            },
            startPoint: new RelativePoint(0, 0.5, RelativeUnit.Relative),
            endPoint: new RelativePoint(1, 0.5, RelativeUnit.Relative));

        context.DrawGeometry(glassBrush, null, capsule);

        double fluidY = tubeY + tubeHeight - _currentVolume / 100.0 * tubeHeight;
        double now = NowSeconds;

        using (context.PushGeometryClip(capsule))
        {
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
            context.DrawGeometry(fluidBrush, null, fluid);

            double SurfaceY(double x)
            {
                double nx = (x - tubeX) / tubeWidth;
                double centerOffset = nx - 0.5;
                double off =
                    Math.Sin(now * 5.00 + nx * 5.0) * 1.9 +
                    Math.Cos(now * 3.00 * Math.Sqrt(2) + nx * 12.0) * 1.1 +
                    Math.Sin(now * 7.30 * 1.6180339887 + nx * 8.0) * 0.7 +
                    Math.Cos(now * 2.10 * Math.PI + nx * 17.0) * 0.5 +
                    Math.Sin(now * 9.70 * Math.Sqrt(3) + nx * 3.0) * 0.4;
                
                double totalAmp = dynamicAmp + _simulatedPeak;
                double sloshCurve = centerOffset * centerOffset * _sloshWave * Math.Sign(centerOffset);
                return fluidY + (off * totalAmp) + (centerOffset * _sloshTilt) + sloshCurve;
            }

            double meniscusHeight = radius * 0.5;
            var meniscusRect = new Rect(tubeX + 1.5, fluidY - meniscusHeight / 2.0, tubeWidth - 3, meniscusHeight);
            var meniscusStroke = new ImmutableSolidColorBrush(Color.FromArgb(160, centerColor.R, centerColor.G, centerColor.B));
            var meniscusFill = new ImmutableSolidColorBrush(Color.FromArgb(80, centerColor.R, centerColor.G, centerColor.B));
            context.DrawEllipse(meniscusFill, new ImmutablePen(meniscusStroke, 1.2), meniscusRect.Center, meniscusRect.Width / 2.0, meniscusRect.Height / 2.0);

            foreach (var b in _bubbles)
            {
                double bx = tubeX + b.X * tubeWidth;
                double by = tubeY + b.Y * tubeHeight;
                if (by < fluidY + b.Size + 2) continue;
                double opacity = b.Opacity * (0.5 + _turbulence * 0.5);
                
                var bubbleStroke = new ImmutableSolidColorBrush(Color.FromArgb((byte)(opacity * 255), 255, 255, 255));
                context.DrawEllipse(null, new ImmutablePen(bubbleStroke, 0.8), new Point(bx, by), b.Size, b.Size);
                
                var hi = new ImmutableSolidColorBrush(Color.FromArgb((byte)(Math.Min(1.0, opacity + 0.4) * 255), 255, 255, 255));
                context.DrawEllipse(hi, null, new Point(bx - b.Size * 0.3, by - b.Size * 0.3), b.Size * 0.35, b.Size * 0.35);
            }

            var baseGlassRect = new Rect(tubeX + 2, tubeY + tubeHeight - radius + 1, tubeWidth - 4, radius - 2);
            context.DrawEllipse(new ImmutableSolidColorBrush(Color.FromArgb(200, 0, 0, 0)), null, baseGlassRect.Center, baseGlassRect.Width / 2.0, baseGlassRect.Height / 2.0);

            context.DrawGeometry(s_innerShadowBrush, null, capsule);

            var leftGlare = new ImmutableLinearGradientBrush(
                new[] {
                    new ImmutableGradientStop(0.0, Color.FromArgb(240, 255, 255, 255)),
                    new ImmutableGradientStop(0.3, Color.FromArgb(120, 255, 255, 255)),
                    new ImmutableGradientStop(1.0, Color.FromArgb(0, 255, 255, 255))
                },
                startPoint: new RelativePoint(0, 0, RelativeUnit.Relative),
                endPoint: new RelativePoint(1, 0, RelativeUnit.Relative));
            context.DrawRectangle(leftGlare, null, new RoundedRect(new Rect(tubeX + 1.5, tubeY + radius * 0.6, 3.5, tubeHeight - radius * 1.2), 1.2));

            var rightGlare = new ImmutableLinearGradientBrush(
                new[] {
                    new ImmutableGradientStop(0.0, Color.FromArgb(0, 255, 255, 255)),
                    new ImmutableGradientStop(0.7, Color.FromArgb(20, 255, 255, 255)),
                    new ImmutableGradientStop(1.0, Color.FromArgb(120, 255, 255, 255))
                },
                startPoint: new RelativePoint(0, 0, RelativeUnit.Relative),
                endPoint: new RelativePoint(1, 0, RelativeUnit.Relative));
            context.DrawRectangle(rightGlare, null, new RoundedRect(new Rect(tubeX + tubeWidth - 3.6, tubeY + radius * 0.8, 2.0, tubeHeight - radius * 1.6), 0.9));

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

        double stickHalf = radius + StickOverhang;
        var stick = new StreamGeometry();
        using (var gc = stick.Open())
        {
            gc.BeginFigure(new Point(centerX - stickHalf, fluidY), false);
            gc.QuadraticBezierTo(new Point(centerX, fluidY + _stickBend), new Point(centerX + stickHalf, fluidY));
            gc.EndFigure(false);
        }

        var ghostStick = new StreamGeometry();
        using (var gc = ghostStick.Open())
        {
            gc.BeginFigure(new Point(centerX - stickHalf, fluidY), false);
            gc.QuadraticBezierTo(new Point(centerX, fluidY + _ghostBend), new Point(centerX + stickHalf, fluidY));
            gc.EndFigure(false);
        }

        var stickGlowPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(51, centerColor.R, centerColor.G, centerColor.B)), 6);
        var stickPen = new ImmutablePen(new ImmutableSolidColorBrush(centerColor), 2);
        var ghostPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(76, centerColor.R, centerColor.G, centerColor.B)), 1.5);

        context.DrawGeometry(null, ghostPen, ghostStick);
        context.DrawGeometry(null, stickGlowPen, stick);
        context.DrawGeometry(null, stickPen, stick);


        string label = ((int)Math.Round(_targetVolume)).ToString(CultureInfo.InvariantCulture) + "%";
        var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, s_typeface, 10, s_white);
        double textY = _targetVolume <= 80
            ? fluidY - ft.Height - 8
            : fluidY + 10;
        textY = Math.Clamp(textY, tubeY + 2, tubeY + tubeHeight - ft.Height - 2);
        double textX = centerX - ft.Width / 2.0;

        var shadow = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, s_typeface, 10, s_shadowTextBrush);
        context.DrawText(shadow, new Point(textX + 1, textY + 1));
        context.DrawText(shadow, new Point(textX - 1, textY + 1));
        context.DrawText(shadow, new Point(textX + 1, textY - 1));
        context.DrawText(shadow, new Point(textX - 1, textY - 1));
        context.DrawText(ft, new Point(textX, textY));
    }

    private static void GetThermalColors(double volume, out Color center, out Color edge)
    {
        double t = volume / 100.0;
        center = LerpColor(Color.Parse("#00D2FF"), Color.Parse("#8A2BE2"), t);
        edge = LerpColor(Color.Parse("#002855"), Color.Parse("#2B0055"), t);
    }

    private static Color LerpColor(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }
}
