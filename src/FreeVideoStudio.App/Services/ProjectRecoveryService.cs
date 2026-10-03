// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Text.Json.Nodes;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Project;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// RECOVERYDOC_04 — THE RECOVERY BRIDGE, NOT A SECOND PROJECT SCHEMA.
///
/// <para>
/// This service used to OWN a parallel, hand-maintained 60-field JSON model of the user's project
/// (<c>SerializeState</c> + a matching field-by-field restore). Every project feature therefore
/// needed two serialisations, and every one that was forgotten was silently lost on crash
/// recovery. That schema is GONE.
/// </para>
///
/// <para>
/// The recovery file now holds exactly one <see cref="ProjectDocument"/> — captured from
/// ProjectSession.Capture, serialised by the canonical <see cref="ProjectSerializer"/> inside
/// <see cref="RecoveryEnvelope"/> — plus crash-only transient metadata (see
/// <see cref="RecoveryEnvelope.Write"/> for what qualifies). Disk writes still go through
/// <see cref="RecoveryManager"/>, which keeps the atomic write protocol (05 §4 SYS-ATOMICWRITE)
/// and the WRITEORDER_01 save/clear ordering, and still preserves an active
/// <c>granular_session</c> sub-object across app-level saves (SYS-RECOVERY).
/// </para>
///
/// <para>
/// ARCHITECTURE GUARD: do not reintroduce project fields here or in the envelope. If a project
/// property is missing from recovery, it is missing from <see cref="ProjectDocument"/> — fix it
/// THERE, once, and both normal saves and recovery gain it.
/// </para>
/// </summary>
public sealed class ProjectRecoveryService
{
    private readonly RecoveryManager _recovery;
    private Func<ProjectDocument>? _captureCurrentDocument;
    private Func<JsonObject?>? _captureTransientMetadata;

    public ProjectRecoveryService(ApplicationPaths? paths = null)
    {
        _recovery = new RecoveryManager(paths ?? ApplicationPaths.CreateDefault());
    }

    /// <summary>
    /// RECOVERYDOC_05 — wires the ONE canonical capture. Called once at composition (MainWindow)
    /// with ProjectSession.Capture(forExplicitSave: false) so a recovery autosave is the SAME
    /// projection the normal project save uses. Without a wired source there is deliberately NO
    /// writer: nothing can silently fall back to a second state model that drifts from the document.
    /// </summary>
    public void WireDocumentSource(Func<ProjectDocument> captureDocument, Func<JsonObject?>? captureTransient)
    {
        _captureCurrentDocument = captureDocument ?? throw new ArgumentNullException(nameof(captureDocument));
        _captureTransientMetadata = captureTransient;
    }

    /// <summary>
    /// RECOVERYDOC_06 — the ONLY recovery write. Persists the canonical document captured from the
    /// live session inside the <see cref="RecoveryEnvelope"/>. Atomic, ordered, debounced by the
    /// caller (EDITHOT_02) — none of that changed.
    /// </summary>
    public void SaveState(ProjectDocument document, JsonObject? transientMetadata, bool sync = false)
    {
        var state = RecoveryEnvelope.Write(document, DateTimeOffset.UtcNow, transientMetadata);
        if (sync)
            _recovery.SaveState(state);
        else
            _recovery.SaveStateAsync(state);
    }

    /// <summary>
    /// Persists the CURRENT live session through the wired canonical capture. Used by paths that
    /// hold no window reference; a no-op when no source was wired, never a hand-built payload.
    /// </summary>
    public void SaveCurrentState(bool sync = false)
    {
        if (_captureCurrentDocument is not { } capture) return;
        ProjectDocument document = capture();
        if (!RecoveryEnvelope.HasContent(document)) return;
        SaveState(document, _captureTransientMetadata?.Invoke(), sync);
    }

    /// <summary>
    /// RECOVERYDOC_07 — loads the recovery snapshot. Legacy schemaVersion-1 payloads (written
    /// before the canonical envelope) are returned with <see cref="RecoverySnapshot.IsLegacy"/> set
    /// and <see cref="RecoverySnapshot.LegacyRaw"/> populated; <see cref="LegacyRecoveryMigrator"/>
    /// converts them once and the next autosave writes the canonical format. Nothing here WRITES
    /// the legacy format — there is one writer, and it is the envelope.
    /// </summary>
    public RecoverySnapshot? LoadSnapshot()
    {
        JsonObject? raw = _recovery.LoadState();
        if (raw is null) return null;

        if (RecoveryEnvelope.IsEnvelope(raw))
        {
            if (!RecoveryEnvelope.TryRead(raw, out ProjectDocument? document, out JsonObject? transient, out string? error))
            {
                CoreLogger.Fail("Recovery", $"Recovery envelope rejected: {error}");
                return null;
            }
            return new RecoverySnapshot(document, transient, raw, IsLegacy: false, LegacyRaw: null);
        }

        return new RecoverySnapshot(null, null, raw, IsLegacy: true, LegacyRaw: raw);
    }


    public void ClearState() => _recovery.ClearState();

    public void ReleaseLockOnly() => _recovery.ReleaseLockOnly();

    /// <summary>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// "Is there anything here a user would be upset to lose?"
    ///
    /// Two consumers, and they pull in the same direction: it decides whether a recovery snapshot
    /// is worth writing, and (SWITCHPROMPT_01) whether the Video Merger / Crop Tools hand-off is
    /// worth interrupting with a confirmation.
    ///
    /// ⚠️ A LOADED VIDEO IS NOT WORK. Uploading a clip and touching nothing else must return false.
    ///
    /// SWITCHPROMPT_01 — TWO REASONS THIS RETURNED TRUE ON A COMPLETELY EMPTY EDITOR, which is why
    /// the switch prompt fired with nothing loaded:
    ///
    ///   1. The toggle comparisons were against HARDCODED assumptions — portrait on, fade on,
    ///      speed 1.1 — rather than against the defaults the user actually configured in
    ///      Settings → Defaults. Anyone who set "Portrait Mode: off", or a default speed of 1.0,
    ///      had a session that was born dirty: the very first frame after startup already
    ///      disagreed with the constants below, with no video and no edit in sight. Every one of
    ///      those comparisons now reads the SAME defaults ApplyDefaults() seeds the session from,
    ///      so "unchanged from where the app put it" reliably means unchanged.
    ///
    ///   2. Nothing checked whether a video was loaded at all. With no clip there is no project:
    ///      no trim to keep, nothing to recover, nothing to warn about. That is now the first
    ///      question asked, and it is the guard that makes the empty-editor case impossible
    ///      regardless of what any toggle says.
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// Unchanged in behaviour: this is a LIVE-SESSION predicate (docs/06 §8 — the close-path
    /// guard, SWITCHPROMPT_01), never a recovery-JSON reader. It answers "does the CURRENT editor
    /// differ from its defaults"; the persisted document's emptiness backstop is
    /// <see cref="RecoveryEnvelope.HasContent"/>.
    /// </summary>
    public bool HasUnsavedWork(MainViewModel mainVm, TimelineViewModel timelineVm, ExportViewModel exportVm)
    {
        if (mainVm.ExportedCleanSinceLastEdit) return false;

        // No clip, no project. Nothing below can be true in a way that matters.
        if (string.IsNullOrWhiteSpace(mainVm.LoadedVideoPath)) return false;

        var defaults = SettingsManager.Instance.Defaults;

        bool memeSelected = mainVm.IsAddMeme || mainVm.SelectedMemeItem != null;

        // SPECTATINGDEFAULT_01 — the eye starts on, so turning it off is an edit.
        // No Mask forces both controls off; that profile's initial state is still clean.
        bool noMask = MaskOverlayManager.IsNoMask(SettingsManager.Instance.ActiveMaskOverlay);
        bool hudToggled = mainVm.IsTeammates != (!noMask && defaults.ShowTeammates)
                       || mainVm.IsSpectating != !noMask;

        bool exportTogglesChanged = mainVm.IsPortraitMode != defaults.PortraitMode
                                 || mainVm.IsEnableFade != defaults.EnableFade;

        bool speedChanged = Math.Abs(timelineVm.BaseSpeed - defaults.DefaultSpeed) > 0.01;

        return timelineVm.IsTrimStartSet || timelineVm.IsTrimEndSet || timelineVm.IsThumbnailSet ||
               mainVm.IsGranularSpeedActive || mainVm.IsMusicActive || mainVm.VoiceOverResult != null ||
               mainVm.MusicWizardResult != null ||
               timelineVm.Cuts.Count > 0 ||
               timelineVm.MemePlacements.Count > 0 ||
               timelineVm.SpeedSegments.Count > 0 || timelineVm.FreezeTimeMs >= 0 ||
               speedChanged ||
               memeSelected || hudToggled || exportTogglesChanged ||
               !string.IsNullOrWhiteSpace(mainVm.OverlayText);
    }
}

/// <summary>
/// RECOVERYDOC_07 — one loaded recovery snapshot. Exactly one of the two shapes is present: a
/// canonical envelope (<see cref="Document"/> + <see cref="TransientMetadata"/>) or a legacy
/// pre-envelope payload (<see cref="LegacyRaw"/>, converted once by
/// <see cref="LegacyRecoveryMigrator"/>). <see cref="Raw"/> is the untouched file root — kept only
/// for crash-only sub-objects that live beside the envelope (the granular editor's
/// <c>granular_session</c>, merged there by RecoveryManager per SYS-RECOVERY). It is NEVER a
/// second project model.
/// </summary>
public sealed record RecoverySnapshot(
    ProjectDocument? Document,
    JsonObject? TransientMetadata,
    JsonObject Raw,
    bool IsLegacy,
    JsonObject? LegacyRaw);
