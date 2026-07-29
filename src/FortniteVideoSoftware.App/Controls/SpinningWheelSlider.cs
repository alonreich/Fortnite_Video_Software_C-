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
        
        private bool _isDragging = false;
        private double _lastMouseX = 0;
        private double _overscroll = 0.08;

        private object? _savedToolTip = null;
        private bool _isTooltipSuppressed = false;
        private Avalonia.Threading.DispatcherTimer? _tooltipRestoreTimer;
        
        public event EventHandler<int>? ValueChanged;
        public event EventHandler<int>? ValueChangeCompleted;

        public SpinningWheelSlider()
        {
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        /// <summary>
        /// Issue #3: Temporarily suppresses the tooltip to prevent it from popping up
        /// destructively over the spinner while the user is dragging/scrolling.
        /// The tooltip is restored after the user stops interacting (with a short delay).
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
                        ValueChangeCompleted?.Invoke(this, _value);
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
                if (_value != clamped)
                {
                    SetAndRaise(ValueProperty, ref _value, clamped);
                    if (Math.Abs(_rotation - clamped) > 0.01)
                    {
                        Rotation = clamped;
                    }
                    ValueChanged?.Invoke(this, clamped);
                }
            }
        }
        
        public double Rotation
        {
            get => _rotation;
            set
            {
                double clamped = ClampRotation(value);
                if (Math.Abs(_rotation - clamped) < 0.001) return;
                
                _rotation = clamped;
                int newVal = (int)Math.Round(Math.Max(_range.min, Math.Min(_range.max, _rotation)));
                if (newVal != _value)
                {
                    _value = newVal;
                    ValueChanged?.Invoke(this, _value);
                }
                InvalidateVisual();
            }
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
            double lo = _range.min - _overscroll;
            double hi = _range.max + _overscroll;
            return Math.Max(lo, Math.Min(hi, val));
        }
        
        private void SetValue(int val, bool animated = true)
        {
            Value = val;
        }
        
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (!IsEnabled) return;
            _isDragging = true;
            var pt = e.GetCurrentPoint(this);
            _lastMouseX = pt.Position.X;
            Cursor = new Cursor(StandardCursorType.Hand);
            Focus();
            SuppressTooltipTemporarily();
            e.Handled = true;
            base.OnPointerPressed(e);
        }
        
        protected override void OnPointerMoved(PointerEventArgs e)
        {
            if (!_isDragging) return;
            var pt = e.GetCurrentPoint(this);
            double dx = pt.Position.X - _lastMouseX;
            _lastMouseX = pt.Position.X;
            double sensitivity = 0.011;
            Rotation = _rotation - (dx * sensitivity);
            SuppressTooltipTemporarily();
            base.OnPointerMoved(e);
        }
        
        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            Cursor = new Cursor(StandardCursorType.Hand);
            int target = (int)Math.Round(Math.Max(_range.min, Math.Min(_range.max, _rotation)));
            SetValue(target);
            base.OnPointerReleased(e);
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            if (!IsEnabled) return;
            double delta = e.Delta.Y > 0 ? 1 : (e.Delta.Y < 0 ? -1 : 0);
            if (e.Delta.X != 0 && delta == 0) delta = e.Delta.X > 0 ? 1 : -1;
            
            int current = (int)Math.Round(_rotation);
            int target = current + (int)delta;
            SetValue(target);
            SuppressTooltipTemporarily();
            e.Handled = true;
            base.OnPointerWheelChanged(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!IsEnabled)
            {
                base.OnKeyDown(e);
                return;
            }

            int current = (int)Math.Round(_rotation);
            switch (e.Key)
            {
                case Key.Left:
                case Key.Down:
                    SetValue(current - 1);
                    e.Handled = true;
                    return;
                case Key.Right:
                case Key.Up:
                    SetValue(current + 1);
                    e.Handled = true;
                    return;
                case Key.PageDown:
                    SetValue(current - 5);
                    e.Handled = true;
                    return;
                case Key.PageUp:
                    SetValue(current + 5);
                    e.Handled = true;
                    return;
                case Key.Home:
                    SetValue(_range.min);
                    e.Handled = true;
                    return;
                case Key.End:
                    SetValue(_range.max);
                    e.Handled = true;
                    return;
            }

            base.OnKeyDown(e);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                Cursor = new Cursor(StandardCursorType.Arrow);
                int target = (int)Math.Round(Math.Max(_range.min, Math.Min(_range.max, _rotation)));
                SetValue(target);
            }
            base.OnPointerExited(e);
        }
        
        private static readonly System.Collections.Generic.Dictionary<(string, Color), (FormattedText normal, FormattedText shadow)> _textCache = new();
        private static readonly Typeface _labelTypeface = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold);
        private static readonly LinearGradientBrush _rimGrad = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.Parse("#15202b"), 0.0),
                new GradientStop(Color.Parse("#3e5871"), 0.5),
                new GradientStop(Color.Parse("#15202b"), 1.0)
            }
        };
        private static readonly RadialGradientBrush _faceGrad = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            Radius = 0.7,
            GradientStops = new GradientStops
            {
                new GradientStop(Color.Parse("#3a6b6b"), 0.0),
                new GradientStop(Color.Parse("#1e313d"), 0.4),
                new GradientStop(Color.Parse("#0f1a0f"), 0.8),
                new GradientStop(Color.Parse("#080c08"), 1.0)
            }
        };
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

        private static readonly Pen _rimPen = new Pen(new SolidColorBrush(Color.Parse("#0d1217")), 1);
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
                        ? (IsEnabled ? Color.Parse("#50ffef") : Color.Parse("#95a5a6"))
                        : (IsEnabled ? Color.Parse("#c5dcf2") : Color.Parse("#7f8c8d"));
                    
                    byte alpha = (byte)(255 * Math.Pow(opacity, 5.0));
                    
                    var typeface = _labelTypeface;
                    var key = (txt, baseColor);
                    if (!_textCache.TryGetValue(key, out var texts))
                    {
                        var brush = new SolidColorBrush(baseColor);
                        var shadowBrush = new SolidColorBrush(Color.Parse("#000000"));
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
