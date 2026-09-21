// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/07_UNDO_AND_HISTORY.md, docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.App;

/// <summary>
/// UNDO_25 — the Granular editor's history, and the part of it that outlives the window.
///
/// <para>
/// ⚠️ IN ITS OWN FILE BECAUSE MVVM_02 SAYS SO. The code-behind is grandfathered at a ceiling that
/// may only ever fall, and adding ninety lines to the largest file in the repository — however
/// good the ninety lines are — is the thing that rule exists to stop. New behaviour goes in a new
/// file.
/// </para>
/// </summary>
public partial class GranularSpeedEditorWindow
{
    /// <summary>
    /// UNDO_25 — THE HISTORY OUTLIVES THE WINDOW THAT MADE IT.
    ///
    /// <para>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// <b>THE DEFECT THIS CLOSES.</b> <c>07_UNDO_AND_HISTORY.md</c> §5 states it plainly: "until it
    /// moves, closing that window still discards history." A user spent ten minutes on speed ramps
    /// and zoom boxes, closed the editor to look at the main timeline, reopened it, pressed Ctrl+Z
    /// — and nothing happened, because <c>OnClosed</c> had called <c>ClearUndoHistory</c>. Nothing
    /// warned them on the way out. The most patient, most detailed work in the application had the
    /// least durable undo in it.
    /// </para>
    ///
    /// <para>
    /// ⚠️ KEYED BY CLIP PATH, AND THAT IS LOAD-BEARING. Restoring history into a DIFFERENT clip
    /// would let a Ctrl+Z apply segment boundaries measured against another video's duration —
    /// silently producing an edit the user never made, on footage they never made it on. That is
    /// strictly worse than losing the history, so a key mismatch drops it without hesitation.
    /// </para>
    ///
    /// <para>
    /// ⚠️ PROCESS-LIFETIME ONLY, DELIBERATELY. This survives closing the WINDOW, not closing the
    /// APP: cross-restart history is the sidecar's job (<c>UndoSidecarStore</c>, UNDO_24) and it
    /// stores <c>ProjectDocument</c>, which is the portable shape. An <c>EditorSnapshot</c> carries
    /// the editor's own working state, including the freeze and the selected segment, which has no
    /// meaning outside a live editor window.
    /// </para>
    ///
    /// <para>
    /// ⚠️ ONE SLOT, NOT A DICTIONARY. Holding history for every clip ever opened is an unbounded
    /// leak of ~40KB per clip in a process that also holds decoded video frames. The user edits one
    /// clip at a time; a second clip's editor replaces the slot, which is the same forgetting the
    /// old code did — only now it takes opening a different clip rather than closing a window.
    /// </para>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    private static (string Key, List<EditorSnapshot> Undo, List<EditorSnapshot> Redo)? _parkedHistory;

    /// <summary>UNDO_25 — normalised identity for the clip this editor is editing.</summary>
    private string HistoryKey =>
        string.IsNullOrWhiteSpace(_videoPath) ? string.Empty : Path.GetFullPath(_videoPath).ToUpperInvariant();

    /// <summary>
    /// UNDO_25 — takes back the history this clip had when its editor was last closed.
    /// Called once, after the window has seeded its own state, so a restored stack describes the
    /// same starting point the user left.
    /// </summary>
    private void AdoptParkedHistory()
    {
        if (_parkedHistory is not { } parked) return;
        if (string.IsNullOrEmpty(HistoryKey) || !string.Equals(parked.Key, HistoryKey, StringComparison.Ordinal))
            return;

        _undoStack.Clear();
        _undoStack.AddRange(parked.Undo);
        _redoStack.Clear();
        _redoStack.AddRange(parked.Redo);

        // U2 — the ceiling is re-applied on adoption. A cap enforced only on push is a cap that a
        // second code path can walk straight past.
        while (_undoStack.Count > MaxUndoDepth) _undoStack.RemoveAt(0);
        while (_redoStack.Count > MaxUndoDepth) _redoStack.RemoveAt(0);

        RuntimeLog.Info("UNDO",
            $"Restored the editor history for this clip: {_undoStack.Count} undo, {_redoStack.Count} redo (UNDO_25).");

        RefreshUndoRedoButtons();
    }

    /// <summary>
    /// UNDO_25 — parks the history on the way out, in place of throwing it away.
    /// An empty history clears the slot rather than parking an empty one, so reopening a clip that
    /// genuinely has no history does not resurrect a stale slot belonging to another.
    /// </summary>
    private void ParkHistoryForReopen()
    {
        if (string.IsNullOrEmpty(HistoryKey)) { _parkedHistory = null; return; }

        if (_undoStack.Count == 0 && _redoStack.Count == 0)
        {
            if (_parkedHistory?.Key == HistoryKey) _parkedHistory = null;
            return;
        }

        _parkedHistory = (HistoryKey, new List<EditorSnapshot>(_undoStack), new List<EditorSnapshot>(_redoStack));
        RuntimeLog.Info("UNDO",
            $"Parked {_undoStack.Count} undo / {_redoStack.Count} redo state(s) for when this clip is reopened (UNDO_25).");
    }
}
