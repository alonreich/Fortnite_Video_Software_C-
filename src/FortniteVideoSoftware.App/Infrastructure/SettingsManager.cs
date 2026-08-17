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

    public ThemeMode ThemeMode { get; set; } = ThemeMode.FollowOS;
    public FontScale FontScale { get; set; } = FontScale.Normal;

    public bool ConfirmVideoMergerRemove { get; set; } = false;
    public bool ConfirmVideoMergerClearAll { get; set; } = false;
    public bool ConfirmCropToolReset { get; set; } = true;
    public bool ConfirmCropToolDelete { get; set; } = true;

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
    public static void NotifyChanged() { try { Changed?.Invoke(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); } }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // SANDBOX_01 — DEV MUST NEVER WRITE INTO THE INSTALLED APP'S MEDIA LIBRARY.
    //
    // `dev.cmd` already keeps its settings and state apart (FVS_PROGRAMDATA_ROOT ->
    // %TMP%\Fortnite_Video_Software_DEV), but the MEDIA folders were resolved straight from the
    // Windows Videos/Music known folders — so a dev run seeded starter files, downloaded memes and
    // wrote songs into exactly the same library the installed copy uses. Debugging a starter-file
    // or download change would silently pollute the user's real collection, and deleting a test
    // file would delete the real one.
    //
    // In dev, all three libraries now live under the same sandbox as everything else, so the whole
    // folder can be deleted to get a clean slate. Behaviour is otherwise IDENTICAL — same seeding,
    // same download buttons, same layout — which is the point: dev should rehearse production, not
    // share its data.
    // ⚠️ ONE PLACE DECIDES THIS. Do not resolve MyVideos/MyMusic directly anywhere else.
    // ═════════════════════════════════════════════════════════════════════════════════════════
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
        try { Directory.CreateDirectory(dir); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
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

    /// <summary>Default Boss HP checkbox state</summary>
    public bool BossHp { get; set; } = false;
    public CheckboxDefaultBehavior BossHpBehavior { get; set; } = CheckboxDefaultBehavior.AlwaysOff;

    /// <summary>Default Show Teammates checkbox state</summary>
    public bool ShowTeammates { get; set; } = false;
    public CheckboxDefaultBehavior ShowTeammatesBehavior { get; set; } = CheckboxDefaultBehavior.AlwaysOff;

    /// <summary>Default Disable Fade checkbox state</summary>
    public bool EnableFade { get; set; } = true;
    public CheckboxDefaultBehavior EnableFadeBehavior { get; set; } = CheckboxDefaultBehavior.AlwaysOn;

    public int QualityIndex { get; set; } = 7;
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
    /// Bump this whenever a field is renamed, removed, or changes meaning, and add the matching
    /// case to <see cref="Migrate"/>. NEVER reuse a number.
    /// </summary>
    public const int CurrentSchemaVersion = 4;

    private static string SettingsPath => Path.Combine(FortniteVideoSoftware.Core.Infrastructure.ApplicationPaths.CreateDefault().ProgramDataRoot, "settings.json");

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
            RuntimeLog.Info("Settings", "No settings file yet — starting from defaults.");
            return;
        }

        string json;
        try
        {
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

            Migrate(loaded);
            Instance = loaded;
            RuntimeLog.Info("Settings", $"Settings loaded (schema v{loaded.SchemaVersion}).");
        }
        catch (Exception ex)
        {
            string backupPath = QuarantineCorruptFile(json);
            Instance = new AppSettings();

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
            // G03: pre-v4 files have no encoder override. "Auto" preserves the old behaviour
            // exactly (trust the boot scan), so this migration is a no-op for existing users.
            if (string.IsNullOrWhiteSpace(loaded.VideoEncoderOverride)) loaded.VideoEncoderOverride = "Auto";
            from = 4;
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
                try { f.Delete(); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
            }
        }
        catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
    }

    public static void Save()
    {
        try
        {
            Instance.SchemaVersion = CurrentSchemaVersion;

            var options = new JsonSerializerOptions { WriteIndented = true };
            options.TypeInfoResolver = SettingsJsonContext.Default;
            var json = JsonSerializer.Serialize(Instance, options);
            string tempFile = SettingsPath + ".tmp";
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Settings", $"Failed to save settings: {ex.Message}");
        }
    }
}
