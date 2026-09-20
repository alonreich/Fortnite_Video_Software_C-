// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/06_PROJECT_DOCUMENT_MODEL.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using FortniteVideoSoftware.Core.Project;

namespace FortniteVideoSoftware.Core.Abstractions;

/// <summary>
/// SEAM_01 / PROJ_08 — reading and writing <c>.fvsproj</c>, behind an interface.
///
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// <b>WHY AN INTERFACE OVER A PERFECTLY GOOD STATIC CLASS.</b> <c>ProjectStore</c> is a static
/// class that touches the real filesystem. Every view-model that needs to save therefore drags the
/// disk into its own tests, and "what does the UI do when the save fails" — the case that actually
/// matters, because it is the one the user must be told about — becomes untestable without
/// contriving a read-only directory.
///
/// The implementation (<c>FileProjectStore</c>) is a thin forward to the existing static methods.
/// The atomic-write protocol, the <c>.bak</c> cascade and the amputation rule all stay exactly
/// where they are, in <c>ProjectStore</c>/<c>ProjectSerializer</c>, governed by spec 06. This
/// interface adds a seam; it does not add a second implementation of the protocol, and it must
/// never be allowed to grow one (<c>05_SYSTEM_LIFECYCLE_STORAGE.md</c> §4c: "Never reimplement this
/// sequence at a call site").
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </para>
/// </summary>
public interface IProjectStore
{
    /// <summary>Atomically write <paramref name="document"/> to <paramref name="path"/>, rotating the backup.</summary>
    ProjectIoResult Save(ProjectDocument document, string path);

    /// <summary>
    /// Read a project. Returns null on failure with <paramref name="error"/> populated;
    /// <paramref name="loadedFromBackup"/> reports that the live file was damaged and the
    /// <c>.bak</c> was used, which the caller MUST surface — silently loading an older version of
    /// the user's work is its own defect.
    /// </summary>
    ProjectDocument? Load(string path, out string? error, out bool loadedFromBackup);

    /// <summary>Force the <c>.fvsproj</c> extension onto a user-chosen path.</summary>
    string NormalizeExtension(string path);
}

/// <summary>
/// The shipping implementation: a forward to <see cref="ProjectStore"/>. Deliberately contains no
/// logic of its own — see the warning on <see cref="IProjectStore"/>.
/// </summary>
public sealed class FileProjectStore : IProjectStore
{
    public static readonly FileProjectStore Instance = new();

    public ProjectIoResult Save(ProjectDocument document, string path)
        => ProjectStore.Save(document, path);

    public ProjectDocument? Load(string path, out string? error, out bool loadedFromBackup)
        => ProjectStore.Load(path, out error, out loadedFromBackup);

    public string NormalizeExtension(string path)
        => ProjectStore.NormalizeExtension(path);
}
