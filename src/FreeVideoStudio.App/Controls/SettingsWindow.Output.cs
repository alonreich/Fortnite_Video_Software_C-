// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.IO;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Controls;

/// <summary>
/// OUTNAME_01 — Settings › Output Files. Edits, per tool, the save folder
/// (<see cref="AppSettings.MainOutputDirectory"/> / <see cref="AppSettings.MergerOutputDirectory"/>,
/// ISSUE_04) and the automatic file-name base (<see cref="AppSettings.MainOutputBaseName"/> /
/// <see cref="AppSettings.MergerOutputBaseName"/>). Values are pending until SAVE, exactly like
/// every other tab; <see cref="ApplyPendingOutputSettings"/> writes them inside the single
/// SETTX_01 transaction in <c>SaveAndClose</c>.
///
/// MVVM_01 — the tab is driven by compiled bindings to the direct properties below and by XAML
/// click handlers; it performs no imperative control lookups.
///
/// An empty folder means "the real Windows Downloads folder, resolved at export time"
/// (<see cref="OutputFolderResolver"/>). A folder is only accepted when it is writable now.
/// </summary>
public partial class SettingsWindow
{
    private string _pendingMainOutputDir = SettingsManager.Instance.MainOutputDirectory ?? "";
    private string _pendingMergerOutputDir = SettingsManager.Instance.MergerOutputDirectory ?? "";
    private string _pendingMainOutputBase = SettingsManager.Instance.MainOutputBaseName ?? OutputFileNaming.MainDefaultBaseName;
    private string _pendingMergerOutputBase = SettingsManager.Instance.MergerOutputBaseName ?? OutputFileNaming.MergerDefaultBaseName;
    private readonly string _previousMergerOutputDir = SettingsManager.Instance.MergerOutputDirectory ?? "";

    private string _mainOutputFolderDisplay = "";
    private string _mergerOutputFolderDisplay = "";
    private string _mainOutputNamePreview = "";
    private string _mergerOutputNamePreview = "";
    private string _outputFilesStatus = "";

    public static readonly DirectProperty<SettingsWindow, string> MainOutputBaseNameTextProperty =
        AvaloniaProperty.RegisterDirect<SettingsWindow, string>(nameof(MainOutputBaseNameText),
            o => o.MainOutputBaseNameText, (o, v) => o.MainOutputBaseNameText = v);

    public static readonly DirectProperty<SettingsWindow, string> MergerOutputBaseNameTextProperty =
        AvaloniaProperty.RegisterDirect<SettingsWindow, string>(nameof(MergerOutputBaseNameText),
            o => o.MergerOutputBaseNameText, (o, v) => o.MergerOutputBaseNameText = v);

    public static readonly DirectProperty<SettingsWindow, string> MainOutputFolderDisplayProperty =
        AvaloniaProperty.RegisterDirect<SettingsWindow, string>(nameof(MainOutputFolderDisplay), o => o.MainOutputFolderDisplay);

    public static readonly DirectProperty<SettingsWindow, string> MergerOutputFolderDisplayProperty =
        AvaloniaProperty.RegisterDirect<SettingsWindow, string>(nameof(MergerOutputFolderDisplay), o => o.MergerOutputFolderDisplay);

    public static readonly DirectProperty<SettingsWindow, string> MainOutputNamePreviewProperty =
        AvaloniaProperty.RegisterDirect<SettingsWindow, string>(nameof(MainOutputNamePreview), o => o.MainOutputNamePreview);

    public static readonly DirectProperty<SettingsWindow, string> MergerOutputNamePreviewProperty =
        AvaloniaProperty.RegisterDirect<SettingsWindow, string>(nameof(MergerOutputNamePreview), o => o.MergerOutputNamePreview);

    public static readonly DirectProperty<SettingsWindow, string> OutputFilesStatusProperty =
        AvaloniaProperty.RegisterDirect<SettingsWindow, string>(nameof(OutputFilesStatus), o => o.OutputFilesStatus);

    /// <summary>Main App automatic file name, bound two-way to its text box.</summary>
    public string MainOutputBaseNameText
    {
        get => _pendingMainOutputBase;
        set
        {
            SetAndRaise(MainOutputBaseNameTextProperty, ref _pendingMainOutputBase, value ?? "");
            RefreshOutputDisplays();
        }
    }

    /// <summary>Video Merger automatic file name, bound two-way to its text box.</summary>
    public string MergerOutputBaseNameText
    {
        get => _pendingMergerOutputBase;
        set
        {
            SetAndRaise(MergerOutputBaseNameTextProperty, ref _pendingMergerOutputBase, value ?? "");
            RefreshOutputDisplays();
        }
    }

    public string MainOutputFolderDisplay
    {
        get => _mainOutputFolderDisplay;
        private set => SetAndRaise(MainOutputFolderDisplayProperty, ref _mainOutputFolderDisplay, value);
    }

    public string MergerOutputFolderDisplay
    {
        get => _mergerOutputFolderDisplay;
        private set => SetAndRaise(MergerOutputFolderDisplayProperty, ref _mergerOutputFolderDisplay, value);
    }

    public string MainOutputNamePreview
    {
        get => _mainOutputNamePreview;
        private set => SetAndRaise(MainOutputNamePreviewProperty, ref _mainOutputNamePreview, value);
    }

    public string MergerOutputNamePreview
    {
        get => _mergerOutputNamePreview;
        private set => SetAndRaise(MergerOutputNamePreviewProperty, ref _mergerOutputNamePreview, value);
    }

    public string OutputFilesStatus
    {
        get => _outputFilesStatus;
        private set => SetAndRaise(OutputFilesStatusProperty, ref _outputFilesStatus, value);
    }

    private void BuildOutputFilesUi() => RefreshOutputDisplays();

    private void RefreshOutputDisplays()
    {
        MainOutputFolderDisplay = FolderDisplay(_pendingMainOutputDir);
        MergerOutputFolderDisplay = FolderDisplay(_pendingMergerOutputDir);
        MainOutputNamePreview = NamePreview(_pendingMainOutputBase, OutputFileNaming.MainDefaultBaseName);
        MergerOutputNamePreview = NamePreview(_pendingMergerOutputBase, OutputFileNaming.MergerDefaultBaseName);
    }

    private static string FolderDisplay(string folder) => string.IsNullOrWhiteSpace(folder)
        ? "Downloads (" + (KnownFolders.GetDownloads() ?? "not available on this PC") + ")"
        : folder;

    private static string NamePreview(string baseName, string fallback)
    {
        string safe = OutputFileNaming.Sanitize(baseName, fallback);
        return "Next files: " + OutputFileNaming.NumberedFileName(safe, 1) + ", " +
               OutputFileNaming.NumberedFileName(safe, 2) + ", " +
               OutputFileNaming.NumberedFileName(safe, 3) + " ...";
    }

    private void OnChangeMainOutputFolder(object? sender, RoutedEventArgs e) =>
        _ = ChangeOutputFolderAsync(merger: false);

    private void OnChangeMergerOutputFolder(object? sender, RoutedEventArgs e) =>
        _ = ChangeOutputFolderAsync(merger: true);

    /// <summary>ASYNCUI_02 — Task-returning; <see cref="PickOutputFolderAsync"/> never throws.</summary>
    private async System.Threading.Tasks.Task ChangeOutputFolderAsync(bool merger)
    {
        string? picked = await PickOutputFolderAsync(
            merger ? _pendingMergerOutputDir : _pendingMainOutputDir,
            merger ? "Choose where the Video Merger saves merged videos" : "Choose where the Main App saves finished videos");
        if (picked == null) return;
        if (merger) _pendingMergerOutputDir = picked;
        else _pendingMainOutputDir = picked;
        RefreshOutputDisplays();
    }

    private void OnResetMainOutputFolder(object? sender, RoutedEventArgs e)
    {
        _pendingMainOutputDir = "";
        OutputFilesStatus = "";
        RefreshOutputDisplays();
    }

    private void OnResetMergerOutputFolder(object? sender, RoutedEventArgs e)
    {
        _pendingMergerOutputDir = "";
        OutputFilesStatus = "";
        RefreshOutputDisplays();
    }

    private void OnResetMainOutputBaseName(object? sender, RoutedEventArgs e) =>
        MainOutputBaseNameText = OutputFileNaming.MainDefaultBaseName;

    private void OnResetMergerOutputBaseName(object? sender, RoutedEventArgs e) =>
        MergerOutputBaseNameText = OutputFileNaming.MergerDefaultBaseName;

    /// <summary>Returns a writable folder the user picked, or null (cancelled / unusable — status explains).</summary>
    private async System.Threading.Tasks.Task<string?> PickOutputFolderAsync(string current, string title)
    {
        try
        {
            IStorageFolder? start = null;
            foreach (string? candidate in new[] { current, KnownFolders.GetDownloads(), KnownFolders.GetVideos() })
            {
                if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate)) continue;
                start = await StorageProvider.TryGetFolderFromPathAsync(candidate);
                if (start != null) break;
            }

            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                SuggestedStartLocation = start,
                AllowMultiple = false
            });
            if (picked == null || picked.Count == 0) return null;

            string? path = picked[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path) || !KnownFolders.IsWritableDirectory(path))
            {
                OutputFilesStatus = "⚠ That folder cannot be written to. Pick a different folder.";
                RuntimeLog.Fail("Output", "Settings: selected output folder is not writable.");
                RuntimeLog.Debug("Output", $"Unwritable output folder: {path}");
                return null;
            }

            OutputFilesStatus = "";
            return path;
        }
        catch (Exception ex)
        {
            OutputFilesStatus = "⚠ The folder picker failed. The previous folder is kept.";
            RuntimeLog.Fail("Output", $"Settings output folder picker failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>OUTNAME_01 — applied inside the SaveAndClose settings transaction.</summary>
    private void ApplyPendingOutputSettings(AppSettings s)
    {
        s.MainOutputDirectory = _pendingMainOutputDir ?? "";
        s.MergerOutputDirectory = _pendingMergerOutputDir ?? "";
        s.MainOutputBaseName = OutputFileNaming.Sanitize(_pendingMainOutputBase, OutputFileNaming.MainDefaultBaseName);
        s.MergerOutputBaseName = OutputFileNaming.Sanitize(_pendingMergerOutputBase, OutputFileNaming.MergerDefaultBaseName);
    }

    /// <summary>
    /// ISSUE_04 — the Video Merger also restores its folder from the IPC state store when it opens.
    /// Keep that copy in step after a committed change so the merger shows the folder chosen here.
    /// </summary>
    private void SyncMergerOutputDirectoryState()
    {
        if (string.Equals(_previousMergerOutputDir, _pendingMergerOutputDir, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            new FreeVideoStudio.Core.Ipc.StateTransferStore(_paths)
                .UpdatePropertiesSync(new System.Text.Json.Nodes.JsonObject
                {
                    ["MergerOutputDirectory"] = _pendingMergerOutputDir ?? ""
                });
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }
}
