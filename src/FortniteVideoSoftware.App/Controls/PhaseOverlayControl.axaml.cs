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
    private DispatcherTimer? _animTimer;
    private TextBlock? _fighter1;
    private TextBlock? _fighter2;
    private double _f1X = 10;
    private double _f2X = 300;
    private int _animState = 0;
    private Random _rand = new();
    private List<TextBlock> _particles = new();
    
    private List<string> _logLines = new();
    
    private List<int> _cpuHist = new();
    private List<int> _gpuHist = new();
    private List<int> _memHist = new();
    
    private ulong _lastIdle;
    private ulong _lastSys;
    private int _lastGpu;
    
    public PhaseOverlayControl()
    {
        InitializeComponent();
        
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;

        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _animTimer.Tick += OnAnimTick;
    }

    public event EventHandler? CancelRequested;

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        var cancelBtn = this.FindControl<Button>("CancelProcessButton");
        if (cancelBtn != null)
        {
            cancelBtn.Click += (s, e) => CancelRequested?.Invoke(this, EventArgs.Empty);
        }
    }
    
    private Process? _smiProcess;

    public void StartOverlay()
    {
        IsVisible = true;
        _cpuHist.Clear();
        _gpuHist.Clear();
        _memHist.Clear();
        _logLines.Clear();
        
        try
        {
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
            _smiProcess.BeginOutputReadLine();
        }
        catch { }
        
        var txt = this.FindControl<TextBox>("LiveLogTextBox");
        if (txt != null) txt.Text = "Backend log stream attached.\n";
        
        RuntimeLog.LogAppended += AppendLog;
        _timer?.Start();

        var canvas = this.FindControl<Canvas>("FightCanvas");
        if (canvas != null)
        {
            canvas.Children.Clear();
            _particles.Clear();
            _fighter1 = new TextBlock { Text = "ᕕ( ᐛ )ᕗ", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeight.Bold };
            _fighter2 = new TextBlock { Text = "(ง'̀-'́)ง", Foreground = Brushes.HotPink, FontSize = 18, FontWeight = FontWeight.Bold };
            canvas.Children.Add(_fighter1);
            canvas.Children.Add(_fighter2);
            _f1X = 10;
            _f2X = 300;
            _animState = 0;
            Canvas.SetTop(_fighter1, 2);
            Canvas.SetTop(_fighter2, 2);
        }
        _animTimer?.Start();
    }
    
    public void StopOverlay()
    {
        IsVisible = false;
        _timer?.Stop();
        _animTimer?.Stop();
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
        catch { }
    }
    public void UpdateTimeRemaining(string timeRemaining)
    {
        Dispatcher.UIThread.Post(() => 
        {
            var trText = this.FindControl<TextBlock>("TimeRemainingText");
            if (trText != null) trText.Text = timeRemaining;
        });
    }
    
    public void UpdatePhase(int phaseIndex, string title, int progress)
    {
        Dispatcher.UIThread.Post(() => 
        {
            var phaseTitle = this.FindControl<TextBlock>("PhaseTitleText");
            var phaseBar = this.FindControl<ProgressBar>("PhaseProgressBar");
            var phaseText = this.FindControl<TextBlock>("PhaseProgressText");
            
            if (phaseTitle != null) phaseTitle.Text = title;
            if (phaseBar != null) phaseBar.Value = progress;
            if (phaseText != null) phaseText.Text = $"{progress}%";
        });
    }

    private System.Collections.Concurrent.ConcurrentQueue<string> _pendingLogs = new();

    public void AppendLog(string message)
    {
        _pendingLogs.Enqueue(message);
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        var canvas = this.FindControl<Canvas>("FightCanvas");
        if (canvas == null || _fighter1 == null || _fighter2 == null) return;

        double width = canvas.Bounds.Width;
        if (width <= 0) width = 400; // Fallback before layout

        // Dynamic State Machine for unpredictable fight
        if (_animState == 0) // Approach
        {
            _f1X += _rand.Next(4, 9);
            _f2X -= _rand.Next(4, 9);
            _fighter1.Text = "ᕕ( ᐛ )ᕗ";
            _fighter2.Text = "(ง'̀-'́)ง";
            if (_f2X - _f1X < 40) _animState = 1;
        }
        else if (_animState == 1) // Clash
        {
            _f1X += _rand.Next(-15, 16);
            _f2X += _rand.Next(-15, 16);
            _fighter1.Text = _rand.NextDouble() > 0.5 ? "ᕙ(⇀‸↼‶)ᕗ" : "ᕙ(`▽´)ᕗ";
            _fighter2.Text = _rand.NextDouble() > 0.5 ? "(╯°□°）╯" : "༼ つ ◕_◕ ༽つ";
            
            if (_rand.NextDouble() > 0.7)
            {
                var hit = new TextBlock { Text = _rand.NextDouble() > 0.5 ? "💥" : "💨", Foreground = Brushes.Yellow, FontSize = 14 };
                Canvas.SetLeft(hit, (_f1X + _f2X) / 2 + _rand.Next(-20, 20));
                Canvas.SetTop(hit, _rand.Next(-5, 10));
                canvas.Children.Add(hit);
                _particles.Add(hit);
            }
            
            if (_rand.NextDouble() > 0.90) _animState = 2; // Separate
        }
        else if (_animState == 2) // Separate / Retreat
        {
            _f1X -= _rand.Next(6, 12);
            _f2X += _rand.Next(6, 12);
            _fighter1.Text = "ε=ε=┌(;￣▽￣)┘";
            _fighter2.Text = "ヽ(｀Д´)ﾉ";
            if (_f1X < 20 || _f2X > width - 50) _animState = 0;
        }

        // Clamp to prevent moving out of bounds
        _f1X = Math.Clamp(_f1X, 0, width - 40);
        _f2X = Math.Clamp(_f2X, 0, width - 40);

        Canvas.SetLeft(_fighter1, _f1X);
        Canvas.SetLeft(_fighter2, _f2X);

        // Particle fade out physics
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Opacity -= 0.15;
            Canvas.SetTop(p, Canvas.GetTop(p) - 2);
            if (p.Opacity <= 0)
            {
                canvas.Children.Remove(p);
                _particles.RemoveAt(i);
            }
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

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        
        var bounds = Bounds;
        var brushBg = new SolidColorBrush(Color.Parse("#780b141d"));
        context.FillRectangle(brushBg, bounds);
        
        DrawMetric(context, CpuData, "#3498db", "CPU", 0, bounds.Width);
        DrawMetric(context, GpuData, "#e74c3c", "GPU", 60, bounds.Width);
        DrawMetric(context, MemData, "#2ecc71", "MEM", 120, bounds.Width);
    }
    
    private void DrawMetric(DrawingContext ctx, List<int> data, string colorHex, string label, double yOffset, double width)
    {
        var textBrush = Brushes.White;
        var barBrush = new SolidColorBrush(Color.Parse(colorHex));
        var bgBarBrush = new SolidColorBrush(Color.Parse("#501f3545"));
        
        int curVal = data.Count > 0 ? data[data.Count - 1] : 0;
        
        var typeFace = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold);
        var fmtLabel = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeFace, 12, textBrush);
        var fmtVal = new FormattedText($"{curVal}%", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeFace, 12, barBrush);
        
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
        
        var sepPen = new Pen(new SolidColorBrush(Color.Parse("#3C10B981")), 2);
        ctx.DrawLine(sepPen, new Point(0, yOffset + 55), new Point(width, yOffset + 55));
    }
}
