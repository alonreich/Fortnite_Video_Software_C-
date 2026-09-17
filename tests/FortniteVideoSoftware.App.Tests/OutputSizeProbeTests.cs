using System.Diagnostics;
using FortniteVideoSoftware.App.Services;
using FortniteVideoSoftware.App.ViewModels;
using FortniteVideoSoftware.Core.Media;
using Xunit;

namespace FortniteVideoSoftware.App.Tests;

public sealed class OutputSizeProbeTests
{
    [Fact]
    public async Task RealMediaProbeRecoversFromFailureCachesAndInvalidatesChangedFiles()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "FortniteVideoSoftware.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        string ffmpeg = Path.Combine(root.FullName, "binaries", "ffmpeg.exe");
        string ffprobe = Path.Combine(root.FullName, "binaries", "ffprobe.exe");
        Assert.True(File.Exists(ffmpeg) && File.Exists(ffprobe), "The repository's media binaries are required.");
        string clip = Path.Combine(Path.GetTempPath(), $"fvs-size-estimate-{Guid.NewGuid():N}.mp4");
        var estimator = new OutputSizeEstimator(() => ffprobe);
        try
        {
            await File.WriteAllTextAsync(clip, "not a video");
            Assert.Null(await estimator.ReadMediaAsync(clip, CancellationToken.None));

            var start = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string arg in new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                "-t", "2", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-b:a", "192k", clip })
                start.ArgumentList.Add(arg);
            var encoded = await AsyncProcessRunner.RunAsync(start, TimeSpan.FromSeconds(20));
            Assert.True(encoded.ExitCode == 0, encoded.StandardError);

            var media = await estimator.ReadMediaAsync(clip, CancellationToken.None);
            Assert.NotNull(media);
            Assert.InRange(media.Duration, 1.9, 2.1);
            Assert.Equal(320, media.Width);
            Assert.Equal(180, media.Height);
            Assert.Equal(30, media.Fps);
            Assert.True(media.VideoKbps > 0 && media.AudioKbps > 0);
            Assert.Same(media, await estimator.ReadMediaAsync(clip, CancellationToken.None));

            File.SetLastWriteTimeUtc(clip, File.GetLastWriteTimeUtc(clip).AddSeconds(-10));
            var changed = await estimator.ReadMediaAsync(clip, CancellationToken.None);
            Assert.NotNull(changed);
            Assert.NotSame(media, changed);

            var request = new MainSizeRequest(clip, 0, 0, 0, 1, [], [], [], null, false, QualityLadder.DefaultIndex);
            var main = await estimator.EstimateMainAsync(request, CancellationToken.None);
            Assert.True(main.Megabytes > 0);
            Assert.InRange(main.DurationSeconds, 2.0, 2.2);
            var merger = await estimator.EstimateMergerAsync(new([clip], 2, 100), CancellationToken.None);
            Assert.Equal(media.Duration / 2, merger.DurationSeconds, 6);
            Assert.True(merger.Megabytes > 0);
            var missingQueue = await estimator.EstimateMergerAsync(new([clip, clip + ".missing"], 1, 100), CancellationToken.None);
            Assert.Null(missingQueue.Megabytes);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => estimator.ReadMediaAsync(clip, cancellation.Token));
            File.Delete(clip);
            Assert.Null(await estimator.ReadMediaAsync(clip, CancellationToken.None));
        }
        finally { if (File.Exists(clip)) File.Delete(clip); }
    }
}
