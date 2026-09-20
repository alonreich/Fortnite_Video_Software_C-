// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace FortniteVideoSoftware.App.Controls;

public partial class PhoneFrameMockup : UserControl
{
    public PhoneFrameMockup()
    {
        InitializeComponent();
    }

    public Image? PortraitImageControl => this.FindControl<Image>("PortraitImage");
}