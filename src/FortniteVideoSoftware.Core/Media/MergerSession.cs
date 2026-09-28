// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace FortniteVideoSoftware.Core.Media;

// ══════════════════════════════════════════════════════════════════════════════════════════════
// MERGESESSION_01 — THE MERGER'S LIVE STATE ↔ ITS EDIT LIST (Video-Merger-Migration.md P3.2).
//
// The window still keeps its state the old way (an ObservableCollection<string>, a thumbnail path,
// a MusicWizardResult). These pure helpers turn that state into a MergeEdl for autosave, the
// project document and undo, and turn a saved MergeEdl back into what the window must restore.
// Everything here is pure so it is unit-tested without a window, mpv or a disk.
// ══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// One stable <see cref="Guid"/> per queue row, kept in step with the queue's collection events.
/// The queue holds plain paths and may hold the same file twice, so a path is not an identity.
/// </summary>
public sealed class ClipIdList
{
    private readonly List<Guid> _ids = new();

    public IReadOnlyList<Guid> Ids => _ids;
    public int Count => _ids.Count;

    public void Insert(int index, int count)
    {
        index = Math.Clamp(index, 0, _ids.Count);
        for (int i = 0; i < count; i++) _ids.Insert(index + i, Guid.NewGuid());
    }

    public void Remove(int index, int count)
    {
        if (index < 0 || index >= _ids.Count) return;
        _ids.RemoveRange(index, Math.Min(count, _ids.Count - index));
    }

    public void Move(int from, int to, int count)
    {
        if (from < 0 || count <= 0 || from + count > _ids.Count) return;
        var moved = _ids.GetRange(from, count);
        _ids.RemoveRange(from, count);
        _ids.InsertRange(Math.Clamp(to, 0, _ids.Count), moved);
    }

    /// <summary>A replaced row is a different clip: it gets new ids.</summary>
    public void Replace(int index, int count)
    {
        for (int i = index; i < index + count && i < _ids.Count; i++) if (i >= 0) _ids[i] = Guid.NewGuid();
    }

    /// <summary>After a Reset the identities are unknown: <paramref name="count"/> fresh ids.</summary>
    public void Reset(int count)
    {
        _ids.Clear();
        Insert(0, count);
    }

    /// <summary>Restore: the ids the saved edit list used, in queue order.</summary>
    public void Seed(IEnumerable<Guid> ids)
    {
        _ids.Clear();
        _ids.AddRange(ids);
    }

    /// <summary>MERGEUNDO_01 — gives row <paramref name="index"/> a known id (a clip re-inserted by undo keeps its identity).</summary>
    public void SetAt(int index, Guid id)
    {
        if (index >= 0 && index < _ids.Count) _ids[index] = id;
    }

    /// <summary>
    /// Mirrors one queue collection event. <paramref name="count"/> is the queue's size AFTER the
    /// event. Returns true when the list had to be re-synchronised (a missed event).
    /// </summary>
    public bool Apply(NotifyCollectionChangedEventArgs e, int count)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                Insert(e.NewStartingIndex < 0 ? _ids.Count : e.NewStartingIndex, e.NewItems?.Count ?? 1);
                break;
            case NotifyCollectionChangedAction.Remove:
                Remove(e.OldStartingIndex, e.OldItems?.Count ?? 1);
                break;
            case NotifyCollectionChangedAction.Move:
                Move(e.OldStartingIndex, e.NewStartingIndex, e.OldItems?.Count ?? 1);
                break;
            case NotifyCollectionChangedAction.Replace:
                Replace(e.NewStartingIndex, e.NewItems?.Count ?? 1);
                break;
            default:
                Reset(count);
                break;
        }
        return EnsureCount(count);
    }

    /// <summary>Makes the list match <paramref name="count"/> rows if events were missed (defensive; logs nothing).</summary>
    public bool EnsureCount(int count)
    {
        if (_ids.Count == count) return false;
        if (_ids.Count > count) _ids.RemoveRange(count, _ids.Count - count);
        else Insert(_ids.Count, count - _ids.Count);
        return true;
    }
}

/// <summary>The music placement as the window holds it (merged seconds, pre base speed).</summary>
public sealed record MergerMusicState(
    IReadOnlyList<string> Paths,
    IReadOnlyList<double> DurationsSec,
    double OffsetSec,
    double StartMergedSec,
    double EndMergedSec,
    double MusicVolume,
    double VideoVolume,
    bool Loop,
    bool Ducking,
    bool Carving);

/// <summary>Everything the window owns today, captured on the UI thread.</summary>
public sealed record MergerUiState
{
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<Guid> Ids { get; init; } = Array.Empty<Guid>();
    public bool ScraperEnabled { get; init; } = true;
    public double BaseSpeed { get; init; } = 1.0;
    public string? ThumbPath { get; init; }
    public double ThumbSourceSec { get; init; }
    public MergerMusicState? Music { get; init; }
}

/// <summary>What the window has to do to restore a saved edit list.</summary>
public sealed record MergerRestorePlan(MergeEdl Edl, IReadOnlyList<string> Missing, IReadOnlyList<string> Changed);

public static class MergerSession
{
    /// <summary>
    /// Builds the edit list for the window's current state.
    /// </summary>
    /// <param name="analysis">Finished analysis for a path, or null while it is still running.</param>
    /// <param name="fileId">(size, last-write UTC ticks) of a path, or null when it cannot be read.</param>
    /// <param name="timeline">The merged timeline when it describes THIS queue; null otherwise.</param>
    /// <param name="previous">The last captured edit list: carries effects, in/out and pending analysis per ClipId.</param>
    public static MergeEdl Capture(
        MergerUiState s,
        Func<string, MergeClipInfo?> analysis,
        Func<string, (long Size, long WriteTicks)?> fileId,
        MergedTimeline? timeline,
        MergeEdl? previous)
    {
        var prevById = new Dictionary<Guid, EdlClip>();
        if (previous != null) foreach (var c in previous.Clips) prevById[c.ClipId] = c;

        int n = Math.Min(s.Paths.Count, s.Ids.Count);
        var clips = new List<EdlClip>(n);
        for (int i = 0; i < n; i++)
        {
            string path = s.Paths[i];
            Guid id = s.Ids[i];
            prevById.TryGetValue(id, out var prev);
            if (prev != null && !SamePath(prev.Path, path)) prev = null;

            var clip = prev ?? new EdlClip { ClipId = id, Path = path };
            if (fileId(path) is (long size, long ticks))
            {
                bool changed = clip.SizeBytes != 0 && (clip.SizeBytes != size || clip.LastWriteUtcTicks != ticks);
                if (changed) clip = clip with { DurationUs = 0, Timing = null, IntroCutUs = 0 };
                clip = clip with { SizeBytes = size, LastWriteUtcTicks = ticks };
            }
            if (analysis(path) is MergeClipInfo info && info.Ok)
            {
                clip = clip with
                {
                    DurationUs = SecToUs(info.DurationSec),
                    Timing = info.Timing,
                    IntroCutUs = info.Timing is { IntroFrames: > 0 } ? SecToUs(info.IntroSec) : 0,
                };
            }
            clips.Add(clip with { ClipId = id, Path = path });
        }

        EdlThumbnail? thumb = null;
        if (s.ThumbPath != null)
        {
            for (int i = 0; i < clips.Count; i++)
            {
                if (!SamePath(clips[i].Path, s.ThumbPath)) continue;
                thumb = new EdlThumbnail(new EdlAnchor(clips[i].ClipId, SecToUs(s.ThumbSourceSec)));
                break;
            }
        }

        EdlMusic? music = null;
        if (s.Music is MergerMusicState m && m.Paths.Count > 0)
        {
            EdlAnchor start, end;
            if (timeline != null && timeline.Clips.Count == clips.Count && clips.Count > 0)
            {
                // Stable capture: an anchor that still maps to the same moment is KEPT, so a clip-end
                // anchor is not rewritten as "start of the next clip" (same moment, different data),
                // which would look like an edit to undo (MERGEUNDO_01).
                start = KeepOrLocate(timeline, clips, previous?.Music?.Start, m.StartMergedSec);
                end = KeepOrLocate(timeline, clips, previous?.Music?.End, m.EndMergedSec);
            }
            else if (previous?.Music is EdlMusic pm)
            {
                start = pm.Start; end = pm.End;
            }
            else
            {
                start = default; end = default;
            }
            music = new EdlMusic
            {
                FilePaths = m.Paths,
                DurationsSec = m.DurationsSec,
                OffsetSec = m.OffsetSec,
                Start = start,
                End = end,
                MusicVolume = m.MusicVolume,
                VideoVolume = m.VideoVolume,
                Loop = m.Loop,
                Ducking = m.Ducking,
                Carving = m.Carving,
            };
        }

        return new MergeEdl
        {
            Clips = clips,
            ScraperEnabled = s.ScraperEnabled,
            BaseSpeed = s.BaseSpeed > 0 && double.IsFinite(s.BaseSpeed) ? s.BaseSpeed : 1.0,
            Thumbnail = thumb,
            Music = music,
        };
    }

    /// <summary>
    /// What to restore from <paramref name="edl"/>. A clip whose file is gone is dropped (and listed);
    /// a clip whose file changed is kept (and listed) with its cached analysis cleared. Thumbnail and
    /// music anchors on dropped clips are repaired. Never throws.
    /// </summary>
    public static MergerRestorePlan Plan(MergeEdl edl, Func<string, (long Size, long WriteTicks)?> fileId)
    {
        var kept = new List<EdlClip>(edl.Clips.Count);
        var missing = new List<string>();
        var changed = new List<string>();
        foreach (var c in edl.Clips)
        {
            if (string.IsNullOrWhiteSpace(c.Path) || fileId(c.Path) is not (long size, long ticks))
            {
                missing.Add(c.Path);
                continue;
            }
            if (c.SizeBytes != 0 && (c.SizeBytes != size || c.LastWriteUtcTicks != ticks))
            {
                changed.Add(c.Path);
                kept.Add(c with { SizeBytes = size, LastWriteUtcTicks = ticks, DurationUs = 0, Timing = null, IntroCutUs = 0 });
                continue;
            }
            kept.Add(c);
        }

        bool Has(Guid id) => kept.Exists(k => k.ClipId == id);

        EdlThumbnail? thumb = edl.Thumbnail is EdlThumbnail t && Has(t.At.ClipId) ? t : null;
        EdlMusic? music = edl.Music;
        if (music != null && kept.Count > 0)
        {
            if (!Has(music.Start.ClipId)) music = music with { Start = new EdlAnchor(kept[0].ClipId, 0) };
            if (!Has(music.End.ClipId)) music = music with { End = new EdlAnchor(kept[^1].ClipId, long.MaxValue) };
        }
        else if (kept.Count == 0) music = null;

        return new MergerRestorePlan(edl with { Clips = kept, Thumbnail = thumb, Music = music }, missing, changed);
    }

    /// <summary>
    /// The window's music state for a restored edit list. <paramref name="timeline"/> must describe the
    /// restored queue in the same order as <paramref name="ids"/>.
    /// </summary>
    public static MergerMusicState? MusicFromEdl(EdlMusic? m, MergedTimeline timeline, IReadOnlyList<Guid> ids)
    {
        if (m is null || m.FilePaths.Count == 0) return null;
        double ToMerged(EdlAnchor a, double fallback)
        {
            int i = IndexOf(ids, a.ClipId);
            return i < 0 || i >= timeline.Clips.Count ? fallback : timeline.ToMerged(i, a.SourceUs / 1_000_000.0);
        }
        double start = ToMerged(m.Start, 0);
        double end = ToMerged(m.End, timeline.TotalSec);
        if (end <= start + 0.01) end = timeline.TotalSec;
        return new MergerMusicState(m.FilePaths, m.DurationsSec, m.OffsetSec, start, end, m.MusicVolume, m.VideoVolume, m.Loop, m.Ducking, m.Carving);
    }

    /// <summary>
    /// RESTOREMISS_01 (user decision 2026-09-26) — the text of the restore approval dialog: every file
    /// of the last session that is no longer on disk, by NAME and by FOLDER, plus files that changed
    /// since. Long lists are capped so the dialog stays on screen.
    /// </summary>
    public static string DescribeRestoreProblems(IReadOnlyList<string> missing, IReadOnlyList<string> changed, int keptCount, int maxListed = 12)
    {
        var sb = new System.Text.StringBuilder();
        if (missing.Count > 0)
        {
            sb.Append(missing.Count == 1 ? "1 clip from your last session is no longer on disk:" : $"{missing.Count} clips from your last session are no longer on disk:");
            AppendFiles(sb, missing, maxListed);
        }
        if (changed.Count > 0)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(changed.Count == 1 ? "1 clip changed on disk since then (it will be re-analysed):" : $"{changed.Count} clips changed on disk since then (they will be re-analysed):");
            AppendFiles(sb, changed, maxListed);
        }
        sb.Append("\n\n").Append(keptCount > 0
            ? $"Continue with the {keptCount} clip(s) that are still there, or start with an empty list?"
            : "Nothing from the last session can be restored.");
        return sb.ToString();
    }

    private static void AppendFiles(System.Text.StringBuilder sb, IReadOnlyList<string> files, int maxListed)
    {
        for (int i = 0; i < files.Count && i < maxListed; i++)
        {
            string f = files[i] ?? "";
            string name = System.IO.Path.GetFileName(f);
            string? folder = System.IO.Path.GetDirectoryName(f);
            sb.Append("\n  • ").Append(string.IsNullOrEmpty(name) ? f : name);
            if (!string.IsNullOrEmpty(folder)) sb.Append("\n      in ").Append(folder);
        }
        if (files.Count > maxListed) sb.Append($"\n  … and {files.Count - maxListed} more (all listed in the log).");
    }

    // ── MERGEUNDO_01 — undo/redo support ────────────────────────────────────────────────────

    /// <summary>
    /// The part of an edit list the USER authored. Analysis results (length, timing tag, snapped
    /// intro cut, file identity) are stripped, so a background analysis finishing is never an undo
    /// step and undo never restores stale analysis.
    /// </summary>
    public static MergeEdl UserEdit(MergeEdl edl)
    {
        var clips = new List<EdlClip>(edl.Clips.Count);
        foreach (var c in edl.Clips)
            clips.Add(c with { DurationUs = 0, Timing = null, IntroCutUs = 0, SizeBytes = 0, LastWriteUtcTicks = 0 });
        return edl with { Clips = clips };
    }

    /// <summary>
    /// Names the action that turned <paramref name="prev"/> into <paramref name="next"/> for the undo
    /// history, plus a gesture key for continuous gestures (a slider sweep, a marker drag).
    /// </summary>
    public static (string Label, string? GestureKey) DescribeChange(MergeEdl prev, MergeEdl next)
    {
        if (!SameIds(prev.Clips, next.Clips))
        {
            if (next.Clips.Count > prev.Clips.Count) return (next.Clips.Count - prev.Clips.Count == 1 ? "add clip" : "add clips", null);
            if (next.Clips.Count < prev.Clips.Count) return (prev.Clips.Count - next.Clips.Count == 1 ? "remove clip" : "remove clips", null);
            return (SameIdSet(prev.Clips, next.Clips) ? "reorder clips" : "change clips", null);
        }
        if (prev.ScraperEnabled != next.ScraperEnabled) return (next.ScraperEnabled ? "turn thumbnail scraper on" : "turn thumbnail scraper off", null);
        if (!Equals(prev.Thumbnail, next.Thumbnail))
        {
            if (prev.Thumbnail is null) return ("set thumbnail", null);
            if (next.Thumbnail is null) return ("remove thumbnail", null);
            return ("move thumbnail", "thumb");
        }
        if (!Equals(prev.Music, next.Music))
        {
            if (prev.Music is null) return ("add music", null);
            if (next.Music is null) return ("remove music", null);
            return ("change music", null);
        }
        if (prev.BaseSpeed != next.BaseSpeed) return ("change speed", "speed");
        return ("edit clip", null);
    }

    /// <summary>
    /// Turns the live <paramref name="queue"/> into <paramref name="target"/> with the fewest simple
    /// operations (Move / Insert / RemoveAt — never Clear, so the clip being previewed is not torn
    /// down). <paramref name="ids"/> must be tracking <paramref name="queue"/>'s events
    /// (<see cref="ClipIdList.Apply"/>); re-inserted clips get their saved ids back.
    /// </summary>
    public static void SyncQueue(ObservableCollection<string> queue, ClipIdList ids, IReadOnlyList<EdlClip> target)
    {
        for (int i = 0; i < target.Count; i++)
        {
            var want = target[i];
            int at = -1;
            for (int j = i; j < ids.Count && j < queue.Count; j++)
            {
                if (ids.Ids[j] == want.ClipId) { at = j; break; }
            }
            if (at == i) continue;
            if (at > i)
            {
                queue.Move(at, i);
                continue;
            }
            queue.Insert(i, want.Path);
            ids.SetAt(i, want.ClipId);
        }
        while (queue.Count > target.Count) queue.RemoveAt(queue.Count - 1);
    }

    private static bool SameIds(IReadOnlyList<EdlClip> a, IReadOnlyList<EdlClip> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i].ClipId != b[i].ClipId) return false;
        return true;
    }

    private static bool SameIdSet(IReadOnlyList<EdlClip> a, IReadOnlyList<EdlClip> b)
    {
        var set = new HashSet<Guid>();
        foreach (var c in a) set.Add(c.ClipId);
        foreach (var c in b) if (!set.Remove(c.ClipId)) return false;
        return set.Count == 0;
    }

    private static EdlAnchor KeepOrLocate(MergedTimeline timeline, List<EdlClip> clips, EdlAnchor? prev, double mergedSec)
    {
        if (prev is EdlAnchor a)
        {
            int i = clips.FindIndex(c => c.ClipId == a.ClipId);
            if (i >= 0 && Math.Abs(timeline.ToMerged(i, a.SourceUs / 1_000_000.0) - mergedSec) < 0.001) return a;
        }
        return ToAnchor(timeline, clips, mergedSec);
    }

    private static EdlAnchor ToAnchor(MergedTimeline timeline, List<EdlClip> clips, double mergedSec)
    {
        var (idx, src) = timeline.Locate(mergedSec);
        if (idx < 0 || idx >= clips.Count) return new EdlAnchor(clips[0].ClipId, 0);
        return new EdlAnchor(clips[idx].ClipId, SecToUs(src));
    }

    private static int IndexOf(IReadOnlyList<Guid> ids, Guid id)
    {
        for (int i = 0; i < ids.Count; i++) if (ids[i] == id) return i;
        return -1;
    }

    private static bool SamePath(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static long SecToUs(double sec) => double.IsFinite(sec) && sec > 0 ? (long)Math.Round(sec * 1_000_000.0) : 0;
}
