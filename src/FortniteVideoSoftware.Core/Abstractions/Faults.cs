// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Threading;

namespace FortniteVideoSoftware.Core.Abstractions;

/// <summary>
/// FAULTTIER_02 — THE AMBIENT SINK. WHERE A CATCH BLOCK REPORTS WHEN IT CANNOT BE HANDED ONE.
///
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// <b>WHY THIS EXISTS, AND WHY IT IS NOT A BACKDOOR AROUND COMPOSITION_02.</b>
/// </para>
///
/// <para>
/// <c>FAULTTIER_01</c> gave the codebase the right vocabulary and the right destination, and then
/// the migration did not happen: an audit of <c>src/</c> finds ~900 catch blocks and <b>three</b>
/// that reach <see cref="IFaultSink"/>. That is not negligence. It is arithmetic. Reporting a
/// fault requires an <see cref="IFaultSink"/> instance; the code that catches exceptions is
/// overwhelmingly static helpers and window code-behind that Avalonia constructs, neither of which
/// has a constructor anyone can pass one to. So the conversion needed a plumbing change per call
/// site, and a 900-site plumbing change does not get done — which is exactly the outcome the
/// FAULTTIER_01 comment predicted when it said the right thing must be shorter to type than the
/// wrong thing.
/// </para>
///
/// <para>
/// ⚠️ <b>THE DISTINCTION THAT MAKES THIS LEGITIMATE.</b> <c>COMPOSITION_02</c> retires the service
/// locator for COLLABORATORS — things a class does its work with, which a test must substitute to
/// exercise that work. A fault sink is not a collaborator, it is a DIAGNOSTIC CHANNEL, in the same
/// category as <c>RuntimeLog</c> and <c>CoreLogger</c>, both of which are already static by
/// deliberate decision and are not on anyone's list to inject. A failure must be reportable from
/// anywhere, including from a static utility, a finaliser and a thread with no object graph in
/// scope. Making that channel reachable is what allows the silence to end.
/// </para>
///
/// <para>
/// <b>NEW CODE STILL TAKES <see cref="IFaultSink"/> IN ITS CONSTRUCTOR.</b> This is for the places
/// that cannot, and the architecture test counts it so the number can be watched. A view-model
/// reaching for this instead of declaring its dependency is a review failure, not a shortcut.
/// </para>
///
/// <para>
/// <b>THREADING.</b> <see cref="Sink"/> is read without a lock on purpose — a reference field's
/// reads and writes are atomic, it is written exactly once during startup, and a fault reporter
/// that can block is a fault reporter that deadlocks the thread it was trying to help.
/// </para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class Faults
{
    private static IFaultSink _sink = NullFaultSink.Instance;

    /// <summary>
    /// The installed sink. Before <see cref="Install"/> runs this is a <see cref="NullFaultSink"/>,
    /// which logs and surfaces nothing — correct for the pre-UI startup window, where there is no
    /// window to show a notice on.
    /// </summary>
    public static IFaultSink Sink => _sink;

    /// <summary>True once the real sink is in place. Startup code can tell the difference.</summary>
    public static bool IsInstalled { get; private set; }

    /// <summary>
    /// Installs the production sink. Called once, from the composition root, immediately after it
    /// builds one.
    ///
    /// <para>⚠️ Installing twice is a defect — it means two graphs exist and half the application
    /// is reporting to the wrong one — but it is NOT worth throwing over. A throw here would take
    /// down startup to complain about diagnostics. The second call is ignored and recorded.</para>
    /// </summary>
    public static void Install(IFaultSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        if (IsInstalled)
        {
            Infrastructure.CoreLogger.Fail("COMPOSITION",
                "Faults.Install called twice. The second sink is ignored; the first stands.");
            return;
        }

        Interlocked.Exchange(ref _sink, sink);
        IsInstalled = true;
    }

    /// <summary>Test teardown. Never called in production — the sink lives as long as the process.</summary>
    public static void ResetForTests()
    {
        Interlocked.Exchange(ref _sink, NullFaultSink.Instance);
        IsInstalled = false;
    }

    // ── The three tiers, as one-liners a catch block can actually use ───────────────────────

    /// <summary>
    /// The user's outcome is unchanged. DEBUG breadcrumb only, nothing on screen.
    /// <para>⚠️ The bar is "the outcome is unchanged", not "we kept running".</para>
    /// </summary>
    public static void Recoverable(string area, string technicalDetail, Exception? ex = null)
        => Report(new Fault(FaultTier.Recoverable, area, string.Empty, technicalDetail, ex));

    /// <summary>
    /// Something the user can perceive stopped; the session is intact.
    /// <paramref name="userMessage"/> must name what stopped AND what still works.
    /// </summary>
    public static void Degraded(string area, string userMessage, Exception? ex = null, string? technicalDetail = null)
        => Report(new Fault(FaultTier.Degraded, area, userMessage, technicalDetail ?? ex?.Message, ex));

    /// <summary>The thing the user asked for cannot happen. They get a dialog.</summary>
    public static void Fatal(string area, string userMessage, Exception? ex = null, string? technicalDetail = null)
        => Report(new Fault(FaultTier.Fatal, area, userMessage, technicalDetail ?? ex?.Message, ex));

    /// <summary>
    /// ⚠️ THIS METHOD MAY NOT THROW, EVER.
    ///
    /// <para>
    /// A reporter that can fault is a reporter every call site wraps in <c>try { } catch { }</c> —
    /// which is the exact shape being retired, reintroduced by the thing retiring it. The
    /// production sink has its own outermost guard for the same reason; this is the second one,
    /// because this path is reachable before any sink is installed and from threads that are
    /// already unwinding.
    /// </para>
    /// </summary>
    public static void Report(Fault fault)
    {
        try
        {
            _sink.Report(fault);
        }
        catch (Exception ex)
        {
            try
            {
                Infrastructure.CoreLogger.Fail("FAULT SINK",
                    $"Reporting a fault threw: {ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception)
            {
                // Nothing left to escalate to. Returning is the only correct action.
            }
        }
    }
}
