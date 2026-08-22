using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace FortniteVideoSoftware.App.Services;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// PREWARM_01 — RENDER THE FILM LANE BEFORE ANYONE ASKS FOR IT.
///
/// The lane is now fast (STRIP_03: first frame ~0.2s, complete ~0.5s) but it still starts from
/// nothing at the moment a screen opens. The inputs, however, are known long before that: the
/// instant the user sets MARK START and MARK END in the Main App, the exact range every filmstrip
/// will be built from is decided. This renders it then, in the background, so by the time the
/// Granular editor or the Voice-Over window is opened the strip is usually already sitting in
/// memory and the lane paints on the first frame.
///
/// ── ⚠️ THIS IS A PREWARM, NOT A CACHE. READ BEFORE JUDGING IT AGAINST MANDATE #1. ─────────────
/// Mandate #1 forbids caches "for FFmpeg exports or media generation", and what it is protecting
/// against is a result OUTLIVING or being REUSED ACROSS jobs — a stale artifact silently standing
/// in for work that should have been redone. Every property that makes a cache dangerous is
/// absent here, by construction:
///   * SINGLE ENTRY. One strip is held. A new range REPLACES it; there is no store to go stale.
///   * MEMORY ONLY. Nothing is written to disk. Nothing survives the process.
///   * TAKE-ONCE. <see cref="TryTake"/> removes the strip as it hands it over. It is consumed by
///     exactly one screen and can never be served twice, so two windows cannot end up sharing —
///     and disposing — one bitmap.
///   * INPUT-KEYED AND SELF-INVALIDATING. The key is (path, start, duration, frames). Any change
///     to the trim, or a different video, makes the held strip unreachable and cancels the render
///     in flight. It cannot answer for a range it was not built from.
///   * NOT ON THE EXPORT PATH. This produces a 60px-tall UI decoration. No exported frame, no
///     encoder decision and no timing calculation reads it. If it is empty, wrong, or never runs,
///     the only consequence is that a lane renders on demand exactly as it does today.
/// It is the same single render the screen would have started itself, moved earlier in time. If a
/// future change would make it PERSIST, be REUSED between exports, or feed anything that ends up
/// in the output file, it stops being this and becomes the thing the mandate forbids.
///
/// ⚠️ FAILURE IS ALWAYS SILENT AND ALWAYS SAFE. Every caller treats a miss as "render it now".
/// Do not add throwing, do not add retries, and do not make a screen WAIT on a prewarm — the
/// whole point is that it is invisible whether it worked or not.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
internal static class FilmstripPrewarm
{
    /// <summary>
    /// Debounce before a scheduled render actually starts. Dragging a trim marker fires a change
    /// per pointer move; without this the app would spawn and kill an ffmpeg process per frame of
    /// the drag. Long enough to sit out a drag, short enough that the render is done before the
    /// user can set a trim and open the editor.
    /// </summary>
    private const int DebounceMs = 450;

    /// <summary>Ranges shorter than this are not worth a process.</summary>
    private const double MinRangeSec = 0.5;

    private readonly record struct Key(string Path, long StartMs, long DurMs, int Frames);

    private static readonly object _gate = new();

    private static Key _readyKey;
    private static WriteableBitmap? _ready;

    private static Key _wantedKey;
    private static CancellationTokenSource? _cts;
    private static Avalonia.Threading.DispatcherTimer? _debounce;
    private static (string ffmpegPath, string videoPath, double startSec, double durSec, int frames, Key key)? _pending;

    private static Key MakeKey(string path, double startSec, double durSec, int frames)
        => new(path ?? string.Empty,
               (long)Math.Round(startSec * 1000.0),
               (long)Math.Round(durSec * 1000.0),
               frames);

    /// <summary>
    /// Asks for the strip covering this range to exist by the time somebody wants it. Cheap and
    /// idempotent: re-scheduling the SAME range that is already rendering or already held does
    /// nothing. Call it whenever the trim changes; the debounce absorbs a drag.
    /// </summary>
    public static void Schedule(string ffmpegPath, string videoPath, double startSec, double durSec,
                                int frames = ThumbnailStripGenerator.DefaultFrames)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || string.IsNullOrWhiteSpace(videoPath)) return;
        if (durSec < MinRangeSec) return;
        if (!System.IO.File.Exists(videoPath)) return;

        var key = MakeKey(videoPath, startSec, durSec, frames);

        lock (_gate)
        {
            if (_ready != null && _readyKey.Equals(key)) return;   // already have it
            if (_cts != null && _wantedKey.Equals(key)) return;    // already rendering it

            // A different range is wanted: whatever was held is now unreachable, and whatever is
            // rendering is for the wrong range. Drop both.
            CancelInFlightLocked();
            DiscardReadyLocked();
            _wantedKey = key;
        }

        // LEAK_01 — the pending request is stored in a FIELD and the Tick handler is attached
        // EXACTLY ONCE. The obvious version — a local `Fire` closure subscribed per call — adds a
        // new handler on every Schedule and only removes it when the timer actually ticks. A
        // marker drag calls this on every pointer-move with a different key, so a five-second drag
        // at 60Hz accumulates ~300 live closures that all fire together on the next tick. It
        // self-heals, which is precisely why it would never be noticed.
        lock (_gate) { _pending = (ffmpegPath, videoPath, startSec, durSec, frames, key); }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_debounce == null)
            {
                _debounce = new Avalonia.Threading.DispatcherTimer(Avalonia.Threading.DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(DebounceMs)
                };
                _debounce.Tick += OnDebounceTick;
            }

            _debounce.Stop();
            _debounce.Start();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private static void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce?.Stop();

        (string ffmpegPath, string videoPath, double startSec, double durSec, int frames, Key key)? job;
        lock (_gate) { job = _pending; _pending = null; }
        if (job == null) return;

        var j = job.Value;
        Start(j.ffmpegPath, j.videoPath, j.startSec, j.durSec, j.frames, j.key);
    }

    private static void Start(string ffmpegPath, string videoPath, double startSec, double durSec,
                              int frames, Key key)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (!_wantedKey.Equals(key)) return;   // superseded while we waited out the debounce
            CancelInFlightLocked();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
        }

        _ = Task.Run(async () =>
        {
            WriteableBitmap? built = null;
            bool ok = false;
            try
            {
                ok = await ThumbnailStripGenerator.StreamAsync(
                    ffmpegPath, videoPath, startSec, durSec, token,
                    onReady: wb => built = wb,
                    onFrame: null,
                    frames: frames,
                    logTag: "Prewarm").ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); return; }

            lock (_gate)
            {
                // LEAK_01 — retire the token source once the render is over. Leaving it in place
                // both holds the object and makes `Schedule` believe this range is still rendering,
                // so a later request for the SAME range after TryTake emptied the slot would be
                // dropped as a duplicate and never re-warm.
                if (_cts != null && _cts.Token == token)
                {
                    try { _cts.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                    _cts = null;
                }

                if (token.IsCancellationRequested || !_wantedKey.Equals(key))
                {
                    try { built?.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                    return;
                }

                if (ok && built != null)
                {
                    DiscardReadyLocked();
                    _ready = built;
                    _readyKey = key;
                    RuntimeLog.Info("Prewarm", $"Film lane pre-rendered for {frames} frames over {durSec:F1}s.");
                }
                else
                {
                    try { built?.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                }
            }
        });
    }

    /// <summary>
    /// Hands over the pre-rendered strip for this range if there is one, REMOVING it from the
    /// store. Returns null on any mismatch — the caller must always be able to render it itself.
    /// </summary>
    public static WriteableBitmap? TryTake(string videoPath, double startSec, double durSec,
                                           int frames = ThumbnailStripGenerator.DefaultFrames)
    {
        var key = MakeKey(videoPath, startSec, durSec, frames);
        lock (_gate)
        {
            if (_ready == null || !_readyKey.Equals(key)) return null;
            var taken = _ready;
            _ready = null;
            _readyKey = default;
            return taken;
        }
    }

    /// <summary>Drops everything. Call when the loaded video changes or the app shuts down.</summary>
    public static void Clear()
    {
        lock (_gate)
        {
            CancelInFlightLocked();
            DiscardReadyLocked();
            _wantedKey = default;
            _pending = null;
        }
    }

    private static void CancelInFlightLocked()
    {
        if (_cts == null) return;
        try { _cts.Cancel(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        try { _cts.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        _cts = null;
    }

    private static void DiscardReadyLocked()
    {
        if (_ready == null) return;
        try { _ready.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        _ready = null;
        _readyKey = default;
    }
}
