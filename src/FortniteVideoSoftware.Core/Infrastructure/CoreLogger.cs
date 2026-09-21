// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;

namespace FortniteVideoSoftware.Core.Infrastructure;

public static class CoreLogger
{
    public static Action<string, string>? InfoAction;
    public static Action<string, string>? FailAction;
    public static Action<string, string>? DebugAction;

    public static Action<string>? AppendAction;

    public static void Info(string step, string detail)
    {
        InfoAction?.Invoke(step, detail);
    }

    public static void Debug(string step, string detail)
    {
        DebugAction?.Invoke(step, detail);
    }

    public static void Fail(string step, string detail)
    {
        FailAction?.Invoke(step, detail);
    }

    public static void Append(string line)
    {
        AppendAction?.Invoke(line);
    }

    /// <summary>
    /// FAULTTIER_02 — A CAUGHT EXCEPTION THE CALLER BELIEVES CHANGED NOTHING.
    ///
    /// <para>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// <b>THIS NOW GOES THROUGH THE FAULT SINK, AND THAT IS THE WHOLE POINT OF THE CHANGE.</b>
    /// It used to write two log lines and stop. There are 337 call sites, which made it far and
    /// away the most common way a failure was handled in this codebase — and every one of them was
    /// invisible to <c>IFaultSink</c>, so the fault-tier system that FAULTTIER_01 introduced was
    /// reporting on 3 failures out of roughly 900.
    /// </para>
    ///
    /// <para>
    /// Routing it here converts all 337 in one edit instead of 337. Nothing about what the USER
    /// sees changes — <see cref="FaultTier.Recoverable"/> is log-only by definition — but the
    /// failures now exist as far as the system is concerned: they are classified, they are counted,
    /// they reach the diagnostic bundle, and reclassifying any one of them to
    /// <see cref="FaultTier.Degraded"/> is now a one-line edit at that call site rather than a
    /// plumbing exercise.
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>"SWALLOWED" MEANS RECOVERABLE, AND THAT IS A CLAIM THE CALLER IS MAKING.</b> The bar
    /// from FAULTTIER_01 is "the user's outcome is unchanged", NOT "we kept running". A site where
    /// the feature the user asked for did not happen is mis-using this method and belongs at
    /// Degraded. The 337 inherited call sites are grandfathered at Recoverable because that is what
    /// they were already doing; they are not thereby blessed.
    /// </para>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    public static void Swallowed(
        Exception ex,
        [System.Runtime.CompilerServices.CallerMemberName] string member = "",
        [System.Runtime.CompilerServices.CallerFilePath] string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        try
        {
            string where = $"{System.IO.Path.GetFileName(file)}:{line} {member}()";

            // ⚠️ Faults.Report has its own outermost guard and cannot throw, so this does not need
            // a second one — but the try/catch around the whole body stays, because BUILDING the
            // message touches ex.Message, and an exception whose Message property throws is rare,
            // real, and must not take out the thread that was already handling a failure.
            Abstractions.Faults.Recoverable("SWALLOWED", $"{where} — {ex.GetType().Name}: {ex.Message}", ex);
        }
        catch (Exception)
        {
            // Nothing left to escalate to.
        }
    }
}
