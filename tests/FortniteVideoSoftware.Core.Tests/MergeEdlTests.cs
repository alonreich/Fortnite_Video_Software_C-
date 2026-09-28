using System;
using FortniteVideoSoftware.Core.Media;
using Xunit;

namespace FortniteVideoSoftware.Core.Tests;

/// <summary>MERGEEDL_01 — Video-Merger-Migration.md P2.1 (T2.1a equality, T2.1b JSON round-trip).</summary>
public class MergeEdlTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static MergeEdl Sample() => new()
    {
        Clips =
        [
            new EdlClip
            {
                ClipId = A, Path = @"C:\clips\a.mp4", SizeBytes = 123, LastWriteUtcTicks = 456, DurationUs = 10_000_000,
                Timing = new ExportTiming(60, 1, 6, 60, null),
                Effects = new EdlEffects
                {
                    Speed = [new EdlSpeedSegment(1_000_000, 2_000_000, 2.0, new EdlZoom(10, 20, 640, 360, true, 1_200_000, 1_800_000))],
                    Freezes = [new EdlFreeze(3_000_000, 1.5)],
                    Memes = [new EdlMeme("meme0", @"C:\memes\x.png", EdlMemePlacement.AtEnd, 0, 4.0)],
                },
            },
            new EdlClip { ClipId = B, Path = @"C:\clips\b.mp4", InUs = 500_000, OutUs = 9_000_000 },
        ],
        ScraperEnabled = false,
        BaseSpeed = 1.25,
        Thumbnail = new EdlThumbnail(new EdlAnchor(B, 4_000_000)),
        Music = new EdlMusic
        {
            FilePaths = [@"C:\m\song.mp3"], DurationsSec = [180.5], OffsetSec = 12,
            Start = new EdlAnchor(A, 2_500_000), End = new EdlAnchor(B, 8_000_000), Loop = true,
        },
    };

    [Fact]
    public void StructuralEquality_IgnoresListInstances()   // T2.1a
    {
        var x = Sample();
        var y = Sample();
        Assert.NotSame(x.Clips, y.Clips);
        Assert.Equal(x, y);
        Assert.Equal(x.GetHashCode(), y.GetHashCode());
    }

    [Fact]
    public void AnyChange_BreaksEquality()   // T2.1a
    {
        var x = Sample();
        Assert.NotEqual(x, x with { BaseSpeed = 1.3 });
        Assert.NotEqual(x, x with { Clips = [x.Clips[1], x.Clips[0]] });   // reorder
        var c0 = x.Clips[0];
        var moved = c0 with { Effects = c0.Effects with { Freezes = [new EdlFreeze(3_000_001, 1.5)] } };
        Assert.NotEqual(x, x with { Clips = [moved, x.Clips[1]] });
        Assert.NotEqual(x, x with { Music = x.Music! with { Start = new EdlAnchor(A, 2_500_001) } });
    }

    [Fact]
    public void Json_RoundTrip_IsLossless()   // T2.1b
    {
        var x = Sample();
        string json = x.ToJson();
        Assert.Contains("\"AtEnd\"", json);   // enums are readable strings
        var back = MergeEdl.FromJson(json);
        Assert.NotNull(back);
        Assert.Equal(x, back);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    public void Json_Garbage_IsNull(string? json) => Assert.Null(MergeEdl.FromJson(json));

    [Fact]
    public void SameFileTwice_GetsTwoIdentities()
    {
        var a = new EdlClip { Path = "x.mp4" };
        var b = new EdlClip { Path = "x.mp4" };
        Assert.NotEqual(a.ClipId, b.ClipId);
        var edl = new MergeEdl { Clips = [a, b] };
        Assert.Equal(1, edl.IndexOf(b.ClipId));
        Assert.Equal(-1, edl.IndexOf(Guid.Empty));
    }

    /// <summary>
    /// EDLNULL_01 regression — an autosave written BEFORE a field existed (here: no "Cuts", no "Effects",
    /// empty "Effects") must load with empty lists, compare equal, and never throw. The source-generated
    /// reader does not run property initializers; before the fix these came back null, every capture
    /// threw, the Merger's autosave froze on an old queue and IsEmpty crashed the app.
    /// </summary>
    [Fact]
    public void OldAutosaveShapes_LoadWithEmptyLists_AndCompare()
    {
        const string json = "{\"Version\":1,\"Clips\":[" +
            "{\"Path\":\"a.mp4\",\"Effects\":{\"Speed\":[],\"Freezes\":[],\"Memes\":[]}}," +
            "{\"Path\":\"b.mp4\",\"Effects\":{}}," +
            "{\"Path\":\"c.mp4\"}]," +
            "\"Music\":{\"OffsetSec\":1}}";
        var edl = MergeEdl.FromJson(json)!;
        Assert.All(edl.Clips, c => Assert.True(c.Effects.IsEmpty));
        Assert.Empty(edl.Clips[0].Effects.Cuts);
        Assert.Empty(edl.Music!.FilePaths);
        Assert.All(edl.Clips, c => Assert.NotEqual(Guid.Empty, c.ClipId));   // a missing id gets a fresh one, never Guid.Empty
        Assert.True(edl.ScraperEnabled);                                       // missing scalars take their DEFAULTS, not 0/false
        Assert.Equal(1.0, edl.BaseSpeed);
        Assert.Equal(1.0, edl.Music.MusicVolume);
        Assert.True(edl.Music.Ducking);
        Assert.Equal(edl, MergeEdl.FromJson(edl.ToJson()));
        Assert.True(edl.Clips[2].Effects.Equals(EdlEffects.None));
        Assert.Equal(MergeEdl.Empty, MergeEdl.FromJson("{\"Version\":1}") ?? MergeEdl.Empty);
    }
}
