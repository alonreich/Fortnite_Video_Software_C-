using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Rendering.Composition;
using FortniteVideoSoftware.App.Interop;
using FortniteVideoSoftware.Core.Media;
using System.Runtime.CompilerServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FortniteVideoSoftware.App;

public sealed class MpvVideoView : Control, IDisposable
{
    public static readonly StyledProperty<bool> IsSoftwareFallbackActiveProperty =
        AvaloniaProperty.Register<MpvVideoView, bool>(nameof(IsSoftwareFallbackActive), false);

    public bool IsSoftwareFallbackActive
    {
        get => GetValue(IsSoftwareFallbackActiveProperty);
        private set => SetValue(IsSoftwareFallbackActiveProperty, value);
    }

    private const string MpvApiTypeOpenGL = "opengl";
    private const string InteropLogStep = "MPV-Interop";

    private nint _mpvHandle;
    private nint _renderContext;
    private GCHandle _gcHandle;

    private CompositionSurfaceVisual? _surfaceVisual;
    private CompositionDrawingSurface? _drawingSurface;
    private ICompositionGpuInterop? _gpuInterop;

    private int _isUpdateQueued = 0;
    private int _currentBufferIndex = 0;

    private ID3D11Device? _d3d11Device;
    private ID3D11DeviceContext? _d3d11Context;

    private nint _dummyHwnd;
    private nint _dummyHdc;
    private nint _hglrc;
    private nint _dxInteropDevice;

    private const int SwapChainSize = 16;
    private const ulong ProducerKey = 0;
    private const ulong ConsumerKey = 1;
    private const int KeyedMutexWaitMs = 1000;

    private uint[] _glFramebuffers = new uint[SwapChainSize];
    private uint[] _glTextures = new uint[SwapChainSize];

    private ID3D11Texture2D?[] _sharedTextures = new ID3D11Texture2D?[SwapChainSize];
    private readonly IDXGIKeyedMutex?[] _sharedTextureMutexes = new IDXGIKeyedMutex?[SwapChainSize];
    private readonly nint[] _renderTexturePtrs = new nint[SwapChainSize];
    private readonly nint[] _sharedTextureHandles = new nint[SwapChainSize];
    private readonly nint[] _dxInteropObjects = new nint[SwapChainSize];
    private readonly ICompositionImportedGpuImage?[] _importedImages = new ICompositionImportedGpuImage?[SwapChainSize];
    private readonly nint[] _lockedInteropObjects = new nint[1];

    private nint _openglLibrary;
    private int _cachedWidth;
    private int _cachedHeight;
    private readonly object _renderLock = new object();

    private System.Threading.Thread? _renderThread;
    private readonly System.Threading.AutoResetEvent _renderSignal = new(false);
    private volatile bool _renderThreadRunning;

    private volatile bool _renderThreadExited;
    private volatile bool _disposing;


    private int _renderTextureW;
    private int _renderTextureH;

    public MpvIpcClient? IpcClient { get; private set; }

    public void InitializeMpv(string mpvPath)
    {
        var renderMode = VideoRenderMode.Current;
        bool useHardwareInterop = renderMode.UseHardwareAcceleration;

        _mpvHandle = MpvWrapper.mpv_create();

        MpvWrapper.mpv_set_option_string(_mpvHandle, "wid", "0");
        MpvWrapper.mpv_set_option_string(_mpvHandle, "vo", "libmpv");
        MpvWrapper.mpv_set_option_string(_mpvHandle, "hwdec", useHardwareInterop ? "cuda,dxva2,auto-safe" : "no");
        MpvWrapper.mpv_set_option_string(_mpvHandle, "background", "#FF000000");
        MpvWrapper.mpv_set_option_string(_mpvHandle, "keep-open", "yes");
        MpvWrapper.mpv_set_option_string(_mpvHandle, "idle", "yes");
        MpvWrapper.mpv_set_option_string(_mpvHandle, "ytdl", "no");

        if (RuntimeLog.IsDevMode && RuntimeLog.DevLogDir != null)
        {
            string mpvLogPath = System.IO.Path.Combine(RuntimeLog.DevLogDir, $"mpv_debug_{Environment.ProcessId}_{RuntimeLog.SessionId}.log");
            MpvWrapper.mpv_set_option_string(_mpvHandle, "terminal", "yes");
            MpvWrapper.mpv_set_option_string(_mpvHandle, "msg-level", "all=v");
            MpvWrapper.mpv_set_option_string(_mpvHandle, "log-file", mpvLogPath);
        }
        else
        {
            MpvWrapper.mpv_set_option_string(_mpvHandle, "terminal", "no");
            MpvWrapper.mpv_set_option_string(_mpvHandle, "msg-level", "all=warn");
        }

        MpvWrapper.mpv_set_option_string(_mpvHandle, "force-window", "no");
        MpvWrapper.mpv_set_option_string(_mpvHandle, "osd-bar", "no");

        MpvWrapper.mpv_initialize(_mpvHandle);
        IpcClient = new MpvIpcClient(_mpvHandle);

        if (OperatingSystem.IsWindows())
        {
            if (!useHardwareInterop)
            {
                RuntimeLog.Info(InteropLogStep, $"Hardware video interop disabled ({renderMode.FailureReason}); using CPU software preview.");
                IsSoftwareFallbackActive = !InitializeSoftwareRender();
                return;
            }

            try
            {
                InitializeWGLInteropContext();
                IsSoftwareFallbackActive = false;
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail(InteropLogStep, $"Hardware video interop unavailable ({ex.Message}); falling back to CPU software preview.");
                ReleaseHardwareInteropResources();
                IsSoftwareFallbackActive = !InitializeSoftwareRender();
            }
        }
        else
        {
            throw new PlatformNotSupportedException();
        }
    }

    private bool _swMode;
    private WriteableBitmap? _swBitmap;

    private byte[]? _swRenderBuffer;
    private byte[]? _swPresentBuffer;
    private int _swPresentW, _swPresentH;
    private int _swW, _swH;
    private volatile int _swTargetW;
    private volatile int _swTargetH;
    private System.Threading.Thread? _swThread;
    private readonly object _swBufLock = new object();
    private bool _swLoggedOk, _swPushLogged, _swPushErrLogged, _swPaintLogged;
    private int _swRenderFailLogged;

    private readonly object _swRenderGate = new object();
    private volatile bool _swDisposing;
    private volatile bool _swThreadExited;


    private bool InitializeSoftwareRender()
    {
        try
        {
            if (!_gcHandle.IsAllocated) _gcHandle = GCHandle.Alloc(this);

            nint apiTypePtr = Marshal.StringToHGlobalAnsi("sw");
            var ctxParams = new LibMpvInterop.mpv_render_param[]
            {
                new() { type = LibMpvInterop.MPV_RENDER_PARAM_API_TYPE, data = apiTypePtr },
                new() { type = 0, data = nint.Zero }
            };
            int err;
            try { err = LibMpvInterop.mpv_render_context_create(out _renderContext, _mpvHandle, ctxParams); }
            finally { Marshal.FreeHGlobal(apiTypePtr); }

            if (err < 0 || _renderContext == nint.Zero)
            {
                RuntimeLog.Fail(InteropLogStep, $"Software render context create failed (code {err}).");
                _renderContext = nint.Zero;
                return false;
            }

            RegisterRenderUpdateCallback();
            _swMode = true;
            _renderThreadRunning = true;
            _swThread = new System.Threading.Thread(SoftwareRenderThreadLoop) { IsBackground = true, Name = "MpvSwRenderThread" };
            _swThread.Start();
            UpdateCachedSize();
            try { _renderSignal.Set(); } catch { }
            RuntimeLog.Info(InteropLogStep, "Software preview active (CPU render). WGL interop not required.");
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail(InteropLogStep, $"Software render init failed: {ex.Message}");
            return false;
        }
    }

    private bool _swLoopLogged;

    private void SoftwareRenderThreadLoop()
    {
        RuntimeLog.Info(InteropLogStep, "SW render loop started (thread alive).");
        try
        {
            while (_renderThreadRunning && !_swDisposing)
            {
                try { _renderSignal.WaitOne(66); }
                catch (ObjectDisposedException) { break; }

                if (!_renderThreadRunning || _swDisposing) break;

                try { SoftwareRenderOnce(); }
                catch (Exception ex) { if (!_swLoopLogged) { _swLoopLogged = true; RuntimeLog.Fail(InteropLogStep, $"SW render loop exception: {ex.Message}"); } }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail(InteropLogStep, $"SW render loop terminated unexpectedly: {ex.Message}");
        }
        finally
        {
            _swThreadExited = true;
            RuntimeLog.Info(InteropLogStep, "SW render loop exited.");
        }
    }

    private void SoftwareRenderOnce()
    {
        Interlocked.Exchange(ref _isUpdateQueued, 0);

        if (_swDisposing || _renderContext == nint.Zero) return;
        int w = _swTargetW;
        int h = _swTargetH;
        if (w < 8 || h < 8) return;

        int stride = w * 4;
        int size = stride * h;

        lock (_swRenderGate)
        {
            if (_swDisposing || _renderContext == nint.Zero) return;

            if (_swRenderBuffer == null || _swRenderBuffer.Length != size) _swRenderBuffer = new byte[size];
            byte[] target = _swRenderBuffer;

            int[] sz = { w, h };
            nint fmtPtr = Marshal.StringToHGlobalAnsi("bgr0");
            nint szPtr = Marshal.AllocHGlobal(sizeof(int) * 2);
            nint stridePtr = Marshal.AllocHGlobal(nint.Size);
            var pin = GCHandle.Alloc(target, GCHandleType.Pinned);
            try
            {
                Marshal.Copy(sz, 0, szPtr, 2);
                Marshal.WriteIntPtr(stridePtr, (nint)stride);
                var pars = new LibMpvInterop.mpv_render_param[]
                {
                    new() { type = LibMpvInterop.MPV_RENDER_PARAM_SW_SIZE, data = szPtr },
                    new() { type = LibMpvInterop.MPV_RENDER_PARAM_SW_FORMAT, data = fmtPtr },
                    new() { type = LibMpvInterop.MPV_RENDER_PARAM_SW_STRIDE, data = stridePtr },
                    new() { type = LibMpvInterop.MPV_RENDER_PARAM_SW_POINTER, data = pin.AddrOfPinnedObject() },
                    new() { type = 0, data = nint.Zero }
                };
                int err = LibMpvInterop.mpv_render_context_render(_renderContext, pars);
                if (err < 0)
                {
                    if (_swRenderFailLogged < 8) { _swRenderFailLogged++; RuntimeLog.Fail(InteropLogStep, $"SW render attempt #{_swRenderFailLogged}: {w}x{h} -> error {err}."); }
                    return;
                }
                if (!_swLoggedOk) { _swLoggedOk = true; RuntimeLog.Info(InteropLogStep, $"First software frame rendered ({w}x{h}) after {_swRenderFailLogged} failed attempt(s)."); }
                for (int i = 3; i < size; i += 4) target[i] = 255;
            }
            finally
            {
                pin.Free();
                Marshal.FreeHGlobal(fmtPtr);
                Marshal.FreeHGlobal(szPtr);
                Marshal.FreeHGlobal(stridePtr);
            }

            lock (_swBufLock)
            {
                byte[]? previous = _swPresentBuffer;
                _swPresentBuffer = target;
                _swPresentW = w;
                _swPresentH = h;
                _swRenderBuffer = (previous != null && previous.Length == size) ? previous : null;
            }
        }

        if (_swDisposing) return;
        Dispatcher.UIThread.Post(() => PushSoftwareFrame(w, h));
    }

    private void PushSoftwareFrame(int w, int h)
    {
        try
        {
            if (w <= 1 || h <= 1) return;
            if (_swDisposing || !_swMode) return;
            if (_swBitmap == null || _swW != w || _swH != h)
            {
                _swBitmap?.Dispose();
                _swBitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                _swW = w; _swH = h;
            }
            using (var fb = _swBitmap.Lock())
            {
                lock (_swBufLock)
                {
                    if (_swPresentBuffer == null || _swPresentW != w || _swPresentH != h) return;
                    Marshal.Copy(_swPresentBuffer, 0, fb.Address, Math.Min(_swPresentBuffer.Length, fb.RowBytes * h));
                }
            }
            if (!_swPushLogged) { _swPushLogged = true; RuntimeLog.Info(InteropLogStep, $"First frame pushed to bitmap ({w}x{h}); invalidating visual."); }
            InvalidateVisual();
        }
        catch (Exception ex) { if (!_swPushErrLogged) { _swPushErrLogged = true; RuntimeLog.Fail(InteropLogStep, $"PushSoftwareFrame failed: {ex.Message}"); } }
    }

    public override void Render(DrawingContext context)
    {
        if (_swMode && _swBitmap != null)
        {
            var b = Bounds;
            double sw = _swBitmap.PixelSize.Width, sh = _swBitmap.PixelSize.Height;
            if (sw > 0 && sh > 0 && b.Width > 0 && b.Height > 0)
            {
                if (!_swPaintLogged) { _swPaintLogged = true; RuntimeLog.Info(InteropLogStep, $"First software paint to screen (control {b.Width:0}x{b.Height:0}, bmp {sw:0}x{sh:0})."); }
                context.DrawImage(_swBitmap, new Rect(0, 0, sw, sh), new Rect(0, 0, b.Width, b.Height));
            }
            return;
        }
        base.Render(context);
    }

    private void ReleaseHardwareInteropResources()
    {
        _renderThreadRunning = false;
        try { _renderSignal.Set(); } catch { }

        lock (_renderLock)
        {
            ReleaseRenderTexture();

            if (_renderContext != nint.Zero)
            {
                try
                {
                    if (_dummyHdc != nint.Zero && _hglrc != nint.Zero)
                    {
                        WglInterop.wglMakeCurrent(_dummyHdc, _hglrc);
                    }

                    LibMpvInterop.mpv_render_context_free(_renderContext);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail(InteropLogStep, ex);
                }
                finally
                {
                    _renderContext = nint.Zero;
                }
            }

            if ((_glFramebuffers[0] != 0 || _glFramebuffers[1] != 0) && _dummyHdc != nint.Zero && _hglrc != nint.Zero)
            {
                try
                {
                    WglInterop.wglMakeCurrent(_dummyHdc, _hglrc);
                    WglInterop.glDeleteFramebuffers?.Invoke(SwapChainSize, _glFramebuffers);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail(InteropLogStep, ex);
                }
                finally
                {
                    Array.Clear(_glFramebuffers, 0, SwapChainSize);
                }
            }

            if ((_glTextures[0] != 0 || _glTextures[1] != 0) && _dummyHdc != nint.Zero && _hglrc != nint.Zero)
            {
                try
                {
                    WglInterop.wglMakeCurrent(_dummyHdc, _hglrc);
                    WglInterop.glDeleteTextures(SwapChainSize, _glTextures);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail(InteropLogStep, ex);
                }
                finally
                {
                    Array.Clear(_glTextures, 0, SwapChainSize);
                }
            }

            if (_dxInteropDevice != nint.Zero && WglInterop.wglDXCloseDeviceNV != null)
            {
                WglInterop.wglDXCloseDeviceNV(_dxInteropDevice);
                _dxInteropDevice = nint.Zero;
            }

            if (_hglrc != nint.Zero)
            {
                WglInterop.wglMakeCurrent(nint.Zero, nint.Zero);
                WglInterop.wglDeleteContext(_hglrc);
                _hglrc = nint.Zero;
            }

            if (_dummyHdc != nint.Zero && _dummyHwnd != nint.Zero)
            {
                WglInterop.ReleaseDC(_dummyHwnd, _dummyHdc);
                _dummyHdc = nint.Zero;
            }

            if (_dummyHwnd != nint.Zero)
            {
                WglInterop.DestroyWindow(_dummyHwnd);
                _dummyHwnd = nint.Zero;
            }

            if (_openglLibrary != nint.Zero)
            {
                NativeLibrary.Free(_openglLibrary);
                _openglLibrary = nint.Zero;
            }

            _d3d11Context?.Dispose();
            _d3d11Context = null;

            _d3d11Device?.Dispose();
            _d3d11Device = null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe nint NativeGetProcAddress(nint ctx, byte* namePtr)
    {
        if (ctx == nint.Zero || namePtr == null) return nint.Zero;
        var handle = GCHandle.FromIntPtr(ctx);
        if (handle.Target is MpvVideoView view)
        {
            string name = Marshal.PtrToStringUTF8((nint)namePtr) ?? string.Empty;
            return view.GetProcAddressForMpvInternal(name);
        }
        return nint.Zero;
    }

    private nint GetProcAddressForMpvInternal(string name)
    {
        nint ptr = WglInterop.wglGetProcAddress(name);
        if (ptr == nint.Zero || ptr == (nint)1 || ptr == (nint)2 || ptr == (nint)3 || ptr == (nint)(-1))
        {
            NativeLibrary.TryGetExport(_openglLibrary, name, out ptr);
        }
        return ptr;
    }

    private void InitializeWGLInteropContext()
    {
        _openglLibrary = NativeLibrary.Load("opengl32.dll");

        _dummyHwnd = WglInterop.CreateWindowEx(
            0, "STATIC", "dummy", 0,
            0, 0, 1, 1, nint.Zero, nint.Zero, nint.Zero, nint.Zero);
        
        _dummyHdc = WglInterop.GetDC(_dummyHwnd);

        var pfd = new WglInterop.PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)Marshal.SizeOf<WglInterop.PIXELFORMATDESCRIPTOR>(),
            nVersion = 1,
            dwFlags = WglInterop.PFD_DRAW_TO_WINDOW | WglInterop.PFD_SUPPORT_OPENGL | WglInterop.PFD_DOUBLEBUFFER,
            iPixelType = WglInterop.PFD_TYPE_RGBA,
            cColorBits = 32,
            cDepthBits = 24,
            cStencilBits = 8,
            iLayerType = 0
        };

        int format = WglInterop.ChoosePixelFormat(_dummyHdc, ref pfd);
        WglInterop.SetPixelFormat(_dummyHdc, format, ref pfd);

        _hglrc = WglInterop.wglCreateContext(_dummyHdc);
        WglInterop.wglMakeCurrent(_dummyHdc, _hglrc);
        
        WglInterop.LoadExtensions();

        var creationFlags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        _d3d11Device = D3D11.D3D11CreateDevice(
            DriverType.Hardware,
            creationFlags,
            new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_0 });
        _d3d11Context = _d3d11Device.ImmediateContext;

        if (WglInterop.wglDXOpenDeviceNV == null)
            throw new Exception("WGL_NV_DX_interop not supported by the OpenGL driver.");

        _dxInteropDevice = WglInterop.wglDXOpenDeviceNV(_d3d11Device.NativePointer);

        if (!_gcHandle.IsAllocated)
        {
            _gcHandle = GCHandle.Alloc(this);
        }

        unsafe
        {
            var initParams = new LibMpvInterop.mpv_opengl_init_params
            {
                get_proc_address = (nint)(delegate* unmanaged[Cdecl]<nint, byte*, nint>)&NativeGetProcAddress,
                get_proc_address_ctx = GCHandle.ToIntPtr(_gcHandle)
            };

        nint initParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<LibMpvInterop.mpv_opengl_init_params>());
        Marshal.StructureToPtr(initParams, initParamsPtr, false);

        nint apiTypePtr = Marshal.StringToHGlobalAnsi(MpvApiTypeOpenGL);
        var ctxParams = new LibMpvInterop.mpv_render_param[]
        {
            new() { type = LibMpvInterop.MPV_RENDER_PARAM_API_TYPE, data = apiTypePtr },
            new() { type = LibMpvInterop.MPV_RENDER_PARAM_OPENGL_INIT_PARAMS, data = initParamsPtr },
            new() { type = 0, data = nint.Zero }
        };

        try
        {
            int err = LibMpvInterop.mpv_render_context_create(out _renderContext, _mpvHandle, ctxParams);
            if (err < 0 || _renderContext == nint.Zero)
            {
                throw new InvalidOperationException($"mpv_render_context_create (opengl) failed with error code {err}.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(apiTypePtr);
            Marshal.FreeHGlobal(initParamsPtr);
        }
        }


        WglInterop.glGenTextures(SwapChainSize, _glTextures);

        RegisterRenderUpdateCallback();

        _renderThreadRunning = true;
        _renderThread = new System.Threading.Thread(RenderThreadLoop)
        {
            IsBackground = true,
            Name = "MpvRenderThread"
        };
        _renderThread.Start();

        WglInterop.wglMakeCurrent(nint.Zero, nint.Zero);
    }

    public Task StartMpvProcessAsync(string mpvPath)
    {
        InitializeMpv(mpvPath);
        return Task.CompletedTask;
    }

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        var elementVisual = ElementComposition.GetElementVisual(this);
        if (elementVisual == null) return;

        var compositor = elementVisual.Compositor;

        _surfaceVisual = compositor.CreateSurfaceVisual();
        _drawingSurface = compositor.CreateDrawingSurface();
        _surfaceVisual.Surface = _drawingSurface;

        ElementComposition.SetElementChildVisual(this, _surfaceVisual);

        UpdateCachedSize();

        if (_gpuInterop == null)
        {
            try
            {
                _gpuInterop = await compositor.TryGetCompositionGpuInterop();
            }
            catch { }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ElementComposition.SetElementChildVisual(this, null);
        _drawingSurface = null;
        _surfaceVisual = null;
        _gpuInterop = null;
        
        for (int i = 0; i < SwapChainSize; i++)
        {
            if (_importedImages[i] != null)
            {
                if (_importedImages[i] is IAsyncDisposable ad) _ = ad.DisposeAsync();
                else if (_importedImages[i] is IDisposable d) d.Dispose();
                _importedImages[i] = null;
            }
        }
    }

    protected override Avalonia.Size ArrangeOverride(Avalonia.Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        UpdateCachedSize();
        return size;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateCachedSize();
    }

    private void UpdateCachedSize()
    {
        if (VisualRoot != null)
        {
            double scale = VisualRoot.RenderScaling;
            _cachedWidth = Math.Max(1, (int)(Bounds.Width * scale));
            _cachedHeight = Math.Max(1, (int)(Bounds.Height * scale));
        }
        else
        {
            _cachedWidth = Math.Max(1, (int)Bounds.Width);
            _cachedHeight = Math.Max(1, (int)Bounds.Height);
        }

        if (_surfaceVisual != null)
        {
            _surfaceVisual.Size = new Avalonia.Vector(Bounds.Width, Bounds.Height);
        }

        if (_swMode)
        {
            int pw = _cachedWidth, ph = _cachedHeight;
            const double cap = 1600.0;
            if (pw > cap && pw > 0) { double f = cap / pw; pw = (int)(pw * f); ph = (int)(ph * f); }
            pw = Math.Max(2, pw & ~1);
            ph = Math.Max(2, ph & ~1);
            if (pw != _swTargetW || ph != _swTargetH)
            {
                _swTargetW = pw; _swTargetH = ph;
                RuntimeLog.Info(InteropLogStep, $"SW target size -> {pw}x{ph} (control bounds {Bounds.Width:0}x{Bounds.Height:0}).");
                if (_renderThreadRunning) { try { _renderSignal.Set(); } catch { } }
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void NativeRenderUpdateCb(nint ctx)
    {
        if (ctx == nint.Zero) return;
        var handle = GCHandle.FromIntPtr(ctx);
        if (handle.Target is MpvVideoView view)
        {
            view.OnRenderUpdate();
        }
    }

    private void OnRenderUpdate()
    {
        try
        {
            if (_renderContext == nint.Zero) return;
            ulong flags = LibMpvInterop.mpv_render_context_update(_renderContext);
            if ((flags & LibMpvInterop.MPV_RENDER_UPDATE_FRAME) != 0)
            {
                if (Interlocked.Exchange(ref _isUpdateQueued, 1) == 0)
                {
                    if (_renderThreadRunning) _renderSignal.Set();
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// The one and only thread that ever calls <see cref="UpdateSurface"/> (and therefore
    /// mpv_render_context_render). AutoResetEvent coalesces bursts, mirroring the old
    /// _isUpdateQueued gate. UpdateSurface releases the GL context each pass, so Dispose can
    /// still take _renderLock and free GL resources without fighting this thread for the context.
    /// </summary>
    private void RenderThreadLoop()
    {
        try
        {
            while (_renderThreadRunning && !_disposing)
            {
                try { _renderSignal.WaitOne(_retryPending ? 66 : System.Threading.Timeout.Infinite); }
                catch (ObjectDisposedException) { break; }

                if (!_renderThreadRunning || _disposing) break;
                try { UpdateSurface(); } catch { }
            }
        }
        catch (Exception ex)
        {
            try { RuntimeLog.Fail(InteropLogStep, $"GPU render loop terminated unexpectedly: {ex.Message}"); } catch { }
        }
        finally
        {
            _renderThreadExited = true;
        }
    }

    private unsafe void RegisterRenderUpdateCallback()
    {
        if (_renderContext == nint.Zero) return;

        LibMpvInterop.mpv_render_context_set_update_callback(_renderContext, &NativeRenderUpdateCb, GCHandle.ToIntPtr(_gcHandle));
    }

    private void UpdateSurface()
    {
        lock (_renderLock)
        {
            Interlocked.Exchange(ref _isUpdateQueued, 0);
            bool glContextCurrent = false;
            try
            {
                if (_renderContext == nint.Zero) return;

                var compositor = _surfaceVisual?.Compositor;
                var gpu = _gpuInterop;

                int width = _cachedWidth;
                int height = _cachedHeight;

                if (_drawingSurface == null || _d3d11Device == null || _d3d11Context == null || gpu == null || width <= 1 || height <= 1)
                {
                    PumpEmptyRender();
                    return;
                }

                EnsureRenderTexture(width, height);

                _currentBufferIndex = (_currentBufferIndex + 1) % SwapChainSize;

                if (_sharedTextures[_currentBufferIndex] == null || _dxInteropObjects[_currentBufferIndex] == nint.Zero)
                {
                    PumpEmptyRender();
                    return;
                }

                bool keyedMutexAcquired = false;
                bool dxObjectLocked = false;
                bool frameReady = false;
                ICompositionImportedGpuImage? imageForAvalonia = null;
                CompositionDrawingSurface? surfaceForAvalonia = null;

                var keyedMutex = _sharedTextureMutexes[_currentBufferIndex];

                try
                {
                    if (keyedMutex != null)
                    {
                        unsafe
                        {
                            void** vtbl = *(void***)keyedMutex.NativePointer;
                            delegate* unmanaged[Stdcall]<nint, ulong, int, int> acquireSync = (delegate* unmanaged[Stdcall]<nint, ulong, int, int>)vtbl[8];
                            int hresult = acquireSync(keyedMutex.NativePointer, ProducerKey, KeyedMutexWaitMs);
                            if (hresult != 0)
                            {
                                PumpEmptyRender();
                                return;
                            }
                        }
                        keyedMutexAcquired = true;
                    }

                    WglInterop.wglMakeCurrent(_dummyHdc, _hglrc);
                    glContextCurrent = true;

                    _lockedInteropObjects[0] = _dxInteropObjects[_currentBufferIndex];

                    if (WglInterop.wglDXLockObjectsNV!(_dxInteropDevice, 1, _lockedInteropObjects))
                    {
                        dxObjectLocked = true;
                        WglInterop.glBindFramebuffer!(WglInterop.GL_FRAMEBUFFER, _glFramebuffers[_currentBufferIndex]);
                        WglInterop.glFramebufferTexture2D!(WglInterop.GL_FRAMEBUFFER, WglInterop.GL_COLOR_ATTACHMENT0, WglInterop.GL_TEXTURE_2D, _glTextures[_currentBufferIndex], 0);

                        unsafe
                        {
                            WglInterop.glClearColor(1.0f, 0.0f, 0.0f, 1.0f);
                            WglInterop.glClear(WglInterop.GL_COLOR_BUFFER_BIT);

                            LibMpvInterop.mpv_opengl_fbo fbo = new LibMpvInterop.mpv_opengl_fbo
                            {
                                fbo = (int)_glFramebuffers[_currentBufferIndex],
                                w = _renderTextureW,
                                h = _renderTextureH,
                                internal_format = (int)WglInterop.GL_RGBA8
                            };

                            int flipY = 0;

                            LibMpvInterop.mpv_render_param* paramsArray = stackalloc LibMpvInterop.mpv_render_param[3];

                            paramsArray[0].type = LibMpvInterop.MPV_RENDER_PARAM_OPENGL_FBO;
                            paramsArray[0].data = (nint)(&fbo);

                            paramsArray[1].type = LibMpvInterop.MPV_RENDER_PARAM_FLIP_Y;
                            paramsArray[1].data = (nint)(&flipY);

                            paramsArray[2].type = 0;
                            paramsArray[2].data = nint.Zero;

                            LibMpvInterop.mpv_render_context_render(_renderContext, (nint)paramsArray);
                        }

                        WglInterop.glFlush();
                        WglInterop.wglDXUnlockObjectsNV?.Invoke(_dxInteropDevice, 1, _lockedInteropObjects);
                        dxObjectLocked = false;

                        _d3d11Context?.Flush();

                        imageForAvalonia = EnsureImportedImage(_currentBufferIndex);
                        surfaceForAvalonia = _drawingSurface;
                        frameReady = imageForAvalonia != null && surfaceForAvalonia != null;
                    }
                    else
                    {
                        RuntimeLog.Fail(InteropLogStep, $"wglDXLockObjectsNV failed for buffer {_currentBufferIndex}.");
                    }
                }
                finally
                {
                    if (dxObjectLocked)
                    {
                        WglInterop.wglDXUnlockObjectsNV?.Invoke(_dxInteropDevice, 1, _lockedInteropObjects);
                    }

                    if (glContextCurrent)
                    {
                        WglInterop.wglMakeCurrent(nint.Zero, nint.Zero);
                        glContextCurrent = false;
                    }

                    if (keyedMutexAcquired)
                    {
                        keyedMutex!.ReleaseSync(frameReady ? ConsumerKey : ProducerKey);
                    }
                }

                if (frameReady && imageForAvalonia != null && surfaceForAvalonia != null)
                {
                    _retryPending = false;
                    ImportAndPresentTexture(_currentBufferIndex, surfaceForAvalonia, imageForAvalonia);
                }
                else
                {
                    PumpEmptyRender();
                }
            }
            catch (Exception ex)
            {
                if (glContextCurrent)
                {
                    WglInterop.wglMakeCurrent(nint.Zero, nint.Zero);
                }

                RuntimeLog.Fail(InteropLogStep, ex);
            }
        }
    }

    private bool _pumpFailLogged;

    /// <summary>
    /// ISSUE_05 — set whenever a frame had to be declined, cleared as soon as one is presented.
    /// Drives the render loop's short retry wait so the ONLY behavioural change on a healthy
    /// GPU machine is: none. When frames are flowing this is always false and the loop blocks
    /// indefinitely exactly as it always has.
    /// </summary>
    private volatile bool _retryPending;

    /// <summary>
    /// ISSUE_05 — this method used to have a COMPLETELY EMPTY BODY while its name promised the
    /// opposite, and every "cannot draw this frame" branch in <see cref="UpdateSurface"/> called
    /// it and returned.
    ///
    /// WHY THAT FROZE THE PREVIEW: with <c>vo=libmpv</c> mpv hands the client one frame and then
    /// WAITS — it only decodes and announces the next frame once the current one has been
    /// consumed by a call to <c>mpv_render_context_render</c>. So declining to render (surface
    /// not created yet, control not laid out, or the 1-second keyed-mutex acquire timing out
    /// under load) meant: no render -&gt; no new frame -&gt; no new frame-ready callback -&gt; the
    /// picture stays frozen on one image for the rest of the session while the audio plays on.
    /// Restarting the app was the only way out.
    ///
    /// THE FIX: consume the frame with <c>MPV_RENDER_PARAM_SKIP_RENDERING=1</c>. mpv does all the
    /// timing/frame-advance bookkeeping and skips only the draw, so the clock keeps moving and
    /// the very next frame is offered normally. It does not touch the graphics API, so this is
    /// safe to call with no GL context current — which matters, because these branches are
    /// reached precisely when the GL/D3D side is not usable.
    ///
    /// FAIL-SAFE: any failure is swallowed. The bounded 66ms wait in <see cref="RenderThreadLoop"/>
    /// is the second, independent line of defence — even if this pump never works on some driver,
    /// the loop still retries ~15x/sec, so the preview recovers either way.
    /// </summary>
    private void PumpEmptyRender()
    {
        _retryPending = true;

        if (_renderContext == nint.Zero || _disposing) return;

        try
        {
            int skip = 1;
            unsafe
            {
                LibMpvInterop.mpv_render_param* pars = stackalloc LibMpvInterop.mpv_render_param[2];
                pars[0].type = LibMpvInterop.MPV_RENDER_PARAM_SKIP_RENDERING;
                pars[0].data = (nint)(&skip);
                pars[1].type = 0;
                pars[1].data = nint.Zero;

                LibMpvInterop.mpv_render_context_render(_renderContext, (nint)pars);
            }
        }
        catch (Exception ex)
        {
            if (!_pumpFailLogged)
            {
                _pumpFailLogged = true;
                RuntimeLog.Fail(InteropLogStep,
                    $"Skip-render pump unavailable ({ex.Message}); relying on the timed render-loop retry instead.");
            }
        }
    }

    private void EnsureRenderTexture(int width, int height)
    {
        if (_sharedTextures[0] != null && _renderTextureW == width && _renderTextureH == height)
            return;

        if (_gpuInterop != null)
        {
            var types = string.Join(", ", _gpuInterop.SupportedImageHandleTypes);
            RuntimeLog.Info(InteropLogStep, "Supported image handle types: " + types);
        }

        ReleaseRenderTexture();

        var sharedDesc = new Texture2DDescription(
            format: Format.B8G8R8A8_UNorm,
            width: (uint)width,
            height: (uint)height,
            arraySize: 1,
            mipLevels: 1,
            bindFlags: BindFlags.RenderTarget | BindFlags.ShaderResource,
            usage: ResourceUsage.Default,
            cpuAccessFlags: CpuAccessFlags.None,
            sampleCount: 1,
            sampleQuality: 0,
            miscFlags: ResourceOptionFlags.SharedKeyedMutex);

        WglInterop.wglMakeCurrent(_dummyHdc, _hglrc);
        try
        {
            for (int i = 0; i < SwapChainSize; i++)
            {
                _sharedTextures[i] = _d3d11Device!.CreateTexture2D(sharedDesc);
                _renderTexturePtrs[i] = _sharedTextures[i]!.NativePointer;
                _sharedTextureMutexes[i] = _sharedTextures[i]!.QueryInterface<IDXGIKeyedMutex>();

                _dxInteropObjects[i] = WglInterop.wglDXRegisterObjectNV!(
                    _dxInteropDevice,
                    _renderTexturePtrs[i],
                    _glTextures[i],
                    WglInterop.GL_TEXTURE_2D,
                    WglInterop.WGL_ACCESS_READ_WRITE_NV);

                if (_dxInteropObjects[i] == nint.Zero)
                {
                    RuntimeLog.Fail(InteropLogStep, $"wglDXRegisterObjectNV failed for buffer {i}.");
                }

                _d3d11Context?.Flush();

                WglInterop.glBindTexture(WglInterop.GL_TEXTURE_2D, _glTextures[i]);
                WglInterop.glTexParameteri(WglInterop.GL_TEXTURE_2D, WglInterop.GL_TEXTURE_MIN_FILTER, WglInterop.GL_LINEAR);
                WglInterop.glTexParameteri(WglInterop.GL_TEXTURE_2D, WglInterop.GL_TEXTURE_MAG_FILTER, WglInterop.GL_LINEAR);
                WglInterop.glBindTexture(WglInterop.GL_TEXTURE_2D, 0);

                uint[] fbo = new uint[1];
                WglInterop.glGenFramebuffers!(1, fbo);
                _glFramebuffers[i] = fbo[0];
                WglInterop.glBindFramebuffer!(WglInterop.GL_FRAMEBUFFER, _glFramebuffers[i]);
                WglInterop.glFramebufferTexture2D!(WglInterop.GL_FRAMEBUFFER, WglInterop.GL_COLOR_ATTACHMENT0, WglInterop.GL_TEXTURE_2D, _glTextures[i], 0);

                using var dxgiResource = _sharedTextures[i]!.QueryInterface<IDXGIResource>();
                _sharedTextureHandles[i] = dxgiResource.SharedHandle;
            }

            _renderTextureW = width;
            _renderTextureH = height;
        }
        finally
        {
            WglInterop.wglMakeCurrent(nint.Zero, nint.Zero);
        }
    }


    private ICompositionImportedGpuImage? EnsureImportedImage(int index)
    {
        if (_importedImages[index] != null && _importedImages[index]!.IsLost)
        {
            var image = _importedImages[index];
            Avalonia.Threading.Dispatcher.UIThread.Post(() => 
            {
                if (image is IAsyncDisposable ad) _ = ad.DisposeAsync();
                else if (image is IDisposable d) d.Dispose();
            });
            _importedImages[index] = null;
        }

        if (_importedImages[index] == null)
        {
            _importedImages[index] = TryImportSharedTexture(index);
        }

        return _importedImages[index];
    }

    private void ImportAndPresentTexture(int index, CompositionDrawingSurface surface, ICompositionImportedGpuImage image)
    {
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try
                {
                    await surface.UpdateWithKeyedMutexAsync(image, (uint)ConsumerKey, (uint)ProducerKey);
                }
                catch (Avalonia.Platform.PlatformGraphicsContextLostException)
                {
                    if (_importedImages[index] is IAsyncDisposable ad) _ = ad.DisposeAsync();
                    else if (_importedImages[index] is IDisposable d) d.Dispose();
                    _importedImages[index] = null;
                }
                catch (Exception ex)
                {
                    if (_importedImages[index] is IAsyncDisposable ad) _ = ad.DisposeAsync();
                    else if (_importedImages[index] is IDisposable d) d.Dispose();
                    _importedImages[index] = null;
                    
                    if (ex is System.Runtime.InteropServices.COMException comEx && (uint)comEx.ErrorCode == 0x80070057)
                    {
                    }
                    else
                    {
                        RuntimeLog.Fail(InteropLogStep, ex);
                    }
                }
            }, Avalonia.Threading.DispatcherPriority.Render);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail(InteropLogStep, ex);
        }
    }


    private ICompositionImportedGpuImage? TryImportSharedTexture(int index)
    {
        var gpu = _gpuInterop;
        var texture = _sharedTextures[index];
        if (gpu == null || texture == null || _renderTextureW <= 0 || _renderTextureH <= 0)
        {
            return null;
        }

        const string handleType = KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle;
        if (!SupportsImageHandleType(gpu, handleType))
        {
            RuntimeLog.Fail(InteropLogStep, $"Avalonia compositor does not support {handleType}.");
            return null;
        }

        nint sharedHandle = _sharedTextureHandles[index];
        if (sharedHandle == nint.Zero)
        {
            using var dxgiResource = texture.QueryInterface<IDXGIResource>();
            sharedHandle = dxgiResource.SharedHandle;
            _sharedTextureHandles[index] = sharedHandle;
        }

        if (sharedHandle == nint.Zero)
        {
            RuntimeLog.Fail(InteropLogStep, $"IDXGIResource.GetSharedHandle returned null for buffer {index}.");
            return null;
        }

        var platformHandle = new PlatformHandle(sharedHandle, handleType);
        var props = new PlatformGraphicsExternalImageProperties
        {
            Width = _renderTextureW,
            Height = _renderTextureH,
            Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
            TopLeftOrigin = true
        };

        return Dispatcher.UIThread.CheckAccess()
            ? gpu.ImportImage(platformHandle, props)
            : Dispatcher.UIThread.Invoke(() => gpu.ImportImage(platformHandle, props));
    }

    private static bool SupportsImageHandleType(ICompositionGpuInterop gpu, string handleType)
    {
        foreach (string supported in gpu.SupportedImageHandleTypes)
        {
            if (string.Equals(supported, handleType, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void ReleaseRenderTexture()
    {
        for (int i = 0; i < SwapChainSize; i++)
        {
            if (_dxInteropObjects[i] != nint.Zero)
            {
                WglInterop.wglMakeCurrent(_dummyHdc, _hglrc);
                WglInterop.wglDXUnregisterObjectNV!(_dxInteropDevice, _dxInteropObjects[i]);
                _dxInteropObjects[i] = nint.Zero;
            }

            if (_sharedTextures[i] != null)
            {
                _sharedTextureMutexes[i]?.Dispose();
                _sharedTextureMutexes[i] = null;

                _sharedTextures[i]!.Dispose();
                _sharedTextures[i] = null;
            }
            else if (_sharedTextureMutexes[i] != null)
            {
                _sharedTextureMutexes[i]!.Dispose();
                _sharedTextureMutexes[i] = null;
            }

            if (_glFramebuffers[i] != 0)
            {
                WglInterop.wglMakeCurrent(_dummyHdc, _hglrc);
                uint[] framebuffer = { _glFramebuffers[i] };
                WglInterop.glDeleteFramebuffers?.Invoke(1, framebuffer);
                _glFramebuffers[i] = 0;
            }

            _renderTexturePtrs[i] = nint.Zero;
            _sharedTextureHandles[i] = nint.Zero;
            if (_importedImages[i] != null)
            {
                var image = _importedImages[i];
                Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                {
                    if (image is IAsyncDisposable ad) _ = ad.DisposeAsync();
                    else if (image is IDisposable d) d.Dispose();
                });
                _importedImages[i] = null;
            }
        }


    }

    ~MpvVideoView()
    {
        Dispose(false);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private bool _isDisposed;

    private void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (!disposing)
        {
            try
            {
                if (_dxInteropDevice != nint.Zero && WglInterop.wglDXCloseDeviceNV != null)
                {
                    WglInterop.wglDXCloseDeviceNV(_dxInteropDevice);
                    _dxInteropDevice = nint.Zero;
                }
                if (_hglrc != nint.Zero)
                {
                    WglInterop.wglDeleteContext(_hglrc);
                    _hglrc = nint.Zero;
                }
                if (_dummyHdc != nint.Zero && _dummyHwnd != nint.Zero)
                {
                    WglInterop.ReleaseDC(_dummyHwnd, _dummyHdc);
                    _dummyHdc = nint.Zero;
                }
                if (_dummyHwnd != nint.Zero)
                {
                    WglInterop.DestroyWindow(_dummyHwnd);
                    _dummyHwnd = nint.Zero;
                }
                if (_mpvHandle != nint.Zero)
                {
                    MpvWrapper.mpv_terminate_destroy(_mpvHandle);
                    _mpvHandle = nint.Zero;
                }
                if (_openglLibrary != nint.Zero)
                {
                    NativeLibrary.Free(_openglLibrary);
                    _openglLibrary = nint.Zero;
                }
                if (_gcHandle.IsAllocated)
                {
                    _gcHandle.Free();
                }
            }
            catch { }
            return;
        }

        _swDisposing = true;
        _disposing = true;
        _renderThreadRunning = false;
        try { _renderSignal.Set(); } catch { }

        bool swThreadStopped = true;
        if (_swThread != null)
        {
            try { swThreadStopped = _swThread.Join(TimeSpan.FromSeconds(3)); } catch { swThreadStopped = false; }
            if (!swThreadStopped)
            {
                RuntimeLog.Fail(InteropLogStep, "SW render thread did not stop within 3s.");
            }
            _swThread = null;
        }

        bool renderGateAcquired = false;
        try
        {
            renderGateAcquired = System.Threading.Monitor.TryEnter(_swRenderGate, TimeSpan.FromSeconds(2));
        }
        catch { renderGateAcquired = false; }
        finally
        {
            if (renderGateAcquired) System.Threading.Monitor.Exit(_swRenderGate);
        }

        bool gpuThreadStopped = true;
        if (_renderThread != null)
        {
            try { gpuThreadStopped = _renderThread.Join(TimeSpan.FromSeconds(3)); }
            catch { gpuThreadStopped = false; }
            if (!gpuThreadStopped)
            {
                RuntimeLog.Fail(InteropLogStep, "GPU render thread did not stop within 3s.");
            }
            _renderThread = null;
        }
        bool gpuThreadAccountedFor = gpuThreadStopped || _renderThreadExited;

        bool swPathOwnsContext = _swMode;
        _swMode = false;
        try { _swBitmap?.Dispose(); } catch { }
        _swBitmap = null;
        _swRenderBuffer = null;
        _swPresentBuffer = null;

        bool swThreadAccountedFor = swThreadStopped || _swThreadExited;
        bool skipRenderContextFree = swPathOwnsContext && !(swThreadAccountedFor && renderGateAcquired);

        if (skipRenderContextFree)
        {
            RuntimeLog.Fail(InteropLogStep,
                $"SW teardown could not be confirmed (threadStopped={swThreadStopped}, threadExited={_swThreadExited}, gateFree={renderGateAcquired}) — abandoning the render context instead of freeing it.");
        }

        if (!gpuThreadAccountedFor)
        {
            skipRenderContextFree = true;
            RuntimeLog.Fail(InteropLogStep,
                "GPU render thread is unaccounted for — abandoning the render context, the mpv handle and the interop GCHandle instead of freeing them.");
        }

        lock (_renderLock)
        {
            if (skipRenderContextFree)
            {
                _renderContext = nint.Zero;
            }

            if (gpuThreadAccountedFor)
            {
                ReleaseRenderTexture();
            }

            if (_renderContext != nint.Zero)
            {
                if (_hglrc != nint.Zero) WglInterop.wglMakeCurrent(_dummyHdc, _hglrc);
                LibMpvInterop.mpv_render_context_free(_renderContext);
                _renderContext = nint.Zero;
            }

            if (gpuThreadAccountedFor)
            {
                if (_glFramebuffers[0] != 0)
                {
                    WglInterop.wglMakeCurrent(_dummyHdc, _hglrc);
                    WglInterop.glDeleteFramebuffers!(SwapChainSize, _glFramebuffers);
                    Array.Clear(_glFramebuffers, 0, SwapChainSize);
                }

                if (_glTextures[0] != 0 || _glTextures[1] != 0)
                {
                    WglInterop.wglMakeCurrent(_dummyHdc, _hglrc);
                    WglInterop.glDeleteTextures(SwapChainSize, _glTextures);
                    Array.Clear(_glTextures, 0, SwapChainSize);
                }

                if (_dxInteropDevice != nint.Zero)
                {
                    WglInterop.wglDXCloseDeviceNV!(_dxInteropDevice);
                    _dxInteropDevice = nint.Zero;
                }

                if (_hglrc != nint.Zero)
                {
                    WglInterop.wglMakeCurrent(nint.Zero, nint.Zero);
                    WglInterop.wglDeleteContext(_hglrc);
                    _hglrc = nint.Zero;
                }

                if (_dummyHdc != nint.Zero && _dummyHwnd != nint.Zero)
                {
                    WglInterop.ReleaseDC(_dummyHwnd, _dummyHdc);
                    _dummyHdc = nint.Zero;
                }

                if (_dummyHwnd != nint.Zero)
                {
                    WglInterop.DestroyWindow(_dummyHwnd);
                    _dummyHwnd = nint.Zero;
                }
            }

            IpcClient?.Dispose();
            IpcClient = null;

            if (_mpvHandle != nint.Zero)
            {
                if (skipRenderContextFree)
                {
                    RuntimeLog.Fail(InteropLogStep,
                        "Skipping mpv_terminate_destroy because a render thread is unaccounted for; the OS will reclaim it at process exit.");
                }
                else
                {
                    MpvWrapper.mpv_terminate_destroy(_mpvHandle);
                }
                _mpvHandle = nint.Zero;
            }

            if (gpuThreadAccountedFor)
            {
                if (_openglLibrary != nint.Zero)
                {
                    NativeLibrary.Free(_openglLibrary);
                    _openglLibrary = nint.Zero;
                }

                _d3d11Context?.Dispose();
                _d3d11Context = null;

                _d3d11Device?.Dispose();
                _d3d11Device = null;
            }

            _gpuInterop = null;

            if (_gcHandle.IsAllocated)
            {
                if (skipRenderContextFree)
                {
                    RuntimeLog.Fail(InteropLogStep,
                        "Leaving the interop GCHandle allocated because a render thread is unaccounted for.");
                }
                else
                {
                    _gcHandle.Free();
                }
            }

            if (!skipRenderContextFree)
            {
                try { _renderSignal.Dispose(); } catch { }
            }
        }
    }
}
