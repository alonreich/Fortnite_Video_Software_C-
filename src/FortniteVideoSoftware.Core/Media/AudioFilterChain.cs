// ==============================================================================
// AudioFilterChain.cs — Exact port of Python filter_builder.py AudioFilterMixin
// Audio ducking (150Hz split), music track normalization, fades, sidechain.
// ==============================================================================

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// Builds the FFmpeg audio filter chain for the rendering pipeline.
/// 
/// Pipeline:
/// 1. Game audio: volume normalize, optional fade-in
/// 2. For each music track: atrim, fade in/out, volume, delay to align
/// 3. Mix multiple music tracks if present
/// 4. Sidechain ducking: split music at 150Hz, duck high freq against game audio
/// 5. Reconstruct music, mix with game audio, dynaudnorm + alimiter
/// </summary>
public class AudioFilterChain
{
    /// <summary>
    /// Builds the complete audio filter chain.
    /// Returns (filterChains, finalLabel).
    /// Exact port of build_audio_chain().
    /// </summary>
    public static (List<string> chains, string finalLabel) Build(
        JsonObject? musicConfig,
        double videoStartTime,
        double videoEndTime,
        double speedFactor,
        bool disableFades,
        double vfadeInD,
        List<string>? audioFilterCmd,
        int sampleRate = 48000,
        List<MusicTrack>? musicTracks = null,
        int musicStartIndex = 1,
        double? totalProjectDuration = null,
        string mainAudioLabel = "[0:a]",
        double volumeNormalizeDb = 0.0)
    {
        var chain = new List<string>();
        musicConfig ??= new JsonObject();
        int targetSampleRate = sampleRate > 0 ? sampleRate : 48000;

        // Build main audio filter from raw parts
        var rawParts = new List<string>();
        if (audioFilterCmd != null) rawParts.AddRange(audioFilterCmd);
        if (vfadeInD > 0) rawParts.Add($"afade=t=in:st=0:d={vfadeInD:F3}");

        var cleanedParts = new List<string>();
        foreach (var part in rawParts)
        {
            string s = part.Trim().Trim(',');
            if (!string.IsNullOrEmpty(s)) cleanedParts.Add(s);
        }
        if (cleanedParts.Count == 0) cleanedParts.Add("anull");
        string mainAudioFilter = string.Join(",", cleanedParts);

        double mainDuration = totalProjectDuration ?? 
            (speedFactor > 0 ? (videoEndTime - videoStartTime) / speedFactor : videoEndTime - videoStartTime);

        // Process main audio
        if (!string.IsNullOrEmpty(mainAudioLabel))
        {
            chain.Add($"{mainAudioLabel}{mainAudioFilter}[a_main_raw]");
        }
        else
        {
            chain.Add($"anullsrc=r={targetSampleRate}:cl=stereo," +
                      $"atrim=duration={Math.Max(0.01, mainDuration):F4}," +
                      $"asetpts=PTS-STARTPTS[a_main_raw]");
        }

        // Normalize music tracks
        var tracks = new List<MusicTrack>();
        double fallbackMusicDuration = totalProjectDuration ?? mainDuration;

        if (musicTracks != null && musicTracks.Count > 0)
        {
            tracks.AddRange(musicTracks);
        }
        else if (musicConfig != null && musicConfig["path"]?.ToString() is string path && !string.IsNullOrEmpty(path))
        {
            double offset = (double)(musicConfig["file_offset_sec"]?.GetValue<double>() ?? 0);
            double dur = totalProjectDuration ?? (videoEndTime - videoStartTime) / Math.Max(0.001, speedFactor);
            tracks.Add(new MusicTrack(path, offset, dur));
        }

        // Clip tracks to music window
        double? musicWindowSec = null;
        if (musicConfig != null)
        {
            try
            {
                double mStart = (double)(musicConfig["timeline_start_sec"]?.GetValue<double>() ?? 0);
                double mEnd = (double)(musicConfig["timeline_end_sec"]?.GetValue<double>() ?? 0);
                if (mEnd > mStart) musicWindowSec = mEnd - mStart;
            }
            catch { }
        }

        if (tracks.Count > 0 && musicWindowSec.HasValue)
        {
            double remaining = musicWindowSec.Value;
            var clippedTracks = new List<MusicTrack>();
            foreach (var track in tracks)
            {
                double take = Math.Min(track.Duration, remaining);
                if (take > 0.001)
                {
                    clippedTracks.Add(new MusicTrack(track.Path, track.Offset, take));
                    remaining -= take;
                }
                if (remaining <= 0.001) break;
            }
            tracks = clippedTracks;
        }

        // No music tracks: simple output
        if (tracks.Count == 0)
        {
            double vVol = GetDouble(musicConfig, "main_vol", GetDouble(musicConfig, "video_volume", 0.8));
            if (volumeNormalizeDb != 0)
                vVol *= Math.Pow(10, volumeNormalizeDb / 20.0);
            chain.Add($"[a_main_raw]volume={vVol:F4}," +
                      $"aresample={targetSampleRate}:async=1[a_main_prepared]");
            return (chain, "[a_main_prepared]");
        }

        // Calculate initial delay
        double initialDelaySec = 0;
        if (musicConfig != null)
        {
            try { initialDelaySec = Math.Max(0, (double)(musicConfig["timeline_start_sec"]?.GetValue<double>() ?? 0)); }
            catch { }
        }

        // Process each music track
        var preparedMusicLabels = new List<string>();
        double accumProjectSec = initialDelaySec;

        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            string inputLabel = $"[{musicStartIndex + i}:a]";
            string outLabel = $"[a_mus_{i}]";
            string preLabel = $"[a_mus_{i}_pre]";

            double fileStart = Math.Max(0, track.Offset);
            double extraDelay = track.Offset < 0 ? -track.Offset : 0;

            var musicFilters = new List<string>
            {
                $"atrim=start={fileStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:duration={track.Duration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}",
                "asetpts=PTS-STARTPTS"
            };

            // Fades
            if (!disableFades && track.Duration > 0.5)
            {
                double fadeDur = Math.Min(1.5, track.Duration / 2.0);
                musicFilters.Add($"afade=t=in:st=0:d={fadeDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
                if (track.ApplyFadeOut)
                {
                    musicFilters.Add($"afade=t=out:st={Math.Max(0, track.Duration - fadeDur).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:d={fadeDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
                }
            }

            double mVol = GetDouble(musicConfig, "music_vol", GetDouble(musicConfig, "volume", 0.8));
            musicFilters.Add($"volume={mVol.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}");

            chain.Add($"{inputLabel}{string.Join(",", musicFilters)}{preLabel}");

            int delayMs = (int)((accumProjectSec + extraDelay + track.TimelineStartDelay) * 1000);
            if (delayMs > 0)
                chain.Add($"{preLabel}adelay={delayMs}|{delayMs}{outLabel}");
            else
                chain.Add($"{preLabel}anull{outLabel}");

            preparedMusicLabels.Add(outLabel);
            accumProjectSec += track.Duration;
        }

        // Mix multiple music tracks if needed
        string bgMusicLabel;
        if (preparedMusicLabels.Count > 1)
        {
            string mixInputs = string.Join("", preparedMusicLabels);
            string weights = string.Join(" ", Enumerable.Repeat("1", preparedMusicLabels.Count));
            chain.Add($"{mixInputs}amix=inputs={preparedMusicLabels.Count}:" +
                      $"duration=longest:dropout_transition=0:weights='{weights}'[a_bg_music_raw]");
            bgMusicLabel = "[a_bg_music_raw]";
        }
        else
        {
            bgMusicLabel = preparedMusicLabels[0];
        }

        // Carving (EQ Dip)
        bool carvingEnabled = musicConfig != null && (musicConfig["carving_enabled"]?.GetValue<bool>() ?? true);
        if (carvingEnabled)
        {
            chain.Add($"{bgMusicLabel}equalizer=f=1500:width_type=h:width=500:g=-5," +
                      $"equalizer=f=3000:width_type=h:width=500:g=-3[a_bg_music]");
            bgMusicLabel = "[a_bg_music]";
        }

        // Ducking pipeline
        double vVolGame = GetDouble(musicConfig, "main_vol", GetDouble(musicConfig, "video_volume", 0.8));
        if (volumeNormalizeDb != 0)
            vVolGame *= Math.Pow(10, volumeNormalizeDb / 20.0);

        chain.Add($"[a_main_raw]volume={vVolGame.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}[game_scaled]");
        chain.Add("[game_scaled]asplit=2[game_out_pre][game_trig]");
        // Trigger cleaning: highpass→lowpass→agate→equalizer for detection
        chain.Add("[game_trig]highpass=f=200,lowpass=f=3500," +
                  "agate=threshold=0.05:attack=5:release=100[trig_cleaned]");
        chain.Add("[trig_cleaned]equalizer=f=1000:t=q:w=2:g=10[trig_final]");

        // Music split at 150Hz
        chain.Add($"{bgMusicLabel}asplit=2[mus_base][mus_to_filter]");
        chain.Add("[mus_base]lowpass=f=150[mus_low]");
        chain.Add("[mus_to_filter]highpass=f=150[mus_high]");

        // Sidechain compression
        double dThresh = GetDouble(musicConfig, "ducking_threshold", 0.15);
        double dRatio = GetDouble(musicConfig, "ducking_ratio", 2.5);
        string duckParams = $"threshold={dThresh.ToString(System.Globalization.CultureInfo.InvariantCulture)}:ratio={dRatio.ToString(System.Globalization.CultureInfo.InvariantCulture)}:attack=1:release=400:detection=rms";
        chain.Add($"[mus_high][trig_final]sidechaincompress={duckParams}[mus_high_ducked]");

        // Reconstruct music
        chain.Add("[mus_low][mus_high_ducked]amix=inputs=2:weights='1 1':normalize=0[a_music_reconstructed]");

        // Final mix: game + reconstructed music
        chain.Add($"[game_out_pre][a_music_reconstructed]amix=inputs=2:" +
                  $"duration=first:dropout_transition=3:weights='1 1':normalize=0," +
                  $"alimiter=limit=0.95:attack=5:release=50," +
                  $"aresample={targetSampleRate}:async=1[a_music_prepared]");

        return (chain, "[a_music_prepared]");
    }

    private static double GetDouble(JsonObject? obj, string key, double defaultValue)
    {
        if (obj == null) return defaultValue;
        try { return obj[key]?.GetValue<double>() ?? defaultValue; }
        catch { return defaultValue; }
    }
}

/// <summary>
/// Represents a single music track for the audio chain.
/// Path, offset (in seconds from file start), duration, timeline delay (in seconds from video start), and whether to fade out.
/// </summary>
public record MusicTrack(string Path, double Offset, double Duration, double TimelineStartDelay = 0.0, bool ApplyFadeOut = true);
