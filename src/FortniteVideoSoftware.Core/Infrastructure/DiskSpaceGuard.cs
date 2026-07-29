namespace FortniteVideoSoftware.Core.Infrastructure;

/// <summary>
/// Pre-flight free-space check for the render pipelines.
///
/// WHY: an export writes the encode to <c>%TMP%\...\core.mp4</c> and then COPIES it to the
/// output folder, so at peak the machine needs roughly 2x the output size (plus headroom for
/// the muxer and the thumbnail). Without this check a full drive surfaces to the user as a raw
/// "FFmpeg exited with code 1" after a ten-minute encode. Checking up front costs nothing and
/// turns it into an actionable message before any work starts.
/// </summary>
public static class DiskSpaceGuard
{
    /// <summary>Extra breathing room on top of the raw estimate (muxer, thumbnail, fragmentation).</summary>
    private const long SafetyMarginBytes = 256L * 1024 * 1024;

    public readonly record struct Result(bool Ok, string? Message)
    {
        public static Result Success() => new(true, null);
        public static Result Fail(string message) => new(false, message);
    }

    /// <summary>
    /// Verifies both the staging drive and the destination drive can hold the job.
    /// When temp and output live on the SAME volume the requirement is summed, because both
    /// copies coexist on that one volume at the moment of the final <c>File.Copy</c>.
    /// </summary>
    /// <param name="tempDirectory">Where the encode is staged.</param>
    /// <param name="outputDirectory">Where the finished file lands.</param>
    /// <param name="estimatedOutputBytes">Best estimate of the finished file size.</param>
    public static Result Check(string tempDirectory, string outputDirectory, long estimatedOutputBytes)
    {
        if (estimatedOutputBytes <= 0) return Result.Success();

        try
        {
            string? tempRoot = GetVolumeRoot(tempDirectory);
            string? outputRoot = GetVolumeRoot(outputDirectory);

            bool sameVolume = tempRoot != null && outputRoot != null &&
                              string.Equals(tempRoot, outputRoot, StringComparison.OrdinalIgnoreCase);

            if (sameVolume)
            {
                long needed = (estimatedOutputBytes * 2) + SafetyMarginBytes;
                return CheckOne(tempRoot!, needed, "staging and output");
            }

            Result staging = CheckOne(tempRoot, estimatedOutputBytes + SafetyMarginBytes, "staging");
            if (!staging.Ok) return staging;

            return CheckOne(outputRoot, estimatedOutputBytes + SafetyMarginBytes, "output");
        }
        catch (Exception ex)
        {
            CoreLogger.Info("DiskSpace", $"Free-space probe skipped: {ex.Message}");
            return Result.Success();
        }
    }

    private static Result CheckOne(string? volumeRoot, long neededBytes, string role)
    {
        if (string.IsNullOrWhiteSpace(volumeRoot)) return Result.Success();

        var drive = new DriveInfo(volumeRoot);
        if (!drive.IsReady) return Result.Success();

        long free = drive.AvailableFreeSpace;
        if (free >= neededBytes)
        {
            CoreLogger.Info("DiskSpace",
                $"Free-space check passed for {role} volume: {Mb(free)} MB available, {Mb(neededBytes)} MB required.");
            return Result.Success();
        }

        string message =
            $"Not enough free space on drive {drive.Name.TrimEnd('\\')} for the {role} files. " +
            $"About {Mb(neededBytes)} MB is required but only {Mb(free)} MB is free. " +
            "Free up some space (or pick a different output folder in Settings) and try again.";

        CoreLogger.Fail("DiskSpace", message);
        return Result.Fail(message);
    }

    private static string? GetVolumeRoot(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;

        try
        {
            string full = Path.GetFullPath(directory);
            string? root = Path.GetPathRoot(full);

            if (string.IsNullOrWhiteSpace(root) || root!.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return null;
            }

            return root;
        }
        catch
        {
            return null;
        }
    }

    private static long Mb(long bytes) => bytes / (1024 * 1024);

    /// <summary>
    /// Rough finished-size estimate used by the pre-flight check.
    /// Uses the explicit size target when the user locked one, otherwise derives it from the
    /// chosen video bitrate; falls back to a deliberately generous 15 Mbit/s guess so the guard
    /// still catches an almost-full drive when quality is CRF-driven.
    /// </summary>
    public static long EstimateOutputBytes(double durationSeconds, int? videoBitrateKbps, double? targetMb)
    {
        if (targetMb is > 0) return (long)(targetMb.Value * 1024 * 1024);
        if (durationSeconds <= 0) return 0;

        int kbps = videoBitrateKbps is > 0 ? videoBitrateKbps!.Value : 15000;
        return (long)((kbps + 192) * 1000.0 / 8.0 * durationSeconds);
    }
}
