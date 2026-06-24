using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>
/// Software fallback video view. Reads raw BGRA frames from a cross-process
/// MemoryMappedFile and displays them via an Avalonia Image + WriteableBitmap.
/// Used when <see cref="VideoRenderMode.UseHardwareAcceleration"/> is false.
/// </summary>
public class SoftwareVideoView : UserControl
{
    private readonly Image _image = new();
    private WriteableBitmap? _bitmap;
    private MmapFrameBridge? _mmap;
    private Timer? _framePump;
    private int _currentWidth;
    private int _currentHeight;

    /// <summary>The IPC client (shared with the GPU path for command/control).</summary>
    public MpvIpcClient? IpcClient { get; set; }

    /// <summary>Called when the bitmap dimensions change (e.g., video loaded at new resolution).</summary>
    public event Action<int, int>? FrameSizeChanged;

    public SoftwareVideoView()
    {
        Content = _image;
        _image.Stretch = global::Avalonia.Media.Stretch.Uniform;
        _image.HorizontalAlignment = HorizontalAlignment.Stretch;
        _image.VerticalAlignment = VerticalAlignment.Stretch;
        Background = Brushes.Black;
    }

    /// <summary>
    /// Connects to a MemoryMappedFile created by the MPV worker process.
    /// </summary>
    public void ConnectToFrameBuffer(string mapName)
    {
        _mmap = MmapFrameBridge.Open(mapName);
        var (w, h) = _mmap.GetFrameDimensions();
        EnsureBitmap(w, h);

        // Pump frames at ~60Hz (16ms interval)
        _framePump = new Timer(PumpFrame, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(16));
    }

    private void EnsureBitmap(int width, int height)
    {
        if (_bitmap != null && _currentWidth == width && _currentHeight == height)
            return;

        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            global::Avalonia.Platform.PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
        _currentWidth = width;
        _currentHeight = height;
        _image.Source = _bitmap;
        FrameSizeChanged?.Invoke(width, height);
    }

    private void PumpFrame(object? state)
    {
        var mmap = _mmap;
        var bitmap = _bitmap;
        if (mmap == null || bitmap == null)
            return;

        if (!mmap.IsNewFrameAvailable)
            return;

        try
        {
            // Check if resolution changed
            var (w, h) = mmap.GetFrameDimensions();
            if (w != _currentWidth || h != _currentHeight)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => EnsureBitmap(w, h));
                return;
            }

            // Lock the bitmap and copy the frame data
            using (var lockCtx = bitmap.Lock())
            {
                mmap.TryCopyFrame(lockCtx.Address, _currentWidth, _currentHeight);
            }

            // Trigger UI invalidation on the dispatcher thread
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _image.InvalidateVisual());
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("SW-Video", $"Frame pump error: {ex.Message}");
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _framePump?.Dispose();
        _framePump = null;
        _mmap?.Dispose();
        _mmap = null;
        _bitmap?.Dispose();
        _bitmap = null;
        base.OnDetachedFromVisualTree(e);
    }
}