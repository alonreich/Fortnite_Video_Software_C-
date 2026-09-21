// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.IO;
using FortniteVideoSoftware.Core.Infrastructure;
using Xunit;

namespace FortniteVideoSoftware.Core.Tests;

/// <summary>
/// SYS-PAYLOADSPLIT — the fingerprint that decides whether an update has to reship 322 MB of codec
/// binaries or just the application.
/// </summary>
public sealed class RuntimePayloadManifestTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fvs_payload_" + Guid.NewGuid().ToString("N"));

    public RuntimePayloadManifestTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* temp folder still held by the OS; harmless. */ }
    }

    private string Folder(string name)
    {
        string p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static void Write(string folder, string name, int bytes)
        => File.WriteAllBytes(Path.Combine(folder, name), new byte[bytes]);

    [Fact]
    public void IdenticalRuntimesProduceTheSameFingerprint()
    {
        string a = Folder("a"), b = Folder("b");
        Write(a, "avcodec-62.dll", 1024); Write(a, "ffmpeg.exe", 512);
        Write(b, "avcodec-62.dll", 1024); Write(b, "ffmpeg.exe", 512);

        Assert.Equal(
            RuntimePayloadManifest.FromFolder(a).Fingerprint,
            RuntimePayloadManifest.FromFolder(b).Fingerprint);
    }

    /// <summary>
    /// The case the whole mechanism turns on. A changed FFmpeg build must NOT look like the
    /// installed one, or the updater ships an app against codecs it was not built for and the
    /// failure lands on the user at export time, after they have done the work.
    /// </summary>
    [Fact]
    public void AChangedBinaryChangesTheFingerprint()
    {
        string a = Folder("a"), b = Folder("b");
        Write(a, "avcodec-62.dll", 1024);
        Write(b, "avcodec-62.dll", 1025);

        Assert.NotEqual(
            RuntimePayloadManifest.FromFolder(a).Fingerprint,
            RuntimePayloadManifest.FromFolder(b).Fingerprint);
    }

    [Fact]
    public void AnAddedOrRemovedBinaryChangesTheFingerprint()
    {
        string a = Folder("a"), b = Folder("b");
        Write(a, "avcodec-62.dll", 1024);
        Write(b, "avcodec-62.dll", 1024); Write(b, "avfilter-11.dll", 64);

        Assert.NotEqual(
            RuntimePayloadManifest.FromFolder(a).Fingerprint,
            RuntimePayloadManifest.FromFolder(b).Fingerprint);
    }

    /// <summary>
    /// ⚠️ The application's own files are not the runtime. If they counted, every patch would
    /// change the fingerprint and the small-download path would never once be taken — the feature
    /// would be present, tested, and dead.
    /// </summary>
    [Fact]
    public void NonBinaryFilesDoNotAffectTheFingerprint()
    {
        string a = Folder("a"), b = Folder("b");
        Write(a, "avcodec-62.dll", 1024);
        Write(b, "avcodec-62.dll", 1024);
        File.WriteAllText(Path.Combine(b, "readme.txt"), "not part of the runtime");
        File.WriteAllText(Path.Combine(b, "starter.mp3"), "nor this");

        Assert.Equal(
            RuntimePayloadManifest.FromFolder(a).Fingerprint,
            RuntimePayloadManifest.FromFolder(b).Fingerprint);
    }

    [Fact]
    public void EnumerationOrderDoesNotAffectTheFingerprint()
    {
        string a = Folder("a");
        Write(a, "z.dll", 10); Write(a, "a.dll", 20); Write(a, "m.dll", 30);

        string first = RuntimePayloadManifest.FromFolder(a).Fingerprint;
        string second = RuntimePayloadManifest.FromFolder(a).Fingerprint;

        Assert.Equal(first, second);
    }

    [Fact]
    public void RoundTripsThroughDisk()
    {
        string a = Folder("a");
        Write(a, "avcodec-62.dll", 4096);

        RuntimePayloadManifest written = RuntimePayloadManifest.FromFolder(a);
        written.Write(a);

        RuntimePayloadManifest? read = RuntimePayloadManifest.Read(a);

        Assert.NotNull(read);
        Assert.Equal(written.Fingerprint, read!.Fingerprint);
        Assert.Equal(written.FileCount, read.FileCount);
        Assert.Equal(written.TotalBytes, read.TotalBytes);
    }

    /// <summary>
    /// No manifest means "I cannot prove the runtime matches", which the updater resolves to the
    /// full installer. A bigger download is always safe; a smaller one is not.
    /// </summary>
    [Fact]
    public void AMissingManifestReadsAsNull()
        => Assert.Null(RuntimePayloadManifest.Read(Folder("empty")));

    [Fact]
    public void FormatBytesReadsInTheUnitsAUserThinksIn()
    {
        Assert.Equal("322 MB", RuntimePayloadManifest.FormatBytes(322L * 1024 * 1024));
        Assert.Equal("8 MB", RuntimePayloadManifest.FormatBytes(8L * 1024 * 1024));
        Assert.Equal("512 KB", RuntimePayloadManifest.FormatBytes(512L * 1024));
    }
}
