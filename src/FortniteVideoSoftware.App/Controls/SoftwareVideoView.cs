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
///
/// NOTE: This view is currently unused — all three apps (Main, Video Merger,
/// Crop Tools) use the GPU-accelerated <see cref="MpvVideoView"/> path via
/// libmpv's OpenGL render API. The MemoryMappedFile bridge (MmapFrameBridge)
/// was part of an abandoned out-of-process software-rendering experiment and
/// has been stubbed out so the file compiles without blocking the build.
/// If software fallback is needed in the future, implement MmapFrameBridge
/// in Core/Media and restore ConnectToFrameBuffer().
/// </summary>
public class SoftwareVideoView : UserControl
{
    private readonly Image _image = new();
    private WriteableBitmap? _bitmap;
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
    /// Currently stubbed — see class-level NOTE.
    /// </summary>
    public void ConnectToFrameBuffer(string mapName)
    {
        // MmapFrameBridge was removed — this path is unused.
        // All rendering goes through MpvVideoView (OpenGL/libmpv).
        RuntimeLog.Info("SW-Video", "ConnectToFrameBuffer called but MmapFrameBridge is not implemented. Software fallback is disabled.");
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

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _framePump?.Dispose();
        _framePump = null;
        _bitmap?.Dispose();
        _bitmap = null;
        base.OnDetachedFromVisualTree(e);
    }
}