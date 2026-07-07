using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// Native libmpv P/Invoke bindings using NativeAOT-friendly [LibraryImport]
/// source generation, plus safe synchronous wrappers for common operations.
/// All methods operate on the raw <c>mpv_handle*</c> (<see cref="nint"/>).
/// </summary>
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
    public static partial int mpv_command_string(nint ctx, string args);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_property_string(nint ctx, string name, string data);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_property(nint ctx, string name, int format, ref double data);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_option_string(nint ctx, string name, string data);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint mpv_get_property_string(nint ctx, string name);

    [LibraryImport(LibraryName)]
    public static partial void mpv_free(nint data);


    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_observe_property(nint ctx, ulong reply_userdata,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_unobserve_property(nint ctx, ulong id);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint mpv_wait_event(nint ctx, double timeout);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_wakeup(nint ctx);


    /// <summary>mpv event IDs (partial — only the ones we handle).</summary>
    public enum MpvEventId : int
    {
        None = 0,
        Shutdown = 9,
        LogMessage = 11,
        PropertyChange = 22,
    }

    /// <summary>mpv property formats (partial).</summary>
    public enum MpvFormat : int
    {
        None = 0,
        String = 1,
        Double = 5,
    }


    /// <summary>
    /// Layout: { int event_id; int error; uint64_t reply_userdata; void* data; }
    /// Total on 64-bit: 4 + 4 + 8 + 8 = 24 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEvent
    {
        public MpvEventId EventId;
        public int Error;
        public ulong ReplyUserdata;
        public nint Data;
    }

    /// <summary>
    /// Layout: { const char* name; mpv_format format; void* data; }
    /// On 64-bit: 8 + 4 + 4(padding) + 8 = 24 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventProperty
    {
        public nint Name;
        public MpvFormat Format;
        public nint Data;
    }


    /// <summary>Creates and initializes an mpv handle, or returns nint.Zero on failure.</summary>
    public static nint CreateAndInitialize()
    {
        nint handle = mpv_create();
        if (handle == nint.Zero) return nint.Zero;
        if (mpv_initialize(handle) < 0)
        {
            mpv_terminate_destroy(handle);
            return nint.Zero;
        }
        return handle;
    }

    /// <summary>Safely terminates and destroys an mpv handle (null-safe).</summary>
    public static void SafeDestroy(ref nint handle)
    {
        if (handle == nint.Zero) return;
        mpv_terminate_destroy(handle);
        handle = nint.Zero;
    }


    /// <summary>Sets the 'pause' property.</summary>
    public static void SetPause(nint handle, bool pause)
    {
        if (handle != nint.Zero)
            mpv_set_property_string(handle, "pause", pause ? "yes" : "no");
    }

    /// <summary>Reads the 'pause' property. Returns false if handle is invalid.</summary>
    public static bool GetIsPaused(nint handle)
    {
        return GetPropertyString(handle, "pause") == "yes";
    }


    /// <summary>Sets the 'speed' property (InvariantCulture).</summary>
    public static void SetSpeed(nint handle, double speed)
    {
        if (handle != nint.Zero)
            mpv_set_property_string(handle, "speed", speed.ToString(CultureInfo.InvariantCulture));
    }


    /// <summary>Sets the 'volume' property (integer 0–100+).</summary>
    public static void SetVolume(nint handle, int volume)
    {
        if (handle != nint.Zero)
            mpv_set_property_string(handle, "volume", volume.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Sets the 'volume' property (double, InvariantCulture).</summary>
    public static void SetVolume(nint handle, double volume)
    {
        if (handle != nint.Zero)
            mpv_set_property_string(handle, "volume", volume.ToString(CultureInfo.InvariantCulture));
    }


    /// <summary>Absolute seek (seconds, InvariantCulture).</summary>
    public static void SeekAbsolute(nint handle, double seconds)
    {
        if (handle != nint.Zero)
            mpv_command_string(handle, $"seek {seconds.ToString(CultureInfo.InvariantCulture)} absolute");
    }

    /// <summary>Relative seek (±seconds, InvariantCulture).</summary>
    public static void SeekRelative(nint handle, double deltaSeconds)
    {
        if (handle != nint.Zero)
            mpv_command_string(handle, $"seek {deltaSeconds.ToString(CultureInfo.InvariantCulture)} relative");
    }


    /// <summary>Advance one frame forward.</summary>
    public static void FrameStep(nint handle)
    {
        if (handle != nint.Zero)
            mpv_command_string(handle, "frame-step");
    }

    /// <summary>Advance one frame backward.</summary>
    public static void FrameBackStep(nint handle)
    {
        if (handle != nint.Zero)
            mpv_command_string(handle, "frame-back-step");
    }


    /// <summary>Stops playback and clears the playlist.</summary>
    public static void Stop(nint handle)
    {
        if (handle != nint.Zero)
            mpv_command_string(handle, "stop");
    }


    /// <summary>
    /// Loads a file into mpv. The path is automatically escaped for the mpv
    /// command-string parser (backslashes doubled, entire path quoted).
    /// </summary>
    public static void LoadFile(nint handle, string path)
    {
        if (handle == nint.Zero) return;
        string safePath = path.Replace("\\", "\\\\");
        mpv_command_string(handle, $"loadfile \"{safePath}\"");
    }

    /// <summary>
    /// Sets the 'start' property independently BEFORE a loadfile, per project
    /// IPC rule (mpv ignores options embedded in the loadfile command).
    /// </summary>
    public static void SetStartPosition(nint handle, double seconds)
    {
        if (handle != nint.Zero)
            mpv_set_property_string(handle, "start",
                seconds > 0 ? seconds.ToString(CultureInfo.InvariantCulture) : "0");
    }


    /// <summary>
    /// Reads a property as a string. The returned nint is automatically freed.
    /// Returns null if the handle is invalid or the property has no value.
    /// </summary>
    public static string? GetPropertyString(nint handle, string name)
    {
        if (handle == nint.Zero) return null;
        nint ptr = mpv_get_property_string(handle, name);
        if (ptr == nint.Zero) return null;
        string? result = Marshal.PtrToStringUTF8(ptr);
        mpv_free(ptr);
        return result;
    }

    /// <summary>Reads 'time-pos' as a double. Returns 0 on failure.</summary>
    public static double GetTimePos(nint handle)
    {
        string? s = GetPropertyString(handle, "time-pos");
        return double.TryParse(s, CultureInfo.InvariantCulture, out double v) ? v : 0;
    }

    /// <summary>Reads 'duration' as a double. Returns 0 on failure.</summary>
    public static double GetDuration(nint handle)
    {
        string? s = GetPropertyString(handle, "duration");
        return double.TryParse(s, CultureInfo.InvariantCulture, out double v) ? v : 0;
    }

    /// <summary>Reads 'eof-reached'. Returns false on failure.</summary>
    public static bool GetIsEof(nint handle)
    {
        return GetPropertyString(handle, "eof-reached") == "yes";
    }


    /// <summary>
    /// Registers interest in a property. When it changes, MPV_EVENT_PROPERTY_CHANGE
    /// events will be delivered on the event queue.
    /// </summary>
    public static ulong ObserveProperty(nint handle, string name, MpvFormat format)
    {
        if (handle == nint.Zero) return 0;
        int id = mpv_observe_property(handle, 0, name, (int)format);
        return (ulong)id;
    }

    /// <summary>Removes a property observation by its ID.</summary>
    public static void UnobserveProperty(nint handle, ulong id)
    {
        if (handle != nint.Zero && id != 0)
            mpv_unobserve_property(handle, id);
    }


    /// <summary>
    /// Waits for the next mpv event (up to <paramref name="timeoutSeconds"/>).
    /// Returns the marshalled <see cref="MpvEvent"/> struct.
    /// The returned struct's <see cref="MpvEvent.Data"/> pointer is owned by
    /// mpv and is only valid until the next call to this method.
    /// </summary>
    public static MpvEvent WaitEvent(nint handle, double timeoutSeconds = -1.0)
    {
        if (handle == nint.Zero)
            return default;

        nint ptr = mpv_wait_event(handle, timeoutSeconds);
        if (ptr == nint.Zero)
            return default;

        return Marshal.PtrToStructure<MpvEvent>(ptr);
    }

    /// <summary>
    /// Reads a <see cref="MpvEventProperty"/> from an event's Data pointer
    /// (only valid when <see cref="MpvEvent.EventId"/> == <see cref="MpvEventId.PropertyChange"/>).
    /// </summary>
    public static MpvEventProperty ReadEventProperty(MpvEvent ev)
    {
        if (ev.Data == nint.Zero)
            return default;
        return Marshal.PtrToStructure<MpvEventProperty>(ev.Data);
    }

    /// <summary>
    /// Reads the property name from an <see cref="MpvEventProperty"/> (UTF-8 string).
    /// </summary>
    public static string? GetEventPropertyName(MpvEventProperty prop)
    {
        return prop.Name == nint.Zero ? null : Marshal.PtrToStringUTF8(prop.Name);
    }

    /// <summary>
    /// Reads the double value from an MPV_FORMAT_DOUBLE property change event.
    /// The <see cref="MpvEventProperty.Data"/> pointer points directly to a double.
    /// </summary>
    public static double ReadEventPropertyDouble(MpvEventProperty prop)
    {
        if (prop.Data == nint.Zero) return 0;
        return Marshal.PtrToStructure<double>(prop.Data);
    }
}