// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Layout;
using FortniteVideoSoftware.App.Controls;

namespace FortniteVideoSoftware.App;

/// <summary>
/// MVVM_03 — the cached control accessors for <see cref="VideoMergerWindow"/>.
///
/// <para>
/// ⚠️ IN ITS OWN FILE ON PURPOSE. MVVM_02 caps window code-behind and the cap may only ever
/// fall, so adding these to the code-behind — even while REMOVING lookups from it — would
/// have pushed several files past their ceiling and forced the ratchet up. A ratchet that
/// gets raised to accommodate an improvement stops being a ratchet.
/// </para>
///
/// <para>
/// These replace repeated <c>this.FindControl&lt;T&gt;("Name")</c> calls — some controls were
/// resolved by string fifteen times in one file. Each lookup walks the visual tree and can
/// return null, so a renamed control compiled cleanly and produced a dead button at run time
/// (QUALITY_04). Now a rename breaks in exactly one place.
/// </para>
///
/// <para>
/// This is a STEP toward MVVM_01, not the destination. The end state is a binding to a
/// view-model property; until then, one resolution per control means the eventual binding
/// replaces one accessor instead of hunting every call site.
/// </para>
/// </summary>
public partial class VideoMergerWindow
{

    private ListBox? _cVideoList;
    private ListBox? VideoListCtl => _cVideoList ??= this.FindControl<ListBox>("VideoList");
    private FortniteVideoSoftware.App.Controls.PhaseOverlayControl? _cOverlayLayer;
    private FortniteVideoSoftware.App.Controls.PhaseOverlayControl? OverlayLayerCtl => _cOverlayLayer ??= this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer");
    private MenuItem? _cMenuOutputFolder;
    private MenuItem? MenuOutputFolderCtl => _cMenuOutputFolder ??= this.FindControl<MenuItem>("MenuOutputFolder");
    private Button? _cAddMusicButton;
    private Button? AddMusicButtonCtl => _cAddMusicButton ??= this.FindControl<Button>("AddMusicButton");
    private Slider? _cVolumeSlider;
    private Slider? VolumeSliderCtl => _cVolumeSlider ??= this.FindControl<Slider>("VolumeSlider");
}
