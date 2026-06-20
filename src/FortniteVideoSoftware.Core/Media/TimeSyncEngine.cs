namespace FortniteVideoSoftware.Core.Media;

public static class TimeSyncEngine
{
    public static string BuildAtempoFilter(double videoSpeed)
    {
        // Video scales according to videoSpeed
        // Background music MUST remain at constant 1.0x tempo
        
        // Return FFmpeg audio filter for scaling game audio
        if (Math.Abs(videoSpeed - 1.0) < 0.01)
        {
            return "anull";
        }
        
        // Atempo limit is usually 0.5 to 2.0 per filter, we might need chaining if extreme, but standard is 1 filter if within bounds
        return $"atempo={videoSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }
    
    public static double CalculateTotalDuration(double originalDuration, List<(double Start, double End, double Speed)> segments, List<(double Time, double Duration)> freezes)
    {
        double total = originalDuration;
        
        // Granular Speed & Freeze Contract:
        // Freeze frame does NOT discard gameplay. It inserts a still image and mathematically shifts all subsequent video/audio forward.
        // Output file total duration MUST mathematically increase by the exact duration of the freeze frame.
        foreach (var freeze in freezes)
        {
            total += freeze.Duration;
        }
        
        foreach (var seg in segments)
        {
            double origSegLen = seg.End - seg.Start;
            double newSegLen = origSegLen / seg.Speed;
            total = total - origSegLen + newSegLen;
        }

        return total;
    }
}
