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

    /// <summary>
    /// EDIT3_01 — dresses the dialog for the THREE-WAY "you already have one of these" question:
    /// change it, throw it away, or leave everything alone.
    ///
    /// The colour language is the whole point. EDIT is the ordinary action, so it wears Primary.
    /// CANCEL wears Secondary and takes the Enter key, because doing nothing must be what a reflex
    /// keypress does. REMOVE destroys work that can take a user twenty minutes to rebuild, so it
    /// wears Danger — it was Primary blue, which put the safest-looking button on the one choice
    /// that deletes everything.
    /// </summary>
    public void UseEditOrRemoveStyling()
    {
        var yesBtn = this.FindControl<Button>("YesBtn");
        var noBtn = this.FindControl<Button>("NoBtn");
        var altBtn = this.FindControl<Button>("AltBtn");

        if (yesBtn != null)
        {
            yesBtn.Classes.Remove("Success");
            yesBtn.Classes.Remove("Danger");
            yesBtn.Classes.Add("Primary");
            yesBtn.IsDefault = false;
        }
        if (noBtn != null)
        {
            noBtn.Classes.Remove("Success");
            noBtn.Classes.Add("Secondary");
            noBtn.IsDefault = true;
        }
        if (altBtn != null)
        {
            altBtn.Classes.Remove("Primary");
            altBtn.Classes.Remove("Success");
            altBtn.Classes.Add("Danger");
        }
    }

    /// <summary>
    /// COVER_01 — repaints the three buttons for a question that is NOT a yes/no.
    ///
    /// The stock dressing (YES green, NO grey, ALT blue) encodes "green is safe, grey is escape".
    /// Some questions have three genuinely different actions and no safe/unsafe axis at all — the
    /// music-coverage question is one: add more music, move the start, or accept the silence.
    /// Rather than inventing a UseXxxStyling method per question, callers name the class they want
    /// on each button. Pass null to leave one alone.
    ///
    /// Valid classes are the four the root dictionary defines: Primary, Secondary, Success, Danger.
    /// </summary>
    public void SetButtonClasses(string? yesClass, string? noClass, string? altClass)
    {
        Repaint(this.FindControl<Button>("YesBtn"), yesClass);
        Repaint(this.FindControl<Button>("NoBtn"), noClass);
        Repaint(this.FindControl<Button>("AltBtn"), altClass);

        static void Repaint(Button? btn, string? cls)
        {
            if (btn == null || string.IsNullOrWhiteSpace(cls)) return;
            btn.Classes.Remove("Primary");
            btn.Classes.Remove("Secondary");
            btn.Classes.Remove("Success");
            btn.Classes.Remove("Danger");
            btn.Classes.Add(cls);
        }
    }

    /// <summary>EDIT3_01 — what the user chose in <see cref="AskEditOrRemoveAsync"/>.</summary>
    public enum EditOrRemoveChoice
    {
        /// <summary>Leave the existing work exactly as it is. Also what closing the dialog means.</summary>
        Cancelled,
        /// <summary>Reopen the editor on the existing work so it can be changed.</summary>
        Edit,
        /// <summary>Throw the existing work away.</summary>
        Remove
    }

    /// <summary>
    /// EDIT3_01 — THE SHARED "EDIT / CANCEL / REMOVE" QUESTION.
    ///
    /// Pressing an already-active feature button used to be a two-way choice in two of the three
    /// editors: the Granular Speed and Add Music buttons DELETED everything on the spot, with no
    /// prompt at all. So changing one thing about a music placement, or one segment out of twelve,
    /// meant destroying the lot and rebuilding it from scratch. The Voice Over button already had
    /// the third option; this is that behaviour lifted out so all three share ONE implementation
    /// and cannot drift apart in wording, colour or keyboard behaviour.
    ///
    /// Anything other than an explicit EDIT or REMOVE — closing the window, Escape, a failure to
    /// show the dialog at all — returns <see cref="EditOrRemoveChoice.Cancelled"/>, so the
    /// existing work survives. A prompt that cannot be shown must never be read as consent to
    /// delete.
    /// </summary>
    public static async System.Threading.Tasks.Task<EditOrRemoveChoice> AskEditOrRemoveAsync(
        Window owner,
        string title,
        string message,
        string editText = "EDIT",
        string cancelText = "CANCEL",
        string removeText = "REMOVE")
    {
        try
        {
            var dlg = new ConfirmDialogWindow();
            dlg.SetTitle(title);
            dlg.SetMessage(message);
            dlg.SetButtonText(editText, cancelText, removeText);
            dlg.UseEditOrRemoveStyling();
            await dlg.ShowDialog(owner);

            return dlg.DialogResult switch
            {
                ConfirmDialogResult.Yes => EditOrRemoveChoice.Edit,
                ConfirmDialogResult.Alt => EditOrRemoveChoice.Remove,
                _ => EditOrRemoveChoice.Cancelled
            };
        }
        catch (System.Exception ex)
        {
            RuntimeLog.Fail("DIALOG", $"Edit-or-remove prompt failed, keeping the existing work: {ex.Message}");
            return EditOrRemoveChoice.Cancelled;
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
