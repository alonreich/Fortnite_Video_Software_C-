// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;

namespace FreeVideoStudio.App.Controls;

public partial class AiSetupWizardWindow : Window
{
    private string _verifiedKey = string.Empty;

    public AiSetupWizardWindow()
    {
        InitializeComponent();

        var openBtn = this.FindNameScope()?.Find("OpenAiStudioBtn") as Button;
        if (openBtn != null)
        {
            openBtn.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://aistudio.google.com/app/apikey",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("AiWizard", $"Failed to launch browser: {ex.Message}");
                }
            };
        }

        var testBtn = this.FindNameScope()?.Find("TestAndSaveBtn") as Button;
        var keyInput = this.FindNameScope()?.Find("ApiKeyInputBox") as TextBox;
        var statusLabel = this.FindNameScope()?.Find("VerificationStatusLabel") as TextBlock;
        var startBtn = this.FindNameScope()?.Find("StartAiZoomBtn") as Button;
        var cancelBtn = this.FindNameScope()?.Find("CancelWizardBtn") as Button;

        if (testBtn != null && keyInput != null && statusLabel != null)
        {
            testBtn.Click += async (_, _) =>
            {
                string key = (keyInput.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    statusLabel.Text = "Please enter an API Key first.";
                    statusLabel.Foreground = Brushes.Orange;
                    return;
                }

                testBtn.IsEnabled = false;
                statusLabel.Text = "Contacting Google Gemini servers...";
                statusLabel.Foreground = Brushes.Gray;

                var (success, message) = await GeminiTrackingService.TestApiKeyAsync(key);

                testBtn.IsEnabled = true;
                if (success)
                {
                    _verifiedKey = key;
                    statusLabel.Text = "Key Verified Successfully!";
                    statusLabel.Foreground = Brushes.LimeGreen;

                    SettingsManager.Update(s => s.GeminiApiKey = key);

                    if (startBtn != null) startBtn.IsEnabled = true;
                }
                else
                {
                    statusLabel.Text = message;
                    statusLabel.Foreground = Brushes.Red;
                    if (startBtn != null) startBtn.IsEnabled = false;
                }
            };
        }

        if (startBtn != null)
        {
            startBtn.Click += (_, _) =>
            {
                Close(true);
            };
        }

        if (cancelBtn != null)
        {
            cancelBtn.Click += (_, _) =>
            {
                Close(false);
            };
        }

        AttachTitleBarDrag();
    }

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindNameScope()?.Find("TitleBarBorder") as Border;
        if (titleBar != null)
        {
            titleBar.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    BeginMoveDrag(e);
                }
            };
        }
    }

    /// <summary>
    /// Checks if a valid Gemini API key is configured. If not, presents the setup wizard.
    /// Returns true if a valid key is ready to use.
    /// </summary>
    public static async Task<bool> EnsureApiKeyConfiguredAsync(Window owner)
    {
        if (!string.IsNullOrWhiteSpace(SettingsManager.Instance.GeminiApiKey))
        {
            return true;
        }

        var wizard = new AiSetupWizardWindow();
        bool? result = await wizard.ShowDialog<bool?>(owner);
        return result == true && !string.IsNullOrWhiteSpace(SettingsManager.Instance.GeminiApiKey);
    }
}
