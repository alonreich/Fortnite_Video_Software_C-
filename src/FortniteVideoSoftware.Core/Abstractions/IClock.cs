// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.

namespace FortniteVideoSoftware.Core.Abstractions;

/// <summary>
/// INJSEAM_02 — the wall clock, behind an interface.
///
/// <para>
/// Small, but it is the difference between a test and a stopwatch. Three pieces of shipped logic
/// are pure functions of "what time is it": the update probe's 24-hour throttle
/// (<c>05_SYSTEM_LIFECYCLE_STORAGE.md</c> §6, SYS-AUTOUPDATE), log retention's 14-day trim
/// (§2, SYS-LOGGING), and <c>ProjectDocument.CreatedUtc</c>/<c>ModifiedUtc</c>
/// (<c>06_PROJECT_DOCUMENT_MODEL.md</c>). With <c>DateTimeOffset.UtcNow</c> called inline, testing
/// "does the throttle actually suppress a second probe 23 hours later" means sleeping, or not
/// testing it. There is currently no test for it.
/// </para>
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>The real clock. The only implementation that ships.</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
