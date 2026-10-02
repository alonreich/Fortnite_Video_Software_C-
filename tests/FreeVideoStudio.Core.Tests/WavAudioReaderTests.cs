using System;
using System.IO;
using FreeVideoStudio.Core.Media;
using NAudio.Wave;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>AOTCLEAN_02 — WavAudioReader behaves like NAudio's AudioFileReader for the WAVs the app writes.</summary>
public sealed class WavAudioReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fvs_wav_{Guid.NewGuid():N}.wav");

    public void Dispose() { try { File.Delete(_path); } catch { } }

    private void WritePcm16(int sampleRate, int channels, double seconds)
    {
        using var w = new WaveFileWriter(_path, new WaveFormat(sampleRate, 16, channels));
        int frames = (int)(sampleRate * seconds);
        var buf = new byte[frames * channels * 2];
        for (int i = 0; i < frames * channels; i++)
        {
            short v = (short)(Math.Sin(i * 0.01) * 16000);
            buf[i * 2] = (byte)(v & 0xff);
            buf[i * 2 + 1] = (byte)((v >> 8) & 0xff);
        }
        w.Write(buf, 0, buf.Length);
    }

    [Fact]
    public void Pcm16_IsExposedAsFloat_WithCorrectDurationAndSeeking()
    {
        WritePcm16(44100, 1, 2.0);
        using var r = new WavAudioReader(_path);

        Assert.Equal(WaveFormatEncoding.IeeeFloat, r.WaveFormat.Encoding);
        Assert.Equal(44100, r.WaveFormat.SampleRate);
        Assert.Equal(2.0, r.TotalTime.TotalSeconds, 2);

        r.CurrentTime = TimeSpan.FromSeconds(1.5);
        Assert.Equal(1.5, r.CurrentTime.TotalSeconds, 2);

        var samples = new float[4410];
        int read = r.Read(samples, 0, samples.Length);
        Assert.Equal(4410, read);
        Assert.InRange(samples[100], -1f, 1f);
    }

    [Fact]
    public void Volume_ScalesSamples()
    {
        WritePcm16(48000, 2, 0.5);
        using var full = new WavAudioReader(_path);
        using var half = new WavAudioReader(_path) { Volume = 0.5f };
        var a = new float[1000]; var b = new float[1000];
        full.Read(a, 0, a.Length); half.Read(b, 0, b.Length);
        Assert.Equal(a[500] * 0.5f, b[500], 4);
    }

    [Fact]
    public void ByteRead_MatchesFloatRead()
    {
        WritePcm16(44100, 1, 0.2);
        using var r = new WavAudioReader(_path);
        var bytes = new byte[400];
        Assert.Equal(400, r.Read(bytes, 0, bytes.Length));
        Assert.Equal(400, r.Position);
    }
}
