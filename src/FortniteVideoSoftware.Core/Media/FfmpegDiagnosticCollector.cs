using System;
using System.Collections.Generic;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// Concurrently collects and categorizes diagnostic output during an FFmpeg execution.
/// Preserves early error lines so that later routine diagnostic output does not push them out of view.
/// </summary>
public sealed class FfmpegDiagnosticCollector
{
    private readonly object _sync = new();
    private readonly List<string> _significantLines = new(32);
    private readonly Queue<string> _tailLines = new(100);
    private int? _explicitErrorCode;
    private string? _explicitErrorSource;

    public int? ExplicitErrorCode
    {
        get { lock (_sync) return _explicitErrorCode; }
    }

    public string? ExplicitErrorSource
    {
        get { lock (_sync) return _explicitErrorSource; }
    }

    public void AddStderrLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        string trimmed = line.Trim();
        if (trimmed.Length == 0) return;

        lock (_sync)
        {
            ParseExplicitErrorCode(trimmed);

            bool isProgressTicker = trimmed.StartsWith("frame=", StringComparison.OrdinalIgnoreCase) ||
                                    trimmed.StartsWith("size=", StringComparison.OrdinalIgnoreCase);

            if (!isProgressTicker)
            {
                _tailLines.Enqueue(trimmed);
                if (_tailLines.Count > 100)
                {
                    _tailLines.Dequeue();
                }
            }

            if (IsSignificantDiagnostic(trimmed))
            {
                if (_significantLines.Count < 30 && !_significantLines.Contains(trimmed))
                {
                    _significantLines.Add(trimmed);
                }
            }
        }
    }

    public IReadOnlyList<string> GetDiagnosticLines()
    {
        lock (_sync)
        {
            var result = new List<string>(_significantLines.Count + _tailLines.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string line in _significantLines)
            {
                if (seen.Add(line))
                {
                    result.Add(line);
                }
            }

            foreach (string line in _tailLines)
            {
                if (seen.Add(line))
                {
                    result.Add(line);
                }
            }

            return result;
        }
    }

    public IReadOnlyList<string> GetSignificantLines()
    {
        lock (_sync)
        {
            return _significantLines.ToArray();
        }
    }

    public IReadOnlyList<string> GetTailLines()
    {
        lock (_sync)
        {
            return _tailLines.ToArray();
        }
    }

    private void ParseExplicitErrorCode(string line)
    {
        if (_explicitErrorCode.HasValue && _explicitErrorCode.Value != 0) return;

        int codeIdx = line.IndexOf("error code:", StringComparison.OrdinalIgnoreCase);
        if (codeIdx >= 0)
        {
            ExtractIntAfter(line, codeIdx + "error code:".Length, "FFmpeg");
            return;
        }

        int mfxIdx = line.IndexOf("MFX session:", StringComparison.OrdinalIgnoreCase);
        if (mfxIdx >= 0)
        {
            ExtractIntAfter(line, mfxIdx + "MFX session:".Length, "FFmpeg");
            return;
        }

        int returnIdx = line.IndexOf("return code", StringComparison.OrdinalIgnoreCase);
        if (returnIdx >= 0)
        {
            ExtractIntAfter(line, returnIdx + "return code".Length, "FFmpeg");
            return;
        }
    }

    private void ExtractIntAfter(string line, int startPos, string source)
    {
        int i = startPos;
        while (i < line.Length && (char.IsWhiteSpace(line[i]) || line[i] == ':')) i++;

        int numStart = i;
        if (i < line.Length && (line[i] == '-' || line[i] == '+')) i++;
        while (i < line.Length && char.IsDigit(line[i])) i++;

        if (i > numStart && int.TryParse(line[numStart..i], out int code))
        {
            _explicitErrorCode = code;
            _explicitErrorSource = source;
        }
    }

    private static bool IsSignificantDiagnostic(string line)
    {
        return line.Contains("[error]", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("[fatal]", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("[panic]", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Cannot load", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Invalid data", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("No space left", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Disk quota", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Impossible to convert", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("OpenEncodeSessionEx", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("moov atom not found", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("could not find codec", StringComparison.OrdinalIgnoreCase);
    }
}
