using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
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
        public double AdultMaleConfidence { get; set; }
        public double AdultFemaleConfidence { get; set; }
        public double ChildConfidence { get; set; }
        public double AdultMaleFrequencyHz { get; set; }
        public double AdultFemaleFrequencyHz { get; set; }
        public double ChildFrequencyHz { get; set; }
    }

    /// <summary>
    /// Probes the first few seconds of an MP4 file to detect fundamental pitch ranges.
    /// Adult Male: ~85-180 Hz
    /// Adult Female: ~165-255 Hz
    /// Child: ~250-300+ Hz
    /// </summary>
    private static string GetFFmpegPath()
    {
        return BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
    }

    public static DemographicResult Probe(string mp4Path, int maxSecondsToProbe = 15, CancellationToken cancellationToken = default, double startSeconds = 0)
    {
        var result = new DemographicResult();
        string tempWav = Path.Combine(ApplicationPaths.CreateDefault().TempDirectory, $"probe_{Guid.NewGuid():N}.wav");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tempWav)!);
            using var proc = new Process();
            proc.StartInfo.FileName = GetFFmpegPath();
            string seekArg = startSeconds > 0
                ? $"-ss {startSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                : "";
            proc.StartInfo.Arguments = $"-y -hide_banner -loglevel error {seekArg}-i \"{mp4Path}\" -t {maxSecondsToProbe} -vn -acodec pcm_s16le -ar 22050 -ac 1 \"{tempWav}\"";
            proc.StartInfo.CreateNoWindow = true;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.RedirectStandardError = true;
            proc.Start();
            using var killRegistration = cancellationToken.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            });

            int timeoutMs = Math.Max(12_000, (maxSecondsToProbe + 5) * 1500);
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("Voice frequency extraction timed out.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            string stderr = proc.StandardError.ReadToEnd();
            if (proc.ExitCode != 0)
            {
                CoreLogger.Fail("FrequencyProber", $"FFmpeg audio extraction failed with code {proc.ExitCode}. {stderr}");
                return result;
            }

            if (!File.Exists(tempWav))
            {
                CoreLogger.Fail("FrequencyProber", "FFmpeg failed to extract audio.");
                return result;
            }

            using var reader = new WaveFileReader(tempWav);
            int m = 11;
            int bufferSize = (int)Math.Pow(2, m);
            var buffer = new float[bufferSize];
            var sampleProvider = reader.ToSampleProvider();

            int sampleRate = sampleProvider.WaveFormat.SampleRate;
            int totalSamplesRead = 0;
            int maxSamples = sampleRate * maxSecondsToProbe;

            double maleScoreTotal = 0;
            double femaleScoreTotal = 0;
            double childScoreTotal = 0;
            int maleHits = 0;
            int femaleHits = 0;
            int childHits = 0;
            int framesProcessed = 0;
            int voicedFrames = 0;
            double maleFreqWeighted = 0;
            double femaleFreqWeighted = 0;
            double childFreqWeighted = 0;
            double maleFreqWeight = 0;
            double femaleFreqWeight = 0;
            double childFreqWeight = 0;

            while (totalSamplesRead < maxSamples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = sampleProvider.Read(buffer, 0, bufferSize);
                if (read < bufferSize) break;

                totalSamplesRead += read;
                double rms = 0;
                for (int i = 0; i < bufferSize; i++)
                    rms += buffer[i] * buffer[i];
                rms = Math.Sqrt(rms / bufferSize);
                if (rms < 0.004)
                {
                    framesProcessed++;
                    continue;
                }

                var complexBuffer = new Complex[bufferSize];
                for (int i = 0; i < bufferSize; i++)
                {
                    complexBuffer[i].X = (float)(buffer[i] * FastFourierTransform.HannWindow(i, bufferSize));
                    complexBuffer[i].Y = 0;
                }

                FastFourierTransform.FFT(true, m, complexBuffer);

                double maleEnergy = 0, femaleEnergy = 0, childEnergy = 0, voiceBandEnergy = 0, totalSpeechishEnergy = 0;
                double malePeak = 0, femalePeak = 0, childPeak = 0;
                double malePeakFreq = 0, femalePeakFreq = 0, childPeakFreq = 0;
                int maleBins = 0, femaleBins = 0, childBins = 0, voiceBins = 0, totalBins = 0;
                for (int i = 0; i < bufferSize / 2; i++)
                {
                    double frequency = (i * sampleRate) / (double)bufferSize;
                    double magnitude = Math.Sqrt(complexBuffer[i].X * complexBuffer[i].X + complexBuffer[i].Y * complexBuffer[i].Y);

                    if (frequency >= 70 && frequency <= 1000)
                    {
                        totalSpeechishEnergy += magnitude;
                        totalBins++;
                    }

                    if (frequency >= 85 && frequency <= 165)
                    {
                        maleEnergy += magnitude;
                        if (magnitude > malePeak)
                        {
                            malePeak = magnitude;
                            malePeakFreq = frequency;
                        }
                        maleBins++;
                    }
                    else if (frequency > 165 && frequency <= 250)
                    {
                        femaleEnergy += magnitude;
                        if (magnitude > femalePeak)
                        {
                            femalePeak = magnitude;
                            femalePeakFreq = frequency;
                        }
                        femaleBins++;
                    }
                    else if (frequency > 250 && frequency <= 400)
                    {
                        childEnergy += magnitude;
                        if (magnitude > childPeak)
                        {
                            childPeak = magnitude;
                            childPeakFreq = frequency;
                        }
                        childBins++;
                    }

                    if (frequency >= 85 && frequency <= 400)
                    {
                        voiceBandEnergy += magnitude;
                        voiceBins++;
                    }
                }

                if (voiceBins == 0 || totalBins == 0 || totalSpeechishEnergy <= 0)
                {
                    framesProcessed++;
                    continue;
                }

                double voiceDensity = voiceBandEnergy / voiceBins;
                double speechishDensity = totalSpeechishEnergy / totalBins;
                if (voiceDensity < speechishDensity * 0.9)
                {
                    framesProcessed++;
                    continue;
                }

                voicedFrames++;
                double maleDensity = maleBins > 0 ? maleEnergy / maleBins : 0;
                double femaleDensity = femaleBins > 0 ? femaleEnergy / femaleBins : 0;
                double childDensity = childBins > 0 ? childEnergy / childBins : 0;
                double strongest = Math.Max(maleDensity, Math.Max(femaleDensity, childDensity));
                if (strongest <= 0)
                {
                    framesProcessed++;
                    continue;
                }

                double dominance = 0.78;
                double maleScore = Math.Min(1.0, (maleDensity / strongest) * (malePeak > 0 ? 1.0 : 0.0));
                double femaleScore = Math.Min(1.0, (femaleDensity / strongest) * (femalePeak > 0 ? 1.0 : 0.0));
                double childScore = Math.Min(1.0, (childDensity / strongest) * (childPeak > 0 ? 1.0 : 0.0));

                if (maleScore >= dominance) maleHits++;
                if (femaleScore >= dominance) femaleHits++;
                if (childScore >= dominance) childHits++;

                if (malePeakFreq > 0 && maleScore >= dominance)
                {
                    maleFreqWeighted += malePeakFreq * maleScore;
                    maleFreqWeight += maleScore;
                }
                if (femalePeakFreq > 0 && femaleScore >= dominance)
                {
                    femaleFreqWeighted += femalePeakFreq * femaleScore;
                    femaleFreqWeight += femaleScore;
                }
                if (childPeakFreq > 0 && childScore >= dominance)
                {
                    childFreqWeighted += childPeakFreq * childScore;
                    childFreqWeight += childScore;
                }

                maleScoreTotal += maleScore;
                femaleScoreTotal += femaleScore;
                childScoreTotal += childScore;
                framesProcessed++;
            }

            if (voicedFrames > 0)
            {
                result.AdultMaleConfidence = Math.Clamp((maleScoreTotal / voicedFrames + (double)maleHits / voicedFrames) / 2.0, 0, 1);
                result.AdultFemaleConfidence = Math.Clamp((femaleScoreTotal / voicedFrames + (double)femaleHits / voicedFrames) / 2.0, 0, 1);
                result.ChildConfidence = Math.Clamp((childScoreTotal / voicedFrames + (double)childHits / voicedFrames) / 2.0, 0, 1);

                const double confidenceThreshold = 0.42;
                const double sustainedHitThreshold = 0.08;
                result.HasAdultMale = result.AdultMaleConfidence >= confidenceThreshold && (double)maleHits / voicedFrames >= sustainedHitThreshold;
                result.HasAdultFemale = result.AdultFemaleConfidence >= confidenceThreshold && (double)femaleHits / voicedFrames >= sustainedHitThreshold;
                result.HasChild = result.ChildConfidence >= confidenceThreshold && (double)childHits / voicedFrames >= sustainedHitThreshold;

                if (result.HasAdultMale && maleFreqWeight > 0)
                    result.AdultMaleFrequencyHz = Math.Clamp(maleFreqWeighted / maleFreqWeight, 85, 165);
                if (result.HasAdultFemale && femaleFreqWeight > 0)
                    result.AdultFemaleFrequencyHz = Math.Clamp(femaleFreqWeighted / femaleFreqWeight, 166, 250);
                if (result.HasChild && childFreqWeight > 0)
                    result.ChildFrequencyHz = Math.Clamp(childFreqWeighted / childFreqWeight, 251, 400);
            }

            CoreLogger.Info("FrequencyProber",
                $"Frames={framesProcessed}, voiced={voicedFrames}, confidence: male={result.AdultMaleConfidence:P0}@{result.AdultMaleFrequencyHz:F0}Hz, female={result.AdultFemaleConfidence:P0}@{result.AdultFemaleFrequencyHz:F0}Hz, child={result.ChildConfidence:P0}@{result.ChildFrequencyHz:F0}Hz");
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException || ex is TimeoutException)
            {
                throw;
            }
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
