// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Ipc;

public static class NamedPipeStateClient
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan FastProbeTimeout = TimeSpan.FromMilliseconds(30);

    private static NamedPipeClientStream CreateClientStream(bool async) =>
        new NamedPipeClientStream(
            ".",
            IpcProtocol.PipeName,
            PipeDirection.InOut,
            async ? PipeOptions.Asynchronous : PipeOptions.None,
            TokenImpersonationLevel.Identification);

    public static async Task<JsonObject?> GetStateAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        TimeSpan actualTimeout = timeout ?? DefaultTimeout;
        try
        {
            using var client = CreateClientStream(true);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(actualTimeout);

            await client.ConnectAsync(cts.Token).ConfigureAwait(false);

            await IpcProtocol.WriteFrameAsync(client, IpcOpcode.GetState, ReadOnlyMemory<byte>.Empty, cts.Token).ConfigureAwait(false);
            var response = await IpcProtocol.ReadFrameAsync(client, cts.Token).ConfigureAwait(false);

            if (response != null && response.Value.Opcode == IpcOpcode.GetStateAck)
            {
                return IpcProtocol.FromUtf8Bytes(response.Value.Payload);
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("IpcClient", $"GetStateAsync failed: {ex.Message}");
            return null;
        }
    }

    public static JsonObject? GetStateSync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        TimeSpan actualTimeout = timeout ?? DefaultTimeout;
        try
        {
            using var client = CreateClientStream(false);
            client.Connect((int)Math.Max(1, actualTimeout.TotalMilliseconds));

            IpcProtocol.WriteFrame(client, IpcOpcode.GetState, ReadOnlySpan<byte>.Empty);
            var response = IpcProtocol.ReadFrame(client);

            if (response != null && response.Value.Opcode == IpcOpcode.GetStateAck)
            {
                return IpcProtocol.FromUtf8Bytes(response.Value.Payload);
            }

            return null;
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("IpcClient", $"GetStateSync failed: {ex.Message}");
            return null;
        }
    }

    public static async Task<bool> UpdatePropertiesAsync(JsonObject updates, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        TimeSpan actualTimeout = timeout ?? DefaultTimeout;
        try
        {
            using var client = CreateClientStream(true);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(actualTimeout);

            await client.ConnectAsync(cts.Token).ConfigureAwait(false);

            await IpcProtocol.WriteJsonFrameAsync(client, IpcOpcode.UpdateProperties, updates, cts.Token).ConfigureAwait(false);
            var response = await IpcProtocol.ReadFrameAsync(client, cts.Token).ConfigureAwait(false);

            return response != null && response.Value.Opcode == IpcOpcode.UpdatePropertiesAck;
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("IpcClient", $"UpdatePropertiesAsync failed: {ex.Message}");
            return false;
        }
    }

    public static bool UpdatePropertiesSync(JsonObject updates, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        TimeSpan actualTimeout = timeout ?? DefaultTimeout;
        try
        {
            using var client = CreateClientStream(false);
            client.Connect((int)Math.Max(1, actualTimeout.TotalMilliseconds));

            IpcProtocol.WriteJsonFrame(client, IpcOpcode.UpdateProperties, updates);
            var response = IpcProtocol.ReadFrame(client);

            return response != null && response.Value.Opcode == IpcOpcode.UpdatePropertiesAck;
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("IpcClient", $"UpdatePropertiesSync failed: {ex.Message}");
            return false;
        }
    }

    public static async Task<bool> SaveStateAsync(JsonObject state, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        TimeSpan actualTimeout = timeout ?? DefaultTimeout;
        try
        {
            using var client = CreateClientStream(true);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(actualTimeout);

            await client.ConnectAsync(cts.Token).ConfigureAwait(false);

            await IpcProtocol.WriteJsonFrameAsync(client, IpcOpcode.SaveState, state, cts.Token).ConfigureAwait(false);
            var response = await IpcProtocol.ReadFrameAsync(client, cts.Token).ConfigureAwait(false);

            return response != null && response.Value.Opcode == IpcOpcode.SaveStateAck;
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("IpcClient", $"SaveStateAsync failed: {ex.Message}");
            return false;
        }
    }

    public static bool SaveStateSync(JsonObject state, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        TimeSpan actualTimeout = timeout ?? DefaultTimeout;
        try
        {
            using var client = CreateClientStream(false);
            client.Connect((int)Math.Max(1, actualTimeout.TotalMilliseconds));

            IpcProtocol.WriteJsonFrame(client, IpcOpcode.SaveState, state);
            var response = IpcProtocol.ReadFrame(client);

            return response != null && response.Value.Opcode == IpcOpcode.SaveStateAck;
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("IpcClient", $"SaveStateSync failed: {ex.Message}");
            return false;
        }
    }

    public static async Task<bool> ClearStateAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        TimeSpan actualTimeout = timeout ?? DefaultTimeout;
        try
        {
            using var client = CreateClientStream(true);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(actualTimeout);

            await client.ConnectAsync(cts.Token).ConfigureAwait(false);

            await IpcProtocol.WriteFrameAsync(client, IpcOpcode.ClearState, ReadOnlyMemory<byte>.Empty, cts.Token).ConfigureAwait(false);
            var response = await IpcProtocol.ReadFrameAsync(client, cts.Token).ConfigureAwait(false);

            return response != null && response.Value.Opcode == IpcOpcode.ClearStateAck;
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("IpcClient", $"ClearStateAsync failed: {ex.Message}");
            return false;
        }
    }

    public static bool ClearStateSync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        TimeSpan actualTimeout = timeout ?? DefaultTimeout;
        try
        {
            using var client = CreateClientStream(false);
            client.Connect((int)Math.Max(1, actualTimeout.TotalMilliseconds));

            IpcProtocol.WriteFrame(client, IpcOpcode.ClearState, ReadOnlySpan<byte>.Empty);
            var response = IpcProtocol.ReadFrame(client);

            return response != null && response.Value.Opcode == IpcOpcode.ClearStateAck;
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("IpcClient", $"ClearStateSync failed: {ex.Message}");
            return false;
        }
    }

    public static async Task<bool> SendHandoffAsync(JsonObject handoffPayload, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        TimeSpan actualTimeout = timeout ?? DefaultTimeout;
        try
        {
            using var client = CreateClientStream(true);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(actualTimeout);

            await client.ConnectAsync(cts.Token).ConfigureAwait(false);

            await IpcProtocol.WriteJsonFrameAsync(client, IpcOpcode.Handoff, handoffPayload, cts.Token).ConfigureAwait(false);
            var response = await IpcProtocol.ReadFrameAsync(client, cts.Token).ConfigureAwait(false);

            return response != null && response.Value.Opcode == IpcOpcode.HandoffAck;
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("IpcClient", $"SendHandoffAsync failed: {ex.Message}");
            return false;
        }
    }

    public static bool SendHandoffSync(JsonObject handoffPayload, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        TimeSpan actualTimeout = timeout ?? DefaultTimeout;
        try
        {
            using var client = CreateClientStream(false);
            client.Connect((int)Math.Max(1, actualTimeout.TotalMilliseconds));

            IpcProtocol.WriteJsonFrame(client, IpcOpcode.Handoff, handoffPayload);
            var response = IpcProtocol.ReadFrame(client);

            return response != null && response.Value.Opcode == IpcOpcode.HandoffAck;
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("IpcClient", $"SendHandoffSync failed: {ex.Message}");
            return false;
        }
    }

    public static async Task<bool> PingAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        TimeSpan actualTimeout = timeout ?? FastProbeTimeout;
        try
        {
            using var client = CreateClientStream(true);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(actualTimeout);

            await client.ConnectAsync(cts.Token).ConfigureAwait(false);

            await IpcProtocol.WriteFrameAsync(client, IpcOpcode.Ping, ReadOnlyMemory<byte>.Empty, cts.Token).ConfigureAwait(false);
            var response = await IpcProtocol.ReadFrameAsync(client, cts.Token).ConfigureAwait(false);

            return response != null && response.Value.Opcode == IpcOpcode.Pong;
        }
        catch
        {
            return false;
        }
    }
}
