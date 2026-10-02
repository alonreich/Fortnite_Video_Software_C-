using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace FreeVideoStudio.App.Services;

/// <summary>GRANULARPERF_01 — one writer, one pending snapshot, and a final ordered close marker.</summary>
public sealed class EditorRecoveryWriter
{
    private readonly Channel<JsonObject> _pending = Channel.CreateBounded<JsonObject>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Task _worker;

    public EditorRecoveryWriter(Action<JsonObject?> write)
    {
        _worker = Task.Run(async () =>
        {
            await foreach (var snapshot in _pending.Reader.ReadAllAsync())
                TryWrite(snapshot);
            TryWrite(null);

            void TryWrite(JsonObject? snapshot)
            {
                try { write(snapshot); }
                catch (Exception ex) { RuntimeLog.Fail("Granular recovery", ex); }
            }
        });
    }

    public void Request(JsonObject snapshot) => _pending.Writer.TryWrite(snapshot);
    public Task FinishAsync()
    {
        _pending.Writer.TryComplete();
        return _worker;
    }
}
