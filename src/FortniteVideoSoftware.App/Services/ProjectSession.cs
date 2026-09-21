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

    /// <summary>
    /// PROJ_11 — reads the HUD mask that is live RIGHT NOW: the active profile name from settings
    /// plus the resolved crop configuration. A seam rather than a direct call, because
    /// <c>SettingsManager</c> and <c>CropConfigStore</c> both touch disk and a session under test
    /// must be constructible without either.
    /// </summary>
    private readonly Func<ProjectMask?> _readLiveMask;

    /// <summary>PROJ_11 — the Video Merger's queue, or null when no merge is in progress.</summary>
    private readonly Func<ProjectMerge?> _readMergeQueue;

    /// <summary>
    /// UNDO_24 — where edit history is kept BETWEEN runs of the application.
    ///
    /// <para>
    /// 07_UNDO_AND_HISTORY.md §5 item 1. Until this was wired, <c>UndoStack</c> lived entirely in
    /// memory: quitting the app discarded every step of how a montage was built, and the user got
    /// no warning on the way out and nothing to Ctrl+Z against on the way back in.
    /// </para>
    ///
    /// <para>⚠️ §4 — the history is NOT in the <c>.fvsproj</c>. It is per-machine and disposable,
    /// and emailing a colleague a montage must not email them forty snapshots of its making.</para>
    /// </summary>
    private readonly UndoSidecarStore _sidecar;

    /// <summary>
    /// PROJSESSION_08 — THE APPLICATION'S OWN NOTION OF "IS THERE ANYTHING TO LOSE".
    ///
    /// <para>
    /// <c>MainWindow.HasUnsavedWork()</c> already answers this, and answers it better than a dirty
    /// bit can: it returns false when the clip has just been exported
    /// (<c>ExportedCleanSinceLastEdit</c>), when no clip is loaded, and when every edit is still at
    /// its default. The suite's tool-switch prompt has consulted it all along (SWITCHPROMPT_01).
    /// </para>
    ///
    /// <para>
    /// ⚠️ This session originally ignored it and prompted on its own <see cref="_dirty"/> flag,
    /// which is set by the edit hook and never cleared by an export. The result was a save prompt
    /// after a finished render — exactly the wrong-state defect the
    /// <c>_exportedCleanSinceLastEdit</c> flag exists to prevent, reintroduced one layer up.
    /// </para>
    /// </summary>
    private readonly Func<bool> _hasUnsavedWork;

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
        Func<(int Width, int Height, double Fps)> probeVideoMetrics,
        Func<bool> hasUnsavedWork,
        Func<ProjectMask?>? readLiveMask = null,
        Func<ProjectMerge?>? readMergeQueue = null,
        UndoSidecarStore? sidecar = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _picker = picker ?? throw new ArgumentNullException(nameof(picker));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _faults = faults ?? throw new ArgumentNullException(nameof(faults));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _probeVideoMetrics = probeVideoMetrics ?? throw new ArgumentNullException(nameof(probeVideoMetrics));
        _hasUnsavedWork = hasUnsavedWork ?? throw new ArgumentNullException(nameof(hasUnsavedWork));

        // PROJ_11 — these two default to "nothing to record" rather than being required, so the
        // existing call sites and every test keep compiling. ⚠️ A default that returns null is
        // honest here in a way it would not be elsewhere: it means "this session has no mask/merge
        // source wired", which is exactly true of a headless session.
        _readLiveMask = readLiveMask ?? (static () => null);
        _readMergeQueue = readMergeQueue ?? (static () => null);

        // UNDO_24 — defaults to the real store under ProgramData. A test that wants no disk passes
        // its own rooted at a temp folder; there is no "null means disabled" mode, because a
        // silently-disabled history is the defect this closes.
        _sidecar = sidecar ?? UndoSidecarStore.CreateDefault(Core.Infrastructure.ApplicationPaths.CreateDefault());

        _lastAutosaveUtc = _clock.UtcNow;
    }

    /// <summary>The <c>.fvsproj</c> this session is bound to, or null for a project never saved.</summary>
    public string? CurrentPath { get; private set; }

    /// <summary>Set by the edit hook, cleared by a successful write. Half of <see cref="IsDirty"/>.</summary>
    private bool _dirty;

    /// <summary>
    /// PROJSESSION_08 — true only when there are edits not yet written AND the application agrees
    /// there is something to lose.
    ///
    /// <para>
    /// Both halves are required. <see cref="_dirty"/> alone says "an edit happened since the last
    /// save", which stays true forever after a render because nothing about exporting writes a
    /// <c>.fvsproj</c>. <see cref="_hasUnsavedWork"/> alone says "this project differs from a fresh
    /// one", which is true the moment a clip is trimmed even if it was saved a second ago.
    /// </para>
    /// </summary>
    public bool IsDirty => _dirty && HasUnsavedWorkSafely();

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
            _dirty = true;
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
        _dirty = false;
        _history = new UndoStack<ProjectDocument>(document);
        _history.Changed += (_, _) => Raise();

        // UNDO_24 — take back the history this project had when it was last closed.
        //
        // ⚠️ ORDERED AFTER the stack is constructed, because Restore replaces the BRANCHES and
        // leaves Current alone — Current must already be the document that was just applied to the
        // view-models, or the first Ctrl+Z would restore a state the screen does not show.
        UndoSidecar? history = _sidecar.Load(chosen, HistoryFingerprint(document));
        if (history is not null)
        {
            _history.Restore(history.Undo, history.Redo);
            RuntimeLog.Info("UNDO",
                $"Restored {history.Undo.Count} undo / {history.Redo.Count} redo step(s) from the sidecar (UNDO_24).");
        }

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
        // ══════════════════════════════════════════════════════════════════════════════════════
        // PROJSESSION_09 — THIS GUARD MUST NEVER TRAP THE USER IN THEIR OWN APPLICATION.
        //
        // It runs from MainWindow.OnClosing, BEFORE the try block that owns the rest of the
        // teardown, and OnClosing is `async void`. Anything that throws in here therefore escapes
        // to AppDomain.UnhandledException, the close is already cancelled, _isSafeToClose is never
        // set, and Close() is never re-posted — the window stays open and the next click on X
        // does exactly the same thing. That is an unclosable application, and it is what shipped:
        // showing a file picker on a window that is mid-close can throw, and every throw landed
        // in that hole.
        //
        // So: the whole body is guarded, and the failure direction is deliberate. A broken dialog
        // or a failed picker lets the close PROCEED rather than blocking it. Losing an unsaved
        // .fvsproj is bad; an application that cannot be closed without Task Manager is worse, and
        // the crash-recovery snapshot (05 §4 SYS-RECOVERY) still holds the session either way.
        // ══════════════════════════════════════════════════════════════════════════════════════
        try
        {
            if (!IsDirty) return true;

            bool save = await _notifier.ConfirmAsync(
                "Unsaved changes",
                $"You have changes that are not saved. {action} without saving them?",
                "Save first",
                "Discard changes");

            // ConfirmDialogWindow.AskAsync returns false for decline AND for a dialog that could
            // not be shown at all. Both mean "do not save", and neither may block the exit.
            if (!save) return true;

            // ⚠️ NOT SaveAsync(). On the close path a never-saved project would fall through to
            // SaveAsAsync and open a FILE PICKER on a window that is already mid-close. The picker
            // does not come up, returns null, the guard reports "not saved" and refuses the close
            // — and the next click on X does exactly the same. The user pressed Save and the
            // application would not shut down. SaveForExitAsync never shows a dialog.
            if (await SaveForExitAsync()) return true;

            // The write itself failed and has already reported as Fatal. Let the close proceed:
            // the user has been told, and holding the window open cannot un-fail the write.
            return true;
        }
        catch (Exception ex)
        {
            _faults.Recoverable("PROJECT",
            $"The unsaved-changes prompt failed during '{action}'; allowing it to proceed rather than "
            + $"blocking the window. {ex.GetType().Name}: {ex.Message}", ex);
            global::FortniteVideoSoftware.App.RuntimeLog.Swallowed(ex);   // FAULTTIER_02 — no failure is silent.
            return true;
        }
    }

    /// <summary>
    /// PROJSESSION_09 — a save that is guaranteed to finish without a dialog, for the close path.
    ///
    /// <para>
    /// An interactive Save As cannot run while the window is closing, so a project that has never
    /// been saved gets a filename derived from its source clip and written beside it. The user is
    /// told exactly where it went, which is the part that makes this acceptable: a file appearing
    /// somewhere they did not choose is only alarming if nobody says so.
    /// </para>
    ///
    /// <para>
    /// The name is made unique rather than overwriting. Two sessions closed on the same clip must
    /// not have the second silently destroy the first.
    /// </para>
    /// </summary>
    private Task<bool> SaveForExitAsync()
    {
        if (!string.IsNullOrWhiteSpace(CurrentPath))
            return Task.FromResult(WriteTo(CurrentPath!, announce: true));

        string? video = _viewModel.LoadedVideoPath;
        if (string.IsNullOrWhiteSpace(video))
        {
            // Nothing to derive a name from. IsDirty should already be false in this case
            // (HasUnsavedWork returns false with no clip loaded), so this is belt and braces.
            return Task.FromResult(true);
        }

        string? directory = Path.GetDirectoryName(video);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            directory = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";

        string stem = Path.GetFileNameWithoutExtension(video);
        string path = Path.Combine(directory!, stem + ProjectDocument.FileExtension);

        for (int i = 2; i <= 1000 && File.Exists(path); i++)
            path = Path.Combine(directory!, $"{stem} ({i}){ProjectDocument.FileExtension}");

        if (!WriteTo(path, announce: false)) return Task.FromResult(false);

        _notifier.Notify($"Saved to {Path.GetFileName(path)} next to your video.", NoticeKind.Success);
        return Task.FromResult(true);
    }

    /// <summary>
    /// PROJSESSION_08 — asks the application whether anything is at stake, and treats a failure to
    /// answer as "no". A predicate that throws must not be able to raise a save prompt, because
    /// that prompt is on the close path.
    /// </summary>
    private bool HasUnsavedWorkSafely()
    {
        try
        {
            return _hasUnsavedWork();
        }
        catch (Exception ex)
        {
            _faults.Recoverable("PROJECT", $"HasUnsavedWork check failed, assuming nothing to save: {ex.Message}", ex);
            global::FortniteVideoSoftware.App.RuntimeLog.Swallowed(ex);   // FAULTTIER_02 — no failure is silent.
            return false;
        }
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
            // PROJ_11 — the mask and the merge queue are part of the work, not of the machine.
            // Captured on every edit boundary so an undo step restores the mask the user had, not
            // whatever the shared profile file says at the moment they press Ctrl+Z.
            Mask = ReadLiveMaskSafely(),
            Merge = ReadMergeQueueSafely(),
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

        // PROJ_11 — OUTSIDE the _applying guard on purpose: this reports, it does not edit.
        ReportMaskDriftIfAny(document);

        _dirty = true;
        DocumentApplied?.Invoke(this, document);
        Raise();
    }

    /// <summary>
    /// PROJ_11 — SAY SO WHEN THE MASK THIS PROJECT WAS BUILT WITH IS NOT THE ONE THAT IS LIVE.
    ///
    /// <para>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// <b>THE DEFECT THIS CLOSES.</b> The HUD mask was a machine-wide setting
    /// (<c>SettingsManager.ActiveMaskOverlay</c> plus one shared <c>crop_coordinates.json</c>) and
    /// was in no way attached to a project. Open a montage from March today and the export runs
    /// through whatever mask is active NOW — different rectangles, different overlays, a visibly
    /// different video — and the application said nothing at all. The user's own saved record of
    /// their edit omitted the single setting that decides what the frame looks like.
    /// </para>
    ///
    /// <para>
    /// ⚠️ THIS DOES NOT SILENTLY SWITCH THE MASK BACK. Changing the machine's active profile
    /// because a file was opened would reach outside the document and alter the next project the
    /// user opens too — trading a silent wrong render for a silent wrong setting. The document
    /// holds the mask it was built with; the user is told what differs and decides.
    /// </para>
    ///
    /// <para>
    /// Degraded, not Fatal: nothing is broken and nothing is lost. Something the user can perceive
    /// has changed under them, and the notice names both halves — what differs, and what still
    /// works — as FAULTTIER_01 requires of this tier.
    /// </para>
    /// </summary>
    private void ReportMaskDriftIfAny(ProjectDocument document)
    {
        if (document.Mask is not { } saved) return;

        ProjectMask? live = ReadLiveMaskSafely();

        // No live mask to compare against is not drift — it is a session with no mask source
        // wired, which is the normal state in a test and during early startup.
        if (live is null) return;

        if (saved.MatchesLive(live.Config)) return;

        bool renamed = !string.Equals(saved.ProfileName, live.ProfileName, StringComparison.OrdinalIgnoreCase);

        _faults.Degraded("PROJECT",
            renamed
                ? $"This project was built with the \"{saved.ProfileName}\" HUD mask, but \"{live.ProfileName}\" "
                + "is active now — the export will look different. Editing and export still work; switch the "
                + "mask in the Crop Tool if you want the original look."
                : $"The \"{saved.ProfileName}\" HUD mask has been edited since this project was saved, so the "
                + "export will look different. Editing and export still work; the project remembers the mask "
                + "it was built with.",
            technicalDetail: $"saved fingerprint {saved.Fingerprint}, live {live.Fingerprint}");
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
        _dirty = false;
        _lastAutosaveUtc = _clock.UtcNow;

        // UNDO_24 — the history is written WITH the save, and fingerprinted against what was just
        // written. Saving is the moment the two are known to agree; writing the sidecar at any
        // other time risks a history that describes a document the file does not contain.
        if (_history is { } history)
            _sidecar.Save(path, HistoryFingerprint(history.Current), history);

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

    /// <summary>
    /// PROJ_11 — reading the live mask must never be able to fail a capture.
    ///
    /// <para>
    /// ⚠️ <see cref="Capture"/> is on the undo path. If reading the crop config threw — a locked
    /// file, a half-written profile, a mutex timeout — the exception would propagate out of
    /// <c>PushEdit</c> and the user's edit would not be recorded at all, which loses work in order
    /// to report a problem with an ancillary read. So the failure is classified and the capture
    /// continues with no mask, which is the same shape <see cref="ProbeMetricsSafely"/> already has
    /// for ffprobe.
    /// </para>
    ///
    /// <para>
    /// Degraded, not Recoverable: the saved project will not carry the mask it was built with, and
    /// that is a difference the user can perceive the next time they open it.
    /// </para>
    /// </summary>
    /// <summary>
    /// UNDO_24 — identifies WHICH VERSION of a project a history belongs to.
    ///
    /// <para>
    /// Built from the source clip and the edit that produced the saved state. A project edited on
    /// another machine, or restored from a backup, produces a different fingerprint and its stale
    /// history is discarded rather than replayed — replaying it would walk the user back into a
    /// document that never existed on this timeline, silently.
    /// </para>
    /// </summary>
    private static string HistoryFingerprint(ProjectDocument document)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{document.Source.FilePath}|{document.Source.SizeBytes}|{document.Source.ModifiedUtcSeconds}|{document.SourceCutStartMs:F3}|{document.TrimmedDurationMs:F3}|{document.Segments.Count}|{document.Cuts.Count}|{document.Memes.Count}");

    private ProjectMask? ReadLiveMaskSafely()
        => _faults.GuardValue<ProjectMask?>(
            "PROJECT",
            "The HUD mask could not be read, so this save will not record which mask you were using. "
          + "Your edit is saved and export still works — reopening will just use whichever mask is active then.",
            _readLiveMask,
            fallback: null);

    private ProjectMerge? ReadMergeQueueSafely()
        => _faults.GuardValue<ProjectMerge?>(
            "PROJECT",
            "The merge queue could not be read, so this save will not record the clips you queued. "
          + "Everything else in the project is saved.",
            _readMergeQueue,
            fallback: null);

    private (int Width, int Height, double Fps) ProbeMetricsSafely()
        => _faults.GuardValue("PROJECT",
            "The video's size and frame rate could not be read, so they were left out of the saved project.",
            _probeVideoMetrics,
            (0, 0, 0.0),
            FaultTier.Recoverable);

    private void Raise() => StateChanged?.Invoke(this, EventArgs.Empty);
}
