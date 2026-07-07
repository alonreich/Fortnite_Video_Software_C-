using System;
using System.Runtime.InteropServices;

namespace FortniteVideoSoftware.Core.Media;

public static class LibMpvInterop
{
    private const string LibraryName = "libmpv-2.dll";

    public const int MPV_RENDER_PARAM_API_TYPE = 1;
    public const int MPV_RENDER_PARAM_OPENGL_INIT_PARAMS = 2;
    public const int MPV_RENDER_PARAM_OPENGL_FBO = 3;
    public const int MPV_RENDER_PARAM_FLIP_Y = 4;

    /// <summary>
    /// Bitmask flag returned by <see cref="mpv_render_context_update"/>.
    /// When set, a new video frame is available and <see cref="mpv_render_context_render"/>
    /// should be called to draw it to the FBO.
    /// </summary>
    public const ulong MPV_RENDER_UPDATE_FRAME = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct mpv_render_param
    {
        public int type;
        public nint data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct mpv_opengl_init_params
    {
        public nint get_proc_address;
        public nint get_proc_address_ctx;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct mpv_opengl_fbo
    {
        public int fbo;
        public int w;
        public int h;
        public int internal_format;
    }


    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_render_context_create(out nint res, nint mpv, [In] mpv_render_param[] param);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_render_context_create")]
    public static extern int mpv_render_context_create(out nint res, nint mpv, nint param);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_render_context_render(nint ctx, [In] mpv_render_param[] param);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_render_context_render")]
    public static extern int mpv_render_context_render(nint ctx, nint param);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_render_context_free(nint ctx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public unsafe static extern void mpv_render_context_set_update_callback(nint ctx, delegate* unmanaged[Cdecl]<nint, void> callback, nint cb_ctx);

    /// <summary>
    /// Signals a "wakeup" to the render context. This must be called from the
    /// update callback (or after receiving it) to acknowledge that the host
    /// is ready to consume the next frame. Returns a bitmask of flags; if
    /// <see cref="MPV_RENDER_UPDATE_FRAME"/> is set, call
    /// <see cref="mpv_render_context_render"/> to draw the frame.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong mpv_render_context_update(nint ctx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command_string(nint mpv, [MarshalAs(UnmanagedType.LPUTF8Str)] string cmd);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property_string(nint mpv, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string val);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint mpv_get_property_string(nint mpv, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_free(nint data);
}
