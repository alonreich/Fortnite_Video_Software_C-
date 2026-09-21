// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/07_UNDO_AND_HISTORY.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Project;

namespace FortniteVideoSoftware.Core.Undo;

/// <summary>
/// UNDO_24 — THE SIDECAR. EDIT HISTORY THAT SURVIVES CLOSING THE APPLICATION.
///
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// <b>WHAT THIS CLOSES.</b> <c>07_UNDO_AND_HISTORY.md</c> §4 describes this store and §5 lists it
/// as item 1 of the work not yet done. Until now <c>UndoStack&lt;T&gt;</c> held everything in
/// memory, so quitting the application discarded every step of how a montage was built. A user who
/// closed the app, reopened their project and pressed Ctrl+Z got nothing — and got no explanation
/// either, because there was no history to say "there is none".
/// </para>
///
/// <para>
/// ⚠️ <b>HISTORY IS NOT PART OF THE DOCUMENT, AND THIS IS NOT A STYLE CHOICE.</b> §4 is explicit:
/// history is per-machine, disposable, and would bloat a file meant to be portable. Sending a
/// colleague a montage must not send them forty snapshots of how it was made — and worse, a
/// <c>.fvsproj</c> carrying history would make the file grow without bound across sessions, on a
/// format whose whole appeal is that it is small enough to email. So it lives BESIDE the project,
/// keyed to it, and its absence is never an error.
/// </para>
///
/// <para>
/// <b>EVERY FAILURE HERE IS SILENT-BY-DESIGN AND THAT IS DELIBERATE, ONCE.</b> This is the one
/// place in the suite where a read failure genuinely leaves the user's outcome unchanged: they get
/// an empty history, which is exactly what they had before this class existed. Writing it through
/// <c>IFaultSink</c> as Degraded would produce a notice saying "your undo history could not be
/// loaded" for a feature the user has never been able to rely on, every time a sidecar is missing —
/// which is most of the time. The CALLER decides whether an empty result is worth mentioning;
/// this class reports the fact and does not editorialise.
/// </para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class UndoSidecarStore
{
    /// <summary>
    /// UNDO_24 — the format this build writes. Bumped when the meaning of a stored field changes.
    /// A sidecar from a different schema is DISCARDED rather than migrated: it is disposable data
    /// and a wrong migration would silently hand the user someone else's edits.
    /// </summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// UNDO_24 — the ceiling, mirroring U2. Enforced on WRITE as well as on restore, because a
    /// file is the one place a stack can outlive the in-memory cap that was supposed to bound it.
    /// </summary>
    public const int MaxEntries = UndoStack<ProjectDocument>.DefaultMaxDepth;

    private const string FolderName = "History";

    private readonly string _folder;

    /// <param name="historyFolder">
    /// Where sidecars live. Under ProgramData, never beside the <c>.fvsproj</c>: the project may
    /// sit on a network share, a USB stick or a read-only folder, and per-machine disposable data
    /// has no business being written there.
    /// </param>
    public UndoSidecarStore(string historyFolder)
    {
        _folder = historyFolder ?? throw new ArgumentNullException(nameof(historyFolder));
    }

    /// <summary>Builds a store rooted under the application's ProgramData directory.</summary>
    public static UndoSidecarStore CreateDefault(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new UndoSidecarStore(Path.Combine(paths.ProgramDataRoot, FolderName));
    }

    /// <summary>
    /// UNDO_24 — the sidecar path for a project.
    ///
    /// <para>
    /// ⚠️ Named by a HASH of the project's full path, not by its file name. Two projects called
    /// <c>montage.fvsproj</c> in two folders are two projects, and sharing one history between them
    /// would hand a user edits they never made. The hash also sidesteps every path-length and
    /// illegal-character question in one step.
    /// </para>
    /// </summary>
    public string PathFor(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        string normalised = Path.GetFullPath(projectPath).ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Path.Combine(_folder, Convert.ToHexString(hash, 0, 12) + ".history.json");
    }

    /// <summary>
    /// UNDO_24 — writes the history beside the project.
    ///
    /// <para>
    /// <paramref name="sourceFingerprint"/> is stored and checked on load. A project edited by
    /// another machine, or restored from a backup, has a history that no longer describes it —
    /// replaying those snapshots would silently revert the user to a document that never existed
    /// on this timeline. Cheaper to discard than to be subtly wrong.
    /// </para>
    /// </summary>
    /// <returns><see langword="true"/> when the file was written.</returns>
    public bool Save(string projectPath, string sourceFingerprint, UndoStack<ProjectDocument> history)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(history);

        try
        {
            Directory.CreateDirectory(_folder);

            JsonObject root = new()
            {
                ["schema_version"] = SchemaVersion,
                ["project_path"] = Path.GetFullPath(projectPath),
                ["source_fingerprint"] = sourceFingerprint ?? string.Empty,
                ["saved_utc"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["undo"] = WriteEntries(history.UndoEntries),
                ["redo"] = WriteEntries(history.RedoEntries),
            };

            // The same atomic write the project itself uses (05 §4c SYS-ATOMICWRITE). A half-written
            // sidecar is worse than none: it parses far enough to look like history and then hands
            // back truncated snapshots.
            AtomicJsonFile.WriteObject(PathFor(projectPath), root);
            return true;
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("UNDO", $"Could not write the history sidecar: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// UNDO_24 — reads the history back, or returns <see langword="null"/> when there is none to
    /// read, when it belongs to a different version of the project, or when it cannot be parsed.
    ///
    /// <para>
    /// ⚠️ ALL THREE OF THOSE ARE THE SAME ANSWER ON PURPOSE: <i>start with an empty history</i>.
    /// A user cannot act on "your sidecar is schema 2", and the recovery from every one of them is
    /// identical. Distinguishing them in the return type would only tempt a caller into handling
    /// them differently, and there is no different handling to do.
    /// </para>
    /// </summary>
    public UndoSidecar? Load(string projectPath, string expectedSourceFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        try
        {
            string path = PathFor(projectPath);
            if (!File.Exists(path)) return null;

            JsonObject? root = AtomicJsonFile.ReadObject(path);
            if (root is null) return null;

            if (root["schema_version"] is not JsonValue sv || !sv.TryGetValue(out int schema) || schema != SchemaVersion)
            {
                CoreLogger.Debug("UNDO", "History sidecar is from a different schema; starting a fresh history.");
                return null;
            }

            string stored = root["source_fingerprint"]?.GetValue<string>() ?? string.Empty;
            if (!string.Equals(stored, expectedSourceFingerprint ?? string.Empty, StringComparison.Ordinal))
            {
                CoreLogger.Debug("UNDO",
                    "History sidecar does not match this project's current state; starting a fresh history.");
                return null;
            }

            IReadOnlyList<UndoEntry<ProjectDocument>> undo = ReadEntries(root["undo"] as JsonArray);
            IReadOnlyList<UndoEntry<ProjectDocument>> redo = ReadEntries(root["redo"] as JsonArray);

            if (undo.Count == 0 && redo.Count == 0) return null;

            return new UndoSidecar(undo, redo);
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("UNDO", $"Could not read the history sidecar: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// UNDO_24 — removes the sidecar. Called when a project is deleted or explicitly reset, so a
    /// stale history cannot be matched to a later file that happens to reuse the path.
    /// </summary>
    public void Delete(string projectPath)
    {
        try
        {
            string path = PathFor(projectPath);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("UNDO", $"Could not delete the history sidecar: {ex.Message}");
        }
    }

    // ── serialisation ───────────────────────────────────────────────────────────────────────

    private static JsonArray WriteEntries(IReadOnlyList<UndoEntry<ProjectDocument>> entries)
    {
        JsonArray array = new();

        // U2 — the cap applies to the FILE as well. Keep the NEWEST entries: a history trimmed
        // from the oldest end is what UndoStack.Restore expects and is what the user reaches first.
        int skip = Math.Max(0, entries.Count - MaxEntries);

        for (int i = skip; i < entries.Count; i++)
        {
            // AOTSAFETY_06 — typed JsonNode so this binds to JsonArray.Add(JsonNode?) rather than
            // the generic Add<T>, which carries RequiresUnreferencedCode / RequiresDynamicCode.
            JsonNode entry = new JsonObject
            {
                ["label"] = entries[i].Label,
                ["at_ms"] = entries[i].AtUnixMs,
                ["state"] = ProjectSerializer.Write(entries[i].State),
            };
            array.Add(entry);
        }

        return array;
    }

    private static IReadOnlyList<UndoEntry<ProjectDocument>> ReadEntries(JsonArray? array)
    {
        List<UndoEntry<ProjectDocument>> entries = new();
        if (array is null) return entries;

        foreach (JsonNode? node in array)
        {
            if (node is not JsonObject o) continue;
            if (o["state"] is not JsonObject stateObj) continue;

            ProjectDocument? doc = ProjectSerializer.Read(stateObj, out string? error);

            // A single unreadable entry does not discard the rest. The stack is a list of
            // independent states, not a chain — dropping one loses that step and keeps the others,
            // which is strictly better than throwing the session's whole history away.
            if (doc is null)
            {
                CoreLogger.Debug("UNDO", $"Skipping an unreadable history entry: {error}");
                continue;
            }

            long atMs = o["at_ms"] is JsonValue v && v.TryGetValue(out long parsed) ? parsed : 0;
            entries.Add(new UndoEntry<ProjectDocument>(doc, o["label"]?.GetValue<string>() ?? "edit", atMs));
        }

        return entries;
    }
}

/// <summary>UNDO_24 — a history read back from disk, ready for <see cref="UndoStack{T}.Restore"/>.</summary>
public sealed record UndoSidecar(
    IReadOnlyList<UndoEntry<ProjectDocument>> Undo,
    IReadOnlyList<UndoEntry<ProjectDocument>> Redo);
