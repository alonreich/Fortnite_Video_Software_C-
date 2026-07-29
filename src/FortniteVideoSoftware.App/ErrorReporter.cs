using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace FortniteVideoSoftware.App;

/// <summary>
/// The ONE place any user-visible failure is reported from.
///
/// Contract (do not weaken): whenever something fails in a way the user can perceive, call
/// <see cref="ShowAsync"/>. The user always gets a modal dialog with
///   (a) a plain-English sentence describing what failed,
///   (b) the single most relevant root-cause line pulled out of the raw error / the log tail,
///   (c) OKAY and SHOW LOGS buttons.
/// A bare toast (ShowTacticalFeedback) is NEVER sufficient on its own for a failure — it may
/// accompany the dialog, but must not replace it.
///
/// This class is UI-thread safe: it marshals itself onto the dispatcher and is re-entrancy
/// guarded so a burst of failures cannot stack a tower of modals on top of each other.
/// </summary>
public static class ErrorReporter
{
    private static bool _dialogOpen;

    /// <summary>
    /// Raw FFmpeg / native fragments that are noise, never a root cause. Filtered out before
    /// ranking so the dialog does not show the user "frame= 1234 fps=60".
    /// </summary>
    private static readonly string[] NoisePrefixes =
    {
        "frame=", "size=", "bitrate=", "speed=", "video:", "audio:", "Press [q]",
        "built with", "configuration:", "libav", "libsw", "libpost", "Input #", "Output #",
        "Stream #", "Metadata:", "encoder ", "  ", "Duration:"
    };

    /// <summary>
    /// Ranked root-cause signatures, strongest first. The first match in the raw text wins,
    /// and each carries a plain-English translation so the user is not handed FFmpeg jargon
    /// with no explanation. Order matters — put the specific ones above the generic ones.
    /// </summary>
    private static readonly (string Needle, string PlainEnglish)[] KnownCauses =
    {
        ("No space left on device",
            "The drive ran out of free space while writing the video."),
        ("Disk quota exceeded",
            "The drive ran out of free space while writing the video."),
        ("moov atom not found",
            "The source video file is incomplete or corrupt, so it could not be read."),
        ("Invalid data found when processing input",
            "The source file is corrupt or is not a video format the app can read."),
        ("Impossible to convert between the formats",
            "The video filter chain could not process the frames it was given. This is an internal pipeline error, not a problem with your file."),
        ("Unknown encoder",
            "The requested video encoder is not available in this build of FFmpeg."),
        ("Cannot load nvcuda",
            "The NVIDIA driver could not be loaded, so the graphics card cannot be used for this export."),
        ("OpenEncodeSessionEx failed",
            "The graphics card refused a new encoding session. Close other apps that are recording or streaming and try again."),
        ("No NVENC capable devices found",
            "No NVIDIA encoder was found on this machine."),
        ("Permission denied",
            "Windows blocked access to a file or folder needed for this operation."),
        ("Access is denied",
            "Windows blocked access to a file or folder needed for this operation."),
        ("No such file or directory",
            "A file the app needed was missing or had been moved."),
        ("Output file is empty",
            "FFmpeg produced an empty file — nothing was encoded."),
        ("Conversion failed",
            "FFmpeg stopped before it finished encoding."),
        ("Error initializing",
            "A component of the encoding pipeline failed to start."),
        ("Error opening",
            "A file or device could not be opened."),
        ("Error while",
            "FFmpeg hit an error part-way through the job."),
        ("Invalid argument",
            "FFmpeg rejected one of the settings the app sent it."),
    };

    /// <summary>
    /// Shows the standard failure dialog. Safe to call from any thread.
    /// </summary>
    /// <param name="owner">Owning window for modality. Null shows a non-owned dialog.</param>
    /// <param name="title">Short title, e.g. "Export failed".</param>
    /// <param name="message">
    /// Plain-English description of the operation that failed. If a known root cause is
    /// detected, its translation is appended so the user knows WHY, not just WHAT.
    /// </param>
    /// <param name="rawDetail">
    /// The raw error text (exception message, FFmpeg stderr tail, etc.). When null or when no
    /// cause can be found in it, the tail of the log file is scanned instead.
    /// </param>
    public static async Task ShowAsync(Window? owner, string title, string message, string? rawDetail = null)
    {
        string rootCause = ExtractRootCause(rawDetail);
        string explanation = TranslateCause(rawDetail, rootCause);

        string fullMessage = string.IsNullOrEmpty(explanation)
            ? message
            : message + Environment.NewLine + Environment.NewLine + explanation;

        RuntimeLog.Fail(title.ToUpperInvariant(), message);
        if (!string.IsNullOrWhiteSpace(rootCause))
        {
            RuntimeLog.Fail(title.ToUpperInvariant(), $"Root cause: {rootCause}");
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            await ShowCoreAsync(owner, title, fullMessage, rootCause);
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(
                () => ShowCoreAsync(owner, title, fullMessage, rootCause));
        }
    }

    /// <summary>Fire-and-forget convenience for call sites that cannot await (event handlers).</summary>
    public static void Show(Window? owner, string title, string message, string? rawDetail = null)
    {
        _ = ShowAsync(owner, title, message, rawDetail);
    }

    private static async Task ShowCoreAsync(Window? owner, string title, string message, string? rootCause)
    {
        if (_dialogOpen)
        {
            RuntimeLog.Info("ErrorDialog", $"Suppressed a second failure dialog while one was open: {title}");
            return;
        }

        _dialogOpen = true;
        try
        {
            var dlg = new Controls.ErrorDialogWindow();
            dlg.SetTitle(title);
            dlg.SetMessage(message);
            dlg.SetDetail(rootCause);
            dlg.SetLogPath(RuntimeLog.LogPath);

            if (owner != null && owner.IsVisible)
            {
                await dlg.ShowDialog(owner);
            }
            else
            {
                dlg.Show();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("ErrorDialog", $"Failed to display the error dialog: {ex.Message}");
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    /// <summary>
    /// Picks the ONE line most likely to be the actual root cause.
    /// Strategy: scan the supplied raw text for a known signature; if that fails, take the last
    /// non-noise line of the raw text; if there is no raw text at all, tail the log file for the
    /// most recent FAIL/FATAL entry.
    /// </summary>
    public static string ExtractRootCause(string? rawDetail)
    {
        string? fromRaw = ScanForCause(rawDetail);
        if (!string.IsNullOrWhiteSpace(fromRaw)) return Truncate(fromRaw!);

        string? fromLog = ScanForCause(ReadLogTail(200));
        if (!string.IsNullOrWhiteSpace(fromLog)) return Truncate(fromLog!);

        string? lastLine = LastMeaningfulLine(rawDetail);
        return lastLine != null ? Truncate(lastLine) : string.Empty;
    }

    private static string? ScanForCause(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                             .Select(l => l.TrimEnd('\r').Trim())
                             .Where(l => l.Length > 0 && !IsNoise(l))
                             .ToArray();
        if (lines.Length == 0) return null;

        foreach ((string needle, _) in KnownCauses)
        {
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (lines[i].Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    return lines[i];
                }
            }
        }

        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string l = lines[i];
            if (l.Contains("[FAIL]", StringComparison.Ordinal) ||
                l.Contains("[FATAL]", StringComparison.Ordinal) ||
                l.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                return l;
            }
        }

        return null;
    }

    private static string? LastMeaningfulLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                   .Select(l => l.TrimEnd('\r').Trim())
                   .LastOrDefault(l => l.Length > 0 && !IsNoise(l));
    }

    private static bool IsNoise(string line)
    {
        foreach (string p in NoisePrefixes)
        {
            if (line.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Maps a detected root cause onto a sentence a non-technical user can act on.</summary>
    private static string TranslateCause(string? rawDetail, string rootCause)
    {
        string haystack = (rawDetail ?? string.Empty) + "\n" + rootCause;
        foreach ((string needle, string plain) in KnownCauses)
        {
            if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase)) return plain;
        }
        return string.Empty;
    }

    /// <summary>
    /// Reads the last N lines of the active log without loading the whole (up to 10 MB) file.
    /// Read-share is permissive because the logger holds an append handle on the same file.
    /// </summary>
    private static string? ReadLogTail(int lineCount)
    {
        try
        {
            string path = RuntimeLog.LogPath;
            if (!File.Exists(path)) return null;

            const int chunkSize = 64 * 1024;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            long length = fs.Length;
            int toRead = (int)Math.Min(length, chunkSize);
            fs.Seek(length - toRead, SeekOrigin.Begin);

            byte[] buffer = new byte[toRead];
            int read = fs.Read(buffer, 0, toRead);
            string text = Encoding.UTF8.GetString(buffer, 0, read);

            string[] lines = text.Split('\n');
            IEnumerable<string> tail = lines.Length > lineCount
                ? lines.Skip(lines.Length - lineCount)
                : lines;

            return string.Join("\n", tail);
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string value)
    {
        const int max = 600;
        value = value.Trim();
        return value.Length <= max ? value : value[..max] + " …";
    }
}
