namespace FvsBuild;

/// <summary>
/// SIGNMANDATE_01 - Authenticode signing. Set FVS_SIGN_PFX and FVS_SIGN_PASS to activate; the
/// password is read from the environment, never hardcoded. The RFC-3161 timestamp is mandatory:
/// without it every shipped copy stops validating the day the certificate expires.
///
/// <para>
/// =============================================================================================
/// SIGNING IS NO LONGER OPTIONAL BY DEFAULT, AND UNSIGNED IS NO LONGER SILENT.
///
/// This method used to return true with an informational line when FVS_SIGN_PFX was unset, so the
/// normal outcome of running Build.cmd on a machine without a certificate was a shipped, unsigned
/// release - and the log line saying so scrolled past between two hundred others.
///
/// Two things depend on that signature, and both were quietly disarmed:
///   1. SmartScreen. An unsigned download gets the full "Windows protected your PC" wall, and the
///      Win32 metadata block in the .csproj (ISSUE_03) exists precisely so the user has something
///      reassuring to read at that moment. Unsigned, the reassurance is a publisher field with no
///      cryptographic backing.
///   2. UPDATETRUST_01. AuthenticodeVerifier pins an update against the RUNNING executable's
///      publisher. An unsigned running executable is not an anchor, so the pin degrades to
///      hash-only - and the hash comes from the same GitHub JSON document that supplies the
///      download URL. Whoever controls that response controls the payload and its fingerprint in
///      one move, and the payload is launched with --install --auto-update, i.e. elevated.
///      That is the attack AuthenticodeVerifier was written to close, and shipping unsigned is
///      what holds it open.
///
/// So: no certificate configured is now a BUILD FAILURE. An operator who genuinely wants an
/// unsigned artifact - a local smoke test, a CI job that signs in a later stage - sets
/// FVS_ALLOW_UNSIGNED=1 and gets a loud, explicit, recorded acknowledgement instead of a default.
/// Spec: docs/05_SYSTEM_LIFECYCLE_STORAGE.md section 5 (SYS-SIGNING).
/// =============================================================================================
/// </para>
///
/// <para>
/// If FVS_SIGN_PFX is set but signing fails, the build fails - silently shipping an unsigned
/// binary when the operator asked for a signed one remains the worst outcome of all.
/// </para>
/// </summary>
internal static class CodeSigning
{
    public static bool SignIfNeeded(string exePath, BuildLog log)
    {
        string? pfx = Environment.GetEnvironmentVariable("FVS_SIGN_PFX");
        if (string.IsNullOrEmpty(pfx))
        {
            // SIGNMANDATE_01 - the deliberate, acknowledged escape hatch.
            bool acknowledged = string.Equals(
                Environment.GetEnvironmentVariable("FVS_ALLOW_UNSIGNED"), "1", StringComparison.Ordinal);

            if (!acknowledged)
            {
                log.Error("ERROR: FVS_SIGN_PFX is not set. Refusing to produce an unsigned release.");
                log.Error("       An unsigned build gets the full SmartScreen wall AND disables");
                log.Error("       UPDATETRUST_01 publisher pinning, which downgrades auto-update to a");
                log.Error("       hash supplied by the same document that supplies the download URL.");
                log.Error("       To sign:              set FVS_SIGN_PFX and FVS_SIGN_PASS, re-run Build.cmd.");
                log.Error("       To build unsigned:    set FVS_ALLOW_UNSIGNED=1 (acknowledged, recorded, NOT for release).");
                return false;
            }

            log.Info("[Sign] ==========================================================================");
            log.Info("[Sign] UNSIGNED BUILD - FVS_ALLOW_UNSIGNED=1 was set.");
            log.Info("[Sign] SmartScreen will warn end users, and this binary cannot act as an");
            log.Info("[Sign] update trust anchor (UPDATETRUST_01). DO NOT PUBLISH THIS ARTIFACT.");
            log.Info("[Sign] ==========================================================================");
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
