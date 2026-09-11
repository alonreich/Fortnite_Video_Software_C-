using Avalonia.Platform.Storage;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FortniteVideoSoftware.App.Controls;
using FortniteVideoSoftware.App.Infrastructure;
using FortniteVideoSoftware.App.Services;
using FortniteVideoSoftware.App.ViewModels;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Media;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Threading;
using System;

namespace FortniteVideoSoftware.App;

public partial class MainWindow : Window
{
    private MpvVideoView? _videoHost;

    private PreviewDetachController? _previewDetach;
    private PreviewDetachController PreviewDetach => _previewDetach ??= BuildPreviewDetachController();
    private PreviewMonitorWindow? DetachedPreviewWindow => _previewDetach?.Monitor;
    public bool IsPreviewDetached => _previewDetach?.IsDetached == true;

    public MpvVideoView? ActiveVideoHost
    {
        get
        {
            return _videoHost;
        }
    }
    private bool _isSeeking = false;
    private double? _nextSeekTarget = null;


    private readonly MainViewModel _viewModel;
    private readonly ProjectRecoveryService _recoveryService;

    private double _trimStartMs { get => _viewModel.Timeline.TrimStartMs; set => _viewModel.Timeline.TrimStartMs = value; }
    private double _trimEndMs { get => _viewModel.Timeline.TrimEndMs; set => _viewModel.Timeline.TrimEndMs = value; }
    private bool _trimStartSet { get => _viewModel.Timeline.IsTrimStartSet; set => _viewModel.Timeline.IsTrimStartSet = value; }
    private bool _trimEndSet { get => _viewModel.Timeline.IsTrimEndSet; set => _viewModel.Timeline.IsTrimEndSet = value; }
    private double _thumbnailPosMs { get => _viewModel.Timeline.ThumbnailPosMs; set => _viewModel.Timeline.ThumbnailPosMs = value; }
    private bool _thumbnailSet { get => _viewModel.Timeline.IsThumbnailSet; set => _viewModel.Timeline.IsThumbnailSet = value; }
    private double _baseSpeed { get => _viewModel.Timeline.BaseSpeed; set => _viewModel.Timeline.BaseSpeed = value; }
    private double _freezeTimeMs { get => _viewModel.Timeline.FreezeTimeMs; set => _viewModel.Timeline.FreezeTimeMs = value; }
    private double _freezeDurationS { get => _viewModel.Timeline.FreezeDurationS; set => _viewModel.Timeline.FreezeDurationS = value; }
    private List<CutRange> _cuts => _viewModel.Timeline.Cuts;
    private List<SpeedSegment> _speedSegments => _viewModel.Timeline.SpeedSegments;
    private List<FortniteVideoSoftware.Core.Media.MemePlacement> _memePlacements => _viewModel.Timeline.MemePlacements;
    private string _hardwareMode { get => _viewModel.Export.HardwareMode; set => _viewModel.Export.HardwareMode = value; }
    private bool _isGranularSpeedActive { get => _viewModel.IsGranularSpeedActive; set => _viewModel.IsGranularSpeedActive = value; }
    private bool _isMusicActive { get => _viewModel.IsMusicActive; set => _viewModel.IsMusicActive = value; }
    private bool _exportedCleanSinceLastEdit { get => _viewModel.ExportedCleanSinceLastEdit; set => _viewModel.ExportedCleanSinceLastEdit = value; }
    private VoiceOverWindow.VoiceOverResult? _voiceOverResult { get => _viewModel.VoiceOverResult; set => _viewModel.VoiceOverResult = value; }
    private MusicWizardResult? _musicWizardResult { get => _viewModel.MusicWizardResult; set => _viewModel.MusicWizardResult = value; }

    public string OverlayText { get => _viewModel.OverlayText; set => _viewModel.OverlayText = value; }
    public bool IsPortraitMode { get => _viewModel.IsPortraitMode; set => _viewModel.IsPortraitMode = value; }
    public bool IsBossHp { get => _viewModel.IsBossHp; set => _viewModel.IsBossHp = value; }
    public bool IsTeammates { get => _viewModel.IsTeammates; set => _viewModel.IsTeammates = value; }
    public bool IsSpectating { get => _viewModel.IsSpectating; set => _viewModel.IsSpectating = value; }
    public bool IsEnableFade { get => _viewModel.IsEnableFade; set => _viewModel.IsEnableFade = value; }
    public bool IsAddMeme { get => _viewModel.IsAddMeme; set => _viewModel.IsAddMeme = value; }
    public int QualitySliderValue { get => _viewModel.QualitySliderValue; set => _viewModel.QualitySliderValue = value; }
    public int MainSpeedSliderValue { get => _viewModel.MainSpeedSliderValue; set => _viewModel.MainSpeedSliderValue = value; }
    public double MainVolume { get => _viewModel.MainVolume; set => _viewModel.MainVolume = value; }

    /// <summary>
    /// PREWARM_01 — the film-lane prewarm stays ASLEEP until the user has deliberately marked an
    /// end point. Loading a video sets a full-range trim automatically, and warming on that would
    /// spend a background render on a range most users immediately replace. Armed by MARK END and
    /// by the end-marker drag; reset when a different video is loaded.
    /// </summary>
    private bool _prewarmArmed = false;

    /// <summary>
    /// ISSUE_10 — the loaded clip's full length in milliseconds, cached the moment it becomes known.
    ///
    /// WHY THIS EXISTS: the export used to take the duration straight off the live mpv player
    /// (`ActiveVideoHost?.IpcClient?.Duration ?? 0.0`). If the player had been torn down or had not
    /// reported yet when PROCESS was pressed, that produced 0 — and with no trim end set, the
    /// exporter was told the clip ends at 0 ms. FFmpeg was then asked for a zero-length segment and
    /// the user got a cryptic encoder failure instead of their video. This field survives the
    /// player, so the export always has a real number to fall back on.
    /// </summary>
    private double _loadedVideoDurationMs = 0;

    private NAudio.Wave.WaveOutEvent? _voiceOverPlayer;
    private NAudio.Wave.AudioFileReader? _voiceOverReader;
    private readonly List<VoiceOverPreviewTake> _voiceOverPreviewTakes = new();
    private Func<double, double>? _voiceOverPreviewTimeMapper;

    private sealed class VoiceOverPreviewTake
    {
        public required VoiceOverTake Take { get; init; }
        public required NAudio.Wave.WaveOutEvent Player { get; init; }
        public required NAudio.Wave.AudioFileReader Reader { get; init; }
        public double StartProjectSec { get; set; }
    }

    private Avalonia.Controls.Control? _thumbnailCameraControl;

    private bool _isCurrentlyFrozen = false;
    private DateTime _freezeStartTime;
    private double _previousVolume = 100;


    private string FormatTime(TimeSpan time, bool includeMilliseconds = false)
    {
        double dur = ActiveVideoHost?.IpcClient?.Duration ?? 0;
        bool showHours = dur >= 3600 || time.TotalHours >= 1;

        if (showHours)
            return includeMilliseconds ? time.ToString("hh\\:mm\\:ss\\.ff") : time.ToString("hh\\:mm\\:ss");
        else
            return includeMilliseconds ? time.ToString("mm\\:ss\\.ff") : time.ToString("mm\\:ss");
    }

    private void SetTrimStart(double valueMs)
    {
        _trimStartMs = valueMs;
        if (_musicWizardResult != null && _isMusicActive)
        {
            double dur = _musicWizardResult.TimelineEndSeconds - _musicWizardResult.TimelineStartSeconds;
            _musicWizardResult.TimelineStartSeconds = valueMs / 1000.0;
            _musicWizardResult.TimelineEndSeconds = (valueMs / 1000.0) + dur;
        }
        SchedulePrewarm();
    }

    /// <summary>
    /// PREWARM_01 — the moment the trim range is known, start building the film lane every later
    /// screen will want. Called from every place the trim can change; the service debounces, so a
    /// marker drag firing this per pointer-move costs one render, not one per frame.
    /// Fire-and-forget by design: it never blocks, never reports, and a failure is invisible
    /// because every consumer can still render the lane itself.
    /// The duration MUST be computed the same way the consumers compute theirs
    /// (`GranularSpeedEditorWindow.GetDuration`, VoiceOver's `durationSec`) or the key will not
    /// match and the prewarm is silently wasted rather than wrong.
    private static string ResolveFfmpegPath() => ExportViewModel.ResolveFfmpegPath();

    private void SchedulePrewarm()
    {
        try
        {
            if (!_prewarmArmed) return;
            if (string.IsNullOrWhiteSpace(_loadedVideoPath)) return;
            if (_trimEndMs <= _trimStartMs) return;

            FortniteVideoSoftware.App.Services.FilmstripPrewarm.Schedule(
                ResolveFfmpegPath(),
                _loadedVideoPath,
                _trimStartMs / 1000.0,
                (_trimEndMs - _trimStartMs) / 1000.0);
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private bool _draggingStartMarker = false;
    private bool _draggingEndMarker = false;
    private bool _qualitySliderInitialized = false;
    private bool _draggingMusicStart = false;
    private bool _draggingMusicEnd = false;
    private bool _draggingMusicBlock = false;
    private double _playingMusicTimelineStartSeconds = -1;
    private FortniteVideoSoftware.Core.Media.MpvIpcClient? _musicPreviewIpcClient;
    private bool _isMusicPreviewPlaying = false;
    private double _lastMusicPreviewSyncTime = -1;
    private DispatcherTimer? _playbackTimer;
    private bool _isTimerUpdatingSlider = false;
    private readonly ApplicationPaths _paths = ApplicationPaths.CreateDefault();

    private bool _isMusicBlockFocused = false;

    /// <summary>
    /// Set by the music block's own PointerPressed to stop the window-level "click elsewhere =
    /// deselect" handler from immediately undoing the selection it just made.
    ///
    /// Needed because that window handler now listens with handledEventsToo:true (so the trim
    /// markers can mark their press Handled and keep their pointer capture). Without this guard
    /// the sequence would be: musicRect sets focus TRUE -> event bubbles -> window handler sees a
    /// focused block and sets it FALSE -> the block could never stay selected, and Delete would
    /// stop working on it. Consumed by the very next bubble of the same gesture, which is
    /// guaranteed to happen because the window is the root ancestor.
    /// </summary>
    private bool _suppressNextMusicDeselect = false;
    private Avalonia.Controls.Shapes.Rectangle? _musicBlockRectRef;
    private Avalonia.Controls.Control? _musicStartPopupRef;
    private Avalonia.Controls.Control? _musicEndPopupRef;
    private Avalonia.Controls.Shapes.Rectangle? _regionRectRef;
    private DispatcherTimer? _marchingAntsTimer;
    private double _marchingAntsOffset = 0;
    private bool _isThumbnailMarkerSelected = false;
    private bool _isDraggingThumbnailMarker = false;
    private Avalonia.Controls.Shapes.Rectangle? _thumbnailMarkerIconAntsRef;
    private Avalonia.Controls.Shapes.Rectangle? _thumbnailMarkerLineAntsRef;
    private bool? _keepMusicDuringMeme;

    private bool _isTimelineDrawn = false;
    private string _loadedVideoPath = string.Empty;
    private System.Threading.CancellationTokenSource? _processCts;
private readonly RecoveryManager _recovery = new RecoveryManager();

/// <summary>
/// RECOVERY / WRONG-STATE FIX. True from the moment an export finishes successfully until the
/// user makes their next real edit.
///
/// THE BUG THIS CLOSES. The success path showed FinishedDialogWindow and never cleared the
/// recovery state, while every edit flag (_trimStartSet, _isMusicActive, _speedSegments, ...)
/// stayed set. HasUnsavedWork() therefore still reported TRUE for work that had just been
/// exported and saved, which produced three separate wrong behaviours:
///   1. switching to Video Merger / Crop Tools re-saved recovery data for finished work and
///      then asked "you have unsaved work, are you sure?" about a file already on disk;
///   2. the same switch path wrote a fresh state file on the way out;
///   3. the NEXT launch saw that state file, RecoveryManager.CheckFault() returned true, and the
///      user was asked to "restore your previous work" after a session that completed perfectly.
///
/// Clearing the state file alone is enough to silence CheckFault (it early-returns when the file
/// is absent), but not enough on its own: SaveRecoveryState is wired to ~25 UI events and the
/// very next one would re-arm it, because HasUnsavedWork was still true. Hence this flag, which
/// HasUnsavedWork honours and which the first genuine edit clears.
/// </summary>
    private bool _isRestoring = false;

    private KineticScrubController? _kineticScrub;


    public MainWindow()
    {
        RuntimeLog.Info("UI", "Initializing MainWindow");
        InitializeComponent();

        _viewModel = new MainViewModel(_paths);
        _recoveryService = new ProjectRecoveryService(_paths);
        DataContext = _viewModel;

        WireComponents();

        // AUTO-UPDATE — silent, fully-guarded background check a few seconds after the window
        // settles. Every guard (Settings toggle, dev mode, 24h throttle, strict newer-version
        // comparison, per-version skip memory) lives inside UpdateService; this hook only
        // supplies the owner window the prompts need. Fire-and-forget on purpose.
        this.Opened += (s, e) => { _ = FortniteVideoSoftware.App.Services.UpdateService.RunStartupCheckAsync(this); };
    }

    /// <summary>
    /// ISSUE_03 — handles a file path pushed to us by another launch of the app. Arrives on a
    /// background pipe thread, so everything is marshalled to the UI thread.
    /// </summary>
    private void OnVideoHandedOffFromAnotherLaunch(string path)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                Activate();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

                if (string.IsNullOrEmpty(path)) return;

                RuntimeLog.Info("SingleInstance", $"Loading handed-off video: {Path.GetFileName(path)}");
                await LoadVideoIntoEditorAsync(path, "opened-with-handoff");
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("SingleInstance", ex);
                await ErrorReporter.ShowAsync(this, "Could not open that video",
                    "The app was asked to open a video from Windows Explorer, but it could not be loaded.",
                    ex.ToString());
            }
        });
    }

    private List<MemeItem> _memeItems = new();
    private string? _pendingMemeRestorePath;

    private Task<double> ProbeMusicDurationSecondsAsync(string musicPath)
        => MemeManagementService.ProbeMusicDurationSecondsAsync(ResolveFfmpegPath(), musicPath);

    private async void PopulateMemeComboBox()
    {
        var cb = this.FindControl<ComboBox>("MemeComboBox");
        if (cb == null) return;

        _memeItems = await MemeManagementService.ScanMemesAsync();
        ApplyMemeItemsToCombo(preserveSelection: true);

        if (_pendingMemeRestorePath != null)
        {
            string p = _pendingMemeRestorePath;
            _pendingMemeRestorePath = null;
            var match = _memeItems.FirstOrDefault(m => string.Equals(m.FullPath, p, StringComparison.OrdinalIgnoreCase)
                                                    || string.Equals(m.FileName, Path.GetFileName(p), StringComparison.OrdinalIgnoreCase));
            if (match != null) cb.SelectedItem = match;
        }
    }

    private void ApplyMemeItemsToCombo(bool preserveSelection)
    {
        var cb = this.FindControl<ComboBox>("MemeComboBox");
        if (cb == null) return;

        cb.ItemTemplate = MemeManagementService.CreateMemeItemTemplate(this, () => IsPortraitMode);

        var prev = preserveSelection ? cb.SelectedItem as MemeItem : null;
        var list = new List<MemeItem>(_memeItems)
        {
            new MemeItem { IsDownloadAction = true, DownloadCategory = "mp4",  FileName = "Download more meme videos..." },
            new MemeItem { IsDownloadAction = true, DownloadCategory = "jpeg", FileName = "Download more meme pictures..." },
        };
        cb.ItemsSource = list;
        if (prev != null && !prev.IsDownloadAction)
        {
            var again = list.FirstOrDefault(m => !m.IsDownloadAction &&
                string.Equals(m.FullPath, prev.FullPath, StringComparison.OrdinalIgnoreCase));
            if (again != null) cb.SelectedItem = again;
        }
    }

    private async Task RunCloudMemeSyncAsync(string category)
    {
        bool pictures = string.Equals(category, "jpeg", StringComparison.OrdinalIgnoreCase);
        string label = pictures ? "meme pictures" : "meme videos";
        var (count, error) = await MemeManagementService.DownloadCloudMemesAsync(this, category);
        PopulateMemeComboBox();

        if (error != null)
        {
            ShowTacticalFeedback($"⚠ Could not finish downloading {label}");
            await ErrorReporter.ShowAsync(this, $"Problem downloading {label}", error,
                "See the log entries tagged [Memes] for the per-file detail.");
        }
        else if (count > 0)
        {
            ShowTacticalFeedback($"✓ Downloaded {count} new {label}");
        }
        else
        {
            ShowTacticalFeedback($"You already have all available {label}");
        }
    }
    private async Task PushAssetsAsync()
    {
        try
        {
            await Task.Run(() => {
                string musicFolder = Infrastructure.MemeDirectory.GetMusicRoot();
                string videosFolder = Infrastructure.MemeDirectory.GetVideosRoot();
                string memeFolder = Infrastructure.MemeDirectory.GetActive();

                if (!Directory.Exists(memeFolder)) Directory.CreateDirectory(memeFolder);

                string legacyMeme = Path.Combine(videosFolder, "MEME");
                if (Directory.Exists(legacyMeme) &&
                    !string.Equals(Path.GetFullPath(legacyMeme), Path.GetFullPath(memeFolder), StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var f in Directory.GetFiles(legacyMeme))
                    {
                        try
                        {
                            string dest = Path.Combine(memeFolder, Path.GetFileName(f));
                            if (!File.Exists(dest)) File.Copy(f, dest);
                        }
                        catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
                    }
                }

                Infrastructure.MemeAssets.DeliverStarter("mp3", musicFolder);
                Infrastructure.MemeAssets.DeliverStarter("mp4", memeFolder);
                Infrastructure.MemeAssets.DeliverStarter("jpeg", memeFolder);
            });
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("STARTUP", $"Failed to push assets: {ex.Message}");
        }
    }

    private void SpeakerIcon_Click(object? sender, RoutedEventArgs e)
    {
        ToggleMuteFromSpeakerIcon();
    }

    private void ToggleMuteFromSpeakerIcon()
    {
        var volumeSlider = this.FindControl<Slider>("VolumeSlider");
        if (volumeSlider != null)
        {
            if (volumeSlider.Value > 0)
            {
                _previousVolume = volumeSlider.Value;
                volumeSlider.Value = 0;
            }
            else
            {
                volumeSlider.Value = _previousVolume > 0 ? _previousVolume : 100;
            }
            SaveRecoveryState();
        }
    }

    private void SeekTimelineFromPointer(Avalonia.Input.PointerEventArgs e, Canvas timelineCanvas, Slider timelineSlider)
    {
        if (e.Handled) return;
        if (_draggingStartMarker || _draggingEndMarker) return;

        double duration = ActiveVideoHost?.IpcClient?.Duration ?? 0.0;
        double width = timelineCanvas.Bounds.Width;
        if (duration <= 0 || width <= 0) return;

        double x = Math.Clamp(e.GetPosition(timelineCanvas).X, 0, width);
        double sliderValue = (x / width) * 100.0;
        double targetTime = (sliderValue / 100.0) * duration;

        try
        {
            _isTimerUpdatingSlider = true;
            timelineSlider.Value = sliderValue;
        }
        finally
        {
            _isTimerUpdatingSlider = false;
        }

        _ = SeekInternal(targetTime);
        ShowPlayheadBadge(targetTime, sliderValue);
        e.Handled = true;
    }

    private Avalonia.Threading.DispatcherTimer? _playheadBadgeTimer;
    private void ShowPlayheadBadge(double timeSeconds, double sliderValuePercentage)
    {
        var badge = this.FindControl<Avalonia.Controls.Border>("PlayheadBadge");
        var text = this.FindControl<Avalonia.Controls.TextBlock>("PlayheadBadgeText");
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineMarkersCanvas");

        if (badge != null && text != null && canvas != null)
        {
            text.Text = FormatTime(TimeSpan.FromSeconds(timeSeconds), true);
            double canvasWidth = canvas.Bounds.Width;
            double x = (sliderValuePercentage / 100.0) * canvasWidth;
            Avalonia.Controls.Canvas.SetLeft(badge, x - 25);
            badge.Opacity = 1.0;

            if (_playheadBadgeTimer == null)
            {
                _playheadBadgeTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _playheadBadgeTimer.Tick += (s, ev) =>
                {
                    _playheadBadgeTimer.Stop();
                    badge.Opacity = 0.0;
                };
            }
            _playheadBadgeTimer.Stop();
            _playheadBadgeTimer.Start();
        }
    }

    private void UpdateTooltips()
    {
        var kb = SettingsManager.Instance.KeyBinds;

        var playPauseBtn = this.FindControl<Button>("PlayPauseButton");
        if (playPauseBtn != null) ToolTip.SetTip(playPauseBtn, $"Play or pause the video ({kb.PlayPause})");

        var markStartBtn = this.FindControl<Button>("MarkStartButton");
        if (markStartBtn != null) ToolTip.SetTip(markStartBtn, $"Mark the beginning of your clip ({kb.MarkStart})");

        var markEndBtn = this.FindControl<Button>("MarkEndButton");
        if (markEndBtn != null) ToolTip.SetTip(markEndBtn, $"Mark the end of your clip ({kb.MarkEnd})");
    }


    private void OnVideoDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(Avalonia.Input.DataFormats.Files) || e.Data.Contains(Avalonia.Input.DataFormats.FileNames) || e.Data.GetFiles()?.Any() == true)
        {
            e.DragEffects = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;
            var dropzone = this.FindControl<Controls.AmbientDropzoneControl>("AmbientDropzone");
            dropzone?.Activate();
            var uploadOverlay = this.FindControl<Border>("UploadOverlay");
            if (uploadOverlay != null && uploadOverlay.IsVisible)
            {
                uploadOverlay.Opacity = 0.15;
            }
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnVideoDragLeave(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var dropzone = this.FindControl<Controls.AmbientDropzoneControl>("AmbientDropzone");
        dropzone?.Deactivate();
        var uploadOverlay = this.FindControl<Border>("UploadOverlay");
        if (uploadOverlay != null && uploadOverlay.IsVisible)
        {
            uploadOverlay.Opacity = 0.95;
        }
    }

    private void OnVideoDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(Avalonia.Input.DataFormats.Files) || e.Data.Contains(Avalonia.Input.DataFormats.FileNames) || e.Data.GetFiles()?.Any() == true)
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private async void OnVideoDrop(object? sender, DragEventArgs e)
    {
        var dropzone = this.FindControl<Controls.AmbientDropzoneControl>("AmbientDropzone");
        dropzone?.Deactivate();
        var uploadOverlay = this.FindControl<Border>("UploadOverlay");
        if (uploadOverlay != null && uploadOverlay.IsVisible)
        {
            uploadOverlay.Opacity = 0.95;
        }

        var files = e.Data.GetFiles();
        if (files == null)
        {
            return;
        }

        await HandleExternalFileDropAsync(files.Select(f => f.Path.LocalPath).ToArray());
    }

    /// <summary>
    /// Shared entry point for externally dropped files (Avalonia OLE drop and the
    /// WM_DROPFILES fallback). Loads the first supported video straight into the
    /// preview player, exactly as if it had been uploaded.
    /// </summary>
    private async Task HandleExternalFileDropAsync(string[] paths)
    {
        foreach (var path in paths)
        {
            if (IsSupportedVideoPath(path))
            {
                await LoadVideoIntoEditorAsync(path, "dropped");
                return;
            }
        }

        ShowTacticalFeedback("Drop an MP4, MKV, AVI, or MOV file.");
        PlayUiSound();
    }

    private async void OnUploadVideoClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var options = new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Open Video (select multiple — the newest file is loaded)",
            AllowMultiple = true,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Video Files") { Patterns = new[] { "*.mp4", "*.mkv", "*.avi", "*.mov" } } }
        };

        try
        {
            string startPath = "";
            try
            {
                string stateFile = _paths.SessionStateFile;
                if (File.Exists(stateFile))
                {
                    var state = FortniteVideoSoftware.Core.Infrastructure.AtomicJsonFile.ReadObject(stateFile);
                    if (state != null && state.TryGetPropertyValue("UploadVideoDirectory", out var node) && node != null)
                    {
                        startPath = node.ToString();
                    }
                }

                if (string.IsNullOrEmpty(startPath) || !Directory.Exists(startPath))
                {
                    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string myVideos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                    string myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    string hardcodedVideos = System.IO.Path.Combine(userProfile, "Videos");
                    string[] probes = new[]
                    {
                        System.IO.Path.Combine(myVideos, "Fortnite"),
                        System.IO.Path.Combine(hardcodedVideos, "Fortnite"),
                        System.IO.Path.Combine(myVideos, "Highlights", "Fortnite"),
                        System.IO.Path.Combine(hardcodedVideos, "Highlights", "Fortnite"),
                        System.IO.Path.Combine(myVideos, "Highlights", "Fortnite"),
                        System.IO.Path.Combine(localAppData, "Temp", "Highlights", "Fortnite"),
                        System.IO.Path.Combine(localAppData, "Temp", "Highlights"),
                        System.IO.Path.Combine(localAppData, "NVIDIA Corporation", "GeForce Experience", "Highlights"),
                        System.IO.Path.Combine(myVideos, "Highlights"),
                        System.IO.Path.Combine(myDocuments, "Highlights")
                    };

                    startPath = myVideos;
                    foreach (var probe in probes)
                    {
                        if (System.IO.Directory.Exists(probe))
                        {
                            startPath = probe;
                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(startPath) && Directory.Exists(startPath))
                {
                    try 
                    {
                        var uri = new Uri(startPath);
                        options.SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(uri);
                    }
                    catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
                }
            }
            catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        }
        catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }

        SetTimelinePopupsVisible(false);
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(options);
        SetTimelinePopupsVisible(true);

        if (files.Count > 0)
        {
            string selectedPath = files
                .Select(f => f.Path.LocalPath)
                .Where(File.Exists)
                .OrderByDescending(p => { try { return File.GetLastWriteTimeUtc(p); } catch { return DateTime.MinValue; } })
                .FirstOrDefault() ?? files[0].Path.LocalPath;

            if (files.Count > 1)
            {
                RuntimeLog.Info("UI", $"User marked {files.Count} files; loading newest by modified time: {Path.GetFileName(selectedPath)}");
                RuntimeLog.Debug("UI", $"Full path: {selectedPath}");
            }

            try
            {
                string dir = Path.GetDirectoryName(selectedPath) ?? "";
                if (!string.IsNullOrEmpty(dir))
                {
                    System.Text.Json.Nodes.JsonObject state;
                    state = new System.Text.Json.Nodes.JsonObject
                    {
                        ["UploadVideoDirectory"] = dir
                    };
                    new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths).UpdatePropertiesSync(state);
                }
            }
            catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }

            await LoadVideoIntoEditorAsync(selectedPath, "uploaded");
        }
    }

    private static bool IsSupportedVideoPath(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".mp4" || ext == ".mkv" || ext == ".avi" || ext == ".mov";
    }

    private async Task LoadVideoIntoEditorAsync(string path, string source)
    {
        if (!IsSupportedVideoPath(path) || !File.Exists(path))
        {
            ShowTacticalFeedback("Choose an MP4, MKV, AVI, or MOV file.");
            PlayUiSound();
            return;
        }

        RuntimeLog.Info("UI", $"User {source} video: {Path.GetFileName(path)}");
        RuntimeLog.Debug("UI", $"Full path: {path}");
        ShowTacticalFeedback("Loading video...");

        ClearLiveZoomCrop();

        var videoHost = _videoHost;
        if (videoHost?.IpcClient == null)
        {
            ShowTacticalFeedback("Video player is still starting.");
            PlayUiSound();
            return;
        }

        _loadedVideoPath = path;
        _isTimelineDrawn = false;
        SaveUploadDirectory(path);
        ResetEditingStateForNewVideo();

        double previousDuration = videoHost.IpcClient.Duration;
        videoHost.IsVisible = true;
        await videoHost.IpcClient.LoadFileAsync(path);
        await videoHost.IpcClient.SetPropertyAsync("pause", "no");

        if (!await WaitForVideoMetadataAsync(previousDuration))
        {
            ShowTacticalFeedback("Video preview failed to load.");
            PlayUiSound();
            return;
        }

        ApplyDefaults();
        UpdateSpeedLabel();
        UpdateEstimatedQuality();
        UpdatePortraitOverlay();

        var uploadOverlay = this.FindControl<Border>("UploadOverlay");
        if (uploadOverlay != null) uploadOverlay.IsVisible = false;

        var timelineOverlay = this.FindControl<Border>("TimelineOverlay");
        if (timelineOverlay != null) timelineOverlay.IsVisible = true;

        EnableEditingControls();
        SaveRecoveryState();


        _ = RunAudioLoudnessCheckAsync(path);
    }


    /// <summary>The user's answer for THIS video. Null until the probe has finished, in which
    /// case the export falls back to the stored preference.</summary>
    private bool? _applyLoudnessNormalization;
    private bool? _applyPeakFlattening;

    /// <summary>The uploaded file's measured loudness, kept so the export can anchor the
    /// voice-over to the game even when the user declined normalisation.</summary>
    private double? _sourceMeasuredLufs;

    /// <summary>Cancels an in-flight probe when a second video is loaded over the first.</summary>
    private CancellationTokenSource? _loudnessProbeCts;

    private async Task RunAudioLoudnessCheckAsync(string path)
    {
        try { _loudnessProbeCts?.Cancel(); } catch (System.ObjectDisposedException) { }
        try { _loudnessProbeCts?.Dispose(); } catch (System.ObjectDisposedException) { }
        var cts = new CancellationTokenSource();
        _loudnessProbeCts = cts;

        _applyLoudnessNormalization = null;
        _applyPeakFlattening = null;
        _sourceMeasuredLufs = null;

        try
        {
            var settings = Infrastructure.SettingsManager.Instance;

            bool nothingToAsk =
                settings.LoudnessNormalizationPrompt != Infrastructure.AudioFixPrompt.Ask &&
                settings.PeakFlatteningPrompt != Infrastructure.AudioFixPrompt.Ask;

            if (nothingToAsk)
            {
                _applyLoudnessNormalization = settings.LoudnessNormalizationPrompt == Infrastructure.AudioFixPrompt.AlwaysApply;
                _applyPeakFlattening = settings.PeakFlatteningPrompt == Infrastructure.AudioFixPrompt.AlwaysApply;

                if (settings.LoudnessNormalizationPrompt == Infrastructure.AudioFixPrompt.AlwaysApply)
                    return;
            }

            string ffmpeg = FortniteVideoSoftware.Core.Infrastructure.BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
            var reading = await FortniteVideoSoftware.Core.Media.AudioLoudnessProbe
                .MeasureAsync(ffmpeg, path, cts.Token).ConfigureAwait(true);

            if (cts.IsCancellationRequested) return;

            if (reading == null) return;

            _sourceMeasuredLufs = reading.IntegratedLufs;

            if (!string.Equals(_loadedVideoPath, path, StringComparison.OrdinalIgnoreCase)) return;

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (reading.Verdict is FortniteVideoSoftware.Core.Media.LoudnessVerdict.TooQuiet
                                    or FortniteVideoSoftware.Core.Media.LoudnessVerdict.TooLoud)
                {
                    _applyLoudnessNormalization = await Controls.AudioFixPromptWindow.ResolveAsync(
                        this,
                        settings.LoudnessNormalizationPrompt,
                        () => Controls.AudioFixPromptWindow.ForLoudness(reading),
                        pref =>
                        {
                            settings.LoudnessNormalizationPrompt = pref;
                            Infrastructure.SettingsManager.Save();
                        },
                        "AudioLoudness");
                }

                if (reading.HasHarshPeaks)
                {
                    _applyPeakFlattening = await Controls.AudioFixPromptWindow.ResolveAsync(
                        this,
                        settings.PeakFlatteningPrompt,
                        () => Controls.AudioFixPromptWindow.ForHarshPeaks(reading),
                        pref =>
                        {
                            settings.PeakFlatteningPrompt = pref;
                            Infrastructure.SettingsManager.Save();
                        },
                        "AudioPeaks");
                }
            });
        }
        catch (OperationCanceledException) { /* superseded by a newer upload */ }
        catch (Exception ex)
        {
            RuntimeLog.Debug("AudioLoudness", $"Loudness check skipped: {ex.Message}");
        }
    }

    private void SaveUploadDirectory(string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths)
                    .UpdatePropertiesSync(new System.Text.Json.Nodes.JsonObject
                    {
                        ["UploadVideoDirectory"] = directory
                    });
            }
        }
        catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
    }

    private void ResetProjectStateToUpload()
    {
        _loadedVideoPath = string.Empty;
        if (ActiveVideoHost?.IpcClient != null)
        {
            _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
            ActiveVideoHost.IsVisible = false;
        }

        ResetEditingStateForNewVideo();
        UpdateTimelineMarkers();

        var uploadOverlay = this.FindControl<Border>("UploadOverlay");
        if (uploadOverlay != null) { uploadOverlay.IsVisible = true; uploadOverlay.Opacity = 0.95; }

        var detachBtnReset = this.FindControl<Button>("DetachOverlayButton");
        if (detachBtnReset != null) detachBtnReset.IsVisible = false;
        SetDetachMenuAvailable(false);
        var unlockHintReset = this.FindControl<TextBlock>("TrimUnlockHint");
        if (unlockHintReset != null) unlockHintReset.IsVisible = true;

        var timelineOverlay = this.FindControl<Border>("TimelineOverlay");
        if (timelineOverlay != null) timelineOverlay.IsVisible = false;

        var qualityPanel = this.FindControl<StackPanel>("QualityPanel");
        if (qualityPanel != null) qualityPanel.IsVisible = false;

        var speedPanel = this.FindControl<Grid>("SpeedPanel");
        if (speedPanel != null) speedPanel.IsVisible = false;

        var process = this.FindControl<Button>("ProcessButton");
        if (process != null) process.IsEnabled = false;
        var playPause = this.FindControl<Button>("PlayPauseButton");
        if (playPause != null) playPause.IsEnabled = false;
        var markStart = this.FindControl<Button>("MarkStartButton");
        if (markStart != null) markStart.IsEnabled = false;
        var markEnd = this.FindControl<Button>("MarkEndButton");
        if (markEnd != null) markEnd.IsEnabled = false;
        var thumb = this.FindControl<Button>("SetThumbnailButton");
        if (thumb != null) thumb.IsEnabled = false;
        var addMusic = this.FindControl<Button>("AddMusicButton");
        if (addMusic != null) addMusic.IsEnabled = false;
        var gran = this.FindControl<Button>("GranularButton");
        if (gran != null) gran.IsEnabled = false;
        var voBtn = this.FindControl<Button>("VoiceOverButton");
        if (voBtn != null) voBtn.IsEnabled = false;
        // CUT_02 — cuts belong to the Granular editor now, so there are no buttons to disable
        // here; the list itself still has to be cleared with the rest of the project state.
        _cuts.Clear();
        
        SaveRecoveryState();
    }

    private void ResetEditingStateForNewVideo()
    {
        _speedSegments.Clear();
        _musicWizardResult = null;
        StopMusicPreview();
        SetMusicButtonActive(false);
        _trimStartMs = 0;
        _trimEndMs = 0;
        _trimStartSet = false;
        _trimEndSet = false;
        _thumbnailSet = false;
        _thumbnailPosMs = 0;
        UpdateThumbnailButtonState();   // THUMB_01
        _freezeTimeMs = -1;
        _freezeDurationS = 1.0;
        ApplyVoiceOverState(null, isRestore: true);

        var addMemeCb = this.FindControl<ToggleSwitch>("AddMemeCheckbox");
        if (addMemeCb != null) addMemeCb.IsChecked = false;

        // THUMB_01 — the reset three lines above already put this button back through its one
        // owner; the hand-rolled duplicate that stood here is gone. Its label also lacked the
        // surrounding spaces the real one uses, so a reset button sat a few pixels narrower than
        // the same button in every other state.
        var markStartReset = this.FindControl<Button>("MarkStartButton");
        if (markStartReset != null) markStartReset.Content = "MARK START";
        var markEndReset = this.FindControl<Button>("MarkEndButton");
        if (markEndReset != null) markEndReset.Content = "MARK END";
        SetGranularButtonActive(false);
    }

    private async Task<bool> WaitForVideoMetadataAsync(double previousDuration)
    {
        if (ActiveVideoHost?.IpcClient == null) return false;

        for (int i = 0; i < 30; i++)
        {
            double duration = ActiveVideoHost.IpcClient.Duration;
            if (duration > 0 && (previousDuration <= 0 || Math.Abs(duration - previousDuration) > 0.01 || i >= 10))
            {
                return true;
            }

            await Task.Delay(100);
        }

        return ActiveVideoHost.IpcClient.Duration > 0;
    }

    /// <summary>
    /// Applies user-configured default values from Settings to the UI controls.
    /// </summary>
    private void ApplyDefaults()
    {
        var d = SettingsManager.Instance.Defaults;
        _baseSpeed = d.DefaultSpeed;
        var speedSliderReset = this.FindControl<SpinningWheelSlider>("MainSpeedSlider");
        if (speedSliderReset != null) speedSliderReset.Value = (int)Math.Round(_baseSpeed * 10.0, MidpointRounding.AwayFromZero);
        if (ActiveVideoHost?.IpcClient != null)
            _ = ActiveVideoHost.IpcClient.SetPropertyAsync("speed", _baseSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));

        var qs = this.FindControl<SpinningWheelSlider>("QualitySlider");
        if (qs != null && !_qualitySliderInitialized) 
        {
            qs.Value = d.QualityIndex;
            _qualitySliderInitialized = true;
        }

        var vol = this.FindControl<Slider>("VolumeSlider");
        if (vol != null) 
        {
            try
            {
                var state = System.IO.File.Exists(_paths.SessionStateFile)
                    ? System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(_paths.SessionStateFile))?.AsObject() ?? new System.Text.Json.Nodes.JsonObject()
                    : new System.Text.Json.Nodes.JsonObject();
                if (state.ContainsKey("MainVolume"))
                {
                    vol.Value = state["MainVolume"]?.GetValue<double>() ?? 100.0;
                }
                else
                {
                    vol.Value = 100.0;
                }
            }
            catch { vol.Value = 100.0; }
        }

        var portrait = this.FindControl<ToggleSwitch>("PortraitModeCheckbox");
        if (portrait != null) portrait.IsChecked = d.PortraitMode;

        var bossHp = this.FindControl<ToggleSwitch>("BossHpCheckbox");
        if (bossHp != null) bossHp.IsChecked = d.BossHp;

        var teammates = this.FindControl<ToggleSwitch>("TeammatesCheckbox");
        if (teammates != null) teammates.IsChecked = d.ShowTeammates;

        var enableFade = this.FindControl<ToggleSwitch>("EnableFadeCheckbox");
        if (enableFade != null) enableFade.IsChecked = d.EnableFade;

        // NOMASK_01 — MUST run last. The lines above restore BossHp/ShowTeammates from the saved
        // defaults, which would switch the HUD flags back ON underneath the reserved profile.
        ApplyMaskProfileToOverlayUi();
    }

    /// <summary>
    /// Enables all editing controls and right-pane checkboxes after a video is uploaded.
    /// This reverts the initial disabled state set in XAML (IsEnabled="False").
    /// </summary>
    /// <summary>
    /// UXQA_01: keeps "Detach Preview Monitor" in the menu greyed out until there is actually a clip
    /// to pop out, with a tooltip explaining why, instead of accepting the click and doing nothing.
    /// </summary>
    private void SetDetachMenuAvailable(bool available)
    {
        var menuTogglePreview = this.FindControl<MenuItem>("MenuTogglePreviewMonitor");
        if (menuTogglePreview == null) return;
        menuTogglePreview.IsEnabled = available;
        ToolTip.SetTip(menuTogglePreview, available
            ? "Pop out the video player into its own separate floating window so you can move it around."
            : "Load a video first — there is nothing to pop out yet.");
    }

    private void EnableEditingControls()
    {
        var detachBtn = this.FindControl<Button>("DetachOverlayButton");
        if (detachBtn != null) detachBtn.IsVisible = true;
        SetDetachMenuAvailable(true);

        var unlockHint = this.FindControl<TextBlock>("TrimUnlockHint");
        if (unlockHint != null) unlockHint.IsVisible = false;

        var gb = this.FindControl<Button>("GranularButton");
        if (gb != null) gb.IsEnabled = true;

        var thumbnail = this.FindControl<Button>("SetThumbnailButton");
        if (thumbnail != null) thumbnail.IsEnabled = true;

        var markStart = this.FindControl<Button>("MarkStartButton");
        if (markStart != null) markStart.IsEnabled = true;


        var playPause = this.FindControl<Button>("PlayPauseButton");
        if (playPause != null) playPause.IsEnabled = true;
        
        var voiceOver = this.FindControl<Button>("VoiceOverButton");
        if (voiceOver != null) voiceOver.IsEnabled = true;

        var markEnd = this.FindControl<Button>("MarkEndButton");
        if (markEnd != null) markEnd.IsEnabled = true;

        var process = this.FindControl<Button>("ProcessButton");
        if (process != null) process.IsEnabled = true;

        var addMusic = this.FindControl<Button>("AddMusicButton");
        if (addMusic != null) addMusic.IsEnabled = true;

        var portrait = this.FindControl<ToggleSwitch>("PortraitModeCheckbox");
        if (portrait != null) portrait.IsEnabled = true;

        var bossHp = this.FindControl<ToggleSwitch>("BossHpCheckbox");
        if (bossHp != null) bossHp.IsEnabled = true;

        var teammates = this.FindControl<ToggleSwitch>("TeammatesCheckbox");
        if (teammates != null) teammates.IsEnabled = true;

        var spectatingToggle = this.FindControl<ToggleSwitch>("SpectatingCheckbox");
        if (spectatingToggle != null) spectatingToggle.IsEnabled = true;

        var enableFade = this.FindControl<ToggleSwitch>("EnableFadeCheckbox");
        if (enableFade != null) enableFade.IsEnabled = true;

        var addMemeCb = this.FindControl<ToggleSwitch>("AddMemeCheckbox");
        if (addMemeCb != null) addMemeCb.IsEnabled = true;

        var qualityPanel = this.FindControl<StackPanel>("QualityPanel");
        if (qualityPanel != null) qualityPanel.IsVisible = true;
        var speedPanel = this.FindControl<Grid>("SpeedPanel");
        if (speedPanel != null) speedPanel.IsVisible = true;

        var qs = this.FindControl<SpinningWheelSlider>("QualitySlider");
        if (qs != null)
        {
            qs.IsEnabled = true;
            qs.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            Avalonia.Controls.ToolTip.SetTip(qs, "Target maximum file size in MB");
        }

        var ss = this.FindControl<SpinningWheelSlider>("MainSpeedSlider");
        if (ss != null)
        {
            ss.IsEnabled = true;
            ss.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            Avalonia.Controls.ToolTip.SetTip(ss, "Playback speed multiplier");
        }

        var speedPresets = this.FindControl<Grid>("MainSpeedPresetsPanel");
        if (speedPresets != null) speedPresets.IsEnabled = true;
    }

    private void UpdatePortraitOverlay()
    {
        var mobileCheckbox = (Avalonia.Controls.Primitives.ToggleButton?)this.FindControl<CheckBox>("MobileCheckbox") ?? this.FindControl<ToggleSwitch>("PortraitModeCheckbox");
        var portraitTextInput = this.FindControl<TextBox>("PortraitTextInput");

        bool isPortrait = mobileCheckbox?.IsChecked == true;

        if (portraitTextInput != null)
            portraitTextInput.IsVisible = isPortrait;

        var previewPortraitImage = this.FindControl<FortniteVideoSoftware.App.Controls.PhoneFrameMockup>("PhoneFrame")?.PortraitImageControl;
        if (previewPortraitImage != null && portraitTextInput != null && isPortrait)
        {
            try
            {
                string tempPngPath = System.IO.Path.Combine(_paths.TempDirectory, "preview_portrait_text.png");
                
                FortniteVideoSoftware.Core.Media.TextOverlayGenerator.GeneratePng(portraitTextInput.Text ?? "", tempPngPath);
                
                using (var stream = System.IO.File.OpenRead(tempPngPath))
                {
                    var bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                    var oldBitmap = previewPortraitImage.Source as Avalonia.Media.Imaging.Bitmap;
                    previewPortraitImage.Source = bitmap;
                    oldBitmap?.Dispose();
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("UI", ex);
            }
        }
        
        ApplyPortraitModeToActiveHost();
    }

    private void ApplyPortraitModeToActiveHost()
    {
        var mobileCheckbox = (Avalonia.Controls.Primitives.ToggleButton?)this.FindControl<CheckBox>("MobileCheckbox") ?? this.FindControl<ToggleSwitch>("PortraitModeCheckbox");
        bool isPortrait = mobileCheckbox?.IsChecked == true;
        bool isVideoLoaded = !string.IsNullOrEmpty(_loadedVideoPath);
        RuntimeLog.Info("UI", $"ApplyPortraitModeToActiveHost: evaluated isPortrait={isPortrait}, isVideoLoaded={isVideoLoaded}");

        var mainContainer = this.FindControl<Grid>("MainContainerGrid");
        if (mainContainer != null)
        {
            mainContainer.Height = isPortrait ? 1280 : 1080;
        }

        var phoneFrame = this.FindControl<FortniteVideoSoftware.App.Controls.PhoneFrameMockup>("PhoneFrame");
        if (phoneFrame != null)
        {
            phoneFrame.IsVisible = isPortrait && !IsPreviewDetached && isVideoLoaded;
        }

        if (_videoHost != null)
        {
            _videoHost.RenderTransform = null;
        }

        var detached = DetachedPreviewWindow;
        if (detached != null)
        {
            detached.TogglePortraitOverlay(isPortrait && isVideoLoaded);

            var previewPortraitImage = this.FindControl<FortniteVideoSoftware.App.Controls.PhoneFrameMockup>("PhoneFrame")?.PortraitImageControl;
            if (isPortrait && isVideoLoaded && previewPortraitImage != null)
            {
                detached.SetSkiaTextPlaceholder(previewPortraitImage.Source as Avalonia.Media.Imaging.Bitmap);
            }
        }
        
        if (ActiveVideoHost?.IpcClient != null)
        {
            _ = ActiveVideoHost.IpcClient.SetPropertyAsync("vf", "");
        }

        UpdateEstimatedQuality();
    }

    private void PlayUiSound()
    {
    }

    /// <summary>
    /// Shows tactical feedback text (e.g., "🏁 01:23.45") that MUST be visible on top of the
    /// playing video. Because MpvVideoView extends NativeControlHost, the native MPV HWND
    /// paints over ALL Avalonia overlays on Windows — including MPV's own OSD layer (which
    /// does not render in --wid embedded mode). The ONLY reliable way to show overlay text
    /// on top of the embedded native video surface is to use an Avalonia Popup, which creates
    /// a SEPARATE native popup window (WS_POPUP) with its own z-order that floats above the
    /// MPV HWND.
    ///
    /// The Popup is positioned at bottom-center of the video area (VerticalOffset=-80 from the
    /// bottom edge of VideoAreaBorder), matching the original bottom-center feedback position.
    /// Styling: golden text (#facc15) on 50% semi-transparent dark blue (#0f1b3a) background.
    /// </summary>
    /// <summary>
    /// ISSUE_05 — fills the cheat sheet from the user's OWN key binds, never the factory defaults.
    /// Rebuilt on every open so a rebind in Settings is reflected without restarting the app.
    /// </summary>

    private void BuildShortcutSheetRows()
    {
        var rows = this.FindControl<StackPanel>("ShortcutSheetRows");
        if (rows != null)
        {
            KeyboardShortcutService.BuildShortcutSheetRows(rows, this);
        }
    }
    private void ShowTacticalFeedback(string text)
        => Controls.FloatingNotice.Show(this, text);

    /// <summary>ISSUE_09 — the red variant, for an action the user attempted that could not run.</summary>
    private void ShowTacticalError(string text)
        => Controls.FloatingNotice.Error(this, text);

    private void ShowTimelineGlow(double timeMs, Avalonia.Media.IBrush color)
    {
        var canvas = this.FindControl<Canvas>("TimelineMarkersCanvas");
        if (canvas == null || ActiveVideoHost?.IpcClient == null) return;

        double duration = ActiveVideoHost.IpcClient.Duration;
        if (duration <= 0) return;

        double canvasWidth = canvas.Bounds.Width;
        double targetX = (timeMs / 1000.0 / duration) * canvasWidth;

        var glow = new Avalonia.Controls.Shapes.Rectangle
        {
            Fill = color,
            Width = 2,
            Height = canvas.Bounds.Height,
            Opacity = 0.8,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(glow, targetX - 1);
        canvas.Children.Add(glow);

        Task.Run(async () =>
        {
            double w = 2;
            double op = 0.8;
            while (op > 0)
            {
                w += 2;
                op -= 0.05;
                await Task.Delay(16);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    glow.Width = w;
                    glow.Opacity = op;
                    Canvas.SetLeft(glow, targetX - (w / 2));
                });
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => canvas.Children.Remove(glow));
        });
    }

    /// <summary>
    /// DETACH_01: builds this window's half of the shared detach mechanism. Everything about
    /// MOVING the preview is the controller's job; this supplies the Main App's own trimmings.
    /// </summary>
    private PreviewDetachController BuildPreviewDetachController()
    {
        var controller = new PreviewDetachController(
            this,
            PreviewDetachController.MainWindowKey,
            "Preview Monitor — Main App",
            () => _videoHost);

        controller.StateChanged += detached =>
        {
            var watermark = this.FindControl<Border>("PreviewDetachedWatermark");
            if (watermark != null) watermark.IsVisible = detached;

            controller.SyncButton(this.FindControl<Button>("DetachOverlayButton"));

            var menuTogglePreview = this.FindControl<MenuItem>("MenuTogglePreviewMonitor");
            if (menuTogglePreview != null) menuTogglePreview.Header = detached ? "Attach Preview Monitor" : "Detach Preview Monitor";

            if (!detached && _videoHost != null) _videoHost.IsVisible = true;

            ApplyPortraitModeToActiveHost();
        };

        return controller;
    }

    public Task AttachPreviewMonitor()
    {
        PreviewDetach.Attach();
        return Task.CompletedTask;
    }

    private Task DetachPreviewMonitor()
    {
        if (string.IsNullOrEmpty(_loadedVideoPath)) return Task.CompletedTask;
        PreviewDetach.Detach();
        return Task.CompletedTask;
    }


    private double CalculateEffectiveDurationMs()
    {
        _viewModel.Timeline.LoadedVideoDurationMs = _loadedVideoDurationMs;
        return _viewModel.Timeline.CalculateEffectiveDurationMs();
    }

    private void UpdateEstimatedQuality()
    {
        _viewModel.Timeline.LoadedVideoDurationMs = _loadedVideoDurationMs;
        _viewModel.Export.UpdateEstimatedQuality(_viewModel.Timeline.CalculateEffectiveDurationMs(), IsPortraitMode);
    }

    private List<SpeedSegment> BuildExportSpeedSegments() => _viewModel.Timeline.BuildExportSpeedSegments();

    private double SourceMsToOutputSeconds(double sourceMs, IReadOnlyList<SpeedSegment>? segments = null)
        => _viewModel.Timeline.SourceMsToOutputSeconds(sourceMs, segments);
    private void UpdateSpeedLabel()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            var label = this.FindControl<TextBlock>("MainSpeedLabel");
            if (label == null) return;

            double speed = _baseSpeed;
            string desc;
            string color;

            if (speed <= 0.5) { desc = "Slow Motion"; color = "#3498db"; }
            else if (speed <= 0.8) { desc = "Cinematic"; color = "#3498db"; }
            else if (speed < 1.05) { desc = "Normal"; color = "White"; }
            else if (speed <= 1.2) { desc = "Slight Boost"; color = "#f1c40f"; }
            else if (speed <= 1.5) { desc = "Fast"; color = "#f39c12"; }
            else if (speed <= 2.0) { desc = "Very Fast"; color = "#e67e22"; }
            else if (speed <= 3.0) { desc = "Turbo"; color = "#e74c3c"; }
            else { desc = "Extreme"; color = "#e74c3c"; }

            label.Text = $"{speed:F1}x — {desc}";
            label.Foreground = Avalonia.Media.Brush.Parse(color);
        });
    }

    private void ApplyMainSpeedPreset(double speed)
    {
        var speedSlider = this.FindControl<SpinningWheelSlider>("MainSpeedSlider");
        SpeedPresetButtons.SetSpinningWheelValue(speedSlider, speed);

        double previousSpeed = _baseSpeed;
        _baseSpeed = Math.Clamp(speed, 0.1, 4.0);
        if (ActiveVideoHost?.IpcClient != null)
        {
            _ = ActiveVideoHost.IpcClient.SetPropertyAsync(
                "speed",
                _baseSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (Math.Abs(previousSpeed - _baseSpeed) > 0.001)
            InvalidateVoiceOverRecordingForTimingChange();
        UpdateEstimatedQuality();
        UpdateSpeedLabel();
        SaveRecoveryState();
    }

    private void SetTimelinePopupsVisible(bool visible)
    {
        var canvas = this.FindControl<Canvas>("TimelineMarkersCanvas");
        if (canvas != null)
        {
            foreach (var child in canvas.Children)
            {
                if (child is Avalonia.Controls.Primitives.Popup popup)
                {
                    popup.IsOpen = visible;
                }
            }
        }
        if (_musicStartPopupRef != null) _musicStartPopupRef.IsVisible = visible;
        if (_musicEndPopupRef != null) _musicEndPopupRef.IsVisible = visible;
    }

    private void NormalizeMusicPlacement(MusicWizardResult result)
    {
        result.OffsetSeconds = Math.Max(0, result.OffsetSeconds);

        double videoStartSec = _trimStartSet ? _trimStartMs / 1000.0 : 0.0;
        double videoEndSec = _trimEndMs > _trimStartMs
            ? _trimEndMs / 1000.0
            : (ActiveVideoHost?.IpcClient?.Duration ?? 0.0);

        if (videoEndSec <= videoStartSec)
            videoEndSec = videoStartSec + Math.Max(1.0, ActiveVideoHost?.IpcClient?.Duration ?? 1.0);

        if (result.TimelineEndSeconds <= result.TimelineStartSeconds)
        {
            result.TimelineStartSeconds = videoStartSec;
            result.TimelineEndSeconds = videoEndSec;
        }

        result.TimelineStartSeconds = Math.Clamp(result.TimelineStartSeconds, 0, Math.Max(0, videoEndSec - 0.5));
        result.TimelineEndSeconds = Math.Clamp(result.TimelineEndSeconds, result.TimelineStartSeconds + 0.5, videoEndSec);
    }

    private async void StartMusicPreview(double currentVideoTimeSec)
    {
        if (_musicWizardResult == null || string.IsNullOrEmpty(_musicWizardResult.MusicFilePath)) return;

        double offsetFromMusicStart = _musicWizardResult.OffsetSeconds + (currentVideoTimeSec - _musicWizardResult.TimelineStartSeconds);
        if (offsetFromMusicStart < 0) offsetFromMusicStart = 0;
        if (_musicWizardResult.MusicDurationSeconds > 0 && offsetFromMusicStart >= _musicWizardResult.MusicDurationSeconds)
            return;

        string mpvExe = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? System.AppContext.BaseDirectory, "binaries", "mpv.exe");
        if (!System.IO.File.Exists(mpvExe)) mpvExe = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? System.AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "mpv.exe");
        if (!System.IO.File.Exists(mpvExe)) mpvExe = "mpv.exe";

        if (_musicPreviewIpcClient == null)
        {
            _musicPreviewIpcClient = new FortniteVideoSoftware.Core.Media.MpvIpcClient();
            await _musicPreviewIpcClient.StartAudioOnlyAsync(mpvExe);
        }

        var volSlider = this.FindControl<Avalonia.Controls.Slider>("VolumeSlider");
        double masterVol = volSlider?.Value ?? 100.0;
        double effectiveMusicVol = masterVol * _musicWizardResult.MusicVolume;

        await _musicPreviewIpcClient.SetPreviewVolumeAsync(effectiveMusicVol);
        await _musicPreviewIpcClient.LoadFileAsync(_musicWizardResult.MusicFilePath, offsetFromMusicStart);
        await _musicPreviewIpcClient.SetPropertyAsync("pause", "no");

        _isMusicPreviewPlaying = true;
        _lastMusicPreviewSyncTime = currentVideoTimeSec;
        _playingMusicTimelineStartSeconds = _musicWizardResult.TimelineStartSeconds;
    }

    private async void StopMusicPreview()
    {
        if (_musicPreviewIpcClient != null)
        {
            await _musicPreviewIpcClient.SetPropertyAsync("pause", "yes");
            await _musicPreviewIpcClient.SendCommandAsync("stop");
        }
        _isMusicPreviewPlaying = false;
    }

    private void ApplyMasterVolume(int masterVolumePercentage)
    {
        FortniteVideoSoftware.Core.Media.MpvIpcClient.SetGlobalMasterVolume(masterVolumePercentage);
    }

    private void AdjustPreviewMasterVolume(int delta)
    {
        var slider = this.FindControl<Slider>("VolumeSlider");
        if (slider != null)
            slider.Value = Math.Clamp(slider.Value + delta, slider.Minimum, slider.Maximum);
    }

    private void OnGlobalMasterVolumeChanged(int masterVolumePercentage)
    {
        double videoBase = 1.0;
        double musicBase = 1.0;

        if (_musicWizardResult != null && _isMusicActive)
        {
            videoBase = _musicWizardResult.VideoVolume;
            musicBase = _musicWizardResult.MusicVolume;
        }

        double effectiveVideoVol = masterVolumePercentage * videoBase;
        double effectiveMusicVol = masterVolumePercentage * musicBase;

        if (ActiveVideoHost?.IpcClient != null)
        {
            _ = ActiveVideoHost.IpcClient.SetPreviewVolumeAsync(effectiveVideoVol);
        }

        if (_musicPreviewIpcClient != null)
        {
            _ = _musicPreviewIpcClient.SetPreviewVolumeAsync(effectiveMusicVol);
        }
    }

    private void EnsureTrimPointsSet()
    {
        double reported = ActiveVideoHost?.IpcClient?.Duration ?? 0.0;
        if (reported > 0) _loadedVideoDurationMs = reported * 1000.0;

        if (!_trimStartSet && ActiveVideoHost?.IpcClient != null)
        {
            double dur = ActiveVideoHost.IpcClient.Duration;
            if (dur > 0)
            {
                _trimStartSet = true;
                _trimEndSet = true;
                _trimStartMs = 0;
                _trimEndMs = dur * 1000.0;
                _prewarmArmed = false;
                FortniteVideoSoftware.App.Services.FilmstripPrewarm.Clear();
                var markStartBtn = this.FindControl<Button>("MarkStartButton");
                if (markStartBtn != null) markStartBtn.Content = "MARK START [" + FormatTime(TimeSpan.Zero) + "]";
                var markEndBtn = this.FindControl<Button>("MarkEndButton");
                if (markEndBtn != null) markEndBtn.Content = $"MARK END [{FormatTime(TimeSpan.FromSeconds(dur))}]";
                UpdateTimelineMarkers();
                SaveRecoveryState();
            }
        }
    }

    private bool _tickFaultLogged;


    /// <summary>Last crop pushed to mpv, so an unchanged value is never re-sent every tick.</summary>
    private string _lastLiveCrop = "";

    private void UpdateLiveZoomCrop()
    {
        if (!FortniteVideoSoftware.Core.Media.VideoRenderMode.Current.UseHardwareAcceleration) return;

        var ipc = ActiveVideoHost?.IpcClient;
        if (ipc == null) return;

        double durSec = Math.Max(0.1, ((_trimEndMs > 0 ? _trimEndMs : ipc.Duration * 1000.0) - _trimStartMs) / 1000.0);
        double tSec = Math.Max(0, (ipc.CurrentTime * 1000.0) - _trimStartMs) / 1000.0;

        bool portrait = this.FindControl<ToggleSwitch>("PortraitModeCheckbox")?.IsChecked == true;
        var result = FortniteVideoSoftware.Core.Media.ZoomPreviewSimulator.Compute(
            _speedSegments, tSec, durSec, portrait, ipc.VideoWidth, ipc.VideoHeight, trimStartSec: _trimStartMs / 1000.0);

        if (!result.HasCrop) { ClearLiveZoomCrop(); return; }
        if (result.Crop == _lastLiveCrop) return;
        _lastLiveCrop = result.Crop;
        _ = ipc.SetPropertyAsync("video-crop", result.Crop);
    }

    /// <summary>
    /// Drops any simulated crop. MUST be called whenever the zoom data changes or the video is
    /// swapped, otherwise a stale crop from the previous clip stays on screen.
    /// </summary>
    private void ClearLiveZoomCrop()
    {
        if (_lastLiveCrop.Length == 0) return;
        _lastLiveCrop = "";
        _ = ActiveVideoHost?.IpcClient?.SetPropertyAsync("video-crop", "");
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        try { PlaybackTimerTickCore(); }
        catch (Exception ex)
        {
            if (!_tickFaultLogged)
            {
                _tickFaultLogged = true;
                RuntimeLog.Fail("UI", $"PlaybackTimer tick fault (sync loop kept alive): {ex}");
            }
        }
    }

    private void PlaybackTimerTickCore()
    {
        // ══════════════════════════════════════════════════════════════════════════════════
        // MEME_07 — BEFORE EVERYTHING ELSE ON THIS TICK, INCLUDING THE CUT SKIP.
        //
        // While a meme cutaway is on screen the ONE mpv host is showing the MEME, so CurrentTime,
        // Duration and IsEof all describe that file. Every line below this point would then be
        // reasoning about the wrong clock: the cut skip would seek somewhere arbitrary, the music
        // would resync to a meaningless position, the timeline slider would fly to the wrong
        // place, and the eof handler would pause the video for good at the end of the meme.
        // Returning early is what keeps all of that correct, and it is why the director owns the
        // whole cutaway rather than each of these features knowing about memes separately.
        // ══════════════════════════════════════════════════════════════════════════════════
        if (_memePlacements.Count > 0) EnsureMemePreviewDirector();
        if (_memePreview != null)
        {
            _memePreview.SetMemes(_memePlacements);
            _memePreview.Tick();
            if (_memePreview.IsActive) return;
        }

        // CUT_01 — SKIP, DO NOT PLAY THROUGH. When playback wanders into deleted footage the
        // player is seeked past it in ONE jump, so the user never watches frames that are not in
        // their video. Fire-and-forget on purpose: the seek is asynchronous and this tick must
        // never block the UI thread waiting on mpv (ZOOMHANG_01 — no unbounded wait may cross
        // between the render thread and the UI thread, in either direction).
        if (_cuts.Count > 0) _ = SkipPlayheadOutOfCutAsync();

        if (ActiveVideoHost?.IpcClient == null) return;

        // THUMB_01 — the button's label depends on where the playhead is, so it has to follow it.
        // Cheap by construction: it only writes when the text actually changes.
        UpdateThumbnailButtonState();

        UpdateLiveZoomCrop();

        var canvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineMarkersCanvas");
        if (canvas != null && canvas.Children.Count == 0)
        {
            UpdateTimelineMarkers();
        }

        var playIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon");
        var pauseIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PauseIcon");
        if (playIcon != null && pauseIcon != null)
        {
            bool isPaused = ActiveVideoHost.IpcClient.IsPaused;
            if (_isCurrentlyFrozen) isPaused = false;
            playIcon.IsVisible = isPaused;
            pauseIcon.IsVisible = !isPaused;
        }

        double time = ActiveVideoHost.IpcClient.CurrentTime;
        double dur = ActiveVideoHost.IpcClient.Duration;
        double displayTime = dur > 0 ? Math.Clamp(time, 0, dur) : Math.Max(0, time);

        bool videoEnded = ActiveVideoHost.IpcClient.IsEof || (dur > 0 && time >= dur - 0.05);

        if (_musicWizardResult != null && !string.IsNullOrEmpty(_musicWizardResult.MusicFilePath))
        {
            bool isPaused = ActiveVideoHost.IpcClient.IsPaused;
            if (_isCurrentlyFrozen) isPaused = false;
            double songTime = _musicWizardResult.OffsetSeconds + (time - _musicWizardResult.TimelineStartSeconds);
            bool songHasAudio = _musicWizardResult.MusicDurationSeconds <= 0 || songTime < _musicWizardResult.MusicDurationSeconds;
            bool shouldPlayMusic = !isPaused && !videoEnded && time >= _musicWizardResult.TimelineStartSeconds && time <= _musicWizardResult.TimelineEndSeconds && songHasAudio;
            bool isDraggingAnyMarker = _draggingStartMarker || _draggingEndMarker || _draggingMusicStart || _draggingMusicEnd || _draggingMusicBlock;

            if (shouldPlayMusic && _isMusicPreviewPlaying && (Math.Abs(time - _lastMusicPreviewSyncTime) > 0.5 || Math.Abs(_musicWizardResult.TimelineStartSeconds - _playingMusicTimelineStartSeconds) > 0.05))
            {
                StopMusicPreview();
            }

            if (shouldPlayMusic && !_isMusicPreviewPlaying && !isDraggingAnyMarker)
            {
                StartMusicPreview(time);
            }
            else if (!shouldPlayMusic && _isMusicPreviewPlaying)
            {
                StopMusicPreview();
            }

            if (_isMusicPreviewPlaying)
            {
                _lastMusicPreviewSyncTime = time;
            }
        }

        if (_voiceOverResult != null && _voiceOverPreviewTakes.Count > 0)
        {
            bool isPaused = ActiveVideoHost.IpcClient.IsPaused;
            if (_isCurrentlyFrozen) isPaused = false;

            double editedTime = GetEditedPreviewTimeSeconds(time);
            foreach (var take in _voiceOverPreviewTakes)
            {
                double voiceTime = editedTime - take.StartProjectSec;
                bool shouldPlayVoice = !isPaused && !videoEnded && voiceTime >= 0 && voiceTime <= take.Reader.TotalTime.TotalSeconds;

                if (shouldPlayVoice && take.Player.PlaybackState != NAudio.Wave.PlaybackState.Playing)
                {
                    take.Reader.CurrentTime = TimeSpan.FromSeconds(voiceTime);
                    take.Player.Play();
                }
                else if (!shouldPlayVoice && take.Player.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                {
                    take.Player.Pause();
                }
                else if (shouldPlayVoice && take.Player.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                {
                    if (Math.Abs(take.Reader.CurrentTime.TotalSeconds - voiceTime) > 0.5)
                    {
                        take.Reader.CurrentTime = TimeSpan.FromSeconds(voiceTime);
                    }
                }
            }
        }

        double currentAbsMs = time * 1000.0;

        if (_freezeTimeMs >= 0 && !_isCurrentlyFrozen && !ActiveVideoHost.IpcClient.IsPaused)
        {
            if (currentAbsMs >= _freezeTimeMs && currentAbsMs <= _freezeTimeMs + 150)
            {
                _isCurrentlyFrozen = true;
                _freezeStartTime = DateTime.UtcNow;
                _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
                return;
            }
        }
        else if (_isCurrentlyFrozen)
        {
            if ((DateTime.UtcNow - _freezeStartTime).TotalSeconds >= _freezeDurationS)
            {
                _isCurrentlyFrozen = false;
                _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "no");
            }
            else
            {
                return;
            }
        }

        if (!ActiveVideoHost.IpcClient.IsPaused && _speedSegments.Count > 0)
        {
            double targetSpeed = GetSpeedForPosition(currentAbsMs);
            if (Math.Abs(targetSpeed - _lastAppliedSpeed) > 0.001)
            {
                _lastAppliedSpeed = targetSpeed;
                _ = ActiveVideoHost.IpcClient.SetPropertyAsync("speed",
                    targetSpeed.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        var timeElapsed = this.FindControl<TextBlock>("TimeElapsed");
        if (timeElapsed != null) timeElapsed.Text = FormatTime(TimeSpan.FromSeconds(displayTime));

        var timelineSlider = this.FindControl<Slider>("TimelineSlider");
        if (timelineSlider != null && dur > 0)
        {
            if (!_isTimelineDrawn)
            {
                UpdateTimelineMarkers();
                _isTimelineDrawn = true;
            }
            _isTimerUpdatingSlider = true;
            timelineSlider.Value = Math.Clamp((time / dur) * 100.0, 0.0, 100.0);
            _isTimerUpdatingSlider = false;

            var timeRemaining = this.FindControl<TextBlock>("TimeRemaining");
            if (timeRemaining != null) timeRemaining.Text = "-" + FormatTime(TimeSpan.FromSeconds(Math.Max(0, dur - displayTime)));
        }

        if (ActiveVideoHost.IpcClient.IsEof)
        {
            _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
        }
    }

    /// <summary>
    /// G03 / issue 2 — resolves which encoder an export requests.
    ///
    /// This is now a thin wrapper over <see cref="ExportEncoderStrategy.Resolve"/>, which the
    /// Video Merger calls too. The precedence logic lives THERE, once, so the two applications
    /// can no longer drift apart — previously the Merger ignored both the Settings override and
    /// the boot scan and hardcoded "GPU".
    /// </summary>

    private string ResolveHardwareMode() => _viewModel.Export.ResolveHardwareMode();

    private async Task InitializeHardwareScanAsync() => await _viewModel.Export.InitializeHardwareScanAsync();

    private bool CheckRdpGpuBlocked() => ExportViewModel.CheckRdpGpuBlocked();

    private async void RdpFixButton_Click(object? sender, RoutedEventArgs e)
    {
        bool success = await _viewModel.Export.AutoFixRdpGpuPolicyAsync();
        if (success)
        {
            ShowTacticalFeedback("RDP policy updated! Please re-connect or reboot.");
            await InitializeHardwareScanAsync();
        }
        else
        {
            ShowTacticalFeedback("Failed to update RDP policy.");
        }
    }
    private double GetCurrentMpvTime() { return ActiveVideoHost?.IpcClient?.CurrentTime ?? 0.0; }

    /// <summary>
    /// Looks up the playback speed for a given absolute position (in ms).
    /// Returns the segment's speed if the position falls within a speed segment,
    /// otherwise returns the base speed. Freeze segments (speed ≈ 0) return 0.
    /// </summary>
    private double GetSpeedForPosition(double positionMs)
    {
        foreach (var seg in _speedSegments)
        {
            if (positionMs >= seg.StartMs && positionMs < seg.EndMs)
            {
                return seg.Speed;
            }
        }
        return _baseSpeed;
    }

    private async void InitializeMpv()
    {
        _videoHost = this.FindControl<MpvVideoView>("VideoHost");
        if (_videoHost != null)
        {
            string mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "frontend", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath)) mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath))
            {
                mpvPath = "mpv.exe";
            }
            try
            {
                await _videoHost.StartMpvProcessAsync(mpvPath);
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("UI", $"Video preview initialization failed. {ex}");
                ShowTacticalFeedback("Video preview unavailable on this hardware.");
                PlayUiSound();
                return;
            }

            if (_videoHost.IpcClient != null)
            {
                _videoHost.IpcClient.SeekCompleted -= OnSeekCompleted;
                _videoHost.IpcClient.SeekCompleted += OnSeekCompleted;
            }
        }
    }


    private void OnSeekCompleted()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () => {
            _isSeeking = false;
            if (_nextSeekTarget.HasValue) {
                double target = _nextSeekTarget.Value;
                _nextSeekTarget = null;
                await SeekInternal(target);
            }
        });
    }

    /// <summary>
    /// Coalescing seek. At most one seek is in flight; anything requested while one is running is
    /// remembered and issued when <see cref="OnSeekCompleted"/> fires.
    ///
    /// ===== ISSUE_06 — the "in flight" flag must only be raised if a seek is REALLY in flight ====
    /// WHAT WAS WRONG: `_isSeeking = true` was set BEFORE the player was checked for null. The
    /// only thing that ever clears that flag is the SeekCompleted callback, which the player
    /// raises. So whenever there was no player — preview failed to start, GPU-less/RDP machine
    /// where the fallback also failed, libmpv missing, or simply a seek attempted during teardown
    /// — the flag was raised and NOTHING could ever lower it again. From that instant the
    /// timeline slider, arrow-key frame stepping and every marker drag stopped moving the
    /// playhead, silently, for the rest of the session: the user dragged the scrubber and nothing
    /// happened, with no message anywhere.
    ///
    /// The check now happens FIRST and the flag is only raised once the command has actually been
    /// handed to the player. The try/catch is the matching guarantee for the other direction: if
    /// the command throws, the flag comes back down instead of wedging the timeline.
    /// </summary>
    /// <summary>
    /// RETURN_01 — park the preview at MARK START, paused, after any sub-editor closes.
    ///
    /// Coming back from the Granular editor, the Voice Over studio or the Music Wizard, the
    /// playhead was left wherever that window had wandered to — in practice the MARK END position,
    /// i.e. the very end of the clip. The user has just changed something and wants to watch it
    /// back, and the one thing they cannot do from the end is press play. Parking at the trim
    /// START with playback paused means the next click reviews the edit that was just made.
    ///
    /// Pause FIRST, then seek: seeking a playing clip lets it run on from the new position, which
    /// would immediately undo the parking.
    /// </summary>
    private void ReturnToTrimStartPaused()
    {
        var ipc = ActiveVideoHost?.IpcClient;
        if (ipc == null) return;

        try
        {
            _ = ipc.SetPropertyAsync("pause", "yes");
            _ = SeekInternal(_trimStartMs / 1000.0);
            ClearLiveZoomCrop();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UI", $"Could not park the preview at the trim start: {ex.Message}");
        }
    }

    private async Task SeekInternal(double time) {
        if (_isSeeking) {
            _nextSeekTarget = time;
            return;
        }

        var ipc = ActiveVideoHost?.IpcClient;
        if (ipc == null) {
            _nextSeekTarget = null;
            return;
        }

        _isSeeking = true;
        try {
            await ipc.SendCommandAsync("seek", time, "absolute");
        }
        catch (Exception ex) {
            _isSeeking = false;
            RuntimeLog.Fail("UI", $"Seek command failed: {ex.Message}");
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void OnSuccessAction(string action)
    {
        if (action == "whatsapp")
        {
            Process.Start(new ProcessStartInfo("cmd", "/c start whatsapp://send?text=CheckOutThisVideo") { CreateNoWindow = true });
        }
        else if (action == "folder")
        {
            Process.Start(new ProcessStartInfo("explorer.exe", ".") { CreateNoWindow = true });
        }

        ShutdownVideoPipeline();
        Environment.Exit(0);
    }

    private bool _isSafeToClose = false;
    private double _lastAppliedSpeed = SpeedPresetButtons.NativeDefaultSpeed;

    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        ForceReleaseDragStates(e.Pointer);
    }

    protected override void OnPointerCaptureLost(Avalonia.Input.PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        ForceReleaseDragStates(e.Pointer);
    }

    /// <summary>
    /// Safety net for a gesture that ended WITHOUT the hit box's own PointerReleased firing.
    ///
    /// THE BUG THIS FIXES ("MARK END leaves the caret stuck as if the button were still held"):
    /// the trim START/END markers are throwaway controls rebuilt by UpdateTimelineMarkers(). When
    /// that rebuild happens mid-gesture, the control currently holding pointer capture is removed
    /// from the visual tree, Avalonia raises PointerCaptureLost, and the hit box's own
    /// PointerReleased handler — the ONLY place that cleared `_draggingStartMarker` /
    /// `_draggingEndMarker` — never runs. This method used to clear only the three MUSIC flags, so
    /// the trim flags stayed true forever. From then on the freshly built hit box's PointerMoved
    /// saw `_draggingEndMarker == true` and dragged the marker on plain hover, with no button held:
    /// exactly "stuck as if held and not released".
    ///
    /// Every drag flag must be cleared here. If you add another, add it to this list too.
    /// </summary>
    private void ForceReleaseDragStates(Avalonia.Input.IPointer pointer)
    {
        bool needsUpdate = false;

        if (_draggingStartMarker || _draggingEndMarker)
        {
            _draggingStartMarker = false;
            _draggingEndMarker = false;
            needsUpdate = true;
        }

        if (_draggingMusicStart || _draggingMusicEnd || _draggingMusicBlock)
        {
            _draggingMusicStart = false;
            _draggingMusicEnd = false;
            _draggingMusicBlock = false;
            needsUpdate = true;
        }

        if (needsUpdate)
        {
            try { pointer?.Capture(null); } catch (System.Exception) { /* ISSUE_13: releasing a capture the OS already dropped. Nothing to report. */ }
            UpdateTimelineMarkers();
            SaveRecoveryState();
        }
    }

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Avalonia.Input.Key.Delete && _isMusicBlockFocused)
        {
            _musicWizardResult = null;
            SetMusicButtonActive(false);
            UpdateTimelineMarkers();
            e.Handled = true;
        }
    }

    protected override async void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        StopMusicPreview();
        if (_isSafeToClose)
        {
            base.OnClosing(e);
            return;
        }

        _recovery.MarkCleanShutdownIntent();

        e.Cancel = true;

        RuntimeLog.Info("UI", "Closing MainWindow. Saving state and cleaning up asynchronously.");

        try
        {
            if (_processCts != null && !_processCts.IsCancellationRequested)
            {
                try { _processCts.Cancel(); }
                catch (ObjectDisposedException) { }
            }


            await WindowBoundsHelper.SaveBoundsAsync(this, "MainWindowBounds");

            this.Hide();

            if (ActiveVideoHost?.IpcClient != null)
            {
                await ActiveVideoHost.IpcClient.SendCommandAsync("stop");
                ActiveVideoHost.IpcClient.Dispose();
            }
            ActiveVideoHost?.Dispose();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UI", $"Error saving state during close: {ex.Message}");
        }
        finally
        {
            try { _recovery.CleanupLock(); }
            catch (Exception ex) { RuntimeLog.Fail("UI", $"Recovery cleanup failed during close: {ex.Message}"); }

            _isSafeToClose = true;
            ShutdownVideoPipeline();
            Environment.Exit(0);
        }
    }

    private bool _videoPipelineShutDown;

    private void ShutdownVideoPipeline()
    {
        if (_videoPipelineShutDown) return;
        _videoPipelineShutDown = true;

        try { _previewDetach?.Attach(); }
        catch (Exception ex) { RuntimeLog.Fail("UI", $"Could not reattach the preview during shutdown: {ex.Message}"); }

        try { _videoHost?.Dispose(); }
        catch (Exception ex) { RuntimeLog.Fail("UI", $"Video preview teardown reported: {ex.Message}"); }

        try { _musicPreviewIpcClient?.Dispose(); }
        catch (Exception ex) { RuntimeLog.Fail("UI", $"Music preview teardown reported: {ex.Message}"); }
    }

    protected override void OnClosed(EventArgs e)
    {
        Controls.CoachOverlay.Cancel(this);
        Controls.FloatingNotice.Clear(this);
        base.OnClosed(e);
        SingleInstanceGuard.VideoPathReceived -= OnVideoHandedOffFromAnotherLaunch;
        SingleInstanceGuard.Release();
        FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged -= OnGlobalMasterVolumeChanged;
        DisposeVoiceOverPreviewTakes();
        ShutdownVideoPipeline();
        Environment.Exit(0);
    }

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<Border>("TitleBarBorder");
        if (titleBar != null)
        {
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


    /// <summary>
    /// ══════════════════════════════════════════════════════════════════════════════
    /// EDIT3_02 — "REMOVE SPEEDS" IS NOW "EDIT SPEEDS", AND IT IS NO LONGER RED.
    ///
    /// The red button was honest when the button really did wipe everything on one click. It stopped
    /// being honest the moment EDIT3_01 put a chooser in front of it: the button's job is now
    /// "open the thing you already made", and removing is one of three answers inside. A red button
    /// that opens a dialog trains people to fear a control that is safe, and the suite reserves red
    /// for actions that destroy data on the spot.
    ///
    /// GREEN, not blue, for the active state. Blue is the inactive state, so reusing it would make
    /// "you have speed changes" and "you have none" look identical. Green reads as "this is done" —
    /// the same thing the VOICE OVER button says by swapping its gradient for a flat fill and its
    /// label for EDIT VOICE OVER. All three entry buttons now behave the same way.
    /// ══════════════════════════════════════════════════════════════════════════════
    /// </summary>
    private void SetGranularButtonActive(bool active)
    {
        var btn = this.FindControl<Button>("GranularButton");
        if (btn == null) return;
        _isGranularSpeedActive = active;
        if (active)
        {
            btn.Classes.Remove("Primary");
            btn.Classes.Remove("Secondary");
            btn.Classes.Remove("Danger");
            if (!btn.Classes.Contains("Success")) btn.Classes.Add("Success");
            btn.Content = "EDIT SPEEDS";
            ToolTip.SetTip(btn, "You have speed changes on this video. Click to reopen the Speed Editor and change them, or to remove them all.");
        }
        else
        {
            btn.Classes.Remove("Danger");
            btn.Classes.Remove("Success");
            if (!btn.Classes.Contains("Primary")) btn.Classes.Add("Primary");
            btn.Content = "GRANULAR SPEED";
            ToolTip.SetTip(btn, "Adjust granular speed settings");
        }

        var mainSpeedSlider = this.FindControl<SpinningWheelSlider>("MainSpeedSlider");
        var presetsPanel = this.FindControl<Grid>("MainSpeedPresetsPanel");
        if (mainSpeedSlider != null)
        {
            mainSpeedSlider.IsEnabled = !active;
            mainSpeedSlider.Opacity = active ? 0.45 : 1.0;
            ToolTip.SetTip(mainSpeedSlider, active
                ? "Granular speed edits are active. Reopen EDIT SPEEDS to adjust speeds or remove granular segments."
                : "Slide left to make the video slow motion, or right to speed it up.");
        }
        if (presetsPanel != null)
        {
            presetsPanel.IsEnabled = !active;
            presetsPanel.Opacity = active ? 0.45 : 1.0;
            ToolTip.SetTip(presetsPanel, active
                ? "Granular speed edits are active. Reopen EDIT SPEEDS to adjust speeds or remove granular segments."
                : null);
        }

        SaveRecoveryState();
    }

    /// <summary>
    /// EDIT3_02 — "REMOVE MUSIC" is now "EDIT MUSIC" in green. See SetGranularButtonActive above
    /// for why the red went: the button opens a chooser now, it does not delete anything itself.
    /// </summary>
    private void SetMusicButtonActive(bool active)
    {
        var btn = this.FindControl<Button>("AddMusicButton");
        if (btn == null) return;
        var txt = this.FindControl<TextBlock>("AddMusicText");
        
        _isMusicActive = active;
        if (active)
        {
            btn.Classes.Remove("Primary");
            btn.Classes.Remove("Secondary");
            btn.Classes.Remove("Danger");
            if (!btn.Classes.Contains("Success")) btn.Classes.Add("Success");
            if (txt != null) txt.Text = " EDIT MUSIC ";
            ToolTip.SetTip(btn, "This video has background music. Click to reopen the wizard on your current setup and change it, or to take the music off.");
        }
        else
        {
            btn.Classes.Remove("Danger");
            btn.Classes.Remove("Success");
            if (!btn.Classes.Contains("Primary")) btn.Classes.Add("Primary");
            if (txt != null) txt.Text = " ADD MUSIC ";
            ToolTip.SetTip(btn, "Add background music to the video");
        }
        SaveRecoveryState();
    }


    /// <summary>
    /// Serializes all editing-session state to the crash-recovery file
    /// (recovery_v2.json) so it can be restored if the app crashes.
    /// Called on every meaningful state change (trim, speed, quality, etc.).
    /// </summary>
    private static bool HasVoiceOverWav(VoiceOverWindow.VoiceOverResult? result)
    {
        foreach (var take in GetExistingVoiceOverTakes(result))
        {
            return true;
        }

        return false;
    }

    private static List<VoiceOverTake> GetExistingVoiceOverTakes(VoiceOverWindow.VoiceOverResult? result)
    {
        var takes = new List<VoiceOverTake>();
        if (result == null) return takes;

        if (result.VoiceOverTakes != null)
        {
            foreach (var take in result.VoiceOverTakes)
            {
                if (!string.IsNullOrWhiteSpace(take.Path) && System.IO.File.Exists(take.Path))
                {
                    takes.Add(take);
                }
            }
        }

        if (takes.Count == 0 &&
            !string.IsNullOrWhiteSpace(result.VoiceOverWavPath) &&
            System.IO.File.Exists(result.VoiceOverWavPath))
        {
            takes.Add(new VoiceOverTake(result.VoiceOverWavPath!, result.VoiceOverStartTimestampSec));
        }

        return takes;
    }

    private static bool HasVoiceOverEffect(VoiceOverWindow.VoiceOverResult? result)
    {
        return HasVoiceOverWav(result) ||
               result?.DuckAudio == true;
    }

    private void DisposeVoiceOverPreviewTakes()
    {
        foreach (var take in _voiceOverPreviewTakes)
        {
            try { take.Player.Dispose(); } catch (System.Exception) { }
            try { take.Reader.Dispose(); } catch (System.Exception) { }
        }
        _voiceOverPreviewTakes.Clear();
    }

    private Func<double, double> GetVoiceOverPreviewTimeMapper()
    {
        if (_voiceOverPreviewTimeMapper != null)
        {
            return _voiceOverPreviewTimeMapper;
        }

        double durationMs = ActiveVideoHost?.IpcClient?.Duration > 0
            ? ActiveVideoHost.IpcClient.Duration * 1000.0
            : Math.Max(_trimEndMs, _trimStartMs + 1000.0);
        double endMs = _trimEndMs > _trimStartMs ? _trimEndMs : durationMs;
        double totalMs = Math.Max(1.0, endMs - _trimStartMs);

        _voiceOverPreviewTimeMapper = GranularSpeedBuilder.CreateTimeMapper(
            totalMs,
            BuildExportSpeedSegments(),
            _baseSpeed,
            _trimStartMs);

        foreach (var take in _voiceOverPreviewTakes)
        {
            take.StartProjectSec = _voiceOverPreviewTimeMapper(take.Take.StartSec);
        }

        return _voiceOverPreviewTimeMapper;
    }

    private double GetEditedPreviewTimeSeconds(double sourceTimeSec)
    {
        var mapper = GetVoiceOverPreviewTimeMapper();
        if (_isCurrentlyFrozen && _freezeTimeMs >= 0)
        {
            double freezeBaseSec = mapper(_freezeTimeMs / 1000.0);
            double elapsedFreezeSec = Math.Clamp((DateTime.UtcNow - _freezeStartTime).TotalSeconds, 0, Math.Max(0, _freezeDurationS));
            return freezeBaseSec + elapsedFreezeSec;
        }

        return mapper(sourceTimeSec);
    }

    private void ApplyVoiceOverState(VoiceOverWindow.VoiceOverResult? result, bool isRestore = false)
    {
        var oldResult = _voiceOverResult;
        _voiceOverResult = HasVoiceOverEffect(result) ? result : null;
        var btn = this.FindControl<Button>("VoiceOverButton");
        var text = this.FindControl<TextBlock>("VoiceOverText");

        _voiceOverPlayer?.Dispose();
        _voiceOverPlayer = null;
        _voiceOverReader?.Dispose();
        _voiceOverReader = null;
        DisposeVoiceOverPreviewTakes();
        _voiceOverPreviewTimeMapper = null;

        if (!isRestore && oldResult != null && oldResult != _voiceOverResult)
        {
            if (!string.IsNullOrEmpty(oldResult.VoiceOverWavPath))
            {
                try { System.IO.File.Delete(oldResult.VoiceOverWavPath); } catch {}
            }
            if (oldResult.VoiceOverTakes != null)
            {
                foreach (var take in oldResult.VoiceOverTakes)
                {
                    if (!string.IsNullOrEmpty(take.Path))
                    {
                        try { System.IO.File.Delete(take.Path); } catch {}
                    }
                }
            }
        }

        if (_voiceOverResult != null)
        {
            var takes = GetExistingVoiceOverTakes(_voiceOverResult);
            bool hasWav = takes.Count > 0;
            if (btn != null)
            {
                btn.Classes.Remove("VoiceOverEntry");
                btn.Classes.Remove("Danger");
                if (!btn.Classes.Contains("Primary")) btn.Classes.Add("Primary");
                ToolTip.SetTip(btn, hasWav ? "Edit or remove the current voiceover" : "Record your own voice over the video");
            }
            if (text != null) text.Text = hasWav ? "EDIT VOICE OVER" : "VOICE OVER";

            if (hasWav)
            {
                foreach (var take in takes)
                {
                    try
                    {
                        var reader = new NAudio.Wave.AudioFileReader(take.Path);
                        var player = new NAudio.Wave.WaveOutEvent();
                        player.Init(reader);
                        _voiceOverPreviewTakes.Add(new VoiceOverPreviewTake
                        {
                            Take = take,
                            Reader = reader,
                            Player = player
                        });
                    }
                    catch (Exception ex)
                    {
                        CoreLogger.Fail("MainWindow", $"Failed to load VoiceOver preview take '{Path.GetFileName(take.Path)}': {ex.Message}");
                        CoreLogger.Debug("MainWindow", $"Failed to load VoiceOver preview take path '{take.Path}': {ex}");
                    }
                }
            }
        }
        else
        {
            _voiceOverResult = null;
            if (btn != null)
            {
                btn.Classes.Remove("Danger");
                btn.Classes.Remove("Primary");
                if (!btn.Classes.Contains("VoiceOverEntry")) btn.Classes.Add("VoiceOverEntry");
                ToolTip.SetTip(btn, "Open Voice Over Studio");
            }
            if (text != null) text.Text = "VOICE OVER";
        }

        if (!isRestore) SaveRecoveryState();
    }

    private void InvalidateVoiceOverRecordingForTimingChange()
    {
        if (!HasVoiceOverWav(_voiceOverResult)) return;
        
        _voiceOverPreviewTimeMapper = null;
        GetVoiceOverPreviewTimeMapper();
    }

    private Avalonia.Threading.DispatcherTimer? _recoveryDebounceTimer;

    /// <summary>
    /// Debounced recovery save for high-frequency UI changes (volume drags, typing).
    /// Coalesces rapid changes into a single disk write ~600ms after the last change,
    /// instead of a full serialize + fsync on every value tick (ISSUE_104).
    /// </summary>
    private void ScheduleRecoveryStateSave()
    {
        if (_recoveryDebounceTimer == null)
        {
            _recoveryDebounceTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600)
            };
            _recoveryDebounceTimer.Tick += (s, e) =>
            {
                _recoveryDebounceTimer!.Stop();
                SaveRecoveryState();
            };
        }
        _recoveryDebounceTimer.Stop();
        _recoveryDebounceTimer.Start();
    }


    private async Task SwitchToCompanionAppAsync(string argument, string toolDisplayName)
    {
        ShowCompanionHandoffOverlay(toolDisplayName);
        SaveRecoveryState(sync: true, isUserEdit: false);

        var payload = new FortniteVideoSoftware.Core.Ipc.HandoffPayload
        {
            SourceProcess = "MainWindow",
            TargetProcess = toolDisplayName,
            SelectedClipPath = _loadedVideoPath,
            SelectedClipStartMs = _trimStartMs,
            SelectedClipEndMs = _trimEndMs
        };

        var companionService = new CompanionAppService(_paths);
        bool launched = await companionService.SwitchToCompanionAppAsync(
            argument,
            toolDisplayName,
            payload,
            () =>
            {
                _recovery.MarkCleanShutdownIntent();
                ShutdownVideoPipeline();
            });

        if (launched)
        {
            Close();
        }
        else
        {
            HideCompanionHandoffOverlay();
        }
    }
    private void ShowCompanionHandoffOverlay(string toolName)
    {
        try
        {
            if (this.Content is not Panel rootPanel) return;

            var card = new StackPanel
            {
                Spacing = 16,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            card.Children.Add(new TextBlock
            {
                Text = $"Opening {toolName}…",
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(20),
                FontWeight = Avalonia.Media.FontWeight.Bold,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Foreground = Infrastructure.ThemeResources.Brush(this, "AppTextPrimaryBrush", new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#ffffff")))
            });
            card.Children.Add(new ProgressBar
            {
                IsIndeterminate = true,
                Width = 240,
                Height = 4
            });
            card.Children.Add(new TextBlock
            {
                Text = "Your edits have been saved and will be waiting when you come back.",
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(12),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                MaxWidth = 420,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Foreground = Infrastructure.ThemeResources.Brush(this, "AppTextMutedBrush", new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#b6c2d0")))
            });

            _companionHandoffOverlay = new Border
            {
                Background = Infrastructure.ThemeResources.Brush(this, "AppDimmerHeavyBrush", new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#C8000000"))),
                ZIndex = int.MaxValue,
                Child = card
            };
            rootPanel.Children.Add(_companionHandoffOverlay);
        }
        catch (System.Exception ex)
        {
            RuntimeLog.Debug("UI", $"Handoff overlay could not be shown: {ex.Message}");
        }
    }

    private void HideCompanionHandoffOverlay()
    {
        try
        {
            if (_companionHandoffOverlay != null && this.Content is Panel rootPanel)
            {
                rootPanel.Children.Remove(_companionHandoffOverlay);
            }
            _companionHandoffOverlay = null;
        }
        catch (System.Exception ex) { RuntimeLog.Debug("UI", $"Handoff overlay could not be hidden: {ex.Message}"); }
    }

    private Border? _companionHandoffOverlay;

    /// <summary>
    /// ISSUE_02 / RECOVERY_03 / RECOVERY_04 — "is there anything worth recovering?", in ONE place.
    ///
    /// <para>
    /// RECOVERY_03: the speed test used to be <c>Math.Abs(_baseSpeed - 1.0) &gt; 0.01</c>, but the
    /// app's default speed is 1.1 (<c>SpeedPresetButtons.NativeDefaultSpeed</c>). |1.1-1.0| = 0.1,
    /// so the condition was TRUE from the instant the window opened, for every user on the
    /// defaults — permanently. Two opposite failures came out of that one wrong constant: the
    /// ClearState() branch in SaveRecoveryState was effectively dead, so a stale recovery file
    /// lingered after the user removed their last edit; and the moment anyone chose exactly 1.0x —
    /// an entirely normal choice — the test flipped to false and the file was DELETED, taking
    /// everything in RECOVERY_04 with it. Compare against the real default, so this means what it
    /// says.
    /// </para>
    /// <para>
    /// RECOVERY_04: the meme choice and the HUD/portrait toggles are all written into the recovery
    /// payload, yet none of them used to count as "work". If they were the user's only edits the
    /// app decided there was nothing to keep and ERASED the file — and since the toggle handlers
    /// call SaveRecoveryState() on every click, ticking Boss HP actively destroyed the recovery
    /// state instead of saving it. They are counted here.
    /// ⚠️ These two fixes are a PAIR. RECOVERY_03 alone would have exposed RECOVERY_04 immediately;
    /// RECOVERY_04 alone leaves the dead-branch half of RECOVERY_03 in place.
    /// </para>
    /// <para>
    /// ⚠️ A LOADED VIDEO IS NOT WORK. Uploading a clip and touching nothing else must return false —
    /// that is exactly the case the Video Merger / Crop Tools switch prompt must NOT interrupt.
    /// Do not add "_videoPath != null" to this expression.
    /// </para>
    /// </summary>

    private bool HasUnsavedWork() => _recoveryService.HasUnsavedWork(_viewModel, _viewModel.Timeline, _viewModel.Export);

    private void SaveRecoveryState(bool sync = false, bool isUserEdit = true)
    {
        if (_isRestoring) return;
        try
        {
            if (isUserEdit && _exportedCleanSinceLastEdit)
            {
                _exportedCleanSinceLastEdit = false;
                RuntimeLog.Info("RECOVERY", "Edit made after a successful export - project is dirty again, recovery re-armed.");
            }

            if (!HasUnsavedWork())
            {
                _recovery.ClearState();
                return;
            }

            RuntimeLog.Info("RECOVERY",
                $"Saving project state: trim[{_trimStartMs:F0}-{_trimEndMs:F0}ms set={_trimStartSet}/{_trimEndSet}] " +
                $"speed[base={_baseSpeed:F2}x segs={_speedSegments.Count}] " +
                $"freeze[at={_freezeTimeMs:F0}ms for={_freezeDurationS:F2}s] " +
                $"cuts[{_cuts.Count} removing {RemovedCutSeconds():F2}s] " +
                $"thumb[set={_thumbnailSet} at={_thumbnailPosMs:F0}ms] " +
                $"voiceOver[{(_voiceOverResult != null ? "yes" : "no")}] " +
                $"music[{(_musicWizardResult != null ? "yes" : "no")}].");

            var state = _recoveryService.SerializeState(_viewModel, _viewModel.Timeline, _viewModel.Export);
            _recoveryService.SaveState(state, sync);
        }
        catch (System.Exception ex)
        {
            RuntimeLog.Fail("RECOVERY", ex);
        }
    }

    private async void OnExportConfigClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await SettingsBackupService.ExportSettingsAsync(this, _paths);
    }

    private async void OnImportConfigClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await SettingsBackupService.ImportSettingsAsync(this, _paths, () =>
        {
            _recovery.MarkCleanShutdownIntent();
            ShutdownVideoPipeline();
        });
    }

    /// <summary>
    /// NOMASK_01 — refuses the Crop Tools hand-off while the reserved "No Mask Profile" is active,
    /// and tells the user how to get out of it. Returns true when the caller must abort.
    ///
    /// Why block rather than open read-only: Crop Tools is a SEPARATE PROCESS that closes the Main
    /// App to start (see Section 2 of project_structure.txt). Launching it only to disable every
    /// control would cost the user their whole session for a window that can do nothing.
    /// </summary>
    private bool BlockCropToolsForNoMaskProfile()
    {
        if (!FortniteVideoSoftware.App.Infrastructure.MaskOverlayManager.IsNoMask(
                FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.ActiveMaskOverlay))
        {
            return false;
        }

        RuntimeLog.Info("UI", "Crop Tools blocked: the reserved No Mask Profile is active.");
        NativeDialog.ShowError(
            "\"" + FortniteVideoSoftware.App.Infrastructure.MaskOverlayManager.NoMaskProfileName + "\" has no overlay elements to edit.\n\n" +
            "It converts your video to portrait with the text strip and nothing else, on purpose.\n\n" +
            "To edit overlay positions, pick a different profile first: Settings \u2192 Mask Overlay.",
            "Nothing to Edit");
        return true;
    }

    /// <summary>
    /// NOMASK_01 — collapses the IN-GAME OVERLAYS block (and its separator) while the reserved
    /// "No Mask Profile" is active, and forces the three HUD toggles off so a stale ON state
    /// cannot survive the profile switch into an export payload.
    ///
    /// Forcing the toggles off is belt-and-braces, not the mechanism: the profile carries zero-size
    /// rects, so MobileFilterBuilder.RegisterLayer skips every layer regardless of these flags.
    /// It matters for what gets SAVED — the toggles are persisted in the recovery state and read
    /// back by BuildExportPayload — so leaving them ON would silently re-enable the HUD the moment
    /// the user switches back to a masked profile.
    /// </summary>
    private void ApplyMaskProfileToOverlayUi()
    {
        try
        {
            bool noMask = FortniteVideoSoftware.App.Infrastructure.MaskOverlayManager.IsNoMask(
                FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.ActiveMaskOverlay);

            var panel = this.FindControl<StackPanel>("InGameOverlaysPanel");
            if (panel != null) panel.IsVisible = !noMask;

            var sep = this.FindControl<Separator>("InGameOverlaysSeparator");
            if (sep != null) sep.IsVisible = !noMask;

            if (noMask)
            {
                var boss = this.FindControl<ToggleSwitch>("BossHpCheckbox");
                if (boss != null) boss.IsChecked = false;

                var team = this.FindControl<ToggleSwitch>("TeammatesCheckbox");
                if (team != null) team.IsChecked = false;

                var spec = this.FindControl<ToggleSwitch>("SpectatingCheckbox");
                if (spec != null) spec.IsChecked = false;
            }

            RuntimeLog.Info("UI", $"ApplyMaskProfileToOverlayUi: noMask={noMask}.");
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // CUT_01 — DELETE A SECTION FROM THE MIDDLE OF THE CLIP.
    //
    // A cut is the mirror image of a freeze: a freeze consumes no source time and occupies output
    // time; a cut consumes source time and occupies NONE. All of the real work lives in
    // OutputTimeline and GranularSpeedBuilder, which already splice the timeline for slow-motion,
    // freezes and memes. This screen only collects the ranges.
    //
    // Cuts are stored in ABSOLUTE SOURCE MILLISECONDS, exactly like _trimStartMs and
    // SpeedSegment.StartMs, so they survive a trim change and convert cleanly at the export
    // boundary via CutRange.ToClipRelative.
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// MEME_06 — memes spliced into the middle of the video, in CLIP-RELATIVE SOURCE seconds.
    ///
    /// Placed in the Speed Editor (see the note on its own `_memes` field for why there and not
    /// here) and held on this window because this is what owns the export payload and the recovery
    /// file. When this list is non-empty it is AUTHORITATIVE: ProcessWorker ignores the legacy
    /// MemeFile + MemeAtStart pair entirely. When it is empty the legacy pair still drives a single
    /// start-or-end meme, which is what keeps the main screen's dropdown working unchanged.
    /// </summary>

    /// <summary>
    /// MEME_07 — plays each meme in this screen's preview at the exact moment it interrupts the
    /// gameplay, instead of the preview running straight past a cutaway that the export will make.
    /// See <see cref="Infrastructure.MemePreviewDirector"/> for the file-swap approach and, more
    /// importantly, for the rule the playback tick below has to follow.
    /// </summary>
    private Infrastructure.MemePreviewDirector? _memePreview;

    /// <summary>MEME_07 — built lazily, because the video host does not exist until a file loads.</summary>
    private void EnsureMemePreviewDirector()
    {
        if (_memePreview != null) return;

        _memePreview = new Infrastructure.MemePreviewDirector(
            () => ActiveVideoHost?.IpcClient,
            () => _loadedVideoPath,
            () => _trimStartMs / 1000.0,
            SetMemeSwapOverlay,
            "MEME");

        // The agreed behaviour: the meme's own sound plays, and the background music pauses with
        // the gameplay and carries on afterwards. The tick restarts the music on its own once the
        // gameplay is back, so only the stop side needs saying here.
        _memePreview.MemeStarted += StopMusicPreview;
    }

    /// <summary>MEME_07 — the black-screen notice shown across the two file swaps.</summary>
    private void SetMemeSwapOverlay(bool visible, string message)
    {
        var overlay = this.FindControl<Border>("MemeSwapOverlay");
        var text = this.FindControl<TextBlock>("MemeSwapOverlayText");
        if (text != null && !string.IsNullOrEmpty(message)) text.Text = message;
        if (overlay != null) overlay.IsVisible = visible;
    }

    /// <summary>
    /// CUT_01 — at least this much footage must survive, in ms. A clip cut down to nothing has no
    /// frames to encode and would fall through GranularSpeedBuilder's "no chunks" path, which
    /// exports the WHOLE clip with the cuts ignored — a silent, total failure. Refusing the last
    /// cut is far kinder than producing that.
    /// </summary>
    private const double MinSurvivingMs = 500.0;

    /// <summary>
    /// CUT_01 — everything that must happen after the cut list changes, in one place so no caller
    /// can forget one. The music wizard's Smart Fit aligned a beat drop to the OLD length, so a
    /// length change has to invalidate it — that is objection #3 from the design doc, and it is
    /// handled the same way a trim change already handles it.
    /// </summary>
    private async Task AfterCutsChangedAsync()
    {
        NormalizeCutsInPlace();
        DropMemesInsideCuts();   // MEME_06
        UpdateTimelineMarkers();
        UpdateEstimatedQuality();
        SaveRecoveryState();

        if (_musicWizardResult != null && _cuts.Count > 0)
        {
            ShowTacticalFeedback("♪ Video length changed — re-run ADD MUSIC to re-align the beat drop.");
        }

        await SkipPlayheadOutOfCutAsync();
    }

    /// <summary>
    /// MEME_06 — a meme anchored to a moment that has just been deleted goes with it.
    ///
    /// The alternative — sliding it to the edge of the hole — keeps the user's work but moves their
    /// meme somewhere they did not put it, and they would only find out at export. Deleting it is
    /// the honest reading of "I deleted that scene", and it is announced rather than silent.
    /// </summary>

    private void DropMemesInsideCuts()
    {
        var dropped = _viewModel.Timeline.DropMemesInsideCuts();
        if (dropped.Count > 0)
        {
            ShowTacticalFeedback($"Deleted parts removed {dropped.Count} meme placement(s)");
        }
    }

    private void NormalizeCutsInPlace() => _viewModel.Timeline.NormalizeCutsInPlace();
    private double SurvivingMsFor(double a, double b) => _viewModel.Timeline.SurvivingMsFor(a, b);
    private double RemovedCutSeconds() => _viewModel.Timeline.RemovedCutSeconds();
    private async Task SkipPlayheadOutOfCutAsync()
    {
        try
        {
            if (_cuts.Count == 0) return;
            var client = ActiveVideoHost?.IpcClient;
            if (client == null) return;

            double nowMs = client.CurrentTime * 1000.0;
            foreach (var c in _cuts)
            {
                if (nowMs > c.StartMs + 1 && nowMs < c.EndMs - 1)
                {
                    double toSec = c.EndMs / 1000.0;
                    RuntimeLog.Info("CUT", $"Playhead was inside a deleted section; skipping to {toSec:F2}s.");
                    await client.SendCommandAsync("seek",
                        toSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "absolute");
                    _memePreview?.NotifySeek();   // MEME_07 — a jump, not playback
                    return;
                }
            }
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    #region UX Innovations (IDEA 001, 004, 005, 006, 007, 008, 009, 010, 011)

    /// <summary>
    /// Initializes all UX innovation controls and wires them into the editing workflow.
    /// Called once during construction after all standard controls are wired.
    /// </summary>
    private void InitializeUxInnovations()
    {

        var memeWall = this.FindControl<Controls.MemeWallControl>("MemeWall");
        if (memeWall != null)
        {
            memeWall.MemeSelected += (path) =>
            {
                var cb = this.FindControl<ComboBox>("MemeComboBox");
                var addMemeCb = this.FindControl<ToggleSwitch>("AddMemeCheckbox");
                if (cb != null)
                {
                    var match = _memeItems.FirstOrDefault(m =>
                        string.Equals(m.FullPath, path, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        cb.SelectedItem = match;
                        if (addMemeCb != null) addMemeCb.IsChecked = true;
                        SaveRecoveryState();
                        TriggerParticleBurst(new Point(Bounds.Width / 2, Bounds.Height / 2),
                            Controls.ParticleBurstCanvas.BurstPreset.TogglePop);
                    }
                }
            };
        }

        _kineticScrub = new KineticScrubController();
        _kineticScrub.SeekRequested += (ms) =>
        {
            if (ActiveVideoHost?.IpcClient != null)
            {
                _ = SeekInternal(ms / 1000.0);
                double duration = ActiveVideoHost.IpcClient.Duration;
                if (duration > 0)
                {
                    var slider = this.FindControl<Slider>("TimelineSlider");
                    if (slider != null)
                    {
                        _isTimerUpdatingSlider = true;
                        slider.Value = (ms / 1000.0 / duration) * 100.0;
                        _isTimerUpdatingSlider = false;
                    }
                    ShowPlayheadBadge(ms / 1000.0, (ms / 1000.0 / duration) * 100.0);
                }
            }
        };

        var portraitGrid = this.FindControl<Grid>("PortraitDimmingGrid");
        if (portraitGrid != null)
        {
            Controls.LiquidMorph.AttachPortraitMorph(portraitGrid);
        }
    }

    /// <summary>
    /// Triggers a particle burst effect at the specified screen-relative point.
    /// Converts to local control coordinates before emitting.
    /// </summary>
    private void TriggerParticleBurst(Point windowRelativePoint,
        Controls.ParticleBurstCanvas.BurstPreset preset)
    {
        var particleLayer = this.FindControl<Controls.ParticleBurstCanvas>("ParticleLayer");
        if (particleLayer == null) return;

        var localPoint = particleLayer.TranslatePoint(windowRelativePoint, particleLayer);
        if (localPoint.HasValue)
        {
            particleLayer.Burst(localPoint.Value, preset);
        }
        else
        {
            particleLayer.Burst(new Point(particleLayer.Bounds.Width / 2, particleLayer.Bounds.Height / 2), preset);
        }
    }


    #endregion

    private bool _isRadialOpen = false;

    private void OnWindowPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            var radialMenu = this.FindControl<Controls.RadialMenuControl>("RadialMenu");
            if (radialMenu != null)
            {
                radialMenu.Open(e.GetPosition(this));
                _isRadialOpen = true;
                e.Handled = true;
            }
        }
    }

    private void OnWindowPointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (_isRadialOpen)
        {
            var radialMenu = this.FindControl<Controls.RadialMenuControl>("RadialMenu");
            if (radialMenu != null)
            {
                var pos = e.GetPosition(radialMenu);
                radialMenu.UpdateHover(pos);
                e.Handled = true;
            }
        }
    }

    private void OnWindowPointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (_isRadialOpen && e.InitialPressMouseButton == Avalonia.Input.MouseButton.Right)
        {
            var radialMenu = this.FindControl<Controls.RadialMenuControl>("RadialMenu");
            radialMenu?.Close();
            _isRadialOpen = false;
            e.Handled = true;
        }
    }
}
