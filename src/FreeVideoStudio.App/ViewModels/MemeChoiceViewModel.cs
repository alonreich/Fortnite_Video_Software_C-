// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Globalization;
using System.Windows.Input;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.ViewModels;

/// <summary>
/// MEMEMODE_01 — the meme popup's "how should it play?" half (UI-MEMESELECT). Bound, not looked up
/// (MVVM_01). Two cards — FULL SCREEN (interrupt the gameplay, the video becomes +X.X s longer) and
/// CORNER OVERLAY (keep the gameplay running, the length stays the same) — and, for the corner, the
/// four corner buttons, Small / Medium / Large and "Play meme sound".
/// Defaults: Full Screen, Bottom Right, Medium, sound on.
/// </summary>
public sealed class MemeChoiceViewModel : ViewModelBase
{
    private MemePresentationMode _mode = MemePresentationMode.InlineFullScreen;
    private MemeOverlayCorner _corner = MemeOverlayCorner.BottomRight;
    private MemeOverlaySize _size = MemeOverlaySize.Medium;
    private bool _playSound = true;
    private double? _durationSec;
    private bool _hasMeme;

    public MemeChoiceViewModel()
    {
        FullScreenCommand = new RelayCommand(() => Mode = MemePresentationMode.InlineFullScreen);
        CornerCommand = new RelayCommand(() => Mode = MemePresentationMode.CornerOverlay);
        TopLeftCommand = new RelayCommand(() => Corner = MemeOverlayCorner.TopLeft);
        TopRightCommand = new RelayCommand(() => Corner = MemeOverlayCorner.TopRight);
        BottomLeftCommand = new RelayCommand(() => Corner = MemeOverlayCorner.BottomLeft);
        BottomRightCommand = new RelayCommand(() => Corner = MemeOverlayCorner.BottomRight);
        SmallCommand = new RelayCommand(() => Size = MemeOverlaySize.Small);
        MediumCommand = new RelayCommand(() => Size = MemeOverlaySize.Medium);
        LargeCommand = new RelayCommand(() => Size = MemeOverlaySize.Large);
    }

    /// <summary>Starts from an existing meme (editing its properties).</summary>
    public void LoadFrom(MemePlacement existing)
    {
        Mode = existing.Mode;
        Corner = existing.Corner;
        Size = existing.Size;
        PlaySound = existing.PlaySound;
        DurationSec = existing.DurationSec;
    }

    public MemePresentationMode Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsFullScreen));
            OnPropertyChanged(nameof(IsCorner));
        }
    }

    public MemeOverlayCorner Corner
    {
        get => _corner;
        set
        {
            if (!SetProperty(ref _corner, value)) return;
            OnPropertyChanged(nameof(IsTopLeft));
            OnPropertyChanged(nameof(IsTopRight));
            OnPropertyChanged(nameof(IsBottomLeft));
            OnPropertyChanged(nameof(IsBottomRight));
        }
    }

    public MemeOverlaySize Size
    {
        get => _size;
        set
        {
            if (!SetProperty(ref _size, value)) return;
            OnPropertyChanged(nameof(IsSmall));
            OnPropertyChanged(nameof(IsMedium));
            OnPropertyChanged(nameof(IsLarge));
        }
    }

    public bool PlaySound { get => _playSound; set => SetProperty(ref _playSound, value); }

    /// <summary>True once a meme is chosen: the cards are shown only then.</summary>
    public bool HasMeme { get => _hasMeme; set => SetProperty(ref _hasMeme, value); }

    /// <summary>The chosen meme's length (null while it is being read).</summary>
    public double? DurationSec
    {
        get => _durationSec;
        set { if (SetProperty(ref _durationSec, value)) OnPropertyChanged(nameof(FullScreenLengthText)); }
    }

    public bool IsFullScreen => _mode == MemePresentationMode.InlineFullScreen;
    public bool IsCorner => _mode == MemePresentationMode.CornerOverlay;
    public bool IsTopLeft => _corner == MemeOverlayCorner.TopLeft;
    public bool IsTopRight => _corner == MemeOverlayCorner.TopRight;
    public bool IsBottomLeft => _corner == MemeOverlayCorner.BottomLeft;
    public bool IsBottomRight => _corner == MemeOverlayCorner.BottomRight;
    public bool IsSmall => _size == MemeOverlaySize.Small;
    public bool IsMedium => _size == MemeOverlaySize.Medium;
    public bool IsLarge => _size == MemeOverlaySize.Large;

    /// <summary>"Video becomes +4.0 sec longer".</summary>
    public string FullScreenLengthText => FormatLonger(_durationSec);

    public static string FormatLonger(double? seconds) => seconds is double d && d > 0
        ? $"Video becomes +{d.ToString("0.0", CultureInfo.InvariantCulture)} sec longer"
        : "Video becomes longer by the meme's length";

    public ICommand FullScreenCommand { get; }
    public ICommand CornerCommand { get; }
    public ICommand TopLeftCommand { get; }
    public ICommand TopRightCommand { get; }
    public ICommand BottomLeftCommand { get; }
    public ICommand BottomRightCommand { get; }
    public ICommand SmallCommand { get; }
    public ICommand MediumCommand { get; }
    public ICommand LargeCommand { get; }

    /// <summary>Applies the choice to a placement (all four properties).</summary>
    public MemePlacement ApplyTo(MemePlacement placement) =>
        placement with { Mode = _mode, Corner = _corner, Size = _size, PlaySound = _playSound };
}
