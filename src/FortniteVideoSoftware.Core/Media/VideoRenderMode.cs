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
    /// Runs the GPU capability probe and stores the result.
    /// Call exactly once at application startup.
    /// </summary>
    public static VideoRenderMode Initialize()
    {
        if (s_instance != null)
            return s_instance;

        var result = GpuCapabilityProbe.Probe();
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