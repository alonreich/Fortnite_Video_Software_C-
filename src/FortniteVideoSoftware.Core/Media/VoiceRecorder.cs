using NAudio.Wave;
using NAudio.Dsp;
using System;
using System.IO;
using System.Threading;
using System.Collections.Generic;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

public class VoiceRecorder : IDisposable
{
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string _outputPath;
    private readonly int _deviceNumber;
    private volatile bool _isRecording;

    private readonly object _writerLock = new object();
    private volatile bool _stopping;
    private ManualResetEventSlim? _recordingStopped;

    /// <summary>How long StopRecording waits for NAudio to hand over its final buffers.</summary>
    private const int StopDrainTimeoutMs = 2000;

    // VODIAG_01 — capture accounting. Without these the only evidence a failed recording leaves
    // behind is a WAV that may or may not exist, which cannot tell "the mic never opened" apart
    // from "the mic opened and Windows fed it digital silence" (microphone privacy blocked).
    private long _bytesCaptured;
    private float _peakSeen;
    private int _buffersSeen;

    /// <summary>Total PCM bytes handed over by the capture device for this recording.</summary>
    public long BytesCaptured => System.Threading.Interlocked.Read(ref _bytesCaptured);

    /// <summary>Loudest absolute sample seen (0..1). Exactly 0 over a long take means silence.</summary>
    public float PeakSeen => _peakSeen;

    /// <summary>Number of DataAvailable callbacks received. Zero means the device never delivered.</summary>
    public int BuffersSeen => _buffersSeen;

    public event EventHandler<float>? VolumeChanged;

    public VoiceRecorder(string outputPath, int deviceNumber = 0)
    {
        _outputPath = outputPath;
        _deviceNumber = Math.Max(0, deviceNumber);
    }

    public static IReadOnlyList<string> GetInputDeviceNames()
    {
        var devices = new List<string>();
        try
        {
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var caps = WaveInEvent.GetCapabilities(i);
                string name = string.IsNullOrWhiteSpace(caps.ProductName)
                    ? $"Microphone {i + 1}"
                    : caps.ProductName;
                devices.Add($"{i + 1}. {name}");
            }
        }
        catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

        return devices;
    }

    public static bool HasInputDevice
    {
        get
        {
            try
            {
                return WaveInEvent.DeviceCount > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public void StartRecording()
    {
        if (_isRecording) return;

        if (!HasInputDevice)
        {
            throw new InvalidOperationException("No microphone input device is available.");
        }

        try
        {
            _stopping = false;
            _recordingStopped = new ManualResetEventSlim(false);

            _waveIn = new WaveInEvent
            {
                DeviceNumber = Math.Min(_deviceNumber, Math.Max(0, WaveInEvent.DeviceCount - 1)),
                WaveFormat = new WaveFormat(44100, 1)
            };

            _writer = new WaveFileWriter(_outputPath, _waveIn.WaveFormat);

            _waveIn.DataAvailable += OnDataAvailable;

            _waveIn.RecordingStopped += OnRecordingStopped;

            _bytesCaptured = 0;
            _peakSeen = 0;
            _buffersSeen = 0;

            _waveIn.StartRecording();
            _isRecording = true;

            string deviceLabel;
            try { deviceLabel = WaveInEvent.GetCapabilities(_waveIn.DeviceNumber).ProductName; }
            catch (System.Exception) { deviceLabel = "(name unavailable)"; }
            CoreLogger.Info("VoiceRecorder",
                $"Capture started on device {_waveIn.DeviceNumber} '{deviceLabel}' at {_waveIn.WaveFormat.SampleRate}Hz/{_waveIn.WaveFormat.Channels}ch -> '{Path.GetFileName(_outputPath)}'.");
        }
        catch
        {
            StopRecording();
            TryDeletePartialOutput();
            throw;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs a)
    {
        try
        {
            lock (_writerLock)
            {
                if (_stopping) return;
                var writer = _writer;
                if (writer == null) return;
                writer.Write(a.Buffer, 0, a.BytesRecorded);
            }

            System.Threading.Interlocked.Add(ref _bytesCaptured, a.BytesRecorded);
            int seen = System.Threading.Interlocked.Increment(ref _buffersSeen);
            if (seen == 1)
            {
                CoreLogger.Info("VoiceRecorder", $"First audio buffer received ({a.BytesRecorded} bytes).");
            }

            float max = 0;
            for (int i = 0; i + 1 < a.BytesRecorded; i += 2)
            {
                short sample = (short)((a.Buffer[i + 1] << 8) | a.Buffer[i]);
                float sample32 = sample / 32768f;
                if (sample32 < 0) sample32 = -sample32;
                if (sample32 > max) max = sample32;
            }
            if (max > _peakSeen) _peakSeen = max;
            VolumeChanged?.Invoke(this, max);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("VoiceRecorder", $"Dropped a captured audio buffer: {ex.Message}");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            CoreLogger.Fail("VoiceRecorder", $"Capture stopped with an error: {e.Exception.Message}");
        }
        try { _recordingStopped?.Set(); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
    }


    public void StopRecording()
    {
        var waveIn = _waveIn;
        bool wasRecording = _isRecording;
        _isRecording = false;

        if (waveIn != null)
        {
            try { waveIn.StopRecording(); }
            catch (Exception ex) { CoreLogger.Debug("VoiceRecorder", $"StopRecording threw: {ex.Message}"); }

            if (wasRecording)
            {
                try { _recordingStopped?.Wait(StopDrainTimeoutMs); }
                catch (Exception ex) { CoreLogger.Debug("VoiceRecorder", $"Wait for RecordingStopped failed: {ex.Message}"); }
            }

            _stopping = true;

            try { waveIn.DataAvailable -= OnDataAvailable; } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
            try { waveIn.RecordingStopped -= OnRecordingStopped; } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
            try { waveIn.Dispose(); }
            catch (Exception ex) { CoreLogger.Debug("VoiceRecorder", $"Disposing the capture device threw: {ex.Message}"); }

            _waveIn = null;
        }
        else
        {
            _stopping = true;
        }

        lock (_writerLock)
        {
            if (_writer != null)
            {
                try { _writer.Flush(); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
                try { _writer.Dispose(); }
                catch (Exception ex) { CoreLogger.Debug("VoiceRecorder", $"Closing the WAV writer threw: {ex.Message}"); }
                _writer = null;
            }
        }

        if (wasRecording)
        {
            long bytes = System.Threading.Interlocked.Read(ref _bytesCaptured);
            double seconds = bytes / (44100.0 * 2.0);
            CoreLogger.Info("VoiceRecorder",
                $"Capture stopped: {_buffersSeen} buffers, {bytes} bytes (~{seconds:0.###}s), peak {_peakSeen:0.####}.");

            if (bytes == 0)
            {
                CoreLogger.Fail("VoiceRecorder",
                    "The microphone opened but delivered no audio at all. The device is present but not producing data.");
            }
            else if (_peakSeen <= 0.0001f)
            {
                CoreLogger.Fail("VoiceRecorder",
                    "The microphone delivered pure digital silence. On Windows this is almost always microphone access being blocked in Settings > Privacy & security > Microphone (check both 'Microphone access' and 'Let desktop apps access your microphone'), or the wrong input device being selected.");
            }
        }

        try { _recordingStopped?.Dispose(); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
        _recordingStopped = null;
    }

    private void TryDeletePartialOutput()
    {
        try
        {
            if (File.Exists(_outputPath))
            {
                File.Delete(_outputPath);
            }
        }
        catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
    }

    public void Dispose()
    {
        StopRecording();
    }
}
