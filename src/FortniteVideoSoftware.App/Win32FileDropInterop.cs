using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;

namespace FortniteVideoSoftware.App;

/// <summary>
/// Legacy WM_DROPFILES fallback so Explorer drag &amp; drop always reaches the app.
///
/// WHY THIS EXISTS: Avalonia registers an OLE drop target per window (that is what
/// the DragDrop.AllowDrop / DragDrop.DropEvent XAML plumbing uses). OLE drag &amp; drop
/// from Explorer is silently blocked by Windows UIPI whenever this process runs at a
/// higher integrity level than Explorer (e.g. the user launched the exe or a dev
/// script "as administrator"). The result is "drag &amp; drop does nothing".
///
/// This helper attaches a Win32 subclass to the window, opts the window in to the
/// legacy shell drop messages (ChangeWindowMessageFilterEx for WM_DROPFILES,
/// WM_COPYDATA and WM_COPYGLOBALDATA) and calls DragAcceptFiles. When OLE works,
/// Explorer uses the OLE path and WM_DROPFILES never fires — so the two mechanisms
/// never double-handle a drop. When OLE is blocked, Explorer falls back to the
/// legacy path and the drop still arrives here.
/// </summary>
public static class Win32FileDropInterop
{
    private const uint WM_DROPFILES = 0x0233;
    private const uint WM_COPYDATA = 0x004A;
    private const uint WM_COPYGLOBALDATA = 0x0049;
    private const uint MSGFLT_ALLOW = 1;

    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(nint hWnd, bool fAccept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(nint hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(nint hDrop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(nint hWnd, uint message, uint action, nint pChangeFilterStruct);

    // Delegates passed to native code must be kept alive for the window lifetime,
    // otherwise the GC collects them and the subclass callback crashes the process.
    private static readonly List<SubclassProc> _keepAlive = new();

    /// <summary>
    /// Attaches the WM_DROPFILES fallback to <paramref name="window"/>. The callback
    /// is invoked on the UI thread with the full list of dropped file paths.
    /// </summary>
    public static void Attach(Window window, Action<string[]> onFilesDropped)
    {
        if (!OperatingSystem.IsWindows()) return;

        window.Opened += (_, _) =>
        {
            try
            {
                nint hwnd = window.TryGetPlatformHandle()?.Handle ?? nint.Zero;
                if (hwnd == nint.Zero) return;

                // Allow the legacy drop messages through UIPI in case we run elevated.
                ChangeWindowMessageFilterEx(hwnd, WM_DROPFILES, MSGFLT_ALLOW, nint.Zero);
                ChangeWindowMessageFilterEx(hwnd, WM_COPYDATA, MSGFLT_ALLOW, nint.Zero);
                ChangeWindowMessageFilterEx(hwnd, WM_COPYGLOBALDATA, MSGFLT_ALLOW, nint.Zero);
                DragAcceptFiles(hwnd, true);

                SubclassProc proc = (h, msg, wParam, lParam, id, refData) =>
                {
                    if (msg == WM_DROPFILES)
                    {
                        try
                        {
                            uint count = DragQueryFile(wParam, 0xFFFFFFFF, null, 0);
                            var files = new List<string>((int)count);
                            for (uint i = 0; i < count; i++)
                            {
                                uint len = DragQueryFile(wParam, i, null, 0);
                                if (len == 0) continue;
                                var sb = new StringBuilder((int)len + 1);
                                if (DragQueryFile(wParam, i, sb, (uint)sb.Capacity) > 0)
                                {
                                    files.Add(sb.ToString());
                                }
                            }

                            if (files.Count > 0)
                            {
                                string[] payload = files.ToArray();
                                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                {
                                    try { onFilesDropped(payload); }
                                    catch (Exception ex) { RuntimeLog.Fail("DND", ex); }
                                });
                            }
                        }
                        finally
                        {
                            DragFinish(wParam);
                        }

                        return 0;
                    }

                    return DefSubclassProc(h, msg, wParam, lParam);
                };

                _keepAlive.Add(proc);
                if (SetWindowSubclass(hwnd, proc, 1, 0))
                {
                    RuntimeLog.Info("DND", $"WM_DROPFILES fallback attached to '{window.Title}'.");
                    window.Closed += (s, ev) =>
                    {
                        RemoveWindowSubclass(hwnd, proc, 1);
                        _keepAlive.Remove(proc);
                    };
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("DND", ex);
            }
        };
    }
}
