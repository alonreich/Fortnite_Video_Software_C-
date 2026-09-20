// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using Avalonia.Controls;
using FortniteVideoSoftware.App.Controls;   // NoticeKind

namespace FortniteVideoSoftware.App.Abstractions;

/// <summary>
/// INJSEAM_03 — "say something to the user", behind an interface.
///
/// <para>
/// <c>FloatingNotice</c> and <c>NativeDialog</c> are static classes that require a live Avalonia
/// <see cref="Window"/>. A view-model that calls them directly cannot be constructed in a unit
/// test, which is the mechanical reason the App layer currently has none: all 4,167 lines of test
/// target <c>Core</c>, and the ~25,000 lines of window code have zero coverage.
/// </para>
///
/// <para>
/// <b>THREADING.</b> Every member is safe to call from any thread and marshals internally.
/// <c>FloatingNotice.Show</c> already guarantees this; the dialog members post to the UI thread.
/// </para>
/// </summary>
public interface IUserNotifier
{
    /// <summary>A transient pill. Used for <c>FaultTier.Degraded</c> and for ordinary success/info.</summary>
    void Notify(string text, NoticeKind kind = NoticeKind.Info);

    /// <summary>
    /// A blocking, acknowledge-only dialog. Used for <c>FaultTier.Fatal</c>.
    /// Implementations append the log path so the user always has somewhere to look next.
    /// </summary>
    void Alert(string title, string message);

    /// <summary>A yes/no question. Returns the user's answer.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText);
}

/// <summary>
/// INJSEAM_03 — the window the notifier currently targets.
///
/// <para>
/// A suite of six top-level windows, three of which can be the active one, means "which window
/// does this notice belong on" is a real question. The answer is: the window that owns the
/// operation that failed, and the composition root tracks it. A notice raised while no window is
/// open (startup, a background worker after teardown) falls back to the log — it must never
/// throw, because a fault reporter that can itself fail is a fault reporter that gets wrapped in
/// a <c>try { } catch { }</c>, and then we are back where we started.
/// </para>
/// </summary>
public interface IActiveWindowProvider
{
    Window? ActiveWindow { get; }
}
