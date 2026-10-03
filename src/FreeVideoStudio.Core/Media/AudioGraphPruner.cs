// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// PREVIEWMIX_01 — cuts an export filter graph down to the chains that produce ONE audio label.
///
/// The live preview's rendered mix is the export's OWN audio graph, not a re-implementation of it:
/// ProcessWorker builds the full graph exactly as it would for the export, and this keeps only the
/// chains the final audio label depends on. The single coupling between picture and sound in that
/// graph — the meme splice <c>concat=n=N:v=1:a=1</c> — is rewritten to its audio half
/// (<c>v=0:a=1</c>, audio inputs and output only). Video inputs are then never decoded, so a whole
/// clip's final mix renders in seconds.
///
/// A kept chain whose output nothing kept consumes (e.g. one pad of a split whose other consumer
/// was video) is terminated with <c>anullsink</c>, because ffmpeg rejects unconnected pads.
/// </summary>
public static class AudioGraphPruner
{
    private static readonly Regex LeadingLabels = new(@"^(\s*\[[^\]]+\])+", RegexOptions.CultureInvariant);
    private static readonly Regex TrailingLabels = new(@"(\[[^\]]+\]\s*)+$", RegexOptions.CultureInvariant);
    private static readonly Regex OneLabel = new(@"\[([^\]]+)\]", RegexOptions.CultureInvariant);
    private static readonly Regex ConcatAv = new(@"^concat=n=(\d+):v=1:a=1$", RegexOptions.CultureInvariant);

    private sealed record Chain(List<string> Inputs, string Body, List<string> Outputs);

    /// <summary>Returns the pruned graph whose only exposed output is <paramref name="finalAudioLabel"/>.</summary>
    public static string Prune(string filterGraph, string finalAudioLabel)
    {
        string final = finalAudioLabel.Trim().Trim('[', ']');
        var chains = SplitTopLevel(filterGraph).Select(Parse).Where(c => c != null).Cast<Chain>().ToList();

        var needed = new HashSet<string>(StringComparer.Ordinal) { final };
        var kept = new List<Chain>();
        for (int i = chains.Count - 1; i >= 0; i--)
        {
            var c = chains[i];
            if (!c.Outputs.Any(needed.Contains)) continue;

            var m = ConcatAv.Match(c.Body);
            if (m.Success)
            {
                int n = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (c.Inputs.Count != 2 * n || c.Outputs.Count != 2)
                    throw new InvalidOperationException($"Unexpected A/V concat shape: {c.Inputs.Count} inputs, {c.Outputs.Count} outputs.");
                var audioIn = c.Inputs.Where((_, k) => k % 2 == 1).ToList();
                c = new Chain(audioIn, $"concat=n={n}:v=0:a=1", new List<string> { c.Outputs[1] });
            }

            kept.Add(c);
            foreach (var input in c.Inputs)
                if (!IsStreamSpecifier(input)) needed.Add(input);
        }
        kept.Reverse();

        var consumed = new HashSet<string>(kept.SelectMany(c => c.Inputs), StringComparer.Ordinal);
        var sb = new StringBuilder();
        int sink = 0;
        foreach (var c in kept)
        {
            if (sb.Length > 0) sb.Append(';');
            sb.Append(string.Concat(c.Inputs.Select(l => $"[{l}]"))).Append(c.Body).Append(string.Concat(c.Outputs.Select(l => $"[{l}]")));
            foreach (var o in c.Outputs)
            {
                if (o == final || consumed.Contains(o)) continue;
                sb.Append(';').Append($"[{o}]anullsink");
                sink++;
            }
        }
        return sb.ToString();
    }

    /// <summary>"0:a", "12:v" — a reference to an input file's stream, not a graph label.</summary>
    private static bool IsStreamSpecifier(string label) => label.Length > 0 && char.IsDigit(label[0]) && label.Contains(':');

    private static Chain? Parse(string text)
    {
        string t = text.Trim();
        if (t.Length == 0) return null;
        var inputs = new List<string>();
        var lead = LeadingLabels.Match(t);
        if (lead.Success)
        {
            foreach (Match l in OneLabel.Matches(lead.Value)) inputs.Add(l.Groups[1].Value);
            t = t.Substring(lead.Length);
        }
        var outputs = new List<string>();
        var trail = TrailingLabels.Match(t);
        if (trail.Success)
        {
            foreach (Match l in OneLabel.Matches(trail.Value)) outputs.Add(l.Groups[1].Value);
            t = t.Substring(0, trail.Index);
        }
        return new Chain(inputs, t.Trim(), outputs);
    }

    /// <summary>Splits on ';' outside single quotes (expressions may contain anything).</summary>
    private static IEnumerable<string> SplitTopLevel(string graph)
    {
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (char ch in graph)
        {
            if (ch == '\'') quoted = !quoted;
            if (ch == ';' && !quoted)
            {
                yield return sb.ToString();
                sb.Clear();
                continue;
            }
            sb.Append(ch);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}

/// <summary>
/// PREVIEWMIX_01 — where the live preview's clock lands in the rendered mix.
///
/// The preview clock is BODY output seconds from MARK START (OutputTimeline, memes excluded). The
/// rendered mix is the export's audio: thumbnail intro first, then the body starting at the
/// fade-in pad, with every meme spliced in at its render-clock cut.
/// </summary>
/// <param name="IntroSec">Thumbnail-intro silence at the very start.</param>
/// <param name="PadStartSec">Fade-in pad before MARK START (output seconds).</param>
/// <param name="Memes">Each spliced meme: its cut in the render clock (intro included) and its length.</param>
public sealed record AudioPreviewMap(double IntroSec, double PadStartSec, IReadOnlyList<(double CutRenderSec, double DurationSec)> Memes)
{
    /// <summary>Mix position for preview body time <paramref name="bodySec"/> (memes at that instant not yet played).</summary>
    public double MixSecFor(double bodySec)
    {
        double r = IntroSec + PadStartSec + Math.Max(0, bodySec);
        double inserted = 0;
        foreach (var (cut, dur) in Memes)
            if (cut < r - 1e-6) inserted += dur;
        return r + inserted;
    }
}
