// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FortniteVideoSoftware.Core.Abstractions;
using FortniteVideoSoftware.Core.Diagnostics;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.App.Services;

/// <summary>
/// SYS-DIAGREPORT — COLLECTS THE DIAGNOSTIC BUNDLE AND PUTS IT SOMEWHERE THE USER CAN FIND IT.
///
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// <b>THE GAP THIS FILLS.</b> <c>FAULTTIER_01</c> classifies every failure and routes it to a log.
/// The Fatal tier even shows the user the log's path. But the path is to a rotating file under
/// <c>%ProgramData%\Fortnite Video Software\logs</c>, and the number of users who will navigate
/// there, identify the right file among the rotation, and attach it to a report is approximately
/// zero. So the fault tiers made failures VISIBLE to the user and left them invisible to whoever
/// has to fix them.
/// </para>
///
/// <para>
/// ⚠️ THE PRODUCT'S CENTRAL RISK IS HARDWARE IT HAS NEVER RUN ON. <c>HardwareScanner</c> chooses
/// between NVENC, AMF, QSV and d3d11va at runtime against a matrix tested on one machine. Without
/// this, a GPU-specific export failure arrives as "it doesn't work on my PC" and stays there.
/// </para>
///
/// <para>
/// <b>NOTHING HERE UPLOADS ANYTHING.</b> It writes a plain-text file and hands back the path. The
/// user reads it, decides, and sends it — or does not. Silent telemetry from a video editor would
/// be an unpleasant surprise in a product whose users are recording their own screens, and a
/// consent dialog nobody understands is not better. A readable file is the honest version.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </para>
/// </summary>
public static class DiagnosticBundle
{
    /// <summary>Where bundles are written. One folder so the user is told one place to look.</summary>
    public static string FolderFor(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(paths.ProgramDataRoot, "Diagnostics");
    }

    /// <summary>
    /// SYS-DIAGREPORT — builds the bundle and writes it.
    ///
    /// <para>
    /// Runs entirely on a worker: it reads log files that can be megabytes and probes the encoder,
    /// and Invariant #6 forbids the UI dispatcher blocking on either. The caller awaits and then
    /// shows the path.
    /// </para>
    /// </summary>
    /// <param name="detectedEncoder">
    /// What <c>HardwareScanner</c> settled on, when it has run. Passed in rather than probed here:
    /// re-probing would report what the hardware can do NOW, and the useful answer is what the
    /// failing session actually chose.
    /// </param>
    /// <returns>The path written, or <see langword="null"/> when nothing could be written.</returns>
    public static async Task<string?> WriteAsync(
        ApplicationPaths paths,
        IFaultSink faults,
        string appVersion,
        string? detectedEncoder,
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(faults);

        string? result = null;

        await faults.GuardAsync("DIAGNOSTICS",
            "The diagnostic report could not be written. Everything else still works — the log file "
          + "itself is still there and can be attached by hand.",
            () => Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var report = new DiagnosticReport()
                    .AddMachineProfile(appVersion, detectedEncoder);

                if (!string.IsNullOrWhiteSpace(context))
                    report.Add("CONTEXT", context!);

                report.AddLogTail(RuntimeLog.LogPath);

                // The recovery state names what the app believed it was doing when it died, which
                // is the one thing a log tail can miss entirely — a hard crash writes no epitaph.
                report.Add("SESSION", DescribeSession(paths));

                string folder = FolderFor(paths);
                Directory.CreateDirectory(folder);

                string file = Path.Combine(folder,
                    $"diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

                File.WriteAllText(file, report.ToString());
                result = file;
            }, cancellationToken),
            FaultTier.Degraded);

        return result;
    }

    private static string DescribeSession(ApplicationPaths paths)
    {
        try
        {
            string recovery = paths.RecoveryStateFile;
            if (!File.Exists(recovery)) return "(no recovery state — the last session closed cleanly)";

            var info = new FileInfo(recovery);
            return $"recovery state    : {info.Length} bytes, last written {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}"
                 + Environment.NewLine
                 + $"safe mode active  : {new RecoveryManager(paths).IsSafeModeActive()}";
        }
        catch (Exception ex)
        {
            // Reported as text inside the bundle rather than as a fault: the bundle IS the error
            // channel here, and a fault raised while building one is how a reporter becomes a
            // source of faults (FAULTTIER_01 on UserFacingFaultSink).
            global::FortniteVideoSoftware.App.RuntimeLog.Swallowed(ex);   // FAULTTIER_02 — no failure is silent.
            return $"(session state unavailable: {ex.GetType().Name} — {ex.Message})";
        }
    }
}
