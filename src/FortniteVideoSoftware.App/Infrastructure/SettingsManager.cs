// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Input;

namespace FortniteVideoSoftware.App.Infrastructure;

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
    /// ISSUE_04 — Main App export destination. Empty means "resolve the real Downloads folder
    /// at export time". Once the user is asked to pick a location (because Downloads is
    /// missing/unwritable) the chosen folder is stored here and becomes the new default.
    /// Each sub-application keeps its OWN destination — do not merge these two fields.
    /// </summary>
    public string MainOutputDirectory { get; set; } = "";

    /// <summary>ISSUE_04 — Video Merger export destination. Independent of <see cref="MainOutputDirectory"/>.</summary>
    public string MergerOutputDirectory { get; set; } = "";

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

    public bool ConfirmVideoMergerRemove { get; set; } = true;
    public bool ConfirmVideoMergerClearAll { get; set; } = true;
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
    /// (MyVideos\Fortnite Video Software\Memes). Changed via Settings → Meme folder.
    /// Always resolve through <see cref="MemeDirectory.GetActive"/> — never read this raw.
    /// </summary>
    public string MemeDirectoryPath { get; set; } = "";

    /// <summary>
    /// What to do when an uploaded video's average loudness is outside the accepted band around
    /// the -14 LUFS streaming standard. Set from the warning dialog's two "do not show again"
    /// checkboxes, and reversible at any time from Settings → Audio.
    /// </summary>
    public AudioFixPrompt LoudnessNormalizationPrompt { get; set; } = AudioFixPrompt.Ask;

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
/// Default is MyVideos\Fortnite Video Software\Memes, overridable via Settings.
/// </summary>
public static class MemeDirectory
{
    /// <summary>Raised after the user successfully changes the meme directory in Settings, so
    /// the MainWindow can silently re-scan and rebuild the MemeComboBox (§3 State Update).</summary>
    public static event Action? Changed;
    public static void NotifyChanged() { try { Changed?.Invoke(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); } }

    private static bool IsDevSandbox =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
            FortniteVideoSoftware.Core.Infrastructure.ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable));

    private static string MediaRoot(Environment.SpecialFolder productionFolder) =>
        IsDevSandbox
            ? Path.Combine(Path.GetTempPath(), "Fortnite_Video_Software_DEV", "media")
            : Environment.GetFolderPath(productionFolder);

    /// <summary>Where background music lives. Sandboxed in dev — see the note above.</summary>
    public static string GetMusicRoot() => MediaRoot(Environment.SpecialFolder.MyMusic);

    /// <summary>Where images/memes live. Sandboxed in dev — see the note above.</summary>
    public static string GetVideosRoot() => MediaRoot(Environment.SpecialFolder.MyVideos);

    public static string GetDefault() => Path.Combine(
        GetVideosRoot(), "Fortnite Video Software", "Memes");

    /// <summary>Resolves the active directory (settings override or default) and ensures it exists.</summary>
    public static string GetActive()
    {
        string configured = SettingsManager.Instance.MemeDirectoryPath;
        string dir = string.IsNullOrWhiteSpace(configured) ? GetDefault() : configured;
        try { Directory.CreateDirectory(dir); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        return dir;
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

    public bool AutoVoiceNormalization { get; set; } = true;
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
    /// </summary>
    public bool AudioProtection { get; set; } = true;
    
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
    /// Bump this whenever a field is renamed, removed, or changes meaning, and add the matching
    /// case to <see cref="Migrate"/>. NEVER reuse a number.
    /// </summary>
    public const int CurrentSchemaVersion = 7;

    private static string SettingsPath => Path.Combine(FortniteVideoSoftware.Core.Infrastructure.ApplicationPaths.CreateDefault().ProgramDataRoot, "settings.json");

    /// <summary>
    /// SETTINGSATOMIC_01 — cross-PROCESS write lock. settings.json is shared by the Main App, the
    /// Video Merger and the Crop Tools; this is the only thing that stops two of them publishing
    /// over each other. Named the same way as the existing Global\Fvs* locks so it is visible
    /// alongside them in a handle dump.
    /// </summary>
    private const string SettingsMutexName = @"Global\FvsSettingsMutex";

    /// <summary>
    /// SETTINGSATOMIC_01 — in-PROCESS gate around reading/writing <see cref="Instance"/>. Serialising
    /// a mutable object graph while another thread mutates it is how a torn settings document gets
    /// written; UpdateService's background task is the concrete second writer.
    /// </summary>
    private static readonly object SerializeGate = new();

    public static AppSettings Instance { get; private set; } = new AppSettings();

    /// <summary>
    /// True when the last <see cref="Load"/> found an unreadable settings file and quarantined
    /// it. The UI surfaces this once at startup so a silent config reset is never invisible.
    /// </summary>
    public static string? LoadFailureMessage { get; private set; }

    public static void Load()
    {
        LoadFailureMessage = null;

        if (!File.Exists(SettingsPath))
        {
            RuntimeLog.Info("Settings", "No settings file yet — starting from defaults (AutoUpdateChecks=true).");
            lock (SerializeGate) { Instance = new AppSettings { AutoUpdateChecks = true }; }
            Save();
            return;
        }

        string json;
        try
        {
            // SETTINGSATOMIC_01 — read under the same cross-process lock the write takes, so a
            // sibling process cannot be mid-File.Move while this read is in flight.
            using FortniteVideoSoftware.Core.Infrastructure.NamedSystemMutex readGuard =
                FortniteVideoSoftware.Core.Infrastructure.NamedSystemMutex.Acquire(
                    SettingsMutexName,
                    FortniteVideoSoftware.Core.Ipc.StateTransferStore.InteractiveMutexTimeout,
                    System.Threading.CancellationToken.None);

            json = File.ReadAllText(SettingsPath);
        }
        catch (Exception ex)
        {
            LoadFailureMessage = "Your saved settings could not be read, so this session is using defaults. " +
                                 "Your settings file was left untouched.";
            RuntimeLog.Fail("Settings", $"Failed to read settings file: {ex.Message}");
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);
            if (loaded == null)
            {
                throw new InvalidDataException("Settings file deserialized to null.");
            }

            int schemaBefore = loaded.SchemaVersion;
            bool lackedAutoUpdate = !json.Contains("\"AutoUpdateChecks\"");
            if (lackedAutoUpdate)
            {
                loaded.AutoUpdateChecks = true;
            }

            Migrate(loaded);
            lock (SerializeGate) { Instance = loaded; }
            RuntimeLog.Info("Settings", $"Settings loaded (schema v{loaded.SchemaVersion}).");
            if (schemaBefore < CurrentSchemaVersion || lackedAutoUpdate)
            {
                Save();
                RuntimeLog.Info("Settings", $"Migrated settings from schema v{schemaBefore} to v{CurrentSchemaVersion} (AutoUpdateChecks: {loaded.AutoUpdateChecks}) and persisted to disk.");
            }
        }
        catch (Exception ex)
        {
            string backupPath = QuarantineCorruptFile(json);
            lock (SerializeGate) { Instance = new AppSettings { AutoUpdateChecks = true }; }
            Save();

            LoadFailureMessage =
                "Your saved settings could not be understood and have been reset to defaults. " +
                (backupPath.Length > 0
                    ? "A copy of the old file was kept at: " + backupPath
                    : "The old file could not be backed up.");

            RuntimeLog.Fail("Settings", $"Failed to parse settings: {ex.Message}");
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
    /// SETTINGSATOMIC_01 — cross-process, power-outage-safe settings persistence.
    ///
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// WHAT WAS WRONG, AND WHY IT WAS THREE BUGS, NOT ONE.
    ///
    /// This used to be:
    ///     string tempFile = SettingsPath + ".tmp";
    ///     File.WriteAllText(tempFile, json);
    ///     File.Move(tempFile, SettingsPath, overwrite: true);
    ///
    ///   1. THE TEMP NAME WAS A CONSTANT. settings.json lives under ProgramDataRoot, which the Main
    ///      App, the Video Merger (--merger) and the Crop Tools (--crop-tool) all share. Three
    ///      processes writing "settings.json.tmp" is three processes writing the SAME scrap of
    ///      paper. The loser gets IOException (swallowed, and 10 of the 12 call sites discarded the
    ///      bool), or — worse — process B's File.Move publishes process A's half-written payload as
    ///      the live configuration.
    ///
    ///   2. THERE WAS NO DURABILITY BARRIER. File.WriteAllText returns when the bytes reach the OS
    ///      cache, not the platter, and File.Move maps to MoveFileExW with MOVEFILE_REPLACE_EXISTING
    ///      only. NTFS journals the RENAME but not the DATA, so a power cut in that window leaves a
    ///      correctly named, correctly sized, ZERO-FILLED settings.json. Load() then quarantines it
    ///      and resets every preference the user ever set.
    ///
    ///   3. Instance WAS SERIALISED WITHOUT A LOCK. It is a mutable static reference object, and
    ///      UpdateService's background task writes to it while UI handlers do. JsonSerializer walking
    ///      a graph that is being mutated yields a torn document or InvalidOperationException.
    ///
    /// THE FIX, in the order the three defects are listed:
    ///   1. A Global\ named mutex (SettingsMutexName) serialises the write across all three
    ///      processes, exactly as CropConfigStore already does for crops_coordinations.conf.
    ///   2. AtomicJsonFile.WriteText supplies the GUID temp name + FileOptions.WriteThrough +
    ///      Flush(flushToDisk: true) + atomic File.Move that
    ///      docs/05_SYSTEM_LIFECYCLE_STORAGE.md#SYS-RECOVERY mandates for ALL disk saves.
    ///   3. The serialisation happens inside SerializeGate, so the JSON string is a consistent
    ///      snapshot taken before any lock on the filesystem is contended for.
    ///
    /// The mutex is held ONLY around the disk write — never around serialisation, and never across
    /// a UI await — and uses the 2-second InteractiveMutexTimeout so a wedged sibling process can
    /// never freeze a click.
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    public static bool Save()
    {
        string json;
        try
        {
            // Snapshot under the gate: no other thread may mutate Instance mid-walk.
            lock (SerializeGate)
            {
                Instance.SchemaVersion = CurrentSchemaVersion;

                var options = new JsonSerializerOptions { WriteIndented = true };
                options.TypeInfoResolver = SettingsJsonContext.Default;
                json = JsonSerializer.Serialize(Instance, options);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Settings", $"Failed to serialize settings: {ex.Message}");
            return false;
        }

        try
        {
            using FortniteVideoSoftware.Core.Infrastructure.NamedSystemMutex guard =
                FortniteVideoSoftware.Core.Infrastructure.NamedSystemMutex.Acquire(
                    SettingsMutexName,
                    FortniteVideoSoftware.Core.Ipc.StateTransferStore.InteractiveMutexTimeout,
                    System.Threading.CancellationToken.None);

            FortniteVideoSoftware.Core.Infrastructure.AtomicJsonFile.WriteText(SettingsPath, json);
            return true;
        }
        catch (FortniteVideoSoftware.Core.Infrastructure.LockException)
        {
            RuntimeLog.Fail("Settings",
                "Settings save skipped: another Fortnite Video Software process is holding the settings lock. " +
                "The in-memory settings are unchanged; the next save will retry.");
            return false;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Settings", $"Failed to save settings: {ex.Message}");
            return false;
        }
    }
}
