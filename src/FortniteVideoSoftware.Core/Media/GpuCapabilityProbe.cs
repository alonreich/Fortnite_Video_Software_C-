using System;
using System.Runtime.InteropServices;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// Smart Startup Probing (The Gatekeeper).
/// Probes GPU/D3D11 capability at app startup to determine whether
/// hardware-accelerated video rendering is viable.
/// Falls back to software-only mode if the GPU driver is broken,
/// missing, or running in a headless/remote session.
/// </summary>
public static class GpuCapabilityProbe
{
    /// <summary>
    /// Result of the GPU capability check.
    /// </summary>
    public sealed record Result(
        bool UseHardwareAcceleration,
        string RendererName,
        string DriverVersion,
        string FailureReason);

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D_FEATURE_LEVEL { }

    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter,
        int DriverType,
        IntPtr Software,
        uint Flags,
        [In] int[]? pFeatureLevels,
        uint FeatureLevels,
        uint SDKVersion,
        out IntPtr ppDevice,
        out int pFeatureLevel,
        out IntPtr ppImmediateContext);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_REMOTESESSION = 0x1000;

    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_SDK_VERSION = 7;
    private static readonly int[] s_featureLevels = { 0xb000, 0xa100, 0xa000, 0x9300, 0x9200, 0x9100 };

    /// <summary>
    /// Probes GPU/D3D11 capability. If D3D11 device creation fails, returns
    /// UseHardwareAcceleration=false so the app falls back to software decode.
    /// </summary>
    public static Result Probe()
    {
        try
        {
            string? forced = Environment.GetEnvironmentVariable("FVS_FORCE_SOFTWARE");
            if (string.Equals(forced, "1", StringComparison.Ordinal))
            {
                CoreLogger.Info("GPU", "Software mode forced by FVS_FORCE_SOFTWARE=1");
                return new Result(false, "N/A", "N/A", "Forced by FVS_FORCE_SOFTWARE environment variable");
            }

            if (GetSystemMetrics(SM_REMOTESESSION) != 0)
            {
                CoreLogger.Info("GPU", "Remote desktop session detected — using software mode");
                return new Result(false, "N/A", "N/A", "Remote desktop session detected");
            }

            int hr = D3D11CreateDevice(
                IntPtr.Zero,
                D3D_DRIVER_TYPE_HARDWARE,
                IntPtr.Zero,
                0,
                s_featureLevels,
                (uint)s_featureLevels.Length,
                D3D11_SDK_VERSION,
                out IntPtr device,
                out int featureLevel,
                out IntPtr context);

            if (hr < 0)
            {
                CoreLogger.Info("GPU", $"D3D11CreateDevice failed with HRESULT 0x{hr:X8} — using software mode");
                return new Result(false, "N/A", "N/A", $"D3D11 device creation failed (HRESULT 0x{hr:X8})");
            }

            if (context != IntPtr.Zero) Marshal.Release(context);
            if (device != IntPtr.Zero) Marshal.Release(device);

            if (featureLevel < 0xa000)
            {
                CoreLogger.Info("GPU", $"D3D feature level 0x{featureLevel:X4} too low — using software mode");
                return new Result(false, "N/A", "N/A", $"Feature level 0x{featureLevel:X4} below minimum (0xA000)");
            }

            string renderer = $"D3D11 Feature Level 0x{featureLevel:X4}";
            CoreLogger.Info("GPU", $"GPU probe SUCCESS — {renderer}. Hardware acceleration enabled.");
            return new Result(true, renderer, "N/A", string.Empty);
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("GPU", $"GPU capability probe threw exception: {ex.Message}");
            return new Result(false, "N/A", "N/A", ex.Message);
        }
    }
}