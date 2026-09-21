// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
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
    private int _qualitySliderValue = QualityLadder.DefaultIndex;   // QUALITY_01
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
    private string _estimatedFileSizeText = "—";
    private string _estimatedFileSizeDescription = "Estimated size of the finished video, including sound.";

    public string EstimatedFileSizeText
    {
        get => _estimatedFileSizeText;
        set => SetProperty(ref _estimatedFileSizeText, value);
    }

    public string EstimatedFileSizeDescription
    {
        get => _estimatedFileSizeDescription;
        set => SetProperty(ref _estimatedFileSizeDescription, value);
    }

    public int QualitySliderValue
    {
        get => _qualitySliderValue;
        // QUALITY_01 — the dial is a TIER index now, not a 0-20 megabyte step. An index restored
        // from an older session is clamped rather than rejected; the top stop still means
        // "no size limit", so the one setting anybody deliberately chose survives the change.
        set => SetProperty(ref _qualitySliderValue, QualityLadder.ClampIndex(value));
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
        catch (System.Exception swallowed)
        {
            _hardwareMode = HardwareScanner.ScanFailed;
            HardwareStatusText = "HW: Detecting…";
            HardwareStatusColor = "#daa520";
            global::FortniteVideoSoftware.App.RuntimeLog.Swallowed(swallowed);   // FAULTTIER_02 — no failure is silent.
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
        catch (System.Exception swallowed2)
        {
            global::FortniteVideoSoftware.App.RuntimeLog.Swallowed(swallowed2);   // FAULTTIER_02 — no failure is silent.
            return true;
        }
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
}
