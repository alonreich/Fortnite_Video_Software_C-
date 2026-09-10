using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FortniteVideoSoftware.App.Infrastructure;
using FortniteVideoSoftware.App.Models;
using FortniteVideoSoftware.App.Services;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App.ViewModels;

public sealed class ExportViewModel : ViewModelBase
{
    private int _qualitySliderValue = 7;
    private string _qualityLabelText = "Standard";
    private string _qualityLabelColor = "White";
    private double? _targetMbOverride;
    private string _hardwareMode = "Auto";
    private string _hardwareStatusText = "HW: Detecting…";
    private string _hardwareStatusColor = "#00783C";
    private bool _isRdpWarningVisible;
    private bool _isRdpGpuBlocked;
    private bool _isExporting;
    private int _progressPercentage;
    private int _phaseCurrent;
    private string _phaseTitle = "";
    private int _phaseProgress;
    private string _statusText = "Ready";
    private CancellationTokenSource? _exportCts;

    public int QualitySliderValue
    {
        get => _qualitySliderValue;
        set => SetProperty(ref _qualitySliderValue, Math.Clamp(value, 0, 20));
    }

    public string QualityLabelText
    {
        get => _qualityLabelText;
        set => SetProperty(ref _qualityLabelText, value);
    }

    public string QualityLabelColor
    {
        get => _qualityLabelColor;
        set => SetProperty(ref _qualityLabelColor, value);
    }

    public double? TargetMbOverride
    {
        get => _targetMbOverride;
        set => SetProperty(ref _targetMbOverride, value);
    }

    public string HardwareMode
    {
        get => _hardwareMode;
        set => SetProperty(ref _hardwareMode, value);
    }

    public string HardwareStatusText
    {
        get => _hardwareStatusText;
        set => SetProperty(ref _hardwareStatusText, value);
    }

    public string HardwareStatusColor
    {
        get => _hardwareStatusColor;
        set => SetProperty(ref _hardwareStatusColor, value);
    }

    public bool IsRdpWarningVisible
    {
        get => _isRdpWarningVisible;
        set => SetProperty(ref _isRdpWarningVisible, value);
    }

    public bool IsRdpGpuBlocked
    {
        get => _isRdpGpuBlocked;
        set => SetProperty(ref _isRdpGpuBlocked, value);
    }

    public bool IsExporting
    {
        get => _isExporting;
        set => SetProperty(ref _isExporting, value);
    }

    public int ProgressPercentage
    {
        get => _progressPercentage;
        set => SetProperty(ref _progressPercentage, value);
    }

    public int PhaseCurrent
    {
        get => _phaseCurrent;
        set => SetProperty(ref _phaseCurrent, value);
    }

    public string PhaseTitle
    {
        get => _phaseTitle;
        set => SetProperty(ref _phaseTitle, value);
    }

    public int PhaseProgress
    {
        get => _phaseProgress;
        set => SetProperty(ref _phaseProgress, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public CancellationTokenSource? ExportCts => _exportCts;

    public void UpdateEstimatedQuality(double effectiveDurationMs, bool isPortraitMode)
    {
        if (effectiveDurationMs <= 0)
        {
            QualityLabelText = "";
            return;
        }

        int idx = QualitySliderValue;
        double targetMb = 5 + idx * 5;
        if (idx >= 20)
        {
            QualityLabelText = "Max CQ";
            QualityLabelColor = "#2ecc71";
            return;
        }

        double durSec = Math.Max(0.1, effectiveDurationMs / 1000.0) + 0.1;
        double audioKbps = 192;
        if (targetMb * 1024 < durSec * 48) audioKbps = 64;

        int w = 1920;
        int h = 1080;
        if (isPortraitMode)
        {
            w = CoordinateConstants.ContentW;
            h = CoordinateConstants.ContentH;
        }

        double videoKbps = ((targetMb * 8192.0) - (audioKbps * durSec)) / durSec;
        if (videoKbps < 100) videoKbps = 100;

        double bpp = (videoKbps * 1000.0) / (w * h * 60.0);
        if (!isPortraitMode)
        {
            bpp /= 1.5;
        }

        string desc = "Standard";
        string color = "White";

        var spectrum = new (double th, string d, string c)[]
        {
            (0.02, "Unwatchable", "#e74c3c"),
            (0.04, "Pixelated", "#e74c3c"),
            (0.06, "Blurry", "#e74c3c"),
            (0.1, "Clear", "White"),
            (0.15, "Sharp", "#2ecc71"),
            (0.25, "Crisp-Clear", "#2ecc71"),
            (99.0, "Lifelike", "#2ecc71")
        };

        for (int i = 0; i < spectrum.Length; i++)
        {
            if (bpp < spectrum[i].th)
            {
                desc = spectrum[i].d;
                color = spectrum[i].c;
                double prev = i > 0 ? spectrum[i - 1].th : 0.0;
                double mid = (spectrum[i].th + prev) / 2.0;
                if (spectrum[i].th < 90.0)
                {
                    desc += bpp < mid ? "-" : "+";
                }
                break;
            }
        }

        QualityLabelText = desc;
        QualityLabelColor = color;
    }

    public string ResolveHardwareMode()
    {
        return ExportEncoderStrategy.Resolve(
            SettingsManager.Instance.VideoEncoderOverride,
            _hardwareMode,
            ResolveFfmpegPath());
    }

    public static string ResolveFfmpegPath()
    {
        return BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
    }

    public async Task InitializeHardwareScanAsync()
    {
        try
        {
            string ffmpegExe = ResolveFfmpegPath();
            string mode = await HardwareScanner.ScanSharedAsync(ffmpegExe);
            _hardwareMode = mode;

            bool isRdpBlocked = CheckRdpGpuBlocked();
            IsRdpGpuBlocked = isRdpBlocked;
            IsRdpWarningVisible = isRdpBlocked;

            if (isRdpBlocked)
            {
                RuntimeLog.Fail("Hardware", "RDP Session detected with blocked GPU.");
                HardwareStatusText = "RDP: CPU BLOCKED";
                HardwareStatusColor = "#e74c3c";
                return;
            }

            string effective = ResolveHardwareMode();
            if (effective == HardwareScanner.ScanFailed)
            {
                RuntimeLog.Fail("Hardware", "Boot hardware scan did not complete; encoder will be re-probed at export time.");
                HardwareStatusText = "HW: Detecting…";
                HardwareStatusColor = "#daa520";
            }
            else if (effective == "CPU")
            {
                RuntimeLog.Fail("Hardware", "No supported hardware encoder detected; CPU fallback active.");
                HardwareStatusText = "HW: CPU Only";
                HardwareStatusColor = "#808080";
            }
            else
            {
                HardwareStatusText = $"HW: {effective} (Ready)";
                HardwareStatusColor = "#00783C";
            }
        }
        catch
        {
            _hardwareMode = HardwareScanner.ScanFailed;
            HardwareStatusText = "HW: Detecting…";
            HardwareStatusColor = "#daa520";
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public static bool CheckRdpGpuBlocked()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        bool isRdp = false;
        try { isRdp = GetSystemMetrics(0x1000) != 0; } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        if (!isRdp)
        {
            var clientName = Environment.GetEnvironmentVariable("CLIENTNAME");
            if (!string.IsNullOrEmpty(clientName) && !clientName.Equals("Console", StringComparison.OrdinalIgnoreCase))
                isRdp = true;
        }

        if (!isRdp) return false;

        try
        {
            var val = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services", "fEnableWddmDriver", null);
            if (val == null || (val is int i && i == 0))
            {
                return true;
            }
        }
        catch { return true; }
        return false;
    }

    public async Task<bool> AutoFixRdpGpuPolicyAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"$path = 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows NT\\Terminal Services'; if (!(Test-Path $path)) { New-Item -Path $path -Force }; Set-ItemProperty -Path $path -Name 'fEnableWddmDriver' -Value 1 -Type DWord; Set-ItemProperty -Path $path -Name 'fEnableAVC444ModeOnHWEncoder' -Value 1 -Type DWord;\"",
            UseShellExecute = true,
            Verb = "runas"
        };

        using var proc = Process.Start(psi);
        if (proc != null)
        {
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
        return false;
    }

    public CancellationToken PrepareExportCancellation()
    {
        _exportCts?.Cancel();
        _exportCts?.Dispose();
        _exportCts = new CancellationTokenSource();
        IsExporting = true;
        ProgressPercentage = 0;
        return _exportCts.Token;
    }

    public void CancelExport()
    {
        if (_exportCts != null && !_exportCts.IsCancellationRequested)
        {
            try { _exportCts.Cancel(); } catch (ObjectDisposedException) { }
        }
        IsExporting = false;
    }

    public void CompleteExport()
    {
        IsExporting = false;
    }
}
