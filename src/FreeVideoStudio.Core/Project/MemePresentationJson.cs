// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/06_PROJECT_DOCUMENT_MODEL.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.Core.Project;

/// <summary>
/// MEMEMODE_01 — the ONE JSON mapping of a meme's presentation (mode, corner, size, sound), used by
/// the project file, the crash-recovery document (both through <see cref="ProjectSerializer"/>) and
/// the editors' own session JSON. Hand-written, no reflection (PROJ_02).
///
/// <para><b>Backward compatibility.</b> Every key is optional. A meme written before MEMEMODE_01 has
/// none of them and reads back as a full-screen cutaway with sound — exactly what it always was. An
/// unknown or malformed value falls back to the same defaults rather than failing the load
/// (06 PROJ-SCHEMA: the reader is forgiving).</para>
/// </summary>
public static class MemePresentationJson
{
    public const string KeyMode = "mode";
    public const string KeyCorner = "corner";
    public const string KeySize = "size";
    public const string KeySound = "sound";

    /// <summary>Writes the four keys onto a meme object.</summary>
    public static void Write(JsonObject o, MemePlacement m)
    {
        o[KeyMode] = ModeText(m.Mode);
        o[KeyCorner] = CornerText(m.Corner);
        o[KeySize] = SizeText(m.Size);
        o[KeySound] = m.PlaySound;
    }

    /// <summary>Applies the four keys (when present) to a placement read without them.</summary>
    public static MemePlacement Apply(JsonObject? o, MemePlacement m)
    {
        if (o == null) return m;
        return m with
        {
            Mode = ParseMode(Text(o[KeyMode])),
            Corner = ParseCorner(Text(o[KeyCorner])),
            Size = ParseSize(Text(o[KeySize])),
            PlaySound = Bool(o[KeySound], true),
        };
    }

    public static string ModeText(MemePresentationMode v) => v == MemePresentationMode.CornerOverlay ? "corner" : "inline";

    public static string CornerText(MemeOverlayCorner v) => v switch
    {
        MemeOverlayCorner.TopLeft => "top_left",
        MemeOverlayCorner.TopRight => "top_right",
        MemeOverlayCorner.BottomLeft => "bottom_left",
        _ => "bottom_right",
    };

    public static string SizeText(MemeOverlaySize v) => v switch
    {
        MemeOverlaySize.Small => "small",
        MemeOverlaySize.Large => "large",
        _ => "medium",
    };

    public static MemePresentationMode ParseMode(string? s) =>
        string.Equals(s, "corner", StringComparison.OrdinalIgnoreCase) ? MemePresentationMode.CornerOverlay : MemePresentationMode.InlineFullScreen;

    public static MemeOverlayCorner ParseCorner(string? s) => s?.ToLowerInvariant() switch
    {
        "top_left" => MemeOverlayCorner.TopLeft,
        "top_right" => MemeOverlayCorner.TopRight,
        "bottom_left" => MemeOverlayCorner.BottomLeft,
        _ => MemeOverlayCorner.BottomRight,
    };

    public static MemeOverlaySize ParseSize(string? s) => s?.ToLowerInvariant() switch
    {
        "small" => MemeOverlaySize.Small,
        "large" => MemeOverlaySize.Large,
        _ => MemeOverlaySize.Medium,
    };

    // JsonValue.TryGetValue never throws on a type mismatch; it returns false.
    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static bool Bool(JsonNode? n, bool fallback)
    {
        if (n is not JsonValue v) return fallback;
        if (v.TryGetValue(out bool b)) return b;
        if (v.TryGetValue(out string? s) && bool.TryParse(s, out bool parsed)) return parsed;
        return fallback;
    }
}
