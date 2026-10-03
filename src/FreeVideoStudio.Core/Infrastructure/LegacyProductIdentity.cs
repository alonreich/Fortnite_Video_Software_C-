// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
namespace FreeVideoStudio.Core.Infrastructure;

/// <summary>UPGRADE_01 — the single compatibility boundary for supported older releases.</summary>
public static class LegacyProductIdentity
{
    public static string CompactName => AppDataPaths.LegacyDirectoryNames[0];
    public static string DisplayName => AppDataPaths.LegacyDirectoryNames[1];
    public static string TempName => DisplayName.Replace(' ', '_');
    public static string DownloadName => CompactName + ".exe";
    public static string[] ExecutableNames => [DownloadName, CompactName + ".App.exe"];
}
