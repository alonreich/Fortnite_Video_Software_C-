using System.Collections.Concurrent;
using FortniteVideoSoftware.App.Services;
using FortniteVideoSoftware.App.ViewModels;
using FortniteVideoSoftware.Core.Media;
using Xunit;

namespace FortniteVideoSoftware.App.Tests;

public sealed class OutputSizeEstimateTests
{
    private static MainSizeRequest Request() => new("clip.mp4", 60000, 0, 60000, 1, [], [], [], null, false, QualityLadder.DefaultIndex);
    private static EstimateMedia Source(string path = "clip.mp4", double duration = 60, double rate = 10000)
        => new(path, duration, rate, 192, 1920, 1080, 60);

    [Theory]
    [InlineData(0, "0.0 MB")]
    [InlineData(0.5, "0.5 MB")]
    [InlineData(100, "100 MB")]
    [InlineData(1024, "1.0 GB")]
    [InlineData(1048576, "1.0 TB")]
    public void FormatsReadableUnits(double mb, string expected)
        => Assert.Equal(expected, OutputFileSize.FormatMegabytes(mb));

    [Fact]
    public void MissingSizeIsNotReportedAsZero()
    {
        Assert.Equal("—", OutputFileSize.FormatMegabytes(null));
        Assert.Equal("—", OutputFileSize.FormatMegabytes(double.NaN));
    }

    [Fact]
    public void SpeedCutsFreezeAndMemeUseTheFinishedTimeline()
    {
        var request = Request() with
        {
            StartMs = 10000, EndMs = 50000,
            Segments = [new(20000, 30000, 0.5), new(40000, 42500, 0)],
            Cuts = [new(32000, 37000)],
            Memes = [new("meme.mp4", 15, 4, "meme1")]
        };
        var estimate = OutputSizeEstimator.CalculateMain(request, Source(), [Source("meme.mp4", 4)]);
        Assert.Equal(51.6, estimate.DurationSeconds, 6);
        Assert.Equal(estimate.TargetMegabytes, estimate.Megabytes);
        var quick = OutputSizeEstimator.QuickMainEstimate(request)!;
        Assert.Equal(estimate.Megabytes, quick.Megabytes);
        Assert.Equal(estimate.DurationSeconds, quick.DurationSeconds);
    }

    [Fact]
    public void FreezeAddsTimeAndCostsLessThanMovingFootage()
    {
        var request = Request();
        var plain = OutputSizeEstimator.CalculateMain(request, Source(), []);
        var freeze = OutputSizeEstimator.CalculateMain(request with { Segments = [new(10000, 13000, 0)] }, Source(), []);
        var longer = OutputSizeEstimator.CalculateMain(request with { EndMs = 63000 }, Source(), []);
        Assert.Equal(63.1, freeze.DurationSeconds, 6);
        Assert.True(freeze.Megabytes > plain.Megabytes);
        Assert.True(freeze.Megabytes < longer.Megabytes);
    }

    [Fact]
    public void UnmarkedEndUsesMetadataWhenPlayerDurationIsNotReady()
    {
        var request = Request() with { DurationMs = 0, StartMs = 10000, EndMs = 0 };
        Assert.Null(OutputSizeEstimator.QuickMainEstimate(request));
        var estimate = OutputSizeEstimator.CalculateMain(request, Source(), []);
        Assert.Equal(50.1, estimate.DurationSeconds, 6);
        Assert.True(estimate.Megabytes > 0);
        Assert.Equal(0, request.EndMs);
        var markedEnd = OutputSizeEstimator.CalculateMain(request with { EndMs = 20000 }, Source(), []);
        Assert.Equal(10.1, markedEnd.DurationSeconds, 6);
    }

    [Fact]
    public void OriginalHasAPredictionWithoutAnExportSizeCap()
    {
        var request = Request() with { Quality = QualityLadder.OriginalIndex };
        var estimate = OutputSizeEstimator.CalculateMain(request, Source(), []);
        Assert.True(estimate.Megabytes > 0);
        Assert.Null(estimate.TargetMegabytes);
        Assert.Null(OutputSizeEstimator.QuickMainEstimate(request));
        var faster = OutputSizeEstimator.CalculateMain(request with { Speed = 2 }, Source(), []);
        Assert.True(faster.Megabytes < estimate.Megabytes);
    }

    [Fact]
    public void TrimQualityAndPortraitEachUpdateSize()
    {
        var request = Request();
        var original = OutputSizeEstimator.CalculateMain(request, Source(), []);
        var shorter = OutputSizeEstimator.CalculateMain(request with { StartMs = 30000 }, Source(), []);
        var lower = OutputSizeEstimator.CalculateMain(request with { Quality = 0 }, Source(), []);
        var portrait = OutputSizeEstimator.CalculateMain(request with { Portrait = true }, Source(), []);
        Assert.True(shorter.Megabytes < original.Megabytes);
        Assert.True(lower.Megabytes < original.Megabytes);
        Assert.True(portrait.Megabytes < original.Megabytes);
    }

    [Fact]
    public void MergerUsesDurationWeightedVideoAndOneSoundtrack()
    {
        EstimateMedia[] sources = [Source("a", 10, 6000), Source("b", 30, 12000)];
        var estimate = OutputSizeEstimator.CalculateMerger(sources, 1, 100);
        Assert.Equal(40, estimate.DurationSeconds);
        Assert.Equal(10500, estimate.VideoKbps);
        double expectedMb = (10500 + 192) * 1000.0 / 8 * 40 / (1024 * 1024) * 1.01;
        Assert.Equal(expectedMb, estimate.Megabytes!.Value, 6);
        var faster = OutputSizeEstimator.CalculateMerger(sources, 2, 100);
        Assert.Equal(20, faster.DurationSeconds);
        Assert.Equal(estimate.Megabytes.Value / 2, faster.Megabytes!.Value, 6);
        Assert.Equal(estimate.Megabytes, OutputSizeEstimator.CalculateMerger(sources.Reverse().ToArray(), 1, 100).Megabytes);
    }

    [Fact]
    public void MergerQualityFollowsTheEncoderCurve()
    {
        var sources = new[] { Source() };
        double previous = 0;
        for (int quality = 5; quality <= 100; quality += 5)
        {
            var estimate = OutputSizeEstimator.CalculateMerger(sources, 1, quality);
            Assert.True(estimate.Megabytes > previous);
            previous = estimate.Megabytes!.Value;
        }
    }

    [Fact]
    public async Task BurstEditsKeepOneWorkerAndPublishOnlyNewestResult()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource<OutputSizeEstimate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var values = new ConcurrentQueue<double?>();
        int active = 0, maxActive = 0;
        using var worker = new LatestEstimateWorker<int>(async (input, token) =>
        {
            int count = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, count);
            if (input == 1) { started.SetResult(); await release.Task.WaitAsync(token); }
            Interlocked.Decrement(ref active);
            return new(input, input);
        }, estimate => { values.Enqueue(estimate.Megabytes); published.TrySetResult(estimate); }, action => action());
        worker.Request(1);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for (int i = 2; i <= 100; i++) worker.Request(i);
        release.SetResult();
        Assert.Equal(100, (await published.Task.WaitAsync(TimeSpan.FromSeconds(3))).Megabytes);
        Assert.Equal(1, maxActive);
        Assert.Equal(new double?[] { 100 }, values.ToArray());
        worker.Dispose();
        await worker.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task QuickResultArrivesBeforeRefinement()
    {
        var quick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var worker = new LatestEstimateWorker<int>(async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return new(12, 1);
        }, result => { if (result.Megabytes == 10) quick.TrySetResult(); else refined.TrySetResult(); },
            action => action(), _ => new(10, 1));
        worker.Request(1);
        await quick.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(refined.Task.IsCompleted);
        release.SetResult();
        await refined.Task.WaitAsync(TimeSpan.FromSeconds(3));
        worker.Dispose();
        await worker.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ClosingCancelsActiveWorkAndRejectsQueuedUiCallbacks()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = new ConcurrentQueue<Action>();
        bool published = false;
        using var worker = new LatestEstimateWorker<int>(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new(20, 1);
        }, _ => published = true, callbacks.Enqueue, _ => new(10, 1));
        worker.Request(1);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        worker.Dispose();
        await worker.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        while (callbacks.TryDequeue(out var callback)) callback();
        worker.Request(2); // Safe after disposal; never starts another worker.
        Assert.False(published);
    }
}
