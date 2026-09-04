using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace FortniteVideoSoftware.App.Controls;

public partial class ConfirmDialogWindow : Window
{
    public enum ConfirmDialogResult { Cancelled, Yes, No, Alt }
    
    public ConfirmDialogResult DialogResult { get; private set; } = ConfirmDialogResult.Cancelled;
    public bool Result => DialogResult == ConfirmDialogResult.Yes;

    public ConfirmDialogWindow()
    {
        InitializeComponent();
        var yesBtn = this.FindControl<Button>("YesBtn");
        var noBtn = this.FindControl<Button>("NoBtn");
        var altBtn = this.FindControl<Button>("AltBtn");
        if (yesBtn != null) yesBtn.Click += (s, e) => { DialogResult = ConfirmDialogResult.Yes; Close(); };
        if (noBtn != null) noBtn.Click += (s, e) => { DialogResult = ConfirmDialogResult.No; Close(); };
        if (altBtn != null) altBtn.Click += (s, e) => { DialogResult = ConfirmDialogResult.Alt; Close(); };
    }

    public void SetMessage(string message)
    {
        var msg = this.FindControl<TextBlock>("MessageText");
        if (msg != null) msg.Text = message;
    }

    public void SetTitle(string title)
    {
        var titleTb = this.FindControl<TextBlock>("DialogTitle");
        if (titleTb != null) titleTb.Text = title;
        this.Title = title;
    }

    /// <summary>
    /// DIALOG_02 — recolours the two buttons for a DESTRUCTIVE question.
    ///
    /// The default dressing is YES=Success / NO=Secondary, which reads "the green one is the safe
    /// one". For a question whose YES destroys something that reading is backwards and dangerous:
    /// the eye goes to green and green is now the button that deletes. This flips them so the
    /// destructive answer wears Danger and the escape wears Success, matching the colour language
    /// used everywhere else in the suite.
    /// </summary>
    public void UseDestructiveStyling()
    {
        var yesBtn = this.FindControl<Button>("YesBtn");
        var noBtn = this.FindControl<Button>("NoBtn");

        if (yesBtn != null)
        {
            yesBtn.Classes.Remove("Success");
            yesBtn.Classes.Add("Danger");
            // The destructive answer must not be what Enter presses.
            yesBtn.IsDefault = false;
        }
        if (noBtn != null)
        {
            noBtn.Classes.Remove("Secondary");
            noBtn.Classes.Add("Success");
            noBtn.IsDefault = true;
        }
    }

    public void SetButtonText(string yesText, string noText, string? altText = null)
    {
        var yesBtn = this.FindControl<Button>("YesBtn");
        var noBtn = this.FindControl<Button>("NoBtn");
        var altBtn = this.FindControl<Button>("AltBtn");
        if (yesBtn != null) yesBtn.Content = yesText;
        if (noBtn != null) noBtn.Content = noText;
        if (altBtn != null && altText != null)
        {
            altBtn.Content = altText;
            altBtn.IsVisible = true;
        }
    }

    /// <summary>
    /// DIALOG_01 — THE THEMED REPLACEMENT FOR <c>NativeDialog.ShowQuestion</c>.
    ///
    /// NativeDialog goes straight to the Win32 MessageBox. That box knows nothing about the app:
    /// wrong font, wrong colours, wrong corner radius, a Windows title bar, and it ignores the
    /// theme and the Settings font scale entirely. Inside a styled full-screen editor it reads as
    /// a different program interrupting you — which is exactly the complaint about DELETE PARTS.
    ///
    /// This window is ordinary Avalonia content, so it inherits the whole root dictionary for free.
    ///
    /// ⚠️ USE THIS ONLY WHERE A REAL OWNER WINDOW EXISTS AND THE UI THREAD IS FREE TO PUMP.
    /// NativeDialog is still correct for install/uninstall and startup failures (DeploymentLifecycle,
    /// Program.cs): those run before — or after — there is any Avalonia window to own a dialog, and
    /// a modal that needs a live UI thread would deadlock or silently never appear there.
    /// </summary>
    /// <param name="destructive">
    /// DIALOG_02 — true when YES destroys something. Paints YES red and NO green, and moves the
    /// Enter key onto NO so a reflex keypress cannot delete anything.
    /// </param>
    public static async System.Threading.Tasks.Task<bool> AskAsync(
        Window owner, string message, string title, string yesText = "Yes", string noText = "No",
        bool destructive = false)
    {
        try
        {
            var dlg = new ConfirmDialogWindow();
            dlg.SetTitle(title);
            dlg.SetMessage(message);
            dlg.SetButtonText(yesText, noText);
            if (destructive) dlg.UseDestructiveStyling();   // DIALOG_02
            await dlg.ShowDialog(owner);
            return dlg.Result;
        }
        catch (System.Exception ex)
        {
            // A confirmation that cannot be shown must NOT be treated as consent.
            RuntimeLog.Fail("DIALOG", $"Themed confirm failed, treating as declined: {ex.Message}");
            return false;
        }
    }

    private void InitializeComponent() { AvaloniaXamlLoader.Load(this); }
}
