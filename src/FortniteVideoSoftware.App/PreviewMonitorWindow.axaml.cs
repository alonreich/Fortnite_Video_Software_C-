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
    
    public MainWindow? ParentMainWindow { get; set; }

    public PreviewMonitorWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
        FortniteVideoSoftware.App.WindowBoundsHelper.LoadBoundsSync(this, "PreviewMonitorWindowBounds");
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
                    try { BeginMoveDrag(e); } catch { }
                }
            };
        }
    }

    protected override void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        FortniteVideoSoftware.App.WindowBoundsHelper.SaveBoundsSync(this, "PreviewMonitorWindowBounds");
        
        if (ParentMainWindow != null && ParentMainWindow.IsPreviewDetached)
        {
            e.Cancel = true;
            _ = ParentMainWindow.AttachPreviewMonitor();
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