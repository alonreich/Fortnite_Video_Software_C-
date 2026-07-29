using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using System;
using Avalonia.Media.Transformation;

namespace FortniteVideoSoftware.App.Controls
{
    public class Tactile : AvaloniaObject
    {
        public static readonly AttachedProperty<bool> IsRippleEnabledProperty =
            AvaloniaProperty.RegisterAttached<Tactile, Control, bool>("IsRippleEnabled");

        public static bool GetIsRippleEnabled(Control element) => element.GetValue(IsRippleEnabledProperty);
        public static void SetIsRippleEnabled(Control element, bool value) => element.SetValue(IsRippleEnabledProperty, value);

        public static readonly AttachedProperty<bool> IsParallaxTiltEnabledProperty =
            AvaloniaProperty.RegisterAttached<Tactile, Control, bool>("IsParallaxTiltEnabled");

        public static bool GetIsParallaxTiltEnabled(Control element) => element.GetValue(IsParallaxTiltEnabledProperty);
        public static void SetIsParallaxTiltEnabled(Control element, bool value) => element.SetValue(IsParallaxTiltEnabledProperty, value);

        static Tactile()
        {
            IsRippleEnabledProperty.Changed.AddClassHandler<Control>(OnIsRippleEnabledChanged);
            IsParallaxTiltEnabledProperty.Changed.AddClassHandler<Control>(OnIsParallaxTiltEnabledChanged);
        }

        private static void OnIsRippleEnabledChanged(Control sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool isEnabled)
            {
                if (isEnabled)
                {
                    sender.PointerPressed += Ripple_PointerPressed;
                }
                else
                {
                    sender.PointerPressed -= Ripple_PointerPressed;
                }
            }
        }

        private static void Ripple_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control c) return;
            var layer = AdornerLayer.GetAdornerLayer(c);
            if (layer == null) return;

            var pt = e.GetPosition(c);
            
            var ripple = new Ellipse
            {
                Width = 20,
                Height = 20,
                IsHitTestVisible = false,
                Fill = new RadialGradientBrush
                {
                    GradientStops = new GradientStops
                    {
                        new GradientStop(Color.Parse("#00FFFFFF"), 0.0),
                        new GradientStop(Color.Parse("#30FFFFFF"), 0.7),
                        new GradientStop(Color.Parse("#00FFFFFF"), 1.0)
                    }
                },
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative)
            };

            var scaleTransform = new ScaleTransform(1, 1);
            ripple.RenderTransform = scaleTransform;

            AdornerLayer.SetAdornedElement(ripple, c);
            layer.Children.Add(ripple);
            
            Canvas.SetLeft(ripple, pt.X - 10);
            Canvas.SetTop(ripple, pt.Y - 10);

            var duration = TimeSpan.FromMilliseconds(450);
            var easing = new SplineEasing(0.25, 1.0, 0.5, 1.0);
            double targetScale = (c.Bounds.Width / 10.0) + 3.0;

            var animation = new Animation
            {
                Duration = duration,
                Easing = easing,
                FillMode = FillMode.Forward,
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(1.0),
                        Setters =
                        {
                            new Setter(ScaleTransform.ScaleXProperty, targetScale),
                            new Setter(ScaleTransform.ScaleYProperty, targetScale),
                            new Setter(Visual.OpacityProperty, 0.0)
                        }
                    }
                }
            };

            animation.RunAsync(ripple).ContinueWith(t =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    layer.Children.Remove(ripple);
                });
            });
        }

        private static void OnIsParallaxTiltEnabledChanged(Control sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool isEnabled)
            {
                if (isEnabled)
                {
                    sender.PointerMoved += Tilt_PointerMoved;
                    sender.PointerExited += Tilt_PointerExited;
                    sender.PointerEntered += Tilt_PointerEntered;
                }
                else
                {
                    sender.PointerMoved -= Tilt_PointerMoved;
                    sender.PointerExited -= Tilt_PointerExited;
                    sender.PointerEntered -= Tilt_PointerEntered;
                    sender.RenderTransform = null;
                }
            }
        }
        
        private static TransformGroup GetOrCreateTransformGroup(Control c)
        {
            if (c.RenderTransform is TransformGroup group) return group;
            var newGroup = new TransformGroup();
            newGroup.Children.Add(new ScaleTransform(1.0, 1.0));
            newGroup.Children.Add(new TranslateTransform(0.0, 0.0));
            c.RenderTransform = newGroup;
            return newGroup;
        }

        private static void Tilt_PointerEntered(object? sender, PointerEventArgs e)
        {
            if (sender is not Control c) return;
            var group = GetOrCreateTransformGroup(c);
            var scale = (ScaleTransform)group.Children[0];
            scale.ScaleX = 0.99;
            scale.ScaleY = 0.99;
        }

        private static void Tilt_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (sender is not Control c) return;
            var pt = e.GetPosition(c);
            
            double centerX = c.Bounds.Width / 2.0;
            double centerY = c.Bounds.Height / 2.0;
            
            double offsetX = pt.X - centerX;
            double offsetY = pt.Y - centerY;
            
            double normX = Math.Clamp(offsetX / centerX, -1.0, 1.0);
            double normY = Math.Clamp(offsetY / centerY, -1.0, 1.0);

            var group = GetOrCreateTransformGroup(c);
            var translate = (TranslateTransform)group.Children[1];
            
            translate.X = normX * -1.5;
            translate.Y = normY * -1.5;
        }

        private static void Tilt_PointerExited(object? sender, PointerEventArgs e)
        {
            if (sender is not Control c) return;
            var group = GetOrCreateTransformGroup(c);
            var scale = (ScaleTransform)group.Children[0];
            var translate = (TranslateTransform)group.Children[1];
            
            scale.ScaleX = 1.0;
            scale.ScaleY = 1.0;
            translate.X = 0;
            translate.Y = 0;
        }
    }
}
