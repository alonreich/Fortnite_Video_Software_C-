// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Windows.Input;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.ViewModels;

/// <summary>
/// DUCKSTRENGTH_01 — Settings › Sound &amp; Music › "Music vs. game sound": the two switches (volume
/// ducking, EQ carving) and their two strength handles (0-100, 50 = the tuned out-of-the-box value).
/// Bound, not looked up (MVVM_01). The handles move in whole steps and each step changes the effect
/// by the same small proportion (SidechainCompressNode.RatioFor), so nothing ever jumps. Written back
/// to settings by SettingsWindow's Save, like every other pending value there.
/// </summary>
public sealed class MixProtectionViewModel : ViewModelBase
{
    private bool _duckingEnabled;
    private bool _carvingEnabled;
    private int _duckingStrength;
    private int _carvingStrength;

    public MixProtectionViewModel(bool duckingEnabled, bool carvingEnabled, int duckingStrength, int carvingStrength)
    {
        _duckingEnabled = duckingEnabled;
        _carvingEnabled = carvingEnabled;
        _duckingStrength = Clamp(duckingStrength);
        _carvingStrength = Clamp(carvingStrength);
        ResetCommand = new RelayCommand(Reset);
    }

    public bool DuckingEnabled { get => _duckingEnabled; set => SetProperty(ref _duckingEnabled, value); }
    public bool CarvingEnabled { get => _carvingEnabled; set => SetProperty(ref _carvingEnabled, value); }

    /// <summary>The DUCK handle (double for the Slider binding; stored as a whole number).</summary>
    public double DuckingStrength
    {
        get => _duckingStrength;
        set { if (SetProperty(ref _duckingStrength, Clamp(value))) OnPropertyChanged(nameof(DuckingLabel)); }
    }

    /// <summary>The CARVE handle.</summary>
    public double CarvingStrength
    {
        get => _carvingStrength;
        set { if (SetProperty(ref _carvingStrength, Clamp(value))) OnPropertyChanged(nameof(CarvingLabel)); }
    }

    public int DuckingStrengthValue => _duckingStrength;
    public int CarvingStrengthValue => _carvingStrength;

    public string DuckingLabel => Label(_duckingStrength);
    public string CarvingLabel => Label(_carvingStrength);

    /// <summary>"Back to normal" — both handles to the middle.</summary>
    public ICommand ResetCommand { get; }

    private void Reset()
    {
        DuckingStrength = SidechainCompressNode.DefaultStrength;
        CarvingStrength = SidechainCompressNode.DefaultStrength;
    }

    /// <summary>"Normal" in the middle, "+3" / "-5" either side.</summary>
    public static string Label(int strength)
    {
        int d = strength - SidechainCompressNode.DefaultStrength;
        return d == 0 ? "Normal" : d > 0 ? $"+{d}" : $"{d}";
    }

    private static int Clamp(double v) =>
        double.IsFinite(v) ? (int)Math.Round(Math.Clamp(v, 0, 100)) : SidechainCompressNode.DefaultStrength;
}
