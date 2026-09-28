// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// SCRAPER_01..05 — THE THUMBNAIL SCRAPER, AND THE MERGED TIMELINE IT PRODUCES.
///
/// THREADING. Nothing here blocks the UI thread:
///   • Every queue change calls <see cref="ScheduleTimelineRebuild"/>. It asks
///     <see cref="MergeClipAnalyzer"/> for every clip. Each clip is probed once, on the thread
///     pool, a few in parallel, and cached per file identity, so a rebuild after a reorder or a
///     toggle costs no process at all.
///   • The rebuild awaits those tasks. The continuation runs back on the UI thread only to build
///     the (pure, microsecond) <see cref="MergedTimeline"/> and apply it.
///   • A version counter drops stale rebuilds: if the queue changed while a probe was running,
///     only the newest rebuild is applied.
///   • MERGE and ADD MUSIC await <see cref="EnsureTimelineReadyAsync"/>. In practice the analysis
///     finished long before, because clips are queued minutes before either button is pressed.
///
/// ONE TRUTH. The same <see cref="MergedTimeline"/> drives the preview (VideoMergerWindow.Timeline.cs),
/// the Music Wizard's clip lanes (<see cref="MusicClipWindows"/>), the music preview, and the export
/// (<see cref="ApplyScraperToWorker"/>). When the layout changes (scraper toggled, custom thumbnail
/// set or cleared), the music window is re-anchored through (clip, source second) by
/// <see cref="MergedTimeline.Remap"/>, so the song still starts on the moment the user chose.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class VideoMergerWindow
{
    private MergedTimeline _timeline = MergedTimeline.Empty;
    private List<double> _timelineIntroTags = new();
    private bool _timelineReady;
    private int _timelineVersion;
    private Task _timelineBuildTask = Task.CompletedTask;
    private bool _scraperEnabled = Infrastructure.SettingsManager.Instance.MergerThumbnailScraper;
    private bool _suppressScraperToggle;

    /// <summary>SCRAPER_04 — the custom thumbnail, by file and SOURCE second (null = none, clip 1 keeps its intro).</summary>
    private string? _thumbPath;
    private double _thumbSourceSec;

    /// <summary>The timeline the current music window was placed on (null = no music, or placed before analysis).</summary>
    private MergedTimeline? _musicTimeline;

    private bool HasCustomThumbnail => _thumbPath != null;

    private void InitializeScraper()
    {
        InitializeSession();   // MERGESESSION_01 — clip ids, autosave, restore on open
        var cb = ScraperCheckBoxCtl;
        if (cb != null)
        {
            cb.IsChecked = _scraperEnabled;
            cb.IsCheckedChanged += (s, e) =>
            {
                if (_suppressScraperToggle) return;
                _scraperEnabled = cb.IsChecked == true;
                Infrastructure.SettingsManager.Instance.MergerThumbnailScraper = _scraperEnabled;
                _ = Task.Run(Infrastructure.SettingsManager.Save);   // file write off the UI thread
                RuntimeLog.Info("MERGER", $"Thumbnail Scraper turned {(_scraperEnabled ? "ON" : "OFF")}.");
                Controls.FloatingNotice.Show(this,
                    _scraperEnabled ? "Thumbnail Scraper on: intros between clips will be cut." : "Thumbnail Scraper off: every clip is merged whole.",
                    Controls.NoticeKind.Info);
                ScheduleTimelineRebuild();
            };
        }

        var list = VideoListCtl;
        if (list != null) list.DoubleTapped += VideoList_DoubleTapped;

        var thumbBtn = this.FindControl<Button>("SetMergerThumbnailButton");
        if (thumbBtn != null) thumbBtn.Click += (s, e) => ToggleThumbnailAtPlayhead();

        ScheduleTimelineRebuild();
    }

    /// <summary>Settings window closed: pick up a change made there.</summary>
    private void SyncScraperFromSettings()
    {
        bool v = Infrastructure.SettingsManager.Instance.MergerThumbnailScraper;
        if (v == _scraperEnabled) return;
        _scraperEnabled = v;
        var cb = ScraperCheckBoxCtl;
        if (cb != null)
        {
            _suppressScraperToggle = true;
            cb.IsChecked = v;
            _suppressScraperToggle = false;
        }
        ScheduleTimelineRebuild();
    }

    /// <summary>
    /// SCRAPER_05 — double-click a clip in the list: open THAT file alone in Windows' default video
    /// app. The preview is paused so two players do not talk over each other.
    /// </summary>
    private void VideoList_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (e.Source is not Avalonia.Visual v) return;
        var item = v as ListBoxItem ?? v.FindAncestorOfType<ListBoxItem>();
        if (item?.DataContext is not string path || !System.IO.File.Exists(path)) return;
        e.Handled = true;
        _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            RuntimeLog.Info("MERGER", $"Opened {System.IO.Path.GetFileName(path)} in the default video app.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MERGER", $"Could not open {System.IO.Path.GetFileName(path)} in the default app: {ex.Message}");
            Controls.FloatingNotice.Show(this, "Windows could not open that video in its default app.", Controls.NoticeKind.Error);
        }
    }

    private void ScheduleTimelineRebuild() => _timelineBuildTask = RebuildTimelineAsync();

    private async Task RebuildTimelineAsync()
    {
        int version = ++_timelineVersion;
        var queue = VideoQueue.ToList();
        if (queue.Count == 0)
        {
            ApplyTimeline(MergedTimeline.Empty, new List<double>());
            return;
        }

        string ffprobe = _ffprobePath;
        MergeClipInfo[] infos;
        try
        {
            infos = await Task.WhenAll(queue.Select(p => MergeClipAnalyzer.AnalyzeAsync(ffprobe, p)));
        }
        catch (Exception ex)
        {
            RuntimeLog.WarnThrottled("MERGER", $"Clip analysis failed: {ex.Message}");
            return;
        }

        if (version != _timelineVersion) return;   // a newer queue state superseded this one

        int thumbIndex = _thumbPath == null ? -1 : queue.FindIndex(p => SameVideoPath(p, _thumbPath));
        if (_thumbPath != null && thumbIndex < 0)
        {
            RuntimeLog.Info("MERGER", "The custom thumbnail's clip left the queue; clip 1's own intro is used again.");
            _thumbPath = null;
        }

        var sources = infos.Select(i => new MergeClipSource(i.Path, i.DurationSec, i.IntroSec)).ToList();
        var built = MergedTimeline.Build(sources, _scraperEnabled, _thumbPath != null);
        ApplyTimeline(built, infos.Select(i => i.IntroSec).ToList());

        if (infos.Any(i => !i.Ok))
            RuntimeLog.WarnThrottled("MERGER", "At least one queued clip could not be analysed; it is merged whole and previewed on its own.");
    }

    private void ApplyTimeline(MergedTimeline built, List<double> introTags)
    {
        _timeline = built;
        _timelineIntroTags = introTags;
        _timelineReady = built.Clips.Count > 0 && built.Clips.All(c => c.LengthSec > 0);

        // The clip mpv is holding keeps its identity across reorders.
        _playClip = _playPath == null ? -1 : built.Clips.ToList().FindIndex(c => SameVideoPath(c.Path, _playPath));
        if (_playClip < 0) _playPath = null;

        // SCRAPER_04 — the thumbnail frame can never sit inside a removed intro.
        if (_thumbPath != null)
        {
            var c = built.Clips.FirstOrDefault(x => SameVideoPath(x.Path, _thumbPath));
            if (c.Path != null) _thumbSourceSec = Math.Clamp(_thumbSourceSec, c.ContentStartSec, c.ContentEndSec);
        }

        // D — the music follows the clip moment it was placed on.
        if (_musicResult != null && _musicTimeline != null && !_musicIsStale && built.SameQueue(_musicTimeline)
            && built.Signature != _musicTimeline.Signature)
        {
            double oldStart = _musicResult.TimelineStartSeconds, oldEnd = _musicResult.TimelineEndSeconds;
            double newStart = built.Remap(_musicTimeline, oldStart);
            double newEnd = Math.Max(newStart + 0.01, built.Remap(_musicTimeline, oldEnd));
            _musicResult.TimelineStartSeconds = newStart;
            _musicResult.TimelineEndSeconds = newEnd;
            _musicTimeline = built;
            RuntimeLog.Info("MERGER", $"Music window re-anchored to the new clip layout: {oldStart:F3}-{oldEnd:F3}s -> {newStart:F3}-{newEnd:F3}s.");
        }

        int removed = built.Clips.Count(c => c.RemovedIntroSec > 0);
        RuntimeLog.Info("MERGER", $"Merged timeline: {built.Clips.Count} clip(s), {built.TotalSec:F3}s, {removed} thumbnail intro(s) removed, custom thumbnail {(built.CustomThumbnail ? "yes" : "no")}.");

        var cb = ScraperCheckBoxCtl;
        if (cb != null)
        {
            int tagged = introTags.Count(t => t > 0);
            ToolTip.SetTip(cb,
                "Thumbnail Scraper: cut the short cover-picture intro this app adds to every export from the 2nd clip onward, " +
                "so the joined video has no flashes between clips. The first clip keeps its intro unless you set a custom thumbnail.\n" +
                $"This queue: {tagged} of {introTags.Count} clip(s) carry an intro; {removed} will be cut.");
        }

        InvalidateMergedTimelineDrawing();
        PaintMergedLength();
        NoteEdlChanged();   // MERGESESSION_01 — analysis/layout changed what the edit list records
        ScheduleLaneRefresh();   // LANES_01
        UpdateMergerGranularButton();   // MERGEEDIT_02 — enabled once the merged timeline matches the queue
    }

    /// <summary>The timeline describes exactly the queue on screen, in order.</summary>
    private bool TimelineMatchesQueue()
    {
        if (!_timelineReady || _timeline.Clips.Count != VideoQueue.Count) return false;
        for (int i = 0; i < VideoQueue.Count; i++)
            if (!SameVideoPath(_timeline.Clips[i].Path, VideoQueue[i])) return false;
        return true;
    }

    /// <summary>Awaits the analysis for the CURRENT queue. Only MERGE and ADD MUSIC wait here.</summary>
    private async Task EnsureTimelineReadyAsync()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { await _timelineBuildTask; } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            if (TimelineMatchesQueue() || VideoQueue.Count == 0) return;
            ScheduleTimelineRebuild();
        }
    }

    /// <summary>TOTAL LENGTH from the merged timeline once it is known (overrides the estimator's raw sum).</summary>
    private void PaintMergedLength()
    {
        if (!TimelineMatchesQueue()) return;
        double speed = _baseSpeed > 0.01 ? _baseSpeed : 1.0;
        var length = this.FindControl<TextBlock>("EstimatedLengthText");
        if (length != null) length.Text = FormatDuration(EdlOutputSec() ?? _timeline.TotalSec / speed + _timeline.SyntheticIntroSec);   // MERGESIZE_01
    }

    /// <summary>Merged (1.0x) length handed to the Music Wizard.</summary>
    private double MusicTimelineTotalSec() => TimelineMatchesQueue() ? _timeline.TotalSec : _cachedTotalDurationSec;

    /// <summary>SCRAPER_02 — the kept window of every clip, for the Music Wizard's lanes.</summary>
    private List<(double StartSec, double EndSec)>? MusicClipWindows()
        => TimelineMatchesQueue() ? _timeline.Clips.Select(c => (c.ContentStartSec, c.ContentEndSec)).ToList() : null;

    /// <summary>The wizard returned a music window: it was placed on the current timeline.</summary>
    private void OnMusicPlaced()
    {
        _musicTimeline = TimelineMatchesQueue() ? _timeline : null;
        InvalidateMergedTimelineDrawing();
        NoteEdlChanged();
    }

    /// <summary>SCRAPER_01..04 — hands the export the exact cut the preview shows.</summary>
    private void ApplyScraperToWorker(MergerWorker worker)
    {
        // MERGEGRAPH_01 — the export renders the same edit list the editor, preview and undo use.
        CaptureEdlNow(flush: false);
        worker.Edl = CurrentEdl;

        if (!TimelineMatchesQueue())
        {
            RuntimeLog.WarnThrottled("MERGER", "Merged timeline not available for this queue; every clip is merged whole.");
            return;
        }

        worker.ClipIntroSkipSec = _timeline.Clips.Select(c => c.RemovedIntroSec > 0 ? c.ContentStartSec : 0).ToList();
        worker.ClipIntroTagSec = new List<double>(_timelineIntroTags);
        if (_thumbPath != null)
        {
            int idx = _timeline.Clips.ToList().FindIndex(c => SameVideoPath(c.Path, _thumbPath));
            if (idx >= 0)
            {
                worker.ThumbnailClipIndex = idx;
                worker.ThumbnailSourceSec = _thumbSourceSec;
            }
        }
        RuntimeLog.Info("MERGER", $"Export uses the merged timeline: {worker.ClipIntroSkipSec.Count(x => x > 0)} intro(s) cut, custom thumbnail clip {worker.ThumbnailClipIndex + 1}.");
    }

    // ── SCRAPER_04 — custom thumbnail ────────────────────────────────────────────────────────

    /// <summary>The thumbnail's position in merged seconds, or null when none is set.</summary>
    private double? ThumbnailMergedSec()
    {
        if (_thumbPath == null || !TimelineMatchesQueue()) return null;
        for (int i = 0; i < _timeline.Clips.Count; i++)
            if (SameVideoPath(_timeline.Clips[i].Path, _thumbPath)) return _timeline.ToMerged(i, _thumbSourceSec);
        return null;
    }

    private bool IsPlayheadOnThumbnail()
        => ThumbnailMergedSec() is double t && Math.Abs(PreviewPositionSec() - t) <= 0.5;

    /// <summary>The Main App's three-way button: SET, MOVE HERE, or REMOVE when parked on it.</summary>
    private void ToggleThumbnailAtPlayhead()
    {
        if (!TimelineMatchesQueue()) return;
        TakeManualControl();
        if (IsPlayheadOnThumbnail())
        {
            _thumbPath = null;
            RuntimeLog.Info("MERGER", "Custom thumbnail removed; clip 1 keeps its own intro.");
            Controls.FloatingNotice.Show(this, "Thumbnail removed. Clip 1's own cover picture is used.", Controls.NoticeKind.Info);
            ScheduleTimelineRebuild();
            return;
        }
        SetThumbnailAtMerged(PreviewPositionSec(), announce: true);
    }

    /// <summary>Points the thumbnail at the frame under <paramref name="mergedSec"/>.</summary>
    private void SetThumbnailAtMerged(double mergedSec, bool announce)
    {
        var (idx, src) = _timeline.Locate(mergedSec);
        if (idx < 0) return;
        bool wasSet = _thumbPath != null;
        _thumbPath = _timeline.Clips[idx].Path;
        _thumbSourceSec = src;
        if (announce)
        {
            RuntimeLog.Info("MERGER", $"Custom thumbnail set: clip {idx + 1} at {src:F3}s.");
            Controls.FloatingNotice.Show(this, "Thumbnail set. This frame becomes the cover picture of the merged video.", Controls.NoticeKind.Info);
        }
        // First custom thumbnail: clip 1's intro is now removed, which changes the layout.
        if (!wasSet) ScheduleTimelineRebuild();
        else { InvalidateMergedTimelineDrawing(); NoteEdlChanged(); }
    }
}
