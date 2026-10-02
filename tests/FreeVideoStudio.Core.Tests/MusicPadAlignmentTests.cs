using System.Collections.Generic;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>MUSICPAD_01 — the music meets the video on the frames the preview showed, with fade pads on.</summary>
public class MusicPadAlignmentTests
{
    [Fact]
    public void MidVideoMusic_MovesByTheLeadPad_Only()
    {
        var t = new List<MusicTrack> { new("song.mp3", 12.0, 2.727, 1.818, true) };
        var a = MusicPadAlignment.Align(t, 1.0, 0.8, 7.255, leadFadeIn: false, tailFadeOut: true);
        Assert.Equal(2.818, a[0].TimelineStartDelay, 6);
        Assert.Equal(12.0, a[0].Offset, 6);
        Assert.Equal(2.727, a[0].Duration, 6);
    }

    [Fact]
    public void LeadIn_PreRollsIntoTheFadeIn_KeepingTheSongInSync()
    {
        var t = new List<MusicTrack> { new("a.mp3", 12.0, 3.0, 0, true), new("b.mp3", 0, 2.0, 0, true) };
        var a = MusicPadAlignment.Align(t, 1.0, 0, 10, leadFadeIn: true, tailFadeOut: false);
        Assert.Equal(0, a[0].TimelineStartDelay, 6);
        Assert.Equal(11.0, a[0].Offset, 6);
        Assert.Equal(4.0, a[0].Duration, 6);
        Assert.Equal(4.0, a[1].TimelineStartDelay + a[0].Duration, 6);

        var b = MusicPadAlignment.Align(new List<MusicTrack> { new("a.mp3", 0.3, 3.0, 0, true) }, 1.0, 0, 10, true, false);
        Assert.Equal(0.7, b[0].TimelineStartDelay, 6);
        Assert.Equal(0, b[0].Offset, 6);
        Assert.Equal(3.3, b[0].Duration, 6);
    }

    [Fact]
    public void TailOut_RunsOnThroughTheFadeOut_NoPadsNoChange()
    {
        var t = new List<MusicTrack> { new("a.mp3", 0, 5.0, 0.455, true) };
        var a = MusicPadAlignment.Align(t, 1.0, 0.8, 7.255, leadFadeIn: false, tailFadeOut: true);
        Assert.Equal(1.455, a[0].TimelineStartDelay, 6);
        Assert.Equal(5.8, a[0].Duration, 6);

        Assert.Equal(t, MusicPadAlignment.Align(t, 0, 0, 5.455, true, true));
    }
}
