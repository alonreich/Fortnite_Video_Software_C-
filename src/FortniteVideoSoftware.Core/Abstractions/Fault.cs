// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.

namespace FortniteVideoSoftware.Core.Abstractions;

/// <summary>
/// FAULTTIER_01 — HOW LOUD A FAILURE IS ALLOWED TO BE.
///
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// WHY THIS EXISTS. An audit of <c>src/</c> counted <b>917 catch blocks in ~90,000 lines</b> — one
/// per 98 lines — of which 368 were bare <c>catch (Exception)</c> and 391 sites carried a comment
/// saying "best-effort" or "ignore". The near-universal shape was:
/// <code>
///     try { ... } catch (Exception ex) { RuntimeLog.Fail("AREA", ex); }
/// </code>
/// That is not error handling. It is error <i>absorption</i>: a log line the user will never open,
/// followed by the program continuing as though nothing happened. A failed thumbnail, a failed
/// ffprobe and a failed settings write all present identically to the person using the app —
/// as <b>nothing happening</b>. The user is left to guess whether they mis-clicked.
///
/// Silence is the one response that must not be available. Every catch block now answers one
/// question — <i>what does the person in front of this window need to know?</i> — and the three
/// possible answers are the three members of this enum.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </para>
/// </summary>
public enum FaultTier
{
    /// <summary>
    /// The operation failed and the program genuinely recovered on its own — a retry succeeded, a
    /// cache miss fell through to the source, an optional probe returned nothing and a documented
    /// default took over. <b>Nothing reaches the screen.</b> Goes to the DEBUG log only.
    ///
    /// <para>⚠️ The bar is "the user's outcome is unchanged", not "we kept running". If the feature
    /// they asked for did not happen, it is <see cref="Degraded"/>, however gracefully the code
    /// coped.</para>
    /// </summary>
    Recoverable,

    /// <summary>
    /// Something the user can perceive stopped working, but the session is intact and the rest of
    /// the app still functions. Surfaces as a <c>FloatingNotice</c> that says <b>what stopped</b>
    /// and <b>what still works</b>, and is written to the log at failure level.
    ///
    /// <para>This is the tier that almost every current silent catch belongs in. "Thumbnails could
    /// not be generated for this clip — editing and export still work" is a complete notice; it
    /// tells the user the blank strip is known, is not their fault, and is not going to cost them
    /// the export.</para>
    /// </summary>
    Degraded,

    /// <summary>
    /// The operation the user explicitly asked for cannot proceed, or continuing risks their work.
    /// Surfaces as a modal dialog naming the failure, the log path and a copy button, and is
    /// written to the log at failure level.
    ///
    /// <para>Export refused to start, the project could not be saved, the update installer failed
    /// its signature check. A dialog is justified precisely because the user is about to act on a
    /// belief that is now false.</para>
    /// </summary>
    Fatal
}

/// <summary>
/// FAULTTIER_01 — one failure, classified, with the words the user will actually read.
/// </summary>
/// <param name="Tier">How loud this is allowed to be. See <see cref="FaultTier"/>.</param>
/// <param name="Area">
/// The short uppercase subsystem tag already used by <c>RuntimeLog</c> ("EXPORT", "IPC", "THUMBS").
/// Kept identical so a fault and its surrounding log lines can be grepped together.
/// </param>
/// <param name="UserMessage">
/// Plain language, written for the person in front of the window, obeying the colloquial tooltip
/// standard of <c>04_UI_UX_AVALONIA_SPEC.md</c> §4 (UI-SAFEGUARDS). For <see cref="FaultTier.Degraded"/>
/// it must name what stopped AND what still works. Never an exception message verbatim — an
/// <c>ObjectDisposedException</c> string is not an explanation. Ignored for
/// <see cref="FaultTier.Recoverable"/>, which never reaches a screen.
/// </param>
/// <param name="TechnicalDetail">
/// What goes in the log beside the stack trace: the file, the exit code, the filtergraph stage.
/// May contain full paths — the log pipeline's own privacy gate (<c>05_SYSTEM_LIFECYCLE_STORAGE.md</c>
/// §2, SYS-LOGGING) decides whether they survive to a production INFO line.
/// </param>
/// <param name="Exception">The original exception, when there was one. Null for a policy refusal.</param>
public sealed record Fault(
    FaultTier Tier,
    string Area,
    string UserMessage,
    string? TechnicalDetail = null,
    Exception? Exception = null);
