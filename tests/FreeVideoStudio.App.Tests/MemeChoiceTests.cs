// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.IO;
using CornerMemeFrames = FreeVideoStudio.App.Infrastructure.CornerMemeFrames;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>MEMEMODE_01 — the meme popup's choice half (UI-MEMESELECT).</summary>
public sealed class MemeChoiceTests
{
    [Fact]
    public void Defaults_AreFullScreen_BottomRight_Medium_WithSound()
    {
        var vm = new MemeChoiceViewModel();
        Assert.True(vm.IsFullScreen);
        Assert.False(vm.IsCorner);
        Assert.True(vm.IsBottomRight);
        Assert.True(vm.IsMedium);
        Assert.True(vm.PlaySound);
        Assert.False(vm.HasMeme);
    }

    [Fact]
    public void FullScreenCard_SaysHowMuchLongerTheVideoGets()
    {
        var vm = new MemeChoiceViewModel { DurationSec = 4.0 };
        Assert.Equal("Video becomes +4.0 sec longer", vm.FullScreenLengthText);
        vm.DurationSec = 2.46;
        Assert.Equal("Video becomes +2.5 sec longer", vm.FullScreenLengthText);
    }

    [Fact]
    public void Commands_SetEveryProperty_AndApplyCarriesThemOntoThePlacement()
    {
        var vm = new MemeChoiceViewModel();
        vm.CornerCommand.Execute(null);
        vm.TopLeftCommand.Execute(null);
        vm.LargeCommand.Execute(null);
        vm.PlaySound = false;
        Assert.True(vm.IsCorner && vm.IsTopLeft && vm.IsLarge);

        var placed = vm.ApplyTo(new MemePlacement("m.mp4", 3, 4, "meme0"));
        Assert.Equal((MemePresentationMode.CornerOverlay, MemeOverlayCorner.TopLeft, MemeOverlaySize.Large, false),
            (placed.Mode, placed.Corner, placed.Size, placed.PlaySound));
        Assert.Equal(0.0, placed.OutputDurationSec);

        var again = new MemeChoiceViewModel();
        again.LoadFrom(placed);
        Assert.True(again.IsCorner && again.IsTopLeft && again.IsLarge && !again.PlaySound);
        Assert.Equal(4.0, again.DurationSec);
    }

    [Fact]
    public void PreviewFrameSplitter_SeparatesConcatenatedPngs()
    {
        byte[] Png(byte marker) => new byte[] { 0x89, (byte)'P', marker, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 1, 2, 3, 4 };
        var stream = new MemoryStream();
        stream.Write(Png(7));
        stream.Write(Png(9));
        var parts = new System.Collections.Generic.List<byte[]>(CornerMemeFrames.SplitPngStream(stream.ToArray()));
        Assert.Equal(2, parts.Count);
        Assert.Equal(7, parts[0][2]);
        Assert.Equal(9, parts[1][2]);
    }
}
