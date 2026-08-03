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
        catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }

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

            _waveIn.StartRecording();
            _isRecording = true;
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

            float max = 0;
            for (int i = 0; i + 1 < a.BytesRecorded; i += 2)
            {
                short sample = (short)((a.Buffer[i + 1] << 8) | a.Buffer[i]);
                float sample32 = sample / 32768f;
                if (sample32 < 0) sample32 = -sample32;
                if (sample32 > max) max = sample32;
            }
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
        try { _recordingStopped?.Set(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
    }

    // =====================================================================================
    // ISSUE_03 (audit round 6): PauseRecording() and ResumeRecording() lived here with zero
    // call sites anywhere in the solution. They were NOT wired up, because resuming into the
    // SAME open WAV is the wrong primitive for this app:
    //
    //   * WaveInEvent.StopRecording() is asynchronous — buffers already in flight keep
    //     arriving after it returns — and StartRecording() has its own device spin-up. Each
    //     pause/resume cycle would therefore splice in an unmeasured few tens of milliseconds.
    //   * Those errors ACCUMULATE, because one continuous WAV carries a single start anchor.
    //     Pause five times and the tail of the voiceover is late by the sum of five unknowns,
    //     with nothing the user or the exporter can do to correct it.
    //
    // VoiceOverWindow implements pause/resume by TAKE-SPLITTING instead: pausing closes the
    // current take, resuming opens a brand-new one that re-arms and re-anchors against the real
    // video clock. Every segment carries its own independently measured StartSec, so error
    // cannot accumulate. See the big sync-model comment in VoiceOverWindow.axaml.cs.
    //
    // Do not reintroduce these two methods. If you need pause/resume, use take-splitting.
    // =====================================================================================

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

            try { waveIn.DataAvailable -= OnDataAvailable; } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
            try { waveIn.RecordingStopped -= OnRecordingStopped; } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
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
                try { _writer.Flush(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
                try { _writer.Dispose(); }
                catch (Exception ex) { CoreLogger.Debug("VoiceRecorder", $"Closing the WAV writer threw: {ex.Message}"); }
                _writer = null;
            }
        }

        try { _recordingStopped?.Dispose(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
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
        catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
    }

    public void Dispose()
    {
        StopRecording();
    }
}
