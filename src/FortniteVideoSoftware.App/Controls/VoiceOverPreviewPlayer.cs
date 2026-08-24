using System;
using System.Collections.Generic;
using System.IO;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App.Controls;

public sealed class VoiceOverPreviewTake : IDisposable
{
    public required VoiceOverTake Take { get; init; }
    public required NAudio.Wave.AudioFileReader Reader { get; init; }
    public required NAudio.Wave.WaveOutEvent Player { get; init; }
    public double StartProjectSec { get; set; }

    public void Dispose()
    {
        try { Player.Dispose(); } catch { }
        try { Reader.Dispose(); } catch { }
    }
}

public sealed class VoiceOverPreviewPlayer : IDisposable
{
    private readonly List<VoiceOverPreviewTake> _takes = new();
    private VoiceOverWindow.VoiceOverResult? _result;

    public VoiceOverWindow.VoiceOverResult? Result
    {
        get => _result;
        set
        {
            _result = value;
            Reload();
        }
    }

    public void Reload()
    {
        DisposeTakes();
        if (_result == null) return;

        var takes = GetExistingVoiceOverTakes(_result);
        foreach (var take in takes)
        {
            try
            {
                var reader = new NAudio.Wave.AudioFileReader(take.Path);
                var player = new NAudio.Wave.WaveOutEvent();
                player.Init(reader);
                _takes.Add(new VoiceOverPreviewTake
                {
                    Take = take,
                    Reader = reader,
                    Player = player
                });
            }
            catch (Exception ex)
            {
                CoreLogger.Fail("VoiceOverPreview", $"Failed to load preview take '{Path.GetFileName(take.Path)}': {ex.Message}");
            }
        }
    }

    public void UpdatePlayback(bool isPaused, bool videoEnded, double editedTimeSec, Func<double, double> timeMapper, bool isFrozen = false)
    {
        if (_takes.Count == 0) return;

        bool effectivePaused = isPaused && !isFrozen;

        foreach (var take in _takes)
        {
            take.StartProjectSec = timeMapper(take.Take.StartSec);
            double voiceTime = editedTimeSec - take.StartProjectSec;
            bool shouldPlayVoice = !effectivePaused && !videoEnded && voiceTime >= 0 && voiceTime <= take.Reader.TotalTime.TotalSeconds;

            if (shouldPlayVoice && take.Player.PlaybackState != NAudio.Wave.PlaybackState.Playing)
            {
                take.Reader.CurrentTime = TimeSpan.FromSeconds(voiceTime);
                take.Player.Play();
            }
            else if (!shouldPlayVoice && take.Player.PlaybackState == NAudio.Wave.PlaybackState.Playing)
            {
                take.Player.Pause();
            }
            else if (shouldPlayVoice && take.Player.PlaybackState == NAudio.Wave.PlaybackState.Playing)
            {
                if (Math.Abs(take.Reader.CurrentTime.TotalSeconds - voiceTime) > 0.5)
                {
                    take.Reader.CurrentTime = TimeSpan.FromSeconds(voiceTime);
                }
            }
        }
    }

    private static List<VoiceOverTake> GetExistingVoiceOverTakes(VoiceOverWindow.VoiceOverResult result)
    {
        var takes = new List<VoiceOverTake>();
        if (result.VoiceOverTakes != null)
        {
            foreach (var take in result.VoiceOverTakes)
            {
                if (!string.IsNullOrWhiteSpace(take.Path) && File.Exists(take.Path))
                    takes.Add(take);
            }
        }

        if (takes.Count == 0 &&
            !string.IsNullOrWhiteSpace(result.VoiceOverWavPath) &&
            File.Exists(result.VoiceOverWavPath))
        {
            takes.Add(new VoiceOverTake(result.VoiceOverWavPath, result.VoiceOverStartTimestampSec));
        }
        return takes;
    }

    public void DisposeTakes()
    {
        foreach (var take in _takes)
        {
            take.Dispose();
        }
        _takes.Clear();
    }

    public void Dispose()
    {
        DisposeTakes();
    }
}
