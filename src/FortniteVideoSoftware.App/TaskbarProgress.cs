using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FortniteVideoSoftware.App;

/// <summary>
/// IDEA_3 — the little progress bar that fills the app's Windows taskbar icon during an export,
/// plus a flash when it finishes.
///
/// WHY IT IS WRITTEN THIS WAY. Windows only exposes this through the COM interface ITaskbarList3.
/// The usual C# route is a [ComImport] interface, which relies on the built-in COM marshaller —
/// something this project cannot use, because it publishes with NativeAOT (PublishAot + TrimMode
/// full) and that marshaller is trimmed away. So the vtable is walked by hand with unmanaged
/// function pointers: no reflection, no runtime code generation, nothing for the trimmer to remove.
///
/// FAILURE POLICY. Every entry point is best-effort and swallows everything. If COM refuses, the
/// shell is not running, the OS is older, or the session is remote, the export must carry on
/// EXACTLY as before — just with no bar on the icon. Nothing here may ever throw into a caller or
/// slow an encode down.
///
/// THREADING. All calls marshal to the UI thread internally via the caller; the COM object is
/// created once, apartment-agnostic for our purposes since we only ever touch it from the UI
/// thread. Do not call these from the FFmpeg reader threads — route through Dispatcher first, as
/// PhaseOverlayControl already does.
/// </summary>
internal static unsafe partial class TaskbarProgress
{
    /// <summary>Matches the native TBPFLAG enum.</summary>
    internal enum State
    {
        NoProgress = 0,
        Indeterminate = 1,
        Normal = 2,
        Error = 4,
        Paused = 8
    }

    private static readonly Guid CLSID_TaskbarList =
        new(0x56FDF344, 0xFD6D, 0x11d0, 0x95, 0x8A, 0x00, 0x60, 0x97, 0xC9, 0xA0, 0x90);

    private static readonly Guid IID_ITaskbarList3 =
        new(0xEA1AFB91, 0x9E28, 0x4B86, 0x90, 0xE9, 0x9E, 0x9F, 0x8A, 0x5E, 0xEF, 0xAF);

    private const uint CLSCTX_INPROC_SERVER = 1;

    private const int VT_RELEASE = 2;
    private const int VT_HRINIT = 3;
    private const int VT_SETPROGRESSVALUE = 9;
    private const int VT_SETPROGRESSSTATE = 10;

    private static readonly object Sync = new();
    private static void** _taskbar;
    private static bool _initialised;
    private static bool _unavailable;

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FLASHWINFO pwfi);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_TRAY = 0x00000002;

    /// <summary>
    /// FLASHW_TIMER | 0x08. This means "keep flashing until the window is brought to the
    /// foreground" and it makes FLASHWINFO.uCount IRRELEVANT - Windows ignores the count when
    /// this bit is set. Flash() used to pass it together with uCount = uint.MaxValue, which is
    /// why the finished-export flash blinked forever instead of a fixed number of times.
    /// Kept named here so nobody re-adds it by accident thinking it is a no-op.
    /// </summary>
    private const uint FLASHW_TIMERNOFG_UNUSED = 0x0000000C;

    /// <summary>Creates the shell object once. Returns false forever after the first failure.</summary>
    private static bool TryGetTaskbar(out void** taskbar)
    {
        taskbar = null;

        lock (Sync)
        {
            if (_unavailable) return false;
            if (_initialised)
            {
                taskbar = _taskbar;
                return taskbar != null;
            }

            _initialised = true;
            try
            {
                int hr = CoCreateInstance(CLSID_TaskbarList, IntPtr.Zero, CLSCTX_INPROC_SERVER,
                                          IID_ITaskbarList3, out IntPtr ptr);
                if (hr < 0 || ptr == IntPtr.Zero)
                {
                    _unavailable = true;
                    SafeLog(hr == unchecked((int)0x80010106)
                        ? "Taskbar progress unavailable: the UI thread is not in the expected COM apartment. Exports run normally, just without the taskbar bar."
                        : $"Taskbar progress unavailable: the shell object could not be created (hr=0x{hr:X8}). Exports run normally, just without the taskbar bar.");
                    return false;
                }

                var obj = (void**)ptr;

                int init = ((delegate* unmanaged<void**, int>)((void**)*obj)[VT_HRINIT])(obj);
                if (init < 0)
                {
                    ReleaseUnlocked(obj);
                    _unavailable = true;
                    SafeLog($"Taskbar progress unavailable: HrInit failed (hr=0x{init:X8}).");
                    return false;
                }

                _taskbar = obj;
                taskbar = obj;
                return true;
            }
            catch (Exception ex)
            {
                _unavailable = true;
                SafeLog($"Taskbar progress unavailable: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Logs once, never throws. Deliberately mirrors UiSoundEffect.SafeLog: an optional cosmetic
    /// feature must not be able to disturb the thing it decorates.
    /// </summary>
    private static void SafeLog(string message)
    {
        try { RuntimeLog.Info("Taskbar", message); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private static void ReleaseUnlocked(void** obj)
    {
        try { ((delegate* unmanaged<void**, uint>)((void**)*obj)[VT_RELEASE])(obj); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    /// <summary>
    /// Shows determinate progress, 0-100. Values are clamped. A window handle of zero is ignored,
    /// which is what happens on the platforms where Avalonia has no Win32 handle to give us.
    /// </summary>
    public static void SetProgress(IntPtr hwnd, int percent)
    {
        if (hwnd == IntPtr.Zero) return;
        if (!TryGetTaskbar(out void** tb)) return;

        try
        {
            ulong value = (ulong)Math.Clamp(percent, 0, 100);
            ((delegate* unmanaged<void**, IntPtr, ulong, ulong, int>)((void**)*tb)[VT_SETPROGRESSVALUE])(tb, hwnd, value, 100UL);
            ((delegate* unmanaged<void**, IntPtr, int, int>)((void**)*tb)[VT_SETPROGRESSSTATE])(tb, hwnd, (int)State.Normal);
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    public static void SetState(IntPtr hwnd, State state)
    {
        if (hwnd == IntPtr.Zero) return;
        if (!TryGetTaskbar(out void** tb)) return;

        try
        {
            ((delegate* unmanaged<void**, IntPtr, int, int>)((void**)*tb)[VT_SETPROGRESSSTATE])(tb, hwnd, (int)state);
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    /// <summary>Clears the bar completely. Safe to call when nothing is running.</summary>
    public static void Clear(IntPtr hwnd) => SetState(hwnd, State.NoProgress);

    /// <summary>
    /// Flashes the taskbar button a FIXED number of times and then stops, whether or not the user
    /// ever comes back to the window.
    ///
    /// This used to flash forever (FLASHW_TIMERNOFG, uCount ignored), which is hostile on a
    /// machine the user has walked away from: come back an hour later and the button is still
    /// blinking. Five slow blinks reads as "it finished" and then leaves the taskbar alone.
    ///
    /// dwTimeout is the interval between blinks in milliseconds. Zero would mean "use the system
    /// cursor blink rate", which is roughly 500 ms and reads as an alarm rather than a
    /// notification, so it is passed explicitly.
    /// </summary>
    /// <param name="count">Number of blinks. Clamped to 1-30.</param>
    /// <param name="intervalMs">Milliseconds per blink. Clamped to 120-3000.</param>
    public static void Flash(IntPtr hwnd, int count = 5, int intervalMs = 750)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            var info = new FLASHWINFO
            {
                cbSize = (uint)Unsafe.SizeOf<FLASHWINFO>(),
                hwnd = hwnd,
                dwFlags = FLASHW_TRAY,
                uCount = (uint)Math.Clamp(count, 1, 30),
                dwTimeout = (uint)Math.Clamp(intervalMs, 120, 3000)
            };
            FlashWindowEx(ref info);
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    /// <summary>Cancels an in-flight flash, e.g. if a new export starts before the blinks finish.</summary>
    public static void StopFlash(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            var info = new FLASHWINFO
            {
                cbSize = (uint)Unsafe.SizeOf<FLASHWINFO>(),
                hwnd = hwnd,
                dwFlags = 0,
                uCount = 0,
                dwTimeout = 0
            };
            FlashWindowEx(ref info);
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    /// <summary>
    /// Releases the shell object. Called from the desktop lifetime Exit hook alongside the other
    /// process-wide teardown, so the COM reference is not left dangling at shutdown.
    /// </summary>
    public static void Shutdown()
    {
        lock (Sync)
        {
            if (_taskbar != null)
            {
                ReleaseUnlocked(_taskbar);
                _taskbar = null;
            }
            _unavailable = true;
        }
    }
}
