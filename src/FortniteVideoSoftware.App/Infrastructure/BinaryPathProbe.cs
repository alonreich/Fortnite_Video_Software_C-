// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md, docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.IO;
using System.Linq;
using IOPath = System.IO.Path;

namespace FortniteVideoSoftware.App.Infrastructure;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// BINPATH_01 — WHERE THE APP LOOKS FOR ITS BUNDLED ffmpeg/ffprobe, AND THE FACT THAT IT DOES NOT
/// AGREE WITH ITSELF.
///
/// ⚠️⚠️ THIS TYPE EXISTS TO MAKE A DIVERGENCE VISIBLE, NOT TO RESOLVE IT. ⚠️⚠️
///
/// There are THREE different implementations of "find the bundled binary" in this solution:
///
///   1. <c>Core/Infrastructure/BinaryPathResolver.Resolve</c> — searches
///      <c>&lt;baseDir&gt;/&lt;dir&gt;/&lt;file&gt;</c> then the five-level source-root fallback.
///   2. <c>CropToolWindow.ResolveBinaryPath</c> — EIGHT candidates, rooted at
///      <c>Environment.ProcessPath</c>'s directory, probing <c>preferred</c>, <c>backend</c>,
///      <c>frontend</c>, the process directory itself, the source root, then two
///      <c>Environment.CurrentDirectory</c> variants.
///   3. <c>VoiceOverWindow.ResolveBinaryPath</c> — FOUR candidates, and it roots the PREFERRED
///      probe at <c>AppContext.BaseDirectory</c> where the Crop Tool roots it at the PROCESS
///      directory.
///
/// THOSE TWO ROOTS ARE NOT THE SAME DIRECTORY. This app publishes <c>SelfContained</c> with
/// <c>PublishAot</c>; for a single-file host, <c>AppContext.BaseDirectory</c> can point at the
/// extraction directory while <c>Environment.ProcessPath</c> points at the .exe the user launched.
/// Two windows of the same application can therefore resolve DIFFERENT ffmpeg binaries, or one can
/// find it where the other reports failure — and the symptom is an inscrutable FFmpeg error in one
/// feature only.
///
/// ⚠️ THE TWO BODIES BELOW ARE MOVED VERBATIM AND KEPT SEPARATE ON PURPOSE. Picking one search
/// order for both is a BEHAVIOUR CHANGE, not a refactor: whichever window currently depends on the
/// order it has would start resolving differently, and which one is "right" depends on how the
/// product is actually installed and launched — something that must be verified on a real install,
/// not decided from the source. Consolidating them is the follow-up task; this step only ends the
/// situation where the divergence is invisible because the two copies sit 5,000 lines apart in two
/// different files.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
internal static class BinaryPathProbe
{
    internal static string ResolveForCropTool(string fileName, string preferredSubdirectory)
    {
        string processDir = IOPath.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string baseDir = AppContext.BaseDirectory;
        string sourceRootCandidate = IOPath.GetFullPath(IOPath.Combine(baseDir, "..", "..", "..", "..", "..", "binaries", fileName));

        string[] candidates =
        [
            IOPath.Combine(processDir, preferredSubdirectory, fileName),
            IOPath.Combine(processDir, "backend", fileName),
            IOPath.Combine(processDir, "frontend", fileName),
            IOPath.Combine(processDir, fileName),
            sourceRootCandidate,
            IOPath.Combine(Environment.CurrentDirectory, "binaries", fileName),
            IOPath.Combine(Environment.CurrentDirectory, preferredSubdirectory, fileName),
            fileName
        ];

        return candidates.FirstOrDefault(File.Exists) ?? fileName;
    }

    internal static string ResolveForVoiceOver(string fileName, string preferredSubdirectory)
    {
        string processDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string baseDir = AppContext.BaseDirectory;
        string sourceRootCandidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, "..", "..", "..", "..", "..", "binaries", fileName));
        
        string preferredPath = System.IO.Path.Combine(baseDir, preferredSubdirectory, fileName);
        if (System.IO.File.Exists(preferredPath)) return preferredPath;
        
        string rootPath = System.IO.Path.Combine(baseDir, fileName);
        if (System.IO.File.Exists(rootPath)) return rootPath;
        
        string debugPath = System.IO.Path.Combine(processDir, fileName);
        if (System.IO.File.Exists(debugPath)) return debugPath;

        if (System.IO.File.Exists(sourceRootCandidate)) return sourceRootCandidate;

        return fileName;
    }
}
