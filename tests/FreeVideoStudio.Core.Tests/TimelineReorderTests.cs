using System.Collections.ObjectModel;
using System.Linq;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>ANTS_01 — Video-Merger-Migration.md P5.3 (T5.3b at Core level).</summary>
public class TimelineReorderTests
{
    private static readonly (double, double)[] Blocks = { (0, 100), (100, 150), (150, 300), (300, 400) };

    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(0, 130, 1)]
    [InlineData(0, 399, 3)]
    [InlineData(3, 0, 0)]
    [InlineData(3, 200, 2)]
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

        int to = TimelineReorder.TargetIndex(Blocks, 0, 260);
        q.Move(0, to);

        Assert.Equal(new[] { "b", "c", "a", "d" }, q);
        Assert.Equal(idOfA, ids.Ids[2]);
    }

    [Fact]
    public void InsertionBar_SitsOnTheBoundaryItWillLandOn()
    {
        Assert.Equal(0, TimelineReorder.InsertionX(Blocks, 3, 0));
        Assert.Equal(150, TimelineReorder.InsertionX(Blocks, 0, 1));
        Assert.Equal(400, TimelineReorder.InsertionX(Blocks, 0, 3));
        Assert.Equal(0, TimelineReorder.InsertionX(new[] { (0.0, 10.0) }, 0, 0));
    }
}
