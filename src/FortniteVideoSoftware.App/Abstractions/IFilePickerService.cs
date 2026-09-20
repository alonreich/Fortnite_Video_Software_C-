// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.

namespace FortniteVideoSoftware.App.Abstractions;

/// <summary>
/// SEAM_04 — the OS file dialogs, behind an interface.
///
/// <para>
/// Needed by Phase 1: "Save Project" and "Open Project" are the two commands that make the
/// <c>.fvsproj</c> document reachable by a user, and a command that can only be exercised by a
/// human clicking through a native dialog cannot be regression-tested. The fake used in tests
/// returns a temp path; the real one shows the picker.
/// </para>
///
/// <para>
/// <b>Directory memory is part of the contract</b> (<c>05_SYSTEM_LIFECYCLE_STORAGE.md</c> §3,
/// SYS-WINSTATE): the chosen folder is written to configuration immediately on selection, even if
/// the dialog is subsequently cancelled. Implementations own that write so no call site can forget
/// it — forgetting it is what makes a file picker open in the wrong folder every single time.
/// </para>
/// </summary>
public interface IFilePickerService
{
    /// <summary>Ask the user where to write a file. Returns null when cancelled.</summary>
    Task<string?> SaveFileAsync(FilePickerRequest request);

    /// <summary>Ask the user to choose an existing file. Returns null when cancelled.</summary>
    Task<string?> OpenFileAsync(FilePickerRequest request);
}

/// <param name="Title">Dialog caption.</param>
/// <param name="SuggestedFileName">Pre-filled name, extension included.</param>
/// <param name="ExtensionLabel">Human-readable filter name, e.g. "Clip Studio project".</param>
/// <param name="Extension">Extension without the dot, e.g. "fvsproj".</param>
/// <param name="StartDirectoryKey">
/// The settings key whose remembered folder opens the dialog, and which is rewritten on selection.
/// Null opts out of directory memory.
/// </param>
public sealed record FilePickerRequest(
    string Title,
    string? SuggestedFileName,
    string ExtensionLabel,
    string Extension,
    string? StartDirectoryKey = null);
