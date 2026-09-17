using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>The four answers the update prompt can give, in importance order.</summary>
public enum UpdateChoice
{
    /// <summary>Closed via the title bar without picking; treated as "not now" with no state change.</summary>
    Dismissed,

    /// <summary>"Yes. Please download and upgrade to the latest released version now."</summary>
    UpdateNow,

    /// <summary>"No. I like this version." — nothing stored; may be asked again on a later start.</summary>
    NotNow,

    /// <summary>"Skip this version…" — this exact release is never offered again; newer ones are.</summary>
    SkipThisVersion,

    /// <summary>"Never tell me about updates again." — flips AutoUpdateChecks to false in Settings.</summary>
    NeverTellMeAgain
}

public partial class UpdateAvailableWindow : Window
{
    public UpdateChoice Choice { get; private set; } = UpdateChoice.Dismissed;

    public UpdateAvailableWindow()
    {
        InitializeComponent();

        Wire("UpdateNowBtn", UpdateChoice.UpdateNow);
        Wire("NotNowBtn", UpdateChoice.NotNow);
        Wire("SkipBtn", UpdateChoice.SkipThisVersion);
        Wire("NeverBtn", UpdateChoice.NeverTellMeAgain);
    }

    private void Wire(string buttonName, UpdateChoice choice)
    {
        var button = this.FindControl<Button>(buttonName);
        if (button != null)
            button.Click += (s, e) => { Choice = choice; Close(); };
    }

    public void SetVersions(Version local, string remoteTag, string? releaseNotes = null)
    {
        string remote = remoteTag.TrimStart('v', 'V');
        var yourVersion = this.FindControl<TextBlock>("YourVersionText");
        var newVersion = this.FindControl<TextBlock>("NewVersionText");
        if (yourVersion != null) yourVersion.Text = $"Your version:  {local}";
        if (newVersion != null) newVersion.Text = $"New version:  {remote}";

        var notesExpander = this.FindControl<Expander>("ReleaseNotesExpander");
        var notesText = this.FindControl<TextBlock>("ReleaseNotesText");
        if (notesExpander != null && notesText != null)
        {
            if (!string.IsNullOrWhiteSpace(releaseNotes))
            {
                notesText.Text = releaseNotes.Trim();
                notesExpander.IsVisible = true;
                notesExpander.IsExpanded = true;
            }
            else
            {
                notesText.Text = "No release notes were provided for this release.";
                notesExpander.IsVisible = true;
                notesExpander.IsExpanded = false;
            }
        }
    }

    /// <summary>
    /// AUTO-UPDATE — shows the four-way update prompt. A prompt that cannot be shown must never
    /// be interpreted as consent, so any failure collapses to <see cref="UpdateChoice.NotNow"/>.
    /// </summary>
    public static async Task<UpdateChoice> AskAsync(Window owner, Version localVersion, string remoteTag, string? releaseNotes = null)
    {
        try
        {
            var dlg = new UpdateAvailableWindow();
            dlg.SetVersions(localVersion, remoteTag, releaseNotes);
            await dlg.ShowDialog(owner);
            return dlg.Choice;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UPDATE", $"Update prompt failed to display, treating as 'not now': {ex.Message}");
            return UpdateChoice.NotNow;
        }
    }

    private void InitializeComponent() { AvaloniaXamlLoader.Load(this); }
}
