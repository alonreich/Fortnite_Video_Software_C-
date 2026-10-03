// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.


using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// Builds the FFmpeg audio filter chain for the rendering pipeline (Main App and Video Merger —
/// one implementation, so a mix sounds the same in both).
///
/// Pipeline:
/// 1. Game audio: optional caller filters, the VIDEO fader.
/// 2. For each music track: atrim, fade in/out, the MUSIC fader, delay to align; several tracks are summed.
/// 3. VOPROT_01: across voice-over takes only, the music is ducked 85% and speech-carved (gated).
/// 4. DUCKMB_01: multiband sidechain against the gameplay (+voice) — see <see cref="Build"/>.
/// 5. Game and music are summed with weights '1 1', normalize=0.
///
/// PRIORITY (VOPRIO_01): voice-over first, gameplay second, music last. The voice is protected from
/// both other buses by the take pulse (step 3 here, the game side in ProcessWorker); the gameplay is
/// protected from the music by the sidechain (step 4), whose trigger is the game bus WITH the voice
/// mixed in, so a take also pushes the music down.
///
/// LEVEL CONTRACT (LOUDSTD_REMOVED_01): there is no loudness normalisation anywhere. Each bus
/// arrives at its recorded level and is scaled by its own fader only. Peak protection
/// (<see cref="PeakSafety"/>) is applied by the callers: the tamer on the game bus before it gets
/// here, the always-on safety limiter on the final mix after.
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
        bool musicLeadFadeIn = true,
        bool musicTailFadeOut = true,
        string? voiceOverLabel = null,
        string? voiceProtectMusicPulse = null)
    {
        var chain = new List<string>();

        musicConfig ??= new JsonObject();
        int targetSampleRate = sampleRate > 0 ? sampleRate : 48000;

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
            double vVol = GetDouble(musicConfig, "main_vol", GetDouble(musicConfig, "video_volume", 1.0));
            chain.Add($"[a_main_raw]volume={vVol.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}," +
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

        // AUDIOCHK_01 — flags are read type-agnostically and never throw (a quoted "true" or a 0/1
        // number must not kill the export). Unrecognised values degrade to the default.
        bool carvingEnabled = ReadBool(musicConfig, "carving_enabled", true);

        // DUCKOFF_01 — explicit flag. The ratio fallback is only for configs written before the key
        // existed: a bypass ratio of 1.0 is how "off" used to be encoded.
        double legacyRatio = GetDouble(musicConfig, "ducking_ratio", SidechainCompressNode.TunedRatio);
        bool duckingEnabled = ReadBool(musicConfig, "ducking_enabled",
                                       legacyRatio > SidechainCompressNode.BypassRatio + 0.0001);

        // VOPROT_01 / VOPRIO_01 — protect the voice from the music FIRST: across the takes only, the
        // bed is ducked 85% and its speech band carved. Gated by the same pulse (VOGATE_01), so the
        // music between takes is untouched. Applied before the sidechain stage below, so the music
        // arriving there is already out of the voice's way.
        if (!string.IsNullOrEmpty(voiceProtectMusicPulse))
        {
            chain.AddRange(GatedVoiceProtection(bgMusicLabel, voiceProtectMusicPulse, "vpm", "[a_bg_voice_protected]"));
            bgMusicLabel = "[a_bg_voice_protected]";
            CoreLogger.Info("Audio", "Voice protection: music bed ducked 85% and carved at 2.5 kHz during the voice-over takes only.");
        }

        double vVolGame = GetDouble(musicConfig, "main_vol", GetDouble(musicConfig, "video_volume", 1.0));

        chain.Add($"[a_main_raw]volume={vVolGame.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)},aresample={targetSampleRate}:async=1[game_leveled_base]");

        if (!string.IsNullOrEmpty(voiceOverLabel))
        {
            chain.Add($"[game_leveled_base]{voiceOverLabel}amix=inputs=2:duration=first:dropout_transition=2:normalize=0[game_leveled]");
        }
        else
        {
            chain.Add("[game_leveled_base]anull[game_leveled]");
        }

        string gameForMix;
        if (duckingEnabled || carvingEnabled)
        {
            // DUCKMB_01 — MULTIBAND SIDECHAIN. Both protections are now DYNAMIC: they act only while
            // the gameplay (+voice) is actually sounding, and leave the music alone in quiet stretches.
            //
            //   music → acrossover 250 Hz / 2900 Hz → LOW | MID (speech/gameplay band) | HIGH
            //   LOW  (<250 Hz)   never touched — the bed keeps its body.
            //   MID  ducking (ratio 4) and/or carving (ratio 2.5, the dynamic replacement of the old
            //        static 2 kHz -4 dB EQ, which dulled the music for the whole video).
            //   HIGH ducking (ratio 4) only.
            //   The three bands are summed back (measured flat to ±0.001 dB with no compression).
            //
            // Old ducking was threshold 0.15 / ratio 1.13: measured -1.2 dB under a near-full-scale
            // trigger, i.e. inaudible. The tuned stage measures about -8 dB under the same trigger.
            // DUCKSTRENGTH_01 — the two strength handles from Settings (0-100, 50 = tuned).
            int duckStrength = (int)Math.Round(GetDouble(musicConfig, "ducking_strength", SidechainCompressNode.DefaultStrength));
            int carveStrength = (int)Math.Round(GetDouble(musicConfig, "carving_strength", SidechainCompressNode.DefaultStrength));
            var compressors = new List<(string band, SidechainCompressNode node)>();
            if (duckingEnabled)
            {
                compressors.Add(("mid", SidechainCompressNode.Duck(duckStrength)));
                compressors.Add(("high", SidechainCompressNode.Duck(duckStrength)));
            }
            if (carvingEnabled)
            {
                compressors.Add(("mid", SidechainCompressNode.Carve(carveStrength)));
            }

            int n = compressors.Count;
            chain.Add($"[game_leveled]asplit=2[game_out_pre][game_trig]");
            // Both sidechaincompress inputs are pinned to one explicit format: with a split trigger
            // the graph otherwise has no channel layout to negotiate and ffmpeg rejects it
            // ("No channel layout for input 1").
            string pinFormat = $"aformat=sample_fmts=fltp:sample_rates={targetSampleRate}:channel_layouts=stereo";
            chain.Add("[game_trig]highpass=f=200,lowpass=f=3500," +
                      $"agate=threshold=0.05:attack=5:release=100,{pinFormat}" +
                      (n > 1 ? $",asplit={n}{string.Concat(Enumerable.Range(0, n).Select(k => $"[trig_{k}]"))}" : "[trig_0]"));

            chain.Add($"{bgMusicLabel}{pinFormat},acrossover=split='{CrossoverLowHz} {CrossoverHighHz}'[mus_low][mus_mid][mus_high]");
            var bandLabel = new Dictionary<string, string> { ["mid"] = "mus_mid", ["high"] = "mus_high" };
            for (int k = 0; k < n; k++)
            {
                var (band, node) = compressors[k];
                string outLabel = $"mus_{band}_c{k}";
                chain.Add(new FilterChain()
                    .WithInputs(bandLabel[band], $"trig_{k}")
                    .AddNode(node)
                    .WithOutputs(outLabel)
                    .ToFFmpegString());
                bandLabel[band] = outLabel;
            }

            chain.Add(new FilterChain()
                .WithInputs("mus_low", bandLabel["mid"], bandLabel["high"])
                .AddNode(new AmixNode { Inputs = 3, Weights = "1 1 1", Normalize = 0 })
                .WithOutputs("a_music_reconstructed")
                .ToFFmpegString());

            gameForMix = "[game_out_pre]";
            bgMusicLabel = "[a_music_reconstructed]";
            CoreLogger.Info("Audio",
                $"Music protection: ducking {(duckingEnabled ? $"ON (strength {duckStrength})" : "OFF")}, dynamic speech-band carving {(carvingEnabled ? $"ON (strength {carveStrength})" : "OFF")} " +
                $"(multiband {CrossoverLowHz}/{CrossoverHighHz} Hz, {n} sidechain stage(s)).");
        }
        else
        {
            // DUCKOFF_01 — no asplit, no trigger bus, no crossover: the music reaches the mix untouched.
            // (An unconsumed asplit pad would make ffmpeg reject the whole filter_complex.)
            gameForMix = "[game_leveled]";
        }

        chain.Add($"{gameForMix}{bgMusicLabel}amix=inputs=2:" +
                  $"duration=first:dropout_transition=3:weights='1 1':normalize=0," +
                  $"aresample={targetSampleRate}:async=1[a_music_prepared]");

        return (chain, "[a_music_prepared]");
    }

    /// <summary>DUCKMB_01 — the music bed's band edges, Hz.</summary>
    public const int CrossoverLowHz = 250;
    public const int CrossoverHighHz = 2900;

    /// <summary>VOPROT_01 — the voice protection's level dip (85%) and speech-band carve.</summary>
    public const double VoiceDuckDepth = 0.85;
    public const string VoiceCarveEq = "equalizer=f=2500:width_type=h:width=2200:g=-3";

    /// <summary>
    /// VOGATE_01 — voice protection that exists ONLY across the takes.
    ///
    /// The level dip was always time-varying (<c>volume=...:eval=frame</c>), but the speech carve was a
    /// plain <c>equalizer</c>, which has no time-varying gain — so it scooped 2.5 kHz out of the whole
    /// bus for the whole video, takes or no takes. Now the bus is split: a carved copy is faded in by
    /// the take pulse and the dry copy faded out by its complement (the pulse already ramps over
    /// 0.3 s), then the two are summed. Outside the takes the output is the dry signal exactly.
    /// </summary>
    public static IEnumerable<string> GatedVoiceProtection(string inLabel, string pulseExpr, string prefix, string outLabel)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        string depth = VoiceDuckDepth.ToString("F2", ci);
        yield return $"{inLabel}asplit=2[{prefix}_dry][{prefix}_wet]";
        yield return $"[{prefix}_wet]{VoiceCarveEq},volume='{pulseExpr}':eval=frame[{prefix}_wet_g]";
        yield return $"[{prefix}_dry]volume='1-({pulseExpr})':eval=frame[{prefix}_dry_g]";
        yield return $"[{prefix}_dry_g][{prefix}_wet_g]amix=inputs=2:weights='1 1':normalize=0," +
                     $"volume='1.0-{depth}*({pulseExpr})':eval=frame{outLabel}";
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
        catch (System.Exception swallowed)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed);   // FAULTTIER_02 — no failure is silent.
            return defaultValue;
        }
    }
}

/// <summary>
/// Represents a single music track for the audio chain.
/// Path, offset (in seconds from file start), duration, timeline delay (in seconds from video start), and whether to fade out.
/// </summary>
public record MusicTrack(string Path, double Offset, double Duration, double TimelineStartDelay = 0.0, bool ApplyFadeOut = true);
