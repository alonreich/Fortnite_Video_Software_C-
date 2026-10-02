
using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;

namespace FreeVideoStudio.App.Controls;

public partial class SettingsWindow : Window
{
    private string _pendingGeminiApiKey = "";
    private string _pendingGeminiModelName = "gemini-2.5-flash";
    private double _pendingAiZoomBaseScale = 2.2;
    private double _pendingAiZoomMinScale = 1.3;
    private bool _pendingAiZoomAvoidHud = true;
    private double _pendingAiZoomDeadbandPercent = 2.0;

    private void InitializeAiTrackingSettings()
    {
        _pendingGeminiApiKey = SettingsManager.Instance.GeminiApiKey;
        _pendingGeminiModelName = SettingsManager.Instance.GeminiModelName;
        _pendingAiZoomBaseScale = SettingsManager.Instance.AiZoomBaseScale;
        _pendingAiZoomMinScale = SettingsManager.Instance.AiZoomMinScale;
        _pendingAiZoomAvoidHud = SettingsManager.Instance.AiZoomAvoidHud;
        _pendingAiZoomDeadbandPercent = SettingsManager.Instance.AiZoomDeadbandPercent;
    }

    /// <summary>
    /// SETTX_01 — the AI-tracking values are no longer written here. They are applied inside
    /// <see cref="SaveAndClose"/>'s single <see cref="SettingsManager.Update(System.Action{AppSettings})"/>
    /// transaction, so all fields of the settings window commit atomically as one snapshot.
    /// Kept as documentation of ownership; the fields are read directly in the patch.
    /// </summary>

    private void BuildAiTrackingUi()
    {
        var keyBox = this.FindNameScope()?.Find("AiGeminiApiKeyTextBox") as TextBox;
        if (keyBox != null)
        {
            keyBox.Text = _pendingGeminiApiKey;
            keyBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                {
                    _pendingGeminiApiKey = keyBox.Text ?? "";
                }
            };
        }

        var baseSlider = this.FindNameScope()?.Find("AiBaseZoomSlider") as Slider;
        var baseLabel = this.FindNameScope()?.Find("AiBaseZoomLabel") as TextBlock;
        if (baseSlider != null)
        {
            baseSlider.Value = _pendingAiZoomBaseScale;
            if (baseLabel != null) baseLabel.Text = $"{_pendingAiZoomBaseScale:0.0}x";
            baseSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty)
                {
                    _pendingAiZoomBaseScale = Math.Round(baseSlider.Value, 1);
                    if (baseLabel != null) baseLabel.Text = $"{_pendingAiZoomBaseScale:0.0}x";
                }
            };
        }

        var minSlider = this.FindNameScope()?.Find("AiMinZoomSlider") as Slider;
        var minLabel = this.FindNameScope()?.Find("AiMinZoomLabel") as TextBlock;
        if (minSlider != null)
        {
            minSlider.Value = _pendingAiZoomMinScale;
            if (minLabel != null) minLabel.Text = $"{_pendingAiZoomMinScale:0.0}x";
            minSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty)
                {
                    _pendingAiZoomMinScale = Math.Round(minSlider.Value, 1);
                    if (minLabel != null) minLabel.Text = $"{_pendingAiZoomMinScale:0.0}x";
                }
            };
        }

        var avoidHudCb = this.FindNameScope()?.Find("AiAvoidHudCheckbox") as CheckBox;
        if (avoidHudCb != null)
        {
            avoidHudCb.IsChecked = _pendingAiZoomAvoidHud;
            avoidHudCb.IsCheckedChanged += (_, _) => _pendingAiZoomAvoidHud = avoidHudCb.IsChecked ?? true;
        }

        var getFreeBtn = this.FindNameScope()?.Find("AiGetFreeKeyBtn") as Button;
        if (getFreeBtn != null)
        {
            getFreeBtn.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "https://aistudio.google.com/app/apikey",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("Settings", $"Failed to launch browser: {ex.Message}");
                }
            };
        }

        var statusText = this.FindNameScope()?.Find("AiConnectionStatusText") as TextBlock;
        var testBtn = this.FindNameScope()?.Find("AiTestConnectionBtn") as Button;
        if (testBtn != null)
        {
            testBtn.Click += async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_pendingGeminiApiKey))
                {
                    if (statusText != null)
                    {
                        statusText.Text = "Please enter an API Key first.";
                        statusText.Foreground = Brushes.Orange;
                    }
                    return;
                }

                testBtn.IsEnabled = false;
                if (statusText != null)
                {
                    statusText.Text = "Contacting Google Gemini servers...";
                    statusText.Foreground = Brushes.Gray;
                }

                var (success, msg) = await GeminiTrackingService.TestApiKeyAsync(_pendingGeminiApiKey, _pendingGeminiModelName);

                testBtn.IsEnabled = true;
                if (statusText != null)
                {
                    statusText.Text = msg;
                    statusText.Foreground = success ? Brushes.LimeGreen : Brushes.Red;
                }
            };
        }
    }

    private void BuildAboutUi()
    {
        var verText = this.FindNameScope()?.Find("AboutVersionText") as TextBlock;
        if (verText != null)
        {
            verText.Text = $"Version {DeploymentLifecycle.GetCurrentVersion()}";
        }

        var platformText = this.FindNameScope()?.Find("AboutPlatformText") as TextBlock;
        if (platformText != null)
        {
            string os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            platformText.Text = $".NET 9.0 ({os}, {arch}, NativeAOT)";
        }

        var gpuText = this.FindNameScope()?.Find("AboutGpuText") as TextBlock;
        if (gpuText != null)
        {
            string activeEncoder = SettingsManager.Instance.VideoEncoderOverride;
            gpuText.Text = activeEncoder.Equals("Auto", StringComparison.OrdinalIgnoreCase)
                ? "Auto (Hardware Acceleration Preferred)"
                : $"{activeEncoder} (Manual Override)";
        }

        var storageText = this.FindNameScope()?.Find("AboutStorageText") as TextBlock;
        if (storageText != null)
        {
            storageText.Text = _paths.ProgramDataRoot;
        }

        var statusText = this.FindNameScope()?.Find("UpdateStatusText") as TextBlock;
        var checkBtn = this.FindNameScope()?.Find("CheckUpdatesNowBtn") as Button;
        var autoCheckCb = this.FindNameScope()?.Find("AutoUpdateChecksCheckbox") as CheckBox;
        if (autoCheckCb != null)
        {
            autoCheckCb.IsChecked = AutoUpdateChecks;
            autoCheckCb.IsCheckedChanged += (_, _) => AutoUpdateChecks = autoCheckCb.IsChecked == true;
        }
        var skippedBorder = this.FindNameScope()?.Find("SkippedReleaseBorder") as Border;
        var skippedLabel = this.FindNameScope()?.Find("SkippedReleaseLabel") as TextBlock;
        var clearSkipBtn = this.FindNameScope()?.Find("ClearSkipBtn") as Button;

        void RefreshSkippedBorder()
        {
            if (skippedBorder == null) return;
            string skipped = UpdateService.GetSkippedVersion();
            if (!string.IsNullOrWhiteSpace(skipped))
            {
                if (skippedLabel != null) skippedLabel.Text = $"Skipped release: {skipped}";
                skippedBorder.IsVisible = true;
            }
            else
            {
                skippedBorder.IsVisible = false;
            }
        }

        RefreshSkippedBorder();

        if (clearSkipBtn != null)
        {
            clearSkipBtn.Click += (_, _) =>
            {
                UpdateService.ClearSkippedVersion();
                RefreshSkippedBorder();
                if (statusText != null) statusText.Text = "Skipped release filter cleared.";
            };
        }

        if (checkBtn != null)
        {
            checkBtn.Click += async (_, _) =>
            {
                checkBtn.IsEnabled = false;
                if (statusText != null) statusText.Text = "Checking for updates...";
                try
                {
                    await UpdateService.CheckManualAsync(this, msg =>
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (statusText != null) statusText.Text = msg;
                        });
                    });
                }
                finally
                {
                    checkBtn.IsEnabled = true;
                    RefreshSkippedBorder();
                }
            };
        }
    }
}
