using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.App.Controls;

public partial class PhaseOverlayControl : UserControl
{
    private DispatcherTimer? _timer;
    private List<string> _logLines = new();
    private List<int> _cpuHist = new();
    private List<int> _gpuHist = new();
    private List<int> _memHist = new();
    private ulong _lastIdle;
    private ulong _lastSys;
    private int _lastGpu;
    private Random _rand = new();

    private Stopwatch _processStopwatch = new();
    private bool _easterEggActive = false;
    private int _lastPercent = -1;
    private string _currentSequence = "";
    private Rect _anchorProgressBar;
    private Rect _anchorLogBox;
    private Rect _anchorTelemetryBox;
    private bool? _originalCanResize;

    /// <summary>
    /// How the last run ended ("SUCCESS" / "CANCEL" / "ERROR", or "" if it never reached a
    /// terminal state). StopOverlay uses this to decide what the taskbar button should do on the
    /// way out - a finished export gets a full green bar and five blinks, a failure gets a red
    /// one. Set BEFORE the _easterEggActive guard in HandleTerminalState on purpose: the taskbar
    /// is real feedback and must not depend on whether a cosmetic easter egg happens to be live.
    /// </summary>
    private string _lastTerminalState = "";

    /// <summary>
    /// Bumped on every StartOverlay and StopOverlay. The delayed "hold the finished bar, then
    /// clear it" task captures the value it was started with and does nothing if it no longer
    /// matches - otherwise starting a second export within the hold window would have the
    /// previous run's timer wipe the new run's progress bar a second later.
    /// </summary>
    private int _taskbarGeneration;

    public PhaseOverlayControl()
    {
        InitializeComponent();
        
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;

        DetachedFromVisualTree += (s, e) => StopOverlay();
    }

    public event EventHandler? CancelRequested;

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        var confirmCancelBtn = this.FindControl<Button>("ConfirmCancelProcessButton");
        if (confirmCancelBtn != null)
        {
            confirmCancelBtn.Click += (s, e) =>
            {
                (this.FindControl<Button>("CancelProcessButton"))?.Flyout?.Hide();
                HandleTerminalState("CANCEL");
                CancelRequested?.Invoke(this, EventArgs.Empty);
            };
        }
    }
    
    private Process? _smiProcess;

    /// <summary>
    /// IDEA_3 — the host window's Win32 handle, or zero when there is none. Resolved fresh each
    /// time because the overlay is reused across windows (Main App and Merger both host one) and
    /// the handle does not exist until the window is shown.
    /// </summary>
    private IntPtr HostWindowHandle
    {
        get
        {
            try { return GetParentWindow()?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero; }
            catch { return IntPtr.Zero; }
        }
    }

    public void StartOverlay()
    {
        IsVisible = true;

        TaskbarProgress.SetState(HostWindowHandle, TaskbarProgress.State.Indeterminate);

        AmbientBubblesBackground.GloballySuspended = true;
        _cpuHist.Clear();
        _gpuHist.Clear();
        _memHist.Clear();
        _logLines.Clear();
        
        try
        {
            if (_smiProcess != null)
            {
                var previous = _smiProcess;
                _smiProcess = null;
                try { if (!previous.HasExited) previous.Kill(entireProcessTree: true); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                try { previous.Dispose(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            }

            _smiProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=utilization.gpu,utilization.encoder --format=csv,noheader,nounits -i 0 -l 1",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            _smiProcess.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    var parts = e.Data.Trim().Split(',');
                    if (parts.Length >= 2)
                    {
                        int core = int.TryParse(parts[0].Trim(), out int c) ? c : 0;
                        int enc = int.TryParse(parts[1].Trim(), out int ex) ? ex : 0;
                        _lastGpu = Math.Max(0, Math.Min(100, Math.Max(core, enc)));
                    }
                }
            };
            _smiProcess.Start();

            try { FortniteVideoSoftware.Core.Infrastructure.ChildProcessTracker.AddProcess(_smiProcess); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

            _smiProcess.BeginOutputReadLine();
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        
        var txt = this.FindControl<TextBox>("LiveLogTextBox");
        if (txt != null) txt.Text = "Backend log stream attached.\n";
        
        RuntimeLog.LogAppended -= AppendLog;
        RuntimeLog.LogAppended += AppendLog;
        _timer?.Start();

        _barTarget = 0;
        _barValue = 0;
        var pbar0 = this.FindControl<ProgressBar>("PhaseProgressBar");
        if (pbar0 != null) pbar0.Value = 0;
        var ptxt0 = this.FindControl<TextBlock>("PhaseProgressText");
        if (ptxt0 != null) ptxt0.Text = "0%";
        EnsureBarTimer();

        _easterEggActive = true;
        _lastTerminalState = "";
        _taskbarGeneration++;
        TaskbarProgress.StopFlash(HostWindowHandle);
        _lastPercent = -1;
        _animTime = 0.0;
        _nextMoveTime = 1.25;
        _nextTauntTime = 2.0;
        _attackerIsA = _rand.Next(2) == 0;
        _projActive = false;
        _moveKind = "";
        _tauntIndex = _rand.Next(s_taunts.Length);
        _lastFightProgress = 0;
        _posInit = false;
        _nextWanderTime = 0;
        _comboLeft = 0;
        _skitKind = ""; _nextSkitTime = 4.0 + _rand.NextDouble() * 4.0;
        _koA = _koB = false;
        _squashA = _squashB = 0;
        _rotA = _rotB = 0;
        _stanceA = _stanceB = "";
        _faceA = 1; _faceB = -1;
        _camShakeUntil = 0; _camShakeMag = 0;
        _ultFlashUntil = 0;
        _processStopwatch.Restart();
        LockWindowAndAnchors();
        ApplyFighterGlow();
        ResetFightVisuals();
        AttachFightKeyHandler();
        ProcessEasterEggState(0);
    }

    private void ResetFightVisuals()
    {
        _isBossFight = _rand.NextDouble() < 0.05;
        _bossPhase = 1;
        _bossPhase2Announced = false;
        _hypeLevel = 0;

        _loserIsA = _isBossFight ? false : _rand.Next(2) == 0;

        var bar = this.FindControl<Avalonia.Controls.ProgressBar>("HypeMeterBar");
        if (bar != null) bar.Value = 0;
        var bossLbl = this.FindControl<TextBlock>("BossLabel");
        if (bossLbl != null) bossLbl.IsVisible = _isBossFight;
        var hypeLbl = this.FindControl<TextBlock>("HypeMeterLabel");
        if (hypeLbl != null) hypeLbl.Text = "MASH SPACEBAR FOR ULTIMATE!";

        var lblA = this.FindControl<TextBlock>("HealthLabelA");
        if (lblA != null) lblA.Text = "BLUE";
        var lblB = this.FindControl<TextBlock>("HealthLabelB");
        if (lblB != null) lblB.Text = _isBossFight ? "BOSS  I" : "RED";
        SetHealth("HealthBarA", 100);
        SetHealth("HealthBarB", 100);

        var canvas = this.FindControl<Canvas>("FightCanvas");
        if (canvas != null) { canvas.IsVisible = true; canvas.RenderTransform = null; }
        var fA = this.FindControl<Canvas>("FighterA");
        var fB = this.FindControl<Canvas>("FighterB");
        if (fA != null) { fA.RenderTransform = null; fA.Opacity = 1; }
        if (fB != null)
        {
            fB.Opacity = 1;
            if (_isBossFight)
            {
                fB.RenderTransformOrigin = new Avalonia.RelativePoint(15, FootOffset, Avalonia.RelativeUnit.Absolute);
                fB.RenderTransform = new ScaleTransform(3.0, 3.0);
            }
            else
            {
                fB.RenderTransform = null;
            }
        }
        _avx = _bvx = _avy = _bvy = 0;
        _airA = _airB = false;
        _scaleA = _scaleB = 1; _growUntilA = _growUntilB = 0;
        _perchReturnA = _perchReturnB = 0;
        _squashA = _squashB = 0; _rotA = _rotB = 0;
        _stanceA = _stanceB = "";
        _mushGrew = _mushLeaped = _mushSlammed = false;
        _koA = _koB = false; _skitKind = "";
        _projActive = _projDouble = false; _moveKind = ""; _hitResolved = false;
        ClearLogStrands();
        var glassR = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("BulbGlass");
        if (glassR != null) glassR.Fill = Infrastructure.ThemeResources.Brush(this, "AppPanelBrush", new SolidColorBrush(Color.Parse("#334155")));
        foreach (var n in new[] { "Projectile", "Projectile2", "SuperFlash", "UltBeam", "ImpactBurst", "ComicBubble",
                                   "ComicText", "TitleFlash", "DustA", "DustB", "Mushroom", "ShockRing",
                                   "StarsA", "StarsB", "GroundLine", "ShadowA", "ShadowB" })
            SetVisible(n, false);
        foreach (var n in s_skitProps) SetVisible(n, false);
    }

    private Color TokenColor(string key, Color fallback)
    {
        try
        {
            return Infrastructure.ThemeResources.Colour(this, key, fallback);
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        return fallback;
    }

    private void ApplyFighterGlow()
    {
        var fA = this.FindControl<Canvas>("FighterA");
        var fB = this.FindControl<Canvas>("FighterB");
        if (fA != null)
            fA.Effect = new Avalonia.Media.DropShadowEffect { Color = TokenColor("AppInfoColor", Color.Parse("#38bdf8")), BlurRadius = 14, OffsetX = 0, OffsetY = 0, Opacity = 0.8 };
        if (fB != null)
            fB.Effect = new Avalonia.Media.DropShadowEffect { Color = TokenColor("AppDangerColor", Color.Parse("#dc2626")), BlurRadius = 14, OffsetX = 0, OffsetY = 0, Opacity = 0.8 };
    }
    
    public void StopOverlay()
    {
        IsVisible = false;

        HandOverTaskbar();

        var window = GetParentWindow();
        if (window != null)
        {
            window.PropertyChanged -= OnWindowPropertyChanged;
            if (_originalCanResize.HasValue)
            {
                window.CanResize = _originalCanResize.Value;
                _originalCanResize = null;
            }
        }

        AmbientBubblesBackground.GloballySuspended = false;
        _timer?.Stop();
        
        if (_vectorAnimTimer != null)
        {
            _vectorAnimTimer.Stop();
            _vectorAnimTimer = null;
        }

        DetachFightKeyHandler();
        ClearLogStrands();

        _fightCanvasCached = null;
        _fighterACached = null;
        _fighterBCached = null;
        if (_barTimer != null)
        {
            _barTimer.Stop();
            _barTimer = null;
        }

        RuntimeLog.LogAppended -= AppendLog;
        
        try
        {
            if (_smiProcess != null && !_smiProcess.HasExited)
            {
                _smiProcess.Kill();
            }
            _smiProcess?.Dispose();
            _smiProcess = null;
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }
    /// <summary>
    /// What the Windows taskbar button does when the overlay closes.
    ///
    /// It used to be Clear() immediately followed by Flash(): the bar was wiped the same
    /// instant the encode hit 100%, so the one moment the user actually wanted to see - a full
    /// bar - was the one moment that never rendered. Now the terminal state decides:
    ///
    ///   SUCCESS - pin the bar to a full 100% and blink the button five times slowly, then
    ///             clear once the blinking is over, so the full bar and the blinks read as one
    ///             "done" signal rather than two unrelated ones.
    ///   ERROR   - turn the bar red (TBPF_ERROR keeps whatever value it had) and hold, so a
    ///             failure is visible from the taskbar without opening the window.
    ///   CANCEL  - turn it yellow (TBPF_PAUSED) and hold briefly.
    ///   nothing - the overlay was torn down without finishing (window closed mid-export);
    ///             clear straight away, since there is no outcome to report.
    ///
    /// The hold is a fire-and-forget delay guarded by _taskbarGeneration, and every
    /// TaskbarProgress call swallows its own failures, so nothing here can throw into the
    /// teardown path or outlive its usefulness.
    /// </summary>
    private void HandOverTaskbar()
    {
        IntPtr hwnd = HostWindowHandle;
        if (hwnd == IntPtr.Zero) return;

        int generation = ++_taskbarGeneration;
        string terminal = _lastTerminalState;

        const int FlashCount = 5;
        const int FlashIntervalMs = 750;

        int holdMs;
        switch (terminal)
        {
            case "SUCCESS":
                TaskbarProgress.SetProgress(hwnd, 100);
                TaskbarProgress.Flash(hwnd, FlashCount, FlashIntervalMs);
                holdMs = FlashCount * FlashIntervalMs + 450;
                break;
            case "ERROR":
                TaskbarProgress.SetState(hwnd, TaskbarProgress.State.Error);
                TaskbarProgress.Flash(hwnd, FlashCount, FlashIntervalMs);
                holdMs = FlashCount * FlashIntervalMs + 450;
                break;
            case "CANCEL":
                TaskbarProgress.SetState(hwnd, TaskbarProgress.State.Paused);
                holdMs = 1600;
                break;
            default:
                TaskbarProgress.Clear(hwnd);
                return;
        }

        int delay = holdMs;
        Task.Run(async () =>
        {
            await Task.Delay(delay).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                if (_taskbarGeneration != generation) return;
                TaskbarProgress.Clear(hwnd);
            });
        });
    }

    public void UpdateTimeRemaining(string timeRemaining)
    {
        Dispatcher.UIThread.Post(() => 
        {
            var trText = this.FindControl<TextBlock>("TimeRemainingText");
            if (trText != null) trText.Text = timeRemaining;
        });
    }
    
    private double _barTarget;
    private double _barValue;
    private DispatcherTimer? _barTimer;

    public void UpdatePhase(int phaseIndex, string title, int progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var phaseTitle = this.FindControl<TextBlock>("PhaseTitleText");
            if (phaseTitle != null) phaseTitle.Text = title;

            _barTarget = Math.Max(_barTarget, Math.Clamp(progress, 0, 100));
            EnsureBarTimer();

            TaskbarProgress.SetProgress(HostWindowHandle, (int)Math.Round(_barTarget));

            ProcessEasterEggState(progress);
        });
    }

    private void EnsureBarTimer()
    {
        if (_barTimer != null) return;
        _barTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _barTimer.Tick += (_, __) =>
        {
            var phaseBar = this.FindControl<ProgressBar>("PhaseProgressBar");
            var phaseText = this.FindControl<TextBlock>("PhaseProgressText");
            double delta = _barTarget - _barValue;
            if (Math.Abs(delta) < 0.4) _barValue = _barTarget;
            else _barValue += delta * 0.22;
            if (phaseBar != null) phaseBar.Value = _barValue;
            if (phaseText != null) phaseText.Text = $"{(int)Math.Round(_barValue)}%";
        };
        _barTimer.Start();
    }

    private System.Collections.Concurrent.ConcurrentQueue<string> _pendingLogs = new();

    /// <summary>
    /// ISSUE_7 — ceiling on the hand-off queue.
    ///
    /// Only 100 lines are ever shown on screen (see the drain in the timer tick), so anything past
    /// this is already unreachable. The cap matters because this method is invoked from
    /// RuntimeLog.LogAppended, a STATIC event: if the overlay is torn down without StopOverlay
    /// running — a window closed mid-export, say — the subscription outlives the timer that drains
    /// this queue, and an unbounded queue would then grow for the rest of the session while also
    /// keeping this control alive.
    /// </summary>
    private const int MaxPendingLogs = 500;

    /// <summary>
    /// Called on RuntimeLog's writer/caller thread, never the UI thread. Must stay cheap and must
    /// never log anything itself — RuntimeLog invokes LogAppended synchronously from inside Write,
    /// so logging here would recurse.
    /// </summary>
    public void AppendLog(string message)
    {
        _pendingLogs.Enqueue(message);

        while (_pendingLogs.Count > MaxPendingLogs && _pendingLogs.TryDequeue(out _))
        {
        }
    }

    /// <summary>
    /// ISSUE_7 — safety net. StopOverlay is the normal place the static RuntimeLog.LogAppended
    /// subscription is released, but nothing guarantees StopOverlay runs if the host window is
    /// closed while an export is still in flight. Detaching from the visual tree always happens,
    /// so releasing here as well makes the subscription impossible to strand.
    /// Unsubscribing twice is harmless.
    /// </summary>
    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        RuntimeLog.LogAppended -= AppendLog;
        DetachFightKeyHandler();
        _pendingLogs.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private Avalonia.Controls.Window? GetParentWindow()
    {
        return this.VisualRoot as Avalonia.Controls.Window;
    }

    /// <summary>
    /// ISSUE_02: this used to count three clicks and then call TriggerWobbleMicroAnimation(),
    /// which looked up an Image named "EasterEggPlayer". That control does NOT exist in
    /// PhaseOverlayControl.axaml (verified: the string appeared nowhere in the whole solution
    /// except that one lookup) — it belonged to the sprite-based easter egg that the procedural
    /// stick-fighter replaced. So the method hit a null control and returned every single time,
    /// the click counter silently reset, and a DispatcherTimer field existed that could never be
    /// created or started.
    ///
    /// The dead method, its timer field and the click counter are gone. The handler itself is
    /// kept because PhaseOverlayControl.axaml binds it on the root Grid, and it now does the one
    /// thing that is unambiguously safe here: nothing. It deliberately does NOT re-enter
    /// PlaySequence — that would overwrite _vectorState mid-export and corrupt the running fight
    /// animation's state machine.
    /// </summary>
    public void OnOverlayPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        AddHype(10);
    }

    private void LockWindowAndAnchors()
    {
        var window = GetParentWindow();
        if (window != null)
        {
            _originalCanResize = window.CanResize;
            window.CanResize = false;
            
            window.PropertyChanged -= OnWindowPropertyChanged;
            window.PropertyChanged += OnWindowPropertyChanged;
        }

        var pbar = this.FindControl<ProgressBar>("PhaseProgressBar")?.Parent as Control;
        var log = this.FindControl<TextBox>("LiveLogTextBox");
        var graph = this.FindControl<HardwareGraphControl>("GraphCanvas")?.Parent as Control;

        if (pbar != null) _anchorProgressBar = pbar.Bounds;
        if (log != null) _anchorLogBox = log.Bounds;
        if (graph != null) _anchorTelemetryBox = graph.Bounds;
    }

    /// <summary>
    /// Reacts to the host window changing size or window-state during an export.
    ///
    /// WHAT THIS USED TO DO, AND WHY IT WAS WRONG. On maximize - or on any window wider than
    /// 2000px, which today is an ordinary 1440p monitor - it hid the fight canvas and set
    /// _easterEggActive = false. That flag is not a "draw the fighters" switch: it also gates
    /// HandleTerminalState, so tripping it meant the VICTORY finisher never played, the
    /// keyboard handler was never released through the terminal path, and the taskbar never
    /// learned how the export ended - permanently, for the rest of that export, with no way
    /// back even after the window was restored. A cosmetic guard was silently disabling real
    /// behaviour, and it fired on hardware most users have.
    ///
    /// The whole layout is derived from canvas.Bounds every single frame (FightAnchor,
    /// LayoutHud, DrawGround), so a resize needs no special handling at all - the next tick
    /// already draws at the new size, and the fighters' rest positions ease back to the new
    /// centre on their own. The one thing that does NOT self-correct is a running log-tangle
    /// skit: its strands were authored against the live log box's on-screen position at the
    /// moment the skit started, so after a resize they would fly to where that box used to be.
    /// That skit is ended cleanly instead.
    /// </summary>
    private void OnWindowPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name != "WindowState" && e.Property.Name != "Bounds") return;
        if (!_easterEggActive) return;

        var canvas = this.FindControl<Canvas>("FightCanvas");
        if (canvas == null) return;

        if (!canvas.IsVisible) canvas.IsVisible = true;

        if (_moveKind == "skit" && _skitKind == "logtangle")
        {
            RuntimeLog.Info("EasterEgg", "Window geometry changed during the log-tangle skit; ending it so the strands do not point at the old log-box position.");
            EndSkit(_processStopwatch.Elapsed.TotalSeconds);
        }
    }


    private DispatcherTimer? _vectorAnimTimer;
    private double _animTime = 0.0;
    private string _vectorState = "";

    private double _ax, _ay, _bx, _by;
    private double _avx, _bvx;
    private double _avy, _bvy;
    private double _tgtAy, _tgtBy;
    private bool _airA, _airB;
    private double _perchReturnA, _perchReturnB;
    private double _scaleA = 1, _scaleB = 1;
    private double _growUntilA, _growUntilB;
    private bool _posInit;
    private double _wanderA, _wanderB;
    private double _nextWanderTime;

    private bool _mushGrew, _mushLeaped, _mushSlammed;

    private string _skitKind = "";
    private double _skitStart, _skitDur;
    private int _skitPhase;
    private double _nextSkitTime;
    private bool _koA, _koB;
    private double _koStartA, _koStartB, _koUntilA, _koUntilB;

    private double _nextMoveTime;
    private string _moveKind = "";
    private double _moveStart, _moveDur;
    private bool _attackerIsA = true;
    private bool _hitResolved;
    private string _defReaction = "hit";
    private int _comboLeft;
    private bool _projActive, _projDouble;
    private double _projT, _projDur = 0.32, _projArc = 26;
    private Point _projStart, _projEnd;

    private double _nextTauntTime;
    private double _tauntUntil;
    private int _tauntIndex;
    private int _lastFightProgress;
    private double _impactUntil;
    private double _shakeUntil; private bool _shakeIsA;
    private double _titleUntil;
    private double _superUntil;
    private bool _finisherDone;
    private bool _isBossFight = false;
    private double _hypeLevel = 0;

    private bool _loserIsA;

    private double _squashA, _squashB;
    private double _rotA, _rotB;
    private int _faceA = 1, _faceB = -1;
    private string _stanceA = "", _stanceB = "";

    private int _bossPhase = 1;
    private bool _bossPhase2Announced;
    private double _camShakeUntil, _camShakeMag;

    public static bool FightInputActive { get; private set; }
    private EventHandler<Avalonia.Input.KeyEventArgs>? _fightKeyHandler;
    private Avalonia.Controls.Window? _fightKeyWindow;
    private double _ultFlashUntil;

    private readonly List<TextBlock> _logStrands = new();
    private readonly List<LogStrandPlan> _logStrandPlan = new();
    private double _logTangleAnchorX;
    private struct LogStrandPlan
    {
        public Point From;
        public Point To;
        public double Rot;
        public double EndScale;
        public double Delay;
    }

    private const double EntranceDur = 1.05;
    private const double Gravity = 2.4;
    private const double FootOffset = 120;

    private static readonly string[] s_taunts =
        { "Encoding!", "Hold still!", "2x speed!", "Almost!", "Rendering!", "Take that!",
          "Get shorty!", "Frame by frame!", "Compressing!", "Eat pixels!", "Too slow!", "Final boss!" };
    private static readonly string[] s_impacts =
        { "POW!", "BAM!", "WHAM!", "BIFF!", "KAPOW!", "THWACK!", "CRUNCH!", "SMACK!", "BOOM!" };
    private static readonly string[] s_moves =
        { "throw", "throw", "dash", "dash", "uppercut", "kick", "feint", "leap", "hide", "super", "mushroom" };
    private static readonly string[] s_bossMoves =
        { "stomp", "stomp", "dash", "throw", "kick", "super" };
    private static readonly string[] s_projBrush =
        { "AppWarningBrush", "AppInfoBrush", "AppAccentBrush", "AppDangerBrush", "AppSuccessBrush" };
    private static readonly string[] s_skits =
        { "door", "bulb", "logtangle", "logtangle", "banana", "tunnel", "piano", "buzzer", "tug", "snooze" };
    private static readonly string[] s_fakeLogLines =
        { "[ffmpeg] frame=1042 fps=61 q=23", "[enc] nvenc_h264 preset=p5", "[mux] writing packet 88421",
          "[scale] 2560x1440 -> 1920x1080", "[audio] aac 192k lufs -14.0", "[io] flush 32 MiB to disk",
          "[gpu] session 1/2 busy", "[probe] stream 0 ok", "[filter] fps=60 applied" };

    /// <summary>Props that must be hidden between skits / at reset.</summary>
    private static readonly string[] s_skitProps =
        { "Door", "Ladder", "Bulb", "ZapBolt", "LogTangle", "BananaPeel", "FakeTunnel",
          "Piano", "TugRope", "SleepZzz", "FaceDoodle" };

    private void PlaySequence(string sequenceName)
    {
        _currentSequence = sequenceName;
        RuntimeLog.Info("EasterEgg", $"Fight sequence: {sequenceName}");

        Dispatcher.UIThread.Post(() =>
        {
            if (_vectorAnimTimer == null)
            {
                _vectorAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _vectorAnimTimer.Tick += OnVectorAnimTick;
                _vectorAnimTimer.Start();
            }

            _vectorState = sequenceName;

            if (sequenceName == "Entrance")
            {
                _finisherDone = false;
                _titleUntil = 0;
            }
        });
    }

    private (double midX, double fightY, double w, double h) FightAnchor(Canvas canvas)
    {
        double w = canvas.Bounds.Width > 10 ? canvas.Bounds.Width : 900;
        double h = canvas.Bounds.Height > 10 ? canvas.Bounds.Height : 600;
        return (w / 2.0, h * 0.26, w, h);
    }

    private Canvas? _fightCanvasCached;
    private Canvas? _fighterACached;
    private Canvas? _fighterBCached;


    /// <summary>
    /// While an export is running the overlay owns the spacebar. Previously Space fell through
    /// to MainWindow / VideoMergerWindow and toggled mpv playback behind the overlay, so the
    /// meter's own label ("MASH SPACEBAR") was a lie and the user was blind-poking the player.
    /// The handler is registered on the host window for BOTH routing strategies with
    /// handledEventsToo:true, so it still lands even if a host handler marks the event first;
    /// the hosts additionally early-out on FightInputActive so playback never toggles.
    /// </summary>
    private void AttachFightKeyHandler()
    {
        DetachFightKeyHandler();

        var window = GetParentWindow();
        if (window == null) return;

        _fightKeyWindow = window;
        _fightKeyHandler = (s, e) =>
        {
            if (!_easterEggActive) return;
            if (e.Key != Avalonia.Input.Key.Space) return;

            var focused = Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (focused is TextBox tb && !tb.IsReadOnly) return;

            e.Handled = true;
            AddHype(12);
        };

        window.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, _fightKeyHandler,
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        FightInputActive = true;
    }

    private void DetachFightKeyHandler()
    {
        if (_fightKeyHandler == null) return;

        FightInputActive = false;
        if (_fightKeyWindow != null && _fightKeyHandler != null)
        {
            try { _fightKeyWindow.RemoveHandler(Avalonia.Input.InputElement.KeyDownEvent, _fightKeyHandler); }
            catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }
        _fightKeyHandler = null;
        _fightKeyWindow = null;
    }

    private void AddHype(double amount)
    {
        _hypeLevel = Math.Min(100, _hypeLevel + amount);
        var bar = this.FindControl<Avalonia.Controls.ProgressBar>("HypeMeterBar");
        if (bar != null) bar.Value = _hypeLevel;
    }


    private void OnVectorAnimTick(object? sender, EventArgs e)
    {
        _animTime += 0.1;
        double now = _processStopwatch.Elapsed.TotalSeconds;

        var canvas = _fightCanvasCached ??= this.FindControl<Canvas>("FightCanvas");
        var fA = _fighterACached ??= this.FindControl<Canvas>("FighterA");
        var fB = _fighterBCached ??= this.FindControl<Canvas>("FighterB");
        if (canvas == null || fA == null || fB == null) return;
        fA.IsVisible = true; fB.IsVisible = true;

        var (midX, fightY, w, h) = FightAnchor(canvas);
        double groundY = fightY + FootOffset;

        LayoutHud(w);
        DrawGround(canvas, w, groundY);
        UpdateHealthBars(now, midX, fightY);
        ApplyCameraShake(canvas, now);

        if (_vectorState == "Melee" && _moveKind == "")
        {
            if (_hypeLevel >= 100)
            {
                _hypeLevel = 0;
                _attackerIsA = true;
                if (_koA) { _koA = false; _rotA = 0; SetVisible("StarsA", false); }
                _moveKind = "ult";
                _moveStart = now;
                _moveDur = 0.62;
                _hitResolved = false;
                _defReaction = "hit";
                StartUltBeam(now, fightY);
            }
            else if (_hypeLevel > 0)
            {
                _hypeLevel = Math.Max(0, _hypeLevel - 0.45);
            }
            var bar = this.FindControl<Avalonia.Controls.ProgressBar>("HypeMeterBar");
            if (bar != null) bar.Value = _hypeLevel;
        }

        if (_vectorState == "Entrance" && now >= EntranceDur)
        {
            _vectorState = "Melee";
            _ax = midX - 95; _bx = midX + 35; _ay = _by = fightY; _tgtAy = _tgtBy = fightY; _posInit = true;
        }

        if (_vectorState == "Entrance")
        {
            double p = Math.Clamp(now / EntranceDur, 0, 1);
            double ease = 1 - Math.Pow(1 - p, 3);
            double ex = (-140) + ((midX - 95) - (-140)) * ease;
            double exB = (w + 140) + ((midX + 35) - (w + 140)) * ease;
            _ax = ex; _bx = exB; _ay = _by = fightY;
            Canvas.SetLeft(fA, ex); Canvas.SetTop(fA, fightY);
            Canvas.SetLeft(fB, exB); Canvas.SetTop(fB, fightY);
            _faceA = 1; _faceB = -1;
            UpdateFighterPoses("A", true, false, _faceA, "");
            UpdateFighterPoses("B", true, false, _faceB, "");
            ApplyFighterTransform(fA, true, now);
            ApplyFighterTransform(fB, false, now);
            DrawShadows(fightY, groundY);
            if (p >= 1.0 && _titleUntil == 0)
            {
                _titleUntil = now + 0.85;
                ShowTitle(_isBossFight ? "BOSS!" : "FIGHT!", midX, fightY - 70, _isBossFight ? "AppDangerBrush" : "AppOnAccentTextBrush");
                PuffDust("DustA", midX - 80, groundY);
                PuffDust("DustB", midX + 50, groundY);
            }
        }
        else if (_vectorState == "Melee")
        {
            if (!_posInit) { _ax = midX - 95; _bx = midX + 35; _ay = _by = fightY; _tgtAy = _tgtBy = fightY; _posInit = true; }
            TickFight(now, midX, fightY, w, h);
            DrawShadows(fightY, groundY);
        }
        else
        {
            if (_vectorState == "End") RenderFinisher(now, fA, fB, _ax, _bx, fightY, midX);
        }

        if (now >= _impactUntil) SetVisible("ImpactBurst", false);
        if (now >= _titleUntil && _vectorState != "End") SetVisible("TitleFlash", false);
        if (now >= _superUntil) SetVisible("SuperFlash", false);
        if (now >= _ultFlashUntil) SetVisible("UltBeam", false);
    }


    private void DrawGround(Canvas canvas, double w, double groundY)
    {
        var line = this.FindControl<Avalonia.Controls.Shapes.Line>("GroundLine");
        if (line == null) return;
        line.IsVisible = true;
        Canvas.SetLeft(line, 0);
        Canvas.SetTop(line, 0);
        line.StartPoint = new Point(Math.Max(0, w * 0.04), groundY);
        line.EndPoint = new Point(Math.Max(10, w * 0.96), groundY);
    }

    private void DrawShadows(double fightY, double groundY)
    {
        DrawOneShadow("ShadowA", _ax, _ay, fightY, groundY, _isBossFight ? 1.0 : _scaleA);
        DrawOneShadow("ShadowB", _bx, _by, fightY, groundY, _isBossFight ? 3.0 : _scaleB);
    }

    private void DrawOneShadow(string name, double fx, double fy, double fightY, double groundY, double scale)
    {
        var sh = this.FindControl<Avalonia.Controls.Shapes.Ellipse>(name);
        if (sh == null) return;
        double lift = Math.Max(0, fightY - fy);
        double shrink = Math.Clamp(1.0 - lift / 260.0, 0.35, 1.0);
        double wdt = 54 * scale * shrink;
        double hgt = 12 * scale * shrink;
        sh.IsVisible = true;
        sh.Width = wdt;
        sh.Height = hgt;
        sh.Opacity = 0.32 * shrink;
        Canvas.SetLeft(sh, fx + 15 * scale - wdt / 2.0);
        Canvas.SetTop(sh, groundY - hgt / 2.0);
    }


    private void LayoutHud(double w)
    {
        var hype = this.FindControl<Border>("HypeContainer");
        if (hype != null) { Canvas.SetLeft(hype, Math.Max(0, w / 2.0 - 110)); Canvas.SetTop(hype, 16); }

        var pa = this.FindControl<Border>("HealthPanelA");
        if (pa != null) { pa.IsVisible = true; Canvas.SetLeft(pa, 26); Canvas.SetTop(pa, 48); }

        var pb = this.FindControl<Border>("HealthPanelB");
        if (pb != null) { pb.IsVisible = true; Canvas.SetLeft(pb, Math.Max(300, w - 286)); Canvas.SetTop(pb, 48); }

        var boss = this.FindControl<TextBlock>("BossLabel");
        if (boss != null) { Canvas.SetLeft(boss, Math.Max(0, w / 2.0 - 145)); Canvas.SetTop(boss, 82); }
    }

    private void UpdateHealthBars(double now, double midX, double fightY)
    {
        double prog = Math.Clamp(_lastFightProgress / 100.0, 0, 1);

        double loserHp = 100.0 * (1.0 - prog);
        double winnerHp = 100.0 - 42.0 * prog;

        if (_isBossFight)
        {
            int phase = prog < 0.5 ? 1 : 2;
            loserHp = phase == 1 ? 100.0 * (1.0 - prog * 2.0) : 100.0 * (1.0 - (prog - 0.5) * 2.0);
            if (phase == 2 && !_bossPhase2Announced)
            {
                _bossPhase2Announced = true;
                _bossPhase = 2;
                ShowTitle("PHASE 2!", midX, fightY - 70, "AppDangerBrush");
                _titleUntil = now + 1.2;
                Shake(now, 0.7, 9);
                Shockwave(now, _bx + 20, fightY + FootOffset);
                _nextMoveTime = now + 0.25;
            }
            var lbl = this.FindControl<TextBlock>("HealthLabelB");
            if (lbl != null) lbl.Text = phase == 1 ? "BOSS  I" : "BOSS  II";
        }

        SetHealth("HealthBarA", _loserIsA ? loserHp : winnerHp);
        SetHealth("HealthBarB", _loserIsA ? winnerHp : loserHp);
    }

    private void SetHealth(string name, double value)
    {
        var bar = this.FindControl<Avalonia.Controls.ProgressBar>(name);
        if (bar == null) return;
        double v = Math.Clamp(value, 0, 100);
        bar.Value = v;
        string key = v > 55 ? "AppSuccessBrush" : v > 25 ? "AppWarningBrush" : "AppDangerBrush";
        bar.Foreground = Infrastructure.ThemeResources.Brush(this, key, new SolidColorBrush(Color.Parse("#22c55e")));
    }


    private void Shake(double now, double seconds, double magnitude)
    {
        _camShakeUntil = Math.Max(_camShakeUntil, now + seconds);
        _camShakeMag = Math.Max(_camShakeMag, magnitude);
    }

    private void ApplyCameraShake(Canvas canvas, double now)
    {
        if (now >= _camShakeUntil)
        {
            if (canvas.RenderTransform is TranslateTransform) canvas.RenderTransform = null;
            _camShakeMag = 0;
            return;
        }
        double left = _camShakeUntil - now;
        double mag = _camShakeMag * Math.Clamp(left / 0.5, 0, 1);
        canvas.RenderTransform = new TranslateTransform(Math.Sin(now * 71) * mag, Math.Cos(now * 53) * mag * 0.6);
    }


    private void TickFight(double now, double midX, double fightY, double w, double h)
    {
        var fA = _fighterACached ??= this.FindControl<Canvas>("FighterA");
        var fB = _fighterBCached ??= this.FindControl<Canvas>("FighterB");
        if (fA == null || fB == null) return;

        if (now >= _nextWanderTime)
        {
            _wanderA = _rand.NextDouble() * 90;
            _wanderB = _rand.NextDouble() * 90;
            _nextWanderTime = now + 1.6 + _rand.NextDouble() * 2.4;
        }
        double restAx = midX - 55 - _wanderA;
        double restBx = midX + 25 + _wanderB;

        if (_perchReturnA > 0 && now >= _perchReturnA) { _tgtAy = fightY; _perchReturnA = 0; }
        if (_perchReturnB > 0 && now >= _perchReturnB) { _tgtBy = fightY; _perchReturnB = 0; }
        if (_growUntilA > 0 && now >= _growUntilA) { _scaleA = 1; _growUntilA = 0; }
        if (_growUntilB > 0 && now >= _growUntilB) { _scaleB = 1; _growUntilB = 0; }

        if (_moveKind == "" && now >= _nextMoveTime) StartMove(now, midX, fightY, w, h);

        if (_moveKind != "") AdvanceMove(now, midX, fightY, w, h);

        bool freeDrift = _moveKind is "" or "throw" or "feint";
        if (Math.Abs(_avx) < 0.2 && freeDrift)
            _ax += (restAx - _ax) * 0.04 + Math.Sin(_animTime * 0.5) * 0.6;
        if (Math.Abs(_bvx) < 0.2 && freeDrift)
            _bx += (restBx - _bx) * 0.04 + Math.Cos(_animTime * 0.5) * 0.6;

        _ax += _avx; _bx += _bvx;
        _avx *= 0.86; _bvx *= 0.86;
        if (Math.Abs(_avx) < 0.15) _avx = 0;
        if (Math.Abs(_bvx) < 0.15) _bvx = 0;

        if (_airA) { _avy += Gravity; _ay += _avy; if (_ay >= _tgtAy) { if (_avy > 8) { PuffDust("DustA", _ax + 15, _tgtAy + FootOffset); _squashA = Math.Max(_squashA, Math.Min(0.5, _avy / 60.0)); } _ay = _tgtAy; _avy = 0; _airA = false; } }
        else _ay += (_tgtAy - _ay) * 0.14;
        if (_airB) { _bvy += Gravity; _by += _bvy; if (_by >= _tgtBy) { if (_bvy > 8) { PuffDust("DustB", _bx + 15, _tgtBy + FootOffset); _squashB = Math.Max(_squashB, Math.Min(0.5, _bvy / 60.0)); } _by = _tgtBy; _bvy = 0; _airB = false; } }
        else _by += (_tgtBy - _by) * 0.14;

        _ax = Math.Clamp(_ax, 10, w - 60); _bx = Math.Clamp(_bx, 10, w - 60);

        double gap = _bx - _ax;
        if (Math.Abs(gap) > 14)
        {
            _faceA = gap > 0 ? 1 : -1;
            _faceB = -_faceA;
        }

        double sA = (now < _shakeUntil && _shakeIsA) ? Math.Sin(now * 90) * 4 : 0;
        double sB = (now < _shakeUntil && !_shakeIsA) ? Math.Sin(now * 90) * 4 : 0;
        Canvas.SetLeft(fA, _ax + sA); Canvas.SetTop(fA, _ay);
        Canvas.SetLeft(fB, _bx + sB); Canvas.SetTop(fB, _by);

        bool aStrike = _attackerIsA && _moveKind != "" && !_hitResolved;
        bool bStrike = !_attackerIsA && _moveKind != "" && !_hitResolved;
        UpdateFighterPoses("A", !_koA, aStrike && !_koA, _faceA, _stanceA);
        UpdateFighterPoses("B", !_koB, bStrike && !_koB, _faceB, _stanceB);

        RenderKO(now, true);
        RenderKO(now, false);

        _squashA *= 0.84; if (_squashA < 0.01) _squashA = 0;
        _squashB *= 0.84; if (_squashB < 0.01) _squashB = 0;

        ApplyFighterTransform(fA, true, now);
        ApplyFighterTransform(fB, false, now);

        if (now >= _nextTauntTime && _moveKind != "skit")
        {
            double sx = (_rand.Next(2) == 0 ? _ax : _bx);
            ShowTaunt(NextTaunt(), sx + 20, fightY - 26);
            _tauntUntil = now + 2.6;
            _nextTauntTime = now + 3.4 + _rand.NextDouble() * 2.0;
        }
        if (now >= _tauntUntil) { SetVisible("ComicBubble", false); SetVisible("ComicText", false); }
    }


    private string NextTaunt()
    {
        var live = new List<string>();
        int prog = Math.Clamp(_lastFightProgress, 0, 100);
        int gpu = Math.Clamp(_lastGpu, 0, 100);
        int cpu = _cpuHist.Count > 0 ? _cpuHist[_cpuHist.Count - 1] : 0;
        int mem = _memHist.Count > 0 ? _memHist[_memHist.Count - 1] : 0;

        live.Add($"Only {prog}%! Weak!");
        live.Add($"{100 - prog}% left, hurry!");
        if (gpu > 3) live.Add($"GPU {gpu}% - he's cooking!");
        if (cpu > 3) live.Add($"CPU {cpu}%! Feel the burn!");
        if (mem > 3) live.Add($"RAM {mem}%! Ouch!");
        if (prog > 90) live.Add("Finish him!");
        if (_isBossFight) live.Add($"Boss phase {_bossPhase}!");

        var tr = this.FindControl<TextBlock>("TimeRemainingText")?.Text;
        if (!string.IsNullOrWhiteSpace(tr) && !tr.StartsWith("Estimating", StringComparison.OrdinalIgnoreCase))
            live.Add(Shorten(tr, 30));

        var ph = this.FindControl<TextBlock>("PhaseTitleText")?.Text;
        if (!string.IsNullOrWhiteSpace(ph)) live.Add(Shorten(ph, 30));

        if (_logLines.Count > 0) live.Add(Shorten(_logLines[_logLines.Count - 1], 30));

        if (live.Count > 0 && _rand.NextDouble() < 0.62) return live[_rand.Next(live.Count)];

        _tauntIndex = _rand.Next(s_taunts.Length);
        return s_taunts[_tauntIndex];
    }

    private static string Shorten(string s, int max)
    {
        s = (s ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (s.Length > max) s = s.Substring(0, max).TrimEnd() + "...";
        return s.Length == 0 ? "..." : s;
    }


    private void StartMove(double now, double midX, double fightY, double w, double h)
    {
        if (!_isBossFight && _comboLeft == 0 && now >= _nextSkitTime && !_koA && !_koB && _rand.NextDouble() < 0.55)
        {
            StartSkit(now, midX, fightY, w, h);
            return;
        }

        if (_comboLeft > 0) { _comboLeft--; }
        else _attackerIsA = _rand.Next(2) == 0;
        if (_isBossFight) _attackerIsA = _rand.NextDouble() < 0.35;
        if (_koA && _attackerIsA) _attackerIsA = false;
        if (_koB && !_attackerIsA) _attackerIsA = true;
        if (_koA && _koB) { _moveKind = ""; _nextMoveTime = now + 0.4; return; }

        _moveKind = (_isBossFight && !_attackerIsA)
            ? s_bossMoves[_rand.Next(s_bossMoves.Length)]
            : s_moves[_rand.Next(s_moves.Length)];

        _moveStart = now; _hitResolved = false;

        double r = _rand.NextDouble();
        _defReaction = (_moveKind is "super" or "mushroom" or "ult" or "stomp") ? "hit" : (r < 0.18 ? "dodge" : r < 0.33 ? "block" : "hit");

        double atkX = _attackerIsA ? _ax : _bx;
        double defX = _attackerIsA ? _bx : _ax;
        int dir = _attackerIsA ? 1 : -1;

        switch (_moveKind)
        {
            case "throw":
                _moveDur = 0.30 + _rand.NextDouble() * 0.22;
                _projDur = _moveDur; _projArc = 10 + _rand.NextDouble() * 40;
                _projDouble = _rand.NextDouble() < 0.3;
                _projStart = new Point(atkX + 15 + dir * 18, fightY + 40);
                _projEnd = new Point(defX + 15, fightY + 10 + _rand.NextDouble() * 20);
                _projT = 0; _projActive = true;
                RandomizeProjectile();
                break;
            case "dash":
                _moveDur = 0.42;
                if (_attackerIsA) _avx = (defX - atkX) * 0.12; else _bvx = (defX - atkX) * 0.12;
                break;
            case "uppercut":
            case "kick":
                _moveDur = 0.36;
                if (_attackerIsA) _avx = dir * 3.0; else _bvx = dir * 3.0;
                break;
            case "feint":
                _moveDur = 0.28;
                if (_attackerIsA) _avx = dir * 3.5; else _bvx = dir * 3.5;
                break;
            case "super":
                _moveDur = 0.5;
                break;

            case "stomp":
                _moveDur = 1.05;
                _tgtBy = fightY; _airB = true; _bvy = -24;
                _bx += (defX - _bx) * 0.25;
                ShowTaunt(_rand.Next(2) == 0 ? "SMALL." : "STAY DOWN.", _bx + 20, fightY - 40);
                _tauntUntil = now + 1.2; _nextTauntTime = now + 2.4;
                break;

            case "leap":
            {
                _moveDur = 0.5;
                double perchY = Math.Max(6, fightY - (95 + _rand.NextDouble() * 55));
                if (_attackerIsA) { _tgtAy = perchY; _airA = true; _avy = -20; _perchReturnA = now + 1.3 + _rand.NextDouble() * 1.2; }
                else { _tgtBy = perchY; _airB = true; _bvy = -20; _perchReturnB = now + 1.3 + _rand.NextDouble() * 1.2; }
                ShowTaunt(_rand.Next(2) == 0 ? "Up here!" : "Can't reach me!", (_attackerIsA ? _ax : _bx) + 20, perchY - 24);
                _tauntUntil = now + 1.6; _nextTauntTime = now + 2.8;
                break;
            }
            case "hide":
            {
                _moveDur = 0.5;
                double hideY = Math.Min(h - 130, fightY + 120 + _rand.NextDouble() * 70);
                if (_attackerIsA) { _tgtAy = hideY; _perchReturnA = now + 1.2 + _rand.NextDouble() * 1.0; }
                else { _tgtBy = hideY; _perchReturnB = now + 1.2 + _rand.NextDouble() * 1.0; }
                ShowTaunt(_rand.Next(2) == 0 ? "Hiding!" : "Nope!", (_attackerIsA ? _ax : _bx) + 20, hideY - 24);
                _tauntUntil = now + 1.4; _nextTauntTime = now + 2.8;
                break;
            }
            case "mushroom":
            {
                _moveDur = 1.7;
                _mushGrew = _mushLeaped = _mushSlammed = false;
                var mush = this.FindControl<Canvas>("Mushroom");
                if (mush != null)
                {
                    double mx = (_ax + _bx) / 2.0 + 15;
                    mush.IsVisible = true;
                    Canvas.SetLeft(mush, mx - 13);
                    Canvas.SetTop(mush, fightY + 96);
                }
                break;
            }
        }
    }

    private void AdvanceMove(double now, double midX, double fightY, double w, double h)
    {
        if (_moveKind == "skit") { AdvanceSkit(now, midX, fightY, w, h); if (now - _skitStart >= _skitDur) EndSkit(now); return; }

        double t = Math.Clamp((now - _moveStart) / Math.Max(0.01, _moveDur), 0, 1);

        if (_moveKind == "mushroom") { AdvanceMushroom(now, t, fightY); if (t >= 1.0) EndMove(now); return; }
        if (_moveKind == "ult") { AdvanceUlt(now, t, fightY); if (t >= 1.0) EndMove(now); return; }
        if (_moveKind == "stomp") { AdvanceStomp(now, t, fightY); if (t >= 1.0) EndMove(now); return; }

        if (_projActive)
        {
            _projT = t;
            MoveProjectile("Projectile", _projStart, _projEnd, t, _projArc, 0);
            if (_projDouble) MoveProjectile("Projectile2", _projStart, _projEnd, Math.Max(0, t - 0.18), _projArc + 12, 10);
        }

        bool strikes = _moveKind is not ("leap" or "hide");
        if (strikes && !_hitResolved && t >= (_moveKind == "throw" ? 1.0 : 0.5))
        {
            _hitResolved = true;
            ResolveHit(now, fightY);
        }

        if (t >= 1.0) EndMove(now);
    }

    private void EndMove(double now)
    {
        _projActive = false; _projDouble = false;
        SetVisible("Projectile", false); SetVisible("Projectile2", false);
        _moveKind = "";
        _stanceA = ""; _stanceB = "";

        double prog = Math.Clamp(_lastFightProgress / 100.0, 0, 1);
        double load = Math.Clamp(_lastGpu / 100.0, 0, 1);
        double baseGap = 0.75 - 0.4 * prog - 0.2 * load;
        if (_isBossFight && _bossPhase == 2) baseGap -= 0.15;
        if (_comboLeft > 0) baseGap = 0.14;
        else if (_rand.NextDouble() < 0.22) _comboLeft = 1 + _rand.Next(2);
        _nextMoveTime = now + Math.Max(0.12, baseGap);
    }


    private void StartUltBeam(double now, double fightY)
    {
        _ultFlashUntil = now + 0.9;
        ShowTitle("ULTIMATE!", (_ax + _bx) / 2 + 15, fightY - 78, "AppInfoBrush");
        _titleUntil = now + 1.1;
        Shake(now, 0.55, 7);
        RuntimeLog.Info("EasterEgg", "Hype meter full - ultimate fired.");
    }

    private void AdvanceUlt(double now, double t, double fightY)
    {
        var beam = this.FindControl<Avalonia.Controls.Shapes.Rectangle>("UltBeam");
        _stanceA = "reach";

        if (beam != null)
        {
            double x0 = _ax + 30;
            double x1 = _bx + 15;
            double grow = Math.Clamp(t / 0.45, 0, 1);
            double len = Math.Max(10, (x1 - x0) * grow);
            double hgt = 10 + 34 * Math.Sin(Math.Clamp(t, 0, 1) * Math.PI);
            beam.IsVisible = true;
            beam.Opacity = 0.35 + 0.55 * Math.Sin(Math.Clamp(t, 0, 1) * Math.PI);
            beam.Width = Math.Abs(len);
            beam.Height = hgt;
            Canvas.SetLeft(beam, Math.Min(x0, x0 + len));
            Canvas.SetTop(beam, fightY + 42 - hgt / 2.0);
        }

        if (!_hitResolved && t >= 0.45)
        {
            _hitResolved = true;
            var pos = new Point(_bx + 15, fightY + 20);
            _squashB = 0.8;
            Shake(now, 0.6, 11);
            Shockwave(now, pos.X, fightY + FootOffset);
            SetVisible("SuperFlash", true);
            var flash = this.FindControl<Avalonia.Controls.Shapes.Rectangle>("SuperFlash");
            if (flash != null)
            {
                flash.Opacity = 0.55; _superUntil = now + 0.4;
                Task.Run(async () => { for (double o = 0.55; o > 0; o -= 0.06) { await Task.Delay(24); double oo = o; Dispatcher.UIThread.Post(() => flash.Opacity = oo); } });
            }

            if (_isBossFight)
            {
                ShowImpact("STAGGERED!", pos.X, pos.Y - 20);
                _impactUntil = now + 0.7;
                _bvx = 6;
            }
            else
            {
                ShowImpact("OBLITERATED!", pos.X, pos.Y - 20);
                _impactUntil = now + 0.7;
                _bvx = 20; _bvy = -15; _airB = true; _tgtBy = fightY;
                KnockOut(false, now, 1.5);
            }
        }
    }


    private void AdvanceStomp(double now, double t, double fightY)
    {
        if (t < 0.55)
        {
            _stanceB = "";
            return;
        }
        if (!_hitResolved && !_airB)
        {
            _hitResolved = true;
            double px = _bx + 45;
            ShowImpact("STOMP!!", px, fightY + 10);
            _impactUntil = now + 0.6;
            Shake(now, 0.8, 14);
            Shockwave(now, px, fightY + FootOffset);
            PuffDust("DustB", px, fightY + FootOffset);
            _squashB = 0.55;

            if (Math.Abs(_ax - _bx) < 260)
            {
                _avx = (_ax < _bx ? -1 : 1) * 17;
                _avy = -13; _airA = true; _tgtAy = fightY;
                _squashA = 0.6;
                KnockOut(true, now, 1.2);
            }
            else
            {
                ShowTaunt("Missed me!", _ax + 20, fightY - 26);
                _tauntUntil = now + 1.2;
            }
        }
    }


    private void AdvanceMushroom(double now, double t, double fightY)
    {
        double defX = _attackerIsA ? _bx : _ax;

        if (t < 0.25)
        {
            var mush = this.FindControl<Canvas>("Mushroom");
            double mx = mush != null ? Canvas.GetLeft(mush) : (_ax + _bx) / 2;
            if (_attackerIsA) _ax += (mx - _ax) * 0.15; else _bx += (mx - _bx) * 0.15;
        }
        else if (!_mushGrew)
        {
            _mushGrew = true;
            SetVisible("Mushroom", false);
            if (_attackerIsA) { _scaleA = 2.0; _growUntilA = now + 2.6; } else { _scaleB = 2.0; _growUntilB = now + 2.6; }
            ShowTitle("POWER-UP!", (_ax + _bx) / 2 + 15, fightY - 80, "AppSuccessBrush");
            _titleUntil = now + 0.9;
        }
        else if (!_mushLeaped && t >= 0.42)
        {
            _mushLeaped = true;
            if (_attackerIsA) { _tgtAy = fightY; _airA = true; _avy = -26; } else { _tgtBy = fightY; _airB = true; _bvy = -26; }
        }
        else if (_mushLeaped && !_mushSlammed && t >= 0.62)
        {
            _mushSlammed = true;
            if (_attackerIsA) { _ax += (defX - _ax) * 0.5; _avy = 30; } else { _bx += (defX - _bx) * 0.5; _bvy = 30; }
        }
        else if (_mushSlammed && !_hitResolved && ((_attackerIsA && !_airA) || (!_attackerIsA && !_airB)))
        {
            _hitResolved = true;
            var impactPos = new Point(defX + 15, fightY + 10);
            ShowImpact("SMASH!!", impactPos.X, impactPos.Y - 16);
            ShowTitle("K.O.!", (_ax + _bx) / 2 + 15, fightY - 80, "AppWarningBrush");
            _impactUntil = now + 0.6; _titleUntil = now + 0.9;
            _shakeUntil = now + 0.45; _shakeIsA = !_attackerIsA;
            Shake(now, 0.4, 6);
            int dir = _attackerIsA ? 1 : -1;
            if (_attackerIsA) { _bvx = 15 * dir; _tgtBy = fightY; _airB = true; _bvy = -16; _squashB = 0.7; }
            else { _avx = 15 * dir; _tgtAy = fightY; _airA = true; _avy = -16; _squashA = 0.7; }
            Shockwave(now, impactPos.X, fightY + FootOffset);
        }
    }


    private double GetX(bool isA) => isA ? _ax : _bx;
    private double GetY(bool isA) => isA ? _ay : _by;
    private void SetX(bool isA, double v) { if (isA) _ax = v; else _bx = v; }
    private void LerpX(bool isA, double target, double k) { if (isA) _ax += (target - _ax) * k; else _bx += (target - _bx) * k; }
    private void SetY(bool isA, double v) { if (isA) _ay = v; else _by = v; }
    private void SetVX(bool isA, double v) { if (isA) _avx = v; else _bvx = v; }
    private void SetStance(bool isA, string s) { if (isA) _stanceA = s; else _stanceB = s; }
    private void SetSquash(bool isA, double v) { if (isA) _squashA = Math.Max(_squashA, v); else _squashB = Math.Max(_squashB, v); }
    private void Launch(bool isA, double vy, double restY)
    {
        if (isA) { _tgtAy = restY; _airA = true; _avy = vy; }
        else { _tgtBy = restY; _airB = true; _bvy = vy; }
    }
    private void SetFighterOpacity(bool isA, double o)
    {
        var f = this.FindControl<Canvas>(isA ? "FighterA" : "FighterB");
        if (f != null) f.Opacity = Math.Clamp(o, 0, 1);
    }


    private void StartSkit(double now, double midX, double fightY, double w, double h)
    {
        _moveKind = "skit"; _skitStart = now; _skitPhase = 0; _hitResolved = false;
        _skitKind = s_skits[_rand.Next(s_skits.Length)];
        _attackerIsA = _rand.Next(2) == 0;
        bool prankIsA = _attackerIsA;
        bool victimIsA = !prankIsA;
        double groundY = fightY + FootOffset;
        int dir = prankIsA ? 1 : -1;

        switch (_skitKind)
        {
            case "door":
            {
                _skitDur = 3.4;
                var door = this.FindControl<Canvas>("Door");
                if (door != null) { door.IsVisible = true; door.RenderTransform = null; Canvas.SetLeft(door, midX - 30); Canvas.SetTop(door, fightY - 20); }
                break;
            }
            case "bulb":
            {
                _skitDur = 4.0;
                var ladder = this.FindControl<Canvas>("Ladder");
                var bulb = this.FindControl<Canvas>("Bulb");
                if (ladder != null) { ladder.IsVisible = true; Canvas.SetLeft(ladder, midX - 25); Canvas.SetTop(ladder, fightY); }
                if (bulb != null) { bulb.IsVisible = true; Canvas.SetLeft(bulb, midX - 13); Canvas.SetTop(bulb, fightY - 62); }
                break;
            }
            case "logtangle":
            {
                _skitDur = 5.8;
                SetX(victimIsA, midX - 15);
                SetY(victimIsA, fightY);
                var cv = _fightCanvasCached ??= this.FindControl<Canvas>("FightCanvas");
                if (cv != null) BuildLogStrands(cv, midX, fightY);
                break;
            }
            case "banana":
            {
                _skitDur = 3.8;
                var peel = this.FindControl<Avalonia.Controls.Shapes.Path>("BananaPeel");
                if (peel != null) { peel.IsVisible = true; Canvas.SetLeft(peel, midX + dir * 40); Canvas.SetTop(peel, groundY - 16); }
                break;
            }
            case "tunnel":
            {
                _skitDur = 4.4;
                var tun = this.FindControl<Canvas>("FakeTunnel");
                if (tun != null) { tun.IsVisible = true; Canvas.SetLeft(tun, midX + dir * 150 - 48); Canvas.SetTop(tun, fightY - 4); }
                break;
            }
            case "piano":
            {
                _skitDur = 3.8;
                var piano = this.FindControl<Canvas>("Piano");
                if (piano != null) { piano.IsVisible = false; Canvas.SetLeft(piano, midX - 33); Canvas.SetTop(piano, -120); }
                break;
            }
            case "buzzer":
                _skitDur = 3.6;
                break;
            case "tug":
            {
                _skitDur = 4.6;
                var rope = this.FindControl<Avalonia.Controls.Shapes.Path>("TugRope");
                if (rope != null) { rope.IsVisible = true; Canvas.SetLeft(rope, 0); Canvas.SetTop(rope, 0); }
                break;
            }
            case "snooze":
                _skitDur = 4.8;
                break;
        }
        RuntimeLog.Info("EasterEgg", $"Skit: {_skitKind}");
    }

    private void AdvanceSkit(double now, double midX, double fightY, double w, double h)
    {
        double t = Math.Clamp((now - _skitStart) / _skitDur, 0, 1);
        bool prankIsA = _attackerIsA;
        bool victimIsA = !prankIsA;
        double propX = midX;
        double groundY = fightY + FootOffset;
        int dir = prankIsA ? 1 : -1;

        if (_skitKind == "door")
        {
            LerpX(prankIsA, propX - 5, 0.2);
            if (t < 0.5)
            {
                double vTarget = propX + dir * 55;
                LerpX(victimIsA, vTarget, 0.06);
                if (_skitPhase == 0 && t > 0.18) { _skitPhase = 1; ShowTaunt("Where'd he go?", GetX(victimIsA) + 15, fightY - 26); _tauntUntil = now + 1.3; }
            }
            else if (!_hitResolved)
            {
                _hitResolved = true;
                var door = this.FindControl<Canvas>("Door");
                if (door != null) { door.RenderTransformOrigin = new Avalonia.RelativePoint(0, 0.5, Avalonia.RelativeUnit.Relative); door.RenderTransform = new RotateTransform(prankIsA ? 72 : -72); }
                SetVX(victimIsA, 11 * dir);
                SetSquash(victimIsA, 0.5);
                ShowImpact("WHAM!", GetX(victimIsA) + 15, fightY - 6);
                _impactUntil = now + 0.5;
                KnockOut(victimIsA, now, 1.7);
            }
            else if (t > 0.6 && _skitPhase < 2)
            {
                _skitPhase = 2;
                ShowTaunt(_rand.Next(2) == 0 ? "Gotcha!" : "Peekaboo!", GetX(prankIsA) + 15, fightY - 26);
                _tauntUntil = now + 1.5;
            }
        }
        else if (_skitKind == "bulb")
        {
            LerpX(prankIsA, propX, 0.15);
            SetY(prankIsA, GetY(prankIsA) + ((fightY - 66) - GetY(prankIsA)) * 0.12);
            if (t < 0.5)
            {
                if (_skitPhase == 0 && t > 0.2) { _skitPhase = 1; ShowTaunt("Help me fix this bulb!", GetX(prankIsA) + 15, fightY - 104); _tauntUntil = now + 1.8; }
                LerpX(victimIsA, propX + dir * 48, 0.05);
            }
            else if (!_hitResolved)
            {
                _hitResolved = true;
                double vx = GetX(victimIsA);
                var zap = this.FindControl<Avalonia.Controls.Shapes.Path>("ZapBolt");
                if (zap != null) { zap.IsVisible = true; Canvas.SetLeft(zap, vx + 8); Canvas.SetTop(zap, fightY - 42); }
                var glass = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("BulbGlass");
                if (glass != null) glass.Fill = Infrastructure.ThemeResources.Brush(this, "AppWarningBrush", new SolidColorBrush(Color.Parse("#facc15")));
                ShowImpact("BZZT!", vx + 15, fightY - 22);
                _impactUntil = now + 0.6;
                KnockOut(victimIsA, now, 1.7);
            }
            else if (t > 0.72) SetVisible("ZapBolt", false);
        }
        else if (_skitKind == "logtangle")
        {
            LerpX(victimIsA, propX - 15, 0.25);
            SetY(victimIsA, fightY);
            if (!_hitResolved) SetStance(victimIsA, "flail");

            LerpX(prankIsA, propX + dir * 82, 0.09);
            AdvanceLogStrands(t, GetX(victimIsA), fightY);

            if (_skitPhase == 0 && t > 0.06)
            { _skitPhase = 1; ShowTaunt("HELP! I'm buried in the logs!", GetX(victimIsA) + 15, fightY - 32); _tauntUntil = now + 2.4; }
            else if (_skitPhase == 1 && t > 0.34)
            { _skitPhase = 2; ShowTaunt("So much green text!!", GetX(victimIsA) + 15, fightY - 32); _tauntUntil = now + 2.0; }
            else if (_skitPhase == 2 && t > 0.56)
            { _skitPhase = 3; SetStance(prankIsA, "reach"); ShowTaunt("Hold still - untangling!", GetX(prankIsA) + 15, fightY - 26); _tauntUntil = now + 2.0; }
            else if (_skitPhase == 3 && !_hitResolved && t > 0.88)
            {
                _hitResolved = true;
                SetStance(victimIsA, ""); SetStance(prankIsA, "");
                Launch(victimIsA, -14, fightY);
                ShowImpact("FREED!", GetX(victimIsA) + 15, fightY - 12);
                _impactUntil = now + 0.6;
            }
            else if (_hitResolved && _skitPhase < 4 && t > 0.94)
            { _skitPhase = 4; ShowTaunt("Never trust a log.", GetX(victimIsA) + 15, fightY - 26); _tauntUntil = now + 1.5; }
        }
        else if (_skitKind == "banana")
        {
            if (t < 0.5)
            {
                LerpX(prankIsA, propX + dir * 130, 0.08);
                LerpX(victimIsA, propX + dir * 40, 0.05);
                if (_skitPhase == 0 && t > 0.12) { _skitPhase = 1; ShowTaunt("Watch this...", GetX(prankIsA) + 15, fightY - 26); _tauntUntil = now + 1.4; }
            }
            else if (!_hitResolved)
            {
                _hitResolved = true;
                SetVX(victimIsA, -9 * dir);
                Launch(victimIsA, -17, fightY);
                SetSquash(victimIsA, 0.5);
                SetVisible("BananaPeel", false);
                ShowImpact("WHOOP!", GetX(victimIsA) + 15, fightY - 6);
                _impactUntil = now + 0.6;
                KnockOut(victimIsA, now, 1.9);
            }
            else if (t > 0.74 && _skitPhase < 2)
            { _skitPhase = 2; ShowTaunt("Classic.", GetX(prankIsA) + 15, fightY - 26); _tauntUntil = now + 1.5; }
        }
        else if (_skitKind == "tunnel")
        {
            double tunX = propX + dir * 150;
            if (t < 0.24)
            {
                LerpX(prankIsA, tunX - dir * 60, 0.12);
                if (_skitPhase == 0 && t > 0.1) { _skitPhase = 1; ShowTaunt("New shortcut! Try it!", GetX(prankIsA) + 15, fightY - 26); _tauntUntil = now + 1.6; }
            }
            else if (t < 0.58)
            {
                LerpX(prankIsA, tunX - dir * 110, 0.15);
                LerpX(victimIsA, tunX - dir * 34, 0.11);
                SetStance(victimIsA, "reach");
            }
            else if (!_hitResolved)
            {
                _hitResolved = true;
                SetStance(victimIsA, "");
                SetVX(victimIsA, -7 * dir);
                ShowImpact("SPLAT!", GetX(victimIsA) + 15, fightY);
                _impactUntil = now + 0.7;
                Shake(now, 0.3, 5);
                KnockOut(victimIsA, now, 1.8);
            }
            else
            {
                if (t < 0.74) SetSquash(victimIsA, 0.72);
                if (t > 0.66 && t < 0.86)
                {
                    LerpX(prankIsA, tunX, 0.2);
                    SetFighterOpacity(prankIsA, Math.Max(0, 1 - (t - 0.66) / 0.14));
                }
                else if (t >= 0.86)
                {
                    SetFighterOpacity(prankIsA, 1);
                    if (_skitPhase < 2)
                    {
                        _skitPhase = 2;
                        SetX(prankIsA, propX - dir * 40);
                        ShowTaunt("It only works for me.", GetX(prankIsA) + 15, fightY - 26);
                        _tauntUntil = now + 1.6;
                    }
                }
            }
        }
        else if (_skitKind == "piano")
        {
            if (t < 0.34)
            {
                LerpX(victimIsA, propX, 0.10);
                LerpX(prankIsA, propX + dir * 140, 0.10);
                if (_skitPhase == 0 && t > 0.08) { _skitPhase = 1; ShowTaunt("Stand right there!", GetX(prankIsA) + 15, fightY - 26); _tauntUntil = now + 1.6; }
            }
            else
            {
                var piano = this.FindControl<Canvas>("Piano");
                double landY = fightY - 28;
                double fall = Math.Clamp((t - 0.34) / 0.20, 0, 1);
                double py = -120 + (landY + 120) * (fall * fall);
                if (piano != null) { piano.IsVisible = true; Canvas.SetLeft(piano, GetX(victimIsA) - 33); Canvas.SetTop(piano, py); }

                if (!_hitResolved && fall >= 1.0)
                {
                    _hitResolved = true;
                    ShowImpact("CRUNCH!", GetX(victimIsA) + 15, fightY - 12);
                    _impactUntil = now + 0.8;
                    Shake(now, 0.6, 12);
                    Shockwave(now, GetX(victimIsA) + 15, groundY);
                    PuffDust(victimIsA ? "DustA" : "DustB", GetX(victimIsA) + 15, groundY);
                    KnockOut(victimIsA, now, 2.4);
                }
                if (_hitResolved)
                {
                    SetSquash(victimIsA, 0.8);
                    if (_skitPhase < 2 && t > 0.74)
                    { _skitPhase = 2; ShowTaunt("Where'd that come from?", GetX(prankIsA) + 15, fightY - 26); _tauntUntil = now + 1.8; }
                }
            }
        }
        else if (_skitKind == "buzzer")
        {
            LerpX(prankIsA, propX - dir * 42, 0.08);
            LerpX(victimIsA, propX + dir * 42, 0.08);
            if (!_hitResolved) { SetStance(prankIsA, "reach"); SetStance(victimIsA, "reach"); }

            if (t < 0.45)
            {
                if (_skitPhase == 0 && t > 0.12) { _skitPhase = 1; ShowTaunt("Truce? Shake on it.", GetX(prankIsA) + 15, fightY - 26); _tauntUntil = now + 1.8; }
            }
            else if (!_hitResolved)
            {
                _hitResolved = true;
                var zap = this.FindControl<Avalonia.Controls.Shapes.Path>("ZapBolt");
                if (zap != null) { zap.IsVisible = true; Canvas.SetLeft(zap, propX + 2); Canvas.SetTop(zap, fightY + 22); }
                ShowImpact("BZZZT!", propX + 10, fightY - 4);
                _impactUntil = now + 0.7;
                _shakeUntil = now + 0.6; _shakeIsA = victimIsA;
                KnockOut(victimIsA, now, 1.9);
            }
            else if (t > 0.7)
            {
                SetVisible("ZapBolt", false);
                SetStance(prankIsA, "");
                if (_skitPhase < 2) { _skitPhase = 2; ShowTaunt("Joy buzzer. Sorry.", GetX(prankIsA) + 15, fightY - 26); _tauntUntil = now + 1.6; }
            }
        }
        else if (_skitKind == "tug")
        {
            double spread = 62 + 92 * Math.Min(1.0, t / 0.56);
            LerpX(true, propX - spread, 0.08);
            LerpX(false, propX + spread, 0.08);

            var rope = this.FindControl<Avalonia.Controls.Shapes.Path>("TugRope");
            if (rope != null && !_hitResolved)
            {
                rope.IsVisible = true;
                double ry = fightY + 44;
                double sag = Math.Max(2, 46 * (1 - t / 0.56));
                rope.Data = Avalonia.Media.Geometry.Parse(
                    System.FormattableString.Invariant($"M {_ax + 28},{ry} Q {propX},{ry + sag} {_bx + 2},{ry}"));
                _stanceA = "pull"; _stanceB = "pull";
            }

            if (_skitPhase == 0 && t > 0.14) { _skitPhase = 1; ShowTaunt("PULL!", _ax + 15, fightY - 26); _tauntUntil = now + 1.2; }
            else if (_skitPhase == 1 && t > 0.34) { _skitPhase = 2; ShowTaunt("No, YOU pull!", _bx + 15, fightY - 26); _tauntUntil = now + 1.2; }
            else if (!_hitResolved && t >= 0.56)
            {
                _hitResolved = true;
                SetVisible("TugRope", false);
                _stanceA = ""; _stanceB = "";
                _avx = -14; _bvx = 14;
                Launch(true, -12, fightY); Launch(false, -12, fightY);
                _squashA = 0.45; _squashB = 0.45;
                ShowImpact("SNAP!", propX, fightY + 10);
                _impactUntil = now + 0.6;
                KnockOut(true, now, 1.5); KnockOut(false, now, 1.5);
            }
            else if (_hitResolved && _skitPhase < 3 && t > 0.80)
            { _skitPhase = 3; ShowTaunt("My back...", _ax + 15, fightY - 26); _tauntUntil = now + 1.5; }
        }
        else if (_skitKind == "snooze")
        {
            var zzz = this.FindControl<TextBlock>("SleepZzz");
            var doodle = this.FindControl<Avalonia.Controls.Shapes.Path>("FaceDoodle");

            if (t < 0.68)
            {
                SetStance(victimIsA, "sleep");
                if (zzz != null)
                {
                    zzz.IsVisible = true;
                    zzz.Opacity = 0.35 + 0.5 * (0.5 + 0.5 * Math.Sin(_animTime * 1.6));
                    Canvas.SetLeft(zzz, GetX(victimIsA) + 26);
                    Canvas.SetTop(zzz, GetY(victimIsA) - 26 - 8 * Math.Sin(_animTime * 1.2));
                }
            }

            if (t < 0.42)
            {
                LerpX(prankIsA, GetX(victimIsA) - dir * 46, 0.05);
                if (_skitPhase == 0 && t > 0.10) { _skitPhase = 1; ShowTaunt("shhh...", GetX(prankIsA) + 15, fightY - 26); _tauntUntil = now + 1.4; }
            }
            else if (!_hitResolved)
            {
                _hitResolved = true;
                SetStance(prankIsA, "reach");
                if (doodle != null) doodle.IsVisible = true;
                ShowTaunt("Perfect.", GetX(prankIsA) + 15, fightY - 26);
                _tauntUntil = now + 1.4;
            }
            else if (t > 0.68 && _skitPhase < 2)
            {
                _skitPhase = 2;
                SetStance(victimIsA, ""); SetStance(prankIsA, "");
                SetVisible("SleepZzz", false);
                ShowImpact("HUH?!", GetX(victimIsA) + 15, fightY - 12);
                _impactUntil = now + 0.6;
                ShowTaunt("MY FACE!", GetX(victimIsA) + 15, fightY - 26);
                _tauntUntil = now + 1.8;
                SetVX(victimIsA, (GetX(prankIsA) > GetX(victimIsA) ? 1 : -1) * 9);
            }

            if (doodle != null && doodle.IsVisible)
            {
                Canvas.SetLeft(doodle, GetX(victimIsA) + 3);
                Canvas.SetTop(doodle, GetY(victimIsA) + 8);
            }
        }
    }

    private void EndSkit(double now)
    {
        var door = this.FindControl<Canvas>("Door");
        if (door != null) door.RenderTransform = null;
        var glass = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("BulbGlass");
        if (glass != null) glass.Fill = Infrastructure.ThemeResources.Brush(this, "AppPanelBrush", new SolidColorBrush(Color.Parse("#334155")));
        foreach (var n in s_skitProps) SetVisible(n, false);
        ClearLogStrands();
        SetFighterOpacity(true, 1); SetFighterOpacity(false, 1);
        _stanceA = ""; _stanceB = "";
        _skitKind = ""; _moveKind = "";
        _nextSkitTime = now + 6 + _rand.NextDouble() * 6;
        _nextMoveTime = now + 0.5;
    }


    /// <summary>
    /// Where the green log box actually sits, expressed in FightCanvas coordinates, so the
    /// strands leave from the real control rather than a guessed screen position. Falls back to
    /// the lower-middle third of the canvas if the box has not been laid out yet.
    /// </summary>
    private Rect LogBoxRectInCanvas(Canvas canvas)
    {
        try
        {
            var box = this.FindControl<TextBox>("LiveLogTextBox");
            if (box != null && box.Bounds.Width > 20 && box.Bounds.Height > 20)
            {
                var p = box.TranslatePoint(new Point(0, 0), canvas);
                if (p.HasValue) return new Rect(p.Value, box.Bounds.Size);
            }
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

        double w = canvas.Bounds.Width > 10 ? canvas.Bounds.Width : 900;
        double h = canvas.Bounds.Height > 10 ? canvas.Bounds.Height : 600;
        return new Rect(w * 0.10, h * 0.56, w * 0.80, h * 0.26);
    }

    /// <summary>Prefers the newest real log lines; falls back to plausible encoder chatter.</summary>
    private string LogStrandText(int i)
    {
        string src;
        if (_logLines.Count > 0) src = _logLines[Math.Max(0, _logLines.Count - 1 - i)] ?? string.Empty;
        else src = s_fakeLogLines[i % s_fakeLogLines.Length];

        src = src.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (src.Length < 6) src = s_fakeLogLines[i % s_fakeLogLines.Length];
        if (src.Length > 20) src = src.Substring(0, 20).TrimEnd();
        return src;
    }

    private void BuildLogStrands(Canvas canvas, double midX, double fightY)
    {
        var host = this.FindControl<Canvas>("LogTangle");
        if (host == null) return;

        ClearLogStrands();

        _logTangleAnchorX = midX - 15;
        var box = LogBoxRectInCanvas(canvas);
        var green = Infrastructure.ThemeResources.Brush(this, "AppSuccessBrush", new SolidColorBrush(Color.Parse("#22c55e")));
        const int n = 9;

        for (int i = 0; i < n; i++)
        {
            var tb = new TextBlock
            {
                Text = LogStrandText(i),
                Foreground = green,
                FontFamily = new FontFamily("Consolas,Courier New"),
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                IsHitTestVisible = false,
                Opacity = 0,
                RenderTransformOrigin = Avalonia.RelativePoint.Center
            };
            host.Children.Add(tb);
            _logStrands.Add(tb);

            double bw = Math.Max(60, box.Width - 80);
            double bh = Math.Max(20, box.Height * 0.55);
            var from = new Point(box.X + 30 + _rand.NextDouble() * bw, box.Y + 8 + _rand.NextDouble() * bh);

            double ang = (i / (double)n) * Math.PI * 2.0;
            var to = new Point(_logTangleAnchorX + 15 + Math.Cos(ang) * 22,
                               fightY + 22 + (i - (n - 1) / 2.0) * 11);

            _logStrandPlan.Add(new LogStrandPlan
            {
                From = from,
                To = to,
                Rot = -30 + _rand.NextDouble() * 60,
                EndScale = 1.15 + _rand.NextDouble() * 0.75,
                Delay = i * 0.022
            });
        }

        host.IsVisible = true;
    }

    private void AdvanceLogStrands(double t, double victimX, double fightY)
    {
        var host = this.FindControl<Canvas>("LogTangle");
        if (host == null || _logStrands.Count == 0) return;
        host.IsVisible = true;

        double drift = victimX - _logTangleAnchorX;

        for (int i = 0; i < _logStrands.Count && i < _logStrandPlan.Count; i++)
        {
            var tb = _logStrands[i];
            var pl = _logStrandPlan[i];

            double pIn = Math.Clamp((t - pl.Delay) / 0.24, 0, 1);
            double outStart = 0.60 + i * 0.030;
            double pOut = Math.Clamp((t - outStart) / 0.20, 0, 1);

            double fwd = 1 - Math.Pow(1 - pIn, 3);
            double back = pOut * pOut;
            double u = fwd * (1 - back);

            double tx = pl.To.X + drift;
            double px = pl.From.X + (tx - pl.From.X) * u;
            double py = pl.From.Y + (pl.To.Y - pl.From.Y) * u;

            double scale = 0.40 + (pl.EndScale - 0.40) * u;
            double fontSize = 11 * scale;
            double wobble = Math.Sin(_animTime * 3.0 + i) * 5.0 * u;

            tb.FontSize = fontSize;
            tb.Opacity = 0.18 + 0.82 * u;
            tb.RenderTransform = new RotateTransform(pl.Rot * u + wobble);

            double approxW = Math.Max(1, tb.Text?.Length ?? 1) * fontSize * 0.60;
            Canvas.SetLeft(tb, px - approxW / 2.0);
            Canvas.SetTop(tb, py - fontSize / 2.0);
        }
    }

    private void ClearLogStrands()
    {
        var host = this.FindControl<Canvas>("LogTangle");
        if (host != null) { host.Children.Clear(); host.IsVisible = false; }
        _logStrands.Clear();
        _logStrandPlan.Clear();
    }


    private void KnockOut(bool isA, double now, double holdSec)
    {
        if (isA) { _koA = true; _koStartA = now; _koUntilA = now + holdSec; }
        else { _koB = true; _koStartB = now; _koUntilB = now + holdSec; }
    }

    private void RenderKO(double now, bool isA)
    {
        bool ko = isA ? _koA : _koB;
        var stars = this.FindControl<TextBlock>(isA ? "StarsA" : "StarsB");

        if (!ko)
        {
            if (isA) _rotA = 0; else _rotB = 0;
            return;
        }

        double koStart = isA ? _koStartA : _koStartB;
        double koUntil = isA ? _koUntilA : _koUntilB;
        if (now >= koUntil)
        {
            if (isA) { _koA = false; _rotA = 0; } else { _koB = false; _rotB = 0; }
            if (stars != null) stars.IsVisible = false;
            return;
        }

        double fall = Math.Clamp((now - koStart) / 0.7, 0, 1);
        double ang = 82 * fall * (isA ? -1 : 1);
        if (isA) _rotA = ang; else _rotB = ang;

        if (stars != null)
        {
            stars.IsVisible = true;
            double hx = GetX(isA) + 15;
            double hy = GetY(isA) + 30;
            Canvas.SetLeft(stars, hx - 28);
            Canvas.SetTop(stars, hy - 40);
            stars.RenderTransformOrigin = new Avalonia.RelativePoint(0.5, 0.5, Avalonia.RelativeUnit.Relative);
            stars.RenderTransform = new RotateTransform((now * 200) % 360);
        }
    }

    /// <summary>
    /// IDEA_3. One transform per fighter per frame, combining power-up scale, impact squash,
    /// airborne stretch and knockout rotation. These used to be three separate writes to
    /// RenderTransform that overwrote each other; whichever ran last was the only one you saw.
    /// The origin is the point between the feet, so squashing pushes him down into the floor
    /// instead of sinking him through it.
    /// </summary>
    private void ApplyFighterTransform(Canvas fighter, bool isA, double now)
    {
        double baseScale = isA ? _scaleA : _scaleB;
        if (_isBossFight && !isA) baseScale = 3.0;

        double squash = Math.Clamp(isA ? _squashA : _squashB, 0, 0.85);
        double vy = isA ? _avy : _bvy;
        bool air = isA ? _airA : _airB;
        double stretch = air ? Math.Clamp(-vy / 46.0, -0.22, 0.30) : 0;

        double sx = baseScale * (1 + squash * 0.90 - stretch * 0.55);
        double sy = baseScale * (1 - squash * 0.80 + stretch);
        double rot = isA ? _rotA : _rotB;

        if (Math.Abs(sx - 1) < 0.002 && Math.Abs(sy - 1) < 0.002 && Math.Abs(rot) < 0.01)
        {
            if (fighter.RenderTransform != null) fighter.RenderTransform = null;
            return;
        }

        fighter.RenderTransformOrigin = new Avalonia.RelativePoint(15, FootOffset, Avalonia.RelativeUnit.Absolute);
        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(Math.Max(0.05, sx), Math.Max(0.05, sy)));
        if (Math.Abs(rot) > 0.01) group.Children.Add(new RotateTransform(rot));
        fighter.RenderTransform = group;
    }

    private void Shockwave(double now, double x, double y)
    {
        var ring = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("ShockRing");
        if (ring == null) return;
        ring.IsVisible = true;
        Task.Run(async () =>
        {
            for (double s = 10; s < 220; s += 22)
            {
                double sz = s; double op = Math.Max(0, 0.7 - s / 300.0);
                await Task.Delay(20);
                Dispatcher.UIThread.Post(() =>
                {
                    ring.Width = sz; ring.Height = sz * 0.4; ring.Opacity = op;
                    Canvas.SetLeft(ring, x - sz / 2); Canvas.SetTop(ring, y - sz * 0.2);
                });
            }
            Dispatcher.UIThread.Post(() => { ring.IsVisible = false; ring.Opacity = 0; });
        });
    }

    private void ResolveHit(double now, double fightY)
    {
        double defX = _attackerIsA ? _bx : _ax;
        int dir = _attackerIsA ? 1 : -1;
        var defPos = new Point(defX + 15, fightY + 10);
        bool defIsA = !_attackerIsA;

        if (_defReaction == "dodge")
        {
            if (_attackerIsA) _bvy = -14; else _avy = -14;
            if (_attackerIsA) _bvx = 3.0; else _avx = -3.0;
            ShowImpact(_rand.Next(2) == 0 ? "MISS!" : "WHIFF!", defPos.X, defPos.Y - 10);
            _impactUntil = now + 0.3;
            return;
        }
        if (_defReaction == "block")
        {
            ShowImpact("BLOCK!", defPos.X, defPos.Y - 10);
            _impactUntil = now + 0.3;
            SetSquash(defIsA, 0.18);
            if (_attackerIsA) _bvx = 1.5; else _avx = -1.5;
            return;
        }

        _shakeUntil = now + 0.24; _shakeIsA = defIsA;
        ShowImpact(s_impacts[_rand.Next(s_impacts.Length)], defPos.X, defPos.Y - 12);
        _impactUntil = now + 0.34;
        SetSquash(defIsA, 0.38);
        SetSquash(_attackerIsA, 0.16);

        switch (_moveKind)
        {
            case "uppercut":
                if (_attackerIsA) _bvy = -22; else _avy = -22;
                if (_attackerIsA) { _airB = true; _tgtBy = fightY; } else { _airA = true; _tgtAy = fightY; }
                break;
            case "kick":
                if (_attackerIsA) _bvx = 9 * dir; else _avx = 9 * dir;
                break;
            case "dash":
                if (_attackerIsA) { _bvx = 7 * dir; _avx = -4 * dir; } else { _avx = 7 * dir; _bvx = -4 * dir; }
                break;
            case "super":
                DoSuper(now, dir, fightY);
                break;
            default:
                if (_attackerIsA) _bvx = 4 * dir; else _avx = 4 * dir;
                break;
        }
    }

    private void DoSuper(double now, int dir, double fightY)
    {
        var flash = this.FindControl<Avalonia.Controls.Shapes.Rectangle>("SuperFlash");
        if (flash != null) { flash.IsVisible = true; flash.Opacity = 0.5; _superUntil = now + 0.35;
            Task.Run(async () => { for (double o = 0.5; o > 0; o -= 0.06) { await Task.Delay(24); double oo = o; Dispatcher.UIThread.Post(() => flash.Opacity = oo); } }); }
        ShowTitle("SUPER!", (_ax + _bx) / 2 + 15, fightY - 70, "AppWarningBrush");
        _titleUntil = now + 1.0;
        _shakeUntil = now + 0.4;
        Shake(now, 0.35, 6);
        SetSquash(!_attackerIsA, 0.6);
        if (_attackerIsA) { _bvx = 13 * dir; _bvy = -12; _airB = true; _tgtBy = fightY; }
        else { _avx = 13 * dir; _avy = -12; _airA = true; _tgtAy = fightY; }
    }

    private void RandomizeProjectile()
    {
        var proj = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("Projectile");
        if (proj == null) return;
        double sz = 12 + _rand.NextDouble() * 12;
        proj.Width = sz; proj.Height = sz;
        proj.Fill = Infrastructure.ThemeResources.Brush(this, s_projBrush[_rand.Next(s_projBrush.Length)], new SolidColorBrush(Color.Parse("#facc15")));
    }

    private void MoveProjectile(string name, Point s, Point e, double t, double arc, double yOff)
    {
        var proj = this.FindControl<Avalonia.Controls.Shapes.Ellipse>(name);
        if (proj == null) return;
        if (t <= 0 || t >= 1) { proj.IsVisible = false; return; }
        double x = s.X + (e.X - s.X) * t;
        double y = s.Y + (e.Y - s.Y) * t - Math.Sin(t * Math.PI) * arc + yOff;
        proj.IsVisible = true;
        Canvas.SetLeft(proj, x - proj.Width / 2);
        Canvas.SetTop(proj, y - proj.Height / 2);
    }

    private void RenderFinisher(double now, Canvas fA, Canvas fB, double baseAx, double baseBx, double fightY, double midX)
    {
        var loser = _loserIsA ? fA : fB;
        var winner = _loserIsA ? fB : fA;
        double kx = (_loserIsA ? baseAx : baseBx) + (_loserIsA ? -40 : 40);
        Canvas.SetLeft(loser, kx);
        Canvas.SetTop(loser, fightY + 40);
        loser.RenderTransformOrigin = new Avalonia.RelativePoint(15, FootOffset, Avalonia.RelativeUnit.Absolute);
        loser.RenderTransform = new RotateTransform(_loserIsA ? -70 : 70);
        if (winner.RenderTransform is TransformGroup) winner.RenderTransform = null;

        var canvas = _fightCanvasCached;
        if (canvas != null)
        {
            var (mx, fy, w, h) = FightAnchor(canvas);
            DrawGround(canvas, w, fy + FootOffset);
            SetVisible(_loserIsA ? "ShadowA" : "ShadowB", false);
            DrawOneShadow(_loserIsA ? "ShadowB" : "ShadowA",
                          _loserIsA ? _bx : _ax, _loserIsA ? _by : _ay,
                          fy, fy + FootOffset,
                          _isBossFight && _loserIsA ? 3.0 : 1.0);
        }

        if (!_finisherDone)
        {
            _finisherDone = true;
            _titleUntil = now + 3.0;
            SetVisible("UltBeam", false);
            SetHealth(_loserIsA ? "HealthBarA" : "HealthBarB", 0);
            ShowTitle(_isBossFight ? "BOSS DOWN!" : "VICTORY!", midX, fightY - 70, "AppSuccessBrush");
            double wx = (_loserIsA ? baseBx : baseAx);
            ShowImpact("GG", wx + 15, fightY - 30);
            _impactUntil = now + 3.0;
        }
    }


    /// <summary>
    /// IDEA_2: <paramref name="facing"/> is +1 for "looking right" and -1 for "looking left".
    /// Every limb that used to be hard-wired to the fighter's identity (A always punched right,
    /// B always punched left, even after they had run past each other) now derives from it,
    /// and the eye dot is moved to the side he is looking at, which is what actually sells it.
    /// </summary>
    private void UpdateFighterPoses(string prefix, bool animate, bool striking, int facing, string stance)
    {
        var head = this.FindControl<Avalonia.Controls.Shapes.Ellipse>($"{prefix}_Head");
        var eye = this.FindControl<Avalonia.Controls.Shapes.Ellipse>($"{prefix}_Eye");
        var body = this.FindControl<Avalonia.Controls.Shapes.Line>($"{prefix}_Body");
        var larm = this.FindControl<Avalonia.Controls.Shapes.Line>($"{prefix}_LArm");
        var rarm = this.FindControl<Avalonia.Controls.Shapes.Line>($"{prefix}_RArm");
        var lleg = this.FindControl<Avalonia.Controls.Shapes.Line>($"{prefix}_LLeg");
        var rleg = this.FindControl<Avalonia.Controls.Shapes.Line>($"{prefix}_RLeg");
        if (head == null || body == null || larm == null || rarm == null || lleg == null || rleg == null) return;

        int dir = facing >= 0 ? 1 : -1;

        Canvas.SetLeft(head, 0); Canvas.SetTop(head, 0);
        if (eye != null)
        {
            eye.IsVisible = true;
            Canvas.SetLeft(eye, 15 + dir * 6 - 3);
            Canvas.SetTop(eye, stance == "sleep" ? 13 : 11);
        }
        body.StartPoint = new Point(15, 30);
        body.EndPoint = new Point(15, 80);

        if (stance == "reach")
        {
            larm.StartPoint = new Point(15, 40); larm.EndPoint = new Point(15 + dir * 36, 46);
            rarm.StartPoint = new Point(15, 44); rarm.EndPoint = new Point(15 + dir * 30, 54);
            lleg.StartPoint = new Point(15, 80); lleg.EndPoint = new Point(15 - dir * 12, 120);
            rleg.StartPoint = new Point(15, 80); rleg.EndPoint = new Point(15 + dir * 16, 120);
        }
        else if (stance == "pull")
        {
            larm.StartPoint = new Point(15, 40); larm.EndPoint = new Point(15 + dir * 30, 52);
            rarm.StartPoint = new Point(15, 46); rarm.EndPoint = new Point(15 + dir * 22, 60);
            lleg.StartPoint = new Point(15, 80); lleg.EndPoint = new Point(15 - dir * 30, 120);
            rleg.StartPoint = new Point(15, 80); rleg.EndPoint = new Point(15 + dir * 6, 120);
        }
        else if (stance == "sleep")
        {
            larm.StartPoint = new Point(15, 42); larm.EndPoint = new Point(15 - 9, 74);
            rarm.StartPoint = new Point(15, 42); rarm.EndPoint = new Point(15 + 9, 74);
            lleg.StartPoint = new Point(15, 80); lleg.EndPoint = new Point(15 - 9, 120);
            rleg.StartPoint = new Point(15, 80); rleg.EndPoint = new Point(15 + 9, 120);
        }
        else if (stance == "flail")
        {
            double f = _animTime * 6.0;
            larm.StartPoint = new Point(15, 40); larm.EndPoint = new Point(15 + Math.Sin(f) * 30, 30 + Math.Cos(f) * 16);
            rarm.StartPoint = new Point(15, 40); rarm.EndPoint = new Point(15 - Math.Sin(f * 1.3) * 30, 30 - Math.Cos(f) * 16);
            lleg.StartPoint = new Point(15, 80); lleg.EndPoint = new Point(15 - 10, 120);
            rleg.StartPoint = new Point(15, 80); rleg.EndPoint = new Point(15 + 10, 120);
        }
        else if (striking)
        {
            larm.StartPoint = new Point(15, 42); larm.EndPoint = new Point(15 + dir * 42, 40);
            rarm.StartPoint = new Point(15, 42); rarm.EndPoint = new Point(15 - dir * 14, 56);
            lleg.StartPoint = new Point(15, 80); lleg.EndPoint = new Point(15 + dir * 22, 120);
            rleg.StartPoint = new Point(15, 80); rleg.EndPoint = new Point(15 - dir * 8, 120);
        }
        else if (animate)
        {
            larm.StartPoint = new Point(15, 40); larm.EndPoint = new Point(15 + Math.Sin(_animTime * 2) * 26 * dir, 40 + Math.Cos(_animTime * 2) * 22);
            rarm.StartPoint = new Point(15, 40); rarm.EndPoint = new Point(15 - Math.Sin(_animTime * 2) * 26 * dir, 40 - Math.Cos(_animTime * 2) * 22);
            lleg.StartPoint = new Point(15, 80); lleg.EndPoint = new Point(15 + Math.Sin(_animTime * 2) * 16, 120);
            rleg.StartPoint = new Point(15, 80); rleg.EndPoint = new Point(15 - Math.Sin(_animTime * 2) * 16, 120);
        }
        else
        {
            larm.StartPoint = new Point(15, 40); larm.EndPoint = new Point(-10, 60);
            rarm.StartPoint = new Point(15, 40); rarm.EndPoint = new Point(40, 60);
            lleg.StartPoint = new Point(15, 80); lleg.EndPoint = new Point(0, 120);
            rleg.StartPoint = new Point(15, 80); rleg.EndPoint = new Point(30, 120);
        }
    }

    private void SetVisible(string name, bool visible)
    {
        var c = this.FindControl<Control>(name);
        if (c != null) c.IsVisible = visible;
    }

    private void ShowImpact(string text, double x, double y)
    {
        var t = this.FindControl<TextBlock>("ImpactBurst");
        if (t == null) return;
        t.Text = text;
        t.IsVisible = true;
        Canvas.SetLeft(t, x - 20);
        Canvas.SetTop(t, y - 30);
    }

    private void ShowTitle(string text, double midX, double y, string brushKey)
    {
        var t = this.FindControl<TextBlock>("TitleFlash");
        if (t == null) return;
        t.Text = text;
        t.Foreground = Infrastructure.ThemeResources.Brush(this, brushKey, new SolidColorBrush(Color.Parse("#ffffff")));
        t.IsVisible = true;
        Canvas.SetLeft(t, midX - text.Length * 18);
        Canvas.SetTop(t, y);
    }

    /// <summary>
    /// IDEA_5 made the taunts much longer ("GPU 91% - he's cooking!"), so the bubble is no
    /// longer a fixed 130px box that the text spilled out of - it is sized to the string.
    /// </summary>
    private void ShowTaunt(string text, double x, double y)
    {
        text ??= string.Empty;
        var bubble = this.FindControl<Avalonia.Controls.Shapes.Path>("ComicBubble");
        var t = this.FindControl<TextBlock>("ComicText");
        double bw = Math.Clamp(text.Length * 8.4 + 22, 78, 320);

        if (bubble != null)
        {
            bubble.IsVisible = true;
            bubble.Data = Avalonia.Media.Geometry.Parse(System.FormattableString.Invariant(
                $"M 0,0 L -14,20 L 10,16 L {bw},16 L {bw},-34 L 10,-34 Z"));
            Canvas.SetLeft(bubble, x);
            Canvas.SetTop(bubble, y);
        }
        if (t != null)
        {
            t.Text = text;
            t.IsVisible = true;
            Canvas.SetLeft(t, x + 12);
            Canvas.SetTop(t, y - 30);
        }
    }

    private void PuffDust(string name, double x, double y)
    {
        var d = this.FindControl<Avalonia.Controls.Shapes.Ellipse>(name);
        if (d == null) return;
        d.IsVisible = true;
        d.Opacity = 0.55;
        Canvas.SetLeft(d, x - 30);
        Canvas.SetTop(d, y - 9);
        Task.Run(async () =>
        {
            for (double op = 0.55; op > 0; op -= 0.06)
            {
                await Task.Delay(28);
                double o = op;
                Dispatcher.UIThread.Post(() => { d.Opacity = o; });
            }
            Dispatcher.UIThread.Post(() => { d.IsVisible = false; });
        });
    }

    public void HandleTerminalState(string state)
    {
        _lastTerminalState = state;

        if (!_easterEggActive) return;
        _easterEggActive = false;
        DetachFightKeyHandler();

        switch (state)
        {
            case "SUCCESS":
                PlaySequence("End");
                break;
            case "CANCEL":
                _vectorState = "Cancel";
                Dispatcher.UIThread.Post(() => ShowTaunt("?!", 0, 0));
                break;
            case "ERROR":
                _vectorState = "Error";
                Dispatcher.UIThread.Post(() => { ShowTitle("K.O.", 0, 0, "AppDangerBrush"); });
                break;
        }

        var window = GetParentWindow();
        if (window != null)
        {
            window.CanResize = true;
            window.PropertyChanged -= OnWindowPropertyChanged;
        }
    }

    private void ProcessEasterEggState(int percent)
    {
        if (!_easterEggActive) return;
        _lastFightProgress = percent;
        if (percent == _lastPercent) return;
        _lastPercent = percent;

        if (percent <= 0)
        {
            PlaySequence("Entrance");
        }
        else if (percent >= 100)
        {
            HandleTerminalState("SUCCESS");
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        Task.Run(() => 
        {
            int cpu = GetCpuUsage();
            int mem = GetMemUsage();
            int gpu = GetGpuUsage();
            
            Dispatcher.UIThread.Post(() => 
            {
                _cpuHist.Add(cpu);
                if (_cpuHist.Count > 200) _cpuHist.RemoveAt(0);
                
                _memHist.Add(mem);
                if (_memHist.Count > 200) _memHist.RemoveAt(0);
                
                _gpuHist.Add(gpu);
                if (_gpuHist.Count > 200) _gpuHist.RemoveAt(0);
                
                var canvas = this.FindControl<HardwareGraphControl>("GraphCanvas");
                if (canvas != null)
                {
                    canvas.CpuData = _cpuHist;
                    canvas.MemData = _memHist;
                    canvas.GpuData = _gpuHist;
                    canvas.InvalidateVisual();
                }

                bool hasLogs = false;
                while (_pendingLogs.TryDequeue(out var msg))
                {
                    _logLines.Add(msg);
                    if (_logLines.Count > 100) _logLines.RemoveAt(0);
                    hasLogs = true;
                }
                if (hasLogs)
                {
                    var txtLog = this.FindControl<TextBox>("LiveLogTextBox");
                    if (txtLog != null)
                    {
                        txtLog.Text = string.Join("\n", _logLines) + "\n";
                        txtLog.CaretIndex = txtLog.Text.Length;
                    }
                }
            });
        });
    }

    private int GetCpuUsage()
    {
        if (GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
        {
            ulong sysIdle = ((ulong)idle.dwHighDateTime << 32) | idle.dwLowDateTime;
            ulong sysKernel = ((ulong)kernel.dwHighDateTime << 32) | kernel.dwLowDateTime;
            ulong sysUser = ((ulong)user.dwHighDateTime << 32) | user.dwLowDateTime;
            ulong sysTime = sysKernel + sysUser;
            
            if (_lastSys > 0)
            {
                ulong idlDiff = sysIdle - _lastIdle;
                ulong sysDiff = sysTime - _lastSys;
                if (sysDiff > 0)
                {
                    double dCpu = (sysDiff - idlDiff) * 100.0 / sysDiff;
                    _lastIdle = sysIdle;
                    _lastSys = sysTime;
                    return Math.Max(0, Math.Min(100, (int)dCpu));
                }
            }
            _lastIdle = sysIdle;
            _lastSys = sysTime;
        }
        return 0;
    }
    
    private int GetMemUsage()
    {
        MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
        memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        if (GlobalMemoryStatusEx(ref memStatus))
        {
            return (int)memStatus.dwMemoryLoad;
        }
        return 0;
    }
    
    private int GetGpuUsage()
    {
        return _lastGpu;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}

public class HardwareGraphControl : Control
{
    public List<int> CpuData { get; set; } = new();
    public List<int> GpuData { get; set; } = new();
    public List<int> MemData { get; set; } = new();

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#780b141d")).ToImmutable();
    private static readonly IBrush BarBackgroundBrush = new SolidColorBrush(Color.Parse("#501f3545")).ToImmutable();
    private static readonly IBrush CpuBrush = new SolidColorBrush(Color.Parse("#3498db")).ToImmutable();
    private static readonly IBrush GpuBrush = new SolidColorBrush(Color.Parse("#e74c3c")).ToImmutable();
    private static readonly IBrush MemBrush = new SolidColorBrush(Color.Parse("#2ecc71")).ToImmutable();
    private static readonly IPen SeparatorPen = new Pen(new SolidColorBrush(Color.Parse("#3C10B981")).ToImmutable(), 2).ToImmutable();
    private static readonly Typeface MetricTypeface = new("Segoe UI", FontStyle.Normal, FontWeight.Bold);

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        context.FillRectangle(BackgroundBrush, bounds);

        DrawMetric(context, CpuData, CpuBrush, "CPU", 0, bounds.Width);
        DrawMetric(context, GpuData, GpuBrush, "GPU", 60, bounds.Width);
        DrawMetric(context, MemData, MemBrush, "MEM", 120, bounds.Width);
    }

    private void DrawMetric(DrawingContext ctx, List<int> data, IBrush barBrush, string label, double yOffset, double width)
    {
        var textBrush = Brushes.White;
        var bgBarBrush = BarBackgroundBrush;

        int curVal = data.Count > 0 ? data[data.Count - 1] : 0;

        var fmtLabel = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, MetricTypeface, 12, textBrush);
        var fmtVal = new FormattedText($"{curVal}%", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, MetricTypeface, 12, barBrush);
        
        ctx.DrawText(fmtLabel, new Point(5, yOffset + 10));
        ctx.DrawText(fmtVal, new Point(5, yOffset + 25));
        
        double startX = 75;
        double stickW = 10;
        double gap = 2;
        double maxH = 45;
        
        for (int i = 0; i < data.Count; i++)
        {
            double x = startX + i * (stickW + gap);
            if (x + stickW > width)
            {
                if (x < startX) continue;
            }
            
            ctx.FillRectangle(bgBarBrush, new Rect(x, yOffset, stickW, maxH));
            
            double val = data[i];
            double fillH = Math.Max(1, (val / 100.0) * maxH);
            ctx.FillRectangle(barBrush, new Rect(x, yOffset + maxH - fillH, stickW, fillH));
        }
        
        ctx.DrawLine(SeparatorPen, new Point(0, yOffset + 55), new Point(width, yOffset + 55));
    }
    
}