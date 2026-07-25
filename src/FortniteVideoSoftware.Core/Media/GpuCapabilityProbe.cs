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

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    private const int SM_REMOTESESSION = 0x1000;

    // Real consumer GPU PCI vendor IDs. Anything else (notably Microsoft 0x1414 = WARP /
    // "Basic Render Driver" / "Hyper-V Video") is NOT a real GPU and cannot provide the
    // WGL_NV_DX_interop bridge — so we must route it to the CPU software preview path.
    private const uint VENDOR_NVIDIA = 0x10DE;
    private const uint VENDOR_AMD_1 = 0x1002;
    private const uint VENDOR_AMD_2 = 0x1022;
    private const uint VENDOR_INTEL = 0x8086;
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    /// <summary>
    /// Enumerates DXGI adapters and returns true only if a REAL hardware GPU (NVIDIA/AMD/Intel,
    /// not software-flagged) exists. Hyper-V/RDP/headless machines expose only Microsoft's
    /// software/basic adapter, which reports D3D11 FL11 but has no OpenGL interop — this is how
    /// we tell "software D3D11" apart from an actual GPU. Any failure returns true (assume GPU;
    /// the WGL interop attempt still guards, so real machines are never wrongly downgraded).
    /// </summary>
    private static unsafe bool HasRealGpuAdapter()
    {
        IntPtr factory = IntPtr.Zero;
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IID_IDXGIFactory1
            if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero)
                return true;

            nint factoryVtbl = Marshal.ReadIntPtr(factory);
            // IDXGIFactory1::EnumAdapters1 is vtable slot 12.
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<nint, uint, out nint, int>)
                Marshal.ReadIntPtr(factoryVtbl, 12 * IntPtr.Size);

            nint descBuf = Marshal.AllocHGlobal(320);
            try
            {
                for (uint i = 0; i < 16; i++)
                {
                    int hr = enumAdapters1(factory, i, out IntPtr adapter);
                    if (hr < 0 || adapter == IntPtr.Zero) break;   // DXGI_ERROR_NOT_FOUND -> done
                    try
                    {
                        nint adapterVtbl = Marshal.ReadIntPtr(adapter);
                        // IDXGIAdapter1::GetDesc1 is vtable slot 10.
                        var getDesc1 = (delegate* unmanaged[Stdcall]<nint, nint, int>)
                            Marshal.ReadIntPtr(adapterVtbl, 10 * IntPtr.Size);
                        if (getDesc1(adapter, descBuf) >= 0)
                        {
                            // DXGI_ADAPTER_DESC1 (x64): Description[128 WCHAR]=256B, then VendorId@256; Flags@304.
                            uint vendor = (uint)Marshal.ReadInt32(descBuf, 256);
                            uint flags = (uint)Marshal.ReadInt32(descBuf, 304);
                            bool isSoftware = (flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0;
                            bool isRealVendor = vendor is VENDOR_NVIDIA or VENDOR_AMD_1 or VENDOR_AMD_2 or VENDOR_INTEL;
                            if (isRealVendor && !isSoftware)
                            {
                                CoreLogger.Info("GPU", $"Real GPU adapter found (vendor 0x{vendor:X4}).");
                                return true;
                            }
                        }
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            finally { Marshal.FreeHGlobal(descBuf); }

            return false;   // only software / basic / Hyper-V adapters present -> no real GPU
        }
        catch (Exception ex)
        {
            CoreLogger.Info("GPU", $"Adapter enumeration failed ({ex.Message}); assuming GPU present.");
            return true;
        }
        finally
        {
            if (factory != IntPtr.Zero) Marshal.Release(factory);
        }
    }

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

            bool isRdpSession = GetSystemMetrics(SM_REMOTESESSION) != 0;
            if (isRdpSession)
            {
                CoreLogger.Info("GPU", "Remote desktop session detected — attempting hardware acceleration anyway.");
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

            // A D3D11 device can be created on WARP / "Microsoft Basic Render Driver" / "Hyper-V
            // Video" even with NO real GPU — those report FL11 but have no OpenGL WGL_NV_DX_interop
            // bridge, so the zero-copy preview is impossible. Verify a REAL GPU vendor exists;
            // otherwise route to the CPU software preview path (hwdec=no + sw render).
            if (!HasRealGpuAdapter())
            {
                // ===== FRESH-BUILD DIAGNOSTIC MARKERS (token: FVS-SWPREVIEW-BUILDCHECK-2026A) =====
                // These two lines are UNIQUE to this build. If you see one of them in the log, you
                // are 100% running the new software-preview binary (not a stale/leftover log).
                if (isRdpSession)
                    CoreLogger.Info("GPU", "### FVS-SWPREVIEW-BUILDCHECK-2026A :: RDP SESSION → CPU SOFTWARE PREVIEW ENGAGED ###");
                else
                    CoreLogger.Info("GPU", "### FVS-SWPREVIEW-BUILDCHECK-2026A :: CPU-ONLY (NO GPU, LOCAL/NON-RDP) → CPU SOFTWARE PREVIEW ENGAGED ###");

                CoreLogger.Info("GPU", "No real GPU adapter (software/basic/Hyper-V display only) — using CPU software preview.");
                return new Result(false, "Software adapter", "N/A", "No hardware GPU adapter present (software/basic/Hyper-V display)");
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