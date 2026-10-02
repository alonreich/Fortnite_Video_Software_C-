
using System;
using System.Collections.Generic;
using Avalonia.Threading;
using FreeVideoStudio.App.Abstractions;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.Core.Abstractions;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// FAULTTIER_01 — the single routing table from a caught exception to what the user sees.
///
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// THE RULE THIS ENFORCES: <b>silence is not one of the options.</b> A fault arrives here, is
/// classified once, and leaves by exactly one of three doors — debug log, floating pill, or modal
/// dialog. There is no fourth door and no early return. The previous arrangement had 917 catch
/// blocks each making this decision privately, and the decision they overwhelmingly made was
/// "log it and say nothing", which is how a broken thumbnail, a dead ffprobe and a failed
/// settings write all became indistinguishable from a user mis-click.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </para>
///
/// <para>
/// <b>FAULTSTORM_01 — a fault reporter needs its own flood control.</b> Degraded faults are raised
/// from worker threads, and a failing per-frame operation raises the same one at frame rate.
/// <c>FloatingNotice</c> already dedupes identical TEXT inside 1.4s (F4), but a fault whose message
/// embeds a changing value defeats that. The gate here is keyed on <c>(Tier, Area, UserMessage)</c>
/// and is deliberately WIDER than the notice's own: <see cref="DegradedRepeatWindowSeconds"/>. A
/// suppressed repeat still reaches the log, so nothing is lost for diagnosis — only the user is
/// spared the pile.
/// </para>
///
/// <para>
/// <b>This class must never throw.</b> Anything that can fail inside it is wrapped, because a
/// reporter that can fault is a reporter that call sites will wrap in <c>try { } catch { }</c>,
/// and that is precisely the shape being retired.
/// </para>
/// </summary>
public sealed class UserFacingFaultSink : IFaultSink
{
    /// <summary>FAULTSTORM_01 — how long an identical Degraded fault stays suppressed on screen.</summary>
    private const int DegradedRepeatWindowSeconds = 8;

    /// <summary>
    /// FAULTSTORM_01 — a Fatal dialog is MODAL, so a repeat is not merely noisy, it is a trap the
    /// user cannot click out of. Identical fatals are collapsed for far longer than degraded ones.
    /// </summary>
    private const int FatalRepeatWindowSeconds = 60;

    private readonly IUserNotifier _notifier;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _lastShown = new(StringComparer.Ordinal);

    public UserFacingFaultSink(IUserNotifier notifier)
        => _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));

    public void Report(Fault fault)
    {
        try
        {
            ReportCore(fault);
        }
        catch (Exception ex)
        {
            RuntimeLog.EmergencyWrite("FAULT SINK", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ReportCore(Fault fault)
    {
        string detail = string.IsNullOrWhiteSpace(fault.TechnicalDetail)
            ? fault.Exception?.Message ?? "(no detail)"
            : fault.TechnicalDetail!;

        switch (fault.Tier)
        {
            case FaultTier.Recoverable:
                FaultCounters.Record(fault);
                if (RuntimeLog.TryPassThrottle("REC|" + fault.Area + "|" + CallSiteKey(detail), out int held))
                {
                    RuntimeLog.Info(fault.Area, held > 0
                        ? $"[RECOVERABLE] {detail} (+{held} from this site in the last 30s)"
                        : $"[RECOVERABLE] {detail}");
                }
                if (fault.Exception is not null)
                    RuntimeLog.Debug(fault.Area, fault.Exception.ToString());
                return;

            case FaultTier.Degraded:
                FaultCounters.Record(fault);
                RuntimeLog.Fail(fault.Area, $"[DEGRADED] {fault.UserMessage} :: {detail}");
                if (fault.Exception is not null)
                    RuntimeLog.Debug(fault.Area, fault.Exception.ToString());

                if (ShouldSurface(fault, DegradedRepeatWindowSeconds))
                    _notifier.Notify(fault.UserMessage, NoticeKind.Warning);
                return;

            case FaultTier.Fatal:
                FaultCounters.Record(fault);
                RuntimeLog.Fail(fault.Area, $"[FATAL] {fault.UserMessage} :: {detail}");
                if (fault.Exception is not null)
                    RuntimeLog.Fail(fault.Area, fault.Exception);

                if (ShouldSurface(fault, FatalRepeatWindowSeconds))
                    _notifier.Alert(TitleFor(fault.Area), fault.UserMessage);
                return;

            default:
                RuntimeLog.Fail(fault.Area, $"[UNCLASSIFIED tier={fault.Tier}] {fault.UserMessage} :: {detail}");
                _notifier.Notify(fault.UserMessage, NoticeKind.Warning);
                return;
        }
    }

    /// <summary>FAULTSTORM_01 — true when this exact fault has not been shown inside the window.</summary>
    /// <summary>
    /// LOGVIS_01 — Swallowed() details start "File.cs:123 Member() — …". That prefix identifies the
    /// call site. Other details are keyed by their first 48 characters.
    /// </summary>
    private static string CallSiteKey(string detail)
    {
        int dash = detail.IndexOf(" — ", StringComparison.Ordinal);
        if (dash > 0) return detail[..dash];
        return detail.Length > 48 ? detail[..48] : detail;
    }

    private bool ShouldSurface(Fault fault, int windowSeconds)
    {
        if (string.IsNullOrWhiteSpace(fault.UserMessage)) return false;

        string key = $"{(int)fault.Tier}\u0000{fault.Area}\u0000{fault.UserMessage}";
        DateTime now = DateTime.UtcNow;

        lock (_gate)
        {
            if (_lastShown.TryGetValue(key, out DateTime previous)
                && (now - previous).TotalSeconds < windowSeconds)
            {
                return false;
            }

            _lastShown[key] = now;

            if (_lastShown.Count > 256)
            {
                DateTime cutoff = now.AddSeconds(-Math.Max(DegradedRepeatWindowSeconds, FatalRepeatWindowSeconds));
                List<string> stale = new();
                foreach (KeyValuePair<string, DateTime> entry in _lastShown)
                    if (entry.Value < cutoff) stale.Add(entry.Key);
                foreach (string s in stale) _lastShown.Remove(s);

                if (_lastShown.Count > 256) _lastShown.Clear();
            }

            return true;
        }
    }

    /// <summary>Turns the log's uppercase subsystem tag into a dialog caption a person can read.</summary>
    private static string TitleFor(string area) => area switch
    {
        "EXPORT"  => "The export could not continue",
        "PROJECT" => "Your project could not be saved",
        "UPDATE"  => "The update could not be installed",
        "AUDIO"   => "The audio step could not continue",
        _         => "Something went wrong",
    };
}

/// <summary>
/// INJSEAM_03 — the shipping <see cref="IUserNotifier"/>: a thin adapter over the existing
/// <c>FloatingNotice</c> and <c>NativeDialog</c>, resolving the target window at call time through
/// <see cref="IActiveWindowProvider"/>.
/// </summary>
public sealed class AvaloniaUserNotifier : IUserNotifier
{
    private readonly IActiveWindowProvider _windows;

    public AvaloniaUserNotifier(IActiveWindowProvider windows)
        => _windows = windows ?? throw new ArgumentNullException(nameof(windows));

    public void Notify(string text, NoticeKind kind = NoticeKind.Info)
    {
        var window = _windows.ActiveWindow;
        if (window is null)
        {
            RuntimeLog.Info("NOTICE", $"(no window) {text}");
            return;
        }

        FloatingNotice.Show(window, text, kind);
    }

    public void Alert(string title, string message)
    {
        string body = message + Environment.NewLine + Environment.NewLine + $"Log: {RuntimeLog.LogPath}";

        if (Dispatcher.UIThread.CheckAccess())
        {
            NativeDialog.ShowError(body, title);
            return;
        }

        Dispatcher.UIThread.Post(() => NativeDialog.ShowError(body, title));
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText)
    {
        var window = _windows.ActiveWindow;
        if (window is null)
        {
            return await Dispatcher.UIThread.InvokeAsync(() => NativeDialog.ShowQuestion(message, title));
        }

        return await ConfirmDialogWindow.AskAsync(window, message, title, confirmText, cancelText);
    }
}
