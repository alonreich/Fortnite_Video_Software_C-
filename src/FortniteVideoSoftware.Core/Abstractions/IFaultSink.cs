// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Abstractions;

/// <summary>
/// FAULTTIER_01 — the one place a caught exception is allowed to go.
///
/// <para>
/// Implemented in the App layer by <c>UserFacingFaultSink</c>, which owns the actual routing to
/// <c>RuntimeLog</c>, <c>FloatingNotice</c> and <c>NativeDialog</c>. Core and the view-models
/// depend on THIS, never on those, which is what lets a view-model be tested without an Avalonia
/// dispatcher and lets a test assert that a given failure was surfaced rather than swallowed.
/// </para>
///
/// <para>
/// <b>THREADING.</b> Implementations MUST be callable from any thread. A worker thread reporting a
/// degraded fault must not have to know whether it is on the UI thread — the sink marshals. This is
/// deliberate: the old <c>catch { RuntimeLog.Fail(...) }</c> shape was thread-agnostic, and any
/// replacement that is not would simply be skipped by the call sites it was meant to convert.
/// </para>
/// </summary>
public interface IFaultSink
{
    /// <summary>Classify, log, and — for Degraded and Fatal — surface this failure to the user.</summary>
    void Report(Fault fault);
}

/// <summary>
/// FAULTTIER_01 — the call-site sugar that makes the right thing shorter to type than the wrong
/// thing.
///
/// <para>
/// A conversion that reads <c>_faults.Report(new Fault(FaultTier.Degraded, "THUMBS", "...", ex.Message, ex))</c>
/// loses to <c>catch { }</c> every time, and the audit above is what losing looks like at scale.
/// These extensions exist so the migration is a mechanical, obviously-correct edit.
/// </para>
/// </summary>
public static class FaultSinkExtensions
{
    /// <summary>
    /// The user's outcome is unchanged; this is a DEBUG breadcrumb and nothing more.
    /// <b>No user message is taken, on purpose</b> — if you find yourself wanting to write one,
    /// the fault is <see cref="Degraded"/>.
    /// </summary>
    public static void Recoverable(this IFaultSink sink, string area, string technicalDetail, Exception? ex = null)
        => sink.Report(new Fault(FaultTier.Recoverable, area, string.Empty, technicalDetail, ex));

    /// <summary>
    /// Something visible stopped working; the session survives.
    /// <paramref name="userMessage"/> must name what stopped AND what still works.
    /// </summary>
    public static void Degraded(this IFaultSink sink, string area, string userMessage, Exception? ex = null, string? technicalDetail = null)
        => sink.Report(new Fault(FaultTier.Degraded, area, userMessage, technicalDetail ?? ex?.Message, ex));

    /// <summary>The thing the user asked for cannot happen. They get a dialog.</summary>
    public static void Fatal(this IFaultSink sink, string area, string userMessage, Exception? ex = null, string? technicalDetail = null)
        => sink.Report(new Fault(FaultTier.Fatal, area, userMessage, technicalDetail ?? ex?.Message, ex));

    /// <summary>
    /// Runs <paramref name="action"/> and converts any exception into a classified fault.
    /// The literal replacement for <c>try { action(); } catch { }</c>.
    /// </summary>
    /// <returns><c>true</c> when the action completed without throwing.</returns>
    public static bool Guard(this IFaultSink sink, string area, string userMessage, Action action, FaultTier tier = FaultTier.Degraded)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            sink.Report(new Fault(tier, area, userMessage, ex.Message, ex));
            return false;
        }
    }

    /// <summary>
    /// Async <see cref="Guard(IFaultSink, string, string, Action, FaultTier)"/>.
    ///
    /// <para>⚠️ <see cref="OperationCanceledException"/> is RE-THROWN, never reported. A cancel is
    /// the user getting what they asked for; reporting it as a fault is how a Cancel button ends up
    /// showing an error pill. Callers that must not propagate it catch it themselves — the suite
    /// already does this correctly in <c>UpdateService</c> and <c>ProcessWorker</c>.</para>
    /// </summary>
    public static async Task<bool> GuardAsync(this IFaultSink sink, string area, string userMessage, Func<Task> action, FaultTier tier = FaultTier.Degraded)
    {
        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sink.Report(new Fault(tier, area, userMessage, ex.Message, ex));
            return false;
        }
    }

    /// <summary>
    /// Value-returning <see cref="Guard(IFaultSink, string, string, Action, FaultTier)"/>; yields
    /// <paramref name="fallback"/> when the function throws. Replaces the
    /// <c>try { return Compute(); } catch { return default; }</c> shape, which is the single most
    /// common swallow in the codebase and the one that most reliably produces a blank readout.
    /// </summary>
    public static T GuardValue<T>(this IFaultSink sink, string area, string userMessage, Func<T> func, T fallback, FaultTier tier = FaultTier.Degraded)
    {
        try
        {
            return func();
        }
        catch (Exception ex)
        {
            sink.Report(new Fault(tier, area, userMessage, ex.Message, ex));
            return fallback;
        }
    }
}

/// <summary>
/// A sink that only logs, for unit tests and for the pre-UI startup window in <c>Program.cs</c>
/// before an Avalonia window exists to host a notice. <b>Not a production fallback</b>: shipping
/// code that resolves to this is back to swallowing, which is the defect this whole file closes.
/// </summary>
public sealed class NullFaultSink : IFaultSink
{
    public static readonly NullFaultSink Instance = new();

    private readonly List<Fault> _captured = new();

    /// <summary>Everything reported to this sink, in order. For test assertions.</summary>
    public IReadOnlyList<Fault> Captured
    {
        get { lock (_captured) { return _captured.ToArray(); } }
    }

    public void Report(Fault fault)
    {
        lock (_captured) { _captured.Add(fault); }
        CoreLogger.Fail(fault.Area, $"[{fault.Tier}] {fault.UserMessage} :: {fault.TechnicalDetail}");
    }
}
