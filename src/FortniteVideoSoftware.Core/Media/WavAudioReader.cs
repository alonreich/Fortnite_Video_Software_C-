// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// AOTCLEAN_02 — A WAV-ONLY REPLACEMENT FOR NAudio's <c>AudioFileReader</c>.
///
/// <c>AudioFileReader</c> lives in the NAudio umbrella assembly and falls back to
/// <c>MediaFoundationReader</c> for anything that is not WAV/MP3/AIFF. MediaFoundationReader is
/// built on classic COM interop (<c>[ComImport]</c> interfaces marshalled through P/Invoke), which
/// NativeAOT does not support at all: the ILC reported it as IL2050, and at runtime that path would
/// throw. Referencing the umbrella package also dragged NAudio.Wasapi, NAudio.Asio, NAudio.Midi and
/// NAudio.WinForms into a NativeAOT binary that uses none of them.
///
/// Every file this suite reads through NAudio is a WAV it wrote itself (voice-over takes from
/// <see cref="VoiceRecorder"/>, the bundled UI cue sounds). So this reader is exactly what
/// AudioFileReader does for a WAV (WaveFileReader → SampleChannel → IEEE float, with the same
/// byte-position conversion), and the solution now references only NAudio.Core and NAudio.WinMM.
/// A non-WAV path fails loudly here instead of silently taking an interop route AOT cannot run.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class WavAudioReader : WaveStream, ISampleProvider
{
    private readonly WaveFileReader _reader;
    private readonly SampleChannel _channel;
    private readonly int _destBytesPerSample;
    private readonly int _sourceBytesPerSample;
    private readonly long _length;
    private readonly object _lock = new();

    public string FileName { get; }

    public WavAudioReader(string path)
    {
        FileName = path;
        _reader = new WaveFileReader(path);
        var enc = _reader.WaveFormat.Encoding;
        if (enc != WaveFormatEncoding.Pcm && enc != WaveFormatEncoding.IeeeFloat && enc != WaveFormatEncoding.Extensible)
        {
            _reader.Dispose();
            throw new NotSupportedException($"'{System.IO.Path.GetFileName(path)}' is a {enc} WAV. Only PCM and IEEE-float WAV files can be played here.");
        }

        _sourceBytesPerSample = Math.Max(1, _reader.WaveFormat.BitsPerSample / 8 * _reader.WaveFormat.Channels);
        _channel = new SampleChannel(_reader, forceStereo: false);
        _destBytesPerSample = 4 * _channel.WaveFormat.Channels;
        _length = SourceToDest(_reader.Length);
    }

    public override WaveFormat WaveFormat => _channel.WaveFormat;

    public override long Length => _length;

    public override long Position
    {
        get => SourceToDest(_reader.Position);
        set { lock (_lock) { _reader.Position = DestToSource(value); } }
    }

    /// <summary>Linear gain applied to the samples (1.0 = unchanged).</summary>
    public float Volume
    {
        get => _channel.Volume;
        set => _channel.Volume = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var waveBuffer = new WaveBuffer(buffer);
        int samplesRead = Read(waveBuffer.FloatBuffer, offset / 4, count / 4);
        return samplesRead * 4;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_lock) { return _channel.Read(buffer, offset, count); }
    }

    private long SourceToDest(long sourceBytes) => _destBytesPerSample * (sourceBytes / _sourceBytesPerSample);

    private long DestToSource(long destBytes) => _sourceBytesPerSample * (destBytes / _destBytesPerSample);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _reader.Dispose();
        base.Dispose(disposing);
    }
}
