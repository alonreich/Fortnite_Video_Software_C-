using System;
using FreeVideoStudio.Core.Media;
using NAudio.Wave;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// MUSICSYNC_02 — ONE SLAVE-TO-THE-VIDEO RULE FOR EVERY PREVIEW AUDIO PLAYER.
///
/// The video (mpv time-pos, mapped to OUTPUT seconds through OutputTimeline) is the master clock.
/// Every other preview sound (voice-over takes on NAudio, the music bed on its own audio-only mpv)
/// follows it. Before this they each ran open-loop:
///   • voice-over takes on a default WaveOutEvent: 300 ms of buffering, compared against the
///     READER position (which runs ahead of what is audible by that buffering), and a 0.5 s
///     tolerance. A take could sit up to half a second off the picture before anything corrected it;
///   • the music bed never corrected at all during continuous playback.
///
/// The rule:
///   • Correct by SEEKING only. Never change a follower's playback RATE. Music and voice always
///     play at 1.0x. Only the video changes speed.
///   • Tolerance <see cref="DriftToleranceSec"/> (≈ 3 frames at 24 fps). A correction needs
///     <see cref="DriftStrikes"/> consecutive readings over tolerance, so tick jitter never
///     causes a seek.
///   • NAudio's reader runs <see cref="VoiceReaderLeadSec"/> ahead of the speaker. That lead is
///     subtracted before comparing, and added back when repositioning.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
internal static class PreviewAudioSync
{
    public const double DriftToleranceSec = 0.12;
    public const int DriftStrikes = 2;

    /// <summary>Total WaveOutEvent buffering. Low enough to track the picture, high enough not to underrun.</summary>
    public const int VoiceLatencyMs = 120;
    public const int VoiceBuffers = 3;
    public const double VoiceReaderLeadSec = VoiceLatencyMs / 1000.0;

    /// <summary>A WaveOutEvent configured for picture-locked preview.</summary>
    public static WaveOutEvent CreateVoicePlayer()
        => new() { DesiredLatency = VoiceLatencyMs, NumberOfBuffers = VoiceBuffers };

    /// <summary>
    /// Drives one voice-over take toward <paramref name="voiceTimeSec"/> (seconds into the take that
    /// should be AUDIBLE now). <paramref name="strikes"/> is per-take state owned by the caller.
    /// Call from the one thread that owns the player (the UI tick, or VoiceOverPreviewPlayer's worker).
    /// </summary>
    public static void SyncVoiceTake(WavAudioReader reader, WaveOutEvent player, bool shouldPlay, double voiceTimeSec, ref int strikes)
    {
        bool playing = player.PlaybackState == PlaybackState.Playing;
        double total = reader.TotalTime.TotalSeconds;

        if (shouldPlay && !playing)
        {
            if (voiceTimeSec >= 0 && voiceTimeSec < total)
                reader.CurrentTime = TimeSpan.FromSeconds(voiceTimeSec);
            player.Play();
            strikes = 0;
            return;
        }

        if (!shouldPlay)
        {
            if (playing) player.Pause();
            strikes = 0;
            return;
        }

        double audible = reader.CurrentTime.TotalSeconds - VoiceReaderLeadSec;
        if (Math.Abs(audible - voiceTimeSec) <= DriftToleranceSec)
        {
            strikes = 0;
            return;
        }

        if (++strikes < DriftStrikes) return;
        strikes = 0;
        double target = Math.Clamp(voiceTimeSec + VoiceReaderLeadSec, 0, Math.Max(0, total));
        reader.CurrentTime = TimeSpan.FromSeconds(target);
    }
}
