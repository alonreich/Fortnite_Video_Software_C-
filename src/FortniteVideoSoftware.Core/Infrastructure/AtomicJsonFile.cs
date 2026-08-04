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
            System.Diagnostics.Debug.WriteLine($"ReadObject failed: {ex}");
            return null;
        }
    }

    public static void WriteObject(string path, JsonObject payload)
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
                using Utf8JsonWriter writer = new(stream, WriterOptions);
                payload.WriteTo(writer);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
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
