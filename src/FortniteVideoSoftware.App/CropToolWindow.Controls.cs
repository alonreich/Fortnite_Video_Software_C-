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
/// MVVM_03 — the cached control accessors for <see cref="CropToolWindow"/>.
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
public partial class CropToolWindow
{

    private Border? _cRolePopup;
    private Border? RolePopupCtl => _cRolePopup ??= this.FindControl<Border>("RolePopup");
    private Grid? _cSnapshotPanel;
    private Grid? SnapshotPanelCtl => _cSnapshotPanel ??= this.FindControl<Grid>("SnapshotPanel");
    private Button? _cDeleteMenuButton;
    private Button? DeleteMenuButtonCtl => _cDeleteMenuButton ??= this.FindControl<Button>("DeleteMenuButton");
    private StackPanel? _cDeleteConfirmPanel;
    private StackPanel? DeleteConfirmPanelCtl => _cDeleteConfirmPanel ??= this.FindControl<StackPanel>("DeleteConfirmPanel");
    private TextBox? _cRolePopupNewName;
    private TextBox? RolePopupNewNameCtl => _cRolePopupNewName ??= this.FindControl<TextBox>("RolePopupNewName");
    private Button? _cResetMenuButton;
    private Button? ResetMenuButtonCtl => _cResetMenuButton ??= this.FindControl<Button>("ResetMenuButton");
    private StackPanel? _cResetConfirmPanel;
    private StackPanel? ResetConfirmPanelCtl => _cResetConfirmPanel ??= this.FindControl<StackPanel>("ResetConfirmPanel");
}
