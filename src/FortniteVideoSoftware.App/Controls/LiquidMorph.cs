using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using System;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>
/// Seamless state transition animations (IDEA_009).
/// Provides reusable morph methods for toggling Portrait mode, opening secondary windows,
/// and switching tool ribbons — all via pure Avalonia Transitions (zero layout passes).
/// </summary>
public static class LiquidMorph
{
    /// <summary>
    /// Morphs a control's opacity from 0→1 with an optional scale-from-origin.
    /// Use for secondary window open animations.
    /// </summary>
    public static async Task FadeInAsync(Control target, TimeSpan? duration = null, double fromScale = 0.96)
    {
        var dur = duration ?? TimeSpan.FromMilliseconds(180);
        target.Opacity = 0;

        if (fromScale < 1.0)
        {
            var transform = new ScaleTransform(fromScale, fromScale);
            target.RenderTransform = transform;
            target.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);

            // Animate scale
            var scaleAnim = new Animation
            {
                Duration = dur,
                Children =
                {
                    new KeyFrame
                    {
                        Setters = { new Setter(ScaleTransform.ScaleXProperty, fromScale), new Setter(ScaleTransform.ScaleYProperty, fromScale) },
                        KeyTime = TimeSpan.Zero
                    },
                    new KeyFrame
                    {
                        Setters = { new Setter(ScaleTransform.ScaleXProperty, 1.0), new Setter(ScaleTransform.ScaleYProperty, 1.0) },
                        KeyTime = dur
                    }
                }
            };
            _ = scaleAnim.RunAsync(target);
        }

        // Animate opacity
        var opacityAnim = new Animation
        {
            Duration = dur,
            Children =
            {
                new KeyFrame { Setters = { new Setter(Visual.OpacityProperty, 0.0) }, KeyTime = TimeSpan.Zero },
                new KeyFrame { Setters = { new Setter(Visual.OpacityProperty, 1.0) }, KeyTime = dur }
            }
        };
        await opacityAnim.RunAsync(target);
    }

    /// <summary>
    /// Morphs a control's opacity from 1→0 with an optional scale shrink.
    /// </summary>
    public static async Task FadeOutAsync(Control target, TimeSpan? duration = null, double toScale = 0.96)
    {
        var dur = duration ?? TimeSpan.FromMilliseconds(150);
        var opacityAnim = new Animation
        {
            Duration = dur,
            Children =
            {
                new KeyFrame { Setters = { new Setter(Visual.OpacityProperty, 1.0) }, KeyTime = TimeSpan.Zero },
                new KeyFrame { Setters = { new Setter(Visual.OpacityProperty, 0.0) }, KeyTime = dur }
            }
        };
        await opacityAnim.RunAsync(target);
    }

    /// <summary>
    /// Crossfade between two controls (old fades out, new fades in).
    /// </summary>
    public static async Task CrossfadeAsync(Control outgoing, Control incoming, TimeSpan? duration = null)
    {
        var dur = duration ?? TimeSpan.FromMilliseconds(250);
        var outTask = FadeOutAsync(outgoing, dur);
        incoming.IsVisible = true;
        var inTask = FadeInAsync(incoming, dur);
        await Task.WhenAll(outTask, inTask);
        outgoing.IsVisible = false;
    }

    /// <summary>
    /// Applies a morphing transition to a control's side-dim flanks
    /// (used when toggling Portrait mode on/off).
    /// The dim flanks animate from fully transparent to semi-opaque.
    /// </summary>
    public static void AttachPortraitMorph(Control videoPanel)
    {
        videoPanel.Transitions ??= new Transitions();
        // Ensure transitions don't duplicate
        for (int i = videoPanel.Transitions.Count - 1; i >= 0; i--)
        {
            if (videoPanel.Transitions[i] is BrushTransition)
                videoPanel.Transitions.RemoveAt(i);
        }
        videoPanel.Transitions.Add(new BrushTransition
        {
            Property = Border.BackgroundProperty,
            Duration = TimeSpan.FromMilliseconds(250)
        });
    }

    /// <summary>
    /// Simple punch animation: scale 1.0 → 1.06 → 1.0.
    /// Used for action confirmation feedback (e.g., marker drop, toggle apply).
    /// </summary>
    public static async Task PunchAsync(Control target)
    {
        var transform = new ScaleTransform(1.0, 1.0);
        var previousTransform = target.RenderTransform;
        target.RenderTransform = transform;
        target.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);

        var dur = TimeSpan.FromMilliseconds(150);
        var anim = new Animation
        {
            Duration = dur,
            Children =
            {
                new KeyFrame
                {
                    Setters = { new Setter(ScaleTransform.ScaleXProperty, 1.0), new Setter(ScaleTransform.ScaleYProperty, 1.0) },
                    KeyTime = TimeSpan.Zero
                },
                new KeyFrame
                {
                    Setters = { new Setter(ScaleTransform.ScaleXProperty, 1.06), new Setter(ScaleTransform.ScaleYProperty, 1.06) },
                    KeyTime = TimeSpan.FromMilliseconds(75)
                },
                new KeyFrame
                {
                    Setters = { new Setter(ScaleTransform.ScaleXProperty, 1.0), new Setter(ScaleTransform.ScaleYProperty, 1.0) },
                    KeyTime = dur
                }
            }
        };
        await anim.RunAsync(target);
        target.RenderTransform = previousTransform;
    }
}