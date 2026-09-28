using System.Collections.ObjectModel;
using System.Linq;
using FortniteVideoSoftware.Core.Media;
using Xunit;

namespace FortniteVideoSoftware.Core.Tests;

/// <summary>ANTS_01 — Video-Merger-Migration.md P5.3 (T5.3b at Core level).</summary>
public class TimelineReorderTests
{
    // Four blocks: [0,100) [100,150) [150,300) [300,400)  → midpoints 50, 125, 225, 350
    private static readonly (double, double)[] Blocks = { (0, 100), (100, 150), (150, 300), (300, 400) };

    [Theory]
    [InlineData(0, 10, 0)]     // no move
    [InlineData(0, 130, 1)]    // past b's midpoint
    [InlineData(0, 399, 3)]    // to the end
    [InlineData(3, 0, 0)]      // to the front
    [InlineData(3, 200, 2)]    // before c's midpoint? no: past a,b (50,125) → 2
    [InlineData(2, 60, 1)]
    public void TargetIndex_IsTheMoveNewIndex(int from, double x, int expected)
        => Assert.Equal(expected, TimelineReorder.TargetIndex(Blocks, from, x));

    [Fact]
    public void T53b_DropOnTheTimeline_ReordersTheQueue_AndIdsFollow()
    {
        var q = new ObservableCollection<string> { "a", "b", "c", "d" };
        var ids = new ClipIdList();
        ids.Insert(0, 4);
        q.CollectionChanged += (s, e) => ids.Apply(e, q.Count);
        var idOfA = ids.Ids[0];

        int to = TimelineReorder.TargetIndex(Blocks, 0, 260);   // drag "a" past b and c
        q.Move(0, to);

        Assert.Equal(new[] { "b", "c", "a", "d" }, q);
        Assert.Equal(idOfA, ids.Ids[2]);
    }

    [Fact]
    public void InsertionBar_SitsOnTheBoundaryItWillLandOn()
    {
        Assert.Equal(0, TimelineReorder.InsertionX(Blocks, 3, 0));      // front
        Assert.Equal(150, TimelineReorder.InsertionX(Blocks, 0, 1));    // after b (a removed)
        Assert.Equal(400, TimelineReorder.InsertionX(Blocks, 0, 3));    // end
        Assert.Equal(0, TimelineReorder.InsertionX(new[] { (0.0, 10.0) }, 0, 0));
    }
}
