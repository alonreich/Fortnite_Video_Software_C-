// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using Xunit;

namespace FortniteVideoSoftware.Core.Tests;

/// <summary>
/// CITEST_01 — A TEST THAT CANNOT APPLY MUST SKIP, NOT FAIL.
///
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// <b>WHY THIS EXISTS.</b> <c>GracefulProcessTerminatorTests</c> spawns real <c>cmd.exe</c> and
/// <c>powershell.exe</c> children — that is the point of it, because the shutdown ladder is only
/// meaningful against a real OS process. Off Windows those five tests did not skip, they FAILED.
/// </para>
///
/// <para>
/// ⚠️ A suite that is EXPECTED to be red is a suite nobody reads. Five permanent failures were
/// sitting beside two REAL ones — <c>UndoStackTests.RestoringDoesNotRecordHistory</c> (the undo
/// re-entrancy guard did not cover the <c>Changed</c> handler) and
/// <c>ProjectDocumentTests.RoundTrip_PreservesEveryField</c> (the source fingerprint read back as
/// zero on every load) — and both were genuine product bugs that shipped. Nobody spotted them
/// because "the suite is red" was already the normal state. Making the inapplicable tests skip is
/// what lets a red result mean "something broke".
/// </para>
///
/// <para>CI runs on <c>windows-latest</c>, so nothing marked with this attribute skips there.</para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only: spawns cmd.exe / powershell.exe. Runs in CI on windows-latest.";
    }
}
