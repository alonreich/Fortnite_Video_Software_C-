using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FortniteVideoSoftware.Core.Media;

public abstract class FilterNode
{
    public abstract string ToFFmpegString();
}

public class RawFilterNode : FilterNode
{
    private readonly string _rawFilter;
    public RawFilterNode(string rawFilter) { _rawFilter = rawFilter; }
    public override string ToFFmpegString() => _rawFilter;
}

public class PadFilterNode : FilterNode
{
    /// <summary>ABSOLUTE output canvas width in pixels (not a delta — see the class remarks).</summary>
    public string Width { get; init; } = string.Empty;
    /// <summary>ABSOLUTE output canvas height in pixels (not a delta — see the class remarks).</summary>
    public string Height { get; init; } = string.Empty;
    public string X { get; init; } = string.Empty;
    public string Y { get; init; } = string.Empty;
    public string Color { get; init; } = "black";

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // G05 — THIS USED TO EMIT `pad=iw+{Width}` WHILE EVERY CALLER PASSED AN ABSOLUTE CANVAS SIZE.
    //
    // GranularSpeedBuilder computes `canvasW = resW + 2*padX` (the finished canvas) and handed it
    // straight to `Width`. Prefixing `iw+` then ADDED THE SOURCE FRAME A SECOND TIME:
    //   intended: 2560x1440 -> 5120x2880   (already 4x the source area)
    //   actual:   2560x1440 -> 7680x4320   (9x the source area, 33 megapixels PER FRAME)
    // Every zoomed chunk allocated and black-filled that buffer for every single frame. The extra
    // 2560x1440 band sat off the right/bottom edge and was NEVER sampled by the crop that follows,
    // so removing it is pixel-for-pixel identical output at a fraction of the cost.
    //
    // The X/Y offsets were always absolute and always correct, which is why the geometry looked
    // right and the waste stayed invisible.
    //
    // AI AGENTS: do NOT "restore" the `iw+` / `ih+` prefixes. If you ever need a relative pad,
    // add a separate node type — do not overload this one.
    // ─────────────────────────────────────────────────────────────────────────────────────────
    public override string ToFFmpegString() => $"pad={Width}:{Height}:{X}:{Y}:color={Color}";
}

public class CropFilterNode : FilterNode
{
    public string Width { get; init; } = string.Empty;
    public string Height { get; init; } = string.Empty;
    public string X { get; init; } = string.Empty;
    public string Y { get; init; } = string.Empty;

    public override string ToFFmpegString() => $"crop=w='{Width}':h='{Height}':x='{X}':y='{Y}'";
}

public class CasFilterNode : FilterNode
{
    public double Strength { get; init; }
    public override string ToFFmpegString() => $"cas={Strength.ToString(CultureInfo.InvariantCulture)}";
}

public class ScaleFilterNode : FilterNode
{
    public string Width { get; init; } = string.Empty;
    public string Height { get; init; } = string.Empty;

    public override string ToFFmpegString() => $"scale={Width}:{Height}";
}

public class SidechainCompressNode : FilterNode
{
    public double Threshold { get; init; } = 0.15;
    public double Ratio { get; init; } = 2.5;
    public double Attack { get; init; } = 1;
    public double Release { get; init; } = 400;
    public string Detection { get; init; } = "rms";

    public override string ToFFmpegString() =>
        $"sidechaincompress=threshold={Threshold.ToString(CultureInfo.InvariantCulture)}:ratio={Ratio.ToString(CultureInfo.InvariantCulture)}:attack={Attack.ToString(CultureInfo.InvariantCulture)}:release={Release.ToString(CultureInfo.InvariantCulture)}:detection={Detection}";
}

public class AmixNode : FilterNode
{
    public int Inputs { get; init; } = 2;
    public string Weights { get; init; } = string.Empty;
    public int Normalize { get; init; } = 0;

    public override string ToFFmpegString()
    {
        var w = string.IsNullOrEmpty(Weights) ? "" : $":weights='{Weights}'";
        return $"amix=inputs={Inputs}{w}:normalize={Normalize}";
    }
}

public class FilterChain
{
    public List<string> InputLabels { get; } = new();
    public List<FilterNode> Nodes { get; } = new();
    public List<string> OutputLabels { get; } = new();

    public FilterChain AddNode(FilterNode node)
    {
        Nodes.Add(node);
        return this;
    }

    public FilterChain AddRaw(string raw)
    {
        Nodes.Add(new RawFilterNode(raw));
        return this;
    }

    public FilterChain WithInputs(params string[] inputs)
    {
        InputLabels.AddRange(inputs);
        return this;
    }

    public FilterChain WithOutputs(params string[] outputs)
    {
        OutputLabels.AddRange(outputs);
        return this;
    }

    public string ToFFmpegString()
    {
        string inputs = string.Join("", InputLabels.Select(l => l.StartsWith("[") ? l : $"[{l}]"));
        string outputs = string.Join("", OutputLabels.Select(l => l.StartsWith("[") ? l : $"[{l}]"));
        string filters = string.Join(",", Nodes.Select(n => n.ToFFmpegString()));
        return $"{inputs}{filters}{outputs}";
    }
}
