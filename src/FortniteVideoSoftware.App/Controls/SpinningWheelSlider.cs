using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.App.Controls
{
    public class SpinningWheelSlider : Control
    {
        public static readonly DirectProperty<SpinningWheelSlider, int> ValueProperty =
            AvaloniaProperty.RegisterDirect<SpinningWheelSlider, int>(
                nameof(Value),
                o => o.Value,
                (o, v) => o.Value = v,
                defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

        private int _value = 0;
        private (int min, int max) _range = (0, 20);
        private List<string> _labels = new List<string>();
        private double _rotation = 0.0;

        private object? _savedToolTip = null;
        private bool _isTooltipSuppressed = false;
        private Avalonia.Threading.DispatcherTimer? _tooltipRestoreTimer;

        public event EventHandler<int>? ValueChanged;
        public event EventHandler<int>? ValueChangeCompleted;

        // ══════════════════════════════════════════════════════════════════════════════════════
        // WHEEL_01 — THE DIAL LANDS ON A DETENT INSTEAD OF FLOATING BETWEEN TWO.
        //
        // WHAT WAS WRONG. `_rotation` (the visual angle) and `_value` (the committed integer) are
        // two different quantities, and on release nothing reconciled them. `OnPointerReleased`
        // called `Value = round(_rotation)`, but the `Value` setter is guarded by
        // `if (_value != clamped)` — and `_rotation`'s own setter had ALREADY written that same
        // integer straight into the `_value` FIELD during the drag. So the guard was false, the
        // setter returned early, and `_rotation` was never pulled onto the detent: the wheel came
        // to rest at 7.43 while the readout said 7. That is the "floats in between" the user sees,
        // and it is why the dial never felt like it clicked into anything.
        //
        // Three things are fixed here, and they are independent — do not collapse them:
        //   (1) LANDING. Release always hands `_rotation` to a damped spring aimed at the nearest
        //       detent, so the dial physically arrives on the value it reports.
        //   (2) FEEL. The spring is UNDERDAMPED on purpose (see SettleZeta) so it overshoots,
        //       returns, overshoots smaller, and stops — the two short wiggles that read as a
        //       mechanical detent catching. A critically damped spring lands correctly and feels
        //       dead; that was tried and rejected.
        //   (3) GAIN. Drag distance is no longer a fixed number of units per pixel. A pointer-
        //       acceleration curve gives fine control when moved gently and long travel when
        //       flicked, and a flick keeps spinning under friction after release.
        //
        // `_value` is now written ONLY through `SetAndRaise`, so the TwoWay bindings in
        // MainWindow/VideoMergerWindow finally track the dial. They previously never saw an
        // intermediate value because the field was mutated behind the property system's back.
        // ══════════════════════════════════════════════════════════════════════════════════════

        private enum WheelPhase { Idle, Dragging, Momentum, Settling }
        private WheelPhase _phase = WheelPhase.Idle;

        /// <summary>Dial speed in INDEX UNITS PER SECOND — never pixels. One unit = one detent.</summary>
        private double _velocity;
        private double _settleTarget;

        /// <summary>While the spring runs, the reported value is pinned to the detent being
        /// approached, so an overshoot cannot flicker the readout to the neighbouring tick.</summary>
        private bool _valueLocked;

        /// <summary>An interaction is in flight; <see cref="ValueChangeCompleted"/> fires once,
        /// when the dial actually stops moving.</summary>
        private bool _interactionPending;

        private double _lastMouseX;
        private long _lastMoveStamp;
        private Avalonia.Threading.DispatcherTimer? _animTimer;
        private long _lastFrameStamp;

        // ── drag gain ────────────────────────────────────────────────────────────────────────
        // Units of dial travel per pixel of pointer travel, interpolated by pointer SPEED. The
        // curve is the same idea as OS pointer acceleration: at a slow, deliberate drag the dial
        // moves at the fine rate so a single detent is easy to hit; at speed it approaches the
        // coarse rate so the far end of a 21-stop range is reachable without clutching.
        // GainRefSpeedPxPerSec is where the curve is roughly halfway; the exponent below 1 makes
        // it bite early, which is what makes the control feel responsive rather than sluggish.
        private const double FineGainPerPx = 0.0085;
        private const double CoarseGainPerPx = 0.0320;
        private const double GainRefSpeedPxPerSec = 1500.0;
        private const double GainCurveExponent = 0.85;

        // ── fling ────────────────────────────────────────────────────────────────────────────
        private const double FlickMinVelocity = 1.2;
        private const double MomentumFriction = 5.5;
        private const double MomentumHandoffSpeed = 0.75;
        private const double MaxFlingVelocity = 26.0;

        // ── detent spring ────────────────────────────────────────────────────────────────────
        // Damped harmonic oscillator. ζ = 0.30 gives a first overshoot of e^(-ζπ/√(1-ζ²)) ≈ 0.37
        // of the remaining error and decays 0.14x per cycle after that: from a worst-case 0.5-unit
        // approach that is a 0.19-unit wiggle, then 0.026, then nothing — visibly TWO bounces, both
        // well inside half a detent so the readout never flickers. ω = 42 rad/s puts the whole
        // landing at ~0.3s. RAISING ζ TOWARD 1 KILLS THE WIGGLE; LOWERING IT PAST ~0.2 makes the
        // dial ring like a loose knob. Both were tried.
        private const double SettleOmega = 42.0;
        private const double SettleZeta = 0.30;
        // Rest thresholds. 0.004 units is ~0.3 SCREEN px on a 160px dial, so cutting the tail here
        // removes ~90ms of motion nobody can see and lands the whole gesture in ~0.30s. Do not
        // raise them much further: past ~0.01 the dial visibly stops short of the detent, which is
        // the exact defect this class of change exists to remove.
        private const double SettleRestPosition = 0.004;
        private const double SettleRestVelocity = 0.12;

        /// <summary>The spring is integrated semi-implicitly at a fixed sub-step. A dropped UI
        /// frame would otherwise hand the integrator a dt large enough to go unstable and fling
        /// the dial off its range.</summary>
        private const double MaxIntegrationStep = 1.0 / 240.0;

        private const double EdgeOverscroll = 0.28;
        private const double EdgeBounceDamping = 0.35;

        public SpinningWheelSlider()
        {
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        private static long Stamp() => System.Diagnostics.Stopwatch.GetTimestamp();

        private static double SecondsSince(long stamp)
            => (Stamp() - stamp) / (double)System.Diagnostics.Stopwatch.Frequency;

        /// <summary>
        /// Issue #3: Temporarily suppresses the tooltip to prevent it from popping up
        /// destructively over the spinner while the user is dragging/scrolling.
        /// The tooltip is restored after the user stops interacting (with a short delay).
        /// WHEEL_01: this timer no longer raises ValueChangeCompleted. "The user stopped touching
        /// it" and "the dial stopped moving" are different moments, and the second one is the one
        /// callers act on; it is raised from the spring instead.
        /// </summary>
        private void SuppressTooltipTemporarily()
        {
            if (!_isTooltipSuppressed)
            {
                _savedToolTip = ToolTip.GetTip(this);
                ToolTip.SetTip(this, null);
                _isTooltipSuppressed = true;
            }

            if (_tooltipRestoreTimer == null)
            {
                _tooltipRestoreTimer = new Avalonia.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1200)
                };
                _tooltipRestoreTimer.Tick += (s, e) =>
                {
                    _tooltipRestoreTimer!.Stop();
                    if (_isTooltipSuppressed)
                    {
                        ToolTip.SetTip(this, _savedToolTip);
                        _isTooltipSuppressed = false;
                    }
                };
            }

            _tooltipRestoreTimer.Stop();
            _tooltipRestoreTimer.Start();
        }

        public int Value
        {
            get => _value;
            set
            {
                int clamped = Math.Max(_range.min, Math.Min(_range.max, value));

                // WHEEL_01 — ECHO GUARD. The TwoWay binding writes the dial's own value back into
                // this setter. Without this line that echo would call StopAnimation() and snap
                // `_rotation` onto an integer on every detent crossed, killing the drag and the
                // spring outright. A genuine external change still gets through.
                if (_phase != WheelPhase.Idle && clamped == _value) return;

                // WHEEL_01 — a PROGRAMMATIC set (preset button, crash-recovery restore, binding)
                // snaps instantly and silently: no spring, no ValueChangeCompleted. Startup restore
                // must not animate 21 stops, and must not look like a user edit to the callers that
                // re-probe on completion.
                StopAnimation();
                _valueLocked = false;

                bool changed = _value != clamped;
                if (changed) SetAndRaise(ValueProperty, ref _value, clamped);

                if (Math.Abs(_rotation - clamped) > 0.0005)
                {
                    _rotation = clamped;
                    InvalidateVisual();
                }

                if (changed) ValueChanged?.Invoke(this, clamped);
            }
        }

        public double Rotation
        {
            get => _rotation;
            set
            {
                StopAnimation();
                SetRotationInternal(value);
            }
        }

        /// <summary>
        /// WHEEL_01 — the ONE writer of <see cref="_rotation"/>. Derives the reported value from
        /// the angle unless the spring has pinned it, and pushes every change through
        /// <see cref="SetAndRaise"/> so the property system (and therefore the TwoWay binding) sees
        /// it. The old code assigned the `_value` field directly here, which is precisely why the
        /// bindings were dead and why the release guard mis-fired.
        /// </summary>
        private void SetRotationInternal(double value)
        {
            double clamped = ClampRotation(value);
            if (Math.Abs(_rotation - clamped) < 0.0005) return;

            _rotation = clamped;

            int newVal = _valueLocked
                ? (int)Math.Round(_settleTarget)
                : (int)Math.Round(Math.Max(_range.min, Math.Min(_range.max, _rotation)));

            if (newVal != _value)
            {
                SetAndRaise(ValueProperty, ref _value, newVal);
                ValueChanged?.Invoke(this, newVal);
            }

            InvalidateVisual();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsEnabledProperty || change.Property.Name == "IsFocused")
            {
                InvalidateVisual();
            }
        }

        public void SetRange(int min, int max)
        {
            _range = (min, max);
            InvalidateVisual();
        }

        public void SetLabels(IEnumerable<string> labels)
        {
            _labels = new List<string>(labels);
            InvalidateVisual();
        }

        private double ClampRotation(double val)
        {
            double lo = _range.min - EdgeOverscroll;
            double hi = _range.max + EdgeOverscroll;
            return Math.Max(lo, Math.Min(hi, val));
        }

        private int NearestDetent()
            => (int)Math.Round(Math.Max(_range.min, Math.Min(_range.max, _rotation)));

        /// <summary>The detent the dial is currently headed for — the spring's target while it is
        /// settling, otherwise whatever is nearest. Wheel and arrow-key steps accumulate from HERE,
        /// so a quick burst of clicks advances by the number of clicks rather than collapsing into
        /// one because the dial had not arrived yet.</summary>
        private int PendingDetent()
            => _phase == WheelPhase.Settling ? (int)Math.Round(_settleTarget) : NearestDetent();

        // ══════════════════════════════════════════════════════════════════════════════════════
        // ANIMATION ENGINE
        // ══════════════════════════════════════════════════════════════════════════════════════

        private void EnsureAnimationTimer()
        {
            if (_animTimer != null) return;
            _animTimer = new Avalonia.Threading.DispatcherTimer(Avalonia.Threading.DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _animTimer.Tick += (s, e) => AnimationTick();
        }

        private void StartAnimation()
        {
            EnsureAnimationTimer();
            _lastFrameStamp = Stamp();
            if (!_animTimer!.IsEnabled) _animTimer.Start();
        }

        private void StopAnimation()
        {
            _animTimer?.Stop();
            if (_phase != WheelPhase.Dragging) _phase = WheelPhase.Idle;
            _velocity = 0;
        }

        private void BeginSettle(int detent)
        {
            _settleTarget = Math.Max(_range.min, Math.Min(_range.max, detent));
            _valueLocked = true;

            // Pin the readout to the detent immediately, so the label and the highlighted tick
            // commit the moment the dial is committed — the wiggle is the dial arriving, not the
            // value still being decided.
            int target = (int)Math.Round(_settleTarget);
            if (target != _value)
            {
                SetAndRaise(ValueProperty, ref _value, target);
                ValueChanged?.Invoke(this, target);
            }

            _phase = WheelPhase.Settling;
            StartAnimation();
        }

        private void AnimationTick()
        {
            double dt = SecondsSince(_lastFrameStamp);
            _lastFrameStamp = Stamp();
            if (dt <= 0) return;
            if (dt > 0.10) dt = 0.10;          // a stalled UI thread must not teleport the dial

            if (_phase == WheelPhase.Momentum) StepMomentum(dt);
            else if (_phase == WheelPhase.Settling) StepSettle(dt);
            else { StopAnimation(); return; }
        }

        private void StepMomentum(double dt)
        {
            SetRotationInternal(_rotation + _velocity * dt);
            _velocity *= Math.Exp(-MomentumFriction * dt);

            // End stops: the dial is already clamped inside ClampRotation, so all that is left is
            // to bleed the energy off rather than let it grind against the wall.
            if (_rotation <= _range.min - EdgeOverscroll + 1e-6 || _rotation >= _range.max + EdgeOverscroll - 1e-6)
            {
                _velocity *= EdgeBounceDamping;
            }

            if (Math.Abs(_velocity) < MomentumHandoffSpeed)
            {
                BeginSettle(NearestDetent());
            }
        }

        private void StepSettle(double dt)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(dt / MaxIntegrationStep));
            double h = dt / steps;

            for (int i = 0; i < steps; i++)
            {
                double x = _rotation - _settleTarget;
                double accel = -(SettleOmega * SettleOmega) * x - (2.0 * SettleZeta * SettleOmega) * _velocity;
                _velocity += accel * h;
                _rotation += _velocity * h;
            }

            InvalidateVisual();

            if (Math.Abs(_rotation - _settleTarget) < SettleRestPosition && Math.Abs(_velocity) < SettleRestVelocity)
            {
                _rotation = _settleTarget;
                _velocity = 0;
                _valueLocked = false;
                _phase = WheelPhase.Idle;
                StopAnimation();
                InvalidateVisual();

                if (_interactionPending)
                {
                    _interactionPending = false;
                    ValueChangeCompleted?.Invoke(this, _value);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // INPUT
        // ══════════════════════════════════════════════════════════════════════════════════════

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (!IsEnabled) return;

            StopAnimation();
            _valueLocked = false;
            _phase = WheelPhase.Dragging;
            _interactionPending = true;
            _velocity = 0;

            var pt = e.GetCurrentPoint(this);
            _lastMouseX = pt.Position.X;
            _lastMoveStamp = Stamp();

            // WHEEL_01 — capture the pointer. Without it the drag died the instant the cursor left
            // the 160x44 control, which on a dial you are deliberately flicking is most of the time.
            e.Pointer.Capture(this);

            Cursor = new Cursor(StandardCursorType.Hand);
            Focus();
            SuppressTooltipTemporarily();
            e.Handled = true;
            base.OnPointerPressed(e);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            if (_phase != WheelPhase.Dragging) { base.OnPointerMoved(e); return; }

            var pt = e.GetCurrentPoint(this);
            double dx = pt.Position.X - _lastMouseX;
            _lastMouseX = pt.Position.X;

            double dt = SecondsSince(_lastMoveStamp);
            _lastMoveStamp = Stamp();
            if (dt < 0.001) dt = 0.001;
            if (dt > 0.10) dt = 0.10;

            double pointerSpeed = Math.Abs(dx) / dt;
            double gain = DragGainFor(pointerSpeed);

            double travel = -(dx * gain);
            SetRotationInternal(_rotation + travel);

            // Velocity for the fling, smoothed so one jittery sample cannot launch the dial.
            double instant = travel / dt;
            _velocity = (_velocity * 0.65) + (instant * 0.35);

            SuppressTooltipTemporarily();
            base.OnPointerMoved(e);
        }

        /// <summary>
        /// WHEEL_01 — pointer-acceleration curve, units of dial travel per pixel of pointer travel.
        /// Saturating rather than linear: a linear map either makes precise selection impossible at
        /// the top end or makes long travel exhausting at the bottom. Exponent &lt; 1 makes the
        /// response bite early so the dial does not feel inert during ordinary movement.
        /// </summary>
        private static double DragGainFor(double pointerSpeedPxPerSec)
        {
            double t = pointerSpeedPxPerSec / GainRefSpeedPxPerSec;
            double k = 1.0 - Math.Exp(-Math.Pow(Math.Max(0.0, t), GainCurveExponent));
            return FineGainPerPx + (CoarseGainPerPx - FineGainPerPx) * k;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (_phase != WheelPhase.Dragging) { base.OnPointerReleased(e); return; }

            e.Pointer.Capture(null);
            EndDrag();
            base.OnPointerReleased(e);
        }

        /// <summary>Capture can be taken away (another control grabs it, the window deactivates).
        /// Treat it exactly like a release, or the dial is stranded mid-drag with a live timer and
        /// a value that never commits.</summary>
        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            if (_phase == WheelPhase.Dragging) EndDrag();
            base.OnPointerCaptureLost(e);
        }

        private void EndDrag()
        {
            Cursor = new Cursor(StandardCursorType.Hand);

            // A stale sample means the pointer was held still before letting go — that is a
            // deliberate placement, not a flick, so it must not inherit whatever speed it had.
            if (SecondsSince(_lastMoveStamp) > 0.12) _velocity = 0;

            _velocity = Math.Max(-MaxFlingVelocity, Math.Min(MaxFlingVelocity, _velocity));

            if (Math.Abs(_velocity) >= FlickMinVelocity)
            {
                _phase = WheelPhase.Momentum;
                StartAnimation();
            }
            else
            {
                BeginSettle(NearestDetent());
            }
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            if (!IsEnabled) return;

            double delta = e.Delta.Y > 0 ? 1 : (e.Delta.Y < 0 ? -1 : 0);
            if (e.Delta.X != 0 && delta == 0) delta = e.Delta.X > 0 ? 1 : -1;
            if (delta == 0) { base.OnPointerWheelChanged(e); return; }

            _interactionPending = true;
            StepToDetent(PendingDetent() + (int)delta);

            SuppressTooltipTemporarily();
            e.Handled = true;
            base.OnPointerWheelChanged(e);
        }

        /// <summary>Every discrete step (wheel, arrow key, Home/End) lands through the same spring
        /// as a drag, so the dial behaves identically however it was moved.</summary>
        private void StepToDetent(int detent)
        {
            _velocity = 0;
            BeginSettle(detent);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!IsEnabled)
            {
                base.OnKeyDown(e);
                return;
            }

            int current = PendingDetent();
            switch (e.Key)
            {
                case Key.Left:
                case Key.Down:
                    _interactionPending = true;
                    StepToDetent(current - 1);
                    e.Handled = true;
                    return;
                case Key.Right:
                case Key.Up:
                    _interactionPending = true;
                    StepToDetent(current + 1);
                    e.Handled = true;
                    return;
                case Key.PageDown:
                    _interactionPending = true;
                    StepToDetent(current - 5);
                    e.Handled = true;
                    return;
                case Key.PageUp:
                    _interactionPending = true;
                    StepToDetent(current + 5);
                    e.Handled = true;
                    return;
                case Key.Home:
                    _interactionPending = true;
                    StepToDetent(_range.min);
                    e.Handled = true;
                    return;
                case Key.End:
                    _interactionPending = true;
                    StepToDetent(_range.max);
                    e.Handled = true;
                    return;
            }

            base.OnKeyDown(e);
        }

        /// <summary>WHEEL_01 — leaving the control no longer cancels the drag; the pointer is
        /// captured, so the gesture legitimately continues outside the bounds. Only the cursor is
        /// restored when the pointer leaves while idle.</summary>
        protected override void OnPointerExited(PointerEventArgs e)
        {
            if (_phase != WheelPhase.Dragging)
            {
                Cursor = new Cursor(StandardCursorType.Arrow);
            }
            base.OnPointerExited(e);
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            if (IsEnabled) Cursor = new Cursor(StandardCursorType.Hand);
            base.OnPointerEntered(e);
        }
        
        private static readonly System.Collections.Generic.Dictionary<(string, Color, Color), (FormattedText normal, FormattedText shadow)> _textCache = new();
        private static readonly Typeface _labelTypeface = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold);
        private LinearGradientBrush _rimGrad = BuildRimGrad(Color.Parse("#15202b"), Color.Parse("#3e5871"));
        private RadialGradientBrush _faceGrad = BuildFaceGrad(Color.Parse("#3a6b6b"), Color.Parse("#1e313d"), Color.Parse("#0f1a0f"), Color.Parse("#080c08"));
        private Pen _rimPen = new Pen(new SolidColorBrush(Color.Parse("#0d1217")), 1);
        private Color _tickActive = Color.Parse("#50ffef");
        private Color _tickIdle = Color.Parse("#c5dcf2");
        private Color _tickActiveDisabled = Color.Parse("#95a5a6");
        private Color _tickIdleDisabled = Color.Parse("#7f8c8d");
        private Color _tickShadow = Color.Parse("#000000");

        private static LinearGradientBrush BuildRimGrad(Color edge, Color mid) => new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(edge, 0.0),
                new GradientStop(mid, 0.5),
                new GradientStop(edge, 1.0)
            }
        };

        private static RadialGradientBrush BuildFaceGrad(Color s0, Color s1, Color s2, Color s3) => new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            Radius = 0.7,
            GradientStops = new GradientStops
            {
                new GradientStop(s0, 0.0),
                new GradientStop(s1, 0.4),
                new GradientStop(s2, 0.8),
                new GradientStop(s3, 1.0)
            }
        };

        /// <summary>
        /// ISSUE_10 — pulls every chrome colour for the CURRENT ThemeVariant. Any token that is
        /// missing simply leaves the existing (Dark) value in place, so a broken resource
        /// dictionary degrades to the old look rather than to an invisible control.
        /// </summary>
        private void RefreshThemeBrushes()
        {
            Color Tok(string key, Color fallback)
                => this.TryFindResource(key, ActualThemeVariant, out object? v) && v is Color c ? c : fallback;

            _rimGrad = BuildRimGrad(Tok("AppDialBezelEdgeColor", Color.Parse("#15202b")),
                                    Tok("AppDialBezelMidColor", Color.Parse("#3e5871")));
            _faceGrad = BuildFaceGrad(Tok("AppDialFaceStop0Color", Color.Parse("#3a6b6b")),
                                      Tok("AppDialFaceStop1Color", Color.Parse("#1e313d")),
                                      Tok("AppDialFaceStop2Color", Color.Parse("#0f1a0f")),
                                      Tok("AppDialFaceStop3Color", Color.Parse("#080c08")));
            _rimPen = new Pen(new SolidColorBrush(Tok("AppDialRimColor", Color.Parse("#0d1217"))), 1);
            _tickActive = Tok("AppDialTickActiveColor", Color.Parse("#50ffef"));
            _tickIdle = Tok("AppDialTickIdleColor", Color.Parse("#c5dcf2"));
            _tickActiveDisabled = Tok("AppDialTickActiveDisabledColor", Color.Parse("#95a5a6"));
            _tickIdleDisabled = Tok("AppDialTickIdleDisabledColor", Color.Parse("#7f8c8d"));
            _tickShadow = Tok("AppDialTickShadowColor", Color.Parse("#000000"));
            InvalidateVisual();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            ActualThemeVariantChanged += OnThemeVariantChangedForDial;
            RefreshThemeBrushes();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            ActualThemeVariantChanged -= OnThemeVariantChangedForDial;

            // LEAK_01 — A RUNNING DispatcherTimer KEEPS THIS CONTROL ALIVE AND KEEPS TICKING.
            // The timer's Tick handler holds a reference to the slider, and the dispatcher holds
            // the timer, so a window closed mid-settle (or mid-fling) leaves the whole control
            // rooted and invalidating a visual that is no longer in any tree. WHEEL_01 stops the
            // animation timer when the dial comes to REST, which is the common case and is exactly
            // why this was easy to miss: it only leaks when the user closes the window while the
            // dial is still moving. The tooltip timer has the same shape.
            _animTimer?.Stop();
            _tooltipRestoreTimer?.Stop();
            _phase = WheelPhase.Idle;
            _velocity = 0;
            _valueLocked = false;
            _interactionPending = false;

            base.OnDetachedFromVisualTree(e);
        }

        private void OnThemeVariantChangedForDial(object? sender, EventArgs e) => RefreshThemeBrushes();
        private static readonly LinearGradientBrush _shadowGrad = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.FromArgb(210, 0, 0, 0), 0.0),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.2),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.8),
                new GradientStop(Color.FromArgb(210, 0, 0, 0), 1.0)
            }
        };
        private static readonly RadialGradientBrush _centerGlow = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            Radius = 0.5,
            GradientStops = new GradientStops
            {
                new GradientStop(Color.FromArgb(45, 80, 255, 239), 0.0),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 1.0)
            }
        };

        private static readonly Pen _innerPen = new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), 1);
        private static readonly Pen _redPen = new Pen(new SolidColorBrush(Color.Parse("#ff4d4d")), 2);
        private static readonly Pen _focusPen = new Pen(new SolidColorBrush(Color.Parse("#38bdf8")), 2);

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            double w = Bounds.Width;
            double h = Bounds.Height;
            double cx = w / 2;
            double cy = h / 2;
            
            var rect = new Rect(0, 0, w, h);
            
            context.DrawRectangle(_rimGrad, _rimPen, rect, 6, 6);
            
            var innerRect = rect.Deflate(3);
            
            context.DrawRectangle(_faceGrad, _innerPen, innerRect, 4, 4);
            
            using (context.PushClip(innerRect))
            {
                for (int i = _range.min - 5; i <= _range.max + 5; i++)
                {
                    double ribAngle = (i - _rotation) * (Math.PI / 5);
                    if (Math.Abs(ribAngle) > Math.PI / 1.8) continue;
                    double ribOpacity = Math.Pow(Math.Cos(ribAngle), 2.0);
                    double ribX = cx + Math.Sin(ribAngle) * (w * 0.85);
                    double ribW = Math.Max(1.0, 5.0 * Math.Pow(ribOpacity, 2.5));
                    
                    var ribGrad = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                        GradientStops = new GradientStops
                        {
                            new GradientStop(Color.FromArgb((byte)(130 * ribOpacity), 0, 10, 20), 0.0),
                            new GradientStop(Color.FromArgb((byte)(40 * ribOpacity), 210, 245, 255), 0.5),
                            new GradientStop(Color.FromArgb((byte)(130 * ribOpacity), 0, 20, 10), 1.0)
                        }
                    };
                    
                    var ribRect = new Rect(ribX - ribW / 2, innerRect.Top, ribW, innerRect.Height);
                    context.DrawRectangle(ribGrad, null, ribRect);
                }
                
                var shadowRect = innerRect.Deflate(1);
                context.DrawRectangle(_shadowGrad, null, shadowRect, 4, 4);
                
                for (int i = _range.min; i <= _range.max; i++)
                {
                    double angle = (i - _rotation) * (Math.PI / 5);
                    if (Math.Abs(angle) > Math.PI / 1.4) continue;
                    double opacity = Math.Cos(angle);
                    if (opacity < 0) continue;
                    
                    double xPos = cx + Math.Sin(angle) * (w * 0.82);
                    double scale = 0.50 + (0.60 * Math.Pow(opacity, 0.6));
                    double yBulge = (1.0 - Math.Pow(opacity, 0.3)) * 12;
                    
                    int labelIndex = i - _range.min;
                    string txt = (labelIndex >= 0 && labelIndex < _labels.Count) ? _labels[labelIndex] : i.ToString();
                    
                    Color baseColor = i == _value
                        ? (IsEnabled ? _tickActive : _tickActiveDisabled)
                        : (IsEnabled ? _tickIdle : _tickIdleDisabled);
                    
                    byte alpha = (byte)(255 * Math.Pow(opacity, 5.0));
                    
                    var typeface = _labelTypeface;
                    var key = (txt, baseColor, _tickShadow);
                    if (!_textCache.TryGetValue(key, out var texts))
                    {
                        var brush = new SolidColorBrush(baseColor);
                        var shadowBrush = new SolidColorBrush(_tickShadow);
                        texts.normal = new FormattedText(txt, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 9, brush);
                        texts.shadow = new FormattedText(txt, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 9, shadowBrush);
                        _textCache[key] = texts;
                    }
                    
                    double tw = texts.normal.Width * scale;
                    double th = texts.normal.Height * scale;
                    
                    using (context.PushOpacity(alpha / 255.0))
                    {
                        var mat = Avalonia.Matrix.CreateScale(scale, scale);
                        using (context.PushTransform(mat * Avalonia.Matrix.CreateTranslation(xPos - tw / 2 + 2, cy - th / 2 + yBulge + 2)))
                            context.DrawText(texts.shadow, new Point(0, 0));

                        using (context.PushTransform(mat * Avalonia.Matrix.CreateTranslation(xPos - tw / 2, cy - th / 2 + yBulge)))
                            context.DrawText(texts.normal, new Point(0, 0));
                    }
                }
            }
            
            if (IsEnabled)
            {
                context.DrawLine(_redPen, new Point(cx, 3), new Point(cx, 11));
                context.DrawLine(_redPen, new Point(cx, h - 11), new Point(cx, h - 3));
                
                context.DrawRectangle(_centerGlow, null, innerRect);
            }

            if (IsFocused)
            {
                context.DrawRectangle(null, _focusPen, rect.Deflate(1), 6, 6);
            }
        }
    }
    
}