namespace FvsBuild;

/// <summary>
/// Authenticode signing, dormant until a certificate is configured - exactly like the
/// old :CODE_SIGN block. Set FVS_SIGN_PFX and FVS_SIGN_PASS to activate; the password
/// is read from the environment, never hardcoded. The RFC-3161 timestamp is mandatory:
/// without it every shipped copy stops validating the day the certificate expires.
/// If FVS_SIGN_PFX is set but signing fails, the build fails - silently shipping an
/// unsigned binary when the operator asked for a signed one is the worst outcome.
/// </summary>
internal static class CodeSigning
{
    public static bool SignIfNeeded(string exePath, BuildLog log)
    {
        string? pfx = Environment.GetEnvironmentVariable("FVS_SIGN_PFX");
        if (string.IsNullOrEmpty(pfx))
        {
            log.Info("[Sign] No FVS_SIGN_PFX set - shipping UNSIGNED. Windows SmartScreen will warn end users.");
            log.Info("[Sign] To sign: set FVS_SIGN_PFX and FVS_SIGN_PASS, then re-run Build.cmd.");
            return true;
        }

        if (!File.Exists(pfx))
        {
            log.Error($"ERROR: FVS_SIGN_PFX is set but the file does not exist: {pfx}");
            return false;
        }
        string password = Environment.GetEnvironmentVariable("FVS_SIGN_PASS") ?? string.Empty;

        // signtool.exe is NOT on PATH in a plain shell - it lives in the Windows SDK.
        // Probe PATH first, then fall back to the newest x64 SDK copy.
        string? signtool = Cli.FindOnPath("signtool.exe") ?? FindWindowsKitsSigntool();
        if (signtool is null)
        {
            log.Error("ERROR: FVS_SIGN_PFX is set but signtool.exe could not be found.");
            log.Error("       Install the Windows SDK Signing Tools, or put signtool.exe on PATH.");
            return false;
        }

        log.Info($"[Sign] Signing {exePath} ...");
        int signExit = Cli.RunStreaming(signtool,
            ["sign", "/fd", "SHA256", "/f", pfx, "/p", password, "/tr", "http://timestamp.digicert.com", "/td", "SHA256", exePath],
            log);
        if (signExit != 0)
        {
            log.Error("ERROR: Authenticode signing FAILED. Refusing to ship an unsigned binary that was meant to be signed.");
            return false;
        }

        int verifyExit = Cli.RunStreaming(signtool, ["verify", "/pa", exePath], log);
        if (verifyExit != 0)
        {
            log.Error($"ERROR: Signature verification failed for {exePath}");
            return false;
        }
        log.Success("[Sign] Signed and verified.");
        return true;
    }

    private static string? FindWindowsKitsSigntool()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Windows Kits", "10", "bin");
        if (!Directory.Exists(root))
        {
            return null;
        }
        // Version directories sort naturally (10.0.26100.0 > 10.0.22621.0), newest first.
        foreach (string versionDir in Directory.EnumerateDirectories(root).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string candidate = Path.Combine(versionDir, "x64", "signtool.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
