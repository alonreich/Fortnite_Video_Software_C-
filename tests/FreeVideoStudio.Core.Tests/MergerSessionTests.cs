using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>MERGESESSION_01 — Video-Merger-Migration.md P3.2 (T3.2a–c).</summary>
public class MergerSessionTests
{
    private static readonly Dictionary<string, (long, long)> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"C:\c\a.mp4"] = (1000, 11),
        [@"C:\c\b.mp4"] = (2000, 22),
        [@"C:\c\c.mp4"] = (3000, 33),
    };

    private static (long Size, long WriteTicks)? FileId(string p) => Files.TryGetValue(p, out var v) ? v : null;

    private static MergeClipInfo? Analysis(string p) => p switch
    {
        @"C:\c\a.mp4" => new MergeClipInfo(p, 10, 0.1, true, 1080, 1920, true) { Timing = new ExportTiming(60, 1, 6, 60, 60) },
        @"C:\c\b.mp4" => new MergeClipInfo(p, 8, 0.0956, true, 1080, 1920, true) { Timing = new ExportTiming(60, 1, 6, null, 60) },
        @"C:\c\c.mp4" => new MergeClipInfo(p, 6, 0, true, 1920, 1080, true),
        _ => null,
    };

    private static MergedTimeline Timeline(IReadOnlyList<string> paths, bool scraper, bool thumb)
    {
        var src = new List<MergeClipSource>();
        foreach (var p in paths) { var i = Analysis(p)!; src.Add(new MergeClipSource(p, i.DurationSec, i.IntroSec)); }
        return MergedTimeline.Build(src, scraper, thumb);
    }

    private static (MergerUiState, ClipIdList) State()
    {
        var ids = new ClipIdList();
        ids.Insert(0, 3);
        var paths = new[] { @"C:\c\a.mp4", @"C:\c\b.mp4", @"C:\c\c.mp4" };
        return (new MergerUiState
        {
            Paths = paths,
            Ids = ids.Ids,
            ScraperEnabled = true,
            BaseSpeed = 1.5,
            ThumbPath = @"C:\c\b.mp4",
            ThumbSourceSec = 3.25,
            Music = new MergerMusicState(new[] { @"C:\m\song.mp3" }, new[] { 200.0 }, 12, 12.0, 20.0, 0.8, 0.6, false, true, true),
        }, ids);
    }

    [Fact]
    public void ClipIdList_FollowsCollectionEvents()
    {
        var ids = new ClipIdList();
        ids.Insert(0, 3);
        var (a, b, c) = (ids.Ids[0], ids.Ids[1], ids.Ids[2]);
        ids.Move(2, 0, 1);
        Assert.Equal(new[] { c, a, b }, ids.Ids);
        ids.Remove(1, 1);
        Assert.Equal(new[] { c, b }, ids.Ids);
        ids.Insert(1, 1);
        Assert.Equal(3, ids.Count);
        Assert.NotEqual(a, ids.Ids[1]);
        ids.Replace(0, 1);
        Assert.NotEqual(c, ids.Ids[0]);
        Assert.True(ids.EnsureCount(5));
        Assert.Equal(5, ids.Count);
        ids.Reset(0);
        Assert.Equal(0, ids.Count);
    }

    [Fact]
    public void T32a_CaptureThenRestore_GivesTheSameState()
    {
        var (s, ids) = State();
        var tl = Timeline(s.Paths, s.ScraperEnabled, thumb: true);
        MergeEdl edl = MergerSession.Capture(s, Analysis, FileId, tl, previous: null);

        Assert.Equal(3, edl.Clips.Count);
        Assert.Equal(ids.Ids, new[] { edl.Clips[0].ClipId, edl.Clips[1].ClipId, edl.Clips[2].ClipId });
        Assert.Equal(95_600, edl.Clips[1].IntroCutUs);
        Assert.Equal(0, edl.Clips[2].IntroCutUs);
        Assert.Equal(new EdlAnchor(ids.Ids[1], 3_250_000), edl.Thumbnail!.At);
        Assert.Equal(1.5, edl.BaseSpeed);

        MergeEdl back = MergeEdl.FromJson(edl.ToJson())!;
        Assert.Equal(edl, back);
        var plan = MergerSession.Plan(back, FileId);
        Assert.Empty(plan.Missing);
        Assert.Empty(plan.Changed);
        Assert.Equal(edl, plan.Edl);

        var restoredIds = new ClipIdList();
        restoredIds.Seed(new[] { back.Clips[0].ClipId, back.Clips[1].ClipId, back.Clips[2].ClipId });
        var music = MergerSession.MusicFromEdl(back.Music, tl, restoredIds.Ids)!;
        Assert.Equal(s.Music!.StartMergedSec, music.StartMergedSec, 3);
        Assert.Equal(s.Music.EndMergedSec, music.EndMergedSec, 3);
        Assert.Equal(s.Music.OffsetSec, music.OffsetSec);
        Assert.Equal(0.8, music.MusicVolume);

        var s2 = s with { Ids = restoredIds.Ids, Music = music };
        Assert.Equal(edl, CaptureAgain(s2, tl, back));
    }

    private static MergeEdl CaptureAgain(MergerUiState s, MergedTimeline tl, MergeEdl prev)
        => MergerSession.Capture(s, Analysis, FileId, tl, prev);

    [Fact]
    public void Capture_CarriesEffectsAndPendingAnalysisForward_ByClipId()
    {
        var (s, _) = State();
        var tl = Timeline(s.Paths, true, true);
        var first = MergerSession.Capture(s, Analysis, FileId, tl, null);
        var fx = new EdlEffects { Freezes = new[] { new EdlFreeze(2_000_000, 1) } };
        var withFx = first with { Clips = new[] { first.Clips[0], first.Clips[1] with { Effects = fx }, first.Clips[2] } };

        var reordered = s with { Paths = new[] { s.Paths[2], s.Paths[1], s.Paths[0] }, Ids = new[] { s.Ids[2], s.Ids[1], s.Ids[0] } };
        var next = MergerSession.Capture(reordered, _ => null, FileId, null, withFx);

        Assert.Equal(fx, next.Clips[1].Effects);
        Assert.Equal(first.Clips[0].DurationUs, next.Clips[2].DurationUs);
        Assert.Equal(first.Music!.Start, next.Music!.Start);
    }

    [Fact]
    public async Task T32b_SimulatedCloseAndReopen_RestoresTheIdenticalEdl()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fvs-merger-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, "merger_session.json");
        try
        {
            var (s, _) = State();
            var edl = MergerSession.Capture(s, Analysis, FileId, Timeline(s.Paths, true, true), null);

            using (var a = new MergerAutosaveStore(file, TimeSpan.FromMilliseconds(30)))
            {
                a.Schedule(edl with { BaseSpeed = 3 });
                a.Schedule(edl);
                await a.FlushAsync();
            }

            using var b = new MergerAutosaveStore(file);
            Assert.Equal(edl, b.Load());

            b.Schedule(MergeEdl.Empty);
            await b.FlushAsync();
            Assert.Null(b.Load());
            b.Schedule(edl);
            await b.FlushAsync();
            Assert.Equal(edl, b.Load());

            File.WriteAllText(file, "{ garbage");
            Assert.Null(b.Load());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void RestoreDialog_NamesEveryMissingFileAndItsFolder()
    {
        string text = MergerSession.DescribeRestoreProblems(
            new[] { @"C:\Users\alon\Videos\a.mp4", @"D:\clips\b.mp4" }, new[] { @"C:\x\c.mp4" }, keptCount: 5);
        Assert.Contains("2 clips from your last session are no longer on disk", text);
        Assert.Contains("a.mp4", text);
        Assert.Contains("b.mp4", text);
        Assert.Contains("1 clip changed on disk", text);
        Assert.Contains("Continue with the 5 clip(s)", text);

        var many = Enumerable.Range(0, 20).Select(i => $"clip{i}.mp4").ToArray();
        Assert.Contains("and 8 more", MergerSession.DescribeRestoreProblems(many, Array.Empty<string>(), 0));
        Assert.Contains("Nothing from the last session can be restored.", MergerSession.DescribeRestoreProblems(many, Array.Empty<string>(), 0));
    }

    [Fact]
    public void T32c_MissingAndChangedFiles_AreFlagged_NeverThrow()
    {
        var (s, ids) = State();
        var edl = MergerSession.Capture(s, Analysis, FileId, Timeline(s.Paths, true, true), null);

        (long, long)? Changed(string p) => p switch
        {
            @"C:\c\a.mp4" => (1000, 11),
            @"C:\c\b.mp4" => null,          // gone
            @"C:\c\c.mp4" => (3001, 34),    // re-encoded
            _ => null,
        };
        var plan = MergerSession.Plan(edl, Changed);

        Assert.Equal(new[] { @"C:\c\b.mp4" }, plan.Missing);
        Assert.Equal(new[] { @"C:\c\c.mp4" }, plan.Changed);
        Assert.Equal(2, plan.Edl.Clips.Count);
        Assert.Null(plan.Edl.Thumbnail);
        Assert.Equal(0, plan.Edl.Clips[1].DurationUs);
        Assert.Equal(3001, plan.Edl.Clips[1].SizeBytes);
        Assert.Equal(ids.Ids[0], plan.Edl.Music!.Start.ClipId);

        var none = MergerSession.Plan(edl, _ => null);
        Assert.Empty(none.Edl.Clips);
        Assert.Null(none.Edl.Music);
    }
}
