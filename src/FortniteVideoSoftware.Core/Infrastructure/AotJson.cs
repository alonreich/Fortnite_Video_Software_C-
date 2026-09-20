// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/06_PROJECT_DOCUMENT_MODEL.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace FortniteVideoSoftware.Core.Infrastructure;

/// <summary>
/// AOTSAFETY_02 — APPENDING A NODE TO A <see cref="JsonArray"/> WITHOUT TRIPPING THE TRIM ANALYSER.
///
/// <para>
/// <c>JsonArray.Add&lt;T&gt;(T)</c> carries <c>RequiresUnreferencedCode</c> and
/// <c>RequiresDynamicCode</c>, because <c>T</c> could be an arbitrary POCO that System.Text.Json
/// would have to reflect over. Handing it a <see cref="JsonNode"/> never does that — the node is
/// already a JSON tree — but C# overload resolution picks the generic anyway, because
/// <c>JsonArray</c> implements <c>ICollection&lt;JsonNode?&gt;.Add</c> EXPLICITLY, so the safe
/// non-generic overload is not visible on the type. The call site therefore inherits a warning for
/// a risk it does not actually take.
/// </para>
///
/// <para>
/// Casting to the interface binds to the unannotated method. The distinction matters:
/// <see cref="AddNode"/> makes the safety PROVABLE to the analyser, where a
/// <c>#pragma warning disable</c> would merely assert it and would go on hiding the next call —
/// possibly one that really does pass a POCO. That is precisely the failure mode AOTSAFETY_01
/// (<c>docs/06_PROJECT_DOCUMENT_MODEL.md#PROJ-AOT</c>) exists to end, so it must not be reproduced
/// one call site at a time.
/// </para>
/// </summary>
public static class AotJson
{
    /// <summary>Appends <paramref name="node"/> without reflection. See the type remarks.</summary>
    public static void AddNode(this JsonArray array, JsonNode? node)
        => ((IList<JsonNode?>)array).Add(node);
}
