using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System;
using System.Diagnostics;
using System.IO;

namespace FortniteVideoSoftware.App.Controls;

public partial class FinishedDialogWindow : Window
{
    private string _outputPath = "";
    
    // Result: 0 = Close/Upload New, 1 = Exit
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

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        DialogResult = 0;
        Close();
    }
    
    private void OnUploadNewClicked(object? sender, RoutedEventArgs e)
    {
        DialogResult = 0;
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
