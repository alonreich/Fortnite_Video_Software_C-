// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/06_PROJECT_DOCUMENT_MODEL.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.IO;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Project;

/// <summary>PROJ_08 — the outcome of a save or a load, in a form the UI can show without guessing.</summary>
public readonly record struct ProjectIoResult(bool Success, string? Path, string? Error)
{
    public static ProjectIoResult Ok(string path) => new(true, path, null);
    public static ProjectIoResult Fail(string message) => new(false, null, message);
}

/// <summary>
/// PROJ_08 — READS AND WRITES <c>.fvsproj</c> FILES ON DISK.
///
/// <para>
/// ⚠️ EVERY WRITE GOES THROUGH <see cref="AtomicJsonFile"/>. This is not a style preference; it is
/// <c>docs/05_SYSTEM_LIFECYCLE_STORAGE.md#SYS-ATOMICWRITE</c>, which states that atomic persistence
/// is "not optional, and it is not per-caller". A project file is the single artefact in this
/// product whose corruption costs the user work that cannot be regenerated, so the GUID-temp,
/// flush-to-platter, atomic-rename protocol matters here more than anywhere else it is already
/// used. Never replace this with <c>File.WriteAllText</c>.
/// </para>
///
/// <para>
/// ⚠️ ONE GENERATION OF BACKUP IS KEPT. Before a successful overwrite the previous file becomes
/// <c>&lt;name&gt;.fvsproj.bak</c>. The crop configuration keeps a five-tier cascade
/// (SYS-RECOVERY) because it is machine state the user never sees and cannot re-author. A project
/// is different: the user knows what they saved, and a folder littered with five numbered backups
/// of every montage is its own kind of damage. One generation buys back the single mistake that
/// actually happens — saving over the wrong project — without turning their output folder into a
/// graveyard.
/// </para>
///
/// <para>
/// ⚠️ THREADING. These calls perform synchronous disk I/O and MUST NOT be invoked on the Avalonia
/// dispatcher (North Star #6). Callers await them on a worker via <c>Task.Run</c> and marshal only
/// the resulting <see cref="ProjectIoResult"/> back to the UI thread.
/// </para>
/// </summary>
public static class ProjectStore
{
    public const string BackupSuffix = ".bak";

    /// <summary>
    /// PROJ_08 — writes the document to <paramref name="path"/>.
    /// <para>
    /// Failure is returned, never thrown. A save that throws out of a window's close handler is how
    /// an application loses the very work the user was trying to protect; the caller needs a value
    /// it can render into "could not save, here is why, your edits are still open".
    /// </para>
    /// </summary>
    public static ProjectIoResult Save(ProjectDocument document, string path)
    {
        if (document == null) return ProjectIoResult.Fail("There is no project to save.");
        if (string.IsNullOrWhiteSpace(path)) return ProjectIoResult.Fail("No file name was given.");

        try
        {
            path = NormalizeExtension(path);

            // The backup is taken from the file that is ON DISK RIGHT NOW, before the new bytes are
            // written. Taking it afterwards would back up the save that just happened, which is
            // worth nothing at the exact moment it is needed.
            TryBackup(path);

            JsonObject payload = ProjectSerializer.Write(document);
            AtomicJsonFile.WriteObject(path, payload);

            CoreLogger.Info("PROJECT", $"Saved '{Path.GetFileName(path)}'.");
            return ProjectIoResult.Ok(path);
        }
        catch (UnauthorizedAccessException swallowed)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed);   // FAULTTIER_02 — no failure is silent.
            return ProjectIoResult.Fail("Windows would not let the app write to that folder. Try a folder inside your Documents.");
        }
        catch (DirectoryNotFoundException swallowed3)
        {
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed3);   // FAULTTIER_02 — no failure is silent.
            return ProjectIoResult.Fail("That folder no longer exists.");
        }
        catch (IOException ex)
        {
            CoreLogger.Fail("PROJECT", $"Save failed: {ex.Message}");
            return ProjectIoResult.Fail($"The project could not be written: {ex.Message}");
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("PROJECT", $"Save failed: {ex}");
            return ProjectIoResult.Fail("The project could not be saved. The log has the details.");
        }
    }

    /// <summary>
    /// PROJ_08 — loads a document, falling back to the backup when the live file is unreadable.
    /// <para>
    /// The fallback is silent in the sense that it does not fail, but it is NOT silent to the user:
    /// <paramref name="loadedFromBackup"/> is set so the caller can say so. A user who is shown
    /// yesterday's version without being told will keep editing and re-save over the good copy.
    /// </para>
    /// </summary>
    public static ProjectDocument? Load(string path, out string? error, out bool loadedFromBackup)
    {
        error = null;
        loadedFromBackup = false;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "No file was chosen.";
            return null;
        }

        if (!File.Exists(path))
        {
            error = "That project file no longer exists.";
            return null;
        }

        ProjectDocument? doc = TryReadOne(path, out error);
        if (doc != null) return doc;

        string backup = path + BackupSuffix;
        if (!File.Exists(backup)) return null;

        // The live file is damaged. The previous generation is the only remaining copy of this
        // work, so it is tried before giving up — but the original error is replaced only if the
        // backup actually parses, so a user with two broken files still sees the real reason.
        ProjectDocument? fromBackup = TryReadOne(backup, out string? backupError);
        if (fromBackup == null)
        {
            CoreLogger.Fail("PROJECT", $"Backup also unreadable: {backupError}");
            return null;
        }

        loadedFromBackup = true;
        error = null;
        CoreLogger.Info("PROJECT", $"Live file damaged; recovered '{Path.GetFileName(path)}' from backup.");
        return fromBackup;
    }

    private static ProjectDocument? TryReadOne(string path, out string? error)
    {
        error = null;
        try
        {
            JsonObject? root = AtomicJsonFile.ReadObject(path);
            if (root == null)
            {
                error = "That project file is empty or damaged.";
                return null;
            }

            return ProjectSerializer.Read(root, out error);
        }
        catch (UnauthorizedAccessException swallowed2)
        {
            error = "Windows would not let the app read that file.";
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(swallowed2);   // FAULTTIER_02 — no failure is silent.
            return null;
        }
        catch (IOException ex)
        {
            error = $"The project could not be read: {ex.Message}";
            global::FortniteVideoSoftware.Core.Infrastructure.CoreLogger.Swallowed(ex);   // FAULTTIER_02 — no failure is silent.
            return null;
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("PROJECT", $"Load failed: {ex}");
            error = "The project could not be opened. The log has the details.";
            return null;
        }
    }

    /// <summary>
    /// Guarantees the path ends in <see cref="ProjectDocument.FileExtension"/>. A file picker that
    /// returns a bare name would otherwise produce a project the shell cannot associate and the
    /// open dialog's filter will not show, which reads to the user as "my save vanished".
    /// </summary>
    public static string NormalizeExtension(string path)
    {
        if (path.EndsWith(ProjectDocument.FileExtension, StringComparison.OrdinalIgnoreCase)) return path;
        return path + ProjectDocument.FileExtension;
    }

    private static void TryBackup(string path)
    {
        try
        {
            if (!File.Exists(path)) return;

            // A zero-length live file is the residue of an interrupted write. Promoting it over a
            // good backup would destroy the last intact copy, so it is left alone.
            if (new FileInfo(path).Length == 0) return;

            File.Copy(path, path + BackupSuffix, overwrite: true);
        }
        catch (Exception ex)
        {
            // A failed backup must never block a save. The user asked to save; losing the previous
            // generation is strictly better than losing the work in front of them.
            CoreLogger.Swallowed(ex);
        }
    }
}
