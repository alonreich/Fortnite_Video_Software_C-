// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Models;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

public static class MemeManagementService
{
    public static string ResolveFfprobePath()
    {
        return BinaryPathResolver.Resolve("ffprobe.exe", "backend", "binaries");
    }

    public static async Task<List<MemeItem>> ScanMemesAsync()
    {
        string memeFolder = MemeDirectory.GetActive();
        try
        {
            return await MemeCatalog.ScanAsync(memeFolder, ResolveFfprobePath());
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Memes", $"Meme directory scan failed: {ex.Message}");
            return new List<MemeItem>();
        }
    }

    public static IDataTemplate CreateMemeItemTemplate(Window parent, Func<bool> isPortraitGetter)
    {
        return new FuncDataTemplate<MemeItem>((item, _) =>
        {
            if (item == null) return new TextBlock();
            var tb = new TextBlock { Text = item.FileName, VerticalAlignment = VerticalAlignment.Center };
            if (item.IsDownloadAction)
            {
                bool pictures = item.DownloadCategory == MemeCategory.Image;   // MEMECAT_01
                tb.Text = pictures
                    ? "⬇  Download more meme PICTURES…"
                    : "⬇  Download more meme VIDEOS…";
                tb.FontWeight = FontWeight.Bold;
                tb.Foreground = new SolidColorBrush(Color.Parse("#38bdf8"));
                ToolTip.SetTip(tb, pictures
                    ? "Fetch more still-image memes from the official library. Files you already have are skipped, never overwritten."
                    : "Fetch more video memes from the official library. Files you already have are skipped, never overwritten.");
            }
            else if (isPortraitGetter() && item.AspectRatio > 0.85f)
            {
                tb.Foreground = ThemeResources.Brush(parent, "AppDangerBrush", Brushes.Red);
                ToolTip.SetTip(tb, "Fit for landscape");
            }
            return tb;
        });
    }

    /// <summary>
    /// DOWNLOAD_01 — confirm, then fetch one category from the single `meme/` library folder
    /// (MEMEFOLDER_01). Shared by the main-screen combo rows and the meme picker (MEMEPICK_01).
    /// On a successful download every open meme list is told to re-scan through
    /// <see cref="MemeDirectory.NotifyChanged"/>, so the main combo and the picker never disagree.
    /// </summary>
    public static async Task<(int Count, string? Error)> DownloadCloudMemesAsync(Window parent, MemeCategory category)
    {
        string label = MemeCatalog.LabelFor(category);

        var dlg = new ConfirmDialogWindow();
        dlg.SetTitle($"Download more {label}?");
        dlg.SetMessage(
            $"This connects to the internet and copies new {label} from the official library into your meme folder.\n\n" +
            "Anything you already have is left alone — nothing is replaced or deleted. " +
            "You can press this again any time to pick up whatever is new.");
        dlg.SetButtonText("DOWNLOAD", "CANCEL");
        await dlg.ShowDialog(parent);
        if (!dlg.Result) return (0, null);

        string memeDir = MemeDirectory.GetActive();
        var result = await CloudSyncProgressWindow.RunAsync(
            parent, $"Downloading {label}",
            (progress, ct) => MemeCatalog.SyncMemesAsync(memeDir, category, progress, ct));
        if (result.Item1 > 0) MemeDirectory.NotifyChanged();
        return result;
    }

    public static async Task<double> ProbeMusicDurationSecondsAsync(string ffmpegPath, string musicPath)
    {
        try
        {
            string ffprobePath = ffmpegPath.Replace("ffmpeg.exe", "ffprobe.exe");
            if (!File.Exists(ffprobePath)) ffprobePath = ResolveFfprobePath();
            var prober = new FreeVideoStudio.Core.Media.MediaProber(ffprobePath, musicPath);
            return await prober.GetDurationAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Audio", $"Failed to probe music file '{musicPath}': {ex.Message}");
            return 0;
        }
    }
}
