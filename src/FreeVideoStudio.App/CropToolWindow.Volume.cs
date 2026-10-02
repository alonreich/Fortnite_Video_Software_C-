
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

    private bool _isSyncingMasterVolume;
    private double _previousVolume = 100;

    private void WireUpVolumeSlider()
    {
        var volumeSlider = VolumeSliderCtl;
        var volumeBadgeText = VolumeBadgeTextCtl;
        var volumeSpeakerIcon = VolumeSpeakerIconCtl;

        if (volumeSlider != null && volumeBadgeText != null)
        {
            volumeSlider.Value = MpvIpcClient.GlobalMasterVolume;
            volumeBadgeText.Text = $"{MpvIpcClient.GlobalMasterVolume}%";

            volumeSlider.PropertyChanged += (s, e) =>
            {
                if (e.Property == Slider.ValueProperty && e.NewValue != null)
                {
                    if (_isSyncingMasterVolume) return;
                    int vol = Convert.ToInt32(e.NewValue);
                    volumeBadgeText.Text = $"{vol}%";
                    ApplyMasterVolume(vol);
                    if (volumeSpeakerIcon != null)
                    {
                        volumeSpeakerIcon.Data = vol == 0
                            ? Geometry.Parse("M3,7 L6,7 L10,3 L10,13 L6,9 L3,9 Z M12,5 L16,13 M16,5 L12,13")
                            : Geometry.Parse("M3,7 L6,7 L10,3 L10,13 L6,9 L3,9 Z M13,5 A4,4 0 0,1 13,11 M16,2 A8,8 0 0,1 16,14");
                    }
                }
            };

            var speakerHitBox = SpeakerHitBoxCtl;
            if (speakerHitBox != null)
            {
                speakerHitBox.Click += (s, e) => ToggleCropMute();
            }

            volumeSlider.PointerReleased += (s, e) =>
            {
                try
                {
                    new StateTransferStore(_paths).UpdatePropertiesSync(new System.Text.Json.Nodes.JsonObject { ["MainVolume"] = volumeSlider.Value });
                }
                catch (Exception ex)
                {
                    RuntimeLog.Swallowed(ex);
                }
            };
        }

        MpvIpcClient.GlobalMasterVolumeChanged += OnGlobalMasterVolumeChangedInCrop;
        this.Closed += (s, e) =>
        {
            MpvIpcClient.GlobalMasterVolumeChanged -= OnGlobalMasterVolumeChangedInCrop;
        };
    }

    private void ToggleCropMute()
    {
        var volumeSlider = VolumeSliderCtl;
        if (volumeSlider != null)
        {
            if (volumeSlider.Value > 0)
            {
                _previousVolume = volumeSlider.Value;
                volumeSlider.Value = 0;
            }
            else
            {
                volumeSlider.Value = _previousVolume > 0 ? _previousVolume : 100;
            }
        }
    }

    private void ApplyMasterVolume(int masterVolumePercentage)
    {
        MpvIpcClient.SetGlobalMasterVolume(masterVolumePercentage);
        if (_videoHost?.IpcClient != null)
        {
            _ = ApplyCurrentVolumeToMpvAsync();
        }
    }

    public async Task ApplyCurrentVolumeToMpvAsync()
    {
        if (_videoHost?.IpcClient != null)
        {
            int vol = MpvIpcClient.GlobalMasterVolume;
            await _videoHost.IpcClient.SetPropertyAsync("mute", vol == 0 ? "yes" : "no");
            await _videoHost.IpcClient.SetPreviewVolumeAsync(vol);
        }
    }

    private void OnGlobalMasterVolumeChangedInCrop(int masterVolumePercentage)
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
                    var badge = VolumeBadgeTextCtl;
                    if (badge != null) badge.Text = $"{masterVolumePercentage}%";
                    var icon = VolumeSpeakerIconCtl;
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
            _ = ApplyCurrentVolumeToMpvAsync();
        }
    }
}
