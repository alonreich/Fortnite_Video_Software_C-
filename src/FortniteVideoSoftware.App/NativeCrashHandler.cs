using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace FortniteVideoSoftware.App;

/// <summary>
/// Installs a Win32 Vectored Exception Handler so fatal NATIVE crashes — e.g. the
/// 0xC0000005 access violations inside libmpv / the NVIDIA GL driver during the
/// zero-copy render bridge — are written into the app's own RuntimeLog before the
/// process dies.
///
/// The managed handlers in Program.cs (AppDomain.UnhandledException /
/// TaskScheduler.UnobservedTaskException) NEVER fire for these: a corrupted-state
/// native crash makes the CLR fast-fail, so only Windows Error Reporting (Event
/// Viewer) ever saw them. This closes that gap so the crash lands in the same .log
/// file as everything else. We only OBSERVE and log — the exception is always left to
/// continue its normal search, so runtime behaviour is unchanged.
/// </summary>
internal static unsafe class NativeCrashHandler
{
    [DllImport("kernel32.dll")]
    private static extern nint AddVectoredExceptionHandler(uint First, nint Handler);

    // Fatal SEH codes worth capturing. Managed .NET exceptions (0xE0434352) and
    // debugger / benign first-chance noise are deliberately NOT in this set so we never
    // flood the log with normal, handled exceptions.
    private const uint EXCEPTION_ACCESS_VIOLATION    = 0xC0000005;
    private const uint EXCEPTION_IN_PAGE_ERROR       = 0xC0000006;
    private const uint EXCEPTION_ILLEGAL_INSTRUCTION = 0xC000001D;
    private const uint EXCEPTION_PRIV_INSTRUCTION    = 0xC0000096;
    private const uint EXCEPTION_INT_DIVIDE_BY_ZERO  = 0xC0000094;
    private const uint STATUS_HEAP_CORRUPTION        = 0xC0000374;
    private const uint STATUS_STACK_BUFFER_OVERRUN   = 0xC0000409;
    private const uint EXCEPTION_STACK_OVERFLOW      = 0xC00000FD;

    private const int EXCEPTION_CONTINUE_SEARCH = 0;

    // A truly fatal SEH crashes right after we log it. Cap logging so a (rare) recoverable
    // native AV used as control flow can't spam the file.
    private static int _logged;

    public static void Install()
    {
        try
        {
            delegate* unmanaged[Stdcall]<nint, int> handler = &VectoredHandler;
            // First=1: run before other vectored handlers so we observe the fault first.
            AddVectoredExceptionHandler(1, (nint)handler);
            RuntimeLog.Info("CRASH GUARD", "Native vectored exception handler installed.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CRASH GUARD", $"Failed to install native crash handler: {ex.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int VectoredHandler(nint exceptionInfo)
    {
        try
        {
            if (exceptionInfo == nint.Zero) return EXCEPTION_CONTINUE_SEARCH;

            // EXCEPTION_POINTERS { PEXCEPTION_RECORD ExceptionRecord; PCONTEXT ContextRecord; }
            nint recordPtr = *(nint*)exceptionInfo;
            if (recordPtr == nint.Zero) return EXCEPTION_CONTINUE_SEARCH;

            // EXCEPTION_RECORD (x64): DWORD ExceptionCode @0; DWORD Flags @4;
            //                         _EXCEPTION_RECORD* @8; PVOID ExceptionAddress @16.
            uint code = *(uint*)recordPtr;
            if (!IsFatal(code)) return EXCEPTION_CONTINUE_SEARCH;

            if (Interlocked.Increment(ref _logged) > 3) return EXCEPTION_CONTINUE_SEARCH;

            nint address = *(nint*)(recordPtr + 16);
            RuntimeLog.EmergencyWrite("NATIVE CRASH",
                $"SEH 0x{code:X8} at 0x{address.ToString("X")} on thread {Environment.CurrentManagedThreadId}. " +
                "Fatal native exception (e.g. libmpv / GPU driver) — full faulting-module stack is in Windows Event Viewer; " +
                "it will also be folded into this log on the next launch by the Event Viewer digest.");
        }
        catch
        {
            // Never let the observer itself destabilise the crashing process further.
        }
        return EXCEPTION_CONTINUE_SEARCH;
    }

    private static bool IsFatal(uint code) =>
        code == EXCEPTION_ACCESS_VIOLATION ||
        code == EXCEPTION_IN_PAGE_ERROR ||
        code == EXCEPTION_ILLEGAL_INSTRUCTION ||
        code == EXCEPTION_PRIV_INSTRUCTION ||
        code == EXCEPTION_INT_DIVIDE_BY_ZERO ||
        code == STATUS_HEAP_CORRUPTION ||
        code == STATUS_STACK_BUFFER_OVERRUN ||
        code == EXCEPTION_STACK_OVERFLOW;
}
