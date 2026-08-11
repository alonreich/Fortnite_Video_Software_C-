using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using FortniteVideoSoftware.App.Infrastructure;

namespace FortniteVideoSoftware.App;

/// <summary>
/// DETACH_01 — ONE implementation of "pop the video preview out into its own window", shared by
/// every screen in the suite that shows a live preview.
///
/// The Main App had this behaviour and nothing else did. Rather than copy the reparenting dance
/// into the Granular editor, the Music Wizard, the Voice Over window and the Merger — four more
/// places to get the teardown order wrong — the whole mechanism lives here and each window
/// supplies only two things:
///
///   • a BOUNDS KEY, which is what gives every screen its OWN memory of size, position, maximised
///     state and therefore which physical display it was last opened on. They are deliberately
///     separate keys: detaching the Granular preview onto the second monitor must not drag the
///     Main App's preview there next time.
///   • a CONTENT ACCESSOR returning the control to move. Usually the <see cref="MpvVideoView"/>
///     itself, but the Granular editor hands over its whole Viewbox so the zoom overlay and the
///     phone frame travel WITH the picture and stay in register with it.
///
/// ⚠️ THE ORIGIN IS CAPTURED, NOT ASSUMED. The Main App's original version hard-coded
/// "put it back into MainVideoGrid at index 0" on reattach. That only worked because it was the
/// only caller. This records the actual parent — Panel (with its child index), Decorator or
/// ContentControl — and restores exactly that, so a preview nested behind other overlays comes
/// back in the same z-order it left from.
///
/// ⚠️ THE OWNER'S Closing REATTACHES FIRST. A detached preview is a live mpv render surface
/// parented to a different window. If the owner closed while it was still out there, the surface
/// would be torn down by whichever window happened to die first. Reattaching on the owner's
/// Closing puts it back in its home tree so the owner's normal disposal path runs unchanged.
/// </summary>
public sealed class PreviewDetachController
{
    // Bounds keys. Every one of these must also appear in StateTransferStore.BoundsKeys and in
    // MainWindow's preserveKeys list, or the geometry is wiped on the next update/reset.
    public const string MainWindowKey = "PreviewMonitorWindowBounds";
    public const string GranularKey = "GranularPreviewMonitorBounds";
    public const string MusicWizardKey = "MusicWizardPreviewMonitorBounds";
    public const string VoiceOverKey = "VoiceOverPreviewMonitorBounds";
    public const string MergerKey = "MergerPreviewMonitorBounds";

    private readonly Window _owner;
    private readonly string _boundsKey;
    private readonly string _title;
    private readonly Func<Control?> _content;

    private PreviewMonitorWindow? _monitor;

    // Captured origin — exactly one of these three is non-null while detached.
    private Panel? _originPanel;
    private int _originIndex = -1;
    private Decorator? _originDecorator;
    private ContentControl? _originContent;
    private Thickness _originMargin;

    private bool _transitioning;

    /// <summary>Raised after every completed transition, with the new detached state.</summary>
    public event Action<bool>? StateChanged;

    public bool IsDetached => _monitor != null;

    /// <summary>The floating window while detached, so an owner can mirror portrait overlays onto it.</summary>
    public PreviewMonitorWindow? Monitor => _monitor;

    public PreviewDetachController(Window owner, string boundsKey, string title, Func<Control?> contentAccessor)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _boundsKey = boundsKey;
        _title = title;
        _content = contentAccessor ?? throw new ArgumentNullException(nameof(contentAccessor));

        _owner.Closing += (_, _) =>
        {
            try { Attach(); }
            catch (Exception ex) { RuntimeLog.Fail("UI", $"Could not reattach the preview before closing: {ex.Message}"); }
        };
    }

    /// <summary>
    /// UXQA_01: is there actually something to pop out right now? A detached preview always counts,
    /// because the button's job in that state is to bring it back.
    /// </summary>
    public bool CanDetach => IsDetached || _content() != null;

    /// <summary>
    /// UXQA_01: raised when the user asked to detach and there was nothing to move. The button used
    /// to just swallow the click — no message, no disabled look — so the app appeared broken.
    /// </summary>
    public event Action<string>? DetachUnavailable;

    /// <summary>
    /// UXQA_01: puts the toggle button into the right state — correct caption, and greyed out with
    /// an explanatory tooltip when there is no preview to move. Call it after wiring, and again
    /// whenever the preview appears or disappears.
    /// </summary>
    public void SyncButton(Button? btn)
    {
        if (btn == null) return;
        btn.Content = IsDetached ? "◱ Attach Monitor" : "◳ Detach Monitor";
        bool can = CanDetach;
        btn.IsEnabled = can;
        ToolTip.SetTip(btn, can
            ? "Pop the preview out into its own floating window. It remembers its size, position and which screen you left it on."
            : "The video preview is still loading — there is nothing to pop out yet.");
    }

    public bool Toggle() => IsDetached ? Attach() : Detach();

    /// <summary>Moves the preview into its own window. Returns false when there is nothing to move.</summary>
    public bool Detach()
    {
        if (_monitor != null || _transitioning) return false;

        var content = _content();
        if (content == null)
        {
            // UXQA_01: never swallow the click in silence. Tell the caller so it can say something.
            const string why = "The video preview is still loading — there is nothing to pop out yet.";
            RuntimeLog.Info("UI", $"Detach requested with no preview available ({_boundsKey}).");
            DetachUnavailable?.Invoke(why);
            return false;
        }

        _transitioning = true;
        try
        {
            if (!CaptureOriginAndRemove(content)) return false;

            _originMargin = content.Margin;
            content.Margin = new Thickness(0);

            _monitor = new PreviewMonitorWindow(_boundsKey, _title) { Controller = this };
            _monitor.VideoContainerControl.Child = content;
            PlaceOnFirstDetach(_monitor);
            _monitor.Show(_owner);

            RuntimeLog.Info("UI", $"Detached the preview into its own window ({_boundsKey}).");
        }
        finally { _transitioning = false; }

        StateChanged?.Invoke(true);
        return true;
    }

    /// <summary>Moves the preview back where it came from and closes the floating window.</summary>
    public bool Attach()
    {
        if (_monitor == null || _transitioning) return false;

        _transitioning = true;
        try
        {
            // Clear the field BEFORE Close(): PreviewMonitorWindow.OnClosing cancels the close and
            // calls back into Attach() while IsDetached is true, which would otherwise recurse.
            var monitor = _monitor;
            _monitor = null;

            var content = monitor.VideoContainerControl.Child as Control;
            monitor.VideoContainerControl.Child = null;

            if (content != null)
            {
                content.Margin = _originMargin;
                RestoreToOrigin(content);
            }

            try { monitor.Close(); }
            catch (Exception ex) { RuntimeLog.Fail("UI", $"Could not close the detached preview window: {ex.Message}"); }

            RuntimeLog.Info("UI", $"Reattached the preview to its home window ({_boundsKey}).");
        }
        finally
        {
            _originPanel = null;
            _originIndex = -1;
            _originDecorator = null;
            _originContent = null;
            _transitioning = false;
        }

        StateChanged?.Invoke(false);
        return true;
    }

    /// <summary>
    /// First detach only. <see cref="WindowBoundsHelper"/> leaves StartupLocation on CenterScreen
    /// when it found nothing saved under this key — that is the signal that this screen has never
    /// had its preview popped out before. Centre it on the owner instead of on whatever the OS
    /// considers the primary display, so the window appears where the user is looking. From the
    /// second detach onwards the saved geometry wins and this does nothing, which is what makes
    /// "it reopens on the monitor I left it on" work per screen.
    /// </summary>
    private void PlaceOnFirstDetach(Window monitor)
    {
        if (monitor.WindowStartupLocation != WindowStartupLocation.CenterScreen) return;

        try
        {
            monitor.WindowStartupLocation = WindowStartupLocation.Manual;
            double w = double.IsNaN(monitor.Width) ? 1280 : monitor.Width;
            double h = double.IsNaN(monitor.Height) ? 750 : monitor.Height;

            int px = _owner.Position.X + (int)((_owner.Bounds.Width - w) / 2);
            int py = Math.Max(0, _owner.Position.Y + (int)((_owner.Bounds.Height - h) / 2) - 150);

            // Clamp to the screen the OWNER is on, not the primary one — on a multi-monitor desk
            // the owner is frequently not on the primary, and clamping to primary would yank the
            // new window onto a different display than the one it was summoned from.
            var screen = _owner.Screens?.ScreenFromWindow(_owner) ?? _owner.Screens?.Primary;
            if (screen != null)
            {
                var b = screen.Bounds;
                px = Math.Max(b.X, Math.Min(px, b.X + b.Width - (int)w));
                py = Math.Max(b.Y, Math.Min(py, b.Y + b.Height - (int)h));
            }

            monitor.Position = new PixelPoint(px, py);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UI", $"Could not place the detached preview window: {ex.Message}");
        }
    }

    private bool CaptureOriginAndRemove(Control content)
    {
        switch (content.Parent)
        {
            case Panel panel:
                _originPanel = panel;
                _originIndex = panel.Children.IndexOf(content);
                panel.Children.Remove(content);
                return true;

            case Decorator decorator:
                _originDecorator = decorator;
                decorator.Child = null;
                return true;

            case ContentControl cc:
                _originContent = cc;
                cc.Content = null;
                return true;

            default:
                RuntimeLog.Fail("UI", "The preview could not be detached: its container is not a type this can put it back into.");
                return false;
        }
    }

    private void RestoreToOrigin(Control content)
    {
        if (_originPanel != null)
        {
            // The tree may have changed while the preview was away, so the recorded index is a
            // preference, not a promise.
            int index = Math.Clamp(_originIndex < 0 ? 0 : _originIndex, 0, _originPanel.Children.Count);
            _originPanel.Children.Insert(index, content);
        }
        else if (_originDecorator != null)
        {
            _originDecorator.Child = content;
        }
        else if (_originContent != null)
        {
            _originContent.Content = content;
        }
    }
}
