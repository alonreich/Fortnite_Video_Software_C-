// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// PREVIEWMIX_01 — THE PREVIEW PLAYS THE EXPORT'S OWN AUDIO.
///
/// The live preview is three independent players (gameplay, music bed, voice-over takes), so it
/// could never reproduce what only exists in the export's filter graph: the multiband ducking and
/// speech carving (the GAME is the music's sidechain), the voice-over protection, the peak tamer,
/// the safety limiter and the meme levels. Whenever the edit has any of those, the export's audio is
/// rendered in the background — ProcessWorker builds the export graph itself and AudioGraphPruner
/// runs only its audio half (measured: the rendered mix and the exported file's audio differ by
/// -60 dB RMS, i.e. AAC noise) — and played here in place of the live players, following the video
/// in output time exactly like the music bed does (seek-only correction, MUSICSYNC_02).
///
/// After every edit the old mix is dropped at once (the live players take over, so nothing stale is
/// ever heard) and a new one is rendered ~0.8 s after the edits stop.
///
/// VOPREVIEW_01 — until a mix is ready (or when the edit has nothing the export would change), the
/// live players do what can be done live and exactly: the voice-over protection's 85% dip is a pure
/// time pulse (VOPRIO_01: voice first, then gameplay, then music), and the peak tamer is a static
/// filter on the gameplay player.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class MainWindow
{
    private const int MixDebounceMs = 800;
    private const int MixSyncSettleMs = 350;

    private DispatcherTimer? _mixTimer;
    private FreeVideoStudio.Core.Media.MpvIpcClient? _mixClient;
    private string? _mixWavPath;              // the rendered mix currently valid, or null
    private AudioPreviewMap? _mixMap;
    private string? _mixRenderedSig;
    private string? _mixLastSig;
    private long _mixSigChangedAt;
    private string? _mixInFlightSig;
    private CancellationTokenSource? _mixCts;
    private bool _mixPlaying;
    private string? _mixLoadedPath;
    private int _mixDriftStrikes;
    private long _mixHoldUntilTicks;
    private int _mixSlot;
    private double _previewVoicePulse;
    private string? _liveGameFilter;

    /// <summary>True while the rendered mix matches the current edit and replaces the live players.</summary>
    private bool PreviewMixActive => _mixWavPath != null && _mixMap != null && _mixRenderedSig != null && _mixRenderedSig == _mixLastSig;

    // ── VOPREVIEW_01 — live gains ──────────────────────────────────────────────────────────────

    private double PreviewGameDuckGain()
    {
        if (PreviewMixActive) return 0.0;
        return _voiceOverResult?.DuckAudio == true ? 1.0 - AudioFilterChain.VoiceDuckDepth * _previewVoicePulse : 1.0;
    }

    private double PreviewMusicDuckGain()
    {
        if (PreviewMixActive) return 0.0;
        return _voiceOverResult?.ProtectFromMusic == true ? 1.0 - AudioFilterChain.VoiceDuckDepth * _previewVoicePulse : 1.0;
    }

    /// <summary>The voice-over pulse at output time <paramref name="outputSec"/>: the export's 0.3 s ramps, exactly.</summary>
    private void UpdatePreviewVoicePulse(double outputSec)
    {
        double pulse = 0;
        foreach (var take in _voiceOverPreviewTakes)
        {
            double s = take.StartProjectSec;
            double e = s + take.Reader.TotalTime.TotalSeconds;
            double up = Math.Clamp((outputSec - (s - 0.3)) / 0.3, 0, 1);
            double down = Math.Clamp(((e + 0.3) - outputSec) / 0.3, 0, 1);
            pulse = Math.Max(pulse, up * down);
        }
        if (Math.Abs(pulse - _previewVoicePulse) < 0.02 && !(pulse == 0 && _previewVoicePulse != 0)) return;
        _previewVoicePulse = pulse;
        if (_voiceOverResult?.DuckAudio == true || _voiceOverResult?.ProtectFromMusic == true) ApplyPreviewPlayersVolume();
    }

    /// <summary>PEAKSAFE_01 in the live preview: the gameplay player gets the same tamer as the export.</summary>
    private void ApplyLivePreviewFilters()
    {
        var settings = Infrastructure.SettingsManager.Instance;
        bool wanted = settings.Defaults.AutoSpikeFlattening
                      && (_applyPeakFlattening ?? settings.PeakFlatteningPrompt != Infrastructure.AudioFixPrompt.NeverApply);
        string filter = wanted && PeakSafety.TamerFilter(_gameplayLoudnessLufs) is string t ? $"lavfi=[{t}]" : "";
        if (filter == _liveGameFilter) return;
        _liveGameFilter = filter;
        _ = ActiveVideoHost?.IpcClient?.SetPropertyAsync("af", filter);
    }

    // ── PREVIEWMIX_01 — rendered mix ──────────────────────────────────────────────────────────

    private void EnsurePreviewMixScheduler()
    {
        if (_mixTimer != null) return;
        _mixTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _mixTimer.Tick += (_, _) => PreviewMixSchedulerTick();
        _mixTimer.Start();
    }

    private void PreviewMixSchedulerTick()
    {
        ApplyLivePreviewFilters();

        string? sig = PreviewMixSignature();
        if (sig != _mixLastSig)
        {
            bool wasActive = PreviewMixActive;
            _mixLastSig = sig;
            _mixSigChangedAt = Environment.TickCount64;
            if (wasActive != PreviewMixActive) OnPreviewMixActiveChanged();
        }

        if (sig == null || sig == _mixRenderedSig || sig == _mixInFlightSig) return;
        if (Environment.TickCount64 - _mixSigChangedAt < MixDebounceMs) return;
        _ = RenderPreviewMixAsync(sig);
    }

    private async Task RenderPreviewMixAsync(string sig)
    {
        try { _mixCts?.Cancel(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        var cts = new CancellationTokenSource();
        _mixCts = cts;
        _mixInFlightSig = sig;
        try
        {
            string dir = _paths.TempDirectory;
            Directory.CreateDirectory(dir);
            _mixSlot ^= 1;
            string wav = Path.Combine(dir, $"fvs_preview_mix_{Environment.ProcessId}_{_mixSlot}.wav");
            if (string.Equals(wav, _mixLoadedPath, StringComparison.OrdinalIgnoreCase))
            {
                await StopPreviewMixPlaybackAsync();
            }

            var payload = await ComposeExportPayloadAsync(dir, SelectedLegacyMemeFile(), 20, null);
            var map = await Task.Run(() => Services.MainMediaController.RenderAudioPreviewAsync(payload, wav, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || map == null) return;
            if (sig != _mixLastSig) return;   // edited again while rendering; the next tick renders anew

            _mixWavPath = wav;
            _mixMap = map;
            _mixRenderedSig = sig;
            RuntimeLog.Info("PreviewMix", "Rendered mix is live in the preview (the export's own audio graph).");
            OnPreviewMixActiveChanged();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { RuntimeLog.WarnThrottled("PreviewMix", $"Rendered preview mix unavailable: {ex.Message}"); }
        finally
        {
            if (_mixInFlightSig == sig) _mixInFlightSig = null;
        }
    }

    private void OnPreviewMixActiveChanged()
    {
        if (PreviewMixActive)
        {
            StopMusicPreview();
        }
        else
        {
            _ = StopPreviewMixPlaybackAsync();
        }
        ApplyPreviewPlayersVolume();
    }

    /// <summary>Called on every playback tick, after the live music/voice followers.</summary>
    private void UpdatePreviewMix(double sourceTimeSec, bool videoEnded)
    {
        EnsurePreviewMixScheduler();
        if (!PreviewMixActive)
        {
            if (_mixPlaying) _ = StopPreviewMixPlaybackAsync();
            return;
        }

        var ipc = ActiveVideoHost?.IpcClient;
        bool isPaused = ipc?.IsPaused ?? true;
        if (_isCurrentlyFrozen) isPaused = false;
        if (isPaused || videoEnded)
        {
            if (_mixPlaying)
            {
                _mixPlaying = false;
                _ = _mixClient?.SetPropertyAsync("pause", "yes");
            }
            return;
        }

        double want = _mixMap!.MixSecFor(PreviewOutputSeconds(sourceTimeSec));
        if (!_mixPlaying || !string.Equals(_mixLoadedPath, _mixWavPath, StringComparison.OrdinalIgnoreCase))
        {
            _ = StartPreviewMixPlaybackAsync(_mixWavPath!, want);
            return;
        }

        if (Environment.TickCount64 < _mixHoldUntilTicks || _mixClient == null) return;
        if (Math.Abs(_mixClient.CurrentTime - want) <= Infrastructure.PreviewAudioSync.DriftToleranceSec)
        {
            _mixDriftStrikes = 0;
            return;
        }
        if (++_mixDriftStrikes < Infrastructure.PreviewAudioSync.DriftStrikes) return;
        _mixDriftStrikes = 0;
        _ = _mixClient.SendCommandAsync("seek", want, "absolute");   // seek, never re-rate
        _mixHoldUntilTicks = Environment.TickCount64 + MixSyncSettleMs;
    }

    private async Task StartPreviewMixPlaybackAsync(string path, double positionSec)
    {
        _mixPlaying = true;
        try
        {
            if (_mixClient == null)
            {
                _mixClient = new FreeVideoStudio.Core.Media.MpvIpcClient();
                await _mixClient.StartAudioOnlyAsync("");
            }
            await _mixClient.ApplyPreviewGainAsync();   // the master only: the mix already carries every fader
            await _mixClient.SetPropertyDoubleAsync("speed", 1.0);
            await _mixClient.LoadFileAsync(path, Math.Max(0, positionSec));
            _mixLoadedPath = path;
            _mixDriftStrikes = 0;
            _mixHoldUntilTicks = Environment.TickCount64 + MixSyncSettleMs;
        }
        catch (Exception ex)
        {
            _mixPlaying = false;
            RuntimeLog.WarnThrottled("PreviewMix", $"Rendered mix could not play: {ex.Message}");
        }
    }

    private async Task StopPreviewMixPlaybackAsync()
    {
        _mixPlaying = false;
        if (_mixClient == null) return;
        try
        {
            await _mixClient.SetPropertyAsync("pause", "yes");
            await _mixClient.SendCommandAsync("stop");
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        _mixLoadedPath = null;
    }

    /// <summary>Re-applies the master to the mix player (called with the other players).</summary>
    private void ApplyPreviewMixVolume() => _ = _mixClient?.ApplyPreviewGainAsync();

    private void DisposePreviewMix()
    {
        try { _mixCts?.Cancel(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        try { _mixClient?.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        _mixClient = null;
        _mixPlaying = false;
        _mixLoadedPath = null;
    }

    /// <summary>
    /// Everything the export's AUDIO depends on. Null when the edit has nothing the live players
    /// cannot already reproduce (no music, no voice-over, no meme, no peak tamer) — then no mix is
    /// rendered at all.
    /// </summary>
    private string? PreviewMixSignature()
    {
        if (string.IsNullOrEmpty(_loadedVideoPath) || _loadedVideoDurationMs <= 0) return null;

        var settings = Infrastructure.SettingsManager.Instance;
        bool tamer = settings.Defaults.AutoSpikeFlattening
                     && (_applyPeakFlattening ?? settings.PeakFlatteningPrompt != Infrastructure.AudioFixPrompt.NeverApply)
                     && _gameplayLoudnessLufs.HasValue;
        var music = _musicWizardResult;   // the export uses the result whenever it exists
        var vo = _voiceOverResult;
        string? legacyMeme = SelectedLegacyMemeFile();
        bool hasVo = vo != null && (vo.VoiceOverTakes.Count > 0 || !string.IsNullOrEmpty(vo.VoiceOverWavPath));
        if (music == null && !hasVo && _memePlacements.Count == 0 && legacyMeme == null && !tamer) return null;

        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(_loadedVideoPath).Append('|').Append(_trimStartMs.ToString("F1", ci)).Append('|').Append(_trimEndMs.ToString("F1", ci))
          .Append('|').Append(_baseSpeed.ToString("F4", ci))
          .Append('|').Append(string.Join(",", BuildExportSpeedSegments()))
          .Append('|').Append(string.Join(",", _cuts))
          .Append('|').Append(string.Join(",", _memePlacements))
          .Append('|').Append(legacyMeme).Append(legacyMeme != null ? Infrastructure.MemePlacementStore.Get(legacyMeme).ToString() : "")
          .Append('|').Append(_thumbnailSet).Append(_thumbnailPosMs.ToString("F1", ci))
          .Append('|').Append(this.FindControl<Avalonia.Controls.ToggleSwitch>("EnableFadeCheckbox")?.IsChecked)
          .Append('|').Append(_keepMusicDuringMeme)
          .Append('|').Append(tamer).Append(_gameplayLoudnessLufs?.ToString("F2", ci))
          .Append('|').Append(settings.Defaults.DuckingEnabled).Append(settings.Defaults.DuckingStrength)
          .Append(settings.Defaults.CarvingEnabled).Append(settings.Defaults.CarvingStrength);   // DUCKSTRENGTH_01
        if (music != null)
        {
            sb.Append("|M:").Append(music.MusicFilePath).Append(string.Join(",", music.MusicFilePaths))
              .Append(music.OffsetSeconds.ToString("F3", ci)).Append(',').Append(music.TimelineStartSeconds.ToString("F3", ci))
              .Append(',').Append(music.TimelineEndSeconds.ToString("F3", ci)).Append(',').Append(music.VideoVolume.ToString("F3", ci))
              .Append(',').Append(music.MusicVolume.ToString("F3", ci)).Append(music.EnableDucking).Append(music.EnableCarving)
              .Append(music.LoopMusic);
        }
        if (hasVo)
        {
            sb.Append("|V:").Append(vo!.DuckAudio).Append(vo.ProtectFromMusic).Append(vo.VoiceOverWavPath)
              .Append(vo.VoiceOverStartTimestampSec.ToString("F3", ci));
            foreach (var t in vo.VoiceOverTakes)
            {
                long len = 0;
                try { len = new FileInfo(t.Path).Length; } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                sb.Append(';').Append(t.Path).Append('@').Append(t.StartSec.ToString("F3", ci)).Append('#').Append(len);
            }
        }
        return sb.ToString();
    }
}
