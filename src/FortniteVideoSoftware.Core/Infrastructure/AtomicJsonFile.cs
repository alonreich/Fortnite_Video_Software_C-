// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Forbidden to modify without reading: docs/06_PROJECT_DOCUMENT_MODEL.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FortniteVideoSoftware.Core.Infrastructure;

public static class AtomicJsonFile
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true
    };

    public static JsonObject? ReadObject(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);

            if (stream.Length == 0) return null;

            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null") return null;

            JsonNode? node = JsonNode.Parse(json);
            var obj = node as JsonObject;
            
            if (obj != null)
            {
                if (!obj.ContainsKey("schema_version") && !obj.ContainsKey("Version"))
                {
                    obj["schema_version"] = 1;
                }
            }

            return obj;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return null;
        }
    }

    public static void WriteObject(string path, JsonObject payload)
        => WriteCore(path, stream =>
        {
            using Utf8JsonWriter writer = new(stream, WriterOptions);
            payload.WriteTo(writer);
            writer.Flush();
        });

    /// <summary>
    /// ATOMICTEXT_01 — the SAME power-outage-safe protocol as <see cref="WriteObject"/>, for callers
    /// that already hold a fully formed JSON document as text and must not have it reshaped.
    ///
    /// WHY THIS EXISTS: <c>SettingsManager.Save</c> serialises <c>AppSettings</c> through a
    /// source-generated <c>JsonSerializerContext</c> (required for NativeAOT). Round-tripping that
    /// output through <see cref="JsonObject"/> just to reach <see cref="WriteObject"/> would re-emit
    /// the document from a different writer and risk silent formatting/ordering drift in a file the
    /// migration tests compare against. This overload keeps the bytes the caller produced and gives
    /// them the identical three-step guarantee that
    /// <c>docs/05_SYSTEM_LIFECYCLE_STORAGE.md#SYS-RECOVERY</c> mandates:
    ///   1. unique GUID temp file in the TARGET directory, opened <see cref="FileOptions.WriteThrough"/>
    ///   2. <c>stream.Flush(flushToDisk: true)</c> — the bytes are on the platter, not in the OS cache
    ///   3. <c>File.Move(temp, path, overwrite: true)</c> — an atomic same-volume NTFS rename
    ///
    /// Writing a fixed-name temp file, or renaming before the flush, is what produces a correctly
    /// named but ZERO-FILLED document after a power cut. Do not "simplify" this back to
    /// File.WriteAllText.
    /// </summary>
    public static void WriteText(string path, string contents)
        => WriteCore(path, stream =>
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(contents);
            stream.Write(bytes, 0, bytes.Length);
        });

    private static void WriteCore(string path, Action<FileStream> emit)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new IOException($"Cannot resolve a parent directory for '{path}'.");
        }

        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (FileStream stream = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.WriteThrough))
            {
                emit(stream);
                stream.Flush(flushToDisk: true);
            }

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 5)
                {
                    Thread.Sleep(20 * (attempt + 1));
                }
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
