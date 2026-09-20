using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace FortniteVideoSoftware.App;

/// <summary>
/// Decides WHICH cue a clicked control gets, if any.
///
/// ======================================================================================
/// AUDIT ROUND 5 — WHY THIS CLASS EXISTS
/// ======================================================================================
/// The routing used to be an if/else chain inside AvaloniaApp.Initialize that sniffed the
/// control's STYLE CLASSES. That coupled audio meaning to visual appearance, and the two do
/// not mean the same thing in this suite:
///
///   AUDIO_07  `Classes.Contains("Success")` -> the 1.385 s export-complete fanfare. Green is
///             this app's "forward action" colour, so that fanfare fired on NextBtn (a wizard
///             step advance), on YesBtn in two warning dialogs, on DownloadSongsBtn, on
///             SaveBtn... roughly ten times a session, always before anything was finished.
///             The Success branch is GONE. Process is now fired only by FinishedDialogWindow.
///
///   AUDIO_04  `Classes.Contains("Danger")` -> the 1.542 s "close" clip. But the suite's
///             destructive pattern is a red button whose ONLY job is to open a confirm flyout.
///             So you heard the sound of the thing being destroyed, were then asked whether to
///             destroy it, and heard the identical clip a second time on CONFIRM. Now handled
///             STRUCTURALLY: a Button that owns a Flyout has not done anything yet, so it gets
///             the short tick; the heavy clip belongs to the Confirm* button that commits.
///
///   AUDIO_10  `name == "UndoButton"` -> Close, while RedoButton matched nothing and got the
///             48 ms tick: a mirrored pair with wildly different audio. Neither is special
///             now, so both land on the same default tick. RdpFixButton APPLIES a system fix
///             but is styled red, so it used to sound like a cancellation; it is listed
///             explicitly below as an apply action.
///
///   AUDIO_11  `Classes.Contains("DangerOutline")` matched a class that exists nowhere in the
///             solution, and the `ProcessButton`/`SaveButton` name checks were already covered
///             by the Success class test on the same line - while `SaveBtn` (the one that
///             actually needed covering) was missing. All dead logic is gone.
///
///   AUDIO_09  The handler was registered on Button.ClickEvent only, so all 17 MenuItems in the
///             Main App and Merger were silent - including menu entries that are the SAME
///             action as a button that did make a sound (MenuVideoMerger vs VideoMergerButton).
///             This class resolves MenuItem too.
///
/// ORDER IS SIGNIFICANT. Read <see cref="Resolve"/> top to bottom. In particular the flyout
/// rule must run BEFORE any name list, because "CancelBtn" is used by BOTH SettingsWindow (a
/// harmless Secondary close) and MusicWizardWindow (a red flyout-guarded discard) - the two
/// can only be told apart structurally.
/// </summary>
internal static class UiSoundRouter
{
    /// <summary>
    /// AUDIO_01: never make a sound for these, whatever they are styled as.
    /// MicRecordButton is Danger-red, so the old class sniffing played a 1.542 s clip through
    /// the speakers at the instant the microphone opened. VoiceOverWindow ALSO wraps recording
    /// in UiSoundEffect.Suppress(); this list is the belt to that braces, and covers the press
    /// that STARTS the take (before the suppression scope exists).
    /// </summary>
    private static readonly HashSet<string> Silent = new(StringComparer.Ordinal)
    {
        "MicRecordButton"
    };

    /// <summary>Trim in/out points. The Granular editor's MarkStartBtn/MarkEndBtn were missing
    /// from the old list, so the same gesture blipped in the Main App and clicked in Granular.</summary>
    private static readonly HashSet<string> MarkNames = new(StringComparer.Ordinal)
    {
        "MarkStartButton", "MarkEndButton",
        "MarkStartBtn", "MarkEndBtn"
    };

    /// <summary>Something that opens a tool window or sub-application. The Menu* entries are the
    /// AUDIO_09 fix: the same action reached from the menu now sounds like the button.</summary>
    private static readonly HashSet<string> OpenNames = new(StringComparer.Ordinal)
    {
        "VideoMergerButton", "CropSettingsButton", "GranularButton",
        "AddMusicButton", "VoiceOverButton", "DetachOverlayButton",
        "MenuVideoMerger", "MenuCropSettings", "MenuSettingsBtn", "MenuSettings",
        "MenuTogglePreviewMonitor"
    };

    /// <summary>
    /// AUDIO_10: actions that APPLY something but are styled red, so the class test used to give
    /// them the cancel/close clip. Listed before the Danger fallback so their styling no longer
    /// dictates their meaning.
    /// </summary>
    private static readonly HashSet<string> ApplyNames = new(StringComparer.Ordinal)
    {
        "RdpFixButton"
    };

    /// <summary>
    /// Genuine "this window/tool is going away" actions. Deliberately an EXPLICIT list rather
    /// than the old `name.Contains("Close")` substring test, which caught HelpCloseButton - the
    /// "GOT IT" button on a help card - and gave dismissing a tooltip the same 1.542 s weight as
    /// deleting a file.
    /// </summary>
    private static readonly HashSet<string> CloseNames = new(StringComparer.Ordinal)
    {
        "MenuExit", "MenuReturnToApp",
        "ReturnButton", "ReturnToMainAppButton",
        "CloseButton", "CancelButton", "CancelBtn"
    };

    /// <summary>
    /// Returns the cue for a clicked control, or null for silence.
    /// </summary>
    public static UiCue? Resolve(object? sender)
    {
        if (sender is not Control control) return null;

        string name = control.Name ?? string.Empty;

        if (name.Length > 0 && Silent.Contains(name)) return null;

        if (control is MenuItem mi && mi.ItemCount > 0) return null;

        if (control is Button btn && btn.Flyout != null) return UiCue.Click;

        if (name.StartsWith("Confirm", StringComparison.Ordinal)) return UiCue.Close;

        if (name.Length > 0)
        {
            if (MarkNames.Contains(name)) return UiCue.Mark;
            if (OpenNames.Contains(name)) return UiCue.Open;
            if (ApplyNames.Contains(name)) return UiCue.Click;
            if (CloseNames.Contains(name)) return UiCue.Close;
        }

        if (control.Classes.Contains("Danger")) return UiCue.Close;

        return UiCue.Click;
    }
}
