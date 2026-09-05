using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App.Infrastructure;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// MEME_07 — THE MEME CUTAWAY, SHOWN IN THE PREVIEW EXACTLY AS IT WILL BE EXPORTED.
///
/// <para>
/// <b>The problem this solves.</b> A meme occupies ZERO source seconds and its whole length in
/// OUTPUT seconds, so <see cref="OutputTimeline.OutputToSourceRelative"/> answers every instant
/// inside the block with the anchor frame. That is mathematically right and visually useless: the
/// preview parked on one still while the finished video plays a whole meme, and the user could not
/// see what they had built until they exported it.
/// </para>
///
/// <para>
/// <b>The approach (option B — one host, swap the file).</b> Every preview in this app is a single
/// <c>MpvVideoView</c> that owns a libmpv render context, a D3D11 device, WGL/DXGI interop and a
/// 16-texture shared swap chain. A SECOND one, layered over the first, would give a seamless
/// cutaway and would also double that stack in every window and add a disposal-ordering hazard to
/// four of them. So instead the ONE host is re-pointed: park the gameplay on the anchor frame,
/// load the meme, play it through, load the gameplay back at the same anchor frame, carry on.
/// </para>
///
/// <para>
/// <b>The cost, and why it is announced rather than hidden.</b> Two <c>loadfile</c> round-trips per
/// meme, a few hundred milliseconds each, during which the picture is black. Hiding that would read
/// as a stutter or a crash. So each swap raises a blocking, unmissable overlay that says what is
/// happening and clears itself the moment the picture is live again. The user asked for exactly
/// this: stall visibly, then be flawless.
/// </para>
///
/// <para>
/// <b>⚠️ THE RULE EVERY HOST WINDOW MUST FOLLOW.</b> While this director is active, mpv is showing
/// the MEME, so <c>CurrentTime</c>, <c>Duration</c> and <c>IsEof</c> describe the meme file and NOT
/// the gameplay. Every consumer of those — cut-skip, trim-end-stop, music sync, voice-take sync,
/// the zoom crop updater, the end-of-video handler — would misfire. So the host's playback tick
/// must begin with:
/// <code>
///     if (_memePreview != null &amp;&amp; _memePreview.IsActive) { /* hold the caret */ return; }
/// </code>
/// That single early return is what makes this safe, and it is why the caret hold is the only
/// thing a host is allowed to do while a meme is on screen.
/// </para>
///
/// <para>
/// <b>What deliberately does NOT trigger a cutaway.</b> Only forward PLAYBACK crossing an anchor
/// does. Scrubbing, dragging the caret, and every programmatic seek are excluded — detected as a
/// jump larger than <see cref="SeekJumpSec"/> or a backwards move — because a cutaway that fired
/// while the user dragged the playhead would make the timeline impossible to scrub, which is the
/// opposite of the point.
/// </para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class MemePreviewDirector
{
    /// <summary>Where the one mpv host currently is.</summary>
    public enum Phase
    {
        /// <summary>Showing the gameplay. The host's normal tick logic is valid.</summary>
        Idle,
        /// <summary>Swapping the gameplay out for the meme. Picture is black; overlay is up.</summary>
        LoadingMeme,
        /// <summary>The meme is on screen and running. mpv's clock is the MEME's clock.</summary>
        PlayingMeme,
        /// <summary>Swapping the gameplay back in. Picture is black; overlay is up.</summary>
        Returning
    }

    /// <summary>A one-tick move larger than this is a seek, not playback, and never fires a meme.</summary>
    private const double SeekJumpSec = 1.5;

    /// <summary>Tolerance for "the playhead crossed the anchor this tick".</summary>
    private const double CrossEps = 0.001;

    /// <summary>
    /// After returning from a meme the playhead sits ON its anchor, which would re-arm the very
    /// meme that just played. Suppression clears once the gameplay has moved this far past it.
    /// </summary>
    private const double ReArmGuardSec = 0.30;

    private const int LoadTimeoutMs = 4000;
    private const int ReturnTimeoutMs = 5000;
    private const int PollMs = 25;

    private readonly Func<MpvIpcClient?> _client;
    private readonly Func<string?> _sourcePath;
    private readonly Func<double> _trimStartSec;
    private readonly Action<bool, string> _setBusy;
    private readonly string _logTag;

    private readonly List<MemePlacement> _memes = new();
    private readonly Stopwatch _memeClock = new();

    private double _lastSourceSec = double.NaN;
    private string? _suppressId;
    private double _suppressUntilSourceSec;
    private bool _resumePaused;
    private string? _savedCrop;
    private string? _savedVf;
    private string? _savedImageDuration;
    private string? _savedSpeed;
    private int _inFlight;

    /// <summary>Raised on the UI thread the moment the gameplay is handed over to a meme.</summary>
    public event Action? MemeStarted;

    /// <summary>Raised on the UI thread once the gameplay is back and playable.</summary>
    public event Action? MemeEnded;

    public Phase CurrentPhase { get; private set; } = Phase.Idle;

    /// <summary>True whenever mpv is NOT showing the gameplay. Host ticks must early-return on this.</summary>
    public bool IsActive => CurrentPhase != Phase.Idle;

    /// <summary>True only while the meme picture is actually on screen and running.</summary>
    public bool IsPlayingMeme => CurrentPhase == Phase.PlayingMeme;

    /// <summary>Absolute source seconds of the gameplay frame the current cutaway is parked on.</summary>
    public double AnchorAbsSourceSec { get; private set; }

    /// <summary>How far into the current meme playback is, in seconds. Zero when idle.</summary>
    public double MemeElapsedSec { get; private set; }

    /// <summary>The current meme's length — the placement's own value, which is what the export uses.</summary>
    public double MemeDurationSec { get; private set; }

    public string? ActiveMemeId { get; private set; }
    public string? ActiveMemeFile { get; private set; }

    /// <summary>
    /// Set by the host while the user is scrubbing, recording, or otherwise owns the playhead.
    /// A suspended director never STARTS a cutaway; one already running still finishes cleanly.
    /// </summary>
    public bool Suspended { get; set; }

    public MemePreviewDirector(
        Func<MpvIpcClient?> client,
        Func<string?> sourcePath,
        Func<double> trimStartSec,
        Action<bool, string> setBusy,
        string logTag)
    {
        _client = client;
        _sourcePath = sourcePath;
        _trimStartSec = trimStartSec;
        _setBusy = setBusy;
        _logTag = logTag;
    }

    /// <summary>
    /// Replaces the placement list. Safe to call as often as the host likes — it only rebuilds when
    /// the set genuinely changed, and it never interrupts a cutaway that is already running.
    /// </summary>
    public bool SetMemes(IReadOnlyList<MemePlacement>? memes)
    {
        string before = Signature();
        _memes.Clear();
        if (memes != null)
        {
            foreach (var m in memes)
                if (m.DurationSec > 0.001) _memes.Add(m);
        }
        _memes.Sort((a, b) => a.AtSourceSecRelative.CompareTo(b.AtSourceSecRelative));

        bool changed = Signature() != before;
        if (changed)
        {
            // A moved meme is a different meme as far as re-arming goes; drop any stale suppression
            // so the new position fires the first time playback reaches it.
            _suppressId = null;
            _suppressUntilSourceSec = 0;
        }
        return changed;
    }

    private string Signature()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var m in _memes)
            sb.Append(m.Id).Append(':')
              .Append(m.AtSourceSecRelative.ToString("0.####", CultureInfo.InvariantCulture)).Append(':')
              .Append(m.DurationSec.ToString("0.####", CultureInfo.InvariantCulture)).Append(';');
        return sb.ToString();
    }

    /// <summary>
    /// Tells the director the playhead was moved deliberately (a seek, a scrub, a jump out of a
    /// cut). Resets the crossing tracker so the move itself cannot be mistaken for playback.
    /// </summary>
    public void NotifySeek()
    {
        _lastSourceSec = double.NaN;
    }

    /// <summary>Drives the state machine. Call once per playback tick, on the UI thread.</summary>
    public void Tick()
    {
        var ipc = _client();
        if (ipc == null) return;

        try
        {
            switch (CurrentPhase)
            {
                case Phase.Idle: TickIdle(ipc); break;
                case Phase.PlayingMeme: TickPlaying(ipc); break;
                default: break;   // the two swap phases are driven by their own async worker
            }
        }
        catch (Exception ex) { RuntimeLog.SwallowedThrottled(ex); }
    }

    private void TickIdle(MpvIpcClient ipc)
    {
        double now = ipc.CurrentTime;
        double prev = _lastSourceSec;
        _lastSourceSec = now;

        if (_memes.Count == 0 || Suspended) return;
        if (double.IsNaN(prev)) return;          // first tick after a seek — nothing crossed yet
        if (ipc.IsPaused) return;                // a cutaway is a PLAYBACK event, never a parked one

        double delta = now - prev;
        if (delta <= 0 || delta > SeekJumpSec) return;   // backwards or a jump: a seek, not playback

        double trimStart = _trimStartSec();
        foreach (var m in _memes)
        {
            double anchor = trimStart + m.AtSourceSecRelative;
            if (anchor <= prev + CrossEps || anchor > now + CrossEps) continue;
            if (_suppressId == m.Id && now < _suppressUntilSourceSec) continue;

            // Set the phase SYNCHRONOUSLY, before the worker is launched: Tick runs on the UI
            // thread every ~33ms and would otherwise fire the same meme several times over.
            CurrentPhase = Phase.LoadingMeme;
            _ = RunMemeAsync(ipc, m, anchor);
            return;
        }
    }

    private void TickPlaying(MpvIpcClient ipc)
    {
        // Mirror mpv's own pause into the meme clock, so pausing during a meme pauses the meme
        // rather than letting it silently time out behind a frozen picture.
        if (ipc.IsPaused) { if (_memeClock.IsRunning) _memeClock.Stop(); }
        else if (!_memeClock.IsRunning) _memeClock.Start();

        double elapsed = _memeClock.Elapsed.TotalSeconds;
        MemeElapsedSec = Math.Min(elapsed, MemeDurationSec);

        // The declared length is what the EXPORT uses, so it is what the preview honours. eof is a
        // second opinion for a meme whose real length is shorter than its probe said, and the last
        // clause is a backstop against a file that never reports eof at all.
        bool done = elapsed >= MemeDurationSec - 0.03
                    || (ipc.IsEof && elapsed > 0.15)
                    || elapsed > MemeDurationSec + 5.0;

        if (!done) return;

        CurrentPhase = Phase.Returning;
        _ = ReturnToSourceAsync(ipc);
    }

    private async Task RunMemeAsync(MpvIpcClient ipc, MemePlacement meme, double anchorAbs)
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return;
        try
        {
            ActiveMemeId = meme.Id;
            ActiveMemeFile = meme.FilePath;
            MemeDurationSec = Math.Max(0.05, meme.DurationSec);
            MemeElapsedSec = 0;
            AnchorAbsSourceSec = anchorAbs;
            _resumePaused = ipc.IsPaused;

            _setBusy(true, "Loading the meme…");
            MemeStarted?.Invoke();

            // Park the gameplay ON the anchor first. If anything below fails, the preview is still
            // sitting exactly where the meme belongs instead of wherever playback had drifted to.
            await SafeSet(ipc, "pause", "yes");
            try { await ipc.SetPropertyDoubleAsync("time-pos", anchorAbs); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }

            // A zoom crop or a video filter belongs to the GAMEPLAY. The export concatenates the
            // meme untouched, so cropping it here would show the user something that will not
            // happen. Saved, cleared, and put back on the way home.
            _savedCrop = SafeGet(ipc, "video-crop");
            _savedVf = SafeGet(ipc, "vf");
            _savedImageDuration = SafeGet(ipc, "image-display-duration");
            await SafeSet(ipc, "video-crop", "");
            await SafeSet(ipc, "vf", "");

            // ⚠️ AND THE PLAYBACK RATE. mpv's `speed` is global and survives a loadfile, so a meme
            // that fires while the gameplay is inside a 2x block would play at 2x here and at 1x in
            // the export — the preview would be lying about the one thing it exists to show. The
            // export concatenates the meme at its own natural rate, so the preview does too.
            _savedSpeed = SafeGet(ipc, "speed");
            await SafeSet(ipc, "speed", "1.0");

            // A still image has no intrinsic length; mpv must be told to hold it for exactly as
            // long as the export will, or it flashes past in mpv's default second.
            await SafeSet(ipc, "image-display-duration",
                MemeDurationSec.ToString("F3", CultureInfo.InvariantCulture));

            await ipc.LoadFileAsync(meme.FilePath, 0);
            await WaitUntilAsync(() => ipc.Duration > 0 || ipc.CurrentTime > 0.02, LoadTimeoutMs);

            _memeClock.Restart();
            CurrentPhase = Phase.PlayingMeme;
            _setBusy(false, "");
            await SafeSet(ipc, "pause", "no");

            RuntimeLog.Info(_logTag,
                $"Meme cutaway started: '{System.IO.Path.GetFileName(meme.FilePath)}' " +
                $"({MemeDurationSec:0.###}s) at {anchorAbs:0.###}s source.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail(_logTag, $"Meme cutaway failed to start ({ex.Message}); returning to the video.");
            CurrentPhase = Phase.Returning;
            Interlocked.Exchange(ref _inFlight, 0);
            await ReturnToSourceAsync(ipc);
            return;
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    private async Task ReturnToSourceAsync(MpvIpcClient ipc)
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return;
        try
        {
            _memeClock.Stop();
            _setBusy(true, "Back to your video…");

            await SafeSet(ipc, "pause", "yes");
            await SafeSet(ipc, "image-display-duration", _savedImageDuration ?? "1");
            await SafeSet(ipc, "video-crop", _savedCrop ?? "");
            await SafeSet(ipc, "vf", _savedVf ?? "");
            await SafeSet(ipc, "speed", _savedSpeed ?? "1.0");

            string? src = _sourcePath();
            if (!string.IsNullOrWhiteSpace(src))
            {
                await ipc.LoadFileAsync(src!, AnchorAbsSourceSec);
                await WaitUntilAsync(
                    () => ipc.Duration > 0 && Math.Abs(ipc.CurrentTime - AnchorAbsSourceSec) < 0.75,
                    ReturnTimeoutMs);
            }

            // The playhead is now ON the anchor, which is exactly the condition that fires this
            // meme. Suppress it until the gameplay has genuinely moved past.
            _suppressId = ActiveMemeId;
            _suppressUntilSourceSec = AnchorAbsSourceSec + ReArmGuardSec;
            _lastSourceSec = ipc.CurrentTime;

            await SafeSet(ipc, "pause", _resumePaused ? "yes" : "no");

            RuntimeLog.Info(_logTag, $"Meme cutaway finished; gameplay resumed at {AnchorAbsSourceSec:0.###}s.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail(_logTag, $"Meme cutaway could not return cleanly: {ex.Message}");
        }
        finally
        {
            MemeElapsedSec = 0;
            ActiveMemeId = null;
            ActiveMemeFile = null;
            CurrentPhase = Phase.Idle;
            _setBusy(false, "");
            Interlocked.Exchange(ref _inFlight, 0);
            MemeEnded?.Invoke();
        }
    }

    /// <summary>
    /// Puts mpv back on the gameplay unconditionally. Called when a window closes or when the
    /// placements change underneath a running cutaway.
    /// </summary>
    public async Task AbortAsync()
    {
        if (CurrentPhase == Phase.Idle) return;
        var ipc = _client();
        if (ipc == null) { CurrentPhase = Phase.Idle; _setBusy(false, ""); return; }
        CurrentPhase = Phase.Returning;
        await ReturnToSourceAsync(ipc);
    }

    private static string? SafeGet(MpvIpcClient ipc, string name)
    {
        try { return ipc.GetPropertyString(name); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); return null; }
    }

    /// <summary>
    /// One bad property must never abort a swap half-finished — that would strand the user on a
    /// black screen with the gameplay unloaded. Every property write is individually survivable.
    /// </summary>
    private static async Task SafeSet(MpvIpcClient ipc, string name, string value)
    {
        try { await ipc.SetPropertyAsync(name, value); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private static async Task WaitUntilAsync(Func<bool> ready, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            bool ok;
            try { ok = ready(); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); ok = false; }
            if (ok) return;
            await Task.Delay(PollMs);
        }
    }
}
