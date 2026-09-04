
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// Builds the FFmpeg audio filter chain for the rendering pipeline.
/// 
/// Pipeline:
/// 1. Game audio: volume normalize, optional fade-in
/// 2. For each music track: atrim, fade in/out, volume, delay to align
/// 3. Mix multiple music tracks if present
/// 4. Sidechain ducking: split music at 250Hz (acrossover), duck the high band against game audio
/// 5. Reconstruct music, mix with game audio at weights '1 1', normalize=0
///
/// AUDIOCHK_01 — CORRECTIONS TO THIS SUMMARY, WHICH WAS WRONG ON TWO COUNTS:
///   - The crossover is at 250 Hz (`acrossover=split=250`), not 150 Hz.
///   - dynaudnorm/alimiter are NOT in this method. The final loudnorm + alimiter ceiling lives in
///     ProcessWorker and is gated behind `AutoSpikeFlattening`; with that off, NOTHING limits the
///     summed bus. Do not go looking for a limiter here.
///
/// LEVEL CONTRACT (why the game/music balance comes out right):
///   - The game bus reaches this method ALREADY normalised to AudioLoudnessProbe.TargetLufs by
///     ProcessWorker's second-pass loudnorm, which is why `volumeNormalizeDb` is passed as 0 when
///     `hasSecondPass` — applying it again would double-correct.
///   - Each music track is shifted by its own measured `musicBedGainDb` to the SAME absolute
///     AudioLoudnessProbe.MusicBedLufs.
///   - `musicFollowGainDb` is the fallback for tracks that could NOT be measured, which is why it
///     is applied only when `!bedApplied`. Applying both to one track is a bug.
///   - Both buses are then scaled by their own slider and summed with weights '1 1', normalize=0.
/// So with both sliders at 100% the two buses are level-matched by construction. Anything that
/// changes one side's reference level without changing the other's breaks that contract.
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
    /// <param name="voiceProtectMusicPulse">
    /// VOPROT_01 — "Protect VoiceOver Recording from Music". An ffmpeg expression that evaluates to
    /// 1.0 wherever a voice-over take is playing and 0 elsewhere, built by ProcessWorker from the
    /// take times. Non-null means the MUSIC bed is ducked and carved across those windows.
    ///
    /// This is NOT the wizard's own ducking. That one is triggered by the GAME and protects the
    /// game from the music. This one is triggered by the VOICE and protects the voice from the
    /// music, so the two are orthogonal and can be on or off in any combination.
    ///
    /// It is applied to the music bed BEFORE the wizard's crossover/sidechain apparatus, so the
    /// music that reaches the sidechain is already out of the voice's way and the two stages do
    /// not fight over the same band.
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
        IReadOnlyDictionary<string, double>? musicBedGainDb = null,
        string? voiceProtectMusicPulse = null)
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
            catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
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
                      $"aresample={targetSampleRate}:async=1[game_leveled_base]");

            if (!string.IsNullOrEmpty(voiceOverLabel))
            {
                chain.Add($"[game_leveled_base]{voiceOverLabel}amix=inputs=2:duration=first:dropout_transition=2:normalize=0[a_main_prepared]");
            }
            else
            {
                chain.Add("[game_leveled_base]anull[a_main_prepared]");
            }

            return (chain, "[a_main_prepared]");
        }

        double initialDelaySec = 0;
        if (musicConfig != null)
        {
            try { initialDelaySec = Math.Max(0, (double)(musicConfig["timeline_start_sec"]?.GetValue<double>() ?? 0)); }
            catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
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

            double mVol = GetDouble(musicConfig, "music_vol", GetDouble(musicConfig, "volume", 0.8));

            double bedDb = 0.0;
            bool bedApplied = musicBedGainDb != null
                              && musicBedGainDb.TryGetValue(track.Path, out bedDb);

            if (bedApplied && Math.Abs(bedDb) > 0.01)
            {
                mVol *= Math.Pow(10, bedDb / 20.0);
            }

            if (!bedApplied && Math.Abs(musicFollowGainDb) > 0.01)
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

        // AUDIOCHK_01 — READ THE FLAG WITHOUT CARING HOW IT WAS STORED, AND NEVER THROW HERE.
        // This was `musicConfig["carving_enabled"]?.GetValue<bool>() ?? true` with NO try/catch,
        // while every other value in this method goes through GetDouble's guarded read. It is the
        // same trap CROPCHK_01 and HardwareCapability were both bitten by: System.Text.Json's
        // GetValue<T> demands the EXACT stored type, so a flag that ever arrives as a quoted
        // "true" or a 0/1 number throws — and this throw is on the export path, where it kills
        // the whole job. Parsed off the raw text instead: true/false, "true"/"false" and 0/1 all
        // work, and anything unrecognised degrades to the safe default (carving ON) rather than
        // failing the render.
        bool carvingEnabled = ReadBool(musicConfig, "carving_enabled", true);
        if (carvingEnabled)
        {
            chain.Add($"{bgMusicLabel}equalizer=f=2000:width_type=h:width=1800:g=-4[a_bg_music]");
            bgMusicLabel = "[a_bg_music]";
        }

        // VOPROT_01 — protect the voice from the music. Duck 85% across the takes and carve the
        // speech band underneath them, using the same pulse envelope the game bus was given.
        // Applied here, before the crossover/sidechain apparatus below, so the music arriving at
        // the sidechain is already clear of the voice and the two stages cannot fight.
        if (!string.IsNullOrEmpty(voiceProtectMusicPulse))
        {
            chain.Add($"{bgMusicLabel}volume='1.0-0.85*{voiceProtectMusicPulse}':eval=frame," +
                      $"equalizer=f=2500:width_type=h:width=2200:g=-3[a_bg_voice_protected]");
            bgMusicLabel = "[a_bg_voice_protected]";
            CoreLogger.Info("Audio", "Voice protection: music bed ducked 85% and carved at 2.5 kHz across the voice-over takes.");
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

        double dThresh = GetDouble(musicConfig, "ducking_threshold", SidechainCompressNode.TunedThreshold);
        double dRatio = GetDouble(musicConfig, "ducking_ratio", SidechainCompressNode.TunedRatio);

        // DUCKOFF_01 — "DUCKING OFF" NOW MEANS THE FILTERS ARE ABSENT, NOT NEUTRALISED.
        //
        // It used to mean BYPASS: the wizard sent SidechainCompressNode.BypassThreshold/BypassRatio
        // (1.0 / 1.0) and the whole apparatus still ran — the game bus was split with asplit, a
        // trigger bus was built with highpass+lowpass+agate, the music was split at 250 Hz with
        // acrossover, pushed through sidechaincompress and summed back. ratio=1 does make the
        // compressor mathematically unity, so the LEVEL was right, but "unity" is not "nothing":
        // the acrossover split-and-sum is an allpass round trip that shifts phase around 250 Hz,
        // and the whole trigger bus was decoded and filtered for a result nobody consumed.
        //
        // Unchecked now means the music goes to the mix UNTOUCHED and the trigger bus is never
        // built. Checked is byte-for-byte the graph that shipped before.
        //
        // The flag is explicit ("ducking_enabled"). The threshold/ratio fallback below is only for
        // configs written before that key existed — a bypass ratio of 1.0 is how "off" used to be
        // encoded, so an old recovery file still behaves the way the user left it.
        bool duckingEnabled = ReadBool(musicConfig, "ducking_enabled",
                                       dRatio > SidechainCompressNode.BypassRatio + 0.0001);

        string gameForMix;

        if (duckingEnabled)
        {
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
                      "agate=threshold=0.05:attack=5:release=100[trig_final]");

            chain.Add($"{bgMusicLabel}acrossover=split=250[mus_low][mus_high]");

            chain.Add(new FilterChain()
                .WithInputs("mus_high", "trig_final")
                .AddNode(new SidechainCompressNode { Threshold = dThresh, Ratio = dRatio })
                .WithOutputs("mus_high_ducked")
                .ToFFmpegString());

            chain.Add(new FilterChain()
                .WithInputs("mus_low", "mus_high_ducked")
                .AddNode(new AmixNode { Inputs = 2, Weights = "1 1", Normalize = 0 })
                .WithOutputs("a_music_reconstructed")
                .ToFFmpegString());

            gameForMix = "[game_out_pre]";
            bgMusicLabel = "[a_music_reconstructed]";
        }
        else
        {
            // DUCKOFF_01 — NO asplit. The split existed ONLY to feed the trigger bus; with no
            // sidechain there is no second consumer, and an unconsumed asplit output pad makes
            // ffmpeg reject the whole filter_complex.
            if (useLoudnorm)
            {
                chain.Add($"[game_leveled]{gameLoudnormFilter},aresample={targetSampleRate}[game_out_pre]");
            }
            else
            {
                chain.Add("[game_leveled]anull[game_out_pre]");
            }

            gameForMix = "[game_out_pre]";
            // bgMusicLabel is carried through untouched: no crossover, no compressor, no re-sum.
        }

        chain.Add($"{gameForMix}{bgMusicLabel}amix=inputs=2:" +
                  $"duration=first:dropout_transition=3:weights='1 1':normalize=0," +
                  $"aresample={targetSampleRate}:async=1[a_music_prepared]");

        return (chain, "[a_music_prepared]");
    }

    /// <summary>
    /// AUDIOCHK_01 — type-agnostic, non-throwing bool read. See the comment at the call site.
    /// </summary>
    private static bool ReadBool(JsonObject? obj, string key, bool defaultValue)
    {
        var node = obj?[key];
        if (node == null) return defaultValue;

        string raw = node.ToString().Trim().Trim('"');
        if (bool.TryParse(raw, out bool parsed)) return parsed;
        if (raw == "1") return true;
        if (raw == "0") return false;
        return defaultValue;
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
