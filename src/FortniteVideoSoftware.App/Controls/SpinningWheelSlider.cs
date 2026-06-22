using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System;
using System.Collections.Generic;

namespace FortniteVideoSoftware.App.Controls
{
    public class SpinningWheelSlider : Control
    {
        private int _value = 0;
        private (int min, int max) _range = (0, 20);
        private List<string> _labels = new List<string>();
        private double _rotation = 0.0;
        
        private bool _isDragging = false;
        private double _lastMouseX = 0;
        private double _overscroll = 0.08;
        
        public event EventHandler<int>? ValueChanged;
        
        public int Value 
        {
            get => _value;
            set => SetValue(value);
        }
        
        public double Rotation
        {
            get => _rotation;
            set
            {
                _rotation = ClampRotation(value);
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
            if (change.Property == IsEnabledProperty)
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
            val = Math.Max(_range.min, Math.Min(_range.max, val));
            if (val != _value || Math.Abs(_rotation - val) > 0.01)
            {
                _value = val;
                Rotation = val;
                ValueChanged?.Invoke(this, val);
            }
        }
        
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (!IsEnabled) return;
            _isDragging = true;
            var pt = e.GetCurrentPoint(this);
            _lastMouseX = pt.Position.X;
            Cursor = new Cursor(StandardCursorType.Hand);
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
            e.Handled = true;
            base.OnPointerWheelChanged(e);
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
        
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            double w = Bounds.Width;
            double h = Bounds.Height;
            double cx = w / 2;
            double cy = h / 2;
            
            var rect = new Rect(0, 0, w, h);
            
            // ── Outer rim: dark blue-grey matching Python's #15202b → #3e5871 → #15202b ──
            var rimGrad = new LinearGradientBrush
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
            
            var rimPen = new Pen(new SolidColorBrush(Color.Parse("#0d1217")), 1);
            context.DrawRectangle(rimGrad, rimPen, rect, 6, 6);
            
            var innerRect = rect.Deflate(3);
            
            // ── Inner face: teal radial gradient matching Python's RadialGradient ──
            // Python: center=#3a6b6b, 0.4=#1e313d, 0.8=#0f1a0f, 1.0=#080c08
            var faceGrad = new RadialGradientBrush
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
            
            var innerPen = new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), 1);
            context.DrawRectangle(faceGrad, innerPen, innerRect, 4, 4);
            
            // Clip all inner drawing to the inner rectangle
            using (context.PushClip(innerRect))
            {
                // ── Rib lines: matching Python's blue-green tinted ribs ──
                for (int i = _range.min - 5; i <= _range.max + 5; i++)
                {
                    double ribAngle = (i - _rotation) * (Math.PI / 5);
                    if (Math.Abs(ribAngle) > Math.PI / 1.8) continue;
                    double ribOpacity = Math.Pow(Math.Cos(ribAngle), 2.0);
                    double ribX = cx + Math.Sin(ribAngle) * (w * 0.85);
                    double ribW = Math.Max(1.0, 5.0 * Math.Pow(ribOpacity, 2.5));
                    
                    // Python: rib colors (0,10,20,130*op) → (210,245,255,40*op) → (0,20,10,130*op)
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
                
                // ── Top/bottom shadow vignette ──
                var shadowRect = innerRect.Deflate(1);
                var shadowGrad = new LinearGradientBrush
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
                context.DrawRectangle(shadowGrad, null, shadowRect, 4, 4);
                
                // ── Text labels ──
                for (int i = _range.min; i <= _range.max; i++)
                {
                    double angle = (i - _rotation) * (Math.PI / 5);
                    if (Math.Abs(angle) > Math.PI / 1.4) continue;
                    double opacity = Math.Cos(angle);
                    if (opacity < 0) continue;
                    
                    double xPos = cx + Math.Sin(angle) * (w * 0.82);
                    double scale = 0.50 + (0.60 * Math.Pow(opacity, 0.6));
                    double yBulge = (1.0 - Math.Pow(opacity, 0.3)) * 12;
                    
                    string txt = (i >= 0 && i < _labels.Count) ? _labels[i] : i.ToString();
                    
                    // Python: selected=#50ffef (cyan), non-selected=#c5dcf2 (light blue)
                    Color baseColor = i == _value 
                        ? (IsEnabled ? Color.Parse("#50ffef") : Color.Parse("#95a5a6"))
                        : (IsEnabled ? Color.Parse("#c5dcf2") : Color.Parse("#7f8c8d"));
                    
                    byte alpha = (byte)(255 * Math.Pow(opacity, 5.0));
                    
                    var typeface = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold);
                    var brush = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
                    var shadowBrush = new SolidColorBrush(Color.FromArgb((byte)(alpha * 0.8), 0, 0, 0));
                    
                    var formattedText = new FormattedText(txt, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 9 * scale, brush);
                    var shadowText = new FormattedText(txt, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 9 * scale, shadowBrush);
                    
                    double tw = formattedText.Width;
                    double th = formattedText.Height;
                    
                    // Draw shadow text offset by 2px
                    context.DrawText(shadowText, new Point(xPos - tw / 2 + 2, cy - th / 3 + yBulge + 2));
                    context.DrawText(formattedText, new Point(xPos - tw / 2, cy - th / 3 + yBulge));
                }
            }
            
            // ── Center indicator and glow (only when enabled) ──
            if (IsEnabled)
            {
                // Python: red indicator tick marks at top and bottom center
                var redPen = new Pen(new SolidColorBrush(Color.Parse("#ff4d4d")), 2);
                // Top tick
                context.DrawLine(redPen, new Point(cx, 3), new Point(cx, 11));
                // Bottom tick
                context.DrawLine(redPen, new Point(cx, h - 11), new Point(cx, h - 3));
                
                // Python: center glow ellipse (80,255,239,45) → transparent
                var centerGlow = new RadialGradientBrush
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
                context.DrawRectangle(centerGlow, null, innerRect);
            }
        }
    }
}
