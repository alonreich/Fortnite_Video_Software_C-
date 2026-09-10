using System.Text;
using System.Text.RegularExpressions;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// Plans video memory and filtering together. A hardware encoder alone does not
/// imply GPU filtering. Unknown effects retain the original software graph.
/// Audio filters always remain on the CPU and do not download video frames.
/// </summary>
public sealed partial class ExportVideoPipeline
{
    private static readonly HashSet<string> FrameMetadataFilters =
        ["setpts", "fps", "trim", "select", "loop", "split", "null", "nullsink", "setsar", "concat"];
    private static readonly HashSet<string> AudioFilters =
        ["anull", "anullsrc", "atrim", "asetpts", "aresample", "aformat", "atempo", "afade",
         "volume", "asplit", "amix", "adelay", "apad", "loudnorm", "alimiter", "equalizer",
         "highpass", "lowpass", "agate", "acrossover", "sidechaincompress"];

    private ExportVideoPipeline(string encoder, string graph, bool gpu, string reason)
    {
        FilterGraph = graph;
        UsesGpuFrames = gpu;
        DecodeFlags = EncoderManager.GetDecodeFlags(encoder, gpu);
        DeviceFlags = gpu ? ["-init_hw_device", "cuda=exportgpu"] : [];
        DecoderDescription = EncoderManager.DescribeDecoder(encoder, gpu);
        Description = gpu
            ? "Video: GPU decode, GPU filtering, GPU encode; frames stay in VRAM. Audio: CPU."
            : $"Video: CPU decode/filtering, {EncoderManager.DescribeEncoder(encoder)} encode. " +
              $"{reason} Audio: CPU.";
    }

    public string FilterGraph { get; }
    public bool UsesGpuFrames { get; }
    public IReadOnlyList<string> DecodeFlags { get; }
    public IReadOnlyList<string> DeviceFlags { get; }
    public string DecoderDescription { get; }
    public string Description { get; }

    public static ExportVideoPipeline Create(string encoder, string softwareGraph, bool allowGpuFrames = true)
    {
        if (encoder != "h264_nvenc" || !allowGpuFrames)
            return new(encoder, softwareGraph, false, allowGpuFrames
                ? "No supported resident video-filter route for this encoder."
                : "Using compatibility processing after a GPU pipeline failure.");

        var output = new StringBuilder();
        int start = 0;
        char quote = '\0';
        bool escaped = false;
        for (int i = 0; i <= softwareGraph.Length; i++)
        {
            if (i < softwareGraph.Length)
            {
                char c = softwareGraph[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c is '\'' or '"') { quote = c; continue; }
                if (c is not (',' or ';')) continue;
            }

            string token = softwareGraph[start..i];
            if (!TryGpuFilter(token, out string replacement, out string reason))
                return new(encoder, softwareGraph, false, reason);
            output.Append(replacement);
            if (i < softwareGraph.Length) output.Append(softwareGraph[i]);
            start = i + 1;
        }
        if (quote != '\0' || escaped)
            return new(encoder, softwareGraph, false, "Unrecognised filter syntax.");
        return new(encoder, output.ToString(), true, "");
    }

    private static bool TryGpuFilter(string token, out string replacement, out string reason)
    {
        replacement = token;
        reason = "Unrecognised filter syntax.";
        int start = 0;
        while (start < token.Length && char.IsWhiteSpace(token[start])) start++;
        while (start < token.Length && token[start] == '[')
        {
            int end = token.IndexOf(']', start);
            if (end < 0) return false;
            start = end + 1;
        }
        int nameEnd = start;
        while (nameEnd < token.Length && (char.IsAsciiLetterOrDigit(token[nameEnd]) || token[nameEnd] == '_')) nameEnd++;
        string name = token[start..nameEnd];
        if (FrameMetadataFilters.Contains(name) || AudioFilters.Contains(name)) return true;

        // Only these exact software operations have a verified GPU equivalent here.
        // Colour conversions, padding, crop, zoom and fades must not be silently removed.
        int labels = token.IndexOf('[', nameEnd);
        if (labels < 0) labels = token.Length;
        string filter = token[start..labels];
        string? gpuFilter = null;
        if (filter == "format=yuv420p")
            gpuFilter = "scale_cuda=format=yuv420p:passthrough=0";
        else if (name == "scale")
        {
            var match = FixedScale().Match(filter);
            if (match.Success)
                gpuFilter = $"scale_cuda={match.Groups[1].Value}:{match.Groups[2].Value}:interp_algo=lanczos:format=yuv420p:passthrough=0";
        }
        if (gpuFilter == null)
        {
            reason = $"Effect '{name}' needs CPU processing; no GPU-to-RAM decode round trip is used.";
            return false;
        }
        replacement = token[..start] + gpuFilter + token[labels..];
        return true;
    }

    public void ApplyCodecFlags(List<string> codecFlags)
    {
        if (!UsesGpuFrames) return;
        int pixelFormat = codecFlags.IndexOf("-pix_fmt");
        if (pixelFormat >= 0 && pixelFormat + 1 < codecFlags.Count)
            codecFlags[pixelFormat + 1] = "cuda";
    }

    [GeneratedRegex(@"^scale=(\d+):(\d+):flags=lanczos$", RegexOptions.CultureInvariant)]
    private static partial Regex FixedScale();
}
