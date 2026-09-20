using System;
using System.Runtime.InteropServices;

namespace FortniteVideoSoftware.App.Interop;

public static unsafe class WglInterop
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern nint CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    public static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(nint hWnd);

    [DllImport("opengl32.dll", ExactSpelling = true)]
    public static extern void glClearColor(float red, float green, float blue, float alpha);

    [DllImport("opengl32.dll", ExactSpelling = true)]
    public static extern void glClear(uint mask);

    [DllImport("opengl32.dll", ExactSpelling = true)]
    public static extern void glFlush();

    [DllImport("opengl32.dll", ExactSpelling = true)]
    public static extern void glFinish();

    public const uint GL_COLOR_BUFFER_BIT = 0x00004000;

    [DllImport("gdi32.dll")]
    public static extern int ChoosePixelFormat(nint hdc, ref PIXELFORMATDESCRIPTOR ppfd);

    [DllImport("gdi32.dll")]
    public static extern bool SetPixelFormat(nint hdc, int format, ref PIXELFORMATDESCRIPTOR ppfd);

    [DllImport("opengl32.dll")]
    public static extern nint wglCreateContext(nint hdc);

    [DllImport("opengl32.dll")]
    public static extern bool wglMakeCurrent(nint hdc, nint hglrc);

    [DllImport("opengl32.dll")]
    public static extern bool wglDeleteContext(nint hglrc);

    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)]
    public static extern nint wglGetProcAddress(string lpszProc);

    [DllImport("opengl32.dll")]
    public static extern void glGenTextures(int n, uint[] textures);

    [DllImport("opengl32.dll")]
    public static extern void glDeleteTextures(int n, uint[] textures);

    [DllImport("opengl32.dll")]
    public static extern void glBindTexture(uint target, uint texture);

    [DllImport("opengl32.dll")]
    public static extern void glTexParameteri(uint target, uint pname, int param);

    [StructLayout(LayoutKind.Sequential)]
    public struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize;
        public ushort nVersion;
        public uint dwFlags;
        public byte iPixelType;
        public byte cColorBits;
        public byte cRedBits;
        public byte cRedShift;
        public byte cGreenBits;
        public byte cGreenShift;
        public byte cBlueBits;
        public byte cBlueShift;
        public byte cAlphaBits;
        public byte cAlphaShift;
        public byte cAccumBits;
        public byte cAccumRedBits;
        public byte cAccumGreenBits;
        public byte cAccumBlueBits;
        public byte cAccumAlphaBits;
        public byte cDepthBits;
        public byte cStencilBits;
        public byte cAuxBuffers;
        public byte iLayerType;
        public byte bReserved;
        public uint dwLayerMask;
        public uint dwVisibleMask;
        public uint dwDamageMask;
    }

    public const uint PFD_DRAW_TO_WINDOW = 4;
    public const uint PFD_SUPPORT_OPENGL = 32;
    public const uint PFD_DOUBLEBUFFER = 1;
    public const byte PFD_TYPE_RGBA = 0;

    public const uint GL_TEXTURE_2D = 0x0DE1;
    public const uint GL_FRAMEBUFFER = 0x8D40;
    public const uint GL_COLOR_ATTACHMENT0 = 0x8CE0;
    public const uint GL_RGBA8 = 0x8058;
    
    public const uint GL_TEXTURE_MIN_FILTER = 0x2801;
    public const uint GL_TEXTURE_MAG_FILTER = 0x2800;
    public const int GL_LINEAR = 0x2601;

    public const uint WGL_ACCESS_READ_ONLY_NV = 0x0000;
    public const uint WGL_ACCESS_READ_WRITE_NV = 0x0001;
    public const uint WGL_ACCESS_WRITE_DISCARD_NV = 0x0002;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void glGenFramebuffers_t(int n, uint[] framebuffers);
    
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void glDeleteFramebuffersDelegate(int n, uint[] framebuffers);
    
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint glCheckFramebufferStatusDelegate(uint target);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void glBindFramebuffer_t(uint target, uint framebuffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void glFramebufferTexture2D_t(uint target, uint attachment, uint textarget, uint texture, int level);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate nint wglDXOpenDeviceNV_t(nint dxDevice);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate bool wglDXCloseDeviceNV_t(nint hDevice);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate nint wglDXRegisterObjectNV_t(nint hDevice, nint dxObject, uint name, uint type, uint access);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate bool wglDXUnregisterObjectNV_t(nint hDevice, nint hObject);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate bool wglDXLockObjectsNV_t(nint hDevice, int count, nint[] hObjects);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate bool wglDXUnlockObjectsNV_t(nint hDevice, int count, nint[] hObjects);

    public static glGenFramebuffers_t? glGenFramebuffers;
    public static glDeleteFramebuffersDelegate? glDeleteFramebuffers;
    public static glCheckFramebufferStatusDelegate? glCheckFramebufferStatus;
    public static glBindFramebuffer_t? glBindFramebuffer;
    public static glFramebufferTexture2D_t? glFramebufferTexture2D;
    
    public static wglDXOpenDeviceNV_t? wglDXOpenDeviceNV;
    public static wglDXCloseDeviceNV_t? wglDXCloseDeviceNV;
    public static wglDXRegisterObjectNV_t? wglDXRegisterObjectNV;
    public static wglDXUnregisterObjectNV_t? wglDXUnregisterObjectNV;
    public static wglDXLockObjectsNV_t? wglDXLockObjectsNV;
    public static wglDXUnlockObjectsNV_t? wglDXUnlockObjectsNV;

    public static void LoadExtensions()
    {
        glGenFramebuffers = Load<glGenFramebuffers_t>("glGenFramebuffers") ?? Load<glGenFramebuffers_t>("glGenFramebuffersEXT");
        glDeleteFramebuffers = Load<glDeleteFramebuffersDelegate>("glDeleteFramebuffers") ?? Load<glDeleteFramebuffersDelegate>("glDeleteFramebuffersEXT");
        glCheckFramebufferStatus = Load<glCheckFramebufferStatusDelegate>("glCheckFramebufferStatus") ?? Load<glCheckFramebufferStatusDelegate>("glCheckFramebufferStatusEXT");
        glBindFramebuffer = Load<glBindFramebuffer_t>("glBindFramebuffer") ?? Load<glBindFramebuffer_t>("glBindFramebufferEXT");
        glFramebufferTexture2D = Load<glFramebufferTexture2D_t>("glFramebufferTexture2D") ?? Load<glFramebufferTexture2D_t>("glFramebufferTexture2DEXT");

        wglDXOpenDeviceNV = Load<wglDXOpenDeviceNV_t>("wglDXOpenDeviceNV");
        wglDXCloseDeviceNV = Load<wglDXCloseDeviceNV_t>("wglDXCloseDeviceNV");
        wglDXRegisterObjectNV = Load<wglDXRegisterObjectNV_t>("wglDXRegisterObjectNV");
        wglDXUnregisterObjectNV = Load<wglDXUnregisterObjectNV_t>("wglDXUnregisterObjectNV");
        wglDXLockObjectsNV = Load<wglDXLockObjectsNV_t>("wglDXLockObjectsNV");
        wglDXUnlockObjectsNV = Load<wglDXUnlockObjectsNV_t>("wglDXUnlockObjectsNV");
    }

    private static T? Load<T>(string name) where T : Delegate
    {
        nint ptr = wglGetProcAddress(name);
        if (ptr == nint.Zero || ptr == (nint)1 || ptr == (nint)2 || ptr == (nint)3 || ptr == (nint)(-1))
            return null;
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }
}
