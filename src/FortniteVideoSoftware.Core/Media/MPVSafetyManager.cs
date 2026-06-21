using System.Diagnostics;
using System.Threading.Channels;
using System.Globalization;

namespace FortniteVideoSoftware.Core.Media;

public class MPVSafetyManager : IDisposable
{
    private readonly nint _mpvHandle;
    private readonly Channel<double> _seekChannel;
    private readonly CancellationTokenSource _cts;
    private readonly Thread _workerThread;
    
    private readonly object _stateLock = new();
    private bool _isSeeking;
    private DateTime _seekStartTime;

    public MPVSafetyManager(nint mpvHandle)
    {
        _mpvHandle = mpvHandle;
        
        // Channel with bounded capacity and drop oldest ensures debouncing implicitly if we pump fast,
        // but we'll use a timer for strict 50ms throttling.
        _seekChannel = Channel.CreateBounded<double>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        
        _cts = new CancellationTokenSource();

        _workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "MPV_Safety_Watchdog"
        };
        _workerThread.Start();
    }

    public void RequestSeek(double timeSeconds)
    {
        _seekChannel.Writer.TryWrite(timeSeconds);
    }

    private void WorkerLoop()
    {
        // Thread for processing seeks
        Thread seekThread = new Thread(SeekProcessorLoop)
        {
            IsBackground = true,
            Name = "MPV_Seek_Processor"
        };
        seekThread.Start();

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                bool isStuck = false;
                lock (_stateLock)
                {
                    if (_isSeeking)
                    {
                        TimeSpan elapsed = DateTime.UtcNow - _seekStartTime;
                        if (elapsed.TotalSeconds > 2.5)
                        {
                            isStuck = true;
                        }
                    }
                }

                if (isStuck)
                {
                    // Force reset internal seek state machine to unblock UI
                    lock (_stateLock)
                    {
                        _isSeeking = false;
                    }
                    
                    Console.Error.WriteLine("MPV WATCHDOG TRIPPED: Seek took longer than 2.5s. Resetting state.");
                }

                Thread.Sleep(500); // Check every 500ms
            }
        }
        catch (ObjectDisposedException) { }
    }

    private async void SeekProcessorLoop()
    {
        Stopwatch debounceTimer = Stopwatch.StartNew();
        
        try
        {
            await foreach (double time in _seekChannel.Reader.ReadAllAsync(_cts.Token))
            {
                // Throttle to 20fps (50ms)
                long elapsedMs = debounceTimer.ElapsedMilliseconds;
                if (elapsedMs < 50)
                {
                    await Task.Delay(50 - (int)elapsedMs, _cts.Token);
                }
                
                debounceTimer.Restart();

                lock (_stateLock)
                {
                    _isSeeking = true;
                    _seekStartTime = DateTime.UtcNow;
                }

                // Execute native seek
                try
                {
                    string pct = time.ToString("F1", CultureInfo.InvariantCulture);
                    MpvWrapper.mpv_command_string(_mpvHandle, $"seek {pct} absolute-percent");
                }
                catch
                {
                    lock (_stateLock)
                    {
                        _isSeeking = false;
                    }
                }
                finally
                {
                    lock (_stateLock)
                    {
                        _isSeeking = false;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
