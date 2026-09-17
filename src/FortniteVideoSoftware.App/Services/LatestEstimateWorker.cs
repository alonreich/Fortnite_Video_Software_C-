// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System.Threading.Channels;

namespace FortniteVideoSoftware.App.Services;

/// <summary>SIZEESTIMATE_01 — one worker and one pending snapshot per window; never blocks the UI.</summary>
public sealed class LatestEstimateWorker<T> : IDisposable
{
    private readonly Channel<(long Version, T Snapshot)> _requests = Channel.CreateBounded<(long, T)>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<T, CancellationToken, Task<OutputSizeEstimate>> _calculate;
    private readonly Action<OutputSizeEstimate> _publish;
    private readonly Action<Action> _dispatch;
    private readonly Func<T, OutputSizeEstimate?>? _quickEstimate;
    private readonly object _lifetimeGate = new();
    private long _version;
    private bool _disposed;
    public Task Completion { get; }

    public LatestEstimateWorker(Func<T, CancellationToken, Task<OutputSizeEstimate>> calculate,
        Action<OutputSizeEstimate> publish, Action<Action> dispatch, Func<T, OutputSizeEstimate?>? quickEstimate = null)
    {
        _calculate = calculate; _publish = publish; _dispatch = dispatch;
        _quickEstimate = quickEstimate;
        Completion = Task.Run(RunAsync);
    }

    public void Request(T snapshot)
    {
        lock (_lifetimeGate)
        {
            if (_disposed) return;
            _requests.Writer.TryWrite((++_version, snapshot));
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (await _requests.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                // Throttle rather than wait for dragging to stop: continuous gestures still update.
                await Task.Delay(80, _stop.Token).ConfigureAwait(false);
                if (!_requests.Reader.TryRead(out var request)) continue;
                OutputSizeEstimate result;
                try
                {
                    if (_quickEstimate?.Invoke(request.Snapshot) is { } quick) Publish(request.Version, quick);
                    result = await _calculate(request.Snapshot, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    RuntimeLog.Debug("SIZE ESTIMATE", ex.Message);
                    result = OutputSizeEstimate.Empty;
                }
                Publish(request.Version, result);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            lock (_lifetimeGate) { _disposed = true; _stop.Dispose(); }
        }
    }

    private void Publish(long version, OutputSizeEstimate result) => _dispatch(() =>
    {
        lock (_lifetimeGate)
        {
            if (!_disposed && version == _version) _publish(result);
        }
    });

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposed) return;
            _disposed = true;
            _requests.Writer.TryComplete();
            _stop.Cancel(); // The worker owns disposal after any ffprobe has exited.
        }
    }
}
