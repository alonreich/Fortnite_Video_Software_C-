using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// Manages a cross-process MemoryMappedFile that the MPV worker process writes
/// raw BGRA frames to and the UI process reads from. Used only in software mode
/// (when <see cref="VideoRenderMode.UseHardwareAcceleration"/> is false).
///
/// Frame layout in shared memory (header + pixel data):
///   Offset 0:   FrameCounter  (int)  — incremented by worker after each frame write
///   Offset 4:   Width         (int)  — frame width in pixels
///   Offset 8:   Height        (int)  — frame height in pixels
///   Offset 12:  Padding       (int)  — reserved for alignment
///   Offset 16:  PixelData     (byte[]) — BGRA pixel data, Width*Height*4 bytes
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MmapFrameBridge : IDisposable
{
    private const int HEADER_SIZE = 16;
    private const string MAP_NAME_PREFIX = "FVS_FrameBuffer";

    private readonly MemoryMappedFile _mmap;
    private readonly MemoryMappedViewAccessor _view;
    private readonly int _frameBufferSize;
    private int _lastFrameCounter = -1;

    public string MapName { get; }
    public int Width { get; }
    public int Height { get; }
    public int TotalSize => HEADER_SIZE + _frameBufferSize;

    private MmapFrameBridge(string mapName, int width, int height, MemoryMappedFile mmap)
    {
        MapName = mapName;
        Width = width;
        Height = height;
        _frameBufferSize = width * height * 4;
        _mmap = mmap;
        _view = mmap.CreateViewAccessor(0, HEADER_SIZE + _frameBufferSize, MemoryMappedFileAccess.ReadWrite);
    }

    public static MmapFrameBridge Create(int width = 1920, int height = 1080)
    {
        string mapName = $"{MAP_NAME_PREFIX}_{Guid.NewGuid():N}";
        int totalSize = HEADER_SIZE + width * height * 4;

        CoreLogger.Info("MMAP", $"Creating frame buffer '{mapName}' — {totalSize:N0} bytes ({width}×{height} BGRA)");

        var mmap = MemoryMappedFile.CreateOrOpen(mapName, totalSize, MemoryMappedFileAccess.ReadWrite);

        using var headerView = mmap.CreateViewAccessor(0, HEADER_SIZE, MemoryMappedFileAccess.ReadWrite);
        headerView.Write(0, 0);
        headerView.Write(4, width);
        headerView.Write(8, height);
        headerView.Write(12, 0);
        headerView.Flush();

        return new MmapFrameBridge(mapName, width, height, mmap);
    }

    public static MmapFrameBridge Open(string mapName)
    {
        var mmap = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.ReadWrite);
        using var headerView = mmap.CreateViewAccessor(0, HEADER_SIZE, MemoryMappedFileAccess.Read);
        int width = headerView.ReadInt32(4);
        int height = headerView.ReadInt32(8);

        CoreLogger.Info("MMAP", $"Opened existing frame buffer '{mapName}' — {width}×{height}");
        return new MmapFrameBridge(mapName, width, height, mmap);
    }

    public bool IsNewFrameAvailable
    {
        get
        {
            int counter = _view.ReadInt32(0);
            return counter != _lastFrameCounter;
        }
    }

    /// <summary>
    /// Reads the current frame dimensions from the header.
    /// </summary>
    public (int Width, int Height) GetFrameDimensions()
    {
        int w = _view.ReadInt32(4);
        int h = _view.ReadInt32(8);
        return (w, h);
    }

    public bool TryCopyFrame(IntPtr destination, int destinationWidth, int destinationHeight)
    {
        int counter = _view.ReadInt32(0);
        if (counter == _lastFrameCounter)
            return false;

        _lastFrameCounter = counter;

        int sourceWidth = _view.ReadInt32(4);
        int sourceHeight = _view.ReadInt32(8);
        int sourceSize = sourceWidth * sourceHeight * 4;

        using var pixelView = _mmap.CreateViewAccessor(HEADER_SIZE, sourceSize, MemoryMappedFileAccess.Read);
        unsafe
        {
            byte* src = (byte*)pixelView.SafeMemoryMappedViewHandle.DangerousGetHandle();
            Buffer.MemoryCopy(src, (void*)destination, destinationWidth * destinationHeight * 4, sourceSize);
        }

        return true;
    }

    public void WriteFrame(IntPtr source, int width, int height)
    {
        int size = width * height * 4;
        if (size > _frameBufferSize)
            throw new InvalidOperationException($"Frame too large: {size} > buffer capacity {_frameBufferSize}");

        using var pixelView = _mmap.CreateViewAccessor(HEADER_SIZE, size, MemoryMappedFileAccess.Write);
        unsafe
        {
            byte* dst = (byte*)pixelView.SafeMemoryMappedViewHandle.DangerousGetHandle();
            Buffer.MemoryCopy((void*)source, dst, size, size);
        }

        int counter = _view.ReadInt32(0) + 1;
        _view.Write(0, counter);
        _view.Flush();
    }

    public string GetMpvVoArgs()
    {
        return $"--vo=rawvideo --video-format=bgra --video-unscaled=yes";
    }

    public void Dispose()
    {
        try { _view?.Dispose(); } catch { }
        try { _mmap?.Dispose(); } catch { }
    }
}