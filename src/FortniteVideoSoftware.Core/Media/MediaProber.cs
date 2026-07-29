
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

public class MediaProber
{
    private readonly string _ffprobePath;
    private readonly string _videoPath;
    private JsonObject? _probeData;
    private readonly SemaphoreSlim _probeLock = new(1, 1);

    public MediaProber(string ffprobePath, string videoPath)
    {
        _ffprobePath = ffprobePath;
        _videoPath = videoPath;
    }

    public async Task<JsonObject> ProbeAsync()
    {
        if (_probeData != null) return _probeData;
        await _probeLock.WaitAsync();
        try
        {
            if (_probeData != null) return _probeData;

            var probeArgs = new[]
            {
                "-v", "quiet",
                "-print_format", "json",
                "-show_format", "-show_streams",
                _videoPath
            };

            var psi = new ProcessStartInfo
            {
                FileName = _ffprobePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in probeArgs) psi.ArgumentList.Add(arg);

            CoreLogger.Debug("FFprobe", $"Command: {_ffprobePath} {ProcessArgs.FormatForLog(probeArgs)}");

            try
            {
                var result = await AsyncProcessRunner.RunAsync(psi, timeout: TimeSpan.FromSeconds(15));
                if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    _probeData = JsonNode.Parse(result.StandardOutput)?.AsObject() ?? new JsonObject();
                    return _probeData ?? new JsonObject();
                }
                
                CoreLogger.Fail("FFprobe", $"Exit code {result.ExitCode}. Stderr: {result.StandardError}");
                return new JsonObject();
            }
            catch (OperationCanceledException)
            {
                CoreLogger.Fail("FFprobe", "FFprobe timed out after 15 seconds.");
                return new JsonObject();
            }
        }
        catch
        {
            _probeData = new JsonObject();
        }
        finally
        {
            _probeLock.Release();
        }
        return _probeData;
    }

    public async Task<double> GetDurationAsync()
    {
        var data = await ProbeAsync();
        var format = data["format"]?.AsObject();
        if (format != null && format["duration"] != null)
        {
            if (double.TryParse(format["duration"]!.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dur))
                return dur;
        }
        var streams = data["streams"]?.AsArray();
        if (streams != null)
        {
            foreach (var stream in streams)
            {
                if (stream?["codec_type"]?.ToString() == "video")
                {
                    var durNode = stream["duration"];
                    if (durNode != null && double.TryParse(durNode.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dur))
                        return dur;
                }
            }
        }
        return 0;
    }

    public async Task<(int width, int height)> GetResolutionAsync()
    {
        var data = await ProbeAsync();
        var streams = data["streams"]?.AsArray();
        if (streams != null)
        {
            foreach (var stream in streams)
            {
                if (stream?["codec_type"]?.ToString() == "video")
                {
                    int w = ParseInt(stream["width"]);
                    int h = ParseInt(stream["height"]);
                    if (w > 0 && h > 0) return (w, h);
                }
            }
        }
        return (1920, 1080);
    }

    public async Task<string> GetResolutionStringAsync()
    {
        var (w, h) = await GetResolutionAsync();
        return $"{w}x{h}";
    }

    public async Task<bool> HasAudioAsync()
    {
        var data = await ProbeAsync();
        var streams = data["streams"]?.AsArray();
        if (streams != null)
        {
            foreach (var stream in streams)
            {
                if (stream?["codec_type"]?.ToString() == "audio") return true;
            }
        }
        return false;
    }

    public async Task<int> GetAudioBitrateAsync()
    {
        var data = await ProbeAsync();
        var streams = data["streams"]?.AsArray();
        if (streams != null)
        {
            foreach (var stream in streams)
            {
                if (stream?["codec_type"]?.ToString() == "audio")
                {
                    int br = ParseInt(stream["bit_rate"]);
                    if (br > 0) return br / 1000;
                }
            }
        }
        var format = data["format"]?.AsObject();
        if (format != null)
        {
            int br = ParseInt(format["bit_rate"]);
            if (br > 0) return br / 1000;
        }
        return 192;
    }

    /// <summary>
    /// Returns the source video stream bitrate in kbps. Falls back to the container
    /// bitrate minus a nominal audio bitrate when the stream tag is missing.
    /// </summary>
    public async Task<double> GetVideoBitrateKbpsAsync()
    {
        var data = await ProbeAsync();
        var streams = data["streams"]?.AsArray();
        if (streams != null)
        {
            foreach (var stream in streams)
            {
                if (stream?["codec_type"]?.ToString() == "video")
                {
                    int br = ParseInt(stream["bit_rate"]);
                    if (br > 0) return br / 1000.0;
                }
            }
        }
        var format = data["format"]?.AsObject();
        if (format != null)
        {
            int br = ParseInt(format["bit_rate"]);
            if (br > 0)
            {
                double audioKbps = await GetAudioBitrateAsync();
                return Math.Max(0, (br / 1000.0) - audioKbps);
            }
        }
        return 0;
    }

    private int ParseInt(JsonNode? node)
    {
        if (node == null) return 0;
        if (node is JsonValue val)
        {
            if (val.TryGetValue(out int i)) return i;
            if (val.TryGetValue(out string? s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) return parsed;
        }
        return 0;
    }


    /// <summary>
    /// Calculate video bitrate to hit target file size.
    /// Port of calculate_video_bitrate().
    /// </summary>
    public static int CalculateVideoBitrate(
        double durationSec, int audioKbps, double? targetMb,
        bool keepHighestRes, int qualityLevel,
        string outputResolution = "1080x1920", string targetFps = "60")
    {
        if (!targetMb.HasValue || durationSec <= 0) return 0;

        double targetBytes = targetMb.Value * 1024 * 1024;
        double audioBytesPerSec = audioKbps * 1000.0 / 8.0;
        double audioTotalBytes = audioBytesPerSec * durationSec;
        double videoBudgetBytes = targetBytes - audioTotalBytes;
        double videoKbps = (videoBudgetBytes * 8.0 / 1000.0) / durationSec;


        return Math.Max(300, Math.Min(EncoderManager.MaxBitrateKbps, (int)videoKbps));
    }

    /// <summary>
    /// Choose audio bitrate based on source quality and target file size.
    /// Port of choose_audio_bitrate().
    /// </summary>
    public static int ChooseAudioBitrate(int sourceAudioKbps, double durationSec, double? targetMb)
    {
        if (targetMb.HasValue && targetMb < 10)
            return 96;
        if (sourceAudioKbps <= 0) return 192;
        return Math.Min(Math.Max(96, sourceAudioKbps), 320);
    }
}
