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
        _processStopwatch.Restart();
        LockWindowAndAnchors();
        ApplyFighterGlow();
        ResetFightVisuals();
        ProcessEasterEggState(0);
    }

    private void ResetFightVisuals()
    {
        _isBossFight = _rand.NextDouble() < 0.05;
        _hypeLevel = 0;
        var bar = this.FindControl<Avalonia.Controls.ProgressBar>("HypeMeterBar");
        if (bar != null) bar.Value = 0;
        var bossLbl = this.FindControl<TextBlock>("BossLabel");
        if (bossLbl != null) bossLbl.IsVisible = _isBossFight;

        var canvas = this.FindControl<Canvas>("FightCanvas");
        if (canvas != null) canvas.IsVisible = true;
        var fA = this.FindControl<Canvas>("FighterA");
        var fB = this.FindControl<Canvas>("FighterB");
        if (fA != null) fA.RenderTransform = null;
        if (fB != null) 
        {
            if (_isBossFight)
            {
                fB.RenderTransformOrigin = new Avalonia.RelativePoint(15, 120, Avalonia.RelativeUnit.Absolute);
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
        _mushGrew = _mushLeaped = _mushSlammed = false;
        _koA = _koB = false; _skitKind = "";
        _projActive = _projDouble = false; _moveKind = ""; _hitResolved = false;
        var glassR = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("BulbGlass");
        if (glassR != null) glassR.Fill = Infrastructure.ThemeResources.Brush(this, "AppPanelBrush", new SolidColorBrush(Color.Parse("#334155")));
        foreach (var n in new[] { "Projectile", "Projectile2", "SuperFlash", "ImpactBurst", "ComicBubble", "ComicText", "TitleFlash", "DustA", "DustB", "Mushroom", "ShockRing",
                                   "Door", "Ladder", "Bulb", "ZapBolt", "StarsA", "StarsB", "LogsTrap" })
            SetVisible(n, false);
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

        IntPtr hwnd = HostWindowHandle;
        TaskbarProgress.Clear(hwnd);
        TaskbarProgress.Flash(hwnd);

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
        _hypeLevel = Math.Min(100, _hypeLevel + 10);
        var bar = this.FindControl<Avalonia.Controls.ProgressBar>("HypeMeterBar");
        if (bar != null) bar.Value = _hypeLevel;
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

    private void OnWindowPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == "WindowState" || e.Property.Name == "Bounds")
        {
            var window = sender as Avalonia.Controls.Window;
            if (window != null && (window.WindowState == Avalonia.Controls.WindowState.Maximized || window.Bounds.Width > 2000))
            {
                var canvas = this.FindControl<Canvas>("FightCanvas");
                if (canvas != null)
                {
                    canvas.IsVisible = false;
                    _easterEggActive = false;
                    RuntimeLog.Info("EasterEgg", "Window Maximization Edge Case Triggered. Easter Egg hidden.");
                }
            }
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
    // Idea 1 & 5
    private bool _isBossFight = false;
    private double _hypeLevel = 0;

    // Cache the resolved fighter controls so we don't spam FindControl 30 times a second.
    private bool _loserIsA;

    private const double EntranceDur = 1.05;
    private const double Gravity = 2.4;

    private static readonly string[] s_taunts =
        { "Encoding!", "Hold still!", "2x speed!", "Almost!", "Rendering!", "Take that!",
          "Get shorty!", "Frame by frame!", "Compressing!", "Eat pixels!", "Too slow!", "Final boss!" };
    private static readonly string[] s_impacts =
        { "POW!", "BAM!", "WHAM!", "BIFF!", "KAPOW!", "THWACK!", "CRUNCH!", "SMACK!", "BOOM!" };
    private static readonly string[] s_moves =
        { "throw", "throw", "dash", "dash", "uppercut", "kick", "feint", "leap", "hide", "super", "mushroom" };
    private static readonly string[] s_projBrush =
        { "AppWarningBrush", "AppInfoBrush", "AppAccentBrush", "AppDangerBrush", "AppSuccessBrush" };

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

    private void OnVectorAnimTick(object? sender, EventArgs e)
    {
        _animTime += 0.1;
        double now = _processStopwatch.Elapsed.TotalSeconds;

        var canvas = _fightCanvasCached ??= this.FindControl<Canvas>("FightCanvas");
        var fA = _fighterACached ??= this.FindControl<Canvas>("FighterA");
        var fB = _fighterBCached ??= this.FindControl<Canvas>("FighterB");
        if (canvas == null || fA == null || fB == null) return;
        fA.IsVisible = true; fB.IsVisible = true;

        if (_vectorState == "Melee" && _moveKind == "")
        {
            if (_hypeLevel >= 100)
            {
                _hypeLevel = 0;
                _moveKind = "super";
                _moveStart = now;
                _hitResolved = false;
                _attackerIsA = true;
                _moveDur = 0.5;
            }
            else if (_hypeLevel > 0)
            {
                _hypeLevel = Math.Max(0, _hypeLevel - 1.5);
            }
            var bar = this.FindControl<Avalonia.Controls.ProgressBar>("HypeMeterBar");
            if (bar != null) bar.Value = _hypeLevel;
        }

        var (midX, fightY, w, h) = FightAnchor(canvas);

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
            Canvas.SetLeft(fA, ex); Canvas.SetTop(fA, fightY);
            Canvas.SetLeft(fB, exB); Canvas.SetTop(fB, fightY);
            UpdateFighterPoses("A", true, false);
            UpdateFighterPoses("B", true, false);
            if (p >= 1.0 && _titleUntil == 0)
            {
                _titleUntil = now + 0.85;
                ShowTitle("FIGHT!", midX, fightY - 70, "AppOnAccentTextBrush");
                PuffDust("DustA", midX - 80, fightY + 120);
                PuffDust("DustB", midX + 50, fightY + 120);
            }
        }
        else if (_vectorState == "Melee")
        {
            if (!_posInit) { _ax = midX - 95; _bx = midX + 35; _ay = _by = fightY; _tgtAy = _tgtBy = fightY; _posInit = true; }
            TickFight(now, midX, fightY, w, h);
        }
        else
        {
            if (_vectorState == "End") RenderFinisher(now, fA, fB, _ax, _bx, fightY, midX);
        }

        if (now >= _impactUntil) SetVisible("ImpactBurst", false);
        if (now >= _titleUntil && _vectorState != "End") SetVisible("TitleFlash", false);
        if (now >= _superUntil) SetVisible("SuperFlash", false);
    }

    private void TickFight(double now, double midX, double fightY, double w, double h)
    {
        var fA = this.FindControl<Canvas>("FighterA");
        var fB = this.FindControl<Canvas>("FighterB");
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

        if (Math.Abs(_avx) < 0.2 && _moveKind is "" or "throw" or "feint")
            _ax += (restAx - _ax) * 0.04 + Math.Sin(_animTime * 0.5) * 0.6;
        if (Math.Abs(_bvx) < 0.2 && _moveKind is "" or "throw" or "feint")
            _bx += (restBx - _bx) * 0.04 + Math.Cos(_animTime * 0.5) * 0.6;

        _ax += _avx; _bx += _bvx;
        _avx *= 0.86; _bvx *= 0.86;
        if (Math.Abs(_avx) < 0.15) _avx = 0;
        if (Math.Abs(_bvx) < 0.15) _bvx = 0;

        if (_airA) { _avy += Gravity; _ay += _avy; if (_ay >= _tgtAy) { if (_avy > 8) PuffDust("DustA", _ax + 15, _tgtAy + 120); _ay = _tgtAy; _avy = 0; _airA = false; } }
        else _ay += (_tgtAy - _ay) * 0.14;
        if (_airB) { _bvy += Gravity; _by += _bvy; if (_by >= _tgtBy) { if (_bvy > 8) PuffDust("DustB", _bx + 15, _tgtBy + 120); _by = _tgtBy; _bvy = 0; _airB = false; } }
        else _by += (_tgtBy - _by) * 0.14;

        _ax = Math.Clamp(_ax, 10, w - 60); _bx = Math.Clamp(_bx, 10, w - 60);

        ApplyScale(fA, _scaleA);
        ApplyScale(fB, _scaleB);

        double sA = (now < _shakeUntil && _shakeIsA) ? Math.Sin(now * 90) * 4 : 0;
        double sB = (now < _shakeUntil && !_shakeIsA) ? Math.Sin(now * 90) * 4 : 0;
        Canvas.SetLeft(fA, _ax + sA); Canvas.SetTop(fA, _ay);
        Canvas.SetLeft(fB, _bx + sB); Canvas.SetTop(fB, _by);

        bool aStrike = _attackerIsA && _moveKind != "" && !_hitResolved;
        bool bStrike = !_attackerIsA && _moveKind != "" && !_hitResolved;
        UpdateFighterPoses("A", !_koA, aStrike && !_koA);
        UpdateFighterPoses("B", !_koB, bStrike && !_koB);

        RenderKO(now, true);
        RenderKO(now, false);

        if (now >= _nextTauntTime)
        {
            double sx = (_rand.Next(2) == 0 ? _ax : _bx);
            ShowTaunt(s_taunts[_tauntIndex % s_taunts.Length], sx + 20, fightY - 26);
            _tauntIndex = _rand.Next(s_taunts.Length);
            _tauntUntil = now + 2.6;
            _nextTauntTime = now + 3.4 + _rand.NextDouble() * 2.0;
        }
        if (now >= _tauntUntil) { SetVisible("ComicBubble", false); SetVisible("ComicText", false); }
    }

    private void StartMove(double now, double midX, double fightY, double w, double h)
    {
        if (_comboLeft == 0 && now >= _nextSkitTime && !_koA && !_koB && _rand.NextDouble() < 0.55)
        {
            StartSkit(now, midX, fightY, w, h);
            return;
        }

        if (_comboLeft > 0) { _comboLeft--; }
        else _attackerIsA = _rand.Next(2) == 0;
        if (_koA && _attackerIsA) _attackerIsA = false;
        if (_koB && !_attackerIsA) _attackerIsA = true;
        if (_koA && _koB) { _moveKind = ""; _nextMoveTime = now + 0.4; return; }

        _moveKind = s_moves[_rand.Next(s_moves.Length)];
        _moveStart = now; _hitResolved = false;

        double r = _rand.NextDouble();
        _defReaction = (_moveKind is "super" or "mushroom") ? "hit" : (r < 0.18 ? "dodge" : r < 0.33 ? "block" : "hit");

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

        double prog = Math.Clamp(_lastFightProgress / 100.0, 0, 1);
        double load = Math.Clamp(_lastGpu / 100.0, 0, 1);
        double baseGap = 0.75 - 0.4 * prog - 0.2 * load;
        if (_comboLeft > 0) baseGap = 0.14;
        else if (_rand.NextDouble() < 0.22) _comboLeft = 1 + _rand.Next(2);
        _nextMoveTime = now + Math.Max(0.12, baseGap);
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
            int dir = _attackerIsA ? 1 : -1;
            if (_attackerIsA) { _bvx = 15 * dir; _tgtBy = fightY; _airB = true; _bvy = -16; }
            else { _avx = 15 * dir; _tgtAy = fightY; _airA = true; _avy = -16; }
            Shockwave(now, impactPos.X, fightY + 120);
        }
    }

    private void StartSkit(double now, double midX, double fightY, double w, double h)
    {
        _moveKind = "skit"; _skitStart = now; _skitPhase = 0; _hitResolved = false;
        
        int skitRoll = _rand.Next(3);
        _skitKind = skitRoll == 0 ? "door" : (skitRoll == 1 ? "bulb" : "logs");
        _attackerIsA = _rand.Next(2) == 0; // for logs: attacker is the one helping, victim is the trapped one

        if (_skitKind == "door")
        {
            _skitDur = 3.4;
            var door = this.FindControl<Canvas>("Door");
            if (door != null) { door.IsVisible = true; door.RenderTransform = null; Canvas.SetLeft(door, midX - 30); Canvas.SetTop(door, fightY - 20); }
        }
        else if (_skitKind == "bulb")
        {
            _skitDur = 4.0;
            var ladder = this.FindControl<Canvas>("Ladder");
            var bulb = this.FindControl<Canvas>("Bulb");
            if (ladder != null) { ladder.IsVisible = true; Canvas.SetLeft(ladder, midX - 25); Canvas.SetTop(ladder, fightY); }
            if (bulb != null) { bulb.IsVisible = true; Canvas.SetLeft(bulb, midX - 13); Canvas.SetTop(bulb, fightY - 62); }
        }
        else if (_skitKind == "logs")
        {
            _skitDur = 5.5; // Logs take a bit longer to untie
            var logs = this.FindControl<Canvas>("LogsTrap");
            if (logs != null) { logs.IsVisible = true; Canvas.SetLeft(logs, midX - 35); Canvas.SetTop(logs, fightY + 60); }
            // The victim is trapped right away
            if (!_attackerIsA) { _tgtAy = fightY + 50; _ay = fightY + 50; _ax = midX - 15; }
            else { _tgtBy = fightY + 50; _by = fightY + 50; _bx = midX - 15; }
        }
        RuntimeLog.Info("EasterEgg", $"Skit: {_skitKind}");
    }

    private void AdvanceSkit(double now, double midX, double fightY, double w, double h)
    {
        double t = Math.Clamp((now - _skitStart) / _skitDur, 0, 1);
        bool prankIsA = _attackerIsA;
        bool victimIsA = !prankIsA;
        double propX = midX;

        if (_skitKind == "door")
        {
            if (prankIsA) _ax += (propX - 5 - _ax) * 0.2; else _bx += (propX - 5 - _bx) * 0.2;
            if (t < 0.5)
            {
                double vTarget = propX + (prankIsA ? 55 : -55);
                if (victimIsA) _ax += (vTarget - _ax) * 0.06; else _bx += (vTarget - _bx) * 0.06;
                if (_skitPhase == 0 && t > 0.18) { _skitPhase = 1; ShowTaunt("Where'd he go?", (victimIsA ? _ax : _bx) + 15, fightY - 26); _tauntUntil = now + 1.3; }
            }
            else if (!_hitResolved)
            {
                _hitResolved = true;
                var door = this.FindControl<Canvas>("Door");
                if (door != null) { door.RenderTransformOrigin = new Avalonia.RelativePoint(0, 0.5, Avalonia.RelativeUnit.Relative); door.RenderTransform = new RotateTransform(prankIsA ? 72 : -72); }
                int dir = prankIsA ? 1 : -1;
                if (victimIsA) _avx = 11 * dir; else _bvx = 11 * dir;
                ShowImpact("WHAM!", (victimIsA ? _ax : _bx) + 15, fightY - 6);
                _impactUntil = now + 0.5;
                KnockOut(victimIsA, now, 1.7);
            }
            else if (t > 0.6 && _skitPhase < 2)
            {
                _skitPhase = 2;
                ShowTaunt(_rand.Next(2) == 0 ? "Gotcha!" : "Peekaboo!", (prankIsA ? _ax : _bx) + 15, fightY - 26);
                _tauntUntil = now + 1.5;
            }
        }
        else if (_skitKind == "bulb")
        {
            if (prankIsA) { _ax += (propX - _ax) * 0.15; _ay += ((fightY - 66) - _ay) * 0.12; }
            else { _bx += (propX - _bx) * 0.15; _by += ((fightY - 66) - _by) * 0.12; }
            if (t < 0.5)
            {
                if (_skitPhase == 0 && t > 0.2) { _skitPhase = 1; ShowTaunt("Help me fix this bulb!", (prankIsA ? _ax : _bx) + 15, fightY - 104); _tauntUntil = now + 1.8; }
                double vTarget = propX + (prankIsA ? 48 : -48);
                if (victimIsA) _ax += (vTarget - _ax) * 0.05; else _bx += (vTarget - _bx) * 0.05;
            }
            else if (!_hitResolved)
            {
                _hitResolved = true;
                double vx = victimIsA ? _ax : _bx;
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
        else if (_skitKind == "logs")
        {
            // victim is trapped
            if (victimIsA) { _ay = fightY + 50; _ax += (propX - 15 - _ax) * 0.3; }
            else { _by = fightY + 50; _bx += (propX - 15 - _bx) * 0.3; }

            // prankIsA is the helper
            if (t < 0.6)
            {
                // helper runs over to help
                double helperTargetX = propX + (prankIsA ? 35 : -35);
                if (prankIsA) _ax += (helperTargetX - _ax) * 0.1; else _bx += (helperTargetX - _bx) * 0.1;

                if (_skitPhase == 0 && t > 0.05) 
                { 
                    _skitPhase = 1; 
                    ShowTaunt("HHEELPP! I am overwhelmed by all these logs!", (victimIsA ? _ax : _bx) + 15, fightY + 20); 
                    _tauntUntil = now + 2.5; 
                }
                else if (_skitPhase == 1 && t > 0.45) 
                {
                    _skitPhase = 2;
                    ShowTaunt("Hold on!", (prankIsA ? _ax : _bx) + 15, fightY - 26);
                    _tauntUntil = now + 1.2;
                }
            }
            else if (!_hitResolved)
            {
                _hitResolved = true;
                SetVisible("LogsTrap", false);
                // victim jumps up
                if (victimIsA) { _tgtAy = fightY; _airA = true; _avy = -15; }
                else { _tgtBy = fightY; _airB = true; _bvy = -15; }
                ShowImpact("FREED!", (victimIsA ? _ax : _bx) + 15, fightY + 30);
                _impactUntil = now + 0.6;
            }
            else if (t > 0.8 && _skitPhase < 3)
            {
                _skitPhase = 3;
                ShowTaunt("Thanks! Back to fighting!", (victimIsA ? _ax : _bx) + 15, fightY - 26);
                _tauntUntil = now + 1.5;
            }
        }
    }

    private void EndSkit(double now)
    {
        var door = this.FindControl<Canvas>("Door");
        if (door != null) door.RenderTransform = null;
        var glass = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("BulbGlass");
        if (glass != null) glass.Fill = Infrastructure.ThemeResources.Brush(this, "AppPanelBrush", new SolidColorBrush(Color.Parse("#334155")));
        foreach (var n in new[] { "Door", "Ladder", "Bulb", "ZapBolt", "LogsTrap" }) SetVisible(n, false);
        _skitKind = ""; _moveKind = "";
        _nextSkitTime = now + 6 + _rand.NextDouble() * 6;
        _nextMoveTime = now + 0.5;
    }

    private void KnockOut(bool isA, double now, double holdSec)
    {
        if (isA) { _koA = true; _koStartA = now; _koUntilA = now + holdSec; }
        else { _koB = true; _koStartB = now; _koUntilB = now + holdSec; }
    }

    private void RenderKO(double now, bool isA)
    {
        bool ko = isA ? _koA : _koB;
        var f = this.FindControl<Canvas>(isA ? "FighterA" : "FighterB");
        var stars = this.FindControl<TextBlock>(isA ? "StarsA" : "StarsB");
        if (f == null) return;
        if (!ko) return;

        double koStart = isA ? _koStartA : _koStartB;
        double koUntil = isA ? _koUntilA : _koUntilB;
        if (now >= koUntil)
        {
            if (isA) _koA = false; else _koB = false;
            f.RenderTransform = null;
            if (stars != null) stars.IsVisible = false;
            return;
        }

        double fall = Math.Clamp((now - koStart) / 0.7, 0, 1);
        double ang = 82 * fall * (isA ? -1 : 1);
        f.RenderTransformOrigin = new Avalonia.RelativePoint(15, 120, Avalonia.RelativeUnit.Absolute);
        f.RenderTransform = new RotateTransform(ang);
        if (stars != null)
        {
            stars.IsVisible = true;
            double hx = (isA ? _ax : _bx) + 15;
            double hy = (isA ? _ay : _by) + 30;
            Canvas.SetLeft(stars, hx - 28);
            Canvas.SetTop(stars, hy - 40);
            stars.RenderTransformOrigin = new Avalonia.RelativePoint(0.5, 0.5, Avalonia.RelativeUnit.Relative);
            stars.RenderTransform = new RotateTransform((now * 200) % 360);
        }
    }

    private void ApplyScale(Canvas fighter, double scale)
    {
        if (_isBossFight && fighter.Name == "FighterB") scale = 3.0;
        if (scale == 1.0) { if (fighter.RenderTransform is ScaleTransform) fighter.RenderTransform = null; return; }
        fighter.RenderTransformOrigin = new Avalonia.RelativePoint(15, 120, Avalonia.RelativeUnit.Absolute);
        fighter.RenderTransform = new ScaleTransform(scale, scale);
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
            if (_attackerIsA) _bvx = 1.5; else _avx = -1.5;
            return;
        }

        _shakeUntil = now + 0.24; _shakeIsA = !_attackerIsA;
        ShowImpact(s_impacts[_rand.Next(s_impacts.Length)], defPos.X, defPos.Y - 12);
        _impactUntil = now + 0.34;

        switch (_moveKind)
        {
            case "uppercut":
                if (_attackerIsA) _bvy = -22; else _avy = -22;
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
        if (_attackerIsA) { _bvx = 13 * dir; _bvy = -12; } else { _avx = 13 * dir; _avy = -12; }
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
        double kx = (_loserIsA ? baseAx : baseBx) + (_loserIsA ? -40 : 40);
        Canvas.SetLeft(loser, kx);
        Canvas.SetTop(loser, fightY + 40);
        loser.RenderTransform = new Avalonia.Media.RotateTransform(_loserIsA ? -70 : 70);
        if (!_finisherDone)
        {
            _finisherDone = true;
            _titleUntil = now + 3.0;
            ShowTitle("VICTORY!", midX, fightY - 70, "AppSuccessBrush");
            double wx = (_loserIsA ? baseBx : baseAx);
            ShowImpact("GG", wx + 15, fightY - 30);
            _impactUntil = now + 3.0;
        }
    }

    private void UpdateFighterPoses(string prefix, bool animate, bool striking)
    {
        var head = this.FindControl<Avalonia.Controls.Shapes.Ellipse>($"{prefix}_Head");
        var body = this.FindControl<Avalonia.Controls.Shapes.Line>($"{prefix}_Body");
        var larm = this.FindControl<Avalonia.Controls.Shapes.Line>($"{prefix}_LArm");
        var rarm = this.FindControl<Avalonia.Controls.Shapes.Line>($"{prefix}_RArm");
        var lleg = this.FindControl<Avalonia.Controls.Shapes.Line>($"{prefix}_LLeg");
        var rleg = this.FindControl<Avalonia.Controls.Shapes.Line>($"{prefix}_RLeg");
        if (head == null || body == null || larm == null || rarm == null || lleg == null || rleg == null) return;

        Canvas.SetLeft(head, 0); Canvas.SetTop(head, 0);
        body.StartPoint = new Point(15, 30);
        body.EndPoint = new Point(15, 80);

        if (striking)
        {
            int dir = prefix == "A" ? 1 : -1;
            larm.StartPoint = new Point(15, 42); larm.EndPoint = new Point(15 + dir * 42, 40);
            rarm.StartPoint = new Point(15, 42); rarm.EndPoint = new Point(15 - dir * 14, 56);
            lleg.StartPoint = new Point(15, 80); lleg.EndPoint = new Point(15 + dir * 22, 120);
            rleg.StartPoint = new Point(15, 80); rleg.EndPoint = new Point(15 - dir * 8, 120);
        }
        else if (animate)
        {
            larm.StartPoint = new Point(15, 40); larm.EndPoint = new Point(15 + Math.Sin(_animTime * 2) * 26, 40 + Math.Cos(_animTime * 2) * 22);
            rarm.StartPoint = new Point(15, 40); rarm.EndPoint = new Point(15 - Math.Sin(_animTime * 2) * 26, 40 - Math.Cos(_animTime * 2) * 22);
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

    private void ShowTaunt(string text, double x, double y)
    {
        var bubble = this.FindControl<Avalonia.Controls.Shapes.Path>("ComicBubble");
        var t = this.FindControl<TextBlock>("ComicText");
        if (bubble != null)
        {
            bubble.IsVisible = true;
            bubble.Data = Avalonia.Media.Geometry.Parse("M 0,0 L -14,20 L 10,16 L 130,16 L 130,-34 L 10,-34 Z");
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
        if (!_easterEggActive) return;
        _easterEggActive = false;

        switch (state)
        {
            case "SUCCESS":
                _loserIsA = !_attackerIsA;
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