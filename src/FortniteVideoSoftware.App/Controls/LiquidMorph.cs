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
    /// Applies a morphing transition to a control's side-dim flanks
    /// (used when toggling Portrait mode on/off).
    /// The dim flanks animate from fully transparent to semi-opaque.
    /// </summary>
    public static void AttachPortraitMorph(Control videoPanel)
    {
        videoPanel.Transitions ??= new Transitions();
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
}