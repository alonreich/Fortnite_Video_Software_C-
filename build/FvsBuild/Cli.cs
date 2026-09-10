using System.ComponentModel;
using System.Diagnostics;

namespace FvsBuild;

/// <summary>
/// Process plumbing shared by every stage of the pipeline. Everything goes through
/// ProcessStartInfo.ArgumentList, so no shell quoting, no cmd.exe parsing and no
/// locale-dependent for-loops are involved anywhere in the build.
/// </summary>
internal static class Cli
{
    /// <summary>Runs a child process, streaming stdout+stderr to console+log live. Returns its exit code.</summary>
    public static int RunStreaming(string fileName, IEnumerable<string> args, BuildLog log)
    {
        using Process process = Start(fileName, args, redirect: true);
        // Output events arrive on threadpool threads; BuildLog is internally locked.
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log.Info(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.Info(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>Runs a child process with output captured (never shown). Returns its exit code.</summary>
    public static int RunQuiet(string fileName, IEnumerable<string> args)
    {
        try
        {
            using Process process = Start(fileName, args, redirect: true);
            process.Start();
            // Drain both pipes concurrently; a full stderr pipe would otherwise deadlock.
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEnd();
            _ = stderr.Result;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return -1;
        }
    }

    /// <summary>Captures stdout as lines for for/f-style single-token probes (gh, vswhere).</summary>
    public static bool TryCapture(string fileName, IEnumerable<string> args, out List<string> lines)
    {
        try
        {
            using Process process = Start(fileName, args, redirect: true);
            process.Start();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            lines = [.. process.StandardOutput.ReadToEnd().Split('\n').Select(l => l.Trim('\r')).Where(l => l.Length > 0)];
            _ = stderr.Result;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            lines = [];
            return false;
        }
    }

    private static Process Start(string fileName, IEnumerable<string> args, bool redirect)
    {
        ProcessStartInfo info = new()
        {
            FileName = fileName,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
        };
        foreach (string argument in args)
        {
            info.ArgumentList.Add(argument);
        }
        return new Process { StartInfo = info };
    }

    /// <summary>Finds an executable on PATH (entries may be quoted on Windows).</summary>
    public static string? FindOnPath(string fileName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        foreach (string raw in path.Split(Path.PathSeparator))
        {
            string dir = raw.Trim().Trim('"');
            if (dir.Length == 0)
            {
                continue;
            }
            try
            {
                string candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry; keep probing.
            }
        }
        return null;
    }

    /// <summary>
    /// Ensures the MSVC linker (link.exe) is reachable for NativeAOT. When it is not on
    /// PATH, sources VsDevCmd.bat inside a child cmd.exe, captures the resulting `set`
    /// block and applies it to THIS process so every later dotnet publish inherits it.
    /// </summary>
    public static bool EnsureMsvcToolchain(BuildLog log)
    {
        if (FindOnPath("link.exe") is not null)
        {
            log.Info("Native AOT toolchain detected.");
            return true;
        }

        string? vswhere = FindOnPath("vswhere.exe");
        if (vswhere is null)
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (File.Exists(fallback))
            {
                vswhere = fallback;
            }
        }
        if (vswhere is null)
        {
            log.Error("ERROR: Native AOT platform linker (link.exe) not found in PATH.");
            log.Error("ERROR: Open a Developer Command Prompt or install Visual Studio C++ build tools.");
            return false;
        }

        if (!TryCapture(vswhere,
                ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"],
                out List<string> installLines) ||
            installLines.Count == 0)
        {
            log.Error("ERROR: vswhere found no installation with the VC.Tools.x86.x64 component.");
            return false;
        }

        string devCmd = Path.Combine(installLines[0], "Common7", "Tools", "VsDevCmd.bat");
        if (!File.Exists(devCmd))
        {
            log.Error($"ERROR: VsDevCmd.bat not found at {devCmd}");
            return false;
        }

        Environment.SetEnvironmentVariable("VSCMD_SKIP_SENDTELEMETRY", "1");
        if (!CaptureEnvironmentFrom(devCmd))
        {
            log.Error("ERROR: could not import the VS developer environment (VsDevCmd.bat failed).");
            return false;
        }

        if (FindOnPath("link.exe") is null)
        {
            log.Error("ERROR: Native AOT platform linker (link.exe) not found after VsDevCmd.");
            return false;
        }
        log.Info("Native AOT toolchain detected.");
        return true;
    }

    private static bool CaptureEnvironmentFrom(string devCmd)
    {
        ProcessStartInfo info = new()
        {
            FileName = "cmd.exe",
            // The outer extra quotes are cmd.exe's quoted-command rule, not C# noise.
            Arguments = $"/d /c \"\"{devCmd}\" -arch=x64 -host_arch=x64 >nul 2>&1 && set\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        try
        {
            using Process process = new() { StartInfo = info };
            process.Start();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            string[] variables = process.StandardOutput.ReadToEnd().Split('\n');
            _ = stderr.Result;
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return false;
            }
            foreach (string raw in variables)
            {
                string line = raw.Trim('\r');
                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }
                Environment.SetEnvironmentVariable(line[..separator], line[(separator + 1)..]);
            }
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
