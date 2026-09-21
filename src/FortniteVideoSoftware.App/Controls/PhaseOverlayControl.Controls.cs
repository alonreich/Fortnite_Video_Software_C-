// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Layout;
using FortniteVideoSoftware.App.Controls;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>
/// MVVM_03 — the cached control accessors for <see cref="PhaseOverlayControl"/>.
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
public partial class PhaseOverlayControl
{

    private TextBox? _cLiveLogTextBox;
    private TextBox? LiveLogTextBoxCtl => _cLiveLogTextBox ??= this.FindControl<TextBox>("LiveLogTextBox");
    private Canvas? _cFightCanvas;
    private Canvas? FightCanvasCtl => _cFightCanvas ??= this.FindControl<Canvas>("FightCanvas");
    private Canvas? _cFighterA;
    private Canvas? FighterACtl => _cFighterA ??= this.FindControl<Canvas>("FighterA");
    private Canvas? _cFighterB;
    private Canvas? FighterBCtl => _cFighterB ??= this.FindControl<Canvas>("FighterB");
    private ProgressBar? _cPhaseProgressBar;
    private ProgressBar? PhaseProgressBarCtl => _cPhaseProgressBar ??= this.FindControl<ProgressBar>("PhaseProgressBar");
    private Avalonia.Controls.ProgressBar? _cHypeMeterBar;
    private Avalonia.Controls.ProgressBar? HypeMeterBarCtl => _cHypeMeterBar ??= this.FindControl<Avalonia.Controls.ProgressBar>("HypeMeterBar");
    private Avalonia.Controls.Shapes.Ellipse? _cBulbGlass;
    private Avalonia.Controls.Shapes.Ellipse? BulbGlassCtl => _cBulbGlass ??= this.FindControl<Avalonia.Controls.Shapes.Ellipse>("BulbGlass");
    private Canvas? _cDoor;
    private Canvas? DoorCtl => _cDoor ??= this.FindControl<Canvas>("Door");
    private Canvas? _cLogTangle;
    private Canvas? LogTangleCtl => _cLogTangle ??= this.FindControl<Canvas>("LogTangle");
}
