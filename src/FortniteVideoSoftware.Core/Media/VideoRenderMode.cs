using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// Singleton holding the result of the GPU capability probe.
/// Set once at startup by AvaloniaApp; read by the view layer and IPC client
/// to determine which rendering path to use (GPU texture sharing vs. software mmap).
/// </summary>
public sealed class VideoRenderMode
{
    private static VideoRenderMode? s_instance;

    /// <summary>
    /// Gets the global instance. Must be initialized via <see cref="Initialize"/>
    /// before first use.
    /// </summary>
    public static VideoRenderMode Current =>
        s_instance ?? throw new InvalidOperationException("VideoRenderMode not initialized. Call Initialize() at startup.");

    /// <summary>
    /// True if hardware-accelerated video rendering should be used.
    /// </summary>
    public bool UseHardwareAcceleration { get; }

    /// <summary>
    /// Human-readable renderer name (for status display).
    /// </summary>
    public string RendererName { get; }

    /// <summary>
    /// If hardware acceleration is disabled, the reason why (for diagnostics).
    /// </summary>
    public string FailureReason { get; }

    private VideoRenderMode(GpuCapabilityProbe.Result result)
    {
        UseHardwareAcceleration = result.UseHardwareAcceleration;
        RendererName = result.RendererName;
        FailureReason = result.FailureReason;
    }

    /// <summary>
    /// Resolves the preview render path and stores the result. Call exactly once at application
    /// startup — `AvaloniaApp.OnFrameworkInitializationCompleted` does this for EVERY process in
    /// the suite, so the Main App, the Video Merger and Crop Tools all select their preview path
    /// through this one method. There is no per-app variant and there must never be one.
    ///
    /// ISSUE 2: this used to run the D3D11/DXGI probe fresh in every process. It now reads the
    /// suite-wide answer published by whichever app started first
    /// (<see cref="HardwareCapability"/>) and only probes when there is nothing valid to reuse.
    ///
    /// ⚠️ THE CACHE IS SESSION-KEYED, AND THAT IS LOAD-BEARING. Windows blocks GPU access inside
    /// an RDP session, so a result recorded in a local session is actively WRONG inside a remote
    /// one. <see cref="HardwareCapability"/> includes the Windows session id and the remote flag
    /// in its key, so crossing that boundary always re-probes. Do not "simplify" that away.
    ///
    /// Fail-safe: any cache problem falls through to a real probe, i.e. the original behaviour.
    /// </summary>
    public static VideoRenderMode Initialize()
    {
        if (s_instance != null)
            return s_instance;

        var shared = HardwareCapability.TryLoadRenderMode();
        if (shared.HasValue)
        {
            var reused = new GpuCapabilityProbe.Result(
                shared.Value.UseHardwareAcceleration,
                shared.Value.RendererName,
                shared.Value.DriverVersion,
                shared.Value.FailureReason);
            CoreLogger.Info("GPU",
                $"GPU probe skipped — reusing the suite-wide result: {(reused.UseHardwareAcceleration ? "hardware" : "software")} ({reused.RendererName}).");
            s_instance = new VideoRenderMode(reused);
            return s_instance;
        }

        var result = GpuCapabilityProbe.Probe();
        HardwareCapability.SaveRenderMode(result);
        s_instance = new VideoRenderMode(result);
        return s_instance;
    }

    /// <summary>
    /// For testing: explicitly set a render mode without probing.
    /// </summary>
    internal static VideoRenderMode InitializeForTesting(bool useHardwareAcceleration)
    {
        s_instance = new VideoRenderMode(new GpuCapabilityProbe.Result(
            useHardwareAcceleration,
            useHardwareAcceleration ? "Test GPU" : "Test CPU",
            "N/A",
            useHardwareAcceleration ? string.Empty : "Test override"));
        return s_instance;
    }

    public override string ToString()
    {
        return UseHardwareAcceleration
            ? $"Hardware ({RendererName})"
            : $"Software ({FailureReason})";
    }
}