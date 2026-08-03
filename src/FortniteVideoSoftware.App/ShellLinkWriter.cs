using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace FortniteVideoSoftware.App;

[SupportedOSPlatform("windows")]
internal static class ShellLinkWriter
{
    public static async Task CreateAsync(
        string shortcutPath,
        string targetPath,
        string workingDirectory,
        string iconLocation,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(shortcutPath))
        {
            throw new ArgumentException("shortcutPath must be provided.", nameof(shortcutPath));
        }

        if (string.IsNullOrEmpty(targetPath))
        {
            throw new ArgumentException("targetPath must be provided.", nameof(targetPath));
        }

        string? directory = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        StringBuilder script = new();
        script.Append("$ws = New-Object -ComObject WScript.Shell; ");
        script.AppendFormat("$s = $ws.CreateShortcut('{0}'); ", EscapePowerShellSingleQuoted(shortcutPath));
        script.AppendFormat("$s.TargetPath = '{0}'; ", EscapePowerShellSingleQuoted(targetPath));
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            script.AppendFormat("$s.WorkingDirectory = '{0}'; ", EscapePowerShellSingleQuoted(workingDirectory));
        }

        if (!string.IsNullOrEmpty(description))
        {
            script.AppendFormat("$s.Description = '{0}'; ", EscapePowerShellSingleQuoted(description));
        }

        if (!string.IsNullOrEmpty(iconLocation))
        {
            script.AppendFormat("$s.IconLocation = '{0}'; ", EscapePowerShellSingleQuoted(iconLocation));
        }

        script.Append("$s.Save();");

        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"{script}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start PowerShell for shortcut creation.");
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }

            throw new TimeoutException("PowerShell shortcut creation timed out.");
        }

        string stderrText = string.Empty;
        try
        {
            _ = await stdoutTask.ConfigureAwait(false);
            stderrText = await stderrTask.ConfigureAwait(false);
        }
        catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"PowerShell shortcut creation failed (ExitCode {process.ExitCode}): {stderrText}");
        }

        if (!File.Exists(shortcutPath))
        {
            throw new FileNotFoundException("PowerShell reported success but shortcut file was not created.", shortcutPath);
        }
    }

    private static string EscapePowerShellSingleQuoted(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }
}
