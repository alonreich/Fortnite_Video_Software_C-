// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Avalonia.Input;

namespace FreeVideoStudio.App.Infrastructure;

public enum ThemeMode
{
    FollowOS,
    Dark,
    Light
}

public enum FontScale
{
    ExtraSmall,
    Small,
    Medium,
    Normal,
    Large,
    ExtraLarge
}

public static class FontScaleExtensions
{
    public static double ToMultiplier(this FontScale scale) => scale switch
    {
        FontScale.ExtraSmall => 0.80,
        FontScale.Small => 0.90,
        FontScale.Medium => 0.95,
        FontScale.Normal => 1.00,
        FontScale.Large => 1.12,
        FontScale.ExtraLarge => 1.25,
        _ => 1.00
    };
}

public class AppSettings
{
    /// <summary>
    /// Bumped whenever a field is renamed/removed or its meaning changes. <see cref="SettingsManager.Load"/>
    /// uses it to migrate instead of silently reverting the user to defaults. A file with a
    /// HIGHER version than this build understands is left untouched on disk and loaded
    /// best-effort, so downgrading the app never destroys a newer config.
    /// </summary>
    public int SchemaVersion { get; set; } = SettingsManager.CurrentSchemaVersion;

    public KeyBinds KeyBinds { get; set; } = new();
    public DefaultValues Defaults { get; set; } = new();
    public int Volume { get; set; } = 100;
    public string ActiveMaskOverlay { get; set; } = "Fortnite";

    /// <summary>
    /// AI SMART ZOOM — Google Gemini API key used for universal subject vision tracking.
    /// Default empty. Configured via Settings › AI Tracking or the First-Time Setup Wizard.
    /// </summary>
    public string GeminiApiKey { get; set; } = "";

    /// <summary>AI SMART ZOOM — Gemini vision model name (default "gemini-2.5-flash").</summary>
    public string GeminiModelName { get; set; } = "gemini-2.5-flash";

    /// <summary>AI SMART ZOOM — Base tight zoom multiplier (default 2.2x, range 1.5x - 4.0x).</summary>
    public double AiZoomBaseScale { get; set; } = 2.2;

    /// <summary>AI SMART ZOOM — Minimum wide zoom multiplier during fast motion (default 1.3x, range 1.1x - 2.0x).</summary>
    public double AiZoomMinScale { get; set; } = 1.3;

    /// <summary>AI SMART ZOOM — Whether to avoid active HUD overlay areas when framing the subject.</summary>
    public bool AiZoomAvoidHud { get; set; } = true;

    /// <summary>AI SMART ZOOM — Percentage of frame width deadband to filter out micro-jitter on still targets (default 2.0%).</summary>
    public double AiZoomDeadbandPercent { get; set; } = 2.0;

    /// <summary>
    /// ISSUE_04 — Main App export destination. Empty means "resolve the real Downloads folder
    /// at export time". Once the user is asked to pick a location (because Downloads is
    /// missing/unwritable) the chosen folder is stored here and becomes the new default.
    /// Each sub-application keeps its OWN destination — do not merge these two fields.
    /// </summary>
    public string MainOutputDirectory { get; set; } = "";

    /// <summary>ISSUE_04 — Video Merger export destination. Independent of <see cref="MainOutputDirectory"/>.</summary>
    public string MergerOutputDirectory { get; set; } = "";

    /// <summary>
    /// OUTNAME_01 — Main App automatic file-name base. Exports are saved as
    /// <c>&lt;base&gt;-1.mp4</c>, <c>&lt;base&gt;-2.mp4</c> … Edited in Settings › Output Files.
    /// Sanitized by <see cref="FreeVideoStudio.Core.Media.OutputFileNaming.Sanitize"/>.
    /// </summary>
    public string MainOutputBaseName { get; set; } = FreeVideoStudio.Core.Media.OutputFileNaming.MainDefaultBaseName;

    /// <summary>OUTNAME_01 — Video Merger automatic file-name base. Independent of <see cref="MainOutputBaseName"/>.</summary>
    public string MergerOutputBaseName { get; set; } = FreeVideoStudio.Core.Media.OutputFileNaming.MergerDefaultBaseName;

    /// <summary>
    /// G03 — user override for which chip encodes the final video.
    /// Valid values: "Auto" (trust the boot hardware scan), "NVIDIA", "AMD", "INTEL", "CPU".
    ///
    /// WHY THIS EXISTS: the boot scan used to be the ONLY input to encoder selection, and when it
    /// crashed (see ChildProcessTracker G01) it reported "CPU" — permanently, silently, with no
    /// way for the user to say "no, I have an RTX, use it". Every export on an affected machine
    /// ran on libx264 while the UI gave no indication anything was wrong.
    ///
    /// "Auto" must remain the default. A non-Auto value wins over the scan result unconditionally
    /// and is passed straight through to <c>ProcessWorker.HardwareStrategy</c> /
    /// <c>MergerWorker.HardwareStrategy</c>. If the chosen encoder is genuinely absent from the
    /// bundled FFmpeg, <c>EncoderManager.EncoderPreflightError</c> blocks the export with a clear
    /// message instead of silently doing something else.
    /// </summary>
    public string VideoEncoderOverride { get; set; } = "Auto";

    /// <summary>
    /// AUTO-UPDATE — master switch for the in-app update suggestor. TRUE by default: the app
    /// quietly probes the single "latest" GitHub release at startup (at most once per 24h) and
    /// only ever ASKS — nothing is downloaded or installed without an explicit Yes. The update
    /// prompt's "Never tell me about updates again" choice writes false here, so this checkbox
    /// always reflects the truth and can re-enable the feature at any time. FALSE means no
    /// network call, no prompt, no nag — ever.
    /// </summary>
    public bool AutoUpdateChecks { get; set; } = true;

    /// <summary>
    /// ISSUE_13 — the suite is a dark-first video tool and its Light palette is the weaker of the
    /// two, so a fresh install starts in Dark rather than inheriting whatever the OS happens to
    /// be set to. Users who want Light still get it from Settings > Appearance > Theme.
    /// </summary>
    public ThemeMode ThemeMode { get; set; } = ThemeMode.Dark;
    public FontScale FontScale { get; set; } = FontScale.Normal;

    public bool ConfirmVideoMergerRemove { get; set; } = false;   // REMOVEUX_01 — off by default; removal is undoable (Ctrl+Z)
    public bool ConfirmVideoMergerClearAll { get; set; } = true;

    /// <summary>
    /// SCRAPER_05 — Video Merger "Thumbnail Scraper": remove the tagged 0.1 s thumbnail intro from
    /// clips 2..N when merging. On for new installs, and forced back on by the v8 migration.
    /// </summary>
    public bool MergerThumbnailScraper { get; set; } = true;

    /// <summary>
    /// CLIPLEVEL_01 — Settings › Merger: even out the volume between merged clips (each clip is
    /// gained toward the queue's median measured loudness, ±12 dB). Default OFF: clips keep their
    /// recorded level unless the user asks.
    /// </summary>
    public bool MergerMatchClipLoudness { get; set; } = false;

    /// <summary>
    /// VOLMUTE_01 — the suite-wide preview mute (speaker button in all three apps), remembered
    /// across launches. Separate from the master level, so unmuting restores the level exactly.
    /// </summary>
    public bool PreviewMuted { get; set; } = false;
    public bool ConfirmCropToolReset { get; set; } = true;
    public bool ConfirmCropToolDelete { get; set; } = true;

    /// <summary>ISSUE_01/ISSUE_12 — ask before deleting the selected speed segment.</summary>
    public bool ConfirmGranularDeleteSegment { get; set; } = true;

    /// <summary>ISSUE_01/ISSUE_12 — ask before wiping every speed segment and pending selection.</summary>
    public bool ConfirmGranularClearAll { get; set; } = true;

    /// <summary>ISSUE_12 — ask before the Main App's CANCEL button closes the application.</summary>
    public bool ConfirmMainAppCancel { get; set; } = true;

    /// <summary>
    /// CUT_01 / DIALOG_02 — confirm before deleting a section from the middle of the clip, and
    /// before putting every section back.
    ///
    /// ⚠️ DEFAULTS **OFF**, unlike most confirmations here, and that is an owner decision rather
    /// than an oversight: cutting is a high-frequency editing gesture, not a one-off teardown like
    /// CLEAR ALL, and a prompt on every cut makes the editor unusable for anyone working quickly.
    /// It is safe to default off precisely BECAUSE the Speed Editor now has undo — a cut made by
    /// accident is one Ctrl+Z away, which is not true of the confirmations that default on.
    /// </summary>
    public bool ConfirmMainAppCut { get; set; } = false;

    /// <summary>
    /// ISSUE_02 — ask before leaving the Main App for the Video Merger / Crop Tools, which closes
    /// the Main App. Only ever asked when real editing work exists (see MainWindow.HasUnsavedWork).
    ///
    /// SWITCHPROMPT_01 — that second sentence was a PROMISE THE CODE DID NOT KEEP. This property
    /// was declared, surfaced in Settings, loaded and saved... and never read by anything. The
    /// switch handlers in MainWindow.Wireup called SwitchToCompanionAppAsync unconditionally, so
    /// the prompt fired on an empty editor with no video loaded — a warning about losing work that
    /// did not exist. MainWindow.ConfirmToolSwitchAsync is the reader it was always missing: the
    /// prompt now requires BOTH this flag AND HasUnsavedWork().
    /// </summary>
    public bool ConfirmMainAppSwitchTool { get; set; } = true;

    /// <summary>
    /// CAPTIONWIPE_01 — should the text strip's caption survive into the next video?
    ///
    /// OFF by default, and that default is the fix rather than a preference. The caption is a
    /// per-video TITLE. Leaving it in place meant the second clip of a session silently inherited
    /// the first clip's title, the third inherited it again, and the mistake is invisible until the
    /// finished file is watched — by which point it has been exported, and possibly uploaded, with
    /// the wrong words burned into the picture. Nothing else in the editor persists across videos
    /// like that.
    ///
    /// ON restores the old behaviour for the one workflow that actually wants it: someone cutting a
    /// numbered series who types the same caption every time.
    ///
    /// Read only through MainWindow.ClearOverlayTextForNextVideo.
    /// </summary>
    public bool KeepOverlayTextBetweenVideos { get; set; } = false;

    /// <summary>
    /// ISSUE_07 — ask before DELETE removes a recorded voice-over take. The take's .wav is
    /// deleted from disk immediately and cannot be recovered, so the option exists; it defaults
    /// to FALSE deliberately, unlike the eight flags above, so power users keep the one-click
    /// workflow and only people who have been bitten switch it on.
    /// </summary>
    public bool ConfirmVoiceOverDeleteTake { get; set; } = false;

    /// <summary>
    /// ISSUE_04 — ask before the finished-export dialog's EXIT APP / NEW FILE buttons act. They
    /// sit 16px from the harmless OPEN FOLDER at the same size, and they end the session or wipe
    /// the project. Also defaults to FALSE for the same reason as above.
    /// </summary>
    public bool ConfirmFinishedDialogExit { get; set; } = false;

    /// <summary>
    /// Meme System §1/§3: the unified meme asset directory. Empty = use the default
    /// (MyVideos\FreeVideoStudio\Memes, MEMEFOLDER_02). Changed via Settings → Meme folder.
    /// Always resolve through <see cref="MemeDirectory.GetActive"/> — never read this raw.
    /// </summary>
    public string MemeDirectoryPath { get; set; } = "";

    /// <summary>
    /// What to do when an uploaded video hides sudden peaks far above its own average — the
    /// "quiet gameplay, then an explosion takes the viewer's head off" case. Set from the warning
    /// dialog, reversible from Settings → Audio.
    /// </summary>
    public AudioFixPrompt PeakFlatteningPrompt { get; set; } = AudioFixPrompt.Ask;

    /// <summary>
    /// AUDIO_06 — master switch for the app's own button/UI sound effects.
    ///
    /// Until audit round 5 there was no way to turn these off, in an app whose Settings window
    /// advertises a "Sound &amp; Music" tab as "the single home for EVERY audio setting in the
    /// suite". That matters most in the two screens built for critical listening — Music Wizard
    /// step 3 (A/B-ing the video against the music) and the Voice Over Studio — where UI chirps
    /// land straight on top of the mix the user is judging.
    ///
    /// Read only through <see cref="UiSoundEffect"/>; nothing else should gate on it.
    /// </summary>
    public bool UiSoundsEnabled { get; set; } = true;

    /// <summary>
    /// AUDIO_06 — UI sound effect level, 0-100. 0 is equivalent to
    /// <see cref="UiSoundsEnabled"/> = false. Defaults to 70 rather than 100: the previous
    /// engine had no attenuation at all and played every clip at full scale.
    /// </summary>
    public int UiSoundVolume { get; set; } = 70;

    // ══════════════════════════════════════════════════════════════════════════════
    // VOPROT_02 — WHO DECIDES THE TWO VOICE-PROTECTION CHECKBOXES.
    //
    // The checkboxes in the Voice Over Studio start ON and are remembered between projects, which
    // is the right default. But "remembered" is only one of three wishes a user can have, and the
    // other two are not reachable from a checkbox: "always on, stop asking me" and "never do this
    // to my audio". A studio that always protects and a studio that never touches the mix are both
    // legitimate ways to work, and a remembered checkbox forces the second kind of user to notice
    // and clear it on every single project.
    //
    // So the MODE lives here and the checkbox is its instrument. On Always* the box is set and
    // disabled with a tooltip that says where the decision was made — never silently overridden,
    // which would look like a bug.
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>VOPROT_02 — who decides "Protect VoiceOver Recording from Game-Play Sound".</summary>
    public VoiceProtectionMode VoiceProtectGameMode { get; set; } = VoiceProtectionMode.RememberLastChoice;

    /// <summary>VOPROT_02 — who decides "Protect VoiceOver Recording from Music".</summary>
    public VoiceProtectionMode VoiceProtectMusicMode { get; set; } = VoiceProtectionMode.RememberLastChoice;

    /// <summary>
    /// VOPROT_02 — the last game-protection choice the user applied, used only under
    /// <see cref="VoiceProtectionMode.RememberLastChoice"/>. Defaults true, which is what makes
    /// the checkbox arrive already ticked on a brand-new install.
    /// </summary>
    public bool VoiceProtectGameLast { get; set; } = true;

    /// <summary>VOPROT_02 — the last music-protection choice the user applied. See above.</summary>
    public bool VoiceProtectMusicLast { get; set; } = true;
}

/// <summary>
/// VOPROT_02 — how one of the Voice Over Studio's protection checkboxes is decided.
///
/// Three states, not a bool, for the same reason as <see cref="AudioFixPrompt"/>: "do not keep
/// asking me" is two different wishes, and collapsing them makes one group of users fight the
/// setting on every project.
///
/// ⚠️ The order here IS the Settings combo-box index. Keep the two in step.
/// </summary>
public enum VoiceProtectionMode
{
    /// <summary>Start from whatever the user chose last time. Default.</summary>
    RememberLastChoice,
    /// <summary>Always on, and the checkbox is shown ticked and disabled.</summary>
    AlwaysOn,
    /// <summary>Always off, and the checkbox is shown clear and disabled.</summary>
    AlwaysOff
}

/// <summary>
/// A remembered answer to a "shall I fix this?" warning dialog.
///
/// Deliberately THREE states, not a bool: "never ask me again" is genuinely two different
/// wishes — "just do it from now on" and "leave my audio alone" — and collapsing them into one
/// checkbox forces the user to keep answering the dialog to get the behaviour they already chose.
/// </summary>
public enum AudioFixPrompt
{
    /// <summary>Show the warning and let the user decide, every time. Default.</summary>
    Ask,
    /// <summary>Never show the warning; silently apply the fix.</summary>
    AlwaysApply,
    /// <summary>Never show the warning; never apply the fix.</summary>
    NeverApply
}

/// <summary>
/// Meme System §1: single source of truth for the ACTIVE meme directory.
/// Default is MyVideos\FreeVideoStudio\Memes, overridable via Settings.
/// </summary>
public static class MemeDirectory
{
    /// <summary>Raised after the user successfully changes the meme directory in Settings, so
    /// the MainWindow can silently re-scan and rebuild the MemeComboBox (§3 State Update).
    /// Also raised after a meme download added files (MEMEPICK_01), so every open list agrees.</summary>
    public static event Action? Changed;
    public static void NotifyChanged() { try { Changed?.Invoke(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); } }

    private static bool IsDevSandbox =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
            FreeVideoStudio.Core.Infrastructure.ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable));

    private static string MediaRoot(Environment.SpecialFolder productionFolder) =>
        IsDevSandbox
            ? Path.Combine(Path.GetTempPath(), "FreeVideoStudio_DEV", "media")
            : Environment.GetFolderPath(productionFolder);

    /// <summary>Where background music lives. Sandboxed in dev — see the note above.</summary>
    public static string GetMusicRoot() => MediaRoot(Environment.SpecialFolder.MyMusic);

    /// <summary>Where images/memes live. Sandboxed in dev — see the note above.</summary>
    public static string GetVideosRoot() => MediaRoot(Environment.SpecialFolder.MyVideos);

    /// <summary>
    /// MEMEFOLDER_02 — folder names on disk never contain spaces. "Free Video Studio" is the
    /// DISPLAY name; folders use <see cref="FreeVideoStudio.Core.Infrastructure.ApplicationPaths.AppDirectoryName"/>.
    /// </summary>
    public static string GetDefault() => Path.Combine(
        GetVideosRoot(), FreeVideoStudio.Core.Infrastructure.ApplicationPaths.AppDirectoryName, "Memes");

    /// <summary>MEMEFOLDER_02 — the spaced default used by every build before this change.</summary>
    public static string GetLegacyDefault() => Path.Combine(GetVideosRoot(), "Free Video Studio", "Memes");

    /// <summary>Resolves the active directory (settings override or default) and ensures it exists.</summary>
    public static string GetActive()
    {
        EnsureLegacyDefaultMigrated();
        string configured = SettingsManager.Instance.MemeDirectoryPath;
        string dir = string.IsNullOrWhiteSpace(configured) ? GetDefault() : configured;
        try { Directory.CreateDirectory(dir); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        return dir;
    }

    private static readonly object _migrationGate = new();
    private static volatile bool _migrationDone;

    /// <summary>
    /// MEMEFOLDER_02 — moves an existing <c>Videos\Free Video Studio\Memes</c> into
    /// <c>Videos\FreeVideoStudio\Memes</c>, once per process, before anyone can read the folder.
    ///
    /// <list type="bullet">
    /// <item>Runs inside <see cref="GetActive"/>, so the scan, the starter delivery and a recovery
    /// restore all see either the old layout or the finished move — never a half-moved folder.</item>
    /// <item>A Settings override that points AT the old default is cleared, so the user follows the
    /// move. Any other custom folder is the user's and is left alone.</item>
    /// <item>Never overwrites: a same-named file already in the new folder wins. An identical copy
    /// in the old folder is removed; a DIFFERENT one is left where it is and logged.</item>
    /// <item>The old folders are removed only when empty.</item>
    /// <item>The move is recorded in migration-paths.json (UPGRADE_05), so saved projects and
    /// recovery files that name a meme by its old full path still open it.</item>
    /// </list>
    /// A failure is logged and never blocks the app; the next launch retries.
    /// </summary>
    public static void EnsureLegacyDefaultMigrated()
    {
        if (_migrationDone) return;
        lock (_migrationGate)
        {
            if (_migrationDone) return;
            _migrationDone = true;
            try
            {
                string legacy = GetLegacyDefault();
                string current = GetDefault();

                string configured = SettingsManager.Instance.MemeDirectoryPath;
                if (!string.IsNullOrWhiteSpace(configured) && SamePath(configured, legacy))
                {
                    SettingsManager.Update(s => s.MemeDirectoryPath = "");
                    RuntimeLog.Info("Memes", "Meme folder setting pointed at the old default; it now follows the new default.");
                }

                int moved = MoveFolderContents(legacy, current);
                if (moved >= 0)
                {
                    FreeVideoStudio.Core.Infrastructure.MigrationPathResolver.AppendMapping(legacy, current);
                    RuntimeLog.Info("Memes", $"Moved {moved} meme file(s) from '{legacy}' to '{current}'.");
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("Memes", $"Meme folder move skipped, will retry next launch: {ex.Message}");
            }
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); return false; }
    }

    /// <summary>
    /// MEMEFOLDER_02 — moves every file under <paramref name="source"/> into the same relative
    /// path under <paramref name="destination"/>. Returns -1 when there was nothing to move from,
    /// otherwise the number of files moved. Public for tests.
    /// </summary>
    public static int MoveFolderContents(string source, string destination)
    {
        if (!Directory.Exists(source) || SamePath(source, destination)) return -1;
        Directory.CreateDirectory(destination);
        int moved = 0;
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToArray())
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            try
            {
                if (File.Exists(target))
                {
                    if (SameContent(file, target)) File.Delete(file);
                    else RuntimeLog.Info("Memes", $"Kept '{file}': a different file with that name is already in the new meme folder.");
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file, target);
                moved++;
            }
            catch (Exception ex)
            {
                RuntimeLog.Info("Memes", $"Could not move '{file}': {ex.Message}");
            }
        }

        RemoveEmptyTree(source);
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(source));
        if (parent != null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            try { Directory.Delete(parent); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        }
        return moved;
    }

    private static void RemoveEmptyTree(string dir)
    {
        try
        {
            foreach (string sub in Directory.EnumerateDirectories(dir).ToArray()) RemoveEmptyTree(sub);
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private static bool SameContent(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sa = fa.OpenRead();
        using var sb = fb.OpenRead();
        byte[] ba = new byte[81920], bb = new byte[81920];
        while (true)
        {
            int na = sa.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false);
            int nb = sb.ReadAtLeast(bb, bb.Length, throwOnEndOfStream: false);
            if (na != nb) return false;
            if (na == 0) return true;
            if (!ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
        }
    }
}

public class KeyBinds
{
    public Key PlayPause { get; set; } = Key.Space;
    public Key MarkStart { get; set; } = Key.OemOpenBrackets;
    public Key MarkEnd { get; set; } = Key.OemCloseBrackets;
    public Key SeekForward { get; set; } = Key.Right;
    public Key SeekBackward { get; set; } = Key.Left;
    public Key VolumeUp { get; set; } = Key.Up;
    public Key VolumeDown { get; set; } = Key.Down;
    public Key FineSeekForward { get; set; } = Key.Right;
    public Key FineSeekBackward { get; set; } = Key.Left;
    public Key AggressiveVolumeUp { get; set; } = Key.Up;
    public Key AggressiveVolumeDown { get; set; } = Key.Down;
}

public enum CheckboxDefaultBehavior
{
    AlwaysOff,
    AlwaysOn,
    RememberLast
}

public enum ValueDefaultBehavior
{
    FixedValue,
    RememberLast
}

/// <summary>
/// Default initial values applied when the app freshly opens.
/// Edited via the Settings window → "Defaults" tab.
/// </summary>
public class DefaultValues
{
    /// <summary>Default speed multiplier (e.g. 1.1 = 1.1x). Range 0.1–4.0</summary>
    public double DefaultSpeed { get; set; } = 1.1;
    public ValueDefaultBehavior SpeedBehavior { get; set; } = ValueDefaultBehavior.FixedValue;

    /// <summary>Default Portrait Mode checkbox state</summary>
    public bool PortraitMode { get; set; } = true;
    public CheckboxDefaultBehavior PortraitBehavior { get; set; } = CheckboxDefaultBehavior.RememberLast;

    /// <summary>Default Show Teammates checkbox state</summary>
    public bool ShowTeammates { get; set; } = false;
    public CheckboxDefaultBehavior ShowTeammatesBehavior { get; set; } = CheckboxDefaultBehavior.AlwaysOff;

    /// <summary>Default Disable Fade checkbox state</summary>
    public bool EnableFade { get; set; } = true;
    public CheckboxDefaultBehavior EnableFadeBehavior { get; set; } = CheckboxDefaultBehavior.AlwaysOn;

    /// <summary>
    /// QUALITY_01 — the quality TIER a new project starts on (index into QualityLadder.Tiers),
    /// not a megabyte step. 8 = "Sharp". A size default produced a different quality for every
    /// clip length, which is the defect the tier ladder exists to remove.
    /// An index written by an older build is clamped on the way in, not rejected.
    /// </summary>
    public int QualityIndex { get; set; } = 8;
    public ValueDefaultBehavior QualityBehavior { get; set; } = ValueDefaultBehavior.FixedValue;

    /// <summary>
    /// PEAKSAFE_01 — master switch for the PEAK TAMER (softening sudden loud moments in the
    /// gameplay). The always-on true-peak safety limiter is not affected by it.
    /// </summary>
    public bool AutoSpikeFlattening { get; set; } = true;

    /// <summary>
    /// AUDIO_09 — the master switch for BOTH sidechain ducking and EQ carving.
    ///
    /// Replaces the per-export "Export Ducking ON/OFF" button that used to sit in Music Wizard
    /// phase 3. That button was in the wrong place twice over: it occupied a permanent row in the
    /// app's most vertically-cramped screen, and it could not demonstrate its own effect — the
    /// preview never applied ducking, so pressing it changed nothing you could hear until after
    /// an export.
    ///
    /// It is also a SET-ONCE PREFERENCE, not a per-video decision. Turning it off is what produces
    /// the "music swallows the gunshots" complaint, so it belongs with the other standing audio
    /// preferences rather than in the middle of a per-clip workflow.
    ///
    /// ⚠️ OFF MEANS NO PROTECTION AT ALL. Both the ducking and the carving are skipped, so the
    /// music sits on top of the gameplay at a fixed level for the whole video. Default ON.
    ///
    /// DUCKSTRENGTH_01 — LEGACY (schema ≤ 12). Replaced by the two separate switches below; read
    /// only by the v13 migration, which copies it into both. Kept so old files still deserialize it.
    /// </summary>
    public bool AudioProtection { get; set; } = true;

    /// <summary>
    /// DUCKSTRENGTH_01 — Settings › Sound &amp; Music › "Music vs. game sound". Volume ducking (the
    /// music steps back while the game is loud) and EQ carving (the music makes room in the
    /// voice/game pitches), each with its own on/off switch and its own strength handle
    /// (0-100, 50 = the tuned out-of-the-box value; see SidechainCompressNode.RatioFor). OFF here
    /// wins over the Music Wizard's per-video checkbox.
    /// </summary>
    public bool DuckingEnabled { get; set; } = true;
    public bool CarvingEnabled { get; set; } = true;
    public int DuckingStrength { get; set; } = FreeVideoStudio.Core.Media.SidechainCompressNode.DefaultStrength;
    public int CarvingStrength { get; set; } = FreeVideoStudio.Core.Media.SidechainCompressNode.DefaultStrength;
    
    /// <summary>Whether to remember the music and video volume set in the music wizard</summary>
    public bool RememberMusicVolumes { get; set; } = true;

    /// <summary>Default zoom-in ramp: true = SLOW (gradual), false = INSTANT (hard cut).</summary>
    public bool DefaultZoomSlow { get; set; } = false;
    /// <summary>Default freeze-image hold duration in seconds (matches the preset buttons 0.5–3.0).</summary>
    public double DefaultFreezeDurationS { get; set; } = 1.0;
}

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(KeyBinds))]
[JsonSerializable(typeof(DefaultValues))]
[JsonSerializable(typeof(CheckboxDefaultBehavior))]
[JsonSerializable(typeof(ValueDefaultBehavior))]
[JsonSerializable(typeof(AudioFixPrompt))]
[JsonSerializable(typeof(VoiceProtectionMode))]
[JsonSerializable(typeof(ThemeMode))]
[JsonSerializable(typeof(FontScale))]
public partial class SettingsJsonContext : JsonSerializerContext { }

public static class SettingsManager
{
    /// <summary>
    /// ISSUE_14 — schema version of the settings shape THIS build writes.
    /// History:
    ///   1 = original unversioned shape (no SchemaVersion field on disk).
    ///   2 = added MainOutputDirectory / MergerOutputDirectory (ISSUE_04).
    ///   3 = added LoudnessNormalizationPrompt / PeakFlatteningPrompt (audio warning dialogs).
    ///   4 = added VideoEncoderOverride (G03 — user-forced encoder).
    ///   5 = added VoiceProtectGameMode / VoiceProtectMusicMode and their remembered
    ///       last-choice flags (VOPROT_02 — voice-protection policy).
    /// Bump this whenever a field is renamed, removed, or changes meaning, and add the matching
    ///   6 = added voice protection / initial auto-update.
    ///   7 = AUTO-UPDATE — ensures AutoUpdateChecks defaults to true on initial install
    ///       and on upgrades where the configuration did not yet exist.
    ///   8 = SCRAPER_05 — added MergerThumbnailScraper; every upgrade turns it ON.
    ///   9 = REMOVEUX_01 — the Video Merger's remove confirmation is OFF by default (removal is undoable
    ///       with Ctrl+Z and a notice says so); every upgrade turns it OFF once. Users who want the dialog
    ///       switch it back on in Settings › Confirmation Dialogs.
    ///   10 = AI SMART ZOOM — added GeminiApiKey, GeminiModelName, AiZoomBaseScale, AiZoomMinScale,
    ///        AiZoomAvoidHud, and AiZoomDeadbandPercent settings.
    ///   11 = OUTNAME_01 / REBRAND_02 — added MainOutputBaseName ("FreeVideoStudio") and
    ///        MergerOutputBaseName ("Merged-Videos"); files from settings written by the previous
    ///        brand receive the new defaults, so the first export after the update is
    ///        FreeVideoStudio-1.mp4.
    ///   12 = LOUDSTD_REMOVED_01 — REMOVED LoudnessNormalizationPrompt and
    ///        Defaults.AutoVoiceNormalization (the loudness-standard feature is gone; old keys are
    ///        simply ignored on read and dropped on the next write). ADDED MergerMatchClipLoudness
    ///        (CLIPLEVEL_01, default off) and PreviewMuted (VOLMUTE_01, default off).
    ///   13 = DUCKSTRENGTH_01 — Defaults.DuckingEnabled / CarvingEnabled (seeded from the legacy
    ///        AudioProtection on upgrade, so a user who had protection OFF keeps it off) and
    ///        Defaults.DuckingStrength / CarvingStrength (50 = tuned). Values set by the user are
    ///        never reset by a later migration.
    /// Bump this whenever a field is renamed, removed, or changes meaning, and add the matching
    /// case to <see cref="Migrate"/>. NEVER reuse a number.
    /// </summary>
    public const int CurrentSchemaVersion = 13;

    private static string SettingsPath => Path.Combine(FreeVideoStudio.Core.Infrastructure.ApplicationPaths.CreateDefault().ProgramDataRoot, "settings.json");

    /// <summary>
    /// SETTINGSATOMIC_01 — cross-PROCESS lock. settings.json is shared by the Main App, the
    /// Video Merger and the Crop Tools; this is the only thing that stops two of them publishing
    /// over each other. Named the same way as the existing Global\Fvs* locks so it is visible
    /// alongside them in a handle dump.
    /// </summary>
    private static readonly string SettingsMutexName =
        FreeVideoStudio.Core.Infrastructure.NamedSystemMutex.UserScopedName("FvsFreeVideoStudioMutex");

    /// <summary>
    /// SETTINGSATOMIC_01 — in-PROCESS gate around every settings transaction and around publishing
    /// <see cref="Instance"/>. UpdateService's background task is the concrete second writer.
    /// </summary>
    private static readonly object SerializeGate = new();

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // SETTX_02 — LOCK ORDER (the ONLY order; every path in this class follows it):
    //
    //   1. SerializeGate            (in-process Monitor)
    //   2. SettingsMutexName        (cross-process named mutex) — acquired ONCE per transaction
    //   3. file I/O on settings.json and its quarantine copies
    //
    // The named mutex is held across the WHOLE read → deserialize → migrate/default → patch →
    // serialize → AtomicJsonFile.WriteText sequence, so no sibling process can commit between this
    // transaction's read and its write. It is never acquired while SerializeGate is NOT held, never
    // acquired twice (no helper reacquires it — the *Locked helpers REQUIRE the caller to own it),
    // and never held across a UI await or while raising Committed.
    //
    // BEFORE (defect): Update took the gate, then the mutex for the READ, RELEASED it, patched and
    // serialized, then took the mutex AGAIN for the WRITE. Load took the mutex for its read without
    // the gate. A sibling process could commit in the gap and be overwritten by a stale snapshot.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Test seam: the settings mutex name, so tests can contend on it from another thread.</summary>
    internal static string SettingsMutexNameForTests => SettingsMutexName;

    /// <summary>
    /// Test seam: invoked inside <see cref="Update"/> after the persisted document was read, while
    /// the gate AND the named mutex are held. Always null in production.
    /// </summary>
    internal static Action? TransactionReadCompletedForTests;

    public static AppSettings Instance { get; private set; } = new AppSettings();

    /// <summary>
    /// True when the last <see cref="Load"/> found an unreadable settings file and quarantined
    /// it. The UI surfaces this once at startup so a silent config reset is never invisible.
    /// </summary>
    public static string? LoadFailureMessage { get; private set; }

    /// <summary>
    /// SETTX_01/SETTX_02 — THE ONLY MUTATION PATH. One logical settings change = one transaction:
    /// under <see cref="SerializeGate"/> and ONE acquisition of the cross-process settings mutex:
    /// read settings.json → deserialize (source-generated metadata) → default/migrate → apply the
    /// caller's patch → serialize known fields and merge back preserved unknown root properties →
    /// <see cref="FreeVideoStudio.Core.Infrastructure.AtomicJsonFile.WriteText"/>. Only after that
    /// durable write succeeded is the new snapshot published to <see cref="Instance"/> and
    /// <see cref="Committed"/> raised. A failed lock/read/patch/serialize/write leaves
    /// <see cref="Instance"/> EXACTLY as it was, raises nothing and returns false.
    /// Synchronous; waits at most the 2-second InteractiveMutexTimeout; no lock across a UI await.
    /// </summary>
    /// <param name="patch">Mutation applied to the fresh settings document.</param>
    public static bool Update(Action<AppSettings> patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        AppSettings committed;
        lock (SerializeGate)
        {
            AppSettings next;
            FreeVideoStudio.Core.Infrastructure.NamedSystemMutex guard;
            try
            {
                guard = AcquireSettingsMutex();
            }
            catch (FreeVideoStudio.Core.Infrastructure.LockException)
            {
                RuntimeLog.Fail("Settings",
                    "Settings transaction not committed: another Free Video Studio process is holding the settings lock. " +
                    "The in-memory settings are unchanged; the caller may retry.");
                return false;
            }

            using (guard)
            {
                PersistedSettings? read = ReadPersistedLocked();
                if (read is null)
                {
                    RuntimeLog.Fail("Settings", "Settings transaction aborted: the settings file could not be read. The in-memory settings are unchanged.");
                    return false;
                }

                TransactionReadCompletedForTests?.Invoke();

                next = read.Settings;
                try
                {
                    patch(next);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("Settings", $"Settings transaction rejected: the patch callback threw ({ex.Message}). Settings unchanged.");
                    return false;
                }

                // Future schema is never downgraded; older/current is stamped current.
                next.SchemaVersion = read.EffectiveSchemaVersion;

                string json;
                try
                {
                    json = ComposeDocument(next, read.UnknownRoot);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("Settings", $"Settings transaction failed: could not serialize settings ({ex.Message}). Settings unchanged.");
                    return false;
                }

                try
                {
                    FreeVideoStudio.Core.Infrastructure.AtomicJsonFile.WriteText(SettingsPath, json);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("Settings", $"Settings transaction failed: the atomic write failed ({ex.Message}). The in-memory settings are unchanged.");
                    return false;
                }
            }

            Instance = next;
            committed = next;
        }

        try
        {
            Committed?.Invoke(committed);
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
        RuntimeLog.Info("Settings", $"Settings transaction committed (schema v{committed.SchemaVersion}).");
        return true;
    }

    /// <summary>Raised after every committed settings transaction, with the published snapshot (read-only — mutate via <see cref="Update"/>).</summary>
    public static event Action<AppSettings>? Committed;

    /// <summary>
    /// SETTX_01 — typed helper for the SINGLE setting a caller owns (UpdateService's
    /// auto-update preference). Same transaction as <see cref="Update"/>.
    /// </summary>
    public static bool SetAutoUpdateChecks(bool enabled) => Update(s => s.AutoUpdateChecks = enabled);

    public static void Load()
    {
        LoadFailureMessage = null;

        lock (SerializeGate)
        {
            FreeVideoStudio.Core.Infrastructure.NamedSystemMutex guard;
            try
            {
                guard = AcquireSettingsMutex();
            }
            catch (Exception ex)
            {
                LoadFailureMessage = "Your saved settings could not be read, so this session is using defaults. " +
                                     "Your settings file was left untouched.";
                RuntimeLog.Fail("Settings", $"Failed to lock settings file for reading: {ex.Message}");
                return;
            }

            using (guard)
            {
                PersistedSettings? read = ReadPersistedLocked();
                if (read is null)
                {
                    LoadFailureMessage = "Your saved settings could not be read, so this session is using defaults. " +
                                         "Your settings file was left untouched.";
                    return;
                }

                AppSettings loaded = read.Settings;
                if (read.NeedsPersist)
                {
                    loaded.SchemaVersion = read.EffectiveSchemaVersion;
                    TryWriteLocked(loaded, read.UnknownRoot);
                    if (read.Origin == PersistedOrigin.Existing)
                    {
                        RuntimeLog.Info("Settings", $"Migrated settings from schema v{read.PersistedSchemaVersion} to v{loaded.SchemaVersion} (AutoUpdateChecks: {loaded.AutoUpdateChecks}) and persisted to disk.");
                    }
                }

                // Load publishes what is on disk (or the defaults that replaced a missing/corrupt
                // file); it is not a mutation and raises no Committed event.
                Instance = loaded;
                RuntimeLog.Info("Settings", $"Settings loaded (schema v{loaded.SchemaVersion}).");
            }
        }
    }

    private enum PersistedOrigin { Missing, Existing, RecoveredFromCorruption }

    /// <summary>One read of settings.json, taken under the named mutex.</summary>
    private sealed class PersistedSettings
    {
        public required AppSettings Settings { get; init; }
        public required PersistedOrigin Origin { get; init; }
        /// <summary>SchemaVersion as found on disk (0 for missing/corrupt).</summary>
        public int PersistedSchemaVersion { get; init; }
        /// <summary>Root properties this build does not understand — written back verbatim.</summary>
        public JsonObject? UnknownRoot { get; init; }
        /// <summary>Load must write the document back (fresh install, corruption, migration).</summary>
        public bool NeedsPersist { get; init; }
        /// <summary>The schema version the next write must carry: never lower than what is on disk.</summary>
        public int EffectiveSchemaVersion => Math.Max(PersistedSchemaVersion, CurrentSchemaVersion);
    }

    private static FreeVideoStudio.Core.Infrastructure.NamedSystemMutex AcquireSettingsMutex()
        => FreeVideoStudio.Core.Infrastructure.NamedSystemMutex.Acquire(
            SettingsMutexName,
            FreeVideoStudio.Core.Ipc.StateTransferStore.InteractiveMutexTimeout,
            System.Threading.CancellationToken.None);

    /// <summary>
    /// SETTX_02 — reads the persisted settings. CALLER MUST HOLD <see cref="SerializeGate"/> AND
    /// the settings mutex; this helper never acquires either (no recursion).
    ///   * missing file  → fresh defaults (AutoUpdateChecks=true), NeedsPersist;
    ///   * readable file → deserialized with source-generated metadata, AutoUpdateChecks defaulted
    ///     when the key predates the field, <see cref="Migrate"/> applied, unknown ROOT properties
    ///     captured for write-back;
    ///   * corrupt file  → quarantined (copy kept), defaults returned, <see cref="LoadFailureMessage"/>
    ///     set — the process's in-memory snapshot is never used to overwrite it;
    ///   * unreadable    → null: the transaction must abort and leave the file alone.
    /// </summary>
    private static PersistedSettings? ReadPersistedLocked()
    {
        if (!File.Exists(SettingsPath))
        {
            RuntimeLog.Info("Settings", "No settings file yet — starting from defaults (AutoUpdateChecks=true).");
            return new PersistedSettings
            {
                Settings = new AppSettings { AutoUpdateChecks = true },
                Origin = PersistedOrigin.Missing,
                NeedsPersist = true,
            };
        }

        string json;
        try
        {
            json = File.ReadAllText(SettingsPath);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Settings", $"Failed to read settings file: {ex.Message}");
            return null;
        }

        AppSettings loaded;
        try
        {
            loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)
                ?? throw new InvalidDataException("Settings file deserialized to null.");
        }
        catch (Exception ex)
        {
            string backupPath = QuarantineCorruptFile(json);
            LoadFailureMessage =
                "Your saved settings could not be understood and have been reset to defaults. " +
                (backupPath.Length > 0
                    ? "A copy of the old file was kept at: " + backupPath
                    : "The old file could not be backed up.");
            RuntimeLog.Fail("Settings", $"Failed to parse settings: {ex.Message}");
            return new PersistedSettings
            {
                Settings = new AppSettings { AutoUpdateChecks = true },
                Origin = PersistedOrigin.RecoveredFromCorruption,
                NeedsPersist = true,
            };
        }

        // SCHEMALEGACY_01 — a file WITHOUT a SchemaVersion key is the original v1 (pre-schema) shape.
        // The deserializer leaves the property initializer (CurrentSchemaVersion) in place for a
        // missing key, which used to skip every migration. Pin it to 1 so Migrate walks the chain.
        if (!HasRootSchemaVersion(json))
        {
            loaded.SchemaVersion = 1;
        }

        int schemaBefore = loaded.SchemaVersion;
        bool lackedAutoUpdate = !json.Contains("\"AutoUpdateChecks\"", StringComparison.Ordinal);
        if (lackedAutoUpdate)
        {
            loaded.AutoUpdateChecks = true;
        }
        Migrate(loaded);

        return new PersistedSettings
        {
            Settings = loaded,
            Origin = PersistedOrigin.Existing,
            PersistedSchemaVersion = schemaBefore,
            UnknownRoot = ExtractUnknownRootProperties(json),
            // A FUTURE-schema file is left untouched by Load (only an explicit Update rewrites it,
            // and then without downgrading or dropping anything).
            NeedsPersist = schemaBefore < CurrentSchemaVersion
                        || (lackedAutoUpdate && schemaBefore <= CurrentSchemaVersion),
        };
    }

    /// <summary>
    /// SCHEMALEGACY_01 — true when the document's ROOT object carries a "SchemaVersion" property.
    /// Called only after the source-generated deserializer accepted the same text. JsonDocument is
    /// reflection-free (NativeAOT-safe) and tolerates duplicate keys.
    /// </summary>
    private static bool HasRootSchemaVersion(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(nameof(AppSettings.SchemaVersion), out _);
    }

    /// <summary>
    /// SETTX_02 — root property names this build serializes, taken from the SOURCE-GENERATED
    /// metadata (no reflection; NativeAOT-safe).
    /// </summary>
    private static readonly HashSet<string> KnownRootPropertyNames =
        new(SettingsJsonContext.Default.AppSettings.Properties.Select(p => p.Name), StringComparer.Ordinal);

    /// <summary>
    /// SETTX_02 — FORWARD COMPATIBILITY. Returns the ROOT properties of <paramref name="json"/> that
    /// this build does not know (written by a newer Free Video Studio), or null when there are none.
    /// A document that the serializer accepted but JsonNode cannot model (e.g. duplicate keys) keeps
    /// the known fields and logs that unknown properties could not be preserved.
    /// </summary>
    private static JsonObject? ExtractUnknownRootProperties(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return null;

            JsonObject? unknown = null;
            foreach (KeyValuePair<string, JsonNode?> property in root)
            {
                if (KnownRootPropertyNames.Contains(property.Key)) continue;
                unknown ??= new JsonObject();
                unknown[property.Key] = property.Value?.DeepClone();
            }
            return unknown;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Settings", $"Unknown settings properties could not be preserved: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// SETTX_02 — serializes the known <see cref="AppSettings"/> through the source-generated
    /// context, then merges the preserved unknown root properties back. Known fields win any name
    /// collision. With nothing to merge, the serializer's exact bytes are returned (ATOMICTEXT_01).
    /// </summary>
    private static string ComposeDocument(AppSettings settings, JsonObject? unknownRoot)
    {
        string known = JsonSerializer.Serialize(settings, IndentedContext.AppSettings);
        if (unknownRoot is null || unknownRoot.Count == 0) return known;

        JsonObject merged = JsonNode.Parse(known) as JsonObject
            ?? throw new InvalidDataException("Serialized settings are not a JSON object.");
        foreach (KeyValuePair<string, JsonNode?> property in unknownRoot)
        {
            if (merged.ContainsKey(property.Key)) continue;   // known current field wins
            merged[property.Key] = property.Value?.DeepClone();
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            merged.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Load's write-back (fresh install, corruption recovery, migration). CALLER MUST HOLD
    /// <see cref="SerializeGate"/> AND the settings mutex.
    /// </summary>
    private static bool TryWriteLocked(AppSettings settings, JsonObject? unknownRoot)
    {
        try
        {
            FreeVideoStudio.Core.Infrastructure.AtomicJsonFile.WriteText(SettingsPath, ComposeDocument(settings, unknownRoot));
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Settings", $"Failed to save settings: {ex.Message}");
            return false;
        }
    }
    /// <summary>
    /// Forward-only migration. Each step upgrades ONE version and falls through to the next,
    /// so a v1 file on a v5 build walks the whole chain.
    /// </summary>
    private static void Migrate(AppSettings loaded)
    {
        int from = loaded.SchemaVersion;

        if (from > CurrentSchemaVersion)
        {
            RuntimeLog.Info("Settings",
                $"Settings file is schema v{from} but this build understands v{CurrentSchemaVersion}. Loading best-effort.");
            return;
        }

        if (from < 1) from = 1;

        if (from < 2)
        {
            if (loaded.MainOutputDirectory is null) loaded.MainOutputDirectory = "";
            if (loaded.MergerOutputDirectory is null) loaded.MergerOutputDirectory = "";
            from = 2;
        }

        if (from < 3)
        {
            from = 3;
        }

        if (from < 4)
        {
            if (string.IsNullOrWhiteSpace(loaded.VideoEncoderOverride)) loaded.VideoEncoderOverride = "Auto";
            from = 4;
        }

        if (from < 5)
        {
            // VOPROT_02 — purely additive. A v4 file has no policy recorded, and the C# property
            // defaults (RememberLastChoice, both last-choices true) reproduce EXACTLY the
            // behaviour that build had, so there is nothing to convert.
            from = 5;
        }

        if (from < 6)
        {
            loaded.AutoUpdateChecks = true;
            from = 6;
        }

        if (from < 7)
        {
            // AUTO-UPDATE — newly introduced configuration:
            // On upgrade from any prior version that lacked this configuration,
            // auto-update checks MUST be enabled (true) by default.
            loaded.AutoUpdateChecks = true;
            from = 7;
        }

        if (from < 8)
        {
            // SCRAPER_05 — new feature, on by default for upgraders too (product decision).
            loaded.MergerThumbnailScraper = true;
            from = 8;
        }

        if (from < 9)
        {
            // REMOVEUX_01 — user decision 2026-09-27: no blocking confirm by default, also on upgrade.
            loaded.ConfirmVideoMergerRemove = false;
            from = 9;
        }

        if (from < 10)
        {
            if (loaded.GeminiApiKey is null) loaded.GeminiApiKey = "";
            if (string.IsNullOrWhiteSpace(loaded.GeminiModelName)) loaded.GeminiModelName = "gemini-2.5-flash";
            if (loaded.AiZoomBaseScale <= 0) loaded.AiZoomBaseScale = 2.2;
            if (loaded.AiZoomMinScale <= 0) loaded.AiZoomMinScale = 1.3;
            if (loaded.AiZoomDeadbandPercent <= 0) loaded.AiZoomDeadbandPercent = 2.0;
            from = 10;
        }

        if (from < 11)
        {
            loaded.MainOutputBaseName = FreeVideoStudio.Core.Media.OutputFileNaming.Sanitize(
                loaded.MainOutputBaseName, FreeVideoStudio.Core.Media.OutputFileNaming.MainDefaultBaseName);
            loaded.MergerOutputBaseName = FreeVideoStudio.Core.Media.OutputFileNaming.Sanitize(
                loaded.MergerOutputBaseName, FreeVideoStudio.Core.Media.OutputFileNaming.MergerDefaultBaseName);
            from = 11;
        }

        if (from < 12)
        {
            // LOUDSTD_REMOVED_01 / CLIPLEVEL_01 / VOLMUTE_01 — removals need nothing (unknown keys are
            // ignored); the additions take their C# defaults.
            from = 12;
        }

        if (from < 13)
        {
            // DUCKSTRENGTH_01 — split the old master switch into the two new ones; strengths start
            // at the middle (their C# defaults). Runs once; later upgrades never touch them.
            loaded.Defaults.DuckingEnabled = loaded.Defaults.AudioProtection;
            loaded.Defaults.CarvingEnabled = loaded.Defaults.AudioProtection;
            from = 13;
        }

        loaded.SchemaVersion = from;
    }

    private static string QuarantineCorruptFile(string originalContent)
    {
        try
        {
            string backupPath = SettingsPath + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.WriteAllText(backupPath, originalContent);
            RuntimeLog.Info("Settings", $"Corrupt settings file backed up to {Path.GetFileName(backupPath)}.");
            PruneOldBackups();
            return backupPath;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Settings", $"Could not back up the corrupt settings file: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>Keeps at most 5 quarantined copies so a repeated fault cannot fill the disk.</summary>
    private static void PruneOldBackups()
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (string.IsNullOrEmpty(dir)) return;

            var backups = new DirectoryInfo(dir)
                .GetFiles("settings.json.corrupt-*.bak")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(5);

            foreach (var f in backups)
            {
                try { f.Delete(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            }
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    /// <summary>
    /// AOTSAFETY_04 — the source-generated context, bound once to the indented options every settings
    /// write uses. A JsonSerializerContext is immutable and thread-safe after construction, so
    /// one static instance serves every save.
    /// </summary>
    private static readonly SettingsJsonContext IndentedContext =
        new(new JsonSerializerOptions { WriteIndented = true });

    // SETTINGSATOMIC_01 — cross-process, power-outage-safe settings persistence.
    //
    // ══════════════════════════════════════════════════════════════════════════════════════════
    // WHAT WAS WRONG, AND WHY IT WAS THREE BUGS, NOT ONE.
    //
    // This used to be:
    //     string tempFile = SettingsPath + ".tmp";
    //     File.WriteAllText(tempFile, json);
    //     File.Move(tempFile, SettingsPath, overwrite: true);
    //
    //   1. THE TEMP NAME WAS A CONSTANT. settings.json lives under ProgramDataRoot, which the Main
    //      App, the Video Merger (--merger) and the Crop Tools (--crop-tool) all share. Three
    //      processes writing "settings.json.tmp" is three processes writing the SAME scrap of
    //      paper. The loser gets IOException (swallowed, and 10 of the 12 call sites discarded the
    //      bool), or — worse — process B's File.Move publishes process A's half-written payload as
    //      the live configuration.
    //
    //   2. THERE WAS NO DURABILITY BARRIER. File.WriteAllText returns when the bytes reach the OS
    //      cache, not the platter, and File.Move maps to MoveFileExW with MOVEFILE_REPLACE_EXISTING
    //      only. NTFS journals the RENAME but not the DATA, so a power cut in that window leaves a
    //      correctly named, correctly sized, ZERO-FILLED settings.json. Load() then quarantines it
    //      and resets every preference the user ever set.
    //
    //   3. Instance WAS SERIALISED WITHOUT A LOCK. It is a mutable static reference object, and
    //      UpdateService's background task writes to it while UI handlers do. JsonSerializer walking
    //      a graph that is being mutated yields a torn document or InvalidOperationException.
    //
    // THE FIX, in the order the three defects are listed:
    //   1. A Global\ named mutex (SettingsMutexName) serialises the write across all three
    //      processes, exactly as CropConfigStore already does for crops_coordinations.conf.
    //   2. AtomicJsonFile.WriteText supplies the GUID temp name + FileOptions.WriteThrough +
    //      Flush(flushToDisk: true) + atomic File.Move that
    //      docs/05_SYSTEM_LIFECYCLE_STORAGE.md#SYS-RECOVERY mandates for ALL disk saves.
    //   3. The serialisation happens inside SerializeGate, so the JSON string is a consistent
    //      snapshot taken before any lock on the filesystem is contended for.
    //
    // SETTX_02 SUPERSEDES the original "mutex only around the write" rule: holding it only there
    // let a sibling process commit between our read and our write (lost update). The mutex now
    // spans the whole read-modify-write (see the LOCK ORDER block above); it is still never held
    // across a UI await and still uses the 2-second InteractiveMutexTimeout.
    // ══════════════════════════════════════════════════════════════════════════════════════════
    // SETTX_02 — the former public/private Save() is gone: every write now happens inside one
    // gate + named-mutex transaction (Update, or Load's own write-back), via TryWriteLocked/Update.
}
