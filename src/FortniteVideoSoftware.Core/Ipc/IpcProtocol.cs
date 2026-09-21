// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Ipc;

public enum IpcOpcode : byte
{
    Ping = 0x01,
    Pong = 0x02,
    GetState = 0x03,
    GetStateAck = 0x04,
    UpdateProperties = 0x05,
    UpdatePropertiesAck = 0x06,
    SaveState = 0x07,
    SaveStateAck = 0x08,
    ClearState = 0x09,
    ClearStateAck = 0x0A,
    Handoff = 0x0B,
    HandoffAck = 0x0C,
    Error = 0xFF
}

public readonly record struct IpcFrame(IpcOpcode Opcode, byte[] Payload);

public class WindowBoundsDto
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public int? WindowState { get; set; }
}

public class HandoffPayload
{
    [JsonPropertyName("source")]
    public string? SourceProcess { get; set; }

    [JsonPropertyName("target")]
    public string? TargetProcess { get; set; }

    [JsonPropertyName("returned_from_crop_tool")]
    public bool? ReturnedFromCropTool { get; set; }

    [JsonPropertyName("selected_clip_path")]
    public string? SelectedClipPath { get; set; }

    [JsonPropertyName("selected_clip_start_ms")]
    public double? SelectedClipStartMs { get; set; }

    [JsonPropertyName("selected_clip_end_ms")]
    public double? SelectedClipEndMs { get; set; }

    [JsonPropertyName("window_bounds")]
    public Dictionary<string, WindowBoundsDto>? WindowBounds { get; set; }

    [JsonPropertyName("properties")]
    public Dictionary<string, string>? Properties { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(HandoffPayload))]
[JsonSerializable(typeof(WindowBoundsDto))]
[JsonSerializable(typeof(Dictionary<string, WindowBoundsDto>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class IpcJsonContext : JsonSerializerContext { }

public static class IpcProtocol
{
    public const uint Magic = 0x50535646; // "FVSP" in Little-Endian ('F'=0x46, 'V'=0x56, 'S'=0x53, 'P'=0x50)
    public const byte Version = 1;
    public const int HeaderSize = 10;
    public const int MaxPayloadSize = 16 * 1024 * 1024; // 16 MB limit

    private static string? _cachedUserScope;

    public static string UserScope
    {
        get
        {
            if (_cachedUserScope != null) return _cachedUserScope;

            string scope;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                    scope = identity.User?.Value ?? identity.Name;
                }
                else
                {
                    scope = Environment.UserName;
                }
            }
            catch (System.Exception swallowed3)
            {
                scope = Environment.UserName;
                global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed3);   // FAULTTIER_02 — no failure is silent.
            }

            _cachedUserScope = scope.Replace('\\', '_').Replace(':', '_').Replace('/', '_');
            return _cachedUserScope;
        }
    }

    public static string PipeName => $"FortniteVideoSoftware_StateIpc_{UserScope}";
    /// <summary>
    /// IPCLEASE_01 — THE SINGLE-SERVER LEASE. IT IS THE OPEN HANDLE, NOT AN ACQUIRED LOCK.
    ///
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// WHAT WAS WRONG: the server created this mutex with <c>initiallyOwned: true</c> — that is,
    /// it OWNED it — and released it in Dispose. A Win32 mutex is THREAD-AFFINE: only the thread
    /// that acquired it may release it. Ownership was taken on whichever thread called TryStart
    /// (startup) and released on whichever thread ran Dispose (shutdown / app teardown), which is
    /// almost never the same one. So <c>ReleaseMutex</c> threw
    ///   "Object synchronization method was called from an unsynchronized block of code"
    /// on EVERY CLEAN SHUTDOWN, was swallowed by a bare catch, and the handle was then closed while
    /// still owned — which marks the mutex ABANDONED. The next launch duly hit the
    /// AbandonedMutexException branch and logged "Prior server process exited abruptly": a
    /// permanently false crash signal on a completely normal exit.
    ///
    /// THE FIX IS TO STOP OWNING IT. A named kernel object lives exactly as long as one handle to
    /// it remains open, so "I hold a handle" is already a perfect lease:
    ///   • <c>new Mutex(initiallyOwned: false, name, out createdNew)</c> — createdNew is true only
    ///     for the process that created it, which is the one that becomes the server.
    ///   • Nothing is ever acquired, so there is nothing to release, no thread affinity, and no
    ///     abandoned state that can exist at all.
    ///   • A crashed server closes its handle with the process; the name frees itself and the next
    ///     launch simply creates it again.
    ///
    /// ⚠️ The NAME is deliberately unchanged. A build from before this fix, still running, OWNS this
    /// mutex — and because <c>initiallyOwned</c> is ignored when the object already exists, old and
    /// new builds still see each other's lease correctly and exactly one of them serves. Renaming it
    /// (or switching to a Semaphore, which cannot share a name with a Mutex) would let two servers
    /// bind the same pipe and silently diverge the session state. Keep the name.
    ///
    /// ⚠️ Mutex is also the only named primitive .NET implements on every platform. Named Semaphore
    /// and EventWaitHandle are Windows-only and throw PlatformNotSupportedException elsewhere, which
    /// takes the IPC test suite with them. Do not substitute one.
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    public static string ServerMutexName => $@"Local\FortniteVideoSoftware_IpcServerMutex_{UserScope}";

    public static bool IsConnectedClientTrusted(NamedPipeServerStream server)
    {
        if (!OperatingSystem.IsWindows()) return true;

        try
        {
            using WindowsIdentity self = WindowsIdentity.GetCurrent();
            string? expected = self.User?.Value;

            bool trusted = false;
            try
            {
                server.RunAsClient(() =>
                {
                    if (!OperatingSystem.IsWindows()) return;
                    using WindowsIdentity peer = WindowsIdentity.GetCurrent();
                    trusted = expected != null && peer.User?.Value == expected;
                });
            }
            catch (System.Exception swallowed2)
            {
                string impersonated = server.GetImpersonationUserName();
                if (!string.IsNullOrEmpty(impersonated))
                {
                trusted = string.Equals(impersonated, Environment.UserName, StringComparison.OrdinalIgnoreCase);
                }
                global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed2);   // FAULTTIER_02 — no failure is silent.
            }

            return trusted;
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("IpcProtocol", $"Could not verify client identity: {ex.Message}");
            return false;
        }
    }

    public static byte[] ToUtf8Bytes(JsonObject? obj)
    {
        if (obj == null) return Array.Empty<byte>();
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            obj.WriteTo(writer);
        }
        return ms.ToArray();
    }

    public static JsonObject? FromUtf8Bytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return new JsonObject();
        try
        {
            var node = JsonNode.Parse(bytes);
            return node as JsonObject ?? new JsonObject();
        }
        catch (JsonException swallowed4)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed4);   // FAULTTIER_02 — no failure is silent.
            return new JsonObject();
        }
    }

    public static void WriteFrame(Stream stream, IpcOpcode opcode, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(0, 4), Magic);
        header[4] = Version;
        header[5] = (byte)opcode;
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(6, 4), payload.Length);

        stream.Write(header);
        if (!payload.IsEmpty)
        {
            stream.Write(payload);
        }
        stream.Flush();
    }

    public static async ValueTask WriteFrameAsync(Stream stream, IpcOpcode opcode, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        byte[] header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), Magic);
        header[4] = Version;
        header[5] = (byte)opcode;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(6, 4), payload.Length);

        await stream.WriteAsync(header.AsMemory(), ct).ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        }
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static IpcFrame? ReadFrame(Stream stream)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        int totalRead = 0;
        while (totalRead < HeaderSize)
        {
            int read = stream.Read(header.Slice(totalRead, HeaderSize - totalRead));
            if (read == 0) return null;
            totalRead += read;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0, 4));
        if (magic != Magic)
            throw new InvalidDataException($"Invalid IPC magic: 0x{magic:X8}");

        byte version = header[4];
        if (version != Version)
            throw new InvalidDataException($"Unsupported IPC version: {version}");

        var opcode = (IpcOpcode)header[5];
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(6, 4));

        if (length < 0 || length > MaxPayloadSize)
            throw new InvalidDataException($"Invalid IPC payload length: {length}");

        byte[] payload = length == 0 ? Array.Empty<byte>() : new byte[length];
        totalRead = 0;
        while (totalRead < length)
        {
            int read = stream.Read(payload, totalRead, length - totalRead);
            if (read == 0)
                throw new EndOfStreamException($"Stream ended prematurely while reading payload of {length} bytes.");
            totalRead += read;
        }

        return new IpcFrame(opcode, payload);
    }

    public static async ValueTask<IpcFrame?> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        byte[] header = new byte[HeaderSize];
        int totalRead = 0;
        while (totalRead < HeaderSize)
        {
            int read = await stream.ReadAsync(header.AsMemory(totalRead, HeaderSize - totalRead), ct).ConfigureAwait(false);
            if (read == 0) return null;
            totalRead += read;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        if (magic != Magic)
            throw new InvalidDataException($"Invalid IPC magic: 0x{magic:X8}");

        byte version = header[4];
        if (version != Version)
            throw new InvalidDataException($"Unsupported IPC version: {version}");

        var opcode = (IpcOpcode)header[5];
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(6, 4));

        if (length < 0 || length > MaxPayloadSize)
            throw new InvalidDataException($"Invalid IPC payload length: {length}");

        byte[] payload = length == 0 ? Array.Empty<byte>() : new byte[length];
        totalRead = 0;
        while (totalRead < length)
        {
            int read = await stream.ReadAsync(payload.AsMemory(totalRead, length - totalRead), ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException($"Stream ended prematurely while reading payload of {length} bytes.");
            totalRead += read;
        }

        return new IpcFrame(opcode, payload);
    }

    public static void WriteJsonFrame(Stream stream, IpcOpcode opcode, JsonObject? obj)
    {
        byte[] payload = ToUtf8Bytes(obj);
        WriteFrame(stream, opcode, payload);
    }

    public static async ValueTask WriteJsonFrameAsync(Stream stream, IpcOpcode opcode, JsonObject? obj, CancellationToken ct = default)
    {
        byte[] payload = ToUtf8Bytes(obj);
        await WriteFrameAsync(stream, opcode, payload, ct).ConfigureAwait(false);
    }
}
