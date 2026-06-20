using System.Runtime.InteropServices;
using System.Text;

namespace FortniteVideoSoftware.Core.Media;

public static partial class MpvWrapper
{
    private const string LibraryName = "libmpv-2.dll";

    [LibraryImport(LibraryName)]
    public static partial nint mpv_create();

    [LibraryImport(LibraryName)]
    public static partial int mpv_initialize(nint ctx);

    [LibraryImport(LibraryName)]
    public static partial void mpv_terminate_destroy(nint ctx);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_command(nint ctx, string[] args);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_property_string(nint ctx, string name, string data);
    
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_property(nint ctx, string name, int format, ref double data);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_option_string(nint ctx, string name, string data);

    [LibraryImport(LibraryName)]
    public static partial nint mpv_get_property_string(nint ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport(LibraryName)]
    public static partial void mpv_free(nint data);
}
