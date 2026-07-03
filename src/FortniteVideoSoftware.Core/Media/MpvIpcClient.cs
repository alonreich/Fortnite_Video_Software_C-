using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

    // Boolean properties are marked volatile for cross-thread visibility
    // (written by event-loop thread, read by UI thread).
    private volatile bool _isPaused;
    private volatile bool _isEof;

    // Observed property IDs (for cleanup on dispose)
    private ulong _timePosObsId;
    private ulong _pauseObsId;
    private ulong _durationObsId;
    private ulong _eofObsId;

    // ==================================================================
    // Public state properties (updated by the event-loop thread)
    // ==================================================================

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

    // ==================================================================
    // Constructors
    // ==================================================================

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

    // ==================================================================
    // Audio-only initialization (Music Wizard)
    // ==================================================================

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

        // CRITICAL: Set audio-only options BEFORE mpv_initialize().
        // These are pre-init options — setting them after initialize has no effect.

        // Disable all video output — route to null sink (no window, no render).
        MpvWrapper.mpv_set_option_string(_mpvHandle, "vo", "null");

        // Disable video decode entirely (audio-only mode for Music Wizard).
        MpvWrapper.mpv_set_option_string(_mpvHandle, "vid", "no");

        // Suppress terminal output.
        MpvWrapper.mpv_set_option_string(_mpvHandle, "terminal", "no");

        // Keep idle so we can load/unload files without quitting.
        MpvWrapper.mpv_set_option_string(_mpvHandle, "idle", "yes");

        // Now initialize with the correct options baked in.
        int err = MpvWrapper.mpv_initialize(_mpvHandle);
        if (err < 0)
            throw new InvalidOperationException(
                $"mpv_initialize failed with error code {err} (audio-only mode).");

        _ownsHandle = true;
        StartEventLoop();
        return Task.CompletedTask;
    }

    // ==================================================================
    // Native event loop (replaces polling timer)
    // ==================================================================

    private void StartEventLoop()
    {
        if (_mpvHandle == nint.Zero) return;

        _cts = new CancellationTokenSource();

        // Observe key properties — mpv will push MPV_EVENT_PROPERTY_CHANGE
        // events onto the queue whenever they change.
        _timePosObsId = MpvWrapper.ObserveProperty(_mpvHandle, "time-pos", MpvWrapper.MpvFormat.Double);
        _pauseObsId   = MpvWrapper.ObserveProperty(_mpvHandle, "pause",     MpvWrapper.MpvFormat.Double);
        _durationObsId = MpvWrapper.ObserveProperty(_mpvHandle, "duration",  MpvWrapper.MpvFormat.Double);
        _eofObsId     = MpvWrapper.ObserveProperty(_mpvHandle, "eof-reached", MpvWrapper.MpvFormat.Double);

        // Dimension properties don't change during playback; read them lazily
        // from GetPropertyString in the event loop occasionally.

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

        while (!token.IsCancellationRequested && _mpvHandle != nint.Zero)
        {
            try
            {
                // Block up to 200ms so we can check cancellation periodically.
                MpvWrapper.MpvEvent ev = MpvWrapper.WaitEvent(_mpvHandle, 0.2);

                if (token.IsCancellationRequested) break;

                switch (ev.EventId)
                {
                    case MpvWrapper.MpvEventId.PropertyChange:
                        HandlePropertyChange(ev);
                        break;

                    case MpvWrapper.MpvEventId.Shutdown:
                        return; // mpv is shutting down — exit thread
                }

                // Poll video dimensions every ~20 iterations (4 seconds).
                // These rarely change and aren't worth a separate observation.
                if (++widthCounter >= 20)
                {
                    widthCounter = 0;
                    PollDimensions();
                }
            }
            catch (Exception)
            {
                // Swallow — transient marshalling errors during video swap.
            }
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
                // mpv reports -1 for "no value" (e.g., stopped). Clamp to 0.
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

    // ==================================================================
    // Property access — direct mpv_get_property_string C API
    // ==================================================================

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

    // ==================================================================
    // Command dispatch — direct mpv_command_string C API
    // ==================================================================

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

            // The first token is the command name — never quote it.
            // Subsequent tokens that look like file paths (contain backslash
            // or space) are quoted with escaped backslashes for mpv.
            if (i > 0 && (str.Contains(' ') || str.Contains('\\')))
            {
                sb.Append('"')
                  .Append(str.Replace("\\", "\\\\"))
                  .Append('"');
            }
            else
            {
                sb.Append(str);
            }
        }

        MpvWrapper.mpv_command_string(_mpvHandle, sb.ToString());

        // Fire seek-completed notification for seek commands.
        if (args[0].ToString() == "seek")
            SeekCompleted?.Invoke();

        return Task.CompletedTask;
    }

    // ==================================================================
    // Property setters — direct mpv_set_property_string C API
    // ==================================================================

    /// <summary>
    /// Sets an mpv string property via <c>mpv_set_property_string</c>.
    /// </summary>
    public Task SetPropertyAsync(string name, string value)
    {
        if (_mpvHandle != nint.Zero)
        {
            MpvWrapper.mpv_set_property_string(_mpvHandle, name, value);

            // Optimistically update local cache for immediate UI responsiveness.
            // The native event loop confirms the actual state shortly.
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

    // ==================================================================
    // File loading — loadfile via mpv_command_string
    // ==================================================================

    /// <summary>
    /// Loads a media file into the mpv player.
    /// Sets the <c>start</c> property BEFORE issuing <c>loadfile</c>
    /// (per project IPC rule: start must be set independently).
    /// </summary>
    public Task LoadFileAsync(string path, double? startTime = null)
    {
        if (_mpvHandle == nint.Zero)
            return Task.CompletedTask;

        // Per project mandate: set 'start' independently before loadfile.
        MpvWrapper.SetStartPosition(_mpvHandle, startTime ?? 0);

        // Load the file (path is escaped inside MpvWrapper.LoadFile).
        MpvWrapper.LoadFile(_mpvHandle, path);

        // Default to playing immediately.
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

        string safePath = path.Replace("\\", "\\\\");
        MpvWrapper.mpv_command_string(_mpvHandle, $"audio-add \"{safePath}\" select");
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

    // ==================================================================
    // Dispose / cleanup — guarantees all unmanaged resources are freed
    // ==================================================================

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Signal the event-loop thread to stop and wait for it.
        if (_cts != null)
        {
            _cts.Cancel();

            // Wake the thread if it's blocked inside mpv_wait_event.
            if (_mpvHandle != nint.Zero)
                MpvWrapper.mpv_wakeup(_mpvHandle);

            // Give the thread a moment to exit cleanly.
            _eventLoopThread?.Join(TimeSpan.FromMilliseconds(500));
            _cts.Dispose();
            _cts = null;
        }

        // Unobserve properties (best-effort — handle may be invalid after this).
        if (_mpvHandle != nint.Zero)
        {
            MpvWrapper.UnobserveProperty(_mpvHandle, _timePosObsId);
            MpvWrapper.UnobserveProperty(_mpvHandle, _pauseObsId);
            MpvWrapper.UnobserveProperty(_mpvHandle, _durationObsId);
            MpvWrapper.UnobserveProperty(_mpvHandle, _eofObsId);
        }

        // Only terminate the handle if we created it (audio-only mode).
        // For the video-view case, MpvVideoView.OnOpenGlDeinit/DisposeMpv
        // owns the lifecycle.
        if (_ownsHandle && _mpvHandle != nint.Zero)
        {
            MpvWrapper.SafeDestroy(ref _mpvHandle);
        }

        _mpvHandle = nint.Zero;
    }
}