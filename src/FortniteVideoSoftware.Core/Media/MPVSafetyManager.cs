using System.Diagnostics;
using System.Threading.Channels;
using System.Globalization;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// Absorbs a rapid-scrub seek storm into ONE issued mpv seek, and watches for a seek that never
/// lands.
///
/// <para><b>MPVSAFETY_01 — TEARDOWN ORDER IS SIGNAL, WAIT, THEN FREE. IN THAT ORDER, ALWAYS.</b></para>
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// WHAT WAS WRONG: <c>Dispose()</c> was two lines — <c>_cts.Cancel()</c> immediately followed by
/// <c>_cts.Dispose()</c>. At that instant TWO threads were still bound to <c>_cts.Token</c>: the
/// watchdog polling <c>IsCancellationRequested</c> on a 500 ms <c>Thread.Sleep</c> cycle, and the
/// seek processor suspended inside <c>ReadAllAsync(_cts.Token)</c> or <c>Task.Delay(..., token)</c>.
/// Freeing the source underneath live registrations makes those consumers observe
/// <see cref="ObjectDisposedException"/>, NOT <see cref="OperationCanceledException"/>.
///
/// The watchdog already guarded that case. The seek processor did not — and it was declared
/// <c>async void</c> and started from a raw <see cref="Thread"/>, so it had no
/// SynchronizationContext to rethrow onto. The exception surfaced on the thread pool as an
/// unobserved top-level throw, which under the .NET default policy FAST-FAILS THE PROCESS. A user
/// closing a window mid-scrub could lose the whole app with no managed stack anywhere.
///
/// Neither thread was joined either, so <c>_mpvHandle</c> could be commanded after its owner had
/// already destroyed it — a native access violation (0xC0000005) that no managed catch can contain.
///
/// THE FIX is the ordering <c>Ipc/NamedPipeStateServer.Dispose</c> already proves under
/// IPCTEARDOWN_01, plus the handle-ownership discipline <c>MpvIpcClient.Dispose</c> proves under
/// ISSUE_13: stop producing, signal, WAIT (bounded) for every consumer to actually exit, and only
/// then free what they were using. There is no <c>async void</c> left in this file.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </para>
/// </summary>
public class MPVSafetyManager : IDisposable
{
    /// <summary>MPVSAFETY_01 — minimum spacing between two ISSUED seeks. Tuned; do not raise.</summary>
    private const int SeekDebounceMs = 50;

    /// <summary>MPVSAFETY_01 — a seek still outstanding after this long is considered stuck.</summary>
    private const double StuckSeekSeconds = 2.5;

    /// <summary>MPVSAFETY_01 — watchdog cadence. The stop signal short-circuits the wait, so this
    /// is a poll interval, NOT a floor on teardown latency.</summary>
    private const int WatchdogPollMs = 500;

    /// <summary>MPVSAFETY_01 — a consumer that will not stop must not hang the caller's shutdown.</summary>
    private static readonly TimeSpan TeardownJoinCeiling = TimeSpan.FromSeconds(2);

    private nint _mpvHandle;
    private readonly Channel<double> _seekChannel;
    private readonly CancellationTokenSource _cts;
    private readonly Thread _watchdogThread;
    private readonly Thread _seekThread;

    /// <summary>MPVSAFETY_01 — set once the owner hands the handle back or teardown begins. Checked
    /// immediately before every native command so a destroyed handle is never dereferenced.</summary>
    private volatile bool _handleValid;

    /// <summary>MPVSAFETY_01 — the ONLY reliable signal that each loop has genuinely stopped
    /// touching <c>_cts</c> and <c>_mpvHandle</c>. Same contract as MpvIpcClient._eventLoopExited.</summary>
    private volatile bool _watchdogExited;
    private volatile bool _seekLoopExited;

    /// <summary>MPVSAFETY_01 — unblocks the watchdog's wait the instant teardown starts, so a join
    /// never costs a full poll interval.</summary>
    private readonly ManualResetEventSlim _stopSignal = new(false);

    private int _disposed;

    private readonly object _stateLock = new();
    private bool _isSeeking;
    private DateTime _seekStartTime;
    private double _seekTargetPercent;

    public MPVSafetyManager(nint mpvHandle)
    {
        _mpvHandle = mpvHandle;
        _handleValid = mpvHandle != nint.Zero;

        // MPVSAFETY_01 — capacity 1 + DropOldest IS the coalescing policy this class exists for:
        // during a scrub burst only the NEWEST target survives. Do not widen the buffer and do not
        // switch to DropWrite; either one reinstates the seek storm.
        _seekChannel = Channel.CreateBounded<double>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

        _cts = new CancellationTokenSource();

        _seekThread = new Thread(SeekProcessorThreadBody)
        {
            IsBackground = true,
            Name = "MPV_Seek_Processor"
        };

        _watchdogThread = new Thread(WatchdogLoop)
        {
            IsBackground = true,
            Name = "MPV_Safety_Watchdog"
        };

        _seekThread.Start();
        _watchdogThread.Start();
    }

    /// <summary>
    /// Fire-and-forget, non-blocking, allocation-free. Safe to call from an input handler on the UI
    /// thread and safe to call after <see cref="Dispose"/> (it becomes a no-op).
    /// </summary>
    public void RequestSeek(double timeSeconds)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _seekChannel.Writer.TryWrite(timeSeconds);
    }

    /// <summary>
    /// MPVSAFETY_01 — the handle owner calls this BEFORE destroying the mpv handle, so an in-flight
    /// seek can never command through a freed pointer. <see cref="Dispose"/> calls it too; calling
    /// both, in either order, is safe.
    /// </summary>
    public void InvalidateHandle()
    {
        _handleValid = false;
        _mpvHandle = nint.Zero;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // SEEK PROCESSOR
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// MPVSAFETY_01 — the thread body. It exists so the loop can be <c>async Task</c> instead of
    /// <c>async void</c> while the thread KEEPS ITS NAME (crash digests are read by that name), and
    /// so every possible throw is terminated here rather than on the thread pool. Blocking on the
    /// task is correct on a thread we own and started for exactly this purpose: there is no
    /// SynchronizationContext to deadlock against and no pool worker is being starved.
    /// </summary>
    private void SeekProcessorThreadBody()
    {
        try
        {
            SeekProcessorLoopAsync().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            // MPVSAFETY_01 — the token source was freed underneath us. That is a teardown, not a
            // fault. This is the exact throw that used to kill the process.
        }
        catch (ChannelClosedException)
        {
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("MPV", $"The seek processor stopped unexpectedly: {ex.GetType().Name}: {ex.Message}");
            CoreLogger.Debug("MPV", ex.ToString());
        }
        finally
        {
            lock (_stateLock) { _isSeeking = false; }
            _seekLoopExited = true;
        }
    }

    private async Task SeekProcessorLoopAsync()
    {
        CancellationToken token = _cts.Token;
        Stopwatch debounceTimer = Stopwatch.StartNew();

        await foreach (double time in _seekChannel.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            if (token.IsCancellationRequested) break;

            // MPVSAFETY_01 — the 50 ms floor between ISSUED seeks. Tuned constant; the burst that
            // arrives during the wait is coalesced by the capacity-1 DropOldest channel above, so
            // the target read on the next iteration is always the newest one.
            long elapsedMs = debounceTimer.ElapsedMilliseconds;
            if (elapsedMs < SeekDebounceMs)
            {
                await Task.Delay(SeekDebounceMs - (int)elapsedMs, token).ConfigureAwait(false);
            }

            debounceTimer.Restart();
            if (token.IsCancellationRequested) break;

            lock (_stateLock)
            {
                _isSeeking = true;
                _seekStartTime = DateTime.UtcNow;
                _seekTargetPercent = time;
            }

            try
            {
                IssueSeek(time);
            }
            catch (Exception ex)
            {
                CoreLogger.Swallowed(ex);
            }
            finally
            {
                lock (_stateLock) { _isSeeking = false; }
            }
        }
    }

    /// <summary>
    /// MPVSAFETY_01 — the ONLY place the native handle is dereferenced, and it is gated on
    /// <see cref="_handleValid"/>. The <c>"F1"</c> / InvariantCulture formatting is load-bearing:
    /// mpv's command-string parser reads an invariant decimal point, and a comma from a localised
    /// format produces a command it silently fails to parse.
    /// </summary>
    private void IssueSeek(double percent)
    {
        if (!_handleValid) return;

        nint handle = _mpvHandle;
        if (handle == nint.Zero) return;

        string pct = percent.ToString("F1", CultureInfo.InvariantCulture);
        MpvWrapper.mpv_command_string(handle, $"seek {pct} absolute-percent");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // WATCHDOG
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// MPVSAFETY_01 — trips when a seek has been outstanding past <see cref="StuckSeekSeconds"/>.
    ///
    /// <para>It used to report through <c>Console.Error.WriteLine</c>, which in a windowed Avalonia
    /// process goes nowhere at all — a tripped watchdog left no artifact on any machine that was
    /// not running under a console. It now reports through <see cref="CoreLogger"/> like every
    /// other diagnostic in this assembly, and names the target it gave up on.</para>
    /// </summary>
    private void WatchdogLoop()
    {
        try
        {
            // MPVSAFETY_01 — Wait returns true the moment teardown signals, so Dispose never waits
            // out a poll interval. This replaces the old unconditional Thread.Sleep(500).
            while (!_stopSignal.Wait(WatchdogPollMs))
            {
                bool stuck = false;
                double target = 0;

                lock (_stateLock)
                {
                    if (_isSeeking && (DateTime.UtcNow - _seekStartTime).TotalSeconds > StuckSeekSeconds)
                    {
                        stuck = true;
                        target = _seekTargetPercent;
                        _isSeeking = false;
                    }
                }

                if (stuck)
                {
                    CoreLogger.Fail("MPV",
                        $"Seek watchdog tripped: a seek to {target.ToString("F1", CultureInfo.InvariantCulture)}% " +
                        $"did not complete within {StuckSeekSeconds.ToString("F1", CultureInfo.InvariantCulture)}s. Seek state reset.");
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
        }
        finally
        {
            _watchdogExited = true;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // TEARDOWN
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// MPVSAFETY_01 — idempotent, never throws, and NEVER frees the token source while a consumer
    /// can still touch it. See the class remarks for the defect this ordering closes.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // 1. No more native commands, whatever else happens below.
        _handleValid = false;

        // 2. Stop producing. TryWrite becomes a no-op and ReadAllAsync will end its enumeration.
        try { _seekChannel.Writer.TryComplete(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }

        // 3. Signal both consumers.
        try { _stopSignal.Set(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }
        try { _cts.Cancel(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }

        // 4. WAIT for them to actually finish. Bounded — a wedged consumer must not hang shutdown.
        bool seekStopped = JoinBounded(_seekThread, "MPV_Seek_Processor");
        bool watchdogStopped = JoinBounded(_watchdogThread, "MPV_Safety_Watchdog");

        bool seekAccountedFor = seekStopped || _seekLoopExited;
        bool watchdogAccountedFor = watchdogStopped || _watchdogExited;

        if (!seekAccountedFor || !watchdogAccountedFor)
        {
            // Same precedent as MpvIpcClient: abandon rather than free something a live thread is
            // still reading. The token source and the event leak for the life of the process; that
            // is strictly better than an ObjectDisposedException on a thread with no handler.
            CoreLogger.Fail("MPV",
                "An MPVSafetyManager consumer did not stop in time — abandoning its synchronisation " +
                "objects instead of freeing them underneath a live thread.");
            _mpvHandle = nint.Zero;
            return;
        }

        // 5. Only now is nothing still using these.
        try { _cts.Dispose(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }
        try { _stopSignal.Dispose(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }

        _mpvHandle = nint.Zero;
    }

    private static bool JoinBounded(Thread thread, string name)
    {
        try
        {
            if (thread.Join(TeardownJoinCeiling)) return true;
            CoreLogger.Debug("MPV", $"{name} did not stop within {TeardownJoinCeiling.TotalSeconds:F0}s; continuing teardown.");
            return false;
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return false;
        }
    }
}
