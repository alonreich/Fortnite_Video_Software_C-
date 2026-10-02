using System;
using System.Collections.Generic;
using System.IO;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Controls;

public sealed class VoiceOverPreviewTake : IDisposable
{
    public required VoiceOverTake Take { get; init; }
    public required FreeVideoStudio.Core.Media.WavAudioReader Reader { get; init; }
    public required NAudio.Wave.WaveOutEvent Player { get; init; }
    public double StartProjectSec { get; set; }
    /// <summary>MUSICSYNC_02 — consecutive out-of-tolerance readings (PreviewAudioSync).</summary>
    public int DriftStrikes;

    public void Dispose()
    {
        try { Player.Dispose(); } catch (System.Exception swallowed)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed);
        }
        try { Reader.Dispose(); } catch (System.Exception swallowed2)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed2);
        }
    }
}

/// <summary>GRANULARPERF_01 — all file/device operations belong to one bounded background worker.</summary>
public sealed class VoiceOverPreviewPlayer : IDisposable
{
    private readonly List<VoiceOverPreviewTake> _takes = new();
    private readonly System.Threading.Channels.Channel<PlaybackRequest> _pending =
        System.Threading.Channels.Channel.CreateBounded<PlaybackRequest>(
            new System.Threading.Channels.BoundedChannelOptions(1)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly object _stateGate = new();
    private VoiceOverWindow.VoiceOverResult? _result;
    private volatile bool _disposed;
    private PlaybackRequest _latest = new([], 0, true, false, 0, static t => t, false);
    private sealed record PlaybackRequest(VoiceOverTake[] Takes, long Revision, bool Paused, bool Ended,
        double Time, Func<double, double> Mapper, bool Frozen);
    public Task Completion { get; }

    public VoiceOverPreviewPlayer()
    {
        MpvIpcClient.GlobalMasterVolumeChanged += OnMasterVolumeChanged;
        Completion = Task.Run(RunAsync);
    }

    private void OnMasterVolumeChanged(int volume)
    {
        lock (_stateGate) { if (!_disposed) _pending.Writer.TryWrite(_latest); }
    }

    public VoiceOverWindow.VoiceOverResult? Result
    {
        get => _result;
        set { _result = value; Reload(); }
    }

    public void Reload()
    {
        var candidates = _result?.VoiceOverTakes?.ToArray() ?? [];
        if (candidates.Length == 0 && !string.IsNullOrWhiteSpace(_result?.VoiceOverWavPath))
            candidates = [new(_result.VoiceOverWavPath, _result.VoiceOverStartTimestampSec)];
        lock (_stateGate)
        {
            if (_disposed) return;
            _latest = _latest with { Takes = candidates, Revision = _latest.Revision + 1 };
            _pending.Writer.TryWrite(_latest);
        }
    }

    public void UpdatePlayback(bool isPaused, bool videoEnded, double editedTimeSec,
        Func<double, double> timeMapper, bool isFrozen = false)
    {
        lock (_stateGate)
        {
            if (_disposed || _latest.Takes.Length == 0) return;
            _latest = _latest with { Paused = isPaused, Ended = videoEnded, Time = editedTimeSec,
                Mapper = timeMapper, Frozen = isFrozen };
            _pending.Writer.TryWrite(_latest);
        }
    }

    private async Task RunAsync()
    {
        long revision = -1;
        try
        {
            await foreach (var request in _pending.Reader.ReadAllAsync())
            {
                if (_disposed) break;
                if (revision != request.Revision)
                {
                    ReleaseTakes();
                    foreach (var take in request.Takes)
                    {
                        if (_disposed) break;
                        if (string.IsNullOrWhiteSpace(take.Path) || !File.Exists(take.Path)) continue;
                        FreeVideoStudio.Core.Media.WavAudioReader? reader = null;
                        NAudio.Wave.WaveOutEvent? player = null;
                        try
                        {
                            reader = new(take.Path);
                            player = Infrastructure.PreviewAudioSync.CreateVoicePlayer();
                            player.Init(reader);
                            _takes.Add(new() { Take = take, Reader = reader, Player = player });
                        }
                        catch (Exception ex)
                        {
                            player?.Dispose();
                            reader?.Dispose();
                            CoreLogger.Fail("VoiceOverPreview", $"Could not open '{Path.GetFileName(take.Path)}': {ex.Message}");
                        }
                    }
                    revision = request.Revision;
                }
                if (_disposed) break;
                if (_pending.Reader.TryPeek(out _)) continue;
                foreach (var take in _takes)
                {
                    try
                    {
                        take.Reader.Volume = MpvIpcClient.GlobalMasterVolume / 100f;
                        take.StartProjectSec = request.Mapper(take.Take.StartSec);
                        double voiceTime = request.Time - take.StartProjectSec;
                        bool play = (!request.Paused || request.Frozen) && !request.Ended &&
                            voiceTime >= 0 && voiceTime <= take.Reader.TotalTime.TotalSeconds;
                        Infrastructure.PreviewAudioSync.SyncVoiceTake(take.Reader, take.Player, play, voiceTime, ref take.DriftStrikes);
                    }
                    catch (Exception ex) { CoreLogger.Swallowed(ex); }
                }
            }
        }
        finally { ReleaseTakes(); }
    }

    private void ReleaseTakes()
    {
        foreach (var take in _takes) take.Dispose();
        _takes.Clear();
    }

    public void DisposeTakes()
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            _latest = _latest with { Takes = [], Revision = _latest.Revision + 1 };
            _pending.Writer.TryWrite(_latest);
        }
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true;
            MpvIpcClient.GlobalMasterVolumeChanged -= OnMasterVolumeChanged;
            _pending.Writer.TryComplete();
        }
    }
}
