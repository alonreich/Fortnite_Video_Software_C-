using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System;
using System.Diagnostics;
using System.IO;

namespace FortniteVideoSoftware.App.Controls;

public partial class FinishedDialogWindow : Window
{
    private string _outputPath = "";
    
    public int DialogResult { get; private set; } = 0;

    public FinishedDialogWindow()
    {
        InitializeComponent();

        Opened += (_, _) => UiSoundEffect.PlayProcess();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
    
    public void SetOutputPath(string path)
    {
        _outputPath = path;
        var txt = this.FindControl<TextBlock>("MessageText");
        if (txt != null)
        {
            txt.Text = $"File successfully saved to:\n{path}";
        }
    }

    /// <summary>
    /// ISSUE_01: this window removes the OS title bar (ExtendClientAreaTitleBarHeightHint="0"),
    /// so without this handler the user cannot move the dialog at all.
    /// Dragging starts anywhere on the background EXCEPT interactive controls (buttons).
    /// </summary>
    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.ClickCount >= 2) return;
        var el = e.Source as StyledElement;
        while (el != null)
        {
            if (el is Button) return;
            el = el.Parent;
        }
        try { BeginMoveDrag(e); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        DialogResult = 0;
        Close();
    }
    
    private async void OnUploadNewClicked(object? sender, RoutedEventArgs e)
    {
        if (Infrastructure.SettingsManager.Instance.ConfirmFinishedDialogExit
            && !await ConfirmAsync("Start a New File",
                                   "Discard the current project and start a new one?\nThe file you just exported is safe on disk.",
                                   "YES, START NEW"))
        {
            return;
        }

        DialogResult = 2;
        Close();
    }

    /// <summary>Shared opt-in guard for the two session-ending buttons (ISSUE_04).</summary>
    private async System.Threading.Tasks.Task<bool> ConfirmAsync(string title, string message, string yesText)
    {
        var dlg = new ConfirmDialogWindow();
        dlg.SetTitle(title);
        dlg.SetMessage(message);
        dlg.SetButtonText(yesText, "CANCEL");
        await dlg.ShowDialog(this);
        return dlg.Result;
    }

    private void OnWhatsAppClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://web.whatsapp.com",
                UseShellExecute = true
            });
            OpenOutputFolder();
            DialogResult = 1;
            Close();
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }
    private void OnInstagramClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://www.instagram.com/",
                UseShellExecute = true
            });
            OpenOutputFolder();
            DialogResult = 1;
            Close();
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private void OnYouTubeClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://studio.youtube.com/",
                UseShellExecute = true
            });
            OpenOutputFolder();
            DialogResult = 1;
            Close();
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private void OnTikTokClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://www.tiktok.com/creator-center/upload",
                UseShellExecute = true
            });
            OpenOutputFolder();
            DialogResult = 1;
            Close();
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private void OnOpenFolderClicked(object? sender, RoutedEventArgs e)
    {
        OpenOutputFolder();
        DialogResult = 1;
        Close();
    }

    private async void OnExitClicked(object? sender, RoutedEventArgs e)
    {
        if (Infrastructure.SettingsManager.Instance.ConfirmFinishedDialogExit
            && !await ConfirmAsync("Exit the App",
                                   "Close the application now?\nThe file you just exported is safe on disk.",
                                   "YES, EXIT"))
        {
            return;
        }

        DialogResult = 1;
        Close();
    }
    
    private void OpenOutputFolder()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_outputPath) && File.Exists(_outputPath))
            {
                var psi = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = false };
                psi.ArgumentList.Add("/select," + _outputPath);
                Process.Start(psi);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UI", $"Could not open the output folder: {ex.Message}");
        }
    }
}
