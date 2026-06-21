using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App;

public class MpvVideoView : NativeControlHost
{
    private nint _hwnd;
    public MpvIpcClient? IpcClient { get; private set; }

    public async Task StartMpvProcessAsync(string mpvPath)
    {
        if (_hwnd == nint.Zero)
        {
            RuntimeLog.Fail("MPV", "StartMpvProcessAsync called before window was loaded! _hwnd is zero.");
            return;
        }
        
        RuntimeLog.Info("MPV", $"Starting MPV with path: {mpvPath}");
        IpcClient = new MpvIpcClient();
        try
        {
            await IpcClient.StartAsync(_hwnd, mpvPath);
        }
        catch (System.Exception ex)
        {
            RuntimeLog.Fail("MPV", $"Failed to start MPV process: {ex.Message}");
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        IPlatformHandle handle = base.CreateNativeControlCore(parent);
        _hwnd = handle.Handle;
        return handle;
    }
    
    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        IpcClient?.Dispose();
        base.DestroyNativeControlCore(control);
    }
}
