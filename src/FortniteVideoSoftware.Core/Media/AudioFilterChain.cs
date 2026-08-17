
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

    /// <summary>Song-to-song: the OUTGOING song starts fading out this long before its end.</summary>
    private const double CrossfadeOutSec = 7.0;

    /// <summary>
    /// Song-to-song: the INCOMING song starts this long before the outgoing song ends, and
    /// fades in across exactly that overlap — so the two are audible together for 3 seconds
    /// with the old one already 4 seconds into its 7-second decay.
    /// </summary>
    private const double CrossfadeInSec = 3.0;

    /// <summary>Fade applied at the very start and the very end of the whole music bed.</summary>
    private const double EdgeFadeSec = 1.5;

    /// <summary>
    /// Builds the complete audio filter chain.
    /// Returns (filterChains, finalLabel).
    /// Exact port of build_audio_chain().
    /// </summary>
    /// <param name="musicLeadFadeIn">
    /// ISSUE_04 — false when the user dragged the music-start note marker to the RIGHT of
    /// MARK START. The music then begins partway into the video, which is a deliberate entrance,
    /// so it hits at full level instead of fading up. True (music starts with the video) gives
    /// the normal <see cref="EdgeFadeSec"/> lead-in.
    /// </param>
    /// <param name="musicTailFadeOut">
    /// ISSUE_04 — false when the user dragged the music-end note marker to the RIGHT of
    /// MARK END. The music is asking to outlast the video, so there is nothing left to fade
    /// over: it is cut dead at MARK END. True gives the normal <see cref="EdgeFadeSec"/> tail.
    /// </param>
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
        double volumeNormalizeDb = 0.0,
        string? gameLoudnormFilter = null,
        double musicFollowGainDb = 0.0,
        bool musicLeadFadeIn = true,
        bool musicTailFadeOut = true,
        string? voiceOverLabel = null,
        IReadOnlyDictionary<string, double>? musicBedGainDb = null)
    {
        var chain = new List<string>();

        bool useLoudnorm = !string.IsNullOrWhiteSpace(gameLoudnormFilter);
        double appliedNormalizeDb = useLoudnorm ? 0.0 : volumeNormalizeDb;

        musicConfig ??= new JsonObject();
        int targetSampleRate = sampleRate > 0 ? sampleRate : 48000;

        string gameNormalizePrefix = useLoudnorm
            ? gameLoudnormFilter! + $",aresample={targetSampleRate},"
            : string.Empty;

        var rawParts = new List<string>();
        if (audioFilterCmd != null) rawParts.AddRange(audioFilterCmd);
        if (vfadeInD > 0) rawParts.Add($"afade=t=in:st=0:d={vfadeInD.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");

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

        if (!string.IsNullOrEmpty(mainAudioLabel))
        {
            chain.Add($"{mainAudioLabel}{mainAudioFilter}[a_main_raw]");
        }
        else
        {
            chain.Add($"anullsrc=r={targetSampleRate}:cl=stereo," +
                      $"atrim=duration={Math.Max(0.01, mainDuration).ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}," +
                      $"asetpts=PTS-STARTPTS[a_main_raw]");
        }

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

        double? musicWindowSec = null;
        if (musicConfig != null)
        {
            try
            {
                double mStart = (double)(musicConfig["timeline_start_sec"]?.GetValue<double>() ?? 0);
                double mEnd = (double)(musicConfig["timeline_end_sec"]?.GetValue<double>() ?? 0);
                if (mEnd > mStart) musicWindowSec = mEnd - mStart;
            }
            catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
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

        if (tracks.Count == 0)
        {
            double vVol = GetDouble(musicConfig, "main_vol", GetDouble(musicConfig, "video_volume", 0.8));
            if (appliedNormalizeDb != 0)
                vVol *= Math.Pow(10, appliedNormalizeDb / 20.0);
            chain.Add($"[a_main_raw]{gameNormalizePrefix}volume={vVol.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}," +
                      $"aresample={targetSampleRate}:async=1[a_main_prepared]");
            return (chain, "[a_main_prepared]");
        }

        double initialDelaySec = 0;
        if (musicConfig != null)
        {
            try { initialDelaySec = Math.Max(0, (double)(musicConfig["timeline_start_sec"]?.GetValue<double>() ?? 0)); }
            catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
        }

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

            bool isFirst = i == 0;
            bool isLast = i == tracks.Count - 1;

            double overlap = isFirst
                ? 0.0
                : Math.Max(0.0, Math.Min(CrossfadeInSec, Math.Min(track.Duration, tracks[i - 1].Duration) / 2.0));

            double playDur = Math.Max(0.01, track.Duration + overlap);
            double half = playDur / 2.0;

            var musicFilters = new List<string>
            {
                $"atrim=start={fileStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:duration={playDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}",
                "asetpts=PTS-STARTPTS"
            };

            if (!disableFades && playDur > 0.1)
            {
                double fadeInDur = isFirst
                    ? (musicLeadFadeIn ? Math.Min(EdgeFadeSec, half) : 0.0)
                    : Math.Min(overlap, half);

                if (fadeInDur > 0.001)
                {
                    musicFilters.Add(
                        $"afade=t=in:st=0:d={fadeInDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
                }

                double fadeOutDur = isLast
                    ? ((musicTailFadeOut && track.ApplyFadeOut) ? Math.Min(EdgeFadeSec, half) : 0.0)
                    : Math.Min(CrossfadeOutSec, half);

                if (fadeOutDur > 0.001)
                {
                    double fadeOutStart = Math.Max(0, playDur - fadeOutDur);
                    musicFilters.Add(
                        $"afade=t=out:st={fadeOutStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:d={fadeOutDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
                }
            }

            // ─────────────────────────────────────────────────────────────────────────────────
            // AUDIO_03 — PUT THE MUSIC AT A BED LEVEL BEFORE THE USER'S SLIDER TOUCHES IT.
            //
            // The gameplay is loudness-measured and normalised; the music never was. A commercial
            // master lands around -8 to -10 LUFS, the game bus at -14, so "slider 1.0 vs slider
            // 1.0" actually meant the music started roughly 5 dB AHEAD. The ducking and carving
            // downstream were then permanently fighting a deficit rather than doing their real job.
            //
            // musicBedGainDb carries a per-file correction measured by AudioLoudnessProbe, so a
            // loud pop master gets pulled down and a quiet acoustic track gets lifted — both land
            // at the same bed level. The user's slider is applied ON TOP, so 1.0 finally means
            // "normal background level" instead of "however loud this particular record is".
            //
            // A missing entry means the measurement failed; the correction is simply 0 dB and the
            // behaviour falls back to exactly what it was before. Never block an export over this.
            // ─────────────────────────────────────────────────────────────────────────────────
            double mVol = GetDouble(musicConfig, "music_vol", GetDouble(musicConfig, "volume", 0.8));
            if (musicBedGainDb != null && musicBedGainDb.TryGetValue(track.Path, out double bedDb) && Math.Abs(bedDb) > 0.01)
            {
                mVol *= Math.Pow(10, bedDb / 20.0);
            }

            if (Math.Abs(musicFollowGainDb) > 0.01)
            {
                mVol *= Math.Pow(10, musicFollowGainDb / 20.0);
            }

            musicFilters.Add($"volume={mVol.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}");

            chain.Add($"{inputLabel}{string.Join(",", musicFilters)}{preLabel}");

            double startSec = Math.Max(0.0,
                accumProjectSec - overlap + extraDelay + track.TimelineStartDelay);
            int delayMs = (int)(startSec * 1000);
            if (delayMs > 0)
                chain.Add($"{preLabel}adelay={delayMs}|{delayMs}{outLabel}");
            else
                chain.Add($"{preLabel}anull{outLabel}");

            preparedMusicLabels.Add(outLabel);
            accumProjectSec += track.Duration;
        }

        string bgMusicLabel;
        if (preparedMusicLabels.Count > 1)
        {
            string mixInputs = string.Join("", preparedMusicLabels);
            string weights = string.Join(" ", Enumerable.Repeat("1", preparedMusicLabels.Count));
            chain.Add($"{mixInputs}amix=inputs={preparedMusicLabels.Count}:" +
                      $"duration=longest:dropout_transition=0:weights='{weights}':normalize=0[a_bg_music_raw]");
            bgMusicLabel = "[a_bg_music_raw]";
        }
        else
        {
            bgMusicLabel = preparedMusicLabels[0];
        }

        bool carvingEnabled = musicConfig != null && (musicConfig["carving_enabled"]?.GetValue<bool>() ?? true);
        if (carvingEnabled)
        {
            // ─────────────────────────────────────────────────────────────────────────────────
            // AUDIO_05 (#6) — ONE HONEST CARVE INSTEAD OF TWO COSMETIC NOTCHES.
            //
            // This was `equalizer=f=1500:width=500:g=-5` plus `equalizer=f=3000:width=500:g=-3`.
            // With width_type=h the width is in HERTZ, so those were Q≈3 and Q≈6 — surgical
            // notches, far too narrow to create real space, yet applied PERMANENTLY, including
            // through silence where there is nothing to make room for. The music was left slightly
            // hollow all the time in exchange for very little.
            //
            // A single wider, gentler scoop centred where game detail actually lives does more
            // with less: -4 dB at 2 kHz across ~1800 Hz covers the 1.1-2.9 kHz band in one move.
            //
            // NEXT STEP IF THIS IS EVER REVISITED: make it DYNAMIC — drive the same cut from the
            // sidechain trigger so the hole only opens while the game needs it, and the music is
            // untouched the rest of the time. That is the proper fix; this is the good one.
            // ─────────────────────────────────────────────────────────────────────────────────
            chain.Add($"{bgMusicLabel}equalizer=f=2000:width_type=h:width=1800:g=-4[a_bg_music]");
            bgMusicLabel = "[a_bg_music]";
        }

        double vVolGame = GetDouble(musicConfig, "main_vol", GetDouble(musicConfig, "video_volume", 0.8));
        if (appliedNormalizeDb != 0)
            vVolGame *= Math.Pow(10, appliedNormalizeDb / 20.0);

        chain.Add($"[a_main_raw]volume={vVolGame.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)},aresample={targetSampleRate}:async=1[game_leveled_base]");
        
        if (!string.IsNullOrEmpty(voiceOverLabel))
        {
            chain.Add($"[game_leveled_base]{voiceOverLabel}amix=inputs=2:duration=first:dropout_transition=2:normalize=0[game_leveled]");
        }
        else
        {
            chain.Add("[game_leveled_base]anull[game_leveled]");
        }

        chain.Add("[game_leveled]asplit=2[game_out_pre_raw][game_trig]");
        if (useLoudnorm)
        {
            chain.Add($"[game_out_pre_raw]{gameLoudnormFilter},aresample={targetSampleRate}[game_out_pre]");
        }
        else
        {
            chain.Add("[game_out_pre_raw]anull[game_out_pre]");
        }
        chain.Add("[game_trig]highpass=f=200,lowpass=f=3500," +
                  "agate=threshold=0.05:attack=5:release=100[trig_cleaned]");
        chain.Add("[trig_cleaned]equalizer=f=1000:t=q:w=2:g=10[trig_final]");

        // ─────────────────────────────────────────────────────────────────────────────────────
        // AUDIO_06 (#5) — THE PROTECTED-BASS LINE MOVES 150 Hz -> 250 Hz.
        //
        // Everything below this split is never ducked, so it is a permanent shelter for the music.
        // At 150 Hz that shelter covered the region where a gunshot's WEIGHT lives (roughly
        // 80-250 Hz), so the music's low-mid body kept sitting on top of gunfire even while its
        // treble politely stepped aside — the shots lost their punch without the music ever
        // appearing to be in the way.
        //
        // 250 Hz hands that band to the ducker while still leaving true bass (kick fundamental,
        // sub) untouched, which is what stops a ducked track going limp.
        // ⚠️ Raising this much further starts thinning the music audibly. 250-300 Hz is the useful
        // range; do not push it into the low mids.
        // ─────────────────────────────────────────────────────────────────────────────────────
        chain.Add($"{bgMusicLabel}acrossover=split=250[mus_low][mus_high]");

        // AUDIO_08: fall back to the SHARED tuned constants, not to stale literals. These used to
        // read 0.15 / 2.5 — the 2.5 was the pre-retune ratio, so any caller that did not supply a
        // config value silently got the old, inaudible ducking back.
        double dThresh = GetDouble(musicConfig, "ducking_threshold", SidechainCompressNode.TunedThreshold);
        double dRatio = GetDouble(musicConfig, "ducking_ratio", SidechainCompressNode.TunedRatio);

        chain.Add(new FilterChain()
            .WithInputs("mus_high", "trig_final")
            // AUDIO_08: ONLY the two genuinely configurable values are set here. Attack, Release and
            // Detection now come from SidechainCompressNode's defaults — they used to be hardcoded
            // to 1 / 400 / "rms" in this initialiser, which silently overrode the retuned defaults
            // and left the ducker on its old, inaudible settings.
            // ⚠️ DO NOT re-add Attack/Release/Detection here. Change the Tuned* constants instead.
            .AddNode(new SidechainCompressNode { Threshold = dThresh, Ratio = dRatio })
            .WithOutputs("mus_high_ducked")
            .ToFFmpegString());

        chain.Add(new FilterChain()
            .WithInputs("mus_low", "mus_high_ducked")
            .AddNode(new AmixNode { Inputs = 2, Weights = "1 1", Normalize = 0 })
            .WithOutputs("a_music_reconstructed")
            .ToFFmpegString());

        chain.Add($"[game_out_pre][a_music_reconstructed]amix=inputs=2:" +
                  $"duration=first:dropout_transition=3:weights='1 1':normalize=0," +
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
