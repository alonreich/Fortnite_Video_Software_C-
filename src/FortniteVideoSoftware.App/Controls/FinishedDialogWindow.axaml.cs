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
        // Do not steal presses aimed at interactive controls.
        var el = e.Source as StyledElement;
        while (el != null)
        {
            if (el is Button) return;
            el = el.Parent;
        }
        try { BeginMoveDrag(e); } catch { }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        DialogResult = 0;
        Close();
    }
    
    private void OnUploadNewClicked(object? sender, RoutedEventArgs e)
    {
        DialogResult = 2;
        Close();
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
        catch { }
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
        catch { }
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
        catch { }
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
        catch { }
    }

    private void OnOpenFolderClicked(object? sender, RoutedEventArgs e)
    {
        OpenOutputFolder();
        DialogResult = 1;
        Close();
    }

    private void OnExitClicked(object? sender, RoutedEventArgs e)
    {
        DialogResult = 1;
        Close();
    }
    
    private void OpenOutputFolder()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_outputPath) && File.Exists(_outputPath))
            {
                Process.Start("explorer.exe", $"/select,\"{_outputPath}\"");
            }
        }
        catch { }
    }
}
