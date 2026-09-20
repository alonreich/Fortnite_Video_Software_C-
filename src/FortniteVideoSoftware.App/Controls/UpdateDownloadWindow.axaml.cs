using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace FortniteVideoSoftware.App.Controls;

public partial class UpdateDownloadWindow : Window
{
    /// <summary>Raised on the UI thread when the user presses CANCEL DOWNLOAD (or Escape).</summary>
    public event Action? CancelRequested;

    public UpdateDownloadWindow()
    {
        InitializeComponent();
        var cancelBtn = this.FindControl<Button>("CancelBtn");
        if (cancelBtn != null)
            cancelBtn.Click += (s, e) => CancelRequested?.Invoke();
    }

    /// <summary>Thread-safe only when called ON the UI thread (UpdateService always Posts).</summary>
    public void SetProgress(double fraction, string status)
    {
        var bar = this.FindControl<ProgressBar>("Progress");
        var text = this.FindControl<TextBlock>("StatusText");
        if (bar != null) bar.Value = Math.Clamp(fraction, 0, 1) * 100;
        if (text != null) text.Text = status;
    }

    /// <summary>Final state: the verified file is being handed to the installer — no more cancelling.</summary>
    public void MarkHandoffToInstaller()
    {
        var headline = this.FindControl<TextBlock>("HeadlineText");
        var status = this.FindControl<TextBlock>("StatusText");
        var bar = this.FindControl<ProgressBar>("Progress");
        var cancelBtn = this.FindControl<Button>("CancelBtn");
        if (headline != null) headline.Text = "Update ready — starting the installer…";
        if (status != null) status.Text = "Windows may ask for Administrator permission to finish. Your settings are kept automatically.";
        if (bar != null) bar.Value = 100;
        if (cancelBtn != null) cancelBtn.IsEnabled = false;
    }

    private void InitializeComponent() { AvaloniaXamlLoader.Load(this); }
}
