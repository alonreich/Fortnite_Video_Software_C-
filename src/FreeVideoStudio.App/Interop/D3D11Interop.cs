using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace FreeVideoStudio.App.Interop.D3D;


internal enum DriverType : int { Hardware = 1 }

[Flags]
internal enum DeviceCreationFlags : uint { None = 0, BgraSupport = 0x20, VideoSupport = 0x800 }

internal enum FeatureLevel : int { Level_10_0 = 0xA000, Level_11_0 = 0xB000 }

internal enum Format : int { B8G8R8A8_UNorm = 87 }

[Flags]
internal enum BindFlags : uint { None = 0, ShaderResource = 0x8, RenderTarget = 0x20 }

internal enum ResourceUsage : int { Default = 0 }

[Flags]
internal enum CpuAccessFlags : uint { None = 0 }

[Flags]
internal enum ResourceOptionFlags : uint { None = 0, SharedKeyedMutex = 0x100 }

/// <summary>D3D11_TEXTURE2D_DESC, field for field.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct Texture2DDescription
{
    public readonly uint Width;
    public readonly uint Height;
    public readonly uint MipLevels;
    public readonly uint ArraySize;
    public readonly Format Format;
    public readonly uint SampleCount;
    public readonly uint SampleQuality;
    public readonly ResourceUsage Usage;
    public readonly BindFlags BindFlags;
    public readonly CpuAccessFlags CpuAccessFlags;
    public readonly ResourceOptionFlags MiscFlags;

    public Texture2DDescription(Format format, uint width, uint height, uint arraySize, uint mipLevels,
        BindFlags bindFlags, ResourceUsage usage, CpuAccessFlags cpuAccessFlags,
        uint sampleCount, uint sampleQuality, ResourceOptionFlags miscFlags)
    {
        Width = width;
        Height = height;
        MipLevels = mipLevels;
        ArraySize = arraySize;
        Format = format;
        SampleCount = sampleCount;
        SampleQuality = sampleQuality;
        Usage = usage;
        BindFlags = bindFlags;
        CpuAccessFlags = cpuAccessFlags;
        MiscFlags = miscFlags;
    }
}

/// <summary>A COM interface this file knows how to QueryInterface for.</summary>
internal interface IComInterface<TSelf> where TSelf : ComObject, IComInterface<TSelf>
{
    static abstract Guid Iid { get; }
    static abstract TSelf Wrap(nint pointer);
}

/// <summary>Owns ONE reference to a COM object. Dispose releases it exactly once.</summary>
internal abstract unsafe class ComObject : IDisposable
{
    private nint _pointer;

    protected ComObject(nint pointer)
    {
        if (pointer == nint.Zero) throw new ArgumentNullException(nameof(pointer), "A COM call returned a null interface pointer.");
        _pointer = pointer;
    }

    public nint NativePointer => _pointer;

    protected void** Vtbl => *(void***)_pointer;

    public T QueryInterface<T>() where T : ComObject, IComInterface<T>
    {
        Guid iid = T.Iid;
        nint result;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtbl[0])(_pointer, &iid, &result);
        ThrowIfFailed(hr, $"QueryInterface({typeof(T).Name})");
        return T.Wrap(result);
    }

    public void Dispose()
    {
        nint p = Interlocked.Exchange(ref _pointer, nint.Zero);
        if (p != nint.Zero) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)p)[2])(p);
        GC.SuppressFinalize(this);
    }

    internal static void ThrowIfFailed(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed (HRESULT 0x{hr:X8}).", hr);
    }
}

internal static unsafe partial class D3D11
{
    private const uint SdkVersion = 7;

    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(
        nint adapter, int driverType, nint software, uint flags,
        FeatureLevel* featureLevels, uint featureLevelCount, uint sdkVersion,
        nint* device, FeatureLevel* featureLevel, nint* immediateContext);

    /// <summary>Creates a device and returns it with its immediate context.</summary>
    public static ID3D11Device D3D11CreateDevice(DriverType driverType, DeviceCreationFlags flags, params FeatureLevel[] featureLevels)
    {
        nint device, context;
        FeatureLevel chosen;
        fixed (FeatureLevel* levels = featureLevels)
        {
            int hr = D3D11CreateDevice(nint.Zero, (int)driverType, nint.Zero, (uint)flags,
                levels, (uint)featureLevels.Length, SdkVersion, &device, &chosen, &context);
            ComObject.ThrowIfFailed(hr, "D3D11CreateDevice");
        }
        return new ID3D11Device(device, new ID3D11DeviceContext(context));
    }
}

internal sealed unsafe class ID3D11Device : ComObject, IComInterface<ID3D11Device>
{
    public static Guid Iid { get; } = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    public static ID3D11Device Wrap(nint pointer) => new(pointer, null);

    /// <summary>The immediate context returned by device creation (its own reference).</summary>
    public ID3D11DeviceContext? ImmediateContext { get; }

    internal ID3D11Device(nint pointer, ID3D11DeviceContext? context) : base(pointer)
    {
        ImmediateContext = context;
    }

    /// <summary>ID3D11Device::CreateTexture2D (slot 5).</summary>
    public ID3D11Texture2D CreateTexture2D(in Texture2DDescription description)
    {
        nint texture;
        fixed (Texture2DDescription* desc = &description)
        {
            int hr = ((delegate* unmanaged[Stdcall]<nint, Texture2DDescription*, void*, nint*, int>)Vtbl[5])(NativePointer, desc, null, &texture);
            ThrowIfFailed(hr, "ID3D11Device::CreateTexture2D");
        }
        return new ID3D11Texture2D(texture);
    }
}

internal sealed unsafe class ID3D11DeviceContext : ComObject
{
    internal ID3D11DeviceContext(nint pointer) : base(pointer) { }

    /// <summary>ID3D11DeviceContext::Flush (slot 111).</summary>
    public void Flush() => ((delegate* unmanaged[Stdcall]<nint, void>)Vtbl[111])(NativePointer);
}

internal sealed class ID3D11Texture2D : ComObject, IComInterface<ID3D11Texture2D>
{
    public static Guid Iid { get; } = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static ID3D11Texture2D Wrap(nint pointer) => new(pointer);
    internal ID3D11Texture2D(nint pointer) : base(pointer) { }
}

internal sealed unsafe class IDXGIKeyedMutex : ComObject, IComInterface<IDXGIKeyedMutex>
{
    public static Guid Iid { get; } = new("9d8e1289-d7b3-465f-8126-250e349af85d");
    public static IDXGIKeyedMutex Wrap(nint pointer) => new(pointer);
    private IDXGIKeyedMutex(nint pointer) : base(pointer) { }

    /// <summary>IDXGIKeyedMutex::ReleaseSync (slot 9). Returns the HRESULT.</summary>
    public int ReleaseSync(ulong key) => ((delegate* unmanaged[Stdcall]<nint, ulong, int>)Vtbl[9])(NativePointer, key);
}

internal sealed unsafe class IDXGIResource : ComObject, IComInterface<IDXGIResource>
{
    public static Guid Iid { get; } = new("035f3ab4-482e-4e50-b41f-8a7f8bd8960b");
    public static IDXGIResource Wrap(nint pointer) => new(pointer);
    private IDXGIResource(nint pointer) : base(pointer) { }

    /// <summary>IDXGIResource::GetSharedHandle (slot 8). Zero when the call fails.</summary>
    public nint SharedHandle
    {
        get
        {
            nint handle;
            int hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtbl[8])(NativePointer, &handle);
            return hr < 0 ? nint.Zero : handle;
        }
    }
}
