using System.Collections.Generic;

namespace FreeVideoStudio.App;

public partial class MusicWizardWindow
{
    /// <summary>
    /// SCRAPER_02 — the kept window (SOURCE seconds) of every merger clip, index-aligned with the
    /// queue, taken from the merger's MergedTimeline. With the Thumbnail Scraper on, removed intros
    /// lie outside these windows, so the clip lanes and the music window in this wizard are the exact
    /// timeline the merger previews and exports. Null (older callers) means "whole clip".
    /// </summary>
    public IReadOnlyList<(double StartSec, double EndSec)>? MergerClipWindows { get; set; }

    /// <summary>Kept length of merger clip <paramref name="index"/>, or null to probe the file as before.</summary>
    private double? MergerWindowDuration(int index)
        => _isMergerMode && MergerClipWindows != null && index >= 0 && index < MergerClipWindows.Count
           && MergerClipWindows[index].EndSec > MergerClipWindows[index].StartSec
            ? MergerClipWindows[index].EndSec - MergerClipWindows[index].StartSec
            : null;

    /// <summary>Source second the lane for merger clip <paramref name="index"/> starts at (0 = whole clip).</summary>
    private double MergerWindowStart(int index)
        => _isMergerMode && MergerClipWindows != null && index >= 0 && index < MergerClipWindows.Count
            ? MergerClipWindows[index].StartSec
            : 0;
}
