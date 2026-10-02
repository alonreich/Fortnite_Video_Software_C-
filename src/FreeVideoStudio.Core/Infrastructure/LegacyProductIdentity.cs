
namespace FreeVideoStudio.Core.Infrastructure;

/// <summary>UPGRADE_01 — the single compatibility boundary for supported older releases.</summary>
public static class LegacyProductIdentity
{
    public static string CompactName => AppDataPaths.LegacyDirectoryNames[0];
    public static string DisplayName => AppDataPaths.LegacyDirectoryNames[1];
    public static string TempName => DisplayName.Replace(' ', '_');
    public static string DownloadName => CompactName + ".exe";
    public static string[] ExecutableNames => [DownloadName, CompactName + ".App.exe"];
    public static string[] ShortcutNames => [DisplayName + ".lnk", CompactName + ".lnk", "Uninstall " + DisplayName + ".lnk"];
}
