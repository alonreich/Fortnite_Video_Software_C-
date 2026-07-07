namespace FortniteVideoSoftware.Core.Media;

public static class TimeSyncEngine
{
    public static string BuildAtempoFilter(double videoSpeed)
    {
        
        if (Math.Abs(videoSpeed - 1.0) < 0.01)
        {
            return "anull";
        }
        
        return $"atempo={videoSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }
    
    public static double CalculateTotalDuration(double originalDuration, List<(double Start, double End, double Speed)> segments, List<(double Time, double Duration)> freezes)
    {
        double total = originalDuration;
        
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
