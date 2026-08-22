using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// SUITE-WIDE HARDWARE CAPABILITY CACHE (issue 2).
///
/// ── THE PROBLEM THIS SOLVES ──────────────────────────────────────────────────────────────────
/// The suite is THREE separate processes (Main App, Video Merger, Crop Tools — Section 2). Each
/// one used to work out the machine's capabilities from scratch:
///   * `VideoRenderMode.Initialize()` ran the D3D11/DXGI GPU probe in EVERY process, and
///   * the Main App ran `HardwareScanner.ScanAsync` (which spawns ffmpeg.exe up to FOUR times:
///     one `-hwaccels` listing plus one real trial encode per vendor) while the Merger ignored it
///     entirely and hardcoded the strategy string "GPU".
/// So the same questions were answered repeatedly, slowly, and — worse — INCONSISTENTLY: the
/// Merger could pick a different encoder than the Main App on the very same machine, and neither
/// could see the other's result.
///
/// ── THE CONTRACT ─────────────────────────────────────────────────────────────────────────────
/// The Main App answers both questions ONCE at boot and publishes the answers here. Every other
/// process in the suite READS them instead of re-probing. There is now exactly ONE code path for
/// "which chip previews video" and exactly ONE for "which chip encodes video", shared by all three
/// applications. Do not reintroduce a per-app probe or a per-app default.
///
/// ── WHY IT IS SAFE TO CACHE, AND WHEN IT IS NOT ──────────────────────────────────────────────
/// The two halves are validated INDEPENDENTLY because they go stale for different reasons:
///
///   ENCODER half (which ffmpeg encoder works) is invalidated by:
///     * a different ffmpeg.exe — validated by a path + byte-length + last-write fingerprint, so
///       an app update or a swapped binary re-probes automatically;
///     * age (<see cref="MaxAgeDays"/>), which covers a GPU driver install;
///     * a recorded <see cref="HardwareScanner.ScanFailed"/> — a failure is NEVER cached, because
///       caching "we could not tell" would freeze a broken state across the whole suite.
///
///   RENDER half (whether the zero-copy preview bridge works) is invalidated by:
///     * a different Windows SESSION — this is the RDP case and it matters. Windows blocks GPU
///       access inside a remote session, so a result recorded in a local session is actively wrong
///       inside an RDP session (and vice-versa). Session id + the remote-session flag are both
///       part of the key, so switching between them always re-probes.
///
/// ── FAIL-SAFE ────────────────────────────────────────────────────────────────────────────────
/// Every read path returns null on ANY problem (missing file, malformed JSON, unreadable ffmpeg,
/// exception) and every write path is silently non-throwing. A cache that cannot be read simply
/// means the caller probes for itself — exactly the old behaviour. This cache can never make the
/// suite worse than not having it.
///
/// ── STORAGE ──────────────────────────────────────────────────────────────────────────────────
/// Goes through <see cref="UiStateStore"/> (ProgramData\uistate), per ISSUE_09: "NEW SMALL STATE
/// FILES BELONG THERE — do not create another root." Serialised with JsonObject rather than
/// reflection-based JsonSerializer because the app is NativeAOT.
/// </summary>
public static class HardwareCapability
{
    private const string FileName = "hardware_capability.json";

    /// <summary>Bump when a field changes meaning. A mismatch discards the record entirely.</summary>
    private const int SchemaVersion = 1;

    /// <summary>
    /// A recorded capability older than this is re-probed. Long enough that the suite is not
    /// re-scanning constantly, short enough that a GPU driver install is picked up on its own.
    /// </summary>
    private const int MaxAgeDays = 7;

    /// <summary>The published answer to "which chip encodes video".</summary>
    /// <param name="EncoderMode">"NVIDIA" | "AMD" | "INTEL" | "CPU". Never <see cref="HardwareScanner.ScanFailed"/>.</param>
    public readonly record struct EncoderCapability(string EncoderMode, DateTime ProbedUtc);

    /// <summary>The published answer to "which path previews video".</summary>
    public readonly record struct RenderCapability(
        bool UseHardwareAcceleration, string RendererName, string DriverVersion, string FailureReason);


    /// <summary>
    /// Returns the suite-wide encoder answer, or null when the caller must probe for itself.
    /// </summary>
    /// <remarks>⚠️ Whole body wrapped — same boot-path reasoning as <see cref="TryLoadRenderMode"/>.</remarks>
    public static EncoderCapability? TryLoadEncoder(string ffmpegPath)
    {
        try
        {
        var root = TryLoadRoot();
        if (root == null) return null;

        string mode = root["encoderMode"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(mode)) return null;

        if (mode == HardwareScanner.ScanFailed) return null;

        string recordedFingerprint = root["ffmpegFingerprint"]?.ToString() ?? "";
        string currentFingerprint = FingerprintFfmpeg(ffmpegPath);
        if (currentFingerprint.Length == 0 || recordedFingerprint != currentFingerprint)
        {
            CoreLogger.Debug("Hardware", "Shared capability: ffmpeg fingerprint changed — re-probing the encoder.");
            return null;
        }

        if (!TryReadUtc(root, "encoderProbedUtc", out DateTime probedUtc)) return null;
        if (DateTime.UtcNow - probedUtc > TimeSpan.FromDays(MaxAgeDays))
        {
            CoreLogger.Debug("Hardware", "Shared capability: encoder result is stale — re-probing.");
            return null;
        }

        return new EncoderCapability(mode, probedUtc);
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("Hardware", $"Shared capability (encoder half) unreadable: {ex.Message} — probing instead.");
            return null;
        }
    }

    /// <summary>
    /// Returns the suite-wide preview-path answer, or null when the caller must probe for itself.
    /// </summary>
    /// <remarks>
    /// ⚠️ WHOLE BODY WRAPPED. This runs on the BOOT PATH via <c>VideoRenderMode.Initialize()</c>,
    /// before any window exists, so an exception here does not surface as a degraded feature — it
    /// kills the app on launch. <c>JsonNode.GetValue&lt;bool&gt;()</c> THROWS when the stored value
    /// is not actually a bool (a hand-edited or half-written file is enough), so the null-coalesce
    /// on its own was not protection. Nothing in this class may throw at a caller.
    /// </remarks>
    public static RenderCapability? TryLoadRenderMode()
    {
        try
        {
            var root = TryLoadRoot();
            if (root == null) return null;

            if (!TryReadBool(root, "renderRecorded", false)) return null;

            string recordedSession = root["sessionKey"]?.ToString() ?? "";
            if (recordedSession.Length == 0 || recordedSession != CurrentSessionKey())
            {
                CoreLogger.Debug("Hardware", "Shared capability: different Windows session (RDP guard) — re-probing the GPU.");
                return null;
            }

            if (!TryReadUtc(root, "renderProbedUtc", out DateTime probedUtc)) return null;
            if (DateTime.UtcNow - probedUtc > TimeSpan.FromDays(MaxAgeDays)) return null;

            return new RenderCapability(
                TryReadBool(root, "useHardwareAcceleration", false),
                root["rendererName"]?.ToString() ?? "Unknown",
                root["driverVersion"]?.ToString() ?? "",
                root["failureReason"]?.ToString() ?? "");
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("Hardware", $"Shared capability (render half) unreadable: {ex.Message} — probing instead.");
            return null;
        }
    }

    /// <summary>
    /// Reads a bool without trusting the stored type. <c>GetValue&lt;bool&gt;()</c> throws on a
    /// type mismatch rather than returning null, so `?? false` does not save you.
    /// </summary>
    private static bool TryReadBool(JsonObject root, string key, bool fallback)
    {
        try
        {
            var node = root[key];
            if (node == null) return fallback;
            if (node.GetValueKind() == System.Text.Json.JsonValueKind.True) return true;
            if (node.GetValueKind() == System.Text.Json.JsonValueKind.False) return false;
            return bool.TryParse(node.ToString(), out bool parsed) ? parsed : fallback;
        }
        catch { return fallback; }
    }


    public static void SaveEncoder(string encoderMode, string ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(encoderMode) || encoderMode == HardwareScanner.ScanFailed) return;

        var root = TryLoadRoot() ?? new JsonObject();
        root["schemaVersion"] = SchemaVersion;
        root["encoderMode"] = encoderMode;
        root["ffmpegFingerprint"] = FingerprintFfmpeg(ffmpegPath);
        root["encoderProbedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        Write(root);
        CoreLogger.Info("Hardware", $"Shared capability published for the whole suite: encoder={encoderMode}.");
    }

    public static void SaveRenderMode(GpuCapabilityProbe.Result result)
    {
        var root = TryLoadRoot() ?? new JsonObject();
        root["schemaVersion"] = SchemaVersion;
        root["renderRecorded"] = true;
        root["useHardwareAcceleration"] = result.UseHardwareAcceleration;
        root["rendererName"] = result.RendererName;
        root["driverVersion"] = result.DriverVersion;
        root["failureReason"] = result.FailureReason;
        root["sessionKey"] = CurrentSessionKey();
        root["renderProbedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        Write(root);
    }


    private static JsonObject? TryLoadRoot()
    {
        try
        {
            string raw = UiStateStore.ReadText(FileName);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (JsonNode.Parse(raw) is not JsonObject root) return null;
            if ((root["schemaVersion"]?.GetValue<int>() ?? 0) != SchemaVersion) return null;
            return root;
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("Hardware", $"Shared capability unreadable ({ex.Message}) — every caller will probe for itself.");
            return null;
        }
    }

    private static void Write(JsonObject root)
    {
        try { UiStateStore.WriteText(FileName, root.ToJsonString()); }
        catch (Exception ex) { CoreLogger.Debug("Hardware", $"Shared capability not written: {ex.Message}"); }
    }

    /// <summary>
    /// Reads a round-trip ("O") timestamp.
    ///
    /// ⚠️ `DateTimeStyles.RoundtripKind` MUST BE USED ALONE. Combining it with
    /// `AdjustToUniversal` (or AssumeLocal/AssumeUniversal) makes DateTime.TryParse throw
    /// ArgumentException on EVERY call, whatever the input — it is an invalid-argument throw, not
    /// a parse failure, so `TryParse` does not swallow it. That combination shipped here briefly
    /// and killed the app at startup, because this runs on the boot path via
    /// VideoRenderMode.Initialize(). RoundtripKind alone is correct anyway: values are written
    /// with `DateTime.UtcNow.ToString("O")`, which ends in "Z", so the parsed value already comes
    /// back with Kind=Utc and needs no adjustment.
    ///
    /// Wrapped defensively as well — nothing on the boot path may throw out of this class.
    /// </summary>
    private static bool TryReadUtc(JsonObject root, string key, out DateTime value)
    {
        value = default;
        try
        {
            string? raw = root[key]?.ToString();
            return !string.IsNullOrWhiteSpace(raw)
                   && DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                                        DateTimeStyles.RoundtripKind, out value);
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("Hardware", $"Shared capability timestamp '{key}' unreadable: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Identifies the exact ffmpeg binary the recorded encoder answer was measured against.
    /// Path + byte length + last-write time is enough to notice an app update or a swapped build,
    /// and is far cheaper than hashing a ~400 KB executable on every process start.
    /// Returns "" when the file cannot be read, which invalidates the cache by design.
    /// </summary>
    private static string FingerprintFfmpeg(string ffmpegPath)
    {
        try
        {
            var info = new FileInfo(ffmpegPath);
            if (!info.Exists) return "";
            return string.Create(CultureInfo.InvariantCulture,
                $"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
        }
        catch { return ""; }
    }

    /// <summary>
    /// Session identity for the RDP guard. `SESSIONNAME` starting with "RDP-" is Windows' own
    /// marker for a remote session and is what the Section 10 RDP guardrail keys off too.
    /// </summary>
    private static string CurrentSessionKey()
    {
        try
        {
            int sessionId = Process.GetCurrentProcess().SessionId;
            string sessionName = Environment.GetEnvironmentVariable("SESSIONNAME") ?? "";
            bool remote = sessionName.StartsWith("RDP-", StringComparison.OrdinalIgnoreCase);
            return string.Create(CultureInfo.InvariantCulture, $"{sessionId}|{(remote ? "remote" : "local")}");
        }
        catch { return ""; }
    }
}
