// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Text.Json.Nodes;

namespace FortniteVideoSoftware.App.Infrastructure;

/// <summary>
/// GRANJSON_01 — tolerant readers for the granular editor's recovery payload, lifted out of
/// <c>GranularSpeedEditorWindow</c> (8,122 lines, 181 fields, one class).
///
/// ⚠️ TOLERANCE IS THE CONTRACT. Every one of these returns a caller-supplied fallback rather than
/// throwing, because they parse a RECOVERY FILE written by a possibly older build after a possibly
/// unclean shutdown. A stricter reader turns "one unexpected field" into "your crashed session
/// cannot be restored", which is the opposite of what recovery is for. Bodies moved verbatim; do
/// not make any of them throw.
/// </summary>
internal static class GranularJsonRead
{
    internal static double GetJsonDouble(JsonNode? node, double fallback)
        => node is JsonValue v && v.TryGetValue(out double d) ? d : fallback;

    internal static double? GetJsonDoubleOrNull(JsonNode? node)
        => node is JsonValue v && v.TryGetValue(out double d) ? d : null;

    internal static int? GetJsonIntOrNull(JsonNode? node)
        => node is JsonValue v && v.TryGetValue(out int i) ? i : null;

    internal static bool GetJsonBool(JsonNode? node, bool fallback)
        => node is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;

    internal static string? GetJsonString(JsonNode? node)
        => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
