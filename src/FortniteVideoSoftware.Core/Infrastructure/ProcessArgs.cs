using System.Collections.Generic;
using System.Linq;

namespace FortniteVideoSoftware.Core.Infrastructure;

/// <summary>
/// ISSUE_07 — shared helper for the ONE remaining legitimate use of a joined command string:
/// writing a copy-pasteable line into the DEBUG log.
///
/// BACKGROUND. Every FFmpeg/FFprobe invocation in this codebase must be launched through
/// <c>ProcessStartInfo.ArgumentList</c>, never through a hand-assembled <c>Arguments</c> string.
/// The old hand-built strings only wrapped an argument in quotes when it contained a SPACE, so:
///   * a path containing a double-quote broke the command line outright, and
///   * a path ending in a backslash escaped its own closing quote and swallowed the next argument.
/// Either way the user got an inscrutable FFmpeg error with no clue that their FOLDER NAME was
/// the cause. <c>ArgumentList</c> applies the real Windows CreateProcess escaping rules per
/// argument and removes the whole class of bug.
///
/// The formatted string produced here is for HUMAN EYES ONLY. It must never be fed to
/// <c>ProcessStartInfo.Arguments</c>.
/// </summary>
public static class ProcessArgs
{
    /// <summary>
    /// Renders an argument list as a readable, copy-pasteable command line for the debug log.
    /// NEVER use the result to launch a process.
    /// </summary>
    public static string FormatForLog(IEnumerable<string> args)
    {
        return string.Join(" ", args.Select(a =>
            a.Length == 0 || a.Contains(' ') || a.Contains('"')
                ? "\"" + a.Replace("\"", "\\\"") + "\""
                : a));
    }
}
