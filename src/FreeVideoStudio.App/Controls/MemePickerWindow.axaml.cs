// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FreeVideoStudio.App.Controls;

/// <summary>
/// MEMEPICK_01 — one row of the meme picker: the scanned <see cref="MemeItem"/> plus its preview
/// picture, which arrives later (<see cref="MemeThumbnailCache"/>).
/// </summary>
public sealed class MemePickerRow : ViewModelBase
{
    public MemePickerRow(MemeItem item, bool portrait)
    {
        Item = item;
        // UI-MEMESELECT — the same rule as the main-screen combo: portrait AND aspect > 0.85f.
        RatioWarning = portrait && item.AspectRatio > 0.85f;
    }

    public MemeItem Item { get; }
    public string FileName => Item.FileName;
    public string KindIcon => Item.IsImage ? "🖼" : "🎬";
    public bool RatioWarning { get; }

    public string Details =>
        (Item.IsImage ? "Picture" : "Video") +
        (Item.Width > 0 && Item.Height > 0 ? $"  ·  {Item.Width}×{Item.Height}" : "");

    public string Tip => RatioWarning ? "Fit for landscape — may be letterboxed in portrait mode" : Item.FileName;

    private Bitmap? _thumbnail;
    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set { if (SetProperty(ref _thumbnail, value)) OnPropertyChanged(nameof(HasThumbnail)); }
    }
    public bool HasThumbnail => _thumbnail != null;
}

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
/// MEMEPICK_01 — preview pictures, a name search, the portrait "fit for landscape" warning
/// (UI-MEMESELECT) and the two "download more" buttons live here too, so the editor never has to
/// be closed to find or fetch a meme. This replaced the Meme Wall (IDEA 008), which had thumbnails
/// but was never shown (MEMEWALL_01).
/// </para>
/// <para>
/// MEMEPICK_02 — the list the owner hands over is shown at once, then RE-SCANNED in the
/// background. The editor used to receive the main window's scan from the moment it opened, so a
/// meme downloaded or copied into the folder since then could not be picked. The scan is cheap:
/// dimensions come from the persisted cache (MEMESCAN_01). A download from either the picker or
/// the main screen raises <see cref="MemeDirectory.Changed"/>, which also re-scans.
/// </para>
/// <para>
/// Returns the chosen <see cref="MemeItem"/> in <see cref="Result"/>, or null for cancel, and the
/// newest scan in <see cref="LatestItems"/> so the owner can keep its own copy current.
/// </para>
/// </summary>
/// <summary>MEMEMODE_01 — what the popup returns: the meme and how it plays.</summary>
public sealed record MemePickResult(
    MemeItem Item,
    double? DurationSec,
    FreeVideoStudio.Core.Media.MemePresentationMode Mode,
    FreeVideoStudio.Core.Media.MemeOverlayCorner Corner,
    FreeVideoStudio.Core.Media.MemeOverlaySize Size,
    bool PlaySound);

public partial class MemePickerWindow : Window
{
    public MemeItem? Result { get; private set; }

    /// <summary>MEMEMODE_01 — the "how should it play?" half, bound (MVVM_01).</summary>
    public MemeChoiceViewModel Choice { get; } = new();

    private string? _preselectPath;
    private int _durationGeneration;

    /// <summary>MEMEPICK_02 — the newest list this window knows about (real memes only).</summary>
    public IReadOnlyList<MemeItem> LatestItems { get; private set; } = Array.Empty<MemeItem>();

    private readonly CancellationTokenSource _cts = new();
    private List<MemePickerRow> _rows = new();
    private bool _portrait;
    private int _scanGeneration;
    private bool _downloading;

    public MemePickerWindow()
    {
        InitializeComponent();
        DataContext = Choice;

        var list = this.FindControl<ListBox>("MemeList");
        var useBtn = this.FindControl<Button>("UseBtn");
        var cancelBtn = this.FindControl<Button>("CancelBtn");
        var search = this.FindControl<TextBox>("SearchBox");
        var moreVideos = this.FindControl<Button>("DownloadVideosBtn");
        var morePictures = this.FindControl<Button>("DownloadPicturesBtn");

        if (list != null && useBtn != null)
        {
            list.SelectionChanged += (_, _) =>
            {
                useBtn.IsEnabled = list.SelectedItem is MemePickerRow;
                OnMemeSelected((list.SelectedItem as MemePickerRow)?.Item);
            };
            list.DoubleTapped += (_, _) =>
            {
                if (list.SelectedItem is MemePickerRow picked) { Result = picked.Item; Close(); }
            };
            useBtn.Click += (_, _) =>
            {
                if (list.SelectedItem is MemePickerRow picked) { Result = picked.Item; Close(); }
            };
        }

        if (cancelBtn != null) cancelBtn.Click += (_, _) => { Result = null; Close(); };
        if (search != null) search.TextChanged += (_, _) => ApplyFilter();
        if (moreVideos != null) moreVideos.Click += async (_, _) => await DownloadAsync(MemeCategory.Video);
        if (morePictures != null) morePictures.Click += async (_, _) => await DownloadAsync(MemeCategory.Image);

        MemeDirectory.Changed += OnMemeDirectoryChanged;
        Opened += (_, _) => _ = RescanAsync();
        Closed += (_, _) =>
        {
            MemeDirectory.Changed -= OnMemeDirectoryChanged;
            _cts.Cancel();
        };
    }

    private void OnMemeDirectoryChanged() => Dispatcher.UIThread.Post(() => _ = RescanAsync());

    /// <summary>
    /// MEME_06 — fills the list. Download-action rows are filtered out: they are a main-screen
    /// affordance, and picking one here would place a meme with no file behind it.
    /// </summary>
    public void SetItems(IEnumerable<MemeItem> items, bool portrait = false)
    {
        _portrait = portrait;
        ApplyItems(items);
    }

    private void ApplyItems(IEnumerable<MemeItem> items)
    {
        var real = items.Where(i => !i.IsDownloadAction && !string.IsNullOrWhiteSpace(i.FullPath)).ToList();
        LatestItems = real;

        var list = this.FindControl<ListBox>("MemeList");
        string? selectedPath = (list?.SelectedItem as MemePickerRow)?.Item.FullPath ?? _preselectPath;
        _preselectPath = null;

        _rows = real.Select(i => new MemePickerRow(i, _portrait)).ToList();
        ApplyFilter();

        if (list != null && selectedPath != null)
        {
            var again = (list.ItemsSource as IEnumerable<MemePickerRow>)?.FirstOrDefault(r =>
                string.Equals(r.Item.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (again != null) list.SelectedItem = again;
        }

        _ = LoadThumbnailsAsync(_rows);
    }

    private void ApplyFilter()
    {
        string query = this.FindControl<TextBox>("SearchBox")?.Text?.Trim() ?? "";
        var shown = string.IsNullOrEmpty(query)
            ? _rows
            : _rows.Where(r => r.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        var list = this.FindControl<ListBox>("MemeList");
        if (list != null) list.ItemsSource = shown;

        var empty = this.FindControl<TextBlock>("EmptyText");
        if (empty != null) empty.IsVisible = shown.Count == 0;
    }

    private async Task LoadThumbnailsAsync(List<MemePickerRow> rows)
    {
        CancellationToken ct = _cts.Token;
        foreach (MemePickerRow row in rows)
        {
            if (ct.IsCancellationRequested || !ReferenceEquals(rows, _rows)) return;
            try
            {
                Bitmap? bmp = await Task.Run(() => MemeThumbnailCache.GetAsync(row.Item.FullPath, row.Item.IsImage, ct), ct);
                if (bmp != null) Dispatcher.UIThread.Post(() => row.Thumbnail = bmp);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        }
    }

    /// <summary>MEMEPICK_02 — re-scan the meme folder; only the newest scan may write.</summary>
    private async Task RescanAsync()
    {
        int generation = Interlocked.Increment(ref _scanGeneration);
        try
        {
            List<MemeItem> scanned = await MemeManagementService.ScanMemesAsync();
            if (generation != Volatile.Read(ref _scanGeneration) || _cts.IsCancellationRequested) return;
            ApplyItems(scanned);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MEME", $"Meme picker re-scan failed: {ex.Message}");
        }
    }

    private async Task DownloadAsync(MemeCategory category)
    {
        if (_downloading) return;
        _downloading = true;
        try
        {
            var (count, error) = await MemeManagementService.DownloadCloudMemesAsync(this, category);
            string label = MemeCatalog.LabelFor(category);
            if (error != null)
                await ErrorReporter.ShowAsync(this, $"Problem downloading {label}", error,
                    "See the log entries tagged [Memes] for the per-file detail.");
            else
                ShowStatus(count == 0
                    ? $"You already have all available {label}."
                    : $"✓ Downloaded {count} new {label}.");   // the re-scan was raised by NotifyChanged
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MEME", $"Meme download from the picker failed: {ex.Message}");
        }
        finally { _downloading = false; }
    }

    /// <summary>
    /// MEMEMODE_01 — a meme was chosen: show the cards and read its length for "+X.X sec longer"
    /// (a picture is <see cref="FreeVideoStudio.Core.Media.MemePlacement.StillImageDurationSec"/>; a video
    /// is probed off the UI thread, newest selection wins).
    /// </summary>
    private void OnMemeSelected(MemeItem? item)
    {
        Choice.HasMeme = item != null;
        int generation = Interlocked.Increment(ref _durationGeneration);
        if (item == null) return;
        if (item.IsImage) { Choice.DurationSec = FreeVideoStudio.Core.Media.MemePlacement.StillImageDurationSec; return; }
        Choice.DurationSec = null;
        _ = Task.Run(async () =>
        {
            try
            {
                string ffprobe = FreeVideoStudio.Core.Infrastructure.BinaryPathResolver.Resolve("ffprobe.exe", "backend", "binaries");
                double d = await new FreeVideoStudio.Core.Media.MediaProber(ffprobe, item.FullPath).GetDurationAsync();
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation == Volatile.Read(ref _durationGeneration)) Choice.DurationSec = d > 0.01 ? d : null;
                });
            }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        });
    }

    private void ShowStatus(string text)
    {
        var status = this.FindControl<TextBlock>("StatusText");
        if (status == null) return;
        status.Text = text;
        status.IsVisible = true;
    }

    /// <summary>
    /// MEME_06 — shows the picker and returns the choice, or null. <paramref name="onLatest"/>
    /// receives the newest scanned list (MEMEPICK_02) so the caller can keep its copy current.
    /// </summary>
    /// <para>MEMEMODE_01 — <paramref name="existing"/> opens the same screen to EDIT a placed meme:
    /// its file is pre-selected and its mode/corner/size/sound pre-set.</para>
    public static async Task<MemePickResult?> PickAsync(Window owner, IEnumerable<MemeItem> items,
        bool portrait = false, Action<IReadOnlyList<MemeItem>>? onLatest = null,
        FreeVideoStudio.Core.Media.MemePlacement? existing = null)
    {
        try
        {
            var dlg = new MemePickerWindow();
            if (existing != null)
            {
                dlg._preselectPath = existing.FilePath;
                dlg.Choice.LoadFrom(existing);
            }
            dlg.SetItems(items, portrait);
            await dlg.ShowDialog(owner);
            onLatest?.Invoke(dlg.LatestItems);
            if (dlg.Result is not MemeItem picked) return null;
            var c = dlg.Choice;
            return new MemePickResult(picked, c.DurationSec, c.Mode, c.Corner, c.Size, c.PlaySound);
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
