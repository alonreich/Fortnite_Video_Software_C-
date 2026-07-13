using NAudio.Wave;
using NAudio.Dsp;
using System;
using System.IO;
using System.Collections.Generic;

namespace FortniteVideoSoftware.Core.Media;

public class VoiceRecorder : IDisposable
{
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string _outputPath;
    private readonly int _deviceNumber;
    private bool _isRecording;

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
        catch
        {
        }

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
            _waveIn = new WaveInEvent
            {
                DeviceNumber = Math.Min(_deviceNumber, Math.Max(0, WaveInEvent.DeviceCount - 1)),
                WaveFormat = new WaveFormat(44100, 1)
            };

            _writer = new WaveFileWriter(_outputPath, _waveIn.WaveFormat);

            _waveIn.DataAvailable += (s, a) =>
            {
                _writer?.Write(a.Buffer, 0, a.BytesRecorded);

                float max = 0;
                for (int i = 0; i + 1 < a.BytesRecorded; i += 2)
                {
                    short sample = (short)((a.Buffer[i + 1] << 8) | a.Buffer[i]);
                    float sample32 = sample / 32768f;
                    if (sample32 < 0) sample32 = -sample32;
                    if (sample32 > max) max = sample32;
                }
                VolumeChanged?.Invoke(this, max);
            };

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

    public void PauseRecording()
    {
        if (!_isRecording) return;
        _waveIn?.StopRecording();
        _isRecording = false;
    }

    public void ResumeRecording()
    {
        if (_isRecording) return;
        _waveIn?.StartRecording();
        _isRecording = true;
    }

    public void StopRecording()
    {
        if (_waveIn != null)
        {
            try
            {
                _waveIn.StopRecording();
            }
            catch
            {
            }
            _waveIn.Dispose();
            _waveIn = null;
        }
        if (_writer != null)
        {
            _writer.Dispose();
            _writer = null;
        }
        _isRecording = false;
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
        catch
        {
        }
    }

    public void Dispose()
    {
        StopRecording();
    }
}
