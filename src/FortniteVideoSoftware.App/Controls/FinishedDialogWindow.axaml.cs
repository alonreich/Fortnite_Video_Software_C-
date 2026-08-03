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

        // AUDIO_02: THIS is the completion. The old dispatcher played the 1.385 s "process"
        // fanfare whenever any Success-classed button was clicked, so it fired the moment the
        // user pressed PROCESS/MERGE — before a single frame had been encoded — and then said
        // nothing at all when the export actually finished minutes later. An export is exactly
        // the moment a user walks away from the machine, so this was the single highest-value
        // audio cue in the suite and it was inverted. Played on Opened rather than in the
        // constructor so it lands with the window, not before it.
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
        try { BeginMoveDrag(e); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
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
        catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
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
        catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
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
        catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
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
        catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
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
                // ISSUE_3: this was Process.Start("explorer.exe", $"/select,\"{_outputPath}\"") —
                // the one place in the app that glues a user path into a raw command line instead
                // of using ArgumentList. ArgumentList applies Windows' own quoting rules per
                // argument, so nothing in the path can break the command.
                //
                // NOTE the deliberate exception: explorer.exe does NOT follow the standard
                // command-line parsing rules, and "/select," plus the path must arrive as ONE
                // argument. That is why they are concatenated into a single ArgumentList entry
                // rather than added as two.
                var psi = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = false };
                psi.ArgumentList.Add("/select," + _outputPath);
                Process.Start(psi);
            }
        }
        catch (Exception ex)
        {
            // ISSUE_2: was `catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }`, so a failure left OPEN FOLDER looking like a dead button
            // with no trace anywhere. Still non-fatal, but now diagnosable.
            RuntimeLog.Fail("UI", $"Could not open the output folder: {ex.Message}");
        }
    }
}
