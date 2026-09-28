using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FortniteVideoSoftware.Core.Media;
using Xunit;

namespace FortniteVideoSoftware.Core.Tests;

/// <summary>LANES_01 — Video-Merger-Migration.md P5.1/P5.2 (T5.1a, T5.2a).</summary>
public class ProgressiveLanesTests
{
    private static LaneClip C(int i, double start, double end, double keepStart = 0)
        => new(i, $"c{i}.mp4", keepStart, keepStart + (end - start), start, end, 100 + i, 200 + i);

    [Fact]
    public void Plan_IsLeftToRight_SkipsSlivers_AndKeysByIdentity()
    {
        var clips = new[] { C(2, 20, 30), C(0, 0, 10, 0.1), C(1, 10, 10.001), C(3, 30, 40) };
        var tiles = LanePlanner.Plan(clips, 40, 800, 64, "film");

        Assert.Equal(new[] { 0, 2, 3 }, tiles.Select(t => t.ClipIndex));
        Assert.True(tiles.Zip(tiles.Skip(1)).All(p => p.First.X0 < p.Second.X0));
        Assert.Equal(0, tiles[0].X0, 6);
        Assert.Equal(200, tiles[0].X1, 6);
        Assert.Equal(10, tiles[0].Frames);         // LANECACHE_02: a fixed grid, 1 frame/s of a 10 s clip — not the width
        Assert.Equal(0.1, tiles[0].StartSec, 6);
        Assert.Equal(10, tiles[0].DurationSec, 6);

        // Same clip + window → same key at ANY width and ANY position (resize/reorder = cache hit); another file identity → new key.
        var again = LanePlanner.Plan(clips, 40, 1600, 64, "film");
        Assert.Equal(tiles[0].CacheKey, again[0].CacheKey);
        Assert.Equal(tiles[0].Frames, again[0].Frames);
        var moved = LanePlanner.Plan(new[] { clips[1] with { MergedStartSec = 30, MergedEndSec = 40 } }, 40, 800, 64, "film");
        Assert.Equal(tiles[0].CacheKey, moved[0].CacheKey);
        var touched = LanePlanner.Plan(new[] { clips[1] with { WriteTicks = 999 } }, 40, 800, 64, "film");
        Assert.NotEqual(tiles[0].CacheKey, touched[0].CacheKey);
    }

    [Fact]
    public void Plan_Waveform_KeyIgnoresWidth()
    {
        var clips = new[] { C(0, 0, 10) };
        var a = LanePlanner.Plan(clips, 10, 1000, 0, "wave")[0];
        var b = LanePlanner.Plan(clips, 10, 377, 0, "wave")[0];
        Assert.Equal(0, a.Frames);
        Assert.Equal(1000, a.WidthPx);
        Assert.Equal(a.CacheKey, b.CacheKey);
        Assert.NotEqual(a.CacheKey, LanePlanner.Plan(clips, 10, 1000, 64, "film")[0].CacheKey);
    }

    [Fact]
    public async Task T51a_Runner_StartsInOrder_BoundsParallelism()
    {
        var runner = new ProgressiveLaneRunner();
        var started = new ConcurrentQueue<int>();
        int live = 0, peak = 0;
        await runner.RunAsync(Enumerable.Range(0, 12).ToList(), async (i, gen, ct) =>
        {
            started.Enqueue(i);
            int now = Interlocked.Increment(ref live);
            InterlockedMax(ref peak, now);
            await Task.Delay(15, ct);
            Interlocked.Decrement(ref live);
        }, maxParallel: 2);

        Assert.Equal(Enumerable.Range(0, 12), started);
        Assert.True(peak <= 2, $"peak {peak}");
    }

    [Fact]
    public async Task T51a_Runner_NewRunCancelsTheStaleOne()
    {
        var runner = new ProgressiveLaneRunner();
        var gate = new TaskCompletionSource();
        var oldDone = new ConcurrentBag<int>();
        var first = runner.RunAsync(Enumerable.Range(0, 10).ToList(), async (i, gen, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            oldDone.Add(i);
        }, 2);
        await Task.Delay(30);

        var newDone = new ConcurrentBag<int>();
        int newGen = 0;
        var second = runner.RunAsync(new[] { 100, 101 }, (i, gen, ct) => { newGen = gen; newDone.Add(i); return Task.CompletedTask; }, 2);
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Empty(oldDone);                 // stale work never finished
        Assert.Equal(2, newDone.Count);
        Assert.Equal(runner.Generation, newGen);
    }

    [Fact]
    public async Task T52a_OneRunnerServesBothLanes_FailuresDoNotStopTheRun()
    {
        var runner = new ProgressiveLaneRunner();
        var done = new ConcurrentBag<int>();
        await runner.RunAsync(new[] { 0, 1, 2 }, (i, gen, ct) =>
        {
            if (i == 1) throw new InvalidOperationException("ffmpeg failed");
            done.Add(i);
            return Task.CompletedTask;
        });
        Assert.Equal(new[] { 0, 2 }, done.OrderBy(x => x));
    }

    [Fact]
    public void LaneCache_EvictsLeastRecentlyUsed()
    {
        var evicted = new List<string>();
        var cache = new LaneCache<string>(2, v => evicted.Add(v));
        cache.Put("a", "A");
        cache.Put("b", "B");
        Assert.True(cache.TryGet("a", out _));   // a is now most recent
        cache.Put("c", "C");
        Assert.Equal(new[] { "B" }, evicted);
        Assert.False(cache.TryGet("b", out _));
        Assert.Equal(2, cache.Count);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = target) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    [Fact]
    public void ThumbGrid_FixedPerClip_SlotsPickTheNearestFrame()
    {
        Assert.Equal(1, ThumbGrid.FrameCount(0.4));
        Assert.Equal(80, ThumbGrid.FrameCount(80));          // 1 frame per second
        Assert.Equal(90, ThumbGrid.FrameCount(3600));        // capped
        Assert.Equal(3, ThumbGrid.Slots(200, 64));
        Assert.Equal(1, ThumbGrid.Slots(5, 64));
        Assert.Equal(new[] { 1, 5, 8 }, ThumbGrid.Pick(10, 3));   // slot centres 1.67, 5, 8.33 → floor
        Assert.Equal(new[] { 0, 0, 1, 1 }, ThumbGrid.Pick(2, 4));   // more slots than frames: repeats, never out of range
    }

    [Fact]
    public void WaveformPeaks_ReduceAndRoundTrip()
    {
        short[] samples = { 0, 16384, -32768, 100, 0, -8192 };
        var p = WaveformPeaks.Reduce(samples, 3);
        Assert.Equal(new[] { 0.5f, 1.0f, 0.25f }, p);
        Assert.Equal(p, WaveformPeaks.FromBytes(WaveformPeaks.ToBytes(p)));
        Assert.Null(WaveformPeaks.FromBytes(new byte[] { 1, 2, 3 }));
        Assert.Equal(400, WaveformPeaks.PeakCount(10));
        Assert.Equal(WaveformPeaks.MaxPeaks, WaveformPeaks.PeakCount(100000));
    }
}
