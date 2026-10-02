
using System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace FreeVideoStudio.App;

public partial class VideoMergerWindow
{
    private bool _isSyncingMasterVolume;

    private void OnGlobalMasterVolumeChanged(int masterVolumePercentage)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var slider = VolumeSliderCtl;
            if (slider != null && Math.Abs(slider.Value - masterVolumePercentage) > 0.5)
            {
                _isSyncingMasterVolume = true;
                try
                {
                    slider.Value = masterVolumePercentage;
                    var badge = this.FindControl<TextBlock>("VolumeBadgeText");
                    if (badge != null) badge.Text = $"{masterVolumePercentage}%";
                    var icon = this.FindControl<Avalonia.Controls.Shapes.Path>("VolumeSpeakerIcon");
                    if (icon != null)
                    {
                        icon.Data = masterVolumePercentage == 0
                            ? Geometry.Parse("M3,7 L6,7 L10,3 L10,13 L6,9 L3,9 Z M12,5 L16,13 M16,5 L12,13")
                            : Geometry.Parse("M3,7 L6,7 L10,3 L10,13 L6,9 L3,9 Z M13,5 A4,4 0 0,1 13,11 M16,2 A8,8 0 0,1 16,14");
                    }
                }
                finally
                {
                    _isSyncingMasterVolume = false;
                }
            }
        });

        if (_videoHost?.IpcClient != null)
        {
            _ = _videoHost.IpcClient.SetPreviewVolumeAsync(masterVolumePercentage);
        }
    }
}
