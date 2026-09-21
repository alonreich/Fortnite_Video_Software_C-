// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using FortniteVideoSoftware.App.Infrastructure;
using FortniteVideoSoftware.App.Models;
using FortniteVideoSoftware.App.Services;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly ApplicationPaths _paths;
    private readonly ProjectRecoveryService _recoveryService;

    private string? _loadedVideoPath;
    private string _loadedVideoName = string.Empty;
    private bool _hasLoadedVideo;

    private double _mainVolume = 100;
    private bool _isMuted;
    private double _previousVolume = 100;

    private bool _isPortraitMode = true;
    private string _overlayText = string.Empty;
    private bool _isTeammates;
    private bool _isSpectating = true; // SPECTATINGDEFAULT_01 — new projects start with the eye enabled.
    private bool _isEnableFade = true;
    private bool _isAddMeme;
    private MemeItem? _selectedMemeItem;
    private int _memePlacementIndex;
    private bool _keepMusicDuringMeme;

    private bool _isInGameOverlaysVisible = true;

    private bool _isPreviewDetached;
    private bool _isSoftwareFallbackActive;
    private bool _isPlaying;
    private bool _isGranularSpeedActive;
    private bool _isMusicActive;
    private bool _exportedCleanSinceLastEdit;

    private VoiceOverWindow.VoiceOverResult? _voiceOverResult;
    private MusicWizardResult? _musicWizardResult;

    public TimelineViewModel Timeline { get; }
    public ExportViewModel Export { get; }

    public ObservableCollection<MemeItem> MemeItems { get; } = new();

    public ICommand ToggleMuteCommand { get; }

    public event Action? OnRecoveryStateDirty;
    public event Action? OnPortraitModeToggled;

    public MainViewModel(ApplicationPaths? paths = null, ProjectRecoveryService? recoveryService = null)
    {
        _paths = paths ?? ApplicationPaths.CreateDefault();
        _recoveryService = recoveryService ?? new ProjectRecoveryService(_paths);

        Timeline = new TimelineViewModel();
        Export = new ExportViewModel();

        Timeline.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Timeline.BaseSpeed) ||
                e.PropertyName == nameof(Timeline.TrimStartMs) ||
                e.PropertyName == nameof(Timeline.TrimEndMs) ||
                e.PropertyName == nameof(Timeline.IsTrimStartSet) ||
                e.PropertyName == nameof(Timeline.IsTrimEndSet) ||
                e.PropertyName == nameof(Timeline.LoadedVideoDurationMs) ||
                e.PropertyName == nameof(Timeline.FreezeTimeMs) ||
                e.PropertyName == nameof(Timeline.FreezeDurationS))
            {
                SizeEstimateRequested?.Invoke();
            }
        };

        Export.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Export.QualitySliderValue))
            {
                SizeEstimateRequested?.Invoke();
                OnPropertyChanged(nameof(QualitySliderValue));
            }
        };

        ToggleMuteCommand = new RelayCommand(ToggleMute);
    }

    public event Action? SizeEstimateRequested;

    public string? LoadedVideoPath
    {
        get => _loadedVideoPath;
        set
        {
            if (SetProperty(ref _loadedVideoPath, value))
            {
                HasLoadedVideo = !string.IsNullOrWhiteSpace(value) && File.Exists(value);
                LoadedVideoName = HasLoadedVideo ? Path.GetFileName(value!) : string.Empty;
            }
        }
    }

    public string LoadedVideoName
    {
        get => _loadedVideoName;
        set => SetProperty(ref _loadedVideoName, value);
    }

    public bool HasLoadedVideo
    {
        get => _hasLoadedVideo;
        set => SetProperty(ref _hasLoadedVideo, value);
    }

    public double MainVolume
    {
        get => _mainVolume;
        set
        {
            double clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _mainVolume, clamped))
            {
                IsMuted = clamped <= 0;
            }
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set => SetProperty(ref _isMuted, value);
    }

    public double PreviousVolume
    {
        get => _previousVolume;
        set => SetProperty(ref _previousVolume, value);
    }

    public bool IsPortraitMode
    {
        get => _isPortraitMode;
        set
        {
            if (SetProperty(ref _isPortraitMode, value))
            {
                SizeEstimateRequested?.Invoke();
                OnPortraitModeToggled?.Invoke();
                NotifyStateDirty();
            }
        }
    }

    public string OverlayText
    {
        get => _overlayText;
        set
        {
            if (SetProperty(ref _overlayText, value))
            {
                NotifyStateDirty();
            }
        }
    }

    public bool IsTeammates
    {
        get => _isTeammates;
        set
        {
            if (SetProperty(ref _isTeammates, value))
                NotifyStateDirty();
        }
    }

    public bool IsSpectating
    {
        get => _isSpectating;
        set
        {
            if (SetProperty(ref _isSpectating, value))
                NotifyStateDirty();
        }
    }

    public bool IsEnableFade
    {
        get => _isEnableFade;
        set
        {
            if (SetProperty(ref _isEnableFade, value))
                NotifyStateDirty();
        }
    }

    public bool IsAddMeme
    {
        get => _isAddMeme;
        set
        {
            if (SetProperty(ref _isAddMeme, value))
                NotifyStateDirty();
        }
    }

    public MemeItem? SelectedMemeItem
    {
        get => _selectedMemeItem;
        set
        {
            if (SetProperty(ref _selectedMemeItem, value))
                NotifyStateDirty();
        }
    }

    public int MemePlacementIndex
    {
        get => _memePlacementIndex;
        set
        {
            if (SetProperty(ref _memePlacementIndex, value))
                NotifyStateDirty();
        }
    }

    public bool KeepMusicDuringMeme
    {
        get => _keepMusicDuringMeme;
        set
        {
            if (SetProperty(ref _keepMusicDuringMeme, value))
                NotifyStateDirty();
        }
    }

    public bool IsInGameOverlaysVisible
    {
        get => _isInGameOverlaysVisible;
        set => SetProperty(ref _isInGameOverlaysVisible, value);
    }

    public bool IsPreviewDetached
    {
        get => _isPreviewDetached;
        set => SetProperty(ref _isPreviewDetached, value);
    }

    public bool IsSoftwareFallbackActive
    {
        get => _isSoftwareFallbackActive;
        set => SetProperty(ref _isSoftwareFallbackActive, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set => SetProperty(ref _isPlaying, value);
    }

    public bool IsGranularSpeedActive
    {
        get => _isGranularSpeedActive;
        set
        {
            if (SetProperty(ref _isGranularSpeedActive, value))
                NotifyStateDirty();
        }
    }

    public bool IsMusicActive
    {
        get => _isMusicActive;
        set
        {
            if (SetProperty(ref _isMusicActive, value))
                NotifyStateDirty();
        }
    }

    public bool ExportedCleanSinceLastEdit
    {
        get => _exportedCleanSinceLastEdit;
        set => SetProperty(ref _exportedCleanSinceLastEdit, value);
    }

    public VoiceOverWindow.VoiceOverResult? VoiceOverResult
    {
        get => _voiceOverResult;
        set
        {
            if (SetProperty(ref _voiceOverResult, value))
                NotifyStateDirty();
        }
    }

    public MusicWizardResult? MusicWizardResult
    {
        get => _musicWizardResult;
        set
        {
            if (SetProperty(ref _musicWizardResult, value))
                NotifyStateDirty();
        }
    }

    public int QualitySliderValue
    {
        get => Export.QualitySliderValue;
        set
        {
            Export.QualitySliderValue = value;
            OnPropertyChanged();
        }
    }

    public int MainSpeedSliderValue
    {
        get => Timeline.MainSpeedSliderValue;
        set
        {
            Timeline.MainSpeedSliderValue = value;
            OnPropertyChanged();
        }
    }

    public void ToggleMute()
    {
        if (MainVolume > 0)
        {
            PreviousVolume = MainVolume;
            MainVolume = 0;
        }
        else
        {
            MainVolume = PreviousVolume > 0 ? PreviousVolume : 100;
        }
    }

    public void ApplyDefaults()
    {
        var d = SettingsManager.Instance.Defaults;
        Timeline.ApplyMainSpeedPreset(d.DefaultSpeed);
        Export.QualitySliderValue = d.QualityIndex;
        IsPortraitMode = d.PortraitMode;
        IsTeammates = d.ShowTeammates;
        IsSpectating = true;
        IsEnableFade = d.EnableFade;

        string stateFile = _paths.SessionStateFile;
        if (File.Exists(stateFile))
        {
            try
            {
                var state = AtomicJsonFile.ReadObject(stateFile);
                if (state != null && state.ContainsKey("MainVolume"))
                {
                    MainVolume = state["MainVolume"]?.GetValue<double>() ?? 100.0;
                }
            }
            catch (System.Exception swallowed)
            {
                MainVolume = 100.0;
                global::FortniteVideoSoftware.App.RuntimeLog.Swallowed(swallowed);   // FAULTTIER_02 — no failure is silent.
            }
        }

        ApplyMaskProfile(SettingsManager.Instance.ActiveMaskOverlay);
    }

    public void ApplyMaskProfile(string profileName)
    {
        bool isNoMask = MaskOverlayManager.IsNoMask(profileName);
        IsInGameOverlaysVisible = !isNoMask;
        if (isNoMask)
        {
            IsTeammates = false;
            IsSpectating = false;
        }
    }

    public void ResetEditingStateForNewVideo()
    {
        Timeline.Reset();
        MusicWizardResult = null;
        VoiceOverResult = null;
        IsAddMeme = false;
        SelectedMemeItem = null;
        IsGranularSpeedActive = false;
        IsMusicActive = false;
    }

    public void ResetProjectStateToUpload()
    {
        LoadedVideoPath = null;
        ResetEditingStateForNewVideo();
        ExportedCleanSinceLastEdit = false;
        _recoveryService.ClearState();
    }

    public bool HasUnsavedWork()
    {
        return _recoveryService.HasUnsavedWork(this, Timeline, Export);
    }

    public void SaveRecoveryState(bool sync = false, bool isUserEdit = true)
    {
        if (isUserEdit && ExportedCleanSinceLastEdit)
        {
            ExportedCleanSinceLastEdit = false;
            RuntimeLog.Info("RECOVERY", "Edit made after export - project is dirty again.");
        }

        if (!HasUnsavedWork())
        {
            _recoveryService.ClearState();
            return;
        }

        var state = _recoveryService.SerializeState(this, Timeline, Export);
        _recoveryService.SaveState(state, sync);
    }

    private void NotifyStateDirty()
    {
        OnRecoveryStateDirty?.Invoke();
    }
}
