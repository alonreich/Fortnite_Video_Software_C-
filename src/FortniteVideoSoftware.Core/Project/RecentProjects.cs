// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/06_PROJECT_DOCUMENT_MODEL.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Project;

/// <summary>One entry in the recent list. <paramref name="Exists"/> is resolved at read time, not stored.</summary>
public readonly record struct RecentProject(string Path, string Title, long OpenedUtcSeconds, bool Exists);

/// <summary>
/// PROJ_09 — THE RECENT PROJECTS LIST.
///
/// <para>
/// Saving work is only half of the feature. A user who can save but must then hunt through Explorer
/// for the file has been given a filing chore, not a document model. The recent list is what makes
/// "carry on where I left off" a single click, and it is the surface the main window's empty state
/// should be built around.
/// </para>
///
/// <para>
/// ⚠️ MISSING FILES ARE SHOWN, NOT SILENTLY DROPPED. A project on an external drive that is not
/// plugged in is not a project the user deleted. Entries carry <see cref="RecentProject.Exists"/>
/// so the UI can grey them out and explain, and <see cref="Prune"/> removes an entry only when the
/// user asks. Quietly deleting the row is how a user concludes the app "lost" their montage.
/// </para>
///
/// <para>⚠️ Synchronous disk I/O — never call from the Avalonia dispatcher (North Star #6).</para>
/// </summary>
public sealed class RecentProjects
{
    /// <summary>
    /// Ten is the number of projects a person can actually recognise in a menu. A longer list stops
    /// being a shortcut and becomes a second file browser with worse sorting.
    /// </summary>
    public const int MaxEntries = 10;

    private readonly string _storePath;

    public RecentProjects(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _storePath = Path.Combine(paths.ProgramDataRoot, "recent_projects.json");
    }

    /// <summary>Test seam: point the list at an arbitrary file.</summary>
    public RecentProjects(string storePath)
    {
        _storePath = storePath ?? throw new ArgumentNullException(nameof(storePath));
    }

    public IReadOnlyList<RecentProject> Read()
    {
        List<RecentProject> result = new();
        try
        {
            JsonObject? root = AtomicJsonFile.ReadObject(_storePath);
            if (root?["entries"] is not JsonArray array) return result;

            foreach (JsonNode? node in array)
            {
                if (node is not JsonObject o) continue;
                string? path = o["path"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(path)) continue;

                string title = o["title"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(path);
                long opened = o["opened_utc_seconds"] is JsonValue v && v.TryGetValue(out long l) ? l : 0;

                bool exists;
                try { exists = File.Exists(path); }
                catch (Exception ex) { CoreLogger.Swallowed(ex); exists = false; }

                result.Add(new RecentProject(path!, title, opened, exists));
                if (result.Count >= MaxEntries) break;
            }
        }
        catch (Exception ex)
        {
            // A damaged recent list is a cosmetic problem. It must never stop the app from starting,
            // and it must never surface as an error the user has to dismiss on every launch.
            CoreLogger.Swallowed(ex);
        }

        return result;
    }

    /// <summary>
    /// Moves <paramref name="path"/> to the top of the list, de-duplicating case-insensitively
    /// (Windows paths differing only in case are the same file, and showing both is a bug the user
    /// reads as the app being confused).
    /// </summary>
    public void Touch(string path, string title)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            List<RecentProject> existing = new(Read());
            existing.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            existing.Insert(0, new RecentProject(path, title, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true));
            if (existing.Count > MaxEntries) existing.RemoveRange(MaxEntries, existing.Count - MaxEntries);
            WriteAll(existing);
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
        }
    }

    /// <summary>Removes one entry. Called when the USER dismisses a row, never automatically.</summary>
    public void Prune(string path)
    {
        try
        {
            List<RecentProject> existing = new(Read());
            if (existing.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)) == 0) return;
            WriteAll(existing);
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
        }
    }

    private void WriteAll(List<RecentProject> entries)
    {
        JsonArray array = new();
        foreach (RecentProject e in entries)
        {
            array.AddNode(new JsonObject   // AOTSAFETY_02
            {
                ["path"] = e.Path,
                ["title"] = e.Title,
                ["opened_utc_seconds"] = e.OpenedUtcSeconds,
            });
        }

        AtomicJsonFile.WriteObject(_storePath, new JsonObject { ["entries"] = array });
    }
}
