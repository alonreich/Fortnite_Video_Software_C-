using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.App.Infrastructure;

/// <summary>
/// ISSUE_04 — resolves WHERE a finished video is written, for each sub-application separately.
///
/// Resolution order (do not reorder):
///   1. The folder saved in settings for THIS app, if it still exists and is writable.
///   2. The user's REAL Downloads folder via <see cref="KnownFolders.GetDownloads"/> — this is
///      shell-resolved, so a moved Downloads folder, a OneDrive-redirected one, or a Group
///      Policy redirected one all resolve correctly. (The old code hardcoded
///      %USERPROFILE%\Downloads and was simply wrong on those machines.)
///   3. Only if neither is usable: ask the user to pick a folder ("Save As"). Whatever they
///      pick is SAVED as the new default for that app, so they are asked exactly once.
///
/// Hard rule: this never throws and never crashes the app when Downloads is missing. If the
/// user cancels the picker it returns null and the caller aborts the export cleanly with the
/// standard error dialog — it must not fall through into the pipeline with a null path.
/// </summary>
public static class OutputFolderResolver
{
    public enum AppScope
    {
        Main,
        Merger
    }

    private static string GetConfigured(AppScope scope) => scope switch
    {
        AppScope.Merger => SettingsManager.Instance.MergerOutputDirectory,
        _ => SettingsManager.Instance.MainOutputDirectory
    };

    private static void SetConfigured(AppScope scope, string path)
    {
        if (scope == AppScope.Merger) SettingsManager.Instance.MergerOutputDirectory = path;
        else SettingsManager.Instance.MainOutputDirectory = path;

        SettingsManager.Save();
        RuntimeLog.Info("Output", $"{scope} output folder set and saved as the new default.");
        RuntimeLog.Debug("Output", $"{scope} output folder path: {path}");
    }

    /// <summary>
    /// Non-interactive best guess, for UI labels ("Saves to: ...") and pre-flight disk checks.
    /// Returns null when the user would have to be prompted.
    /// </summary>
    public static string? PeekDirectory(AppScope scope)
    {
        string configured = GetConfigured(scope);
        if (!string.IsNullOrWhiteSpace(configured) && KnownFolders.IsWritableDirectory(configured))
        {
            return configured;
        }

        string? downloads = KnownFolders.GetDownloads();
        return KnownFolders.IsWritableDirectory(downloads) ? downloads : null;
    }

    /// <summary>
    /// Full interactive resolution. Call this once, before starting a render.
    /// Returns the directory to write into, or null if the user cancelled the picker.
    /// </summary>
    public static async Task<string?> ResolveAsync(Window owner, AppScope scope)
    {
        string configured = GetConfigured(scope);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (KnownFolders.IsWritableDirectory(configured))
            {
                return configured;
            }

            RuntimeLog.Fail("Output",
                $"{scope} saved output folder is missing or not writable — falling back to Downloads.");
            RuntimeLog.Debug("Output", $"Unusable saved output folder: {configured}");
        }

        string? downloads = KnownFolders.GetDownloads();
        if (KnownFolders.IsWritableDirectory(downloads))
        {
            RuntimeLog.Info("Output", $"{scope} using the system Downloads folder.");
            return downloads;
        }

        RuntimeLog.Fail("Output",
            downloads == null
                ? "The Downloads folder could not be resolved on this machine."
                : "The Downloads folder was resolved but is not writable.");

        string? picked = await PromptForFolderAsync(owner, scope, downloads);
        if (picked == null)
        {
            RuntimeLog.Info("Output", $"{scope} export cancelled — the user did not choose an output folder.");
            return null;
        }

        SetConfigured(scope, picked);
        return picked;
    }

    /// <summary>
    /// Lets the user change the destination on purpose (Settings). Returns the new folder or
    /// null if cancelled. Unlike <see cref="ResolveAsync"/> this never explains a failure.
    /// </summary>
    public static async Task<string?> ChooseAsync(Window owner, AppScope scope)
    {
        string? picked = await ShowFolderPickerAsync(owner, GetConfigured(scope), scope);
        if (picked == null) return null;

        if (!KnownFolders.IsWritableDirectory(picked))
        {
            await ErrorReporter.ShowAsync(owner, "Folder not usable",
                "That folder cannot be written to, so it was not saved. Pick a different folder.",
                $"Selected output folder is not writable: {picked}");
            return null;
        }

        SetConfigured(scope, picked);
        return picked;
    }

    private static async Task<string?> PromptForFolderAsync(Window owner, AppScope scope, string? attemptedDownloads)
    {
        string appLabel = scope == AppScope.Merger ? "merged video" : "exported video";

        var confirm = new Controls.ConfirmDialogWindow();
        confirm.SetTitle("Choose where to save");
        confirm.SetMessage(
            "Your Downloads folder could not be used, so the " + appLabel + " has nowhere to go." +
            Environment.NewLine + Environment.NewLine +
            (attemptedDownloads == null
                ? "Windows did not report a Downloads folder for this account."
                : "Tried: " + attemptedDownloads) +
            Environment.NewLine + Environment.NewLine +
            "Choose a folder now? It will be remembered as your default for this tool.");
        confirm.SetButtonText("CHOOSE FOLDER", "CANCEL");
        await confirm.ShowDialog(owner);

        if (!confirm.Result) return null;

        return await ShowFolderPickerAsync(owner, null, scope);
    }

    private static async Task<string?> ShowFolderPickerAsync(Window owner, string? startAt, AppScope scope)
    {
        try
        {
            IStorageFolder? start = null;

            foreach (string? candidate in new[] { startAt, KnownFolders.GetVideos(), KnownFolders.GetDownloads() })
            {
                if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate)) continue;
                try
                {
                    start = await owner.StorageProvider.TryGetFolderFromPathAsync(candidate);
                    if (start != null) break;
                }
                catch
                {
                }
            }

            var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = scope == AppScope.Merger
                    ? "Choose where merged videos are saved"
                    : "Choose where exported videos are saved",
                AllowMultiple = false,
                SuggestedStartLocation = start
            });

            if (folders == null || folders.Count == 0) return null;

            string? path = folders[0].TryGetLocalPath();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Output", $"Folder picker failed: {ex.Message}");
            return null;
        }
    }
}
