using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>
/// MEME_06 — "which meme goes here?", asked at the moment ADD MEME is pressed.
///
/// <para>
/// Deliberately a themed Avalonia window and not a Win32 file dialog. The memes a user actually
/// wants live in the app's own meme folder, already scanned and dimension-probed by
/// <see cref="MemeCatalog"/>; a file browser would drop them somewhere on their disk with no
/// guarantee the file is even a video, and would look like a different program interrupting the
/// editor — the same complaint DIALOG_01 records about NativeDialog.
/// </para>
/// <para>
/// Returns the chosen <see cref="MemeItem"/> in <see cref="Result"/>, or null for cancel. An empty
/// meme folder is a legitimate outcome and says so on screen rather than showing a blank list.
/// </para>
/// </summary>
public partial class MemePickerWindow : Window
{
    public MemeItem? Result { get; private set; }

    public MemePickerWindow()
    {
        InitializeComponent();

        var list = this.FindControl<ListBox>("MemeList");
        var useBtn = this.FindControl<Button>("UseBtn");
        var cancelBtn = this.FindControl<Button>("CancelBtn");

        if (list != null && useBtn != null)
        {
            list.SelectionChanged += (_, _) => useBtn.IsEnabled = list.SelectedItem is MemeItem;
            list.DoubleTapped += (_, _) =>
            {
                if (list.SelectedItem is MemeItem picked) { Result = picked; Close(); }
            };
            useBtn.Click += (_, _) =>
            {
                if (list.SelectedItem is MemeItem picked) { Result = picked; Close(); }
            };
        }

        if (cancelBtn != null) cancelBtn.Click += (_, _) => { Result = null; Close(); };
    }

    /// <summary>
    /// MEME_06 — fills the list. Download-action rows are filtered out: they are a main-screen
    /// affordance, and picking one here would place a meme with no file behind it.
    /// </summary>
    public void SetItems(IEnumerable<MemeItem> items)
    {
        var real = items.Where(i => !i.IsDownloadAction && !string.IsNullOrWhiteSpace(i.FullPath)).ToList();

        var list = this.FindControl<ListBox>("MemeList");
        if (list != null) list.ItemsSource = real;

        var empty = this.FindControl<TextBlock>("EmptyText");
        if (empty != null) empty.IsVisible = real.Count == 0;
    }

    /// <summary>MEME_06 — shows the picker and returns the choice, or null.</summary>
    public static async Task<MemeItem?> PickAsync(Window owner, IEnumerable<MemeItem> items)
    {
        try
        {
            var dlg = new MemePickerWindow();
            dlg.SetItems(items);
            await dlg.ShowDialog(owner);
            return dlg.Result;
        }
        catch (Exception ex)
        {
            // A picker that cannot open must not be read as "the user picked something".
            RuntimeLog.Fail("MEME", $"Meme picker failed to open: {ex.Message}");
            return null;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
