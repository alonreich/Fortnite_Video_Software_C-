// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.Core.Ipc;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class CropToolWindow
{
    public double CropVolume { get; set; } = 100;

    /// <summary>
    /// VOLSHARED_01 — the shared master rack. The old copy here sent every change to mpv TWICE
    /// (directly and again through its own event handler), juggled mpv's `mute` property, kept a
    /// private pre-mute level and saved synchronously on the UI thread on mouse release only.
    /// </summary>
    private void WireUpVolumeSlider()
    {
        Infrastructure.MasterVolumeUi.Bind(this, VolumeSliderCtl, VolumeBadgeTextCtl, VolumeSpeakerIconCtl, SpeakerHitBoxCtl,
            () => { _ = ApplyCurrentVolumeToMpvAsync(); });
    }

    public async Task ApplyCurrentVolumeToMpvAsync()
    {
        if (_videoHost?.IpcClient != null)
        {
            await _videoHost.IpcClient.ApplyPreviewGainAsync();
        }
    }
}
