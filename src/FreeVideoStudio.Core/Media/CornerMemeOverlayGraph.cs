// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FreeVideoStudio.Core.Media;

/// <summary>One corner-overlay meme as the graph needs it.</summary>
/// <param name="InputIndex">FFmpeg input index of the meme file (images: <c>-loop 1 -framerate F -t D</c>).</param>
/// <param name="StartSec">Output second (of the stream being overlaid) at which the meme appears.</param>
/// <param name="EndSec">Output second at which it disappears (already clipped to the stream).</param>
public sealed record CornerMemeInput(
    int InputIndex,
    bool IsImage,
    bool HasAudio,
    double StartSec,
    double EndSec,
    MemeOverlayCorner Corner,
    MemeOverlaySize Size,
    bool PlaySound,
    double GainDb = 0);

/// <summary>Filters plus the labels that carry the overlaid video and the mixed audio.</summary>
public sealed record CornerMemeOverlayResult(IReadOnlyList<string> Filters, string VideoLabel, string AudioLabel);

/// <summary>
/// MEMEMODE_01 — FFM-MEMECORNER. Corner-overlay memes over a running stream, for BOTH the Main App
/// export (<see cref="ProcessWorker"/>) and the Merger's effects clips (<see cref="MergeClipGraph"/>).
///
/// <para><b>Zero added duration, by construction.</b> The overlaid stream is the MAIN input of every
/// <c>overlay</c> (<c>eof_action=pass</c>) and the FIRST input of the audio <c>amix</c>
/// (<c>duration=first</c>), so the output is exactly as long as the input — nothing after the meme
/// moves. The meme picture is shifted onto the stream's clock (<c>setpts=PTS+S/TB</c>) and gated
/// with <c>enable='between(t,S,E)'</c>.</para>
///
/// <para><b>Geometry</b> is <see cref="MemeOverlayLayout"/>, the same numbers every preview draws with.
/// <b>Sound</b> (only when <see cref="CornerMemeInput.PlaySound"/> and the file has audio) is matched to
/// the gameplay (MEMELEVEL_02 gain), de-clicked (SPLICE_03), delayed to S and summed over the game
/// audio without normalisation.</para>
/// </summary>
public static class CornerMemeOverlayGraph
{
    public static CornerMemeOverlayResult Build(
        string videoIn,
        string audioIn,
        IReadOnlyList<CornerMemeInput> memes,
        int frameW,
        int frameH,
        string fps,
        string labelPrefix)
    {
        var filters = new List<string>();
        if (memes == null || memes.Count == 0) return new CornerMemeOverlayResult(filters, videoIn, audioIn);

        string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);
        string currentV = videoIn;
        var audioPads = new List<string>();

        for (int k = 0; k < memes.Count; k++)
        {
            var m = memes[k];
            double len = m.EndSec - m.StartSec;
            if (!(len > 0.001)) continue;

            string ov = $"[{labelPrefix}cm{k}_v]";
            string outV = $"[{labelPrefix}cm{k}_o]";
            var (x, y) = MemeOverlayLayout.OverlayPosition(frameW, frameH, m.Corner);

            filters.Add(
                $"[{m.InputIndex}:v]trim=duration={F(len)},setpts=PTS-STARTPTS,fps={fps}:round=near," +
                $"{MemeOverlayLayout.ScaleFilter(frameW, frameH, m.Size)},format=yuva420p," +
                $"setpts=PTS+{F(m.StartSec)}/TB{ov}");
            filters.Add(
                $"{currentV}{ov}overlay=x={x}:y={y}:eof_action=pass:" +
                $"enable='between(t,{F(m.StartSec)},{F(m.EndSec)})'{outV}");
            currentV = outV;

            if (m.PlaySound && m.HasAudio && !m.IsImage)
            {
                string a = $"[{labelPrefix}cm{k}_a]";
                long delayMs = (long)Math.Round(m.StartSec * 1000.0);
                filters.Add(
                    $"[{m.InputIndex}:a]atrim=duration={F(len)},asetpts=PTS-STARTPTS,aresample=48000," +
                    $"aformat=sample_fmts=fltp:channel_layouts=stereo:sample_rates=48000" +
                    $"{MemeLoudness.Chain(m.GainDb)}{MemeLoudness.SpliceFade(len)}," +
                    $"adelay=delays={delayMs}:all=1{a}");
                audioPads.Add(a);
            }
        }

        string currentA = audioIn;
        if (audioPads.Count > 0)
        {
            string mixed = $"[{labelPrefix}cm_amix]";
            filters.Add($"{audioIn}{string.Concat(audioPads)}amix=inputs={audioPads.Count + 1}:normalize=0:duration=first:dropout_transition=0{mixed}");
            currentA = mixed;
        }

        return new CornerMemeOverlayResult(filters, currentV, currentA);
    }

    /// <summary>The meme inputs as FFmpeg arguments: images looped for their visible length, videos as-is.</summary>
    public static IEnumerable<string> InputArgs(string path, bool isImage, double durationSec, string fps)
    {
        if (isImage)
        {
            yield return "-loop"; yield return "1";
            yield return "-framerate"; yield return fps;
            yield return "-t"; yield return durationSec.ToString("F3", CultureInfo.InvariantCulture);
        }
        yield return "-i"; yield return path;
    }
}
