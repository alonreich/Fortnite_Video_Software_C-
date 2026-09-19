// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Globalization;
using System.IO;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// RESCUE_01 — moves a finished render out of a failing pipeline and into the temp root under a
/// name that is RESERVED, not merely observed to be free.
///
/// WHAT WAS WRONG. <c>ProcessWorker.TryRescueFinishedRender</c> and
/// <c>MergerWorker.TryRescueFinishedRender</c> were two byte-identical copies of this shape:
/// <code>
///     while (File.Exists(rescued)) { rescued = $"...-{n}.mp4"; n++; }
///     File.Move(corePath, rescued);
/// </code>
/// That is precisely the defect <c>ProcessWorker.ResolveOutputPath</c> documents under OUTPATH_01
/// and was rewritten to remove — "a File.Exists scan: check, then return the name, then write to
/// it much later. Two pipelines running at once ... both saw the same index free". The rescue
/// path never received that fix, and the consequence here is STRICTLY WORSE than it was for
/// ResolveOutputPath:
///
///   1. <c>ResolveOutputPath</c> feeds a <c>File.Move(..., overwrite: true)</c>. The rescue called
///      the TWO-argument <c>File.Move(string, string)</c>, which THROWS <see cref="IOException"/>
///      when the destination exists. The throw was caught, null was returned, and the finished
///      render — the ONLY copy, which is the entire reason it is being rescued — was abandoned on
///      a path the failing pipeline was about to clean up. A completed export, lost silently, with
///      one log line as the only trace.
///   2. The name is stamped to ONE SECOND (<c>yyyyMMdd-HHmmss</c>) and the temp root is shared by
///      every process. Cancel-then-restart — the scenario OUTPATH_01 was written for — and Main
///      App + Merger running together both put two rescues inside the same second.
///   3. The <c>n</c> loop had NO CEILING, unlike ResolveOutputPath's. On a directory where
///      File.Exists keeps returning true (permissions, a full disk, an offline share) it spun
///      forever INSIDE A FAILURE HANDLER.
///
/// ⚠️ ONE COPY, ON PURPOSE. The two workers previously held two copies of this method and two
/// copies of <c>ReadExitCodeSafely</c>; the latter pair silently DIVERGED (one was hardened to
/// use GracefulProcessTerminator, the other was left calling Kill raw). Anything that both
/// pipelines do identically belongs here, once, so a fix cannot miss a caller.
///
/// ⚠️ THE PREFIX AND LOG TAG ARE PARAMETERS, NOT CONSTANTS. "Fortnite-Video-RECOVERED-" and
/// "Merged-Videos-RECOVERED-" are user-visible and tell the user which tool produced the file;
/// "Output" and "Merger" are how crash digests attribute the failure. Both must stay distinct.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
internal static class RescuedOutputPath
{
    /// <summary>
    /// RESCUE_01 — the same ceiling <c>ResolveOutputPath</c> uses, for the same reason: a
    /// directory that cannot be written to must fail loudly after a bounded number of attempts
    /// instead of spinning forever inside the export's failure handling.
    /// </summary>
    private const int MaxIndex = 10000;

    /// <summary>
    /// Moves <paramref name="sourcePath"/> into <paramref name="tempDirectory"/> under a reserved
    /// name and returns that name, or null when there was nothing to rescue or the rescue failed.
    ///
    /// TOTAL BY CONTRACT: this is invoked from inside failure handling, so it never throws. An
    /// exception escaping here would turn a partial failure into a total one.
    /// </summary>
    /// <param name="sourcePath">The finished render to preserve. Missing file =&gt; null.</param>
    /// <param name="tempDirectory">Destination root. Created if absent — never presumed to exist.</param>
    /// <param name="filenamePrefix">User-visible prefix, e.g. "Fortnite-Video-RECOVERED-".</param>
    /// <param name="logTag">CoreLogger tag, e.g. "Output" or "Merger".</param>
    /// <param name="failureMessage">
    /// The caller's own wording for the failure line ("Could not preserve the finished render" /
    /// "... the finished merge"). Kept as a parameter so both messages survive verbatim.
    /// </param>
    internal static string? TryRescue(
        string sourcePath,
        string tempDirectory,
        string filenamePrefix,
        string logTag,
        string failureMessage)
    {
        try
        {
            // Nothing to rescue. First statement, unchanged from both original implementations.
            if (!File.Exists(sourcePath)) return null;

            Directory.CreateDirectory(tempDirectory);

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

            for (int idx = 0; idx <= MaxIndex; idx++)
            {
                // idx 0 keeps the historical un-suffixed name; 1..n keep the historical "-{n}"
                // suffix format, so existing user folders and support notes still match.
                string candidate = Path.Combine(
                    tempDirectory,
                    idx == 0 ? $"{filenamePrefix}{stamp}.mp4"
                             : $"{filenamePrefix}{stamp}-{idx}.mp4");

                try
                {
                    // OUTPATH_01 primitive: FileMode.CreateNew + FileShare.None is an ATOMIC
                    // create-or-fail at the filesystem level. Exactly one caller can win a given
                    // name, in this process or any other.
                    using (new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                    }
                }
                catch (IOException)
                {
                    // Taken by an existing file, or lost the race to a sibling rescue. Next index.
                    continue;
                }

                // ⚠️ OVERWRITE THE ZERO-BYTE PLACEHOLDER, never delete-then-move: deleting it
                // reopens the very race the reservation just closed.
                File.Move(sourcePath, candidate, overwrite: true);
                return candidate;
            }

            CoreLogger.Fail(logTag,
                $"{failureMessage}: could not reserve a filename in '{tempDirectory}' after {MaxIndex} attempts. " +
                "The folder may be full, read-only, or unavailable.");
            return null;
        }
        catch (Exception ex)
        {
            CoreLogger.Fail(logTag, $"{failureMessage}: {ex.Message}");
            return null;
        }
    }
}
