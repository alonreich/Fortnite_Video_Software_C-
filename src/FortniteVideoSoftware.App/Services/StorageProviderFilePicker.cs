// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FortniteVideoSoftware.App.Abstractions;
using FortniteVideoSoftware.Core.Abstractions;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.App.Services;

/// <summary>
/// SEAM_04 — the shipping <see cref="IFilePickerService"/>, over Avalonia's
/// <see cref="IStorageProvider"/>.
///
/// <para>
/// <b>PICKERMEMORY_01 — the remembered folder is written on SELECTION, not on completion.</b>
/// <c>05_SYSTEM_LIFECYCLE_STORAGE.md</c> §3 (SYS-WINSTATE) requires that a user-chosen directory is
/// persisted "immediately upon selection, even if the file picker dialog is subsequently
/// cancelled". That rule is owned HERE rather than at each call site, because a call site that
/// forgets it produces a picker that opens in the wrong folder every single time — a defect that
/// is individually trivial, collectively infuriating, and invisible in review.
/// </para>
///
/// <para>
/// The existing pickers in <c>MainWindow</c>, <c>VideoMergerWindow</c>, <c>CropToolWindow</c> and
/// <c>MusicWizardWindow</c> each re-implement this dance with their own try/catch. They are not
/// touched by this change — converting them belongs with each window's view-model extraction.
/// </para>
/// </summary>
public sealed class StorageProviderFilePicker : IFilePickerService
{
    private readonly IActiveWindowProvider _windows;
    private readonly IFaultSink _faults;

    public StorageProviderFilePicker(IActiveWindowProvider windows, IFaultSink faults)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _faults = faults ?? throw new ArgumentNullException(nameof(faults));
    }

    public Task<string?> SaveFileAsync(FilePickerRequest request) => RunAsync(request, save: true);

    public Task<string?> OpenFileAsync(FilePickerRequest request) => RunAsync(request, save: false);

    private async Task<string?> RunAsync(FilePickerRequest request, bool save)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The picker is a UI operation; callers may be on a worker (an autosave prompt, a recovery
        // flow). InvokeAsync rather than Post because the caller needs the answer.
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            Window? owner = _windows.ActiveWindow;
            if (owner is null)
            {
                // Degraded, not silent: a user who clicked Save and got nothing must be told why.
                _faults.Degraded("PICKER",
                    "The file dialog could not open because no window is active. Nothing was saved — try again from the main window.");
                return null;
            }

            IStorageProvider storage = owner.StorageProvider;

            var fileType = new FilePickerFileType(request.ExtensionLabel)
            {
                Patterns = new[] { "*." + request.Extension.TrimStart('.') }
            };

            IStorageFolder? start = await TryResolveStartFolderAsync(storage, request.StartDirectoryKey);

            string? chosen;
            if (save)
            {
                IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = request.Title,
                    SuggestedFileName = request.SuggestedFileName,
                    DefaultExtension = request.Extension.TrimStart('.'),
                    ShowOverwritePrompt = true,
                    FileTypeChoices = new[] { fileType },
                    SuggestedStartLocation = start
                });
                chosen = file?.TryGetLocalPath();
            }
            else
            {
                IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = request.Title,
                    AllowMultiple = false,
                    FileTypeFilter = new[] { fileType },
                    SuggestedStartLocation = start
                });
                chosen = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            }

            // PICKERMEMORY_01 — persist the folder the moment we have one, before the caller gets
            // a chance to fail, cancel or throw.
            if (chosen is not null) RememberDirectory(request.StartDirectoryKey, chosen);

            return chosen;
        });
    }

    private async Task<IStorageFolder?> TryResolveStartFolderAsync(IStorageProvider storage, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        string remembered = UiStateStore.ReadText(key!);
        if (string.IsNullOrWhiteSpace(remembered) || !Directory.Exists(remembered)) return null;

        try
        {
            return await storage.TryGetFolderFromPathAsync(new Uri(remembered));
        }
        catch (Exception ex)
        {
            // Recoverable by definition: the dialog still opens, just at the OS default.
            // The user's outcome is unchanged, so nothing reaches the screen.
            _faults.Recoverable("PICKER", $"Remembered folder '{remembered}' could not be resolved: {ex.Message}", ex);
            return null;
        }
    }

    private void RememberDirectory(string? key, string chosenFilePath)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        _faults.Guard("PICKER",
            "Your chosen folder could not be remembered — the next dialog may open somewhere else. Everything else is unaffected.",
            () =>
            {
                string? dir = Path.GetDirectoryName(chosenFilePath);
                if (!string.IsNullOrWhiteSpace(dir)) UiStateStore.WriteText(key!, dir!);
            },
            FaultTier.Recoverable);
    }
}
