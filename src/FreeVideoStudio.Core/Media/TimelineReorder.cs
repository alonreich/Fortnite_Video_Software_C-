using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// ANTS_01 — the maths of dragging a clip block along the merged timeline (Video-Merger-Migration.md
/// P5.3, D14). Pure, so the gesture is unit-tested without a window.
/// </summary>
public static class TimelineReorder
{
    /// <summary>
    /// Final index of the dragged clip if dropped at <paramref name="pointerX"/>: the number of OTHER
    /// blocks whose midpoint lies left of the pointer. That is exactly the <c>newIndex</c> of
    /// <c>ObservableCollection.Move(from, newIndex)</c>.
    /// </summary>
    public static int TargetIndex(IReadOnlyList<(double X0, double X1)> blocks, int from, double pointerX)
    {
        int target = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            if (i == from) continue;
            if ((blocks[i].X0 + blocks[i].X1) / 2.0 < pointerX) target++;
        }
        return Math.Clamp(target, 0, Math.Max(0, blocks.Count - 1));
    }

    /// <summary>Where to draw the insertion bar for a drop at <paramref name="target"/> (the boundary it will land on).</summary>
    public static double InsertionX(IReadOnlyList<(double X0, double X1)> blocks, int from, int target)
    {
        var others = new List<(double X0, double X1)>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++) if (i != from) others.Add(blocks[i]);
        if (others.Count == 0) return blocks.Count > 0 ? blocks[0].X0 : 0;
        if (target <= 0) return others[0].X0;
        return others[Math.Min(target, others.Count) - 1].X1;
    }
}
