// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/06_PROJECT_DOCUMENT_MODEL.md
// Forbidden to modify without reading: docs/07_UNDO_AND_HISTORY.md
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using FortniteVideoSoftware.Core.Project;

namespace FortniteVideoSoftware.App;

/// <summary>
/// PROJSESSION_01 — the main window's half of the document session.
///
/// <para>
/// Kept in its own partial rather than added to the 3,300-line <c>MainWindow.axaml.cs</c>. That
/// file is co-governed by four specs and is one of the two the view-model extraction phase will
/// rewrite; new document code lands here so the diff for that rewrite stays legible.
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// The metrics the document stores so a project opens into a drawn timeline rather than into a
    /// spinner (PROJ_05). Resolved at call time, not captured, because the detach controller
    /// (DETACH_01) can swap the video host underneath us.
    /// </summary>
    private (int Width, int Height, double Fps) ProbeVideoMetricsForProject()
    {
        var ipc = ActiveVideoHost?.IpcClient;
        if (ipc == null) return (0, 0, 0.0);
        return (ipc.VideoWidth, ipc.VideoHeight, ipc.VideoFps);
    }

    /// <summary>
    /// PROJSESSION_05 — the title bar is the ONLY continuous signal that unsaved work exists.
    ///
    /// <para>
    /// A dirty marker in the caption is the convention every editor uses, and its absence is why
    /// closing this window never felt like it was discarding anything. The version string stays,
    /// because <c>SYS-AUTOUPDATE</c> makes the title the place a user reads their build number.
    /// </para>
    /// </summary>
    private void RefreshProjectTitle()
    {
        if (_projectSession is null) return;

        string version = DeploymentLifecycle.GetCurrentVersion();
        string name = string.IsNullOrWhiteSpace(_projectSession.CurrentPath)
            ? "Untitled project"
            : System.IO.Path.GetFileNameWithoutExtension(_projectSession.CurrentPath);

        string dirty = _projectSession.IsDirty ? " •" : string.Empty;

        Title = $"{name}{dirty} — Fortnite Video Software v{version}";
    }

    /// <summary>
    /// Called after a document has been written into the view-models — by Open, Undo or Redo.
    /// The view-models now hold different segments, cuts and memes than the screen is drawing, so
    /// everything derived from them has to be rebuilt.
    ///
    /// <para>
    /// ⚠️ UNDO_22 — the PLAYHEAD IS NOT MOVED. 04 §6 (UI-GRANULAR) states it for the granular
    /// editor's stack and the rule generalises: undo restores the EDIT, not the viewing position.
    /// Seeking on undo makes a sequence of undos feel like the video is being scrubbed by someone
    /// else, and it costs an mpv seek per step (SEEKSTORM_01).
    /// </para>
    /// </summary>
    private void OnProjectDocumentApplied()
    {
        var services = Infrastructure.AppServices.Current;

        services.Faults.Guard("PROJECT",
            "The timeline could not be redrawn after that change. Your edits are intact — switching " +
            "views or reloading the project will refresh it.",
            () =>
            {
                UpdateTimelineMarkers();
                QueueTimelineRedrawPass(Avalonia.Threading.DispatcherPriority.Render);

                _viewModel.Timeline.NormalizeCutsInPlace();
                _viewModel.Timeline.UpdateFormattedTimes();

                if (_viewModel.Timeline.MemePlacements.Count > 0)
                {
                    EnsureMemePreviewDirector();
                    _memePreview?.SetMemes(_viewModel.Timeline.MemePlacements);
                }

                RequestSizeEstimate();
            });
    }

    /// <summary>
    /// PROJSESSION_06 — starts a fresh undo history for a newly loaded clip.
    ///
    /// <para>
    /// History does NOT survive a video change, deliberately. Every segment, cut and meme in the
    /// stack is expressed in milliseconds into a specific file; undoing across a load would restore
    /// positions that index into footage that is no longer open, and the result would be a timeline
    /// that looks plausible and is wrong. A discarded history is recoverable by the user; a
    /// silently mis-mapped one is not.
    /// </para>
    /// </summary>
    private void BeginProjectHistory()
    {
        _projectSession?.BeginHistory();
        RefreshProjectTitle();
    }

    /// <summary>
    /// UNDO_20 — records an edit. The single call the rest of the window uses.
    ///
    /// <para>
    /// <paramref name="gestureKey"/> coalesces a continuous gesture into one undo step. Pass the
    /// same key for every tick of a drag or slider sweep and call <see cref="EndProjectGesture"/>
    /// on release; without it a single trim drag leaves forty entries on the stack and Ctrl+Z
    /// appears not to work because each press moves the handle one pixel.
    /// </para>
    /// </summary>
    private void PushProjectEdit(string label, string? gestureKey = null)
        => _projectSession?.PushEdit(label, gestureKey);

    /// <summary>Closes the coalescing window opened by a gesture-keyed <see cref="PushProjectEdit"/>.</summary>
    private void EndProjectGesture() => _projectSession?.EndGesture();

    /// <summary>
    /// PROJSESSION_03 — autosave pulse. Cheap: returns immediately unless the project is dirty,
    /// has a path, and the interval has elapsed.
    /// </summary>
    private void ProjectAutosaveTick() => _projectSession?.AutosaveTick();

    /// <summary>
    /// The close guard. Returns false to keep the window open.
    ///
    /// <para>
    /// ⚠️ Wired INSIDE the deferred-close contract (05 §3 SYS-WINSTATE): <c>OnClosing</c> already
    /// sets <c>e.Cancel = true</c> and re-posts <c>Close()</c> on the next dispatcher turn. This
    /// guard runs before <c>_isSafeToClose</c> is set, so a "cancel" here simply means the re-post
    /// never happens — the window stays open and stays functional, rather than being hidden with
    /// its work still in memory.
    /// </para>
    /// </summary>
    private Task<bool> ConfirmProjectDiscardOnCloseAsync()
        => _projectSession?.ConfirmDiscardAsync("Close") ?? Task.FromResult(true);
}
