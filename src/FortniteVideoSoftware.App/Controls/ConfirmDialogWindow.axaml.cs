using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace FortniteVideoSoftware.App.Controls;

public partial class ConfirmDialogWindow : Window
{
    public bool Result { get; private set; } = false;

    public ConfirmDialogWindow()
    {
        InitializeComponent();
        var yesBtn = this.FindControl<Button>("YesBtn");
        var noBtn = this.FindControl<Button>("NoBtn");
        if (yesBtn != null) yesBtn.Click += (s, e) => { Result = true; Close(); };
        if (noBtn != null) noBtn.Click += (s, e) => { Result = false; Close(); };
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

    public void SetButtonText(string yesText, string noText)
    {
        var yesBtn = this.FindControl<Button>("YesBtn");
        var noBtn = this.FindControl<Button>("NoBtn");
        if (yesBtn != null) yesBtn.Content = yesText;
        if (noBtn != null) noBtn.Content = noText;
    }

    private void InitializeComponent() { AvaloniaXamlLoader.Load(this); }
}
