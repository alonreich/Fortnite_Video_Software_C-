using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FortniteVideoSoftware.Core.Media;
using FortniteVideoSoftware.Core.Undo;
using Xunit;

namespace FortniteVideoSoftware.Core.Tests;

/// <summary>MERGEUNDO_01 — Video-Merger-Migration.md P3.3 (T3.3a, T3.3b).</summary>
public class MergerUndoTests
{
    private static EdlClip C(string p) => new() { Path = p };

    /// <summary>A queue whose ids track its events exactly like the window's.</summary>
    private static (ObservableCollection<string> Queue, ClipIdList Ids) Live()
    {
        var q = new ObservableCollection<string>();
        var ids = new ClipIdList();
        q.CollectionChanged += (s, e) => ids.Apply(e, q.Count);
        return (q, ids);
    }

    [Fact]
    public void T33a_AnalysisFinishing_IsNotAnUndoStep()
    {
        var a = C("a.mp4");
        var before = new MergeEdl { Clips = new[] { a } };
        var analysed = new MergeEdl { Clips = new[] { a with { DurationUs = 9_000_000, SizeBytes = 5, LastWriteUtcTicks = 6, IntroCutUs = 100_000, Timing = new ExportTiming(60, 1, 6, null, null) } } };

        var stack = new UndoStack<MergeEdl>(MergerSession.UserEdit(before));
        Assert.False(stack.Apply(MergerSession.UserEdit(analysed), "x"));
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public void T33a_DescribeChange_NamesEveryKindOfEdit()
    {
        var a = C("a"); var b = C("b");
        var one = new MergeEdl { Clips = new[] { a } };
        var two = new MergeEdl { Clips = new[] { a, b } };
        Assert.Equal(("add clip", (string?)null), MergerSession.DescribeChange(one, two));
        Assert.Equal("remove clip", MergerSession.DescribeChange(two, one).Label);
        Assert.Equal("reorder clips", MergerSession.DescribeChange(two, two with { Clips = new[] { b, a } }).Label);
        Assert.Equal("turn thumbnail scraper off", MergerSession.DescribeChange(two, two with { ScraperEnabled = false }).Label);
        var thumb = two with { Thumbnail = new EdlThumbnail(new EdlAnchor(a.ClipId, 1)) };
        Assert.Equal("set thumbnail", MergerSession.DescribeChange(two, thumb).Label);
        Assert.Equal(("move thumbnail", "thumb"), MergerSession.DescribeChange(thumb, thumb with { Thumbnail = new EdlThumbnail(new EdlAnchor(a.ClipId, 2)) }));
        Assert.Equal("remove thumbnail", MergerSession.DescribeChange(thumb, two).Label);
        var music = two with { Music = new EdlMusic { FilePaths = new[] { "s.mp3" } } };
        Assert.Equal("add music", MergerSession.DescribeChange(two, music).Label);
        Assert.Equal("change music", MergerSession.DescribeChange(music, music with { Music = music.Music! with { MusicVolume = 0.5 } }).Label);
        Assert.Equal("remove music", MergerSession.DescribeChange(music, two).Label);
        Assert.Equal(("change speed", "speed"), MergerSession.DescribeChange(two, two with { BaseSpeed = 2 }));
        var fx = two with { Clips = new[] { a with { Effects = new EdlEffects { Freezes = new[] { new EdlFreeze(1, 1) } } }, b } };
        Assert.Equal("edit clip", MergerSession.DescribeChange(two, fx).Label);
    }

    [Fact]
    public void SyncQueue_ReordersInPlace_ReinsertsWithTheSavedIds_AndRemoves()
    {
        var (q, ids) = Live();
        foreach (var p in new[] { "a", "b", "c", "d" }) q.Add(p);
        var clips = q.Select((p, i) => new EdlClip { ClipId = ids.Ids[i], Path = p }).ToArray();

        int resets = 0;
        q.CollectionChanged += (s, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; };

        // Remove b and d, reorder, re-add a duplicate of a as a new clip.
        var dupA = new EdlClip { Path = "a" };
        var target = new[] { clips[2], clips[0], dupA };
        MergerSession.SyncQueue(q, ids, target);
        Assert.Equal(new[] { "c", "a", "a" }, q);
        Assert.Equal(target.Select(c => c.ClipId), ids.Ids);

        // And back (undo): b and d come back with their ORIGINAL ids.
        MergerSession.SyncQueue(q, ids, clips);
        Assert.Equal(new[] { "a", "b", "c", "d" }, q);
        Assert.Equal(clips.Select(c => c.ClipId), ids.Ids);
        Assert.Equal(0, resets);
    }

    [Fact]
    public void T33b_FiveEdits_UndoFive_RedoFive()
    {
        var (q, ids) = Live();
        MergeEdl Snapshot(bool scraper, double speed, EdlThumbnail? thumb) => new()
        {
            Clips = q.Select((p, i) => new EdlClip { ClipId = ids.Ids[i], Path = p }).ToArray(),
            ScraperEnabled = scraper,
            BaseSpeed = speed,
            Thumbnail = thumb,
        };

        q.Add("a"); q.Add("b");
        var original = MergerSession.UserEdit(Snapshot(true, 1, null));
        var stack = new UndoStack<MergeEdl>(original);
        var states = new List<MergeEdl> { original };

        void Edit(MergeEdl s)
        {
            var u = MergerSession.UserEdit(s);
            var (label, gesture) = MergerSession.DescribeChange(stack.Current, u);
            Assert.True(stack.Apply(u, label, gesture));
            stack.EndGesture();
            states.Add(u);
        }

        q.Add("c"); Edit(Snapshot(true, 1, null));                                                  // 1 add clip
        q.Move(2, 0); Edit(Snapshot(true, 1, null));                                                // 2 reorder
        Edit(Snapshot(false, 1, null));                                                             // 3 scraper off
        Edit(Snapshot(false, 1.5, null));                                                           // 4 speed
        Edit(Snapshot(false, 1.5, new EdlThumbnail(new EdlAnchor(ids.Ids[1], 2_000_000))));         // 5 thumbnail
        var final = stack.Current;

        for (int i = 4; i >= 0; i--)
        {
            var back = stack.Undo()!;
            Assert.Equal(states[i], back);
            MergerSession.SyncQueue(q, ids, back.Clips);   // what the window does
            Assert.Equal(back.Clips.Select(c => c.Path), q);
            Assert.Equal(back.Clips.Select(c => c.ClipId), ids.Ids);
        }
        Assert.Equal(original, stack.Current);
        Assert.False(stack.CanUndo);

        for (int i = 1; i <= 5; i++)
        {
            var fwd = stack.Redo()!;
            Assert.Equal(states[i], fwd);
            MergerSession.SyncQueue(q, ids, fwd.Clips);
            Assert.Equal(fwd.Clips.Select(c => c.ClipId), ids.Ids);
        }
        Assert.Equal(final, stack.Current);

        // ReplaceCurrent never touches either branch.
        stack.Undo();
        stack.ReplaceCurrent(stack.Current with { });
        Assert.True(stack.CanRedo);
        Assert.Equal(4, stack.UndoCount);
    }

    /// <summary>D20 regression — the list used RemoveAt+Insert to reorder, which re-identifies the clip (effects lost).</summary>
    [Fact]
    public void D20_ReorderMustUseMove_RemoveInsertLosesTheClipId()
    {
        var (q, ids) = Live();
        q.Add("a"); q.Add("b"); q.Add("c");
        var idA = ids.Ids[0];

        q.Move(0, 2);
        Assert.Equal(idA, ids.Ids[2]);                // Move keeps identity

        q.RemoveAt(2); q.Insert(0, "a");              // the old reorder
        Assert.NotEqual(idA, ids.Ids[0]);             // a NEW id: effects keyed by the old one would be dropped
    }

    [Fact]
    public void SpeedSweep_IsOneUndoStep()
    {
        var start = new MergeEdl { Clips = new[] { C("a") } };
        var stack = new UndoStack<MergeEdl>(start);
        foreach (double s in new[] { 1.1, 1.2, 1.3, 1.4 })
        {
            var next = stack.Current with { BaseSpeed = s };
            var (label, gesture) = MergerSession.DescribeChange(stack.Current, next);
            stack.Apply(next, label, gesture);
        }
        Assert.Equal(1, stack.UndoCount);
        Assert.Equal(1.0, stack.Undo()!.BaseSpeed);
    }
}
