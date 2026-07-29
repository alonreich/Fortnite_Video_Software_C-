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
    public string Width { get; init; } = string.Empty;
    public string Height { get; init; } = string.Empty;
    public string X { get; init; } = string.Empty;
    public string Y { get; init; } = string.Empty;
    public string Color { get; init; } = "black";

    public override string ToFFmpegString() => $"pad=iw+{Width}:ih+{Height}:{X}:{Y}:color={Color}";
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

public class FilterGraph
{
    public List<FilterChain> Chains { get; } = new();

    public FilterChain AddChain()
    {
        var chain = new FilterChain();
        Chains.Add(chain);
        return chain;
    }

    public void AddRaw(string rawChain)
    {
        var chain = new FilterChain();
        chain.AddRaw(rawChain);
        Chains.Add(chain);
    }

    public string ToFFmpegString()
    {
        return string.Join(";", Chains.Select(c => c.ToFFmpegString()).Where(s => !string.IsNullOrEmpty(s)));
    }
}

public class VolumeNode : FilterNode
{
    public double Volume { get; init; }
    public override string ToFFmpegString() => $"volume={Volume.ToString("F4", CultureInfo.InvariantCulture)}";
}

public class LoudnormNode : FilterNode
{
    public double I { get; init; } = -16.0;
    public double Lra { get; init; } = 11.0;
    public double Tp { get; init; } = -1.5;
    public double TargetOffset { get; init; } = 0.0;
    public override string ToFFmpegString() => $"loudnorm=I={I.ToString("F2", CultureInfo.InvariantCulture)}:LRA={Lra.ToString("F2", CultureInfo.InvariantCulture)}:tp={Tp.ToString("F2", CultureInfo.InvariantCulture)}:offset={TargetOffset.ToString("F2", CultureInfo.InvariantCulture)}";
}

public class AresampleNode : FilterNode
{
    public int SampleRate { get; init; } = 48000;
    public int Async { get; init; } = 1;
    public override string ToFFmpegString() => $"aresample={SampleRate}:async={Async}";
}

public class ConcatNode : FilterNode
{
    public int N { get; init; }
    public int V { get; init; }
    public int A { get; init; }
    public override string ToFFmpegString() => $"concat=n={N}:v={V}:a={A}";
}

public class SplitNode : FilterNode
{
    public int Branches { get; init; } = 2;
    public override string ToFFmpegString() => Branches == 2 ? "split=2" : $"split={Branches}";
}

public class ASplitNode : FilterNode
{
    public int Branches { get; init; } = 2;
    public override string ToFFmpegString() => Branches == 2 ? "asplit=2" : $"asplit={Branches}";
}
