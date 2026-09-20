// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// TELEMETRY_01 — samples system CPU load, memory pressure and GPU utilisation for the export
/// overlay's live gauges.
///
/// WHY THIS IS ITS OWN TYPE. It was four fields and five methods inside
/// <see cref="PhaseOverlayControl"/>, a 2,492-line control that also hosts a progress bar, a log
/// tail, a Win32 taskbar integration and a fighting-game easter egg. Measured by identifier
/// reference count in that one file, <c>fight</c> outnumbers <c>Progress</c> six to one. Sampling
/// hardware is a reusable, testable job; welded to an easter egg it is neither, and its state sat
/// in the same 117-field bag as the animation state.
///
/// ⚠️ TWO REAL CONCURRENCY DEFECTS CAME ACROSS WITH IT AND ARE FIXED HERE:
///
///   1. OVERLAPPING SAMPLES CORRUPTED THE CPU READING. The host's 1-second DispatcherTimer fired
///      <c>Task.Run(...)</c> with NO overlap guard, and <c>GetCpuUsage</c> is a read-modify-write
///      of <c>_lastIdle</c>/<c>_lastSys</c> — it derives a percentage from the DELTA since the
///      previous call. Two ticks running concurrently on thread-pool threads both read the old
///      counters, both compute a delta, and both write back; the second delta is then measured
///      against a baseline the first already advanced. The result is a nonsense percentage
///      (clamped to 0, or a spike). The likeliest moment for the pool to be busy enough to delay a
///      tick is during a heavy export — which is exactly when this overlay is on screen and being
///      watched. <see cref="TryBeginSample"/> makes a sample single-flight, and the counters are
///      only ever touched under <c>_cpuGate</c>.
///
///   2. THE GPU READING CROSSED THREADS UNSYNCHRONISED. <c>_lastGpu</c> was written from
///      nvidia-smi's <c>OutputDataReceived</c> callback thread and read on the UI thread as a plain
///      field, so the UI could read an indefinitely stale value with no memory barrier. It is now
///      written and read through <see cref="Volatile"/>.
///
/// Neither defect can corrupt data or crash — they put a wrong number on a gauge. That is
/// precisely why they were never worth their own finding, and precisely why the file kept growing
/// around them.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
internal sealed class HardwareTelemetrySampler : IDisposable
{
    /// <summary>
    /// TELEMETRY_01 — guards the CPU tick counters. <see cref="GetCpuUsage"/> is a
    /// read-modify-write against the PREVIOUS sample, so it is only correct when exactly one
    /// caller is inside it.
    /// </summary>
    private readonly object _cpuGate = new();

    private ulong _lastIdle;
    private ulong _lastSys;

    /// <summary>
    /// TELEMETRY_01 — written on nvidia-smi's output-callback thread, read on the UI thread.
    /// Accessed only through <see cref="Volatile"/>. Never a plain field read.
    /// </summary>
    private int _lastGpu;

    /// <summary>0 = idle, 1 = a sample is in flight. <see cref="Interlocked"/> only.</summary>
    private int _sampleInFlight;

    private Process? _smiProcess;

    /// <summary>Latest GPU utilisation (0-100) reported by nvidia-smi, or 0 when unavailable.</summary>
    internal int LastGpu => Volatile.Read(ref _lastGpu);

    /// <summary>
    /// TELEMETRY_01 — single-flight gate. Returns false when the previous sample has not finished,
    /// in which case the caller must skip this tick entirely. Every successful call MUST be paired
    /// with <see cref="EndSample"/> in a finally block.
    /// </summary>
    internal bool TryBeginSample() => Interlocked.CompareExchange(ref _sampleInFlight, 1, 0) == 0;

    /// <summary>Releases the single-flight gate taken by <see cref="TryBeginSample"/>.</summary>
    internal void EndSample() => Volatile.Write(ref _sampleInFlight, 0);

    /// <summary>
    /// Takes one reading of all three metrics. Safe to call from a thread-pool thread; the CPU
    /// counters are serialised internally so a stray concurrent call cannot corrupt the delta.
    /// </summary>
    internal (int Cpu, int Mem, int Gpu) Sample() => (GetCpuUsage(), GetMemUsage(), LastGpu);

    /// <summary>
    /// Starts (or restarts) the nvidia-smi polling child. Non-throwing: a machine with no NVIDIA
    /// GPU simply reports 0, which is the pre-existing behaviour.
    /// </summary>
    internal void Start()
    {
        try
        {
            StopSmiProcess();

            _smiProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=utilization.gpu,utilization.encoder --format=csv,noheader,nounits -i 0 -l 1",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            _smiProcess.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    var parts = e.Data.Trim().Split(',');
                    if (parts.Length >= 2)
                    {
                        int core = int.TryParse(parts[0].Trim(), out int c) ? c : 0;
                        int enc = int.TryParse(parts[1].Trim(), out int ex) ? ex : 0;
                        // TELEMETRY_01 — this is the cross-thread write. Volatile, not a plain store.
                        Volatile.Write(ref _lastGpu, Math.Max(0, Math.Min(100, Math.Max(core, enc))));
                    }
                }
            };
            _smiProcess.Start();

            try { FortniteVideoSoftware.Core.Infrastructure.ChildProcessTracker.AddProcess(_smiProcess); }
            catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

            _smiProcess.BeginOutputReadLine();
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    /// <summary>Stops the nvidia-smi child. Non-throwing; safe to call when never started.</summary>
    internal void Stop() => StopSmiProcess();

    private void StopSmiProcess()
    {
        var previous = _smiProcess;
        _smiProcess = null;
        if (previous == null) return;

        try
        {
            // TELEMETRY_01 — routed through the project's bounded ladder instead of a bare Kill.
            // attemptQuitCommand: false because nvidia-smi has no redirected stdin and nothing to
            // finalize; cooperativeGraceMs: 0 so stopping the overlay stays instant. What the
            // ladder adds over the two different bare Kill calls this replaced (one with
            // entireProcessTree, one without) is a single consistent path plus a bounded exit
            // confirmation, so teardown cannot proceed while the child is still dying.
            FortniteVideoSoftware.Core.Infrastructure.GracefulProcessTerminator.Terminate(
                previous, "Telemetry", attemptQuitCommand: false, cooperativeGraceMs: 0);
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

        try { previous.Dispose(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    public void Dispose() => StopSmiProcess();

    /// <summary>
    /// System-wide CPU load since the previous call, as a 0-100 percentage.
    ///
    /// ⚠️ STATEFUL BY NATURE — the value IS the delta between two calls, so the counters must be
    /// advanced exactly once per sample. That is what <c>_cpuGate</c> is for; before TELEMETRY_01
    /// there was no gate and overlapping ticks produced garbage.
    /// </summary>
    private int GetCpuUsage()
    {
        lock (_cpuGate)
        {
            if (GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
            {
                ulong sysIdle = ((ulong)idle.dwHighDateTime << 32) | idle.dwLowDateTime;
                ulong sysKernel = ((ulong)kernel.dwHighDateTime << 32) | kernel.dwLowDateTime;
                ulong sysUser = ((ulong)user.dwHighDateTime << 32) | user.dwLowDateTime;
                ulong sysTime = sysKernel + sysUser;

                if (_lastSys > 0)
                {
                    ulong idlDiff = sysIdle - _lastIdle;
                    ulong sysDiff = sysTime - _lastSys;
                    if (sysDiff > 0)
                    {
                        double dCpu = (sysDiff - idlDiff) * 100.0 / sysDiff;
                        _lastIdle = sysIdle;
                        _lastSys = sysTime;
                        return Math.Max(0, Math.Min(100, (int)dCpu));
                    }
                }
                _lastIdle = sysIdle;
                _lastSys = sysTime;
            }
            return 0;
        }
    }

    /// <summary>Memory pressure as the 0-100 figure Windows itself reports. Stateless.</summary>
    private int GetMemUsage()
    {
        MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
        // AOTSAFETY_03: Marshal.SizeOf(Type) asks the runtime to build marshalling code for a
        // type it only knows reflectively — unavailable after AOT compilation. The generic
        // overload is computed at compile time and yields the identical size.
        memStatus.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        if (GlobalMemoryStatusEx(ref memStatus))
        {
            return (int)memStatus.dwMemoryLoad;
        }
        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
