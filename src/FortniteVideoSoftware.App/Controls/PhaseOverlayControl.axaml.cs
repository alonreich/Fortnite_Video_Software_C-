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
    private int _animState = 0;
    private Random _rand = new();
    
    private Border? _fighter1;
    private Border? _fighter2;
    private List<Border> _projectiles = new();
    private double _f1X, _f1Y, _f2X, _f2Y;
    private int _chaosPhase = 0; 
    private int _chaosTicks = 0;
    
    private Avalonia.Media.Imaging.Bitmap? _bmpF1;
    private Avalonia.Media.Imaging.Bitmap? _bmpF2;
    private Avalonia.Media.Imaging.Bitmap? _bmpTnt;
    
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

        DetachedFromVisualTree += (s, e) => StopOverlay();
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
            if (_smiProcess != null && !_smiProcess.HasExited)
            {
                try { _smiProcess.Kill(); } catch { }
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
            _smiProcess.BeginOutputReadLine();
        }
        catch { }
        
        var txt = this.FindControl<TextBox>("LiveLogTextBox");
        if (txt != null) txt.Text = "Backend log stream attached.\n";
        
        RuntimeLog.LogAppended -= AppendLog;
        RuntimeLog.LogAppended += AppendLog;
        _timer?.Start();

        try
        {
            var canvas = this.FindControl<Canvas>("FightCanvas");
            if (canvas != null)
            {
                canvas.Children.Clear();
                _projectiles.Clear();
                
                if (_bmpF1 == null) {
                    try {
                        string aPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
                        if (!System.IO.Directory.Exists(aPath)) aPath = @"C:\Fortnite_Video_Software - C#\src\FortniteVideoSoftware.App\Assets";
                        
                        _bmpF1 = new Avalonia.Media.Imaging.Bitmap(System.IO.Path.Combine(aPath, "fighter1.jpg"));
                        _bmpF2 = new Avalonia.Media.Imaging.Bitmap(System.IO.Path.Combine(aPath, "fighter2.jpg"));
                        _bmpTnt = new Avalonia.Media.Imaging.Bitmap(System.IO.Path.Combine(aPath, "tnt.jpg"));
                    } catch (Exception ex) {
                        RuntimeLog.Fail("UI", $"Failed to load Chaos AI tokens: {ex.Message}");
                    }
                }
                
                if (_bmpF1 != null && _bmpF2 != null)
                {
                    _fighter1 = CreateToken(_bmpF1, 64, Brushes.Cyan, "fighter");
                    _fighter2 = CreateToken(_bmpF2, 64, Brushes.OrangeRed, "fighter");
                    canvas.Children.Add(_fighter1);
                    canvas.Children.Add(_fighter2);
                    
                    _f1X = 400; _f1Y = 40;
                    _f2X = 480; _f2Y = 40;
                    
                    Canvas.SetLeft(_fighter1, _f1X);
                    Canvas.SetTop(_fighter1, _f1Y);
                    Canvas.SetLeft(_fighter2, _f2X);
                    Canvas.SetTop(_fighter2, _f2Y);
                }
                
                _chaosPhase = 0;
                _chaosTicks = 0;
            }
        }
        catch { }
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

    private Border CreateToken(Avalonia.Media.Imaging.Bitmap bmp, double size, IBrush borderBrush, string tag = "")
    {
        var img = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            ClipToBounds = true,
            Child = img,
            Tag = tag,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(2)
        };
    }
    
    private Avalonia.Controls.Shapes.Path CreateVectorProp(string svgData, IBrush fill, double size, string tag = "")
    {
        return new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse(svgData),
            Fill = fill,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Tag = tag
        };
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        var canvas = this.FindControl<Canvas>("FightCanvas");
        if (canvas == null || _fighter1 == null || _fighter2 == null) return;
        
        _chaosTicks++;
        
        if (_chaosTicks == 100) _chaosPhase = 1;
        if (_chaosTicks == 200) _chaosPhase = 2;
        if (_chaosTicks > 350 && _rand.NextDouble() < 0.02) { _chaosPhase = 0; _chaosTicks = 0; }

        if (_chaosPhase == 0)
        {
            if (_f1X > 200) { _f1X -= 4; _f2X -= 4; }
            
            _f1X += _rand.Next(-8, 9); _f1Y += _rand.Next(-8, 9);
            _f2X += _rand.Next(-8, 9); _f2Y += _rand.Next(-8, 9);
            
            if (Math.Abs(_f1X - _f2X) > 60) _f2X = _f1X + 50;
        }
        else if (_chaosPhase == 1)
        {
            _f1Y += _rand.Next(-15, 15);
            _f2Y += _rand.Next(-15, 15);
            
            if (_rand.NextDouble() < 0.15)
            {
                var chair = CreateVectorProp("M15.528 2.973a.75.75 0 0 1 .472.696v8.662a.75.75 0 0 1-.472.696l-7.25 2.9a.75.75 0 0 1-.557 0l-7.25-2.9A.75.75 0 0 1 0 12.331V3.669a.75.75 0 0 1 .471-.696L7.443.184l.01-.003.268-.108a.75.75 0 0 1 .558 0l.269.108.01.003zM10.404 2 4.25 4.461 1.846 3.5 1 3.839v.4l6.5 2.6v7.922l.5.2.5-.2V6.84l6.5-2.6v-.4l-.846-.339L8 5.961 5.596 5l6.154-2.461z", Brushes.BurlyWood, 32, "chair");
                Canvas.SetLeft(chair, _f1X);
                Canvas.SetTop(chair, _f1Y);
                canvas.Children.Add(chair);
                var chairWrapper = new Border { Child = chair, Tag = "chair" };
                Canvas.SetLeft(chairWrapper, _f1X);
                Canvas.SetTop(chairWrapper, _f1Y);
                canvas.Children.Add(chairWrapper);
                _projectiles.Add(chairWrapper);
            }
        }
        else if (_chaosPhase == 2)
        {
            _f1X += (350 - _f1X) * 0.15;
            _f1Y += (60 - _f1Y) * 0.15; 
            
            _f2X += (_f1X - _f2X) * 0.05 + _rand.Next(-30, 30);
            _f2Y += (_f1Y - _f2Y) * 0.05 + _rand.Next(-30, 30);
            
            if (_rand.NextDouble() < 0.08 && _bmpTnt != null)
            {
                var bombToken = CreateToken(_bmpTnt, _rand.Next(40, 90), Brushes.Red, "bomb");
                Canvas.SetLeft(bombToken, _f2X + _rand.Next(-100, 100));
                Canvas.SetTop(bombToken, _f2Y + _rand.Next(-100, 100));
                canvas.Children.Add(bombToken);
                _projectiles.Add(bombToken);
            }
        }

        _f1X = Math.Max(0, Math.Min(1200, _f1X));
        _f1Y = Math.Max(0, Math.Min(600, _f1Y));
        _f2X = Math.Max(0, Math.Min(1200, _f2X));
        _f2Y = Math.Max(0, Math.Min(600, _f2Y));

        if (_fighter1 != null && _fighter2 != null)
        {
            Canvas.SetLeft(_fighter1, _f1X);
            Canvas.SetTop(_fighter1, _f1Y);
            Canvas.SetLeft(_fighter2, _f2X);
            Canvas.SetTop(_fighter2, _f2Y);
        }

        for (int i = _projectiles.Count - 1; i >= 0; i--)
        {
            var p = _projectiles[i];
            double px = Canvas.GetLeft(p);
            double py = Canvas.GetTop(p);
            
            if (p.Tag as string == "chair") { px += 20; py += Math.Sin(_chaosTicks * 0.5) * 15; }
            if (p.Tag as string == "bomb") { p.Opacity -= 0.15; }
            
            Canvas.SetLeft(p, px);
            Canvas.SetTop(p, py);
            
            if (px > 1300 || p.Opacity <= 0)
            {
                canvas.Children.Remove(p);
                _projectiles.RemoveAt(i);
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
