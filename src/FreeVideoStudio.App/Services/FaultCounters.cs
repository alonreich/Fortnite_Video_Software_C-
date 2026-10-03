// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using FreeVideoStudio.Core.Abstractions;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// LOGVIS_01 — per-session fault totals by tier and area. The spec (COMP-FAULTCHANNEL) promised
/// that routed failures would be "classified, counted, in the diagnostic bundle". Nothing counted
/// them. This does, lock-free, and <see cref="DiagnosticBundle"/> prints it.
/// </summary>
public static class FaultCounters
{
    private static readonly ConcurrentDictionary<string, long> Counts = new();

    public static void Record(Fault fault)
        => Counts.AddOrUpdate($"{fault.Tier}/{fault.Area}", 1, static (_, n) => n + 1);

    public static string Describe()
    {
        if (Counts.IsEmpty) return "(no faults recorded this session)";
        var sb = new StringBuilder();
        foreach (var kv in Counts.OrderByDescending(k => k.Value))
            sb.Append(kv.Key.PadRight(40)).Append(kv.Value).AppendLine();
        return sb.ToString().TrimEnd();
    }
}
