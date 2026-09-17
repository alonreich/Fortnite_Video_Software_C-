using System.IO.Compression;

namespace FvsBuild;

/// <summary>
/// Step 1-2 of the pipeline: clean, stage the NativeAOT payload (app publish output +
/// backend/frontend native binaries + starter media) and zip it into
/// src\FortniteVideoSoftware.App\payload.zip with System.IO.Compression, replacing the
/// old external tar.exe call. Every filename here is a contract: DeploymentLifecycle
/// extracts this zip on launch and MemeAssets.StarterFiles lists the starter names.
/// </summary>
internal static class Staging
{
    private const string ProjectFile = @"src\FortniteVideoSoftware.App\FortniteVideoSoftware.App.csproj";
    private const string ProjectExe = "FortniteVideoSoftware.App.exe";
    private const string OutputExe = "FortniteVideoSoftware.exe";
    private const string OutputDir = @".\compiled";
    private const string StagingDir = @".\obj\StandaloneTemp\Staging";
    private const string FinalDir = @".\obj\StandaloneTemp\NativeAot_final";
    private const string PayloadZip = @"src\FortniteVideoSoftware.App\payload.zip";

    private static readonly string[] BackendExact =
    [
        "ffmpeg.exe",
        "ffprobe.exe",
    ];

    // Wildcard families: each must yield at least one DLL or the build fails loudly
    // (a silent zero-file copy once shipped an installer with no codecs).
    private static readonly string[] BackendWildcards = ["av*.dll", "sw*.dll", "postproc*.dll"];

    private static readonly string[] FrontendExact = ["libmpv-2.dll", "mpv.exe"];

    private static readonly string[] StarterMp3 =
    [
        "Bonnie Tyler - Holding Out For A Hero.mp3",
        "Cool Dance Background Music (No CopyRights).mp3",
    ];

    private static readonly string[] StarterMp4 =
    [
        "What the fuck am I doing here (Robert Deniro).mp4",
        "Donald Trump - He Died like a Dog.mp4",
        "I will find you and I will kill you.mp4",
    ];

    private static readonly string[] StarterJpegWildcards = ["*.png", "*.jpg"];

    public static string CompiledExePath => Path.Combine(OutputDir, OutputExe);
    public static string StagingDirPath => StagingDir;
    public static string FinalDirPath => FinalDir;
    public static string PayloadZipPath => PayloadZip;

    /// <summary>Kills any running copies of the product and stops dotnet build servers, so locked files cannot survive a clean.</summary>
    public static void TerminateProcesses(BuildLog log)
    {
        _ = Cli.RunQuiet("taskkill.exe", ["/F", "/IM", "FortniteVideoSoftware.exe", "/T"]);
        _ = Cli.RunQuiet("taskkill.exe", ["/F", "/IM", "FortniteVideoSoftware.App.exe", "/T"]);
        _ = Cli.RunQuiet("dotnet", ["build-server", "shutdown"]);
    }

    /// <summary>Wipes bin/obj of both product projects and runs dotnet clean (parity with the old :CLEAN_ALL).</summary>
    public static void CleanAll(BuildLog log)
    {
        foreach (string directory in new[]
                 {
                     @"src\FortniteVideoSoftware.App\bin",
                     @"src\FortniteVideoSoftware.App\obj",
                     @"src\FortniteVideoSoftware.Core\bin",
                     @"src\FortniteVideoSoftware.Core\obj",
                 })
        {
            TryDeleteDirectory(directory);
        }
        _ = Cli.RunQuiet("dotnet", ["clean", ProjectFile, "-c", "Release", "-r", "win-x64", "--nologo", "-v", "q"]);
    }

    /// <summary>Release mode wipes .\compiled entirely; dev mode only ensures it exists (old Build.cmd vs dev_build.cmd).</summary>
    public static void PrepareCompiledDir(BuildLog log, bool devMode)
    {
        if (devMode)
        {
            Directory.CreateDirectory(OutputDir);
            return;
        }
        TryDeleteDirectory(OutputDir);
        Directory.CreateDirectory(OutputDir);
    }

    /// <summary>Removes stale staging folders and any leftover payload.zip before the payload publish runs.</summary>
    public static void PurgeTempArtifacts(BuildLog log)
    {
        log.Info("[NativeAOT] 1. Purging old temp folders...");
        TryDeleteDirectory(StagingDir);
        TryDeleteDirectory(FinalDir);
        TryDeleteFile(PayloadZip);
    }

    /// <summary>Runs one of the two identical NativeAOT publishes (staging pass or final installer pass).</summary>
    public static bool Publish(string outputDir, string buildVersion, BuildLog log, string stepTitle)
    {
        log.Info($"[NativeAOT] {stepTitle}");
        List<string> arguments =
        [
            "publish", ProjectFile,
            "-c", "Release",
            "-r", "win-x64",
            "-p:TreatWarningsAsErrors=true",
            "-p:PublishAot=true",
            "-p:SelfContained=true",
            "-p:AssemblyVersion=" + buildVersion,
            "-p:Version=" + buildVersion,
            "-p:FileVersion=" + buildVersion,
            "-p:InformationalVersion=" + buildVersion,
            "-o", outputDir,
            "-consoleLoggerParameters:ErrorsOnly",
        ];
        return Cli.RunStreaming("dotnet", arguments, log) == 0;
    }

    /// <summary>Step 2.5/2.6: stages backend codecs, the mpv frontend and the starter media next to the staged app.</summary>
    public static bool StageDependencies(BuildLog log)
    {
        log.Info("[NativeAOT] 2.5 Copying binaries to staging...");
        foreach (string group in new[] { "backend", "frontend" })
        {
            Directory.CreateDirectory(Path.Combine(StagingDir, group));
        }

        List<string> missing = [];

        foreach (string file in BackendExact)
        {
            if (!CopyTo($@".\binaries\{file}", Path.Combine(StagingDir, "backend")))
            {
                missing.Add($@"binaries\{file}");
            }
        }
        foreach (string wildcard in BackendWildcards)
        {
            string[] matches = Directory.Exists(@".\binaries")
                ? Directory.GetFiles(@".\binaries", wildcard)
                : [];
            if (matches.Length == 0)
            {
                missing.Add($@"binaries\{wildcard} (no match)");
                continue;
            }
            foreach (string match in matches)
            {
                CopyTo(match, Path.Combine(StagingDir, "backend"));
            }
        }

        foreach (string file in FrontendExact)
        {
            if (!CopyTo($@".\binaries\{file}", Path.Combine(StagingDir, "frontend")))
            {
                missing.Add($@"binaries\{file}");
            }
        }

        log.Info("[NativeAOT] 2.6 Copying starter media to staging...");
        string starterRoot = Path.Combine(StagingDir, "starter");
        foreach (string folder in new[] { "mp3", "mp4", "jpeg" })
        {
            Directory.CreateDirectory(Path.Combine(starterRoot, folder));
        }
        foreach (string file in StarterMp3)
        {
            if (!CopyTo($@".\mp3\{file}", Path.Combine(starterRoot, "mp3")))
            {
                missing.Add($@"mp3\{file}");
            }
        }
        foreach (string file in StarterMp4)
        {
            if (!CopyTo($@".\mp4\{file}", Path.Combine(starterRoot, "mp4")))
            {
                missing.Add($@"mp4\{file}");
            }
        }
        foreach (string wildcard in StarterJpegWildcards)
        {
            foreach (string match in Directory.Exists(@".\jpeg") ? Directory.GetFiles(@".\jpeg", wildcard) : [])
            {
                CopyTo(match, Path.Combine(starterRoot, "jpeg"));
            }
        }

        if (missing.Count > 0)
        {
            // Fail loudly rather than shipping an installer that silently seeds nothing.
            foreach (string file in missing)
            {
                log.Error($"ERROR: staging input missing: {file}");
            }
            log.Error("ERROR: the installer would be incomplete. Fix the inputs above and rebuild.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Step 3: creates payload.zip from the staging folder. System.IO.Compression with
    /// optimal compression replaces the old tar.exe call; entries use zip-standard
    /// forward slashes and no leading "./" so ZipFile.ExtractToDirectory on launch
    /// lands every file exactly where the old tar-built payload did.
    /// </summary>
    public static bool CreatePayloadZip(BuildLog log)
    {
        log.Info("[NativeAOT] 3. Zipping payload...");
        try
        {
            if (File.Exists(PayloadZip))
            {
                File.Delete(PayloadZip);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(PayloadZip)!);
            int entryCount = 0;
            using (ZipArchive archive = ZipFile.Open(PayloadZip, ZipArchiveMode.Create))
            {
                foreach (string file in Directory.EnumerateFiles(StagingDir, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(StagingDir, file).Replace('\\', '/');
                    _ = archive.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
                    entryCount++;
                }
            }
            // Entries cannot be read while the archive is open in Create mode; count them ourselves.
            log.Info($"[NativeAOT] Payload zipped: {entryCount} entries, {new FileInfo(PayloadZip).Length / (1024 * 1024)} MB.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            log.Error($"ERROR: Failed to zip payload: {ex.Message}");
            return false;
        }
    }

    /// <summary>Step 5: moves the NativeAOT exe into .\compiled under its shipped name.</summary>
    public static bool MoveFinalExe(BuildLog log)
    {
        log.Info("[NativeAOT] 5. Moving final EXE to compiled folder...");
        string source = Path.Combine(FinalDir, ProjectExe);
        if (!File.Exists(source))
        {
            log.Error($"ERROR: Expected NativeAOT EXE was not produced in {FinalDir}");
            return false;
        }
        Directory.CreateDirectory(OutputDir);
        File.Move(source, CompiledExePath, overwrite: true);
        return true;
    }

    /// <summary>
    /// Verifies .\compiled contains EXACTLY FortniteVideoSoftware.exe (MANDATE #2).
    /// Strays are removed, then reported as a failure - same as the old
    /// :PURGE_COMPILED_EXTRAS + :VALIDATE_COMPILED_OUTPUT pair.
    /// </summary>
    public static bool ValidateCompiledOutput(BuildLog log)
    {
        bool invalid = !File.Exists(CompiledExePath);
        foreach (string entry in Directory.EnumerateFileSystemEntries(OutputDir))
        {
            if (!string.Equals(Path.GetFileName(entry), OutputExe, StringComparison.OrdinalIgnoreCase))
            {
                log.Error($"ERROR: Removing disallowed artifact from compiled: {Path.GetFileName(entry)}");
                TryDeleteDirectory(entry);
                TryDeleteFile(entry);
                invalid = true;
            }
        }
        if (invalid)
        {
            log.Error($"ERROR: {OutputDir} must contain only {OutputExe}.");
            return false;
        }
        log.Success($"Verified {OutputDir} contains exactly {OutputExe}.");
        return true;
    }

    /// <summary>Step 6: removes staging folders and the transient payload.zip.</summary>
    public static void CleanupTempArtifacts(BuildLog log)
    {
        log.Info("[NativeAOT] 6. Cleaning up temporary artifacts...");
        TryDeleteDirectory(StagingDir);
        TryDeleteDirectory(FinalDir);
        TryDeleteFile(PayloadZip);
    }

    private static bool CopyTo(string source, string destinationFolder)
    {
        if (!File.Exists(source))
        {
            return false;
        }
        File.Copy(source, Path.Combine(destinationFolder, Path.GetFileName(source)), overwrite: true);
        return true;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked file here fails the publish or validation stage with a clearer message.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
