// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Ipc;
using FortniteVideoSoftware.Core.Media;   // Frac, CoordinateMath

namespace FortniteVideoSoftware.App.Infrastructure;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// CROPJSON_01 — the typed accessors over the crop-tool config document.
///
/// Extracted from <c>CropToolWindow</c>, where they were private statics among 172 methods. They
/// touch no window state, and between them they have FORTY call sites in that file — which is
/// exactly why they were invisible there: the reading and writing rules for a cross-process
/// config file were buried inside a UI class.
///
/// ⚠️ THIS DOCUMENT IS A CROSS-PROCESS, CROSS-VERSION CONTRACT. <c>CropConfigStore</c> is read
/// back by the Main App after the Crop Tool hands off. A config written by a new build must still
/// load in an older one and vice versa, so the tolerant parsing below (fallback on absent,
/// malformed or wrongly-typed nodes) is the contract, not defensive padding. Bodies moved
/// verbatim; do not "tighten" any of them.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
internal static class CropConfigJson
{
    internal static JsonObject EnsureObject(JsonObject config, string section)
    {
        if (config[section] is JsonObject obj)
        {
            return obj;
        }

        obj = new JsonObject();
        config[section] = obj;
        return obj;
    }

    /// <summary>
    /// KEYCASE_01 — reads a section entry by element key, case-insensitively.
    ///
    /// RoleByKey is an OrdinalIgnoreCase dictionary, so everywhere else in this window "Loot" and
    /// "loot" are one role. System.Text.Json.Nodes.JsonObject, however, indexes ORDINALLY: a config
    /// written by an older build, hand-edited, or produced on a case-preserving path could hold
    /// "Loot" while the role table offers "loot", and every `section[role.Key]` read then returned
    /// null. The element looked absent on load and was re-created as a second entry on save, so the
    /// exporter drew one of them and the user edited the other.
    /// </summary>
    internal static JsonNode? ReadSectionNode(JsonObject section, string key)
    {
        if (section.TryGetPropertyValue(key, out JsonNode? exact))
        {
            return exact;
        }

        foreach (KeyValuePair<string, JsonNode?> pair in section)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// KEYCASE_01 — writes a section entry under <paramref name="key"/> and removes every
    /// case-variant of it, so a save can never leave two spellings of the same element behind.
    /// </summary>
    internal static void WriteSectionNode(JsonObject section, string key, JsonNode? value)
    {
        List<string> variants = section
            .Where(pair => !string.Equals(pair.Key, key, StringComparison.Ordinal)
                        && string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .ToList();

        foreach (string variant in variants)
        {
            section.Remove(variant);
            RuntimeLog.Info("CROP", $"  Removed duplicate config key '{variant}' (same element as '{key}').");
        }

        section[key] = value;
    }

    internal static int ReadInt(JsonNode? node, int fallback)
    {
        try
        {
            return node?.GetValue<int>() ?? fallback;
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("CROP", $"JSON int parse fallback to {fallback}: {ex.Message}");
            return fallback;
        }
    }

    internal static double ReadDouble(JsonNode? node, double fallback)
    {
        try
        {
            return node?.GetValue<double>() ?? fallback;
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("CROP", $"JSON double parse fallback to {fallback}: {ex.Message}");
            return fallback;
        }
    }

    internal static Frac ReadFrac(JsonNode? node, Frac fallback)
    {
        try
        {
            if (node == null) return fallback;
            if (node.AsValue().TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
                return Frac.FromString(s);
            if (node.AsValue().TryGetValue(out double d))
                return Frac.FromDouble(d);
            return fallback;
        }
        catch (System.Exception swallowed)
        {
            global::FortniteVideoSoftware.App.RuntimeLog.Swallowed(swallowed);   // FAULTTIER_02 — no failure is silent.
            return fallback;
        }
    }
}
