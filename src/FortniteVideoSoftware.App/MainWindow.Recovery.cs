// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Forbidden to modify without reading: docs/07_UNDO_AND_HISTORY.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.App;

/// <summary>
/// EDITHOT_02 — the main window's edit hook (undo push + crash-recovery write-behind). It was moved
/// out of MainWindow.axaml.cs under MVVM_02 ("new behaviour goes in a new file"). The call sites
/// and the UNDO_20 / RECOVERY_03-04 contracts documented on HasUnsavedWork are unchanged.
/// </summary>
public partial class MainWindow
{
    /// <param name="label">UNDOEQ_02: the action, as the undo menu will name it ("change speed").</param>
    /// <param name="gestureKey">
    /// UNDOEQ_02: pass the SAME key on every tick of one continuous control (a dial sweep, a key
    /// repeat) so U1 collapses the whole gesture into ONE undo step. End it explicitly with
    /// EndProjectGesture() on release. Null for discrete actions (clicks, toggles, releases).
    /// </param>
    private void SaveRecoveryState(bool sync = false, bool isUserEdit = true, string label = "edit", string? gestureKey = null)
    {
        if (_isRestoring) return;
        if (isUserEdit) UpdateEstimatedQuality();

        // ══════════════════════════════════════════════════════════════════════════════════════
        // UNDO_20 / PROJSESSION_03 — ONE HOOK FOR THE WHOLE EDIT SURFACE.
        //
        // This method is already wired to ~25 UI events and already means exactly "the user
        // changed something", already honours _isRestoring, and already distinguishes a genuine
        // edit from a bookkeeping save via isUserEdit. Every one of those properties is a
        // precondition the undo stack needs, so hooking here gives the main window undo across its
        // whole surface in one place instead of 25 hand-placed PushEdit calls that the 26th edit
        // would then forget.
        //
        // The label is generic because this hook cannot know which control fired. A specific label
        // is better UX and belongs at the individual call sites; a generic label that works
        // everywhere beats a specific one that covers a third of the surface.
        //
        // ⚠️ ORDER: push BEFORE the HasUnsavedWork early-return below. That return fires when the
        // user has just UNDONE their way back to an empty project — which is itself a state the
        // redo stack must be able to come back from.
        // ══════════════════════════════════════════════════════════════════════════════════════
        if (isUserEdit)
        {
            PushProjectEdit(label, gestureKey);
            ProjectAutosaveTick();
        }

        if (isUserEdit && _exportedCleanSinceLastEdit)
        {
            _exportedCleanSinceLastEdit = false;
            RuntimeLog.Info("RECOVERY", "Edit made after a successful export - project is dirty again, recovery re-armed.");
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // EDITHOT_02 — THE CRASH-RECOVERY SNAPSHOT IS WRITE-BEHIND, NOT PER TICK.
        //
        // This hook fires on every notch of the speed and quality dials. It used to serialise the
        // full recovery document and queue a WriteThrough + flush + File.Move for EACH notch, so
        // a dial sweep produced one durable disk write per notch. The undo push above stays
        // immediate (memory only). The disk snapshot is coalesced: it is written once, 750ms after
        // the last change. `sync: true` callers (companion-tool hand-off, shutdown paths) still
        // write immediately, because they are about to leave and cannot wait for the timer.
        // Crash exposure is at most the last 750ms of edits, the same trade ISSUE_104 already made
        // for the volume slider.
        // ══════════════════════════════════════════════════════════════════════════════════════
        if (sync)
        {
            _recoveryWriteBehindTimer?.Stop();
            PersistRecoveryStateNow(sync: true);
            return;
        }

        if (_recoveryWriteBehindTimer == null)
        {
            _recoveryWriteBehindTimer = new Avalonia.Threading.DispatcherTimer(Avalonia.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(RecoveryWriteBehindMs)
            };
            _recoveryWriteBehindTimer.Tick += (_, _) =>
            {
                _recoveryWriteBehindTimer!.Stop();
                if (_isRestoring) return;
                PersistRecoveryStateNow(sync: false);
            };
        }
        _recoveryWriteBehindTimer.Stop();
        _recoveryWriteBehindTimer.Start();
    }

    /// <summary>EDITHOT_01 — the volume preference write, off the UI thread. Never throws.</summary>
    private async Task PersistMainVolumeAsync(double volume)
    {
        try
        {
            await new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths)
                .UpdatePropertiesAsync(new System.Text.Json.Nodes.JsonObject { ["MainVolume"] = volume })
                .ConfigureAwait(false);
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private const int RecoveryWriteBehindMs = 750;
    private Avalonia.Threading.DispatcherTimer? _recoveryWriteBehindTimer;

    /// <summary>
    /// EDITHOT_02 — the disk half of <see cref="SaveRecoveryState"/>: decide between clear and
    /// save, then hand the snapshot to the recovery writer. Snapshot building reads the view-models,
    /// so it runs on the UI thread, but only once per settled edit, never per tick.
    /// </summary>
    private void PersistRecoveryStateNow(bool sync)
    {
        try
        {
            if (!HasUnsavedWork())
            {
                // WRITEORDER_01 — through the SAME ordered writer as the saves, so a save that
                // was queued before this cannot land after it and resurrect the file.
                _recoveryService.ClearState();
                return;
            }

            RuntimeLog.Info("RECOVERY",
                $"Saving project state: trim[{_trimStartMs:F0}-{_trimEndMs:F0}ms set={_trimStartSet}/{_trimEndSet}] " +
                $"speed[base={_baseSpeed:F2}x segs={_speedSegments.Count}] " +
                $"freeze[at={_freezeTimeMs:F0}ms for={_freezeDurationS:F2}s] " +
                $"cuts[{_cuts.Count} removing {RemovedCutSeconds():F2}s] " +
                $"thumb[set={_thumbnailSet} at={_thumbnailPosMs:F0}ms] " +
                $"voiceOver[{(_voiceOverResult != null ? "yes" : "no")}] " +
                $"music[{(_musicWizardResult != null ? "yes" : "no")}].");

            var state = _recoveryService.SerializeState(_viewModel, _viewModel.Timeline, _viewModel.Export);
            _recoveryService.SaveState(state, sync);
        }
        catch (System.Exception ex)
        {
            RuntimeLog.Fail("RECOVERY", ex);
        }
    }
}
