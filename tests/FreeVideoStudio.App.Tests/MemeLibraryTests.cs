// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md, docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FreeVideoStudio.App.Infrastructure;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// MEMEFOLDER_01/02, STARTERLIST_01, NOSPACE_01 — the meme library consolidation and the
/// space-free folder names.
/// </summary>
public sealed class MemeLibraryTests
{
    /// <summary>
    /// STARTERLIST_01 — the installer's staging list and the app's delivery list must name the
    /// same files. They drifted once (the De Niro clip was split into Landscape/Portrait and both
    /// lists kept the old name), which made staging refuse to build.
    /// </summary>
    [Fact]
    public void StarterListsMatchTheStagingLists()
    {
        string staging = File.ReadAllText(Path.Combine(RepoRoot.Path, "build", "FvsBuild", "Staging.cs"));
        Match block = Regex.Match(staging, @"StarterMeme\s*=\s*\[(?<body>.*?)\];", RegexOptions.Singleline);
        Assert.True(block.Success, "StarterMeme list not found in Staging.cs");
        string[] staged = Regex.Matches(block.Groups["body"].Value, "\"(?<n>[^\"]+)\"")
            .Select(m => m.Groups["n"].Value).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        string[] delivered = MemeAssets.StarterFiles[MemeAssets.MemeCategoryFolder]
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(staged, delivered);
    }

    /// <summary>STARTERLIST_01 — every shipped starter meme exists in the repository's meme folder.</summary>
    [Fact]
    public void EveryStarterMemeExistsInTheRepository()
    {
        string folder = Path.Combine(RepoRoot.Path, "meme");
        if (!Directory.Exists(folder)) return;   // media is not part of every checkout (CI without LFS)
        string[] missing = MemeAssets.StarterFiles[MemeAssets.MemeCategoryFolder]
            .Where(n => !File.Exists(Path.Combine(folder, n))).ToArray();
        Assert.True(missing.Length == 0, "Missing starter memes: " + string.Join(", ", missing));
    }

    /// <summary>MEMEFOLDER_01 — the per-kind repository folders are gone for good.</summary>
    [Fact]
    public void TheOldPerKindMemeFoldersAreGone()
    {
        Assert.False(Directory.Exists(Path.Combine(RepoRoot.Path, "mp4")), "mp4\\ was merged into meme\\");
        Assert.False(Directory.Exists(Path.Combine(RepoRoot.Path, "jpeg")), "jpeg\\ was merged into meme\\");
    }

    /// <summary>NOSPACE_01 — no folder the app creates has a space in its own name.</summary>
    [Fact]
    public void AppFoldersHaveNoSpaces()
    {
        foreach (string folder in new[]
                 {
                     DeploymentFootprint.InstallFolder,
                     DeploymentFootprint.ProgramDataFolder,
                     DeploymentFootprint.TempAppFolder,
                     MemeDirectory.GetDefault(),
                 })
        {
            string own = Path.GetFileName(folder);
            string parent = Path.GetFileName(Path.GetDirectoryName(folder)!);
            Assert.DoesNotContain(' ', own);
            if (folder == MemeDirectory.GetDefault()) Assert.DoesNotContain(' ', parent);
        }
    }

    /// <summary>MEMEFOLDER_02 — files move, nothing is overwritten, empty old folders go away.</summary>
    [Fact]
    public void MoveFolderContentsMovesWithoutOverwriting()
    {
        string root = Path.Combine(Path.GetTempPath(), "fvs_memefolder_" + Guid.NewGuid().ToString("N"));
        string oldParent = Path.Combine(root, "Free Video Studio");
        string oldDir = Path.Combine(oldParent, "Memes");
        string newDir = Path.Combine(root, "FreeVideoStudio", "Memes");
        try
        {
            Directory.CreateDirectory(oldDir);
            Directory.CreateDirectory(newDir);
            File.WriteAllText(Path.Combine(oldDir, "a.mp4"), "A");
            File.WriteAllText(Path.Combine(oldDir, "same.png"), "S");
            File.WriteAllText(Path.Combine(newDir, "same.png"), "S");
            File.WriteAllText(Path.Combine(oldDir, "clash.jpg"), "OLD");
            File.WriteAllText(Path.Combine(newDir, "clash.jpg"), "NEW");

            int moved = MemeDirectory.MoveFolderContents(oldDir, newDir);

            Assert.Equal(1, moved);
            Assert.Equal("A", File.ReadAllText(Path.Combine(newDir, "a.mp4")));
            Assert.Equal("NEW", File.ReadAllText(Path.Combine(newDir, "clash.jpg")));
            Assert.False(File.Exists(Path.Combine(oldDir, "same.png")), "an identical duplicate is removed");
            Assert.True(File.Exists(Path.Combine(oldDir, "clash.jpg")), "a different file is kept, never overwritten");
            Assert.True(Directory.Exists(oldDir), "a folder that still holds a kept file stays");

            File.Delete(Path.Combine(oldDir, "clash.jpg"));
            Assert.Equal(0, MemeDirectory.MoveFolderContents(oldDir, newDir));
            Assert.False(Directory.Exists(oldParent), "empty old folders are removed");
            Assert.Equal(-1, MemeDirectory.MoveFolderContents(oldDir, newDir));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>MEMECAT_01 — each category filters the single folder by extension.</summary>
    [Fact]
    public void CategoriesSplitTheSingleFolderByExtension()
    {
        Assert.Contains(".mp4", MemeCatalog.ExtensionsFor(MemeCategory.Video));
        Assert.DoesNotContain(".png", MemeCatalog.ExtensionsFor(MemeCategory.Video));
        Assert.Contains(".png", MemeCatalog.ExtensionsFor(MemeCategory.Image));
        Assert.Contains(".jpg", MemeCatalog.ExtensionsFor(MemeCategory.Image));
        Assert.DoesNotContain(".mp4", MemeCatalog.ExtensionsFor(MemeCategory.Image));
        Assert.Equal("meme", MemeCatalog.CloudMemeFolder);
    }
}
