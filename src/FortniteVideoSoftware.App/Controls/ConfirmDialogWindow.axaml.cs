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

    private void InitializeComponent() { AvaloniaXamlLoader.Load(this); }
}
