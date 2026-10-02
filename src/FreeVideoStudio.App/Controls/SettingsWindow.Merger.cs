using FreeVideoStudio.App.Infrastructure;

namespace FreeVideoStudio.App.Controls;

public partial class SettingsWindow
{
    /// <summary>
    /// SCRAPER_05 — "Thumbnail Scraper" (Defaults tab). Seeded from settings when the window is
    /// built and written back by SaveAndClose, exactly like every other checkbox here. The Video
    /// Merger's own bottom-left checkbox edits the same flag.
    /// </summary>
    public bool MergerThumbnailScraper { get; set; } = SettingsManager.Instance.MergerThumbnailScraper;
}
