
using System;
using System.Collections.Generic;
using FvsVerify;


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
