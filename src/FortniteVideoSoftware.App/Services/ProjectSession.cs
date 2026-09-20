// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/06_PROJECT_DOCUMENT_MODEL.md
// Forbidden to modify without reading: docs/07_UNDO_AND_HISTORY.md
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using FortniteVideoSoftware.App.Abstractions;
using FortniteVideoSoftware.App.Controls;   // NoticeKind
using FortniteVideoSoftware.App.ViewModels;
using FortniteVideoSoftware.Core.Abstractions;
using FortniteVideoSoftware.Core.Media;
using FortniteVideoSoftware.Core.Project;
using FortniteVideoSoftware.Core.Undo;

namespace FortniteVideoSoftware.App.Services;

/// <summary>
/// PROJSESSION_01 — THE DOCUMENT THE USER IS EDITING, AND ITS HISTORY.
///
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// WHY THIS EXISTS — TWO DEFECTS, ONE CAUSE.
///
/// <b>1. The application could not save.</b> `ProjectDocument`, `ProjectSerializer`, `ProjectStore`
/// and `RecentProjects` were fully written, specified (06), and covered by ~370 lines of test —
/// and a grep for any of them across `src/FortniteVideoSoftware.App/` returned <b>nothing</b>.
/// There was no Save, no Open, no recent list and no autosave. A user who spent forty minutes on
/// speed ramps, memes and a music bed and closed the window lost all of it, with no prompt.
///
/// <b>2. Undo was three private implementations and one dead one.</b> `Core/Undo/UndoStack&lt;T&gt;`
/// was specified by 07 and tested, and never instantiated. `GranularSpeedEditorWindow` kept its own
/// `List`-based `_undoStack`/`_redoStack`; `CropToolWindow` had a third; and <b>`MainWindow` had no
/// undo at all</b> — no Ctrl+Z on the main timeline, where trims, cuts, memes and the music bed are
/// decided.
///
/// Both are the same cause: <b>there was no object that WAS the user's work.</b> State lived in
/// whichever window happened to own the control that produced it. This class is that object.
/// Everything else follows from it — you can save a document, you can keep a stack of documents,
/// and you can hand one to another window without serialising it through a named pipe.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </para>
///
/// <para>
/// <b>PROJSESSION_02 — capture is a PROJECTION, not a second source of truth.</b> The view-models
/// remain where the live editing state lives; this class reads them to build a document and writes
/// a document back into them. It deliberately does NOT hold a parallel copy that could drift.
/// `Capture()` is cheap and is called at edit boundaries, not per frame.
/// </para>
///
/// <para>
/// <b>THREADING.</b> UI thread only. It reads and writes view-model properties bound to controls.
/// The one exception is <see cref="AutosaveTick"/>, which is invoked from a dispatcher timer and is
/// therefore also on the UI thread; the actual disk write inside `ProjectStore` is synchronous and
/// atomic, and is fast enough at document size (kilobytes) not to warrant a worker.
/// </para>
/// </summary>
public sealed class ProjectSession
{
    /// <summary>
    /// PROJSESSION_03 — autosave cadence. The RecoveryManager already snapshots continuously for
    /// crash recovery (05 §4); this is different — it keeps the user's NAMED file current so a
    /// power cut does not cost the last hour. Only fires when dirty AND a path is known.
    /// </summary>
    public const int AutosaveIntervalSeconds = 30;

    private readonly IProjectStore _store;
    private readonly IFilePickerService _picker;
    private readonly IUserNotifier _notifier;
    private readonly IFaultSink _faults;
    private readonly IClock _clock;
    private readonly MainViewModel _viewModel;
    private readonly Func<(int Width, int Height, double Fps)> _probeVideoMetrics;

    private UndoStack<ProjectDocument>? _history;
    private DateTimeOffset _lastAutosaveUtc;

    /// <summary>
    /// UNDO_21 — re-entrancy guard. Applying an undone document writes ~20 view-model properties,
    /// and several of those setters are wired to handlers that call <see cref="PushEdit"/>. Without
    /// this flag an undo pushes its own result onto the stack, which makes redo unreachable and the
    /// history grow while the user is trying to shrink it. 07 §2 names this as one of the four
    /// inherited rules; it was previously honoured only inside the granular editor.
    /// </summary>
    private bool _applying;

    public ProjectSession(
        IProjectStore store,
        IFilePickerService picker,
        IUserNotifier notifier,
        IFaultSink faults,
        IClock clock,
        MainViewModel viewModel,
        Func<(int Width, int Height, double Fps)> probeVideoMetrics)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _picker = picker ?? throw new ArgumentNullException(nameof(picker));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _faults = faults ?? throw new ArgumentNullException(nameof(faults));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _probeVideoMetrics = probeVideoMetrics ?? throw new ArgumentNullException(nameof(probeVideoMetrics));
        _lastAutosaveUtc = _clock.UtcNow;
    }

    /// <summary>The <c>.fvsproj</c> this session is bound to, or null for a project never saved.</summary>
    public string? CurrentPath { get; private set; }

    /// <summary>True when there are edits not yet written to <see cref="CurrentPath"/>.</summary>
    public bool IsDirty { get; private set; }

    public bool CanUndo => _history?.CanUndo == true;
    public bool CanRedo => _history?.CanRedo == true;
    public string? NextUndoLabel => _history?.NextUndoLabel;
    public string? NextRedoLabel => _history?.NextRedoLabel;

    /// <summary>Raised whenever the title bar's text would change (path, dirty flag or undo depth).</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Raised after a document has been written into the view-models, so the window can redraw the
    /// timeline, reload the music bed and re-seat the preview. Carries the document that was applied.
    /// </summary>
    public event EventHandler<ProjectDocument>? DocumentApplied;

    // ── History (#5) ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a history for the clip that was just loaded. Called once per video load. Discards any
    /// previous history on purpose — undoing across a video change would restore segments that
    /// index into a file that is no longer open.
    /// </summary>
    public void BeginHistory()
    {
        _history = new UndoStack<ProjectDocument>(Capture());
        _history.Changed += (_, _) => Raise();
        Raise();
    }

    /// <summary>
    /// UNDO_20 — records the state AFTER an edit, with a label the user will read in the notice.
    /// <paramref name="gestureKey"/> coalesces a continuous gesture (a drag, a slider sweep) into a
    /// single undo step; pass the same key for the duration of the gesture and call
    /// <see cref="EndGesture"/> on release.
    /// </summary>
    public void PushEdit(string label, string? gestureKey = null)
    {
        if (_applying || _history is null) return;

        if (_history.Apply(Capture(), label, gestureKey))
        {
            IsDirty = true;
            Raise();
        }
    }

    /// <summary>Closes a coalescing window opened by <see cref="PushEdit"/> with a gesture key.</summary>
    public void EndGesture() => _history?.EndGesture();

    public void Undo()
    {
        if (_history is null) return;

        ProjectDocument? previous = _history.Undo();
        if (previous is null)
        {
            _notifier.Notify("Nothing left to undo", NoticeKind.Info);
            return;
        }

        Apply(previous);
        _notifier.Notify($"Undid: {_history.NextRedoLabel ?? "last change"} — Ctrl+Y to redo", NoticeKind.Info);
    }

    public void Redo()
    {
        if (_history is null) return;

        ProjectDocument? next = _history.Redo();
        if (next is null)
        {
            _notifier.Notify("Nothing left to redo", NoticeKind.Info);
            return;
        }

        Apply(next);
        _notifier.Notify($"Redid: {_history.NextUndoLabel ?? "last change"}", NoticeKind.Info);
    }

    // ── Save / Open (#1) ────────────────────────────────────────────────────────────────────

    /// <summary>Ctrl+S. Falls through to Save As when the project has never been written.</summary>
    public async Task<bool> SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentPath)) return await SaveAsAsync();
        return WriteTo(CurrentPath!, announce: true);
    }

    /// <summary>Ctrl+Shift+S.</summary>
    public async Task<bool> SaveAsAsync()
    {
        if (!_viewModel.HasLoadedVideo)
        {
            _notifier.Notify("Load a video before saving a project.", NoticeKind.Warning);
            return false;
        }

        string suggested = SuggestFileName();

        string? chosen = await _picker.SaveFileAsync(new FilePickerRequest(
            Title: "Save project",
            SuggestedFileName: suggested,
            ExtensionLabel: "Clip Studio project",
            Extension: "fvsproj",
            StartDirectoryKey: "last_project_dir.txt"));

        if (chosen is null) return false;   // user cancelled; not a failure, nothing to report

        return WriteTo(_store.NormalizeExtension(chosen), announce: true);
    }

    /// <summary>
    /// Ctrl+O. Prompts about unsaved work first — losing an hour of edits to a mis-click on Open is
    /// the same defect as losing it on close, and the same guard closes both.
    /// </summary>
    public async Task<bool> OpenAsync()
    {
        if (!await ConfirmDiscardAsync("Open another project")) return false;

        string? chosen = await _picker.OpenFileAsync(new FilePickerRequest(
            Title: "Open project",
            SuggestedFileName: null,
            ExtensionLabel: "Clip Studio project",
            Extension: "fvsproj",
            StartDirectoryKey: "last_project_dir.txt"));

        if (chosen is null) return false;

        ProjectDocument? document = _store.Load(chosen, out string? error, out bool fromBackup);

        if (document is null)
        {
            _faults.Fatal("PROJECT",
                "That project file could not be opened, so nothing was changed." +
                Environment.NewLine + Environment.NewLine + (error ?? "The file is not readable."));
            return false;
        }

        // PROJ_09 — a silent fall back to .bak hands the user an OLDER version of their own work
        // and lets them keep editing it believing it is current. It must be said out loud.
        if (fromBackup)
        {
            _notifier.Notify(
                "The main project file was damaged, so the backup was opened instead. " +
                "Any changes made after that backup are not in this version.",
                NoticeKind.Warning);
        }

        SourceIntegrity check = document.CheckSource();
        if (check == SourceIntegrity.Missing)
        {
            _faults.Fatal("PROJECT",
                "The video this project was built from is no longer at:" + Environment.NewLine +
                document.Source.FilePath + Environment.NewLine + Environment.NewLine +
                "Move it back, or load the video first and re-apply your edits.");
            return false;
        }

        if (check == SourceIntegrity.Changed)
        {
            _notifier.Notify(
                "The source video has changed since this project was saved — cuts and speed ramps " +
                "may no longer line up with the footage.",
                NoticeKind.Warning);
        }

        Apply(document);
        CurrentPath = chosen;
        IsDirty = false;
        _history = new UndoStack<ProjectDocument>(document);
        _history.Changed += (_, _) => Raise();
        Raise();

        _notifier.Notify($"Opened {Path.GetFileNameWithoutExtension(chosen)}", NoticeKind.Success);
        return true;
    }

    /// <summary>
    /// PROJSESSION_03 — called from the window's existing dispatcher timer. Writes only when there
    /// is something to write AND somewhere to write it; a never-saved project has no path to
    /// autosave to and is covered by the crash-recovery snapshot instead.
    /// </summary>
    public void AutosaveTick()
    {
        if (!IsDirty || string.IsNullOrWhiteSpace(CurrentPath)) return;
        if ((_clock.UtcNow - _lastAutosaveUtc).TotalSeconds < AutosaveIntervalSeconds) return;

        _lastAutosaveUtc = _clock.UtcNow;
        WriteTo(CurrentPath!, announce: false);
    }

    /// <summary>
    /// The close guard. Returns false to cancel the close. Wired into the existing deferred-close
    /// contract (05 §3) BEFORE `e.Cancel = true` is cleared.
    /// </summary>
    public async Task<bool> ConfirmDiscardAsync(string action)
    {
        if (!IsDirty) return true;

        bool save = await _notifier.ConfirmAsync(
            "Unsaved changes",
            $"You have changes that are not saved. {action} without saving them?",
            "Save first",
            "Discard changes");

        if (!save) return true;           // user chose to discard
        return await SaveAsync();          // saved -> proceed; save failed -> stay put
    }

    // ── Document <-> view-model ─────────────────────────────────────────────────────────────

    /// <summary>PROJSESSION_02 — projects the live view-model state into an immutable document.</summary>
    public ProjectDocument Capture()
    {
        var timeline = _viewModel.Timeline;
        string path = _viewModel.LoadedVideoPath ?? string.Empty;
        (int width, int height, double fps) = ProbeMetricsSafely();

        double cutStartMs = timeline.IsTrimStartSet ? timeline.TrimStartMs : 0;
        double trimmedMs = timeline.IsTrimEndSet && timeline.TrimEndMs > cutStartMs
            ? timeline.TrimEndMs - cutStartMs
            : 0;

        MusicWizardResult? music = _viewModel.MusicWizardResult;
        VoiceOverWindow.VoiceOverResult? voice = _viewModel.VoiceOverResult;

        return new ProjectDocument
        {
            Source = SourceClip.Probe(path, timeline.LoadedVideoDurationMs, width, height, fps),
            BaseSpeed = timeline.BaseSpeed,
            SourceCutStartMs = cutStartMs,
            TrimmedDurationMs = trimmedMs,
            Segments = timeline.SpeedSegments.ToArray(),
            Cuts = CutRange.ToClipRelative(timeline.Cuts, cutStartMs).ToArray(),
            Memes = timeline.MemePlacements.ToArray(),
            Audio = new ProjectAudio
            {
                MusicFilePath = _viewModel.IsMusicActive ? music?.MusicFilePath : null,
                MusicStartSec = music?.OffsetSeconds ?? 0,
                MusicVolume = music?.MusicVolume ?? 1.0,
                VideoVolume = music?.VideoVolume ?? 1.0,
                SidechainDucking = music?.EnableDucking ?? false,
                VoiceOverFilePath = voice?.VoiceOverWavPath,
                VoiceOverAtOutputSec = voice?.VoiceOverStartTimestampSec ?? 0,
                VoiceOverVolume = 1.0,
            },
            Export = new ProjectExport
            {
                QualityIndex = _viewModel.QualitySliderValue,
                PortraitMode = _viewModel.IsPortraitMode,
                HardwareMode = "Auto",
            },
            Title = string.IsNullOrWhiteSpace(path) ? "Untitled" : Path.GetFileNameWithoutExtension(path),
            ModifiedUtc = _clock.UtcNow,
        };
    }

    /// <summary>
    /// Writes a document back into the view-models, then tells the window to redraw.
    ///
    /// <para>⚠️ UNDO_21 — the whole body runs under <see cref="_applying"/>. Several of the setters
    /// below are wired to change handlers that call <see cref="PushEdit"/>; without the guard an
    /// undo would push its own result and redo would be unreachable.</para>
    /// </summary>
    private void Apply(ProjectDocument document)
    {
        _applying = true;
        try
        {
            var timeline = _viewModel.Timeline;

            timeline.BaseSpeed = document.BaseSpeed;

            timeline.SpeedSegments.Clear();
            timeline.SpeedSegments.AddRange(document.Segments);

            timeline.Cuts.Clear();
            foreach (OutputTimeline.Cut cut in document.Cuts)
            {
                timeline.Cuts.Add(new CutRange(
                    document.SourceCutStartMs + (cut.StartSec * 1000.0),
                    document.SourceCutStartMs + (cut.EndSec * 1000.0)));
            }

            timeline.MemePlacements.Clear();
            timeline.MemePlacements.AddRange(document.Memes);

            if (document.SourceCutStartMs > 0.001)
            {
                timeline.TrimStartMs = document.SourceCutStartMs;
                timeline.IsTrimStartSet = true;
            }

            if (document.TrimmedDurationMs > 0.001)
            {
                timeline.TrimEndMs = document.SourceCutStartMs + document.TrimmedDurationMs;
                timeline.IsTrimEndSet = true;
            }

            _viewModel.IsPortraitMode = document.Export.PortraitMode;
            if (document.Export.QualityIndex >= 0)
                _viewModel.QualitySliderValue = document.Export.QualityIndex;

            _viewModel.IsMusicActive = !string.IsNullOrWhiteSpace(document.Audio.MusicFilePath);
        }
        finally
        {
            _applying = false;
        }

        IsDirty = true;
        DocumentApplied?.Invoke(this, document);
        Raise();
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────

    private bool WriteTo(string path, bool announce)
    {
        ProjectIoResult result = _store.Save(Capture(), path);

        if (!result.Success)
        {
            // Fatal, not degraded: the user believes their work is on disk and it is not.
            _faults.Fatal("PROJECT",
                "Your project could not be saved, so nothing was written." + Environment.NewLine +
                Environment.NewLine + (result.Error ?? "The file could not be written."));
            return false;
        }

        CurrentPath = path;
        IsDirty = false;
        _lastAutosaveUtc = _clock.UtcNow;
        Raise();

        if (announce) _notifier.Notify($"Saved {Path.GetFileNameWithoutExtension(path)}", NoticeKind.Success);
        return true;
    }

    private string SuggestFileName()
    {
        string? video = _viewModel.LoadedVideoPath;
        string stem = string.IsNullOrWhiteSpace(video) ? "Untitled" : Path.GetFileNameWithoutExtension(video);
        return stem + ProjectDocument.FileExtension;
    }

    private (int Width, int Height, double Fps) ProbeMetricsSafely()
        => _faults.GuardValue("PROJECT",
            "The video's size and frame rate could not be read, so they were left out of the saved project.",
            _probeVideoMetrics,
            (0, 0, 0.0),
            FaultTier.Recoverable);

    private void Raise() => StateChanged?.Invoke(this, EventArgs.Empty);
}
