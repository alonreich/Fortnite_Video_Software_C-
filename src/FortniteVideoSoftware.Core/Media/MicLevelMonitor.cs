using NAudio.Wave;
using System;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// VOMON_01 — LISTENS WITHOUT RECORDING.
///
/// The EQ meter used to be fed only by <see cref="VoiceRecorder"/>, so it was frozen until a take
/// was already running. That made the single most common failure — the wrong input device selected,
/// or Windows microphone privacy blocking the app — invisible until AFTER a take had been lost.
/// This opens the same device with the same format, throws every buffer away, and reports only the
/// peak, so the meter proves the microphone works before the user commits to a take.
///
/// ⚠️ It must be stopped before <see cref="VoiceRecorder"/> opens the same device: some drivers
/// (and every exclusive-mode endpoint) refuse a second capture handle. <c>Stop</c> is idempotent
/// and safe to call from the UI thread.
/// </summary>
public sealed class MicLevelMonitor : IDisposable
{
    private WaveInEvent? _waveIn;
    private readonly object _gate = new();
    private volatile bool _running;
    private int _deviceNumber = -1;

    /// <summary>Peak of the last buffer, 0..1. Raised on NAudio's capture thread.</summary>
    public event EventHandler<float>? LevelChanged;

    /// <summary>True while a capture handle is open and buffers are arriving.</summary>
    public bool IsRunning => _running;

    /// <summary>Device this monitor is currently listening to, or -1 when stopped.</summary>
    public int DeviceNumber => _deviceNumber;

    /// <summary>
    /// Opens (or re-opens) the given device. Restarting on the SAME device is a no-op, so this can
    /// be called freely from selection-changed handlers without churning the driver.
    /// </summary>
    public void Start(int deviceNumber)
    {
        lock (_gate)
        {
            if (_running && _deviceNumber == deviceNumber) return;
            StopCore();

            int count;
            try { count = WaveInEvent.DeviceCount; }
            catch (Exception ex) { CoreLogger.Swallowed(ex); return; }
            if (count <= 0) return;

            int device = Math.Clamp(deviceNumber, 0, count - 1);

            try
            {
                var waveIn = new WaveInEvent
                {
                    DeviceNumber = device,
                    WaveFormat = new WaveFormat(44100, 1),
                    BufferMilliseconds = 50
                };
                waveIn.DataAvailable += OnDataAvailable;
                waveIn.RecordingStopped += OnRecordingStopped;
                waveIn.StartRecording();

                _waveIn = waveIn;
                _deviceNumber = device;
                _running = true;
                CoreLogger.Info("MicMonitor", $"Idle input monitoring started on device {device}.");
            }
            catch (Exception ex)
            {
                // Not fatal and not worth a modal: the meter simply stays still, and recording is
                // still attempted normally when the user presses record.
                CoreLogger.Debug("MicMonitor", $"Could not open device {device} for monitoring: {ex.Message}");
                StopCore();
            }
        }
    }

    /// <summary>Closes the capture handle. Safe to call when already stopped.</summary>
    public void Stop()
    {
        lock (_gate) { StopCore(); }
    }

    private void StopCore()
    {
        var waveIn = _waveIn;
        _waveIn = null;
        _running = false;
        _deviceNumber = -1;

        if (waveIn == null) return;
        try { waveIn.DataAvailable -= OnDataAvailable; } catch (Exception ex) { CoreLogger.Swallowed(ex); }
        try { waveIn.RecordingStopped -= OnRecordingStopped; } catch (Exception ex) { CoreLogger.Swallowed(ex); }
        try { waveIn.StopRecording(); } catch (Exception ex) { CoreLogger.Debug("MicMonitor", $"StopRecording threw: {ex.Message}"); }
        try { waveIn.Dispose(); } catch (Exception ex) { CoreLogger.Debug("MicMonitor", $"Dispose threw: {ex.Message}"); }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs a)
    {
        try
        {
            float max = 0;
            for (int i = 0; i + 1 < a.BytesRecorded; i += 2)
            {
                short sample = (short)((a.Buffer[i + 1] << 8) | a.Buffer[i]);
                float s32 = sample / 32768f;
                if (s32 < 0) s32 = -s32;
                if (s32 > max) max = s32;
            }
            LevelChanged?.Invoke(this, max);
        }
        catch (Exception ex) { CoreLogger.Swallowed(ex); }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            CoreLogger.Debug("MicMonitor", $"Monitoring stopped with an error: {e.Exception.Message}");
        }
    }

    public void Dispose() => Stop();
}
