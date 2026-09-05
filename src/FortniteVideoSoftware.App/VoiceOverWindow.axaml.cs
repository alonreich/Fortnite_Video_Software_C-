using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using FortniteVideoSoftware.App.Controls;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.App;

public partial class VoiceOverWindow : Window
{
    private const string BoundsKey = "VoiceOverWindowBounds";

    public static readonly Avalonia.StyledProperty<bool> IsDuckAudioProperty =
        Avalonia.AvaloniaProperty.Register<VoiceOverWindow, bool>(nameof(IsDuckAudio), false);
    public bool IsDuckAudio
    {
        get => GetValue(IsDuckAudioProperty);
        set => SetValue(IsDuckAudioProperty, value);
    }

    private MpvVideoView? _videoHost;
    private VoiceRecorder? _recorder;

    /// <summary>
    /// AUDIO_01: live while the microphone is open. Disposing it re-enables UI sounds.
    /// Held as a field rather than a `using` because recording spans two separate user actions
    /// (press to start, press again to stop).
    /// </summary>
    private IDisposable? _uiSoundMute;
    private readonly ApplicationPaths _paths = ApplicationPaths.CreateDefault();
    private string _videoPath = "";
    private string _outputWavPath = "";

    private bool _isRecording = false;
    private bool _isMpvReady = false;
    private bool _isClosing = false;
    private bool _isSeeking = false;
    private double? _nextSeekTarget = null;
    private DispatcherTimer _timer;

    private double _smoothedVolume = 0;
    private double _peakVolume = 0;

    /// <summary>
    /// VOMON_01 — idle input monitoring, so the meter and the READY lamp tell the truth BEFORE
    /// the user commits to a take. Stopped whenever <see cref="VoiceRecorder"/> needs the device.
    /// </summary>
    private FortniteVideoSoftware.Core.Media.MicLevelMonitor? _micMonitor;

    // ══════════════════════════════════════════════════════════════════════════════
    // VOASYNC_02 — THE AUDIO DEVICE CHAIN.
    //
    // Four operations touch the capture device, and EVERY one of them blocks:
    //   opening the recorder      waveInOpen + creating the WAV file
    //   draining the recorder     waits on RecordingStopped, up to 2 s
    //   stopping the monitor      waveInReset + waveInClose, joins the capture thread
    //   starting the monitor      waveInOpen
    // Run inline they froze the window on every press of record. Run on separate tasks they would
    // race: the recorder could try to open the device before the monitor had let go of it, which
    // on many drivers simply fails and loses the take.
    //
    // So they are queued onto ONE chain. Order is preserved exactly as the interface thread issued
    // it, nothing runs on the interface thread, and the device is never held by two objects at
    // once. The field is only ever read and written on the interface thread, so it needs no lock.
    // ══════════════════════════════════════════════════════════════════════════════
    private Task _audioDeviceChain = Task.CompletedTask;

    /// <summary>VOASYNC_02 — queues blocking capture-device work, in order, off the interface thread.</summary>
    private void QueueAudioDeviceWork(Action work)
    {
        _audioDeviceChain = _audioDeviceChain.ContinueWith(
            _ =>
            {
                try { work(); }
                catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            },
            System.Threading.CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    /// <summary>
    /// VOASYNC_02 — takes whose drain is still in flight. Apply and the close prompt both have to
    /// wait for these, otherwise a take the user just recorded would be invisible to them for the
    /// ~100 ms the device takes to drain — and silently lost if they pressed Apply inside it.
    /// Only touched on the interface thread.
    /// </summary>
    private readonly List<TaskCompletionSource> _pendingFinalizes = new();

    private Task WhenTakesSettled()
    {
        if (_pendingFinalizes.Count == 0) return Task.CompletedTask;
        var tasks = new List<Task>(_pendingFinalizes.Count);
        foreach (var tcs in _pendingFinalizes) tasks.Add(tcs.Task);
        return Task.WhenAll(tasks);
    }

    /// <summary>
    /// VOTAKE_01 — decoded peak envelopes, one array per take WAV, keyed by path.
    /// Populated by a THREAD-POOL worker (EnsureTakePeaksAsync) and read only on the UI thread.
    /// A take with no entry yet simply draws as a flat red block until its worker lands, which is
    /// what keeps recording from stuttering while a WAV is decoded.
    /// </summary>
    private readonly Dictionary<string, float[]> _takePeaks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _takePeakPending = new(StringComparer.OrdinalIgnoreCase);
    private const int TakePeakBuckets = 900;

    private string? _tempThumbPath;
    private string? _tempWavePath;
    private System.Threading.CancellationTokenSource? _generationCts;


    private double _trimStartSec = 0;
    private double _trimEndSec = 0;
    private readonly List<SpeedSegment> _speedSegments = new();

    // ══════════════════════════════════════════════════════════════════════════════════════
    // ZOOMLIVE_06 — THE VOICE OVER STUDIO NOW SHOWS THE ZOOM TOO.
    //
    // This was the ONE preview of the four that had never simulated a zoom. The Main App, the
    // Granular editor and Music Wizard phase 3 all ran ZoomPreviewSimulator; this window did not,
    // so a user recording a take over a zoomed stretch saw the full uncropped frame and pitched
    // their commentary at scenery the finished video does not show.
    //
    // ⚠️ IT SHARES ONE SIMULATOR WITH THE OTHER THREE ON PURPOSE. ZoomPreviewSimulator reads its
    // ramp timing straight off GranularSpeedBuilder, so the previews and the exported file cannot
    // drift apart. Do not compute a crop locally here.
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>ZOOMLIVE_06 — set by the Main App, exactly as it sets the Music Wizard's copy.</summary>
    public bool IsPortraitPreview { get; set; }

    /// <summary>ZOOMLIVE_06 — last crop pushed to mpv, so an unchanged value is never re-sent every tick.</summary>
    private string _lastLiveCrop = "";

    /// <summary>
    /// ZOOMLIVE_06 — pushes the simulated zoom crop for wherever the playhead is now.
    ///
    /// <para>
    /// ⚠️ NEVER CALL THIS WHILE A MEME CUTAWAY IS ON SCREEN. During a cutaway mpv is showing the
    /// meme, <c>CurrentTime</c> belongs to that file, and the director has deliberately cleared
    /// <c>video-crop</c> because the export splices a meme in UNCROPPED. Writing a crop here would
    /// zoom the meme and then be clobbered on the way back. The tick's early return on
    /// <c>_memePreview.IsActive</c> is what guarantees it (MEME_07).
    /// </para>
    /// </summary>
    private void UpdateLiveZoomCrop()
    {
        if (!FortniteVideoSoftware.Core.Media.VideoRenderMode.Current.UseHardwareAcceleration) return;

        var ipc = _videoHost?.IpcClient;
        if (ipc == null || !_isMpvReady) return;
        if (_speedSegments.Count == 0 && !IsPortraitPreview) { ClearLiveZoomCrop(); return; }
        if (ipc.VideoWidth <= 0 || ipc.VideoHeight <= 0) return;

        // This window keeps its trim in SECONDS (_trimStartSec/_trimEndSec), unlike the Granular
        // editor's milliseconds. The simulator wants clip-relative seconds either way.
        double tSec = Math.Max(0, ipc.CurrentTime - _trimStartSec);
        double endSec = _trimEndSec > 0 ? _trimEndSec : ipc.Duration;
        double durSec = Math.Max(0.1, endSec - _trimStartSec);

        var result = FortniteVideoSoftware.Core.Media.ZoomPreviewSimulator.Compute(
            _speedSegments, tSec, durSec, IsPortraitPreview, ipc.VideoWidth, ipc.VideoHeight);

        if (!result.HasCrop) { ClearLiveZoomCrop(); return; }
        if (result.Crop == _lastLiveCrop) return;
        _lastLiveCrop = result.Crop;
        _ = ipc.SetPropertyAsync("video-crop", result.Crop);
    }

    /// <summary>ZOOMLIVE_06 — drops any simulated crop. Called on teardown and when no zoom applies.</summary>
    private void ClearLiveZoomCrop()
    {
        if (_lastLiveCrop.Length == 0) return;
        _lastLiveCrop = "";
        _ = _videoHost?.IpcClient?.SetPropertyAsync("video-crop", "");
    }

    /// <summary>
    /// ══════════════════════════════════════════════════════════════════════════════
    /// CUTS_02 — THE PARTS OF THE CLIP THAT NO LONGER EXIST.
    ///
    /// Deleted in the Speed Editor, in ABSOLUTE source milliseconds — the same frame of reference
    /// this window's playhead, takes and trim points all use. Until now this window knew nothing
    /// about them, which broke two things at once:
    ///
    ///   THE PICTURE. The film strip and the ruler covered the whole trimmed span, so the user was
    ///   scrubbing over, and could record a take across, footage that will not be in the video.
    ///
    ///   THE MATHS. Voice-over takes are anchored in source time and mapped to output time by
    ///   OutputTimeline. Built without the cuts, that map thinks every deleted second is still
    ///   there, so every take after the first cut lands late in the finished video by the total
    ///   length of everything removed before it.
    ///
    /// The timeline axis stays SOURCE time — it is a recording surface, and a take has to be
    /// anchored to the frame it was spoken over. The cuts are drawn on it and the playhead refuses
    /// to sit inside one, which is how the main screen already behaves.
    /// ══════════════════════════════════════════════════════════════════════════════
    /// </summary>
    private readonly List<FortniteVideoSoftware.Core.Media.CutRange> _cuts = new();

    /// <summary>
    /// MEME_06 — memes spliced into the video, clip-relative source seconds.
    ///
    /// The mirror of the cut list above. A cut removes output time and makes every later take map
    /// EARLY without it; a meme adds output time and makes every later take map LATE. Both are the
    /// same defect and both are fixed by handing the real edit list to OutputTimeline rather than
    /// letting this window assume the video is one uninterrupted run of gameplay.
    ///
    /// Nothing is drawn for them on this window's lanes: its axis is SOURCE time, where a meme
    /// occupies no width at all.
    /// </summary>
    private readonly List<FortniteVideoSoftware.Core.Media.MemePlacement> _memes = new();

    /// <summary>
    /// MEME_07 — plays each meme in this window's preview at the moment it interrupts the gameplay.
    /// See <see cref="Infrastructure.MemePreviewDirector"/>; the rule its host tick must follow is
    /// documented there and obeyed at the top of <see cref="Timer_Tick"/>.
    /// </summary>
    private Infrastructure.MemePreviewDirector? _memePreview;

    /// <summary>MEME_07 — built lazily, once mpv is up and a file is loaded.</summary>
    private void EnsureMemePreviewDirector()
    {
        if (_memePreview != null) return;

        _memePreview = new Infrastructure.MemePreviewDirector(
            () => _videoHost?.IpcClient,
            () => _videoPath,
            () => _trimStartSec,
            SetMemeSwapOverlay,
            "VOICEOVER");

        // The agreed behaviour: the meme's own sound plays; every take pauses with the gameplay
        // and carries on afterwards. The tick's early return stops UpdatePreviewPlayers from
        // running during the cutaway, so the takes are silenced explicitly here rather than left
        // playing over the meme.
        _memePreview.MemeStarted += PauseTakePlaybackForMeme;
    }

    /// <summary>MEME_07 — silences every take the instant a cutaway begins.</summary>
    private void PauseTakePlaybackForMeme()
    {
        try
        {
            foreach (var p in _previewPlayers)
            {
                if (p.Player.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                    p.Player.Pause();
            }
        }
        catch (System.Exception ex) { RuntimeLog.SwallowedThrottled(ex); }
    }

    /// <summary>MEME_07 — the black-screen notice shown across the two file swaps.</summary>
    private void SetMemeSwapOverlay(bool visible, string message)
    {
        var overlay = this.FindControl<Border>("MemeSwapOverlay");
        var text = this.FindControl<TextBlock>("MemeSwapOverlayText");
        if (text != null && !string.IsNullOrEmpty(message)) text.Text = message;
        if (overlay != null) overlay.IsVisible = visible;
    }
    private double _baseSpeed = 1.0;
    private double _lastAppliedSpeed = 1.0;
    private bool _isCurrentlyFrozen;
    private DateTime _freezeStartTime;
    private double _currentFreezeDurationMs;
    private double _lastFreezeTriggerMs = -1;
    private DateTime _lastTimelineSeekUtc = DateTime.MinValue;
    private double? _dragSeekTimeSec = null;
    private bool _isVKeyPressed = false;
    private bool _isSpaceKeyPressed = false;

    private class VoiceOverSession
    {
        public string WavPath { get; set; } = "";
        public double StartSec { get; set; }
        public double EndSec { get; set; }
        public double TrimLeftSec { get; set; } = 0;
        public double TrimRightSec { get; set; } = 0;
        public bool IsMuted { get; set; } = false;
        public double RenderStartSec => StartSec + TrimLeftSec;
        public double RenderEndSec => EndSec - TrimRightSec;
    }
    private List<VoiceOverSession> _sessions = new();
    private VoiceOverSession? _currentSession;
    private VoiceOverSession? _selectedSession;
    private VoiceOverSession? _draggingSession;
    private bool _isDraggingStartEdge;
    private bool _isDraggingEndEdge;
    private readonly List<Line> _waveformLinePool = new();
    private Rectangle? _currentSessionRegionRect;
    private Polygon? _playheadCaret;
    private Line? _rulerPlayheadLine;
    private int _renderedSessionCount = -1;
    private double _renderedRulerWidth = -1;
    private double _renderedRulerHeight = -1;
    private double _renderedScaleWidth = -1;
    private double _renderedScaleDuration = -1;

    private sealed class PreviewPlayer : IDisposable
    {
        public NAudio.Wave.AudioFileReader Reader { get; }
        public NAudio.Wave.WaveOutEvent Player { get; }
        public VoiceOverSession Session { get; }

        public PreviewPlayer(VoiceOverSession session, float previewGain = 1.0f)
        {
            Session = session;
            Reader = new NAudio.Wave.AudioFileReader(session.WavPath);

            Reader.Volume = previewGain;

            Player = new NAudio.Wave.WaveOutEvent();
            Player.Init(Reader);
        }

        public void Dispose()
        {
            try { Player.Stop(); Player.Dispose(); } catch (System.Exception) { }
            try { Reader.Dispose(); } catch (System.Exception) { }
        }
    }
    private List<PreviewPlayer> _previewPlayers = new();

    private readonly Dictionary<string, float> _takePreviewGain = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _takeGainPending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// PREVIEW_03 — the clip's measured loudness, supplied by the Main App.
    ///
    /// The export lifts the game bus to TargetLufs and aims the voice at the same figure, so the
    /// two land together. This window plays the video RAW through mpv, so without this the voice
    /// would be normalised against a game that had not moved — the voice would sound too loud here
    /// by exactly the boost the export was going to apply, and the user would turn it down to
    /// compensate for a problem that only exists in the preview.
    ///
    /// The offset shifts the VOICE down by whatever boost the video is missing, reproducing the
    /// export's relationship. NAudio has no 0-100 ceiling, but going DOWN is still the right
    /// direction: it keeps the preview honest without ever amplifying a noisy take.
    /// </summary>
    public double? SourceMeasuredLufs
    {
        get => _sourceMeasuredLufs;
        set
        {
            if (_sourceMeasuredLufs == value) return;
            _sourceMeasuredLufs = value;
            // PREVIEW_03 — the Main App may set this after the window is up, and any gain already
            // memoised was computed from a null measurement (i.e. no shift at all).
            InvalidateTakePreviewGains();
        }
    }
    private double? _sourceMeasuredLufs;

    /// <summary>
    /// PREVIEW_03 — THE SHIFT THAT MAKES THIS WINDOW HONEST. Was hard-coded to 0.0, which is why
    /// the compensation the comment above describes never actually happened.
    ///
    /// The export lifts the game bus to TargetLufs and aims the voice at that same figure, so the
    /// two land together. Here the video plays RAW through mpv. If the clip measured -25 LUFS, the
    /// export is going to add 11 dB to it — but in this window it is still at -25, so an unshifted
    /// take sounds 11 dB too loud against it. The user turns the take down to fix that, and the
    /// exported voice ends up 11 dB too quiet.
    ///
    /// So the VOICE is shifted DOWN by exactly the boost the video has not received yet. Going
    /// down is the only safe direction: it reproduces the export's relationship without ever
    /// amplifying a noisy take. A clip already at or above the target needs no shift, and an
    /// unmeasured clip gets none — which is the old behaviour, restored as the fallback.
    /// </summary>
    private double VoicePreviewOffsetDb
    {
        get
        {
            if (SourceMeasuredLufs is not double measured) return 0.0;

            double missingBoost = FortniteVideoSoftware.Core.Media.AudioLoudnessProbe.TargetLufs - measured;
            if (missingBoost <= 0.0) return 0.0;

            // Same rail the export clamps its own corrections to, so a badly-measured or nearly
            // silent clip cannot drive the preview take down to nothing.
            return -Math.Min(missingBoost, FortniteVideoSoftware.Core.Media.AudioLoudnessProbe.MaxMusicGainDb);
        }
    }

    /// <summary>
    /// PREVIEW_03 — the linear NAudio gain for one take. Was hard-coded to 1.0f.
    /// Memoised per take path so the value cannot drift between rebuilds of the preview players
    /// (they are torn down and recreated whenever the take count changes).
    /// </summary>
    private float GetTakePreviewGain(VoiceOverSession session)
    {
        string key = session.WavPath ?? string.Empty;
        if (key.Length > 0 && _takePreviewGain.TryGetValue(key, out float cached)) return cached;

        float gain = (float)Math.Pow(10.0, VoicePreviewOffsetDb / 20.0);
        if (key.Length > 0) _takePreviewGain[key] = gain;
        return gain;
    }

    /// <summary>
    /// PREVIEW_03 — drops the memoised gains so the next preview rebuild recomputes them. Called
    /// when SourceMeasuredLufs arrives after the takes were already built.
    /// </summary>
    private void InvalidateTakePreviewGains()
    {
        _takePreviewGain.Clear();
        _takeGainPending.Clear();
        _previewPlayersBuiltForCount = -1;
    }


    private Button? _micRecordButton;
    private Button? _playPauseButton;
    private Button? _applyButton;
    private Button? _cancelButton;
    private ComboBox? _micDeviceComboBox;
    private TextBlock? _recordingStatusText;
    private TextBlock? _voiceOverHintText;
    private TextBlock? _thumbFallbackText;
    private TextBlock? _waveformFallbackText;
    private Ellipse? _recordingLight;

    private Border? _eqMeterTrack;
    private Canvas? _timelineRulerCanvas;
    private Canvas? _waveformCanvas;
    private Canvas? _takeOverlayCanvas;
    private Ellipse? _readyLamp;
    private TextBlock? _voTimeElapsed;
    private TextBlock? _voTimeTotal;
    private TextBlock? _voTimeRemaining;
    private CheckBox? _duckMusicCb;
    private Grid? _thumbnailLaneGrid;
    private Grid? _waveformLaneGrid;
    private Image? _thumbnailLaneImage;
    private Image? _waveformLaneImage;
    private Border? _thumbLoadingOverlay;
    private Border? _waveformLoadingOverlay;
    private Avalonia.Controls.Shapes.Path? _playIcon;
    private Avalonia.Controls.Shapes.Path? _pauseIcon;
    private CheckBox? _duckAudioCb;
    private Border? _thumbPlayheadLine;
    private Border? _wavePlayheadLine;

    private FortniteVideoSoftware.Core.Media.OutputTimeline? _timeline;
    public VoiceOverResult? Result { get; private set; }
    public VoiceOverResult? InitialState { get; set; }

    public class VoiceOverResult
    {
        public string? VoiceOverWavPath { get; set; }
        public double VoiceOverStartTimestampSec { get; set; }
        public List<VoiceOverTake> VoiceOverTakes { get; set; } = new();

        /// <summary>
        /// VOPROT_01 — "Protect VoiceOver Recording from Game-Play Sound".
        /// Ducks AND EQ-carves the GAME bus across the takes. Named DuckAudio for compatibility
        /// with the recovery file's existing `voiceOverDuckAudio` key.
        /// </summary>
        public bool DuckAudio { get; set; }

        /// <summary>
        /// VOPROT_01 — "Protect VoiceOver Recording from Music".
        /// The same treatment applied to the music bed added in the Add Music wizard. Independent
        /// of that wizard's own ducking checkbox, which protects the GAME from the music, not the
        /// voice from the music — a different job with a different trigger.
        /// </summary>
        public bool ProtectFromMusic { get; set; }
    }

    public VoiceOverWindow()
    {
        InitializeComponent();
        CacheControls();
        FortniteVideoSoftware.App.WindowBoundsHelper.Track(this, BoundsKey);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        Closing += OnWindowClosing;
        AttachTitleBarDrag();
        AttachResizeGrip();
        PopulateMicrophoneDevices();
        WireEffectStateControls();
        UpdateTransportState();
        UpdateApplyState();
    }

    private bool _isSafeToClose = false;

    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // ZOOMLIVE_06 — this window pushes `video-crop` into a host the Main App owns and reuses.
        // Leaving a crop behind would zoom the main screen's preview after the studio closes.
        ClearLiveZoomCrop();

        if (_isSafeToClose) return;

        if (HasApplicableVoiceEffect())
        {
            e.Cancel = true;
            var dialog = new FortniteVideoSoftware.App.Controls.ConfirmDialogWindow();
            dialog.SetTitle("DISCARD RECORDINGS?");
            dialog.SetMessage("You have unsaved voiceover takes. Are you sure you want to discard them and close?");
            dialog.SetButtonText("DISCARD", "KEEP EDITING");
            var top = Avalonia.Controls.TopLevel.GetTopLevel(this) as Window;
            if (top != null)
            {
                await dialog.ShowDialog(top);
                if (dialog.Result)
                {
                    _isSafeToClose = true;
                    Close();
                }
            }
            return;
        }

        if (_isClosing) return;
        WindowBoundsHelper.SaveBoundsSync(this, BoundsKey);
    }

    public VoiceOverWindow(
        string videoPath,
        double startPosSec,
        double trimStartMs = 0,
        double trimEndMs = 0,
        IEnumerable<SpeedSegment>? speedSegments = null,
        double baseSpeed = 1.0,
        IEnumerable<FortniteVideoSoftware.Core.Media.CutRange>? cuts = null,
        IEnumerable<FortniteVideoSoftware.Core.Media.MemePlacement>? memes = null) : this()
    {
        if (memes != null) _memes.AddRange(memes);
        _videoPath = videoPath;
        _trimStartSec = trimStartMs / 1000.0;
        _trimEndSec = trimEndMs / 1000.0;
        _baseSpeed = Math.Clamp(baseSpeed, 0.1, 4.0);
        _lastAppliedSpeed = _baseSpeed;
        if (speedSegments != null)
        {
            foreach (var segment in speedSegments)
            {
                _speedSegments.Add(segment);
            }
            _speedSegments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        }
        if (cuts != null)
        {
            foreach (var cut in cuts)
            {
                if (cut.EndMs > cut.StartMs) _cuts.Add(cut);
            }
            _cuts.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        }
        _paths.EnsureWritableDirectories();
        _outputWavPath = CreateTempVoiceOverPath();


        if (_micRecordButton != null) _micRecordButton.Click += ToggleRecord;
        if (_playPauseButton != null) _playPauseButton.Click += TogglePreviewPlayback;
        if (_applyButton != null) _applyButton.Click += (s, e) => ApplyAndClose();
        if (_cancelButton != null) _cancelButton.Click += (s, e) => Close();

        _timer.Tick += Timer_Tick;
        _timer.Start();

        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, OnKeyDownHandler, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(Avalonia.Input.InputElement.KeyUpEvent, OnKeyUpHandler, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        if (_timelineRulerCanvas != null)
        {
            WireTimelineSeekSurface(_timelineRulerCanvas);
        }

        if (_thumbnailLaneGrid != null)
        {
            WireTimelineSeekSurface(_thumbnailLaneGrid);
        }

        if (_waveformLaneGrid != null)
        {
            WireTimelineSeekSurface(_waveformLaneGrid);
        }

        Loaded += (_, _) => Controls.CoachOverlay.Register(this, Controls.CoachTours.VoiceOverKey, Controls.CoachTours.VoiceOver);

        Loaded += async (_, _) =>
        {

            if (_videoHost != null)
            {
                bool previewReady = false;
                try
                {
                    string mpvPath = ResolveBinaryPath("mpv.exe", "frontend");
                    await _videoHost.StartMpvProcessAsync(mpvPath).WaitAsync(TimeSpan.FromSeconds(8));

                    if (_videoHost.IpcClient != null)
                    {
                        _videoHost.IpcClient.SeekCompleted -= OnSeekCompleted;
                        _videoHost.IpcClient.SeekCompleted += OnSeekCompleted;
                        
                        // VOSTART_01 — ALWAYS OPEN AT MARK START.
                        // `startPosSec` is wherever the main screen's playhead happened to be
                        // sitting, which is almost never the beginning of what the user trimmed.
                        // Recording is anchored to the video clock, so opening mid-clip meant the
                        // first take started mid-clip too. The trim start IS "the beginning" here.
                        double initialPos = NormalizePreviewPlaybackPosition(_trimStartSec);
                        await _videoHost.IpcClient.LoadFileAsync(_videoPath, initialPos);
                        await _videoHost.IpcClient.SetPropertyAsync("pause", "yes");


                        // ⚠️ VOFIX_01 — THE CAUSE OF "IT KEEPS LOOPING AND REPLAYING THE VIDEO".
                        //
                        // This used to set `ab-loop-a` / `ab-loop-b`. Those are mpv's A-B REPEAT
                        // properties: on reaching B, mpv SEEKS BACK TO A and plays the range again,
                        // forever. The intent was clearly "confine playback to the trim region",
                        // but the property chosen does the opposite of stopping there.
                        //
                        // The damage went well past an annoying replay. Recording arms by watching
                        // for the video clock to MOVE FORWARD (PumpRecordArming); a loop-back makes
                        // the clock jump backwards mid-take, which is why takes came out empty or
                        // misanchored and why the transport felt unstable. One property, all three
                        // reported symptoms.
                        //
                        // The range is now enforced in Timer_Tick, which pauses at the trim end
                        // instead of rewinding. Any leftover A-B loop from a previous session on
                        // this mpv instance is explicitly cleared.
                        await _videoHost.IpcClient.SetPropertyAsync("ab-loop-a", "no");
                        await _videoHost.IpcClient.SetPropertyAsync("ab-loop-b", "no");
                        await _videoHost.IpcClient.SetPropertyAsync("keep-open", "yes");

                        double videoDuration = _videoHost.IpcClient.Duration;
                        double effectiveDuration = (_trimEndSec > 0 ? _trimEndSec : videoDuration) - _trimStartSec;
                        if (effectiveDuration <= 0) effectiveDuration = videoDuration;
                        // CUTS_02 — without this last argument every take recorded after a deleted
                        // section is exported late by exactly the amount that was removed.
                        _timeline = FortniteVideoSoftware.Core.Media.OutputTimeline.Create(
                            effectiveDuration * 1000.0,
                            _speedSegments,
                            _baseSpeed,
                            _trimStartSec * 1000.0,
                            // ⚠️ MEME_06 — MEMES ARE DELIBERATELY OMITTED. DO NOT ADD THEM.
                            // This timeline is used by ApplyAndClose to work out how much of a
                            // take's WAV to trim off each end, as the DIFFERENCE between two
                            // SourceToOutput calls. A meme sitting between those two instants
                            // would add its whole length to that difference and ffmpeg would cut
                            // seconds of real speech off the take. The export positions takes
                            // around memes itself (ProcessWorker's MemeTimeInsertedBefore), so this
                            // window stays meme-blind and self-consistent: its preview does not
                            // play memes either.
                            null,
                            FortniteVideoSoftware.Core.Media.CutRange.ToClipRelative(_cuts, _trimStartSec * 1000.0));
                        
                        previewReady = true;
                        
                        if (InitialState != null)
                        {
                            // VOPROT_02 — the project's own saved choice is only ONE of the three
                            // possible sources; ApplyVoiceProtectionPolicy decides which wins.
                            ApplyVoiceProtectionPolicy(InitialState.DuckAudio, InitialState.ProtectFromMusic);
                            if (InitialState.VoiceOverTakes != null)
                            {
                                foreach (var t in InitialState.VoiceOverTakes)
                                {
                                    if (System.IO.File.Exists(t.Path))
                                    {
                                        double dur = 0.1;
                                        try { using var af = new NAudio.Wave.AudioFileReader(t.Path); dur = af.TotalTime.TotalSeconds; } catch {}
                                        _sessions.Add(new VoiceOverSession { WavPath = t.Path, StartSec = t.StartSec, EndSec = t.StartSec + dur });
                                        _renderedSessionCount = -1;
                                        EnsureTakePeaksAsync(t.Path);   // VOTAKE_01
                                    }
                                }
                            }
                            UpdatePlayheadUI();
                        }
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("VoiceOver", $"Preview startup failed. Recording disabled, auto-ducking remains available. {ex.Message}");
                    _videoHost.Dispose();
                    if (_recordingStatusText != null)
                    {
                        _recordingStatusText.Text = "PREVIEW OFF";
                        _recordingStatusText.Foreground = GetAppBrush("AppWarningBrush", Brushes.Yellow);
                    }
                    UpdateApplyState(previewReady ? null : "Preview could not start on this graphics session.");
                }

                _isMpvReady = previewReady;

                UpdateTransportState();
                UpdateApplyState(previewReady ? null : "Preview could not start on this graphics session.");

                if (previewReady)
                {
                    _ = GenerateLanesAsync();
                }
            }
        };
    }

    /// <summary>VOICE_02 — the take count the current player list was built for. -1 = invalid.</summary>
    private int _previewPlayersBuiltForCount = -1;

    /// <summary>VOASYNC_01 — true while a background player rebuild is in flight.</summary>
    private bool _previewRebuildInFlight;

    /// <summary>
    /// VOASYNC_02 — tears the preview players down OFF the interface thread.
    ///
    /// Each PreviewPlayer owns a WaveOutEvent, and disposing one performs Stop() followed by
    /// waveOutClose — a render endpoint being handed back to Windows. This ran inline, and it ran
    /// on exactly the frame a new take was mounted, so every recording paid for closing every
    /// previous take's endpoint before the new list went up. Retiring them on a worker keeps the
    /// interface free; the objects are already detached from `_previewPlayers` by then, so nothing
    /// can reach them again.
    /// </summary>
    private void StopPreviewPlayers()
    {
        if (_previewPlayers.Count > 0)
        {
            var retiring = new List<PreviewPlayer>(_previewPlayers);
            _previewPlayers.Clear();
            RetirePreviewPlayersAsync(retiring);
        }
        _previewPlayersBuiltForCount = -1;
    }

    private static void RetirePreviewPlayersAsync(List<PreviewPlayer> players)
    {
        if (players.Count == 0) return;
        _ = Task.Run(() =>
        {
            foreach (var player in players)
            {
                try { player.Dispose(); }
                catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            }
        });
    }

    private void UpdatePreviewPlayers()
    {
        if (_videoHost?.IpcClient == null) return;
        
        bool isMpvPaused = _videoHost.IpcClient.IsPaused;
        bool shouldPlay = (!isMpvPaused || _isCurrentlyFrozen) && !_isRecording;
        double time = _videoHost.IpcClient.CurrentTime;
        
        double elapsedFreezeSec = 0;
        if (_isCurrentlyFrozen)
        {
            elapsedFreezeSec = (DateTime.UtcNow - _freezeStartTime).TotalSeconds;
        }

        // VOASYNC_01 — REBUILDING THE PREVIEW PLAYERS IS NOW A BACKGROUND JOB.
        //
        // This block ran on the interface thread inside a 50 ms timer tick, and for EVERY take it
        // opened an AudioFileReader (decode + header parse) and initialised a WaveOutEvent (which
        // opens a WASAPI render endpoint). With three takes that is three device opens in one
        // tick — hundreds of milliseconds of frozen interface immediately after each recording,
        // which is exactly the "stutter and it takes time till the recording appears" complaint.
        // The construction now happens on the thread pool and the finished list is swapped in on
        // the interface thread. `_previewPlayersBuiltForCount` is claimed BEFORE the work starts so
        // subsequent ticks do not queue the same rebuild again.
        if (_sessions.Count != _previewPlayersBuiltForCount && !_previewRebuildInFlight)
        {
            _previewRebuildInFlight = true;
            int builtForCount = _sessions.Count;
            var snapshot = new List<(VoiceOverSession session, float gain)>();
            foreach (var session in _sessions)
            {
                snapshot.Add((session, GetTakePreviewGain(session)));
            }

            _ = Task.Run(() =>
            {
                var built = new List<PreviewPlayer>();
                foreach (var (session, gain) in snapshot)
                {
                    if (!System.IO.File.Exists(session.WavPath)) continue;
                    try { built.Add(new PreviewPlayer(session, gain)); }
                    catch (System.Exception __ex)
                    {
                        RuntimeLog.Fail("VoiceOver", $"A recorded take could not be opened for preview: {__ex.Message}");
                    }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    _previewRebuildInFlight = false;

                    // The window may have closed, or the take list may have moved on, while the
                    // players were being built. Either way these are orphans — dispose them rather
                    // than mounting a stale set.
                    if (_isClosing || _sessions.Count != builtForCount)
                    {
                        RetirePreviewPlayersAsync(built);
                        return;
                    }

                    StopPreviewPlayers();
                    _previewPlayers.AddRange(built);
                    _previewPlayersBuiltForCount = builtForCount;
                });
            });
        }

        foreach (var player in _previewPlayers)
        {
            var take = player.Session;
            if (take.IsMuted)
            {
                if (player.Player.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                    player.Player.Pause();
                continue;
            }

            bool shouldPlayVoice = shouldPlay && time >= take.RenderStartSec && time <= take.RenderEndSec;
            
            double mappedTime = _timeline != null ? _timeline.SourceToOutput(time) : time;
            mappedTime += elapsedFreezeSec;
            
            double mappedStart = _timeline != null ? _timeline.SourceToOutput(take.StartSec) : take.StartSec;
            double mappedOffset = mappedTime - mappedStart;
            
            if (shouldPlayVoice && player.Player.PlaybackState != NAudio.Wave.PlaybackState.Playing)
            {
                if (mappedOffset >= 0 && mappedOffset < player.Reader.TotalTime.TotalSeconds)
                {
                    try { player.Reader.CurrentTime = TimeSpan.FromSeconds(mappedOffset); } catch (System.Exception __ex) { RuntimeLog.SwallowedThrottled(__ex); }
                }
                player.Player.Play();
            }
            else if (!shouldPlayVoice && player.Player.PlaybackState == NAudio.Wave.PlaybackState.Playing)
            {
                player.Player.Pause();
            }
            else if (shouldPlayVoice && player.Player.PlaybackState == NAudio.Wave.PlaybackState.Playing)
            {
                double expectedPos = mappedOffset;
                double actualPos = player.Reader.CurrentTime.TotalSeconds;
                if (Math.Abs(expectedPos - actualPos) > 0.15)
                {
                    try { player.Reader.CurrentTime = TimeSpan.FromSeconds(Math.Max(0, expectedPos)); } catch (System.Exception __ex) { RuntimeLog.SwallowedThrottled(__ex); }
                }
            }
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        // ══════════════════════════════════════════════════════════════════════════════════
        // MEME_07 — BEFORE EVERYTHING ELSE ON THIS TICK.
        //
        // A cutaway swaps the meme file into this same mpv host, so CurrentTime, Duration and
        // IsEof stop describing the gameplay. Every line below would then act on the wrong clock:
        // the trim-end stop would fire at the meme's end, the cut skip would seek at random, the
        // playhead would jump, the takes would resync to a meaningless offset and — worst — the
        // record arming watches the video clock move forward, so it would arm off the meme.
        //
        // ⚠️ RECORDING SUSPENDS CUTAWAYS ENTIRELY. A take is anchored to the video clock; letting
        // the picture cut away mid-take would anchor speech to frames the take never heard.
        // ══════════════════════════════════════════════════════════════════════════════════
        if (_memes.Count > 0 && _isMpvReady) EnsureMemePreviewDirector();
        if (_memePreview != null)
        {
            _memePreview.Suspended = _isRecording || _recordArming;
            _memePreview.SetMemes(_memes);
            _memePreview.Tick();
            if (_memePreview.IsActive) { UpdatePlayPauseIconUI(); return; }
        }

        EnforceTrimEndStop();   // VOFIX_01 — replaces the A-B repeat loop
        EnforceCutSkip();       // CUTS_02 — never sit inside footage that was deleted
        UpdateLiveZoomCrop();   // ZOOMLIVE_06 — show the zoom the export will apply
        PumpRecordArming();
        UpdatePlayPauseIconUI();
        UpdatePlayheadUI();
        UpdatePreviewPlayers();
        UpdateSmoothEqMeter();
    }


    private static string ResolveBinaryPath(string fileName, string preferredSubdirectory)
    {
        string processDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string baseDir = AppContext.BaseDirectory;
        string sourceRootCandidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, "..", "..", "..", "..", "..", "binaries", fileName));
        
        string preferredPath = System.IO.Path.Combine(baseDir, preferredSubdirectory, fileName);
        if (System.IO.File.Exists(preferredPath)) return preferredPath;
        
        string rootPath = System.IO.Path.Combine(baseDir, fileName);
        if (System.IO.File.Exists(rootPath)) return rootPath;
        
        string debugPath = System.IO.Path.Combine(processDir, fileName);
        if (System.IO.File.Exists(debugPath)) return debugPath;

        if (System.IO.File.Exists(sourceRootCandidate)) return sourceRootCandidate;

        return fileName;
    }

    private string CreateTempVoiceOverPath()
    {
        Directory.CreateDirectory(_paths.TempDirectory);
        return System.IO.Path.Combine(_paths.TempDirectory, $"voiceover_{Guid.NewGuid():N}.wav");
    }

    private string CreatePersistedVoiceOverPath()
    {
        string voiceOverDir = System.IO.Path.Combine(_paths.ProgramDataRoot, "voiceovers");
        Directory.CreateDirectory(voiceOverDir);
        return System.IO.Path.Combine(voiceOverDir, $"voiceover_{Guid.NewGuid():N}.wav");
    }

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<Border>("TitleBarBorder");
        if (titleBar == null) return;

        titleBar.IsHitTestVisible = true;
        titleBar.DoubleTapped += (s, e) =>
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            e.Handled = true;
        };
        titleBar.PointerPressed += (s, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount < 2)
            {
                try { BeginMoveDrag(e); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            }
        };
    }

    private void CacheControls()
    {
        _videoHost = this.FindControl<MpvVideoView>("VideoHost");
        WirePreviewDetach();
        _micRecordButton = this.FindControl<Button>("MicRecordButton");
        _pauseResumeButton = this.FindControl<Button>("RecordPauseButton");
        if (_pauseResumeButton != null) _pauseResumeButton.Click += ToggleRecordPause;
        _playPauseButton = this.FindControl<Button>("PlayPauseButton");
        _applyButton = this.FindControl<Button>("ApplyButton");
        _cancelButton = this.FindControl<Button>("CancelButton");

        var voHelpButton = this.FindControl<Button>("VoiceOverHelpButton");
        if (voHelpButton != null) voHelpButton.Click += (_, _) => Controls.CoachOverlay.Replay(this);

        _micDeviceComboBox = this.FindControl<ComboBox>("MicDeviceComboBox");
        _recordingStatusText = this.FindControl<TextBlock>("RecordingStatusText");
        _voiceOverHintText = this.FindControl<TextBlock>("VoiceOverHintText");
        _thumbFallbackText = this.FindControl<TextBlock>("ThumbFallbackText");
        _waveformFallbackText = this.FindControl<TextBlock>("WaveformFallbackText");
        _recordingLight = this.FindControl<Ellipse>("RecordingLight");
        _eqMeterCanvas = this.FindControl<Canvas>("EqMeterCanvas");
        _eqMeterTrack = this.FindControl<Border>("EqMeterTrack");
        _timelineRulerCanvas = this.FindControl<Canvas>("TimelineRulerCanvas");
        _waveformCanvas = this.FindControl<Canvas>("WaveformCanvas");
        _takeOverlayCanvas = this.FindControl<Canvas>("TakeOverlayCanvas");
        _readyLamp = this.FindControl<Ellipse>("ReadyLamp");
        _voTimeElapsed = this.FindControl<TextBlock>("VoTimeElapsed");
        _voTimeTotal = this.FindControl<TextBlock>("VoTimeTotal");
        _voTimeRemaining = this.FindControl<TextBlock>("VoTimeRemaining");
        _thumbnailLaneGrid = this.FindControl<Grid>("ThumbnailLaneGrid");
        _waveformLaneGrid = this.FindControl<Grid>("WaveformLaneGrid");
        _thumbnailLaneImage = this.FindControl<Image>("ThumbnailLaneImage");
        _waveformLaneImage = this.FindControl<Image>("WaveformLaneImage");
        _thumbLoadingOverlay = this.FindControl<Border>("ThumbLoadingOverlay");
        _waveformLoadingOverlay = this.FindControl<Border>("WaveformLoadingOverlay");
        _playIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon");
        _pauseIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PauseIcon");
        _duckAudioCb = this.FindControl<CheckBox>("DuckAudioCb");
        _duckMusicCb = this.FindControl<CheckBox>("DuckMusicCb");
        _thumbPlayheadLine = this.FindControl<Border>("ThumbPlayheadLine");
        _wavePlayheadLine = this.FindControl<Border>("WavePlayheadLine");
        
        var selectedTakeToolbar = this.FindControl<Border>("SelectedTakeToolbar");
        var muteTakeBtn = this.FindControl<Button>("MuteTakeButton");
        var deleteTakeBtn = this.FindControl<Button>("DeleteTakeButton");

        if (muteTakeBtn != null)
        {
            muteTakeBtn.Click += (s, e) =>
            {
                if (_selectedSession != null)
                {
                    _selectedSession.IsMuted = !_selectedSession.IsMuted;
                    muteTakeBtn.Content = _selectedSession.IsMuted ? "UNMUTE" : "MUTE";
                    muteTakeBtn.Classes.Remove("Secondary");
                    muteTakeBtn.Classes.Remove("Primary");
                    muteTakeBtn.Classes.Add(_selectedSession.IsMuted ? "Primary" : "Secondary");
                    _renderedSessionCount = -1;
                    UpdatePlayheadUI();
                    UpdateApplyState();
                    Controls.FloatingNotice.Info(this, _selectedSession.IsMuted ? "Take muted" : "Take unmuted");
                }
            };
        }

        if (deleteTakeBtn != null)
        {
            deleteTakeBtn.Click += async (s, e) =>
            {
                if (_selectedSession != null)
                {
                    if (FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.ConfirmVoiceOverDeleteTake)
                    {
                        var confirm = new FortniteVideoSoftware.App.Controls.ConfirmDialogWindow();
                        confirm.SetTitle("Delete Take");
                        confirm.SetMessage("Delete this voice-over take?\nThe recording is removed from disk and cannot be undone.");
                        confirm.SetButtonText("YES, DELETE", "CANCEL");
                        await confirm.ShowDialog(this);
                        if (!confirm.Result) return;
                    }

                    bool isInitial = InitialState?.VoiceOverTakes?.Any(t => string.Equals(t.Path, _selectedSession.WavPath, StringComparison.OrdinalIgnoreCase)) == true;
                    if (!isInitial) TryDeleteFile(_selectedSession.WavPath);
                    // VOTAKE_01 — drop the decoded envelope with the take. Each one is
                    // TakePeakBuckets floats; leaving them behind would grow the window's
                    // footprint every time a take was recorded and thrown away.
                    _takePeaks.Remove(_selectedSession.WavPath);
                    _takePeakPending.Remove(_selectedSession.WavPath);
                    _sessions.Remove(_selectedSession);
                    _selectedSession = null;
                    if (selectedTakeToolbar != null) selectedTakeToolbar.IsVisible = false;
                    _renderedSessionCount = -1;
                    UpdatePlayheadUI();
                    UpdateApplyState();
                    Controls.FloatingNotice.Show(this, "Take deleted");
                }
            };
        }
    }

    private void PopulateMicrophoneDevices()
    {
        if (_micDeviceComboBox == null) return;

        var devices = VoiceRecorder.GetInputDeviceNames();
        RuntimeLog.Info("VoiceOver",
            devices.Count == 0
                ? "Voice-over window opened. No microphone input devices were found."
                : $"Voice-over window opened. {devices.Count} microphone input device(s): {string.Join(" | ", devices)}");
        _micDeviceComboBox.ItemsSource = devices;
        _micDeviceComboBox.IsEnabled = devices.Count > 0;
        _micDeviceComboBox.SelectedIndex = devices.Count > 0 ? 0 : -1;
        ToolTip.SetTip(_micDeviceComboBox, devices.Count > 0
            ? "Choose which microphone records the voiceover"
            : "No microphone input device detected");

        // VOMON_01 — re-point the idle monitor whenever the user picks a different input, so the
        // meter always reflects the device that would actually be recorded from.
        _micDeviceComboBox.SelectionChanged += (_, _) => StartMicMonitor();
        StartMicMonitor();
    }

    /// <summary>
    /// VOMON_01 — opens idle monitoring on the selected device. No-ops while a take is running,
    /// because the recorder owns the device then.
    /// </summary>
    private void StartMicMonitor()
    {
        if (_isRecording || _recordArming || _isClosing) return;
        if (!FortniteVideoSoftware.Core.Media.VoiceRecorder.HasInputDevice)
        {
            UpdateReadyLamp();
            return;
        }

        if (_micMonitor == null)
        {
            _micMonitor = new FortniteVideoSoftware.Core.Media.MicLevelMonitor();
            _micMonitor.LevelChanged += OnMonitorLevel;
        }

        // VOASYNC_02 — waveInOpen blocks; it goes on the chain, after any pending drain.
        var monitor = _micMonitor;
        int deviceIndex = GetSelectedMicrophoneDeviceIndex();
        QueueAudioDeviceWork(() => monitor.Start(deviceIndex));
        UpdateReadyLamp();
    }

    /// <summary>VOMON_01 — releases the device so VoiceRecorder can claim it.</summary>
    private void StopMicMonitor()
    {
        // VOASYNC_02 — waveInClose joins the capture thread, so this blocks too. Queued, which
        // also guarantees the device is free before the recorder's open is reached on the chain.
        var monitor = _micMonitor;
        if (monitor != null) QueueAudioDeviceWork(monitor.Stop);
        UpdateReadyLamp();
    }

    /// <summary>
    /// VOMON_01 — the idle meter feed. Deliberately shares <see cref="_peakVolume"/> with the
    /// recording feed: the meter's job is "what is the microphone hearing right now", and that is
    /// the same question in both states, so there is one path and no way for them to disagree.
    /// </summary>
    private void OnMonitorLevel(object? sender, float level)
    {
        if (_isRecording) return;   // the recorder is driving the meter; don't double-feed it
        _peakVolume = Math.Max(_peakVolume, level);
    }

    /// <summary>
    /// VOROW_01 — the GREEN lamp right of Play/Pause. It answers one question only: is there a
    /// microphone this studio can record from? It is NOT the recording light (that is the red REC
    /// lamp left of the microphone button, driven by UpdateRecordingUi).
    /// </summary>
    private void UpdateReadyLamp()
    {
        if (_readyLamp == null) return;

        bool hasDevice = FortniteVideoSoftware.Core.Media.VoiceRecorder.HasInputDevice;
        bool ready = hasDevice && _isMpvReady;
        _readyLamp.Opacity = ready ? 1.0 : 0.18;

        // The tooltip separates "a device exists" from "we can actually open it". A device that
        // enumerates but will not open — held by another app, or blocked by Windows microphone
        // privacy — is the single most common cause of a silent take, and this is where that
        // shows up BEFORE a take is lost to it.
        string tip;
        if (!hasDevice) tip = "No microphone input device detected";
        else if (!_isMpvReady) tip = "Waiting for the video preview to start";
        else if (_isRecording) tip = "Recording — the meter is being fed by the take in progress";
        else if (_micMonitor?.IsRunning == true) tip = "A microphone is connected and listening. Speak and the meter should move.";
        else tip = "A microphone is listed, but this app could not open it. Check that no other app is using it, and that microphone access is allowed in Windows privacy settings.";
        ToolTip.SetTip(_readyLamp, tip);
    }

    private int GetSelectedMicrophoneDeviceIndex()
    {
        if (_micDeviceComboBox == null || _micDeviceComboBox.SelectedIndex < 0)
        {
            return 0;
        }

        return Math.Max(0, _micDeviceComboBox.SelectedIndex);
    }

    private IBrush GetAppBrush(string resourceKey, IBrush fallback)
    {
        if (Application.Current?.TryFindResource(resourceKey, ActualThemeVariant, out var value) == true &&
            value is IBrush brush)
        {
            return brush;
        }

        return fallback;
    }

    private Color GetAppColor(string resourceKey, Color fallback)
    {
        if (Application.Current?.TryFindResource(resourceKey, ActualThemeVariant, out var value) == true &&
            value is ISolidColorBrush brush)
        {
            return brush.Color;
        }

        return fallback;
    }

    private void AttachResizeGrip()
    {
        var resizeGrip = this.FindControl<Border>("ResizeGrip");
        if (resizeGrip == null) return;

        resizeGrip.Cursor = new Cursor(StandardCursorType.BottomRightCorner);
        resizeGrip.PointerPressed += (s, e) =>
        {
            if (WindowState == WindowState.Maximized) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            try
            {
                BeginResizeDrag(WindowEdge.SouthEast, e);
                e.Handled = true;
            }
            catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        };
    }

    private void WireTimelineSeekSurface(Control surface)
    {
        surface.PointerPressed += (s, e) =>
        {
            surface.Focus();
            e.Pointer.Capture(surface);
            SeekTimelineFromPointer(e, surface, force: true);
        };
        surface.PointerMoved += (s, e) =>
        {
            if (e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed)
            {
                SeekTimelineFromPointer(e, surface, force: false);
            }
        };
        surface.PointerReleased += (s, e) =>
        {
            e.Pointer.Capture(null);
            SeekTimelineFromPointer(e, surface, force: true);
        };
        surface.KeyDown += TimelineSurface_KeyDown;
    }

    private void TimelineSurface_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            if (_selectedSession != null)
            {
                bool isInitial = InitialState?.VoiceOverTakes?.Any(t => string.Equals(t.Path, _selectedSession.WavPath, StringComparison.OrdinalIgnoreCase)) == true;
                if (!isInitial) TryDeleteFile(_selectedSession.WavPath);
                _sessions.Remove(_selectedSession);
                _selectedSession = null;
                _renderedSessionCount = -1;
                UpdatePlayheadUI();
                UpdateApplyState();
                e.Handled = true;
                return;
            }
        }

        if ((_isRecording && !_recordPaused) || _videoHost?.IpcClient == null) return;

        switch (e.Key)
        {
            case Key.Left:
                SeekBySeconds(-1);
                e.Handled = true;
                break;
            case Key.Right:
                SeekBySeconds(1);
                e.Handled = true;
                break;
            case Key.Home:
                SeekToAbsolute(_trimStartSec);
                e.Handled = true;
                break;
            case Key.End:
                SeekToAbsolute(GetEffectiveTimelineEnd());
                e.Handled = true;
                break;
        }
    }

    private void SeekBySeconds(double seconds)
    {
        if (_videoHost?.IpcClient == null) return;
        double target = Math.Clamp(_videoHost.IpcClient.CurrentTime + seconds, _trimStartSec, GetEffectiveTimelineEnd());
        SeekToAbsolute(target);
    }

    private void SeekToAbsolute(double seconds)
    {
        if (_videoHost?.IpcClient == null) return;
        _isCurrentlyFrozen = false;
        _lastFreezeTriggerMs = -1;
        ApplyPreviewSpeedForPosition(seconds * 1000.0);
        
        if (_isSeeking)
        {
            _nextSeekTarget = seconds;
            return;
        }

        _isSeeking = true;
        try
        {
            _ = _videoHost.IpcClient.SendCommandAsync("seek", seconds, "absolute");
        }
        catch
        {
            _isSeeking = false;
        }
    }

    private void OnSeekCompleted()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _isSeeking = false;
            if (_nextSeekTarget.HasValue)
            {
                double target = _nextSeekTarget.Value;
                _nextSeekTarget = null;
                SeekToAbsolute(target);
            }
        });
    }

    private double GetEffectiveTimelineEnd()
    {
        if (_videoHost?.IpcClient == null) return _trimEndSec > 0 ? _trimEndSec : _trimStartSec;
        double videoDuration = _videoHost.IpcClient.Duration;
        return _trimEndSec > 0 ? _trimEndSec : Math.Max(_trimStartSec, videoDuration);
    }

    private double NormalizePreviewPlaybackPosition(double seconds)
    {
        double start = Math.Max(0, _trimStartSec);
        double end = _trimEndSec > start ? _trimEndSec : double.PositiveInfinity;

        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < start)
        {
            return start;
        }

        // ⚠️ VOFIX_02 — SECOND, INDEPENDENT LOOP. This returned `start`, so any position at or
        // near the trim end silently REWOUND to the beginning. Pressing record late in the clip
        // therefore threw the playhead back to the start before the take even began. Clamp to just
        // inside the end instead: the caller asked to be constrained, not rewound.
        if (!double.IsInfinity(end) && seconds >= end - 0.05)
        {
            return Math.Max(start, end - 0.05);
        }

        return seconds;
    }

    /// <summary>
    /// VOFIX_01 — stops at the trim end instead of looping back to the trim start.
    ///
    /// This is what the removed `ab-loop-a`/`ab-loop-b` pair was reaching for. Reaching the end of
    /// the clip PAUSES, and if a take is open it is finalised first, so the recording that was
    /// running is kept rather than being cut mid-loop. Idempotent: once paused at the end it does
    /// nothing on subsequent ticks, so it cannot fight the user pressing play.
    /// </summary>
    private void EnforceTrimEndStop()
    {
        try
        {
            var ipc = _videoHost?.IpcClient;
            if (ipc == null || !_isMpvReady) return;
            if (_trimEndSec <= _trimStartSec) return;
            if (ipc.IsPaused) return;

            double now = ipc.CurrentTime;
            if (now < _trimEndSec - 0.05) return;

            if (_isRecording && !_recordPaused)
            {
                RuntimeLog.Info("VoiceOver",
                    $"Reached the end of the clip at {now:F2}s while recording — finalising the take and stopping.");
                StopRecordingAndPlayback();
                return;
            }

            RuntimeLog.Info("VoiceOver", $"Reached the end of the clip at {now:F2}s — pausing (no loop).");
            _ = ipc.SetPropertyAsync("pause", "yes");
            UpdatePlayPauseIconUI();
        }
        catch (System.Exception ex) { RuntimeLog.SwallowedThrottled(ex); }
    }

    /// <summary>
    /// CUTS_02 — jumps the preview over a deleted section in ONE seek, exactly as the main screen
    /// does. Scrubbing frame-by-frame through footage that is not in the video is both misleading
    /// and, on the main screen, the texture-churn pattern that caused a render-thread hang.
    /// </summary>
    private void EnforceCutSkip()
    {
        try
        {
            if (_cuts.Count == 0) return;
            var ipc = _videoHost?.IpcClient;
            if (ipc == null || !_isMpvReady) return;

            double nowMs = ipc.CurrentTime * 1000.0;
            foreach (var cut in _cuts)
            {
                if (nowMs <= cut.StartMs + 1 || nowMs >= cut.EndMs - 1) continue;

                double toSec = Math.Min(cut.EndMs / 1000.0, GetEffectiveTimelineEnd());
                _ = ipc.SetPropertyAsync("time-pos",
                    toSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                _memePreview?.NotifySeek();   // MEME_07 — a jump, not playback

                // A take being recorded across a cut would be anchored to frames that are not in
                // the finished video, so say so rather than letting it silently mis-time.
                if (_isRecording && !_recordPaused)
                {
                    Controls.FloatingNotice.Info(this, "Skipped a deleted section");
                }
                return;
            }
        }
        catch (System.Exception ex) { RuntimeLog.SwallowedThrottled(ex); }
    }

    private SpeedSegment? FindFreezeSegment(double positionMs)
    {
        foreach (var seg in _speedSegments)
        {
            if (Math.Abs(seg.Speed) < 0.001 &&
                positionMs >= seg.StartMs &&
                positionMs < seg.EndMs)
            {
                return seg;
            }
        }

        return null;
    }

    private double GetSpeedForPosition(double positionMs)
    {
        foreach (var seg in _speedSegments)
        {
            if (positionMs >= seg.StartMs && positionMs < seg.EndMs)
            {
                return Math.Abs(seg.Speed) < 0.001 ? _baseSpeed : Math.Clamp(seg.Speed, 0.1, 4.0);
            }
        }

        return _baseSpeed;
    }

    private void ApplyPreviewSpeedForPosition(double positionMs)
    {
        if (_videoHost?.IpcClient == null) return;
        double targetSpeed = GetSpeedForPosition(positionMs);
        if (Math.Abs(targetSpeed - _lastAppliedSpeed) <= 0.001) return;

        _lastAppliedSpeed = targetSpeed;
        _ = _videoHost.IpcClient.SetPropertyAsync("speed",
            targetSpeed.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture));
    }

    private void UpdatePreviewSpeedAndFreeze(double currentTimeSec)
    {
        if (_videoHost?.IpcClient == null) return;

        double currentAbsMs = currentTimeSec * 1000.0;
        if (_isCurrentlyFrozen)
        {
            if ((DateTime.UtcNow - _freezeStartTime).TotalSeconds >= Math.Max(0.05, (_currentFreezeDurationMs / 1000.0)))
            {
                _isCurrentlyFrozen = false;
                _ = _videoHost.IpcClient.SetPropertyAsync("pause", "no");
            }
            return;
        }

        if (_videoHost.IpcClient.IsPaused) return;

        var freeze = FindFreezeSegment(currentAbsMs);
        if (freeze != null && Math.Abs(freeze.StartMs - _lastFreezeTriggerMs) > 1.0)
        {
            _lastFreezeTriggerMs = freeze.StartMs;
            _currentFreezeDurationMs = Math.Max(50.0, freeze.EndMs - freeze.StartMs);
            _isCurrentlyFrozen = true;
            _freezeStartTime = DateTime.UtcNow;
            _ = _videoHost.IpcClient.SetPropertyAsync("time-pos", (freeze.StartMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture));
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
            return;
        }
        if (freeze == null && _lastFreezeTriggerMs >= 0 && Math.Abs(currentAbsMs - _lastFreezeTriggerMs) > 1000.0)
        {
            _lastFreezeTriggerMs = -1;
        }

        ApplyPreviewSpeedForPosition(currentAbsMs);
    }

    private void WireEffectStateControls()
    {
        if (_duckAudioCb != null)
        {
            _duckAudioCb.IsCheckedChanged += (_, _) => UpdateApplyState();
        }
        if (_duckMusicCb != null)
        {
            _duckMusicCb.IsCheckedChanged += (_, _) => UpdateApplyState();
        }

        // VOPROT_02 — a brand-new voice-over (no InitialState) still has to obey the policy.
        // With an InitialState the Loaded handler calls this again with the project's own values.
        ApplyVoiceProtectionPolicy(null, null);
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // VOPROT_02 — WHERE THE TWO PROTECTION CHECKBOXES GET THEIR VALUE.
    //
    // Three sources, in strict order of authority:
    //   1. Settings says Always On / Always Off  -> that value, and the box is DISABLED.
    //   2. This project already had a voice-over -> the choice saved with that project.
    //   3. Neither                               -> the choice the user applied last time.
    //
    // On an Always mode the box is still shown in the state that will actually be used, and its
    // tooltip says where the decision was made. Hiding it, or leaving it ticked while the export
    // ignored it, would both read as a bug — the user must be able to see the truth and find the
    // switch that changed it.
    // ══════════════════════════════════════════════════════════════════════════════
    private void ApplyVoiceProtectionPolicy(bool? projectGame, bool? projectMusic)
    {
        var settings = FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance;

        Apply(_duckAudioCb, settings.VoiceProtectGameMode, projectGame, settings.VoiceProtectGameLast,
            "Protect VoiceOver Recording from Game-Play Sound");
        Apply(_duckMusicCb, settings.VoiceProtectMusicMode, projectMusic, settings.VoiceProtectMusicLast,
            "Protect VoiceOver Recording from Music");

        static void Apply(CheckBox? box, FortniteVideoSoftware.App.Infrastructure.VoiceProtectionMode mode,
                          bool? projectChoice, bool rememberedChoice, string label)
        {
            if (box == null) return;

            switch (mode)
            {
                case FortniteVideoSoftware.App.Infrastructure.VoiceProtectionMode.AlwaysOn:
                    box.IsChecked = true;
                    box.IsEnabled = false;
                    ToolTip.SetTip(box, $"Settings > Sound & Music is set to always protect your voice, so \"{label}\" is locked on. Change it there to unlock this.");
                    break;

                case FortniteVideoSoftware.App.Infrastructure.VoiceProtectionMode.AlwaysOff:
                    box.IsChecked = false;
                    box.IsEnabled = false;
                    ToolTip.SetTip(box, $"Settings > Sound & Music is set to never protect your voice, so \"{label}\" is locked off. Change it there to unlock this.");
                    break;

                default:
                    box.IsChecked = projectChoice ?? rememberedChoice;
                    box.IsEnabled = true;
                    break;
            }
        }
    }

    /// <summary>
    /// VOPROT_02 — stores the applied choices as the "last time" values, but ONLY for a checkbox
    /// the user was actually allowed to set. Writing back a locked box would let an Always mode
    /// quietly overwrite the preference the user would return to if they switched back to
    /// Remember — the setting would appear to change itself.
    /// </summary>
    private static void RememberVoiceProtectionChoices(bool duckGame, bool duckMusic)
    {
        var settings = FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance;
        bool dirty = false;

        if (settings.VoiceProtectGameMode == FortniteVideoSoftware.App.Infrastructure.VoiceProtectionMode.RememberLastChoice &&
            settings.VoiceProtectGameLast != duckGame)
        {
            settings.VoiceProtectGameLast = duckGame;
            dirty = true;
        }

        if (settings.VoiceProtectMusicMode == FortniteVideoSoftware.App.Infrastructure.VoiceProtectionMode.RememberLastChoice &&
            settings.VoiceProtectMusicLast != duckMusic)
        {
            settings.VoiceProtectMusicLast = duckMusic;
            dirty = true;
        }

        if (!dirty) return;

        try
        {
            FortniteVideoSoftware.App.Infrastructure.SettingsManager.Save();
            RuntimeLog.Info("VoiceOver",
                $"Voice-protection choices remembered: game={duckGame}, music={duckMusic}.");
        }
        catch (Exception ex)
        {
            // A preference that cannot be written is not worth failing an export over.
            RuntimeLog.Fail("VoiceOver", $"Could not save the voice-protection choices: {ex.Message}");
        }
    }

    private void UpdateTransportState()
    {
        bool hasInputDevice = VoiceRecorder.HasInputDevice;
        if (_micRecordButton != null)
        {
            _micRecordButton.IsEnabled = _isMpvReady && (_isRecording || hasInputDevice);
            ToolTip.SetTip(_micRecordButton, hasInputDevice
                ? "Start or stop voiceover recording (V)"
                : "No microphone input device detected");
        }
        if (_playPauseButton != null)
        {
            _playPauseButton.IsEnabled = _isMpvReady && !_isRecording;
            _playPauseButton.IsVisible = !_isRecording;
        }
        if (_micDeviceComboBox != null) _micDeviceComboBox.IsEnabled = hasInputDevice && !_isRecording;

        if (_pauseResumeButton != null)
        {
            _pauseResumeButton.IsVisible = _isRecording;
            _pauseResumeButton.IsEnabled = _isRecording && !_recordArming && !_recordOpening;
            var rpi = this.FindControl<Avalonia.Controls.Shapes.Path>("RecordPauseIcon");
            var rri = this.FindControl<Avalonia.Controls.Shapes.Path>("RecordResumeIcon");
            if (rpi != null) rpi.IsVisible = !_recordPaused;
            if (rri != null) rri.IsVisible = _recordPaused;
        }

        if (!hasInputDevice && !_isRecording && _recordingStatusText != null)
        {
            _recordingStatusText.Text = "NO MIC";
            _recordingStatusText.Foreground = GetAppBrush("AppWarningBrush", Brushes.Yellow);
        }

        UpdateReadyLamp();
    }

    private bool HasSavedVoiceOverSession()
    {
        foreach (var session in _sessions)
        {
            if (!session.IsMuted &&
                session.EndSec > session.StartSec &&
                !string.IsNullOrWhiteSpace(session.WavPath) &&
                System.IO.File.Exists(session.WavPath))
            {
                return true;
            }
        }
        return false;
    }

    private bool HasApplicableVoiceEffect()
    {
        // VOASYNC_02 — a take whose drain has not landed yet is still a take. Without this the
        // Apply button and the discard prompt would both go blind for the ~100 ms after stop.
        return _isRecording || _pendingFinalizes.Count > 0 || HasSavedVoiceOverSession();
    }

    private void UpdateApplyState(string? message = null)
    {
        bool canApply = HasApplicableVoiceEffect();
        string? effectiveMessage = message;
        if (effectiveMessage == null && !canApply && !VoiceRecorder.HasInputDevice)
        {
            effectiveMessage = "No microphone input detected. You must record a take to apply voiceover.";
        }

        if (_applyButton != null)
        {
            _applyButton.IsEnabled = canApply;
            ToolTip.SetTip(_applyButton, canApply
                ? "Apply recorded voiceover"
                : "Record a take before applying");
        }

        if (_voiceOverHintText != null)
        {
            _voiceOverHintText.Text = effectiveMessage ?? (canApply
                ? "Ready to apply the current voiceover changes. Cancel discards unapplied takes."
                : "Record a take before applying. Cancel discards unapplied takes.");
            _voiceOverHintText.Foreground = canApply
                ? GetAppBrush("AppTextPrimaryBrush", Brushes.White)
                : GetAppBrush("AppTextMutedBrush", Brushes.Gray);
        }
    }


    private async Task GenerateLanesAsync()
    {
        if (_videoHost?.IpcClient == null) return;
        
        while (_videoHost.IpcClient.Duration <= 0)
        {
            await Task.Delay(100);
            if (_isClosing) return;
        }

        double videoDuration = _videoHost.IpcClient.Duration;
        double durationSec = (_trimEndSec > 0 ? _trimEndSec : videoDuration) - _trimStartSec;
        if (durationSec <= 0) durationSec = videoDuration;
        string ffmpeg = ResolveBinaryPath("ffmpeg.exe", "backend");

        RetireGenerationCts(_generationCts);
        _generationCts = new System.Threading.CancellationTokenSource();
        var token = _generationCts.Token;

        if (_thumbFallbackText != null) _thumbFallbackText.IsVisible = false;
        if (_waveformFallbackText != null) _waveformFallbackText.IsVisible = false;
        if (_thumbLoadingOverlay != null) _thumbLoadingOverlay.IsVisible = true;
        if (_waveformLoadingOverlay != null) _waveformLoadingOverlay.IsVisible = true;

        string localVideoPath = _videoPath ?? "";
        double localTrimStart = _trimStartSec;

        string thumbTempDir = FortniteVideoSoftware.Core.Infrastructure.ApplicationPaths.CreateDefault().TempDirectory;
        var warmedStrip = FortniteVideoSoftware.App.Services.FilmstripPrewarm.TryTake(
            localVideoPath, localTrimStart, durationSec);
        bool warmedMounted = false;
        if (warmedStrip != null)
        {
            if (_thumbnailLaneImage != null)
            {
                (_thumbnailLaneImage.Source as IDisposable)?.Dispose();
                _thumbnailLaneImage.Source = warmedStrip;
                if (_thumbLoadingOverlay != null) _thumbLoadingOverlay.IsVisible = false;
                if (_thumbFallbackText != null) _thumbFallbackText.IsVisible = false;
                warmedMounted = true;
                CoreLogger.Info("VoiceOver", "Thumbnail lane served from the background prewarm.");
            }
            else
            {
                try { warmedStrip.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            }
        }

        var thumbTask = warmedMounted ? Task.FromResult(true) : Task.Run(async () =>
        {
            try
            {
                return await ThumbnailStripGenerator.StreamAsync(
                    ffmpeg, localVideoPath, localTrimStart, durationSec, token,
                    onReady: wb =>
                    {
                        if (_thumbnailLaneImage == null) return;
                        (_thumbnailLaneImage.Source as IDisposable)?.Dispose();
                        _thumbnailLaneImage.Source = wb;
                        if (_thumbLoadingOverlay != null) _thumbLoadingOverlay.IsVisible = false;
                        if (_thumbFallbackText != null) _thumbFallbackText.IsVisible = false;
                    },
                    onFrame: () => _thumbnailLaneImage?.InvalidateVisual(),
                    logTag: "VoiceOver");
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                CoreLogger.Fail("VoiceOver", $"Thumbnail lane generation failed: {ex.Message}");
                return false;
            }
        });

        var waveTask = Task.Run(async () =>
        {
            try
            {
                return await FortniteVideoSoftware.Core.Media.WaveformGenerator.GenerateWaveformImageAsync(
                        ffmpeg, localVideoPath, 1200, 60, localTrimStart, durationSec, token);
            }
            catch (Exception ex)
            {
                CoreLogger.Fail("VoiceOver", $"Waveform lane generation failed: {ex.Message}");
            }
            return null;
        });

        bool thumbStreamed = await thumbTask;
        string? wavePath = await waveTask;

        if (token.IsCancellationRequested) return;

        string? thumbPath = null;
        if (!thumbStreamed)
        {
            try
            {
                thumbPath = await ThumbnailStripGenerator.GenerateAsync(
                    ffmpeg, localVideoPath, thumbTempDir, localTrimStart, durationSec, token,
                    logTag: "VoiceOver");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                CoreLogger.Fail("VoiceOver", $"Thumbnail lane fallback failed: {ex.Message}");
            }
        }

        bool thumbLoaded = thumbStreamed;
        bool waveformLoaded = false;

        if (thumbPath != null && _thumbnailLaneImage != null)
        {
            try
            {
                using var fs = System.IO.File.OpenRead(thumbPath);
                (_thumbnailLaneImage.Source as IDisposable)?.Dispose();
                _thumbnailLaneImage.Source = new Bitmap(fs);
                _tempThumbPath = thumbPath;
                thumbLoaded = true;
            }
            catch (Exception ex)
            {
                CoreLogger.Fail("VoiceOver", $"Thumbnail lane image load failed: {ex.Message}");
            }
        }
        if (_thumbLoadingOverlay != null) _thumbLoadingOverlay.IsVisible = false;
        if (_thumbFallbackText != null)
        {
            _thumbFallbackText.Text = thumbLoaded ? "" : "Frame preview unavailable.";
            _thumbFallbackText.IsVisible = !thumbLoaded;
        }

        if (wavePath != null && _waveformLaneImage != null)
        {
            try
            {
                using var fs = System.IO.File.OpenRead(wavePath);
                (_waveformLaneImage.Source as IDisposable)?.Dispose();
                _waveformLaneImage.Source = new Bitmap(fs);
                _tempWavePath = wavePath;
                waveformLoaded = true;
            }
            catch (Exception ex)
            {
                CoreLogger.Fail("VoiceOver", $"Waveform lane image load failed: {ex.Message}");
            }
        }
        if (_waveformLoadingOverlay != null) _waveformLoadingOverlay.IsVisible = false;
        if (_waveformFallbackText != null)
        {
            _waveformFallbackText.Text = waveformLoaded ? "" : "Waveform unavailable.";
            _waveformFallbackText.IsVisible = !waveformLoaded;
        }
    }

    private void UpdatePlayPauseIconUI()
    {
        if (_videoHost?.IpcClient == null) return;
        bool isPaused = _videoHost.IpcClient.IsPaused && !_isCurrentlyFrozen;
        if (_playIcon != null) _playIcon.IsVisible = isPaused;
        if (_pauseIcon != null) _pauseIcon.IsVisible = !isPaused;
    }

    /// <summary>
    /// VOTL_01 — formats a clock the same way the main screen's timeline does, so the two windows
    /// read as one product. Always hh:mm:ss; a leading sign is the caller's business.
    /// </summary>
    private static string FormatClock(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // VOTAKE_01 — THE VOICE ENVELOPE IS DECODED OFF THE INTERFACE THREAD.
    //
    // Drawing a take's waveform means reading the whole WAV and reducing it to one peak per
    // horizontal pixel. On the interface thread that is a visible freeze the instant a take is
    // saved — the exact stutter this screen was reported for. So: the red block is drawn the
    // moment the take exists (instant feedback), a thread-pool worker decodes the envelope, and
    // when it lands the block is redrawn with the shape inside it. Nothing ever waits.
    //
    // The dictionary is written ONLY on the interface thread (inside the Post below) and read only
    // there, so it needs no lock. `_takePeakPending` stops a second worker being queued for a file
    // whose first worker has not finished.
    // ══════════════════════════════════════════════════════════════════════════════
    private void EnsureTakePeaksAsync(string? wavPath)
    {
        if (string.IsNullOrWhiteSpace(wavPath)) return;
        string path = wavPath!;
        if (_takePeaks.ContainsKey(path)) return;
        if (!_takePeakPending.Add(path)) return;

        _ = Task.Run(() =>
        {
            float[]? peaks = null;
            try
            {
                peaks = DecodePeaks(path, TakePeakBuckets);
            }
            catch (Exception ex)
            {
                CoreLogger.Debug("VoiceOver", $"Could not build the waveform for '{System.IO.Path.GetFileName(path)}': {ex.Message}");
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_isClosing) return;
                _takePeakPending.Remove(path);
                // An empty array is still a RESULT: it stops the file being decoded again on every
                // redraw when the take is silent or unreadable.
                _takePeaks[path] = peaks ?? Array.Empty<float>();
                _renderedSessionCount = -1;   // force one redraw with the shape in place
                UpdatePlayheadUI();
            });
        });
    }

    /// <summary>
    /// VOTAKE_01 — reduces a WAV to <paramref name="buckets"/> absolute peaks (0..1).
    /// Runs on a worker thread; touches no interface state.
    /// </summary>
    private static float[] DecodePeaks(string path, int buckets)
    {
        if (buckets < 1) buckets = 1;
        using var reader = new NAudio.Wave.AudioFileReader(path);

        long totalSamples = reader.Length / (reader.WaveFormat.BitsPerSample / 8);
        if (totalSamples <= 0) return Array.Empty<float>();

        var peaks = new float[buckets];
        var buffer = new float[8192];
        long samplesPerBucket = Math.Max(1, totalSamples / buckets);

        long read = 0;
        int n;
        while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                int bucket = (int)Math.Min(buckets - 1, (read + i) / samplesPerBucket);
                float v = buffer[i];
                if (v < 0) v = -v;
                if (v > peaks[bucket]) peaks[bucket] = v;
            }
            read += n;
        }

        return peaks;
    }

    private void UpdatePlayheadUI()
    {
        if (_videoHost?.IpcClient == null) return;
        double currentTime = _videoHost.IpcClient.CurrentTime;
        double videoDuration = _videoHost.IpcClient.Duration;
        if (videoDuration <= 0) return;
        UpdatePreviewSpeedAndFreeze(currentTime);

        double effectiveDuration = (_trimEndSec > 0 ? _trimEndSec : videoDuration) - _trimStartSec;
        if (effectiveDuration <= 0) effectiveDuration = videoDuration;

        double visualTime = _dragSeekTimeSec ?? currentTime;
        double relativeTime = visualTime - _trimStartSec;
        double fraction = Math.Clamp(relativeTime / effectiveDuration, 0, 1);

        // VOTL_01 — the clock strip. Elapsed and remaining are measured inside the TRIMMED range,
        // because that range is the whole world on this screen: 00:00:00 here is MARK START.
        double elapsed = Math.Clamp(relativeTime, 0, effectiveDuration);
        if (_voTimeElapsed != null) _voTimeElapsed.Text = FormatClock(elapsed);
        if (_voTimeTotal != null) _voTimeTotal.Text = FormatClock(effectiveDuration);
        if (_voTimeRemaining != null) _voTimeRemaining.Text = "-" + FormatClock(effectiveDuration - elapsed);

        if (_timelineRulerCanvas != null && _timelineRulerCanvas.Bounds.Width > 0)
        {
            double width = _timelineRulerCanvas.Bounds.Width;
            double height = Math.Max(1, _timelineRulerCanvas.Bounds.Height);

            if (Math.Abs(_renderedScaleWidth - width) > 0.5 ||
                Math.Abs(_renderedScaleDuration - effectiveDuration) > 0.01)
            {
                RebuildRulerScale(_timelineRulerCanvas, effectiveDuration, width, height);
            }

            EnsureRulerDynamicVisuals(_timelineRulerCanvas, height);

            double caretX = Math.Clamp(fraction * width, 0, width);
            if (_playheadCaret != null)
            {
                Canvas.SetLeft(_playheadCaret, caretX);
                // Sits ON the boundary between the ruler and the film lane, pointing down at the
                // frame it is parked on.
                Canvas.SetTop(_playheadCaret, Math.Max(0, height - VoCaretHeight));
            }
            if (_rulerPlayheadLine != null)
            {
                _rulerPlayheadLine.StartPoint = new Avalonia.Point(0, 0);
                _rulerPlayheadLine.EndPoint = new Avalonia.Point(0, height);
                Canvas.SetLeft(_rulerPlayheadLine, caretX);
            }
        }

        // VOTAKE_01 — takes are drawn over the FILM LANE now, not on the ruler.
        if (_takeOverlayCanvas != null && _takeOverlayCanvas.Bounds.Width > 0)
        {
            double laneWidth = _takeOverlayCanvas.Bounds.Width;
            double laneHeight = Math.Max(1, _takeOverlayCanvas.Bounds.Height);

            if (_renderedSessionCount != _sessions.Count ||
                Math.Abs(_renderedRulerWidth - laneWidth) > 0.5 ||
                Math.Abs(_renderedRulerHeight - laneHeight) > 0.5)
            {
                RebuildTakeRegions(_takeOverlayCanvas, effectiveDuration, laneWidth, laneHeight);
            }

            EnsureLiveRecordingRegion(_takeOverlayCanvas, laneHeight);
            UpdateCurrentRecordingRegion(effectiveDuration, laneWidth, laneHeight, fraction);
        }

        var toolbar = this.FindControl<Border>("SelectedTakeToolbar");
        if (toolbar != null) toolbar.IsVisible = _selectedSession != null;
        var muteBtn = this.FindControl<Button>("MuteTakeButton");
        if (muteBtn != null && _selectedSession != null)
        {
            muteBtn.Content = _selectedSession.IsMuted ? "UNMUTE" : "MUTE";
            muteBtn.Classes.Remove("Secondary");
            muteBtn.Classes.Remove("Primary");
            muteBtn.Classes.Add(_selectedSession.IsMuted ? "Primary" : "Secondary");
        }

        if (_thumbnailLaneGrid != null && _thumbnailLaneGrid.Bounds.Width > 0)
        {
            if (_thumbPlayheadLine != null)
            {
                double x = fraction * _thumbnailLaneGrid.Bounds.Width;
                _thumbPlayheadLine.Margin = new Thickness(x, 0, 0, 0);
            }
        }

        if (_waveformLaneGrid != null && _waveformLaneGrid.Bounds.Width > 0)
        {
            if (_wavePlayheadLine != null)
            {
                double x = fraction * _waveformLaneGrid.Bounds.Width;
                _wavePlayheadLine.Margin = new Thickness(x, 0, 0, 0);
            }
        }
    }

    /// <summary>
    /// CUTS_02 — paints the sections the Speed Editor removed, so this window stops pretending
    /// they are still part of the video. Absolute source ms in, lane pixels out.
    /// </summary>
    private void DrawDeletedSections(Canvas lane, double effectiveDuration, double width, double height)
    {
        if (_cuts.Count == 0 || effectiveDuration <= 0 || width <= 0) return;

        var fill = new SolidColorBrush(Color.FromArgb(215, 26, 26, 30));
        var hatch = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));

        foreach (var cut in _cuts)
        {
            double x1 = Math.Clamp(((cut.StartMs / 1000.0) - _trimStartSec) / effectiveDuration * width, 0, width);
            double x2 = Math.Clamp(((cut.EndMs / 1000.0) - _trimStartSec) / effectiveDuration * width, 0, width);
            double bandWidth = x2 - x1;
            if (bandWidth <= 0.5) continue;

            var band = new Rectangle
            {
                Fill = fill,
                Width = bandWidth,
                Height = height,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(band, x1);
            Canvas.SetTop(band, 0);
            lane.Children.Add(band);

            // Diagonal hatching, drawn as one geometry rather than N shapes so a long cut on a wide
            // window does not add hundreds of controls to the visual tree on every redraw.
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                for (double x = -height; x < bandWidth; x += 7)
                {
                    double sx = Math.Max(0, x);
                    double sy = x < 0 ? -x : 0;
                    double ex = Math.Min(bandWidth, x + height);
                    double ey = height - Math.Max(0, (x + height) - bandWidth);
                    if (ex <= sx) continue;
                    ctx.BeginFigure(new Avalonia.Point(sx, sy), false);
                    ctx.LineTo(new Avalonia.Point(ex, ey));
                    ctx.EndFigure(false);
                }
            }

            var hatchPath = new Avalonia.Controls.Shapes.Path
            {
                Data = geometry,
                Stroke = hatch,
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(hatchPath, x1);
            Canvas.SetTop(hatchPath, 0);
            lane.Children.Add(hatchPath);

            if (bandWidth >= 46)
            {
                var label = new TextBlock
                {
                    Text = "DELETED",
                    FontSize = Infrastructure.ThemeManager.ScaledFontSize(9),
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)),
                    IsHitTestVisible = false,
                    Width = bandWidth,
                    TextAlignment = TextAlignment.Center
                };
                Canvas.SetLeft(label, x1);
                Canvas.SetTop(label, Math.Max(0, height / 2 - 7));
                lane.Children.Add(label);
            }
        }
    }

    /// <summary>VOTL_01 — the caret is 18 wide and 14 tall (was 12 x 10) so it is grabbable and readable.</summary>
    private const double VoCaretHeight = 14.0;
    private const double VoCaretHalfWidth = 9.0;

    /// <summary>
    /// VOTL_01 — the time grid: minor ticks, labelled major ticks, and a baseline.
    ///
    /// Rebuilt only when the WIDTH or the CLIP LENGTH changes, never per frame — this canvas also
    /// hosts the caret and the playhead line, which are kept as fields and repositioned instead of
    /// being recreated. Tick spacing follows the same ladder as the main screen's timeline so the
    /// two rulers agree about what "every 10 seconds" looks like.
    /// </summary>
    private void RebuildRulerScale(Canvas ruler, double effectiveDuration, double width, double height)
    {
        ruler.Children.Clear();
        _playheadCaret = null;
        _rulerPlayheadLine = null;

        _renderedScaleWidth = width;
        _renderedScaleDuration = effectiveDuration;

        if (effectiveDuration <= 0 || width <= 0) return;

        var tickBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
        var majorBrush = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255));
        var labelBrush = Infrastructure.ThemeResources.Brush(this, "AppTextMutedBrush", Brushes.Gainsboro);
        double labelFont = Infrastructure.ThemeManager.ScaledFontSize(9);

        double major = 5;
        if (effectiveDuration > 3600) major = 300;
        else if (effectiveDuration > 1800) major = 60;
        else if (effectiveDuration > 300) major = 30;
        else if (effectiveDuration > 60) major = 10;
        double minor = major / 5.0;

        for (double t = 0; t <= effectiveDuration + 1e-6; t += minor)
        {
            double tx = (t / effectiveDuration) * width;
            bool isMajor = Math.Abs(t / major - Math.Round(t / major)) < 1e-6;

            var tick = new Rectangle
            {
                Fill = isMajor ? majorBrush : tickBrush,
                Width = 1,
                Height = isMajor ? 10 : 5,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(tick, tx);
            Canvas.SetTop(tick, Math.Max(0, height - (isMajor ? 10 : 5)));
            ruler.Children.Add(tick);

            if (!isMajor) continue;

            var label = new TextBlock
            {
                Text = FormatClock(t),
                Foreground = labelBrush,
                FontSize = labelFont,
                IsHitTestVisible = false
            };
            // Keep the first and last labels inside the canvas instead of half-off each edge.
            double desired = tx + 3;
            Canvas.SetLeft(label, Math.Max(0, Math.Min(Math.Max(0, width - 48), desired)));
            Canvas.SetTop(label, 1);
            ruler.Children.Add(label);
        }

        var baseline = new Rectangle
        {
            Fill = tickBrush,
            Width = width,
            Height = 1,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(baseline, 0);
        Canvas.SetTop(baseline, Math.Max(0, height - 1));
        ruler.Children.Add(baseline);

        // CUTS_02 — a slim marker on the ruler too, so the deleted spans are visible even when the
        // film strip has not finished generating.
        foreach (var cut in _cuts)
        {
            double cx1 = Math.Clamp(((cut.StartMs / 1000.0) - _trimStartSec) / effectiveDuration * width, 0, width);
            double cx2 = Math.Clamp(((cut.EndMs / 1000.0) - _trimStartSec) / effectiveDuration * width, 0, width);
            if (cx2 - cx1 <= 0.5) continue;

            var mark = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(150, 150, 150, 160)),
                Width = cx2 - cx1,
                Height = 4,
                IsHitTestVisible = false
            };
            ToolTip.SetTip(mark, "This part was deleted in the Speed Editor and will not be in your video.");
            Canvas.SetLeft(mark, cx1);
            Canvas.SetTop(mark, Math.Max(0, height - 5));
            ruler.Children.Add(mark);
        }
    }

    /// <summary>
    /// VOTAKE_01 — draws every saved take as a semi-transparent red block over the film strip,
    /// with the recorded voice drawn INSIDE it once its envelope has been decoded.
    ///
    /// The block uses an ALPHA FILL rather than <c>Opacity</c>, because Opacity on the rectangle
    /// would be inherited by nothing (it is a sibling of the waveform) — but Opacity on a shared
    /// parent would also fade the waveform, which is the one thing that has to stay legible.
    /// </summary>
    private void RebuildTakeRegions(Canvas lane, double effectiveDuration, double width, double height)
    {
        lane.Children.Clear();
        _currentSessionRegionRect = null;

        // CUTS_02 — deleted footage is drawn FIRST so takes and the live recording block sit on
        // top of it. Grey with diagonal hatching rather than a colour: it must not be mistaken for
        // a take, and it must read as "there is nothing here" rather than "here is something red".
        DrawDeletedSections(lane, effectiveDuration, width, height);

        var dangerBase = Infrastructure.ThemeResources.Colour(this, "AppDangerColor", Color.FromRgb(168, 50, 50));
        // No AppBorderColor token exists (the border is a brush-only token), and a muted take is
        // deliberately drawn as neutral grey rather than a tinted red, so this is a literal.
        var mutedBase = Color.FromRgb(110, 110, 110);

        foreach (var session in _sessions)
        {
            double startFrac = (session.RenderStartSec - _trimStartSec) / effectiveDuration;
            double endFrac = (session.RenderEndSec - _trimStartSec) / effectiveDuration;
            double x1 = Math.Clamp(startFrac * width, 0, width);
            double x2 = Math.Clamp(endFrac * width, 0, width);
            if (x2 <= x1) continue;

            bool isSelected = session == _selectedSession;
            double blockWidth = x2 - x1;

            var baseColour = session.IsMuted ? mutedBase : dangerBase;
            byte alpha = session.IsMuted ? (byte)70 : (isSelected ? (byte)190 : (byte)120);

            var region = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(alpha, baseColour.R, baseColour.G, baseColour.B)),
                Width = blockWidth,
                Height = height,
                IsHitTestVisible = true,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            ToolTip.SetTip(region, session.IsMuted
                ? "Muted take — click to select it, then UNMUTE in the right-hand panel"
                : $"Voice take, {session.RenderEndSec - session.RenderStartSec:0.0}s. Click to select it.");

            region.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(lane).Properties.IsLeftButtonPressed)
                {
                    lane.Focus();
                    _selectedSession = session;
                    _renderedSessionCount = -1;
                    UpdatePlayheadUI();
                    e.Handled = true;
                }
            };

            Canvas.SetLeft(region, x1);
            Canvas.SetTop(region, 0);
            lane.Children.Add(region);

            if (isSelected)
            {
                var outline = new Rectangle
                {
                    Stroke = Infrastructure.ThemeResources.Brush(this, "AppSuccessBrush", Brushes.LimeGreen),
                    StrokeThickness = 2,
                    Fill = Brushes.Transparent,
                    Width = blockWidth,
                    Height = height,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(outline, x1);
                Canvas.SetTop(outline, 0);
                lane.Children.Add(outline);
            }

            // The voice itself. Absent on the first frame after a take is saved — the worker is
            // still decoding — and it simply appears when ready. This is why recording no longer
            // stalls: the block is never waiting on the shape.
            var shape = BuildTakeWaveformPath(session, blockWidth, height);
            if (shape != null)
            {
                Canvas.SetLeft(shape, x1);
                Canvas.SetTop(shape, 0);
                lane.Children.Add(shape);
            }
            else
            {
                EnsureTakePeaksAsync(session.WavPath);
            }

            if (isSelected)
            {
                var handleBrush = Infrastructure.ThemeResources.Brush(this, "AppSuccessBrush", Brushes.LimeGreen);

                var leftHandle = new Rectangle
                {
                    Width = 10,
                    Height = height,
                    Fill = handleBrush,
                    IsHitTestVisible = true,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast)
                };
                ToolTip.SetTip(leftHandle, "Drag to trim the start of this take");
                leftHandle.PointerPressed += (s, e) =>
                {
                    if (e.GetCurrentPoint(lane).Properties.IsLeftButtonPressed)
                    {
                        _draggingSession = session;
                        _isDraggingStartEdge = true;
                        e.Handled = true;
                    }
                };
                Canvas.SetLeft(leftHandle, Math.Max(0, x1 - 5));
                Canvas.SetTop(leftHandle, 0);
                lane.Children.Add(leftHandle);

                var rightHandle = new Rectangle
                {
                    Width = 10,
                    Height = height,
                    Fill = handleBrush,
                    IsHitTestVisible = true,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast)
                };
                ToolTip.SetTip(rightHandle, "Drag to trim the end of this take");
                rightHandle.PointerPressed += (s, e) =>
                {
                    if (e.GetCurrentPoint(lane).Properties.IsLeftButtonPressed)
                    {
                        _draggingSession = session;
                        _isDraggingEndEdge = true;
                        e.Handled = true;
                    }
                };
                Canvas.SetLeft(rightHandle, Math.Min(Math.Max(0, width - 10), x2 - 5));
                Canvas.SetTop(rightHandle, 0);
                lane.Children.Add(rightHandle);
            }
        }

        _renderedSessionCount = _sessions.Count;
        _renderedRulerWidth = width;
        _renderedRulerHeight = height;
    }

    /// <summary>
    /// VOTAKE_01 — one vertical line per pixel column, mirrored around the lane's centre.
    /// Returns null while the envelope is still being decoded, or when the take is silent.
    ///
    /// The take may have been TRIMMED by its edge handles, so the slice of the envelope drawn is
    /// the slice that will actually be exported — otherwise the picture would keep showing audio
    /// the user had already trimmed away.
    /// </summary>
    private Avalonia.Controls.Shapes.Path? BuildTakeWaveformPath(VoiceOverSession session, double blockWidth, double height)
    {
        if (blockWidth < 2 || height < 4) return null;
        if (string.IsNullOrWhiteSpace(session.WavPath)) return null;
        if (!_takePeaks.TryGetValue(session.WavPath, out var peaks) || peaks.Length == 0) return null;

        double fullLength = Math.Max(0.001, session.EndSec - session.StartSec);
        double fromFrac = Math.Clamp(session.TrimLeftSec / fullLength, 0, 1);
        double toFrac = Math.Clamp(1.0 - (session.TrimRightSec / fullLength), 0, 1);
        if (toFrac <= fromFrac) return null;

        double centreY = height / 2.0;
        double maxHalf = (height / 2.0) - 3.0;
        if (maxHalf < 2) maxHalf = 2;

        int columns = (int)Math.Max(1, Math.Floor(blockWidth));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (int i = 0; i < columns; i++)
            {
                double frac = fromFrac + (toFrac - fromFrac) * (i / (double)columns);
                int idx = Math.Clamp((int)(frac * peaks.Length), 0, peaks.Length - 1);
                double half = Math.Max(1.0, peaks[idx] * maxHalf);
                double x = i + 0.5;
                ctx.BeginFigure(new Avalonia.Point(x, centreY - half), false);
                ctx.LineTo(new Avalonia.Point(x, centreY + half));
                ctx.EndFigure(false);
            }
        }

        return new Avalonia.Controls.Shapes.Path
        {
            Data = geometry,
            Stroke = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
            StrokeThickness = 1,
            IsHitTestVisible = false
        };
    }

    /// <summary>VOTL_01 — creates the caret and playhead line once, on the ruler canvas.</summary>
    private void EnsureRulerDynamicVisuals(Canvas ruler, double height)
    {
        if (_playheadCaret == null)
        {
            _playheadCaret = new Polygon
            {
                Points = new List<Avalonia.Point>
                {
                    new(-VoCaretHalfWidth, 0),
                    new(VoCaretHalfWidth, 0),
                    new(0, VoCaretHeight)
                },
                Stroke = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            _playheadCaret[!Polygon.FillProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppDangerBrush");
            ruler.Children.Add(_playheadCaret);
        }

        if (_rulerPlayheadLine == null)
        {
            _rulerPlayheadLine = new Line
            {
                StrokeThickness = 2,
                IsHitTestVisible = false
            };
            _rulerPlayheadLine[!Line.StrokeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppDangerBrush");
            ruler.Children.Add(_rulerPlayheadLine);
        }
    }

    /// <summary>VOTAKE_01 — the block that grows in real time while a take is being recorded.</summary>
    private void EnsureLiveRecordingRegion(Canvas lane, double height)
    {
        if (_currentSessionRegionRect != null) return;

        var dangerBase = Infrastructure.ThemeResources.Colour(this, "AppDangerColor", Color.FromRgb(168, 50, 50));
        _currentSessionRegionRect = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(150, dangerBase.R, dangerBase.G, dangerBase.B)),
            Height = height,
            IsHitTestVisible = false,
            IsVisible = false
        };
        lane.Children.Add(_currentSessionRegionRect);
    }

    private void UpdateCurrentRecordingRegion(double effectiveDuration, double width, double height, double currentFraction)
    {
        if (_currentSessionRegionRect == null) return;

        if (!_isRecording || _currentSession == null)
        {
            _currentSessionRegionRect.IsVisible = false;
            return;
        }

        double startFrac = (_currentSession.StartSec - _trimStartSec) / effectiveDuration;
        double x1 = Math.Clamp(startFrac * width, 0, width);
        double x2 = Math.Clamp(currentFraction * width, 0, width);
        if (x2 <= x1)
        {
            _currentSessionRegionRect.IsVisible = false;
            return;
        }

        _currentSessionRegionRect.IsVisible = true;
        _currentSessionRegionRect.Width = x2 - x1;
        _currentSessionRegionRect.Height = height;
        Canvas.SetLeft(_currentSessionRegionRect, x1);
        Canvas.SetTop(_currentSessionRegionRect, 0);
    }

    private void SeekTimelineFromPointer(Avalonia.Input.PointerEventArgs e, Avalonia.Controls.Control timelineCanvas, bool force)
    {
        if (e.Handled) return;

        if (_draggingSession != null)
        {
            if (force && e.RoutedEvent == Avalonia.Input.InputElement.PointerReleasedEvent)
            {
                _draggingSession = null;
                _isDraggingStartEdge = false;
                _isDraggingEndEdge = false;
                e.Handled = true;
                return;
            }

            if (_videoHost?.IpcClient == null) return;
            double vDur = _videoHost.IpcClient.Duration;
            double eDur = (_trimEndSec > 0 ? _trimEndSec : vDur) - _trimStartSec;
            double w = timelineCanvas.Bounds.Width;
            double pointX = e.GetCurrentPoint(timelineCanvas).Position.X;
            
            double dragTargetTime = _trimStartSec + (pointX / w) * eDur;

            if (_isDraggingStartEdge)
            {
                double maxStart = _draggingSession.RenderEndSec - 0.1;
                double newStart = Math.Clamp(dragTargetTime, _draggingSession.StartSec, maxStart);
                _draggingSession.TrimLeftSec = newStart - _draggingSession.StartSec;
            }
            else if (_isDraggingEndEdge)
            {
                double minEnd = _draggingSession.RenderStartSec + 0.1;
                double newEnd = Math.Clamp(dragTargetTime, minEnd, _draggingSession.EndSec);
                _draggingSession.TrimRightSec = _draggingSession.EndSec - newEnd;
            }

            _renderedSessionCount = -1;
            UpdatePlayheadUI();
            e.Handled = true;
            return;
        }

        if (_isRecording && !_recordPaused) return;
        if (_videoHost?.IpcClient == null) return;
        double videoDuration = _videoHost.IpcClient.Duration;
        double effectiveDuration = (_trimEndSec > 0 ? _trimEndSec : videoDuration) - _trimStartSec;
        if (effectiveDuration <= 0) effectiveDuration = videoDuration;
        double width = timelineCanvas.Bounds.Width;
        if (effectiveDuration <= 0 || width <= 0) return;

        double x = Math.Clamp(e.GetPosition(timelineCanvas).X, 0, width);
        double targetTime = _trimStartSec + (x / width) * effectiveDuration;
        
        if (!force)
        {
            _dragSeekTimeSec = targetTime;
            _lastTimelineSeekUtc = DateTime.UtcNow;
            SeekToAbsolute(targetTime);
            e.Handled = true;
            return;
        }

        _dragSeekTimeSec = targetTime;
        _lastTimelineSeekUtc = DateTime.UtcNow;
        SeekToAbsolute(targetTime);
        e.Handled = true;
        
        _ = Task.Delay(300).ContinueWith(_ => Dispatcher.UIThread.Post(() => 
        {
            if (Math.Abs((_dragSeekTimeSec ?? 0) - targetTime) < 0.001) 
            {
                _dragSeekTimeSec = null;
            }
        }));
    }

    private void OnKeyUpHandler(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Avalonia.Controls.TextBox or Avalonia.Controls.NumericUpDown)
            return;

        if (e.Key == Avalonia.Input.Key.V)
        {
            _isVKeyPressed = false;
        }

        var kb = FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.KeyBinds;
        var playPause = new Avalonia.Input.KeyGesture(kb.PlayPause);
        
        if (playPause.Matches(e) || e.Key is Avalonia.Input.Key.Space or Avalonia.Input.Key.Left or Avalonia.Input.Key.Right)
        {
            if (playPause.Matches(e) || e.Key == Avalonia.Input.Key.Space)
            {
                _isSpaceKeyPressed = false;
            }
            e.Handled = true;
        }
    }

    private void OnKeyDownHandler(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Avalonia.Controls.TextBox or Avalonia.Controls.NumericUpDown)
            return;

        if (e.Key == Avalonia.Input.Key.V)
        {
            if (!_isVKeyPressed)
            {
                _isVKeyPressed = true;
                ToggleRecord(null, null);
            }
            e.Handled = true;
            return;
        }

        var kb = FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.KeyBinds;
        var playPause = new Avalonia.Input.KeyGesture(kb.PlayPause);
        var seekFwd = new Avalonia.Input.KeyGesture(kb.SeekForward);
        var seekBack = new Avalonia.Input.KeyGesture(kb.SeekBackward);
        var fineSeekFwdCtrl = new Avalonia.Input.KeyGesture(kb.FineSeekForward, Avalonia.Input.KeyModifiers.Control);
        var fineSeekFwdShift = new Avalonia.Input.KeyGesture(kb.FineSeekForward, Avalonia.Input.KeyModifiers.Shift);
        var fineSeekBackCtrl = new Avalonia.Input.KeyGesture(kb.FineSeekBackward, Avalonia.Input.KeyModifiers.Control);
        var fineSeekBackShift = new Avalonia.Input.KeyGesture(kb.FineSeekBackward, Avalonia.Input.KeyModifiers.Shift);

        if (playPause.Matches(e))
        {
            if (!_isSpaceKeyPressed)
            {
                _isSpaceKeyPressed = true;
                if (_isRecording) ToggleRecordPause(null, null);
                else TogglePreviewPlayback(null, null);
            }
            e.Handled = true;
        }
        else if (fineSeekFwdCtrl.Matches(e) || fineSeekFwdShift.Matches(e))
        {
            if (_isRecording && !_recordPaused) return;
            _ = _videoHost?.IpcClient?.SendCommandAsync("frame-step");
            e.Handled = true;
        }
        else if (fineSeekBackCtrl.Matches(e) || fineSeekBackShift.Matches(e))
        {
            if (_isRecording && !_recordPaused) return;
            _ = _videoHost?.IpcClient?.SendCommandAsync("frame-back-step");
            e.Handled = true;
        }
        else if (seekFwd.Matches(e))
        {
            if (_isRecording && !_recordPaused) return;
            _ = _videoHost?.IpcClient?.SendCommandAsync("seek", "5", "relative");
            e.Handled = true;
        }
        else if (seekBack.Matches(e))
        {
            if (_isRecording && !_recordPaused) return;
            _ = _videoHost?.IpcClient?.SendCommandAsync("seek", "-5", "relative");
            e.Handled = true;
        }
    }

    private void ToggleRecord(object? sender, Avalonia.Interactivity.RoutedEventArgs? e)
    {
        RuntimeLog.Info("VoiceOver",
            $"Record button pressed. mpvReady={_isMpvReady}, recording={_isRecording}, arming={_recordArming}, paused={_recordPaused}.");
        if (!_isMpvReady) return;

        if (_isRecording)
        {
            StopRecordingAndPlayback();
        }
        else
        {
            StartRecordingAndPlayback();
        }
    }

    private async void TogglePreviewPlayback(object? sender, Avalonia.Interactivity.RoutedEventArgs? e)
    {
        try
        {
            if (!_isMpvReady || _isRecording || _videoHost?.IpcClient == null) return;

            bool shouldPlay = _videoHost.IpcClient.IsPaused;
            if (shouldPlay)
            {
                double current = _videoHost.IpcClient.CurrentTime;
                double safeStart = NormalizePreviewPlaybackPosition(current);
                if (Math.Abs(safeStart - current) > 0.01)
                {
                    await _videoHost.IpcClient.SetPropertyAsync("time-pos", safeStart.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                _isCurrentlyFrozen = false;
                _lastFreezeTriggerMs = -1;
                ApplyPreviewSpeedForPosition(safeStart * 1000.0);
                RuntimeLog.Info("VoiceOver", $"Preview playback requested at {safeStart:0.###}s.");
            }

            await _videoHost.IpcClient.SetPropertyAsync("pause", shouldPlay ? "no" : "yes");
            UpdatePlayPauseIconUI();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("VoiceOver", $"Error toggling preview playback: {ex.Message}");
        }
    }


    /// <summary>True between "user pressed record" and "the video clock actually moved".</summary>
    private bool _recordArming;

    /// <summary>
    /// VOASYNC_02 — true between "the clock moved, open the device" and "capture is live".
    /// Distinct from <see cref="_recordArming"/>: arming waits on the VIDEO, this waits on the
    /// AUDIO DRIVER. The pause button stays disabled across it because there is nothing to pause
    /// yet, and PumpRecordArming must not queue a second open while one is in flight.
    /// </summary>
    private bool _recordOpening;

    /// <summary>Video position observed on the previous arming tick, to detect real movement.</summary>
    private double _armPrevTime;

    /// <summary>Give up arming if the clock never moves (end of clip, stuck decoder).</summary>
    private DateTime _armDeadlineUtc;

    /// <summary>True while the user has paused an in-progress recording session.</summary>
    private bool _recordPaused;

    private Button? _pauseResumeButton;

    private void StartRecordingAndPlayback()
    {
        if (!VoiceRecorder.HasInputDevice)
        {
            ShowMicrophoneUnavailable("No microphone input device is available.");
            return;
        }

        double currentPreviewTime = _videoHost?.IpcClient?.CurrentTime ?? _trimStartSec;
        double recordingStart = NormalizePreviewPlaybackPosition(currentPreviewTime);
        RuntimeLog.Info("VoiceOver",
            $"Starting recording. previewTime={currentPreviewTime:0.###}s, normalisedStart={recordingStart:0.###}s, trim={_trimStartSec:0.###}s..{_trimEndSec:0.###}s, mpvPaused={_videoHost?.IpcClient?.IsPaused}, micIndex={GetSelectedMicrophoneDeviceIndex()}.");
        if (Math.Abs(recordingStart - currentPreviewTime) > 0.01)
        {
            _ = _videoHost?.IpcClient?.SetPropertyAsync("time-pos", recordingStart.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        _isCurrentlyFrozen = false;
        _lastFreezeTriggerMs = -1;
        ApplyPreviewSpeedForPosition(recordingStart * 1000.0);

        _recordPaused = false;
        _isRecording = true;

        if (!ArmTakeSegment(recordingStart)) return;

        UpdateRecordingUi("ARMING", "AppWarningBrush");
        UpdateApplyState("Recording starts the moment the video rolls. Apply will save the take and close.");
    }

    /// <summary>
    /// Opens a NEW take: fresh WAV file, fresh session, and arms the anchor. Called both when
    /// recording starts and on every resume, which is what keeps segments independent.
    /// Returns false if the take could not be prepared (caller has already been told why).
    /// </summary>
    private bool ArmTakeSegment(double provisionalStartSec)
    {
        ReleaseRecorder();

        // VOMON_01 — hand the device over. Some drivers refuse a second capture handle, so the
        // idle monitor MUST be closed before VoiceRecorder opens the same input.
        StopMicMonitor();

        _outputWavPath = CreateTempVoiceOverPath();
        _currentSession = new VoiceOverSession
        {
            WavPath = _outputWavPath,
            StartSec = provisionalStartSec
        };

        _uiSoundMute?.Dispose();
        _uiSoundMute = UiSoundEffect.Suppress();

        _recordArming = true;
        _armPrevTime = _videoHost?.IpcClient?.CurrentTime ?? provisionalStartSec;
        _armDeadlineUtc = DateTime.UtcNow.AddSeconds(3);

        _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "no");

        if (_recordingLight != null) _recordingLight.Opacity = 0.6;
        UpdateTransportState();
        return true;
    }

    /// <summary>
    /// Runs on the 50 ms UI timer while armed. Opens the microphone on the first tick where the
    /// video clock has genuinely moved, and stamps the take's anchor from that same reading.
    /// </summary>
    private void PumpRecordArming()
    {
        if (_recordOpening) return;   // VOASYNC_02 — a device open is already queued
        if (!_recordArming) return;

        var ipc = _videoHost?.IpcClient;
        if (ipc == null) return;

        if (_isCurrentlyFrozen || ipc.IsPaused)
        {
            _armPrevTime = ipc.CurrentTime;
            if (DateTime.UtcNow > _armDeadlineUtc)
            {
                RuntimeLog.Fail("VoiceOver", "Recording could not start: the preview never left the paused/frozen state.");
                _recordArming = false;
                AbortActiveTake();
                ShowMicrophoneUnavailable("The video would not start playing, so recording was cancelled.");
            }
            return;
        }

        double now = ipc.CurrentTime;

        // VOFIX_04 — a BACKWARDS jump must re-baseline, not stall the arm.
        // Arming waits for the clock to move FORWARD. The A-B repeat loop (VOFIX_01) made the clock
        // jump backwards, so `now <= _armPrevTime` stayed true until the 3-second deadline aborted
        // the take — which is why recordings came out empty, and why RecordPauseButton stayed
        // disabled (`IsEnabled = _isRecording && !_recordArming`) and looked broken. The loop is
        // gone, but a user seek during the arm window would reproduce it exactly, so re-baseline on
        // any backwards movement and let the deadline keep its meaning.
        if (now < _armPrevTime - 1e-4)
        {
            RuntimeLog.Info("VoiceOver",
                $"Preview jumped backwards while arming ({_armPrevTime:F2}s -> {now:F2}s). Re-baselining the arm.");
            _armPrevTime = now;
            _armDeadlineUtc = DateTime.UtcNow.AddSeconds(3);
            return;
        }

        if (now <= _armPrevTime + 1e-4)
        {
            if (DateTime.UtcNow > _armDeadlineUtc)
            {
                RuntimeLog.Fail("VoiceOver", "Recording could not start: the video clock never advanced.");
                _recordArming = false;
                AbortActiveTake();
                ShowMicrophoneUnavailable("The video would not start playing, so recording was cancelled.");
            }
            _armPrevTime = now;
            return;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // VOASYNC_02 — OPENING THE DEVICE IS QUEUED, NOT INLINE.
        //
        // `new VoiceRecorder(...).StartRecording()` performs waveInOpen and creates the WAV file.
        // Done here it ran inside a 50 ms timer tick on the interface thread, so pressing record
        // stuttered for as long as the driver took to hand over the endpoint.
        //
        // The ANCHOR is the subtle part. It used to be stamped from `now` — the clock reading that
        // triggered the arm — which was already slightly stale by the time the device finished
        // opening, so the voice sat a little early against the picture. It is now re-read at the
        // instant capture actually goes live, which is strictly more accurate.
        // ══════════════════════════════════════════════════════════════════════════
        _recordArming = false;
        _recordOpening = true;

        string takePath = _outputWavPath;
        int micIndex = GetSelectedMicrophoneDeviceIndex();
        RuntimeLog.Info("VoiceOver", $"Opening microphone index {micIndex} (video clock at {now:0.###}s).");

        QueueAudioDeviceWork(() =>
        {
            var recorder = new VoiceRecorder(takePath, micIndex);
            Exception? failure = null;
            try { recorder.StartRecording(); }
            catch (Exception ex)
            {
                failure = ex;
                try { recorder.Dispose(); } catch (Exception dex) { RuntimeLog.Swallowed(dex); }
            }

            Dispatcher.UIThread.Post(() =>
            {
                _recordOpening = false;

                // The user may have pressed stop, or closed the window, while the driver was
                // opening. The recorder is then an orphan and must not be mounted.
                if (_isClosing || !_isRecording || _currentSession == null)
                {
                    if (failure == null)
                    {
                        // The stop arrived while the driver was still opening. Close the device
                        // and delete the WAV it just created — the delete is queued BEHIND the
                        // dispose on the same chain, because the file is still open until then.
                        QueueAudioDeviceWork(() =>
                        {
                            try { recorder.StopRecording(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                            try { recorder.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                            TryDeleteFile(takePath);
                        });
                        RuntimeLog.Info("VoiceOver", "Recording was stopped while the microphone was still opening; the empty take was discarded.");
                    }
                    return;
                }

                if (failure != null)
                {
                    RuntimeLog.Fail("VoiceOver", $"Microphone recording could not start. {failure.Message}");
                    AbortActiveTake();
                    ShowMicrophoneUnavailable("Microphone recording could not start on this PC.");
                    return;
                }

                recorder.VolumeChanged += OnVolumeChanged;
                _recorder = recorder;

                double liveAt = _videoHost?.IpcClient?.CurrentTime ?? now;
                _currentSession.StartSec = liveAt;

                RuntimeLog.Info("VoiceOver",
                    $"Microphone open on index {micIndex}; take anchored at {liveAt:0.###}s (source time).");

                UpdateRecordingUi("RECORDING", "AppDangerBrush");
                UpdateTransportState();
                UpdateApplyState("Recording in progress. Apply will save the current take and close.");
            });
        });
    }

    /// <summary>Pause: close the current take cleanly and stop the video. Resume opens a new one.</summary>
    private void PauseRecordingSegment()
    {
        if (!_isRecording || _recordPaused) return;

        _recordArming = false;
        FinalizeCurrentTake();
        _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");

        _recordPaused = true;
        UpdateRecordingUi("PAUSED", "AppWarningBrush");
        UpdateTransportState();
        UpdateApplyState("Recording paused. Resume to add another part, or Apply to save what you have.");
    }

    /// <summary>Resume: a brand-new take, re-armed and re-anchored, so drift cannot accumulate.</summary>
    private void ResumeRecordingSegment()
    {
        if (!_isRecording || !_recordPaused) return;
        if (!VoiceRecorder.HasInputDevice)
        {
            ShowMicrophoneUnavailable("No microphone input device is available.");
            return;
        }

        _recordPaused = false;
        double resumeAt = _videoHost?.IpcClient?.CurrentTime ?? _trimStartSec;
        if (!ArmTakeSegment(resumeAt)) return;

        UpdateRecordingUi("ARMING", "AppWarningBrush");
        UpdateApplyState("Recording resumes the moment the video rolls again.");
    }

    private void ToggleRecordPause(object? sender, Avalonia.Interactivity.RoutedEventArgs? e)
    {
        if (!_isRecording) return;
        if (_recordPaused) ResumeRecordingSegment();
        else PauseRecordingSegment();
    }

    /// <summary>
    /// VOASYNC_02 — CLOSES THE TAKE WITHOUT BLOCKING THE INTERFACE THREAD.
    ///
    /// ⚠️ THIS IS THE FREEZE. <see cref="VoiceRecorder.StopRecording"/> waits on
    /// <c>RecordingStopped</c> for up to two seconds so the last captured buffers are written
    /// before the WAV is closed. That wait is CORRECT — dropping it truncates the end of every
    /// take — but it was being performed ON THE INTERFACE THREAD, inside the record button's own
    /// click handler. A typical drain is one buffer period, so every press of stop froze the whole
    /// window for roughly 50-150 ms, which is exactly the stutter that was reported.
    ///
    /// The drain now happens on the shared audio-device chain (see QueueAudioDeviceWork), which
    /// also guarantees it finishes before the idle monitor reclaims the device. The interface
    /// thread returns immediately; the take is added when the worker reports back. Because the
    /// byte count is read AFTER the drain, the take's length is now MORE accurate than it was
    /// when this ran inline, not less.
    /// </summary>
    private void FinalizeCurrentTake()
    {
        // Ownership of both objects transfers out of the fields here, on the interface thread, so
        // a second stop (or the window closing) cannot race the worker for the same recorder.
        var recorder = _recorder;
        var session = _currentSession;
        _recorder = null;
        _currentSession = null;

        if (session == null)
        {
            RuntimeLog.Info("VoiceOver", "Finalise was called with no open take — nothing to keep or discard.");
            if (recorder != null) RetireRecorderAsync(recorder);
            return;
        }

        if (recorder == null)
        {
            // The microphone never opened (the take was still arming), so there is nothing to
            // drain and nothing was captured. Settle it inline — this path does no blocking work.
            CompleteTake(session, micWasOpen: false, capturedBytes: -1, capturedBuffers: -1, capturedPeak: -1f);
            return;
        }

        var settled = new TaskCompletionSource();
        _pendingFinalizes.Add(settled);

        recorder.VolumeChanged -= OnVolumeChanged;

        QueueAudioDeviceWork(() =>
        {
            try { recorder.StopRecording(); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }

            long bytes = recorder.BytesCaptured;
            int buffers = recorder.BuffersSeen;
            float peak = recorder.PeakSeen;

            try { recorder.Dispose(); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }

            Dispatcher.UIThread.Post(() =>
            {
                _pendingFinalizes.Remove(settled);
                try { CompleteTake(session, true, bytes, buffers, peak); }
                finally { settled.TrySetResult(); }
            });
        });
    }

    /// <summary>
    /// VOASYNC_02 — the interface-thread half of finalising: decide whether the take is worth
    /// keeping, and say so. Runs once per take, after the capture device has fully drained.
    /// </summary>
    private void CompleteTake(VoiceOverSession session, bool micWasOpen, long capturedBytes, int capturedBuffers, float capturedPeak)
    {
        if (_isClosing) return;

        // VOASYNC_01 — LENGTH COMES FROM THE BYTE COUNT, NOT FROM RE-OPENING THE FILE.
        // The recorder counted every byte it wrote at a known 44100 Hz / 16-bit / mono, so the
        // length is arithmetic. The file is only consulted as a fallback for a take restored from
        // disk, where no byte count exists.
        double dur = 0;
        if (capturedBytes > 0)
        {
            dur = capturedBytes / (44100.0 * 2.0);
        }
        else
        {
            try
            {
                if (System.IO.File.Exists(session.WavPath))
                {
                    using var af = new NAudio.Wave.AudioFileReader(session.WavPath);
                    dur = af.TotalTime.TotalSeconds;
                }
            }
            catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }

        session.EndSec = session.StartSec + dur;

        if (micWasOpen &&
            session.EndSec > session.StartSec + 0.05 &&
            System.IO.File.Exists(session.WavPath))
        {
            _sessions.Add(session);
            _renderedSessionCount = -1;
            EnsureTakePeaksAsync(session.WavPath);   // VOTAKE_01 — off-thread envelope
            RuntimeLog.Info("VoiceOver",
                $"Take saved: {session.StartSec:0.###}s -> {session.EndSec:0.###}s (source time). buffers={capturedBuffers}, capturedBytes={capturedBytes}, peak={capturedPeak:0.####}.");
            Controls.FloatingNotice.Success(this, $"Take saved — {session.EndSec - session.StartSec:0.0}s");
            _lastTakeWasRejected = false;
        }
        else
        {
            long fileBytes = -1;
            try { if (System.IO.File.Exists(session.WavPath)) fileBytes = new System.IO.FileInfo(session.WavPath).Length; }
            catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

            RuntimeLog.Fail("VoiceOver",
                $"Take DISCARDED. micWasOpen={micWasOpen}, buffers={capturedBuffers}, capturedBytes={capturedBytes}, peak={capturedPeak:0.####}, wavBytes={fileBytes}, wavSeconds={dur:0.###}, window={session.StartSec:0.###}s..{session.EndSec:0.###}s.");

            TryDeleteFile(session.WavPath);
            _lastTakeWasRejected = true;
            Controls.FloatingNotice.Error(this, "That take was too short to keep");
        }

        UpdatePlayheadUI();
        UpdateTransportState();
        UpdateApplyState(_lastTakeWasRejected && !HasSavedVoiceOverSession()
            ? "Recording was too short to apply. Record another take or choose a mute option."
            : null);
    }

    /// <summary>Throws away the take being armed (never captured anything).</summary>
    private void AbortActiveTake()
    {
        RuntimeLog.Fail("VoiceOver",
            $"Take aborted before the microphone ever opened (arming failed). wav='{(_currentSession?.WavPath is string w ? System.IO.Path.GetFileName(w) : "none")}'.");
        ReleaseRecorder();
        if (_currentSession != null)
        {
            TryDeleteFile(_currentSession.WavPath);
            _currentSession = null;
        }
        _isRecording = false;
        _recordPaused = false;
        _recordOpening = false;
        _uiSoundMute?.Dispose();
        _uiSoundMute = null;
    }

    /// <summary>
    /// VOASYNC_02 — detaches the recorder and retires it on the audio chain. Never blocks.
    /// </summary>
    private void ReleaseRecorder()
    {
        var recorder = _recorder;
        _recorder = null;
        if (recorder == null) return;
        recorder.VolumeChanged -= OnVolumeChanged;
        RetireRecorderAsync(recorder);
    }

    /// <summary>VOASYNC_02 — drains and disposes a recorder off the interface thread, in order.</summary>
    private void RetireRecorderAsync(FortniteVideoSoftware.Core.Media.VoiceRecorder recorder)
    {
        QueueAudioDeviceWork(() =>
        {
            try { recorder.StopRecording(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            try { recorder.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        });
    }

    /// <summary>Set by FinalizeCurrentTake when a take was discarded, so the UI can explain why.</summary>
    private bool _lastTakeWasRejected;

    private void UpdateRecordingUi(string status, string brushKey)
    {
        bool live = status == "RECORDING";
        if (_recordingLight != null)
        {
            // VOROW_01 — this lamp is now labelled REC and means one thing only.
            //   1.00  capture is live      0.60  armed, waiting for the video clock
            //   0.18  idle (dark)
            // It used to sit at 0.6 whenever it was not live, which read as "on" and made the
            // studio look like it was recording when it was not.
            _recordingLight.Opacity = live ? 1.0 : (_isRecording ? 0.6 : 0.18);
            _recordingLight.Classes.Remove("recording");
            if (live) _recordingLight.Classes.Add("recording");
        }
        if (_micRecordButton != null)
        {
            _micRecordButton.Classes.Remove("recording");
            if (live) _micRecordButton.Classes.Add("recording");
        }
        if (_recordingStatusText != null)
        {
            _recordingStatusText.Text = status;
            _recordingStatusText.Foreground = GetAppBrush(brushKey, Brushes.White);
        }
        if (_pauseResumeButton != null)
        {
            _pauseResumeButton.IsVisible = _isRecording;
            var rpi = this.FindControl<Avalonia.Controls.Shapes.Path>("RecordPauseIcon");
            var rri = this.FindControl<Avalonia.Controls.Shapes.Path>("RecordResumeIcon");
            if (rpi != null) rpi.IsVisible = !_recordPaused;
            if (rri != null) rri.IsVisible = _recordPaused;
            ToolTip.SetTip(_pauseResumeButton, _recordPaused
                ? "Carry on recording from here. The new part is anchored to the video on its own, so it stays in sync."
                : "Stop recording and pause the video. You can resume and keep going.");
        }
    }

    private void ShowMicrophoneUnavailable(string message)
    {
        _isRecording = false;
        if (_recordingLight != null)
        {
            _recordingLight.Classes.Remove("recording");
            _recordingLight.Opacity = 0.18;
        }
        if (_recordingStatusText != null)
        {
            _recordingStatusText.Text = "NO MIC";
            _recordingStatusText.Foreground = GetAppBrush("AppWarningBrush", Brushes.Yellow);
        }
        if (_micRecordButton != null) _micRecordButton.Classes.Remove("recording");
        UpdateTransportState();
        StartMicMonitor();   // VOMON_01
        UpdateApplyState(message);
    }

    /// <summary>
    /// ISSUE_05 — deletes a temp take, tolerating a briefly-still-held file handle.
    ///
    /// The recorder now closes its WAV writer in an ordered shutdown, but Windows can hold a
    /// handle open for a few more milliseconds after the last Dispose. A single attempt therefore
    /// used to lose the race now and then and leave abandoned takes accumulating in the temp
    /// folder forever, because every failure was swallowed by a bare `catch`.
    /// </summary>
    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        Task.Run(() =>
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    if (!System.IO.File.Exists(path)) return;
                    System.IO.File.Delete(path);
                    return;
                }
                catch (System.IO.IOException) when (attempt < 3)
                {
                    System.Threading.Thread.Sleep(60);
                }
                catch (UnauthorizedAccessException) when (attempt < 3)
                {
                    System.Threading.Thread.Sleep(60);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Debug("VoiceOver", $"Could not delete temp take '{System.IO.Path.GetFileName(path)}': {ex.Message}");
                    return;
                }
            }

            RuntimeLog.Debug("VoiceOver", $"Temp take '{System.IO.Path.GetFileName(path)}' is still locked; leaving it for temp cleanup.");
        });
    }

    private void StopRecordingAndPlayback()
    {
        if (!_isRecording) return;

        _isRecording = false;
        _recordArming = false;
        _recordPaused = false;
        _isCurrentlyFrozen = false;
        _recordOpening = false;

        FinalizeCurrentTake();

        _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");

        _uiSoundMute?.Dispose();
        _uiSoundMute = null;

        if (_recordingLight != null)
        {
            _recordingLight.Classes.Remove("recording");
            _recordingLight.Opacity = 0.18;
        }
        if (_recordingStatusText != null)
        {
            _recordingStatusText.Text = "READY";
            _recordingStatusText.Foreground = GetAppBrush("AppTextPrimaryBrush", Brushes.White);
        }
        if (_pauseResumeButton != null) _pauseResumeButton.IsVisible = false;

        if (_micRecordButton != null) _micRecordButton.Classes.Remove("recording");
        UpdateTransportState();
        StartMicMonitor();   // VOMON_01 — take the device back for the idle meter

        // VOASYNC_02 — the verdict on this take is not known yet (the device is still draining on
        // the chain). CompleteTake sets the hint when it lands; until then the interface simply
        // says nothing has changed, rather than flashing a stale "too short" from a previous take.
        UpdateApplyState();
    }

    private void OnVolumeChanged(object? sender, float volume)
    {
        // Raised on NAudio's capture thread. A float write is atomic and the meter samples it on
        // the next 50 ms tick, so this deliberately does NOT marshal to the interface thread —
        // posting once per 50 ms audio buffer was queueing ~20 dispatcher items a second for a
        // value that is overwritten before anyone looks at it.
        _peakVolume = Math.Max(_peakVolume, volume);
    }

    private Canvas? _eqMeterCanvas;
    private Avalonia.Controls.Shapes.Path? _eqPath;
    private Random _eqRandom = new Random();

    private void UpdateSmoothEqMeter()
    {
        if (_eqMeterCanvas == null) return;

        double currentPeak = _peakVolume;
        _peakVolume = 0;

        if (currentPeak > _smoothedVolume)
        {
            _smoothedVolume = currentPeak;
        }
        else
        {
            _smoothedVolume = Math.Max(0, _smoothedVolume - 0.05);
        }

        if (_eqPath == null)
        {
            _eqPath = new Avalonia.Controls.Shapes.Path
            {
                Stroke = Avalonia.Application.Current?.FindResource("AppSuccessBrush") as Avalonia.Media.IBrush,
                StrokeThickness = 4,
                IsHitTestVisible = false
            };
            _eqMeterCanvas.Children.Add(_eqPath);
        }

        if (_smoothedVolume > 0.9)
            _eqPath.Stroke = Avalonia.Application.Current?.FindResource("AppWarningBrush") as Avalonia.Media.IBrush;
        else
            _eqPath.Stroke = Avalonia.Application.Current?.FindResource("AppSuccessBrush") as Avalonia.Media.IBrush;

        double width = _eqMeterTrack != null && _eqMeterTrack.Bounds.Width > 0 ? _eqMeterTrack.Bounds.Width : 250;
        // VOROW_01 — was hard-coded to 30 while the track is now 34 and stretches with the window.
        // Reading the real height keeps the bars centred instead of riding above centre.
        double height = _eqMeterCanvas.Bounds.Height > 4 ? _eqMeterCanvas.Bounds.Height : 32;
        int numBars = (int)(width / 8);
        
        var geometry = new Avalonia.Media.StreamGeometry();
        using (var context = geometry.Open())
        {
            double x = 4;
            for (int i = 0; i < numBars; i++)
            {
                double targetAmp = _smoothedVolume * 2.0; 
                double randomized = targetAmp * (0.3 + _eqRandom.NextDouble() * 0.7);
                double barHeight = Math.Min(height - 4, randomized * height);
                if (barHeight < 2) barHeight = 2;

                double centerY = height / 2;
                context.BeginFigure(new Avalonia.Point(x, centerY + barHeight / 2), false);
                context.LineTo(new Avalonia.Point(x, centerY - barHeight / 2));
                x += 8;
            }
        }
        _eqPath.Data = geometry;
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // VOROW_01 — THE LIVE WAVEFORM MONITOR WAS REMOVED, NOT MOVED.
    //
    // It was a 60px scrolling scope under the EQ meter that drew the last N input peaks while a
    // take ran. It cost more vertical space than the entire transport row and answered the same
    // question the EQ meter already answers ("is the microphone hearing me"), one row above it.
    // The information a scrolling scope uniquely carried — the SHAPE of what was said, over time —
    // is now drawn where it actually belongs: inside the take's own red block on the film lane,
    // where it lines up with the picture it was recorded against (see EnsureTakePeaksAsync).
    // `_waveformSamples` went with it; nothing else read that list.
    // ══════════════════════════════════════════════════════════════════════════════

    private async void ApplyAndClose()
    {
        if (_isRecording)
        {
            StopRecordingAndPlayback();
        }

        // VOASYNC_02 — the last take may still be draining on the audio chain. Without this wait
        // the take the user recorded a moment ago would not be in `_sessions` yet, and Apply would
        // report "nothing to apply" and throw it away. This is the one place that genuinely has to
        // wait — and it awaits, so the interface stays responsive while it does.
        if (_pendingFinalizes.Count > 0)
        {
            if (_applyButton != null) { _applyButton.IsEnabled = false; _applyButton.Content = "SAVING..."; }
            await WhenTakesSettled();
            if (_applyButton != null) _applyButton.Content = "APPLY & CLOSE";
            if (_isClosing) return;
        }

        bool duckAudio = _duckAudioCb?.IsChecked == true;
        bool protectFromMusic = _duckMusicCb?.IsChecked == true;
        RememberVoiceProtectionChoices(duckAudio, protectFromMusic);

        if (!HasSavedVoiceOverSession())
        {
            Result = null;
            UpdateApplyState("Nothing to apply yet. Record a take before applying.");
            return;
        }
        
        var persistedTakes = new List<VoiceOverTake>();
        var keepPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_sessions.Count > 0)
        {
            if (_applyButton != null) { _applyButton.IsEnabled = false; _applyButton.Content = "SAVING..."; }

            for (int i = 0; i < _sessions.Count; i++)
            {
                var session = _sessions[i];
                if (session.IsMuted ||
                    session.EndSec <= session.StartSec ||
                    string.IsNullOrWhiteSpace(session.WavPath) ||
                    !System.IO.File.Exists(session.WavPath))
                {
                    continue;
                }

                try
                {
                    string persistedPath = CreatePersistedVoiceOverPath();
                    
                    if (session.TrimLeftSec > 0 || session.TrimRightSec > 0)
                    {
                        double realTrimLeft = _timeline != null ? Math.Max(0, _timeline.SourceToOutput(session.StartSec + session.TrimLeftSec) - _timeline.SourceToOutput(session.StartSec)) : session.TrimLeftSec;
                        double realTrimRight = _timeline != null ? Math.Max(0, _timeline.SourceToOutput(session.EndSec) - _timeline.SourceToOutput(session.EndSec - session.TrimRightSec)) : session.TrimRightSec;
                        double totalRealDur = _timeline != null ? Math.Max(0, _timeline.SourceToOutput(session.EndSec) - _timeline.SourceToOutput(session.StartSec)) : (session.EndSec - session.StartSec);
                        double realDur = Math.Max(0.1, totalRealDur - realTrimLeft - realTrimRight);

                        var startInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = ResolveBinaryPath("ffmpeg.exe", "backend"),
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        startInfo.ArgumentList.Add("-y");
                        startInfo.ArgumentList.Add("-i");
                        startInfo.ArgumentList.Add(session.WavPath);
                        startInfo.ArgumentList.Add("-ss");
                        startInfo.ArgumentList.Add(realTrimLeft.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                        startInfo.ArgumentList.Add("-t");
                        startInfo.ArgumentList.Add(realDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                        startInfo.ArgumentList.Add("-c");
                        startInfo.ArgumentList.Add("copy");
                        startInfo.ArgumentList.Add(persistedPath);
                        using var proc = System.Diagnostics.Process.Start(startInfo);
                        if (proc == null) throw new Exception($"FFmpeg trim process failed to start for take {i + 1}");
                        await proc.WaitForExitAsync();
                        if (proc.ExitCode != 0 || !System.IO.File.Exists(persistedPath) || new System.IO.FileInfo(persistedPath).Length == 0)
                        {
                            throw new Exception($"FFmpeg trim failed with code {proc.ExitCode} for take {i + 1}");
                        }
                    }
                    else
                    {
                        await Task.Run(() => File.Copy(session.WavPath, persistedPath, overwrite: true));
                    }
                    persistedTakes.Add(new VoiceOverTake(persistedPath, session.RenderStartSec));
                    keepPaths.Add(persistedPath);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("VoiceOver", $"Voiceover take {i + 1} could not be persisted. {ex.Message}");
                    Result = null;
                    if (_applyButton != null) _applyButton.Content = "APPLY & CLOSE";
                    UpdateApplyState($"Voiceover take {i + 1} could not be saved. Record another take.");
                    return;
                }
            }

            if (_applyButton != null) _applyButton.Content = "APPLY & CLOSE";
            DeleteSessionFilesExcept(keepPaths);
        }

        if (persistedTakes.Count == 0)
        {
            Result = null;
            UpdateApplyState("Voiceover audio could not be prepared. Record another take.");
            return;
        }

        string? finalWav = persistedTakes.Count > 0 ? persistedTakes[0].Path : null;
        double finalStart = persistedTakes.Count > 0 ? persistedTakes[0].StartSec : 0;

        Result = new VoiceOverResult
        {
            VoiceOverWavPath = finalWav,
            VoiceOverStartTimestampSec = finalStart,
            VoiceOverTakes = persistedTakes,
            DuckAudio = duckAudio,
            ProtectFromMusic = protectFromMusic
        };

        _isSafeToClose = true;
        Close();
    }

    private void DeleteSessionFilesExcept(IReadOnlySet<string> keepPaths)
    {
        foreach (var session in _sessions)
        {
            if (!keepPaths.Contains(session.WavPath))
            {
                TryDeleteFile(session.WavPath);
            }
        }
        if (!keepPaths.Contains(_outputWavPath))
        {
            TryDeleteFile(_outputWavPath);
        }
    }

    private void DeleteUnappliedVoiceOverFiles()
    {
        var appliedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Result?.VoiceOverWavPath))
        {
            appliedPaths.Add(Result!.VoiceOverWavPath!);
        }
        if (Result?.VoiceOverTakes != null)
        {
            foreach (var take in Result.VoiceOverTakes)
            {
                if (!string.IsNullOrWhiteSpace(take.Path))
                {
                    appliedPaths.Add(take.Path);
                }
            }
        }
        
        if (InitialState?.VoiceOverTakes != null)
        {
            foreach (var take in InitialState.VoiceOverTakes)
            {
                if (!string.IsNullOrWhiteSpace(take.Path))
                {
                    appliedPaths.Add(take.Path);
                }
            }
        }

        foreach (var session in _sessions)
        {
            if (!appliedPaths.Contains(session.WavPath))
            {
                TryDeleteFile(session.WavPath);
            }
        }

        if (_currentSession != null)
        {
            if (!appliedPaths.Contains(_currentSession.WavPath))
            {
                TryDeleteFile(_currentSession.WavPath);
            }
        }

        if (!appliedPaths.Contains(_outputWavPath))
        {
            TryDeleteFile(_outputWavPath);
        }
    }

    /// <summary>
    /// LEAK_02 — cancel a superseded generation, then dispose it OFF the interface thread.
    ///
    /// ⚠️ NEVER call <c>CancellationTokenSource.Dispose()</c> directly from an event handler here.
    /// Dispose BLOCKS until every callback raised by Cancel() has finished, and those callbacks
    /// marshal back to the interface thread — so the interface thread ends up waiting for itself.
    /// That is the exact deadlock that froze the Granular Speed Editor earlier (see CANCEL_01);
    /// it is a real, reproduced bug in this codebase, not a theoretical one.
    ///
    /// Handing the disposal to a worker thread keeps the tidy-up without the wait: nothing on that
    /// thread is holding the interface hostage, so Cancel's callbacks are free to complete.
    /// </summary>
    private static void RetireGenerationCts(System.Threading.CancellationTokenSource? cts)
    {
        if (cts == null) return;
        try { cts.Cancel(); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try { cts.Dispose(); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        });
    }

    protected override void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
        {
            _videoHost?.Dispose();
            _videoHost = null;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Controls.CoachOverlay.Cancel(this);
        Controls.FloatingNotice.Clear(this);
        _isClosing = true;
        _generationCts?.Cancel();
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        // VOASYNC_02 — closing the window must not block on the capture drain either. Both go on
        // the chain, in order, and the chain outlives the window just long enough to finish.
        ReleaseRecorder();

        // VOMON_01 — the idle monitor holds a live capture handle; it must not outlive the window.
        var monitorToRetire = _micMonitor;
        _micMonitor = null;
        if (monitorToRetire != null)
        {
            monitorToRetire.LevelChanged -= OnMonitorLevel;
            QueueAudioDeviceWork(monitorToRetire.Dispose);
        }

        _uiSoundMute?.Dispose();
        _uiSoundMute = null;

        DeleteUnappliedVoiceOverFiles();
        StopPreviewPlayers();
        
        if (_tempThumbPath != null && System.IO.File.Exists(_tempThumbPath))
        {
            try { System.IO.File.Delete(_tempThumbPath); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }
        if (_tempWavePath != null && System.IO.File.Exists(_tempWavePath))
        {
            try { System.IO.File.Delete(_tempWavePath); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }

        base.OnClosed(e);
    }

    private PreviewDetachController? _previewDetach;

    private void WirePreviewDetach()
    {
        var btn = this.FindControl<Button>("VoiceOverDetachPreviewBtn");
        if (btn == null) return;

        _previewDetach = new PreviewDetachController(
            this,
            PreviewDetachController.VoiceOverKey,
            "Preview Monitor — Voice Over",
            () => _videoHost);

        _previewDetach.StateChanged += detached =>
        {
            var watermark = this.FindControl<Avalonia.Controls.Border>("VoiceOverPreviewDetachedWatermark");
            if (watermark != null) watermark.IsVisible = detached;
            _previewDetach!.SyncButton(btn);
        };

        _previewDetach.DetachUnavailable += why => RuntimeLog.Info("UI", why);

        btn.Click += (_, _) => _previewDetach.Toggle();
        _previewDetach.SyncButton(btn);
    }
}
