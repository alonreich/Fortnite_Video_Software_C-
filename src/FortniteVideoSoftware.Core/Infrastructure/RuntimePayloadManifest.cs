// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FortniteVideoSoftware.Core.Infrastructure;

/// <summary>
/// SYS-PAYLOADSPLIT — WHAT THE INSTALLED RUNTIME ACTUALLY IS, SO A PATCH DOES NOT HAVE TO RESHIP IT.
///
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// <b>THE PROBLEM THIS EXISTS TO MEASURE.</b> The product ships as one 322 MB executable: a
/// NativeAOT installer with <c>payload.zip</c> embedded as a managed resource, which it extracts on
/// first run. The payload is FFmpeg and libmpv — <c>avcodec-62.dll</c> alone is 97 MB — plus the
/// starter media. The application's own code is a small fraction of that.
/// </para>
///
/// <para>
/// ⚠️ <b>AND THE AUTO-UPDATER DOWNLOADS ALL OF IT, EVERY TIME.</b> <c>UpdateService</c> fetches the
/// single <c>FortniteVideoSoftware.exe</c> release asset and reinstalls. So a one-line typo fix
/// costs every user 322 MB over a 30-minute timeout — for a set of codec DLLs that have not changed
/// since June and will not change in this release either. On a metered connection that is a reason
/// to stop updating, which turns every shipped fix into a fix most users never receive.
/// </para>
///
/// <para>
/// <b>WHAT THIS CLASS DOES.</b> It records a fingerprint of the runtime payload — the binaries, not
/// the app — next to the installed files. The build writes one into the payload; the updater
/// compares the installed fingerprint against the one the release advertises. Equal means the
/// runtime is already correct on this machine and only the application needs to come down.
/// </para>
///
/// <para>
/// ⚠️ <b>NAMES AND SIZES, NOT CONTENT HASHES.</b> Hashing 368 MB of DLLs on every startup to
/// decide whether to check for an update would be a worse bug than the one being fixed. Name plus
/// exact byte length over the whole set is enough to notice a version change — FFmpeg builds do not
/// coincidentally keep every file at exactly the same length — and it costs a directory listing.
/// The downloaded package is still verified by SHA-256 before it is trusted (UPDATETRUST_02); this
/// fingerprint decides WHAT to download, never whether to trust it.
/// </para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed record RuntimePayloadManifest(string Fingerprint, int FileCount, long TotalBytes)
{
    /// <summary>The file the build writes into the payload and the installer lays down beside the app.</summary>
    public const string FileName = "runtime.manifest.json";

    /// <summary>
    /// SYS-PAYLOADSPLIT — fingerprints a folder of runtime binaries.
    ///
    /// <para>Ordinal-sorted by relative path so the answer does not depend on the order the
    /// filesystem happened to enumerate, which differs between machines and between runs.</para>
    /// </summary>
    public static RuntimePayloadManifest FromFolder(string folder, IEnumerable<string>? extensions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var allowed = new HashSet<string>(
            extensions ?? new[] { ".dll", ".exe", ".com" },
            StringComparer.OrdinalIgnoreCase);

        var entries = new List<string>();
        long total = 0;

        if (Directory.Exists(folder))
        {
            foreach (string path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (!allowed.Contains(Path.GetExtension(path))) continue;

                var info = new FileInfo(path);
                string relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
                entries.Add($"{relative}:{info.Length}");
                total += info.Length;
            }
        }

        entries.Sort(StringComparer.Ordinal);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries)));
        return new RuntimePayloadManifest(Convert.ToHexString(hash, 0, 12), entries.Count, total);
    }

    public JsonObject ToJson() => new()
    {
        ["fingerprint"] = Fingerprint,
        ["file_count"] = FileCount,
        ["total_bytes"] = TotalBytes,
    };

    public static RuntimePayloadManifest? FromJson(JsonObject? root)
    {
        if (root?["fingerprint"] is null) return null;
        string fp = root["fingerprint"]!.GetValue<string>();
        if (string.IsNullOrWhiteSpace(fp)) return null;

        int count = root["file_count"] is JsonValue c && c.TryGetValue(out int ci) ? ci : 0;
        long bytes = root["total_bytes"] is JsonValue b && b.TryGetValue(out long bi) ? bi : 0;
        return new RuntimePayloadManifest(fp, count, bytes);
    }

    /// <summary>Reads the manifest the installer laid down beside the app, or null when there is none.</summary>
    public static RuntimePayloadManifest? Read(string folder)
    {
        try
        {
            string path = Path.Combine(folder, FileName);
            return File.Exists(path) ? FromJson(AtomicJsonFile.ReadObject(path)) : null;
        }
        catch (Exception ex)
        {
            // An unreadable manifest means "I cannot prove the runtime matches", which resolves to
            // the full installer. Degrading to a bigger download is always safe; degrading to a
            // smaller one is not.
            CoreLogger.Swallowed(ex);
            return null;
        }
    }

    /// <summary>Writes the manifest into <paramref name="folder"/>.</summary>
    public void Write(string folder)
    {
        Directory.CreateDirectory(folder);
        AtomicJsonFile.WriteObject(Path.Combine(folder, FileName), ToJson());
    }

    /// <summary>Human-readable size, for the update prompt that tells the user what they are about to spend.</summary>
    public static string FormatBytes(long bytes)
        => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):0.0} GB"
         : bytes >= 1024L * 1024        ? $"{bytes / (1024.0 * 1024):0} MB"
         : bytes >= 1024L               ? $"{bytes / 1024.0:0} KB"
         :                                $"{bytes} bytes";
}
