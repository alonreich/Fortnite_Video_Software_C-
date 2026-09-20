using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>
/// ISSUE_11 — modal progress + cancel for a cloud asset download.
///
/// Usage: <c>await CloudSyncProgressWindow.RunAsync(owner, "Downloading memes", (progress, ct) =&gt; ...)</c>.
/// The window owns the <see cref="CancellationTokenSource"/>, closes itself when the work
/// finishes, and returns the worker's result. Cancelling is a normal outcome, not an error.
/// </summary>
public partial class CloudSyncProgressWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private bool _workFinished;

    public CloudSyncProgressWindow()
    {
        InitializeComponent();

        var cancelBtn = this.FindControl<Button>("CancelSyncBtn");
        if (cancelBtn != null)
        {
            cancelBtn.Click += (_, _) =>
            {
                cancelBtn.IsEnabled = false;
                cancelBtn.Content = "CANCELLING…";
                SetHeadline("Cancelling — finishing the current file…");
                _cts.Cancel();
            };
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> with this window shown modally over <paramref name="owner"/>.
    /// </summary>
    public static async Task<(int downloaded, string? error)> RunAsync(
        Window owner,
        string title,
        Func<IProgress<MemeCatalog.SyncProgress>, CancellationToken, Task<(int downloaded, string? error)>> work)
    {
        var win = new CloudSyncProgressWindow();
        win.SetTitle(title);

        var progress = new Progress<MemeCatalog.SyncProgress>(win.Apply);
        (int downloaded, string? error) result = (0, null);

        win.Opened += (_, _) =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    result = await work(progress, win._cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (!win._cts.Token.IsCancellationRequested)
                    {
                        RuntimeLog.Fail("CloudSync", "Download timed out.");
                        result = (0, "The download timed out. Please check your network connection and try again.");
                    }
                    else
                    {
                        result = (0, null);
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("CloudSync", ex);
                    result = (0, $"The download stopped unexpectedly: {ex.Message}");
                }
                finally
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        win._workFinished = true;
                        win.Close();
                    });
                }
            });
        };

        await win.ShowDialog(owner);
        return result;
    }

    private void Apply(MemeCatalog.SyncProgress p)
    {
        var bar = this.FindControl<ProgressBar>("SyncProgressBar");
        var fileText = this.FindControl<TextBlock>("CurrentFileText");
        var countText = this.FindControl<TextBlock>("CountText");

        if (p.Total <= 0)
        {
            SetHeadline("Checking which files are missing…");
            if (bar != null) bar.IsIndeterminate = true;
            return;
        }

        SetHeadline("Downloading new files…");

        if (bar != null)
        {
            bar.IsIndeterminate = false;
            bar.Value = Math.Clamp(p.Completed * 100.0 / p.Total, 0, 100);
        }

        if (fileText != null)
        {
            fileText.Text = string.IsNullOrEmpty(p.FileName) ? string.Empty : "Current: " + p.FileName;
        }

        if (countText != null)
        {
            countText.Text = $"{p.Completed} of {p.Total} file(s) complete";
        }
    }

    private void SetHeadline(string text)
    {
        var headline = this.FindControl<TextBlock>("HeadlineText");
        if (headline != null) headline.Text = text;
    }

    public void SetTitle(string title)
    {
        var titleTb = this.FindControl<TextBlock>("DialogTitle");
        if (titleTb != null) titleTb.Text = title;
        Title = title;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_workFinished)
        {
            _cts.Cancel();
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    private void InitializeComponent() { AvaloniaXamlLoader.Load(this); }
}
