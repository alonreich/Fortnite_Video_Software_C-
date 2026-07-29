namespace FortniteVideoSoftware.Core.Infrastructure;

/// <summary>
/// ISSUE_09 — the one accessor for small per-user UI state files (onboarding counters,
/// dismissed hints, etc.).
///
/// Everything lives under <see cref="ApplicationPaths.UiStateDirectory"/>
/// (<c>%PROGRAMDATA%\Fortnite Video Software\uistate</c>), which is inside the SAME root as
/// settings.json and session_state.json. That is the whole point: one root means the
/// installer's "preserve my settings" switch and the uninstaller's zero-footprint sweep each
/// only have to know about one place.
///
/// Files previously written to <c>%APPDATA%\FortniteVideoSoftware\Settings</c> are migrated
/// across automatically the first time this class is touched.
///
/// Every method is total — a locked or unreadable file returns the caller's default rather
/// than throwing. UI state is never important enough to fail an operation over.
/// </summary>
public static class UiStateStore
{
    private static readonly object Sync = new();
    private static bool _migrated;

    private static string Directory_ => ApplicationPaths.CreateDefault().UiStateDirectory;

    /// <summary>
    /// One-time move of the legacy %APPDATA% files into the consolidated root. Idempotent and
    /// safe to call from any thread; runs at most once per process.
    /// </summary>
    public static void MigrateLegacyFilesOnce()
    {
        lock (Sync)
        {
            if (_migrated) return;
            _migrated = true;

            try
            {
                string legacy = ApplicationPaths.LegacyRoamingUiStateDirectory;
                if (!Directory.Exists(legacy)) return;

                string target = Directory_;
                Directory.CreateDirectory(target);

                int moved = 0;
                foreach (string file in Directory.GetFiles(legacy))
                {
                    string destination = Path.Combine(target, Path.GetFileName(file));

                    if (File.Exists(destination)) continue;

                    try
                    {
                        File.Copy(file, destination, overwrite: false);
                        moved++;
                    }
                    catch (Exception ex)
                    {
                        CoreLogger.Info("UiState", $"Failed to migrate '{file}': {ex.Message}");
                    }
                }

                if (moved > 0)
                {
                    CoreLogger.Info("UiState",
                        $"Migrated {moved} legacy UI-state file(s) out of %APPDATA% into the consolidated ProgramData root.");
                }
            }
            catch (Exception ex)
            {
                CoreLogger.Info("UiState", $"Legacy UI-state migration skipped: {ex.Message}");
            }
        }
    }

    private static string PathFor(string fileName)
    {
        MigrateLegacyFilesOnce();
        string dir = Directory_;
        try { Directory.CreateDirectory(dir); } catch (Exception ex) { CoreLogger.Debug("UiState", $"Failed to create directory '{dir}': {ex.Message}"); }
        return Path.Combine(dir, fileName);
    }

    /// <summary>Reads a small text value. Returns <paramref name="fallback"/> on any problem.</summary>
    public static string ReadText(string fileName, string fallback = "")
    {
        try
        {
            string path = PathFor(fileName);
            return File.Exists(path) ? File.ReadAllText(path) : fallback;
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("UiState", $"Failed to read text from '{fileName}': {ex.Message}");
            return fallback;
        }
    }

    /// <summary>Writes a small text value. Silently no-ops on failure.</summary>
    public static void WriteText(string fileName, string value)
    {
        try
        {
            File.WriteAllText(PathFor(fileName), value);
        }
        catch (Exception ex)
        {
            CoreLogger.Info("UiState", $"Could not write '{fileName}': {ex.Message}");
        }
    }

    /// <summary>Reads an integer counter, returning <paramref name="fallback"/> when absent or malformed.</summary>
    public static int ReadInt(string fileName, int fallback = 0)
    {
        return int.TryParse(ReadText(fileName).Trim(), out int n) ? n : fallback;
    }

    public static void WriteInt(string fileName, int value)
    {
        WriteText(fileName, value.ToString());
    }
}
