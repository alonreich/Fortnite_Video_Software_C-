using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App;

public class MpvVideoView : NativeControlHost
{
    private nint _mpvHandle;
    private nint _hwnd;

    public void AttachMpv(nint mpvHandle)
    {
        _mpvHandle = mpvHandle;
        UpdateMpvWid();
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        // On Windows, NativeControlHost creates a generic HWND by default if we don't override.
        // Actually, we just need to return base and then get the handle.
        IPlatformHandle handle = base.CreateNativeControlCore(parent);
        _hwnd = handle.Handle;
        UpdateMpvWid();
        return handle;
    }

    private void UpdateMpvWid()
    {
        if (_mpvHandle != nint.Zero && _hwnd != nint.Zero)
        {
            // libmpv expects "wid" as int64 string
            string widStr = _hwnd.ToInt64().ToString();
            MpvWrapper.mpv_set_option_string(_mpvHandle, "wid", widStr);
        }
    }
}
