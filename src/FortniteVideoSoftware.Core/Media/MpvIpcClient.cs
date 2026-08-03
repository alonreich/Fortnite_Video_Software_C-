using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// Direct libmpv C API client. All method calls, property lookups, and event
/// notifications are routed through the native <c>libmpv-2.dll</c> via
/// <see cref="MpvWrapper"/> using the active <c>_mpvHandle</c> pointer.
///
/// <para><b>Event-Driven Architecture:</b> Instead of a 30fps polling timer,
/// this client spawns a dedicated background thread that blocks on
/// <c>mpv_wait_event</c>. Properties are observed via
/// <c>mpv_observe_property</c>, and <c>MPV_EVENT_PROPERTY_CHANGE</c> events
/// are intercepted in real-time. The <see cref="TimePosChanged"/> event fires
/// on the background thread — subscribers on the Avalonia UI thread must
/// marshal via <c>Dispatcher.UIThread.Post</c>.</para>
/// </summary>
public class MpvIpcClient : IDisposable
{
    private nint _mpvHandle;
    private Thread? _eventLoopThread;
    private CancellationTokenSource? _cts;
    private bool _ownsHandle;
    private bool _disposed;

    private volatile bool _isPaused;
    private volatile bool _isEof;

    private ulong _timePosObsId;
    private ulong _pauseObsId;
    private ulong _durationObsId;
    private ulong _eofObsId;


    public static int GlobalMasterVolume { get; private set; } = 100;
    public static event Action<int>? GlobalMasterVolumeChanged;
    public static void SetGlobalMasterVolume(int volume)
    {
        GlobalMasterVolume = volume;
        GlobalMasterVolumeChanged?.Invoke(volume);
    }

    public double CurrentTime { get; private set; }
    public double Duration { get; private set; }
    /// <summary>Thread-safe pause state (updated by event loop, read by UI).</summary>
    public bool IsPaused { get => _isPaused; private set => _isPaused = value; }
    /// <summary>Thread-safe EOF state (updated by event loop, read by UI).</summary>
    public bool IsEof { get => _isEof; private set => _isEof = value; }
    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }

    /// <summary>
    /// Fires on the event-loop thread whenever <c>time-pos</c> changes.
    /// Subscribers running on a UI thread must marshal via
    /// <c>Dispatcher.UIThread.Post</c>.
    /// </summary>
    public event Action<double>? TimePosChanged;

    /// <summary>
    /// Fires synchronously after a <c>seek</c> command is issued.
    /// </summary>
    public event Action? SeekCompleted;

    /// <summary>
    /// Fires when the <c>pause</c> property changes.
    /// </summary>
    public event Action<bool>? PauseChanged;


    /// <summary>
    /// Wraps an existing mpv handle (created by MpvVideoView / OpenGL path).
    /// The caller retains ownership of the handle lifecycle.
    /// </summary>
    public MpvIpcClient(nint mpvHandle)
    {
        _mpvHandle = mpvHandle;
        _ownsHandle = false;
        StartEventLoop();
    }

    /// <summary>
    /// Parameterless constructor for audio-only mode (Music Wizard).
    /// Call <see cref="StartAudioOnlyAsync"/> to create and initialize the
    /// underlying mpv handle. Until then, all command/property calls are no-ops.
    /// </summary>
    public MpvIpcClient()
    {
        _mpvHandle = nint.Zero;
        _ownsHandle = false;
    }


    /// <summary>
    /// Creates a headless audio-only mpv instance using the native libmpv C API.
    /// Sets <c>vid=no</c> and <c>vo=null</c> so no video decode or output occurs.
    /// The client owns the handle and will terminate it on <see cref="Dispose"/>.
    /// </summary>
    public Task StartAudioOnlyAsync(string mpvPath)
    {
        _mpvHandle = MpvWrapper.mpv_create();
        if (_mpvHandle == nint.Zero)
            throw new InvalidOperationException(
                "Failed to create mpv handle for audio-only mode. " +
                "Ensure libmpv-2.dll is available on the search path.");

        _ownsHandle = true;

        try
        {
            MpvWrapper.mpv_set_option_string(_mpvHandle, "vo", "null");

            MpvWrapper.mpv_set_option_string(_mpvHandle, "vid", "no");

            MpvWrapper.mpv_set_option_string(_mpvHandle, "terminal", "no");

            MpvWrapper.mpv_set_option_string(_mpvHandle, "idle", "yes");
            MpvWrapper.mpv_set_option_string(_mpvHandle, "ytdl", "no");

            int err = MpvWrapper.mpv_initialize(_mpvHandle);
            if (err < 0)
                throw new InvalidOperationException(
                    $"mpv_initialize failed with error code {err} (audio-only mode).");

            StartEventLoop();
            return Task.CompletedTask;
        }
        catch
        {
            MpvWrapper.SafeDestroy(ref _mpvHandle);
            _ownsHandle = false;
            throw;
        }
    }


    private void StartEventLoop()
    {
        if (_mpvHandle == nint.Zero) return;

        _cts = new CancellationTokenSource();

        _timePosObsId = MpvWrapper.ObserveProperty(_mpvHandle, "time-pos", MpvWrapper.MpvFormat.Double);
        _pauseObsId   = MpvWrapper.ObserveProperty(_mpvHandle, "pause",     MpvWrapper.MpvFormat.Double);
        _durationObsId = MpvWrapper.ObserveProperty(_mpvHandle, "duration",  MpvWrapper.MpvFormat.Double);
        _eofObsId     = MpvWrapper.ObserveProperty(_mpvHandle, "eof-reached", MpvWrapper.MpvFormat.Double);


        _eventLoopThread = new Thread(EventLoopWorker)
        {
            Name = "MpvEventLoop",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _eventLoopThread.Start(_cts.Token);
    }

    /// <summary>
    /// Background thread that blocks on mpv_wait_event and dispatches
    /// property-change events to subscribers.
    /// </summary>
    private void EventLoopWorker(object? obj)
    {
        var token = (CancellationToken)obj!;
        int widthCounter = 0;

        try
        {
            while (!token.IsCancellationRequested && _mpvHandle != nint.Zero && !_disposed)
            {
                try
                {
                    MpvWrapper.MpvEvent ev = MpvWrapper.WaitEvent(_mpvHandle, 0.2);

                    if (token.IsCancellationRequested || _disposed) break;

                    switch (ev.EventId)
                    {
                        case MpvWrapper.MpvEventId.PropertyChange:
                            HandlePropertyChange(ev);
                            break;

                        case MpvWrapper.MpvEventId.Shutdown:
                            return;
                    }

                    if (++widthCounter >= 20)
                    {
                        widthCounter = 0;
                        PollDimensions();
                    }
                }
                catch (Exception)
                {
                }
            }
        }
        finally
        {
            _eventLoopExited = true;
        }
    }

    /// <summary>
    /// Dispatches a property-change event to the appropriate state property
    /// and fires the corresponding C# event.
    /// </summary>
    private void HandlePropertyChange(MpvWrapper.MpvEvent ev)
    {
        MpvWrapper.MpvEventProperty prop = MpvWrapper.ReadEventProperty(ev);
        string? name = MpvWrapper.GetEventPropertyName(prop);
        if (name == null) return;

        double value = MpvWrapper.ReadEventPropertyDouble(prop);

        switch (name)
        {
            case "time-pos":
                double time = value >= 0 ? value : 0;
                if (Math.Abs(time - CurrentTime) > 0.001)
                {
                    CurrentTime = time;
                    TimePosChanged?.Invoke(time);
                }
                break;

            case "pause":
                bool paused = value > 0.5;
                if (paused != IsPaused)
                {
                    IsPaused = paused;
                    PauseChanged?.Invoke(paused);
                }
                break;

            case "duration":
                Duration = value > 0 ? value : 0;
                break;

            case "eof-reached":
                IsEof = value > 0.5;
                break;
        }
    }

    /// <summary>
    /// Lazily reads width/height from the native handle (infrequent).
    /// </summary>
    private void PollDimensions()
    {
        if (_mpvHandle == nint.Zero) return;

        string? wStr = GetPropertyString("width");
        if (int.TryParse(wStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w))
            VideoWidth = w;

        string? hStr = GetPropertyString("height");
        if (int.TryParse(hStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h))
            VideoHeight = h;
    }


    /// <summary>
    /// Reads an mpv property as a UTF-8 string via
    /// <c>mpv_get_property_string</c> + <c>mpv_free</c>.
    /// </summary>
    public string? GetPropertyString(string name)
    {
        if (_mpvHandle == nint.Zero) return null;

        nint ptr = MpvWrapper.mpv_get_property_string(_mpvHandle, name);
        if (ptr == nint.Zero) return null;

        string? result = Marshal.PtrToStringUTF8(ptr);
        MpvWrapper.mpv_free(ptr);
        return result;
    }


    /// <summary>
    /// Sends a command to mpv via <c>mpv_command_string</c>.
    /// Arguments are joined with spaces. Numeric values use InvariantCulture.
    /// String arguments containing spaces or backslashes are automatically
    /// quoted and escaped for the mpv command-string parser.
    /// </summary>
    public Task SendCommandAsync(params object[] args)
    {
        if (_mpvHandle == nint.Zero || args.Length == 0)
            return Task.CompletedTask;

        var sb = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0)
                sb.Append(' ');

            string str = args[i] switch
            {
                double d => d.ToString(CultureInfo.InvariantCulture),
                float f  => f.ToString(CultureInfo.InvariantCulture),
                decimal dec => dec.ToString(CultureInfo.InvariantCulture),
                _        => args[i].ToString() ?? string.Empty
            };

            if (i > 0 && NeedsMpvQuoting(str))
            {
                sb.Append(QuoteForMpv(str));
            }
            else
            {
                sb.Append(str);
            }
        }

        MpvWrapper.mpv_command_string(_mpvHandle, sb.ToString());

        if (args[0].ToString() == "seek")
            SeekCompleted?.Invoke();

        return Task.CompletedTask;
    }


    /// <summary>
    /// ISSUE_15 — true when an argument cannot be pasted into an mpv command string as-is.
    ///
    /// WHAT WAS WRONG: the old test was <c>str.Contains(' ') || str.Contains('\\')</c>, and the
    /// escaping was a bare <c>Replace("\\", "\\\\")</c>. A DOUBLE QUOTE in a filename was
    /// therefore neither a reason to quote nor something that got escaped, so a file such as
    /// <c>My "best" clip.mp4</c> produced a command mpv could not parse. mpv reports nothing
    /// useful for a malformed command string, so the preview (or the added audio track) simply
    /// did nothing, with no error anywhere. Every FFmpeg call in the suite was hardened against
    /// exactly this class of filename; the player commands were missed.
    ///
    /// A <c>#</c> starts a comment in mpv's parser and a leading/trailing space would be eaten,
    /// so those are covered too. An empty argument must be quoted or it disappears entirely.
    /// </summary>
    private static bool NeedsMpvQuoting(string s)
    {
        if (s.Length == 0) return true;
        foreach (char c in s)
        {
            if (c is ' ' or '\t' or '\\' or '"' or '\'' or '#' or '\r' or '\n') return true;
        }
        return false;
    }

    /// <summary>
    /// ISSUE_15 — wraps an argument in mpv's double-quoted string syntax, escaping the two
    /// characters that are special INSIDE such a string: the backslash and the double quote.
    /// Order matters — backslashes must be doubled first, otherwise the backslashes introduced
    /// while escaping the quotes would themselves be doubled.
    /// </summary>
    private static string QuoteForMpv(string s)
    {
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>
    /// Sets an mpv string property via <c>mpv_set_property_string</c>.
    /// </summary>
        public Task SetPropertyAsync(string name, string value)
    {
        if (_mpvHandle != nint.Zero)
        {
            if (name == "pause" && value == "no" && Duration > 0 && Math.Abs(CurrentTime - Duration) < 0.2)
            {
                MpvWrapper.mpv_command_string(_mpvHandle, "seek 0 absolute");
                CurrentTime = 0;
            }

            MpvWrapper.mpv_set_property_string(_mpvHandle, name, value);

            if (name == "pause")
                IsPaused = value == "yes";
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets an mpv double property (formatted with InvariantCulture) via
    /// <c>mpv_set_property_string</c>.
    /// </summary>
    public Task SetPropertyDoubleAsync(string name, double value)
    {
        if (_mpvHandle != nint.Zero)
            MpvWrapper.mpv_set_property_string(
                _mpvHandle, name,
                value.ToString(CultureInfo.InvariantCulture));
        return Task.CompletedTask;
    }


    /// <summary>
    /// Loads a media file into the mpv player.
    /// Sets the <c>start</c> property BEFORE issuing <c>loadfile</c>
    /// (per project IPC rule: start must be set independently).
    /// </summary>
    public Task LoadFileAsync(string path, double? startTime = null)
    {
        if (_mpvHandle == nint.Zero)
            return Task.CompletedTask;

        MpvWrapper.SetStartPosition(_mpvHandle, startTime ?? 0);

        MpvWrapper.LoadFile(_mpvHandle, path);

        MpvWrapper.SetPause(_mpvHandle, false);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds an external audio track via <c>audio-add</c> command.
    /// </summary>
    public Task AddExternalAudioAsync(string path)
    {
        if (_mpvHandle == nint.Zero)
            return Task.CompletedTask;

        MpvWrapper.mpv_command_string(_mpvHandle, $"audio-add {QuoteForMpv(path)} select");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes track ID 2 (the external audio track) via <c>audio-remove</c>.
    /// </summary>
    public Task RemoveExternalAudioAsync()
    {
        if (_mpvHandle == nint.Zero)
            return Task.CompletedTask;

        MpvWrapper.mpv_command_string(_mpvHandle, "audio-remove 2");
        return Task.CompletedTask;
    }


    /// <summary>
    /// ISSUE_13 — set by <see cref="EventLoopWorker"/>'s finally block. This is the ONLY reliable
    /// signal that the background thread has genuinely stopped touching <c>_mpvHandle</c>.
    /// </summary>
    private volatile bool _eventLoopExited;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        bool loopStopped = true;
        if (_cts != null)
        {
            try { _cts.Cancel(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }

            if (_mpvHandle != nint.Zero)
            {
                try { MpvWrapper.mpv_wakeup(_mpvHandle); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
            }

            if (_eventLoopThread != null)
            {
                try { loopStopped = _eventLoopThread.Join(TimeSpan.FromSeconds(3)); }
                catch { loopStopped = false; }
                _eventLoopThread = null;
            }

            try { _cts.Dispose(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
            _cts = null;
        }

        bool loopAccountedFor = loopStopped || _eventLoopExited;

        if (!loopAccountedFor)
        {
            CoreLogger.Fail("MPV",
                "The mpv event loop did not stop in time — abandoning the player handle instead of destroying it underneath a live thread.");
            _mpvHandle = nint.Zero;
            return;
        }

        if (_mpvHandle != nint.Zero)
        {
            MpvWrapper.UnobserveProperty(_mpvHandle, _timePosObsId);
            MpvWrapper.UnobserveProperty(_mpvHandle, _pauseObsId);
            MpvWrapper.UnobserveProperty(_mpvHandle, _durationObsId);
            MpvWrapper.UnobserveProperty(_mpvHandle, _eofObsId);
        }

        if (_ownsHandle && _mpvHandle != nint.Zero)
        {
            MpvWrapper.SafeDestroy(ref _mpvHandle);
        }

        _mpvHandle = nint.Zero;
    }
}
