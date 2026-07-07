
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FortniteVideoSoftware.Core.Media;

public class EncoderManager
{
    public static readonly string[] EncoderPreference = ["h264_nvenc", "h264_amf", "h264_qsv", "libx264"];

    public static readonly Dictionary<string, string> HardwareByStrategy = new()
    {
        { "NVIDIA", "h264_nvenc" },
        { "AMD", "h264_amf" },
        { "INTEL", "h264_qsv" },
    };

    public const int MaxBitrateKbps = 100000;

    public string FFmpegPath { get; }
    public HashSet<string> AvailableEncoders { get; }
    public string PrimaryEncoder { get; private set; }
    public bool ForcedCpu { get; private set; }
    public string? HardwareStrategy { get; }
    public string? EncoderPreflightError { get; private set; }
    public HashSet<string> AttemptedEncoders { get; } = [];

    public EncoderManager(string? hardwareStrategy = null, string? ffmpegPath = null, string? videoHwEncoderEnv = null, bool forceCpuEnv = false)
    {
        string localFfmpeg = Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "backend", "ffmpeg.exe");
        FFmpegPath = !string.IsNullOrEmpty(ffmpegPath) ? ffmpegPath :
                     File.Exists(localFfmpeg) ? localFfmpeg : "ffmpeg.exe";

        AvailableEncoders = DetectAvailableEncoders(FFmpegPath);
        PrimaryEncoder = videoHwEncoderEnv ?? "h264_nvenc";
        ForcedCpu = forceCpuEnv;
        HardwareStrategy = hardwareStrategy;

        if (!string.IsNullOrEmpty(hardwareStrategy))
        {
            string upper = hardwareStrategy.ToUpperInvariant();
            if (HardwareByStrategy.TryGetValue(upper, out string? requestedEncoder))
            {
                PrimaryEncoder = requestedEncoder;
                ForcedCpu = false;
                if (AvailableEncoders.Count > 0 && !AvailableEncoders.Contains(requestedEncoder))
                {
                    EncoderPreflightError = $"Export blocked: {hardwareStrategy} ({requestedEncoder}) not in FFmpeg.";
                }
            }
            else if (upper == "CPU")
            {
                PrimaryEncoder = "libx264";
                ForcedCpu = true;
            }
            else if (upper == "GPU" || upper == "AUTO")
            {
                PrimaryEncoder = EncoderPreference.FirstOrDefault(encoder =>
                    encoder != "libx264" && (AvailableEncoders.Count == 0 || AvailableEncoders.Contains(encoder))) ?? "libx264";
                ForcedCpu = PrimaryEncoder == "libx264";
            }
            else
            {
                PrimaryEncoder = "libx264";
                ForcedCpu = true;
            }
        }
    }

    private int VbvBufKbps(int kbps) => Math.Min(MaxBitrateKbps, Math.Max(kbps, kbps * 2));

    private static Frac FpsFraction(string? fpsExpr, string defaultFps = "60")
    {
        try
        {
            if (string.IsNullOrEmpty(fpsExpr)) return Frac.FromString(defaultFps);
            var fps = Frac.FromString(fpsExpr);
            if (fps <= Frac.Zero) return Frac.FromString(defaultFps);
            Frac max60 = new(60, 1);
            return fps > max60 ? max60 : fps;
        }
        catch { return Frac.FromString(defaultFps); }
    }

    private static HashSet<string> DetectAvailableEncoders(string ffmpegPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -encoders",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return [];
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            var found = new HashSet<string>();
            foreach (string line in output.Split('\n'))
            {
                if (line.Contains("h264_nvenc")) found.Add("h264_nvenc");
                if (line.Contains("h264_amf")) found.Add("h264_amf");
                if (line.Contains("h264_qsv")) found.Add("h264_qsv");
            }
            return found;
        }
        catch { return []; }
    }

    public string GetInitialEncoder(bool useCuda)
    {
        if (ForcedCpu) return "libx264";
        return useCuda ? PrimaryEncoder : "libx264";
    }

    public List<string> GetFallbackList(string failedEncoder, bool allowCpu = true)
    {
        AttemptedEncoders.Add(failedEncoder);

        if (!string.IsNullOrEmpty(HardwareStrategy) &&
            HardwareByStrategy.ContainsKey(HardwareStrategy.ToUpperInvariant()))
            return allowCpu && failedEncoder != "libx264" ? ["libx264"] : [];

        int startIndex;
        int idx = Array.IndexOf(EncoderPreference, failedEncoder);
        startIndex = idx >= 0 ? idx + 1 : 0;

        var options = new List<string>();
        for (int i = startIndex; i < EncoderPreference.Length; i++)
        {
            string encoder = EncoderPreference[i];
            if (!allowCpu && encoder == "libx264") continue;
            if (encoder != "libx264" && AvailableEncoders.Count > 0 && !AvailableEncoders.Contains(encoder)) continue;
            if (!AttemptedEncoders.Contains(encoder))
                options.Add(encoder);
        }
        return options;
    }

    /// <summary>
    /// Returns (codecArgs, rcLabel) — exact port of get_codec_flags().
    /// codecArgs is a list of ffmpeg command-line arguments starting with "-c:v".
    /// </summary>
    public (List<string> codecArgs, string rcLabel) GetCodecFlags(
        string encoderName, int? videoBitrateKbps, double effectiveDurationSec,
        string fpsExpr = "60", int qualityLevel = 2, bool sizeLocked = true)
    {
        var fpsValue = FpsFraction(fpsExpr);
        if (ForcedCpu)
        {
            string cpuPreset = qualityLevel <= 1 ? "fast" : "medium";
            var flags = new List<string> { "-c:v", "libx264", "-preset", cpuPreset, "-pix_fmt", "yuv420p", "-profile:v", "high", "-level:v", "5.1", "-bf", "2" };
            if (videoBitrateKbps == null)
            {
                string crf = qualityLevel <= 1 ? "20" : "17";
                flags.AddRange(["-crf", crf]);
                return (flags, $"CPU ({cpuPreset}/CRF{crf})");
            }
            int kbps = Math.Min(MaxBitrateKbps, Math.Max(300, videoBitrateKbps.Value));
            flags.AddRange(["-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{VbvBufKbps(kbps)}k"]);
            return (flags, $"CPU ({cpuPreset}/{kbps}k)");
        }

        var vcodec = new List<string> { "-c:v", encoderName };
        int gop = (int)(fpsValue * new Frac(2, 1) + new Frac(1, 2)).ToDouble();
        int keyintMin = (int)(fpsValue + new Frac(1, 2)).ToDouble();
        vcodec.AddRange(["-g", gop.ToString(), "-keyint_min", keyintMin.ToString()]);

        string rcLabel;
        if (encoderName == "h264_nvenc")
        {
            string nvPreset = qualityLevel >= 2 ? "p7" : "p6";
            string multipass = "fullres";
            string lookahead = qualityLevel >= 2 ? "32" : "24";
            string aqStrength = qualityLevel >= 2 ? "10" : "9";

            vcodec.AddRange([
                "-pix_fmt", "yuv420p", "-preset", nvPreset, "-tune", "hq",
                "-rc", sizeLocked && videoBitrateKbps.HasValue ? "cbr" : "vbr",
                "-multipass", multipass, "-spatial-aq", "1", "-temporal-aq", "1",
                "-aq-strength", aqStrength, "-bf", "2", "-b_ref_mode", "middle",
                "-weighted_pred", "0", "-nonref_p", "0", "-strict_gop", "1",
                "-forced-idr", "1", "-rc-lookahead", lookahead, "-profile:v", "high", "-level:v", "5.1"
            ]);

            if (videoBitrateKbps.HasValue)
            {
                int kbps = Math.Min(MaxBitrateKbps, Math.Max(300, videoBitrateKbps.Value));
                vcodec.AddRange(["-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{VbvBufKbps(kbps)}k"]);
                rcLabel = $"NVENC {nvPreset}/{multipass} ({(sizeLocked ? "CBR" : "VBR")})";
            }
            else
            {
                string cqVal = qualityLevel <= 1 ? "22" : (qualityLevel >= 20 ? "15" : "19");
                vcodec.AddRange(["-cq", cqVal]);
                rcLabel = $"NVENC {nvPreset}/{multipass} (CQ {cqVal})";
            }
        }
        else if (encoderName == "h264_amf")
        {
            string amfQuality = qualityLevel <= 1 ? "balanced" : "quality";
            vcodec.AddRange([
                "-pix_fmt", "yuv420p", "-usage", "transcoding", "-quality", amfQuality,
                "-rc", sizeLocked && videoBitrateKbps.HasValue ? "cbr" : "vbr_peak",
                "-enforce_hrd", "1", "-vbaq", "1", "-bf", "2", "-profile:v", "high", "-level:v", "5.1"
            ]);
            if (videoBitrateKbps.HasValue)
            {
                int kbps = Math.Min(MaxBitrateKbps, Math.Max(300, videoBitrateKbps.Value));
                vcodec.AddRange(["-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{VbvBufKbps(kbps)}k"]);
            }
            rcLabel = $"AMD AMF {amfQuality}";
        }
        else if (encoderName == "h264_qsv")
        {
            string qsvPreset = qualityLevel <= 1 ? "balanced" : "slow";
            string laDepth = qualityLevel <= 1 ? "60" : "100";
            vcodec.AddRange([
                "-pix_fmt", "yuv420p", "-preset", qsvPreset, "-bf", "2", "-look_ahead", "1",
                "-look_ahead_depth", laDepth, "-profile:v", "high", "-level:v", "5.1"
            ]);
            if (videoBitrateKbps.HasValue)
            {
                int kbps = Math.Min(MaxBitrateKbps, Math.Max(300, videoBitrateKbps.Value));
                vcodec.AddRange(["-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{VbvBufKbps(kbps)}k"]);
            }
            rcLabel = $"Intel QSV {qsvPreset}";
        }
        else if (encoderName == "libx264")
        {
            string cpuPreset = qualityLevel <= 0 ? "veryfast" : (qualityLevel <= 1 ? "fast" : "medium");
            vcodec.AddRange(["-pix_fmt", "yuv420p"]);
            if (videoBitrateKbps == null)
            {
                string crf = qualityLevel <= 0 ? "23" : (qualityLevel <= 1 ? "20" : "17");
                vcodec.AddRange(["-preset", cpuPreset, "-crf", crf, "-bf", "2", "-profile:v", "high", "-level:v", "5.1"]);
                return (vcodec, $"CPU libx264 ({cpuPreset}/CRF{crf})");
            }
            else
            {
                int kbps = Math.Min(MaxBitrateKbps, Math.Max(300, videoBitrateKbps.Value));
                vcodec.AddRange(["-preset", cpuPreset, "-bf", "2", "-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{VbvBufKbps(kbps)}k", "-profile:v", "high", "-level:v", "5.1"]);
                return (vcodec, $"CPU libx264 ({cpuPreset})");
            }
        }
        else
        {
            vcodec.AddRange(["-pix_fmt", "yuv420p"]);
            rcLabel = $"{encoderName} (Generic)";
        }

        return (vcodec, rcLabel);
    }
}

