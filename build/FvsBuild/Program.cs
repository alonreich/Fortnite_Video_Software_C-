using System.Globalization;

namespace FvsBuild;

/// <summary>
/// FvsBuild - structured replacement for the old monolithic Build.cmd.
/// Pipeline: terminate processes -> clean -> detect MSVC toolchain -> NativeAOT publish
/// to staging -> stage backend/frontend/starter files -> zip payload (System.IO.Compression)
/// -> NativeAOT publish of the installer (embeds payload.zip) -> move to .\compiled ->
/// Authenticode sign if configured -> validate exactly one exe -> cleanup -> optional
/// GitHub release replacement.
/// Exit codes (consumed by CI and by Build.cmd): 0 = OK, 1 = BUILD FAILED, 2 = build OK
/// but the GitHub publish did not complete.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitBuildFailed = 1;
    private const int ExitPublishIncomplete = 2;

    private static int Main(string[] args)
    {
        bool noPublish = args.Any(a => a.Equals("--no-publish", StringComparison.OrdinalIgnoreCase));
        bool devMode = args.Any(a => a.Equals("--dev", StringComparison.OrdinalIgnoreCase));
        bool zipOnly = args.Any(a => a.Equals("--zip-only", StringComparison.OrdinalIgnoreCase));
        if (args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase) || a.Equals("-h", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("Usage: FvsBuild [--no-publish] [--dev]");
            Console.WriteLine("  (default)   build .\\compiled\\FortniteVideoSoftware.exe, then replace the GitHub release");
            Console.WriteLine("  --no-publish  build only; GitHub is not touched");
            Console.WriteLine("  --dev         local dev build: no GitHub, and .\\compiled is not wiped first");
            Console.WriteLine("  --zip-only    diagnostic: stage dependencies and create payload.zip, then stop.");
            Console.WriteLine("                Leaves src\\FortniteVideoSoftware.App\\payload.zip and the staging");
            Console.WriteLine("                folder in place for inspection. GitHub and .\\compiled are untouched.");
            return ExitOk;
        }

        // The orchestrator may be launched from any CWD (CI checkout, double-click);
        // anchor everything to the repository that contains this build project.
        Environment.CurrentDirectory = FindRepoRoot();
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }

        DateTimeOffset started = DateTimeOffset.Now;
        using BuildLog log = new("build.log");
        int exitCode = RunPipeline(log, noPublish, devMode, zipOnly, started);
        return exitCode;
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FortniteVideoSoftware.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return Environment.CurrentDirectory;
    }

    private static int RunPipeline(BuildLog log, bool noPublish, bool devMode, bool zipOnly, DateTimeOffset started)
    {
        // yyyy.MM.dd.HHmm - each part must stay under 65535 for a Windows version resource,
        // and the time keeps two same-day builds from colliding on one tag. Computed ONCE
        // so the exe, the release tag and the published notes all say the same thing.
        string buildVersion = DateTime.Now.ToString("yyyy.MM.dd.HHmm", CultureInfo.InvariantCulture);
        string tag = "v" + buildVersion;
        string flavor = devMode ? "Local Dev" : "NativeAOT win-x64";
        log.Info($"Build version: {buildVersion}  (tag {tag})");

        try
        {
            if (zipOnly)
            {
                // Diagnostic path for CI and hand checks: reproduce the exact payload.zip the
                // shipping build embeds, without publishing the installer. Nothing outside
                // obj\StandaloneTemp and src\...\App\payload.zip is touched.
                log.Banner("###########################################################");
                log.Banner("ZIP-ONLY: staging + payload.zip, then stop. .\\compiled is untouched.");
                log.Banner("###########################################################");
                Staging.PurgeTempArtifacts(log);
                bool staged = Staging.Publish(Staging.StagingDirPath, buildVersion, log, "Publishing raw payload to staging...")
                              && Staging.StageDependencies(log)
                              && Staging.CreatePayloadZip(log);
                if (!staged)
                {
                    return FailBuild(log, started);
                }
                log.Banner("###########################################################");
                log.Success($"SUCCESS: payload.zip diagnostic build completed: {Staging.PayloadZipPath}");
                log.Info("Staging folder kept for inspection: .\\obj\\StandaloneTemp\\Staging");
                log.Banner("###########################################################");
                log.WriteVerdictAndClose(ExitOk, DateTimeOffset.Now - started);
                return ExitOk;
            }

            log.Banner("###########################################################");
            log.Banner(devMode ? "PREPARING LOCAL BUILD ENVIRONMENT..." : "PURGING PREVIOUS BUILD ARTIFACTS...");
            log.Banner("###########################################################");
            Staging.TerminateProcesses(log);
            Staging.PrepareCompiledDir(log, devMode);
            Staging.CleanAll(log);

            log.Banner(string.Empty);
            log.Banner("###########################################################");
            log.Banner("CHECKING NATIVE AOT TOOLCHAIN...");
            log.Banner("###########################################################");
            if (!Cli.EnsureMsvcToolchain(log))
            {
                return FailBuild(log, started);
            }

            log.Banner(string.Empty);
            log.Banner("###########################################################");
            log.Banner($"BUILDING Fortnite Video Software: {flavor}");
            log.Banner("###########################################################");
            if (!RunNativeAotStages(log, buildVersion))
            {
                return FailBuild(log, started);
            }

            log.Banner(string.Empty);
            log.Banner("###########################################################");
            log.Success(devMode
                ? "SUCCESS: Local dev build completed successfully."
                : "SUCCESS: Build completed successfully.");
            log.Banner(string.Empty);
            log.Info($"Native EXE: {Staging.CompiledExePath}");
            log.Info("Log file:  .\\build.log  (first line: OK / WARN / FAIL)");
            log.Banner("###########################################################");

            if (noPublish || devMode)
            {
                if (!devMode)
                {
                    log.Info(string.Empty);
                    log.Info("[PUBLISH] Skipped on request (--no-publish). GitHub was not touched.");
                }
                log.WriteVerdictAndClose(ExitOk, DateTimeOffset.Now - started);
                return ExitOk;
            }

            log.Banner(string.Empty);
            log.Banner("###########################################################");
            log.Banner("PUBLISHING RELEASE TO GITHUB...");
            log.Banner("###########################################################");
            bool published = GitHubReleasePublisher.Publish(Staging.CompiledExePath, tag, log);
            log.WriteVerdictAndClose(published ? ExitOk : ExitPublishIncomplete, DateTimeOffset.Now - started);
            if (published)
            {
                return ExitOk;
            }
            log.Banner(string.Empty);
            log.Banner("###########################################################");
            log.Warn("BUILD OK - but the release was NOT updated.");
            log.Warn(".\\compiled\\FortniteVideoSoftware.exe is good and usable.");
            log.Warn("Only the GitHub publish step did not finish - reason above.");
            log.Banner("###########################################################");
            return ExitPublishIncomplete;
        }
        catch (Exception ex)
        {
            log.Error($"ERROR: unhandled build failure: {ex}");
            return FailBuild(log, started);
        }
    }

    /// <summary>The :BUILD_NATIVE ladder: staging publish, dependency staging, payload zip, installer publish, sign, validate, cleanup.</summary>
    private static bool RunNativeAotStages(BuildLog log, string buildVersion)
    {
        Staging.PurgeTempArtifacts(log);

        if (!Staging.Publish(Staging.StagingDirPath, buildVersion, log, "2. Publishing raw payload to staging..."))
        {
            return false;
        }
        if (!Staging.StageDependencies(log))
        {
            return false;
        }
        if (!Staging.CreatePayloadZip(log))
        {
            return false;
        }
        if (!Staging.Publish(Staging.FinalDirPath, buildVersion, log, "4. Publishing standalone installer..."))
        {
            return false;
        }
        if (!Staging.MoveFinalExe(log))
        {
            return false;
        }
        if (!CodeSigning.SignIfNeeded(Staging.CompiledExePath, log))
        {
            return false;
        }
        if (!Staging.ValidateCompiledOutput(log))
        {
            return false;
        }
        Staging.CleanupTempArtifacts(log);
        return true;
    }

    private static int FailBuild(BuildLog log, DateTimeOffset started)
    {
        log.Banner(string.Empty);
        log.Banner("###########################################################");
        log.Error("BUILD FAILED - nothing was published.");
        log.Error("The existing GitHub release was NOT touched.");
        log.Error("Scroll up for the first ERROR line, or read .\\build.log");
        log.Banner("###########################################################");
        log.WriteVerdictAndClose(ExitBuildFailed, DateTimeOffset.Now - started);
        PauseIfInteractive();
        return ExitBuildFailed;
    }

    /// <summary>The old Build.cmd paused on failure for double-click users; skip that in CI (redirected input).</summary>
    private static void PauseIfInteractive()
    {
        if (Console.IsInputRedirected)
        {
            return;
        }
        try
        {
            Console.Write("Press any key to continue . . . ");
            _ = Console.ReadKey(intercept: true);
            Console.WriteLine();
        }
        catch (InvalidOperationException)
        {
            // No interactive console at all; nothing to pause on.
        }
    }
}
