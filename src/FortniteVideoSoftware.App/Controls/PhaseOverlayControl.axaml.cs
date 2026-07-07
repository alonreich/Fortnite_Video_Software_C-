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
    private StringBuilder _logBuffer = new();
    
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
    
    public void StartOverlay()
    {
        IsVisible = true;
        _cpuHist.Clear();
        _gpuHist.Clear();
        _memHist.Clear();
        _logBuffer.Clear();
        
        var txt = this.FindControl<TextBox>("LiveLogTextBox");
        if (txt != null) txt.Text = "Backend log stream attached.\n";
        
        RuntimeLog.LogAppended += AppendLog;
        _timer?.Start();
    }
    
    public void StopOverlay()
    {
        IsVisible = false;
        RuntimeLog.LogAppended -= AppendLog;
        _timer?.Stop();
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

    public void AppendLog(string message)
    {
        Dispatcher.UIThread.Post(() => 
        {
            var txt = this.FindControl<TextBox>("LiveLogTextBox");
            if (txt != null)
            {
                txt.Text += message + "\n";
                txt.CaretIndex = txt.Text.Length;
            }
        });
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
        try
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=utilization.gpu,utilization.encoder --format=csv,noheader,nounits -i 0",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            p.Start();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(1000);
            
            if (!string.IsNullOrWhiteSpace(output))
            {
                var parts = output.Trim().Split(',');
                if (parts.Length >= 2)
                {
                    int core = int.TryParse(parts[0].Trim(), out int c) ? c : 0;
                    int enc = int.TryParse(parts[1].Trim(), out int e) ? e : 0;
                    _lastGpu = Math.Max(0, Math.Min(100, Math.Max(core, enc)));
                }
            }
        }
        catch { }
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
                double offset = (i * (stickW + gap)) - (width - startX - stickW);
                x = startX + i * (stickW + gap) - offset;
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
