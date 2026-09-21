// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using FvsVerify;

// ══════════════════════════════════════════════════════════════════════════════════════════════
// SYS-VERIFYTOOL — THE PRE-BUILD HALT, AS A PROGRAM.
//
// dev.cmd calls this and tests the exit code. That is the whole of dev.cmd's involvement now:
// it no longer parses a list, no longer seeks labels, and no longer has an opinion about
// brackets. Exit 0 = every sentinel resolved. Exit 1 = halt, with the reason printed.
//
// ⚠️ THE EXIT CODE IS THE PRODUCT. VERIFYHALT_01 is the record of what happens when the answer
//    is computed and not returned: this check existed for months and could not fail.
// ══════════════════════════════════════════════════════════════════════════════════════════════

string start = args.Length > 0 ? args[0] : Environment.CurrentDirectory;

string? root = SentinelList.FindRepositoryRoot(start);
if (root is null)
{
    Console.Error.WriteLine(
        $"FIX SENTINEL CHECK FAILED: could not find {SentinelList.RelativePath} at or above '{start}'.");
    return 1;
}

IReadOnlyList<Sentinel> sentinels;
IReadOnlyList<string> malformed;
try
{
    sentinels = SentinelList.Load(root, out malformed);
}
catch (Exception ex)
{
    // Unreadable list == unverified build. It does not degrade to "assume fine".
    Console.Error.WriteLine($"FIX SENTINEL CHECK FAILED: {SentinelList.RelativePath} could not be read. {ex.Message}");
    return 1;
}

IReadOnlyList<SentinelResult> failures = SentinelList.Check(root, sentinels);

if (malformed.Count == 0 && failures.Count == 0)
{
    Console.WriteLine($"Fix sentinels: {sentinels.Count} checked, all present.");
    return 0;
}

Console.Error.WriteLine(SentinelList.FormatFailures(malformed, failures));
return 1;
