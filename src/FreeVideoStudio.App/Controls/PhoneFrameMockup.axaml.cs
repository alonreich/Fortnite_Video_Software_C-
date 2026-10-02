
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace FreeVideoStudio.App.Controls;

public partial class PhoneFrameMockup : UserControl
{
    public PhoneFrameMockup()
    {
        InitializeComponent();
    }

    public Image? PortraitImageControl => this.FindControl<Image>("PortraitImage");
}