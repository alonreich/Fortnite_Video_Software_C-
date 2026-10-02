
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// ARCHTEST_01 — locates the repository from the test binary's own folder.
///
/// <para>
/// The architecture tests read SOURCE, not compiled metadata. That is the point: the rules they
/// enforce are about things a compiler is happy with — a Command and a Click on one button, a
/// hex literal in a style, an empty catch — which is exactly why those defects survived review
/// and ended up documented in prose instead.
/// </para>
/// </summary>
internal static class RepoRoot
{
    private static readonly Lazy<string> Located = new(Locate);

    public static string Path => Located.Value;

    private static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "FreeVideoStudio.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find FreeVideoStudio.sln walking up from {AppContext.BaseDirectory}. "
          + "The architecture tests read source files and need the repository root.");
    }

    /// <summary>Every production source file under src/, excluding bin/ and obj/.</summary>
    public static IReadOnlyList<string> SourceFiles(params string[] extensions)
    {
        string src = System.IO.Path.Combine(Path, "src");
        return Directory
            .EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Repo-relative path, for readable assertion messages.</summary>
    public static string Relative(string absolute)
        => absolute.StartsWith(Path, StringComparison.OrdinalIgnoreCase)
            ? absolute[(Path.Length + 1)..]
            : absolute;

    /// <summary>
    /// ARCHTEST_01 — builds an absolute path under src/ for source-level assertions.
    /// Added by ARCHTEST_01 consumers; pure path composition, no new discovery behaviour.
    /// </summary>
    public static string SourcePath(params string[] relativeParts)
        => System.IO.Path.Combine(new[] { Path }.Concat(relativeParts).ToArray());
}
