
using System;
using System.Text.Json.Nodes;

namespace FreeVideoStudio.Core.Project;

/// <summary>
/// RECOVERYDOC_01 — THE CRASH-RECOVERY ENVELOPE: ONE <see cref="ProjectDocument"/> PLUS
/// CRASH-ONLY METADATA, AND NOTHING ELSE.
///
/// <para>
/// WHY THIS EXISTS — THERE MUST BE NO SECOND PROJECT SCHEMA. Until this type the recovery file was
/// written by a hand-rolled 60-field payload (<c>ProjectRecoveryService.SerializeState</c>) that
/// duplicated the project surface. Every new project feature therefore needed TWO serialisations:
/// the canonical <see cref="ProjectDocument"/>/<see cref="ProjectSerializer"/>, and a parallel
/// recovery copy that was forgotten the moment the next feature landed. A crash then "recovered"
/// the user's work while silently dropping everything the recovery schema never grew.
///
/// The fix is structural, not cosmetic: the recovery payload contains the CANONICAL document,
/// serialised by the CANONICAL <see cref="ProjectSerializer"/>. A field that can be saved can be
/// recovered, because they are the same field — by construction, not by discipline.
///
/// THIS TYPE MUST NEVER LIST PROJECT FIELDS. It carries exactly one embedded
/// <see cref="ProjectDocument"/> plus a <c>transient</c> object for crash-only state that
/// deliberately does NOT belong in a saved project. If you are tempted to add "baseSpeed" or
/// "cuts" or "mask" here, stop: they already live in the document inside <c>project</c>.
/// </para>
///
/// <para>
/// NO REFLECTION (PROJ_02). NativeAOT + TrimMode=full: this file touches nothing but
/// <see cref="JsonNode"/>/JsonObject, exactly like <see cref="ProjectSerializer"/>.
/// </para>
/// </summary>
public static class RecoveryEnvelope
{
    /// <summary>Magic string so a wrong-type recovery file is rejected by name, not by crash.</summary>
    public const string FormatId = "fvsrecovery";

    /// <summary>
    /// RECOVERYDOC_02 — the envelope version this build WRITES. Bump only when the ENVELOPE shape
    /// changes; the embedded document carries its own <see cref="ProjectDocument.SchemaVersion"/>
    /// and is validated by <see cref="ProjectSerializer.Read"/> independently.
    /// </summary>
    public const int CurrentFormatVersion = 1;

    private const string KeyFormat = "recovery_format";
    private const string KeyVersion = "recovery_format_version";
    private const string KeySavedAt = "saved_at_utc";
    private const string KeyProject = "project";
    private const string KeyTransient = "transient";

    /// <summary>
    /// Builds the canonical recovery payload.
    ///
    /// <para>
    /// WHAT MAY GO INTO <paramref name="transientMetadata"/>: ONLY state that must survive a
    /// crash but must NOT be saved with a normal project, because it describes THIS machine or
    /// THIS session rather than the user's work — the live preview volume, the thumbnail frame,
    /// the freeze preview position, HUD tool selections mid-flight, voice-over takes metadata, the
    /// in-flight granular editor session. Each of these is a pointer or a monitoring level, and
    /// docs/06_PROJECT_DOCUMENT_MODEL.md (PROJ_06 / North Star #3) already rules them out of the
    /// document. Anything the user BUILT goes in the document, not here.
    /// </para>
    /// </summary>
    public static JsonObject Write(ProjectDocument document, DateTimeOffset savedAtUtc, JsonObject? transientMetadata)
    {
        ArgumentNullException.ThrowIfNull(document);

        JsonObject root = new()
        {
            [KeyFormat] = FormatId,
            [KeyVersion] = CurrentFormatVersion,
            [KeySavedAt] = savedAtUtc.ToUnixTimeSeconds(),
            [KeyProject] = ProjectSerializer.Write(document),
        };

        if (transientMetadata is { Count: > 0 })
            root[KeyTransient] = transientMetadata.DeepClone();

        return root;
    }

    /// <summary>True when the root object is a canonical envelope written by this or a newer build.</summary>
    public static bool IsEnvelope(JsonObject root)
        => root[KeyFormat] is JsonValue v
        && v.TryGetValue(out string? s)
        && string.Equals(s, FormatId, StringComparison.Ordinal);

    /// <summary>
    /// Parses an envelope. Returns false and sets <paramref name="error"/> when the payload is not
    /// a recovery envelope this build can honour — including one written by a FUTURE build, whose
    /// envelope shape this build may misinterpret (the same refusal rule
    /// <see cref="ProjectSerializer.Read"/> applies to documents).
    /// The document passes through every document-level protection unchanged:
    /// schema minimum/maximum validation, unknown-field preservation, forgiving field reads.
    /// </summary>
    public static bool TryRead(
        JsonObject root,
        out ProjectDocument? document,
        out JsonObject? transientMetadata,
        out string? error)
    {
        document = null;
        transientMetadata = null;
        error = null;

        if (!IsEnvelope(root))
        {
            error = "The recovery payload is not a canonical recovery envelope.";
            return false;
        }

        long version = ReadLong(root, KeyVersion, 0);
        if (version > CurrentFormatVersion)
        {
            error = $"This recovery file was written by a newer build (envelope version {version}, "
                  + $"this build writes {CurrentFormatVersion}). Update the app to restore it.";
            return false;
        }
        if (version < 1)
        {
            error = $"This recovery file uses a retired envelope format (version {version}).";
            return false;
        }

        if (root[KeyProject] is not JsonObject projectNode)
        {
            error = "The recovery envelope carried no project document.";
            return false;
        }

        document = ProjectSerializer.Read(projectNode, out error);
        if (document is null) return false;

        if (root[KeyTransient] is JsonObject t) transientMetadata = t;
        return true;
    }

    /// <summary>
    /// RECOVERYDOC_03 — "is there a project here a user would be upset to lose?", answered from the
    /// CANONICAL document. An uploaded clip with no edits (a default document with no source) must
    /// not produce a recovery file, a recovery prompt, or a restore — the same rule
    /// HasUnsavedWork applies to the live session. Callers keep their richer live-state checks;
    /// this is the document-level backstop so an empty capture can never be persisted.
    /// </summary>
    public static bool HasContent(ProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return !string.IsNullOrWhiteSpace(document.Source.FilePath);
    }

    private static long ReadLong(JsonObject o, string key, long fallback)
    {
        if (o[key] is not JsonValue v) return fallback;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out string? s) && long.TryParse(s, out long parsed)) return parsed;
        return fallback;
    }
}
