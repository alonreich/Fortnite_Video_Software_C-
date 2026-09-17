// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.App.ViewModels;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App.Services;

public sealed record EstimateMedia(string Path, double Duration, double VideoKbps, double AudioKbps,
    int Width, int Height, double Fps);
public sealed record MainSizeRequest(string? Path, double DurationMs, double StartMs, double EndMs,
    double Speed, SpeedSegment[] Segments, CutRange[] Cuts, MemePlacement[] Memes,
    string? LegacyMeme, bool Portrait, int Quality);
public sealed record MergerSizeRequest(string[] Paths, double Speed, int Quality, EstimateMedia[]? KnownSources = null);
public sealed record OutputSizeEstimate(double? Megabytes, double DurationSeconds, double? TargetMegabytes = null,
    IReadOnlyList<EstimateMedia>? Sources = null, double VideoKbps = 0)
{
    public string Text => OutputFileSize.FormatMegabytes(Megabytes);
    public static OutputSizeEstimate Empty { get; } = new(null, 0);
}

/// <summary>Metadata is bounded, cached by file identity, and read only by the background worker.</summary>
public sealed class OutputSizeEstimator
{
    private readonly Func<string> _probePath;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private readonly Dictionary<string, (long Bytes, DateTime Modified, EstimateMedia Media)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private const int CacheLimit = 128;

    public OutputSizeEstimator(Func<string> probePath) => _probePath = probePath;

    // The first result needs no disk access. The same worker refines it using actual file details.
    public static OutputSizeEstimate? QuickMainEstimate(MainSizeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path)) return OutputSizeEstimate.Empty;
        if (request.EndMs <= request.StartMs) return null; // Metadata can still be loading after open/restore.
        if (QualityLadder.IsOriginal(request.Quality) ||
            (request.Memes.Length == 0 && !string.IsNullOrWhiteSpace(request.LegacyMeme))) return null;
        var timeline = OutputTimeline.Create(Math.Max(0, request.EndMs - request.StartMs), request.Segments,
            request.Speed, request.StartMs, cuts: CutRange.ToClipRelative(request.Cuts, request.StartMs));
        double freezeSeconds = timeline.Chunks.Where(c => c.IsFreeze).Sum(c => c.OutputLengthSec);
        double totalSeconds = timeline.TotalOutputSeconds + request.Memes.Sum(m => m.DurationSec) + 0.1;
        double? target = QualityLadder.TargetMbFor(request.Quality, totalSeconds,
            request.Portrait ? CoordinateConstants.ContentW : 1920,
            request.Portrait ? CoordinateConstants.ContentH : 1080, request.Portrait, freezeSeconds);
        return new(target, totalSeconds, target);
    }

    public static OutputSizeEstimate? QuickMergerEstimate(MergerSizeRequest request)
        => request.KnownSources is { } known && known.Select(s => s.Path).SequenceEqual(request.Paths)
            ? CalculateMerger(known, request.Speed, request.Quality) : null;

    public async Task<EstimateMedia?> ReadMediaAsync(string path, CancellationToken token)
    {
        await _cacheGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            if (_cache.TryGetValue(path, out var cached) && cached.Bytes == file.Length && cached.Modified == file.LastWriteTimeUtc)
                return cached.Media;
            long bytes = file.Length;
            DateTime modified = file.LastWriteTimeUtc;
            var start = new ProcessStartInfo(_probePath())
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in new[] { "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", path })
                start.ArgumentList.Add(arg);
            var output = await AsyncProcessRunner.RunAsync(start, TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (output.ExitCode != 0) return null;
            var json = JsonNode.Parse(output.StandardOutput);
            var streams = json?["streams"] as JsonArray;
            var video = streams?.FirstOrDefault(s => s?["codec_type"]?.ToString() == "video");
            var audio = streams?.FirstOrDefault(s => s?["codec_type"]?.ToString() == "audio");
            double duration = Number(json?["format"]?["duration"]);
            if (duration <= 0) duration = Number(video?["duration"]);
            bool still = IsStill(path);
            if (still) duration = MemePlacement.StillImageDurationSec;
            if (duration <= 0 || video == null) return null;
            double audioKbps = audio == null ? 0 : Number(audio["bit_rate"]) / 1000;
            if (audio != null && audioKbps <= 0) audioKbps = 192;
            double videoKbps = Number(video["bit_rate"]) / 1000;
            if (videoKbps <= 0 && !still)
                videoKbps = Math.Max(0, bytes * 8.0 / duration / 1000 - audioKbps);
            var media = new EstimateMedia(path, duration, videoKbps, audioKbps,
                (int)Number(video["width"]), (int)Number(video["height"]), Rate(video["avg_frame_rate"]));
            file.Refresh();
            if (!file.Exists || file.Length != bytes || file.LastWriteTimeUtc != modified) return null;
            if (_cache.Count >= CacheLimit) _cache.Remove(_cache.Keys.First());
            _cache[path] = (bytes, modified, media);
            return media;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            RuntimeLog.Debug("SIZE ESTIMATE", $"Could not read {System.IO.Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
        finally { _cacheGate.Release(); }
    }

    public async Task<OutputSizeEstimate> EstimateMainAsync(MainSizeRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Path)) return OutputSizeEstimate.Empty;
        var source = await ReadMediaAsync(request.Path, token).ConfigureAwait(false);
        if (source == null) return OutputSizeEstimate.Empty;
        var memes = new List<EstimateMedia>();
        var paths = request.Memes.Length > 0 ? request.Memes.Select(m => m.FilePath)
            : string.IsNullOrWhiteSpace(request.LegacyMeme) ? [] : new[] { request.LegacyMeme };
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            var media = await ReadMediaAsync(path, token).ConfigureAwait(false);
            if (media == null && File.Exists(path)) return OutputSizeEstimate.Empty;
            if (media != null) memes.Add(media);
        }
        return CalculateMain(request, source, memes);
    }

    public static OutputSizeEstimate CalculateMain(MainSizeRequest request, EstimateMedia source, IReadOnlyList<EstimateMedia> memes)
    {
        // Recovery can arrive before the player reports duration. Resolve the unmarked end locally.
        double endMs = request.EndMs > request.StartMs ? request.EndMs : source.Duration * 1000;
        var timeline = OutputTimeline.Create(Math.Max(0, endMs - request.StartMs), request.Segments,
            request.Speed, request.StartMs, cuts: CutRange.ToClipRelative(request.Cuts, request.StartMs));
        double bodySeconds = timeline.TotalOutputSeconds;
        double freezeSeconds = timeline.Chunks.Where(c => c.IsFreeze).Sum(c => c.OutputLengthSec);
        double totalSeconds = bodySeconds + memes.Sum(m => m.Duration) + 0.1; // Export's intro still.
        double? target = QualityLadder.TargetMbFor(request.Quality, totalSeconds,
            request.Portrait ? CoordinateConstants.ContentW : 1920,
            request.Portrait ? CoordinateConstants.ContentH : 1080, request.Portrait, freezeSeconds);
        if (target.HasValue) return new(target, totalSeconds, target);

        // Original remains uncapped. This prediction never becomes an encoder size target.
        double rate = OriginalVideoRate(source, request.Portrait);
        double videoKilobits = rate * QualityLadder.BillableSeconds(bodySeconds + 0.1, freezeSeconds + 0.1);
        foreach (var meme in memes)
            videoKilobits += (IsStill(meme.Path) ? rate * 0.15 : OriginalVideoRate(meme, request.Portrait)) * meme.Duration;
        int audioRate = MediaProber.ChooseAudioBitrate((int)source.AudioKbps, totalSeconds, null);
        return new(OutputFileSize.FromBitrate(videoKilobits, 1, audioRate, totalSeconds) * 1.01, totalSeconds);
    }

    public async Task<OutputSizeEstimate> EstimateMergerAsync(MergerSizeRequest request, CancellationToken token)
    {
        if (request.Paths.Length == 0) return OutputSizeEstimate.Empty;
        var sources = new List<EstimateMedia>();
        foreach (string path in request.Paths)
        {
            var media = await ReadMediaAsync(path, token).ConfigureAwait(false);
            if (media == null) return OutputSizeEstimate.Empty; // Never present a partial queue as the total.
            sources.Add(media);
        }
        return CalculateMerger(sources, request.Speed, request.Quality);
    }

    public static OutputSizeEstimate CalculateMerger(IReadOnlyList<EstimateMedia> sources, double speed, int quality)
    {
        double duration = sources.Sum(s => s.Duration);
        if (duration <= 0 || speed <= 0 || !double.IsFinite(speed)) return OutputSizeEstimate.Empty;
        double average = sources.Sum(s => s.Duration * s.VideoKbps) / duration;
        double rate = OutputFileSize.MergerTargetKbps(average);
        if (quality < 100)
        {
            // Constant quality is a rough prediction. Follow the export's CQ curve, not a linear percentage.
            double normalized = sources.Sum(s => s.Duration * OriginalVideoRate(s, false, normalize1080: true)) / duration;
            rate = normalized * Math.Pow(2, (15 - OutputFileSize.MergerConstantQuality(quality)) / 6.0);
        }
        double seconds = duration / speed;
        // MergerWorker always writes one 192 kbps AAC soundtrack, including mixed music/voice.
        return new(OutputFileSize.FromBitrate(rate, seconds, 192, seconds) * 1.01,
            seconds, Sources: sources, VideoKbps: rate);
    }

    private static double OriginalVideoRate(EstimateMedia media, bool portrait, bool normalize1080 = false)
    {
        double sourcePixels = Math.Max(1.0, (double)media.Width * media.Height);
        double outputPixels = portrait || normalize1080 ? 1920.0 * 1080 : sourcePixels;
        double fpsFactor = media.Fps > 0 ? Math.Min(2, 60 / media.Fps) : 1;
        return Math.Clamp(media.VideoKbps * Math.Min(1, outputPixels / sourcePixels) * fpsFactor, 300, EncoderManager.MaxBitrateKbps);
    }

    private static bool IsStill(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp";
    private static double Number(JsonNode? node) => double.TryParse(node?.ToString(), NumberStyles.Float,
        CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : 0;
    private static double Rate(JsonNode? node)
    {
        var parts = node?.ToString().Split('/');
        return parts?.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out double n)
            && double.TryParse(parts[1], CultureInfo.InvariantCulture, out double d) && d > 0 ? n / d : Number(node);
    }
}
