// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// MAGICWAND_02 — AUTOMATIC HUD DETECTION. THE C# PORT OF THE OLD PYTHON TOOL'S magic_wand.py.
///
/// WHAT THE BUTTON USED TO DO, AND WHY IT WAS HIDDEN.
/// <c>CropToolWindow.ShowMagicWandCandidates</c> drew six rectangles at hardcoded fractions of the
/// frame — 0.65, 0.02, 0.32, 0.28 and five more like it — and presented them as detections. On any
/// capture whose HUD did not happen to sit at those fractions it was confidently, silently wrong,
/// which is worse than absent, so MAGICWAND_01 hid the button rather than delete a handler someone
/// might one day back with a real analyser. This is that analyser.
///
/// WHAT IT ACTUALLY DOES, in the order it does it:
///   1. Samples frames uniformly across the WHOLE clip (not the single frozen frame — see below).
///   2. Takes the per-pixel TEMPORAL MEDIAN. Gameplay moves; the HUD does not. The median of sixty
///      frames spread over a match is a picture of the parts of the screen that never change.
///   3. Takes the per-pixel TEMPORAL STANDARD DEVIATION and inverts it into a stability mask, so
///      "this pixel never changed" becomes a first-class, scoreable signal.
///   4. Builds three more masks off the median: an adaptive threshold (dark HUD plates on bright
///      gameplay), Canny edges (panel borders), and an HSV colour-anchor mask tuned to the four
///      colour families a Fortnite HUD actually uses — health green, shield blue, loot amber,
///      rarity purple/pink.
///   5. For each of the five known HUD roles, looks ONLY inside that role's zone of the screen,
///      finds connected blobs, filters them by size and aspect, and scores the survivors on eight
///      weighted terms. Non-maximum suppression keeps the best one or two per role.
///   6. Falls back to a generic detector if fewer than three roles were found, and to a Hough
///      circle hunt if the minimap specifically was missed.
///
/// WHY IT ANALYSES THE CLIP AND NOT THE SNAPSHOT. The snapshot is one frame. One frame cannot tell
/// a HUD panel from a wall, because both are just pixels; it is the fact that the panel is STILL
/// THERE sixty frames later that identifies it. The old Python tool took the snapshot path as an
/// argument too and then ignored it for exactly this reason (<c>extract_all(snapshot_path, ...)</c>
/// reads <c>params["input_file"]</c>), and so does this. The snapshot's DIMENSIONS are what matter,
/// and those come in as <c>sourceWidth</c>/<c>sourceHeight</c>.
///
/// WHY THERE IS NO OpenCV. Invariant 1 in docs/README.md is the Single Binary Executable Mandate.
/// The primitives OpenCV supplied — adaptive threshold, morphology, contours, Canny, Hough — are
/// implemented in <see cref="HudImageOps"/>, which documents each one against the call it replaces
/// and every place the two differ.
///
/// COORDINATE SPACES. There are two, and mixing them is the classic way to break this file:
///   • ANALYSIS space: every mask, blob and pixel index, 540 rows tall by construction.
///   • SOURCE space: what the caller gets back, in the snapshot's own pixels.
/// <see cref="ScaledToSourceRect"/> is the ONLY crossing point. Nothing else may convert.
///
/// THREADING. Pure computation with no UI types; call it from a background task. It honours the
/// cancellation token between every stage and inside the frame reader.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class HudAutoDetector
{
    /// <summary>
    /// One thing the detector found: a rectangle in the caller's SOURCE pixel space, plus the HUD
    /// role it was found by.
    ///
    /// <see cref="RoleKey"/> is one of <see cref="HudConfig.HudKeys"/>, or null when the rectangle
    /// came from the generic or circle fallback and the detector genuinely does not know what it
    /// is looking at. The Python original threw this away — <c>extract_all</c> returned bare
    /// QRects and the UI made the user label every one. Keeping it costs nothing and lets the Crop
    /// Tool pre-select the right role when the user clicks a box, so a confident detection becomes
    /// one click instead of two.
    /// </summary>
    public readonly record struct DetectionRect(int X, int Y, int Width, int Height, string? RoleKey);

    /// <summary>
    /// WANDPROGRESS_01 — one progress beat, for a caller that wants to show its user what is
    /// happening.
    ///
    /// WHY THIS EXISTS. Detection takes anywhere from three seconds to the full
    /// <see cref="MaxSeconds"/> ceiling, and for every one of those seconds the only thing the
    /// screen said was that a button had gone grey. A user cannot tell a long job from a hung one
    /// without being told, so they sit and wonder whether the feature is working or broken — and
    /// the honest answer is that both look identical from outside.
    ///
    /// <paramref name="Stage"/> is written for the person watching, not for the log: it says what
    /// the machine is looking at in words that mean something to someone who has never heard of a
    /// temporal median. <paramref name="Percent"/> is 0-100 and monotonic — it never goes
    /// backwards, because a bar that retreats reads as a fault.
    /// </summary>
    public readonly record struct DetectionProgress(int Percent, string Stage);

    /// <summary>Analysis height, and therefore the whole pipeline's working resolution. 540 is the
    /// Python original's figure and it is load-bearing: every pixel threshold, kernel size and
    /// size range in <see cref="RoleSpecs"/> is calibrated against it. Changing it silently
    /// re-tunes every one of them.</summary>
    private const int AnalysisHeight = 540;

    /// <summary>Hard ceiling on the whole run. The old tool had the same idea
    /// (<c>UI_BEHAVIOR.MAGIC_WAND_MAX_SECONDS</c>): a wand that never comes back is a hang, and the
    /// user is staring at a disabled button while it thinks.</summary>
    public const int MaxSeconds = 60;

    /// <summary>Above this clip length the sampler switches to keyframes only. See
    /// <see cref="SampleFramesAsync"/> for why.</summary>
    private const double KeyframeOnlyThresholdSeconds = 150.0;

    /// <summary>Upper bound on frames pulled in keyframe mode before subsampling. A two-hour
    /// capture has thousands; we only ever want sixty, and each one costs ~1.5 MB of RAM.</summary>
    private const int MaxKeyframesRead = 400;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // ROLE SPECIFICATIONS
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Everything the detector knows about where one HUD role lives and what it looks like. Ported
    /// value-for-value from <c>magic_wand.py :: _get_role_specs</c> — these numbers are the result
    /// of the original tool's tuning against real captures and are NOT to be "tidied".
    /// </summary>
    /// <param name="RoleKey">The HudConfig key. The Python keyed these by DISPLAY name
    /// ("Mini Map + Stats"); this build keys by the stable config key and lets the Crop Tool supply
    /// the display name, so a renamed label can never break detection.</param>
    /// <param name="Zone">Search window as fractions of the frame: x1, y1, x2, y2.</param>
    /// <param name="MinW">Minimum acceptable width in SOURCE pixels.</param>
    /// <param name="MaxW">Maximum acceptable width in SOURCE pixels.</param>
    /// <param name="MinH">Minimum acceptable height in SOURCE pixels.</param>
    /// <param name="MaxH">Maximum acceptable height in SOURCE pixels.</param>
    /// <param name="MinAspect">Minimum width/height.</param>
    /// <param name="MaxAspect">Maximum width/height.</param>
    /// <param name="IdealAspect">The shape this role usually is; deviation costs score.</param>
    /// <param name="IdealCx">Where this role usually sits, as a fraction of frame width.</param>
    /// <param name="IdealCy">Same, as a fraction of frame height.</param>
    /// <param name="Pad">Fixed padding added around a blob, in analysis pixels.</param>
    /// <param name="MaxKeep">How many candidates NMS may keep for this role.</param>
    /// <param name="ExpandL">Extra left padding as a fraction of the blob's own width.</param>
    /// <param name="ExpandT">Extra top padding as a fraction of the blob's own height.</param>
    /// <param name="ExpandR">Extra right padding as a fraction of the blob's own width.</param>
    /// <param name="ExpandB">Extra bottom padding as a fraction of the blob's own height.</param>
    /// <param name="MinAnchorRatio">Reject below this much HUD-coloured pixel coverage.</param>
    /// <param name="MinStabilityRatio">Reject below this much never-changed coverage.</param>
    /// <param name="MinPosScore">Reject below this much agreement with IdealCx/IdealCy.</param>
    /// <param name="SecondaryMinRatio">A second candidate is kept only if it scores at least this
    /// fraction of the winner. Used for the roles that legitimately appear twice.</param>
    private sealed record RoleSpec(
        string RoleKey,
        double ZoneX1, double ZoneY1, double ZoneX2, double ZoneY2,
        int MinW, int MaxW, int MinH, int MaxH,
        double MinAspect, double MaxAspect, double IdealAspect,
        double IdealCx, double IdealCy,
        int Pad, int MaxKeep,
        double ExpandL, double ExpandT, double ExpandR, double ExpandB,
        double MinAnchorRatio, double MinStabilityRatio, double MinPosScore,
        double SecondaryMinRatio);

    /// <summary>
    /// The five roles, in the order the Python's <c>role_order</c> listed them. Order matters twice:
    /// it is the order primary picks are appended in, and it is the order the caller sees, so the
    /// first box the user is offered is the minimap rather than whatever happened to sort first.
    /// </summary>
    private static readonly RoleSpec[] RoleSpecs =
    [
        // "Mini Map + Stats"
        new("stats",
            0.60, 0.00, 1.00, 0.42,
            180, 760, 90, 560,
            0.60, 3.40, 1.85,
            0.84, 0.18,
            2, 2,
            0.02, 0.02, 0.02, 0.08,
            0.03, 0.04, 0.50,
            0.94),

        // "Own Health Bar (HP)"
        new("normal_hp",
            0.00, 0.58, 0.52, 1.00,
            120, 960, 20, 300,
            1.80, 22.00, 5.60,
            0.18, 0.86,
            2, 2,
            0.01, 0.01, 0.01, 0.01,
            0.008, 0.12, 0.38,
            0.90),

        // "Loot Area"
        new("loot",
            0.46, 0.56, 1.00, 1.00,
            160, 1200, 30, 430,
            1.60, 28.00, 4.80,
            0.83, 0.88,
            2, 2,
            0.01, 0.01, 0.01, 0.01,
            0.015, 0.10, 0.36,
            0.92),

        // "Teammates health Bars (HP)" — the one role that legitimately repeats, hence MaxKeep 3.
        new("team",
            0.00, 0.00, 0.35, 0.45,
            80, 500, 40, 400,
            0.40, 4.00, 1.20,
            0.12, 0.18,
            2, 3,
            0.01, 0.01, 0.01, 0.01,
            0.005, 0.10, 0.30,
            0.88),

        // "Spectating Eye"
        new("spectating",
            0.35, 0.00, 0.65, 0.30,
            40, 200, 30, 150,
            0.80, 2.50, 1.40,
            0.50, 0.12,
            2, 1,
            0.02, 0.02, 0.02, 0.02,
            0.005, 0.05, 0.40,
            0.88),

    ];

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // ENTRY POINT
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs the whole pipeline and returns what it found, best-first within each role and roles in
    /// <see cref="RoleSpecs"/> order.
    ///
    /// Returns an EMPTY list rather than throwing when the clip cannot be sampled or nothing scores
    /// well enough — "found nothing" is a normal outcome the caller must handle anyway, and turning
    /// it into an exception would make the ordinary case look like a failure in the logs.
    /// </summary>
    /// <param name="ffmpegPath">Resolved ffmpeg executable.</param>
    /// <param name="videoPath">The clip to analyse.</param>
    /// <param name="sourceWidth">Snapshot width. Results come back in this space.</param>
    /// <param name="sourceHeight">Snapshot height. Results come back in this space.</param>
    /// <param name="totalMs">Clip length, if known; 0 to have it probed.</param>
    /// <param name="progress">WANDPROGRESS_01 — optional; receives a beat at every stage boundary
    /// and once per sampled frame, so a caller can drive a real progress bar rather than a
    /// spinner. Reports are posted on whatever thread the pipeline is running on, so a UI caller
    /// must marshal (Progress&lt;T&gt; does this automatically when constructed on the UI thread).</param>
    public static async Task<IReadOnlyList<DetectionRect>> DetectAsync(
        string ffmpegPath,
        string videoPath,
        int sourceWidth,
        int sourceHeight,
        double totalMs,
        CancellationToken cancellationToken,
        IProgress<DetectionProgress>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            CoreLogger.Info("HudAutoDetector", "No usable video path; detection skipped.");
            return Array.Empty<DetectionRect>();
        }

        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            CoreLogger.Info("HudAutoDetector", "Source dimensions unknown; detection skipped.");
            return Array.Empty<DetectionRect>();
        }

        // The Python fell back to 30s when the duration was unavailable, purely so the frame-count
        // ladder below had something to answer. Same conservative default here.
        if (totalMs <= 0) totalMs = 30000;

        // Analysis geometry. Width is derived from the SOURCE aspect and forced even, exactly as
        // the Python did (`if self.scale_w % 2 != 0: self.scale_w += 1`) — odd widths break several
        // pixel-format conversions inside ffmpeg's scaler.
        int analysisWidth = (int)Math.Round(AnalysisHeight * (sourceWidth / (double)Math.Max(1, sourceHeight)));
        if (analysisWidth % 2 != 0) analysisWidth++;
        if (analysisWidth < 2) analysisWidth = 2;

        // Frame-count ladder, straight from extract_all. Short clips get PROPORTIONALLY more
        // samples, because a 3-second clip has less temporal variety to average over.
        int targetFrames = totalMs <= 3500 ? 45 : totalMs <= 15000 ? 50 : 60;
        int minRequired = totalMs < 5000 ? 8 : 16;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(MaxSeconds));
        CancellationToken ct = timeout.Token;

        progress?.Report(new DetectionProgress(2, "Opening the clip\u2026"));

        List<byte[]> frames = await SampleFramesAsync(
            ffmpegPath, videoPath, analysisWidth, AnalysisHeight, targetFrames, totalMs / 1000.0, ct, progress)
            .ConfigureAwait(false);

        if (frames.Count < minRequired)
        {
            CoreLogger.Info("HudAutoDetector",
                $"Only {frames.Count} frame(s) sampled, {minRequired} required. Detection abandoned.");
            return Array.Empty<DetectionRect>();
        }

        CoreLogger.Info("HudAutoDetector",
            $"Analysing {frames.Count} frames at {analysisWidth}x{AnalysisHeight} " +
            $"(source {sourceWidth}x{sourceHeight}).");

        ct.ThrowIfCancellationRequested();

        return Analyse(frames, analysisWidth, AnalysisHeight, sourceWidth, sourceHeight, ct, progress);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // WANDPROGRESS_01 — THE PERCENTAGE BUDGET.
    //
    // The bands below are shares of the WALL CLOCK, not of the code. Frame sampling is by far the
    // longest stage (it decodes the clip), so it owns nearly half the bar on its own; the per-role
    // scoring is six near-identical passes, so it gets a band it can subdivide evenly. They are
    // deliberately not evenly spaced: a bar whose segments each take a wildly different length of
    // real time is a bar that lies, and a bar that lies is worse than no bar, because the user
    // calibrates their patience against it.
    // ══════════════════════════════════════════════════════════════════════════════════════════
    private const int PctSamplingStart = 5;
    private const int PctSamplingEnd = 48;
    private const int PctMedian = 58;
    private const int PctStability = 66;
    private const int PctMasks = 74;
    private const int PctRolesStart = 76;
    private const int PctRolesEnd = 94;
    private const int PctFallbacks = 97;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // STAGE 1 — FRAME SAMPLING
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Pulls <paramref name="targetFrames"/> frames spread evenly across the WHOLE clip, already
    /// scaled to analysis size, as raw interleaved BGR.
    ///
    /// TWO MODES, AND THE REASON THERE ARE TWO.
    /// The Python seeked with <c>CAP_PROP_POS_FRAMES</c> sixty times, which OpenCV implements as
    /// sixty seek-and-decode-forward operations. That is fine at OpenCV's level of abstraction and
    /// intolerable here: sixty ffmpeg processes, or one full decode of a long capture, and the user
    /// is watching a disabled button the whole time.
    ///
    ///   • SHORT AND MEDIUM CLIPS (under <see cref="KeyframeOnlyThresholdSeconds"/>): one ffmpeg,
    ///     full decode, <c>fps=</c> filter resampling the stream down to exactly the frames wanted.
    ///     Uniform by construction, and a full decode of a two-minute clip is a few seconds.
    ///   • LONG CLIPS: one ffmpeg with <c>-skip_frame nokey</c>, which decodes ONLY keyframes and
    ///     is therefore roughly constant-cost per minute of footage no matter how long the file is.
    ///     Keyframes land every 1-4 seconds in any normal game capture, so they already span the
    ///     whole file; we read up to <see cref="MaxKeyframesRead"/> of them and then subsample
    ///     uniformly in managed code. Deliberately NOT combined with <c>-frames:v</c>, which would
    ///     take the first N keyframes and give us a detailed picture of the first two minutes and
    ///     nothing at all about the rest.
    ///
    /// Both modes end up at "frames spread evenly over the clip", which is the only property the
    /// median and the stability map actually depend on.
    ///
    /// A short read at the end of the stream is normal (ffmpeg closes the pipe mid-frame when the
    /// stream ends); the partial frame is discarded rather than analysed as garbage.
    /// </summary>
    private static async Task<List<byte[]>> SampleFramesAsync(
        string ffmpegPath,
        string videoPath,
        int width,
        int height,
        int targetFrames,
        double durationSec,
        CancellationToken cancellationToken,
        IProgress<DetectionProgress>? progress = null)
    {
        var frames = new List<byte[]>(targetFrames);
        int frameBytes = width * height * 3;
        bool keyframeMode = durationSec > KeyframeOnlyThresholdSeconds;
        int readLimit = keyframeMode ? MaxKeyframesRead : targetFrames;

        var ci = CultureInfo.InvariantCulture;
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };

        if (keyframeMode)
        {
            args.Add("-skip_frame");
            args.Add("nokey");
        }

        args.Add("-i");
        args.Add(videoPath);

        if (keyframeMode)
        {
            // No fps filter: every keyframe the decoder emits is wanted, and -vsync 0 stops ffmpeg
            // from duplicating or dropping to hit a constant output rate.
            args.Add("-vsync");
            args.Add("0");
            args.Add("-vf");
            args.Add($"scale={width.ToString(ci)}:{height.ToString(ci)}:flags=bilinear");
        }
        else
        {
            double fps = targetFrames / Math.Max(0.001, durationSec);
            args.Add("-vf");
            args.Add($"fps=fps={fps.ToString("0.0000", ci)}:round=up," +
                     $"scale={width.ToString(ci)}:{height.ToString(ci)}:flags=bilinear");
            args.Add("-frames:v");
            args.Add(targetFrames.ToString(ci));
        }

        args.Add("-an");
        args.Add("-sn");
        args.Add("-dn");
        args.Add("-f");
        args.Add("rawvideo");
        args.Add("-pix_fmt");
        args.Add("bgr24");
        args.Add("-");

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
            foreach (string a in args) psi.ArgumentList.Add(a);

            CoreLogger.Debug("HudAutoDetector",
                $"Sampler ({(keyframeMode ? "keyframe" : "uniform")}): {psi.FileName} {ProcessArgs.FormatForLog(args)}");

            process = Process.Start(psi);
            if (process == null) return frames;

            try { ChildProcessTracker.AddProcess(process); } catch (Exception ex) { CoreLogger.Swallowed(ex); }

            // Drain stderr on its own task. ffmpeg writes enough on some inputs to fill the pipe
            // buffer, and a blocked writer is a child that never exits.
            Task<string> errTask = process.StandardError.ReadToEndAsync();

            Stream pipe = process.StandardOutput.BaseStream;
            var buffer = new byte[frameBytes];

            while (frames.Count < readLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int have = 0;
                while (have < frameBytes)
                {
                    int read = await pipe.ReadAsync(buffer.AsMemory(have, frameBytes - have), cancellationToken)
                        .ConfigureAwait(false);
                    if (read <= 0) break;
                    have += read;
                }

                if (have < frameBytes) break;   // EOF, or a torn final frame. Either way, stop.

                var frame = new byte[frameBytes];
                Buffer.BlockCopy(buffer, 0, frame, 0, frameBytes);
                frames.Add(frame);

                // WANDPROGRESS_01 — the only stage that can report continuously, and the longest
                // one, so it is what stops the bar from looking stuck. In keyframe mode readLimit is
                // the over-read ceiling rather than the target, so the fraction is against whichever
                // is actually being counted up to.
                if (progress != null && readLimit > 0)
                {
                    int pct = PctSamplingStart + (int)((PctSamplingEnd - PctSamplingStart) * (frames.Count / (double)readLimit));
                    progress.Report(new DetectionProgress(
                        Math.Min(PctSamplingEnd, pct),
                        $"Reading the clip\u2026 frame {frames.Count}"));
                }
            }

            // The reader may have stopped early (readLimit hit); closing our end makes ffmpeg see a
            // broken pipe and exit rather than block forever writing frames nobody wants.
            try { process.StandardOutput.Close(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                string err = await errTask.ConfigureAwait(false);
                if (process.ExitCode != 0 && frames.Count == 0 && !string.IsNullOrWhiteSpace(err))
                {
                    CoreLogger.Info("HudAutoDetector", $"Sampler exited {process.ExitCode}: {err.Trim()}");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { CoreLogger.Swallowed(ex); }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            CoreLogger.Info("HudAutoDetector", $"Frame sampling failed: {ex.Message}");
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try
                {
                    await GracefulProcessTerminator.TerminateAsync(process, "HudAutoDetector", attemptQuitCommand: false)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) { CoreLogger.Swallowed(ex); }
            }
            process?.Dispose();
        }

        // Keyframe mode over-reads on purpose; thin the result down to the requested count, evenly.
        if (frames.Count > targetFrames)
        {
            var picked = new List<byte[]>(targetFrames);
            for (int i = 0; i < targetFrames; i++)
            {
                int idx = (int)Math.Round(i * (frames.Count - 1) / (double)Math.Max(1, targetFrames - 1));
                picked.Add(frames[Math.Clamp(idx, 0, frames.Count - 1)]);
            }
            frames = picked;
        }

        return frames;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // STAGE 2 — THE ANALYSIS ITSELF
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static IReadOnlyList<DetectionRect> Analyse(
        List<byte[]> frames,
        int width, int height,
        int sourceWidth, int sourceHeight,
        CancellationToken ct,
        IProgress<DetectionProgress>? progress = null)
    {
        // ── The temporal median. This is the single most important line in the file: it is what
        //    turns "a video of a match" into "a picture of the parts of the screen that never
        //    move". Everything downstream is measured against it.
        progress?.Report(new DetectionProgress(PctSamplingEnd, "Working out what never moves\u2026"));
        byte[] median = TemporalMedian(frames, width, height);
        ct.ThrowIfCancellationRequested();

        // ── The stability mask, before the frames are released. Bright = never changed.
        progress?.Report(new DetectionProgress(PctMedian, "Measuring what stayed still\u2026"));
        byte[] stability = TemporalStabilityMask(frames, width, height);
        ct.ThrowIfCancellationRequested();

        // ~1.5 MB per frame times sixty; there is no reason to hold that while the (much smaller)
        // masks are being scored.
        frames.Clear();

        progress?.Report(new DetectionProgress(PctStability, "Looking for panels and edges\u2026"));
        byte[] gray = HudImageOps.BgrToGray(median, width, height);

        // Dark structure on bright gameplay: HUD plates are dark, semi-transparent panels.
        byte[] baseMask = HudImageOps.MorphClose(
            HudImageOps.AdaptiveThresholdGaussianInv(gray, width, height, blockSize: 21, c: 5),
            width, height, 5, 5);
        ct.ThrowIfCancellationRequested();

        byte[] hsv = HudImageOps.BgrToHsv(median, width, height);
        byte[] anchorMask = HudImageOps.MorphClose(
            HudImageOps.MorphOpen(BuildColourAnchorMask(hsv, width, height), width, height, 3, 3),
            width, height, 5, 5);
        ct.ThrowIfCancellationRequested();

        byte[] edgeMask = HudImageOps.Canny(gray, width, height, 60, 150);
        ct.ThrowIfCancellationRequested();
        progress?.Report(new DetectionProgress(PctMasks, "Looking for HUD colours\u2026"));

        var ctx = new AnalysisContext(width, height, sourceWidth, sourceHeight,
            baseMask, anchorMask, edgeMask, stability);

        // ── Per-role detection.
        var perRole = new Dictionary<string, List<(double Score, DetectionRect Rect)>>(StringComparer.Ordinal);
        for (int i = 0; i < RoleSpecs.Length; i++)
        {
            RoleSpec spec = RoleSpecs[i];
            ct.ThrowIfCancellationRequested();

            // WANDPROGRESS_01 — the role band is split evenly because the six passes really do cost
            // about the same: same masks, same zone-sized blob search, same eight-term scorer.
            // The friendly name is the one the user will see on the box if this role is found.
            progress?.Report(new DetectionProgress(
                PctRolesStart + (PctRolesEnd - PctRolesStart) * i / RoleSpecs.Length,
                $"Checking for the {FriendlyRoleName(spec.RoleKey)}\u2026"));

            var found = ExtractRoleCandidates(ctx, spec);
            perRole[spec.RoleKey] = found;
            CoreLogger.Debug("HudAutoDetector", $"Role '{spec.RoleKey}': {found.Count} candidate(s).");
        }
        progress?.Report(new DetectionProgress(PctRolesEnd, "Sorting out what it found\u2026"));

        var selected = new List<DetectionRect>();

        // Winners first, in role order, so the first box the user meets is the minimap.
        foreach (RoleSpec spec in RoleSpecs)
        {
            var list = perRole[spec.RoleKey];
            if (list.Count > 0) selected.Add(list[0].Rect);
        }

        // Then the legitimate seconds — a squad has more than one teammate bar, and some layouts
        // split the loot row. Only when the runner-up is genuinely comparable in both score and
        // size, or every busy frame would produce a second box for every role.
        foreach (RoleSpec spec in RoleSpecs)
        {
            var list = perRole[spec.RoleKey];
            if (list.Count < 2) continue;

            (double bestScore, DetectionRect best) = list[0];
            (double secondScore, DetectionRect second) = list[1];

            double bestArea = Math.Max(1, best.Width * (long)best.Height);
            double secondArea = Math.Max(1, second.Width * (long)second.Height);
            double areaRatio = secondArea / bestArea;

            if (secondScore >= bestScore * spec.SecondaryMinRatio && areaRatio is >= 0.45 and <= 1.85)
            {
                selected.Add(second);
            }
        }

        int primaryFound = RoleSpecs.Count(s => perRole[s.RoleKey].Count > 0);

        // ── Generic fallback: only when the role detectors clearly did not understand this HUD.
        //    Running it alongside a good role pass just adds noise the user has to dismiss.
        if (primaryFound < 3)
        {
            progress?.Report(new DetectionProgress(PctFallbacks, "Taking a second look\u2026"));
            var generic = ExtractGenericCandidates(ctx);
            CoreLogger.Debug("HudAutoDetector", $"Generic fallback: {generic.Count} candidate(s).");

            foreach ((double score, DetectionRect rect) in generic)
            {
                if (selected.Count >= 6) break;
                if (score < 26.0) continue;
                if (selected.Any(s => Iou(rect, s) >= 0.35)) continue;

                double cxn = (rect.X + rect.Width / 2.0) / Math.Max(1, sourceWidth);
                double cyn = (rect.Y + rect.Height / 2.0) / Math.Max(1, sourceHeight);

                // The middle-right band is where gameplay lives, not HUD. A candidate there has to
                // clear a higher bar before it is worth showing.
                if (cyn is > 0.32 and < 0.72 && cxn is > 0.36 and < 0.94 && score < 32.0) continue;

                selected.Add(rect);
            }
        }
        else
        {
            CoreLogger.Debug("HudAutoDetector", "Role detections complete; generic fallback skipped.");
        }

        // ── Circle hunt: the minimap is round in a lot of layouts, and a round thing on a busy
        //    background is exactly the case the rectangular blob finder is worst at.
        if (perRole["stats"].Count == 0)
        {
            progress?.Report(new DetectionProgress(PctFallbacks, "Hunting for a round minimap\u2026"));
            foreach (DetectionRect circle in HuntForCircles(ctx, gray).Take(2))
            {
                if (selected.Any(s => Iou(circle, s) >= 0.40)) continue;
                selected.Add(circle);
            }
        }

        if (selected.Count == 0)
        {
            CoreLogger.Info("HudAutoDetector", "No candidates survived scoring.");
            return Array.Empty<DetectionRect>();
        }

        List<DetectionRect> merged = MergeOverlapping(selected);
        List<DetectionRect> final = DedupeByIou(merged, 0.45);

        progress?.Report(new DetectionProgress(100, $"Found {final.Count} HUD element(s)."));
        CoreLogger.Info("HudAutoDetector", $"Detection finished: {final.Count} HUD element(s).");
        return final;
    }

    /// <summary>
    /// WANDPROGRESS_01 — a role key in words the user recognises from the screen.
    ///
    /// Deliberately NOT the display names in CropToolWindow.Roles: this assembly has no reference
    /// to the App project, and duplicating that table here would create two sources of truth for
    /// something a user reads. These are short, plain descriptions of the THING, which is what a
    /// progress line wants anyway ("Checking for the health bar" beats "Checking for
    /// Own Health Bar (HP)"). An unknown key falls through to itself rather than to "unknown", so a
    /// new role added to RoleSpecs still produces a sensible line before anyone updates this.
    /// </summary>
    private static string FriendlyRoleName(string roleKey) => roleKey switch
    {
        "stats" => "mini map",
        "normal_hp" => "health bar",
        "loot" => "loot area",
        "team" => "teammate bars",
        "spectating" => "spectator counter",
        _ => roleKey.Replace('_', ' ')
    };

    /// <summary>Everything the scorers need, bundled so the signatures stay readable.</summary>
    private sealed record AnalysisContext(
        int Width, int Height,
        int SourceWidth, int SourceHeight,
        byte[] BaseMask, byte[] AnchorMask, byte[] EdgeMask, byte[] StabilityMask);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // TEMPORAL STATISTICS
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Per-pixel, per-channel median across every sampled frame — <c>np.median(frames, axis=0)</c>.
    ///
    /// The median and not the mean, and this is not a detail: a mean smears a bright muzzle flash
    /// across every pixel it touched, while a median ignores it entirely unless it was present in
    /// more than half the frames. HUD elements are present in all of them.
    ///
    /// Quickselect rather than a sort. With sixty samples per channel and roughly 1.5 million
    /// channels at analysis resolution, an insertion sort is ~1.4 billion comparisons and a
    /// noticeable stall; quickselect is linear in the sample count and finishes in well under a
    /// second. The median-of-an-even-count convention is the LOWER of the two middle values, which
    /// is what OpenCV/NumPy's integer path effectively yields after the cast back to uint8.
    /// </summary>
    private static byte[] TemporalMedian(List<byte[]> frames, int width, int height)
    {
        int n = frames.Count;
        int len = width * height * 3;
        var median = new byte[len];
        var samples = new byte[n];

        for (int i = 0; i < len; i++)
        {
            for (int f = 0; f < n; f++) samples[f] = frames[f][i];
            median[i] = QuickSelect(samples, n, (n - 1) / 2);
        }

        return median;
    }

    /// <summary>Hoare selection over a scratch buffer. Destroys <paramref name="data"/>, which is
    /// fine — the caller refills it for every pixel.</summary>
    private static byte QuickSelect(byte[] data, int count, int k)
    {
        int lo = 0, hi = count - 1;
        while (lo < hi)
        {
            byte pivot = data[lo + ((hi - lo) >> 1)];
            int i = lo, j = hi;
            while (i <= j)
            {
                while (data[i] < pivot) i++;
                while (data[j] > pivot) j--;
                if (i <= j)
                {
                    (data[i], data[j]) = (data[j], data[i]);
                    i++; j--;
                }
            }
            if (k <= j) hi = j;
            else if (k >= i) lo = i;
            else return data[k];
        }
        return data[lo];
    }

    /// <summary>
    /// The stability mask — <c>_compute_temporal_stability_mask</c>. Bright means "this pixel did
    /// not change over the clip", which is the closest thing to a definition of "HUD" that exists.
    ///
    /// Per-pixel standard deviation over time (population, ddof=0, matching NumPy's default),
    /// averaged across the three channels, min-max normalised, inverted, and thresholded hard at
    /// 165. The open-then-close pass afterwards removes single-pixel speckle and then seals the
    /// gaps inside what survived, so a panel reads as one region rather than a constellation.
    ///
    /// Under three frames there is nothing meaningful to measure and the Python returned an
    /// all-pass mask rather than a misleading one; so does this.
    /// </summary>
    private static byte[] TemporalStabilityMask(List<byte[]> frames, int width, int height)
    {
        int pixels = width * height;

        if (frames.Count < 3)
        {
            var allPass = new byte[pixels];
            Array.Fill(allPass, (byte)255);
            return allPass;
        }

        int n = frames.Count;
        var stdMap = new double[pixels];

        for (int p = 0; p < pixels; p++)
        {
            int baseIdx = p * 3;
            double channelStdSum = 0;

            for (int c = 0; c < 3; c++)
            {
                int idx = baseIdx + c;
                double sum = 0, sumSq = 0;
                for (int f = 0; f < n; f++)
                {
                    double v = frames[f][idx];
                    sum += v;
                    sumSq += v * v;
                }
                double mean = sum / n;
                double variance = (sumSq / n) - (mean * mean);
                channelStdSum += Math.Sqrt(variance > 0 ? variance : 0);
            }

            stdMap[p] = channelStdSum / 3.0;
        }

        byte[] normalised = HudImageOps.NormalizeMinMaxToByte(stdMap);

        var stability = new byte[pixels];
        for (int i = 0; i < pixels; i++) stability[i] = (byte)(255 - normalised[i]);

        byte[] thresholded = HudImageOps.ThresholdBinary(stability, 165);
        byte[] opened = HudImageOps.MorphOpen(thresholded, width, height, 3, 3);
        return HudImageOps.MorphClose(opened, width, height, 7, 7);
    }

    /// <summary>
    /// <c>get_hud_color_anchors</c> — the four colour families a Fortnite HUD is actually built
    /// from, in OpenCV's 8-bit HSV (hue 0..179).
    ///
    /// These four ranges are the single strongest signal the detector has, which is why the role
    /// scorer weights their coverage at 44 out of roughly 130. Gameplay is muddy; a health bar is
    /// a saturated green rectangle and nothing else on screen looks like one.
    /// </summary>
    private static byte[] BuildColourAnchorMask(byte[] hsv, int width, int height)
    {
        var mask = new byte[width * height];

        for (int p = 0, i = 0; i < mask.Length; i++, p += 3)
        {
            int h = hsv[p], s = hsv[p + 1], v = hsv[p + 2];

            bool hp     = h is >= 35  and <= 95  && s >= 80  && v >= 80;    // health green
            bool shield = h is >= 100 and <= 140 && s >= 80  && v >= 80;    // shield blue
            bool loot   = h is >= 15  and <= 40  && s >= 100 && v >= 100;   // loot amber/gold
            bool rarity = h is >= 120 and <= 175 && s >= 50  && v >= 50;    // rarity purple/pink

            mask[i] = (hp || shield || loot || rarity) ? (byte)255 : (byte)0;
        }

        return mask;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // SCORING
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>_extract_role_candidates</c> — find and rank the candidates for one role.
    ///
    /// The role's zone is cut out of the three masks first, so a blob is only ever found where that
    /// role could legitimately be. Inside the zone the search mask is
    /// <c>(base OR close(anchor)) AND dilate(stability)</c>, then closed: read that as "dark
    /// structure or HUD-coloured pixels, but only where nothing moved".
    ///
    /// Eight weighted terms decide the ranking; the weights are the Python's and are not arbitrary:
    /// colour anchoring (44) dominates, then position agreement and aspect plausibility (16 each),
    /// then edges (20), stability (16), fill (9) and size (6). Minimap candidates get a bonus for
    /// having a busy top band, which is what the stats row above the map looks like.
    /// </summary>
    private static List<(double Score, DetectionRect Rect)> ExtractRoleCandidates(AnalysisContext ctx, RoleSpec spec)
    {
        int sx1 = (int)(ctx.Width * spec.ZoneX1);
        int sy1 = (int)(ctx.Height * spec.ZoneY1);
        int sx2 = (int)(ctx.Width * spec.ZoneX2);
        int sy2 = (int)(ctx.Height * spec.ZoneY2);

        if (sx2 <= sx1 || sy2 <= sy1) return [];

        int zw = sx2 - sx1;
        int zh = sy2 - sy1;

        byte[] subBase = CropMask(ctx.BaseMask, ctx.Width, sx1, sy1, zw, zh);
        byte[] subAnchor = CropMask(ctx.AnchorMask, ctx.Width, sx1, sy1, zw, zh);
        byte[] subStability = CropMask(ctx.StabilityMask, ctx.Width, sx1, sy1, zw, zh);

        byte[] roleMask = HudImageOps.Or(subBase, HudImageOps.MorphClose(subAnchor, zw, zh, 3, 3));
        roleMask = HudImageOps.And(roleMask, HudImageOps.Dilate(subStability, zw, zh, 5, 5));
        roleMask = HudImageOps.MorphClose(roleMask, zw, zh, 5, 5);

        var scored = new List<(double Score, DetectionRect Rect)>();

        foreach (HudImageOps.Blob blob in HudImageOps.FindBlobs(roleMask, zw, zh))
        {
            int x = blob.X + sx1;
            int y = blob.Y + sy1;
            int w = blob.Width;
            int h = blob.Height;

            if (w < 3 || h < 3) continue;

            int expandL = (int)Math.Round(w * spec.ExpandL);
            int expandT = (int)Math.Round(h * spec.ExpandT);
            int expandR = (int)Math.Round(w * spec.ExpandR);
            int expandB = (int)Math.Round(h * spec.ExpandB);

            int x0 = Math.Max(0, x - spec.Pad - expandL);
            int y0 = Math.Max(0, y - spec.Pad - expandT);
            int x1 = Math.Min(ctx.Width, x + w + spec.Pad + expandR);
            int y1 = Math.Min(ctx.Height, y + h + spec.Pad + expandB);

            int xs = x0, ys = y0;
            int ws = Math.Max(1, x1 - x0);
            int hs = Math.Max(1, y1 - y0);

            DetectionRect rect = ScaledToSourceRect(ctx, xs, ys, ws, hs, spec.RoleKey);
            int rw = rect.Width, rh = rect.Height;

            if (rw < spec.MinW || rw > spec.MaxW) continue;
            if (rh < spec.MinH || rh > spec.MaxH) continue;

            double aspect = rw / (double)Math.Max(1, rh);
            if (aspect < spec.MinAspect || aspect > spec.MaxAspect) continue;

            double anchorRatio = Ratio(ctx.AnchorMask, ctx, xs, ys, ws, hs);
            double edgeRatio = Ratio(ctx.EdgeMask, ctx, xs, ys, ws, hs);
            double stableRatio = Ratio(ctx.StabilityMask, ctx, xs, ys, ws, hs);

            if (anchorRatio < spec.MinAnchorRatio) continue;
            if (stableRatio < spec.MinStabilityRatio) continue;

            double fillRatio = blob.Area / (double)Math.Max(1, w * h);

            double idealCx = ctx.SourceWidth * spec.IdealCx;
            double idealCy = ctx.SourceHeight * spec.IdealCy;
            double cx = rect.X + rw / 2.0;
            double cy = rect.Y + rh / 2.0;
            double dx = (cx - idealCx) / ctx.SourceWidth;
            double dy = (cy - idealCy) / ctx.SourceHeight;
            double posScore = Math.Max(0.0, 1.0 - Math.Sqrt(dx * dx + dy * dy) * 1.8);

            if (posScore < spec.MinPosScore) continue;

            double aspectDev = Math.Abs(Math.Log(Math.Max(1e-6, aspect / Math.Max(1e-6, spec.IdealAspect))));
            double aspectScore = Math.Max(0.0, 1.0 - aspectDev / Math.Log(2.0));
            double areaNorm = Math.Min(1.0, (rw * (double)rh) / (spec.MaxW * (double)spec.MaxH));

            // The minimap's tell: a dense strip of text and icons across the top of the block.
            double topBandBonus = 0.0;
            if (spec.RoleKey == "stats")
            {
                int bandH = Math.Max(4, (int)(hs * 0.22));
                topBandBonus = (Ratio(ctx.EdgeMask, ctx, xs, ys, ws, bandH) * 7.0)
                             + (Ratio(ctx.AnchorMask, ctx, xs, ys, ws, bandH) * 6.0);
            }

            double score =
                (anchorRatio * 44.0) +
                (edgeRatio * 20.0) +
                (stableRatio * 16.0) +
                (fillRatio * 9.0) +
                (posScore * 16.0) +
                (aspectScore * 16.0) +
                (areaNorm * 6.0) +
                topBandBonus;

            scored.Add((score, rect));
        }

        return NonMaxSuppress(scored, iouThreshold: 0.40, maxKeep: spec.MaxKeep);
    }

    /// <summary>
    /// <c>_extract_generic_candidates</c> — the "I do not recognise this HUD" path.
    ///
    /// Searches the union of every role's zone and applies shape, size and position heuristics
    /// instead of role knowledge: corners and edges of the screen are preferred, the centre is
    /// penalised (that is where the game is), and the middle-right band has to clear an extra bar
    /// because that is where gameplay most often produces a stable-looking rectangle that is not a
    /// HUD element at all.
    ///
    /// Returns candidates with no role key: the detector genuinely does not know what these are,
    /// and guessing a label the user then has to correct is worse than asking.
    /// </summary>
    private static List<(double Score, DetectionRect Rect)> ExtractGenericCandidates(AnalysisContext ctx)
    {
        byte[] zoneMask = BuildHudZoneMask(ctx);
        byte[] searchMask = HudImageOps.And(ctx.BaseMask, zoneMask);

        var scored = new List<(double Score, DetectionRect Rect)>();

        foreach (HudImageOps.Blob blob in HudImageOps.FindBlobs(searchMask, ctx.Width, ctx.Height))
        {
            if (blob.Width < 4 || blob.Height < 4) continue;

            int xs = Math.Max(0, blob.X - 4);
            int ys = Math.Max(0, blob.Y - 4);
            int xe = Math.Min(ctx.Width, blob.X + blob.Width + 4);
            int ye = Math.Min(ctx.Height, blob.Y + blob.Height + 4);
            int ws = Math.Max(1, xe - xs);
            int hs = Math.Max(1, ye - ys);

            DetectionRect rect = ScaledToSourceRect(ctx, xs, ys, ws, hs, roleKey: null);
            int rw = rect.Width, rh = rect.Height;

            if (rw < 20 || rh < 16) continue;
            if (rw > 620 || rh > 420) continue;

            long area = rw * (long)rh;
            if (area < 1000 || area > 240000) continue;

            double anchorRatio = Ratio(ctx.AnchorMask, ctx, xs, ys, ws, hs);
            double edgeRatio = Ratio(ctx.EdgeMask, ctx, xs, ys, ws, hs);
            double stableRatio = Ratio(ctx.StabilityMask, ctx, xs, ys, ws, hs);
            double fillRatio = blob.Area / (double)Math.Max(1, blob.Width * blob.Height);
            double areaScore = Math.Min(1.0, area / 70000.0);

            double cxn = (rect.X + rw / 2.0) / Math.Max(1, ctx.SourceWidth);
            double cyn = (rect.Y + rh / 2.0) / Math.Max(1, ctx.SourceHeight);

            if (stableRatio < 0.44) continue;
            if (anchorRatio < 0.04 && edgeRatio < 0.03) continue;

            bool inMidRightBand = cyn is > 0.32 and < 0.72 && cxn is > 0.36 and < 0.94;
            if (inMidRightBand && anchorRatio < 0.18 && stableRatio < 0.68) continue;

            double cornerPref = (cyn > 0.58 || cyn < 0.24 || cxn < 0.20 || cxn > 0.80) ? 1.0 : 0.0;
            double centreDist = Math.Sqrt((cxn - 0.5) * (cxn - 0.5) + (cyn - 0.5) * (cyn - 0.5));
            double centrePenalty = Math.Max(0.0, (0.30 - centreDist) / 0.30);

            double score =
                (anchorRatio * 30.0) +
                (edgeRatio * 16.0) +
                (stableRatio * 13.0) +
                (fillRatio * 7.0) +
                (areaScore * 6.0) +
                (cornerPref * 8.0) -
                (centrePenalty * 10.0);

            scored.Add((score, rect));
        }

        return NonMaxSuppress(scored, iouThreshold: 0.42, maxKeep: 6);
    }

    /// <summary><c>_build_hud_zone_mask</c> — the union of all five role zones, i.e. everywhere a HUD
    /// element is allowed to be at all.</summary>
    private static byte[] BuildHudZoneMask(AnalysisContext ctx)
    {
        var mask = new byte[ctx.Width * ctx.Height];

        foreach (RoleSpec spec in RoleSpecs)
        {
            int x1 = (int)(ctx.Width * spec.ZoneX1);
            int y1 = (int)(ctx.Height * spec.ZoneY1);
            int x2 = (int)(ctx.Width * spec.ZoneX2);
            int y2 = (int)(ctx.Height * spec.ZoneY2);

            if (x2 <= x1 || y2 <= y1) continue;

            for (int y = y1; y < y2 && y < ctx.Height; y++)
            {
                int row = y * ctx.Width;
                for (int x = x1; x < x2 && x < ctx.Width; x++) mask[row + x] = 255;
            }
        }

        return mask;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // CIRCLE HUNT
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>_hunt_for_circles</c> — the minimap-specific last resort, run only when the role detector
    /// found no minimap at all.
    ///
    /// A Hough gradient circle detector, matching <c>cv2.HoughCircles(HOUGH_GRADIENT, dp=1,
    /// minDist=120, param1=50, param2=30, ...)</c> in structure: Canny at (param1/2, param1), then
    /// every edge pixel votes for centres along its own gradient line at each radius in range, then
    /// centres are the local maxima above param2, separated by minDist, and each centre's radius is
    /// the most-supported distance to the edge pixels around it.
    ///
    /// APPROXIMATION, DECLARED: OpenCV refines centres to sub-pixel accuracy and groups radii with
    /// a sorted-distance walk; this votes on an integer accumulator and takes the modal radius
    /// bin. Results can therefore differ by a pixel or two from the Python's. That is acceptable
    /// here and nowhere else in this file: this path produces a SUGGESTION the user then drags into
    /// place, it runs only when everything better has already failed, and the ±10px margin the
    /// original added around every circle it found is an order of magnitude larger than the
    /// disagreement.
    ///
    /// Searched region is the top-right of the frame only, which is where a minimap is.
    /// </summary>
    private static List<DetectionRect> HuntForCircles(AnalysisContext ctx, byte[] gray)
    {
        double scaleX = ctx.SourceWidth / (double)ctx.Width;
        double scaleY = ctx.SourceHeight / (double)ctx.Height;

        int zoneX = (int)(ctx.Width * 0.55);
        int zoneW = ctx.Width - zoneX;
        int zoneH = (int)(ctx.Height * 0.45);

        if (zoneW < 16 || zoneH < 16) return [];

        byte[] zone = CropMask(gray, ctx.Width, zoneX, 0, zoneW, zoneH);
        byte[] blurred = HudImageOps.GaussianBlur(zone, zoneW, zoneH, 9, 2.0);

        int minRadius = Math.Max(4, (int)(30 / scaleX));
        int maxRadius = Math.Max(minRadius + 1, (int)(180 / scaleX));
        maxRadius = Math.Min(maxRadius, Math.Min(zoneW, zoneH));

        byte[] edges = HudImageOps.Canny(blurred, zoneW, zoneH, 25, 50);
        (int[] gx, int[] gy) = HudImageOps.Sobel(blurred, zoneW, zoneH);

        var accumulator = new int[zoneW * zoneH];
        var edgePoints = new List<int>();

        for (int i = 0; i < edges.Length; i++)
        {
            if (edges[i] == 0) continue;
            edgePoints.Add(i);

            double mag = Math.Sqrt((double)gx[i] * gx[i] + (double)gy[i] * gy[i]);
            if (mag < 1e-6) continue;

            double ux = gx[i] / mag;
            double uy = gy[i] / mag;

            int py = i / zoneW;
            int px = i - py * zoneW;

            // Vote in BOTH directions along the gradient: a centre may be on the bright or the
            // dark side of the edge depending on the map's rendering, and OpenCV votes both ways too.
            for (int r = minRadius; r <= maxRadius; r++)
            {
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    int ax = (int)Math.Round(px + sign * ux * r);
                    int ay = (int)Math.Round(py + sign * uy * r);
                    if (ax < 0 || ay < 0 || ax >= zoneW || ay >= zoneH) continue;
                    accumulator[ay * zoneW + ax]++;
                }
            }
        }

        const int CentreThreshold = 30;      // param2
        const double MinCentreDistance = 120.0;

        var centres = new List<(int Votes, int X, int Y)>();
        for (int y = 1; y < zoneH - 1; y++)
        {
            for (int x = 1; x < zoneW - 1; x++)
            {
                int i = y * zoneW + x;
                int v = accumulator[i];
                if (v < CentreThreshold) continue;

                bool isPeak =
                    v >= accumulator[i - 1] && v >= accumulator[i + 1] &&
                    v >= accumulator[i - zoneW] && v >= accumulator[i + zoneW] &&
                    v >= accumulator[i - zoneW - 1] && v >= accumulator[i - zoneW + 1] &&
                    v >= accumulator[i + zoneW - 1] && v >= accumulator[i + zoneW + 1];

                if (isPeak) centres.Add((v, x, y));
            }
        }

        var results = new List<DetectionRect>();
        var kept = new List<(int X, int Y)>();

        foreach ((int votes, int cx, int cy) in centres.OrderByDescending(c => c.Votes))
        {
            if (results.Count >= 4) break;
            if (kept.Any(k => Math.Sqrt((k.X - cx) * (double)(k.X - cx) + (k.Y - cy) * (double)(k.Y - cy)) < MinCentreDistance))
                continue;

            // Modal distance from this centre to the edge pixels around it — the circle's radius.
            var histogram = new int[maxRadius + 1];
            foreach (int p in edgePoints)
            {
                int py = p / zoneW;
                int px = p - py * zoneW;
                double d = Math.Sqrt((px - cx) * (double)(px - cx) + (py - cy) * (double)(py - cy));
                int bin = (int)Math.Round(d);
                if (bin >= minRadius && bin <= maxRadius) histogram[bin]++;
            }

            int bestRadius = 0, bestSupport = 0;
            for (int r = minRadius; r <= maxRadius; r++)
            {
                if (histogram[r] > bestSupport) { bestSupport = histogram[r]; bestRadius = r; }
            }

            if (bestRadius <= 0 || bestSupport < CentreThreshold) continue;

            kept.Add((cx, cy));

            // Back into SOURCE space, with the original's ±10px generosity: the user is going to
            // adjust this box anyway, and a box slightly too large is far easier to pull in than a
            // box that clips the thing it is meant to contain.
            double fx = (cx - bestRadius + zoneX) * scaleX;
            double fy = (cy - bestRadius) * scaleY;
            double fw = 2.0 * bestRadius * scaleX;
            double fh = 2.0 * bestRadius * scaleY;

            results.Add(ClampToSource(ctx,
                (int)fx - 10, (int)fy - 10, (int)fw + 20, (int)fh + 20, "stats"));
        }

        if (results.Count > 0)
        {
            CoreLogger.Debug("HudAutoDetector", $"Circle hunter: {results.Count} candidate(s).");
        }

        return results;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GEOMETRY HELPERS
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE ONLY PLACE ANALYSIS PIXELS BECOME SOURCE PIXELS. <c>_scaled_to_original_rect</c>.
    /// Everything upstream is in the 540-row analysis space; everything the caller sees is in the
    /// snapshot's own space. If a coordinate ever looks wrong by a constant factor, it went around
    /// this method.
    /// </summary>
    private static DetectionRect ScaledToSourceRect(AnalysisContext ctx, int x, int y, int w, int h, string? roleKey)
    {
        double sx = ctx.SourceWidth / (double)ctx.Width;
        double sy = ctx.SourceHeight / (double)ctx.Height;

        return ClampToSource(ctx,
            (int)Math.Round(x * sx),
            (int)Math.Round(y * sy),
            (int)Math.Round(w * sx),
            (int)Math.Round(h * sy),
            roleKey);
    }

    private static DetectionRect ClampToSource(AnalysisContext ctx, int x, int y, int w, int h, string? roleKey)
    {
        int rx = Math.Clamp(x, 0, Math.Max(0, ctx.SourceWidth - 1));
        int ry = Math.Clamp(y, 0, Math.Max(0, ctx.SourceHeight - 1));
        int rw = Math.Clamp(w, 1, Math.Max(1, ctx.SourceWidth - rx));
        int rh = Math.Clamp(h, 1, Math.Max(1, ctx.SourceHeight - ry));
        return new DetectionRect(rx, ry, rw, rh, roleKey);
    }

    /// <summary>Copies a rectangular window out of a full-frame mask. Callers have already clamped.</summary>
    private static byte[] CropMask(byte[] src, int srcWidth, int x, int y, int w, int h)
    {
        var dst = new byte[w * h];
        for (int row = 0; row < h; row++)
        {
            Buffer.BlockCopy(src, (y + row) * srcWidth + x, dst, row * w, w);
        }
        return dst;
    }

    /// <summary>Fraction of a window that is non-zero in the given mask. The denominator is the
    /// CLAMPED window, so a window hanging off the frame still yields a meaningful ratio.</summary>
    private static double Ratio(byte[] mask, AnalysisContext ctx, int x, int y, int w, int h)
    {
        (int nonZero, int total) = HudImageOps.CountNonZero(mask, ctx.Width, ctx.Height, x, y, w, h);
        return total <= 0 ? 0.0 : nonZero / (double)total;
    }

    /// <summary><c>_rect_iou</c> — intersection over union.</summary>
    private static double Iou(DetectionRect a, DetectionRect b)
    {
        int ix = Math.Max(a.X, b.X);
        int iy = Math.Max(a.Y, b.Y);
        int ix2 = Math.Min(a.X + a.Width, b.X + b.Width);
        int iy2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

        long iw = Math.Max(0, ix2 - ix);
        long ih = Math.Max(0, iy2 - iy);
        long inter = iw * ih;
        if (inter <= 0) return 0.0;

        long union = (a.Width * (long)a.Height) + (b.Width * (long)b.Height) - inter;
        return inter / (double)Math.Max(1, union);
    }

    /// <summary><c>_nms_candidates</c> — greedy non-maximum suppression, highest score first.</summary>
    private static List<(double Score, DetectionRect Rect)> NonMaxSuppress(
        List<(double Score, DetectionRect Rect)> scored, double iouThreshold, int maxKeep)
    {
        var kept = new List<(double Score, DetectionRect Rect)>();

        foreach (var candidate in scored.OrderByDescending(t => t.Score))
        {
            if (kept.All(k => Iou(candidate.Rect, k.Rect) < iouThreshold))
            {
                kept.Add(candidate);
                if (kept.Count >= maxKeep) break;
            }
        }

        return kept;
    }

    /// <summary>
    /// <c>_merge_overlapping_rects</c> — unions any two rectangles that come within 25 source pixels
    /// of each other, repeatedly, until nothing more merges.
    ///
    /// This is what turns "the health bar" and "the shield bar just above it" into one box the user
    /// can place as a unit, which is how the HUD is actually laid out and how the exporter expects
    /// to receive it.
    ///
    /// Role keys are preserved on merge: if either input knew what it was, the union inherits it.
    /// When two DIFFERENT roles merge, the FIRST one wins, and since the caller appends in role
    /// order that means the earlier (more important) role keeps the label.
    /// </summary>
    private static List<DetectionRect> MergeOverlapping(List<DetectionRect> rects)
    {
        const int Slack = 25;

        var pending = rects.OrderBy(r => r.X).ToList();
        var merged = new List<DetectionRect>();

        while (pending.Count > 0)
        {
            DetectionRect current = pending[0];
            pending.RemoveAt(0);

            int i = 0;
            while (i < pending.Count)
            {
                DetectionRect other = pending[i];

                bool touches =
                    current.X < other.X + other.Width + Slack &&
                    other.X - Slack < current.X + current.Width &&
                    current.Y < other.Y + other.Height + Slack &&
                    other.Y - Slack < current.Y + current.Height;

                if (touches)
                {
                    int x1 = Math.Min(current.X, other.X);
                    int y1 = Math.Min(current.Y, other.Y);
                    int x2 = Math.Max(current.X + current.Width, other.X + other.Width);
                    int y2 = Math.Max(current.Y + current.Height, other.Y + other.Height);

                    current = new DetectionRect(x1, y1, x2 - x1, y2 - y1, current.RoleKey ?? other.RoleKey);
                    pending.RemoveAt(i);
                    i = 0;   // The union is bigger, so things that did not touch before may now.
                }
                else
                {
                    i++;
                }
            }

            merged.Add(current);
        }

        return merged;
    }

    /// <summary><c>_dedupe_rects_by_iou</c> — keeps the first of any group of near-identical boxes.
    /// Input order is significant and is role order, so the labelled box survives the unlabelled
    /// one.</summary>
    private static List<DetectionRect> DedupeByIou(List<DetectionRect> rects, double iouThreshold)
    {
        var kept = new List<DetectionRect>();
        foreach (DetectionRect r in rects)
        {
            if (kept.All(k => Iou(r, k) < iouThreshold)) kept.Add(r);
        }
        return kept;
    }
}
