using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>
/// The single user-facing failure surface for the whole suite.
/// Shows WHAT failed in plain English, the ONE root-cause line lifted out of the log, and two
/// actions: OKAY (acknowledge) and SHOW LOGS (open the log file in the OS default viewer).
/// Never construct this directly from feature code — go through <see cref="ErrorReporter"/>.
/// </summary>
public partial class ErrorDialogWindow : Window
{
    private string _logPath = string.Empty;

    public ErrorDialogWindow()
    {
        InitializeComponent();

        var okayBtn = this.FindControl<Button>("OkayBtn");
        var showLogsBtn = this.FindControl<Button>("ShowLogsBtn");

        if (okayBtn != null) okayBtn.Click += (_, _) => Close();
        if (showLogsBtn != null) showLogsBtn.Click += (_, _) => OpenLogFile();

        // AUDIO_03: UiSoundEffect.PlayError and its 108 KB clip existed but had ZERO call sites
        // anywhere in src\ — a sound compiled into the binary that was structurally impossible
        // to hear, while every failure in the suite happened in total silence. This window is
        // the single user-facing failure surface (see the class summary above), so it is the
        // one correct place to fire it.
        Opened += (_, _) => UiSoundEffect.PlayError();
    }

    public void SetTitle(string title)
    {
        var titleTb = this.FindControl<TextBlock>("DialogTitle");
        if (titleTb != null) titleTb.Text = title;
        Title = title;
    }

    public void SetMessage(string message)
    {
        var msg = this.FindControl<TextBlock>("MessageText");
        if (msg != null) msg.Text = message;
    }

    /// <summary>Root-cause line. Passing null/blank keeps the detail block collapsed.</summary>
    public void SetDetail(string? detail)
    {
        var container = this.FindControl<Border>("DetailContainer");
        var detailTb = this.FindControl<SelectableTextBlock>("DetailText");
        bool hasDetail = !string.IsNullOrWhiteSpace(detail);

        if (detailTb != null) detailTb.Text = hasDetail ? detail!.Trim() : string.Empty;
        if (container != null) container.IsVisible = hasDetail;
    }

    public void SetLogPath(string logPath)
    {
        _logPath = logPath ?? string.Empty;

        var showLogsBtn = this.FindControl<Button>("ShowLogsBtn");
        if (showLogsBtn != null)
        {
            bool exists = !string.IsNullOrWhiteSpace(_logPath) && File.Exists(_logPath);
            showLogsBtn.IsEnabled = exists;
            if (!exists) showLogsBtn.Content = "NO LOG FILE";
        }
    }

    private void OpenLogFile()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_logPath) || !File.Exists(_logPath))
            {
                RuntimeLog.Fail("ErrorDialog", "SHOW LOGS pressed but the log file no longer exists.");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = _logPath,
                UseShellExecute = true
            });
            RuntimeLog.Info("ErrorDialog", "User opened the log file from the failure dialog.");
        }
        catch (Exception ex)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{_logPath}\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                RuntimeLog.Fail("ErrorDialog", $"Could not open the log file: {ex.Message}");
            }
        }
    }

    private void InitializeComponent() { AvaloniaXamlLoader.Load(this); }
}
