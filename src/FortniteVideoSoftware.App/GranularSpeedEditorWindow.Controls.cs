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
/// MVVM_03 — the cached control accessors for <see cref="GranularSpeedEditorWindow"/>.
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
public partial class GranularSpeedEditorWindow
{

    private Avalonia.Controls.Canvas? _cZoomOverlayCanvas;
    private Avalonia.Controls.Canvas? ZoomOverlayCanvasCtl => _cZoomOverlayCanvas ??= this.FindControl<Avalonia.Controls.Canvas>("ZoomOverlayCanvas");
    private FortniteVideoSoftware.App.Controls.SpinningWheelSlider? _cPendingSpeedSlider;
    private FortniteVideoSoftware.App.Controls.SpinningWheelSlider? PendingSpeedSliderCtl => _cPendingSpeedSlider ??= this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("PendingSpeedSlider");
    private TextBlock? _cPendingSpeedLabel;
    private TextBlock? PendingSpeedLabelCtl => _cPendingSpeedLabel ??= this.FindControl<TextBlock>("PendingSpeedLabel");
    private Button? _cClearAllSegmentsBtn;
    private Button? ClearAllSegmentsBtnCtl => _cClearAllSegmentsBtn ??= this.FindControl<Button>("ClearAllSegmentsBtn");
    private FortniteVideoSoftware.App.Controls.TimelineLanesControl? _cGranularLanes;
    private FortniteVideoSoftware.App.Controls.TimelineLanesControl? GranularLanesCtl => _cGranularLanes ??= this.FindControl<FortniteVideoSoftware.App.Controls.TimelineLanesControl>("GranularLanes");
    private TextBlock? _cFreezeHintLabel;
    private TextBlock? FreezeHintLabelCtl => _cFreezeHintLabel ??= this.FindControl<TextBlock>("FreezeHintLabel");
    private TextBlock? _cFreezeHintLabelBottom;
    private TextBlock? FreezeHintLabelBottomCtl => _cFreezeHintLabelBottom ??= this.FindControl<TextBlock>("FreezeHintLabelBottom");
    private Button? _cZoomSegmentBtn;
    private Button? ZoomSegmentBtnCtl => _cZoomSegmentBtn ??= this.FindControl<Button>("ZoomSegmentBtn");
    private RadioButton? _cSlowZoomCheck;
    private RadioButton? SlowZoomCheckCtl => _cSlowZoomCheck ??= this.FindControl<RadioButton>("SlowZoomCheck");
    private Border? _cZoomStylePanel;
    private Border? ZoomStylePanelCtl => _cZoomStylePanel ??= this.FindControl<Border>("ZoomStylePanel");
    private Button? _cFreezeImageToggle;
    private Button? FreezeImageToggleCtl => _cFreezeImageToggle ??= this.FindControl<Button>("FreezeImageToggle");
    private TextBlock? _cFreezeImageToggleIcon;
    private TextBlock? FreezeImageToggleIconCtl => _cFreezeImageToggleIcon ??= this.FindControl<TextBlock>("FreezeImageToggleIcon");
    private TextBlock? _cFreezeImageToggleText;
    private TextBlock? FreezeImageToggleTextCtl => _cFreezeImageToggleText ??= this.FindControl<TextBlock>("FreezeImageToggleText");
    private Button? _cDeleteSegmentBtn;
    private Button? DeleteSegmentBtnCtl => _cDeleteSegmentBtn ??= this.FindControl<Button>("DeleteSegmentBtn");
    private Button? _cCancelGranularBtn;
    private Button? CancelGranularBtnCtl => _cCancelGranularBtn ??= this.FindControl<Button>("CancelGranularBtn");
    private RadioButton? _cInstantZoomCheck;
    private RadioButton? InstantZoomCheckCtl => _cInstantZoomCheck ??= this.FindControl<RadioButton>("InstantZoomCheck");
    private Button? _cUndoBtn;
    private Button? UndoBtnCtl => _cUndoBtn ??= this.FindControl<Button>("UndoBtn");
}
