using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NAudio.Dsp;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

public static class FrequencyProber
{
    public class DemographicResult
    {
        public bool HasAdultMale { get; set; }
        public bool HasAdultFemale { get; set; }
        public bool HasChild { get; set; }
    }

    /// <summary>
    /// Probes the first few seconds of an MP4 file to detect fundamental pitch ranges.
    /// Adult Male: ~85-180 Hz
    /// Adult Female: ~165-255 Hz
    /// Child: ~250-300+ Hz
    /// </summary>
    private static string GetFFmpegPath()
    {
        string processDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string path = Path.Combine(processDir, "backend", "ffmpeg.exe");
        if (File.Exists(path)) return path;

        path = Path.Combine(processDir, "binaries", "ffmpeg.exe");
        if (File.Exists(path)) return path;
        
        path = Path.Combine(processDir, "..", "..", "..", "..", "..", "binaries", "ffmpeg.exe");
        if (File.Exists(path)) return Path.GetFullPath(path);

        return "ffmpeg.exe";
    }

    public static DemographicResult Probe(string mp4Path, int maxSecondsToProbe = 15)
    {
        var result = new DemographicResult();
        string tempWav = Path.Combine(Path.GetTempPath(), $"probe_{Guid.NewGuid()}.wav");

        try
        {
            var proc = new System.Diagnostics.Process();
            proc.StartInfo.FileName = GetFFmpegPath();
            proc.StartInfo.Arguments = $"-y -i \"{mp4Path}\" -t {maxSecondsToProbe} -vn -acodec pcm_s16le -ar 44100 -ac 1 \"{tempWav}\"";
            proc.StartInfo.CreateNoWindow = true;
            proc.StartInfo.UseShellExecute = false;
            proc.Start();
            proc.WaitForExit();

            if (!File.Exists(tempWav))
            {
                CoreLogger.Fail("FrequencyProber", "FFmpeg failed to extract audio.");
                return result;
            }

            using var reader = new WaveFileReader(tempWav);
            int m = 11; // 2048 samples
            int bufferSize = (int)Math.Pow(2, m);
            var buffer = new float[bufferSize];
            var sampleProvider = reader.ToSampleProvider();

            int sampleRate = sampleProvider.WaveFormat.SampleRate;
            int totalSamplesRead = 0;
            int maxSamples = sampleRate * maxSecondsToProbe;

            float maleEnergy = 0;
            float femaleEnergy = 0;
            float childEnergy = 0;
            int framesProcessed = 0;

            while (totalSamplesRead < maxSamples)
            {
                int read = sampleProvider.Read(buffer, 0, bufferSize);
                if (read < bufferSize) break;

                totalSamplesRead += read;

                var complexBuffer = new Complex[bufferSize];
                for (int i = 0; i < bufferSize; i++)
                {
                    complexBuffer[i].X = (float)(buffer[i] * FastFourierTransform.HannWindow(i, bufferSize));
                    complexBuffer[i].Y = 0;
                }

                FastFourierTransform.FFT(true, m, complexBuffer);

                // Analyze frequency bins
                for (int i = 0; i < bufferSize / 2; i++)
                {
                    double frequency = (i * sampleRate) / (double)bufferSize;
                    double magnitude = Math.Sqrt(complexBuffer[i].X * complexBuffer[i].X + complexBuffer[i].Y * complexBuffer[i].Y);

                    if (frequency >= 85 && frequency <= 165) maleEnergy += (float)magnitude;
                    if (frequency > 165 && frequency <= 250) femaleEnergy += (float)magnitude;
                    if (frequency > 250 && frequency <= 400) childEnergy += (float)magnitude;
                }
                framesProcessed++;
            }

            if (framesProcessed > 0)
            {
                maleEnergy /= framesProcessed;
                femaleEnergy /= framesProcessed;
                childEnergy /= framesProcessed;

                // Simple heuristic thresholds
                float threshold = 5.0f; // Arbitrary threshold for energy detection
                result.HasAdultMale = maleEnergy > threshold;
                result.HasAdultFemale = femaleEnergy > threshold;
                result.HasChild = childEnergy > threshold;
            }
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("FrequencyProber", $"Failed to probe MP4 audio: {ex.Message}");
        }
        finally
        {
            if (File.Exists(tempWav))
            {
                try { File.Delete(tempWav); } catch { }
            }
        }

        return result;
    }
}
