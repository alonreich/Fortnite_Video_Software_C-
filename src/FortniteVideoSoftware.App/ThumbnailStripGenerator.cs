using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
    /// STRIP_01 — the crossover between the two render strategies, in seconds of video per
    /// thumbnail (<c>durationSec / frames</c>).
    ///
    /// AT OR ABOVE IT: N keyframe-seeked inputs. Decodes N frames instead of the whole range.
    /// BELOW IT: the full-range sweep. Counter-intuitive but measured — on a short range the sweep
    /// is CHEAPER than seeking, because an accurate seek re-decodes from the preceding keyframe
    /// every time (15 samples x half a GOP beats decoding 6 seconds outright), and a keyframe seek
    /// is not usable at all: with spacing below one GOP several samples collapse onto the SAME
    /// keyframe and the lane shows one picture repeated.
    ///
    /// ⚠️ THERE IS DELIBERATELY NO ACCURATE-SEEK PATH. It was implemented and benchmarked and it
    /// loses on BOTH sides of this crossover — 5474ms vs 1488ms for the sweep on a 6s range, and
    /// 2846ms vs 799ms for keyframe seeking on a 60s one. Do not reintroduce it as a "compromise".
    ///
    /// 2.0s is above a typical capture GOP, so whenever the seeked path runs, the keyframe snap is
    /// smaller than the spacing between samples and cannot produce a duplicate.
    /// </summary>
    private const double SeekedPathMinSpacingSec = 2.0;

    /// <summary>
    /// Renders <paramref name="frames"/> stills from <paramref name="videoPath"/>, starting at
    /// <paramref name="startSec"/> and covering <paramref name="durationSec"/>, into one wide PNG.
    /// Returns null on any failure — a missing strip must degrade to an empty lane, never to an
    /// error the user has to dismiss.
    ///
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// STRIP_01 — WHY THIS NO LONGER SWEEPS THE WHOLE RANGE.
    ///
    /// The original command was `-ss start -t dur -i v -vf fps=N/dur,...,tile=Nx1`. The `-ss` is a
    /// fast input seek, but `-t dur` then makes FFmpeg DECODE EVERY FRAME in the range so the `fps`
    /// filter can throw almost all of them away. On a 2560x1440 120fps capture — what this app is
    /// built for — an 85-second clip decodes ~10,200 frames to produce 15 thumbnails. That is the
    /// entire reason the film lane took so long to appear on every screen that has one.
    ///
    /// It now opens the SAME file N times, each input carrying its own `-ss` to the exact moment
    /// that thumbnail represents, takes one frame from each (`trim=end_frame=1`) and `hstack`s them.
    /// Decode cost drops from "the whole range" to "N frames". MEASURED on a 60s 1920x1080 60fps
    /// H.264 file, 15 frames, identical 1605x60 output:
    ///     old, full-range sweep        12590 ms
    ///     new, accurate seek            2846 ms   (4.4x)
    ///     new, keyframe seek             799 ms   (15.8x)
    /// The user's real source is heavier than the probe on every axis, so the saving there is
    /// larger still.
    ///
    /// ⚠️ THE LEGACY SWEEP IS STILL HERE AS A FALLBACK AND MUST STAY. N seeked inputs is the fast
    /// path, not the safe one: a seek landing past the real end of file (a container whose declared
    /// duration lies, a caller passing a range longer than the media) yields an input with no
    /// frames, and `hstack` cannot configure without one frame per input. Rather than reason about
    /// every such case, a failed fast path silently re-runs the old command, which tolerates all of
    /// them. Do not delete the fallback to "simplify".
    /// ══════════════════════════════════════════════════════════════════════════════════════════
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
        try
        {
            Directory.CreateDirectory(tempDirectory);
        }
        catch (System.Exception ex) { Debug.WriteLine(ex.ToString()); return null; }

        if (durationSec <= 0) durationSec = 10;
        if (startSec < 0) startSec = 0;
        frames = Math.Max(1, frames);

        string tempPng = Path.Combine(tempDirectory, $"fvs_thumb_{Guid.NewGuid():N}.png");

        bool seekedFirst = (durationSec / frames) >= SeekedPathMinSpacingSec;

        string[] primary = seekedFirst
            ? BuildSeekedArgs(videoPath, tempPng, startSec, durationSec, frames)
            : BuildSweepArgs(videoPath, tempPng, startSec, durationSec, frames);
        string[] secondary = seekedFirst
            ? BuildSweepArgs(videoPath, tempPng, startSec, durationSec, frames)
            : BuildSeekedArgs(videoPath, tempPng, startSec, durationSec, frames);

        string primaryLabel = seekedFirst ? "seeked" : "sweep";
        string secondaryLabel = seekedFirst ? "sweep" : "seeked";

        if (await RunAsync(ffmpegPath, primary, tempPng, cancellationToken, logTag, primaryLabel).ConfigureAwait(true))
        {
            return tempPng;
        }

        RuntimeLog.Debug(logTag, $"Filmstrip {primaryLabel} path produced nothing — retrying on the {secondaryLabel} path.");
        TryDelete(tempPng);

        if (await RunAsync(ffmpegPath, secondary, tempPng, cancellationToken, logTag, secondaryLabel).ConfigureAwait(true))
        {
            return tempPng;
        }

        TryDelete(tempPng);
        return null;
    }

    /// <summary>STRIP_01 — N independently KEYFRAME-seeked inputs of the same file, one frame each,
    /// stacked. Only ever chosen when the spacing between samples exceeds a GOP; see
    /// <see cref="SeekedPathMinSpacingSec"/>.</summary>
    private static string[] BuildSeekedArgs(
        string videoPath, string outPng, double startSec, double durationSec, int frames)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var args = new System.Collections.Generic.List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error"
        };

        double span = durationSec / frames;
        for (int i = 0; i < frames; i++)
        {
            // Sample the MIDDLE of the slice this thumbnail stands for, not its leading edge: the
            // first frame of a range is frequently a cut or a fade and makes a poor index picture.
            double t = startSec + ((i + 0.5) * span);
            if (t < 0) t = 0;

            args.Add("-noaccurate_seek");
            args.Add("-ss");
            args.Add(t.ToString("0.###", ci));
            args.Add("-i");
            args.Add(videoPath);
        }

        var filter = new System.Text.StringBuilder();
        for (int i = 0; i < frames; i++)
        {
            if (i > 0) filter.Append(';');
            filter.Append('[').Append(i).Append(":v]trim=end_frame=1,setpts=PTS-STARTPTS,scale=-1:")
                  .Append(StripHeightPx).Append(":flags=bilinear[s").Append(i).Append(']');
        }

        if (frames > 1)
        {
            filter.Append(';');
            for (int i = 0; i < frames; i++) filter.Append("[s").Append(i).Append(']');
            filter.Append("hstack=inputs=").Append(frames).Append("[strip]");
        }
        else
        {
            // hstack with a single input is not worth relying on; alias the lone frame instead.
            filter.Append(";[s0]null[strip]");
        }

        args.Add("-filter_complex");
        args.Add(filter.ToString());
        args.Add("-map");
        args.Add("[strip]");
        args.Add("-frames:v");
        args.Add("1");
        args.Add("-an");
        args.Add("-sn");
        args.Add("-dn");
        args.Add(outPng);

        return args.ToArray();
    }

    /// <summary>STRIP_01 — the original full-range sweep, kept as the tolerant fallback.</summary>
    private static string[] BuildSweepArgs(
        string videoPath, string outPng, double startSec, double durationSec, int frames)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        double fps = frames / durationSec;
        string filter = $"fps=fps={fps.ToString("0.000", ci)}:round=up,scale=-1:{StripHeightPx}," +
                        $"tpad=stop_mode=clone:stop_duration=1,tile={frames}x1:margin=0:padding=0";

        return new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-ss", startSec.ToString("0.###", ci),
            "-t", durationSec.ToString("0.###", ci),
            "-i", videoPath,
            "-vf", filter,
            "-frames:v", "1",
            "-an", "-sn", "-dn",
            outPng
        };
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (System.Exception ex) { Debug.WriteLine(ex.ToString()); }
    }

    private static async Task<bool> RunAsync(
        string ffmpegPath, string[] args, string outPng,
        CancellationToken cancellationToken, string logTag, string pathLabel)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            process = Process.Start(psi);
            if (process == null) return false;

            try { ChildProcessTracker.AddProcess(process); } catch (System.Exception ex) { Debug.WriteLine(ex.ToString()); }

            Task<string> stdOut = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stdErr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(true);
            _ = await stdOut.ConfigureAwait(true);

            string errText = string.Empty;
            try { errText = await stdErr.ConfigureAwait(true); } catch (System.Exception ex) { Debug.WriteLine(ex.ToString()); }

            if (process.ExitCode == 0 && File.Exists(outPng)) return true;

            RuntimeLog.Fail(logTag, $"Filmstrip render failed on the {pathLabel} path (exit {process.ExitCode}).");
            if (!string.IsNullOrWhiteSpace(errText))
                RuntimeLog.Debug(logTag, $"FFmpeg stderr:\n{errText.Trim()}");
            return false;
        }
        catch (OperationCanceledException)
        {
            try { if (process != null && !process.HasExited) process.Kill(entireProcessTree: true); }
            catch (System.Exception ex) { Debug.WriteLine(ex.ToString()); }
            TryDelete(outPng);
            throw;
        }
        catch (System.Exception ex)
        {
            Debug.WriteLine(ex.ToString());
            return false;
        }
        finally
        {
            try { process?.Dispose(); } catch (System.Exception ex) { Debug.WriteLine(ex.ToString()); }
        }
    }

    /// <summary>
    /// STRIP_03 — the frame width every streamed thumbnail is forced to, in pixels.
    ///
    /// <see cref="StripHeightPx"/> x 16/9 = 106.67 -> 107. Forcing it rather than using
    /// `scale=-1:60` is deliberate: it makes the frame size known BEFORE the first byte arrives,
    /// which is what lets the reader below treat the pipe as fixed-size records instead of hunting
    /// for image boundaries. It also makes an assumption the lane was ALREADY making honest —
    /// `GranularSpeedEditorWindow.RelayoutFrameLane` computes each frame's source span as
    /// `stripPxH * 16/9 / stripPxW`, i.e. it has always assumed 16:9 frames. On a non-16:9 source
    /// the old `-1` width made that computation subtly wrong; now it is exact by construction.
    /// </summary>
    public const int FrameWidthPx = 107;

    /// <summary>
    /// LEAK_01 — hard ceiling on one streamed strip. Nothing else ever cancels a prewarm that is
    /// not superseded, so an ffmpeg that opens a file and then produces nothing (a network path
    /// that stalls, a container it cannot walk) would leave a process and a pending read alive for
    /// the life of the app. A watchdog expiry is NOT treated as user cancellation: the process is
    /// killed and the call returns false so the caller falls back, exactly as it would on any
    /// other failure.
    /// </summary>
    private const int StreamWatchdogSeconds = 30;

    /// <summary>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// STRIP_03 — PAINT THE LANE WHILE IT IS STILL BEING DECODED.
    ///
    /// <see cref="GenerateAsync"/> is all-or-nothing: it renders a tiled PNG and the lane stays
    /// empty until the last frame is done. Even at STRIP_01 speed that is most of a second of
    /// blank grey, and blank-then-complete is what reads as slow — not the total.
    ///
    /// This streams instead. ONE ffmpeg process decodes ONLY keyframes (`-skip_frame nokey`) and
    /// writes uncompressed BGRA straight to stdout; each frame is blitted into a
    /// <see cref="WriteableBitmap"/> the caller is handed as soon as the FIRST one lands, and the
    /// caller repaints on every frame after that. MEASURED, 60s 1920x1080 60fps, 15 frames:
    ///     first frame painted   221 ms
    ///     strip complete        533 ms
    ///     arrivals (ms)         221 221 307 307 307 368 368 453 453 453 512 512 533 533 533
    /// against 852 ms of nothing at all before this change.
    ///
    /// ⚠️ RAW BGRA, NOT MJPEG — AND THIS IS THE LOAD-BEARING DECISION.
    ///   * The frame is a FIXED 107x60x4 = 25,680 bytes, so framing the pipe is arithmetic. An
    ///     MJPEG pipe has to be scanned for SOI/EOI markers, which is guesswork on a byte stream.
    ///   * No decode step, so no second image codec in the hot path.
    ///   * `-pix_fmt bgra` is byte-for-byte `PixelFormat.Bgra8888`. Piping MJPEG would mean
    ///     decoding through Avalonia, whose output channel order is platform-dependent — a
    ///     red/blue swap that would only show up on someone else's machine.
    /// The cost is bandwidth: 15 x 25 KB = 385 KB down a pipe. That is nothing.
    ///
    /// ⚠️ KEYFRAME SNAPPING IS ACCEPTED HERE UNCONDITIONALLY (owner's decision) — the lane is a
    /// visual index, and this is what Jellyfin, Premiere and every scrub-thumbnail implementation
    /// does. If a caller ever needs frame-exact stills, it must use <see cref="GenerateAsync"/>,
    /// which still picks its strategy by sample spacing.
    ///
    /// Returns false if not one frame arrived, so the caller can fall back to
    /// <see cref="GenerateAsync"/>. Never throws except on cancellation.
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    /// <param name="onReady">Invoked ON THE UI THREAD with the strip bitmap when the first frame
    /// has landed. This is the moment to swap the loading overlay for the lane.</param>
    /// <param name="onFrame">Invoked ON THE UI THREAD after every frame, including the first.
    /// Repaint here.</param>
    public static async Task<bool> StreamAsync(
        string ffmpegPath,
        string videoPath,
        double startSec,
        double durationSec,
        CancellationToken cancellationToken,
        Action<WriteableBitmap> onReady,
        Action? onFrame = null,
        int frames = DefaultFrames,
        string logTag = "Filmstrip")
    {
        if (durationSec <= 0) durationSec = 10;
        if (startSec < 0) startSec = 0;
        frames = Math.Max(1, frames);

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        double fps = frames / durationSec;

        var argList = new System.Collections.Generic.List<string>
        {
            "-hide_banner", "-loglevel", "error",
            "-skip_frame", "nokey",
            "-ss", startSec.ToString("0.###", ci),
            "-t", durationSec.ToString("0.###", ci),
            "-i", videoPath,
            "-vf", $"fps=fps={fps.ToString("0.0000", ci)}:round=up," +
                   $"scale={FrameWidthPx}:{StripHeightPx}:flags=bilinear," +
                   $"tpad=stop_mode=clone:stop_duration=1",
            "-frames:v", frames.ToString(ci),
            "-an", "-sn", "-dn",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-"
        };

        int frameRow = FrameWidthPx * 4;
        int frameBytes = frameRow * StripHeightPx;
        int stripW = FrameWidthPx * frames;
        int stripRow = stripW * 4;

        // The whole strip is kept as one managed buffer and re-uploaded after each frame. 15 x
        // ~385 KB of memcpy is far cheaper than reasoning about partial-rect uploads, and it means
        // the untouched tail is deterministic zeros (black) rather than whatever the graphics
        // allocator handed back.
        byte[] strip = new byte[stripRow * StripHeightPx];
        byte[] frameBuf = new byte[frameBytes];

        WriteableBitmap? bitmap = null;
        int landed = 0;
        Process? process = null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string a in argList) psi.ArgumentList.Add(a);

            process = Process.Start(psi);
            if (process == null) return false;

            try { ChildProcessTracker.AddProcess(process); } catch (System.Exception ex) { Debug.WriteLine(ex.ToString()); }

            using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            watchdog.CancelAfter(TimeSpan.FromSeconds(StreamWatchdogSeconds));
            CancellationToken ct = watchdog.Token;

            Task<string> errTask = process.StandardError.ReadToEndAsync(ct);
            var pipe = process.StandardOutput.BaseStream;

            int have = 0;
            while (landed < frames)
            {
                int read = await pipe.ReadAsync(frameBuf.AsMemory(have, frameBytes - have), ct)
                                     .ConfigureAwait(false);
                if (read <= 0) break;

                have += read;
                if (have < frameBytes) continue;
                have = 0;

                byte[] pixels = (byte[])frameBuf.Clone();
                int index = landed;
                landed++;

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // LEAK_01 — THIS GUARD IS LOAD-BEARING, DO NOT REMOVE IT.
                    // Cancellation and the caller's teardown both happen on the UI thread, and so
                    // does this callback, so a post queued before a window closed runs AFTER that
                    // window disposed the very bitmap being written here. Without the check this
                    // calls Lock() on a disposed WriteableBitmap — a native handle, so the failure
                    // is an access violation rather than a catchable exception. Re-checking the
                    // token here is what makes the ordering safe; the try/catch is the second net.
                    if (ct.IsCancellationRequested) return;

                    for (int y = 0; y < StripHeightPx; y++)
                    {
                        Buffer.BlockCopy(pixels, y * frameRow,
                                         strip, (y * stripRow) + (index * frameRow),
                                         frameRow);
                    }

                    bitmap ??= new WriteableBitmap(
                        new PixelSize(stripW, StripHeightPx),
                        new Vector(96, 96),
                        PixelFormat.Bgra8888,
                        AlphaFormat.Opaque);

                    using (var fb = bitmap.Lock())
                    {
                        // Row by row: the locked framebuffer's stride is not required to equal
                        // width * 4, and a flat copy silently shears the image where it does not.
                        for (int y = 0; y < StripHeightPx; y++)
                        {
                            System.Runtime.InteropServices.Marshal.Copy(
                                strip, y * stripRow,
                                IntPtr.Add(fb.Address, y * fb.RowBytes),
                                stripRow);
                        }
                    }

                    try
                    {
                        if (index == 0) onReady(bitmap);
                        onFrame?.Invoke();
                    }
                    catch (System.Exception ex) { Debug.WriteLine(ex.ToString()); }
                });
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            try
            {
                string err = await errTask.ConfigureAwait(false);
                if (landed == 0 && !string.IsNullOrWhiteSpace(err))
                    RuntimeLog.Debug(logTag, $"Filmstrip stream produced no frames. FFmpeg stderr:\n{err.Trim()}");
            }
            catch (System.Exception ex) { Debug.WriteLine(ex.ToString()); }

            return landed > 0;
        }
        catch (OperationCanceledException)
        {
            try { if (process != null && !process.HasExited) process.Kill(entireProcessTree: true); }
            catch (System.Exception ex) { Debug.WriteLine(ex.ToString()); }

            // LEAK_01 — only a REAL cancellation propagates. A watchdog expiry is a failure, and
            // callers must be able to fall back rather than have it surface as "the user closed
            // the window".
            if (cancellationToken.IsCancellationRequested) throw;

            RuntimeLog.Fail(logTag, $"Filmstrip stream exceeded {StreamWatchdogSeconds}s and was killed.");
            return landed > 0;
        }
        catch (System.Exception ex)
        {
            Debug.WriteLine(ex.ToString());
            RuntimeLog.Fail(logTag, $"Filmstrip stream failed: {ex.Message}");
            return landed > 0;
        }
        finally
        {
            try { process?.Dispose(); } catch (System.Exception ex) { Debug.WriteLine(ex.ToString()); }
        }
    }

}
