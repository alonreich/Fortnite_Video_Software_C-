// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>
/// STARTER_01 / MEME_02 — everything the app knows about the media it SHIPS WITH.
///
/// ⚠️ WHY THIS FILE EXISTS. New installations used to arrive with three completely empty
/// libraries. Two independent faults caused it, and either one alone was enough:
///   1. Build.cmd packaged only the program and its codecs — no media reached payload.zip.
///   2. The seeding code looked for an `mp3` folder FIVE LEVELS ABOVE the executable, which is
///      the DEV TREE layout (bin/Debug/net9.0-windows/win-x64 -> repo root). On a real machine
///      that path is nowhere, the fallback was the install directory which had no media either,
///      so every File.Exists() returned false and nothing was ever copied. Silently.
/// The starter media now ships in `starter\` NEXT TO THE PROGRAM, which exists identically in a
/// dev run and a real install — so `dev.cmd` finally rehearses production instead of being the
/// only place the feature worked.
/// </summary>
public static class MemeAssets
{
    /// <summary>Folder next to the executable holding the shipped starter media.</summary>
    private const string StarterFolderName = "starter";

    /// <summary>
    /// One marker per category, written after a successful delivery.
    ///
    /// ⚠️ THIS IS WHAT MAKES "DELETED MEANS DELETED" WORK. Without it, seeding would re-copy on
    /// every launch and a starter file the user deliberately removed would keep coming back —
    /// which is infuriating and looks like a bug. The marker records that the category was
    /// delivered ONCE; after that the user owns the folder entirely.
    /// </summary>
    private static string MarkerPath(string category) =>
        Path.Combine(FreeVideoStudio.Core.Infrastructure.ApplicationPaths.CreateDefault().ProgramDataRoot,
                     $"starter_{category}.delivered");

    /// <summary>STARTER_01 — the song category: repo `mp3\`, shipped as `starter\mp3`.</summary>
    public const string SongCategory = "mp3";

    /// <summary>
    /// MEMEFOLDER_01 — the ONE meme category: repo `meme\`, shipped as `starter\meme`. Video and
    /// picture memes used to be two categories (`mp4`, `jpeg`) with two delivery markers.
    /// </summary>
    public const string MemeCategoryFolder = "meme";

    /// <summary>
    /// MEMEFOLDER_01 — delivery markers written by builds that still had one category per kind.
    /// A user who already received the old starter set has had their chance to delete files, so
    /// one of these markers counts as "meme starter delivered" and nothing is copied again.
    /// Anything new in the starter set reaches existing users through "Download more".
    /// </summary>
    private static readonly string[] LegacyMemeCategories = { "mp4", "jpeg" };

    /// <summary>
    /// The exact files shipped in the installer, per category.
    /// ⚠️ THESE NAMES ARE A CONTRACT WITH build/FvsBuild/Staging.cs (StarterMp3 / StarterMeme).
    /// Rename a file in one place and it silently stops being delivered — STARTERLIST_01:
    /// "What the fuck am I doing here (Robert Deniro).mp4" was split into Landscape and Portrait
    /// files, both lists kept the old name, and staging refused to build.
    /// ArchitectureRuleTests.StarterListsMatchTheStagingLists keeps the two lists identical.
    /// Deliberately a handful — the full mp3 library alone is 197 MB; everything else is one click
    /// away via the per-category download buttons.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> StarterFiles =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [SongCategory] = new[]
            {
                "Bonnie Tyler - Holding Out For A Hero.mp3",
                "Cool Dance Background Music (No CopyRights).mp3",
            },
            [MemeCategoryFolder] = new[]
            {
                "What the fuck am I doing here (Robert Deniro) - Landscape.mp4",
                "What the fuck am I doing here (Robert Deniro) - Portrait.mp4",
                "Donald Trump - He Died like a Dog.mp4",
                "I will find you and I will kill you.mp4",
                "Terminated.png",
                "What The Fuck.jpg",
                "oopsie.png",
            },
        };

    /// <summary>
    /// MEME_02 — memes that make sense at the START of a video rather than the end.
    ///
    /// ⚠️ NOTHING CAN DETECT THIS FROM THE VIDEO ITSELF. "This is an intro meme" is a judgement
    /// about meaning, not a measurable property, so it has to be stated. This list gives the
    /// SHIPPED memes a sensible default; the user's own choice, once made, is remembered per file
    /// and always wins (see MemePlacementStore). Anything not listed defaults to the End, which is
    /// the safe, conventional case.
    /// </summary>
    private static readonly HashSet<string> PrependByDefault =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "I will find you and I will kill you.mp4",
        };

    /// <summary>True when this meme should default to playing BEFORE the video.</summary>
    public static bool DefaultsToStart(string memePath) =>
        !string.IsNullOrWhiteSpace(memePath) &&
        PrependByDefault.Contains(Path.GetFileName(memePath));

    private static string StarterRoot()
    {
        string baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        return Path.Combine(baseDir, StarterFolderName);
    }

    /// <summary>
    /// Copies the shipped starter media for one category into <paramref name="destination"/>.
    ///
    /// NON-DESTRUCTIVE BY CONSTRUCTION: a file that already exists by name is skipped, never
    /// overwritten — the user's own edits and their own files of the same name always win.
    /// Runs at most once per category thanks to the delivery marker.
    /// Returns the number of files actually copied.
    /// </summary>
    public static int DeliverStarter(string category, string destination)
    {
        int copied = 0;
        try
        {
            string marker = MarkerPath(category);
            if (File.Exists(marker)) return 0;

            // MEMEFOLDER_01 — honour a delivery made under the old per-kind categories.
            if (string.Equals(category, MemeCategoryFolder, StringComparison.OrdinalIgnoreCase) &&
                LegacyMemeCategories.Any(c => File.Exists(MarkerPath(c))))
            {
                File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
                RuntimeLog.Info("Starter", "Meme starter set was already delivered by an older build; nothing copied.");
                return 0;
            }

            string src = Path.Combine(StarterRoot(), category);
            if (!Directory.Exists(src))
            {
                RuntimeLog.Info("Starter", $"No starter '{category}' folder shipped with this build — skipping.");
                return 0;
            }

            Directory.CreateDirectory(destination);

            string[] wanted = StarterFiles.TryGetValue(category, out var list) ? list : Array.Empty<string>();

            foreach (string name in wanted)
            {
                try
                {
                    string from = Path.Combine(src, name);
                    string to = Path.Combine(destination, name);
                    if (!File.Exists(from)) continue;
                    if (File.Exists(to)) continue;
                    File.Copy(from, to);
                    copied++;
                }
                catch (Exception ex)
                {
                    RuntimeLog.Info("Starter", $"Could not deliver '{name}': {ex.Message}");
                }
            }

            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
            RuntimeLog.Info("Starter",
                copied > 0
                    ? $"Delivered {copied} starter file(s) to {destination}. This happens once — deleting them keeps them deleted."
                    : $"Starter '{category}' already present in {destination}; nothing copied.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("Starter", $"Starter delivery for '{category}' skipped: {ex.Message}");
        }
        return copied;
    }
}
