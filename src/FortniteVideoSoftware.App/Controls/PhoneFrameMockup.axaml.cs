using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace FortniteVideoSoftware.App.Controls;

public partial class PhoneFrameMockup : UserControl
{
    public PhoneFrameMockup()
    {
        InitializeComponent();
    }

    public void SetPortraitImage(Bitmap? bitmap)
    {
        var img = this.FindControl<Image>("PortraitImage");
        if (img != null)
            img.Source = bitmap;
    }

    public Image? PortraitImageControl => this.FindControl<Image>("PortraitImage");
}