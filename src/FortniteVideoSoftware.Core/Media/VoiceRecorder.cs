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
    private bool _isRecording;

    public event EventHandler<float>? VolumeChanged;

    public VoiceRecorder(string outputPath)
    {
        _outputPath = outputPath;
    }

    public void StartRecording()
    {
        if (_isRecording) return;
        
        _waveIn = new WaveInEvent();
        _waveIn.DeviceNumber = 0; // Default microphone
        _waveIn.WaveFormat = new WaveFormat(44100, 1); // 44.1kHz mono
        
        _writer = new WaveFileWriter(_outputPath, _waveIn.WaveFormat);
        
        _waveIn.DataAvailable += (s, a) =>
        {
            _writer.Write(a.Buffer, 0, a.BytesRecorded);
            
            // Calculate RMS for live EQ volume meter
            float max = 0;
            for (int i = 0; i < a.BytesRecorded; i += 2)
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
            _waveIn.StopRecording();
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

    public void Dispose()
    {
        StopRecording();
    }
}
