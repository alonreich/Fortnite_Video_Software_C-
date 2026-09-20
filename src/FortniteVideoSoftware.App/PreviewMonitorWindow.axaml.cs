using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FortniteVideoSoftware.App.Controls;
using FortniteVideoSoftware.App.Infrastructure;
using System.ComponentModel;

namespace FortniteVideoSoftware.App;

public partial class PreviewMonitorWindow : Window
{
    public Border VideoContainerControl => this.FindControl<Border>("VideoContainer") ?? throw new System.NullReferenceException("VideoContainer not found in XAML");

    /// <summary>
    /// DETACH_01: the controller that owns this window. It decides where the preview goes back to
    /// and is the ONLY thing allowed to actually close this window while a preview is inside it.
    /// Replaces the old <c>ParentMainWindow</c> back-reference, which hard-wired this window to
    /// the Main App and was the reason no other screen could use it.
    /// </summary>
    public PreviewDetachController? Controller { get; set; }

    private readonly string _boundsKey;

    public PreviewMonitorWindow() : this(PreviewDetachController.MainWindowKey, "Preview Monitor") { }

    /// <param name="boundsKey">
    /// Per-owner persistence key. Each screen gets its own, so every one remembers the size,
    /// position, maximised state and therefore the DISPLAY its detached preview was last on,
    /// independently of the others.
    /// </param>
    public PreviewMonitorWindow(string boundsKey, string title)
    {
        _boundsKey = string.IsNullOrWhiteSpace(boundsKey) ? PreviewDetachController.MainWindowKey : boundsKey;
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
        Title = string.IsNullOrWhiteSpace(title) ? "Preview Monitor" : title;
        FortniteVideoSoftware.App.WindowBoundsHelper.Track(this, _boundsKey);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        AttachTitleBarDrag();
    }

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<Border>("TitleBarBorder");
        if (titleBar != null)
        {
            titleBar.DoubleTapped += (s, e) =>
            {
                this.WindowState = this.WindowState == WindowState.Maximized 
                    ? WindowState.Normal 
                    : WindowState.Maximized;
                e.Handled = true;
            };
            titleBar.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount < 2)
                {
                    try { BeginMoveDrag(e); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                }
            };
        }
    }

    protected override void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        FortniteVideoSoftware.App.WindowBoundsHelper.SaveBoundsSync(this, _boundsKey);

        if (Controller != null && Controller.IsDetached)
        {
            e.Cancel = true;
            Controller.Attach();
        }
    }

    public void TogglePortraitOverlay(bool isPortrait)
    {
        var overlay = this.FindControl<UserControl>("PortraitSimulatorOverlay");
        RuntimeLog.Info("UI", $"TogglePortraitOverlay called with isPortrait={isPortrait}. Found overlay? {overlay != null}");
        if (overlay != null)
        {
            overlay.IsVisible = isPortrait;
            overlay.ZIndex = 9999;
        }

        var videoContainer = this.FindControl<Border>("VideoContainer");
        if (videoContainer != null)
        {
            videoContainer.RenderTransform = null;
        }
    }

    public void SetSkiaTextPlaceholder(Avalonia.Media.Imaging.Bitmap? bitmap)
    {
        var phoneFrame = this.FindControl<PhoneFrameMockup>("PhoneFrame");
        var img = phoneFrame?.PortraitImageControl;
        if (img != null)
        {
            img.Source = bitmap;
        }
    }
}