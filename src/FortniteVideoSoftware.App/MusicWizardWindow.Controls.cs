// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Layout;
using FortniteVideoSoftware.App.Controls;

namespace FortniteVideoSoftware.App;

/// <summary>
/// MVVM_03 — the cached control accessors for <see cref="MusicWizardWindow"/>.
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
public partial class MusicWizardWindow
{

    private ListBox? _cMusicListBox;
    private ListBox? MusicListBoxCtl => _cMusicListBox ??= this.FindControl<ListBox>("MusicListBox");
    private Slider? _cVideoVolSlider;
    private Slider? VideoVolSliderCtl => _cVideoVolSlider ??= this.FindControl<Slider>("VideoVolSlider");
    private Slider? _cMusicVolSlider;
    private Slider? MusicVolSliderCtl => _cMusicVolSlider ??= this.FindControl<Slider>("MusicVolSlider");
    private CheckBox? _cLoopMusicCheckBox;
    private CheckBox? LoopMusicCheckBoxCtl => _cLoopMusicCheckBox ??= this.FindControl<CheckBox>("LoopMusicCheckBox");
    private TextBlock? _cOffsetLabel;
    private TextBlock? OffsetLabelCtl => _cOffsetLabel ??= this.FindControl<TextBlock>("OffsetLabel");
    private Button? _cAutoFillSongsBtn;
    private Button? AutoFillSongsBtnCtl => _cAutoFillSongsBtn ??= this.FindControl<Button>("AutoFillSongsBtn");
    private CheckBox? _cDuckingCheckBox;
    private CheckBox? DuckingCheckBoxCtl => _cDuckingCheckBox ??= this.FindControl<CheckBox>("DuckingCheckBox");
    private CheckBox? _cCarvingCheckBox;
    private CheckBox? CarvingCheckBoxCtl => _cCarvingCheckBox ??= this.FindControl<CheckBox>("CarvingCheckBox");
    private FortniteVideoSoftware.App.Controls.TimelineLanesControl? _cPhase3Lanes;
    private FortniteVideoSoftware.App.Controls.TimelineLanesControl? Phase3LanesCtl => _cPhase3Lanes ??= this.FindControl<FortniteVideoSoftware.App.Controls.TimelineLanesControl>("Phase3Lanes");
    private Avalonia.Controls.Border? _cVideoHostBorder;
    private Avalonia.Controls.Border? VideoHostBorderCtl => _cVideoHostBorder ??= this.FindControl<Avalonia.Controls.Border>("VideoHostBorder");
    private ListBox? _cAutoFillQueueList;
    private ListBox? AutoFillQueueListCtl => _cAutoFillQueueList ??= this.FindControl<ListBox>("AutoFillQueueList");
    private Button? _cPlayBtn;
    private Button? PlayBtnCtl => _cPlayBtn ??= this.FindControl<Button>("PlayBtn");
    private Button? _cNextBtn;
    private Button? NextBtnCtl => _cNextBtn ??= this.FindControl<Button>("NextBtn");
}
