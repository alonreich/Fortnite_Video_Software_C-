using Avalonia.Controls;

using Avalonia.Interactivity;

using Avalonia.Markup.Xaml;

using Avalonia.Input;

using Avalonia.Threading;

using FortniteVideoSoftware.Core.Infrastructure;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;


namespace FortniteVideoSoftware.App;


public class MusicTrackItem : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string DurationText { get; set; } = "";
    public string SizeText { get; set; } = "";
    public double DurationSec { get; set; } = 0.0;
    public long LastModifiedTicks { get; set; } = 0;
    
    private bool _isRecent;
    public bool IsRecent 
    { 
        get => _isRecent; 
        set 
        { 
            if (_isRecent != value) 
            { 
                _isRecent = value; 
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsRecent))); 
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(PinText))); 
            } 
        } 
    }
    public string PinText => IsRecent ? "RECENT" : "";
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public class MusicQueueItem
{
    public int Order { get; set; }
    public string OrderText => $"{Order}.";
    public string Name { get; set; } = string.Empty;
    public string DurationText { get; set; } = "";
}


public class MusicWizardResult
{
    public string MusicFilePath { get; set; } = string.Empty;
    public System.Collections.Generic.List<string> MusicFilePaths { get; set; } = new();
    public System.Collections.Generic.List<double> MusicDurationsSeconds { get; set; } = new();
    public double OffsetSeconds { get; set; } = 0.0;
    public double SongStartSeconds
    {
        get => OffsetSeconds;
        set => OffsetSeconds = value;
    }
    public double TimelineStartSeconds { get; set; } = 0.0;
    public double TimelineEndSeconds { get; set; } = 0.0;
    public bool EnableDucking { get; set; } = true;

    public bool EnableCarving { get; set; } = true;

    public double VideoVolume { get; set; } = 1.0;

    public double MusicVolume { get; set; } = 1.0;

    public double MusicDurationSeconds { get; set; } = 0.0;

    public bool LoopMusic { get; set; } = false;

}


public partial class MusicWizardWindow : Window

{

    public ObservableCollection<MusicTrackItem> AvailableTracks { get; } = new();

    public ObservableCollection<MusicQueueItem> AutoFillQueueItems { get; } = new();

    public MusicWizardResult? Result { get; private set; }

    /// <summary>
    /// EDIT3_01 — an existing music placement to REOPEN rather than start from nothing.
    ///
    /// Set by the Main App when the user chose EDIT on the ADD MUSIC button. When present the
    /// wizard restores the track, the song start point, the queue, the two volume sliders and the
    /// three phase-3 checkboxes, then jumps straight to phase 3 — the screen where the placement
    /// actually lives. Phases 1 and 2 remain reachable with BACK, so changing the song itself is
    /// still possible; this only decides where the user LANDS.
    ///
    /// Must be assigned before the window is shown. Null is the ordinary first-run path.
    /// </summary>
    public MusicWizardResult? InitialState { get; set; }
    
    private readonly FortniteVideoSoftware.App.Controls.VoiceOverPreviewPlayer _voiceOverPlayer = new();
    
    public static readonly Avalonia.StyledProperty<string> MusicSearchTextProperty =
        Avalonia.AvaloniaProperty.Register<MusicWizardWindow, string>(nameof(MusicSearchText), string.Empty);
    public string MusicSearchText
    {
        get => GetValue(MusicSearchTextProperty);
        set => SetValue(MusicSearchTextProperty, value);
    }


    // ══════════════════════════════════════════════════════════════════════════════════════
    // LIST_02 — THE LENGTH COLUMN FOLLOWS THE NAMES, THE NAMES DO NOT FOLLOW THE WINDOW.
    //
    // The name cell used to be a "*" column, so it swallowed every spare pixel and shoved the
    // length against the far right edge — a hand-span of nothing between a song and its own
    // duration on a 1300px-wide window, and the last digit clipped by the scrollbar on top of it.
    //
    // The name cell is now an explicit width, measured once from the LONGEST title actually in the
    // folder plus a 50px gap. Every row therefore shares one left-aligned block of names with the
    // lengths packed immediately after the longest of them — close enough to read across, and
    // identical on every row so the eye has a straight edge to follow.
    //
    // Measured, not guessed: a title's pixel width depends on the font scale the user chose in
    // Settings, so a hard-coded number would clip at Large and waste space at Small.
    // ══════════════════════════════════════════════════════════════════════════════════════
    public static readonly Avalonia.StyledProperty<double> TrackNameColumnWidthProperty =
        Avalonia.AvaloniaProperty.Register<MusicWizardWindow, double>(nameof(TrackNameColumnWidth), 320.0);

    public double TrackNameColumnWidth
    {
        get => GetValue(TrackNameColumnWidthProperty);
        set => SetValue(TrackNameColumnWidthProperty, value);
    }

    /// <summary>
    /// LIST_03 — the RECENT pin cell's width, measured so it is the same on every row.
    ///
    /// It was an Auto column holding either "RECENT" or an empty string, so it was ~40px wide on
    /// pinned rows and 0px on all the others — and the name column therefore began at a different
    /// x depending on whether the song happened to be recent. Nobody noticed because most rows are
    /// empty here, but it makes the column headings impossible to align to, and a heading that
    /// does not sit over its column is worse than no heading.
    /// </summary>
    public static readonly Avalonia.StyledProperty<double> TrackPinColumnWidthProperty =
        Avalonia.AvaloniaProperty.Register<MusicWizardWindow, double>(nameof(TrackPinColumnWidth), 52.0);

    public double TrackPinColumnWidth
    {
        get => GetValue(TrackPinColumnWidthProperty);
        set => SetValue(TrackPinColumnWidthProperty, value);
    }

    /// <summary>
    /// LIST_04 — the LENGTH column's width: the word "Length" plus 10px of breathing room on each
    /// side. It was a flat 72, which on most font scales left the column noticeably wider than
    /// anything in it.
    ///
    /// Floored at the widest duration actually in the list, because a column sized to its HEADING
    /// is only correct while the heading is the longest thing in it. One 1:04:07 track in a folder
    /// of three-minute songs would otherwise clip — which is the exact fault this column was
    /// reported for in the first place, reintroduced from the other direction.
    /// </summary>
    public static readonly Avalonia.StyledProperty<double> TrackLengthColumnWidthProperty =
        Avalonia.AvaloniaProperty.Register<MusicWizardWindow, double>(nameof(TrackLengthColumnWidth), 60.0);

    public double TrackLengthColumnWidth
    {
        get => GetValue(TrackLengthColumnWidthProperty);
        set => SetValue(TrackLengthColumnWidthProperty, value);
    }

    /// <summary>LIST_04 — 10px each side of the heading word, as specified.</summary>
    private const double TrackLengthPaddingPx = 20.0;

    /// <summary>LIST_02 — the gap the user asked for between the longest title and the length.</summary>
    private const double TrackNameGapPx = 50.0;
    private const double TrackNameMinWidthPx = 180.0;

    /// <summary>
    /// LIST_02 — measures the widest song title in the list and sizes the name column to it.
    ///
    /// Capped against the list's own width so a pathologically long filename cannot push the
    /// length column off the right-hand edge — the very problem this is fixing. Cheap: one
    /// FormattedText per track, run only when the list content or the list width changes, never
    /// per row and never per frame.
    /// </summary>
    private void RecalculateTrackNameColumnWidth()
    {
        try
        {
            var listbox = this.FindControl<ListBox>("MusicListBox");
            double fontSize = Infrastructure.ThemeManager.ScaledFontSize(11);
            var typeface = new Avalonia.Media.Typeface(
                Avalonia.Media.FontFamily.Default,
                Avalonia.Media.FontStyle.Normal,
                Avalonia.Media.FontWeight.SemiBold);

            // LIST_03 — the pin cell is sized for its only non-empty value, plus the 8px gap that
            // used to be a Margin. Measured for the same reason the name column is: "RECENT" is
            // wider at the larger Settings font scales.
            var pinTypeface = new Avalonia.Media.Typeface(
                Avalonia.Media.FontFamily.Default,
                Avalonia.Media.FontStyle.Normal,
                Avalonia.Media.FontWeight.Bold);
            var pinText = new Avalonia.Media.FormattedText(
                "RECENT",
                System.Globalization.CultureInfo.CurrentCulture,
                Avalonia.Media.FlowDirection.LeftToRight,
                pinTypeface,
                Infrastructure.ThemeManager.ScaledFontSize(9),
                Avalonia.Media.Brushes.White);
            TrackPinColumnWidth = Math.Round(pinText.Width + 8.0, 0);

            // LIST_04 — the heading sets the width; the longest value in the list sets the floor.
            var headingText = new Avalonia.Media.FormattedText(
                "Length",
                System.Globalization.CultureInfo.CurrentCulture,
                Avalonia.Media.FlowDirection.LeftToRight,
                pinTypeface,
                Infrastructure.ThemeManager.ScaledFontSize(10),
                Avalonia.Media.Brushes.White);
            double lengthWidth = headingText.Width + TrackLengthPaddingPx;

            double widest = 0;
            foreach (var track in AvailableTracks)
            {
                if (!string.IsNullOrEmpty(track.DurationText))
                {
                    var dt = new Avalonia.Media.FormattedText(
                        track.DurationText,
                        System.Globalization.CultureInfo.CurrentCulture,
                        Avalonia.Media.FlowDirection.LeftToRight,
                        typeface,
                        fontSize,
                        Avalonia.Media.Brushes.White);
                    double needed = dt.Width + 12.0;
                    if (needed > lengthWidth) lengthWidth = needed;
                }

                if (string.IsNullOrEmpty(track.Name)) continue;
                var ft = new Avalonia.Media.FormattedText(
                    track.Name,
                    System.Globalization.CultureInfo.CurrentCulture,
                    Avalonia.Media.FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    Avalonia.Media.Brushes.White);
                if (ft.Width > widest) widest = ft.Width;
            }

            TrackLengthColumnWidth = Math.Round(lengthWidth, 0);

            double target = widest > 0 ? widest + TrackNameGapPx : TrackNameMinWidthPx;

            // Everything else on the row: the measured pin cell, the two 10px separator margins,
            // the 1px rule, the 72px length cell, the row padding and a scrollbar. Reserved so the
            // length can never be pushed out of view.
            double RowFurniturePx = TrackPinColumnWidth + 10 + 1 + 10 + TrackLengthColumnWidth + 10 + 20;
            double listWidth = listbox?.Bounds.Width ?? 0;
            double ceiling = listWidth > RowFurniturePx + TrackNameMinWidthPx
                ? listWidth - RowFurniturePx
                : double.MaxValue;

            TrackNameColumnWidth = Math.Round(Math.Clamp(target, TrackNameMinWidthPx, ceiling), 0);
        }
        catch (Exception ex)
        {
            // A measurement failure must not empty the list; the registered default still renders.
            RuntimeLog.Swallowed(ex);
        }
    }

    private int _currentStep = 1;

    private readonly ApplicationPaths _paths = ApplicationPaths.CreateDefault();

    private bool _isSafeToClose = false;

    private string? _lastWaveformFile;

    private double _trackDuration = 100.0;

    private MusicTrackItem? _selectedTrack;

    private FortniteVideoSoftware.Core.Media.MpvIpcClient? _audioIpcClient;
    private bool _isPreviewPlaying = false;
    private double _previewCurrentOffset = 0.0;
    private DateTime? _previewStartTime = null;
    private DateTime? _phase3PreviewClockStartTime = null;
    private double _phase3PreviewClockStartOffsetSec = 0.0;
    private double _songStartSeconds = 0.0;
    private Avalonia.Threading.DispatcherTimer? _playheadTimer;
    private Avalonia.Controls.Shapes.Line? _waveformOffsetLine;
    private Avalonia.Controls.Shapes.Line? _waveformPlayheadLine;
    private Avalonia.Controls.Shapes.Line? _timelinePlayheadLine;

    private string _videoPath = "";
    private double _trimStartMs = 0;
    private double _trimEndMs = 0;
    private double _actualVideoDurationMs = 0;
    private string? _lastLoadedTrackPath;
    private string? _lastConfiguredTrackPath;
    private readonly System.Collections.Generic.List<string> _pendingAutoFillMusicPaths = new();
    private readonly System.Collections.Generic.List<MusicTrackItem> _allTracks = new();
    private readonly System.Collections.Generic.HashSet<string> _recentMusicPaths = new(StringComparer.OrdinalIgnoreCase);
    private string _musicSearchText = string.Empty;
    private string _musicSortMode = "Name";
    private bool _autoFillUseVisibleTracks = true;
    private CancellationTokenSource? _phase3LoadCts;
    private int _phase3LoadVersion;
    private bool _phase3Ready;
    private double _phase3VideoDurationSec = 60.0;
    private System.Collections.Generic.List<string> _lastPhase3ThumbFiles = new();
    private string? _lastPhase3WaveFile;
    private System.Collections.Generic.List<string>? _mergerVideos = null;
    private bool _isMergerMode = false;
    private double _phase3BaseSpeed = 1.0;
    private readonly System.Collections.Generic.List<FortniteVideoSoftware.Core.Media.SpeedSegment> _phase3SpeedSegments = new();
    private int _waveformRenderVersion = 0;
    private readonly System.Collections.Generic.List<double> _phase3ClipDurationsSec = new();
    private CancellationTokenSource? _audioAnalysisCts;
    private CancellationTokenSource? _musicScanCts;
    private readonly SemaphoreSlim _trackProbeGate = new(4, 4);
    private int _musicScanVersion;
    private int _phase3MusicSyncInFlight;
    private string? _phase3PreviewMusicPath;
    private double _phase3PreviewMusicSegmentStartSec = double.NaN;

    private FortniteVideoSoftware.App.MpvVideoView? WizardVideoHost => this.FindControl<Avalonia.Controls.Border>("VideoHostBorder")?.Child as FortniteVideoSoftware.App.MpvVideoView;

    private sealed class AudioEnergyAnalysis
    {
        public double BucketSeconds { get; init; }
        public double DurationSeconds { get; init; }
        public double[] Energy { get; init; } = Array.Empty<double>();
        public System.Collections.Generic.List<double> PeakTimesSeconds { get; init; } = new();
    }

    private sealed class Phase3MusicPreviewSegment
    {
        public string Path { get; init; } = string.Empty;
        public double TimelineStartSec { get; init; }
        public double TimelineEndSec { get; init; }
        public double FileStartSec { get; init; }
    }


    public MusicWizardWindow()

    {
        InitializeComponent();
        FortniteVideoSoftware.App.WindowBoundsHelper.Track(this, "MusicWizardBounds");
        _playheadTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _playheadTimer.Tick += PlayheadTimer_Tick;
        WirePreviewDetach();

        this.Loaded += (_, _) => Controls.CoachOverlay.Register(this, Controls.CoachTours.MusicWizardKey, Controls.CoachTours.MusicWizard);
        WireHelpButton();
    }

    /// <summary>ISSUE_04 — the permanent replay route for this screen's walkthrough.</summary>
    private void WireHelpButton()
    {
        var help = this.FindControl<Avalonia.Controls.Button>("WizardHelpButton");
        if (help != null) help.Click += (_, _) => Controls.CoachOverlay.Replay(this);
    }

    private PreviewDetachController? _previewDetach;

    private void WirePreviewDetach()
    {
        var btn = this.FindControl<Button>("WizardDetachPreviewBtn");
        if (btn == null) return;

        _previewDetach = new PreviewDetachController(
            this,
            PreviewDetachController.MusicWizardKey,
            "Preview Monitor — Add Music Wizard",
            () => WizardVideoHost);

        _previewDetach.StateChanged += detached =>
        {
            var watermark = this.FindControl<Avalonia.Controls.Border>("WizardPreviewDetachedWatermark");
            if (watermark != null) watermark.IsVisible = detached;
            _previewDetach!.SyncButton(btn);
        };

        _previewDetach.DetachUnavailable += why => SetPhase3Status(why);

        btn.Click += (_, _) => _previewDetach.Toggle();
        _previewDetach.SyncButton(btn);
    }

    /// <summary>UXQA_01: re-enable the detach button once phase 3's player actually exists.</summary>
    private void RefreshDetachButtonState()
        => _previewDetach?.SyncButton(this.FindControl<Button>("WizardDetachPreviewBtn"));

    private void PlayheadTimer_Tick(object? sender, EventArgs e)
    {
        // ══════════════════════════════════════════════════════════════════════════════════
        // MEME_07 — BEFORE EVERYTHING ELSE ON THIS TICK.
        //
        // A cutaway swaps the meme file into phase 3's mpv host, so CurrentTime and Duration stop
        // describing the gameplay. SyncPhase3VideoPreviewClock would then drive the video clock
        // from a position on the wrong file, the music would resync to it, and the playhead would
        // jump. Returning early is what keeps the A/B screen honest.
        // ══════════════════════════════════════════════════════════════════════════════════
        if (_currentStep == 3 && _phase3Memes.Count > 0 && WizardVideoHost?.IpcClient != null)
            EnsureMemePreviewDirector();
        if (_memePreview != null)
        {
            _memePreview.Suspended = _currentStep != 3;
            _memePreview.SetMemes(_phase3Memes);
            _memePreview.Tick();
            if (_memePreview.IsActive) return;
        }

        // PREVIEW1_01 — drive the 1-second ease-in. Costs one property write per 33 ms tick, and
        // only while a fade is actually running.
        if (_previewFadeStartUtc.HasValue) ApplyPreviewMusicVolume();

        if (_isPreviewPlaying)
        {
            SyncPhase3VideoPreviewClock();
            QueuePhase3MusicPreviewSync();
            EnforcePhase3PreviewEnd();
            UpdatePlayhead();
        }

        UpdatePhase3LiveZoomCrop();
        
        if (WizardVideoHost?.IpcClient != null)
        {
            double t = WizardVideoHost.IpcClient.CurrentTime;
            var timeMapper = FortniteVideoSoftware.Core.Media.GranularSpeedBuilder.CreateTimeMapper(_trimEndMs - _trimStartMs, _phase3SpeedSegments, _phase3BaseSpeed, _trimStartMs);
            double editedTimeSec = timeMapper(t);
            bool isVoicePaused = WizardVideoHost.IpcClient.IsPaused;
            bool ended = _trimEndMs > 0 && t >= _trimEndMs / 1000.0;
            _voiceOverPlayer.UpdatePlayback(isVoicePaused, ended, editedTimeSec, timeMapper, false);
        }
    }


    /// <summary>Last crop pushed to mpv, so an unchanged value is never re-sent every tick.</summary>
    private string _lastLiveCrop = "";

    /// <summary>
    /// PORTRAIT_01 — set by the Main App when Portrait mode is on.
    ///
    /// Phase 3 had no idea whether the export would be portrait, so its preview showed the full
    /// 16:9 frame while the export produced a 2:3 clip. That made the A/B screen — the one whose
    /// entire job is judging the finished result — the least truthful preview in the suite.
    /// </summary>
    public bool IsPortraitPreview { get; set; }

    private void UpdatePhase3LiveZoomCrop()
    {
        if (!FortniteVideoSoftware.Core.Media.VideoRenderMode.Current.UseHardwareAcceleration) return;

        var ipc = WizardVideoHost?.IpcClient;
        if (ipc == null) return;

        if (_currentStep != 3) { ClearPhase3LiveZoomCrop(); return; }
        if (_phase3SpeedSegments.Count == 0 && !IsPortraitPreview) { ClearPhase3LiveZoomCrop(); return; }

        double outputRelativeSec = GetCurrentPhase3VideoRelativeSeconds();
        double sourceRelativeSec = MapPhase3OutputToSourceRelativeSeconds(outputRelativeSec);
        double durSec = Math.Max(0.1, GetPhase3SourceDurationSeconds());

        var result = FortniteVideoSoftware.Core.Media.ZoomPreviewSimulator.Compute(
            _phase3SpeedSegments, sourceRelativeSec, durSec,
            IsPortraitPreview, ipc.VideoWidth, ipc.VideoHeight);

        if (!result.HasCrop) { ClearPhase3LiveZoomCrop(); return; }
        if (result.Crop == _lastLiveCrop) return;
        _lastLiveCrop = result.Crop;
        _ = ipc.SetPropertyAsync("video-crop", result.Crop);
    }

    /// <summary>Drops any simulated crop. Called when leaving phase 3 and on teardown.</summary>
    private void ClearPhase3LiveZoomCrop()
    {
        if (_lastLiveCrop.Length == 0) return;
        _lastLiveCrop = "";
        _ = WizardVideoHost?.IpcClient?.SetPropertyAsync("video-crop", "");
    }


    public MusicWizardWindow(System.Collections.Generic.List<string> mergerVideos, double totalDurationSec) : this()
    {
        _mergerVideos = mergerVideos;
        _isMergerMode = true;
        _videoPath = mergerVideos.FirstOrDefault() ?? "";
        _trimStartMs = 0;
        _trimEndMs = totalDurationSec * 1000.0;
        _playheadTimer?.Start();
        SharedInit();
    }

    public MusicWizardWindow(System.Collections.Generic.List<string> mergerVideos, double totalDurationSec, double baseSpeed) : this()
    {
        _mergerVideos = mergerVideos;
        _isMergerMode = true;
        _videoPath = mergerVideos.FirstOrDefault() ?? "";
        _trimStartMs = 0;
        _trimEndMs = totalDurationSec * 1000.0;
        ConfigurePhase3Timeline(baseSpeed, null);
        _playheadTimer?.Start();
        SharedInit();
    }

    public MusicWizardWindow(
        string videoPath,
        double trimStartMs,
        double trimEndMs,
        double baseSpeed = 1.0,
        System.Collections.Generic.IReadOnlyList<FortniteVideoSoftware.Core.Media.SpeedSegment>? speedSegments = null,
        VoiceOverWindow.VoiceOverResult? voiceOverResult = null,
        System.Collections.Generic.IReadOnlyList<FortniteVideoSoftware.Core.Media.CutRange>? cuts = null,
        System.Collections.Generic.IReadOnlyList<FortniteVideoSoftware.Core.Media.MemePlacement>? memes = null) : this()
    {
        _voiceOverPlayer.Result = voiceOverResult;
        _videoPath = videoPath;
        _trimStartMs = trimStartMs;
        _trimEndMs = trimEndMs;
        if (cuts != null) _phase3Cuts.AddRange(cuts);
        if (memes != null) _phase3Memes.AddRange(memes);
        ConfigurePhase3Timeline(baseSpeed, speedSegments);
        _playheadTimer?.Start();
        SharedInit();

        // EDIT3_01 — resuming has to wait for the window to exist: it writes to sliders and
        // checkboxes that FindControl cannot reach until the visual tree is up.
        this.Loaded += async (_, _) => await ResumeFromInitialStateAsync();
    }

    /// <summary>
    /// EDIT3_01 — REOPENS AN EXISTING MUSIC PLACEMENT AT PHASE 3.
    ///
    /// This mirrors, in one place, everything the phase 1 -> 2 -> 3 walk would have set, so the
    /// wizard arrives in exactly the state the user left it in. The order matters:
    ///
    ///   1. select the track FIRST — OnTrackSelected clears the auto-fill queue, so a queue
    ///      restored before it would be wiped;
    ///   2. then the queue, offsets, sliders and checkboxes;
    ///   3. then the phase switch and the phase-3 load, which is what the step-2 branch of
    ///      OnNextClicked does.
    ///
    /// The track item is SYNTHESISED when the music folder scan has not found it (the scan is
    /// asynchronous, and the file may since have been moved out of the scanned folder entirely).
    /// Resuming must not depend on a background scan having finished, and a track the user
    /// already used must not become unreachable because its folder changed.
    /// </summary>
    private async Task ResumeFromInitialStateAsync()
    {
        var state = InitialState;
        InitialState = null;   // one-shot: a later Loaded must not re-run this
        if (state == null) return;
        if (string.IsNullOrWhiteSpace(state.MusicFilePath)) return;

        try
        {
            var track = FindTrackByPath(state.MusicFilePath)
                        ?? AvailableTracks.FirstOrDefault(t =>
                               string.Equals(t.FilePath, state.MusicFilePath, StringComparison.OrdinalIgnoreCase));

            if (track == null)
            {
                track = new MusicTrackItem
                {
                    Name = Path.GetFileNameWithoutExtension(state.MusicFilePath),
                    FilePath = state.MusicFilePath,
                    Title = Path.GetFileNameWithoutExtension(state.MusicFilePath),
                    DurationSec = state.MusicDurationSeconds
                };
            }

            OnTrackSelected(track);

            var listbox = this.FindControl<ListBox>("MusicListBox");
            if (listbox != null && AvailableTracks.Contains(track)) listbox.SelectedItem = track;

            double duration = state.MusicDurationSeconds;
            if (duration <= 0.001)
            {
                var prober = new FortniteVideoSoftware.Core.Media.MediaProber(ResolveFfprobePath(), state.MusicFilePath);
                duration = await prober.GetDurationAsync();
            }
            _trackDuration = Math.Max(1.0, duration > 0 ? duration : track.DurationSec);
            track.DurationSec = _trackDuration;

            _songStartSeconds = Math.Clamp(state.OffsetSeconds, 0, Math.Max(0, _trackDuration - 0.01));
            _previewCurrentOffset = _songStartSeconds;
            _lastConfiguredTrackPath = state.MusicFilePath;
            _lastLoadedTrackPath = null;

            // A multi-song placement was built by Auto-Fill; restore the whole queue, not just
            // the first track, or applying again would silently drop every song after the first.
            _pendingAutoFillMusicPaths.Clear();
            if (state.MusicFilePaths != null && state.MusicFilePaths.Count > 1)
            {
                _pendingAutoFillMusicPaths.AddRange(state.MusicFilePaths);
                UpdateAutoFillQueuePreview();
                var autoFillBtn = this.FindControl<Button>("AutoFillSongsBtn");
                if (autoFillBtn != null) autoFillBtn.Content = $"Auto-Filled {_pendingAutoFillMusicPaths.Count} Songs";
            }

            var selectedLabel = this.FindControl<TextBlock>("SelectedTrackLabel");
            if (selectedLabel != null) selectedLabel.Text = track.Name;
            var offsetLabel = this.FindControl<TextBlock>("OffsetLabel");
            if (offsetLabel != null) offsetLabel.Text = $"Song begins at {FormatSeconds(_songStartSeconds)}";

            var videoVolSlider = this.FindControl<Slider>("VideoVolSlider");
            if (videoVolSlider != null) videoVolSlider.Value = Math.Clamp(state.VideoVolume * 100.0, videoVolSlider.Minimum, videoVolSlider.Maximum);
            var musicVolSlider = this.FindControl<Slider>("MusicVolSlider");
            if (musicVolSlider != null) musicVolSlider.Value = Math.Clamp(state.MusicVolume * 100.0, musicVolSlider.Minimum, musicVolSlider.Maximum);

            var duckingCheck = this.FindControl<CheckBox>("DuckingCheckBox");
            if (duckingCheck != null) duckingCheck.IsChecked = state.EnableDucking;
            var carvingCheck = this.FindControl<CheckBox>("CarvingCheckBox");
            if (carvingCheck != null) carvingCheck.IsChecked = state.EnableCarving;
            var loopCheck = this.FindControl<CheckBox>("LoopMusicCheckBox");
            if (loopCheck != null) loopCheck.IsChecked = state.LoopMusic;

            _ = RenderWaveformAsync(state.MusicFilePath);
            DrawTimelineScale();
            UpdatePlayhead();

            // Same transition the step-2 branch of OnNextClicked performs.
            StopPreview();
            CancelPhase3Load();
            _phase3Ready = false;
            _currentStep = 3;
            UpdateStepVisibility();
            UpdateNextButtonState();

            _phase3LoadCts = new CancellationTokenSource();
            int loadVersion = ++_phase3LoadVersion;
            RuntimeLog.Info("MUSIC_WIZARD",
                $"Reopened at phase 3 for editing: '{Path.GetFileName(state.MusicFilePath)}', " +
                $"{(state.MusicFilePaths?.Count ?? 1)} track(s), song start {_songStartSeconds:F2}s.");
            await LoadPhase3DataAsync(_phase3LoadCts.Token, loadVersion);
        }
        catch (Exception ex)
        {
            // Falling back to phase 1 is a usable outcome; a half-restored phase 3 is not.
            RuntimeLog.Fail("MUSIC_WIZARD", $"Could not reopen the existing music placement, starting from the song list instead: {ex.Message}");
            _currentStep = 1;
            UpdateStepVisibility();
            UpdateNextButtonState();
            ShowToast("Could not reopen your music setup — please pick the song again.");
        }
    }

    /// <summary>
    /// ══════════════════════════════════════════════════════════════════════════════
    /// CUTS_02 — SECTIONS THE SPEED EDITOR DELETED, in absolute source milliseconds.
    ///
    /// This wizard lays music against the length of the FINISHED video. Every duration it works
    /// from — the coverage check, Smart Fit's search window, Fit By End Of Video, the phase-3
    /// timeline, the result's TimelineEndSeconds — comes out of
    /// CalculatePhase3EffectiveDurationSeconds, and that was building its OutputTimeline without
    /// the cuts. So a project with two minutes deleted told the wizard the video was two minutes
    /// longer than it will be: the music was stretched to cover footage that no longer exists, and
    /// every beat aligned by Smart Fit landed two minutes off the mark.
    ///
    /// This is the identical class of bug as TIME_01 (freezes making the video longer than this
    /// method believed) and is fixed the same way: hand the real edit list to the one type that
    /// owns source-to-output time, and let it do the arithmetic.
    ///
    /// Nothing is DRAWN for these. Phase 3's timeline is output time, where a cut is zero seconds
    /// wide by definition — there is no gap to mark, because in the finished video there is none.
    /// ══════════════════════════════════════════════════════════════════════════════
    /// </summary>
    private readonly System.Collections.Generic.List<FortniteVideoSoftware.Core.Media.CutRange> _phase3Cuts = new();

    /// <summary>
    /// MEME_06 — memes spliced into the video, in clip-relative source seconds.
    ///
    /// Same class of bug as CUTS_03 and TIME_01, from the opposite direction: a cut makes the
    /// finished video SHORTER than this wizard believed, a meme makes it LONGER. Without these the
    /// coverage check, Smart Fit, Fit By End Of Video and the phase-3 preview would all lay music
    /// against a video shorter than the one that gets exported, so the music would stop early by
    /// the total length of every meme.
    ///
    /// Nothing is drawn for them here: phase 3's ruler is output time, where a meme is a stretch of
    /// foreign footage the music simply plays over (or not — see KeepMusicDuringMeme).
    /// </summary>
    private readonly System.Collections.Generic.List<FortniteVideoSoftware.Core.Media.MemePlacement> _phase3Memes = new();

    /// <summary>
    /// MEME_07 — plays each meme in the phase-3 A/B preview at the moment it interrupts the
    /// gameplay. This screen's whole job is judging the finished result, so it is the one preview
    /// that must not quietly skip a cutaway the export will make. See
    /// <see cref="Infrastructure.MemePreviewDirector"/> for the approach and the host-tick rule.
    /// </summary>
    private Infrastructure.MemePreviewDirector? _memePreview;

    /// <summary>MEME_07 — built lazily, because phase 3's video host is created on demand.</summary>
    private void EnsureMemePreviewDirector()
    {
        if (_memePreview != null) return;

        _memePreview = new Infrastructure.MemePreviewDirector(
            () => WizardVideoHost?.IpcClient,
            () => _videoPath,
            () => _trimStartMs / 1000.0,
            SetMemeSwapOverlay,
            "MUSIC_WIZARD");

        // The agreed behaviour: the meme's own sound plays, and the music pauses with the gameplay
        // and carries on afterwards. The music runs in its own audio-only mpv client, which the
        // tick's early return would otherwise leave playing straight over the meme.
        _memePreview.MemeStarted += () =>
        {
            if (_audioIpcClient != null) _ = _audioIpcClient.SetPropertyAsync("pause", "yes");
        };
        _memePreview.MemeEnded += () =>
        {
            if (_audioIpcClient != null && _isPreviewPlaying)
                _ = _audioIpcClient.SetPropertyAsync("pause", "no");
        };
    }

    /// <summary>MEME_07 — the black-screen notice shown across the two file swaps.</summary>
    private void SetMemeSwapOverlay(bool visible, string message)
    {
        var overlay = this.FindControl<Avalonia.Controls.Border>("MemeSwapOverlay");
        var text = this.FindControl<Avalonia.Controls.TextBlock>("MemeSwapOverlayText");
        if (text != null && !string.IsNullOrEmpty(message)) text.Text = message;
        if (overlay != null) overlay.IsVisible = visible;
    }

    private void ConfigurePhase3Timeline(
        double baseSpeed,
        System.Collections.Generic.IReadOnlyList<FortniteVideoSoftware.Core.Media.SpeedSegment>? speedSegments)
    {
        _phase3BaseSpeed = baseSpeed > 0.001 ? baseSpeed : 1.0;
        _phase3SpeedSegments.Clear();
        if (speedSegments != null)
            _phase3SpeedSegments.AddRange(speedSegments);
    }

    private void OnGlobalMasterVolumeChanged(int volume)
    {
        if (WizardVideoHost?.IpcClient != null)
            _ = WizardVideoHost.IpcClient.SetPropertyDoubleAsync("volume", GetPreviewVideoVolume(volume));
        if (_audioIpcClient != null)
            _ = _audioIpcClient.SetPropertyDoubleAsync("volume", GetPreviewMusicVolume(volume));
    }

    private void SharedInit()
    {
        if (FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.Defaults.RememberMusicVolumes)
        {
            try
            {
                var state = new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths).LoadSync();
                var vSlider = this.FindControl<Avalonia.Controls.Slider>("VideoVolSlider");
                var mSlider = this.FindControl<Avalonia.Controls.Slider>("MusicVolSlider");

                if (vSlider != null && state.TryGetPropertyValue("WizardVideoVolume", out var vvNode) && vvNode != null)
                    vSlider.Value = vvNode.GetValue<double>();

                if (mSlider != null && state.TryGetPropertyValue("WizardMusicVolume", out var mvNode) && mvNode != null)
                    mSlider.Value = mvNode.GetValue<double>();
            }
            catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        }

        FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged += OnGlobalMasterVolumeChanged;

        this.Closing += (s, e) => {
            WindowBoundsHelper.SaveBoundsSync(this, "MusicWizardBounds");
        };

        LoadRecentMusicPins();

        var listbox = this.FindControl<ListBox>("MusicListBox");

        if (listbox != null)

        {

            listbox.ItemsSource = AvailableTracks;

            // LIST_02 — a resized window changes the ceiling the name column is clamped against.
            listbox.SizeChanged += (_, _) => RecalculateTrackNameColumnWidth();

            listbox.SelectionChanged += (s, e) => OnTrackSelected(listbox.SelectedItem as MusicTrackItem);

            listbox.DoubleTapped += (s, e) =>

            {

                if (listbox.SelectedItem != null && _currentStep == 1)

                {

                    RuntimeLog.Info("UI", "User double-clicked a track to proceed in Music Wizard.");

                    OnNextClicked(listbox, new RoutedEventArgs());

                }

            };

            LoadMusicDirectory();

        }

        var queueList = this.FindControl<ListBox>("AutoFillQueueList");
        if (queueList != null)
        {
            queueList.ItemsSource = AutoFillQueueItems;
        }

        var searchBox = this.FindControl<TextBox>("MusicSearchBox");
        if (searchBox != null)
        {
            searchBox.TextChanged += (s, e) =>
            {
                _musicSearchText = searchBox.Text ?? string.Empty;
                ApplyTrackFilterAndSort();
            };
            searchBox.KeyDown += OnMusicSearchKeyDown;
            Dispatcher.UIThread.Post(() => searchBox.Focus(), DispatcherPriority.Input);
        }

        var clearSearchBtn = this.FindControl<Button>("ClearSearchBtn");
        if (clearSearchBtn != null)
        {
            clearSearchBtn.Click += (s, e) =>
            {
                if (searchBox != null)
                {
                    searchBox.Text = string.Empty;
                    searchBox.Focus();
                }
            };
        }

        var sortCombo = this.FindControl<ComboBox>("MusicSortComboBox");
        if (sortCombo != null)
        {
            sortCombo.SelectionChanged += (s, e) =>
            {
                if (sortCombo.SelectedItem is ComboBoxItem item && item.Content != null)
                {
                    _musicSortMode = item.Content.ToString() ?? "Name";
                    ApplyTrackFilterAndSort();
                }
            };
        }


        AddHandler(DragDrop.DropEvent, OnFileDrop);

        var loopCheck = this.FindControl<CheckBox>("LoopMusicCheckBox");
        if (loopCheck != null)
        {
            loopCheck.IsCheckedChanged += (s, e) =>
            {
                UpdateCoverageBar();
                UpdateAutoFillQueuePreview();
                UpdateProblemFlags();
            };
        }

        var duckingCheck = this.FindControl<CheckBox>("DuckingCheckBox");
        if (duckingCheck != null)
        {
            duckingCheck.IsCheckedChanged += (s, e) =>
            {
                UpdateDuckingCompareButton();
                ApplyPreviewMusicVolume();
                UpdateProblemFlags();
            };
        }

        var carvingCheckWire = this.FindControl<CheckBox>("CarvingCheckBox");
        if (carvingCheckWire != null)
        {
            carvingCheckWire.IsCheckedChanged += (s, e) =>
            {
                ApplyPreviewMusicFilters();
                UpdateProblemFlags();
            };
        }

        var visibleOnlyCheck = this.FindControl<CheckBox>("AutoFillVisibleOnlyCheckBox");
        if (visibleOnlyCheck != null)
        {
            visibleOnlyCheck.IsCheckedChanged += (s, e) =>
            {
                _autoFillUseVisibleTracks = visibleOnlyCheck.IsChecked ?? true;
            };
        }

        this.FindControl<Button>("QueueMoveUpBtn")!.Click += (s, e) => MoveQueuedTrack(-1);
        this.FindControl<Button>("QueueMoveDownBtn")!.Click += (s, e) => MoveQueuedTrack(1);
        this.FindControl<Button>("QueueRemoveBtn")!.Click += (s, e) => RemoveSelectedQueuedTrack();

        var autoFillBtn = this.FindControl<Button>("AutoFillSongsBtn");
        if (autoFillBtn != null)
        {
            autoFillBtn.Click += (s, e) =>
            {
                if (_selectedTrack != null)
                {
                    BuildAutoFillQueue();
                }
            };
        }

        var fitByEndBtn = this.FindControl<Button>("FitByEndBtn");
        if (fitByEndBtn != null)
            fitByEndBtn.Click += async (s, e) => await ApplyFitByEndAsync(fitByEndBtn);

        var beatSnapBtn = this.FindControl<Button>("BeatSnapBtn");
        if (beatSnapBtn != null)
            beatSnapBtn.Click += async (s, e) => await SnapSongStartToBeatAsync(beatSnapBtn);

        // KEYS_01 — tunnel, so it is seen before the song list eats the arrow keys.
        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, OnWizardKeyDown,
                   Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // COVER_01 — the coverage warning on the last screen is now something you can press.
        var problemPanel = this.FindControl<Border>("ProblemFlagsPanel");
        if (problemPanel != null)
        {
            problemPanel.PointerPressed += async (s, e) =>
            {
                if (GetMusicShortfallSeconds() <= 0.0) return;
                e.Handled = true;
                await WarnIfMusicTooShortAsync(askEvenIfAlreadyAccepted: true);
                UpdateProblemFlags();
            };
        }

        var smartFitBtn = this.FindControl<Button>("SmartFitBtn");
        if (smartFitBtn != null)
            smartFitBtn.Click += async (s, e) => await ApplySmartFitAsync(smartFitBtn);


        var downloadSongsBtn = this.FindControl<Button>("DownloadSongsBtn");
        if (downloadSongsBtn != null)
        {
            downloadSongsBtn.Click += async (s, e) =>
            {
                downloadSongsBtn.IsEnabled = false;
                try
                {
                    await RunSongDownloadAsync();
                }
                finally
                {
                    downloadSongsBtn.IsEnabled = true;
                }
            };
        }

        var changeFolderBtn = this.FindControl<Button>("ChangeFolderBtn");

        if (changeFolderBtn != null)

        {

            changeFolderBtn.Click += async (s, e) =>

            {

                var musicPath = Infrastructure.MemeDirectory.GetMusicRoot();

                try
                {
                    if (File.Exists(_paths.SessionStateFile))
                    {
                        var state = FortniteVideoSoftware.Core.Infrastructure.AtomicJsonFile.ReadObject(_paths.SessionStateFile);
                        if (state != null && state.TryGetPropertyValue("CustomMusicDirectory", out var node) && node != null)
                        {
                            string customPath = node.ToString();
                            if (Directory.Exists(customPath))
                            {
                                musicPath = customPath;
                            }
                        }
                    }
                }
                catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }

                Avalonia.Platform.Storage.IStorageFolder? musicFolder = null;
                try
                {
                    var uri = new Uri(musicPath);
                    musicFolder = await this.StorageProvider.TryGetFolderFromPathAsync(uri);
                }
                catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }

                var result = await this.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions

                {

                    Title = "Select Music Folder",

                    SuggestedStartLocation = musicFolder,

                    AllowMultiple = false

                });


                if (result != null && result.Count > 0)

                {

                    string selectedFolderPath = result[0].Path.LocalPath;


                    try

                    {

                        await new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths)
                            .UpdatePropertiesAsync(new System.Text.Json.Nodes.JsonObject
                            {
                                ["CustomMusicDirectory"] = selectedFolderPath
                            });

                    }

                    catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }


                    await ScanDirectoryForMusicAsync(selectedFolderPath);

                }

            };

        }


        var timelineMarkersCanvas = this.FindControl<Canvas>("TimelineMarkersCanvas");

        if (timelineMarkersCanvas != null)

        {

            timelineMarkersCanvas.SizeChanged += (s, e) => { DrawTimelineScale(); UpdatePlayhead(); };

            bool isScrubbingTimeline = false;
            timelineMarkersCanvas.PointerPressed += (s, e) => {
                var pt = e.GetCurrentPoint(timelineMarkersCanvas);
                if (pt.Properties.IsLeftButtonPressed) {
                    isScrubbingTimeline = true;
                    e.Pointer.Capture(timelineMarkersCanvas);
                    SetOffsetFromPointer(pt.Position.X, timelineMarkersCanvas.Bounds.Width);
                }
            };
            timelineMarkersCanvas.PointerMoved += (s, e) => {
                if (isScrubbingTimeline && e.GetCurrentPoint(timelineMarkersCanvas).Properties.IsLeftButtonPressed) {
                    SetOffsetFromPointer(e.GetPosition(timelineMarkersCanvas).X, timelineMarkersCanvas.Bounds.Width);
                }
            };
            timelineMarkersCanvas.PointerReleased += (s, e) => {
                isScrubbingTimeline = false;
                e.Pointer.Capture(null);
            };

            timelineMarkersCanvas.KeyDown += (s, e) => HandleSongOffsetKeyDown(e);

        }


        var canvas = this.FindControl<Canvas>("WaveformCanvas");

        if (canvas != null)

        {

            canvas.SizeChanged += (s, e) => { UpdatePlayhead(); };

            bool isScrubbingWaveform = false;
            canvas.PointerPressed += (s, e) => {
                var pt = e.GetCurrentPoint(canvas);
                if (pt.Properties.IsLeftButtonPressed) {
                    isScrubbingWaveform = true;
                    e.Pointer.Capture(canvas);
                    SetOffsetFromPointer(pt.Position.X, canvas.Bounds.Width);
                }
            };
            canvas.PointerMoved += (s, e) => {
                if (isScrubbingWaveform && e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) {
                    SetOffsetFromPointer(e.GetPosition(canvas).X, canvas.Bounds.Width);
                }
            };
            canvas.PointerReleased += (s, e) => {
                isScrubbingWaveform = false;
                e.Pointer.Capture(null);
            };

            canvas.KeyDown += (s, e) => HandleSongOffsetKeyDown(e);

        }


        var laneHolder = this.FindControl<Panel>("Phase3LaneContentHolder");
        var lanesHost = this.FindControl<FortniteVideoSoftware.App.Controls.TimelineLanesControl>("Phase3Lanes");
        var thumbContent = this.FindControl<Panel>("Phase3ThumbLaneContent");
        var waveContent = this.FindControl<Panel>("Phase3WaveLaneContent");
        if (laneHolder != null && lanesHost?.LaneAHost != null && lanesHost.LaneBHost != null)
        {
            if (thumbContent != null)
            {
                laneHolder.Children.Remove(thumbContent);
                thumbContent.IsVisible = true;
                lanesHost.LaneAHost.Children.Add(thumbContent);
            }
            if (waveContent != null)
            {
                laneHolder.Children.Remove(waveContent);
                waveContent.IsVisible = true;
                lanesHost.LaneBHost.Children.Add(waveContent);
            }
            laneHolder.IsVisible = false;
        }

        var phase3Lanes = this.FindControl<FortniteVideoSoftware.App.Controls.TimelineLanesControl>("Phase3Lanes");
        if (phase3Lanes != null)
        {
            phase3Lanes.LaneASeekable = true;
            phase3Lanes.LaneBSeekable = true;
            phase3Lanes.SeekRequested += videoRelativeSec =>
            {
                bool wasPlaying = _isPreviewPlaying;
                StopPreview();
                _previewCurrentOffset = _songStartSeconds + videoRelativeSec;
                SeekPhase3VideoHost(videoRelativeSec, forcePause: !wasPlaying);
                if (wasPlaying) StartPreviewInternal(_previewCurrentOffset);
                else UpdatePlayhead();
            };
        }

        var phase3WaveformClip = this.FindControl<Canvas>("Phase3WaveformClip");
        if (phase3WaveformClip != null)
        {
            phase3WaveformClip.SizeChanged += (s, e) => UpdatePhase3WaveformLaneWidth();
        }


        var videoVolSlider = this.FindControl<Slider>("VideoVolSlider");

        if (videoVolSlider != null)

        {

            videoVolSlider.PropertyChanged += (s, e) =>

            {

                if (e.Property == Slider.ValueProperty)

                {

                    var lbl = this.FindControl<TextBlock>("VideoVolLabel");

                    if (lbl != null) lbl.Text = $"Video {videoVolSlider.Value:0}%";


                    if (_currentStep == 3)

                    {

                        var wizardVideoHost = WizardVideoHost;

                        if (wizardVideoHost?.IpcClient != null)

                            _ = wizardVideoHost.IpcClient.SetPropertyDoubleAsync("volume", GetPreviewVideoVolume());

                        SaveWizardVolumes();
                        UpdateProblemFlags();

                    }

                }

            };

        }


        var musicVolSlider = this.FindControl<Slider>("MusicVolSlider");

        if (musicVolSlider != null)

        {

            musicVolSlider.PropertyChanged += (s, e) =>

            {

                if (e.Property == Slider.ValueProperty)

                {

                    var lbl = this.FindControl<TextBlock>("MusicVolLabel");

                    if (lbl != null) lbl.Text = $"Music {musicVolSlider.Value:0}%";


                    if (_audioIpcClient != null)

                    {

                        ApplyPreviewMusicVolume();
                        
                        SaveWizardVolumes();
                        UpdateProblemFlags();

                    }

                }

            };

        }


        var playBtn = this.FindControl<Button>("PlayBtn");

        if (playBtn != null)

        {

            playBtn.Click += (s, e) => TogglePreview();

        }


        var skipBackBtn = this.FindControl<Button>("SkipBackBtn");

        if (skipBackBtn != null) skipBackBtn.Click += (s, e) => SkipPreview(-30);


        var skipForwardBtn = this.FindControl<Button>("SkipForwardBtn");

        if (skipForwardBtn != null) skipForwardBtn.Click += (s, e) => SkipPreview(30);


        var nextBtn = this.FindControl<Button>("NextBtn");

        if (nextBtn != null) nextBtn.Click += (s, e) =>

        {

            RuntimeLog.Info("UI", "User clicked Next in Music Wizard.");

            OnNextClicked(s, e);

        };


        var backBtn = this.FindControl<Button>("BackBtn");

        if (backBtn != null) backBtn.Click += (s, e) =>

        {

            RuntimeLog.Info("UI", "User clicked Back in Music Wizard.");

            OnBackClicked(s, e);

        };


        var confirmCancelBtn = this.FindControl<Button>("ConfirmCancelBtn");
        if (confirmCancelBtn != null) confirmCancelBtn.Click += (s, e) =>
        {
            var btn = this.FindControl<Button>("CancelBtn");
            btn?.Flyout?.Hide();
            RuntimeLog.Info("UI", "User confirmed Cancel in Music Wizard.");
            StopPreview();
            Close();
        };


        UpdateNextButtonState();
        UpdatePreviewControlsState();
        UpdateDuckingCompareButton();
        UpdateProblemFlags();
        AttachTitleBarDrag();
    }
    private void InitializeComponent()

    {

        AvaloniaXamlLoader.Load(this);

    }


    private void UpdateStepProgress()

    {

        var dots = new[] {

            (this.FindControl<Avalonia.Controls.Border>("Step1Dot"),

             this.FindControl<TextBlock>("Step1Icon"),

             this.FindControl<TextBlock>("Step1Label")),

            (this.FindControl<Avalonia.Controls.Border>("Step2Dot"),

             this.FindControl<TextBlock>("Step2Icon"),

             this.FindControl<TextBlock>("Step2Label")),

            (this.FindControl<Avalonia.Controls.Border>("Step3Dot"),

             this.FindControl<TextBlock>("Step3Icon"),

             this.FindControl<TextBlock>("Step3Label")),

        };


        for (int i = 0; i < 3; i++)

        {

            if (dots[i].Item1 == null || dots[i].Item2 == null || dots[i].Item3 == null) continue;

            if (i < _currentStep - 1)

            {


                dots[i].Item1!.Background = Infrastructure.ThemeResources.Brush(this, "AppSuccessBrush", Avalonia.Media.Brush.Parse("#3f9c6b"));   // TONE_01

                dots[i].Item2!.Text = "✓";

                dots[i].Item2!.Foreground = Avalonia.Media.Brushes.White;

                dots[i].Item3!.Foreground = Avalonia.Media.Brush.Parse("#94a3b8");

            }

            else if (i == _currentStep - 1)

            {


                dots[i].Item1!.Background = Avalonia.Media.Brush.Parse("#3b82f6");

                dots[i].Item2!.Text = (i + 1).ToString();

                dots[i].Item2!.Foreground = Avalonia.Media.Brushes.White;

                dots[i].Item3!.Foreground = Avalonia.Media.Brush.Parse("#60a5fa");

                dots[i].Item3!.FontWeight = Avalonia.Media.FontWeight.Bold;

            }

            else

            {


                dots[i].Item1!.Background = Avalonia.Media.Brush.Parse("#334155");

                dots[i].Item2!.Text = (i + 1).ToString();

                dots[i].Item2!.Foreground = Avalonia.Media.Brush.Parse("#94a3b8");

                dots[i].Item3!.Foreground = Avalonia.Media.Brush.Parse("#94a3b8");

                dots[i].Item3!.FontWeight = Avalonia.Media.FontWeight.Normal;

            }

        }

    }


    private void UpdateStepVisibility()
    {
        this.FindControl<Grid>("Step1Panel")!.IsVisible = _currentStep == 1;
        this.FindControl<Control>("Step2Panel")!.IsVisible = _currentStep == 2;
        this.FindControl<Grid>("Step3Panel")!.IsVisible = _currentStep == 3;

        // COVER_02 — the coverage block appears WHEN IT HAS A JOB, on either app.
        UpdateCoverageHelperVisibility();

        var backBtn = this.FindControl<Button>("BackBtn");

        if (backBtn != null) backBtn.IsEnabled = _currentStep > 1;


        var nextBtn = this.FindControl<Button>("NextBtn");

        if (nextBtn != null)

        {

            nextBtn.Content = _currentStep == 3 ? "APPLY" : "NEXT";

        }


        UpdateFinalPlacementSummary();
        UpdateProblemFlags();
        UpdateDuckingCompareButton();
        UpdateStepProgress();
        UpdatePreviewControlsState();
        
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (this.Content is Avalonia.Controls.Control contentControl)
            {
                contentControl.InvalidateMeasure();
                contentControl.InvalidateArrange();
            }
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }


    private void OnTrackSelected(MusicTrackItem? track)
    {
        _selectedTrack = track;
        System.Threading.Interlocked.Increment(ref _waveformRenderVersion);
        _phase1UserSeeked = false;              // PREVIEW1_01
        _coverageAcceptedKey = null;            // COVER_01 — a new song is a new question
        if (_currentStep == 1) ScheduleAutoPreview(track);
        ResetAutoFillQueueState();
        UpdateNextButtonState();
        UpdateFinalPlacementSummary();
        UpdatePreviewControlsState();
        UpdateCoverageBar();
        UpdateProblemFlags();
        SetSmartFitStatus("");
    }

    private void OnMusicSearchKeyDown(object? sender, KeyEventArgs e)
    {
        var listbox = this.FindControl<ListBox>("MusicListBox");
        if (listbox == null) return;

        if (e.Key == Key.Escape)
        {
            if (sender is TextBox searchBox)
                searchBox.Text = string.Empty;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down || e.Key == Key.Up)
        {
            if (AvailableTracks.Count == 0) return;
            int currentIndex = listbox.SelectedIndex;
            int nextIndex = e.Key == Key.Down
                ? Math.Min(AvailableTracks.Count - 1, currentIndex + 1)
                : Math.Max(0, currentIndex <= 0 ? 0 : currentIndex - 1);
            listbox.SelectedIndex = nextIndex;
            if (listbox.SelectedItem != null)
                listbox.ScrollIntoView(listbox.SelectedItem);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (listbox.SelectedItem == null && AvailableTracks.Count > 0)
                listbox.SelectedIndex = 0;
            if (listbox.SelectedItem != null && _currentStep == 1)
                OnNextClicked(listbox, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void ApplyTrackFilterAndSort()
    {
        string selectedPath = _selectedTrack?.FilePath ?? string.Empty;
        var visible = SortTracks(_allTracks.Where(t => TrackMatchesSearch(t, _musicSearchText))).ToList();

        AvailableTracks.Clear();
        foreach (var track in visible)
            AvailableTracks.Add(track);

        RecalculateTrackNameColumnWidth();   // LIST_02 — the visible set decides the widest title

        var listbox = this.FindControl<ListBox>("MusicListBox");
        if (listbox != null)
        {
            var selectedVisibleTrack = visible.FirstOrDefault(t =>
                string.Equals(t.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (selectedVisibleTrack != null)
            {
                listbox.SelectedItem = selectedVisibleTrack;
            }
            else if (!string.IsNullOrEmpty(selectedPath))
            {
                listbox.SelectedItem = null;
                OnTrackSelected(null);
            }
        }

        UpdateMusicEmptyState();
        UpdateMusicResultCount();
    }

    private System.Collections.Generic.IEnumerable<MusicTrackItem> SortTracks(System.Collections.Generic.IEnumerable<MusicTrackItem> tracks)
    {
        return _musicSortMode switch
        {
            "Newest" => tracks
                .OrderByDescending(t => t.IsRecent)
                .ThenByDescending(t => t.LastModifiedTicks)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
            "Shortest" => tracks
                .OrderByDescending(t => t.IsRecent)
                .ThenBy(t => t.DurationSec <= 0 ? double.MaxValue : t.DurationSec)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
            "Longest" => tracks
                .OrderByDescending(t => t.IsRecent)
                .ThenByDescending(t => t.DurationSec)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
            _ => tracks
                .OrderByDescending(t => t.IsRecent)
                .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static bool TrackMatchesSearch(MusicTrackItem track, string rawQuery)
    {
        string query = NormalizeSearchQuery(rawQuery);
        if (query.Length == 0)
            return true;

        return ContainsIgnoreCase(track.Title, query)
            || ContainsIgnoreCase(Path.GetFileNameWithoutExtension(track.Name), query)
            || ContainsIgnoreCase(track.Name, query)
            || ContainsIgnoreCase(track.Artist, query)
            || ContainsIgnoreCase(track.Album, query);
    }

    private static string NormalizeSearchQuery(string query)
    {
        return (query ?? string.Empty).Trim().Replace("*", string.Empty, StringComparison.Ordinal);
    }

    private static bool ContainsIgnoreCase(string source, string query)
    {
        return !string.IsNullOrEmpty(source) &&
            source.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateMusicResultCount()
    {
        var countText = this.FindControl<TextBlock>("MusicResultCountText");
        if (countText == null) return;

        if (_allTracks.Count == 0)
        {
            countText.Text = "0 songs";
        }
        else if (AvailableTracks.Count == _allTracks.Count)
        {
            countText.Text = $"{_allTracks.Count} songs";
        }
        else
        {
            countText.Text = $"{AvailableTracks.Count} of {_allTracks.Count} songs";
        }
    }

    private System.Collections.Generic.List<MusicTrackItem> GetAutoFillSourceTracks()
    {
        var source = _autoFillUseVisibleTracks ? AvailableTracks : SortTracks(_allTracks);
        return source
            .Where(t => !string.IsNullOrWhiteSpace(t.FilePath) && File.Exists(t.FilePath))
            .ToList();
    }

    private void SetSmartFitStatus(string message, bool isWarning = false)
    {
        var status = this.FindControl<TextBlock>("SmartFitStatusText");
        if (status == null) return;

        status.Text = message;
        status.Foreground = isWarning
            ? Avalonia.Media.Brushes.Orange
            : Avalonia.Media.Brushes.LightGreen;
    }

    private void CancelAudioAnalysis()
    {
        try { _audioAnalysisCts?.Cancel(); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        try { _audioAnalysisCts?.Dispose(); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        _audioAnalysisCts = null;
    }

    private void CancelMusicScan()
    {
        Interlocked.Increment(ref _musicScanVersion);
        try { _musicScanCts?.Cancel(); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        try { _musicScanCts?.Dispose(); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        _musicScanCts = null;
    }

    private async Task<AudioEnergyAnalysis?> AnalyzeAudioEnergyAsync(string audioPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            return null;

        Process? process = null;
        try
        {
            var peakArgs = new[]
            {
                "-nostdin", "-hide_banner", "-loglevel", "error",
                "-i", audioPath,
                "-vn", "-ac", "1", "-ar", "1000", "-f", "s16le", "pipe:1"
            };

            var psi = new ProcessStartInfo
            {
                FileName = ResolveFfmpegPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string arg in peakArgs) psi.ArgumentList.Add(arg);

            process = Process.Start(psi);
            using var _processCleanup = process;
            if (process == null) return null;
            ChildProcessTracker.AddProcess(process);

            using var audioBytes = new MemoryStream();
            Task copyTask = process.StandardOutput.BaseStream.CopyToAsync(audioBytes, cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await copyTask;
            _ = await errorTask;

            if (process.ExitCode != 0)
                return null;

            byte[] data = audioBytes.ToArray();
            const int sampleRate = 1000;
            const double bucketSeconds = 0.05;
            int sampleCount = data.Length / 2;
            if (sampleCount <= 0)
                return null;

            int bucketSize = Math.Max(1, (int)Math.Round(sampleRate * bucketSeconds));
            int bucketCount = Math.Max(1, (int)Math.Ceiling(sampleCount / (double)bucketSize));
            var energy = new double[bucketCount];

            for (int bucket = 0; bucket < bucketCount; bucket++)
            {
                int start = bucket * bucketSize;
                int end = Math.Min(sampleCount, start + bucketSize);
                if (end <= start) continue;

                double sum = 0;
                for (int sampleIndex = start; sampleIndex < end; sampleIndex++)
                {
                    int byteIndex = sampleIndex * 2;
                    short sample = BitConverter.ToInt16(data, byteIndex);
                    sum += Math.Abs((int)sample) / 32768.0;
                }

                energy[bucket] = sum / (end - start);
            }

            double maxEnergy = energy.Length > 0 ? energy.Max() : 0.0;
            if (maxEnergy > 0.000001)
            {
                for (int i = 0; i < energy.Length; i++)
                    energy[i] /= maxEnergy;
            }

            double mean = energy.Length > 0 ? energy.Average() : 0.0;
            double variance = energy.Length > 0
                ? energy.Select(v => (v - mean) * (v - mean)).Average()
                : 0.0;
            double std = Math.Sqrt(variance);
            double threshold = Math.Max(mean + std * 0.65, 0.28);
            int minPeakSpacingBuckets = Math.Max(1, (int)Math.Round(0.28 / bucketSeconds));

            var peakIndexes = new System.Collections.Generic.List<int>();
            for (int i = 2; i < energy.Length - 2; i++)
            {
                if (energy[i] < threshold) continue;
                if (energy[i] < energy[i - 1] || energy[i] < energy[i + 1]) continue;

                if (peakIndexes.Count > 0 && i - peakIndexes[^1] < minPeakSpacingBuckets)
                {
                    if (energy[i] > energy[peakIndexes[^1]])
                        peakIndexes[^1] = i;
                }
                else
                {
                    peakIndexes.Add(i);
                }
            }

            return new AudioEnergyAnalysis
            {
                BucketSeconds = bucketSeconds,
                DurationSeconds = sampleCount / (double)sampleRate,
                Energy = energy,
                PeakTimesSeconds = peakIndexes.Select(i => i * bucketSeconds).ToList()
            };
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (process != null && !process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Audio energy analysis failed: {ex.Message}");
            return null;
        }
        finally
        {
            try { process?.Dispose(); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        }
    }

    private static double? FindNearestPeakTime(AudioEnergyAnalysis analysis, double targetSeconds, double radiusSeconds)
    {
        double? best = null;
        double bestDistance = double.MaxValue;

        foreach (double peak in analysis.PeakTimesSeconds)
        {
            double distance = Math.Abs(peak - targetSeconds);
            if (distance <= radiusSeconds && distance < bestDistance)
            {
                best = peak;
                bestDistance = distance;
            }
        }

        return best;
    }

    private double FindSmartFitStart(AudioEnergyAnalysis analysis, double videoDurationSeconds)
    {
        double trackDuration = _trackDuration > 0 ? _trackDuration : analysis.DurationSeconds;
        if (trackDuration <= 0 || analysis.Energy.Length == 0)
            return 0.0;

        double usableWindowSeconds = Math.Min(videoDurationSeconds, trackDuration);
        double maxStartSeconds = Math.Max(0.0, trackDuration - usableWindowSeconds);
        if (maxStartSeconds <= 0.01)
            return 0.0;

        double bucketSeconds = analysis.BucketSeconds;
        int windowBuckets = Math.Max(1, Math.Min(analysis.Energy.Length, (int)Math.Round(usableWindowSeconds / bucketSeconds)));
        int maxStartBucket = Math.Min(analysis.Energy.Length - 1, (int)Math.Floor(maxStartSeconds / bucketSeconds));
        int stepBuckets = Math.Max(1, (int)Math.Round(0.10 / bucketSeconds));
        int earlyBuckets = Math.Max(1, (int)Math.Round(Math.Min(12.0, Math.Max(1.0, usableWindowSeconds * 0.25)) / bucketSeconds));

        var prefix = new double[analysis.Energy.Length + 1];
        for (int i = 0; i < analysis.Energy.Length; i++)
            prefix[i + 1] = prefix[i] + analysis.Energy[i];

        int bestBucket = 0;
        double bestScore = double.NegativeInfinity;

        for (int start = 0; start <= maxStartBucket; start += stepBuckets)
        {
            int end = Math.Min(analysis.Energy.Length, start + windowBuckets);
            if (end <= start) continue;

            int earlyEnd = Math.Min(end, start + earlyBuckets);
            double fullAverage = (prefix[end] - prefix[start]) / (end - start);
            double earlyAverage = (prefix[earlyEnd] - prefix[start]) / Math.Max(1, earlyEnd - start);
            double score = fullAverage * 0.65 + earlyAverage * 0.35;

            if (start < (int)Math.Round(1.0 / bucketSeconds) && earlyAverage < 0.04)
                score -= 0.05;

            if (score > bestScore)
            {
                bestScore = score;
                bestBucket = start;
            }
        }

        double startSeconds = bestBucket * bucketSeconds;
        double? snapped = FindNearestPeakTime(analysis, startSeconds, 1.0);
        return Math.Clamp(snapped ?? startSeconds, 0.0, maxStartSeconds);
    }

    private void ApplySongStartSeconds(double startSeconds, string statusMessage)
    {
        bool wasPlaying = _isPreviewPlaying;
        if (wasPlaying) StopPreview();

        _songStartSeconds = Math.Clamp(startSeconds, 0, Math.Max(0, _trackDuration - 0.01));

        // PREVIEW_04 — the cached loudness belongs to the OLD song-start window. Moving the start
        // moves the material, so the measurement has to be retaken or the preview balance is stale.
        if (_selectedTrack?.FilePath is string movedPath) _musicSegmentLufs.Remove(movedPath);
        ResetAutoFillQueueState();
        _previewCurrentOffset = _songStartSeconds;

        var lbl = this.FindControl<TextBlock>("OffsetLabel");
        if (lbl != null) lbl.Text = $"Song begins at {FormatSeconds(_songStartSeconds)}";

        DrawTimelineScale();
        DrawPhase3TimelineScale();
        UpdateFinalPlacementSummary();
        UpdateCoverageBar();
        UpdateProblemFlags();
        UpdatePlayhead();
        SetSmartFitStatus(statusMessage);

        if (wasPlaying)
            StartPreviewInternal(_previewCurrentOffset);
    }

    private async Task SnapSongStartToBeatAsync(Button button)
    {
        if (_selectedTrack == null || !File.Exists(_selectedTrack.FilePath))
        {
            ShowToast("Select a music track first.");
            return;
        }

        CancelAudioAnalysis();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _audioAnalysisCts = cts;
        button.IsEnabled = false;
        button.Opacity = 0.5;
        SetSmartFitStatus("Finding nearby beat...");

        try
        {
            var analysis = await AnalyzeAudioEnergyAsync(_selectedTrack.FilePath, cts.Token);
            if (analysis == null || analysis.PeakTimesSeconds.Count == 0)
            {
                SetSmartFitStatus("No clear beat found.", isWarning: true);
                return;
            }

            double radius = _songStartSeconds <= 0.05 ? 15.0 : 2.0;
            double? beatTime = FindNearestPeakTime(analysis, _songStartSeconds, radius);
            if (!beatTime.HasValue && _songStartSeconds <= 0.05)
            {
                foreach (double peakTime in analysis.PeakTimesSeconds)
                {
                    if (peakTime <= Math.Min(30.0, _trackDuration))
                    {
                        beatTime = peakTime;
                        break;
                    }
                }
            }

            if (!beatTime.HasValue)
            {
                SetSmartFitStatus("No strong beat near this point.", isWarning: true);
                return;
            }

            ApplySongStartSeconds(beatTime.Value, $"Snapped to {FormatSeconds(beatTime.Value)}.");
        }
        catch (OperationCanceledException)
        {
            SetSmartFitStatus("Beat scan timed out.", isWarning: true);
        }
        finally
        {
            if (ReferenceEquals(_audioAnalysisCts, cts))
                _audioAnalysisCts = null;
            cts.Dispose();
            button.IsEnabled = true;
            button.Opacity = 1.0;
        }
    }

    private async Task ApplySmartFitAsync(Button button)
    {
        if (_selectedTrack == null || !File.Exists(_selectedTrack.FilePath))
        {
            ShowToast("Select a music track first.");
            return;
        }

        CancelAudioAnalysis();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _audioAnalysisCts = cts;
        button.IsEnabled = false;
        button.Opacity = 0.5;
        SetSmartFitStatus("Finding a strong section...");

        try
        {
            var analysis = await AnalyzeAudioEnergyAsync(_selectedTrack.FilePath, cts.Token);
            double videoDuration = GetPhase3VideoDurationSeconds();
            double smartStart = analysis != null
                ? FindSmartFitStart(analysis, videoDuration)
                : 0.0;

            var musicVolSlider = this.FindControl<Slider>("MusicVolSlider");
            if (musicVolSlider != null && Math.Abs(musicVolSlider.Value - 100.0) < 0.5)
                musicVolSlider.Value = 85.0;

            ApplySongStartSeconds(smartStart, $"Smart Fit picked {FormatSeconds(smartStart)}.");

            if (GetQueuedMusicCoverageSeconds() < videoDuration - 0.5)   // COVER_01 — both modes
                BuildAutoFillQueue();

            UpdateDuckingCompareButton();
            ApplyPreviewMusicVolume();
            UpdateProblemFlags();
        }
        catch (OperationCanceledException)
        {
            SetSmartFitStatus("Smart Fit scan timed out.", isWarning: true);
        }
        finally
        {
            if (ReferenceEquals(_audioAnalysisCts, cts))
                _audioAnalysisCts = null;
            cts.Dispose();
            button.IsEnabled = true;
            button.Opacity = 1.0;
        }
    }


    // ══════════════════════════════════════════════════════════════════════════════════════
    // ANALYSIS_01 — ONE DECODE, FOUR FEATURES.
    //
    // Snap To Beat, Smart Fit, Fit By End Of Video and the phase-1 auto-preview all need the same
    // thing: the song's loudness over time. Each was (or would have been) spawning its own ffmpeg
    // and decoding the whole file again — several seconds of work, repeated, for a result that
    // cannot change. Cached by path for the life of the window.
    // ══════════════════════════════════════════════════════════════════════════════════════
    private readonly System.Collections.Generic.Dictionary<string, AudioEnergyAnalysis> _energyCache =
        new(StringComparer.OrdinalIgnoreCase);

    private async Task<AudioEnergyAnalysis?> GetTrackEnergyAsync(string audioPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(audioPath)) return null;
        if (_energyCache.TryGetValue(audioPath, out var cached)) return cached;

        var analysis = await AnalyzeAudioEnergyAsync(audioPath, cancellationToken);
        if (analysis != null) _energyCache[audioPath] = analysis;
        return analysis;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // COVER_01 — "YOUR SONG IS SHORTER THAN YOUR VIDEO" IS NOW ASKED, NOT MURMURED.
    //
    // What used to happen: you picked a 2-minute song for a 5-minute video, sailed through the
    // wizard, and the only mention of it was a grey sentence on the last screen —
    // "Music ends 3:00 before the video ends." — sitting next to no control that could fix it,
    // because the loop switch and the auto-fill button were hidden unless you had come in from the
    // Video Merger. A warning you cannot act on is just an accusation.
    //
    // Now the wizard measures the gap and asks, at the two moments the answer matters: when you
    // arrive at the start-point screen, and again when you commit. Three ways out, and all three
    // are real:
    //   ADD MORE SONGS   queues further tracks until the video is covered (the old merger-only
    //                    Auto-Fill, now available everywhere).
    //   CHANGE START     closes the question and leaves you on the start-point screen, where
    //                    dragging the start earlier may cover the video on its own.
    //   PROCEED ANYWAY   accept the silence. Recorded, so it is not asked again for this choice.
    //
    // Closing the dialog (X or Escape) means CHANGE START — the safe reading.
    // ══════════════════════════════════════════════════════════════════════════════════════
    private enum MusicCoverageChoice { ChangeStart, AddMoreSongs, ProceedAnyway }

    /// <summary>COVER_01 — the track+start the user last said "proceed anyway" to, so it is asked once.</summary>
    private string? _coverageAcceptedKey;

    private string BuildCoverageKey()
        => $"{_selectedTrack?.FilePath ?? ""}|{_songStartSeconds:F2}|{_pendingAutoFillMusicPaths.Count}";

    /// <summary>
    /// COVER_01 — how many seconds of the finished video would have no music, or 0 when covered.
    /// Looping covers everything by definition, so it always returns 0.
    /// </summary>
    private double GetMusicShortfallSeconds()
    {
        if (_selectedTrack == null) return 0.0;
        if (IsPhase3LoopMusicEnabled()) return 0.0;

        double videoDuration = GetPhase3VideoDurationSeconds();
        if (videoDuration <= 0.1) return 0.0;

        double covered;
        if (_pendingAutoFillMusicPaths.Count > 0)
        {
            covered = GetQueuedMusicCoverageSeconds();
        }
        else
        {
            double trackLength = _trackDuration > 0 ? _trackDuration : _selectedTrack.DurationSec;
            if (trackLength <= 0.01) return 0.0;   // length not known yet — do not guess
            covered = Math.Max(0.0, trackLength - _songStartSeconds);
        }

        double shortfall = videoDuration - covered;
        return shortfall > 0.5 ? shortfall : 0.0;
    }

    /// <summary>
    /// COVER_01 — asks the coverage question. Returns TRUE when the caller may carry on, FALSE
    /// when the user asked to stay and change the start point.
    /// </summary>
    private async Task<bool> WarnIfMusicTooShortAsync(bool askEvenIfAlreadyAccepted)
    {
        double shortfall = GetMusicShortfallSeconds();
        if (shortfall <= 0.0) return true;

        string key = BuildCoverageKey();
        if (!askEvenIfAlreadyAccepted && string.Equals(_coverageAcceptedKey, key, StringComparison.Ordinal))
            return true;

        double videoDuration = GetPhase3VideoDurationSeconds();
        double covered = Math.Max(0.0, videoDuration - shortfall);

        string message =
            "The song you picked is not long enough to cover your whole video.\n\n" +
            $"Your video is {FormatSeconds(videoDuration)} long.\n" +
            $"The music covers {FormatSeconds(covered)} of it.\n" +
            $"The last {FormatSeconds(shortfall)} would play in silence.\n\n" +
            "ADD MORE SONGS lines up more tracks from your music folder until the whole video is covered.\n" +
            "CHANGE START takes you back so you can start the song earlier, which may be all it needs.\n" +
            "PROCEED ANYWAY keeps it as it is and leaves the ending silent.\n\n" +
            "There is also a \"Loop music until video ends\" tick box on this screen, which repeats the song instead.";

        var dlg = new FortniteVideoSoftware.App.Controls.ConfirmDialogWindow();
        dlg.SetTitle("The music will run out before the video does");
        dlg.SetMessage(message);
        dlg.SetButtonText("ADD MORE SONGS", "CHANGE START", "PROCEED ANYWAY");
        // Green on the option that actually solves it; blue on the one that sends you back to try;
        // grey on the one that accepts the silence. No red — nothing here destroys anything.
        dlg.SetButtonClasses("Success", "Primary", "Secondary");

        try
        {
            await dlg.ShowDialog(this);
        }
        catch (Exception ex)
        {
            // A question that cannot be asked must not silently become "proceed".
            RuntimeLog.Fail("MUSIC_WIZARD", $"Coverage prompt failed, staying on this step: {ex.Message}");
            return false;
        }

        MusicCoverageChoice choice = dlg.DialogResult switch
        {
            FortniteVideoSoftware.App.Controls.ConfirmDialogWindow.ConfirmDialogResult.Yes => MusicCoverageChoice.AddMoreSongs,
            FortniteVideoSoftware.App.Controls.ConfirmDialogWindow.ConfirmDialogResult.Alt => MusicCoverageChoice.ProceedAnyway,
            _ => MusicCoverageChoice.ChangeStart
        };

        RuntimeLog.Info("MUSIC_WIZARD",
            $"Coverage gap of {shortfall:F1}s on a {videoDuration:F1}s video — user chose {choice}.");

        switch (choice)
        {
            case MusicCoverageChoice.AddMoreSongs:
                BuildAutoFillQueue();
                // Auto-Fill may still fall short if the folder has too little music. Say so rather
                // than pretending it worked, but do not block: the queue IS better than before.
                double remaining = GetMusicShortfallSeconds();
                if (remaining > 0.5)
                {
                    SetSmartFitStatus($"Still {FormatSeconds(remaining)} short — add more songs to your music folder, or tick Loop.", isWarning: true);
                    _coverageAcceptedKey = BuildCoverageKey();
                }
                return true;

            case MusicCoverageChoice.ProceedAnyway:
                _coverageAcceptedKey = key;
                return true;

            default:
                return false;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // PREVIEW1_01 — HEARING A SONG BEFORE COMMITTING TO IT.
    //
    // Phase 1 was a list of file names with a dead transport bar underneath it: PlayBtn, -30s and
    // +30s were all disabled unless you had already reached phase 2. So auditioning meant select,
    // NEXT, wait for an ffprobe and a waveform render, listen, BACK, repeat. Five songs, five
    // round trips.
    //
    // Highlighting a song now just plays it, from the part worth hearing rather than from the
    // silence at the front, and eases in over a second so it does not detonate in your headphones.
    // The transport bar works in phase 1 too, and its PLAY button means what it says: from the
    // beginning, no fade.
    //
    // Debounced, because arrowing down a list of two hundred songs must not start two hundred
    // playbacks or spawn two hundred ffmpeg processes.
    // ══════════════════════════════════════════════════════════════════════════════════════
    // ══════════════════════════════════════════════════════════════════════════════════════
    // PREVIEW1_02 — THE ANALYSIS CAME OFF THE CRITICAL PATH.
    //
    // The first version decoded the whole song through ffmpeg to find its busiest passage, THEN
    // started playing. Correct, and far too slow to click through a folder with: every new song
    // cost a full decode before a single note came out, so the list felt frozen.
    //
    // Now the sound starts first. A song that has been auditioned before starts exactly on its
    // best passage, from cache. A song being heard for the first time starts at 40% of its
    // length — past the intro, in the body of almost any track — and the decode runs in the
    // BACKGROUND purely to cache the exact point for next time. It deliberately does NOT seek
    // when it lands: a preview that lurches sideways a second after you clicked is worse than one
    // that started slightly off the perfect spot.
    //
    // The debounce drops to 110 ms — still enough to stop a held arrow key launching a playback
    // per row, short enough to feel like a click.
    // ══════════════════════════════════════════════════════════════════════════════════════
    private const double AutoPreviewDebounceMs = 110.0;

    /// <summary>PREVIEW1_02 — where a never-heard song starts: past the intro, inside the body.</summary>
    private const double AutoPreviewBlindFraction = 0.40;
    private const double PreviewFadeInSeconds = 1.0;

    private DispatcherTimer? _autoPreviewTimer;
    private MusicTrackItem? _autoPreviewPendingTrack;
    private CancellationTokenSource? _autoPreviewCts;
    private readonly System.Collections.Generic.Dictionary<string, double> _autoPreviewStartCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>PREVIEW1_01 — when the current playback's 1-second ease-in began. Null = no fade.</summary>
    private DateTime? _previewFadeStartUtc;

    /// <summary>PREVIEW1_01 — true once the user has skipped in phase 1, so PLAY resumes instead of restarting.</summary>
    private bool _phase1UserSeeked;

    private void ScheduleAutoPreview(MusicTrackItem? track)
    {
        _autoPreviewPendingTrack = track;

        _autoPreviewTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutoPreviewDebounceMs) };
        _autoPreviewTimer.Stop();

        if (track == null || !File.Exists(track.FilePath)) return;

        _autoPreviewTimer.Tick -= AutoPreviewTimer_Tick;
        _autoPreviewTimer.Tick += AutoPreviewTimer_Tick;
        _autoPreviewTimer.Start();
    }

    private void AutoPreviewTimer_Tick(object? sender, EventArgs e)
    {
        _autoPreviewTimer?.Stop();
        var track = _autoPreviewPendingTrack;
        if (track == null || _currentStep != 1) return;
        if (!ReferenceEquals(track, _selectedTrack)) return;
        StartAutoPreview(track);
    }

    private void StartAutoPreview(MusicTrackItem track)
    {
        // Nothing here awaits anything: the sound has to start on this turn of the message loop.
        double startAt;
        bool knownExactly = _autoPreviewStartCache.TryGetValue(track.FilePath, out startAt);
        if (!knownExactly)
        {
            double length = track.DurationSec;
            startAt = length > 1.0 ? length * AutoPreviewBlindFraction : 0.0;
        }

        _previewCurrentOffset = startAt;
        _phase1UserSeeked = false;
        StartPreviewInternal(startAt, fadeIn: true);

        if (!knownExactly) _ = LearnAutoPreviewStartAsync(track);
    }

    /// <summary>
    /// PREVIEW1_02 — decodes the song in the background and remembers its best passage, so the
    /// NEXT time this track is highlighted it starts exactly there. Never touches playback: the
    /// preview the user is already listening to is left where it is.
    /// </summary>
    private async Task LearnAutoPreviewStartAsync(MusicTrackItem track)
    {
        try { _autoPreviewCts?.Cancel(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _autoPreviewCts = cts;

        try
        {
            var analysis = await GetTrackEnergyAsync(track.FilePath, cts.Token);
            if (analysis == null || cts.IsCancellationRequested) return;

            double length = track.DurationSec > 0 ? track.DurationSec : analysis.DurationSeconds;
            _autoPreviewStartCache[track.FilePath] = FindAutoPreviewStart(analysis, length);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RuntimeLog.Debug("MUSIC_WIZARD", $"Could not learn the best passage of '{Path.GetFileName(track.FilePath)}': {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_autoPreviewCts, cts)) _autoPreviewCts = null;
            cts.Dispose();
        }
    }

    /// <summary>
    /// PREVIEW1_01 — the "play me the good bit" point.
    ///
    /// Not the exact middle: the middle of a song is often a breakdown or a quiet bridge, and the
    /// front of one is usually an intro that tells you nothing. This takes the loudest sustained
    /// 12-second stretch within the middle 70% of the track, then nudges it onto the nearest beat
    /// so it starts on a hit rather than halfway through one.
    /// </summary>
    private double FindAutoPreviewStart(AudioEnergyAnalysis analysis, double trackDurationSeconds)
    {
        double length = trackDurationSeconds > 0 ? trackDurationSeconds : analysis.DurationSeconds;
        if (length <= 20.0 || analysis.Energy.Length == 0) return Math.Max(0, length * 0.35);

        double bucket = analysis.BucketSeconds;
        const double windowSeconds = 12.0;
        int windowBuckets = Math.Max(1, (int)Math.Round(windowSeconds / bucket));

        // Middle 70%: skip the intro and stop well before the outro.
        int firstBucket = (int)Math.Floor((length * 0.15) / bucket);
        int lastBucket = (int)Math.Floor((length * 0.85) / bucket) - windowBuckets;
        firstBucket = Math.Clamp(firstBucket, 0, Math.Max(0, analysis.Energy.Length - 1));
        lastBucket = Math.Clamp(lastBucket, firstBucket, Math.Max(firstBucket, analysis.Energy.Length - windowBuckets));

        var prefix = new double[analysis.Energy.Length + 1];
        for (int i = 0; i < analysis.Energy.Length; i++) prefix[i + 1] = prefix[i] + analysis.Energy[i];

        int step = Math.Max(1, (int)Math.Round(0.25 / bucket));
        int bestBucket = firstBucket;
        double bestAverage = double.NegativeInfinity;

        for (int start = firstBucket; start <= lastBucket; start += step)
        {
            int end = Math.Min(analysis.Energy.Length, start + windowBuckets);
            if (end <= start) continue;
            double average = (prefix[end] - prefix[start]) / (end - start);
            if (average > bestAverage)
            {
                bestAverage = average;
                bestBucket = start;
            }
        }

        double seconds = bestBucket * bucket;
        double? onBeat = FindNearestPeakTime(analysis, seconds, 1.0);
        return Math.Clamp(onBeat ?? seconds, 0.0, Math.Max(0.0, length - 5.0));
    }

    /// <summary>
    /// PREVIEW1_01 — the ease-in multiplier, 0 to 1. Applied inside GetPreviewMusicVolume so every
    /// path that sets the preview volume respects it without knowing it exists.
    /// </summary>
    private double CurrentPreviewFadeFactor()
    {
        if (_previewFadeStartUtc is not DateTime started) return 1.0;
        double elapsed = (DateTime.UtcNow - started).TotalSeconds;
        if (elapsed >= PreviewFadeInSeconds)
        {
            _previewFadeStartUtc = null;
            return 1.0;
        }
        double t = Math.Clamp(elapsed / PreviewFadeInSeconds, 0.0, 1.0);
        return t * t;   // ease-in: quiet for longer, then up — kinder than a straight ramp
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // FITEND_01 — LANDING THE END OF THE VIDEO ON THE END OF THE SONG.
    //
    // Smart Fit works forwards: find a strong section and start there. That leaves the ending to
    // chance, and a video that stops mid-verse feels unfinished however good the opening was. This
    // works backwards from where the song ACTUALLY finishes.
    //
    // "Actually finishes" is the whole trick. Subtracting the video length from the file's last
    // second lands you in the fade-out tail or the dead air a lot of mp3s carry — the video ends on
    // nothing. So the song's own average loudness is measured, and the ending is taken to be the
    // last moment the track was still at a third of that average. Below a third is a tail, not
    // music. A small cushion is kept after it so the final hit is not clipped off.
    // ══════════════════════════════════════════════════════════════════════════════════════
    private const double FitByEndTailCushionSeconds = 0.35;

    /// <summary>
    /// FITEND_01 — the song-start that makes the video finish on the song's last musical moment,
    /// or null when the song is too short to reach back that far.
    /// </summary>
    private double? FindFitByEndStart(AudioEnergyAnalysis analysis, double videoDurationSeconds, out double musicEndSeconds)
    {
        musicEndSeconds = 0.0;
        if (analysis.Energy.Length == 0) return null;

        double bucket = analysis.BucketSeconds;
        double trackDuration = _trackDuration > 0 ? _trackDuration : analysis.DurationSeconds;
        if (trackDuration <= 0.01) return null;

        // Average over the audible material only, so a long silent tail cannot drag the average
        // down and make the threshold meaningless.
        double sum = 0;
        int counted = 0;
        foreach (double value in analysis.Energy)
        {
            if (value <= 0.005) continue;
            sum += value;
            counted++;
        }
        if (counted == 0) return null;

        double average = sum / counted;
        double threshold = average / 3.0;

        int endBucket = -1;
        for (int i = analysis.Energy.Length - 1; i >= 0; i--)
        {
            if (analysis.Energy[i] >= threshold) { endBucket = i; break; }
        }
        if (endBucket < 0) return null;

        musicEndSeconds = Math.Clamp((endBucket + 1) * bucket + FitByEndTailCushionSeconds, 0.0, trackDuration);

        double start = musicEndSeconds - videoDurationSeconds;
        if (start < 0.0) return null;   // song is shorter than the video — caller explains why

        // Snap onto a beat, but only ever EARLIER: snapping later would push the song's ending past
        // the end of the video, which is the exact cut-off this feature exists to avoid.
        double? onBeat = null;
        double bestDistance = double.MaxValue;
        foreach (double peak in analysis.PeakTimesSeconds)
        {
            if (peak > start || peak < start - 1.0) continue;
            double distance = start - peak;
            if (distance < bestDistance) { bestDistance = distance; onBeat = peak; }
        }

        return Math.Clamp(onBeat ?? start, 0.0, Math.Max(0.0, trackDuration - 0.01));
    }

    private async Task ApplyFitByEndAsync(Button button)
    {
        if (_selectedTrack == null || !File.Exists(_selectedTrack.FilePath))
        {
            ShowToast("Select a music track first.");
            return;
        }

        CancelAudioAnalysis();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _audioAnalysisCts = cts;
        button.IsEnabled = false;
        button.Opacity = 0.5;
        SetSmartFitStatus("Finding where the song really ends...");

        try
        {
            var analysis = await GetTrackEnergyAsync(_selectedTrack.FilePath, cts.Token);
            if (analysis == null)
            {
                SetSmartFitStatus("Could not read this song.", isWarning: true);
                return;
            }

            double videoDuration = GetPhase3VideoDurationSeconds();
            double? start = FindFitByEndStart(analysis, videoDuration, out double musicEnd);

            if (start == null)
            {
                SetSmartFitStatus("This song is not long enough to end with the video.", isWarning: true);
                await WarnIfMusicTooShortAsync(askEvenIfAlreadyAccepted: true);
                return;
            }

            ApplySongStartSeconds(
                start.Value,
                $"Set to {FormatSeconds(start.Value)} — the song now finishes at {FormatSeconds(musicEnd)}, right as the video ends.");
            RuntimeLog.Info("MUSIC_WIZARD",
                $"Fit By End Of Video: music ends at {musicEnd:F2}s, video is {videoDuration:F2}s, song start set to {start.Value:F2}s.");
        }
        catch (OperationCanceledException)
        {
            SetSmartFitStatus("Scan timed out.", isWarning: true);
        }
        finally
        {
            if (ReferenceEquals(_audioAnalysisCts, cts)) _audioAnalysisCts = null;
            cts.Dispose();
            button.IsEnabled = true;
            button.Opacity = 1.0;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // KEYS_01 — the wizard had NO keyboard handling of any kind. Space is the universal
    // play/pause and this screen is about listening, so it is the one key worth having. Text
    // boxes keep their space bar, obviously. Arrows nudge the song start on the step where a
    // song start exists, and scrub on the step where there is a video to scrub.
    // ══════════════════════════════════════════════════════════════════════════════════════
    private void OnWizardKeyDown(object? sender, KeyEventArgs e)
    {
        var focused = Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is TextBox || focused is Avalonia.Controls.NumericUpDown) return;

        if (e.Key == Key.Space)
        {
            TogglePreview();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Left && e.Key != Key.Right) return;

        // Phase 1 belongs to the song list — arrows move the selection there.
        if (_currentStep == 1) return;

        double direction = e.Key == Key.Right ? 1.0 : -1.0;

        if (_currentStep == 2)
        {
            if (_selectedTrack == null || _trackDuration <= 0) return;
            double stepSeconds = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 5.0 : 0.5;
            double target = Math.Clamp(_songStartSeconds + direction * stepSeconds, 0, Math.Max(0, _trackDuration - 0.01));
            ApplySongStartSeconds(target, $"Song begins at {FormatSeconds(target)}.");
            e.Handled = true;
            return;
        }

        if (_currentStep == 3 && _phase3Ready)
        {
            SkipPreview(direction * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 30.0 : 5.0));
            e.Handled = true;
        }
    }

    private void BuildAutoFillQueue()
    {
        if (_selectedTrack == null)
            return;

        _pendingAutoFillMusicPaths.Clear();
        _pendingAutoFillMusicPaths.Add(_selectedTrack.FilePath);

        double targetDuration = GetPhase3VideoDurationSeconds();
        double coveredDuration = Math.Max(0, _selectedTrack.DurationSec - _songStartSeconds);
        foreach (var track in GetAutoFillSourceTracks().Where(t =>
            !string.Equals(t.FilePath, _selectedTrack.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            _pendingAutoFillMusicPaths.Add(track.FilePath);
            coveredDuration += Math.Max(1.0, track.DurationSec);
            if (coveredDuration >= targetDuration)
                break;
        }

        UpdateAutoFillQueuePreview();
        UpdateCoverageBar();
        UpdateFinalPlacementSummary();
        UpdateProblemFlags();
        DrawPhase3TimelineScale();

        var autoFillBtn = this.FindControl<Button>("AutoFillSongsBtn");
        if (autoFillBtn != null)
            autoFillBtn.Content = $"Auto-Filled {_pendingAutoFillMusicPaths.Count} Songs";
        ShowToastSuccess($"Auto-filled {_pendingAutoFillMusicPaths.Count} songs.");
    }

    private void ResetAutoFillQueueState()
    {
        _pendingAutoFillMusicPaths.Clear();
        AutoFillQueueItems.Clear();
        var queuePanel = this.FindControl<Grid>("AutoFillQueuePanel");
        if (queuePanel != null)
            queuePanel.IsVisible = false;
        var autoFillBtn = this.FindControl<Button>("AutoFillSongsBtn");
        if (autoFillBtn != null)
            autoFillBtn.Content = "Auto-Fill Remaining Time";
        UpdateAutoFillQueuePreview();
        UpdateProblemFlags();
    }

    private void UpdateAutoFillQueuePreview()
    {
        AutoFillQueueItems.Clear();

        var queuePanel = this.FindControl<Grid>("AutoFillQueuePanel");
        bool hasQueue = _pendingAutoFillMusicPaths.Count > 0;
        if (queuePanel != null)
            queuePanel.IsVisible = hasQueue;

        if (!hasQueue)
        {
            var remainingText = this.FindControl<TextBlock>("AutoFillRemainingText");
            if (remainingText != null)
                remainingText.Text = "";
            return;
        }

        double coveredDuration = 0.0;
        for (int i = 0; i < _pendingAutoFillMusicPaths.Count; i++)
        {
            string path = _pendingAutoFillMusicPaths[i];
            var track = FindTrackByPath(path);
            double offset = i == 0 ? _songStartSeconds : 0.0;
            double duration = Math.Max(0, (track?.DurationSec ?? 0.0) - offset);
            coveredDuration += duration;
            AutoFillQueueItems.Add(new MusicQueueItem
            {
                Order = i + 1,
                Name = track?.Name ?? Path.GetFileName(path),
                DurationText = duration > 0 ? FormatSeconds(duration) : "loading"
            });
        }

        double targetDuration = GetPhase3VideoDurationSeconds();
        double remaining = Math.Max(0, targetDuration - coveredDuration);
        var summaryText = this.FindControl<TextBlock>("AutoFillQueueSummaryText");
        if (summaryText != null)
            summaryText.Text = $"QUEUE: {AutoFillQueueItems.Count} song(s)";
        var uncoveredText = this.FindControl<TextBlock>("AutoFillRemainingText");
        if (uncoveredText != null)
            uncoveredText.Text = remaining <= 0.01
                ? "Coverage complete."
                : $"{FormatSeconds(remaining)} still uncovered.";
    }

    private void MoveQueuedTrack(int direction)
    {
        var queueList = this.FindControl<ListBox>("AutoFillQueueList");
        if (queueList == null || queueList.SelectedIndex <= 0)
        {
            ShowToast("Select an auto-fill song after the first track.");
            return;
        }

        int oldIndex = queueList.SelectedIndex;
        int newIndex = Math.Clamp(oldIndex + direction, 1, _pendingAutoFillMusicPaths.Count - 1);
        if (newIndex == oldIndex)
            return;

        string path = _pendingAutoFillMusicPaths[oldIndex];
        _pendingAutoFillMusicPaths.RemoveAt(oldIndex);
        _pendingAutoFillMusicPaths.Insert(newIndex, path);
        UpdateAutoFillQueuePreview();
        queueList.SelectedIndex = newIndex;
        UpdateCoverageBar();
        UpdateFinalPlacementSummary();
        UpdateProblemFlags();
        DrawPhase3TimelineScale();
    }

    private void RemoveSelectedQueuedTrack()
    {
        var queueList = this.FindControl<ListBox>("AutoFillQueueList");
        if (queueList == null || queueList.SelectedIndex <= 0)
        {
            ShowToast("Select an auto-fill song after the first track.");
            return;
        }

        int removedIndex = queueList.SelectedIndex;
        _pendingAutoFillMusicPaths.RemoveAt(removedIndex);
        UpdateAutoFillQueuePreview();
        if (_pendingAutoFillMusicPaths.Count > 1)
            queueList.SelectedIndex = Math.Min(removedIndex, _pendingAutoFillMusicPaths.Count - 1);
        UpdateCoverageBar();
        UpdateFinalPlacementSummary();
        UpdateProblemFlags();
        DrawPhase3TimelineScale();
    }

    private MusicTrackItem? FindTrackByPath(string path)
    {
        return _allTracks.FirstOrDefault(track =>
            string.Equals(track.FilePath, path, StringComparison.OrdinalIgnoreCase));
    }

    private void LoadRecentMusicPins()
    {
        try
        {
            var state = new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths).LoadSync();
            if (state["RecentMusicPaths"] is System.Text.Json.Nodes.JsonArray recentArray)
            {
                foreach (var node in recentArray)
                {
                    string? path = node?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(path))
                        _recentMusicPaths.Add(path);
                }
            }
        }
        catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
    }

    private void SaveRecentMusicPins(System.Collections.Generic.IEnumerable<string> selectedPaths)
    {
        try
        {
            var orderedPaths = selectedPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Concat(_recentMusicPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();

            _recentMusicPaths.Clear();
            foreach (string path in orderedPaths)
                _recentMusicPaths.Add(path);

            var recentArray = new System.Text.Json.Nodes.JsonArray();
            foreach (string path in orderedPaths)
                recentArray.Add(System.Text.Json.Nodes.JsonValue.Create(path));

            new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths)
                .UpdatePropertiesSync(new System.Text.Json.Nodes.JsonObject
                {
                    ["RecentMusicPaths"] = recentArray
                });
        }
        catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
    }

    private void HandleSongOffsetKeyDown(KeyEventArgs e)
    {
        double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 5.0 : 1.0;
        double maxStart = Math.Max(0, _trackDuration - 0.01);

        if (e.Key == Key.Left)
        {
            _songStartSeconds = Math.Clamp(_songStartSeconds - step, 0, maxStart);
        }
        else if (e.Key == Key.Right)
        {
            _songStartSeconds = Math.Clamp(_songStartSeconds + step, 0, maxStart);
        }
        else if (e.Key == Key.Home)
        {
            _songStartSeconds = 0;
        }
        else if (e.Key == Key.End)
        {
            _songStartSeconds = maxStart;
        }
        else
        {
            return;
        }

        ResetAutoFillQueueState();
        _previewCurrentOffset = _songStartSeconds;
        var lbl = this.FindControl<TextBlock>("OffsetLabel");
        if (lbl != null) lbl.Text = $"Song begins at {FormatSeconds(_songStartSeconds)}";
        UpdatePlayhead();
        UpdateFinalPlacementSummary();
        UpdateAutoFillQueuePreview();
        UpdateCoverageBar();
        UpdateProblemFlags();
        e.Handled = true;
    }

    private void SeekPhase3Relative(double videoRelativeSec)
    {
        double duration = GetPhase3VideoDurationSeconds();
        videoRelativeSec = Math.Clamp(videoRelativeSec, 0.0, duration);

        bool wasPlaying = _isPreviewPlaying;
        StopPreview();
        _previewCurrentOffset = _songStartSeconds + videoRelativeSec;
        SeekPhase3VideoHost(videoRelativeSec, forcePause: !wasPlaying);
        if (wasPlaying) StartPreviewInternal(_previewCurrentOffset);
        else UpdatePlayhead();
    }

    private void SeekPhase3VideoHost(double outputRelativeSec, bool forcePause)
    {
        var wizardVideoHost = WizardVideoHost;
        if (wizardVideoHost?.IpcClient == null) return;

        double sourceRelativeSec = MapPhase3OutputToSourceRelativeSeconds(outputRelativeSec);
        double sourceAbsSec = (_trimStartMs / 1000.0) + sourceRelativeSec;
        double speed = GetPhase3PreviewSpeedAtSourceRelativeSeconds(sourceRelativeSec);

        _ = wizardVideoHost.IpcClient.SetPropertyAsync("time-pos", sourceAbsSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _ = wizardVideoHost.IpcClient.SetPropertyAsync("speed", Math.Max(0.001, speed).ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture));
        if (forcePause || speed <= 0.001)
            _ = wizardVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
    }

    private void SyncPhase3VideoPreviewClock()
    {
        if (_currentStep != 3 || !_isPreviewPlaying) return;

        var wizardVideoHost = WizardVideoHost;
        if (wizardVideoHost?.IpcClient == null) return;

        double outputRelativeSec = GetCurrentPhase3VideoRelativeSeconds();
        double sourceRelativeSec = MapPhase3OutputToSourceRelativeSeconds(outputRelativeSec);
        double sourceAbsSec = (_trimStartMs / 1000.0) + sourceRelativeSec;
        double speed = GetPhase3PreviewSpeedAtSourceRelativeSeconds(sourceRelativeSec);

        if (speed <= 0.001)
        {
            _ = wizardVideoHost.IpcClient.SetPropertyAsync("time-pos", sourceAbsSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _ = wizardVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
            return;
        }

        _ = wizardVideoHost.IpcClient.SetPropertyAsync("speed", speed.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture));
        if (Math.Abs(wizardVideoHost.IpcClient.CurrentTime - sourceAbsSec) > 0.15)
            _ = wizardVideoHost.IpcClient.SetPropertyAsync("time-pos", sourceAbsSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _ = wizardVideoHost.IpcClient.SetPropertyAsync("pause", "no");
    }


    private void UpdateNextButtonState()

    {

        var nextBtn = this.FindControl<Button>("NextBtn");

        if (nextBtn == null) return;


        if (_currentStep == 1)

        {

            nextBtn.IsEnabled = _selectedTrack != null;

            if (_selectedTrack == null)

            {

                nextBtn.Opacity = 0.5;

                ToolTip.SetTip(nextBtn, "Please select a music track first");

            }

            else

            {

                nextBtn.Opacity = 1.0;

                ToolTip.SetTip(nextBtn, "Proceed to the next step");

            }

        }

        else if (_currentStep == 3)
        {
            nextBtn.IsEnabled = _phase3Ready;
            nextBtn.Opacity = _phase3Ready ? 1.0 : 0.5;
            ToolTip.SetTip(nextBtn, _phase3Ready ? "Apply music settings to your video" : "Wait for the final preview to finish loading");
        }
        else
        {
            nextBtn.IsEnabled = true;
            nextBtn.Opacity = 1.0;
            ToolTip.SetTip(nextBtn, "Proceed to the next step");
        }
    }


    private async void OnNextClicked(object? sender, RoutedEventArgs e)

    {

        if (_currentStep == 1)

        {

            if (_selectedTrack == null)

            {


                ShowToast("⚠ Please select a music track first!");

                return;

            }


            bool selectedTrackChanged = !string.Equals(_lastConfiguredTrackPath, _selectedTrack.FilePath, StringComparison.OrdinalIgnoreCase);

            var ffprobePath = ResolveFfprobePath();
            var prober = new FortniteVideoSoftware.Core.Media.MediaProber(ffprobePath, _selectedTrack.FilePath);
            double duration = await prober.GetDurationAsync();
            _trackDuration = Math.Max(1.0, duration > 0 ? duration : _selectedTrack.DurationSec);
            _selectedTrack.DurationSec = _trackDuration;

            if (selectedTrackChanged)
            {
                _songStartSeconds = 0;
                _lastLoadedTrackPath = null;
                _lastConfiguredTrackPath = _selectedTrack.FilePath;
            }
            _songStartSeconds = Math.Clamp(_songStartSeconds, 0, Math.Max(0, _trackDuration - 0.01));
            _previewCurrentOffset = _songStartSeconds;
            var lbl = this.FindControl<TextBlock>("OffsetLabel");
            if (lbl != null) lbl.Text = $"Song begins at {FormatSeconds(_songStartSeconds)}";


            Avalonia.Threading.Dispatcher.UIThread.Post(() => {

                DrawTimelineScale();

                UpdatePlayhead();

            });


            var selectedLabel = this.FindControl<TextBlock>("SelectedTrackLabel");

            if (selectedLabel != null) selectedLabel.Text = _selectedTrack.Name;


            _ = RenderWaveformAsync(_selectedTrack.FilePath);


            _currentStep = 2;

            // ══════════════════════════════════════════════════════════════════════════
            // COVER_01 — ASK AS SOON AS THE ANSWER IS KNOWABLE.
            //
            // The song's real length is probed a few lines above, and the video's finished length
            // (speed changes and freezes included) comes from GetPhase3VideoDurationSeconds, which
            // needs nothing from phase 3. So this is the earliest point at which "your song is too
            // short" is a fact rather than a guess — and it is the screen where all three answers
            // live, so the user is standing in front of the controls when asked.
            //
            // Deliberately fired AFTER the step switch and NOT awaited before it: the panels must
            // already be visible behind the dialog, or the user is answering a question about a
            // screen they have not seen.
            // ══════════════════════════════════════════════════════════════════════════
            UpdateStepVisibility();
            UpdateNextButtonState();
            UpdatePreviewControlsState();
            await WarnIfMusicTooShortAsync(askEvenIfAlreadyAccepted: false);
            return;
        }

        else if (_currentStep == 2)
        {
            // COVER_01 — the commit. Asked every time, because this is the last chance: FALSE means
            // the user chose CHANGE START, so stay on this step rather than walking them forward.
            if (!await WarnIfMusicTooShortAsync(askEvenIfAlreadyAccepted: false))
                return;

            StopPreview();
            CancelPhase3Load();
            _phase3Ready = false;
            _previewCurrentOffset = _songStartSeconds;
            _currentStep = 3;
            UpdateStepVisibility();
            UpdateNextButtonState();

            _phase3LoadCts = new CancellationTokenSource();
            int loadVersion = ++_phase3LoadVersion;
            await LoadPhase3DataAsync(_phase3LoadCts.Token, loadVersion);
            return;
        }
        else if (_currentStep == 3)
        {
            if (!_phase3Ready)
            {
                ShowToast("Final preview is still loading.");
                return;
            }

            var duckingCheck = this.FindControl<CheckBox>("DuckingCheckBox");
            var carvingCheck = this.FindControl<CheckBox>("CarvingCheckBox");
            bool audioProtection = Infrastructure.SettingsManager.Instance.Defaults.AudioProtection;
            var videoVolSlider = this.FindControl<Slider>("VideoVolSlider");
            var musicVolSlider = this.FindControl<Slider>("MusicVolSlider");
            double timelineStartSec = _trimStartMs / 1000.0;
            double timelineEndSec = timelineStartSec + GetPhase3SourceDurationSeconds();
            var resultMusicPaths = _pendingAutoFillMusicPaths.Count > 0
                ? new System.Collections.Generic.List<string>(_pendingAutoFillMusicPaths)
                : new System.Collections.Generic.List<string> { _selectedTrack?.FilePath ?? "" };

            Result = new MusicWizardResult
            {
                MusicFilePath = _selectedTrack?.FilePath ?? "",
                MusicFilePaths = resultMusicPaths,
                MusicDurationsSeconds = resultMusicPaths.Select(GetKnownTrackDurationSeconds).ToList(),
                OffsetSeconds = _songStartSeconds,
                TimelineStartSeconds = timelineStartSec,
                TimelineEndSeconds = timelineEndSec,
                EnableDucking = audioProtection && (duckingCheck?.IsChecked ?? true),
                EnableCarving = audioProtection && (carvingCheck?.IsChecked ?? true),
                VideoVolume = (videoVolSlider?.Value ?? 100.0) / 100.0,
                MusicVolume = (musicVolSlider?.Value ?? 100.0) / 100.0,
                MusicDurationSeconds = _trackDuration,
                LoopMusic = this.FindControl<CheckBox>("LoopMusicCheckBox")?.IsChecked ?? false
            };

            SaveRecentMusicPins(resultMusicPaths);
            RuntimeLog.Success("MUSIC_WIZARD", $"Wizard completed. Track: {Path.GetFileName(Result.MusicFilePath)}, SongStart: {Result.OffsetSeconds:F2}s, Timeline: {Result.TimelineStartSeconds:F2}-{Result.TimelineEndSeconds:F2}s, Ducking: {Result.EnableDucking}, Carving: {Result.EnableCarving}, VideoVol: {Result.VideoVolume}, MusicVol: {Result.MusicVolume}");
            RuntimeLog.Debug("MUSIC_WIZARD", $"Wizard completed track path: {Result.MusicFilePath}");
            _isSafeToClose = true;

            Close();

            return;

        }


        UpdateStepVisibility();

        UpdateNextButtonState();

    }

    private double GetKnownTrackDurationSeconds(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return 0.0;
        if (_selectedTrack != null &&
            string.Equals(_selectedTrack.FilePath, filePath, StringComparison.OrdinalIgnoreCase) &&
            _trackDuration > 0)
        {
            return _trackDuration;
        }

        var item = AvailableTracks.FirstOrDefault(track =>
            string.Equals(track.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        item ??= _allTracks.FirstOrDefault(track =>
            string.Equals(track.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        return item?.DurationSec > 0 ? item.DurationSec : 0.0;
    }


    /// <summary>
    /// LANES_01: delegates to the shared <see cref="ThumbnailStripGenerator"/>. The ~70 lines of
    /// FFmpeg tiling that used to live here were lifted out when the Granular Speed Editor needed
    /// the identical strip — two copies would have been free to drift on frame count, scaling and
    /// temp-file cleanup. Signature unchanged so every phase-3 call site is untouched.
    /// </summary>
    private async Task<string?> GenerateThumbnailsStripAsync(string ffmpegPath, string videoPath, double startSec, double durationSec, CancellationToken cancellationToken, int frames = ThumbnailStripGenerator.DefaultFrames)
        => await ThumbnailStripGenerator.GenerateAsync(
            ffmpegPath, videoPath, _paths.TempDirectory, startSec, durationSec, cancellationToken, frames, "MusicWizard");

    private async Task<string?> GeneratePhase3MusicSequenceWaveformAsync(
        string ffmpegPath,
        System.Collections.Generic.IReadOnlyList<Phase3MusicPreviewSegment> segments,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        string? tempPng = null;
        Process? process = null;
        try
        {
            var usableSegments = segments
                .Where(segment => segment.TimelineEndSec > segment.TimelineStartSec + 0.001 &&
                                  !string.IsNullOrWhiteSpace(segment.Path) &&
                                  File.Exists(segment.Path))
                .ToList();

            if (usableSegments.Count == 0)
                return null;

            tempPng = Path.Combine(_paths.TempDirectory, $"fvs_wave_sequence_{Guid.NewGuid():N}.png");

            var filter = new System.Text.StringBuilder();
            for (int i = 0; i < usableSegments.Count; i++)
            {
                var segment = usableSegments[i];
                double duration = Math.Max(0.001, segment.TimelineEndSec - segment.TimelineStartSec);
                filter.Append($"[{i}:a]atrim=start={segment.FileStartSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:duration={duration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},");
                filter.Append($"asetpts=PTS-STARTPTS,aformat=channel_layouts=mono[a{i}];");
            }

            if (usableSegments.Count > 1)
            {
                filter.Append(string.Concat(Enumerable.Range(0, usableSegments.Count).Select(i => $"[a{i}]")));
                filter.Append($"concat=n={usableSegments.Count}:v=0:a=1[a_seq];");
                filter.Append($"[a_seq]volume=1.5,showwavespic=s={width}x{height}:colors=0x7DD3FC:draw=full[v_wave]");
            }
            else
            {
                filter.Append($"[a0]volume=1.5,showwavespic=s={width}x{height}:colors=0x7DD3FC:draw=full[v_wave]");
            }

            var seqArgs = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
            foreach (var segment in usableSegments)
            {
                seqArgs.Add("-i");
                seqArgs.Add(segment.Path);
            }
            seqArgs.Add("-filter_complex");
            seqArgs.Add(filter.ToString());
            seqArgs.Add("-map");
            seqArgs.Add("[v_wave]");
            seqArgs.Add("-frames:v");
            seqArgs.Add("1");
            seqArgs.Add(tempPng);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string arg in seqArgs) psi.ArgumentList.Add(arg);

            process = Process.Start(psi);
            using var _processCleanup = process;
            if (process == null) return null;
            ChildProcessTracker.AddProcess(process);

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            _ = await outputTask;
            _ = await errorTask;

            if (process.ExitCode == 0 && File.Exists(tempPng))
                return tempPng;

            if (File.Exists(tempPng))
                File.Delete(tempPng);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (process != null && !process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }

            if (tempPng != null && File.Exists(tempPng))
            {
                try { File.Delete(tempPng); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
            }

            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Failed to generate sequence waveform: {ex.Message}");
            if (tempPng != null && File.Exists(tempPng))
            {
                try { File.Delete(tempPng); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
            }
        }
        finally
        {
            try { process?.Dispose(); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        }

        return null;
    }


    private string FindBinary(string name)
    {
        string basePath = System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var current = new DirectoryInfo(basePath);
        while (current != null)
        {

            string fPath = Path.Combine(current.FullName, "frontend", name);
            if (File.Exists(fPath)) return Path.GetFullPath(fPath);

            string bPath = Path.Combine(current.FullName, "backend", name);
            if (File.Exists(bPath)) return Path.GetFullPath(bPath);

            string srcPath = Path.Combine(current.FullName, "binaries", name);
            if (File.Exists(srcPath)) return Path.GetFullPath(srcPath);


            current = current.Parent;

        }

        return name;

    }


    private string ResolveMpvPath() 
    {
        string p = FindBinary("mpv.exe");
        if (p == "mpv.exe")
        {
            string fallback = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "mpv.exe");
            if (File.Exists(fallback)) return Path.GetFullPath(fallback);
        }
        return p;
    }

    private string ResolveFfprobePath() 
    {
        string p = FindBinary("ffprobe.exe");
        if (p == "ffprobe.exe")
        {
            string fallback = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "ffprobe.exe");
            if (File.Exists(fallback)) return Path.GetFullPath(fallback);
        }
        return p;
    }

    private string ResolveFfmpegPath() 
    {
        string p = FindBinary("ffmpeg.exe");
        if (p == "ffmpeg.exe")
        {
            string fallback = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "ffmpeg.exe");
            if (File.Exists(fallback)) return Path.GetFullPath(fallback);
        }
        return p;
    }


    private void SetLoadingOverlay(string name, bool isVisible)
    {
        var overlay = this.FindControl<Avalonia.Controls.Border>(name);
        if (overlay != null) overlay.IsVisible = isVisible;
    }

    private async Task LoadPhase3DataAsync(CancellationToken cancellationToken, int loadVersion)
    {
        SetPhase3Status("Loading final preview...");
        SetLoadingOverlay("Phase3VideoLoadingOverlay", true);
        SetLoadingOverlay("Phase3ThumbLoadingOverlay", true);
        SetLoadingOverlay("Phase3WaveLoadingOverlay", true);
        _phase3Ready = false;
        UpdateNextButtonState();
        UpdatePreviewControlsState();

        try
        {
            _phase3VideoDurationSec = GetPhase3VideoDurationSeconds();
            var thumbLaneGrid = this.FindControl<Avalonia.Controls.Grid>("ThumbnailLaneGrid");
            var waveLane = this.FindControl<Avalonia.Controls.Image>("WaveformLaneImage");
            if (thumbLaneGrid != null)
            {
                foreach (var child in thumbLaneGrid.Children)
                {
                    if (child is Avalonia.Controls.Image oldFrame)
                    {
                        (oldFrame.Source as IDisposable)?.Dispose();
                        oldFrame.Source = null;
                    }
                }
                thumbLaneGrid.Children.Clear();
                thumbLaneGrid.ColumnDefinitions.Clear();
            }
            if (waveLane != null)
            {
                (waveLane.Source as IDisposable)?.Dispose();
                waveLane.Source = null;
            }
            _phase3ClipDurationsSec.Clear();

            _previewDetach?.Attach();

            var border = this.FindControl<Avalonia.Controls.Border>("VideoHostBorder");
            if (border != null && !string.IsNullOrEmpty(_videoPath))
            {
                if (border.Child is FortniteVideoSoftware.App.MpvVideoView oldHost)
                {
                    oldHost.Dispose();
                    border.Child = null;
                }

                var wizardVideoHost = new FortniteVideoSoftware.App.MpvVideoView
                {
                    Name = "WizardVideoHost",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
                };
                border.Child = wizardVideoHost;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Task.Delay(50, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (_currentStep != 3 || loadVersion != _phase3LoadVersion) return;

                await wizardVideoHost.StartMpvProcessAsync(ResolveMpvPath());

                if (wizardVideoHost.IpcClient == null)
                    throw new InvalidOperationException("Video preview did not start.");

                await wizardVideoHost.IpcClient.LoadFileAsync(_videoPath, _trimStartMs / 1000.0);
                await wizardVideoHost.IpcClient.SetPropertyAsync("time-pos", (_trimStartMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture));
                await wizardVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
                RuntimeLog.Info("MUSIC_WIZARD", "Phase 3 MPV preview video loaded.");
                RefreshDetachButtonState();


                var videoVolSlider = this.FindControl<Slider>("VideoVolSlider");
                if (videoVolSlider != null)
                    await wizardVideoHost.IpcClient.SetPropertyDoubleAsync("volume", GetPreviewVideoVolume());
            }
            SetLoadingOverlay("Phase3VideoLoadingOverlay", false);

            cancellationToken.ThrowIfCancellationRequested();

            if (thumbLaneGrid != null)
            {
                var ffmpeg = ResolveFfmpegPath();
                var videosToThumb = (_isMergerMode && _mergerVideos != null && _mergerVideos.Count > 0) 
                    ? _mergerVideos : new System.Collections.Generic.List<string> { _videoPath };
                
                foreach (var f in _lastPhase3ThumbFiles) { try { if (File.Exists(f)) File.Delete(f); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); } }
                _lastPhase3ThumbFiles.Clear();

                double totalDur = 0;
                var videoDurs = new System.Collections.Generic.List<double>();
                foreach (var v in videosToThumb)
                {
                    double dur = 10.0;
                    if (!_isMergerMode) dur = _phase3VideoDurationSec;
                    else
                    {
                        var prober = new FortniteVideoSoftware.Core.Media.MediaProber(ffmpeg.Replace("ffmpeg.exe", "ffprobe.exe"), v);
                        try { dur = await prober.GetDurationAsync(); } catch { dur = 10.0; }
                    }
                    videoDurs.Add(dur);
                    totalDur += dur;
                }

                if (totalDur <= 0) totalDur = 1.0;
                _phase3ClipDurationsSec.Clear();
                _phase3ClipDurationsSec.AddRange(videoDurs.Select(d => Math.Max(0.1, d / Math.Max(0.001, _phase3BaseSpeed))));

                var laneImages = new System.Collections.Generic.List<Avalonia.Controls.Image>(videosToThumb.Count);
                for (int i = 0; i < videosToThumb.Count; i++)
                {
                    thumbLaneGrid.ColumnDefinitions.Add(
                        new Avalonia.Controls.ColumnDefinition(videoDurs[i], Avalonia.Controls.GridUnitType.Star));

                    var img = new Avalonia.Controls.Image { Stretch = Avalonia.Media.Stretch.Fill };
                    Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(
                        img, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
                    Avalonia.Controls.Grid.SetColumn(img, i);
                    thumbLaneGrid.Children.Add(img);
                    laneImages.Add(img);
                }

                using var stripGate = new System.Threading.SemaphoreSlim(
                    Math.Clamp(Environment.ProcessorCount / 2, 2, 4));

                var stripTasks = new System.Collections.Generic.List<Task>(videosToThumb.Count);
                for (int i = 0; i < videosToThumb.Count; i++)
                {
                    double vDur = videoDurs[i];
                    int framesCount = Math.Max(1, (int)Math.Round(15 * (vDur / totalDur)));
                    double startOffset = (_isMergerMode || i > 0) ? 0 : (_trimStartMs / 1000.0);
                    string videoForStrip = videosToThumb[i];
                    var target = laneImages[i];

                    stripTasks.Add(Task.Run(async () =>
                    {
                        await stripGate.WaitAsync(cancellationToken);
                        try
                        {
                            bool streamed = await ThumbnailStripGenerator.StreamAsync(
                                ffmpeg, videoForStrip, startOffset, vDur, cancellationToken,
                                onReady: wb =>
                                {
                                    if (loadVersion != _phase3LoadVersion || _currentStep != 3) return;
                                    (target.Source as IDisposable)?.Dispose();
                                    target.Source = wb;
                                },
                                onFrame: () => target.InvalidateVisual(),
                                frames: framesCount,
                                logTag: "MUSIC_WIZARD");

                            if (streamed) return;

                            string? path = await GenerateThumbnailsStripAsync(
                                ffmpeg, videoForStrip, startOffset, vDur, cancellationToken, framesCount);
                            if (path == null) return;

                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                if (loadVersion != _phase3LoadVersion || _currentStep != 3) return;
                                _lastPhase3ThumbFiles.Add(path);
                                try
                                {
                                    using var fs = File.OpenRead(path);
                                    target.Source = new Avalonia.Media.Imaging.Bitmap(fs);
                                }
                                catch (Exception ex)
                                {
                                    RuntimeLog.Fail("MUSIC_WIZARD", $"Could not load a filmstrip thumbnail: {ex.Message}");
                                }
                            });
                        }
                        finally { stripGate.Release(); }
                    }));
                }

                await Task.WhenAll(stripTasks);
            }
            SetLoadingOverlay("Phase3ThumbLoadingOverlay", false);

            cancellationToken.ThrowIfCancellationRequested();

            if (waveLane != null && _selectedTrack != null && !string.IsNullOrEmpty(_selectedTrack.FilePath))
            {
                var previewSegments = BuildPhase3MusicPreviewSegments();
                double audibleMusicDuration = previewSegments.Count > 0
                    ? Math.Min(GetPhase3VideoDurationSeconds(), previewSegments[^1].TimelineEndSec)
                    : 0.0;
                waveLane.Width = double.NaN;
                waveLane.Margin = new Avalonia.Thickness(0, 0, 0, 0);

                if (audibleMusicDuration > 0.01)
                {
                    var ffmpeg = ResolveFfmpegPath();
                    bool useSequenceWaveform = previewSegments.Count > 1 || IsPhase3LoopMusicEnabled();
                    string? wavePath = useSequenceWaveform
                        ? await GeneratePhase3MusicSequenceWaveformAsync(ffmpeg, previewSegments, 1200, 60, cancellationToken)
                        : await FortniteVideoSoftware.Core.Media.WaveformGenerator.GenerateWaveformImageAsync(
                            ffmpeg, _selectedTrack.FilePath, 1200, 60, _songStartSeconds, audibleMusicDuration, cancellationToken);

                    if (loadVersion != _phase3LoadVersion || _currentStep != 3) return;
                    if (wavePath != null)
                    {
                        try
                        {
                            using var fs = File.OpenRead(wavePath);
                            (waveLane.Source as IDisposable)?.Dispose();
                            waveLane.Source = new Avalonia.Media.Imaging.Bitmap(fs);
                            DeleteTempFile(ref _lastPhase3WaveFile);
                            _lastPhase3WaveFile = wavePath;
                            UpdatePhase3WaveformLaneWidth();
                        }
                        catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
                    }
                }
            }
            SetLoadingOverlay("Phase3WaveLoadingOverlay", false);

            if (loadVersion != _phase3LoadVersion || _currentStep != 3) return;
            _phase3Ready = true;
            SetPhase3Status("");
            UpdateFinalPlacementSummary();
            UpdateProblemFlags();
            DrawPhase3TimelineScale();
            UpdatePlayhead();
            UpdateNextButtonState();
            UpdatePreviewControlsState();
        }
        catch (OperationCanceledException)
        {
            SetPhase3Status("");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Failed to load phase 3 preview: {ex.Message}");
            SetPhase3Status("Final preview could not load. You can go back and try again.");
            _phase3Ready = false;
            UpdateNextButtonState();
            UpdatePreviewControlsState();
        }
        finally
        {
            SetLoadingOverlay("Phase3VideoLoadingOverlay", false);
            SetLoadingOverlay("Phase3ThumbLoadingOverlay", false);
            SetLoadingOverlay("Phase3WaveLoadingOverlay", false);

            if (loadVersion == _phase3LoadVersion)
            {
                DrawPhase3TimelineScale();
            }
        }
    }

    private void CancelPhase3Load()
    {
        _phase3LoadVersion++;
        try { _phase3LoadCts?.Cancel(); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        try { _phase3LoadCts?.Dispose(); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        _phase3LoadCts = null;
        _phase3Ready = false;
    }

    private void DisposePhase3VideoHost()
    {
        var border = this.FindControl<Avalonia.Controls.Border>("VideoHostBorder");
        if (border?.Child is FortniteVideoSoftware.App.MpvVideoView wizardVideoHost)
        {
            wizardVideoHost.Dispose();
            border.Child = null;
        }
    }

    private double GetPhase3VideoDurationSeconds()
    {
        double duration = _trimEndMs > _trimStartMs
            ? (_trimEndMs - _trimStartMs) / 1000.0
            : (_actualVideoDurationMs > _trimStartMs ? (_actualVideoDurationMs - _trimStartMs) / 1000.0 : 60.0);
        double effectiveDuration = CalculatePhase3EffectiveDurationSeconds(duration);
        return Math.Max(0.1, effectiveDuration);
    }

    private double GetPhase3SourceDurationSeconds()
    {
        double duration = _trimEndMs > _trimStartMs
            ? (_trimEndMs - _trimStartMs) / 1000.0
            : (_actualVideoDurationMs > _trimStartMs ? (_actualVideoDurationMs - _trimStartMs) / 1000.0 : 60.0);
        return Math.Max(0.1, duration);
    }

    /// <summary>
    /// TIME_01 - how long phase 3's video actually runs for once speed changes and freezes have
    /// stretched it.
    ///
    /// <para>
    /// THIS METHOD USED TO BE WRONG. Its hand-written loop advanced its cursor past a freeze
    /// (`cursor = Math.Max(cursor, segEnd)`), i.e. it believed a held frame REPLACED the footage
    /// underneath it. The exported graph does the opposite - `GranularSpeedBuilder` holds the frame
    /// and then carries on playing from the same spot, so a 1.5 second freeze makes the finished
    /// video 1.5 seconds LONGER and skips nothing. Measured against the export as ground truth this
    /// method under-reported by the whole freeze duration every time: 63.000s reported as 60.000s
    /// for a single 3 second freeze, 33.000s as 31.500s at base speed 2x. Music was therefore
    /// positioned against a length the finished video never had.
    /// </para>
    /// <para>
    /// It now delegates to <see cref="FortniteVideoSoftware.Core.Media.OutputTimeline"/>, the single
    /// shared model, so it cannot disagree with the export again.
    /// </para>
    /// </summary>
    private double CalculatePhase3EffectiveDurationSeconds(double sourceDurationSec)
    {
        // CUTS_02 — the last argument is the fix. Without it every deleted second is still counted
        // as video the music has to cover.
        var timeline = FortniteVideoSoftware.Core.Media.OutputTimeline.Create(
            sourceDurationSec * 1000.0,
            _phase3SpeedSegments,
            _phase3BaseSpeed,
            _trimStartMs,
            FortniteVideoSoftware.Core.Media.MemePlacement.ToInsertions(_phase3Memes),   // MEME_06
            FortniteVideoSoftware.Core.Media.CutRange.ToClipRelative(_phase3Cuts, _trimStartMs));
        return Math.Max(0.001, timeline.TotalOutputSeconds);
    }

    /// <summary>
    /// TIME_01 - a moment in the FINISHED video to the source moment showing at that instant,
    /// measured from the trim-in point.
    ///
    /// <para>
    /// Carried the same freeze error as <see cref="CalculatePhase3EffectiveDurationSeconds"/> and is
    /// fixed the same way, by delegating to the shared
    /// <see cref="FortniteVideoSoftware.Core.Media.OutputTimeline"/>. Inside a held frame the answer
    /// is deliberately lossy - every moment of the freeze shows the same source instant, so that
    /// instant is what comes back.
    /// </para>
    /// </summary>
    /// <summary>
    /// ══════════════════════════════════════════════════════════════════════════════
    /// CUTS_03 — WHY PHASE 3 NEEDS NO SKIP LOOP OF ITS OWN.
    ///
    /// This preview is driven from an OUTPUT clock: the wizard counts finished-video seconds and
    /// asks this method where in the source footage that moment lives, then seeks mpv there. A cut
    /// occupies zero output time, so once the timeline knows about it, no output second can ever
    /// map into deleted footage — the deleted span is simply never a possible answer, and the
    /// preview steps over it on its own.
    ///
    /// Which is exactly why the missing argument here was so quiet: the timeline was built WITHOUT
    /// the cuts, so it still believed every deleted second was playable, mapped output seconds
    /// into them, and told mpv to go and show the user footage that will not be in their video.
    /// The fix is the cut list, not a watchdog.
    /// ══════════════════════════════════════════════════════════════════════════════
    /// </summary>
    private double MapPhase3OutputToSourceRelativeSeconds(double outputRelativeSec)
    {
        double sourceDurationSec = GetPhase3SourceDurationSeconds();
        var timeline = FortniteVideoSoftware.Core.Media.OutputTimeline.Create(
            sourceDurationSec * 1000.0,
            _phase3SpeedSegments,
            _phase3BaseSpeed,
            _trimStartMs,
            FortniteVideoSoftware.Core.Media.MemePlacement.ToInsertions(_phase3Memes),   // MEME_06
            FortniteVideoSoftware.Core.Media.CutRange.ToClipRelative(_phase3Cuts, _trimStartMs));
        double clamped = Math.Clamp(outputRelativeSec, 0, GetPhase3VideoDurationSeconds());
        return timeline.OutputToSourceRelative(clamped);
    }

    private double GetPhase3PreviewSpeedAtSourceRelativeSeconds(double sourceRelativeSec)
    {
        double sourceAbsMs = _trimStartMs + sourceRelativeSec * 1000.0;
        foreach (var seg in _phase3SpeedSegments)
        {
            if (sourceAbsMs >= seg.StartMs && sourceAbsMs <= seg.EndMs)
                return Math.Max(0.0, seg.Speed);
        }

        return Math.Max(0.001, _phase3BaseSpeed);
    }

    private double GetCurrentPhase3VideoRelativeSeconds()
    {
        double fallback = Math.Clamp(_previewCurrentOffset - _songStartSeconds, 0, GetPhase3VideoDurationSeconds());
        if (_currentStep == 3 && _isPreviewPlaying && _phase3PreviewClockStartTime.HasValue)
            return Math.Clamp(
                _phase3PreviewClockStartOffsetSec + (DateTime.UtcNow - _phase3PreviewClockStartTime.Value).TotalSeconds,
                0,
                GetPhase3VideoDurationSeconds());

        return fallback;
    }

    private void EnforcePhase3PreviewEnd()
    {
        if (_currentStep != 3 || !_isPreviewPlaying) return;

        double videoDuration = GetPhase3VideoDurationSeconds();
        if (GetCurrentPhase3VideoRelativeSeconds() < videoDuration - 0.03) return;

        _previewCurrentOffset = _songStartSeconds + videoDuration;
        var wizardVideoHost = WizardVideoHost;
        if (wizardVideoHost?.IpcClient != null)
        {
            double endTime = (_trimStartMs / 1000.0) + MapPhase3OutputToSourceRelativeSeconds(videoDuration);
            _ = wizardVideoHost.IpcClient.SetPropertyAsync("time-pos", endTime.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        StopPreview();
        UpdatePlayhead();
    }

    private void SaveWizardVolumes()
    {
        if (!FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.Defaults.RememberMusicVolumes) return;
        try
        {
            var videoVolSlider = this.FindControl<Avalonia.Controls.Slider>("VideoVolSlider");
            var musicVolSlider = this.FindControl<Avalonia.Controls.Slider>("MusicVolSlider");
            var updates = new System.Text.Json.Nodes.JsonObject();
            if (videoVolSlider != null) updates["WizardVideoVolume"] = videoVolSlider.Value;
            if (musicVolSlider != null) updates["WizardMusicVolume"] = musicVolSlider.Value;
            new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths).UpdatePropertiesSync(updates);
        }
        catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
    }

    private void UpdatePhase3WaveformLaneWidth()
    {
        var clip = this.FindControl<Canvas>("Phase3WaveformClip");
        var waveImg = this.FindControl<Avalonia.Controls.Image>("WaveformLaneImage");
        if (clip == null || waveImg == null) return;

        double videoDuration = GetPhase3VideoDurationSeconds();
        if (videoDuration <= 0) return;

        double ratio = GetQueuedMusicCoverageSeconds() / videoDuration;
        waveImg.Width = clip.Bounds.Width * ratio;
        waveImg.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        waveImg.Height = 60;
    }

    private void UpdateFinalPlacementSummary()
    {
        var label = this.FindControl<TextBlock>("FinalPlacementLabel");
        if (label == null) return;

        if (_selectedTrack == null)
        {
            label.Text = "Select a music track to continue.";
            return;
        }

        double trimStartSec = _trimStartMs / 1000.0;
        double trimEndSec = trimStartSec + GetPhase3VideoDurationSeconds();
        double audibleMusic = GetQueuedMusicCoverageSeconds();
        string endText = audibleMusic >= GetPhase3VideoDurationSeconds() - 0.01
            ? "Music is trimmed at the video end."
            : $"Only {FormatSeconds(audibleMusic)} of music remains after this song point.";

        string queueText = _pendingAutoFillMusicPaths.Count > 1
            ? $" Auto-Fill queue has {_pendingAutoFillMusicPaths.Count} songs."
            : "";
        label.Text = $"Song starts at {FormatSeconds(_songStartSeconds)}. Video range is {FormatSeconds(trimStartSec)} to {FormatSeconds(trimEndSec)}. {endText}{queueText}";
    }

    private void UpdateCoverageBar()
    {
        // COVER_02 — the `if (!_isMergerMode) return;` guard is gone. COVER_01 showed this panel on
        // both apps but left this method refusing to fill it, so a Main App user with a short song
        // got the block with a permanently empty bar and a 0% figure. Either it is shown and it
        // works, or it is not shown at all — which is what UpdateCoverageHelperVisibility decides.
        double videoDuration = GetPhase3VideoDurationSeconds();
        double audibleMusic = GetQueuedMusicCoverageSeconds();

        var loopCheck = this.FindControl<CheckBox>("LoopMusicCheckBox");
        bool loopEnabled = loopCheck?.IsChecked ?? false;

        double coveragePercent = loopEnabled ? 100.0 : Math.Min(100.0, (audibleMusic / videoDuration) * 100.0);

        var fill = this.FindControl<Border>("CoverageBarFill");
        if (fill != null)
        {
            double panelWidth = this.FindControl<Avalonia.Controls.Control>("MultiSongHelperPanel")?.Bounds.Width ?? 200;
            fill.Width = Math.Max(0, panelWidth * (coveragePercent / 100.0) - 24);
            // TONE_01: all three coverage states come off tokens now.
            fill.Background = coveragePercent >= 99.9
                ? Infrastructure.ThemeResources.Brush(this, "AppSuccessBrush", Avalonia.Media.Brush.Parse("#3f9c6b"))
                : coveragePercent >= 50
                    ? Infrastructure.ThemeResources.Brush(this, "AppWarningBrush", Avalonia.Media.Brush.Parse("#facc15"))
                    : Infrastructure.ThemeResources.Brush(this, "AppDangerBrush", Avalonia.Media.Brush.Parse("#a83232"));
        }

        var pctText = this.FindControl<TextBlock>("CoveragePercentText");
        if (pctText != null)
        {
            pctText.Text = $"{coveragePercent:0}%";
            pctText.Foreground = coveragePercent >= 99.9   // TONE_01
                ? Infrastructure.ThemeResources.Brush(this, "AppSuccessBrush", Avalonia.Media.Brush.Parse("#3f9c6b"))
                : Infrastructure.ThemeResources.Brush(this, "AppWarningBrush", Avalonia.Media.Brush.Parse("#facc15"));
        }

        var barText = this.FindControl<TextBlock>("CoverageBarText");
        if (barText != null)
        {
            barText.Text = loopEnabled
                ? "Music loops - full coverage"
                : $"{FormatSeconds(audibleMusic)} / {FormatSeconds(videoDuration)}";
        }

        var warningBanner = this.FindControl<Border>("CoverageWarningBanner");
        var warningText = this.FindControl<TextBlock>("CoverageWarningText");
        if (warningBanner != null && warningText != null)
        {
            if (coveragePercent >= 99.9 || loopEnabled)
            {
                warningBanner.IsVisible = false;
            }
            else
            {
                double uncovered = videoDuration - audibleMusic;
                warningBanner.IsVisible = true;
                warningText.Text = $"WARNING: Your music covers {coveragePercent:0}% of the video. The last {FormatSeconds(uncovered)} will have NO music. Add more songs, enable looping, or continue anyway.";
            }
        }

        // COVER_02 — every path that can change coverage already calls this method, so hanging the
        // visibility decision off the end of it means no caller has to remember a second step.
        UpdateCoverageHelperVisibility();
    }

    private double GetQueuedMusicCoverageSeconds()
    {
        var segments = BuildPhase3MusicPreviewSegments();
        if (segments.Count == 0)
            return 0.0;

        return Math.Min(GetPhase3VideoDurationSeconds(), segments[^1].TimelineEndSec);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // COVER_02 — THE COVERAGE BLOCK IS CONDITIONAL, NOT MODE-GATED.
    //
    // It started life gated on `_isMergerMode`, which hid it from the Main App even when the song
    // ran out — a warning with no reachable cure. COVER_01 showed it on both apps, which fixed
    // that and created the opposite nuisance: a three-minute song on a forty-second trim got a
    // coverage bar, a loop switch and an auto-fill button for a problem it does not have, and the
    // extra height pushed the rest of the step around.
    //
    // The rule is simply "is there anything to answer": the music falls short, OR the user has
    // already engaged one of these controls (loop ticked, queue built) and must be able to reach
    // it again to undo that. The Video Merger always qualifies — a merge is a sequence of clips
    // and coverage is the normal question there, not the exception.
    // ══════════════════════════════════════════════════════════════════════════════════════
    private void UpdateCoverageHelperVisibility()
    {
        var helperPanel = this.FindControl<Avalonia.Controls.StackPanel>("MultiSongHelperPanel");
        if (helperPanel == null) return;

        bool loopOn = this.FindControl<CheckBox>("LoopMusicCheckBox")?.IsChecked ?? false;
        bool needed = _isMergerMode
                      || loopOn
                      || _pendingAutoFillMusicPaths.Count > 0
                      || GetMusicShortfallSeconds() > 0.0;

        bool visible = _currentStep == 2 && needed;
        helperPanel.IsVisible = visible;

        EnsureCoverageHeadroom(visible);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // COVER_03 — HEADROOM FOR THE COVERAGE BLOCK.
    //
    // With the block on, step 2 needs about 875px of window to show the waveform, the start-point
    // controls, the two protection checkboxes, the coverage bar, the loop switch, the auto-fill
    // row AND the queue list without the last of them falling off the bottom. The window's own
    // MinHeight is 730, which is right when the block is absent.
    //
    // 875 was measured on a 1920x1080 display. Avalonia lays out in DEVICE-INDEPENDENT pixels, so
    // a 4K or 8K panel at 200%/300% scaling needs no adjustment — the number already means the
    // same physical size there, and multiplying by the DPI would make the window absurd. What DOES
    // change the height needed is the Settings font scale, because every row in that block grows
    // with it, so that is what the figure is multiplied by.
    //
    // Then it is clamped to the screen actually in use: raising MinHeight above a laptop's working
    // area produces a window whose bottom edge, and therefore its NEXT button, cannot be reached.
    // A slightly cramped panel is recoverable by scrolling; an unreachable button is not.
    // ══════════════════════════════════════════════════════════════════════════════════════
    private const double CoverageHelperDesignHeight = 875.0;
    private const double WizardBaseMinHeight = 730.0;

    private void EnsureCoverageHeadroom(bool coverageVisible)
    {
        try
        {
            double target = WizardBaseMinHeight;

            if (coverageVisible)
            {
                target = CoverageHelperDesignHeight * Infrastructure.ThemeManager.CurrentFontMultiplier;

                var screen = Screens?.ScreenFromWindow(this) ?? Screens?.Primary;
                if (screen != null)
                {
                    // WorkingArea is in physical pixels; Scaling converts it to the layout units
                    // MinHeight is expressed in. 60 leaves room for the taskbar and the frame.
                    double usable = (screen.WorkingArea.Height / Math.Max(0.1, screen.Scaling)) - 60.0;
                    if (usable > WizardBaseMinHeight) target = Math.Min(target, usable);
                }
            }

            target = Math.Max(WizardBaseMinHeight, Math.Round(target));
            if (Math.Abs(MinHeight - target) < 0.5) return;

            MinHeight = target;
            if (Height < target) Height = target;
        }
        catch (Exception ex)
        {
            // Never let a screen-geometry query stop the wizard from showing a step.
            RuntimeLog.Swallowed(ex);
        }
    }

    private bool IsPhase3LoopMusicEnabled()
    {
        // COVER_01 — the `_isMergerMode &&` guard is gone. A single video whose song runs out
        // needs looping for exactly the same reason a merged one does.
        return this.FindControl<CheckBox>("LoopMusicCheckBox")?.IsChecked ?? false;
    }

    private System.Collections.Generic.List<Phase3MusicPreviewSegment> BuildPhase3MusicPreviewSegments()
    {
        var segments = new System.Collections.Generic.List<Phase3MusicPreviewSegment>();
        if (_selectedTrack == null)
            return segments;

        double targetDuration = GetPhase3VideoDurationSeconds();
        if (targetDuration <= 0.01)
            return segments;

        var sourcePaths = (_pendingAutoFillMusicPaths.Count > 0
                ? _pendingAutoFillMusicPaths
                : new System.Collections.Generic.List<string> { _selectedTrack.FilePath })
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .ToList();

        if (sourcePaths.Count == 0)
            return segments;

        bool loopEnabled = IsPhase3LoopMusicEnabled();
        double cursor = 0.0;
        bool firstSegment = true;
        int guard = 0;

        do
        {
            bool addedAny = false;
            foreach (string path in sourcePaths)
            {
                if (cursor >= targetDuration - 0.001)
                    break;

                double fileStart = firstSegment ? _songStartSeconds : 0.0;
                double knownDuration = GetKnownTrackDurationSeconds(path);
                if (knownDuration <= 0.01 && string.Equals(path, _selectedTrack.FilePath, StringComparison.OrdinalIgnoreCase))
                    knownDuration = _trackDuration;
                if (knownDuration <= 0.01)
                {
                    firstSegment = false;
                    continue;
                }

                double availableDuration = Math.Max(0.0, knownDuration - fileStart);
                double takeDuration = Math.Min(availableDuration, targetDuration - cursor);
                firstSegment = false;

                if (takeDuration <= 0.001)
                    continue;

                segments.Add(new Phase3MusicPreviewSegment
                {
                    Path = path,
                    TimelineStartSec = cursor,
                    TimelineEndSec = cursor + takeDuration,
                    FileStartSec = fileStart
                });

                cursor += takeDuration;
                addedAny = true;
            }

            if (!loopEnabled || !addedAny)
                break;
        }
        while (cursor < targetDuration - 0.001 && ++guard < 1000);

        return segments;
    }

    private Phase3MusicPreviewSegment? FindPhase3MusicPreviewSegment(double outputRelativeSec)
    {
        foreach (var segment in BuildPhase3MusicPreviewSegments())
        {
            if (outputRelativeSec >= segment.TimelineStartSec &&
                outputRelativeSec < segment.TimelineEndSec - 0.005)
            {
                return segment;
            }
        }

        return null;
    }

    private async void QueuePhase3MusicPreviewSync()
    {
        if (_currentStep != 3 || !_isPreviewPlaying || _audioIpcClient == null)
            return;

        if (Interlocked.Exchange(ref _phase3MusicSyncInFlight, 1) == 1)
            return;

        try
        {
            await SyncPhase3MusicPreviewTrackAsync(forceReload: false);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Phase 3 music preview sync failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _phase3MusicSyncInFlight, 0);
        }
    }

    private async Task EnsureAudioPreviewClientAsync()
    {
        if (_audioIpcClient != null)
            return;

        _audioIpcClient = new FortniteVideoSoftware.Core.Media.MpvIpcClient();
        await _audioIpcClient.StartAudioOnlyAsync(ResolveMpvPath());
    }

    private async Task SyncPhase3MusicPreviewTrackAsync(bool forceReload)
    {
        if (_currentStep != 3 || !_isPreviewPlaying)
            return;

        await EnsureAudioPreviewClientAsync();

        if (_audioIpcClient == null)
            return;

        double outputRelativeSec = GetCurrentPhase3VideoRelativeSeconds();
        var segment = FindPhase3MusicPreviewSegment(outputRelativeSec);
        if (segment == null)
        {
            await _audioIpcClient.SetPropertyAsync("pause", "yes");
            _phase3PreviewMusicPath = null;
            _phase3PreviewMusicSegmentStartSec = double.NaN;
            return;
        }

        double audioStartOffset = Math.Max(0.0, segment.FileStartSec + outputRelativeSec - segment.TimelineStartSec);
        string targetPath = segment.Path.Replace("\\", "/");
        bool segmentChanged =
            !string.Equals(_phase3PreviewMusicPath, targetPath, StringComparison.OrdinalIgnoreCase) ||
            Math.Abs(_phase3PreviewMusicSegmentStartSec - segment.TimelineStartSec) > 0.001;

        if (forceReload || segmentChanged)
        {
            await EnsureMusicBedGainAsync(segment.Path);
            await _audioIpcClient.SetPropertyAsync("start", audioStartOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await _audioIpcClient.SendCommandAsync("loadfile", targetPath, "replace");
            _phase3PreviewMusicPath = targetPath;
            _phase3PreviewMusicSegmentStartSec = segment.TimelineStartSec;
        }
        else if (Math.Abs(_audioIpcClient.CurrentTime - audioStartOffset) > 0.5)
        {
            await _audioIpcClient.SendCommandAsync("seek", audioStartOffset.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
        }

        await _audioIpcClient.SetPropertyDoubleAsync("volume", GetPreviewMusicVolume());
        ApplyPreviewMusicFilters();
        await _audioIpcClient.SetPropertyAsync("pause", "no");
    }

    /// <summary>
    /// AUDIOPROT_01 — PUTS THE EQ CARVE ON THE PREVIEW MUSIC BUS SO THE CHECKBOX IS AUDIBLE.
    ///
    /// <para>
    /// The wizard preview runs the music on its OWN audio-only mpv (<c>_audioIpcClient</c>) and, up
    /// to now, set exactly one property on it: <c>volume</c>. No <c>af</c>, no lavfi, nothing. So
    /// neither protection switch changed a single sample of what the user heard in phase 2 or phase
    /// 3 — ticking or unticking them was inaudible BY CONSTRUCTION, which is the other half of the
    /// "it does not respect the setting" report.
    /// </para>
    /// <para>
    /// The carve is a STATIC EQ on the music bus, so it ports exactly. The string below is the
    /// literal twin of the export's, in <c>AudioFilterChain.BuildMusicChain</c>:
    /// <c>equalizer=f=2000:width_type=h:width=1800:g=-4</c>. ⚠️ IF THAT ONE CHANGES, CHANGE THIS
    /// ONE IN THE SAME COMMIT — a preview that carves by a different amount than the export is
    /// worse than a preview that does not carve at all, because it is silently wrong instead of
    /// visibly absent. It is wrapped in mpv's explicit <c>lavfi=[...]</c> bridge rather than passed
    /// bare, so the syntax cannot be mistaken for one of mpv's own built-in af names.
    /// </para>
    /// <para>
    /// DUCKING IS DELIBERATELY NOT HERE, AND CANNOT BE. The export ducks with
    /// <c>sidechaincompress</c>, whose whole point is that the GAME bus is the trigger for the
    /// MUSIC bus. In the wizard the two live in two separate mpv processes, so there is no path to
    /// route one as a sidechain into the other. Do not "fix" that by inventing a static
    /// approximation — a fake duck that does not follow the real gameplay peaks would misrepresent
    /// the export rather than preview it.
    /// </para>
    /// </summary>
    private void ApplyPreviewMusicFilters()
    {
        if (_audioIpcClient == null) return;

        bool carving = this.FindControl<CheckBox>("CarvingCheckBox")?.IsChecked ?? true;
        string af = carving
            ? "lavfi=[equalizer=f=2000:width_type=h:width=1800:g=-4]"
            : "";


        _ = _audioIpcClient.SetPropertyAsync("af", af);
    }

    private void UpdatePreviewControlsState()
    {
        // PREVIEW1_01 — phase 1 was excluded here, which is what made the transport bar look
        // permanently broken on the first screen you land on.
        bool enabled = _selectedTrack != null && (_currentStep == 1 || _currentStep == 2 || (_currentStep == 3 && _phase3Ready));
        foreach (string name in new[] { "PlayBtn", "SkipBackBtn", "SkipForwardBtn" })
        {
            var btn = this.FindControl<Button>(name);
            if (btn == null) continue;
            btn.IsEnabled = enabled;
            btn.Opacity = enabled ? 1.0 : 0.5;
        }
    }

    /// <summary>
    /// AUDIO_09 — kept as a no-op rather than deleted, because it had FOUR call sites scattered
    /// through the wizard's state-refresh paths (502, 900, 1030, 1542). Removing the method would
    /// have meant touching all four in a change that is otherwise about layout, and each is on a
    /// different refresh trigger. The button it used to drive now lives in Settings as
    /// `AudioProtection`, so there is nothing left to update here.
    /// ⚠️ If you are cleaning up: delete this AND its four call sites together, or not at all.
    /// </summary>
    private void UpdateDuckingCompareButton()
    {
    }

    private double GetPreviewVideoVolume(double? masterVolume = null)
    {
        double videoVolume = this.FindControl<Slider>("VideoVolSlider")?.Value ?? 100.0;
        double master = masterVolume ?? FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolume;
        videoVolume = videoVolume * master / 100.0;

        // PREVIEW_04 — the other half of the balance match: when the correction says the music
        // should come UP, the video comes down by the same amount instead. 0 dB when unmeasured.
        videoVolume *= DbToLinear(PreviewVideoAttenuationDb());

        return Math.Clamp(videoVolume, 0.0, 100.0);
    }


    /// <summary>
    /// PREVIEW_04 — the raw integrated loudness of the gameplay clip, supplied by the Main App
    /// (its `_sourceMeasuredLufs`). Null when it could not be measured; the preview then leaves
    /// both sides alone, exactly as before.
    /// </summary>
    public double? SourceMeasuredLufs { get; set; }

    /// <summary>
    /// PREVIEW_04 — where the EXPORT will put the game bus. Set to
    /// <see cref="FortniteVideoSoftware.Core.Media.AudioLoudnessProbe.TargetLufs"/> when loudness
    /// normalisation will run, and left null when the game bus is exported untouched.
    /// </summary>
    public double? GameBusTargetLufs { get; set; }

    /// <summary>Measured integrated loudness of each music SEGMENT, keyed by file path.</summary>
    private readonly System.Collections.Generic.Dictionary<string, double> _musicSegmentLufs =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// PREVIEW_04 — HOW MUCH THE MUSIC MUST MOVE, IN dB, FOR THIS PREVIEW TO SOUND LIKE THE EXPORT.
    ///
    /// This window plays both files RAW through mpv at nothing but the slider value, while the
    /// export pins the game bus to TargetLufs and the music bed to MusicBedLufs. Commercial masters
    /// sit around -8 to -10 LUFS and a gameplay capture around -20 to -25, so the untouched preview
    /// puts the music 10-15 dB above the game — and the user, hearing that, pulls the music slider
    /// down to fix a problem that only exists here. The exported file then has music far below
    /// where they wanted it. This is the same fault PREVIEW_03 documents for the voice-over window.
    ///
    ///   export delta (music - game) = MusicBedLufs - (GameBusTargetLufs ?? rawGame)
    ///   preview delta if untouched  = rawMusic - rawGame
    ///   correction                  = export delta - preview delta
    ///
    /// Returns 0 when either side could not be measured, which restores the old behaviour exactly.
    /// </summary>
    private double PreviewMusicBalanceDb()
    {
        string? path = _selectedTrack?.FilePath;
        if (path == null || !_musicSegmentLufs.TryGetValue(path, out double rawMusic)) return 0.0;
        if (SourceMeasuredLufs is not double rawGame) return 0.0;

        double exportGame = GameBusTargetLufs ?? rawGame;
        double exportDelta = FortniteVideoSoftware.Core.Media.AudioLoudnessProbe.MusicBedLufs - exportGame;
        double previewDelta = rawMusic - rawGame;

        // Clamped to the same rails the export clamps its bed gain to, so a badly-tagged file
        // cannot silence one side of the preview.
        return Math.Clamp(
            exportDelta - previewDelta,
            FortniteVideoSoftware.Core.Media.AudioLoudnessProbe.MinMusicGainDb,
            FortniteVideoSoftware.Core.Media.AudioLoudnessProbe.MaxMusicGainDb);
    }

    /// <summary>
    /// PREVIEW_04 — the correction is applied by ATTENUATING ONE SIDE, NEVER BOOSTING EITHER.
    /// mpv's volume property is a 0-100 percentage, so "turn the music up 12 dB" is not available
    /// above 100 and would clip if it were. Only the RELATIVE distance matters for judging a mix,
    /// so a positive correction is applied as a cut to the VIDEO instead of a lift to the music.
    /// </summary>
    private double PreviewMusicAttenuationDb() => Math.Min(0.0, PreviewMusicBalanceDb());

    private double PreviewVideoAttenuationDb() => Math.Min(0.0, -PreviewMusicBalanceDb());

    private static double DbToLinear(double db) => Math.Pow(10.0, db / 20.0);

    /// <summary>
    /// PREVIEW_04 — measures the SEGMENT of the track that will actually play and caches it.
    ///
    /// Was a stub returning Task.CompletedTask, which is why the preview never matched the export.
    /// Measures the same window the export's BEDSEG_01 measurement uses — from the chosen song
    /// start, for as long as the video runs — so the two agree. Failures leave the gain at 0 dB,
    /// i.e. exactly the old behaviour: this must never block or slow the preview.
    /// </summary>
    private async Task EnsureMusicBedGainAsync(string musicPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(musicPath) || !File.Exists(musicPath)) return;
            if (_musicSegmentLufs.ContainsKey(musicPath)) return;

            bool isSelected = string.Equals(musicPath, _selectedTrack?.FilePath, StringComparison.OrdinalIgnoreCase);
            double startSec = isSelected ? Math.Max(0.0, _songStartSeconds) : 0.0;

            double videoDur = GetPhase3VideoDurationSeconds();
            double trackDur = isSelected && _selectedTrack != null ? _selectedTrack.DurationSec : 0.0;
            double available = trackDur > 0 ? Math.Max(0.0, trackDur - startSec) : videoDur;
            double window = videoDur > 0 ? Math.Min(videoDur, available) : available;

            string ffmpeg = BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");

            var reading = await FortniteVideoSoftware.Core.Media.AudioLoudnessProbe
                .MeasureAsync(ffmpeg, musicPath, CancellationToken.None,
                              segmentStartSec: startSec,
                              segmentDurationSec: window)
                .ConfigureAwait(true);

            if (reading == null) return;

            _musicSegmentLufs[musicPath] = reading.IntegratedLufs;
            RuntimeLog.Info("MUSIC_WIZARD",
                $"PREVIEW LEVEL MATCH: '{Path.GetFileName(musicPath)}' segment {startSec:F1}s +{window:F1}s " +
                $"measured {reading.IntegratedLufs:F2} LUFS. Music {PreviewMusicAttenuationDb():F2} dB / " +
                $"video {PreviewVideoAttenuationDb():F2} dB applied to the preview only.");
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private double GetPreviewMusicVolume(double? masterVolume = null)
    {
        double musicVolume = this.FindControl<Slider>("MusicVolSlider")?.Value ?? 100.0;
        double master = masterVolume ?? FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolume;
        musicVolume = musicVolume * master / 100.0;

        // PREVIEW_04 — see PreviewMusicBalanceDb. 0 dB when unmeasured, so this is a no-op then.
        musicVolume *= DbToLinear(PreviewMusicAttenuationDb());

        // PREVIEW1_01 — applied here so every caller that sets the preview volume inherits the
        // ease-in without having to know about it.
        musicVolume *= CurrentPreviewFadeFactor();

        return Math.Clamp(musicVolume, 0.0, 100.0);
    }

    private void ApplyPreviewMusicVolume()
    {
        if (_audioIpcClient == null) return;
        _ = _audioIpcClient.SetPropertyDoubleAsync("volume", GetPreviewMusicVolume());
    }

    private void UpdateProblemFlags()
    {
        var panel = this.FindControl<Border>("ProblemFlagsPanel");
        var text = this.FindControl<TextBlock>("ProblemFlagsText");
        if (panel == null || text == null) return;

        var flags = new System.Collections.Generic.List<string>();
        if (_selectedTrack == null)
        {
            panel.IsVisible = false;
            text.Text = "";
            return;
        }

        if (string.IsNullOrWhiteSpace(_selectedTrack.FilePath) || !File.Exists(_selectedTrack.FilePath))
            flags.Add("Music file is missing.");

        double videoDuration = GetPhase3VideoDurationSeconds();
        double coverage = GetQueuedMusicCoverageSeconds();
        bool loopEnabled = IsPhase3LoopMusicEnabled();
        // COVER_01 — this line used to be the END of the story. Now it is a button.
        if (!loopEnabled && videoDuration > 0.1 && coverage < videoDuration - 0.5)
            flags.Add($"Music ends {FormatSeconds(videoDuration - coverage)} before the video ends. Click here to fix it.");

        if (_trackDuration <= 0.01)
            flags.Add("Song length is unknown.");
        else if (_songStartSeconds >= _trackDuration - 0.1)
            flags.Add("Song start is at the very end of the song.");

        double videoVolume = this.FindControl<Slider>("VideoVolSlider")?.Value ?? 100.0;
        double musicVolume = this.FindControl<Slider>("MusicVolSlider")?.Value ?? 100.0;
        if (musicVolume <= 1.0)
            flags.Add("Music volume is muted.");
        if (videoVolume <= 1.0)
            flags.Add("Original video audio is muted.");

        bool duckingEnabled = this.FindControl<CheckBox>("DuckingCheckBox")?.IsChecked ?? true;
        if (!duckingEnabled && videoVolume >= 70.0 && musicVolume >= 80.0)
            flags.Add("Ducking is off while both music and video audio are loud.");

        if (_isMergerMode && _mergerVideos != null && _phase3ClipDurationsSec.Count > 0 &&
            _phase3ClipDurationsSec.Count != _mergerVideos.Count)
        {
            flags.Add("Clip boundary preview could not confirm every merged clip duration.");
        }

        panel.IsVisible = _currentStep == 3 && flags.Count > 0;
        text.Text = string.Join(Environment.NewLine, flags.Select(flag => $"WARNING: {flag}"));

        // COVER_01 — only offer the hand cursor when there is actually something behind the click.
        bool clickable = GetMusicShortfallSeconds() > 0.0;
        panel.Cursor = new Avalonia.Input.Cursor(clickable
            ? Avalonia.Input.StandardCursorType.Hand
            : Avalonia.Input.StandardCursorType.Arrow);
        ToolTip.SetTip(panel, clickable
            ? "Click to add more songs, move the song start, or accept the silent ending."
            : null);
    }

    /// <summary>
    /// LAYOUT_01 — the status line now COLLAPSES when it has nothing to say.
    ///
    /// It used to be a permanent row in the header stack, holding a full line of height open on a
    /// screen whose only elastic row is the video. It is empty the overwhelming majority of the
    /// time, so it was paying rent for a message that rarely arrives.
    /// </summary>
    private void SetPhase3Status(string message)
    {
        var status = this.FindControl<TextBlock>("Phase3StatusLabel");
        if (status == null) return;
        status.Text = message;
        status.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    private static string FormatSeconds(double seconds)
    {
        seconds = Math.Max(0, seconds);
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss\.ff")
            : ts.ToString(@"m\:ss\.ff");
    }

    private static void DeleteTempFile(ref string? path)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try { File.Delete(path); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        }
        path = null;
    }

    private void TogglePreview()
    {
        if (_isPreviewPlaying)
        {
            StopPreview();
            return;

        }


        if (_selectedTrack == null || !File.Exists(_selectedTrack.FilePath))

        {

            ShowToast("⚠ Select a track first to preview!");
            return;
        }

        if (_currentStep == 3 && !_phase3Ready)
        {
            ShowToast("Final preview is still loading.");
            return;
        }

        // PREVIEW1_01 — on the song list, PLAY means "play me this song, from the top". The
        // automatic preview that starts when you highlight a row is a different thing: it drops
        // you into the busiest part and eases in. Once you have skipped, PLAY resumes instead of
        // yanking you back to the beginning.
        double startOffset = (_currentStep == 1 && !_phase1UserSeeked) ? 0.0 : _previewCurrentOffset;

        StartPreviewInternal(startOffset);
    }

    private void SkipPreview(double offsetSeconds)
    {
        if (_selectedTrack == null) return;
        if (_currentStep == 3 && !_phase3Ready) return;

        bool wasPlaying = _isPreviewPlaying;
        StopPreview();

        if (_currentStep == 3)
        {
            double videoRelative = Math.Clamp(GetCurrentPhase3VideoRelativeSeconds() + offsetSeconds, 0, GetPhase3VideoDurationSeconds());
            _previewCurrentOffset = _songStartSeconds + videoRelative;
            SeekPhase3VideoHost(videoRelative, forcePause: !wasPlaying);
        }
        else
        {
            if (_currentStep == 1) _phase1UserSeeked = true;   // PREVIEW1_01
            _previewCurrentOffset += offsetSeconds;
            if (_previewCurrentOffset < 0) _previewCurrentOffset = 0;
            if (_previewCurrentOffset > _selectedTrack.DurationSec) _previewCurrentOffset = _selectedTrack.DurationSec;
        }

        if (wasPlaying)
        {
            StartPreviewInternal(_previewCurrentOffset);
        }

    }


    private async void StartPreviewInternal(double startOffset, bool fadeIn = false)

    {
        // PREVIEW1_01 — the ease-in is a volume ramp rather than an `afade` filter, because `af`
        // on this player is already owned by the carving preview and the two would fight.
        _previewFadeStartUtc = fadeIn ? DateTime.UtcNow : null;


        var playBtn = this.FindControl<Button>("PlayBtn");

        if (playBtn != null)

        {

            playBtn.Classes.Remove("Success");

            playBtn.Classes.Add("Danger");

            var playIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon");

            var pauseIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PauseIcon");

            if (playIcon != null) playIcon.IsVisible = false;

            if (pauseIcon != null) pauseIcon.IsVisible = true;

        }

        _isPreviewPlaying = true;

        _previewCurrentOffset = startOffset;

        _previewStartTime = DateTime.UtcNow;
        _phase3PreviewClockStartTime = null;


        if (_currentStep == 3)
        {
            double outputRelativeSec = Math.Clamp(startOffset - _songStartSeconds, 0, GetPhase3VideoDurationSeconds());
            _phase3PreviewClockStartOffsetSec = outputRelativeSec;
            _phase3PreviewClockStartTime = DateTime.UtcNow;
            SeekPhase3VideoHost(outputRelativeSec, forcePause: false);
            SyncPhase3VideoPreviewClock();

        }


        try

        {

            await EnsureAudioPreviewClientAsync();
            var audioClient = _audioIpcClient;
            if (audioClient == null)
                return;


            // PREVIEW1_01 — `_trackDuration` is only probed on the way OUT of phase 1, so on the
            // song list it is still 0 and this clamp used to force every phase-1 preview to 0:00.
            double clampLimit = _trackDuration > 0 ? _trackDuration : (_selectedTrack?.DurationSec ?? 0);
            double audioStartOffset = clampLimit > 0 ? Math.Clamp(startOffset, 0, clampLimit) : Math.Max(0, startOffset);
            if (_currentStep == 3)
            {
                await SyncPhase3MusicPreviewTrackAsync(forceReload: true);
                return;
            }

            string targetPath = _selectedTrack!.FilePath.Replace("\\", "/");
            if (_lastLoadedTrackPath != targetPath)
            {
                await EnsureMusicBedGainAsync(_selectedTrack!.FilePath);
                await audioClient.SetPropertyAsync("start", audioStartOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await audioClient.SendCommandAsync("loadfile", targetPath, "replace");
                _lastLoadedTrackPath = targetPath;
            }
            else
            {
                await audioClient.SendCommandAsync("seek", audioStartOffset.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
            }

            await audioClient.SetPropertyDoubleAsync("volume", GetPreviewMusicVolume());
            ApplyPreviewMusicFilters();
            await audioClient.SetPropertyAsync("pause", "no");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Preview failed: {ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}");
            ShowToast($"⚠ Preview playback failed: {ex.Message}");
        }
    }

    private void StopPreview()
    {
        if (_previewStartTime.HasValue)
        {
            if (_currentStep == 3)
            {
                _previewCurrentOffset = _songStartSeconds + GetCurrentPhase3VideoRelativeSeconds();
                _phase3PreviewClockStartTime = null;
            }
            else
            {
                _previewCurrentOffset += (DateTime.UtcNow - _previewStartTime.Value).TotalSeconds;
            }

            if (_selectedTrack != null && _currentStep != 3 && _previewCurrentOffset > _selectedTrack.DurationSec)
                _previewCurrentOffset = _selectedTrack.DurationSec;
            _previewStartTime = null;
        }


        _isPreviewPlaying = false;
        _previewFadeStartUtc = null;   // PREVIEW1_01 — a stopped preview has no fade in progress
        _phase3PreviewMusicPath = null;
        _phase3PreviewMusicSegmentStartSec = double.NaN;


        if (_audioIpcClient != null)

        {

            _ = _audioIpcClient.SetPropertyAsync("pause", "yes");

        }


        if (_currentStep == 3)

        {

            var wizardVideoHost = WizardVideoHost;

            if (wizardVideoHost?.IpcClient != null)

                _ = wizardVideoHost.IpcClient.SetPropertyAsync("pause", "yes");

        }


        var playBtn = this.FindControl<Button>("PlayBtn");

        if (playBtn != null)

        {

            playBtn.Classes.Remove("Danger");

            playBtn.Classes.Add("Success");

            var playIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon");

            var pauseIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PauseIcon");

            if (playIcon != null) playIcon.IsVisible = true;

            if (pauseIcon != null) pauseIcon.IsVisible = false;

        }

    }


    private async Task RenderWaveformAsync(string? filePath)

    {

        if (string.IsNullOrEmpty(filePath)) return;
        int renderVersion = System.Threading.Interlocked.Increment(ref _waveformRenderVersion);
        string requestedPath = filePath;


        var waveformImage = this.FindControl<Image>("WaveformImage");

        var loadingText = this.FindControl<TextBlock>("WaveformLoadingText");


        if (waveformImage == null || loadingText == null) return;


        loadingText.IsVisible = true;
        loadingText.Text = "Generating Waveform...";

        (waveformImage.Source as IDisposable)?.Dispose();
        waveformImage.Source = null;


        var ffmpegPath = ResolveFfmpegPath();


        string? pngFile = await FortniteVideoSoftware.Core.Media.WaveformGenerator.GenerateWaveformImageAsync(ffmpegPath, filePath);

        if (renderVersion != _waveformRenderVersion ||
            !string.Equals(_selectedTrack?.FilePath, requestedPath, StringComparison.OrdinalIgnoreCase))
        {
            if (pngFile != null && File.Exists(pngFile))
            {
                try { File.Delete(pngFile); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
            }
            return;
        }

        loadingText.IsVisible = false;


        if (pngFile != null && File.Exists(pngFile))

        {

            try

            {

                using var fs = File.OpenRead(pngFile);

                var bitmap = new Avalonia.Media.Imaging.Bitmap(fs);

                waveformImage.Source = bitmap;


                if (_lastWaveformFile != null && File.Exists(_lastWaveformFile))

                {

                    try { File.Delete(_lastWaveformFile); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }

                }

                _lastWaveformFile = pngFile;


                UpdatePlayhead();

            }

            catch (Exception ex)

            {

                RuntimeLog.Fail("MUSIC_WIZARD", "Failed to load waveform: " + ex.Message);

            }

        }

        else

        {

            loadingText.Text = "Failed to generate waveform.";

            loadingText.IsVisible = true;

        }

    }


    private void DrawTimelineScale()

    {

        var scaleCanvas = this.FindControl<Canvas>("TimelineScaleCanvas");

        if (scaleCanvas == null || _trackDuration <= 0) return;


        double canvasWidth = scaleCanvas.Bounds.Width;

        if (canvasWidth <= 0) return;


        scaleCanvas.Children.Clear();


        double interval = 10.0;

        if (_trackDuration > 300) interval = 60.0;

        else if (_trackDuration > 60) interval = 30.0;

        else if (_trackDuration < 30) interval = 5.0;


        for (double t = 0; t <= _trackDuration; t += interval)

        {

            double fraction = t / _trackDuration;

            double xPos = fraction * canvasWidth;


            var tickLine = new Avalonia.Controls.Shapes.Line

            {

                StartPoint = new Avalonia.Point(xPos, scaleCanvas.Bounds.Height - 4),

                EndPoint = new Avalonia.Point(xPos, scaleCanvas.Bounds.Height),

                Stroke = Avalonia.Media.Brushes.Gray,

                StrokeThickness = 1,

                IsHitTestVisible = false

            };

            scaleCanvas.Children.Add(tickLine);


            var tickLabel = new TextBlock

            {

                Text = TimeSpan.FromSeconds(t).ToString(@"m\:ss"),

                FontSize = Infrastructure.ThemeManager.ScaledFontSize(9),

                Foreground = Avalonia.Media.Brushes.Gray,

                IsHitTestVisible = false,

                RenderTransform = new Avalonia.Media.TranslateTransform(xPos - 10, -2)

            };

            scaleCanvas.Children.Add(tickLabel);

        }

    }


    private void DrawPhase3TimelineScale()

    {

        var lanes = this.FindControl<FortniteVideoSoftware.App.Controls.TimelineLanesControl>("Phase3Lanes");
        double canvasWidth = lanes?.Bounds.Width ?? 0;
        if (canvasWidth <= 0) return;

        DrawPhase3MergerOverlays(canvasWidth);
    }

    private void DrawPhase3MergerOverlays(double canvasWidth)
    {
        if (!_isMergerMode) return;

        Canvas? scaleCanvas = null;
        var thumbCanvas = this.FindControl<Canvas>("ThumbnailOverlayCanvas");
        var waveCanvas = this.FindControl<Canvas>("WaveformOverlayCanvas");
        if (thumbCanvas != null) thumbCanvas.Children.Clear();
        if (waveCanvas != null) waveCanvas.Children.Clear();

        if (_mergerVideos != null && _mergerVideos.Count > 1 && _phase3ClipDurationsSec.Count > 1)
        {
            double totalClipDuration = _phase3ClipDurationsSec.Sum();
            if (totalClipDuration > 0.01)
            {
                double cursor = 0;
                for (int i = 1; i < _phase3ClipDurationsSec.Count; i++)
                {
                    cursor += _phase3ClipDurationsSec[i - 1];
                    double xPos = Math.Clamp((cursor / totalClipDuration) * canvasWidth, 0, canvasWidth);
                    if (thumbCanvas != null)
                        AddLaneBoundary(thumbCanvas, xPos, 60, Avalonia.Media.Brushes.White, 0.85);
                    if (waveCanvas != null)
                        AddLaneBoundary(waveCanvas, xPos, 60, Avalonia.Media.Brushes.White, 0.55);
                    if (scaleCanvas != null)
                    {
                        AddLaneBoundary(scaleCanvas, xPos, scaleCanvas.Bounds.Height, Avalonia.Media.Brushes.White, 0.70);
                        var label = new TextBlock
                        {
                            Text = $"CLIP {i + 1}",
                            FontSize = Infrastructure.ThemeManager.ScaledFontSize(8),
                            Foreground = Avalonia.Media.Brushes.White,
                            IsHitTestVisible = false,
                            RenderTransform = new Avalonia.Media.TranslateTransform(xPos + 4, 0)
                        };
                        scaleCanvas.Children.Add(label);
                    }
                }
            }
        }

        var songs = _pendingAutoFillMusicPaths;
        if (songs != null && songs.Count > 1 && waveCanvas != null)
        {
            double videoDuration = GetPhase3VideoDurationSeconds();
            double cursor = 0.0;
            for (int i = 1; i < songs.Count; i++)
            {
                var previousTrack = FindTrackByPath(songs[i - 1]);
                double offset = i == 1 ? _songStartSeconds : 0.0;
                cursor += Math.Max(0, (previousTrack?.DurationSec ?? 0.0) - offset);
                if (videoDuration <= 0.01 || cursor >= videoDuration) break;

                double xPos = Math.Clamp((cursor / videoDuration) * canvasWidth, 0, canvasWidth);
                AddLaneBoundary(waveCanvas, xPos, 60, Avalonia.Media.Brushes.LightGreen, 0.80);
            }
        }
    }

    private static void AddLaneBoundary(Canvas canvas, double xPos, double height, Avalonia.Media.IBrush brush, double opacity)
    {
        var border = new Avalonia.Controls.Border
        {
            Width = 2,
            Height = Math.Max(1, height),
            Background = brush,
            Opacity = opacity,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(border, Math.Max(0, xPos - 1));
        Canvas.SetTop(border, 0);
        canvas.Children.Add(border);
    }


    private static void EnsurePlayheadLine(
        Canvas canvas,
        ref Avalonia.Controls.Shapes.Line? line,
        Avalonia.Media.IBrush stroke,
        bool dashed)
    {
        if (line == null)
        {
            line = new Avalonia.Controls.Shapes.Line
            {
                Stroke = stroke,
                StrokeThickness = 2,
                IsHitTestVisible = false
            };

            if (dashed)
            {
                line.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(new[] { 2.0, 2.0 });
            }

            canvas.Children.Add(line);
        }
        else if (!canvas.Children.Contains(line))
        {
            canvas.Children.Add(line);
        }
    }

    private void UpdatePlayhead()

    {

        var canvas = this.FindControl<Canvas>("WaveformCanvas");

        var timelineCanvas = this.FindControl<Canvas>("TimelineMarkersCanvas");

        if (canvas == null) return;


        double offsetFraction = _songStartSeconds / Math.Max(0.1, _trackDuration);
        double offsetXPos = canvas.Bounds.Width * offsetFraction;

        double currentTime = _previewCurrentOffset;
        if (_currentStep == 3)
        {
            currentTime = _songStartSeconds + GetCurrentPhase3VideoRelativeSeconds();
        }
        else if (_isPreviewPlaying && _previewStartTime.HasValue)
        {
            currentTime += (DateTime.UtcNow - _previewStartTime.Value).TotalSeconds;
        }
        double playheadFraction = currentTime / Math.Max(0.1, _trackDuration);

        if (playheadFraction > 1.0) playheadFraction = 1.0;

        double playheadXPos = canvas.Bounds.Width * playheadFraction;


        EnsurePlayheadLine(
            canvas,
            ref _waveformOffsetLine,
            Avalonia.Media.Brushes.Gray,
            dashed: true);
        _waveformOffsetLine!.StartPoint = new Avalonia.Point(offsetXPos, 0);
        _waveformOffsetLine.EndPoint = new Avalonia.Point(offsetXPos, canvas.Bounds.Height);

        EnsurePlayheadLine(
            canvas,
            ref _waveformPlayheadLine,
            // TONE_01: the playhead is the app's red, not raw #FF0000.
            Infrastructure.ThemeResources.Brush(this, "AppDangerBrush", Avalonia.Media.Brushes.Red),
            dashed: false);
        _waveformPlayheadLine!.StartPoint = new Avalonia.Point(playheadXPos, 0);
        _waveformPlayheadLine.EndPoint = new Avalonia.Point(playheadXPos, canvas.Bounds.Height);


        if (timelineCanvas != null)

        {

            double txPos = timelineCanvas.Bounds.Width * playheadFraction;

            EnsurePlayheadLine(
                timelineCanvas,
                ref _timelinePlayheadLine,
                Infrastructure.ThemeResources.Brush(this, "AppDangerBrush", Avalonia.Media.Brushes.Red),   // TONE_01
                dashed: false);
            _timelinePlayheadLine!.StartPoint = new Avalonia.Point(txPos, 0);
            _timelinePlayheadLine.EndPoint = new Avalonia.Point(txPos, timelineCanvas.Bounds.Height);

        }


        if (_currentStep == 3)

        {

            var p3Lanes = this.FindControl<FortniteVideoSoftware.App.Controls.TimelineLanesControl>("Phase3Lanes");
            if (p3Lanes != null)
            {
                double videoDuration = GetPhase3VideoDurationSeconds();
                if (Math.Abs(p3Lanes.DurationSeconds - videoDuration) > 0.001)
                    p3Lanes.DurationSeconds = videoDuration;
                p3Lanes.PositionSeconds = GetCurrentPhase3VideoRelativeSeconds();
            }
        }
    }


    private void SetOffsetFromPointer(double x, double width)

    {

        if (width <= 0) return;

        double fraction = x / width;

        fraction = Math.Clamp(fraction, 0.0, 1.0);


        _songStartSeconds = Math.Clamp(_trackDuration * fraction, 0, Math.Max(0, _trackDuration - 0.01));

        var lbl = this.FindControl<TextBlock>("OffsetLabel");
        if (lbl != null) lbl.Text = $"Song begins at {FormatSeconds(_songStartSeconds)}";

        bool wasPlaying = _isPreviewPlaying;
        if (wasPlaying) StopPreview();

        _previewCurrentOffset = _songStartSeconds;

        if (wasPlaying) StartPreviewInternal(_previewCurrentOffset);
        else
        {
            UpdateFinalPlacementSummary();
            UpdateAutoFillQueuePreview();
            UpdateCoverageBar();
            UpdateProblemFlags();
            UpdatePlayhead();
        }
    }


    private void OnBackClicked(object? sender, RoutedEventArgs e)

    {

        if (_currentStep > 1)
        {
            if (_currentStep == 3)
            {
                StopPreview();
                CancelPhase3Load();
                DisposePhase3VideoHost();
                SetPhase3Status("");
                _previewCurrentOffset = _songStartSeconds;
            }
            else
            {
                StopPreview();
            }
            _currentStep--;
            UpdateStepVisibility();
            UpdateNextButtonState();
            UpdatePlayhead();
        }
    }


    private void OnFileDrop(object? sender, DragEventArgs e)

    {

        CancelMusicScan();

        var files = e.Data.GetFiles();

        if (files == null) return;


        var musicExts = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".m4a", ".aac", ".ogg" };

        var firstMusic = files.FirstOrDefault(f => musicExts.Contains(System.IO.Path.GetExtension(f.Name)));


        if (firstMusic != null)

        {

            string path = firstMusic.Path.LocalPath;

            var track = new MusicTrackItem

            {

                Name = Path.GetFileName(path),

                Title = Path.GetFileNameWithoutExtension(path),

                FilePath = path,

                DurationText = "Loading...",

                SizeText = "",

                LastModifiedTicks = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0,

                IsRecent = _recentMusicPaths.Contains(path)

            };


            _allTracks.Clear();

            _allTracks.Add(track);

            var searchBox = this.FindControl<TextBox>("MusicSearchBox");
            if (searchBox != null)
                searchBox.Text = string.Empty;
            ApplyTrackFilterAndSort();

            var listbox = this.FindControl<ListBox>("MusicListBox");

            if (listbox != null) listbox.SelectedIndex = 0;


            RuntimeLog.Info("MUSIC_WIZARD", $"File dropped: {Path.GetFileName(path)}");
            RuntimeLog.Debug("MUSIC_WIZARD", $"Dropped music path: {path}");

            _ = ProbeTrackInfoAsync(track);

            ShowToastSuccess("✔ Music file loaded!");

        }
        else
        {
            ShowToast("Drop an MP3, WAV, M4A, AAC, or OGG file.");
        }

        UpdateMusicEmptyState();

    }


    protected override async void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        CancelAudioAnalysis();
        CancelMusicScan();
        CancelPhase3Load();
        StopPreview();
        ClearPhase3LiveZoomCrop();
        DisposePhase3VideoHost();

        if (_playheadTimer != null) { _playheadTimer.Stop(); _playheadTimer.Tick -= PlayheadTimer_Tick; _playheadTimer = null; }
        
        if (_isSafeToClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        this.Hide();
        
        FortniteVideoSoftware.App.WindowBoundsHelper.SaveBoundsSync(this, "MusicWizardBounds");
        _isSafeToClose = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(Close);

    }


    protected override void OnClosed(EventArgs e)

    {

        Controls.CoachOverlay.Cancel(this);
        Controls.FloatingNotice.Clear(this);
        if (_playheadTimer != null) { _playheadTimer.Stop(); _playheadTimer.Tick -= PlayheadTimer_Tick; _playheadTimer = null; }
        FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged -= OnGlobalMasterVolumeChanged;
        CancelAudioAnalysis();
        CancelMusicScan();
        if (_lastWaveformFile != null && File.Exists(_lastWaveformFile))
        {
            try { File.Delete(_lastWaveformFile); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        }
        foreach (var f in _lastPhase3ThumbFiles) { try { if (File.Exists(f)) File.Delete(f); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); } }
        _lastPhase3ThumbFiles.Clear();
        DeleteTempFile(ref _lastPhase3WaveFile);
        StopPreview();
        DisposePhase3VideoHost();


        if (_audioIpcClient != null)

        {

            try { _audioIpcClient.Dispose(); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }

            _audioIpcClient = null;

        }

        _voiceOverPlayer.Dispose();
        base.OnClosed(e);

    }


    /// <summary>
    /// ISSUE_10 + ISSUE_11 — downloads the shared song library into the folder the wizard is
    /// currently browsing, with live progress and a working CANCEL, then rescans so the new
    /// tracks appear immediately.
    /// </summary>
    private async Task RunSongDownloadAsync()
    {
        string targetDir = ResolveCurrentMusicDirectory();

        var confirm = new Controls.ConfirmDialogWindow();
        confirm.SetTitle("Download more songs?");
        confirm.SetMessage(
            "This will connect to the internet and download background music tracks from the " +
            "official library." + Environment.NewLine + Environment.NewLine +
            "Saving into: " + targetDir + Environment.NewLine + Environment.NewLine +
            "Continue?");
        confirm.SetButtonText("DOWNLOAD", "CANCEL");
        await confirm.ShowDialog(this);
        if (!confirm.Result) return;

        var (count, error) = await Controls.CloudSyncProgressWindow.RunAsync(
            this, "Downloading songs",
            (progress, ct) => MemeCatalog.SyncSongsFromCloudAsync(targetDir, progress, ct));

        _ = ScanDirectoryForMusicAsync(targetDir);

        if (error != null)
        {
            await ErrorReporter.ShowAsync(this, "Song download problem", error,
                "See the log entries tagged [Songs] for the per-file detail.");
            return;
        }

        RuntimeLog.Info("Songs", $"Song download finished: {count} new track(s).");
    }

    /// <summary>
    /// ISSUE_10 — the folder the wizard is currently browsing: the user's saved custom music
    /// folder if set and present, otherwise the shell Music folder.
    /// </summary>
    private string ResolveCurrentMusicDirectory()
    {
        try
        {
            if (File.Exists(_paths.SessionStateFile))
            {
                var state = FortniteVideoSoftware.Core.Infrastructure.AtomicJsonFile.ReadObject(_paths.SessionStateFile);
                if (state != null && state.TryGetPropertyValue("CustomMusicDirectory", out var node) && node != null)
                {
                    string custom = node.ToString();
                    if (Directory.Exists(custom)) return custom;
                }
            }
        }
        catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }

        return Infrastructure.MemeDirectory.GetMusicRoot();
    }

    private void LoadMusicDirectory()

    {

        string? targetDir = null;

        try

        {

            if (File.Exists(_paths.SessionStateFile))
            {
                var state = FortniteVideoSoftware.Core.Infrastructure.AtomicJsonFile.ReadObject(_paths.SessionStateFile);
                if (state != null && state.TryGetPropertyValue("CustomMusicDirectory", out var node) && node != null)
                {
                    targetDir = node.ToString();
                }
            }

        }

        catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }


        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))

        {

            targetDir = Infrastructure.MemeDirectory.GetMusicRoot();

        }


        _ = ScanDirectoryForMusicAsync(targetDir);

    }


    private async Task ScanDirectoryForMusicAsync(string directoryPath)

    {

        CancelMusicScan();
        var cts = new CancellationTokenSource();
        _musicScanCts = cts;
        int scanVersion = _musicScanVersion;

        _allTracks.Clear();
        AvailableTracks.Clear();
        ApplyTrackFilterAndSort();

        try
        {
            var tracks = await Task.Run(() =>
            {
                var found = new System.Collections.Generic.List<MusicTrackItem>();
                if (!Directory.Exists(directoryPath))
                    return found;

                var exts = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".m4a", ".aac", ".ogg" };

                foreach (string f in Directory.EnumerateFiles(directoryPath))
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (!exts.Contains(Path.GetExtension(f)))
                        continue;

                    var fileInfo = new FileInfo(f);
                    found.Add(new MusicTrackItem
                    {
                        Name = Path.GetFileName(f),
                        Title = Path.GetFileNameWithoutExtension(f),
                        FilePath = f,
                        DurationText = "Loading...",
                        SizeText = "",
                        LastModifiedTicks = fileInfo.LastWriteTimeUtc.Ticks,
                        IsRecent = _recentMusicPaths.Contains(f)
                    });
                }

                return found;
            }, cts.Token);

            if (cts.Token.IsCancellationRequested || scanVersion != _musicScanVersion)
                return;

            _allTracks.Clear();
            foreach (var item in tracks)
                _allTracks.Add(item);

            ApplyTrackFilterAndSort();

            foreach (var item in tracks)
                _ = ProbeTrackInfoAsync(item, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Failed to scan music directory: {ex.Message}");
            ApplyTrackFilterAndSort();
        }

    }

    private void UpdateMusicEmptyState()
    {
        var emptyText = this.FindControl<TextBlock>("EmptyMusicListText");
        if (emptyText != null)
        {
            emptyText.Text = _allTracks.Count == 0
                ? "No music files found. Change folder or drop an audio file here."
                : "No songs match the current search.";
            emptyText.IsVisible = AvailableTracks.Count == 0;
        }
    }


    /// <summary>

    /// Improvement #6: Probes duration and file size asynchronously for display in the list.

    /// </summary>

    private async Task ProbeTrackInfoAsync(MusicTrackItem item, CancellationToken cancellationToken = default)

    {

        try

        {

            await _trackProbeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileInfo = new FileInfo(item.FilePath);
                double sizeMb = fileInfo.Length / (1024.0 * 1024.0);
                string sizeText = sizeMb >= 1.0 ? $"{sizeMb:F1} MB" : $"{fileInfo.Length / 1024.0:F0} KB";

                var ffprobePath = ResolveFfprobePath();
                var prober = new FortniteVideoSoftware.Core.Media.MediaProber(ffprobePath, item.FilePath);
                double duration = await prober.GetDurationAsync().ConfigureAwait(false);

                string durationText;
                double durationSec = 0.0;
                if (duration > 0)
                {
                    durationSec = duration;
                    var ts = TimeSpan.FromSeconds(duration);
                    durationText = ts.TotalHours >= 1
                        ? ts.ToString(@"h\:mm\:ss")
                        : ts.ToString(@"m\:ss");
                }
                else
                {
                    durationText = "—";
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    item.SizeText = sizeText;
                    item.DurationSec = durationSec;
                    item.DurationText = durationText;

                    if (_musicSortMode == "Shortest" || _musicSortMode == "Longest")
                    {
                        ApplyTrackFilterAndSort();
                    }
                    else
                    {
                        var idx = AvailableTracks.IndexOf(item);

                        if (idx >= 0)
                        {
                            var tmp = AvailableTracks[idx];

                            AvailableTracks[idx] = tmp;
                        }
                    }

                    UpdateAutoFillQueuePreview();
                    UpdateCoverageBar();
                    UpdateProblemFlags();

                });
            }
            finally
            {
                _trackProbeGate.Release();
            }

        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)

        {

            RuntimeLog.Fail("MUSIC_WIZARD", $"Failed to probe {item.Name}: {ex.Message}");

            Dispatcher.UIThread.Post(() => item.DurationText = "—");

        }

    }


    /// <summary>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// ISSUE_09 — THIS WAS 95 LINES OF HAND-ROLLED TOAST. IT IS NOW ONE LINE.
    ///
    /// The old body built a Border by hand, hunted for `Step1Panel`'s parent to host it, computed a
    /// ZIndex from its siblings, then hand-animated opacity in a `for` loop with `await Task.Delay(16)`
    /// — twice. It slid in from the TOP of the wizard while every other screen in the suite put its
    /// feedback somewhere else entirely, and it had TWO early `return`s (one if `Step1Panel` was not
    /// found, one if its parent was not a Panel) that made the message vanish silently rather than
    /// show up somewhere imperfect. A user who pressed a button and saw nothing had no way to tell a
    /// no-op from a lost message.
    ///
    /// Every one of the 13 call sites is unchanged — they still call ShowToast(text). What changed is
    /// that they now produce the SAME float-up-and-fade notice as the Main App, the Speed Editor, the
    /// Merger, the Crop Tools and the Voice Over recorder.
    /// ⚠️ Do not reintroduce a bespoke toast here. See Controls/FloatingNotice.cs.
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    private void ShowToast(string message)
        => Controls.FloatingNotice.Show(this, message);

    /// <summary>ISSUE_09 — the same notice in the "that worked" colour.</summary>
    private void ShowToastSuccess(string message)
        => Controls.FloatingNotice.Success(this, message);

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<Border>("TitleBarBorder");
        if (titleBar != null)
        {
            titleBar.IsHitTestVisible = true;
            titleBar.DoubleTapped += (s, e) =>
            {
                this.WindowState = this.WindowState == Avalonia.Controls.WindowState.Maximized 
                    ? Avalonia.Controls.WindowState.Normal 
                    : Avalonia.Controls.WindowState.Maximized;
                e.Handled = true;
            };
            titleBar.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount < 2)
                {
                    try { BeginMoveDrag(e); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
                }
            };
        }
    }
}


