// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.
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

/// <summary>GRANULARPERF_01 — all file/device operations belong to one bounded background worker.</summary>
public sealed class VoiceOverPreviewPlayer : IDisposable
{
    private readonly List<VoiceOverPreviewTake> _takes = new();
    private readonly System.Threading.Channels.Channel<PlaybackRequest> _pending =
        System.Threading.Channels.Channel.CreateBounded<PlaybackRequest>(
            new System.Threading.Channels.BoundedChannelOptions(1)
            { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest, SingleReader = true });
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
        // Only copy immutable take descriptions here. File.Exists and device creation run below.
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
                        NAudio.Wave.AudioFileReader? reader = null;
                        NAudio.Wave.WaveOutEvent? player = null;
                        try
                        {
                            reader = new(take.Path);
                            player = new();
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
                // Loading takes can be slow. Let the latest queued playback position win first.
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
                        bool playing = take.Player.PlaybackState == NAudio.Wave.PlaybackState.Playing;
                        if (play && !playing)
                        {
                            take.Reader.CurrentTime = TimeSpan.FromSeconds(voiceTime);
                            take.Player.Play();
                        }
                        else if (!play && playing) take.Player.Pause();
                        else if (play && Math.Abs(take.Reader.CurrentTime.TotalSeconds - voiceTime) > 0.5)
                            take.Reader.CurrentTime = TimeSpan.FromSeconds(voiceTime);
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
