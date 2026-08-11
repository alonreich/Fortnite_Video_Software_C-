namespace FortniteVideoSoftware.Core.Media;

using FortniteVideoSoftware.Core.Infrastructure;

/// <summary>
/// THE ONE PLACE THAT DECIDES WHICH CHIP ENCODES (issue 2).
///
/// Every application in the suite that exports video routes through this class. Before it existed
/// the Main App and the Video Merger reached the same decision by DIFFERENT routes:
///   * Main App  : Settings override -> boot hardware scan -> EncoderManager
///   * Merger    : hardcoded the literal string "GPU", ignoring both the user's Settings choice
///                 and the boot scan entirely
/// so the two apps could legitimately pick different encoders on the same machine, and the
/// documented "encoder selection chain" was only ever true for one of them.
///
/// RULE FOR ALL AI AGENTS AND HUMANS: an app must NEVER build its own strategy string. If a new
/// export surface is added, it calls <see cref="Resolve"/>. If the precedence needs to change, it
/// changes HERE, once, for everybody.
/// </summary>
public static class ExportEncoderStrategy
{
    /// <summary>
    /// Resolves the strategy string handed to <see cref="EncoderManager"/>.
    ///
    /// PRECEDENCE (highest first):
    ///   1. An explicit Settings ▸ Performance choice ("NVIDIA" / "AMD" / "INTEL" / "CPU").
    ///      The user's word beats any probe, always. If the chosen encoder genuinely is not in the
    ///      bundled FFmpeg, EncoderManager surfaces a clear preflight error rather than quietly
    ///      doing something else.
    ///   2. The suite-wide boot scan result published by the Main App
    ///      (<see cref="HardwareCapability"/>), so the Merger and any future export surface reach
    ///      the SAME answer as the Main App without re-running a four-process ffmpeg probe.
    ///   3. <see cref="HardwareScanner.ScanFailed"/> when nothing is known. This is NOT "CPU" —
    ///      EncoderManager treats it as "unknown, re-probe at export time" and recovers to
    ///      hardware if its own `ffmpeg -encoders` probe finds any. Returning "CPU" here is the
    ///      exact bug that made a machine with a working RTX export everything on libx264.
    /// </summary>
    /// <param name="userOverride">AppSettings.VideoEncoderOverride. "Auto"/empty means "no opinion".</param>
    /// <param name="bootScanResult">
    /// The current process's own scan result, or null if it never ran one (the Merger and Crop
    /// Tools never do). Null falls through to the shared cache.
    /// </param>
    /// <param name="ffmpegPath">Used to validate the shared cache against the actual binary.</param>
    public static string Resolve(string? userOverride, string? bootScanResult, string ffmpegPath)
    {
        if (!string.IsNullOrWhiteSpace(userOverride) &&
            !userOverride.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            CoreLogger.Info("Hardware", $"Encoder override active from Settings: {userOverride}.");
            return userOverride;
        }

        // A live scan result from THIS process always beats the cache — it is newer by definition.
        if (!string.IsNullOrWhiteSpace(bootScanResult) && bootScanResult != HardwareScanner.ScanFailed)
        {
            return bootScanResult;
        }

        var shared = HardwareCapability.TryLoadEncoder(ffmpegPath);
        if (shared.HasValue)
        {
            CoreLogger.Info("Hardware",
                $"Using the suite-wide encoder result published by the Main App: {shared.Value.EncoderMode} (no re-scan needed).");
            return shared.Value.EncoderMode;
        }

        // Nothing known. NOT "CPU" — see the precedence notes above.
        return HardwareScanner.ScanFailed;
    }
}
