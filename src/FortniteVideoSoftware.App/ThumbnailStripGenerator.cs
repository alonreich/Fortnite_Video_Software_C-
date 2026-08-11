using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.App;

/// <summary>
/// LANES_01 — builds the "film strip" image that fills a 60px timeline lane.
///
/// Renders N evenly-spaced frames of a video range into ONE tiled PNG (`tile=Nx1`), 60px tall, so
/// the caller can stretch a single Image across the lane instead of managing N separate bitmaps.
///
/// ── WHY IT LIVES HERE ────────────────────────────────────────────────────────────────────────
/// This logic was written for Music Wizard phase 3 and lived as a private method on that window.
/// The Granular Speed Editor now needs the identical strip, and copying it would have created a
/// second implementation free to drift — different frame counts, different scaling, a different
/// temp-file cleanup story. `MusicWizardWindow.GenerateThumbnailsStripAsync` now delegates here,
/// so there is exactly one.
///
/// ── CALLER OWNS THE FILE ─────────────────────────────────────────────────────────────────────
/// On success this returns a path to a temp PNG the caller MUST delete when it is done (both
/// callers track theirs in a list and delete on teardown). It is not cached and never reused
/// between runs — see mandate #1.
/// </summary>
public static class ThumbnailStripGenerator
{
    /// <summary>Frames per strip. 15 is what phase 3 shipped with and reads well at lane width.</summary>
    public const int DefaultFrames = 15;

    /// <summary>Lane height in pixels; frames are scaled to exactly this tall.</summary>
    public const int StripHeightPx = 60;

    /// <summary>
    /// Renders <paramref name="frames"/> stills from <paramref name="videoPath"/>, starting at
    /// <paramref name="startSec"/> and covering <paramref name="durationSec"/>, into one wide PNG.
    /// Returns null on any failure — a missing strip must degrade to an empty lane, never to an
    /// error the user has to dismiss.
    /// </summary>
    public static async Task<string?> GenerateAsync(
        string ffmpegPath,
        string videoPath,
        string tempDirectory,
        double startSec,
        double durationSec,
        CancellationToken cancellationToken,
        int frames = DefaultFrames,
        string logTag = "Filmstrip")
    {
        string? tempPng = null;
        Process? process = null;
        try
        {
            Directory.CreateDirectory(tempDirectory);
            tempPng = Path.Combine(tempDirectory, $"fvs_thumb_{Guid.NewGuid():N}.png");
            if (durationSec <= 0) durationSec = 10;
            frames = Math.Max(1, frames);

            double fps = frames / durationSec;
            string startArg = startSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            string durationArg = durationSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            string fpsArg = fps.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

            // `tpad=stop_mode=clone` holds the last frame so a range that yields slightly fewer
            // frames than requested still tiles to the full width instead of leaving a gap.
            string filter = $"fps=fps={fpsArg}:round=up,scale=-1:{StripHeightPx},tpad=stop_mode=clone:stop_duration=1,tile={frames}x1:margin=0:padding=0";

            var stripArgs = new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-ss", startArg,
                "-t", durationArg,
                "-i", videoPath,
                "-vf", filter,
                "-frames:v", "1",
                tempPng
            };

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string arg in stripArgs) psi.ArgumentList.Add(arg);

            process = Process.Start(psi);
            if (process == null) return null;

            try { ChildProcessTracker.AddProcess(process); } catch (System.Exception ex) { Debug.WriteLine(ex.ToString()); }

            Task<string> stripOut = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stripErr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            _ = await stripOut;
            string stripErrText = string.Empty;
            try { stripErrText = await stripErr; } catch (System.Exception ex) { Debug.WriteLine(ex.ToString()); }

            if (process.ExitCode == 0 && File.Exists(tempPng)) return tempPng;

            RuntimeLog.Fail(logTag, $"Filmstrip render failed (exit {process.ExitCode}).");
            if (!string.IsNullOrWhiteSpace(stripErrText))
                RuntimeLog.Debug(logTag, $"FFmpeg stderr:\n{stripErrText.Trim()}");
        }
        catch (OperationCanceledException)
        {
            try { if (process != null && !process.HasExited) process.Kill(entireProcessTree: true); }
            catch (System.Exception ex) { Debug.WriteLine(ex.ToString()); }
            if (tempPng != null && File.Exists(tempPng))
            {
                try { File.Delete(tempPng); } catch (System.Exception ex) { Debug.WriteLine(ex.ToString()); }
            }
            throw;
        }
        catch (System.Exception ex) { Debug.WriteLine(ex.ToString()); }
        finally
        {
            try { process?.Dispose(); } catch (System.Exception ex) { Debug.WriteLine(ex.ToString()); }
        }

        return null;
    }
}
