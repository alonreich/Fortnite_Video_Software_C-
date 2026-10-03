// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Collections.Generic;

namespace FreeVideoStudio.App;

public partial class VideoMergerWindow
{
    private Infrastructure.CornerMemeOverlayPresenter? _cornerMemes;
    private object? _cornerMemePlan;

    /// <summary>
    /// MEMEMODE_01 — corner memes in the Merger preview, on the plan's GAMEPLAY clock
    /// (<see cref="Core.Media.MergerPreviewPlan.GameplaySecAt"/>). Hidden while a full-screen cutaway
    /// owns the player; never pauses, seeks or swaps it.
    /// </summary>
    private void UpdateMergerCornerMemes(bool cutawayActive)
    {
        var plan = _plan;
        if (plan == null || plan.CornerOverlays.Count == 0 || cutawayActive || _videoHost?.IpcClient == null || _edlUrl == null)
        {
            _cornerMemes?.Hide();
            return;
        }

        _cornerMemes ??= new Infrastructure.CornerMemeOverlayPresenter();
        _cornerMemes.Attach(_videoHost);
        if (!ReferenceEquals(_cornerMemePlan, plan))
        {
            _cornerMemePlan = plan;
            var spans = new List<Infrastructure.CornerMemeSpan>(plan.CornerOverlays.Count);
            foreach (var c in plan.CornerOverlays) spans.Add(new Infrastructure.CornerMemeSpan(c.Meme, c.GameStartSec, c.GameEndSec));
            _cornerMemes.SetSpans(spans);
        }
        _cornerMemes.Update(plan.GameplaySecAt(_videoHost.IpcClient.CurrentTime));
    }
}
