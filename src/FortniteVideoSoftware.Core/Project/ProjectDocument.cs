// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/06_PROJECT_DOCUMENT_MODEL.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.Core.Project;

/// <summary>
/// PROJ_01 — THE SAVEABLE DOCUMENT. Everything a user built, in one immutable value.
///
/// <para>
/// WHY THIS TYPE EXISTS: until now the application had no way to save work. The only thing that
/// ever reached disk was <c>recovery_v2.json</c>, which is a CRASH artefact — it answers "the app
/// died, what was on screen?", not "open the montage I made on Tuesday". Closing the app on
/// purpose destroyed the session. An hour of markers, cuts, speed ramps, zooms and meme placements
/// existed only in the live window.
/// </para>
///
/// <para>
/// The set of fields here is not a guess. It is exactly the input surface of
/// <see cref="OutputTimeline.Create"/> plus the settings that decide how those chunks are rendered.
/// If a field is not needed to reconstruct the finished video, it does not belong in the document;
/// if it is, its absence is a silent data-loss bug. <see cref="BuildTimeline"/> is the proof — it
/// reconstructs the authoritative timeline from nothing but this record, which is the test that the
/// document is complete.
/// </para>
///
/// <para>
/// ⚠️ NORTH STAR #2 (ABSOLUTE AUTHORITY FOR TIME) APPLIES HERE. This document stores the INPUTS to
/// <see cref="OutputTimeline"/> and never a derived duration. Caching <c>TotalOutputSeconds</c> in
/// the file would create a second answer to "how long is the video", which would go stale the
/// moment the maths is corrected in a later build and would then be believed over the real model.
/// <see cref="SourceClip.DurationMs"/> is the one measured value stored, and it is a property of
/// the FILE, not of the edit.
/// </para>
///
/// <para>
/// ⚠️ IMMUTABLE ON PURPOSE. Undo (PROJ_07 / <c>UndoStack</c>) keeps a stack of these. If the record
/// were mutable, every entry on that stack would be the same object and undo would restore the
/// present. Mutate by <c>with</c>-expression only, and keep every collection an
/// <see cref="IReadOnlyList{T}"/> of value types or immutable records — never a UI control, bitmap,
/// stream or IPC handle (the rule 04_UI_UX_AVALONIA_SPEC.md §6 UI-GRANULAR already states for the
/// granular editor's stack, generalised).
/// </para>
/// </summary>
public sealed record ProjectDocument
{
    /// <summary>Magic string written into every file so a wrong-type file is rejected by name, not by crash.</summary>
    public const string FormatId = "fvsproj";

    /// <summary>
    /// PROJ_02 — the schema number this build WRITES. Bump it only when the meaning of an existing
    /// field changes; adding a new optional field does not need a bump, because
    /// <see cref="ProjectSerializer"/> tolerates missing keys on read.
    /// </summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// PROJ_02 — the oldest schema this build can still READ. A file below this is refused with an
    /// explanation instead of being silently mangled into something the user did not author.
    /// </summary>
    public const int MinimumReadableSchemaVersion = 1;

    public const string FileExtension = ".fvsproj";

    /// <summary>The gameplay file being edited, plus enough fingerprint to notice it changed.</summary>
    public required SourceClip Source { get; init; }

    /// <summary>Speed applied to every stretch not covered by a <see cref="Segments"/> entry.</summary>
    public double BaseSpeed { get; init; } = 1.0;

    /// <summary>
    /// Trim-in point in ABSOLUTE source milliseconds. This is the same number
    /// <see cref="OutputTimeline.Create"/> takes as <c>sourceCutStartMs</c>, and it is the origin
    /// that makes every other position in this document clip-relative.
    /// </summary>
    public double SourceCutStartMs { get; init; }

    /// <summary>
    /// Length of the trimmed clip in milliseconds — <c>totalDurationMs</c> for
    /// <see cref="OutputTimeline.Create"/>. Zero means "not trimmed", and
    /// <see cref="EffectiveDurationMs"/> falls back to the measured file duration.
    /// </summary>
    public double TrimmedDurationMs { get; init; }

    /// <summary>Speed ramps and freezes, in ABSOLUTE source milliseconds (as <see cref="SpeedSegment"/> is defined).</summary>
    public IReadOnlyList<SpeedSegment> Segments { get; init; } = Array.Empty<SpeedSegment>();

    /// <summary>Deleted stretches, CLIP-RELATIVE seconds (as <see cref="OutputTimeline.Cut"/> is defined).</summary>
    public IReadOnlyList<OutputTimeline.Cut> Cuts { get; init; } = Array.Empty<OutputTimeline.Cut>();

    /// <summary>Meme cutaways, CLIP-RELATIVE seconds (as <see cref="MemePlacement"/> is defined).</summary>
    public IReadOnlyList<MemePlacement> Memes { get; init; } = Array.Empty<MemePlacement>();

    public ProjectAudio Audio { get; init; } = new();

    public ProjectExport Export { get; init; } = new();

    /// <summary>Free-text name shown in the title bar and the recent list. Never used as a file path.</summary>
    public string Title { get; init; } = "Untitled";

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ModifiedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// PROJ_03 — FORWARD COMPATIBILITY BAG. Top-level keys this build did not recognise, kept
    /// verbatim so that opening a v2 file in a v1 build and saving it back does not AMPUTATE the
    /// v2 fields. Without this, one accidental open-and-save on an older machine silently destroys
    /// work the newer build could have read. It is never interpreted, only carried.
    /// </summary>
    public JsonObject? UnknownFields { get; init; }

    /// <summary>
    /// The duration the timeline should actually be built against: the explicit trim when one was
    /// set, otherwise the measured length of the file.
    /// </summary>
    public double EffectiveDurationMs =>
        TrimmedDurationMs > 0.001 ? TrimmedDurationMs : Source.DurationMs;

    /// <summary>
    /// PROJ_04 — reconstructs the authoritative timeline from the document alone.
    /// <para>
    /// This is deliberately the ONLY way the rest of the application is allowed to turn a loaded
    /// project into a timeline. Any screen that re-derives chunks from its own copies of these
    /// lists is re-introducing exactly the duplicated-maths defect that
    /// <see cref="OutputTimeline"/>'s own header documents.
    /// </para>
    /// </summary>
    public OutputTimeline BuildTimeline()
    {
        var insertions = new List<OutputTimeline.Insertion>(Memes.Count);
        foreach (MemePlacement m in Memes)
            insertions.Add(new OutputTimeline.Insertion(m.AtSourceSecRelative, m.DurationSec, m.Id));

        return OutputTimeline.Create(
            EffectiveDurationMs,
            Segments,
            BaseSpeed,
            SourceCutStartMs,
            insertions,
            Cuts);
    }

    /// <summary>
    /// PROJ_05 — is the gameplay file still the one this project was built against?
    /// <para>
    /// A project stores a PATH, and paths outlive the files they point at. Answering this before
    /// restoring an edit is what lets the app say "that video has been replaced, your cuts may not
    /// line up" instead of cheerfully rendering markers against different footage. Length and
    /// modification time are a fingerprint, not a hash: hashing a 4 GB capture on every open would
    /// cost more than the entire load.
    /// </para>
    /// </summary>
    public SourceIntegrity CheckSource()
    {
        if (string.IsNullOrWhiteSpace(Source.FilePath)) return SourceIntegrity.Missing;

        FileInfo info;
        try
        {
            info = new FileInfo(Source.FilePath);
            if (!info.Exists) return SourceIntegrity.Missing;
        }
        catch (Exception ex)
        {
            Infrastructure.CoreLogger.Swallowed(ex);
            return SourceIntegrity.Missing;
        }

        // A zero fingerprint means the project predates fingerprinting, or the probe failed when it
        // was saved. Reporting Changed there would nag the user about every older project, so an
        // absent fingerprint is treated as "cannot tell", which is the honest answer.
        if (Source.SizeBytes <= 0) return SourceIntegrity.Unknown;

        if (info.Length != Source.SizeBytes) return SourceIntegrity.Changed;

        // Modification time is compared at whole-second resolution. Copying a file between
        // filesystems routinely perturbs the sub-second part without the bytes differing, and a
        // false "your video changed" warning teaches users to dismiss the real one.
        long stored = Source.ModifiedUtcSeconds;
        if (stored > 0)
        {
            long actual = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
            if (Math.Abs(actual - stored) > 2) return SourceIntegrity.Changed;
        }

        return SourceIntegrity.Intact;
    }

    /// <summary>Creates the empty document a brand-new edit starts from.</summary>
    public static ProjectDocument ForClip(SourceClip clip, string? title = null) => new()
    {
        Source = clip,
        Title = string.IsNullOrWhiteSpace(title)
            ? SafeTitleFrom(clip.FilePath)
            : title!,
        TrimmedDurationMs = clip.DurationMs,
    };

    private static string SafeTitleFrom(string path)
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrWhiteSpace(name) ? "Untitled" : name;
        }
        catch (ArgumentException)
        {
            return "Untitled";
        }
    }
}

/// <summary>PROJ_05 — the answer to "is the footage still what this project was cut against?".</summary>
public enum SourceIntegrity
{
    /// <summary>File is present and matches the stored fingerprint.</summary>
    Intact,

    /// <summary>File is present but its size or timestamp differs — the edit may no longer line up.</summary>
    Changed,

    /// <summary>File is gone or unreadable. The project can still be opened to relink it.</summary>
    Missing,

    /// <summary>No fingerprint was stored (older project). Do not warn the user about this.</summary>
    Unknown,
}

/// <summary>
/// PROJ_05 — the gameplay file plus the measurements the editor needs before it can lay anything
/// out. Width/height/fps are stored because probing a file costs a process launch, and a project
/// should open into a drawn timeline rather than into a spinner.
/// </summary>
public sealed record SourceClip
{
    public required string FilePath { get; init; }

    /// <summary>Full length of the FILE in milliseconds, before any trim.</summary>
    public double DurationMs { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public double Fps { get; init; }

    /// <summary>Size in bytes at save time. Zero means "not measured" — see <see cref="ProjectDocument.CheckSource"/>.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Unix seconds of last write at save time. Zero means "not measured".</summary>
    public long ModifiedUtcSeconds { get; init; }

    /// <summary>Reads the fingerprint fields off disk. Never throws; an unreadable file yields zeroes.</summary>
    public static SourceClip Probe(string filePath, double durationMs, int width, int height, double fps)
    {
        long size = 0;
        long modified = 0;
        try
        {
            FileInfo info = new(filePath);
            if (info.Exists)
            {
                size = info.Length;
                modified = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
            }
        }
        catch (Exception ex)
        {
            Infrastructure.CoreLogger.Swallowed(ex);
        }

        return new SourceClip
        {
            FilePath = filePath,
            DurationMs = durationMs,
            Width = width,
            Height = height,
            Fps = fps,
            SizeBytes = size,
            ModifiedUtcSeconds = modified,
        };
    }
}

/// <summary>
/// PROJ_06 — audio choices that survive a save.
/// <para>
/// ⚠️ NORTH STAR #3 (STRICT A/V PROCESS ISOLATION). <see cref="MusicVolume"/> and
/// <see cref="VideoVolume"/> are EXPORT mix levels and belong in the document. The master PREVIEW
/// volume is a property of this machine's playback session, not of the montage, and must never be
/// stored here — saving it would carry one machine's monitoring level into another machine's
/// export, which is precisely the coupling that invariant forbids.
/// </para>
/// </summary>
public sealed record ProjectAudio
{
    public string? MusicFilePath { get; init; }

    /// <summary>Where the music bed starts inside the music file, in seconds.</summary>
    public double MusicStartSec { get; init; }

    /// <summary>0.0 to 1.0 export mix level for the music bed.</summary>
    public double MusicVolume { get; init; } = 1.0;

    /// <summary>0.0 to 1.0 export mix level for the gameplay's own audio.</summary>
    public double VideoVolume { get; init; } = 1.0;

    /// <summary>Duck the music under the gameplay / voice over.</summary>
    public bool SidechainDucking { get; init; }

    public string? VoiceOverFilePath { get; init; }

    /// <summary>Where the recorded voice over sits in OUTPUT seconds.</summary>
    public double VoiceOverAtOutputSec { get; init; }

    public double VoiceOverVolume { get; init; } = 1.0;
}

/// <summary>PROJ_06 — export choices that survive a save, so re-exporting a project is one click.</summary>
public sealed record ProjectExport
{
    /// <summary>Index into the quality ladder. Stored as the INDEX the user chose, never as a bitrate.</summary>
    public int QualityIndex { get; init; } = -1;

    /// <summary>Explicit target size in megabytes when the user overrode the ladder; null means "use the ladder".</summary>
    public double? TargetMegabytes { get; init; }

    /// <summary>"Auto", "GPU" or "CPU". Free text so a future encoder does not need a schema bump.</summary>
    public string HardwareMode { get; init; } = "Auto";

    /// <summary>Folder the last export was written to, so the next one defaults sensibly.</summary>
    public string? OutputDirectory { get; init; }

    public bool PortraitMode { get; init; } = true;
}
